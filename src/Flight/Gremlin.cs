using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace EliteStudio.Flight;

/// <summary>
/// A stick response as Studio tunes it, and as it goes into a Joystick Gremlin "Response Curve": a
/// dead zone either side of the middle, how much of the stick's travel is used (SatX), the most it
/// gives at full travel (SatY), and how soft the middle is (Bend: 0 a straight line, 100 all cubic).
/// All five from 0 to 100. Gremlin first maps the travel past the dead zone to 0..1 (its "deadzone"
/// with low, center-low, center-high and high), then reads the curve's points at that value.
/// </summary>
public sealed record StickCurve(double DeadLow = 4, double DeadHigh = 4, double SatX = 92, double SatY = 68, double Bend = 20)
{
    /// <summary>What the curve gives for a stick position from -1 to 1: the same sum as Gremlin's, with the curve exact rather than in 21 points.</summary>
    public double At(double stick)
    {
        double dead = (stick < 0 ? DeadLow : DeadHigh) / 100, travel = Math.Max(SatX / 100, dead + 0.01);
        double t = Math.Clamp((Math.Abs(stick) - dead) / (travel - dead), 0, 1);
        return Math.Sign(stick) * Shape(t);
    }

    /// <summary>The curve after the dead zone, for a value from 0 to 1.</summary>
    internal double Shape(double t)
    {
        double k = Bend / 100;
        return SatY / 100 * ((1 - k) * t + k * t * t * t);
    }

    /// <summary>The same with every number inside its range, rounded to whole tenths: the file can be edited by hand, and a page may send anything.</summary>
    public StickCurve Cleaned() => new(Clean(DeadLow, 0, 40), Clean(DeadHigh, 0, 40), Clean(SatX, 50, 100), Clean(SatY, 10, 100), Clean(Bend, 0, 100));

    static double Clean(double value, double min, double max) => double.IsFinite(value) ? Math.Round(Math.Clamp(value, min, max), 1) : min;
}

/// <summary>One axis Joystick Gremlin passes on to vJoy, as its profile has it.</summary>
/// <param name="DeviceName">The real stick's name as Gremlin shows it ("Logitech Extreme 3D").</param>
/// <param name="Axis">The real stick's axis by the game's letters: "X", "Y", "RZ", "U"...</param>
/// <param name="VjoyAxis">The vJoy axis it goes to, the same way.</param>
/// <param name="Curve">Its response curve read back as Studio's five numbers; null when it has none or one Studio cannot read.</param>
/// <param name="CurveUnread">It has a response curve, but not one Studio can read exactly (so Curve is null).</param>
/// <param name="Studios">Its curve is one Studio put there (it carries Studio's label), not the player's own.</param>
public sealed record GremlinAxis(string Mode, string DeviceId, string DeviceName, string Axis, int VjoyDevice, string VjoyAxis, StickCurve? Curve, bool CurveUnread = false,
    bool Studios = false);

/// <summary>
/// Joystick Gremlin (github.com/WhiteMagic/JoystickGremlin, release 14 and later, whose profiles are
/// "version 14"): the profile it last opened, read for what it does with the stick's axes, and a copy
/// of it written with a second mode, "FA Off", that has the curves for flying with Flight Assist off.
/// The key that switches Flight Assist in the game switches Gremlin's mode too: Gremlin listens to the
/// keyboard without taking the key from the game. Studio only ever writes a new file beside the
/// player's own (or replaces a file it wrote itself, after a backup); Gremlin's own settings are only read.
/// </summary>
public static class Gremlin
{
    /// <summary>The mode Studio adds, a child of "Default", so it has every binding it does not change.</summary>
    public const string FaOffMode = "FA Off";
    const string MainMode = "Default";
    /// <summary>The label on the action Studio adds for the mode switch: how a file Studio wrote is told apart.</summary>
    const string SwitchLabel = "Elite Studio: Flight Assist mode";
    const string CurveLabel = "Elite Studio curve";
    /// <summary>DirectInput's GUID_SysKeyboard, under which Gremlin keeps keyboard inputs.</summary>
    const string KeyboardId = "6f1d2b61-d5a0-11cf-bfc7-444553540000";
    /// <summary>Gremlin's and vJoy's axis numbers, 1 to 8, by the game's letters.</summary>
    static readonly string[] AxisLetters = ["X", "Y", "Z", "RX", "RY", "RZ", "U", "V"];

    /// <summary>
    /// The profile Gremlin last opened, from its settings (configuration.json in "Joystick Gremlin" in
    /// the user folder). Null when Gremlin was never run here, or that file has gone.
    /// </summary>
    public static string? LastProfile(string? settingsFile = null)
    {
        settingsFile ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Joystick Gremlin", "configuration.json");
        try
        {
            if (!File.Exists(settingsFile)) return null;
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(settingsFile));
            string? profile = settings.RootElement.GetProperty("global").GetProperty("internal").GetProperty("last-profile").GetProperty("value").GetString();
            return profile is { Length: > 0 } && File.Exists(profile) ? profile : null;
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The axes of a profile that go to vJoy, in every mode. Throws for a file that is not a Gremlin profile Studio can read.</summary>
    public static IReadOnlyList<GremlinAxis> Axes(XDocument profile)
    {
        XElement root = Check(profile);
        Dictionary<string, XElement> library = Library(root);
        Dictionary<string, string> names = root.Element("devices")?.Elements("device")
            .ToDictionary(d => Text(d, "device-id").ToLowerInvariant(), d => Text(d, "device-name")) ?? [];
        var axes = new List<GremlinAxis>();
        foreach (XElement input in root.Element("inputs")?.Elements("input") ?? [])
        {
            if (Text(input, "input-type") != "axis" || !int.TryParse(Text(input, "input-id"), out int axis) || axis is < 1 or > 8) continue;
            string rootId = input.Element("action-configuration")?.Element("root-action")?.Value.Trim() ?? "";
            if (!library.TryGetValue(rootId, out XElement? top)) continue;
            List<XElement> steps = [.. top.Element("actions")?.Elements("action-id").Select(id => library.GetValueOrDefault(id.Value.Trim())).OfType<XElement>() ?? []];
            XElement? vjoy = steps.FirstOrDefault(a => (string?)a.Attribute("type") == "map-to-vjoy" && Property(a, "vjoy-input-type") == "axis");
            if (vjoy is null || !int.TryParse(Property(vjoy, "vjoy-input-id"), out int vjoyAxis) || vjoyAxis is < 1 or > 8) continue;
            int.TryParse(Property(vjoy, "vjoy-device-id"), out int vjoyDevice);
            XElement? curve = steps.FirstOrDefault(a => (string?)a.Attribute("type") == "response-curve");
            string device = Text(input, "device-id").ToLowerInvariant();
            axes.Add(new GremlinAxis(Text(input, "mode"), device, names.GetValueOrDefault(device, ""), AxisLetters[axis - 1], vjoyDevice,
                AxisLetters[vjoyAxis - 1], curve is null ? null : ReadCurve(curve), curve is not null && ReadCurve(curve) is null,
                curve is not null && Property(curve, "action-label") == CurveLabel));
        }
        return axes;
    }

    /// <summary>
    /// The axis of a profile's Default mode that feeds a vJoy axis (by the game's letters): the one going to vJoy device 1
    /// when there are several, as the game names only one "vJoy". Null when no axis goes there.
    /// </summary>
    public static GremlinAxis? Through(IReadOnlyList<GremlinAxis> axes, string vjoyAxis) =>
        axes.Where(g => g.Mode == MainMode && g.VjoyAxis == vjoyAxis).OrderBy(g => g.VjoyDevice == 1 ? 0 : 1).ThenBy(g => g.VjoyDevice).FirstOrDefault();

    /// <summary>
    /// A copy of a profile with Flight Assist off as a mode of its own. Every axis that goes to the vJoy axis the game reads
    /// for pitch, roll or yaw gets the "off" curve in "FA Off", and the "on" curve in Default when one is given (none: the
    /// player's own Default stays exactly as it was). Everything else stays as it was and goes on working in both modes.
    /// The key (or a button of a stick the profile already has) that switches Flight Assist in the game switches between
    /// the two modes. A profile Studio wrote before has its FA Off axes and its switch replaced, not added again; anything
    /// else the player put in FA Off stays.
    /// </summary>
    /// <param name="controls">For each of "pitch", "roll" and "yaw": the vJoy axis the game reads it from (the game's letters), and the two curves.</param>
    /// <param name="faSwitches">Every way the game's Flight Assist is bound, keyboard first; empty when it is not bound.</param>
    /// <param name="faToggles">The game's Flight Assist is a switch (true) or held (false).</param>
    /// <param name="switching">
    /// The curves follow the game's Flight Assist. False: no FA Off axes and no mode switch are written (and those Studio wrote
    /// before go), the game always gets the "on" curves in Default; a mode the player filled with their own bindings stays.
    /// </param>
    /// <returns>The new profile, and what to tell the player about anything that could not be done.</returns>
    public static (XDocument Profile, IReadOnlyList<string> Notes) WithFaOff(XDocument profile,
        IReadOnlyDictionary<string, (string VjoyAxis, StickCurve? On, StickCurve Off)> controls, IReadOnlyList<FlightButton> faSwitches, bool faToggles,
        bool switching = true)
    {
        var copy = new XDocument(profile);
        XElement root = Check(copy);
        XElement inputs = root.Element("inputs") ?? Add(root, new XElement("inputs"));
        XElement libraryNode = root.Element("library") ?? Add(root, new XElement("library"));
        var notes = new List<string>();
        Dictionary<string, XElement> library = Library(root);
        bool Labelled(XElement input, string label) => Steps(input, library).Any(step => Property(step, "action-label") == label);

        // What Studio added the last time goes first: its FA Off axes and its mode switch.
        foreach (XElement input in inputs.Elements("input").ToList())
            if ((Text(input, "mode") == FaOffMode && Labelled(input, CurveLabel)) || Labelled(input, SwitchLabel)) input.Remove();

        foreach ((string control, (string vjoyAxis, StickCurve? on, StickCurve off)) in controls)
        {
            int wanted = Array.IndexOf(AxisLetters, vjoyAxis) + 1;
            var found = inputs.Elements("input").Where(input => Text(input, "mode") == MainMode && Text(input, "input-type") == "axis")
                .Select(input => (Input: input, Steps: Steps(input, library),
                    Vjoy: Steps(input, library).FirstOrDefault(a => (string?)a.Attribute("type") == "map-to-vjoy" && Property(a, "vjoy-input-type") == "axis"
                        && Property(a, "vjoy-input-id") == wanted.ToString(CultureInfo.InvariantCulture))))
                .Where(x => x.Vjoy is not null)
                .ToList();
            if (wanted == 0 || found.Count == 0)
            {
                notes.Add($"No axis of this profile goes to the vJoy axis the game reads for {control}, so {control} has no Flight Assist off curve. Add it in Gremlin, or bind {control} in the game to Gremlin's vJoy.");
                continue;
            }
            // The game reads one vJoy: device 1 when the profile feeds several.
            int VjoyDevice(XElement vjoy) => int.TryParse(Property(vjoy, "vjoy-device-id"), out int n) ? n : 1;
            int device = found.Select(x => VjoyDevice(x.Vjoy!)).OrderBy(n => n == 1 ? 0 : 1).ThenBy(n => n).First();
            if (found.Select(x => VjoyDevice(x.Vjoy!)).Distinct().Count() > 1)
                notes.Add($"This profile feeds {control}'s vJoy axis on more than one vJoy device; Studio changed the one on vJoy device {device}.");
            foreach ((XElement input, List<XElement> steps, XElement? vjoy) in found.Where(x => VjoyDevice(x.Vjoy!) == device))
            {
                XElement topAction = library[input.Element("action-configuration")!.Element("root-action")!.Value.Trim()];
                XElement? oldCurve = steps.FirstOrDefault(a => (string?)a.Attribute("type") == "response-curve");

                // Default: the "on" curve, when one is given, in the place of the curve it had (or ahead of the rest when it had none).
                if (on is not null)
                {
                    XElement newOn = CurveAction(on);
                    libraryNode.Add(newOn);
                    XElement list = topAction.Element("actions")!;
                    if (oldCurve is not null)
                        foreach (XElement id in list.Elements("action-id").Where(id => id.Value.Trim() == (string?)oldCurve.Attribute("id")).ToList()) id.Value = (string)newOn.Attribute("id")!;
                    else list.AddFirst(new XElement("action-id", (string)newOn.Attribute("id")!));
                }

                if (!switching) continue;   // the game always gets Default's curve

                // FA Off: the same input again, doing what it does in Default with the "off" curve in place of the curve,
                // each step a copy of its own. A binding of that axis the player had put in FA Off gives way to it.
                foreach (XElement theirs in inputs.Elements("input").Where(other => Text(other, "mode") == FaOffMode && Text(other, "input-type") == "axis"
                    && Text(other, "device-id").Equals(Text(input, "device-id"), StringComparison.OrdinalIgnoreCase) && Text(other, "input-id") == Text(input, "input-id")).ToList())
                {
                    theirs.Remove();
                    notes.Add($"Your own FA Off binding of the axis behind {control} was replaced by Studio's.");
                }
                XElement newOff = CurveAction(off);
                var offSteps = new List<XElement>();
                if (oldCurve is null) offSteps.Add(newOff);
                foreach (XElement step in steps)
                {
                    if (step == oldCurve) { offSteps.Add(newOff); continue; }
                    var stepCopy = new XElement(step);
                    stepCopy.SetAttributeValue("id", NewId());
                    offSteps.Add(stepCopy);
                }
                XElement offRoot = RootAction([.. offSteps]);
                libraryNode.Add(offSteps.Where(s => s != newOff).Prepend(newOff).Append(offRoot));
                var offInput = new XElement(input);
                offInput.Element("mode")!.Value = FaOffMode;
                offInput.Element("action-configuration")!.Element("root-action")!.Value = (string)offRoot.Attribute("id")!;
                inputs.Add(offInput);
            }
        }

        // The mode switch, bound in Default only: "FA Off" inherits it. A switch (Cycle) is bound to one key or button,
        // so a single action keeps count of where the cycle is; a held Flight Assist (Temporary) can be bound to every one.
        if (!switching) faSwitches = [];
        var usable = faSwitches.Select(fa => (Way: fa, At: SwitchInput(root, fa.Press))).ToList();
        foreach ((FlightButton way, _) in usable.Where(u => u.At is null))
            notes.Add($"Gremlin cannot switch modes on {Say(way.Press)}, which switches Flight Assist in the game: that one will switch the game but not Gremlin.");
        var bound = usable.Where(u => u.At is not null).Take(faToggles ? 1 : int.MaxValue).ToList();
        if (!switching) notes.Add("The curves are not set to follow Flight Assist, so the profile has no FA Off mode: the game gets your Flight Assist on curves.");
        else if (faSwitches.Count == 0) notes.Add("Flight Assist has no key or button in the game, so Gremlin's FA Off mode has no switch. Bind Flight Assist in the game, then save this again.");
        else if (bound.Count == 0) notes.Add("Bind Flight Assist in the game to a keyboard key (or to a button of the stick this profile uses), then save this again, so that Gremlin's mode switches with it.");
        foreach ((FlightButton way, _) in usable.Where(u => u.At is not null).Skip(bound.Count))
            notes.Add($"{Say(way.Press)} also switches Flight Assist in the game but not Gremlin's mode: use {Say(bound[0].Way.Press)}, or the two will get out of step.");
        foreach ((FlightButton way, (string Device, string Type, int Id)? at) in bound)
        {
            if (way.Hold.Count > 0) notes.Add($"Flight Assist needs {string.Join(" + ", way.Hold.Select(Say))} held in the game; Gremlin switches on {Say(way.Press)} alone.");
            // Cycle starts at its first mode on the first press, whatever mode Gremlin is in: so FA Off comes first.
            XElement change = new("action", new XAttribute("id", NewId()), new XAttribute("type", "change-mode"),
                Prop("string", "change-type", faToggles ? "Cycle" : "Temporary"),
                faToggles ? new[] { TargetMode(FaOffMode), TargetMode(MainMode) } : new[] { TargetMode(FaOffMode) },
                Prop("string", "action-label", SwitchLabel),
                Prop("activation-mode", "activation-mode", "press"));
            XElement top = RootAction(change);
            libraryNode.Add(change, top);
            inputs.Add(new XElement("input",
                new XElement("device-id", at!.Value.Device), new XElement("input-type", at.Value.Type), new XElement("mode", MainMode),
                new XElement("input-id", at.Value.Id),
                new XElement("action-configuration", new XElement("root-action", (string)top.Attribute("id")!), new XElement("behavior", at.Value.Type))));
        }

        XElement modes = root.Element("modes") ?? Add(root, new XElement("modes"));
        if (!modes.Elements("mode").Any(m => m.Value.Trim() == MainMode)) modes.AddFirst(new XElement("mode", MainMode));
        modes.Elements("mode").Where(m => m.Value.Trim() == FaOffMode).Remove();
        // Without switching the mode stays only where the player has bindings of their own in it.
        if (switching || inputs.Elements("input").Any(input => Text(input, "mode") == FaOffMode))
            modes.Add(new XElement("mode", new XAttribute("parent", MainMode), FaOffMode));
        Prune(root);
        return (copy, notes);
    }

    /// <summary>A profile that has something of Studio's in it: it carries an action with one of Studio's labels.</summary>
    internal static bool IsStudios(XDocument profile) =>
        profile.Root?.Element("library")?.Elements("action").Any(a => Property(a, "action-label") is SwitchLabel or CurveLabel) == true;

    /// <summary>
    /// The note Studio puts at the top of every file it writes: a file Studio wrote stays Studio's to write over even when the
    /// player turned the switch off and nothing of Studio's actions is left in it.
    /// </summary>
    const string Mark = " Written by Elite Studio (Studio Flight). Studio may write this file again; your own profile is never written over. ";

    static bool HasMark(XDocument profile) => profile.Nodes().OfType<XComment>().Any(c => c.Value == Mark);

    internal static bool IsStudiosFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            XDocument written = XDocument.Load(stream);
            return IsStudios(written) || HasMark(written);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Where Studio's FA Off copy of a profile goes: a profile Studio wrote itself is written over in place; any other gets
    /// "-fa-off" added to its name, or "-studio-fa-off" when a file of the player's own already has that name.
    /// </summary>
    public static string TargetFor(string playersProfile)
    {
        if (File.Exists(playersProfile) && IsStudiosFile(playersProfile)) return playersProfile;
        string folder = Path.GetDirectoryName(playersProfile) ?? ".";
        string name = Path.GetFileNameWithoutExtension(playersProfile);
        // The first of these that is not there or that Studio wrote: never a file of the player's own.
        string target = Path.Combine(folder, name + "-fa-off.xml");
        for (int n = 1; File.Exists(target) && !IsStudiosFile(target) && n < 100; n++)
            target = Path.Combine(folder, name + (n == 1 ? "-studio-fa-off.xml" : $"-studio-fa-off-{n}.xml"));
        return target;
    }

    /// <summary>
    /// Writes Studio's FA Off copy where <see cref="TargetFor"/> says, never over a file Studio did not write; a file of
    /// Studio's that is replaced is kept first as a dated backup beside it. Returns where it went.
    /// </summary>
    public static string Save(XDocument profile, string playersProfile)
    {
        string target = TargetFor(playersProfile);
        if (File.Exists(target))
        {
            if (!IsStudiosFile(target)) throw new IOException($"{target} is not a file Studio wrote, so Studio will not write over it.");
            File.Copy(target, target + ".backup-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture), overwrite: true);
        }
        string temp = target + ".tmp";
        var settings = new System.Xml.XmlWriterSettings { Indent = true, IndentChars = "    ", Encoding = new UTF8Encoding(false) };
        // Marked as Studio's, in a copy (the caller's profile is left as it is).
        var marked = new XDocument(profile);
        if (!HasMark(marked)) marked.Root?.AddBeforeSelf(new XComment(Mark));   // (ahead of the root: Gremlin's reader never sees it)
        using (var writer = System.Xml.XmlWriter.Create(temp, settings)) marked.Save(writer);
        File.Move(temp, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// Studio's five numbers back from a Gremlin curve, when it is one Studio can have made: piecewise linear, the same
    /// travel either side, points that rise through the middle the same way both sides and lie on Studio's shape. Null for
    /// any other curve (a spline, a lopsided or inverted one): Studio then does not claim to know it.
    /// </summary>
    internal static StickCurve? ReadCurve(XElement action)
    {
        if (Property(action, "curve-type") != "PiecewiseLinear") return null;
        XElement? dz = action.Element("deadzone");
        double low = Float(dz, "low"), centerLow = Float(dz, "center-low"), centerHigh = Float(dz, "center-high"), high = Float(dz, "high");
        if (double.IsNaN(low) || double.IsNaN(high) || double.IsNaN(centerLow) || double.IsNaN(centerHigh)) return null;
        List<(double X, double Y)> points = [];
        foreach (XElement p in action.Element("control-points")?.Elements("property") ?? [])
        {
            string[] xy = (p.Element("value")?.Value ?? "").Split(',');
            if (xy.Length != 2 || !double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                || !double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)) return null;
            points.Add((x, y));
        }
        points.Sort((a, b) => a.X.CompareTo(b.X));
        if (points.Count < 2 || points[0].X > -0.999 || points[^1].X < 0.999) return null;   // Gremlin's own straight line has just its two ends
        for (int i = 1; i < points.Count; i++) if (points[i].Y < points[i - 1].Y) return null;   // rising: not inverted
        double full = Interpolate(points, 1);
        if (full <= 0.01 || Math.Abs(Interpolate(points, -1) + full) > 0.01 || Math.Abs(Interpolate(points, 0)) > 0.01) return null;
        foreach ((double x, double y) in points) if (Math.Abs(Interpolate(points, -x) + y) > 0.01) return null;   // the same both ways
        if (Math.Abs(-low - high) > 0.011) return null;   // one travel either side
        double bend = Math.Clamp((0.5 - Interpolate(points, 0.5) / full) / 0.375, 0, 1);
        var curve = new StickCurve(-centerLow * 100, centerHigh * 100, Math.Min(-low, high) * 100, full * 100, bend * 100);
        for (int i = 0; i <= 10; i++)   // and it is Studio's shape, not just any curve through those three points
            if (Math.Abs(curve.Shape(i / 10.0) - Interpolate(points, i / 10.0)) > 0.012) return null;
        return curve.Cleaned();
    }

    static double Interpolate(List<(double X, double Y)> points, double x)
    {
        for (int i = 1; i < points.Count; i++)
            if (x <= points[i].X)
            {
                (double x0, double y0) = points[i - 1];
                (double x1, double y1) = points[i];
                return x1 == x0 ? y1 : y0 + (y1 - y0) * (x - x0) / (x1 - x0);
            }
        return points[^1].Y;
    }

    /// <summary>A Response Curve action with Studio's curve in 21 points, as Gremlin's own editor writes one.</summary>
    internal static XElement CurveAction(StickCurve curve)
    {
        static string N(double value) => value.ToString(value == Math.Floor(value) ? "0.0" : "0.0####", CultureInfo.InvariantCulture);
        double travel = curve.SatX / 100;
        double low = Math.Min(-travel, -curve.DeadLow / 100 - 0.01), high = Math.Max(travel, curve.DeadHigh / 100 + 0.01);
        var points = new XElement("control-points");
        for (int i = -10; i <= 10; i++)
        {
            double t = i / 10.0;
            points.Add(Prop("point2d", "point", $"{N(t)},{N(Math.Round(Math.Sign(t) * curve.Shape(Math.Abs(t)), 4))}"));
        }
        return new XElement("action", new XAttribute("id", NewId()), new XAttribute("type", "response-curve"),
            new XElement("deadzone",
                Prop("float", "low", N(Math.Round(low, 4))), Prop("float", "center-low", N(Math.Round(-curve.DeadLow / 100, 4))),
                Prop("float", "center-high", N(Math.Round(curve.DeadHigh / 100, 4))), Prop("float", "high", N(Math.Round(high, 4)))),
            points,
            Prop("string", "curve-type", "PiecewiseLinear"),
            Prop("string", "action-label", CurveLabel),
            Prop("activation-mode", "activation-mode", "disallowed"));
    }

    /// <summary>
    /// The input Gremlin takes for the game's Flight Assist key or button: a keyboard key, or a button of
    /// a stick the profile already knows (matched by its name). Null when Gremlin cannot have it.
    /// </summary>
    static (string Device, string Type, int Id)? SwitchInput(XElement root, FlightKey press)
    {
        if (press.Device == "Keyboard") return KeyNames.GremlinId(press.Key) is { } key ? (KeyboardId, "key", key) : null;
        if (!press.Key.StartsWith("Joy_", StringComparison.Ordinal) || !int.TryParse(press.Key[4..], out int button)) return null;   // a hat, an axis
        List<XElement> sticks = [.. root.Element("devices")?.Elements("device") ?? []];
        string wanted = string.Concat(press.Device.Where(char.IsLetterOrDigit));
        XElement? stick = sticks.FirstOrDefault(d => string.Concat(Text(d, "device-name").Where(char.IsLetterOrDigit)) is { Length: > 0 } name
            && (wanted.StartsWith(name, StringComparison.OrdinalIgnoreCase) || name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)));
        return stick is null || press.Device.Equals("vJoy", StringComparison.OrdinalIgnoreCase) ? null : (Text(stick, "device-id").ToLowerInvariant(), "button", button);
    }

    static string Say(FlightKey key) => key.Device == "Keyboard" ? key.Key.Replace("Key_", "") + " key" : key.Key.Replace("Joy_", "button ") + " of " + key.Device;

    /// <summary>Drops every library action no input reaches any more (what the last FA Off mode used).</summary>
    static void Prune(XElement root)
    {
        Dictionary<string, XElement> library = Library(root);
        var kept = new HashSet<string>();
        var todo = new Stack<string>((root.Element("inputs")?.Elements("input") ?? [])
            .Select(i => i.Element("action-configuration")?.Element("root-action")?.Value.Trim() ?? ""));
        while (todo.Count > 0)
        {
            string id = todo.Pop();
            if (!kept.Add(id) || !library.TryGetValue(id, out XElement? action)) continue;
            foreach (XElement next in action.Descendants("action-id")) todo.Push(next.Value.Trim());
        }
        foreach ((string id, XElement action) in library)
            if (!kept.Contains(id)) action.Remove();
    }

    static List<XElement> Steps(XElement input, Dictionary<string, XElement> library)
    {
        string rootId = input.Element("action-configuration")?.Element("root-action")?.Value.Trim() ?? "";
        return library.TryGetValue(rootId, out XElement? top)
            ? [.. top.Element("actions")?.Elements("action-id").Select(id => library.GetValueOrDefault(id.Value.Trim())).OfType<XElement>() ?? []]
            : [];
    }

    static XElement RootAction(params XElement[] steps) =>
        new("action", new XAttribute("id", NewId()), new XAttribute("type", "root"),
            new XElement("actions", steps.Select(s => new XElement("action-id", (string)s.Attribute("id")!))),
            Prop("string", "action-label", "Root"),
            Prop("activation-mode", "activation-mode", "disallowed"));

    static XElement TargetMode(string mode) => new("target-mode", Prop("string", "name", mode));

    static XElement Prop(string type, string name, string value) =>
        new("property", new XAttribute("type", type), new XElement("name", name), new XElement("value", value));

    static string? Property(XElement action, string name) =>
        action.Elements("property").FirstOrDefault(p => p.Element("name")?.Value.Trim() == name)?.Element("value")?.Value.Trim();

    static double Float(XElement? parent, string name) =>
        parent?.Elements("property").FirstOrDefault(p => p.Element("name")?.Value.Trim() == name)?.Element("value")?.Value is { } text
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN;

    static Dictionary<string, XElement> Library(XElement root)
    {
        var library = new Dictionary<string, XElement>();
        foreach (XElement action in root.Element("library")?.Elements("action") ?? [])
            if ((string?)action.Attribute("id") is { } id) library[id.Trim()] = action;
        return library;
    }

    static string Text(XElement parent, string name) => parent.Element(name)?.Value.Trim() ?? "";

    static XElement Add(XElement parent, XElement child)
    {
        parent.Add(child);
        return child;
    }

    static string NewId() => Guid.NewGuid().ToString();

    static XElement Check(XDocument profile)
    {
        XElement root = profile.Root ?? throw new InvalidDataException("The file is empty.");
        if (root.Name != "profile" || (string?)root.Attribute("version") != "14")
            throw new InvalidDataException("This is not a Joystick Gremlin profile of release 14 or later (\"version 14\"), the only kind Studio can read and write.");
        return root;
    }
}
