using System.Globalization;
using EliteStudio.Core;

namespace EliteStudio.Tests;

/// <summary>
/// The ship table and travel sums against the Haul Overlay's own numbers (its source comments,
/// its developer self-test, and worked examples from the owner's ships), and the frame shift
/// drives of the bundled module table against the values in the analysis notes.
/// </summary>
public class ShipMathTests
{
    [Theory]
    [InlineData("type8", "Type-8 Transporter", 2)]
    [InlineData("mandalay", "Mandalay", 2)]
    [InlineData("smallcombat01_nx", "Kestrel Mk II", 1)]
    [InlineData("independant_trader", "Keelback", 2)]       // the game's own spelling
    [InlineData("Krait_MkII", "Krait Mk II", 2)]            // LoadGame writes ship codes in mixed case
    [InlineData("panthermkii", "Panther Clipper Mk II", 3)]
    [InlineData("empire_trader", "Imperial Clipper", 3)]
    [InlineData("testbuggy", "testbuggy", 3)]               // not a ship in the table: its code, and the safe pad size
    public void Ship_table_gives_name_and_landing_pad(string code, string name, int pad)
    {
        Assert.Equal(name, ShipMath.ShipName(code));
        Assert.Equal(pad, ShipMath.ShipPad(code));
    }

    [Fact]
    public void A_ship_not_read_yet_needs_a_large_pad() => Assert.Equal(3, ShipMath.ShipPad(null));

    // The notes' drive table (external-apis.md A.5: Auto_Neutron's fsd.py, checked against Spansh),
    // laid out as it is there: the most fuel per jump and the optimal mass, in tons, for the
    // ratings E / D / C / B / A of each size. It is a second source to hold the bundled table
    // against, and the two agree on every drive but one: the notes round the 4B's optimal mass
    // to 438 t, and here it is the game's own 437.5 t, as the bundled table has it.
    static readonly string[] StandardDrives =
    [
        "2: 0.60, 48 / 0.60, 54 / 0.60, 60 / 0.80, 75 / 0.90, 90",
        "3: 1.20, 80 / 1.20, 90 / 1.20, 100 / 1.50, 125 / 1.80, 150",
        "4: 2.00, 280 / 2.00, 315 / 2.00, 350 / 2.50, 437.5 / 3.00, 525",
        "5: 3.30, 560 / 3.30, 630 / 3.30, 700 / 4.10, 875 / 5.00, 1050",
        "6: 5.30, 960 / 5.30, 1080 / 5.30, 1200 / 6.60, 1500 / 8.00, 1800",
        "7: 8.50, 1440 / 8.50, 1620 / 8.50, 1800 / 10.60, 2250 / 12.80, 2700",
    ];

    static readonly string[] OverchargeDrives =
    [
        "2: 0.60, 60 / 0.90, 90 / 0.90, 90 / 0.90, 90 / 1.00, 100",
        "3: 1.20, 100 / 1.80, 150 / 1.80, 150 / 1.80, 150 / 1.90, 167",
        "4: 2.00, 350 / 3.00, 525 / 3.00, 525 / 3.00, 525 / 3.20, 585",
        "5: 3.30, 700 / 5.00, 1050 / 5.00, 1050 / 5.00, 1050 / 5.20, 1175",
        "6: 5.30, 1200 / 8.00, 1800 / 8.00, 1800 / 8.00, 1800 / 8.30, 2000",
        "7: 8.50, 1800 / 12.80, 2700 / 12.80, 2700 / 12.80, 2700 / 13.10, 3000",
        "8: 13.60, 2800 / 20.40, 4200 / 20.40, 4200 / 20.40, 4200 / 20.70, 4670",
    ];

    [Fact]
    public void The_drive_table_has_every_drive_of_the_notes()
    {
        double[] power = [2.0, 2.15, 2.3, 2.45, 2.6, 2.75, 2.9];   // by size, 2 to 8
        // by rating, E to A: the notes' 11, 10, 8, 10, 12 and 8, 12, 12, 12, 13, each divided by 1000
        double[] standard = [0.011, 0.010, 0.008, 0.010, 0.012], overcharge = [0.008, 0.012, 0.012, 0.012, 0.013];
        int drives = 0;
        foreach ((string[] rows, string family, double[] multiplier) in new[] { (StandardDrives, "", standard), (OverchargeDrives, "_overcharge", overcharge) })
        {
            foreach (string row in rows)
            {
                int size = row[0] - '0';
                string[] ratings = row[3..].Split(" / ");
                for (int rating = 1; rating <= 5; rating++, drives++)
                {
                    double[] built = [.. ratings[rating - 1].Split(", ").Select(number => double.Parse(number, CultureInfo.InvariantCulture))];
                    Assert.Equal(
                        new FrameShiftDrive(power[size - 2], multiplier[rating - 1], built[0], built[1], 4),
                        ShipMath.Fsd($"int_hyperdrive{family}_size{size}_class{rating}"));
                }
            }
        }
        Assert.Equal((6 + 7) * 5, drives);
    }

    [Fact]
    public void The_Mk_II_drive_has_its_own_numbers_and_an_unknown_drive_has_none()
    {
        // Spansh's own values; the one drive a neutron star stretches six times instead of four.
        Assert.Equal(new FrameShiftDrive(2.5025, 0.011, 6.8, 4670, 6), ShipMath.Fsd("int_hyperdrive_overcharge_size8_class5_overchargebooster_mkii"));
        Assert.Equal(new FrameShiftDrive(2.45, 0.008, 3.3, 700, 4), ShipMath.Fsd("Int_Hyperdrive_Size5_Class3"));   // the journal writes codes in either case
        Assert.Equal(new FrameShiftDrive(2.0, 0.011, 0.6, 48, 4), ShipMath.Fsd("Int_Hyperdrive_Size2_Class1_free"));   // a starter ship's free drive is the plain 2E

        string[] unknown =
        [
            "int_hyperdrive_size8_class5",              // the standard drives stop at size 7
            "int_hyperdrive_size1_class5", "int_hyperdrive_overcharge_size9_class1",
            "int_hyperdrive_size5_class0", "int_hyperdrive_size5_class6",
            "int_engine_size5_class5", "int_guardianfsdbooster_size5", "",
        ];
        Assert.All(unknown, drive => Assert.Null(ShipMath.Fsd(drive)));
    }

    static ShipModule Module(string slot, string item, double maxFuelPerJump = 0) => new(slot, item, 0, "", true, 0, 1, 0,
        maxFuelPerJump == 0 ? null : new ModuleEngineering("", "FSD_LongRange", 5, 1, "", "", [new("MaxFuelPerJump", maxFuelPerJump, 5.2, false, "")]));

    [Theory]
    [InlineData("int_hyperdrive_overcharge_size5_class5", 5.2)]    // the owner's Type-8 and Mandalay
    [InlineData("int_hyperdrive_overcharge_size4_class1", 2.0)]    // the Kestrel
    [InlineData("int_hyperdrive_overcharge_size5_class3", 5.0)]
    [InlineData("int_hyperdrive_overcharge_size6_class4", 8.0)]    // the Anaconda
    [InlineData("int_hyperdrive_size5_class4", 4.1)]
    [InlineData("int_hyperdrive_size5_class2", 3.3)]
    [InlineData("int_hyperdrive_size3_class5", 1.8)]
    [InlineData("int_hyperdrive_size8_class5", 0.0)]               // a drive the table does not have
    // Where the table differs from the Haul Overlay's rule of thumb (an A-rated overcharge drive
    // burns 1.04 times what the plain A drive does), which also knew no size 8:
    [InlineData("int_hyperdrive_overcharge_size2_class5", 1.0)]    // was 0.9
    [InlineData("int_hyperdrive_overcharge_size4_class5", 3.2)]    // was 3.1
    [InlineData("int_hyperdrive_overcharge_size7_class5", 13.1)]   // was 13.3
    [InlineData("int_hyperdrive_overcharge_size8_class5", 20.7)]   // was 0
    [InlineData("int_hyperdrive_overcharge_size8_class5_overchargebooster_mkii", 6.8)]   // was 0
    public void Fuel_per_jump_for_the_jump_range_sum_comes_from_the_drive_table(string drive, double tons) =>
        Assert.Equal((tons, 0.0), ShipMath.DriveNumbers([Module("FrameShiftDrive", drive)]));

    [Theory]
    [InlineData(1, 4.0)]
    [InlineData(3, 7.75)]
    [InlineData(5, 10.5)]
    [InlineData(6, 0.0)]
    public void Guardian_booster_bonus(int size, double ly) => Assert.Equal(ly, ShipMath.GuardianBoost(size));

    [Theory]
    // best, weight, tank, fuel per jump, booster, cargo -> empty, laden
    [InlineData(27.562246, 486.600006, 32, 5.2, 0, 404, 26.1, 14.7)]          // the owner's Type-8
    [InlineData(56.968773, 388.600006, 32, 5.2, 10.5, 32, 54.0, 50.9)]        // the owner's Mandalay with a booster
    [InlineData(56.968773, 388.600006, 32, 5.2, 10.5, 388, 54.0, 33.1)]
    [InlineData(11.170611, 343.600006, 16, 2.0, 0, 4, 10.7, 10.6)]            // Kestrel Mk II
    [InlineData(56.968773, 0, 32, 5.2, 10.5, 32, 54.1, 45.6)]                 // weight not known: 95 % and 80 %
    [InlineData(0, 388.6, 32, 5.2, 10.5, 32, 0.0, 0.0)]                       // ship not known
    [InlineData(5.0, 100, 8, 1.2, 10.5, 0, 5.0, 5.0)]                         // a booster figure above the best range is capped just below it
    public void Jump_ranges(double best, double mass, double tank, double fsdFuel, double boost, int cargo, double empty, double laden) =>
        Assert.Equal((empty, laden), ShipMath.JumpRanges(best, mass, tank, fsdFuel, boost, cargo));

    [Theory]
    [InlineData(86.1, 14.8, 8)]     // seen in play: the game's fastest route took 8
    [InlineData(64.1, 14.8, 6)]
    [InlineData(86.1, 14.0, 8)]
    [InlineData(86.1, 27.0, 4)]
    [InlineData(17.65, 14.0, 2)]
    [InlineData(17.65, 27.0, 1)]
    [InlineData(30.0, 12.0, 4)]
    [InlineData(14.0, 14.0, 1)]     // exactly in range
    [InlineData(14.01, 14.0, 2)]
    [InlineData(0.02, 14.0, 1)]
    [InlineData(0.01, 14.0, 0)]     // already there
    [InlineData(10.0, 0.0, 0)]      // range not known
    [InlineData(9.0, 5.0, 3)]       // short ranges: at least 60 % of the range is covered per jump
    public void Jumps_for_a_distance(double ly, double range, int jumps) => Assert.Equal(jumps, ShipMath.JumpsFor(ly, range));

    [Theory]
    [InlineData(0, 89.71)]          // unknown counts as 300 light-seconds
    [InlineData(300, 89.71)]
    [InlineData(10, 36.15)]
    [InlineData(1000, 136.99)]
    [InlineData(5000, 253.73)]
    [InlineData(100000, 867.52)]
    public void Supercruise_seconds(double lightSeconds, double seconds) =>
        Assert.Equal(seconds, Math.Round(ShipMath.SupercruiseSeconds(lightSeconds), 2));

    [Fact]
    public void A_round_trip_adds_up_like_the_haul_overlay()
    {
        // 86.1 ly out with a full hold (14 ly) and back empty (27 ly); sell station 1,000 ls out, buy station 300 ls.
        double seconds = (ShipMath.JumpsFor(86.1, 14) + ShipMath.JumpsFor(86.1, 27)) * ShipMath.SecondsPerJump
            + ShipMath.SupercruiseSeconds(1000) + ShipMath.SupercruiseSeconds(300) + 2 * ShipMath.SecondsPerStop;
        Assert.Equal(946.70, Math.Round(seconds, 2));
    }

    [Fact]
    public void Drive_numbers_come_from_the_module_list()
    {
        Assert.Equal((5.2, 10.5), ShipMath.DriveNumbers([
            Module("FrameShiftDrive", "int_hyperdrive_overcharge_size5_class5"), Module("Slot02_Size5", "int_guardianfsdbooster_size5")]));
        Assert.Equal((3.3, 0.0), ShipMath.DriveNumbers([Module("FrameShiftDrive", "Int_Hyperdrive_Size5_Class2")]));
        // an engineered fuel figure wins over the table
        Assert.Equal((5.9, 0.0), ShipMath.DriveNumbers([Module("FrameShiftDrive", "int_hyperdrive_size5_class5", 5.9)]));
        // a drive lying in the hold is not the one fitted
        Assert.Equal((0.0, 0.0), ShipMath.DriveNumbers([Module("Slot03_Size5", "int_hyperdrive_size5_class5")]));
    }

    [Fact]
    public void Distance_is_the_straight_line_between_two_places()
    {
        double[] sol = [0, 0, 0], sirius = [6.25, -1.28125, -5.75];
        Assert.Equal(0, ShipMath.Distance(sol, sol));
        Assert.Equal(13, ShipMath.Distance(sol, [3, -4, 12]));
        Assert.Equal(13, ShipMath.Distance([-1, 2, 5], [2, -2, 17]));   // the same step taken from somewhere else
        Assert.Equal(ShipMath.Distance(sol, sirius), ShipMath.Distance(sirius, sol));
        Assert.Equal(8.59, Math.Round(ShipMath.Distance(sol, sirius), 2));   // as the galaxy map shows it
        // Spansh's own figure for Sol to PSR J1752-2806, the first leg of the recorded neutron route
        Assert.Equal(407.486400990496, ShipMath.Distance(sol, [-10.96875, -6.84375, 407.28125]), 9);
    }
}
