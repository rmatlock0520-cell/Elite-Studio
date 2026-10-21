using System.Globalization;
using System.Net;
using System.Net.Http;
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
    const string EdsmShipyard = "https://www.edsm.net/api-system-v1/stations/shipyard", EdsmName = "EDSM";
    // EDSM turns a PC away for a while ("429 Too Many Requests") after a dozen questions sent at once (16 October 2026).
    // So it is asked one station at a time, a second apart, and left alone for ten minutes when it says so. Spansh lists
    // most large shipyards for a new ship: around Wolf 397 the first station EDSM confirmed for the Corsair was the
    // seventeenth. So a search checks up to 25 stations, nearest first, and stops once it has 5 that sell the ship.
    const int EdsmMost = 25;                // stations one search asks EDSM about; the farther ones are "not checked"
    const int ShipsWanted = 5;              // stations confirmed to sell the ship that make a list
    const double EdsmQuietMinutes = 10;

    /// <summary>What EDSM says of a station and the ship looked for.</summary>
    enum EdsmSays { NotAsked, NoList, Sells, DoesNotSell }

    /// <summary>How long to wait after each question to EDSM before the next. Tests set none.</summary>
    internal TimeSpan EdsmGap { get; set; } = TimeSpan.FromSeconds(1);

    DateTime edsmQuietUntil;                // EDSM asked to be left alone until then
    const int MostSellers = 20, Nearest = 10;   // stations asked of Spansh at a time; one runs to 100,000 letters, for it comes with its whole price list
    const int MostPages = 3;                // one search asks for them this often at most: again only when the station filters left too few of the page before
    const double StationMinutes = 10;       // how long the same question gets the remembered answer
    const string CarrierType = "Drake-Class Carrier";   // Spansh's name for a player's fleet carrier
    const int CarriersAtOnce = 100;         // fleet carriers one question asks the docking access of

    // The services with a quick button: the name on it, Spansh's filter, and the value to filter by.
    // Material traders and technology brokers have filters of their own, which is what tells their kinds apart.
    // A broker's kind is no report, though: the game, EDDN, EDSM and Ardent Insight only say that a station has
    // a broker, and Spansh's kind is the station's main economy in other words. Of the 9,170 stations it knows
    // with a broker (20 October 2026), every Guardian one is High Tech and every Human one Industrial; the
    // 6,198 with any other main economy have no kind at all. The owner found human brokers (Shock Cannon and the
    // like) at two stations it calls Guardian: Ray Gateway in Diaguandri and Soukup City in Latuba, both 80% High
    // Tech, and both with Guardian modules in their outfitting lists, so those are no proof either (October 2026).
    // Inara calls Ray Gateway Guardian too. Then a human one at Dodectech, a colony's High Tech station, which
    // Inara calls human: a colony's station has a human broker whatever its economy (BrokerKind, 21 October 2026).
    // So a broker search goes by BrokerKind, and says it is a guess.
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
        List<Entry?>? Services, List<Entry?>? Modules, List<Entry?>? Ships,
        long? MarketId = null, string? MarketUpdatedAt = null, List<Good?>? Market = null, string? CarrierDockingAccess = null,
        // what the station's own page shows besides (FindByStation.cs)
        string? CarrierName = null, string? Allegiance = null, string? Government = null, string? ControllingMinorFaction = null, List<Part?>? Economies = null,
        string? SystemControllingPower = null, string? SystemPowerState = null, string? State = null, double? SystemPopulation = null,
        string? PrimaryEconomy = null, bool? SystemIsColonised = null);

    /// <summary>One of a station's economies and how much of the station it is, in hundredths.</summary>
    sealed record Part(string? Name, double? Share);

    /// <summary>One line of a station's price list as Spansh sends it: the commodity by its name in words.</summary>
    sealed record Good(string? Commodity, double? BuyPrice, double? SellPrice, double? Supply, double? Demand);

    /// <summary>A module or a ship on sale, or a service by its name.</summary>
    /// <param name="EdSymbol">Modules: the game's own code for it.</param>
    /// <param name="Symbol">Ships: the game's own code for it ("Krait_MkII").</param>
    sealed record Entry(string? Name, string? EdSymbol, double? Class, string? Rating, double? Price, string? Symbol = null);

    /// <summary>What one station has of the thing looked for.</summary>
    /// <param name="Ship">Ships: Spansh's name for the ship; null for everything else.</param>
    /// <param name="Code">Ships: the game's own code for the ship, when Spansh gave it; null for everything else.</param>
    /// <param name="Broker">Technology brokers: what Spansh's kind rests on, in words (see <see cref="BrokerGuess"/>); "" for everything else.</param>
    /// <param name="Seen">Technology brokers: the kind the player saw there (<see cref="BrokerBook"/>); "" when they have not marked it.</param>
    sealed record Listing(string Item, long? Price, string? Updated, string? Ship = null, string? Code = null, string Broker = "", string Seen = "");

    /// <summary>The ships EDSM says a station's shipyard sells: by the game's number for each, and by the letters of each name. Empty when EDSM has no list.</summary>
    sealed record EdsmShips(HashSet<long> Ids, HashSet<string> Names);

    /// <summary>What a stations search looks for: the words for it, Spansh's filter, how many stations to ask for at a time, and how to find it in a station (null: not there).</summary>
    /// <param name="Broker">A technology broker search: the kind looked for ("Guardian"); "" for every other search.</param>
    sealed record Sought(string What, string Field, object Filter, int Size, Func<Station, Listing?> At, string Broker = "");

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
        int notSold = 0, sold = 0, noList = 0, notChecked = 0, edsmAsked = 0;   // ships: what EDSM said of the stations, and how many it was asked about
        int otherKind = 0;   // brokers: stations left out because the player saw a broker of the other kind there
        int notThisKind = 0; // brokers: stations Spansh calls this kind that BrokerKind does not
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
            var found = new List<(Station Station, (string Type, int? Pad) Kept, Listing Has)>();
            foreach (Station station in sent)
            {
                if (sought.At(station) is not { } has) { strangers++; continue; }
                if (Kept(station, filter) is not { } kept) continue;
                // A broker the player saw to be of the other kind is left out; one they saw to be of this kind says so.
                string? saw = sought.Broker.Length > 0 ? brokers?.KindAt(station.MarketId ?? 0) : null;
                if (saw is not null && saw != sought.Broker) { otherKind++; continue; }
                // One they have not marked goes by BrokerKind: the human search is asked for every broker, and Spansh calls a colony's station Guardian.
                if (saw is null && sought.Broker.Length > 0 && BrokerKind(station) != sought.Broker)
                {
                    if (Same(station.TechnologyBroker, sought.Broker)) notThisKind++;
                    continue;
                }
                found.Add((station, kept, saw is null ? has : has with { Seen = saw }));
            }
            // Spansh's shipyard lists hold the ships a station is not selling right now too (the tools that report
            // a shipyard add the game's "unavailable" list, as EDDN asks them to), and the owner flew to three
            // stations in Wolf 397 for a Corsair none of them had (16 October 2026). EDSM keeps the same reports
            // with the ships on sale only, so the nearest stations are checked there, one after the other.
            foreach ((Station station, (string Type, int? Pad) kept, Listing has) in found)
            {
                EdsmSays says = EdsmSays.NotAsked;
                if (kind == "ship" && sold < ShipsWanted && edsmAsked < EdsmMost)
                {
                    (says, bool askedEdsm) = await SellsShip(station, has, ask);
                    if (askedEdsm) edsmAsked++;
                }
                string edsm = "";
                if (kind == "ship")
                {
                    switch (says)
                    {
                        case EdsmSays.DoesNotSell: notSold++; continue;
                        case EdsmSays.Sells: sold++; edsm = "confirmed"; break;
                        case EdsmSays.NoList: noList++; edsm = "no list"; break;
                        default: notChecked++; edsm = "not checked"; break;
                    }
                }
                rows.Add(new FoundPlace(station.Name ?? "", station.SystemName ?? "", Math.Round(station.Distance ?? 0, 2), station.DistanceToArrival,
                    StationFilter.PadLetter(kept.Pad), Shown(kept.Type), has.Item, has.Price, null, MarketData.Time(has.Updated), station.MarketId ?? 0,
                    Edsm: edsm, Broker: has.Broker, BrokerSeen: has.Seen));
            }
            // A filter Spansh does not understand it drops without a word, and then sends the nearest stations of any kind.
            if (seen > 0 && strangers == seen) throw new InvalidDataException("It sent stations that do not have what was asked for.");
            // A page shorter than asked for is Spansh's last. The next one is asked for only while the
            // filters have left less than half a list: every page is another long answer from a free service.
            last = sent.Length < sought.Size;
        }
        // A ship's list is long enough once EDSM confirmed enough stations, or there is nothing more to check them with.
        while (!last && next < first + MostPages && (kind == "ship"
            ? sold < ShipsWanted && edsmAsked < EdsmMost && utcNow() >= edsmQuietUntil
            : rows.Count < sought.Size / 2));
        // The player's own brokers of this kind that Spansh guesses otherwise: its filter never sends them. They go with the
        // nearest stations, as no later page has them.
        if (sought.Broker.Length > 0 && first == 0 && brokers?.Of(sought.Broker) is { Length: > 0 } mine)
            await OwnBrokers(mine, sought, rows, filter, from, here, ask);

        bool more = known > first * sought.Size + seen;
        var notes = new List<string>();
        if (rows.Count > 0 && more)
            notes.Add($"The {(first == 0 ? "nearest" : "next")} {rows.Count} of {(known >= 10000 ? "more than 10,000" : known.ToString("N0", CultureInfo.InvariantCulture))} stations.");
        if (rows.Count > 0 && sought.Broker.Length > 0) notes.Add(BrokerWarning);
        if (otherKind > 0)
            notes.Add(Invariant($"{otherKind} {(otherKind == 1 ? "station" : "stations")} where you saw a {(sought.Broker == "Guardian" ? "Human" : "Guardian")} broker {(otherKind == 1 ? "was" : "were")} left out."));
        if (notThisKind > 0)
            notes.Add(Invariant($"{notThisKind} {(notThisKind == 1 ? "station" : "stations")} that Spansh calls {sought.Broker} {(notThisKind == 1 ? "was" : "were")} left out: a colony's station has a human broker, and a megaship's kind is not known."));
        if (rows.Count == 0)
        {
            notes.Add(more
                ? Invariant($"None of the {(first == 0 ? $"{seen} nearest" : $"next {seen}")} stations that have this passes your filters, and Spansh knows more farther out. Try other filters, or search from another system.")
                : first > 0 ? "No more stations that pass your filters are known to have this."
                : "No station that passes your filters is known to have this." + (kind == "module" ? " Not every module comes in every class and rating." : ""));
        }
        if (rows.Any(row => row.Price is null && row.Item.Contains(' '))) notes.Add("Pick a class and a rating to see one price for each station.");
        if (notSold > 0)
            notes.Add(Invariant($"{notSold} {(notSold == 1 ? "station Spansh lists was" : "stations Spansh lists were")} left out: {EdsmName} says {(notSold == 1 ? "its shipyard does" : "their shipyards do")} not sell it right now."));
        if (noList > 0)
            notes.Add(Invariant($"{EdsmName} has no shipyard list for {noList} of them, so {(noList == 1 ? "that one" : "those")} may not have it."));
        if (notChecked > 0)
            notes.Add(Invariant($"{notChecked} {(notChecked == 1 ? "was" : "were")} not checked with {EdsmName} and may well not have it.")
                + (utcNow() < edsmQuietUntil ? $" {EdsmName} asked Studio to slow down, so it is asked again in a few minutes." : ""));
        // Spansh leaves out the stations far from their star by itself, so no answer can say how many there were (the owner,
        // 21 October 2026: Kerwin Horizons, 18,751 ls out, was missing from a Guardian broker search).
        if (kind == "services" && filter.MaxStarLs > 0)
            notes.Add(Invariant($"Stations more than {filter.MaxStarLs:N0} ls from their star are left out, as your filters ask."));
        // The name is put into an address, so it has to be the kind of name Spansh gives out and nothing else.
        string? link = Guid.TryParse(searchName, out Guid reference) ? $"https://spansh.co.uk/stations/search/{reference.ToString().ToUpperInvariant()}/1" : null;
        return new FindAnswer(sought.What, from, [.. rows.OrderBy(row => row.DistanceLy)], SpanshName, link, notes.Count > 0 ? string.Join(" ", notes) : null,
            more && !last ? next : null, asked);
    }

    /// <summary>
    /// Who may dock at each of these fleet carriers, as their owners have set it and Spansh last
    /// heard: "All", "Squadron", "Friends", "Squadron Friends" or "None", by the carrier's name. A
    /// carrier Spansh does not know, or knows nothing of the kind about, is not in the answer. Ardent
    /// Insight, which finds the carriers, does not say. One question names up to a hundred carriers,
    /// and its answer is short: a carrier sells little. When Spansh gives no answer nobody's access
    /// is known, which is written to the log and is no reason to fail a search.
    /// </summary>
    async Task<Dictionary<string, string>> CarrierAccess(IEnumerable<string> carriers, MarketData.Asking ask)
    {
        var access = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // In the order of their names, so the same carriers make the same question, which is then not asked twice.
            foreach (string[] names in carriers.Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Chunk(CarriersAtOnce))
            {
                var question = new Dictionary<string, object>
                {
                    ["filters"] = new Dictionary<string, object> { ["type"] = Value(CarrierType), ["name"] = Value(names) },
                    ["size"] = names.Length, ["page"] = 0,
                };
                (StationPage page, _) = await Page(question, null, ask);
                foreach (Station carrier in page.Results?.OfType<Station>() ?? [])
                    if (carrier is { Name.Length: > 0, CarrierDockingAccess.Length: > 0 }) access[carrier.Name] = carrier.CarrierDockingAccess;
            }
        }
        catch (Exception problem) when (Lost(problem))
        {
            App.Log.Error("Who may dock at the fleet carriers found could not be fetched from Spansh, so none of them says", problem);
        }
        return access;
    }

    /// <summary>A carrier's docking access in words for a row; "" when it is not known.</summary>
    internal static string AccessWords(string? access) => access switch
    {
        "All" => "open to all",
        "Squadron Friends" => "squadron and friends only",
        "Squadron" => "squadron only",
        "Friends" => "friends only",
        "None" => "closed to everyone",
        _ => "",
    };

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
            station => OnSale(station.Ships, entry => Same(entry.Name, name), station.ShipyardUpdatedAt) is { } has
                ? has with { Ship = name, Code = station.Ships?.OfType<Entry>().FirstOrDefault(entry => Same(entry.Name, name))?.Symbol }
                : null);
    }

    /// <summary>
    /// What EDSM says of the station's shipyard and the ship. Not asked: EDSM did not answer, or asked
    /// to be left alone. EDSM is a third service the player never picked, so its failing only leaves the
    /// station unchecked and is written to the log. Sent: a question really went out (an answer from
    /// memory costs EDSM nothing).
    /// </summary>
    async Task<(EdsmSays Says, bool Sent)> SellsShip(Station station, Listing has, MarketData.Asking ask)
    {
        if (utcNow() < edsmQuietUntil) return (EdsmSays.NotAsked, false);
        string url = station.MarketId is > 0 and long market
            ? Invariant($"{EdsmShipyard}?marketId={market}")
            : $"{EdsmShipyard}?systemName={Uri.EscapeDataString(station.SystemName ?? "")}&stationName={Uri.EscapeDataString(station.Name ?? "")}";
        int before = ask.Urls.Count;
        EdsmShips ships;
        try
        {
            (ships, _) = await ask.Answer(url, null, StationMinutes, async (body, stop) =>
            {
                using JsonDocument answer = await JsonDocument.ParseAsync(body, default, stop).ConfigureAwait(false);
                // A station EDSM does not know is "{}"; one it knows comes with "ships".
                JsonElement[] listed = [.. answer.RootElement.List("ships")];
                return new EdsmShips([.. listed.Select(ship => ship.Whole("id")).Where(id => id != 0)], [.. listed.Select(ship => Letters(ship.Text("name")))]);
            });
        }
        catch (Exception problem) when (Lost(problem))
        {
            if (problem is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
            {
                edsmQuietUntil = utcNow().AddMinutes(EdsmQuietMinutes);
                App.Log.Error($"{EdsmName} asked Studio to slow down; ship searches leave it alone for {EdsmQuietMinutes} minutes", problem);
            }
            else App.Log.Error($"{EdsmName} could not say whether {station.Name} sells the {has.Ship}", problem);
            return (EdsmSays.NotAsked, true);
        }
        bool sent = ask.Urls.Count > before;
        if (sent && EdsmGap > TimeSpan.Zero) await Task.Delay(EdsmGap);
        if (ships.Ids.Count == 0 && ships.Names.Count == 0) return (EdsmSays.NoList, sent);
        // By the game's number for the ship where its code is known, else by the letters of its name ("Cobra MkIII" is EDSM's "Cobra Mk III").
        bool sells = has.Code is { } code && GameData.Ships.TryGetValue(code, out GameData.Ship? known)
            ? ships.Ids.Contains(known.Id) || ships.Names.Contains(Letters(known.Name))
            : ships.Names.Contains(Letters(has.Ship ?? ""));
        return (sells ? EdsmSays.Sells : EdsmSays.DoesNotSell, sent);
    }

    static Sought Service(FindRequest request)
    {
        (string? name, string field, string value) = Services.FirstOrDefault(service => Same(service.Name, request.Name.Trim()));
        if (name is null) throw new FindProblem("Unknown kind of service.");
        if (field == "technology_broker") return Broker(name, value);
        // Spansh's site wraps a service's name once more than it does a trader's kind.
        return new Sought($"Nearest: {name}", field, field == "services" ? new[] { new { name = Value(value) } } : Value(value), Nearest,
            station => (field == "material_trader" ? Same(station.MaterialTrader, value) : HasService(station, value)) ? new Listing("", null, station.UpdatedAt) : null);
    }

    /// <summary>
    /// A technology broker search. Spansh's own filter finds the Guardian ones, for every station
    /// BrokerKind calls Guardian Spansh does too. The human ones are asked for as any broker:
    /// Spansh calls a colony's station Guardian or gives it no kind.
    /// </summary>
    static Sought Broker(string name, string kind)
    {
        bool guardian = kind == "Guardian";
        return new Sought($"Nearest: {name}", guardian ? "technology_broker" : "services", guardian ? Value(kind) : new[] { new { name = Value(BrokerService) } }, Nearest,
            station => (guardian ? Same(station.TechnologyBroker, kind) : HasService(station, BrokerService)) ? new Listing("", null, station.UpdatedAt, Broker: BrokerGuess(station)) : null,
            Broker: kind);
    }

    const string BrokerService = "Technology Broker";   // as Spansh names the service

    static bool HasService(Station station, string service) => station.Services?.Any(listed => Same(listed?.Name, service)) == true;

    const string BrokerWarning = "No service records which kind of technology broker a station has. Studio goes by the station, as Inara does: a colony's station has a human broker, "
        + "and any other station a Guardian one when its main economy is High Tech and a human one when it is not. A few stations are not like that. "
        + "Once you have seen a station's broker, mark its kind, and Studio goes by that.";

    /// <summary>
    /// The kind of a station's technology broker, as far as it can be told without the player's mark: a
    /// colony's station has a human one; a megaship Spansh calls Guardian is not known; any other station's
    /// is Spansh's kind, which is its main economy in other words, and human where Spansh gives none.
    /// That is how Inara tells them apart. Matched against its lists around Col 285 Sector LT-V b17-4
    /// (21 October 2026): its 100 Guardian brokers within 126 ly are the 100 Studio calls Guardian there,
    /// with one more (Moxon's Mojo in Bolg, which Inara calls human); its 99 human ones within 91 ly are 98
    /// of the 100 Studio calls human (two colony stations newer than Inara's list).
    /// </summary>
    static string? BrokerKind(Station station) =>
        station.SystemIsColonised == true ? "Human"
        : Same(station.TechnologyBroker, "Guardian") ? (Same(station.Type, "Mega ship") ? null : "Guardian")
        : "Human";

    /// <summary>
    /// Adds to a broker search the stations where the player saw a broker of the kind looked for but
    /// that the search's own list does not hold. One question names them all by market id; the station
    /// filters count for them as for the rest. Without an answer from Spansh they are left out, which
    /// goes to the log: Spansh's own list is still the answer.
    /// </summary>
    async Task OwnBrokers(BrokerSeen[] mine, Sought sought, List<FoundPlace> rows, StationFilter filter, string from, double[]? here, MarketData.Asking ask)
    {
        string[] ids = [.. mine.Where(mark => !rows.Any(row => row.MarketId == mark.MarketId)).Select(mark => mark.MarketId.ToString(CultureInfo.InvariantCulture))];
        if (ids.Length == 0) return;
        try
        {
            (StationPage page, _) = await Page(Question(new() { ["market_id"] = Value(ids) }, filter, ids.Length, from, here), here is null ? from : null, ask);
            foreach (Station station in page.Results?.OfType<Station>() ?? [])
            {
                // One the search's own list holds comes in it, on whichever page its distance puts it.
                if (sought.At(station) is not null || Kept(station, filter) is not { } kept) continue;
                rows.Add(new FoundPlace(station.Name ?? "", station.SystemName ?? "", Math.Round(station.Distance ?? 0, 2), station.DistanceToArrival,
                    StationFilter.PadLetter(kept.Pad), Shown(kept.Type), "", null, null, MarketData.Time(station.UpdatedAt), station.MarketId ?? 0,
                    Broker: station.TechnologyBroker is { Length: > 0 } other ? $"Spansh guesses {other}" : "Spansh gives no kind", BrokerSeen: sought.Broker));
            }
        }
        catch (Exception problem) when (Lost(problem))
        {
            App.Log.Error($"The {sought.Broker} brokers the player marked could not be fetched from Spansh, so the search lists Spansh's own only", problem);
        }
    }

    /// <summary>Marks the kind of technology broker the player saw at a station, or takes the mark away (kind "").</summary>
    public BrokerMarked MarkBroker(BrokerRequest request)
    {
        if (brokers is null) throw new FindProblem("Studio keeps no broker marks here.");
        if (request.MarketId <= 0) throw new FindProblem("Studio does not know that station's market number, so its broker cannot be marked.");
        string kind = request.Kind.Trim();
        if (kind.Length > 0) kind = BrokerBook.Kinds.FirstOrDefault(known => Same(known, kind)) ?? throw new FindProblem($"A broker is Human or Guardian, not \"{kind}\".");
        try
        {
            brokers.Mark(request.MarketId, request.Station.Trim(), request.System.Trim(), kind, utcNow());
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            App.Log.Error($"The broker mark for {request.Station} could not be saved", problem);
            throw new FindProblem("Studio could not save the mark: " + problem.Message, 500);
        }
        App.Log.Info(Invariant($"Broker at {request.Station} in {request.System} ({request.MarketId}): {(kind.Length > 0 ? "marked " + kind : "mark taken away")}"));
        return new BrokerMarked(request.MarketId, kind);
    }

    /// <summary>
    /// What a station's broker's kind (<see cref="BrokerKind"/>) rests on, in words for a row: "a
    /// colony's station", or "a guess from its High Tech economy", with the main economy's part of the
    /// station when it has several ("(80%)"). The parts of a colony's station need not add up to a
    /// hundred, so the part is worked out of their sum.
    /// </summary>
    static string BrokerGuess(Station station)
    {
        if (station.SystemIsColonised == true) return "a colony's station";
        Part[] parts = [.. (station.Economies ?? []).OfType<Part>().Where(part => part.Name is { Length: > 0 } && part.Share is > 0)];
        string? main = station.PrimaryEconomy is { Length: > 0 } named ? named : parts.MaxBy(part => part.Share)?.Name;
        if (main is null) return "a guess";
        double all = parts.Sum(part => part.Share ?? 0);
        double? share = parts.Length > 1 ? parts.FirstOrDefault(part => Same(part.Name, main))?.Share : null;
        return $"a guess from its {main} economy" + (share is { } piece ? Invariant($" ({Math.Round(piece / all * 100)}%)") : "");
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
