using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace EliteStudio.App;

/// <summary>
/// The desktop window: one WebView2 (the web engine built into Windows) showing the pages from the local
/// web server, plus the icon near the clock. Closing the window only hides it, because Studio has to keep
/// running for the in-game overlay; "Exit" on the tray icon really leaves.
/// </summary>
public sealed class MainWindow : Window, IStudioWindow
{
    /// <summary>
    /// The old tool's dark window colour, close to (not the same as) the pages' own background. Window and
    /// web view start in it so there is no white flash while the pages load.
    /// </summary>
    internal static readonly Color Dark = Color.FromRgb(11, 15, 23);

    /// <summary>
    /// A web view for Studio's own pages: dark from the start, and on the one browser profile every
    /// view of the program shares (the overlay's boxes that show a page use it too). The profile (the
    /// pages' saved preferences) lives with Studio's data, never beside the program, where a synced
    /// or antivirus-protected folder could get in its way.
    /// </summary>
    internal static WebView2 NewView() => new()
    {
        DefaultBackgroundColor = System.Drawing.Color.FromArgb(Dark.R, Dark.G, Dark.B),
        CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = Path.Combine(Paths.DataDir, "WebView2") },
    };

    readonly WebView2 view = NewView();
    readonly Forms.NotifyIcon tray;
    readonly bool smoke;
    bool smokeDone;

    /// <param name="smoke">True for a --smoke run: report to the log whether a page loaded, then leave.</param>
    public MainWindow(bool smoke)
    {
        this.smoke = smoke;
        Title = "Elite Studio";   // the title-bar and taskbar icon is the program's own, set in the project file
        Background = new SolidColorBrush(Dark);
        Content = view;
        view.SizeChanged += (_, _) => FitZoom();

        Rect screen = SystemParameters.WorkArea;
        Width = screen.Width * 0.88;
        Height = screen.Height * 0.88;
        MinWidth = Math.Min(1000, screen.Width * 0.6);
        MinHeight = Math.Min(650, screen.Height * 0.6);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (smoke)
        {
            // A check run must not take the keyboard from whatever is in use. (WPF refuses to open a
            // window both maximized and unfocused, so it also stays at its normal size.)
            ShowActivated = false;
            var limit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            limit.Tick += (_, _) => FinishSmoke("no page had loaded after 60 seconds");
            limit.Start();
        }
        else
        {
            WindowState = WindowState.Maximized;
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => BringToFront());
        menu.Items.Add("Exit", null, (_, _) => Exit());
        using Stream icon = typeof(MainWindow).Assembly.GetManifestResourceStream("studio.ico")!;
        tray = new Forms.NotifyIcon
        {
            Text = Title,
            Icon = new System.Drawing.Icon(icon, Forms.SystemInformation.SmallIconSize),
            ContextMenuStrip = menu,
        };
        tray.DoubleClick += (_, _) => BringToFront();
        Application.Current.Exit += (_, _) => tray.Dispose();
    }

    /// <summary>
    /// Shows the window and the tray icon and loads the pages. If the web engine cannot start, the reason
    /// is logged and shown in the window itself as plain text.
    /// </summary>
    public async void Open(string url)
    {
        tray.Visible = true;
        Show();
        try
        {
            await view.EnsureCoreWebView2Async();
        }
        catch (Exception problem)   // WebView2 reports a missing or broken engine with several unrelated exception types
        {
            string reason = problem is WebView2RuntimeNotFoundException
                ? "This PC is missing the Microsoft Edge WebView2 Runtime (built into Windows 11 and most Windows 10 PCs). "
                    + "It is a free download from Microsoft: https://go.microsoft.com/fwlink/p/?LinkId=2124703"
                : problem.Message;
            Log.Error($"The window's web engine could not start: {reason}");
            Content = new TextBlock
            {
                Text = $"Elite Studio could not start its window.\n\n{reason}\n\nUntil that is fixed, Studio also works in your normal web browser at {url}",
                Foreground = Brushes.Gainsboro,
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(40),
            };
            FinishSmoke("the window's web engine could not start");
            return;
        }

        CoreWebView2 core = view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // The window only ever shows Studio's own pages; links to websites open in the player's normal browser.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            AppApi.OpenInBrowser(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            // "blob:" is how a page hands over a file it made itself (exporting a theme).
            bool own = e.Uri.StartsWith(url, StringComparison.OrdinalIgnoreCase)
                || e.Uri.StartsWith("blob:" + url, StringComparison.OrdinalIgnoreCase)
                || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase);
            if (own) return;
            e.Cancel = true;
            AppApi.OpenInBrowser(e.Uri);
        };
        core.NavigationCompleted += (_, e) =>
        {
            string? problem = e.IsSuccess ? null : $"the page did not load ({e.WebErrorStatus}, HTTP status {e.HttpStatusCode})";
            if (smoke) FinishSmoke(problem);
            else if (problem is not null) Log.Error($"In the window, {problem}");
        };
        core.ProcessFailed += (_, e) => Log.Error($"The window's web engine had a problem: {e.ProcessFailedKind}");
        FitZoom();
        if (!smoke) view.Focus();   // so keyboard shortcuts reach the page without a click first
        core.Navigate(url);
    }

    /// <summary>Shows the window again (from the tray, or when a second copy is started) and brings it forward.</summary>
    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(this);
        Activate();
    }

    /// <summary>The folder picker belongs to the Studio window, so that is brought forward first, in case it was hidden or behind a browser.</summary>
    public string? PickFolder(string title)
    {
        BringToFront();
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    /// <summary>Ends the app, from whichever thread asks.</summary>
    public void Quit() => Dispatcher.InvokeAsync(() => Application.Current?.Shutdown());

    public string Engine => WebViewVersion();

    /// <summary>The WebView2 engine's version on this PC, or "not found".</summary>
    internal static string WebViewVersion()
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

    protected override void OnClosing(CancelEventArgs e)
    {
        // Hide instead of closing. When the whole app is shutting down WPF ignores the cancel, so Exit still works.
        e.Cancel = true;
        Hide();
    }

    void Exit()
    {
        if (AppApi.UnsavedChanges > 0)
        {
            BringToFront();
            MessageBoxResult answer = MessageBox.Show(this, "You have unsaved changes. Exit anyway?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }
        Application.Current.Shutdown();
    }

    void FitZoom()
    {
        if (view.CoreWebView2 is null || view.ActualWidth <= 0) return;
        double zoom = IStudioWindow.ZoomFor(view.ActualWidth);   // the rule every system's window follows
        if (Math.Abs(view.ZoomFactor - zoom) > 0.01) view.ZoomFactor = zoom;
    }

    /// <summary>Ends a --smoke run: one line in the log saying how it went, two seconds for the page to settle, then exit code 0 or 1.</summary>
    async void FinishSmoke(string? problem)
    {
        if (!smoke || smokeDone) return;
        smokeDone = true;
        if (problem is null) Log.Info("SMOKE READY");
        else Log.Error($"SMOKE FAILED: {problem}");
        await Task.Delay(TimeSpan.FromSeconds(2));
        Application.Current.Shutdown(problem is null ? 0 : 1);
    }
}
