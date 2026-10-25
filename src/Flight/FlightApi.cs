using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Gimbalyn;
using EliteStudio.Overlay;
using StudioSettings = EliteStudio.App.Settings;

namespace EliteStudio.Flight;

/// <summary>One ship as the trainer flies it: top and boost speed (m/s), how fast it turns (degrees a second, at its best speed) and the seconds between boosts.</summary>
public sealed record TrainerShip(string Id, string Name, double Speed, double Boost, double Pitch, double Roll, double Yaw, double BoostEvery);

/// <summary>
/// One round kept for its game's "Last rounds" (flight-rounds.json): when it was flown (Studio's clock, "2026-10-22T14:32:05.120"),
/// its score (Chase the dot: the share of the round on the dot, in percent) or result (a trainer attempt: good or not, and why),
/// the curve set it was flown with ("faOff" or "faOn") and that set's numbers as flown, by control, and the page's record of
/// the round (flight.js's Flight.recorder, as the page sent it: Studio only keeps it, the page draws it).
/// </summary>
public sealed record FlightRound(string Time, double? Pct, bool? Ok, string? Why, string Set, Dictionary<string, StickCurve> Curves, JsonObject Record);

/// <summary>
/// Studio Flight, for flying with Flight Assist off: the routes its pages use, the stream of the
/// sticks for the trainer, the flight overlay, and the overlay showing by itself while Flight Assist
/// is off.
///
/// Routes: GET /api/flight (everything the pages show: settings, the game's flight bindings,
/// Gremlin's profile, the ships, Flight Assist now); POST /api/flight/settings (the named settings
/// changed); POST /api/flight/progress ({"exercise":"chase-1","successes":3,"attempts":5}, one set of
/// the trainer flown); POST /api/flight/score ({"game":"dot2-medium-faOff","pct":62.5}, a round of Chase the dot
/// played, its best kept); GET /api/flight/rounds?game=dot2-medium-faOff and POST /api/flight/rounds (the last ten rounds
/// of each game, see <see cref="FlightRound"/>); POST /api/flight/stream (the stick readings as "flight-input" events for the
/// next few seconds); POST /api/flight/gremlin (writes the FA Off profile for Joystick Gremlin);
/// GET /api/flight/vjoy (looks for vJoy and says what state it is in, see <see cref="VJoyState"/>),
/// POST /api/flight/vjoy/folder (the player points to vJoy's folder by hand, the only way to set it),
/// POST /api/flight/vjoy/forget (forgets that folder) and POST /api/flight/vjoy/configure (opens vJoy's
/// own settings program). The live event "flight" carries
/// what GET /api/flight answers whenever any of it changes. The "shaper" of that answer is Studio's own stick shaper
/// (Gimbalyn, see <see cref="FlightShaper"/>), switched on and off with the setting "shaperOn" and made to follow Flight Assist
/// with "switchCurvesWithFa".
/// </summary>
public sealed class FlightApi : IDisposable
{
    const string Section = "flight";

    readonly WebServer web;
    readonly StudioSettings file;
    readonly GameState game;
    readonly OverlayHost? overlay;
    readonly string bindingsDir;
    readonly Func<string?>? gameDir;
    readonly Func<string?> gremlinProfile;
    readonly FlightReader reader;
    KeyBindings bindingsSeen = KeyBindings.None;   // the game's bindings the flight ones were last read for
    FlightBinds? binds;
    string? bindsProblem;
    (string? Path, DateTime Written, IReadOnlyList<GremlinAxis> Axes, string? Problem) gremlin = (null, default, [], null);
    string? gremlinNamed;
    long gremlinLooked;
    FlightControls controls;
    bool? faOff;   // Flight Assist off at the last look; null before the first, so the first look always counts

    // Studio's own stick shaper (Gimbalyn) for the Flight Assist curves: looked at four times a second, with the overlay's clock.
    readonly FlightShaper shaper;
    readonly bool shaperPractice;   // a practice copy, or no data folder to reach Gimbalyn through: the shaper is not for it
    readonly Func<bool, IReadOnlyList<StickState>> readShaperSticks;
    IReadOnlyList<StickState>? shaperSticks;   // the sticks as listed lately; null before the first listing
    long shaperSticksAt;
    int shaperLooking;       // 1 while a look at the shaper is on its way
    Task shaperLook = Task.CompletedTask;   // that look
    string? shaperProblem;   // why it does not feed vJoy, as the last look found
    string shaperSeen = "";  // what the pages were last told of it

    readonly Func<string?, VJoyInstall> findVJoy;
    readonly Func<string, Task<VJoyProbeResult?>> askVJoy;
    readonly string? roundsFile;   // flight-rounds.json in the data folder; null: the rounds are kept for this run only
    Dictionary<string, List<FlightRound>>? rounds;   // by game, oldest first, never changed in place; read from the file at the first ask

    /// <param name="overlay">The overlay, for its flight box; null for none.</param>
    /// <param name="gremlinProfile">Where Gremlin's profile is (null: the one Gremlin last opened); tests hand in their own.</param>
    /// <param name="sticks">How sticks are read; tests hand in their own (see <see cref="FlightReader"/>).</param>
    /// <param name="findVJoy">How vJoy is looked for in what Windows keeps (null: <see cref="VJoyFinder.Look"/>); tests hand in their own.</param>
    /// <param name="askVJoy">How vJoy's library is asked (null: Studio's helper, <see cref="VJoyHelper.Ask"/>); tests hand in their own.</param>
    /// <param name="dataDir">Studio's data folder, where the last rounds are kept (flight-rounds.json) and Gimbalyn is found; null keeps the rounds for this run only and has no shaper.</param>
    /// <param name="shaper">How Gimbalyn is started and spoken to (null: the real one, see <see cref="GimbalynClient"/>); tests hand in their own.</param>
    public FlightApi(WebServer web, StudioSettings settings, GameState game, OverlayHost? overlay, string bindingsDir, Func<string?>? gameDir,
        Func<string?>? gremlinProfile = null, Func<bool, IReadOnlyList<StickState>>? sticks = null, Func<int, bool>? held = null,
        Func<string?, VJoyInstall>? findVJoy = null, Func<string, Task<VJoyProbeResult?>>? askVJoy = null, string? dataDir = null,
        FlightShaper? shaper = null)
    {
        (this.web, file, this.game, this.overlay, this.bindingsDir, this.gameDir) = (web, settings, game, overlay, bindingsDir, gameDir);
        string shaperDir = dataDir ?? "";
        this.shaper = shaper ?? new FlightShaper(() => GimbalynClient.Running(shaperDir), GimbalynClient.Launch, request => GimbalynClient.Send(shaperDir, request),
            text => Log.Info("Studio Flight: " + text), centre: () => GimbalynClient.CentreOnce());
        shaperPractice = shaper is null && (dataDir is null || Paths.Practice);
        readShaperSticks = sticks ?? new WindowsSticks().Read;
        this.gremlinProfile = gremlinProfile ?? (() => Gremlin.LastProfile());
        this.findVJoy = findVJoy ?? VJoyFinder.Look;
        this.askVJoy = askVJoy ?? VJoyHelper.Ask;
        roundsFile = dataDir is null ? null : Path.Combine(Path.GetFullPath(dataDir), RoundsFile);
        try
        {
            Current = KeptOn(settings.Get<FlightSettings>(Section)).Cleaned();
        }
        catch (Exception problem)   // a hand-edited section must never stop Studio from starting
        {
            Log.Error("settings.json: the \"flight\" section could not be used, so its defaults are in use", problem);
            Current = new FlightSettings().Cleaned();
        }
        controls = new FlightControls(null, [], Current);
        reader = new FlightReader(sticks, held, frame => web.Send("flight-input", controls.Take(frame)));
        Refresh(force: true);

        web.Map("GET", "/api/flight", _ =>
        {
            Refresh(force: true);   // Gremlin's file may have been changed in Gremlin since
            return WebResponse.Json(Answer());
        });
        web.Map("POST", "/api/flight/settings", Change);
        web.Map("POST", "/api/flight/progress", Progress);
        web.Map("POST", "/api/flight/score", Score);
        web.Map("GET", "/api/flight/rounds", request => IsGame(request.Query.GetValueOrDefault("game"), out string game)
            ? WebResponse.Json(new { game, rounds = Rounds().GetValueOrDefault(game) ?? [] })
            : WebResponse.Error("That is not a game of Chase the dot or of the trainer.", 400));
        web.Map("POST", "/api/flight/rounds", KeepRound);
        web.Map("POST", "/api/flight/stream", _ =>
        {
            reader.Stream();
            return WebResponse.Json(new { ok = true });
        });
        web.Map("POST", "/api/flight/gremlin", _ => SaveGremlin());
        // vJoy: looked for only when the Options page asks (never at start-up, so a player with no stick never loads any of it).
        web.Map("GET", "/api/flight/vjoy", async _ => WebResponse.Json(await CheckVJoy()));
        web.Map("POST", "/api/flight/vjoy/folder", _ => PickVJoyFolder());
        web.Map("POST", "/api/flight/vjoy/forget", _ => ForgetVJoyFolder());
        web.Map("POST", "/api/flight/vjoy/configure", _ => OpenConfigureVJoy());

        game.Changed += OnGameChanged;
        if (overlay is not null)
        {
            _ = new FlightOverlay(overlay, reader, () => controls, game);
            // "Overlay on/off" off: the flight box never shows, by its key, a page or Flight Assist; one left showing goes now.
            overlay.MayShow = box => box != OverlaySettings.ShowOnly || Current.ShowWhenFaOff;
            if (overlay.Settings.Box(OverlaySettings.ShowOnly).On && !Current.ShowWhenFaOff) overlay.Apply(overlay.Settings);
            // The player showed or hid the flight box with its key: from now on it is theirs, not Flight Assist's.
            overlay.KeyPressed += action => { if (action == OverlaySettings.ShowOnly && Current.ShownForFaOff) Save(Current with { ShownForFaOff = false }); };
            // Switching Flight Assist changes only a flag in Status.json, which is no change of the game's state to the rest
            // of Studio: so it is looked at here four times a second, with the overlay's own clock.
            overlay.Ticked += CheckFlightAssist;
            overlay.Ticked += LookAtShaper;
        }
        reader.Start();
    }

    /// <summary>The settings in use.</summary>
    public FlightSettings Current { get; private set; }

    public void Dispose()
    {
        game.Changed -= OnGameChanged;
        reader.Dispose();
    }

    /// <summary>
    /// Reads the game's flight bindings again when the game's bindings changed, and Gremlin's profile
    /// when its file changed (or always, with force), and builds the controls from them.
    /// </summary>
    void Refresh(bool force = false)
    {
        bool changed = false;
        if (force || !ReferenceEquals(game.Bindings, bindingsSeen))
        {
            bindingsSeen = game.Bindings;
            try
            {
                FlightBinds? read = FlightBinds.Load(bindingsDir, gameDir);
                changed |= read != binds;
                (binds, bindsProblem) = (read, read is null ? "Elite has not written its control settings yet: start Elite once, or open Options › Controls." : null);
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                bindsProblem = "Elite's control settings could not be read just now: " + problem.Message;   // the ones read before stay
            }
        }
        // Which profile Gremlin last opened is looked up now and then (it means reading Gremlin's settings), its file's time every time.
        if (force || Environment.TickCount64 - gremlinLooked > 10_000) (gremlinNamed, gremlinLooked) = (gremlinProfile(), Environment.TickCount64);
        string? path = gremlinNamed;
        DateTime written = path is not null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        if (force || path != gremlin.Path || written != gremlin.Written)
        {
            try
            {
                gremlin = path is null ? (null, default, [], null) : (path, written, Gremlin.Axes(LoadXml(path)), null);
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException or FormatException)
            {
                gremlin = (path, written, [], problem.Message);
            }
            changed = true;
        }
        if (changed || force) Rebuild();
    }

    void Rebuild()
    {
        controls = new FlightControls(binds, gremlin.Axes, Current);
        reader.Watch(controls.Keys);
    }

    static XDocument LoadXml(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return XDocument.Load(stream);
    }

    void OnGameChanged()
    {
        KeyBindings before = bindingsSeen;
        Refresh();
        if (!ReferenceEquals(before, bindingsSeen)) web.Send(Section, Answer());
        CheckFlightAssist();
    }

    /// <summary>Flight Assist switched in the game (or this is the first look): the pages are told, and the flight box follows it.</summary>
    internal void CheckFlightAssist()
    {
        bool off = FlightAssistIsOff();
        if (off == faOff) return;
        faOff = off;
        ShowForFlightAssist(off);
        web.Send(Section, Answer());
        LookAtShaper();
    }

    /// <summary>Flight Assist is off in the game now, in a ship or a fighter.</summary>
    bool FlightAssistIsOff() => game.Status.FlightAssistOff && (game.Status.InMainShip || game.Status.InFighter);

    void LookAtShaper() => _ = KeepShaper();

    /// <summary>
    /// Looks at the shaper: what is wanted of it (the settings, Elite's bindings, the curves, the real stick behind each control,
    /// Flight Assist now) goes to <see cref="FlightShaper"/>, which starts Gimbalyn, hands it the profile and keeps its mode the
    /// game's. One look at a time (a look that finds another running leaves it), and the pages are told when what they show of it
    /// changed. Turned off, nothing here touches Gimbalyn but to let it go once.
    /// </summary>
    internal Task KeepShaper()
    {
        if (Interlocked.Exchange(ref shaperLooking, 1) == 1) return shaperLook;   // (a caller may wait for the look on its way)
        return shaperLook = Look();
    }

    async Task Look()
    {
        try
        {
            long now = Environment.TickCount64;
            // The sticks are listed now and then (off the window's thread: looking at all of winmm's places is slow), not every look.
            if (Current.ShaperOn && (shaperSticks is null || now - shaperSticksAt >= SticksEveryMs))
            {
                shaperSticks = await Task.Run(() => readShaperSticks(true));
                shaperSticksAt = now;
            }
            FlightSettings wish = Current;
            FlightControls read = controls;
            IReadOnlyList<StickState> listed = shaperSticks ?? [];
            (FlightCurves on, FlightCurves off) = Curves();
            bool flightAssistOff = FlightAssistIsOff();
            var ask = new ShaperAsk(wish.ShaperOn, wish.SwitchCurvesWithFa, flightAssistOff, binds, on, off, control => read.RealSource(control, listed), wish.VJoyFolder, shaperPractice);
            shaperProblem = await shaper.Look(ask, now);
            string seen = string.Join('|', wish.ShaperOn, shaperProblem, shaper.State, shaper.Mode, string.Join(';', ShaperNotes()));
            if (seen == shaperSeen) return;
            shaperSeen = seen;
            web.Send(Section, Answer());
        }
        catch (Exception problem) when (problem is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
        {
            Log.Error("Studio Flight: looking at the stick shaper failed", problem);
        }
        finally
        {
            Volatile.Write(ref shaperLooking, 0);
        }
    }

    const int SticksEveryMs = 3000;   // how often the sticks are listed for the shaper

    /// <summary>What the shaper's routing found worth saying, and for a Gremlin curve Studio cannot read, that the shaper would use Studio's default for it.</summary>
    IReadOnlyList<string> ShaperNotes()
    {
        if (!Current.ShaperOn) return [];
        List<string> notes = [.. shaper.Notes];
        if (Current.FaOn is null)
            foreach (string control in (string[])["pitch", "roll", "yaw"])
                if (binds?.Axes.GetValueOrDefault(control) is { } bound && bound.Device.Equals("vJoy", StringComparison.OrdinalIgnoreCase)
                    && Gremlin.Through(gremlin.Axes, bound.Axis) is { CurveUnread: true })
                    notes.Add($"Your Gremlin curve for {control} is not one Studio can read, so Studio's shaper would use its default: save your own Flight Assist on curve on Stick setup.");
        return notes;
    }

    /// <summary>What GET answers of the stick shaper (see the "shaper" of <see cref="Answer"/>).</summary>
    object ShaperAnswer() => new
    {
        on = Current.ShaperOn,
        switching = Current.SwitchCurvesWithFa,
        practice = shaperPractice,
        // Gimbalyn's own word ("running", "busy": another program, as Gremlin, holds vJoy; "noVJoy"), and whether it feeds vJoy now.
        state = shaper.State,
        running = Current.ShaperOn && shaperProblem is null && shaper.State == "running",
        mode = shaper.Mode,
        problem = Current.ShaperOn ? shaperProblem : null,
        notes = ShaperNotes(),
    };

    /// <summary>
    /// Flight Assist went off: the flight overlay comes up by itself (when the player wants that). It came back on: the
    /// overlay goes again if it came up by itself, which is kept with the settings so a restart in between does not lose it.
    /// </summary>
    void ShowForFlightAssist(bool off)
    {
        if (overlay is null) return;
        BoxLook box = overlay.Settings.Box(OverlaySettings.ShowOnly);
        if (off && Current.ShowWhenFaOff && !box.On)
        {
            overlay.Apply(overlay.Settings.With(OverlaySettings.ShowOnly, box with { On = true }));
            Save(Current with { ShownForFaOff = true });
        }
        else if (!off && Current.ShownForFaOff)
        {
            if (box.On) overlay.Apply(overlay.Settings.With(OverlaySettings.ShowOnly, box with { On = false }));
            Save(Current with { ShownForFaOff = false });
        }
    }

    /// <summary>
    /// The curves in use: the player's own, else for Flight Assist on Gremlin's (Default mode) and for off a starting point
    /// made from those. A Gremlin axis with no curve passes the stick on as it is; one with a curve Studio cannot read
    /// exactly is shown as Studio's default (and is never written back, see <see cref="SaveGremlin"/>).
    /// </summary>
    internal (FlightCurves On, FlightCurves Off) Curves()
    {
        StickCurve FromGremlin(string control) =>
            binds?.Axes.GetValueOrDefault(control) is { } bound && bound.Device.Equals("vJoy", StringComparison.OrdinalIgnoreCase)
            && Gremlin.Through(gremlin.Axes, bound.Axis) is { } through
                ? through.Curve ?? (through.CurveUnread ? new StickCurve() : new StickCurve(0, 0, 100, 100, 0))
                : new StickCurve();
        FlightCurves on = Current.FaOn ?? new FlightCurves(FromGremlin("pitch"), FromGremlin("roll"), FromGremlin("yaw"));
        // With Flight Assist off the stick sets how hard the ship turns, not how fast: full authority to stop a spin,
        // and a softer middle for the small touches that hold the nose still.
        static StickCurve Off(StickCurve c) => c with { SatY = 100, Bend = Math.Max(c.Bend, 35) };
        FlightCurves off = Current.FaOff ?? new FlightCurves(Off(on.Pitch), Off(on.Roll), Off(on.Yaw));
        return (on, off);
    }

    /// <summary>What GET answers and the "flight" event carries.</summary>
    object Answer()
    {
        (FlightCurves on, FlightCurves off) = Curves();
        string? exported = gremlin.Path is { } p ? Gremlin.TargetFor(p) : null;
        string? faOffFile = exported is not null && File.Exists(exported) ? exported : null;
        // The turning controls whose Gremlin curve Studio cannot read exactly (a spline, a lopsided one...).
        string[] unread = [.. ((string[])["pitch", "roll", "yaw"]).Where(control => binds?.Axes.GetValueOrDefault(control) is { } bound
            && bound.Device.Equals("vJoy", StringComparison.OrdinalIgnoreCase) && Gremlin.Through(gremlin.Axes, bound.Axis) is { CurveUnread: true })];
        // What Studio saved for Gremlin, read back (the profile Gremlin opens may be that very file).
        IReadOnlyList<GremlinAxis>? saved = faOffFile is null ? null : faOffFile == gremlin.Path ? gremlin.Axes : SavedAxes(faOffFile);
        (bool Off, bool On) older = saved is not null && binds is not null ? Older(saved, binds, Current.FaOn, off, gremlin.Axes, Current.SwitchCurvesWithFa) : default;
        GameSnapshot now = game.Snapshot();
        return new
        {
            settings = Current with { FaOn = on, FaOff = off },
            saved = new { faOn = Current.FaOn is not null, faOff = Current.FaOff is not null },
            binds,
            bindsProblem,
            trainerKeys = controls.TrainerOnly,
            gremlin = new
            {
                profile = gremlin.Path,
                axes = gremlin.Axes,
                problem = gremlin.Problem,
                modes = gremlin.Axes.Select(a => a.Mode).Distinct(),
                faOffFile,
                unread,
                // The profile Gremlin opens has the FA Off mode: without it the Flight Assist off curves tuned here never reach Elite.
                hasFaOff = gremlin.Axes.Any(a => a.Mode == Gremlin.FaOffMode),
                // The FA Off profile saved before no longer has the curves in use: saving it again brings it up to date. And which
                // curves changed since: the Flight Assist off ones (its FA Off mode), the on ones (its Default mode), or both.
                outOfDate = older.Off || older.On,
                older = new { faOff = older.Off, faOn = older.On },
            },
            shaper = ShaperAnswer(),
            ships = Ships(),
            flying = game.Ship is { Length: > 0 } code && ShipOf(code) is { } ship ? ship.Id : null,
            flyingName = now.ShipType,
            faOff = game.Status.FlightAssistOff,
            inShip = game.Status.InMainShip || game.Status.InFighter,
            gameRunning = now.GameRunning,
        };
    }

    /// <summary>
    /// The switches a player of an earlier Studio already had on, kept on: those Studios had them on by themselves, and only a
    /// fresh Studio starts with them off. For the curves: a flight section saved without "switchCurvesWithFa" (1.9.2 to 1.9.5,
    /// before that switch), or Studio's FA Off copy of the Gremlin profile with its FA Off mode (saving it saves no flight
    /// settings). For "Overlay on/off": the flight box left up (its key saves no flight settings either). A switch saved either
    /// way stays as saved.
    /// </summary>
    FlightSettings KeptOn(FlightSettings saved)
    {
        if (!file.Has(Section, "switchCurvesWithFa") && (file.Has(Section) || SwitchedForGremlin()))
            saved = saved with { SwitchCurvesWithFa = true };
        if (!file.Has(Section, "showWhenFaOff") && overlay?.Settings.Box(OverlaySettings.ShowOnly).On == true)
            saved = saved with { ShowWhenFaOff = true };
        return saved;
    }

    /// <summary>Studio's FA Off copy of the player's Gremlin profile is there, with its FA Off mode.</summary>
    bool SwitchedForGremlin()
    {
        try
        {
            return gremlinProfile() is { } profile && Gremlin.TargetFor(profile) is var copy && File.Exists(copy)
                && SavedAxes(copy)?.Any(axis => axis.Mode == Gremlin.FaOffMode && axis.Studios) == true;
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;   // nothing to go by: as a fresh Studio
        }
    }

    /// <summary>The axes of a profile Studio saved for Gremlin; null when it cannot be read just now.</summary>
    static IReadOnlyList<GremlinAxis>? SavedAxes(string path)
    {
        try
        {
            return Gremlin.Axes(LoadXml(path));
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the FA Off profile Studio saved for Gremlin no longer has the curves in use, so saving again would change it, for the
    /// Flight Assist off curves (Off) and the on ones (On): for each of pitch, roll and yaw that Elite reads from vJoy and the
    /// profile feeds, its FA Off curves against the Flight Assist off curves, and its Default curve against what saving again
    /// would put there: the Flight Assist on curves when the player saved some here (a curve of the player's own that Studio
    /// cannot read stays as it is, so it is never counted), else the Default curve of the profile Gremlin opens, kept as it is
    /// (<paramref name="running"/>; so after "Back to the suggestion" a Default still holding the curves saved before is older).
    /// The comparison is of the curves, not of the files' times.
    /// </summary>
    internal static (bool Off, bool On) Older(IReadOnlyList<GremlinAxis> saved, FlightBinds binds, FlightCurves? on, FlightCurves off, IReadOnlyList<GremlinAxis> running,
        bool switching = true)
    {
        bool offOlder = false, onOlder = false;
        foreach (string control in (string[])["pitch", "roll", "yaw"])
        {
            if (binds.Axes.GetValueOrDefault(control) is not { } bound || !bound.Device.Equals("vJoy", StringComparison.OrdinalIgnoreCase)) continue;
            if (Gremlin.Through(saved, bound.Axis) is not { } main) continue;   // nothing feeds that axis: saving again cannot change it
            GremlinAxis[] faOff = [.. saved.Where(a => a.Mode == Gremlin.FaOffMode && a.VjoyAxis == bound.Axis)];
            // Not switching, a profile saved before with Studio's FA Off curves is older (saving again takes them out); a mode the player filled themself stays and is never old.
            offOlder |= switching ? faOff.Length == 0 || faOff.Any(a => a.Curve is not { } curve || !Same(curve, off.Of(control))) : faOff.Any(a => a.Studios);
            if (on is not null) onOlder |= !main.CurveUnread && (main.Curve is not { } mine || !Same(mine, on.Of(control)));
            else if (Gremlin.Through(running, bound.Axis) is { } own) onOlder |= !SameAs(main, own);
        }
        return (offOlder, onOlder);
    }

    /// <summary>Two Gremlin axes with the same curve as far as Studio can tell: both without one, both the same, or both unreadable (never counted).</summary>
    static bool SameAs(GremlinAxis a, GremlinAxis b) =>
        a.CurveUnread || b.CurveUnread ? a.CurveUnread == b.CurveUnread
            : a.Curve is { } x && b.Curve is { } y ? Same(x, y) : a.Curve is null && b.Curve is null;

    /// <summary>
    /// Two curves the same as far as a player can tell them apart: each number within a quarter. Read back from Gremlin's 21 points
    /// Studio's own curve comes out at most about a tenth off (the bend), and a page changes a number by half a step at the least.
    /// </summary>
    static bool Same(StickCurve a, StickCurve b) =>
        Math.Abs(a.DeadLow - b.DeadLow) < 0.25 && Math.Abs(a.DeadHigh - b.DeadHigh) < 0.25 && Math.Abs(a.SatX - b.SatX) < 0.25
        && Math.Abs(a.SatY - b.SatY) < 0.25 && Math.Abs(a.Bend - b.Bend) < 0.25;

    // ------------------------------------------------------------ routes

    /// <summary>
    /// What POST /api/flight/settings never changes: the progress and the scores have their own routes (which keep the best), the
    /// marker is Studio's, and vJoy's folder is set only through Windows' folder picker (POST /api/flight/vjoy/folder), which checks
    /// it is vJoy's and on this PC. By any spelling of the name, as the settings are read whatever the case of their names.
    /// </summary>
    static readonly HashSet<string> NotThroughSettings = new(["progress", "scores", "shownForFaOff", "vJoyFolder"], StringComparer.OrdinalIgnoreCase);

    WebResponse Change(WebRequest request)
    {
        FlightSettings changed;
        try
        {
            if (JsonNode.Parse(request.Body) is not JsonObject given) throw new JsonException("Send the settings to change as one JSON object.");
            JsonObject all = JsonSerializer.SerializeToNode(Current, StudioSettings.Json)!.AsObject();
            foreach ((string name, JsonNode? value) in given)
                if (all.ContainsKey(name) && !NotThroughSettings.Contains(name)) all[name] = value?.DeepClone();
            changed = all.Deserialize<FlightSettings>(StudioSettings.Json)!.Cleaned();
        }
        catch (JsonException problem)
        {
            return WebResponse.Error("These Studio Flight settings could not be read: " + problem.Message, 400);
        }
        Save(changed);
        return WebResponse.Json(Answer());
    }

    /// <summary>
    /// Looks for vJoy: what Windows keeps (read off the UI thread), then vJoy's library through the helper when a library
    /// was found, and the verdict for the axes Elite's flight bindings use on vJoy.
    /// </summary>
    internal async Task<VJoyState> CheckVJoy()
    {
        string chosen = Current.VJoyFolder;
        VJoyInstall found;
        try
        {
            found = await Task.Run(() => findVJoy(chosen));
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Error("Studio Flight: looking for vJoy failed", problem);
            found = VJoyInstall.None;
        }
        VJoyProbeResult? probe = found.Dll is { } dll ? await askVJoy(dll) : null;
        (string[] need, bool fromBinds) = VJoyState.AxesFor(binds);
        return VJoyState.Judge(found, probe, need, fromBinds, chosen);
    }

    /// <summary>The player points Studio to vJoy's folder by hand (Windows' folder picker, over the Studio window).</summary>
    async Task<WebResponse> PickVJoyFolder()
    {
        App.MainWindow? window = null;
        try
        {
            window = System.Windows.Application.Current?.MainWindow as App.MainWindow;
        }
        catch (InvalidOperationException) { }   // asked from another thread than the window's (tests): no window here
        if (window is null) return WebResponse.Error("Studio has no window to show the folder picker over.", 409);
        window.BringToFront();
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder vJoy is installed in" };
        if (dialog.ShowDialog(window) != true) return WebResponse.Json(await CheckVJoy());
        string picked = dialog.FolderName;
        if (await Task.Run(() => VJoyFinder.WhyNotVJoy(picked)) is { } why) return WebResponse.Error(why, 400);
        Save(Current with { VJoyFolder = picked });
        return WebResponse.Json(await CheckVJoy());
    }

    /// <summary>Forgets the folder the player picked by hand, and looks again where Windows says.</summary>
    async Task<WebResponse> ForgetVJoyFolder()
    {
        if (Current.VJoyFolder.Length > 0) Save(Current with { VJoyFolder = "" });
        return WebResponse.Json(await CheckVJoy());
    }

    /// <summary>Opens vJoy's own settings program ("Configure vJoy"), which asks Windows for permission itself.</summary>
    async Task<WebResponse> OpenConfigureVJoy()
    {
        string chosen = Current.VJoyFolder;
        VJoyInstall found = await Task.Run(() => findVJoy(chosen));
        if (found.Configure is not { } program || !VJoyFinder.LocalFolder(Path.GetDirectoryName(program)))
            return WebResponse.Error("Studio found no \"Configure vJoy\" program (vJoyConf.exe) in vJoy's folder. Open it from the Start menu, or install vJoy again with its configuration program.", 404);
        try
        {
            // Off the UI thread: Windows' permission question holds the starting thread until it is answered, and Studio's
            // pages and overlay must not stop meanwhile.
            await Task.Run(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = true })?.Dispose());
            return WebResponse.Json(new { ok = true });
        }
        catch (System.ComponentModel.Win32Exception problem)
        {
            // 1223: the player said no to Windows' permission question.
            return WebResponse.Error(problem.NativeErrorCode == 1223 ? "Windows did not open Configure vJoy: permission was not given." : "Configure vJoy did not open: " + problem.Message, 409);
        }
    }

    sealed class SetBody
    {
        public string Exercise { get; set; } = "";
        public int Successes { get; set; }
        public int Attempts { get; set; }
    }

    /// <summary>One set of an exercise flown: counted, its best kept, and passed with three of five.</summary>
    WebResponse Progress(WebRequest request)
    {
        SetBody set = request.Read<SetBody>();
        if (!IsExercise(set.Exercise) || set.Attempts is < 1 or > 5 || set.Successes < 0 || set.Successes > set.Attempts)
            return WebResponse.Error("That is not a set of the trainer.", 400);
        ExerciseRecord was = Current.Progress.GetValueOrDefault(set.Exercise!) ?? new ExerciseRecord();
        bool passed = set.Successes >= 3;
        ExerciseRecord now = was with
        {
            Sets = was.Sets + 1,
            Best = Math.Max(was.Best, set.Successes),
            Passed = was.Passed || passed,
            PassedOn = was.PassedOn ?? (passed ? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null),
        };
        Save(Current with { Progress = new Dictionary<string, ExerciseRecord>(Current.Progress) { [set.Exercise!] = now } });
        return WebResponse.Json(Answer());
    }

    sealed class RoundBody
    {
        public string Game { get; set; } = "";
        public double? Pct { get; set; }
    }

    /// <summary>A round of Chase the dot played: its score (the share of the round on the dot, in percent) kept when it is the best of that game.</summary>
    WebResponse Score(WebRequest request)
    {
        RoundBody round;
        try
        {
            round = request.Read<RoundBody>();
        }
        catch (JsonException)
        {
            round = new RoundBody();
        }
        if (!FlightSettings.IsDotGame(round.Game) || round.Pct is not { } pct || !double.IsFinite(pct) || pct is < 0 or > 100)
            return WebResponse.Error("That is not a round of Chase the dot.", 400);
        double best = Math.Max(Current.Scores.GetValueOrDefault(round.Game) ?? 0, pct);
        Save(Current with { Scores = new Dictionary<string, double?>(Current.Scores) { [round.Game] = best } });
        return WebResponse.Json(Answer());
    }

    // ------------------------------------------------------------ the last ten rounds of each game

    const string RoundsFile = "flight-rounds.json";
    /// <summary>The rounds kept of each game, and the most a round sent may be (Flight.recorder's longest record, a four-minute tunnel run, is about 23 KB).</summary>
    const int RoundsKept = 10, RoundBytes = 32 * 1024;
    static readonly JsonSerializerOptions RoundsJson = new(StudioSettings.Json) { WriteIndented = false };

    /// <summary>An exercise of the trainer: "chase-1" to "track-3".</summary>
    static bool IsExercise(string? id) => System.Text.RegularExpressions.Regex.IsMatch(id ?? "", "^(chase|tunnel|track)-[1-3]$");

    /// <summary>A game whose last rounds are kept: one of Chase the dot ("dot2-medium-faOff") or an exercise of the trainer ("tunnel-2").</summary>
    static bool IsGame(string? id, out string game) => FlightSettings.IsDotGame(game = id ?? "") || IsExercise(game);

    sealed class SentRound
    {
        public string Game { get; set; } = "";
        public double? Pct { get; set; }
        public bool? Ok { get; set; }
        public string? Why { get; set; }
        public string Set { get; set; } = "";
        public Dictionary<string, StickCurve?>? Curves { get; set; }
        public JsonObject? Record { get; set; }
    }

    /// <summary>
    /// A round of Chase the dot played, or an attempt of the trainer flown: kept with the last ones of its game, the oldest going
    /// when there are more than ten. The file is written whole beside the old one and swapped in, as settings.json is, so a write
    /// that fails leaves the rounds kept before whole, and Studio keeps the ones it had. Answers the game's rounds as kept now.
    /// </summary>
    WebResponse KeepRound(WebRequest request)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(request.Body) > RoundBytes)
            return WebResponse.Error($"That round is too big to keep: Studio keeps rounds of up to {RoundBytes / 1024} KB.", 400);
        SentRound sent;
        try
        {
            sent = request.Read<SentRound>();
        }
        catch (Exception wrong) when (wrong is JsonException or InvalidOperationException)
        {
            sent = new SentRound();
        }
        bool dot = FlightSettings.IsDotGame(sent.Game);
        Dictionary<string, StickCurve> curves = (sent.Curves ?? []).Where(p => p.Key is "pitch" or "roll" or "yaw" && p.Value is not null)
            .ToDictionary(p => p.Key, p => p.Value!.Cleaned());
        if (!IsGame(sent.Game, out string game) || (dot ? sent.Pct is not (>= 0 and <= 100) : sent.Ok is null)
            || sent.Set is not ("faOff" or "faOn") || curves.Count == 0 || sent.Record is null)
            return WebResponse.Error("That is not a round of Chase the dot or of the trainer.", 400);
        string why = (sent.Why ?? "").Trim();
        // To the thousandth of a second: a page tells the rounds apart by it.
        var round = new FlightRound(DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture), dot ? sent.Pct : null,
            dot ? null : sent.Ok, dot ? null : why[..Math.Min(why.Length, 200)], sent.Set, curves, sent.Record);
        var all = new Dictionary<string, List<FlightRound>>(Rounds()) { [game] = [.. (Rounds().GetValueOrDefault(game) ?? []).Append(round).TakeLast(RoundsKept)] };
        if (roundsFile is not null)
        {
            string temp = roundsFile + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(all, RoundsJson));
                File.Move(temp, roundsFile, overwrite: true);
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                try
                {
                    File.Delete(temp);   // what was written of the new file (a folder in its way is not Studio's to take away)
                }
                catch (Exception left) when (left is IOException or UnauthorizedAccessException) { }
                Log.Error("Studio Flight: a round could not be kept in " + roundsFile, problem);
                return WebResponse.Error("Studio could not keep this round: " + problem.Message, 500);
            }
        }
        rounds = all;
        return WebResponse.Json(new { game, rounds = all[game] });
    }

    /// <summary>
    /// The rounds kept, by game, oldest first: read from flight-rounds.json at the first ask. The file may have been edited by hand:
    /// a game Studio does not have, an entry with nothing in it and rounds past the last ten are left out. One Studio cannot read is
    /// kept beside it for the owner to send in (flight-rounds.json.unreadable), as settings.json is, and the rounds start again.
    /// </summary>
    Dictionary<string, List<FlightRound>> Rounds()
    {
        if (rounds is not null) return rounds;
        if (roundsFile is null || !File.Exists(roundsFile)) return rounds = [];
        try
        {
            var read = JsonSerializer.Deserialize<Dictionary<string, List<FlightRound?>?>>(File.ReadAllText(roundsFile), RoundsJson) ?? [];
            return rounds = read.Where(p => IsGame(p.Key, out _) && p.Value is not null).ToDictionary(p => p.Key, p => p.Value!
                .Where(r => r is { Time: not null, Set: not null, Curves: not null, Record: not null }).Select(r => r!).TakeLast(RoundsKept).ToList());
        }
        catch (JsonException problem)
        {
            File.Move(roundsFile, roundsFile + ".unreadable", overwrite: true);
            Log.Error("flight-rounds.json could not be read; it was kept as flight-rounds.json.unreadable and the last rounds start again", problem);
            return rounds = [];
        }
    }

    void Save(FlightSettings changed)
    {
        bool overlaySwitched = changed.ShowWhenFaOff != Current.ShowWhenFaOff;
        Current = changed.Cleaned();
        file.Set(Section, Current);
        Rebuild();
        if (overlaySwitched) FollowOverlaySwitch();
        web.Send(Section, Answer());
        LookAtShaper();   // a switch turned, or a curve changed: the shaper hears of it now, not at the next tick
    }

    /// <summary>
    /// "Overlay on/off" was turned. Off: the flight box goes, and it no longer came up by itself. On: it may show again, and
    /// comes up at once when Flight Assist is off now, as it would have when Flight Assist went off. The pages are told.
    /// </summary>
    void FollowOverlaySwitch()
    {
        if (overlay is null) return;
        if (!Current.ShowWhenFaOff && Current.ShownForFaOff)
        {
            Current = Current with { ShownForFaOff = false };
            file.Set(Section, Current);
        }
        overlay.Apply(overlay.Settings);   // through MayShow again
        if (Current.ShowWhenFaOff && faOff == true) ShowForFlightAssist(true);
    }

    /// <summary>
    /// Writes Gremlin's profile with the FA Off mode beside the player's own, and says where. Default keeps the player's
    /// own curves unless they saved Flight Assist on curves in Studio: those it writes, nothing else of Default changes.
    /// </summary>
    WebResponse SaveGremlin()
    {
        Refresh(force: true);
        if (gremlin.Path is not { } path) return WebResponse.Error("Studio found no Joystick Gremlin profile. Open your profile in Joystick Gremlin once, then try again.", 409);
        if (binds is null) return WebResponse.Error(bindsProblem ?? "Elite's control settings are not known.", 409);
        (_, FlightCurves off) = Curves();
        var wanted = new Dictionary<string, (string, StickCurve?, StickCurve)>();
        var notes = new List<string>();
        foreach (string control in (string[])["pitch", "roll", "yaw"])
        {
            if (binds.Axes.GetValueOrDefault(control) is not { } bound || !bound.Device.Equals("vJoy", StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"In Elite, {control} is not bound to Gremlin's vJoy, so Gremlin's curves do not reach it. Studio left it out.");
                continue;
            }
            // A curve of the player's that Studio cannot read is never replaced: what Studio shows for it is only its default.
            bool unread = Gremlin.Through(gremlin.Axes, bound.Axis) is { CurveUnread: true };
            if (unread && Current.FaOn is not null) notes.Add($"Your Gremlin curve for {control} is not one Studio can read, so Default keeps it as it is.");
            wanted[control] = (bound.Axis, unread ? null : Current.FaOn?.Of(control), off.Of(control));
        }
        // Every way Flight Assist is bound, the keyboard's first: Gremlin follows a key most surely.
        IReadOnlyList<FlightButton> fa = [.. (binds.Buttons.GetValueOrDefault("fa") ?? []).OrderBy(b => b.Press.Device == "Keyboard" ? 0 : 1)];
        bool toggles = binds.Toggles.GetValueOrDefault("fa", true);
        (XDocument profile, IReadOnlyList<string> more) = Gremlin.WithFaOff(LoadXml(path), wanted, fa, toggles, Current.SwitchCurvesWithFa);
        // Nothing of Studio's in it (no axis reached, no switch Gremlin can take): no file, and the reasons why. Except that a file
        // Studio wrote earlier, with the switch, is made plain again when the player turned the switch off: that is what they asked.
        bool takingBack = !Current.SwitchCurvesWithFa && Gremlin.TargetFor(path) is var target && File.Exists(target) && Gremlin.IsStudiosFile(target);
        if (!Gremlin.IsStudios(profile) && !takingBack)
            return WebResponse.Error("There was nothing to save for Gremlin. " + string.Join(" ", notes.Concat(more)), 409);
        string saved = Gremlin.Save(profile, path);
        Log.Info($"Studio Flight: Gremlin profile with the FA Off mode written to {saved}");
        Refresh(force: true);
        web.Send(Section, Answer());
        return WebResponse.Json(new { ok = true, path = saved, notes = notes.Concat(more) });
    }

    // ------------------------------------------------------------ ships

    static readonly Lazy<IReadOnlyList<TrainerShip>> ships = new(() =>
    {
        var list = new List<TrainerShip>();
        foreach (JsonProperty ship in GameData.Coriolis.GetProperty("ships").EnumerateArray().SelectMany(f => f.EnumerateObject()))
        {
            JsonElement about = ship.Value.GetProperty("properties");
            double Number(string name) => about.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            if (Number("pitch") <= 0 || Number("speed") <= 0) continue;
            list.Add(new TrainerShip(ship.Name, about.GetProperty("name").GetString() ?? ship.Name, Number("speed"), Number("boost"),
                Number("pitch"), Number("roll"), Number("yaw"), Number("boostInt") is > 0 and var every ? every : 5));
        }
        return [.. list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    });

    /// <summary>Every ship the trainer can fly like, from the community's ship table.</summary>
    public static IReadOnlyList<TrainerShip> Ships() => ships.Value;

    /// <summary>The trainer's ship for the journal's ship code ("cobramkiii").</summary>
    internal static TrainerShip? ShipOf(string code) =>
        Loadouts.ShipData.Hulls.GetValueOrDefault(code) is { } hull ? Ships().FirstOrDefault(s => s.Id == hull.Table) : null;
}
