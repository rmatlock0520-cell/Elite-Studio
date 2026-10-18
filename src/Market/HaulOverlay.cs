using System.Globalization;
using System.Windows;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Nav;
using EliteStudio.Overlay;

namespace EliteStudio.Market;

/// <summary>
/// Hauling inside the in-game overlay: everything the Elite Haul Overlay drew in its box. It stands
/// on <see cref="OverlayHost"/> (the boxes, their settings, their keys) and <see cref="Hauling"/>
/// (all hauling state and every operation), and keeps nothing of its own but what is purely on
/// screen: whether the menu is open, which page and line are lit, whether a value is being changed,
/// and when the menu closes by itself.
///
/// The "haul" box is filled here: the trade on screen, or a lookup; with neither, a line saying
/// where the player is with hauling's message under it. <see cref="NavContent"/> draws the route's
/// next stop and the exploring line as the "nav" box showed them until that box became Studio Nav's
/// own page, with the line of the 3D map's check of the plotted route's stops under them; nothing
/// draws it in the game now but --render-shots (see PLAN.md, "Awaiting sign-off before removal").
/// In the game that line is Studio Nav's page's own (nav/index.html in the box), which follows the
/// check's events and prints the same words (<see cref="HeatmapFinds.LineOf"/>).
///
/// Buttons. A box that takes clicks has a row of buttons under the trade, each tagged "do:" and one
/// of the actions the Elite Haul Overlay had a key for: next, copy, accept, cargo, menu. Next trade,
/// copy, accept and "sell my cargo" go straight to <see cref="Hauling"/>; menu opens and closes the
/// menu that drops down under the trade, whose every line is clicked. Hiding the box closes the
/// menu: nothing may go on answering unseen.
///
/// The actions "back", "menu" and "forward" still step through the menu line by line as the old
/// tool's three menu keys did. Nothing in the game raises them any more; see PLAN.md, "Awaiting
/// sign-off before removal". The drawing itself is in HaulOverlayDrawing.cs.
/// </summary>
public sealed partial class HaulOverlay
{
    const int ClipboardTries = 6;   // the clipboard is sometimes busy for a moment: the Haul Overlay tried this often, 40 ms apart
    static readonly TimeSpan ClipboardBusy = TimeSpan.FromMilliseconds(40);
    static readonly TimeSpan LeftAlone = TimeSpan.FromSeconds(15), LeftAloneWithMouse = TimeSpan.FromSeconds(45);
    // Two of the buttons by the words on them, quoted the way hauling's notes name them ("Back to trades" goes back to your trades first).
    const string DropButton = "Drop this trade", BackButton = "Back to trades", DropNamed = "\"" + DropButton + "\"", BackNamed = "\"" + BackButton + "\"";

    readonly OverlayHost overlay;
    readonly Hauling hauling;
    readonly NavRoutes nav;
    readonly ExploreTrip explore;
    readonly GameState game;
    readonly Func<DateTime> now;

    // The menu. Nothing here outlives the program, and none of it is hauling state.
    bool open;
    string page = HaulMenu.Pages[0].Key;
    int lit = -1;           // the lit line: -1 the page names; on the trades page 0 is the "Order" line and 1 the first trade
    bool editing;           // back and forward change the lit line's value instead of moving
    int top;                // the first of the five trades on show, counted from 0
    bool mouse;             // the menu was opened in a box that takes clicks: its lines have their own buttons
    DateTime closesAt;

    /// <param name="explore">Studio Nav's exploring trip, for its line.</param>
    /// <param name="now">The clock for the menu closing by itself; tests pass their own.</param>
    /// <param name="oldSettingsFile">The Elite Haul Overlay's settings file, copied from on the overlay's very first run; left out, an empty name and tests' own as for <see cref="Hauling"/>.</param>
    /// <param name="routeCheck">The 3D map's check of the stops of the plotted route, for <see cref="NavContent"/>'s line about it; none when left out.</param>
    /// <param name="navScan">The 3D map's Nav scan, for <see cref="NavContent"/>'s line about it; none when left out.</param>
    public HaulOverlay(OverlayHost overlay, Hauling hauling, NavRoutes nav, ExploreTrip explore, GameState game, Func<DateTime>? now = null, string? oldSettingsFile = null,
        HeatmapFinds? routeCheck = null, NavScan? navScan = null)
    {
        (this.overlay, this.hauling, this.nav, this.game, this.explore) = (overlay, hauling, nav, game, explore);
        this.now = now ?? (() => DateTime.Now);
        Clickable = () => overlay.Box(Box)?.TakingClicks == true;
        RouteCheck = () => routeCheck?.RouteCheckNow();
        NavScanNow = () => navScan?.View().Line;
        overlay.CopyOldSettings(oldSettingsFile ?? OldHaulSettings.DefaultFile);

        overlay.KeyPressed += OnKey;
        overlay.Ticked += Tick;
        hauling.Changed += Redraw;
        nav.Changed += Redraw;
        explore.Changed += Redraw;
        // (The nav box is a page, which follows the route's check by itself: nothing here to draw again when it changes.)
        if (overlay.Box(Box) is not { } window) return;   // a run without windows: there is nothing to draw on
        window.Clicked += Click;
        window.PreviewMouseMove += (_, _) => { if (open && mouse) Touch(); };   // a mouse being moved over the menu is a menu in use
        window.SetContent(Content);
    }

    const string Box = "haul", NavBox = "nav";   // the two boxes filled here, by their names in OverlaySettings.Boxes

    /// <summary>Puts text on the Windows clipboard, and throws while another program has it open. Tests put a stand-in here.</summary>
    internal Action<string> ToClipboard { get; init; } = text => Clipboard.SetDataObject(text, true);

    /// <summary>The wait between two tries at a busy clipboard. Tests replace it so they need not really wait.</summary>
    internal Func<TimeSpan, Task> Wait { get; init; } = Task.Delay;

    /// <summary>
    /// The hauling box takes clicks just now: it is shown in the game. It then has its row of buttons,
    /// and a menu opened in it has a button on every line. Never in a run without windows. Tests put
    /// a stand-in here.
    /// </summary>
    internal Func<bool> Clickable { get; init; }

    /// <summary>The 3D map's check of the plotted route as it stands, for the nav box's line; null without one. Tests put a stand-in here.</summary>
    internal Func<RouteCheckView?> RouteCheck { get; init; }

    /// <summary>The 3D map's Nav scan line as it stands (<see cref="NavScan.LineOf"/>), for the nav box; null while it is off. Tests put a stand-in here.</summary>
    internal Func<NavScanLine?> NavScanNow { get; init; }

    /// <summary>The menu as it stands, for tests: is it open, on which page, which line is lit, is its value being changed, and the first trade on show.</summary>
    internal (bool Open, string Page, int Lit, bool Editing, int Top) Menu => (open, page, lit, editing, top);

    /// <summary>
    /// The Studio Nav stop the nav box names, or null: a route is being followed that has a stop still
    /// to reach, or something to do at the stop the ship is in (the last purchase of a shopping trip).
    /// </summary>
    internal NavProgress? RouteShown => nav.Progress is { } progress && (progress.Next is not null || progress.HereNote.Length > 0) ? progress : null;

    // ---------------------------------------------------------------- buttons

    /// <summary>
    /// One action, answered as the Haul Overlay answered the key it had for it. Hiding the hauling
    /// box arrives here too, as the box's own name: the box is hidden or shown already.
    /// </summary>
    void OnKey(string key)
    {
        switch (key)
        {
            case Box:
                // Hidden: the menu goes with it. (The Haul Overlay left the menu open behind the hidden box, where the
                // next menu key could accept a trade unseen.)
                if (!overlay.Settings.HaulOn) Close();
                break;
            case "copy":
                _ = Copy();
                break;
            case "next":
                if (!hauling.NextTrade(DropNamed)) break;
                if (open && page == "trades" && lit >= 1)
                {
                    lit = ShownLine();   // the lit line of the menu's list follows
                    Touch();
                    Redraw();
                }
                break;
            case "accept":
                hauling.ToggleAccept(BackNamed);
                break;
            case "cargo":
                hauling.SellMyCargo();
                break;
            case "back" or "menu" or "forward":
                MenuKey(key);
                break;
        }
    }

    /// <summary>
    /// The copy button: the star system the hauling box names goes on the clipboard, ready to paste
    /// into the galaxy map, and a note says so. A busy clipboard is tried a few times; when it stays busy that
    /// is logged and no note appears. Ends when the text is on the clipboard or has been given up.
    /// </summary>
    internal async Task Copy()
    {
        if (hauling.CopyText is not { } text) return;
        Exception? busy = null;
        for (int attempt = 1; attempt <= ClipboardTries; attempt++)
        {
            if (attempt > 1) await Wait(ClipboardBusy);
            try
            {
                ToClipboard(text);
                busy = null;
                break;
            }
            catch (Exception problem)
            {
                busy = problem;
            }
        }
        if (busy is null) hauling.Copied(text);
        else Log.Error($"Could not copy to the clipboard: {text}", busy);
    }

    /// <summary>
    /// Draws the box again. A drawing that fails is logged here and goes no further: whatever led
    /// to it (a click, a search answer, a route being plotted) must be able to finish.
    /// </summary>
    void Redraw()
    {
        try { overlay.Box(Box)?.Redraw(); }
        catch (Exception problem) { Log.Error("The hauling overlay could not be drawn", problem); }
    }

    // ---------------------------------------------------------------- the menu

    /// <summary>Lines the open page has under its page names.</summary>
    int Lines => page switch
    {
        "trades" => 1 + hauling.Trades.Count,
        "settings" => hauling.SettingLines.Count,
        _ => hauling.PresetLines.Count,
    };

    /// <summary>One of the three menu keys: "back", "menu" (open, select) or "forward".</summary>
    void MenuKey(string key)
    {
        // While a lookup is on screen the menu stays as it is; the note says how to get back to the trades.
        if (hauling.BlockedByLookup(BackNamed)) return;
        if (!open)
        {
            // The menu key opens on the page names of the page used last; back and forward open the trade list on the trade on screen.
            if (key == "menu") Open(page, -1);
            else Open("trades", ShownLine());
        }
        else
        {
            Touch();
            int direction = key == "back" ? -1 : 1;
            if (key == "menu") Select();
            else if (editing) Change(direction);
            else Move(direction);
        }
        if (!open) return;   // closing has drawn the box already
        Settle();
        Redraw();
    }

    /// <summary>
    /// A click on the hauling box. Every piece that can be clicked carries a tag saying what a click
    /// on it does: "do:" and an action for the row of buttons, anything else for a piece of the menu.
    /// </summary>
    internal void Click(string tag)
    {
        if (tag.StartsWith("do:", StringComparison.Ordinal))
        {
            string action = tag["do:".Length..];
            // The menu button opens the trade list on the trade on screen, as the old "back" key did, and closes it again.
            if (action != "menu") OnKey(action);
            else if (open) Close();
            else OnKey("back");
            return;
        }
        if (!open) return;
        if (tag == "close")
        {
            Close();
            return;
        }
        // While a lookup is on screen a click gets the answer a menu key gets: the note that says how to get back to the
        // trades. (The Haul Overlay let clicks through; here a trade cannot be accepted during a lookup, so "accept" would
        // close the menu and accept nothing.)
        if (hauling.BlockedByLookup(BackNamed)) return;
        Touch();
        string[] parts = tag.Split(':', 2);
        string what = parts.Length > 1 ? parts[1] : "";
        int line = int.TryParse(what, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : 0;
        switch (parts[0])
        {
            case "tab":
                (page, lit, editing, top) = (what, -1, false, 0);
                break;
            case "sort":
                // A column title: the list is put in that order, and the lit line stays on the trade on screen.
                hauling.SortListBy(what);
                (top, editing) = (0, false);
                if (lit > 0) lit = ShownLine();
                break;
            case "row":
                (lit, editing) = (line, false);
                if (page == "trades")
                {
                    if (lit == 0) editing = true;
                    else Preview();
                }
                else if (page == "settings") editing = hauling.SettingLines.ElementAtOrDefault(lit)?.Usable == true;
                else if (hauling.PresetLines.ElementAtOrDefault(lit) is { } preset) hauling.UsePreset(preset.Name);
                break;
            case "dec" or "inc":
                // The arrows beside a setting. As in the Haul Overlay the setting stays "being changed", so the keys go on stepping it.
                (lit, editing) = (line, true);
                Change(parts[0] == "dec" ? -1 : 1);
                break;
            case "pick":
                (lit, editing) = (line, false);
                Select();
                break;
            default:
                return;
        }
        if (!open) return;
        Settle();
        Redraw();
    }

    /// <summary>Time passing, four times a second. Left alone the menu closes by itself.</summary>
    internal void Tick()
    {
        if (open && now() >= closesAt) Close();
    }

    void Open(string onPage, int onLine)
    {
        (open, page, lit, editing, top, mouse) = (true, onPage, onLine, false, 0, Clickable());
        Touch();
    }

    void Close()
    {
        if (!open) return;
        (open, editing, lit, mouse) = (false, false, -1, false);
        Redraw();
    }

    /// <summary>The menu was used: it stays open for a while from now.</summary>
    void Touch() => closesAt = now() + (mouse ? LeftAloneWithMouse : LeftAlone);

    /// <summary>The lit line moves up or down and stops at both ends.</summary>
    void Move(int direction)
    {
        lit = Math.Clamp(lit + direction, -1, Lines - 1);
        Preview();
    }

    /// <summary>The box follows the lit trade, unless a trade is accepted: that one stays on screen.</summary>
    void Preview()
    {
        if (page == "trades" && lit >= 1 && hauling.SortedTrades.ElementAtOrDefault(lit - 1) is { } trade) hauling.Show(trade);
    }

    /// <summary>Back or forward while something is being changed: the order of the list, or the setting on the lit line.</summary>
    void Change(int direction)
    {
        if (page == "trades" && lit == 0)
        {
            hauling.StepListSort(direction);
            top = 0;
        }
        else if (page == "settings" && hauling.SettingLines.ElementAtOrDefault(lit) is { } setting) hauling.StepSetting(setting.Key, direction);
    }

    void Select()
    {
        if (lit < 0)
        {
            // On the page names: the next page. After the last one the menu closes, and opens on the first page next time.
            int next = HaulMenu.Pages.ToList().FindIndex(name => name.Key == page) + 1;
            if (next < HaulMenu.Pages.Count) (page, top) = (HaulMenu.Pages[next].Key, 0);
            else
            {
                page = HaulMenu.Pages[0].Key;
                Close();
            }
        }
        else if (page == "trades")
        {
            if (lit == 0) editing = !editing;
            else if (hauling.SortedTrades.ElementAtOrDefault(lit - 1) is { } trade)
            {
                hauling.Accept(trade);   // the accepted trade itself stays as it is; another one takes its place
                Close();
            }
        }
        else if (page == "settings")
        {
            if (hauling.SettingLines.ElementAtOrDefault(lit)?.Usable == true) editing = !editing;
        }
        else if (hauling.PresetLines.ElementAtOrDefault(lit) is { } preset) hauling.UsePreset(preset.Name);   // the menu stays open
    }

    /// <summary>The line of the trade on screen in the list's own order (line 0 is the "Order" line); the first trade when it is not listed.</summary>
    int ShownLine() => Math.Max(0, Hauling.Find(hauling.SortedTrades, hauling.Shown)) + 1;

    /// <summary>
    /// Keeps the lit line inside a list that may have got shorter (a new search answer, a deleted
    /// preset), and the five trades on show round the lit one.
    /// </summary>
    void Settle()
    {
        lit = Math.Min(lit, Lines - 1);
        if (page == "trades") top = HaulMenu.WindowTop(top, lit - 1, hauling.Trades.Count);
    }
}
