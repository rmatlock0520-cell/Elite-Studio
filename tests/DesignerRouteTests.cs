using System.Text.Json;
using System.Text.Json.Nodes;

namespace EliteStudio.Tests;

/// <summary>
/// The ship designer's routes (/api/loadouts/designer/...), on the practice data folder and the
/// recordings that the rest of <see cref="LoadoutsTests"/> uses: what goes on the bench, how a
/// changed build is worked out again, saved and handed on, and what the page is told about slots
/// and engineers. The tables and sums behind the routes have their own tests in DesignerTests.cs.
/// </summary>
public sealed partial class LoadoutsTests
{
    static int[] Slots(JsonElement design) => [.. design.GetProperty("groups").EnumerateArray().Select(list => list.GetProperty("slots").GetArrayLength())];

    static JsonObject ModuleIn(JsonNode loadout, string slot) =>
        loadout["Modules"]!.AsArray().Single(module => module!["Slot"]!.GetValue<string>() == slot)!.AsObject();

    [Fact]
    public async Task The_designer_starts_from_the_current_ship_a_saved_build_or_an_empty_hull()
    {
        Assert.StartsWith("No ship is known yet.", await Refused(409, "GET", "designer/start"));
        string journal = game.Journal(ShipSwap, []);
        Fly(Engineered, journal);

        // The ship being flown: the journal's own Loadout line goes on the bench, with the picture of it.
        JsonElement bench = await Ok("GET", "designer/start");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(LoadoutLine(Engineered)), JsonNode.Parse(bench.GetProperty("loadout").GetRawText())));
        Assert.Equal("", bench.GetProperty("name").GetString());
        JsonElement design = bench.GetProperty("design");
        Assert.Equal(("mandalay", "Mandalay"), (design.GetProperty("ship").GetString(), design.GetProperty("shipType").GetString()));
        Assert.Equal(new[] { "Hardpoints", "Utility mounts", "Core internals", "Optional internals" },
            design.GetProperty("groups").EnumerateArray().Select(list => list.GetProperty("name").GetString()));
        Assert.Equal(new[] { 6, 4, 8, 10 }, Slots(design));
        JsonElement stats = design.GetProperty("stats");
        Assert.Equal((388.6, 56.97, 54.01, 50.93), (Math.Round(stats.GetProperty("unladenMass").GetDouble(), 2), Math.Round(stats.GetProperty("jumpMax").GetDouble(), 2),
            Math.Round(stats.GetProperty("jumpEmpty").GetDouble(), 2), Math.Round(stats.GetProperty("jumpLaden").GetDouble(), 2)));
        Assert.False(stats.GetProperty("tooHeavy").GetBoolean());
        JsonElement drive = design.GetProperty("groups")[2].GetProperty("slots")[3];
        Assert.Equal(("Frame Shift Drive", "FrameShiftDrive", 5), (drive.GetProperty("slot").GetString(), drive.GetProperty("slotId").GetString(), drive.GetProperty("size").GetInt32()));
        Assert.Equal(("Frame Shift Drive (SCO)", "A", "Increased Range"), (drive.GetProperty("module").GetProperty("name").GetString(),
            drive.GetProperty("module").GetProperty("rating").GetString(), drive.GetProperty("module").GetProperty("engineering").GetProperty("blueprint").GetString()));
        Assert.Equal((21.6, 0.654), (Math.Round(stats.GetProperty("modules").GetProperty("FrameShiftDrive").GetProperty("mass").GetDouble(), 4),
            stats.GetProperty("modules").GetProperty("FrameShiftDrive").GetProperty("power").GetDouble()));

        // A saved build, whatever is flown now.
        await Ok("POST", "save", new { name = "Explorer" });
        Fly(ShipSwap, journal);
        bench = await Ok("GET", "designer/start?id=Explorer");
        Assert.Equal(("Explorer", "mandalay"), (bench.GetProperty("name").GetString(), bench.GetProperty("design").GetProperty("ship").GetString()));
        Assert.Equal("type8", (await Ok("GET", "designer/start")).GetProperty("design").GetProperty("ship").GetString());
        Assert.Equal("That saved build was not found.", await Refused(404, "GET", "designer/start?id=Nothing"));

        // An empty hull: armour, the seven core modules at their plainest in full size, and the cargo hatch.
        bench = await Ok("GET", "designer/start?ship=type9");
        Assert.Equal(new[]
        {
            "Armour type9_armour_grade1", "PowerPlant int_powerplant_size6_class1", "MainEngines int_engine_size7_class1",
            "FrameShiftDrive int_hyperdrive_size6_class1", "LifeSupport int_lifesupport_size5_class1",
            "PowerDistributor int_powerdistributor_size6_class1", "Radar int_sensors_size4_class1", "FuelTank int_fueltank_size6_class3",
            "CargoHatch modularcargobaydoor",
        }, bench.GetProperty("loadout").GetProperty("Modules").EnumerateArray().Select(module => $"{module.GetProperty("Slot").GetString()} {module.GetProperty("Item").GetString()}"));
        Assert.Equal(("Loadout", "type9", "2026-10-09T12:02:00Z"), (bench.GetProperty("loadout").GetProperty("event").GetString(),
            bench.GetProperty("loadout").GetProperty("Ship").GetString(), bench.GetProperty("loadout").GetProperty("timestamp").GetString()));
        design = bench.GetProperty("design");
        Assert.Equal(new[] { 5, 4, 8, 11 }, Slots(design));
        Assert.All(design.GetProperty("groups")[3].GetProperty("slots").EnumerateArray(), slot => Assert.Equal(JsonValueKind.Null, slot.GetProperty("module").ValueKind));
        Assert.Equal("Slot00_Size8", design.GetProperty("groups")[3].GetProperty("slots")[0].GetProperty("slotId").GetString());   // the Type-9 counts from 0
        stats = design.GetProperty("stats");
        Assert.Equal((0, 64.0, 0.0), (stats.GetProperty("cargoCapacity").GetInt32(), stats.GetProperty("fuelCapacity").GetDouble(), stats.GetProperty("shield").GetDouble()));
        Assert.True(stats.GetProperty("jumpMax").GetDouble() > 9 && stats.GetProperty("powerRetracted").GetDouble() < stats.GetProperty("powerCapacity").GetDouble());

        // Every hull of the table can be asked for; a ship without numbers is refused in plain words.
        JsonElement[] ships = [.. (await Ok("GET", "designer/ships")).GetProperty("ships").EnumerateArray()];
        Assert.Equal(47, ships.Length);
        Assert.Equal(("adder", "Adder"), (ships[0].GetProperty("ship").GetString(), ships[0].GetProperty("name").GetString()));
        Assert.Contains(ships, ship => ship.GetProperty("ship").GetString() == "smallcombat01_nx" && ship.GetProperty("name").GetString() == "Kestrel Mk II");
        foreach (JsonElement ship in ships) await Ok("GET", "designer/start?ship=" + ship.GetProperty("ship").GetString());
        Assert.StartsWith("Studio has no numbers for this ship yet", await Refused(404, "GET", "designer/start?ship=mediumtransport01"));
    }

    [Fact]
    public async Task A_build_changed_on_the_bench_is_worked_out_again_and_saved_as_the_game_would_write_it()
    {
        Fly(Engineered, game.Journal(ShipSwap, []));
        JsonNode loadout = JsonNode.Parse((await Ok("GET", "designer/start")).GetProperty("loadout").GetRawText())!;

        // What the page does to its copy: the drive is planned at grade 5 with Mass Manager (the game's own list of
        // changed numbers goes), the size 6 slot gets a rack of its size, and the countermeasure is taken out.
        ModuleIn(loadout, "FrameShiftDrive")["Engineering"] = new JsonObject
        {
            ["BlueprintName"] = "FSD_LongRange", ["Level"] = 5, ["Quality"] = 1,
            ["ExperimentalEffect"] = "special_fsd_heavy", ["ExperimentalEffect_Localised"] = "Mass Manager",
        };
        JsonArray modules = loadout["Modules"]!.AsArray();
        modules[modules.IndexOf(ModuleIn(loadout, "Slot01_Size6"))] = new JsonObject { ["Slot"] = "Slot01_Size6", ["Item"] = "int_cargorack_size6_class1", ["On"] = true, ["Priority"] = 0, ["Health"] = 1 };
        modules.Remove(ModuleIn(loadout, "TinyHardpoint4"));

        JsonElement design = await Ok("POST", "designer/view", new { loadout });
        JsonElement stats = design.GetProperty("stats");
        // 388.6 t, less the old drive's 21.6 t and the countermeasure's 1.3 t, plus the new drive's 26 t; the hold doubles.
        // The drive is now built for 1,175 t x 1.55 x 1.04 = 1,894.1 t: 11.5363 x 1,894.1 / (391.7 + 5.2) + 10.5 ly from the booster.
        Assert.Equal((391.7, 64, 65.55), (Math.Round(stats.GetProperty("unladenMass").GetDouble(), 4), stats.GetProperty("cargoCapacity").GetInt32(), Math.Round(stats.GetProperty("jumpMax").GetDouble(), 2)));
        Assert.Equal(new[] { 6, 4, 8, 10 }, Slots(design));   // the emptied utility mount is still listed
        Assert.Equal(JsonValueKind.Null, design.GetProperty("groups")[1].GetProperty("slots")[3].GetProperty("module").ValueKind);
        JsonElement planned = design.GetProperty("groups")[2].GetProperty("slots")[3].GetProperty("module").GetProperty("engineering");
        Assert.Equal(("Increased Range", 5, "Mass Manager", "", 0), (planned.GetProperty("blueprint").GetString(), planned.GetProperty("grade").GetInt32(),
            planned.GetProperty("experimental").GetString(), planned.GetProperty("engineer").GetString(), planned.GetProperty("changes").GetArrayLength()));

        // Saved: listed with the saved builds, with the numbers the game would have put at the top of its Loadout line.
        Assert.Equal("Far explorer", (await Ok("POST", "designer/save", new { name = " Far explorer ", loadout })).GetProperty("id").GetString());
        JsonElement listed = (await Ok("GET", "saved")).GetProperty("saved")[0];
        Assert.Equal(("Far explorer", "Mandalay"), (listed.GetProperty("name").GetString(), listed.GetProperty("shipType").GetString()));
        JsonElement ship = (await Ok("GET", "saved/one?id=Far%20explorer")).GetProperty("ship");
        Assert.Equal((391.7, 64, 32.0, 65.553776), (ship.GetProperty("unladenMass").GetDouble(), ship.GetProperty("cargoCapacity").GetInt32(),
            ship.GetProperty("fuelMain").GetDouble(), ship.GetProperty("jumpMax").GetDouble()));
        long cost = stats.GetProperty("hullCost").GetInt64() + stats.GetProperty("modulesCost").GetInt64();
        Assert.Equal((cost, cost / 20), (ship.GetProperty("hullValue").GetInt64() + ship.GetProperty("modulesValue").GetInt64(), ship.GetProperty("rebuy").GetInt64()));
        Assert.Equal((62.1, 55.3), (ship.GetProperty("jumpEmpty").GetDouble(), ship.GetProperty("jumpLaden").GetDouble()));   // "My ship" works these out from the saved numbers
        Assert.Equal((62.07, 55.3), (Math.Round(stats.GetProperty("jumpEmpty").GetDouble(), 2), Math.Round(stats.GetProperty("jumpLaden").GetDouble(), 2)));   // as the designer showed them

        // Handed to EDSY and Coriolis as a Loadout event like any other. A module planned on the bench carries blueprint,
        // grade, a finished roll and its effect, and no list of changed numbers: both sites then roll the blueprint
        // themselves (Coriolis only does so when the list is absent). A module left alone keeps the game's own list.
        string link = (await Ok("GET", "export?to=coriolis&id=Far%20explorer")).GetProperty("url").GetString()!;
        JsonNode sent = JsonNode.Parse(Unpack(link["https://coriolis.io/import?data=".Length..]))!;
        Assert.Equal(("Loadout", "mandalay", "2026-10-09T12:01:00Z"), (sent["event"]!.GetValue<string>(), sent["Ship"]!.GetValue<string>(), sent["timestamp"]!.GetValue<string>()));
        Assert.Equal(new[] { "BlueprintName", "Level", "Quality", "ExperimentalEffect", "ExperimentalEffect_Localised" },
            ModuleIn(sent, "FrameShiftDrive")["Engineering"]!.AsObject().Select(field => field.Key));
        Assert.Equal(3, ModuleIn(sent, "MainEngines")["Engineering"]!["Modifiers"]!.AsArray().Count);
        Assert.Equal((391.7, 65.553776, 64, 32.0, 0.52), (sent["UnladenMass"]!.GetValue<double>(), sent["MaxJumpRange"]!.GetValue<double>(),
            sent["CargoCapacity"]!.GetValue<int>(), sent["FuelCapacity"]!["Main"]!.GetValue<double>(), sent["FuelCapacity"]!["Reserve"]!.GetValue<double>()));
        JsonNode slef = JsonNode.Parse((await Ok("GET", "export?to=slef&id=Far%20explorer")).GetProperty("text").GetString()!)![0]!["data"]!;
        Assert.True(JsonNode.DeepEquals(sent["Modules"], slef["Modules"]));

        // The same rules as for any saved build, and no saving of a ship the tables cannot work out.
        Assert.Contains("already have a saved build called \"Far explorer\"", await Refused(409, "POST", "designer/save", new { name = "Far explorer", loadout }));
        Assert.StartsWith("Please give the build a name", await Refused(400, "POST", "designer/save", new { name = "", loadout }));
        loadout["Ship"] = "mediumtransport01";
        Assert.StartsWith("Studio has no numbers for this ship yet", await Refused(400, "POST", "designer/save", new { name = "Liner", loadout }));
        Assert.StartsWith("Studio has no numbers for this ship yet", await Refused(400, "POST", "designer/view", new { loadout }));
        Assert.StartsWith("Studio has no numbers for this ship yet", await Refused(400, "POST", "designer/view"));
    }

    [Fact]
    public async Task The_designer_lists_what_fits_a_slot_and_what_engineers_offer()
    {
        // Kinds in the alphabet; within a kind the biggest first.
        JsonElement[] kinds = [.. (await Ok("GET", "designer/fits?ship=mandalay&slot=Slot02_Size5")).GetProperty("kinds").EnumerateArray()];
        string[] names = [.. kinds.Select(kind => kind.GetProperty("name").GetString()!)];
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.Contains("Cargo Rack", names);
        Assert.DoesNotContain("Vessel Hangar", names);
        JsonElement boosters = kinds.Single(kind => kind.GetProperty("name").GetString() == "Guardian FSD Booster").GetProperty("modules");
        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, boosters.EnumerateArray().Select(module => module.GetProperty("size").GetInt32()));
        Assert.Equal(("int_guardianfsdbooster_size5", "H", "", 1.3, 2.14, 6483101L), (boosters[0].GetProperty("item").GetString(), boosters[0].GetProperty("rating").GetString(),
            boosters[0].GetProperty("mount").GetString(), boosters[0].GetProperty("mass").GetDouble(), boosters[0].GetProperty("power").GetDouble(), boosters[0].GetProperty("cost").GetInt64()));

        // Weapons say how they are mounted; the one the names table lists twice is under its plain name.
        kinds = [.. (await Ok("GET", "designer/fits?ship=mandalay&slot=MediumHardpoint1")).GetProperty("kinds").EnumerateArray()];
        JsonElement cannons = kinds.Single(kind => kind.GetProperty("name").GetString() == "Multi-Cannon").GetProperty("modules");
        Assert.Equal(new[] { "2E Fixed", "2F Gimballed", "2F Turreted", "1F Fixed", "1G Gimballed", "1G Turreted" },
            cannons.EnumerateArray().Select(module => $"{module.GetProperty("size").GetInt32()}{module.GetProperty("rating").GetString()} {module.GetProperty("mount").GetString()}"));
        Assert.DoesNotContain(kinds, kind => kind.GetProperty("name").GetString()!.Contains("Rapid Phase"));
        Assert.StartsWith("Studio's tables do not have that slot", await Refused(404, "GET", "designer/fits?ship=mandalay&slot=HugeHardpoint1"));
        Assert.StartsWith("Studio's tables do not have that slot", await Refused(404, "GET", "designer/fits?ship=nothing&slot=PowerPlant"));

        // Blueprints by the game's code and name, with the grades on offer, and the experimental effects.
        JsonElement offers = await Ok("GET", "designer/engineering?item=Int_Engine_Size5_Class5");
        Assert.Equal(new[] { "Engine_Dirty = Dirty Drive Tuning 1-5", "Engine_Reinforced = Drive Strengthening 1-5", "Engine_Tuned = Clean Drive Tuning 1-5" },
            offers.GetProperty("blueprints").EnumerateArray().Select(b => $"{b.GetProperty("code").GetString()} = {b.GetProperty("name").GetString()} {b.GetProperty("grades")[0]}-{b.GetProperty("grades")[4]}"));
        Assert.Equal(new[] { "Thermal Spread", "Drive Distributors", "Drag Drives", "Double Braced", "Stripped Down" },
            offers.GetProperty("experimentals").EnumerateArray().Select(effect => effect.GetProperty("name").GetString()));
        Assert.Equal("special_engine_overloaded", offers.GetProperty("experimentals")[2].GetProperty("code").GetString());
        offers = await Ok("GET", "designer/engineering?item=int_shieldcellbank_size5_class5");
        Assert.Equal(4, offers.GetProperty("blueprints")[0].GetProperty("grades").GetArrayLength());   // shield cell banks stop at grade 4
        foreach (string untouched in new[] { "int_guardianfsdbooster_size5", "int_newthing_size2_class3", "" })
        {
            offers = await Ok("GET", "designer/engineering?item=" + untouched);
            Assert.Equal((0, 0), (offers.GetProperty("blueprints").GetArrayLength(), offers.GetProperty("experimentals").GetArrayLength()));
        }
    }
}
