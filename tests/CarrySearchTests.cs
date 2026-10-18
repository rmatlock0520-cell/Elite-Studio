using System.Globalization;
using System.Net.Http;
using EliteStudio.Core;
using EliteStudio.Market;
using static EliteStudio.Tests.HaulRig;   // its places (Alpha, Beta) and Place itself

namespace EliteStudio.Tests;

/// <summary>
/// "From a station to a system": what one station sells that the stations of a system the player
/// names pay more for. First the search itself against made-up lists of the two systems, then
/// hauling asking it: for the station the player is docked at, one way, with its own lines.
/// </summary>
public class CarrySearchTests
{
    const string Sold = RecordedMarket.Ardent + "/system/name/Alpha/commodities/exports?maxDaysAgo=2";
    const string Wanted = RecordedMarket.Ardent + "/system/name/Beta/commodities/imports?maxDaysAgo=2&fleetCarriers=false";

    static readonly CancellationToken Never = CancellationToken.None;

    /// <summary>One listing as Ardent Insight sends it. Alpha is at 10, 0, 0 and Beta at 10, 20, 0: twenty light years apart.</summary>
    static string Listing(string system, string station, long market, string commodity, int buy = 0, int stock = 0, int sell = 0, int demand = 0,
        string type = "Coriolis", int pad = 3, double starLs = 500, double hoursOld = 1) =>
        string.Create(CultureInfo.InvariantCulture, $$"""
            { "commodityName":"{{commodity}}", "marketId":{{market}}, "stationName":"{{station}}", "stationType":"{{type}}", "maxLandingPadSize":{{pad}},
              "distanceToArrival":{{starLs}}, "systemName":"{{system}}", "systemX":10, "systemY":{{(system == "Beta" ? 20 : 0)}}, "systemZ":0,
              "buyPrice":{{buy}}, "sellPrice":{{sell}}, "stock":{{stock}}, "demand":{{demand}}, "meanPrice":5000,
              "updatedAt":"{{OldTool.RecordedUtc.AddHours(-hoursOld):yyyy-MM-ddTHH:mm:ssZ}}" }
            """);

    static string List(params string[] listings) => "[" + string.Join(",", listings) + "]";

    // Alpha Port sells gold, bauxite and a little silver; another station of the system sells gold for next to nothing.
    static readonly string AlphaSells = List(
        Listing("Alpha", "Alpha Port", 101, "gold", buy: 9000, stock: 5000),
        Listing("Alpha", "Alpha Port", 101, "bauxite", buy: 500, stock: 20000),
        Listing("Alpha", "Alpha Port", 101, "silver", buy: 4000, stock: 50),
        Listing("Alpha", "Cheap Yard", 102, "gold", buy: 1000, stock: 5000));

    // Beta has two starports, an outpost with medium pads, a base on the ground and a fleet carrier.
    static readonly string BetaWants = List(
        Listing("Beta", "Beta Hub", 202, "gold", sell: 12000, demand: 300),
        Listing("Beta", "Beta Two", 203, "gold", sell: 11000, demand: 5000, starLs: 90000),
        Listing("Beta", "Beta Two", 203, "bauxite", sell: 900, demand: 5000, starLs: 90000),
        Listing("Beta", "Small Post", 204, "gold", sell: 15000, demand: 5000, type: "Outpost", pad: 2),
        Listing("Beta", "Ground Base", 205, "bauxite", sell: 2500, demand: 5000, type: "CraterOutpost"),
        Listing("Beta", "Beta Hub", 202, "silver", sell: 9000, demand: 5000),
        Listing("Beta", "Beta Hub", 202, "palladium", sell: 60000, demand: 5000));

    static RecordedMarket Market(string? sold = null, string? wanted = null) => RecordedMarket.Answering(address =>
        address == RecordedMarket.Overview ? null
        : address == Sold ? RecordedMarket.Json(sold ?? AlphaSells)
        : address == Wanted ? RecordedMarket.Json(wanted ?? BetaWants)
        : RecordedMarket.Json("[]"));

    static TradeSearch Search(HttpMessageHandler market) => new(new MarketData(market), () => OldTool.RecordedUtc);

    static SearchRequest Request() => new()
    {
        Mode = "carry", StarSystem = "Alpha", FromSystem = "Alpha", FromStation = "Alpha Port", ToSystem = "Beta",
        Pad = 3, Cargo = 400, Jump = 20, JumpEmpty = 25, MaxStarLs = 0, MinSupply = 100, MinDemand = 100, MaxAgeHours = 48, Surface = "odyssey",
        RankBy = "trip", Returns = true,   // a round trip is asked for, and still it is one way
    };

    static string Line(TradeRow row) => $"{row.Rank} {row.Commodity}: {row.BuyStation} {row.BuyPrice} -> {row.SellStation} {row.SellPrice}, {row.Units} t, +{row.ProfitPerTrip}";

    // ---------------------------------------------------------------- the search

    [Fact]
    public async Task Lists_what_the_station_sells_that_the_system_pays_more_for_best_load_first()
    {
        RecordedMarket market = Market();
        SearchResult found = await Search(market).Run(Request(), Never);

        Assert.True(found.Ok, found.Error);
        Assert.Equal(new[]
        {
            // By what the trip earns. The hub pays most for gold but wants only 300 tons. Bauxite sells on the ground
            // for far more than in space, and that base is closer to its star than the second starport, so of the
            // two loads that earn the same it comes first. Silver: 50 tons on sale is under the least supply. The
            // outpost has no large pad, and what the other station of Alpha charges is nobody's business.
            "1 Gold: Alpha Port 9000 -> Beta Hub 12000, 300 t, +900000",
            "2 Bauxite: Alpha Port 500 -> Ground Base 2500, 400 t, +800000",
            "3 Gold: Alpha Port 9000 -> Beta Two 11000, 400 t, +800000",
            "4 Bauxite: Alpha Port 500 -> Beta Two 900, 400 t, +160000",
        }, found.Trades.Select(Line));
        Assert.Equal("4 loads from Alpha Port to Beta", found.Summary);
        Assert.Null(found.Empty);
        Assert.Equal(new[] { Sold, Wanted }, market.Asked.Where(address => address != RecordedMarket.Overview));

        TradeRow gold = found.Trades[0];
        Assert.Equal(("Alpha", "Beta", 101L, 202L, "Coriolis", "L"), (gold.BuySystem, gold.SellSystem, gold.BuyMarketId, gold.SellMarketId, gold.SellType, gold.SellPad));
        Assert.Equal((20.0, 1, 0, true, ""), (gold.DistanceLy, gold.JumpsOut, gold.JumpsBack, gold.OneWay, gold.Return));
        Assert.Equal((3000, 900_000L, 900_000L, "1h ago"), (gold.ProfitPerTon, gold.ProfitPerTrip, gold.RoundTripProfit, gold.Updated));
    }

    [Fact]
    public async Task The_station_filters_count_for_where_to_sell_and_not_for_where_to_buy()
    {
        // Space stations only, within 1,000 light seconds of the star, and small ships: the base on the ground and the
        // far starport go, the outpost comes in and pays most.
        SearchResult found = await Search(Market()).Run(Request() with { Surface = "none", MaxStarLs = 1000, Pad = 1 }, Never);
        Assert.Equal(new[] { "Small Post", "Beta Hub" }, found.Trades.Select(row => row.SellStation));

        // The station to buy at is the player's own choice: a fleet carrier with small pads far from its star is still bought at.
        string carrier = List(Listing("Alpha", "Alpha Port", 101, "gold", buy: 9000, stock: 5000, type: "FleetCarrier", pad: 1, starLs: 400000));
        found = await Search(Market(sold: carrier)).Run(Request() with { MaxStarLs = 1000 }, Never);
        Assert.Equal(new[] { "Beta Hub" }, found.Trades.Select(row => row.SellStation));
        Assert.Equal("FleetCarrier", found.Trades[0].BuyType);

        // A price that is too old is no price: the station's list is forty hours old, and thirty is the most allowed.
        string old = List(Listing("Alpha", "Alpha Port", 101, "gold", buy: 9000, stock: 5000, hoursOld: 40));
        found = await Search(Market(sold: old)).Run(Request() with { MaxAgeHours = 30 }, Never);
        Assert.Empty(found.Trades);
        Assert.StartsWith("No prices new enough are known for Alpha Port in Alpha.", found.Empty);
    }

    [Fact]
    public async Task Docked_at_the_station_the_games_own_price_list_is_what_counts()
    {
        var shown = new StationMarket(101, "Alpha Port", "Alpha", new Dictionary<string, MarketItem>
        {
            ["gold"] = new("gold", "Gold", BuyPrice: 8000, SellPrice: 7000, Stock: 250, Demand: 0),
            ["silver"] = new("silver", "Silver", BuyPrice: 4000, SellPrice: 3900, Stock: 900, Demand: 0),
            ["bauxite"] = new("bauxite", "Bauxite", BuyPrice: 0, SellPrice: 400, Stock: 0, Demand: 10),   // bought here, not sold
        }, "Orbis");

        // The service has never heard of the station: the list on the game's screen is enough, and it is as new as can be.
        string others = List(Listing("Alpha", "Cheap Yard", 102, "gold", buy: 1000, stock: 5000));
        SearchResult found = await Search(Market(sold: others)).Run(Request() with { LiveMarket = shown }, Never);

        Assert.Equal(new[]
        {
            "1 Silver: Alpha Port 4000 -> Beta Hub 9000, 400 t, +2000000",
            "2 Gold: Alpha Port 8000 -> Beta Hub 12000, 250 t, +1000000",
            "3 Gold: Alpha Port 8000 -> Beta Two 11000, 250 t, +750000",
        }, found.Trades.Select(Line));
        Assert.Equal(("Silver", 101L, "Orbis", 20.0), (found.Trades[0].Commodity, found.Trades[0].BuyMarketId, found.Trades[0].BuyType, found.Trades[0].DistanceLy));
        Assert.All(found.Trades, row => Assert.Equal(1, row.AgeHours));   // the older of the two prices: the selling station's, an hour old

        // Another station's list on the screen (the player has flown on) says nothing about Alpha Port.
        found = await Search(Market()).Run(Request() with { LiveMarket = shown with { MarketId = 102, StationName = "Cheap Yard" } }, Never);
        Assert.Equal(9000, found.Trades[0].BuyPrice);
    }

    [Fact]
    public async Task Says_in_words_why_there_is_nothing_to_list()
    {
        async Task<string?> Why(SearchRequest request, string? sold = null, string? wanted = null)
        {
            SearchResult found = await Search(Market(sold, wanted)).Run(request, Never);
            Assert.True(found.Ok, found.Error);
            Assert.Empty(found.Trades);
            Assert.Null(found.Summary);
            return found.Empty;
        }

        Assert.Equal("Type the system you are travelling to under \"To system\" on the Market page.", await Why(Request() with { ToSystem = " " }));
        Assert.Equal("Dock at the station you are buying at, or pick it under \"From\" on the Market page.", await Why(Request() with { FromStation = "" }));
        Assert.Equal("Dock at the station you are buying at, or pick it under \"From\" on the Market page.", await Why(Request() with { FromSystem = "" }));
        Assert.StartsWith("No prices new enough are known for Nowhere Port in Alpha.", await Why(Request() with { FromStation = "Nowhere Port" }));
        Assert.Equal("No station in Beta that fits your filters has prices new enough. Check the system's name, or allow older prices.", await Why(Request(), wanted: "[]"));
        Assert.Equal("Nothing Alpha Port sells is known to fetch more at a station in Beta that fits your filters.",
            await Why(Request(), wanted: List(Listing("Beta", "Beta Hub", 202, "gold", sell: 8000, demand: 5000))));
    }

    [Fact]
    public async Task Names_the_stations_of_a_system_to_pick_from()
    {
        const string asked = RecordedMarket.Ardent + "/system/name/Col%20285%20Sector/commodities/exports?maxDaysAgo=30";
        RecordedMarket market = RecordedMarket.Answering(address => address == asked ? RecordedMarket.Json(AlphaSells) : RecordedMarket.Json("[]"));
        TradeSearch search = Search(market);

        Assert.Equal(new[] { "Alpha Port", "Cheap Yard" }, await search.Stations(" Col 285 Sector ", Never));
        Assert.Empty(await search.Stations("Nowhere", Never));
        Assert.Empty(await search.Stations("  ", Never));
        Assert.Equal(2, market.Asked.Count);   // no name, no question
    }

    // ---------------------------------------------------------------- hauling asks it

    [Fact]
    public void Hauling_asks_for_the_station_the_player_is_docked_at_one_way_and_shows_the_searchs_own_lines()
    {
        using var rig = new HaulRig(In(Alpha), request => request.Mode == "carry" && request.ToSystem.Length > 0
            ? Found(request, Gold()) with { Summary = "1 load from Alpha Port to Beta" }
            : new SearchResult { StarSystem = request.StarSystem, Empty = request.Mode == "carry" ? "Type the system you are travelling to." : null });
        Hauling hauling = rig.Hauling;
        rig.After(0.5);
        Assert.Equal(("near", "", "", ""), (rig.Search.Asked[^1].Mode, rig.Search.Asked[^1].FromStation, rig.Search.Asked[^1].FromSystem, rig.Search.Asked[^1].ToSystem));

        // The choice alone: no system named yet, and the line says what to do.
        Assert.True(rig.Change("""{ "startMode": "carry" }"""));
        rig.After(0.7);
        Assert.Equal("Type the system you are travelling to.", hauling.Message);
        Assert.Equal(("Alpha", "Alpha Port"), hauling.CarryFrom);

        Assert.True(rig.Change("""{ "toSystem": "  Beta " }"""));
        rig.After(0.7);
        SearchRequest asked = rig.Search.Asked[^1];
        Assert.Equal(("carry", "Alpha", "Alpha Port", "Beta", false), (asked.Mode, asked.FromSystem, asked.FromStation, asked.ToSystem, asked.Returns));
        Assert.Equal((10.0, 0.0, 0.0), (asked.FromX, asked.FromY, asked.FromZ));
        Assert.Null(asked.LiveMarket);   // the commodities screen has not been opened
        Assert.Equal("1 load from Alpha Port to Beta", hauling.ListMessage);
        Assert.Equal("Beta", rig.Saved().ToSystem);

        // Its commodities screen opened: the same question with the game's own prices.
        int before = rig.Search.Asked.Count;
        rig.GameFile("Market.json", PriceList(Alpha, ("gold", 9000, 500)));
        Assert.Equal(before + 1, rig.Search.Asked.Count);
        Assert.Equal("Alpha Port", rig.Search.Asked[^1].LiveMarket?.StationName);

        // Taking off changes nothing: the list stays the docked station's, with the prices the game showed there.
        before = rig.Search.Asked.Count;
        rig.Play(Undock(Alpha));
        Assert.Equal(before, rig.Search.Asked.Count);
        Assert.Equal(("Alpha", "Alpha Port"), hauling.CarryFrom);
        Assert.Equal("1 load from Alpha Port to Beta", hauling.ListMessage);

        // A station the player names counts before the one they are docked at; half a name is no name.
        Assert.True(rig.Change("""{ "fromSystem": "Gamma" }"""));
        Assert.Equal(("Alpha", "Alpha Port"), hauling.CarryFrom);
        Assert.True(rig.Change("""{ "fromStation": "Gamma Dock" }"""));
        rig.After(0.7);
        Assert.Equal(("Gamma", "Gamma Dock"), hauling.CarryFrom);
        asked = rig.Search.Asked[^1];
        Assert.Equal(("Gamma", "Gamma Dock", 0.0), (asked.FromSystem, asked.FromStation, asked.FromX));   // not where the player is: its place is the service's to say

        // On the menu's settings page the three lines that mean nothing here say so.
        Assert.Equal("From a station to a system", hauling.SettingLines.First(line => line.Key == "startMode").Text);
        Assert.All(new[] { "buyLy", "maxLy", "returns" }, key =>
        {
            SettingLine line = hauling.SettingLines.First(line => line.Key == key);
            Assert.Equal((false, "Not used from a station to a system"), (line.Usable, line.Text));
        });
    }
}
