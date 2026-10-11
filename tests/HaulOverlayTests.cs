using System.Runtime.InteropServices;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Market;
using EliteStudio.Nav;
using EliteStudio.Overlay;
using static EliteStudio.Tests.HaulRig;   // its places (Alpha, Beta, Gamma, Delta), Place itself, and OldFile

namespace EliteStudio.Tests;

/// <summary>
/// Hauling in the overlay, wired as the app wires it but without a window: a real Hauling on the
/// practice game of <see cref="HaulRig"/> (used as it is), a real overlay host that takes no keys, a
/// real Studio Nav with its exploring trip, the rig's clock, and stand-ins for the clipboard, for
/// waiting on it and for the haul box taking clicks. Hauling's actions are pressed the way a click
/// on one of its buttons passes them on.
/// </summary>
sealed class BoxRig : IDisposable
{
    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioHaulOverlay-").FullName;
    readonly WebServer web;
    readonly Spansh spansh = new(new SpanshStandIn());

    /// <param name="overlay">The overlay settings already saved; none saved yet when left out, as on the very first run.</param>
    /// <param name="oldSettings">A Haul Overlay settings file to copy from on that first run; none when left out.</param>
    /// <param name="route">A Studio Nav route saved earlier; none when left out.</param>
    /// <param name="market">Studio's own "market" settings as saved, as JSON; the standard ones when left out.</param>
    public BoxRig(IEnumerable<string>? history = null, Func<SearchRequest, SearchResult>? answer = null, OverlaySettings? overlay = null,
        string? oldSettings = null, NavRoute? route = null, string? market = null)
    {
        Haul = new HaulRig(history ?? HaulRig.In(HaulRig.Alpha), answer ?? Trades, savedSettings: market is null ? null : $"{{ \"market\": {market} }}");
        // Its own file: the rig's Hauling keeps its settings in another one. In the app both share one.
        var settings = new Settings(SettingsFile);
        if (overlay is not null) settings.Set("overlay", overlay);
        if (route is not null) settings.Set("nav", new NavSettings { Route = route });
        web = new WebServer(temp);
        Overlay = new OverlayHost(web, settings, Game, headless: true);
        Nav = new NavRoutes(Game, settings, spansh);
        Explore = new ExploreTrip(Game, settings);
        Box = new HaulOverlay(Overlay, Hauling, Nav, Explore, Game, () => Haul.Clock, oldSettings ?? Path.Combine(temp, "no Haul Overlay here.json"))
        {
            ToClipboard = text =>
            {
                ClipboardTries++;
                if (ClipboardBusy-- > 0) throw new COMException("Another program has the clipboard open.");
                Clipboard = text;
            },
            Wait = time =>
            {
                Waits.Add(time.TotalMilliseconds);
                return Task.CompletedTask;
            },
            Clickable = () => Clickable,
        };
    }

    public HaulRig Haul { get; }
    public OverlayHost Overlay { get; }
    public NavRoutes Nav { get; }
    public ExploreTrip Explore { get; }
    public HaulOverlay Box { get; }
    public Hauling Hauling => Haul.Hauling;
    public GameState Game => Haul.Watcher.State;
    public string SettingsFile => Path.Combine(temp, "settings.json");

    /// <summary>What is on the stand-in clipboard; null while nothing has reached it.</summary>
    public string? Clipboard { get; private set; }

    /// <summary>The clipboard refuses this many more times before it takes a text.</summary>
    public int ClipboardBusy { get; set; }

    public int ClipboardTries { get; private set; }

    /// <summary>Every wait for a busy clipboard, in milliseconds.</summary>
    public List<double> Waits { get; } = [];

    /// <summary>The haul box takes clicks, as it does while it is shown in the game. Without a window it never does by itself.</summary>
    public bool Clickable { get; set; }

    public void Keys(params string[] keys) => Array.ForEach(keys, Overlay.Press);

    /// <summary>Time passes, and both timers come round: the app's and the overlay's.</summary>
    public void After(double seconds)
    {
        Haul.After(seconds);
        Box.Tick();
    }

    /// <summary>The overlay settings as they are on disk.</summary>
    public OverlaySettings Saved() => new Settings(SettingsFile).Get<OverlaySettings>("overlay");

    public void Dispose()
    {
        Overlay.Dispose();
        web.Dispose();
        spansh.Dispose();
        Haul.Dispose();
        Directory.Delete(temp, recursive: true);
    }

    /// <summary>The rig's three trades, or two places to sell for a lookup, worded as the real search words its rows.</summary>
    public static SearchResult Trades(SearchRequest request) => request.Mode == "lookup"
        ? HaulRig.Found(request, Worded(HaulRig.SellAt(request, HaulRig.Beta)), Worded(HaulRig.SellAt(request, HaulRig.Gamma)))
        : HaulRig.Found(request, Worded(HaulRig.Gold()), Worded(HaulRig.Bauxite()), Worded(HaulRig.Silver()));

    public static SearchResult Nothing(SearchRequest request) => HaulRig.Found(request);

    public static TradeRow Worded(TradeRow row)
    {
        row.Updated = row.AgeHours < 1 ? "just now" : $"{row.AgeHours:0}h ago";
        row.Return = row.ReturnId.Length > 0 ? char.ToUpperInvariant(row.ReturnId[0]) + row.ReturnId[1..] : "";
        return row;
    }

    /// <summary>A Studio Nav route through the rig's places, two jumps and 25 ly along the route from each to the next.</summary>
    public static NavRoute RouteVia(params Place[] stops) => new(
        new PlotRequest { Kind = PlotRequest.Neutron, From = stops[0].System, To = stops[^1].System, Range = 30, Efficiency = 60, Supercharge = 4 },
        "practice", new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc),
        [.. stops.Select((stop, i) => new RouteHop(stop.System, 0, stop.At[0], stop.At[1], stop.At[2], i == 0 ? 0 : 25, 0, i == 0 ? 0 : 2, false, false, 0))]);
}

/// <summary>
/// The actions of hauling in the overlay: the menu's every move, when it closes by itself, what a
/// lookup blocks, the row of buttons, the clipboard, the first run's copy of the old tool's
/// settings, and the Studio Nav stop the nav box names.
/// </summary>
public class HaulOverlayTests
{
    // ---- the menu: its pages ----

    [Fact]
    public void The_menu_key_opens_on_the_page_names_and_steps_page_by_page_until_the_menu_is_closed()
    {
        using var rig = new BoxRig();
        Assert.Equal((false, "trades", -1, false, 0), rig.Box.Menu);

        rig.Keys("menu");
        Assert.Equal((true, "trades", -1, false, 0), rig.Box.Menu);
        rig.Keys("back");                                   // nothing above the page names
        Assert.Equal((true, "trades", -1), (rig.Box.Menu.Open, rig.Box.Menu.Page, rig.Box.Menu.Lit));
        rig.Keys("menu");
        Assert.Equal((true, "settings", -1), (rig.Box.Menu.Open, rig.Box.Menu.Page, rig.Box.Menu.Lit));
        rig.Keys("menu");
        Assert.Equal((true, "presets", -1), (rig.Box.Menu.Open, rig.Box.Menu.Page, rig.Box.Menu.Lit));
        rig.Keys("menu");                                   // past the last page: closed, and the first page next time
        Assert.Equal((false, "trades", -1, false, 0), rig.Box.Menu);

        // Closed by being left alone on another page, it opens on that page again.
        rig.Keys("menu", "menu", "forward", "forward");
        Assert.Equal((true, "settings", 1), (rig.Box.Menu.Open, rig.Box.Menu.Page, rig.Box.Menu.Lit));
        rig.After(15);
        Assert.False(rig.Box.Menu.Open);
        rig.Keys("menu");
        Assert.Equal((true, "settings", -1, false, 0), rig.Box.Menu);
        Assert.Single(rig.Haul.Search.Asked);               // walking through the menu asks the data service nothing
    }

    // ---- the menu: the trade list ----

    [Fact]
    public void Back_and_forward_open_the_trade_list_on_the_trade_on_screen_and_the_box_follows_the_lit_line()
    {
        using var rig = new BoxRig();
        Hauling hauling = rig.Hauling;
        rig.Keys("next");
        Assert.Equal("bauxite", hauling.Shown?.CommodityId);

        rig.Keys("forward");                                // closed: opens the list, lit on the trade on screen
        Assert.Equal((true, "trades", 2, false, 0), rig.Box.Menu);
        rig.Keys("forward");
        Assert.Equal((3, "silver"), (rig.Box.Menu.Lit, hauling.Shown?.CommodityId));
        rig.Keys("forward");                                // the last line: it stops there
        Assert.Equal((3, "silver"), (rig.Box.Menu.Lit, hauling.Shown?.CommodityId));
        rig.Keys("back", "back");
        Assert.Equal((1, "gold"), (rig.Box.Menu.Lit, hauling.Shown?.CommodityId));
        rig.Keys("back");                                   // the "Order" line: the box stays on the trade it had
        Assert.Equal((0, "gold"), (rig.Box.Menu.Lit, hauling.Shown?.CommodityId));
        rig.Keys("back", "back");                           // the page names, and no further
        Assert.Equal((true, "trades", -1, false, 0), rig.Box.Menu);
        rig.Keys("forward");                                // from the page names: into the list
        Assert.Equal(0, rig.Box.Menu.Lit);

        // Closed again, "back" opens the list just as "forward" does.
        rig.Keys("back", "menu", "menu", "menu");
        Assert.False(rig.Box.Menu.Open);
        rig.Keys("back");
        Assert.Equal((true, "trades", 1, false, 0), rig.Box.Menu);
    }

    [Fact]
    public void On_the_order_line_the_menu_key_starts_and_ends_changing_the_order_and_on_a_trade_it_accepts_that_trade()
    {
        using var rig = new BoxRig();
        Hauling hauling = rig.Hauling;

        rig.Keys("menu", "forward", "menu");                // the "Order" line, being changed
        Assert.Equal((true, "trades", 0, true, 0), rig.Box.Menu);
        rig.Keys("forward");
        Assert.Equal("hour", hauling.Settings.ListSort);
        rig.Keys("back", "back");                           // round the other end
        Assert.Equal(("age", "age"), (hauling.Settings.ListSort, rig.Haul.Saved().ListSort));
        Assert.Equal(0, rig.Box.Menu.Lit);                  // the lit line did not move meanwhile
        rig.Keys("menu");
        Assert.False(rig.Box.Menu.Editing);

        // Newest prices first: bauxite (1 h), gold (2 h), silver (30 h). Lines are counted in that order.
        rig.Keys("forward");
        Assert.Equal((1, "bauxite"), (rig.Box.Menu.Lit, hauling.Shown?.CommodityId));
        rig.Keys("menu");                                   // accepts it, and the menu closes
        Assert.Equal((false, "bauxite"), (rig.Box.Menu.Open, hauling.Accepted?.CommodityId));

        // With a trade accepted the list opens on it, and the box stays on it whatever line is lit.
        rig.Keys("back");
        Assert.Equal((true, "trades", 1), (rig.Box.Menu.Open, rig.Box.Menu.Page, rig.Box.Menu.Lit));
        rig.Keys("forward");
        Assert.Equal((2, "bauxite"), (rig.Box.Menu.Lit, hauling.Shown?.CommodityId));
        rig.Keys("menu");                                   // another trade selected: switched to it, without dropping or searching
        Assert.Equal((false, "gold"), (rig.Box.Menu.Open, hauling.Accepted?.CommodityId));

        rig.Keys("forward");
        Assert.Equal(2, rig.Box.Menu.Lit);
        rig.Keys("menu");                                   // the accepted trade's own line: the menu just closes
        Assert.Equal((false, "gold"), (rig.Box.Menu.Open, hauling.Accepted?.CommodityId));
        rig.After(1);
        Assert.Single(rig.Haul.Search.Asked);               // neither the order nor accepting from the list searched again
    }

    [Fact]
    public void The_list_shows_five_trades_round_the_lit_one_and_the_lit_line_survives_a_shorter_list()
    {
        TradeRow[] seven = [.. Enumerable.Range(1, 7).Select(n => BoxRig.Worded(HaulRig.Trade($"metal{n}", Alpha, new Place($"Far {n}", [10, 10 * n, 0], $"Port {n}", 500 + n))))];
        using var rig = new BoxRig(answer: request => HaulRig.Found(request, seven));

        rig.Keys("forward");
        for (int line = 2; line <= 7; line++)
        {
            rig.Keys("forward");
            Assert.Equal((line, Math.Max(0, line - 5)), (rig.Box.Menu.Lit, rig.Box.Menu.Top));   // the sixth line pushes the first out of sight
        }
        rig.Keys("forward");
        Assert.Equal((7, 2), (rig.Box.Menu.Lit, rig.Box.Menu.Top));
        rig.Keys("back", "back", "back", "back", "back");
        Assert.Equal((2, 1), (rig.Box.Menu.Lit, rig.Box.Menu.Top));     // going up, the list moves only when the lit line would leave it

        // A new answer with fewer trades while line 7 was lit: the last line there is.
        rig.Keys("forward", "forward", "forward", "forward", "forward");
        rig.Haul.Search.Answer = BoxRig.Trades;
        rig.Hauling.SearchAgain();
        rig.Keys("forward");
        Assert.Equal((true, "trades", 3, false, 0), rig.Box.Menu);
    }

    [Fact]
    public void The_next_key_takes_the_lit_line_of_the_open_list_with_it()
    {
        using var rig = new BoxRig();
        rig.Keys("forward");                                // the list, lit on gold
        rig.After(10);
        rig.Keys("next");
        Assert.Equal((2, "bauxite"), (rig.Box.Menu.Lit, rig.Hauling.Shown?.CommodityId));
        rig.After(10);                                      // and counts as using the menu: 20 s after it opened it is still there
        Assert.True(rig.Box.Menu.Open);
        rig.Keys("next", "next");                           // round to the first trade again
        Assert.Equal((1, "gold"), (rig.Box.Menu.Lit, rig.Hauling.Shown?.CommodityId));

        rig.Keys("back");                                   // on the "Order" line the next key leaves the menu alone
        rig.Keys("next");
        Assert.Equal((0, "bauxite"), (rig.Box.Menu.Lit, rig.Hauling.Shown?.CommodityId));
    }

    // ---- the menu: settings and presets ----

    [Fact]
    public void On_the_settings_page_back_and_forward_move_or_change_the_value_being_changed()
    {
        using var rig = new BoxRig();
        Hauling hauling = rig.Hauling;

        rig.Keys("menu", "menu", "forward");
        Assert.Equal((true, "settings", 0, false, 0), rig.Box.Menu);
        rig.Keys("menu", "forward");                        // "Trip", being changed
        Assert.Equal((true, false), (rig.Box.Menu.Editing, hauling.Settings.Returns));
        rig.Keys("forward");                                // two choices: round again
        Assert.True(hauling.Settings.Returns);
        rig.Keys("back", "menu");
        Assert.Equal((false, false, 0), (rig.Box.Menu.Editing, hauling.Settings.Returns, rig.Box.Menu.Lit));

        rig.Keys("forward", "forward", "menu", "forward", "forward");   // "Max. route distance": 30, then 40, then 60
        Assert.Equal((2, 60), (rig.Box.Menu.Lit, hauling.Settings.MaxLy));
        rig.Keys("back", "back", "back", "back", "back", "back", "back", "back");
        Assert.Equal((2, 10), (rig.Box.Menu.Lit, hauling.Settings.MaxLy));  // a list of numbers stops at its end
        rig.Keys("menu");

        rig.Keys("forward", "forward", "forward", "menu", "forward");   // "Trades start": anywhere
        Assert.Equal((5, "anywhere"), (rig.Box.Menu.Lit, hauling.Settings.StartMode));
        rig.Keys("menu", "back", "back", "menu");           // "Buy within" means nothing then, and cannot be changed
        Assert.Equal((3, false), (rig.Box.Menu.Lit, rig.Box.Menu.Editing));
        rig.Keys("forward", "forward", "forward", "forward", "forward", "forward");
        Assert.Equal(7, rig.Box.Menu.Lit);                  // eight lines, and no further down
        rig.Keys("back", "back", "back", "back", "back", "back", "back", "back", "back");
        Assert.Equal((true, "settings", -1, false, 0), rig.Box.Menu);

        Assert.Equal((false, 10, "anywhere"), (rig.Haul.Saved().Returns, rig.Haul.Saved().MaxLy, rig.Haul.Saved().StartMode));    // saved as they were changed
        Assert.Single(rig.Haul.Search.Asked);
        rig.After(0.7);                                     // all that stepping: one search, a moment after the last change
        Assert.Equal(2, rig.Haul.Search.Asked.Count);
        Assert.Equal((false, 10, "anywhere"), (rig.Haul.Search.Asked[1].Returns, rig.Haul.Search.Asked[1].MaxLy, rig.Haul.Search.Asked[1].Mode));
    }

    [Fact]
    public void On_the_presets_page_the_menu_key_uses_the_lit_preset_and_the_menu_stays_open()
    {
        using var rig = new BoxRig();
        Hauling hauling = rig.Hauling;

        rig.Keys("menu", "menu", "menu", "forward");
        Assert.Equal((true, "presets", 0, false, 0), rig.Box.Menu);
        rig.Keys("menu");                                   // "Quick one-way hops"
        Assert.Equal((false, "hour", 40), (hauling.Settings.Returns, hauling.Settings.RankBy, hauling.Settings.MaxLy));
        Assert.Equal(new[] { true, false }, hauling.PresetLines.Select(preset => preset.Active));
        Assert.Equal((true, "presets", 0, false, 0), rig.Box.Menu);

        rig.Keys("forward", "forward");                     // two presets: the second is the last line
        Assert.Equal(1, rig.Box.Menu.Lit);
        rig.Keys("menu");                                   // "Long round trips"
        Assert.Equal((true, "trip", 120), (hauling.Settings.Returns, hauling.Settings.RankBy, hauling.Settings.MaxLy));
        rig.Keys("back", "back", "back");
        Assert.Equal(-1, rig.Box.Menu.Lit);
        rig.Keys("menu");
        Assert.Equal((false, "trades"), (rig.Box.Menu.Open, rig.Box.Menu.Page));
    }

    [Fact]
    public void Without_presets_there_is_nothing_to_move_down_to()
    {
        using var rig = new BoxRig(market: """{ "presets": [] }""");

        rig.Keys("menu", "menu", "menu", "forward", "forward");
        Assert.Equal((true, "presets", -1, false, 0), rig.Box.Menu);
        rig.Keys("menu");
        Assert.False(rig.Box.Menu.Open);
    }

    // ---- the menu: closing by itself ----

    [Fact]
    public void Left_alone_the_menu_closes_fifteen_seconds_after_the_last_key()
    {
        using var rig = new BoxRig();

        rig.Keys("menu");
        rig.After(14.9);
        Assert.True(rig.Box.Menu.Open);
        rig.After(0.1);
        Assert.False(rig.Box.Menu.Open);

        rig.Keys("menu");
        rig.After(10);
        rig.Keys("forward");                                // every menu key starts the fifteen seconds again
        rig.After(10);
        rig.Keys("menu");                                   // the "Order" line, being changed: that is forgotten when it closes
        rig.After(14.9);
        Assert.Equal((true, "trades", 0, true, 0), rig.Box.Menu);
        rig.After(0.1);
        Assert.Equal((false, "trades", -1, false, 0), rig.Box.Menu);
    }

    [Fact]
    public void In_a_box_that_takes_clicks_the_menu_stays_for_forty_five_seconds_and_clicks_work_it()
    {
        using var rig = new BoxRig { Clickable = true };
        Hauling hauling = rig.Hauling;

        rig.Keys("menu");
        rig.After(44.9);
        Assert.True(rig.Box.Menu.Open);
        rig.After(0.1);
        Assert.False(rig.Box.Menu.Open);

        rig.Keys("menu");
        rig.After(40);
        rig.Box.Click("tab:settings");                      // a click is using the menu, as a key is
        rig.After(40);
        Assert.Equal((true, "settings", -1, false, 0), rig.Box.Menu);

        rig.Box.Click("row:4");                             // a setting clicked is a setting being changed
        Assert.Equal((4, true), (rig.Box.Menu.Lit, rig.Box.Menu.Editing));
        rig.Box.Click("inc:2");                             // the arrows beside any setting step it
        rig.Box.Click("inc:2");
        rig.Box.Click("dec:2");
        Assert.Equal((2, true, 40), (rig.Box.Menu.Lit, rig.Box.Menu.Editing, hauling.Settings.MaxLy));
        rig.Keys("forward");                                // still being changed, so the keys go on stepping it
        Assert.Equal(60, hauling.Settings.MaxLy);
        hauling.StepSetting("startMode", 1);
        rig.Box.Click("row:3");                             // "Buy within" while trades start anywhere: lit, not changed
        Assert.Equal((3, false), (rig.Box.Menu.Lit, rig.Box.Menu.Editing));

        rig.Box.Click("tab:presets");
        rig.Box.Click("row:1");                             // a preset clicked is a preset used
        Assert.Equal((1, "trip", 120), (rig.Box.Menu.Lit, hauling.Settings.RankBy, hauling.Settings.MaxLy));

        rig.Box.Click("tab:trades");
        rig.Box.Click("row:3");                             // a trade clicked is lit, and the box follows
        Assert.Equal((3, "silver"), (rig.Box.Menu.Lit, hauling.Shown?.CommodityId));
        rig.Box.Click("sort:age");                          // a column title: that order, the lit line still on the same trade
        Assert.Equal(("age", 3, "silver"), (hauling.Settings.ListSort, rig.Box.Menu.Lit, hauling.Shown?.CommodityId));
        rig.Box.Click("sort:ly");                           // shortest trip first: gold 20 ly, silver 36.1, bauxite 50
        Assert.Equal(("ly", 2), (hauling.Settings.ListSort, rig.Box.Menu.Lit));
        rig.Box.Click("row:0");                             // the "Order" line clicked is the order being changed
        Assert.Equal((0, true), (rig.Box.Menu.Lit, rig.Box.Menu.Editing));
        rig.Box.Click("pick:3");                            // "accept" beside the third line: accepted, and the menu closes
        Assert.Equal((false, "bauxite"), (rig.Box.Menu.Open, hauling.Accepted?.CommodityId));

        rig.Keys("menu");
        rig.Box.Click("somewhere else");                    // no such piece: nothing happens
        Assert.True(rig.Box.Menu.Open);
        rig.Box.Click("close");
        Assert.False(rig.Box.Menu.Open);
        rig.Box.Click("tab:settings");                      // a click that arrives after the menu closed is dropped
        Assert.Equal((false, "trades"), (rig.Box.Menu.Open, rig.Box.Menu.Page));

        // Which of the two it is, is settled when the menu opens: one opened while the box takes no clicks goes after fifteen seconds.
        rig.Clickable = false;
        rig.Keys("menu");
        rig.Clickable = true;
        rig.After(15);
        Assert.False(rig.Box.Menu.Open);
    }

    [Fact]
    public void The_menu_button_opens_the_trade_list_on_the_trade_on_screen_and_closes_it_again_and_the_others_do_what_they_say()
    {
        using var rig = new BoxRig { Clickable = true };
        Hauling hauling = rig.Hauling;

        rig.Box.Click("do:next");
        Assert.Equal("bauxite", hauling.Shown?.CommodityId);
        rig.Box.Click("do:menu");                           // the list, lit on the trade on screen
        Assert.Equal((true, "trades", 2, false, 0), rig.Box.Menu);
        rig.Box.Click("do:next");                           // the lit line follows, as it did the key
        Assert.Equal((3, "silver"), (rig.Box.Menu.Lit, hauling.Shown?.CommodityId));
        rig.Box.Click("do:menu");
        Assert.False(rig.Box.Menu.Open);

        rig.Box.Click("do:accept");
        Assert.Equal("silver", hauling.Accepted?.CommodityId);
        rig.Box.Click("do:copy");
        Assert.Equal("Delta", rig.Clipboard);
        rig.Box.Click("do:cargo");                          // nothing in the hold: a lookup with only a message
        Assert.Equal("none", hauling.Look?.Status);
        rig.After(4);
        rig.Box.Click("do:menu");                           // blocked by the lookup, as the menu keys are
        Assert.Equal((false, new HaulNote("\"Back to trades\" goes back to your trades first", Good: false)), (rig.Box.Menu.Open, hauling.Note));
        rig.Box.Click("do:something else");                 // no such action: nothing happens
        Assert.NotNull(hauling.Look);
    }

    [Fact]
    public void Hiding_the_haul_box_with_its_key_closes_the_menu_and_nothing_hauling_does_shows_the_box_again()
    {
        using var rig = new BoxRig(overlay: new OverlaySettings { HaulOn = true });

        rig.Keys("forward", "forward");                     // the list, lit on the second trade
        rig.Keys("haul");                                   // hidden: the menu goes with it
        Assert.Equal((false, false), (rig.Overlay.Settings.HaulOn, rig.Box.Menu.Open));

        rig.Keys("next");                                   // hauling goes on behind the hidden box, and leaves it hidden
        Assert.Equal((false, false, "silver"), (rig.Overlay.Settings.HaulOn, rig.Box.Menu.Open, rig.Hauling.Shown?.CommodityId));
        rig.Keys("menu");                                   // the menu then opens afresh; it does not accept the trade that was lit unseen
        Assert.Equal((false, true, "trades", -1), (rig.Overlay.Settings.HaulOn, rig.Box.Menu.Open, rig.Box.Menu.Page, rig.Box.Menu.Lit));
        Assert.Null(rig.Hauling.Accepted);

        rig.Keys("haul");                                   // shown again: showing the box closes nothing
        Assert.Equal((true, true), (rig.Overlay.Settings.HaulOn, rig.Box.Menu.Open));
        rig.Keys("market", "nav", "loadouts");              // the other boxes' keys are nothing of hauling's
        Assert.Equal((true, true), (rig.Overlay.Settings.HaulOn, rig.Box.Menu.Open));
    }

    // ---- a lookup blocks the menu and the accept key ----

    [Fact]
    public void While_a_lookup_is_on_screen_the_menu_keys_and_the_accept_key_only_say_how_to_get_back()
    {
        using var rig = new BoxRig();
        Hauling hauling = rig.Hauling;
        rig.Haul.GameFile("Cargo.json", HaulRig.Hold(("gold", 400)));
        rig.Keys("cargo");
        Assert.Equal(("gold", 2), (hauling.Look?.Id, hauling.Look?.Rows.Count));

        foreach (string key in new[] { "menu", "back", "forward", "accept" })
        {
            rig.After(4);                                   // the note of the key before has gone
            Assert.Null(hauling.Note);
            rig.Keys(key);
            Assert.Equal(new HaulNote("\"Back to trades\" goes back to your trades first", Good: false), hauling.Note);   // the button, by the words on it
            Assert.False(rig.Box.Menu.Open);
            Assert.Null(hauling.Accepted);
        }
        rig.Keys("next");                                   // next and copy work on the lookup's places
        Assert.Equal(1, hauling.Look?.Index);
        rig.Keys("copy");
        Assert.Equal("Gamma", rig.Clipboard);

        rig.Keys("cargo");                                  // one kind of cargo: the key goes back to the trades
        Assert.Null(hauling.Look);
        rig.Keys("accept", "next");
        Assert.Equal(new HaulNote("This trade is accepted: \"Drop this trade\" drops it", Good: false), hauling.Note);
    }

    [Fact]
    public void A_menu_that_was_open_when_a_lookup_began_stays_as_it_is_until_it_closes_by_itself()
    {
        using var rig = new BoxRig();
        rig.Haul.GameFile("Cargo.json", HaulRig.Hold(("gold", 400)));
        rig.Keys("menu", "forward");
        rig.After(5);
        rig.Keys("cargo");
        Assert.NotNull(rig.Hauling.Look);

        rig.Keys("forward", "menu", "back");                // blocked: nothing moves, and it does not count as using the menu
        Assert.Equal((true, "trades", 0, false, 0), rig.Box.Menu);
        rig.After(9.9);
        Assert.True(rig.Box.Menu.Open);
        rig.After(0.1);                                     // fifteen seconds after the last key that reached it
        Assert.False(rig.Box.Menu.Open);
    }

    [Fact]
    public void While_a_lookup_is_on_screen_a_click_on_the_menu_only_says_how_to_get_back_and_close_still_closes()
    {
        using var rig = new BoxRig { Clickable = true };
        Hauling hauling = rig.Hauling;
        rig.Haul.GameFile("Cargo.json", HaulRig.Hold(("gold", 400)));
        rig.Keys("menu", "forward", "forward");             // the list, lit on the first trade
        rig.Keys("cargo");
        Assert.NotNull(hauling.Look);

        // "accept" beside a trade, another trade, a page name, a column title, a setting's arrow: each gets the note and does nothing else.
        foreach (string click in new[] { "pick:2", "row:3", "tab:settings", "sort:age", "inc:2" })
        {
            rig.After(4);                                   // the note of the click before has gone
            Assert.Null(hauling.Note);
            rig.Box.Click(click);
            Assert.Equal(new HaulNote("\"Back to trades\" goes back to your trades first", Good: false), hauling.Note);
            Assert.Equal((true, "trades", 1, false, 0), rig.Box.Menu);
        }
        Assert.Null(hauling.Accepted);
        Assert.Equal(("rank", 30), (hauling.Settings.ListSort, hauling.Settings.MaxLy));

        rig.After(4);
        rig.Box.Click("close");                             // the way out always works, and says nothing
        Assert.False(rig.Box.Menu.Open);
        Assert.Null(hauling.Note);
        Assert.NotNull(hauling.Look);                       // the lookup itself is still on screen
    }

    // ---- with one trade, or none ----

    [Fact]
    public void With_no_trade_there_is_nothing_to_step_to_or_copy_and_the_list_opens_on_its_order_line()
    {
        using var rig = new BoxRig(answer: BoxRig.Nothing);

        rig.Keys("next", "copy");

        Assert.Null(rig.Hauling.Shown);
        Assert.Equal(0, rig.ClipboardTries);                // and there is nothing to copy

        rig.Keys("forward");                                // the list opens all the same, on its "Order" line: there is no trade to light
        Assert.Equal((true, "trades", 0, false, 0), rig.Box.Menu);
        rig.Keys("forward", "menu");                        // nothing below it, and the order can still be changed
        Assert.Equal((true, "trades", 0, true, 0), rig.Box.Menu);
    }

    [Fact]
    public void With_a_single_trade_the_next_key_comes_round_to_that_trade()
    {
        using var rig = new BoxRig(answer: request => HaulRig.Found(request, BoxRig.Worded(HaulRig.Gold())));

        rig.Keys("next");                                   // as in the Haul Overlay

        Assert.Equal(("gold", 0), (rig.Hauling.Shown?.CommodityId, rig.Hauling.Index));
    }

    // ---- the clipboard ----

    [Fact]
    public async Task A_busy_clipboard_is_tried_six_times_forty_milliseconds_apart()
    {
        using var rig = new BoxRig();

        rig.ClipboardBusy = 5;                              // free at the sixth try
        await rig.Box.Copy();
        Assert.Equal(("Beta", 6), (rig.Clipboard, rig.ClipboardTries));
        Assert.Equal(new double[] { 40, 40, 40, 40, 40 }, rig.Waits);
        Assert.Equal(new HaulNote("✔ Beta copied", Good: true), rig.Hauling.Note);
    }

    [Fact]
    public async Task A_clipboard_that_stays_busy_is_given_up_and_nothing_claims_to_be_copied()
    {
        using var rig = new BoxRig();

        rig.ClipboardBusy = 6;
        await rig.Box.Copy();
        Assert.Null(rig.Clipboard);
        Assert.Equal((6, 5), (rig.ClipboardTries, rig.Waits.Count));    // no wait after the last try
        Assert.Null(rig.Hauling.Note);

        await rig.Box.Copy();                               // free again: the first try does it, with no wait
        Assert.Equal(("Beta", 7, 5), (rig.Clipboard, rig.ClipboardTries, rig.Waits.Count));
        Assert.Equal("✔ Beta copied", rig.Hauling.Note?.Text);
    }

    // ---- the first run ----

    [Fact]
    public void The_first_run_copies_the_haul_overlays_colour_and_background_and_shows_no_box()
    {
        string old = OldFile("version-1.0.json");               // left showing, at the top left, bigger, with keys of its own and a fainter background
        byte[] bytes = File.ReadAllBytes(old);

        using var rig = new BoxRig(oldSettings: old);
        OverlaySettings copied = rig.Overlay.Settings;
        // The background and "only while Elite is in front" are the old tool's; every key, place and size is Studio's own,
        // and every box starts hidden. Where it copied from is noted for the Market page, until the player has read of it.
        Assert.Equal(new OverlaySettings { Opacity = 0.5, OnlyWhenElite = false, CopiedFrom = old }, copied);
        Assert.DoesNotContain(true, OverlaySettings.Boxes.Select(box => copied.Box(box).On));
        Assert.Equal(copied, rig.Saved());                  // Studio's own from now on
        Assert.Equal(bytes, File.ReadAllBytes(old));        // the old tool's file is only read
    }

    [Fact]
    public void What_the_player_changed_in_studio_is_never_overwritten_from_the_old_file()
    {
        // "overlay" has been saved before (here with another key, a box shown and another background): the old file is not looked at.
        var own = new OverlaySettings { HotNav = "F10", HaulOn = true, HaulScale = 1.5, Opacity = 0.7 };
        using var rig = new BoxRig(overlay: own, oldSettings: OldFile("owner.json"));

        Assert.Equal(own, rig.Overlay.Settings);
        Assert.Equal(own, rig.Saved());
    }

    [Theory]
    [InlineData("owner.json", true)]            // the owner's own file: its colour and background are Studio's standard ones
    [InlineData("cut-short.json", false)]       // a file that cannot be read: Studio's own defaults, and nothing was copied
    [InlineData("no such file.json", false)]    // the old tool was never used here
    public void The_first_run_takes_what_it_can_from_any_old_file_and_saves_it_once(string file, bool copied)
    {
        string old = OldFile(file);
        using var rig = new BoxRig(oldSettings: old);

        OverlaySettings first = rig.Overlay.Settings;
        Assert.Equal(new OverlaySettings { CopiedFrom = copied ? old : null }, first);
        Assert.True(new Settings(rig.SettingsFile).Has("overlay"));     // saved, so the next start does not copy again
        Assert.Equal(first, rig.Saved());
    }

    // ---- Studio Nav's next stop ----

    [Fact]
    public async Task The_nav_box_names_the_next_stop_of_the_route_whatever_hauling_shows_and_copy_copies_only_haulings_own_stop()
    {
        using var rig = new BoxRig(answer: BoxRig.Nothing, route: BoxRig.RouteVia(Alpha, Beta, Gamma));
        Assert.Null(rig.Hauling.Shown);

        Assert.Equal(new NavProgress(0, 0, "Beta", 20, 2, false, 2, 4, 45), rig.Box.RouteShown);
        await rig.Box.Copy();                               // hauling has no stop of its own: the route's is the nav box's to copy
        Assert.Equal(0, rig.ClipboardTries);

        // A lookup in the haul box, even one with nothing but a message, takes nothing from the nav box.
        rig.Keys("cargo");
        Assert.Equal("none", rig.Hauling.Look?.Status);
        Assert.Equal("Beta", rig.Box.RouteShown?.Next);
        rig.Keys("cargo");

        // Trades are found: each box has its own, and copy copies the trade's next stop.
        rig.Haul.Search.Answer = request => HaulRig.Found(request, BoxRig.Worded(HaulRig.Trade("gold", Alpha, Delta)));
        rig.Hauling.SearchAgain();
        Assert.Equal(("gold", "Beta"), (rig.Hauling.Shown?.CommodityId, rig.Box.RouteShown?.Next));
        await rig.Box.Copy();
        Assert.Equal("Delta", rig.Clipboard);
        Assert.Equal("✔ Delta copied", rig.Hauling.Note?.Text);

        // Further along the route: its last stop. At the end of the route there is no next stop.
        rig.Haul.Search.Answer = BoxRig.Nothing;
        rig.Haul.Play(HaulRig.Undock(Alpha), HaulRig.Jump(Beta));
        Assert.Equal(("Gamma", 1), (rig.Box.RouteShown?.Next, rig.Box.RouteShown?.HopsLeft));
        rig.Haul.Play(HaulRig.Jump(Gamma));
        Assert.Null(rig.Box.RouteShown);
    }

    [Fact]
    public void Without_a_route_there_is_no_nav_stop_and_clearing_the_route_takes_it_away()
    {
        using var none = new BoxRig(answer: BoxRig.Nothing);
        Assert.Null(none.Box.RouteShown);

        using var rig = new BoxRig(answer: BoxRig.Nothing, route: BoxRig.RouteVia(Alpha, Beta));
        Assert.Equal("Beta", rig.Box.RouteShown?.Next);
        rig.Nav.Clear();
        Assert.Null(rig.Box.RouteShown);
    }
}
