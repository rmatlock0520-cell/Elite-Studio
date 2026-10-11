using System.Globalization;
using System.Text;
using System.Text.Json;
using EliteStudio.App;
using EliteStudio.Market;
using static System.FormattableString;
using Place = EliteStudio.Tests.HaulRig.Place;
using WebRequest = EliteStudio.App.WebRequest;     // System.Net has older types with the same names
using WebResponse = EliteStudio.App.WebResponse;

namespace EliteStudio.Tests;

/// <summary>
/// The market sniffer through its routes: saved commodity searches, run by the clock anywhere in the
/// galaxy, with a chime when one is met. The player sits in Sol in a Type-8 (medium pad). Ardent
/// Insight's price overview is the recorded one (FindTests); its galaxy-wide lists are made up here.
/// </summary>
public sealed class SnifferTests : IDisposable
{
    const string Ardent = "https://api.ardent-insight.com/v2";
    const string At = "/api/market/sniffer";
    // Gold to buy at 40,000 cr or less, 500 t or more, with every other field as the page sends it.
    const string GoldToBuy = """{ "name": "gold", "side": "buy", "price": 40000, "tons": 500, "pad": "auto", "maxStarLs": 5000, "surface": "odyssey", "carriers": false, "maxAgeHours": 48 }""";
    const string GoldAsked = Ardent + "/commodity/name/gold/exports?minVolume=500&maxPrice=40000&maxDaysAgo=2&fleetCarriers=false";

    static readonly Place Sol = new("Sol", [0, 0, 0], "Daedalus", 128016384);

    readonly RecordedServices net = new();
    readonly MarketData data;
    readonly HaulRig rig = new(HaulRig.In(Sol, docked: false));
    readonly List<int> chimes = [];
    WebServer web;
    Sniffer sniffer;
    DateTime clock = RecordedServices.RecordedUtc;

    public SnifferTests()
    {
        data = new MarketData(net);
        (web, sniffer) = Start();
        // Three stations sell gold: the cheapest 300 ly out, a dearer one next door, and one that asks too much.
        net.Instead = asked => asked.StartsWith(Ardent + "/commodity/name/gold/exports", StringComparison.Ordinal)
            ? "[" + string.Join(",", Depot(1, "Far Mine", 300, 31000, 9000), Depot(2, "Near Mine", 10, 38000, 700), Depot(3, "Dear Mine", 5, 45000, 9000)) + "]" : null;
    }

    (WebServer, Sniffer) Start()
    {
        var server = new WebServer(rig.Game.JournalDir);   // no page is asked for here: any folder will do
        return (server, SnifferApi.Map(server, rig.Watcher.State, rig.Hauling, data, new Settings(rig.SettingsFile), () => clock, chimes.Add));
    }

    public void Dispose()
    {
        web.Dispose();
        rig.Dispose();
        data.Dispose();
    }

    /// <summary>A made-up station that sells gold, reported an hour ago, x light years from Sol.</summary>
    string Depot(int id, string name, int x, int price, int stock, double starLs = 100, int pad = 3, double hoursAgo = 1) => Invariant(
        $$"""{"commodityName":"gold","marketId":{{id}},"stationName":"{{name}}","stationType":"Coriolis","distanceToArrival":{{starLs}},"maxLandingPadSize":{{pad}},"systemName":"System {{id}}","systemX":{{x}},"systemY":0,"systemZ":0,"buyPrice":{{price}},"stock":{{stock}},"sellPrice":0,"demand":0,"meanPrice":47000,"updatedAt":"{{clock.AddHours(-hoursAgo).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)}}"}""");

    async Task<(int Status, JsonElement Json)> Call(string method, string path, string body = "")
    {
        string[] parts = path.Split('?', 2);
        WebResponse answer = await web.Handle(new WebRequest(method, parts[0], WebServer.ParseQuery(parts.Length == 2 ? parts[1] : ""), body));
        return (answer.Status, JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(answer.Body)));
    }

    /// <summary>A call that must succeed: the picture, or the stations found.</summary>
    async Task<JsonElement> Ok(string method, string path, string body = "")
    {
        var (status, answer) = await Call(method, path, body);
        Assert.True(status == 200, answer.GetRawText());
        return answer;
    }

    /// <summary>Saves a search and gives its id.</summary>
    async Task<string> Save(string body) =>
        (await Ok("POST", At + "/save", body)).GetProperty("searches").EnumerateArray().Last().GetProperty("search").GetProperty("id").GetString()!;

    /// <summary>The app's timer comes round after so many minutes, and what it started is waited for.</summary>
    async Task After(double minutes)
    {
        clock = clock.AddMinutes(minutes);
        sniffer.Tick();
        await sniffer.LastRun;
    }

    /// <summary>The saved searches as lines: "Buy Gold: found 2, best 31000 at Far Mine" or "…: not run" or "…: " and the problem.</summary>
    async Task<string[]> Lines() =>
    [
        .. (await Ok("GET", At)).GetProperty("searches").EnumerateArray().Select(line =>
        {
            JsonElement search = line.GetProperty("search");
            string what = $"{(search.GetProperty("side").GetString() == "sell" ? "Sell" : "Buy")} {search.GetProperty("name").GetString()}: ";
            return what + (line.GetProperty("problem").GetString() is { } problem ? problem
                : line.GetProperty("ranUtc").ValueKind == JsonValueKind.Null ? "not run"
                : line.GetProperty("found").GetInt32() == 0 ? "found 0"
                : $"found {line.GetProperty("found")}, best {line.GetProperty("bestPrice")} at {line.GetProperty("bestStation").GetString()}");
        }),
    ];

    int Asked(string address) => net.Asked.Count(asked => asked == address);

    [Fact]
    public async Task A_saved_search_is_run_by_the_clock_and_chimes_when_it_is_met()
    {
        string id = await Save(GoldToBuy);
        Assert.Equal(["Buy gold: not run"], await Lines());   // (the list of names is not known yet, so the name stands as typed)
        Assert.Empty(chimes);

        // The first tick of the timer runs it: the whole galaxy is asked in one question, with the price and the tons in it.
        await After(0);
        Assert.Equal(1, Asked(GoldAsked));
        // The one that asks too much is left out, whatever the service sent. The best price counts, not the nearest.
        Assert.Equal(["Buy gold: found 2, best 31000 at Far Mine"], await Lines());
        Assert.Equal([60], chimes);

        // Its stations as a Find by name answer, best price first, with distances from where the player is.
        JsonElement found = await Ok("GET", At + "/matches?id=" + id);
        Assert.Equal("Where to buy gold", found.GetProperty("what").GetString());
        Assert.Equal("Sol", found.GetProperty("from").GetString());
        Assert.Equal(["Far Mine | System 1 | 300 | 31000 | 9000", "Near Mine | System 2 | 10 | 38000 | 700"],
            found.GetProperty("rows").EnumerateArray().Select(row => $"{row.GetProperty("station").GetString()} | {row.GetProperty("system").GetString()} | {row.GetProperty("distanceLy")} | {row.GetProperty("price")} | {row.GetProperty("tons")}"));

        // Nothing more for an hour, however often the timer comes round.
        await After(30);
        await After(29);
        Assert.Equal(1, Asked(GoldAsked));
        Assert.Equal([60], chimes);
        // After the hour it is asked again, and it chimes again: every hour while it stays met (the owner's choice).
        await After(1);
        Assert.Equal(2, Asked(GoldAsked));
        Assert.Equal([60, 60], chimes);
    }

    [Fact]
    public async Task Each_search_has_its_own_two_ticks()
    {
        string quiet = await Save(GoldToBuy);
        string off = await Save(GoldToBuy.Replace("40000", "39000"));
        await Ok("POST", At + "/set", $$"""{ "id": "{{quiet}}", "sound": false }""");
        await Ok("POST", At + "/set", $$"""{ "id": "{{off}}", "on": false }""");

        // The one that is off is never run by the clock; the one without sound is, and stays silent.
        await After(0);
        await After(2);
        await After(2);
        Assert.Equal(["Buy gold: found 2, best 31000 at Far Mine", "Buy gold: not run"], await Lines());
        Assert.Empty(chimes);
        Assert.Equal(0, Asked(GoldAsked.Replace("40000", "39000")));

        // "Run now" runs even one that is off, and its sound is its own.
        await Ok("POST", At + "/run", $$"""{ "id": "{{off}}" }""");
        Assert.Equal(["Buy gold: found 2, best 31000 at Far Mine", "Buy gold: found 2, best 31000 at Far Mine"], await Lines());
        Assert.Equal([60], chimes);

        // Several that are on go out a minute apart, not in one burst.
        await Ok("POST", At + "/set", $$"""{ "id": "{{off}}", "on": true }""");
        string third = await Save(GoldToBuy.Replace("40000", "32000"));
        int Lists() => net.Asked.Count(asked => asked.Contains("/exports", StringComparison.Ordinal));
        int before = Lists();
        await After(60);   // all three are due: the one never run goes first
        Assert.Equal(1, Asked(GoldAsked.Replace("40000", "32000")));
        await After(0.5);
        Assert.Equal(before + 1, Lists());
        await After(1);
        Assert.Equal(before + 2, Lists());
        await After(1);
        Assert.Equal(before + 3, Lists());
        Assert.Equal("Buy Gold: found 1, best 31000 at Far Mine", (await Lines())[2]);   // saved once the names were known: set right
        Assert.NotEqual(quiet, third);
    }

    [Fact]
    public async Task The_price_the_tons_and_the_station_filters_decide_what_meets_a_search()
    {
        // Six stations, each failing one thing but the last: price, tons, the pad, the distance from its star, the age of its price.
        net.Instead = asked => asked.Contains("/commodity/name/gold/exports", StringComparison.Ordinal) ? "[" + string.Join(",",
            Depot(1, "Too dear", 1, 40001, 9000), Depot(2, "Too little", 1, 30000, 499), Depot(3, "Small pad", 1, 30000, 9000, pad: 1),
            Depot(4, "Far from its star", 1, 30000, 9000, starLs: 5001), Depot(5, "Old price", 1, 30000, 9000, hoursAgo: 49), Depot(6, "Just right", 1, 40000, 500)) + "]" : null;
        string id = await Save(GoldToBuy);
        await Ok("POST", At + "/run", $$"""{ "id": "{{id}}" }""");
        Assert.Equal(["Buy gold: found 1, best 40000 at Just right"], await Lines());

        // To sell: the service is asked who buys, at this price or more; a station that has it in stock is no buyer.
        net.Instead = asked => asked == Ardent + "/commodity/name/gold/imports?minVolume=200&minPrice=50000&maxDaysAgo=1"
            ? "[" + string.Join(",", Depot(7, "Buyer", 1, 0, 0).Replace("\"sellPrice\":0,\"demand\":0", "\"sellPrice\":52000,\"demand\":800"),
                Depot(8, "Seller", 1, 30000, 50).Replace("\"sellPrice\":0,\"demand\":0", "\"sellPrice\":60000,\"demand\":800")) + "]" : null;
        string sell = await Save("""{ "name": "Gold", "side": "sell", "price": 50000, "tons": 200, "pad": "L", "maxStarLs": 0, "surface": "none", "carriers": true, "maxAgeHours": 24 }""");
        await Ok("POST", At + "/run", $$"""{ "id": "{{sell}}" }""");
        // (By now the list of names is known, so the name was set right when it was saved.)
        Assert.Equal("Sell Gold: found 1, best 52000 at Buyer", (await Lines())[1]);

        // Against the galactic average (47,000 cr in these lists), with no price in credits: half the average or less is 23,500 or less.
        net.Instead = asked => asked == Ardent + "/commodity/name/gold/exports?minVolume=1&maxDaysAgo=2&fleetCarriers=false"
            ? "[" + string.Join(",", Depot(9, "Half price", 1, 23500, 50), Depot(10, "A little more", 1, 23501, 50)) + "]" : null;
        string half = await Save("""{ "name": "Gold", "side": "buy", "averagePercent": -50, "pad": "auto", "maxStarLs": 5000 }""");
        JsonElement halved = await Ok("POST", At + "/run", $$"""{ "id": "{{half}}" }""");
        Assert.Equal("Buy Gold: found 1, best 23500 at Half price", (await Lines())[2]);
        Assert.Equal(47000, halved.GetProperty("searches")[2].GetProperty("average").GetInt64());
        // With a price in credits as well, both count; a stand that is not one of the tens moves to the nearest.
        string both = await Save("""{ "name": "Gold", "side": "buy", "price": 23000, "averagePercent": -47, "pad": "auto", "maxStarLs": 5000 }""");
        net.Instead = asked => asked.Contains("maxPrice=23000", StringComparison.Ordinal) ? "[" + Depot(9, "Half price", 1, 23000, 50) + "," + Depot(10, "Over", 1, 23001, 50) + "]" : null;
        JsonElement strict = await Ok("POST", At + "/run", $$"""{ "id": "{{both}}" }""");
        Assert.Equal(-50, strict.GetProperty("searches")[3].GetProperty("search").GetProperty("averagePercent").GetInt32());
        Assert.Equal("Buy Gold: found 1, best 23000 at Half price", (await Lines())[3]);

        // Nothing meets it: no chime, and the page is told so.
        net.Instead = _ => null;   // the recording has no such list: "not found"
        chimes.Clear();
        clock = clock.AddMinutes(10);
        var (status, answer) = await Call("POST", At + "/run", $$"""{ "id": "{{id}}" }""");
        Assert.Equal(200, status);
        Assert.StartsWith("Buy gold: Couldn't get an answer from Ardent Insight", (await Lines())[0]);
        Assert.Empty(chimes);
        Assert.True(answer.GetProperty("searches")[0].GetProperty("problem").GetString()!.Length > 0);
    }

    [Fact]
    public async Task Saved_searches_are_kept_changed_and_deleted()
    {
        string id = await Save(GoldToBuy);
        await Ok("POST", At + "/volume", """{ "volume": 250 }""");
        Assert.Equal([100], chimes);   // setting the volume plays it, so the player hears what they picked

        // A name nobody could mean is refused; without a name there is nothing to save.
        await Ok("POST", At + "/run", $$"""{ "id": "{{id}}" }""");
        Assert.Equal((400, "Ardent Insight knows no commodity called \"Unobtainium\". Pick a name from the list."), await Refused("save", GoldToBuy.Replace("gold", "Unobtainium")));
        Assert.Equal((400, "Type the name of the commodity to watch, or pick it from the list."), await Refused("save", GoldToBuy.Replace("gold", " ")));
        Assert.Equal((404, "That saved search is no longer there."), await Refused("run", """{ "id": "nobody" }"""));

        // Saved again under its id it is changed in place, values that fit nothing are set right, and what it found is forgotten.
        JsonElement changed = (await Ok("POST", At + "/save", $$"""{ "id": "{{id}}", "name": "Gold", "side": "SELL", "price": -5, "tons": -1, "pad": "huge", "surface": "moon", "maxStarLs": -3, "maxAgeHours": 0 }""")).GetProperty("searches");
        Assert.Equal(1, changed.GetArrayLength());
        Assert.Equal($$"""{"id":"{{id}}","name":"Gold","side":"sell","price":0,"averagePercent":null,"tons":0,"pad":"auto","maxStarLs":0,"surface":"odyssey","carriers":false,"maxAgeHours":1,"on":true,"sound":true}""",
            JsonSerializer.Serialize(changed[0].GetProperty("search")));
        Assert.Equal(["Sell Gold: not run"], await Lines());

        // There is room for ten.
        for (int n = 2; n <= Sniffer.Most; n++) await Save(GoldToBuy);
        Assert.Equal((400, "There is room for 10 saved searches. Delete one first."), await Refused("save", GoldToBuy));

        // They are there again when Studio starts, the volume too.
        web.Dispose();
        (web, sniffer) = Start();
        JsonElement again = await Ok("GET", At);
        Assert.Equal(10, again.GetProperty("searches").GetArrayLength());
        Assert.Equal(id, again.GetProperty("searches")[0].GetProperty("search").GetProperty("id").GetString());
        Assert.Equal(100, again.GetProperty("volume").GetInt32());

        JsonElement fewer = await Ok("POST", At + "/delete", $$"""{ "id": "{{id}}" }""");
        Assert.Equal(9, fewer.GetProperty("searches").GetArrayLength());
        Assert.Equal((404, "That saved search is no longer there."), await Refused("delete", $$"""{ "id": "{{id}}" }"""));
    }

    async Task<(int Status, string Error)> Refused(string route, string body)
    {
        var (status, answer) = await Call("POST", At + "/" + route, body);
        Assert.False(answer.GetProperty("ok").GetBoolean());
        return (status, answer.GetProperty("error").GetString()!);
    }
}
