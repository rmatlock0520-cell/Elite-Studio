using System.Net;
using System.Net.Http;
using System.Text.Json;
using EliteStudio.App;
using EliteStudio.Core;
using static System.FormattableString;
using WebResponse = EliteStudio.App.WebResponse;   // System.Net has an older type with the same name

namespace EliteStudio.Market;

/// <summary>One place a "Find by name" search found.</summary>
/// <param name="DistanceLy">Light years from the system the search started in.</param>
/// <param name="StarLs">Light seconds from the arrival star; null when not known.</param>
/// <param name="Pad">The largest landing pad: "S", "M" or "L"; "" when not known.</param>
/// <param name="Type">The kind of station, in words.</param>
/// <param name="Item">Modules: the sizes and grades on sale there ("5A"); "" for everything else.</param>
/// <param name="Price">Credits, per ton for a commodity. Null when the service does not say, or when what matched is sold there at several prices.</param>
/// <param name="Tons">Commodities: tons on sale, or tons wanted; null for everything else.</param>
/// <param name="Updated">When this was last reported (UTC): the price, the outfitting or shipyard list, or for a service the station itself. Null when not known.</param>
public sealed record FoundPlace(
    string Station, string System, double DistanceLy, double? StarLs, string Pad, string Type, string Item, long? Price, int? Tons, DateTime? Updated);

/// <summary>What one "Find by name" search found.</summary>
/// <param name="What">The search in words, as a heading: "Where to buy Gold".</param>
/// <param name="From">The system the distances are measured from.</param>
/// <param name="Rows">Nearest first.</param>
/// <param name="Source">The service that answered: "Ardent Insight" or "Spansh".</param>
/// <param name="Link">The same search on the service's own site, where it keeps one to show; else null.</param>
/// <param name="Note">A line to go with the rows (how many more there are, why there are none), or null.</param>
/// <param name="More">What the same search sends as "more" to be given the places after these; null when the service has no more.</param>
/// <param name="AskedUtc">When the service was asked. That may be a few minutes ago: the same question is not sent twice in a row.</param>
public sealed record FindAnswer(string What, string From, IReadOnlyList<FoundPlace> Rows, string Source, string? Link, string? Note, int? More, DateTime AskedUtc);

/// <summary>Every name a search of one kind takes, and for modules the classes and ratings there are.</summary>
public sealed record NameList(IReadOnlyList<string> Names, IReadOnlyList<string> Classes, IReadOnlyList<string> Ratings);

/// <summary>
/// "Find by name": the nearest places that have a commodity, a ship module or a ship, and the
/// nearest of each kind of service. There is no galaxy database on this PC yet, so the community's
/// services are asked directly. Routes, all under /api/market/find/:
///   GET  names?kind=    every name a search of that kind takes: commodity | module | ship | services
///   POST commodity      {"name":"Gold","side":"buy"} or "sell": the nearest places to buy it, or to sell it
///   POST module         {"name":"Frame Shift Drive","class":"5","rating":"A"}; class and rating may be left out
///   POST ship           {"name":"Python MkII"}
///   POST services       {"name":"Raw material trader"}
/// Every search may add "from" (a system other than the one the player is in) and "filters" (pad,
/// maxStarLs, surface, carriers, and for commodities maxAgeHours, minSupply and minDemand, named
/// and valued as in settings.json), which replace the player's own for that one search and are not
/// saved. (The player's own the page reads from the Market picture, GET /api/market/hauling.)
/// Each answers a <see cref="FindAnswer"/>, or the error shape with words for the player. An answer
/// that is not the whole list says so with "more": the same search sent again with that number is
/// answered with the places that come next, and with nothing that was listed before.
///
/// Commodities come from Ardent Insight, which knows prices but not what outfitting and shipyards
/// stock. Everything else comes from Spansh: its stations search tells the three kinds of material
/// trader and the two kinds of technology broker apart, and it takes most of the station filters
/// itself. (Ardent's own "nearest service" gives the 20 nearest stations of any type, which around
/// a busy system are nearly all fleet carriers and settlements.)
///
/// One search has no route of its own: Studio Loadouts' shopping list asks through
/// <see cref="Finder.Sellers"/> for the stations that sell several modules, and a ship, at once.
/// </summary>
public static class FindApi
{
    /// <summary>Registers the routes. Call on the UI thread, which is also where the server runs them.</summary>
    /// <param name="data">The app's one client for the data services, which the trade search asks through as well.</param>
    /// <param name="utcNow">The clock, in UTC; tests pass their own.</param>
    /// <returns>The searches themselves, for whoever else needs a station found (Studio Loadouts, for a shopping trip).</returns>
    internal static Finder Map(WebServer web, GameState game, Hauling hauling, MarketData data, Func<DateTime>? utcNow = null)
    {
        // One for as long as the app runs, so what it remembers lasts the session.
        var finder = new Finder(game, hauling, data, utcNow ?? (() => DateTime.UtcNow));
        web.Map("GET", "/api/market/find/names", request => Answered(() => finder.Names(request.Query.GetValueOrDefault("kind") ?? "")));
        foreach (string kind in Finder.Kinds)
            web.Map("POST", "/api/market/find/" + kind, request => Answered(() => finder.Find(kind, request.Read<FindRequest>())));
        return finder;
    }

    /// <summary>A search's answer for a page, or the error shape when it could not be answered.</summary>
    internal static async Task<WebResponse> Answered<T>(Func<Task<T>> search) where T : notnull
    {
        try { return WebResponse.Json(await search()); }
        catch (FindProblem problem) { return WebResponse.Error(problem.Message, problem.Status); }
    }
}

/// <summary>What the page sends to search. Everything but the name may be left out.</summary>
sealed class FindRequest
{
    public string Name { get; set; } = "";
    public string Side { get; set; } = "buy";       // commodities: buy | sell
    public string Class { get; set; } = "";         // modules: "" = any
    public string Rating { get; set; } = "";
    public string From { get; set; } = "";          // "" = the system the player is in
    public int More { get; set; }                   // 0 = from the nearest on; else the "more" of the answer before: the places after it
    public Dictionary<string, JsonElement>? Filters { get; set; }   // station filters for this one search
}

/// <summary>A search that cannot be answered, in words for the player. 400: the request cannot be met as it is; 502: a service gave no answer.</summary>
sealed class FindProblem(string message, int status = 400) : Exception(message)
{
    public int Status => status;
}

/// <summary>
/// The searches behind <see cref="FindApi"/>, and what they remember for the session. Here: the names
/// to pick from, the commodity search (Ardent Insight) and what every search shares. The searches
/// that ask Spansh about stations are in FindStations.cs.
/// </summary>
sealed partial class Finder(GameState game, Hauling hauling, MarketData data, Func<DateTime> utcNow)
{
    public static readonly string[] Kinds = ["commodity", "module", "ship", "services"];

    const string ArdentName = "Ardent Insight", SpanshName = "Spansh";   // as the player reads them; MarketData.Ardent is the address
    const int ReachLy = 500;                // as far around a system as Ardent Insight looks
    const int MostPlaces = 100;             // places to buy or sell a commodity that are listed
    const double OfferMinutes = 5;          // how long the same question gets the remembered answer

    // Station types as the game's journal and Ardent Insight spell them, and as Spansh does. Spansh's words are the ones shown.
    static readonly (string Journal, string Spansh)[] StationTypes =
    [
        ("Coriolis", "Coriolis Starport"), ("Orbis", "Orbis Starport"), ("Ocellus", "Ocellus Starport"), ("Dodec", "Dodec Starport"),
        ("Outpost", "Outpost"), ("AsteroidBase", "Asteroid base"), ("MegaShip", "Mega ship"),
        ("CraterPort", "Planetary Port"), ("CraterOutpost", "Planetary Outpost"), ("SurfaceStation", "Surface Settlement"),
        ("OnFootSettlement", "Settlement"), ("FleetCarrier", "Drake-Class Carrier"),
        ("PlanetaryConstructionDepot", "Planetary Construction Depot"), ("SpaceConstructionDepot", "Space Construction Depot"),
    ];

    readonly Dictionary<string, double[]> places = new(StringComparer.OrdinalIgnoreCase);   // where systems are; they do not move

    // ---------------------------------------------------------------- the names to pick from

    /// <summary>The names to pick from. The services themselves say which there are; only the ten services are Studio's own choice.</summary>
    public async Task<NameList> Names(string kind)
    {
        MarketData.Asking ask = data.Ask(utcNow(), CancellationToken.None);
        try
        {
            return kind switch
            {
                "commodity" => Commodities(await Overview(ask)),
                "module" or "ship" => await SpanshList(kind, ask),
                "services" => new NameList([.. Services.Select(service => service.Name)], [], []),
                _ => throw new FindProblem("Unknown kind of thing to find."),
            };
        }
        catch (Exception problem) when (Lost(problem))
        {
            throw Unreachable(kind == "commodity" ? ArdentName : SpanshName, $"The list of names for \"{kind}\" could not be fetched", problem);
        }
    }

    // The price overview is Ardent's list of every commodity; MarketData keeps it for twelve hours.
    // (Started on a background thread, as MarketData's own searches are: sending may dawdle before its first real wait.)
    static Task<PriceOverview> Overview(MarketData.Asking ask) => Task.Run(ask.Overview);

    static NameList Commodities(PriceOverview overview) =>
        new([.. overview.Prices.Keys.Select(GameData.CommodityName).Order(StringComparer.OrdinalIgnoreCase)], [], []);

    // ---------------------------------------------------------------- one search

    /// <summary>Runs one search. What cannot be answered is thrown as a <see cref="FindProblem"/>.</summary>
    public async Task<FindAnswer> Find(string kind, FindRequest request)
    {
        string from = request.From.Trim() is { Length: > 0 } typed ? typed : game.StarSystem ?? "";
        if (from.Length == 0) throw new FindProblem("Studio does not know yet where you are. Start Elite, or type a system under \"From\".");
        if (hauling.Settings.With(request.Filters ?? []) is not { } set) throw new FindProblem("One of the filters holds a value that does not fit it.");
        // The trade search's own station rules, on the player's settings unless the page set others for this search.
        var filter = new StationFilter(StationFilter.PadSize(set.Pad) ?? hauling.ShipPad, set.MaxStarLs, set.Surface, set.Carriers);
        // The game says where the system the player is in lies; any other system the services have to know.
        double[]? here = Same(from, game.StarSystem) ? game.StarPos : null;
        FindAnswer answer = await Asked(kind == "commodity" ? ArdentName : SpanshName, $"Find {kind} \"{request.Name}\" from {from}",
            ask => kind == "commodity" ? Commodity(request, from, here, filter, set, ask) : InStations(kind, request, from, here, filter, ask));
        Log.Info(Invariant($"Find {kind} \"{request.Name}\" from {from}: {answer.Rows.Count} places"));
        return answer;
    }

    /// <summary>
    /// One search's questions to the services. What goes wrong on the way is put in words for the
    /// player, and every request really sent goes to the log, whatever came of it: what was asked
    /// is the first thing to know when an answer looks wrong.
    /// </summary>
    async Task<T> Asked<T>(string service, string doing, Func<MarketData.Asking, Task<T>> search)
    {
        MarketData.Asking ask = data.Ask(utcNow(), CancellationToken.None);
        try
        {
            return await search(ask);
        }
        catch (Exception problem) when (Lost(problem))
        {
            throw Unreachable(service, doing + " failed", problem);
        }
        finally
        {
            if (ask.Urls.Count > 0)
                Log.Info(Invariant($"{doing}: {ask.Urls.Count} requests sent:") + string.Concat(ask.Urls.Select(url => Environment.NewLine + "    " + url)));
        }
    }

    /// <summary>The nearest places to buy a commodity, or to sell it, from Ardent Insight.</summary>
    /// <param name="set">The settings of this search: besides the station filter, how old a price may be and how many tons must be on offer or wanted.</param>
    async Task<FindAnswer> Commodity(FindRequest request, string from, double[]? here, StationFilter filter, MarketSettings set, MarketData.Asking ask)
    {
        PriceOverview overview = await Overview(ask);
        // Ardent names a commodity by the game's own code, which is not always the name without its spaces
        // ("agriculturalmedicines" is Agri-Medicines). What was typed or picked is matched by its letters
        // and digits alone, against the name on the list and against the code itself.
        string typed = Letters(request.Name);
        string id = overview.Prices.Keys.FirstOrDefault(code => Letters(code) == typed || Letters(GameData.CommodityName(code)) == typed)
            ?? throw new FindProblem(NotKnown(request.Name, "commodity", ArdentName));
        string name = GameData.CommodityName(id);
        bool selling = Same(request.Side, "sell");
        string system = MarketData.SystemAddress(from);
        // Whether Ardent knows the system shows in its first answer about it. Where the game has not said
        // where the system lies, that is its position: asked first and alone, so that a name Ardent does
        // not know costs one request and not three. Otherwise it is the offers themselves.
        bool placed = here is not null;
        here ??= await KnownSystem(ArdentName, from, Position(from, system, ask));

        int days = MarketData.Days(set.MaxAgeHours);   // the exact limit in hours is applied below
        // "Any" supply or demand still means at least one ton.
        int least = Math.Max(1, selling ? set.MinDemand : set.MinSupply);
        string carriers = MarketData.CarriersWord(filter.Carriers);
        var around = ask.Answer(Invariant($"{system}/commodity/name/{id}/nearby/{(selling ? "imports" : "exports")}?maxDistance={ReachLy}&maxDaysAgo={days}&minVolume={least}&sort=distance{carriers}"), null, OfferMinutes, Offer.ReadList);
        // "nearby" leaves out the system asked about, so its own stations are a second question
        var inSystem = ask.Answer(Invariant($"{system}/commodity/name/{id}?maxDaysAgo={days}"), null, OfferMinutes, Offer.ReadList);
        var both = Task.WhenAll(around, inSystem);   // so that neither is left to fail unseen
        await (placed ? KnownSystem(ArdentName, from, both) : both);

        var seen = new HashSet<long>();
        var places = new List<FoundPlace>();
        foreach (Offer offer in around.Result.Data.Concat(inSystem.Result.Data))
        {
            (int price, int tons) = selling ? (offer.SellPrice, offer.Demand) : (offer.BuyPrice, offer.Stock);
            // The service's own volume filter lets "demand 0" through, and the system's own list has none.
            // A station in the system searched from is never too far from its star: the player is there already, and
            // would look for it in vain (the owner, 11 October 2026). The pad and the kinds of station still count.
            double? fromStar = Same(offer.StarSystem, from) ? null : offer.StarLs;
            if (!seen.Add(offer.MarketId) || price <= 0 || tons < least || !filter.Keeps(offer.Type, offer.Pad, fromStar)) continue;
            // A station that has the commodity in stock sells it: the demand of one ton the game gives every
            // such station is not somebody wanting it. Near Sol that is three in ten of the "buyers" of gold.
            if (selling && offer.Stock > 0) continue;
            // A price with no date counts as too old.
            if (offer.Updated is not { } updated || (ask.Now - updated).TotalHours > set.MaxAgeHours) continue;
            // Ardent gives whole light years only, so the distance is worked out from where the two systems are.
            double ly = ShipMath.Distance(here, [offer.X, offer.Y, offer.Z]);
            places.Add(new FoundPlace(offer.Station, offer.StarSystem, Math.Round(ly, 2), offer.StarLs, StationFilter.PadLetter(offer.Pad), Shown(offer.Type), "", price, tons, updated));
        }
        // Of two places in one system, the better price first. Ardent sends every place within its reach at
        // once; a hundred are listed at a time, and "more" is how many have been listed so far.
        int listed = Math.Max(0, request.More);
        FoundPlace[] next = [.. places.OrderBy(place => place.DistanceLy).ThenBy(place => selling ? -place.Price : place.Price).Skip(listed).Take(MostPlaces)];
        listed += next.Length;
        bool more = places.Count > listed;
        string? note = places.Count == 0
            ? $"No station within {ReachLy} ly of {from} that passes your filters is known to {(selling ? "buy" : "sell")} {name}. Try older prices, fewer tons or other filters."
            : more ? Invariant($"The nearest {listed} places are listed; there are more farther out.")
            : null;
        return new FindAnswer($"Where to {(selling ? "sell" : "buy")} {name}", from, next, ArdentName, null, note, more ? listed : null,
            around.Result.Asked < inSystem.Result.Asked ? around.Result.Asked : inSystem.Result.Asked);
    }

    /// <summary>Where a system is, from Ardent Insight; asked once a session for each system.</summary>
    async Task<double[]> Position(string system, string address, MarketData.Asking ask)
    {
        if (places.TryGetValue(system, out double[]? known)) return known;
        // (0 minutes: it is `places` that remembers.)
        (double[] position, _) = await ask.Answer(address, null, 0, async (body, stop) =>
        {
            using JsonDocument found = await JsonDocument.ParseAsync(body, default, stop).ConfigureAwait(false);
            JsonElement at = found.RootElement;
            // Without this check, anything that is not a system would count as the system at 0, 0, 0, which is Sol.
            if (at.Field("systemX") is null) throw new InvalidDataException("Its answer does not say where the system is.");
            return (double[])[at.Number("systemX"), at.Number("systemY"), at.Number("systemZ")];
        });
        return places[system] = position;
    }

    // ---------------------------------------------------------------- small helpers

    /// <summary>
    /// The answer to the first question that names a system to a service, which may not know it.
    /// Ardent Insight then says "not found": its own code gives that answer to a question about a
    /// system's position or offers for no other reason (a system without a market gets an empty
    /// list). Spansh says "invalid request"; the name of the thing looked for was checked against
    /// its own list before. Any other refusal is the service letting the player down.
    /// </summary>
    async Task<T> KnownSystem<T>(string service, string system, Task<T> answer)
    {
        try { return await answer; }
        catch (HttpRequestException problem) when (problem.StatusCode == (service == ArdentName ? HttpStatusCode.NotFound : HttpStatusCode.BadRequest))
        {
            // The system the player is in comes from the game: there is no spelling to check.
            throw new FindProblem(Same(system, game.StarSystem)
                ? $"{service} has no data for {system}, the system you are in. Type a system near it under \"From\"."
                : $"{service} does not know a system called \"{system}\". Check its spelling.");
        }
    }

    // Names are compared the way the rest of Studio Market does: without regard to capitals.
    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A commodity's name or code without its spaces, hyphens and capitals: "agrimedicines" for "Agri-Medicines".</summary>
    internal static string Letters(string text) => string.Concat(text.Where(char.IsAsciiLetterOrDigit)).ToLowerInvariant();

    static string NotKnown(string typed, string kind, string service) => string.IsNullOrWhiteSpace(typed)
        ? $"Type the name of the {kind} to find, or pick it from the list."
        : $"{service} knows no {kind} called \"{typed.Trim()}\". Pick a name from the list.";

    /// <summary>A station type in words: Spansh's where it has them, else the journal's with its words set apart ("Stronghold Carrier").</summary>
    internal static string Shown(string type) =>
        StationTypes.Where(known => Same(known.Journal, type)).Select(known => known.Spansh).FirstOrDefault()
        ?? string.Concat(type.Select((letter, at) => at > 0 && char.IsUpper(letter) ? " " + letter : letter.ToString()));

    // What can go wrong between here and a service: no connection, an error page, no answer in time, an answer that is not what it should be.
    internal static bool Lost(Exception problem) => problem is HttpRequestException or IOException or TimeoutException or JsonException or InvalidDataException;

    static FindProblem Unreachable(string service, string doing, Exception problem)
    {
        Log.Error(doing, problem);
        return new FindProblem($"Couldn't get an answer from {service}: {MarketData.Reason(problem)}", 502);
    }
}
