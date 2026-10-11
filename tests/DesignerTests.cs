using System.Text.Json;
using System.Text.Json.Nodes;
using EliteStudio.Core;
using EliteStudio.Loadouts;

namespace EliteStudio.Tests;

/// <summary>
/// The ship designer's tables and sums. The game itself is the referee wherever it can be: every
/// Loadout line of the recordings carries the real ship's weight, best jump, hold and tank, and the
/// sums fed with that same line must arrive at the same numbers. Where the journal has no answer
/// (speed, shields, armour, power) the numbers are held against EDSY, the community's reference
/// ship designer, which was shown the same recorded ships.
/// </summary>
public sealed class DesignerTests
{
    const string Anaconda = "Journal.2026-09-24T191106.01.log";
    const string Kestrel = "Journal.2026-09-30T231247.01.log";
    const string Krait = "Journal.2026-10-05T015030.01.log";         // its first Loadout; the last is the Type-8
    const string Type8 = "Journal.2026-10-05T152112.01.log";
    const string Mandalay = "Journal.2026-10-08T050849.01.log";      // before the engineers
    const string Engineered = "Journal.2026-10-08T080236.01.log";    // the same Mandalay after six engineering jobs

    /// <summary>Every Loadout line of every recording.</summary>
    static IEnumerable<string> RecordedLoadouts() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "journals"), "*.log").Order()
            .SelectMany(file => File.ReadLines(file).Where(line => line.Contains("\"event\":\"Loadout\"")));

    static string Line(string recording, bool first = false)
    {
        IEnumerable<string> lines = PracticeGame.Recording(recording).Where(line => line.Contains("\"event\":\"Loadout\""));
        return first ? lines.First() : lines.Last();
    }

    static ShipLoadout Parse(string line) => ShipLoadout.Parse(JsonSerializer.Deserialize<JsonElement>(line));

    /// <summary>A recorded ship with something changed, the way the designer's page changes its copy.</summary>
    static ShipLoadout Changed(string recording, Action<JsonArray> change)
    {
        JsonNode loadout = JsonNode.Parse(Line(recording))!;
        change(loadout["Modules"]!.AsArray());
        return ShipLoadout.Parse(JsonSerializer.SerializeToElement(loadout));
    }

    static JsonObject Module(JsonArray modules, string slot) => modules.Single(module => module!["Slot"]!.GetValue<string>() == slot)!.AsObject();

    static ShipData.Slot Slot(ShipData.Hull hull, string id) => hull.Slots.Single(slot => slot.Id == id);

    static string[] Fits(string ship, string slot) =>
        [.. ShipData.Fits(ShipData.Hulls[ship], Slot(ShipData.Hulls[ship], slot)).Select(part => part.Item.ToLowerInvariant())];

    // ------------------------------------------------------------ the game as referee

    [Fact]
    public void Every_recorded_ship_weighs_jumps_and_carries_what_the_game_says()
    {
        var ships = new HashSet<string>();
        foreach (string line in RecordedLoadouts())
        {
            ShipLoadout ship = Parse(line);
            BuildStats stats = BuildStats.Of(ship)!;
            ships.Add(ship.Ship);

            Assert.Empty(stats.Unknown);
            // Asked for: weight within 0.1 t, best jump within 0.05 ly. Met: every recorded ship to a kilogram and a ten-thousandth of a light year.
            Assert.Equal(ship.UnladenMass, stats.UnladenMass, 0.001);
            Assert.Equal(ship.MaxJumpRange, stats.JumpMax, 0.0001);
            Assert.Equal(ship.CargoCapacity, stats.CargoCapacity);
            Assert.Equal(ship.FuelMain, stats.FuelCapacity);
        }
        Assert.Equal(new[] { "anaconda", "krait_mkii", "mandalay", "smallcombat01_nx", "type8", "viper" }, ships.Order());
    }

    [Fact]
    public void A_full_tank_and_a_full_hold_shorten_the_jump_as_the_status_strip_says()
    {
        // The strip's two numbers come from the game's best jump (ShipMath.JumpRanges); the designer's come from the drive itself.
        foreach (string line in RecordedLoadouts())
        {
            ShipLoadout ship = Parse(line);
            BuildStats stats = BuildStats.Of(ship)!;
            var (fuel, boost) = ShipMath.DriveNumbers(ship.Modules);
            var (empty, laden) = ShipMath.JumpRanges(ship.MaxJumpRange, ship.UnladenMass, ship.FuelMain, fuel, boost, ship.CargoCapacity);
            Assert.Equal(empty, stats.JumpEmpty, 0.051);   // the strip rounds to a tenth
            Assert.Equal(laden, stats.JumpLaden, 0.051);
        }
    }

    [Fact]
    public void The_blueprint_table_changes_a_module_as_the_game_did()
    {
        // Four of the Mandalay's engineering jobs were rolled to the end of their grade. Forgetting the game's
        // own list of changed numbers for them leaves blueprint, grade and experimental effect: what a module
        // engineered in the designer carries. The tables must then give the numbers the game gave.
        BuildStats real = BuildStats.Of(Parse(Line(Engineered)))!;
        var planned = new List<string>();
        BuildStats designed = BuildStats.Of(Changed(Engineered, modules =>
        {
            foreach (JsonObject work in modules.Select(module => module!["Engineering"]).OfType<JsonObject>())
            {
                if (work["Quality"]!.GetValue<double>() < 1) continue;
                work.Remove("Modifiers");
                planned.Add($"{work["BlueprintName"]} {work["Level"]} {work["ExperimentalEffect"]}");
            }
        }))!;
        Assert.Equal(new[] { "Weapon_Overcharged 1 ", "Engine_Reinforced 1 ", "FSD_LongRange 3 special_fsd_lightweight", "ShieldGenerator_Optimised 3 special_shield_regenerative" }, planned);

        Assert.Equal(388.6, designed.UnladenMass, 0.001);       // the journal's 388.600006
        Assert.Equal(56.968765, designed.JumpMax, 0.0001);      // the journal's best jump
        foreach (string slot in new[] { "SmallHardpoint1", "MainEngines", "FrameShiftDrive", "Slot04_Size4" })
        {
            Assert.Equal(real.Modules[slot].Mass, designed.Modules[slot].Mass, 0.0001);
            Assert.Equal(real.Modules[slot].Power, designed.Modules[slot].Power, 0.0001);
        }
        // The game's own numbers for the three: thrusters 21 t, the drive 21.6 t and 0.654 MW, the shield generator 3.3 t and 1.764 MW.
        Assert.Equal(21, designed.Modules["MainEngines"].Mass, 0.0001);
        Assert.Equal(new ModuleNumbers(21.6, 0.654), designed.Modules["FrameShiftDrive"] with { Mass = Math.Round(designed.Modules["FrameShiftDrive"].Mass, 4) });
        Assert.Equal(3.3, designed.Modules["Slot04_Size4"].Mass, 0.0001);
        Assert.Equal(1.764, designed.Modules["Slot04_Size4"].Power, 0.0001);
        Assert.Equal(real.Shield, designed.Shield, 0.001);      // strength +9% and the built-for weight -4%, as the game wrote them
        Assert.Equal(real.Speed, designed.Speed, 0.0001);       // the thrusters' extra ton is in both
        Assert.Equal(real.Boost, designed.Boost, 0.0001);
    }

    [Fact]
    public void A_top_grade_long_range_drive_reaches_what_every_ship_designer_says()
    {
        // A 5A drive with grade 5 Increased Range and Mass Manager is built for 1,692.6 t (1,050 t, +55%, then +4%)
        // and weighs 26 t: the figures EDSY and Coriolis show, and what the game writes for a finished roll.
        ShipLoadout plain = Changed(Mandalay, modules =>
        {
            Module(modules, "FrameShiftDrive")["Item"] = "int_hyperdrive_size5_class5";
            modules.Remove(Module(modules, "Slot02_Size5"));   // without the Guardian booster, so the drive alone is seen
        });
        ShipLoadout ranged = Changed(Mandalay, modules =>
        {
            JsonObject drive = Module(modules, "FrameShiftDrive");
            drive["Item"] = "int_hyperdrive_size5_class5";
            drive["Engineering"] = new JsonObject { ["BlueprintName"] = "FSD_LongRange", ["Level"] = 5, ["Quality"] = 1.0, ["ExperimentalEffect"] = "special_fsd_heavy" };
            modules.Remove(Module(modules, "Slot02_Size5"));
        });
        BuildStats before = BuildStats.Of(plain)!, after = BuildStats.Of(ranged)!;

        Assert.Equal(26, after.Modules["FrameShiftDrive"].Mass, 0.0001);
        Assert.Equal(0.6 * 1.15, after.Modules["FrameShiftDrive"].Power, 0.0001);
        Assert.Equal(before.UnladenMass + 6, after.UnladenMass, 0.0001);
        // The same drive on a heavier ship: the reach grows by 1,692.6 / 1,050 and shrinks by the weight.
        double reach = before.JumpMax * (before.UnladenMass + 5);
        Assert.Equal(reach * 1692.6 / 1050 / (after.UnladenMass + 5), after.JumpMax, 0.0001);
    }

    [Fact]
    public void My_ship_and_the_designer_agree_about_a_drive_planned_to_burn_more_fuel()
    {
        // Deep Charge lets one jump burn 10% more fuel. Planned in the designer, the drive carries no list of changed
        // numbers, which is where the "My ship" page's own sum would otherwise look for the fuel per jump.
        ShipLoadout planned = Changed(Mandalay, modules => Module(modules, "FrameShiftDrive")["Engineering"] = new JsonObject
        {
            ["BlueprintName"] = "FSD_LongRange", ["Level"] = 5, ["Quality"] = 1.0, ["ExperimentalEffect"] = "special_fsd_fuelcapacity",
        });
        BuildStats stats = BuildStats.Of(planned)!;
        Assert.Equal(5.2 * 1.1, stats.JumpFuel, 0.0001);
        Assert.Equal(5.2, BuildStats.Of(Parse(Line(Mandalay)))!.JumpFuel);

        // As saved from the bench, the build carries the designer's weight and best jump; "My ship" works out the other two.
        ShipView shown = ShipView.From(planned with { UnladenMass = stats.UnladenMass, MaxJumpRange = stats.JumpMax });
        Assert.Equal((Math.Round(stats.JumpEmpty, 1), Math.Round(stats.JumpLaden, 1)), (shown.JumpEmpty, shown.JumpLaden));
    }

    [Fact]
    public void A_guardian_booster_adds_its_light_years_only_while_fitted()
    {
        BuildStats with = BuildStats.Of(Parse(Line(Mandalay)))!;
        BuildStats without = BuildStats.Of(Changed(Mandalay, modules => modules.Remove(Module(modules, "Slot02_Size5"))))!;
        // 10.5 ly for the size 5 booster (ShipMath.GuardianBoost), and the ship is 1.3 t lighter without it.
        Assert.Equal(10.5, ShipMath.GuardianBoost(5));
        Assert.Equal(with.UnladenMass - 1.3, without.UnladenMass, 0.0001);
        Assert.Equal((with.JumpMax - 10.5) * (with.UnladenMass + 5.2) / (without.UnladenMass + 5.2), without.JumpMax, 0.0001);
    }

    // ------------------------------------------------------------ slots and what fits them

    [Fact]
    public void Every_recorded_module_sits_in_a_slot_the_table_has_and_fits_it()
    {
        foreach (string line in RecordedLoadouts())
        {
            ShipLoadout ship = Parse(line);
            ShipData.Hull hull = ShipData.Hulls[ship.Ship];
            foreach (ShipModule module in ship.Modules.Where(module => ShipView.Place(module.Slot) is not null))
            {
                ShipData.Slot slot = hull.Slots.SingleOrDefault(known => known.Id == module.Slot)
                    ?? throw new Xunit.Sdk.XunitException($"{ship.Ship} has no slot {module.Slot} in the table");
                Assert.True(ShipData.Fits(hull, slot).Any(part => part.Item.Equals(module.Item, StringComparison.OrdinalIgnoreCase)), $"{module.Item} does not fit {ship.Ship} {slot.Id}");
            }
        }
    }

    [Fact]
    public void Every_hull_of_the_table_is_known_by_its_journal_code_with_slots_the_page_can_list()
    {
        Assert.Equal(47, ShipData.Hulls.Count);
        Assert.All(ShipData.Hulls, hull => Assert.NotEqual(hull.Key, ShipMath.ShipName(hull.Key)));   // each has a name players know
        // the three ships whose id the two tables disagree about are found by name
        Assert.Equal(("imperial_corsair", "explorer_nx", "kestrel"),
            (ShipData.Hulls["corsair"].Table, ShipData.Hulls["explorer_nx"].Table, ShipData.Hulls["smallcombat01_nx"].Table));
        foreach (ShipData.Hull hull in ShipData.Hulls.Values)
        {
            Assert.Equal(hull.Slots.Count, hull.Slots.Select(slot => slot.Id).Distinct().Count());
            Assert.All(hull.Slots, slot => Assert.True(ShipView.Place(slot.Id) is not null, $"{hull.Ship} {slot.Id}"));
            // eight core slots, each with something to fit in it
            Assert.Equal(8, hull.Slots.Count(slot => ShipView.Place(slot.Id)!.Value.Group == 2));
            Assert.All(hull.Slots.Where(slot => ShipView.Place(slot.Id)!.Value.Group == 2), slot => Assert.NotEmpty(ShipData.Fits(hull, slot)));
        }
    }

    [Fact]
    public void Slots_are_numbered_as_the_journal_numbers_them()
    {
        static string[] Ids(string ship, string start) => [.. ShipData.Hulls[ship].Slots.Select(slot => slot.Id).Where(id => id.StartsWith(start, StringComparison.Ordinal))];

        // As recorded: the Anaconda's numbers jump from 10 to 13, the Type-8 has no third small hardpoint.
        Assert.Equal(new[] { "Slot01_Size7", "Slot02_Size6", "Slot03_Size6", "Slot04_Size6", "Slot05_Size5", "Slot06_Size5", "Slot07_Size5",
            "Slot08_Size4", "Slot09_Size4", "Slot10_Size4", "Slot13_Size2", "Slot14_Size1" }, Ids("anaconda", "Slot"));
        Assert.Equal(new[] { "Military01" }, Ids("anaconda", "Military"));
        Assert.Equal(new[] { "SmallHardpoint1", "SmallHardpoint2", "SmallHardpoint4", "SmallHardpoint5", "SmallHardpoint6" }, Ids("type8", "Small"));
        Assert.Equal(new[] { "TinyHardpoint1", "TinyHardpoint2", "TinyHardpoint3", "TinyHardpoint4" }, Ids("mandalay", "Tiny"));
        // Not recorded. The rules are those of Coriolis' journal import, and the names they give are the ones EDSY's
        // ship table lists: the Type-9 starts at 0 and skips 9 and 10, as the Type-10 does; the Vulture skips 4, the Dropship 7 and 8.
        Assert.Equal(new[] { "Slot00_Size8", "Slot01_Size8", "Slot02_Size7", "Slot03_Size6", "Slot04_Size5", "Slot05_Size4", "Slot06_Size4",
            "Slot07_Size3", "Slot08_Size3", "Slot11_Size2", "Slot12_Size1" }, Ids("type9", "Slot"));
        Assert.Equal(new[] { "Slot01_Size8", "Slot02_Size7", "Slot03_Size6", "Slot04_Size5", "Slot05_Size4", "Slot06_Size4", "Slot07_Size3",
            "Slot08_Size3", "Slot11_Size2", "Slot12_Size1" }, Ids("type9_military", "Slot"));
        Assert.Equal(new[] { "Slot01_Size5", "Slot02_Size4", "Slot03_Size2", "Slot05_Size1", "Slot06_Size1", "Slot07_Size1", "Slot08_Size1" }, Ids("vulture", "Slot"));
        Assert.Equal(new[] { "Slot01_Size6", "Slot02_Size5", "Slot03_Size5", "Slot04_Size4", "Slot05_Size3", "Slot06_Size3", "Slot09_Size2", "Slot10_Size1" },
            Ids("federation_dropship", "Slot"));
        // Three ships' slots grew after the game had named them, and the journal kept the names (EDSY's list): the
        // Type-7's third slot is called Size5 and takes a size 6 module.
        ShipData.Hull type7 = ShipData.Hulls["type7"];
        Assert.Equal(new[] { "Slot01_Size6", "Slot02_Size6", "Slot03_Size5", "Slot04_Size5", "Slot05_Size4", "Slot06_Size4", "Slot07_Size2", "Slot08_Size2", "Slot09_Size2", "Slot09_Size1" },
            Ids("type7", "Slot"));
        Assert.Equal(new[] { 6, 6, 6, 5, 5, 5, 3, 3, 2, 1 }, type7.Slots.Where(slot => slot.Id.StartsWith("Slot", StringComparison.Ordinal)).Select(slot => slot.Size));
        Assert.Contains("int_cargorack_size6_class1", Fits("type7", "Slot03_Size5"));
        Assert.Equal(("Size 6", "Size 5"), (ShipView.Place("Slot03_Size5", type7)!.Value.Label, ShipView.Place("Slot03_Size5")!.Value.Label));
        Assert.Equal("Slot03_Size3", Ids("independant_trader", "Slot")[2]);   // the Keelback's size 4 slot
        Assert.Equal("Slot01_Size4", Ids("asp_scout", "Slot")[0]);            // the Asp Scout's size 5 slot
        // Slots for one kind of module have names of their own.
        Assert.Equal(new[] { "Cargo01", "Cargo02" }, Ids("panthermkii", "Cargo"));
        Assert.Equal(new[] { "LargeMiningHardpoint1", "MediumMiningHardpoint1", "MediumMiningHardpoint2", "MediumHardpoint3", "SmallMiningHardpoint1" },
            ShipData.Hulls["lakonminer"].Slots.Select(slot => slot.Id).Where(id => id.Contains("Hardpoint")).Take(5));
        Assert.Contains("LimpetController01", ShipData.Hulls["lakonminer"].Slots.Select(slot => slot.Id));
        Assert.Contains("FighterBay01", ShipData.Hulls["lakonminer"].Slots.Select(slot => slot.Id));
    }

    [Fact]
    public void A_slot_takes_modules_of_its_kind_up_to_its_size()
    {
        // An optional slot: anything optional up to its size, and nothing made for another kind of slot.
        string[] bay = Fits("mandalay", "Slot02_Size5");
        Assert.Contains("int_guardianfsdbooster_size5", bay);
        Assert.Contains("int_cargorack_size5_class1", bay);
        Assert.Contains("int_fueltank_size3_class3", bay);            // a fuel tank is a core module and an optional one
        Assert.DoesNotContain("int_cargorack_size6_class1", bay);     // too big
        Assert.DoesNotContain("int_fighterbay_size5_class1", bay);    // the Mandalay carries no fighter
        Assert.DoesNotContain("int_passengercabin_size5_class4", bay);   // luxury cabins are for the three liners
        Assert.DoesNotContain("int_planetapproachsuite_advanced", bay);
        Assert.DoesNotContain(bay, item => item.StartsWith("hpt_", StringComparison.Ordinal) || item.Contains("_armour_") || item.Contains("powerplant"));
        Assert.Contains("int_fighterbay_size7_class1", Fits("anaconda", "Slot01_Size7"));
        Assert.Contains("int_passengercabin_size6_class4", Fits("belugaliner", "Slot01_Size6"));
        // each module once, although the table lists pre-engineered copies under the same code
        Assert.Equal(bay.Length, bay.Distinct().Count());

        // A military slot takes armour and shield cells only.
        string[] military = Fits("anaconda", "Military01");
        Assert.Contains("int_hullreinforcement_size5_class2", military);
        Assert.Contains("int_shieldcellbank_size5_class5", military);
        Assert.DoesNotContain(military, item => item.Contains("cargorack") || item.Contains("shieldgenerator"));

        // Weapons go in hardpoints of their size or bigger, utilities in utility mounts only.
        string[] medium = Fits("mandalay", "MediumHardpoint1"), utility = Fits("mandalay", "TinyHardpoint1");
        Assert.Contains("hpt_multicannon_fixed_medium_advanced", medium);
        Assert.Contains("hpt_pulselaser_fixed_small", medium);
        Assert.DoesNotContain(medium, item => item.EndsWith("_large", StringComparison.Ordinal) || item.Contains("shieldbooster") || item.Contains("_tiny"));
        Assert.Contains("hpt_shieldbooster_size0_class5", utility);
        Assert.DoesNotContain(utility, item => item.Contains("pulselaser"));

        // Core slots take their own kind; a hull takes its own armour; one ship's special thrusters fit no other.
        Assert.All(Fits("mandalay", "PowerPlant"), item => Assert.Contains("powerplant", item));
        Assert.Contains("int_powerplant_size4_class5", Fits("mandalay", "PowerPlant"));   // smaller than the slot is allowed
        Assert.DoesNotContain("int_powerplant_size6_class1", Fits("mandalay", "PowerPlant"));
        Assert.Equal(new[] { "mandalay_armour_grade1", "mandalay_armour_grade2", "mandalay_armour_grade3", "mandalay_armour_mirrored", "mandalay_armour_reactive" },
            Fits("mandalay", "Armour"));
        Assert.Equal(6, Fits("explorer_nx", "Armour").Length);   // the Caspian Explorer has six kinds
        Assert.Contains("int_mkiiagileboost_engine_size5_class5", Fits("smallcombat01_nx", "MainEngines"));
        Assert.DoesNotContain("int_mkiiagileboost_engine_size5_class5", Fits("mandalay", "MainEngines"));
        // The Panther Clipper's big racks go in its two cargo slots and nowhere else.
        Assert.Contains("int_largecargorack_size8_class1", Fits("panthermkii", "Cargo01"));
        Assert.DoesNotContain("int_largecargorack_size8_class1", Fits("panthermkii", "Slot01_Size8"));
    }

    [Fact]
    public void Modules_are_found_by_the_journals_code_however_it_is_written()
    {
        Assert.Equal(("fsd", 5, "A"), (ShipData.Find("INT_HYPERDRIVE_SIZE5_CLASS5")!.Group, ShipData.Find("int_hyperdrive_size5_class5")!.Class, ShipData.Find("Int_Hyperdrive_Size5_Class5")!.Rating));
        // the plain drive, not the pre-engineered one the table lists under the same code
        Assert.Equal((1050, 5.0, 2.45, 0.012), (ShipData.Find("int_hyperdrive_size5_class5")!.Number("optmass"), ShipData.Find("int_hyperdrive_size5_class5")!.Number("maxfuel"),
            ShipData.Find("int_hyperdrive_size5_class5")!.Number("fuelpower"), ShipData.Find("int_hyperdrive_size5_class5")!.Number("fuelmul")));
        Assert.Equal(32, ShipData.Find("int_cargorack_size5_class1")!.Number("cargo"));
        // a starter Sidewinder's free modules are the plain ones
        Assert.Same(ShipData.Find("int_engine_size2_class1"), ShipData.Find("Int_Engine_Size2_Class1_Free"));
        Assert.Null(ShipData.Find("int_newthing_size2_class3"));
        Assert.Null(ShipData.Find("mandalay_cockpit"));
    }

    [Fact]
    public void The_status_strip_and_the_designer_read_the_same_drives_from_the_one_table()
    {
        // Each reads the bundled table by itself (ShipMath.Fsd for the strip and for Spansh, ShipData here), and each must
        // take the plain drive: the table lists pre-engineered copies under the same codes, one with a number of its own.
        ShipData.Part[] drives = [.. ShipData.Fits(ShipData.Hulls["explorer_nx"], Slot(ShipData.Hulls["explorer_nx"], "FrameShiftDrive"))];   // the one size 8 slot takes every drive
        Assert.Equal(30 + 35 + 1, drives.Length);   // sizes 2 to 7 in five ratings, the overcharge drives up to size 8, and the Mk II
        foreach (ShipData.Part part in drives)
        {
            FrameShiftDrive strip = ShipMath.Fsd(part.Item) ?? throw new Xunit.Sdk.XunitException($"{part.Item} is not a drive the strip knows");
            Assert.Equal((part.Item, strip.MaxFuelPerJump, strip.FuelPower, strip.FuelMultiplier, strip.OptimalMass),
                (part.Item, part.Number("maxfuel"), part.Number("fuelpower"), part.Number("fuelmul"), part.Number("optmass")));
        }
    }

    [Fact]
    public void Engineers_offer_each_kind_of_module_its_own_blueprints()
    {
        ShipData.Workshop drive = ShipData.Engineering("fsd");
        Assert.Equal(new[] { "FSD_FastBoot", "FSD_LongRange", "FSD_Shielded" }, drive.Blueprints.Select(blueprint => blueprint.Code));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, drive.Blueprints[1].Grades.Keys.Order());
        Assert.Equal((0.55, 0.3, 0.15), (drive.Blueprints[1].Grades[5]["optmass"], drive.Blueprints[1].Grades[5]["mass"], drive.Blueprints[1].Grades[5]["power"]));
        Assert.Equal(0.35, drive.Blueprints[1].Grades[3]["optmass"]);
        Assert.Contains(new ShipData.Experimental("special_fsd_heavy", "Mass Manager"), drive.Experimentals);
        // A lighter module is better, so a lightweight roll ends at the bigger cut.
        Assert.Equal(-0.55, ShipData.Engineering("bh").Blueprints.Single(blueprint => blueprint.Code == "Armour_Advanced").Grades[5]["mass"]);
        // A multi-cannon's overcharge is a recipe of its own under the same game code.
        Assert.Equal(-0.15, ShipData.Engineering("mc").Blueprints.Single(blueprint => blueprint.Code == "Weapon_Overcharged").Grades[5]["clip"]);
        Assert.DoesNotContain("clip", ShipData.Engineering("pl").Blueprints.Single(blueprint => blueprint.Code == "Weapon_Overcharged").Grades[5].Keys);
        Assert.Empty(ShipData.Engineering("cr").Blueprints);     // an expanded cargo rack only comes ready-made: no engineer offers it
        Assert.Empty(ShipData.Engineering("gfsb").Blueprints);   // nobody engineers a Guardian booster
        Assert.Empty(ShipData.Engineering("gfsb").Experimentals);
        Assert.Same(ShipData.Workshop.None, ShipData.Engineering("no such kind"));
    }

    // ------------------------------------------------------------ what the journal does not say

    [Theory]
    // What coriolis.io (version 4.1.13) showed for the same recorded ships on 9 October 2026, to the digits it shows:
    // top speed and boost in m/s, the power drawn with the hardpoints retracted and deployed in MW, shield strength
    // in MJ, armour, and the cost in millions of credits.
    [InlineData("Journal.2025-04-17T013240.01.log", false, 315, 394, 7.01, 8.99, 107, 276, 0.39)]   // Viper Mk III
    [InlineData(Anaconda, false, 181, 242, 21.86, 32.58, 703, 1725, 263.35)]
    [InlineData(Kestrel, false, 314, 418, 16.42, 23.81, 378, 356, 35.80)]                           // with thrusters made for this ship alone
    [InlineData(Krait, true, 276, 379, 21.78, 30.96, 398, 1376, 132.95)]                            // bi-weave shields, three hull packages
    [InlineData(Type8, false, 222, 377, 9.28, 14.23, 0, 792, 53.99)]                                // no shield generator
    [InlineData(Mandalay, false, 325, 406, 17.83, 20.65, 207, 414, 43.61)]
    [InlineData(Engineered, false, 325, 406, 17.05, 19.84, 219, 414, 43.61)]                        // six engineered modules
    public void Speed_power_shields_armour_and_cost_are_what_Coriolis_shows_for_the_recorded_ships(
        string recording, bool first, int speed, int boost, double retracted, double deployed, int shield, int armour, double millions)
    {
        BuildStats stats = BuildStats.Of(Parse(Line(recording, first)))!;
        Assert.Equal((speed, boost), ((int)Math.Round(stats.Speed), (int)Math.Round(stats.Boost)));
        Assert.Equal((retracted, deployed), (Math.Round(stats.PowerRetracted, 2), Math.Round(stats.PowerDeployed, 2)));
        Assert.Equal((shield, armour), ((int)Math.Round(stats.Shield), (int)Math.Round(stats.Armour)));
        // (Coriolis adds the 500 cr planetary approach suite, which is not in the outfitting screen's four lists.)
        Assert.Equal(millions, stats.Cost / 1e6, 0.006);
    }

    [Fact]
    public void A_build_engineered_in_the_designer_comes_out_as_Coriolis_works_it_out()
    {
        // Fixtures\loadouts\type9-design.json was made on the designer's page: an empty Type-9 hull given ten
        // engineered modules, each with an experimental effect, and saved. No module in it carries the game's
        // list of changed numbers, so everything rests on the blueprint table and the table of experimental
        // effects. Opened in coriolis.io (9 October 2026) through Studio's own hand-off it showed: dry mass
        // 1,394 t, cargo 512 t, fuel 64 t, 200 and 307 m/s, 23.57 and 24.73 MW, shield 935 MJ, armour 3,133,
        // cost 292.12 M; an FSD built for 2,902 t, a power plant giving 31.8 MW, a shield booster of +73.9%.
        JsonElement saved = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "loadouts", "type9-design.json")));
        ShipLoadout build = ShipLoadout.Parse(saved.GetProperty("loadout"));
        Assert.Equal(10, build.Modules.Count(module => module.Engineering is { Modifiers.Count: 0 }));
        BuildStats stats = BuildStats.Of(build)!;

        Assert.Empty(stats.Unknown);
        Assert.Equal((1394, 512, 64), (Math.Round(stats.UnladenMass), stats.CargoCapacity, stats.FuelCapacity));
        Assert.Equal((200, 307), (Math.Round(stats.Speed), Math.Round(stats.Boost)));
        Assert.Equal((31.75, 23.57, 24.73), (Math.Round(stats.PowerCapacity, 2), Math.Round(stats.PowerRetracted, 2), Math.Round(stats.PowerDeployed, 2)));
        Assert.Equal((935, 3133), (Math.Round(stats.Shield), Math.Round(stats.Armour)));
        Assert.Equal(292.12, stats.Cost / 1e6, 0.006);
        // Coriolis showed 35.73, 34.76 and 28.45 ly: it counts the reserve tank's 0.77 t, which the game's own best jump does not.
        Assert.Equal((35.74, 34.77, 28.46), (Math.Round(stats.JumpMax, 2), Math.Round(stats.JumpEmpty, 2), Math.Round(stats.JumpLaden, 2)));
        // what was saved with it is what the sums say
        Assert.Equal((build.UnladenMass, build.MaxJumpRange, build.CargoCapacity, build.FuelMain, build.HullValue + build.ModulesValue),
            (Math.Round(stats.UnladenMass, 6), Math.Round(stats.JumpMax, 6), stats.CargoCapacity, stats.FuelCapacity, stats.Cost));
    }

    [Fact]
    public void A_ship_too_heavy_for_its_thrusters_is_told_apart()
    {
        BuildStats fine = BuildStats.Of(Parse(Line(Anaconda)))!;
        Assert.False(fine.TooHeavy);
        Assert.True(fine.Speed > 0 && fine.Boost > fine.Speed);

        // Size 2 thrusters move 72 t at most; the Anaconda's hull alone weighs 400 t.
        BuildStats stuck = BuildStats.Of(Changed(Anaconda, modules => Module(modules, "MainEngines")["Item"] = "int_engine_size2_class1"))!;
        Assert.Equal(72, stuck.MaxMass);
        Assert.True(stuck.TooHeavy);
        Assert.Equal((0, 0), (stuck.Speed, stuck.Boost));
    }

    [Fact]
    public void Switched_off_modules_draw_nothing_and_weapons_only_when_deployed()
    {
        BuildStats on = BuildStats.Of(Parse(Line(Mandalay)))!;
        Assert.Equal(20.4, on.PowerCapacity);   // a 5A power plant
        double weapons = 4 * 0.46 + 2 * 0.39;   // four medium advanced multi-cannons, two small pulse lasers
        Assert.Equal(weapons + 0.2, on.PowerDeployed - on.PowerRetracted, 0.0001);   // and the countermeasure, which works through the hardpoints

        BuildStats off = BuildStats.Of(Changed(Mandalay, modules =>
        {
            Module(modules, "MediumHardpoint1")["On"] = false;
            Module(modules, "Slot03_Size4")["On"] = false;   // the 4B repair unit
            Module(modules, "CargoHatch")["On"] = false;
        }))!;
        Assert.Equal(on.PowerDeployed - on.PowerRetracted - 0.46, off.PowerDeployed - off.PowerRetracted, 0.0001);
        Assert.Equal(on.PowerRetracted - on.Modules["Slot03_Size4"].Power - 0.6, off.PowerRetracted, 0.0001);   // the hatch draws 0.6 MW
        Assert.Equal(on.UnladenMass, off.UnladenMass);   // a module weighs the same switched off
    }

    [Fact]
    public void A_build_costs_its_hull_and_modules_at_list_price()
    {
        // The journal gives what the player paid, discounts and all, so only single prices can be held against it:
        // the Type-8's 5A thrusters went for the table's 5,103,953 cr.
        Assert.Equal(5103953, ShipData.Find("int_engine_size5_class5")!.Cost);
        BuildStats stats = BuildStats.Of(Parse(Line(Mandalay)))!;
        Assert.Equal(16527192, stats.HullCost);
        Assert.Equal(stats.HullCost + stats.ModulesCost, stats.Cost);
        Assert.Equal(Parse(Line(Mandalay)).Modules.Where(module => ShipView.Place(module.Slot) is not null).Sum(module => ShipData.Find(module.Item)!.Cost), stats.ModulesCost);
    }

    [Fact]
    public void A_ship_or_module_the_tables_lack_is_said_not_guessed()
    {
        Assert.Null(BuildStats.Of(Parse("""{ "event":"Loadout", "Ship":"mediumtransport01", "Modules":[ ] }""")));   // the Lynx Highliner: named, but no numbers yet
        Assert.Null(DesignView.From(Parse("""{ "event":"Loadout", "Ship":"", "Modules":[ ] }""")));

        ShipLoadout odd = Changed(Mandalay, modules => Module(modules, "Slot07_Size2")["Item"] = "int_mkiilargebuggybay_size2_class3");
        BuildStats stats = BuildStats.Of(odd)!;
        Assert.Equal(new[] { "int_mkiilargebuggybay_size2_class3" }, stats.Unknown);
        Assert.Equal(BuildStats.Of(Parse(Line(Mandalay)))!.UnladenMass - 12, stats.UnladenMass, 0.0001);   // left out of the sums: the plain hangar weighs 12 t
    }
}
