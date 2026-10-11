using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using EliteStudio.Core;

namespace EliteStudio.Market;

/// <summary>One station's listing for one commodity, as the Ardent Insight service reports it.</summary>
/// <param name="Pad">The largest landing pad: 1 small, 2 medium, 3 large; null when not known.</param>
/// <param name="StarLs">Light-seconds from the arrival star; null when not known.</param>
/// <param name="BuyPrice">Credits per ton the player pays here.</param>
/// <param name="SellPrice">Credits per ton the station pays the player.</param>
/// <param name="Updated">When the price list was last uploaded (UTC); null when the service does not say.</param>
/// <param name="Distance">Whole light years from the system asked about; only "nearby" answers carry it.</param>
sealed record Offer(
    string Commodity, long MarketId, string Station, string Type, int? Pad, double? StarLs,
    string StarSystem, double X, double Y, double Z,
    int BuyPrice, int SellPrice, int Stock, int Demand, double MeanPrice, DateTime? Updated, double? Distance)
{
    /// <summary>Reads an answer that is a list of offers. Anything else in its place (an error note, say) is no offers.</summary>
    public static async ValueTask<IReadOnlyList<Offer>> ReadList(Stream body, CancellationToken stop)
    {
        using JsonDocument answer = await JsonDocument.ParseAsync(body, default, stop).ConfigureAwait(false);
        return [.. MarketData.Rows(answer.RootElement).Select(Parse)];
    }

    static Offer Parse(JsonElement e) => new(
        e.Text("commodityName"), e.Whole("marketId"), e.Text("stationName"), e.Text("stationType"),
        Known(e, "maxLandingPadSize") is { } pad ? (int)Math.Round(pad) : null, Known(e, "distanceToArrival"),
        e.Text("systemName"), e.Number("systemX"), e.Number("systemY"), e.Number("systemZ"),
        Int(e, "buyPrice"), Int(e, "sellPrice"), Int(e, "stock"), Int(e, "demand"), e.Number("meanPrice"),
        MarketData.Time(e.Text("updatedAt")), Known(e, "distance"));

    static double? Known(JsonElement e, string name) => e.Field(name) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;

    static int Int(JsonElement e, string name) => (int)Math.Clamp(e.Whole(name), int.MinValue, int.MaxValue);
}

/// <summary>One commodity across all known markets: credits per ton, and tons in total.</summary>
readonly record struct Price(double AvgSell, double MaxSell, double MinBuy, double AvgBuy, double Stock, double Demand);

/// <summary>
/// The galaxy price overview: what every commodity costs and fetches across all known markets and
/// how much of it is on offer and wanted. It tells a search which commodities are worth asking about.
/// </summary>
/// <param name="Prices">By the commodity's code as the service writes it ("lowtemperaturediamond"): the word every question about it is asked with.</param>
/// <param name="Traded">Every commodity somebody wants to buy (code, display name), by name.</param>
sealed record PriceOverview(DateTime Time, IReadOnlyDictionary<string, Price> Prices, IReadOnlyList<(string Id, string Name)> Traded)
{
    public static PriceOverview Parse(JsonElement all, DateTime time)
    {
        var prices = new Dictionary<string, Price>(StringComparer.OrdinalIgnoreCase);
        // The service lists a commodity twice: under its code ("advancedcatalysers") with the real
        // numbers, and under a spelled-out name ("advanced catalysers"), which is of no use here.
        foreach (JsonElement row in MarketData.Rows(all))
        {
            string code = row.Text("commodityName");
            if (code.Length == 0 || !code.All(char.IsAsciiLetterOrDigit)) continue;
            prices[code] = new Price(
                row.Number("avgSellPrice"), row.Number("maxSellPrice"), row.Number("minBuyPrice"), row.Number("avgBuyPrice"),
                row.Number("totalStock"), row.Number("totalDemand"));
        }
        return new PriceOverview(time, prices,
            [.. prices.Where(p => p.Value.Demand > 0).Select(p => (Id: p.Key, Name: GameData.CommodityName(p.Key))).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)]);
    }
}

/// <summary>The systems around the player that match one Powerplay filter, each with its Power and state.</summary>
/// <param name="Total">How many systems match in all: more than <see cref="Systems"/> holds when only the nearest were fetched.</param>
sealed record PowerSet(IReadOnlyDictionary<string, (string Power, string State)> Systems, int Total);

/// <summary>
/// Asks the two data services and remembers what they said: Ardent Insight for market prices, and
/// Spansh for Powerplay and (for "Find by name" and a shopping list) for stations. It is a polite
/// client: it says who it is, sends at most six requests at a time, never repeats a request that
/// failed, and does not ask again for what it was told a few minutes ago.
/// </summary>
/// <param name="handler">Tests pass a stand-in for the network; null means the real internet.</param>
sealed class MarketData(HttpMessageHandler? handler) : IDisposable
{
    public const string Ardent = "https://api.ardent-insight.com/v2";
    const string Spansh = "https://spansh.co.uk/api/systems/search";
    const double OverviewHours = 12, PowerplayMinutes = 120;
    const double LongestOfferMinutes = 15;             // no search trusts a list of offers for longer
    const int SpanshPageSize = 500, SpanshPages = 6;   // so at most the 3,000 nearest systems

    /// <summary>How Spansh names the parts of its answers: "power_state" for PowerState.</summary>
    public static readonly JsonSerializerOptions SpanshNames = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    readonly HttpClient http = NewClient(handler);
    readonly SemaphoreSlim sixAtOnce = new(6);

    // Answers by request address (Powerplay sets by their own key), with the start time of the
    // search that fetched them. How long an answer stays good is up to whoever reads it.
    readonly ConcurrentDictionary<string, (DateTime Time, object Data)> answers = new();
    volatile PriceOverview? overview;

    /// <summary>How long one request may take, counted from the moment it is really sent. Tests shorten it.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The newest galaxy price overview; null until a search has loaded one.</summary>
    public PriceOverview? Overview => overview;

    /// <summary>Starts one search's round of questions. <paramref name="now"/> is the time every age and every remembered answer is measured from.</summary>
    public Asking Ask(DateTime now, CancellationToken cancel)
    {
        // What no search would accept any more only takes up room: a wide search remembers tens of thousands of offers.
        foreach ((string key, (DateTime time, object data)) in answers)
            if ((now - time).TotalMinutes >= (data is PowerSet ? PowerplayMinutes : LongestOfferMinutes)) answers.TryRemove(key, out _);
        return new Asking(this, now, cancel);
    }

    public void Dispose() => http.Dispose();

    /// <summary>The entries of an answer that is a list of things; an answer of another shape has none.</summary>
    public static IEnumerable<JsonElement> Rows(JsonElement answer) =>
        answer.ValueKind == JsonValueKind.Array ? answer.EnumerateArray().Where(row => row.ValueKind == JsonValueKind.Object) : [];

    /// <summary>The address of what Ardent Insight knows about one system, by the system's name.</summary>
    public static string SystemAddress(string system) => $"{Ardent}/system/name/{Uri.EscapeDataString(system)}";

    /// <summary>How old prices may be, as Ardent Insight is asked: it counts in whole days, so the exact limit in hours is the asker's to apply.</summary>
    public static int Days(double maxAgeHours) => Math.Max(1, (int)Math.Ceiling(maxAgeHours / 24));

    /// <summary>What a question to Ardent Insight ends with. It reads fleetCarriers=true as "carriers ONLY": leaving the word out is what includes them.</summary>
    public static string CarriersWord(bool carriers) => carriers ? "" : "&fleetCarriers=false";

    /// <summary>A time as the services write it, in UTC; null when the text is not one.</summary>
    public static DateTime? Time(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime at) ? at : null;

    /// <summary>True when a Powerplay filter is set to a Power or a state, and not to "any".</summary>
    public static bool Chosen(string? value) => !string.IsNullOrEmpty(value) && !value.Equals("any", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why a request or a search failed, in words that can be shown to the player.</summary>
    public static string Reason(Exception problem) =>
        problem is JsonException ? "The service sent an answer that could not be read." : problem.GetBaseException().Message;

    static HttpClient NewClient(HttpMessageHandler? handler)
    {
        handler ??= new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
        // No time limit on the client itself: each request gets its own, which starts when its turn comes.
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", App.Program.UserAgent);
        return client;
    }

    sealed record SpanshPage(int Count, List<SpanshSystem?>? Results);

    sealed record SpanshSystem(string? Name, string? ControllingPower, string? PowerState);

    /// <summary>The questions of one search: its clock, its stop signal, and the list of what was really sent.</summary>
    public sealed class Asking(MarketData data, DateTime now, CancellationToken cancel)
    {
        int answered, lost;
        string? firstLoss;

        public DateTime Now => now;

        /// <summary>Every request sent, in the order started. Answers taken from memory are not listed.</summary>
        public List<string> Urls { get; } = [];

        /// <summary>Requests for offers that failed and were counted as "no offers".</summary>
        public int Lost => lost;

        /// <summary>Why the first of them failed.</summary>
        public string? FirstLoss => firstLoss;

        /// <summary>
        /// The reason when requests failed and nothing at all came back, neither from the services
        /// nor from memory: the player is offline or the service is down, which is not the same as
        /// "no trades found". Null otherwise.
        /// </summary>
        public string? NothingAnswered => answered == 0 && lost > 0 ? firstLoss : null;

        /// <summary>The galaxy price overview, fetched again when the one in memory is older than twelve hours. A failure here stops the search.</summary>
        public async Task<PriceOverview> Overview()
        {
            if (data.overview is { } kept && (now - kept.Time).TotalHours <= OverviewHours) return kept;
            string url = Ardent + "/commodities";
            Urls.Add(url);
            PriceOverview fresh = await Send(new HttpRequestMessage(HttpMethod.Get, url), async (body, stop) =>
            {
                using JsonDocument all = await JsonDocument.ParseAsync(body, default, stop).ConfigureAwait(false);
                return PriceOverview.Parse(all.RootElement, now);
            }).ConfigureAwait(false);
            // An answer with no commodity in it (an empty list, an error note) is no overview. Kept for
            // its twelve hours it would make every search find nothing, without a word of why.
            if (fresh.Prices.Count == 0) throw new InvalidDataException("The price overview came back empty.");
            return data.overview = fresh;
        }

        /// <summary>
        /// A list of offers: from memory when the same question was answered less than
        /// <paramref name="minutes"/> ago, otherwise asked now. The request is already under way
        /// when this returns, so a caller starts everything it needs and then awaits each in turn.
        /// A request that fails counts as "no offers" and is not remembered; only an answer that
        /// cannot be read stops the search.
        /// </summary>
        public Task<IReadOnlyList<Offer>> Offers(string url, double minutes)
        {
            if (data.answers.TryGetValue(url, out (DateTime Time, object Data) kept) && (now - kept.Time).TotalMinutes < minutes)
            {
                Interlocked.Increment(ref answered);
                return Task.FromResult((IReadOnlyList<Offer>)kept.Data);
            }
            Urls.Add(url);
            return Fetch(url);
        }

        /// <summary>
        /// The systems within <paramref name="radius"/> light years of <paramref name="system"/>
        /// that match a Powerplay filter, nearest first, from Spansh (the market data knows nothing
        /// about Powerplay). Remembered for two hours. A failure here stops the search.
        /// </summary>
        public async Task<PowerSet> Powerplay(string system, string power, string state, int radius)
        {
            string key = $"powerplay|{system}|{power}|{state}|{radius.ToString(CultureInfo.InvariantCulture)}";
            if (data.answers.TryGetValue(key, out (DateTime Time, object Data) kept) && (now - kept.Time).TotalMinutes < PowerplayMinutes)
                return (PowerSet)kept.Data;

            var filters = new Dictionary<string, object> { ["distance"] = new { min = "0", max = radius.ToString(CultureInfo.InvariantCulture) } };
            if (Chosen(state)) filters["power_state"] = new { value = new[] { state } };
            // An unoccupied system has no controlling Power, so there "Power" means a Power in range of it.
            if (Chosen(power)) filters[string.Equals(state, "Unoccupied", StringComparison.OrdinalIgnoreCase) ? "power" : "controlling_power"] = new { value = new[] { power } };

            var systems = new Dictionary<string, (string Power, string State)>(StringComparer.OrdinalIgnoreCase);
            int total, got, page = 0;
            do
            {
                Urls.Add($"{Spansh} (page {page.ToString(CultureInfo.InvariantCulture)})");
                string question = JsonSerializer.Serialize(new
                {
                    filters, reference_system = system, sort = new[] { new { distance = new { direction = "asc" } } }, size = SpanshPageSize, page,
                });
                var request = new HttpRequestMessage(HttpMethod.Post, Spansh) { Content = new StringContent(question, Encoding.UTF8, "application/json") };
                // A page runs to many megabytes (every system comes with its stations and bodies), so
                // it is read as it arrives and only the three fields needed are kept.
                SpanshPage? answer = await Send(request, (body, stop) => JsonSerializer.DeserializeAsync<SpanshPage>(body, SpanshNames, stop)).ConfigureAwait(false);
                total = answer?.Count ?? 0;
                got = 0;
                foreach (SpanshSystem? found in answer?.Results ?? [])
                {
                    if (found?.Name is null) continue;
                    systems[found.Name] = (found.ControllingPower ?? "", found.PowerState ?? "");
                    got++;
                }
                page++;
            }
            while (got > 0 && systems.Count < total && page < SpanshPages);

            var set = new PowerSet(systems, total);
            data.answers[key] = (now, set);
            return set;
        }

        /// <summary>
        /// Any other question, as "Find by name" asks them: sent with the same manners as every
        /// request here and read with <paramref name="read"/>. A <paramref name="question"/> is
        /// posted as JSON; without one the address is simply fetched. The answer comes with the
        /// time it was asked: now, or earlier when the same was asked less than
        /// <paramref name="minutes"/> ago (fifteen at most: nothing but Powerplay is kept longer).
        /// Unlike <see cref="Offers"/> a failure is thrown, so the caller can say which service let
        /// the player down, and how. Sending and reading happen on a background thread, so this
        /// may be called from the thread that draws.
        /// </summary>
        public async Task<(T Data, DateTime Asked)> Answer<T>(string url, string? question, double minutes, Func<Stream, CancellationToken, ValueTask<T>> read) where T : notnull
        {
            string key = question is null ? url : $"{url} {question}";
            if (data.answers.TryGetValue(key, out (DateTime Time, object Data) kept) && (now - kept.Time).TotalMinutes < minutes) return ((T)kept.Data, kept.Time);
            Urls.Add(key);
            var request = new HttpRequestMessage(question is null ? HttpMethod.Get : HttpMethod.Post, url);
            if (question is not null) request.Content = new StringContent(question, Encoding.UTF8, "application/json");
            T answer = await Task.Run(() => Send(request, read)).ConfigureAwait(false);
            data.answers[key] = (now, answer);
            return (answer, now);
        }

        async Task<IReadOnlyList<Offer>> Fetch(string url)
        {
            try
            {
                IReadOnlyList<Offer> offers = await Send(new HttpRequestMessage(HttpMethod.Get, url), Offer.ReadList).ConfigureAwait(false);
                data.answers[url] = (now, offers);
                return offers;
            }
            catch (Exception problem) when (problem is HttpRequestException or IOException or TimeoutException)
            {
                Interlocked.Increment(ref lost);
                Interlocked.CompareExchange(ref firstLoss, Reason(problem), null);
                return [];
            }
        }

        // Sends one request when one of the six places is free, and reads the answer with `read`.
        // The time limit starts when the request is really sent, not while it waits its turn.
        async Task<T> Send<T>(HttpRequestMessage request, Func<Stream, CancellationToken, ValueTask<T>> read)
        {
            using HttpRequestMessage sent = request;
            await data.sixAtOnce.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                cancel.ThrowIfCancellationRequested();   // a place that came free because the search was stopped is not for sending more
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                limit.CancelAfter(data.RequestTimeout);
                try
                {
                    using HttpResponseMessage answer = await data.http.SendAsync(sent, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref answered);
                    answer.EnsureSuccessStatusCode();
                    await using Stream body = await answer.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
                    return await read(body, limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    throw new TimeoutException($"No answer within {data.RequestTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds.");
                }
            }
            finally
            {
                data.sixAtOnce.Release();
            }
        }
    }
}
