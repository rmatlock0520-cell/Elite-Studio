using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using EliteStudio.Core;
using static System.FormattableString;

namespace EliteStudio.Market;

/// <summary>A station with everything its outfitting and shipyard sell: what a shopping trip is picked from.</summary>
/// <param name="SystemAddress">The game's own number for the system; 0 when Spansh gave none.</param>
/// <param name="At">Where the system lies: [x, y, z] in light years.</param>
/// <param name="DistanceLy">Light years from the system the player is in.</param>
/// <param name="Pad">The largest landing pad: "S", "M" or "L"; "" when not known.</param>
/// <param name="Updated">When its outfitting was last reported (UTC); null when not known.</param>
/// <param name="Modules">Every module on sale and its price, by the game's code for it ("Int_Hyperdrive_Size5_Class5"; found without regard to capitals).</param>
/// <param name="Ships">Every ship on sale and its price, by Spansh's name for it (found without regard to capitals).</param>
public sealed record Seller(
    string Station, string System, long SystemAddress, double[] At, double DistanceLy, double? StarLs, string Pad, string Type, DateTime? Updated,
    IReadOnlyDictionary<string, long> Modules, IReadOnlyDictionary<string, long> Ships);

// The half of Finder that asks Spansh's stations search: the names it filters by, the nearest stations
// that sell a module or a ship or have a service, and the stations a shopping trip is picked from. The
// class is described in FindApi.cs, which also holds the commodity search and what both halves share.
sealed partial class Finder
{
    const string Stations = "https://spansh.co.uk/api/stations";
    const int MostSellers = 20, Nearest = 10;   // stations asked of Spansh at a time; one runs to 100,000 letters, for it comes with its whole price list
    const int MostPages = 3;                // one search asks for them this often at most: again only when the station filters left too few of the page before
    const double StationMinutes = 10;       // how long the same question gets the remembered answer

    // The services with a quick button: the name on it, Spansh's filter, and the value to filter by.
    // Material traders and technology brokers have filters of their own, which is what tells their kinds apart.
    static readonly (string Name, string Field, string Value)[] Services =
    [
        ("Raw material trader", "material_trader", "Raw"), ("Manufactured material trader", "material_trader", "Manufactured"),
        ("Encoded material trader", "material_trader", "Encoded"),
        ("Human technology broker", "technology_broker", "Human"), ("Guardian technology broker", "technology_broker", "Guardian"),
        ("Interstellar factors", "services", "Interstellar Factors Contact"), ("Black market", "services", "Black Market"),
        ("Shipyard", "services", "Shipyard"), ("Outfitting", "services", "Outfitting"), ("Repair", "services", "Repair"),
    ];

    // A question is written as it reads, "<=>" and not its escaped form: the log lists it too.
    static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly Dictionary<string, Task<NameList>> lists = [];   // Spansh's lists of names, asked for once a session

    /// <param name="Count">How many stations fit the question in all; Spansh stops counting at 10,000.</param>
    /// <param name="SearchReference">Spansh's name for the search, under which its site shows it.</param>
    sealed record StationPage(int Count, string? SearchReference, List<Station?>? Results);

    /// <summary>What a search reads of a station. Spansh sends some fifty fields; the three lists are what makes a row so long.</summary>
    sealed record Station(
        string? Name, string? SystemName, long? SystemId64, double? SystemX, double? SystemY, double? SystemZ, string? Type, double? Distance, double? DistanceToArrival,
        double? LargePads, double? MediumPads, double? SmallPads, string? MaterialTrader, string? TechnologyBroker,
        string? UpdatedAt, string? OutfittingUpdatedAt, string? ShipyardUpdatedAt,
        List<Entry?>? Services, List<Entry?>? Modules, List<Entry?>? Ships);

    /// <summary>A module or a ship on sale, or a service by its name.</summary>
    /// <param name="EdSymbol">Modules: the game's own code for it.</param>
    sealed record Entry(string? Name, string? EdSymbol, double? Class, string? Rating, double? Price);

    /// <summary>What one station has of the thing looked for.</summary>
    sealed record Listing(string Item, long? Price, string? Updated);

    /// <summary>What a stations search looks for: the words for it, Spansh's filter, how many stations to ask for at a time, and how to find it in a station (null: not there).</summary>
    sealed record Sought(string What, string Field, object Filter, int Size, Func<Station, Listing?> At);

    /// <summary>Spansh's own list of the names its stations search filters by. A list that could not be fetched is asked for again next time.</summary>
    Task<NameList> SpanshList(string kind, MarketData.Asking ask)
    {
        if (lists.TryGetValue(kind, out Task<NameList>? known) && !known.IsFaulted) return known;
        return lists[kind] = Fetch();

        // (0 minutes: it is `lists` that remembers a list, and for the whole session.)
        async Task<NameList> Fetch() => (await ask.Answer($"{Stations}/field_values/{kind}s", null, 0, async (body, stop) =>
        {
            using JsonDocument answer = await JsonDocument.ParseAsync(body, default, stop).ConfigureAwait(false);
            JsonElement values = answer.RootElement.Field("values") ?? default;
            string[] Sorted(string name) =>
                [.. values.List(name).Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()).Distinct().Order(StringComparer.OrdinalIgnoreCase)];
            var list = new NameList(Sorted("name"), Sorted("class"), Sorted("rating"));
            // Kept for the session it would make every name "unknown", without a word of why.
            return list.Names.Count > 0 ? list : throw new InvalidDataException("Its list of names came back empty.");
        })).Data;
    }

    /// <summary>
    /// The stations nearest the player that sell every one of these modules, and that ship when one
    /// is named, each with all it sells: Studio Loadouts picks the stops of a shopping trip from
    /// them. The player's own station filters count, and the landing pad may be asked larger than
    /// their ship needs. A station is told by the game's codes of what it sells, so the caller
    /// checks each one against its list: Spansh is asked by name, class and rating, which for
    /// armour fits every ship's.
    /// </summary>
    /// <param name="modules">Each by Spansh's own name for it (see <see cref="Names"/>), its class and its rating.</param>
    /// <param name="ship">Spansh's own name for a ship that must be on sale too, or null.</param>
    /// <param name="pad">The smallest landing pad that will do, when that is larger than the player's own choice: 1 small, 2 medium, 3 large.</param>
    public async Task<(IReadOnlyList<Seller> Sellers, DateTime Asked)> Sellers(IReadOnlyList<(string Name, int Class, string Rating)> modules, string? ship, int pad)
    {
        string from = game.StarSystem is { Length: > 0 } system ? system : throw new FindProblem("Studio does not know yet where you are. Start Elite first.");
        MarketSettings set = hauling.Settings;
        var filter = new StationFilter(Math.Max(pad, StationFilter.PadSize(set.Pad) ?? hauling.ShipPad), set.MaxStarLs, set.Surface, set.Carriers);
        var what = new Dictionary<string, object>();
        if (modules.Count > 0)
            what["modules"] = modules.Select(module => new { name = Value(module.Name), @class = Value(Digits(module.Class)), rating = Value(module.Rating) }).ToArray();
        if (ship is not null) what["ships"] = new[] { new { name = Value(ship) } };
        return await Asked(SpanshName, $"Find sellers of {modules.Count} modules{(ship is null ? "" : " and the " + ship)} from {from}", async ask =>
        {
            (StationPage page, DateTime asked) = await Page(Question(what, filter, Nearest, from, game.StarPos), game.StarPos is null ? from : null, ask);
            Seller[] sellers =
            [
                .. from station in page.Results?.OfType<Station>() ?? []
                   let kept = Kept(station, filter)
                   where kept is not null
                   select new Seller(station.Name ?? "", station.SystemName ?? "", station.SystemId64 ?? 0, [station.SystemX ?? 0, station.SystemY ?? 0, station.SystemZ ?? 0],
                       Math.Round(station.Distance ?? 0, 2), station.DistanceToArrival, StationFilter.PadLetter(kept.Value.Pad), Shown(kept.Value.Type),
                       MarketData.Time(station.OutfittingUpdatedAt), Stock(station.Modules, entry => entry.EdSymbol), Stock(station.Ships, entry => entry.Name)),
            ];
            return ((IReadOnlyList<Seller>)sellers, asked);
        });

        // What is on sale and its price, found without regard to capitals.
        static Dictionary<string, long> Stock(List<Entry?>? entries, Func<Entry, string?> name)
        {
            var stock = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (Entry entry in (entries ?? []).OfType<Entry>())
                if (name(entry) is { Length: > 0 } sold) stock.TryAdd(sold, (long)Math.Round(entry.Price ?? 0));
            return stock;
        }
    }

    /// <summary>The nearest stations that sell a module or a ship, or that have a service, from Spansh.</summary>
    async Task<FindAnswer> InStations(string kind, FindRequest request, string from, double[]? here, StationFilter filter, MarketData.Asking ask)
    {
        Sought sought = kind switch
        {
            "module" => Module(request, await SpanshList(kind, ask)),
            "ship" => Ship(request, await SpanshList(kind, ask)),
            _ => Service(request),
        };
        Dictionary<string, object> question = Question(new() { [sought.Field] = sought.Filter }, filter, sought.Size, from, here);
        var rows = new List<FoundPlace>();
        // Spansh sends its stations a page at a time, nearest first. A first search starts at page 0; "more" is the page after
        // the last one the answer before was made of.
        int first = Math.Max(0, request.More), next = first, seen = 0, strangers = 0, known = 0;
        bool last;   // the page just read is Spansh's last: shorter than was asked for
        string? searchName = null;
        DateTime asked = default;
        do
        {
            question["page"] = next;
            (StationPage page, DateTime at) = await Page(question, here is null ? from : null, ask);
            if (next++ == first) (known, searchName, asked) = (page.Count, page.SearchReference, at);

            Station[] sent = [.. page.Results?.OfType<Station>() ?? []];
            seen += sent.Length;
            foreach (Station station in sent)
            {
                if (sought.At(station) is not { } has) { strangers++; continue; }
                if (Kept(station, filter) is not { } kept) continue;
                rows.Add(new FoundPlace(station.Name ?? "", station.SystemName ?? "", Math.Round(station.Distance ?? 0, 2), station.DistanceToArrival,
                    StationFilter.PadLetter(kept.Pad), Shown(kept.Type), has.Item, has.Price, null, MarketData.Time(has.Updated)));
            }
            // A filter Spansh does not understand it drops without a word, and then sends the nearest stations of any kind.
            if (seen > 0 && strangers == seen) throw new InvalidDataException("It sent stations that do not have what was asked for.");
            // A page shorter than asked for is Spansh's last. The next one is asked for only while the
            // filters have left less than half a list: every page is another long answer from a free service.
            last = sent.Length < sought.Size;
        }
        while (!last && rows.Count < sought.Size / 2 && next < first + MostPages);

        bool more = known > first * sought.Size + seen;
        var notes = new List<string>();
        if (rows.Count > 0 && more)
            notes.Add($"The {(first == 0 ? "nearest" : "next")} {rows.Count} of {(known >= 10000 ? "more than 10,000" : known.ToString("N0", CultureInfo.InvariantCulture))} stations.");
        if (rows.Count == 0)
        {
            notes.Add(more
                ? Invariant($"None of the {(first == 0 ? $"{seen} nearest" : $"next {seen}")} stations that have this passes your filters, and Spansh knows more farther out. Try other filters, or search from another system.")
                : first > 0 ? "No more stations that pass your filters are known to have this."
                : "No station that passes your filters is known to have this." + (kind == "module" ? " Not every module comes in every class and rating." : ""));
        }
        if (rows.Any(row => row.Price is null && row.Item.Contains(' '))) notes.Add("Pick a class and a rating to see one price for each station.");
        // The name is put into an address, so it has to be the kind of name Spansh gives out and nothing else.
        string? link = Guid.TryParse(searchName, out Guid reference) ? $"https://spansh.co.uk/stations/search/{reference.ToString().ToUpperInvariant()}/1" : null;
        return new FindAnswer(sought.What, from, [.. rows.OrderBy(row => row.DistanceLy)], SpanshName, link, notes.Count > 0 ? string.Join(" ", notes) : null,
            more && !last ? next : null, asked);
    }

    /// <summary>
    /// A question for Spansh's stations search: what is looked for, nearest first from a system. The
    /// station filters go into it as far as Spansh takes them; <see cref="Kept"/> applies them to the
    /// letter of the rules. Two of them it cannot be asked. "A medium pad or larger": it only knows
    /// "has a large pad". And "no surface stations": it files the Powers' stronghold carriers, which
    /// are none, as planetary outposts, so that type is asked for whatever the filter says.
    /// </summary>
    /// <param name="what">Spansh's filters for the thing looked for; they come first in the question.</param>
    /// <param name="here">Where the system lies, when the game has said: Spansh need not know it by name then (a fresh discovery, say).</param>
    static Dictionary<string, object> Question(Dictionary<string, object> what, StationFilter filter, int size, string from, double[]? here)
    {
        var filters = new Dictionary<string, object>(what)
        {
            ["type"] = Value([.. StationTypes.Where(type => filter.Allows(type.Journal) || type.Journal == "CraterOutpost").Select(type => type.Spansh)]),
        };
        if (filter.Pad >= 3) filters["has_large_pad"] = new { value = true };
        if (filter.MaxStarLs > 0) filters["distance_to_arrival"] = new { comparison = "<=>", value = new[] { 0, filter.MaxStarLs } };
        var question = new Dictionary<string, object>
        {
            ["filters"] = filters, ["sort"] = new[] { new { distance = new { direction = "asc" } } }, ["size"] = size, ["page"] = 0,
        };
        if (here is null) question["reference_system"] = from;
        else question["reference_coords"] = new { x = here[0], y = here[1], z = here[2] };
        return question;
    }

    /// <summary>The page of Spansh's stations that a question asks for.</summary>
    /// <param name="named">The system the question names, which Spansh may not know; null when the question says where the system lies.</param>
    Task<(StationPage Page, DateTime Asked)> Page(Dictionary<string, object> question, string? named, MarketData.Asking ask)
    {
        var answer = ask.Answer($"{Stations}/search", JsonSerializer.Serialize(question, Readable), StationMinutes,
            async (body, stop) => await JsonSerializer.DeserializeAsync<StationPage>(body, MarketData.SpanshNames, stop).ConfigureAwait(false) ?? new StationPage(0, null, null));
        // Asked by where the system lies there is no name for Spansh not to know: a refusal then means its search has changed.
        return named is null ? answer : KnownSystem(SpanshName, named, answer);
    }

    /// <summary>A station's type as the game's journal spells it and its largest landing pad, or null when the station filters leave the station out.</summary>
    static (string Type, int? Pad)? Kept(Station station, StationFilter filter)
    {
        // A stronghold carrier is told from the planetary outposts it is filed with by its name: they are all called the same.
        string type = station.Name == "Stronghold Carrier" ? "StrongholdCarrier"
            : StationTypes.Where(known => Same(known.Spansh, station.Type)).Select(known => known.Journal).FirstOrDefault() ?? station.Type ?? "";
        int? pad = station.LargePads > 0 ? 3 : station.MediumPads > 0 ? 2 : station.SmallPads > 0 ? 1 : null;
        return filter.Keeps(type, pad, station.DistanceToArrival) ? (type, pad) : null;
    }

    static Sought Module(FindRequest request, NameList known)
    {
        string name = Picked(known.Names, request.Name, "module");
        string size = Choice(known.Classes, request.Class, "class"), grade = Choice(known.Ratings, request.Rating, "rating");
        var entry = new Dictionary<string, object> { ["name"] = Value(name) };
        // "Any" is said by leaving the word out: with every class listed, Spansh finds nothing for some modules.
        if (size.Length > 0) entry["class"] = Value(size);
        if (grade.Length > 0) entry["rating"] = Value(grade);
        string which = (size, grade) switch { ("", "") => "", (_, "") => $"class {size} ", ("", _) => $"{grade}-rated ", _ => $"{size}{grade} " };
        return new Sought($"Stations that sell the {which}{name}", "modules", new[] { entry }, MostSellers, station => OnSale(
            station.Modules, sold => Same(sold.Name, name) && (size.Length == 0 || Digits(sold.Class) == size) && (grade.Length == 0 || Same(sold.Rating, grade)),
            station.OutfittingUpdatedAt));
    }

    static Sought Ship(FindRequest request, NameList known)
    {
        string name = Picked(known.Names, request.Name, "ship");
        return new Sought($"Stations that sell the {name}", "ships", new[] { new { name = Value(name) } }, MostSellers,
            station => OnSale(station.Ships, entry => Same(entry.Name, name), station.ShipyardUpdatedAt));
    }

    static Sought Service(FindRequest request)
    {
        (string? name, string field, string value) = Services.FirstOrDefault(service => Same(service.Name, request.Name.Trim()));
        if (name is null) throw new FindProblem("Unknown kind of service.");
        // Spansh's site wraps a service's name once more than it does a trader's or a broker's kind.
        return new Sought($"Nearest: {name}", field, field == "services" ? new[] { new { name = Value(value) } } : Value(value), Nearest,
            station => (field switch
            {
                "material_trader" => Same(station.MaterialTrader, value),
                "technology_broker" => Same(station.TechnologyBroker, value),
                _ => station.Services?.Any(service => Same(service?.Name, value)) == true,
            }) ? new Listing("", null, station.UpdatedAt) : null);
    }

    /// <summary>
    /// What a station sells of the thing looked for. With no class or rating picked that may be
    /// several kinds (and armour is listed once for every ship): they are named, and a price is
    /// given only when it is the same for all of them.
    /// </summary>
    static Listing? OnSale(List<Entry?>? stock, Func<Entry, bool> wanted, string? updated)
    {
        Entry[] found = [.. (stock ?? []).OfType<Entry>().Where(wanted)];
        if (found.Length == 0) return null;
        string[] kinds = [.. found.Select(entry => Digits(entry.Class) + entry.Rating).Distinct()];
        return new Listing(kinds.Length > 4 ? Invariant($"{kinds.Length} kinds") : string.Join(" ", kinds),
            found.Select(entry => entry.Price).Distinct().ToArray() is [{ } price] ? (long)Math.Round(price) : null, updated);
    }

    /// <summary>The name as the service spells it, for a name typed or picked.</summary>
    static string Picked(IReadOnlyList<string> names, string typed, string kind) =>
        names.FirstOrDefault(name => Same(name, typed.Trim())) ?? throw new FindProblem(NotKnown(typed, kind, SpanshName));

    /// <summary>A class or a rating as Spansh lists it; "" for any.</summary>
    static string Choice(IReadOnlyList<string> choices, string picked, string what)
    {
        picked = picked.Trim();
        if (picked.Length == 0) return "";
        return choices.FirstOrDefault(choice => Same(choice, picked)) ?? throw new FindProblem($"There is no {what} \"{picked}\".");
    }

    /// <summary>The shape Spansh's own site gives a filter value. Other shapes are dropped without a word.</summary>
    static object Value(params string[] texts) => new { value = texts };

    static string Digits(double? number) => number?.ToString(CultureInfo.InvariantCulture) ?? "";
}
