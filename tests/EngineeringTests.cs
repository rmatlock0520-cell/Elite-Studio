using System.Text;
using System.Text.Json;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Loadouts;
using WebRequest = EliteStudio.App.WebRequest;     // System.Net has older types with the same names
using WebResponse = EliteStudio.App.WebResponse;

namespace EliteStudio.Tests;

/// <summary>
/// The engineering planner: the fixed facts it reads from the bundled coriolis-data, the sums over
/// the pinned blueprints, and the /api/loadouts/engineering/ routes. The commander is the owner's,
/// from the "Engineered" recording: the materials and the standing with the engineers are the ones
/// the game wrote on 8 October 2026, with the ship in HIP 12099.
/// </summary>
public sealed class EngineeringTests : IDisposable
{
    const string Engineered = "Journal.2026-10-08T080236.01.log";

    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioEngineering-").FullName;
    readonly PracticeGame game = new();
    readonly GameWatcher watcher;
    readonly string journal;

    public EngineeringTests()
    {
        journal = game.Journal(Engineered);
        watcher = game.Watch();
        watcher.Poll();
    }

    public void Dispose()
    {
        watcher.Dispose();
        game.Dispose();
        Directory.Delete(temp, recursive: true);
    }

    Settings SettingsFile() => new(Path.Combine(temp, "settings.json"));

    static string Line(PlannedMaterial m) => $"{m.Name} g{m.Grade} {m.Kind}: {m.Need} of {m.Have}";

    static string Line(MaterialTotal m) =>
        $"{m.Name}: {m.Need} of {m.Have}" + (m.Short > 0 ? $", short {m.Short}" : "") + (m.Swap is { } swap ? $", swap {swap.Give} {swap.Name} for {swap.Get}" : "");

    // ------------------------------------------------------------ the fixed facts

    [Fact]
    public void Every_kind_of_module_blueprint_material_and_engineer_in_the_data_is_known()
    {
        IReadOnlyList<EngineeringData.ModuleKind> kinds = EngineeringData.Kinds;

        Assert.Equal(43, kinds.Count);
        Assert.Equal(kinds.Count, kinds.Select(kind => kind.Code).Distinct().Count());   // the data lists one kind twice
        Assert.All(kinds, kind => Assert.NotEqual(kind.Code, kind.Name));   // a kind the name table lacks would show its short code
        Assert.Equal(kinds.Select(kind => kind.Name).Order(), kinds.Select(kind => kind.Name));
        Assert.DoesNotContain(kinds, kind => kind.Code == "cr");   // its one blueprint is offered by nobody
        foreach (EngineeringData.Grade grade in kinds.SelectMany(kind => kind.Blueprints).SelectMany(blueprint => blueprint.Grades))
        {
            Assert.InRange(grade.Number, 1, 5);
            Assert.NotEmpty(grade.Cost);
            Assert.All(grade.Cost.Keys, code => Assert.True(GameData.Materials.ContainsKey(code), code));
            Assert.NotEmpty(grade.Engineers);
            Assert.All(grade.Engineers, id => Assert.InRange(id, 300000, 300300));   // the ship engineers
        }
        Assert.All(kinds.SelectMany(kind => kind.Experimentals), effect => Assert.All(effect.Cost.Keys, code => Assert.True(GameData.Materials.ContainsKey(code), code)));
        Assert.Equal(GameData.Engineers.Select(engineer => engineer.Id).Order(), EngineeringData.Places.Keys.Order());   // all 38 have a place

        // One blueprint in full, as the game itself prices it.
        EngineeringData.ModuleKind drive = kinds.Single(kind => kind.Code == "fsd");
        Assert.Equal("Frame Shift Drive", drive.Name);
        Assert.Equal(new[] { "FSD_FastBoot", "FSD_LongRange", "FSD_Shielded" }, drive.Blueprints.Select(blueprint => blueprint.Code).Order());
        EngineeringData.Blueprint range = drive.Blueprints.Single(blueprint => blueprint.Code == "FSD_LongRange");
        Assert.Equal(("Increased Range", "FSD_LongRange", 5), (range.Name, range.Game, range.Grades.Count));
        Assert.Equal(new[] { ("DisruptedWakeEchoes", 1) }, range.Grades[0].Cost.Select(part => (part.Key, part.Value)));
        Assert.Equal(new[] { ("Arsenic", 1), ("ChemicalManipulators", 1), ("DataminedWake", 1) }, range.Grades[4].Cost.OrderBy(part => part.Key).Select(part => (part.Key, part.Value)));
        Assert.Equal(6, range.Grades[0].Engineers.Count);
        Assert.Equal(new long[] { 300100, 300160, 300280 }, range.Grades[4].Engineers.Order());   // Farseer, Martuuk, Brandon
        EngineeringData.Experimental stripped = drive.Experimentals.Single(effect => effect.Code == "special_fsd_lightweight");
        Assert.Equal("Stripped Down", stripped.Name);
        Assert.Equal(new[] { ("DisruptedWakeEchoes", 5), ("GalvanisingAlloys", 3), ("ProtoLightAlloys", 1) }, stripped.Cost.OrderBy(part => part.Key).Select(part => (part.Key, part.Value)));
        Assert.Equal(new EngineeringData.Place("Deciat", "Farseer Inc", 122.625, -0.8125, -47.28125), EngineeringData.Places[300100]);

        // A blueprint that stops at grade 4, and the data's own spelling slips put right.
        Assert.Equal(new[] { 1, 2, 3, 4 }, kinds.Single(kind => kind.Code == "scb").Blueprints.Single(b => b.Code == "ShieldCellBank_Rapid").Grades.Select(grade => grade.Number));
        EngineeringData.ModuleKind railGun = kinds.Single(kind => kind.Code == "rg");
        Assert.Contains("AdaptiveEncryptors", kinds.Single(kind => kind.Code == "pa").Experimentals.Single(effect => effect.Code == "special_lock_breaker").Cost.Keys);
        Assert.Contains(300260, railGun.Blueprints.SelectMany(blueprint => blueprint.Grades).SelectMany(grade => grade.Engineers));   // Tod "The Blaster" McQuinn
        Assert.Contains(300100, kinds.Single(kind => kind.Code == "s").Blueprints.SelectMany(blueprint => blueprint.Grades).SelectMany(grade => grade.Engineers));   // "Felicty" Farseer
        // An effect kept without a recipe is left out; so is the second listing of one effect under another code.
        Assert.DoesNotContain(railGun.Experimentals, effect => effect.Code == "special_feedback_cascade");
        Assert.Single(kinds.Single(kind => kind.Code == "tp").Experimentals, effect => effect.Name.Equals("Mass lock munition", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------ the sums

    [Fact]
    public void A_pinned_blueprint_is_costed_against_what_the_commander_holds_and_follows_the_game()
    {
        var planner = new EngineeringPlanner(watcher.State, SettingsFile());
        int told = 0;
        planner.Changed += () => told++;
        Assert.Equal((0, 0, true), (planner.View().Pins.Count, planner.View().Totals.Count, planner.View().MaterialsKnown));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, planner.View().Rolls);

        Assert.True(planner.Pin("fsd", "FSD_LongRange", 5, "special_fsd_lightweight"));
        Assert.Equal(1, told);
        EngineeringPlan plan = planner.View();
        PinView pin = Assert.Single(plan.Pins);

        // Grade 1 once, grade 2 twice ... grade 5 five times, and the effect once.
        Assert.Equal((1, "Frame Shift Drive", "Increased Range", 5, "Stripped Down"), (pin.Id, pin.Module, pin.Blueprint, pin.Grade, pin.Experimental));
        Assert.Equal(new[]
        {
            "Phosphorus g1 Raw: 3 of 203", "Arsenic g2 Raw: 5 of 9", "Manganese g2 Raw: 4 of 165",
            "Chemical Processors g2 Manufactured: 5 of 17", "Galvanising Alloys g2 Manufactured: 3 of 48", "Chemical Distillery g3 Manufactured: 4 of 15",
            "Chemical Manipulators g4 Manufactured: 5 of 6", "Proto Light Alloys g4 Manufactured: 1 of 124",
            "Atypical Disrupted Wake Echoes g1 Encoded: 8 of 223", "Strange Wake Solutions g3 Encoded: 3 of 13",
            "Eccentric Hyperspace Trajectories g4 Encoded: 4 of 0", "Datamined Wake Exceptions g5 Encoded: 5 of 0",
        }, pin.Materials.Select(Line));

        // Who offers grade 5, the one working for the commander first; distances are from HIP 12099.
        Assert.Equal(new[]
        {
            new PlannedEngineer(300160, "Elvira Martuuk", "Khun", "Long Sight Base", 173.2, "Unlocked", 4),
            new PlannedEngineer(300100, "Felicity Farseer", "Deciat", "Farseer Inc", 270.9, "Invited", 0),
        }, pin.Engineers.Take(2));
        Assert.Equal(("Mel Brandon", "Luchtaine", "The Brig", "Known"), (pin.Engineers[2].Name, pin.Engineers[2].System, pin.Engineers[2].Base, pin.Engineers[2].Progress));
        Assert.InRange(pin.Engineers[2].Ly!.Value, 22000, 22200);   // Colonia

        // Two wake scans are missing. 144 of the 226 grade 2 ones held buy the four of grade 4 (36 for 1, two
        // grades up); nothing held buys five of grade 5 as well.
        Assert.Equal(new[]
        {
            "Eccentric Hyperspace Trajectories: 4 of 0, short 4, swap 144 Anomalous FSD Telemetry for 4",
            "Datamined Wake Exceptions: 5 of 0, short 5",
        }, plan.Totals.Where(total => total.Short > 0).Select(Line));
        Assert.All(plan.Totals, total => Assert.Equal($"{total.Kind} material trader", total.Trader));
        Assert.Equal(12, plan.Totals.Count);

        // The same blueprint a second time doubles every need; the two pins are told apart by their numbers.
        Assert.True(planner.Pin("fsd", "FSD_LongRange", 5, ""));
        Assert.Equal(new[] { 1, 2 }, planner.View().Pins.Select(p => p.Id));
        Assert.Equal("Chemical Manipulators: 10 of 6, short 4, swap 2 Pharmaceutical Isolators for 6", Line(planner.View().Totals.Single(total => total.Code == "ChemicalManipulators")));
        planner.Unpin(2);
        Assert.Equal(3, told);

        // Fewer rolls planned: one of each grade. One grade 5 wake scan can be had for 216 of grade 2 (three
        // grades up, 6 x 6 x 6), which leaves the grade 4 gap to the grade 3 ones: 6 of the 10 that are spare.
        Assert.True(planner.SetRolls([1, 1, 1, 1, 1]));
        Assert.Equal("Datamined Wake Exceptions: 1 of 0, short 1, swap 216 Anomalous FSD Telemetry for 1", Line(planner.View().Totals.Single(total => total.Code == "DataminedWake")));
        Assert.Equal("Eccentric Hyperspace Trajectories: 1 of 0, short 1, swap 6 Strange Wake Solutions for 1", Line(planner.View().Totals.Single(total => total.Code == "HyperspaceTrajectories")));
        Assert.True(planner.SetRolls([1, 2, 3, 4, 5]));
        Assert.Equal(5, told);

        // The game goes on: the missing data is found, an engineer is unlocked, the ship jumps.
        game.Append(journal, """{ "timestamp":"2026-10-08T13:00:00Z", "event":"MaterialCollected", "Category":"Encoded", "Name":"dataminedwake", "Name_Localised":"Datamined Wake Exceptions", "Count":5 }""");
        watcher.Poll();
        Assert.Equal(6, told);
        Assert.Equal("Datamined Wake Exceptions: 5 of 5", Line(planner.View().Totals.Single(total => total.Code == "DataminedWake")));

        game.Append(journal, """{ "timestamp":"2026-10-08T13:01:00Z", "event":"EngineerProgress", "Engineer":"Felicity Farseer", "EngineerID":300100, "Progress":"Unlocked", "Rank":1 }""");
        watcher.Poll();
        Assert.Equal(7, told);
        Assert.Equal(("Unlocked", 1), (planner.View().Pins[0].Engineers[1].Progress, planner.View().Pins[0].Engineers[1].Rank));

        game.Append(journal, """{ "timestamp":"2026-10-08T13:02:00Z", "event":"FSDJump", "StarSystem":"Deciat", "SystemAddress":6681123623626, "StarPos":[122.625,-0.8125,-47.28125] }""");
        watcher.Poll();
        Assert.Equal(8, told);
        Assert.Equal(0, planner.View().Pins[0].Engineers[1].Ly);

        // A line that changes nothing the planner shows is not passed on.
        game.Append(journal, """{ "timestamp":"2026-10-08T13:03:00Z", "event":"Music", "MusicTrack":"Exploration" }""");
        watcher.Poll();
        Assert.Equal(8, told);

        // What no engineer offers is refused, and so are rolls that are not five sensible numbers.
        Assert.False(planner.Pin("fsd", "Engine_Dirty", 5, ""));
        Assert.False(planner.Pin("scb", "ShieldCellBank_Rapid", 5, ""));
        Assert.False(planner.Pin("fsd", "FSD_LongRange", 5, "special_engine_overloaded"));
        Assert.False(planner.Pin("no such kind", "FSD_LongRange", 5, ""));
        Assert.False(planner.SetRolls([1, 2, 3, 4]));
        Assert.False(planner.SetRolls([1, 2, 3, 4, 0]));
        Assert.False(planner.SetRolls([1, 2, 3, 4, 51]));
        Assert.Equal(8, told);
    }

    [Fact]
    public void Swaps_go_down_the_grades_first_and_never_count_on_the_same_materials_twice()
    {
        // A commander with none of the three chemicals the drive takes (grades 2, 3 and 4), some of the
        // rarest chemical (grade 5) and plenty of the commonest (grade 1).
        static string Holding(int rarest, int commonest) =>
            $$"""{ "timestamp":"2026-10-09T03:00:00Z", "event":"Materials", "Raw":[ { "Name":"phosphorus", "Count":300 }, { "Name":"manganese", "Count":250 }, { "Name":"arsenic", "Count":250 } ], "Manufactured":[ { "Name":"pharmaceuticalisolators", "Name_Localised":"Pharmaceutical Isolators", "Count":{{rarest}} }, { "Name":"chemicalstorageunits", "Name_Localised":"Chemical Storage Units", "Count":{{commonest}} } ], "Encoded":[ { "Name":"disruptedwakeechoes", "Count":300 }, { "Name":"wakesolutions", "Count":200 }, { "Name":"hyperspacetrajectories", "Count":150 }, { "Name":"dataminedwake", "Count":100 } ] }""";
        using var other = new PracticeGame();
        string file = other.Journal("Journal.2026-10-09T030000.01.log", [Holding(rarest: 3, commonest: 300)]);
        using GameWatcher watching = other.Watch();
        watching.Poll();
        var planner = new EngineeringPlanner(watching.State, SettingsFile());

        planner.Pin("fsd", "FSD_LongRange", 5, "");

        EngineeringPlan plan = planner.View();
        Assert.Equal(new[]
        {
            // 30 of the commonest for 5, six for one, one grade up.
            "Chemical Processors: 5 of 0, short 5, swap 30 Chemical Storage Units for 5",
            // The last of the rarest, one for nine, two grades down. Grades 2 and 4 are nearer, but they are short themselves.
            "Chemical Distillery: 4 of 0, short 4, swap 1 Pharmaceutical Isolators for 9",
            // Two of the rarest, one for three: more than was missing comes back.
            "Chemical Manipulators: 5 of 0, short 5, swap 2 Pharmaceutical Isolators for 6",
        }, plan.Totals.Where(total => total.Short > 0).Select(Line));
        Assert.Null(plan.Pins[0].Engineers[0].Ly);   // this journal never said where the ship is
        Assert.All(plan.Pins[0].Engineers, engineer => Assert.Equal(("", 0), (engineer.Progress, engineer.Rank)));   // nor named an engineer

        // Less of both. The grade 4 gap takes both of the rarest; the grade 3 gap then has to come from the
        // commonest, 36 for one, which takes 144 of the 150. The grade 2 gap would need 30 of those and finds 6.
        other.Append(file, Holding(rarest: 2, commonest: 150));
        watching.Poll();
        Assert.Equal(new[]
        {
            "Chemical Processors: 5 of 0, short 5",
            "Chemical Distillery: 4 of 0, short 4, swap 144 Chemical Storage Units for 4",
            "Chemical Manipulators: 5 of 0, short 5, swap 2 Pharmaceutical Isolators for 6",
        }, planner.View().Totals.Where(total => total.Short > 0).Select(Line));

        // Nothing spare at all: no swap is offered, and nothing is traded away that the pins themselves take.
        other.Append(file, Holding(rarest: 0, commonest: 0).Replace("\"Count\":100", "\"Count\":5"));   // exactly the five grade 5 wake scans the drive takes
        watching.Poll();
        Assert.All(planner.View().Totals, total => Assert.Null(total.Swap));
    }

    [Fact]
    public void Every_blueprint_there_is_can_be_pinned_and_no_swap_promises_a_material_twice()
    {
        var planner = new EngineeringPlanner(watcher.State, SettingsFile());
        foreach (EngineeringData.ModuleKind kind in EngineeringData.Kinds)
            foreach (EngineeringData.Blueprint blueprint in kind.Blueprints)
                Assert.True(planner.Pin(kind.Code, blueprint.Code, blueprint.Grades[^1].Number, kind.Experimentals.FirstOrDefault()?.Code ?? ""), blueprint.Code);

        EngineeringPlan plan = planner.View();

        Assert.Equal(EngineeringData.Kinds.Sum(kind => kind.Blueprints.Count), plan.Pins.Count);
        Assert.All(plan.Pins, pin => Assert.True(pin.Materials.Count > 0 && pin.Engineers.Count > 0, pin.Blueprint));
        Assert.All(plan.Totals, total => Assert.Equal(Math.Max(0, total.Need - total.Have), total.Short));
        // With this much pinned nearly everything is short, and the owner's stores still cover some of it by trading.
        MaterialTotal[] swapped = [.. plan.Totals.Where(total => total.Swap is not null)];
        Assert.NotEmpty(swapped);
        Assert.All(swapped, total => Assert.True(total.Short > 0 && total.Swap!.Get >= total.Short && total.Swap.Grade != total.Grade));
        // Whatever the swaps give away, taken together, was spare: held beyond what the pins themselves take.
        Assert.All(swapped.GroupBy(total => total.Swap!.Code), given =>
        {
            int held = watcher.State.Materials.GetValueOrDefault(given.Key);
            int taken = plan.Totals.FirstOrDefault(total => total.Code == given.Key)?.Need ?? 0;
            Assert.True(given.Sum(total => total.Swap!.Give) <= held - taken, given.Key);
        });
    }

    [Fact]
    public void Pins_and_rolls_are_kept_across_a_restart_and_what_cannot_be_used_is_dropped()
    {
        var planner = new EngineeringPlanner(watcher.State, SettingsFile());
        planner.Pin("fsd", "FSD_LongRange", 3, "");
        planner.Pin("sb", "ShieldBooster_HeavyDuty", 5, "special_shieldbooster_chunky");
        planner.SetRolls([2, 2, 2, 2, 8]);

        EngineeringPlan again = new EngineeringPlanner(watcher.State, SettingsFile()).View();
        Assert.Equal(new[] { (1, "Frame Shift Drive", 3, null), (2, "Shield Booster", 5, "Super Capacitors") }, again.Pins.Select(pin => (pin.Id, pin.Module, pin.Grade, pin.Experimental)));
        Assert.Equal(new[] { 2, 2, 2, 2, 8 }, again.Rolls);
        Assert.Equal(JsonSerializer.Serialize(planner.View()), JsonSerializer.Serialize(again));

        // A settings file from elsewhere: a blueprint the tables do not have, a grade nobody offers, a pin left empty, rolls that make no sense.
        File.WriteAllText(Path.Combine(temp, "settings.json"), """
            { "engineering": { "pins": [
                { "id": 4, "module": "fsd", "blueprint": "FSD_Gone", "grade": 5, "experimental": "" },
                { "id": 5, "module": "scb", "blueprint": "ShieldCellBank_Rapid", "grade": 5, "experimental": "" },
                null,
                { "id": 7, "module": "t" },
                { "id": 9, "module": "t", "blueprint": "Engine_Dirty", "grade": 5, "experimental": "special_engine_overloaded" } ],
              "rolls": [ 1, 2, 3 ] } }
            """);
        EngineeringPlan kept = new EngineeringPlanner(watcher.State, SettingsFile()).View();
        Assert.Equal(new[] { (9, "Thrusters", "Dirty Drive Tuning", "Drag Drives") }, kept.Pins.Select(pin => (pin.Id, pin.Module, pin.Blueprint, pin.Experimental!)));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, kept.Rolls);

        // No materials list from the game yet (a journal that begins without one): the page is told so.
        using var bare = new PracticeGame();
        bare.Journal("Journal.2026-10-09T030000.01.log", [PracticeGame.Line(PracticeGame.Recording(Engineered), "Fileheader")]);
        using GameWatcher watching = bare.Watch();
        watching.Poll();
        Assert.False(new EngineeringPlanner(watching.State, SettingsFile()).View().MaterialsKnown);
    }

    // ------------------------------------------------------------ the /api/loadouts/engineering/ routes

    [Fact]
    public async Task Routes_list_what_can_be_pinned_and_pin_unpin_and_set_the_rolls()
    {
        using var web = new WebServer(temp);
        EngineeringApi.Map(web, watcher.State, SettingsFile());
        async Task<(int Status, JsonElement Json)> Call(string method, string route, string body = "")
        {
            WebResponse answer = await web.Handle(new WebRequest(method, "/api/loadouts/engineering/" + route, new Dictionary<string, string>(), body));
            return (answer.Status, JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(answer.Body)));
        }

        var (status, json) = await Call("GET", "blueprints");
        Assert.Equal((200, 43), (status, json.GetProperty("kinds").GetArrayLength()));
        JsonElement drive = json.GetProperty("kinds").EnumerateArray().Single(kind => kind.GetProperty("code").GetString() == "fsd");
        Assert.Equal(
            """{"code":"fsd","name":"Frame Shift Drive","blueprints":[{"code":"FSD_FastBoot","name":"Faster Boot Sequence","grades":[1,2,3,4,5]},{"code":"FSD_LongRange","name":"Increased Range","grades":[1,2,3,4,5]},{"code":"FSD_Shielded","name":"Shielded","grades":[1,2,3,4,5]}],"experimentals":[{"code":"special_fsd_fuelcapacity","name":"Deep Charge"},{"code":"special_fsd_heavy","name":"Mass Manager"},{"code":"special_fsd_toughened","name":"Double Braced"},{"code":"special_fsd_lightweight","name":"Stripped Down"},{"code":"special_fsd_cooled","name":"Thermal Spread"}]}""",
            JsonSerializer.Serialize(drive));

        (status, json) = await Call("GET", "plan");
        Assert.Equal((200, """{"rolls":[1,2,3,4,5],"pins":[],"totals":[],"materialsKnown":true}"""), (status, JsonSerializer.Serialize(json)));

        (status, json) = await Call("POST", "pin", """{"module":"fsd","blueprint":"FSD_LongRange","grade":1,"experimental":""}""");
        Assert.Equal(200, status);
        JsonElement pin = json.GetProperty("pins")[0];
        Assert.Equal(
            """{"id":1,"module":"Frame Shift Drive","blueprint":"Increased Range","grade":1,"experimental":null}""",
            JsonSerializer.Serialize(new { id = pin.GetProperty("id"), module = pin.GetProperty("module"), blueprint = pin.GetProperty("blueprint"), grade = pin.GetProperty("grade"), experimental = pin.GetProperty("experimental") }));
        Assert.Equal(
            """[{"code":"DisruptedWakeEchoes","name":"Atypical Disrupted Wake Echoes","kind":"Encoded","grade":1,"need":1,"have":223}]""",
            JsonSerializer.Serialize(pin.GetProperty("materials")));
        Assert.Equal(6, pin.GetProperty("engineers").GetArrayLength());
        Assert.Equal(
            """{"id":300160,"name":"Elvira Martuuk","system":"Khun","base":"Long Sight Base","ly":173.2,"progress":"Unlocked","rank":4}""",
            JsonSerializer.Serialize(pin.GetProperty("engineers")[0]));
        Assert.Equal(
            """[{"code":"DisruptedWakeEchoes","name":"Atypical Disrupted Wake Echoes","kind":"Encoded","grade":1,"need":1,"have":223,"short":0,"trader":"Encoded material trader","swap":null}]""",
            JsonSerializer.Serialize(json.GetProperty("totals")));

        (status, json) = await Call("POST", "pin", """{"module":"fsd","blueprint":"FSD_LongRange","grade":9}""");
        Assert.Equal((400, "Choose a module, a blueprint and a grade that an engineer offers."), (status, json.GetProperty("error").GetString()));
        (status, json) = await Call("POST", "rolls", """{"rolls":[1,2]}""");
        Assert.Equal((400, "Give a number of rolls from 1 to 50 for each of the five grades."), (status, json.GetProperty("error").GetString()));

        (status, json) = await Call("POST", "rolls", """{"rolls":[4,2,3,4,5]}""");
        Assert.Equal((200, 4), (status, json.GetProperty("totals")[0].GetProperty("need").GetInt32()));
        (status, json) = await Call("POST", "unpin", """{"id":1}""");
        Assert.Equal((200, 0), (status, json.GetProperty("pins").GetArrayLength()));
        (_, json) = await Call("GET", "plan");
        Assert.Equal("""{"rolls":[4,2,3,4,5],"pins":[],"totals":[],"materialsKnown":true}""", JsonSerializer.Serialize(json));
    }
}
