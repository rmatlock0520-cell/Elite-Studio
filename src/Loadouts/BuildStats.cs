using System.Text.Json;
using EliteStudio.Core;

namespace EliteStudio.Loadouts;

/// <summary>
/// The key numbers of a build, worked out from its hull and modules alone: what the ship designer
/// shows, and what it writes into a build it saves. Pure sums over the journal's Loadout shape, the
/// tables of <see cref="ShipData"/> and the bundled table of what the experimental effects change;
/// nothing here reads a file or keeps state.
///
/// The sums are those of Coriolis, the community's ship designer (github.com/EDCD/coriolis, MIT
/// licence: src/app/shipyard/Calculations.js, Module.js and Ship.js as they stood in October
/// 2026), with one difference the game's own journal settles: the best possible jump is worked
/// out without the reserve tank's weight, which is how the game arrives at its MaxJumpRange.
/// Units: tons, light years, megawatts, metres per second, megajoules, credits.
/// </summary>
/// <param name="UnladenMass">Hull and modules, without fuel and cargo.</param>
/// <param name="JumpMax">Best possible jump: no cargo and only the fuel for one jump on board.</param>
/// <param name="JumpEmpty">One jump with a full tank and an empty hold.</param>
/// <param name="JumpLaden">One jump with a full tank and a full hold.</param>
/// <param name="JumpFuel">The most fuel one jump burns, after engineering; 0 without a working drive.</param>
/// <param name="PowerCapacity">What the power plant gives.</param>
/// <param name="PowerRetracted">What the switched-on modules draw with the hardpoints retracted.</param>
/// <param name="PowerDeployed">The same with the hardpoints deployed.</param>
/// <param name="Speed">Top speed with four pips to engines, a full tank and an empty hold; 0 when the ship cannot fly.</param>
/// <param name="Boost">The same while boosting; 0 when the distributor cannot feed a boost.</param>
/// <param name="Shield">Raw shield strength, boosters included; 0 without a working generator.</param>
/// <param name="Armour">Raw hull strength, reinforcement packages included.</param>
/// <param name="MaxMass">The most the thrusters can move; 0 without thrusters.</param>
/// <param name="Unknown">Fitted modules the tables have no numbers for (their codes): the sums leave them out.</param>
/// <param name="Modules">Weight and power draw of each fitted module after engineering, by slot code.</param>
public sealed record BuildStats(
    double UnladenMass, double FuelCapacity, double ReserveFuel, int CargoCapacity,
    double JumpMax, double JumpEmpty, double JumpLaden, double JumpFuel,
    double PowerCapacity, double PowerRetracted, double PowerDeployed,
    double Speed, double Boost, double Shield, double Armour, long HullCost, long ModulesCost, double MaxMass,
    IReadOnlyList<string> Unknown, IReadOnlyDictionary<string, ModuleNumbers> Modules)
{
    /// <summary>The cargo hatch is a module of every ship, with no entry in the tables: megawatts while switched on.</summary>
    const double CargoHatchPower = 0.6;

    static readonly string[] ShieldGenerators = ["sg", "bsg", "psg"];
    static readonly string[] HullPackages = ["hr", "ghrp", "mahr"];

    /// <summary>The numbers of a real ship's engineering, by the journal's label: which number of the tables it is, and in what unit.</summary>
    static readonly Dictionary<string, (string Number, double Unit)> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mass"] = ("mass", 1), ["PowerDraw"] = ("power", 1), ["PowerCapacity"] = ("pgen", 1), ["MaxFuelPerJump"] = ("maxfuel", 1),
        ["FSDOptimalMass"] = ("optmass", 1), ["EngineOptimalMass"] = ("optmass", 1), ["ShieldGenOptimalMass"] = ("optmass", 1),
        ["EngineOptPerformance"] = ("optmul", 0.01), ["ShieldGenStrength"] = ("optmul", 0.01),
        ["DefenceModifierShieldMultiplier"] = ("shieldboost", 0.01), ["DefenceModifierHealthMultiplier"] = ("hullboost", 0.01),
        ["DefenceModifierHealthAddition"] = ("hullreinforcement", 1), ["EnginesCapacity"] = ("engcap", 1), ["CargoCapacity"] = ("cargo", 1),
    };

    /// <summary>One fitted module with its numbers after engineering.</summary>
    sealed record Fitted(ShipModule Module, ShipData.Part Part, Dictionary<string, double> Numbers)
    {
        public double this[string number] => Numbers.GetValueOrDefault(number);

        public bool Is(params string[] kinds) => kinds.Contains(Part.Group);
    }

    public long Cost => HullCost + ModulesCost;

    /// <summary>With a full tank and a full hold the ship weighs more than its thrusters can move: it cannot fly, so it cannot jump either.</summary>
    public bool TooHeavy => MaxMass > 0 && UnladenMass + FuelCapacity + CargoCapacity >= MaxMass;

    /// <summary>The numbers of a build, or null for a ship the tables do not have.</summary>
    public static BuildStats? Of(ShipLoadout build)
    {
        if (!ShipData.Hulls.TryGetValue(build.Ship, out ShipData.Hull? hull)) return null;

        var fitted = new List<Fitted>();
        var unknown = new List<string>();
        double hatch = 0;
        foreach (ShipModule module in build.Modules)
        {
            if (module.Slot.Equals("CargoHatch", StringComparison.OrdinalIgnoreCase)) hatch = module.On ? CargoHatchPower : 0;
            else if (ShipView.Place(module.Slot) is null) continue;   // paint, voice, cockpit: nothing to sum
            else if (ShipData.Find(module.Item) is { } part) fitted.Add(new(module, part, Engineered(part, module.Engineering)));
            else unknown.Add(module.Item);
        }
        Fitted? Working(params string[] kinds) => fitted.FirstOrDefault(f => f.Module.On && f.Is(kinds));

        double mass = hull.Mass + fitted.Sum(f => f["mass"]);
        double tank = fitted.Sum(f => f["fuel"]);
        int cargo = (int)fitted.Sum(f => Math.Round(f["cargo"]));   // each rack holds whole tons, also an engineered one

        // A weapon, and a scanner that works through the hardpoints, draws power only while they are deployed.
        double retracted = hatch, weapons = 0;
        foreach (Fitted f in fitted.Where(f => f.Module.On))
        {
            if (f.Part.Shelf == "hardpoints" && !f.Part.Passive) weapons += f["power"];
            else retracted += f["power"];
        }

        Fitted? drive = Working("fsd"), thrusters = Working("t"), generator = Working(ShieldGenerators);
        double booster = fitted.Where(f => f.Module.On).Sum(f => f["jumpboost"]);
        double jumpFuel = drive?["maxfuel"] ?? 0, burnt = Math.Min(jumpFuel, tank);   // a tank may hold less than one full jump's worth
        double Jump(double weight) =>
            burnt <= 0 ? 0 : Math.Pow(burnt / drive!["fuelmul"], 1 / drive["fuelpower"]) * drive["optmass"] / weight + booster;

        // Thrusters push the full tank and the reserve tank; a shield generator covers the bare hull.
        double flying = mass + tank + hull.ReserveFuel, maxMass = thrusters?["maxmass"] ?? 0;
        double thrust = thrusters is null || flying >= maxMass ? 0 : MassCurve(thrusters, flying, "speed");
        double shield = generator is null || hull.Mass >= generator["maxmass"] ? 0
            : hull.Shield * MassCurve(generator, hull.Mass) * (1 + fitted.Where(f => f.Module.On).Sum(f => f["shieldboost"]))
                + fitted.Where(f => f.Module.On).Sum(f => f["shieldaddition"]);
        // A reinforcement package adds its own strength; a lightweight one also a share of the hull's.
        double armour = hull.Armour * (1 + fitted.Where(f => f.Is("bh")).Sum(f => f["hullboost"]))
            + fitted.Where(f => f.Is(HullPackages)).Sum(f => f["hullreinforcement"] + hull.Armour * f["hullboost"]);

        var modules = new Dictionary<string, ModuleNumbers>();
        foreach (Fitted f in fitted) modules[f.Module.Slot] = new(f["mass"], f["power"]);
        return new(
            mass, tank, hull.ReserveFuel, cargo,
            Jump(mass + burnt), Jump(mass + tank), Jump(mass + tank + cargo), jumpFuel,
            fitted.Where(f => f.Is("pp")).Sum(f => f["pgen"]), retracted, retracted + weapons,
            thrust * hull.Speed, (Working("pd")?["engcap"] ?? 0) >= hull.BoostEnergy ? thrust * hull.Boost : 0,
            shield, armour, hull.Cost, fitted.Sum(f => f.Part.Cost), maxMass,
            unknown, modules);
    }

    /// <summary>
    /// A module's numbers as fitted. On a real ship the journal lists what the engineer changed,
    /// and those values are the truth. A module engineered in the designer carries no such list:
    /// it gets the best roll of its blueprint's grade, then its experimental effect.
    /// </summary>
    static Dictionary<string, double> Engineered(ShipData.Part part, ModuleEngineering? work)
    {
        var numbers = new Dictionary<string, double>(part.Numbers);
        if (work is null) return numbers;
        if (work.Modifiers.Count > 0)
        {
            foreach (ModuleModifier change in work.Modifiers)
            {
                if (!Labels.TryGetValue(change.Label, out var known)) continue;
                if (change.OriginalValue != 0) Follow(numbers, part.Group, known.Number, change.Value / change.OriginalValue);
                numbers[known.Number] = change.Value * known.Unit;
            }
            return numbers;
        }
        ShipData.Blueprint? blueprint = ShipData.Engineering(part.Group).Blueprints
            .FirstOrDefault(b => b.Code.Equals(work.Blueprint, StringComparison.OrdinalIgnoreCase));
        if (blueprint?.Grades.GetValueOrDefault(work.Level) is { } grade)
            foreach ((string number, double change) in grade) Change(numbers, part.Group, number, change);
        // What an experimental effect changes (0.04 is 4% more) is in the bundled table under the effect's
        // code, among numbers the sums do not read and a few entries that are not numbers at all. The
        // table's other rows are no effect an engineer offers today, so only a "special_" code is looked up.
        string code = work.Experimental.ToLowerInvariant();
        if (code.StartsWith("special_", StringComparison.Ordinal) && GameData.Coriolis.GetProperty("modifierActions").Field(code) is { } effect)
            foreach (JsonProperty change in effect.EnumerateObject().Where(change => change.Value.ValueKind == JsonValueKind.Number))
                Change(numbers, part.Group, change.Name, change.Value.GetDouble());
        return numbers;
    }

    /// <summary>One change by a fraction (0.3 is 30% more), the way the game applies each kind of number.</summary>
    static void Change(Dictionary<string, double> numbers, string group, string number, double change)
    {
        // A boost is itself a percentage on top of 100%, and the change applies to the whole.
        if (number is "hullboost" or "shieldboost") numbers[number] = (1 + numbers.GetValueOrDefault(number)) * (1 + change) - 1;
        else if (numbers.ContainsKey(number))
        {
            numbers[number] *= 1 + change;
            Follow(numbers, group, number, 1 + change);
        }
    }

    /// <summary>
    /// The numbers that move along when one is changed. The weight a module is built for takes its
    /// least and most weight with it (a shield generator's most only upwards); the strength at that
    /// weight takes the strengths at the least and the most weight with it.
    /// </summary>
    static void Follow(Dictionary<string, double> numbers, string group, string number, double factor)
    {
        string[] along = number switch
        {
            "optmass" when ShieldGenerators.Contains(group) && factor < 1 => ["minmass"],
            "optmass" => ["minmass", "maxmass"],
            "optmul" => ["minmul", "maxmul", "optmulspeed", "minmulspeed", "maxmulspeed"],
            _ => [],
        };
        foreach (string other in along)
            if (numbers.ContainsKey(other)) numbers[other] *= factor;
    }

    /// <summary>
    /// How much of its strength a thruster or shield generator gives at a weight: most at its least
    /// weight, the rated strength at the weight it is built for, least at its most weight, along the
    /// game's curve between them. Some thrusters have multipliers of their own for speed.
    /// </summary>
    static double MassCurve(Fitted module, double mass, string kind = "")
    {
        double Strength(string at) => module[at + kind] is > 0 and var own ? own : module[at];
        double minMass = module["minmass"], optMass = module["optmass"], maxMass = module["maxmass"];
        double minMul = Strength("minmul"), optMul = Strength("optmul"), maxMul = Strength("maxmul");
        double span = maxMass - minMass;
        double power = Math.Log((optMul - minMul) / (maxMul - minMul)) / Math.Log(Math.Min(1, (maxMass - optMass) / span));
        double strength = minMul + Math.Pow(Math.Min(1, (maxMass - mass) / span), power) * (maxMul - minMul);
        return double.IsFinite(strength) ? strength : 0;   // a module whose numbers make no curve gives nothing, rather than a number no page can show
    }
}

/// <summary>What one fitted module weighs and draws, after engineering.</summary>
public sealed record ModuleNumbers(double Mass, double Power);
