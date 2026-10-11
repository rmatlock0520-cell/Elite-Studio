using System.Globalization;
using System.Media;
using EliteStudio.App;
using EliteStudio.Core;
using static System.FormattableString;
using WebResponse = EliteStudio.App.WebResponse;   // System.Net has an older type with the same name

namespace EliteStudio.Market;

/// <summary>
/// One search the sniffer keeps: a commodity to buy or to sell, the price and the tons that make it
/// worth an alert, and the station filters. The owner, 11 October 2026: "most important fields. is the
/// amount of supply, the price, and the ability to turn it off per each saved search."
/// </summary>
public sealed record SavedSearch
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";             // the commodity, as the player reads it
    public string Side { get; init; } = "buy";          // buy | sell
    public long Price { get; init; }                    // buy: this or less; sell: this or more; 0 = any price
    public int? AveragePercent { get; init; }           // the price against the galactic average: -50 to 50 in tens (buy: that or less; sell: that or more); null = not used
    public int Tons { get; init; }                      // buy: tons on sale; sell: tons wanted; 0 = any
    public string Pad { get; init; } = "auto";          // auto (from the ship) | S | M | L
    public int MaxStarLs { get; init; }                 // 0 = any
    public string Surface { get; init; } = "odyssey";   // odyssey | horizons | none
    public bool Carriers { get; init; }
    public int MaxAgeHours { get; init; } = 48;
    public bool On { get; init; } = true;               // searched for every hour
    public bool Sound { get; init; } = true;            // a chime when it is met
}

/// <summary>The "sniffer" section of settings.json.</summary>
public sealed record SnifferSettings
{
    internal const string Section = "sniffer";

    public IReadOnlyList<SavedSearch> Searches { get; init; } = [];
    public int Volume { get; init; } = 60;              // of the chime, 0 to 100
}

/// <summary>One saved search as the page shows it: the search, and what its last run found.</summary>
/// <param name="RanUtc">When it was last run; null when not yet since Studio started.</param>
/// <param name="Found">Stations that meet it. More than none is "met".</param>
/// <param name="BestPrice">The best price among them, at <paramref name="BestStation"/> in <paramref name="BestSystem"/>.</param>
/// <param name="Problem">Why the last run gave no answer, in words for the player; else null.</param>
/// <param name="BestTons">Tons on sale there, or tons wanted there.</param>
/// <param name="BestLy">Light years from the system the player is in to that station's; null when the game has not said where the player is.</param>
/// <param name="Average">The commodity's galactic average price as the last run learned it; null when not known.</param>
public sealed record SniffLine(SavedSearch Search, DateTime? RanUtc, bool Running, int Found, long? BestPrice, int? BestTons, string? BestStation, string? BestSystem, double? BestLy, long? Average, string? Problem);

/// <summary>The Sniffer page's picture.</summary>
/// <param name="Most">There is room for this many saved searches.</param>
/// <param name="EveryMinutes">How often a search that is on is run.</param>
public sealed record SnifferPicture(IReadOnlyList<SniffLine> Searches, int Volume, int Most, int EveryMinutes);

/// <summary>
/// The market sniffer: saved commodity searches that Studio runs by itself once an hour, anywhere in
/// the galaxy, with a chime when one is met. Routes, all under /api/market/sniffer:
///   GET  (itself)       the picture: every saved search and what its last run found; also the "sniffer" event
///   POST save           a <see cref="SavedSearch"/>; with the id of a saved one it replaces that one
///   POST set            {"id":"…","on":true,"sound":false}: the two ticks; either may be left out
///   POST delete         {"id":"…"}
///   POST run            {"id":"…"}: runs it now
///   GET  matches?id=    the stations the last run found, as a "Find by name" answer, measured from where the player is now
///   POST volume         {"volume":60}: how loud the chime is, and plays it
/// All but "matches" answer the picture, or the error shape with words for the player.
/// </summary>
public static class SnifferApi
{
    /// <summary>Registers the routes. Call on the UI thread, which is also where the server runs them.</summary>
    /// <param name="chime">Plays the alert at a volume of 0 to 100; tests pass their own.</param>
    internal static Sniffer Map(WebServer web, GameState game, Hauling hauling, MarketData data, Settings settings, Func<DateTime>? utcNow = null, Action<int>? chime = null)
    {
        var sniffer = new Sniffer(game, hauling, data, settings, utcNow ?? (() => DateTime.UtcNow), chime ?? Chime.Play);
        sniffer.Changed += () => web.Send("sniffer", sniffer.Picture());
        const string at = "/api/market/sniffer";
        web.Map("GET", at, _ => WebResponse.Json(sniffer.Picture()));
        web.Map("POST", at + "/save", request => Done(() => sniffer.Save(request.Read<SavedSearch>())));
        web.Map("POST", at + "/set", request => Done(() => { SniffRequest asked = request.Read<SniffRequest>(); sniffer.Set(asked.Id, asked.On, asked.Sound); }));
        web.Map("POST", at + "/delete", request => Done(() => sniffer.Delete(request.Read<SniffRequest>().Id)));
        web.Map("POST", at + "/volume", request => Done(() => sniffer.SetVolume(request.Read<SniffRequest>().Volume)));
        web.Map("POST", at + "/run", request => FindApi.Answered(async () => { await sniffer.Run(request.Read<SniffRequest>().Id); return sniffer.Picture(); }));
        web.Map("GET", at + "/matches", request => FindApi.Answered(() => Task.FromResult(sniffer.Matches(request.Query.GetValueOrDefault("id") ?? ""))));
        return sniffer;

        WebResponse Done(Action change)
        {
            try { change(); }
            catch (FindProblem problem) { return WebResponse.Error(problem.Message, problem.Status); }
            return WebResponse.Json(sniffer.Picture());
        }
    }
}

/// <summary>What the page sends about a saved search, besides the search itself.</summary>
sealed class SniffRequest
{
    public string Id { get; set; } = "";
    public bool? On { get; set; }
    public bool? Sound { get; set; }
    public int Volume { get; set; }
}

/// <summary>
/// The saved searches, their last answers, and the clockwork that runs them. Everything here happens
/// on the thread that calls <see cref="Tick"/> and the routes, which in the app is the UI thread.
/// </summary>
sealed class Sniffer
{
    /// <summary>Room for this many saved searches: each one is a question an hour to a free service.</summary>
    public const int Most = 10;

    const int EveryMinutes = 60;       // the owner: "auto search once each hour"
    const double ApartSeconds = 60;    // between two searches Studio starts by itself, so they do not all go out at once
    const double OfferMinutes = 5;     // "Run now" pressed twice gets the remembered answer

    /// <param name="Offers">The stations that met the search, best price first.</param>
    /// <param name="Mean">The commodity's galactic average price, as the service passes on the game's own figure; 0 when not known.</param>
    sealed record Result(DateTime Ran, IReadOnlyList<Offer> Offers, long Mean, string? Problem);

    readonly GameState game;
    readonly Hauling hauling;
    readonly MarketData data;
    readonly Settings settings;
    readonly Func<DateTime> utcNow;
    readonly Action<int> chime;
    readonly Dictionary<string, Result> results = [];
    readonly HashSet<string> running = [];
    SnifferSettings saved;
    DateTime lastStart;

    /// <summary>The search <see cref="Tick"/> started last, for tests to wait on.</summary>
    internal Task LastRun { get; private set; } = Task.CompletedTask;

    public Sniffer(GameState game, Hauling hauling, MarketData data, Settings settings, Func<DateTime> utcNow, Action<int> chime)
    {
        (this.game, this.hauling, this.data, this.settings, this.utcNow, this.chime) = (game, hauling, data, settings, utcNow, chime);
        SnifferSettings read = settings.Get<SnifferSettings>(SnifferSettings.Section);
        // A hand-edited file may hold anything: every search is brought inside what a search can be, and two may not share an id.
        saved = new SnifferSettings
        {
            Searches = [.. (read.Searches ?? []).OfType<SavedSearch>().Where(search => !string.IsNullOrWhiteSpace(search.Name)).Select(Tidied)
                .DistinctBy(search => search.Id).Take(Most)],
            Volume = Math.Clamp(read.Volume, 0, 100),
        };
    }

    /// <summary>Raised after anything the picture shows has changed.</summary>
    public event Action? Changed;

    public SnifferPicture Picture() => new(
        [.. saved.Searches.Select(search =>
        {
            Result? result = results.GetValueOrDefault(search.Id);
            Offer? best = result?.Offers.FirstOrDefault();
            return new SniffLine(search, result?.Ran, running.Contains(search.Id), result?.Offers.Count ?? 0,
                best is null ? null : Selling(search) ? best.SellPrice : best.BuyPrice, best is null ? null : Selling(search) ? best.Demand : best.Stock, best?.Station, best?.StarSystem,
                best is null || game.StarPos is not { } here ? null : Math.Round(ShipMath.Distance(here, [best.X, best.Y, best.Z]), 2),
                result is { Mean: > 0 } ? result.Mean : null, result?.Problem);
        })],
        saved.Volume, Most, EveryMinutes);

    // ---------------------------------------------------------------- the list

    /// <summary>Keeps a search: a new one, or in place of the saved one with the same id. What it found before is forgotten.</summary>
    public void Save(SavedSearch search)
    {
        if (string.IsNullOrWhiteSpace(search.Name)) throw new FindProblem("Type the name of the commodity to watch, or pick it from the list.");
        // The list of commodities is known once any search has run; until then the name is checked when the search first runs.
        if (data.Overview is { } overview)
        {
            string typed = Finder.Letters(search.Name);
            string id = overview.Prices.Keys.FirstOrDefault(code => Finder.Letters(code) == typed || Finder.Letters(GameData.CommodityName(code)) == typed)
                ?? throw new FindProblem($"Ardent Insight knows no commodity called \"{search.Name.Trim()}\". Pick a name from the list.");
            search = search with { Name = GameData.CommodityName(id) };
        }
        bool known = search.Id is { Length: > 0 } && saved.Searches.Any(old => old.Id == search.Id);
        if (!known && saved.Searches.Count >= Most) throw new FindProblem(Invariant($"There is room for {Most} saved searches. Delete one first."));
        search = Tidied(known ? search : search with { Id = Guid.NewGuid().ToString("N")[..12] });
        results.Remove(search.Id);
        Keep(saved with { Searches = known ? [.. saved.Searches.Select(old => old.Id == search.Id ? search : old)] : [.. saved.Searches, search] });
    }

    /// <summary>The two ticks of a saved search; null leaves one as it is.</summary>
    public void Set(string id, bool? on, bool? sound)
    {
        SavedSearch search = Saved(id);
        Keep(saved with { Searches = [.. saved.Searches.Select(old => old.Id == id ? search with { On = on ?? search.On, Sound = sound ?? search.Sound } : old)] });
    }

    public void Delete(string id)
    {
        Saved(id);
        results.Remove(id);
        Keep(saved with { Searches = [.. saved.Searches.Where(old => old.Id != id)] });
    }

    /// <summary>How loud the chime is, 0 to 100; it is played, so the player hears what they picked.</summary>
    public void SetVolume(int volume)
    {
        Keep(saved with { Volume = Math.Clamp(volume, 0, 100) });
        chime(saved.Volume);
    }

    SavedSearch Saved(string id) => saved.Searches.FirstOrDefault(search => search.Id == id) ?? throw new FindProblem("That saved search is no longer there.", 404);

    void Keep(SnifferSettings changed)
    {
        saved = changed;
        try { settings.Set(SnifferSettings.Section, saved); }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error("The saved searches could not be written to settings.json", problem);
        }
        Changed?.Invoke();
    }

    static SavedSearch Tidied(SavedSearch search) => search with
    {
        Id = string.IsNullOrWhiteSpace(search.Id) ? Guid.NewGuid().ToString("N")[..12] : search.Id.Trim(),
        Name = (search.Name ?? "").Trim(),
        Side = Selling(search) ? "sell" : "buy",
        Price = Math.Max(0, search.Price),
        AveragePercent = search.AveragePercent is { } percent ? Math.Clamp((int)Math.Round(percent / 10.0) * 10, -50, 50) : null,
        Tons = Math.Max(0, search.Tons),
        Pad = new[] { "S", "M", "L" }.FirstOrDefault(pad => pad.Equals(search.Pad, StringComparison.OrdinalIgnoreCase)) ?? "auto",
        Surface = new[] { "horizons", "none" }.FirstOrDefault(choice => choice.Equals(search.Surface, StringComparison.OrdinalIgnoreCase)) ?? "odyssey",
        MaxStarLs = Math.Max(0, search.MaxStarLs),
        MaxAgeHours = Math.Max(1, search.MaxAgeHours),
    };

    static bool Selling(SavedSearch search) => string.Equals(search.Side, "sell", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- running them

    /// <summary>
    /// Called often (the app does twice a second). Starts the search that has waited longest when it is
    /// on and was last run an hour ago or more, or not at all since Studio started. One at a time and
    /// a minute apart, so a full list is spread out and not sent in one burst.
    /// </summary>
    public void Tick()
    {
        DateTime now = utcNow();
        if (running.Count > 0 || (now - lastStart).TotalSeconds < ApartSeconds) return;
        SavedSearch? due = saved.Searches
            .Where(search => search.On && (!results.TryGetValue(search.Id, out Result? last) || (now - last.Ran).TotalMinutes >= EveryMinutes))
            .OrderBy(search => results.TryGetValue(search.Id, out Result? last) ? last.Ran : DateTime.MinValue).FirstOrDefault();
        if (due is null) return;
        lastStart = now;
        LastRun = Run(due.Id);   // it reports its own problems, as a line on the page
    }

    /// <summary>Runs a saved search now. When stations meet it and its sound is ticked, the chime plays: every time, the owner's choice ("Every hour").</summary>
    public async Task Run(string id)
    {
        SavedSearch search = Saved(id);
        if (!running.Add(id)) return;   // already on its way
        Changed?.Invoke();
        Result result;
        try
        {
            try
            {
                (IReadOnlyList<Offer> met, long mean) = await Ask(search);
                result = new(utcNow(), met, mean, null);
            }
            catch (FindProblem problem) { result = new(utcNow(), [], 0, problem.Message); }
        }
        finally { running.Remove(id); }
        // It may have been deleted or changed while the service was thinking: then this answer is nobody's.
        // (Its two ticks may change meanwhile: they are not part of what is searched for.)
        if (saved.Searches.FirstOrDefault(now => now.Id == id) is { } still && still with { On = search.On, Sound = search.Sound } == search)
        {
            results[id] = result;
            Log.Info(Invariant($"Sniffer: {Words(search)}: {result.Problem ?? result.Offers.Count + " stations"}"));
            if (still.Sound && result.Offers.Count > 0) chime(saved.Volume);
        }
        Changed?.Invoke();
    }

    /// <summary>The search in a few words, for the log.</summary>
    static string Words(SavedSearch search) => Invariant($"{search.Side} {search.Name}, {(search.Price > 0 ? search.Price + " cr" : "any price")}{(search.AveragePercent is { } percent ? Invariant($", average {percent:+0;-0;+0}%") : "")}, {search.Tons} t");

    /// <summary>The stations anywhere in the galaxy that meet a search, best price first, from Ardent Insight.</summary>
    async Task<(IReadOnlyList<Offer> Met, long Mean)> Ask(SavedSearch search)
    {
        MarketData.Asking ask = data.Ask(utcNow(), CancellationToken.None);
        try
        {
            PriceOverview overview = await Task.Run(ask.Overview);
            string typed = Finder.Letters(search.Name);
            string id = overview.Prices.Keys.FirstOrDefault(code => Finder.Letters(code) == typed || Finder.Letters(GameData.CommodityName(code)) == typed)
                ?? throw new FindProblem($"Ardent Insight knows no commodity called \"{search.Name}\".");
            bool selling = Selling(search);
            int least = Math.Max(1, search.Tons);   // "any" still means at least one ton
            // The service is given the price and the tons, so the hundred stations it sends are the hundred best that meet them.
            string price = search.Price > 0 ? Invariant($"&{(selling ? "minPrice" : "maxPrice")}={search.Price}") : "";
            string url = Invariant($"{MarketData.Ardent}/commodity/name/{id}/{(selling ? "imports" : "exports")}?minVolume={least}{price}&maxDaysAgo={MarketData.Days(search.MaxAgeHours)}{MarketData.CarriersWord(search.Carriers)}");
            (IReadOnlyList<Offer> offers, _) = await ask.Answer(url, null, OfferMinutes, Offer.ReadList);

            // The galactic average comes with every offer: the game's own figure, the same at every station. A search may
            // set its price against it ("the average less 50%"), with or without a price in credits (the owner, 11 October 2026).
            long mean = (long)Math.Round(offers.Select(offer => offer.MeanPrice).FirstOrDefault(average => average > 0));
            double? against = search.AveragePercent is { } percent ? mean * (1 + percent / 100.0) : null;
            var filter = new StationFilter(StationFilter.PadSize(search.Pad) ?? hauling.ShipPad, search.MaxStarLs, search.Surface, search.Carriers);
            var seen = new HashSet<long>();
            // The same rules as "Find by name", and the price and the tons once more: what the service was asked is not always what it sends.
            Offer[] met =
            [
                .. from offer in offers
                   let cost = selling ? offer.SellPrice : offer.BuyPrice
                   let tons = selling ? offer.Demand : offer.Stock
                   where cost > 0 && tons >= least && (search.Price <= 0 || (selling ? cost >= search.Price : cost <= search.Price))
                   where against is null || (mean > 0 && (selling ? cost >= against : cost <= against))
                   where !(selling && offer.Stock > 0) && filter.Keeps(offer.Type, offer.Pad, offer.StarLs)
                   where offer.Updated is { } updated && (ask.Now - updated).TotalHours <= search.MaxAgeHours
                   where seen.Add(offer.MarketId)
                   orderby selling ? -cost : cost
                   select offer,
            ];
            return (met, mean);
        }
        catch (Exception problem) when (Finder.Lost(problem))
        {
            Log.Error($"Sniffer: {Words(search)} failed", problem);
            throw new FindProblem($"Couldn't get an answer from Ardent Insight: {MarketData.Reason(problem)}", 502);
        }
        finally
        {
            if (ask.Urls.Count > 0) Log.Info("Sniffer asked:" + string.Concat(ask.Urls.Select(url => Environment.NewLine + "    " + url)));
        }
    }

    /// <summary>
    /// What a search's last run found, as a "Find by name" answer. The distances are from the system
    /// the player is in now, so the list can be sorted by them as any search's can; 0 where the game
    /// has not said where that is.
    /// </summary>
    public FindAnswer Matches(string id)
    {
        SavedSearch search = Saved(id);
        if (!results.TryGetValue(id, out Result? result)) throw new FindProblem("This search has not been run yet. Press Run now.");
        bool selling = Selling(search);
        double[]? here = game.StarPos;
        FoundPlace[] rows =
        [
            .. result.Offers.Select(offer => new FoundPlace(offer.Station, offer.StarSystem,
                here is null ? 0 : Math.Round(ShipMath.Distance(here, [offer.X, offer.Y, offer.Z]), 2), offer.StarLs, StationFilter.PadLetter(offer.Pad),
                Finder.Shown(offer.Type), "", selling ? offer.SellPrice : offer.BuyPrice, selling ? offer.Demand : offer.Stock, offer.Updated)),
        ];
        string? note = result.Problem ?? (rows.Length == 0 ? "No station meets this search at the moment."
            : here is null ? "Start Elite to see how far away these are." : null);
        return new FindAnswer($"Where to {(selling ? "sell" : "buy")} {search.Name}", game.StarSystem ?? "", rows, "Ardent Insight", null, note, null, result.Ran);
    }
}

/// <summary>The sniffer's alert: two rising notes, made here so no sound file has to ship with Studio.</summary>
static class Chime
{
    static SoundPlayer? playing;   // kept, so it is not cleared away while it still sounds

    /// <summary>Plays it at a volume of 0 to 100 and returns at once. A PC without a sound device stays silent.</summary>
    public static void Play(int volume)
    {
        if (volume <= 0) return;
        try
        {
            SoundPlayer? before = playing;
            playing = new SoundPlayer(Wave(volume / 100.0));
            playing.Play();
            before?.Dispose();
        }
        catch (Exception problem) when (problem is InvalidOperationException or IOException or TimeoutException)
        {
            Log.Error("The sniffer's chime could not be played", problem);
        }
    }

    /// <summary>The chime as a WAV file in memory: 16-bit mono, an A and the E above it, each fading out.</summary>
    internal static MemoryStream Wave(double loudness)
    {
        const int rate = 44100;
        (double Hz, double Start, double Seconds)[] notes = [(880, 0, 0.5), (1318.51, 0.16, 0.75)];
        short[] samples = new short[(int)(rate * 0.95)];
        foreach ((double hz, double start, double seconds) in notes)
            for (int at = 0; at < (int)(rate * seconds); at++)
            {
                double time = (double)at / rate;
                // A quick rise so it does not click, then a fade.
                double shape = Math.Min(1, time / 0.008) * Math.Exp(-time * 5.5);
                int index = (int)(rate * start) + at;
                samples[index] = (short)Math.Clamp(samples[index] + Math.Sin(2 * Math.PI * hz * time) * shape * loudness * 0.45 * short.MaxValue, short.MinValue, short.MaxValue);
            }

        var wave = new MemoryStream();
        using (var writer = new BinaryWriter(wave, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(36 + samples.Length * 2); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(samples.Length * 2);
            foreach (short sample in samples) writer.Write(sample);
        }
        wave.Position = 0;
        return wave;
    }
}
