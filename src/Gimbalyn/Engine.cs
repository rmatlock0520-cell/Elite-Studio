using System.Globalization;
using EliteStudio.Flight;

namespace EliteStudio.Gimbalyn;

/// <summary>How Gimbalyn reads real sticks (Windows' joystick calls in Studio; stand-ins in tests).</summary>
public interface IStickInput
{
    /// <summary>
    /// The device's six axes this moment, by Windows' letters in <see cref="GimbalynProfile.StickAxes"/>' order, each from -1
    /// to 1 (NaN where it has none); false when the device is not there now (or another device sits in its place).
    /// </summary>
    bool Read(string device, Span<double> axes);

    /// <summary>Forgets where each device was found, so the next reading looks for it afresh (at a start, a wake, a new profile).</summary>
    void Forget();
}

/// <summary>What happened when Gimbalyn tried to take vJoy's device.</summary>
/// <param name="Taken">It is Gimbalyn's to feed now.</param>
/// <param name="Owner">The program holding it instead, when one does ("joystick_gremlin"); null when none could be named.</param>
/// <param name="Problem">Why it could not be taken, when no other program holds it (vJoy missing, not running, device not set up).</param>
public sealed record VJoyTake(bool Taken, string? Owner = null, string? Problem = null);

/// <summary>How Gimbalyn feeds vJoy (vJoy's own library in Studio; a stand-in in tests).</summary>
public interface IVJoyOutput
{
    /// <summary>Takes the device to feed it; never from another program that holds it.</summary>
    VJoyTake Take(int device);
    /// <summary>Whether the device is still this program's.</summary>
    bool Owns(int device);
    /// <summary>The device's range for an axis (by the game's letter), or null when the device lacks that axis.</summary>
    (int Min, int Max)? Range(int device, string axis);
    /// <summary>Sets an axis to a raw value inside its range; false when vJoy did not take it.</summary>
    bool Set(int device, string axis, int raw);
    /// <summary>Lets go of the device (its axes stay where they were last set, so Gimbalyn centres them first).</summary>
    void Release(int device);
}

/// <summary>Gimbalyn as a page or a log sees it this moment.</summary>
/// <param name="State">"stopped", "running", "busy" (another program holds vJoy), "noVJoy" (vJoy cannot be fed), "asleep" (Windows is sleeping).</param>
/// <param name="Inputs">Each route's stick reading; null for a stick that is gone (see <paramref name="Lost"/>).</param>
public sealed record GimbalynStatus(string State, string? Problem, string? Owner, string Mode, string[] Modes, string[] Lost,
    double?[] Inputs, double[] Outputs, string[] Targets, int Rate, int Device);

/// <summary>
/// Gimbalyn's work, without Windows: each tick it reads the routed sticks, shapes each axis with the curve of the mode in
/// use, and writes to vJoy what changed (and every axis again four times a second, so a vJoy changed behind its back is
/// put right). Every way out of feeding (stop, a lost stick, a failed write, sleep, closing) centres the axes first: vJoy
/// keeps an axis wherever it was last set, and with Flight Assist off an axis left deflected keeps a ship turning. All its
/// doors are locked, so the pipe and the clock may call it from their own threads.
/// </summary>
public sealed class Engine
{
    /// <summary>How often every axis is written again even when unchanged, and how often vJoy is asked whether it is still Gimbalyn's.</summary>
    const int RewriteEvery = 250, OwnCheckEvery = 1000;

    readonly object gate = new();
    readonly IStickInput input;
    readonly IVJoyOutput output;
    readonly Func<int, bool> keyHeld;
    readonly Func<string?> front;
    readonly Recorder? recorder;
    readonly Action<string> log;

    GimbalynProfile profile = new();
    // The profile laid out for the ticks, so nothing is looked up or allocated while feeding.
    string[] devices = [];             // the real sticks read
    double[][] readings = [];          // their axes this tick
    bool[] present = [];               // whether each answered this tick
    int[] routeDevice = [], routeAxis = [];
    string[] targets = [];
    bool[] invert = [];
    (int Min, int Max)[] ranges = [];
    int[] last = [];                   // the raw value last written to each target (int.MinValue: none yet, or to be written again)
    double[] inputs = [], outputs = [];
    string[] modeNames = [];
    StickCurve?[][] modeCurves = [];   // by mode, by route
    int mode;
    int modeKey;                       // Windows' key for the mode key (0: none)
    bool modeKeyDown;

    string state = "stopped";
    string? problem, owner;
    bool wake;                         // feeding when Windows went to sleep, so feed again when it wakes
    long retryAt;                      // when vJoy is next tried again after a failed write
    int failures;                      // failed tries in a row (the wait grows: 50 ms, 100, 200... up to a second)
    long clock, rewriteAt, ownCheckAt;
    readonly HashSet<string> lost = [];
    int ticks, rate;
    long rateFrom;

    /// <param name="keyHeld">Whether a key (Windows' number for it) is held; null: never.</param>
    /// <param name="front">The program in front (its process name); null: unknown.</param>
    /// <param name="log">Where events go (taken, released, lost, found...): never every tick.</param>
    public Engine(IStickInput input, IVJoyOutput output, Func<int, bool>? keyHeld = null, Func<string?>? front = null, Recorder? recorder = null,
        Action<string>? log = null)
    {
        (this.input, this.output, this.recorder) = (input, output, recorder);
        this.keyHeld = keyHeld ?? (_ => false);
        this.front = front ?? (() => null);
        this.log = log ?? (_ => { });
        Lay(profile);
    }

    public GimbalynProfile Profile { get { lock (gate) return profile; } }

    /// <summary>Uses a new profile. While feeding, the axes it no longer routes are centred first, and the new ones start from the middle.</summary>
    public void Use(GimbalynProfile next)
    {
        next = next.Cleaned();
        lock (gate)
        {
            bool feeding = state == "running";
            if (feeding)
            {
                // Centre what the old profile fed (the new one may route other axes, or another device), then start the new one from the middle.
                CentreLogged("the profile changed");
                if (next.Device != profile.Device) { output.Release(profile.Device); state = "stopped"; }
            }
            string before = modeNames.Length > 0 ? modeNames[mode] : "";
            Lay(next);
            log("profile: " + Describe(next));
            if (before != "" && Array.IndexOf(modeNames, before) is >= 0 and int kept) mode = kept;   // the mode in use stays in use
            if (state == "running")
            {
                input.Forget();
                if (TakeRanges()) Centre();
                else output.Release(profile.Device);   // the new profile wants axes the device lacks: let go (said in the problem)
            }
            else if (feeding) StartLocked();
        }
    }

    /// <summary>The profile in a line for the log: device, routes, and each mode's curves.</summary>
    static string Describe(GimbalynProfile p)
    {
        static string Curve(StickCurve c) => string.Create(CultureInfo.InvariantCulture, $"dead {c.DeadLow}/{c.DeadHigh} satX {c.SatX} satY {c.SatY} bend {c.Bend}");
        string routes = p.Routes.Count == 0 ? "no routes" : string.Join(", ", p.Routes.Select(r => $"{r.Device} {r.Axis}{(r.Invert ? " inverted" : "")} -> {r.Target}"));
        string modes = string.Join("; ", p.Modes.Select(m => $"{m.Key}: " + (m.Value.Count == 0 ? "straight" : string.Join(", ", m.Value.Select(c => $"{c.Key} {Curve(c.Value)}")))));
        return $"vJoy device {p.Device}; {routes}; modes {modes}; starts in {(p.Mode == "" ? "none" : p.Mode)}" + (p.ModeKey is { } k ? $"; mode key {k}" + (p.ModeKeyOnlyWith is { } w ? $" with {w} in front" : "") : "");
    }

    void Lay(GimbalynProfile next)
    {
        profile = next;
        devices = [.. next.Routes.Select(r => r.Device).Distinct()];
        readings = [.. devices.Select(_ => new double[6])];
        present = new bool[devices.Length];
        routeDevice = [.. next.Routes.Select(r => Array.IndexOf(devices, r.Device))];
        routeAxis = [.. next.Routes.Select(r => Array.IndexOf(GimbalynProfile.StickAxes, r.Axis))];
        targets = [.. next.Routes.Select(r => r.Target)];
        invert = [.. next.Routes.Select(r => r.Invert)];
        ranges = new (int, int)[targets.Length];
        last = [.. targets.Select(_ => int.MinValue)];
        inputs = new double[targets.Length];
        outputs = new double[targets.Length];
        modeNames = [.. next.Modes.Keys];
        modeCurves = [.. next.Modes.Values.Select(curves => targets.Select(t => curves.GetValueOrDefault(t)).ToArray())];
        mode = Math.Max(0, Array.IndexOf(modeNames, next.Mode));
        modeKey = next.ModeKey is { } key ? KeyNames.VirtualKey(key) : 0;
        modeKeyDown = modeKey != 0 && keyHeld(modeKey);   // a key held as the profile arrives is not a press
        lost.Clear();
    }

    /// <summary>Takes vJoy and starts feeding from the middle; true when feeding.</summary>
    public bool Start()
    {
        lock (gate) return StartLocked();
    }

    bool StartLocked()
    {
        if (state == "running") return true;
        wake = false;
        if (targets.Length == 0) return Fail("stopped", "The profile routes no axis to vJoy.");
        VJoyTake take = output.Take(profile.Device);
        if (!take.Taken)
        {
            owner = take.Owner;
            return Fail(take.Owner is not null ? "busy" : "noVJoy",
                take.Owner is not null ? $"vJoy device {profile.Device} is held by {take.Owner}." : take.Problem ?? $"vJoy device {profile.Device} could not be taken.");
        }
        if (!TakeRanges())
        {
            output.Release(profile.Device);
            return false;
        }
        (state, problem, owner, failures) = ("running", null, null, 0);
        input.Forget();   // each stick looked for afresh: one unplugged meanwhile may have left another in its place
        (rewriteAt, ownCheckAt) = (clock + RewriteEvery, clock + OwnCheckEvery);
        Centre();
        log($"took vJoy device {profile.Device}; mode {Mode()}");
        return true;
    }

    /// <summary>Reads each routed axis's range from vJoy; false (and why) when the device lacks one.</summary>
    bool TakeRanges()
    {
        string[] missing = [.. targets.Where(t => output.Range(profile.Device, t) is null)];
        if (missing.Length > 0) return Fail("noVJoy", $"vJoy device {profile.Device} lacks the axes {string.Join(", ", missing)}. Add them in Configure vJoy.");
        for (int i = 0; i < targets.Length; i++) { ranges[i] = output.Range(profile.Device, targets[i])!.Value; last[i] = int.MinValue; }
        return true;
    }

    bool Fail(string to, string why)
    {
        (state, problem) = (to, why);
        log(why);
        return false;
    }

    /// <summary>Centres every routed axis and lets go of vJoy.</summary>
    public void Stop(string why)
    {
        lock (gate) StopLocked(why);
    }

    /// <summary>
    /// The same, for the way out of the process (closing, a fatal problem): waits for the lock a moment only, so a thread that
    /// died holding it can never keep Gimbalyn from ending; false when it could not stop in time.
    /// </summary>
    public bool TryStop(string why, int waitMs = 500)
    {
        if (!Monitor.TryEnter(gate, waitMs)) return false;
        try
        {
            StopLocked(why);
            return true;
        }
        finally
        {
            Monitor.Exit(gate);
        }
    }

    void StopLocked(string why)
    {
        wake = false;
        if (state != "running") { (state, problem, owner) = ("stopped", null, null); return; }   // busy, no vJoy or asleep: nothing held
        CentreLogged(why);
        output.Release(profile.Device);
        (state, problem, owner) = ("stopped", null, null);
        log($"let go of vJoy device {profile.Device}: {why}");
    }

    /// <summary>Centres every routed axis now (and keeps feeding, from the middle).</summary>
    public void CentreNow(string why)
    {
        lock (gate) if (state == "running") CentreLogged(why);
    }

    /// <summary>Windows goes to sleep: centred and let go, to be taken again when it wakes.</summary>
    public void Sleep()
    {
        lock (gate)
        {
            if (state != "running") return;
            CentreLogged("Windows is going to sleep");
            output.Release(profile.Device);
            (state, wake) = ("asleep", true);
            log($"let go of vJoy device {profile.Device} while Windows sleeps");
        }
    }

    /// <summary>Windows woke: feeding again if it was before (the stick and vJoy are taken afresh).</summary>
    public void Wake()
    {
        lock (gate)
        {
            if (!wake) return;
            (state, wake) = ("stopped", false);
            lost.Clear();
            log("Windows woke: taking vJoy again");
            StartLocked();
        }
    }

    /// <summary>Uses another mode by name; false when there is none of that name.</summary>
    public bool SetMode(string name)
    {
        lock (gate)
        {
            int index = Array.IndexOf(modeNames, name);
            if (index < 0) return false;
            if (index != mode) { mode = index; log($"mode {name}"); }
            return true;
        }
    }

    string Mode() => modeNames.Length > 0 ? modeNames[mode] : "";

    /// <summary>One round: the mode key, the sticks read, each axis shaped, and what changed written.</summary>
    public void Tick(long now)
    {
        lock (gate)
        {
            clock = now;
            CountRate(now);
            ModeKey();   // in every state, so no press is missed and the count never slips
            if (state == "running") Feed(now);
            recorder?.Add(now, StateCode(), mode, modeNames, targets, inputs, outputs, LostMask());
        }
    }

    void Feed(long now)
    {
        if (problem is not null)
        {
            // A write failed: vJoy is tried again soon, then less often (50 ms doubling to a second), until it answers or another program has it.
            if (now < retryAt) return;
            VJoyTake take = output.Take(profile.Device);
            if (!take.Taken)
            {
                if (take.Owner is not null) { (state, owner, problem) = ("busy", take.Owner, $"vJoy device {profile.Device} is held by {take.Owner} now."); log(problem); }
                else Wait();
                return;
            }
            if (!TakeRanges()) { output.Release(profile.Device); return; }   // set up differently meanwhile: let go (said in the problem)
            if (!Centre()) { Wait(); return; }   // taken, but it still takes no values: tried again later, less often
            log($"vJoy device {profile.Device} answers again");   // once: only now that it took the middle
            (problem, failures) = (null, 0);
        }
        else if (now >= ownCheckAt)
        {
            ownCheckAt = now + OwnCheckEvery;
            if (!output.Owns(profile.Device))
            {
                // vJoy let go of Gimbalyn (its driver restarted, or it was set up anew): tried again as after a failed write.
                problem = $"vJoy device {profile.Device} is no longer Gimbalyn's";
                CentreLogged(problem);   // best effort, as every way out of feeding: whatever vJoy still takes goes to the middle
                log(problem + ": taking it again if it is free");
                retryAt = now;
                return;
            }
        }
        if (now >= rewriteAt)
        {
            rewriteAt = now + RewriteEvery;
            Array.Fill(last, int.MinValue);   // every axis written again: a vJoy changed behind Gimbalyn's back is put right
        }
        for (int d = 0; d < devices.Length; d++)
        {
            bool there = input.Read(devices[d], readings[d]);
            if (!there && lost.Add(devices[d])) log($"stick {devices[d]} is gone: its axes are held in the middle until it is back");
            else if (there && lost.Remove(devices[d])) log($"stick {devices[d]} is back");
            present[d] = there;
        }
        StickCurve?[] curves = modeCurves.Length > 0 ? modeCurves[mode] : [];
        for (int i = 0; i < targets.Length; i++)
        {
            bool there = present[routeDevice[i]];
            double v = there ? readings[routeDevice[i]][routeAxis[i]] : 0;
            if (!double.IsFinite(v)) v = 0;   // the stick lacks that axis: the middle
            if (invert[i]) v = -v;
            inputs[i] = there ? v : double.NaN;   // a lost stick shows as nothing in the recorder, not as a stick at rest
            outputs[i] = i < curves.Length && curves[i] is { } curve ? curve.At(v) : v;
            if (!Write(i, outputs[i])) return;
        }
    }

    /// <summary>The mode key: a press (not a hold) moves on to the next mode, when its program is in front.</summary>
    void ModeKey()
    {
        if (modeKey == 0) return;
        bool down = keyHeld(modeKey);
        if (down && !modeKeyDown && modeNames.Length > 1 && (profile.ModeKeyOnlyWith is null || string.Equals(front(), profile.ModeKeyOnlyWith, StringComparison.OrdinalIgnoreCase)))
        {
            mode = (mode + 1) % modeNames.Length;
            log($"mode {modeNames[mode]} (key)");
        }
        modeKeyDown = down;
    }

    /// <summary>Writes one axis when its raw value changed; false (axes centred as far as vJoy lets, and vJoy to be tried again) when the write failed.</summary>
    bool Write(int i, double value)
    {
        int raw = ToRaw(value, ranges[i].Min, ranges[i].Max);
        if (raw == last[i]) return true;
        if (output.Set(profile.Device, targets[i], raw)) { last[i] = raw; return true; }
        problem = $"vJoy device {profile.Device} stopped taking values";
        if (failures == 0) log(problem + ": centring what it still takes and trying again");
        Centre();   // best effort: whatever vJoy still takes goes to the middle at once
        Wait();
        return false;
    }

    /// <summary>The next try after a failure: 50 ms, then twice as long each time, at most a second.</summary>
    void Wait()
    {
        retryAt = clock + Math.Min(1000, 50 << Math.Min(failures, 5));
        failures++;
    }

    /// <summary>Every routed axis to the middle, written whatever was written before; true when vJoy took them all.</summary>
    bool Centre()
    {
        bool all = true;
        for (int i = 0; i < targets.Length; i++)
        {
            last[i] = int.MinValue;
            inputs[i] = outputs[i] = 0;
            if (ranges[i] is not { Max: > 0 }) { all = false; continue; }
            int middle = ToRaw(0, ranges[i].Min, ranges[i].Max);
            if (output.Set(profile.Device, targets[i], middle)) last[i] = middle;
            else all = false;
        }
        return all;
    }

    void CentreLogged(string why) => log(Centre() ? $"centred vJoy device {profile.Device}: {why}" : $"could NOT centre every axis of vJoy device {profile.Device} ({why}): vJoy did not take it");

    /// <summary>A value from -1 to 1 as vJoy's raw number in its range, rounded as Joystick Gremlin does (0 is the middle: 16384 of 0 to 32767).</summary>
    public static int ToRaw(double value, int min, int max)
    {
        double half = (max - min) / 2.0;
        return Math.Clamp(min + (int)Math.Floor(half * (1 + Math.Clamp(double.IsFinite(value) ? value : 0, -1, 1)) + 0.5), min, max);
    }

    /// <summary>The state as the recorder keeps it: 0 stopped, 1 running, 2 running but vJoy failing, 3 busy, 4 no vJoy, 5 asleep.</summary>
    int StateCode() => state switch { "running" => problem is null ? 1 : 2, "busy" => 3, "noVJoy" => 4, "asleep" => 5, _ => 0 };

    int LostMask()
    {
        int mask = 0;
        for (int d = 0; d < devices.Length && d < 31; d++) if (lost.Contains(devices[d])) mask |= 1 << d;
        return mask;
    }

    void CountRate(long now)
    {
        ticks++;
        if (now - rateFrom < 1000) return;
        rate = (int)Math.Round(ticks * 1000.0 / Math.Max(1, now - rateFrom));
        (ticks, rateFrom) = (0, now);
    }

    public GimbalynStatus Status()
    {
        lock (gate)
            return new GimbalynStatus(state, problem, owner, Mode(), [.. modeNames], [.. lost], [.. inputs.Select(v => double.IsFinite(v) ? v : (double?)null)],
                [.. outputs], [.. targets], rate, profile.Device);
    }
}
