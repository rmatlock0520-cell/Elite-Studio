using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EliteStudio.Core;
using EliteStudio.Overlay;

namespace EliteStudio.Flight;

/// <summary>
/// The flight overlay: what the hands do, drawn over the game while flying, the way pilots show it to
/// the people watching them fly with Flight Assist off. On the left the throttle hand (throttle, boost,
/// Flight Assist, the lateral and vertical thrusters), on the right the stick hand (the four defence
/// buttons, yaw, pitch and roll). A red dot is what the game gets; a white ring beside it is the real
/// stick before Gremlin's curve, so a curve can be seen at work. The box never takes the mouse (see
/// <see cref="OverlaySettings.ShowOnly"/>), and is drawn afresh with every reading of the sticks, about
/// sixty times a second, while it is on screen.
/// </summary>
internal sealed class FlightOverlay
{
    const string Box = "flight";
    const double Side = 118, BarLong = 118, BarThick = 22, PillW = 64, PillH = 22;
    const string Red = "#FFFF4A3D", Ring = "#FFE8EEF7", Lit = "#FFFFB020";

    readonly OverlayHost host;
    readonly GameState game;
    readonly Func<FlightControls> controls;
    readonly Canvas panel = new() { Width = 600, Height = 168 };

    /// <summary>What the box shows (for tests, which draw it to a picture).</summary>
    internal Canvas Panel => panel;
    readonly TextBlock title = new() { FontSize = 13, FontWeight = FontWeights.SemiBold };
    readonly TextBlock state = new() { FontSize = 13, FontWeight = FontWeights.SemiBold };
    readonly List<Shape> outlines = [];
    readonly Dictionary<string, (Border Pill, TextBlock Text)> pills = [];
    readonly Dictionary<string, (Ellipse Game, Ellipse Real)> dots = [];
    readonly Border throttleTop, throttleBottom;
    string accent = "";
    double throttle;          // where the throttle is set when no throttle axis is bound: kept from the speed buttons
    HashSet<string> before = [];
    long lastTime;

    // The places of the parts on the panel.
    static readonly Point ThrottleAt = new(8, 30), LateralAt = new(128, 30), YawAt = new(306, 128), PitchAt = new(470, 30);

    public FlightOverlay(OverlayHost host, FlightReader reader, Func<FlightControls> controls, GameState game)
    {
        (this.host, this.game, this.controls) = (host, game, controls);
        Place(title, 4, 2);
        Place(state, 200, 2);
        Frame(ThrottleAt, BarThick, BarLong, "THROTTLE", vertical: true);
        (throttleTop, throttleBottom) = (Cap(ThrottleAt.X, ThrottleAt.Y - 6), Cap(ThrottleAt.X, ThrottleAt.Y + BarLong + 2));
        AddPill("boost", "BOOST", 46, 30);
        AddPill("fa", "FA", 46, 60);
        Frame(LateralAt, Side, Side, "LATERAL · VERTICAL", cross: true);
        AddPill("chaff", "CHAFF", 306, 30);
        AddPill("ecm", "ECM", 376, 30);
        AddPill("heat", "HEAT", 306, 60);
        AddPill("shield", "SHIELD", 376, 60);
        Frame(YawAt, Side + 32, BarThick, "YAW", vertical: false);
        Frame(PitchAt, Side, Side, "PITCH · ROLL", cross: true);
        foreach (string name in (string[])["throttle", "lateral", "yaw", "pitch"])
        {
            var gameDot = new Ellipse { Width = 12, Height = 12, Fill = Draw.Brush(Red) };
            var realDot = new Ellipse { Width = 14, Height = 14, Stroke = Draw.Brush(Ring), StrokeThickness = 1.5, Visibility = Visibility.Hidden };
            panel.Children.Add(realDot);
            panel.Children.Add(gameDot);
            dots[name] = (gameDot, realDot);
        }

        if (host.Box(Box) is not { } window) return;   // a run without windows
        window.SetContent(() => panel);
        reader.OverlayWants = () => window.IsVisible;
        reader.Read += frame =>
        {
            if (window.IsVisible) Show(controls().Take(frame));
        };
        Show(null);
    }

    /// <summary>Draws one reading (null: none yet, everything in the middle).</summary>
    internal void Show(ControlsFrame? now)
    {
        if (host.Settings.Accent != accent) Recolour(host.Settings.Accent);
        bool faOff = game.Status.FlightAssistOff;
        title.Text = "FLIGHT INPUTS";
        state.Text = !game.Status.InMainShip && !game.Status.InFighter ? "" : faOff ? "FLIGHT ASSIST OFF" : "flight assist on";
        state.Foreground = Brush(faOff ? Lit : Draw.Dim);

        HashSet<string> pressed = now is null ? [] : [.. now.Pressed];
        double seconds = now is null || lastTime == 0 ? 0 : Math.Clamp((now.Time - lastTime) / 1000.0, 0, 0.2);
        lastTime = now?.Time ?? 0;
        ControlValue? Value(string name) => now?.Controls.GetValueOrDefault(name);

        // The throttle: its axis when one is bound; otherwise where the speed buttons last set it.
        if (Value("throttle") is { Game: { } axis } t) throttle = Throttle(axis, t.Invert, controls().ThrottleForwardOnly);
        else
        {
            foreach ((string button, double to) in Presets)
                if (pressed.Contains(button) && !before.Contains(button)) throttle = to;
            if (pressed.Contains("throttleUp")) throttle = Math.Min(1, throttle + seconds * 0.6);
            if (pressed.Contains("throttleDown")) throttle = Math.Max(-1, throttle - seconds * 0.6);
        }
        Put("throttle", ThrottleAt, BarThick, BarLong, 0, -throttle, null);
        throttleTop.Background = Brush(pressed.Contains("forward") ? Lit : Draw.Unlit);
        throttleBottom.Background = Brush(pressed.Contains("backward") ? Lit : Draw.Unlit);

        double lateral = Value("lateral")?.Game ?? (pressed.Contains("right") ? 1 : 0) - (pressed.Contains("left") ? 1 : 0);
        double vertical = Value("vertical")?.Game is { } v ? -v : (pressed.Contains("up") ? 1 : 0) - (pressed.Contains("down") ? 1 : 0);
        Put("lateral", LateralAt, Side, Side, lateral, -vertical, null);

        ControlValue? yaw = Value("yaw");
        Put("yaw", YawAt, Side + 32, BarThick, yaw?.Game ?? Keys(pressed, "yawRight", "yawLeft"), 0, yaw?.Real is { } yr ? (yr, 0) : null);
        ControlValue? roll = Value("roll"), pitch = Value("pitch");
        double pitchY = pitch?.Game ?? Keys(pressed, "pitchDown", "pitchUp");
        (double, double)? real = roll?.Real is { } rr && pitch?.Real is { } pr ? (rr, pr) : null;
        Put("pitch", PitchAt, Side, Side, roll?.Game ?? Keys(pressed, "rollRight", "rollLeft"), pitchY, real);

        foreach ((string name, (Border pill, TextBlock text)) in pills)
        {
            bool on = name == "fa" ? faOff : pressed.Contains(name);
            pill.Background = on ? Brush(name == "fa" ? Lit : accent) : Brush(Draw.Unlit);
            text.Foreground = Brush(on ? "#FF0A0E14" : Draw.Dim);
            if (name == "fa") text.Text = faOff ? "FA OFF" : "FA";
        }
        before = pressed;
    }

    /// <summary>The speed buttons and where each sets the throttle.</summary>
    static readonly (string Button, double To)[] Presets =
    [
        ("speedMinus100", -1), ("speedMinus75", -0.75), ("speedMinus50", -0.5), ("speedMinus25", -0.25), ("speedZero", 0),
        ("speed25", 0.25), ("speed50", 0.5), ("speed75", 0.75), ("speed100", 1),
    ];

    /// <summary>A throttle axis as -1 (full reverse) to 1 (full ahead): pushed away is ahead unless the game inverts it.</summary>
    internal static double Throttle(double axis, bool invert, bool forwardOnly = false) => forwardOnly ? ((invert ? axis : -axis) + 1) / 2 : invert ? axis : -axis;

    /// <summary>
    /// One brush per colour, made once: the box is drawn about sixty times a second, and a brush handed over afresh each
    /// time counts as a change that has the whole see-through window drawn again, even when nothing moved.
    /// </summary>
    static SolidColorBrush Brush(string colour)
    {
        if (!Made.TryGetValue(colour, out SolidColorBrush? brush)) Made[colour] = brush = Draw.Brush(colour);
        return brush;
    }
    static readonly Dictionary<string, SolidColorBrush> Made = [];

    static double Keys(HashSet<string> pressed, string plus, string minus) => (pressed.Contains(plus) ? 1 : 0) - (pressed.Contains(minus) ? 1 : 0);

    /// <summary>Puts a part's dots: across and down from -1 to 1 inside its frame; the ring at the real stick's place, hidden when there is none or it lies under the dot.</summary>
    void Put(string name, Point at, double width, double height, double across, double down, (double Across, double Down)? real)
    {
        (Ellipse gameDot, Ellipse realDot) = dots[name];
        static double Clamp(double v) => Math.Clamp(double.IsFinite(v) ? v : 0, -1, 1);
        double Px(double v, double size, double start) => start + (size - 12) / 2 * (1 + Clamp(v));
        Canvas.SetLeft(gameDot, width > height || width == height ? Px(across, width, at.X) : at.X + (width - 12) / 2);
        Canvas.SetTop(gameDot, height > width || width == height ? Px(down, height, at.Y) : at.Y + (height - 12) / 2);
        if (real is { } r && Math.Abs(r.Across - across) + Math.Abs(r.Down - down) > 0.02)
        {
            realDot.Visibility = Visibility.Visible;
            Canvas.SetLeft(realDot, (width >= height ? Px(r.Across, width, at.X) : at.X + (width - 12) / 2) - 1);
            Canvas.SetTop(realDot, (height >= width ? Px(r.Down, height, at.Y) : at.Y + (height - 12) / 2) - 1);
        }
        else realDot.Visibility = Visibility.Hidden;
    }

    void Recolour(string colour)
    {
        accent = colour;
        title.Foreground = Draw.Brush(colour);
        foreach (Shape outline in outlines) outline.Stroke = Draw.Brush(colour);
        foreach ((Border pill, _) in pills.Values) pill.BorderBrush = Draw.Brush(colour);
    }

    /// <summary>The rounded frame of a bar or a box, with its name in small letters, and a cross through the middle of a box.</summary>
    void Frame(Point at, double width, double height, string name, bool vertical = false, bool cross = false)
    {
        var frame = new Rectangle { Width = width, Height = height, RadiusX = 7, RadiusY = 7, StrokeThickness = 1.5, Fill = Draw.Brush("#22000000") };
        outlines.Add(frame);
        Place(frame, at.X, at.Y);
        if (cross || vertical || width > height)
        {
            var line = new Line { Stroke = Draw.Brush("#55FFFFFF"), StrokeThickness = 1 };
            if (cross || !vertical) (line.X1, line.Y1, line.X2, line.Y2) = (at.X + 6, at.Y + height / 2, at.X + width - 6, at.Y + height / 2);
            else (line.X1, line.Y1, line.X2, line.Y2) = (at.X + width / 2, at.Y + 6, at.X + width / 2, at.Y + height - 6);
            panel.Children.Add(line);
            if (cross) panel.Children.Add(new Line { Stroke = Draw.Brush("#55FFFFFF"), StrokeThickness = 1, X1 = at.X + width / 2, Y1 = at.Y + 6, X2 = at.X + width / 2, Y2 = at.Y + height - 6 });
        }
        var label = new TextBlock { Text = name, FontSize = 10, Foreground = Draw.Brush(Draw.Dim) };
        if (vertical)
        {
            label.LayoutTransform = new RotateTransform(-90);
            Place(label, at.X + width + 1, at.Y + 2);
        }
        else Place(label, at.X, at.Y + height + 1);
    }

    Border Cap(double x, double y)
    {
        var cap = new Border { Width = BarThick, Height = 4, CornerRadius = new CornerRadius(2), Background = Draw.Brush(Draw.Unlit) };
        Place(cap, x, y);
        return cap;
    }

    void AddPill(string name, string text, double x, double y)
    {
        var label = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var pill = new Border { Width = PillW, Height = PillH, CornerRadius = new CornerRadius(PillH / 2), BorderThickness = new Thickness(1.5), Child = label };
        Place(pill, x, y);
        pills[name] = (pill, label);
    }

    void Place(UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        panel.Children.Add(element);
    }
}
