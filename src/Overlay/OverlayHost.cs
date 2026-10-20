using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using EliteStudio.App;
using EliteStudio.Core;
using StudioSettings = EliteStudio.App.Settings;

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

/// <summary>
/// The in-game overlay as the rest of Studio sees it: its settings (with the routes
/// GET and POST /api/overlay/settings, POST /api/overlay/listen, POST /api/overlay/old-tool-read
/// and the "overlay" live event), its window, its hotkeys and its controller buttons, kept in step
/// with the game.
///
/// A studio that wants to show something calls <see cref="OverlayWindow.SetContent"/> on its box
/// (<see cref="Box"/>), then <see cref="OverlayWindow.Redraw"/> whenever something of its own
/// changed (a change in the game redraws every box by itself), and listens to
/// <see cref="OverlayWindow.Clicked"/> for its buttons, to <see cref="KeyPressed"/> and, for anything
/// that goes away by itself after a while, to <see cref="Ticked"/>.
///
/// A box that is shown in the game takes the mouse: the game waits while any box is up, and has
/// mouse and keyboard back when the last one is hidden. Every box carries a strip of its own to
/// drag it by, two buttons for its size and one to hide it; a button tagged "copy:" followed by a
/// text puts that text on the clipboard.
/// </summary>
public sealed class OverlayHost : IDisposable
{
    const string Section = "overlay";
    const double SizeStep = 0.1;   // what one click on "smaller" or "bigger" changes

    readonly WebServer web;
    readonly StudioSettings file;
    readonly GameState game;
    readonly Dictionary<string, OverlayWindow> boxes = [];
    readonly Hotkeys? hotkeys;
    readonly Controller controller;
    readonly DispatcherTimer? timer;
    (DisplayMode Mode, KeyBindings Bindings) told;   // what the last answer said of the game's own files
    string? tickProblem;

    /// <summary>What a page sends to the listen route: the setting to keep the next controller button in ("padNext").</summary>
    sealed class ListenBody
    {
        public string Setting { get; set; } = "";
    }

    /// <summary>The boxes that show a page of Studio's own, and the page each shows.</summary>
    static readonly Dictionary<string, string> Pages = new() { ["market"] = "market/find.html", ["nav"] = "nav/" };

    /// <summary>What a page in a box sends to have another box opened: the box, and what its page is to do ("?to=Sol"; "" for nothing).</summary>
    sealed class OpenBody
    {
        public string Box { get; set; } = "";
        public string Page { get; set; } = "";
    }

    /// <summary>
    /// POST /api/overlay/open: a page shown in one box wants another box's page, with something for
    /// it to do (Find by name's "Route" hands a system to the Routes page). That box is shown, with
    /// its page opened so; the box that asked stays as it is, with its own key still its own. Only a
    /// box that shows a page can be opened this way, and only with words for that page after a "?".
    /// </summary>
    WebResponse Open(WebRequest request)
    {
        OpenBody asked = request.Read<OpenBody>();
        string name = (asked.Box ?? "").Trim(), doing = (asked.Page ?? "").Trim();
        if (!Pages.TryGetValue(name, out string? page)) return WebResponse.Error("That part of Studio has no overlay to open.", 400);
        if (doing.Length > 0 && !doing.StartsWith('?')) return WebResponse.Error("An overlay opens with words for its own page only.", 400);
        if (!boxes.TryGetValue(name, out OverlayWindow? box)) return WebResponse.Error("The overlay is not running.", 409);
        box.Open(web.Url + page + "?overlay" + (doing.Length > 1 ? "&" + doing[1..] : ""));
        Apply(Settings.With(name, Settings.Box(name) with { On = true }));
        return WebResponse.Json(Answer());
    }

    /// <param name="headless">
    /// True for a run without windows: the settings and their routes work, nothing is shown and no key is
    /// taken. A page can still have a controller button listened for; in the game none does anything.
    /// </param>
    /// <param name="readPads">How controller buttons are read (see <see cref="Controller"/>); tests put made-up controllers here.</param>
    public OverlayHost(WebServer web, StudioSettings settings, GameState game, bool headless, Func<bool, IReadOnlyList<PadState>>? readPads = null)
    {
        (this.web, file, this.game) = (web, settings, game);
        Settings = settings.Get<OverlaySettings>(Section).Cleaned();
        web.Map("GET", "/api/overlay/settings", _ => WebResponse.Json(Answer()));
        web.Map("POST", "/api/overlay/settings", Change);
        web.Map("POST", "/api/overlay/listen", Listen);
        web.Map("POST", "/api/overlay/open", Open);
        web.Map("POST", "/api/overlay/old-tool-read", _ =>
        {
            Apply(Settings with { CopiedFrom = null });
            return WebResponse.Json(Answer());
        });
        told = (game.DisplayMode, game.Bindings);
        game.Changed += OnGameChanged;
        controller = new Controller(readPads);
        controller.Pressed += Press;
        controller.Start();
        if (headless) return;

        foreach (string name in OverlaySettings.Boxes)
        {
            // Two boxes show a page of Studio's own, whole: Find by name with its results, and Routes with the route beside it.
            // "?overlay" tells a page it is in a box (see studio.js).
            string? page = Pages.GetValueOrDefault(name);
            var box = new OverlayWindow(name, Settings, page is null ? null : () => web.Url + page + "?overlay");
            box.Clicked += tag => Click(name, tag);
            box.Moved += (x, y) => Apply(Settings.With(name, Settings.Box(name) with { X = x, Y = y }));
            game.Changed += box.Redraw;
            boxes[name] = box;
        }
        hotkeys = new Hotkeys(game);
        hotkeys.Pressed += Press;
        // Its own timer, four times a second, rather than the app's half-second one: this is how long
        // the keys stay taken from another program after the player leaves the game, and how long the
        // boxes linger. The Haul Overlay looked once a second.
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    /// <summary>The settings in use now. Change them with <see cref="Apply"/>.</summary>
    public OverlaySettings Settings { get; private set; }

    /// <summary>One box's window by its name in <see cref="OverlaySettings.Boxes"/>; null in a run without windows.</summary>
    public OverlayWindow? Box(string name) => boxes.GetValueOrDefault(name);

    /// <summary>
    /// A box's key or its controller button was pressed in the game, or its own "hide" button: the
    /// box's name. Showing or hiding it has already been done when this is raised.
    /// </summary>
    public event Action<string>? KeyPressed;

    /// <summary>
    /// Raised four times a second while the overlay has a window, after it and its keys have been
    /// brought in step with the game: the clock for whatever a studio shows only for a while.
    /// </summary>
    public event Action? Ticked;

    /// <summary>Saves changed settings and takes them into use at once: the overlay's look, place and visibility, and the keys.</summary>
    public void Apply(OverlaySettings changed)
    {
        Settings = changed.Cleaned();
        file.Set(Section, Settings);
        Use();
    }

    /// <summary>
    /// The very first run with the overlay: nothing is saved under "overlay" yet, so the colour and
    /// the background the player chose in the Elite Haul Overlay are copied in (its file is only
    /// read) and saved. Saving makes this happen once: what the player changes in Studio afterwards
    /// stays. Its keys, place and size are not copied: Studio's boxes have their own. That it copied
    /// is saved with the settings and told by the settings route until the player has read it.
    /// </summary>
    /// <param name="oldSettingsFile">The Elite Haul Overlay's settings file. Without one, Studio's own defaults are saved.</param>
    public void CopyOldSettings(string oldSettingsFile)
    {
        if (file.Has(Section)) return;
        if (Market.OldHaulSettings.Read(oldSettingsFile)?.Overlay is { } old)
        {
            Settings = (old with { CopiedFrom = oldSettingsFile }).Cleaned();
            Log.Info($"First run: the overlay's colour and background copied from {oldSettingsFile}");
        }
        try { file.Set(Section, Settings); }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error("The overlay settings could not be saved, so the first run's copy will be made again next time", problem);
        }
        Use();
    }

    /// <summary>Gives the keys back and stops looking at the game and the controllers. The window itself goes when the program ends.</summary>
    public void Dispose()
    {
        timer?.Stop();
        hotkeys?.Dispose();
        controller.Dispose();
        game.Changed -= OnGameChanged;
        foreach (OverlayWindow box in boxes.Values) game.Changed -= box.Redraw;
    }

    /// <summary>
    /// One press of a box's key or of its controller button: the box is shown, or hidden, and the
    /// studios are told. (Anything else a studio's own code presses goes on to the studios as it is.)
    /// </summary>
    internal void Press(string action)
    {
        if (OverlaySettings.Boxes.Contains(action))
        {
            BoxLook box = Settings.Box(action);
            Apply(Settings.With(action, box with { On = !box.On }));
        }
        KeyPressed?.Invoke(action);
    }

    /// <summary>A click on one of a box's own buttons, or on a "copy:" button of its content. Every other tag is the studio's.</summary>
    void Click(string name, string tag)
    {
        BoxLook box = Settings.Box(name);
        if (tag == "box:hide") Press(name);
        else if (tag is "box:smaller" or "box:bigger") Apply(Settings.With(name, box with { Scale = box.Scale + (tag == "box:bigger" ? SizeStep : -SizeStep) }));
        else if (tag.StartsWith("copy:", StringComparison.Ordinal))
        {
            string text = tag["copy:".Length..];
            try
            {
                Clipboard.SetDataObject(text, true);
                boxes[name].Say($"✔ {text} copied");
            }
            catch (ExternalException problem)   // another program has the clipboard open just now
            {
                Log.Error($"Could not copy to the clipboard: {text}", problem);
            }
        }
    }

    /// <summary>Takes <see cref="Settings"/> into use: every box's look, place and visibility, the keys and the controller buttons. Pages are told.</summary>
    void Use()
    {
        bool inGame = Boxes(OverlayWindow.InGame);
        hotkeys?.Sync(inGame, Settings);
        controller.Watch(inGame, Settings);
        Tell();
    }

    /// <summary>
    /// Brings every box in step with the settings and the game: shown or hidden, and taking the mouse
    /// while it is shown in the game. Over Studio's own window a box only shows, so that its place
    /// and size can be seen while they are being set.
    /// </summary>
    bool Boxes(bool inGame)
    {
        foreach ((string name, OverlayWindow box) in boxes)
        {
            box.TakeClicks(Settings.Box(name).On && inGame && name != OverlaySettings.ShowOnly);
            box.Apply(Settings);
        }
        return boxes.Count > 0 && inGame;
    }

    /// <summary>
    /// The "overlay" live event: what the settings route answers, sent to every listening page when
    /// something in it has changed. That is how a page hears of the show/hide key pressed in the
    /// game, and of a key Windows would not give.
    /// </summary>
    void Tell()
    {
        told = (game.DisplayMode, game.Bindings);
        web.Send("overlay", Answer());
    }

    // The answer also says two things read from the game's own files. They change rarely; only then is it sent again.
    void OnGameChanged()
    {
        if (told != (game.DisplayMode, game.Bindings)) Tell();
    }

    void Tick()
    {
        try
        {
            // "In the game" and not "Elite in front": a box that is shown is itself the window in front,
            // and the keys that hide the boxes must stay taken.
            bool inGame = Boxes(OverlayWindow.InGame);
            if (hotkeys!.Sync(inGame, Settings)) Tell();   // the keys were just taken, or refused
            controller.Watch(inGame, Settings);
            Ticked?.Invoke();
            tickProblem = null;
        }
        catch (Exception problem)
        {
            // This runs four times a second, so a problem that keeps coming back is logged once, not every time.
            if (problem.Message != tickProblem) Log.Error("Keeping the overlay and its keys in step with the game failed", problem);
            tickProblem = problem.Message;
        }
    }

    /// <summary>
    /// What every route answers, and what the "overlay" event carries: the settings; per hotkey whether
    /// Studio can use the key text (valid), whether Windows gave the key to Studio the last time Elite
    /// came to the front (registered; null while not tried with this key), what the game itself does
    /// on that key (gameActions), and the action's controller button: the setting that holds it
    /// (padSetting) and how to show it to the player (pad; empty for none); the game's display mode,
    /// because no overlay can show over a game in exclusive "fullscreen"; and, from the first run until
    /// POST /api/overlay/old-tool-read, the Elite Haul Overlay's settings file the look was copied
    /// from (copiedFrom).
    /// </summary>
    object Answer() => new
    {
        settings = Settings,
        copiedFrom = Settings.CopiedFrom,
        keys = Settings.Hotkeys().Zip(Settings.PadButtons(), (hotkey, pad) => new
        {
            action = hotkey.Action,
            setting = hotkey.Setting,
            key = hotkey.Key,
            valid = Hotkeys.Parse(hotkey.Key) is not null,
            registered = hotkeys?.Registered(hotkey.Action, hotkey.Key),
            gameActions = Hotkeys.GameUses(game.Bindings, hotkey.Key),
            padSetting = pad.Setting,
            pad = PadChord.Parse(pad.Buttons)?.Label(WindowsPads.NameOf) ?? "",
        }),
        displayMode = game.DisplayMode.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// The settings with the named ones replaced, as a page names them ("hotNext", "scale"); the others
    /// stay as they are, and so does the first run's marker. JsonException for a value of the wrong kind.
    /// </summary>
    OverlaySettings With(JsonObject given)
    {
        JsonObject all = JsonSerializer.SerializeToNode(Settings, StudioSettings.Json)!.AsObject();
        foreach ((string name, JsonNode? value) in given)
            if (all.ContainsKey(name)) all[name] = value?.DeepClone();
        return all.Deserialize<OverlaySettings>(StudioSettings.Json)! with { CopiedFrom = Settings.CopiedFrom };
    }

    /// <summary>POST: the body names the settings to change. A page's wait for a controller button ends with it: the page has moved on.</summary>
    WebResponse Change(WebRequest request)
    {
        OverlaySettings changed;
        try
        {
            if (JsonNode.Parse(request.Body) is not JsonObject given) throw new JsonException("Send the settings to change as one JSON object.");
            changed = With(given);
        }
        catch (JsonException problem)
        {
            return WebResponse.Error("These overlay settings could not be read: " + problem.Message, 400);
        }
        Apply(changed);
        controller.StopListening();   // after the change, so that the page that waited is answered with the settings as they now are
        return WebResponse.Json(Answer());
    }

    /// <summary>
    /// POST /api/overlay/listen with {"setting":"padNext"}: waits for the player to press a button on a
    /// game controller, or to hold one and press another, and keeps that as the action's controller
    /// button. The app has to do the listening: a page cannot see a controller unless its own window
    /// is in front and a button was pressed there first. Answers like the settings route once a button
    /// is kept, and also when the wait was ended by a newer one or by a change to the settings. With
    /// no button within <see cref="Controller.ListenMs"/>, or no controller, the answer is an error
    /// whose text tells the player what to do. (Its status is 404, "none found", and never 408,
    /// "timed out": a browser answers that one by quietly sending the request again, and the page
    /// would go on listening for another ten seconds, five times over.)
    /// </summary>
    async Task<WebResponse> Listen(WebRequest request)
    {
        string setting = request.Read<ListenBody>().Setting;
        if (!Settings.PadButtons().Any(pad => pad.Setting == setting)) return WebResponse.Error("No controller button is kept under that name.", 400);
        Heard heard = await controller.Listen();
        if (heard.Buttons is not null) Apply(With(new JsonObject { [setting] = heard.Buttons }));
        return heard.Said is null ? WebResponse.Json(Answer()) : WebResponse.Error(heard.Said, 404);
    }
}
