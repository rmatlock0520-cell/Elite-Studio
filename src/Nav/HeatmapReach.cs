using System.Text.Json.Serialization;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Nav;

/// <summary>Where "Within reach" lists from: the commander's place when the list was made (<see cref="ReachView.Centre"/>).</summary>
/// <param name="X">In light years, with <paramref name="Y"/> and <paramref name="Z"/>.</param>
/// <param name="System">The system the commander was in; null when the game did not say.</param>
public sealed record ReachCentre(double X, double Y, double Z, string? System);

/// <summary>A system within reach (<see cref="ReachView.Systems"/>), as the detailed map or Spansh has it.</summary>
/// <param name="Id64">The game's own number for the system; 0 when the list gave none. Written as text, because these numbers can be larger than a web page can hold exactly.</param>
/// <param name="X">Where it is, in light years (with <paramref name="Y"/> and <paramref name="Z"/>).</param>
/// <param name="Distance">Light years from the commander, to two decimals.</param>
/// <param name="State">
/// <see cref="HeatmapReach.Recorded"/>; <see cref="HeatmapReach.NameOnly"/> when the detailed map has its name and
/// place but not its star type; or what the 3D map's memory of checks says of it when it says more
/// (<see cref="HeatmapReach.NotRecorded"/> included).
/// </param>
public sealed record ReachSystem(
    string Name,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Id64,
    double X, double Y, double Z, double Distance, string State);

/// <summary>
/// A system that must exist within reach (<see cref="ReachView.MustExist"/>): a gap in the numbering of one of the
/// game's cubes that reaches into the sphere. The game numbers the systems of each of its cubes 0, 1, 2 and so on with
/// no gaps, so a number below the highest known that no list has is a system nobody has recorded.
/// </summary>
/// <param name="Name">Its name, built as the names panel builds the names to try (<see cref="HeatmapNames"/>): what to type in the galaxy map's search.</param>
/// <param name="Id64">The game's number for it, worked out from its cube and its number. Written as text.</param>
/// <param name="X">The middle of its cube, in light years (with <paramref name="Y"/> and <paramref name="Z"/>).</param>
/// <param name="PlusMinus">It lies within this many light years of that point along each of x, y and z: half its cube.</param>
/// <param name="Distance">Light years from the commander to that point, to two decimals.</param>
/// <param name="Certainty">
/// <see cref="HeatmapReach.Probably"/>: its whole cube is inside the sphere, so it is within reach; <see cref="HeatmapReach.Maybe"/>:
/// the cube is partly inside, and no wider than the sphere, so it may be. (A wider cube's are counted, not listed:
/// <see cref="ReachView.InLargeCubes"/>.)
/// </param>
public sealed record ReachMustExist(
    string Name,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Id64,
    double X, double Y, double Z, double PlusMinus, double Distance, string Certainty)
{
    /// <summary>What the 3D map's memory of checks says of it (<see cref="HeatmapReach.Recorded"/> or <see cref="HeatmapReach.NotRecorded"/>: somebody recorded it since the list was made, or a check found it is still not recorded); null when it has nothing.</summary>
    public string? State { get; init; }
}

/// <summary>"Within reach" as it stands (GET /api/nav/heatmap/reach).</summary>
/// <param name="On">The switch (setting "withinReach").</param>
/// <param name="RangeLy">The jump range in use, in light years; null while it is not known.</param>
/// <param name="From"><see cref="HeatmapReach.FromShip"/> (the ship's own) or <see cref="HeatmapReach.FromYou"/> (typed by the player: setting "reachLy").</param>
/// <param name="At">When the list was made (UTC); null without one.</param>
/// <param name="Centre">Where it was made from; null without one.</param>
/// <param name="Source"><see cref="HeatmapReach.FromDetailed"/> (the detailed map on this PC), <see cref="HeatmapReach.FromSpansh"/>, or null without a list.</param>
/// <param name="Capped">Spansh knows more systems within reach than it sends at once (500): the list holds the nearest 500.</param>
/// <param name="Systems">The systems within reach, nearest first, the nearest <see cref="HeatmapReach.MostListed"/> of them.</param>
/// <param name="MustExist">The systems that must exist within reach, nearest first (by their cube's middle), <see cref="HeatmapReach.MostListed"/> at most.</param>
/// <param name="Problem">What stands in the way, in the player's words; null when nothing does.</param>
public sealed record ReachView(
    bool On, double? RangeLy, string From, DateTime? At, ReachCentre? Centre, string? Source, bool Capped,
    [property: JsonPropertyOrder(1)] IReadOnlyList<ReachSystem> Systems,
    [property: JsonPropertyOrder(1)] IReadOnlyList<ReachMustExist> MustExist,
    string? Problem)
{
    /// <summary>
    /// <see cref="HeatmapReach.Off"/>, <see cref="HeatmapReach.Idle"/> (on, with nothing to list: see <see cref="Problem"/>),
    /// <see cref="HeatmapReach.Listing"/>, <see cref="HeatmapReach.Waiting"/> (its request waits its turn behind another of the
    /// 3D map's) or <see cref="HeatmapReach.Done"/>.
    /// </summary>
    [JsonPropertyOrder(-1)]
    public string State { get; init; } = HeatmapReach.Off;

    /// <summary>Systems within reach past the <see cref="HeatmapReach.MostListed"/> listed.</summary>
    public int MoreSystems { get; init; }

    /// <summary>Systems that must exist within reach past those listed (those no name can be given for included).</summary>
    public long MoreMustExist { get; init; }

    /// <summary>
    /// Systems that must exist in the game's cubes that reach into the sphere but are wider than it (the cubes are 10
    /// to 1,280 ly wide, a jump some 30): somewhere in a cube far bigger than the reach, so most likely out of it.
    /// Counted here, not listed, and not among <see cref="MoreMustExist"/> (the page may say "N more in large cubes
    /// that reach into range").
    /// </summary>
    public long InLargeCubes { get; init; }
}

/// <summary>
/// News of "Within reach", sent as a "heatmap" event with kind "reach" whenever it changes: what GET reach gives
/// without the lists (which a page asks for with GET reach), with how many there are.
/// </summary>
/// <param name="Systems">Systems within reach, all told.</param>
/// <param name="MustExist">Systems that must exist within reach, all told.</param>
public sealed record ReachNews(
    string State, bool On, double? RangeLy, string From, DateTime? At, ReachCentre? Centre, string? Source, bool Capped,
    int Systems, long MustExist, string? Problem)
{
    /// <summary>Always "reach": it tells these events from those of a survey, of the detailed map, of the game's target or of the route's check.</summary>
    [JsonPropertyOrder(-1)]
    public string Kind => "reach";

    /// <summary>As <see cref="ReachView.InLargeCubes"/>: not among <see cref="MustExist"/>.</summary>
    public long InLargeCubes { get; init; }
}

/// <summary>
/// "Within reach" (the owner, 15 October 2026): after every arrival, the systems one jump away, those known and those
/// that must exist, with what the 3D map's memory of checks says of each. A player looking for systems nobody has
/// recorded sees at once which are in reach of the next jump.
///
/// The range is the ship's own for the load it carries (<see cref="ShipMath.JumpRanges"/> with the cargo on board, as
/// the game's Loadout gives the drive), or the one the player typed. The list is made after every arrival (FSDJump,
/// CarrierJump, Location), when the switch is turned on, and when the range changes:
/// - inside the detailed map (<see cref="FineMap"/>) from its sector files, asking nobody: only the files of the
///   sectors the sphere reaches into are read, and what was read round the commander is kept for the next jumps
///   (<see cref="Region"/>), so a busy sector's half a million systems are not read again after every jump;
/// - outside it, with ONE request to Spansh (its system search, round the commander's place), one of the 3D map's own:
///   through its gate and counted in its 200 for the session. Never more: past 50 left, Within reach asks nothing, so
///   the route check and Nav scan keep what they need. Nothing is asked without the player's doing (a jump, the
///   switch, the range): the place Studio finds the commander in when it starts is listed from the detailed map only.
///   An answer is kept for the session: a jump back, or a smaller range, is answered from it;
/// - and the systems that must exist: the gaps in the numbering of the game's cubes that reach into the sphere, named
///   as the names panel names them, placed at their cube's middle to within half the cube. Only a cube no wider than the
///   sphere is listed when it is partly inside: the game's bigger cubes (160 to 1,280 ly) touch any sphere of a jump's
///   size, and their gaps are most likely hundreds of light years out of reach, so they are only counted.
/// Lives on the UI thread like the game state it follows; what it reads from disk is read away from it.
/// </summary>
public sealed class HeatmapReach
{
    /// <summary>What <see cref="ReachView.State"/> says.</summary>
    public const string Off = "off", Idle = "idle", Listing = "listing", Waiting = "waiting", Done = "done";

    /// <summary>What <see cref="ReachView.From"/> says.</summary>
    public const string FromShip = "ship", FromYou = "you";

    /// <summary>What <see cref="ReachView.Source"/> says.</summary>
    public const string FromDetailed = "detailed", FromSpansh = "spansh";

    /// <summary>What <see cref="ReachMustExist.Certainty"/> says.</summary>
    public const string Probably = "probably", Maybe = "maybe";

    /// <summary>What <see cref="ReachSystem.State"/> says.</summary>
    public const string Recorded = CheckMemory.Recorded, NotRecorded = CheckMemory.NotRecorded, NameOnly = "name-only";

    /// <summary>Systems (and systems that must exist) listed at most, the nearest: a page can show no more usefully.</summary>
    public const int MostListed = 2000;

    /// <summary>Of the 3D map's requests for the session, those Within reach leaves for the route check and Nav scan.</summary>
    public const int KeptForOthers = 50;

    /// <summary>The range a player may type, in light years.</summary>
    public const double LeastLy = 1, MostLy = 200;

    /// <summary>Why a typed range is none, in the player's words.</summary>
    public const string NoSuchRange = "A jump range is a number of light years from 1 to 200, or empty for your ship's own range.";

    /// <summary>
    /// Light years kept round the sphere when the detailed map is read: the next few jumps fall inside what was read,
    /// and are listed without reading a sector file again. Not more: round the bubble a ball of this size holds tens
    /// of thousands of systems, and Studio runs all day beside the game.
    /// </summary>
    const double PadLy = 100;

    /// <summary>Areas of the detailed map kept (the one round the commander and the one before), and Spansh's answers kept, at most (the least recently used go first).</summary>
    const int RegionsKept = 2, AnswersKept = 8;

    /// <summary>Names of a cube kept for naming its gaps (as <see cref="HeatmapNames"/> reads that many at most).</summary>
    const int NamesKept = 8;

    const string Section = "heatmap";

    const string NotHere = "Studio does not know where you are yet. Start Elite, and it will follow you.";
    const string NoRange = "Studio does not know your ship's jump range yet: the game tells it when it starts, and after outfitting. You can type a range instead.";
    const string AfterNextJump = "You are outside the detailed map, so Studio asks Spansh for the systems within reach, after your next jump (it asks nothing by itself when Studio starts).";
    const string Unexpected = "Studio could not list the systems within reach.";
    static readonly string KeptBack =
        $"Fewer than {KeptForOthers} of the 3D map's {HeatmapSurvey.SessionCap} requests to Spansh are left this session, and Within reach leaves those for the route check and Nav scan, so it asks Spansh nothing more until Studio is started again. Inside the detailed map it still lists the systems from this PC.";

    readonly GameState game;
    readonly HeatmapSurvey survey;
    readonly FineMap fine;
    readonly CheckMemory memory;
    readonly Settings? settings;
    readonly Lock guard = new();               // lists are made on other threads than the game's
    readonly List<Region> regions = [];        // what was read of the detailed map, the most recently used first
    readonly List<Answer> answers = [];        // Spansh's answers this session, the newest first
    bool on;
    double? typed;                             // the range the player typed; null for the ship's
    string state;
    string? idleWhy;                           // why there is nothing to list (state Idle)
    ReachRun? run;                             // the list being made; null for none
    Made? shown;                               // the list made last (state Done)
    bool primed;                               // the first look at the game's state (Studio's start) has been taken
    bool updated;                              // a list was begun (or found nothing to list) at least once
    double shipSeen;                           // the ship's range at the last look
    CancellationTokenSource? settling;         // a change of the ship's range waiting to settle
    Task idle = Task.CompletedTask;
    int sectorReads;

    /// <param name="game">The game state, whose arrivals and ship are followed from now on.</param>
    /// <param name="survey">The 3D map's questions to Spansh: its gate and its budget.</param>
    /// <param name="fine">The detailed map, read before Spansh is asked.</param>
    /// <param name="memory">The 3D map's memory of checks, which says what is known of each system listed.</param>
    /// <param name="settings">Where the switch and a typed range are kept (section "heatmap"); without it, only for the session.</param>
    public HeatmapReach(GameState game, HeatmapSurvey survey, FineMap fine, CheckMemory memory, Settings? settings = null)
    {
        (this.game, this.survey, this.fine, this.memory, this.settings) = (game, survey, fine, memory, settings);
        HeatmapSettings kept = settings?.Get<HeatmapSettings>(Section) ?? new HeatmapSettings();
        on = kept.WithinReach;
        // A range typed into the file by hand that the page would refuse is taken for none.
        typed = Typed(kept.ReachLy, out double? range) ? range : null;
        state = on ? Idle : Off;
        game.Journal += Follow;
        game.Changed += Look;
        // A detailed map built since may hold more than what was read before it.
        fine.Progress += progress =>
        {
            if (progress.State == FineMap.Done)
                lock (guard) regions.Clear();
        };
    }

    /// <summary>Raised on every change: a list begun, waiting its turn, made, the switch, the range. Raised on the thread the change is made on: in the app, the UI thread.</summary>
    public event Action<ReachNews>? News;

    /// <summary>The lists begun by the game's lines and changes and by the player, all of them; for tests, which wait for them.</summary>
    internal Task Pending
    {
        get
        {
            lock (guard) return idle;
        }
    }

    /// <summary>Sector files of the detailed map read so far; for tests.</summary>
    internal int SectorReads => Volatile.Read(ref sectorReads);

    /// <summary>How long a change of the ship's range waits to settle before the list is made again (cargo sold a commodity at a time comes in steps); tests wait none.</summary>
    internal TimeSpan RangeWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// "Within reach": on, the systems within reach are listed after every arrival (the place the commander is in now
    /// is listed at once: the player's click asks for it); off, nothing is listed or asked. Kept in settings.json.
    /// </summary>
    public bool On
    {
        get
        {
            lock (guard) return on;
        }
        set
        {
            ReachRun? old;
            lock (guard)
            {
                if (on == value) return;
                on = value;
                old = run;
                if (!value) (run, shown, state, idleWhy) = (null, null, Off, null);
            }
            Save("switch");
            if (value) Update(mayAsk: true);
            else
            {
                old?.Leave();
                Tell();
            }
        }
    }

    /// <summary>
    /// The range the player typed, in light years; null for the ship's own. Kept in settings.json. A new one lists
    /// again at once (the player's click asks for it).
    /// </summary>
    /// <exception cref="ArgumentException">Not from <see cref="LeastLy"/> to <see cref="MostLy"/>.</exception>
    public double? RangeLy
    {
        get
        {
            lock (guard) return typed;
        }
        set
        {
            if (!Typed(value, out double? range)) throw new ArgumentException(NoSuchRange, nameof(value));
            lock (guard)
            {
                if (typed == range) return;
                typed = range;
            }
            Save("jump range");
            Update(mayAsk: true);
        }
    }

    /// <summary>A typed range as it is kept: null for none, else from <see cref="LeastLy"/> to <see cref="MostLy"/>. False for anything else.</summary>
    internal static bool Typed(double? text, out double? range)
    {
        range = text;
        return text is null || (double.IsFinite(text.Value) && text.Value >= LeastLy && text.Value <= MostLy);
    }

    /// <summary>
    /// The ship's jump range for the load it carries: a full tank and the cargo on board (Studio's own reckoning from
    /// the game's Loadout, as Studio Market's), else the drive's best as the game gives it; 0 while the ship is not known.
    /// The ship's own hold counts (<see cref="GameState.ShipCargoCount"/>): while the commander drives the SRV the game's
    /// cargo file describes the SRV, and getting into it must neither change the range nor ask Spansh for a new list.
    /// </summary>
    public double ShipRangeLy
    {
        get
        {
            double laden = ShipMath.JumpRanges(game.MaxJumpRange, game.UnladenMass, game.FuelCapacity, game.FsdFuel, game.BoosterLy, game.ShipCargoCount).Laden;
            return laden > 0 ? laden : game.MaxJumpRange;
        }
    }

    /// <summary>The range in use, and where it comes from.</summary>
    (double Range, string From) RangeNow()
    {
        double? mine;
        lock (guard) mine = typed;
        return mine is { } range ? (range, FromYou) : (ShipRangeLy, FromShip);
    }

    /// <summary>The switch and the typed range into settings.json, beside the rest of the 3D map's, which are left as they are.</summary>
    void Save(string what)
    {
        if (settings is null) return;
        HeatmapSettings now = settings.Get<HeatmapSettings>(Section);
        lock (guard) (now.WithinReach, now.ReachLy) = (on, typed);
        try
        {
            settings.Set(Section, now);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Within reach's {what} could not be kept in settings.json; it holds until Studio is closed", problem);
        }
    }

    // ------------------------------------------------------------ what starts a list

    /// <summary>An arrival, as the game writes it: the systems within reach are listed. Lines read at start-up are history.</summary>
    void Follow(JournalLine line)
    {
        if (!line.Replay && line.Event is "FSDJump" or "CarrierJump" or "Location") Update(mayAsk: true);
    }

    /// <summary>
    /// The game's state changed. The first look, at Studio's start, lists the place the commander is in from the
    /// detailed map (asking nobody); after that, a change of the ship's range lists again once it has settled.
    /// </summary>
    void Look()
    {
        double ship = ShipRangeLy;
        if (!primed)
        {
            (primed, shipSeen) = (true, ship);
            // Not when an arrival of the game's has listed already (the game started after Studio: its lines are no history).
            bool listed;
            lock (guard) listed = updated;
            if (!listed) Update(mayAsk: false);
            return;
        }
        if (ship == shipSeen) return;
        shipSeen = ship;
        bool own;
        lock (guard) own = typed is null;
        if (!own) return;
        settling?.Cancel();
        settling = new CancellationTokenSource();
        Track(Later(settling.Token));
    }

    async Task Later(CancellationToken settled)
    {
        if (RangeWait > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(RangeWait, settled);
            }
            catch (OperationCanceledException)
            {
                return;   // it changed again: the newer change lists
            }
        }
        Update(mayAsk: true);
    }

    void Track(Task task)
    {
        lock (guard) idle = idle.IsCompleted ? task : Task.WhenAll(idle, task);
    }

    /// <summary>
    /// Lists the systems within reach of where the commander is now, in place of any list being made. Spansh is asked
    /// only when <paramref name="mayAsk"/> (the player's doing) and what is kept cannot answer.
    /// </summary>
    void Update(bool mayAsk)
    {
        ReachRun? old;
        lock (guard)
        {
            if (!on) return;
            (old, run, updated) = (run, null, true);
        }
        old?.Leave();
        (double range, string from) = RangeNow();
        string? why = game.StarPos is not [_, _, _] ? NotHere : !(range > 0) ? NoRange : null;
        if (why is not null)
        {
            lock (guard)
            {
                if (!on) return;
                (state, idleWhy, shown) = (Idle, why, null);
            }
            Tell();
            return;
        }
        double[] at = game.StarPos!;
        var mine = new ReachRun(new ReachCentre(at[0], at[1], at[2], game.StarSystem), range, from, mayAsk);
        lock (guard)
        {
            if (!on) return;
            (run, state, idleWhy) = (mine, Listing, null);
        }
        Tell();
        Track(Run(mine));
    }

    /// <summary>One list being made. Changed under the guard only.</summary>
    sealed class ReachRun(ReachCentre centre, double range, string from, bool mayAsk)
    {
        readonly CancellationTokenSource leaving = new();

        public ReachCentre Centre { get; } = centre;
        public double Range { get; } = range;
        public string From { get; } = from;

        /// <summary>The player's doing: Spansh may be asked when nothing kept answers.</summary>
        public bool MayAsk { get; } = mayAsk;

        /// <summary>Cancelled when a newer list takes its place, or the switch goes off: a request still waiting its turn at the gate is then never sent.</summary>
        public CancellationToken Leaving => leaving.Token;

        public void Leave() => leaving.Cancel();
    }

    bool Gone(ReachRun mine)
    {
        lock (guard) return run != mine;
    }

    /// <summary>One list, start to end. Runs by itself, off the game's line or change that began it: whatever goes wrong is logged and said, never thrown.</summary>
    async Task Run(ReachRun mine)
    {
        // The game's thread goes on at once.
        await Task.Yield();
        Made? made;
        try
        {
            made = await Make(mine);
        }
        catch (Exception unexpected)
        {
            Log.Error("3D map: the systems within reach could not be listed", unexpected);
            made = Nothing(mine, Unexpected);
        }
        if (made is null) return;   // gone before its request's turn: nothing was asked
        lock (guard)
        {
            if (run != mine) return;
            (run, shown, state) = (null, made, Done);
        }
        if (made.Problem is null)
            Log.Info($"3D map: {made.Systems.Count + made.MoreSystems} systems within {mine.Range:0.##} ly of {mine.Centre.System ?? "you"} ({made.Source}), {made.MustExist.Count + made.MoreMustExist} that must exist"
                + (made.InLargeCubes > 0 ? $" and {made.InLargeCubes} more in large cubes that reach into range" : ""));
        Tell();
    }

    /// <summary>The list: from the detailed map inside it, else from a kept answer of Spansh's, else (when the player's doing) from Spansh. Null when it went before its request's turn.</summary>
    async Task<Made?> Make(ReachRun mine)
    {
        ReachCentre at = mine.Centre;
        (int X, int Y, int Z)[] sectors = SectorsOf(at.X, at.Y, at.Z, mine.Range);
        // The block files say what the detailed map covers; a busy one runs to megabytes the first time: not on this thread.
        bool inside = await Task.Run(() => sectors.Length > 0 && sectors.All(sector => fine.Covers(sector.X, sector.Z)));
        if (Gone(mine)) return null;
        if (inside)
        {
            try
            {
                return await Task.Run(() => ListDetailed(mine, sectors));
            }
            catch (Exception unreadable) when (unreadable is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // A sector file that cannot be read says nothing either way: Spansh is asked instead, as outside the map.
                Log.Error("3D map: the detailed map could not be read for the systems within reach; Spansh is asked instead", unreadable);
            }
        }

        if (KeptAnswer(mine) is { } kept) return ListSpansh(mine, kept);
        if (!mine.MayAsk) return Nothing(mine, AfterNextJump);
        NearLookUp answer = await survey.SystemsNear(at.X, at.Y, at.Z, mine.Range, KeptForOthers, waiting =>
        {
            lock (guard)
            {
                if (run != mine) return;
                state = waiting ? Waiting : Listing;
            }
            Tell();
        }, mine.Leaving);
        if (answer.NotSent) return null;
        if (answer.Held) return Nothing(mine, answer.Problem ?? KeptBack);
        if (answer.Problem is { } problem)
        {
            // Not sent again: the next arrival asks afresh.
            Log.Error($"3D map: the systems within reach could not be asked of Spansh: {problem}");
            return Nothing(mine, problem);
        }
        var got = new Answer(at.X, at.Y, at.Z, mine.Range, answer.Found!, answer.Capped, DateTime.UtcNow);
        lock (guard)
        {
            answers.Insert(0, got);
            if (answers.Count > AnswersKept) answers.RemoveAt(answers.Count - 1);
        }
        return ListSpansh(mine, got);
    }

    /// <summary>A list with nothing in it, and why.</summary>
    static Made Nothing(ReachRun mine, string why) => new(mine.Centre, mine.Range, mine.From, DateTime.UtcNow, null, false, [], 0, [], 0, 0, why);

    // ------------------------------------------------------------ the lists

    /// <summary>A list made, ready for the page but for what the memory says of each system (looked up as the page asks).</summary>
    /// <param name="InLargeCubes">As <see cref="ReachView.InLargeCubes"/>.</param>
    sealed record Made(
        ReachCentre Centre, double Range, string From, DateTime At, string? Source, bool Capped,
        IReadOnlyList<Near> Systems, int MoreSystems, IReadOnlyList<ReachMustExist> MustExist, long MoreMustExist, long InLargeCubes, string? Problem);

    /// <summary>A system within reach: <paramref name="Star"/> says whether the detailed map knows its star type (null: Spansh, which lists recorded systems).</summary>
    readonly record struct Near(string Name, long Id64, double X, double Y, double Z, double Distance, bool? Star);

    /// <summary>One of the game's cubes, with the numbers known in it and some of its systems' names (for naming its gaps).</summary>
    sealed class Numbers
    {
        public readonly HashSet<long> Known = [];
        public readonly List<string> Names = [];
    }

    /// <summary>
    /// What was read of the detailed map round a place: the systems within <see cref="Radius"/> of it, from the files of
    /// <see cref="Sectors"/>, and the numbers known in each of the game's cubes that reach into that ball. A sphere
    /// inside the ball whose sectors were all read is listed from it.
    /// </summary>
    sealed record Region(double X, double Y, double Z, double Radius, HashSet<(int X, int Y, int Z)> Sectors, List<FineSystem> Systems, Dictionary<long, Numbers> Cubes);

    /// <summary>One of Spansh's answers this session: the systems within <paramref name="Radius"/> of a place, nearest first.</summary>
    sealed record Answer(double X, double Y, double Z, double Radius, IReadOnlyList<NamedSystem> Systems, bool Capped, DateTime At);

    /// <summary>The systems within reach from the detailed map: from what was read before when it covers the sphere, else from the sector files the sphere reaches into. Call away from the UI thread.</summary>
    /// <exception cref="InvalidDataException">A sector file is damaged or cut short.</exception>
    Made ListDetailed(ReachRun mine, (int X, int Y, int Z)[] sectors)
    {
        ReachCentre at = mine.Centre;
        double r = mine.Range;
        Region region = KeptRegion(at, r, sectors) ?? Read(at, r, sectors);
        var near = new List<Near>();
        foreach (FineSystem system in region.Systems)
        {
            double distance = Far(at, system.X, system.Y, system.Z);
            if (distance <= r) near.Add(new Near(system.Name, system.Id64, system.X, system.Y, system.Z, distance, system.Star));
        }
        var cubes = new List<Gaps>();
        foreach (var (cube, numbers) in region.Cubes)
            if (Gapped(cube, numbers.Known, numbers.Names, at, r, wholeOnly: false) is { } gaps) cubes.Add(gaps);
        return Shape(mine, FromDetailed, DateTime.UtcNow, false, near, cubes);
    }

    /// <summary>What was read of the detailed map before, when it covers this sphere; it becomes the most recently used.</summary>
    Region? KeptRegion(ReachCentre at, double r, (int X, int Y, int Z)[] sectors)
    {
        lock (guard)
        {
            Region? kept = regions.FirstOrDefault(region => Far(at, region.X, region.Y, region.Z) + r <= region.Radius + 1e-9 && sectors.All(region.Sectors.Contains));
            if (kept is not null)
            {
                regions.Remove(kept);
                regions.Insert(0, kept);
            }
            return kept;
        }
    }

    /// <summary>
    /// Reads the sector files the sphere reaches into, keeping the systems within <see cref="PadLy"/> more than the
    /// range, and the numbers known in each of the game's cubes that reach into that ball (from every system of the
    /// files, wherever it lies: a cube's numbers are all in its sector's file).
    /// </summary>
    Region Read(ReachCentre at, double r, (int X, int Y, int Z)[] sectors)
    {
        double padded = r + PadLy;
        var region = new Region(at.X, at.Y, at.Z, padded, [.. sectors], [], []);
        var reaches = new Dictionary<long, bool>();   // a cube of the game's, by its system 0: whether it reaches into the ball
        foreach (var (sx, sy, sz) in sectors)
        {
            Interlocked.Increment(ref sectorReads);
            foreach (FineSystem system in fine.Systems(sx, sy, sz))
            {
                if (Far(at, system.X, system.Y, system.Z) <= padded) region.Systems.Add(system);
                if (system.Id64 <= 0) continue;
                long cube = HeatmapNames.CubeOf(system.Id64);
                if (!reaches.TryGetValue(cube, out bool inside))
                    reaches[cube] = inside = SystemAddress.Place(cube) is { } place && Touches(place, at, padded);
                if (!inside) continue;
                if (!region.Cubes.TryGetValue(cube, out Numbers? numbers)) region.Cubes[cube] = numbers = new Numbers();
                numbers.Known.Add(HeatmapNames.NumberOf(system.Id64));
                if (numbers.Names.Count < NamesKept) numbers.Names.Add(system.Name);
            }
        }
        lock (guard)
        {
            regions.Insert(0, region);
            if (regions.Count > RegionsKept) regions.RemoveAt(regions.Count - 1);
        }
        return region;
    }

    /// <summary>An answer of Spansh's this session for the same place, reaching as far as this sphere: a jump back, or a smaller range.</summary>
    Answer? KeptAnswer(ReachRun mine)
    {
        ReachCentre at = mine.Centre;
        lock (guard) return answers.FirstOrDefault(answer => Far(at, answer.X, answer.Y, answer.Z) < 0.01 && answer.Radius >= mine.Range - 1e-9);
    }

    /// <summary>
    /// The systems within reach from one of Spansh's answers, and those that must exist in the game's cubes wholly
    /// inside the sphere (a cube reaching out of it may have systems Spansh did not send, so its gaps are not certain;
    /// nor are any of a cut-short answer's).
    /// </summary>
    Made ListSpansh(ReachRun mine, Answer answer)
    {
        ReachCentre at = mine.Centre;
        double r = mine.Range;
        var near = new List<Near>();
        bool beyond = false;
        foreach (NamedSystem system in answer.Systems)
        {
            if (system is not { X: { } x, Y: { } y, Z: { } z }) continue;
            double distance = Far(at, x, y, z);
            if (distance <= r + 1e-6) near.Add(new Near(system.Name, system.Id64, x, y, z, distance, null));
            else beyond = true;
        }
        // Cut short at the nearest 500: still so for a smaller sphere unless one of those 500 lies outside it.
        bool capped = answer.Capped && !beyond;
        var cubes = new List<Gaps>();
        if (!capped)
        {
            var numbers = new Dictionary<long, Numbers>();
            foreach (Near system in near.Where(system => system.Id64 > 0))
            {
                long cube = HeatmapNames.CubeOf(system.Id64);
                if (!numbers.TryGetValue(cube, out Numbers? known)) numbers[cube] = known = new Numbers();
                known.Known.Add(HeatmapNames.NumberOf(system.Id64));
                if (known.Names.Count < NamesKept) known.Names.Add(system.Name);
            }
            foreach (var (cube, known) in numbers)
                if (Gapped(cube, known.Known, known.Names, at, r, wholeOnly: true) is { } gaps) cubes.Add(gaps);
        }
        return Shape(mine, FromSpansh, answer.At, capped, near, cubes);
    }

    /// <summary>The gaps of one of the game's cubes that reaches into the sphere: its numbers below the highest known that no list has.</summary>
    /// <param name="Certainty"><see cref="Probably"/>, <see cref="Maybe"/>, or <see cref="Large"/> for a cube wider than the sphere, whose gaps are counted apart and not listed.</param>
    sealed record Gaps(CubePlace Zero, long Highest, HashSet<long> Known, Func<long, string>? Namer, string Certainty, double Distance)
    {
        public long Count => Highest + 1 - Known.Count;
    }

    /// <summary>What <see cref="Gaps.Certainty"/> says of a cube wider than the sphere that only reaches into it: its gaps are counted, not listed.</summary>
    const string Large = "large";

    /// <summary>
    /// A cube's gaps when it has any and reaches into the sphere (or lies wholly inside it, with <paramref name="wholeOnly"/>);
    /// null otherwise. A cube that only reaches into the sphere and is wider than it (the game's d to h cubes, 80 to
    /// 1,280 ly, against a jump of some 30) is <see cref="Large"/>: one of its gaps is far more likely out of reach than
    /// in it, so a row "maybe 600 ±640 ly" would only fill the list with systems the player cannot jump to.
    /// </summary>
    static Gaps? Gapped(long cube, HashSet<long> known, List<string> names, ReachCentre at, double r, bool wholeOnly)
    {
        if (known.Count == 0 || SystemAddress.Place(cube) is not { } zero) return null;
        long highest = known.Max();
        if (highest + 1 - known.Count <= 0) return null;
        string? certainty = Inside(zero, at, r) ? Probably
            : wholeOnly || !Touches(zero, at, r) ? null
            : zero.CubeLy <= 2 * r ? Maybe
            : Large;
        if (certainty is null) return null;
        (double cx, double cy, double cz) = zero.Centre;
        return new Gaps(zero, highest, known, HeatmapNames.NameOf(zero, names).Namer, certainty, Far(at, cx, cy, cz));
    }

    /// <summary>
    /// The lists as the page gets them: the nearest <see cref="MostListed"/> systems, and as many of the systems that
    /// must exist, nearest cube first, named (one that cannot be named is counted, not listed). The gaps of the large
    /// cubes that reach into the sphere are counted apart (<see cref="ReachView.InLargeCubes"/>).
    /// </summary>
    static Made Shape(ReachRun mine, string source, DateTime at, bool capped, List<Near> near, List<Gaps> cubes)
    {
        near.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Near[] listed = [.. near.Take(MostListed)];

        var mustExist = new List<ReachMustExist>();
        long total = 0, large = 0;
        foreach (Gaps cube in cubes.OrderBy(cube => cube.Distance).ThenBy(cube => cube.Zero.Address))
        {
            if (cube.Certainty == Large)
            {
                large += cube.Count;
                continue;
            }
            total += cube.Count;
            if (cube.Namer is not { } name) continue;
            (double x, double y, double z) = cube.Zero.Centre;
            for (long number = 0; number < cube.Highest && mustExist.Count < MostListed; number++)
            {
                if (cube.Known.Contains(number)) continue;
                mustExist.Add(new ReachMustExist(name(number), (cube.Zero with { Number = number }).Address, x, y, z, cube.Zero.CubeLy / 2,
                    Math.Round(cube.Distance, 2), cube.Certainty));
            }
        }
        return new Made(mine.Centre, mine.Range, mine.From, at, source, capped, listed, near.Count - listed.Length, mustExist, total - mustExist.Count, large, null);
    }

    // ------------------------------------------------------------ space

    /// <summary>The sectors of the grid a sphere's box reaches into (Nav scan looks for catalogue names in them too).</summary>
    internal static (int X, int Y, int Z)[] SectorsOf(double x, double y, double z, double r)
    {
        (int x0, int y0, int z0) = Galaxy.SectorOf(x - r, y - r, z - r);
        (int x1, int y1, int z1) = Galaxy.SectorOf(x + r, y + r, z + r);
        var sectors = new List<(int X, int Y, int Z)>();
        for (int sy = Math.Max(0, y0); sy <= Math.Min(Galaxy.Layers - 1, y1); sy++)
            for (int sz = Math.Max(0, z0); sz <= Math.Min(Galaxy.SectorsAcross - 1, z1); sz++)
                for (int sx = Math.Max(0, x0); sx <= Math.Min(Galaxy.SectorsAcross - 1, x1); sx++)
                    sectors.Add((sx, sy, sz));
        return [.. sectors];
    }

    static double Far(ReachCentre at, double x, double y, double z) =>
        Math.Sqrt((at.X - x) * (at.X - x) + (at.Y - y) * (at.Y - y) + (at.Z - z) * (at.Z - z));

    /// <summary>Some of the cube lies within <paramref name="r"/> of the place.</summary>
    static bool Touches(CubePlace cube, ReachCentre at, double r)
    {
        (double x0, double y0, double z0) = cube.Corner;
        double side = cube.CubeLy;
        double dx = Math.Max(0, Math.Max(x0 - at.X, at.X - (x0 + side)));
        double dy = Math.Max(0, Math.Max(y0 - at.Y, at.Y - (y0 + side)));
        double dz = Math.Max(0, Math.Max(z0 - at.Z, at.Z - (z0 + side)));
        return dx * dx + dy * dy + dz * dz <= r * r;
    }

    /// <summary>All of the cube lies within <paramref name="r"/> of the place: its farthest corner does.</summary>
    static bool Inside(CubePlace cube, ReachCentre at, double r)
    {
        (double x0, double y0, double z0) = cube.Corner;
        double side = cube.CubeLy;
        double dx = Math.Max(Math.Abs(at.X - x0), Math.Abs(at.X - (x0 + side)));
        double dy = Math.Max(Math.Abs(at.Y - y0), Math.Abs(at.Y - (y0 + side)));
        double dz = Math.Max(Math.Abs(at.Z - z0), Math.Abs(at.Z - (z0 + side)));
        return dx * dx + dy * dy + dz * dz <= r * r;
    }

    // ------------------------------------------------------------ what the page and the overlay are given

    /// <summary>Within reach as it stands, with what the 3D map's memory of checks says of each system now.</summary>
    public ReachView Now()
    {
        bool isOn;
        string now;
        string? why;
        ReachRun? going;
        Made? made;
        lock (guard) (isOn, now, why, going, made) = (on, state, idleWhy, run, shown);
        if (made is not null && now == Done)
        {
            ReachSystem[] systems = [.. made.Systems.Select(system => new ReachSystem(system.Name, system.Id64, system.X, system.Y, system.Z, Math.Round(system.Distance, 2), StateOf(system)))];
            ReachMustExist[] mustExist = [.. made.MustExist.Select(gap => gap with { State = Known(gap.Name, gap.Id64) })];
            return new ReachView(true, made.Range, made.From, made.At, made.Centre, made.Source, made.Capped, systems, mustExist, made.Problem)
            {
                State = Done,
                MoreSystems = made.MoreSystems,
                MoreMustExist = made.MoreMustExist,
                InLargeCubes = made.InLargeCubes,
            };
        }
        if (going is not null)
            return new ReachView(true, going.Range, going.From, null, going.Centre, null, false, [], [], null) { State = now };
        (double range, string from) = RangeNow();
        double[]? at = game.StarPos;
        ReachCentre? centre = at is [var x, var y, var z] ? new ReachCentre(x, y, z, game.StarSystem) : null;
        return new ReachView(isOn, range > 0 ? range : null, from, null, isOn ? centre : null, null, false, [], [], isOn ? why : null) { State = isOn ? Idle : Off };
    }

    /// <summary>A system of the list by what the memory says of it, else by what the list says.</summary>
    string StateOf(Near system) => Known(system.Name, system.Id64) ?? (system.Star == false ? NameOnly : Recorded);

    /// <summary>What the memory of checks says of a system: recorded or not; null when it has no answer for it (a failure is none).</summary>
    string? Known(string name, long id64) => memory.Get(name, id64)?.Result switch
    {
        CheckMemory.Recorded => Recorded,
        CheckMemory.NotRecorded => NotRecorded,
        _ => null,
    };

    /// <summary>
    /// Tells the page how Within reach stands again, while a list is shown: the memory of checks changed (a check
    /// settled systems, or the player had it forgotten), so what it says of the systems listed may have changed, and
    /// the page asks for the list again. Any thread: nothing of the game's state is read.
    /// </summary>
    public void Retell()
    {
        lock (guard)
        {
            if (state != Done) return;
        }
        Tell();
    }

    void Tell()
    {
        ReachNews news;
        lock (guard)
        {
            Made? made = state == Done ? shown : null;
            if (made is not null)
                news = new ReachNews(Done, true, made.Range, made.From, made.At, made.Centre, made.Source, made.Capped,
                    made.Systems.Count + made.MoreSystems, made.MustExist.Count + made.MoreMustExist, made.Problem) { InLargeCubes = made.InLargeCubes };
            else if (run is { } going)
                news = new ReachNews(state, true, going.Range, going.From, null, going.Centre, null, false, 0, 0, null);
            else news = new ReachNews(state, on, null, typed is null ? FromShip : FromYou, null, null, null, false, 0, 0, on ? idleWhy : null);
        }
        if (news.RangeLy is null && news.State is Off or Idle)
        {
            // The range the page shows beside the switch, also while there is nothing to list.
            (double range, string from) = RangeNow();
            news = news with { RangeLy = range > 0 ? range : null, From = from };
        }
        News?.Invoke(news);
    }
}
