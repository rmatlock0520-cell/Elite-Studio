using EliteStudio.Core;

namespace EliteStudio.Market;

/// <summary>
/// One trade as the overlay and the trade list show it: buy a commodity at one station, sell it at
/// another, optionally bring a second commodity back. Prices are credits per ton, distances light
/// years, station distances from the star light seconds.
/// </summary>
public sealed class TradeRow
{
    public int Rank { get; set; }                   // 1-based position in the search's own order
    public string CommodityId { get; set; } = "";   // e.g. "gold"
    public string Commodity { get; set; } = "";     // display name
    public string BuyStation { get; set; } = "";
    public string BuySystem { get; set; } = "";
    public double BuyDistanceLy { get; set; }       // from the player to the buy system; kept current as the player moves
    public string BuyType { get; set; } = "";       // station type as the data service names it
    public string BuyPad { get; set; } = "";        // "S", "M", "L" or ""
    public double BuyStarLs { get; set; }
    public int BuyPrice { get; set; }
    public int Stock { get; set; }                  // tons for sale
    public bool BuyHere { get; set; }               // the player is docked at the buy station
    public string SellStation { get; set; } = "";
    public string SellSystem { get; set; } = "";
    public string SellType { get; set; } = "";
    public string SellPad { get; set; } = "";
    public double SellStarLs { get; set; }
    public int SellPrice { get; set; }
    public int Demand { get; set; }                 // tons wanted
    public double DistanceLy { get; set; }          // buy system to sell system
    public int ProfitPerTon { get; set; }
    public int Units { get; set; }                  // tons this ship would carry: min(hold, stock, demand)
    public long ProfitPerTrip { get; set; }
    public double AgeHours { get; set; }            // the older of the two prices
    public string Updated { get; set; } = "";       // AgeHours as text: "just now", "5h ago", "3d ago"
    public long BuyMarketId { get; set; }
    public long SellMarketId { get; set; }
    public double BuyX { get; set; }
    public double BuyY { get; set; }
    public double BuyZ { get; set; }
    public double SellX { get; set; }
    public double SellY { get; set; }
    public double SellZ { get; set; }
    public string ReturnId { get; set; } = "";      // commodity for the way back, or ""
    public string Return { get; set; } = "";        // its display name, or ""
    public int ReturnProfitPerTon { get; set; }
    public int ReturnUnits { get; set; }
    public long ReturnProfit { get; set; }
    public long RoundTripProfit { get; set; }
    public double Minutes { get; set; }             // estimated time for the trip (round or one way)
    public long PerHour { get; set; }
    public int JumpsToBuy { get; set; }             // from the player, empty hold; kept current as the player moves
    public int JumpsOut { get; set; }               // buy to sell, full hold
    public int JumpsBack { get; set; }              // sell to buy; 0 for one way
    public string BuyPower { get; set; } = "";      // Powerplay text, or ""
    public string SellPower { get; set; } = "";
    public bool OneWay { get; set; }

    /// <summary>Two rows are the same trade when the commodity and both markets match.</summary>
    public bool IsSameTrade(TradeRow? other) =>
        other is not null && BuyMarketId == other.BuyMarketId && SellMarketId == other.SellMarketId
        && string.Equals(CommodityId, other.CommodityId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Everything one search needs to know. It carries no reference to live state, so a search can run while the player flies on.</summary>
public sealed record SearchRequest
{
    public string Mode { get; init; } = "near";          // near | anywhere | lookup
    public string? StarSystem { get; init; }             // where the player is
    public int Pad { get; init; } = 3;                   // smallest landing pad the ship needs: 1 small, 2 medium, 3 large
    public int BuyLy { get; init; } = 15;                // near: buy within this distance of the player
    public int MaxLy { get; init; } = 30;                // longest buy-to-sell distance
    public double MaxAgeHours { get; init; } = 48;
    public double MaxStarLs { get; init; } = 5000;       // 0 = any
    public int MinSupply { get; init; } = 100;
    public int MinDemand { get; init; } = 100;
    public bool Carriers { get; init; }                  // include fleet carriers
    public string Surface { get; init; } = "odyssey";    // odyssey | horizons | none
    public int Cargo { get; init; }                      // tons the hold takes; 0 = not known
    public string SrcPower { get; init; } = "any";
    public string DstPower { get; init; } = "any";
    public string SrcState { get; init; } = "any";
    public string DstState { get; init; } = "any";
    public string RankBy { get; init; } = "hour";        // hour | trip
    public bool Returns { get; init; } = true;           // round trip (true) or one way
    public double Jump { get; init; }                    // light years per jump with a full hold; 0 = not known
    public double JumpEmpty { get; init; }               // ... with an empty hold

    // "Where can I sell this?" (Mode = lookup)
    public string LookItem { get; init; } = "";          // commodity id
    public int LookCount { get; init; }                  // tons to sell
    public int LookPaid { get; init; }                   // credits per ton paid; 0 = not known
    public string LookFrom { get; init; } = "";          // shown as where the cargo is, e.g. "Your cargo hold"
    public string LookRank { get; init; } = "near";      // near | profit
    public int LookLy { get; init; } = 30;
    public StationMarket? LiveMarket { get; init; }      // the docked station's own price list, when the game has written it
}

/// <summary>
/// The station rules of every Studio Market search, in one place: the trade search and "Find by
/// name" both ask here. Building sites take deliveries but do not trade; settlements need Odyssey;
/// a pad size that is not known only suits a small ship; a distance from the star that is not known
/// passes. The surface setting is read without regard to capitals, as names and settings are
/// everywhere in Studio Market.
/// </summary>
/// <param name="Pad">The smallest landing pad that will do: 1 small, 2 medium, 3 large.</param>
/// <param name="MaxStarLs">Light seconds from the star; 0 = any.</param>
/// <param name="Surface">odyssey (all surface stations) | horizons (no settlements) | none.</param>
/// <param name="Carriers">Fleet carriers count as stations.</param>
readonly record struct StationFilter(int Pad, double MaxStarLs, string Surface, bool Carriers)
{
    /// <summary>Stations of this type may be shown. The type is spelt as the game's journal spells it.</summary>
    public bool Allows(string type)
    {
        if (type.Contains("ConstructionDepot", StringComparison.OrdinalIgnoreCase)) return false;
        if (type.Contains("FleetCarrier", StringComparison.OrdinalIgnoreCase) && !Carriers) return false;
        if (type.Equals("OnFootSettlement", StringComparison.OrdinalIgnoreCase) && !SurfaceIs("odyssey")) return false;
        return !(SurfaceIs("none") && new[] { "Crater", "Surface", "OnFoot" }.Any(kind => type.StartsWith(kind, StringComparison.OrdinalIgnoreCase)));
    }

    public bool Keeps(string type, int? pad, double? starLs) =>
        Allows(type) && !(pad is null ? Pad > 1 : pad < Pad) && !(MaxStarLs > 0 && starLs > MaxStarLs);

    /// <summary>A pad size as its letter: "S", "M" or "L"; "" when it is not known.</summary>
    public static string PadLetter(int? pad) => pad switch { 1 => "S", 2 => "M", 3 => "L", _ => "" };

    /// <summary>The pad size a pad setting asks for; null for "auto", which leaves it to the ship.</summary>
    public static int? PadSize(string setting) => setting switch { "S" => 1, "M" => 2, "L" => 3, _ => null };

    bool SurfaceIs(string choice) => string.Equals(Surface, choice, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a search found.</summary>
public sealed record SearchResult
{
    public bool Ok { get; init; } = true;
    public string? Error { get; init; }                         // why it failed, in words a player can read
    public IReadOnlyList<TradeRow> Trades { get; init; } = [];  // best first, at most 40
    public int Exports { get; init; }                           // near: commodities on sale where the player is that pass the filters
    public int Scanned { get; init; }                           // near: commodities the market scan asked about
    public int Candidates { get; init; }                        // commodities looked at closely
    public bool Lookup { get; init; }
    public IReadOnlyList<string> Urls { get; init; } = [];      // every request actually sent, in order (cache hits are not listed)
    public string? StarSystem { get; init; }                    // the system the search was for
    public string? Note { get; init; }                          // extra line for the list message (Powerplay), or null
}

/// <summary>
/// The trade search, as the rest of Studio Market sees it. <see cref="TradeSearch"/> is the real one;
/// tests and the overlay's sample pictures (--render-shots) use stand-ins.
/// </summary>
public interface ITradeSearch
{
    /// <summary>
    /// Runs one search and never throws: a failed or stopped search comes back with Ok = false.
    /// Every row comes numbered (Rank) and worded (Commodity and Return by the bundled name table,
    /// Updated), and every result names the system it was for; the hauling state relies on that
    /// and only keeps BuyHere, BuyDistanceLy and JumpsToBuy current as the player moves.
    /// </summary>
    Task<SearchResult> Run(SearchRequest request, CancellationToken cancel);

    /// <summary>Every traded commodity known from the galaxy price overview (id, display name); empty until a search has loaded it.</summary>
    IReadOnlyList<(string Id, string Name)> Commodities { get; }
}
