using System.Text.Json.Serialization;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Market;
using static System.FormattableString;

namespace EliteStudio.Loadouts;

/// <summary>One kind of module the ship still lacks to become a build.</summary>
/// <param name="Item">The game's code for the module, in lower case.</param>
/// <param name="Name">What the outfitting screen calls it.</param>
/// <param name="Mount">"Fixed", "Gimballed" or "Turreted" for a weapon, otherwise "".</param>
/// <param name="Size">Its size number; 0 for a utility.</param>
/// <param name="Count">How many of it are missing.</param>
public sealed record Lacking(string Item, string Name, string Mount, int Size, string Rating, int Count)
{
    /// <summary>As a shopping list writes it: "5A Frame Shift Drive", "2F Pulse Laser, gimballed", "A Shield Booster".</summary>
    public string Label => Invariant($"{(Size > 0 ? Size : null)}{Rating} {Name}").TrimStart() + (Mount.Length > 0 ? ", " + Mount.ToLowerInvariant() : "");
}

/// <summary>What the ship being flown lacks to become a saved build: the answer to GET /api/loadouts/shopping.</summary>
/// <param name="ShipType">The build's ship, by the name players know it.</param>
/// <param name="NewHull">
/// The build is of another ship than the one flown. That ship has to be bought first; it comes
/// with its plainest modules, as the designer's empty hull has them, and the rest is listed.
/// </param>
/// <param name="Modules">In the order of the outfitting screen. Engineering is not shopping: a module is the same module with or without it.</param>
/// <param name="Engineered">How many of the build's modules are engineered, which the planner can pin.</param>
public sealed record ShoppingList(string ShipType, bool NewHull, IReadOnlyList<Lacking> Modules, int Engineered);

/// <summary>One thing to buy at a stop.</summary>
/// <param name="What">In words: "5A Frame Shift Drive", or "the Mandalay itself".</param>
/// <param name="Price">Credits for one, as last reported there.</param>
public sealed record Purchase(string What, int Count, long Price);

/// <summary>One stop of a shopping trip: a station, and what to buy there.</summary>
/// <param name="Address">The game's own number for the system, written as text because a web page cannot hold it exactly; 0 when not known.</param>
/// <param name="X">Where the system lies, in light years (with <paramref name="Y"/> and <paramref name="Z"/>).</param>
/// <param name="Ly">Light years from the stop before; for the first stop, from the ship.</param>
/// <param name="Updated">When the station's outfitting was last reported (UTC); null when not known.</param>
/// <param name="Note">The stop in one sentence, which Studio Nav shows with it: "Dock at Jameson Memorial and buy ...".</param>
public sealed record ShoppingStop(
    string Station, string System,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Address,
    double X, double Y, double Z, double Ly, double? StarLs, string Pad, string Type, DateTime? Updated, IReadOnlyList<Purchase> Buy, string Note);

/// <summary>Where to buy a shopping list: the answer to POST /api/loadouts/shopping/stops.</summary>
/// <param name="Name">What Studio Nav calls the trip: "Shopping for Explorer".</param>
/// <param name="From">The system the ship is in, which the trip starts from.</param>
/// <param name="Stops">In flying order.</param>
/// <param name="NotFound">What no station found sells, in words; empty when the stops cover the list.</param>
/// <param name="Questions">How many questions it took. Spansh may have been asked fewer: an answer is remembered for ten minutes.</param>
/// <param name="CutShort">The asking stopped at its limit with things on the list that had not been asked about; they are among <paramref name="NotFound"/>.</param>
public sealed record ShoppingTrip(
    string Name, string From, IReadOnlyList<ShoppingStop> Stops, IReadOnlyList<string> NotFound, int Questions, bool CutShort, DateTime AskedUtc);

/// <summary>The sums of a shopping list. Pure functions.</summary>
public static class Shopping
{
    /// <summary>
    /// What a ship lacks to become a build: the build's modules that are not on board, each kind
    /// counted (a build with three shield boosters on a ship with one lacks two). Only what the
    /// outfitting screen's four lists hold counts; paint and the cargo hatch are not bought.
    /// </summary>
    /// <param name="have">The modules that are there to start with: the ship's own, or for another hull what it leaves the shipyard with.</param>
    public static ShoppingList List(ShipLoadout build, IEnumerable<ShipModule> have, bool newHull)
    {
        // A starter ship's free modules are the plain ones under another name.
        static string Code(ShipModule module) => (module.Item.EndsWith("_free", StringComparison.OrdinalIgnoreCase) ? module.Item[..^5] : module.Item).ToLowerInvariant();
        Dictionary<string, int> held = have.GroupBy(Code).ToDictionary(kind => kind.Key, kind => kind.Count());
        var modules = new List<Lacking>();
        foreach (var kind in build.Modules.Select(module => (Module: module, Place: ShipView.Place(module.Slot))).Where(module => module.Place is not null)
            .OrderBy(module => module.Place!.Value.Group).ThenBy(module => module.Place!.Value.Order).GroupBy(module => Code(module.Module)))
        {
            int missing = kind.Count() - held.GetValueOrDefault(kind.Key);
            var (name, mount, size, rating) = Names.Module(kind.Key);
            if (missing > 0) modules.Add(new Lacking(kind.Key, name, mount, size, rating, missing));
        }
        return new ShoppingList(ShipMath.ShipName(build.Ship), newHull, modules, build.Modules.Count(module => module.Engineering is not null));
    }

    /// <summary>So many of a thing, as a list writes it: "2 × A Shield Booster".</summary>
    internal static string Counted(int count, string what) => count > 1 ? Invariant($"{count} × {what}") : what;

    /// <summary>
    /// Picks the stops for a shopping list from the stations found: as few as will do, and of two
    /// choices the one nearer the ship. First the station that sells the most of the list, then
    /// the one that sells the most of what is still open, until nothing more can be had; a station
    /// that later picks have made needless goes again. A ship has to be bought before anything is
    /// fitted to it, so its seller is the first stop: of the stations that sell it, the one that
    /// also sells the most modules. The other stops follow in flying order, always the nearest
    /// next, and each thing is bought at the first stop on the way that sells it.
    /// (The fewest stops of all would mean trying every set of stations; picking the fullest
    /// first comes close, and is what a player does by hand.)
    /// </summary>
    /// <param name="hull">Spansh's name for the ship to buy first, or null when the ship flown is the build's.</param>
    /// <param name="here">Where the ship is: [x, y, z] in light years; null when the game has not said.</param>
    /// <returns>The stops in flying order, and what none of the stations sells, in words.</returns>
    public static (IReadOnlyList<ShoppingStop> Stops, IReadOnlyList<string> NotFound) Choose(
        IReadOnlyList<Lacking> modules, string? hull, IReadOnlyList<Seller> sellers, double[]? here)
    {
        var open = modules.Select(module => module.Item).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int Covers(Seller seller) => open.Count(seller.Modules.ContainsKey);
        // Of two that sell as much of what is still open, the one that sells more of the whole list: it may make another stop needless.
        Seller? Best(IEnumerable<Seller> among) => among.OrderByDescending(Covers)
            .ThenByDescending(seller => modules.Count(module => seller.Modules.ContainsKey(module.Item))).ThenBy(seller => seller.DistanceLy).FirstOrDefault();

        var chosen = new List<Seller>();
        Seller? yard = hull is null ? null : Best(sellers.Where(seller => seller.Ships.ContainsKey(hull)));
        for (Seller? next = yard ?? Best(sellers); next is not null && (next == yard || Covers(next) > 0); next = Best(sellers.Except(chosen)))
        {
            chosen.Add(next);
            open.ExceptWith(next.Modules.Keys);
        }
        foreach (Seller seller in chosen.OrderByDescending(seller => seller.DistanceLy).ToArray())   // the farthest goes first
            if (seller != yard && modules.All(module => !seller.Modules.ContainsKey(module.Item) || chosen.Any(other => other != seller && other.Modules.ContainsKey(module.Item))))
                chosen.Remove(seller);

        var left = modules.ToList();
        var stops = new List<ShoppingStop>();
        double[]? from = here;
        double Leg(Seller to) => from is null ? to.DistanceLy : ShipMath.Distance(from, to.At);
        while (chosen.Count > 0)
        {
            Seller stop = stops.Count == 0 && yard is not null ? yard : chosen.MinBy(Leg)!;
            chosen.Remove(stop);
            List<Purchase> buy = stop == yard ? [new Purchase($"the {hull} itself", 1, stop.Ships[hull!])] : [];
            buy.AddRange(left.Where(module => stop.Modules.ContainsKey(module.Item)).Select(module => new Purchase(module.Label, module.Count, stop.Modules[module.Item])));
            left.RemoveAll(module => stop.Modules.ContainsKey(module.Item));
            stops.Add(new ShoppingStop(
                stop.Station, stop.System, stop.SystemAddress, stop.At[0], stop.At[1], stop.At[2], Math.Round(Leg(stop), 2), stop.StarLs, stop.Pad, stop.Type, stop.Updated,
                buy, $"Dock at {stop.Station} and buy {string.Join(", ", buy.Select(thing => Counted(thing.Count, thing.What)))}."));
            from = stop.At;
        }
        IEnumerable<string> noYard = hull is not null && yard is null ? [$"the {hull} itself"] : [];
        return (stops, [.. noYard, .. left.Select(module => Counted(module.Count, module.Label))]);
    }
}

/// <summary>The part of Studio Loadouts that hands a saved build on to the other studios: its shopping list to Studio Market and Studio Nav, its engineering to the planner.</summary>
internal sealed partial class StudioLoadouts
{
    /// <summary>
    /// The most questions one shopping list puts to Spansh. The first asks for a station that sells
    /// everything; each one after that is about a single thing no station seen so far sells. An
    /// answer runs to a million letters, for every station comes with all it sells.
    /// </summary>
    internal const int MostQuestions = 9;

    /// <summary>Registers the routes of the hand-offs (they are listed with the others, at <see cref="LoadoutsApi"/>).</summary>
    public void MapHandOffs(WebServer web, GameState game, EngineeringPlanner planner, Finder finder)
    {
        web.Map("GET", "/api/loadouts/shopping", async request =>
            await Read(request.Query.GetValueOrDefault("id") ?? "") is not { } saved ? WebResponse.Error(NotFound, 404)
            : current is not { } flown ? WebResponse.Error(NoShip, 409)
            : WebResponse.Json(Lacks(ShipLoadout.Parse(flown.Loadout), ShipLoadout.Parse(saved.Loadout))));

        web.Map("POST", "/api/loadouts/shopping/stops", async request =>
            await Read(request.Read<Body>().Id) is not { } saved ? WebResponse.Error(NotFound, 404)
            : current is not { } flown ? WebResponse.Error(NoShip, 409)
            : await FindApi.Answered(() => Stops(saved.Name, ShipLoadout.Parse(flown.Loadout), ShipLoadout.Parse(saved.Loadout), game, finder)));

        web.Map("POST", "/api/loadouts/pin", async request =>
        {
            if (await Read(request.Read<Body>().Id) is not { } saved) return WebResponse.Error(NotFound, 404);
            var (added, already, notOffered) = planner.PinBuild(ShipLoadout.Parse(saved.Loadout));
            return WebResponse.Json(new { added, already, notOffered });
        });
    }

    ShoppingList Lacks(ShipLoadout flown, ShipLoadout build)
    {
        bool newHull = !flown.Ship.Equals(build.Ship, StringComparison.OrdinalIgnoreCase);
        return Shopping.List(build,
            !newHull ? flown.Modules : ShipData.Hulls.TryGetValue(build.Ship, out ShipData.Hull? hull) ? ShipLoadout.Parse(EmptyHull(hull)).Modules : [], newHull);
    }

    /// <summary>
    /// Where to buy what the ship lacks, near the player first. Spansh is asked once for the
    /// stations that sell all of it: the nearest of those is the whole trip. When there is none, it
    /// is asked about one thing at a time, though only about what no station seen so far sells
    /// (every station comes with all it sells, so the first answers usually cover the common
    /// modules) and at most <see cref="MostQuestions"/> times; <see cref="Shopping.Choose"/> then
    /// picks the stops. The player's station filters count, and the landing pad has to take the
    /// ship flown and the build's ship.
    /// </summary>
    async Task<ShoppingTrip> Stops(string name, ShipLoadout flown, ShipLoadout build, GameState game, Finder finder)
    {
        ShoppingList list = Lacks(flown, build);
        // Spansh is asked by its own names. It files a module under the name the game's tables give its code last, which
        // for some twenty everyday modules is a special edition's ("Balanced Power Distributor" for the plain 5A one).
        // What it lists under neither that name nor ours cannot be asked for: said plainly, like what nobody sells.
        static string Plain(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        IReadOnlyList<string> listed = (await finder.Names("module")).Names;
        var spansh = new Dictionary<string, string>();   // Spansh's name for each module of the list it knows, by the module's code
        foreach (Lacking module in list.Modules)
            if (new[] { GameData.SpecialEdition(module.Item), module.Name }.FirstOrDefault(name => name is not null && listed.Contains(name, StringComparer.OrdinalIgnoreCase)) is { } known)
                spansh[module.Item] = known;
        Lacking[] modules = [.. list.Modules.Where(module => spansh.ContainsKey(module.Item))];
        string? hull = !list.NewHull ? null
            : (await finder.Names("ship")).Names.FirstOrDefault(ship => Plain(ship) == Plain(list.ShipType) || Plain(ship) == Plain(GameData.Ships.GetValueOrDefault(build.Ship)?.Name ?? ""));
        IEnumerable<string> noShip = list.NewHull && hull is null ? [$"the {list.ShipType} itself"] : [];
        string[] unlisted = [.. noShip, .. list.Modules.Except(modules).Select(module => Shopping.Counted(module.Count, module.Label))];

        var seen = new Dictionary<(string Station, string System), Seller>();
        int questions = 0;
        bool cutShort = false;
        DateTime asked = DateTime.UtcNow;
        int pad = Math.Max(ShipMath.ShipPad(flown.Ship), ShipMath.ShipPad(build.Ship));
        async Task Ask(IReadOnlyList<Lacking> wanted, string? ship)
        {
            var (sellers, at) = await finder.Sellers([.. wanted.Select(module => (spansh[module.Item], module.Size, module.Rating))], ship, pad);
            if (questions++ == 0) asked = at;
            foreach (Seller seller in sellers) seen.TryAdd((seller.Station, seller.System), seller);
        }

        if (modules.Length > 0 || hull is not null) await Ask(modules, hull);
        bool all = seen.Values.Any(seller => (hull is null || seller.Ships.ContainsKey(hull)) && modules.All(module => seller.Modules.ContainsKey(module.Item)));
        if (!all && modules.Length + (hull is null ? 0 : 1) > 1)
        {
            if (hull is not null && !seen.Values.Any(seller => seller.Ships.ContainsKey(hull))) await Ask([], hull);
            foreach (Lacking module in modules)
            {
                if (seen.Values.Any(seller => seller.Modules.ContainsKey(module.Item))) continue;
                if (questions < MostQuestions) await Ask([module], null);
                else cutShort = true;
            }
        }

        var (stops, notFound) = Shopping.Choose(modules, hull, [.. seen.Values], game.StarPos);
        Log.Info($"Shopping for \"{name}\": {list.Modules.Count} kinds of module{(list.NewHull ? " and the ship" : "")}, {questions} questions, {stops.Count} stops, {unlisted.Length + notFound.Count} not found");
        return new ShoppingTrip($"Shopping for {name}", game.StarSystem ?? "", stops, [.. unlisted, .. notFound], questions, cutShort, asked);
    }
}
