using System.Text.Json;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Loadouts;

/// <summary>
/// The Guardian helper's one route and its "guardian" event.
///   GET /api/loadouts/guardian/list   everything a Guardian technology broker unlocks, what the commander holds
///                                     toward each, which are unlocked already, where the blueprint segments
///                                     for the rest are found, and every known Guardian site (<see cref="GuardianView"/>)
/// Every "guardian" event carries the same picture, sent whenever it changes: materials or cargo picked up,
/// something unlocked, a jump, or the search of the older journals coming in.
/// </summary>
public static class GuardianApi
{
    /// <summary>Registers the route. Call on the UI thread, before the game's files are first read (see <see cref="GuardianHelper"/>).</summary>
    public static void Map(WebServer web, GameState game, Settings settings)
    {
        var helper = new GuardianHelper(game, settings);
        helper.Changed += () => web.Send("guardian", helper.View());
        web.Map("GET", "/api/loadouts/guardian/list", _ => WebResponse.Json(helper.View()));
    }
}

/// <summary>
/// The "guardian" section of settings.json: what the journals have told of technology-broker
/// unlocks so far. The game writes an unlock down once, on the day; without this every start
/// would have to search every journal the commander ever played.
/// </summary>
public sealed class GuardianSettings
{
    /// <summary>The game's codes of everything unlocked at any technology broker, Guardian or not.</summary>
    public List<string> Unlocked { get; set; } = [];

    /// <summary>Journals last written before this time have been searched (<see cref="GameState.UnlocksReadTo"/>).</summary>
    public DateTime JournalsReadTo { get; set; }
}

/// <summary>One line of a recipe, against what the commander holds.</summary>
/// <param name="Kind">"Raw", "Manufactured" or "Encoded" for a material; "Commodity" for cargo, which has to be in the ship's hold.</param>
public sealed record GuardianPart(string Name, string Kind, int Need, int Have);

/// <summary>One thing a Guardian technology broker unlocks.</summary>
/// <param name="Group">"Modules", "Weapons" or "Fighters".</param>
/// <param name="Detail">Which of several: "Fixed, small", "Sizes 2 to 8".</param>
/// <param name="Blueprint">The code of the blueprint segment its recipe takes; one of <see cref="GuardianView.Blueprints"/>.</param>
public sealed record GuardianItem(string Group, string Name, string Detail, bool Unlocked, string Blueprint, IReadOnlyList<GuardianPart> Recipe);

/// <summary>A Guardian structure whose data terminal gives a blueprint segment.</summary>
/// <param name="Ly">Light years from the system the ship is in; null while that is not known.</param>
/// <param name="BeaconSystem">For the structures that need an Ancient Key: the system of the Guardian beacon nearest to this site (any beacon's key opens any such site).</param>
public sealed record GuardianSite(string System, string Body, double? Ly, string? BeaconSystem, string? BeaconBody);

/// <summary>One of the three kinds of blueprint segment, and where it is found.</summary>
/// <param name="Need">How many the recipes of everything still locked take between them.</param>
/// <param name="Layouts">The shapes of Guardian structure that give it, by the names explorers use for them.</param>
/// <param name="Key">What the structure's data terminal takes before it gives the segment up.</param>
/// <param name="Sites">The three nearest to the ship (to Sol while the ship's place is not known).</param>
public sealed record GuardianBlueprint(
    string Code, string Name, int Have, int Need, IReadOnlyList<string> Layouts, string Key, IReadOnlyList<GuardianSite> Sites);

/// <summary>One known Guardian site, for the page's list of all of them.</summary>
/// <param name="Kind">
/// What it is for: "weapon", "module" or "vessel" (a structure whose data terminal gives that blueprint segment),
/// "materials" (a small structure with no data terminal), "beacon" (in space: it gives the Ancient Key) or "ruins"
/// (obelisk data).
/// </param>
/// <param name="Layouts">
/// The structure's shape by the name explorers use (Bear, Turtle...) or "Beacon"; for ruins the type of each ruin on
/// the body, as several often share one ("Alpha", "Gamma", "Gamma").
/// </param>
/// <param name="Ly">Light years from the system the ship is in; null while that is not known.</param>
public sealed record GuardianPlace(string Kind, IReadOnlyList<string> Layouts, string System, string Body, double? Ly);

/// <summary>Everything the Guardian page shows: the answer to GET list and the data of every "guardian" event.</summary>
/// <param name="HistoryRead">False while the commander's older journals have never been searched for unlocks: the search is still running.</param>
/// <param name="Places">Every known Guardian site, nearest to the ship first (to Sol while the ship's place is not known).</param>
public sealed record GuardianView(IReadOnlyList<GuardianItem> Items, IReadOnlyList<GuardianBlueprint> Blueprints, bool HistoryRead, IReadOnlyList<GuardianPlace> Places);

/// <summary>
/// The Guardian checklist against the game. Lives on the UI thread like the game state it reads.
/// It is also what remembers the technology-broker unlocks between runs: it hands the saved ones to
/// the game state when it is made, which has to be before the first Poll for the older journals
/// not to be searched all over again, and saves what the search and the game add.
///
/// THE RECIPES below were taken from two community programs and compared line by line:
///   EDEngineer (github.com/msarilar/EDEngineer, Resources/Data/blueprints.json, type "Guardian") and
///   ED Odyssey Materials Helper (github.com/jixxed/ed-odyssey-materials-helper, release 3.13.0,
///   constants/horizons/TechbrokerBlueprints.java).
/// The two agree on every number. A third and fourth were read as well: the wiki
/// (elite-dangerous.fandom.com, "Technology Broker" and "Guardian Plasma Charger") agrees too;
/// EDDiscovery's table, copied from Inara in 2024, has 18 instead of 19 Guardian Power Conduits for
/// the medium fixed plasma charger and 20 instead of 26 for the large turreted one. The three that
/// agree are followed. The fighters' codes are EDDiscovery's; that v1, v2 and v3 are the Trident,
/// the Javelin and the Lance is also how EDDI names them.
/// Left out: the six "modified" Guardian weapons, which only a few special brokers sell. The
/// sources do not agree on their recipes, and their codes in the journal could not be confirmed.
///
/// THE SITES: which shape of structure gives which segment, and what its terminal takes, is from
/// Canonn's survey (canonn.science/codex/guardian-structure) and the wiki ("Ancient Data Terminal",
/// "Guardian Structure"); ED Odyssey Materials Helper's own list says the same. That a beacon gives
/// an Ancient Key that opens any vessel site, what Sentinels and panels drop, and that obelisk data
/// comes by chance (Delta and Epsilon rarely) are from Canonn, the wiki, Inara and players' guides.
/// The sites themselves are in Core\Data\guardian_sites.csv: Canonn's survey of ruins and structures
/// (github.com/canonn-science/GR-RuinsComboMap) joined with ED Odyssey Materials Helper's list of
/// structures and beacons, which agree on every structure both name. The file says how it is made.
/// </summary>
public sealed class GuardianHelper
{
    const string Weapon = "Guardian_WeaponBlueprint", Module = "Guardian_ModuleBlueprint", Vessel = "Guardian_VesselBlueprint";

    // Shorter names for the codes the recipes are written in: materials first, then the commodities.
    const string Cell = "Guardian_PowerCell", Conduit = "Guardian_PowerConduit", Tech = "Guardian_TechComponent",
        Parts = "Guardian_Sentinel_WeaponParts", Wreckage = "Guardian_Sentinel_WreckageComponents",
        Alpha = "AncientBiologicalData", Beta = "AncientCulturalData", Gamma = "AncientHistoricalData",
        Delta = "AncientLanguageData", Epsilon = "AncientTechnologicalData";

    /// <param name="Code">
    /// The game's code as a TechnologyBroker line writes it. For a module that comes in sizes it is the
    /// part before "_size": one unlock opens them all.
    /// </param>
    internal sealed record Unlock(string Group, string Name, string Detail, string Code, params (string Code, int Count)[] Recipe);

    internal static readonly Unlock[] Unlocks =
    [
        new("Modules", "Guardian FSD Booster", "Sizes 1 to 5", "Int_GuardianFSDBooster",
            (Module, 1), (Cell, 21), (Tech, 21), ("FocusCrystals", 24), ("HNShockMount", 8)),
        new("Modules", "Guardian Hybrid Power Plant", "Sizes 2 to 8", "Int_GuardianPowerplant",
            (Module, 1), (Conduit, 18), (Epsilon, 21), ("HeatResistantCeramics", 15), ("PowerGridAssembly", 10)),
        new("Modules", "Guardian Hybrid Power Distributor", "Sizes 1 to 8", "Int_GuardianPowerDistributor",
            (Module, 1), (Alpha, 20), (Cell, 24), ("PhaseAlloys", 18), ("HeatsinkInterlink", 6)),
        new("Modules", "Guardian Hull Reinforcement", "Sizes 1 to 5", "Int_GuardianHullReinforcement",
            (Module, 1), (Wreckage, 21), (Beta, 16), (Gamma, 16), ("ReinforcedMountingPlate", 12)),
        new("Modules", "Guardian Module Reinforcement", "Sizes 1 to 5", "Int_GuardianModuleReinforcement",
            (Module, 1), (Wreckage, 18), (Epsilon, 15), (Conduit, 20), ("ReinforcedMountingPlate", 9)),
        new("Modules", "Guardian Shield Reinforcement", "Sizes 1 to 5", "Int_GuardianShieldReinforcement",
            (Module, 1), (Cell, 17), (Tech, 20), (Delta, 24), ("DiagnosticSensor", 8)),

        new("Weapons", "Guardian Gauss Cannon", "Fixed, small", "Hpt_Guardian_GaussCannon_Fixed_Small",
            (Weapon, 1), (Conduit, 12), (Wreckage, 12), (Parts, 15)),
        new("Weapons", "Guardian Gauss Cannon", "Fixed, medium", "Hpt_Guardian_GaussCannon_Fixed_Medium",
            (Weapon, 1), (Cell, 18), (Tech, 20), ("Manganese", 15), ("MagneticEmitterCoil", 6)),
        new("Weapons", "Guardian Plasma Charger", "Fixed, small", "Hpt_Guardian_PlasmaLauncher_Fixed_Small",
            (Weapon, 1), (Cell, 12), (Parts, 12), (Tech, 15)),
        new("Weapons", "Guardian Plasma Charger", "Fixed, medium", "Hpt_Guardian_PlasmaLauncher_Fixed_Medium",
            (Weapon, 1), (Conduit, 19), (Parts, 16), ("Chromium", 14), ("CoolingHoses", 8)),
        new("Weapons", "Guardian Plasma Charger", "Fixed, large", "Hpt_Guardian_PlasmaLauncher_Fixed_Large",
            (Weapon, 1), (Conduit, 28), (Parts, 20), ("Chromium", 28), ("CoolingHoses", 10)),
        new("Weapons", "Guardian Plasma Charger", "Turreted, small", "Hpt_Guardian_PlasmaLauncher_Turret_Small",
            (Weapon, 1), (Cell, 12), (Tech, 12), (Parts, 15)),
        new("Weapons", "Guardian Plasma Charger", "Turreted, medium", "Hpt_Guardian_PlasmaLauncher_Turret_Medium",
            (Weapon, 2), (Conduit, 21), (Parts, 20), ("Chromium", 16), ("ArticulationMotors", 8)),
        new("Weapons", "Guardian Plasma Charger", "Turreted, large", "Hpt_Guardian_PlasmaLauncher_Turret_Large",
            (Weapon, 2), (Conduit, 26), (Parts, 24), ("Chromium", 26), ("ArticulationMotors", 10)),
        new("Weapons", "Guardian Shard Cannon", "Fixed, small", "Hpt_Guardian_ShardCannon_Fixed_Small",
            (Weapon, 1), (Conduit, 12), (Tech, 12), (Parts, 15)),
        new("Weapons", "Guardian Shard Cannon", "Fixed, medium", "Hpt_Guardian_ShardCannon_Fixed_Medium",
            (Weapon, 1), (Tech, 18), (Wreckage, 20), ("Carbon", 14), ("PowerTransferConduits", 12)),
        new("Weapons", "Guardian Shard Cannon", "Fixed, large", "Hpt_Guardian_ShardCannon_Fixed_Large",
            (Weapon, 1), (Tech, 28), (Wreckage, 20), ("Carbon", 20), ("MicroControllers", 18)),
        new("Weapons", "Guardian Shard Cannon", "Turreted, small", "Hpt_Guardian_ShardCannon_Turret_Small",
            (Weapon, 1), (Conduit, 12), (Parts, 12), (Tech, 15)),
        new("Weapons", "Guardian Shard Cannon", "Turreted, medium", "Hpt_Guardian_ShardCannon_Turret_Medium",
            (Weapon, 2), (Wreckage, 16), (Tech, 20), ("Carbon", 15), ("MicroControllers", 12)),
        new("Weapons", "Guardian Shard Cannon", "Turreted, large", "Hpt_Guardian_ShardCannon_Turret_Large",
            (Weapon, 2), (Wreckage, 20), (Tech, 26), ("Carbon", 28), ("MicroControllers", 12)),

        new("Fighters", "XG7 Trident", "Ship-launched fighter", "GDN_Hybrid_Fighter_V1",
            (Vessel, 1), (Cell, 25), (Epsilon, 26), (Beta, 18), (Tech, 25)),
        new("Fighters", "XG8 Javelin", "Ship-launched fighter", "GDN_Hybrid_Fighter_V2",
            (Vessel, 1), (Cell, 25), (Wreckage, 18), (Epsilon, 26), (Tech, 25)),
        new("Fighters", "XG9 Lance", "Ship-launched fighter", "GDN_Hybrid_Fighter_V3",
            (Vessel, 1), (Cell, 25), (Parts, 18), (Epsilon, 26), (Tech, 25)),
    ];

    /// <summary>One line of Core\Data\guardian_sites.csv: a site, what it is for (see <see cref="GuardianPlace.Kind"/>) and its system's place in light years.</summary>
    internal sealed record Site(string Kind, string Layout, string System, string Body, double X, double Y, double Z);

    /// <summary>Every known Guardian site, read once from the program's own copy of the list.</summary>
    internal static IReadOnlyList<Site> Sites => sites.Value;

    static readonly Lazy<Site[]> sites = new(() =>
    {
        using Stream file = typeof(GameData).Assembly.GetManifestResourceStream("Data.guardian_sites.csv")
            ?? throw new InvalidOperationException("The list of Guardian sites is missing from the program");
        using var reader = new StreamReader(file);
        var list = new List<Site>();
        bool heading = true;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (heading) { heading = false; continue; }
            string[] r = line.Split(',');
            list.Add(new Site(r[0], r[1], r[2], r[3], Number(r[4]), Number(r[5]), Number(r[6])));
        }
        return [.. list];
    });

    static double Number(string text) => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The three kinds of blueprint segment: what the list calls the sites that give each, their shapes, and what the terminal takes.</summary>
    static readonly (string Code, string Kind, string[] Layouts, string Key)[] Segments =
    [
        (Weapon, "weapon", ["Bear", "Bowl", "Hammerbot"], "Guardian Relic"),
        (Module, "module", ["Turtle"], "Guardian Relic"),
        (Vessel, "vessel", ["Squid", "Stickyhand", "Robolobster"], "Ancient Key"),
    ];

    readonly GameState game;
    readonly Settings settings;
    (int Unlocked, DateTime ReadTo) kept;   // what settings.json holds of the unlocks, to see when the game knows more
    string told;                            // the picture as it was last told, to see whether a change in the game changed it

    public GuardianHelper(GameState game, Settings settings)
    {
        (this.game, this.settings) = (game, settings);
        GuardianSettings saved = settings.Get<GuardianSettings>("guardian");
        string[] codes = [.. (saved.Unlocked ?? []).Where(code => !string.IsNullOrEmpty(code))];   // the file may have been changed by hand
        game.AddUnlocks(codes, saved.JournalsReadTo);
        kept = (codes.Length, saved.JournalsReadTo);
        told = JsonSerializer.Serialize(View());
        game.Changed += Follow;
    }

    /// <summary>Raised after something in the game changed the picture.</summary>
    public event Action? Changed;

    public GuardianView View()
    {
        GuardianItem[] items =
        [
            .. Unlocks.Select(unlock => new GuardianItem(
                unlock.Group, unlock.Name, unlock.Detail, IsUnlocked(unlock), unlock.Recipe[0].Code, [.. unlock.Recipe.Select(part => Part(part.Code, part.Count))])),
        ];
        // Distances are from the ship; while its place is not known, the nearest to Sol come first and no distance is given.
        double[] from = game.StarPos ?? [0, 0, 0];
        double Away(Site site) => ShipMath.Distance(from, [site.X, site.Y, site.Z]);
        double? Ly(Site site) => game.StarPos is null ? null : Math.Round(Away(site), 1);
        Site[] beacons = [.. Sites.Where(site => site.Kind == "beacon")];
        Site? NearestBeacon(Site to) => beacons.MinBy(beacon => ShipMath.Distance([to.X, to.Y, to.Z], [beacon.X, beacon.Y, beacon.Z]));
        return new GuardianView(
            items,
            [
                .. Segments.Select(segment => new GuardianBlueprint(
                    segment.Code, GameData.Materials[segment.Code].Name, game.Materials.GetValueOrDefault(segment.Code),
                    items.Where(item => !item.Unlocked && item.Blueprint == segment.Code).Sum(item => item.Recipe[0].Need),
                    segment.Layouts, segment.Key,
                    [
                        .. Sites.Where(site => site.Kind == segment.Kind).OrderBy(Away).Take(3).Select(site =>
                        {
                            Site? beacon = segment.Code == Vessel ? NearestBeacon(site) : null;
                            return new GuardianSite(site.System, site.Body, Ly(site), beacon?.System, beacon?.Body);
                        }),
                    ])),
            ],
            game.UnlocksReadTo != default,
            [
                // a body with several ruins is one place, with the type of each
                .. Sites.GroupBy(site => site.Kind == "ruins" ? $"ruins|{site.System}|{site.Body}" : $"{site.Kind}|{site.System}|{site.Body}|{site.Layout}")
                    .Select(group => (Site: group.First(), Layouts: group.Select(site => site.Layout).Order(StringComparer.Ordinal).ToArray()))
                    .OrderBy(place => Away(place.Site)).ThenBy(place => place.Site.System, StringComparer.OrdinalIgnoreCase).ThenBy(place => place.Site.Body, StringComparer.OrdinalIgnoreCase)
                    .Select(place => new GuardianPlace(place.Site.Kind, place.Layouts, place.Site.System, place.Site.Body, Ly(place.Site))),
            ]);
    }

    /// <summary>The journals name what was unlocked by the game's code; for a module in sizes they name each size.</summary>
    bool IsUnlocked(Unlock unlock) => game.Unlocked.Any(code =>
        code.Equals(unlock.Code, StringComparison.OrdinalIgnoreCase) || code.StartsWith(unlock.Code + "_size", StringComparison.OrdinalIgnoreCase));

    /// <summary>A recipe's line: a material is counted from the commander's materials, anything else is cargo and counted from the hold.</summary>
    GuardianPart Part(string code, int count) =>
        GameData.Materials.TryGetValue(code, out GameData.Material? material)
            ? new GuardianPart(material.Name, material.Kind, count, game.Materials.GetValueOrDefault(code))
            : new GuardianPart(GameData.CommodityName(code), "Commodity", count,
                game.Cargo.Where(cargo => cargo.Id.Equals(code, StringComparison.OrdinalIgnoreCase)).Sum(cargo => cargo.Count));

    void Follow()
    {
        // What the background search or the game itself added to the unlocks is kept for the next start.
        if (kept != (game.Unlocked.Count, game.UnlocksReadTo))
        {
            kept = (game.Unlocked.Count, game.UnlocksReadTo);
            try
            {
                settings.Set("guardian", new GuardianSettings { Unlocked = [.. game.Unlocked.Order(StringComparer.OrdinalIgnoreCase)], JournalsReadTo = game.UnlocksReadTo });
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                Log.Error("The technology-broker unlocks could not be saved, so the older journals will be searched again at the next start", problem);
            }
        }
        string now = JsonSerializer.Serialize(View());
        if (now == told) return;
        told = now;
        Changed?.Invoke();
    }
}
