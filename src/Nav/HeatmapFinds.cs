using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Nav;

/// <summary>A system the game knows that no database had when Studio checked: one of "Your finds" (<see cref="HeatmapFinds"/>).</summary>
/// <param name="Name">The name as the game gave it.</param>
/// <param name="Id64">
/// The game's own number for the system, which the game gave with the name: the proof that the system exists. Written
/// as text, because these numbers can be larger than a web page can hold exactly.
/// </param>
/// <param name="X">
/// Where it is, in light years (with <paramref name="Y"/> and <paramref name="Z"/>): where the plotted route
/// (NavRoute.json) says, else the middle of the cube its number names; null when neither says.
/// </param>
/// <param name="PlusMinus">Placed from its number or name: it lies within this many light years of the point along each of x, y and z. Null for a place the game gave.</param>
/// <param name="At">When Studio found it (UTC).</param>
/// <param name="From">
/// What the game named it as: <see cref="HeatmapFinds.FromTarget"/>, <see cref="HeatmapFinds.FromRouteEnd"/>, or
/// <see cref="HeatmapFinds.FromRoute"/> for a stop of a plotted route checked with all the others; or
/// <see cref="HeatmapFinds.FromNavScan"/> for a name Nav scan read off a picture (see <see cref="Confirmed"/>).
/// </param>
/// <param name="How">How it was checked: what was looked in, in a few words each.</param>
public sealed record HeatmapFind(
    string Name,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Id64,
    double? X, double? Y, double? Z, double? PlusMinus, DateTime At, string From, IReadOnlyList<string> How)
{
    /// <summary>
    /// For a name Nav scan read off a picture: what the player should know of it, in their words ("read from a picture;
    /// target it in the game to confirm"). Null for the others.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    /// <summary>
    /// For a name Nav scan read off a picture: false while only the picture says so (the reader may have misread a
    /// letter, and its number is the one the name gives), true once the game named the system with its number (the
    /// player targeted it, plotted a route through it or arrived there; at once for the system the commander was in),
    /// which proves it exists. Null for the others, which the game named in the first place.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Confirmed { get; init; }

    /// <summary>
    /// For one of the readings of a name Nav scan could not make out for certain ("NX-Q" or "NX-G"): the same text on
    /// each of them. Once the game names one, the others are taken off: they were misreads. Null for the others.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Readings { get; init; }
}

/// <summary>
/// The system the game has targeted, or the end of the plotted route, where the 3D map marks it (GET block: "target",
/// "routeEnd").
/// </summary>
/// <param name="Id64">The game's own number for the system. Written as text, because these numbers can be larger than a web page can hold exactly.</param>
/// <param name="X">Where it is, in light years (with <paramref name="Y"/> and <paramref name="Z"/>).</param>
public sealed record MapMark(
    string Name,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Id64,
    double X, double Y, double Z)
{
    /// <summary>The game gave no place: this is the middle of the cube the system's number (or name) names, and it lies within this many light years of it along each of x, y and z.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PlusMinus { get; init; }

    /// <summary>What this session's check found: recorded somewhere, or not recorded. Null while it has not been checked (or could not be).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Recorded { get; init; }
}

/// <summary>
/// News of the game's target or the end of its plotted route, sent as a "heatmap" event with kind "target": it moved
/// (the page asks for the block again to move its markers), or a check of it ended.
/// </summary>
/// <param name="From"><see cref="HeatmapFinds.FromTarget"/> or <see cref="HeatmapFinds.FromRouteEnd"/>.</param>
/// <param name="Name">The system's name; "" when the target or route has gone.</param>
/// <param name="Id64">The game's own number for the system (0 when gone). Written as text.</param>
/// <param name="State">One of <see cref="HeatmapFinds.Moved"/>, <see cref="HeatmapFinds.Recorded"/>, <see cref="HeatmapFinds.NotRecorded"/> or <see cref="HeatmapFinds.Unchecked"/>.</param>
/// <param name="Problem">Why it could not be checked, in the player's words; null otherwise.</param>
public sealed record TargetNews(
    string From, string Name,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Id64,
    string State, string? Problem)
{
    /// <summary>Always "target": it tells these events from those of a survey (<see cref="SurveyProgress"/>) or of the detailed map.</summary>
    [JsonPropertyOrder(-1)]
    public string Kind => "target";
}

/// <summary>A system as the game names it: where the 3D map's markers and checks start from.</summary>
/// <param name="Id64">The game's own number for it.</param>
/// <param name="At">Where the game says it is ([x, y, z], from NavRoute.json); null when the game gave no place.</param>
public sealed record GameNamed(string Name, long Id64, double[]? At);

/// <summary>
/// The 3D map's part of settings.json (section "heatmap"). Each part of the map that keeps a setting here reads the
/// section, changes its own and writes it back (<see cref="Settings.Get{T}"/> then <see cref="Settings.Set{T}"/>), so
/// none of them writes over another's.
/// </summary>
public sealed class HeatmapSettings
{
    /// <summary>"Check my targets": every system the game targets or plots a route to is checked (once, as long as <see cref="CheckMemory"/> keeps its answer).</summary>
    public bool CheckTargets { get; set; } = true;

    /// <summary>
    /// What of a plotted route is checked: <see cref="HeatmapFinds.CheckAll"/>, every stop (the owner asked for it on
    /// 15 October 2026), or <see cref="HeatmapFinds.CheckEnds"/>, only its end (with the game's target, which both check).
    /// </summary>
    public string RouteCheck { get; set; } = HeatmapFinds.CheckAll;

    /// <summary>Days a system found recorded is remembered (<see cref="CheckMemory.RecordedDays"/>): 1 (the owner's choice of 15 October 2026), 7 or 30.</summary>
    public int RecordedDays { get; set; } = 1;

    /// <summary>"Within reach": after every arrival Studio lists the systems within the ship's jump range (<see cref="HeatmapReach"/>). On unless the player turns it off.</summary>
    public bool WithinReach { get; set; } = true;

    /// <summary>The jump range "Within reach" uses, typed by the player in light years; null for the ship's own.</summary>
    public double? ReachLy { get; set; }

    /// <summary>"Nav scan": Studio reads the player's screenshots of the game's Navigation panel and checks the names on them (<see cref="Nav.NavScan"/>). Off unless the player turns it on.</summary>
    public bool NavScan { get; set; }

    /// <summary>How loud Nav scan's click and ping are, 0 (silent) to 100.</summary>
    public int NavScanVolume { get; set; } = 70;
}

/// <summary>
/// "Your finds", and the 3D map's markers for the game's target and the end of its plotted route. A player looking
/// for systems nobody has recorded guesses names in the galaxy map's search (<see cref="HeatmapNames"/>): when the game
/// finds one, the player targets it or plots a route to it, and the game names it with its number (FSDTarget,
/// Status.json's destination, NavRoute.json), which proves it exists. Studio then checks whether any database has it:
/// first in what is kept on this PC (the surveys and the detailed map), and only when it is not there, with one name
/// lookup at Spansh. Not found anywhere: "not recorded — exists in game", kept for good in heatmap\finds.json.
///
/// Spansh is one person's free service, so the lookup is a careful guest's: it goes through the 3D map's own gate and
/// is counted in its requests for the session (<see cref="HeatmapSurvey.LookUp"/>); a system is not checked again while
/// the 3D map's memory of checks keeps its answer (<see cref="CheckMemory"/>: a day for one found recorded, unless the
/// player chose longer, a day for one not recorded, ten minutes for one that could not be checked), whichever check
/// settled it, Within reach and Nav scan included; nothing from before Studio started is checked (the journal read at
/// start-up, the route and destination already there), though the page and the overlay say a route is there and not
/// checked, and the player may ask for it ("Check this route"); and the player can switch it all off ("Check my
/// targets", kept in settings.json).
///
/// A plotted route is checked as the player chose ("routeCheck", kept in settings.json too): only its end, the game
/// picking the hops (<see cref="CheckEnds"/>), or every stop of it (<see cref="CheckAll"/>, the owner's choice of
/// 15 October 2026, and so the default). Every stop is then checked the same careful way, the many in one go: what is
/// kept on this PC first, and the rest with one request to Spansh for each 100 of them (HeatmapRouteCheck.cs).
///
/// Lives on the UI thread like the game state it follows; what it reads from disk is read away from it.
/// </summary>
public sealed partial class HeatmapFinds
{
    const string FileName = "finds.json";
    const string Section = "heatmap";

    /// <summary>What <see cref="HeatmapFind.From"/> and <see cref="TargetNews.From"/> say.</summary>
    public const string FromTarget = "target", FromRouteEnd = "route end";

    /// <summary>What <see cref="HeatmapFind.From"/> says of a stop of a plotted route checked with all the others.</summary>
    public const string FromRoute = "route";

    /// <summary>What <see cref="HeatmapFind.From"/> says of a name Nav scan read off a picture (HeatmapNavScan.cs).</summary>
    public const string FromNavScan = "nav scan";

    /// <summary>What <see cref="TargetNews.State"/> says.</summary>
    public const string Moved = "moved", Recorded = "recorded", NotRecorded = "not-recorded", Unchecked = "unchecked";

    /// <summary>What <see cref="HeatmapSettings.RouteCheck"/> says: the route's end only, or every stop of it.</summary>
    public const string CheckEnds = "ends", CheckAll = "all";

    /// <summary>
    /// Changes of the game's state a newly plotted route waits for its file (NavRoute.json, which the game may write a
    /// moment after its journal line) before its end is checked as it stands: a route plotted to the same end again.
    /// </summary>
    const int RouteWaits = 10;

    readonly GameState game;
    readonly HeatmapSurvey survey;
    readonly FineMap fine;
    readonly Settings? settings;
    readonly string file;
    readonly Lock guard = new();                        // checks end on other threads than the game's
    // The systems being checked just now, each by the one check that took it (a target's, or a route's or a picture's
    // that will ask Spansh about it): another check that comes to one leaves it be (a target's), waits for what comes of
    // a target's check of it, or puts aside one another check of many took, to wait for it once it holds none of its
    // own (HeatmapRouteCheck.cs: Offline). What came of a check is kept in the memory (Memory).
    readonly Dictionary<long, Claim> underWay = [];
    readonly Dictionary<long, Located> places = [];     // what is kept on this PC knows of a system, by its number
    readonly List<HeatmapFind> unsaved = [];            // finds made while finds.json could not be read
    List<HeatmapFind>? finds;                           // finds.json, read the first time it can be
    bool checkTargets;
    string routeMode;                                   // CheckEnds or CheckAll
    GameNamed? fsdTarget;                               // the last FSDTarget line: the system the game's drive is aimed at
    bool fsdHop;                                        // that line aimed at a stop of a plotted route (jumps left in the route)
    bool primed;                                        // the first look at the game's state (Studio's start) has been taken
    (long Target, long RouteEnd) shown;                 // what the markers showed at the last look
    long destinationSeen;                               // Status.json's destination at the last look
    long routeBefore;                                   // the route's end when the game said it plotted a new one
    int routeNews;                                      // looks left to wait for the new route's file
    (long End, int Looks) routeEarly;                   // a route file read before its journal line: the end it replaced, and the looks it waits for the line
    Task idle = Task.CompletedTask;

    /// <param name="game">The game state, whose journal lines and changes are followed from now on.</param>
    /// <param name="survey">The 3D map's questions to Spansh: its kept surveys, its gate and its budget. Finds are kept in its folder.</param>
    /// <param name="fine">The detailed map, looked in before Spansh is asked.</param>
    /// <param name="settings">Where "Check my targets", what of a route is checked and how long a recorded system is remembered are kept; without it, only for the session.</param>
    /// <param name="memory">The 3D map's memory of checks, shared with Within reach and Nav scan; when left out, the one in <paramref name="survey"/>'s folder.</param>
    public HeatmapFinds(GameState game, HeatmapSurvey survey, FineMap fine, Settings? settings = null, CheckMemory? memory = null)
    {
        (this.game, this.survey, this.fine, this.settings) = (game, survey, fine, settings);
        file = Path.Combine(survey.Folder, FileName);
        Memory = memory ?? new CheckMemory(survey.Folder);
        HeatmapSettings kept = settings?.Get<HeatmapSettings>(Section) ?? new HeatmapSettings();
        checkTargets = kept.CheckTargets;
        // A value typed into the file by hand that means neither is taken for the default.
        routeMode = RouteMode(kept.RouteCheck) ?? CheckAll;
        // So is a number of days the page does not offer.
        Memory.RecordedDays = CheckMemory.DaysOffered.Contains(kept.RecordedDays) ? kept.RecordedDays : 1;
        game.Journal += Follow;
        game.Changed += Look;
        // A new survey or a new detailed map may know where a system is: what was looked up before is looked up again.
        survey.Progress += progress =>
        {
            if (progress.State == HeatmapSurvey.Done) Forget();
        };
        fine.Progress += progress =>
        {
            if (progress.State == FineMap.Done) Forget();
        };
    }

    /// <summary>The 3D map's memory of checks: asked first by every check, and told what came of each.</summary>
    public CheckMemory Memory { get; }

    /// <summary>
    /// Days a system found recorded is remembered, so not checked again: 1, 7 or 30 (<see cref="CheckMemory.RecordedDays"/>).
    /// Kept in settings.json.
    /// </summary>
    /// <exception cref="ArgumentException">Not 1, 7 or 30.</exception>
    public int RecordedDays
    {
        get => Memory.RecordedDays;
        set
        {
            Memory.RecordedDays = value;
            SaveSettings("choice of how long a recorded system is remembered");
        }
    }

    /// <summary>Raised when a check finds a system no database has, once it is kept. Raised on the thread the check ends on.</summary>
    public event Action<HeatmapFind>? Found;

    /// <summary>Raised when the game's target or route end moves, and when a check of one ends.</summary>
    public event Action<TargetNews>? News;

    /// <summary>
    /// Raised when the game names a system Nav scan read off a picture and kept as a find: the find, now confirmed
    /// (<see cref="HeatmapFind.Confirmed"/>). Raised away from the game's thread.
    /// </summary>
    public event Action<HeatmapFind>? Confirmed;

    /// <summary>The checks started by the game's lines and changes, all of them; for tests, which wait for them.</summary>
    internal Task Idle
    {
        get
        {
            lock (guard) return idle;
        }
    }

    /// <summary>
    /// "Check my targets": on, the systems the game targets or plots a route to are checked; off, nothing is, and a
    /// check of a route under way stops after its request under way. Kept in settings.json. Switched on with every stop
    /// of a route to be checked, the route plotted now is checked (the player's click asks for it).
    /// </summary>
    public bool CheckTargets
    {
        get
        {
            lock (guard) return checkTargets;
        }
        set
        {
            lock (guard) checkTargets = value;
            SaveSettings("\"Check my targets\"");
            if (value) CheckRouteNow();
            else StopRoute(tell: true);
        }
    }

    /// <summary>
    /// What of a plotted route is checked: <see cref="CheckEnds"/> or <see cref="CheckAll"/>. Kept in settings.json.
    /// Set to every stop, the route plotted now is checked unless it is being checked already (the route that was
    /// there at Studio's start too: the player's click asks for it); set to the end only, a check of every stop under
    /// way stops after its request under way.
    /// </summary>
    /// <exception cref="ArgumentException">Neither "ends" nor "all".</exception>
    public string RouteCheck
    {
        get
        {
            lock (guard) return routeMode;
        }
        set
        {
            string mode = RouteMode(value) ?? throw new ArgumentException(NoSuchRouteCheck, nameof(value));
            lock (guard) routeMode = mode;
            SaveSettings("choice of what of a route is checked");
            if (mode == CheckAll) CheckRouteNow();
            else StopRoute(tell: true);
        }
    }

    /// <summary>Why a value is no <see cref="RouteCheck"/>, in the player's words.</summary>
    public const string NoSuchRouteCheck = "Studio checks the target and the end of a plotted route (\"ends\"), or every stop of the route (\"all\").";

    /// <summary>"ends" or "all" as <see cref="RouteCheck"/> spells them, whatever the capitals and spaces; null for anything else.</summary>
    static string? RouteMode(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        CheckEnds => CheckEnds,
        CheckAll => CheckAll,
        _ => null,
    };

    /// <summary>
    /// The checks' settings into settings.json, beside the rest of the 3D map's (Within reach's, Nav scan's), which are
    /// left as they are; one that cannot be written holds for the session.
    /// </summary>
    void SaveSettings(string what)
    {
        if (settings is null) return;
        HeatmapSettings now = settings.Get<HeatmapSettings>(Section);
        lock (guard) (now.CheckTargets, now.RouteCheck) = (checkTargets, routeMode);
        now.RecordedDays = Memory.RecordedDays;
        try
        {
            settings.Set(Section, now);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error($"The 3D map's {what} could not be kept in settings.json; it holds until Studio is closed", problem);
        }
    }

    /// <summary>Every find kept, the earliest first.</summary>
    public IReadOnlyList<HeatmapFind> Finds
    {
        get
        {
            lock (guard) return [.. (Loaded() ?? []).Concat(unsaved).OrderBy(find => find.At).ThenBy(find => find.Id64)];
        }
    }

    /// <summary>Takes a find off the list, by its number (or by its name when the number is not given). False when there was no such find.</summary>
    public bool Remove(long id64, string? name)
    {
        name = name?.Trim();
        bool Matches(HeatmapFind find) => id64 > 0 ? find.Id64 == id64 : !string.IsNullOrEmpty(name) && find.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
        lock (guard)
        {
            int gone = unsaved.RemoveAll(Matches);
            if (Loaded() is not { } list) return gone > 0;
            gone += list.RemoveAll(Matches);
            if (gone > 0) Save(list);
            return gone > 0;
        }
    }

    // ------------------------------------------------------------ what the game names

    /// <summary>
    /// The system the game has targeted (Status.json's destination when it is another system, else the last FSDTarget
    /// that is not the system the ship is in) and the end of the plotted route (NavRoute.json), as the game names
    /// them. Read on the game's thread.
    /// </summary>
    public (GameNamed? Target, GameNamed? RouteEnd) Marks()
    {
        long here = game.SystemAddress;
        GameNamed? target = null;
        if (game.Status.Destination is { System: > 0 } destination && destination.System != here)
        {
            // A body or station of another system names the body: the system's own name is taken from what else names it.
            string name = fsdTarget?.Id64 == destination.System ? fsdTarget.Name
                : destination.Body == 0 && destination.Name.Length > 0 ? destination.Name
                : NameOf(destination.System);
            target = new GameNamed(name, destination.System, PlaceOf(destination.System));
        }
        else if (fsdTarget is { } aimed && aimed.Id64 != here)
            target = aimed with { At = PlaceOf(aimed.Id64) };
        return (target, RouteEnd());
    }

    GameNamed? RouteEnd() =>
        game.Route is [_, .., var last] && last.SystemAddress > 0 && last.SystemAddress != game.SystemAddress
            ? new GameNamed(last.StarSystem, last.SystemAddress, last.StarPos)
            : null;

    /// <summary>Where the plotted route says a system is; null when it is not on the route, or the route gives no place.</summary>
    double[]? PlaceOf(long id64) => game.Route.FirstOrDefault(stop => stop.SystemAddress == id64)?.StarPos is [_, _, _] at ? at : null;

    /// <summary>A system's name from what names it: the FSDTarget, the route, or the game's rule for its number.</summary>
    string NameOf(long id64) =>
        fsdTarget?.Id64 == id64 ? fsdTarget.Name
        : game.Route.FirstOrDefault(stop => stop.SystemAddress == id64)?.StarSystem
        ?? (SystemAddress.Place(id64) is { } place ? ProcGenNames.SystemName(place) : null)
        ?? "";

    /// <summary>
    /// Where the 3D map marks a system the game names: where the game says, else where a kept survey or the detailed
    /// map has it, else the middle of the cube its number (or name) names, with how far off that may be. Null for
    /// none, or for a system Studio cannot place at all.
    /// </summary>
    public async Task<MapMark?> Place(GameNamed? named)
    {
        if (named is null) return null;
        bool? recorded = Memory.Get(named.Name, named.Id64)?.Result switch
        {
            CheckMemory.Recorded => true,
            CheckMemory.NotRecorded => false,
            _ => null,
        };
        if (named.At is [var x, var y, var z]) return new MapMark(named.Name, named.Id64, x, y, z) { Recorded = recorded };
        Located where = await Locate(named.Name, named.Id64);
        return where is { X: { } lx, Y: { } ly, Z: { } lz }
            ? new MapMark(named.Name, named.Id64, lx, ly, lz) { PlusMinus = where.PlusMinus, Recorded = recorded }
            : null;
    }

    /// <summary>
    /// A journal line as the game writes it. A target picked without a route is checked at once, from the line itself
    /// (Status.json may still name the one before); a route's hops are the game's picks, so its end is checked instead
    /// (or with every stop all of them, as the player chose), once the new route's file has been read (<see cref="Look"/>).
    /// Lines read at start-up are history: not checked. A route cleared takes its check with it. Every system the game
    /// names with its number (a target, a hop of the route, an arrival) confirms a find Nav scan read off a picture of it.
    /// </summary>
    void Follow(JournalLine line)
    {
        switch (line.Event)
        {
            case "FSDTarget":
                string name = line.Data.Text("Name");
                long address = line.Data.Whole("SystemAddress");
                fsdTarget = name.Length > 0 && address > 0 ? new GameNamed(name, address, null) : null;
                // On a plotted route the game aims at each stop by itself (the last one too, with 1 jump left).
                fsdHop = fsdTarget is not null && line.Data.Whole("RemainingJumpsInRoute") > 0;
                if (!line.Replay && fsdTarget is not null && !fsdHop) Start(name, address, PlaceOf(address), FromTarget);
                // A hop is not checked (the route's check has it), but the game named it all the same: it exists.
                else if (!line.Replay && fsdTarget is not null) Confirm([address]);
                break;
            case "FSDJump" or "CarrierJump" or "Location":
                // The commander is there: the game named the system with its number. (The system the commander is in at
                // Studio's start is confirmed by the first look: those lines are history.)
                if (!line.Replay && line.Data.Whole("SystemAddress") is > 0 and long arrived) Confirm([arrived]);
                break;
            case "NavRoute":
                // The route file is mostly read after the lines (GameState.Journal), so this is still the route before it.
                // A file the game wrote a moment before its line can have come with the Poll before, though: then the end it
                // replaced is the one to tell the new route by (Look noted it), or the new end would wait RouteWaits looks.
                if (!line.Replay) (routeNews, routeBefore) = (RouteWaits, routeEarly.Looks > 0 ? routeEarly.End : RouteEnd()?.Id64 ?? 0);
                routeEarly = default;
                break;
            case "NavRouteClear":
                (routeNews, routeEarly) = (0, default);
                // The game has emptied its route already (GameState): the same route plotted again is a new one to check.
                // A route there since Studio's start, shown as not checked, goes too.
                StopRoute(tell: NoteRoute(""));
                break;
        }
    }

    /// <summary>
    /// The system is one the game picked on a plotted route: a stop of the route's file, or what the last FSDTarget
    /// aimed at as a stop of its route (the route's file may not have been read yet).
    /// </summary>
    bool OnRoute(long id64) => game.Route.Any(stop => stop.SystemAddress == id64) || (fsdHop && fsdTarget?.Id64 == id64);

    /// <summary>
    /// The game's state changed: the markers may have moved (the page is told), Status.json may name a new destination,
    /// and a route just plotted may have arrived. The first look, at Studio's start, only notes what is there.
    /// </summary>
    void Look()
    {
        (GameNamed? target, GameNamed? end) = Marks();
        bool first = !primed;
        primed = true;
        // The system the commander is in as Studio starts: the game named it, so a find Nav scan read of it exists.
        if (first && game.SystemAddress > 0) Confirm([game.SystemAddress]);
        if (routeEarly.Looks > 0) routeEarly.Looks--;
        (long Target, long RouteEnd) now = (target?.Id64 ?? 0, end?.Id64 ?? 0);
        if (now != shown)
        {
            (long Target, long RouteEnd) before = shown;
            shown = now;
            if (!first && now.Target != before.Target) News?.Invoke(new TargetNews(FromTarget, target?.Name ?? "", now.Target, Moved, null));
            if (!first && now.RouteEnd != before.RouteEnd) News?.Invoke(new TargetNews(FromRouteEnd, end?.Name ?? "", now.RouteEnd, Moved, null));
            // The route's end moved with no NavRoute line waiting for its file: the game may have written the file a moment
            // before the line, which then comes with one of the next Polls (Follow).
            if (!first && now.RouteEnd != before.RouteEnd && routeNews == 0) routeEarly = (before.RouteEnd, 2);
        }

        if (game.Status.Destination is { System: > 0, Body: 0, Name.Length: > 0 } destination && destination.System != game.SystemAddress
            && destination.System != destinationSeen)
        {
            destinationSeen = destination.System;
            // On a plotted route the game aims at every stop by itself, so the nav panel's target (Status.json's destination)
            // moves to each new stop after a jump: those are the game's picks, not the player's, and checking each would
            // spend the 3D map's requests on them. The route's end is checked as the route's end (below), so its find
            // says so.
            if (!first && !OnRoute(destination.System))
                Start(fsdTarget?.Id64 == destination.System ? fsdTarget.Name : destination.Name, destination.System, PlaceOf(destination.System), FromTarget);
        }

        bool plotted = false;
        if (routeNews > 0 && end is not null && (end.Id64 != routeBefore || --routeNews == 0))
        {
            (routeNews, plotted) = (0, true);
            // With every stop of a route checked, its end is checked with the rest of them (and told as the route's end).
            if (RouteCheck == CheckEnds) Start(end.Name, end.Id64, end.At, FromRouteEnd);
        }

        // The route as a whole, for the check of every stop: a new one is checked, one gone takes its check with it. The
        // route there at Studio's start is history, like the rest, until the player plots it again (the same stops: the
        // file alone does not tell, the NavRoute line does) or asks for it on the 3D map ("Check this route"); meanwhile
        // the page and the overlay are told it is there and not checked (RouteCheckView.Plotted).
        string key = RouteKey(game.Route);
        if (key != routeKey || (plotted && !Following(key)))
        {
            bool changed = NoteRoute(key);
            if (key.Length == 0) StopRoute(tell: changed);
            else if ((first || !StartRoute()) && changed) Tell(null);
        }
    }

    void Start(string name, long id64, double[]? at, string from)
    {
        Task check = Check(name, id64, at, from);
        lock (guard) idle = idle.IsCompleted ? check : Task.WhenAll(idle, check);
    }

    // ------------------------------------------------------------ the check

    /// <summary>
    /// Checks whether any database has a system the game named, by its number: first in the 3D map's memory of checks,
    /// then in the surveys kept on this PC and the detailed map, and only when it is in none of them, with one name
    /// lookup at Spansh. Not found anywhere, it is kept as a find (<see cref="Found"/>). What came of it goes into the
    /// memory. Null when it is not checked: "Check my targets" is off, the memory keeps an answer for the system
    /// (whichever check gave it, whatever it was: not even a failure of the last ten minutes is tried again), another
    /// check of it is under way, or the game gave no name or number.
    /// </summary>
    /// <param name="at">Where the game says the system is (the plotted route's place for it); null when it gave none.</param>
    /// <param name="from">What the game named it as: <see cref="FromTarget"/> or <see cref="FromRouteEnd"/>.</param>
    public async Task<TargetNews?> Check(string? name, long id64, double[]? at = null, string from = FromTarget)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0 || id64 <= 0) return null;
        // The game names it with its number: if Nav scan read it off a picture, it exists (whatever else is done here,
        // and with "Check my targets" off too: nothing is asked of anybody for it).
        Confirm([id64]);
        lock (guard)
        {
            if (!checkTargets || underWay.ContainsKey(id64) || Memory.Get(name, id64) is not null) return null;
            // A route's or a picture's check that comes to this system meanwhile waits for what comes of it, rather than
            // asking again: this check waits for nothing of theirs, so it cannot hold them up for good.
            underWay[id64] = new Claim(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), Batch: false);
        }
        (bool? Recorded, string? By, string? How, string? Problem) outcome;
        try
        {
            outcome = await Checked(name, id64, at, from);
        }
        catch (Exception unexpected)
        {
            // A check runs by itself, from a line of the game's: whatever goes wrong must not reach the game's thread.
            Log.Error($"3D map: {name} could not be checked", unexpected);
            outcome = (null, null, null, Unexpected);
        }

        Claim? waiting;
        lock (guard)
        {
            Remember(name, id64, outcome.Recorded, outcome.By, outcome.How, outcome.Problem);
            underWay.Remove(id64, out waiting);
        }
        waiting?.Done.TrySetResult();
        await Memory.SaveAsync();
        var news = new TargetNews(from, name, id64, outcome.Recorded switch { true => Recorded, false => NotRecorded, null => Unchecked }, outcome.Problem);
        News?.Invoke(news);
        return news;
    }

    /// <summary>
    /// The check of one system the game named, once it is known that it has to be checked: what is kept on this PC,
    /// then Spansh's name search (unless Within reach's answer, come while the search waited its turn, lists it). Gives
    /// what came of it: recorded or not (null: it could not be checked), where the answer came from
    /// (<see cref="CheckEntry.By"/>), in a few words, and why it could not be checked.
    /// </summary>
    async Task<(bool? Recorded, string? By, string? How, string? Problem)> Checked(string name, long id64, double[]? at, string from)
    {
        Located kept = await Locate(name, id64);
        if (kept.Found) return (true, kept.By, kept.Source, null);
        var how = new List<string>(kept.How);

        // Spansh's name search wants three letters; no system name the game gives is shorter, but none is sent for one.
        if (name.Length < 3) return (null, null, null, "The name is too short for Spansh's name search.");
        (IReadOnlyList<LocatedSystem>? suggested, string? problem, NamedSystem? near) = await survey.LookUp(name, id64);
        // Within reach's answer came while the lookup waited its turn behind it, and lists the system: nothing was sent.
        if (near is not null) return (true, CheckMemory.BySpansh, FoundWithinReach, null);
        if (problem is not null)
        {
            Log.Error($"3D map: {name} could not be checked at Spansh: {problem}");
            return (null, CheckMemory.BySpansh, null, problem);
        }
        string day = DateTime.UtcNow.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        if (suggested!.FirstOrDefault(system => system.Id64 == id64 || system.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } listed)
        {
            lock (guard) places[id64] = new Located(true, listed.X, listed.Y, listed.Z, null, [], FoundByName, CheckMemory.BySpansh);
            return (true, CheckMemory.BySpansh, FoundByName, null);
        }
        how.Add($"{NotFoundByName} ({day})");

        // Placed where the game says, else where its number says, to within half its cube.
        HeatmapFind find = at is [var x, var y, var z]
            ? new HeatmapFind(name, id64, x, y, z, null, DateTime.UtcNow, from, how)
            : new HeatmapFind(name, id64, kept.X, kept.Y, kept.Z, kept.PlusMinus, DateTime.UtcNow, from, how);
        Keep([find]);
        Log.Info($"3D map: {name} is not recorded in any database Studio knows; kept in your finds");
        Found?.Invoke(find);
        return (false, CheckMemory.BySpansh, NotFoundByName, null);
    }

    const string FoundByName = "found by Spansh's name search";
    const string NotFoundByName = "not found by Spansh's name search";
    const string FoundWithinReach = "found by Spansh's system search (within reach)";

    /// <summary>
    /// What came of a check, into the memory (written by the next <see cref="CheckMemory.SaveAsync"/>): recorded, not
    /// recorded, or (null) not checkable, with why. Call under the guard.
    /// </summary>
    void Remember(string name, long id64, bool? recorded, string? by, string? how, string? reason) =>
        Memory.Put(name, id64, recorded switch { true => CheckMemory.Recorded, false => CheckMemory.NotRecorded, null => CheckMemory.Failed },
            by, recorded is null ? null : how, recorded is null ? reason ?? Unexpected : null);

    /// <summary>What a check says when something went wrong in Studio itself (it is logged).</summary>
    const string Unexpected = "Studio could not check this system.";

    /// <summary>What is kept on this PC knows of a system: whether it is recorded there, where it is (or about where), and what was looked in.</summary>
    /// <param name="Found">A kept survey or the detailed map has it.</param>
    /// <param name="X">Where it is (with <paramref name="Y"/> and <paramref name="Z"/>); for one not found, the middle of its cube; null when Studio cannot place it.</param>
    /// <param name="PlusMinus">For one not found: how far from that middle it may lie along each of x, y and z.</param>
    /// <param name="How">What was looked in without finding it, in a few words each.</param>
    /// <param name="Source">For one found: where, in a few words ("in the detailed map (Spansh's list of 14 Oct 2026)").</param>
    /// <param name="By">For one found: where, as the memory of checks says it (<see cref="CheckEntry.By"/>).</param>
    sealed record Located(bool Found, double? X, double? Y, double? Z, double? PlusMinus, IReadOnlyList<string> How, string? Source = null, string? By = null);

    /// <summary>
    /// What is kept on this PC knows of a system, looked up once (until a new survey or detailed map comes) and away from
    /// the calling thread; first, what Spansh listed round the commander this session (Within reach's request after a
    /// jump outside the detailed map: Nav scan's pictures and the first stops of a route are mostly of those systems).
    /// </summary>
    async Task<Located> Locate(string name, long id64)
    {
        if (survey.SentNear(id64, name) is { } near) return new Located(true, near.X, near.Y, near.Z, null, [], FoundWithinReach, CheckMemory.BySpansh);
        if (id64 > 0)
            lock (guard)
                if (places.TryGetValue(id64, out Located? known)) return known;
        Located found;
        try
        {
            found = await Task.Run(() => LocateKept(name, id64));
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            // A kept file that cannot be read just now says nothing either way; it is looked in again next time.
            Log.Error($"3D map: what is kept on this PC could not be searched for {name}", unreadable);
            return Guess(name, id64, []);
        }
        if (id64 > 0)
            lock (guard) places[id64] = found;
        return found;
    }

    void Forget()
    {
        lock (guard) places.Clear();
        ForgetCatalogue();
    }

    /// <summary>
    /// "Forget all checks": the memory of checks is emptied (written by the next <see cref="CheckMemory.SaveAsync"/>),
    /// and so is what this session looked up of each system, Spansh's answers included, so every system is checked
    /// afresh. Gives how many answers the memory held (those not out of date).
    /// </summary>
    public int ForgetChecks()
    {
        survey.ForgetNear();
        lock (guard)
        {
            places.Clear();
            return Memory.Forget();
        }
    }

    /// <summary>
    /// Told of each search of what is kept on this PC, by the system's name, away from the calling thread. Tests put
    /// something here that throws, standing for a fault in Studio itself (an odd name, a kept file misread).
    /// </summary>
    internal Action<string>? Searching { get; set; }

    /// <summary>
    /// The kept surveys that could hold the system (its sector's, and those of the nebulae round its cube, by its number
    /// and by its name), then the detailed map (which looks over a sector's face too). Nothing is asked of anybody.
    /// </summary>
    async Task<Located> LocateKept(string name, long id64)
    {
        Searching?.Invoke(name);
        var how = new List<string>();
        CubePlace? place = SystemAddress.Place(id64);
        DecodedName? decoded = ProcGenNames.Decode(name);
        var keys = new List<string>();
        foreach (CubePlace cube in new[] { place, decoded?.Cube }.OfType<CubePlace>())
        {
            keys.Add(SurveyArea.SectorKey(cube.SectorX, cube.SectorY, cube.SectorZ));
            (double cx, double cy, double cz) = cube.Centre;
            keys.AddRange(HaRegions.Containing(cx, cy, cz).Select(SurveyArea.NebulaKey));
        }
        foreach (string key in keys.Distinct())
        {
            if (survey.Summary(key) is not { State: SurveySummary.Complete or SurveySummary.Partial }) continue;
            if (await survey.View(key) is not { } view) continue;
            if (view.Systems.FirstOrDefault(system => (id64 > 0 && system.Id64 == id64) || system.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } listed)
                return new Located(true, listed.X, listed.Y, listed.Z, null, [], $"in Spansh's listing of {Title(key)} kept on this PC ({Day(view.At)})", CheckMemory.BySurvey);
            if (view.Complete) how.Add($"not in Spansh's listing of {Title(key)} kept on this PC ({Day(view.At)})");
        }

        FineLookup detailed = fine.Lookup(name, id64);
        if (detailed.System is { } system)
            return new Located(true, system.X, system.Y, system.Z, null, [], detailed.SourceDate is { } from ? $"in the detailed map (Spansh's list of {Day(from)})" : "in the detailed map",
                CheckMemory.ByDetailed);
        if (detailed.Covered && detailed.SourceDate is { } listDate) how.Add($"not in the detailed map (Spansh's list of {Day(listDate)})");
        return Guess(name, id64, how);
    }

    /// <summary>
    /// A system nothing kept has, placed from its number (which the game gave) or else its generated name: either names
    /// its cube, and the system is within half the cube of its middle. Not placed when neither says.
    /// </summary>
    static Located Guess(string name, long id64, IReadOnlyList<string> how)
    {
        if ((SystemAddress.Place(id64) ?? ProcGenNames.Decode(name)?.Cube) is not { } cube) return new Located(false, null, null, null, null, how);
        (double x, double y, double z) = cube.Centre;
        return new Located(false, x, y, z, cube.CubeLy / 2, how);
    }

    static string Day(DateTime at) => at.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>A kept survey's area by the name the player knows it by: the sector's name, or the nebula's.</summary>
    static string Title(string key)
    {
        if (key.StartsWith("n:", StringComparison.Ordinal)) return HaRegions.Named(key[2..])?.Label ?? key[2..];
        if (SurveyArea.Of(key) is not { } area) return key;
        (double x, double y, double z) = area.Box.Centre;
        (int ix, int iy, int iz) = Galaxy.SectorOf(x, y, z);
        return ProcGenNames.SectorName(ix, iy, iz) ?? "its sector";
    }

    // ------------------------------------------------------------ finds.json

    /// <summary>
    /// Keeps finds, with one write of finds.json for them all, except the systems among the finds already (the first
    /// time one was found is the one kept).
    /// </summary>
    void Keep(IReadOnlyList<HeatmapFind> made)
    {
        lock (guard)
        {
            if (Loaded() is not { } list)
            {
                foreach (HeatmapFind find in made)
                    if (!unsaved.Any(kept => kept.Id64 == find.Id64)) unsaved.Add(find);
                Log.Error(made.Count == 1
                    ? "A find of the 3D map is held in memory until finds.json can be read again; it is lost if Studio is closed first"
                    : $"{made.Count} finds of the 3D map are held in memory until finds.json can be read again; they are lost if Studio is closed first");
                return;
            }
            int before = list.Count;
            foreach (HeatmapFind find in made)
                if (!list.Any(kept => kept.Id64 == find.Id64)) list.Add(find);
            if (list.Count > before) Save(list);
        }
    }

    /// <summary>
    /// finds.json (call under the guard), read the first time it can be, with the finds made while it could not. One
    /// another program holds just now is read again next time (null meanwhile), and nothing is written over it; one
    /// that is not a list of finds is moved aside for the owner to send in, and the finds start afresh.
    /// </summary>
    List<HeatmapFind>? Loaded()
    {
        if (finds is not null) return finds;
        List<HeatmapFind> read;
        try
        {
            read = File.Exists(file)
                ? [.. (JsonSerializer.Deserialize<HeatmapFind?[]>(File.ReadAllText(file), Settings.Json) ?? throw new JsonException("The file holds no list"))
                    .OfType<HeatmapFind>().Where(find => find is { Name.Length: > 0, Id64: > 0 }).Select(find => find with { How = find.How ?? [] })]
                : [];
        }
        catch (JsonException problem)
        {
            string aside = file + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            try
            {
                File.Move(file, aside);
            }
            catch (Exception held) when (held is IOException or UnauthorizedAccessException)
            {
                Log.Error("The 3D map's finds could not be read, nor moved aside; they are left as they are", held);
                return null;
            }
            Log.Error($"The 3D map's finds could not be read; the file was kept as {Path.GetFileName(aside)} and the list starts afresh", problem);
            read = [];
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error("The 3D map's finds could not be read just now; they are read again next time, and nothing is written over them meanwhile", problem);
            return null;
        }
        foreach (HeatmapFind find in unsaved)
            if (!read.Any(kept => kept.Id64 == find.Id64)) read.Add(find);
        if (unsaved.Count > 0) Save(read);
        unsaved.Clear();
        return finds = read;
    }

    /// <summary>Writes the finds beside their file and then moves them there, so a crash mid-write never leaves half a file. Call under the guard.</summary>
    void Save(List<HeatmapFind> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(list, Settings.Json));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error("The 3D map's finds could not be kept on disk; they hold until Studio is closed", problem);
        }
    }
}
