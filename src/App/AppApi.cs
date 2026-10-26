using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using EliteStudio.Core;
using EliteStudio.UI;

namespace EliteStudio.App;

/// <summary>
/// The window the pages are shown in, as the app-level routes need it. Each system has its own (Windows: WebView2 in a
/// WPF window; a --headless run has a stand-in that shows nothing).
/// </summary>
public interface IStudioWindow
{
    /// <summary>Shows the window again and brings it forward. Called on the app's UI thread.</summary>
    void BringToFront();

    /// <summary>Ends Studio. May be called from any thread.</summary>
    void Quit();

    /// <summary>Asks the player for a folder; null when they cancel. Called on the app's UI thread.</summary>
    string? PickFolder(string title);

    /// <summary>The web engine and its version, for the report.</summary>
    string Engine { get; }

    /// <summary>
    /// The pages are laid out for a width of 1400: on a narrower window (or a heavily scaled display) zoom
    /// out a little, down to 60%, so the side-by-side layout survives. Never zooms in. Every system's window follows it.
    /// </summary>
    /// <param name="width">The web view's width with display scaling taken out (on Windows, in WPF units).</param>
    internal static double ZoomFor(double width) => Math.Round(Math.Clamp(width / 1400, 0.6, 1.0), 2);
}

/// <summary>The "linux" section of settings.json.</summary>
internal sealed record LinuxSettings
{
    /// <summary>The journal folder the player chose by hand; used from the next start, over the one Studio found.</summary>
    public string? JournalDir { get; init; }
}

/// <summary>
/// The app-level routes: GET /api/state and everything under /api/app/. They give the pages what the old
/// tool's window gave them through WebView2-only messages (folder picker, report file, page errors in
/// the log, the unsaved-changes count), so the pages work the same in a normal browser.
/// </summary>
public static class AppApi
{
    static volatile int unsavedChanges;

    /// <summary>How many unsaved changes the pages last reported. Exit asks before throwing them away.</summary>
    public static int UnsavedChanges => unsavedChanges;

    /// <summary>What a page may send to these routes; each route reads the one field it needs.</summary>
    sealed class Body
    {
        public string Text { get; set; } = "";
        public string Message { get; set; } = "";
        public int Count { get; set; }
        public string Url { get; set; } = "";
    }

    /// <summary>Registers the routes. Call on the UI thread, which is also where the server runs them.</summary>
    /// <param name="window">Null only in tests: nothing is ever shown, and the folder pickers answer as if cancelled.</param>
    /// <param name="settings">Where the journal folder the player chose is kept (POST /api/app/pick-journal); null only in tests.</param>
    public static void Map(WebServer web, GameState state, IStudioWindow? window, Settings? settings = null)
    {
        WebResponse Done() => WebResponse.Json(new { ok = true });

        web.Map("GET", "/api/state", _ => WebResponse.Json(state.Snapshot()));

        web.Map("POST", "/api/app/pick-folder", _ =>
            WebResponse.Json(new { path = window?.PickFolder("Choose your Elite Dangerous folder") is string picked ? FindGameFolder(picked) : null }));

        web.Map("POST", "/api/app/report", request =>
            WebResponse.Json(new { path = SaveReport(Paths.DataDir, request.Read<Body>().Text, window?.Engine) }));

        web.Map("POST", "/api/app/log", request =>
        {
            Log.Error($"page: {request.Read<Body>().Message}");
            return Done();
        });

        web.Map("POST", "/api/app/dirty", request =>
        {
            unsavedChanges = request.Read<Body>().Count;
            return Done();
        });

        web.Map("POST", "/api/app/show", _ =>
        {
            window?.BringToFront();
            return Done();
        });

        web.Map("POST", "/api/app/quit", _ =>
        {
            // Answer first, then leave a moment later, so the caller gets its reply before the server is gone.
            Task.Delay(200).ContinueWith(answered => window?.Quit());
            return Done();
        });

        // A link to a website, opened in the player's own browser (for a window that cannot do that by itself).
        web.Map("POST", "/api/app/open", request =>
            OpenInBrowser(request.Read<Body>().Url) ? Done() : WebResponse.Error("Only links to websites can be opened.", 400));

        // Where Studio reads the game from, and the player's own choice of journal folder, kept for the next start
        // (Linux, where the game's folders are inside Proton's and Studio may not find them).
        web.Map("GET", "/api/app/journal", _ => WebResponse.Json(new { path = Paths.JournalDir, found = Directory.Exists(Paths.JournalDir) }));

        web.Map("POST", "/api/app/pick-journal", _ =>
        {
            string? path = window?.PickFolder("Choose the folder that holds Elite's journal files");
            if (path is not null) settings?.Set("linux", settings.Get<LinuxSettings>("linux") with { JournalDir = path });
            return WebResponse.Json(new { path });
        });
    }

    /// <summary>
    /// Turns the folder a player picked into the folder that holds EliteDangerous64.exe: the pick itself,
    /// or one of the places below it where a launcher keeps the game (<see cref="GameFolder.ProductFolders"/>).
    /// When the game is in none of them the answer is "?" + the pick, and the page explains what to
    /// choose instead.
    /// </summary>
    internal static string FindGameFolder(string picked) =>
        ((string[])["", .. GameFolder.ProductFolders]).Select(place => Path.Combine(picked, place)).FirstOrDefault(GameFolder.HasGame) ?? "?" + picked;

    /// <summary>
    /// Writes what the page gathered, followed by this PC's details and the end of the log, as one text
    /// file the owner can send when asking for help. Returns the file's full path.
    /// </summary>
    /// <param name="engine">The window's web engine (<see cref="IStudioWindow.Engine"/>); null without a window.</param>
    internal static string SaveReport(string dataDir, string text, string? engine = null)
    {
        string folder = Path.GetFullPath(Path.Combine(dataDir, "logs"));
        Directory.CreateDirectory(folder);
        DateTime now = DateTime.Now;
        string path = Path.Combine(folder, $"report-{now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}.txt");
        var report = new StringBuilder()
            .AppendLine("ELITE STUDIO REPORT")
            .AppendLine(CultureInfo.InvariantCulture, $"Time: {now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine(CultureInfo.InvariantCulture, $"Version: {Program.Version}")
            .AppendLine()
            .AppendLine(text)
            .AppendLine()
            .AppendLine("--- this PC ---");
        if (OperatingSystem.IsWindows())
            report.AppendLine(CultureInfo.InvariantCulture, $"Windows: {Environment.OSVersion.VersionString}")
                .AppendLine(CultureInfo.InvariantCulture, $".NET: {Environment.Version}")
                .AppendLine(CultureInfo.InvariantCulture, $"WebView2: {engine ?? "not found"}");
        else
            // Off Windows the system's own name says more ("Ubuntu 24.04.1 LTS", "SteamOS") than its kernel's version.
            report.AppendLine(CultureInfo.InvariantCulture, $"System: {RuntimeInformation.OSDescription}")
                .AppendLine(CultureInfo.InvariantCulture, $".NET: {Environment.Version}")
                .AppendLine(CultureInfo.InvariantCulture, $"Web engine: {engine ?? "no window"}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Program folder: {AppContext.BaseDirectory}")
            .AppendLine(CultureInfo.InvariantCulture, $"Data folder: {dataDir}")
            .AppendLine("--- end of the log ---")
            .AppendLine(LogTail(60));
        File.WriteAllText(path, report.ToString());
        return path;
    }

    /// <summary>
    /// Opens a link to a website in the player's normal browser (on Linux through xdg-open). False, and nothing
    /// opened, for anything that is not an http or https link.
    /// </summary>
    internal static bool OpenInBrowser(string uri)
    {
        if (!uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception problem)   // no default browser set up
        {
            Log.Error($"Could not open {uri} in the browser", problem);
        }
        return true;
    }

    static string LogTail(int lines)
    {
        if (Log.FilePath is null || !File.Exists(Log.FilePath)) return "(none)";
        // Opened for sharing because the log may be written at this very moment.
        using var file = new FileStream(Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return string.Join('\n', reader.ReadToEnd().TrimEnd().Split('\n').TakeLast(lines));
    }
}
