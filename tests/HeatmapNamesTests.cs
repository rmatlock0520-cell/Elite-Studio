using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using EliteStudio.Core;
using EliteStudio.Nav;

namespace EliteStudio.Tests;

/// <summary>
/// The names under the 3D map (<see cref="HeatmapNames"/>): the systems known in a square, grouped by the game's own
/// cube each belongs to, with the names a player can try in the galaxy map. Nothing goes near the internet: the names
/// come from a survey kept on this PC (made here through <see cref="SpanshStandIn"/>, its answers made up in Spansh's
/// layout) or from the detailed map (built from tests\Fixtures\nav\systems-small.json, see FineMapTests). The systems
/// are real where it matters: the cube of Wredguia WD-K d8 as FineMapTests numbers it, Suhte's cube inside Col 285
/// Sector with the numbers Spansh recorded there (Col 285 Sector VK-N c7-17, c7-18 and c7-23, and Suhte, number 19;
/// see SystemAddressTests), and Eephonth AA-A h0, a real line of Spansh's list of 14 October 2026.
/// </summary>
public sealed class HeatmapNamesTests : IDisposable
{
    const string Wredguia = "s:38:32:18";

    // Real addresses (see SystemAddressTests and FineMapTests).
    const long D8First = 10410264907, D8Second = 44770003275, D8Fourth = 113489480011, D8Eighth = 250928433483;
    const long VkN17 = 4756508545738, VkN18 = 5031386452682, Suhte = 5306264359626, VkN23 = 6405775987402;
    const long Eephonth = 752903;

    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioHeatmapNames-").FullName;
    readonly List<Spansh> clients = [];

    public void Dispose()
    {
        foreach (Spansh client in clients) client.Dispose();
        Directory.Delete(temp, recursive: true);
    }

    HeatmapSurvey Survey(HttpMessageHandler net, string? folder = null)
    {
        var spansh = new Spansh(net);
        clients.Add(spansh);
        return new HeatmapSurvey(spansh, folder ?? temp);
    }

    // ------------------------------------------------------------ answers made up in Spansh's layout

    static string Counted(int count) => $$"""{"count":{{count}},"from":0,"results":[],"size":1}""";

    sealed record Star(string Name, long Id64, double X, double Y, double Z);

    static string Listed(IReadOnlyCollection<Star> stars) => JsonSerializer.Serialize(new
    {
        count = stars.Count,
        from = 0,
        results = stars.Select(star => new Dictionary<string, object?>
        {
            ["bodies"] = new[] { new { name = "a body" } },
            ["body_count"] = 1,
            ["id64"] = star.Id64,
            ["name"] = star.Name,
            ["updated_at"] = "2026-10-14T10:00:00Z",
            ["x"] = star.X,
            ["y"] = star.Y,
            ["z"] = star.Z,
        }),
        size = 500,
    });

    /// <summary>
    /// Wredguia, looked inside: the cube of Wredguia WD-K d8 with 0, 1, 3 and 7 known and a catalogue name numbered 5
    /// in it, and Suhte's cube of Col 285 Sector with 17, 18, Suhte (19) and 23.
    /// </summary>
    static readonly Star[] WredguiaStars =
    [
        new("Wredguia WD-K d8-0", D8First, -350.5, 10.25, -300.125),
        new("Wredguia WD-K d8-1", D8Second, -340, 20, -290),
        new("Wredguia WD-K d8-3", D8Fourth, -306.5, 50, -266),
        new("Wredguia WD-K d8-7", D8Eighth, -384.96875, -24.96875, -344.96875),
        new("HIP 99", SystemAddress.WithNumber(D8First, 5), -350, 12, -301),
        new("Col 285 Sector VK-N c7-23", VkN23, -160, 40, -40),
        new("Col 285 Sector VK-N c7-18", VkN18, -165.5, 35, -45),
        new("Suhte", Suhte, -170, 30, -50),
        new("Col 285 Sector VK-N c7-17", VkN17, -180, 20, -60),
    ];

    /// <summary>Looks inside a sector through Studio's own survey: a count and one page, at once.</summary>
    static async Task Look(HeatmapSurvey survey, SpanshStandIn net, string key, Star[] stars)
    {
        net.Then(Counted(stars.Length)).Then(Listed(stars));
        SurveyAnswer answer = await survey.Survey(key, go: false);
        Assert.True(answer.Area?.Complete, answer.Problem);
    }

    static NameSquare Sector(string key) => new(NameSquare.Sector, SurveyArea.Of(key)!, SurveyArea.Of(key)!.Box);

    static NameGroup Group(NamesAnswer answer, string name) => Assert.Single(answer.Groups, group => group.Group == name);

    static string[] Names(NameGroup group) => [.. group.Names!.Select(named => named.Name)];

    /// <summary>Spansh's list of every system on this PC, packed as Spansh packs it and dated as the owner's download.</summary>
    static void Download(string folder, string text)
    {
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, FineMap.DownloadName);
        using (var output = File.Create(file))
        using (var packed = new GZipStream(output, CompressionLevel.Optimal))
            packed.Write(new UTF8Encoding(false).GetBytes(text));
        File.SetLastWriteTimeUtc(file, new DateTime(2026, 10, 14, 11, 53, 17, DateTimeKind.Utc));
    }

    // ------------------------------------------------------------ from a survey kept on this PC

    [Fact]
    public void The_real_names_used_here_name_the_cubes_their_numbers_name()
    {
        // The same cube by its generated name and by its number, inside a nebula and out: what the grouping relies on.
        Assert.Equal(VkN17, ProcGenNames.Decode("Col 285 Sector VK-N c7-17")!.Cube.Address);
        Assert.Equal(VkN23, ProcGenNames.Decode("Col 285 Sector VK-N c7-23")!.Cube.Address);
        Assert.Equal(D8Eighth, ProcGenNames.Decode("Wredguia WD-K d8-7")!.Cube.Address);
        Assert.Equal(Eephonth, ProcGenNames.Decode("Eephonth AA-A h0")!.Cube.Address);
        Assert.Equal(SystemAddress.Place(VkN18)! with { Number = 19 }, SystemAddress.Place(Suhte));
    }

    [Fact]
    public async Task A_surveyed_sectors_names_are_grouped_by_the_games_cubes_with_the_numbers_known_and_the_names_to_try()
    {
        var net = new SpanshStandIn();
        HeatmapSurvey survey = Survey(net);
        await Look(survey, net, Wredguia, WredguiaStars);
        var names = new HeatmapNames(survey, new FineMap(temp));

        NamesAnswer answer = await names.Names(Sector(Wredguia));

        Assert.Equal(("survey", false, (string?)null), (answer.Source, answer.Truncated, answer.Problem));
        Assert.InRange(answer.At!.Value, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(["Col 285 Sector VK-N c7", "Wredguia WD-K d8"], answer.Groups.Select(group => group.Group));

        // A nebula's cube: named as its known systems are, Suhte (a name of its own) among them by its number.
        NameGroup nebula = Group(answer, "Col 285 Sector VK-N c7");
        Assert.Equal(("c", 4, "17–19, 23", 20L), (nebula.Mass, nebula.Count, nebula.Known, nebula.GapsTotal));
        Assert.Equal([-165.0, 35.0, -45.0], nebula.Centre!);
        Assert.Equal(["Col 285 Sector VK-N c7-17", "Col 285 Sector VK-N c7-18", "Suhte", "Col 285 Sector VK-N c7-23"], Names(nebula));
        Assert.Equal("Col 285 Sector VK-N c7-0", nebula.Gaps[0]);
        Assert.Equal(["Col 285 Sector VK-N c7-16", "Col 285 Sector VK-N c7-20", "Col 285 Sector VK-N c7-21", "Col 285 Sector VK-N c7-22"], nebula.Gaps.TakeLast(4));
        Assert.Equal(20, nebula.Gaps.Count);
        Assert.Equal(["Col 285 Sector VK-N c7-24", "Col 285 Sector VK-N c7-25", "Col 285 Sector VK-N c7-26"], nebula.Next);
        Assert.Equal(new ListedName("Suhte", Suhte, -170, 30, -50), nebula.Names![2]);

        // The grid's cube, with a catalogue name in it by its number (5): only the last number changes in the names to try.
        NameGroup cube = Group(answer, "Wredguia WD-K d8");
        Assert.Equal(("d", 5, "0, 1, 3, 5, 7", 3L), (cube.Mass, cube.Count, cube.Known, cube.GapsTotal));
        Assert.Equal(["Wredguia WD-K d8-0", "Wredguia WD-K d8-1", "Wredguia WD-K d8-3", "HIP 99", "Wredguia WD-K d8-7"], Names(cube));
        Assert.Equal(["Wredguia WD-K d8-2", "Wredguia WD-K d8-4", "Wredguia WD-K d8-6"], cube.Gaps);
        Assert.Equal(["Wredguia WD-K d8-8", "Wredguia WD-K d8-9", "Wredguia WD-K d8-10"], cube.Next);
        Assert.Equal([-345.0, 15.0, -305.0], cube.Centre!);

        // The answer's words, as the page reads them: numbers as text, nothing left out.
        string json = JsonSerializer.Serialize(answer, App.Settings.Json);
        Assert.Contains("\"id64\": \"10410264907\"", json, StringComparison.Ordinal);
        Assert.Contains("\"gapsTotal\": 3", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cube_lists_only_its_own_names_but_knows_its_game_cubes_numbers_from_the_whole_sector()
    {
        var net = new SpanshStandIn();
        HeatmapSurvey survey = Survey(net);
        await Look(survey, net, Wredguia, WredguiaStars);
        var names = new HeatmapNames(survey, new FineMap(temp));

        // A cube of 40 ly that holds Wredguia WD-K d8-0 and HIP 99 only.
        var square = new NameSquare(NameSquare.Cube, SurveyArea.Of(Wredguia)!, new Box(-385, -25, -305, -345, 15, -265));
        NamesAnswer answer = await names.Names(square);

        NameGroup cube = Assert.Single(answer.Groups);
        Assert.Equal(("Wredguia WD-K d8", 2, "0, 1, 3, 5, 7"), (cube.Group, cube.Count, cube.Known));
        Assert.Equal(["Wredguia WD-K d8-0", "HIP 99"], Names(cube));
        // The numbers known elsewhere in the sector are no gaps.
        Assert.Equal(["Wredguia WD-K d8-2", "Wredguia WD-K d8-4", "Wredguia WD-K d8-6"], cube.Gaps);
        Assert.Single(net.Asked, asked => asked.Contains("\"size\":500", StringComparison.Ordinal));   // the one page of the survey: nothing asked for the names
    }

    [Fact]
    public async Task A_cube_of_the_heaviest_stars_has_no_number_before_its_systems_number()
    {
        // Eephonth AA-A h0: a cube as big as its sector, whose first number is 0 and left out of the name.
        (int x, int y, int z) = Galaxy.SectorOf(-35713.78125, -135.03125, 17712.15625);
        string key = SurveyArea.SectorKey(x, y, z);
        var net = new SpanshStandIn();
        HeatmapSurvey survey = Survey(net);
        await Look(survey, net, key,
        [
            new("Eephonth AA-A h0", Eephonth, -35713.78125, -135.03125, 17712.15625),
            new("Eephonth AA-A h2", SystemAddress.WithNumber(Eephonth, 2), -35000, -100, 17000),
        ]);

        NamesAnswer answer = await new HeatmapNames(survey, new FineMap(temp)).Names(Sector(key));

        NameGroup cube = Assert.Single(answer.Groups);
        Assert.Equal(("Eephonth AA-A h", "h", "0, 2"), (cube.Group, cube.Mass, cube.Known));
        Assert.Equal(["Eephonth AA-A h1"], cube.Gaps);
        Assert.Equal(["Eephonth AA-A h3", "Eephonth AA-A h4", "Eephonth AA-A h5"], cube.Next);
    }

    [Fact]
    public async Task A_listing_that_stopped_part_way_gives_its_names_but_no_numbers_to_try()
    {
        // 600 systems take a count and two pages; the second page fails, so the listing stops part way.
        var stars = Enumerable.Range(0, 500).Select(i => new Star($"Wredguia WD-K d8-{i}", SystemAddress.WithNumber(D8First, i), -345, 15, -305)).ToArray();
        string firstPage = Listed(stars).Replace("\"count\":500", "\"count\":600", StringComparison.Ordinal);
        var net = new SpanshStandIn().Then(Counted(600)).Then(firstPage);
        HeatmapSurvey survey = Survey(net);
        SurveyAnswer stopped = await survey.Survey(Wredguia, go: true);
        Assert.False(stopped.Area!.Complete);

        NamesAnswer answer = await new HeatmapNames(survey, new FineMap(temp)).Names(Sector(Wredguia));

        NameGroup cube = Assert.Single(answer.Groups);
        Assert.Equal(("survey", 500, "0–499", 0L), (answer.Source, cube.Count, cube.Known, cube.GapsTotal));
        Assert.Empty(cube.Gaps);
        Assert.Empty(cube.Next);   // 500 may be on the page that never came
    }

    // ------------------------------------------------------------ the 5,000-name rule

    [Fact]
    public async Task A_square_with_too_many_names_sends_its_cubes_without_names_and_each_cube_in_full_when_asked()
    {
        var net = new SpanshStandIn();
        HeatmapSurvey survey = Survey(net);
        await Look(survey, net, Wredguia, WredguiaStars);
        var fine = new FineMap(temp);
        Assert.Equal(5000, HeatmapNames.MostNames);
        Assert.Equal(5000, new HeatmapNames(survey, fine).Most);

        // 9 names and 29 to try (20 + 3 gaps, 3 + 3 next) are more than 30: no names, but the names to try still fit.
        NamesAnswer answer = await new HeatmapNames(survey, fine) { Most = 30 }.Names(Sector(Wredguia));
        Assert.True(answer.Truncated);
        Assert.All(answer.Groups, group => Assert.Null(group.Names));
        Assert.Equal([20, 3], answer.Groups.Select(group => group.Gaps.Count));
        Assert.Equal([4, 5], answer.Groups.Select(group => group.Count));

        // Fewer still: not even the names to try fit, so only how many must exist.
        answer = await new HeatmapNames(survey, fine) { Most = 10 }.Names(Sector(Wredguia));
        Assert.True(answer.Truncated);
        Assert.All(answer.Groups, group => Assert.Empty(group.Gaps));
        Assert.All(answer.Groups, group => Assert.Empty(group.Next));
        Assert.Equal([20L, 3L], answer.Groups.Select(group => group.GapsTotal));

        // One cube, asked for by its name (capitals do not matter): its names to try in full, and its names beside them.
        var names = new HeatmapNames(survey, fine) { Most = 30 };
        NamesAnswer one = await names.Names(Sector(Wredguia), "col 285 sector vk-n C7");
        NameGroup nebula = Assert.Single(one.Groups);
        Assert.Equal(("Col 285 Sector VK-N c7", 20, 3, false), (nebula.Group, nebula.Gaps.Count, nebula.Next.Count, one.Truncated));
        Assert.Equal(4, nebula.Names!.Count);
        // Where they do not all fit beside the names to try, as many as do, and the answer says it is cut short.
        one = await new HeatmapNames(survey, fine) { Most = 25 }.Names(Sector(Wredguia), "Col 285 Sector VK-N c7");
        Assert.Equal((2, true), (one.Groups[0].Names!.Count, one.Truncated));
        // A cube the square does not hold.
        one = await names.Names(Sector(Wredguia), "Wredguia AA-A h0");
        Assert.Equal((0, "No cube by that name is listed in this square."), (one.Groups.Count, one.Problem));
    }

    [Fact]
    public async Task A_square_with_more_cubes_than_an_answer_lists_sends_those_nearest_the_focus_with_the_whole_squares_totals()
    {
        var net = new SpanshStandIn();
        HeatmapSurvey survey = Survey(net);
        await Look(survey, net, Wredguia, WredguiaStars);
        var fine = new FineMap(temp);
        Assert.Equal(2000, HeatmapNames.MostGroups);
        Assert.Equal(2000, new HeatmapNames(survey, fine).MostCubes);
        // One cube to an answer, where Wredguia (here) has two of the game's cubes: as a busy sector's 150,000 are to 2,000.
        var names = new HeatmapNames(survey, fine) { MostCubes = 1 };

        // The one nearest the camera's focus, with its names (they fit), and the totals of the whole square for the heading.
        NamesAnswer answer = await names.Names(Sector(Wredguia), near: (-345, 15, -305));
        NameGroup only = Assert.Single(answer.Groups);
        Assert.Equal(("Wredguia WD-K d8", 5, false), (only.Group, only.Names!.Count, answer.Truncated));
        Assert.Equal(new NameTotals(2, 9, 23), answer.Totals);
        Assert.Equal("Col 285 Sector VK-N c7", Assert.Single((await names.Names(Sector(Wredguia), near: (-170, 30, -50))).Groups).Group);
        // With no focus given, the square's middle (-705, 615, -425), which is nearer the cube of WD-K d8.
        Assert.Equal("Wredguia WD-K d8", Assert.Single((await names.Names(Sector(Wredguia))).Groups).Group);
        // A cube left out of the answer is still there to ask for by its name (a full name typed in the page's filter).
        NamesAnswer one = await names.Names(Sector(Wredguia), "Col 285 Sector VK-N c7");
        Assert.Equal((4, 20), (Assert.Single(one.Groups).Names!.Count, one.Groups[0].Gaps.Count));

        // Room for both: both, and the same totals.
        answer = await new HeatmapNames(survey, fine).Names(Sector(Wredguia), near: (-345, 15, -305));
        Assert.Equal((2, new NameTotals(2, 9, 23)), (answer.Groups.Count, answer.Totals));
        Assert.Contains("\"totals\": {", JsonSerializer.Serialize(answer, App.Settings.Json), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ from the detailed map

    [Fact]
    public async Task A_cube_of_the_sector_listed_last_comes_from_what_was_read_for_the_sector_not_from_its_file_again()
    {
        Download(temp, SpanshStandIn.Recorded("systems-small.json"));
        var fine = new FineMap(temp);
        Assert.Null((await fine.Build(38, 18)).Problem);
        HeatmapSurvey survey = Survey(new SpanshStandIn());
        // A cube of 40 ly in Wredguia that holds Wredguia WD-K d8-0, as a square read on its own from the sector's file.
        var cube = new NameSquare(NameSquare.Cube, SurveyArea.Of(Wredguia)!, new Box(-385, -25, -305, -345, 15, -265));
        NamesAnswer alone = await new HeatmapNames(survey, fine).Names(cube);
        Assert.Equal(["Wredguia WD-K d8-0"], Names(Assert.Single(alone.Groups)));

        var names = new HeatmapNames(survey, fine);
        Assert.Equal(9, (await names.Names(Sector(Wredguia))).Totals!.Count);
        // The sector's file spoiled since: read again, it could not be; taken from the sector read, the cube is as before.
        File.WriteAllText(Path.Combine(temp, "fine", "sector-38-32-18.jsonl.gz"), "spoiled");
        NamesAnswer taken = await names.Names(cube);

        Assert.Equal(JsonSerializer.Serialize(alone, App.Settings.Json), JsonSerializer.Serialize(taken, App.Settings.Json));
        // Its own cube in full too, from the same.
        Assert.Equal(["Wredguia WD-K d8-2", "Wredguia WD-K d8-4", "Wredguia WD-K d8-5", "Wredguia WD-K d8-6"], (await names.Names(cube, "Wredguia WD-K d8")).Groups[0].Gaps);
        // Read from the file again (a new start of Studio), the cube finds it spoiled: so it was not read just now.
        Assert.StartsWith("The detailed map of this area could not be read", (await new HeatmapNames(survey, fine).Names(cube)).Problem);
    }

    [Fact]
    public async Task A_busy_sectors_names_come_from_the_detailed_map_and_a_survey_wins_where_both_are()
    {
        Download(temp, SpanshStandIn.Recorded("systems-small.json"));
        var fine = new FineMap(temp);
        Assert.Null((await fine.Build(38, 18)).Problem);
        var net = new SpanshStandIn();
        HeatmapSurvey survey = Survey(net);
        var names = new HeatmapNames(survey, fine);

        NamesAnswer answer = await names.Names(Sector(Wredguia));

        Assert.Equal(("detailed", new DateTime(2026, 10, 14, 11, 53, 17, DateTimeKind.Utc)), (answer.Source, answer.SourceDate));
        NameGroup cube = Group(answer, "Wredguia WD-K d8");
        Assert.Equal((4, "0, 1, 3, 7", 4L), (cube.Count, cube.Known, cube.GapsTotal));
        Assert.Equal(["Wredguia WD-K d8-0", "Wredguia WD-K d8-1", "Wredguia WD-K d8-3", "Wredguia WD-K d8-7"], Names(cube));
        Assert.Equal(["Wredguia WD-K d8-2", "Wredguia WD-K d8-4", "Wredguia WD-K d8-5", "Wredguia WD-K d8-6"], cube.Gaps);
        Assert.Equal(["Wredguia WD-K d8-8", "Wredguia WD-K d8-9", "Wredguia WD-K d8-10"], cube.Next);
        Assert.Equal(new ListedName("Wredguia WD-K d8-0", D8First, -350.5, 10.25, -300.125), cube.Names![0]);
        // Every system of the sector is listed somewhere; catalogue names are named by the game's rule for their cube.
        Assert.Equal(9, answer.Groups.Sum(group => group.Count));
        // "Stray In" lies in Wredguia but is numbered in the sector west of it, which was not read: no numbers to try there.
        NameGroup stray = Assert.Single(answer.Groups, group => group.Names!.Any(named => named.Name == "Stray In"));
        Assert.Equal((0L, 0, 0), (stray.GapsTotal, stray.Gaps.Count, stray.Next.Count));
        Assert.Equal(ProcGenNames.SystemName(SystemAddress.Place(40406519663520)! with { Number = 0 })![..^2], stray.Group);

        // Looked inside since: the survey (which knows the bodies too) is the one shown.
        await Look(survey, net, Wredguia, WredguiaStars);
        answer = await names.Names(Sector(Wredguia));
        Assert.Equal(("survey", "0, 1, 3, 5, 7"), (answer.Source, Group(answer, "Wredguia WD-K d8").Known));
    }

    [Fact]
    public async Task A_system_the_list_gives_no_number_is_grouped_by_its_name()
    {
        Download(temp,
            """
            [
            	{"id64":10410264907,"name":"Wredguia WD-K d8-0","coords":{"x":-350.5,"y":10.25,"z":-300.125},"updateTime":"2025-03-01T10:00:00Z"},
            	{"name":"Wredguia WD-K d8-4","coords":{"x":-345,"y":15,"z":-305},"updateTime":"2026-10-01T00:00:00Z"}
            ]
            """);
        var fine = new FineMap(temp);
        Assert.Null((await fine.Build(38, 18)).Problem);

        NamesAnswer answer = await new HeatmapNames(Survey(new SpanshStandIn()), fine).Names(Sector(Wredguia));

        NameGroup cube = Assert.Single(answer.Groups);
        Assert.Equal(("Wredguia WD-K d8", "0, 4"), (cube.Group, cube.Known));
        Assert.Equal(["Wredguia WD-K d8-1", "Wredguia WD-K d8-2", "Wredguia WD-K d8-3"], cube.Gaps);
        Assert.Equal(0, cube.Names![1].Id64);
    }

    [Fact]
    public async Task With_nothing_kept_there_are_no_names_and_nothing_is_asked()
    {
        var net = new SpanshStandIn();
        var names = new HeatmapNames(Survey(net), new FineMap(temp));

        NamesAnswer answer = await names.Names(new NameSquare(NameSquare.Nebula, SurveyArea.Of("n:Col 285 Sector")!, SurveyArea.Of("n:Col 285 Sector")!.Box));

        Assert.Equal(((string?)null, 0, false, (string?)null), (answer.Source, answer.Groups.Count, answer.Truncated, answer.Problem));
        Assert.Empty(net.Asked);
    }
}
