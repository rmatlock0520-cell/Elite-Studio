using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Nav;

/// <summary>
/// What is asked of Spansh: one of its two route plotters and what that plotter needs. Two equal
/// requests get the same route, which is how a finished route is found again without asking twice.
/// It also says what a route is that Spansh never saw: a <see cref="Trip"/>.
/// </summary>
public sealed record PlotRequest
{
    /// <summary>The neutron plotter: a few long legs from neutron star to neutron star. The game plots each leg.</summary>
    public const string Neutron = "neutron";

    /// <summary>The exact ("galaxy") plotter: every single jump for one ship, with the stars to scoop fuel at.</summary>
    public const string Exact = "exact";

    /// <summary>Not a plotter: stops another studio handed over (<see cref="NavRoutes.Follow(string, IReadOnlyList{TripStop})"/>), each with a note on what to do there. The game plots each leg.</summary>
    public const string Trip = "trip";

    public string Kind { get; init; } = Neutron;
    public string From { get; init; } = "";
    public string To { get; init; } = "";

    /// <summary>Trip only: what the trip is for, in words: "Shopping for Explorer".</summary>
    public string Name { get; init; } = "";

    /// <summary>Neutron only: light years per jump.</summary>
    public double Range { get; init; }

    /// <summary>Neutron only, 1 to 100: higher stays nearer the straight line, lower allows longer detours to reach neutron stars.</summary>
    public int Efficiency { get; init; }

    /// <summary>Neutron only: how many times a neutron star stretches one jump. 4, or 6 with the Mk II overcharge drive.</summary>
    public int Supercharge { get; init; }

    /// <summary>Exact only: the ship the jumps are worked out for.</summary>
    public ShipNumbers? Ship { get; init; }

    /// <summary>Exact only: tons of cargo on board.</summary>
    public int Cargo { get; init; }
}

/// <summary>
/// What Spansh's exact plotter wants to know about a ship, under the names Spansh gives them.
/// Masses and fuel are in tons, <paramref name="RangeBoost"/> (a Guardian booster) in light years.
/// </summary>
/// <param name="BaseMass">The ship without fuel and cargo, plus its reserve tank: Spansh's own sum.</param>
/// <param name="InternalTankSize">The reserve tank.</param>
/// <param name="IsSupercharged">
/// The drive holds a neutron star's supercharge as the route starts (<see cref="GameState.Supercharged"/>),
/// so Spansh may plan the first jump at full stretch. True only of a route that starts where the
/// ship is, which <see cref="NavRoutes.Plot"/> sees to.
/// </param>
public sealed record ShipNumbers(
    double FuelPower, double FuelMultiplier, double OptimalMass, double MaxFuelPerJump,
    double BaseMass, double TankSize, double InternalTankSize, double RangeBoost, int SuperchargeMultiplier, bool IsSupercharged)
{
    /// <summary>
    /// The numbers for the ship the player flies now, or null with <paramref name="missing"/> saying
    /// in plain words what the game has not told Studio. The drive as built comes from
    /// <see cref="ShipMath.Fsd"/>; engineered drive values replace the table's, the way Spansh
    /// itself reads a ship build.
    /// </summary>
    public static ShipNumbers? Of(GameState game, out string missing)
    {
        missing = "";
        if (game.Loadout is not { } ship)
            missing = "your ship's details: the game writes them when you board your ship, so start Elite first";
        else if (ship.Modules.FirstOrDefault(m => m.Slot.Equals("FrameShiftDrive", StringComparison.OrdinalIgnoreCase)) is not { } drive)
            missing = "your frame shift drive: the game listed none for this ship";
        else if (ShipMath.Fsd(drive.Item) is not { } built)
            missing = $"the numbers for your frame shift drive ({drive.Item}), which is new to Studio";
        else if (game.UnladenMass <= 0 || game.FuelCapacity <= 0)
            missing = "your ship's weight and fuel tank size: the game left them out";
        else
        {
            double Engineered(string label, double asBuilt) =>
                drive.Engineering?.Modifiers.FirstOrDefault(m => m.Label.Equals(label, StringComparison.OrdinalIgnoreCase)) is { Value: > 0 } changed
                    ? changed.Value
                    : asBuilt;
            return new ShipNumbers(
                built.FuelPower, built.FuelMultiplier, Engineered("FSDOptimalMass", built.OptimalMass), Engineered("MaxFuelPerJump", built.MaxFuelPerJump),
                game.UnladenMass + ship.FuelReserve, game.FuelCapacity, ship.FuelReserve, game.BoosterLy, built.SuperchargeMultiplier,
                game.Supercharged);
        }
        return null;
    }
}

/// <summary>One stop of a route. The first stop is the system the route starts in.</summary>
/// <param name="Address">
/// The game's own number for the system (Spansh's id64); 0 when Spansh gave none. Written as text,
/// because these numbers can be larger than a web page can hold exactly.
/// </param>
/// <param name="X">Where the system is, in light years (with <paramref name="Y"/> and <paramref name="Z"/>).</param>
/// <param name="Distance">Light years from the stop before; 0 for the first.</param>
/// <param name="DistanceLeft">Light years from here to the destination in a straight line.</param>
/// <param name="Jumps">Jumps from the stop before; 0 for the first. Spansh's estimate on a neutron route, always 1 on an exact one.</param>
/// <param name="Neutron">A neutron star to supercharge at.</param>
/// <param name="Refuel">Exact routes: scoop fuel here.</param>
/// <param name="Fuel">Exact routes: tons in the tank at this stop, after scooping when <paramref name="Refuel"/> is set.</param>
/// <param name="Note">Trips: what to do at this stop, in words ("Dock at Jameson Memorial and buy..."); "" for none.</param>
public sealed record RouteHop(
    string System,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Address,
    double X, double Y, double Z, double Distance, double DistanceLeft, int Jumps, bool Neutron, bool Refuel, double Fuel, string Note = "");

/// <summary>A route as Spansh worked it out, or a trip as another studio handed it over.</summary>
/// <param name="Job">Spansh's id for the route; "" for a trip.</param>
public sealed record NavRoute(PlotRequest Request, string Job, DateTime PlottedUtc, IReadOnlyList<RouteHop> Hops);

/// <summary>A system Spansh knows near a point in space.</summary>
/// <param name="X">Where the system is, in light years (with <paramref name="Y"/> and <paramref name="Z"/>).</param>
public sealed record NearSystem(string Name, double X, double Y, double Z);

/// <summary>A system Spansh suggests for the letters typed in a name box, with where it is (<see cref="Spansh.Locate"/>).</summary>
/// <param name="Id64">
/// The game's own number for the system; 0 when Spansh gave none. Written as text, because these
/// numbers can be larger than a web page can hold exactly.
/// </param>
/// <param name="X">Where the system is, in light years (with <paramref name="Y"/> and <paramref name="Z"/>).</param>
public sealed record LocatedSystem(
    string Name,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Id64,
    double X, double Y, double Z);

/// <summary>A system Spansh has under one of the exact names it was asked about (<see cref="Spansh.SystemsNamed"/>).</summary>
/// <param name="Name">The name as Spansh spells it.</param>
/// <param name="Id64">
/// The game's own number for the system; 0 when Spansh gave none. Written as text, because these
/// numbers can be larger than a web page can hold exactly.
/// </param>
/// <param name="X">
/// Where the system is, in light years (with <paramref name="Y"/> and <paramref name="Z"/>); null when
/// Spansh gave no place. Such a row still says Spansh has the system, so it is not left out.
/// </param>
public sealed record NamedSystem(
    string Name,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Id64,
    double? X, double? Y, double? Z);

/// <summary>How many systems Spansh knows in a box of space (<see cref="Spansh.CountBox"/>).</summary>
/// <param name="Capped">
/// Spansh counts no further than <see cref="Spansh.CountCap"/>, so <paramref name="Count"/> is a
/// floor: the box holds that many systems or more.
/// </param>
public sealed record BoxCount(int Count, bool Capped)
{
    /// <summary>
    /// The pages <see cref="Spansh.ListBoxPage"/> takes to list them all: 0 for an empty box, and
    /// all <see cref="Spansh.MostPages"/> for a capped one, which still leaves systems unlisted.
    /// </summary>
    public int Pages => Math.Min(Spansh.MostPages, (Count + Spansh.PageSize - 1) / Spansh.PageSize);
}

/// <summary>
/// A system from a box listing (<see cref="Spansh.ListBoxPage"/>), with only the little the 3D map
/// uses of all Spansh sends about it.
/// </summary>
/// <param name="Id64">
/// The game's own number for the system. Written as text, because these numbers can be larger than
/// a web page can hold exactly.
/// </param>
/// <param name="X">Where the system is, in light years (with <paramref name="Y"/> and <paramref name="Z"/>).</param>
/// <param name="BodyCount">
/// How many bodies the system has, as the game tells whoever scans it from afar on arrival (the
/// discovery scan); null when nobody has passed that scan on to the databases.
/// </param>
/// <param name="Bodies">How many of its bodies the databases hold details of.</param>
/// <param name="Updated">When Spansh last heard about the system; null when it did not say.</param>
public sealed record BoxSystem(
    string Name,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Id64,
    double X, double Y, double Z, int? BodyCount, int Bodies, DateTime? Updated);

/// <summary>One page of the systems Spansh knows in a box (<see cref="Spansh.ListBoxPage"/>).</summary>
/// <param name="Page">Which page, counted from 0.</param>
/// <param name="Count">How many systems Spansh said the box holds when it sent this page.</param>
/// <param name="Systems">
/// The page's systems in Spansh's order. That order is loose (it sorts by number, but not
/// strictly), so a system can turn up on two pages: whoever puts pages together drops repeats by
/// <see cref="BoxSystem.Id64"/>, and picks up again by page number, never after the last number seen.
/// </param>
public sealed record BoxPage(int Page, BoxCount Count, IReadOnlyList<BoxSystem> Systems);

/// <summary>Spansh said no, went quiet or could not be reached. The message is written for the player.</summary>
/// <param name="temporary">
/// A hiccup on the way (no connection, no answer in time, an error page instead of an answer),
/// not Spansh's word on the request: the same call may well work a moment later.
/// </param>
public sealed class SpanshException(string message, bool temporary = false) : Exception(message)
{
    public bool Temporary { get; } = temporary;
}

/// <summary>
/// Asks spansh.co.uk for routes, system names, the system nearest to a point in space, and for the
/// 3D map how many systems it knows in a box of space and which they are. Spansh is one person's
/// free service, so this is a careful guest: it says who it is, looks for a result at the
/// unhurried pace other tools use, never asks twice for what it already has, and never sends a
/// request a second time by itself.
/// The box calls are kept one at a time and within the 3D map's budget by the map's own gate, not
/// here, so that route plotting and name suggestions never wait behind a long listing.
/// Everything here is awaited, never blocked on; in the app it runs on the UI thread, apart from
/// the sending of each call and the reading of its answer (see <see cref="Send{T}"/>).
/// </summary>
/// <param name="handler">Tests pass a stand-in for the network; null means the real internet.</param>
public sealed class Spansh(HttpMessageHandler? handler = null) : IDisposable
{
    const string Api = "https://spansh.co.uk/api/";

    // Seconds to wait before each look at a job's result; every look after these waits 10. The pace
    // of Auto_Neutron, a long-standing Spansh client: quick while an answer may come in seconds,
    // light once it is clear that it will take a while.
    static readonly int[] PollSeconds = [1, 2, 4, 4, 4, 6, 6];

    /// <summary>
    /// This many looks in a row lost on the way (see <see cref="SpanshException.Temporary"/>) end the
    /// wait for a job. Fewer do not: Spansh is still working on it, and the player's only way on
    /// would be to plot again, which starts a second job for the same route.
    /// </summary>
    const int LostLooks = 3;

    /// <summary>How long the exact plotter is told it may search (Spansh's own default). It uses all of it, even for a short trip.</summary>
    const int SearchSeconds = 60;

    /// <summary>
    /// A job still not finished after this many seconds of waiting is given up. Room for a busy
    /// day: on 9 October 2026 a short neutron route took Spansh 40 seconds, an exact one its full 60.
    /// </summary>
    const int GiveUpSeconds = 3 * SearchSeconds;

    /// <summary>The seconds Spansh has to start answering a call.</summary>
    const int AnswerSeconds = 20;

    /// <summary>
    /// The same for a page of a box listing, which takes Spansh longer to put together: 500 systems
    /// with every body of each. Market searches give Spansh's pages of 500 a minute too.
    /// </summary>
    const int PageAnswerSeconds = 60;

    /// <summary>
    /// Once Spansh starts to answer, the seconds allowed for the rest of the answer to arrive. Room
    /// for the largest, a page of a box listing: about 240 KB on the wire, over 2 MB unpacked.
    /// </summary>
    const int ReadSeconds = 60;

    /// <summary>Spansh counts the systems in a box no further than this: a count of 10,000 means "10,000 or more".</summary>
    public const int CountCap = 10_000;

    /// <summary>
    /// Systems asked for on one page of a box listing, and never more: asked for 501, Spansh
    /// silently sends 25 (seen live in October 2026), which is also why the rows of every page are counted.
    /// </summary>
    public const int PageSize = 500;

    /// <summary>Pages a box listing can have, numbered 0 to 19: Spansh fails a page that reaches past its 10,000th system.</summary>
    public const int MostPages = CountCap / PageSize;

    /// <summary>Exact names asked about in one request at most (<see cref="SystemsNamed"/>): a whole plotted route is asked in batches of this many.</summary>
    public const int MostNames = 100;

    readonly HttpClient http = Client(handler);
    readonly Dictionary<PlotRequest, NavRoute> routes = [];
    readonly Dictionary<string, Suggestions> names = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(long X, long Y, long Z), NearSystem?> near = [];

    /// <summary>One answer to a name search: the names for a name box, and where those systems are.</summary>
    sealed record Suggestions(IReadOnlyList<string> Names, IReadOnlyList<LocatedSystem> Located);

    static readonly Suggestions NoSuggestions = new([], []);

    /// <summary>How far from a point <see cref="Nearest"/> looks, in light years: across most of one of the game's sectors.</summary>
    const int NearLy = 1000;

    /// <summary>The wait between two looks at a job. Tests replace it so they need not really wait.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = Task.Delay;

    public void Dispose() => http.Dispose();

    /// <summary>
    /// System names that match the text, best match first, for a name box. Fewer than three
    /// letters give nothing without asking, and every answer is kept for the session.
    /// </summary>
    public async Task<IReadOnlyList<string>> SystemNames(string text) => (await Suggest(text)).Names;

    /// <summary>
    /// The systems <see cref="SystemNames"/> suggests for the same text, each with its number and
    /// where it is, for the 3D map's star search. It is the same request and the same kept answer:
    /// Spansh sends the positions with the names, so asking for both costs one call.
    /// </summary>
    /// <param name="fresh">
    /// Ask Spansh even when an answer for the text is kept (the new one is kept in its place): for the 3D map's check
    /// of a system, which its memory of checks asks again only when the answer it had is out of date, or the player
    /// had it forgotten. A system not recorded an hour ago may be recorded now; the kept answer would still say not.
    /// </param>
    public async Task<IReadOnlyList<LocatedSystem>> Locate(string text, bool fresh = false) => (await Suggest(text, fresh)).Located;

    async Task<Suggestions> Suggest(string text, bool fresh = false)
    {
        text = text.Trim();
        if (text.Length < 3) return NoSuggestions;
        if (!fresh && names.TryGetValue(text, out Suggestions? known)) return known;
        JsonElement answer = await Send(HttpMethod.Get, "systems/field_values/system_names?q=" + Uri.EscapeDataString(text), null, CancellationToken.None);
        return names[text] = new Suggestions(
            [.. answer.List("values").Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)],
            // "min_max" holds one entry per name, with its number and position. One without a
            // position is left out rather than placed at 0, 0, 0, which is Sol.
            [
                .. answer.List("min_max")
                    .Where(found => found.Text("name").Length > 0 && Placed(found))
                    .Select(found => new LocatedSystem(found.Text("name"), Address(found, "id64"), Number(found, "x"), Number(found, "y"), Number(found, "z"))),
            ]);
    }

    /// <summary>
    /// The system Spansh knows nearest to a point in space (taken to the whole light year), or null
    /// when it knows none within <see cref="NearLy"/> light years. This is Spansh's own system
    /// search, sorted by distance. Every answer is kept for the session.
    /// </summary>
    public async Task<NearSystem?> Nearest(double x, double y, double z)
    {
        var point = (X: (long)Math.Round(x), Y: (long)Math.Round(y), Z: (long)Math.Round(z));
        if (near.TryGetValue(point, out NearSystem? known)) return known;
        // As Spansh's own site sends it: the distance limits are text.
        string search = JsonSerializer.Serialize(new
        {
            filters = new { distance = new { min = "0", max = NearLy.ToString(CultureInfo.InvariantCulture) } },
            sort = new[] { new { distance = new { direction = "asc" } } },
            size = 1,
            page = 0,
            reference_coords = new { x = point.X, y = point.Y, z = point.Z },
        });
        JsonElement answer = await Send(HttpMethod.Post, "systems/search", new StringContent(search, Encoding.UTF8, "application/json"), CancellationToken.None);
        return near[point] = answer.List("results").Select(found => new NearSystem(found.Text("name"), Number(found, "x"), Number(found, "y"), Number(found, "z")))
            .FirstOrDefault(found => found.Name.Length > 0);
    }

    /// <summary>
    /// How many systems Spansh knows in a box of space: one call, asking for a single system and
    /// reading the total that comes with it. Exact below <see cref="CountCap"/>; at the cap it is
    /// marked <see cref="BoxCount.Capped"/>. Nothing is kept here: the 3D map keeps what it asked.
    /// </summary>
    /// <exception cref="SpanshException">No count: the message says why in the player's words.</exception>
    public async Task<BoxCount> CountBox(Box box, CancellationToken cancel = default)
    {
        BoxAnswer answer = await Send(HttpMethod.Post, "systems/search", BoxSearch(box, size: 1, page: 0), ReadBox, cancel);
        return Total(answer);
    }

    /// <summary>
    /// One page of the systems Spansh knows in a box, <see cref="PageSize"/> at a time. Page
    /// <paramref name="page"/> holds systems page × 500 onwards, up to 500 of them; a box holding
    /// more than 10,000 cannot be listed whole. Spansh's answer runs to over 2 MB, of which only a
    /// few fields a system are read, as it arrives and away from the calling thread.
    /// A page short of the systems it should hold (Spansh said how many the box has) is refused
    /// rather than taken for the whole page. Repeats across pages are left for the caller to drop.
    /// </summary>
    /// <param name="page">0 to <see cref="MostPages"/> − 1.</param>
    /// <exception cref="SpanshException">No page: the message says why in the player's words.</exception>
    public async Task<BoxPage> ListBoxPage(Box box, int page, CancellationToken cancel = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(page, MostPages);
        BoxAnswer answer = await Send(HttpMethod.Post, "systems/search", BoxSearch(box, PageSize, page), ReadBox, cancel, PageAnswerSeconds);
        BoxCount count = Total(answer);
        List<BoxRow?> rows = answer.Results ?? throw new SpanshException(Unreadable, temporary: true);

        int due = Math.Clamp(count.Count - page * PageSize, 0, PageSize);
        if (rows.Count != due)
            throw new SpanshException(
                $"Spansh sent {rows.Count} systems where it should have sent {due}. Try again in a moment.", temporary: true);

        List<BoxSystem> systems = [];
        foreach (BoxRow? row in rows)
        {
            // A row without a name, number or position cannot be placed or told apart, so it is
            // left out; it still counts as sent above. (None has been seen.)
            if (row is { Name: { Length: > 0 } name, Id64: > 0 and long id64, X: { } x, Y: { } y, Z: { } z })
                systems.Add(new BoxSystem(name, id64, x, y, z, row.BodyCount, row.Bodies, When(row.UpdatedAt)));
        }
        if (systems.Count < rows.Count)
            Log.Info($"Spansh box listing, page {page}: {rows.Count - systems.Count} of {rows.Count} rows had no name, number or position and were left out");
        return new BoxPage(page, count, systems);
    }

    static BoxCount Total(BoxAnswer answer) =>
        answer.Count is >= 0 and int count ? new BoxCount(count, count >= CountCap) : throw new SpanshException(Unreadable, temporary: true);

    /// <summary>
    /// The systems Spansh has under these exact names: its own system search with the list of names as the name
    /// filter, one request for all of them (at most <see cref="MostNames"/>), asking for as many systems as names.
    /// For the 3D map's check of a plotted route, whose stops the game names. Spansh's answer brings every body of
    /// each system, of which nothing is kept: it is read as it arrives and away from the calling thread, as a page
    /// of a box listing is. Nothing is kept here either: the 3D map keeps what it asked.
    /// </summary>
    /// <returns>
    /// The systems, and whether that is all Spansh found: it found more than it sent when two systems share a name,
    /// and a name missing from the answer may then be among those left out.
    /// </returns>
    /// <exception cref="SpanshException">No answer: the message says why in the player's words.</exception>
    public async Task<(IReadOnlyList<NamedSystem> Systems, bool Complete)> SystemsNamed(IReadOnlyList<string> names, CancellationToken cancel = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(names.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(names.Count, MostNames);
        BoxAnswer answer = await Send(HttpMethod.Post, "systems/search", NameSearch(names), ReadBox, cancel, PageAnswerSeconds);
        BoxCount count = Total(answer);
        List<BoxRow?> rows = answer.Results ?? throw new SpanshException(Unreadable, temporary: true);
        // A row without a name cannot be matched to a stop; one without a place still says Spansh has the system.
        List<NamedSystem> systems = [.. rows.OfType<BoxRow>().Where(row => row.Name is { Length: > 0 })
            .Select(row => new NamedSystem(row.Name!, row.Id64 ?? 0, row.X, row.Y, row.Z))];
        return (systems, count.Count <= rows.Count);
    }

    /// <summary>
    /// The systems Spansh knows within <paramref name="radius"/> light years of a point, nearest first: its own system
    /// search with a distance filter round the point (the search <see cref="Nearest"/> sends, and Studio Market's
    /// Powerplay search), one page of <see cref="PageSize"/> at most. For the 3D map's "Within reach", which lists the
    /// systems one jump away. Spansh's answer brings every body of each system, of which nothing is kept: it is read
    /// as it arrives and away from the calling thread, as a page of a box listing is. Nothing is kept here either.
    /// </summary>
    /// <returns>
    /// The systems (those Spansh gave no place for left out: they cannot be placed in the sphere), and how many Spansh
    /// knows there: more than it sent when the answer was cut at <see cref="PageSize"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">A radius that is not more than 0.</exception>
    /// <exception cref="SpanshException">No answer: the message says why in the player's words.</exception>
    public async Task<(IReadOnlyList<NamedSystem> Systems, int Count)> SystemsWithin(double x, double y, double z, double radius, CancellationToken cancel = default)
    {
        if (!(radius > 0) || !double.IsFinite(radius)) throw new ArgumentOutOfRangeException(nameof(radius), radius, "A distance of more than 0 light years.");
        // As Spansh's own site sends it: the distance limits are text.
        string search = JsonSerializer.Serialize(new
        {
            filters = new { distance = new { min = "0", max = radius.ToString("0.##", CultureInfo.InvariantCulture) } },
            sort = new[] { new { distance = new { direction = "asc" } } },
            size = PageSize,
            page = 0,
            reference_coords = new { x, y, z },
        }, AsWritten);
        BoxAnswer answer = await Send(HttpMethod.Post, "systems/search", new StringContent(search, Encoding.UTF8, "application/json"), ReadBox, cancel, PageAnswerSeconds);
        BoxCount count = Total(answer);
        List<BoxRow?> rows = answer.Results ?? throw new SpanshException(Unreadable, temporary: true);
        List<NamedSystem> systems = [.. rows.OfType<BoxRow>().Where(row => row is { Name.Length: > 0, X: not null, Y: not null, Z: not null })
            .Select(row => new NamedSystem(row.Name!, row.Id64 ?? 0, row.X, row.Y, row.Z))];
        return (systems, Math.Max(count.Count, rows.Count));
    }

    /// <summary>
    /// A search for systems by their exact names: Spansh's name filter given the names as a list, as many systems
    /// asked for as names, the first page. The names go as written (see <see cref="AsWritten"/>): "Barnard's Star",
    /// not "Barnard's Star".
    /// </summary>
    static StringContent NameSearch(IReadOnlyList<string> names)
    {
        string search = JsonSerializer.Serialize(new
        {
            filters = new { name = new { value = names } },
            size = names.Count,
            page = 0,
        }, AsWritten);
        return new StringContent(search, Encoding.UTF8, "application/json");
    }

    // "<=>" written as it is, the way Spansh's own site sends it: by default the < and > would go as
    // escape codes, which mean the same to Spansh but make the request text differ from its own.
    static readonly JsonSerializerOptions AsWritten = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// A search for the systems in a box, sorted by number (the order Spansh pages in), as Spansh's
    /// own site sends it.
    /// </summary>
    static StringContent BoxSearch(Box box, int size, int page)
    {
        if (!(box.X1 > box.X0 && box.Y1 > box.Y0 && box.Z1 > box.Z0))
            throw new ArgumentException("A box needs some width, height and depth.", nameof(box));

        // "<=>" takes in both ends, but a box owns only its near faces: a star on a far face
        // belongs to the next box. Every position is a whole number of 32nds of a light year, so
        // the far end sent is a 64th short of the first position on or past the face. For the
        // game's grid, whose faces lie on whole light years, that is the face less 1/64 ly. (Seen
        // live in October 2026: Sphuedu's sector holds 2,522 systems with its far faces, 2,521 without.)
        static double[] Span(double from, double to) => [from, Math.Ceiling(to * 32) / 32 - 1.0 / 64];

        string search = JsonSerializer.Serialize(new
        {
            filters = new
            {
                x = new { comparison = "<=>", value = Span(box.X0, box.X1) },
                y = new { comparison = "<=>", value = Span(box.Y0, box.Y1) },
                z = new { comparison = "<=>", value = Span(box.Z0, box.Z1) },
            },
            sort = new[] { new { id64 = new { direction = "asc" } } },
            size,
            page,
        }, AsWritten);
        return new StringContent(search, Encoding.UTF8, "application/json");
    }

    /// <summary>
    /// Hands the request to Spansh as a job and waits for its result: anything from a few seconds
    /// to most of a minute for the neutron plotter, a full minute for the exact plotter. A route
    /// already worked out this session comes back at once.
    /// </summary>
    /// <param name="progress">Told Spansh's word for the job ("queued", "started") after each look that found it unfinished.</param>
    /// <exception cref="SpanshException">No route: the message says why in the player's words.</exception>
    /// <exception cref="OperationCanceledException">Cancelled through <paramref name="cancel"/>.</exception>
    public async Task<NavRoute> Plot(PlotRequest request, Action<string>? progress = null, CancellationToken cancel = default)
    {
        if (routes.TryGetValue(request, out NavRoute? known)) return known;

        bool exact = request.Kind == PlotRequest.Exact;
        string job = (await Send(HttpMethod.Post, exact ? "generic/route" : "route", new FormUrlEncodedContent(Form(request)), cancel)).Text("job");
        if (job.Length == 0) throw new SpanshException(Unreadable);
        Log.Info($"Spansh {request.Kind} route from {request.From} to {request.To}: job {job}");

        int waited = 0, lost = 0;
        for (int look = 0; ; look++)
        {
            int seconds = look < PollSeconds.Length ? PollSeconds[look] : 10;
            if ((waited += seconds) > GiveUpSeconds)
                throw new SpanshException($"Spansh is still working on this route after {GiveUpSeconds / 60} minutes. It may be busy: try again later, or try a shorter route.");
            await Wait(TimeSpan.FromSeconds(seconds), cancel);

            JsonElement answer;
            try
            {
                answer = await Send(HttpMethod.Get, "results/" + Uri.EscapeDataString(job), null, cancel);
                lost = 0;
            }
            catch (SpanshException hiccup) when (hiccup.Temporary && ++lost < LostLooks)
            {
                Log.Info($"Spansh job {job}: a look at it failed ({hiccup.Message}); looking again at the usual pace");
                continue;
            }
            if (answer.Field("result") is { ValueKind: JsonValueKind.Object } result)
            {
                IReadOnlyList<RouteHop> hops = exact ? ExactHops(result) : NeutronHops(result);
                if (hops.Count == 0) throw new SpanshException("Spansh found no route between these two systems.");
                Log.Info($"Spansh job {job}: a route of {hops.Count} stops");
                return routes[request] = new NavRoute(request, job, DateTime.UtcNow, hops);
            }
            // Spansh says "queued" for as long as the job is unfinished, whether it waits its turn or is being worked on.
            if (answer.Text("status") != "queued") throw new SpanshException(Unreadable);
            progress?.Invoke(answer.Text("state") is { Length: > 0 } state ? state : "queued");
        }
    }

    const string Unreadable = "Spansh sent an answer Studio could not read. Try again in a moment.";

    static HttpClient Client(HttpMessageHandler? handler)
    {
        var client = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            // Every call sets its own time limits (see Send); the wait for a whole route is bounded by GiveUpSeconds.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Program.UserAgent);
        return client;
    }

    /// <summary>One call. Gives Spansh's JSON answer, or throws a <see cref="SpanshException"/> the player can read.</summary>
    Task<JsonElement> Send(HttpMethod method, string path, HttpContent? content, CancellationToken cancel) =>
        Send(method, path, content, ReadWhole, cancel);

    /// <summary>
    /// One call, whose answer <paramref name="read"/> takes in as it arrives. Gives what that makes of
    /// it, or throws a <see cref="SpanshException"/> the player can read.
    /// </summary>
    /// <param name="answerSeconds">How long Spansh has to start answering; <see cref="ReadSeconds"/> more are allowed for the rest.</param>
    async Task<T> Send<T>(
        HttpMethod method, string path, HttpContent? content, Func<Stream, CancellationToken, Task<T>> read, CancellationToken cancel,
        int answerSeconds = AnswerSeconds)
    {
        try
        {
            using var message = new HttpRequestMessage(method, Api + path) { Content = content };
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TimeSpan.FromSeconds(answerSeconds));
            // Started on a background thread: before its first real wait, sending looks up Windows'
            // proxy settings, which on some networks takes seconds, and the UI thread must never
            // stand still. The answer is read there too, as it arrives: a page of a box listing is
            // over 2 MB once unpacked. What follows each await is back on the calling thread.
            using HttpResponseMessage response = await Task.Run(() => http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, limit.Token), limit.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(ReadSeconds));
            T answer = await Task.Run(async () =>
            {
                await using Stream body = await response.Content.ReadAsStreamAsync(limit.Token);
                return await read(body, limit.Token);   // which turns an "error" field into a SpanshException, whatever the status code
            }, limit.Token);
            if (!response.IsSuccessStatusCode)
                throw new SpanshException($"Spansh answered with error {(int)response.StatusCode}. Try again in a moment.", temporary: (int)response.StatusCode >= 500);
            return answer;
        }
        catch (HttpRequestException)
        {
            throw new SpanshException("Spansh could not be reached. Check your internet connection and try again.", temporary: true);
        }
        catch (IOException)
        {
            throw new SpanshException("The connection to Spansh broke off. Check your internet connection and try again.", temporary: true);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new SpanshException("Spansh did not answer in time. Try again in a moment.", temporary: true);
        }
        catch (Exception unreadable) when (unreadable is JsonException or InvalidDataException)
        {
            throw new SpanshException(Unreadable, temporary: true);   // an error page instead of JSON, most likely
        }
    }

    /// <summary>A short answer, read whole. Spansh explains every refusal in an "error" field, whatever the status code.</summary>
    static async Task<JsonElement> ReadWhole(Stream body, CancellationToken cancel)
    {
        using JsonDocument answer = await JsonDocument.ParseAsync(body, default, cancel);
        if (answer.RootElement.Text("error") is { Length: > 0 } error) throw new SpanshException(Readable(error));
        return answer.RootElement.Clone();
    }

    // Spansh is loose with types, so numbers sent as text are read too.
    static readonly JsonSerializerOptions BoxReading = new() { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    /// <summary>
    /// A box search's answer, read as it streams past: only the fields of <see cref="BoxAnswer"/> are
    /// kept, and everything else (each body's own details, values, economy and so on) is passed over.
    /// </summary>
    static async Task<BoxAnswer> ReadBox(Stream body, CancellationToken cancel)
    {
        BoxAnswer answer = await JsonSerializer.DeserializeAsync<BoxAnswer>(body, BoxReading, cancel)
            ?? throw new SpanshException(Unreadable, temporary: true);
        if (answer.Error is { Length: > 0 } error) throw new SpanshException(Readable(error));
        return answer;
    }

    /// <summary>What Studio reads of a box search's answer, under Spansh's own names.</summary>
    sealed class BoxAnswer
    {
        [JsonPropertyName("error")] public string? Error { get; init; }
        [JsonPropertyName("count")] public int? Count { get; init; }
        [JsonPropertyName("results")] public List<BoxRow?>? Results { get; init; }
    }

    /// <summary>What Studio reads of one system in a box search's answer.</summary>
    sealed class BoxRow
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("id64")] public long? Id64 { get; init; }
        [JsonPropertyName("x")] public double? X { get; init; }
        [JsonPropertyName("y")] public double? Y { get; init; }
        [JsonPropertyName("z")] public double? Z { get; init; }
        [JsonPropertyName("body_count")] public int? BodyCount { get; init; }

        // Only how many: the bodies' own details are most of a page.
        [JsonPropertyName("bodies"), JsonConverter(typeof(Tally))] public int Bodies { get; init; }

        [JsonPropertyName("updated_at")] public string? UpdatedAt { get; init; }
    }

    /// <summary>
    /// Reads a list as the number of entries in it, keeping none of them; anything else as 0.
    /// (TrySkip, not Skip: while an answer is still arriving Skip refuses to work, even though the
    /// serializer has already gathered the whole list before handing it over.)
    /// </summary>
    sealed class Tally : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                return reader.TrySkip() ? 0 : throw new JsonException();
            int entries = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                entries++;
                if (!reader.TrySkip()) throw new JsonException();   // past the whole entry
            }
            return entries;
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    // Spansh writes "2024-09-12T00:47:21Z"; anything it may write instead that does not read as a time counts as not said.
    static DateTime? When(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime at) ? at : null;

    static string Readable(string error) =>
        error.Contains("starting system", StringComparison.OrdinalIgnoreCase)
            ? "Spansh does not know the system typed under From. Check its spelling, or pick a name from the list."
        : error.Contains("finishing system", StringComparison.OrdinalIgnoreCase)
            ? "Spansh does not know the system typed under To. Check its spelling, or pick a name from the list."
        : $"Spansh said: {error}";

    /// <summary>The fields of a route request, as Spansh's own site sends them.</summary>
    static IEnumerable<KeyValuePair<string, string>> Form(PlotRequest request)
    {
        static string Text(double number) => number.ToString("0.######", CultureInfo.InvariantCulture);

        (string Name, string Value)[] fields;
        if (request.Kind == PlotRequest.Exact)
        {
            ShipNumbers ship = request.Ship ?? throw new ArgumentException("An exact route needs the ship's numbers.", nameof(request));
            fields =
            [
                ("source", request.From), ("destination", request.To), ("is_supercharged", ship.IsSupercharged ? "1" : "0"),
                // The choices Spansh's site starts with: use neutron stars, top the tank up at
                // every star that can be scooped, never use synthesis, its "optimistic" search.
                ("use_supercharge", "1"), ("use_injections", "0"), ("use_injections_when_required", "0"),
                ("exclude_secondary", "0"), ("refuel_every_scoopable", "1"),
                ("fuel_power", Text(ship.FuelPower)), ("fuel_multiplier", Text(ship.FuelMultiplier)),
                ("optimal_mass", Text(ship.OptimalMass)), ("base_mass", Text(ship.BaseMass)),
                ("tank_size", Text(ship.TankSize)), ("internal_tank_size", Text(ship.InternalTankSize)),
                ("max_fuel_per_jump", Text(ship.MaxFuelPerJump)), ("range_boost", Text(ship.RangeBoost)),
                ("reserve_size", "0"), ("cargo", Text(request.Cargo)), ("supercharge_multiplier", Text(ship.SuperchargeMultiplier)),
                ("algorithm", "optimistic"), ("max_time", Text(SearchSeconds)),
            ];
        }
        else
        {
            fields =
            [
                ("from", request.From), ("to", request.To), ("range", Text(request.Range)),
                ("efficiency", Text(request.Efficiency)), ("supercharge_multiplier", Text(request.Supercharge)),
            ];
        }
        return fields.Select(field => KeyValuePair.Create(field.Name, field.Value));
    }

    static IReadOnlyList<RouteHop> NeutronHops(JsonElement result) =>
    [
        .. result.List("system_jumps").Select(stop => new RouteHop(
            stop.Text("system"), stop.Whole("id64"), Number(stop, "x"), Number(stop, "y"), Number(stop, "z"),
            Number(stop, "distance_jumped"), Number(stop, "distance_left"), (int)Number(stop, "jumps"),
            Yes(stop, "neutron_star"), Refuel: false, Fuel: 0)),
    ];

    // Every row of an exact route is one real jump; the first row is where the route starts.
    static IReadOnlyList<RouteHop> ExactHops(JsonElement result) =>
    [
        .. result.List("jumps").Select((stop, index) => new RouteHop(
            stop.Text("name"), stop.Whole("id64"), Number(stop, "x"), Number(stop, "y"), Number(stop, "z"),
            Number(stop, "distance"), Number(stop, "distance_to_destination"), index == 0 ? 0 : 1,
            Yes(stop, "has_neutron"), Yes(stop, "must_refuel"), Number(stop, "fuel_in_tank"))),
    ];

    // Spansh is loose with types: a number may arrive as text ("range":"50") and a yes or no as true, false, 1 or 0.
    // (A stop's id64 is not read through here: it does not fit a double. Sent as text it would
    // read as 0, "none given", and the stop would be matched by its name.)
    static double Number(JsonElement e, string name) => e.Field(name) switch
    {
        { ValueKind: JsonValueKind.Number } number => number.GetDouble(),
        { ValueKind: JsonValueKind.String } text
            when double.TryParse(text.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
        _ => 0.0,
    };

    static bool Yes(JsonElement e, string name) => e.Flag(name) || Number(e, name) != 0;

    // A system's number to the last digit, whether it comes as a number or as text (never through a
    // double, which cannot hold it); 0 when there is none.
    static long Address(JsonElement e, string name) => e.Field(name) switch
    {
        { ValueKind: JsonValueKind.Number } number when number.TryGetInt64(out long whole) => whole,
        { ValueKind: JsonValueKind.String } text
            when long.TryParse(text.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out long whole) => whole,
        _ => 0,
    };

    // The entry gives all three of x, y and z, as numbers or as text.
    static bool Placed(JsonElement e) =>
        new[] { "x", "y", "z" }.All(axis => e.Field(axis)?.ValueKind is JsonValueKind.Number or JsonValueKind.String);
}
