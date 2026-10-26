using System.Text;
using EliteStudio.Core;

namespace EliteStudio.Tests;

/// <summary>
/// A practice copy of the game's folders under the temp folder, so nothing here touches the
/// owner's real game files. Every write moves the file's time one second on: the watcher goes by
/// file times, and a test must not depend on how fast the disk's clock ticks.
/// </summary>
sealed class PracticeGame : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "EliteStudioTest-" + Guid.NewGuid().ToString("N"));
    DateTime clock = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

    public PracticeGame() => Directory.CreateDirectory(JournalDir);

    public string JournalDir => Path.Combine(root, "Journals");
    public string OptionsDir => Path.Combine(root, "Options");
    public string BindingsDir => Path.Combine(OptionsDir, "Bindings");

    public GameWatcher Watch() => new(JournalDir, BindingsDir);

    public static string[] Recording(string name) => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "journals", name));

    /// <summary>The first line of that event in a recording.</summary>
    public static string Line(string[] recording, string eventName) => recording.First(line => line.Contains($"\"event\":\"{eventName}\""));

    public string Journal(string name) => Journal(name, Recording(name));

    public string Journal(string name, IEnumerable<string> lines) => Write(Path.Combine(JournalDir, name), GameLines(lines));

    public void Append(string file, params string[] lines) => AppendBytes(file, Encoding.UTF8.GetBytes(GameLines(lines)));

    public void AppendBytes(string file, byte[] bytes)
    {
        using (var stream = new FileStream(file, FileMode.Append)) stream.Write(bytes);
        Touch(file);
    }

    public string Write(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text, new UTF8Encoding(false));
        return Touch(file);
    }

    /// <summary>Copies one of the sample files in Fixtures\gamefiles into a practice folder.</summary>
    public string Copy(string sample, string toDir)
    {
        Directory.CreateDirectory(toDir);
        string file = Path.Combine(toDir, sample);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gamefiles", sample), file, overwrite: true);
        return Touch(file);
    }

    public string Touch(string file)
    {
        File.SetLastWriteTimeUtc(file, clock = clock.AddSeconds(1));
        return file;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    // the game ends every line with CR LF
    static string GameLines(IEnumerable<string> lines) => string.Concat(lines.Select(line => line + "\r\n"));
}

/// <summary>
/// The core against recordings of real play (Fixtures\journals). The recordings are the owner's
/// journals with the commander's name and id replaced and chat, friends, wing, squadron,
/// other-player and hired-crew lines taken out; three are cut down to the stretch that matters,
/// as noted below.
/// The samples in Fixtures\gamefiles: the key bindings and display settings are the owner's files
/// as found; Status, Cargo, Market and NavRoute are written in the game's exact layout from values
/// in the recordings, because the owner's own copies were in their empty "game closed" form.
/// </summary>
public partial class CoreTests
{
    const string OnFoot = "Journal.2025-04-17T013240.01.log";       // game 4.1: leaves the ship in a station and boards again (whole)
    const string Replot = "Journal.2026-09-24T191106.01.log";       // the game plots a route again without saying NavRoute (whole)
    const string Exploring = "Journal.2026-09-30T231247.01.log";    // a system's bodies scanned; ship destroyed twice (whole)
    const string ShipSwap = "Journal.2026-10-05T015030.01.log";     // Krait Mk II swapped for the Type-8 (opening lines, then the swap)
    const string Hauling = "Journal.2026-10-05T152112.01.log";      // trade round trips in the Type-8 (whole)
    const string Unfinished = "Journal.2026-10-06T040304.01.log";   // no Shutdown: stops in the middle of a plotted route (whole)
    const string Booster = "Journal.2026-10-08T050849.01.log";      // Mandalay with a Guardian booster flies a five-jump route (first part)
    const string Engineered = "Journal.2026-10-08T080236.01.log";   // engineered Mandalay maps two bodies and lands (first part)

    static DateTime Utc(int year, int month, int day, int hour, int minute, int second) =>
        new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    [Fact]
    public void Hauling_recording_follows_stations_and_prices_paid()
    {
        using var game = new PracticeGame();
        game.Journal(Hauling);
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        var moments = new List<string>();
        state.Journal += line =>
        {
            if (line.Event is "FSDJump" or "Docked" or "Undocked") moments.Add($"{line.Event}: {state.StarSystem} / {state.Station ?? "-"}");
            if (line.Event == "MarketBuy") moments.Add($"paid: {line.Data.GetProperty("Type")} {state.PaidFor(line.Data.GetProperty("Type").GetString()!)}");
        };

        watcher.Poll();

        Assert.Equal(new[]
        {
            "paid: bauxite 2956", "Undocked: Arima / -", "FSDJump: LHS 3583 / -", "Docked: LHS 3583 / Antoniodeandrade Beacon",
            "paid: polymers 380", "Undocked: LHS 3583 / -", "FSDJump: Arima / -", "Docked: Arima / Kent's Legacy",
            "paid: silver 34663", "Undocked: Arima / -", "FSDJump: Nanabozho / -", "Docked: Nanabozho / Lamarck Orbital",
            "paid: polymers 61", "Undocked: Nanabozho / -", "FSDJump: Arima / -", "Docked: Arima / Kent's Legacy",
            "paid: silver 34663", "Undocked: Arima / -", "FSDJump: Nanabozho / -", "Docked: Nanabozho / Lamarck Orbital",
        }, moments);

        GameSnapshot now = state.Snapshot();
        Assert.Equal("CMDR Test", now.Commander);
        Assert.Equal("Type-8 Transporter", now.ShipType);
        Assert.Null(now.ShipName);                              // this ship was never given a name
        Assert.Equal("Nanabozho", now.StarSystem);
        Assert.Equal(new[] { 22.9375, -35.75, 24.15625 }, now.StarPos);
        Assert.True(now.Docked);
        Assert.Equal("Lamarck Orbital", now.Station);
        Assert.Equal(3223906560L, state.MarketId);
        Assert.Equal(404, now.CargoCapacity);
        Assert.Equal((26.1, 14.7), (now.JumpEmpty, now.JumpLaden));
        Assert.Equal(8, ShipMath.JumpsFor(86.1, now.JumpLaden));
        Assert.Equal(6, ShipMath.JumpsFor(64.1, now.JumpLaden));
        Assert.Equal(Utc(2026, 10, 5, 21, 30, 44), now.LastEventUtc);
        Assert.Equal("unknown", now.Mode);                      // no Status.json in this practice folder
        Assert.Equal((2956, 61, 34663, 0), (state.PaidFor("bauxite"), state.PaidFor("Polymers"), state.PaidFor("silver"), state.PaidFor("gold")));
    }

    [Fact]
    public void Route_count_follows_the_game_across_a_replot()
    {
        using var game = new PracticeGame();
        game.Journal(Replot);
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        var counts = new List<int>();
        var arrivals = new List<string>();
        state.Journal += line =>
        {
            if (line.Event is "FSDTarget" or "NavRouteClear") counts.Add(state.RouteLeft);
            if (line.Event == "FSDJump") arrivals.Add($"{state.StarSystem}: {state.RouteLeft}");
        };

        watcher.Poll();

        // 5, 5, 15, 15: the same route plotted again with other settings, with no NavRoute line in between.
        Assert.Equal(new[] { 8, 7, 6, 6, 1, 0, 5, 5, 15, 15, 2, 1, 5, 4, 3, 2, 1, 0 }, counts);
        // Arriving does not touch the count: the game sent the count for after the jump while it was under way,
        // and cleared the route before the last arrival.
        Assert.Equal(new[]
        {
            "Aparctias: 7", "CQ Tucani: 6", "Owaha: 0", "LHS 3583: 1", "LHS 3167: 4", "Core Sys Sector PN-T b3-3: 3",
            "ER 8: 2", "Anyanwu: 1", "Crucis Sector AQ-Y c25: 0",
        }, arrivals);
        Assert.Equal(("Crucis Sector AQ-Y c25", "Wendy Wharf", 4209027587L), (state.StarSystem, state.Station, state.MarketId));
        Assert.Equal(("Anaconda", 17.0, 16.6), (state.Snapshot().ShipType, state.JumpEmpty, state.JumpLaden));
    }

    [Fact]
    public void Recording_without_a_shutdown_ends_mid_route()
    {
        using var game = new PracticeGame();
        game.Journal(Unfinished);
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;

        watcher.Poll();

        Assert.Equal(("Core Sys Sector ON-T b3-5", 11666339210673), (state.StarSystem, state.SystemAddress));
        Assert.Equal(new[] { 25.0625, 13.53125, 19.9375 }, state.StarPos);
        Assert.False(state.Docked);
        Assert.Null(state.Station);
        Assert.Equal(0, state.MarketId);
        Assert.Equal(2, state.RouteLeft);
        Assert.Equal(("type8", 26.1, 14.7), (state.Ship, state.JumpEmpty, state.JumpLaden));
        Assert.Equal(Utc(2026, 10, 6, 8, 12, 37), state.LastEventUtc);
    }

    [Fact]
    public void Leaving_the_ship_on_foot_keeps_the_ship_docked()
    {
        using var game = new PracticeGame();
        game.Journal(OnFoot);
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        var moments = new List<string>();
        state.Journal += line =>
        {
            if (line.Event is "Disembark" or "SuitLoadout" or "Embark") moments.Add($"{line.Event}: {state.StarSystem} / {state.Station} / {state.Ship}");
        };

        watcher.Poll();

        Assert.Equal(new[]
        {
            "Disembark: AY Indi / Farkas Dock / viper", "SuitLoadout: AY Indi / Farkas Dock / viper", "Embark: AY Indi / Farkas Dock / viper",
        }, moments);
        Assert.Equal(("Owaha", false, null), (state.StarSystem, state.Docked, state.Station));   // flew on and left Kenton Works
        Assert.Equal(("Viper Mk III", 4, 7.7, 7.4), (state.Snapshot().ShipType, state.CargoCapacity, state.JumpEmpty, state.JumpLaden));
        Assert.Equal(("SE-16V", 23), (state.Loadout!.ShipIdent, state.Loadout.Modules.Count));
    }

    [Fact]
    public void Exploration_recording_passes_scans_on_and_survives_two_deaths()
    {
        using var game = new PracticeGame();
        game.Journal(Exploring);
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        var moments = new List<string>();
        int scans = 0;
        state.Journal += line =>
        {
            if (line.Event == "Scan") scans++;
            if (line.Event is "Location" or "Died") moments.Add($"{line.Event}: {state.StarSystem} / {state.Station ?? "-"}");
        };

        watcher.Poll();

        Assert.Equal(20, scans);   // not used by the core, but every line reaches the studios
        Assert.Equal(new[]
        {
            "Location: Erlaza / Gagarin Vista", "Died: Anyanwu / -", "Location: Anyanwu / Delbruck Outpost",
            "Died: Anyanwu / -", "Location: Anyanwu / Delbruck Outpost",
        }, moments);
        Assert.Equal((true, 3228710912L), (state.Docked, state.MarketId));
        Assert.Equal(("Kestrel Mk II", 10.7, 10.6), (state.Snapshot().ShipType, state.JumpEmpty, state.JumpLaden));
    }

    [Fact]
    public void Swapping_ships_takes_the_new_ships_numbers_from_its_loadout()
    {
        using var game = new PracticeGame();
        game.Journal(ShipSwap);
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        (string? Ship, int Cargo, double Laden) atSwap = default;
        state.Journal += line =>
        {
            if (line.Event == "ShipyardSwap") atSwap = (state.Ship, state.CargoCapacity, state.JumpLaden);
        };

        watcher.Poll();

        Assert.Equal(("type8", 0, 16.4), atSwap);   // the swap names the ship; the Krait's numbers stand until the Loadout
        Assert.Equal(("type8", 352, 25.7, 15.4), (state.Ship, state.CargoCapacity, state.JumpEmpty, state.JumpLaden));
        Assert.Equal(("SE-13T", 29), (state.Loadout!.ShipIdent, state.Loadout.Modules.Count));
        Assert.Equal(("Agni", "Muspelheim", 4), (state.StarSystem, state.Station, state.RouteLeft));
    }

    [Fact]
    public void Loadout_is_kept_whole_with_modules_and_engineering()
    {
        using var game = new PracticeGame();
        game.Journal(Engineered);
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        var events = new List<string>();
        state.Journal += line => events.Add(line.Event);

        watcher.Poll();

        ShipLoadout ship = state.Loadout!;
        Assert.Equal(("mandalay", 20, "", "SE-06M", 32), (ship.Ship, ship.ShipId, ship.ShipName, ship.ShipIdent, ship.Modules.Count));
        Assert.Equal((17639220L, 24979632L, 2130942L, 0.5), (ship.HullValue, ship.ModulesValue, ship.Rebuy, ship.FuelReserve));

        ShipModule drive = ship.Modules.Single(m => m.Slot == "FrameShiftDrive");
        Assert.Equal(("int_hyperdrive_overcharge_size5_class5", 5, "A", true, 6124743L), (drive.Item, drive.Class, drive.Rating, drive.On, drive.Value));
        ModuleEngineering work = drive.Engineering!;
        Assert.Equal(("Elvira Martuuk", "FSD_LongRange", 3, 1.0, "special_fsd_lightweight", "Stripped Down"),
            (work.Engineer, work.Blueprint, work.Level, work.Quality, work.Experimental, work.ExperimentalName));
        Assert.Equal(new[]
        {
            new ModuleModifier("Mass", 21.599998, 20, true, ""), new ModuleModifier("Integrity", 109.199997, 120, false, ""),
            new ModuleModifier("PowerDraw", 0.654, 0.6, true, ""), new ModuleModifier("FSDOptimalMass", 1586.25, 1175, false, ""),
        }, work.Modifiers);
        Assert.Equal((4, "E"), ship.Modules.Where(m => m.Slot == "LifeSupport").Select(m => (m.Class, m.Rating)).Single());
        Assert.Equal((0, ""), ship.Modules.Where(m => m.Slot == "Armour").Select(m => (m.Class, m.Rating)).Single());
        // a size but no rating: the Guardian booster comes in one grade
        Assert.Equal((5, ""), ship.Modules.Where(m => m.Item == "int_guardianfsdbooster_size5").Select(m => (m.Class, m.Rating)).Single());
        Assert.Null(ship.Modules.Single(m => m.Slot == "PowerPlant").Engineering);

        // the numbers the jump range sum uses, and its result for the worked example in the Haul Overlay notes
        Assert.Equal((56.968765, 388.600006, 32.0, 32), (state.MaxJumpRange, state.UnladenMass, state.FuelCapacity, state.CargoCapacity));
        Assert.Equal((5.2, 10.5, 54.0, 50.9), (state.FsdFuel, state.BoosterLy, state.JumpEmpty, state.JumpLaden));
        Assert.Equal(("HIP 12099", false), (state.StarSystem, state.Docked));
        Assert.Equal(2, events.Count(e => e == "SAAScanComplete"));
        Assert.Contains("Touchdown", events);
    }

    [Fact]
    public void Plotted_route_gives_destination_and_jumps_left()
    {
        using var game = new PracticeGame();
        string[] lines = PracticeGame.Recording(Booster);
        int firstArrival = Array.IndexOf(lines, PracticeGame.Line(lines, "FSDJump")) + 1;
        string journal = game.Journal(Booster, lines.Take(firstArrival));   // one jump into the five-jump route
        game.Copy("NavRoute.json", game.JournalDir);
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;

        watcher.Poll();
        Assert.Equal(6, state.Route.Count);   // the start and five stops
        Assert.Equal(("Diaguandri", "M"), (state.Route[0].StarSystem, state.Route[0].StarClass));
        Assert.Equal(new[] { -171.59375, 19.96875, -56.96875 }, state.Route[5].StarPos);
        Assert.Equal(3107241104074, state.Route[5].SystemAddress);
        Assert.Equal(("Arietis Sector KJ-F a12-2", 4), (state.StarSystem, state.RouteLeft));
        Assert.Equal(("Khun", 4), (state.Snapshot().RouteDestination, state.Snapshot().JumpsLeft));
        Assert.Equal(4, state.PlottedJumps("khun"));
        Assert.Equal(0, state.PlottedJumps("Sol"));   // the route does not end there: the caller estimates instead

        // The game plots the route again: a new count, but it does not rewrite the list of stops.
        game.Append(journal, """{ "timestamp":"2026-10-08T09:16:00Z", "event":"FSDTarget", "Name":"Hyades Sector YB-K b9-1", "SystemAddress":2868635510153, "StarClass":"M", "RemainingJumpsInRoute":7 }""");
        watcher.Poll();
        Assert.Equal(7, state.Snapshot().JumpsLeft);

        // Logging in again forgets the game's count; the list of stops still tells where on the route the ship is.
        game.Append(journal, PracticeGame.Line(lines, "LoadGame"));
        watcher.Poll();
        Assert.Equal((0, 4), (state.RouteLeft, state.Snapshot().JumpsLeft));

        // The rest of the flight. The game clears the route during the last jump.
        game.Append(journal, lines[firstArrival..]);
        watcher.Poll();
        Assert.Equal("Khun", state.StarSystem);
        Assert.Empty(state.Route);
        Assert.Equal((null, 0), (state.Snapshot().RouteDestination, state.Snapshot().JumpsLeft));
        Assert.Equal((10.5, 42.8, 40.5), (state.BoosterLy, state.JumpEmpty, state.JumpLaden));

        // The game's own "no route" form of the file.
        game.Write(Path.Combine(game.JournalDir, "NavRoute.json"), "{ \"timestamp\":\"2026-10-08T10:33:27Z\", \"event\":\"NavRouteClear\", \"Route\":[ \r\n\r\n ] }\r\n");
        watcher.Poll();
        Assert.Empty(state.Route);
    }

    [Fact]
    public void First_poll_replays_then_lines_are_live_and_changed_fires_once_per_poll()
    {
        using var game = new PracticeGame();
        string[] lines = PracticeGame.Recording(Hauling);
        string journal = game.Journal(Hauling, lines.Take(100));
        using GameWatcher watcher = game.Watch();
        var seen = new List<JournalLine>();
        int changed = 0;
        watcher.State.Journal += seen.Add;
        watcher.State.Changed += () => changed++;

        watcher.Poll();
        Assert.Equal(100, seen.Count);
        Assert.All(seen, line => Assert.True(line.Replay));
        Assert.Equal(1, changed);
        Assert.Equal(("Fileheader", Utc(2026, 10, 5, 19, 21, 7)), (seen[0].Event, seen[0].TimeUtc));
        Assert.Equal("English/UK", seen[0].Data.GetProperty("language").GetString());

        watcher.Poll();   // nothing was written in between
        Assert.Equal((100, 1), (seen.Count, changed));

        game.Append(journal, lines[100..103]);
        watcher.Poll();
        Assert.Equal((103, 2), (seen.Count, changed));
        Assert.All(seen.Skip(100), line => Assert.False(line.Replay));
        // every line, in file order
        Assert.Equal(lines.Take(103).Select(line => line.Split("\"event\":\"")[1].Split('"')[0]), seen.Select(line => line.Event));
    }

    [Fact]
    public void A_line_split_across_two_polls_is_read_once_and_whole()
    {
        using var game = new PracticeGame();
        string journal = game.Journal(Hauling, PracticeGame.Recording(Hauling).Take(16));   // docked at Kent's Legacy
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        watcher.Poll();
        var seen = new List<JournalLine>();
        state.Journal += seen.Add;

        // The game has written only part of its next line, and stopped in the middle of a two-byte letter.
        byte[] line = Encoding.UTF8.GetBytes("{ \"timestamp\":\"2026-10-05T19:30:00Z\", \"event\":\"Docked\", \"StationName\":\"Café Lune\", \"StarSystem\":\"Arima\", \"MarketID\":4227056643 }\r\n");
        int middle = Array.IndexOf(line, (byte)0xC3) + 1;
        game.AppendBytes(journal, line[..middle]);
        watcher.Poll();
        Assert.Empty(seen);
        Assert.Equal("Kent's Legacy", state.Station);

        game.AppendBytes(journal, line[middle..]);
        watcher.Poll();
        JournalLine docked = Assert.Single(seen);
        Assert.Equal(("Docked", false, Utc(2026, 10, 5, 19, 30, 0)), (docked.Event, docked.Replay, docked.TimeUtc));
        Assert.Equal("Café Lune", state.Station);
    }

    [Fact]
    public void A_carrier_jump_moves_the_docked_ship_with_it()
    {
        using var game = new PracticeGame();
        string journal = game.Journal(Hauling);   // ends docked at Lamarck Orbital in Nanabozho
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        watcher.Poll();

        // No recording has one that could be published (the owner's are all on the squadron's carrier),
        // so this line is made up in the event's real shape: a docked Location under another name.
        game.Append(journal, """{ "timestamp":"2026-10-05T21:40:00Z", "event":"CarrierJump", "Docked":true, "StationName":"X9X-9XX", "StationType":"FleetCarrier", "MarketID":3700000001, "Taxi":false, "Multicrew":false, "StarSystem":"HIP 12099", "SystemAddress":560216394075, "StarPos":[-101.90625,-95.46875,-165.59375], "Body":"HIP 12099 2", "BodyID":17, "BodyType":"Planet" }""");
        watcher.Poll();

        Assert.Equal(("HIP 12099", true, "X9X-9XX", 3700000001L), (state.StarSystem, state.Docked, state.Station, state.MarketId));
        Assert.Equal(new[] { -101.90625, -95.46875, -165.59375 }, state.StarPos);
        Assert.Null(state.Market);   // the price list on disk, if any, belongs to the station left behind
    }

    [Fact]
    public void The_systems_number_follows_the_ship_and_never_outlives_its_system()
    {
        using var game = new PracticeGame();
        string journal = game.Journal(Hauling);   // ends docked at Lamarck Orbital in Nanabozho
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        var numbers = new List<string>();
        state.Journal += line =>
        {
            if (line.Event is "Location" or "FSDJump" or "Docked") numbers.Add($"{line.Event} {state.StarSystem} {state.SystemAddress}");
        };

        watcher.Poll();

        // Every kind of line that names the system carries its number: where the game starts, each jump, each docking.
        Assert.Equal(new[]
        {
            "Location Arima 5370319620968", "FSDJump LHS 3583 7268292634033", "Docked LHS 3583 7268292634033",
            "FSDJump Arima 5370319620968", "Docked Arima 5370319620968", "FSDJump Nanabozho 2832698675930", "Docked Nanabozho 2832698675930",
        }, numbers.Take(7));
        Assert.Equal(("Nanabozho", 2832698675930), (state.StarSystem, state.SystemAddress));

        // A carrier takes the docked ship along: the number moves with the name.
        game.Append(journal, """{ "timestamp":"2026-10-05T21:40:00Z", "event":"CarrierJump", "Docked":true, "StationName":"X9X-9XX", "StationType":"FleetCarrier", "MarketID":3700000001, "StarSystem":"HIP 12099", "SystemAddress":560216394075, "StarPos":[-101.90625,-95.46875,-165.59375] }""");
        watcher.Poll();
        Assert.Equal(("HIP 12099", 560216394075), (state.StarSystem, state.SystemAddress));

        // A line that names a system without its number (the game writes none like it) must not leave the old number on the new name.
        game.Append(journal, """{ "timestamp":"2026-10-05T21:45:00Z", "event":"FSDJump", "StarSystem":"Sol", "StarPos":[0,0,0] }""");
        watcher.Poll();
        Assert.Equal(("Sol", 0), (state.StarSystem, state.SystemAddress));
    }

    [Fact]
    public void A_neutron_stars_supercharge_lasts_until_the_ship_jumps_dies_is_swapped_or_logs_in_again()
    {
        // None of the owner's journals holds a supercharge, so the two JetConeBoost lines are written
        // in the shape Frontier's journal manual gives the event; everything around them is from
        // the recordings. The Mandalay has just left Ray Gateway with a five-jump route plotted.
        const string neutronStar = """{ "timestamp":"2026-10-08T09:15:20Z", "event":"JetConeBoost", "BoostValue":4.000000 }""";
        const string whiteDwarf = """{ "timestamp":"2026-10-08T09:15:20Z", "event":"JetConeBoost", "BoostValue":1.500000 }""";
        using var game = new PracticeGame();
        string[] lines = PracticeGame.Recording(Booster), hauling = PracticeGame.Recording(Hauling);
        int startJump = Array.IndexOf(lines, PracticeGame.Line(lines, "StartJump")), arrival = Array.IndexOf(lines, PracticeGame.Line(lines, "FSDJump"));
        string journal = game.Journal(Booster, lines.Take(startJump));
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        watcher.Poll();
        Assert.False(state.Supercharged);

        bool After(params string[] written)
        {
            game.Append(journal, written);
            watcher.Poll();
            return state.Supercharged;
        }

        Assert.True(After(neutronStar));
        // Charged until the jump is made: through the countdown and the seconds in hyperspace...
        Assert.True(After(lines[startJump..arrival]));
        // ...and spent on arrival. (The game marks such a jump with "BoostUsed":4; the mark is not needed to know.)
        Assert.False(After(lines[arrival].Replace("\"JumpDist\"", "\"BoostUsed\":4, \"JumpDist\"")));
        Assert.Equal("Arietis Sector KJ-F a12-2", state.StarSystem);

        // Short of a jump the charge stays: docking, the ship reported again after a refit, a carrier taking the ship along.
        Assert.True(After(neutronStar));
        Assert.True(After(
            PracticeGame.Line(hauling, "Docked"), PracticeGame.Line(lines, "Loadout"),
            """{ "timestamp":"2026-10-08T09:30:00Z", "event":"CarrierJump", "Docked":true, "StationName":"X9X-9XX", "StationType":"FleetCarrier", "MarketID":3700000001, "StarSystem":"HIP 12099", "SystemAddress":560216394075, "StarPos":[-101.90625,-95.46875,-165.59375] }"""));

        // A white dwarf's jet gives a much weaker boost, which is not what route plotters call supercharged.
        Assert.False(After(whiteDwarf));

        // What else ends it: the ship is destroyed, the player logs in again, the player boards another ship.
        Assert.True(After(neutronStar));
        Assert.False(After(PracticeGame.Line(PracticeGame.Recording(Exploring), "Died")));
        Assert.True(After(neutronStar));
        Assert.False(After(PracticeGame.Line(lines, "LoadGame")));
        Assert.True(After(neutronStar));
        Assert.False(After(PracticeGame.Line(hauling, "Loadout")));   // the Type-8

        // Studio started while the drive is charged: the journal read at start-up says so just the same.
        Assert.True(After(neutronStar));
        using GameWatcher started = game.Watch();
        started.Poll();
        Assert.True(started.State.Supercharged);
    }

    [Fact]
    public void A_newer_journal_file_is_followed_after_finishing_the_old_one()
    {
        using var game = new PracticeGame();
        string[] next = PracticeGame.Recording(Hauling);
        string old = game.Journal(Unfinished);
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        watcher.Poll();
        var seen = new List<JournalLine>();
        state.Journal += seen.Add;

        // Between two Polls the game closes and is started again: one last line in the old file, then a new file.
        game.Append(old, """{ "timestamp":"2026-10-06T08:13:00Z", "event":"Shutdown" }""");
        string started = game.Journal("Journal.2026-10-06T041358.01.log", next.Take(16));
        watcher.Poll();

        Assert.Equal(17, seen.Count);
        Assert.Equal(("Shutdown", "Fileheader", "Loadout"), (seen[0].Event, seen[1].Event, seen[15].Event));
        Assert.All(seen, line => Assert.False(line.Replay));   // only the very first file is a replay
        Assert.Equal(("Arima", "Kent's Legacy"), (state.StarSystem, state.Station));

        game.Append(started, next[16..23]);   // ... up to leaving the station
        watcher.Poll();
        Assert.Equal((24, "Undocked", false), (seen.Count, seen[^1].Event, state.Docked));
    }

    [Fact]
    public void An_older_journal_that_gets_touched_is_not_taken_for_news()
    {
        using var game = new PracticeGame();
        string earlier = game.Journal(Hauling);
        string current = game.Journal(Unfinished);
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        watcher.Poll();
        var seen = new List<JournalLine>();
        state.Journal += seen.Add;

        game.Touch(earlier);   // a backup tool or an editor: now the file written last, with nothing new in it
        watcher.Poll();
        Assert.Empty(seen);
        Assert.Equal("Core Sys Sector ON-T b3-5", state.StarSystem);

        // The game writes on in its own file: followed again from where the reading stopped, nothing twice.
        game.Append(current, """{ "timestamp":"2026-10-06T08:13:00Z", "event":"Shutdown" }""");
        watcher.Poll();
        JournalLine shutdown = Assert.Single(seen);
        Assert.Equal(("Shutdown", false), (shutdown.Event, shutdown.Replay));
    }

    [Fact]
    public void A_broken_line_or_a_failing_listener_does_not_stop_the_journal()
    {
        using var game = new PracticeGame();
        string[] lines = PracticeGame.Recording(Hauling);
        string journal = game.Journal(Hauling, lines.Take(16));   // docked at Kent's Legacy
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        watcher.Poll();
        var seen = new List<string>();
        var seenAfterIt = new List<string>();
        int changed = 0;
        state.Journal += line =>
        {
            seen.Add(line.Event);
            if (line.Event == "Undocked") throw new InvalidOperationException("a studio tripped over this line");
        };
        state.Journal += line => seenAfterIt.Add(line.Event);   // another studio, called after the failing one
        state.Changed += () => throw new InvalidOperationException("a studio tripped over the change");
        state.Changed += () => changed++;

        // The line a studio fails on is this Poll's only news: the other studio still gets it, and the change it made.
        game.Append(journal, PracticeGame.Line(lines, "Undocked"));
        watcher.Poll();   // must not throw
        Assert.Equal(new[] { "Undocked" }, seenAfterIt);
        Assert.Equal((false, 1), (state.Docked, changed));

        game.Append(journal,
            "{ \"timestamp\":\"2026-10-05T19:25:00Z\", \"event\":\"FSDJump\", \"StarSystem\":\"Nowhe",   // cut off
            "this is not JSON",
            "",
            "[ 1, 2, 3 ]");                                                                              // JSON, but not a journal line
        // JSON as far as the parser can tell, but the name holds a byte that is not UTF-8 text
        game.AppendBytes(journal, [.. "{ \"timestamp\":\"2026-10-05T19:25:01Z\", \"event\":\"Commander\", \"FID\":\"F0000000\", \"Name\":\"CMDR "u8, 0xC3, .. "\" }\r\n"u8]);
        game.Append(journal, PracticeGame.Line(lines, "FSDJump"));
        watcher.Poll();

        Assert.Equal(new[] { "Undocked", "FSDJump" }, seen);
        Assert.Equal(seen, seenAfterIt);
        Assert.Equal(("LHS 3583", "CMDR Test", 2), (state.StarSystem, state.Commander, changed));
    }

    [Fact]
    public void A_missing_journal_folder_is_waited_for()
    {
        using var game = new PracticeGame();
        Directory.Delete(game.JournalDir);
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        var replays = new List<bool>();
        state.Journal += line => replays.Add(line.Replay);

        watcher.Poll();   // no journal folder, no options folder: nothing known, nothing wrong
        Assert.Null(state.Snapshot().StarSystem);
        Assert.Equal((DisplayMode.Unknown, 0, GameStatus.None), (state.DisplayMode, state.Bindings.Keys.Count, state.Status));
        Assert.Equal(EliteWindow.Running, state.GameRunning);

        game.Journal(Unfinished);
        watcher.Poll();
        Assert.Equal("Core Sys Sector ON-T b3-5", state.StarSystem);
        Assert.Equal(78, replays.Count);
        Assert.All(replays, replay => Assert.True(replay));   // what was already in the first file found is history, whenever it is found
    }

    [Fact]
    public void Prices_paid_in_earlier_sessions_are_still_known()
    {
        using var game = new PracticeGame();
        game.Journal(Hauling);      // an earlier session, with purchases
        game.Journal(Unfinished);   // the newest file: none
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        int lines = 0;
        state.Journal += _ => lines++;

        watcher.Poll();

        Assert.Equal((78, "Core Sys Sector ON-T b3-5"), (lines, state.StarSystem));   // only the newest file is replayed
        Assert.Equal((34663, 61, 2956), (state.PaidFor("silver"), state.PaidFor("polymers"), state.PaidFor("bauxite")));
    }

    [Fact]
    public void Status_flags_are_decoded()
    {
        using var game = new PracticeGame();
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        GameStatus Status(string json)
        {
            game.Write(Path.Combine(game.JournalDir, "Status.json"), json + "\r\n");
            watcher.Poll();
            return state.Status;
        }

        // docked in the main ship, gear down, shields up, mass locked, station services open
        game.Copy("Status.json", game.JournalDir);
        watcher.Poll();
        GameStatus docked = state.Status;
        Assert.True(docked.Docked && docked.InMainShip);
        Assert.False(docked.Landed || docked.Supercruise || docked.HardpointsDeployed || docked.InDanger || docked.BeingInterdicted
            || docked.InFighter || docked.InSrv || docked.OnFoot || docked.FsdCharging || docked.FsdJumping);
        Assert.Equal((5, 32.0, 0.52, 417.0, null), (docked.GuiFocus, docked.FuelMain, docked.FuelReservoir, docked.Cargo, docked.Destination));
        Assert.Equal(("ship", 5), (state.Snapshot().Mode, state.Snapshot().GuiFocus));

        GameStatus charging = Status("""{ "timestamp":"2026-10-05T19:27:02Z", "event":"Status", "Flags":16908312, "Flags2":0, "Pips":[4,8,0], "FireGroup":0, "GuiFocus":0, "Fuel":{ "FuelMain":31.500000, "FuelReservoir":0.410000 }, "Cargo":404.000000, "LegalState":"Clean", "Destination":{ "System":7268292634033, "Body":0, "Name":"LHS 3583" } }""");
        Assert.True(charging.Supercruise && charging.FsdCharging && charging.InMainShip);
        Assert.False(charging.Docked || charging.FsdJumping);
        Assert.Equal(new StatusDestination(7268292634033, 0, "LHS 3583"), charging.Destination);

        Assert.True(Status("""{ "timestamp":"2026-10-05T19:27:20Z", "event":"Status", "Flags":1090519064 }""").FsdJumping);

        GameStatus fighting = Status("""{ "timestamp":"2026-10-05T19:40:00Z", "event":"Status", "Flags":29360200, "Destination":{ "System":1, "Body":2, "Name":"$MULTIPLAYER_SCENARIO42_TITLE;", "Name_Localised":"Nav Beacon" } }""");
        Assert.True(fighting.HardpointsDeployed && fighting.InDanger && fighting.BeingInterdicted && fighting.InMainShip);
        Assert.Equal("Nav Beacon", fighting.Destination!.Name);

        Assert.True(Status("""{ "timestamp":"2026-10-05T19:41:00Z", "event":"Status", "Flags":16777222 }""").Landed);
        Assert.Equal("fighter", Status("""{ "timestamp":"2026-10-05T19:42:00Z", "event":"Status", "Flags":33554440 }""").Mode);
        Assert.Equal("srv", Status("""{ "timestamp":"2026-10-05T19:43:00Z", "event":"Status", "Flags":2214592512 }""").Mode);   // top bit too: SRV high beam
        Assert.Equal("foot", Status("""{ "timestamp":"2026-10-05T19:44:00Z", "event":"Status", "Flags":0, "Flags2":9 }""").Mode);
        Assert.Equal("unknown", Status("""{ "timestamp":"2026-10-08T13:03:09Z", "event":"Status", "Flags":0 }""").Mode);         // as the game leaves it on closing
    }

    [Fact]
    public void Hold_and_price_list_come_from_the_companion_files()
    {
        using var game = new PracticeGame();
        string[] lines = PracticeGame.Recording(Hauling);
        string journal = game.Journal(Hauling);   // ends docked at Lamarck Orbital
        string cargo = game.Copy("Cargo.json", game.JournalDir);
        game.Copy("Market.json", game.JournalDir);
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;

        watcher.Poll();
        // biggest load first, limpets left out of the list but counted in the tons
        Assert.Equal(new[] { new CargoItem("militarygradefabrics", "Military Grade Fabrics", 400), new CargoItem("haematite", "Haematite", 4) }, state.Cargo);
        Assert.Equal((417, 417), (state.CargoCount, state.Snapshot().CargoCount));
        StationMarket market = state.Market!;
        Assert.Equal((3223906560L, "Lamarck Orbital", "Nanabozho", 5), (market.MarketId, market.StationName, market.StarSystem, market.Items.Count));
        Assert.Equal(new MarketItem("platinum", "Platinum", 0, 20536, 0, 1326), market.Items["platinum"]);
        Assert.Equal(260, market.Items["Water"].BuyPrice);

        // Leaving: the list on disk is no longer the list of where the player is.
        game.Append(journal, PracticeGame.Line(lines, "Undocked"));
        watcher.Poll();
        Assert.Null(state.Market);

        // Docked somewhere else while the file still holds Lamarck Orbital's list.
        game.Append(journal, PracticeGame.Line(lines, "Docked"));
        watcher.Poll();
        Assert.Equal(("Antoniodeandrade Beacon", null), (state.Station, state.Market));

        // In the SRV the file describes the SRV, which counts as an empty hold. (The owner's own file, as found.)
        game.Write(cargo, "{ \"timestamp\":\"2026-10-08T12:39:34Z\", \"event\":\"Cargo\", \"Vessel\":\"SRV\", \"Count\":0, \"Inventory\":[ \r\n\r\n ] }\r\n");
        watcher.Poll();
        Assert.Empty(state.Cargo);
        Assert.Equal(0, state.CargoCount);
    }

    [Fact]
    public void A_file_the_game_is_still_writing_is_read_on_a_later_poll()
    {
        const string cruising = """{ "timestamp":"2026-10-05T21:31:00Z", "event":"Status", "Flags":16777240 }""";
        using var game = new PracticeGame();
        string file = game.Copy("Status.json", game.JournalDir);
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        watcher.Poll();
        Assert.True(state.Status.Docked);
        int changed = 0;
        state.Changed += () => changed++;

        using (var locked = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            locked.Write(Encoding.UTF8.GetBytes(cruising));
            locked.Flush();
            watcher.Poll();   // the game holds the file: no error, the last good read stands
            Assert.True(state.Status.Docked);
        }
        game.Touch(file);
        watcher.Poll();
        Assert.True(state.Status.Supercruise);

        // Caught half-written, then finished without the file's time or size moving on: it is still read again.
        DateTime time = File.GetLastWriteTimeUtc(file);
        File.WriteAllText(file, cruising[..^12].PadRight(cruising.Length));
        File.SetLastWriteTimeUtc(file, time.AddSeconds(1));
        watcher.Poll();
        Assert.True(state.Status.Supercruise);
        File.WriteAllText(file, cruising.Replace("16777240", "67108865"));
        File.SetLastWriteTimeUtc(file, time.AddSeconds(1));
        watcher.Poll();
        Assert.True(state.Status.Docked && state.Status.InSrv);

        Assert.Equal(2, changed);   // one per good read (each changed what the snapshot shows); the two failed reads changed nothing
    }

    [Fact]
    public void A_status_rewrite_counts_as_a_change_only_when_the_snapshot_shows_it()
    {
        // In flight the game rewrites Status.json about once a second. Told on every time, each rewrite would
        // go to all the pages, draw the overlay again and send hauling's whole trade list once more.
        static string Flying(double fuel, int flags = 16777240, int screen = 0) => FormattableString.Invariant(
            $$"""{ "timestamp":"2026-10-05T21:31:00Z", "event":"Status", "Flags":{{flags}}, "GuiFocus":{{screen}}, "Fuel":{ "FuelMain":{{fuel}}, "FuelReservoir":0.41 } }""");
        using var game = new PracticeGame();
        string file = Path.Combine(game.JournalDir, "Status.json");
        game.Write(file, Flying(31.5));
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        watcher.Poll();
        int changed = 0;
        state.Changed += () => changed++;

        game.Write(file, Flying(31.25));                    // the fuel gauge moved: read, and nobody is told
        watcher.Poll();
        Assert.Equal((31.25, 0), (state.Status.FuelMain, changed));

        game.Write(file, Flying(31, screen: 6));            // the galaxy map was opened
        watcher.Poll();
        Assert.Equal((6, 1), (state.Snapshot().GuiFocus, changed));

        game.Write(file, Flying(30.75, flags: 67108864));   // out in the SRV
        watcher.Poll();
        Assert.Equal(("srv", 2), (state.Snapshot().Mode, changed));
    }

    [Fact]
    public void Keys_the_game_uses_are_read_from_the_active_bindings()
    {
        using var game = new PracticeGame();
        game.Copy("Custom.4.2.binds", game.BindingsDir);
        string start = game.Copy("StartPreset.4.start", game.BindingsDir);
        // an older file of the same preset, which the newer one replaces
        game.Write(Path.Combine(game.BindingsDir, "Custom.4.1.binds"), "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\r\n<Root PresetName=\"Custom\" MajorVersion=\"4\" MinorVersion=\"1\">\r\n\t<OldAction>\r\n\t\t<Primary Device=\"Keyboard\" Key=\"Key_F3\" />\r\n\t</OldAction>\r\n</Root>\r\n");
        using GameWatcher watcher = game.Watch();

        watcher.Poll();
        KeyBindings keys = watcher.State.Bindings;

        Assert.Equal(new[] { "Custom", "KeyboardMouseOnly" }, keys.Presets);   // the second ships with the game and has no file here
        Assert.Equal(71, keys.Keys.Count);
        Assert.Equal(new[] { "ShowPGScoreSummaryInput" }, keys.UsedBy("F1"));
        Assert.Equal(new[] { "HMDReset" }, keys.UsedBy("Ctrl+Shift+F12"));
        Assert.All(new[] { "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9" }, key => Assert.Empty(keys.UsedBy(key)));   // the Haul Overlay's default keys
        Assert.Contains("LeftThrustButton", keys.UsedBy("Key_Q"));
        Assert.Contains("HeadLookReset", keys.UsedBy("q"));
        Assert.Contains("HeadLookReset", keys.UsedBy("LeftShift"));   // held with Q for that action
        // Windows' names for keys the game calls something else
        Assert.Equal(keys.UsedBy("UpArrow"), keys.UsedBy("Up"));
        Assert.Contains("OrderDefensiveBehaviour", keys.UsedBy("Alt+OemTilde"));
        Assert.All(new[] { "Up", "D1", "NumPad0", "Return", "Back", "LeftCtrl", "OemPlus", "OemMinus", "OemPipe", "OemQuestion", "OemQuotes", "Capital", "Next", "Prior" },
            key => Assert.NotEmpty(keys.UsedBy(key)));

        // The player switches every control group to a preset that ships with the game.
        game.Write(start, "KeyboardMouseOnly\nKeyboardMouseOnly\nKeyboardMouseOnly\nKeyboardMouseOnly");
        watcher.Poll();
        Assert.Equal(new[] { "KeyboardMouseOnly" }, watcher.State.Bindings.Presets);
        Assert.Empty(watcher.State.Bindings.UsedBy("F1"));
    }

    [Fact]
    public void Key_bindings_that_cannot_be_read_yet_keep_the_known_keys()
    {
        // what the game writes when the player rebinds: here a single action, on the left bracket key
        const string rebound = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\r\n<Root PresetName=\"Custom\" MajorVersion=\"4\" MinorVersion=\"2\">\r\n\t<GalaxyMapOpen>\r\n\t\t<Primary Device=\"Keyboard\" Key=\"Key_LeftBracket\" />\r\n\t</GalaxyMapOpen>\r\n</Root>\r\n";
        using var game = new PracticeGame();
        string binds = game.Copy("Custom.4.2.binds", game.BindingsDir);
        game.Copy("StartPreset.4.start", game.BindingsDir);
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        watcher.Poll();
        Assert.NotEmpty(state.Bindings.UsedBy("F1"));
        int changed = 0;
        state.Changed += () => changed++;

        // Caught half-written. "Every key is free" would be the wrong answer: the known keys stand.
        game.Write(binds, rebound[..^20].PadRight(rebound.Length));
        DateTime time = File.GetLastWriteTimeUtc(binds);
        watcher.Poll();
        Assert.NotEmpty(state.Bindings.UsedBy("F1"));

        // Finished without the file's time or size moving on, but another program holds it.
        File.WriteAllText(binds, rebound);
        File.SetLastWriteTimeUtc(binds, time);
        using (new FileStream(binds, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            watcher.Poll();
            Assert.NotEmpty(state.Bindings.UsedBy("F1"));
        }
        Assert.Equal(0, changed);

        watcher.Poll();   // let go: read at last, with nothing about the file having changed since the failed tries
        Assert.Equal(1, changed);
        Assert.Empty(state.Bindings.UsedBy("F1"));
        Assert.Equal(new[] { "LeftBracket" }, state.Bindings.Keys);
        // both of Windows' names for that key
        Assert.All(new[] { "Oem4", "OemOpenBrackets", "Ctrl+Oem4" }, key => Assert.Equal(new[] { "GalaxyMapOpen" }, state.Bindings.UsedBy(key)));
    }

    [Fact]
    public void A_preset_the_player_never_changed_is_read_from_the_games_own_folder()
    {
        // A few actions as the game's own ControlSchemes\KeyboardMouseOnly.binds writes them: a key, a key
        // to hold down, a key with a second one held, a mouse button, and an action with nothing on it.
        // (One is made up: there HMDReset is on F12. Here it is on F6, one of the overlay's own default keys.)
        const string shipped = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Root PresetName=\"KeyboardMouseOnly\" SortOrder=\"0\">\r\n"
            + "\t<HumanoidForwardButton>\r\n\t\t<Primary Device=\"Keyboard\" Key=\"Key_W\" />\r\n\t\t<Secondary Device=\"{NoDevice}\" Key=\"\" />\r\n\t</HumanoidForwardButton>\r\n"
            + "\t<HumanoidEmoteWheelButton>\r\n\t\t<Primary Device=\"Keyboard\" Key=\"Key_B\">\r\n\t\t\t<Hold Value=\"1\" />\r\n\t\t</Primary>\r\n\t\t<Secondary Device=\"{NoDevice}\" Key=\"\" />\r\n\t\t<ToggleOn />\r\n\t</HumanoidEmoteWheelButton>\r\n"
            + "\t<PhotoCameraToggle_Humanoid>\r\n\t\t<Primary Device=\"Keyboard\" Key=\"Key_Space\">\r\n\t\t\t<Modifier Device=\"Keyboard\" Key=\"Key_LeftControl\" />\r\n\t\t</Primary>\r\n\t\t<Secondary Device=\"{NoDevice}\" Key=\"\" />\r\n\t</PhotoCameraToggle_Humanoid>\r\n"
            + "\t<HumanoidUtilityWheelCycleMode>\r\n\t\t<Primary Device=\"Mouse\" Key=\"Mouse_2\" />\r\n\t\t<Secondary Device=\"{NoDevice}\" Key=\"\" />\r\n\t</HumanoidUtilityWheelCycleMode>\r\n"
            + "\t<HMDReset>\r\n\t\t<Primary Device=\"Keyboard\" Key=\"Key_F6\" />\r\n\t\t<Secondary Device=\"{NoDevice}\" Key=\"\" />\r\n\t</HMDReset>\r\n"
            + "\t<GalaxyMapOpen>\r\n\t\t<Primary Device=\"{NoDevice}\" Key=\"\" />\r\n\t\t<Secondary Device=\"{NoDevice}\" Key=\"\" />\r\n\t</GalaxyMapOpen>\r\n</Root>\r\n";
        using var game = new PracticeGame();
        string install = Path.Combine(game.OptionsDir, "..", "Game");
        game.Write(Path.Combine(install, "ControlSchemes", "KeyboardMouseOnly.binds"), shipped);
        game.Copy("Custom.4.2.binds", game.BindingsDir);
        string start = game.Copy("StartPreset.4.start", game.BindingsDir);   // ship and SRV on Custom, on foot on KeyboardMouseOnly
        int asked = 0;
        using var watcher = new GameWatcher(game.JournalDir, game.BindingsDir, () => { asked++; return install; });

        watcher.Poll();
        KeyBindings keys = watcher.State.Bindings;

        // the player's own file and the game's preset together
        Assert.Equal(new[] { "Custom", "KeyboardMouseOnly" }, keys.Presets);
        Assert.Equal(new[] { "ShowPGScoreSummaryInput" }, keys.UsedBy("F1"));
        Assert.Equal(new[] { "HMDReset" }, keys.UsedBy("F6"));
        Assert.Contains("HumanoidForwardButton", keys.UsedBy("W"));
        Assert.Contains("HumanoidEmoteWheelButton", keys.UsedBy("Shift+B"));
        Assert.Contains("PhotoCameraToggle_Humanoid", keys.UsedBy("Space"));
        Assert.Contains("PhotoCameraToggle_Humanoid", keys.UsedBy("LeftCtrl"));   // held with Space for that action
        Assert.Equal(72, keys.Keys.Count);                                        // Custom's 71 and F6
        Assert.Equal(1, asked);

        // Every control group on presets that ship with the game. One of them is not in this game folder, and
        // one is no file name at all: both are passed over, and the game folder is looked for once all the same.
        game.Write(start, "KeyboardMouseOnly\nClassicKeyboardOnly\n..\\..\\Bindings\\Custom.4.2\nKeyboardMouseOnly");
        watcher.Poll();
        keys = watcher.State.Bindings;
        Assert.Equal(new[] { "KeyboardMouseOnly", "ClassicKeyboardOnly", "..\\..\\Bindings\\Custom.4.2" }, keys.Presets);
        Assert.Equal(new[] { "B", "F6", "LeftControl", "Space", "W" }, keys.Keys.Order());
        Assert.Equal(2, asked);

        // The player changes a control: the game writes the changed preset into the bindings folder, and that file counts.
        game.Write(Path.Combine(game.BindingsDir, "KeyboardMouseOnly.4.2.binds"), shipped.Replace("Key_F6", "Key_F10"));
        watcher.Poll();
        Assert.Empty(watcher.State.Bindings.UsedBy("F6"));
        Assert.NotEmpty(watcher.State.Bindings.UsedBy("F10"));
    }

    [Fact]
    public void Display_mode_is_read_from_the_games_settings()
    {
        using var game = new PracticeGame();
        using GameWatcher watcher = game.Watch();
        string file = game.Copy("DisplaySettings.xml", Path.Combine(game.OptionsDir, "Graphics"));
        string settings = File.ReadAllText(file);

        watcher.Poll();
        Assert.Equal(DisplayMode.Borderless, watcher.State.DisplayMode);   // the owner's own setting

        game.Write(file, settings.Replace("<FullScreen>2<", "<FullScreen>1<"));
        watcher.Poll();
        Assert.Equal(DisplayMode.Fullscreen, watcher.State.DisplayMode);

        game.Write(file, settings.Replace("<FullScreen>2<", "<FullScreen>0<"));
        watcher.Poll();
        Assert.Equal(DisplayMode.Windowed, watcher.State.DisplayMode);

        // Caught half-written, then finished without the file's time or size moving on: the mode
        // known so far stands until the whole file can be read.
        string fullscreen = settings.Replace("<FullScreen>2<", "<FullScreen>1<");
        game.Write(file, fullscreen[..^10].PadRight(fullscreen.Length));
        DateTime time = File.GetLastWriteTimeUtc(file);
        watcher.Poll();
        Assert.Equal(DisplayMode.Windowed, watcher.State.DisplayMode);
        File.WriteAllText(file, fullscreen);
        File.SetLastWriteTimeUtc(file, time);
        watcher.Poll();
        Assert.Equal(DisplayMode.Fullscreen, watcher.State.DisplayMode);
    }

    [Fact]
    public void Off_Windows_the_game_is_the_process_whose_command_line_names_its_program()
    {
        // A practice copy of Linux's list of processes: one folder a process, named by its number, its command line inside.
        string proc = Directory.CreateTempSubdirectory("EliteStudioProc-").FullName;
        try
        {
            void Process(string name, string? commandLine)
            {
                Directory.CreateDirectory(Path.Combine(proc, name));
                if (commandLine is not null) File.WriteAllText(Path.Combine(proc, name, "cmdline"), commandLine);
            }
            Process("self", "bash\0");                                         // not a process number
            Process("12", "/usr/bin/steam\0-silent\0");
            Process("40", null);                                                // ended while the list was read
            Assert.Equal(0u, EliteWindow.UnderProton(proc));

            // Under Proton the game runs as a Wine process whose command line is the Windows path of its program.
            Process("4471", @"Z:\home\deck\.local\share\Steam\steamapps\common\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe" + "\0");
            Assert.Equal(4471u, EliteWindow.UnderProton(proc));
        }
        finally
        {
            Directory.Delete(proc, recursive: true);
        }
        Assert.Equal(0u, EliteWindow.UnderProton(proc));   // and no list of processes at all: not running
    }
}
