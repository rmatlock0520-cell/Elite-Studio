using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using EliteStudio.Core;
using EliteStudio.UI;

namespace EliteStudio.App;

/// <summary>What the running copy leaves in running.json so a second launch (and test tools) can reach it.</summary>
internal sealed record RunningCopy(int Port, string Token, int Pid);

/// <summary>
/// Start-up. Reads the options, makes sure one copy runs per data folder, starts the game watcher and the
/// local web server, then runs the message loop until Exit.
///
/// Options: --data-dir, --web-root, --journal-dir, --bindings-dir, --game-dir (see <see cref="Paths"/>;
/// with --data-dir it is a practice copy, whose first run copies nothing in from the older tools),
/// --port N, --headless (no window or tray icon; ends on POST /api/app/quit), --smoke (opens the
/// window, writes SMOKE READY or SMOKE FAILED to the log, and leaves with exit code 0 or 1),
/// --offline (no community service is reached, for practice runs) and --render-shots FOLDER (draws
/// the overlay to PNG files and leaves; no window, no data folder, no log, works while Studio is
/// already open).
/// </summary>
public static class Program
{
    /// <summary>Studio's version as it is shown and sent everywhere: "1.7.1".</summary>
    public static readonly string Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>
    /// How Studio names itself to community services (Spansh, Ardent Insight, EDAstro), so their owners can see which
    /// program is calling. It carries no contact address: which one to show, if any, is the owner's to decide.
    /// </summary>
    /// <summary>Studio's public home: where its code and downloads are, and where the data services it asks can reach its maker.</summary>
    public const string Home = "https://github.com/rmatlock0520-cell/Elite-Studio";

    public static readonly string UserAgent = $"EliteStudio/{Version} (Elite Dangerous companion app; +{Home})";

    /// <summary>The server's first choice. A steady port matters: browsers keep each page's saved preferences per address.</summary>
    public const int PreferredPort = 47820;

    const string RunningFile = "running.json";

    [STAThread]
    public static int Main(string[] args)
    {
        Dictionary<string, string> options = ParseOptions(args);
        try
        {
            return Start(options);
        }
        catch (Exception problem)
        {
            // Never a crash box. (If the log itself could not start, the reason is lost and only the exit code tells.)
            Log.Error($"Elite Studio stopped: {problem}");
            if (options.ContainsKey("smoke")) Log.Error($"SMOKE FAILED: {problem.Message}");
            return 1;
        }
    }

    /// <summary>
    /// "--name value" pairs and bare flags such as "--headless" (stored with an empty value).
    /// Names are matched without regard to case; anything that is not an option or its value is ignored.
    /// </summary>
    internal static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            options[args[i][2..]] = hasValue ? args[++i] : "";
        }
        return options;
    }

    /// <summary>The first port to try: --port when it is a usable number, else <see cref="PreferredPort"/>.</summary>
    internal static int FirstPort(IReadOnlyDictionary<string, string> options) =>
        int.TryParse(options.GetValueOrDefault("port"), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            && port is > 0 and <= 65535 - 9
            ? port
            : PreferredPort;

    /// <summary>
    /// The name of the "already running" marker. It carries a short hash of the data folder, so a practice
    /// copy started with --data-dir never collides with the player's real one.
    /// </summary>
    internal static string MutexName(string dataDir)
    {
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir)).ToUpperInvariant();
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..8];
        return $@"Local\EliteStudio-{hash}";
    }

    static int Start(Dictionary<string, string> options)
    {
        // (Until the log is started below, what these two write goes nowhere; leaving quietly still holds.)
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error($"Fatal problem: {e.ExceptionObject}");
            Environment.Exit(1);   // leave quietly; left to Windows, a "stopped working" box could cover the game
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error($"Background problem: {e.Exception}");
            e.SetObserved();
        };

        // --render-shots <folder> draws the overlay into PNG files and leaves. It is answered before the
        // data folder and the log are set up, so it writes nothing of the player's but those pictures, and
        // before the one-copy check: it needs nothing from a Studio that is already open, and must never
        // bring that one's window forward (perhaps over the game) and then leave with 0 as if the pictures
        // were drawn.
        if (options.TryGetValue("render-shots", out string? shots)) return Overlay.OverlayWindow.RenderShots(shots);

        Paths.Init(options);
        Log.Start(Paths.DataDir);
        using var oneCopy = new Mutex(true, MutexName(Paths.DataDir), out bool first);
        if (!first)
        {
            if (options.ContainsKey("smoke"))
            {
                // A check run has to report on itself. Bringing the open Studio forward (perhaps over the
                // game) and leaving with 0 would read as a pass although nothing was checked.
                Log.Error("SMOKE FAILED: Elite Studio is already running on this data folder");
                return 1;
            }
            ShowRunningCopy();
            return 0;
        }
        string runningFile = Path.Combine(Paths.DataDir, RunningFile);
        try
        {
            return Run(options, runningFile);
        }
        finally
        {
            File.Delete(runningFile);
        }
    }

    static int Run(IReadOnlyDictionary<string, string> options, string runningFile)
    {
        bool smoke = options.ContainsKey("smoke");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // From here on, whatever is awaited on this thread comes back to it. That matters for the web
        // server, which answers every request on the thread that started it: WPF only arranges this once
        // its message loop runs, and the server starts before that. Without this line a --headless run
        // would answer requests on background threads, several at once.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error($"Unexpected problem: {e.Exception}");
            e.Handled = true;
        };
        // The tray icon is Windows Forms, which shows its own error box unless someone listens here.
        System.Windows.Forms.Application.ThreadException += (_, e) => Log.Error($"Unexpected problem: {e.Exception}");

        var settings = new Settings(Path.Combine(Paths.DataDir, "settings.json"));
        // The control presets that ship with the game are in its install folder: the practice one, else the
        // first one a launcher knows of, else the one the player picked by hand in Studio UI.
        string? GameDir() => Paths.GameDir ?? GameFolder.FindAll().FirstOrDefault()?.Dir ?? settings.Get<UiSettings>("ui").GameDir;
        using var watcher = new GameWatcher(Paths.JournalDir, Paths.BindingsDir, gameDir: GameDir);
        using var web = new WebServer(Paths.WebRoot);
        MainWindow? window = options.ContainsKey("headless") ? null : new MainWindow(smoke);
        AppApi.Map(web, watcher.State, window);
        ThemeApi.Map(web, settings, options.ContainsKey("offline") ? new Offline() : null);
        using var overlay = new Overlay.OverlayHost(web, settings, watcher.State, headless: window is null);
        // Studio Flight, for flying with Flight Assist off: the trainer's stream of the sticks, the flight overlay (shown by
        // itself while Flight Assist is off), and Joystick Gremlin's profile with an FA Off mode. A practice copy must not
        // touch the player's real Gremlin files: it uses "gremlin\profile.xml" in its own data folder, when there is one.
        string practiceProfile = Path.Combine(Paths.DataDir, "gremlin", "profile.xml");
        using var flight = new Flight.FlightApi(web, settings, watcher.State, overlay, Paths.BindingsDir, GameDir,
            gremlinProfile: Paths.Practice ? () => File.Exists(practiceProfile) ? practiceProfile : null : null);
        // The clients for the community's services are made here and closed when the app ends. Studio Nav's, for Spansh
        // (--offline: a practice run reaches neither Spansh nor EDAstro):
        using var spansh = new Nav.Spansh(options.ContainsKey("offline") ? new Offline() : null);
        Nav.NavRoutes route = Nav.NavApi.Map(web, watcher.State, settings, spansh);
        // One copy of EDAstro's sector list for the flat map and the 3D map, so a new list reaches both.
        var coverage = new Nav.ExploreCoverage(Paths.DataDir, options.ContainsKey("offline") ? new Offline() : null);
        Nav.ExploreTrip trip = Nav.ExploreApi.Map(web, watcher.State, settings, spansh, coverage);
        // The 3D map: the same Spansh client and sector list, and a folder of its own for what it keeps. Its detailed map
        // reads Spansh's list of every system from that folder when the player has downloaded it there (no internet).
        // Its "Check my targets" switch, what of a plotted route it checks, how long a recorded system is remembered and
        // Within reach's switch and range are kept in settings.json; what its checks found, in heatmap\checks.json.
        // Nav scan's switch and volume are kept there too, and the screenshots it has read in heatmap\navscan-read.json.
        string heatmap = Path.Combine(Paths.DataDir, "heatmap");
        var heatmapSurvey = new Nav.HeatmapSurvey(spansh, heatmap);
        var fineMap = new Nav.FineMap(heatmap);
        var routeCheck = new Nav.HeatmapFinds(watcher.State, heatmapSurvey, fineMap, settings);
        // Nav scan watches the player's screenshot folders while it is on (off unless the player turns it on). A practice
        // copy watches none: it must not read the player's real pictures.
        using var navScan = new Nav.NavScan(watcher.State, routeCheck, heatmap, settings, places: Paths.Practice ? () => [] : null);
        Nav.HeatmapApi.Map(web, watcher.State, trip, spansh, coverage, heatmapSurvey,
            new Nav.NebulaList(heatmap, options.ContainsKey("offline") ? new Offline() : null), new Nav.FirstsLog(watcher.State, Paths.JournalDir, heatmap),
            fineMap, settings, routeCheck, navScan: navScan);
        Loadouts.EngineeringPlanner planner = Loadouts.EngineeringApi.Map(web, watcher.State, settings);
        Loadouts.GuardianApi.Map(web, watcher.State, settings);
        // And Studio Market's: one for its two kinds of search, so they share one queue to the community's servers, one memory
        // of answers and one price overview. --offline: practice runs must not send a real search every time they start.
        using var marketData = new Market.MarketData(options.ContainsKey("offline") ? new Offline() : null);
        // A practice copy must not pull in the player's real files: for it there is no Elite Haul Overlay to copy from.
        string? oldTool = Paths.Practice ? "" : null;
        var hauling = new Market.Hauling(watcher.State, settings, new Market.TradeSearch(marketData), oldSettingsFile: oldTool);
        Market.MarketApi.Map(web, hauling);
        // Hauling and the route in the in-game overlay, with the 3D map's check of the plotted route. It hooks itself onto
        // the overlay's boxes and the changes it shows, and they keep it alive.
        _ = new Market.HaulOverlay(overlay, hauling, route, trip, watcher.State, oldSettingsFile: oldTool, routeCheck: routeCheck, navScan: navScan);
        // The price list of every station whose commodities screen the player opens, kept for "Find by name" (the owner, 11 October 2026).
        var stationBook = new Market.StationBook(watcher.State, Path.Combine(Paths.DataDir, "stations.json"));
        Market.Finder finder = Market.FindApi.Map(web, watcher.State, hauling, marketData, book: stationBook);
        // The market sniffer: saved commodity searches run once an hour, with a chime when one is met (the owner, 11 October 2026).
        Market.Sniffer sniffer = Market.SnifferApi.Map(web, watcher.State, hauling, marketData, settings);
        // Studio Loadouts' box of the overlay. The owner, 10 October 2026: "for now just make it an overlay". So far it names the ship being flown.
        overlay.Box("loadouts")?.SetContent(() => watcher.State.Snapshot() is { ShipType: { Length: > 0 } ship } flown
            ? Overlay.Draw.Add(Overlay.Draw.Add(Overlay.Draw.Text(14.5), ship, bold: true), flown.ShipName is { Length: > 0 } named ? "   " + named : "", Overlay.Draw.Dim)
            : Overlay.Draw.Note("Studio does not know your ship yet. Start Elite."));
        // Studio Loadouts last: a saved build is handed on to the planner and, through Studio Market's searches, to a shopping trip.
        Loadouts.LoadoutsApi.Map(web, watcher.State, planner, finder);

        string? tickProblem = null;
        void Tick()
        {
            try
            {
                watcher.Poll();
                hauling.Tick();
                sniffer.Tick();
                tickProblem = null;
            }
            catch (Exception problem)
            {
                // This runs twice a second, so a problem that keeps coming back is logged once, not every time.
                if (problem.Message != tickProblem) Log.Error("The twice-a-second update failed", problem);
                tickProblem = problem.Message;
            }
        }

        // Catch up with the journal before the server opens, so the very first /api/state is already right.
        Tick();
        int firstPort = FirstPort(options);
        if (!web.Start(firstPort))
        {
            string problem = $"No free port from {firstPort} to {firstPort + 9}; close other copies of Elite Studio and start it again";
            Log.Error(problem);
            if (smoke) Log.Error($"SMOKE FAILED: {problem}");
            return 1;
        }
        File.WriteAllText(runningFile, JsonSerializer.Serialize(new RunningCopy(web.Port, web.Token, Environment.ProcessId), Settings.Json));
        Log.Info($"Elite Studio {Version} started on port {web.Port}");

        watcher.State.Changed += () => web.Send("state", watcher.State.Snapshot());
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        // Opened from inside the message loop so the window's async start-up continues on the UI thread.
        app.Startup += (_, _) => window?.Open(web.Url);

        int exitCode = app.Run();
        Log.Info("Exit");
        return exitCode;
    }

    /// <summary>The network of an --offline run: every request fails at once, as if the PC had no connection.</summary>
    sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Elite Studio was started with --offline."));
    }

    /// <summary>A second launch: ask the copy that is already running to show its window, then leave quietly.</summary>
    static void ShowRunningCopy()
    {
        string file = Path.Combine(Paths.DataDir, RunningFile);
        // The first copy may itself still be starting, so allow a few seconds for the file to appear and the server to answer.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                RunningCopy running = JsonSerializer.Deserialize<RunningCopy>(File.ReadAllText(file), Settings.Json)
                    ?? throw new JsonException("running.json is empty");
                // Windows lets only the program the player just started hand the foreground to another one.
                AllowSetForegroundWindow(running.Pid);
                // Straight to this PC, never through a proxy: one set up in Windows or in HTTP_PROXY would
                // otherwise be handed this call and could not reach the running copy.
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{running.Port}/api/app/show")
                {
                    Content = new StringContent("{}", new MediaTypeHeaderValue("application/json")),
                };
                request.Headers.Add("X-Studio-Token", running.Token);
                if (client.Send(request).IsSuccessStatusCode) return;
            }
            catch (Exception problem) when (problem is IOException or JsonException or HttpRequestException or OperationCanceledException)
            {
                // not there yet: try again
            }
            Thread.Sleep(300);
        }
        Log.Error("Elite Studio is already running but did not answer, so its window could not be brought to the front");
    }

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
