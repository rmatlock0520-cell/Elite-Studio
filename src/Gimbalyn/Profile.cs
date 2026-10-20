using System.Text.RegularExpressions;
using EliteStudio.Flight;

namespace EliteStudio.Gimbalyn;

// GIMBALYN: Studio's own stick shaper. It reads a real stick, reshapes each axis with a curve (dead zones each side,
// travel, saturation, bend: the same sum as Studio Flight's trainer), and feeds the result to vJoy, the virtual joystick a
// game is bound to. It replaces Joystick Gremlin for that one job, and knows nothing of any game: which curves, which
// axes and when to switch between sets of curves (modes) come from its profile, which Studio writes. See
// ED Studio\Plans\Stick shaper plan.md.

/// <summary>Where one axis of a real stick goes on vJoy.</summary>
/// <param name="Device">The real stick as Studio names it: "046D:C215", or "046D:C215#2" for the second of two alike.</param>
/// <param name="Axis">Windows' letter for its axis: X, Y, Z, R (the twist), U or V.</param>
/// <param name="Target">The vJoy axis it feeds, by the game's letters: X, Y, Z, RX, RY, RZ, U or V.</param>
/// <param name="Invert">Turn it round on the way (most games invert on their side instead).</param>
public sealed record AxisRoute(string Device, string Axis, string Target, bool Invert = false);

/// <summary>What Gimbalyn does: its routes, its modes (a curve per vJoy axis in each), the mode it starts in, and an optional key that cycles the modes.</summary>
public sealed record GimbalynProfile
{
    /// <summary>The vJoy device fed (1 to 16).</summary>
    public int Device { get; init; } = 1;
    /// <summary>The axes passed on, one route per vJoy axis.</summary>
    public List<AxisRoute> Routes { get; init; } = [];
    /// <summary>The modes by name, each a curve per vJoy axis; a vJoy axis with no curve in a mode passes the stick on as it is.</summary>
    public Dictionary<string, Dictionary<string, StickCurve>> Modes { get; init; } = [];
    /// <summary>The mode in use at the start.</summary>
    public string Mode { get; init; } = "";
    /// <summary>A key (by the game's name, "Key_Z") that moves on to the next mode each time it is pressed; null for none.</summary>
    public string? ModeKey { get; init; }
    /// <summary>The program that must be in front for the mode key to count ("EliteDangerous64"); null: whichever is.</summary>
    public string? ModeKeyOnlyWith { get; init; }
    /// <summary>vJoy's folder when it is not where Windows lists it ("" for none); only a folder on this PC's own disks is used.</summary>
    public string VJoyFolder { get; init; } = "";

    /// <summary>The vJoy axes and Windows' stick axes by letter, in the order the readers use.</summary>
    public static readonly string[] StickAxes = ["X", "Y", "Z", "R", "U", "V"];
    public static readonly string[] VJoyAxes = ["X", "Y", "Z", "RX", "RY", "RZ", "U", "V"];

    static readonly Regex DeviceId = new(@"^[0-9A-F]{4}:[0-9A-F]{4}(#\d{1,2})?$");
    static readonly Regex ModeName = new(@"^[A-Za-z0-9][A-Za-z0-9 _\-]{0,39}$");
    static readonly Regex Program = new(@"^[A-Za-z0-9_.\-]{1,64}$");

    /// <summary>Everything made usable: the file can be edited by hand, and a page may send anything.</summary>
    public GimbalynProfile Cleaned()
    {
        var routes = new List<AxisRoute>();
        foreach (AxisRoute? route in Routes ?? [])
            if (route is not null && route.Device is not null && DeviceId.IsMatch(route.Device.ToUpperInvariant()) && StickAxes.Contains(route.Axis)
                && VJoyAxes.Contains(route.Target) && routes.All(r => r.Target != route.Target))   // one route per vJoy axis: the first wins
                routes.Add(route with { Device = route.Device.ToUpperInvariant() });
        var modes = new Dictionary<string, Dictionary<string, StickCurve>>();
        foreach ((string name, Dictionary<string, StickCurve>? curves) in Modes ?? [])
            if (name is not null && ModeName.IsMatch(name) && modes.Count < 8)
                modes[name] = (curves ?? []).Where(c => VJoyAxes.Contains(c.Key) && c.Value is not null).ToDictionary(c => c.Key, c => c.Value.Cleaned());
        string mode = Mode is not null && modes.ContainsKey(Mode) ? Mode : modes.Keys.FirstOrDefault() ?? "";
        return this with
        {
            Device = Math.Clamp(Device, 1, 16),
            Routes = routes,
            Modes = modes,
            Mode = mode,
            ModeKey = KeyNames.ScanCode(ModeKey) is not null ? ModeKey : null,
            ModeKeyOnlyWith = ModeKeyOnlyWith is not null && Program.IsMatch(ModeKeyOnlyWith) ? ModeKeyOnlyWith : null,
            VJoyFolder = VJoyFinder.LocalFolder(VJoyFolder) ? VJoyFolder : "",
        };
    }
}
