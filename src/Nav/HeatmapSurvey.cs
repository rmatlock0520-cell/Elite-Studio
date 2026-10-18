using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Nav;

/// <summary>
/// A piece of space the 3D map can look inside: one sector of the game's grid, or the cube round a
/// nebula. Spansh is asked about its <see cref="Box"/>, and what it knows there is shown cut into
/// cubes, <see cref="CubeLy"/> on a side to begin with.
/// </summary>
/// <param name="Key">
/// How the page names it, and how it is kept on this PC: "s:" and the sector's numbers along x, y
/// and z ("s:38:33:18"), or "n:" and the nebula's own sector as the game spells it ("n:California Sector").
/// </param>
/// <param name="Box">The box Spansh is asked about. It is a cube, <paramref name="Size"/> light years on every side.</param>
/// <param name="CubeLy">
/// The size of the cubes it is first shown in: 160 ly for a sector (8 to a side, the game's own
/// cubes of mass code e), and about an eighth of the width for a nebula (<see cref="NebulaCubeLy"/>).
/// </param>
public sealed record SurveyArea(string Key, Box Box, double Size, double CubeLy)
{
    /// <summary>Cubes along each side of the area at this cube size; the last row reaches past the far faces when the size does not divide the width.</summary>
    public int CubesAcross(double cubeLy) => Math.Max(1, (int)Math.Ceiling(Size / cubeLy - 1e-6));

    /// <summary>The key of a sector, by its numbers along x, y and z.</summary>
    public static string SectorKey(int x, int y, int z) => string.Create(CultureInfo.InvariantCulture, $"s:{x}:{y}:{z}");

    /// <summary>The key of a nebula's own sector.</summary>
    public static string NebulaKey(Nebula nebula) => "n:" + nebula.Name;

    /// <summary>
    /// The area a key names; null for a key that names nothing Studio knows: a sector outside the
    /// grid, or a nebula that is not in <see cref="HaRegions"/>. A nebula may be named by any name
    /// <see cref="HaRegions.Named"/> takes ("n:california nebula"); the area's own key then spells it as the game does.
    /// </summary>
    public static SurveyArea? Of(string? key)
    {
        key = key?.Trim() ?? "";
        if (key.StartsWith("n:", StringComparison.OrdinalIgnoreCase))
            return HaRegions.Named(key[2..]) is { } nebula ? Round(nebula) : null;
        string[] parts = key.Split(':');
        if (parts.Length != 4 || !parts[0].Equals("s", StringComparison.OrdinalIgnoreCase)
            || !Whole(parts[1], out int x) || !Whole(parts[2], out int y) || !Whole(parts[3], out int z)
            || x >= Galaxy.SectorsAcross || y >= Galaxy.Layers || z >= Galaxy.SectorsAcross)
            return null;
        return new SurveyArea(SectorKey(x, y, z), Galaxy.SectorBounds(x, y, z), Galaxy.SectorLy, HeatmapSurvey.CountCubeLy);

        static bool Whole(string text, out int number) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>
    /// The area round a nebula: the smallest cube that holds all of it, which for a nebula of one
    /// sphere is its centre plus and minus its radius.
    /// </summary>
    public static SurveyArea Round(Nebula nebula)
    {
        Box round = nebula.Bounds;
        double size = Math.Max(round.X1 - round.X0, Math.Max(round.Y1 - round.Y0, round.Z1 - round.Z0));
        (double x0, double x1) = Widen(round.X0, round.X1);
        (double y0, double y1) = Widen(round.Y0, round.Y1);
        (double z0, double z1) = Widen(round.Z0, round.Z1);
        return new SurveyArea(NebulaKey(nebula), new Box(x0, y0, z0, x1, y1, z1), size, NebulaCubeLy(size));

        // A nebula of several spheres can be wider one way than another: its narrower sides are
        // widened evenly to make a cube. A difference of a rounding error is left alone, so that a
        // single sphere's box stays exactly its centre plus and minus its radius.
        (double From, double To) Widen(double from, double to) =>
            size - (to - from) < 1e-6 ? (from, to) : ((from + to - size) / 2, (from + to + size) / 2);
    }

    /// <summary>
    /// The cube size a nebula is first shown in: about an eighth of its width, as the nearest of
    /// 10, 20, 40 and 80 ly counting by doublings. Cubes of these sizes are the game's own (mass
    /// codes a to d), and from 8 to a dozen of them span the nebula.
    /// </summary>
    public static double NebulaCubeLy(double size) =>
        10 * (1 << (int)Math.Clamp(Math.Round(Math.Log2(Math.Max(size, 1) / 80), MidpointRounding.AwayFromZero), 0, 3));
}

/// <summary>What an action would ask of Spansh, told to the player before it is sent (<see cref="HeatmapSurvey.AskAbove"/>).</summary>
/// <param name="Requests">Requests it would send.</param>
/// <param name="Megabytes">About how much would come down the line, in megabytes.</param>
/// <param name="Left">Requests the 3D map may still send in this session (<see cref="HeatmapSurvey.SessionCap"/>).</param>
public sealed record SurveyNeeds(int Requests, double Megabytes, int Left);

/// <summary>One cube of a looked-inside area, with what Spansh knows in it (<see cref="AreaView"/>).</summary>
/// <param name="I">Where it is among the area's cubes, counted from 0 at the area's west face (with <paramref name="J"/> from its bottom and <paramref name="K"/> from its south face).</param>
/// <param name="Known">Systems Spansh knows in it.</param>
/// <param name="Full">Of those, the fully recorded: the databases hold details of at least as many bodies as the discovery scan said the system has.</param>
/// <param name="Part">Partly recorded: the discovery scan was reported, but details of fewer bodies.</param>
/// <param name="Never">Never scanned: no discovery scan of the system has reached the databases.</param>
/// <param name="Missing">Systems that must exist but no database has, placed in this cube (see <see cref="AreaView.Missing"/>).</param>
public sealed record SurveyCube(int I, int J, int K, int Known, int Full, int Part, int Never, long Missing);

/// <summary>A system Spansh knows in a looked-inside area, as the 3D map draws it.</summary>
/// <param name="Id64">The game's own number for the system. Written as text, because these numbers can be larger than a web page can hold exactly.</param>
/// <param name="X">Where it is, in light years (with <paramref name="Y"/> and <paramref name="Z"/>).</param>
/// <param name="State">How well it is recorded: <see cref="Full"/>, <see cref="Part"/> or <see cref="Never"/> (see <see cref="SurveyCube"/>).</param>
public sealed record SurveyedSystem(
    string Name,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Id64,
    double X, double Y, double Z, string State)
{
    public const string Full = "full", Part = "part", Never = "never";
}

/// <summary>
/// A system that must exist but no database has (<see cref="AreaView.Missing"/>), named and placed
/// from the game's numbering alone: it is somewhere in the game's cube that its name names, so it is
/// put at that cube's middle.
/// </summary>
/// <param name="Id64">The game's own number the system must have. Written as text, because these numbers can be larger than a web page can hold exactly.</param>
/// <param name="X">The middle of its cube, in light years (with <paramref name="Y"/> and <paramref name="Z"/>).</param>
public sealed record MissingSystem(
    string Name,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Id64,
    double X, double Y, double Z);

/// <summary>
/// A looked-inside area as it is kept on this PC, cut into cubes for the 3D map
/// (<see cref="HeatmapSurvey.View"/>). The source is Spansh: every system it knows there, whatever
/// it is called, which is why its totals are higher than EDAstro's count of the systems named
/// after a sector.
/// </summary>
/// <param name="At">When Spansh was last asked about it (UTC). What it said is kept for good; the page shows the age.</param>
/// <param name="Count">How many systems Spansh said the area holds.</param>
/// <param name="Capped">Spansh counts no further than 10,000, so the area holds that many or more and cannot be listed.</param>
/// <param name="Complete">Every request of the listing has come back. Only then are the missing systems worked out (see <see cref="Unlisted"/>).</param>
/// <param name="PagesDone">Requests of the listing that have come back (each brings up to 500 systems).</param>
/// <param name="PagesTotal">Requests the whole listing takes.</param>
/// <param name="CubeLy">The size of the cubes, in light years.</param>
/// <param name="N">Cubes along each side of the area.</param>
/// <param name="Corner">The area's corner with the smallest x, y and z, from which the cubes are counted.</param>
/// <param name="Size">Light years along each side of the area.</param>
/// <param name="Cubes">The cubes that hold any system, known or missing; the others are empty. Layer by layer from the bottom, then from south to north, then from west to east.</param>
/// <param name="Systems">Every system listed, in Spansh's order.</param>
/// <param name="Missing">
/// Systems that must exist but no database has. The game numbers the systems of each of its own
/// cubes 0, 1, 2 and so on, so when "… d8-65" is known, so are 0 to 64 somewhere: every number below
/// the highest known that nobody has recorded is a system nobody has passed on. Each is counted in the
/// cube that holds its own cube's middle. Only the game's cubes that lie wholly inside the area
/// count, because the others' systems may be known outside it. At most 10,000 are listed; see <paramref name="MissingTotal"/>.
/// </param>
/// <param name="MissingTotal">All the missing systems, listed or not.</param>
public sealed record AreaView(
    string Key, DateTime At, int Count, bool Capped, bool Complete, int PagesDone, int PagesTotal,
    double CubeLy, int N, double[] Corner, double Size,
    IReadOnlyList<SurveyCube> Cubes, IReadOnlyList<SurveyedSystem> Systems, IReadOnlyList<MissingSystem> Missing, long MissingTotal)
{
    /// <summary>Always true: something is kept for this area. (An area with nothing kept has no view at all.)</summary>
    public bool Stored => true;

    /// <summary>
    /// Of a complete listing, the systems Spansh counted that did not come back in it: Spansh's order is loose, so a
    /// system sent on two pages means another was sent on none; it can also learn of new systems while a listing is
    /// under way, and rows with no name or place are left out. Up to this many of <see cref="Missing"/> may be known
    /// after all, which the page says. 0 for a listing that is not complete.
    /// </summary>
    public int Unlisted { get; init; }

    /// <summary>A newer listing of the area that "Survey again" began, kept beside this complete one until it is complete itself; null when there is none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RenewingListing? Renewing { get; init; }
}

/// <summary>
/// A newer listing of a looked-inside area (<see cref="AreaView.Renewing"/>). "Survey again" keeps it in a file of
/// its own beside the complete listing until it is complete itself, so a new listing stopped or broken off half way
/// never leaves less than the player had: the next survey goes on with it, from its next page.
/// </summary>
/// <param name="At">When its last request came back (UTC).</param>
/// <param name="PagesDone">Its requests that have come back.</param>
/// <param name="PagesTotal">The requests it takes in all.</param>
public sealed record RenewingListing(DateTime At, int PagesDone, int PagesTotal);

/// <summary>What is kept about an area, in short, for the block of sectors (<see cref="HeatmapSurvey.Summary"/>).</summary>
/// <param name="At">When Spansh was last asked about it (UTC).</param>
/// <param name="State">
/// <see cref="Complete"/> (all listed), <see cref="Partial"/> (the listing stopped part way; a new survey carries on)
/// or <see cref="Counted"/>: only counted, either too busy to list (<paramref name="Capped"/>, or found to have passed
/// 10,000 part way through its listing) or not listed yet (the player said no to the listing, or Studio was closed
/// before it began; a new survey lists it from this count).
/// </param>
public sealed record SurveySummary(DateTime At, int Count, bool Capped, string State)
{
    public const string Complete = "complete", Partial = "partial", Counted = "counted";
}

/// <summary>The answer to a survey (<see cref="HeatmapSurvey.Survey"/>).</summary>
/// <param name="Count">How many systems Spansh said the area holds; 0 when it could not be asked.</param>
/// <param name="Needs">Set when nothing was listed because the player is asked first; the listing runs when the page says go.</param>
/// <param name="Area">What is kept for the area now, whole or in part; null when nothing is.</param>
/// <param name="Problem">Why the survey did not finish, in the player's words: Spansh failed, or the session's requests are used up.</param>
/// <param name="Stopped">The player stopped it (<see cref="HeatmapSurvey.Cancel"/>). What came back before that is kept.</param>
public sealed record SurveyAnswer(string Key, int Count, bool Capped, SurveyNeeds? Needs, AreaView? Area, string? Problem, bool Stopped);

/// <summary>A cube of a busy sector, counted on its own (<see cref="HeatmapSurvey.Count"/>).</summary>
/// <param name="X0">Its corner with the smallest x, y and z, in light years (with <paramref name="Y0"/> and <paramref name="Z0"/>).</param>
/// <param name="Size">Light years along each side.</param>
/// <param name="Known">Systems Spansh knows in it.</param>
/// <param name="Capped">Spansh counts no further than 10,000: the cube holds that many or more.</param>
/// <param name="At">When it was counted (UTC). Kept for good.</param>
public sealed record CountedCube(double X0, double Y0, double Z0, double Size, int Known, bool Capped, DateTime At);

/// <summary>The answer to a count of cubes (<see cref="HeatmapSurvey.Count"/>).</summary>
/// <param name="Kind">"cube", "column" or "around".</param>
/// <param name="Cubes">The action's cubes that have a count, new or kept from before, in the action's order.</param>
/// <param name="Needs">Set when the rest were not counted because the player is asked first.</param>
/// <param name="Problem">Why not every cube was counted, in the player's words.</param>
/// <param name="Stopped">The player stopped it. The cubes counted before that are kept.</param>
public sealed record CountAnswer(string Kind, IReadOnlyList<CountedCube> Cubes, SurveyNeeds? Needs, string? Problem, bool Stopped);

/// <summary>How a heat-map job is getting on (<see cref="HeatmapSurvey.Progress"/>).</summary>
/// <param name="Key">The area's key, or "c:" and the kind for a count of cubes.</param>
/// <param name="Page">Requests of the job that have come back.</param>
/// <param name="Pages">Requests the job takes; 0 while that is not known yet.</param>
/// <param name="State">One of <see cref="HeatmapSurvey.Waiting"/>, <see cref="HeatmapSurvey.Counting"/>, <see cref="HeatmapSurvey.Listing"/>, <see cref="HeatmapSurvey.Done"/>, <see cref="HeatmapSurvey.Stopped"/> or <see cref="HeatmapSurvey.Failed"/>.</param>
public sealed record SurveyProgress(string Key, int Page, int Pages, string State);

/// <summary>What came of looking systems up by their exact names (<see cref="HeatmapSurvey.LookUpNames"/>).</summary>
/// <param name="Found">The systems Spansh has under those names; null when it could not be asked, or failed.</param>
/// <param name="Complete">Spansh sent every system it found under them (see <see cref="Spansh.SystemsNamed"/>).</param>
/// <param name="Problem">Why there is no answer, in the player's words; null when there is one.</param>
/// <param name="UsedUp">Nothing was sent: the 3D map's requests for the session are used up.</param>
/// <param name="NotSent">
/// Nothing was sent, and nothing counted: the lookup was no longer wanted by the time its turn came (the player
/// switched the check off, cleared the route or plotted another while it waited behind another of the map's jobs).
/// </param>
public sealed record NamesLookUp(IReadOnlyList<NamedSystem>? Found, bool Complete, string? Problem, bool UsedUp, bool NotSent = false)
{
    /// <summary>
    /// One for each system asked about, in the order given: the system as Within reach's answer had it by the time this
    /// lookup's turn came (<see cref="HeatmapSurvey.SentNear"/>), taken from there and left out of the request; null for
    /// the others. Empty when nothing was looked at (<see cref="NotSent"/>). <see cref="Found"/>,
    /// <see cref="Complete"/> and <see cref="Problem"/> are of the others only.
    /// </summary>
    public IReadOnlyList<NamedSystem?> Near { get; init; } = [];

    /// <summary>A request went to Spansh, and was counted: false when it was not wanted any more, the session's requests are used up, or every system was in Within reach's answer.</summary>
    public bool Sent { get; init; }
}

/// <summary>What came of looking for the systems within a distance of a point (<see cref="HeatmapSurvey.SystemsNear"/>).</summary>
/// <param name="Found">The systems Spansh sent, nearest first; null when it could not be asked, or failed.</param>
/// <param name="Count">How many Spansh knows there: more than it sent when the answer was cut short (<see cref="Capped"/>).</param>
/// <param name="Problem">Why there is no answer, in the player's words; null when there is one, and when the request was held back only to keep the session's last requests (<see cref="Held"/>).</param>
/// <param name="Held">Nothing was sent: the 3D map's requests for the session are used up, or fewer are left than the asker keeps for others.</param>
/// <param name="NotSent">Nothing was sent, and nothing counted: the lookup was no longer wanted by the time its turn came.</param>
public sealed record NearLookUp(IReadOnlyList<NamedSystem>? Found, int Count, string? Problem, bool Held, bool NotSent = false)
{
    /// <summary>Spansh knows more systems there than it sent (it sends <see cref="Spansh.PageSize"/> at most).</summary>
    public bool Capped => Found is { } found && Count > found.Count;
}

/// <summary>
/// The 3D map's questions to Spansh, and what came back, kept on this PC: how many systems a sector,
/// a nebula or a single cube holds, and for the less busy ones which systems they are, sorted into cubes.
///
/// Spansh is one person's free service, and a listing can take twenty requests of over 2 MB each, so
/// this is a careful guest, more so than anywhere else in Studio:
/// - nothing is asked without a click, and nothing is asked again by itself after a failure;
/// - the map's requests go one at a time through a gate of their own, so a long listing never
///   holds up route plotting or name suggestions, which ask Spansh directly;
/// - an action that needs more than <see cref="AskAbove"/> requests says so first and waits for a go;
/// - one session of Studio sends at most <see cref="SessionCap"/> of them;
/// - what comes back is kept for good (the page shows its age, and the player may survey again
///   whenever they like), and a listing stopped or broken off part way keeps its finished requests,
///   so the next survey carries on where it stopped instead of starting over. A count is kept the
///   moment it comes, even when the player then says no to the listing; "Survey again" keeps its new
///   listing beside the complete one until it is complete too, so nothing kept is ever replaced by less;
///   and a kept file that cannot be read is never written over: one another program holds just now is
///   left alone (nothing is asked), a damaged one is moved aside for the owner to send in.
/// Everything is awaited, never blocked on. Spansh's answers are read away from the calling thread
/// (see <see cref="Spansh.ListBoxPage"/>), and so are the files kept here and the sorting into cubes.
/// </summary>
/// <param name="spansh">The app's one client for Spansh, the same one the route plotter uses.</param>
/// <param name="folder">Where the results are kept: the 3D map's folder in Studio's data folder. It is made when the first result is kept.</param>
public sealed class HeatmapSurvey(Spansh spansh, string folder)
{
    /// <summary>Requests to Spansh the 3D map may send in one session of Studio. Past them it refuses, in plain words, until Studio is started again.</summary>
    public const int SessionCap = 200;

    /// <summary>An action that needs more requests than this asks the player first (<see cref="SurveyNeeds"/>).</summary>
    public const int AskAbove = 5;

    /// <summary>
    /// Light years along a side of the cubes of a busy sector that are counted one by one, and of a
    /// sector's cubes when it is looked inside: the game's own cubes of mass code e, 8 to a sector's side.
    /// </summary>
    public const double CountCubeLy = 160;

    /// <summary>What <see cref="SurveyProgress.State"/> says.</summary>
    public const string Waiting = "waiting", Counting = "counting", Listing = "listing", Done = "done", Stopped = "stopped", Failed = "failed";

    // What comes down the line, measured in October 2026: a page of 500 systems is 212 to 235 KB
    // (Spansh packs it), a count about 5 KB.
    const double KbPerSystem = 0.47, KbPerCount = 5;

    /// <summary>Missing systems listed one by one at most; more are counted but not listed (a busy cube can lack thousands).</summary>
    const int MostMissingListed = 10_000;

    const string CountedFile = "counted.json";

    // One line per system in the area files, so the files stay small; the rest of Studio's style otherwise.
    static readonly JsonSerializerOptions FileJson = new(Settings.Json) { WriteIndented = false };

    /// <summary>What a kept file says when another program holds it just now, so that it cannot be read.</summary>
    const string Unreadable = "Studio could not read what it keeps on this PC for this sector or nebula just now: another program may have the file open. Nothing was asked of Spansh, so nothing kept was replaced. Try again in a moment.";

    readonly SemaphoreSlim gate = new(1, 1);   // one heat-map job at a time
    readonly Lock disk = new();                // Studio's own reading of a kept file, and the moving of a newer copy into its place
    readonly Lock guard = new();               // for everything below, which pages and jobs touch from more than one thread
    readonly Dictionary<string, SurveySummary?> summaries = [];
    Dictionary<(double X0, double Y0, double Z0, double Size), CountedCube>? counted;   // counted.json and the counts since; null until it could be read
    readonly Dictionary<(double X0, double Y0, double Z0, double Size), CountedCube> waiting = [];   // counts made while counted.json could not be read
    bool countedHeld;                          // counted.json could not be read the last time it was tried (said in the log once)
    CancellationTokenSource stop = new();
    int used;
    // Spansh's answers round a point this session (SystemsNear: Within reach, after a jump), the newest first, each by
    // number and by name: what the checks look in before they ask Spansh about the same systems again.
    readonly List<(Dictionary<long, NamedSystem> ById, Dictionary<string, NamedSystem> ByName)> sentNear = [];

    /// <summary>Spansh's answers round a point kept for the checks (<see cref="SentNear"/>), at most: Within reach keeps as many.</summary>
    const int NearKept = 8;

    /// <summary>Requests the 3D map has sent to Spansh in this session.</summary>
    public int Used => Volatile.Read(ref used);

    /// <summary>
    /// A system Spansh sent this session in one of its last answers round a point (<see cref="SystemsNear"/>: Within reach
    /// asks after every jump outside the detailed map), by its number, or by its name when the numbers do not say
    /// otherwise; null when none of them has it. Such a system is recorded: the checks of a target, a route and Nav scan
    /// take it from here rather than spend another request on what Spansh said a moment ago. Kept in memory only (at
    /// most <see cref="NearKept"/> answers of 500 systems), never written to disk.
    /// </summary>
    public NamedSystem? SentNear(long id64, string name)
    {
        lock (guard)
        {
            foreach ((Dictionary<long, NamedSystem> byId, Dictionary<string, NamedSystem> byName) in sentNear)
            {
                if (id64 > 0 && byId.TryGetValue(id64, out NamedSystem? numbered)) return numbered;
                if (byName.TryGetValue(name.Trim(), out NamedSystem? named) && (id64 <= 0 || named.Id64 <= 0 || named.Id64 == id64)) return named;
            }
        }
        return null;
    }

    /// <summary>Keeps an answer round a point for <see cref="SentNear"/>, letting the oldest go.</summary>
    void KeepNear(IReadOnlyList<NamedSystem> systems)
    {
        var byId = new Dictionary<long, NamedSystem>();
        var byName = new Dictionary<string, NamedSystem>(StringComparer.OrdinalIgnoreCase);
        foreach (NamedSystem system in systems)
        {
            if (system.Id64 > 0) byId.TryAdd(system.Id64, system);
            byName.TryAdd(system.Name.Trim(), system);
        }
        lock (guard)
        {
            sentNear.Insert(0, (byId, byName));
            if (sentNear.Count > NearKept) sentNear.RemoveAt(sentNear.Count - 1);
        }
    }

    /// <summary>Lets go of Spansh's answers round a point ("Forget all checks": every system is checked afresh).</summary>
    public void ForgetNear()
    {
        lock (guard) sentNear.Clear();
    }

    /// <summary>The same as <see cref="SessionCap"/>, for the page's budget line.</summary>
    public int Cap => SessionCap;

    /// <summary>
    /// Where the results are kept: the 3D map's folder. The detailed map (<see cref="FineMap"/>) keeps its files
    /// beside them, and reads Spansh's list of every system from here.
    /// </summary>
    public string Folder => folder;

    /// <summary>
    /// Raised as a job gets on: when it has to wait for another, when it starts, after each request
    /// that came back, and when it ends. Raised on the thread the job runs on: in the app, the UI thread.
    /// </summary>
    public event Action<SurveyProgress>? Progress;

    /// <summary>
    /// Stops the job that is running after the request under way (its answer is kept), and the
    /// jobs waiting their turn before they ask anything. Gives true when there was one.
    /// </summary>
    public bool Cancel()
    {
        CancellationTokenSource stopping;
        lock (guard) (stopping, stop) = (stop, new CancellationTokenSource());
        bool busy = gate.CurrentCount == 0;
        stopping.Cancel();
        return busy;
    }

    // ------------------------------------------------------------ looking inside a sector or a nebula

    /// <summary>
    /// Looks inside an area: counts the systems Spansh knows there, and lists them all when there
    /// are fewer than 10,000. When that would take more than <see cref="AskAbove"/> requests in all,
    /// the count included, the listing is not started until <paramref name="go"/> is true; the
    /// answer then says what it still needs. The count is kept on disk the moment it comes, so a
    /// listing the player says no to (or that Studio is closed before) starts from it later
    /// without counting again. A listing that stopped part way carries on from where it stopped
    /// without a new count; a finished one is started again from a new count (the player's "Survey
    /// again"), and the new listing is kept beside the finished one until it is finished too. An
    /// area counted before as too busy to list is answered from what is kept.
    /// Every job that has said it is under way (<see cref="Progress"/>) says when it is over, also
    /// when it stops to ask the player first, so that no open page shows a job that has ended.
    /// </summary>
    /// <exception cref="ArgumentException">The key names no sector or nebula.</exception>
    public async Task<SurveyAnswer> Survey(string key, bool go)
    {
        SurveyArea area = SurveyArea.Of(key) ?? throw new ArgumentException("Studio knows no sector or nebula by that name.", nameof(key));
        DateTime asked = DateTime.UtcNow;
        CancellationToken stopping = StopToken();
        (bool entered, bool waited) = await Enter(area.Key, stopping);
        if (!entered) return new SurveyAnswer(area.Key, 0, false, null, null, null, Stopped: true);
        try
        {
            // Read with the gate held, so that no listing writes these files meanwhile. A damaged one is moved aside
            // (kept for the owner) before anything new is written in its place.
            (AreaRecord? kept, bool held) = await Task.Run(() => Read(area, FileOf(area), setAside: true));
            // A listing "Survey again" began beside a complete one, to carry on with.
            bool renewing = kept is { Complete: true };
            AreaRecord? beside = null;
            if (renewing) (beside, held) = await Task.Run(() => Read(area, SideOf(area), setAside: true));
            if (held)
            {
                // Another program has a kept file open just now. Asking Spansh would end in writing over what
                // could not be read, so nothing is asked; the player's next click tries again.
                if (waited) Report(area.Key, 0, 0, Failed);
                return new SurveyAnswer(area.Key, 0, false, null, null, Unreadable, false);
            }

            // Kept as it is, without asking: an area too busy to list stays so (Spansh's counts only
            // grow), and a listing finished while this call waited its turn is the one it was after
            // (a second click, or the page asking again after a reload).
            if (kept is not null && (kept.Count.Capped || kept.Complete && kept.UpdatedUtc >= asked))
            {
                // It said it was waiting, so it says it is over: other open pages would show it waiting on.
                if (waited) Report(area.Key, 0, 0, Done);
                return new SurveyAnswer(area.Key, kept.Count.Count, kept.Count.Capped, null, await Task.Run(() => Shown(area, kept, area.CubeLy)), null, false);
            }

            AreaRecord? run = Unfinished(kept, beside);
            int sent = 0;
            if (run is null)
            {
                if (Refusal() is { } refused)
                {
                    Report(area.Key, 0, 0, Failed);
                    return new SurveyAnswer(area.Key, 0, false, null, await View(area.Key), refused, false);
                }
                Report(area.Key, 0, 0, Counting);
                BoxCount count;
                try
                {
                    count = await Ask(() => spansh.CountBox(area.Box));
                    sent = 1;
                }
                catch (SpanshException failed)
                {
                    Report(area.Key, 0, 0, Failed);
                    return new SurveyAnswer(area.Key, 0, false, null, await View(area.Key), failed.Message, false);
                }
                run = new AreaRecord(area.Key, count, DateTime.UtcNow);
                if (count.Capped || count.Count == 0)
                {
                    // Nothing to list: too busy (the map then offers counts of single cubes), or empty. That is the
                    // area's news, so it takes the place of what was kept before.
                    await Save(area, run);
                    Report(area.Key, 0, 0, Done);
                    return new SurveyAnswer(area.Key, count.Count, count.Capped, null, await Task.Run(() => Shown(area, run, area.CubeLy)), null, false);
                }
                // Kept on disk at once: the request is not lost, nor sent again, when the player says no to the
                // listing or Studio is closed before it. A complete listing kept from before stays where it is, with
                // the new one beside it until that one is complete too.
                await Save(area, run, aside: renewing);
            }

            // The player is asked first when the click would send more than five requests in all,
            // the count just made included; the answer says what is still to be sent.
            IReadOnlyList<int> pending = run.Pending;
            if (!go && sent + pending.Count > AskAbove)
            {
                // Nothing more is asked until the player says yes, so the job is over for now and says so: without
                // this last word, other open pages (or this one, reloaded) would show the count as still under way.
                if (waited || sent > 0) Report(area.Key, sent, sent, Done);
                return new SurveyAnswer(area.Key, run.Count.Count, false, Needs(pending.Count, pending.Sum(run.RowsDue) * KbPerSystem), null, null, false);
            }
            return await List(area, run, renewing, stopping);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// A listing to carry on with, without a new count: the one kept for the area when it stopped
    /// part way (or was only counted: the player said no, or Studio was closed, before its first
    /// page); or, beside a complete one (<paramref name="kept"/>), the newer listing "Survey again"
    /// began (<paramref name="beside"/>). Null when the area is to be counted first.
    /// </summary>
    static AreaRecord? Unfinished(AreaRecord? kept, AreaRecord? beside) =>
        kept is { Count.Capped: false, Complete: false } ? kept
        // A listing beside one that is newer than it is left over from before (it could not be deleted): not that.
        : kept is { Complete: true } && beside is { Count.Capped: false, Complete: false } && beside.CountedUtc >= kept.UpdatedUtc ? beside
        : null;

    /// <summary>
    /// Asks for the pages of a listing that are not on disk yet, lowest first, one at a time, and
    /// keeps each as it comes. It stops at the first failure without asking again, when the player
    /// stops it, or when the session's requests are used up. Pages are picked up again by number,
    /// never "after the last system seen": Spansh's order by system number is loose.
    /// A listing that is to take the place of a complete one (<paramref name="renewing"/>) is kept
    /// beside it until it is complete itself (or found too busy to list); until then the answer is
    /// the complete one, which says how far the new one has got.
    /// </summary>
    async Task<SurveyAnswer> List(SurveyArea area, AreaRecord run, bool renewing, CancellationToken stopping)
    {
        string? problem = null;
        bool stopped = false;
        bool inPlace = !renewing;   // the area's own file holds this listing
        Report(area.Key, run.PagesDone.Count, run.PagesTotal, Listing);
        while (run.Pending is [int page, ..])
        {
            if (stopping.IsCancellationRequested)
            {
                stopped = true;
                break;
            }
            if (Refusal() is { } refused)
            {
                problem = refused;
                break;
            }
            BoxPage got;
            try
            {
                got = await Ask(() => spansh.ListBoxPage(area.Box, page));
            }
            catch (SpanshException failed)
            {
                problem = failed.Message;
                break;
            }
            run.Add(got, DateTime.UtcNow);
            bool final = !renewing || run.Complete || run.Count.Capped;
            await Save(area, run, aside: !final);
            inPlace |= final;
            Report(area.Key, run.PagesDone.Count, run.PagesTotal, Listing);
            if (got.Count.Capped)
            {
                // Spansh learned of enough new systems since the count to pass its limit.
                problem = "Spansh now knows 10,000 systems or more here, too many to list. What was listed is kept; count its cubes one at a time instead.";
                break;
            }
        }
        Report(area.Key, run.PagesDone.Count, run.PagesTotal, stopped ? Stopped : problem is null ? Done : Failed);
        if (run.Complete) Log.Info($"3D map: {area.Key} listed, {run.Systems.Count} systems from Spansh");

        // A new listing still beside a complete one is not shown yet: the answer is the complete one, as on disk.
        AreaView? view = inPlace ? await Task.Run(() => Shown(area, run, area.CubeLy)) : await View(area.Key);
        return new SurveyAnswer(area.Key, run.Count.Count, run.Count.Capped, null, view, problem, stopped);
    }

    /// <summary>
    /// What is kept for an area, cut into cubes of <paramref name="cubeLy"/> light years (the area's
    /// own size when null); null when nothing is kept. Asks nothing: another cube size is worked out
    /// from the systems already on this PC.
    /// </summary>
    /// <exception cref="ArgumentException">The key names no sector or nebula.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Not a size the game's cubes come in (10 ly doubling up to 1,280).</exception>
    public async Task<AreaView?> View(string key, double? cubeLy = null)
    {
        SurveyArea area = SurveyArea.Of(key) ?? throw new ArgumentException("Studio knows no sector or nebula by that name.", nameof(key));
        double size = cubeLy ?? area.CubeLy;
        if (!Enumerable.Range(0, 8).Any(mass => Galaxy.CubeLy(mass) == size))
            throw new ArgumentOutOfRangeException(nameof(cubeLy), cubeLy, "Cubes are 10, 20, 40, 80, 160, 320, 640 or 1,280 light years on a side.");
        // A look never moves a damaged file aside: a listing may be writing the area's files while the page looks.
        return await Task.Run(() => Read(area, FileOf(area), setAside: false).Record is { } record ? Shown(area, record, size) : null);
    }

    /// <summary>
    /// What is kept for an area, in short; null when nothing is. Only the first line of the area's
    /// file is read, so a whole block of sectors can be shown at once.
    /// </summary>
    public SurveySummary? Summary(string key)
    {
        if (SurveyArea.Of(key) is not { } area) return null;
        lock (guard)
        {
            if (summaries.TryGetValue(area.Key, out SurveySummary? known)) return known;
        }
        SurveySummary? read = null;
        string file = FileOf(area);
        try
        {
            string? first = null;
            lock (disk)
            {
                if (File.Exists(file))
                {
                    using var reader = new StreamReader(file, Encoding.UTF8);
                    first = reader.ReadLine();
                }
            }
            read = Heading(first, area.Key) is { } heading ? AreaRecord.From(heading).Summary : null;
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            // Another program has the file just now: read again next time, not remembered as nothing kept.
            Log.Error($"The 3D map's kept survey of {area.Key} could not be read just now", problem);
            return null;
        }
        catch (JsonException problem)
        {
            Log.Error($"The 3D map's kept survey of {area.Key} could not be read", problem);
        }
        lock (guard) return summaries.TryAdd(area.Key, read) ? read : summaries[area.Key];   // a job may have kept a newer one meanwhile
    }

    // ------------------------------------------------------------ counting single cubes

    /// <summary>
    /// The cubes of 160 ly an action counts, each on the game's grid of such cubes, which runs
    /// through every sector's corner:
    /// - "cube": the one that holds the point;
    /// - "column": the 8 of the point's sector straight above one another, through the point;
    /// - "around": the 27 round the point's cube and that cube itself, three by three by three.
    /// In the order layer by layer from the bottom, then from south to north, then from west to east.
    /// </summary>
    /// <exception cref="ArgumentException">Not one of those kinds.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The point is not in the galaxy's grid.</exception>
    public static IReadOnlyList<Box> CubesFor(string kind, double x, double y, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            throw new ArgumentOutOfRangeException(nameof(x), "A place needs three numbers.");
        (int sx, int sy, int sz) = Galaxy.SectorOf(x, y, z);
        if (sx is < 0 or >= Galaxy.SectorsAcross || sy is < 0 or >= Galaxy.Layers || sz is < 0 or >= Galaxy.SectorsAcross)
            throw new ArgumentOutOfRangeException(nameof(x), "That place is outside the galaxy's sectors.");

        // Counted from the grid's corner, so that the corners come out as exact whole light years
        // (stepping back from the point by the remainder leaves a trace of rounding behind).
        static double Snap(double at, double origin) => origin + Math.Floor((at - origin) / CountCubeLy) * CountCubeLy;
        (double x0, double y0, double z0) = (Snap(x, Galaxy.OriginX), Snap(y, Galaxy.OriginY), Snap(z, Galaxy.OriginZ));
        Box Cube(double cx, double cy, double cz) => new(cx, cy, cz, cx + CountCubeLy, cy + CountCubeLy, cz + CountCubeLy);

        switch (kind?.Trim().ToLowerInvariant())
        {
            case "cube":
                return [Cube(x0, y0, z0)];
            case "column":
                double bottom = Galaxy.SectorBounds(sx, sy, sz).Y0;
                return [.. Enumerable.Range(0, (int)(Galaxy.SectorLy / CountCubeLy)).Select(up => Cube(x0, bottom + up * CountCubeLy, z0))];
            case "around":
                return [.. from dy in new[] { -1, 0, 1 }
                           from dz in new[] { -1, 0, 1 }
                           from dx in new[] { -1, 0, 1 }
                           select Cube(x0 + dx * CountCubeLy, y0 + dy * CountCubeLy, z0 + dz * CountCubeLy)];
            default:
                throw new ArgumentException("Studio counts a cube, a column or the cubes around you.", nameof(kind));
        }
    }

    /// <summary>
    /// Counts the systems Spansh knows in each cube of an action (<see cref="CubesFor"/>), one
    /// request a cube, for sectors too busy to list. Cubes counted before are not asked again unless
    /// <paramref name="again"/> is true. More than <see cref="AskAbove"/> cubes to ask about are not
    /// asked until <paramref name="go"/> is true; the answer then says what it needs. Each count is
    /// kept as it comes.
    /// </summary>
    /// <exception cref="ArgumentException">Not a kind <see cref="CubesFor"/> knows.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The point is not in the galaxy's grid.</exception>
    public async Task<CountAnswer> Count(string kind, double x, double y, double z, bool go, bool again = false)
    {
        IReadOnlyList<Box> cubes = CubesFor(kind, x, y, z);
        kind = kind.Trim().ToLowerInvariant();
        string key = "c:" + kind;
        DateTime asked = DateTime.UtcNow;
        Box[] ask = [.. cubes.Where(cube => again || Kept(cube) is null)];
        if (ask.Length == 0) return new CountAnswer(kind, Results(cubes), null, null, false);
        if (!go && ask.Length > AskAbove) return new CountAnswer(kind, Results(cubes), Needs(ask.Length, ask.Length * KbPerCount), null, false);

        CancellationToken stopping = StopToken();
        if (!(await Enter(key, stopping)).In) return new CountAnswer(kind, Results(cubes), null, null, Stopped: true);
        try
        {
            // Cubes counted while this call waited its turn (a second click) are not asked again.
            ask = [.. ask.Where(cube => Kept(cube) is not { } before || before.At < asked)];
            string? problem = null;
            bool stopped = false;
            int done = 0;
            Report(key, 0, ask.Length, Counting);
            foreach (Box cube in ask)
            {
                if (stopping.IsCancellationRequested)
                {
                    stopped = true;
                    break;
                }
                if (Refusal() is { } refused)
                {
                    problem = refused;
                    break;
                }
                BoxCount count;
                try
                {
                    count = await Ask(() => spansh.CountBox(cube));
                }
                catch (SpanshException failed)
                {
                    problem = failed.Message;
                    break;
                }
                await Keep(new CountedCube(cube.X0, cube.Y0, cube.Z0, cube.X1 - cube.X0, count.Count, count.Capped, DateTime.UtcNow));
                Report(key, ++done, ask.Length, Counting);
            }
            Report(key, done, ask.Length, stopped ? Stopped : problem is null ? Done : Failed);
            return new CountAnswer(kind, Results(cubes), null, problem, stopped);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Every cube counted so far, in every session: layer by layer from the bottom, then from south to north, then from west to east.</summary>
    public IReadOnlyList<CountedCube> Counted
    {
        get
        {
            lock (guard) return [.. LoadCounted().Values.OrderBy(cube => cube.Y0).ThenBy(cube => cube.Z0).ThenBy(cube => cube.X0).ThenBy(cube => cube.Size)];
        }
    }

    CountedCube? Kept(Box cube)
    {
        lock (guard) return LoadCounted().GetValueOrDefault((cube.X0, cube.Y0, cube.Z0, cube.X1 - cube.X0));
    }

    IReadOnlyList<CountedCube> Results(IEnumerable<Box> cubes) => [.. cubes.Select(Kept).OfType<CountedCube>()];

    /// <summary>
    /// Keeps a new count, in place of an older one of the same cube, and writes them all to disk. While the file
    /// of counts cannot be read, the new count waits in memory instead: writing then would put this session's
    /// counts in place of every earlier one.
    /// </summary>
    async Task Keep(CountedCube cube)
    {
        CountedCube[] all;
        lock (guard)
        {
            LoadCounted()[(cube.X0, cube.Y0, cube.Z0, cube.Size)] = cube;
            if (counted is null)
            {
                Log.Error("A count of the 3D map is held in memory until the file of counted cubes can be read again; it is lost if Studio is closed first");
                return;
            }
            all = [.. counted.Values];
        }
        try
        {
            await Task.Run(() => Write(Path.Combine(folder, CountedFile), writer => writer.Write(JsonSerializer.Serialize(all, Settings.Json))));
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error("The 3D map's counted cubes could not be kept on disk, so they will be gone after a restart", problem);
        }
    }

    /// <summary>
    /// The counted cubes (call under the guard): counted.json, read the first time it can be, with the counts made
    /// since. A file another program holds just now (a virus scanner, say) is tried again next time, and the counts
    /// made meanwhile wait in <see cref="waiting"/>. A file that is not a list of counted cubes at all is moved aside,
    /// kept for the owner to send in, and counting starts afresh. No writer is at work while this reads: counts are
    /// written only once the file has been read.
    /// </summary>
    Dictionary<(double X0, double Y0, double Z0, double Size), CountedCube> LoadCounted()
    {
        if (counted is not null) return counted;
        string file = Path.Combine(folder, CountedFile);
        var read = new Dictionary<(double X0, double Y0, double Z0, double Size), CountedCube>();
        try
        {
            if (File.Exists(file))
                foreach (CountedCube cube in JsonSerializer.Deserialize<CountedCube[]>(File.ReadAllText(file), Settings.Json) ?? [])
                    read[(cube.X0, cube.Y0, cube.Z0, cube.Size)] = cube;
        }
        catch (JsonException problem)
        {
            if (SetAside(file) is not { } aside) return waiting;   // not even moved: left alone, and tried again next time
            Log.Error($"The 3D map's counted cubes could not be read; the file was kept as {Path.GetFileName(aside)} and counting starts afresh", problem);
            read.Clear();
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            if (!countedHeld) Log.Error("The 3D map's counted cubes could not be read just now; they are read again next time, and nothing is written over them meanwhile", problem);
            countedHeld = true;
            return waiting;
        }
        countedHeld = false;
        foreach (var (at, cube) in waiting) read[at] = cube;   // counted since, so newer than the file's
        waiting.Clear();
        return counted = read;
    }

    // ------------------------------------------------------------ one name lookup, for the finds

    /// <summary>
    /// Looks a system up by its name at Spansh, for the 3D map's finds (<see cref="HeatmapFinds"/>): Spansh's name
    /// search, the same request as the route boxes' name suggestions (<see cref="Spansh.Locate"/>), but sent as one
    /// of the map's own requests: through the map's gate, after any job under way, and counted in the session's
    /// <see cref="SessionCap"/> whether or not it is answered. It is never sent again by itself, and it says nothing
    /// to the page's progress line: no player is waiting on it. Gives the systems Spansh suggested, with their numbers
    /// and places; or, when Spansh could not be asked or failed, why, in the player's words; or (Near), with nothing
    /// sent, the system as Within reach's answer had it once this lookup's turn came (<see cref="SentNear"/>).
    /// </summary>
    /// <param name="id64">The game's number for it; 0 when not known (the name alone is then looked for in Within reach's answers).</param>
    public async Task<(IReadOnlyList<LocatedSystem>? Found, string? Problem, NamedSystem? Near)> LookUp(string name, long id64 = 0)
    {
        await gate.WaitAsync();
        try
        {
            // Within reach's request after a jump holds the gate while Spansh searches (seconds): a target picked just
            // after arriving waits behind it, and the answer it waited for may well list the system. Taken from there.
            if (SentNear(id64, name) is { } near) return (null, null, near);
            if (Refusal() is { } refused) return (null, refused, null);
            try
            {
                // Always sent (not the client's kept answer): the memory of checks has decided this system is to be
                // asked about again, and a request counted in the session's 200 must be one that goes.
                return (await Ask(() => spansh.Locate(name, fresh: true)), null, null);
            }
            catch (SpanshException failed)
            {
                return (null, failed.Message, null);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Looks up to <see cref="Spansh.MostNames"/> systems up by their exact names at Spansh, for the check of a plotted
    /// route (<see cref="HeatmapFinds"/>): one request, sent as one of the map's own as <see cref="LookUp"/> is (through
    /// the map's gate, after any job under way, counted in the session's <see cref="SessionCap"/> whether or not it is
    /// answered, never sent again by itself, and nothing said to the page's progress line). Like the jobs waiting
    /// their turn when the player stops them (<see cref="Cancel"/>), one that is no longer wanted by its turn asks
    /// nothing: the wait behind a survey's listing can be minutes, and the route it was for may be gone by then. Once its
    /// turn has come, the systems Within reach's answer lists by then are taken from it (<see cref="SentNear"/>,
    /// <see cref="NamesLookUp.Near"/>) and left out of the request: that answer is often the very request this one waited
    /// behind (a picture of the Navigation panel taken just after a jump). With none left, nothing is sent or counted.
    /// </summary>
    /// <param name="systems">The systems by name, each with the game's number for it (0 when not known).</param>
    /// <param name="waiting">Told true when the request has to wait for another of the map's jobs first, and false when its turn has come.</param>
    /// <param name="stopping">
    /// Cancelled when the lookup is no longer wanted. Before its turn, or as it comes, nothing is sent and nothing
    /// counted (<see cref="NamesLookUp.NotSent"/>); a request already sent is let finish, as a job's is.
    /// </param>
    public async Task<NamesLookUp> LookUpNames(IReadOnlyList<(string Name, long Id64)> systems, Action<bool>? waiting = null, CancellationToken stopping = default)
    {
        var notSent = new NamesLookUp(null, false, null, UsedUp: false, NotSent: true);
        if (stopping.IsCancellationRequested) return notSent;
        if (!gate.Wait(0))
        {
            waiting?.Invoke(true);
            try
            {
                await gate.WaitAsync(stopping);
            }
            catch (OperationCanceledException)
            {
                return notSent;   // the gate was never held, so there is nothing to give back
            }
            waiting?.Invoke(false);
        }
        try
        {
            if (stopping.IsCancellationRequested) return notSent;
            NamedSystem?[] near = [.. systems.Select(system => SentNear(system.Id64, system.Name))];
            List<string> asking = [.. systems.Where((_, at) => near[at] is null).Select(system => system.Name)];
            if (asking.Count == 0) return new NamesLookUp([], true, null, UsedUp: false) { Near = near };
            if (Refusal() is { } refused) return new NamesLookUp(null, false, refused, UsedUp: true) { Near = near };
            try
            {
                (IReadOnlyList<NamedSystem> found, bool complete) = await Ask(() => spansh.SystemsNamed(asking));
                return new NamesLookUp(found, complete, null, UsedUp: false) { Near = near, Sent = true };
            }
            catch (SpanshException failed)
            {
                return new NamesLookUp(null, false, failed.Message, UsedUp: false) { Near = near, Sent = true };
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The systems Spansh knows within a distance of a point, nearest first, for the 3D map's "Within reach"
    /// (<see cref="HeatmapReach"/>): one request (<see cref="Spansh.SystemsWithin"/>), sent as one of the map's own as
    /// <see cref="LookUpNames"/> is (through the map's gate, after any job under way, counted in the session's
    /// <see cref="SessionCap"/> whether or not it is answered, never sent again by itself, nothing said to the page's
    /// progress line), and given up as that is when no longer wanted by its turn. It is not sent when fewer than
    /// <paramref name="keep"/> of the session's requests are left (counted with the gate held, so nothing slips in
    /// meanwhile): Within reach asks after every jump, and must not use up what the route check and Nav scan need.
    /// What Spansh sends is kept in memory for those checks too (<see cref="SentNear"/>), so they do not ask again about
    /// systems it has just listed.
    /// </summary>
    /// <param name="keep">Requests of the session's that this one must leave for others.</param>
    /// <param name="waiting">Told true when the request has to wait for another of the map's jobs first, and false when its turn has come.</param>
    /// <param name="stopping">Cancelled when the lookup is no longer wanted (<see cref="NearLookUp.NotSent"/>).</param>
    public async Task<NearLookUp> SystemsNear(double x, double y, double z, double radius, int keep = 0, Action<bool>? waiting = null, CancellationToken stopping = default)
    {
        var notSent = new NearLookUp(null, 0, null, Held: false, NotSent: true);
        if (stopping.IsCancellationRequested) return notSent;
        if (!gate.Wait(0))
        {
            waiting?.Invoke(true);
            try
            {
                await gate.WaitAsync(stopping);
            }
            catch (OperationCanceledException)
            {
                return notSent;   // the gate was never held, so there is nothing to give back
            }
            waiting?.Invoke(false);
        }
        try
        {
            if (stopping.IsCancellationRequested) return notSent;
            if (Refusal() is { } refused) return new NearLookUp(null, 0, refused, Held: true);
            if (SessionCap - Used < keep) return new NearLookUp(null, 0, null, Held: true);
            try
            {
                (IReadOnlyList<NamedSystem> systems, int count) = await Ask(() => spansh.SystemsWithin(x, y, z, radius));
                // For the checks too: Nav scan's pictures come mostly right after a jump, of the systems just listed.
                KeepNear(systems);
                return new NearLookUp(systems, count, null, Held: false);
            }
            catch (SpanshException failed)
            {
                return new NearLookUp(null, 0, failed.Message, Held: false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    // ------------------------------------------------------------ the rules every job follows

    CancellationToken StopToken()
    {
        lock (guard) return stop.Token;
    }

    /// <summary>
    /// Waits for the gate: the map's jobs go one at a time. In: false when the player stopped the job
    /// while it waited; it has asked nothing. Waited: it had to wait, and said so (<see cref="Waiting"/>),
    /// so whatever way it ends, it must say that it is over.
    /// </summary>
    async Task<(bool In, bool Waited)> Enter(string key, CancellationToken stopping)
    {
        if (gate.Wait(0)) return (true, false);
        Report(key, 0, 0, Waiting);
        try
        {
            await gate.WaitAsync(stopping);
            return (true, true);
        }
        catch (OperationCanceledException)
        {
            Report(key, 0, 0, Stopped);
            return (false, true);
        }
    }

    /// <summary>
    /// One request to Spansh, counted against the session's requests whether or not it is
    /// answered. It is never sent again by itself. (A request already sent is let finish when the
    /// player stops the job: what it brings is kept, and Spansh's work is not wasted.)
    /// </summary>
    async Task<T> Ask<T>(Func<Task<T>> request)
    {
        Interlocked.Increment(ref used);
        return await request();
    }

    // Closing Studio's window only hides it (the count goes on), so the words say how to exit it.
    string? Refusal() => Used >= SessionCap
        ? $"Studio allows itself {SessionCap} requests to Spansh for the 3D map each time it runs, and they are used up. Everything that came back is kept. To ask for more, exit Studio (right-click its icon near the clock and choose Exit) and start it again."
        : null;

    SurveyNeeds Needs(int requests, double kilobytes) => new(requests, Math.Round(kilobytes / 1024, 2), Math.Max(0, SessionCap - Used));

    void Report(string key, int page, int pages, string state) => Progress?.Invoke(new SurveyProgress(key, page, pages, state));

    // ------------------------------------------------------------ sorting into cubes

    static string StateOf(BoxSystem system) =>
        system.BodyCount is not { } bodies ? SurveyedSystem.Never : system.Bodies >= bodies ? SurveyedSystem.Full : SurveyedSystem.Part;

    sealed class Tally
    {
        public int Known, Full, Part, Never;
        public long Missing;
    }

    /// <summary>
    /// An area's systems sorted into cubes of <paramref name="cubeLy"/> by where they are, with
    /// the missing systems of a complete listing (see <see cref="AreaView.Missing"/>). A system is
    /// placed by its position, not by its number: a heavy star's own cube can be larger than the
    /// cubes shown. A cube owns its near faces: a system on the face between two cubes is in the
    /// one beyond it.
    /// </summary>
    static AreaView Sort(SurveyArea area, AreaRecord record, double cubeLy)
    {
        int n = area.CubesAcross(cubeLy);
        Box box = area.Box;
        var cubes = new Dictionary<(int I, int J, int K), Tally>();
        Tally At(double x, double y, double z)
        {
            var index = (Index(x, box.X0), Index(y, box.Y0), Index(z, box.Z0));
            if (!cubes.TryGetValue(index, out Tally? tally)) cubes[index] = tally = new Tally();
            return tally;
        }
        int Index(double at, double from) => Math.Clamp((int)Math.Floor((at - from) / cubeLy), 0, n - 1);

        var systems = new List<SurveyedSystem>(record.Systems.Count);
        foreach (BoxSystem system in record.Systems)
        {
            string state = StateOf(system);
            Tally tally = At(system.X, system.Y, system.Z);
            tally.Known++;
            if (state == SurveyedSystem.Full) tally.Full++;
            else if (state == SurveyedSystem.Part) tally.Part++;
            else tally.Never++;
            systems.Add(new SurveyedSystem(system.Name, system.Id64, system.X, system.Y, system.Z, state));
        }

        var missing = new List<MissingSystem>();
        long missingTotal = 0;
        // A listing that stopped part way would show the unlisted systems as missing.
        if (record.Complete)
        {
            // The game's own cubes (boxels) the systems lie in, by the address of number 0 in each.
            var boxels = new Dictionary<long, (CubePlace Place, List<BoxSystem> Known, HashSet<long> Numbers)>();
            foreach (BoxSystem system in record.Systems)
            {
                if (SystemAddress.Place(system.Id64) is not { } place) continue;
                CubePlace zero = place with { Number = 0 };
                if (!boxels.TryGetValue(zero.Address, out var boxel)) boxels[zero.Address] = boxel = (zero, [], []);
                boxel.Known.Add(system);
                boxel.Numbers.Add(place.Number);
            }
            foreach (var (_, boxel) in boxels.OrderBy(entry => entry.Key))
            {
                long highest = boxel.Numbers.Max(), gaps = highest + 1 - boxel.Numbers.Count;
                if (gaps == 0 || !Within(box, boxel.Place)) continue;
                (double x, double y, double z) = boxel.Place.Centre;
                At(x, y, z).Missing += gaps;
                missingTotal += gaps;
                if (missing.Count >= MostMissingListed || Namer(boxel.Place, boxel.Known) is not { } name) continue;
                for (long number = 0; number < highest && missing.Count < MostMissingListed; number++)
                    if (!boxel.Numbers.Contains(number))
                        missing.Add(new MissingSystem(name(number), (boxel.Place with { Number = number }).Address, x, y, z));
            }
        }

        return new AreaView(
            area.Key, record.UpdatedUtc, record.Count.Count, record.Count.Capped, record.Complete,
            record.PagesDone.Count(page => page < record.PagesTotal), record.PagesTotal,
            cubeLy, n, [box.X0, box.Y0, box.Z0], area.Size,
            [.. cubes.OrderBy(cube => cube.Key.J).ThenBy(cube => cube.Key.K).ThenBy(cube => cube.Key.I)
                .Select(cube => new SurveyCube(cube.Key.I, cube.Key.J, cube.Key.K, cube.Value.Known, cube.Value.Full, cube.Value.Part, cube.Value.Never, cube.Value.Missing))],
            systems, missing, missingTotal)
        {
            // Every page holds as many rows as Spansh's count says, so each repeat dropped is a system that never came.
            Unlisted = record.Complete ? Math.Max(0, record.Count.Count - record.Systems.Count) : 0,
        };
    }

    /// <summary>An area's listing cut into cubes as the page gets it (<see cref="Sort"/>), with how far a newer listing beside it has got.</summary>
    AreaView Shown(SurveyArea area, AreaRecord record, double cubeLy) => Sort(area, record, cubeLy) with { Renewing = Renewal(area, record) };

    /// <summary>
    /// The newer listing "Survey again" keeps beside a complete one (<see cref="AreaView.Renewing"/>), from the first
    /// line of its file; null when there is none, when it is older than the complete one (left over), or cannot be read.
    /// </summary>
    RenewingListing? Renewal(SurveyArea area, AreaRecord shown)
    {
        if (!shown.Complete) return null;
        string file = SideOf(area);
        try
        {
            string? first = null;
            lock (disk)
            {
                if (!File.Exists(file)) return null;
                using var reader = new StreamReader(file, Encoding.UTF8);
                first = reader.ReadLine();
            }
            if (Heading(first, area.Key) is not { } heading || heading.CountedUtc < shown.UpdatedUtc) return null;
            AreaRecord beside = AreaRecord.From(heading);
            return new RenewingListing(beside.UpdatedUtc, beside.PagesDone.Count(page => page < beside.PagesTotal), beside.PagesTotal);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error($"The newer listing of {area.Key} kept beside the complete one could not be read", problem);
            return null;
        }
    }

    /// <summary>The game's cube lies wholly inside the box, so every system of it that Spansh knows was listed.</summary>
    static bool Within(Box box, CubePlace place)
    {
        (double x, double y, double z) = place.Corner;
        double side = place.CubeLy;
        return x >= box.X0 && y >= box.Y0 && z >= box.Z0 && x + side <= box.X1 && y + side <= box.Y1 && z + side <= box.Z1;
    }

    /// <summary>
    /// How the systems of one of the game's cubes are named, by their number: as a known system of
    /// that cube with a generated name is ("Wredguia WD-K d8-" and the number). When the cube's known
    /// systems all have catalogue names, by the game's rule: after the nebula the cube's middle lies
    /// in (the smallest, as EDTS has it), else after the grid's sector. Null where neither gives a name.
    /// </summary>
    static Func<long, string>? Namer(CubePlace zero, IEnumerable<BoxSystem> known)
    {
        foreach (BoxSystem system in known)
            if (ProcGenNames.Decode(system.Name) is { } decoded && (decoded.Cube with { Number = 0 }).Address == zero.Address)
                return Numbered(decoded.Name);
        (double x, double y, double z) = zero.Centre;
        string? first = HaRegions.Containing(x, y, z).Select(nebula => ProcGenNames.SystemName(zero, nebula.Name)).FirstOrDefault(name => name is not null)
            ?? ProcGenNames.SystemName(zero);
        return first is null ? null : Numbered(first);

        // The name less its number: the number is always last, after a dash when the letters end in a digit.
        static Func<long, string> Numbered(string name)
        {
            string stem = name.TrimEnd("0123456789".ToCharArray());
            return number => stem + number.ToString(CultureInfo.InvariantCulture);
        }
    }

    // ------------------------------------------------------------ keeping it on disk

    /// <summary>
    /// The file an area is kept in. One line of JSON per system, after a first line that says what
    /// the file holds (<see cref="AreaHeading"/>): the block of sectors reads only that line.
    /// </summary>
    string FileOf(SurveyArea area)
    {
        string name = area.Key[2..];
        return Path.Combine(folder, area.Key[0] == 's'
            ? $"sector-{name.Replace(':', '-')}.jsonl"
            : $"nebula-{Paths.FileNameFor(name) ?? string.Concat(name.Where(char.IsAsciiLetterOrDigit))}.jsonl");
    }

    /// <summary>
    /// The file beside it for a newer listing that "Survey again" began over a complete one, in the same layout,
    /// until the new listing is complete and takes the area's own file (<see cref="AreaView.Renewing"/>).
    /// </summary>
    string SideOf(SurveyArea area) => FileOf(area) + ".new";

    /// <summary>The first line of an area's file.</summary>
    /// <param name="Systems">The lines that follow it: a file cut short is not taken for the whole.</param>
    sealed record AreaHeading(string Key, int Count, bool Capped, DateTime CountedUtc, DateTime UpdatedUtc, int[] PagesDone, int Systems);

    static AreaHeading? Heading(string? line, string key) =>
        line is null ? null
        : JsonSerializer.Deserialize<AreaHeading>(line, FileJson) is { } heading && heading.Key == key ? heading
        : throw new JsonException("The file is not the survey of " + key);

    /// <summary>
    /// Keeps an area's listing, whole or in part, in the area's own file; or (<paramref name="aside"/>) in the file
    /// beside it, for a newer listing that is not complete yet while the area's own file holds a complete one.
    /// Written to the area's own file, it takes the place of any listing beside it, which is deleted.
    /// </summary>
    async Task Save(SurveyArea area, AreaRecord record, bool aside = false)
    {
        AreaHeading heading = record.Heading;
        BoxSystem[] systems = [.. record.Systems];
        string file = aside ? SideOf(area) : FileOf(area);
        bool written = false;
        try
        {
            await Task.Run(() => Write(file, writer =>
            {
                writer.Write(JsonSerializer.Serialize(heading, FileJson));
                writer.Write('\n');
                foreach (BoxSystem system in systems)
                {
                    writer.Write(JsonSerializer.Serialize(system, FileJson));
                    writer.Write('\n');
                }
            }));
            written = true;
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error($"What Spansh knows in {area.Key} could not be kept on disk, so it will be asked for again", problem);
        }
        // The block's note of the area is its own file's: a listing beside it counts only once it takes its place.
        if (aside) return;
        lock (guard) summaries[area.Key] = record.Summary;
        if (!written) return;   // the listing beside it, if any, is all the newer one there is
        try
        {
            await Task.Run(() =>
            {
                lock (disk) File.Delete(SideOf(area));
            });
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            // Left over, it is older than the area's own file now, and is passed over (see Unfinished).
            Log.Error($"An older listing of {area.Key} beside its file could not be deleted", problem);
        }
    }

    /// <summary>
    /// An area's listing kept in <paramref name="file"/> (its own, or the one beside it); null when there is none.
    /// Held: another program has the file open just now, so it could not be read and must not be written over.
    /// A damaged file (cut short, or not this area's) counts as not there, and with <paramref name="setAside"/>
    /// (only while the gate is held, so that no listing is writing it) it is first moved aside for the owner to send in.
    /// </summary>
    (AreaRecord? Record, bool Held) Read(SurveyArea area, string file, bool setAside)
    {
        try
        {
            string[] lines;
            lock (disk)
            {
                if (!File.Exists(file)) return (null, false);
                lines = File.ReadAllLines(file, Encoding.UTF8);
            }
            if (Heading(lines.FirstOrDefault(), area.Key) is not { } heading) throw new JsonException("The file is empty");
            AreaRecord record = AreaRecord.From(heading);
            foreach (string line in lines.Skip(1))
                if (line.Length > 0) record.Add(JsonSerializer.Deserialize<BoxSystem>(line, FileJson) ?? throw new JsonException("A line is empty"));
            return record.Systems.Count == heading.Systems ? (record, false) : throw new JsonException("The file is cut short");
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error($"The 3D map's kept survey of {area.Key} could not be read just now; it is left as it is", problem);
            return (null, true);
        }
        catch (JsonException problem)
        {
            string? aside = setAside ? SetAside(file) : null;
            if (setAside && aside is null) return (null, true);   // could not even be moved: left alone
            Log.Error($"The 3D map's kept survey of {area.Key} could not be read, so it counts as not there"
                + (aside is null ? "" : $"; the file was kept as {Path.GetFileName(aside)}"), problem);
            return (null, false);
        }
    }

    /// <summary>
    /// Moves a file that cannot be read out of the way, beside its place and named for when, so that nothing written
    /// later takes its place and the owner can send it in. Gives the new name; null when it could not be moved.
    /// </summary>
    string? SetAside(string file)
    {
        string aside = file + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        try
        {
            lock (disk) File.Move(file, aside);
            return aside;
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error($"{Path.GetFileName(file)} of the 3D map could not be read, nor moved aside; it is left as it is", problem);
            return null;
        }
    }

    /// <summary>
    /// Writes a file beside its place and then moves it there, so that a crash mid-write never
    /// leaves half a file. Windows will not move a file over one that is open, so the move waits
    /// for Studio's own reading of the file to end: the page can look at an area while its listing
    /// goes on, and no copy is lost.
    /// </summary>
    void Write(string file, Action<TextWriter> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".tmp";
        using (var writer = new StreamWriter(temp, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            write(writer);
        lock (disk) File.Move(temp, file, overwrite: true);
    }

    /// <summary>
    /// A listing of an area as it is kept, whole or in part: the count it started from and the
    /// pages that have come back, with their systems, each once.
    /// </summary>
    sealed class AreaRecord(string key, BoxCount count, DateTime countedUtc)
    {
        readonly HashSet<long> seen = [];

        public BoxCount Count { get; private set; } = count;
        public DateTime CountedUtc { get; } = countedUtc;
        public DateTime UpdatedUtc { get; private set; } = countedUtc;
        public SortedSet<int> PagesDone { get; } = [];
        public List<BoxSystem> Systems { get; } = [];

        /// <summary>Pages the whole listing takes; none for an area too busy to list.</summary>
        public int PagesTotal => Count.Capped ? 0 : Count.Pages;

        /// <summary>The pages still to ask for, lowest first.</summary>
        public IReadOnlyList<int> Pending => [.. Enumerable.Range(0, PagesTotal).Where(page => !PagesDone.Contains(page))];

        public bool Complete => !Count.Capped && Pending.Count == 0;

        /// <summary>The systems a page brings, as Spansh's count stands.</summary>
        public int RowsDue(int page) => Math.Clamp(Count.Count - page * Spansh.PageSize, 0, Spansh.PageSize);

        /// <summary>
        /// Takes in a page. A system already listed on an earlier page is not taken twice. Spansh's
        /// count on the newest page is the one that holds from now on (it can grow while a listing is under way).
        /// </summary>
        public void Add(BoxPage page, DateTime at)
        {
            foreach (BoxSystem system in page.Systems) Add(system);
            PagesDone.Add(page.Page);
            (Count, UpdatedUtc) = (page.Count, at);
        }

        public void Add(BoxSystem system)
        {
            if (seen.Add(system.Id64)) Systems.Add(system);
        }

        // A listing that found Spansh past 10,000 part way is too busy to list from then on, like one
        // counted so at first: a new survey answers from what is kept rather than carrying on.
        public SurveySummary Summary => new(UpdatedUtc, Count.Count, Count.Capped,
            Complete ? SurveySummary.Complete : PagesDone.Count > 0 && !Count.Capped ? SurveySummary.Partial : SurveySummary.Counted);

        public AreaHeading Heading => new(key, Count.Count, Count.Capped, CountedUtc, UpdatedUtc, [.. PagesDone], Systems.Count);

        public static AreaRecord From(AreaHeading heading)
        {
            var record = new AreaRecord(heading.Key, new BoxCount(heading.Count, heading.Capped), heading.CountedUtc) { UpdatedUtc = heading.UpdatedUtc };
            foreach (int page in heading.PagesDone) record.PagesDone.Add(page);
            return record;
        }
    }
}
