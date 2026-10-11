using System.Text.Json;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Market;
using static EliteStudio.Tests.HaulRig;   // its places (Alpha, Beta, Gamma, Delta) and Place itself

namespace EliteStudio.Tests;

/// <summary>
/// Hauling wired to a practice game folder, a stand-in search, a clock the test moves and settings
/// in a temp folder. The journal given at the start is history; what is played afterwards happens now.
/// </summary>
sealed class HaulRig : IDisposable
{
    /// <summary>A station the practice player can fly to.</summary>
    public sealed record Place(string System, double[] At, string Station, long Market);

    /// <summary>Stands in for the trade search: notes what it was asked and answers what the test says.</summary>
    public sealed class StandInSearch : ITradeSearch
    {
        readonly Queue<(SearchRequest Request, TaskCompletionSource<SearchResult> Answer)> held = new();

        public List<SearchRequest> Asked { get; } = [];

        public Func<SearchRequest, SearchResult> Answer { get; set; } = request => new SearchResult { StarSystem = request.StarSystem };

        /// <summary>While set, a search does not end until <see cref="Finish"/> or <see cref="Fail"/>, like a slow data service.</summary>
        public bool Hold { get; set; }

        /// <summary>While set, a held search ends the moment it is told to stop, as the real search does; otherwise it answers late all the same.</summary>
        public bool Stops { get; set; }

        public IReadOnlyList<(string Id, string Name)> Commodities { get; set; } = [];

        public Task<SearchResult> Run(SearchRequest request, CancellationToken cancel)
        {
            Asked.Add(request);
            if (!Hold) return Task.FromResult(Answer(request));
            var later = new TaskCompletionSource<SearchResult>();
            if (Stops) cancel.Register(() => later.TrySetResult(new SearchResult { Ok = false, Error = "The search was stopped.", StarSystem = request.StarSystem }));
            held.Enqueue((request, later));
            return later.Task;
        }

        /// <summary>The oldest held search gets its answer.</summary>
        public void Finish()
        {
            (SearchRequest request, TaskCompletionSource<SearchResult> answer) = held.Dequeue();
            answer.SetResult(Answer(request));
        }

        /// <summary>The oldest held search breaks instead of answering.</summary>
        public void Fail(string why) => held.Dequeue().Answer.SetException(new InvalidOperationException(why));
    }

    public static readonly Place Alpha = new("Alpha", [10, 0, 0], "Alpha Port", 101);
    public static readonly Place Beta = new("Beta", [10, 20, 0], "Beta Hub", 202);
    public static readonly Place Gamma = new("Gamma", [10, 0, 50], "Gamma Dock", 303);
    public static readonly Place Delta = new("Delta", [40, 0, 0], "Delta Yard", 404);

    /// <summary>The time a rig's clock starts at unless the test gives another.</summary>
    public static readonly DateTime Noon = new(2026, 10, 9, 12, 0, 0);

    /// <summary>One of the small settings files in the Elite Haul Overlay's own format (Fixtures\hauling), by its name.</summary>
    public static string OldFile(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "hauling", name);

    readonly string dir = Directory.CreateTempSubdirectory("EliteStudioHaul-").FullName;
    readonly string journal;

    /// <param name="history">The journal as it stands when Studio starts.</param>
    /// <param name="oldSettings">A Haul Overlay settings file to copy from; none when left out.</param>
    /// <param name="savedSettings">The text of Studio's own settings.json; none yet when left out.</param>
    /// <param name="tickFirst">The app's timer comes round once before the journal has been read at all.</param>
    /// <param name="hold">The game's Cargo.json as it stands when Studio starts (see <see cref="Hold"/>); none when left out.</param>
    public HaulRig(IEnumerable<string> history, Func<SearchRequest, SearchResult>? answer = null, string? oldSettings = null, string? savedSettings = null,
        DateTime? clock = null, bool tickFirst = false, string? hold = null)
    {
        Search.Answer = answer ?? (request => Found(request, Gold(), Bauxite(), Silver()));
        Clock = clock ?? Noon;
        if (savedSettings is not null) File.WriteAllText(SettingsFile, savedSettings);
        if (hold is not null) Game.Write(Path.Combine(Game.JournalDir, "Cargo.json"), hold);
        journal = Game.Journal("Journal.2026-10-09T100000.01.log", history);
        Watcher = Game.Watch();
        Hauling = new Hauling(Watcher.State, new Settings(SettingsFile), Search, () => Clock, oldSettings ?? Path.Combine(dir, "no such file.json"));
        Hauling.Changed += () => Changes++;
        if (tickFirst) Hauling.Tick();
        Watcher.Poll();   // the catch-up reading
    }

    public PracticeGame Game { get; } = new();
    public StandInSearch Search { get; } = new();
    public GameWatcher Watcher { get; }
    public Hauling Hauling { get; }
    public DateTime Clock { get; set; }
    public int Changes { get; private set; }
    public string SettingsFile => Path.Combine(dir, "settings.json");

    /// <summary>The game writes these journal lines now, and the app reads them.</summary>
    public void Play(params string[] lines)
    {
        Game.Append(journal, lines);
        Watcher.Poll();
    }

    /// <summary>
    /// The journal file is replaced by a shorter one. The watcher then starts that file over, and
    /// what it finds in it is history again, not something happening now.
    /// </summary>
    public void Rewrite(params string[] lines)
    {
        Game.Journal(Path.GetFileName(journal), lines);
        Watcher.Poll();
    }

    /// <summary>The game rewrites one of its small files (Cargo.json, Market.json, NavRoute.json), and the app reads it.</summary>
    public void GameFile(string name, string text)
    {
        Game.Write(Path.Combine(Game.JournalDir, name), text);
        Watcher.Poll();
    }

    /// <summary>Time passes, and the app's timer comes round.</summary>
    public void After(double seconds)
    {
        Clock = Clock.AddMilliseconds(Math.Round(seconds * 1000));   // whole milliseconds, so 0.4 + 0.6 + 0.1 is exactly 1.1
        Hauling.Tick();
    }

    public bool Change(string json) => Hauling.ChangeSettings(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!);

    /// <summary>The market settings as they are on disk.</summary>
    public MarketSettings Saved() => new Settings(SettingsFile).Get<MarketSettings>("market");

    /// <summary>What is on disk for a restart of Studio to bring back.</summary>
    public HaulingKept Kept() => new Settings(SettingsFile).Get<HaulingKept>("hauling");

    public void Dispose()
    {
        Watcher.Dispose();
        Game.Dispose();
        Directory.Delete(dir, recursive: true);
    }

    // ---- journal lines and game files, in the game's own layout ----

    public static string[] In(Place place, bool docked = true) =>
    [
        Line("LoadGame", "\"Commander\":\"CMDR Test\", \"Ship\":\"Type8\", \"ShipID\":14"),
        Line("Loadout", "\"Ship\":\"type8\", \"ShipID\":14, \"ShipName\":\"\", \"ShipIdent\":\"\", \"UnladenMass\":400.000000, \"CargoCapacity\":400, \"MaxJumpRange\":30.000000, \"FuelCapacity\":{ \"Main\":32.000000, \"Reserve\":0.500000 }, \"Modules\":[  ]"),
        Line("Location", docked
            ? $"\"Docked\":true, \"StationName\":\"{place.Station}\", \"MarketID\":{place.Market}, \"StarSystem\":\"{place.System}\", \"StarPos\":{Pos(place)}"
            : $"\"Docked\":false, \"StarSystem\":\"{place.System}\", \"StarPos\":{Pos(place)}"),
    ];

    public static string Jump(Place to) => Line("FSDJump", $"\"Taxi\":false, \"Multicrew\":false, \"StarSystem\":\"{to.System}\", \"StarPos\":{Pos(to)}");
    public static string Dock(Place at) => Line("Docked", $"\"StationName\":\"{at.Station}\", \"StationType\":\"Coriolis\", \"StarSystem\":\"{at.System}\", \"MarketID\":{at.Market}");
    public static string Undock(Place at) => Line("Undocked", $"\"StationName\":\"{at.Station}\", \"StationType\":\"Coriolis\", \"MarketID\":{at.Market}");
    public static string Buy(Place at, string item, int price = 1000) => Line("MarketBuy", $"\"MarketID\":{at.Market}, \"Type\":\"{item}\", \"Count\":400, \"BuyPrice\":{price}, \"TotalCost\":{price * 400L}");
    public static string Sell(Place at, string item) => Line("MarketSell", $"\"MarketID\":{at.Market}, \"Type\":\"{item}\", \"Count\":400, \"SellPrice\":12000, \"TotalSale\":4800000, \"AvgPricePaid\":1000");
    public static string Target(string system, int jumpsLeft) => Line("FSDTarget", $"\"Name\":\"{system}\", \"SystemAddress\":1, \"StarClass\":\"K\", \"RemainingJumpsInRoute\":{jumpsLeft}");
    public static string RouteCleared => Line("NavRouteClear");

    public static string Hold(params (string Id, int Tons)[] loads) =>
        $"{{ \"timestamp\":\"2026-10-09T10:00:00Z\", \"event\":\"Cargo\", \"Vessel\":\"Ship\", \"Count\":{loads.Sum(load => load.Tons)}, \"Inventory\":[ "
        + string.Join(", ", loads.Select(load => $"{{ \"Name\":\"{load.Id}\", \"Count\":{load.Tons}, \"Stolen\":0 }}")) + " ] }";

    public static string PriceList(Place at, params (string Id, int Buy, int Stock)[] items) =>
        $"{{ \"timestamp\":\"2026-10-09T10:00:00Z\", \"event\":\"Market\", \"MarketID\":{at.Market}, \"StationName\":\"{at.Station}\", \"StarSystem\":\"{at.System}\", \"Items\":[ "
        + string.Join(", ", items.Select(item => $"{{ \"id\":1, \"Name\":\"${item.Id}_name;\", \"Name_Localised\":\"{Name(item.Id)}\", \"BuyPrice\":{item.Buy}, \"SellPrice\":5000, \"Stock\":{item.Stock}, \"Demand\":100 }}")) + " ] }";

    public static string Route(params Place[] stops) =>
        "{ \"timestamp\":\"2026-10-09T10:00:00Z\", \"event\":\"NavRoute\", \"Route\":[ "
        + string.Join(", ", stops.Select(stop => $"{{ \"StarSystem\":\"{stop.System}\", \"SystemAddress\":1, \"StarPos\":{Pos(stop)}, \"StarClass\":\"K\" }}")) + " ] }";

    static string Line(string name, string fields = "") =>
        "{ \"timestamp\":\"2026-10-09T10:00:00Z\", \"event\":\"" + name + "\"" + (fields.Length > 0 ? ", " + fields : "") + " }";

    static string Pos(Place place) => FormattableString.Invariant($"[{place.At[0]:0.00000},{place.At[1]:0.00000},{place.At[2]:0.00000}]");

    static string Name(string id) => char.ToUpperInvariant(id[0]) + id[1..];

    // ---- what the stand-in search finds ----

    /// <summary>An answer as the real search gives it: for the system asked about, the rows numbered in the order given.</summary>
    public static SearchResult Found(SearchRequest request, params TradeRow[] trades)
    {
        for (int i = 0; i < trades.Length; i++) trades[i].Rank = i + 1;
        return new() { StarSystem = request.StarSystem, Trades = trades, Exports = trades.Length, Lookup = request.Mode == "lookup" };
    }

    public static TradeRow Gold() => Trade("gold", Alpha, Beta, bringBack: "silver", perTon: 3000, perHour: 30_000_000, age: 2);
    public static TradeRow Bauxite() => Trade("bauxite", Alpha, Gamma, perTon: 5000, perHour: 20_000_000, age: 1);
    public static TradeRow Silver() => Trade("silver", Delta, Beta, perTon: 1000, perHour: 10_000_000, age: 30);

    public static TradeRow Trade(string id, Place buy, Place sell, string bringBack = "", bool oneWay = false, int perTon = 3000, long perHour = 30_000_000, double age = 2)
    {
        double ly = Math.Round(Math.Sqrt(buy.At.Zip(sell.At, (a, b) => (a - b) * (a - b)).Sum()), 1);
        long back = bringBack.Length > 0 ? 400_000 : 0;
        return new TradeRow
        {
            CommodityId = id, Commodity = Name(id), BuyStation = buy.Station, BuySystem = buy.System, BuyMarketId = buy.Market, BuyPad = "L",
            BuyX = buy.At[0], BuyY = buy.At[1], BuyZ = buy.At[2], BuyPrice = 9000, Stock = 5000,
            SellStation = sell.Station, SellSystem = sell.System, SellMarketId = sell.Market, SellPad = "L",
            SellX = sell.At[0], SellY = sell.At[1], SellZ = sell.At[2], SellPrice = 9000 + perTon, Demand = 5000,
            DistanceLy = ly, ProfitPerTon = perTon, Units = 400, ProfitPerTrip = perTon * 400L,
            ReturnId = bringBack, ReturnProfitPerTon = (int)(back / 400), ReturnUnits = back > 0 ? 400 : 0, ReturnProfit = back,
            RoundTripProfit = perTon * 400L + back, PerHour = perHour, Minutes = 20, AgeHours = age,
            JumpsOut = (int)Math.Ceiling(ly / 14), JumpsBack = oneWay ? 0 : (int)Math.Ceiling(ly / 14), OneWay = oneWay,
        };
    }

    /// <summary>
    /// A place to sell, as a lookup answers: the buy side says what is being sold, and has no market.
    /// The commodity is named as the search names it (by the bundled table), which is not always the
    /// name the game wrote for the hold: the name here is marked, so a test sees which one is shown.
    /// </summary>
    public static TradeRow SellAt(SearchRequest request, Place sell) => new()
    {
        CommodityId = request.LookItem, Commodity = request.LookItem + " (the search's name)",
        BuyStation = request.LookFrom, BuySystem = request.StarSystem ?? "", BuyPrice = request.LookPaid, Stock = request.LookCount,
        SellStation = sell.Station, SellSystem = sell.System, SellMarketId = sell.Market, SellX = sell.At[0], SellY = sell.At[1], SellZ = sell.At[2],
        SellPrice = request.LookPaid + 2000, Demand = 5000, Units = request.LookCount, ProfitPerTon = 2000, AgeHours = 0.5,
    };
}

/// <summary>
/// Hauling driven through whole flights: journal lines are appended to a practice journal the way
/// the game writes them, the watcher reads them, and the test looks at what a screen would show.
/// </summary>
public class HaulingTests
{
    // ---- the accepted trade ----

    [Fact]
    public void A_recorded_evening_of_round_trips_is_followed_from_each_purchase_to_the_return()
    {
        // The owner's own session: bauxite out and polymers back once, then silver out and polymers back twice.
        string[] recording = PracticeGame.Recording("Journal.2026-10-05T152112.01.log");
        var kents = new Place("Arima", [26.09375, -25.3125, 31.15625], "Kent's Legacy", 4227056643);
        var beacon = new Place("LHS 3583", [26.5625, -21.1875, 23.40625], "Antoniodeandrade Beacon", 4275254531);
        var lamarck = new Place("Nanabozho", [22.9375, -35.75, 24.15625], "Lamarck Orbital", 3223906560);
        static DateTime TimeOf(string line) => JsonDocument.Parse(line).RootElement.GetProperty("timestamp").GetDateTime();

        // Studio starts while the player sits docked at Kent's Legacy, before the first purchase (line 21 of the recording).
        using var rig = new HaulRig(recording.Take(20), clock: TimeOf(recording[19]),
            answer: request => HaulRig.Found(request, HaulRig.Trade("bauxite", kents, beacon, "polymers"), HaulRig.Trade("silver", kents, lamarck, "polymers")));
        Hauling hauling = rig.Hauling;
        string Now() => (hauling.Accepted is { } trade ? $"{trade.Commodity} {hauling.Leg}{(hauling.ReturnLoaded ? " loaded" : "")}" : "nothing accepted")
            + $", next {hauling.NextStop?.What} {hauling.NextStop?.System}, searched {rig.Search.Asked.Count}";

        var story = new List<string> { "start: " + Now() };
        string before = Now();
        foreach (string line in recording.Skip(20))
        {
            rig.Clock = TimeOf(line);
            rig.Play(line);
            hauling.Tick();
            if (Now() != before) story.Add($"{JsonDocument.Parse(line).RootElement.GetProperty("event")}: {before = Now()}");
        }

        Assert.Equal(new[]
        {
            "start: nothing accepted, next SELL LHS 3583, searched 1",          // docked where the best trade buys: go and sell
            "MarketBuy: Bauxite sell, next SELL LHS 3583, searched 1",          // buying what the trade says accepts it
            "MarketSell: Bauxite back, next BACK Arima, searched 1",
            "MarketBuy: Bauxite back loaded, next BACK Arima, searched 1",      // the polymers for the way back
            "Docked: nothing accepted, next SELL LHS 3583, searched 2",         // back at Kent's Legacy: finished, new search
            "MarketBuy: Silver sell, next SELL Nanabozho, searched 2",          // not the trade on screen, but a listed one
            "MarketSell: Silver back, next BACK Arima, searched 2",
            "MarketBuy: Silver back loaded, next BACK Arima, searched 2",
            "Docked: nothing accepted, next SELL LHS 3583, searched 3",
            "MarketBuy: Silver sell, next SELL Nanabozho, searched 3",
            "MarketSell: Silver back, next BACK Arima, searched 3",             // the recording ends at the far station
        }, story);
        Assert.All(rig.Search.Asked, request => Assert.Equal("Arima", request.StarSystem));   // nothing was searched while a trade was accepted
        Assert.Equal((404, 2), (rig.Search.Asked[0].Cargo, rig.Search.Asked[0].Pad));         // the Type-8: 404 t, medium pad
    }

    [Fact]
    public void A_one_way_trade_accepted_by_key_ends_at_the_sale_and_a_new_search_follows()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha, docked: false),
            answer: request => HaulRig.Found(request, HaulRig.Trade("bauxite", Alpha, Gamma, oneWay: true)));
        Hauling hauling = rig.Hauling;
        Assert.Empty(rig.Search.Asked);             // in space, nothing asks for a search until the app's timer comes round
        Assert.Equal("Waiting for Elite…", hauling.Message);

        rig.After(0.5);
        Assert.Single(rig.Search.Asked);
        Assert.True(hauling.ListOneWay);
        Assert.Equal(new Stop("BUY", "Alpha", "Alpha Port", "buy at", Here: true, Ly: 0, "empty", Plotted: false, Jumps: 0), hauling.NextStop);

        hauling.ToggleAccept("F6");
        Assert.Equal(("bauxite", "buy"), (hauling.Accepted?.CommodityId, hauling.Leg));   // nothing in the hold yet

        rig.Play(HaulRig.Dock(Alpha));
        Assert.Equal("SELL", hauling.NextStop?.What);       // docked at the buy station the next stop is already the place to sell
        Assert.Equal("buy", hauling.Leg);
        rig.Play(HaulRig.Buy(Alpha, "gold"));               // something else bought there: not this trade
        Assert.Equal("buy", hauling.Leg);
        rig.Play(HaulRig.Buy(Alpha, "bauxite"));
        Assert.Equal("sell", hauling.Leg);

        rig.Play(HaulRig.Undock(Alpha), HaulRig.Jump(Gamma), HaulRig.Dock(Gamma));
        Assert.Single(rig.Search.Asked);                    // arriving somewhere new does not search while a trade is accepted
        Assert.Equal(new Stop("SELL", "Gamma", "Gamma Dock", "sell at", Here: true, Ly: 0, "full", Plotted: false, Jumps: 0), hauling.NextStop);
        rig.Play(HaulRig.Sell(Gamma, "gold"));              // selling something else changes nothing
        Assert.Equal("sell", hauling.Leg);

        rig.Play(HaulRig.Sell(Gamma, "bauxite"));
        Assert.Null(hauling.Accepted);
        Assert.Equal("", hauling.Leg);
        Assert.Equal(2, rig.Search.Asked.Count);
        Assert.Equal("Gamma", rig.Search.Asked[1].StarSystem);
        Assert.Equal("1 trades from Gamma", hauling.ListMessage);
    }

    [Fact]
    public void Dropping_by_hand_always_asks_the_data_service_again()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;
        Assert.Single(rig.Search.Asked);            // docked in a system not searched yet: searched on the first reading

        Assert.True(hauling.NextTrade("F5"));
        hauling.ToggleAccept("F6");
        Assert.Equal("bauxite", hauling.Accepted?.CommodityId);
        Assert.Same(hauling.Accepted, hauling.Shown);
        Assert.Equal(1, hauling.Index);

        hauling.ToggleAccept("F6");
        Assert.Null(hauling.Accepted);
        Assert.Equal(2, rig.Search.Asked.Count);    // seconds after the first search, with nothing changed: asked all the same
        Assert.Equal(0, hauling.Index);             // the overlay is back on the best trade
        Assert.Equal("gold", hauling.Shown?.CommodityId);

        hauling.Drop();                             // nothing accepted: nothing to do
        Assert.Equal(2, rig.Search.Asked.Count);
    }

    [Fact]
    public void A_drop_while_a_search_runs_still_gets_its_own_fresh_search()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;
        hauling.ToggleAccept("F6");
        rig.Search.Hold = true;
        hauling.SearchAgain();
        Assert.True(hauling.Searching);

        hauling.ToggleAccept("F6");                 // drop: its search has to wait for the one that runs
        Assert.Equal(2, rig.Search.Asked.Count);
        rig.Search.Finish();
        Assert.Equal(3, rig.Search.Asked.Count);    // not answered from the one just ended
        rig.Search.Finish();
        Assert.False(hauling.Searching);
        Assert.Equal(3, hauling.Trades.Count);
    }

    [Fact]
    public void Purchases_and_sales_read_as_history_never_accept_or_move_a_trade()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;

        rig.Rewrite(HaulRig.Buy(Alpha, "gold"));    // a purchase of the trade on screen, but read as history
        Assert.Null(hauling.Accepted);
        rig.Play(HaulRig.Buy(Alpha, "gold"));       // the same purchase, made now
        Assert.Equal(("gold", "sell"), (hauling.Accepted?.CommodityId, hauling.Leg));

        rig.Rewrite(HaulRig.Sell(Beta, "gold"));    // a sale at the trade's sell station, read as history
        Assert.Equal(("gold", "sell"), (hauling.Accepted?.CommodityId, hauling.Leg));
        rig.Play(HaulRig.Sell(Beta, "gold"));
        Assert.Equal("back", hauling.Leg);
        Assert.Single(rig.Search.Asked);
    }

    [Fact]
    public void Selling_the_load_elsewhere_ends_the_trade_once_the_hold_is_seen_empty_of_it()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;
        rig.Play(HaulRig.Buy(Alpha, "gold"));
        rig.GameFile("Cargo.json", HaulRig.Hold(("gold", 400)));
        Assert.Equal(("gold", "sell"), (hauling.Accepted?.CommodityId, hauling.Leg));

        rig.Play(HaulRig.Undock(Alpha), HaulRig.Jump(Gamma), HaulRig.Dock(Gamma), HaulRig.Sell(Gamma, "gold"));
        rig.After(2);
        Assert.NotNull(hauling.Accepted);           // only part of it sold: the rest is still on board

        rig.Play(HaulRig.Sell(Gamma, "gold"));
        rig.GameFile("Cargo.json", HaulRig.Hold());
        rig.After(1.5);
        Assert.NotNull(hauling.Accepted);           // the game gets two seconds to write the hold's contents
        Assert.Single(rig.Search.Asked);
        rig.After(0.5);
        Assert.Null(hauling.Accepted);
        Assert.Equal("Gamma", rig.Search.Asked[1].StarSystem);
    }

    [Fact]
    public void Accepting_from_the_list_switches_trades_without_searching_and_knows_what_is_on_board()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;
        rig.GameFile("Cargo.json", HaulRig.Hold(("silver", 400), ("drones", 8)));
        TradeRow gold = hauling.Trades[0], silver = hauling.Trades[2];

        Assert.True(hauling.Accept(gold));
        Assert.Equal("buy", hauling.Leg);
        Assert.False(hauling.Show(silver));                         // an accepted trade keeps the screen
        Assert.Same(gold, hauling.Shown);

        Assert.True(hauling.Accept(HaulRig.Silver()));              // the same trade as a listed one, as a web page would send it
        Assert.Same(silver, hauling.Accepted);
        Assert.Equal("sell", hauling.Leg);                          // silver is in the hold already
        Assert.True(hauling.Accept(silver));                        // the accepted one again: nothing to do
        Assert.Single(rig.Search.Asked);

        Assert.False(hauling.Accept(HaulRig.Trade("tea", Alpha, Delta)));   // not a trade on the list
        Assert.Same(silver, hauling.Accepted);
    }

    [Fact]
    public void A_search_asked_for_while_a_trade_is_accepted_fills_the_list_but_leaves_the_overlay_on_the_trade()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;
        hauling.ToggleAccept("F6");
        TradeRow accepted = hauling.Accepted!;
        rig.Search.Hold = true;
        rig.Search.Answer = request => HaulRig.Found(request, HaulRig.Silver(), HaulRig.Gold()) with { Note = "Powerplay filter: 3 systems to buy in" };

        hauling.SearchAgain();
        rig.Play(HaulRig.Undock(Alpha), HaulRig.Jump(Beta));        // flown on before the answer came
        rig.Search.Finish();

        Assert.Equal(new[] { "silver", "gold" }, hauling.Trades.Select(trade => trade.CommodityId));
        Assert.Same(accepted, hauling.Shown);                       // the trade as accepted, not the new list's copy of it
        Assert.Equal("2 trades from Alpha   ·   Powerplay filter: 3 systems to buy in   ·   the overlay stays on your accepted trade until you drop it", hauling.ListMessage);
        Assert.Equal(20, accepted.BuyDistanceLy);                   // kept current for the accepted trade too
    }

    // ---- which trade is on screen ----

    [Fact]
    public void The_next_key_steps_round_the_list_and_only_notes_that_a_trade_is_accepted()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;

        var seen = new List<string?> { hauling.Shown?.CommodityId };
        for (int press = 0; press < 3; press++)
        {
            Assert.True(hauling.NextTrade("F5"));
            seen.Add(hauling.Shown?.CommodityId);
        }
        Assert.Equal(new[] { "gold", "bauxite", "silver", "gold" }, seen);

        Assert.True(hauling.Show(hauling.Trades[1]));               // a row clicked in the list
        Assert.Equal(1, hauling.Index);
        Assert.True(hauling.Show(HaulRig.Silver()));                // the same, by a copy of the row, as a web page sends it
        Assert.Equal(2, hauling.Index);
        Assert.False(hauling.Show(HaulRig.Trade("tea", Alpha, Delta)));
        hauling.ToggleAccept("F6");
        Assert.Null(hauling.Note);

        int changes = rig.Changes;
        Assert.False(hauling.NextTrade("F5"));
        Assert.Equal("silver", hauling.Shown?.CommodityId);
        Assert.Equal(new HaulNote("This trade is accepted: F5 drops it", Good: false), hauling.Note);
        Assert.True(rig.Changes > changes);

        rig.After(3.5);
        Assert.NotNull(hauling.Note);
        changes = rig.Changes;
        rig.After(0.5);                                             // four seconds: the note goes, and a screen is told to draw again
        Assert.Null(hauling.Note);
        Assert.Equal(changes + 1, rig.Changes);
    }

    [Fact]
    public void The_next_stop_follows_the_player_and_uses_the_games_own_count_on_a_plotted_route()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;
        Assert.Equal((27.8, 14.4), (hauling.JumpEmpty, hauling.JumpFull));
        TradeRow silver = hauling.Trades[2];
        Assert.Equal((true, 0.0, 0), (hauling.Trades[0].BuyHere, hauling.Trades[0].BuyDistanceLy, hauling.Trades[0].JumpsToBuy));
        Assert.Equal((false, 30.0, 2), (silver.BuyHere, silver.BuyDistanceLy, silver.JumpsToBuy));

        // Gold is bought where the player is docked, so the stop is the place to sell: 20 ly with a full hold.
        Assert.Equal(new Stop("SELL", "Beta", "Beta Hub", "sell at", Here: false, Ly: 20, "full", Plotted: false, Jumps: 2), hauling.NextStop);
        Assert.Equal("Beta", hauling.CopyText);
        hauling.Copied("Beta");
        Assert.Equal(new HaulNote("✔ Beta copied", Good: true), hauling.Note);

        hauling.Show(silver);                                       // bought elsewhere: fly there empty
        Assert.Equal(new Stop("BUY", "Delta", "Delta Yard", "buy at", Here: false, Ly: 30, "empty", Plotted: false, Jumps: 2), hauling.NextStop);

        rig.GameFile("NavRoute.json", HaulRig.Route(Alpha, Gamma, Beta, Delta));
        Assert.Equal((true, 3), (hauling.NextStop?.Plotted, hauling.NextStop?.Jumps));     // counted from the route's list of stops
        rig.Play(HaulRig.Target("Gamma", 5));
        Assert.Equal((true, 5), (hauling.NextStop?.Plotted, hauling.NextStop?.Jumps));     // the game's own newer count wins
        hauling.Show(hauling.Trades[0]);
        Assert.Equal((false, 2), (hauling.NextStop?.Plotted, hauling.NextStop?.Jumps));    // the route does not end at Beta: an estimate

        rig.Play(HaulRig.Undock(Alpha), HaulRig.RouteCleared, HaulRig.Jump(Delta), HaulRig.Dock(Delta));
        Assert.Equal((false, 30.0, 2), (hauling.Trades[0].BuyHere, hauling.Trades[0].BuyDistanceLy, hauling.Trades[0].JumpsToBuy));
        hauling.Show(hauling.Trades[2]);
        Assert.True(hauling.Trades[2].BuyHere);
        Assert.Equal("SELL", hauling.NextStop?.What);
    }

    // ---- when a search runs ----

    [Fact]
    public void An_answer_for_a_system_already_left_is_dropped_and_the_search_that_waited_runs()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;
        rig.Search.Hold = true;

        rig.Play(HaulRig.Undock(Alpha), HaulRig.Jump(Beta));
        Assert.True(hauling.Searching);
        Assert.Empty(hauling.Trades);                               // an answer to another question is on its way
        Assert.Null(hauling.Shown);
        Assert.Equal("Searching trades from Beta…", hauling.Message);
        Assert.Equal(hauling.Message, hauling.ListMessage);         // the line above the emptied list no longer counts Alpha's trades

        rig.Play(HaulRig.Jump(Gamma));                              // on again before the answer came
        Assert.Equal(2, rig.Search.Asked.Count);                    // one search at a time

        rig.Search.Finish();
        Assert.Empty(hauling.Trades);                               // Beta's answer is of no use in Gamma
        Assert.Equal(3, rig.Search.Asked.Count);
        Assert.Equal("Gamma", rig.Search.Asked[2].StarSystem);
        Assert.True(hauling.Searching);
        Assert.Equal("Searching trades from Gamma…", hauling.ListMessage);

        rig.Search.Finish();
        Assert.False(hauling.Searching);
        Assert.Equal("3 trades from Gamma", hauling.ListMessage);
        Assert.Equal(("Gamma", rig.Clock), (hauling.LastResult?.StarSystem, hauling.LastSearchAt));
    }

    [Fact]
    public void In_the_middle_of_a_plotted_route_the_list_is_kept_until_the_route_ends_or_the_player_docks()
    {
        var mid = new Place("Mid", [20, 0, 0], "Mid Station", 555);
        var end = new Place("End", [30, 0, 0], "End Station", 666);
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;
        TradeRow silver = hauling.Trades[2];
        hauling.NextTrade("F5");
        rig.Search.Hold = true;
        hauling.SearchAgain();                                      // asked again just before leaving; the answer will come too late
        Assert.Equal((3, "3 trades from Alpha"), (hauling.Trades.Count, hauling.ListMessage));    // the same question: list and line stay meanwhile

        // The game sends the count for after a jump while that jump is still under way.
        rig.Play(HaulRig.Undock(Alpha), HaulRig.Target("Mid", 2), HaulRig.Target("End", 1), HaulRig.Jump(mid));
        Assert.Equal(2, rig.Search.Asked.Count);
        Assert.Same(silver, hauling.Trades[2]);
        Assert.Equal((20.0, 1), (silver.BuyDistanceLy, silver.JumpsToBuy));    // distances keep up all the same
        Assert.False(hauling.Trades[0].BuyHere);

        rig.Search.Finish();                                        // Alpha's answer, with the player in Mid: not taken
        rig.Search.Hold = false;
        Assert.Same(silver, hauling.Trades[2]);
        Assert.Equal((1, false), (hauling.Index, hauling.Searching));

        rig.Play(HaulRig.Dock(mid));                                // docking part-way: this system was never searched
        Assert.Equal(new[] { "Alpha", "Alpha", "Mid" }, rig.Search.Asked.Select(request => request.StarSystem));

        rig.Play(HaulRig.Undock(mid), HaulRig.RouteCleared, HaulRig.Jump(end));   // the game clears the route before the last arrival
        Assert.Equal(new[] { "Alpha", "Alpha", "Mid", "End" }, rig.Search.Asked.Select(request => request.StarSystem));
    }

    [Fact]
    public void The_first_search_waits_until_the_game_has_said_where_the_player_is()
    {
        // The app's timer came round before the journal was read at all: asking then does not count as having asked.
        using var rig = new HaulRig(HaulRig.In(Alpha, docked: false), tickFirst: true);
        Assert.Empty(rig.Search.Asked);                             // in space, and all that was read is history: only the timer asks
        rig.After(0.5);
        Assert.Equal("Alpha", Assert.Single(rig.Search.Asked).StarSystem);
        Assert.Equal(3, rig.Hauling.Trades.Count);
    }

    [Fact]
    public void What_happens_while_a_search_runs_goes_on_screen_at_once_and_its_own_search_waits()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;
        rig.Play(HaulRig.Buy(Alpha, "bauxite"));
        rig.Play(HaulRig.Undock(Alpha), HaulRig.Jump(Gamma), HaulRig.Dock(Gamma));
        Assert.Equal(("bauxite", "sell"), (hauling.Accepted?.CommodityId, hauling.Leg));
        rig.Search.Hold = true;
        hauling.SearchAgain();                                      // a slow search the player asked for

        int changes = rig.Changes;
        rig.Play(HaulRig.Sell(Gamma, "bauxite"));                   // the trade is finished while that search is still out
        Assert.Null(hauling.Accepted);
        Assert.True(rig.Changes > changes);                         // a screen hears of it now, not when the search ends
        Assert.Equal(2, rig.Search.Asked.Count);                    // one search at a time

        rig.Search.Finish();                                        // the search that waited asks what has just been answered
        Assert.Equal((2, false, "3 trades from Gamma"), (rig.Search.Asked.Count, hauling.Searching, hauling.ListMessage));

        TradeRow silver = hauling.Trades[2];                        // bought in Delta: 30 ly across and 50 down from Gamma
        Assert.Equal(58.3, silver.BuyDistanceLy);
        hauling.SearchAgain();
        rig.Play(HaulRig.Undock(Gamma), HaulRig.Jump(Beta));        // flown on while that one is out
        Assert.Equal(36.1, silver.BuyDistanceLy);                   // the list on screen keeps up at once
        Assert.Equal(3, rig.Search.Asked.Count);
        rig.Search.Finish();                                        // Gamma's answer is of no use in Beta; now Beta's search runs
        Assert.Equal("Beta", rig.Search.Asked[3].StarSystem);
    }

    [Fact]
    public void A_screen_that_fails_to_draw_stops_neither_a_search_nor_a_lookup_nor_the_other_screen()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha, docked: false));
        Hauling hauling = rig.Hauling;
        int heard = 0;
        hauling.Changed += () => throw new InvalidOperationException("The overlay could not draw this.");
        hauling.Changed += () => heard++;

        rig.After(0.5);                                             // the first search starts, and its answer is taken in
        Assert.Equal((false, 3), (hauling.Searching, hauling.Trades.Count));
        Assert.Equal(2, heard);                                     // the screen after the failing one heard of both

        rig.GameFile("Cargo.json", HaulRig.Hold(("gold", 300)));
        hauling.SellMyCargo();
        Assert.Equal("done", hauling.Look?.Status);
    }

    [Fact]
    public void The_same_question_within_five_minutes_gets_the_last_answer_and_anywhere_ignores_where_the_player_is()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha, docked: false));
        Hauling hauling = rig.Hauling;
        rig.After(0.5);
        Assert.True(rig.Change("""{ "startMode": "anywhere" }"""));
        rig.After(0.7);
        Assert.Equal(new[] { "near", "anywhere" }, rig.Search.Asked.Select(request => request.Mode));
        TradeRow silver = hauling.Trades[2];

        hauling.NextTrade("F5");
        rig.Play(HaulRig.Jump(Gamma));                              // "anywhere" does not depend on where the player is
        Assert.Equal(2, rig.Search.Asked.Count);
        Assert.Same(silver, hauling.Trades[2]);
        Assert.Equal(58.3, silver.BuyDistanceLy);                   // 30 ly across and 50 up, from Gamma
        Assert.Equal(0, hauling.Index);                             // the answer was put on screen again, from the first trade

        rig.Play(HaulRig.Dock(Gamma));                              // "anywhere" lists are made for no system in particular
        Assert.Equal(2, rig.Search.Asked.Count);

        rig.Clock = rig.Clock.AddMinutes(5);
        rig.Play(HaulRig.Undock(Gamma), HaulRig.Jump(Beta));
        Assert.Equal(3, rig.Search.Asked.Count);                    // five minutes on, the prices may have moved
        Assert.Equal("Beta", rig.Search.Asked[2].StarSystem);

        hauling.SearchAgain();                                      // the button never settles for the last answer
        Assert.Equal(4, rig.Search.Asked.Count);
    }

    [Fact]
    public void Each_kind_of_answer_has_its_own_words()
    {
        using var rig = new HaulRig([]);                            // the game has never run: no journal lines at all
        Hauling hauling = rig.Hauling;
        rig.After(0.5);
        Assert.Equal("Waiting for Elite… (start the game, or jump to a system)", hauling.Message);
        Assert.Equal("", hauling.ListMessage);
        Assert.Empty(rig.Search.Asked);
        Assert.Null(hauling.NextStop);
        Assert.Null(hauling.CopyText);
        int changes = rig.Changes;
        rig.After(0.5);                                             // still no game: nothing new, so nothing for a screen to draw again
        Assert.Equal(changes, rig.Changes);

        rig.Search.Answer = request => new SearchResult { StarSystem = request.StarSystem };
        rig.Play(HaulRig.In(Alpha));
        Assert.Equal("No market data for stations in Alpha that fit your filters. Try older prices or another pad size.", hauling.Message);
        Assert.Equal(hauling.Message, hauling.ListMessage);

        rig.Search.Answer = request => new SearchResult { StarSystem = request.StarSystem, Exports = 4, Note = "Powerplay filter: 2 systems to buy in" };
        hauling.SearchAgain();
        Assert.Equal("No profitable trades within 30 ly of Alpha. Try a wider distance or older prices.", hauling.Message);
        Assert.Equal(hauling.Message + "   ·   Powerplay filter: 2 systems to buy in", hauling.ListMessage);

        rig.Search.Answer = request => new SearchResult { StarSystem = request.StarSystem, Ok = false, Error = "Powerplay data from Spansh could not be loaded: timeout" };
        hauling.SearchAgain();
        Assert.Equal("Powerplay data from Spansh could not be loaded: timeout", hauling.Message);

        rig.Search.Hold = true;
        hauling.SearchAgain();
        Assert.Equal("Searching trades from Alpha…", hauling.Message);
        rig.Search.Fail("A task was canceled.");                    // the search itself broke
        Assert.Equal("Couldn't reach the market data service: A task was canceled.", hauling.Message);
        Assert.False(hauling.LastResult?.Ok);

        rig.Search.Hold = false;
        rig.Change("""{ "maxLy": 40 }""");                          // a setting changed and changed back: the same question as before,
        rig.Change("""{ "maxLy": 30 }""");                          // and a failure is an answer too, so nothing is asked this soon
        rig.After(0.7);
        Assert.Equal(4, rig.Search.Asked.Count);
        Assert.Equal("Couldn't reach the market data service: A task was canceled.", hauling.Message);
    }

    // ---- lookups ----

    [Fact]
    public void Sell_my_cargo_steps_through_the_hold_and_ends_when_the_cargo_is_sold()
    {
        // The gold was bought before Studio started: that purchase gives the price paid, and accepts nothing.
        using var rig = new HaulRig([.. HaulRig.In(Alpha), HaulRig.Buy(Alpha, "gold", price: 9000)],
            answer: request => request.Mode == "lookup"
                ? HaulRig.Found(request, HaulRig.SellAt(request, Beta), HaulRig.SellAt(request, Gamma))
                : HaulRig.Found(request, HaulRig.Gold(), HaulRig.Bauxite(), HaulRig.Silver()));
        Hauling hauling = rig.Hauling;
        Assert.Null(hauling.Accepted);
        rig.GameFile("Cargo.json", HaulRig.Hold(("silver", 100), ("drones", 12), ("gold", 300)));
        rig.GameFile("Market.json", HaulRig.PriceList(Alpha, ("gold", 9000, 500)));

        hauling.SellMyCargo();                                      // the biggest load first; limpets are not cargo to sell
        SearchRequest asked = rig.Search.Asked[1];
        Assert.Equal(("lookup", "Alpha", "gold", 300, 9000, "Your cargo hold", "near", 30),
            (asked.Mode, asked.StarSystem, asked.LookItem, asked.LookCount, asked.LookPaid, asked.LookFrom, asked.LookRank, asked.LookLy));
        Assert.Equal(101, asked.LiveMarket?.MarketId);              // the docked station's own price list goes along
        Lookup look = hauling.Look!;
        Assert.Equal(("gold", "Gold", 300, 9000, "cargo", "done"), (look.Id, look.Name, look.Count, look.Paid, look.Source, look.Status));
        Assert.Equal("2 places to sell Gold", hauling.ListMessage);
        Assert.Equal(new[] { "Beta Hub", "Gamma Dock" }, look.Rows.Select(row => row.SellStation));
        Assert.All(look.Rows, row => Assert.Equal(("Gold", false), (row.Commodity, row.BuyHere)));    // named as the hold names it
        Assert.Same(look.Rows[0], hauling.Shown);
        Assert.Equal(new Stop("SELL", "Beta", "Beta Hub", "sell at", Here: false, Ly: 20, "full", Plotted: false, Jumps: 2), hauling.NextStop);
        Assert.Equal(3, hauling.Trades.Count);                      // the trade list waits underneath

        Assert.False(hauling.NextTrade("F5"));                      // the next key walks the places
        Assert.Same(look.Rows[1], hauling.Shown);
        Assert.True(hauling.Show(look.Rows[0]));
        Assert.False(hauling.Show(hauling.Trades[1]));

        hauling.ToggleAccept("F6");                                 // not during a lookup
        Assert.Null(hauling.Accepted);
        Assert.Equal(new HaulNote("F6 goes back to your trades first", Good: false), hauling.Note);
        Assert.True(hauling.BlockedByLookup("F6"));
        Assert.False(hauling.Accept(hauling.Trades[0]));
        rig.Play(HaulRig.Buy(Alpha, "gold"));                       // nor does a purchase accept anything now
        Assert.Null(hauling.Accepted);

        hauling.SellMyCargo();                                      // the next kind of cargo; nobody knows what the silver cost
        Assert.Equal(("silver", 100, 0), (rig.Search.Asked[2].LookItem, rig.Search.Asked[2].LookCount, rig.Search.Asked[2].LookPaid));
        Assert.Equal("Silver", hauling.Look?.Name);
        hauling.SellMyCargo();                                      // after the last kind: back to the trades
        Assert.Null(hauling.Look);
        Assert.Equal("gold", hauling.Shown?.CommodityId);
        Assert.Equal("3 trades from Alpha", hauling.ListMessage);
        Assert.False(hauling.BlockedByLookup("F6"));

        hauling.SellMyCargo();
        rig.Play(HaulRig.Sell(Alpha, "gold"));                      // part of it sold: two seconds on it is still in the hold
        rig.After(2);
        Assert.Equal("gold", hauling.Look?.Id);
        rig.Play(HaulRig.Sell(Alpha, "gold"));
        rig.GameFile("Cargo.json", HaulRig.Hold(("silver", 100)));
        rig.After(1.5);
        Assert.NotNull(hauling.Look);
        rig.After(0.5);
        Assert.Null(hauling.Look);                                  // all sold: the trades are back by themselves

        rig.GameFile("Cargo.json", HaulRig.Hold());
        hauling.SellMyCargo();
        Assert.Equal(("none", "Your cargo hold is empty, so there is nothing to look up."), (hauling.Look?.Status, hauling.Message));
        Assert.Null(hauling.Shown);
        hauling.SellMyCargo();
        Assert.Null(hauling.Look);
    }

    [Fact]
    public void A_picked_commodity_is_looked_up_for_a_full_hold_at_the_docked_stations_price()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;
        rig.Search.Commodities = [("silver", "Silver"), ("gold", "Gold"), ("bauxite", "Bauxite")];
        Assert.Equal(new[] { new CommodityChoice("bauxite", "Bauxite", 0), new("gold", "Gold", 0), new("silver", "Silver", 0) }, hauling.CommodityChoices);

        // The station sells gold and tea; it has water on its list but none in stock.
        rig.GameFile("Market.json", HaulRig.PriceList(Alpha, ("tea", 1500, 80), ("gold", 9000, 500), ("water", 260, 0)));
        Assert.Equal(new[] { new CommodityChoice("gold", "Gold", 9000), new("tea", "Tea", 1500), new("bauxite", "Bauxite", 0), new("silver", "Silver", 0) },
            hauling.CommodityChoices);

        rig.Search.Answer = request => HaulRig.Found(request);
        Assert.True(hauling.WhereToSell("Gold"));
        SearchRequest asked = rig.Search.Asked[1];
        Assert.Equal(("lookup", "gold", 400, 9000, "Alpha Port", "profit"), (asked.Mode, asked.LookItem, asked.LookCount, asked.LookPaid, asked.LookFrom, asked.LookRank));
        Assert.Equal(("pick", "none"), (hauling.Look?.Source, hauling.Look?.Status));
        Assert.Equal("No station within 30 ly is known to pay more than the 9,000 cr you paid for Gold. Try a bigger route distance or older prices.", hauling.Message);

        Assert.True(hauling.WhereToSell("silver"));                 // not for sale here: no price to beat
        Assert.Equal((0, ""), (rig.Search.Asked[2].LookPaid, rig.Search.Asked[2].LookFrom));
        Assert.Equal("No station within 30 ly is known to buy Silver. Try a bigger route distance or older prices.", hauling.ListMessage);

        rig.Search.Answer = request => new SearchResult { Ok = false, Error = "The operation has timed out." };
        Assert.True(hauling.WhereToSell("silver"));
        Assert.Equal(("error", "Couldn't reach the market data service: The operation has timed out."), (hauling.Look?.Status, hauling.Look?.Message));

        hauling.SellMyCargo();                                      // from a picked commodity that key just goes back
        Assert.Null(hauling.Look);
        Assert.False(hauling.WhereToSell("  "));
    }

    [Fact]
    public void A_lookup_overtaken_by_another_never_shows_its_answer()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;
        rig.GameFile("Cargo.json", HaulRig.Hold(("gold", 300), ("silver", 100)));
        rig.Search.Answer = request => HaulRig.Found(request, HaulRig.SellAt(request, Beta));
        rig.Search.Hold = true;

        hauling.SellMyCargo();
        Assert.Equal(("searching", "Looking for places to sell Gold…"), (hauling.Look?.Status, hauling.Message));
        hauling.SellMyCargo();                                      // on to the silver before the gold's answer came
        int changes = rig.Changes;
        rig.Search.Finish();
        Assert.Equal(("silver", "searching"), (hauling.Look?.Id, hauling.Look?.Status));
        Assert.Null(hauling.Shown);
        Assert.Equal(changes, rig.Changes);                         // nothing for a screen to draw again

        rig.Search.Finish();
        Assert.Equal(("silver", "done", "silver"), (hauling.Look?.Id, hauling.Look?.Status, hauling.Shown?.CommodityId));

        hauling.SellMyCargo();                                      // no more kinds of cargo: back to the trades
        hauling.WhereToSell("gold");
        hauling.BackToTrades();                                     // left before the answer came
        changes = rig.Changes;
        rig.Search.Finish();
        Assert.Equal(changes, rig.Changes);
        Assert.Null(hauling.Look);
        Assert.Equal("gold", hauling.Shown?.CommodityId);           // the trade list's gold, not a lookup row
        Assert.Equal(101, hauling.Shown?.BuyMarketId);
    }

    [Fact]
    public void A_lookup_whose_search_stops_when_told_is_not_written_up_as_a_failure()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;
        rig.GameFile("Cargo.json", HaulRig.Hold(("gold", 300), ("silver", 100)));
        rig.Search.Hold = rig.Search.Stops = true;

        hauling.SellMyCargo();
        Lookup gold = hauling.Look!;
        int changes = rig.Changes;
        hauling.SellMyCargo();                                      // on to the silver: the gold's search ends while gold is still on screen
        Assert.Equal(("searching", "Looking for places to sell Gold…"), (gold.Status, gold.Message));
        Assert.Equal(changes + 1, rig.Changes);                     // drawn once, for the silver

        Lookup silver = hauling.Look!;
        hauling.BackToTrades();                                     // the same when the player just goes back
        Assert.Equal(("silver", "searching"), (silver.Id, silver.Status));
        Assert.Equal(changes + 2, rig.Changes);
        Assert.Null(hauling.Look);
    }
}
