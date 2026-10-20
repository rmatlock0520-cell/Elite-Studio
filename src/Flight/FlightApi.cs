using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Overlay;
using StudioSettings = EliteStudio.App.Settings;

namespace EliteStudio.Flight;

/// <summary>One ship as the trainer flies it: top and boost speed (m/s), how fast it turns (degrees a second, at its best speed) and the seconds between boosts.</summary>
public sealed record TrainerShip(string Id, string Name, double Speed, double Boost, double Pitch, double Roll, double Yaw, double BoostEvery);

/// <summary>
/// Studio Flight, for flying with Flight Assist off: the routes its pages use, the stream of the
/// sticks for the trainer, the flight overlay, and the overlay showing by itself while Flight Assist
/// is off.
///
/// Routes: GET /api/flight (everything the pages show: settings, the game's flight bindings,
/// Gremlin's profile, the ships, Flight Assist now); POST /api/flight/settings (the named settings
/// changed); POST /api/flight/progress ({"exercise":"chase-1","successes":3,"attempts":5}, one set of
/// the trainer flown); POST /api/flight/stream (the stick readings as "flight-input" events for the
/// next few seconds); POST /api/flight/gremlin (writes the FA Off profile for Joystick Gremlin);
/// GET /api/flight/vjoy (looks for vJoy and says what state it is in, see <see cref="VJoyState"/>),
/// POST /api/flight/vjoy/folder (the player points to vJoy's folder by hand, the only way to set it),
/// POST /api/flight/vjoy/forget (forgets that folder) and POST /api/flight/vjoy/configure (opens vJoy's
/// own settings program). The live event "flight" carries
/// what GET /api/flight answers whenever any of it changes.
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

    readonly Func<string?, VJoyInstall> findVJoy;
    readonly Func<string, Task<VJoyProbeResult?>> askVJoy;

    /// <param name="overlay">The overlay, for its flight box; null for none.</param>
    /// <param name="gremlinProfile">Where Gremlin's profile is (null: the one Gremlin last opened); tests hand in their own.</param>
    /// <param name="sticks">How sticks are read; tests hand in their own (see <see cref="FlightReader"/>).</param>
    /// <param name="findVJoy">How vJoy is looked for in what Windows keeps (null: <see cref="VJoyFinder.Look"/>); tests hand in their own.</param>
    /// <param name="askVJoy">How vJoy's library is asked (null: Studio's helper, <see cref="VJoyHelper.Ask"/>); tests hand in their own.</param>
    public FlightApi(WebServer web, StudioSettings settings, GameState game, OverlayHost? overlay, string bindingsDir, Func<string?>? gameDir,
        Func<string?>? gremlinProfile = null, Func<bool, IReadOnlyList<StickState>>? sticks = null, Func<int, bool>? held = null,
        Func<string?, VJoyInstall>? findVJoy = null, Func<string, Task<VJoyProbeResult?>>? askVJoy = null)
    {
        (this.web, file, this.game, this.overlay, this.bindingsDir, this.gameDir) = (web, settings, game, overlay, bindingsDir, gameDir);
        this.gremlinProfile = gremlinProfile ?? (() => Gremlin.LastProfile());
        this.findVJoy = findVJoy ?? VJoyFinder.Look;
        this.askVJoy = askVJoy ?? VJoyHelper.Ask;
        try
        {
            Current = settings.Get<FlightSettings>(Section).Cleaned();
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
        web.Map("POST", "/api/flight/stream", _ =>
        {
            reader.Stream();
            return WebResponse.Json(new { ok = true });
        });
        web.Map("POST", "/api/flight/gremlin", _ => SaveGremlin());
        // vJoy: looked for only when the Stick setup page asks (never at start-up, so a player with no stick never loads any of it).
        web.Map("GET", "/api/flight/vjoy", async _ => WebResponse.Json(await CheckVJoy()));
        web.Map("POST", "/api/flight/vjoy/folder", _ => PickVJoyFolder());
        web.Map("POST", "/api/flight/vjoy/forget", _ => ForgetVJoyFolder());
        web.Map("POST", "/api/flight/vjoy/configure", _ => OpenConfigureVJoy());

        game.Changed += OnGameChanged;
        if (overlay is not null)
        {
            _ = new FlightOverlay(overlay, reader, () => controls, game);
            // The player showed or hid the flight box with its key: from now on it is theirs, not Flight Assist's.
            overlay.KeyPressed += action => { if (action == OverlaySettings.ShowOnly && Current.ShownForFaOff) Save(Current with { ShownForFaOff = false }); };
            // Switching Flight Assist changes only a flag in Status.json, which is no change of the game's state to the rest
            // of Studio: so it is looked at here four times a second, with the overlay's own clock.
            overlay.Ticked += CheckFlightAssist;
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
        bool off = game.Status.FlightAssistOff && (game.Status.InMainShip || game.Status.InFighter);
        if (off == faOff) return;
        faOff = off;
        ShowForFlightAssist(off);
        web.Send(Section, Answer());
    }

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
        // The turning controls whose Gremlin curve Studio cannot read exactly (a spline, a lopsided one...).
        string[] unread = [.. ((string[])["pitch", "roll", "yaw"]).Where(control => binds?.Axes.GetValueOrDefault(control) is { } bound
            && bound.Device.Equals("vJoy", StringComparison.OrdinalIgnoreCase) && Gremlin.Through(gremlin.Axes, bound.Axis) is { CurveUnread: true })];
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
                faOffFile = exported is not null && File.Exists(exported) ? exported : null,
                unread,
            },
            ships = Ships(),
            flying = game.Ship is { Length: > 0 } code && ShipOf(code) is { } ship ? ship.Id : null,
            flyingName = now.ShipType,
            faOff = game.Status.FlightAssistOff,
            inShip = game.Status.InMainShip || game.Status.InFighter,
            gameRunning = now.GameRunning,
        };
    }

    // ------------------------------------------------------------ routes

    WebResponse Change(WebRequest request)
    {
        FlightSettings changed;
        try
        {
            if (JsonNode.Parse(request.Body) is not JsonObject given) throw new JsonException("Send the settings to change as one JSON object.");
            JsonObject all = JsonSerializer.SerializeToNode(Current, StudioSettings.Json)!.AsObject();
            foreach ((string name, JsonNode? value) in given)
                // Not these: the progress has its own route, the marker is Studio's, and vJoy's folder is set only through
                // Windows' folder picker (POST /api/flight/vjoy/folder), which checks it is vJoy's and on this PC.
                if (all.ContainsKey(name) && name is not "progress" and not "shownForFaOff" and not "vJoyFolder") all[name] = value?.DeepClone();
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
        if (!System.Text.RegularExpressions.Regex.IsMatch(set.Exercise ?? "", "^(chase|tunnel|track)-[1-3]$") || set.Attempts is < 1 or > 5 || set.Successes < 0 || set.Successes > set.Attempts)
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

    void Save(FlightSettings changed)
    {
        Current = changed.Cleaned();
        file.Set(Section, Current);
        Rebuild();
        web.Send(Section, Answer());
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
        (XDocument profile, IReadOnlyList<string> more) = Gremlin.WithFaOff(LoadXml(path), wanted, fa, toggles);
        // Nothing of Studio's in it (no axis reached, no switch Gremlin can take): no file, and the reasons why.
        if (!Gremlin.IsStudios(profile))
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
