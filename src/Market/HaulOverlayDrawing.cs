using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Nav;
using EliteStudio.Overlay;

namespace EliteStudio.Market;

// What hauling puts in the overlay's box: the Elite Haul Overlay's Render-Overlay, Draw-Lookup and
// Render-Menu, line for line, on the pieces in Overlay.Draw. Texts, signs, colours, sizes and number
// formats are the old tool's own; numbers are always written the English way (41,293 and 86.1),
// whatever Windows is set to. The class is described in HaulOverlay.cs.
public sealed partial class HaulOverlay
{
    const string CheckMark = "✔", Right = "►", Left = "◄", Up = "▲", Down = "▼";
    const string LitBand = "#553D8BFF";     // behind the lit line and the open page's name; the old tool's blue whatever the accent colour is
    const string UnlitBand = Draw.Unlit;    // a line could otherwise only be clicked on its letters
    const string OnGold = "#FF080C14";      // the page name under the highlight: dark letters on gold

    /// <summary>What goes in the hauling box now, built afresh on every redraw.</summary>
    internal UIElement Content()
    {
        OverlaySettings look = overlay.Settings;
        var lines = new List<UIElement>();
        if (hauling.Shown is { } trade && hauling.NextStop is { } stop) AddTrade(lines, trade, stop, look);
        else if (hauling.Look is { } lookup)
        {
            // A lookup with no place to show: its title, and what it has to say.
            lines.Add(Draw.Text(17).Add(LookupTitle(lookup), look.Accent, bold: true));
            lines.Add(Draw.Note(lookup.Message));
        }
        else
        {
            // Nothing of hauling's own: a line saying where the player is, with the reason there is no trade under it.
            // (Before the game has said where the player is, both would say "Waiting for Elite".)
            lines.Add(OverlayWindow.StatusLine(game.Snapshot(), look.Accent));
            if (!string.IsNullOrEmpty(game.StarSystem)) lines.Add(Draw.Note(hauling.Message));
        }
        if (Clickable()) lines.Add(Buttons());
        if (open) lines.Add(MenuBox(look));
        return Draw.Stack([.. lines]);
    }

    /// <summary>
    /// The row of buttons of a hauling box that takes clicks: what the Elite Haul Overlay had keys
    /// for. During a lookup they read as what they do then, and the two that do nothing are left out.
    /// </summary>
    StackPanel Buttons()
    {
        bool lookup = hauling.Look is not null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        void Add(string text, string action) => row.Children.Add(Draw.Button(text, "do:" + action));
        Add(lookup ? "Next place" : "Next trade", "next");
        Add("Copy system", "copy");
        if (!lookup) Add(hauling.Accepted is null ? CheckMark + " Accept this trade" : DropButton, "accept");
        Add(lookup ? BackButton : "Sell my cargo", "cargo");
        if (!lookup) Add(open ? "Close the list" : "Trades and settings", "menu");
        return row;
    }

    /// <summary>
    /// What goes in the nav box now: the route being followed, or the exploring trip's line, or a
    /// line saying there is neither. A box that takes clicks has a button that copies the next stop.
    /// </summary>
    internal UIElement NavContent()
    {
        OverlaySettings look = overlay.Settings;
        if (RouteShown is not { } route)
            return explore.OverlayLine(look.Accent) ?? Draw.Note("No route is being followed. Plot one in Studio Nav.");
        var lines = new List<UIElement>(RouteLines(route));
        if (route.Next is not null && overlay.Box(NavBox)?.TakingClicks == true)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(Draw.Button("Copy system", "copy:" + route.Next));
            lines.Add(row);
        }
        return Draw.Stack([.. lines]);
    }

    // ---------------------------------------------------------------- a trade, or a lookup's place to sell

    void AddTrade(List<UIElement> lines, TradeRow trade, Stop stop, OverlaySettings look)
    {
        Lookup? lookup = hauling.Look;
        // Top left: what to buy first. Top right, in the same big letters: what to buy for the way back (round trips
        // with a load to bring). Once the first load is sold the right one turns gold ("buy now") and the left one
        // fades, until the return load is on board.
        bool back = lookup is null && !trade.OneWay && trade.ReturnProfit > 0 && trade.Return.Length > 0;
        bool onBack = back && stop.What == "BACK";
        bool accepted = hauling.Accepted is not null && lookup is null;

        TextBlock title = Draw.Text(17).Add(lookup is null ? trade.Commodity.ToUpperInvariant() : LookupTitle(lookup), onBack ? Draw.Dim : look.Accent, bold: true);
        TextBlock status = Draw.Text(12.5);
        if (accepted) status.Add(CheckMark + " ACCEPTED", Draw.Green, bold: true);
        string which = lookup is not null ? $"place {lookup.Index + 1} of {lookup.Rows.Count}"
            : accepted ? ""
            : $"trade {(hauling.Index < hauling.Trades.Count ? hauling.Index : 0) + 1} of {hauling.Trades.Count}";
        status.Add($"{which}{Draw.Dot}prices updated {trade.Updated}", Draw.Dim);
        if (!back) lines.Add(Draw.Line(title, status));
        else
        {
            string load = trade.Return.ToUpperInvariant();
            TextBlock bringBack = Draw.Text(17);
            if (onBack && !hauling.ReturnLoaded) bringBack.Add(Right + " BUY NOW   ", Draw.Gold, bold: true, size: 12.5).Add(load, Draw.Gold, bold: true);
            else if (onBack) bringBack.Add(CheckMark + " ON BOARD   ", Draw.Green, bold: true, size: 12.5).Add(load, look.Accent, bold: true);
            else bringBack.Add("THEN BRING BACK   ", Draw.Dim, size: 12.5).Add(load, look.Accent, bold: true);
            lines.Add(Draw.Line(title, status, bringBack));
        }

        Grid table = Draw.Table();
        TextBlock profit = Draw.Text(14.5);
        if (lookup is null) TradeRows(table, profit, trade, stop, look.Accent);
        else LookupRows(table, profit, lookup, trade, stop, look);
        lines.Add(table);
        lines.Add(Draw.Rule(profit));

        // NEXT STOP: the star system to fly to now; at its right-hand end, a note while there is one.
        TextBlock next = Draw.Text(14.5).Add("NEXT STOP   ", Draw.Gold, bold: true).Add(stop.System, Draw.White, bold: true).Add($"   {stop.Verb} {stop.Station}", Draw.Dim);
        if (stop.Here) next.Add(Draw.Dot + "you are in this system");
        else if (stop.Ly > 0)
        {
            next.Add($"{Draw.Dot}{Ly(stop.Ly)} ly from you");
            next.Add(stop.Plotted ? $"{Draw.Dot}{stop.Jumps} {(stop.Jumps == 1 ? "jump" : "jumps")} on your plotted route" : JumpText(stop.Jumps, stop.Load));
        }
        DockPanel nextStop = Draw.Line(next, NoteText());
        nextStop.Margin = new Thickness(0, 4, 0, 0);
        lines.Add(nextStop);
    }

    static void TradeRows(Grid table, TextBlock profit, TradeRow trade, Stop stop, string accent)
    {
        // The leg being flown is gold and marked; once a route is plotted to its end it shows the game's own count, not the estimate.
        string Mark(string leg) => stop.What == leg ? $"{Right} {leg}" : leg;
        string Ink(string leg, string otherwise) => stop.What == leg ? Draw.Gold : otherwise;
        string Jumps(string leg, string estimate) =>
            stop.Plotted && stop.What == leg ? $"{Draw.Dot}{stop.Jumps} {(stop.Jumps == 1 ? "jump" : "jumps")} left" : estimate;
        static string Power(string power) => power.Length > 0 ? " · " + power : "";

        // BUY: where to pick the cargo up, how far that is from the player (flown empty), the price and how much there is.
        table.Cell(0, 0, 12.5, Ink("BUY", accent)).Add(Mark("BUY"), bold: true);
        table.Cell(0, 1, 15).Add(trade.BuyStation, bold: true).Add($"  {PadText(trade.BuyType, trade.BuyPad)} · {trade.BuySystem}{Power(trade.BuyPower)}", Draw.Dim);
        TextBlock toBuy = table.Cell(0, 2, 14);
        if (trade.BuyHere) toBuy.Add("★ you are docked here", Draw.Gold);
        else if (trade.BuyDistanceLy > 0) toBuy.Add($"{Ly(trade.BuyDistanceLy)} ly from you{Jumps("BUY", JumpText(trade.JumpsToBuy, "empty"))}");
        else toBuy.Add("in this system");
        table.Cell(0, 3, 14).Add($"{Draw.Whole(trade.BuyPrice)} cr", bold: true).Add($"   {Draw.Whole(trade.Stock)} t for sale", Draw.Dim);

        // SELL: the destination, in grey; the trip there is flown with a full hold.
        table.Cell(1, 0, 12.5, Ink("SELL", Draw.Grey)).Add(Mark("SELL"), bold: true);
        table.Cell(1, 1, 15, Draw.Grey).Add(trade.SellStation, bold: true).Add($"  {PadText(trade.SellType, trade.SellPad)} · {trade.SellSystem}{Power(trade.SellPower)}");
        table.Cell(1, 2, 14, Draw.Grey).Add($"{Ly(trade.DistanceLy)} ly trip{Jumps("SELL", JumpText(trade.JumpsOut, "full"))}");
        table.Cell(1, 3, 14, Draw.Grey).Add($"{Draw.Whole(trade.SellPrice)} cr", bold: true).Add($"   wants {Draw.Whole(trade.Demand)} t");

        // BACK: what to carry on the way back, if anything (a full hold needs more jumps than an empty one).
        // A one-way trade is only a pick-up and a drop-off, so it has no BACK row.
        if (!trade.OneWay)
        {
            bool loaded = trade.ReturnProfit > 0;
            table.Cell(2, 0, 12.5, Ink("BACK", accent)).Add(Mark("BACK"), bold: true);
            TextBlock load = table.Cell(2, 1, 15);
            if (loaded) load.Add(trade.Return, bold: true).Add("  to " + trade.BuyStation, Draw.Dim);
            else load.Add("Nothing worth carrying back", Draw.Dim);
            table.Cell(2, 2, 14).Add($"{Ly(trade.DistanceLy)} ly back{Jumps("BACK", JumpText(trade.JumpsBack, loaded ? "full" : "empty"))}");
            TextBlock gain = table.Cell(2, 3, 14);
            if (loaded) gain.Add($"+{Draw.Whole(trade.ReturnProfitPerTon)} cr/t", Draw.Green, bold: true);
        }

        // PROFIT: everything about money is green. The totals need the hold's size.
        profit.Add("PROFIT   ", Draw.Green, bold: true).Add($"+{Draw.Whole(trade.ProfitPerTon)} cr/t", Draw.Green, bold: true);
        if (trade.RoundTripProfit <= 0)
        {
            profit.Add(Draw.Dot + "set your cargo capacity to see totals", Draw.Dim);
            return;
        }
        string minutes = Math.Round(trade.Minutes).ToString("0", CultureInfo.InvariantCulture);
        profit.Add(Draw.Dot, Draw.Dim).Add($"+{Short(trade.ProfitPerTrip)} this load", Draw.Green);
        if (trade.OneWay) profit.Add($"{Draw.Dot}one way in ~{minutes} min", Draw.Dim);
        else profit.Add(Draw.Dot + "round trip ", Draw.Dim).Add("+" + Short(trade.RoundTripProfit), Draw.Green).Add($" in ~{minutes} min", Draw.Dim);
        profit.Add(Draw.Dot, Draw.Dim).Add(Short(trade.PerHour) + " cr/hour", Draw.Green, bold: true);
    }

    /// <summary>A lookup's rows: what is being sold, the chosen place to sell it, and the next choice down the list.</summary>
    static void LookupRows(Grid table, TextBlock foot, Lookup lookup, TradeRow place, Stop stop, OverlaySettings look)
    {
        bool cargo = lookup.Source == "cargo";
        string paid = cargo ? (lookup.Paid > 0 ? $"   you paid {Draw.Whole(lookup.Paid)} cr/t" : "   price paid not known")
            : lookup.Paid > 0 ? $"   {Draw.Whole(lookup.Paid)} cr/t at {lookup.From}"
            : "   not for sale where you are";
        table.Cell(0, 0, 12.5, look.Accent).Add(cargo ? "CARGO" : "BUY", bold: true);
        table.Cell(0, 1, 15).Add($"{Draw.Whole(lookup.Count)} t of {lookup.Name}", bold: true).Add(paid, Draw.Dim);

        table.Cell(1, 0, 12.5, Draw.Gold).Add(Right + " SELL", bold: true);
        table.Cell(1, 1, 15, Draw.Grey).Add(place.SellStation, bold: true).Add($"  {PadText(place.SellType, place.SellPad)} · {place.SellSystem}");
        table.Cell(1, 2, 14, Draw.Grey).Add(stop.Here ? "in this system" : $"{Ly(stop.Ly)} ly from you{JumpText(stop.Jumps, "full")}");
        TextBlock price = table.Cell(1, 3, 14, Draw.Grey).Add($"{Draw.Whole(place.SellPrice)} cr", bold: true).Add($"   wants {Draw.Whole(place.Demand)} t");
        if (place.Demand < lookup.Count) price.Add("   (not your whole load)", Draw.Gold);

        if (lookup.Rows.Count > 1)
        {
            TradeRow also = lookup.Rows[(lookup.Index + 1) % lookup.Rows.Count];
            table.Cell(2, 0, 12.5, Draw.Dim).Add("ALSO", bold: true);
            table.Cell(2, 1, 14, Draw.Dim).Add($"{also.SellStation}  {PadText(also.SellType, also.SellPad)} · {also.SellSystem}");
            table.Cell(2, 2, 14, Draw.Dim).Add($"{Ly(also.DistanceLy)} ly{JumpText(also.JumpsOut, "full")}");
            table.Cell(2, 3, 14, Draw.Dim).Add($"{Draw.Whole(also.SellPrice)} cr   wants {Draw.Whole(also.Demand)} t{Draw.Dot}{also.Updated}");
        }

        if (lookup.Paid > 0)
            foot.Add("PROFIT   ", Draw.Green, bold: true).Add($"+{Draw.Whole(place.ProfitPerTon)} cr/t", Draw.Green, bold: true)
                .Add(Draw.Dot, Draw.Dim).Add($"+{Short(place.ProfitPerTrip)} for {Draw.Whole(place.Units)} t", Draw.Green);
        else
            foot.Add("SELLS FOR   ", Draw.Green, bold: true).Add($"{Draw.Whole(place.SellPrice)} cr/t", Draw.Green, bold: true)
                .Add(Draw.Dot, Draw.Dim).Add($"{Short((double)place.SellPrice * place.Units)} for {Draw.Whole(place.Units)} t", Draw.Green);
    }

    static string LookupTitle(Lookup lookup) =>
        (lookup.Source == "cargo" ? "SELL YOUR CARGO" : "WHERE TO SELL") + (lookup.Name.Length > 0 ? Draw.Dot + lookup.Name.ToUpperInvariant() : "");

    /// <summary>
    /// Studio Nav's lines. In the style of NEXT STOP: the system to fly to on the route being
    /// followed, how far and how many jumps that is, and how much of the route is left after it.
    /// A stop of a trip comes with a note on what to do there (the station to dock at and what to
    /// buy): the next stop's goes under its line, and the note of the stop the ship is in stands
    /// over it, for that is what to do now.
    /// </summary>
    static IEnumerable<UIElement> RouteLines(NavProgress route)
    {
        if (route.HereNote.Length > 0) yield return Wrapped(Draw.Text(14.5).Add("HERE   ", Draw.Gold, bold: true).Add(route.HereNote, Draw.White));
        if (route.Next is null) yield break;
        TextBlock next = Draw.Text(14.5).Add("NEXT STOP   ", Draw.Gold, bold: true).Add(route.Next, Draw.White, bold: true);
        if (route.NextLy > 0) next.Add($"{Draw.Dot}{Ly(route.NextLy)} ly from you");
        next.Add(route.NextPlotted ? $"{Draw.Dot}{route.NextJumps} {(route.NextJumps == 1 ? "jump" : "jumps")} on your plotted route" : JumpText(route.NextJumps));
        next.Add(Draw.Dot + (route.HopsLeft == 1 ? "the last stop of your route" : $"{route.HopsLeft} stops left on your route"), Draw.Dim);
        yield return next;
        if (route.NextNote.Length > 0) yield return Wrapped(Draw.Note(route.NextNote));
    }

    /// <summary>A line of text that may be long: it goes on under itself instead of making the box ever wider.</summary>
    static TextBlock Wrapped(TextBlock text)
    {
        (text.TextWrapping, text.MaxWidth, text.HorizontalAlignment) = (TextWrapping.Wrap, 900, HorizontalAlignment.Left);
        return text;
    }

    /// <summary>The small text at the right-hand end of the NEXT STOP line: hauling's note while there is one ("✔ Sol copied"), otherwise nothing.</summary>
    TextBlock NoteText() => hauling.Note is { } note
        ? Draw.Text(12.5).Add(note.Text, note.Good ? Draw.Green : Draw.Gold, bold: true)
        : Draw.Text(12.5);

    // ---------------------------------------------------------------- the menu

    /// <summary>The menu under the trade: the page names, the open page's lines, and a line of help.</summary>
    Border MenuBox(OverlaySettings look)
    {
        Settle();
        // The page names: the open one is lit, and gold while the highlight is up here.
        var names = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (MenuPage name in HaulMenu.Pages)
        {
            bool on = name.Key == page, here = on && lit < 0;
            names.Children.Add(Draw.Button(name.Title, "tab:" + name.Key, here ? OnGold : on ? Draw.White : Draw.Dim, here ? Draw.Gold : on ? LitBand : UnlitBand, bold: true));
        }
        if (mouse) names.Children.Add(Draw.Button("✕ close", "close"));

        Grid lines = Draw.Table();
        string note = page switch
        {
            "trades" => TradesPage(lines, look),
            "settings" => SettingsPage(lines),
            _ => PresetsPage(lines),
        };

        TextBlock under = Wrapped(Draw.Text(12.5, Draw.Dim).Add(note));
        under.Margin = new Thickness(0, 4, 0, 0);

        Border box = Draw.Rule(Draw.Stack(names, lines, under));
        (box.Margin, box.Padding) = (new Thickness(0, 7, 0, 0), new Thickness(0, 6, 0, 1));
        return box;
    }

    /// <summary>The short trade list: its order, the column titles, and the five trades round the lit one.</summary>
    string TradesPage(Grid lines, OverlaySettings look)
    {
        IReadOnlyList<TradeRow> list = hauling.SortedTrades;
        ListSort order = HaulMenu.ListSorts.FirstOrDefault(sort => sort.Key == hauling.Settings.ListSort) ?? HaulMenu.ListSorts[0];
        bool manyBuys = list.Select(trade => trade.BuyStation).Distinct().Count() > 1;
        // column title, the order it stands for, and whether its text sits at the right
        List<(string Title, string Order, bool AtRight)> columns = [("", "", false), ("#", "rank", true), ("Commodity", "", false)];
        if (manyBuys) columns.Add(("Buy at", "", false));
        columns.AddRange([("Sell at", "", false), ("Trip ly", "ly", true), ("Jumps", "jumps", true), ("Profit /t", "ton", true),
            ("Trip profit", "load", true), ("Per hour", "hour", true), ("Updated", "age", false)]);
        if (mouse) columns.Add(("", "", false));

        // line 0: the order of the list
        bool onOrder = lit == 0;
        Band(lines, 0, columns.Count, onOrder, "row:0");
        MenuCell(lines, 0, 0, "row:0", Draw.Gold).Add(onOrder ? Right : "", bold: true);
        TextBlock orderLine = MenuCell(lines, 0, 1, "row:0").Add("Order   ", Draw.Dim);
        Grid.SetColumnSpan(orderLine, columns.Count - 1);
        if (onOrder && editing) orderLine.Add($"{Left}  {order.Name}  {Right}", Draw.Gold, bold: true);
        else orderLine.Add(order.Name, Draw.White, bold: true);

        // the column titles; in mouse mode a click on one puts the list in that order
        for (int column = 0; column < columns.Count; column++)
        {
            (string title, string key, bool atRight) = columns[column];
            bool inOrder = key.Length > 0 && key == order.Key;
            MenuCell(lines, 1, column, key.Length > 0 ? "sort:" + key : "", inOrder ? Draw.Gold : Draw.Dim, atRight)
                .Add(inOrder ? $"{title} {(order.BiggestFirst ? Down : Up)}" : title, bold: inOrder);
        }

        int shown = Math.Min(HaulMenu.ListRows, list.Count - top);
        for (int i = 0; i < shown; i++)
        {
            TradeRow trade = list[top + i];
            int line = top + i + 1, at = 2 + i, column = 0;
            bool on = lit == line, mine = trade.IsSameTrade(hauling.Accepted);
            string ink = on ? Draw.White : Draw.Grey, tag = $"row:{line}";
            Band(lines, at, columns.Count, on, tag);
            MenuCell(lines, at, column++, tag, on ? Draw.Gold : Draw.Green).Add(on ? Right : mine ? CheckMark : "", bold: true);
            MenuCell(lines, at, column++, tag, Draw.Dim, atRight: true).Add(trade.Rank.ToString(CultureInfo.InvariantCulture));
            MenuCell(lines, at, column++, tag, ink).Add(trade.Commodity + (mine && on ? "  " + CheckMark : ""), bold: true);
            if (manyBuys) MenuCell(lines, at, column++, tag, ink).Add(trade.BuyStation);
            MenuCell(lines, at, column++, tag, ink).Add($"{trade.SellStation} · {trade.SellSystem}");
            MenuCell(lines, at, column++, tag, ink, atRight: true).Add(Ly(trade.DistanceLy));
            MenuCell(lines, at, column++, tag, ink, atRight: true).Add((trade.JumpsToBuy + trade.JumpsOut + trade.JumpsBack).ToString(CultureInfo.InvariantCulture));
            MenuCell(lines, at, column++, tag, Draw.Green, atRight: true).Add("+" + Draw.Whole(trade.ProfitPerTon));
            MenuCell(lines, at, column++, tag, Draw.Green, atRight: true).Add("+" + Short(trade.RoundTripProfit));
            MenuCell(lines, at, column++, tag, Draw.Green, atRight: true).Add(Short(trade.PerHour), bold: true);
            MenuCell(lines, at, column++, tag, Draw.Dim).Add(trade.Updated);
            if (mouse) MenuCell(lines, at, column, $"pick:{line}", mine ? Draw.Green : look.Accent).Add(CheckMark + " accept", bold: true);
        }

        return list.Count == 0 ? hauling.Message
            : $"Trades {top + 1} to {top + shown} of {list.Count}. Jumps count the whole trip{(list[0].OneWay ? "" : ", there and back")}."
                + (hauling.Accepted is null ? "" : $"  {CheckMark} marks the trade you accepted; selecting another one switches to it.");
    }

    /// <summary>The settings that can be changed from the overlay, each with its value; arrows beside the one being changed.</summary>
    string SettingsPage(Grid lines)
    {
        IReadOnlyList<SettingLine> settings = hauling.SettingLines;
        for (int i = 0; i < settings.Count; i++)
        {
            SettingLine setting = settings[i];
            bool on = lit == i, changing = on && editing, arrows = setting.Usable && (changing || mouse);
            string tag = $"row:{i}", arrowInk = changing ? Draw.Gold : Draw.Dim;
            Band(lines, i, 5, on, tag);
            MenuCell(lines, i, 0, tag, Draw.Gold).Add(on ? Right : "", bold: true);
            MenuCell(lines, i, 1, tag, on ? Draw.White : Draw.Grey).Add(setting.Label);
            MenuCell(lines, i, 2, arrows ? $"dec:{i}" : tag, arrowInk).Add(arrows ? Left : "", bold: true);
            MenuCell(lines, i, 3, tag, changing ? Draw.Gold : setting.Usable ? Draw.White : Draw.Dim).Add(setting.Text, bold: true);
            MenuCell(lines, i, 4, arrows ? $"inc:{i}" : tag, arrowInk).Add(arrows ? Right : "", bold: true);
        }

        return "Changes are saved, and a new search starts by itself a moment after the last change.";
    }

    /// <summary>The presets by name, each with what it sets; a tick on the one whose settings are in use.</summary>
    string PresetsPage(Grid lines)
    {
        IReadOnlyList<PresetLine> presets = hauling.PresetLines;
        for (int i = 0; i < presets.Count; i++)
        {
            PresetLine preset = presets[i];
            bool on = lit == i;
            string tag = $"row:{i}";
            Band(lines, i, 3, on, tag);
            MenuCell(lines, i, 0, tag, on ? Draw.Gold : Draw.Green).Add(on ? Right : preset.Active ? CheckMark : "", bold: true);
            MenuCell(lines, i, 1, tag, on ? Draw.White : Draw.Grey).Add(preset.Name + (preset.Active && on ? "  " + CheckMark : ""), bold: true);
            MenuCell(lines, i, 2, tag, Draw.Dim).Add(preset.Text);
        }

        // The Haul Overlay sent the player to its main window here; presets are made on Studio's Market page now.
        return presets.Count > 0 ? $"A preset changes several settings in one go. {CheckMark} marks the one in use. Make your own on Studio's Market page."
            : "No presets yet. Save one on Studio's Market page.";
    }

    /// <summary>One piece of text in the menu's lines. The tag says what a click on it does in mouse mode; "" for nothing.</summary>
    static TextBlock MenuCell(Grid lines, int line, int column, string tag, string? colour = null, bool atRight = false)
    {
        TextBlock cell = lines.Cell(line, column, 13.5, colour);
        cell.Margin = new Thickness(column > 0 ? 16 : 5, 2.5, 5, 2.5);
        cell.HorizontalAlignment = atRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        if (tag.Length > 0) cell.Tag = tag;
        return cell;
    }

    /// <summary>The strip behind a line of the menu: blue behind the lit one. In mouse mode it makes the whole line take a click.</summary>
    static void Band(Grid lines, int line, int columns, bool on, string tag)
    {
        var band = new Border { Background = Draw.Brush(on ? LitBand : UnlitBand), CornerRadius = new CornerRadius(3), Tag = tag };
        Grid.SetRow(band, line);
        Grid.SetColumnSpan(band, columns);
        lines.Children.Add(band);   // before the line's texts, so it lies behind them
    }

    // ---------------------------------------------------------------- numbers and small texts

    /// <summary>Light years: at most one decimal, none when whole.</summary>
    static string Ly(double lightYears) => lightYears.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Big sums in short: 17,089,800 is 17.1M, 250,000 is 250k, 1,234,567,890 is 1.23B.</summary>
    static string Short(double credits) => Math.Abs(credits) switch
    {
        >= 1e9 => (credits / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + "B",
        >= 1e6 => (credits / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M",
        >= 1e5 => (credits / 1e3).ToString("0", CultureInfo.InvariantCulture) + "k",
        _ => credits.ToString("N0", CultureInfo.InvariantCulture),
    };

    /// <summary>An estimated number of jumps with how the hold is on the way ("full", "empty"); nothing at all when not known.</summary>
    static string JumpText(int jumps, string hold = "") =>
        jumps <= 0 ? "" : Draw.Dot + (jumps == 1 ? "1 jump" : $"~{jumps} jumps") + (hold.Length > 0 ? " " + hold : "");

    /// <summary>"(L pad)", "(settlement, M pad)", "(carrier, L pad)".</summary>
    static string PadText(string stationType, string pad)
    {
        string kind = stationType.Contains("Settlement", StringComparison.OrdinalIgnoreCase) ? "settlement, "
            : stationType.Contains("FleetCarrier", StringComparison.OrdinalIgnoreCase) ? "carrier, "
            : "";
        return $"({kind}{pad} pad)";
    }

    // ---------------------------------------------------------------- pictures of every state, for --render-shots

    /// <summary>
    /// Plays a made-up evening of hauling and hands every state the box can be in to
    /// <paramref name="shoot"/> to be drawn: the picture's file name, the overlay settings to draw it
    /// with, and what goes in the box. Nothing of the player's is read or written and no service is
    /// asked: the game, the trade search and the clock are stand-ins, the settings live in a scratch
    /// folder, and the keys are pressed the way the hotkeys press them. (Hauling logs what it does;
    /// the program answers --render-shots before it starts its log, so those lines go nowhere.)
    /// </summary>
    internal static void RenderShots(Action<string, OverlaySettings, Func<UIElement?>> shoot)
    {
        string scratch = Directory.CreateTempSubdirectory("EliteStudioShots-").FullName;
        try
        {
            DateTime clock = new(2026, 10, 9, 20, 0, 0);
            const string noOldTool = "";   // there is no Elite Haul Overlay to copy from
            var settings = new Settings(Path.Combine(scratch, "settings.json"));
            // The settings of the "Long round trips" preset, so the presets page has a tick to show.
            settings.Set(MarketSettings.Section, new MarketSettings { RankBy = "trip", MaxLy = 120, JumpRange = 14, JumpEmpty = 27, CargoOverride = SampleHold });
            settings.Set("nav", new NavSettings { Route = SampleRoute(), Reached = 0 });

            var game = new GameState();
            var search = new SampleSearch();
            using var web = new WebServer(scratch);
            using var overlay = new OverlayHost(web, settings, game, headless: true);
            using var spansh = new Spansh();
            var hauling = new Hauling(game, settings, search, () => clock, noOldTool);
            var nav = new NavRoutes(game, settings, spansh);
            var explore = new ExploreTrip(game, settings);
            bool clicks = false;   // as if the hauling box were shown in the game, where it takes clicks
            var box = new HaulOverlay(overlay, hauling, nav, explore, game, () => clock, noOldTool) { ToClipboard = _ => { }, Clickable = () => clicks };

            void Shoot(string name, bool navBox = false) => shoot(name + ".png", overlay.Settings, navBox ? box.NavContent : box.Content);
            void Keys(params string[] keys) => Array.ForEach(keys, overlay.Press);
            void Later(double seconds)
            {
                clock = clock.AddSeconds(seconds);
                hauling.Tick();
                box.Tick();
            }
            void Game(params string[] journalLines)
            {
                foreach (string line in journalLines)
                {
                    using JsonDocument parsed = JsonDocument.Parse(line);
                    game.Apply(parsed.RootElement.Clone(), replay: false);
                }
                game.RaiseChanged();
            }
            const string inErlaza = """{ "event":"FSDJump", "StarSystem":"Erlaza", "StarPos":[86.1,0,0] }""";
            const string atItoLanding = """{ "event":"Docked", "StationName":"Ito Landing", "StarSystem":"Erlaza", "MarketID":1 }""";
            const string undocked = """{ "event":"Undocked" }""";

            // In Sol, following a Studio Nav route: the nav box's line. In the hauling box, with no trade in reach: why there is none.
            Game("""{ "event":"Location", "Docked":false, "StarSystem":"Sol", "StarPos":[0,0,0] }""");
            Later(0.5);
            Shoot("route", navBox: true);
            // A shopping trip from Studio Loadouts in its place: something to buy in Sol, and the next stop with its own list.
            nav.Follow("Shopping for Explorer",
            [
                new TripStop("Sol", 0, 0, 0, 0, "Dock at Abraham Lincoln and buy 5A Frame Shift Drive, 2 × A Shield Booster."),
                new TripStop("Alpha Centauri", 0, 3.03125, -0.09375, 3.15625, "Dock at Hutton Orbital and buy 6A Fuel Scoop, 4D Life Support, 3D Sensors."),
            ]);
            Shoot("route-trip", navBox: true);
            nav.Clear();
            Shoot("message");

            // As if nobody had been here before: the star's scan, the discovery scan and a first planet give Studio Nav's exploring line. Then that trip is put aside.
            Game("""{ "event":"Scan", "BodyID":0, "StarType":"G", "DistanceFromArrivalLS":0.0, "WasDiscovered":false, "WasMapped":false }""",
                """{ "event":"FSSDiscoveryScan", "BodyCount":9 }""", """{ "event":"Scan", "BodyID":3, "PlanetClass":"Icy body", "WasDiscovered":false, "WasMapped":false }""");
            Shoot("exploring", navBox: true);
            explore.Reset();

            // Trades are found: the first one, then accepted with a route plotted to where it buys.
            search.Finds = request => request.Mode == "lookup" ? SamplePlaces(request) : SampleTrades(oneWay: !request.Returns);
            hauling.SearchAgain();
            Shoot("trade-buy");
            Keys("accept");
            game.SetRoute([new("Sol", 1, [0, 0, 0], "G"), new("Erlaza", 2, [86.1, 0, 0], "K")]);
            Game("""{ "event":"FSDTarget", "Name":"Erlaza", "RemainingJumpsInRoute":4 }""");
            Shoot("accepted");

            // Bought at Ito Landing: the next stop is the place to sell. The copy key leaves its note for four seconds.
            Game("""{ "event":"NavRouteClear" }""", inErlaza, atItoLanding, """{ "event":"MarketBuy", "MarketID":1, "Type":"silver", "BuyPrice":41293 }""");
            Shoot("trade-sell");
            Keys("copy");
            Shoot("trade-sell-copied");
            Later(4);

            // Sold at Broglie Terminal: the way back, before and after the load for it is bought.
            Game(undocked, """{ "event":"FSDJump", "StarSystem":"61 Cygni", "StarPos":[86.1,54.4,0] }""",
                """{ "event":"Docked", "StationName":"Broglie Terminal", "StarSystem":"61 Cygni", "MarketID":101 }""",
                """{ "event":"MarketSell", "MarketID":101, "Type":"silver" }""");
            Shoot("trade-back");
            Game("""{ "event":"MarketBuy", "MarketID":101, "Type":"militarygradefabrics", "BuyPrice":3105 }""");
            Shoot("trade-back-loaded");

            // Back at Ito Landing the trade is finished and a new list is made. "Sell my cargo" with silver in the hold, then with nothing in it.
            Game(undocked, inErlaza, atItoLanding);
            game.SetCargo(400, [new("silver", "Silver", 400)]);
            Keys("cargo");
            Shoot("lookup");
            game.SetCargo(0, []);
            Keys("cargo", "cargo");
            Shoot("lookup-message");
            Keys("cargo");

            // One-way trades: no BACK row.
            hauling.StepSetting("returns", 1);
            Later(1);
            Shoot("trade-one-way");
            hauling.StepSetting("returns", 1);
            Later(1);

            // The menu's trade list: the page names lit, a trade lit, the order being changed, and an accepted trade far down the list.
            Keys("menu");
            Shoot("menu-page-names");
            Keys("forward", "forward", "forward");
            Shoot("menu-trades");
            Keys("back", "back", "menu", "forward");
            Shoot("menu-order");
            Keys("menu", "forward", "forward", "forward", "forward", "forward", "forward", "forward", "menu", "forward");
            Shoot("menu-accepted");
            Keys("accept");
            hauling.SortListBy("rank");
            Later(20);   // left alone, the menu closes by itself

            // Its other two pages: a setting lit, the same setting being changed, a preset lit.
            Keys("menu", "menu", "forward", "forward", "forward");
            Shoot("menu-settings");
            Keys("menu", "forward");
            Shoot("menu-setting-changing");
            Keys("back", "menu", "back", "back", "back", "menu", "forward");
            Shoot("menu-presets");
            Later(20);

            // In the game, where the box takes clicks: the row of buttons, a close button, "accept" beside every trade, arrows beside every setting.
            clicks = true;
            Shoot("buttons");
            box.Click("do:menu");
            Shoot("menu-mouse");
            box.Click("tab:settings");
            box.Click("row:4");
            Shoot("menu-mouse-settings");
            box.Click("close");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    const int SampleHold = 388;   // tons; the owner's own Type-8

    /// <summary>The sample's trades, all out of Ito Landing in Erlaza: seven round trips, or three one-way trips.</summary>
    static TradeRow[] SampleTrades(bool oneWay)
    {
        int rank = 0;
        TradeRow Trade(string commodity, int buy, int stock, string station, string system, double ly, int sell, int demand, int backPerTon, double minutes, long perHour, double hours, string updated) => new()
        {
            Rank = ++rank, CommodityId = commodity.ToLowerInvariant(), Commodity = commodity, Units = SampleHold, AgeHours = hours, Updated = updated, OneWay = oneWay,
            BuyStation = "Ito Landing", BuySystem = "Erlaza", BuyMarketId = 1, BuyType = "Coriolis", BuyPad = "L", BuyX = 86.1, BuyPrice = buy, Stock = stock,
            SellStation = station, SellSystem = system, SellMarketId = 100 + rank, SellType = "Coriolis", SellPad = "L", SellX = 86.1, SellY = ly, SellPrice = sell, Demand = demand,
            DistanceLy = ly, ProfitPerTon = sell - buy, ProfitPerTrip = (long)(sell - buy) * SampleHold, RoundTripProfit = (long)(sell - buy + backPerTon) * SampleHold,
            ReturnId = backPerTon > 0 ? "militarygradefabrics" : "", Return = backPerTon > 0 ? "Military Grade Fabrics" : "",
            ReturnProfitPerTon = backPerTon, ReturnUnits = backPerTon > 0 ? SampleHold : 0, ReturnProfit = (long)backPerTon * SampleHold,
            Minutes = minutes, PerHour = perHour, JumpsOut = ShipMath.JumpsFor(ly, 14), JumpsBack = oneWay ? 0 : ShipMath.JumpsFor(ly, backPerTon > 0 ? 14 : 27),
        };
        return oneWay
            ?
            [
                Trade("Haematite", 6880, 1694614, "Powell High", "Wolf 359", 43.5, 19596, 1388, 0, 7, 42_000_000, 12, "12h ago"),
                Trade("Bauxite", 6660, 2032248, "The Golden Nugget", "LHS 2661", 32.3, 15327, 13334316, 0, 6, 33_600_000, 1, "1h ago"),
                Trade("Silver", 41293, 84054, "Broglie Terminal", "61 Cygni", 54.4, 49275, 19198, 0, 8, 23_200_000, 6, "6h ago"),
            ]
            :
            [
                Trade("Silver", 41293, 84054, "Broglie Terminal", "61 Cygni", 54.4, 49275, 19198, 11208, 12, 38_000_000, 6, "6h ago"),
                Trade("Bauxite", 6660, 2032248, "Miller Depot", "Barnard's Star", 45.6, 14982, 291352, 10839, 12, 37_700_000, 5, "5h ago"),
                Trade("Bauxite", 6660, 2032248, "Yamazaki Landing", "WISE 0855-0714", 43.4, 15033, 120556, 6318, 11, 29_000_000, 8, "8h ago"),
                Trade("Bauxite", 6660, 2032248, "The Golden Nugget", "LHS 2661", 32.3, 15327, 13334316, 4550, 10, 30_500_000, 1, "1h ago"),
                Trade("Haematite", 6880, 1694614, "Powell High", "Wolf 359", 43.5, 19596, 1388, 0, 11, 28_000_000, 12, "12h ago"),
                Trade("Gold", 44100, 31400, "Fozard Ring", "Wolf 424", 28.9, 51020, 8800, 5100, 10, 27_400_000, 3, "3h ago"),
                Trade("Bertrandite", 16420, 55020, "Julian Market", "Ross 128", 38.2, 23050, 4100, 4900, 11, 24_600_000, 50, "2d ago"),
            ];
    }

    /// <summary>The sample's answer to "where can I sell this?": three places some way from Erlaza.</summary>
    static TradeRow[] SamplePlaces(SearchRequest request)
    {
        int rank = 0;
        TradeRow Place(string station, string system, double ly, int price, int demand, string updated) => new()
        {
            Rank = ++rank, CommodityId = request.LookItem, BuyStation = request.LookFrom, BuySystem = request.StarSystem ?? "", BuyPrice = request.LookPaid, Stock = request.LookCount,
            SellStation = station, SellSystem = system, SellMarketId = 200 + rank, SellType = "Coriolis", SellPad = "L", SellX = 86.1, SellZ = ly, SellPrice = price, Demand = demand,
            DistanceLy = ly, ProfitPerTon = price - request.LookPaid, Units = Math.Min(request.LookCount, demand),
            ProfitPerTrip = (long)(price - request.LookPaid) * Math.Min(request.LookCount, demand), JumpsOut = ShipMath.JumpsFor(ly, request.Jump), Updated = updated,
        };
        return
        [
            Place("Wendy Wharf", "Crucis Sector AQ-Y c25", 83.4, 49020, 33838, "11h ago"),
            Place("Rune Memorial", "Crucis Sector AQ-Y c24", 9, 48916, 34280, "6h ago"),
            Place("Aristotle Gateway", "Tjakiri", 21.7, 48105, 310, "2h ago"),
        ];
    }

    /// <summary>The sample's Studio Nav route: Sol to Colonia by two neutron stars.</summary>
    static NavRoute SampleRoute() => new(
        new PlotRequest { Kind = PlotRequest.Neutron, From = "Sol", To = "Colonia", Range = 60, Efficiency = 60, Supercharge = 4 },
        "sample", new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc),
        [
            new("Sol", 0, 0, 0, 0, 0, 22000.5, 0, false, false, 0),
            new("PSR J1752-2806", 0, -11, -6.8, 407.3, 407.5, 21620.3, 9, true, false, 0),
            new("Col 359 Sector NN-T e3-3", 0, -90.2, -12.5, 935.6, 534.4, 21100.8, 9, true, false, 0),
            new("Colonia", 0, -9530.5, -910.3, 19808.1, 21100.8, 0, 240, false, false, 0),
        ]);

    /// <summary>The sample's trade search: answers at once, from the made-up lists.</summary>
    sealed class SampleSearch : ITradeSearch
    {
        public Func<SearchRequest, TradeRow[]> Finds { get; set; } = _ => [];

        public IReadOnlyList<(string Id, string Name)> Commodities => [];

        public Task<SearchResult> Run(SearchRequest request, CancellationToken cancel) =>
            Task.FromResult(new SearchResult { StarSystem = request.StarSystem, Trades = Finds(request), Exports = 1, Lookup = request.Mode == "lookup" });
    }
}
