using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EliteStudio.Core;
using EliteStudio.Market;

namespace EliteStudio.Tests;

/// <summary>
/// The two data services, played back from tests\Fixtures\market. ardent.json holds real answers of
/// the Ardent Insight service and spansh.json two real pages from Spansh, all recorded on the
/// morning of 9 October 2026 for searches from the system LHS 3447 and cut down to stay small:
/// the price overview to 23 commodities, "nearby" and galaxy lists to their first 20 rows (100
/// for a lookup), a system's own lists to those 23 commodities, a Spansh system to eight fields.
/// A request that was not recorded gets "404 Not Found" and is noted in <see cref="NotRecorded"/>.
/// </summary>
sealed class RecordedMarket : HttpMessageHandler
{
    public const string Ardent = "https://api.ardent-insight.com/v2";
    public const string Overview = Ardent + "/commodities";
    public const string Spansh = "https://spansh.co.uk/api/systems/search";

    static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "market");

    static readonly Lazy<Dictionary<string, string>> Answers = new(() =>
    {
        using JsonDocument file = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Folder, "ardent.json")));
        return file.RootElement.EnumerateObject().ToDictionary(answer => answer.Name, answer => answer.Value.GetRawText());
    });

    static readonly Lazy<(JsonNode Question, string Page)[]> Pages = new(() =>
        [.. JsonNode.Parse(File.ReadAllBytes(Path.Combine(Folder, "spansh.json")))!.AsArray().Select(page => (page!["request"]!, page["answer"]!.ToJsonString()))]);

    readonly Lock gate = new();
    int underWay;

    /// <summary>Every request that reached the "network": its address, and for Spansh "POST address body".</summary>
    public List<string> Asked { get; } = [];

    public List<string> NotRecorded { get; } = [];

    public HashSet<string> UserAgents { get; } = [];

    /// <summary>The most requests that were under way at the same moment.</summary>
    public int MostAtOnce { get; private set; }

    /// <summary>How long every recorded answer takes.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>A test's own answer to a request; without one, or when it gives null, the recording answers.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Instead { get; set; }

    /// <summary>The recorded answer to a request address, as the service sent it; null when that was not recorded.</summary>
    public static string? Recorded(string address) => Answers.Value.GetValueOrDefault(address);

    /// <summary>The rows of a recorded answer.</summary>
    public static JsonElement[] Rows(string address)
    {
        using JsonDocument answer = JsonDocument.Parse(Answers.Value[address]);
        return [.. answer.RootElement.EnumerateArray().Select(row => row.Clone())];
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A market that answers with <paramref name="answer"/>'s result; where that is null the recording answers.</summary>
    public static RecordedMarket Answering(Func<string, HttpResponseMessage?> answer) =>
        new() { Instead = (request, _) => Task.FromResult(answer(request.RequestUri!.OriginalString)) };

    /// <summary>A market with the recorded price overview and nothing else: every list of offers comes back empty.</summary>
    public static RecordedMarket Empty() => Answering(address => address == Overview ? null : Json("[]"));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken stop)
    {
        string address = request.RequestUri!.OriginalString;
        string? posted = request.Content is null ? null : await request.Content.ReadAsStringAsync(stop);
        string asked = posted is null ? address : $"POST {address} {posted}";
        lock (gate)
        {
            Asked.Add(asked);
            UserAgents.Add(request.Headers.UserAgent.ToString());
            MostAtOnce = Math.Max(MostAtOnce, ++underWay);
        }
        try
        {
            if (Instead is not null && await Instead(request, stop) is { } own) return own;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, stop);
            if (posted is null && Answers.Value.TryGetValue(address, out string? answer)) return Json(answer);
            if (posted is not null && Pages.Value.FirstOrDefault(page => JsonNode.DeepEquals(page.Question, JsonNode.Parse(posted))).Page is { } found) return Json(found);
            lock (gate) NotRecorded.Add(asked);
            return Json("{\"error\":\"Not Found\"}", HttpStatusCode.NotFound);
        }
        finally
        {
            lock (gate) underWay--;
        }
    }
}

/// <summary>
/// What the Haul Overlay's own search answered. old-tool.json was made by running the search code
/// of overlay.ps1 (copied in memory, its clock held at the recording time) against the very answers
/// in ardent.json and spansh.json, once per request listed there. Its rows are the old tool's raw
/// search values plus the three texts its window added (commodity name, return name, age).
/// </summary>
static class OldTool
{
    static readonly JsonElement Recorded = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "market", "old-tool.json"))).RootElement;
    static readonly string[] Columns = [.. Recorded.GetProperty("columns").EnumerateArray().Select(column => column.GetString()!)];
    static readonly JsonSerializerOptions Loose = new(JsonSerializerDefaults.Web);

    /// <summary>The moment the answers were recorded: the clock every test search runs on.</summary>
    public static DateTime RecordedUtc { get; } = Recorded.GetProperty("recordedUtc").GetDateTime().ToUniversalTime();

    public static IEnumerable<string> Names => Recorded.GetProperty("scenarios").EnumerateArray().Select(scenario => scenario.GetProperty("name").GetString()!);

    public static SearchRequest Request(string name) => Scenario(name).GetProperty("request").Deserialize<SearchRequest>(Loose)!;

    public static JsonElement Result(string name) => Scenario(name).GetProperty("result");

    /// <summary>One trade of the old tool as one line of text, every value in a fixed form, to compare with <see cref="Line(TradeRow)"/>.</summary>
    public static string Line(JsonElement row) => string.Join("; ", Columns.Zip(row.EnumerateArray(), (column, value) => column + "=" + (
        column is "BuyPad" or "SellPad" ? value.GetInt32() switch { 1 => "S", 2 => "M", 3 => "L", _ => "" }
        : value.ValueKind == JsonValueKind.Number ? Number(value.GetDouble())
        : value.ValueKind == JsonValueKind.String ? value.GetString()
        : value.ValueKind == JsonValueKind.True ? "True" : value.ValueKind == JsonValueKind.False ? "False" : "")));

    public static string Line(TradeRow row) => string.Join("; ", Columns.Select(column => column + "=" + (
        typeof(TradeRow).GetProperty(column)!.GetValue(row) switch
        {
            string text => text,
            bool flag => flag ? "True" : "False",
            object number => Number(Convert.ToDouble(number, CultureInfo.InvariantCulture)),
            null => "",
        })));

    /// <summary>One value out of a line made by <see cref="Line(TradeRow)"/>.</summary>
    public static string Value(string line, string column) =>
        line.Split("; ").First(value => value.StartsWith(column + "=", StringComparison.Ordinal))[(column.Length + 1)..];

    // Six decimals: the two runtimes work out hours from ticks in slightly different ways, which shows in the last digit.
    static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    static JsonElement Scenario(string name) => Recorded.GetProperty("scenarios").EnumerateArray().First(scenario => scenario.GetProperty("name").GetString() == name);
}

/// <summary>
/// The trade search against recorded answers of the real services, compared with what the Haul
/// Overlay's own search made of the same answers; then every promise the search makes about
/// filters, memory, failures, stopping and good manners.
/// </summary>
public class TradeSearchTests
{
    const string NearByHour = "near, round trip, best per hour", NearOneWay = "near, one way", NearPowerplay = "near, Powerplay filters";
    const string Anywhere = "anywhere, round trip";
    const string LookupCargo = "lookup, cargo", LookupLive = "lookup, cargo, docked with a live price list", LookupGold = "lookup, carriers and small pads";
    const string GoldAround = RecordedMarket.Ardent + "/system/name/LHS%203447/commodity/name/gold/nearby/imports?maxDistance=40&maxDaysAgo=2&minVolume=1";
    const string Stopped = "The search was stopped.";

    static readonly CancellationToken Never = CancellationToken.None;

    public static TheoryData<string> Scenarios => new(OldTool.Names);

    /// <summary>A search on the recording's clock, asking through a client of its own as the app's asks through the app's one.</summary>
    /// <param name="timeoutSeconds">How long one request may take; the client's usual 60 seconds when left out.</param>
    static TradeSearch Search(RecordedMarket market, double timeoutSeconds = 60) =>
        new(new MarketData(market) { RequestTimeout = TimeSpan.FromSeconds(timeoutSeconds) }, () => OldTool.RecordedUtc);

    // ---------------------------------------------------------------- the same answers as the old tool

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Finds_what_the_haul_overlay_found(string scenario)
    {
        var market = new RecordedMarket();
        TradeSearch search = Search(market);
        SearchRequest request = OldTool.Request(scenario);

        SearchResult found = await search.Run(request, Never);

        JsonElement old = OldTool.Result(scenario);
        Assert.Empty(market.NotRecorded);
        Assert.True(found.Ok, found.Error);
        Assert.Null(found.Error);
        Assert.Equal(request.StarSystem, found.StarSystem);
        Assert.Equal(old.GetProperty("lookup").GetBoolean(), found.Lookup);
        Assert.Equal(old.GetProperty("note").GetString(), found.Note);
        Assert.Equal((old.GetProperty("exports").GetInt32(), old.GetProperty("scanned").GetInt32(), old.GetProperty("candidates").GetInt32()),
            (found.Exports, found.Scanned, found.Candidates));
        // the same requests, each once; the old tool starts them in no fixed order
        Assert.Equal(old.GetProperty("urls").EnumerateArray().Select(url => url.GetString()).Order(), found.Urls.Order());
        Assert.Equal(market.Asked.Count, found.Urls.Count);
        Assert.Equal(Enumerable.Range(1, found.Trades.Count), found.Trades.Select(trade => trade.Rank));
        Assert.All(found.Trades, trade => Assert.False(trade.BuyHere));   // the hauling state fills that in

        // The same trades with the same numbers, in the order the search promises. That is not always
        // line for line the old tool's list: many stations pay exactly the same, and the old tool's
        // sort (PowerShell's Sort-Object) puts equals in no particular order. So first: ours are in order.
        bool lookup = request.Mode == "lookup", byTrip = request.RankBy == "trip";
        IEnumerable<TradeRow> inOrder =
            lookup && request.LookRank == "profit" ? found.Trades.OrderByDescending(trade => trade.ProfitPerTrip).ThenByDescending(trade => trade.ProfitPerTon).ThenBy(trade => trade.DistanceLy)
            : lookup ? found.Trades.OrderBy(trade => trade.Units < request.LookCount).ThenBy(trade => trade.JumpsOut).ThenByDescending(trade => trade.SellPrice).ThenBy(trade => trade.AgeHours)
            : byTrip ? found.Trades.OrderByDescending(trade => trade.RoundTripProfit).ThenByDescending(trade => trade.ProfitPerTon).ThenByDescending(trade => trade.PerHour)
            : found.Trades.OrderByDescending(trade => trade.PerHour).ThenByDescending(trade => trade.ProfitPerTon);
        Assert.Equal(inOrder.Select(trade => trade.Rank), found.Trades.Select(trade => trade.Rank));
        // Then: they are the old tool's trades, every one with every number.
        List<string> onlyOld = [.. old.GetProperty("trades").EnumerateArray().Select(OldTool.Line)], onlyOurs = [];
        Assert.Equal(onlyOld.Count, found.Trades.Count);
        foreach (string ours in found.Trades.Select(OldTool.Line))
            if (!onlyOld.Remove(ours)) onlyOurs.Add(ours);
        // One more thing is left to chance in the old tool: ranking by trip, the same load sold to another
        // station at the same price earns the same, and it kept whichever its sort put first. Studio keeps the quickest.
        static string Deal(string line) => string.Join("|", ((string[])["CommodityId", "BuyMarketId", "BuyPrice", "SellPrice", "Units", "ProfitPerTrip"]).Select(column => OldTool.Value(line, column)));
        static long PerHour(string line) => long.Parse(OldTool.Value(line, "PerHour"), CultureInfo.InvariantCulture);
        foreach (string theirs in byTrip && !lookup ? onlyOld.ToArray() : [])
        {
            if (onlyOurs.FirstOrDefault(ours => Deal(ours) == Deal(theirs) && PerHour(ours) >= PerHour(theirs)) is not { } quicker) continue;
            onlyOld.Remove(theirs);
            onlyOurs.Remove(quicker);
        }
        Assert.Equal(onlyOld, onlyOurs);   // both empty
    }

    [Fact]
    public async Task The_best_trade_adds_up()
    {
        TradeSearch search = Search(new RecordedMarket());
        SearchResult found = await search.Run(OldTool.Request(NearByHour), Never);

        Assert.Equal(40, found.Trades.Count);   // the list stops at 40; these answers hold more trades than that
        TradeRow best = found.Trades[0];
        Assert.Equal(("bauxite", "Bauxite", 1), (best.CommodityId, best.Commodity, best.Rank));
        Assert.Equal(("Roddenberry City", "Alrai Sector LC-V b2-4", "Coriolis", "L", 2476, 559), (best.BuyStation, best.BuySystem, best.BuyType, best.BuyPad, best.BuyPrice, best.Stock));
        Assert.Equal(("Wohler Terminal", "Kremainn", "Coriolis", "L", 14073, 902598), (best.SellStation, best.SellSystem, best.SellType, best.SellPad, best.SellPrice, best.Demand));
        Assert.Equal((4234296835, 3230458112), (best.BuyMarketId, best.SellMarketId));
        Assert.Equal(15, best.BuyDistanceLy);               // the service's whole light years from LHS 3447
        Assert.Equal(18.6, best.DistanceLy);                // buy system to sell system
        Assert.Equal(11597, best.ProfitPerTon);             // 14,073 - 2,476
        Assert.Equal(404, best.Units);                      // the whole hold: 559 t in stock, 902,598 t wanted
        Assert.Equal(11597L * 404, best.ProfitPerTrip);
        Assert.Equal(("silver", "Silver", 7582, 404, 7582L * 404), (best.ReturnId, best.Return, best.ReturnProfitPerTon, best.ReturnUnits, best.ReturnProfit));
        Assert.Equal(11597L * 404 + 7582L * 404, best.RoundTripProfit);
        Assert.Equal((2, 2, 0), (best.JumpsOut, best.JumpsBack, best.JumpsToBuy));   // 18.6 ly at 14.7 ly a jump, full both ways
        // four jumps, supercruise to both stations (2,846 ls and 4.6 ls from their stars), two stops
        double seconds = 4 * 45 + ShipMath.SupercruiseSeconds(2846.016961) + ShipMath.SupercruiseSeconds(4.555892) + 180;
        Assert.Equal(Math.Round(seconds / 60, 1), best.Minutes);
        Assert.Equal(9.9, best.Minutes);
        Assert.Equal(46884647, best.PerHour);
        Assert.Equal(Convert.ToInt64(best.RoundTripProfit / seconds * 3600), best.PerHour);
        Assert.Equal("30h ago", best.Updated);              // the older of the two prices
        Assert.False(best.OneWay);
    }

    [Fact]
    public async Task The_list_is_ranked_by_hour_or_by_trip_with_three_trades_a_commodity()
    {
        TradeSearch search = Search(new RecordedMarket());
        SearchResult byHour = await search.Run(OldTool.Request(NearByHour), Never);
        SearchResult byTrip = await search.Run(OldTool.Request(NearByHour) with { RankBy = "trip" }, Never);

        Assert.Equal(byHour.Trades.OrderByDescending(trade => trade.PerHour).Select(trade => trade.PerHour), byHour.Trades.Select(trade => trade.PerHour));
        Assert.Equal(byTrip.Trades.OrderByDescending(trade => trade.RoundTripProfit).Select(trade => trade.RoundTripProfit), byTrip.Trades.Select(trade => trade.RoundTripProfit));
        Assert.NotEqual(byHour.Trades.Select(trade => trade.PerHour), byTrip.Trades.Select(trade => trade.PerHour));
        foreach (SearchResult list in (SearchResult[])[byHour, byTrip])
        {
            Assert.Equal(40, list.Trades.Count);
            Assert.All(list.Trades.GroupBy(trade => trade.CommodityId), commodity => Assert.InRange(commodity.Count(), 1, 3));
            Assert.All(list.Trades, trade => Assert.True(trade.DistanceLy <= 30 && trade.Stock >= 100 && trade.Demand >= 100 && trade.AgeHours <= 48));
        }
    }

    [Fact]
    public async Task One_way_flies_nothing_back()
    {
        var market = new RecordedMarket();
        TradeSearch search = Search(market);
        SearchResult oneWay = await search.Run(OldTool.Request(NearOneWay), Never);
        Assert.DoesNotContain(market.Asked, asked => asked.Contains("/commodities/imports"));   // no station is asked what it could send back
        SearchResult round = await search.Run(OldTool.Request(NearByHour), Never);
        Assert.Contains(market.Asked, asked => asked.Contains("/commodities/imports"));

        Assert.All(oneWay.Trades, trade =>
        {
            Assert.True(trade.OneWay);
            Assert.Equal(("", "", 0, 0, 0L), (trade.ReturnId, trade.Return, trade.ReturnProfitPerTon, trade.ReturnUnits, trade.ReturnProfit));
            Assert.Equal(0, trade.JumpsBack);
            Assert.Equal(trade.ProfitPerTrip, trade.RoundTripProfit);
            // the clock stops when the cargo is sold: jumps out, supercruise at the selling end, and both stops
            double seconds = trade.JumpsOut * 45 + ShipMath.SupercruiseSeconds(trade.SellStarLs) + 180;
            Assert.Equal(Math.Round(seconds / 60, 1), trade.Minutes);
            Assert.Equal(Convert.ToInt64(trade.ProfitPerTrip / seconds * 3600), trade.PerHour);
        });
        Assert.Contains(round.Trades, trade => trade.ReturnProfit > 0 && trade.JumpsBack > 0 && !trade.OneWay);
        // the same trade both ways: one way is quicker, and the load back is what the round trip earns on top
        TradeRow there = oneWay.Trades.First(trade => round.Trades.Any(trade.IsSameTrade));
        TradeRow andBack = round.Trades.First(there.IsSameTrade);
        Assert.True(there.Minutes < andBack.Minutes);
        Assert.Equal(there.ProfitPerTrip + andBack.ReturnProfit, andBack.RoundTripProfit);
    }

    [Fact]
    public async Task A_ship_that_is_not_known_gets_no_tons_no_jumps_and_a_list_by_profit_per_ton()
    {
        TradeSearch search = Search(new RecordedMarket());
        SearchResult found = await search.Run(OldTool.Request(NearByHour) with { Cargo = 0, Jump = 0, JumpEmpty = 0 }, Never);

        Assert.Equal(40, found.Trades.Count);
        Assert.All(found.Trades, trade => Assert.Equal((0, 0L, 0L, 0L, 0, 0), (trade.Units, trade.ProfitPerTrip, trade.RoundTripProfit, trade.PerHour, trade.JumpsOut, trade.JumpsBack)));
        Assert.Equal(found.Trades.OrderByDescending(trade => trade.ProfitPerTon).Select(trade => trade.ProfitPerTon), found.Trades.Select(trade => trade.ProfitPerTon));
        // the time is still estimated, with 12 light years a jump
        TradeRow first = found.Trades[0];
        double seconds = 2 * ShipMath.JumpsFor(first.DistanceLy, 12) * 45 + ShipMath.SupercruiseSeconds(first.SellStarLs) + ShipMath.SupercruiseSeconds(first.BuyStarLs) + 180;
        Assert.Equal(Math.Round(seconds / 60, 1), first.Minutes);
    }

    /// <summary>
    /// Three limits of a "near" search that the recordings are too small to reach, on a made-up
    /// market: 30 metals that earn well everywhere, and for one of them 30 stations that buy it and
    /// 8 systems that sell it. The search looks closely at 28 commodities, buys in the 6 systems
    /// where a full hold earns most, and pairs each of those with the 25 stations that pay best.
    /// </summary>
    [Fact]
    public async Task A_near_search_stops_at_28_commodities_6_systems_to_buy_in_and_25_buyers_for_each()
    {
        string updated = OldTool.RecordedUtc.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        int id = 0;
        string Station(string name, string system, int x, int starLs, int sells = 0, int pays = 0) =>
            $$"""{"commodityName":"metal01","marketId":{{++id}},"stationName":"{{name}}","stationType":"Coriolis","distanceToArrival":{{starLs}},"maxLandingPadSize":3,"systemName":"{{system}}","systemX":{{x}},"systemY":0,"systemZ":0,"buyPrice":{{sells}},"stock":{{(sells > 0 ? 5000 : 0)}},"sellPrice":{{pays}},"demand":{{(pays > 0 ? 5000 : 0)}},"meanPrice":30000,"updatedAt":"{{updated}}","distance":10}""";
        static string List(IEnumerable<string> rows) => "[" + string.Join(",", rows) + "]";

        // Thirty metals, each a little less valuable than the one before.
        string overview = List(Enumerable.Range(1, 30).Select(n =>
            $$"""{"commodityName":"metal{{n:00}}","avgSellPrice":{{50000 - 100 * n}},"maxSellPrice":60000,"minBuyPrice":1000,"avgBuyPrice":30000,"totalStock":1000000,"totalDemand":1000000}"""));
        // Thirty buyers in one system, each paying 10 cr less than the one before. All sit far from
        // their star but numbers 25 and 26, which are next to it: a much quicker trip.
        string buyers = List(Enumerable.Range(1, 30).Select(n => Station($"Buyer {n:00}", "Hub", x: 0, starLs: n is 25 or 26 ? 10 : 4000, pays: 60000 - 10 * n)));
        // Eight systems that sell it, each 10 cr dearer than the one before. Mine 1 to 5 are two
        // jumps from the buyers, Mine 6 to 8 only one.
        string mines = List(Enumerable.Range(1, 8).Select(n => Station($"Mine {n}", $"Lode {n}", x: n <= 5 ? 25 : 10, starLs: 100, sells: 1000 + 10 * n)));
        TradeSearch search = Search(RecordedMarket.Answering(address => RecordedMarket.Json(
            address == RecordedMarket.Overview ? overview
            : address.Contains("/commodity/name/metal01/nearby/imports?") ? buyers
            : address.Contains("/commodity/name/metal01/nearby/exports?") ? mines
            : "[]")));

        SearchResult found = await search.Run(new SearchRequest { StarSystem = "LHS 3447", Cargo = 100, Jump = 20, JumpEmpty = 20, Returns = false }, Never);

        Assert.True(found.Ok, found.Error);
        Assert.Equal((30, 28), (found.Scanned, found.Candidates));   // all thirty are asked about; the 28 best are looked at closely
        // Best per hour is the quick trip from the nearest mine. Mine 6 is the last of the six where a hold
        // earns most, and Buyer 25 the last of the 25 that pay best. One more of either (Mine 7, also one
        // jump away; Buyer 26, also next to its star) would be the second-best trade, and is not in the list.
        Assert.Equal(
            [("Mine 6", "Buyer 25", 1), ("Mine 1", "Buyer 25", 2), ("Mine 2", "Buyer 25", 2)],
            found.Trades.Select(trade => (trade.BuyStation, trade.SellStation, trade.JumpsOut)));
    }

    // ---------------------------------------------------------------- station filters

    /// <summary>
    /// Each station filter against the 88 real gold buyers within 40 light years of LHS 3447: the
    /// stations a rule is about are in the recorded answer, and none of them reaches the list.
    /// </summary>
    [Fact]
    public async Task Each_station_filter_takes_out_the_stations_it_is_about()
    {
        JsonElement[] rows = RecordedMarket.Rows(GoldAround);
        TradeSearch search = Search(new RecordedMarket());
        // everything allowed, best price first (one ton to sell, nothing paid)
        SearchRequest open = OldTool.Request(LookupGold) with { Carriers = true, Pad = 1, Surface = "odyssey", MaxStarLs = 0, MaxAgeHours = 48, LookCount = 1 };

        static string Type(JsonElement row) => row.GetProperty("stationType").GetString()!;
        static double HoursOld(JsonElement row) => (OldTool.RecordedUtc - row.GetProperty("updatedAt").GetDateTime().ToUniversalTime()).TotalHours;
        async Task<IReadOnlyList<TradeRow>> Without(SearchRequest request, int about, Func<JsonElement, bool> rule)
        {
            long[] stations = [.. rows.Where(rule).Select(row => row.GetProperty("marketId").GetInt64())];
            Assert.Equal(about, stations.Length);
            SearchResult found = await search.Run(request, Never);
            Assert.NotEmpty(found.Trades);
            Assert.DoesNotContain(found.Trades, trade => stations.Contains(trade.SellMarketId));
            return found.Trades;
        }

        IReadOnlyList<TradeRow> all = await Without(open, 11, row => row.GetProperty("demand").GetInt32() < 1);   // "demand 0" comes back whatever volume is asked for
        Assert.Equal(40, all.Count);
        Assert.Contains(all, trade => trade.SellType == "OnFootSettlement");
        Assert.Contains(all, trade => trade.SellType == "CraterOutpost");
        Assert.Contains(all, trade => trade.SellPad == "M");
        Assert.Contains(all, trade => trade.SellStarLs > 5000);

        await Without(open with { Pad = 3 }, 9, row => row.GetProperty("maxLandingPadSize").GetInt32() < 3);
        await Without(open with { MaxStarLs = 500 }, 50, row => row.GetProperty("distanceToArrival").GetDouble() > 500);
        await Without(open with { MaxAgeHours = 26 }, 10, row => HoursOld(row) > 26);
        IReadOnlyList<TradeRow> horizons = await Without(open with { Surface = "horizons" }, 1, row => Type(row) == "OnFootSettlement");
        Assert.Contains(horizons, trade => trade.SellType == "CraterOutpost");   // a port on the ground is not a settlement
        await Without(open with { Surface = "none" }, 3, row => Type(row) is "OnFootSettlement" or "CraterOutpost");

        // A Powerplay stronghold carrier is no fleet carrier: it trades whatever the carrier setting.
        // (The one fleet carrier in this answer wants no gold; the rule itself is in the next test.)
        IReadOnlyList<TradeRow> noCarriers = (await search.Run(open with { Carriers = false }, Never)).Trades;
        Assert.Contains(noCarriers, trade => trade.SellType == "StrongholdCarrier");
        Assert.DoesNotContain(noCarriers, trade => trade.SellType == "FleetCarrier");
    }

    /// <summary>
    /// The filter rule by rule, on rows written by hand in the service's layout. The recorded
    /// answers cannot show every rule: their one fleet carrier and the one real building site
    /// found near LHS 3447 both report "demand 0", every station in them has a pad size, and
    /// every price has a date.
    /// </summary>
    [Fact]
    public async Task The_station_filter_rule_by_rule()
    {
        int id = 0;
        string Row(string station, string type = "Coriolis", string pad = "3", string starLs = "100", double hoursOld = 1, int sells = 5000, int demand = 500, bool nearby = true, int? market = null)
        {
            string updated = double.IsNaN(hoursOld) ? "null" : '"' + OldTool.RecordedUtc.AddHours(-hoursOld).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) + '"';
            string distance = nearby ? ""","distance":4""" : "";   // only "nearby" answers say how far it is
            return $$"""{"commodityName":"gold","marketId":{{market ?? ++id}},"stationName":"{{station}}","stationType":"{{type}}","distanceToArrival":{{starLs}},"maxLandingPadSize":{{pad}},"systemName":"Eravate","systemX":-42.4375,"systemY":-3.15625,"systemZ":59.65625,"buyPrice":0,"demand":{{demand}},"meanPrice":47000,"sellPrice":{{sells}},"stock":0,"updatedAt":{{updated}}{{distance}}}""";
        }
        string around = "[" + string.Join(",",
            Row("Port"), Row("Space building site", "SpaceConstructionDepot"), Row("Ground building site", "PlanetaryConstructionDepot"),
            Row("Carrier", "FleetCarrier"), Row("Stronghold", "StrongholdCarrier"), Row("Megaship", "MegaShip"),
            Row("Settlement", "OnFootSettlement"), Row("Crater port", "CraterPort"), Row("Crater outpost", "CraterOutpost"), Row("Surface station", "SurfaceStation"),
            Row("Outpost", "Outpost", pad: "2"), Row("Small pads", pad: "1"), Row("Pad not known", pad: "null"),
            Row("Far out", starLs: "1000.5"), Row("Distance not known", starLs: "null"),
            Row("No date", hoursOld: double.NaN), Row("Two days old", hoursOld: 48), Row("Older", hoursOld: 48.01),
            Row("Wants nothing", demand: 0), Row("Pays nothing", sells: 0)) + ",null]";
        // the system's own stations: one the first answer listed already (it is taken once, as listed there), and one that is new
        string inSystem = "[" + Row("Port", sells: 9999, nearby: false, market: 1) + "," + Row("Home port", nearby: false) + "]";

        async Task<IReadOnlyList<TradeRow>> Listed(SearchRequest request)
        {
            TradeSearch search = Search(RecordedMarket.Answering(address =>
                address == RecordedMarket.Overview ? null : RecordedMarket.Json(address.Contains("/nearby/imports?") ? around : inSystem)));
            SearchResult found = await search.Run(request, Never);
            Assert.True(found.Ok, found.Error);
            return found.Trades;
        }
        async Task<string[]> Kept(SearchRequest request) => [.. (await Listed(request)).Select(trade => trade.SellStation).Order(StringComparer.Ordinal)];
        static string[] Sorted(params string[] stations) => [.. stations.Order(StringComparer.Ordinal)];

        SearchRequest plain = OldTool.Request(LookupGold) with { Carriers = false, Pad = 2, Surface = "odyssey", MaxStarLs = 1000, MaxAgeHours = 48, LookCount = 1 };
        string[] orbiting = ["Home port", "Port", "Megaship", "Stronghold", "Outpost", "Distance not known", "Two days old"];
        string[] onTheGround = ["Crater port", "Crater outpost", "Surface station"];

        Assert.Equal(Sorted([.. orbiting, .. onTheGround, "Settlement"]), await Kept(plain));
        Assert.Equal(Sorted([.. orbiting, .. onTheGround, "Settlement", "Carrier"]), await Kept(plain with { Carriers = true }));
        Assert.Equal(Sorted([.. orbiting, .. onTheGround]), await Kept(plain with { Surface = "horizons" }));   // settlements need Odyssey
        Assert.Equal(Sorted(orbiting), await Kept(plain with { Surface = "none" }));
        Assert.Equal(Sorted([.. orbiting.Except(["Outpost"]), .. onTheGround, "Settlement"]), await Kept(plain with { Pad = 3 }));
        // only a small ship may try a pad nobody measured
        Assert.Equal(Sorted([.. orbiting, .. onTheGround, "Settlement", "Small pads", "Pad not known"]), await Kept(plain with { Pad = 1 }));
        Assert.Equal(Sorted([.. orbiting, .. onTheGround, "Settlement", "Far out"]), await Kept(plain with { MaxStarLs = 0 }));   // 0 = any distance
        Assert.Contains("Far out", await Kept(plain with { MaxStarLs = 1000.5 }));
        Assert.Equal(Sorted([.. orbiting, .. onTheGround, "Settlement", "Older"]), await Kept(plain with { MaxAgeHours = 49 }));
        Assert.DoesNotContain("Two days old", await Kept(plain with { MaxAgeHours = 47.9 }));
        // With every filter wide open: no building site (they take deliveries, they do not trade), no price
        // without a date, and nobody who wants nothing or pays nothing.
        Assert.Equal(
            Sorted([.. orbiting, .. onTheGround, "Settlement", "Carrier", "Small pads", "Pad not known", "Far out", "Older"]),
            await Kept(plain with { Carriers = true, Pad = 1, MaxStarLs = 0, MaxAgeHours = 720 }));

        IReadOnlyList<TradeRow> listed = await Listed(plain);
        TradeRow port = Assert.Single(listed, trade => trade.SellStation == "Port");
        Assert.Equal((5000, 4.0, 1), (port.SellPrice, port.DistanceLy, port.JumpsOut));
        TradeRow home = Assert.Single(listed, trade => trade.SellStation == "Home port");
        Assert.Equal((0.0, 0), (home.DistanceLy, home.JumpsOut));   // in the system the player is in
        Assert.Equal("", Assert.Single(await Listed(plain with { Pad = 1 }), trade => trade.SellStation == "Pad not known").SellPad);
    }

    [Fact]
    public async Task Supply_and_demand_limits_are_applied_here_whatever_the_service_sends()
    {
        // Asked for 50,000 tons on sale and 5,000 wanted, the service answers with its lists for 100 or more.
        // (In real life it lets every "demand 0" through, so its own volume filter cannot be relied on.)
        TradeSearch search = Search(RecordedMarket.Answering(address =>
            RecordedMarket.Recorded(address.Replace("minVolume=50000", "minVolume=100").Replace("minVolume=5000", "minVolume=100")) is { } forHundred ? RecordedMarket.Json(forHundred) : null));
        TradeSearch usual = Search(new RecordedMarket());

        SearchResult plenty = await search.Run(OldTool.Request(NearByHour) with { MinSupply = 50000, MinDemand = 5000 }, Never);
        SearchResult hundred = await usual.Run(OldTool.Request(NearByHour), Never);

        Assert.NotEmpty(plenty.Trades);
        Assert.All(plenty.Trades, trade => Assert.True(trade.Stock >= 50000 && trade.Demand >= 5000));
        Assert.Contains(hundred.Trades, trade => trade.Stock < 50000);
        Assert.Contains(hundred.Trades, trade => trade.Demand < 5000);
        Assert.Equal(5, plenty.Exports);    // of the six commodities on sale in LHS 3447, tobacco has only 15,031 t
        Assert.Equal(6, hundred.Exports);
        Assert.Contains(plenty.Urls, url => url.Contains("/nearby/imports?") && url.Contains("&minVolume=5000&minPrice="));
        Assert.Contains(plenty.Urls, url => url.Contains("/nearby/exports?") && url.Contains("&minVolume=50000&maxPrice="));

        // "Any" still means at least one ton
        TradeSearch any = Search(RecordedMarket.Empty());
        SearchResult scan = await any.Run(OldTool.Request(NearByHour) with { MinSupply = 0, MinDemand = 0 }, Never);
        Assert.Equal(21, scan.Urls.Count);
        Assert.All(scan.Urls.Skip(2), url => Assert.Contains("&minVolume=1&minPrice=", url));
    }

    // ---------------------------------------------------------------- "where can I sell this?"

    [Fact]
    public async Task A_lookup_takes_the_docked_stations_prices_from_the_game_not_from_the_service()
    {
        TradeSearch search = Search(new RecordedMarket());
        SearchRequest docked = OldTool.Request(LookupLive);   // stations up to 1,000 ls from the star; paid 6,900 cr a ton
        Assert.Equal(128673074, docked.LiveMarket!.MarketId);

        SearchResult withList = await search.Run(docked, Never);
        SearchResult without = await search.Run(docked with { LiveMarket = null }, Never);
        SearchResult farStations = await search.Run(docked with { LiveMarket = null, MaxStarLs = 5000 }, Never);

        // Bluford Orbital is 2,114 ls out. Docked there it is listed all the same, with what its own screen says.
        TradeRow here = Assert.Single(withList.Trades, trade => trade.SellStation == "Bluford Orbital");
        Assert.Equal((7711, 39000, 811, 404, 811L * 404), (here.SellPrice, here.Demand, here.ProfitPerTon, here.Units, here.ProfitPerTrip));
        Assert.Equal((0.0, "just now", 0.0, 0), (here.AgeHours, here.Updated, here.DistanceLy, here.JumpsOut));
        Assert.Equal(("Your cargo hold", "LHS 3447", 6900, 404, "", 0L), (here.BuyStation, here.BuySystem, here.BuyPrice, here.Stock, here.BuyPad, here.BuyMarketId));
        Assert.Equal(1, here.Rank);   // takes the whole load and needs no jump
        // Not docked: the filter hides it; with far stations allowed it shows the price last uploaded.
        Assert.DoesNotContain(without.Trades, trade => trade.SellStation == "Bluford Orbital");
        Assert.Equal(withList.Trades.Count, without.Trades.Count + 1);
        TradeRow uploaded = Assert.Single(farStations.Trades, trade => trade.SellStation == "Bluford Orbital");
        Assert.Equal((7566, 43746, 666), (uploaded.SellPrice, uploaded.Demand, uploaded.ProfitPerTon));
        Assert.Equal("2h ago", uploaded.Updated);

        Assert.All(withList.Trades.Concat(farStations.Trades), trade =>
        {
            Assert.True(trade.SellPrice > 6900);   // only places that pay more than was paid
            Assert.Equal(("consumertechnology", "Consumer Technology", 0L, "", false), (trade.CommodityId, trade.Commodity, trade.PerHour, trade.ReturnId, trade.OneWay));
            Assert.Equal(Math.Round((trade.JumpsOut * 45 + ShipMath.SupercruiseSeconds(trade.SellStarLs) + 90) / 60, 1), trade.Minutes);
        });
        // cargo: places that take the whole load come first, then the fewest jumps
        Assert.Equal(farStations.Trades.OrderBy(trade => trade.Units < 404).ThenBy(trade => trade.JumpsOut).Select(trade => trade.SellMarketId), farStations.Trades.Select(trade => trade.SellMarketId));
        // two questions on top of the price overview, whatever the filters; asked once for all three searches
        Assert.Equal(3, withList.Urls.Count);
        Assert.Empty(without.Urls.Concat(farStations.Urls));
    }

    [Fact]
    public async Task A_picked_commodity_is_listed_by_what_the_load_earns()
    {
        TradeSearch search = Search(new RecordedMarket());
        SearchResult found = await search.Run(OldTool.Request(LookupGold) with { LookPaid = 0, LookCount = 404 }, Never);

        Assert.True(found.Lookup);
        Assert.Equal(found.Trades.OrderByDescending(trade => trade.ProfitPerTrip).ThenByDescending(trade => trade.ProfitPerTon).Select(trade => trade.SellMarketId), found.Trades.Select(trade => trade.SellMarketId));
        Assert.All(found.Trades, trade => Assert.Equal((trade.SellPrice, Math.Min(404, trade.Demand)), (trade.ProfitPerTon, trade.Units)));   // nothing paid: the price is the profit
        Assert.Equal(
            [RecordedMarket.Overview, GoldAround, RecordedMarket.Ardent + "/system/name/LHS%203447/commodity/name/gold?maxDaysAgo=2"],
            found.Urls);
    }

    // ---------------------------------------------------------------- names

    [Fact]
    public async Task The_price_overview_says_which_commodities_are_traded_under_the_games_own_names()
    {
        TradeSearch search = Search(new RecordedMarket());
        Assert.Empty(search.Commodities);

        await search.Run(OldTool.Request(LookupCargo), Never);

        // every commodity somebody wants, by name; "ancientrelictg" is wanted nowhere
        Assert.Equal(22, search.Commodities.Count);
        Assert.Equal(("agronomictreatment", "Agronomic Treatment"), search.Commodities[0]);
        Assert.Equal(search.Commodities.OrderBy(commodity => commodity.Name, StringComparer.OrdinalIgnoreCase), search.Commodities);
        Assert.Contains(("lowtemperaturediamond", "Low Temperature Diamonds"), search.Commodities);
        Assert.Contains(("platinum", "Platinum"), search.Commodities);                    // sold nowhere, wanted everywhere
        Assert.DoesNotContain(search.Commodities, commodity => commodity.Id == "ancientrelictg");
    }

    // ---------------------------------------------------------------- memory

    [Fact]
    public async Task Answers_are_remembered_for_minutes_and_remembered_answers_are_not_in_the_list_of_requests()
    {
        DateTime clock = OldTool.RecordedUtc;
        var market = new RecordedMarket();
        var search = new TradeSearch(new MarketData(market), () => clock);
        SearchRequest near = OldTool.Request(NearByHour);

        SearchResult first = await search.Run(near, Never);
        Assert.Equal(50, first.Urls.Count);
        Assert.Equal(first.Urls.Order(), market.Asked.Order());

        clock = OldTool.RecordedUtc.AddMinutes(9.9);
        SearchResult again = await search.Run(near, Never);
        Assert.Empty(again.Urls);
        Assert.Equal(50, market.Asked.Count);
        Assert.Equal(first.Trades.Select(trade => (trade.CommodityId, trade.BuyMarketId, trade.SellMarketId, trade.PerHour)), again.Trades.Select(trade => (trade.CommodityId, trade.BuyMarketId, trade.SellMarketId, trade.PerHour)));
        Assert.Equal((first.Exports, first.Scanned, first.Candidates), (again.Exports, again.Scanned, again.Candidates));

        // a lookup's questions are other questions, good for three minutes
        SearchResult lookup = await search.Run(OldTool.Request(LookupCargo), Never);
        Assert.Equal(2, lookup.Urls.Count);
        clock = clock.AddMinutes(2.9);
        Assert.Empty((await search.Run(OldTool.Request(LookupCargo), Never)).Urls);
        clock = clock.AddMinutes(0.2);
        Assert.Equal(lookup.Urls, (await search.Run(OldTool.Request(LookupCargo), Never)).Urls);

        // ten minutes after the first search the market is asked again; the price overview is good for twelve hours
        clock = OldTool.RecordedUtc.AddMinutes(10);
        SearchResult later = await search.Run(near, Never);
        Assert.DoesNotContain(RecordedMarket.Overview, later.Urls);
        Assert.Equal(first.Urls.Skip(1).Order(), later.Urls.Order());
        Assert.Empty(market.NotRecorded);

        clock = OldTool.RecordedUtc.AddHours(12);
        Assert.DoesNotContain(RecordedMarket.Overview, (await search.Run(near, Never)).Urls);
        clock = OldTool.RecordedUtc.AddHours(12.01);
        Assert.Equal(RecordedMarket.Overview, (await search.Run(near, Never)).Urls[0]);
    }

    [Fact]
    public async Task Powerplay_systems_are_remembered_for_two_hours()
    {
        DateTime clock = OldTool.RecordedUtc;
        var search = new TradeSearch(new MarketData(new RecordedMarket()), () => clock);
        SearchRequest powerplay = OldTool.Request(NearPowerplay);
        static int Pages(SearchResult result) => result.Urls.Count(url => url.StartsWith(RecordedMarket.Spansh, StringComparison.Ordinal));

        SearchResult first = await search.Run(powerplay, Never);
        Assert.Equal([RecordedMarket.Spansh + " (page 0)", RecordedMarket.Spansh + " (page 0)"], first.Urls.Skip(1).Take(2));   // where to buy, then where to sell
        Assert.Equal("Powerplay filter: 24 systems to buy in, 98 systems to sell in", first.Note);
        Assert.All(first.Trades, trade => Assert.StartsWith("Yuri Grom", trade.BuyPower));
        Assert.All(first.Trades, trade => Assert.EndsWith(", Fortified", trade.SellPower));
        Assert.Contains(first.Trades, trade => trade.BuyPower == "Yuri Grom, Exploited");

        clock = OldTool.RecordedUtc.AddMinutes(119);
        SearchResult later = await search.Run(powerplay, Never);
        Assert.Equal(0, Pages(later));
        Assert.Equal(first.Note, later.Note);
        Assert.NotEmpty(later.Urls);   // the market itself is asked again after ten minutes

        clock = OldTool.RecordedUtc.AddMinutes(120);
        Assert.Equal(2, Pages(await search.Run(powerplay, Never)));
    }

    // ---------------------------------------------------------------- Powerplay questions

    /// <summary>
    /// Spansh's side of the Powerplay filter on made-up pages, for what the two recorded pages
    /// cannot show: the question for "unoccupied", turning pages, and stopping after six of them.
    /// </summary>
    [Fact]
    public async Task Powerplay_asks_spansh_page_by_page_and_stops_at_six()
    {
        var questions = new List<(string To, string As, JsonNode Question)>();
        var market = new RecordedMarket
        {
            Instead = async (request, stop) =>
            {
                string address = request.RequestUri!.OriginalString;
                if (request.Method != HttpMethod.Post) return address == RecordedMarket.Overview ? null : RecordedMarket.Json("[]");
                JsonNode question = JsonNode.Parse(await request.Content!.ReadAsStringAsync(stop))!;
                lock (questions) questions.Add((address, request.Content.Headers.ContentType!.ToString(), question));
                int page = question["page"]!.GetValue<int>();
                return RecordedMarket.Json($$"""{"count":1203,"results":[{"name":"Made up {{page}} A","controlling_power":"Yuri Grom","power_state":"Exploited","bodies":[{"name":"skipped"}]},{"name":"Made up {{page}} B","power_state":"Unoccupied"},null]}""");
            },
        };
        TradeSearch search = Search(market);

        SearchResult found = await search.Run(OldTool.Request(NearByHour) with { SrcPower = "A. Lavigny-Duval", SrcState = "Unoccupied" }, Never);

        Assert.True(found.Ok, found.Error);
        Assert.Equal("Powerplay filter: 12 systems to buy in (nearest of 1203)", found.Note);
        Assert.Equal(Enumerable.Range(0, 6).Select(page => $"{RecordedMarket.Spansh} (page {page})"), found.Urls.Skip(1).Take(6));
        Assert.Equal(Enumerable.Range(0, 6), questions.Select(asked => asked.Question["page"]!.GetValue<int>()));
        Assert.All(questions, asked => Assert.Equal((RecordedMarket.Spansh, "application/json; charset=utf-8"), (asked.To, asked.As)));
        // "Unoccupied" asks for systems in range of the Power, not controlled by it; the distances go as text, as the old tool sent them
        Assert.True(JsonNode.DeepEquals(questions[0].Question, JsonNode.Parse(
            """{"filters":{"distance":{"min":"0","max":"15"},"power_state":{"value":["Unoccupied"]},"power":{"value":["A. Lavigny-Duval"]}},"reference_system":"LHS 3447","sort":[{"distance":{"direction":"asc"}}],"size":500,"page":0}""")), questions[0].Question.ToJsonString());
        // LHS 3447 is not among the systems to buy in, so what is on sale there is not even asked for
        Assert.Equal(0, found.Exports);
        Assert.DoesNotContain(found.Urls, url => url.Contains("/commodities/exports"));

        // A state that is missing altogether (a settings file can hold "srcState": null) counts as "any": the systems that Power controls.
        SearchResult noState = await search.Run(OldTool.Request(NearByHour) with { SrcPower = "A. Lavigny-Duval", SrcState = null! }, Never);
        Assert.True(noState.Ok, noState.Error);
        Assert.True(JsonNode.DeepEquals(questions[6].Question["filters"], JsonNode.Parse(
            """{"distance":{"min":"0","max":"15"},"controlling_power":{"value":["A. Lavigny-Duval"]}}""")), questions[6].Question.ToJsonString());
    }

    [Fact]
    public async Task Powerplay_filters_are_left_out_of_an_anywhere_search_and_say_so()
    {
        RecordedMarket market = RecordedMarket.Empty();
        TradeSearch search = Search(market);
        SearchResult found = await search.Run(OldTool.Request(NearPowerplay) with { Mode = "anywhere" }, Never);
        Assert.Equal("Powerplay filters need a \"Near me\" search, so they were not used.", found.Note);
        Assert.DoesNotContain(market.Asked, asked => asked.StartsWith("POST", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the questions themselves

    /// <summary>The service reads fleetCarriers=true as "carriers ONLY".</summary>
    [Theory]
    [InlineData("near", NearByHour, 21)]      // overview, on sale here, nineteen scans
    [InlineData("anywhere", Anywhere, 15)]    // overview, ten "where to buy", four "who buys gold near there"
    [InlineData("lookup", LookupCargo, 3)]
    public async Task Carriers_are_included_by_leaving_the_word_out_never_by_asking_for_them(string mode, string scenario, int requests)
    {
        async Task<IReadOnlyList<string>> Urls(bool carriers)
        {
            // only the galaxy's gold sellers are known: enough for an "anywhere" search to go on to its second kind of question
            TradeSearch search = Search(RecordedMarket.Answering(address =>
                address == RecordedMarket.Overview || address.Contains("/commodity/name/gold/exports?") ? null : RecordedMarket.Json("[]")));
            return (await search.Run(OldTool.Request(scenario) with { Carriers = carriers }, Never)).Urls;
        }
        IReadOnlyList<string> with = await Urls(carriers: true), without = await Urls(carriers: false);

        Assert.Equal(mode == "anywhere" ? 11 : requests, with.Count);   // (the recording has the gold sellers only for "no carriers")
        Assert.Equal(requests, without.Count);
        Assert.DoesNotContain(with, url => url.Contains("fleetCarriers"));
        // every list of offers says "no carriers", bar one: a lookup's question about the system's own stations has no such word
        Assert.All(without.Skip(1).Where(url => !url.EndsWith("?maxDaysAgo=2", StringComparison.Ordinal)), url => Assert.EndsWith("&fleetCarriers=false", url));
        Assert.Equal(mode == "lookup" ? 1 : 0, without.Count(url => url.EndsWith("?maxDaysAgo=2", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(24, 1)]
    [InlineData(24.5, 2)]
    [InlineData(48, 2)]
    [InlineData(72, 3)]
    [InlineData(168, 7)]
    [InlineData(720, 30)]
    public async Task The_price_age_is_asked_for_in_whole_days(double hours, int days)
    {
        TradeSearch search = Search(RecordedMarket.Empty());
        SearchResult found = await search.Run(OldTool.Request(LookupCargo) with { MaxAgeHours = hours, LookLy = 900, StarSystem = "Barnard's Star" }, Never);
        Assert.Equal(
            [
                RecordedMarket.Overview,
                $"{RecordedMarket.Ardent}/system/name/Barnard%27s%20Star/commodity/name/consumertechnology/nearby/imports?maxDistance=500&maxDaysAgo={days}&minVolume=1&fleetCarriers=false",
                $"{RecordedMarket.Ardent}/system/name/Barnard%27s%20Star/commodity/name/consumertechnology?maxDaysAgo={days}",
            ],
            found.Urls);
    }

    // ---------------------------------------------------------------- a polite client

    [Fact]
    public async Task At_most_six_requests_are_under_way_at_once_and_each_says_who_asks()
    {
        var market = new RecordedMarket { Delay = TimeSpan.FromMilliseconds(15) };
        TradeSearch search = Search(market);

        SearchResult found = await search.Run(OldTool.Request(NearByHour), Never);

        Assert.Equal(40, found.Trades.Count);
        Assert.Equal(6, market.MostAtOnce);   // nineteen questions of the market scan are ready at the same moment
        Assert.Equal([EliteStudio.App.Program.UserAgent], market.UserAgents);   // the program, and nobody's name
        Assert.Equal(market.Asked.Count, market.Asked.Distinct().Count());   // nothing is asked twice
    }

    // ---------------------------------------------------------------- failures

    static RecordedMarket Offline() => new() { Instead = (_, _) => throw new HttpRequestException("No such host is known.") };

    [Theory]
    [InlineData(NearByHour)]
    [InlineData(Anywhere)]
    [InlineData(LookupCargo)]
    public async Task Without_a_network_the_search_fails_with_the_reason_and_does_not_throw(string scenario)
    {
        RecordedMarket market = Offline();
        TradeSearch search = Search(market);
        SearchResult found = await search.Run(OldTool.Request(scenario), Never);

        Assert.False(found.Ok);
        Assert.Equal("No such host is known.", found.Error);
        Assert.Empty(found.Trades);
        Assert.Equal([RecordedMarket.Overview], found.Urls);
        Assert.Equal("LHS 3447", found.StarSystem);
        Assert.Equal(scenario == LookupCargo, found.Lookup);
        Assert.Single(market.Asked);   // no second try
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", "Response status code does not indicate success: 503 (Service Unavailable).")]
    [InlineData(HttpStatusCode.OK, "<html>Down for maintenance</html>", "The service sent an answer that could not be read.")]
    [InlineData(HttpStatusCode.OK, "[]", "The price overview came back empty.")]
    [InlineData(HttpStatusCode.OK, "{\"error\":\"Try again later\"}", "The price overview came back empty.")]
    public async Task A_price_overview_that_cannot_be_had_fails_the_search_and_is_not_kept(HttpStatusCode status, string body, string error)
    {
        RecordedMarket market = RecordedMarket.Answering(address => address == RecordedMarket.Overview ? RecordedMarket.Json(body, status) : null);
        TradeSearch search = Search(market);
        SearchResult found = await search.Run(OldTool.Request(NearByHour), Never);
        Assert.Equal((false, error), (found.Ok, found.Error));
        Assert.Empty(search.Commodities);
        Assert.Single(market.Asked);   // without an overview nothing else is asked

        // Kept, an overview with nothing in it would make every search of the next twelve hours find nothing.
        market.Instead = null;
        SearchResult next = await search.Run(OldTool.Request(NearByHour), Never);
        Assert.True(next.Ok, next.Error);
        Assert.Equal(RecordedMarket.Overview, next.Urls[0]);
        Assert.Equal(40, next.Trades.Count);
    }

    [Fact]
    public async Task One_question_that_fails_counts_as_no_data_and_is_not_asked_again()
    {
        static bool AboutGold(string address) => address.Contains("/commodity/name/gold/");
        var market = new RecordedMarket
        {
            Instead = (request, _) => AboutGold(request.RequestUri!.OriginalString) ? throw new HttpRequestException("The connection was reset.") : Task.FromResult<HttpResponseMessage?>(null),
        };
        DateTime clock = OldTool.RecordedUtc;
        var search = new TradeSearch(new MarketData(market), () => clock);

        SearchResult found = await search.Run(OldTool.Request(NearByHour), Never);

        Assert.True(found.Ok, found.Error);
        Assert.Equal(40, found.Trades.Count);
        Assert.DoesNotContain(found.Trades, trade => trade.CommodityId == "gold");
        Assert.Single(market.Asked, AboutGold);   // the scan's question; with no buyer known, where to buy gold is not asked at all
        // a failure is not remembered as an answer: the next search asks that question again, first of all
        clock = clock.AddMinutes(1);
        market.Instead = null;
        SearchResult next = await search.Run(OldTool.Request(NearByHour), Never);
        Assert.Contains(next.Trades, trade => trade.CommodityId == "gold");
        Assert.True(AboutGold(next.Urls[0]));
        Assert.DoesNotContain(RecordedMarket.Overview, next.Urls);
    }

    [Fact]
    public async Task A_system_the_service_does_not_know_gives_an_empty_list_not_an_error()
    {
        var market = new RecordedMarket();
        TradeSearch search = Search(market);
        SearchResult found = await search.Run(OldTool.Request(NearByHour) with { StarSystem = "Nowhere At All" }, Never);

        Assert.True(found.Ok, found.Error);   // "404 Not Found" is an answer
        Assert.Empty(found.Trades);
        Assert.Equal((0, 19, 20), (found.Exports, found.Scanned, found.Candidates));
        Assert.Equal(21, found.Urls.Count);   // overview, on sale here, nineteen scans; nothing to follow up
        Assert.Equal(20, market.NotRecorded.Count);
    }

    [Fact]
    public async Task Going_offline_after_the_overview_is_loaded_fails_the_search_instead_of_finding_nothing()
    {
        var market = new RecordedMarket();
        TradeSearch search = Search(market);
        Assert.True((await search.Run(OldTool.Request(LookupCargo), Never)).Ok);

        market.Instead = (_, _) => throw new HttpRequestException("No such host is known.");
        SearchResult near = await search.Run(OldTool.Request(NearByHour), Never);
        Assert.Equal((false, "No such host is known."), (near.Ok, near.Error));
        Assert.Empty(near.Trades);
        // what is still in memory is still good: the lookup of a moment ago needs no network
        SearchResult lookup = await search.Run(OldTool.Request(LookupCargo), Never);
        Assert.True(lookup.Ok);
        Assert.NotEmpty(lookup.Trades);
        Assert.Empty(lookup.Urls);
    }

    [Fact]
    public async Task A_market_answer_that_cannot_be_read_fails_the_search()
    {
        TradeSearch search = Search(RecordedMarket.Answering(address => address.Contains("/commodity/name/gold/nearby/imports") ? RecordedMarket.Json("[{\"commodityName\":") : null));
        SearchResult found = await search.Run(OldTool.Request(NearByHour), Never);
        Assert.Equal((false, "The service sent an answer that could not be read."), (found.Ok, found.Error));
    }

    [Fact]
    public async Task A_search_that_fails_half_way_sends_none_of_the_requests_still_waiting_their_turn()
    {
        // the market scan's first question: the first answer the search waits for
        string firstScan = (await Search(new RecordedMarket()).Run(OldTool.Request(NearByHour), Never)).Urls[2];
        Assert.Contains("minPrice=", firstScan);

        var letThrough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var market = new RecordedMarket
        {
            Instead = async (request, stop) =>
            {
                string address = request.RequestUri!.OriginalString;
                if (address == firstScan) return RecordedMarket.Json("[{\"commodityName\":");   // cannot be read
                if (address.Contains("minPrice=")) await letThrough.Task.WaitAsync(stop);       // every other scan is held up
                return null;
            },
        };
        TradeSearch search = Search(market);

        SearchResult found = await search.Run(OldTool.Request(NearByHour), Never).WaitAsync(TimeSpan.FromSeconds(30), Never);

        Assert.Equal((false, "The service sent an answer that could not be read."), (found.Ok, found.Error));
        int asked = market.Asked.Count;
        // overview, on sale here, the six scans that had their turn, and at most the one that took the failed scan's place
        Assert.InRange(asked, 8, 9);
        letThrough.SetResult();
        await Task.Delay(100, Never);
        Assert.Equal(asked, market.Asked.Count);   // the dozen scans still waiting were dropped, not sent
        // the search is as good as new afterwards
        market.Instead = null;
        Assert.Equal(40, (await search.Run(OldTool.Request(NearByHour), Never)).Trades.Count);
    }

    [Fact]
    public async Task Spansh_failing_fails_a_powerplay_search_and_says_which_service()
    {
        var market = new RecordedMarket
        {
            Instead = (request, _) => Task.FromResult(request.Method == HttpMethod.Post ? RecordedMarket.Json("{\"error\":\"Invalid request\"}", HttpStatusCode.BadRequest) : null),
        };
        TradeSearch search = Search(market);
        SearchResult found = await search.Run(OldTool.Request(NearPowerplay), Never);

        Assert.False(found.Ok);
        Assert.Equal("Powerplay data from Spansh could not be loaded: Response status code does not indicate success: 400 (Bad Request).", found.Error);
        Assert.Equal([RecordedMarket.Overview, RecordedMarket.Spansh + " (page 0)"], found.Urls);
    }

    [Fact]
    public async Task A_question_that_gets_no_answer_in_time_is_given_up_and_counts_as_no_data()
    {
        static async Task<HttpResponseMessage?> Never_answers(CancellationToken stop)
        {
            await Task.Delay(Timeout.Infinite, stop);
            return null;
        }
        var market = new RecordedMarket
        {
            Instead = (request, stop) => request.RequestUri!.OriginalString.Contains("/commodity/name/gold/") ? Never_answers(stop) : Task.FromResult<HttpResponseMessage?>(null),
        };
        TradeSearch search = Search(market, timeoutSeconds: 1);

        SearchResult found = await search.Run(OldTool.Request(NearByHour), Never).WaitAsync(TimeSpan.FromSeconds(30), Never);

        Assert.True(found.Ok, found.Error);
        Assert.Equal(40, found.Trades.Count);
        Assert.DoesNotContain(found.Trades, trade => trade.CommodityId == "gold");

        // when it is the price overview that never comes, the search fails and says so
        TradeSearch slow = Search(new RecordedMarket { Instead = (_, stop) => Never_answers(stop) }, timeoutSeconds: 0.5);
        SearchResult nothing = await slow.Run(OldTool.Request(NearByHour), Never).WaitAsync(TimeSpan.FromSeconds(30), Never);
        Assert.Equal((false, "No answer within 0.5 seconds."), (nothing.Ok, nothing.Error));
    }

    [Fact]
    public async Task A_search_needs_to_know_where_the_player_is_unless_it_looks_anywhere()
    {
        RecordedMarket market = RecordedMarket.Empty();
        TradeSearch search = Search(market);
        SearchResult near = await search.Run(OldTool.Request(NearByHour) with { StarSystem = null }, Never);
        Assert.Equal((false, "Your current system is not known yet."), (near.Ok, near.Error));
        Assert.Empty(market.Asked);
        Assert.True((await search.Run(OldTool.Request(NearByHour) with { StarSystem = null, Mode = "anywhere" }, Never)).Ok);
    }

    // ---------------------------------------------------------------- stopping

    [Fact]
    public async Task A_search_stopped_before_it_starts_asks_nothing()
    {
        var market = new RecordedMarket();
        TradeSearch search = Search(market);
        SearchResult found = await search.Run(OldTool.Request(NearByHour), new CancellationToken(canceled: true));
        Assert.Equal((false, Stopped), (found.Ok, found.Error));
        Assert.Empty(found.Urls);
        Assert.Empty(market.Asked);
    }

    [Fact]
    public async Task A_search_stopped_half_way_comes_back_at_once_and_asks_nothing_more()
    {
        var scanning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var market = new RecordedMarket
        {
            Instead = async (request, stop) =>
            {
                if (!request.RequestUri!.OriginalString.Contains("minPrice=")) return null;
                scanning.TrySetResult();
                await Task.Delay(Timeout.Infinite, stop);   // the market scan hangs until the search is stopped
                return null;
            },
        };
        TradeSearch search = Search(market);
        using var stop = new CancellationTokenSource();

        Task<SearchResult> running = search.Run(OldTool.Request(NearByHour), stop.Token);
        await scanning.Task.WaitAsync(TimeSpan.FromSeconds(30), Never);
        stop.Cancel();
        SearchResult found = await running.WaitAsync(TimeSpan.FromSeconds(30), Never);

        Assert.Equal((false, Stopped), (found.Ok, found.Error));
        Assert.Empty(found.Trades);
        int asked = market.Asked.Count;
        Assert.InRange(asked, 3, 8);   // overview, on sale here, and at most six of the nineteen scans
        await Task.Delay(100, Never);
        Assert.Equal(asked, market.Asked.Count);
        // the search is as good as new afterwards
        market.Instead = null;
        Assert.Equal(40, (await search.Run(OldTool.Request(NearByHour), Never)).Trades.Count);
    }
}
