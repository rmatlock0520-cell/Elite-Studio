using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EliteStudio.Overlay;

/// <summary>One of the overlay's boxes as the settings hold it.</summary>
/// <param name="On">Shown. Its key in the game switches this.</param>
/// <param name="X">Where it sits across the screen, 0 (left edge) to 100 (right edge): a share of the room the box has left to move in.</param>
/// <param name="Y">The same down the screen, 0 (top) to 100 (bottom).</param>
/// <param name="Scale">Its size, 0.7 to 2.</param>
public readonly record struct BoxLook(bool On, double X, double Y, double Scale);

/// <summary>
/// The "overlay" section of settings.json. The overlay is five boxes, one per studio part: hauling,
/// Find by name ("market"), the route ("nav"), loadouts and the flight inputs ("flight"). Each has its
/// own key that shows or hides it, its own place and its own size; the colour and the background are shared.
/// </summary>
public sealed record OverlaySettings
{
    /// <summary>The boxes by the names code uses, in the order of their keys.</summary>
    public static readonly IReadOnlyList<string> Boxes = ["haul", "market", "nav", "loadouts", "flight"];

    /// <summary>
    /// The box that only shows and never takes the mouse: the flight inputs are watched while flying,
    /// and the game must keep the stick and the keys the whole time. It is moved and sized from Studio.
    /// </summary>
    public const string ShowOnly = "flight";
    static readonly OverlaySettings Defaults = new();

    // The key that shows or hides each box, as Ctrl, Alt, Shift and Win joined with "+" and then a
    // key by its Windows name: "F2", "Ctrl+Shift+H", "Alt+D1".
    public string HotHaul { get; init; } = "F2";
    public string HotMarket { get; init; } = "F3";
    public string HotNav { get; init; } = "F4";
    public string HotLoadouts { get; init; } = "F5";
    public string HotFlight { get; init; } = "F6";

    // A second way to each of the five: a button on a game controller, or one held while another is
    // pressed, as PadChord writes them ("046D:C215 7", "046D:C215 3 + 046D:C215 7"). Empty: none.
    public string PadHaul { get; init; } = "";
    public string PadMarket { get; init; } = "";
    public string PadNav { get; init; } = "";
    public string PadLoadouts { get; init; } = "";
    public string PadFlight { get; init; } = "";

    // Each box: shown or not, where it sits and how big it is (see BoxLook). Every box starts hidden.
    public bool HaulOn { get; init; }
    public double HaulX { get; init; } = 50;
    public double HaulY { get; init; } = 4;
    public double HaulScale { get; init; } = 1.0;
    public bool MarketOn { get; init; }
    public double MarketX { get; init; } = 2;
    public double MarketY { get; init; } = 35;
    public double MarketScale { get; init; } = 1.0;
    public bool NavOn { get; init; }
    public double NavX { get; init; } = 50;
    public double NavY { get; init; } = 96;
    public double NavScale { get; init; } = 1.0;
    public bool LoadoutsOn { get; init; }
    public double LoadoutsX { get; init; } = 98;
    public double LoadoutsY { get; init; } = 35;
    public double LoadoutsScale { get; init; } = 1.0;
    public bool FlightOn { get; init; }
    public double FlightX { get; init; } = 50;
    public double FlightY { get; init; } = 70;
    public double FlightScale { get; init; } = 1.0;

    /// <summary>How solid the background is, 0.2 to 1. Text and border are always solid.</summary>
    public double Opacity { get; init; } = 0.92;
    /// <summary>
    /// How solid the two boxes that show a page are (Find by name and the route), 0.3 to 1. Such a
    /// box cannot have a background of its own strength: the whole box, letters and all, is this solid.
    /// </summary>
    public double PageOpacity { get; init; } = 1;
    /// <summary>Show the boxes only while Elite (or Studio's own window) is in front.</summary>
    public bool OnlyWhenElite { get; init; } = true;
    /// <summary>Colour of the border and the headings, as #RRGGBB.</summary>
    public string Accent { get; init; } = "#3D8BFF";

    /// <summary>
    /// Not a setting, a marker saved with them: the Elite Haul Overlay's settings file the look was
    /// copied from on the overlay's very first run, until the player has read the note that says so.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CopiedFrom { get; init; }

    /// <summary>One box's part of the settings; an unknown name counts as hauling's.</summary>
    public BoxLook Box(string name) => name switch
    {
        "market" => new(MarketOn, MarketX, MarketY, MarketScale),
        "nav" => new(NavOn, NavX, NavY, NavScale),
        "loadouts" => new(LoadoutsOn, LoadoutsX, LoadoutsY, LoadoutsScale),
        "flight" => new(FlightOn, FlightX, FlightY, FlightScale),
        _ => new(HaulOn, HaulX, HaulY, HaulScale),
    };

    /// <summary>The same settings with one box's part replaced.</summary>
    public OverlaySettings With(string name, BoxLook box) => name switch
    {
        "market" => this with { MarketOn = box.On, MarketX = box.X, MarketY = box.Y, MarketScale = box.Scale },
        "nav" => this with { NavOn = box.On, NavX = box.X, NavY = box.Y, NavScale = box.Scale },
        "loadouts" => this with { LoadoutsOn = box.On, LoadoutsX = box.X, LoadoutsY = box.Y, LoadoutsScale = box.Scale },
        "flight" => this with { FlightOn = box.On, FlightX = box.X, FlightY = box.Y, FlightScale = box.Scale },
        _ => this with { HaulOn = box.On, HaulX = box.X, HaulY = box.Y, HaulScale = box.Scale },
    };

    /// <summary>
    /// The hotkeys by the box each shows or hides, with the name of the setting that holds each key.
    /// The order is the one they are registered in: when two boxes are given the same key, the
    /// earlier one gets it.
    /// </summary>
    public IReadOnlyList<(string Action, string Setting, string Key)> Hotkeys() =>
        [("haul", "hotHaul", HotHaul), ("market", "hotMarket", HotMarket), ("nav", "hotNav", HotNav), ("loadouts", "hotLoadouts", HotLoadouts), ("flight", "hotFlight", HotFlight)];

    /// <summary>The controller buttons of the same boxes in the same order, with the name of the setting that holds each.</summary>
    public IReadOnlyList<(string Action, string Setting, string Buttons)> PadButtons() =>
        [("haul", "padHaul", PadHaul), ("market", "padMarket", PadMarket), ("nav", "padNav", PadNav), ("loadouts", "padLoadouts", PadLoadouts), ("flight", "padFlight", PadFlight)];

    /// <summary>
    /// The same settings with every value made usable: numbers inside their ranges, an unknown colour
    /// and a missing key back at the default, controller buttons that cannot be read taken off. The
    /// file can be edited by hand, and a page may send anything.
    /// </summary>
    internal OverlaySettings Cleaned()
    {
        OverlaySettings cleaned = this with
        {
            PadHaul = PadOrNone(PadHaul),
            PadMarket = PadOrNone(PadMarket),
            PadNav = PadOrNone(PadNav),
            PadLoadouts = PadOrNone(PadLoadouts),
            PadFlight = PadOrNone(PadFlight),
            HotHaul = KeyOr(HotHaul, Defaults.HotHaul),
            HotMarket = KeyOr(HotMarket, Defaults.HotMarket),
            HotNav = KeyOr(HotNav, Defaults.HotNav),
            HotLoadouts = KeyOr(HotLoadouts, Defaults.HotLoadouts),
            HotFlight = KeyOr(HotFlight, Defaults.HotFlight),
            Opacity = Math.Round(Math.Clamp(Opacity, 0.2, 1), 2),
            PageOpacity = Math.Round(Math.Clamp(PageOpacity, 0.3, 1), 2),
            Accent = Accent is not null && Regex.IsMatch(Accent, "^#[0-9A-Fa-f]{6}$") ? Accent : Defaults.Accent,
        };
        foreach (string name in Boxes)
        {
            BoxLook box = cleaned.Box(name);
            cleaned = cleaned.With(name, box with
            {
                X = Math.Round(Math.Clamp(box.X, 0, 100), 1),
                Y = Math.Round(Math.Clamp(box.Y, 0, 100), 1),
                Scale = Math.Round(Math.Clamp(box.Scale, 0.7, 2), 2),
            });
        }
        return cleaned;
    }

    // JSON can put null where the class says there is always text.
    static string KeyOr(string? key, string fallback) => string.IsNullOrWhiteSpace(key) ? fallback : key.Trim();

    static string PadOrNone(string? buttons) => PadChord.Parse(buttons)?.ToString() ?? "";
}
