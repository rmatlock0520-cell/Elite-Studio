using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EliteStudio.Core;
using EliteStudio.Nav;

namespace EliteStudio.Tests;

/// <summary>
/// The 3D map's detailed map (<see cref="FineMap"/>): Spansh's list of every system, once it is on this PC, read
/// for the block of 3 by 3 columns of sectors round a column. Nothing goes near the internet; the detailed map never
/// does. The list is packed here the way Spansh packs it, from tests\Fixtures\nav:
///   systems-small.json  29 systems in Spansh's own layout: one to a line, "\t{...}," between a first line "[" and a last
///                       line "]" (the last system without its comma). The first two are real lines of Spansh's list of
///                       14 October 2026, far from the block, so they are passed over. The rest are made up round the
///                       owner's block (columns 37 to 39 west to east, 17 to 19 south to north), each numbered from where it
///                       lies the way the game numbers systems: on and just past every edge of the block, on the seams
///                       between sectors and between layers, high above, with and without a main star, a name with a quote
///                       and a letter beyond ASCII, a cube of the game's own (Wredguia WD-K d8) with numbers 0, 1, 3 and 7
///                       known, two systems numbered in another sector than the one they lie in ("Stray In" lies in
///                       Wredguia, numbered in the sector west of it; "Stray Out" lies east of the block, numbered inside
///                       it), one with its fields in another order and another field besides, and two that cannot be read.
/// </summary>
public sealed class FineMapTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>When the owner's download finished, which the detailed map takes for the list's date.</summary>
    static readonly DateTime ListDate = new(2026, 10, 14, 11, 53, 17, DateTimeKind.Utc);

    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioFineMap-").FullName;

    public void Dispose() => Directory.Delete(temp, recursive: true);

    string Fine => Path.Combine(temp, "fine");

    string Source => Path.Combine(temp, FineMap.DownloadName);

    static string Fixture => SpanshStandIn.Recorded("systems-small.json");

    /// <summary>Spansh's list on this PC: packed as Spansh sends it, and dated as the owner's download.</summary>
    void Download(string text, DateTime? date = null)
    {
        using (var file = File.Create(Source))
        using (var packed = new GZipStream(file, CompressionLevel.Optimal))
            packed.Write(new UTF8Encoding(false).GetBytes(text));
        File.SetLastWriteTimeUtc(Source, date ?? ListDate);
    }

    static string[] Names(FineMap map, int x, int y, int z) => [.. map.Systems(x, y, z).Select(system => system.Name)];

    /// <summary>The files the detailed map keeps, with a fingerprint of each, to see that nothing changed.</summary>
    Dictionary<string, string> Snapshot() => Directory.GetFiles(Fine).ToDictionary(file => Path.GetFileName(file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));

    static string Joined(double[] numbers) => string.Join(',', numbers.Select(number => number.ToString(CultureInfo.InvariantCulture)));

    /// <summary>The lines of a packed file, unpacked.</summary>
    static string[] Unpacked(string file)
    {
        using var packed = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
        using var reader = new StreamReader(packed, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    // ------------------------------------------------------------ the block

    [Fact]
    public void The_made_up_systems_are_numbered_as_the_game_numbers_them()
    {
        // The fixture's numbers were worked out by hand from where each system lies: the game's own rule must agree.
        Assert.Equal(10410264907, ProcGenNames.Decode("Wredguia WD-K d8-0")!.Cube.Address);
        Assert.Equal(250928433483, ProcGenNames.Decode("Wredguia WD-K d8-7")!.Cube.Address);
        Assert.Equal(new CubePlace(38, 32, 18, 12, 0, 9, 3, 3), SystemAddress.Place(113489480011));
        // The strays: numbered in the cube on the west sector's east edge, and in the cube on the east edge of the block.
        Assert.Equal(new CubePlace(37, 32, 18, 127, 32, 116, 0, 2), SystemAddress.Place(40406519663520));
        Assert.Equal(new CubePlace(39, 32, 18, 127, 7, 111, 0, 3), SystemAddress.Place(58273580338040));
        Assert.True(SystemAddress.Holds(5222147574688, -1350, 300, 100));
    }

    [Fact]
    public async Task A_block_keeps_every_system_of_its_nine_columns_at_every_height_and_no_other()
    {
        Download(Fixture);
        var map = new FineMap(temp);

        FineBuildAnswer built = await map.Build(38, 18);

        Assert.Equal((null, false, 20L, 9), (built.Problem, built.Stopped, built.Systems, built.Sectors));
        Assert.Equal(
            ["block-38-18.json", "sector-37-32-17.jsonl.gz", "sector-37-32-18.jsonl.gz", "sector-38-31-18.jsonl.gz", "sector-38-32-17.jsonl.gz", "sector-38-32-18.jsonl.gz",
             "sector-38-33-18.jsonl.gz", "sector-38-35-18.jsonl.gz", "sector-39-32-18.jsonl.gz", "sector-39-32-19.jsonl.gz"],
            Directory.GetFiles(Fine).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal));
        Assert.Empty(Directory.GetDirectories(Fine));   // the build's working folder is gone

        // The grid's edges are half-open: a system on a sector's near face is in it, one on its far face in the next.
        Assert.Equal(["West Edge"], Names(map, 37, 32, 17));
        Assert.Equal(["South Edge"], Names(map, 38, 32, 17));
        Assert.Equal(["East Edge", "Stray Out Home"], Names(map, 39, 32, 18));
        Assert.Equal(["North Edge"], Names(map, 39, 32, 19));
        Assert.Equal(["Stray Home"], Names(map, 37, 32, 18));
        Assert.Equal(["Seam Down", "Low \"Two\" Ž"], Names(map, 38, 31, 18));                // just below y -25
        Assert.Equal(["Sphuedu Star", "Sphuedu Name"], Names(map, 38, 33, 18));
        Assert.Equal(["High Above"], Names(map, 38, 35, 18));                                   // every layer, however high
        Assert.Equal(["Wredguia WD-K d8-0", "Wredguia WD-K d8-1", "Wredguia WD-K d8-3", "Wredguia WD-K d8-7", "HIP 2001", "Seam Up", "Stray In", "Order Changed", "Null Star"],
            Names(map, 38, 32, 18));   // HIP 2001 and Stray In lie on the seam with the sector west: they are Wredguia's

        // Nothing past the block's edges, nothing far away, nothing that could not be read.
        string[] all = [.. Directory.GetFiles(Fine, "sector-*").SelectMany(file => Unpacked(file).Skip(1))];
        Assert.Equal(20, all.Length);
        foreach (string outside in new[] { "Past West", "Past East", "Past North", "Past South", "Stray Out\"", "Cygni X-3", "Eephonth", "Broken", "No Coords" })
            Assert.DoesNotContain(all, line => line.Contains(outside, StringComparison.Ordinal));
        Assert.Empty(map.Systems(40, 32, 18));
    }

    [Fact]
    public async Task Each_system_is_kept_on_one_line_with_its_name_number_place_star_and_date_under_a_line_that_says_what_the_file_holds()
    {
        Download(Fixture);
        var map = new FineMap(temp);
        await map.Build(38, 18);

        string[] lines = Unpacked(Path.Combine(Fine, "sector-38-32-18.jsonl.gz"));
        Assert.Equal(10, lines.Length);
        using (JsonDocument heading = JsonDocument.Parse(lines[0]))
        {
            JsonElement first = heading.RootElement;
            Assert.Equal(("[38,32,18]", "Wredguia", 9, "2026-10-14T11:53:17Z", """["name","id64","x","y","z","star","updated"]"""),
                (first.GetProperty("sector").GetRawText(), first.GetProperty("name").GetString(), first.GetProperty("systems").GetInt32(),
                 first.GetProperty("sourceDate").GetString(), first.GetProperty("columns").GetRawText()));
            Assert.InRange(first.GetProperty("built").GetDateTime(), DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(1));
        }
        // The number travels as text; "s": the main star is recorded, "n": the name only; the day Spansh last heard of it.
        Assert.Equal("""["Wredguia WD-K d8-0","10410264907",-350.5,10.25,-300.125,"s","2025-03-01"]""", lines[1]);
        Assert.Equal("""["Null Star","5299456067928",-630,230,-630,"n",null]""", lines[9]);

        FineSystem[] systems = [.. map.Systems(38, 32, 18)];
        Assert.Equal(new FineSystem("Wredguia WD-K d8-1", 44770003275, -340, 20, -290, false, "2024-05-06"), systems[1]);
        // Fields in another order, and one more besides: read all the same.
        Assert.Equal(new FineSystem("Order Changed", 5302676900208, -600, 200, -600, true, "2026-06-01"), systems[7]);
        Assert.Equal(new FineSystem("Low \"Two\" Ž", 5270454159488, -900, -600, -900, false, "2026-01-04"), map.Systems(38, 31, 18).Last());
    }

    [Fact]
    public async Task Counts_come_per_cube_of_160_and_40_ly_with_the_star_split_and_the_gaps_in_the_numbering()
    {
        Download(Fixture);
        var map = new FineMap(temp);
        await map.Build(38, 18);

        // Wredguia: in cubes of 160 ly, layer by layer from the bottom, then south to north, then west to east. Its cube
        // of the game's own WD-K d8 has numbers 0, 1, 3 and 7: so 2, 4, 5 and 6 must exist, counted at that cube's middle.
        FineCubes big = map.Cubes(38, 32, 18);
        Assert.Equal((true, 160, 8, "-1345,-25,-1065", 9, 5, 4, 4L, ListDate),
            (big.Have, big.Size, big.N, Joined(big.Corner), big.Known, big.Star, big.NameOnly, big.Missing, big.SourceDate));
        Assert.Equal(
            [new FineCube(5, 0, 3, 1, 0, 1, 0), new FineCube(6, 0, 4, 4, 2, 2, 4), new FineCube(0, 0, 6, 1, 1, 0, 0), new FineCube(4, 1, 2, 2, 1, 1, 0), new FineCube(0, 2, 7, 1, 1, 0, 0)],
            big.Cubes);
        FineCubes small = map.Cubes(38, 32, 18, FineMap.SmallCubeLy);
        Assert.Equal((40, 32), (small.Size, small.N));
        Assert.Equal(
            [new FineCube(21, 0, 14, 1, 0, 1, 0), new FineCube(24, 0, 18, 1, 1, 0, 0), new FineCube(24, 0, 19, 1, 1, 0, 0), new FineCube(25, 1, 19, 2, 0, 2, 4),
             new FineCube(0, 3, 26, 1, 1, 0, 0), new FineCube(18, 5, 11, 1, 1, 0, 0), new FineCube(17, 6, 10, 1, 0, 1, 0), new FineCube(0, 8, 29, 1, 1, 0, 0)],
            small.Cubes);

        // A system counts where it lies, and its number in the cube its number names: Stray In fills number 2 of a cube
        // in the sector west of Wredguia, whose number 0 is known, so only 1 is missing there; Stray Out, past the block's
        // east edge, fills number 3 of a cube on its edge, so 1 and 2 are missing.
        Assert.Equal([new FineCube(7, 2, 7, 1, 0, 1, 1)], map.Cubes(37, 32, 18).Cubes);
        Assert.Equal([new FineCube(7, 0, 6, 2, 1, 1, 2)], map.Cubes(39, 32, 18).Cubes);

        Assert.Equal(new FineSectorCounts(9, 5, 4, 4), map.Summary(38, 32, 18));
        Assert.Equal(new FineSectorCounts(2, 1, 1, 0), map.Summary(38, 33, 18));
        // A sector of the block with nothing in it is known to be empty; one outside every block is not known at all.
        Assert.Equal(new FineSectorCounts(0, 0, 0, 0), map.Summary(37, 30, 19));
        Assert.Equal((true, 0), (map.Cubes(37, 30, 19).Have, map.Cubes(37, 30, 19).Cubes.Count));
        Assert.Null(map.Summary(40, 32, 18));
        Assert.Equal((false, 0, "-1345,1255,-1065"), (map.Cubes(38, 33, 21).Have, map.Cubes(38, 33, 21).Cubes.Count, Joined(map.Cubes(38, 33, 18).Corner)));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.Cubes(38, 32, 18, 80));
    }

    [Fact]
    public async Task The_status_says_whether_a_map_covers_a_block_when_it_was_built_and_from_which_list()
    {
        var map = new FineMap(temp);
        // No list on this PC and nothing built: nothing to say.
        Assert.Null(map.Status(38, 18));
        Assert.Null(map.Needs());

        Download(Fixture);
        FineStatus before = map.Status(38, 18)!;
        Assert.Equal(new FineStatus(false, null, ListDate, true, 38, 18), before with { Bytes = null, Seconds = 0 });
        Assert.Equal((new FileInfo(Source).Length, 10), (before.Bytes, before.Seconds));
        Assert.Equal(new FineNeeds(10, "0.0 GB on your PC", false), map.Needs());

        DateTime started = DateTime.UtcNow;
        await map.Build(38, 18);
        FineStatus after = map.Status(38, 18)!;
        Assert.Equal((true, ListDate, true, 38, 18, true, false, (FineMapProgress?)null), (after.Built, after.SourceDate, after.Have, after.Ix, after.Iz, after.Exact, after.Newer, after.Running));
        Assert.InRange(after.At!.Value, started.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
        // A block next to it shares six columns: its centre column is covered, by the map built round 38, 18.
        Assert.Equal((true, false, 38, 18), (map.Status(39, 19)!.Built, map.Status(39, 19)!.Exact, map.Status(39, 19)!.Ix, map.Status(39, 19)!.Iz));
        Assert.Equal((false, true, 40, 18), (map.Status(40, 18)!.Built, map.Status(40, 18)!.Have, map.Status(40, 18)!.Ix, map.Status(40, 18)!.Iz));
        Assert.True(map.Covers(37, 17));
        Assert.False(map.Covers(36, 17));

        // The block file says what was read.
        using (JsonDocument block = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fine, "block-38-18.json"))))
        {
            JsonElement file = block.RootElement;
            Assert.Equal((1, 38, 18, "systems.json.gz", new FileInfo(Source).Length, "2026-10-14T11:53:17Z", 29L, 20L, 2L, 2L, 9),
                (file.GetProperty("version").GetInt32(), file.GetProperty("ix").GetInt32(), file.GetProperty("iz").GetInt32(), file.GetProperty("source").GetString(),
                 file.GetProperty("sourceBytes").GetInt64(), file.GetProperty("sourceDate").GetString(), file.GetProperty("lines").GetInt64(), file.GetProperty("systems").GetInt64(),
                 file.GetProperty("unreadable").GetInt64(), file.GetProperty("strays").GetInt64(), file.GetProperty("sectors").GetArrayLength()));
            JsonElement wredguia = file.GetProperty("sectors").EnumerateArray().Single(sector => sector.GetProperty("name").GetString() == "Wredguia");
            Assert.Equal("""{"ix":38,"iy":32,"iz":18,"name":"Wredguia","known":9,"star":5,"missing":4,"c160":[5,0,3,1,0,0,6,0,4,4,2,4,0,0,6,1,1,0,4,1,2,2,1,0,0,2,7,1,1,0],""",
                wredguia.GetRawText()[..wredguia.GetRawText().IndexOf("\"c40\"", StringComparison.Ordinal)]);
        }

        // A newer download: the map says it was built from another list, so the page can offer to build it again.
        File.SetLastWriteTimeUtc(Source, ListDate.AddDays(3));
        Assert.Equal((true, ListDate), (map.Status(38, 18)!.Newer, map.Status(38, 18)!.SourceDate));
    }

    [Fact]
    public async Task A_system_is_looked_up_by_its_number_or_its_generated_name_and_a_gap_is_not_in_the_list()
    {
        Download(Fixture);
        var map = new FineMap(temp);
        Assert.Equal(new FineLookup(false, null, null), map.Lookup("Wredguia WD-K d8-3"));   // nothing built yet
        await map.Build(38, 18);

        Assert.Equal(new FineLookup(true, new FineSystem("Wredguia WD-K d8-3", 113489480011, -306.5, 50, -266, false, "2023-01-02"), ListDate), map.Lookup("wredguia wd-k d8-3"));
        Assert.Equal("Stray Home", map.Lookup(null, 5222147574688).System?.Name);
        Assert.Equal("Stray In", map.Lookup("A name Spansh does not use", 40406519663520).System?.Name);   // its number names the west sector; it lies in Wredguia
        // Number 2 of WD-K d8 must exist, and the list does not have it: a system no database has recorded.
        Assert.Equal(new FineLookup(true, null, ListDate), map.Lookup("Wredguia WD-K d8-2"));
        // Far from the block, the detailed map can say nothing either way (this one is in Spansh's list, 35,000 ly west).
        Assert.Equal(new FineLookup(false, null, null), map.Lookup("Eephonth AA-A h0"));
        Assert.Equal(new FineLookup(false, null, null), map.Lookup("HIP 2001"));   // a catalogue name says nothing of where it is
    }

    [Fact]
    public async Task A_build_says_how_far_it_has_got_and_that_it_is_done()
    {
        Download(Fixture);
        var map = new FineMap(temp);
        var events = new List<FineMapProgress>();
        map.Progress += progress =>
        {
            lock (events) events.Add(progress);
        };

        await map.Build(38, 18);

        Assert.Equal(new FineMapProgress("f:38:18", 38, 18, FineMap.Reading, 0, 0, events[0].Seconds), events[0]);
        Assert.Equal((FineMap.Done, 100.0, 20L), (events[^1].State, events[^1].Percent, events[^1].Systems));
        Assert.All(events, progress => Assert.Contains(progress.State, new[] { FineMap.Reading, FineMap.Writing, FineMap.Done }));
        Assert.Equal(events.Select(progress => progress.Percent).Order(), events.Select(progress => progress.Percent));
        Assert.Contains(events, progress => progress.State == FineMap.Writing);
        // As a page gets it: told from a survey's events by its kind.
        Assert.StartsWith("""{"kind":"finemap","key":"f:38:18","ix":38,"iz":18,"state":"done","percent":100,"systems":20,""",
            JsonSerializer.Serialize(events[^1], new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Null(map.Running);
    }

    // ------------------------------------------------------------ stopping, building again, and what goes wrong

    [Fact]
    public async Task Stopped_while_reading_a_build_leaves_nothing_behind()
    {
        Download(Fixture);
        var map = new FineMap(temp);
        map.Progress += progress =>
        {
            if (progress.State == FineMap.Reading) map.Cancel();
        };

        FineBuildAnswer answer = await map.Build(38, 18);

        Assert.Equal((true, (string?)null, 0L), (answer.Stopped, answer.Problem, answer.Systems));
        Assert.Empty(Directory.GetFileSystemEntries(Fine));   // not even the working files written before it stopped
        Assert.False(answer.Fine!.Built);
        Assert.False(map.Cancel());   // nothing under way any more
    }

    [Fact]
    public async Task Stopped_while_writing_a_rebuild_leaves_the_map_built_before_as_it_was()
    {
        Download(Fixture);
        var map = new FineMap(temp);
        await map.Build(38, 18);
        Dictionary<string, string> before = Snapshot();

        Download(Fixture.Replace("Sphuedu Name", "Sphuedu Renamed", StringComparison.Ordinal), ListDate.AddDays(1));
        map.Progress += progress =>
        {
            if (progress.State == FineMap.Writing) map.Cancel();
        };
        FineBuildAnswer answer = await map.Build(38, 18);

        Assert.True(answer.Stopped);
        Assert.Equal(before.OrderBy(file => file.Key), Snapshot().OrderBy(file => file.Key));
        Assert.Empty(Directory.GetDirectories(Fine));
        Assert.Equal(["Sphuedu Star", "Sphuedu Name"], Names(map, 38, 33, 18));
    }

    [Fact]
    public async Task A_rebuild_puts_the_new_map_in_place_of_the_old_whole()
    {
        Download(Fixture);
        var map = new FineMap(temp);
        await map.Build(38, 18);
        Assert.Equal(new FineSectorCounts(9, 5, 4, 4), map.Summary(38, 32, 18));
        // A sector of another block, outside these nine columns: a rebuild of this block leaves it alone.
        File.WriteAllText(Path.Combine(Fine, "sector-50-32-18.jsonl.gz"), "another block's");

        // A newer list: Sphuedu's two systems are gone, and Wredguia has number 2 of WD-K d8 now.
        long two = ProcGenNames.Decode("Wredguia WD-K d8-2")!.Cube.Address;
        string newer = string.Join('\n', Fixture.Split('\n').Where(line => !line.Contains("Sphuedu", StringComparison.Ordinal)))
            .Replace("\t{\"id64\":113489480011,",
                "\t{\"id64\":" + two.ToString(CultureInfo.InvariantCulture) + ""","name":"Wredguia WD-K d8-2","coords":{"x":-320,"y":30,"z":-280},"updateTime":"2026-10-14T09:00:00Z"},""" + "\n\t{\"id64\":113489480011,",
                StringComparison.Ordinal);
        Download(newer, ListDate.AddDays(2));
        FineBuildAnswer answer = await map.Build(38, 18);

        Assert.Equal((null, 19L, 8), (answer.Problem, answer.Systems, answer.Sectors));
        Assert.Equal((ListDate.AddDays(2), false), (answer.Fine!.SourceDate, answer.Fine.Newer));
        Assert.False(File.Exists(Path.Combine(Fine, "sector-38-33-18.jsonl.gz")));
        Assert.Empty(map.Systems(38, 33, 18));
        Assert.Equal(new FineSectorCounts(0, 0, 0, 0), map.Summary(38, 33, 18));
        Assert.Equal(new FineSectorCounts(10, 5, 5, 3), map.Summary(38, 32, 18));
        Assert.Contains("Wredguia WD-K d8-2", Names(map, 38, 32, 18));
        Assert.Equal("another block's", File.ReadAllText(Path.Combine(Fine, "sector-50-32-18.jsonl.gz")));
        Assert.Empty(Directory.GetDirectories(Fine));
        Assert.Empty(Directory.GetFiles(Fine, "*.tmp"));
    }

    [Fact]
    public async Task A_rebuild_that_could_not_all_be_put_in_place_says_so_and_is_put_in_place_at_the_next_look()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file another program holds open.");
        Download(Fixture);
        var map = new FineMap(temp) { MoveTries = 2, ResumeWait = TimeSpan.Zero };
        await map.Build(38, 18);
        DateTime before = map.Status(38, 18)!.At!.Value;

        // A newer list, with number 2 of WD-K d8 in Wredguia. While the rebuild puts its files in place, another program
        // (a virus scanner, a backup) holds Wredguia's file open, and goes on holding it.
        long two = ProcGenNames.Decode("Wredguia WD-K d8-2")!.Cube.Address;
        Download(Fixture.Replace("\t{\"id64\":113489480011,",
            "\t{\"id64\":" + two.ToString(CultureInfo.InvariantCulture) + ""","name":"Wredguia WD-K d8-2","coords":{"x":-320,"y":30,"z":-280},"updateTime":"2026-10-14T09:00:00Z"},""" + "\n\t{\"id64\":113489480011,",
            StringComparison.Ordinal), ListDate.AddDays(2));
        using (new FileStream(Path.Combine(Fine, "sector-38-32-18.jsonl.gz"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            FineBuildAnswer answer = await map.Build(38, 18);

            // Some sector files are new now and the rest old, beside the old block file: the answer does not say "unchanged".
            Assert.StartsWith("Studio built the new detailed map but could not put all of it in place", answer.Problem);
            Assert.DoesNotContain("unchanged", answer.Problem, StringComparison.Ordinal);
            Assert.False(answer.Stopped);
            // What was not moved waits whole in the build's folder (its block file is there), and is tried again at the
            // next look, which finds the file still held: the block file in place is still the one from before.
            Assert.True(File.Exists(Path.Combine(Fine, "building-38-18", "block-38-18.json")));
            Assert.Equal(before, map.Status(38, 18)!.At);
        }

        // Let go: the next look puts the rest in place, and the new list is the map's.
        FineStatus after = map.Status(38, 18)!;
        Assert.True(after.At > before);
        Assert.Equal((ListDate.AddDays(2), false), (after.SourceDate, after.Newer));
        Assert.Contains("Wredguia WD-K d8-2", Names(map, 38, 32, 18));
        Assert.Equal(new FineSectorCounts(10, 5, 5, 3), map.Summary(38, 32, 18));
        Assert.Empty(Directory.GetDirectories(Fine));
        // Every sector file is the new build's, each as long as the block file says.
        Assert.All(Directory.GetFiles(Fine, "sector-*.jsonl.gz"), file => Assert.Contains("2026-10-16T11:53:17Z", Unpacked(file)[0], StringComparison.Ordinal));
    }

    [Fact]
    public async Task Maps_built_round_neighbouring_columns_are_each_read_once_however_often_their_sectors_are_asked_for()
    {
        Download(Fixture);
        var build = new FineMap(temp);
        foreach ((int x, int z) in new[] { (38, 18), (39, 18), (38, 19) }) Assert.Null((await build.Build(x, z)).Problem);
        var map = new FineMap(temp);   // a new start of Studio: no block file read yet

        // As GET block asks for the block round 38, 18, twice: every sector, layer by layer, south to north, west to east.
        // Each column is read from the newest map that covers it, so the three maps take turns, layer after layer.
        for (int pass = 0; pass < 2; pass++)
            for (int y = Galaxy.FirstNamedLayer; y <= Galaxy.LastNamedLayer; y++)
                for (int z = 17; z <= 19; z++)
                    for (int x = 37; x <= 39; x++)
                        map.Summary(x, y, z);

        Assert.Equal(3, map.BlockReads);
    }

    [Fact]
    public async Task Without_the_list_or_from_a_damaged_one_nothing_is_built_and_the_answer_says_why()
    {
        var map = new FineMap(temp);
        FineBuildAnswer none = await map.Build(38, 18);
        Assert.StartsWith("Studio has no copy of Spansh's list of every system on this PC", none.Problem);
        Assert.Null(none.Fine);
        Assert.False(Directory.Exists(Fine));

        // A good build first: nothing that goes wrong later may take it away.
        Download(Fixture);
        await map.Build(38, 18);
        Dictionary<string, string> built = Snapshot();

        // Cut short in the middle of a packed block, as a download that stopped would be.
        byte[] packed = File.ReadAllBytes(Source);
        File.WriteAllBytes(Source, packed[..(packed.Length / 2)]);
        Assert.StartsWith("Spansh's list on this PC is damaged or cut short", (await map.Build(38, 18)).Problem);
        // Whole as a packed file, but the list's last line is missing.
        Download(Fixture[..Fixture.LastIndexOf(']')]);
        Assert.StartsWith("Spansh's list on this PC is damaged or cut short", (await map.Build(38, 18)).Problem);
        // Not the list at all.
        Download("Hello\nthere\n");
        Assert.StartsWith("The file on this PC is not Spansh's list", (await map.Build(38, 18)).Problem);
        File.WriteAllText(Source, "not packed");
        Assert.StartsWith("Spansh's list on this PC is damaged or cut short", (await map.Build(38, 18)).Problem);

        Assert.Equal(built.OrderBy(file => file.Key), Snapshot().OrderBy(file => file.Key));
        Assert.Empty(Directory.GetDirectories(Fine));
        // Not a column of the grid: turned down before anything starts.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = map.Build(128, 18);
        });
    }

    [Fact]
    public async Task Lines_ending_in_CR_LF_or_with_a_comma_after_the_last_system_read_the_same()
    {
        Download(Fixture.Replace("\n", "\r\n", StringComparison.Ordinal).Replace("\"z\":-630}}\r\n]", "\"z\":-630}},\r\n]", StringComparison.Ordinal));
        var map = new FineMap(temp);

        FineBuildAnswer answer = await map.Build(38, 18);

        Assert.Equal((null, 20L), (answer.Problem, answer.Systems));
        Assert.Equal(new FineSectorCounts(9, 5, 4, 4), map.Summary(38, 32, 18));
        Assert.Equal("Null Star", map.Systems(38, 32, 18).Last().Name);
    }

    [Fact]
    public async Task One_build_runs_at_a_time()
    {
        Download(Fixture);
        var map = new FineMap(temp);
        using var reading = new ManualResetEventSlim();
        using var letGo = new ManualResetEventSlim();
        map.Progress += progress =>
        {
            if (progress is not { State: FineMap.Reading, Percent: 0 }) return;
            reading.Set();
            letGo.Wait(TimeSpan.FromSeconds(30));
        };

        Task<FineBuildAnswer> first = map.Build(38, 18);
        Assert.True(reading.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        // The same block again waits for the same build; another is turned down.
        Assert.Same(first, map.Build(38, 18));
        FineBuildAnswer other = await map.Build(40, 18);
        Assert.Equal("Studio is already building the detailed map of another area. Wait for it to finish, or press Cancel.", other.Problem);
        Assert.Equal(FineMap.Reading, map.Running?.State);
        Assert.Equal(map.Running, map.Status(38, 18)!.Running);
        letGo.Set();

        Assert.Equal((null, 20L), ((await first).Problem, (await first).Systems));
        Assert.False(File.Exists(Path.Combine(Fine, "block-40-18.json")));
    }

    // ------------------------------------------------------------ the owner's own list

    /// <summary>
    /// The owner's own download, read whole: how long a build of one block takes and how big its files come out. It
    /// reads a 6.4 GB file for minutes, so it is skipped unless the environment variable ELITE_STUDIO_FINEMAP_LIST names
    /// Spansh's list on this PC. ELITE_STUDIO_FINEMAP_BLOCK may name the column ("38,18" by default) and
    /// ELITE_STUDIO_FINEMAP_OUT the folder to build into (else a temporary one, deleted afterwards). The list is only
    /// read. Run it on its own to see what it prints:
    /// <code>
    /// $env:ELITE_STUDIO_FINEMAP_LIST = "$env:LOCALAPPDATA\Elite Studio\heatmap\systems.json.gz"
    /// &amp; "$env:USERPROFILE\.elite-studio\build\&lt;checkout folder&gt;\bin\EliteStudio.Tests\debug\EliteStudio.Tests.exe" -method "*Real_the_owners_list*" -showLiveOutput
    /// </code>
    /// </summary>
    [Fact]
    public async Task Real_the_owners_list_builds_one_block()
    {
        string? list = Environment.GetEnvironmentVariable("ELITE_STUDIO_FINEMAP_LIST");
        Assert.SkipUnless(!string.IsNullOrEmpty(list) && File.Exists(list), "Set ELITE_STUDIO_FINEMAP_LIST to Spansh's list of every system on this PC to build one block from it.");
        int[] column = [.. (Environment.GetEnvironmentVariable("ELITE_STUDIO_FINEMAP_BLOCK") ?? "38,18").Split(',').Select(part => int.Parse(part.Trim(), CultureInfo.InvariantCulture))];
        string folder = Environment.GetEnvironmentVariable("ELITE_STUDIO_FINEMAP_OUT") is { Length: > 0 } chosen ? chosen : Path.Combine(temp, "real");
        var map = new FineMap(folder, list);
        double said = -10;
        map.Progress += progress =>
        {
            if (progress.Percent - said < 10 && progress.State is FineMap.Reading or FineMap.Writing) return;
            said = progress.Percent;
            output.WriteLine($"{progress.Seconds,7:0.0} s  {progress.State,-8} {progress.Percent,5:0.0} %  {progress.Systems,10:N0} systems");
        };
        output.WriteLine($"Reading {list} ({new FileInfo(list!).Length:N0} bytes) for the block round {column[0]}, {column[1]}; said to take {map.Needs()!.Seconds} s");
        long memory = Process.GetCurrentProcess().PeakWorkingSet64;

        FineBuildAnswer answer = await map.Build(column[0], column[1]);

        Assert.Null(answer.Problem);
        string fine = Path.Combine(folder, "fine");
        FileInfo[] sectors = [.. new DirectoryInfo(fine).GetFiles("sector-*.jsonl.gz").OrderByDescending(file => file.Length)];
        FileInfo block = new(Path.Combine(fine, $"block-{column[0]}-{column[1]}.json"));
        output.WriteLine($"Built in {answer.Seconds:0.0} s: {answer.Systems:N0} systems in {answer.Sectors} sectors; peak memory of the test program {Process.GetCurrentProcess().PeakWorkingSet64 / 1e6:N0} MB (before: {memory / 1e6:N0} MB)");
        output.WriteLine($"Sector files: {sectors.Sum(file => file.Length) / 1e6:N1} MB in all; the biggest {sectors[0].Name} {sectors[0].Length / 1e6:N1} MB. Block file: {block.Length / 1e6:N2} MB");
        using (JsonDocument kept = JsonDocument.Parse(File.ReadAllText(block.FullName)))
        {
            JsonElement file = kept.RootElement;
            output.WriteLine($"Of {file.GetProperty("seconds").GetDouble():0.0} s before the files were put in place, {file.GetProperty("unpackSeconds").GetDouble():0.0} s went on unpacking the list");
            output.WriteLine($"Spansh's list holds {file.GetProperty("lines").GetInt64():N0} systems; {file.GetProperty("unreadable").GetInt64()} lines near the block unreadable; {file.GetProperty("strays").GetInt64()} numbered in another sector than they lie in");
            var clock = Stopwatch.StartNew();
            FineCubes again = new FineMap(folder, list).Cubes(column[0], 32, column[1], FineMap.SmallCubeLy);
            output.WriteLine($"A fresh start reads the block file and gives a sector's {again.Cubes.Count:N0} cubes of 40 ly in {clock.Elapsed.TotalSeconds:0.00} s");
        }
        foreach (FileInfo file in sectors.Take(6))
        {
            string[] parts = file.Name.Split('-', '.');
            (int x, int y, int z) = (int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture), int.Parse(parts[3], CultureInfo.InvariantCulture));
            var clock = Stopwatch.StartNew();
            int count = map.Systems(x, y, z).Count();
            double read = clock.Elapsed.TotalSeconds;
            FineCubes cubes = map.Cubes(x, y, z);
            output.WriteLine($"  {file.Name,-26} {ProcGenNames.SectorName(x, y, z),-14} {count,9:N0} systems ({cubes.Star:N0} with a star, {cubes.Missing:N0} missing), {file.Length / 1e6,6:N1} MB, read back in {read:0.00} s; {cubes.Cubes.Count} cubes of 160 ly, {map.Cubes(x, y, z, 40).Cubes.Count:N0} of 40 ly");
        }
    }
}
