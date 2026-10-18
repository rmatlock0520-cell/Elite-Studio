using System.Globalization;
using System.Text.Json;
using EliteStudio.App;

namespace EliteStudio.Market;

/// <summary>
/// Studio Market's routes, all under /api/market/: what the Market page shows of the hauling state
/// and every operation it offers, plus the "market" live event whenever that state changes.
///   GET  hauling          everything the page shows, in one answer (see Picture below)
///   GET  stations?system=Sol    {"stations":[...]}: the stations there that sell something, to pick the one to buy at from
///   POST search           search again
///   POST show             a row of the list: puts it on the overlay
///   POST accept           a row of the list: accepts it; with no row, the trade on the overlay
///   POST drop             drops the accepted trade and searches again
///   POST sell-cargo       where to sell what is in the hold; again for the next kind of cargo, then back
///   POST where-to-sell    {"commodity":"gold"}: where a picked commodity sells for the most
///   POST back             ends a lookup: the trades are listed again
///   POST copied           {"text":"Sol"}: the page has put the next stop's star system on the clipboard
///   POST settings         {"maxLy":40}: only the settings to change, under their names in settings.json;
///                         {"listSort":"hour"} is the order of the overlay menu's short trade list
///   POST preset/use, preset/save, preset/remove    {"name":"Long round trips"}
///   POST old-tool-read    the player has read the note that the settings were copied from the Elite Haul Overlay
/// A row is sent as it was given out; its commodityId, buyMarketId and sellMarketId are enough.
/// Every POST answers with the same picture as the GET, and so does every "market" event. A POST
/// adds "said": the line the Haul Overlay's window showed after that click ("Accepted: Gold to ...",
/// "Pick a preset from the list first."), or null when it had nothing to say. The page keeps no
/// hauling state of its own: it draws the newest picture.
/// </summary>
public static class MarketApi
{
    /// <summary>"market" events follow each other no closer than this; what changes meanwhile goes out with the next one.</summary>
    static readonly TimeSpan PushGap = TimeSpan.FromMilliseconds(300);

    /// <summary>What the page may send with an operation; each route reads the one field it needs.</summary>
    sealed class Body
    {
        public string? Commodity { get; set; }
        public string? Text { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>Registers the routes. Call on the UI thread, which is also where the server runs them.</summary>
    /// <param name="pause">Waits that long; tests pass their own, so no test depends on the clock.</param>
    public static void Map(WebServer web, Hauling hauling, Func<TimeSpan, Task>? pause = null)
    {
        pause ??= Task.Delay;

        object Picture(string? said = null)
        {
            Lookup? look = hauling.Look;
            IReadOnlyList<TradeRow> rows = look?.Rows ?? hauling.Trades;
            return new
            {
                // the list: the trades, or a lookup's places to sell while one is open, in the search's own order
                rows,
                // which of the rows is on the overlay, counted from 0; -1 when none of them is
                shown = Hauling.Find(rows, hauling.Shown),
                listMessage = hauling.ListMessage,
                // what the overlay says in place of a trade; the page shows it while there is no list message yet
                message = hauling.Message,
                listOneWay = hauling.ListOneWay,
                searching = hauling.Searching,
                lastSearch = hauling.LastSearchAt?.ToString("HH:mm", CultureInfo.InvariantCulture),
                accepted = hauling.Accepted,
                nextStop = hauling.NextStop,
                lookup = look is null ? null : new { look.Id, look.Name, look.Count, look.Paid, look.From, look.Source, look.Status },
                settings = hauling.Settings,
                // from a station to a system: the station the search buys at, the player's own choice or else where they are (or were last) docked
                carryFrom = new { system = hauling.CarryFrom.System, station = hauling.CarryFrom.Station },
                // what the search really uses, once the ship has had its say; and the ship's own say: the pad it
                // needs, and how far it works out to jump with that cargo in the hold (0 while it is not known)
                inUse = new
                {
                    pad = StationFilter.PadLetter(hauling.Pad),
                    shipPad = StationFilter.PadLetter(hauling.ShipPad),
                    cargoTons = hauling.CargoTons,
                    jumpEmpty = hauling.JumpEmpty,
                    jumpFull = hauling.JumpFull,
                    shipEmpty = hauling.ShipRanges.Empty,
                    shipFull = hauling.ShipRanges.Full,
                    jumpRangesSwapped = hauling.JumpRangesSwapped,
                },
                // the settings the overlay menu steps through, each with its choices, and the orders of its short list
                settingLines = hauling.SettingLines,
                listSorts = HaulMenu.ListSorts,
                presets = hauling.PresetLines,
                commodities = hauling.CommodityChoices,
                // the old tool's settings file, from the first run that copied the search settings from it until the player has read that
                copiedFrom = hauling.CopiedFrom,
                said,
            };
        }

        WebResponse Answer(string? said = null) => WebResponse.Json(Picture(said));

        // One push, then a rest. Hauling says "changed" several times within one click and with every
        // change in the game; what comes during the rest goes out as one push when the rest is over.
        bool resting = false, missed = false;
        void Push()
        {
            if (resting)
            {
                missed = true;
                return;
            }
            web.Send("market", Picture());
            resting = true;
            _ = Rest();
        }
        async Task Rest()
        {
            await pause(PushGap);
            resting = false;
            if (!missed) return;
            missed = false;
            // Nobody waits for this task, so a problem would vanish with it: it is logged here instead.
            try { Push(); }
            catch (Exception problem) { Log.Error("The Market page could not be told of a change in hauling", problem); }
        }
        hauling.Changed += Push;

        web.Map("GET", "/api/market/hauling", _ => Answer());

        web.Map("GET", "/api/market/stations", async request =>
            WebResponse.Json(new { stations = await hauling.Stations(Trimmed(request.Query.GetValueOrDefault("system"))) }));

        web.Map("POST", "/api/market/search", _ =>
        {
            hauling.SearchAgain();
            return Answer();
        });

        web.Map("POST", "/api/market/show", request =>
        {
            TradeRow row = request.Read<TradeRow>();
            // An accepted trade keeps the overlay: picking another listed row only says how to switch to it.
            bool stays = hauling.Look is null && hauling.Accepted is { } accepted && !row.IsSameTrade(accepted) && hauling.Trades.Any(row.IsSameTrade);
            hauling.Show(row);
            return Answer(stays ? "The overlay stays on your accepted trade. Tick this row's box, or click Accept trade, to switch to it." : null);
        });

        web.Map("POST", "/api/market/accept", request =>
        {
            if (hauling.Look is not null) return Answer("Click \"Back to trades\" first, then pick the trade to accept.");
            TradeRow row = request.Read<TradeRow>();
            // The row the player picked in the list, or else the trade the overlay is showing.
            bool picked = !string.IsNullOrEmpty(row.CommodityId);
            if ((picked ? row : hauling.Shown) is not { } trade || !hauling.Accept(trade))
                return Answer(picked ? "That trade is no longer on the list." : "No trade to accept yet.");
            return Answer($"Accepted: {hauling.Accepted!.Commodity} to {hauling.Accepted.SellStation}. The overlay stays on it until you finish it or drop it.");
        });

        web.Map("POST", "/api/market/drop", _ =>
        {
            bool accepted = hauling.Accepted is not null;
            hauling.Drop();
            return Answer(accepted ? "Dropped. Searching again from where you are…" : null);
        });

        web.Map("POST", "/api/market/sell-cargo", _ =>
        {
            hauling.SellMyCargo();
            return Answer();
        });

        web.Map("POST", "/api/market/where-to-sell", request =>
        {
            string commodity = Trimmed(request.Read<Body>().Commodity);
            if (commodity.Length == 0) return Answer("Pick a commodity from the list first.");
            // No lookup without knowing where the player is: the line that says so is hauling's own.
            return Answer(hauling.WhereToSell(commodity) ? null : hauling.Message);
        });

        web.Map("POST", "/api/market/back", _ =>
        {
            hauling.BackToTrades();
            return Answer();
        });

        web.Map("POST", "/api/market/copied", request =>
        {
            string text = Trimmed(request.Read<Body>().Text);
            if (text.Length == 0) return WebResponse.Error("Nothing was copied.", 400);
            hauling.Copied(text);
            return Answer();
        });

        web.Map("POST", "/api/market/settings", request =>
        {
            Dictionary<string, JsonElement>? changes = null;
            try { changes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Body, Settings.Json); }
            catch (JsonException) { }   // not a list of settings: answered below
            return changes is not null && hauling.ChangeSettings(changes)
                ? Answer()
                : WebResponse.Error("These settings could not be read, so nothing was changed.", 400);
        });

        web.Map("POST", "/api/market/preset/use", request =>
        {
            if (Named(hauling, request) is not { } preset || !hauling.UsePreset(preset.Name)) return Answer("Pick a preset from the list first.");
            return Answer($"Using \"{preset.Name}\": {preset.Text}.");
        });

        web.Map("POST", "/api/market/preset/save", request => Answer(hauling.SavePreset(Trimmed(request.Read<Body>().Name)).Message));

        web.Map("POST", "/api/market/preset/remove", request =>
        {
            if (Named(hauling, request) is not { } preset) return Answer("Pick a preset from the list first.");
            hauling.RemovePreset(preset.Name);
            return Answer($"Deleted \"{preset.Name}\". Your settings were not changed.");
        });

        web.Map("POST", "/api/market/old-tool-read", _ =>
        {
            hauling.CopyNoteRead();
            return Answer();
        });
    }

    /// <summary>The preset a request names, capitals not minded, or null when there is none of that name.</summary>
    static PresetLine? Named(Hauling hauling, WebRequest request)
    {
        string name = Trimmed(request.Read<Body>().Name);
        return hauling.PresetLines.FirstOrDefault(preset => preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    // JSON can put null where a name is expected.
    static string Trimmed(string? text) => text?.Trim() ?? "";
}
