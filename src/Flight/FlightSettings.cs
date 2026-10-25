using System.Text.RegularExpressions;

namespace EliteStudio.Flight;

/// <summary>One curve for each of the three turning controls.</summary>
public sealed record FlightCurves(StickCurve Pitch, StickCurve Roll, StickCurve Yaw)
{
    public FlightCurves() : this(new StickCurve(), new StickCurve(), new StickCurve()) { }

    public StickCurve Of(string control) => control switch { "roll" => Roll, "yaw" => Yaw, _ => Pitch };

    internal FlightCurves Cleaned() => new((Pitch ?? new()).Cleaned(), (Roll ?? new()).Cleaned(), (Yaw ?? new()).Cleaned());
}

/// <summary>How the player has done at one exercise of the trainer: sets flown, the most attempts that came off in one set, and whether a set was passed (three of five) and when.</summary>
public sealed record ExerciseRecord(int Sets = 0, int Best = 0, bool Passed = false, string? PassedOn = null);

/// <summary>
/// The "flight" section of settings.json: Studio Flight's curves, the trainer's progress, Chase the dot's
/// best scores and the flight overlay's own switch. The overlay box itself (key, place, size) is the overlay's (see
/// <see cref="Overlay.OverlaySettings"/>, box "flight").
/// </summary>
public sealed record FlightSettings
{
    /// <summary>The curves for flying with Flight Assist on; null until the player saves some (until then Gremlin's, read from its profile).</summary>
    public FlightCurves? FaOn { get; init; }
    /// <summary>The curves for Flight Assist off, the ones the trainer flies with; null until the player saves some.</summary>
    public FlightCurves? FaOff { get; init; }
    /// <summary>The ship the trainer flies like, by coriolis-data's name ("cobra_mk_iii"); empty for the ship the player is flying.</summary>
    public string Ship { get; init; } = "";
    /// <summary>
    /// "Overlay on/off" on Studio Flight's Options page. On: the flight overlay shows by itself while Flight Assist is off, and
    /// goes again when it comes back on. Off (until the player turns it on): it never shows, not with its key nor from a page
    /// (see <see cref="Overlay.OverlayHost.MayShow"/>).
    /// </summary>
    public bool ShowWhenFaOff { get; init; }
    /// <summary>
    /// Switch the curves with the game's Flight Assist: the game gets the Flight Assist on curves, and the off ones while Flight
    /// Assist is off. Off (until the player turns it on, on Options): it always gets the on curves. Separate from
    /// <see cref="ShowWhenFaOff"/>, which is only the overlay.
    /// It is for whoever shapes the stick: Studio's own shaper (<see cref="ShaperOn"/>) and the Gremlin profile Studio writes.
    /// </summary>
    public bool SwitchCurvesWithFa { get; init; }
    /// <summary>
    /// Studio shapes the stick itself, with Gimbalyn, its own program (instead of Joystick Gremlin): it reads the real stick, applies
    /// the curves and feeds vJoy. Off until the player turns it on (on Options), so Studio never takes vJoy unasked.
    /// </summary>
    public bool ShaperOn { get; init; }
    /// <summary>What the trainer reads: "stick", the real stick with the FA off curves applied by Studio; "game", what the game itself gets (Gremlin's vJoy).</summary>
    public string Input { get; init; } = "stick";
    /// <summary>
    /// The player's corrections of which of Windows' axis letters a control of a device is: "046D:C215 U" → "Z"
    /// (the device, the game's letter for the axis; Windows' letter, see <see cref="StickState"/>).
    /// </summary>
    public Dictionary<string, string> AxisFix { get; init; } = [];
    /// <summary>The real stick behind each control where Studio cannot tell it from Gremlin: "pitch" → "046D:C215 Y".</summary>
    public Dictionary<string, string> Sources { get; init; } = [];
    /// <summary>Keys the trainer turns the ship with when the game has none for that: "pitchUp" → "Key_Numpad_5".</summary>
    public Dictionary<string, string> TrainerKeys { get; init; } = new(DefaultTrainerKeys);
    /// <summary>The trainer's progress by exercise ("chase-1", "tunnel-2", "track-3"); the "spin-N" of the exercise chase replaced are dropped.</summary>
    public Dictionary<string, ExerciseRecord> Progress { get; init; } = [];
    /// <summary>
    /// Chase the dot's best scores, the share of a round on the dot in percent, by game: "dot2-medium-faOff" is the two-axis
    /// game at Medium with the Flight Assist off curves (see <see cref="IsDotGame"/>). Kept by POST /api/flight/score only. A
    /// score is never null once cleaned: null is allowed only so one typed into the file by hand is left out, not the section.
    /// </summary>
    public Dictionary<string, double?> Scores { get; init; } = [];
    /// <summary>
    /// Not a setting, a marker kept with them: the flight overlay is up because Flight Assist went off, not because the
    /// player showed it, so it goes again when Flight Assist comes back on, also after Studio was restarted in between.
    /// </summary>
    public bool ShownForFaOff { get; init; }
    /// <summary>The folder the player picked for vJoy by hand, when it is not where Windows says; "" for none.</summary>
    public string VJoyFolder { get; init; } = "";

    internal static readonly Dictionary<string, string> DefaultTrainerKeys = new()
    {
        ["pitchUp"] = "Key_Numpad_5", ["pitchDown"] = "Key_Numpad_8", ["yawLeft"] = "Key_Numpad_4",
        ["yawRight"] = "Key_Numpad_6", ["rollLeft"] = "Key_Numpad_7", ["rollRight"] = "Key_Numpad_9",
    };

    static readonly Regex DeviceAxis = new(@"^[0-9A-F]{4}:[0-9A-F]{4}(#\d{1,2})? (X|Y|Z|RX|RY|RZ|U|V|[LR]Stick[XY])$");
    static readonly Regex Exercise = new(@"^(chase|tunnel|track)-[1-3]$");
    static readonly Regex DotGame = new(@"^dot2-(easy|medium|hard)-(faOff|faOn)$");

    /// <summary>A game of Chase the dot: "dot2-", its level, and the curve set it was played with ("dot2-hard-faOn").</summary>
    internal static bool IsDotGame(string? id) => DotGame.IsMatch(id ?? "");

    /// <summary>Every value made usable: the file can be edited by hand, and a page may send anything.</summary>
    internal FlightSettings Cleaned() => this with
    {
        FaOn = FaOn?.Cleaned(),
        FaOff = FaOff?.Cleaned(),
        Ship = Regex.IsMatch(Ship ?? "", "^[a-z0-9_]{1,40}$") ? Ship! : "",
        Input = Input == "game" ? "game" : "stick",
        // JSON can put null where a value belongs (a hand-edited file): such an entry is left out.
        AxisFix = (AxisFix ?? []).Where(p => p.Value is not null && DeviceAxis.IsMatch(p.Key) && WindowsSticks.Letters.Contains(p.Value)).ToDictionary(),
        Sources = (Sources ?? []).Where(p => p.Value is not null && p.Key is "pitch" or "roll" or "yaw" && DeviceAxis.IsMatch(p.Value)).ToDictionary(),
        TrainerKeys = (TrainerKeys ?? []).Where(p => p.Value is not null && DefaultTrainerKeys.ContainsKey(p.Key) && KeyNames.ScanCode(p.Value) is not null).ToDictionary(),
        Progress = (Progress ?? []).Where(p => Exercise.IsMatch(p.Key) && p.Value is not null)
            .ToDictionary(p => p.Key, p => p.Value with { Sets = Math.Max(0, p.Value.Sets), Best = Math.Clamp(p.Value.Best, 0, 5) }),
        Scores = (Scores ?? []).Where(p => IsDotGame(p.Key) && p.Value is { } v && double.IsFinite(v)).ToDictionary(p => p.Key, p => (double?)Math.Clamp(p.Value!.Value, 0, 100)),
        VJoyFolder = FullFolder(VJoyFolder) ? VJoyFolder!.TrimEnd('\\') : "",
    };

    /// <summary>
    /// A folder by its whole path on a drive letter (never a share: see <see cref="VJoyFinder.LocalFolder"/>, which also
    /// turns away mapped network drives when the folder is used), of a length Windows takes, with no character it refuses.
    /// </summary>
    static bool FullFolder(string? path) =>
        path is { Length: > 3 and < 248 } && path.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(path)
        && !path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith("//", StringComparison.Ordinal);
}
