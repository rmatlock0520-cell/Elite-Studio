using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Loadouts;

namespace EliteStudio.Tests;

/// <summary>
/// Studio Loadouts against the recordings of real play in Fixtures\journals: the ship view, the
/// readable names, saved builds on a practice data folder, and the hand-off to EDSY and Coriolis.
/// Nothing here goes to the internet or to the real Recycle Bin. The numbers quoted for the
/// engineered Mandalay's link are the ones measured on the owner's own journal before the port.
/// </summary>
public sealed partial class LoadoutsTests : IDisposable
{
    const string Anaconda = "Journal.2026-09-24T191106.01.log";      // a damaged Anaconda: every size of hardpoint, a military slot
    const string Kestrel = "Journal.2026-09-30T231247.01.log";       // Kestrel Mk II with two modules newer than most tools know
    const string ShipSwap = "Journal.2026-10-05T015030.01.log";      // Krait Mk II swapped for the Type-8
    const string Engineered = "Journal.2026-10-08T080236.01.log";    // the engineered Mandalay with a Guardian booster

    readonly string temp = Directory.CreateTempSubdirectory("studio-loadouts-").FullName;
    readonly PracticeGame game = new();
    readonly GameWatcher watcher;
    readonly WebServer web;
    readonly List<string> recycled = [];
    DateTime clock = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    public LoadoutsTests()
    {
        watcher = game.Watch();
        web = new WebServer(Path.Combine(temp, "web"));
        new StudioLoadouts(Builds)
        {
            Recycle = file => { recycled.Add(Path.GetFileName(file)); File.Delete(file); },
            Now = () => clock = clock.AddMinutes(1),
        }.Map(web, watcher.State);
    }

    public void Dispose()
    {
        web.Dispose();
        watcher.Dispose();
        game.Dispose();
        Directory.Delete(temp, recursive: true);
    }

    string Builds => Path.Combine(temp, "loadouts");

    static string LoadoutLine(string recording) => PracticeGame.Recording(recording).Last(line => line.Contains("\"event\":\"Loadout\""));

    static ShipView View(string recording)
    {
        using JsonDocument line = JsonDocument.Parse(LoadoutLine(recording));
        return ShipView.From(ShipLoadout.Parse(line.RootElement));
    }

    /// <summary>The game writes a Loadout: the ship is now the one of that recording.</summary>
    void Fly(string recording, string journal)
    {
        game.Append(journal, LoadoutLine(recording));
        watcher.Poll();
    }

    async Task<(int Status, JsonElement Json)> Call(string method, string route, object? body = null)
    {
        string[] parts = route.Split('?', 2);
        WebResponse response = await web.Handle(new WebRequest(method, "/api/loadouts/" + parts[0],
            WebServer.ParseQuery(parts.Length == 2 ? parts[1] : ""), body is null ? "" : JsonSerializer.Serialize(body)));
        return (response.Status, JsonSerializer.Deserialize<JsonElement>(response.Body));
    }

    /// <summary>A call that must succeed.</summary>
    async Task<JsonElement> Ok(string method, string route, object? body = null)
    {
        var (status, json) = await Call(method, route, body);
        Assert.True(status == 200, $"{method} {route} answered {status}: {json}");
        return json;
    }

    /// <summary>A call that must be refused with that status; gives the reason shown to the player.</summary>
    async Task<string> Refused(int expected, string method, string route, object? body = null)
    {
        var (status, json) = await Call(method, route, body);
        Assert.True(status == expected, $"{method} {route} answered {status}: {json}");
        Assert.False(json.GetProperty("ok").GetBoolean());
        return json.GetProperty("error").GetString()!;
    }

    static string[] Lines(ShipView ship, string group) =>
    [
        .. ship.Groups.Single(g => g.Name == group).Modules.Select(m =>
        {
            string fitted = string.Join(' ', new[] { $"{(m.Size > 0 ? m.Size : "")}{m.Rating}", m.Name }.Where(text => text.Length > 0));
            string work = m.Engineering is { } e ? $" | {e.Blueprint} G{e.Grade}{(e.Experimental.Length > 0 ? " + " + e.Experimental : "")}" : "";
            return $"{m.Slot} | {fitted}{(m.Mount.Length > 0 ? ", " + m.Mount : "")} | P{m.Priority}{work}";
        }),
    ];

    // ------------------------------------------------------------ readable names

    [Theory]
    // weapons: the name, how it is mounted, size and rating as the outfitting screen shows them
    [InlineData("hpt_pulselaser_gimbal_medium", "Pulse Laser", "Gimballed", 2, "F")]
    [InlineData("hpt_pulselaser_gimbal_huge", "Pulse Laser", "Gimballed", 4, "A")]
    [InlineData("hpt_pulselaser_fixed_small", "Pulse Laser", "Fixed", 1, "F")]
    [InlineData("hpt_pulselaserburst_turret_small", "Burst Laser", "Turreted", 1, "G")]
    [InlineData("hpt_beamlaser_turret_medium", "Beam Laser", "Turreted", 2, "E")]
    [InlineData("hpt_cannon_gimbal_medium", "Cannon", "Gimballed", 2, "D")]
    [InlineData("hpt_multicannon_fixed_medium_advanced", "Advanced Multi-Cannon", "Fixed", 2, "E")]
    [InlineData("hpt_basicmissilerack_fixed_small", "Seeker Missile Rack", "Fixed", 1, "B")]
    [InlineData("hpt_mkiiplasmashockautocannon_fixed_large", "Mk II Plasma Shock Accelerator", "Fixed", 3, "B")]
    [InlineData("hpt_mining_subsurfdispmisle_fixed_medium", "Sub-Surface Displacement Missile", "Fixed", 2, "B")]
    [InlineData("Hpt_MultiCannon_Fixed_Small_Strong", "Enforcer Cannon", "Fixed", 1, "F")]
    [InlineData("hpt_guardian_gausscannon_fixed_medium", "Guardian Gauss Cannon", "Fixed", 2, "B")]
    [InlineData("hpt_atmulticannon_turret_large", "AX Multi-Cannon", "Turreted", 3, "E")]
    // utility mounts: no mount, size 0
    [InlineData("hpt_heatsinklauncher_turret_tiny", "Heat Sink Launcher", "", 0, "I")]
    [InlineData("hpt_chafflauncher_tiny", "Chaff Launcher", "", 0, "I")]
    [InlineData("hpt_electroniccountermeasure_tiny", "Electronic Countermeasure", "", 0, "F")]
    [InlineData("hpt_plasmapointdefence_turret_tiny", "Point Defence", "", 0, "I")]
    [InlineData("hpt_shieldbooster_size0_class5", "Shield Booster", "", 0, "A")]
    [InlineData("hpt_crimescanner_size0_class4", "Kill Warrant Scanner", "", 0, "B")]
    [InlineData("hpt_mrascanner_size0_class4", "Pulse Wave Analyser", "", 0, "B")]
    // core internals
    [InlineData("mandalay_armour_grade1", "Lightweight Alloy", "", 1, "I")]
    [InlineData("Federation_Corvette_Armour_Reactive", "Reactive Surface Composite", "", 1, "I")]
    [InlineData("int_powerplant_size5_class5", "Power Plant", "", 5, "A")]
    [InlineData("int_guardianpowerplant_size5", "Guardian Hybrid Power Plant", "", 5, "A")]
    [InlineData("int_engine_size5_class5", "Thrusters", "", 5, "A")]
    [InlineData("int_engine_size3_class5_fast", "Enhanced Performance Thrusters", "", 3, "A")]
    [InlineData("int_mkiiagileboost_engine_size5_class5", "Mk II Agile Boost Thrusters", "", 5, "A")]   // the table's mount column says "mount" here
    [InlineData("int_hyperdrive_size3_class1", "Frame Shift Drive", "", 3, "E")]
    [InlineData("Int_Hyperdrive_Overcharge_Size5_Class5", "Frame Shift Drive (SCO)", "", 5, "A")]
    [InlineData("Int_Hyperdrive_Overcharge_Size8_Class5_OverchargeBooster_MkII", "Mk II Supercharge Optimised Frame Shift Drive (SCO)", "", 8, "A")]
    [InlineData("int_lifesupport_size4_class1", "Life Support", "", 4, "E")]
    [InlineData("int_sensors_size5_class1", "Sensors", "", 5, "E")]
    [InlineData("int_fueltank_size5_class3", "Fuel Tank", "", 5, "C")]
    // optional internals, with the ratings no code spells out
    [InlineData("int_guardianfsdbooster_size5", "Guardian FSD Booster", "", 5, "H")]
    [InlineData("int_cargorack_size7_class1", "Cargo Rack", "", 7, "E")]
    [InlineData("int_shieldgenerator_size5_class3_fast", "Bi-Weave Shield Generator", "", 5, "C")]
    [InlineData("int_shieldgenerator_size5_class5_strong", "Prismatic Shield Generator", "", 5, "A")]
    [InlineData("int_repairer_size4_class4", "Auto Field-Maintenance Unit", "", 4, "B")]
    [InlineData("int_buggybay_size2_class1", "Planetary Vehicle Hangar", "", 2, "H")]
    [InlineData("int_fighterbay_size7_class1", "Vessel Hangar", "", 7, "D")]
    [InlineData("Int_MkIILargeBuggyBay_Size2_Class3", "Mk II Large Planetary Vehicle Hangar", "", 2, "F")]
    [InlineData("int_passengercabin_size5_class4", "Luxury Class Passenger Cabin", "", 5, "B")]
    [InlineData("int_dronecontrol_repair_size3_class5", "Repair Limpet Controller", "", 3, "A")]
    [InlineData("int_multidronecontrol_operations_size3_class4", "Operations Multi Limpet Controller", "", 3, "B")]
    [InlineData("int_dockingcomputer_advanced", "Advanced Docking Computer", "", 1, "E")]
    [InlineData("int_supercruiseassist", "Supercruise Assist", "", 1, "E")]
    // The everyday modules the table lists a second time as a pre-engineered special edition (a "Double
    // Screaming Fragment Cannon"): they are known by their plain row.
    [InlineData("hpt_pulselaserburst_gimbal_medium", "Burst Laser", "Gimballed", 2, "F")]
    [InlineData("hpt_beamlaser_fixed_huge", "Beam Laser", "Fixed", 4, "A")]
    [InlineData("hpt_cannon_fixed_huge", "Cannon", "Fixed", 4, "B")]
    [InlineData("hpt_slugshot_gimbal_large", "Fragment Cannon", "Gimballed", 3, "C")]
    [InlineData("hpt_multicannon_fixed_medium", "Multi-Cannon", "Fixed", 2, "E")]
    [InlineData("hpt_railgun_fixed_medium", "Rail Gun", "Fixed", 2, "B")]
    [InlineData("hpt_basicmissilerack_fixed_large", "Seeker Missile Rack", "Fixed", 3, "A")]
    [InlineData("hpt_causticmissile_fixed_medium", "Enzyme Missile Rack", "Fixed", 2, "B")]
    [InlineData("hpt_mininglaser_fixed_small", "Mining Laser", "Fixed", 1, "D")]
    [InlineData("hpt_mining_abrblstr_fixed_small", "Abrasion Blaster", "Fixed", 1, "D")]
    [InlineData("int_powerdistributor_size5_class5", "Power Distributor", "", 5, "A")]
    [InlineData("int_cargorack_size6_class1", "Cargo Rack", "", 6, "E")]
    [InlineData("int_modulereinforcement_size5_class2", "Module Reinforcement Package", "", 5, "D")]
    [InlineData("int_detailedsurfacescanner_tiny", "Detailed Surface Scanner", "", 1, "I")]
    // a code no table knows is still shown, as its own words; its size and rating are then read from the code by Core
    [InlineData("int_newthing_size2_class3", "Newthing", "", 0, "")]
    public void A_module_code_reads_as_the_game_names_the_module(string code, string name, string mount, int size, string rating) =>
        Assert.Equal((name, mount, size, rating), Names.Module(code));

    [Theory]
    [InlineData("FSD_LongRange", "Increased Range")]
    [InlineData("Engine_Reinforced", "Drive Strengthening")]
    [InlineData("Engine_Dirty", "Dirty Drive Tuning")]
    [InlineData("PowerDistributor_PriorityWeapons", "Weapon Focused")]
    [InlineData("ShieldGenerator_Optimised", "Enhanced Low Power")]
    [InlineData("ShieldBooster_Thermic", "Thermal Resistant")]
    [InlineData("Armour_Advanced", "Lightweight")]
    [InlineData("Sensor_Sensor_LightWeight", "Lightweight")]
    [InlineData("Weapon_Overcharged", "Overcharged")]
    [InlineData("Weapon_Efficient", "Efficient")]
    [InlineData("Weapon_LongRange", "Long Range")]
    [InlineData("Weapon_RapidFire", "Rapid Fire")]
    public void A_blueprint_code_reads_as_the_engineer_names_it(string code, string name) => Assert.Equal(name, Names.Blueprint(code));

    [Fact]
    public void Changed_numbers_read_as_words()
    {
        Assert.Equal("FSD Optimal Mass", Names.Words("FSDOptimalMass"));
        Assert.Equal("Damage Per Second", Names.Words("DamagePerSecond"));
        Assert.Equal("Shield Gen Optimal Mass", Names.Words("ShieldGenOptimalMass"));
        Assert.Equal("Integrity", Names.Words("Integrity"));
    }

    [Fact]
    public void Every_module_in_the_recordings_gets_a_name_made_of_proper_words()
    {
        var odd = new SortedSet<string>();
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "journals"), "*.log"))
        {
            foreach (string line in File.ReadLines(file).Where(line => line.Contains("\"event\":\"Loadout\"")))
            {
                using JsonDocument loadout = JsonDocument.Parse(line);
                foreach (ModuleView module in ShipView.From(ShipLoadout.Parse(loadout.RootElement)).Groups.SelectMany(g => g.Modules))
                    if (!ProperWords().IsMatch(module.Name)) odd.Add($"{module.Item} -> {module.Name}");
            }
        }
        Assert.Empty(odd);
    }

    // Words with a capital and at most 14 letters (a code that was not taken apart is one long word), "II", "FSD" and "(SCO)".
    [GeneratedRegex(@"^(\(SCO\)|II|FSD|[A-Z][a-z]{1,13}(-[A-Z][a-z]{1,13})?)( (\(SCO\)|II|FSD|[A-Z][a-z]{1,13}(-[A-Z][a-z]{1,13})?))*$")]
    private static partial Regex ProperWords();

    // ------------------------------------------------------------ the ship view

    [Fact]
    public void The_engineered_Mandalay_is_shown_as_the_outfitting_screen_lists_it()
    {
        ShipView ship = View(Engineered);

        Assert.Equal(("Mandalay", "", "SE-06M"), (ship.ShipType, ship.ShipName, ship.ShipIdent));
        Assert.Equal((17639220L, 24979632L, 2130942L, 1.0), (ship.HullValue, ship.ModulesValue, ship.Rebuy, ship.HullHealth));
        Assert.Equal((32, 32.0, 388.600006), (ship.CargoCapacity, ship.FuelMain, ship.UnladenMass));
        Assert.Equal((54.0, 50.9, 56.968765), (ship.JumpEmpty, ship.JumpLaden, ship.JumpMax));
        Assert.Equal(new[] { "Hardpoints", "Utility mounts", "Core internals", "Optional internals" }, ship.Groups.Select(g => g.Name));

        Assert.Equal(new[]
        {
            "Medium 1 | 2E Advanced Multi-Cannon, Fixed | P2",
            "Medium 2 | 2E Advanced Multi-Cannon, Fixed | P2",
            "Medium 3 | 2E Advanced Multi-Cannon, Fixed | P2",
            "Medium 4 | 2E Advanced Multi-Cannon, Fixed | P2",
            "Small 1 | 1F Pulse Laser, Fixed | P1 | Overcharged G1",
            "Small 2 | 1F Pulse Laser, Fixed | P1 | Efficient G2 + Scramble Spectrum",
        }, Lines(ship, "Hardpoints"));
        Assert.Equal(new[]
        {
            "Utility 1 | I Point Defence | P2",
            "Utility 2 | I Heat Sink Launcher | P1",
            "Utility 3 | I Chaff Launcher | P1",
            "Utility 4 | F Electronic Countermeasure | P1",
        }, Lines(ship, "Utility mounts"));
        Assert.Equal(new[]
        {
            "Bulkheads | 1I Lightweight Alloy | P2",
            "Power Plant | 5A Power Plant | P2",
            "Thrusters | 5A Thrusters | P1 | Drive Strengthening G1",
            "Frame Shift Drive | 5A Frame Shift Drive (SCO) | P1 | Increased Range G3 + Stripped Down",
            "Life Support | 4E Life Support | P1",
            "Power Distributor | 5A Power Distributor | P1 | Weapon Focused G1 + Flow Control",
            "Sensors | 5E Sensors | P1",
            "Fuel Tank | 5C Fuel Tank | P2",
        }, Lines(ship, "Core internals"));
        Assert.Equal(new[]
        {
            "Size 6 | 5E Cargo Rack | P2",
            "Size 5 | 5H Guardian FSD Booster | P4",
            "Size 4 | 4B Auto Field-Maintenance Unit | P1",
            "Size 4 | 3A Shield Generator | P1 | Enhanced Low Power G3 + Fast Charge",
            "Size 3 | 3A Fuel Scoop | P4",
            "Size 3 | 3B Operations Multi Limpet Controller | P1",
            "Size 2 | 2H Planetary Vehicle Hangar | P1",   // ratings like this one are in no module code: they come from the module table
            "Size 1 | 1I Detailed Surface Scanner | P1",
            "Size 1 | 1E Advanced Docking Computer | P4",
            "Size 1 | 1E Supercruise Assist | P4",
        }, Lines(ship, "Optional internals"));

        // Paint, voice, cockpit and cargo hatch are not part of a build.
        Assert.Equal(28, ship.Groups.Sum(g => g.Modules.Count));

        ModuleView drive = ship.Groups[2].Modules.Single(m => m.SlotId == "FrameShiftDrive");
        Assert.Equal(("int_hyperdrive_overcharge_size5_class5", true, 1.0), (drive.Item, drive.On, drive.Health));
        Assert.Equal(("Elvira Martuuk", 1.0), (drive.Engineering!.Engineer, drive.Engineering.Quality));
        Assert.Equal(new[]
        {
            new ChangeView("Mass", 21.599998, 20, true, ""),
            new ChangeView("Integrity", 109.199997, 120, false, ""),
            new ChangeView("Power Draw", 0.654, 0.6, true, ""),
            new ChangeView("FSD Optimal Mass", 1586.25, 1175, false, ""),
        }, drive.Engineering.Changes);
    }

    [Fact]
    public void Hardpoints_are_listed_biggest_first_and_damage_is_kept()
    {
        ShipView conda = View(Anaconda);

        Assert.Equal(("Anaconda", 0.972802, 17.0, 16.6), (conda.ShipType, conda.HullHealth, conda.JumpEmpty, conda.JumpLaden));
        Assert.Equal(new[]
        {
            "Huge 1 | 4A Pulse Laser, Gimballed | P1",
            "Large 1 | 3C Fragment Cannon, Turreted | P1",
            "Large 2 | 3C Fragment Cannon, Turreted | P1",
            "Large 3 | 3C Fragment Cannon, Turreted | P1",
            "Medium 1 | 2D Cannon, Gimballed | P1",
            "Medium 2 | 2D Cannon, Gimballed | P1",
            "Small 1 | 1G Burst Laser, Turreted | P1",
            "Small 2 | 1G Burst Laser, Turreted | P1",
        }, Lines(conda, "Hardpoints"));
        Assert.Equal("Utility 2 | A Kill Warrant Scanner | P1", Lines(conda, "Utility mounts")[1]);
        Assert.Equal(0.921748, conda.Groups[2].Modules.Single(m => m.Slot == "Power Plant").Health);

        string[] optional = Lines(conda, "Optional internals");
        Assert.Equal("Size 7 | 7D Vessel Hangar | P2", optional[0]);
        Assert.Equal("Size 4 | 3A Repair Limpet Controller | P1", optional[8]);   // a size 3 module in a size 4 slot
        Assert.Equal("Military | 5D Hull Reinforcement Package | P2", optional[^1]);

        // The Kestrel Mk II's own modules, which came out after most tools' lists were written.
        ShipView kestrel = View(Kestrel);
        Assert.Equal("Kestrel Mk II", kestrel.ShipType);
        Assert.Equal("Large 1 | 3B Mk II Plasma Shock Accelerator, Fixed | P2", Lines(kestrel, "Hardpoints")[0]);
        Assert.Equal("Thrusters | 5A Mk II Agile Boost Thrusters | P1", Lines(kestrel, "Core internals")[2]);
        Assert.Equal("Military | 4A Shield Cell Bank | P2", Lines(kestrel, "Optional internals")[^1]);
    }

    // ------------------------------------------------------------ the ship being flown

    [Fact]
    public async Task The_current_ship_follows_the_journal()
    {
        JsonElement answer = await Ok("GET", "ship");
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (answer.GetProperty("ship").ValueKind, answer.GetProperty("reportedUtc").ValueKind));
        Assert.StartsWith("No ship is known yet.", await Refused(409, "POST", "save", new { name = "Too early" }));
        Assert.StartsWith("No ship is known yet.", await Refused(409, "GET", "export?to=edsy"));

        // Catching up with a journal: the last Loadout in it is the ship (here the Type-8 the Krait was swapped for),
        // and its time goes with it, so the page can say how old the picture is.
        string journal = game.Journal(ShipSwap);
        watcher.Poll();
        answer = await Ok("GET", "ship");
        Assert.Equal("2026-10-05T08:56:16Z", answer.GetProperty("reportedUtc").GetString());
        JsonElement ship = answer.GetProperty("ship");
        Assert.Equal("Type-8 Transporter", ship.GetProperty("shipType").GetString());
        Assert.Equal("SE-13T", ship.GetProperty("shipIdent").GetString());
        Assert.Equal(352, ship.GetProperty("cargoCapacity").GetInt32());
        // the same numbers the status strip shows
        Assert.Equal((watcher.State.JumpEmpty, watcher.State.JumpLaden), (ship.GetProperty("jumpEmpty").GetDouble(), ship.GetProperty("jumpLaden").GetDouble()));
        Assert.Equal((25.7, 15.4), (watcher.State.JumpEmpty, watcher.State.JumpLaden));

        // The player boards another ship while Studio runs.
        Fly(Engineered, journal);
        ship = (await Ok("GET", "ship")).GetProperty("ship");
        Assert.Equal("Mandalay", ship.GetProperty("shipType").GetString());
        JsonElement drive = ship.GetProperty("groups")[2].GetProperty("modules")[3];
        Assert.Equal("Frame Shift Drive (SCO)", drive.GetProperty("name").GetString());
        Assert.Equal("Increased Range", drive.GetProperty("engineering").GetProperty("blueprint").GetString());

        // A fighter or SRV reporting in does not replace the ship.
        game.Append(journal, """{ "timestamp":"2026-10-08T13:00:00Z", "event":"Loadout", "Ship":"TestBuggy", "ShipID":1, "Modules":[ ] }""");
        watcher.Poll();
        answer = await Ok("GET", "ship");
        Assert.Equal("Mandalay", answer.GetProperty("ship").GetProperty("shipType").GetString());
        Assert.Equal("2026-10-08T12:05:01Z", answer.GetProperty("reportedUtc").GetString());
    }

    [Fact]
    public async Task A_new_ship_is_pushed_to_the_open_pages()
    {
        Assert.True(web.Start(47840));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using HttpResponseMessage response = await http.GetAsync($"http://127.0.0.1:{web.Port}/api/events?t={web.Token}",
            HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        using var events = new StreamReader(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken), Encoding.UTF8);
        Task<string?> NextLine() => events.ReadLineAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(": connected", await NextLine());
        Assert.Equal("", await NextLine());

        Fly(Engineered, game.Journal(ShipSwap, []));

        Assert.Equal("event: loadouts", await NextLine());
        JsonElement pushed = JsonSerializer.Deserialize<JsonElement>((await NextLine())!["data: ".Length..]);
        Assert.Equal("Mandalay", pushed.GetProperty("ship").GetProperty("shipType").GetString());
        Assert.Equal(4, pushed.GetProperty("ship").GetProperty("groups").GetArrayLength());
        Assert.Equal("2026-10-08T12:05:01Z", pushed.GetProperty("reportedUtc").GetString());
    }

    // ------------------------------------------------------------ saved builds

    [Fact]
    public async Task Builds_are_saved_listed_renamed_and_deleted()
    {
        string journal = game.Journal(ShipSwap, []);
        Fly(Engineered, journal);

        Assert.Empty((await Ok("GET", "saved")).GetProperty("saved").EnumerateArray());
        Assert.Equal("Explorer", (await Ok("POST", "save", new { name = "  Explorer " })).GetProperty("id").GetString());
        Assert.True(File.Exists(Path.Combine(Builds, "Explorer.json")));
        // Windows file names ignore capitals, so "explorer" is the same file there (on Linux it would be another build).
        if (OperatingSystem.IsWindows())
            Assert.Contains("already have a saved build called \"explorer\"", await Refused(409, "POST", "save", new { name = "explorer" }));
        foreach (string unusable in new[] { "", "   ", "...", "CON", "nul.old", new string('x', 81) })
            Assert.StartsWith("Please give the build a name", await Refused(400, "POST", "save", new { name = unusable }));
        // A name Windows cannot use as a file name is kept as typed; only the file is named differently.
        Assert.Equal("To Colonia- 54 ly-", (await Ok("POST", "save", new { name = "To Colonia: 54 ly?" })).GetProperty("id").GetString());

        Fly(ShipSwap, journal);
        await Ok("POST", "save", new { name = "Hauler" });

        string Listed(JsonElement answer) => string.Join("; ", answer.GetProperty("saved").EnumerateArray().Select(b =>
            $"{b.GetProperty("id").GetString()} = {b.GetProperty("name").GetString()} ({b.GetProperty("shipType").GetString()})"));
        Assert.Equal("Hauler = Hauler (Type-8 Transporter); To Colonia- 54 ly- = To Colonia: 54 ly? (Mandalay); Explorer = Explorer (Mandalay)",
            Listed(await Ok("GET", "saved")));   // newest first

        // A build that another program has open (antivirus, a backup tool) cannot be read just now:
        // the others are still listed, and asking for that one is refused instead of made up.
        using (new FileStream(Path.Combine(Builds, "Hauler.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal("To Colonia- 54 ly- = To Colonia: 54 ly? (Mandalay); Explorer = Explorer (Mandalay)", Listed(await Ok("GET", "saved")));
            await Refused(500, "GET", "saved/one?id=Hauler");
        }

        // A saved build keeps its own ship whatever is flown now.
        JsonElement explorer = await Ok("GET", "saved/one?id=Explorer");
        Assert.Equal("Explorer", explorer.GetProperty("name").GetString());
        Assert.Equal(new DateTime(2026, 10, 9, 12, 1, 0, DateTimeKind.Utc), explorer.GetProperty("savedUtc").GetDateTime());
        Assert.Equal("Mandalay", explorer.GetProperty("ship").GetProperty("shipType").GetString());
        Assert.Equal(54.0, explorer.GetProperty("ship").GetProperty("jumpEmpty").GetDouble());
        Assert.Equal("Type-8 Transporter", (await Ok("GET", "ship")).GetProperty("ship").GetProperty("shipType").GetString());

        // Renaming moves the file; the build and the time it was saved stay.
        Assert.Equal("Deep space", (await Ok("POST", "rename", new { id = "Explorer", name = "Deep space" })).GetProperty("id").GetString());
        Assert.False(File.Exists(Path.Combine(Builds, "Explorer.json")));
        Assert.Equal("That saved build was not found.", await Refused(404, "GET", "saved/one?id=Explorer"));
        JsonElement renamed = await Ok("GET", "saved/one?id=Deep%20space");
        Assert.Equal(("Deep space", explorer.GetProperty("savedUtc").GetDateTime()), (renamed.GetProperty("name").GetString(), renamed.GetProperty("savedUtc").GetDateTime()));
        Assert.Equal(explorer.GetProperty("ship").GetRawText(), renamed.GetProperty("ship").GetRawText());
        Assert.Contains("already have a saved build called \"Hauler\"", await Refused(409, "POST", "rename", new { id = "Deep space", name = "Hauler" }));
        Assert.Equal("That saved build was not found.", await Refused(404, "POST", "rename", new { id = "Explorer", name = "Again" }));
        // Only the capitals change: the same file to Windows, and allowed.
        Assert.Equal("DEEP space", (await Ok("POST", "rename", new { id = "Deep space", name = "DEEP space" })).GetProperty("id").GetString());
        Assert.Contains("DEEP space.json", Directory.GetFiles(Builds).Select(Path.GetFileName));

        // Files that are not builds are neither listed nor touched.
        File.WriteAllText(Path.Combine(Builds, "Broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(Builds, "Other.json"), """{"name":"Other","loadout":"nothing"}""");
        Assert.Equal(3, (await Ok("GET", "saved")).GetProperty("saved").GetArrayLength());
        Assert.Equal("That saved build was not found.", await Refused(404, "GET", "saved/one?id=Broken"));

        // Deleting goes through the Recycle Bin; ids can never point outside the builds folder.
        File.WriteAllText(Path.Combine(temp, "settings.json"), "{}");
        await Ok("POST", "delete", new { id = "Hauler" });
        Assert.Equal("That saved build was not found.", await Refused(404, "POST", "delete", new { id = "Hauler" }));
        Assert.Equal("That saved build was not found.", await Refused(404, "POST", "delete", new { id = @"..\settings" }));
        Assert.Equal("That saved build was not found.", await Refused(404, "GET", @"saved/one?id=..%5Csettings"));
        Assert.Equal(new[] { "Hauler.json" }, recycled);
        Assert.True(File.Exists(Path.Combine(temp, "settings.json")));
        Assert.Equal(new[] { "Broken.json", "DEEP space.json", "Other.json", "To Colonia- 54 ly-.json" },
            Directory.GetFiles(Builds).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    // ------------------------------------------------------------ hand-off to EDSY and Coriolis

    /// <summary>What EDSY and Coriolis do with the text after "I=" or "data=": undo the URL escaping, base64, then gzip.</summary>
    static string Unpack(string encoded)
    {
        Assert.Matches("^[A-Za-z0-9_-]+(%3D){0,2}$", encoded);   // no "/" for EDSY to cut at, no bare "=" for Coriolis to cut at
        byte[] packed = Convert.FromBase64String(Uri.UnescapeDataString(encoded).Replace('-', '+').Replace('_', '/'));
        Assert.Equal(new byte[] { 0x1f, 0x8b, 0x08, 0x00 }, packed[..4]);   // a gzip file, not bare deflate
        using var unzip = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress);
        return new StreamReader(unzip, Encoding.UTF8).ReadToEnd();
    }

    [Fact]
    public void Packing_for_a_link_comes_back_unchanged_and_is_as_long_as_measured()
    {
        string line = LoadoutLine(Engineered);
        Assert.Equal(9006, Encoding.UTF8.GetByteCount(line));   // the owner's newest Loadout, as measured for the notes

        string encoded = HandOff.Encode(line);
        Assert.Equal(line, Unpack(encoded));
        // Measured before the port: 2,612 characters, so 2,633 for EDSY and 2,644 for Coriolis with their
        // addresses in front. The exact count depends on the compressor's version; the size class does not.
        Assert.InRange(encoded.Length, 2500, 2650);
        Assert.Equal((2633 - 2612, 2644 - 2612), (HandOff.Edsy.Length, HandOff.Coriolis.Length));
        Assert.Equal("", HandOff.Encode(""));   // nothing in, nothing out, as in EDMarketConnector
        Assert.Equal("Müller’s “Ship” + more / = ?", Unpack(HandOff.Encode("Müller’s “Ship” + more / = ?")));
    }

    [Fact]
    public async Task A_build_is_handed_over_as_the_games_own_loadout_event()
    {
        string journal = game.Journal(ShipSwap, []);
        Fly(Engineered, journal);
        JsonNode written = JsonNode.Parse(LoadoutLine(Engineered))!;

        string edsy = (await Ok("GET", "export?to=edsy")).GetProperty("url").GetString()!;
        string coriolis = (await Ok("GET", "export?to=coriolis&id=")).GetProperty("url").GetString()!;
        Assert.StartsWith("https://edsy.org/#/I=", edsy);
        Assert.StartsWith("https://coriolis.io/import?data=", coriolis);
        Assert.Equal(edsy["https://edsy.org/#/I=".Length..], coriolis["https://coriolis.io/import?data=".Length..]);

        string sent = Unpack(edsy["https://edsy.org/#/I=".Length..]);
        Assert.True(JsonNode.DeepEquals(written, JsonNode.Parse(sent)), "the link does not carry the journal's Loadout event");
        Assert.StartsWith("{\"timestamp\":\"2026-10-08T12:05:01Z\",\"event\":\"Loadout\",\"Ship\":\"mandalay\",", sent);   // EDSY reads nothing without "event"
        Assert.Contains("\"OriginalValue\":1175.000000,", sent);   // numbers go out exactly as the game wrote them
        // Past what Windows' "open this link" call accepts, which is why the page goes through /loadouts/?open=.
        Assert.InRange(edsy.Length, 2084, 2650);

        // A saved build gives the same link as the ship did, also after the player has moved to another ship.
        await Ok("POST", "save", new { name = "Explorer" });
        Fly(ShipSwap, journal);
        Assert.Equal(edsy, (await Ok("GET", "export?to=edsy&id=Explorer")).GetProperty("url").GetString());
        Assert.NotEqual(edsy, (await Ok("GET", "export?to=edsy")).GetProperty("url").GetString());

        // SLEF: a list with one build; the event's own fields without the ones that are about this journal line.
        JsonNode slef = JsonNode.Parse((await Ok("GET", "export?to=slef&id=Explorer")).GetProperty("text").GetString()!)!;
        JsonNode build = Assert.Single(slef.AsArray())!;
        Assert.Equal("Elite Studio", build["header"]!["appName"]!.GetValue<string>());
        Assert.Matches(@"^\d+\.\d+\.\d+$", build["header"]!["appVersion"]!.GetValue<string>());
        JsonObject data = build["data"]!.AsObject();
        Assert.Equal(new[] { "FuelCapacity", "Ship", "ShipName", "ShipIdent", "HullValue", "ModulesValue", "Rebuy", "MaxJumpRange", "UnladenMass", "CargoCapacity", "Modules" },
            data.Select(field => field.Key));
        foreach (var (name, value) in data) Assert.True(JsonNode.DeepEquals(written[name], value), name);

        Assert.StartsWith("A build can be exported to", await Refused(400, "GET", "export?to=inara"));
        Assert.Equal("That saved build was not found.", await Refused(404, "GET", "export?to=edsy&id=Nothing"));
    }
}
