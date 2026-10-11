using EliteStudio.App;
using EliteStudio.Core;
using static System.FormattableString;

namespace EliteStudio.Market;

/// <summary>One line of a station's price list.</summary>
/// <param name="Buy">Credits per ton the player pays there; 0 when the station does not sell it.</param>
/// <param name="Sell">Credits per ton the station pays the player; 0 when it does not buy it.</param>
/// <param name="TakeThere">Credits a ton made by buying it where the player is docked and selling it at this station; null when that makes nothing.</param>
/// <param name="BringBack">Credits a ton made by buying it at this station and selling it where the player is docked; null when that makes nothing.</param>
public sealed record PriceRow(string Name, int Buy, int Supply, int Sell, int Demand, int? TakeThere, int? BringBack);

/// <summary>A station's price list, for "Find by name".</summary>
/// <param name="Mine">True when the list is the one the player saw there themselves; false when it is the service's.</param>
/// <param name="Updated">When the list was seen or reported (UTC); null when not known.</param>
/// <param name="Here">The station the player is docked at, whose prices the two profit columns are worked out against; null when there is none to compare with.</param>
public sealed record StationPrices(long MarketId, string Station, string System, bool Mine, DateTime? Updated, string? Here, IReadOnlyList<PriceRow> Rows);

// The part of Finder that finds a station by its name and shows its price list (the owner, 11 October
// 2026: "add a function to search via station to the find by name tab"). Spansh's stations search
// takes a name and sends each station with its whole price list, so one question answers both. The
// player's own visits (StationBook) come first where there are any: a list they saw themselves is
// shown in place of Spansh's when it is the newer one, and a station Spansh does not know is still found.
sealed partial class Finder
{
    const int MostNamed = 10;   // stations with a name like the one typed that are listed

    /// <summary>A station's price list as a service sent it this session.</summary>
    sealed record Listed(string Station, string System, DateTime? Updated, IReadOnlyList<PriceRow> Rows);

    readonly Dictionary<long, Listed> listed = [];   // by market id

    /// <summary>The stations with a name like the one typed: the player's own visits first, then Spansh's, its best match first.</summary>
    async Task<FindAnswer> Named(FindRequest request, string from, double[]? here, MarketData.Asking ask)
    {
        string typed = request.Name.Trim();
        if (typed.Length == 0) throw new FindProblem("Type a station's name, or a part of it.");
        string letters = Letters(typed);
        var rows = new List<FoundPlace>(
            from visit in book?.Visits ?? []
            where letters.Length > 0 && Letters(visit.Station).Contains(letters, StringComparison.Ordinal)
            select new FoundPlace(visit.Station, visit.System, here is not null && visit.At is not null ? Math.Round(ShipMath.Distance(here, visit.At), 2) : null,
                null, "", Shown(visit.Type), "", null, null, visit.SeenUtc, visit.MarketId));
        int own = rows.Count;

        var question = new Dictionary<string, object>
        {
            ["filters"] = new Dictionary<string, object> { ["name"] = new { value = typed } }, ["size"] = MostNamed, ["page"] = 0,
        };
        if (here is null) question["reference_system"] = from;
        else question["reference_coords"] = new { x = here[0], y = here[1], z = here[2] };
        DateTime asked = ask.Now;
        string? note = null;
        try
        {
            (StationPage page, asked) = await Page(question, here is null ? from : null, ask);
            foreach (Station station in page.Results?.OfType<Station>() ?? [])
            {
                if (station.Name is not { Length: > 0 } name) continue;
                long id = station.MarketId ?? 0;
                DateTime? reported = MarketData.Time(station.MarketUpdatedAt);
                if (id != 0 && station.Market is { Count: > 0 } goods)
                {
                    listed[id] = new Listed(name, station.SystemName ?? "", reported,
                    [
                        .. from good in goods.OfType<Good>()
                           where good.Commodity is { Length: > 0 }
                           select new PriceRow(good.Commodity!, Whole(good.BuyPrice), Whole(good.Supply), Whole(good.SellPrice), Whole(good.Demand), null, null),
                    ]);
                }
                int at = id == 0 ? -1 : rows.FindIndex(row => row.MarketId == id);
                int? pad = station.LargePads > 0 ? 3 : station.MediumPads > 0 ? 2 : station.SmallPads > 0 ? 1 : null;
                // A station the player visited is listed once, with what Spansh knows of it besides; the time stays the newer of the two.
                var found = new FoundPlace(name, station.SystemName ?? "", station.Distance is { } ly ? Math.Round(ly, 2) : null, station.DistanceToArrival,
                    StationFilter.PadLetter(pad), station.Type ?? "", "", null, null, reported ?? MarketData.Time(station.UpdatedAt), id);
                if (at < 0) rows.Add(found);
                else rows[at] = found with { Updated = rows[at].Updated > found.Updated || found.Updated is null ? rows[at].Updated : found.Updated };
            }
        }
        catch (Exception problem) when (Lost(problem) && own > 0)
        {
            // The player's own lists need no service.
            Log.Error($"Find station \"{typed}\": Spansh gave no answer, so only the player's own visits are listed", problem);
            note = $"Spansh gave no answer ({MarketData.Reason(problem)}), so these are only the stations whose market you opened yourself.";
        }
        note ??= rows.Count == 0 ? $"No station with a name like \"{typed}\" is known. Check its spelling, or type less of it."
            : "Press Prices on a station to see what it sells and buys. The best match is listed first.";
        return new FindAnswer($"Stations called \"{typed}\"", from, rows, SpanshName, null, note, null, asked);

        static int Whole(double? number) => (int)Math.Clamp(Math.Round(number ?? 0), 0, int.MaxValue);
    }

    /// <summary>
    /// The price list of a station found by name: the one the player saw there themselves, unless
    /// the service's is newer. While the player is docked at another station whose prices are
    /// known, every line says what a ton makes carried there and carried back.
    /// </summary>
    public StationPrices Prices(long marketId)
    {
        StationBook.Visit? own = book?.Find(marketId);
        listed.TryGetValue(marketId, out Listed? theirs);
        if (own is null && theirs is null) throw new FindProblem("Studio has no price list for that station. Search for it by name first.");
        bool mine = own is not null && (theirs?.Updated is not { } reported || own.SeenUtc >= reported);
        IEnumerable<PriceRow> rows = mine ? own!.Items.Select(Line) : theirs!.Rows;

        // What the station the player is docked at charges and pays: its list as the game shows it now, else as they saw it last.
        StationBook.Visit? docked = game.Docked && game.MarketId != 0 && game.MarketId != marketId ? book?.Find(game.MarketId) : null;
        Dictionary<string, PriceRow>? here = docked?.Items.Select(Line).GroupBy(row => Letters(row.Name)).ToDictionary(group => group.Key, group => group.First());
        PriceRow[] shown =
        [
            .. from row in rows
               where row.Buy > 0 || row.Sell > 0
               let near = here?.GetValueOrDefault(Letters(row.Name))
               orderby row.Name
               select row with
               {
                   TakeThere = near is { Buy: > 0, Supply: > 0 } && row is { Sell: > 0, Demand: > 0 } && row.Sell > near.Buy ? row.Sell - near.Buy : null,
                   BringBack = near is { Sell: > 0, Demand: > 0 } && row is { Buy: > 0, Supply: > 0 } && near.Sell > row.Buy ? near.Sell - row.Buy : null,
               },
        ];
        Log.Info(Invariant($"Prices of {(mine ? own!.Station : theirs!.Station)} ({marketId}): {shown.Length} lines, {(mine ? "the player's own" : "Spansh's")}"));
        return mine ? new StationPrices(marketId, own!.Station, own.System, true, own.SeenUtc, docked?.Station, shown)
            : new StationPrices(marketId, theirs!.Station, theirs.System, false, theirs.Updated, docked?.Station, shown);

        static PriceRow Line(MarketItem item) =>
            new(item.Name.Length > 0 ? item.Name : GameData.CommodityName(item.Id), item.BuyPrice, item.Stock, item.SellPrice, item.Demand, null, null);
    }
}
