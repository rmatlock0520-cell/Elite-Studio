using System.Globalization;
using System.Text.Json;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Market;

/// <summary>Where to fly next for the trade on screen.</summary>
/// <param name="What">BUY, SELL or BACK: the line of the trade this stop belongs to.</param>
/// <param name="Verb">"buy at", "sell at" or "back to", to go before the station's name.</param>
/// <param name="Here">The player is in that star system already.</param>
/// <param name="Ly">Straight-line distance from the player in light years; 0 when not known.</param>
/// <param name="Load">"full" or "empty": the hold on the way there, which decides the jump range.</param>
/// <param name="Plotted">The route plotted in the game ends there, so <paramref name="Jumps"/> is the game's own count. Otherwise it is an estimate, 0 when not known.</param>
public sealed record Stop(string What, string System, string Station, string Verb, bool Here, double Ly, string Load, bool Plotted, int Jumps);

/// <summary>A short line that goes away by itself after four seconds.</summary>
/// <param name="Good">True for something that worked (the old overlay drew it green), false for "that key does nothing just now" (gold).</param>
public sealed record HaulNote(string Text, bool Good);

/// <summary>One commodity in the "where can I sell" pick list.</summary>
/// <param name="PriceHere">Credits per ton at the station the player is docked at; 0 when it is not for sale there.</param>
public sealed record CommodityChoice(string Id, string Name, int PriceHere);

/// <summary>
/// A "where can I sell this?" lookup while it is on screen. Its rows are trade rows whose buy side
/// says what is being sold (<see cref="From"/>, <see cref="Paid"/>) and whose sell side is one place.
/// </summary>
public sealed class Lookup
{
    public string Id { get; internal init; } = "";              // commodity id; "" when the hold was empty
    public string Name { get; internal init; } = "";
    public int Count { get; internal init; }                    // tons to sell
    public int Paid { get; internal init; }                     // credits per ton paid; 0 = not known
    public string From { get; internal init; } = "";            // "Your cargo hold", or the docked station that sells it, or ""
    public string Rank { get; internal init; } = "near";        // near (nearest first) | profit (best money first)
    public string Source { get; internal init; } = "cargo";     // cargo ("sell my cargo") | pick (a commodity the player picked)
    public string Status { get; internal set; } = "searching";  // searching | done | none | error
    public string Message { get; internal set; } = "";          // the line for the list; with no rows, also what the overlay says
    public IReadOnlyList<TradeRow> Rows { get; internal set; } = [];
    public int Index { get; internal set; }                     // which of Rows is on screen
    internal int CargoIndex { get; init; }                      // which kind of cargo in the hold, biggest first
}

/// <summary>
/// The "hauling" section of settings.json: what the player had going, so that it is still there
/// after Studio was closed and started again. It holds the least that will do. The accepted trade
/// is kept whole, because no later search would find it again with the prices it was accepted at;
/// of an open lookup only what was looked up, which is asked afresh.
/// </summary>
sealed record HaulingKept
{
    /// <summary>The section's name in settings.json.</summary>
    public const string Section = "hauling";

    public TradeRow? Accepted { get; init; }
    public string Leg { get; init; } = "";          // how far along it was: buy | sell | back
    public DateTime AcceptedAt { get; init; }       // when its prices were as old as it says (AgeHours)
    public string LookSource { get; init; } = "";   // cargo | pick; "" when no lookup was open
    public string LookId { get; init; } = "";       // the commodity looked up
}

// The part of Hauling a screen looks at most: the one trade on show, where to fly for it, the short
// notes, the "where can I sell this?" lookups that take the screen over, what of all that a restart
// of Studio keeps, and the overlay menu's lines with the presets. The class is described in Hauling.cs.
public sealed partial class Hauling
{
    const string CargoHold = "Your cargo hold";

    static readonly TimeSpan NoteShown = TimeSpan.FromSeconds(4);

    HaulNote? note;
    DateTime? noteUntil;
    DateTime? checkLookup;                  // when to look whether the looked-up cargo is gone, after a sale
    CancellationTokenSource? lookupStop;    // stops a lookup that is still running when another takes its place

    // ---------------------------------------------------------------- what is on screen

    /// <summary>
    /// The one trade to show: a lookup's place first (null while it has none), else the accepted
    /// trade (even when a newer list no longer has it), else trade number <see cref="Index"/> of
    /// <see cref="Trades"/>. Null means show <see cref="Message"/>.
    /// </summary>
    public TradeRow? Shown =>
        Look is not null ? Look.Rows.ElementAtOrDefault(Look.Index)
        : Accepted ?? (trades.Count == 0 ? null : trades[Index < trades.Count ? Index : 0]);

    /// <summary>Which of <see cref="Trades"/> shows while nothing is accepted, counted from 0. Every new answer puts it back to the first.</summary>
    public int Index { get; private set; }

    /// <summary>
    /// Puts a row the player picked on screen: one of a lookup's rows while a lookup is on, else
    /// one of <see cref="Trades"/>. False when the screen stays as it is: an accepted trade keeps it
    /// until it is finished, dropped or another is accepted.
    /// </summary>
    public bool Show(TradeRow row)
    {
        int at = Find(Look?.Rows ?? trades, row);
        if (at < 0 || (Look is null && Accepted is not null)) return false;
        if (Look is not null) Look.Index = at;
        else Index = at;
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// The next key: the next trade in the search's own order, round and round; during a lookup the
    /// next place to sell. True only when it stepped to another trade (the old overlay then showed
    /// itself if it was hidden); with a trade accepted it only says so in a note.
    /// </summary>
    /// <param name="acceptKey">What the overlay calls the way to drop a trade (the words on its button, quoted), for that note.</param>
    public bool NextTrade(string acceptKey)
    {
        if (Look is not null)
        {
            if (Look.Rows.Count == 0) return false;
            Look.Index = (Look.Index + 1) % Look.Rows.Count;
        }
        else if (Accepted is not null) Say($"This trade is accepted: {acceptKey} drops it", good: false);
        else if (trades.Count > 0) Index = (Index + 1) % trades.Count;
        else return false;
        RaiseChanged();
        return Look is null && Accepted is null;
    }

    /// <summary>Where to fly next for the trade on screen: the place to buy, the place to sell, or back again. Null with no trade on screen.</summary>
    public Stop? NextStop
    {
        get
        {
            if (Shown is not { } trade) return null;
            // A lookup has one stop, the place to sell. Docked at the buy station the next stop is already the place to sell.
            string what = Look is not null ? "SELL" : Leg == "back" ? "BACK" : Leg == "sell" || trade.BuyHere ? "SELL" : "BUY";
            (string name, string station, double x, double y, double z) = what == "SELL"
                ? (trade.SellSystem, trade.SellStation, trade.SellX, trade.SellY, trade.SellZ)
                : (trade.BuySystem, trade.BuyStation, trade.BuyX, trade.BuyY, trade.BuyZ);
            bool full = what == "SELL" || (what == "BACK" && trade.ReturnProfit > 0);
            double ly = game.StarPos is { } here && (x != 0 || y != 0 || z != 0) ? Math.Round(ShipMath.Distance(here, [x, y, z]), 1) : 0;
            // The game's own count wins when the plotted route ends there; otherwise it is an estimate from the jump range.
            int plotted = game.PlottedJumps(name);
            return new Stop(what, name, station, what switch { "BUY" => "buy at", "SELL" => "sell at", _ => "back to" },
                Here: Same(game.StarSystem, name), ly, full ? "full" : "empty", plotted > 0,
                plotted > 0 ? plotted : ShipMath.JumpsFor(ly, full ? JumpFull : JumpEmpty));
        }
    }

    /// <summary>
    /// The copy key: the next stop's star system, ready to paste into the galaxy map's search, or
    /// null with no trade on screen. The caller owns the clipboard; once the text is on it, call <see cref="Copied"/>.
    /// </summary>
    public string? CopyText => NextStop?.System;

    /// <summary>Says in a note that the text reached the clipboard.</summary>
    public void Copied(string text)
    {
        Log.Info($"Copied next stop: {text}");
        Say($"✔ {text} copied", good: true);
        RaiseChanged();
    }

    /// <summary>The short line to show just now, or null.</summary>
    public HaulNote? Note => now() < noteUntil ? note : null;

    void Say(string text, bool good) => (note, noteUntil) = (new(text, good), now() + NoteShown);

    // ---------------------------------------------------------------- lookups: where to sell one commodity

    /// <summary>
    /// The lookup on screen, or null. While it is set the overlay and the list show its places; the
    /// trade list underneath keeps up to date and comes back when the lookup ends.
    /// </summary>
    public Lookup? Look { get; private set; }

    /// <summary>
    /// While a lookup is on screen the accept key and the menu keys do nothing but say how to get
    /// back. True, with that note shown, when that is so.
    /// </summary>
    /// <param name="cargoKey">What the overlay calls the way back to the trades (the words on its button, quoted).</param>
    public bool BlockedByLookup(string cargoKey)
    {
        if (Look is null) return false;
        Say($"{cargoKey} goes back to your trades first", good: false);
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// The "sell my cargo" key: the nearest stations that take the biggest load in the hold for more
    /// than was paid. Again: the next kind of cargo. After the last kind, or during a lookup for a
    /// picked commodity: back to the trades.
    /// </summary>
    public void SellMyCargo()
    {
        int kinds = game.Cargo.Count, next = Look is { Source: "cargo" } ? Look.CargoIndex + 1 : 0;
        if (Look is not null && (Look.Source != "cargo" || next >= kinds)) BackToTrades();
        else if (kinds > 0) LookUpCargo(next);
        else
        {
            Look = new Lookup { Status = "none", Message = "Your cargo hold is empty, so there is nothing to look up." };
            RaiseChanged();
        }
    }

    /// <summary>Where to sell one kind of cargo in the hold, counted from the biggest load.</summary>
    void LookUpCargo(int kind)
    {
        CargoItem load = game.Cargo[kind];
        StartLookup(new Lookup { Id = load.Id, Name = load.Name, Count = load.Count, Paid = game.PaidFor(load.Id), From = CargoHold, CargoIndex = kind });
    }

    /// <summary>
    /// Where a commodity the player picked sells for the most, for a full hold of it. When the
    /// station they are docked at sells it, its price there is taken as the cost. False when no
    /// lookup started: no commodity given, or the game has not said yet where the player is.
    /// </summary>
    public bool WhereToSell(string commodityId)
    {
        string id = commodityId.Trim().ToLowerInvariant();
        if (id.Length == 0 || string.IsNullOrEmpty(game.StarSystem)) return false;
        MarketItem? here = game.Market?.Items.GetValueOrDefault(id) is { BuyPrice: > 0, Stock: > 0 } sold ? sold : null;
        StartLookup(new Lookup
        {
            Id = id, Name = GameData.CommodityName(id), Count = Math.Max(1, CargoTons), Paid = here?.BuyPrice ?? 0,
            From = here is null ? "" : game.Station ?? "", Rank = "profit", Source = "pick",
        });
        return true;
    }

    /// <summary>Ends the lookup: the trades are on screen again.</summary>
    public void BackToTrades()
    {
        lookupStop?.Cancel();
        (Look, checkLookup) = (null, null);
        RaiseChanged();
    }

    /// <summary>
    /// The commodities to pick from: what the docked station sells first, by name, each with its
    /// price there (known once the player has opened its commodities screen); then every other
    /// traded commodity, by name.
    /// </summary>
    public IReadOnlyList<CommodityChoice> CommodityChoices
    {
        get
        {
            CommodityChoice[] here =
            [
                .. (game.Market?.Items.Values ?? []).Where(item => item.Stock > 0 && item.BuyPrice > 0)
                    .Select(item => new CommodityChoice(item.Id, item.Name.Length > 0 ? item.Name : GameData.CommodityName(item.Id), item.BuyPrice))
                    .OrderBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase),
            ];
            return
            [
                .. here,
                .. search.Commodities.Where(commodity => !here.Any(choice => Same(choice.Id, commodity.Id)))
                    .Select(commodity => new CommodityChoice(commodity.Id, commodity.Name, 0))
                    .OrderBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase),
            ];
        }
    }

    void StartLookup(Lookup lookup)
    {
        if (string.IsNullOrEmpty(game.StarSystem)) return;
        lookupStop?.Cancel();   // a lookup still running is overtaken: its answer is not wanted any more
        lookupStop = new CancellationTokenSource();
        lookup.Message = $"Looking for places to sell {lookup.Name}…";
        Look = lookup;
        Log.Info($"Lookup: {lookup.Name} ({lookup.Count} t, paid {lookup.Paid}) from {game.StarSystem}");
        RaiseChanged();
        _ = RunLookup(lookup, lookupStop.Token);
    }

    async Task RunLookup(Lookup lookup, CancellationToken stop)
    {
        SearchRequest request = Request() with
        {
            Mode = "lookup", LookItem = lookup.Id, LookCount = lookup.Count, LookPaid = lookup.Paid, LookFrom = lookup.From,
            LookRank = lookup.Rank, LookLy = Settings.MaxLy, LiveMarket = game.Market,
        };
        SearchResult result;
        try { result = await search.Run(request, stop); }
        catch (Exception problem) { result = new SearchResult { Ok = false, Error = problem.GetBaseException().Message }; }
        // Overtaken by another lookup, or the player went back to the trades. A search may end the moment it is
        // told to stop, while this lookup is still the one on screen: that is no failure to write up either.
        if (stop.IsCancellationRequested || !ReferenceEquals(Look, lookup)) return;

        try
        {
            List<TradeRow> rows = [.. result.Trades];
            // Every place carries the lookup's name: for cargo that is the name the game itself wrote for the hold, in the
            // language it runs in, where the search's is the bundled table's English one.
            foreach (TradeRow row in rows) row.Commodity = lookup.Name;
            (lookup.Rows, lookup.Index) = (rows, 0);
            (lookup.Status, lookup.Message) =
                !result.Ok ? ("error", $"Couldn't reach the market data service: {result.Error}")
                : rows.Count > 0 ? ("done", $"{rows.Count} places to sell {lookup.Name}")
                : ("none", $"No station within {Settings.MaxLy} ly is known to "
                    + (lookup.Paid > 0 ? $"pay more than the {lookup.Paid.ToString("N0", CultureInfo.InvariantCulture)} cr you paid for" : "buy")
                    + $" {lookup.Name}. Try a bigger route distance or older prices.");
            RaiseChanged();
        }
        catch (Exception problem)
        {
            Log.Error("A lookup answer could not be taken in", problem);
        }
    }

    // ---------------------------------------------------------------- what a restart of Studio keeps

    DateTime acceptedAt;        // when the accepted trade's prices were as old as it says
    HaulingKept kept = new();   // what the settings file holds

    /// <summary>
    /// Saves the accepted trade and what the open lookup looks up whenever they are not what the
    /// settings file holds. It runs with every change a screen is told of, so no operation has to
    /// think of it.
    /// </summary>
    void Keep()
    {
        // Until the game has said where the player is nothing has been brought back, and what the file holds must not be written over.
        if (!started) return;
        var current = new HaulingKept
        {
            Accepted = Accepted, Leg = Leg, AcceptedAt = Accepted is null ? default : acceptedAt,
            LookSource = Look is { Id.Length: > 0 } ? Look.Source : "", LookId = Look?.Id ?? "",
        };
        if (current == kept) return;
        kept = current;
        try { settings.Set(HaulingKept.Section, current); }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error("The accepted trade and the open lookup could not be saved, so a restart of Studio will not bring them back", problem);
        }
    }

    /// <summary>
    /// Brings back what Studio was last closed with, as far as it is still true: the player may
    /// have flown on, traded and waited meanwhile, so the hold and the dock as they are now have
    /// the say, not what was saved. An accepted trade comes back unless it was finished or given
    /// up meanwhile (its cargo is gone, or the ship stands where the round trip ends) or its
    /// prices, which have aged with the time that passed, are now older than the player lets
    /// prices be. A cargo lookup comes back while that cargo is still in the hold, a picked
    /// commodity's as it was; both ask the data service afresh. The rest is dropped without a
    /// word to the player. True when a trade was brought back.
    /// </summary>
    bool Restore()
    {
        // (A section that cannot be read counts as nothing kept; the settings file logs that itself.)
        kept = settings.Get<HaulingKept>(HaulingKept.Section);
        if (kept.Accepted is { } trade && Whole(trade))
        {
            // How far along it is now. Bought meanwhile: the next stop is the place to sell.
            bool carrying = Carrying(trade.CommodityId);
            string leg = kept.Leg switch
            {
                "buy" => carrying ? "sell" : "buy",
                "sell" => carrying ? "sell" : "",
                "back" => game.Docked && game.MarketId == trade.BuyMarketId ? "" : "back",
                _ => "",
            };
            double hours = trade.AgeHours + Math.Max(0, (now() - kept.AcceptedAt).TotalHours);
            if (leg.Length > 0 && hours <= Settings.MaxAgeHours)
            {
                (trade.AgeHours, trade.Updated) = (hours, TradeSearch.AgeText(hours));
                (Accepted, Leg, acceptedAt, ReturnLoaded) = (trade, leg, now(), leg == "back" && Carrying(trade.ReturnId));
                RefreshRows();   // from where the player is now
                Log.Info($"Brought back the accepted trade: {trade.Commodity}, {trade.BuyStation} to {trade.SellStation} (next: {Leg})");
            }
            else Log.Info($"Not brought back: the accepted trade ({trade.Commodity} to {trade.SellStation}) was finished meanwhile, or its prices are too old by now");
        }

        string looked = kept.LookId ?? "";   // a file changed by hand can put null where a name is expected
        if (kept.LookSource == "pick") WhereToSell(looked);
        else if (kept.LookSource == "cargo" && Array.FindIndex<CargoItem>([.. game.Cargo], load => Same(load.Id, looked)) is >= 0 and var kind) LookUpCargo(kind);
        return Accepted is not null;
    }

    /// <summary>A saved trade can be readable and still lack parts: a text the file sets to null arrives as null, which no screen expects.</summary>
    static bool Whole(TradeRow trade) => trade.CommodityId is { Length: > 0 } && typeof(TradeRow).GetProperties().All(part => part.GetValue(trade) is not null);

    // ---------------------------------------------------------------- the overlay menu's lines, and the presets

    /// <summary>The overlay menu's settings page as it stands.</summary>
    public IReadOnlyList<SettingLine> SettingLines => HaulMenu.SettingLines(Settings);

    /// <summary>
    /// Steps one of the <see cref="SettingLines"/> to the choice before (-1) or after (+1).
    /// False when nothing changed: the end of a list that does not come round, or a line that is not usable.
    /// </summary>
    public bool StepSetting(string key, int direction) =>
        HaulMenu.NextChoice(Settings, key, direction) is { } choice && ChangeSettings(One(key, choice));

    /// <summary>The trades in the order the menu's short list is set to (<see cref="MarketSettings.ListSort"/>).</summary>
    public IReadOnlyList<TradeRow> SortedTrades => HaulMenu.Sorted(trades, Settings.ListSort);

    /// <summary>Puts the short list in one of the <see cref="HaulMenu.ListSorts"/> orders, by its key. Saved; it starts no search.</summary>
    public void SortListBy(string key) => ChangeSettings(One("listSort", key));

    /// <summary>Moves the short list to the order before (-1) or after (+1) the one it is in.</summary>
    public void StepListSort(int direction) => SortListBy(HaulMenu.NextSort(Settings.ListSort, direction));

    /// <summary>The presets, each with what it sets and whether the settings in use are its own.</summary>
    public IReadOnlyList<PresetLine> PresetLines =>
        [.. Settings.Presets.Select(preset => new PresetLine(preset.Name, HaulMenu.PresetText(preset), Settings.IsUsing(preset)))];

    /// <summary>Changes the settings the named preset holds, as <see cref="ChangeSettings"/> would. False when there is no such preset.</summary>
    public bool UsePreset(string name)
    {
        if (Settings.Presets.FirstOrDefault(preset => Same(preset.Name, name)) is not { } wanted || Settings.With(wanted.Set) is not { } changed) return false;
        Log.Info($"Preset used: {wanted.Name}");
        Save(changed);
        return true;
    }

    /// <summary>Keeps the search settings as they are now under a name; an existing preset of that name is replaced.</summary>
    public Outcome SavePreset(string name)
    {
        name = name.Trim();
        if (name.Length > MarketSettings.MaxPresetName) name = name[..MarketSettings.MaxPresetName].Trim();
        if (name.Length == 0) return new(false, "Type a name for the preset first.");
        var preset = new Preset(name, Settings.Only(MarketSettings.PresetKeys));
        List<Preset> presets = [.. Settings.Presets];
        int at = presets.FindIndex(old => Same(old.Name, name));
        if (at < 0 && presets.Count >= MarketSettings.MaxPresets) return new(false, $"There is room for {MarketSettings.MaxPresets} presets. Delete one first.");
        if (at < 0) presets.Add(preset);
        else presets[at] = preset;
        Save(Settings with { Presets = presets });
        return new(true, at < 0 ? $"Saved \"{name}\"." : $"Updated \"{name}\" with the settings as they are now.");
    }

    /// <summary>Forgets a preset. The settings in use stay as they are.</summary>
    public void RemovePreset(string name) =>
        Save(Settings with { Presets = [.. Settings.Presets.Where(preset => !Same(preset.Name, name))] });

    static Dictionary<string, JsonElement> One(string key, object value) => new() { [key] = JsonSerializer.SerializeToElement(value) };
}
