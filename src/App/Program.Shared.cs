using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EliteStudio.App;

/// <summary>What the running copy leaves in running.json so a second launch (and test tools) can reach it.</summary>
internal sealed record RunningCopy(int Port, string Token, int Pid);

/// <summary>
/// What this build of Studio has, for the pages (GET /api/ping): they hide whatever is not there instead of showing it dead.
/// </summary>
/// <param name="System">"windows" or "linux".</param>
/// <param name="Flight">Studio Flight.</param>
/// <param name="Overlay">The boxes shown over the game, their keys and controller buttons.</param>
/// <param name="NavScan">Nav scan, which reads system names off the player's screenshots.</param>
/// <param name="ReloadKey">Studio presses F11 in the game itself after a theme is saved.</param>
/// <param name="TrayIcon">An icon near the clock, so closing the window leaves Studio running.</param>
public sealed record StudioFeatures(string System, bool Flight, bool Overlay, bool NavScan, bool ReloadKey, bool TrayIcon)
{
    public static readonly StudioFeatures Windows = new("windows", true, true, true, true, true);
    public static readonly StudioFeatures Linux = new("linux", false, false, false, false, false);
}

// The parts of start-up that every build shares. Each system's own start (Main, the window, the message loop) is in its own file.
public static partial class Program
{
    /// <summary>Studio's version as it is shown and sent everywhere: "1.7.1".</summary>
    public static readonly string Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>Studio's public home: where its code and downloads are, and where the data services it asks can reach its maker.</summary>
    public const string Home = "https://github.com/rmatlock0520-cell/Elite-Studio";

    /// <summary>
    /// How Studio names itself to community services (Spansh, Ardent Insight, EDAstro), so their owners can see which
    /// program is calling, with <see cref="Home"/> as the way to reach its maker.
    /// </summary>
    public static readonly string UserAgent = $"EliteStudio/{Version} (Elite Dangerous companion app; +{Home})";

    /// <summary>The server's first choice. A steady port matters: browsers keep each page's saved preferences per address.</summary>
    public const int PreferredPort = 47820;

    /// <summary>
    /// What this build has: everything on Windows, the Linux set elsewhere (the code those parts need is not in that build).
    /// A start-up can still change it before the server starts.
    /// </summary>
    public static StudioFeatures Features { get; set; } = OperatingSystem.IsWindows() ? StudioFeatures.Windows : StudioFeatures.Linux;

    internal const string RunningFile = "running.json";

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

    /// <summary>The network of an --offline run: every request fails at once, as if the PC had no connection.</summary>
    internal sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Elite Studio was started with --offline."));
    }

    /// <summary>A second launch: ask the copy that is already running to show its window, then leave quietly.</summary>
    internal static void ShowRunningCopy()
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
                if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(running.Pid);
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
            catch (Exception problem) when (problem is IOException or JsonException or HttpRequestException or OperationCanceledException or DllNotFoundException)
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
