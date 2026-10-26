using System.Runtime.InteropServices;
using System.Text.Json;
using Photino.NET;

namespace EliteStudio.App;

/// <summary>
/// Studio's window on Linux: a Photino window (GTK with WebKitGTK, the web engine of most Linux desktops) showing the
/// pages from the local web server. There is no in-game overlay to keep Studio running for, so there is no icon near the
/// clock: closing the window ends Studio, after the same unsaved-changes question as Exit on Windows.
///
/// When the window cannot start (WebKitGTK is missing, as on a Steam Deck, or there is no desktop) or the player asks for
/// it (--browser, or ELITE_STUDIO_BROWSER=1), Studio opens in the player's own web browser instead, and runs until Exit
/// or until it is stopped (Ctrl+C, or the system closing it). A --headless run shows nothing and ends on Exit.
/// </summary>
/// <param name="smoke">True for a --smoke run with a window: report to the log whether a page loaded, then leave.</param>
internal sealed class StudioWindow(bool headless, bool smoke, bool browser) : IStudioWindow
{
    const string Title = "Elite Studio";

    readonly Lock gate = new();
    readonly ManualResetEventSlim ended = new();
    PhotinoWindow? open;              // the window while it is up: Photino may only be called then
    volatile bool quitting;
    volatile string? noWindow;        // why Studio is in the browser instead, for the log and the report
    string url = "";
    int smokeDone;

    /// <summary>What Studio leaves with: 0, or 1 when a --smoke run failed.</summary>
    public int ExitCode { get; private set; }

    public string Engine => headless ? "none (--headless)" : noWindow is null ? "WebKitGTK" : $"the player's web browser ({noWindow})";

    /// <summary>
    /// Shows Studio at url, in the window or else in the browser, and returns when Studio is to end: the window closed,
    /// Exit, or Studio stopped. Runs on the main thread, which GTK needs for itself.
    /// </summary>
    public void Show(string url)
    {
        this.url = url;
        if (smoke) Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(_ => FinishSmoke("no page had loaded after 60 seconds"));
        if (!headless)
        {
            noWindow = browser ? "asked for with --browser or ELITE_STUDIO_BROWSER=1" : NoDesktop() ?? ShowWindow();
            if (noWindow is null) return;   // the window was shown, and has been closed
            string line = $"Studio opens in the web browser at {url}, not in a window of its own: {noWindow}";
            if (browser) Log.Info(line);
            else Log.Error(line);
            // A check run is about the window, so without one it fails instead of passing in some browser.
            if (smoke && !browser) FinishSmoke($"the window could not start: {noWindow}");
            else AppApi.OpenInBrowser(url);
        }
        ended.Wait();
    }

    /// <summary>
    /// Why there is no desktop to show the window on, or null when there is. Photino starts GTK in the way that ends the
    /// whole program when the desktop cannot be reached (none is set, or the one set refuses, as under sudo or over SSH),
    /// so GTK is started here first, in the way that answers instead. Photino then finds it started.
    /// </summary>
    static string? NoDesktop()
    {
        try
        {
            XInitThreads();   // as Photino does: before any other call to the X display
            if (gtk_init_check(IntPtr.Zero, IntPtr.Zero)) return null;
        }
        catch (DllNotFoundException missing)   // no GTK: the reason names the library
        {
            return missing.Message.ReplaceLineEndings(" ");
        }
        return $"no desktop could be reached to show a window on (DISPLAY \"{Environment.GetEnvironmentVariable("DISPLAY")}\", WAYLAND_DISPLAY \"{Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")}\")";
    }

    /// <summary>The window until it is closed (null), or why it could not start: mostly a library missing, libwebkit2gtk-4.1.</summary>
    string? ShowWindow()
    {
        bool created = false;
        try
        {
            var window = new PhotinoWindow()
                .SetTitle(Title)
                .SetLogVerbosity(0)   // Photino would talk to the terminal
                .SetDevToolsEnabled(false)
                // Photino cannot colour its web view, so it starts on a dark page of its own that moves straight on to
                // Studio's (the colour of the window on Windows): no white flash while the pages load.
                .LoadRawString($"<html style=\"background:#0b0f17\"><script>location.replace({JsonSerializer.Serialize(url)})</script></html>");
            if (IconFile() is string icon) window.SetIconFile(icon);
            window.RegisterWindowCreatedHandler((_, _) =>
            {
                bool close;
                lock (gate)
                {
                    open = window;
                    created = true;
                    close = quitting;
                }
                // As on Windows: 88% of the screen's free area when restored, at least 1000 x 650 (less on a small screen),
                // in the middle, opened maximised. A check run must not take over the screen: it keeps its normal size.
                if (window.Monitors is [var screen, ..])
                {
                    var work = screen.WorkArea;
                    int width = work.Width * 88 / 100, height = work.Height * 88 / 100;
                    window.SetMinSize(Math.Min(1000, work.Width * 6 / 10), Math.Min(650, work.Height * 6 / 10))
                        .SetSize(width, height)
                        .MoveTo(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, allowOutsideWorkArea: true);
                }
                if (!smoke) window.SetMaximized(true);
                if (close) window.Close();
            });
            // The zoom rule of every system's window (IStudioWindow.ZoomFor); Photino takes it in percent.
            window.RegisterSizeChangedHandler((_, size) => window.Zoom = (int)Math.Round(IStudioWindow.ZoomFor(size.Width) * 100));
            // True keeps the window open. Exit and Studio being stopped never ask; the player closing the window does.
            window.RegisterWindowClosingHandler((_, _) => !quitting && AppApi.UnsavedChanges > 0
                && window.ShowMessage(Title, "You have unsaved changes. Exit anyway?", PhotinoDialogButtons.YesNo, PhotinoDialogIcon.Warning) != PhotinoDialogResult.Yes);
            window.WaitForClose();
            return null;
        }
        catch (Exception problem) when (!created)   // Photino reports a missing library inside its own exception
        {
            return (problem.InnerException ?? problem).Message.ReplaceLineEndings(" ");
        }
        finally
        {
            lock (gate) open = null;
        }
    }

    /// <summary>Raises the window (a second start of Studio asks for this); in the browser, opens Studio there again.</summary>
    public void BringToFront()
    {
        PhotinoWindow? window;
        lock (gate) window = open;
        if (window is not null) window.Invoke(Present);
        else if (noWindow is not null) AppApi.OpenInBrowser(url);
    }

    /// <summary>Ends Studio: closes the window, or lets a browser or --headless run end. From any thread.</summary>
    public void Quit()
    {
        PhotinoWindow? window;
        lock (gate)
        {
            quitting = true;
            window = open;
        }
        ended.Set();
        window?.Close();
    }

    /// <summary>
    /// GTK's folder picker, starting in the home folder (Steam's folders are inside it). This thread waits while it is
    /// open. Without the window, the desktop's own picker; a --headless run never asks.
    /// </summary>
    public string? PickFolder(string title)
    {
        PhotinoWindow? window;
        lock (gate) window = open;
        if (window is not null) return window.ShowOpenFolder(title, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).FirstOrDefault();
        return noWindow is null ? null : Desktop.PickFolder(title);
    }

    /// <summary>A --smoke run passes once the window's page has asked Studio for the game, as the pages do as soon as they load.</summary>
    public void PageLoaded() => FinishSmoke(null);

    /// <summary>Ends a --smoke run: one line in the log saying how it went, two seconds for the page to settle, then exit code 0 or 1.</summary>
    void FinishSmoke(string? problem)
    {
        if (!smoke || Interlocked.Exchange(ref smokeDone, 1) == 1) return;
        if (problem is null) Log.Info("SMOKE READY");
        else Log.Error($"SMOKE FAILED: {problem}");
        ExitCode = problem is null ? 0 : 1;
        Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => Quit());
    }

    /// <summary>GTK takes a window's icon only from a file, by its full path, so the one carried inside the program is put in the data folder.</summary>
    static string? IconFile()
    {
        string file = Path.Combine(Paths.DataDir, "studio.png");
        using Stream? icon = typeof(StudioWindow).Assembly.GetManifestResourceStream("studio.png");
        if (icon is null) return null;
        try
        {
            using FileStream copy = File.Create(file);
            icon.CopyTo(copy);
            return file;
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            return null;   // a window without its icon is still a window
        }
    }

    /// <summary>Photino has no call that raises its window, so GTK is asked directly, on the window's own thread.</summary>
    static void Present()
    {
        IntPtr windows = gtk_window_list_toplevels();
        for (IntPtr item = windows; item != IntPtr.Zero; item = Marshal.ReadIntPtr(item, IntPtr.Size))   // a GList: data, next, previous
            if (Marshal.PtrToStringUTF8(gtk_window_get_title(Marshal.ReadIntPtr(item))) == Title) gtk_window_present(Marshal.ReadIntPtr(item));
        g_list_free(windows);
    }

    [DllImport("libX11.so.6")]
    static extern int XInitThreads();

    [DllImport("libgtk-3.so.0")]
    static extern bool gtk_init_check(IntPtr argc, IntPtr argv);

    [DllImport("libgtk-3.so.0")]
    static extern IntPtr gtk_window_list_toplevels();

    [DllImport("libgtk-3.so.0")]
    static extern IntPtr gtk_window_get_title(IntPtr window);

    [DllImport("libgtk-3.so.0")]
    static extern void gtk_window_present(IntPtr window);

    [DllImport("libglib-2.0.so.0")]
    static extern void g_list_free(IntPtr list);
}
