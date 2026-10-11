using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Overlay;

/// <summary>
/// One of the boxes drawn over the game: a see-through window without a frame that stays above
/// everything. It is exactly as big as what is in it, and sits on the monitor Elite is on, at its
/// own place and size from the settings (<see cref="OverlaySettings.Box"/>).
///
/// What is in the box comes from a studio (<see cref="SetContent"/>), built from the pieces in
/// <see cref="Draw"/>. Until a studio supplies something there is nothing to draw and it stays away.
///
/// Over Studio's own window a box lets every click through and never takes the keyboard. In the
/// game it takes the mouse (<see cref="TakeClicks"/>): it is then an ordinary window in front of
/// the game, with a strip on top to drag it by and buttons for its size and to hide it, and every
/// click on a tagged piece is passed on (<see cref="Clicked"/>).
/// </summary>
public sealed class OverlayWindow : Window
{
    const int SaidMs = 3000;   // how long a line from Say stays
    static readonly List<OverlayWindow> all = [];   // every box there is, for telling whether one of them is the window in front

    // The window styles the Haul Overlay set, by their Windows numbers.
    const int ExtendedStyle = -20;
    const int Layered = 0x80000, ClickThrough = 0x20, NeverActive = 0x8000000, NotInAltTab = 0x80;
    const int MainMonitorWidth = 0, MainMonitorHeight = 1;
    const uint KeepSize = 0x0001, DoNotActivate = 0x0010;
    const int TagSearchSteps = 14;   // how far up from a clicked piece its tag is looked for, as in the Haul Overlay
    const string DragTag = "box:drag";
    static readonly IntPtr AboveEverything = new(-1);

    /// <summary>The folder inside the --render-shots folder that holds hauling's pictures.</summary>
    internal const string HaulingShots = "hauling";

    readonly Border box = new() { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1.5), Padding = new Thickness(14, 8, 14, 9) };
    readonly ScaleTransform zoom = new();
    readonly string name;
    OverlaySettings look;
    Func<UIElement?> drawInside = () => null;
    long placedAt, saidAt;
    string? said;
    bool dragging;

    // A box that shows one of Studio's pages instead of drawn lines: the page's address, the web view it is shown in,
    // and the two things the box is then made of for good (a web view cannot be taken out and put back on every redraw).
    readonly Func<string>? page;
    readonly Microsoft.Web.WebView2.Wpf.WebView2? view;
    readonly Border strip = new();
    readonly StackPanel pageBox = new();
    bool loaded;

    /// <summary>
    /// How big a box with a page is at size 1: this share of the size its page is laid out at. The
    /// page draws itself smaller to fit whatever width the box has (studio.js, "boxWidth"), so a page
    /// made for a 1400 pixel window leaves most of a heavily scaled 4K screen (1280 units wide at 300%)
    /// to the game. (The web view's own zoom is left alone: with display scaling it did not give the
    /// page the width asked for.)
    /// </summary>
    const double PageZoom = 0.65;
    static readonly Size PageSize = new(1380, 620);

    /// <param name="name">Which box this is: one of <see cref="OverlaySettings.Boxes"/>.</param>
    /// <param name="page">
    /// For a box that shows one of Studio's own pages: the page's address, asked for when the box is
    /// first shown (the web server has its port by then). Such a box is solid, not see-through: a web
    /// view cannot be drawn in a see-through window. Left out, a studio draws the box (<see cref="SetContent"/>).
    /// </param>
    public OverlayWindow(string name, OverlaySettings look, Func<string>? page = null)
    {
        (this.name, this.look, this.page) = (name, look, page);
        all.Add(this);

        Title = "Elite Studio overlay";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = page is null;
        Background = page is null ? Brushes.Transparent : new SolidColorBrush(MainWindow.Dark);
        if (page is not null)
        {
            view = MainWindow.NewView();
            (view.Width, view.Height) = (PageSize.Width * PageZoom, PageSize.Height * PageZoom);
            pageBox.Children.Add(strip);
            pageBox.Children.Add(view);
            drawInside = () => pageBox;
        }
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;

        // The size setting scales the layout, not just the picture, so the window grows with it.
        box.LayoutTransform = zoom;
        TextElement.SetFontFamily(box, new FontFamily("Segoe UI"));
        TextElement.SetForeground(box, Draw.Brush(Draw.White));
        TextOptions.SetTextFormattingMode(box, TextFormattingMode.Display);
        Content = box;
        SizeChanged += (_, _) => Place();   // new text changes the size, and a centred box must move with it
        Fill();
    }

    /// <summary>
    /// Hands the inside of the box to a studio. Its function is called on every <see cref="Redraw"/>
    /// (the studio's own, and the one after each change in the game) and builds the content afresh
    /// from the pieces in <see cref="Draw"/>. When it returns null there is nothing to draw and the
    /// overlay stays away.
    /// </summary>
    public void SetContent(Func<UIElement?> draw)
    {
        drawInside = draw;
        Redraw();
    }

    /// <summary>Draws the box again. Call after anything it shows has changed.</summary>
    public void Redraw()
    {
        Fill();
        Sync();
    }

    /// <summary>The box takes clicks just now (see <see cref="TakeClicks"/>).</summary>
    public bool TakingClicks { get; private set; }

    /// <summary>
    /// A click landed on a tagged piece of the box while it takes clicks: the tag, which is whatever
    /// text the studio put in that piece's Tag (or in the Tag of something around it). The box's own
    /// buttons are tagged "box:smaller", "box:bigger" and "box:hide".
    /// </summary>
    public event Action<string>? Clicked;

    /// <summary>The player has dragged the box to a new place: where it now sits, as <see cref="BoxLook"/> holds it.</summary>
    public event Action<double, double>? Moved;

    /// <summary>
    /// The player counts as being in the game: Elite is the window in front, or one of the boxes is
    /// while it takes clicks. A box that is shown in the game is itself the window in front, and the
    /// hotkeys and the boxes must not go away because of that.
    /// </summary>
    internal static bool InGame => EliteWindow.IsForeground || (EliteWindow.Running && all.Any(box => box.TakingClicks && box.IsInFront));

    /// <summary>Shows a short line in the box's top strip for a few seconds ("✔ Sol copied").</summary>
    public void Say(string text)
    {
        (said, saidAt) = (text, Environment.TickCount64);
        Redraw();
    }

    /// <summary>
    /// On: the box stops letting clicks through, may become the active window, and takes the place in
    /// front from the game, which then waits. Off: it is click-through and never active again, and the
    /// place in front goes to another box that takes clicks or, with none left, back to Elite's
    /// window, but only when the box itself still has it, so nothing is taken from another program.
    /// </summary>
    public void TakeClicks(bool on)
    {
        if (on == TakingClicks) return;
        TakingClicks = on;
        IsHitTestVisible = on;
        Cursor = on ? Cursors.Hand : null;
        Fill();   // the top strip comes and goes with it
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;   // never been on screen yet: the styles are set when it first is

        SetStyles(handle);
        if (!on) HandBack();
        else if (IsVisible) ComeToFront(handle);   // a box that is not on screen must not hold the keyboard
    }

    /// <summary>
    /// The tag of what was clicked: its own, or that of the nearest thing it sits in (a run of text
    /// in a tagged line, a line in a tagged strip). Null when nothing around it carries one.
    /// </summary>
    internal static string? TagOf(object? clicked)
    {
        for (int step = 0; clicked is not null && step < TagSearchSteps; step++)
        {
            object? tag = clicked switch { FrameworkElement piece => piece.Tag, FrameworkContentElement text => text.Tag, _ => null };
            if (tag is string { Length: > 0 } found) return found;
            clicked = clicked switch
            {
                Visual piece => VisualTreeHelper.GetParent(piece),
                FrameworkContentElement text => text.Parent,   // a run of text is no visual: its line is its parent
                _ => null,
            };
        }
        return null;
    }

    /// <summary>
    /// The Haul Overlay's rule for when the box is on screen: switched on, and either allowed over any
    /// program, or Elite is the window in front, or one of Studio's own windows is (so a change to its
    /// place, size or colour can be seen while making it).
    /// </summary>
    internal static bool ShouldShow(bool overlayOn, bool onlyWhenElite, bool eliteInFront, bool studioInFront) =>
        overlayOn && (!onlyWhenElite || eliteInFront || studioInFront);

    /// <summary>
    /// Where the box's top-left corner goes, in screen pixels. Everything is worked out in the pixels
    /// of the monitor the box is going to, with the display scaling the box is drawn at, which is
    /// that monitor's own once the box is on it (see <see cref="Place"/>). (The Haul Overlay worked
    /// with one scaling for every monitor. In this program, which sees each monitor in its own real
    /// pixels, that would put the box in the wrong place when two monitors are scaled differently.)
    /// </summary>
    /// <param name="monitor">The whole monitor in screen pixels, taskbar included.</param>
    /// <param name="scaling">The display scaling the box is drawn at: 1 at 100%, 1.5 at 150%.</param>
    /// <param name="size">The box in WPF units, the size setting already in it.</param>
    /// <param name="across">0 puts the box at the left edge, 100 at the right edge, 50 in the middle: a share of the room it has left to move in, so it is never off the screen.</param>
    /// <param name="down">The same from the top edge to the bottom one.</param>
    internal static (int X, int Y) PlaceOn(Int32Rect monitor, double scaling, Size size, double across, double down) =>
        ((int)Math.Round(monitor.X + (monitor.Width - size.Width * scaling) * across / 100),
            (int)Math.Round(monitor.Y + (monitor.Height - size.Height * scaling) * down / 100));

    /// <summary>The other way round: what <see cref="PlaceOn"/> takes as "across" and "down" for a box that sits here, both in screen pixels.</summary>
    internal static (double Across, double Down) PlaceOf(Int32Rect monitor, Int32Rect box)
    {
        static double Share(int gone, int room) => room <= 0 ? 50 : Math.Round(Math.Clamp(100.0 * gone / room, 0, 100), 1);
        return (Share(box.X - monitor.X, monitor.Width - box.Width), Share(box.Y - monitor.Y, monitor.Height - box.Height));
    }

    /// <summary>One line saying where the player is, for a box whose studio has nothing of its own to show.</summary>
    internal static TextBlock StatusLine(GameSnapshot game, string accent)
    {
        TextBlock line = Draw.Text(14.5).Add("ELITE STUDIO", accent, bold: true, size: 17);
        if (string.IsNullOrEmpty(game.StarSystem)) return line.Add("   Waiting for Elite…", Draw.Soft);

        line.Add("   " + game.StarSystem, bold: true);
        if (game.Docked && !string.IsNullOrEmpty(game.Station)) line.Add("   docked at " + game.Station, Draw.Dim);
        if (game.JumpsLeft > 0)
        {
            string jumps = game.JumpsLeft.ToString(CultureInfo.InvariantCulture) + (game.JumpsLeft == 1 ? " jump left" : " jumps left");
            line.Add(Draw.Dot, Draw.Dim).Add(jumps).Add(" to " + game.RouteDestination, Draw.Dim);
        }
        return line;
    }

    /// <summary>
    /// Draws the overlay into PNG files without showing any window (the option --render-shots), so the
    /// look can be checked without the game: the status line, three places on a 1920 x 1080 screen, and
    /// a sample made of every piece in <see cref="Draw"/> at three sizes; then, in a folder of their own
    /// inside (<see cref="HaulingShots"/>), every state of hauling from made-up trades. Returns the exit code.
    /// </summary>
    public static int RenderShots(string folder)
    {
        Directory.CreateDirectory(folder);
        var shown = new GameSnapshot();
        var window = new OverlayWindow(OverlaySettings.Boxes[0], new OverlaySettings());
        window.drawInside = () => StatusLine(shown, window.look.Accent);
        var screen = new Int32Rect(0, 0, 1920, 1080);

        void Shoot(string name, bool onScreen = false)
        {
            window.Fill();
            Border box = window.box;
            box.InvalidateMeasure();
            box.UpdateLayout();   // let changed text settle first, or the size from the last picture is used again
            box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size size = box.DesiredSize;

            // On its own: the box with a margin, at twice the size so small text can be judged.
            // On the screen: the whole screen at its real size, the box where PlaceOn puts it.
            (double x, double y) = (14, 14);
            (double width, double height, double sharpness) = (size.Width + 28, size.Height + 28, 2);
            if (onScreen)
            {
                (x, y) = PlaceOn(screen, 1, size, window.look.HaulX, window.look.HaulY);
                (width, height, sharpness) = (screen.Width, screen.Height, 1);
            }
            box.Arrange(new Rect(x, y, size.Width, size.Height));
            box.UpdateLayout();

            var picture = new RenderTargetBitmap((int)Math.Ceiling(width * sharpness), (int)Math.Ceiling(height * sharpness),
                96 * sharpness, 96 * sharpness, PixelFormats.Pbgra32);
            var backdrop = new DrawingVisual();
            // something dark behind the box, as in the game, or its see-through background would come out as nothing
            using (DrawingContext paint = backdrop.RenderOpen()) paint.DrawRectangle(Draw.Brush("#FF10141C"), null, new Rect(0, 0, width, height));
            picture.Render(backdrop);
            picture.Render(box);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(picture));
            using FileStream file = File.Create(Path.Combine(folder, name));
            png.Save(file);
        }

        Shoot("status-waiting.png");
        shown = new GameSnapshot { StarSystem = "Erlaza", Docked = true, Station = "Ito Landing", RouteDestination = "61 Cygni", JumpsLeft = 5 };
        Shoot("status.png");
        foreach ((string place, double across, double down) in new[] { ("top-left", 0.0, 0.0), ("top-middle", 50.0, 4.0), ("bottom-right", 100.0, 100.0) })
        {
            window.look = window.look with { HaulX = across, HaulY = down };
            Shoot($"place-{place}.png", onScreen: true);
        }
        window.drawInside = () => Sample(window.look.Accent);
        foreach ((double scale, string name) in new[] { (1.0, "sample.png"), (0.7, "sample-scale-0.7.png"), (1.5, "sample-scale-1.5.png") })
        {
            window.look = new OverlaySettings { HaulScale = scale };
            Shoot(name);
        }

        // What a studio puts in the box is the studio's to show: hauling walks through its own states.
        Directory.CreateDirectory(Path.Combine(folder, HaulingShots));
        Market.HaulOverlay.RenderShots((name, look, content) =>
        {
            (window.look, window.drawInside) = (look, content);
            Shoot(Path.Combine(HaulingShots, name));
        });
        return 0;
    }

    /// <summary>
    /// Shows or hides the box by <see cref="ShouldShow"/>; with nothing to draw it stays hidden. While
    /// it shows, it is put back in its place above everything about once a second, as the Haul Overlay
    /// did: other always-on-top windows can cover it, and the game can be moved to another monitor.
    /// Called a few times a second and after every redraw.
    /// </summary>
    internal void Sync()
    {
        if (said is not null && Environment.TickCount64 - saidAt >= SaidMs)
        {
            said = null;
            Fill();
        }
        // The boxes do not count as Studio's windows here. One can be left as the active window (the game has gone,
        // or Windows would not put it in front), and would then keep itself on screen.
        bool studioInFront = Application.Current?.Windows.OfType<Window>().Any(window => window is not OverlayWindow && window.IsActive) == true;
        bool show = box.Child is not null && ShouldShow(look.Box(name).On, look.OnlyWhenElite, InGame, studioInFront);
        if (show && !IsVisible)
        {
            Show();
            Place();
            LoadPage();
            // Shown in the game while it takes clicks: the mouse is the game's until this box is the window in front.
            if (TakingClicks && EliteWindow.IsForeground) ComeToFront(new WindowInteropHelper(this).Handle);
        }
        else if (!show && IsVisible)
        {
            HandBack();   // hidden while it is the window in front: the game must not be left waiting
            Hide();
        }
        else if (show && Environment.TickCount64 - placedAt >= 1000) Place();
    }

    /// <summary>Takes changed settings into use at once.</summary>
    internal void Apply(OverlaySettings changed)
    {
        look = changed;
        Redraw();
        Place();   // a new position or gap moves the box without changing its size
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SetStyles(new WindowInteropHelper(this).Handle);
    }

    /// <summary>
    /// A click while the box takes clicks never reaches anything behind it; one on a tagged piece is
    /// passed on. The top strip is the exception: holding the mouse down on it drags the box, and
    /// where it is let go is told through <see cref="Moved"/>.
    /// </summary>
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (!TakingClicks) return;
        e.Handled = true;
        try
        {
            string? tag = TagOf(e.OriginalSource);
            if (tag != DragTag)
            {
                if (tag is not null) Clicked?.Invoke(tag);
                return;
            }
            dragging = true;   // or the once-a-second Place would pull the box back to where it was
            try { DragMove(); }   // comes back when the mouse button is let go
            finally { dragging = false; }
            if (!GetWindowRect(new WindowInteropHelper(this).Handle, out Edges at)) return;
            (double across, double down) = PlaceOf(Monitor(), new Int32Rect(at.Left, at.Top, at.Right - at.Left, at.Bottom - at.Top));
            Moved?.Invoke(across, down);
        }
        catch (Exception problem)
        {
            Log.Error("A click on the overlay could not be carried out", problem);
        }
    }

    /// <summary>
    /// Only the end of the program closes the overlay. Windows can ask every window of a program to
    /// close (it does for "taskkill" without /F); the main window answers that by hiding, and Studio
    /// runs on. A closed overlay could never be shown again, and every later redraw would fail.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e) => e.Cancel = true;   // WPF takes no notice of this when the whole app shuts down

    /// <summary>This box is the window in front (it only ever is while it takes clicks).</summary>
    bool IsInFront => new WindowInteropHelper(this).Handle is var handle && handle != IntPtr.Zero && GetForegroundWindow() == handle;

    /// <summary>
    /// Layered and out of Alt+Tab always. Click-through and never active too, except while the box
    /// takes clicks: those two are what mouse mode switches off and on again.
    /// </summary>
    void SetStyles(IntPtr handle)
    {
        // A box with a page is an ordinary solid window: "layered" would leave it undrawn, and it is never click-through.
        int through = view is null ? ClickThrough : 0;
        int styles = GetWindowLong(handle, ExtendedStyle) | NotInAltTab | (view is null ? Layered : 0);
        styles = TakingClicks ? styles & ~(ClickThrough | NeverActive) : styles | through | NeverActive;
        SetWindowLong(handle, ExtendedStyle, styles);
    }

    /// <summary>
    /// The first time a box with a page is on screen: starts its web view and opens the page. Links
    /// that would open a window of their own (a service's website) do nothing here: nothing may pop
    /// up over the game. A web engine that will not start is logged, and the box stays empty.
    /// </summary>
    async void LoadPage()
    {
        if (view is null || loaded) return;
        loaded = true;
        try
        {
            await view.EnsureCoreWebView2Async();
            view.CoreWebView2.Settings.AreDevToolsEnabled = false;
            view.CoreWebView2.Settings.IsStatusBarEnabled = false;
            view.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            view.CoreWebView2.Navigate(page!());
        }
        catch (Exception problem)   // WebView2 reports a missing or broken engine with several unrelated exception types
        {
            Log.Error($"The overlay's {name} box could not show its page", problem);
        }
    }

    void ComeToFront(IntPtr handle)
    {
        // Windows may say no; the box can still be clicked then, once the mouse is free of the game.
        if (!SetForegroundWindow(handle)) Log.Info("Mouse mode: Windows did not hand the mouse to the overlay");
    }

    /// <summary>
    /// The place in front goes on to another box that is on screen and takes clicks or, with none,
    /// back to Elite's window; but only when this box itself has it, so nothing is taken from another program.
    /// </summary>
    void HandBack()
    {
        if (!IsInFront) return;
        OverlayWindow? other = all.FirstOrDefault(box => box != this && box.IsVisible && box.TakingClicks);
        IntPtr next = other is null ? EliteWindow.Handle : new WindowInteropHelper(other).Handle;
        if (next != IntPtr.Zero) SetForegroundWindow(next);
    }

    /// <summary>Fills the box from the settings and the content, without touching the window itself.</summary>
    void Fill()
    {
        box.BorderBrush = Draw.Brush(look.Accent);
        // Only the background lets the game show through; text and border stay solid. Rounded as the
        // Haul Overlay rounded it, so its default of 0.92 still gives 235 of 255.
        box.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(look.Opacity * 255), 8, 12, 20));
        zoom.ScaleX = zoom.ScaleY = look.Box(name).Scale;
        if (view is not null)
        {
            // A page: the box is always the same strip over the same web view; only the strip is drawn afresh.
            // Its background is solid, and the page fits itself to the width the box has (studio.js).
            box.Background = new SolidColorBrush(MainWindow.Dark);
            strip.Child = TopStrip();
            if (box.Child != pageBox) box.Child = pageBox;
            return;
        }
        UIElement? inside = drawInside();
        box.Child = inside is null || !TakingClicks ? inside : Draw.Stack(TopStrip(), inside);
    }

    /// <summary>
    /// What a box that takes clicks has above its content: its name, which is also the handle to drag
    /// it by, a line from <see cref="Say"/> while there is one, and its own three buttons.
    /// </summary>
    DockPanel TopStrip()
    {
        TextBlock handle = Draw.Text(12.5).Add(name.ToUpperInvariant(), look.Accent, bold: true).Add("   hold here to move this box", Draw.Dim);
        // As good as invisible, but there: a strip with no colour at all takes no click, and the box could only be held by its letters.
        (handle.Tag, handle.Background, handle.Cursor) = (DragTag, Draw.Brush(Draw.Unlit), Cursors.SizeAll);
        DockPanel strip = Draw.Line(handle, Draw.Text(12.5).Add(said ?? "", Draw.Green, bold: true),
            Draw.Button("smaller", "box:smaller"), Draw.Button("bigger", "box:bigger"), Draw.Button("✕ hide", "box:hide"));
        strip.Margin = new Thickness(0, 0, 0, 6);
        return strip;
    }

    /// <summary>The whole monitor Elite is on, in screen pixels; the main monitor while the game has no window.</summary>
    static Int32Rect Monitor() =>
        EliteWindow.MonitorBounds ?? new Int32Rect(0, 0, GetSystemMetrics(MainMonitorWidth), GetSystemMetrics(MainMonitorHeight));

    /// <summary>
    /// Moves the window to its place on the monitor Elite is on (the main monitor while the game has
    /// no window) and above every other window. Positioned in screen pixels through Windows itself:
    /// WPF's own Left and Top are in the units of the monitor the window is on now, which is the wrong
    /// measure when it has to go to a monitor with other scaling.
    ///
    /// The sums use the scaling the box is drawn at this moment, because that is the size in pixels it
    /// really has. On a monitor with other scaling Windows has the box redrawn at that monitor's
    /// scaling while the move is being made, so the box is then placed once more at its new size.
    /// (Sums in the new monitor's scaling from the start would be for a size the box does not have
    /// yet: a box still three times too big could be left hanging over the monitor it came from, and
    /// stay that big, because Windows only changes a window's scaling once most of it is on another
    /// monitor.)
    /// </summary>
    void Place()
    {
        if (dragging || PresentationSource.FromVisual(this) is not HwndSource source || ActualWidth <= 0) return;   // not shown yet: Sync places it when it is
        placedAt = Environment.TickCount64;
        Int32Rect monitor = Monitor();
        BoxLook mine = look.Box(name);
        for (int move = 1; move <= 2; move++)
        {
            double scaling = VisualTreeHelper.GetDpi(this).DpiScaleX;
            (int x, int y) = PlaceOn(monitor, scaling, new Size(ActualWidth, ActualHeight), mine.X, mine.Y);
            SetWindowPos(source.Handle, AboveEverything, x, y, 0, 0, KeepSize | DoNotActivate);
            if (VisualTreeHelper.GetDpi(this).DpiScaleX == scaling) break;   // nearly always: same monitor, or one with the same scaling
        }
    }

    /// <summary>A trade as the Haul Overlay drew it, made of every piece in <see cref="Draw"/>. Only for <see cref="RenderShots"/>.</summary>
    static StackPanel Sample(string accent)
    {
        TextBlock title = Draw.Text(17).Add("SILVER", accent, bold: true);
        TextBlock status = Draw.Text(12.5).Add("trade 1 of 21" + Draw.Dot + "prices updated 6h ago", Draw.Dim);
        TextBlock goBack = Draw.Text(17).Add("THEN BRING BACK   ", Draw.Dim, size: 12.5).Add("MILITARY GRADE FABRICS", accent, bold: true);

        Grid table = Draw.Table();
        table.Cell(0, 0, 12.5, Draw.Gold).Add("► BUY", bold: true);
        table.Cell(0, 1, 15).Add("Ito Landing", bold: true).Add("  (L pad) · Erlaza", Draw.Dim);
        table.Cell(0, 2, 14).Add("★ you are docked here", Draw.Gold);
        table.Cell(0, 3, 14).Add("41,293 cr", bold: true).Add("   84,054 t for sale", Draw.Dim);
        table.Cell(1, 0, 12.5, Draw.Grey).Add("SELL", bold: true);
        table.Cell(1, 1, 15, Draw.Grey).Add("Broglie Terminal", bold: true).Add("  (L pad) · 61 Cygni");
        table.Cell(1, 2, 14, Draw.Grey).Add("54.4 ly trip" + Draw.Dot + "~5 jumps full");
        table.Cell(1, 3, 14, Draw.Grey).Add("49,275 cr", bold: true).Add("   wants 19,198 t");
        table.Cell(2, 0, 12.5, accent).Add("BACK", bold: true);
        table.Cell(2, 1, 15).Add("Military Grade Fabrics", bold: true).Add("  to Ito Landing", Draw.Dim);
        table.Cell(2, 2, 14).Add("54.4 ly back" + Draw.Dot + "~5 jumps full");
        table.Cell(2, 3, 14).Add("+11,208 cr/t", Draw.Green, bold: true);

        TextBlock profit = Draw.Text(14.5).Add("PROFIT   +7,982 cr/t", Draw.Green, bold: true).Add(Draw.Dot, Draw.Dim).Add("+3.1M this load", Draw.Green)
            .Add(Draw.Dot + "round trip ", Draw.Dim).Add("+7.4M", Draw.Green).Add(" in ~12 min" + Draw.Dot, Draw.Dim).Add("38.0M cr/hour", Draw.Green, bold: true);
        TextBlock next = Draw.Text(14.5).Add("NEXT STOP   ", Draw.Gold, bold: true).Add("61 Cygni", bold: true).Add("   sell at Broglie Terminal", Draw.Dim)
            .Add(Draw.Dot + "54.4 ly from you" + Draw.Dot + "5 jumps on your plotted route");
        TextBlock keys = Draw.Text(12.5).Add("✔ 61 Cygni copied", Draw.Green, bold: true);
        DockPanel nextLine = Draw.Line(next, keys);
        nextLine.Margin = new Thickness(0, 4, 0, 0);

        return Draw.Stack(Draw.Line(title, status, goBack), table, Draw.Rule(profit), nextLine,
            Draw.Note("A sample for checking the look: this note line normally stands alone, in place of the table."));
    }

    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int which);

    [StructLayout(LayoutKind.Sequential)]
    struct Edges
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr window, out Edges edges);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr window);
}

/// <summary>
/// The pieces overlay content is built from, the same few the Haul Overlay drew with: a line of text
/// made of runs that can each have their own colour, weight and size; a table of such lines; a line
/// with text at both ends; a note; a hairline. All are ordinary WPF elements, so anything they do
/// not cover can be set on them directly. Font and default text colour come from the box.
/// </summary>
public static class Draw
{
    public const string White = "#FFE8EEF7";      // main text
    public const string Dim = "#FF8A98AD";        // secondary text, separators, hints
    public const string Grey = "#FFB7C0CC";       // the destination row
    public const string Green = "#FF7CFC9A";      // everything about money
    public const string Gold = "#FFE4FF3D";       // the current leg, warnings, the thing being changed
    public const string Soft = "#FFB8C4D6";       // the note line
    public const string Hairline = "#40FFFFFF";
    public const string Unlit = "#01000000";      // as good as invisible, but there: something with no colour at all takes no click
    public const string ButtonBack = "#33FFFFFF";

    /// <summary>The gap between phrases on one line: three spaces, a middle dot, three spaces.</summary>
    public const string Dot = "   ·   ";

    /// <summary>A whole number as the overlay writes every one of them: the English way (41,293), whatever Windows is set to.</summary>
    public static string Whole(long number) => number.ToString("N0", CultureInfo.InvariantCulture);

    public static SolidColorBrush Brush(string colour)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colour));
        brush.Freeze();
        return brush;
    }

    /// <summary>An empty line of text in one size (and colour). Fill it with <see cref="Add"/>.</summary>
    public static TextBlock Text(double size, string? colour = null)
    {
        var text = new TextBlock { FontSize = size };
        if (colour is not null) text.Foreground = Brush(colour);
        return text;
    }

    /// <summary>
    /// Adds a run of text to a line. Colour and size are the line's own unless given. Bold is
    /// semi-bold, the only heavier weight the overlay uses.
    /// </summary>
    public static TextBlock Add(this TextBlock line, string text, string? colour = null, bool bold = false, double size = 0)
    {
        var run = new Run(text);
        if (colour is not null) run.Foreground = Brush(colour);
        if (bold) run.FontWeight = FontWeights.SemiBold;
        if (size > 0) run.FontSize = size;
        line.Inlines.Add(run);
        return line;
    }

    /// <summary>
    /// One line with text at both ends: the first at the left, the others pushed to the right edge in
    /// the order given, each at least 28 units from its neighbour. This is the header line, and the
    /// NEXT STOP line with its key hints.
    /// </summary>
    public static DockPanel Line(TextBlock left, params TextBlock[] right)
    {
        var line = new DockPanel();
        for (int i = right.Length - 1; i >= 0; i--)   // docked from the last to the first, so they read in the order given
        {
            TextBlock text = right[i];
            DockPanel.SetDock(text, Dock.Right);
            // Smaller text sits on the same baseline as the left text: Segoe UI hangs a quarter of its
            // size below the baseline, so that much of the difference goes under the smaller one.
            text.VerticalAlignment = VerticalAlignment.Bottom;
            text.Margin = new Thickness(28, 0, 0, Math.Max(0, left.FontSize - text.FontSize) * 0.25);
            line.Children.Add(text);
        }
        line.Children.Add(left);   // last, so it gets the room that is left over
        return line;
    }

    /// <summary>An empty table whose columns are as wide as their widest cell. Fill it with <see cref="Cell"/>.</summary>
    public static Grid Table() => new() { Margin = new Thickness(0, 5, 0, 0) };

    /// <summary>Adds one cell to a table and returns its line of text to fill. Rows and columns appear as they are used.</summary>
    public static TextBlock Cell(this Grid table, int row, int column, double size, string? colour = null)
    {
        while (table.RowDefinitions.Count <= row) table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        while (table.ColumnDefinitions.Count <= column) table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock cell = Text(size, colour);
        cell.VerticalAlignment = VerticalAlignment.Center;
        cell.Margin = new Thickness(column > 0 ? 20 : 0, 1.5, 0, 1.5);
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        table.Children.Add(cell);
        return cell;
    }

    /// <summary>A piece of text to click, on a strip of its own colour. The tag says what a click on it does (see <see cref="OverlayWindow.Clicked"/>).</summary>
    public static TextBlock Button(string text, string tag, string ink = White, string background = ButtonBack, bool bold = false)
    {
        TextBlock button = Text(13, ink).Add(text, bold: bold);
        (button.Padding, button.Margin, button.Background, button.Tag) = (new Thickness(9, 2, 9, 2), new Thickness(0, 0, 6, 0), Brush(background), tag);
        return button;
    }

    /// <summary>One plain line of status text, for when there is nothing else to show ("Searching…").</summary>
    public static TextBlock Note(string text)
    {
        TextBlock note = Text(14, Soft).Add(text);
        note.Margin = new Thickness(0, 2, 0, 0);
        return note;
    }

    /// <summary>A hairline across the box with something under it: how the PROFIT line is set off from the table.</summary>
    public static Border Rule(UIElement under) => new()
    {
        BorderThickness = new Thickness(0, 1, 0, 0),
        BorderBrush = Brush(Hairline),
        Margin = new Thickness(0, 6, 0, 0),
        Padding = new Thickness(0, 5, 0, 0),
        Child = under,
    };

    /// <summary>Lines one under the other: the whole content of the box.</summary>
    public static StackPanel Stack(params UIElement[] lines)
    {
        var stack = new StackPanel();
        foreach (UIElement line in lines) stack.Children.Add(line);
        return stack;
    }
}
