using System.Globalization;
using EliteStudio.App;
using EliteStudio.Core;
using static System.FormattableString;

namespace EliteStudio.Market;

/// <summary>
/// Finds trades from live community market data (the Ardent Insight service, plus Spansh for
/// Powerplay filters). What was answered a few minutes ago is not asked again: <see cref="MarketData"/> remembers it.
/// </summary>
/// <param name="data">The app's one client for the data services, which "Find by name" asks through as well; whoever made it closes it.</param>
/// <param name="utcNow">The clock, in UTC; tests pass their own.</param>
sealed class TradeSearch(MarketData data, Func<DateTime>? utcNow = null) : ITradeSearch
{
    const int MostRows = 40;
    const int StationListDays = 30;   // a station whose prices are a month old is still a station to pick from

    public IReadOnlyList<(string Id, string Name)> Commodities => data.Overview?.Traded ?? [];

    /// <summary>
    /// Runs one search. It never throws: a search that fails comes back with Ok = false and the
    /// reason in Error. So does one stopped through <paramref name="cancel"/>, at once and without
    /// a line in the log.
    /// </summary>
    public async Task<SearchResult> Run(SearchRequest request, CancellationToken cancel)
    {
        // The search's own stop signal: the caller's, and the search being over. A search that fails
        // half-way still has requests waiting their turn; those are dropped, not sent for nothing.
        using var over = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        MarketData.Asking ask = data.Ask(utcNow?.Invoke() ?? DateTime.UtcNow, over.Token);
        // Worked out off the caller's thread, which is the one that draws the overlay: a wide search
        // sifts through some hundred thousand offers. The result comes back on the caller's thread.
        return await Task.Run(async () =>
        {
            try { return await Answer(request, ask, cancel); }
            finally { over.Cancel(); }
        }, CancellationToken.None);
    }

    /// <summary>The stations of a system that sell something, by name. A question the service does not answer brings none.</summary>
    public async Task<IReadOnlyList<string>> Stations(string system, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(system)) return [];
        MarketData.Asking ask = data.Ask(utcNow?.Invoke() ?? DateTime.UtcNow, cancel);
        IReadOnlyList<Offer> sold = await ask.Offers(Invariant($"{MarketData.SystemAddress(system.Trim())}/commodities/exports?maxDaysAgo={StationListDays}"), 10);
        return [.. sold.Select(offer => offer.Station).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }

    async Task<SearchResult> Answer(SearchRequest request, MarketData.Asking ask, CancellationToken cancel)
    {
        bool lookup = Is(request.Mode, "lookup");
        SearchResult Failed(string why) => new() { Ok = false, Error = why, Lookup = lookup, Urls = ask.Urls, StarSystem = request.StarSystem };
        try
        {
            cancel.ThrowIfCancellationRequested();
            if (!Is(request.Mode, "anywhere") && string.IsNullOrWhiteSpace(request.StarSystem)) return Failed("Your current system is not known yet.");
            var search = new Search(request, ask, await ask.Overview());
            SearchResult found = lookup ? await search.Lookup() : Is(request.Mode, "carry") ? await search.Carry() : await search.Trades();
            cancel.ThrowIfCancellationRequested();
            if (ask.Lost > 0)
                Log.Error(Invariant($"Trade search ({request.Mode}) from {request.StarSystem}: {ask.Lost} of {ask.Urls.Count} requests failed and counted as no data. First: {ask.FirstLoss}"));
            return ask.NothingAnswered is { } silence ? Failed(silence) : found;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return Failed("The search was stopped.");
        }
        catch (Exception problem)
        {
            Log.Error($"Trade search ({request.Mode}) from {request.StarSystem} failed", problem);
            return Failed(MarketData.Reason(problem));
        }
    }

    // Names and settings are compared the way the Haul Overlay did: without regard to capitals.
    static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>How old a price is, in words: "just now", "5h ago", "3d ago".</summary>
    internal static string AgeText(double hours) =>
        hours < 1 ? "just now"
        : hours < 48 ? hours.ToString("0", CultureInfo.InvariantCulture) + "h ago"
        : (hours / 24).ToString("0", CultureInfo.InvariantCulture) + "d ago";

    /// <summary>
    /// One run of the search: the request, the answers it asks for, and every sum that depends on
    /// both. The steps and their numbers (6 local picks, 16 price spikes, 28 commodities, 6 places
    /// to buy, 3 trades per commodity ...) are the Haul Overlay's, kept as they were.
    /// </summary>
    sealed class Search(SearchRequest request, MarketData.Asking ask, PriceOverview overview)
    {
        readonly bool anywhere = Is(request.Mode, "anywhere");
        readonly bool byTrip = Is(request.RankBy, "trip");
        readonly bool returns = request.Returns && !Is(request.Mode, "carry");   // carrying to a system the player names is one way
        readonly int cargo = Math.Max(0, request.Cargo);

        // "Any" supply or demand still means at least one ton.
        readonly int minSupply = Math.Max(1, request.MinSupply);
        readonly int minDemand = Math.Max(1, request.MinDemand);

        readonly int days = MarketData.Days(request.MaxAgeHours);   // the exact limit in hours is applied here, in Keep
        readonly string carriers = MarketData.CarriersWord(request.Carriers);
        readonly StationFilter stations = new(request.Pad, request.MaxStarLs, request.Surface, request.Carriers);
        readonly string here = MarketData.SystemAddress(request.StarSystem ?? "");

        // A jump range that is not known counts as 12 light years for the time estimate (no jump counts are shown then).
        readonly double jumpFull = request.Jump > 0 ? request.Jump : 12.0;
        readonly double jumpEmpty = Math.Max(request.JumpEmpty, request.Jump > 0 ? request.Jump : 12.0);

        PowerSet? buyPower, sellPower;

        /// <summary>Where near the player ONE commodity can be sold: what is in the hold, or a commodity picked from the list.</summary>
        public async Task<SearchResult> Lookup()
        {
            string id = request.LookItem;
            int count = request.LookCount, paid = request.LookPaid;
            Task<IReadOnlyList<Offer>> around = ask.Offers(Invariant($"{here}/commodity/name/{id}/nearby/imports?maxDistance={Math.Clamp(request.LookLy, 1, 500)}&maxDaysAgo={days}&minVolume=1{carriers}"), 3);
            // "nearby" leaves out the system asked about, so its own stations are a second question
            Task<IReadOnlyList<Offer>> inSystem = ask.Offers(Invariant($"{here}/commodity/name/{id}?maxDaysAgo={days}"), 3);

            var seen = new HashSet<long>();
            var places = new List<TradeRow>();
            foreach (Offer sell in (await around).Concat(await inSystem))
            {
                if (!seen.Add(sell.MarketId)) continue;
                int price = sell.SellPrice, demand = sell.Demand;
                double hours = Hours(sell);
                // The station the player is docked at: what its market screen says right now beats
                // what was last uploaded, and no filter can hide the station the player is standing in.
                if (request.LiveMarket is { } live && live.MarketId == sell.MarketId && live.Items.TryGetValue(id, out MarketItem? shown))
                    (price, demand, hours) = (shown.SellPrice, shown.Demand, 0.0);
                else if (!Keep(sell)) continue;
                if (price <= 0 || demand < 1) continue;
                int profit = price - paid;
                if (paid > 0 && profit <= 0) continue;   // only places that pay more than was paid

                double ly = sell.Distance ?? 0.0;   // from the player; 0 for the system the player is in
                int units = Math.Min(count, demand), jumps = ShipMath.JumpsFor(ly, jumpFull);
                TradeRow row = SellSide(sell, new TradeRow
                {
                    CommodityId = id, Commodity = GameData.CommodityName(id),
                    BuyStation = request.LookFrom, BuySystem = request.StarSystem ?? "", BuyPrice = paid, Stock = count,
                    DistanceLy = Math.Round(ly, 1), ProfitPerTon = profit, Units = units,
                    ProfitPerTrip = (long)profit * units, RoundTripProfit = (long)profit * units, AgeHours = hours, JumpsOut = jumps,
                    Minutes = Math.Round((jumps * ShipMath.SecondsPerJump + Cruise(sell.StarLs) + ShipMath.SecondsPerStop) / 60, 1),
                });
                (row.SellPrice, row.Demand) = (price, demand);   // the game's own numbers when docked there
                places.Add(row);
            }

            IEnumerable<TradeRow> sorted = Is(request.LookRank, "profit")
                // a commodity picked from the list: the most money first
                ? places.OrderByDescending(r => r.ProfitPerTrip).ThenByDescending(r => r.ProfitPerTon).ThenBy(r => r.DistanceLy)
                // the player's cargo: places that take the whole load first, then the fewest jumps, the best price, the newest price
                : places.OrderBy(r => r.Units < count).ThenBy(r => r.JumpsOut).ThenByDescending(r => r.SellPrice).ThenBy(r => r.AgeHours);
            List<TradeRow> best = Listed(sorted);
            if (request.Jump <= 0) best.ForEach(r => r.JumpsOut = 0);   // range not known: the guess was good enough to sort by, not to show
            return new SearchResult { Trades = best, Lookup = true, Urls = ask.Urls, StarSystem = request.StarSystem };
        }

        /// <summary>The trade list: "near" (buy within BuyLy of the player) or "anywhere" (best in the galaxy).</summary>
        public async Task<SearchResult> Trades()
        {
            string? note = await Powerplay();
            Dictionary<string, Offer> local = await ForSaleHere();
            (Dictionary<string, List<Offer>> sellers, int scanned) = await MarketScan();
            List<string> picked = Pick(local, sellers);
            Dictionary<string, List<Offer>> buys = await PlacesToBuy(picked, local, sellers);
            List<TradeRow> rows = await Pair(picked, buys, sellers);
            if (returns) await ReturnLoads(rows);
            Timed(rows);
            return new SearchResult
            {
                Trades = Listed(Ranked(rows, row => row.RoundTripProfit)),
                Exports = local.Count, Scanned = scanned, Candidates = picked.Count,
                Urls = ask.Urls, StarSystem = request.StarSystem, Note = note,
            };
        }

        /// <summary>
        /// From one station to one system ("carry"): what that station sells that a station in the
        /// system pays more for, the best three places for each commodity. The station to buy at is
        /// the player's choice, so the station filter does not count for it; where they are docked
        /// there with its commodities screen read, the game's own numbers are used.
        /// </summary>
        public async Task<SearchResult> Carry()
        {
            SearchResult Nothing(string why) => new() { Empty = why, Urls = ask.Urls, StarSystem = request.StarSystem };
            string from = request.FromSystem.Trim(), station = request.FromStation.Trim(), to = request.ToSystem.Trim();
            if (to.Length == 0) return Nothing("Type the system you are travelling to under \"To system\" on the Market page.");
            if (from.Length == 0 || station.Length == 0) return Nothing("Dock at the station you are buying at, or pick it under \"From\" on the Market page.");

            // Everything the one system sells and everything the other buys: two questions, whatever the number of stations.
            // (The station to buy at may be a fleet carrier whatever the carriers setting says, so that one is asked without it.)
            Task<IReadOnlyList<Offer>> sold = ask.Offers(Invariant($"{MarketData.SystemAddress(from)}/commodities/exports?maxDaysAgo={days}"), 10);
            Task<IReadOnlyList<Offer>> wanted = ask.Offers(SystemList(to, "imports"), 10);

            var buys = new Dictionary<string, Offer>(StringComparer.OrdinalIgnoreCase);
            Offer? place = (await sold).FirstOrDefault(offer => Is(offer.Station, station));   // any listing of the station says what kind it is
            Offer? anyThere = place ?? (await sold).FirstOrDefault();                           // and any listing of the system where it is
            (double x, double y, double z) = anyThere is not null ? (anyThere.X, anyThere.Y, anyThere.Z) : (request.FromX, request.FromY, request.FromZ);
            if (request.LiveMarket is { } live && Is(live.StationName, station) && Is(live.StarSystem, from))
            {
                foreach (MarketItem item in live.Items.Values.Where(item => item.BuyPrice > 0 && item.Stock >= minSupply))
                    buys[item.Id] = new Offer(item.Id, live.MarketId, live.StationName, live.Type.Length > 0 ? live.Type : place?.Type ?? "", place?.Pad, place?.StarLs,
                        live.StarSystem, x, y, z, item.BuyPrice, item.SellPrice, item.Stock, item.Demand, 0, ask.Now, null);
            }
            else
            {
                foreach (Offer offer in (await sold).Where(offer => Is(offer.Station, station) && offer.BuyPrice > 0 && offer.Stock >= minSupply && Hours(offer) <= request.MaxAgeHours))
                    buys[offer.Commodity] = offer;
            }
            if (buys.Count == 0)
                return Nothing($"No prices new enough are known for {station} in {from}. Open its commodities market in the game while you are docked there, or allow older prices.");

            Dictionary<string, List<Offer>> takers = Sellers(await wanted, null).GroupBy(offer => offer.Commodity, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            if (takers.Count == 0)
                return Nothing($"No station in {to} that fits your filters has prices new enough. Check the system's name, or allow older prices.");

            var rows = new List<TradeRow>();
            foreach ((string id, Offer buy) in buys)
            {
                if (!takers.TryGetValue(id, out List<Offer>? sells)) continue;
                IEnumerable<TradeRow> pairs = sells.Where(sell => sell.SellPrice > buy.BuyPrice && sell.MarketId != buy.MarketId)
                    // a station whose place is not known is no distance away: better no jump count than one measured from Sol
                    .Select(sell => Trade(id, buy, sell, buy is { X: 0, Y: 0, Z: 0 } ? 0 : ShipMath.Distance([buy.X, buy.Y, buy.Z], [sell.X, sell.Y, sell.Z])));
                rows.AddRange(Ranked(pairs, pair => pair.ProfitPerTrip).Take(3));
            }
            if (rows.Count == 0) return Nothing($"Nothing {station} sells is known to fetch more at a station in {to} that fits your filters.");

            Timed(rows);
            List<TradeRow> best = Listed(Ranked(rows, row => row.RoundTripProfit));
            return new SearchResult
            {
                Trades = best, Exports = buys.Count, Candidates = rows.Count, Urls = ask.Urls, StarSystem = request.StarSystem,
                Summary = $"{best.Count} {(best.Count == 1 ? "load" : "loads")} from {station} to {to}",
            };
        }

        /// <summary>The jumps, the time and what an hour earns, for every trade found; and the name of the load to bring back.</summary>
        void Timed(List<TradeRow> rows)
        {
            foreach (TradeRow row in rows)
            {
                // Out with a full hold; back with a full hold too when there is a load to bring, otherwise empty (fewer jumps).
                int jumpsOut = ShipMath.JumpsFor(row.DistanceLy, jumpFull);
                int jumpsBack = returns ? ShipMath.JumpsFor(row.DistanceLy, row.ReturnProfit > 0 ? jumpFull : jumpEmpty) : 0;
                if (request.Jump > 0) (row.JumpsOut, row.JumpsBack) = (jumpsOut, jumpsBack);
                double seconds = TripSeconds(jumpsOut, jumpsBack, row.SellStarLs, row.BuyStarLs);
                row.RoundTripProfit = row.ProfitPerTrip + row.ReturnProfit;
                row.OneWay = !returns;
                row.Minutes = Math.Round(seconds / 60, 1);
                row.PerHour = Convert.ToInt64(row.RoundTripProfit / seconds * 3600);
                row.Return = row.ReturnId.Length > 0 ? GameData.CommodityName(row.ReturnId) : "";
            }
        }

        /// <summary>
        /// Fetches the systems a Powerplay filter allows ("near" searches only) and returns the line
        /// that tells the player what the filter did. No filter set: nothing is asked, no line.
        /// </summary>
        async Task<string?> Powerplay()
        {
            bool buySide = MarketData.Chosen(request.SrcPower) || MarketData.Chosen(request.SrcState);
            bool sellSide = MarketData.Chosen(request.DstPower) || MarketData.Chosen(request.DstState);
            if (!buySide && !sellSide) return null;
            if (anywhere) return "Powerplay filters need a \"Near me\" search, so they were not used.";
            try
            {
                if (buySide) buyPower = await ask.Powerplay(request.StarSystem!, request.SrcPower, request.SrcState, request.BuyLy);
                if (sellSide) sellPower = await ask.Powerplay(request.StarSystem!, request.DstPower, request.DstState, Math.Min(500, request.BuyLy + request.MaxLy));
            }
            catch (Exception problem) when (problem is not OperationCanceledException)
            {
                // the list message shows a text that starts with "Powerplay" as it is
                throw new InvalidOperationException("Powerplay data from Spansh could not be loaded: " + MarketData.Reason(problem));
            }
            var parts = new List<string>();
            if (buyPower is not null) parts.Add(Count(buyPower, "buy"));
            if (sellPower is not null) parts.Add(Count(sellPower, "sell"));
            return "Powerplay filter: " + string.Join(", ", parts);

            static string Count(PowerSet set, string doing) =>
                Invariant($"{set.Systems.Count} systems to {doing} in") + (set.Systems.Count < set.Total ? Invariant($" (nearest of {set.Total})") : "");
        }

        /// <summary>Step 1, "near" only: the cheapest station for each commodity on sale in the system the player is in.</summary>
        async Task<Dictionary<string, Offer>> ForSaleHere()
        {
            var local = new Dictionary<string, Offer>(StringComparer.OrdinalIgnoreCase);
            if (anywhere || !Allowed(buyPower, request.StarSystem!)) return local;
            foreach (Offer offer in await ask.Offers(SystemList(request.StarSystem!, "exports"), 10))
            {
                if (offer.BuyPrice > 0 && offer.Stock >= minSupply && Keep(offer)
                    && (!local.TryGetValue(offer.Commodity, out Offer? cheapest) || offer.BuyPrice < cheapest.BuyPrice))
                    local[offer.Commodity] = offer;
            }
            return local;
        }

        /// <summary>
        /// Step 2, "near" only: for every commodity that is really traded, which stations within
        /// reach pay well for it, best price first. This is what finds a cheap commodity with a
        /// local price spike, and not only the metals that are valuable everywhere.
        /// </summary>
        async Task<(Dictionary<string, List<Offer>> Sellers, int Scanned)> MarketScan()
        {
            var sellers = new Dictionary<string, List<Offer>>(StringComparer.OrdinalIgnoreCase);
            if (anywhere) return (sellers, 0);
            int reach = Math.Min(500, request.BuyLy + request.MaxLy);
            var asked = new List<(string Id, Task<IReadOnlyList<Offer>> Offers)>();
            foreach ((string id, Price price) in overview.Prices)
            {
                if (price.MinBuy <= 0 || price.Stock < 1000 || price.Demand < 1000 || price.MaxSell - price.MinBuy < 1500) continue;
                // only stations paying at least 1,000 cr more than the cheapest it is sold for anywhere: less is not worth a trip
                asked.Add((id, ask.Offers(Invariant($"{here}/commodity/name/{id}/nearby/imports?maxDistance={reach}&maxDaysAgo={days}&minVolume={minDemand}&minPrice={Convert.ToInt64(price.MinBuy + 1000)}{carriers}"), 10)));
            }
            foreach ((string id, Task<IReadOnlyList<Offer>> offers) in asked)
            {
                List<Offer> kept = Sellers(await offers, sellPower);
                if (kept.Count > 0) sellers[id] = kept;
            }
            return (sellers, asked.Count);
        }

        /// <summary>Step 3: which commodities to look at closely, most promising first.</summary>
        List<string> Pick(Dictionary<string, Offer> local, Dictionary<string, List<Offer>> sellers)
        {
            var picked = new List<string>();
            void Add(string id) { if (!picked.Contains(id, StringComparer.OrdinalIgnoreCase)) picked.Add(id); }

            // a) the best of what is on sale right where the player is
            foreach ((string id, _) in Best(local.Select(l => (l.Key, (sellers.TryGetValue(l.Key, out List<Offer>? wanted) ? wanted[0].SellPrice : 0.0) - l.Value.BuyPrice)), 6)) Add(id);

            // b) commodities a station near the player pays far more for than they usually cost to buy
            //    (the 16 biggest spikes are looked at; those already picked add nothing)
            double Usual(string id)
            {
                if (local.TryGetValue(id, out Offer? sold)) return sold.BuyPrice;
                Price price = overview.Prices.GetValueOrDefault(id);
                return price.AvgBuy > 0 ? price.AvgBuy : price.MinBuy;
            }
            foreach ((string id, _) in Best(sellers.Select(s => (s.Key, s.Value[0].SellPrice - Usual(s.Key))), 16)) Add(id);

            // c) the galaxy's usual big earners (precious metals and the like), which are often cheap somewhere near
            int limit = anywhere ? 10 : request.BuyLy > 0 ? 28 : picked.Count;
            foreach ((string id, Price price) in overview.Prices
                .Where(p => p.Value.MinBuy > 0 && p.Value.Stock > 10000 && p.Value.Demand > 10000)
                .OrderByDescending(p => p.Value.AvgSell - p.Value.MinBuy))
            {
                if (picked.Count >= limit) break;
                Add(id);
            }
            return picked;

            static IEnumerable<(string Id, double Gain)> Best(IEnumerable<(string Id, double Gain)> scored, int most) =>
                scored.Where(x => x.Gain > 0).OrderByDescending(x => x.Gain).Take(most);
        }

        /// <summary>
        /// Step 4: the best few places to buy each picked commodity, one per system: the station that
        /// earns most for a full hold (a station 2 cr cheaper with only 150 t in stock is worse than
        /// one with 40,000 t).
        /// </summary>
        async Task<Dictionary<string, List<Offer>>> PlacesToBuy(List<string> picked, Dictionary<string, Offer> local, Dictionary<string, List<Offer>> sellers)
        {
            var asked = new Dictionary<string, Task<IReadOnlyList<Offer>>>();
            foreach (string id in picked)
            {
                if (anywhere)
                    asked[id] = ask.Offers(Invariant($"{MarketData.Ardent}/commodity/name/{id}/exports?maxDaysAgo={days}&minVolume={minSupply}{carriers}"), 15);
                else if (request.BuyLy > 0 && sellers.TryGetValue(id, out List<Offer>? wanted))
                    // nothing dearer than the best price it sells for near the player
                    asked[id] = ask.Offers(Invariant($"{here}/commodity/name/{id}/nearby/exports?maxDistance={request.BuyLy}&maxDaysAgo={days}&minVolume={minSupply}&maxPrice={wanted[0].SellPrice - 1}{carriers}"), 10);
            }

            var buys = new Dictionary<string, List<Offer>>();
            int hold = cargo > 0 ? cargo : 100;
            foreach (string id in picked)
            {
                IEnumerable<Offer> offers = asked.TryGetValue(id, out Task<IReadOnlyList<Offer>>? answer) ? await answer : [];
                offers = offers.Where(o => o.BuyPrice > 0 && o.Stock >= minSupply && Keep(o) && Allowed(buyPower, o.StarSystem));
                if (local.TryGetValue(id, out Offer? sold)) offers = offers.Append(sold);

                double bestSell = sellers.TryGetValue(id, out List<Offer>? wanted) ? wanted[0].SellPrice : 0;
                double usualSell = overview.Prices.GetValueOrDefault(id).AvgSell;
                var bySystem = new Dictionary<string, (double Value, Offer Offer)>(StringComparer.OrdinalIgnoreCase);
                foreach (Offer offer in offers)
                {
                    double sellFor = bestSell > 0 ? bestSell : Math.Max(usualSell, offer.MeanPrice);
                    double value = (sellFor - offer.BuyPrice) * Math.Min(hold, offer.Stock);
                    if (!bySystem.TryGetValue(offer.StarSystem, out (double Value, Offer Offer) best) || value > best.Value) bySystem[offer.StarSystem] = (value, offer);
                }
                buys[id] = [.. bySystem.Values.OrderByDescending(b => b.Value).Take(anywhere ? 4 : 6).Select(b => b.Offer)];
            }
            return buys;
        }

        /// <summary>
        /// Step 5: pairs every place to buy with the best places to sell within MaxLy of it, and
        /// keeps the three best trades of each commodity, each from another place to buy or sell.
        /// </summary>
        async Task<List<TradeRow>> Pair(List<string> picked, Dictionary<string, List<Offer>> buys, Dictionary<string, List<Offer>> sellers)
        {
            var asked = new Dictionary<(string Id, string System), Task<IReadOnlyList<Offer>>>();
            if (anywhere)
            {
                foreach (string id in picked)
                    foreach (Offer buy in buys[id])
                        asked[(id, buy.StarSystem)] = ask.Offers(Invariant($"{MarketData.SystemAddress(buy.StarSystem)}/commodity/name/{id}/nearby/imports?maxDistance={Math.Min(500, request.MaxLy)}&maxDaysAgo={days}&minVolume={minDemand}{carriers}"), 15);
            }

            var rows = new List<TradeRow>();
            foreach (string id in picked)
            {
                var pairs = new List<TradeRow>();
                foreach (Offer buy in buys[id])
                {
                    List<Offer> sells = anywhere ? Sellers(await asked[(id, buy.StarSystem)], null) : sellers.GetValueOrDefault(id) ?? [];
                    int found = 0;
                    foreach (Offer sell in sells)
                    {
                        if (sell.SellPrice <= buy.BuyPrice) break;   // best price first, so nothing further on pays more
                        if (sell.MarketId == buy.MarketId) continue;
                        double ly = ShipMath.Distance([buy.X, buy.Y, buy.Z], [sell.X, sell.Y, sell.Z]);
                        if (ly > request.MaxLy) continue;
                        pairs.Add(Trade(id, buy, sell, ly));
                        if (++found >= 25) break;
                    }
                }
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int kept = 0;
                foreach (TradeRow pair in Ranked(pairs, pair => pair.ProfitPerTrip))
                {
                    if (used.Contains(pair.BuyStation) && used.Contains(pair.SellStation)) continue;
                    used.Add(pair.BuyStation);
                    used.Add(pair.SellStation);
                    rows.Add(pair);
                    if (++kept >= 3) break;
                }
            }
            return rows;
        }

        TradeRow Trade(string id, Offer buy, Offer sell, double ly)
        {
            int units = cargo > 0 ? Math.Min(cargo, Math.Min(buy.Stock, sell.Demand)) : 0;
            int profit = sell.SellPrice - buy.BuyPrice;
            // A first guess at the time (out full, back empty), so that a short route beats a long one that pays a few credits more.
            double seconds = TripSeconds(ShipMath.JumpsFor(ly, jumpFull), returns ? ShipMath.JumpsFor(ly, jumpEmpty) : 0, sell.StarLs, buy.StarLs);
            return SellSide(sell, new TradeRow
            {
                CommodityId = id, Commodity = GameData.CommodityName(id),
                BuyStation = buy.Station, BuySystem = buy.StarSystem, BuyMarketId = buy.MarketId, BuyType = buy.Type, BuyPad = StationFilter.PadLetter(buy.Pad),
                BuyStarLs = buy.StarLs ?? 0, BuyX = buy.X, BuyY = buy.Y, BuyZ = buy.Z, BuyDistanceLy = buy.Distance ?? 0,
                BuyPrice = buy.BuyPrice, Stock = buy.Stock, BuyPower = PowerText(buyPower, buy.StarSystem), SellPower = PowerText(sellPower, sell.StarSystem),
                DistanceLy = Math.Round(ly, 1), ProfitPerTon = profit, Units = units, ProfitPerTrip = (long)profit * units,
                AgeHours = Math.Max(Hours(buy), Hours(sell)),
                PerHour = Convert.ToInt64((double)profit * Math.Max(1, units) / seconds * 3600),   // with the hold not known, per ton
            });
        }

        /// <summary>
        /// Step 6, round trips only: a load to carry back, for the 15 trades that earn most per trip:
        /// what the selling station has for sale that the buying station wants.
        /// </summary>
        async Task ReturnLoads(List<TradeRow> rows)
        {
            List<TradeRow> top = [.. rows.OrderByDescending(r => r.ProfitPerTrip).Take(15)];
            var asked = new Dictionary<string, Task<IReadOnlyList<Offer>>>();
            foreach (TradeRow row in top)
            {
                foreach (string url in (string[])[SystemList(row.SellSystem, "exports"), SystemList(row.BuySystem, "imports")])
                    if (!asked.ContainsKey(url)) asked[url] = ask.Offers(url, 10);
            }
            foreach (TradeRow row in top)
            {
                var wants = new Dictionary<string, Offer>(StringComparer.OrdinalIgnoreCase);
                foreach (Offer wanted in await asked[SystemList(row.BuySystem, "imports")])
                {
                    if (wanted.MarketId == row.BuyMarketId && wanted.SellPrice > 0 && wanted.Demand >= minDemand && Hours(wanted) <= request.MaxAgeHours)
                        wants[wanted.Commodity] = wanted;
                }
                long best = -1;
                foreach (Offer sold in await asked[SystemList(row.SellSystem, "exports")])
                {
                    if (sold.MarketId != row.SellMarketId || sold.BuyPrice <= 0 || sold.Stock < minSupply || !Keep(sold)) continue;
                    if (!wants.TryGetValue(sold.Commodity, out Offer? wanted)) continue;
                    int perTon = wanted.SellPrice - sold.BuyPrice;
                    if (perTon <= 0) continue;
                    int units = cargo > 0 ? Math.Min(cargo, Math.Min(sold.Stock, wanted.Demand)) : 0;
                    long profit = (long)perTon * units;
                    if (profit <= best) continue;   // with the hold not known every load counts 0, so the first one found stays
                    best = profit;
                    (row.ReturnId, row.ReturnProfitPerTon, row.ReturnUnits, row.ReturnProfit) = (sold.Commodity, perTon, units, profit);
                }
            }
        }

        /// <summary>The station passes the player's station filter and its price is new enough; a price with no date counts as too old.</summary>
        bool Keep(Offer offer) => stations.Keeps(offer.Type, offer.Pad, offer.StarLs) && Hours(offer) <= request.MaxAgeHours;

        /// <summary>The offers a ship could sell to, best price first. The service's own volume filter lets "demand 0" through, so it is applied again here.</summary>
        List<Offer> Sellers(IReadOnlyList<Offer> offers, PowerSet? power) =>
            [.. offers.Where(o => o.Demand >= minDemand && o.SellPrice > 0 && Keep(o) && Allowed(power, o.StarSystem)).OrderByDescending(o => o.SellPrice)];

        /// <summary>
        /// Best first: by profit per hour, or by what the trip earns, then by profit per ton. Many
        /// stations pay exactly the same, so trips that earn the same are common: of those the
        /// quicker one comes first (the Haul Overlay left that to chance).
        /// </summary>
        IEnumerable<TradeRow> Ranked(IEnumerable<TradeRow> trades, Func<TradeRow, long> perTrip) =>
            trades.OrderByDescending(trade => byTrip ? perTrip(trade) : trade.PerHour).ThenByDescending(trade => trade.ProfitPerTon).ThenByDescending(trade => trade.PerHour);

        /// <summary>The best 40, numbered, with the age put into words.</summary>
        static List<TradeRow> Listed(IEnumerable<TradeRow> best)
        {
            List<TradeRow> rows = [.. best.Take(MostRows)];
            for (int i = 0; i < rows.Count; i++) (rows[i].Rank, rows[i].Updated) = (i + 1, AgeText(rows[i].AgeHours));
            return rows;
        }

        static TradeRow SellSide(Offer sell, TradeRow row)
        {
            (row.SellStation, row.SellSystem, row.SellMarketId, row.SellType, row.SellPad) = (sell.Station, sell.StarSystem, sell.MarketId, sell.Type, StationFilter.PadLetter(sell.Pad));
            (row.SellStarLs, row.SellX, row.SellY, row.SellZ) = (sell.StarLs ?? 0, sell.X, sell.Y, sell.Z);
            (row.SellPrice, row.Demand) = (sell.SellPrice, sell.Demand);
            return row;
        }

        double Hours(Offer offer) => offer.Updated is { } at ? (ask.Now - at).TotalHours : 99999;

        /// <summary>
        /// The time one trip takes, in seconds: every jump, supercruise to the station at each end,
        /// and two stops. One way, the clock stops when the cargo is sold: nothing is flown back, and
        /// only the selling end's supercruise counts.
        /// </summary>
        double TripSeconds(int jumpsOut, int jumpsBack, double? sellStarLs, double? buyStarLs) =>
            (jumpsOut + jumpsBack) * ShipMath.SecondsPerJump + Cruise(sellStarLs) + (returns ? Cruise(buyStarLs) : 0) + 2 * ShipMath.SecondsPerStop;

        static double Cruise(double? lightSeconds) => ShipMath.SupercruiseSeconds(lightSeconds ?? 0);

        static bool Allowed(PowerSet? power, string system) => power is null || power.Systems.ContainsKey(system);

        /// <summary>"Li Yong-Rui, Fortified" for a system inside a Powerplay filter; "" when that side has no filter.</summary>
        static string PowerText(PowerSet? power, string system) =>
            power is not null && power.Systems.TryGetValue(system, out (string Power, string State) found)
                ? string.Join(", ", new[] { found.Power, found.State }.Where(text => text.Length > 0))
                : "";

        /// <summary>The address of everything one system sells ("exports") or buys ("imports").</summary>
        string SystemList(string system, string kind) => Invariant($"{MarketData.SystemAddress(system)}/commodities/{kind}?maxDaysAgo={days}{carriers}");
    }
}
