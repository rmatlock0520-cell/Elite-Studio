using System.Text.Json;
using System.Text.RegularExpressions;

namespace EliteStudio.Core;

/// <summary>
/// A frame shift drive as it leaves the shop, before any engineering: what the game's own fuel
/// sum needs to know about it, under the names Spansh gives the numbers.
/// </summary>
/// <param name="FuelPower">The sum's constant that goes with the drive's size.</param>
/// <param name="FuelMultiplier">The sum's constant that goes with the drive's rating.</param>
/// <param name="MaxFuelPerJump">The most fuel one jump can burn, tons.</param>
/// <param name="OptimalMass">The ship weight the drive is built for, tons.</param>
/// <param name="SuperchargeMultiplier">How many times a neutron star stretches one jump: 4, or 6 with the Mk II overcharge drive.</param>
public readonly record struct FrameShiftDrive(
    double FuelPower, double FuelMultiplier, double MaxFuelPerJump, double OptimalMass, int SuperchargeMultiplier);

/// <summary>
/// The ship table, the frame shift drives and the travel sums. The ship table and the sums are
/// taken over from the Haul Overlay; the drives are read once from the bundled module table.
/// Pure functions otherwise: nothing here reads a file or keeps state. Units: light years, tons,
/// seconds, light-seconds.
/// </summary>
public static partial class ShipMath
{
    /// <summary>Seconds one hyperspace jump takes, from charging to arrival.</summary>
    public const double SecondsPerJump = 45;

    /// <summary>Seconds one stop takes: docking plus trading.</summary>
    public const double SecondsPerStop = 90;

    // Journal ship code -> display name and landing pad (1 = small, 2 = medium, 3 = large).
    // "independant_trader" is the game's own spelling.
    static readonly Dictionary<string, (string Name, int Pad)> Ships = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sidewinder"] = ("Sidewinder", 1), ["eagle"] = ("Eagle", 1), ["hauler"] = ("Hauler", 1),
        ["adder"] = ("Adder", 1), ["empire_eagle"] = ("Imperial Eagle", 1), ["viper"] = ("Viper Mk III", 1),
        ["viper_mkiv"] = ("Viper Mk IV", 1), ["cobramkiii"] = ("Cobra Mk III", 1), ["cobramkiv"] = ("Cobra Mk IV", 1),
        ["cobramkv"] = ("Cobra Mk V", 1), ["diamondback"] = ("Diamondback Scout", 1),
        ["diamondbackxl"] = ("Diamondback Explorer", 1), ["dolphin"] = ("Dolphin", 1),
        ["empire_courier"] = ("Imperial Courier", 1), ["vulture"] = ("Vulture", 1),
        ["smallcombat01_nx"] = ("Kestrel Mk II", 1),

        ["type6"] = ("Type-6 Transporter", 2), ["type8"] = ("Type-8 Transporter", 2), ["asp"] = ("Asp Explorer", 2),
        ["asp_scout"] = ("Asp Scout", 2), ["federation_dropship"] = ("Federal Dropship", 2),
        ["federation_dropship_mkii"] = ("Federal Assault Ship", 2), ["federation_gunship"] = ("Federal Gunship", 2),
        ["independant_trader"] = ("Keelback", 2), ["krait_mkii"] = ("Krait Mk II", 2),
        ["krait_light"] = ("Krait Phantom", 2), ["mamba"] = ("Mamba", 2), ["ferdelance"] = ("Fer-de-Lance", 2),
        ["python"] = ("Python", 2), ["python_nx"] = ("Python Mk II", 2), ["typex"] = ("Alliance Chieftain", 2),
        ["typex_2"] = ("Alliance Crusader", 2), ["typex_3"] = ("Alliance Challenger", 2),
        ["mandalay"] = ("Mandalay", 2), ["corsair"] = ("Corsair", 2), ["lakonminer"] = ("Type-11 Prospector", 2),

        ["type7"] = ("Type-7 Transporter", 3), ["type9"] = ("Type-9 Heavy", 3),
        ["type9_military"] = ("Type-10 Defender", 3), ["anaconda"] = ("Anaconda", 3),
        ["federation_corvette"] = ("Federal Corvette", 3), ["cutter"] = ("Imperial Cutter", 3),
        ["empire_trader"] = ("Imperial Clipper", 3), ["belugaliner"] = ("Beluga Liner", 3), ["orca"] = ("Orca", 3),
        ["panthermkii"] = ("Panther Clipper Mk II", 3), ["explorer_nx"] = ("Caspian Explorer", 3),
    };

    /// <summary>The name players know a ship by. A ship missing from the table shows its journal code.</summary>
    public static string ShipName(string ship) => Ships.TryGetValue(ship, out var known) ? known.Name : ship;

    /// <summary>
    /// The smallest landing pad the ship fits on: 1 small, 2 medium, 3 large. An unknown ship counts
    /// as large, so a big ship is never sent to an outpost by mistake.
    /// </summary>
    public static int ShipPad(string? ship) => ship is not null && Ships.TryGetValue(ship, out var known) ? known.Pad : 3;

    /// <summary>Light years a Guardian FSD Booster of that module size adds to every jump.</summary>
    public static double GuardianBoost(int size) => size switch { 1 => 4.0, 2 => 6.0, 3 => 7.75, 4 => 9.25, 5 => 10.5, _ => 0.0 };

    // Every frame shift drive of the bundled module table by its code. Left out: the pre-engineered
    // copy the table lists under a plain drive's own code, and a placeholder of the table's own
    // that the names table does not have.
    static readonly Lazy<Dictionary<string, FrameShiftDrive>> drives = new(() =>
    {
        var table = new Dictionary<string, FrameShiftDrive>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement file in GameData.Coriolis.GetProperty("standard").EnumerateArray())
            foreach (JsonElement drive in file.List("fsd"))
            {
                string code = drive.Text("symbol");
                if (drive.Field("preEngineered") is not null || !GameData.Modules.ContainsKey(code)) continue;
                // How far a neutron star stretches a jump is not in the table: six times for the Mk II
                // overcharge drive (the value on Spansh's site), four times for every other.
                int supercharge = code.EndsWith("_overchargebooster_mkii", StringComparison.OrdinalIgnoreCase) ? 6 : 4;
                table[code] = new(drive.Number("fuelpower"), drive.Number("fuelmul"), drive.Number("maxfuel"), drive.Number("optmass"), supercharge);
            }
        return table;
    });

    /// <summary>
    /// The frame shift drive with that module code (e.g. "int_hyperdrive_overcharge_size5_class5")
    /// as built, or null for a drive Studio has no numbers for. This is the one drive table: the
    /// jump range sum, the ship numbers sent to Spansh and the ship designer all read the bundled
    /// module table (<see cref="GameData.Coriolis"/>).
    /// </summary>
    public static FrameShiftDrive? Fsd(string item)
    {
        // A starter ship's free drive ("int_hyperdrive_size2_class1_free") is the plain one under another name.
        if (item.EndsWith("_free", StringComparison.OrdinalIgnoreCase)) item = item[..^5];
        return drives.Value.TryGetValue(item, out FrameShiftDrive drive) ? drive : null;
    }

    /// <summary>
    /// What the jump range sum needs from a ship's modules: the frame shift drive's fuel per jump
    /// (an engineered value wins over the table; 0 for a drive the table does not have) and the
    /// Guardian booster's bonus.
    /// </summary>
    public static (double FsdFuel, double BoostLy) DriveNumbers(IEnumerable<ShipModule> modules)
    {
        double fuel = 0.0, boost = 0.0;
        foreach (ShipModule module in modules)
        {
            if (Booster().Match(module.Item) is { Success: true } booster)
            {
                boost = GuardianBoost(Digit(booster, 1));
            }
            else if (module.Slot.Equals("FrameShiftDrive", StringComparison.OrdinalIgnoreCase))
            {
                fuel = Fsd(module.Item)?.MaxFuelPerJump ?? 0.0;
                foreach (ModuleModifier change in module.Engineering?.Modifiers ?? [])
                    if (change.Label.Equals("MaxFuelPerJump", StringComparison.OrdinalIgnoreCase) && change.Value != 0)
                        fuel = change.Value;
            }
        }
        return (fuel, boost);
    }

    /// <summary>
    /// How far the ship jumps with an empty hold and with a full one, both with a full fuel tank,
    /// to one decimal. <paramref name="best"/> is the journal's MaxJumpRange: no cargo and only the
    /// fuel for one jump on board. Range shrinks in step with the ship's total weight; a Guardian
    /// booster adds a fixed amount on top. Unknown ship: 0 and 0.
    /// </summary>
    public static (double Empty, double Laden) JumpRanges(double best, double mass, double tank, double fsdFuel, double boostLy, int cargo)
    {
        if (best <= 0) return (0.0, 0.0);
        if (mass <= 0) return (Math.Round(best * 0.95, 1), Math.Round(best * 0.8, 1));   // weight not known
        double boost = Math.Min(boostLy, best - 0.1);
        double reach = (best - boost) * (mass + fsdFuel);   // the drive's "range times weight", which stays the same
        return (Math.Round(reach / (mass + tank) + boost, 1), Math.Round(reach / (mass + tank + cargo) + boost, 1));
    }

    /// <summary>
    /// Jumps needed for a distance; 0 when the range or the distance is not known. Every jump has to
    /// end at a star and there is rarely one right at the edge of the range, so each jump covers a
    /// little less: about sqrt(106 / range) light years less at 0.003 stars per cubic light year.
    /// Seen in play: 86.1 ly at 14.8 ly range took the game's fastest route 8 jumps. An estimate.
    /// </summary>
    public static int JumpsFor(double ly, double range)
    {
        if (range <= 0 || ly <= 0.01) return 0;
        if (ly <= range) return 1;
        return (int)Math.Ceiling(ly / Math.Max(range * 0.6, range - Math.Sqrt(106 / range)));
    }

    /// <summary>
    /// Seconds in supercruise from the arrival star to a station that many light-seconds out.
    /// An unknown distance (0 or less) counts as 300 light-seconds.
    /// </summary>
    public static double SupercruiseSeconds(double lightSeconds) =>
        20 + 6 * Math.Pow(lightSeconds <= 0 ? 300 : lightSeconds, 0.43);

    /// <summary>
    /// Light years between two places in the galaxy in a straight line. Each place is [x, y, z] in
    /// light years, the way the game writes a system's StarPos; a caller that holds three separate
    /// numbers writes them as [x, y, z] on the spot.
    /// </summary>
    public static double Distance(ReadOnlySpan<double> from, ReadOnlySpan<double> to)
    {
        double x = to[0] - from[0], y = to[1] - from[1], z = to[2] - from[2];
        return Math.Sqrt(x * x + y * y + z * z);
    }

    static int Digit(Match match, int group) => match.Groups[group].Value[0] - '0';

    [GeneratedRegex("guardianfsdbooster_size([0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Booster();
}
