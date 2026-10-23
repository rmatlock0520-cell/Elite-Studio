namespace EliteStudio.Flight;

/// <summary>
/// An axis of a stick as Studio looks for it in a reading: the devices it may be (their maker's and
/// product's numbers, "046D:C215"), or failing those its name ("Logitech Extreme 3D", which is all
/// Joystick Gremlin keeps), and the axis by the game's letters ("RZ").
/// </summary>
public sealed record AxisRef(IReadOnlyList<string> Devices, string? Name, string Axis);

/// <summary>One control this moment: what the game gets, and for pitch, roll and yaw what the real stick behind it does (before Gremlin's curve). Null where it cannot be read.</summary>
/// <param name="Invert">The game's "invert" for the axis.</param>
/// <param name="RealUnknown">
/// Pitch, roll or yaw: the game gets a reading but Studio cannot read the real stick behind it (Gremlin's stick is not
/// plugged in, or the game reads vJoy and nothing tells Studio which stick feeds it), so Real is null and a page that wants
/// the real stick has only what the game gets. Detect on Stick setup tells Studio which stick it is.
/// </param>
public sealed record ControlValue(double? Game, double? Real, bool Invert, bool RealUnknown = false);

/// <summary>What the hands do this moment, in flight terms: the axes, and the flight actions whose key or button is held.</summary>
public sealed record ControlsFrame(long Time, IReadOnlyDictionary<string, ControlValue> Controls, IReadOnlyList<string> Pressed, IReadOnlyList<StickState> Sticks, IReadOnlyList<string> Keys);

/// <summary>
/// How the player's bindings turn a reading of the sticks and keys into flight controls: for each of
/// pitch, roll, yaw, throttle, lateral, vertical and ahead the axis the game reads (and for the first
/// three the real stick behind Gremlin's vJoy), and for each flight action the keys and buttons that
/// set it off. The trainer's own keys stand in for rotation buttons the game has none of.
/// </summary>
public sealed class FlightControls
{
    public static readonly string[] Axes = ["pitch", "roll", "yaw", "throttle", "lateral", "vertical", "ahead"];
    static readonly string[] Turning = ["pitch", "roll", "yaw"];

    readonly Dictionary<string, (AxisRef? Game, AxisRef? Real, bool Invert)> axes = [];
    readonly Dictionary<string, IReadOnlyList<FlightButton>> buttons = [];
    readonly IReadOnlyDictionary<string, string> axisFix;
    readonly IReadOnlyDictionary<string, IReadOnlyList<string>> devices;   // the numbers of each device the game names

    /// <summary>Studio's guess at which of Windows' letters each of the game's axis letters is (the player can correct it).</summary>
    static readonly Dictionary<string, string[]> Guess = new()
    {
        ["X"] = ["X"], ["Y"] = ["Y"], ["Z"] = ["Z"], ["RZ"] = ["R"], ["RX"] = ["U"], ["RY"] = ["V"], ["U"] = ["U", "Z"], ["V"] = ["V"],
    };

    /// <param name="binds">The game's bindings; null when they could not be read.</param>
    /// <param name="gremlin">The axes of Gremlin's profile; empty without one.</param>
    public FlightControls(FlightBinds? binds, IReadOnlyList<GremlinAxis> gremlin, FlightSettings settings)
    {
        axisFix = settings.AxisFix;
        devices = binds?.Devices ?? new Dictionary<string, IReadOnlyList<string>>();
        foreach (string name in Axes)
        {
            FlightAxis? bound = binds?.Axes.GetValueOrDefault(name);
            AxisRef? game = bound is null ? null : new AxisRef(binds!.Devices.GetValueOrDefault(bound.Device) ?? [], null, bound.Axis);
            AxisRef? real = null;
            if (Turning.Contains(name))
            {
                if (settings.Sources.GetValueOrDefault(name) is { } chosen && chosen.Split(' ') is [var device, var letter]) real = new AxisRef([device], null, letter);
                // The game reads vJoy: the real stick is the one Gremlin passes on to it. With no route there Studio does not know
                // the stick, and vJoy's value must never stand in for it: it is already curved, and the trainer would curve it again.
                else if (bound is not null && bound.Device.Equals("vJoy", StringComparison.OrdinalIgnoreCase))
                    real = Gremlin.Through(gremlin, bound.Axis) is { } through ? new AxisRef([], through.DeviceName, through.Axis) : null;
                else real = game;   // the game reads the stick itself
            }
            axes[name] = (game, real, bound?.Inverted ?? false);
        }
        ThrottleForwardOnly = binds?.Values.GetValueOrDefault("ThrottleRange")?.Contains("Forward", StringComparison.OrdinalIgnoreCase) == true;
        foreach ((string name, _) in FlightBinds.ButtonActions)
            if (binds?.Buttons.GetValueOrDefault(name) is { Count: > 0 } list) buttons[name] = list;
        foreach ((string name, string key) in settings.TrainerKeys)
            if (!buttons.ContainsKey(name)) buttons["trainer:" + name] = [new FlightButton(new FlightKey("Keyboard", key), [])];
    }

    /// <summary>Every keyboard key the bindings use for flight (and the trainer's keys): the keys to watch.</summary>
    public IEnumerable<string> Keys => buttons.Values.SelectMany(list => list).SelectMany(b => b.Hold.Append(b.Press))
        .Where(k => k.Device == "Keyboard").Select(k => k.Key).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The game's throttle axis runs from stop to full ahead only (Elite's "forward only" throttle range), not from full reverse.</summary>
    public bool ThrottleForwardOnly { get; }

    /// <summary>The flight actions that have a trainer key because the game has none ("pitchUp").</summary>
    public IEnumerable<string> TrainerOnly => buttons.Keys.Where(k => k.StartsWith("trainer:", StringComparison.Ordinal)).Select(k => k["trainer:".Length..]);

    /// <summary>The flight controls in one reading.</summary>
    public ControlsFrame Take(FlightFrame frame)
    {
        var controls = new Dictionary<string, ControlValue>();
        foreach ((string name, (AxisRef? game, AxisRef? real, bool invert)) in axes)
        {
            double? gets = Value(frame, game), stick = Value(frame, real);
            controls[name] = new ControlValue(gets, stick, invert, Turning.Contains(name) && gets is not null && stick is null);
        }
        var held = new HashSet<string>(frame.Keys, StringComparer.OrdinalIgnoreCase);
        List<string> pressed = [.. buttons.Where(b => b.Value.Any(way => Down(frame, held, way.Press) && way.Hold.All(h => Down(frame, held, h))))
            .Select(b => b.Key.StartsWith("trainer:", StringComparison.Ordinal) ? b.Key["trainer:".Length..] : b.Key).Distinct()];
        return new ControlsFrame(frame.Time, controls, pressed, frame.Sticks, frame.Keys);
    }

    /// <summary>
    /// The stick a reference means, among those in the reading: by its numbers (with "#2" and so on, that very one of two
    /// alike, or when its twin is gone and it is the only one left, that one; without, the first), else by its name,
    /// else the only real stick there is.
    /// </summary>
    internal static StickState? Find(IReadOnlyList<StickState> sticks, AxisRef where)
    {
        static string Plain(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        static bool Exactly(StickState stick, string device) => stick.Device.Equals(device, StringComparison.OrdinalIgnoreCase);
        static bool Alike(StickState stick, string device) => stick.Device.Split('#')[0].Equals(device.Split('#')[0], StringComparison.OrdinalIgnoreCase);
        StickState? byNumber = sticks.FirstOrDefault(s => where.Devices.Any(device => device.Contains('#') ? Exactly(s, device) : Alike(s, device)))
            ?? sticks.FirstOrDefault(s => where.Devices.Any(device => Alike(s, device)));
        if (byNumber is not null || where.Name is null) return byNumber;
        string name = Plain(where.Name);
        return sticks.FirstOrDefault(s => Plain(s.Name) is { Length: > 0 } other && (other.StartsWith(name, StringComparison.Ordinal) || name.StartsWith(other, StringComparison.Ordinal)))
            ?? (sticks.Count(s => !s.Device.StartsWith("1234:BEAD", StringComparison.OrdinalIgnoreCase)) == 1 ? sticks.First(s => !s.Device.StartsWith("1234:BEAD", StringComparison.OrdinalIgnoreCase)) : null);
    }

    /// <summary>
    /// The real stick behind pitch, roll or yaw among those in a reading: its device and Windows' letter for the axis, as Gimbalyn
    /// routes it. Null when Studio does not know the stick (see <see cref="ControlValue.RealUnknown"/>) or it is not there.
    /// </summary>
    internal (string Device, string Letter)? RealSource(string control, IReadOnlyList<StickState> sticks) =>
        axes.GetValueOrDefault(control).Real is { } real && Find(sticks, real) is { } stick && Letter(stick, real.Axis) is { } letter ? (stick.Device, letter) : null;

    /// <summary>Windows' letter for one of the game's axis letters on a stick: the player's correction, else Studio's guess among the axes the stick has.</summary>
    internal string? Letter(StickState stick, string axis) =>
        axisFix.GetValueOrDefault($"{stick.Device} {axis}") is { } fixedLetter ? fixedLetter
            : Guess.GetValueOrDefault(axis)?.FirstOrDefault(stick.Axes.ContainsKey);

    double? Value(FlightFrame frame, AxisRef? where)
    {
        if (where is null || Find(frame.Sticks, where) is not { } stick || Letter(stick, where.Axis) is not { } letter) return null;
        return stick.Axes.TryGetValue(letter, out double value) ? value : null;
    }

    /// <summary>Whether a key or button is held in a reading: a keyboard key by the keys held, a stick's button by its buttons ("Joy_4", "Joy_POV1Up").</summary>
    bool Down(FlightFrame frame, HashSet<string> keys, FlightKey key)
    {
        if (key.Device == "Keyboard") return keys.Contains(key.Key);
        int number = key.Key switch
        {
            "Joy_POV1Up" => 33, "Joy_POV1Right" => 34, "Joy_POV1Down" => 35, "Joy_POV1Left" => 36,
            _ when key.Key.StartsWith("Joy_", StringComparison.Ordinal) && int.TryParse(key.Key[4..], out int n) && n is >= 1 and <= 32 => n,
            _ => 0,
        };
        if (number == 0 || devices.GetValueOrDefault(key.Device) is not { Count: > 0 } numbers) return false;
        return frame.Sticks.Where(s => numbers.Contains(s.Device.Split('#')[0], StringComparer.OrdinalIgnoreCase))
            .Any(s => (s.Down >> (number - 1) & 1) != 0);
    }
}
