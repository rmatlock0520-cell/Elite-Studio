using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EliteStudio.Flight;
using Microsoft.Win32;

namespace EliteStudio.Gimbalyn;

/// <summary>
/// The Gimbalyn process: Studio's own program started as EliteStudio.exe --gimbalyn [--data-dir D] [--with PID] [--start].
/// One copy runs per Windows session. It feeds vJoy on its own thread, answers a control pipe only this Windows user can
/// open (and only with the token it writes to its data folder), keeps a log, and centres vJoy on every way out: quit,
/// closing, a fatal problem, Windows signing out or going to sleep, and the process named by --with (Studio) ending. Should
/// vJoy's library crash it, the process ends at once (no "stopped working" box keeps it alive holding vJoy), the driver lets
/// go of vJoy, and Studio centres it with --gimbalyn-centre. It shows no window.
/// </summary>
public static class Host
{
    /// <summary>The options that start Gimbalyn, and that centre vJoy once and leave (Studio's way out if Gimbalyn ended mid-turn).</summary>
    public const string Option = "gimbalyn", CentreOption = "gimbalyn-centre";
    const string OneCopy = @"Local\Gimbalyn";
    /// <summary>The file in the data folder that tells Studio how to reach a running Gimbalyn (pipe, token, process).</summary>
    public const string InfoFile = "gimbalyn.json", ProfileFile = "gimbalyn-profile.json";
    /// <summary>The exit code of a second copy, which leaves because one already runs.</summary>
    public const int AlreadyRunning = 3;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static int Run(IReadOnlyDictionary<string, string> options, string dataDir)
    {
        VJoyProbe.QuietCrashes();   // before vJoy's library is ever loaded
        var log = new GimbalynLog(Path.Combine(dataDir, "logs", "gimbalyn.log"));
        using var oneCopy = new Mutex(false, OneCopy);
        bool mine;
        try
        {
            mine = oneCopy.WaitOne(TimeSpan.FromSeconds(3));   // a Gimbalyn just told to quit may still be centring
        }
        catch (AbandonedMutexException)
        {
            mine = true;   // the one before ended without letting go: this one carries on
        }
        if (!mine)
        {
            log.Write($"another Gimbalyn runs ({(ReadInfo(dataDir) is { } info ? "process " + info.Pid : "process unknown")}): this one leaves");
            return AlreadyRunning;
        }
        Engine? engine = null;
        try
        {
            return Serve(options, dataDir, log, e => engine = e);
        }
        catch (Exception problem)
        {
            log.Write("Gimbalyn stopped by a problem: " + problem);
            engine?.TryStop("Gimbalyn hit a problem");
            return 1;
        }
        finally
        {
            oneCopy.ReleaseMutex();
        }
    }

    static int Serve(IReadOnlyDictionary<string, string> options, string dataDir, GimbalynLog log, Action<Engine> made)
    {
        log.Write($"Gimbalyn started (Elite Studio {App.Program.Version}, process {Environment.ProcessId})");
        string profileFile = Path.Combine(dataDir, ProfileFile);
        var recorder = new Recorder();
        Engine engine = null!;   // set on the next lines; vJoy's folder is asked for only once feeding starts
        var feeder = new VJoyFeeder(() => engine.Profile.VJoyFolder);
        engine = new Engine(new WinmmSticks(), feeder, KeyNames.Held, Front, recorder, log.Write);
        made(engine);
        engine.Use(LoadProfile(profileFile, log));

        using var quit = new ManualResetEventSlim();
        // Every way out centres vJoy first. A fatal problem on any thread: said in the log, vJoy centred, then the process
        // ends at once (Studio's own handler is not in use in this process; see Program.Start).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => engine.TryStop("Gimbalyn is closing");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            log.Write("Gimbalyn stopped by a problem: " + e.ExceptionObject);
            engine.TryStop("Gimbalyn hit a problem");
            Environment.Exit(1);
        };
        // Windows' own events, subscribed from a thread of the pool so that Windows' hidden window for them gets a thread of
        // its own (this thread waits below and pumps no messages).
        Task.Run(() =>
        {
            SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Suspend) engine.Sleep(); else if (e.Mode == PowerModes.Resume) engine.Wake(); };
            SystemEvents.SessionEnding += (_, _) => engine.Stop("Windows is signing out");
        }).Wait();

        var feeding = new Thread(() =>
        {
            using var pacer = new Pacer(4);
            var clock = Stopwatch.StartNew();
            while (!quit.IsSet)
            {
                pacer.Wait();
                try
                {
                    engine.Tick(clock.ElapsedMilliseconds);
                }
                catch (Exception problem)
                {
                    log.Write("feeding stopped by a problem: " + problem);
                    engine.TryStop("Gimbalyn hit a problem");
                }
            }
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "Gimbalyn feeding" };
        feeding.Start();

        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        string pipe = PipeName();
        using var server = new ControlServer(engine, recorder, token, pipe, profileFile, Path.Combine(dataDir, "logs"), log, quit);
        server.Start();
        string infoFile = Path.Combine(dataDir, InfoFile);
        using (Process me = Process.GetCurrentProcess())
            WriteWhole(infoFile, JsonSerializer.Serialize(new GimbalynInfo(pipe, token, Environment.ProcessId, App.Program.Version, me.StartTime.ToUniversalTime().Ticks), Json));

        if (options.TryGetValue("with", out string? with) && int.TryParse(with, NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) WatchParent(pid, quit, log);
        if (options.ContainsKey("start")) engine.Start();

        quit.Wait();
        engine.Stop("Gimbalyn was asked to quit");
        server.Dispose();
        feeding.Join(500);
        try
        {
            if (ReadInfo(dataDir)?.Pid == Environment.ProcessId) File.Delete(infoFile);   // only our own
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException) { }
        log.Write("Gimbalyn ended");
        return 0;
    }

    /// <summary>Centres once what Gimbalyn fed (the profile's vJoy device and routed axes) and leaves (EliteStudio.exe --gimbalyn-centre).</summary>
    public static int Centre(string dataDir)
    {
        VJoyProbe.QuietCrashes();
        var log = new GimbalynLog(Path.Combine(dataDir, "logs", "gimbalyn.log"));
        try
        {
            GimbalynProfile profile = LoadProfile(Path.Combine(dataDir, ProfileFile), log);
            string[] axes = [.. profile.Routes.Select(r => r.Target)];
            log.Write("centre once: " + VJoyFeeder.CentreOnce(new VJoyFeeder(() => profile.VJoyFolder), profile.Device, axes));
            return 0;
        }
        catch (Exception problem)
        {
            log.Write("centre once failed: " + problem);
            return 1;
        }
    }

    static GimbalynProfile LoadProfile(string file, GimbalynLog log)
    {
        try
        {
            return File.Exists(file) ? (JsonSerializer.Deserialize<GimbalynProfile>(File.ReadAllText(file), Json) ?? new GimbalynProfile()).Cleaned() : new GimbalynProfile();
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Write($"the profile {file} could not be read, so none is in use: {problem.Message}");
            return new GimbalynProfile();
        }
    }

    /// <summary>The data folder's gimbalyn.json, whatever process wrote it; null when there is none or it cannot be read.</summary>
    internal static GimbalynInfo? ReadInfo(string dataDir)
    {
        try
        {
            return JsonSerializer.Deserialize<GimbalynInfo>(File.ReadAllText(Path.Combine(dataDir, InfoFile)), Json);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A file written whole or not at all (a reader never sees half of it): written beside it, then put in its place.</summary>
    internal static void WriteWhole(string file, string text)
    {
        string beside = file + ".new";
        File.WriteAllText(beside, text);
        File.Move(beside, file, overwrite: true);
    }

    /// <summary>Quits when the process that started Gimbalyn (Studio) ends, or straight away when it is already gone.</summary>
    static void WatchParent(int pid, ManualResetEventSlim quit, GimbalynLog log)
    {
        try
        {
            Process parent = Process.GetProcessById(pid);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => { log.Write($"process {pid} ended: Gimbalyn ends too"); quit.Set(); };
            if (parent.HasExited) quit.Set();
        }
        catch (Exception problem) when (problem is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.Write($"process {pid} is not there: Gimbalyn ends");
            quit.Set();
        }
    }

    /// <summary>This Windows user's pipe name: pipes are seen by every user, so each one's has its own.</summary>
    internal static string PipeName() => "Gimbalyn-" + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

    /// <summary>The program in front, by its process name (asked only when the mode key is pressed).</summary>
    static string? Front()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out uint pid) == 0) return null;
        try
        {
            using Process front = Process.GetProcessById((int)pid);
            return front.ProcessName;
        }
        catch (Exception problem) when (problem is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}

/// <summary>How to reach a running Gimbalyn (its data folder's gimbalyn.json): pipe, token, process and when that process started (UTC ticks).</summary>
public sealed record GimbalynInfo(string Pipe, string Token, int Pid, string Version, long Started = 0);

/// <summary>
/// Gimbalyn's control pipe: one request at a time, each one line of JSON with the token and a command, answered with one
/// line of JSON. Only this Windows user can open the pipe (Windows checks it), and only a caller that read the token from
/// the data folder is answered. Commands: status, start, stop, centre, mode {mode}, profile {profile}, record, quit.
/// </summary>
public sealed class ControlServer : IDisposable
{
    readonly Engine engine;
    readonly Recorder recorder;
    readonly byte[] token;
    readonly string pipe, profileFile, logs;
    readonly GimbalynLog log;
    readonly ManualResetEventSlim quit;
    readonly CancellationTokenSource stop = new();
    Task? serving;
    int refused, troubles;
    volatile bool quitAsked;   // quit is said yes to first, and done once the answer is on its way

    /// <summary>A caller asked Gimbalyn to quit (it does, once the answer is written).</summary>
    internal bool QuitAsked => quitAsked;

    public ControlServer(Engine engine, Recorder recorder, string token, string pipe, string profileFile, string logs, GimbalynLog log, ManualResetEventSlim quit)
    {
        (this.engine, this.recorder, this.pipe, this.profileFile, this.logs, this.log, this.quit) = (engine, recorder, pipe, profileFile, logs, log, quit);
        this.token = Encoding.UTF8.GetBytes(token);
    }

    public void Start() => serving = Task.Run(Serve);

    async Task Serve()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                // CurrentUserOnly: Windows lets only this user open it; one instance at a time, so no other program can squat on the name.
                await using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stop.Token);
                using var read = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var both = CancellationTokenSource.CreateLinkedTokenSource(read.Token, stop.Token);
                string? line = await ReadLine(server, both.Token);
                string answer = line is null ? Error("No request came.") : Handle(line);
                byte[] bytes = Encoding.UTF8.GetBytes(answer + "\n");
                await server.WriteAsync(bytes, both.Token);
                await server.FlushAsync(both.Token);
                if (quitAsked) { server.WaitForPipeDrain(); quit.Set(); }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception problem)
            {
                // Whatever went wrong with one caller (gone, too slow, the pipe not to be made just now, anything else), the
                // pipe keeps serving: only Gimbalyn's end stops it. A breath first, so a pipe that cannot be made does not spin.
                if (troubles++ < 10 && problem is not (IOException or OperationCanceledException)) log.Write("the control pipe had a problem: " + problem.Message);
                try { await Task.Delay(200, stop.Token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>One line, at most 256 KB (a profile is far smaller).</summary>
    static async Task<string?> ReadLine(Stream stream, CancellationToken cancel)
    {
        var bytes = new List<byte>();
        var one = new byte[4096];
        while (bytes.Count < 256 * 1024)
        {
            int n = await stream.ReadAsync(one, cancel);
            if (n == 0) break;
            int end = Array.IndexOf(one, (byte)'\n', 0, n);
            bytes.AddRange(end >= 0 ? one.Take(end) : one.Take(n));
            if (end >= 0) return Encoding.UTF8.GetString(bytes.ToArray());
        }
        return bytes.Count > 0 ? Encoding.UTF8.GetString(bytes.ToArray()) : null;
    }

    internal string Handle(string line)
    {
        try
        {
            return Answer(line);
        }
        catch (Exception problem) when (problem is InvalidOperationException or FormatException or JsonException or ArgumentException or NotSupportedException)
        {
            return Error("That request could not be read: " + problem.Message);   // a field of the wrong kind, a name twice...
        }
    }

    string Answer(string line)
    {
        JsonObject? request;
        try
        {
            request = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return Error("That is not a request.");
        }
        if (request?["token"]?.GetValue<string>() is not { } given || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), token))
        {
            if (refused++ < 5) log.Write("a request without the right token was turned away");
            return Error("Not allowed.");
        }
        string command = request["command"]?.GetValue<string>() ?? "";
        switch (command)
        {
            case "status": break;
            case "start": engine.Start(); break;
            case "stop": engine.Stop("asked to stop over the control pipe"); break;
            case "centre": engine.CentreNow("asked over the control pipe"); break;
            case "mode":
                if (!engine.SetMode(request["mode"]?.GetValue<string>() ?? "")) return Error("There is no mode of that name.");
                break;
            case "profile":
                GimbalynProfile? profile;
                try
                {
                    profile = request["profile"]?.Deserialize<GimbalynProfile>(Host.Json)?.Cleaned();
                }
                catch (JsonException problem)
                {
                    return Error("That profile could not be read: " + problem.Message);
                }
                if (profile is null) return Error("No profile came.");
                try
                {
                    // Saved first, then used: the one-off centre reads this file, so it must never name another device than the one fed.
                    Host.WriteWhole(profileFile, JsonSerializer.Serialize(profile, Host.Json));
                }
                catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
                {
                    log.Write("the profile could not be saved, so it is not in use: " + problem.Message);
                    return Error("The profile could not be saved, so Gimbalyn keeps the one before: " + problem.Message);
                }
                engine.Use(profile);
                break;
            case "record":
                Directory.CreateDirectory(logs);
                string file = Path.Combine(logs, $"gimbalyn-record-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv");
                File.WriteAllText(file, recorder.Csv());
                log.Write("flight recorder saved to " + file);
                return JsonSerializer.Serialize(new { ok = true, path = file }, Host.Json);
            case "quit":
                quitAsked = true;   // done by Serve once this answer has gone
                return JsonSerializer.Serialize(new { ok = true }, Host.Json);
            default:
                return Error("Gimbalyn does not know that command.");
        }
        return JsonSerializer.Serialize(new { ok = true, status = engine.Status(), profile = engine.Profile }, Host.Json);
    }

    static string Error(string why) => JsonSerializer.Serialize(new { ok = false, error = why }, Host.Json);

    int disposed;

    /// <summary>Stops serving; once only (Gimbalyn's end calls it, and so does the "using" around it).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;
        stop.Cancel();
        try { serving?.Wait(1000); } catch (AggregateException) { }
        stop.Dispose();
    }
}

/// <summary>Studio's side of the control pipe.</summary>
public static class GimbalynClient
{
    /// <summary>
    /// Whether the Gimbalyn written down in the data folder runs now: its process is there and started when it said. False
    /// for a file left by one that ended without tidying up (Studio then centres vJoy with --gimbalyn-centre).
    /// </summary>
    public static bool Running(string dataDir)
    {
        if (Host.ReadInfo(dataDir) is not { } info) return false;
        try
        {
            using Process process = Process.GetProcessById(info.Pid);
            return !process.HasExited && (info.Started == 0 || process.StartTime.ToUniversalTime().Ticks == info.Started);
        }
        catch (Exception problem) when (problem is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>The command line that starts Gimbalyn for the Studio of that process: Gimbalyn ends when Studio does (see <see cref="Host"/>).</summary>
    public static string[] LaunchArguments(int studioProcess) => ["--" + Host.Option, "--with", studioProcess.ToString(CultureInfo.InvariantCulture)];

    /// <summary>
    /// Starts Gimbalyn: Studio's own program again, without a window, as a process of its own. It uses the data folder this Studio
    /// uses (a practice copy, which has one of its own, never starts it). False when it could not be started.
    /// </summary>
    public static bool Launch() => Start(LaunchArguments(Environment.ProcessId));

    /// <summary>
    /// Centres vJoy once, with Studio's own program again (EliteStudio.exe --gimbalyn-centre), for when a Gimbalyn that was feeding
    /// ended: vJoy keeps the axes where it left them. It leaves a device another program holds alone. False when it could not be started.
    /// </summary>
    public static bool CentreOnce() => Start(["--" + Host.CentreOption]);

    static bool Start(IEnumerable<string> arguments)
    {
        if (Environment.ProcessPath is not { } program) return false;
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using Process? process = Process.Start(start);
            return process is not null;
        }
        catch (Exception problem) when (problem is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>One request, one answer; null when Gimbalyn does not run or did not answer in time.</summary>
    public static async Task<JsonObject?> Send(string dataDir, JsonObject request, int timeoutMs = 2000)
    {
        if (!Running(dataDir) || Host.ReadInfo(dataDir) is not { } info) return null;
        request["token"] = info.Token;
        using var limit = new CancellationTokenSource(timeoutMs);
        try
        {
            await using var client = new NamedPipeClientStream(".", info.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(limit.Token);
            await client.WriteAsync(Encoding.UTF8.GetBytes(request.ToJsonString() + "\n"), limit.Token);
            await client.FlushAsync(limit.Token);
            using var reader = new StreamReader(client, Encoding.UTF8);
            string? line = await reader.ReadLineAsync(limit.Token);
            return line is null ? null : JsonNode.Parse(line) as JsonObject;
        }
        catch (Exception problem) when (problem is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>Gimbalyn's own log (logs\gimbalyn.log beside Studio's): events only, never every reading; the file starts afresh past 1 MB.</summary>
public sealed class GimbalynLog(string file)
{
    readonly object gate = new();

    public void Write(string line)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                if (File.Exists(file) && new FileInfo(file).Length > 1_000_000) File.Move(file, file + ".old", overwrite: true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException) { }   // a log that cannot be written must not stop the stick
        }
    }
}
