using System.Text.Json;
using System.Text.Json.Nodes;
using EliteStudio.Gimbalyn;

namespace EliteStudio.Flight;

/// <summary>
/// The Gimbalyn profile for flying: pitch, roll and yaw from the player's real stick to the vJoy axes Elite reads them from,
/// with one mode of curves for Flight Assist on and, when switching, one for off. Made from the bindings, the stick found
/// for each control and the two sets of curves; nothing here touches Windows.
/// </summary>
public static class ShaperProfile
{
    public const string OnMode = "FA On", OffMode = "FA Off";
    /// <summary>Elite's process: the Flight Assist key counts only while the game is in front.</summary>
    const string Elite = "EliteDangerous64";
    static readonly string[] Turning = ["pitch", "roll", "yaw"];

    /// <param name="Profile">Null when nothing can be routed (see <paramref name="Problem"/>).</param>
    /// <param name="Notes">Controls that were left out, and why.</param>
    public sealed record Made(GimbalynProfile? Profile, string? Problem, IReadOnlyList<string> Notes);

    /// <param name="realSource">The real stick behind a control: its device and Windows' letter for the axis; null when none is found.</param>
    /// <param name="switching">Two modes, switched with the game's Flight Assist; off, the game always gets the Flight Assist on curves.</param>
    /// <param name="faOff">Flight Assist is off in the game now: the mode the profile starts in when switching.</param>
    public static Made Build(FlightBinds? binds, Func<string, (string Device, string Letter)?> realSource, FlightCurves on, FlightCurves off,
        bool switching, bool faOff, string vjoyFolder)
    {
        if (binds is null) return new(null, "Elite's bindings are not known yet.", []);
        List<AxisRoute> routes = [];
        Dictionary<string, StickCurve> onCurves = [], offCurves = [];
        List<string> notes = [];
        foreach (string control in Turning)
        {
            if (binds.Axes.GetValueOrDefault(control) is not { } bound) { notes.Add($"{control} is not bound in Elite."); continue; }
            if (!bound.Device.Equals("vJoy", StringComparison.OrdinalIgnoreCase)) { notes.Add($"{control} is read from {bound.Device}, not from vJoy: bind it to vJoy."); continue; }
            if (realSource(control) is not { } real) { notes.Add($"No stick was found for {control}."); continue; }
            if (bound.Deadzone > 0.005) notes.Add($"Elite adds its own dead zone of {Math.Round(bound.Deadzone * 100)}% on {control}, on top of the curve's: set it to 0 in Elite.");
            routes.Add(new AxisRoute(real.Device, real.Letter, bound.Axis));
            onCurves[bound.Axis] = on.Of(control);
            offCurves[bound.Axis] = off.Of(control);
        }
        if (routes.Count == 0) return new(null, "Nothing can go through Gimbalyn: " + string.Join(" ", notes), notes);

        Dictionary<string, Dictionary<string, StickCurve>> modes = new() { [OnMode] = onCurves };
        if (switching) modes[OffMode] = offCurves;
        // A toggle on a keyboard key: Gimbalyn moves to the other mode on each press, at once. Held, or on a stick button, only the game's own flag switches.
        FlightButton? fa = binds.Buttons.GetValueOrDefault("fa")?.FirstOrDefault(b => b.Press.Device == "Keyboard" && b.Hold.Count == 0);
        bool key = switching && fa is not null && binds.Toggles.GetValueOrDefault("fa");
        return new(new GimbalynProfile
        {
            Device = 1,
            Routes = routes,
            Modes = modes,
            Mode = switching && faOff ? OffMode : OnMode,
            ModeKey = key ? fa!.Press.Key : null,
            ModeKeyOnlyWith = key ? Elite : null,
            VJoyFolder = vjoyFolder,
        }, null, notes);
    }
}

/// <summary>What Studio wants of Gimbalyn this moment (see <see cref="FlightShaper.Update"/>).</summary>
/// <param name="On">The player wants Studio to shape the stick and feed vJoy; off, Gimbalyn is let go.</param>
/// <param name="Switching">Switch the curves with the game's Flight Assist (see <see cref="ShaperProfile.Build"/>).</param>
/// <param name="FaOff">Flight Assist is off in the game now.</param>
/// <param name="RealSource">The real stick behind pitch, roll or yaw: its device and Windows' letter; null when none is found.</param>
/// <param name="Practice">This is a practice copy of Studio: it never starts or talks to Gimbalyn, whatever else is asked (Gimbalyn's pipe and lock are the real Studio's).</param>
public sealed record ShaperAsk(bool On, bool Switching, bool FaOff, FlightBinds? Binds, FlightCurves OnCurves, FlightCurves OffCurves,
    Func<string, (string Device, string Letter)?> RealSource, string VJoyFolder, bool Practice = false);

/// <summary>
/// Studio's dealings with a running Gimbalyn for Flight Assist: starts it when it is not running, hands it the profile once
/// (again when it changed, or when Gimbalyn was closed and started afresh), tells it the mode when the game's Flight
/// Assist changes, and lets it go when the player turns it off. The process and the pipe are handed in, so tests use stand-ins.
/// </summary>
/// <param name="centre">Centres vJoy once when a Gimbalyn that was feeding is found gone (the axes stay where it left them); null for none.</param>
public sealed class FlightShaper(Func<bool> running, Func<bool> launch, Func<JsonObject, Task<JsonObject?>> send, Action<string> log, int waitMs = 3000, Action? centre = null)
{
    const int CheckMs = 1000;   // how often Gimbalyn's mode is looked at
    const int RetryMs = 5000;   // how long a profile that could not be fed (vJoy held by another program...) waits before the next try
    const int AgreeChecks = 2;  // how many checks in a row must find the mode wrong before it is put right (the game's flag lags a key press)
    string? fed;        // the profile as last sent and taken, as JSON without the mode: the mode follows Flight Assist and is sent on its own
    string? tried, problem;   // the profile last tried and why it could not be fed
    string? trouble;    // what Gimbalyn said about its state when it was not feeding
    long retryAt, checkAt, startAt, quitAt, fedAt, clock;   // clock: the time of the latest look, as the caller counts it
    string? lastMode;   // the mode Gimbalyn is in, as far as Studio knows
    bool switching;     // the profile fed has a mode for Flight Assist off
    int wrongChecks;    // checks in a row that found Gimbalyn in another mode than the game's Flight Assist wants

    /// <summary>Gimbalyn's state as it last said ("running", "busy": another program holds vJoy, "noVJoy"...); null before it was asked, or when it is not wanted.</summary>
    public string? State { get; private set; }
    /// <summary>The mode Gimbalyn last said it is in ("FA On", "FA Off"); null as <see cref="State"/>.</summary>
    public string? Mode { get; private set; }
    /// <summary>What the last look found in the way of routing: controls left out, Elite's own dead zone... (see <see cref="ShaperProfile.Made.Notes"/>).</summary>
    public IReadOnlyList<string> Notes { get; private set; } = [];

    void Seen(JsonObject? answer)
    {
        if (answer?["status"] is not JsonObject status) return;
        State = (string?)status["state"] ?? State;
        Mode = (string?)status["mode"] ?? Mode;
        trouble = State is "busy" or "noVJoy" or "stopped" ? (string?)status["problem"] ?? $"Gimbalyn is {State}." : null;
    }

    /// <summary>Gimbalyn is there and fed, but is not feeding vJoy (another program took it, the driver went away, Windows slept): it only starts again when asked.</summary>
    bool NotFeeding => State is "busy" or "noVJoy" or "stopped";

    /// <summary>
    /// One look, as the app makes it: <see cref="Update"/>, then <see cref="Follow"/> (also while Update has something to say: a
    /// Gimbalyn that was fed and does not feed vJoy now is only started again by Follow). The answer is Update's.
    /// </summary>
    public async Task<string?> Look(ShaperAsk ask, long now)
    {
        string? why = await Update(ask, now);
        if (ask.On && !ask.Practice) await Follow(ask.FaOff, now);
        return why;
    }

    /// <summary>
    /// Looks at what is wanted and does what that needs, as often as asked (the profile is sent only when it changed, a try that
    /// failed is repeated every few seconds): null when Gimbalyn feeds vJoy (or is rightly let go), else why not, in words.
    /// </summary>
    public async Task<string?> Update(ShaperAsk ask, long now)
    {
        if (ask.Practice) return "A practice copy of Studio never starts Gimbalyn: the real Studio's is the one.";
        clock = now;
        if (!ask.On)
        {
            // Let go once: also of a Gimbalyn that came up after a try that was not answered in time (nothing was fed, but it may feed now).
            // A quit it did not take is asked again every few seconds while it runs.
            if ((fed is not null || tried is not null) && running())
            {
                if (now < quitAt) return null;
                quitAt = now + RetryMs;
                if (Failed(await send(new JsonObject { ["command"] = "quit" })) is not null && running()) return null;
            }
            (fed, tried, problem, trouble, lastMode, State, Mode, Notes, wrongChecks, quitAt) = (null, null, null, null, null, null, null, [], 0, 0);
            return null;
        }
        ShaperProfile.Made made = ShaperProfile.Build(ask.Binds, ask.RealSource, ask.OnCurves, ask.OffCurves, ask.Switching, ask.FaOff, ask.VJoyFolder);
        Notes = made.Notes;
        if (made.Profile is null) return made.Problem;
        string key = Key(made.Profile);
        if (fed is not null && !running())
        {
            // Gimbalyn was feeding and is gone (it crashed, it was ended): vJoy keeps the axes where it left them, so centre them at once
            // and start it again without waiting, unless it ended just after it was fed (a start that keeps failing waits its few seconds).
            log("Gimbalyn ended while it was feeding");
            centre?.Invoke();
            (fed, lastMode, State, Mode, trouble, wrongChecks) = (null, null, null, null, null, 0);
            (tried, retryAt) = (key, fedAt + RetryMs);
        }
        if (key == tried && now < retryAt)
        {
            // A try that failed waits; so does starting Gimbalyn again when it ended just after it was fed.
            if (problem is not null) return problem;
            if (!running()) return "Gimbalyn stopped. Studio starts it again in a few seconds.";
        }
        tried = key;
        problem = await Use(made.Profile);
        retryAt = now + RetryMs;
        return problem;
    }

    static string Key(GimbalynProfile profile) => JsonSerializer.Serialize(profile with { Mode = "" }, Host.Json);

    /// <summary>Feeds the profile: null when Gimbalyn took it and feeds vJoy, else why not (said in words for the player).</summary>
    public async Task<string?> Use(GimbalynProfile profile)
    {
        string json = JsonSerializer.Serialize(profile, Host.Json), key = Key(profile);
        if (running())
        {
            if (key == fed) return NotFeeding ? trouble : null;   // Follow starts it again; until it feeds, the player is told why not
        }
        else
        {
            (fed, lastMode, State, Mode, trouble) = (null, null, null, null, null);
            if (!launch()) return "Studio could not start Gimbalyn, its stick shaper.";
            for (int waited = 0; !running() && waited < waitMs; waited += 100) await Task.Delay(100);
            if (!running()) return "Gimbalyn did not come up.";
            log("started Gimbalyn");
        }
        JsonObject? answer = await send(new JsonObject { ["command"] = "profile", ["profile"] = JsonNode.Parse(json) });
        if (Failed(answer) is { } why) return why;
        answer = await send(new JsonObject { ["command"] = "start" });
        if (Failed(answer) is { } stopped) return stopped;
        Seen(answer);
        if ((string?)answer!["status"]?["state"] is { } state && state != "running")
            return (string?)answer["status"]?["problem"] ?? $"Gimbalyn is {state}.";
        // The mode it is in, as it says: with a profile it already has the mode in use (a name both have) stays in use, which may not be the one asked for.
        (fed, lastMode, switching, wrongChecks) = (key, (string?)answer["status"]?["mode"] ?? profile.Mode, profile.Modes.ContainsKey(ShaperProfile.OffMode), 0);
        fedAt = clock;
        return null;
    }

    /// <summary>
    /// Keeps Gimbalyn's mode the one for the game's Flight Assist (when the profile has the two). A change moves it at once (again
    /// if it refused); besides, once a second Gimbalyn is asked how it is (its state, and which mode it is in) and the mode put
    /// right when it differs: its own key only cycles, so a press the game did not take (a menu, a chat line) would leave the two
    /// out of step for good. Call it with every look, not only when Flight Assist changed.
    /// </summary>
    public async Task Follow(bool faOff, long now)
    {
        if (fed is null) return;
        string mode = faOff ? ShaperProfile.OffMode : ShaperProfile.OnMode;
        if (switching && mode != lastMode) { wrongChecks = 0; await Move(mode, now); return; }
        if (now < checkAt) return;
        checkAt = now + CheckMs;
        JsonObject? answer = await send(new JsonObject { ["command"] = "status" });
        if (Failed(answer) is not null) return;
        Seen(answer);
        if (NotFeeding) { await Restart(now); return; }
        // Its own key moves Gimbalyn at once, but the game's flag lags the press by a moment: one look that finds the two apart may
        // be that, so the mode is only put right when the next look finds them apart still.
        if (switching && (string?)answer!["status"]?["mode"] is { } actual && actual != mode)
        {
            if (++wrongChecks >= AgreeChecks) { wrongChecks = 0; await Move(mode, now); }
        }
        else wrongChecks = 0;
    }

    /// <summary>Gimbalyn took the profile but does not feed vJoy now (it only tries again when asked): ask it to start, every few seconds.</summary>
    async Task Restart(long now)
    {
        if (now < startAt) return;
        startAt = now + RetryMs;
        JsonObject? answer = await send(new JsonObject { ["command"] = "start" });
        if (Failed(answer) is { } why) { trouble = why; return; }
        Seen(answer);
        if (!NotFeeding) { problem = null; log("Gimbalyn feeds vJoy again"); }
    }

    async Task Move(string mode, long now)
    {
        checkAt = now + CheckMs;
        JsonObject? answer = await send(new JsonObject { ["command"] = "mode", ["mode"] = mode });
        if (Failed(answer) is not null) return;
        lastMode = mode;
        Seen(answer);
    }

    static string? Failed(JsonObject? answer) =>
        answer is null ? "Gimbalyn did not answer."
        : answer["ok"]?.GetValue<bool>() == true ? null
        : (string?)answer["error"] ?? "Gimbalyn did not take that.";
}
