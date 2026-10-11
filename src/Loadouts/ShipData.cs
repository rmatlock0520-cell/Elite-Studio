using System.Text.Json;
using EliteStudio.Core;

namespace EliteStudio.Loadouts;

/// <summary>
/// Every hull and module with the numbers the ship designer sums up, taken once from the
/// community's coriolis-data (<see cref="GameData.Coriolis"/>), and what engineering does to
/// them, taken from <see cref="EngineeringData"/>. All of it is found by the codes the game's
/// journal uses: the ship ("mandalay"), the slot ("MediumHardpoint1", "Slot03_Size5") and the
/// module ("int_hyperdrive_size5_class5"; capitals do not matter). Only what the designer reads is
/// kept. Units as in the game: tons, megawatts, credits, light years.
/// </summary>
public static class ShipData
{
    /// <summary>One place a module can go.</summary>
    /// <param name="Id">The journal's name for it: "PowerPlant", "TinyHardpoint2", "Slot03_Size5", "Military01".</param>
    /// <param name="Size">The biggest module it takes; 0 for a utility mount.</param>
    /// <param name="Only">The kinds of module it takes (the table's group codes) when it does not take every kind, else null.</param>
    public sealed record Slot(string Id, int Size, IReadOnlySet<string>? Only);

    /// <param name="Ship">The journal's ship code in lower case, e.g. "krait_mkii".</param>
    /// <param name="Table">The table's own name for the ship; a module made for one ship names it this way.</param>
    /// <param name="Cost">The bare hull, credits.</param>
    /// <param name="Speed">Metres per second with four pips to engines, at the weight the thrusters are built for.</param>
    /// <param name="BoostEnergy">What one boost takes from the distributor's engine capacitor.</param>
    public sealed record Hull(
        string Ship, string Table, long Cost, double Mass, double Speed, double Boost, double BoostEnergy,
        double Shield, double Armour, double ReserveFuel, bool Fighters, bool LuxuryCabins, IReadOnlyList<Slot> Slots);

    /// <summary>One module as it leaves the shop.</summary>
    /// <param name="Shelf">Where it is fitted: "standard" (core internals and hulls' armour), "hardpoints" (utility mounts too) or "internal".</param>
    /// <param name="Group">The table's code for the kind of module: "fsd", "sg", "pl".</param>
    /// <param name="Class">Its size number; 0 for a utility.</param>
    /// <param name="Ship">Set when it fits one ship only: that ship's <see cref="Hull.Table"/>.</param>
    /// <param name="NamedSlot">Set when it only goes in slots of that name, e.g. "Cargo".</param>
    /// <param name="Passive">A utility that draws its power with the hardpoints retracted as well.</param>
    /// <param name="Numbers">Its numbers under the table's names: "mass", "power", "optmass" and so on.</param>
    public sealed record Part(
        string Item, string Shelf, string Group, int Class, string Rating, long Cost, string Ship, string NamedSlot, bool Passive,
        IReadOnlyDictionary<string, double> Numbers)
    {
        /// <summary>One of its numbers; 0 when it has none of that name.</summary>
        public double Number(string name) => Numbers.GetValueOrDefault(name);
    }

    /// <summary>An engineer's recipe.</summary>
    /// <param name="Code">The game's code, as the journal writes it: "FSD_LongRange".</param>
    /// <param name="Grades">For each grade on offer, what its best roll changes: 0.55 under "optmass" is 55% more.</param>
    public sealed record Blueprint(string Code, IReadOnlyDictionary<int, IReadOnlyDictionary<string, double>> Grades);

    /// <param name="Code">The game's code: "special_fsd_heavy".</param>
    public sealed record Experimental(string Code, string Name);

    /// <summary>What engineers offer for one kind of module.</summary>
    public sealed record Workshop(IReadOnlyList<Blueprint> Blueprints, IReadOnlyList<Experimental> Experimentals)
    {
        public static readonly Workshop None = new([], []);
    }

    sealed record Tables(Dictionary<string, Hull> Hulls, Part[] Parts, Dictionary<string, Part> ByItem, Dictionary<string, Workshop> Workshops);

    // The numbers the sums read; everything else in the table (damage, heat, scan ranges...) is left behind.
    static readonly string[] Kept =
    [
        "mass", "power", "pgen", "fuel", "cargo", "maxfuel", "fuelmul", "fuelpower", "jumpboost", "engcap",
        "optmass", "minmass", "maxmass", "optmul", "minmul", "maxmul", "optmulspeed", "minmulspeed", "maxmulspeed",
        "shieldboost", "shieldaddition", "hullboost", "hullreinforcement",
    ];

    // The core internals in the table's order, each with the kind of module that goes in it.
    static readonly (string Id, string Group)[] Core =
    [
        ("PowerPlant", "pp"), ("MainEngines", "t"), ("FrameShiftDrive", "fsd"), ("LifeSupport", "ls"),
        ("PowerDistributor", "pd"), ("Radar", "s"), ("FuelTank", "ft"),
    ];

    static readonly string[] HardpointSizes = ["Tiny", "Small", "Medium", "Large", "Huge"];
    static readonly string[] ArmourGrades = ["grade1", "grade2", "grade3", "mirrored", "reactive"];

    // Optional slots with a name of their own: the table's word and the journal's.
    static readonly Dictionary<string, string> NamedSlots = new()
    {
        ["Military"] = "Military", ["Cargo"] = "Cargo", ["Limpets"] = "LimpetController", ["Fighter"] = "FighterBay", ["MkIIPassenger"] = "Passenger",
    };

    // Three ships were given bigger slots after their launch, and the journal still names those slots by the size
    // they had (the Type-7 even has two numbered 09). The names are those of EDSY's ship table
    // (github.com/taleden/EDSY, eddb.js, "slotnames"), in the table's order of slots.
    static readonly Dictionary<string, string[]> SlotNamesKept = new()
    {
        ["type_7_transport"] = ["Slot01_Size6", "Slot02_Size6", "Slot03_Size5", "Slot04_Size5", "Slot05_Size4", "Slot06_Size4", "Slot07_Size2", "Slot08_Size2", "Slot09_Size2", "Slot09_Size1"],
        ["keelback"] = ["Slot01_Size5", "Slot02_Size5", "Slot03_Size3", "Slot04_Size3", "Slot05_Size2", "Slot06_Size2", "Slot07_Size1"],
        ["asp_scout"] = ["Slot01_Size4", "Slot02_Size4", "Slot03_Size3", "Slot04_Size3", "Slot05_Size2", "Slot06_Size2", "Slot07_Size1"],
    };

    static readonly Lazy<Tables> tables = new(Load);

    /// <summary>Every ship the table has, by its journal code.</summary>
    public static IReadOnlyDictionary<string, Hull> Hulls => tables.Value.Hulls;

    /// <summary>
    /// The module with that journal code, or null when the table has no numbers for it. The free
    /// modules of a starter ship ("int_engine_size2_class1_free") are the plain ones under another name.
    /// </summary>
    public static Part? Find(string item) =>
        tables.Value.ByItem.GetValueOrDefault(item)
        ?? (item.EndsWith("_free", StringComparison.OrdinalIgnoreCase) ? tables.Value.ByItem.GetValueOrDefault(item[..^5]) : null);

    /// <summary>The modules that may be fitted in a slot of a ship: by size, by kind, and by the few rules for single ships and named slots.</summary>
    public static IEnumerable<Part> Fits(Hull hull, Slot slot)
    {
        string shelf = slot.Id == "Armour" || Core.Any(core => core.Id == slot.Id) ? "standard"
            : slot.Id.Contains("Hardpoint", StringComparison.Ordinal) ? "hardpoints" : "internal";
        return tables.Value.Parts.Where(part =>
            part.Shelf == shelf && part.Class <= slot.Size
            && (part.Class > 0 || slot.Size == 0 || slot.Only is not null)   // a utility goes in a utility mount, or where a slot asks for it by kind
            && (slot.Only is null || slot.Only.Contains(part.Group))
            && (part.Ship.Length == 0 || part.Ship == hull.Table)
            && (part.NamedSlot.Length == 0 || slot.Id.StartsWith(part.NamedSlot, StringComparison.Ordinal))
            && (part.Group != "fh" || hull.Fighters) && (part.Group != "pcq" || hull.LuxuryCabins)
            && part.Group != "pas");   // the planetary approach suite has a slot of its own, outside the four lists
    }

    /// <summary>What engineers offer for a kind of module (<see cref="Part.Group"/>); nothing for a kind they do not touch.</summary>
    public static Workshop Engineering(string group) => tables.Value.Workshops.GetValueOrDefault(group, Workshop.None);

    /// <summary>
    /// The table's name for a blueprint code from the journal, or null. The game writes one recipe
    /// under several codes ("Sensor_Sensor_LightWeight" beside "Sensor_LightWeight"), so a code the
    /// table lacks takes the name of the recipes that end in the same word.
    /// </summary>
    public static string? BlueprintName(string code)
    {
        IReadOnlyDictionary<string, string> names = EngineeringData.RecipeNames;
        if (names.TryGetValue(code, out string? name)) return name;
        string ending = "_" + code[(code.LastIndexOf('_') + 1)..];
        return names.FirstOrDefault(known => known.Key.EndsWith(ending, StringComparison.OrdinalIgnoreCase)).Value;
    }

    // ------------------------------------------------------------ reading the table

    static Tables Load()
    {
        JsonElement all = GameData.Coriolis;
        var hulls = new Dictionary<string, Hull>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<Part>();
        foreach (JsonProperty ship in all.GetProperty("ships").EnumerateArray().SelectMany(file => file.EnumerateObject()))
        {
            if (ToHull(ship.Name, ship.Value) is not { } hull) continue;   // a ship the names table does not have cannot be found by its journal code
            hulls[hull.Ship] = hull;
            int grade = 0;
            foreach (JsonElement armour in ship.Value.List("bulkheads"))
            {
                // Only the newest hulls name their armour; the others have the five kinds in the game's order.
                string item = armour.Text("symbol") is { Length: > 0 } own ? own : $"{hull.Ship}_armour_{ArmourGrades[Math.Min(grade, 4)]}";
                parts.Add(new(item, "standard", "bh", 1, "I", armour.Whole("cost"), hull.Table, "", false, Numbers(armour)));
                grade++;
            }
        }

        foreach (string shelf in (string[])["standard", "hardpoints", "internal"])
            foreach (JsonProperty kind in all.GetProperty(shelf).EnumerateArray().SelectMany(file => file.EnumerateObject()))
                foreach (JsonElement module in kind.Value.EnumerateArray())
                {
                    string item = module.Text("symbol");
                    // A pre-engineered module carries the plain module's code, so the plain one stands for both.
                    // What the names table lacks is nothing a station sells: Coriolis' own placeholders, and two cargo racks.
                    if (module.Field("preEngineered") is not null || !GameData.Modules.ContainsKey(item)) continue;
                    parts.Add(new(item, shelf, kind.Name, (int)module.Whole("class"), module.Text("rating"), module.Whole("cost"),
                        module.Text("ship"), module.Text("restriction"), module.Whole("passive") != 0, Numbers(module)));
                }

        var byItem = new Dictionary<string, Part>(StringComparer.OrdinalIgnoreCase);
        foreach (Part part in parts) byItem.TryAdd(part.Item, part);   // a fuel tank is on two shelves: the same module

        // Recipes: the engineering tables are read in one place, EngineeringData; the sums take from it what each
        // grade changes at its best, under the game's own code for the blueprint.
        Dictionary<string, Workshop> workshops = EngineeringData.Kinds.ToDictionary(kind => kind.Code, kind => new Workshop(
            [.. kind.Blueprints.Select(blueprint => new Blueprint(blueprint.Game, blueprint.Grades.ToDictionary(grade => grade.Number, grade => grade.Changes)))],
            [.. kind.Experimentals.Select(effect => new Experimental(effect.Code, effect.Name))]));
        return new(hulls, [.. parts], byItem, workshops);
    }

    /// <summary>
    /// A ship of the table under its journal code, which only the names table knows: matched by the
    /// game's id, and by name for the three newest ships where the two tables disagree about the id.
    /// </summary>
    static Hull? ToHull(string table, JsonElement ship)
    {
        JsonElement about = ship.GetProperty("properties");
        long id = ship.Whole("edID");
        GameData.Ship? known = GameData.Ships.Values.FirstOrDefault(s => s.Id == id)
            ?? GameData.Ships.Values.FirstOrDefault(s => s.Name == about.Text("name"));
        if (known is null) return null;
        return new(
            known.Symbol.ToLowerInvariant(), table, about.Whole("hullCost"), about.Number("hullMass"),
            about.Number("speed"), about.Number("boost"), about.Number("boostEnergy"),
            about.Number("baseShieldStrength"), about.Number("baseArmour"), about.Number("reserveFuelCapacity"),
            about.Flag("fighterHangars"), about.Flag("luxuryCabins"), Slots(table, ship.GetProperty("slots")));
    }

    /// <summary>
    /// A ship's slots under the names the journal gives them. The table only lists sizes, in the
    /// game's order; the numbering, with its gaps on a few ships, follows Coriolis' own journal
    /// import (github.com/EDCD/coriolis, src/app/utils/JournalUtils.js, MIT licence) and agrees
    /// with the names EDSY lists for those ships.
    /// </summary>
    static List<Slot> Slots(string table, JsonElement slots)
    {
        var all = new List<Slot> { new("Armour", 1, new HashSet<string> { "bh" }) };
        int core = 0;
        foreach (JsonElement size in slots.GetProperty("standard").EnumerateArray())
            all.Add(new(Core[core].Id, size.GetInt32(), new HashSet<string> { Core[core++].Group }));

        int[] mounts = new int[HardpointSizes.Length];
        foreach (JsonElement mount in slots.GetProperty("hardpoints").EnumerateArray())
        {
            (int size, string name, IReadOnlySet<string>? only) = Read(mount);
            int number = ++mounts[size];
            if (table == "type_8_transport" && size == 1 && number == 3) number = ++mounts[size];   // the Type-8 has no SmallHardpoint3
            all.Add(new($"{HardpointSizes[size]}{name}Hardpoint{number}", size, only));   // "Mining" in the name on the Type-11
        }

        int next = table == "type_9_heavy" ? 0 : 1;   // the Type-9 alone starts at Slot00
        string[]? kept = SlotNamesKept.GetValueOrDefault(table);
        int plain = 0;
        var named = new Dictionary<string, int>();
        foreach (JsonElement bay in slots.GetProperty("internal").EnumerateArray())
        {
            (int size, string name, IReadOnlySet<string>? only) = Read(bay);
            if (name == "PlanetaryApproachSuite") continue;   // not one of the outfitting screen's four lists
            if (name.Length > 0)
            {
                int number = named[name] = named.GetValueOrDefault(name) + 1;
                all.Add(new($"{NamedSlots.GetValueOrDefault(name, name)}{number:00}", size, only));
                continue;
            }
            // Where the game's numbering has a gap.
            if ((next == 11 && table == "anaconda") || (next == 7 && table == "federal_dropship")
                || (next == 9 && table is "type_9_heavy" or "type_10_defender")) next += 2;
            else if (next == 4 && table == "vulture") next++;
            all.Add(new(kept is null ? $"Slot{next++:00}_Size{size}" : kept[plain++], size, null));
        }
        return all;
    }

    /// <summary>A slot of the table: a bare size, or a size with a name and the kinds of module allowed in it.</summary>
    static (int Size, string Name, IReadOnlySet<string>? Only) Read(JsonElement slot) =>
        slot.ValueKind == JsonValueKind.Number
            ? (slot.GetInt32(), "", null)
            : ((int)slot.Whole("class"), slot.Text("name"), slot.Field("eligible")?.EnumerateObject().Select(kind => kind.Name).ToHashSet());

    static Dictionary<string, double> Numbers(JsonElement module)
    {
        var numbers = new Dictionary<string, double>();
        foreach (string name in Kept)
            if (module.Field(name) is { ValueKind: JsonValueKind.Number } value) numbers[name] = value.GetDouble();
        return numbers;
    }
}
