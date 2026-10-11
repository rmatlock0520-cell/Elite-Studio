using System.Text.Json;
using System.Windows.Controls;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Overlay;

namespace EliteStudio.Nav;

/// <summary>What the game has said about one star, planet or moon, as far as the trip's count needs it. Saved as a number.</summary>
[Flags]
public enum BodyMarks
{
    /// <summary>The commander has its scan.</summary>
    Scanned = 1,

    /// <summary>A scan said nobody had discovered it before.</summary>
    Found = 2,

    /// <summary>A scan said nobody had mapped it before.</summary>
    Unmapped = 4,

    /// <summary>The commander mapped it with probes.</summary>
    Probed = 8,
}

/// <summary>One star system of the trip: the one the commander is in, and every one left behind that holds a first.</summary>
public sealed class ExploreSystem
{
    /// <summary>Nobody had discovered the star the ship arrives at; null until the game has written that star's scan.</summary>
    public bool? IsNew { get; set; }

    /// <summary>The system's stars, planets and moons as its discovery scan (the "honk") counted them; 0 before that scan.</summary>
    public int BodyCount { get; set; }

    /// <summary>The stars, planets and moons the game has written about, by its own number for each.</summary>
    public Dictionary<int, BodyMarks> Bodies { get; set; } = [];
}

/// <summary>The "explore" section of settings.json: the trip so far, so it survives a restart.</summary>
public sealed class ExploreSettings
{
    /// <summary>When the trip began: the time on the first journal line counted, or the moment of a reset.</summary>
    public DateTime? SinceUtc { get; set; }

    /// <summary>The trip's exploration data has been sold. The trip is over, and the next jump begins a new one.</summary>
    public bool HandedIn { get; set; }

    /// <summary>Arrivals in a star system, the one the trip began in included. Coming back to a system is another visit.</summary>
    public int Visited { get; set; }

    /// <summary>The game's number for the system the commander is in; 0 before the journal has said.</summary>
    public long Here { get; set; }

    /// <summary>The time on the newest journal line counted. After a restart, lines older than this are not counted again.</summary>
    public DateTime LastUtc { get; set; }

    /// <summary>By the game's number for each system.</summary>
    public Dictionary<long, ExploreSystem> Systems { get; set; } = [];

    /// <summary>Where the last few jumps arrived, oldest first: [x, y, z] in light years. They give the heading.</summary>
    public List<double[]> Trail { get; set; } = [];
}

/// <summary>What the live count shows: the answer to GET /api/nav/explore/trip and the data of every "explore" event.</summary>
/// <param name="NewSystems">Systems whose arrival star nobody had discovered before.</param>
/// <param name="FirstDiscoveries">Stars, planets and moons nobody had discovered before.</param>
/// <param name="FirstMapped">Planets and moons the commander mapped that nobody had mapped before.</param>
/// <param name="System">The system the commander is in now; the next four are about it.</param>
/// <param name="IsNew">Its arrival star had not been discovered; null while the game has not written that star's scan.</param>
/// <param name="Scanned">Its stars, planets and moons scanned so far.</param>
/// <param name="BodyCount">How many it has, by its discovery scan; 0 before that scan.</param>
/// <param name="Position">Where it is: [x, y, z] in light years.</param>
/// <param name="Heading">See <see cref="ExploreTrip.Heading"/>.</param>
public sealed record ExploreView(
    DateTime? SinceUtc, bool HandedIn, int Visited, int NewSystems, int FirstDiscoveries, int FirstMapped,
    string? System, bool? IsNew, int Scanned, int BodyCount, double[]? Position, double? Heading);

/// <summary>
/// The live proof of unexplored space: a running count, kept from the journal, of what the commander
/// found on this trip that nobody had found before. The game says so itself: every scan it writes
/// carries "WasDiscovered" and "WasMapped". They tell what has been sold to the game's cartographers,
/// so a first is only certain once the commander has sold it too: another commander who was there
/// earlier and has not sold yet would still get the name on it.
///
/// A trip runs from one sale of exploration data to the next (or from the Reset button). Once the
/// data is sold the count stays on show until the next jump, which begins a new trip. A ship that
/// is destroyed takes its unsold data with it, so buying it back begins a new trip at once.
///
/// Lives on the UI thread like the game state it follows. A system or a body is only ever counted
/// once, however often the game writes about it: on arrival, after mapping, or on a later visit.
/// </summary>
public sealed class ExploreTrip
{
    const string Section = "explore";
    const int TrailLength = 10;       // jumps the heading is taken over
    const double MinTravelLy = 50;    // less than this between the ends of the trail is no heading: one short hop, or flying in circles
    const BodyMarks MappedFirst = BodyMarks.Unmapped | BodyMarks.Probed;

    readonly GameState game;
    readonly Settings settings;
    readonly ExploreSettings trip;
    readonly Func<DateTime> now;
    bool unsaved;

    /// <param name="now">The clock (UTC) for the moment of a reset; tests pass their own.</param>
    public ExploreTrip(GameState game, Settings settings, Func<DateTime>? now = null)
    {
        (this.game, this.settings, this.now) = (game, settings, now ?? (() => DateTime.UtcNow));
        // The saved trip is a file on disk: it may be from an older Studio or changed by hand, and
        // whatever is in it must not stop Studio from starting. So any problem at all is caught here.
        try
        {
            trip = settings.Get<ExploreSettings>(Section);
            if (trip.Systems is null || trip.Trail is null || trip.Systems.Values.Any(system => system?.Bodies is null) || trip.Trail.Any(at => at is not { Length: 3 }))
                throw new JsonException("The saved trip has parts missing.");
        }
        catch (Exception problem)
        {
            Log.Error("The saved exploration trip could not be read, so Studio Nav starts a new one", problem);
            trip = new ExploreSettings();
        }
        game.Journal += Count;
        game.Changed += Tell;
    }

    /// <summary>Raised after the count changed: at most once for all the journal lines the game wrote in one go.</summary>
    public event Action? Changed;

    public ExploreView View
    {
        get
        {
            ExploreSystem? here = trip.Systems.GetValueOrDefault(trip.Here);
            BodyMarks[] bodies = [.. trip.Systems.Values.SelectMany(system => system.Bodies.Values)];
            return new ExploreView(
                trip.SinceUtc, trip.HandedIn, trip.Visited,
                trip.Systems.Values.Count(system => system.IsNew == true),
                bodies.Count(marks => marks.HasFlag(BodyMarks.Found)),
                bodies.Count(marks => marks.HasFlag(MappedFirst)),
                game.StarSystem, here?.IsNew, here?.Bodies.Values.Count(marks => marks.HasFlag(BodyMarks.Scanned)) ?? 0, here?.BodyCount ?? 0,
                game.StarPos, Heading);
        }
    }

    /// <summary>
    /// The way the commander is travelling: the direction from where the oldest of the last few
    /// jumps arrived to where the newest did, as a bearing on the galaxy map seen from above, in
    /// whole degrees clockwise from "up" (towards larger z, where the galaxy's core lies from Sol).
    /// Null until those jumps have covered some distance.
    /// </summary>
    public double? Heading
    {
        get
        {
            if (trip.Trail is not [var from, .., var to]) return null;
            double east = to[0] - from[0], north = to[2] - from[2];
            if (Math.Sqrt(east * east + north * north) < MinTravelLy) return null;
            return Math.Round(Math.Atan2(east, north) * 180 / Math.PI + 360) % 360;
        }
    }

    /// <summary>Starts a new trip now, in the system the commander is in.</summary>
    public void Reset()
    {
        StartOver();
        // The clock, not the journal: whatever the game wrote before this moment belongs to the trip that was put aside.
        trip.SinceUtc = trip.LastUtc = now();
        unsaved = true;
        Tell();
    }

    /// <summary>
    /// The in-game overlay's one quiet line while the commander is out exploring, or null when not:
    /// docked, the trip's finds sold, or nothing on this trip yet that nobody had found before.
    /// </summary>
    public TextBlock? OverlayLine(string accent)
    {
        ExploreView view = View;
        if (game.Docked || view.HandedIn || view.NewSystems + view.FirstDiscoveries + view.FirstMapped == 0) return null;

        TextBlock line = Draw.Text(14.5).Add("EXPLORING   ", accent, bold: true);
        if (view.IsNew is { } isNew) line.Add(isNew ? "new system" : "already discovered", isNew ? Draw.Green : null, bold: isNew).Add(Draw.Dot, Draw.Dim);
        line.Add(view.BodyCount > 0 ? $"{view.Scanned} of {view.BodyCount} bodies" : Counted(view.Scanned, "body scanned", "bodies scanned"));
        string finds = $"{Counted(view.NewSystems, "new system", "new systems")}, {Counted(view.FirstDiscoveries, "first discovery", "first discoveries")}"
            + (view.FirstMapped > 0 ? $", {Draw.Whole(view.FirstMapped)} first mapped" : "");
        return line.Add($"{Draw.Dot}trip: {finds}", Draw.Dim);
    }

    static string Counted(int number, string one, string many) => $"{Draw.Whole(number)} {(number == 1 ? one : many)}";

    /// <summary>Takes one journal line into the count. Lines about anything else change nothing.</summary>
    void Count(JournalLine line)
    {
        // Catching up after a start: a line older than the newest one counted before Studio was
        // closed has been counted. One just as old may have been, and is taken again without harm,
        // because nothing is counted twice. Newer ones were written while Studio was closed.
        if (line.Replay && line.TimeUtc < trip.LastUtc) return;
        JsonElement e = line.Data;
        switch (line.Event)
        {
            case "Location" or "FSDJump" or "CarrierJump":
                Arrive(e);
                break;
            case "Scan":
                Scan(e);
                break;
            case "FSSDiscoveryScan":
                SystemOf(e).BodyCount = (int)e.Whole("BodyCount");
                break;
            case "SAAScanComplete":
                Mark(SystemOf(e), e, BodyMarks.Probed);
                break;
            case "SellExplorationData" or "MultiSellExplorationData":
                trip.HandedIn = true;
                break;
            case "Resurrect" when e.Text("Option") == "rebuy":
                // The ship was destroyed and bought back: what it had found and not sold is gone. The
                // trip starts over wherever the game puts the commander next (a Location line follows).
                trip.Here = 0;
                StartOver();
                break;
            default:
                return;
        }
        if (line.TimeUtc > trip.LastUtc) trip.LastUtc = line.TimeUtc;
        if (line.TimeUtc != default) trip.SinceUtc ??= line.TimeUtc;
        unsaved = true;
    }

    /// <summary>The ship is in a star system. Arriving in another one than before is a visit; logging in where the ship already was is not.</summary>
    void Arrive(JsonElement e)
    {
        long address = e.Whole("SystemAddress");   // the game always writes it, and nothing else tells two systems apart for certain
        if (address == 0 || address == trip.Here) return;
        // A system left behind is remembered only while it holds a first, so that coming back to it counts nothing twice.
        foreach (long left in trip.Systems.Where(system => system.Key != address && !HoldsFirst(system.Value)).Select(system => system.Key).ToList())
            trip.Systems.Remove(left);
        trip.Here = address;
        if (trip.HandedIn) StartOver();   // the last trip's finds are sold: this jump begins the next one
        else trip.Visited++;
        if (e.Position("StarPos") is not { } at) return;
        trip.Trail.Add(at);
        if (trip.Trail.Count > TrailLength) trip.Trail.RemoveAt(0);
    }

    void Scan(JsonElement e)
    {
        // Stars, planets and moons only. The game also writes scans of belt clusters and rings,
        // which a discovery scan does not count among a system's bodies.
        bool star = e.Field("StarType") is not null;
        if (!star && e.Field("PlanetClass") is null) return;
        // A scan that lacks these two (the game left them out years ago) says nothing either way, and counts as no first.
        BodyMarks marks = BodyMarks.Scanned;
        if (e.Field("WasDiscovered") is { ValueKind: JsonValueKind.False }) marks |= BodyMarks.Found;
        if (e.Field("WasMapped") is { ValueKind: JsonValueKind.False }) marks |= BodyMarks.Unmapped;
        ExploreSystem system = SystemOf(e);
        Mark(system, e, marks);
        // The star the ship arrives at stands for the system: "new" is said of that one.
        if (star && e.Field("DistanceFromArrivalLS") is { ValueKind: JsonValueKind.Number } fromArrival && fromArrival.GetDouble() == 0)
            system.IsNew = system.IsNew == true || marks.HasFlag(BodyMarks.Found);
    }

    /// <summary>
    /// Adds to what is known of the body a line is about. Marks are only ever added: the game writes
    /// a body's scan again after it was mapped and on every later arrival, and a mark once earned
    /// must not depend on what such a repeat says.
    /// </summary>
    static void Mark(ExploreSystem system, JsonElement e, BodyMarks marks)
    {
        int body = (int)e.Whole("BodyID");
        system.Bodies[body] = system.Bodies.GetValueOrDefault(body) | marks;
    }

    /// <summary>What the trip remembers of the system a journal line is about: the line names it by its number; failing that, the one the ship is in.</summary>
    ExploreSystem SystemOf(JsonElement e)
    {
        long address = e.Whole("SystemAddress") is not 0 and var named ? named : trip.Here;
        if (!trip.Systems.TryGetValue(address, out ExploreSystem? system)) trip.Systems[address] = system = new ExploreSystem();
        return system;
    }

    static bool HoldsFirst(ExploreSystem system) =>
        system.IsNew == true || system.Bodies.Values.Any(marks => marks.HasFlag(BodyMarks.Found) || marks.HasFlag(MappedFirst));

    /// <summary>A new trip, with nothing counted but the system the commander is in. It begins with the next journal line counted.</summary>
    void StartOver() => (trip.Systems, trip.Visited, trip.SinceUtc, trip.HandedIn) = ([], trip.Here == 0 ? 0 : 1, null, false);

    /// <summary>After the journal lines of one reading: saves what they changed, and says so once.</summary>
    void Tell()
    {
        if (!unsaved) return;
        unsaved = false;
        try
        {
            settings.Set(Section, trip);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error("The exploration trip could not be saved, so it will not survive a restart", problem);
        }
        Changed?.Invoke();
    }
}
