using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
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
/// Asks spansh.co.uk for routes, system names and the system nearest to a point in space. Spansh
/// is one person's free service, so this is a careful guest: it says who it is, looks for a
/// result at the unhurried pace other tools use, never asks twice for what it already has, and
/// never sends a request a second time by itself.
/// Everything here is awaited, never blocked on; in the app it runs on the UI thread, apart from
/// the sending of each call (see <see cref="Send"/>).
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

    readonly HttpClient http = Client(handler);
    readonly Dictionary<PlotRequest, NavRoute> routes = [];
    readonly Dictionary<string, IReadOnlyList<string>> names = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(long X, long Y, long Z), NearSystem?> near = [];

    /// <summary>How far from a point <see cref="Nearest"/> looks, in light years: across most of one of the game's sectors.</summary>
    const int NearLy = 1000;

    /// <summary>The wait between two looks at a job. Tests replace it so they need not really wait.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = Task.Delay;

    public void Dispose() => http.Dispose();

    /// <summary>
    /// System names that match the text, best match first, for a name box. Fewer than three
    /// letters give nothing without asking, and every answer is kept for the session.
    /// </summary>
    public async Task<IReadOnlyList<string>> SystemNames(string text)
    {
        text = text.Trim();
        if (text.Length < 3) return [];
        if (names.TryGetValue(text, out IReadOnlyList<string>? known)) return known;
        JsonElement answer = await Send(HttpMethod.Get, "systems/field_values/system_names?q=" + Uri.EscapeDataString(text), null, CancellationToken.None);
        return names[text] = [.. answer.List("values").Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)];
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
            Timeout = TimeSpan.FromSeconds(20),   // for one call; the wait for a whole route is bounded by GiveUpSeconds
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Program.UserAgent);
        return client;
    }

    /// <summary>One call. Gives Spansh's JSON answer, or throws a <see cref="SpanshException"/> the player can read.</summary>
    async Task<JsonElement> Send(HttpMethod method, string path, HttpContent? content, CancellationToken cancel)
    {
        try
        {
            using var message = new HttpRequestMessage(method, Api + path) { Content = content };
            // Started on a background thread: before its first real wait, sending looks up Windows'
            // proxy settings, which on some networks takes seconds, and the UI thread must never
            // stand still. What follows the await is back on the calling thread.
            using HttpResponseMessage response = await Task.Run(() => http.SendAsync(message, cancel), cancel);
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            // Spansh explains every refusal in an "error" field, whatever the status code.
            if (body.RootElement.Text("error") is { Length: > 0 } error) throw new SpanshException(Readable(error));
            if (!response.IsSuccessStatusCode)
                throw new SpanshException($"Spansh answered with error {(int)response.StatusCode}. Try again in a moment.", temporary: (int)response.StatusCode >= 500);
            return body.RootElement.Clone();
        }
        catch (HttpRequestException)
        {
            throw new SpanshException("Spansh could not be reached. Check your internet connection and try again.", temporary: true);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new SpanshException("Spansh did not answer in time. Try again in a moment.", temporary: true);
        }
        catch (JsonException)
        {
            throw new SpanshException(Unreadable, temporary: true);   // an error page instead of JSON, most likely
        }
    }

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
}
