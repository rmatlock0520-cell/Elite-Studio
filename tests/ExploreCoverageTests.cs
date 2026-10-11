using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Nav;
using WebRequest = EliteStudio.App.WebRequest;     // System.Net has older types with the same names
using WebResponse = EliteStudio.App.WebResponse;

namespace EliteStudio.Tests;

/// <summary>
/// Where few have been: the map made from EDAstro's sector list, the first thinly known square on a
/// heading, the system Spansh names for it, and the /api/nav/explore/ routes. In tests\Fixtures\nav:
///   sector-list.csv      EDAstro's real list of 8 October 2026, cut down from 12,083 sectors to 28:
///                        six squares side by side far out in the west of the galaxy, each with its
///                        four layers of sectors, and one square near Sol holding two of the game's
///                        layers and two hand-named sectors (which have no place in the game's grid)
///   systems-search.json  Spansh's real answer of 9 October 2026, byte for byte, to a search for the
///                        two systems nearest to the point -6000, -50, 12000
/// The six squares hold 5,073, 7,180, 5,435, 4,532, 4,722 and 3,501 known systems from west to east,
/// the one near Sol 396,184. A quarter of the way up that list stands 4,532, so only the last of the
/// six counts as thinly known.
/// </summary>
public sealed class ExploreCoverageTests(ITestOutputHelper output) : IDisposable
{
    static readonly string SectorList = SpanshStandIn.Recorded("sector-list.csv");
    static readonly DateTime Fetched = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    static readonly IReadOnlyDictionary<string, string> NoQuery = new Dictionary<string, string>();

    /// <summary>What Spansh answers to a search, in its own layout, when the nearest system it knows is this one.</summary>
    const string NearThinSquare = """{"count":3,"from":0,"results":[{"distance":10.2,"id64":1,"name":"Loihee AA-A h1","x":-27610,"y":-48,"z":20030}],"size":1}""";

    const string Unreachable = "EDAstro could not be reached. Check your internet connection and try again.";
    const string NotTheList = "EDAstro's sector list is not laid out the way Studio knows it, so no map could be made from it.";

    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioCoverage-").FullName;
    readonly PracticeGame game = new();

    public void Dispose()
    {
        game.Dispose();
        Directory.Delete(temp, recursive: true);
    }

    // ------------------------------------------------------------ the map

    [Fact]
    public void The_sector_list_becomes_a_map_of_squares_with_every_layer_of_sectors_added_up()
    {
        SectorMap map = SectorMap.Parse(SectorList.ReplaceLineEndings("\r\n"), Fetched);   // EDAstro ends its lines the Windows way

        Assert.Equal((37, 31, 1280, -44865, -18985, Fetched), (map.Width, map.Height, map.Square, map.West, map.South, map.FetchedUtc));
        Assert.Equal(new[] { 5073, 7180, 5435, 4532, 4722, 3501 }, Enumerable.Range(8, 6).Select(column => map.Known[30 * map.Width + column]));
        Assert.Equal(396184, map.Known[14 * map.Width + 36]);
        Assert.Equal(7, map.Known.Count(known => known > 0));
        Assert.Equal(4532, map.ThinBelow);
        Assert.Equal(map.Known, SectorMap.Parse(SectorList, Fetched).Known);
    }

    [Theory]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("")]
    [InlineData("Sector,Systems,Avg X,Avg Y,Avg Z,Min X,Min Y,Min Z,Max X,Max Y,Max Z,MapSector X,MapSector Y,First Encountered,id64 X,id64 Y,id64 Z\n")]   // titles and nothing under them
    [InlineData("Sector,Systems,Avg X,Avg Y,Avg Z,Column,Row\nLoihee,1859,-27631.31,-377.73,20027.85,13,30\n")]                                              // a title Studio needs is gone
    [InlineData("Sector,Systems,Avg X,Avg Y,Avg Z,MapSector X,MapSector Y\nLoihee,many,-27631.31,-377.73,20027.85,13,30\nLoihea,12,0,0,0,-3,30\nEos,12,0,0,0,13,9000\n")]
    public void Anything_that_is_not_the_list_as_Studio_knows_it_is_turned_down_in_plain_words(string text)
    {
        var problem = Assert.Throws<InvalidDataException>(() => SectorMap.Parse(text, Fetched));

        Assert.Equal(NotTheList, problem.Message);
    }

    [Fact]
    public void The_first_thinly_known_square_on_a_heading_is_found_across_squares_that_are_better_known_or_empty()
    {
        SectorMap map = SectorMap.Parse(SectorList, Fetched);
        const double Row = 20055;   // the z half way up the row of six squares

        // From the westernmost of the six, heading east: the sixth. Its place is the middle of the 3,501 systems known in it.
        ThinSquare east = map.Ahead(-33985, Row, 90)!;
        Assert.Equal((3501, false), (east.Known, east.Here));
        Assert.Equal((-27613.9, -50.1, 20020.7), (Math.Round(east.X, 1), Math.Round(east.Y, 1), Math.Round(east.Z, 1)));

        // Already in it, whichever way the ship points.
        Assert.Equal(east with { Here = true }, map.Ahead(-27600, 20000, 0));
        Assert.Equal(east with { Here = true }, map.Ahead(-27600, 20000, 225));

        // Heading west from the fifth: none of the other four is thin (4,532 is the limit itself), and then the map ends.
        Assert.Null(map.Ahead(-28800, Row, 270));

        // From the square near Sol, north-west across 36,000 light years of squares with nothing known in them.
        Assert.Equal(east, map.Ahead(1900, -300, 304.5));
        // A commander off the map to the west flies onto it.
        Assert.Equal(east, map.Ahead(-50000, Row, 90));
        // Due north there is nothing at all.
        Assert.Null(map.Ahead(1900, -300, 0));
    }

    // ------------------------------------------------------------ fetching the list

    [Fact]
    public async Task The_list_is_fetched_when_asked_kept_for_a_week_and_an_older_copy_serves_while_EDAstro_cannot_be_reached()
    {
        var net = new SpanshStandIn().Then(SectorList);
        var coverage = new ExploreCoverage(temp, net);
        Assert.Null(coverage.Map);
        Assert.True(coverage.Stale);

        Assert.Null(await coverage.Refresh());
        Assert.Equal(["GET /mapcharts/files/sector-list.csv"], net.Asked);
        Assert.Matches(@"^EliteStudio/\d+\.\d+\.\d+ \(Elite Dangerous companion app; \+https://github\.com/[\w-]+/Elite-Studio\)$", net.UserAgent);
        Assert.Equal((4532, false), (coverage.Map?.ThinBelow, coverage.Stale));
        Assert.InRange(coverage.Map!.FetchedUtc, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow);

        // Asked for again within the week: the copy serves, and EDAstro is left alone.
        Assert.Null(await coverage.Refresh());
        Assert.Single(net.Asked);

        // The next start finds the copy on disk, and a PC that is offline does not matter.
        var offline = new SpanshStandIn();
        var nextStart = new ExploreCoverage(temp, offline);
        Assert.Equal((4532, false), (nextStart.Map?.ThinBelow, nextStart.Stale));
        Assert.Null(await nextStart.Refresh());
        Assert.Empty(offline.Asked);

        // Eight days on EDAstro may be asked again. It cannot be reached: the old map stays, and the answer says why.
        string kept = Path.Combine(temp, "edastro-sector-list.csv");
        File.SetLastWriteTimeUtc(kept, DateTime.UtcNow.AddDays(-8));
        var weekLater = new ExploreCoverage(temp, offline);
        Assert.True(weekLater.Stale);
        Assert.Equal(Unreachable, await weekLater.Refresh());
        Assert.Equal(4532, weekLater.Map?.ThinBelow);
        // EDAstro says no, or something else than the list comes back: the same.
        offline.Then("Too many requests", HttpStatusCode.TooManyRequests).Then("<html><body>Maintenance</body></html>");
        Assert.Equal("EDAstro answered with error 429. Try again later.", await weekLater.Refresh());
        Assert.Equal(NotTheList, await weekLater.Refresh());
        Assert.Equal((4532, true), (weekLater.Map?.ThinBelow, weekLater.Stale));
        // And then the list itself, with one sector fewer: that is the map from now on, and the copy kept.
        offline.Then(SectorList[..SectorList.LastIndexOf("Tr 10 Sector", StringComparison.Ordinal)]);
        Assert.Null(await weekLater.Refresh());
        Assert.Equal((395149, false), (weekLater.Map?.Known[14 * 37 + 36], weekLater.Stale));
        Assert.Equal(395149, new ExploreCoverage(temp, offline).Map?.Known[14 * 37 + 36]);
        Assert.Equal(4, offline.Asked.Count);
    }

    [Fact]
    public async Task Without_a_copy_and_without_EDAstro_there_is_no_map_and_the_answer_says_why()
    {
        File.WriteAllText(Path.Combine(temp, "edastro-sector-list.csv"), "not the list");   // a copy that cannot be read counts as none
        var coverage = new ExploreCoverage(temp, new SpanshStandIn());

        Assert.Null(coverage.Map);
        Assert.Equal(Unreachable, await coverage.Refresh());
        Assert.Null(coverage.Map);
    }

    // ------------------------------------------------------------ Spansh's nearest system

    [Fact]
    public async Task The_nearest_system_is_asked_of_Spansh_by_the_whole_light_year_and_each_answer_is_kept()
    {
        var net = new SpanshStandIn().Then(SpanshStandIn.Recorded("systems-search.json")).Then("""{"count":0,"from":0,"results":[],"size":1}""");
        using Spansh spansh = NavSpanshTests.Quick(net);

        NearSystem? near = await spansh.Nearest(-6000.4, -49.6, 12000.2);

        Assert.Equal(new NearSystem("Clooku CW-O b12-112", -6005.125, -48.09375, 11995.125), near);   // the first of the two, 7.3 ly from the point
        Assert.Equal(
            """POST /api/systems/search {"filters":{"distance":{"min":"0","max":"1000"}},"sort":[{"distance":{"direction":"asc"}}],"size":1,"page":0,"reference_coords":{"x":-6000,"y":-50,"z":12000}}""",
            net.Asked[0]);
        Assert.Same(near, await spansh.Nearest(-6000, -50, 12000));   // the same point to the light year: not asked again
        Assert.Null(await spansh.Nearest(60000, 0, -0.2));            // nothing known within reach of it
        Assert.Null(await spansh.Nearest(60000, 0, 0));
        Assert.Equal(2, net.Asked.Count);
        Assert.EndsWith("""reference_coords":{"x":60000,"y":0,"z":0}}""", net.Asked[1]);

        await Assert.ThrowsAsync<SpanshException>(() => spansh.Nearest(1, 2, 3));   // offline now
    }

    // ------------------------------------------------------------ the routes

    /// <summary>A journal line that puts the ship in a system on the row of six squares, x light years east of the galaxy's middle line.</summary>
    static string Arrival(string eventName, string system, long address, double x, double z = 20055) => string.Create(CultureInfo.InvariantCulture,
        $$"""{ "timestamp":"2026-10-09T03:00:00Z", "event":"{{eventName}}", "Docked":false, "StarSystem":"{{system}}", "SystemAddress":{{address}}, "StarPos":[{{x}},0,{{z}}] }""");

    static async Task<(int Status, JsonElement Json)> Call(WebServer web, string method, string route, string body = "")
    {
        WebResponse answer = await web.Handle(new WebRequest(method, "/api/nav/explore/" + route, NoQuery, body));
        return (answer.Status, JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(answer.Body)));
    }

    static string Text(JsonElement json) => JsonSerializer.Serialize(json);

    [Fact]
    public async Task The_routes_show_the_trip_fetch_the_map_and_name_a_system_to_head_for()
    {
        // In the westernmost of the six squares, having just jumped 100 ly due east.
        string journal = game.Journal("Journal.2026-10-09T030000.01.log", [Arrival("Location", "West Start", 11, -34085), Arrival("FSDJump", "West One", 12, -33985)]);
        using GameWatcher watcher = game.Watch();
        var edastro = new SpanshStandIn().Then(SectorList);
        var net = new SpanshStandIn().Then(NearThinSquare);
        using Spansh spansh = NavSpanshTests.Quick(net);
        using var web = new WebServer(temp);
        ExploreTrip trip = ExploreApi.Map(web, watcher.State, new Settings(Path.Combine(temp, "settings.json")), spansh, new ExploreCoverage(temp, edastro));
        watcher.Poll();

        var (status, view) = await Call(web, "GET", "trip");
        Assert.Equal(200, status);
        Assert.Equal(
            """{"sinceUtc":"2026-10-09T03:00:00Z","handedIn":false,"visited":2,"newSystems":0,"firstDiscoveries":0,"firstMapped":0,"system":"West One","isNew":null,"scanned":0,"bodyCount":0,"position":[-33985,0,20055],"heading":90}""",
            Text(view));

        // No map yet: nothing to look along, and nobody is asked.
        (status, view) = await Call(web, "POST", "waypoint", "{}");
        Assert.Equal((400, "Get the map first: without it Studio cannot tell where few have been."), (status, view.GetProperty("error").GetString()));
        (_, view) = await Call(web, "GET", "coverage");
        Assert.Equal("""{"map":null,"stale":true,"problem":null}""", Text(view));
        Assert.Empty(edastro.Asked);

        // The player asks for the map.
        (status, view) = await Call(web, "POST", "coverage", "{}");
        Assert.Equal((200, false, JsonValueKind.Null), (status, view.GetProperty("stale").GetBoolean(), view.GetProperty("problem").ValueKind));
        JsonElement map = view.GetProperty("map");
        Assert.Equal((37, 31, 1280, -44865, -18985, 4532), (map.GetProperty("width").GetInt32(), map.GetProperty("height").GetInt32(),
            map.GetProperty("square").GetDouble(), map.GetProperty("west").GetDouble(), map.GetProperty("south").GetDouble(), map.GetProperty("thinBelow").GetInt32()));
        Assert.Equal((37 * 31, 3501), (map.GetProperty("known").GetArrayLength(), map.GetProperty("known")[30 * 37 + 13].GetInt32()));
        Assert.Equal(Text(view), Text((await Call(web, "GET", "coverage")).Json));

        // The way the commander is travelling: east, where the sixth square is the first thin one. Spansh names
        // the system nearest to the middle of what is known there.
        (status, view) = await Call(web, "POST", "waypoint", "{}");
        Assert.Equal(200, status);
        Assert.Equal(90, view.GetProperty("bearing").GetDouble());
        Assert.Equal((3501, false), (view.GetProperty("square").GetProperty("known").GetInt32(), view.GetProperty("square").GetProperty("here").GetBoolean()));
        Assert.Equal("""{"name":"Loihee AA-A h1","x":-27610,"y":-48,"z":20030}""", Text(view.GetProperty("system")));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("problem").ValueKind);
        Assert.EndsWith("""reference_coords":{"x":-27614,"y":-50,"z":20021}}""", Assert.Single(net.Asked));

        // A direction picked on the map instead: nothing thin to the west before the map ends.
        (status, view) = await Call(web, "POST", "waypoint", """{"bearing":270}""");
        Assert.Equal((200, """{"bearing":270,"square":null,"system":null,"problem":null}"""), (status, Text(view)));

        // In the thin square itself there is nowhere to send the commander, and Spansh is not asked.
        game.Append(journal, Arrival("FSDJump", "Thin One", 13, -27600, 20000));
        watcher.Poll();
        (_, view) = await Call(web, "POST", "waypoint", """{"bearing":0}""");
        Assert.Equal((true, JsonValueKind.Null), (view.GetProperty("square").GetProperty("here").GetBoolean(), view.GetProperty("system").ValueKind));
        Assert.Single(net.Asked);

        // Reset: a new trip, beginning in the system the commander is in.
        (status, view) = await Call(web, "POST", "reset", "{}");
        Assert.Equal((200, 1, "Thin One"), (status, view.GetProperty("visited").GetInt32(), view.GetProperty("system").GetString()));
        Assert.Equal(1, trip.View.Visited);
    }

    [Fact]
    public async Task A_waypoint_needs_to_know_where_the_commander_is_and_which_way_and_comes_without_a_name_when_Spansh_is_silent()
    {
        File.WriteAllText(Path.Combine(temp, "edastro-sector-list.csv"), SectorList);   // the map is on disk from an earlier day
        string journal = game.Journal("Journal.2026-10-09T030000.01.log", ["""{ "timestamp":"2026-10-09T03:00:00Z", "event":"Fileheader", "part":1 }"""]);
        using GameWatcher watcher = game.Watch();
        var net = new SpanshStandIn();   // offline
        using Spansh spansh = NavSpanshTests.Quick(net);
        using var web = new WebServer(temp);
        ExploreApi.Map(web, watcher.State, new Settings(Path.Combine(temp, "settings.json")), spansh, new ExploreCoverage(temp, new SpanshStandIn()));
        watcher.Poll();

        var (status, view) = await Call(web, "POST", "waypoint", """{"bearing":90}""");
        Assert.Equal((400, "Studio does not know where you are yet. Start Elite, and it will follow you."), (status, view.GetProperty("error").GetString()));

        // One arrival is a place but no direction.
        game.Append(journal, Arrival("Location", "West One", 12, -33985));
        watcher.Poll();
        (status, view) = await Call(web, "POST", "waypoint", "{}");
        Assert.Equal((400, "Studio cannot tell yet which way you are travelling. Make a few jumps, or click the map to pick a direction."), (status, view.GetProperty("error").GetString()));
        Assert.Empty(net.Asked);

        // With a direction picked the square is found. Spansh cannot be reached: the square without a name, and the reason.
        (status, view) = await Call(web, "POST", "waypoint", """{"bearing":90}""");
        Assert.Equal((200, 3501, JsonValueKind.Null), (status, view.GetProperty("square").GetProperty("known").GetInt32(), view.GetProperty("system").ValueKind));
        Assert.Equal("Spansh could not be reached. Check your internet connection and try again.", view.GetProperty("problem").GetString());

        // Spansh answers, but knows nothing near: said in so many words.
        net.Then("""{"count":0,"from":0,"results":[],"size":1}""");
        (_, view) = await Call(web, "POST", "waypoint", """{"bearing":90}""");
        Assert.Equal("Spansh knows no system near the middle of that square, so Studio has no name to give you.", view.GetProperty("problem").GetString());
    }

    // ------------------------------------------------------------ the real services

    /// <summary>
    /// Goes out to the internet, with one request to EDAstro and one to Spansh, to see that both
    /// still answer in the layout Studio reads. Skipped unless the environment variable
    /// ELITE_STUDIO_LIVE is 1; run it on its own, as the top of TradeSearchLiveTests.cs shows, with
    /// -class EliteStudio.Tests.ExploreCoverageTests. Nothing is kept on disk: the copy is pointed at a folder that is not there.
    /// </summary>
    [Fact]
    public async Task Live_EDAstro_sends_the_list_and_Spansh_names_a_system_near_a_thin_square()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ELITE_STUDIO_LIVE") == "1", "Set ELITE_STUDIO_LIVE=1 to ask EDAstro and Spansh over the internet.");
        var coverage = new ExploreCoverage(Path.Combine(temp, "no such folder"));

        Assert.Null(await coverage.Refresh());
        SectorMap map = coverage.Map!;
        int squares = map.Known.Count(known => known > 0);
        output.WriteLine($"{map.Width} x {map.Height} squares, {squares} with known systems, {map.Known.Sum(known => (long)known):N0} systems in all, thin below {map.ThinBelow:N0}");
        Assert.InRange(map.Width, 60, 90);
        Assert.InRange(map.Height, 60, 90);
        Assert.InRange(squares, 3000, map.Width * map.Height);
        Assert.InRange(map.Known[14 * map.Width + 35], 200_000, int.MaxValue);   // Sol's own square

        ThinSquare thin = map.Ahead(0, 0, 270)!;   // west from Sol
        using var spansh = new Spansh();
        NearSystem near = (await spansh.Nearest(thin.X, thin.Y, thin.Z))!;
        double away = ShipMath.Distance([thin.X, thin.Y, thin.Z], [near.X, near.Y, near.Z]);
        output.WriteLine($"West of Sol the first thin square holds {thin.Known:N0} known systems round {thin.X:0}, {thin.Y:0}, {thin.Z:0}; Spansh's nearest system is {near.Name}, {away:0.0} ly from that point");
        Assert.InRange(thin.X, -45000, -65);   // west of Sol's square
        Assert.NotEmpty(near.Name);
        Assert.InRange(away, 0, 1001);
    }
}
