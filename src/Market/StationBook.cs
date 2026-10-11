using System.Text.Json;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Market;

/// <summary>
/// The price lists the player has seen with their own eyes. The game writes a station's list to
/// Market.json when the commodities screen is opened, and writes over it at the next station; this
/// keeps the last list of every station, so "what does the station I left buy?" has an answer that
/// is the player's own and not as old as the last visit by somebody else's tool. (The owner,
/// 11 October 2026: "i wana see whats back at the station i left".)
///
/// Kept in stations.json in the data folder, the newest visit first, the oldest dropped after
/// <see cref="Most"/> stations. Lives on the UI thread, like the game state it listens to.
/// </summary>
sealed class StationBook
{
    public const int Most = 300;

    /// <summary>One station's price list as the player saw it.</summary>
    /// <param name="Type">The kind of station as the journal spells it; "" when not known.</param>
    /// <param name="At">Where its system lies, [x, y, z] in light years; null when the game had not said.</param>
    public sealed record Visit(long MarketId, string Station, string System, string Type, double[]? At, DateTime SeenUtc, IReadOnlyList<MarketItem> Items);

    readonly GameState game;
    readonly string file;
    readonly Func<DateTime> utcNow;
    readonly List<Visit> visits;
    StationMarket? taken;   // the list read last, so that the same one is not looked at twice

    /// <param name="file">Where the book is kept; read now, and written each time a list is new.</param>
    public StationBook(GameState game, string file, Func<DateTime>? utcNow = null)
    {
        (this.game, this.file, this.utcNow) = (game, file, utcNow ?? (() => DateTime.UtcNow));
        visits = Load(file);
        game.Changed += Take;
        Take();   // the list on disk when Studio started
    }

    /// <summary>Every station remembered, the one seen last first.</summary>
    public IReadOnlyList<Visit> Visits => visits;

    public Visit? Find(long marketId) => visits.FirstOrDefault(visit => visit.MarketId == marketId);

    void Take()
    {
        if (game.LastMarket is not { MarketId: not 0 } market || ReferenceEquals(market, taken)) return;
        taken = market;
        if (market.Items.Count == 0) return;   // a station without a commodities market
        DateTime seen = market.Seen ?? utcNow();
        Visit? before = Find(market.MarketId);
        if (before is not null && before.SeenUtc >= seen) return;   // the same list as at the last start
        // The game says where the system the player is in lies, which is the list's system while they are still there.
        double[]? at = string.Equals(game.StarSystem, market.StarSystem, StringComparison.OrdinalIgnoreCase) ? game.StarPos ?? before?.At : before?.At;
        if (before is not null) visits.Remove(before);
        visits.Insert(0, new Visit(market.MarketId, market.StationName, market.StarSystem, market.Type.Length > 0 ? market.Type : before?.Type ?? "", at, seen,
            [.. market.Items.Values.OrderBy(item => item.Id, StringComparer.Ordinal)]));
        if (visits.Count > Most) visits.RemoveRange(Most, visits.Count - Most);
        Save();
    }

    void Save()
    {
        try
        {
            // Beside the file and swapped, so a crash mid-save cannot leave half a book. Not indented: 300 lists of 100 lines each.
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(visits, JsonSerializerOptions.Web));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error("The stations' price lists could not be saved; they are kept until Studio closes", problem);
        }
    }

    static List<Visit> Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<List<Visit>>(File.ReadAllText(file), JsonSerializerOptions.Web) ?? [] : [];
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or JsonException)
        {
            // Nothing in it that the next visit to a station does not bring back.
            Log.Error("stations.json could not be read; the remembered price lists start over", problem);
            return [];
        }
    }
}
