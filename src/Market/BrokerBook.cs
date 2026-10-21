using EliteStudio.App;

namespace EliteStudio.Market;

/// <summary>One station's technology broker as the player saw it in game.</summary>
/// <param name="MarketId">The game's number for the station, which Spansh gives too.</param>
/// <param name="Kind">"Human" or "Guardian", in Spansh's words for them.</param>
public sealed record BrokerSeen(long MarketId, string Station, string System, string Kind, DateTime MarkedUtc);

/// <summary>The "brokers" section of settings.json: every broker the player marked, the oldest mark first.</summary>
public sealed class BrokerSettings
{
    public List<BrokerSeen> Seen { get; set; } = [];
}

/// <summary>
/// The kinds of technology broker the player saw themselves. No service records which kind a
/// station has: Spansh's kind is a guess from the station's main economy, and the owner flew to
/// two "Guardian" brokers that were human (FindStations.cs). The owner, 20 October 2026: "let me
/// mark a station's broker as Human or Guardian, remember it, and leave stations I marked Human
/// out of the Guardian search". So a mark is the one sure word Studio has, and every broker search
/// and station sheet goes by it. Kept by the station's market id, which a station keeps when it is
/// renamed. Searches run on the UI thread in the app and on any thread in the tests, hence the lock.
/// </summary>
public sealed class BrokerBook
{
    /// <summary>The kinds there are, as Spansh spells them.</summary>
    public static readonly string[] Kinds = ["Human", "Guardian"];

    readonly Settings settings;
    readonly Dictionary<long, BrokerSeen> seen;

    public BrokerBook(Settings settings)
    {
        this.settings = settings;
        // A mark typed into the file by hand with a kind there is not, or no station, is not one.
        seen = settings.Get<BrokerSettings>("brokers").Seen
            .Where(mark => mark is { MarketId: > 0 } && Kinds.Contains(mark.Kind))
            .GroupBy(mark => mark.MarketId).ToDictionary(group => group.Key, group => group.Last());
    }

    /// <summary>The kind the player saw at the station; null when they have not marked it.</summary>
    public string? KindAt(long marketId)
    {
        lock (seen) return seen.GetValueOrDefault(marketId)?.Kind;
    }

    /// <summary>Every station the player saw a broker of this kind at.</summary>
    public BrokerSeen[] Of(string kind)
    {
        lock (seen) return [.. seen.Values.Where(mark => mark.Kind == kind)];
    }

    /// <summary>Marks a station's broker, or takes the mark away (kind ""), and saves at once.</summary>
    /// <param name="kind">One of <see cref="Kinds"/>, or "".</param>
    public void Mark(long marketId, string station, string system, string kind, DateTime utcNow)
    {
        BrokerSeen[] all;
        lock (seen)
        {
            if (kind.Length == 0) seen.Remove(marketId);
            else seen[marketId] = new BrokerSeen(marketId, station, system, kind, utcNow);
            all = [.. seen.Values.OrderBy(mark => mark.MarkedUtc)];
        }
        settings.Set("brokers", new BrokerSettings { Seen = [.. all] });
    }
}
