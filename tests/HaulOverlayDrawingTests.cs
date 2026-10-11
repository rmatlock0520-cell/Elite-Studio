using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EliteStudio.Market;
using EliteStudio.Nav;
using EliteStudio.Overlay;
using static EliteStudio.Tests.OverlayTests;   // OnWindowThread and TextOf
using static EliteStudio.Tests.HaulRig;        // its places (Alpha, Beta, Gamma, Delta) and Place itself

namespace EliteStudio.Tests;

/// <summary>
/// What hauling draws in the overlay's haul box and nav box, read back as text: every line of every
/// state, in the Haul Overlay's own words. The content is built without any window (see <see cref="BoxRig"/>).
/// Also the pictures --render-shots makes of it, and a click finding its way to a box's pieces.
/// </summary>
public class HaulOverlayDrawingTests
{
    /// <summary>
    /// The content as lines of text, top to bottom. A line with text at both ends reads left to right
    /// with "  ||  " between its parts (an end with nothing in it left out); a table gives one line
    /// per row, its cells joined with " | " (empty cells left out); the page names, and the buttons
    /// of a row, stand side by side.
    /// </summary>
    internal static List<string> Read(UIElement? piece) => piece switch
    {
        TextBlock text => [TextOf(text)],
        DockPanel line => [string.Join("  ||  ", line.Children.Cast<UIElement>().Reverse().SelectMany(Read).Where(part => part.Length > 0))],
        StackPanel { Orientation: Orientation.Horizontal } side => [string.Join(" ", side.Children.Cast<UIElement>().SelectMany(Read))],
        StackPanel stack => [.. stack.Children.Cast<UIElement>().SelectMany(Read)],
        Grid table =>
        [
            .. table.Children.OfType<TextBlock>().GroupBy(Grid.GetRow).OrderBy(row => row.Key)
                .Select(row => string.Join(" | ", row.OrderBy(Grid.GetColumn).Select(TextOf).Where(text => text.Length > 0))),
        ],
        Border { Child: not null } box => Read(box.Child),
        _ => [],
    };

    static IEnumerable<FrameworkElement> Pieces(DependencyObject from) =>
        LogicalTreeHelper.GetChildren(from).OfType<FrameworkElement>().SelectMany(piece => Pieces(piece).Prepend(piece));

    internal static string[] Tags(UIElement content) => [.. Pieces(content).Select(piece => piece.Tag).OfType<string>().Distinct()];

    static Color Ink(TextBlock line, int run = 0) => ((SolidColorBrush)line.Inlines.OfType<Run>().ElementAt(run).Foreground).Color;

    static Color Colour(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static TextBlock Line(UIElement content, string startsWith) => Pieces(content).OfType<TextBlock>().First(line => TextOf(line).StartsWith(startsWith, StringComparison.Ordinal));

    // ---- a trade ----

    [Fact]
    public void A_round_trip_is_drawn_leg_by_leg_from_the_first_look_to_the_way_back() => OnWindowThread(() =>
    {
        // In Delta, in space. The best trade buys in Alpha, 30 ly away: two jumps with the hold empty.
        using var rig = new BoxRig(HaulRig.In(Delta, docked: false), overlay: new OverlaySettings { Accent = "#FF8800" });
        rig.After(0.5);

        Assert.Equal(new[]
        {
            "GOLD  ||  trade 1 of 3   ·   prices updated 2h ago  ||  THEN BRING BACK   SILVER",
            "► BUY | Alpha Port  (L pad) · Alpha | 30 ly from you   ·   ~2 jumps empty | 9,000 cr   5,000 t for sale",
            "SELL | Beta Hub  (L pad) · Beta | 20 ly trip   ·   ~2 jumps full | 12,000 cr   wants 5,000 t",
            "BACK | Silver  to Alpha Port | 20 ly back   ·   ~2 jumps full | +1,000 cr/t",
            "PROFIT   +3,000 cr/t   ·   +1.2M this load   ·   round trip +1.6M in ~20 min   ·   30.0M cr/hour",
            "NEXT STOP   Alpha   buy at Alpha Port   ·   30 ly from you   ·   ~2 jumps empty",
        }, Read(rig.Box.Content()));

        // The leg being flown is gold, the other labels and both big names are in the accent colour of the settings.
        UIElement first = rig.Box.Content();
        Assert.Equal(Colour("#FF8800"), Ink(Line(first, "GOLD")));
        Assert.Equal(Colour("#FF8800"), Ink(Line(first, "THEN BRING BACK"), run: 1));
        Assert.Equal(Colour(Draw.Gold), ((SolidColorBrush)Line(first, "► BUY").Foreground).Color);
        Assert.Equal(Colour("#FF8800"), ((SolidColorBrush)Line(first, "BACK").Foreground).Color);
        Assert.Equal(Colour(Draw.Grey), ((SolidColorBrush)Line(first, "SELL").Foreground).Color);
        Assert.Equal((17.0, 12.5, 15.0, 14.5), (Line(first, "GOLD").FontSize, Line(first, "trade 1").FontSize, Line(first, "Alpha Port").FontSize, Line(first, "PROFIT").FontSize));

        // Accepted, with a route plotted in the game to where it buys: the game's own count of jumps, on the leg being flown only.
        rig.Keys("accept");
        rig.Haul.GameFile("NavRoute.json", HaulRig.Route(Delta, Gamma, Alpha));
        rig.Haul.Play(HaulRig.Target("Gamma", 2));
        List<string> accepted = Read(rig.Box.Content());
        Assert.Equal("GOLD  ||  ✔ ACCEPTED   ·   prices updated 2h ago  ||  THEN BRING BACK   SILVER", accepted[0]);
        Assert.Equal("► BUY | Alpha Port  (L pad) · Alpha | 30 ly from you   ·   2 jumps left | 9,000 cr   5,000 t for sale", accepted[1]);
        Assert.Equal("SELL | Beta Hub  (L pad) · Beta | 20 ly trip   ·   ~2 jumps full | 12,000 cr   wants 5,000 t", accepted[2]);
        Assert.Equal("NEXT STOP   Alpha   buy at Alpha Port   ·   30 ly from you   ·   2 jumps on your plotted route", accepted[5]);

        rig.Haul.Play(HaulRig.Jump(Gamma), HaulRig.Target("Alpha", 1));     // one jump to go
        List<string> nearly = Read(rig.Box.Content());
        Assert.Equal("► BUY | Alpha Port  (L pad) · Alpha | 50 ly from you   ·   1 jump left | 9,000 cr   5,000 t for sale", nearly[1]);
        Assert.EndsWith("50 ly from you   ·   1 jump on your plotted route", nearly[5]);

        // Docked where it buys, and bought: the place to sell is next.
        rig.Haul.Play(HaulRig.Jump(Alpha), HaulRig.RouteCleared, HaulRig.Dock(Alpha), HaulRig.Buy(Alpha, "gold"));
        List<string> loaded = Read(rig.Box.Content());
        Assert.Equal("BUY | Alpha Port  (L pad) · Alpha | ★ you are docked here | 9,000 cr   5,000 t for sale", loaded[1]);
        Assert.StartsWith("► SELL | Beta Hub", loaded[2]);
        Assert.Equal("NEXT STOP   Beta   sell at Beta Hub   ·   20 ly from you   ·   ~2 jumps full", loaded[5]);

        // Sold: the way back. The load to bring turns gold ("buy now") and the first one fades, until that load is on board.
        rig.Haul.Play(HaulRig.Undock(Alpha), HaulRig.Jump(Beta), HaulRig.Dock(Beta), HaulRig.Sell(Beta, "gold"));
        UIElement wayBack = rig.Box.Content();
        Assert.Equal("GOLD  ||  ✔ ACCEPTED   ·   prices updated 2h ago  ||  ► BUY NOW   SILVER", Read(wayBack)[0]);
        Assert.StartsWith("BUY | Alpha Port  (L pad) · Alpha | 20 ly from you   ·   1 jump empty | ", Read(wayBack)[1]);   // a single jump is no estimate, so no "~"
        Assert.StartsWith("► BACK | Silver  to Alpha Port | 20 ly back   ·   ~2 jumps full", Read(wayBack)[3]);
        Assert.Equal("NEXT STOP   Alpha   back to Alpha Port   ·   20 ly from you   ·   ~2 jumps full", Read(wayBack)[5]);
        Assert.Equal((Colour(Draw.Dim), Colour(Draw.Gold), Colour(Draw.Gold)), (Ink(Line(wayBack, "GOLD")), Ink(Line(wayBack, "► BUY NOW")), Ink(Line(wayBack, "► BUY NOW"), run: 1)));

        rig.Haul.Play(HaulRig.Buy(Beta, "silver"));
        UIElement home = rig.Box.Content();
        Assert.Equal("GOLD  ||  ✔ ACCEPTED   ·   prices updated 2h ago  ||  ✔ ON BOARD   SILVER", Read(home)[0]);
        Assert.Equal((Colour(Draw.Dim), Colour(Draw.Green), Colour("#FF8800")), (Ink(Line(home, "GOLD")), Ink(Line(home, "✔ ON BOARD")), Ink(Line(home, "✔ ON BOARD"), run: 1)));
    });

    [Fact]
    public void A_trip_without_a_load_back_a_one_way_trip_and_a_trade_without_totals_are_each_drawn_their_own_way() => OnWindowThread(() =>
    {
        TradeRow oneWay = BoxRig.Worded(HaulRig.Trade("tin", Alpha, Delta, oneWay: true));
        TradeRow noTotals = BoxRig.Worded(HaulRig.Trade("zinc", Alpha, Delta));
        (noTotals.RoundTripProfit, noTotals.ProfitPerTrip, noTotals.BuyPower, noTotals.SellPower, noTotals.BuyType, noTotals.SellType) =
            (0, 0, "Aisling Duval, Stronghold", "Li Yong-Rui, Fortified", "OnFootSettlement", "FleetCarrier");
        using var rig = new BoxRig(answer: request => HaulRig.Found(request, BoxRig.Worded(HaulRig.Bauxite()), oneWay, noTotals));

        // A round trip with nothing worth bringing: the BACK row says so, flown with the hold empty, and no load is named at the top.
        List<string> empty = Read(rig.Box.Content());
        Assert.Equal("BAUXITE  ||  trade 1 of 3   ·   prices updated 1h ago", empty[0]);
        Assert.Equal("BACK | Nothing worth carrying back | 50 ly back   ·   ~4 jumps empty", empty[3]);
        Assert.Equal("PROFIT   +5,000 cr/t   ·   +2.0M this load   ·   round trip +2.0M in ~20 min   ·   20.0M cr/hour", empty[4]);

        // One way: no BACK row at all.
        rig.Keys("next");
        Assert.Equal(new[]
        {
            "TIN  ||  trade 2 of 3   ·   prices updated 2h ago",
            "BUY | Alpha Port  (L pad) · Alpha | ★ you are docked here | 9,000 cr   5,000 t for sale",
            "► SELL | Delta Yard  (L pad) · Delta | 30 ly trip   ·   ~3 jumps full | 12,000 cr   wants 5,000 t",
            "PROFIT   +3,000 cr/t   ·   +1.2M this load   ·   one way in ~20 min   ·   30.0M cr/hour",
            "NEXT STOP   Delta   sell at Delta Yard   ·   30 ly from you   ·   ~3 jumps full",
        }, Read(rig.Box.Content()));

        // The hold's size not known, so no totals; a settlement, a carrier and Powerplay named with the stations.
        rig.Keys("next");
        List<string> bare = Read(rig.Box.Content());
        Assert.Equal("BUY | Alpha Port  (settlement, L pad) · Alpha · Aisling Duval, Stronghold | ★ you are docked here | 9,000 cr   5,000 t for sale", bare[1]);
        Assert.Equal("► SELL | Delta Yard  (carrier, L pad) · Delta · Li Yong-Rui, Fortified | 30 ly trip   ·   ~3 jumps full | 12,000 cr   wants 5,000 t", bare[2]);
        Assert.Equal("PROFIT   +3,000 cr/t   ·   set your cargo capacity to see totals", bare[4]);
    });

    [Fact]
    public void The_right_hand_end_of_the_next_stop_line_shows_a_note_for_four_seconds_and_nothing_otherwise() => OnWindowThread(() =>
    {
        using var rig = new BoxRig();
        string[] Ends() => Read(rig.Box.Content())[5].Split("  ||  ");

        Assert.Equal(new[] { "NEXT STOP   Beta   sell at Beta Hub   ·   20 ly from you   ·   ~2 jumps full" }, Ends());
        rig.Keys("copy");
        Assert.Equal("✔ Beta copied", Ends()[1]);
        Assert.Equal(Colour(Draw.Green), Ink(Line(rig.Box.Content(), "✔ Beta copied")));
        rig.After(4);
        rig.Keys("accept", "next");                         // the note names the button that would do it, by the words on it
        Assert.Equal("This trade is accepted: \"Drop this trade\" drops it", Ends()[1]);
        Assert.Equal(Colour(Draw.Gold), Ink(Line(rig.Box.Content(), "This trade is accepted")));
        rig.After(4);
        Assert.Single(Ends());
    });

    // ---- the row of buttons ----

    [Fact]
    public void A_haul_box_that_takes_clicks_has_a_row_of_buttons_under_what_it_shows_and_one_that_does_not_has_none() => OnWindowThread(() =>
    {
        using var rig = new BoxRig();
        string Buttons() => Read(rig.Box.Content())[6];

        Assert.Empty(Tags(rig.Box.Content()));              // click-through, as over Studio's own window: nothing to click
        Assert.Equal(6, Read(rig.Box.Content()).Count);

        rig.Clickable = true;                               // shown in the game: what the Haul Overlay had keys for
        Assert.Equal(new[] { "do:next", "do:copy", "do:accept", "do:cargo", "do:menu" }, Tags(rig.Box.Content()));
        Assert.Equal("Next trade Copy system ✔ Accept this trade Sell my cargo Trades and settings", Buttons());
        rig.Box.Click("do:accept");
        Assert.Equal("Next trade Copy system Drop this trade Sell my cargo Trades and settings", Buttons());

        rig.Box.Click("do:menu");                           // the menu drops down under the row, and the same button closes it
        Assert.True(rig.Box.Menu.Open);
        Assert.Equal("Next trade Copy system Drop this trade Sell my cargo Close the list", Buttons());
        Assert.Equal("TRADES SETTINGS PRESETS ✕ close", Read(rig.Box.Content())[7]);
        rig.Box.Click("do:menu");
        Assert.False(rig.Box.Menu.Open);
        Assert.Equal(7, Read(rig.Box.Content()).Count);

        // During a lookup the buttons read as what they do then, and the two that do nothing are left out.
        rig.Haul.GameFile("Cargo.json", HaulRig.Hold(("gold", 400)));
        rig.Box.Click("do:cargo");
        Assert.Equal(new[] { "do:next", "do:copy", "do:cargo" }, Tags(rig.Box.Content()));
        Assert.Equal("Next place Copy system Back to trades", Buttons());

        // With nothing of hauling's to show the row stands under the status line and its reason.
        using var idle = new BoxRig(answer: BoxRig.Nothing) { Clickable = true };
        Assert.Equal("Next trade Copy system ✔ Accept this trade Sell my cargo Trades and settings", Read(idle.Box.Content())[2]);
    });

    // ---- a lookup ----

    [Fact]
    public void A_lookup_is_drawn_with_what_is_being_sold_the_chosen_place_and_the_next_choice() => OnWindowThread(() =>
    {
        SearchResult Places(SearchRequest request)
        {
            if (request.Mode != "lookup") return BoxRig.Trades(request);
            TradeRow near = HaulRig.SellAt(request, Beta), far = HaulRig.SellAt(request, Gamma);
            (near.SellType, near.SellPad, near.Demand, near.Units, near.ProfitPerTrip, near.Updated) = ("OnFootSettlement", "M", 300, 300, 600_000, "just now");
            (far.SellType, far.SellPad, far.DistanceLy, far.JumpsOut, far.ProfitPerTrip, far.Updated) = ("FleetCarrier", "L", 50, 4, 800_000, "3h ago");
            return HaulRig.Found(request, near, far);
        }
        // The gold in the hold was bought for 9,000 a ton before Studio started.
        using var rig = new BoxRig([.. HaulRig.In(Alpha), HaulRig.Buy(Alpha, "gold", 9000)], Places);
        rig.Haul.GameFile("Cargo.json", HaulRig.Hold(("gold", 400)));

        rig.Keys("cargo");
        Assert.Equal(new[]
        {
            "SELL YOUR CARGO   ·   GOLD  ||  place 1 of 2   ·   prices updated just now",
            "CARGO | 400 t of Gold   you paid 9,000 cr/t",
            "► SELL | Beta Hub  (settlement, M pad) · Beta | 20 ly from you   ·   ~2 jumps full | 11,000 cr   wants 300 t   (not your whole load)",
            "ALSO | Gamma Dock  (carrier, L pad) · Gamma | 50 ly   ·   ~4 jumps full | 11,000 cr   wants 5,000 t   ·   3h ago",
            "PROFIT   +2,000 cr/t   ·   +600k for 300 t",
            "NEXT STOP   Beta   sell at Beta Hub   ·   20 ly from you   ·   ~2 jumps full",
        }, Read(rig.Box.Content()));

        // The next place; the one after it is the first again. The chosen place's jumps are worked out from where the
        // player is now (five for this ship); the "also" line above showed what the search had counted (four).
        rig.Keys("next");
        List<string> second = Read(rig.Box.Content());
        Assert.Equal("SELL YOUR CARGO   ·   GOLD  ||  place 2 of 2   ·   prices updated 3h ago", second[0]);
        Assert.Equal("► SELL | Gamma Dock  (carrier, L pad) · Gamma | 50 ly from you   ·   ~5 jumps full | 11,000 cr   wants 5,000 t", second[2]);
        Assert.StartsWith("ALSO | Beta Hub  (settlement, M pad) · Beta | ", second[3]);
        Assert.Equal("PROFIT   +2,000 cr/t   ·   +800k for 400 t", second[4]);

        // A commodity picked on the Market page, not for sale where the player is: no price to compare with.
        rig.Hauling.WhereToSell("silver");
        List<string> picked = Read(rig.Box.Content());
        Assert.Equal("WHERE TO SELL   ·   SILVER  ||  place 1 of 2   ·   prices updated just now", picked[0]);
        Assert.Equal("BUY | 400 t of Silver   not for sale where you are", picked[1]);
        Assert.Equal("SELLS FOR   2,000 cr/t   ·   600k for 300 t", picked[4]);
    });

    // ---- nothing but a message, and the nav box ----

    [Fact]
    public void A_lookup_with_no_place_to_show_is_its_title_and_what_it_has_to_say() => OnWindowThread(() =>
    {
        using var rig = new BoxRig();

        rig.Keys("cargo");                                  // nothing in the hold
        Assert.Equal(new[] { "SELL YOUR CARGO", "Your cargo hold is empty, so there is nothing to look up." }, Read(rig.Box.Content()));
        rig.Keys("cargo");

        rig.Haul.GameFile("Cargo.json", HaulRig.Hold(("gold", 400)));
        rig.Haul.Search.Hold = true;                        // the data service takes its time
        rig.Keys("cargo");
        Assert.Equal(new[] { "SELL YOUR CARGO   ·   GOLD", "Looking for places to sell Gold…" }, Read(rig.Box.Content()));
    });

    [Fact]
    public void With_no_trade_the_haul_box_shows_the_status_line_with_the_reason_whether_or_not_a_route_is_followed() => OnWindowThread(() =>
    {
        // Nothing known about the game yet: the status line says so by itself.
        using var waiting = new BoxRig(history: [], answer: BoxRig.Nothing);
        Assert.Equal(new[] { "ELITE STUDIO   Waiting for Elite…" }, Read(waiting.Box.Content()));

        // No trades: where the player is, and why there is no trade.
        using var idle = new BoxRig(answer: BoxRig.Nothing);
        string[] noTrade =
        [
            "ELITE STUDIO   Alpha   docked at Alpha Port",
            "No market data for stations in Alpha that fit your filters. Try older prices or another pad size.",
        ];
        Assert.Equal(noTrade, Read(idle.Box.Content()));
        idle.Keys("menu", "menu", "menu");                  // the menu opens under it all the same
        Assert.Equal("TRADES SETTINGS PRESETS", Read(idle.Box.Content())[2]);

        // A Studio Nav route is the nav box's to show: the haul box says the same with one.
        using var rig = new BoxRig(answer: BoxRig.Nothing, route: BoxRig.RouteVia(Alpha, Beta, Gamma));
        Assert.Equal(noTrade, Read(rig.Box.Content()));
    });

    [Fact]
    public void The_nav_box_shows_the_routes_next_stop_while_the_haul_box_shows_a_trade_and_says_so_when_there_is_no_route() => OnWindowThread(() =>
    {
        using var none = new BoxRig();
        Assert.Equal(new[] { "No route is being followed. Plot one in Studio Nav." }, Read(none.Box.NavContent()));

        // A trade in the haul box, a Studio Nav route in the nav box: its next stop, in the style of NEXT STOP.
        using var rig = new BoxRig(route: BoxRig.RouteVia(Alpha, Beta, Gamma));
        Assert.StartsWith("GOLD  ||  trade 1 of 3", Read(rig.Box.Content())[0]);
        Assert.Equal(new[] { "NEXT STOP   Beta   ·   20 ly from you   ·   ~2 jumps   ·   2 stops left on your route" }, Read(rig.Box.NavContent()));
        Assert.Equal((Colour(Draw.Gold), Colour(Draw.White)), (Ink(Line(rig.Box.NavContent(), "NEXT STOP")), Ink(Line(rig.Box.NavContent(), "NEXT STOP"), run: 1)));
        Assert.Empty(Tags(rig.Box.NavContent()));           // its "Copy system" button is there only in a box that takes clicks
        rig.Keys("copy");                                   // hauling's own copy says so in the haul box and leaves the nav box as it is
        Assert.EndsWith("  ||  ✔ Beta copied", Read(rig.Box.Content())[5]);
        Assert.Single(Read(rig.Box.NavContent()));

        // Plotted in the game: its own count. Then on to the last stop, and at the end of the route there is none to name.
        rig.Haul.GameFile("NavRoute.json", HaulRig.Route(Alpha, Delta, Gamma, Beta));
        rig.Haul.Play(HaulRig.Target("Delta", 3));
        Assert.Equal("NEXT STOP   Beta   ·   20 ly from you   ·   3 jumps on your plotted route   ·   2 stops left on your route", Read(rig.Box.NavContent())[0]);
        rig.Haul.Play(HaulRig.Undock(Alpha), HaulRig.Jump(Beta), HaulRig.RouteCleared);
        Assert.Equal("NEXT STOP   Gamma   ·   53.9 ly from you   ·   ~2 jumps   ·   the last stop of your route", Read(rig.Box.NavContent())[0]);
        rig.Haul.Play(HaulRig.Jump(Gamma));
        Assert.Equal(new[] { "No route is being followed. Plot one in Studio Nav." }, Read(rig.Box.NavContent()));

        // A trip another studio handed to Studio Nav: every stop says what to do there. What to do where the ship is comes
        // first, the next stop's note goes under its line, and at the last stop the note is all there is to show.
        rig.Nav.Follow("Shopping for Explorer",
        [
            new TripStop("Gamma", 0, Gamma.At[0], Gamma.At[1], Gamma.At[2], "Dock at Gamma Dock and buy 5A Frame Shift Drive."),
            new TripStop("Delta", 0, Delta.At[0], Delta.At[1], Delta.At[2], "Dock at Delta Yard and buy 2 × A Shield Booster."),
        ]);
        Assert.Equal(new[]
        {
            "HERE   Dock at Gamma Dock and buy 5A Frame Shift Drive.",
            "NEXT STOP   Delta   ·   58.3 ly from you   ·   ~5 jumps   ·   the last stop of your route",
            "Dock at Delta Yard and buy 2 × A Shield Booster.",
        }, Read(rig.Box.NavContent()));
        rig.Haul.Play(HaulRig.Jump(Delta));
        Assert.Equal(new[] { "HERE   Dock at Delta Yard and buy 2 × A Shield Booster." }, Read(rig.Box.NavContent()));
    });

    // ---- the menu ----

    [Fact]
    public void The_menus_trade_list_is_drawn_with_its_order_its_trades_and_the_lit_line() => OnWindowThread(() =>
    {
        using var rig = new BoxRig();

        rig.Keys("menu");                                   // the page names are lit
        UIElement names = rig.Box.Content();
        Assert.Equal(new[]
        {
            "NEXT STOP   Beta   sell at Beta Hub   ·   20 ly from you   ·   ~2 jumps full",
            "TRADES SETTINGS PRESETS",
            "Order   Best first",
            "# ▲ | Commodity | Buy at | Sell at | Trip ly | Jumps | Profit /t | Trip profit | Per hour | Updated",
            "1 | Gold | Alpha Port | Beta Hub · Beta | 20 | 4 | +3,000 | +1.6M | 30.0M | 2h ago",
            "2 | Bauxite | Alpha Port | Gamma Dock · Gamma | 50 | 8 | +5,000 | +2.0M | 20.0M | 1h ago",
            "3 | Silver | Delta Yard | Beta Hub · Beta | 36.1 | 8 | +1,000 | +400k | 10.0M | 30h ago",
            "Trades 1 to 3 of 3. Jumps count the whole trip, there and back.",
        }, Read(names).Skip(5));
        // The open page's name is dark on gold while the highlight is on it; the others are dim on next to nothing.
        TextBlock trades = Line(names, "TRADES"), settings = Line(names, "SETTINGS");
        Assert.Equal((Colour("#FF080C14"), Colour(Draw.Gold)), (((SolidColorBrush)trades.Foreground).Color, ((SolidColorBrush)trades.Background).Color));
        Assert.Equal((Colour(Draw.Dim), Colour("#01000000")), (((SolidColorBrush)settings.Foreground).Color, ((SolidColorBrush)settings.Background).Color));

        rig.Keys("forward", "menu", "forward");             // the order, being changed: profit per hour, biggest first
        List<string> order = Read(rig.Box.Content());
        Assert.Equal("► | Order   ◄  Profit per hour  ►", order[7]);
        Assert.Equal("# | Commodity | Buy at | Sell at | Trip ly | Jumps | Profit /t | Trip profit | Per hour ▼ | Updated", order[8]);
        Assert.Equal(Colour(Draw.Gold), Ink(Line(rig.Box.Content(), "Order"), run: 1));     // gold while it is being changed

        rig.Keys("menu");
        Assert.Equal("► | Order   Profit per hour", Read(rig.Box.Content())[7]);

        rig.Keys("forward", "forward");                     // the second trade is lit, and the box above shows it
        UIElement lit = rig.Box.Content();
        Assert.StartsWith("BAUXITE  ||  trade 2 of 3", Read(lit)[0]);
        Assert.Equal("► | 2 | Bauxite | Alpha Port | Gamma Dock · Gamma | 50 | 8 | +5,000 | +2.0M | 20.0M | 1h ago", Read(lit)[10]);
        // The strip behind the lit line is the old tool's blue; behind the others it is all but invisible. The open page's name is lit the same way.
        Border[] strips = [.. Pieces(lit).OfType<Border>().Where(strip => strip.Tag is string tag && tag.StartsWith("row:", StringComparison.Ordinal))];
        Assert.Equal(new[] { ("row:0", "#01000000"), ("row:1", "#01000000"), ("row:2", "#553D8BFF"), ("row:3", "#01000000") },
            strips.Select(strip => ((string)strip.Tag, ((SolidColorBrush)strip.Background).Color.ToString())));
        Assert.Equal((Colour(Draw.White), Colour("#553D8BFF")), (((SolidColorBrush)Line(lit, "TRADES").Foreground).Color, ((SolidColorBrush)Line(lit, "TRADES").Background).Color));

        rig.Keys("menu", "forward");                        // accepted from the list; opened again on its line
        List<string> accepted = Read(rig.Box.Content());
        Assert.Equal("► | 2 | Bauxite  ✔ | Alpha Port | Gamma Dock · Gamma | 50 | 8 | +5,000 | +2.0M | 20.0M | 1h ago", accepted[10]);
        Assert.Equal("Trades 1 to 3 of 3. Jumps count the whole trip, there and back.  ✔ marks the trade you accepted; selecting another one switches to it.", accepted[12]);
        rig.Keys("forward");                                // lit elsewhere, the accepted trade keeps its tick
        Assert.StartsWith("✔ | 2 | Bauxite | ", Read(rig.Box.Content())[10]);
    });

    [Fact]
    public void A_long_list_shows_five_trades_at_a_time_and_an_empty_one_says_why() => OnWindowThread(() =>
    {
        TradeRow[] seven = [.. Enumerable.Range(1, 7).Select(n => BoxRig.Worded(HaulRig.Trade($"metal{n}", Alpha, new Place($"Far {n}", [10, 10 * n, 0], $"Port {n}", 500 + n), oneWay: true)))];
        using var rig = new BoxRig(answer: request => HaulRig.Found(request, seven));

        rig.Keys("forward", "forward", "forward", "forward", "forward", "forward");
        List<string> list = Read(rig.Box.Content());
        Assert.Equal("# ▲ | Commodity | Sell at | Trip ly | Jumps | Profit /t | Trip profit | Per hour | Updated", list[7]);    // one place to buy: no "Buy at" column
        Assert.Equal(new[] { "2 | Metal2", "3 | Metal3", "4 | Metal4", "5 | Metal5", "► | 6 | Metal6" }, list.Skip(8).Take(5).Select(line => line[..line.IndexOf(" | Port", StringComparison.Ordinal)]));
        Assert.Equal("Trades 2 to 6 of 7. Jumps count the whole trip.", list[13]);     // one way: no "there and back"

        rig.Haul.Search.Answer = BoxRig.Nothing;
        rig.Hauling.SearchAgain();
        Assert.Equal(new[]
        {
            "ELITE STUDIO   Alpha   docked at Alpha Port",
            "No market data for stations in Alpha that fit your filters. Try older prices or another pad size.",
            "TRADES SETTINGS PRESETS",
            "► | Order   Best first",
            "# ▲ | Commodity | Sell at | Trip ly | Jumps | Profit /t | Trip profit | Per hour | Updated",
            "No market data for stations in Alpha that fit your filters. Try older prices or another pad size.",
        }, Read(rig.Box.Content()));
    });

    [Fact]
    public void The_settings_page_and_the_presets_page_are_drawn_with_their_values_and_marks() => OnWindowThread(() =>
    {
        using var rig = new BoxRig();

        rig.Keys("menu", "menu", "forward");
        Assert.Equal(new[]
        {
            "TRADES SETTINGS PRESETS",
            "► | Trip | Round trip (bring a load back)",
            "Rank trades by | Profit per hour",
            "Max. route distance | 30 ly, buy to sell",
            "Buy within | 15 ly of me",
            "Max. price age | 2 days",
            "Trades start | Near me",
            "Min. supply | 100 t or more",
            "Min. demand | 100 t or more",
            "Changes are saved, and a new search starts by itself a moment after the last change.",
        }, Read(rig.Box.Content()).Skip(6));

        rig.Keys("menu", "forward");                        // being changed: arrows either side, and gold
        UIElement changing = rig.Box.Content();
        Assert.Equal("► | Trip | ◄ | One way (pick up and drop off) | ►", Read(changing)[7]);
        Assert.Equal(Colour(Draw.Gold), ((SolidColorBrush)Line(changing, "One way").Foreground).Color);

        rig.Keys("menu");
        rig.Hauling.StepSetting("startMode", 1);            // a line that means nothing just now says so, dimmed
        UIElement anywhere = rig.Box.Content();
        Assert.Equal("Buy within | Not used when trades start anywhere", Read(anywhere)[10]);
        Assert.Equal(Colour(Draw.Dim), ((SolidColorBrush)Line(anywhere, "Not used").Foreground).Color);

        rig.Keys("back", "menu");                           // the presets: the first is in use now that trips are one way
        rig.Hauling.StepSetting("maxLy", 1);
        Assert.Equal(new[]
        {
            "TRADES SETTINGS PRESETS",
            "✔ | Quick one-way hops | one way · routes to 40 ly · ranked by profit per hour",
            "Long round trips | round trip · routes to 120 ly · ranked by trip profit",
            "A preset changes several settings in one go. ✔ marks the one in use. Make your own on Studio's Market page.",
        }, Read(rig.Box.Content()).Skip(6));
        rig.Keys("forward");
        Assert.Equal("► | Quick one-way hops  ✔ | one way · routes to 40 ly · ranked by profit per hour", Read(rig.Box.Content())[7]);

        using var bare = new BoxRig(market: """{ "presets": [] }""");
        bare.Keys("menu", "menu", "menu");
        Assert.Equal(new[] { "TRADES SETTINGS PRESETS", "No presets yet. Save one on Studio's Market page." }, Read(bare.Box.Content()).Skip(6));
    });

    [Fact]
    public void In_a_box_that_takes_clicks_every_piece_of_the_menu_carries_what_a_click_on_it_does() => OnWindowThread(() =>
    {
        using var plain = new BoxRig();
        plain.Keys("menu", "forward");
        Assert.Equal(
            new[] { "tab:trades", "tab:settings", "tab:presets", "row:0", "sort:rank", "sort:ly", "sort:jumps", "sort:ton", "sort:load", "sort:hour", "sort:age", "row:1", "row:2", "row:3" },
            Tags(plain.Box.Content()));

        using var rig = new BoxRig { Clickable = true };
        rig.Keys("menu", "forward");
        UIElement list = rig.Box.Content();
        Assert.Equal(
            new[] { "do:next", "do:copy", "do:accept", "do:cargo", "do:menu", "tab:trades", "tab:settings", "tab:presets", "close", "row:0", "sort:rank", "sort:ly", "sort:jumps", "sort:ton", "sort:load", "sort:hour", "sort:age",
                "row:1", "pick:1", "row:2", "pick:2", "row:3", "pick:3" },
            Tags(list));
        Assert.Equal("TRADES SETTINGS PRESETS ✕ close", Read(list)[7]);     // under the row of buttons
        Assert.EndsWith(" | 2h ago | ✔ accept", Read(list)[10]);

        // A click lands on a run of text or on a strip; either way the tag of the piece it belongs to is found.
        TextBlock accept = Pieces(list).OfType<TextBlock>().First(piece => (string?)piece.Tag == "pick:2");
        Assert.Equal("pick:2", OverlayWindow.TagOf(accept.Inlines.FirstInline));
        Assert.Equal("row:2", OverlayWindow.TagOf(Pieces(list).OfType<Border>().First(strip => (string?)strip.Tag == "row:2")));
        Assert.Equal("tab:settings", OverlayWindow.TagOf(Line(list, "SETTINGS").Inlines.FirstInline));
        Assert.Null(OverlayWindow.TagOf(Line(list, "PROFIT").Inlines.FirstInline));     // the trade above the menu does nothing when clicked
        Assert.Null(OverlayWindow.TagOf(null));

        // Settings: arrows beside every setting that can be changed, each with its own tag.
        rig.Hauling.StepSetting("startMode", 1);
        rig.Box.Click("tab:settings");
        UIElement settings = rig.Box.Content();
        Assert.Equal("Trip | ◄ | Round trip (bring a load back) | ►", Read(settings)[8]);
        Assert.Equal("Buy within | Not used when trades start anywhere", Read(settings)[11]);
        string[] tags = Tags(settings);
        Assert.Equal(new[] { "dec:0", "dec:1", "dec:2", "dec:4", "dec:5", "dec:6", "dec:7" }, tags.Where(tag => tag.StartsWith("dec:", StringComparison.Ordinal)));
        Assert.Equal(new[] { "inc:0", "inc:1", "inc:2", "inc:4", "inc:5", "inc:6", "inc:7" }, tags.Where(tag => tag.StartsWith("inc:", StringComparison.Ordinal)));
    });

    // ---- clicks, and the window that takes them ----

    [Fact]
    public void A_click_reaches_the_studio_as_the_tag_of_the_piece_clicked_only_while_the_box_takes_clicks() => OnWindowThread(() =>
    {
        // Hidden, as every box starts, and never shown, so nothing can appear on the owner's screen while this runs.
        var window = new OverlayWindow("haul", new OverlaySettings());
        TextBlock tagged = null!, plain = null!;
        window.SetContent(() =>     // built afresh on every redraw, as a studio builds it
        {
            (tagged, plain) = (Draw.Text(13).Add("SETTINGS"), Draw.Text(13).Add("just text"));
            tagged.Tag = "tab:settings";
            return Draw.Stack(tagged, plain);
        });
        string[] Tagged() => [.. Tags((UIElement)window.Content).Order(StringComparer.Ordinal)];
        var clicked = new List<string>();
        window.Clicked += clicked.Add;
        bool Click(UIElement on)
        {
            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
            on.RaiseEvent(press);
            return press.Handled;
        }

        Assert.False(window.TakingClicks);
        Assert.False(Click(tagged));                        // click-through: not the box's click to take
        Assert.Empty(clicked);
        Assert.Equal(new[] { "tab:settings" }, Tagged());   // and nothing of its own to click

        window.TakeClicks(true);
        Assert.Equal((true, true, Cursors.Hand), (window.TakingClicks, window.IsHitTestVisible, window.Cursor));
        Assert.True(Click(tagged));
        Assert.True(Click(plain));                          // swallowed all the same, so it never lands on what is behind
        Assert.Equal(new[] { "tab:settings" }, clicked);
        // A strip of its own above the content: the handle to drag it by, and buttons for its size and to hide it.
        Assert.Equal(new[] { "box:bigger", "box:drag", "box:hide", "box:smaller", "tab:settings" }, Tagged());
        Assert.True(Click(Pieces(window).OfType<TextBlock>().First(piece => (string?)piece.Tag == "box:hide")));
        Assert.Equal(new[] { "tab:settings", "box:hide" }, clicked);

        window.TakeClicks(false);
        Assert.Equal((false, false, null), (window.TakingClicks, window.IsHitTestVisible, window.Cursor));
        Assert.False(Click(tagged));
        Assert.Equal(2, clicked.Count);
        Assert.Equal(new[] { "tab:settings" }, Tagged());   // the strip went with it
        Assert.False(window.IsVisible);
    });

    // ---- pictures ----

    [Fact]
    public void Render_shots_draw_every_state_of_hauling_into_a_folder_of_their_own() => OnWindowThread(() =>
    {
        string shots = Directory.CreateTempSubdirectory("EliteStudioHaulShots-").FullName;
        try
        {
            Assert.Equal(0, OverlayWindow.RenderShots(shots));

            (int Width, int Height) Size(string name)
            {
                using FileStream file = File.OpenRead(Path.Combine(shots, OverlayWindow.HaulingShots, name + ".png"));
                BitmapFrame picture = BitmapDecoder.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                return (picture.PixelWidth, picture.PixelHeight);
            }

            Assert.Equal(
                new[]
                {
                    "accepted", "buttons", "exploring", "lookup", "lookup-message", "menu-accepted", "menu-mouse", "menu-mouse-settings", "menu-order", "menu-page-names",
                    "menu-presets", "menu-setting-changing", "menu-settings", "menu-trades", "message", "route", "route-trip", "trade-back", "trade-back-loaded", "trade-buy",
                    "trade-one-way", "trade-sell", "trade-sell-copied",
                },
                Directory.GetFiles(Path.Combine(shots, OverlayWindow.HaulingShots)).Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal));

            // Pictures are twice life size. A trade is a header, three rows and two lines; one way has a row less;
            // a message is two lines; the nav box's route is one, and three as a trip with something to do here
            // and at the next stop. The menu hangs under the trade, and in the game a row of buttons between them.
            int trade = Size("trade-buy").Height, oneWay = Size("trade-one-way").Height, message = Size("message").Height, route = Size("route").Height;
            Assert.InRange(trade, 360, 440);
            Assert.InRange(trade - oneWay, 40, 52);
            Assert.True(route < message && message < Size("route-trip").Height && message < oneWay, $"route {route}, message {message}, one way {oneWay}");
            Assert.Equal(trade, Size("accepted").Height);
            Assert.InRange(Size("lookup").Height, trade - 4, trade);   // its third row is in slightly smaller letters
            Assert.All(new[] { "menu-page-names", "menu-trades", "menu-order", "menu-accepted", "menu-settings", "menu-setting-changing", "menu-presets", "menu-mouse", "menu-mouse-settings" },
                name => Assert.True(Size(name).Height > trade + 150, name));
            int buttons = Size("buttons").Height - trade;
            Assert.InRange(buttons, 40, 80);
            Assert.Equal(Size("menu-settings").Height + buttons, Size("menu-mouse-settings").Height);     // eight settings either way
            Assert.True(Size("menu-mouse").Width > Size("menu-trades").Width);                  // "accept" beside every trade
        }
        finally
        {
            Directory.Delete(shots, recursive: true);
        }
    });
}
