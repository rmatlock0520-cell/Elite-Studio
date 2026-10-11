using System.Globalization;
using System.Text.RegularExpressions;
using EliteStudio.Core;

namespace EliteStudio.Loadouts;

/// <summary>
/// One ship as the Loadouts page shows it: the key numbers and the modules in the four lists of the
/// game's outfitting screen. Built the same way for the ship being flown and for a saved build.
/// Units: credits, tons, light years; health is 0 to 1.
/// </summary>
/// <param name="ShipType">The name players know the ship by, e.g. "Mandalay".</param>
/// <param name="ShipName">The player's own name for it; "" when it has none.</param>
/// <param name="JumpEmpty">Light years per jump with a full tank and an empty hold.</param>
/// <param name="JumpLaden">Light years per jump with a full tank and a full hold.</param>
/// <param name="JumpMax">Best possible jump: no cargo and only the fuel for one jump on board.</param>
public sealed partial record ShipView(
    string ShipType, string ShipName, string ShipIdent,
    long HullValue, long ModulesValue, long Rebuy, double HullHealth,
    int CargoCapacity, double FuelMain, double UnladenMass,
    double JumpEmpty, double JumpLaden, double JumpMax,
    IReadOnlyList<ModuleGroup> Groups)
{
    internal static readonly string[] GroupNames = ["Hardpoints", "Utility mounts", "Core internals", "Optional internals"];
    static readonly string[] HardpointSizes = ["Tiny", "Small", "Medium", "Large", "Huge"];

    // The core internals in the order the outfitting screen lists them, with the game's name for each slot.
    static readonly (string Slot, string Label)[] Core =
    [
        ("Armour", "Bulkheads"), ("PowerPlant", "Power Plant"), ("MainEngines", "Thrusters"),
        ("FrameShiftDrive", "Frame Shift Drive"), ("LifeSupport", "Life Support"),
        ("PowerDistributor", "Power Distributor"), ("Radar", "Sensors"), ("FuelTank", "Fuel Tank"),
    ];

    // Optional slots that take one kind of module only, by the journal's name for them, in the order they are listed.
    static readonly (string Slot, string Label)[] Named =
    [
        ("Military", "Military"), ("Cargo", "Cargo"), ("LimpetController", "Limpets"), ("FighterBay", "Fighter bay"), ("Passenger", "Cabin"),
    ];

    public static ShipView From(ShipLoadout ship)
    {
        // The same sum the status strip uses, so the two can never disagree about a ship. One number in it
        // is the designer's when its tables know the ship: the fuel a jump burns. The strip's sum reads that
        // from the game's list of what an engineer changed, and a drive planned in the designer has no such list.
        var (fsdFuel, boost) = ShipMath.DriveNumbers(ship.Modules);
        if (BuildStats.Of(ship) is { JumpFuel: > 0 } stats) fsdFuel = stats.JumpFuel;
        var (empty, laden) = ShipMath.JumpRanges(ship.MaxJumpRange, ship.UnladenMass, ship.FuelMain, fsdFuel, boost, ship.CargoCapacity);

        ShipData.Hull? hull = ShipData.Hulls.GetValueOrDefault(ship.Ship);
        var placed = new List<(int Group, int Order, ModuleView Module)>();
        foreach (ShipModule module in ship.Modules)
            if (Place(module.Slot, hull) is { } place)
                placed.Add((place.Group, place.Order, Show(module, place.Label)));

        return new(
            ShipMath.ShipName(ship.Ship), ship.ShipName, ship.ShipIdent,
            ship.HullValue, ship.ModulesValue, ship.Rebuy, ship.HullHealth,
            ship.CargoCapacity, ship.FuelMain, ship.UnladenMass,
            empty, laden, ship.MaxJumpRange,
            [.. placed.GroupBy(p => p.Group).OrderBy(g => g.Key)
                .Select(g => new ModuleGroup(GroupNames[g.Key], [.. g.OrderBy(p => p.Order).Select(p => p.Module)]))]);
    }

    /// <summary>
    /// Which list a slot belongs to, its place in that list and what to call it. Null for everything
    /// the outfitting screen's four lists leave out: paint, voice, cockpit, cargo hatch and the like.
    /// </summary>
    /// <param name="hull">The ship, when the tables have it: they know an optional slot's true size.</param>
    internal static (int Group, int Order, string Label)? Place(string slot, ShipData.Hull? hull = null)
    {
        if (Hardpoint().Match(slot) is { Success: true } mount)
        {
            int size = Array.FindIndex(HardpointSizes, word => word.Equals(mount.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            int number = Number(mount.Groups[2]);
            // biggest weapons first, as the game lists them
            return size == 0 ? (1, number, $"Utility {number}") : (0, (4 - size) * 100 + number, $"{HardpointSizes[size]} {number}");
        }
        int core = Array.FindIndex(Core, known => known.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase));
        if (core >= 0) return (2, core, Core[core].Label);
        // An optional slot is known by its size: a size 6 slot may well hold a size 5 module. Its name says the
        // size, except on three ships whose slots grew after the game had named them.
        if (Optional().Match(slot) is { Success: true } bay)
        {
            int size = hull?.Slots.FirstOrDefault(known => known.Id.Equals(slot, StringComparison.OrdinalIgnoreCase))?.Size ?? Number(bay.Groups[2]);
            return (3, Number(bay.Groups[1]), $"Size {size}");
        }
        if (NamedSlot().Match(slot) is { Success: true } own)
        {
            int kind = Array.FindIndex(Named, known => known.Slot.Equals(own.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            return (3, 100 * (kind + 1) + Number(own.Groups[2]), Named[kind].Label);
        }
        return null;
    }

    internal static ModuleView Show(ShipModule module, string label)
    {
        var (name, mount, size, rating) = Names.Module(module.Item);
        ModuleEngineering? work = module.Engineering;
        return new(
            label, module.Slot, module.Item, name, mount,
            // A module the names table lacks still says its size and rating in its code: the digit after "class" is 1 = E up to 5 = A.
            size > 0 ? size : module.Class, rating.Length > 0 ? rating : module.Rating,
            module.On, module.Priority + 1,   // the journal counts priorities from 0, the game's screens from 1
            module.Health,
            work is null ? null : new(
                Names.Blueprint(work.Blueprint), work.Level, work.Quality, work.Engineer, work.ExperimentalName,
                [.. work.Modifiers.Select(m => new ChangeView(Names.Words(m.Label), m.Value, m.OriginalValue, m.LessIsGood, m.Text))]));
    }

    static int Number(Group digits) => int.Parse(digits.Value, CultureInfo.InvariantCulture);

    // "Mining" in the name marks the Type-11's hardpoints for mining tools; they are numbered with the others of their size.
    [GeneratedRegex(@"^(Tiny|Small|Medium|Large|Huge)(?:Mining)?Hardpoint(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Hardpoint();

    [GeneratedRegex(@"^Slot(\d+)_Size(\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex Optional();

    [GeneratedRegex(@"^(Military|Cargo|LimpetController|FighterBay|Passenger)(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NamedSlot();
}

/// <summary>One of the outfitting screen's lists, e.g. "Core internals", with what is fitted in it.</summary>
public sealed record ModuleGroup(string Name, IReadOnlyList<ModuleView> Modules);

/// <summary>One fitted module, ready to show.</summary>
/// <param name="Slot">What to call the slot, e.g. "Medium 1", "Thrusters", "Size 6".</param>
/// <param name="SlotId">The journal's slot code, e.g. "MediumHardpoint1"; unique within a ship.</param>
/// <param name="Item">The game's module code, kept so a name that reads badly can be reported.</param>
/// <param name="Mount">"Fixed", "Gimballed" or "Turreted" for a weapon, otherwise "".</param>
/// <param name="Size">The module's size number; 0 for a utility.</param>
/// <param name="Rating">"A" to "I", as the outfitting screen shows it.</param>
/// <param name="Priority">Power priority as the game shows it, 1 to 5.</param>
public sealed record ModuleView(
    string Slot, string SlotId, string Item, string Name, string Mount, int Size, string Rating,
    bool On, int Priority, double Health, EngineeringView? Engineering);

/// <summary>What an engineer did to a module, in words.</summary>
/// <param name="Grade">1 to 5.</param>
/// <param name="Quality">How far the grade is rolled, 0 to 1.</param>
/// <param name="Engineer">"" for engineering planned in the designer.</param>
/// <param name="Experimental">The experimental effect in the game's own words; "" when none.</param>
/// <param name="Changes">The numbers the game reports as changed; none for engineering planned in the designer.</param>
public sealed record EngineeringView(
    string Blueprint, int Grade, double Quality, string Engineer, string Experimental, IReadOnlyList<ChangeView> Changes);

/// <summary>One number the engineering changed, before and after. The journal gives no units, so none are shown.</summary>
/// <param name="Text">Set instead of the numbers for the few changes that are words.</param>
public sealed record ChangeView(string Label, double Value, double Original, bool LessIsGood, string Text);

/// <summary>
/// A build on the designer's bench: its numbers, and every slot of the hull in the outfitting
/// screen's four lists, fitted or empty.
/// </summary>
public sealed record DesignView(string Ship, string ShipType, BuildStats Stats, IReadOnlyList<SlotGroup> Groups)
{
    /// <summary>Null for a ship the tables have no numbers for.</summary>
    public static DesignView? From(ShipLoadout build)
    {
        if (BuildStats.Of(build) is not { } stats) return null;
        var fitted = new Dictionary<string, ShipModule>(StringComparer.OrdinalIgnoreCase);
        foreach (ShipModule module in build.Modules) fitted[module.Slot] = module;

        // The hull's own slots, and after them any slot the build uses that the table does not list
        // for this ship: such a module is in the sums, so it must be in sight too.
        ShipData.Hull hull = ShipData.Hulls[build.Ship];
        IEnumerable<ShipData.Slot> slots = hull.Slots.Concat(build.Modules
            .Where(module => !hull.Slots.Any(slot => slot.Id.Equals(module.Slot, StringComparison.OrdinalIgnoreCase)))
            .Select(module => new ShipData.Slot(module.Slot, module.Class, null)));
        var placed = new List<(ShipData.Slot Slot, int Group, int Order, string Label)>();
        foreach (ShipData.Slot slot in slots)
            if (ShipView.Place(slot.Id, hull) is { } place) placed.Add((slot, place.Group, place.Order, place.Label));
        return new(build.Ship, ShipMath.ShipName(build.Ship), stats,
        [
            .. placed.GroupBy(p => p.Group).OrderBy(list => list.Key).Select(list => new SlotGroup(ShipView.GroupNames[list.Key],
            [
                .. list.OrderBy(p => p.Order).Select(p => new SlotView(p.Label, p.Slot.Id, p.Slot.Size,
                    fitted.TryGetValue(p.Slot.Id, out ShipModule? module) ? ShipView.Show(module, p.Label) : null)),
            ])),
        ]);
    }
}

/// <summary>One of the four lists with every slot the hull has in it.</summary>
public sealed record SlotGroup(string Name, IReadOnlyList<SlotView> Slots);

/// <summary>One slot of a hull.</summary>
/// <param name="Slot">What to call it, e.g. "Medium 1", "Thrusters", "Size 6".</param>
/// <param name="SlotId">The journal's slot code.</param>
/// <param name="Size">The biggest module it takes; 0 for a utility mount.</param>
/// <param name="Module">What is fitted; null for an empty slot.</param>
public sealed record SlotView(string Slot, string SlotId, int Size, ModuleView? Module);

/// <summary>
/// Readable names for the game's codes. Modules are named by the community's module table
/// (<see cref="GameData.Modules"/>) and blueprints by its engineering table; the little that is
/// still made by hand covers what those lack. Pure functions.
/// </summary>
public static partial class Names
{
    static readonly Dictionary<string, string> Mounts = new() { ["fixed"] = "Fixed", ["gimbal"] = "Gimballed", ["turret"] = "Turreted" };
    static readonly string[] SizeWords = ["tiny", "small", "medium", "large", "huge"];   // a weapon's size number is the word's place here

    // Where the game says more than the engineering table's one word ("Dirty").
    static readonly Dictionary<string, string> Blueprints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Engine_Dirty"] = "Dirty Drive Tuning", ["Engine_Tuned"] = "Clean Drive Tuning", ["Engine_Reinforced"] = "Drive Strengthening",
    };

    /// <summary>
    /// A module code in English with what the outfitting screen shows beside the name:
    /// "hpt_pulselaser_gimbal_medium" gives "Pulse Laser", "Gimballed", 2, "F".
    /// </summary>
    /// <returns>Mount is "" for anything that is not a weapon. For a code the table lacks, Rating is "" and Size is only known for a weapon.</returns>
    public static (string Name, string Mount, int Size, string Rating) Module(string item)
    {
        // (The table's mount column holds a stray word for one module, so only the three real mounts count.)
        if (GameData.Modules.GetValueOrDefault(item) is { } listed)
            return (listed.Name, Mounts.ContainsValue(listed.Mount) ? listed.Mount : "", listed.Class, listed.Rating);

        // A module newer than the table: the words of its code, without the prefix, the size and the rating.
        string[] all = item.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>();
        string mount = "";
        int size = 0;
        foreach (string part in all.Skip(all is ["hpt" or "int", ..] ? 1 : 0))
        {
            if (SizeOrRating().IsMatch(part)) continue;
            if (Mounts.TryGetValue(part, out string? how)) mount = how;
            else if (Array.IndexOf(SizeWords, part) is >= 0 and int word) size = word;
            else parts.Add(part);
        }
        if (size == 0) mount = "";   // "turret" in a utility's code tells a player nothing
        return (string.Join(' ', parts.Select(Capital)), mount, size, "");
    }

    /// <summary>An engineering blueprint in the game's words, e.g. "FSD_LongRange" gives "Increased Range".</summary>
    public static string Blueprint(string code) =>
        Blueprints.GetValueOrDefault(code) ?? ShipData.BlueprintName(code) ?? Words(code[(code.LastIndexOf('_') + 1)..]);

    /// <summary>Words run together with capitals, set apart: "FSDOptimalMass" gives "FSD Optimal Mass".</summary>
    public static string Words(string runTogether) => WordStart().Replace(runTogether, " ");

    static string Capital(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    [GeneratedRegex("^(size|class)[0-9]$")]
    private static partial Regex SizeOrRating();

    // before a capital that follows a small letter or digit, and before the last capital of a run of capitals ("FSD|Optimal")
    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex WordStart();
}
