using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EliteStudio.Core;

namespace EliteStudio.Flight;

/// <summary>A key or a button as the game names it: the device ("Keyboard", "LogitechExtreme3DPro", "vJoy") and the key ("Key_W", "Joy_4", "Joy_POV1Up").</summary>
public sealed record FlightKey(string Device, string Key);

/// <summary>What sets off one flight action: a key or button, and any keys that must be held with it.</summary>
public sealed record FlightButton(FlightKey Press, IReadOnlyList<FlightKey> Hold);

/// <summary>A stick axis bound to a flight control, with the game's own two settings for it.</summary>
/// <param name="Axis">The game's name for the axis without "Joy_" and "Axis": "X", "Y", "Z", "RX", "RY", "RZ", "U" (first slider), "V".</param>
/// <param name="Deadzone">The game's own dead zone for it, 0 to 1.</param>
public sealed record FlightAxis(string Device, string Axis, bool Inverted, double Deadzone);

/// <summary>
/// The ship controls of the player's binding preset that flying with Flight Assist off is made of:
/// the six axes and their "alternate" set, the thruster, throttle and rotation buttons, boost, the
/// Flight Assist switch, the four defence buttons, and how yaw turns into roll. Read from the same
/// files as <see cref="KeyBindings"/> (see there), for the ship's preset.
/// </summary>
/// <param name="Axes">By Studio's names: pitch, roll, yaw, throttle, lateral, vertical, ahead, and the same with "Alt" for the alternate set. An axis that is not bound is missing.</param>
/// <param name="Buttons">By Studio's names (see <see cref="ButtonActions"/>); each with what the game has as primary and secondary. One that is not bound is missing.</param>
/// <param name="Toggles">The game's "toggle" switch for the actions that have one, by the same names: on means one press switches, off means hold.</param>
/// <param name="Values">The game's settings that are no key, by its own names ("YawToRollMode_FAOff").</param>
/// <param name="Devices">Every device named in the bindings, with its maker's and product's numbers as Windows shows them ("046D:C215"); several when the game counts other models as the same.</param>
public sealed record FlightBinds(
    string Preset, string File,
    IReadOnlyDictionary<string, FlightAxis> Axes,
    IReadOnlyDictionary<string, IReadOnlyList<FlightButton>> Buttons,
    IReadOnlyDictionary<string, bool> Toggles,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Devices)
{
    /// <summary>Studio's name for each axis, and the game's.</summary>
    public static readonly IReadOnlyList<(string Name, string Action)> AxisActions =
    [
        ("pitch", "PitchAxisRaw"), ("roll", "RollAxisRaw"), ("yaw", "YawAxisRaw"), ("throttle", "ThrottleAxis"),
        ("lateral", "LateralThrustRaw"), ("vertical", "VerticalThrustRaw"), ("ahead", "AheadThrust"),
        ("pitchAlt", "PitchAxisAlternate"), ("rollAlt", "RollAxisAlternate"), ("yawAlt", "YawAxisAlternate"),
        ("lateralAlt", "LateralThrustAlternate"), ("verticalAlt", "VerticalThrustAlternate"),
    ];

    /// <summary>Studio's name for each button action, and the game's.</summary>
    public static readonly IReadOnlyList<(string Name, string Action)> ButtonActions =
    [
        ("pitchUp", "PitchUpButton"), ("pitchDown", "PitchDownButton"), ("rollLeft", "RollLeftButton"), ("rollRight", "RollRightButton"),
        ("yawLeft", "YawLeftButton"), ("yawRight", "YawRightButton"),
        ("left", "LeftThrustButton"), ("right", "RightThrustButton"), ("up", "UpThrustButton"), ("down", "DownThrustButton"),
        ("forward", "ForwardThrustButton"), ("backward", "BackwardThrustButton"),
        ("throttleUp", "ForwardKey"), ("throttleDown", "BackwardKey"),
        ("speedMinus100", "SetSpeedMinus100"), ("speedMinus75", "SetSpeedMinus75"), ("speedMinus50", "SetSpeedMinus50"), ("speedMinus25", "SetSpeedMinus25"),
        ("speedZero", "SetSpeedZero"), ("speed25", "SetSpeed25"), ("speed50", "SetSpeed50"), ("speed75", "SetSpeed75"), ("speed100", "SetSpeed100"),
        ("boost", "UseBoostJuice"), ("fa", "ToggleFlightAssist"), ("alternate", "UseAlternateFlightValuesToggle"),
        ("rotationCorrection", "DisableRotationCorrectToggle"),
        ("chaff", "FireChaffLauncher"), ("ecm", "ChargeECM"), ("heat", "DeployHeatSink"), ("shield", "UseShieldCell"),
    ];

    /// <summary>The game's settings kept in <see cref="Values"/>.</summary>
    static readonly string[] ValueNames = ["YawToRollMode", "YawToRollMode_FAOff", "YawToRollSensitivity", "ThrottleRange", "ThrottleIncrement"];

    /// <summary>
    /// Devices the game names whose numbers Studio knows without the game's folder (the game's own list,
    /// ControlSchemes\DeviceMappings.xml, has them all): the ones flown with most, and vJoy, which
    /// Joystick Gremlin feeds.
    /// </summary>
    static readonly Dictionary<string, string> KnownDevices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LogitechExtreme3DPro"] = "046D:C215", ["vJoy"] = "1234:BEAD", ["ThrustMasterHOTAS4"] = "044F:B67B",
        ["T16000M"] = "044F:B10A", ["T16000MTHROTTLE"] = "044F:B687", ["ThrustMasterWarthogJoystick"] = "044F:0402",
        ["ThrustMasterWarthogThrottle"] = "044F:0404", ["SaitekX52"] = "06A3:075C", ["SaitekX52Pro"] = "06A3:0762",
        ["SaitekX56Joystick"] = "0738:2221", ["SaitekX56Throttle"] = "0738:A221",
    };

    /// <summary>
    /// Reads the ship controls of the active preset. Null when the game has not written its list of
    /// presets yet, or the preset's file cannot be found. A file that is locked or half-written this
    /// moment throws (IOException, UnauthorizedAccessException, XmlException): the caller keeps what it had.
    /// </summary>
    /// <param name="gameDir">The game's install folder, for a preset used as it came and for its list of devices; null when not known.</param>
    public static FlightBinds? Load(string bindingsDir, Func<string?>? gameDir)
    {
        FileInfo[] files = KeyBindings.Files(bindingsDir);
        if (KeyBindings.StartPresets(files) is not { Length: > 0 } presets) return null;
        // Since Odyssey the list has one line each for general, ship, SRV and on-foot controls.
        string preset = presets.Length >= 2 ? presets[1] : presets[0];
        var game = new Lazy<string?>(() => gameDir?.Invoke());
        if (KeyBindings.FileOf(files, preset, game, []) is not { } file) return null;
        XElement root;
        using (FileStream stream = KeyBindings.Open(file)) root = XDocument.Load(stream).Root ?? new XElement("Root");
        return Read(preset, file, root, game.Value is { } folder ? Path.Combine(folder, "ControlSchemes", "DeviceMappings.xml") : null);
    }

    /// <summary>The ship controls in one binds file's root element. Kept apart from the files so tests can hand in their own.</summary>
    /// <param name="deviceList">The game's DeviceMappings.xml, when there is one; read only if it is there and readable.</param>
    internal static FlightBinds Read(string preset, string file, XElement root, string? deviceList)
    {
        var axes = new Dictionary<string, FlightAxis>();
        foreach ((string name, string action) in AxisActions)
        {
            XElement? node = root.Element(action);
            XElement? binding = node?.Element("Binding");
            if (node is null || binding is null || Device(binding) is not { } device) continue;
            string key = (string?)binding.Attribute("Key") ?? "";
            Match axis = Regex.Match(key, "^(?:Neg_|Pos_)?Joy_([A-Z]+)Axis$");
            if (!axis.Success) continue;   // a mouse axis, or something new
            axes[name] = new FlightAxis(device, axis.Groups[1].Value, Number(node.Element("Inverted")) != 0, Number(node.Element("Deadzone")));
        }

        var buttons = new Dictionary<string, IReadOnlyList<FlightButton>>();
        var toggles = new Dictionary<string, bool>();
        foreach ((string name, string action) in ButtonActions)
        {
            if (root.Element(action) is not { } node) continue;
            var bound = new List<FlightButton>();
            foreach (XElement way in node.Elements().Where(e => e.Name.LocalName is "Primary" or "Secondary"))
            {
                if (Device(way) is not { } device) continue;
                IReadOnlyList<FlightKey> hold = [.. way.Elements("Modifier").Select(held => Device(held) is { } on ? new FlightKey(on, (string?)held.Attribute("Key") ?? "") : null).OfType<FlightKey>()];
                bound.Add(new FlightButton(new FlightKey(device, (string?)way.Attribute("Key") ?? ""), hold));
            }
            if (bound.Count > 0) buttons[name] = bound;
            if (node.Element("ToggleOn") is { } toggle) toggles[name] = Number(toggle) != 0;
        }

        var values = new Dictionary<string, string>();
        foreach (string name in ValueNames)
            if ((string?)root.Element(name)?.Attribute("Value") is { Length: > 0 } value) values[name] = value;

        IEnumerable<string> used = axes.Values.Select(a => a.Device)
            .Concat(buttons.Values.SelectMany(list => list).SelectMany(b => b.Hold.Append(b.Press)).Select(k => k.Device))
            .Where(d => d is not "Keyboard" and not "Mouse").Distinct(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> listed = DeviceList(deviceList);
        var devices = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string device in used) devices[device] = NumbersOf(device, listed);
        return new FlightBinds(preset, file, axes, buttons, toggles, values, devices);
    }

    /// <summary>
    /// A device's maker's and product's numbers: from the game's own list, from Studio's short list, or
    /// from the name itself, which is those numbers run together ("231D0200") for a device the game has
    /// no name for. Empty when none of the three knows it.
    /// </summary>
    internal static IReadOnlyList<string> NumbersOf(string device, IReadOnlyDictionary<string, List<string>> listed)
    {
        if (listed.TryGetValue(device, out List<string>? found) && found.Count > 0) return found;
        if (KnownDevices.TryGetValue(device, out string? known)) return [known];
        Match hex = Regex.Match(device, "^([0-9A-Fa-f]{4})([0-9A-Fa-f]{4})$");
        return hex.Success ? [$"{hex.Groups[1].Value.ToUpperInvariant()}:{hex.Groups[2].Value.ToUpperInvariant()}"] : [];
    }

    /// <summary>
    /// The game's list of devices it has names for (ControlSchemes\DeviceMappings.xml): each name with
    /// its maker's and product's numbers and those of the models it counts as the same ("Alternative").
    /// Empty when there is no such file or it cannot be read.
    /// </summary>
    internal static Dictionary<string, List<string>> DeviceList(string? file)
    {
        var list = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (file is null || !System.IO.File.Exists(file)) return list;
        try
        {
            XElement? root;
            using (FileStream stream = KeyBindings.Open(file)) root = XDocument.Load(stream).Root;
            foreach (XElement device in root?.Elements() ?? [])
            {
                var numbers = new List<string>();
                foreach (XElement entry in device.Elements("Alternative").Prepend(device))
                {
                    string vid = ((string?)entry.Element("VID") ?? "").Trim(), pid = ((string?)entry.Element("PID") ?? "").Trim();
                    if (Regex.IsMatch(vid, "^[0-9A-Fa-f]{4}$") && Regex.IsMatch(pid, "^[0-9A-Fa-f]{4}$"))
                        numbers.Add($"{vid.ToUpperInvariant()}:{pid.ToUpperInvariant()}");
                }
                list[device.Name.LocalName] = numbers;
            }
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            App.Log.Error($"The game's list of devices could not be read: {file}", problem);
        }
        return list;
    }

    /// <summary>The device of a binding, or null for none ("{NoDevice}", or a key left empty).</summary>
    static string? Device(XElement binding)
    {
        string device = (string?)binding.Attribute("Device") ?? "";
        string key = (string?)binding.Attribute("Key") ?? "";
        return device.Length == 0 || device.StartsWith('{') || key.Length == 0 ? null : device;
    }

    /// <summary>The number in an element's Value; 0 when there is none.</summary>
    static double Number(XElement? node) =>
        double.TryParse((string?)node?.Attribute("Value"), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : 0;
}
