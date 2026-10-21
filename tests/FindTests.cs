using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EliteStudio.App;
using EliteStudio.Market;
using static System.FormattableString;
using Place = EliteStudio.Tests.HaulRig.Place;
using WebRequest = EliteStudio.App.WebRequest;     // System.Net has older types with the same names
using WebResponse = EliteStudio.App.WebResponse;

namespace EliteStudio.Tests;

/// <summary>
/// Ardent Insight and Spansh, played back from tests\Fixtures\find\recorded.json: their real answers
/// to every request "Find by name" makes in these tests, recorded on 9 October 2026 around 12:25 UTC
/// for searches from Sol (and one from Alpha Centauri) and cut down to stay small. The price overview
/// keeps 9 commodities and the two lists of names a dozen each; a list of offers around a system
/// keeps its nearest 40; a Spansh station keeps the fields a search reads, with every entry of the
/// module or ship asked for and two others, and a stations answer its first 8 stations (5 for a
/// service). "count" in those answers is still Spansh's own; a page that short tells a search that
/// Spansh has no more, so none of the recorded searches asks for a second page. An answer is found by the request's
/// address, and for a posted question by the address and the question, letter for letter: a
/// question Spansh does not understand it answers as if nothing had been asked, so its exact
/// wording is part of what is tested. A request that was not recorded gets "404 Not Found".
/// The last two answers were recorded the same day around 20:40 UTC for Studio Loadouts' shopping
/// list (ShoppingTests): Spansh's ten nearest stations that sell three modules a Mandalay build
/// takes, and its ten nearest that sell a Hauler with three modules for it. Each station keeps where
/// its system lies and, of all it sells, the modules and the ship those two lists name and two others.
/// </summary>
sealed class RecordedServices : HttpMessageHandler
{
    static readonly JsonElement Recording = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "find", "recorded.json"))).RootElement;

    static readonly Dictionary<string, (int Status, string Body)> Answers = Recording.GetProperty("answers").EnumerateArray().ToDictionary(
        answer => answer.GetProperty("ask").GetString()!,
        answer => (answer.TryGetProperty("status", out JsonElement status) ? status.GetInt32() : 200, answer.GetProperty("answer").GetRawText()));

    /// <summary>The moment the last answer was recorded: the clock the test searches start on.</summary>
    public static DateTime RecordedUtc { get; } = Recording.GetProperty("recordedUtc").GetDateTime().ToUniversalTime();

    /// <summary>Every request that reached the "network": its address, and for a posted question the address and the question.</summary>
    public List<string> Asked { get; } = [];

    public HashSet<string> UserAgents { get; } = [];

    /// <summary>While set, requests whose address holds this text fail as if the service could not be reached.</summary>
    public string? Down { get; set; }

    /// <summary>A refusal the service sends instead of an answer ("429 Too Many Requests"); null lets the request through.</summary>
    public Func<string, HttpStatusCode?>? Refuse { get; set; }

    /// <summary>A test's own answer to a request; without one, or when it gives null, the recording answers.</summary>
    public Func<string, string?>? Instead { get; set; }

    /// <summary>The one recorded answer whose request holds all of these texts, as the service sent it.</summary>
    public static string Recorded(params string[] holding) => Answers.Single(answer => holding.All(text => answer.Key.Contains(text, StringComparison.Ordinal))).Value.Body;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken stop)
    {
        string address = request.RequestUri!.OriginalString;
        string asked = request.Content is null ? address : $"{address} {await request.Content.ReadAsStringAsync(stop)}";
        lock (Asked)
        {
            Asked.Add(asked);
            UserAgents.Add(request.Headers.UserAgent.ToString());
        }
        if (Down is not null && address.Contains(Down, StringComparison.Ordinal)) throw new HttpRequestException("No such host is known.");
        if (Refuse?.Invoke(asked) is { } refusal) return new HttpResponseMessage(refusal) { Content = new StringContent("", Encoding.UTF8, "text/plain") };
        (int status, string body) = Instead?.Invoke(asked) is { } own ? (200, own)
            : Answers.TryGetValue(asked, out (int Status, string Body) recorded) ? recorded
            : (404, "{\"error\":\"Not Found\"}");
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

/// <summary>
/// "Find by name" through its routes, against the recorded answers of the two services: what is
/// asked of them, what is made of their answers, the station filters, and what the player is told
/// when a name, a system or a service lets them down. The player sits in Sol in a Type-8 (medium
/// pad) with the standard settings: 5,000 ls from the star at most, all surface stations, no fleet
/// carriers, prices up to 48 hours old, 100 tons or more.
/// </summary>
public sealed class FindTests : IDisposable
{
    const string Ardent = "https://api.ardent-insight.com/v2", Spansh = "https://spansh.co.uk/api/stations", Edsm = "https://www.edsm.net/api-system-v1/stations/shipyard";
    const string GoldAroundSol = Ardent + "/system/name/Sol/commodity/name/gold/nearby/exports?maxDistance=500&maxDaysAgo=2&minVolume=100&sort=distance&fleetCarriers=false";
    const string GoldInSol = Ardent + "/system/name/Sol/commodity/name/gold?maxDaysAgo=2";

    // The rest of a standard question to Spansh, after what is looked for: the station types the filters let
    // through (in Spansh's words), the distance from the star, nearest first, and Sol by where it lies.
    const string StandardTypes = "\"type\":" + """{"value":["Coriolis Starport","Orbis Starport","Ocellus Starport","Dodec Starport","Outpost","Asteroid base","Mega ship","Planetary Port","Planetary Outpost","Surface Settlement","Settlement"]}""";
    const string NearSol = StandardTypes + ""","distance_to_arrival":{"comparison":"<=>","value":[0,5000]}},"sort":[{"distance":{"direction":"asc"}}],"size":20,"page":0,"reference_coords":{"x":0,"y":0,"z":0}}""";

    static readonly Place Sol = new("Sol", [0, 0, 0], "Daedalus", 128016384);

    readonly RecordedServices net = new();
    readonly MarketData data;
    readonly HaulRig rig = new(HaulRig.In(Sol, docked: false));
    readonly WebServer web;
    DateTime clock = RecordedServices.RecordedUtc;

    // The brokers the player marked, in a settings file of their own: the rig's is Hauling's, which saves its whole file.
    string BrokersFile => Path.Combine(Path.GetDirectoryName(rig.SettingsFile)!, "brokers.json");

    public FindTests()
    {
        data = new MarketData(net);
        web = new WebServer(rig.Game.JournalDir);   // no page is asked for here: any folder will do
        FindApi.Map(web, rig.Watcher.State, rig.Hauling, data, () => clock, brokers: new BrokerBook(new Settings(BrokersFile))).EdsmGap = TimeSpan.Zero;
    }

    public void Dispose()
    {
        web.Dispose();
        rig.Dispose();
        data.Dispose();
    }

    static async Task<(int Status, JsonElement Json)> Call(WebServer server, string method, string path, string body = "")
    {
        string[] parts = path.Split('?', 2);
        WebResponse answer = await server.Handle(new WebRequest(method, parts[0], WebServer.ParseQuery(parts.Length == 2 ? parts[1] : ""), body));
        return (answer.Status, JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(answer.Body)));
    }

    /// <summary>One search that must succeed.</summary>
    async Task<JsonElement> Find(string kind, string body)
    {
        var (status, answer) = await Call(web, "POST", "/api/market/find/" + kind, body);
        Assert.True(status == 200, answer.GetRawText());
        return answer;
    }

    /// <summary>One search that must be refused: the status and the words for the player.</summary>
    async Task<(int Status, string Error)> Refused(string kind, string body)
    {
        var (status, answer) = await Call(web, "POST", "/api/market/find/" + kind, body);
        Assert.False(answer.GetProperty("ok").GetBoolean());
        return (status, answer.GetProperty("error").GetString()!);
    }

    static string[] Texts(JsonElement list) => [.. list.EnumerateArray().Select(text => text.GetString()!)];

    static JsonElement[] Rows(JsonElement answer) => [.. answer.GetProperty("rows").EnumerateArray()];

    /// <summary>A row as one line: station | system | ly | ls from the star | pad | type | item | price | tons | reported. "-" where the answer has no value.</summary>
    static string Line(JsonElement row) => string.Join(" | ", ((string[])["station", "system", "distanceLy", "starLs", "pad", "type", "item", "price", "tons", "updated"])
        .Select(name => row.GetProperty(name) is { ValueKind: not JsonValueKind.Null } value ? value.ToString() : "-"));

    static string[] Stations(JsonElement answer) => [.. Rows(answer).Select(row => row.GetProperty("station").GetString()!)];

    /// <summary>One answer of a list that comes in batches, as one line: how many rows, the first and the last station | the note | what asks for more. Nothing where the answer has none.</summary>
    static string Batch(JsonElement answer) =>
        $"{Rows(answer).Length} rows, {Stations(answer).FirstOrDefault()} to {Stations(answer).LastOrDefault()} | {answer.GetProperty("note")} | more: {answer.GetProperty("more")}";

    /// <summary>What was asked of the services: first everything but lists of offers, in the order asked, then the lists of offers by address. (The two of one search go out side by side, and either may arrive first.)</summary>
    string[] Asked() => [.. net.Asked.OrderBy(asked => asked.Contains("/commodity/name/", StringComparison.Ordinal) ? asked : "", StringComparer.Ordinal)];

    static void NearestFirst(JsonElement answer)
    {
        double[] distances = [.. Rows(answer).Select(row => row.GetProperty("distanceLy").GetDouble())];
        Assert.Equal(distances.Order(), distances);
    }

    // ---------------------------------------------------------------- the names to pick from

    [Fact]
    public async Task Names_come_from_the_services_own_lists_and_each_list_is_asked_for_once()
    {
        for (int round = 0; round < 2; round++)
        {
            var (_, commodities) = await Call(web, "GET", "/api/market/find/names?kind=commodity");
            // By name, and the names are the game's own: the service's codes for the first and the sixth are
            // "agriculturalmedicines" and "lowtemperaturediamond". Harma Silver Sea Rum is a rare good.
            Assert.Equal(["Agri-Medicines", "Basic Medicines", "Gold", "Harma Silver Sea Rum", "Hydrogen Fuel", "Low Temperature Diamonds", "Palladium", "Silver", "Tritium"],
                Texts(commodities.GetProperty("names")));

            var (_, modules) = await Call(web, "GET", "/api/market/find/names?kind=module");
            Assert.Equal(12, modules.GetProperty("names").GetArrayLength());
            Assert.Equal(["Detailed Surface Scanner", "Frame Shift Drive", "Frame Shift Drive (SCO)"], Texts(modules.GetProperty("names")).Take(3));
            Assert.Equal(["0", "1", "2", "3", "4", "5", "6", "7", "8"], Texts(modules.GetProperty("classes")));
            Assert.Equal(["A", "B", "C", "D", "E", "F", "G", "H", "I"], Texts(modules.GetProperty("ratings")));

            var (_, ships) = await Call(web, "GET", "/api/market/find/names?kind=ship");
            Assert.Equal(["Anaconda", "Cobra MkIII", "Hauler", "Imperial Cutter", "Kestrel Mk II", "Mandalay", "Python", "Python MkII", "Sidewinder", "Type-8 Transporter"],
                Texts(ships.GetProperty("names")));
            Assert.Empty(ships.GetProperty("classes").EnumerateArray());

            var (_, services) = await Call(web, "GET", "/api/market/find/names?kind=services");
            Assert.Equal(["Raw material trader", "Manufactured material trader", "Encoded material trader", "Human technology broker", "Guardian technology broker",
                "Interstellar factors", "Black market", "Shipyard", "Outfitting", "Repair"], Texts(services.GetProperty("names")));
        }

        Assert.Equal([Ardent + "/commodities", Spansh + "/field_values/modules", Spansh + "/field_values/ships"], net.Asked);
        Assert.Equal([Program.UserAgent], net.UserAgents);

        var (status, unknown) = await Call(web, "GET", "/api/market/find/names?kind=planets");
        Assert.Equal((400, "Unknown kind of thing to find."), (status, unknown.GetProperty("error").GetString()));
    }

    // ---------------------------------------------------------------- commodities, from Ardent Insight

    [Fact]
    public async Task A_commodity_to_buy_lists_the_nearest_places_that_sell_it()
    {
        JsonElement found = await Find("commodity", """{ "name": "gold" }""");

        Assert.Equal(("Where to buy Gold", "Sol", "Ardent Insight"),
            (found.GetProperty("what").GetString(), found.GetProperty("from").GetString(), found.GetProperty("source").GetString()));
        Assert.Equal(RecordedServices.RecordedUtc, found.GetProperty("askedUtc").GetDateTime());
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (found.GetProperty("note").ValueKind, found.GetProperty("link").ValueKind));
        Assert.Equal(JsonValueKind.Null, found.GetProperty("more").ValueKind);   // fewer than a hundred places: this is all of them
        // The price overview for the commodity's name, then the stations around Sol and Sol's own: "nearby" leaves those out.
        Assert.Equal([Ardent + "/commodities", GoldAroundSol, GoldInSol], Asked());

        // Sol's own stations first, the cheapest of them on top; then Barnard's Star, 5.95 ly away by
        // the two systems' positions (the service itself says "6").
        JsonElement[] rows = Rows(found);
        Assert.Equal(42, rows.Length);
        Assert.Equal(
        [
            "Galileo | Sol | 0 | 503.478445 | L | Ocellus Starport |  | 45099 | 39819 | 2026-10-09T08:32:05Z",
            "Burnell Station | Sol | 0 | 362.91813 | M | Outpost |  | 45436 | 367 | 2026-10-09T08:08:53Z",
            "Columbus | Sol | 0 | 2616.175016 | L | Ocellus Starport |  | 45461 | 669 | 2026-10-09T08:40:36Z",
            "Daedalus | Sol | 0 | 232.241181 | L | Orbis Starport |  | 45505 | 24685 | 2026-10-09T08:00:58Z",
            "Titan City | Sol | 0 | 4717.541493 | L | Orbis Starport |  | 45642 | 176 | 2026-10-09T08:55:10Z",
            "Miller Depot | Barnard's Star | 5.95 | 37.254078 | L | Orbis Starport |  | 45229 | 112 | 2026-10-09T03:27:32Z",
            "Levi-Strauss Installation | Barnard's Star | 5.95 | 6.498147 | M | Outpost |  | 45240 | 214 | 2026-10-08T02:01:22Z",
            "Efremov Plant | Sirius | 8.59 | 4389.561194 | L | Planetary Outpost |  | 49560 | 269 | 2026-10-08T23:40:00Z",
        ], rows.Take(8).Select(Line));
        NearestFirst(found);
        // What the standard filters leave out of the recording: three stations more than 5,000 ls from
        // their star, and every station in Sol that only buys gold or has fewer than 100 tons of it.
        Assert.DoesNotContain(rows, row => row.GetProperty("starLs").GetDouble() > 5000 || row.GetProperty("tons").GetInt32() < 100);
        Assert.Empty(Stations(found).Intersect(["Gentle City", "Linnaeus Station", "Newman Reach", "Abraham Lincoln", "Bourne Mining Installation"]));
    }

    [Fact]
    public async Task A_commodity_to_sell_lists_the_nearest_places_that_want_it()
    {
        JsonElement found = await Find("commodity", """{ "name": "Gold", "side": "sell" }""");

        Assert.Equal("Where to sell Gold", found.GetProperty("what").GetString());
        Assert.Equal([Ardent + "/commodities", GoldAroundSol.Replace("exports", "imports"), GoldInSol], Asked());
        // In one system the best price comes first; stations that pay the same stay in the service's order.
        JsonElement[] rows = Rows(found);
        Assert.Equal(32, rows.Length);
        Assert.Equal(
        [
            "Abraham Lincoln | Sol | 0 | 504.03136 | L | Orbis Starport |  | 50945 | 214306 | 2026-10-09T08:34:36Z",
            "Li Qing Jao | Sol | 0 | 504.240549 | L | Orbis Starport |  | 50945 | 204021 | 2026-10-09T08:21:54Z",
            "M.Gorbachev | Sol | 0 | 504.253561 | L | Orbis Starport |  | 50945 | 214306 | 2026-10-09T08:27:02Z",
            "Mars High | Sol | 0 | 817.285903 | L | Orbis Starport |  | 50944 | 122860 | 2026-10-09T08:47:50Z",
        ], rows.Take(4).Select(Line));
        // A settlement (Odyssey is allowed) and a Power's stronghold carrier, which is no fleet carrier.
        Assert.Equal("Ikeda Tourist Zone | Sol | 0 | 4719.432611 | L | Settlement |  | 50204 | 131 | 2026-10-08T16:17:50Z", Line(rows[8]));
        Assert.Equal("Stronghold Carrier | Alpha Centauri | 4.38 | 4473.863372 | L | Stronghold Carrier |  | 51237 | 1024 | 2026-10-09T05:27:12Z", Line(rows[9]));
        NearestFirst(found);
        // The service was asked for a demand of 100 tons or more, and it sends stations with no demand at all
        // as well (Pohl Metallurgic Station, 5.95 ly away, is the nearest of 14). Those are no places to sell.
        Assert.Contains("\"stationName\":\"Pohl Metallurgic Station\"", RecordedServices.Recorded("/Sol/", "/nearby/imports", "minVolume=100&"));
        Assert.DoesNotContain(rows, row => row.GetProperty("tons").GetInt32() < 100);

        // Asked for any demand, the service sends the stations that sell gold too: the game gives every one of them
        // a "demand" of one ton (13 of the 40 nearest here, Miller Depot among them). Those are no places to sell either.
        JsonElement any = await Find("commodity", """{ "name": "Gold", "side": "sell", "filters": { "minDemand": 0 } }""");
        Assert.Contains("\"stationName\":\"Miller Depot\"", RecordedServices.Recorded("/Sol/", "/nearby/imports", "minVolume=1&"));
        Assert.Equal(22, Rows(any).Length);
        Assert.Empty(Stations(any).Intersect(["Levi-Strauss Installation", "Boston Base", "Miller Depot", "Efremov Plant"]));
    }

    [Fact]
    public async Task A_commodity_is_found_by_the_games_name_for_it_and_asked_for_by_the_services_code()
    {
        // Nothing was recorded for these two commodities: to the questions about them the stand-in answers "no offers".
        net.Instead = asked => asked.Contains("/commodity/name/", StringComparison.Ordinal) ? "[]" : null;
        const string diamonds = Ardent + "/system/name/Sol/commodity/name/lowtemperaturediamond";

        // As picked from the list, as somebody might type it, and the service's own code.
        foreach (string typed in (string[])["Low Temperature Diamonds", " low temperature diamonds ", "LowTemperatureDiamonds", "lowtemperaturediamond"])
            Assert.Equal("Where to buy Low Temperature Diamonds", (await Find("commodity", JsonSerializer.Serialize(new { name = typed }))).GetProperty("what").GetString());
        // (The third question is the one a list that comes back short brings: what the filters may have left out.)
        Assert.Equal([Ardent + "/commodities", diamonds + "/nearby/exports?maxDistance=500&maxDaysAgo=2&minVolume=100&sort=distance&fleetCarriers=false",
            diamonds + "/nearby/exports?maxDistance=500&maxDaysAgo=30&minVolume=100&sort=distance", diamonds + "?maxDaysAgo=2"], Asked());

        JsonElement medicines = await Find("commodity", """{ "name": "agri-medicines", "side": "sell" }""");
        Assert.Equal("Where to sell Agri-Medicines", medicines.GetProperty("what").GetString());
        Assert.StartsWith("No station within 500 ly of Sol that passes your filters is known to buy Agri-Medicines.", medicines.GetProperty("note").GetString());
        Assert.Contains(net.Asked, asked => asked.StartsWith(Ardent + "/system/name/Sol/commodity/name/agriculturalmedicines/nearby/imports?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Show_more_lists_the_next_hundred_places_of_a_commodity_and_asks_nothing_new()
    {
        // 250 made-up stations that sell gold, each one light year farther from Sol than the one before.
        string updated = clock.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string depots = "[" + string.Join(",", Enumerable.Range(1, 250).Select(n => Invariant(
            $$"""{"commodityName":"gold","marketId":{{n}},"stationName":"Depot {{n}}","stationType":"Coriolis","distanceToArrival":100,"maxLandingPadSize":3,"systemName":"Far {{n}}","systemX":{{n}},"systemY":0,"systemZ":0,"buyPrice":45000,"stock":500,"sellPrice":0,"demand":0,"meanPrice":47000,"updatedAt":"{{updated}}","distance":{{n}}}"""))) + "]";
        net.Instead = asked => asked == GoldAroundSol ? depots : asked == GoldInSol ? "[]" : null;
        Assert.Equal("100 rows, Depot 1 to Depot 100 | The nearest 100 places are listed; there are more farther out. | more: 100",
            Batch(await Find("commodity", """{ "name": "Gold" }""")));
        // The same search with the answer's "more": the places after those, and none of them again.
        Assert.Equal("100 rows, Depot 101 to Depot 200 | The nearest 200 places are listed; there are more farther out. | more: 200",
            Batch(await Find("commodity", """{ "name": "Gold", "more": 100 }""")));
        Assert.Equal("50 rows, Depot 201 to Depot 250 |  | more: ", Batch(await Find("commodity", """{ "name": "Gold", "more": 200 }""")));
        // Ardent sent every place within its reach the first time: nothing was asked for the two batches after it.
        Assert.Equal([Ardent + "/commodities", GoldAroundSol, GoldInSol], Asked());
    }

    [Fact]
    public async Task A_station_in_the_system_searched_from_is_never_too_far_from_its_star()
    {
        // Two made-up stations that sell gold, both 128,000 ls from their star: one in Sol itself, one a light year away.
        string updated = clock.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string Depot(int id, string name, string system, int x) => Invariant(
            $$"""[{"commodityName":"gold","marketId":{{id}},"stationName":"{{name}}","stationType":"Coriolis","distanceToArrival":128000,"maxLandingPadSize":3,"systemName":"{{system}}","systemX":{{x}},"systemY":0,"systemZ":0,"buyPrice":45000,"stock":500,"sellPrice":0,"demand":0,"meanPrice":47000,"updatedAt":"{{updated}}","distance":{{x}}}]""");
        // A third, in Sol too, has a small pad only.
        string home = Depot(2, "Home Depot", "Sol", 0)[..^1] + "," + Depot(3, "Small Depot", "Sol", 0).Replace("\"maxLandingPadSize\":3", "\"maxLandingPadSize\":1")[1..];
        net.Instead = asked => asked == GoldAroundSol ? Depot(1, "Far Depot", "Far 1", 1) : asked == GoldInSol ? home : null;

        // 5,000 ls at most leaves out the one next door and keeps the one the player is next to. The pad still counts there.
        Assert.Equal(["Home Depot"], Stations(await Find("commodity", """{ "name": "Gold", "filters": { "pad": "L", "maxStarLs": 5000 } }""")));
    }

    [Fact]
    public async Task Filters_set_for_one_search_replace_the_players_own_and_are_not_saved()
    {
        int changes = rig.Changes;

        // A large pad, close to the star, no surface stations, prices from the last 30 hours.
        // (30 hours are still "2 days" to the service, so the questions are the standard ones.)
        JsonElement found = await Find("commodity", """{ "name": "Gold", "filters": { "pad": "L", "maxStarLs": 1000, "surface": "none", "maxAgeHours": 30 } }""");
        Assert.Equal([Ardent + "/commodities", GoldAroundSol, GoldInSol], Asked());
        // Sol's own stations are listed however far from the Sun they are: the player is in the system already.
        static bool Home(JsonElement row) => row.GetProperty("system").GetString() == "Sol";
        static bool Near(JsonElement row) => row.GetProperty("starLs").GetDouble() <= 1000;
        Assert.Equal(["Galileo", "Daedalus", "Miller Depot", "Birkeland City", "Polyakov Station", "Wang City", "Gaspardeportola's Progress", "Porter Legacy",
            "Lonnie's Iceberg Lair", "Scortia Hub", "Cowper Exchange", "Hooper Relay", "New Nassau", "Zamka Platform", "Radium Station"],
            Rows(found).Where(Near).Select(row => row.GetProperty("station").GetString()!));
        Assert.Contains(Rows(found), row => Home(row) && !Near(row));
        Assert.All(Rows(found), row =>
        {
            Assert.Equal("L", row.GetProperty("pad").GetString());
            Assert.True(Home(row) || Near(row));
            Assert.DoesNotContain("Planetary", row.GetProperty("type").GetString());
            Assert.True(clock - row.GetProperty("updated").GetDateTime().ToUniversalTime() <= TimeSpan.FromHours(30));
        });

        // Fleet carriers, the price age in days and the least supply are part of the question itself.
        found = await Find("commodity", """{ "name": "Gold", "filters": { "carriers": true } }""");
        Assert.Contains(GoldAroundSol.Replace("&fleetCarriers=false", ""), net.Asked);
        // The carriers found bring one more question, to Spansh: who may dock at them. (Not recorded: nothing is known of these two.)
        Assert.Equal(Spansh + """/search {"filters":{"type":{"value":["Drake-Class Carrier"]},"name":{"value":["G2M-BQQ","H1M-21L"]}},"size":2,"page":0}""", net.Asked[^1]);
        Assert.All(Rows(found), row => Assert.Equal(row.GetProperty("type").GetString() == "Drake-Class Carrier" ? "access not known" : "", row.GetProperty("access").GetString()));
        Assert.Equal(["G2M-BQQ | Duamta | 9.88 | 1285.10882 | L | Drake-Class Carrier |  | 2356 | 1796 | 2026-10-08T01:52:59Z",
            "H1M-21L | Gendalla | 16.55 | 66.60763 | L | Drake-Class Carrier |  | 1508029 | 249 | 2026-10-08T17:56:37Z"],
            Rows(found).Where(row => row.GetProperty("type").GetString() == "Drake-Class Carrier").Select(Line));
        // (Nothing was recorded for a week of prices: to those two questions the stand-in answers "no offers".)
        net.Instead = asked => asked.Contains("maxDaysAgo=7", StringComparison.Ordinal) ? "[]" : null;
        Assert.Empty(Rows(await Find("commodity", """{ "name": "Gold", "filters": { "minSupply": 0, "maxAgeHours": 168 } }""")));
        Assert.Contains(GoldAroundSol.Replace("maxDaysAgo=2&minVolume=100", "maxDaysAgo=7&minVolume=1"), net.Asked);   // "Any" still means one ton
        net.Instead = null;

        // A value that does not fit its filter is refused before anything is asked.
        int asked = net.Asked.Count;
        Assert.Equal((400, "One of the filters holds a value that does not fit it."), await Refused("commodity", """{ "name": "Gold", "filters": { "maxStarLs": "far" } }"""));
        Assert.Equal(asked, net.Asked.Count);

        // None of it touched the player's settings: the next plain search is the standard one again.
        Assert.Equal(changes, rig.Changes);
        Assert.Equal(new MarketSettings(), rig.Hauling.Settings with { Presets = new MarketSettings().Presets });
        Assert.Equal(42, Rows(await Find("commodity", """{ "name": "Gold" }""")).Length);
    }

    [Fact]
    public async Task A_station_found_by_name_comes_with_what_spansh_knows_of_it_for_its_own_sheet()
    {
        // A made-up fleet carrier as Spansh sends one, cut down to what the sheet reads.
        const string carrier = """
            {"count":1,"results":[{"name":"VBY-LXV","carrier_name":"ARGOS","type":"Drake-Class Carrier","carrier_docking_access":"Squadron Friends","system_name":"Fong Wang",
              "distance":68.9863,"distance_to_arrival":18.2,"large_pads":8,"medium_pads":4,"small_pads":4,"market_id":3706813440,"market_updated_at":"2026-10-11T18:03:42Z",
              "updated_at":"2026-10-11T18:05:00Z","outfitting_updated_at":"2026-07-17T16:00:10Z","government":"Private Ownership","controlling_minor_faction":"FleetCarrier",
              "economies":[{"name":"Private Enterprise","share":100.0}],"system_controlling_power":"Yuri Grom","system_power_state":"Stronghold","system_population":11147154,
              "material_trader":"Raw","services":[{"name":"Dock"},{"name":"Market"},{"name":"Refuel"},{"name":"Material Trader"},{"name":"Market"}],
              "market":[{"commodity":"Gold","buy_price":45000,"sell_price":44000,"supply":10,"demand":0}]}]}
            """;
        net.Instead = asked => asked.StartsWith(Spansh + "/search ", StringComparison.Ordinal) && asked.Contains("\"name\":{\"value\":\"Argos\"}", StringComparison.Ordinal) ? carrier : null;
        await Find("station", """{ "name": "Argos" }""");

        var (status, prices) = await Call(web, "GET", "/api/market/find/prices?id=3706813440");
        Assert.Equal(200, status);
        JsonElement sheet = prices.GetProperty("details");
        string Text(string name) => sheet.GetProperty(name).GetString()!;
        Assert.Equal(("VBY-LXV", "ARGOS", "Fong Wang", "Drake-Class Carrier", "squadron and friends only"), (Text("station"), Text("carrierName"), Text("system"), Text("type"), Text("access")));
        Assert.Equal((18.2, 68.99, 8, 4, 4), (sheet.GetProperty("starLs").GetDouble(), sheet.GetProperty("distanceLy").GetDouble(),
            sheet.GetProperty("largePads").GetInt32(), sheet.GetProperty("mediumPads").GetInt32(), sheet.GetProperty("smallPads").GetInt32()));
        // Each service once; a material trader says which kind. An economy that is all of the station needs no share.
        Assert.Equal(["Dock", "Market", "Refuel", "Material Trader (Raw)"], Texts(sheet.GetProperty("services")));
        Assert.Equal(["Private Enterprise"], Texts(sheet.GetProperty("economies")));
        Assert.Equal(("Private Ownership", "", "FleetCarrier", "Yuri Grom", "Stronghold", ""), (Text("government"), Text("allegiance"), Text("faction"), Text("power"), Text("powerState"), Text("state")));
        Assert.Equal(11147154, sheet.GetProperty("population").GetInt64());
        Assert.Equal(new DateTime(2026, 10, 11, 18, 3, 42, DateTimeKind.Utc), sheet.GetProperty("marketUpdated").GetDateTime().ToUniversalTime());
        Assert.Equal(JsonValueKind.Null, sheet.GetProperty("shipyardUpdated").ValueKind);   // it has no shipyard to report
    }

    [Fact]
    public async Task A_station_name_opens_the_sheet_of_the_station_of_that_exact_name_in_that_system()
    {
        // Two stations of one name, as Spansh sends them for the name alone; the system named picks between them.
        const string namesakes = """
            {"count":2,"results":[{"name":"Smith Hub","type":"Coriolis Starport","system_name":"Far Away","market_id":11,"large_pads":5,"services":[{"name":"Market"}]},
              {"name":"Smith Hub","type":"Outpost","system_name":"Near By","market_id":22,"medium_pads":3,"government":"Democracy","services":[{"name":"Refuel"}],
               "market":[{"commodity":"Gold","buy_price":45000,"sell_price":44000,"supply":10,"demand":0}]}]}
            """;
        net.Instead = asked => asked.StartsWith(Spansh + "/search ", StringComparison.Ordinal) && asked.Contains("\"name\":{\"value\":[\"Smith Hub\"]}", StringComparison.Ordinal) ? namesakes : null;

        var (status, sheet) = await Call(web, "GET", "/api/market/inspect/station?name=" + Uri.EscapeDataString(" Smith Hub ") + "&system=" + Uri.EscapeDataString("near by"));
        Assert.Equal(200, status);
        Assert.Equal(("Smith Hub", "Near By", "Outpost", "Democracy", 3), (sheet.GetProperty("station").GetString(), sheet.GetProperty("system").GetString(),
            sheet.GetProperty("type").GetString(), sheet.GetProperty("government").GetString(), sheet.GetProperty("mediumPads").GetInt32()));
        // The system goes into the question too, and Sol by where it lies (the game has said).
        Assert.Equal(Spansh + """/search {"filters":{"name":{"value":["Smith Hub"]},"system_name":{"value":["near by"]}},"size":5,"page":0,"reference_coords":{"x":0,"y":0,"z":0}}""", net.Asked[^1]);
        // Its price list came with it, and can be opened from the sheet without another question.
        int asked = net.Asked.Count;
        Assert.Equal("Smith Hub", (await Call(web, "GET", "/api/market/find/prices?id=22")).Json.GetProperty("station").GetString());
        Assert.Equal(asked, net.Asked.Count);

        // Without a system, the first one Spansh sends. A name it does not know, or not in that system, is said so.
        Assert.Equal("Far Away", (await Call(web, "GET", "/api/market/inspect/station?name=Smith%20Hub")).Json.GetProperty("system").GetString());
        var (missing, why) = await Call(web, "GET", "/api/market/inspect/station?name=Smith%20Hub&system=Nowhere");
        Assert.Equal((404, "Spansh knows no station called \"Smith Hub\" in Nowhere."), (missing, why.GetProperty("error").GetString()));
        Assert.Equal(400, (await Call(web, "GET", "/api/market/inspect/station?name=%20")).Status);
    }

    [Fact]
    public async Task A_system_name_opens_the_sheet_of_that_star_system_with_its_factions_and_its_stations()
    {
        const string systems = "https://spansh.co.uk/api/systems/search";
        const string star = """
            {"count":1,"results":[{"name":"Fong Wang","allegiance":"Federation","government":"Corporate","security":"High","population":11147154,"primary_economy":"Refinery",
              "secondary_economy":"Industrial","controlling_power":"Yuri Grom","power_state":"Stronghold","power":["Yuri Grom","Felicia Winters"],
              "controlling_minor_faction":"Fong Wang Limited","controlling_minor_faction_state":"Boom","region":"Inner Orion Spur","needs_permit":false,"x":3,"y":4,"z":0,
              "body_count":27,"updated_at":"2026-10-12T20:39:30Z",
              "minor_faction_presences":[{"name":"Allied Fong Wang Front","allegiance":"Independent","government":"Dictatorship","influence":0.074597,"state":"None"},
                {"name":"Fong Wang Limited","allegiance":"Federation","government":"Corporate","influence":0.5512,"state":"Boom"}],
              "stations":[{"name":"VBY-LXV","type":"Drake-Class Carrier","distance_to_arrival":18.2,"large_pads":8,"has_market":true},
                {"name":"Far Port","type":"Coriolis Starport","distance_to_arrival":900.4,"large_pads":4,"has_market":true,"has_shipyard":true,"has_outfitting":true},
                {"name":"Near Post","type":"Outpost","distance_to_arrival":58.6,"medium_pads":2,"has_market":true}]}]}
            """;
        // (As typed, capitals and all: Spansh does not mind them. A name it does not know brings an empty list.)
        net.Instead = asked => !asked.StartsWith(systems + " ", StringComparison.Ordinal) ? null
            : asked.Contains("\"fong wang\"", StringComparison.Ordinal) ? star : """{"count":0,"results":[]}""";

        var (status, sheet) = await Call(web, "GET", "/api/market/inspect/system?name=fong%20wang");
        Assert.Equal(200, status);
        string Text(string name) => sheet.GetProperty(name).GetString()!;
        Assert.Equal(("Fong Wang", "Federation", "Corporate", "High", "Refinery, Industrial", "Yuri Grom", "Stronghold", "Fong Wang Limited", "Boom", "Inner Orion Spur"),
            (Text("name"), Text("allegiance"), Text("government"), Text("security"), Text("economy"), Text("power"), Text("powerState"), Text("faction"), Text("factionState"), Text("region")));
        Assert.Equal((5.0, 11147154, 27, false), (sheet.GetProperty("distanceLy").GetDouble(), sheet.GetProperty("population").GetInt64(), sheet.GetProperty("bodies").GetInt32(), sheet.GetProperty("needsPermit").GetBoolean()));
        Assert.Equal(["Yuri Grom", "Felicia Winters"], Texts(sheet.GetProperty("powers")));
        // The factions by their influence, in hundredths; a faction in no state has none to show.
        Assert.Equal(["Fong Wang Limited 55.1 Boom", "Allied Fong Wang Front 7.5 "],
            sheet.GetProperty("factions").EnumerateArray().Select(faction => $"{faction.GetProperty("name").GetString()} {faction.GetProperty("influence").GetDouble()} {faction.GetProperty("state").GetString()}"));
        // Stations nearest the star first, fleet carriers after them.
        Assert.Equal(["Near Post Outpost M market", "Far Port Coriolis Starport L market shipyard outfitting", "VBY-LXV Drake-Class Carrier L market"],
            sheet.GetProperty("stations").EnumerateArray().Select(station => string.Join(" ", new[] { station.GetProperty("name").GetString(), station.GetProperty("type").GetString(), station.GetProperty("pad").GetString(),
                station.GetProperty("market").GetBoolean() ? "market" : null, station.GetProperty("shipyard").GetBoolean() ? "shipyard" : null, station.GetProperty("outfitting").GetBoolean() ? "outfitting" : null }.OfType<string>())));

        var (missing, why) = await Call(web, "GET", "/api/market/inspect/system?name=Nowhere");
        Assert.Equal((404, "Spansh knows no system called \"Nowhere\"."), (missing, why.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task A_sector_lists_the_systems_whose_names_start_with_what_was_typed_nearest_first()
    {
        const string systems = "https://spansh.co.uk/api/systems/search";
        // Spansh's best matches for "Wregoe YV-W": three of that sector, and one whose name is only like it.
        const string matches = """
            {"count":4,"results":[
              {"name":"Wregoe YV-W b56-4","distance":341.2,"population":0,"body_count":3,"updated_at":"2026-10-01T10:00:00Z"},
              {"name":"Wregoe YV-W b56-10","distance":12.456,"population":52000,"allegiance":"Empire","government":"Corporate","security":"Low","primary_economy":"Industrial",
               "secondary_economy":"Extraction","controlling_power":"A. Lavigny-Duval","body_count":14,"stations":[{"name":"One"},{"name":"Two"}]},
              {"name":"Wregoe XV-W b56-0","distance":1.0,"population":0},
              {"name":"wregoe yv-w b2-0","distance":900.0}]}
            """;
        net.Instead = asked => asked.StartsWith(systems + " ", StringComparison.Ordinal) ? matches : null;

        var (status, found) = await Call(web, "POST", "/api/market/find/sector", """{ "name": " Wregoe YV-W " }""");
        Assert.Equal(200, status);
        Assert.Equal(("Systems of Wregoe YV-W", "Sol", "Spansh"), (found.GetProperty("what").GetString(), found.GetProperty("from").GetString(), found.GetProperty("source").GetString()));
        // Nearest first; the one of another sector is left out, and capitals are not minded.
        Assert.Equal(["Wregoe YV-W b56-10 12.46", "Wregoe YV-W b56-4 341.2", "wregoe yv-w b2-0 900"],
            Rows(found).Select(row => $"{row.GetProperty("system").GetString()} {row.GetProperty("distanceLy").GetDouble()}"));
        JsonElement lived = Rows(found)[0];
        Assert.Equal((52000, "Empire", "Low", "Industrial, Extraction", "A. Lavigny-Duval", 2, 14), (lived.GetProperty("population").GetInt64(), lived.GetProperty("allegiance").GetString(),
            lived.GetProperty("security").GetString(), lived.GetProperty("economy").GetString(), lived.GetProperty("power").GetString(), lived.GetProperty("stations").GetInt32(), lived.GetProperty("bodies").GetInt32()));
        Assert.Equal(JsonValueKind.Null, Rows(found)[1].GetProperty("population").ValueKind);   // nobody lives there
        Assert.Equal(JsonValueKind.Null, found.GetProperty("more").ValueKind);                   // a page that was not full is the last
        // The question: names like the one typed, best match first, and Sol by where it lies.
        Assert.Equal(systems + """ {"filters":{"name":{"value":"Wregoe YV-W"}},"size":50,"page":0,"reference_coords":{"x":0,"y":0,"z":0}}""", net.Asked[^1]);

        // Only where people live: that goes into the question, and the heading says so.
        (_, found) = await Call(web, "POST", "/api/market/find/sector", """{ "name": "Wregoe YV-W", "inhabited": true }""");
        Assert.Equal("Systems of Wregoe YV-W where people live", found.GetProperty("what").GetString());
        Assert.Contains("\"population\":{\"comparison\":\"<=>\",\"value\":[1,1000000000000]}", net.Asked[^1]);

        // Nothing that starts so, and no name at all.
        (_, found) = await Call(web, "POST", "/api/market/find/sector", """{ "name": "Nowhere" }""");
        Assert.Equal("No system whose name starts with \"Nowhere\" is known. Check its spelling, or type less of it.", found.GetProperty("note").GetString());
        Assert.Equal((400, "Type a sector's name, or the start of one: Wregoe, Col 285 Sector."), await Refused("sector", """{ "name": " " }"""));
    }

    [Fact]
    public async Task Fleet_carriers_that_do_not_let_everyone_dock_are_left_out_unless_any_is_asked_for()
    {
        // Spansh knows who may dock at the two carriers that sell gold near Sol: everyone at one, its owner's squadron at the other.
        const string carriers = """{"count":2,"results":[{"name":"G2M-BQQ","type":"Drake-Class Carrier","carrier_docking_access":"All"},{"name":"H1M-21L","type":"Drake-Class Carrier","carrier_docking_access":"Squadron"}]}""";
        net.Instead = asked => asked.StartsWith(Spansh + "/search ", StringComparison.Ordinal) && asked.Contains("\"name\":{\"value\":[\"G2M-BQQ\"", StringComparison.Ordinal) ? carriers : null;
        static string[] Carriers(JsonElement answer) =>
            [.. Rows(answer).Where(row => row.GetProperty("type").GetString() == "Drake-Class Carrier").Select(row => $"{row.GetProperty("station").GetString()}: {row.GetProperty("access").GetString()}")];

        // As the page asks unless told otherwise: the carrier open to all stays and says so, the other goes, and the line says one went.
        JsonElement found = await Find("commodity", """{ "name": "Gold", "filters": { "carriers": true } }""");
        Assert.Equal(["G2M-BQQ: open to all"], Carriers(found));
        Assert.Equal("1 fleet carrier that does not let everyone dock was left out.", found.GetProperty("note").GetString());

        // Any: both, each with who may dock there. The same carriers are not asked about twice.
        int asked = net.Asked.Count;
        found = await Find("commodity", """{ "name": "Gold", "docking": "any", "filters": { "carriers": true } }""");
        Assert.Equal(["G2M-BQQ: open to all", "H1M-21L: squadron only"], Carriers(found));
        Assert.Equal(JsonValueKind.Null, found.GetProperty("note").ValueKind);
        Assert.Equal(asked, net.Asked.Count);

        // A station that is no carrier has no such thing to say, and without carriers Spansh is not asked at all.
        found = await Find("commodity", """{ "name": "Gold" }""");
        Assert.All(Rows(found), row => Assert.Equal("", row.GetProperty("access").GetString()));
        Assert.DoesNotContain(net.Asked.Skip(asked), question => question.StartsWith(Spansh, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_short_list_says_how_many_more_the_price_age_and_the_carriers_filter_leave_out()
    {
        // A list that is long enough asks nothing more: gold near Sol, as recorded.
        Assert.Equal(42, Rows(await Find("commodity", """{ "name": "Gold" }""")).Length);
        Assert.DoesNotContain(net.Asked, question => question.Contains("maxDaysAgo=30", StringComparison.Ordinal));

        // Made-up sellers of silver, each a light year farther from Sol: fresh or days old, a starport or a fleet carrier.
        string Seller(int id, string type, double hoursOld) => Invariant(
            $$"""{"commodityName":"silver","marketId":{{id}},"stationName":"Seller {{id}}","stationType":"{{type}}","distanceToArrival":100,"maxLandingPadSize":3,"systemName":"Far {{id}}","systemX":{{id}},"systemY":0,"systemZ":0,"buyPrice":45000,"stock":500,"sellPrice":0,"demand":0,"meanPrice":47000,"updatedAt":"{{clock.AddHours(-hoursOld):yyyy-MM-ddTHH:mm:ss.fffZ}}","distance":{{id}}}""");
        string fresh = "[" + Seller(1, "Coriolis", 1) + "]";
        // Thirty days of prices, carriers and all: the fresh one again, three starports with old prices (one of them with
        // small pads only, which the pad filter would leave out anyway), and two carriers, one fresh and one old.
        string wide = "[" + string.Join(",", Seller(1, "Coriolis", 1), Seller(2, "Coriolis", 200), Seller(3, "Orbis", 300),
            Seller(4, "Outpost", 300).Replace("\"maxLandingPadSize\":3", "\"maxLandingPadSize\":1"), Seller(5, "FleetCarrier", 2), Seller(6, "FleetCarrier", 400)) + "]";
        string around = GoldAroundSol.Replace("/gold/", "/silver/"), inSol = GoldInSol.Replace("/gold?", "/silver?");
        const string wideQuestion = Ardent + "/system/name/Sol/commodity/name/silver/nearby/exports?maxDistance=500&maxDaysAgo=30&minVolume=100&sort=distance";
        net.Instead = asked => asked == around ? fresh : asked == wideQuestion ? wide : asked.StartsWith(inSol[..^1], StringComparison.Ordinal) ? "[]" : null;

        JsonElement found = await Find("commodity", """{ "name": "Silver" }""");
        Assert.Equal(["Seller 1"], Stations(found));
        Assert.Equal("Your filters leave out 2 more with prices up to 30 days old and 2 more on fleet carriers. Change the price age or the carriers filter to see them.",
            found.GetProperty("note").GetString());

        // With carriers in and thirty days of prices allowed there is nothing the two filters could leave out, so nothing more is asked.
        int asked = net.Asked.Count(question => question == wideQuestion);
        await Find("commodity", """{ "name": "Silver", "docking": "any", "filters": { "carriers": true, "maxAgeHours": 720 } }""");
        Assert.Equal(asked, net.Asked.Count(question => question == wideQuestion));
    }

    [Fact]
    public async Task From_another_system_the_service_is_asked_where_it_is_once()
    {
        const string alpha = Ardent + "/system/name/Alpha%20Centauri";

        JsonElement found = await Find("commodity", """{ "name": "Gold", "from": " Alpha Centauri " }""");

        Assert.Equal("Alpha Centauri", found.GetProperty("from").GetString());
        Assert.Equal([Ardent + "/commodities", alpha, alpha + GoldAroundSol[(Ardent + "/system/name/Sol").Length..], alpha + "/commodity/name/gold?maxDaysAgo=2"], Asked());
        // Sol is 4.38 ly from Alpha Centauri, and Barnard's Star 6.49.
        Assert.Equal(37, Rows(found).Length);
        Assert.Equal("Galileo | Sol | 4.38 | 503.478445 | L | Ocellus Starport |  | 45099 | 39819 | 2026-10-09T08:32:05Z", Line(Rows(found)[0]));
        Assert.Equal("Miller Depot | Barnard's Star | 6.49 | 37.254078 | L | Orbis Starport |  | 45229 | 112 | 2026-10-09T03:27:32Z", Line(Rows(found)[5]));
        NearestFirst(found);

        // Twenty minutes on the offers are asked for again, but a system does not move.
        clock = clock.AddMinutes(20);
        await Find("commodity", """{ "name": "Gold", "from": "Alpha Centauri" }""");
        Assert.Equal(6, net.Asked.Count);
        Assert.DoesNotContain(alpha, net.Asked.Skip(4));
    }

    [Fact]
    public async Task The_same_question_within_minutes_gets_the_remembered_answer()
    {
        JsonElement first = await Find("commodity", """{ "name": "Gold" }""");
        clock = clock.AddMinutes(4);
        JsonElement again = await Find("commodity", """{ "name": "Gold" }""");

        Assert.Equal(3, net.Asked.Count);
        Assert.Equal(first.GetProperty("rows").GetRawText(), again.GetProperty("rows").GetRawText());
        Assert.Equal(RecordedServices.RecordedUtc, again.GetProperty("askedUtc").GetDateTime());   // and it says how old it is

        clock = clock.AddMinutes(2);   // six minutes: prices may have moved
        again = await Find("commodity", """{ "name": "Gold" }""");
        Assert.Equal(5, net.Asked.Count);
        Assert.Equal(clock, again.GetProperty("askedUtc").GetDateTime());

        // Spansh's stations change more slowly: ten minutes.
        await Find("services", """{ "name": "Shipyard" }""");
        clock = clock.AddMinutes(9);
        await Find("services", """{ "name": "Shipyard" }""");
        Assert.Equal(6, net.Asked.Count);
    }

    [Fact]
    public async Task The_trade_search_and_find_by_name_share_one_memory_of_answers()
    {
        // In the app both ask through one client. What Sol's own stations pay for gold is one question to the two of
        // them, in the very same words: whichever asks second is given what the first one fetched, and can read it.
        var search = new TradeSearch(data, () => clock);
        var whereToSell = new SearchRequest { Mode = "lookup", StarSystem = "Sol", Pad = 2, LookItem = "gold", LookCount = 100, LookFrom = "Your cargo hold" };
        const string aroundSol = Ardent + "/system/name/Sol/commodity/name/gold/nearby/imports?maxDistance=30&maxDaysAgo=2&minVolume=1&fleetCarriers=false";
        net.Instead = asked => asked == aroundSol ? "[]" : null;   // the lookup's own question was not recorded: "no offers"
        static string[] Buyers(SearchResult places) => [.. places.Trades.Select(place => $"{place.SellStation} in {place.SellSystem} pays {place.SellPrice}")];

        JsonElement found = await Find("commodity", """{ "name": "Gold", "side": "sell" }""");
        SearchResult places = await search.Run(whereToSell, TestContext.Current.CancellationToken);

        Assert.True(places.Ok, places.Error);
        Assert.Equal([aroundSol], places.Urls);                     // Sol's own stations were not asked about again
        Assert.Single(net.Asked, asked => asked == GoldInSol);
        Assert.Contains("Abraham Lincoln in Sol pays 50945", Buyers(places));
        Assert.All(places.Trades, place => Assert.Equal("Sol", place.SellSystem));

        // The other way round, with everything forgotten meanwhile: the trade search fetches it, and Find is given it.
        clock = clock.AddMinutes(20);
        SearchResult later = await search.Run(whereToSell, TestContext.Current.CancellationToken);
        JsonElement again = await Find("commodity", """{ "name": "Gold", "side": "sell" }""");

        Assert.Equal([aroundSol, GoldInSol], later.Urls);
        Assert.Equal(2, net.Asked.Count(asked => asked == GoldInSol));
        Assert.Equal(Buyers(places), Buyers(later));
        Assert.Equal(Rows(found).Select(Line), Rows(again).Select(Line));
        Assert.Equal(32, Rows(again).Length);
    }

    // ---------------------------------------------------------------- modules, ships and services, from Spansh

    [Fact]
    public async Task A_module_lists_the_nearest_stations_that_sell_that_class_and_rating()
    {
        JsonElement found = await Find("module", """{ "name": "frame shift drive", "class": "5", "rating": "a" }""");

        Assert.Equal(("Stations that sell the 5A Frame Shift Drive", "Sol", "Spansh"),
            (found.GetProperty("what").GetString(), found.GetProperty("from").GetString(), found.GetProperty("source").GetString()));
        // The list of names first, to spell the module as Spansh does; then one question, in the shape Spansh's own
        // site uses. Sol is asked for by where it lies, because it is the system the player is in.
        Assert.Equal(
        [
            Spansh + "/field_values/modules",
            Spansh + """/search {"filters":{"modules":[{"name":{"value":["Frame Shift Drive"]},"class":{"value":["5"]},"rating":{"value":["A"]}}],""" + NearSol,
        ], net.Asked);

        // The price is that of the 5A drive alone, and the age that of the station's outfitting list.
        Assert.Equal(
        [
            "Columbus | Sol | 0 | 2616.175016 | L | Ocellus Starport | 5A | 5103950 | - | 2026-10-09T08:40:36Z",
            "Abraham Lincoln | Sol | 0 | 504.04391 | L | Orbis Starport | 5A | 5103950 | - | 2026-10-09T08:36:40Z",
            "Titan City | Sol | 0 | 4717.541493 | L | Orbis Starport | 5A | 5103950 | - | 2026-10-09T08:55:10Z",
            "Stronghold Carrier | Alpha Centauri | 4.38 | 4466.324552 | L | Stronghold Carrier | 5A | 5103950 | - | 2026-10-09T10:31:00Z",
            "Levi-Strauss Installation | Barnard's Star | 5.95 | 6.498147 | M | Outpost | 5A | 5103950 | - | 2026-10-08T02:01:22Z",
            "Yamazaki Landing | WISE 0855-0714 | 7.17 | 944.355172 | M | Outpost | 5A | 5103950 | - | 2026-10-07T21:07:04Z",
            "Stronghold Carrier | Sirius | 8.59 | 952.816674 | L | Stronghold Carrier | 5A | 5103950 | - | 2026-10-09T06:18:56Z",
            "Patterson Enterprise | Sirius | 8.59 | 953.006972 | L | Coriolis Starport | 5A | 5103950 | - | 2026-10-09T09:03:53Z",
        ], Rows(found).Select(Line));
        Assert.Equal("The nearest 8 of more than 10,000 stations.", found.GetProperty("note").GetString());
        // Spansh keeps every search under a name of its own, and its site shows the search under that name.
        Assert.Equal("https://spansh.co.uk/stations/search/52EF002A-C3DC-11F1-AE1B-B4FA6439EAB6/1", found.GetProperty("link").GetString());
    }

    [Fact]
    public async Task A_module_with_no_class_or_rating_picked_names_what_each_station_sells()
    {
        JsonElement found = await Find("module", """{ "name": "Frame Shift Drive", "class": "" }""");

        Assert.Equal("Stations that sell the Frame Shift Drive", found.GetProperty("what").GetString());
        // "Any" (the page sends "", or nothing) is said by leaving class and rating out of the question.
        Assert.Equal(Spansh + """/search {"filters":{"modules":[{"name":{"value":["Frame Shift Drive"]}}],""" + NearSol, net.Asked[1]);
        // Every station sells the drive in eight sizes and grades or more, each at its own price: no one price to show.
        Assert.Equal("Ehrlich City | Sol | 0 | 232.401431 | L | Planetary Port | 9 kinds | - | - | 2026-10-08T21:28:39Z", Line(Rows(found)[0]));
        Assert.Equal("Burnell Station | Sol | 0 | 362.91813 | M | Outpost | 10 kinds | - | - | 2026-10-09T08:08:53Z", Line(Rows(found)[4]));
        Assert.Equal(8, Rows(found).Length);
        Assert.Equal("The nearest 8 of more than 10,000 stations. Pick a class and a rating to see one price for each station.", found.GetProperty("note").GetString());
    }

    [Fact]
    public async Task A_ship_lists_the_nearest_stations_that_sell_it()
    {
        // EDSM, which each station is checked against, was not recorded: unasked, it has no list for any of them.
        net.Instead = asked => asked.StartsWith(Edsm, StringComparison.Ordinal) ? "{}" : null;
        JsonElement found = await Find("ship", """{ "name": "python mkii" }""");

        Assert.Equal("Stations that sell the Python MkII", found.GetProperty("what").GetString());
        Assert.Equal([Spansh + "/field_values/ships", Spansh + """/search {"filters":{"ships":[{"name":{"value":["Python MkII"]}}],""" + NearSol],
            net.Asked.Where(asked => asked.StartsWith(Spansh, StringComparison.Ordinal)));
        // The age is that of the station's shipyard list.
        Assert.Equal("Furukawa Enterprise | Sol | 0 | 232.382846 | L | Planetary Outpost |  | 67527360 | - | 2026-10-08T22:41:00Z", Line(Rows(found)[0]));
        Assert.Equal(["Furukawa Enterprise", "Galileo", "Mars High", "Daedalus", "M.Gorbachev", "Li Qing Jao", "Columbus", "Abraham Lincoln"], Stations(found));
        Assert.All(Rows(found), row => Assert.Equal(67527360, row.GetProperty("price").GetInt64()));
        Assert.All(Rows(found), row => Assert.Equal("no list", row.GetProperty("edsm").GetString()));
        Assert.EndsWith("EDSM has no shipyard list for 8 of them, so those may not have it.", found.GetProperty("note").GetString());
    }

    [Fact]
    public async Task A_ship_search_leaves_out_the_stations_EDSM_says_do_not_sell_it()
    {
        // Spansh's shipyard lists name ships a station is not selling right now (the owner, 16 October 2026: three stations in
        // Wolf 397 "sold" a Corsair that none of them had). EDSM keeps the ships on sale only. Here it lists the Python MkII at
        // Galileo by the game's number for it, and at Daedalus by its name in its own spelling; Mars High and Columbus sell other
        // ships only; it knows nothing of the rest, and does not answer for Abraham Lincoln.
        net.Instead = asked => !asked.StartsWith(Edsm, StringComparison.Ordinal) ? null
            : asked.Contains("stationName=Galileo", StringComparison.Ordinal) ? """{"name":"Sol","sName":"Galileo","ships":[{"id":129030464,"name":"Python MkII"},{"id":128049249,"name":"Sidewinder"}]}"""
            : asked.Contains("stationName=Daedalus", StringComparison.Ordinal) ? """{"name":"Sol","sName":"Daedalus","ships":[{"name":"Python Mk II"}]}"""
            : asked.Contains("stationName=Mars%20High", StringComparison.Ordinal) || asked.Contains("stationName=Columbus", StringComparison.Ordinal)
                ? """{"name":"Sol","ships":[{"id":128049249,"name":"Sidewinder"},{"id":128049339,"name":"Python"}]}"""
            : asked.Contains("stationName=Abraham%20Lincoln", StringComparison.Ordinal) ? "not json"
            : "{}";

        JsonElement found = await Find("ship", """{ "name": "Python MkII" }""");

        // Asked by the station's name and its system's, for the recorded stations come without their market numbers.
        Assert.Contains(Edsm + "?systemName=Sol&stationName=Mars%20High", net.Asked);
        Assert.Equal(["Furukawa Enterprise: no list", "Galileo: confirmed", "Daedalus: confirmed", "M.Gorbachev: no list", "Li Qing Jao: no list", "Abraham Lincoln: not checked"],
            Rows(found).Select(row => $"{row.GetProperty("station").GetString()}: {row.GetProperty("edsm").GetString()}"));
        Assert.Equal("The nearest 6 of more than 10,000 stations. 2 stations Spansh lists were left out: EDSM says their shipyards do not sell it right now. "
            + "EDSM has no shipyard list for 3 of them, so those may not have it. 1 was not checked with EDSM and may well not have it.",
            found.GetProperty("note").GetString());

        // The same search minutes later asks EDSM nothing again.
        int asked = net.Asked.Count(one => one.StartsWith(Edsm, StringComparison.Ordinal));
        clock = clock.AddMinutes(3);
        await Find("ship", """{ "name": "Python MkII" }""");
        Assert.Equal(asked + 1, net.Asked.Count(one => one.StartsWith(Edsm, StringComparison.Ordinal)));   // only Abraham Lincoln's, which had failed
    }

    [Fact]
    public async Task EDSM_is_left_alone_for_ten_minutes_when_it_asks_Studio_to_slow_down()
    {
        // EDSM turns a PC away after too many questions. Its first answer here is that refusal: nothing more is asked of it.
        net.Instead = asked => asked.StartsWith(Edsm, StringComparison.Ordinal) ? "{}" : null;
        net.Refuse = asked => asked.StartsWith(Edsm, StringComparison.Ordinal) ? HttpStatusCode.TooManyRequests : null;
        JsonElement found = await Find("ship", """{ "name": "Python MkII" }""");
        Assert.Single(net.Asked, asked => asked.StartsWith(Edsm, StringComparison.Ordinal));
        Assert.Equal(8, Rows(found).Length);   // every station Spansh sent, none of them checked
        Assert.All(Rows(found), row => Assert.Equal("not checked", row.GetProperty("edsm").GetString()));
        Assert.EndsWith("8 were not checked with EDSM and may well not have it. EDSM asked Studio to slow down, so it is asked again in a few minutes.",
            found.GetProperty("note").GetString());

        // Nine minutes on, another ship (another question to Spansh) still asks EDSM nothing; eleven minutes on, it is asked again.
        net.Refuse = null;
        clock = clock.AddMinutes(9);
        await Find("ship", """{ "name": "Python MkII" }""");
        Assert.Single(net.Asked, asked => asked.StartsWith(Edsm, StringComparison.Ordinal));
        clock = clock.AddMinutes(2);
        await Find("ship", """{ "name": "Python MkII" }""");
        Assert.Equal(9, net.Asked.Count(asked => asked.StartsWith(Edsm, StringComparison.Ordinal)));
    }

    [Theory]
    // EDSM knows none of the 30: it is asked about the nearest 25, and Spansh for no second page.
    [InlineData("{}", 25, "no list", 25)]
    // EDSM says all 30 sell it: five make a list, and the rest are not asked about.
    [InlineData("""{"ships":[{"id":129030464,"name":"Python MkII"}]}""", 5, "confirmed", 5)]
    public async Task A_ship_search_asks_EDSM_about_25_stations_at_most_and_stops_at_5_that_sell_it(string edsmAnswer, int askedEdsm, string nearest, int checkedRows)
    {
        // Thirty stations that sell the ship, as Spansh sends them on its first page (it was asked for 20, but its answer counts).
        string stations = string.Join(",", Enumerable.Range(1, 30).Select(n => Invariant(
            $$"""{"name":"Port {{n}}","system_name":"Sol","market_id":{{n}},"type":"Orbis Starport","distance":0,"distance_to_arrival":{{n}},"large_pads":1,"ships":[{"name":"Python MkII","price":1,"symbol":"Python_NX"}]}""")));
        net.Instead = asked => asked.StartsWith(Edsm, StringComparison.Ordinal) ? edsmAnswer
            : asked.StartsWith(Spansh + "/search", StringComparison.Ordinal) ? $$"""{"count":90,"results":[{{stations}}]}""" : null;
        JsonElement found = await Find("ship", """{ "name": "Python MkII" }""");
        Assert.Equal([.. Enumerable.Range(1, askedEdsm).Select(n => Invariant($"{Edsm}?marketId={n}"))], net.Asked.Where(asked => asked.StartsWith(Edsm, StringComparison.Ordinal)));
        Assert.Single(net.Asked, asked => asked.StartsWith(Spansh + "/search", StringComparison.Ordinal));
        Assert.Equal(30, Rows(found).Length);
        Assert.Equal([.. Enumerable.Repeat(nearest, checkedRows), .. Enumerable.Repeat("not checked", 30 - checkedRows)], Rows(found).Select(row => row.GetProperty("edsm").GetString()));
    }

    [Theory]
    [InlineData("Raw material trader", "material_trader", """{"value":["Raw"]}""", 338, "Broglie Terminal | 61 Cygni | 11.37", 5)]
    [InlineData("Manufactured material trader", "material_trader", """{"value":["Manufactured"]}""", 721, "Patterson Enterprise | Sirius | 8.59", 5)]
    [InlineData("Encoded material trader", "material_trader", """{"value":["Encoded"]}""", 402, "Magnus Gateway | EZ Aquarii | 11.1", 5)]
    // The human brokers are asked for as any broker: of Spansh's ten nearest, five are Guardian by BrokerKind.
    [InlineData("Human technology broker", "services", """[{"name":{"value":["Technology Broker"]}}]""", 7960, "Dobrovolskiy Enterprise | WISE 1506+7027 | 10.52", 5)]
    [InlineData("Guardian technology broker", "technology_broker", """{"value":["Guardian"]}""", 780, "Magnus Gateway | EZ Aquarii | 11.1", 5)]
    // Of the five nearest with interstellar factors one settlement has only a small pad, and of those with a black
    // market one settlement likewise and one ship has no known pads at all: no place for a Type-8.
    [InlineData("Interstellar factors", "services", """[{"name":{"value":["Interstellar Factors Contact"]}}]""", 10000, "Mohanty Drilling Base | Barnard's Star | 5.95", 4)]
    [InlineData("Black market", "services", """[{"name":{"value":["Black Market"]}}]""", 10000, "Markus Extraction Exchange | Alpha Centauri | 4.38", 3)]
    [InlineData("Shipyard", "services", """[{"name":{"value":["Shipyard"]}}]""", 10000, "Ehrlich City | Sol | 0", 5)]
    [InlineData("Outfitting", "services", """[{"name":{"value":["Outfitting"]}}]""", 10000, "Ehrlich City | Sol | 0", 5)]
    [InlineData("Repair", "services", """[{"name":{"value":["Repair"]}}]""", 10000, "Ehrlich City | Sol | 0", 5)]
    public async Task Every_service_has_its_own_question_and_lists_the_nearest_stations(string name, string field, string value, int known, string nearest, int listed)
    {
        JsonElement found = await Find("services", JsonSerializer.Serialize(new { name }));

        Assert.Equal(("Nearest: " + name, "Spansh"), (found.GetProperty("what").GetString(), found.GetProperty("source").GetString()));
        // One question and nothing else: the kinds of trader and of broker have filters of their own.
        Assert.Equal([Spansh + "/search {\"filters\":{\"" + field + "\":" + value + "," + NearSol.Replace("\"size\":20", "\"size\":10")], net.Asked);
        Assert.StartsWith(nearest + " | ", Line(Rows(found)[0]));
        Assert.Equal(listed, Rows(found).Length);
        NearestFirst(found);
        // A broker's kind is a guess, which the answer says. (The recording leaves out the economies it rests on: the next test has them.)
        bool broker = name.EndsWith("technology broker", StringComparison.Ordinal);
        Assert.Equal($"The nearest {listed} of {(known == 10000 ? "more than 10,000" : known.ToString("N0", CultureInfo.InvariantCulture))} stations." + (broker ? " " + BrokerWarning : "") + FarNote,
            found.GetProperty("note").GetString());
        Assert.All(Rows(found), row => Assert.Contains(row.GetProperty("broker").GetString(), broker ? (string[])["a guess", "a colony's station"] : [""]));
        Assert.All(Rows(found), row => Assert.Contains(row.GetProperty("pad").GetString(), (string[])["M", "L"]));
    }

    const string BrokerWarning = "No service records which kind of technology broker a station has. Studio goes by the station, as Inara does: a colony's station has a human broker, "
        + "and any other station a Guardian one when its main economy is High Tech and a human one when it is not. A few stations are not like that. "
        + "Once you have seen a station's broker, mark its kind, and Studio goes by that.";

    // Spansh leaves out the stations far from their star itself, so a service search says the filter did (the owner, 21 October 2026:
    // Kerwin Horizons, 18,751 ls out, was missing from a Guardian broker search).
    const string FarNote = " Stations more than 5,000 ls from their star are left out, as your filters ask.";

    const string ColonyLeftOut = " 1 station that Spansh calls Guardian was left out: a colony's station has a human broker, and a megaship's kind is not known.";

    /// <summary>The part of a question that asks Spansh for any technology broker: the human search's.</summary>
    const string AnyBroker = "\"services\":[{\"name\":{\"value\":[\"Technology Broker\"]}}]";

    // Spansh's nearest "Guardian" brokers, cut down. The first two are the owner's: they docked at both (15 and 20 October
    // 2026) for a Guardian broker and found a human one. Spansh's kind is the main economy in other words: High Tech is
    // "Guardian". The third is a colony's station, whose parts need not add up to a hundred, and so has a human broker
    // (BrokerKind); the fourth has one economy.
    const string OwnersBrokers = """
            {"count":4,"results":[
              {"name":"Ray Gateway","system_name":"Diaguandri","type":"Coriolis Starport","distance":3.5,"distance_to_arrival":475.75,"large_pads":4,"market_id":3223343616,
               "technology_broker":"Guardian","material_trader":"Encoded","primary_economy":"High Tech","economies":[{"name":"High Tech","share":80.0},{"name":"Refinery","share":20.0}],"updated_at":"2026-10-20T21:29:43Z",
               "services":[{"name":"Technology Broker"}]},
              {"name":"Soukup City","system_name":"Latuba","type":"Coriolis Starport","distance":4.25,"distance_to_arrival":1210.5,"large_pads":4,"market_id":3224837632,
               "technology_broker":"Guardian","material_trader":"Encoded","primary_economy":"High Tech","economies":[{"name":"High Tech","share":80.0},{"name":"Refinery","share":20.0}],"updated_at":"2026-10-20T20:36:00Z",
               "services":[{"name":"Technology Broker"}]},
              {"name":"Colony Port","system_name":"Col 285 Sector AA-A a1-1","type":"Coriolis Starport","distance":6,"distance_to_arrival":12,"large_pads":2,"market_id":3,
               "technology_broker":"Guardian","primary_economy":"High Tech","economies":[{"name":"High Tech","share":140.0},{"name":"Industrial","share":60.0}],"updated_at":"2026-10-19T10:00:00Z",
               "system_is_colonised":true,"services":[{"name":"Technology Broker"}]},
              {"name":"Magnus Gateway","system_name":"EZ Aquarii","type":"Orbis Starport","distance":11.1,"distance_to_arrival":832,"large_pads":4,"market_id":4,
               "technology_broker":"Guardian","primary_economy":"High Tech","economies":[{"name":"High Tech","share":100.0}],"updated_at":"2026-10-20T21:15:09Z",
               "services":[{"name":"Technology Broker"}]}]}
            """;

    /// <summary>Spansh's answer to a question for the Guardian brokers; null for any other question.</summary>
    static string? GuardianBrokers(string asked) => asked.Contains("\"technology_broker\":{\"value\":[\"Guardian\"]}", StringComparison.Ordinal) ? OwnersBrokers : null;

    /// <summary>Marks a station's broker as the page does; the answer's status and the kind it is marked with now.</summary>
    async Task<(int Status, string Kind)> Mark(long marketId, string station, string kind)
    {
        var (status, answer) = await Call(web, "POST", "/api/market/find/broker", JsonSerializer.Serialize(new { marketId, station, system = "Sol", kind }));
        return (status, status == 200 ? answer.GetProperty("kind").GetString()! : answer.GetProperty("error").GetString()!);
    }

    static string[] Brokers(JsonElement answer) =>
        [.. Rows(answer).Select(row => $"{row.GetProperty("station").GetString()}: {row.GetProperty("brokerSeen").GetString()} | {row.GetProperty("broker").GetString()}")];

    [Fact]
    public async Task A_brokers_kind_is_called_a_guess_from_the_economy_it_rests_on()
    {
        string stations = OwnersBrokers;
        net.Instead = GuardianBrokers;
        JsonElement found = await Find("services", """{ "name": "Guardian technology broker" }""");

        Assert.Equal("Nearest: Guardian technology broker", found.GetProperty("what").GetString());
        Assert.Equal(["Ray Gateway: a guess from its High Tech economy (80%)", "Soukup City: a guess from its High Tech economy (80%)",
                "Magnus Gateway: a guess from its High Tech economy"],
            Rows(found).Select(row => $"{row.GetProperty("station").GetString()}: {row.GetProperty("broker").GetString()}"));
        // All four fit on Spansh's one page, so there is no count. The colony's station is left out.
        Assert.Equal(BrokerWarning + ColonyLeftOut + FarNote, found.GetProperty("note").GetString());

        // A human broker's kind rests on its economy just the same, but a colony's station is human whatever its economy;
        // the other services have no kind to guess.
        net.Instead = asked => asked.Contains(AnyBroker, StringComparison.Ordinal)
            ? stations.Replace("\"Guardian\"", "\"Human\"", StringComparison.Ordinal).Replace("High Tech", "Industrial", StringComparison.Ordinal) : null;
        found = await Find("services", """{ "name": "Human technology broker" }""");
        Assert.Equal(["a guess from its Industrial economy (80%)", "a guess from its Industrial economy (80%)", "a colony's station", "a guess from its Industrial economy"],
            Rows(found).Select(row => row.GetProperty("broker").GetString()));
        net.Instead = null;
        JsonElement traders = await Find("services", """{ "name": "Encoded material trader" }""");
        Assert.All(Rows(traders), row => Assert.Equal("", row.GetProperty("broker").GetString()));
        Assert.DoesNotContain("technology broker", traders.GetProperty("note").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_colonys_station_has_a_human_broker_and_a_megaship_Spansh_calls_Guardian_is_neither()
    {
        // Dodectech, a colony's High Tech station that Spansh calls Guardian: the owner found a human broker there, and
        // Inara calls it human (21 October 2026). The human search asks for any broker and goes by the station.
        net.Instead = asked => asked.Contains(AnyBroker, StringComparison.Ordinal) ? OwnersBrokers : GuardianBrokers(asked);
        Assert.Equal(["Colony Port:  | a colony's station"], Brokers(await Find("services", """{ "name": "Human technology broker" }""")));
        Assert.DoesNotContain("Colony Port", Stations(await Find("services", """{ "name": "Guardian technology broker" }""")));

        // Inara lists a megaship Spansh calls Guardian under neither kind.
        const string megaship = """
            {"count":1,"results":[{"name":"Spirit of Laelaps","system_name":"Sol","type":"Mega ship","distance":43.4,"distance_to_arrival":10,"large_pads":1,"market_id":5,
              "technology_broker":"Guardian","services":[{"name":"Technology Broker"}]}]}
            """;
        net.Instead = _ => megaship;
        clock = clock.AddMinutes(11);   // Spansh's answers from before are forgotten
        JsonElement guardian = await Find("services", """{ "name": "Guardian technology broker" }""");
        Assert.Empty(Rows(guardian));
        Assert.Contains(ColonyLeftOut.Trim(), guardian.GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.Empty(Rows(await Find("services", """{ "name": "Human technology broker" }""")));
    }

    [Fact]
    public async Task A_station_in_a_system_that_needs_a_permit_says_so()
    {
        // Prospect's Deep in Mbooni, the one broker for the modified Guardian weapons, is behind the Mbooni permit. Spansh's
        // answer, cut down from its own of 21 October 2026, does not say so.
        const string mbooni = """
            {"count":2,"results":[
              {"name":"Chretien Terminal","system_name":"LHS 1197","type":"Coriolis Starport","distance":4.1,"distance_to_arrival":429,"large_pads":4,"market_id":6,
               "technology_broker":"Guardian","primary_economy":"High Tech","updated_at":"2026-10-20T10:00:00Z","services":[{"name":"Technology Broker"}]},
              {"name":"Prospect's Deep","system_name":"Mbooni","type":"Planetary Outpost","distance":11.26,"distance_to_arrival":1491.56,"large_pads":2,"market_id":128988470,
               "technology_broker":"Guardian","primary_economy":"High Tech","updated_at":"2026-10-20T15:31:06Z","services":[{"name":"Technology Broker"}]}]}
            """;
        net.Instead = asked => GuardianBrokers(asked) is null ? null : mbooni;
        JsonElement found = await Find("services", """{ "name": "Guardian technology broker" }""");

        Assert.Equal(["Chretien Terminal: ", "Prospect's Deep: the Mbooni permit"],
            Rows(found).Select(row => $"{row.GetProperty("station").GetString()}: {row.GetProperty("permit").GetString()}"));
        Assert.Equal("Mbooni needs the Mbooni permit: become Allied with Azimuth Biotech in LHS 1163, then take their \"Permit Acquisition Opportunity\" mission "
            + "(Studio Loadouts, Guardian, has the steps). " + BrokerWarning + FarNote, found.GetProperty("note").GetString());
    }

    [Fact]
    public async Task A_station_sheet_calls_a_brokers_kind_probable()
    {
        // Two stations of the name: the owner's Ray Gateway, which Spansh calls Guardian for its High Tech economy, and a
        // colony's station with a broker and an Industrial and Extraction economy, of which Spansh gives no kind at all:
        // a colony's station has a human broker (BrokerKind).
        const string namesakes = """
            {"count":2,"results":[
              {"name":"Ray Gateway","system_name":"Diaguandri","type":"Coriolis Starport","distance":3.5,"market_id":3223343616,"market_updated_at":"2026-10-20T21:28:27Z",
               "technology_broker":"Guardian","material_trader":"Encoded","services":[{"name":"Dock"},{"name":"Material Trader"},{"name":"Technology Broker"}],
               "market":[{"commodity":"Gold","buy_price":45000,"sell_price":44000,"supply":10,"demand":0}]},
              {"name":"Ray Gateway","system_name":"Col 285 Sector DR-V d2-53","type":"Outpost","distance":40.2,"market_id":7,"market_updated_at":"2025-10-12T10:22:44Z","system_is_colonised":true,
               "services":[{"name":"Dock"},{"name":"Technology Broker"}],
               "market":[{"commodity":"Gold","buy_price":45000,"sell_price":44000,"supply":10,"demand":0}]}]}
            """;
        // (Find by name asks for the name as it was typed, a station's own sheet for the exact name.)
        net.Instead = asked => asked.StartsWith(Spansh + "/search ", StringComparison.Ordinal) && asked.Contains("\"Ray Gateway\"", StringComparison.Ordinal) ? namesakes : null;
        await Find("station", """{ "name": "Ray Gateway" }""");

        async Task<string[]> Services(long id)
        {
            var (status, prices) = await Call(web, "GET", Invariant($"/api/market/find/prices?id={id}"));
            Assert.Equal(200, status);
            return Texts(prices.GetProperty("details").GetProperty("services"));
        }
        Assert.Equal(["Dock", "Material Trader (Encoded)", "Technology Broker (probably Guardian)"], await Services(3223343616));
        Assert.Equal(["Dock", "Technology Broker (probably Human)"], await Services(7));

        // Marked as what the owner saw there, the sheet says so, also on its way from the station's own search (Inspect).
        Assert.Equal((200, "Human"), await Mark(3223343616, "Ray Gateway", "Human"));
        Assert.Equal(["Dock", "Material Trader (Encoded)", "Technology Broker (Human, you saw it)"], await Services(3223343616));
        var (_, sheet) = await Call(web, "GET", "/api/market/inspect/station?name=Ray%20Gateway&system=Diaguandri");
        Assert.Equal((3223343616, "Human"), (sheet.GetProperty("marketId").GetInt64(), sheet.GetProperty("brokerSeen").GetString()));
        Assert.Contains("Technology Broker (Human, you saw it)", Texts(sheet.GetProperty("services")));
        // The kind the player saw goes before the guess; taken away, the guess is back.
        Assert.Equal((200, "Guardian"), await Mark(7, "Ray Gateway", "Guardian"));
        Assert.Equal(["Dock", "Technology Broker (Guardian, you saw it)"], await Services(7));
        Assert.Equal((200, ""), await Mark(7, "Ray Gateway", ""));
        Assert.Equal(["Dock", "Technology Broker (probably Human)"], await Services(7));
    }

    [Fact]
    public async Task A_broker_the_player_saw_is_kept_for_good_and_every_broker_search_goes_by_it()
    {
        // The owner saw human brokers at Ray Gateway and Soukup City, and a Guardian one at Magnus Gateway. The kind is
        // spelt as Spansh does, whatever the page sent.
        Assert.Equal((200, "Human"), await Mark(3223343616, "Ray Gateway", "human"));
        Assert.Equal((200, "Human"), await Mark(3224837632, "Soukup City", "Human"));
        Assert.Equal((200, "Guardian"), await Mark(4, "Magnus Gateway", "Guardian"));

        // The Guardian search leaves out the two human ones and says so; the one the owner saw says that too.
        net.Instead = GuardianBrokers;
        JsonElement found = await Find("services", """{ "name": "Guardian technology broker" }""");
        Assert.Equal(["Magnus Gateway: Guardian | a guess from its High Tech economy"], Brokers(found));
        Assert.Equal(BrokerWarning + " 2 stations where you saw a Human broker were left out." + ColonyLeftOut + FarNote, found.GetProperty("note").GetString());
        Assert.Single(net.Asked);   // no station was marked Guardian that Spansh does not call so

        // The Human search: the two human ones say they were seen, and Magnus Gateway, seen to be Guardian, is left out.
        net.Instead = asked => asked.Contains(AnyBroker, StringComparison.Ordinal) ? OwnersBrokers.Replace("\"Guardian\"", "\"Human\"", StringComparison.Ordinal)
            : asked.Contains("\"market_id\"", StringComparison.Ordinal) ? """{"count":0,"results":[]}""" : null;
        found = await Find("services", """{ "name": "Human technology broker" }""");
        Assert.Equal(["Ray Gateway: Human", "Soukup City: Human", "Colony Port: "], Brokers(found).Select(line => line.Split(" | ")[0]));
        Assert.EndsWith(" 1 station where you saw a Guardian broker was left out." + FarNote, found.GetProperty("note").GetString());

        // A Guardian broker the owner saw where Spansh guesses Human: Spansh's filter never sends it, so the Guardian search
        // asks for it by its market id, nearest page only, with the same station filters.
        Assert.Equal((200, "Guardian"), await Mark(99, "Dirac Terminal", "Guardian"));
        const string dirac = """
            {"count":1,"results":[{"name":"Dirac Terminal","system_name":"Denebola","type":"Coriolis Starport","distance":35.8,"distance_to_arrival":3164,"large_pads":4,"market_id":99,
              "technology_broker":"Human","primary_economy":"Industrial","updated_at":"2026-10-20T10:00:00Z"}]}
            """;
        net.Asked.Clear();
        net.Instead = asked => asked.Contains("\"market_id\"", StringComparison.Ordinal) ? dirac : GuardianBrokers(asked);
        clock = clock.AddMinutes(11);   // Spansh's answer from before is forgotten: the question is sent again
        found = await Find("services", """{ "name": "Guardian technology broker" }""");
        Assert.Equal(["Magnus Gateway: Guardian | a guess from its High Tech economy", "Dirac Terminal: Guardian | Spansh guesses Human"], Brokers(found));
        Assert.Equal(2, net.Asked.Count);
        Assert.Contains("{\"filters\":{\"market_id\":{\"value\":[\"99\"]}," + StandardTypes, net.Asked[1]);
        // "Show more" never lists it again.
        net.Asked.Clear();
        await Find("services", """{ "name": "Guardian technology broker", "more": 1 }""");
        Assert.DoesNotContain(net.Asked, asked => asked.Contains("\"market_id\"", StringComparison.Ordinal));

        // The marks are in settings.json, and a new start reads them; taking one away saves that too.
        Assert.Equal("Human", new BrokerBook(new Settings(BrokersFile)).KindAt(3223343616));
        Assert.Equal((200, ""), await Mark(3223343616, "Ray Gateway", ""));
        Assert.Null(new BrokerBook(new Settings(BrokersFile)).KindAt(3223343616));
        Assert.Equal("Human", new BrokerBook(new Settings(BrokersFile)).KindAt(3224837632));

        // What cannot be a mark is refused in words for the player.
        Assert.Equal((400, "A broker is Human or Guardian, not \"Thargoid\"."), await Mark(5, "Somewhere", "Thargoid"));
        Assert.Equal((400, "Studio does not know that station's market number, so its broker cannot be marked."), await Mark(0, "Somewhere", "Human"));
    }

    [Fact]
    public async Task The_station_filters_go_into_the_question_and_are_applied_to_the_answer_again()
    {
        // The standard search for a black market: three of the five stations Spansh sent take a medium ship.
        Assert.Equal(["Markus Extraction Exchange", "Kawle Genetics Complex", "al-Din Prospect"], Stations(await Find("services", """{ "name": "Black market" }""")));
        // For a small ship the question is the same (Spansh is only ever asked for "large"), and all five are
        // listed: a pad size that is not known only suits a small ship. The first was last seen in 2021.
        JsonElement small = await Find("services", """{ "name": "Black market", "filters": { "pad": "S" } }""");
        Assert.Single(net.Asked);
        Assert.Equal("Rescue Ship - Li Qing Jao | Sol | 0 | 496.966721 |  | Mega ship |  | - | - | 2021-03-18T16:19:26Z", Line(Rows(small)[0]));
        Assert.Equal("Yan Excavation Hub | Alpha Centauri | 4.38 | 4233.149805 | S | Settlement |  | - | - | 2026-10-04T02:07:49Z", Line(Rows(small)[3]));
        Assert.Equal(5, Rows(small).Length);

        // A large pad, any distance from the star, fleet carriers too, no surface stations, from a system named by hand.
        // (Planetary outposts are asked for all the same: the next test says why.)
        await Call(web, "POST", "/api/market/find/services", """{ "name": "Repair", "from": "Shinrarta Dezhra", "filters": { "pad": "L", "maxStarLs": 0, "carriers": true, "surface": "none" } }""");
        Assert.Equal(Spansh + """/search {"filters":{"services":[{"name":{"value":["Repair"]}}],"type":{"value":["Coriolis Starport","Orbis Starport","Ocellus Starport","Dodec Starport","Outpost","Asteroid base","Mega ship","Planetary Outpost","Drake-Class Carrier"]},"has_large_pad":{"value":true}},"sort":[{"distance":{"direction":"asc"}}],"size":10,"page":0,"reference_system":"Shinrarta Dezhra"}""",
            net.Asked[^1]);
        // No Odyssey settlements: every other surface station stays.
        await Call(web, "POST", "/api/market/find/services", """{ "name": "Repair", "filters": { "surface": "horizons" } }""");
        Assert.Contains("\"Mega ship\",\"Planetary Port\",\"Planetary Outpost\",\"Surface Settlement\"]}", net.Asked[^1]);
    }

    [Fact]
    public async Task With_no_surface_stations_the_Powers_stronghold_carriers_stay()
    {
        // Spansh files a stronghold carrier as a planetary outpost, which it is not. So that type is asked for even
        // when no surface stations are wanted, and what really stands on a planet is left out of the answer here.
        // (The recording's answers to the standard questions stand in for the answers to these two.)
        net.Instead = asked => asked.Contains("\"ships\"", StringComparison.Ordinal) ? RecordedServices.Recorded("Python MkII", "reference_coords")
            : asked.Contains("\"modules\":[", StringComparison.Ordinal) ? RecordedServices.Recorded("\"class\":{\"value\":[\"5\"]}") : null;

        JsonElement ships = await Find("ship", """{ "name": "Python MkII", "filters": { "surface": "none" } }""");
        Assert.Contains("\"Asteroid base\",\"Mega ship\",\"Planetary Outpost\"]}", net.Asked.Last(asked => asked.StartsWith(Spansh + "/search", StringComparison.Ordinal)));
        Assert.Contains("\"name\":\"Furukawa Enterprise\"", RecordedServices.Recorded("Python MkII", "reference_coords"));
        Assert.Equal(["Galileo", "Mars High", "Daedalus", "M.Gorbachev", "Li Qing Jao", "Columbus", "Abraham Lincoln"], Stations(ships));

        JsonElement drives = await Find("module", """{ "name": "Frame Shift Drive", "class": "5", "rating": "A", "filters": { "surface": "none" } }""");
        Assert.Equal(["Stronghold Carrier | Alpha Centauri | 4.38", "Stronghold Carrier | Sirius | 8.59"],
            Rows(drives).Where(row => row.GetProperty("type").GetString() == "Stronghold Carrier").Select(row => string.Join(" | ", Line(row).Split(" | ").Take(3))));
        Assert.Equal(8, Rows(drives).Length);
    }

    /// <summary>
    /// A page of Spansh's answer to "nearest black market", made of the recording's Yan Excavation Hub
    /// (a settlement with one small pad): one copy for every number given, with that many medium pads,
    /// a name of its own and one light year more than the copy before.
    /// </summary>
    /// <param name="known">How many stations Spansh says it has in all.</param>
    static string Hubs(int first, int[] mediumPads, int known = 10000)
    {
        JsonNode page = JsonNode.Parse(RecordedServices.Recorded("\"Black Market\""))!;
        JsonNode hub = page["results"]!.AsArray().Single(station => station!["name"]!.GetValue<string>() == "Yan Excavation Hub")!;
        page["count"] = known;
        page["results"] = new JsonArray([.. mediumPads.Select((pads, at) =>
        {
            JsonNode copy = hub.DeepClone();
            copy["name"] = Invariant($"Hub {first + at}");
            copy["distance"] = first + at;
            copy["medium_pads"] = pads;
            return copy;
        })]);
        return page.ToJsonString();
    }

    [Fact]
    public async Task A_page_the_filters_leave_too_little_of_is_followed_by_the_next()
    {
        // Spansh can be asked for a large pad, but not for "medium or larger": what suits a Type-8 is picked out of what it sends.
        const string blackMarket = """{ "name": "Black market" }""";
        string question = Spansh + """/search {"filters":{"services":[{"name":{"value":["Black Market"]}}],""" + NearSol.Replace("\"size\":20", "\"size\":10");
        string[] pages = [];
        net.Instead = asked => pages[Enumerable.Range(0, pages.Length).First(number => asked.Contains(Invariant($"\"page\":{number},"), StringComparison.Ordinal))];

        // The ten nearest have a small pad only; of the next ten, five take the ship. Half a list is list enough: two pages, not three.
        pages = [Hubs(1, new int[10]), Hubs(11, [0, 1, 0, 2, 1, 0, 0, 1, 1, 0]), Hubs(21, new int[10])];
        JsonElement found = await Find("services", blackMarket);
        Assert.Equal([question, question.Replace("\"page\":0", "\"page\":1")], net.Asked);
        Assert.Equal(["Hub 12", "Hub 14", "Hub 15", "Hub 18", "Hub 19"], Stations(found));
        Assert.Equal("Hub 12 | Alpha Centauri | 12 | 4233.149805 | M | Settlement |  | - | - | 2026-10-04T02:07:49Z", Line(Rows(found)[0]));
        Assert.Equal("The nearest 5 of more than 10,000 stations." + FarNote, found.GetProperty("note").GetString());
        Assert.Equal("https://spansh.co.uk/stations/search/59A29F08-C3DC-11F1-8D68-D7C1D08B7A5E/1", found.GetProperty("link").GetString());

        // Nothing for the ship on three pages: there the asking stops (a fourth request would fail this test), and
        // the player is told what was looked at, not that no such station is known.
        clock = clock.AddMinutes(11);
        pages = [Hubs(1, new int[10]), Hubs(11, new int[10]), Hubs(21, new int[10])];
        found = await Find("services", blackMarket);
        Assert.Equal(5, net.Asked.Count);
        Assert.Equal(question.Replace("\"page\":0", "\"page\":2"), net.Asked[^1]);
        Assert.Empty(Rows(found));
        Assert.Equal("None of the 30 nearest stations that have this passes your filters, and Spansh knows more farther out. Try other filters, or search from another system." + FarNote,
            found.GetProperty("note").GetString());

        // A small ship can use every one of them, so the first page is its whole list. (The same question, minutes later: nothing is asked.)
        found = await Find("services", """{ "name": "Black market", "filters": { "pad": "S" } }""");
        Assert.Equal(5, net.Asked.Count);
        Assert.Equal(10, Rows(found).Length);
        NearestFirst(found);

        // A page shorter than was asked for is Spansh's last: there is no next one to ask for, and nothing farther out to speak of.
        clock = clock.AddMinutes(11);
        pages = [Hubs(1, new int[4], known: 4)];
        found = await Find("services", blackMarket);
        Assert.Equal(6, net.Asked.Count);
        Assert.Equal("No station that passes your filters is known to have this." + FarNote, found.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Show_more_asks_spansh_for_the_pages_after_the_ones_listed_and_for_no_page_twice()
    {
        string question = Spansh + """/search {"filters":{"services":[{"name":{"value":["Black Market"]}}],""" + NearSol.Replace("\"size\":20", "\"size\":10");
        string Page(int number) => question.Replace("\"page\":0", Invariant($"\"page\":{number}"));
        string[] pages = [];
        net.Instead = asked => pages[Enumerable.Range(0, pages.Length).First(number => asked.Contains(Invariant($"\"page\":{number},"), StringComparison.Ordinal))];
        // The search as the page sends it: no "more" the first time, which the app reads as the 0 sent here, and after that what the answer before gave.
        Task<JsonElement> BlackMarkets(string pad, int more) => Find("services", JsonSerializer.Serialize(new { name = "Black market", more, filters = new { pad } }));

        // A small ship can use every hub. Spansh knows 25 of them: two full pages and a short one.
        pages = [Hubs(1, new int[10], known: 25), Hubs(11, new int[10], known: 25), Hubs(21, new int[5], known: 25)];
        Assert.Equal("10 rows, Hub 1 to Hub 10 | The nearest 10 of 25 stations. Stations more than 5,000 ls from their star are left out, as your filters ask. | more: 1", Batch(await BlackMarkets("S", 0)));
        Assert.Equal([Page(0)], net.Asked);                             // a first search asks what it always asked
        Assert.Equal("10 rows, Hub 11 to Hub 20 | The next 10 of 25 stations. Stations more than 5,000 ls from their star are left out, as your filters ask. | more: 2", Batch(await BlackMarkets("S", 1)));
        Assert.Equal([Page(0), Page(1)], net.Asked);                    // the page that comes next, and the first one not again
        Assert.Equal("5 rows, Hub 21 to Hub 25 | Stations more than 5,000 ls from their star are left out, as your filters ask. | more: ", Batch(await BlackMarkets("S", 2)));   // Spansh's last page: nothing more to ask for
        Assert.Equal(3, net.Asked.Count);

        // A Type-8 needs a medium pad. Its first search reads two pages to find five hubs, so more of it starts at the
        // third. Nothing there suits the ship, so it reads on, three pages at most as a first search does, and says
        // what it looked at. Asked to go on once more it reaches Spansh's last page, which has nothing for the ship either.
        clock = clock.AddMinutes(11);
        net.Asked.Clear();
        pages = [Hubs(1, new int[10]), Hubs(11, [0, 1, 0, 2, 1, 0, 0, 1, 1, 0]), Hubs(21, new int[10]), Hubs(31, new int[10]), Hubs(41, new int[10]), Hubs(51, new int[4], known: 54)];
        Assert.Equal("5 rows, Hub 12 to Hub 19 | The nearest 5 of more than 10,000 stations. Stations more than 5,000 ls from their star are left out, as your filters ask. | more: 2", Batch(await BlackMarkets("auto", 0)));
        Assert.Equal("0 rows,  to  | None of the next 30 stations that have this passes your filters, and Spansh knows more farther out. Try other filters, or search from another system. Stations more than 5,000 ls from their star are left out, as your filters ask. | more: 5",
            Batch(await BlackMarkets("auto", 2)));
        Assert.Equal([Page(0), Page(1), Page(2), Page(3), Page(4)], net.Asked);
        Assert.Equal("0 rows,  to  | No more stations that pass your filters are known to have this. Stations more than 5,000 ls from their star are left out, as your filters ask. | more: ", Batch(await BlackMarkets("auto", 5)));
        Assert.Equal(Page(5), net.Asked[^1]);
    }

    [Fact]
    public void The_station_filter_keeps_the_trade_searchs_rules()
    {
        var standard = new StationFilter(Pad: 2, MaxStarLs: 5000, Surface: "odyssey", Carriers: false);

        Assert.True(standard.Keeps("Coriolis", 3, 400));
        Assert.True(standard.Keeps("Outpost", 2, 5000));
        Assert.False(standard.Keeps("Outpost", 2, 5000.5));                 // too far from the star
        Assert.True(standard.Keeps("Outpost", 2, null));                    // a distance that is not known passes
        Assert.True((standard with { MaxStarLs = 0 }).Keeps("Outpost", 2, 6_400_000));   // 0 = any distance
        Assert.False(standard.Keeps("OnFootSettlement", 1, 400));           // too small a pad
        Assert.False(standard.Keeps("MegaShip", null, 400));                // a pad that is not known only suits a small ship
        Assert.True((standard with { Pad = 1 }).Keeps("MegaShip", null, 400));
        Assert.False((standard with { Pad = 3 }).Keeps("Outpost", 2, 400));

        // Building sites take deliveries, they do not trade. Fleet carriers only when asked for; a Power's stronghold carrier is none.
        Assert.False(standard.Keeps("SpaceConstructionDepot", 3, 400));
        Assert.False((standard with { Carriers = true }).Keeps("PlanetaryConstructionDepot", 3, 400));
        Assert.False(standard.Keeps("FleetCarrier", 3, 400));
        Assert.True((standard with { Carriers = true }).Keeps("FleetCarrier", 3, 400));
        Assert.True(standard.Keeps("StrongholdCarrier", 3, 400));

        // Surface stations: all of them, all but Odyssey's settlements, or none.
        string[] surface = ["CraterPort", "CraterOutpost", "SurfaceStation", "OnFootSettlement"];
        Assert.All(surface, type => Assert.True(standard.Allows(type)));
        Assert.Equal(["CraterPort", "CraterOutpost", "SurfaceStation"], surface.Where((standard with { Surface = "horizons" }).Allows));
        Assert.DoesNotContain(surface, (standard with { Surface = "none" }).Allows);
        Assert.DoesNotContain(surface, (standard with { Surface = "NONE" }).Allows);   // capitals do not matter, as in every setting of Studio Market
        Assert.True((standard with { Surface = "none" }).Allows("AsteroidBase"));
    }

    // ---------------------------------------------------------------- when a name, a system or a service lets the player down

    [Fact]
    public async Task A_name_the_service_does_not_know_is_refused_without_a_search()
    {
        Assert.Equal((400, "Ardent Insight knows no commodity called \"Unobtainium\". Pick a name from the list."), await Refused("commodity", """{ "name": " Unobtainium " }"""));
        Assert.Equal((400, "Spansh knows no module called \"Warp Drive\". Pick a name from the list."), await Refused("module", """{ "name": "Warp Drive" }"""));
        Assert.Equal((400, "Spansh knows no ship called \"Millennium Falcon\". Pick a name from the list."), await Refused("ship", """{ "name": "Millennium Falcon" }"""));
        Assert.Equal((400, "Type the name of the ship to find, or pick it from the list."), await Refused("ship", """{ "name": "  " }"""));
        Assert.Equal((400, "There is no class \"9\"."), await Refused("module", """{ "name": "Frame Shift Drive", "class": "9" }"""));
        Assert.Equal((400, "There is no rating \"Z\"."), await Refused("module", """{ "name": "Frame Shift Drive", "rating": "Z" }"""));
        Assert.Equal((400, "Unknown kind of service."), await Refused("services", """{ "name": "Hairdresser" }"""));

        // Only the three lists of names were fetched, each once.
        Assert.Equal([Ardent + "/commodities", Spansh + "/field_values/modules", Spansh + "/field_values/ships"], net.Asked);
    }

    [Fact]
    public async Task A_system_the_service_does_not_know_is_said_so()
    {
        // Ardent Insight is asked where the system is before anything else: one request for nothing, not three.
        Assert.Equal((400, "Ardent Insight does not know a system called \"Zzqxnotasystem\". Check its spelling."), await Refused("commodity", """{ "name": "Gold", "from": "Zzqxnotasystem" }"""));
        Assert.Equal([Ardent + "/commodities", Ardent + "/system/name/Zzqxnotasystem"], net.Asked);

        // Spansh refuses the search itself ("Invalid request").
        Assert.Equal((400, "Spansh does not know a system called \"Zzqxnotasystem\". Check its spelling."), await Refused("ship", """{ "name": "Python MkII", "from": "Zzqxnotasystem" }"""));
        Assert.EndsWith("\"reference_system\":\"Zzqxnotasystem\"}", net.Asked[^1]);
    }

    [Fact]
    public async Task A_system_the_player_is_in_that_the_service_has_no_data_for_is_no_spelling_mistake()
    {
        // Far from everything, in a system no player's tool has told Ardent Insight about. The game says where it lies,
        // so Ardent first hears its name with the two questions about offers, and says "not found" to both.
        using var far = new HaulRig(HaulRig.In(new Place("Praea Euq NW-W b1-3", [-1000, 50, 20000], "", 0), docked: false));
        using var server = new WebServer(far.Game.JournalDir);
        FindApi.Map(server, far.Watcher.State, far.Hauling, data, () => clock);

        var (status, answer) = await Call(server, "POST", "/api/market/find/commodity", """{ "name": "Gold" }""");

        Assert.Equal((400, "Ardent Insight has no data for Praea Euq NW-W b1-3, the system you are in. Type a system near it under \"From\"."),
            (status, answer.GetProperty("error").GetString()));
        Assert.Equal(3, net.Asked.Count);
        Assert.All(net.Asked.Skip(1), asked => Assert.StartsWith(Ardent + "/system/name/Praea%20Euq%20NW-W%20b1-3/commodity/name/gold", asked));

        // Spansh is given the place and not the name, so it finds what is nearest all the same. (The answer recorded from Sol stands in.)
        net.Instead = asked => asked.Contains("/search", StringComparison.Ordinal) ? RecordedServices.Recorded("\"Shipyard\"") : null;
        (status, answer) = await Call(server, "POST", "/api/market/find/services", """{ "name": "Shipyard" }""");
        Assert.Equal((200, 5), (status, Rows(answer).Length));
        Assert.EndsWith("\"reference_coords\":{\"x\":-1000,\"y\":50,\"z\":20000}}", net.Asked[^1]);
    }

    [Fact]
    public async Task Without_a_system_to_search_from_nothing_is_asked()
    {
        using var nowhere = new HaulRig([]);   // Elite has not said yet where the player is
        using var server = new WebServer(nowhere.Game.JournalDir);
        FindApi.Map(server, nowhere.Watcher.State, nowhere.Hauling, data, () => clock);

        var (status, answer) = await Call(server, "POST", "/api/market/find/services", """{ "name": "Repair" }""");

        Assert.Equal((400, "Studio does not know yet where you are. Start Elite, or type a system under \"From\"."), (status, answer.GetProperty("error").GetString()));
        Assert.Empty(net.Asked);
    }

    [Fact]
    public async Task A_service_that_is_down_is_named_and_not_asked_twice()
    {
        net.Down = "spansh.co.uk";

        // The list of names cannot be fetched, so no name can be checked: one request, no second try.
        var (status, names) = await Call(web, "GET", "/api/market/find/names?kind=module");
        Assert.Equal((502, "Couldn't get an answer from Spansh: No such host is known."), (status, names.GetProperty("error").GetString()));
        Assert.Equal((502, "Couldn't get an answer from Spansh: No such host is known."), await Refused("module", """{ "name": "Frame Shift Drive" }"""));
        Assert.Equal((502, "Couldn't get an answer from Spansh: No such host is known."), await Refused("services", """{ "name": "Repair" }"""));
        Assert.Equal(3, net.Asked.Count);

        // Ardent Insight is another service: commodities still work.
        Assert.Equal(42, Rows(await Find("commodity", """{ "name": "Gold" }""")).Length);

        // A failure is not remembered: when Spansh is back, the same search works.
        net.Down = null;
        Assert.Equal(8, Rows(await Find("module", """{ "name": "Frame Shift Drive", "class": "5", "rating": "A" }""")).Length);

        net.Down = "ardent-insight.com";
        clock = clock.AddMinutes(6);
        Assert.Equal((502, "Couldn't get an answer from Ardent Insight: No such host is known."), await Refused("commodity", """{ "name": "Gold" }"""));
    }

    [Fact]
    public async Task An_answer_that_is_not_what_was_asked_for_is_not_shown()
    {
        // Spansh drops a filter it does not understand without a word and sends the nearest stations of any kind.
        // Here it "answers" the question for raw material traders with stations that have a Guardian technology broker.
        net.Instead = asked => asked.Contains("\"material_trader\"", StringComparison.Ordinal) ? RecordedServices.Recorded("\"technology_broker\":{\"value\":[\"Guardian\"]}") : null;
        Assert.Equal((502, "Couldn't get an answer from Spansh: It sent stations that do not have what was asked for."), await Refused("services", """{ "name": "Raw material trader" }"""));

        // An error page instead of an answer, and an answer with nothing in it.
        net.Instead = _ => "<html>Bad gateway</html>";
        Assert.Equal((502, "Couldn't get an answer from Spansh: The service sent an answer that could not be read."), await Refused("services", """{ "name": "Shipyard" }"""));
        net.Instead = asked => asked.Contains("/search", StringComparison.Ordinal) ? """{"count":0,"results":[]}""" : null;
        JsonElement none = await Find("module", """{ "name": "Guardian FSD Booster", "class": "5", "rating": "A" }""");
        Assert.Empty(Rows(none));
        Assert.Equal("No station that passes your filters is known to have this. Not every module comes in every class and rating.", none.GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("link").ValueKind);

        // Ardent Insight: a price overview with nothing in it is no list of names, and no offers at all is an answer of its own.
        net.Instead = asked => asked.EndsWith("/commodities", StringComparison.Ordinal) ? "[]" : null;
        Assert.Equal((502, "Couldn't get an answer from Ardent Insight: The price overview came back empty."), await Refused("commodity", """{ "name": "Gold" }"""));
        net.Instead = asked => asked.Contains("/commodity/name/", StringComparison.Ordinal) ? "[]" : null;
        none = await Find("commodity", """{ "name": "Tritium", "side": "sell" }""");
        Assert.Empty(Rows(none));
        Assert.Equal("No station within 500 ly of Sol that passes your filters is known to buy Tritium. Try older prices, fewer tons or other filters.", none.GetProperty("note").GetString());
    }
}
