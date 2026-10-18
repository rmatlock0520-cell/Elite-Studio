using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EliteStudio.App;
using EliteStudio.Market;
using static EliteStudio.Tests.HaulRig;   // its places (Alpha, Beta, Gamma) and OldFile

namespace EliteStudio.Tests;

/// <summary>
/// Studio Market's routes as the Market page uses them: a real Hauling on a practice game and a
/// stand-in search (the rig of HaulingTests), asked through the web server without the network.
/// One test listens on a real port, because that is the only way to hear a "market" event.
/// </summary>
public sealed class MarketApiTests : IDisposable
{
    static readonly string OwnersOldFile = OldFile("owner.json");

    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioTests-").FullName;
    readonly WebServer web;
    readonly List<TaskCompletionSource> rests = [];   // the rest after each "market" event; it lasts until a test ends it
    HaulRig? rig;

    public MarketApiTests() => web = new WebServer(temp);

    public void Dispose()
    {
        web.Dispose();
        rig?.Dispose();
        Directory.Delete(temp, recursive: true);
    }

    /// <summary>Studio starts on a practice game with this journal, and the Market routes are registered.</summary>
    Hauling Start(IEnumerable<string> history, Func<SearchRequest, SearchResult>? answer = null, string? oldSettings = null, string? savedSettings = null)
    {
        rig = new HaulRig(history, answer, oldSettings, savedSettings);
        MarketApi.Map(web, rig.Hauling, gap =>
        {
            Assert.Equal(300, gap.TotalMilliseconds);
            rests.Add(new TaskCompletionSource());
            return rests[^1].Task;
        });
        return rig.Hauling;
    }

    async Task<JsonElement> Ask(string method, string route, string body = "{}", int expect = 200)
    {
        WebResponse answer = await web.Handle(new WebRequest(method, "/api/market/" + route, new Dictionary<string, string>(), body));
        Assert.True(answer.Status == expect, $"{method} {route} answered {answer.Status}: {Encoding.UTF8.GetString(answer.Body)}");
        return JsonDocument.Parse(answer.Body).RootElement;
    }

    Task<JsonElement> Post(string route, string body = "{}") => Ask("POST", route, body);

    /// <summary>A row as the page sends it back: the three values that say which trade it is.</summary>
    static string Row(TradeRow row) => JsonSerializer.Serialize(new { row.CommodityId, row.BuyMarketId, row.SellMarketId }, Settings.Json);

    static string? Said(JsonElement page) => page.GetProperty("said").GetString();

    /// <summary>A part of an answer as JSON text, with "·" left as it is.</summary>
    static string Text(JsonElement part) => JsonSerializer.Serialize(part, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    static bool IsNull(JsonElement page, string name) => page.GetProperty(name).ValueKind == JsonValueKind.Null;

    // ---- the one big answer ----

    [Fact]
    public async Task One_answer_holds_everything_the_page_shows()
    {
        Hauling hauling = Start(HaulRig.In(Alpha));
        rig!.Search.Commodities = [("silver", "Silver"), ("gold", "Gold")];
        rig.GameFile("Market.json", HaulRig.PriceList(Alpha, ("gold", 9000, 500)));

        JsonElement page = await Ask("GET", "hauling");
        Assert.Equal(new[]
        {
            "rows", "shown", "listMessage", "message", "listOneWay", "searching", "lastSearch", "accepted", "nextStop", "lookup",
            "settings", "carryFrom", "inUse", "settingLines", "listSorts", "presets", "commodities", "copiedFrom", "said",
        }, page.EnumerateObject().Select(part => part.Name));

        // the list: the search's rows in its order, under the names the page's columns read
        JsonElement rows = page.GetProperty("rows");
        Assert.Equal(new[] { "Gold", "Bauxite", "Silver" }, rows.EnumerateArray().Select(row => row.GetProperty("commodity").GetString()));
        Assert.All(new[]
        {
            "rank", "commodityId", "commodity", "perHour", "profitPerTon", "buyStation", "buySystem", "buyDistanceLy", "jumpsToBuy", "buyPrice", "stock",
            "sellStation", "sellSystem", "sellPad", "sellStarLs", "sellPrice", "demand", "distanceLy", "jumpsOut", "jumpsBack", "profitPerTrip", "return",
            "returnProfitPerTon", "roundTripProfit", "minutes", "updated", "ageHours", "buyPower", "sellPower", "buyMarketId", "sellMarketId",
        }, name => Assert.True(rows[0].TryGetProperty(name, out _), $"a row has no \"{name}\""));
        Assert.Equal((1, 30_000_000, "Alpha Port", 101, "Beta Hub", 202, 20.0), (rows[0].GetProperty("rank").GetInt32(), rows[0].GetProperty("perHour").GetInt32(),
            rows[0].GetProperty("buyStation").GetString(), rows[0].GetProperty("buyMarketId").GetInt32(), rows[0].GetProperty("sellStation").GetString(),
            rows[0].GetProperty("sellMarketId").GetInt32(), rows[0].GetProperty("distanceLy").GetDouble()));

        Assert.Equal((0, "3 trades from Alpha", false, false, "12:00"), (page.GetProperty("shown").GetInt32(), page.GetProperty("listMessage").GetString(),
            page.GetProperty("listOneWay").GetBoolean(), page.GetProperty("searching").GetBoolean(), page.GetProperty("lastSearch").GetString()));
        Assert.All(new[] { "accepted", "lookup", "copiedFrom", "said" }, name => Assert.True(IsNull(page, name), $"\"{name}\" is not empty"));
        Assert.Equal("""{"what":"SELL","system":"Beta","station":"Beta Hub","verb":"sell at","here":false,"ly":20,"load":"full","plotted":false,"jumps":2}""",
            Text(page.GetProperty("nextStop")));

        // the settings as saved, and what the search really uses once the ship has had its say
        JsonElement settings = page.GetProperty("settings");
        Assert.Equal(("near", true, 15, 30, 48, 0.0, "auto", "rank"), (settings.GetProperty("startMode").GetString(), settings.GetProperty("returns").GetBoolean(),
            settings.GetProperty("buyLy").GetInt32(), settings.GetProperty("maxLy").GetInt32(), settings.GetProperty("maxAgeHours").GetInt32(),
            settings.GetProperty("jumpRange").GetDouble(), settings.GetProperty("pad").GetString(), settings.GetProperty("listSort").GetString()));
        Assert.Equal("""{"pad":"M","shipPad":"M","cargoTons":400,"jumpEmpty":27.8,"jumpFull":14.4,"shipEmpty":27.8,"shipFull":14.4,"jumpRangesSwapped":false}""", Text(page.GetProperty("inUse")));

        // the choices the overlay menu steps through, the orders of its short list, the presets and the commodities to pick from
        JsonElement lines = page.GetProperty("settingLines");
        Assert.Equal(new[] { "returns", "rankBy", "maxLy", "buyLy", "maxAgeHours", "startMode", "minSupply", "minDemand" }, lines.EnumerateArray().Select(line => line.GetProperty("key").GetString()));
        Assert.Equal(("Max. price age", "2 days", true), (lines[4].GetProperty("label").GetString(), lines[4].GetProperty("text").GetString(), lines[4].GetProperty("usable").GetBoolean()));
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 8, 12, 24, 48, 72, 168, 720 }, lines[4].GetProperty("choices").EnumerateArray().Select(choice => choice.GetProperty("value").GetInt32()));
        Assert.Equal("100 t or more", lines[6].GetProperty("choices")[1].GetProperty("text").GetString());
        Assert.Equal(new[] { "rank:Best first", "hour:Profit per hour", "ton:Profit per ton", "load:Profit per trip", "jumps:Fewest jumps", "ly:Shortest trip", "age:Newest prices" },
            page.GetProperty("listSorts").EnumerateArray().Select(sort => $"{sort.GetProperty("key").GetString()}:{sort.GetProperty("name").GetString()}"));
        Assert.Equal("""[{"name":"Quick one-way hops","text":"one way · routes to 40 ly · ranked by profit per hour","active":false},"""
            + """{"name":"Long round trips","text":"round trip · routes to 120 ly · ranked by trip profit","active":false}]""",
            Text(page.GetProperty("presets")));
        Assert.Equal("""[{"id":"gold","name":"Gold","priceHere":9000},{"id":"silver","name":"Silver","priceHere":0}]""", Text(page.GetProperty("commodities")));
        Assert.Same(hauling.Trades[0], hauling.Shown);   // asking changes nothing
    }

    [Fact]
    public async Task Before_the_game_has_been_seen_the_answer_says_what_is_waited_for()
    {
        Start([]);
        rig!.After(0.5);

        JsonElement page = await Ask("GET", "hauling");
        Assert.Equal((0, -1, "", "Waiting for Elite… (start the game, or jump to a system)"), (page.GetProperty("rows").GetArrayLength(), page.GetProperty("shown").GetInt32(),
            page.GetProperty("listMessage").GetString(), page.GetProperty("message").GetString()));
        Assert.All(new[] { "lastSearch", "nextStop", "accepted" }, name => Assert.True(IsNull(page, name), $"\"{name}\" is not empty"));
        Assert.Equal("""{"pad":"L","shipPad":"L","cargoTons":0,"jumpEmpty":0,"jumpFull":0,"shipEmpty":0,"shipFull":0,"jumpRangesSwapped":false}""", Text(page.GetProperty("inUse")));   // an unknown ship counts as large

        Assert.Equal("No trade to accept yet.", Said(await Post("accept")));
        Assert.Equal("Waiting for Elite… (start the game, or jump to a system)", Said(await Post("where-to-sell", """{"commodity":"gold"}""")));
        Assert.Null(Said(await Post("drop")));              // nothing accepted: nothing to say
        Assert.Null(Said(await Post("sell-cargo")));
        Assert.Empty(rig.Search.Asked);
    }

    // ---- the list: show, accept, drop, search again ----

    [Fact]
    public async Task A_row_is_shown_accepted_switched_and_dropped_by_the_three_values_that_name_it()
    {
        Hauling hauling = Start(HaulRig.In(Alpha));
        TradeRow bauxite = hauling.Trades[1], silver = hauling.Trades[2];

        JsonElement page = await Post("show", Row(silver));         // a click on the third row
        Assert.Equal((2, null, "Delta"), (page.GetProperty("shown").GetInt32(), Said(page), page.GetProperty("nextStop").GetProperty("system").GetString()));
        Assert.Same(silver, hauling.Shown);

        page = await Post("accept");                                // the Accept button with no row picked: the trade on the overlay
        Assert.Equal("Accepted: Silver to Beta Hub. The overlay stays on it until you finish it or drop it.", Said(page));
        Assert.Equal(("silver", "buy", 2), (page.GetProperty("accepted").GetProperty("commodityId").GetString(), hauling.Leg, page.GetProperty("shown").GetInt32()));

        page = await Post("show", Row(bauxite));                    // another row clicked: the overlay stays where it is
        Assert.Equal("The overlay stays on your accepted trade. Tick this row's box, or click Accept trade, to switch to it.", Said(page));
        Assert.Equal(2, page.GetProperty("shown").GetInt32());
        Assert.Null(Said(await Post("show", Row(silver))));         // the accepted row itself: nothing to say

        page = await Post("accept", Row(bauxite));                  // its box ticked: switched without a search
        Assert.Equal("Accepted: Bauxite to Gamma Dock. The overlay stays on it until you finish it or drop it.", Said(page));
        Assert.Equal((1, "bauxite"), (page.GetProperty("shown").GetInt32(), hauling.Accepted?.CommodityId));
        Assert.Single(rig!.Search.Asked);

        Assert.Equal("That trade is no longer on the list.", Said(await Post("accept", Row(HaulRig.Trade("tea", Alpha, Beta)))));
        Assert.Same(bauxite, hauling.Accepted);
        Assert.Null(Said(await Post("show", Row(HaulRig.Trade("tea", Alpha, Beta)))));   // a row of a list long gone: the fresh picture is the answer

        page = await Post("drop");
        Assert.Equal("Dropped. Searching again from where you are…", Said(page));
        Assert.True(IsNull(page, "accepted"));
        Assert.Equal((2, 0, ""), (rig.Search.Asked.Count, page.GetProperty("shown").GetInt32(), hauling.Leg));

        page = await Post("search");                                // the Search again button always asks afresh
        Assert.Equal((3, "3 trades from Alpha"), (rig.Search.Asked.Count, page.GetProperty("listMessage").GetString()));
        Assert.Equal("gold", hauling.Shown?.CommodityId);           // back on the best trade
    }

    [Fact]
    public async Task While_a_search_runs_the_answer_says_so_and_a_one_way_list_is_marked()
    {
        Hauling hauling = Start(HaulRig.In(Alpha), request => HaulRig.Found(request, HaulRig.Trade("bauxite", Alpha, Gamma, oneWay: true)));
        rig!.Search.Hold = true;
        rig.Play(HaulRig.Undock(Alpha), HaulRig.Jump(Beta));

        JsonElement page = await Ask("GET", "hauling");
        Assert.Equal((true, 0, "Searching trades from Beta…", -1), (page.GetProperty("searching").GetBoolean(), page.GetProperty("rows").GetArrayLength(),
            page.GetProperty("listMessage").GetString(), page.GetProperty("shown").GetInt32()));

        rig.Search.Finish();
        page = await Ask("GET", "hauling");
        Assert.Equal((false, 1, "1 trades from Beta", true), (page.GetProperty("searching").GetBoolean(), page.GetProperty("rows").GetArrayLength(),
            page.GetProperty("listMessage").GetString(), page.GetProperty("listOneWay").GetBoolean()));
        Assert.True(hauling.ListOneWay);
    }

    // ---- where can I sell ----

    [Fact]
    public async Task Lookups_take_the_list_over_until_the_player_goes_back()
    {
        Hauling hauling = Start([.. HaulRig.In(Alpha), HaulRig.Buy(Alpha, "gold", price: 9000)], request => request.Mode == "lookup"
            ? HaulRig.Found(request, HaulRig.SellAt(request, Beta), HaulRig.SellAt(request, Gamma))
            : HaulRig.Found(request, HaulRig.Gold(), HaulRig.Bauxite(), HaulRig.Silver()));
        rig!.GameFile("Cargo.json", HaulRig.Hold(("gold", 300), ("silver", 100)));

        JsonElement page = await Post("sell-cargo");                // the biggest load in the hold
        Assert.Equal("""{"id":"gold","name":"Gold","count":300,"paid":9000,"from":"Your cargo hold","source":"cargo","status":"done"}""", Text(page.GetProperty("lookup")));
        Assert.Equal(new[] { "Beta Hub", "Gamma Dock" }, page.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("sellStation").GetString()));
        Assert.Equal(("2 places to sell Gold", 0, "Gold"), (page.GetProperty("listMessage").GetString(), page.GetProperty("shown").GetInt32(), page.GetProperty("rows")[0].GetProperty("commodity").GetString()));

        page = await Post("show", Row(hauling.Look!.Rows[1]));      // a place clicked in the list
        Assert.Equal((1, "Gamma", null), (page.GetProperty("shown").GetInt32(), page.GetProperty("nextStop").GetProperty("system").GetString(), Said(page)));
        Assert.Equal("Click \"Back to trades\" first, then pick the trade to accept.", Said(await Post("accept", Row(hauling.Trades[0]))));
        Assert.Null(hauling.Accepted);

        page = await Post("sell-cargo");                            // again: the next kind of cargo
        Assert.Equal(("silver", "2 places to sell Silver"), (page.GetProperty("lookup").GetProperty("id").GetString(), page.GetProperty("listMessage").GetString()));
        page = await Post("sell-cargo");                            // after the last kind: the trades again
        Assert.True(IsNull(page, "lookup"));
        Assert.Equal((3, "3 trades from Alpha"), (page.GetProperty("rows").GetArrayLength(), page.GetProperty("listMessage").GetString()));

        Assert.Equal("Pick a commodity from the list first.", Said(await Post("where-to-sell", """{"commodity":"  "}""")));
        Assert.Equal("Pick a commodity from the list first.", Said(await Post("where-to-sell", """{"commodity":null}""")));
        Assert.Null(hauling.Look);

        rig.Search.Hold = true;
        page = await Post("where-to-sell", """{"commodity":"Bauxite"}""");   // a picked commodity, for a full hold
        Assert.Equal(("bauxite", "pick", "searching", 400), (page.GetProperty("lookup").GetProperty("id").GetString(), page.GetProperty("lookup").GetProperty("source").GetString(),
            page.GetProperty("lookup").GetProperty("status").GetString(), page.GetProperty("lookup").GetProperty("count").GetInt32()));
        Assert.Equal((0, -1, "Looking for places to sell Bauxite…"), (page.GetProperty("rows").GetArrayLength(), page.GetProperty("shown").GetInt32(), page.GetProperty("listMessage").GetString()));

        page = await Post("back");                                  // left before the answer came
        Assert.True(IsNull(page, "lookup"));
        Assert.Equal(3, page.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task The_page_reports_its_copy_of_the_next_stop_and_the_overlay_says_so()
    {
        Hauling hauling = Start(HaulRig.In(Alpha));

        JsonElement page = await Post("copied", """{"text":" Beta "}""");
        Assert.Null(Said(page));                                    // the page did the copying and words it itself
        Assert.Equal(new HaulNote("✔ Beta copied", Good: true), hauling.Note);

        page = await Ask("POST", "copied", "{}", expect: 400);
        Assert.Equal((false, "Nothing was copied."), (page.GetProperty("ok").GetBoolean(), page.GetProperty("error").GetString()));
    }

    // ---- settings, the short list's order, presets ----

    [Fact]
    public async Task Only_the_named_settings_change_and_a_typed_decimal_arrives_as_a_number()
    {
        Hauling hauling = Start(HaulRig.In(Alpha));

        JsonElement page = await Post("settings", """{"maxLy":40,"jumpEmpty":14.5,"returns":false}""");
        Assert.Equal((40, 14.5, false, 15), (hauling.Settings.MaxLy, hauling.Settings.JumpEmpty, hauling.Settings.Returns, hauling.Settings.BuyLy));
        Assert.Equal((40, 14.5), (page.GetProperty("settings").GetProperty("maxLy").GetInt32(), page.GetProperty("settings").GetProperty("jumpEmpty").GetDouble()));
        Assert.Equal((40, 14.5), (rig!.Saved().MaxLy, rig.Saved().JumpEmpty));                 // saved at once
        Assert.Equal((14.5, 14.4), (page.GetProperty("inUse").GetProperty("jumpEmpty").GetDouble(), page.GetProperty("inUse").GetProperty("jumpFull").GetDouble()));
        rig.After(0.7);
        Assert.Equal((2, 40, false), (rig.Search.Asked.Count, rig.Search.Asked[1].MaxLy, rig.Search.Asked[1].Returns));    // the search they asked for

        page = await Post("settings", """{"jumpRange":27,"jumpEmpty":14,"cargoOverride":388,"pad":"S","startMode":"anywhere"}""");
        Assert.Equal("""{"pad":"S","shipPad":"M","cargoTons":388,"jumpEmpty":27,"jumpFull":14,"shipEmpty":27.8,"shipFull":14.6,"jumpRangesSwapped":true}""", Text(page.GetProperty("inUse")));
        Assert.Equal((27.0, 14.0), (page.GetProperty("settings").GetProperty("jumpRange").GetDouble(), page.GetProperty("settings").GetProperty("jumpEmpty").GetDouble()));   // kept as typed
        JsonElement buyWithin = page.GetProperty("settingLines")[3];
        Assert.Equal(("buyLy", false, "Not used when trades start anywhere"), (buyWithin.GetProperty("key").GetString(), buyWithin.GetProperty("usable").GetBoolean(), buyWithin.GetProperty("text").GetString()));

        MarketSettings before = hauling.Settings;
        foreach (string unreadable in new[] { "not json", "", "null", "[1]", """{"maxLy":"far"}""", """{"maxLy":50,"carriers":"yes"}""" })
        {
            page = await Ask("POST", "settings", unreadable, expect: 400);
            Assert.Equal((false, "These settings could not be read, so nothing was changed."), (page.GetProperty("ok").GetBoolean(), page.GetProperty("error").GetString()));
        }
        Assert.Same(before, hauling.Settings);

        page = await Post("settings", """{"listSort":"hour"}""");   // the order of the overlay menu's short list; no reason to search
        Assert.Equal(("hour", "hour"), (page.GetProperty("settings").GetProperty("listSort").GetString(), rig.Saved().ListSort));
        Assert.Equal("rank", (await Post("settings", """{"listSort":"sideways"}""")).GetProperty("settings").GetProperty("listSort").GetString());   // no such order: the search's own
        rig.After(0.7);
        Assert.Equal(3, rig.Search.Asked.Count);                    // one more, for the second settings change only
    }

    [Fact]
    public async Task Presets_are_used_saved_and_removed_with_the_old_windows_words()
    {
        Hauling hauling = Start(HaulRig.In(Alpha));

        JsonElement page = await Post("preset/use", """{"name":"quick ONE-way hops"}""");
        Assert.Equal("Using \"Quick one-way hops\": one way · routes to 40 ly · ranked by profit per hour.", Said(page));
        Assert.Equal(new[] { true, false }, page.GetProperty("presets").EnumerateArray().Select(preset => preset.GetProperty("active").GetBoolean()));
        Assert.Equal((false, 40), (hauling.Settings.Returns, hauling.Settings.MaxLy));
        Assert.Equal("Pick a preset from the list first.", Said(await Post("preset/use")));
        Assert.Equal("Pick a preset from the list first.", Said(await Post("preset/use", """{"name":"No such preset"}""")));

        Assert.Equal("Type a name for the preset first.", Said(await Post("preset/save", """{"name":"  "}""")));
        page = await Post("preset/save", """{"name":" Mine "}""");
        Assert.Equal("Saved \"Mine\".", Said(page));
        Assert.Equal("""{"name":"Mine","text":"one way · buy within 15 ly · routes to 40 ly · prices under 2 days · ranked by profit per hour","active":true}""",
            Text(page.GetProperty("presets")[2]));
        Assert.Equal("Updated \"MINE\" with the settings as they are now.", Said(await Post("preset/save", """{"name":"MINE"}""")));
        Assert.Equal(3, rig!.Saved().Presets.Count);

        page = await Post("preset/remove", """{"name":"mine"}""");
        Assert.Equal("Deleted \"MINE\". Your settings were not changed.", Said(page));
        Assert.Equal((2, 40), (page.GetProperty("presets").GetArrayLength(), hauling.Settings.MaxLy));
        Assert.Equal("Pick a preset from the list first.", Said(await Post("preset/remove", """{"name":"mine"}""")));
        Assert.Equal("Pick a preset from the list first.", Said(await Post("preset/remove")));
    }

    // ---- the first run's copy from the Elite Haul Overlay ----

    [Fact]
    public async Task The_old_tools_file_is_named_from_the_first_runs_copy_until_the_player_has_read_of_it()
    {
        Start(HaulRig.In(Alpha), oldSettings: OwnersOldFile);       // Studio's very first run, next to the owner's old settings

        Assert.Equal(OwnersOldFile, (await Ask("GET", "hauling")).GetProperty("copiedFrom").GetString());
        // It stays news until it has been read, whatever is changed meanwhile, and no request can put another name there.
        Assert.Equal(OwnersOldFile, (await Post("settings", """{"maxLy":60,"copiedFrom":"somewhere else"}""")).GetProperty("copiedFrom").GetString());
        Assert.Equal(OwnersOldFile, (await Post("preset/use", """{"name":"Long round trips"}""")).GetProperty("copiedFrom").GetString());
        Assert.Equal((OwnersOldFile, 120), (rig!.Saved().CopiedFrom, rig.Saved().MaxLy));   // kept with the settings: still news after a restart

        JsonElement page = await Post("old-tool-read");             // the note's OK button
        Assert.True(IsNull(page, "copiedFrom"));
        Assert.Null(Said(page));
        Assert.Equal(((string?)null, 120), (rig.Saved().CopiedFrom, rig.Saved().MaxLy));    // forgotten for good; the settings themselves stay
        Assert.True(IsNull(await Post("old-tool-read"), "copiedFrom"));                     // pressed again: nothing left to forget
    }

    [Fact]
    public async Task Settings_that_are_studios_own_name_no_file()
    {
        // A later run: Studio has its own settings, so the old tool's file is not looked at.
        Start(HaulRig.In(Alpha), oldSettings: OwnersOldFile, savedSettings: """{ "market": { "maxLy": 60 } }""");
        Assert.True(IsNull(await Ask("GET", "hauling"), "copiedFrom"));
    }

    // ---- the "market" event ----

    [Fact]
    public async Task Changes_reach_a_listening_page_as_market_events_with_a_rest_in_between()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        Hauling hauling = Start(HaulRig.In(Alpha));
        Assert.True(web.Start(47880));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var events = new StreamReader(await http.GetStreamAsync($"http://127.0.0.1:{web.Port}/api/events?t={web.Token}", cancel));

        async Task<JsonElement> NextMarketEvent()
        {
            while (true)
            {
                string? line = await events.ReadLineAsync(cancel).AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancel);
                Assert.NotNull(line);   // null: the stream ended
                if (line.StartsWith("data: ", StringComparison.Ordinal)) return JsonSerializer.Deserialize<JsonElement>(line[6..]);
                Assert.True(line is "" or ": connected" or "event: market", $"unexpected line on the stream: {line}");
            }
        }
        static string Order(JsonElement page) => page.GetProperty("settings").GetProperty("listSort").GetString()!;

        hauling.SortListBy("hour");                                 // a change: told at once, and the same picture as the GET gives
        JsonElement first = await NextMarketEvent();
        Assert.Equal("hour", Order(first));
        Assert.Equal(Text(await Ask("GET", "hauling")), Text(first));
        Assert.Single(rests);

        hauling.SortListBy("ton");                                  // two more during the rest: nothing is sent
        await Post("show", Row(hauling.Trades[1]));
        hauling.SortListBy("load");
        Assert.Single(rests);
        rests[0].SetResult();                                       // the rest is over: one event carries all of it
        JsonElement second = await NextMarketEvent();
        Assert.Equal(("load", 1), (Order(second), second.GetProperty("shown").GetInt32()));
        Assert.Equal(2, rests.Count);

        rests[1].SetResult();                                       // a rest in which nothing happened: nothing is sent after it
        hauling.SortListBy("jumps");
        Assert.Equal("jumps", Order(await NextMarketEvent()));
        Assert.Equal(3, rests.Count);
    }
}
