using System.Globalization;
using System.Text.Json;
using EliteStudio.Core;
using EliteStudio.Nav;

namespace EliteStudio.Tests;

/// <summary>
/// The commander's first discoveries over all their journals, for the 3D map (<see cref="FirstsLog"/>).
/// The trip flown is tests\Fixtures\nav\first-discoveries.log (see ExploreTests): written by hand in
/// the game's own layout, with made-up systems. Its firsts: the two stars and three planets of
/// "Practice Sector LX-T d3-7" (system 3003), and a planet of "Practice Sector MX-T c3-4" (4004),
/// whose star somebody had discovered before. The other lines below are cut down from those.
/// </summary>
public sealed class FirstsLogTests : IDisposable
{
    static readonly string[] Trip = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nav", "first-discoveries.log"));

    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioFirsts-").FullName;
    readonly PracticeGame game = new();
    readonly List<GameWatcher> watchers = [];

    public void Dispose()
    {
        foreach (GameWatcher watcher in watchers) watcher.Dispose();
        game.Dispose();
        Directory.Delete(temp, recursive: true);
    }

    string Folder => Path.Combine(temp, "heatmap");

    /// <summary>Studio starts: a new watcher, and the list of firsts on it, before the journal is caught up with.</summary>
    (GameWatcher Watcher, FirstsLog Firsts) Start()
    {
        GameWatcher watcher = game.Watch();
        watchers.Add(watcher);
        var firsts = new FirstsLog(watcher.State, game.JournalDir, Folder);
        watcher.Poll();
        return (watcher, firsts);
    }

    static DateTime Time(string clock) => DateTime.Parse($"2026-10-09T{clock}Z", CultureInfo.InvariantCulture).ToUniversalTime();

    static string Arrival(string system, long address, double x, double y, double z, string clock) => string.Create(CultureInfo.InvariantCulture,
        $$"""{ "timestamp":"2026-10-09T{{clock}}Z", "event":"FSDJump", "StarSystem":"{{system}}", "SystemAddress":{{address}}, "StarPos":[{{x}},{{y}},{{z}}], "Body":"{{system}}", "BodyID":0, "BodyType":"Star", "JumpDist":40.1 }""");

    /// <summary>A scan of a star (or of a belt cluster, which is no body), with what the game said about its discovery: true, false, or nothing at all (null).</summary>
    static string Scan(string system, long address, string clock, bool? discovered, bool star = true) =>
        $$"""{ "timestamp":"2026-10-09T{{clock}}Z", "event":"Scan", "ScanType":"AutoScan", "BodyName":"{{system}}", "BodyID":0, "StarSystem":"{{system}}", "SystemAddress":{{address}}, "DistanceFromArrivalLS":0.0{{(star ? """, "StarType":"K" """ : "")}}{{discovered switch { true => """, "WasDiscovered":true""", false => """, "WasDiscovered":false""", null => "" }}} }""";

    static readonly FirstFind NewSystem = new("Practice Sector LX-T d3-7", 3003, 60, 10, 80, Time("10:08:05"));
    static readonly FirstFind HalfKnown = new("Practice Sector MX-T c3-4", 4004, 90, 15, 120, Time("10:20:40"));

    [Fact]
    public async Task Every_journal_is_searched_once_for_first_discoveries_and_new_lines_are_followed()
    {
        // The trip is in an older journal; the newest has the commander back home.
        game.Journal("Journal.2026-10-09T100000.01.log", Trip);
        string newest = game.Journal("Journal.2026-10-09T120000.01.log", [Trip[0].Replace("10:00:00", "12:00:00"), Trip[1].Replace("10:00:10", "12:00:10")]);
        var (watcher, firsts) = Start();

        // A system somebody discovered before counts nothing, nor does a belt cluster. Coming back to the new
        // system, whose star still says "not discovered" because the finds were not sold yet, keeps the first time.
        Assert.Equal([NewSystem, HalfKnown], await firsts.Systems());

        // What the game writes from now on is followed: a new system's star, a belt cluster nobody has
        // discovered in a known system, and a known star.
        game.Append(newest,
            Arrival("Practice Sector NX-T c3-5", 5005, 120, 20, 160, "12:10:00"),
            Scan("Practice Sector NX-T c3-5", 5005, "12:10:05", discovered: false),
            Arrival("Practice Sector OX-T c3-6", 6006, 150, 25, 200, "12:20:00"),
            Scan("Practice Sector OX-T c3-6", 6006, "12:20:05", discovered: true),
            Scan("Practice Sector OX-T c3-6", 6006, "12:20:10", discovered: false, star: false));
        watcher.Poll();

        Assert.Equal([NewSystem, HalfKnown, new FirstFind("Practice Sector NX-T c3-5", 5005, 120, 20, 160, Time("12:10:05"))], await firsts.Systems());

        // The id64 travels as text.
        Assert.Equal("""{"name":"Practice Sector LX-T d3-7","id64":"3003","x":60,"y":10,"z":80,"at":"2026-10-09T10:08:05Z"}""",
            JsonSerializer.Serialize(NewSystem, new JsonSerializerOptions(App.Settings.Json) { WriteIndented = false }));
    }

    [Fact]
    public async Task What_was_found_is_kept_and_the_next_start_searches_only_the_journals_written_since()
    {
        string older = game.Journal("Journal.2026-10-09T100000.01.log", Trip);
        string newest = game.Journal("Journal.2026-10-09T120000.01.log", [Trip[0].Replace("10:00:00", "12:00:00"), Trip[1].Replace("10:00:10", "12:00:10")]);
        var (_, firsts) = Start();
        Assert.Equal([NewSystem, HalfKnown], await firsts.Systems());
        Assert.True(File.Exists(Path.Combine(Folder, "firsts.json")));
        Assert.False(File.Exists(Path.Combine(Folder, "firsts.json.tmp")));

        // The older journal is not read again: were it, this first (written into it behind Studio's back, the
        // file's time left as it was) would show. The newest one is, and so is one the game has started since.
        DateTime written = File.GetLastWriteTimeUtc(older);
        File.AppendAllLines(older, [Arrival("Hidden", 7007, 1, 2, 3, "11:00:00"), Scan("Hidden", 7007, "11:00:05", discovered: false)]);
        File.SetLastWriteTimeUtc(older, written);
        game.Append(newest, Arrival("Practice Sector NX-T c3-5", 5005, 120, 20, 160, "12:10:00"), Scan("Practice Sector NX-T c3-5", 5005, "12:10:05", discovered: false));
        game.Journal("Journal.2026-10-09T130000.01.log",
            [Arrival("Practice Sector PX-T c3-7", 8008, 180, 30, 240, "13:00:00"), Scan("Practice Sector PX-T c3-7", 8008, "13:00:05", discovered: false)]);
        (_, firsts) = Start();

        Assert.Equal(["Practice Sector LX-T d3-7", "Practice Sector MX-T c3-4", "Practice Sector NX-T c3-5", "Practice Sector PX-T c3-7"],
            (await firsts.Systems()).Select(system => system.Name));

        // A kept list that cannot be read: every journal is searched again, the changed older one included.
        File.WriteAllText(Path.Combine(Folder, "firsts.json"), "{ not the list");
        (_, firsts) = Start();
        Assert.Equal(["Practice Sector LX-T d3-7", "Practice Sector MX-T c3-4", "Hidden", "Practice Sector NX-T c3-5", "Practice Sector PX-T c3-7"],
            (await firsts.Systems()).Select(system => system.Name));
    }

    [Fact]
    public async Task A_first_whose_arrival_the_journal_does_not_show_is_placed_by_its_number_and_old_scans_count_nothing()
    {
        // The game's own number for "Wredguia WD-K d8-65" names a cube of 80 ly round (-345, 15, -305).
        long wredguia = ProcGenNames.Decode("Wredguia WD-K d8-65")!.Cube.Address;
        game.Journal("Journal.2026-10-09T100000.01.log",
        [
            Trip[0],
            Scan("Wredguia WD-K d8-65", wredguia, "10:00:05", discovered: false),
            // Before 2018 the game did not say whether a body had been discovered: such a scan says nothing.
            Arrival("Old Times", 9009, 5, 5, 5, "10:01:00"),
            Scan("Old Times", 9009, "10:01:05", discovered: null),
            """{ "timestamp":"2026-10-09T10:02:00Z", "event":"Scan", "BodyName":"Cut short", "StarType":"K", "WasDiscovered":false""",
        ]);
        var (_, firsts) = Start();

        Assert.Equal([new FirstFind("Wredguia WD-K d8-65", wredguia, -345, 15, -305, Time("10:00:05"))], await firsts.Systems());
    }

    [Fact]
    public async Task Without_a_journal_folder_there_are_no_firsts_and_nothing_is_kept()
    {
        using var watcher = new GameWatcher(Path.Combine(temp, "no journals"), Path.Combine(temp, "no bindings"));
        var firsts = new FirstsLog(watcher.State, Path.Combine(temp, "no journals"), Folder);

        Assert.Empty(await firsts.Systems());
        Assert.False(Directory.Exists(Folder));
    }
}
