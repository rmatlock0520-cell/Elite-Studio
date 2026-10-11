using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using EliteStudio.Core;
using EliteStudio.UI;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace EliteStudio.App;

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
    }

    /// <summary>Registers the routes. Call on the UI thread, which is also where the server runs them.</summary>
    /// <param name="window">Null in a --headless run: nothing is ever shown, and the folder picker answers as if cancelled.</param>
    public static void Map(WebServer web, GameState state, MainWindow? window)
    {
        Dispatcher ui = Dispatcher.CurrentDispatcher;
        WebResponse Done() => WebResponse.Json(new { ok = true });

        web.Map("GET", "/api/state", _ => WebResponse.Json(state.Snapshot()));

        web.Map("POST", "/api/app/pick-folder", _ => WebResponse.Json(new { path = PickGameFolder(window) }));

        web.Map("POST", "/api/app/report", request =>
            WebResponse.Json(new { path = SaveReport(Paths.DataDir, request.Read<Body>().Text) }));

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
            Task.Delay(200).ContinueWith(answered => ui.InvokeAsync(() => Application.Current?.Shutdown()));
            return Done();
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
    internal static string SaveReport(string dataDir, string text)
    {
        string folder = Path.GetFullPath(Path.Combine(dataDir, "logs"));
        Directory.CreateDirectory(folder);
        DateTime now = DateTime.Now;
        string path = Path.Combine(folder, $"report-{now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}.txt");
        string report = new StringBuilder()
            .AppendLine("ELITE STUDIO REPORT")
            .AppendLine(CultureInfo.InvariantCulture, $"Time: {now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine(CultureInfo.InvariantCulture, $"Version: {Program.Version}")
            .AppendLine()
            .AppendLine(text)
            .AppendLine()
            .AppendLine("--- this PC ---")
            .AppendLine(CultureInfo.InvariantCulture, $"Windows: {Environment.OSVersion.VersionString}")
            .AppendLine(CultureInfo.InvariantCulture, $".NET: {Environment.Version}")
            .AppendLine(CultureInfo.InvariantCulture, $"WebView2: {WebViewVersion()}")
            .AppendLine(CultureInfo.InvariantCulture, $"Program folder: {AppContext.BaseDirectory}")
            .AppendLine(CultureInfo.InvariantCulture, $"Data folder: {dataDir}")
            .AppendLine("--- end of the log ---")
            .AppendLine(LogTail(60))
            .ToString();
        File.WriteAllText(path, report);
        return path;
    }

    static string? PickGameFolder(MainWindow? window)
    {
        if (window is null) return null;
        // The picker belongs to the Studio window, so bring that forward in case it was hidden or behind a browser.
        window.BringToFront();
        var dialog = new OpenFolderDialog { Title = "Choose your Elite Dangerous folder" };
        return dialog.ShowDialog(window) == true ? FindGameFolder(dialog.FolderName) : null;
    }

    static string WebViewVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception)   // not installed, or its loader is missing: the report must still be written
        {
            return "not found";
        }
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
