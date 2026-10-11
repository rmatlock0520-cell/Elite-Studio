using System.Text;
using System.Text.Json;
using EliteStudio.App;
using EliteStudio.Market;
using WebRequest = EliteStudio.App.WebRequest;     // System.Net has older types with the same names
using WebResponse = EliteStudio.App.WebResponse;

namespace EliteStudio.Tests;

/// <summary>
/// The price lists the player has seen themselves (StationBook), and "Find by name" for a station:
/// the stations with a name like the one typed, and a station's price list with what a ton makes
/// carried there and back from where the player is docked. The player opens the market at Alpha
/// Port, flies to Beta Hub and opens the market there. Spansh's answers are made up here.
/// </summary>
public sealed class StationSearchTests : IDisposable
{
    const string Search = "https://spansh.co.uk/api/stations/search";
    // Alpha Port as Spansh knows it, and a station far off with a name like it that the player has never seen.
    const string TwoAlphas = """
        {"count":2,"results":[
          {"name":"Alpha Port","system_name":"Alpha","market_id":101,"type":"Coriolis Starport","distance":20,"distance_to_arrival":250,"large_pads":4,
           "market_updated_at":"REPORTED","market":[{"commodity":"Gold","buy_price":1200,"sell_price":0,"supply":300,"demand":0},{"commodity":"Painite","buy_price":0,"sell_price":0,"supply":0,"demand":0}]},
          {"name":"Alpha Annex","system_name":"Far","market_id":999,"type":"Outpost","distance":40.126,"distance_to_arrival":12,"medium_pads":2,
           "market_updated_at":"2026-10-08T08:00:00Z","market":[{"commodity":"Tea","buy_price":0,"sell_price":1500,"supply":0,"demand":40}]}]}
        """;

    readonly RecordedServices net = new();
    readonly MarketData data;
    readonly HaulRig rig = new(HaulRig.In(HaulRig.Alpha));
    readonly WebServer web;
    readonly string file;
    string reported = "2026-10-09T09:00:00Z";   // when Spansh last heard of Alpha Port's prices: an hour before the player saw them

    public StationSearchTests()
    {
        data = new MarketData(net);
        web = new WebServer(rig.Game.JournalDir);   // no page is asked for here: any folder will do
        file = Path.Combine(rig.Game.JournalDir, "stations.json");
        FindApi.Map(web, rig.Watcher.State, rig.Hauling, data, () => RecordedServices.RecordedUtc, new StationBook(rig.Watcher.State, file));
        net.Instead = asked => asked.StartsWith(Search, StringComparison.Ordinal) ? TwoAlphas.Replace("REPORTED", reported, StringComparison.Ordinal) : null;

        // Alpha Port sells gold at 1,000 and nothing else; Beta Hub sells gold at 9,000 and silver at 2,000. Both pay 5,000 for all of them.
        rig.GameFile("Market.json", HaulRig.PriceList(HaulRig.Alpha, ("gold", 1000, 500), ("silver", 0, 0)));
        rig.Play(HaulRig.Undock(HaulRig.Alpha), HaulRig.Jump(HaulRig.Beta), HaulRig.Dock(HaulRig.Beta));
        rig.GameFile("Market.json", HaulRig.PriceList(HaulRig.Beta, ("gold", 9000, 10), ("silver", 2000, 50)).Replace("T10:00:00Z", "T10:30:00Z", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        web.Dispose();
        rig.Dispose();
        data.Dispose();
    }

    async Task<(int Status, JsonElement Json)> Call(string method, string path, string body = "")
    {
        string[] parts = path.Split('?', 2);
        WebResponse answer = await web.Handle(new WebRequest(method, parts[0], WebServer.ParseQuery(parts.Length == 2 ? parts[1] : ""), body));
        return (answer.Status, JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(answer.Body)));
    }

    async Task<JsonElement> Ok(string method, string path, string body = "")
    {
        var (status, answer) = await Call(method, path, body);
        Assert.True(status == 200, answer.GetRawText());
        return answer;
    }

    static string Text(JsonElement value) => value.ValueKind == JsonValueKind.Null ? "-" : value.ToString();

    /// <summary>Stations found, each as "station | system | ly | pad | type | market id".</summary>
    static string[] Places(JsonElement answer) =>
    [
        .. answer.GetProperty("rows").EnumerateArray().Select(row => string.Join(" | ",
            ((string[])["station", "system", "distanceLy", "pad", "type", "marketId"]).Select(name => Text(row.GetProperty(name))))),
    ];

    /// <summary>A price list, each line as "name: buy/supply, sell/demand, take there, bring back".</summary>
    static string[] Lines(JsonElement prices) =>
    [
        .. prices.GetProperty("rows").EnumerateArray().Select(row =>
            $"{row.GetProperty("name").GetString()}: {row.GetProperty("buy")}/{row.GetProperty("supply")}, {row.GetProperty("sell")}/{row.GetProperty("demand")}, {Text(row.GetProperty("takeThere"))}, {Text(row.GetProperty("bringBack"))}"),
    ];

    [Fact]
    public void Every_market_the_player_opens_is_remembered_and_outlasts_a_restart()
    {
        var again = new StationBook(rig.Watcher.State, file);
        Assert.Equal(["Beta Hub in Beta", "Alpha Port in Alpha"], again.Visits.Select(visit => $"{visit.Station} in {visit.System}"));
        StationBook.Visit alpha = again.Find(HaulRig.Alpha.Market)!;
        Assert.Equal(new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc), alpha.SeenUtc);
        Assert.Equal(HaulRig.Alpha.At, alpha.At);   // where the player was when they saw it
        Assert.Equal(["gold 1000 500 5000 100", "silver 0 0 5000 100"], alpha.Items.Select(item => $"{item.Id} {item.BuyPrice} {item.Stock} {item.SellPrice} {item.Demand}"));
        Assert.Null(again.Find(12345));

        // The same list again (Studio starts, the file is as it was) writes nothing; a newer one takes the station's place at the front.
        DateTime written = File.GetLastWriteTimeUtc(file);
        _ = new StationBook(rig.Watcher.State, file);
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public async Task A_station_is_found_by_a_part_of_its_name()
    {
        JsonElement found = await Ok("POST", "/api/market/find/station", """{ "name": " alpha " }""");
        Assert.Equal("Stations called \"alpha\"", found.GetProperty("what").GetString());
        // The player's own visit first, with what Spansh knows of the station besides; then Spansh's other match.
        Assert.Equal(["Alpha Port | Alpha | 20 | L | Coriolis Starport | 101", "Alpha Annex | Far | 40.13 | M | Outpost | 999"], Places(found));
        // The newer of the two times: the player's own look at 10:00, not Spansh's report of 09:00.
        Assert.Equal(new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc), found.GetProperty("rows")[0].GetProperty("updated").GetDateTime().ToUniversalTime());
        // Asked by name, with no station filter and no order of its own: Spansh's best match comes first. Beta lies at 10, 20, 0.
        Assert.Equal([Search + """ {"filters":{"name":{"value":"alpha"}},"size":10,"page":0,"reference_coords":{"x":10,"y":20,"z":0}}"""], net.Asked.Where(asked => asked.StartsWith(Search, StringComparison.Ordinal)));

        // The names to pick from are the stations whose market the player opened.
        Assert.Equal(["Beta Hub", "Alpha Port"], (await Ok("GET", "/api/market/find/names?kind=station")).GetProperty("names").EnumerateArray().Select(name => name.GetString()));

        var (status, refused) = await Call("POST", "/api/market/find/station", """{ "name": "  " }""");
        Assert.Equal((400, "Type a station's name, or a part of it."), (status, refused.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task A_price_list_says_what_to_take_there_and_what_to_bring_back()
    {
        var (status, unknown) = await Call("GET", "/api/market/find/prices?id=999");
        Assert.Equal((400, "Studio has no price list for that station. Search for it by name first."), (status, unknown.GetProperty("error").GetString()));

        // Docked at Beta Hub, looking at Alpha Port, whose list the player saw after Spansh last heard of it: their own is shown.
        await Ok("POST", "/api/market/find/station", """{ "name": "alpha" }""");
        JsonElement prices = await Ok("GET", "/api/market/find/prices?id=101");
        Assert.Equal(("Alpha Port", "Alpha", true, "Beta Hub"),
            (prices.GetProperty("station").GetString(), prices.GetProperty("system").GetString(), prices.GetProperty("mine").GetBoolean(), prices.GetProperty("here").GetString()));
        // Gold: 1,000 at Alpha, 5,000 paid at Beta: 4,000 a ton brought back. Silver: 2,000 at Beta, 5,000 paid at Alpha: 3,000 a ton taken there.
        // (Gold bought at Beta for 9,000 makes nothing at Alpha, and Alpha sells no silver.)
        Assert.Equal(["Gold: 1000/500, 5000/100, -, 4000", "Silver: 0/0, 5000/100, 3000, -"], Lines(prices));

        // A station the player never saw: Spansh's list, with the same comparison. Tea is not on Beta Hub's list.
        prices = await Ok("GET", "/api/market/find/prices?id=999");
        Assert.Equal(("Alpha Annex", false, "Beta Hub"), (prices.GetProperty("station").GetString(), prices.GetProperty("mine").GetBoolean(), prices.GetProperty("here").GetString()));
        Assert.Equal(["Tea: 0/0, 1500/40, -, -"], Lines(prices));

        // The station the player is docked at has nothing to be compared with.
        prices = await Ok("GET", "/api/market/find/prices?id=202");
        Assert.Equal(JsonValueKind.Null, prices.GetProperty("here").ValueKind);
        Assert.Equal(["Gold: 9000/10, 5000/100, -, -", "Silver: 2000/50, 5000/100, -, -"], Lines(prices));
    }

    [Fact]
    public async Task The_services_list_is_shown_when_it_is_newer_than_the_players_own()
    {
        reported = "2026-10-09T11:00:00Z";   // somebody's tool was there an hour after the player
        await Ok("POST", "/api/market/find/station", """{ "name": "alpha" }""");
        JsonElement prices = await Ok("GET", "/api/market/find/prices?id=101");
        Assert.False(prices.GetProperty("mine").GetBoolean());
        // A line with neither price (Painite) is left out. Gold at 1,200 now: 3,800 a ton brought back to Beta Hub.
        Assert.Equal(["Gold: 1200/300, 0/0, -, 3800"], Lines(prices));
    }

    [Fact]
    public async Task Without_the_service_the_players_own_visits_are_still_found()
    {
        net.Down = "spansh.co.uk";
        JsonElement found = await Ok("POST", "/api/market/find/station", """{ "name": "Alpha Port" }""");
        // Its place is the one the game gave while the player was there: 20 ly from Beta.
        Assert.Equal(["Alpha Port | Alpha | 20 |  |  | 101"], Places(found));
        Assert.StartsWith("Spansh gave no answer (", found.GetProperty("note").GetString());
        Assert.True((await Ok("GET", "/api/market/find/prices?id=101")).GetProperty("mine").GetBoolean());

        // A name nobody knows is the service's failure to report, as for every other search.
        var (status, answer) = await Call("POST", "/api/market/find/station", """{ "name": "Nowhere" }""");
        Assert.Equal(502, status);
        Assert.StartsWith("Couldn't get an answer from Spansh", answer.GetProperty("error").GetString());
    }
}
