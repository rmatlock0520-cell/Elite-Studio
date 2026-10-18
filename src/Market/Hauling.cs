using System.Text.Json;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Market;

/// <summary>
/// The hauling side of Studio Market, with no window of its own: the search settings and presets,
/// when a search runs, the trade list, the accepted trade and where to fly next, and the "where can
/// I sell this?" lookups. The overlay and the Market page both show what this holds and call its
/// operations; neither keeps hauling state of its own. Everything runs on the UI thread.
///
/// What to show. <see cref="Changed"/> means "draw again". <see cref="Shown"/> is the one trade on
/// screen and <see cref="NextStop"/> where to fly for it; when there is none, <see cref="Message"/>
/// says why. <see cref="Note"/> is a short line that comes and goes. <see cref="Accepted"/>,
/// <see cref="Leg"/> and <see cref="ReturnLoaded"/> say how far along the accepted trade is. The
/// Market page lists <see cref="Trades"/> (or <see cref="Lookup.Rows"/> while <see cref="Look"/> is
/// set) under <see cref="ListMessage"/>; <see cref="Searching"/> tells it that an answer is on its
/// way. Rows are shown as the search made them: it numbers them and puts their names and their
/// age into words. Two things are written over here: a lookup's places carry the lookup's own
/// name for the commodity, and a trade brought back after a restart is as much older as Studio
/// was closed.
///
/// What the player does. Next trade: <see cref="NextTrade"/>; a row picked in a list: <see cref="Show"/>.
/// Accept or drop: <see cref="ToggleAccept"/>, or <see cref="Accept"/> and <see cref="Drop"/>. Copy the
/// next star system: <see cref="CopyText"/>, then <see cref="Copied"/>. Sell my cargo:
/// <see cref="SellMyCargo"/>; a commodity from <see cref="CommodityChoices"/>: <see cref="WhereToSell"/>;
/// back: <see cref="BackToTrades"/>. Search again: <see cref="SearchAgain"/>. Buying and selling in
/// the game accept a trade and move it along by themselves; what the journal holds from before
/// Studio started never does. Rows are handed back as they were given out: a copy of a row (from a
/// web page) counts as that row. Two notes name a key, so the operations that can raise them take
/// what the overlay calls that key.
///
/// Settings. Read <see cref="Settings"/>; change them only through <see cref="ChangeSettings"/>,
/// <see cref="StepSetting"/>, <see cref="SortListBy"/>, <see cref="StepListSort"/> and the preset
/// operations (<see cref="UsePreset"/>, <see cref="SavePreset"/>, <see cref="RemovePreset"/>), which
/// save and start a new search when one is needed. <see cref="Pad"/>, <see cref="CargoTons"/>,
/// <see cref="JumpFull"/> and <see cref="JumpEmpty"/> are what the search really uses once the ship
/// has had its say; <see cref="ShipPad"/> and <see cref="ShipRanges"/> are the ship's own say.
/// <see cref="CopiedFrom"/> tells a screen that the settings were just copied from the Elite Haul
/// Overlay, until <see cref="CopyNoteRead"/>.
///
/// The overlay menu's lines come from <see cref="SortedTrades"/>, <see cref="SettingLines"/> and
/// <see cref="PresetLines"/>; <see cref="HaulMenu"/> has its fixed parts. While a lookup is on screen
/// the menu stays shut: ask <see cref="BlockedByLookup"/> first.
///
/// Time. The app calls <see cref="Tick"/> about twice a second; nothing here has a timer of its own.
///
/// A restart of Studio. The accepted trade and an open lookup are saved with the settings
/// (<see cref="HaulingKept"/>) and come back the first time the game has said where the player is,
/// as far as they are still true.
///
/// This file holds the settings in use, the searching, the accepted trade and what the game's
/// journal does to them; HaulingScreen.cs holds what is on screen, the lookups, what a restart
/// keeps, the menu's lines and the presets.
/// </summary>
public sealed partial class Hauling
{
    static readonly TimeSpan SettingsSettle = TimeSpan.FromMilliseconds(700);   // a search starts this long after the last change to a search setting
    static readonly TimeSpan AnswerKept = TimeSpan.FromMinutes(5);              // the same question asked again this soon gets the last answer
    static readonly TimeSpan HoldSettles = TimeSpan.FromSeconds(2);             // the game takes a moment to write the hold's contents after a sale

    /// <summary>A purchase, a sale or a docking read from the journal, waiting to be looked at.</summary>
    readonly record struct Mark(string Kind, long Market, string Item);

    readonly GameState game;
    readonly Settings settings;
    readonly ITradeSearch search;
    readonly Func<DateTime> now;
    readonly List<Mark> marks = [];

    List<TradeRow> trades = [];
    string? system;                 // where the journal last put the player: tells a move from a line that only repeats it
    string? searchedFor;            // the system the newest search was started for
    string? message;                // why there is no trade to show
    string listMessage = "";
    bool started;                   // a search has been asked for with the player's system known; until then Tick keeps asking
    bool searchWanted;              // a journal line since the last look said the player is somewhere new
    (bool Waits, bool Force, bool ByHand) queued;   // a search asked for while another was running
    SearchRequest? lastKey;         // what the newest answer was an answer to
    DateTime? searchDue;            // when the search after a changed setting starts
    DateTime? checkAccepted;        // when to look whether the accepted trade's cargo is gone, after a sale somewhere else
    (string System, string Station) lastDock = ("", "");   // the station the player is docked at, or was last
    StationMarket? dockMarket;      // its price list as the game showed it; null until its commodities screen was opened

    /// <param name="now">The clock; tests pass their own.</param>
    /// <param name="oldSettingsFile">
    /// The Elite Haul Overlay's settings file, copied from on the very first run. Left out: where that
    /// tool keeps it. An empty name: there is none (a practice copy). Tests pass their own.
    /// </param>
    public Hauling(GameState game, Settings settings, ITradeSearch search, Func<DateTime>? now = null, string? oldSettingsFile = null)
    {
        this.game = game;
        this.settings = settings;
        this.search = search;
        this.now = now ?? (() => DateTime.Now);
        Settings = MarketSettings.Load(settings, oldSettingsFile ?? OldHaulSettings.DefaultFile);
        system = game.StarSystem;
        NoteDock();
        game.Journal += OnJournal;
        game.Changed += OnGameChanged;
    }

    /// <summary>Raised after anything a screen shows has changed.</summary>
    public event Action? Changed;

    // Listeners are called one by one, not as one event call, which would stop at the first that
    // throws: a screen that fails to draw must not keep the other screen from hearing, nor leave
    // a search or a lookup half started.
    void RaiseChanged()
    {
        Keep();
        foreach (Action screen in Changed?.GetInvocationList() ?? [])
        {
            try { screen(); }
            catch (Exception problem) { Log.Error($"A listener in {screen.Method.DeclaringType?.FullName} failed on a change in hauling", problem); }
        }
    }

    // ---------------------------------------------------------------- settings

    /// <summary>The settings in use. A changed copy takes their place; this one never changes.</summary>
    public MarketSettings Settings { get; private set; }

    /// <summary>The smallest landing pad searched for: 1 small, 2 medium, 3 large. From the ship unless the player set one.</summary>
    public int Pad => StationFilter.PadSize(Settings.Pad) ?? ShipPad;

    /// <summary>The smallest landing pad the ship being flown fits on: what the "auto" pad setting stands for.</summary>
    public int ShipPad => ShipMath.ShipPad(game.Ship);

    /// <summary>Tons the hold takes: the player's own number when they typed one, else the ship's.</summary>
    public int CargoTons => Settings.CargoOverride > 0 ? Settings.CargoOverride : game.CargoCapacity;

    /// <summary>
    /// The player typed both jump ranges the wrong way round. A full hold never jumps farther than
    /// an empty one, so the smaller number is taken as the full-hold range. The saved numbers stay as typed.
    /// </summary>
    public bool JumpRangesSwapped => Settings.JumpEmpty > 0 && Settings.JumpRange > Settings.JumpEmpty;

    /// <summary>
    /// Light years per jump with a full hold: the typed number, else worked out from the ship; 0 when not known.
    /// (Studio Market says "full" where the game state says "laden": the Elite Haul Overlay's word, kept with its settings.)
    /// </summary>
    public double JumpFull => Typed.Full > 0 ? Typed.Full : ShipRanges.Full;

    /// <summary>Light years per jump with an empty hold; never less than <see cref="JumpFull"/>.</summary>
    public double JumpEmpty => Math.Max(Typed.Empty > 0 ? Typed.Empty : ShipRanges.Empty, JumpFull);

    (double Empty, double Full) Typed => JumpRangesSwapped ? (Settings.JumpRange, Settings.JumpEmpty) : (Settings.JumpEmpty, Settings.JumpRange);

    /// <summary>
    /// What the ship itself works out to, in light years per jump, with <see cref="CargoTons"/> in a
    /// full hold: what typed jump ranges are measured against. 0 and 0 while the ship is not known.
    /// (Worked out here, not taken from the game state, because a typed cargo capacity changes the full-hold range.)
    /// </summary>
    public (double Empty, double Full) ShipRanges =>
        ShipMath.JumpRanges(game.MaxJumpRange, game.UnladenMass, game.FuelCapacity, game.FsdFuel, game.BoosterLy, CargoTons);

    /// <summary>
    /// The Elite Haul Overlay's settings file, from Studio's very first run (which copied its search
    /// settings from there) until the player has read the note that says so; null otherwise. It is
    /// kept with the saved settings, so it is still there after a restart.
    /// </summary>
    public string? CopiedFrom => Settings.CopiedFrom;

    /// <summary>The player has read the note about the copied settings: <see cref="CopiedFrom"/> is forgotten.</summary>
    public void CopyNoteRead() => Save(Settings with { CopiedFrom = null });

    /// <summary>
    /// The one way to change settings: the named ones ("maxLy", "listSort", ...) take the values
    /// given, the result is saved, and when it changes what a search would find, a new search
    /// starts once the player has left the settings alone for a moment. That search also runs while
    /// a trade is accepted. Numbers must come as numbers. False, with nothing changed, when a
    /// value does not fit its setting.
    /// </summary>
    public bool ChangeSettings(IReadOnlyDictionary<string, JsonElement> changes)
    {
        if (Settings.With(changes) is not { } changed) return false;
        Save(changed);
        return true;
    }

    void Save(MarketSettings changed)
    {
        if (changed == Settings) return;
        SearchRequest before = Key(Request());
        Settings = changed;
        try { settings.Set(MarketSettings.Section, changed); }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error("The market settings could not be saved; they hold until Studio closes", problem);
        }
        // A changed search setting is a search the player asked for, once they have stopped changing things.
        if (Key(Request()) != before) searchDue = now() + SettingsSettle;
        RaiseChanged();
    }

    // ---------------------------------------------------------------- searching

    /// <summary>The trades of the newest answer, best first. Empty while the first search runs and after a search for something else has started.</summary>
    public IReadOnlyList<TradeRow> Trades => trades;

    /// <summary>What to show in place of a trade when <see cref="Shown"/> is null.</summary>
    public string Message => Look?.Message ?? message ?? "Waiting for Elite…";

    /// <summary>
    /// The line that goes with the list on the Market page: how many trades from where, or why
    /// there are none. While the list is empty because a search for something else is running,
    /// it says that.
    /// </summary>
    public string ListMessage => Look?.Message ?? listMessage;

    /// <summary>The list on show came from a one-way search, so it has nothing to say about the way back.</summary>
    public bool ListOneWay { get; private set; }

    /// <summary>A trade search is running.</summary>
    public bool Searching { get; private set; }

    /// <summary>When the newest search ended, for "last search 14:05"; null before the first has.</summary>
    public DateTime? LastSearchAt { get; private set; }

    /// <summary>What the newest search brought, whether or not it was put on screen: the same question asked again soon gets it.</summary>
    internal SearchResult? LastResult { get; private set; }

    /// <summary>The "Search again" button: asks the data service afresh, also while a trade is accepted.</summary>
    public void SearchAgain() => StartSearch(force: true, byHand: true);

    SearchRequest Request()
    {
        var request = new SearchRequest
        {
            Mode = Settings.StartMode, StarSystem = game.StarSystem, Pad = Pad, BuyLy = Settings.BuyLy, MaxLy = Settings.MaxLy,
            MaxAgeHours = Settings.MaxAgeHours, MaxStarLs = Settings.MaxStarLs, MinSupply = Settings.MinSupply, MinDemand = Settings.MinDemand,
            Carriers = Settings.Carriers, Surface = Settings.Surface, Cargo = CargoTons,
            SrcPower = Settings.SrcPower, DstPower = Settings.DstPower, SrcState = Settings.SrcState, DstState = Settings.DstState,
            RankBy = Settings.RankBy, Returns = Settings.Returns, Jump = JumpFull, JumpEmpty = JumpEmpty,
        };
        if (!CarryMode) return request;

        // From a station to a system: both named, and one way. Where the station is, is known while the player is in its
        // system; its price list as the game showed it, once they have opened its commodities screen while docked there.
        (string from, string station) = CarryFrom;
        double[]? here = Same(from, game.StarSystem) ? game.StarPos : null;
        return request with
        {
            Returns = false, FromSystem = from, FromStation = station, ToSystem = Settings.ToSystem,
            FromX = here?[0] ?? 0, FromY = here?[1] ?? 0, FromZ = here?[2] ?? 0,
            LiveMarket = dockMarket is { } shown && Same(shown.StationName, station) && Same(shown.StarSystem, from) ? shown : null,
        };
    }

    bool CarryMode => Settings.StartMode == "carry";

    /// <summary>
    /// The station a "from a station to a system" search buys at, with its system: the one the
    /// player named, else the one they are docked at, or were docked at last (the list must not
    /// empty itself at take-off). Two empty names when neither is known.
    /// </summary>
    public (string System, string Station) CarryFrom =>
        Settings.FromSystem.Length > 0 && Settings.FromStation.Length > 0 ? (Settings.FromSystem, Settings.FromStation) : lastDock;

    /// <summary>The stations of a system that sell something, by name, for the page's list of stations to buy at.</summary>
    public Task<IReadOnlyList<string>> Stations(string system) => search.Stations(system, CancellationToken.None);

    // Where the player is docked, and that station's price list once the game has shown it. Both are kept after take-off:
    // what was bought there is still what is being carried.
    void NoteDock()
    {
        if (!game.Docked || string.IsNullOrEmpty(game.Station) || string.IsNullOrEmpty(game.StarSystem)) return;
        if (!Same(lastDock.Station, game.Station) || !Same(lastDock.System, game.StarSystem)) dockMarket = null;
        lastDock = (game.StarSystem, game.Station);
        dockMarket = game.Market ?? dockMarket;
    }

    /// <summary>
    /// What decides a search's answer: the request itself. "Anywhere" answers do not depend on where
    /// the player is, so there the system and the buy distance are left out and a jump asks nothing new.
    /// </summary>
    static SearchRequest Key(SearchRequest request) => request.Mode == "anywhere" ? request with { StarSystem = "*", BuyLy = 0 } : request;

    /// <param name="force">Ask the data service even when the last answer is fresh.</param>
    /// <param name="byHand">The player asked for it (a button, a changed setting, a dropped trade), so it runs even while a trade is accepted.</param>
    void StartSearch(bool force, bool byHand = false)
    {
        if (string.IsNullOrEmpty(game.StarSystem))
        {
            // Tick asks again until the game has said where the player is; a screen is told only the first time.
            const string waiting = "Waiting for Elite… (start the game, or jump to a system)";
            if (message != waiting)
            {
                message = waiting;
                RaiseChanged();
            }
            return;
        }
        // The first time the player's system is known, what Studio was last closed with comes back. A trade
        // brought back keeps the overlay like any accepted trade, but its list has yet to be made: this once the search runs.
        if (!started)
        {
            started = true;
            byHand |= Restore();
        }
        // An accepted trade stays as it is: only a search the player asks for runs, and even that leaves the overlay on it.
        if (Accepted is not null && !byHand)
        {
            RefreshRows();
            RaiseChanged();
            return;
        }
        if (Searching)
        {
            // One at a time: this one runs when the other has ended. (The old tool forgot here that it was forced or asked for by hand.)
            queued = (true, queued.Force || force, queued.ByHand || byHand);
            // What led here (a trade finished, an arrival) goes on screen now, not when the other search ends.
            RefreshRows();
            RaiseChanged();
            return;
        }

        searchedFor = game.StarSystem;
        SearchRequest request = Request(), key = Key(request);
        if (!force && key == lastKey && LastResult is not null && now() - LastSearchAt < AnswerKept)
        {
            Apply(LastResult);
            return;
        }
        message = CarryMode && request.ToSystem.Length > 0 ? $"Searching what to carry to {request.ToSystem}…" : $"Searching trades from {game.StarSystem}…";
        // The answer on its way is to another question, so the list on show is not its list: it goes, and its line says
        // why nothing is listed. The same question asked again keeps its list and its line on screen meanwhile.
        if (key != lastKey) (trades, listMessage) = ([], message);
        Searching = true;
        RaiseChanged();
        _ = Run(request, key);
    }

    async Task Run(SearchRequest request, SearchRequest key)
    {
        SearchResult result;
        try { result = await search.Run(request, CancellationToken.None); }
        catch (Exception problem)
        {
            result = new SearchResult { Ok = false, Error = problem.GetBaseException().Message, StarSystem = request.StarSystem };
        }

        try
        {
            Searching = false;
            (LastSearchAt, lastKey, LastResult) = (now(), key, result);
            Log.Info($"Search {result.StarSystem}: ok={result.Ok} trades={result.Trades.Count} {result.Error}");
            // An answer for a system the player has already left is dropped. With a trade accepted the
            // list is only for reading, so then it is filled in wherever the player has flown meanwhile.
            if (Same(result.StarSystem, game.StarSystem) || Accepted is not null) Apply(result);
            else RaiseChanged();
            if (queued.Waits)
            {
                (_, bool force, bool byHand) = queued;
                queued = default;
                StartSearch(force, byHand);
            }
        }
        catch (Exception problem)
        {
            Log.Error("A search answer could not be taken in", problem);
        }
    }

    void Apply(SearchResult result)
    {
        trades = [.. result.Trades];
        RefreshRows();
        ListOneWay = trades.Count > 0 ? trades[0].OneWay : !Settings.Returns || CarryMode;
        Index = 0;
        if (!result.Ok)
        {
            // a Powerplay problem explains itself; anything else is the data service not answering
            message = result.Error?.StartsWith("Powerplay", StringComparison.OrdinalIgnoreCase) == true ? result.Error : $"Couldn't reach the market data service: {result.Error}";
            Log.Error($"Search failed for {result.StarSystem}: {result.Error}");
        }
        else if (trades.Count > 0) message = null;
        else if (result.Empty is { } why) message = why;
        else if (result.Exports == 0) message = $"No market data for stations in {result.StarSystem} that fit your filters. Try older prices or another pad size.";
        else message = $"No profitable trades within {Settings.MaxLy} ly of {result.StarSystem}. Try a wider distance or older prices.";

        listMessage = (trades.Count > 0 ? result.Summary ?? $"{trades.Count} trades from {result.StarSystem}" : message)
            + (string.IsNullOrEmpty(result.Note) ? "" : $"   ·   {result.Note}")
            + (Accepted is null ? "" : "   ·   the overlay stays on your accepted trade until you drop it");
        RaiseChanged();
    }

    /// <summary>
    /// Keeps "docked here", the distance to each place to buy and the jumps to get there (empty
    /// hold) right as the player moves, for the listed trades and the accepted one.
    /// </summary>
    void RefreshRows()
    {
        double range = JumpEmpty;
        foreach (TradeRow row in trades.Append(Accepted).OfType<TradeRow>())
        {
            row.BuyHere = game.Docked && !string.IsNullOrEmpty(game.Station) && Same(row.BuyStation, game.Station);
            // A place at 0, 0, 0 counts as "position not known", as in the old tool; its distance stays as the search gave it.
            if (game.StarPos is { } here && (row.BuyX != 0 || row.BuyY != 0 || row.BuyZ != 0))
                row.BuyDistanceLy = Math.Round(ShipMath.Distance(here, [row.BuyX, row.BuyY, row.BuyZ]), 1);
            row.JumpsToBuy = ShipMath.JumpsFor(row.BuyDistanceLy, range);
        }
    }

    // ---------------------------------------------------------------- the accepted trade

    /// <summary>
    /// The trade the player has taken on, or null. The overlay stays on it wherever they fly:
    /// automatic searches wait and the next-trade key leaves it alone. It keeps the prices it had
    /// when it was accepted.
    /// </summary>
    public TradeRow? Accepted { get; private set; }

    /// <summary>How far along the accepted trade is: "buy", "sell" or "back"; "" with none accepted.</summary>
    public string Leg { get; private set; } = "";

    /// <summary>On the way back: the load to bring back has been bought.</summary>
    public bool ReturnLoaded { get; private set; }

    /// <summary>
    /// Accepts one of <see cref="Trades"/>; with another trade accepted it switches to this one
    /// without a new search. False when it cannot be: a lookup is on screen, or the trade is not listed.
    /// </summary>
    public bool Accept(TradeRow trade)
    {
        if (Look is not null) return false;
        if (trade.IsSameTrade(Accepted)) return true;
        int at = Find(trades, trade);
        if (at < 0) return false;
        TakeOn(trades[at]);
        RaiseChanged();
        return true;
    }

    /// <summary>Drops the accepted trade and searches again from where the player is.</summary>
    public void Drop()
    {
        if (Accepted is null) return;
        Log.Info($"Dropped: {Accepted.Commodity} to {Accepted.SellStation}. Searching again.");
        Release();
        StartSearch(force: true, byHand: true);
        RaiseChanged();
    }

    /// <summary>The accept key: accepts the trade on screen, or drops the accepted one.</summary>
    /// <param name="cargoKey">What the overlay's "sell my cargo" key is called ("F6"), for the note shown while a lookup is on screen.</param>
    public void ToggleAccept(string cargoKey)
    {
        if (BlockedByLookup(cargoKey)) return;
        if (Accepted is not null) Drop();
        else if (Shown is { } shown) Accept(shown);
    }

    void TakeOn(TradeRow trade)
    {
        (Accepted, acceptedAt) = (trade, now());
        checkAccepted = null;
        ReturnLoaded = false;
        Leg = Carrying(trade.CommodityId) ? "sell" : "buy";   // already carrying it? then the next stop is the place to sell
        Log.Info($"Accepted: {trade.Commodity}, {trade.BuyStation} to {trade.SellStation} (next: {Leg})");
    }

    void Release()
    {
        Accepted = null;
        Leg = "";
        checkAccepted = null;
        ReturnLoaded = false;
        Index = 0;
    }

    bool Carrying(string commodity) => game.Cargo.Any(item => Same(item.Id, commodity));

    // ---------------------------------------------------------------- the game

    /// <summary>
    /// Journal lines as they are read. Lines from before Studio started only say where the player
    /// is; what happens now is noted and acted on in <see cref="OnGameChanged"/>, once every line of
    /// this reading has been taken in and the hold's contents are fresh.
    /// </summary>
    void OnJournal(JournalLine line)
    {
        switch (line.Event)
        {
            case "Location" or "CarrierJump" or "FSDJump" or "Docked":
                bool moved = !Same(game.StarSystem, system);
                system = game.StarSystem;
                if (line.Replay) break;
                if (line.Event == "Docked") marks.Add(new("dock", line.Data.Whole("MarketID"), ""));
                // In the middle of a plotted route (the game said more jumps follow this one) the list is kept, with its
                // distances brought up to date. A new search waits for the end of the route, or for the player to dock.
                if (moved && !(line.Event == "FSDJump" && game.RouteLeft >= 1)) searchWanted = true;
                break;
            case "MarketBuy" or "MarketSell" when !line.Replay:
                marks.Add(new(line.Event == "MarketBuy" ? "buy" : "sell", line.Data.Whole("MarketID"), line.Data.Text("Type").ToLowerInvariant()));
                break;
        }
    }

    void OnGameChanged()
    {
        NoteDock();
        bool search = TakeMarks() || searchWanted;   // a finished trade: look for the next one from here
        searchWanted = false;
        // Docked somewhere the list was not made for, as after arriving in the middle of a plotted route.
        bool unsearched = game.Docked && !string.IsNullOrEmpty(game.StarSystem) && !Same(searchedFor, game.StarSystem) && Settings.StartMode != "anywhere";
        // From a station to a system: the list is for the station the player is docked at, and for its prices as the game
        // shows them. Another station in the same system, or its commodities screen just read, is another question.
        bool elsewhere = CarryMode && started && !Searching && lastKey is not null && Request() is { } asked
            && (!Same(asked.FromStation, lastKey.FromStation) || !Same(asked.FromSystem, lastKey.FromSystem) || !ReferenceEquals(asked.LiveMarket, lastKey.LiveMarket));
        if (search || unsearched || elsewhere) StartSearch(force: false);
        else
        {
            RefreshRows();
            RaiseChanged();
        }
    }

    /// <summary>
    /// Buying, selling and docking say which trade the player is doing and how far along they are.
    /// True when the accepted trade was finished.
    /// </summary>
    bool TakeMarks()
    {
        bool finished = false;
        foreach (Mark mark in marks)
        {
            switch (mark.Kind)
            {
                case "buy" when Accepted is { } trade:
                    if (Leg == "buy" && mark.Market == trade.BuyMarketId && Fits(mark.Item, trade.CommodityId)) Leg = "sell";
                    else if (Leg == "back" && mark.Item.Length > 0 && Same(mark.Item, trade.ReturnId)) ReturnLoaded = true;
                    break;
                case "buy" when Look is null && mark.Market != 0:
                    // Nothing accepted yet: buying what a listed trade says, where it says, accepts that trade.
                    // The trade on screen comes first, then the best listed one.
                    if (trades.Prepend(Shown).FirstOrDefault(listed => listed is not null && listed.BuyMarketId == mark.Market && Fits(mark.Item, listed.CommodityId)) is { } bought)
                    {
                        TakeOn(bought);
                        Leg = "sell";
                    }
                    break;
                case "sell":
                    if (Look is { Source: "cargo" }) checkLookup = now() + HoldSettles;
                    if (Accepted is { } sold && Leg != "back" && Fits(mark.Item, sold.CommodityId))
                    {
                        // sold somewhere else: look in a moment whether the load is gone
                        if (mark.Market != sold.SellMarketId) checkAccepted = now() + HoldSettles;
                        else if (sold.ReturnProfit > 0) Leg = "back";
                        else
                        {
                            Log.Info("Accepted trade finished");
                            Release();
                            finished = true;
                        }
                    }
                    break;
                case "dock" when Accepted is { } home && Leg == "back" && mark.Market == home.BuyMarketId:
                    Log.Info("Accepted trade finished (back at the buy station)");
                    Release();
                    finished = true;
                    break;
            }
        }
        marks.Clear();
        return finished;
    }

    /// <summary>Time-outs, re-checks and searches that are due. The app calls this about twice a second.</summary>
    public void Tick()
    {
        DateTime time = now();
        if (!started) StartSearch(force: false);
        if (searchDue <= time)
        {
            searchDue = null;
            StartSearch(force: false, byHand: true);
        }
        if (noteUntil <= time)
        {
            (note, noteUntil) = (null, null);
            RaiseChanged();
        }
        if (checkAccepted <= time)
        {
            // The accepted trade's cargo was sold at another station: if none of it is left, that trade is over.
            checkAccepted = null;
            if (Accepted is not null && Leg != "back" && !Carrying(Accepted.CommodityId))
            {
                Log.Info("Accepted trade finished (its cargo was sold at another station)");
                Release();
                StartSearch(force: false);
            }
        }
        if (checkLookup <= time)
        {
            // Something was sold during a cargo lookup: if that cargo is gone, go back to the trades.
            checkLookup = null;
            if (Look is { Source: "cargo" } && !Carrying(Look.Id)) BackToTrades();
        }
    }

    // ---------------------------------------------------------------- small helpers

    // Names are compared the way the Haul Overlay did: without regard to capitals.
    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where a row is in a list, counted from 0, or -1 (also for no row at all). A copy of the row
    /// counts as the row: a web page sends back what it was given, not the object itself.
    /// </summary>
    internal static int Find(IReadOnlyList<TradeRow> rows, TradeRow? row)
    {
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].IsSameTrade(row)) return i;
        return -1;
    }

    /// <summary>A purchase or sale is of this commodity; a journal line that does not name one counts for any.</summary>
    static bool Fits(string item, string commodity) => item.Length == 0 || Same(item, commodity);
}
