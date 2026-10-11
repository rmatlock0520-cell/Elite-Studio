using System.Globalization;
using System.Text.Json;
using EliteStudio.Core;

namespace EliteStudio.Loadouts;

/// <summary>
/// The fixed facts of engineering, for the planner and the ship designer: which blueprints each kind
/// of module takes, what a roll of each grade costs and changes, which engineers offer it, the
/// experimental effects, where the engineers work, and the material trader's rates. Blueprints,
/// costs and engineers are read once from the bundled coriolis-data (<see cref="GameData.Coriolis"/>),
/// here and nowhere else: its "modules" section says which blueprints a kind of module takes and
/// who offers each grade, "blueprints" what a grade costs and changes, "specials" the experimental
/// effects.
/// </summary>
public static class EngineeringData
{
    /// <param name="Cost">What one roll takes: material code (as in <see cref="GameData.Materials"/>) and how many.</param>
    /// <param name="Engineers">The game's numbers of the engineers who offer this grade for this kind of module.</param>
    /// <param name="Changes">What the grade changes when rolled to the end, under the data's names for a module's numbers: 0.55 under "optmass" is 55% more.</param>
    public sealed record Grade(int Number, IReadOnlyDictionary<string, int> Cost, IReadOnlyList<long> Engineers, IReadOnlyDictionary<string, double> Changes);

    /// <param name="Code">
    /// The data's own name for the recipe, which is what a pin is saved under. It is the game's code
    /// for every recipe but one: a multi-cannon's overcharge is a recipe of its own, "MC_Overcharged".
    /// </param>
    /// <param name="Game">The game's code, as the journal writes it in a ship's build: "FSD_LongRange", "Weapon_Overcharged".</param>
    /// <param name="Grades">The grades somebody offers, lowest first. Most blueprints have five; a few stop earlier.</param>
    public sealed record Blueprint(string Code, string Game, IReadOnlyList<Grade> Grades)
    {
        /// <summary>What every page calls it, the planner as the "My ship" page and the designer: "Increased Range", "Dirty Drive Tuning".</summary>
        public string Name => Names.Blueprint(Game);
    }

    /// <param name="Code">The game's code: "special_fsd_lightweight".</param>
    public sealed record Experimental(string Code, string Name, IReadOnlyDictionary<string, int> Cost);

    /// <param name="Code">coriolis-data's short name for the kind: "fsd".</param>
    /// <param name="Blueprints">In the data's own order, which is the order the designer offers them in.</param>
    public sealed record ModuleKind(string Code, string Name, IReadOnlyList<Blueprint> Blueprints, IReadOnlyList<Experimental> Experimentals);

    /// <summary>Where an engineer works: the star system, the base, and the system's place in light years.</summary>
    public sealed record Place(string System, string Base, double X, double Y, double Z);

    /// <summary>
    /// How many rolls a grade is planned with, grades 1 to 5, until the player says otherwise. It is
    /// an assumption: the game decides by the engineer's rank and by luck. These are the numbers
    /// Coriolis starts its shopping list with (Persist.js, "matsPerGrade") and the ones ED Odyssey
    /// Materials Helper counts at an engineer of rank 5 (HorizonsBlueprintGrade.getNumberOfRolls).
    /// </summary>
    public static readonly IReadOnlyList<int> UsualRolls = [1, 2, 3, 4, 5];

    /// <summary>
    /// The material trader's rates, the same at all three kinds of trader, within one of the
    /// trader's categories (a row of its table): six of a material buy one of the next grade up,
    /// and one buys three of the next grade down; over more grades the numbers multiply. Checked
    /// against the wiki's "Material Trader" page and ED Odyssey Materials Helper's own sums
    /// (HorizonsTradeSuggestion). Trading across categories costs six times more and is left alone.
    /// </summary>
    public const int UpRate = 6, DownRate = 3;

    /// <summary>
    /// coriolis-data's short names for the kinds of module that take a blueprint, in the words of
    /// the game's outfitting screen. Fixed here because the data itself only carries the short name.
    /// </summary>
    static readonly Dictionary<string, string> KindNames = new()
    {
        ["am"] = "Auto Field-Maintenance Unit", ["bh"] = "Armour", ["bl"] = "Beam Laser", ["bsg"] = "Bi-Weave Shield Generator",
        ["c"] = "Cannon", ["cc"] = "Collector Limpet Controller", ["ch"] = "Chaff Launcher", ["cs"] = "Cargo Scanner",
        ["csl"] = "Caustic Sink Launcher", ["ec"] = "Electronic Countermeasure", ["fc"] = "Fragment Cannon",
        ["fi"] = "Frame Shift Drive Interdictor", ["fs"] = "Fuel Scoop", ["fsd"] = "Frame Shift Drive",
        ["fx"] = "Fuel Transfer Limpet Controller", ["hb"] = "Hatch Breaker Limpet Controller", ["hr"] = "Hull Reinforcement Package",
        ["hs"] = "Heat Sink Launcher", ["kw"] = "Kill Warrant Scanner", ["ls"] = "Life Support", ["mc"] = "Multi-Cannon",
        ["advmc"] = "Advanced Multi-Cannon", ["mr"] = "Missile Rack", ["amr"] = "Advanced Missile Rack", ["nl"] = "Mine Launcher",
        ["pa"] = "Plasma Accelerator", ["pc"] = "Prospector Limpet Controller", ["pd"] = "Power Distributor", ["pl"] = "Pulse Laser",
        ["po"] = "Point Defence", ["pp"] = "Power Plant", ["psg"] = "Prismatic Shield Generator", ["rf"] = "Refinery",
        ["rg"] = "Rail Gun", ["s"] = "Sensors", ["sb"] = "Shield Booster", ["scb"] = "Shield Cell Bank", ["sg"] = "Shield Generator",
        ["ss"] = "Detailed Surface Scanner", ["t"] = "Thrusters", ["tp"] = "Torpedo Pylon", ["ul"] = "Burst Laser",
        ["ws"] = "Frame Shift Wake Scanner",
    };

    /// <summary>Three names coriolis-data spells its own way, as the game's tables (FDevIDs) spell them.</summary>
    static readonly Dictionary<string, string> Respelled = new()
    {
        ["Felicty Farseer"] = "Felicity Farseer",
        ["Tod \"The Blaster\" McQuinn"] = "Tod 'The Blaster' McQuinn",
        ["Adaptive Encyptors Capture"] = "Adaptive Encryptors Capture",
    };

    /// <summary>
    /// Where each of the 38 engineers works, by the game's number for the engineer. The game's
    /// tables carry only numbers for the system and the base, so the names and the place are fixed
    /// here. Each line was checked against Spansh (spansh.co.uk/api/system/NUMBER, October 2026):
    /// the system by the game's own system number, the base by its own market number. The last
    /// thirteen (400001 on) work on suits and hand weapons, so no ship blueprint names them.
    /// </summary>
    public static readonly IReadOnlyDictionary<long, Place> Places = new Dictionary<long, Place>
    {
        [300000] = new("Leesti", "Vatermann LLC", 72.75, 48.75, 68.25),
        [300010] = new("Alioth", "Turner Metallics Inc", -33.65625, 72.46875, -20.65625),
        [300030] = new("Muang", "Broo's Legacy", 17.03125, -172.78125, -3.46875),
        [300040] = new("Beta-3 Tucani", "The Beach", 32.25, -55.1875, 23.875),
        [300050] = new("Yoru", "Nemo Cyber Party Base", 97.875, -86.90625, 64.125),
        [300080] = new("Eurybia", "Demolition Unlimited", 51.40625, -54.40625, -30.5),
        [300090] = new("Kuwemaki", "The Jet's Hole", 134.65625, -226.90625, -7.8125),
        [300100] = new("Deciat", "Farseer Inc", 122.625, -0.8125, -47.28125),
        [300110] = new("Meene", "Phoenix Base", 118.78125, -56.4375, -97.1875),
        [300120] = new("Laksak", "Trader's Rest", -21.53125, -6.3125, 116.03125),
        [300130] = new("Asura", "Sanctuary", -9550.28125, -916.65625, 19816.1875),
        [300140] = new("Sol", "Dekker's Yard", 0, 0, 0),
        [300150] = new("Tir", "The Watchtower", -9532.9375, -923.4375, 19799.125),
        [300160] = new("Khun", "Long Sight Base", -171.59375, 19.96875, -56.96875),
        [300180] = new("Wyrd", "Black Hide", -11.625, 31.53125, -3.9375),
        [300200] = new("Sirius", "Qwent Research Base", 6.25, -1.28125, -5.75),
        [300210] = new("Kuk", "Prospector's Rest", -21.28125, 69.09375, -16.3125),
        [300220] = new("Arque", "Abel Laboratory", 66.5, 38.0625, 61.125),
        [300230] = new("Shinrarta Dezhra", "Jameson Base", 55.71875, 17.59375, 27.15625),
        [300250] = new("Giryak", "Pater's Memorial", 14.6875, 27.65625, 108.65625),
        [300260] = new("Wolf 397", "Trophy Camp", 40, 79.21875, -10.40625),
        [300270] = new("Achenar", "Fortune's Loss", 67.5, -119.46875, 24.84375),
        [300280] = new("Luchtaine", "The Brig", -9523.3125, -914.46875, 19825.90625),
        [300290] = new("Los", "Kraken's Retreat", -9509.34375, -886.3125, 19820.125),
        [300300] = new("Shenve", "Cinder Dock", 351.96875, -373.46875, -711.09375),
        [400001] = new("Aurai", "Marshall's Drift", 0.9375, -47.8125, 46.28125),
        [400002] = new("Orishis", "The Jackrabbit", -31, 93.96875, -3.5625),
        [400003] = new("Siris", "Nevermore Terrace", 131.0625, -73.59375, -11.25),
        [400004] = new("Capoya", "The Last Call", -60.65625, 82.4375, -45.0625),
        [400005] = new("Jolapa", "Beck Facility", 100.1875, -41.34375, -78),
        [400006] = new("Shou Xing", "Rascal's Choice", -16.28125, -44.53125, 94.375),
        [400007] = new("Xuane", "Laszlo's Resolve", 93.875, -9.25, -32.53125),
        [400008] = new("Candiaei", "Ankh's Promise", -113.5, -4.9375, 66.84375),
        [400009] = new("Bayan", "Salamander Bank", -19.96875, 117.625, -90.46875),
        [400010] = new("Deriso", "The Divine Apparatus", -9520.3125, -909.5, 19808.75),
        [400011] = new("Desy", "Bresa Modifications", -9534.21875, -912.21875, 19792.375),
        [400012] = new("Kojeara", "Rosa's Shop", -9513.09375, -908.84375, 19814.28125),
        [400013] = new("Einheriar", "Eidolon Hold", -9557.8125, -880.1875, 19801.5625),
    };

    static readonly Lazy<(IReadOnlyList<ModuleKind> Kinds, Dictionary<string, string> Names)> tables = new(Load);

    /// <summary>Every kind of module that some engineer works on, by name.</summary>
    public static IReadOnlyList<ModuleKind> Kinds => tables.Value.Kinds;

    /// <summary>
    /// The data's own name for every recipe it has, by the game's code, each word with a capital:
    /// "Increased Range" for "FSD_LongRange". <see cref="Names.Blueprint"/> makes the name the pages
    /// show from it, so ask there.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> RecipeNames => tables.Value.Names;

    /// <summary>
    /// A kind of module's blueprint by the game's code for it, as a ship's build has it. The game
    /// writes one recipe under several codes ("Sensor_Sensor_LightWeight" beside
    /// "Sensor_LightWeight"), so a code the kind lacks is taken for its blueprint that ends in the
    /// same word. Null for a blueprint no engineer offers this kind of module.
    /// </summary>
    public static Blueprint? BlueprintOf(ModuleKind kind, string gameCode)
    {
        string ending = "_" + gameCode[(gameCode.LastIndexOf('_') + 1)..];
        return kind.Blueprints.FirstOrDefault(blueprint => blueprint.Game.Equals(gameCode, StringComparison.OrdinalIgnoreCase))
            ?? kind.Blueprints.FirstOrDefault(blueprint => blueprint.Game.EndsWith(ending, StringComparison.OrdinalIgnoreCase));
    }

    static (IReadOnlyList<ModuleKind>, Dictionary<string, string>) Load()
    {
        // The data names materials and engineers in words; the rest of Studio knows them by the game's codes and numbers.
        var materials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (GameData.Material material in GameData.Materials.Values) materials[material.Name] = material.Symbol;
        Dictionary<string, long> engineers = GameData.Engineers.ToDictionary(engineer => engineer.Name, engineer => engineer.Id);
        string Spelled(string name) => Respelled.GetValueOrDefault(name, name);
        Dictionary<string, int> Cost(JsonElement recipe) =>
            recipe.GetProperty("components").EnumerateObject().ToDictionary(part => materials[Spelled(part.Name)], part => part.Value.GetInt32());

        JsonElement recipes = GameData.Coriolis.GetProperty("blueprints"), effects = GameData.Coriolis.GetProperty("specials");
        JsonElement numbers = GameData.Coriolis.GetProperty("modifications");
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty recipe in recipes.EnumerateObject())
            names.TryAdd(recipe.Value.Text("fdname"), CultureInfo.InvariantCulture.TextInfo.ToTitleCase(recipe.Value.Text("name")));

        var loaded = new List<ModuleKind>();
        foreach (JsonProperty kind in GameData.Coriolis.GetProperty("modules").EnumerateObject())
        {
            // (The data lists one kind twice, the advanced missile rack; its first entry is the fuller one.)
            if (kind.Value.Field("blueprints") is not { } taken || loaded.Any(known => known.Code == kind.Name)) continue;
            var blueprints = new List<Blueprint>();
            foreach (JsonProperty blueprint in taken.EnumerateObject())
            {
                JsonElement recipe = recipes.GetProperty(blueprint.Name);
                // A grade nobody offers cannot be planned (the data lists one such blueprint, a cargo rack sold ready-made).
                Grade[] grades =
                [
                    .. blueprint.Value.GetProperty("grades").EnumerateObject()
                        .Select(grade => (Offered: grade, Recipe: recipe.GetProperty("grades").GetProperty(grade.Name)))
                        .Select(grade => new Grade(
                            int.Parse(grade.Offered.Name, CultureInfo.InvariantCulture),
                            Cost(grade.Recipe),
                            [.. grade.Offered.Value.List("engineers").Select(engineer => engineers[Spelled(engineer.GetString()!)]).Distinct()],
                            BestRoll(grade.Recipe.Field("features"), numbers)))
                        .Where(grade => grade.Engineers.Count > 0)
                        .OrderBy(grade => grade.Number),
                ];
                if (grades.Length > 0) blueprints.Add(new Blueprint(blueprint.Name, recipe.Text("fdname"), grades));
            }
            if (blueprints.Count == 0) continue;

            // Left out: effects from before the engineering of today, which the data keeps without a recipe,
            // and one it lists twice for torpedo pylons under two codes.
            Experimental[] experimentals =
            [
                .. kind.Value.List("specials").Select(code => code.GetString()!)
                    .Where(code => effects.Field(code)?.Field("components") is not null)
                    .Select(code => new Experimental(code, effects.GetProperty(code).Text("name"), Cost(effects.GetProperty(code))))
                    .DistinctBy(effect => effect.Name, StringComparer.OrdinalIgnoreCase),
            ];
            loaded.Add(new ModuleKind(kind.Name, KindNames.GetValueOrDefault(kind.Name, kind.Name), blueprints, experimentals));
        }
        return ([.. loaded.OrderBy(kind => kind.Name)], names);
    }

    /// <summary>
    /// What a grade changes when rolled to the end. The data gives each number as a range; which
    /// end the roll finishes at depends on whether more of that number is good, as in Coriolis
    /// (src/app/utils/BlueprintFunctions.js, setQualityCB at quality 1).
    /// </summary>
    static Dictionary<string, double> BestRoll(JsonElement? features, JsonElement numbers)
    {
        var best = new Dictionary<string, double>();
        if (features is not { ValueKind: JsonValueKind.Object } ranges) return best;
        foreach (JsonProperty feature in ranges.EnumerateObject())
        {
            if (feature.Value.ValueKind != JsonValueKind.Array || feature.Value.GetArrayLength() != 2) continue;   // a few are words, not ranges
            double from = feature.Value[0].GetDouble(), to = feature.Value[1].GetDouble();
            bool downwards = from < 0 || (from == 0 && to < 0);
            best[feature.Name] = numbers.Field(feature.Name)?.Flag("higherbetter") == downwards ? from : to;
        }
        return best;
    }
}
