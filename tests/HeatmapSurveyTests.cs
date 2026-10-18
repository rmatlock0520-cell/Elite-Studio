using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using EliteStudio.Core;
using EliteStudio.Nav;

namespace EliteStudio.Tests;

/// <summary>
/// The 3D map's questions to Spansh (<see cref="HeatmapSurvey"/>): looking inside a sector or a
/// nebula, counting single cubes of a busy sector, and the rules that keep the map a careful guest.
/// No test goes near the internet: <see cref="SpanshStandIn"/> answers in Spansh's own layout. The
/// answers are made up here, with systems placed and numbered by hand (each made-up system's number
/// is built from where it lies, the way the game builds it), except for one recorded answer:
///   systems-box.json  Spansh's answer of 14 October 2026 with the first 5 of the 131 systems it
///                     knew in Systoodou's sector, byte for byte (see NavSpanshTests)
/// </summary>
public sealed class HeatmapSurveyTests : IDisposable
{
    const string Sphuedu = "s:38:33:18";     // the sector above the player's; 2,521 systems known to Spansh in October 2026
    const string Systoodou = "s:38:33:17";   // north of it; 131 known
    const string Wredguia = "s:38:32:18";    // the player's own sector

    /// <summary>The owner's commander, in Wredguia.</summary>
    static readonly (double X, double Y, double Z) Player = (-167.09, 26.72, -55.38);

    const string Unreachable = "Spansh could not be reached. Check your internet connection and try again.";

    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioHeatmap-").FullName;
    readonly List<Spansh> clients = [];

    public void Dispose()
    {
        foreach (Spansh client in clients) client.Dispose();
        Directory.Delete(temp, recursive: true);
    }

    /// <summary>The 3D map's side of things on a fresh start of Studio, keeping its results in this test's own folder.</summary>
    HeatmapSurvey New(HttpMessageHandler net)
    {
        var spansh = new Spansh(net);
        clients.Add(spansh);
        return new HeatmapSurvey(spansh, temp);
    }

    // ------------------------------------------------------------ answers made up in Spansh's layout

    /// <summary>What Spansh answers to a count (a search for one system), as far as Studio reads it.</summary>
    static string Counted(int count) => $$"""{"count":{{count}},"from":0,"results":[],"size":1}""";

    /// <summary>A made-up system, with the bodies its discovery scan reported (null: never reported) and those the databases hold.</summary>
    sealed record Star(string Name, long Id64, double X, double Y, double Z, int? BodyCount = 1, int Bodies = 1);

    /// <summary>A page of a box listing in Spansh's layout, for a box holding <paramref name="count"/> systems.</summary>
    static string Listed(int count, IEnumerable<Star> stars) => JsonSerializer.Serialize(new
    {
        count,
        from = 0,
        results = stars.Select(star => new Dictionary<string, object?>
        {
            ["bodies"] = Enumerable.Repeat(new { name = "a body", rings = Array.Empty<object>() }, star.Bodies),
            ["body_count"] = star.BodyCount,
            ["id64"] = star.Id64,
            ["name"] = star.Name,
            ["updated_at"] = "2026-10-14T10:00:00Z",
            ["x"] = star.X,
            ["y"] = star.Y,
            ["z"] = star.Z,
        }),
        size = 500,
    });

    /// <summary>The game's number for the system with this number in the cube of this mass code (0 to 7 for a to h) that holds the point.</summary>
    static long Address(double x, double y, double z, int mass, long number)
    {
        (int sx, int sy, int sz) = Galaxy.SectorOf(x, y, z);
        Box sector = Galaxy.SectorBounds(sx, sy, sz);
        double side = Galaxy.CubeLy(mass);
        return new CubePlace(sx, sy, sz, (int)Math.Floor((x - sector.X0) / side), (int)Math.Floor((y - sector.Y0) / side), (int)Math.Floor((z - sector.Z0) / side), mass, number).Address;
    }

    /// <summary>
    /// Systems "Star N" from N = <paramref name="from"/> on, all at one point (by default in
    /// Sphuedu's corner, low in the south-west), numbered in their cube of 20 ly as they are named.
    /// </summary>
    static IEnumerable<Star> Stars(int from, int count, double x = -1340, double y = 1260, double z = -1060) =>
        Enumerable.Range(from, count).Select(i => new Star($"Star {i}", Address(x, y, z, 1, i), x, y, z));

    /// <summary>Page <paramref name="page"/> of a listing of that many such systems, holding as many as Spansh would send.</summary>
    static string Page(int count, int page) => Listed(count, Stars(page * Spansh.PageSize, Math.Clamp(count - page * Spansh.PageSize, 0, Spansh.PageSize)));

    const string SearchCall = "POST /api/systems/search ";

    /// <summary>The page each listing request asked for, in order (counts, which ask for one system, are left out).</summary>
    static int[] PagesAsked(SpanshStandIn net) =>
    [
        .. net.Asked.Where(asked => asked.StartsWith(SearchCall, StringComparison.Ordinal))
            .Select(asked => JsonNode.Parse(asked[SearchCall.Length..])!)
            .Where(search => (int)search["size"]! == Spansh.PageSize)
            .Select(search => (int)search["page"]!),
    ];

    static (int Known, int Full, int Part, int Never, long Missing) Totals(AreaView area) =>
        (area.Cubes.Sum(cube => cube.Known), area.Cubes.Sum(cube => cube.Full), area.Cubes.Sum(cube => cube.Part), area.Cubes.Sum(cube => cube.Never), area.Cubes.Sum(cube => cube.Missing));

    // ------------------------------------------------------------ looking inside a sector

    [Fact]
    public async Task A_listing_that_takes_more_than_five_requests_is_counted_and_then_waits_for_the_players_go()
    {
        var net = new SpanshStandIn().Then(Counted(2521));
        for (int page = 0; page < 6; page++) net.Then(Page(2521, page));
        HeatmapSurvey survey = New(net);

        SurveyAnswer asked = await survey.Survey(Sphuedu, go: false);

        Assert.Equal(new SurveyNeeds(6, 1.16, 199), asked.Needs);   // about 0.47 KB a system on the wire
        Assert.Equal((Sphuedu, 2521, false, (AreaView?)null, (string?)null, false), (asked.Key, asked.Count, asked.Capped, asked.Area, asked.Problem, asked.Stopped));
        Assert.Single(net.Asked);
        // The count is kept at once, not yet a list: it is never asked for twice, even when the player says no or
        // closes Studio before saying yes.
        AreaView counted = (await survey.View(Sphuedu))!;
        Assert.Equal((2521, false, false, 0, 6, 0), (counted.Count, counted.Capped, counted.Complete, counted.PagesDone, counted.PagesTotal, counted.Systems.Count));
        Assert.Empty(counted.Cubes);
        Assert.Equal((2521, false, SurveySummary.Counted), (survey.Summary(Sphuedu)!.Count, survey.Summary(Sphuedu)!.Capped, survey.Summary(Sphuedu)!.State));
        Assert.Equal(new SurveyNeeds(6, 1.16, 200), (await New(net).Survey(Sphuedu, go: false)).Needs);   // after a restart: asked first, not counted again
        Assert.Single(net.Asked);

        SurveyAnswer listed = await survey.Survey(Sphuedu, go: true);

        Assert.Equal([0, 1, 2, 3, 4, 5], PagesAsked(net));
        Assert.Equal(7, net.Asked.Count);   // the count made for the question is the one listed from
        Assert.Equal(7, survey.Used);
        AreaView area = listed.Area!;
        Assert.Equal((2521, false, true, 6, 6), (area.Count, area.Capped, area.Complete, area.PagesDone, area.PagesTotal));
        Assert.Equal(2521, area.Systems.Count);
        Assert.Equal(new SurveyCube(0, 0, 0, 2521, 2521, 0, 0, 0), Assert.Single(area.Cubes));
        Assert.Equal((160.0, 8, 1280.0, 0L, 0), (area.CubeLy, area.N, area.Size, area.MissingTotal, area.Unlisted));
        Assert.Equal([-1345.0, 1255, -1065], area.Corner);
        Assert.Equal(SurveySummary.Complete, survey.Summary(Sphuedu)!.State);

        // "Survey again" counts afresh. 2,000 systems take four more requests, five in all: those go
        // at once, and the new listing takes the old one's place.
        net.Then(Counted(2000));
        for (int page = 0; page < 4; page++) net.Then(Page(2000, page));
        SurveyAnswer again = await survey.Survey(Sphuedu, go: false);

        Assert.Null(again.Needs);
        Assert.Equal((2000, true, 2000), (again.Area!.Count, again.Area.Complete, again.Area.Systems.Count));
        Assert.Equal(7 + 5, net.Asked.Count);
        Assert.Equal(2000, (await survey.View(Sphuedu))!.Systems.Count);

        // 2,001 would take five more, six in all: the player is asked first.
        net.Then(Counted(2001));
        Assert.Equal(new SurveyNeeds(5, 0.92, 200 - 13), (await survey.Survey(Sphuedu, go: false)).Needs);
        Assert.Equal(7 + 5 + 1, net.Asked.Count);
        AreaView kept = (await survey.View(Sphuedu))!;
        Assert.Equal(2000, kept.Systems.Count);   // the listing kept stays until the new one is complete
        Assert.Equal((0, 5), (kept.Renewing!.PagesDone, kept.Renewing.PagesTotal));   // the new count is kept beside it
    }

    [Fact]
    public async Task Pages_are_put_together_by_number_and_a_system_sent_on_two_pages_is_kept_once()
    {
        // Spansh sorts by system number, but loosely: three systems of the first page come again on the second.
        var net = new SpanshStandIn().Then(Counted(600)).Then(Listed(600, Stars(0, 500))).Then(Listed(600, Stars(497, 3).Concat(Stars(500, 97))));
        HeatmapSurvey survey = New(net);

        AreaView area = (await survey.Survey(Sphuedu, go: false)).Area!;   // two requests: no need to ask

        Assert.Equal([0, 1], PagesAsked(net));
        Assert.True(area.Complete);
        Assert.Equal(597, area.Systems.Count);
        Assert.Equal(597, area.Systems.DistinctBy(system => system.Id64).Count());
        Assert.Equal(new SurveyCube(0, 0, 0, 597, 597, 0, 0, 0), Assert.Single(area.Cubes));
        Assert.Equal(0, area.MissingTotal);   // numbers 0 to 596 in their cube: none left out
        // Spansh counted 600, and every page held the rows it should: three it knows never came, so the page can say
        // that up to three of any "missing" systems may be known after all.
        Assert.Equal(3, area.Unlisted);
    }

    [Fact]
    public async Task Systems_are_sorted_into_cubes_by_where_they_are_and_a_system_on_a_face_is_in_the_cube_beyond_it()
    {
        Star[] edges =
        [
            new("The corner", Address(-1345, 1255, -1065, 0, 0), -1345, 1255, -1065),
            new("Just short of the faces", Address(-1185.03125, 1414.96875, -905.03125, 0, 0), -1185.03125, 1414.96875, -905.03125),
            new("On the faces", Address(-1185, 1415, -905, 0, 0), -1185, 1415, -905),
            new("The last place in the sector", Address(-65.03125, 2534.96875, 214.96875, 0, 0), -65.03125, 2534.96875, 214.96875),
        ];
        var net = new SpanshStandIn().Then(Counted(4)).Then(Listed(4, edges));
        HeatmapSurvey survey = New(net);

        AreaView at160 = (await survey.Survey(Sphuedu, go: false)).Area!;
        AreaView at80 = (await survey.View(Sphuedu, 80))!;
        AreaView at320 = (await survey.View(Sphuedu, 320))!;

        Assert.Equal([(0, 0, 0, 2), (1, 1, 1, 1), (7, 7, 7, 1)], at160.Cubes.Select(cube => (cube.I, cube.J, cube.K, cube.Known)));
        Assert.Equal([(0, 0, 0, 1), (1, 1, 1, 1), (2, 2, 2, 1), (15, 15, 15, 1)], at80.Cubes.Select(cube => (cube.I, cube.J, cube.K, cube.Known)));
        Assert.Equal([(0, 0, 0, 3), (3, 3, 3, 1)], at320.Cubes.Select(cube => (cube.I, cube.J, cube.K, cube.Known)));
        Assert.Equal((8, 16, 4), (at160.N, at80.N, at320.N));
        Assert.Equal(2, net.Asked.Count);   // the other sizes come from what is kept, without asking

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => survey.View(Sphuedu, 100));   // not a size the game's cubes come in
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => survey.View(Sphuedu, 5));
    }

    [Fact]
    public async Task Each_system_is_fully_recorded_partly_recorded_or_never_scanned()
    {
        // Spansh's real rows, told the box holds just these five: two never had their discovery scan reported.
        JsonNode real = JsonNode.Parse(SpanshStandIn.Recorded("systems-box.json"))!;
        real["count"] = 5;
        long Number(int i) => Address(-1300, 1300, -1000, 0, i);
        Star[] made =
        [
            new("All there", Number(0), -1300, 1300, -1000, BodyCount: 5, Bodies: 5),
            new("More than the scan said", Number(1), -1300, 1300, -1000, BodyCount: 5, Bodies: 7),
            new("Some there", Number(2), -1300, 1300, -1000, BodyCount: 5, Bodies: 2),
            new("Never scanned", Number(3), -1300, 1300, -1000, BodyCount: null, Bodies: 9),
            new("Nothing to find", Number(4), -1300, 1300, -1000, BodyCount: 0, Bodies: 0),
        ];
        var net = new SpanshStandIn().Then(Counted(5)).Then(real.ToJsonString()).Then(Counted(5)).Then(Listed(5, made));
        HeatmapSurvey survey = New(net);

        AreaView systoodou = (await survey.Survey(Systoodou, go: false)).Area!;
        AreaView sphuedu = (await survey.Survey(Sphuedu, go: false)).Area!;

        Assert.Equal(
            [("HD 80711", "never"), ("HIP 44601", "full"), ("HD 237948", "full"), ("HD 74904", "never"), ("HD 75229", "full")],
            systoodou.Systems.Select(system => (system.Name, system.State)));
        Assert.Equal((5, 3, 0, 2), (Totals(systoodou).Known, Totals(systoodou).Full, Totals(systoodou).Part, Totals(systoodou).Never));
        Assert.Equal(["full", "full", "part", "never", "full"], sphuedu.Systems.Select(system => system.State));
        Assert.Equal(new SurveyCube(0, 0, 0, 5, 3, 1, 1, 0), Assert.Single(sphuedu.Cubes));

        // To a page the number goes as text, so that no digit of it is lost on the way.
        Assert.Contains("\"id64\": \"10242754787\"", JsonSerializer.Serialize(systoodou.Systems[0], EliteStudio.App.Settings.Json));
    }

    [Fact]
    public async Task Systems_that_must_exist_are_found_in_the_gaps_of_the_numbering_once_the_listing_is_complete()
    {
        // In the player's sector: one cube of 80 ly with numbers 0, 2 and 5 known (so 1, 3 and 4 must exist
        // too), the sector's one cube of 1,280 ly with only number 3 known under a catalogue name, and a
        // cube of 40 ly whose 0, 1 and 2 are all known.
        DecodedName d8 = ProcGenNames.Decode("Wredguia WD-K d8-65")!;
        long D8(long number) => SystemAddress.WithNumber(d8.Cube.Address, number);
        CubePlace c23 = SystemAddress.Place(5031386452682)!;   // Wredguia XD-I c23-18
        (double cx, double cy, double cz) = c23.Centre;
        Star[] known =
        [
            new("Wredguia WD-K d8-0", D8(0), -340, 10, -300),
            new("Wredguia WD-K d8-2", D8(2), -350, 20, -310),
            new("HIP 99999", D8(5), -345, 15, -305),   // a catalogue name; its number counts all the same
            new("HD 1", ProcGenNames.Decode("Wredguia AA-A h3")!.Cube.Address, -700, 600, -400),
            new("Wredguia XD-I c23-0", SystemAddress.WithNumber(c23.Address, 0), cx, cy, cz),
            new("Wredguia XD-I c23-1", SystemAddress.WithNumber(c23.Address, 1), cx, cy, cz),
            new("Wredguia XD-I c23-2", SystemAddress.WithNumber(c23.Address, 2), cx, cy, cz),
        ];
        // 500 more on the first page, numbered 0 to 499 in a cube of their own, so it takes two pages.
        IEnumerable<Star> More(int from, int count) => Stars(from, count, -1000, 100, -1000);
        var net = new SpanshStandIn().Then(Counted(507)).Then(Listed(507, known.Concat(More(0, 493)))).ThenOffline();
        HeatmapSurvey survey = New(net);

        // Half listed: the systems on the page not yet listed would all look missing, so none is worked out.
        SurveyAnswer half = await survey.Survey(Wredguia, go: false);
        Assert.Equal(Unreachable, half.Problem);
        Assert.Equal((false, 0L), (half.Area!.Complete, half.Area.MissingTotal));
        Assert.Empty(half.Area.Missing);

        net.Then(Listed(507, More(493, 7)));
        AreaView area = (await survey.Survey(Wredguia, go: false)).Area!;

        Assert.True(area.Complete);
        Assert.Equal(6, area.MissingTotal);
        Assert.Equal(
            ["Wredguia AA-A h0", "Wredguia AA-A h1", "Wredguia AA-A h2", "Wredguia WD-K d8-1", "Wredguia WD-K d8-3", "Wredguia WD-K d8-4"],
            area.Missing.Select(system => system.Name).Order(StringComparer.Ordinal));
        // Named as the game names them, each at its own cube's middle (the name says no more than that).
        Assert.All(area.Missing, system =>
        {
            DecodedName decoded = ProcGenNames.Decode(system.Name)!;
            Assert.Equal(decoded.Cube.Address, system.Id64);
            Assert.Equal(decoded.Cube.Centre, (system.X, system.Y, system.Z));
        });
        // Counted in the cubes of 160 ly that hold those middles: the 80 ly cube's at -345, 15, -305,
        // and the sector's own middle, where no system is known at all.
        Assert.Equal(new SurveyCube(6, 0, 4, 3, 3, 0, 0, 3), area.Cubes.Single(cube => (cube.I, cube.J, cube.K) == (6, 0, 4)));
        Assert.Equal(new SurveyCube(4, 4, 4, 0, 0, 0, 0, 3), area.Cubes.Single(cube => (cube.I, cube.J, cube.K) == (4, 4, 4)));
        Assert.Equal((507, 6L), (Totals(area).Known, Totals(area).Missing));
    }

    // ------------------------------------------------------------ the careful guest's rules

    [Fact]
    public async Task A_listing_stopped_by_the_player_keeps_its_finished_pages_and_the_next_survey_carries_on_from_there()
    {
        var net = new SpanshStandIn().Then(Counted(2521));
        for (int page = 0; page < 6; page++) net.Then(Page(2521, page));
        HeatmapSurvey survey = New(net);
        var seen = new List<SurveyProgress>();
        survey.Progress += progress =>
        {
            lock (seen) seen.Add(progress);
            if (progress is { State: HeatmapSurvey.Listing, Page: 2 }) survey.Cancel();   // the player presses Cancel after the second page
        };

        SurveyAnswer stopped = await survey.Survey(Sphuedu, go: true);   // straight to go: counted, then listed

        Assert.True(stopped.Stopped);
        Assert.Null(stopped.Problem);
        Assert.Equal([0, 1], PagesAsked(net));
        Assert.Equal((false, 2, 6, 1000), (stopped.Area!.Complete, stopped.Area.PagesDone, stopped.Area.PagesTotal, stopped.Area.Systems.Count));
        Assert.Equal(SurveySummary.Partial, survey.Summary(Sphuedu)!.State);
        Assert.Equal(
            [
                new SurveyProgress(Sphuedu, 0, 0, HeatmapSurvey.Counting), new SurveyProgress(Sphuedu, 0, 6, HeatmapSurvey.Listing),
                new SurveyProgress(Sphuedu, 1, 6, HeatmapSurvey.Listing), new SurveyProgress(Sphuedu, 2, 6, HeatmapSurvey.Listing),
                new SurveyProgress(Sphuedu, 2, 6, HeatmapSurvey.Stopped),
            ],
            seen);

        // Even after a restart: the next survey picks up at the third page, without counting again,
        // and at once, because only four requests are left.
        HeatmapSurvey restarted = New(net);
        SurveyAnswer resumed = await restarted.Survey(Sphuedu, go: false);

        Assert.Equal([0, 1, 2, 3, 4, 5], PagesAsked(net));
        Assert.Equal(1 + 6, net.Asked.Count);
        Assert.Equal((true, 2521, 6), (resumed.Area!.Complete, resumed.Area.Systems.Count, resumed.Area.PagesDone));
        Assert.Equal(SurveySummary.Complete, restarted.Summary(Sphuedu)!.State);
    }

    [Fact]
    public async Task Nothing_that_failed_is_asked_again_by_itself_and_the_players_next_click_carries_on()
    {
        var net = new SpanshStandIn().Then(Counted(1200)).Then(Page(1200, 0)).ThenOffline();
        HeatmapSurvey survey = New(net);

        SurveyAnswer broken = await survey.Survey(Sphuedu, go: false);   // three requests: started at once

        Assert.Equal(Unreachable, broken.Problem);
        Assert.False(broken.Stopped);
        Assert.Equal([0, 1], PagesAsked(net));
        Assert.Equal(3, net.Asked.Count);
        Assert.Equal((1, 3, 500), (broken.Area!.PagesDone, broken.Area.PagesTotal, broken.Area.Systems.Count));

        // The player's next click asks again, from the page that failed.
        net.Then(Page(1200, 1)).Then(Page(1200, 2));
        SurveyAnswer mended = await survey.Survey(Sphuedu, go: false);
        Assert.Equal([0, 1, 1, 2], PagesAsked(net));
        Assert.True(mended.Area!.Complete);

        // A count that fails stops a column there; the cubes counted before it are kept.
        net.Then(Counted(10)).Then(Counted(20)).Then("{}", HttpStatusCode.ServiceUnavailable);
        CountAnswer column = await survey.Count("column", Player.X, Player.Y, Player.Z, go: true);
        Assert.Equal("Spansh answered with error 503. Try again in a moment.", column.Problem);
        Assert.Equal([10, 20], column.Cubes.Select(cube => cube.Known));
        Assert.Equal(5 + 3, net.Asked.Count);

        // And a count of an area that fails keeps nothing.
        SurveyAnswer uncounted = await survey.Survey(Systoodou, go: false);
        Assert.Equal((Unreachable, (AreaView?)null), (uncounted.Problem, uncounted.Area));
        Assert.Equal(5 + 3 + 1, net.Asked.Count);
        Assert.Null(survey.Summary(Systoodou));
    }

    [Fact]
    public async Task A_listing_that_finds_Spansh_past_10000_part_way_keeps_what_came_and_is_too_busy_to_list_from_then_on()
    {
        // Counted at 9,800, twenty requests; by the first page Spansh has learned of enough new systems to pass its limit.
        var net = new SpanshStandIn().Then(Counted(9800)).Then(Listed(10_000, Stars(0, 500)));
        HeatmapSurvey survey = New(net);

        SurveyAnswer answer = await survey.Survey(Sphuedu, go: true);

        Assert.Equal(
            "Spansh now knows 10,000 systems or more here, too many to list. What was listed is kept; count its cubes one at a time instead.",
            answer.Problem);
        Assert.Equal([0], PagesAsked(net));
        Assert.Equal((10_000, true), (answer.Count, answer.Capped));
        Assert.Equal((false, 500, 0L), (answer.Area!.Complete, answer.Area.Systems.Count, answer.Area.MissingTotal));
        // Not "partial": a new survey would not carry on, so the map offers counts of single cubes instead.
        Assert.Equal(SurveySummary.Counted, survey.Summary(Sphuedu)!.State);
        Assert.Equal(SurveySummary.Counted, New(new SpanshStandIn()).Summary(Sphuedu)!.State);   // and so it stays after a restart

        SurveyAnswer again = await survey.Survey(Sphuedu, go: true);
        Assert.Equal((10_000, true, (string?)null, 500), (again.Count, again.Capped, again.Problem, again.Area!.Systems.Count));
        Assert.Equal(2, net.Asked.Count);   // answered from what is kept
    }

    [Fact]
    public async Task One_session_sends_at_most_200_requests_and_then_says_so()
    {
        var net = new SpanshStandIn { Forever = Counted(42) };
        HeatmapSurvey survey = New(net);

        for (int i = 0; i < 7; i++) Assert.Null((await survey.Count("around", Player.X, Player.Y, Player.Z, go: true, again: true)).Problem);
        Assert.Null((await survey.Count("column", Player.X, Player.Y, Player.Z, go: true, again: true)).Problem);
        Assert.Equal(7 * 27 + 8, survey.Used);

        CountAnswer cut = await survey.Count("column", Player.X, Player.Y, Player.Z, go: true, again: true);
        SurveyAnswer refused = await survey.Survey(Sphuedu, go: false);

        Assert.Equal(
            "Studio allows itself 200 requests to Spansh for the 3D map each time it runs, and they are used up. Everything that came back is kept. To ask for more, exit Studio (right-click its icon near the clock and choose Exit) and start it again.",
            cut.Problem);
        Assert.Equal(cut.Problem, refused.Problem);
        Assert.Equal(8, cut.Cubes.Count);   // three counted now, five kept from before
        Assert.Equal(200, net.Asked.Count);
        Assert.Equal((200, 200), (survey.Used, survey.Cap));
        Assert.Equal(new SurveyNeeds(8, 0.04, 0), (await survey.Count("column", Player.X, Player.Y, Player.Z, go: false, again: true)).Needs);

        // Starting Studio again starts a new session.
        Assert.Null((await New(net).Count("cube", Player.X, Player.Y, Player.Z, go: false, again: true)).Problem);
        Assert.Equal(201, net.Asked.Count);
    }

    /// <summary>Holds back each request for a page of a listing until it is let go; everything else passes straight on.</summary>
    sealed class HeldPages(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LetGo { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            if (request.Content is { } content && (await content.ReadAsStringAsync(cancel)).Contains("\"size\":500", StringComparison.Ordinal))
            {
                Holding.TrySetResult();
                await LetGo.Task.WaitAsync(cancel);
            }
            return await base.SendAsync(request, cancel);
        }
    }

    [Fact]
    public async Task The_maps_requests_go_one_at_a_time_without_holding_up_name_suggestions_and_Cancel_stops_the_waiting_ones_too()
    {
        var net = new SpanshStandIn().Then(Counted(600)).Then(SpanshStandIn.Recorded("system-names.json")).Then(Page(600, 0)).Then(Page(600, 1));
        var held = new HeldPages(net);
        HeatmapSurvey survey = New(held);
        Spansh spansh = clients[^1];
        var seen = new List<SurveyProgress>();
        survey.Progress += progress =>
        {
            lock (seen) seen.Add(progress);
        };

        Task<SurveyAnswer> listing = survey.Survey(Sphuedu, go: false);   // two requests: started at once
        await held.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Name suggestions (and route plotting) ask Spansh directly, not through the map's gate.
        Assert.Equal(["Maia", "Maiadiwobog", "Maiawa"], await spansh.SystemNames("Maia"));
        Assert.False(listing.IsCompleted);

        // Another of the map's jobs waits its turn, and Cancel stops it before it asks anything.
        Task<CountAnswer> queued = survey.Count("cube", Player.X, Player.Y, Player.Z, go: true);
        lock (seen) Assert.Contains(new SurveyProgress("c:cube", 0, 0, HeatmapSurvey.Waiting), seen);
        Assert.True(survey.Cancel());
        CountAnswer notAsked = await queued;
        Assert.True(notAsked.Stopped);
        Assert.Empty(notAsked.Cubes);

        // The page under way is let finish and kept; the next one is not asked for.
        held.LetGo.SetResult();
        SurveyAnswer stopped = await listing;
        Assert.True(stopped.Stopped);
        Assert.Equal((1, 2, 500), (stopped.Area!.PagesDone, stopped.Area.PagesTotal, stopped.Area.Systems.Count));
        Assert.Equal(3, net.Asked.Count);   // the count, the names, the first page
        Assert.Equal(2, survey.Used);       // the names are not the map's
        Assert.False(survey.Cancel());      // nothing left to stop
    }

    [Fact]
    public async Task Results_are_written_beside_their_file_then_moved_into_place_and_kept_for_good()
    {
        string file = Path.Combine(temp, "sector-38-33-17.jsonl");
        File.WriteAllText(file + ".tmp", "half a file left by a crash");
        Star[] three = [.. Stars(0, 3, -1000, 1300, -2000)];
        var net = new SpanshStandIn().Then(Counted(3)).Then(Listed(3, three)).Then(Counted(7933));
        HeatmapSurvey survey = New(net);

        AreaView first = (await survey.Survey(Systoodou, go: false)).Area!;
        await survey.Count("cube", Player.X, Player.Y, Player.Z, go: false);

        Assert.Equal(["counted.json", "sector-38-33-17.jsonl"], Directory.GetFiles(temp).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal));
        string[] lines = File.ReadAllLines(file);
        Assert.Equal(1 + 3, lines.Length);   // what the file holds, then one line a system
        using (JsonDocument heading = JsonDocument.Parse(lines[0]))
        {
            Assert.Equal(Systoodou, heading.RootElement.GetProperty("key").GetString());
            Assert.Equal(3, heading.RootElement.GetProperty("systems").GetInt32());
        }
        Assert.Contains($"\"id64\":\"{three[0].Id64}\"", lines[1]);   // the number as text, whole

        // The next start of Studio finds it all, and asks nothing.
        var offline = new SpanshStandIn();
        HeatmapSurvey next = New(offline);
        AreaView kept = (await next.View(Systoodou))!;
        Assert.Equal(first.Systems, kept.Systems);
        Assert.Equal((first.At, first.Cubes.Single()), (kept.At, kept.Cubes.Single()));
        Assert.Equal(new SurveySummary(first.At, 3, false, SurveySummary.Complete), next.Summary(Systoodou));
        Assert.Equal(new CountedCube(-225, -25, -105, 160, 7933, false, survey.Counted[0].At), Assert.Single(next.Counted));

        // A survey of long ago is still shown, with its date: results never run out.
        DateTime longAgo = new(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.WriteAllLines(Path.Combine(temp, "sector-38-33-16.jsonl"),
        [
            """{"key":"s:38:33:16","count":1,"capped":false,"countedUtc":"2025-01-02T03:04:05Z","updatedUtc":"2025-01-02T03:04:05Z","pagesDone":[0],"systems":1}""",
            """{"name":"Long ago","id64":"9007199254740993","x":-1000,"y":1300,"z":-3000,"bodyCount":2,"bodies":1,"updated":null}""",
        ]);
        AreaView old = (await next.View("s:38:33:16"))!;
        Assert.Equal((longAgo, true), (old.At, old.Complete));
        Assert.Equal(new SurveyedSystem("Long ago", 9007199254740993, -1000, 1300, -3000, SurveyedSystem.Part), Assert.Single(old.Systems));
        Assert.Equal(new SurveySummary(longAgo, 1, false, SurveySummary.Complete), next.Summary("s:38:33:16"));

        // A file cut short, or holding another area, counts as not there.
        File.WriteAllLines(Path.Combine(temp, "sector-38-33-15.jsonl"),
            ["""{"key":"s:38:33:15","count":2,"capped":false,"countedUtc":"2025-01-02T03:04:05Z","updatedUtc":"2025-01-02T03:04:05Z","pagesDone":[0],"systems":2}""", lines[1]]);
        File.WriteAllLines(Path.Combine(temp, "sector-38-33-14.jsonl"), lines);
        Assert.Null(await next.View("s:38:33:15"));
        Assert.Null(await next.View("s:38:33:14"));
        Assert.Null(next.Summary("s:38:33:14"));
        Assert.Empty(offline.Asked);
    }

    [Fact]
    public async Task The_page_can_look_at_an_area_while_its_listing_goes_on_and_every_page_still_reaches_the_disk()
    {
        var net = new SpanshStandIn().Then(Counted(2521));
        for (int page = 0; page < 6; page++) net.Then(Page(2521, page));
        HeatmapSurvey survey = New(net);
        string file = Path.Combine(temp, "sector-38-33-18.jsonl");
        // After each request that came back, the copy on disk holds it.
        var behind = new List<int>();
        survey.Progress += progress =>
        {
            if (progress is not { State: HeatmapSurvey.Listing, Page: > 0 }) return;
            using JsonDocument heading = JsonDocument.Parse(File.ReadLines(file).First());
            if (heading.RootElement.GetProperty("pagesDone").GetArrayLength() != progress.Page) behind.Add(progress.Page);
        };
        using var listed = new CancellationTokenSource();
        int looks = 0;
        // Meanwhile the page asks for the area again and again, from several places at once.
        Task[] looking =
        [
            .. Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                while (!listed.IsCancellationRequested)
                {
                    if (await survey.View(Sphuedu) is { } seen) Assert.Equal(Math.Min(seen.PagesDone * 500, 2521), seen.Systems.Count);
                    survey.Summary(Sphuedu);
                    Interlocked.Increment(ref looks);
                }
            }, TestContext.Current.CancellationToken)),
        ];

        SurveyAnswer answer = await survey.Survey(Sphuedu, go: true);
        listed.Cancel();
        await Task.WhenAll(looking);

        Assert.True(answer.Area!.Complete);
        Assert.True(looks > 0);
        Assert.Empty(behind);
        Assert.Equal(2521, (await New(new SpanshStandIn()).View(Sphuedu))!.Systems.Count);
        Assert.Equal(["sector-38-33-18.jsonl"], Directory.GetFiles(temp).Select(path => Path.GetFileName(path)));
    }

    [Fact]
    public async Task A_second_click_while_a_job_runs_waits_for_it_and_takes_its_result_without_asking_again()
    {
        var net = new SpanshStandIn().Then(Counted(600)).Then(Page(600, 0)).Then(Page(600, 1));
        for (int up = 0; up < 8; up++) net.Then(Counted(100 + up));
        var held = new HeldPages(net);
        HeatmapSurvey survey = New(held);

        Task<SurveyAnswer> first = survey.Survey(Sphuedu, go: false);
        await held.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        // The page is reloaded and asks again; the player clicks "Count the column" twice.
        Task<SurveyAnswer> second = survey.Survey(Sphuedu, go: false);
        Task<CountAnswer> column = survey.Count("column", Player.X, Player.Y, Player.Z, go: true);
        Task<CountAnswer> columnAgain = survey.Count("column", Player.X, Player.Y, Player.Z, go: true);
        held.LetGo.SetResult();

        Assert.Equal((await first).Area!.Systems, (await second).Area!.Systems);
        Assert.True((await second).Area!.Complete);
        Assert.Equal((await column).Cubes, (await columnAgain).Cubes);
        Assert.Equal(8, (await column).Cubes.Count);
        Assert.Equal(1 + 2 + 8, net.Asked.Count);
        Assert.Equal(1 + 2 + 8, survey.Used);
    }

    [Fact]
    public async Task Every_job_that_says_it_is_under_way_says_when_it_is_over_also_when_it_stops_to_ask_or_answers_from_what_is_kept()
    {
        // Systoodou counted before as too busy to list; Wredguia big enough to ask first; Sphuedu listed at once.
        var net = new SpanshStandIn().Then(Counted(10_000)).Then(Counted(2521)).Then(Counted(600)).Then(Page(600, 0)).Then(Page(600, 1));
        var held = new HeldPages(net);
        HeatmapSurvey survey = New(held);
        var seen = new List<SurveyProgress>();
        survey.Progress += progress =>
        {
            lock (seen) seen.Add(progress);
        };
        await survey.Survey(Systoodou, go: false);
        lock (seen) seen.Clear();

        // Counted, then stopped to ask the player: the count is over, and says so (an open page would otherwise
        // show "Spansh is being asked" for nothing).
        Assert.NotNull((await survey.Survey(Wredguia, go: false)).Needs);
        lock (seen) Assert.Equal([new SurveyProgress(Wredguia, 0, 0, HeatmapSurvey.Counting), new SurveyProgress(Wredguia, 1, 1, HeatmapSurvey.Done)], seen);

        // A look inside Systoodou waits for a listing under way, then answers from what is kept, without asking.
        Task<SurveyAnswer> listing = survey.Survey(Sphuedu, go: false);
        await held.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Task<SurveyAnswer> kept = survey.Survey(Systoodou, go: false);
        lock (seen) Assert.Equal(new SurveyProgress(Systoodou, 0, 0, HeatmapSurvey.Waiting), seen[^1]);
        held.LetGo.SetResult();
        Assert.True((await listing).Area!.Complete);
        Assert.True((await kept).Capped);
        Assert.Equal(5, net.Asked.Count);

        // Whatever the job, its last word is that it is over: no open page is left showing one that has ended.
        string[] over = [HeatmapSurvey.Done, HeatmapSurvey.Stopped, HeatmapSurvey.Failed];
        lock (seen)
        {
            Assert.All(seen.GroupBy(progress => progress.Key), job => Assert.Contains(job.Last().State, over));
            Assert.Equal(new SurveyProgress(Systoodou, 0, 0, HeatmapSurvey.Done), seen.Last(progress => progress.Key == Systoodou));
        }
    }

    [Fact]
    public async Task Survey_again_keeps_its_new_listing_beside_the_complete_one_until_it_is_complete_too()
    {
        var net = new SpanshStandIn().Then(Counted(600)).Then(Page(600, 0)).Then(Page(600, 1));
        HeatmapSurvey survey = New(net);
        AreaView before = (await survey.Survey(Sphuedu, go: false)).Area!;
        Assert.True(before.Complete);

        // Survey again: 1,200 now, four requests, so at once; Spansh goes out of reach after the new listing's first page.
        net.Then(Counted(1200)).Then(Page(1200, 0)).ThenOffline();
        SurveyAnswer broken = await survey.Survey(Sphuedu, go: false);

        Assert.Equal(Unreachable, broken.Problem);
        // The answer, the area's file and the block's note are still the complete listing; the new page is kept beside it.
        Assert.Equal((600, true, 600), (broken.Area!.Count, broken.Area.Complete, broken.Area.Systems.Count));
        Assert.Equal((1, 3), (broken.Area.Renewing!.PagesDone, broken.Area.Renewing.PagesTotal));
        AreaView still = (await survey.View(Sphuedu))!;
        Assert.Equal((600, true, before.At), (still.Systems.Count, still.Complete, still.At));
        Assert.Equal(new SurveySummary(before.At, 600, false, SurveySummary.Complete), survey.Summary(Sphuedu));
        Assert.True(File.Exists(Path.Combine(temp, "sector-38-33-18.jsonl.new")));

        // Even after a restart, the next survey goes on with the new listing from its second page, without a new count.
        net.Then(Page(1200, 1)).Then(Page(1200, 2));
        HeatmapSurvey restarted = New(net);
        AreaView after = (await restarted.Survey(Sphuedu, go: false)).Area!;

        Assert.Equal([0, 1, 0, 1, 1, 2], PagesAsked(net));
        Assert.Equal(3 + 3 + 2, net.Asked.Count);
        Assert.Equal((1200, true, 1200, (RenewingListing?)null), (after.Count, after.Complete, after.Systems.Count, after.Renewing));
        Assert.Equal(1200, (await New(new SpanshStandIn()).View(Sphuedu))!.Systems.Count);
        // The new listing has taken the old one's place, and nothing is left beside it.
        Assert.Equal(["sector-38-33-18.jsonl"], Directory.GetFiles(temp).Select(path => Path.GetFileName(path)));
    }

    [Fact]
    public async Task A_kept_file_that_cannot_be_read_is_never_written_over()
    {
        // The counted cubes damaged (a hand edit, a fault of the disk): moved aside and kept, and counting starts afresh.
        string countedFile = Path.Combine(temp, "counted.json");
        const string Damaged = """[{"x0":-225,"y0":-25,"z0":-105,"size":160,"kno""";
        File.WriteAllText(countedFile, Damaged);
        var net = new SpanshStandIn().Then(Counted(42));
        HeatmapSurvey survey = New(net);

        Assert.Empty(survey.Counted);
        Assert.Equal(Damaged, File.ReadAllText(Assert.Single(Directory.GetFiles(temp, "counted.json.unreadable-*"))));
        Assert.Equal(42, Assert.Single((await survey.Count("cube", Player.X, Player.Y, Player.Z, go: false)).Cubes).Known);

        // Held by another program just now (a virus scanner, say): nothing is written over it, and it is read again later.
        HeatmapSurvey next = New(net);   // the next start of Studio
        net.Then(Counted(7)).Then(Counted(3));
        using (new FileStream(countedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(next.Counted);
            Assert.Equal(7, Assert.Single((await next.Count("cube", 0, 0, 0, go: false)).Cubes).Known);   // Sol's cube: counted, and held in memory
        }
        Assert.Equal([42, 7], next.Counted.Select(cube => cube.Known));   // let go: read at last, with the count made meanwhile
        await next.Count("cube", 0, 200, 0, go: false);
        Assert.Equal([42, 7, 3], New(new SpanshStandIn()).Counted.Select(cube => cube.Known));   // and the next count writes them all

        // A sector's kept listing cut short: a look leaves it where it is; a survey moves it aside before keeping anything.
        string sector = Path.Combine(temp, "sector-38-33-17.jsonl");
        const string Cut = """{"key":"s:38:33:17","count":3,"capped":false,"countedUtc":"2025-01-02T03:04:05Z","updatedUtc":"2025-01-02T03:04:05Z","pagesDone":[0],"systems":3}""" + "\n";
        File.WriteAllText(sector, Cut);
        net.Then(Counted(3)).Then(Listed(3, Stars(0, 3, -1000, 1300, -2000)));
        Assert.Null(await next.View(Systoodou));
        Assert.Equal(Cut, File.ReadAllText(sector));
        Assert.True((await next.Survey(Systoodou, go: false)).Area!.Complete);
        Assert.Equal(Cut, File.ReadAllText(Assert.Single(Directory.GetFiles(temp, "sector-38-33-17.jsonl.unreadable-*"))));

        // Held by another program: nothing is asked of Spansh, and what is kept stays as it is.
        int asked = net.Asked.Count;
        using (new FileStream(sector, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            SurveyAnswer refused = await next.Survey(Systoodou, go: true);
            Assert.Equal(
                ("Studio could not read what it keeps on this PC for this sector or nebula just now: another program may have the file open. Nothing was asked of Spansh, so nothing kept was replaced. Try again in a moment.",
                 (AreaView?)null),
                (refused.Problem, refused.Area));
        }
        Assert.Equal(asked, net.Asked.Count);
        Assert.Equal(3, (await next.View(Systoodou))!.Systems.Count);
    }

    // ------------------------------------------------------------ counting single cubes of a busy sector

    [Fact]
    public async Task Counted_cubes_lie_on_the_sectors_grid_of_160_ly_cubes_a_column_and_the_cubes_around_the_player()
    {
        IReadOnlyList<Box> cube = HeatmapSurvey.CubesFor("cube", Player.X, Player.Y, Player.Z);
        IReadOnlyList<Box> column = HeatmapSurvey.CubesFor("column", Player.X, Player.Y, Player.Z);
        IReadOnlyList<Box> around = HeatmapSurvey.CubesFor("around", Player.X, Player.Y, Player.Z);

        // Exact whole light years, on the grid through Wredguia's corner (-1345, -25, -1065).
        Assert.Equal([new Box(-225, -25, -105, -65, 135, 55)], cube);
        Assert.Equal(Enumerable.Range(0, 8).Select(up => new Box(-225, -25 + 160 * up, -105, -65, 135 + 160 * up, 55)), column);
        Assert.Equal(1255.0, column[^1].Y1);   // the column ends on the sector's top face
        Assert.Equal(27, around.Count);
        Assert.Equal(new Box(-385, -185, -265, -225, -25, -105), around[0]);
        Assert.Equal(cube[0], around[13]);
        Assert.Equal(new Box(-65, 135, 55, 95, 295, 215), around[26]);   // the cubes around reach into Sol's sector, east
        Assert.Equal(cube, HeatmapSurvey.CubesFor(" Cube ", -225, -25, -105));   // a point on the near faces: this cube
        Assert.Equal(new Box(-65, 135, 55, 95, 295, 215), HeatmapSurvey.CubesFor("cube", -65, 135, 55)[0]);

        Assert.Throws<ArgumentException>(() => HeatmapSurvey.CubesFor("row", Player.X, Player.Y, Player.Z));
        Assert.Throws<ArgumentOutOfRangeException>(() => HeatmapSurvey.CubesFor("cube", double.NaN, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => HeatmapSurvey.CubesFor("cube", 200_000, 0, 0));

        var net = new SpanshStandIn().Then(Counted(7933)).Then(Counted(10_000));
        for (int up = 2; up < 8; up++) net.Then(Counted(1000 / up));
        HeatmapSurvey survey = New(net);

        // Eight requests: the player is asked first.
        CountAnswer asked = await survey.Count("column", Player.X, Player.Y, Player.Z, go: false);
        Assert.Equal(new SurveyNeeds(8, 0.04, 200), asked.Needs);
        Assert.Empty(asked.Cubes);
        Assert.Empty(net.Asked);

        CountAnswer counted = await survey.Count("column", Player.X, Player.Y, Player.Z, go: true);
        Assert.Equal(
            """POST /api/systems/search {"filters":{"x":{"comparison":"<=>","value":[-225,-65.015625]},"y":{"comparison":"<=>","value":[-25,134.984375]},"z":"""
            + """{"comparison":"<=>","value":[-105,54.984375]}},"sort":[{"id64":{"direction":"asc"}}],"size":1,"page":0}""",
            net.Asked[0]);
        Assert.Equal(8, net.Asked.Count);
        Assert.Equal((-225.0, -25.0, -105.0, 160.0, 7933, false), (counted.Cubes[0].X0, counted.Cubes[0].Y0, counted.Cubes[0].Z0, counted.Cubes[0].Size, counted.Cubes[0].Known, counted.Cubes[0].Capped));
        Assert.Equal((10_000, true), (counted.Cubes[1].Known, counted.Cubes[1].Capped));   // "10,000 or more"
        Assert.Equal(column.Select(box => box.Y0), counted.Cubes.Select(found => found.Y0));
        Assert.Equal(column.Select(box => box.Y0), survey.Counted.Select(found => found.Y0));

        // What is counted is kept, and not asked again unless the player asks for it again.
        Assert.Equal(7933, Assert.Single((await survey.Count("cube", Player.X, Player.Y, Player.Z, go: false)).Cubes).Known);
        Assert.Equal(8, net.Asked.Count);
        Assert.Equal(8, (await survey.Count("column", Player.X, Player.Y, Player.Z, go: false, again: true)).Needs?.Requests);
        net.Then(Counted(7940));
        Assert.Equal(7940, Assert.Single((await survey.Count("cube", Player.X, Player.Y, Player.Z, go: false, again: true)).Cubes).Known);
        Assert.Equal(9, net.Asked.Count);
        Assert.Equal(8, survey.Counted.Count);   // the new count took the old one's place
    }

    // ------------------------------------------------------------ looking inside a nebula

    [Theory]
    [InlineData(60, 10)]
    [InlineData(100, 10)]
    [InlineData(120, 20)]
    [InlineData(200, 20)]
    [InlineData(300, 40)]
    [InlineData(652, 80)]
    [InlineData(2000, 80)]
    public void A_nebula_is_cut_into_cubes_of_about_an_eighth_of_its_width_from_10_to_80_ly(double width, double cubeLy) =>
        Assert.Equal(cubeLy, SurveyArea.NebulaCubeLy(width));

    [Fact]
    public async Task Looking_inside_a_nebula_lists_the_cube_round_it_and_counts_only_the_gaps_of_cubes_wholly_inside()
    {
        SurveyArea california = SurveyArea.Of("n:california nebula")!;
        Nebula nebula = HaRegions.Named("California Sector")!;
        Assert.Equal("n:California Sector", california.Key);
        Assert.Equal(new Box(nebula.X - 100, nebula.Y - 100, nebula.Z - 100, nebula.X + 100, nebula.Y + 100, nebula.Z + 100), california.Box);
        Assert.Equal((20.0, 10), (california.CubeLy, california.CubesAcross(california.CubeLy)));

        DecodedName lxt = ProcGenNames.Decode("California Sector LX-T b3-0")!;   // a cube of 20 ly near the nebula's middle
        long Lxt(long number) => SystemAddress.WithNumber(lxt.Cube.Address, number);
        long East(long number) => Address(-315, -215, -935, 1, number);       // the next cube of 20 ly to the east
        Star[] inside =
        [
            new("California Sector LX-T b3-0", Lxt(0), -335, -215, -935),
            new("California Sector LX-T b3-2", Lxt(2), -336, -216, -936),
            new("HIP 1", East(0), -315, -215, -935),
            new("HIP 2", East(3), -316, -216, -936),
            // Number 4 of a cube of 160 ly that reaches out of the area: its 0 to 3 may well be known out there.
            new("HD 5", Address(-430, -215, -935, 4, 4), -430, -215, -935),
        ];
        var net = new SpanshStandIn().Then(Counted(5)).Then(Listed(5, inside)).Then(Counted(10_000));
        HeatmapSurvey survey = New(net);

        SurveyAnswer answer = await survey.Survey("n:California Nebula", go: false);

        Assert.Equal("n:California Sector", answer.Key);
        JsonNode sent = JsonNode.Parse(net.Asked[0][SearchCall.Length..])!;
        Assert.Equal(california.Box.X0, (double)sent["filters"]!["x"]!["value"]![0]!);
        Assert.Equal(Math.Ceiling(california.Box.X1 * 32) / 32 - 1.0 / 64, (double)sent["filters"]!["x"]!["value"]![1]!);
        AreaView area = answer.Area!;
        Assert.Equal((20.0, 10, 3L), (area.CubeLy, area.N, area.MissingTotal));
        Assert.Equal([california.Box.X0, california.Box.Y0, california.Box.Z0], area.Corner);
        Assert.Contains("California Sector LX-T b3-1", area.Missing.Select(system => system.Name));
        Assert.All(area.Missing, system =>
        {
            Assert.StartsWith("California Sector ", system.Name);   // inside the nebula, named after it
            Assert.Equal(system.Id64, ProcGenNames.Decode(system.Name)!.Cube.Address);
        });
        Assert.Equal(new SurveyCube(4, 4, 4, 2, 2, 0, 0, 1), area.Cubes.Single(cube => (cube.I, cube.J, cube.K) == (4, 4, 4)));
        Assert.Equal(new SurveyCube(5, 4, 4, 2, 2, 0, 0, 2), area.Cubes.Single(cube => (cube.I, cube.J, cube.K) == (5, 4, 4)));
        Assert.Equal(new SurveyCube(0, 4, 4, 1, 1, 0, 0, 0), area.Cubes.Single(cube => (cube.I, cube.J, cube.K) == (0, 4, 4)));
        Assert.True(File.Exists(Path.Combine(temp, "nebula-California Sector.jsonl")));

        // A nebula too busy to list is counted and kept as such; the map then offers counts of single cubes.
        SurveyAnswer busy = await survey.Survey("n:Col 285", go: false);
        Assert.Equal(("n:Col 285 Sector", 10_000, true, (SurveyNeeds?)null), (busy.Key, busy.Count, busy.Capped, busy.Needs));
        Assert.Equal((false, 0, 0, 80.0, 9), (busy.Area!.Complete, busy.Area.PagesTotal, busy.Area.Systems.Count, busy.Area.CubeLy, busy.Area.N));
        Assert.Equal(SurveySummary.Counted, survey.Summary("n:Col 285 Sector")!.State);
        Assert.Equal(3, net.Asked.Count);

        // Spansh's counts only grow: looking inside it again answers from what is kept, without asking.
        SurveyAnswer stillBusy = await survey.Survey("n:Col 285 Sector", go: true);
        Assert.Equal((10_000, true, (string?)null), (stillBusy.Count, stillBusy.Capped, stillBusy.Problem));
        Assert.Equal(3, net.Asked.Count);
    }

    // ------------------------------------------------------------ mistakes

    [Theory]
    [InlineData("s:38:64:18")]          // above the grid's top layer
    [InlineData("s:128:32:18")]
    [InlineData("s:-1:32:18")]
    [InlineData("s:38:32")]
    [InlineData("n:Nowhere Nebula")]
    [InlineData("Wredguia")]
    [InlineData("")]
    public async Task A_key_that_names_no_sector_or_nebula_is_turned_down_without_asking(string key)
    {
        var net = new SpanshStandIn();
        HeatmapSurvey survey = New(net);

        Assert.Null(SurveyArea.Of(key));
        await Assert.ThrowsAsync<ArgumentException>(() => survey.Survey(key, go: true));
        await Assert.ThrowsAsync<ArgumentException>(() => survey.View(key));
        Assert.Null(survey.Summary(key));
        Assert.Empty(net.Asked);
    }
}
