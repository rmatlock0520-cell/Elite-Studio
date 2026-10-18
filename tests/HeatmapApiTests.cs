using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Nav;

namespace EliteStudio.Tests;

/// <summary>
/// The 3D map's routes (<see cref="HeatmapApi"/>), called through the server's own routing without a
/// network (only the test of the "heatmap" event listens on a port of this PC, as a page does).
/// Nothing goes near the internet: <see cref="SpanshStandIn"/> stands in for Spansh and for
/// EDAstro, and with its script used up it behaves like a PC that is offline. The commander is the
/// owner's, in Wredguia (38, 32, 18), in a practice journal. In tests\Fixtures\nav:
///   sector-list.csv          EDAstro's real sector list of 8 October 2026, cut down (see SectorCubesTests)
///   nebulae-coordinates.csv  11 of the real lines of EDAstro's list of nebulae (see NebulaListTests)
///   system-names.json        Spansh's name search for "Maia" (see NavSpanshTests)
///   first-discoveries.log    a made-up trip with first discoveries (see ExploreTests)
/// Spansh's answers to the box searches are made up here in its layout, as in HeatmapSurveyTests.
/// </summary>
public sealed class HeatmapApiTests : IDisposable
{
    const string Unreachable = "Spansh could not be reached. Check your internet connection and try again.";
    const string Wredguia = "s:38:32:18";   // the commander's own sector
    const string Sphuedu = "s:38:33:18";    // the one above it

    static readonly (double X, double Y, double Z) Player = (-167.09, 26.72, -55.38);

    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioHeatmapApi-").FullName;
    readonly PracticeGame game = new();
    readonly SpanshStandIn net = new(), edastro = new();
    GameWatcher? watcher;
    Spansh? spansh;
    WebServer? web;
    HeatmapFinds? finds;   // what the routes answer of the finds and the route's check; its checks are waited for here
    HeatmapReach? reach;   // what the routes answer of Within reach; its lists are waited for here
    NavScan? scan;         // what the routes answer of Nav scan; its pictures are waited for here
    readonly List<(string Sound, int Volume)> played = [];                                            // Nav scan's sounds, never played aloud
    readonly Dictionary<string, PictureText> texts = new(StringComparer.OrdinalIgnoreCase);         // what the stand-in reader reads off each picture

    public void Dispose()
    {
        scan?.Dispose();
        web?.Dispose();
        spansh?.Dispose();
        watcher?.Dispose();
        game.Dispose();
        Directory.Delete(temp, recursive: true);
    }

    string Folder => Path.Combine(temp, "heatmap");

    string Pictures => Path.Combine(temp, "pictures");

    /// <summary>
    /// Studio starts, with the 3D map's routes. <paramref name="listed"/>: EDAstro's sector list is on
    /// this PC from earlier today. <paramref name="placed"/>: the journal says where the commander is.
    /// </summary>
    void Start(bool listed = true, bool placed = true, HttpMessageHandler? spanshNet = null, FineMap? fine = null, params string[] lines)
    {
        if (listed) File.WriteAllText(Path.Combine(temp, "edastro-sector-list.csv"), SpanshStandIn.Recorded("sector-list.csv"));
        string header = """{ "timestamp":"2026-10-14T10:00:00Z", "event":"Fileheader", "part":1 }""";
        string location = string.Create(CultureInfo.InvariantCulture,
            $$"""{ "timestamp":"2026-10-14T10:00:10Z", "event":"Location", "Docked":false, "StarSystem":"Practice Home", "SystemAddress":1001, "StarPos":[{{Player.X}},{{Player.Y}},{{Player.Z}}] }""");
        game.Journal("Journal.2026-10-14T100000.01.log", [.. placed ? [header, location] : new[] { header }, .. lines]);
        watcher = game.Watch();
        spansh = new Spansh(spanshNet ?? net);
        web = new WebServer(temp);
        var settings = new Settings(Path.Combine(temp, "settings.json"));
        // The finds and Within reach as the routes would make them, made here so their work can be waited for: one
        // survey (the 3D map's gate and budget), one detailed map and one memory of checks (heatmap\checks.json) for both.
        var survey = new HeatmapSurvey(spansh, Folder);
        FineMap detailed = fine ?? new FineMap(Folder);
        var memory = new CheckMemory(Folder);
        finds = new HeatmapFinds(watcher.State, survey, detailed, settings, memory);
        reach = new HeatmapReach(watcher.State, survey, detailed, memory, settings) { RangeWait = TimeSpan.Zero };
        // Nav scan watching a pictures folder of the test's own, its reader and its sounds stand-ins.
        Directory.CreateDirectory(Pictures);
        scan = new NavScan(watcher.State, finds, Folder, settings, () => [Pictures])
        {
            Reader = (path, _) => Task.FromResult(texts.GetValueOrDefault(Path.GetFileName(path)) ?? new PictureText([])),
            Play = (sound, volume) =>
            {
                lock (played) played.Add((sound, volume));
            },
            SettleWait = TimeSpan.Zero,
        };
        Assert.Same(finds, HeatmapApi.Map(web, watcher.State, new ExploreTrip(watcher.State, settings), spansh,
            new ExploreCoverage(temp, edastro), survey, new NebulaList(Folder, edastro), new FirstsLog(watcher.State, game.JournalDir, Folder),
            fineMap: detailed, settings: settings, finds: finds, reach: reach, navScan: scan));
        watcher.Poll();
    }

    /// <summary>One of the map's routes, e.g. "block?ix=39&amp;iz=18", with a JSON body for a POST.</summary>
    async Task<(int Status, JsonElement Json)> Call(string method, string route, string body = "")
    {
        string[] parts = route.Split('?', 2);
        WebResponse answer = await web!.Handle(new WebRequest(method, "/api/nav/heatmap/" + parts[0], WebServer.ParseQuery(parts.Length == 2 ? parts[1] : ""), body));
        return (answer.Status, JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(answer.Body)));
    }

    static string Text(JsonElement json) => JsonSerializer.Serialize(json);

    static string Error(JsonElement json) => json.GetProperty("error").GetString()!;

    static IEnumerable<string> Names(JsonElement results) => results.GetProperty("results").EnumerateArray().Select(found => found.GetProperty("name").GetString()!);

    // ------------------------------------------------------------ Spansh's answers, made up in its layout

    static string Counted(int count) => $$"""{"count":{{count}},"from":0,"results":[],"size":1}""";

    sealed record Star(string Name, long Id64, double X, double Y, double Z, int? BodyCount = 1, int Bodies = 1);

    static string Listed(int count, IEnumerable<Star> stars) => JsonSerializer.Serialize(new
    {
        count,
        from = 0,
        results = stars.Select(star => new Dictionary<string, object?>
        {
            ["bodies"] = Enumerable.Repeat(new { name = "a body" }, star.Bodies),
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

    /// <summary>The game's number for the system with this number in the cube of this mass code that holds the point.</summary>
    static long Address(double x, double y, double z, int mass, long number)
    {
        (int sx, int sy, int sz) = Galaxy.SectorOf(x, y, z);
        Box sector = Galaxy.SectorBounds(sx, sy, sz);
        double side = Galaxy.CubeLy(mass);
        return new CubePlace(sx, sy, sz, (int)Math.Floor((x - sector.X0) / side), (int)Math.Floor((y - sector.Y0) / side), (int)Math.Floor((z - sector.Z0) / side), mass, number).Address;
    }

    /// <summary>Page <paramref name="page"/> of a listing of that many made-up systems in Sphuedu's south-west corner.</summary>
    static string Page(int count, int page) => Listed(count,
        Enumerable.Range(page * Spansh.PageSize, Math.Clamp(count - page * Spansh.PageSize, 0, Spansh.PageSize))
            .Select(i => new Star($"Star {i}", Address(-1340, 1260, -1060, 1, i), -1340, 1260, -1060)));

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

    // ------------------------------------------------------------ the block

    [Fact]
    public async Task The_block_round_the_commander_has_its_layers_sectors_nebulae_and_says_what_is_on_this_PC()
    {
        Start();

        var (status, block) = await Call("GET", "block");

        Assert.Equal(200, status);
        Assert.Equal(["sectorLy", "origin", "centre", "layers", "sectors", "nebulae", "player", "sectorList", "nebulaList", "budget", "fine", "target", "routeEnd"],
            block.EnumerateObject().Select(field => field.Name));
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (block.GetProperty("target").ValueKind, block.GetProperty("routeEnd").ValueKind));   // nothing targeted, no route
        Assert.Equal((1280, "[-49985,-40985,-24105]", "[38,32,18]"), (block.GetProperty("sectorLy").GetInt32(), Text(block.GetProperty("origin")), Text(block.GetProperty("centre"))));
        // The cut-down list has no row round the commander's column: the two layers either side of the middle, nothing known
        // in them, and the commander at the height of the upper one.
        Assert.Equal(
            """[{"iy":31,"name":"Just below the middle","yFrom":-1305,"yTo":-25,"known":0,"here":false},{"iy":32,"name":"Just above the middle","yFrom":-25,"yTo":1255,"known":0,"here":true}]""",
            Text(block.GetProperty("layers")));
        JsonElement[] sectors = [.. block.GetProperty("sectors").EnumerateArray()];
        Assert.Equal(2 * 9, sectors.Length);
        Assert.Equal($$"""{"ix":37,"iy":31,"iz":17,"name":"{{ProcGenNames.SectorName(37, 31, 17)}}","known":0,"first":null,"survey":null,"fine":null}""", Text(sectors[0]));
        Assert.Equal("""{"ix":38,"iy":32,"iz":18,"name":"Wredguia","known":0,"first":null,"survey":null,"fine":null}""", Text(sectors[9 + 4]));
        Assert.Equal("""{"x":-167.09,"y":26.72,"z":-55.38,"system":"Practice Home","heading":null,"sector":[38,32,18]}""", Text(block.GetProperty("player")));
        Assert.Equal("""{"have":true,"stale":false}""", Text(block.GetProperty("sectorList")));
        Assert.Equal("""{"have":false,"stale":true}""", Text(block.GetProperty("nebulaList")));
        Assert.Equal("""{"used":0,"cap":200}""", Text(block.GetProperty("budget")));

        // The nebulae that reach into the block, smallest first, each with its sphere (or spheres) and its permit.
        JsonElement[] nebulae = [.. block.GetProperty("nebulae").EnumerateArray()];
        Assert.Equal(
            """{"name":"Col 285 Sector","label":"Col 285","x":-53.46875,"y":56.27344,"z":-19.35547,"r":326,"known":0,"permit":false,"spheres":[[-53.46875,56.27344,-19.35547,326]]}""",
            Text(nebulae.Single(nebula => nebula.GetProperty("name").GetString() == "Col 285 Sector")));
        JsonElement horsehead = nebulae.Single(nebula => nebula.GetProperty("name").GetString() == "Horsehead Dark Region");
        Assert.Equal(("Horsehead Nebula", true), (horsehead.GetProperty("label").GetString(), horsehead.GetProperty("permit").GetBoolean()));
        Assert.Contains("California Sector", nebulae.Select(nebula => nebula.GetProperty("name").GetString()));
        Assert.DoesNotContain("Rosette Sector", nebulae.Select(nebula => nebula.GetProperty("name").GetString()));   // 4,700 ly south
        Assert.Equal(nebulae.OrderBy(nebula => nebula.GetProperty("r").GetDouble()).Select(Text), nebulae.Select(Text));

        // The column east of it holds two of the list's sectors and both of its nebulae. The commander's height is still the one shown first.
        (status, block) = await Call("GET", "block?ix=39&iz=18");
        Assert.Equal((200, "[39,32,18]"), (status, Text(block.GetProperty("centre"))));
        Assert.Equal([156257, 237198], block.GetProperty("layers").EnumerateArray().Select(layer => layer.GetProperty("known").GetInt32()));
        Assert.Equal("""{"ix":40,"iy":32,"iz":18,"name":"Plio Eurl","known":237198,"first":"2015-09-12","survey":null,"fine":null}""",
            Text(block.GetProperty("sectors").EnumerateArray().Single(sector => sector.GetProperty("name").GetString() == "Plio Eurl")));
        Assert.Equal([1035, 1694], block.GetProperty("nebulae").EnumerateArray()
            .Where(nebula => nebula.GetProperty("name").GetString() is "Tr 10 Sector" or "Eight Burst Sector").Select(nebula => nebula.GetProperty("known").GetInt32()));

        // A column that is not in the grid is the player's mistake.
        foreach (string wrong in new[] { "block?ix=128", "block?ix=-1&iz=18", "block?iz=north" })
            Assert.Equal((400, "Sectors are numbered 0 to 127 from west to east and from south to north."), ((await Call("GET", wrong)).Status, Error((await Call("GET", wrong)).Json)));
    }

    [Fact]
    public async Task Without_the_lists_or_the_commanders_place_the_block_opens_on_Sol_with_names_but_no_counts()
    {
        Start(listed: false, placed: false);

        var (status, block) = await Call("GET", "block");

        Assert.Equal((200, "[39,32,18]"), (status, Text(block.GetProperty("centre"))));
        Assert.Equal(JsonValueKind.Null, block.GetProperty("player").ValueKind);
        Assert.Equal(
            """[{"iy":31,"name":"Just below the middle","yFrom":-1305,"yTo":-25,"known":null,"here":false},{"iy":32,"name":"Just above the middle","yFrom":-25,"yTo":1255,"known":null,"here":false}]""",
            Text(block.GetProperty("layers")));
        Assert.Equal("""{"ix":38,"iy":32,"iz":18,"name":"Wredguia","known":null,"first":null,"survey":null,"fine":null}""", Text(block.GetProperty("sectors")[9 + 3]));
        Assert.All(block.GetProperty("nebulae").EnumerateArray(), nebula => Assert.Equal(JsonValueKind.Null, nebula.GetProperty("known").ValueKind));
        Assert.Equal("""{"have":false,"stale":true}""", Text(block.GetProperty("sectorList")));

        // Without a list nobody knows what EDAstro counts; without the commander's place there is nothing to count around.
        Assert.Equal("""{"key":"s:38:32:18","stored":false,"edastroKnown":null}""", Text((await Call("GET", "area?key=" + Wredguia)).Json));
        var (refused, answer) = await Call("POST", "count", """{"kind":"around","go":true}""");
        Assert.Equal((400, "Studio does not know where you are yet. Start Elite, and it will follow you."), (refused, Error(answer)));
        Assert.Empty(net.Asked);
        Assert.Empty(edastro.Asked);
    }

    // ------------------------------------------------------------ the search

    [Fact]
    public async Task A_place_is_found_by_how_its_name_begins_then_by_how_near_it_is_and_by_the_names_players_know()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "nebulae.csv"), SpanshStandIn.Recorded("nebulae-coordinates.csv"));   // EDAstro's names, fetched earlier
        Start();

        // A nebula by the name players know it by, or its own.
        var (status, found) = await Call("GET", "find?q=california&kind=place");
        Assert.Equal(200, status);
        JsonElement california = Assert.Single(found.GetProperty("results").EnumerateArray());
        Assert.Equal("""{"kind":"nebula","name":"California Sector","label":"California Nebula","x":-332.56641,"y":-213.03125,"z":-918.70508,"sector":[38,31,18]""",
            Text(california)[..Text(california).IndexOf(",\"distance\"", StringComparison.Ordinal)]);
        Assert.Equal((100, false), (california.GetProperty("r").GetDouble(), california.GetProperty("permit").GetBoolean()));
        Assert.InRange(california.GetProperty("distance").GetDouble(), 911.1, 911.2);   // from the commander
        Assert.Equal(JsonValueKind.Null, found.GetProperty("problem").ValueKind);
        Assert.Equal("California Sector", Names((await Call("GET", "find?q=California%20Nebula&kind=place")).Json).First());
        Assert.Equal("Rosette Sector", Names((await Call("GET", "find?q=rosette%20neb")).Json).First());   // "place" is the default

        // Those whose name begins with the text first, then those with a word that begins with it, then the
        // rest; nearest first within each. The Monkey Head is 6,000 ly away, the Horsehead's two 1,000 and 1,400.
        Assert.Equal(["Witch Head Sector", "Parrot's Head Sector", "Monkey Head Sector", "Horsehead Sector", "Horsehead Dark Region"],
            Names((await Call("GET", "find?q=head")).Json));
        Assert.Equal(["Ingais", "Ingairl", "Ingaiq"], Names((await Call("GET", "find?q=INGAI")).Json));   // from the sector list
        Assert.Equal(["Eos Fraae", "Eorm Fraae"], Names((await Call("GET", "find?q=fraae")).Json));

        // A sector's name typed in full, though the cut-down list has no row for it: the game's rule knows where it is.
        Assert.Equal("""{"kind":"sector","name":"Wredguia","label":"Wredguia","x":-705,"y":615,"z":-425,"sector":[38,32,18]""",
            Text((await Call("GET", "find?q=wredguia")).Json.GetProperty("results")[0]).Split(",\"distance\"")[0]);

        // EDAstro's list adds the names the table does not have, and nothing twice.
        JsonElement soul = (await Call("GET", "find?q=kay")).Json.GetProperty("results").EnumerateArray().Single(place => place.GetProperty("name").GetString() == "Dr. Kay's Soul");
        Assert.Equal(("nebula", -8241.25, false, false), (soul.GetProperty("kind").GetString(), soul.GetProperty("x").GetDouble(), soul.TryGetProperty("r", out _), soul.GetProperty("permit").GetBoolean()));
        Assert.Equal(["Pleiades Sector"], Names((await Call("GET", "find?q=pleiades")).Json));

        Assert.Equal("""{"results":[],"problem":null}""", Text((await Call("GET", "find?q=%20")).Json));
        var (refused, answer) = await Call("GET", "find?q=sol&kind=planet");
        Assert.Equal((400, "Search for a place or a star."), (refused, Error(answer)));
        Assert.Empty(net.Asked);   // finding a place asks nobody
    }

    [Fact]
    public async Task A_star_is_found_through_Spansh_or_from_its_name_alone_and_an_unreachable_Spansh_is_a_problem_not_an_error()
    {
        Start();
        net.Then(SpanshStandIn.Recorded("system-names.json"));

        // Spansh's suggestions, with their places and numbers (as text).
        var (status, found) = await Call("GET", "find?q=Maia&kind=star");
        Assert.Equal(200, status);
        Assert.Equal(["Maia", "Maiadiwobog", "Maiawa"], Names(found));
        JsonElement maia = found.GetProperty("results")[0];
        Assert.Equal(("star", "8216113749", "[38,31,18]", -343.375), (maia.GetProperty("kind").GetString(), maia.GetProperty("id64").GetString(), Text(maia.GetProperty("sector")), maia.GetProperty("z").GetDouble()));
        Assert.False(maia.TryGetProperty("plusMinus", out _));
        Assert.Equal(["GET /api/systems/field_values/system_names?q=Maia"], net.Asked);

        // Spansh out of reach: a generated name still gives its place, to within half its cube, and says why there is no more.
        const string Named = "Wredguia WD-K d8-65";
        (status, found) = await Call("GET", "find?kind=star&q=" + Uri.EscapeDataString(Named.ToLowerInvariant()));
        Assert.Equal((200, Unreachable), (status, found.GetProperty("problem").GetString()));
        JsonElement decoded = Assert.Single(found.GetProperty("results").EnumerateArray());
        Assert.Equal(("decoded", Named, -345.0, 15.0, -305.0, 40.0, "[38,32,18]"),
            (decoded.GetProperty("kind").GetString(), decoded.GetProperty("name").GetString(), decoded.GetProperty("x").GetDouble(), decoded.GetProperty("y").GetDouble(),
             decoded.GetProperty("z").GetDouble(), decoded.GetProperty("plusMinus").GetDouble(), Text(decoded.GetProperty("sector"))));
        Assert.Equal(ProcGenNames.Decode(Named)!.Cube.Address.ToString(CultureInfo.InvariantCulture), decoded.GetProperty("id64").GetString());
        Assert.False(decoded.GetProperty("checked").GetBoolean());   // Spansh could not be asked: nobody has said whether a database has it

        // When Spansh knows the star, its own place is given and not the guess.
        net.Then("""{"min_max":[{"id64":"3238296097059","name":"Synuefai YQ-A c15-1","x":-680.5,"y":-690.25,"z":-400.125}],"values":["Synuefai YQ-A c15-1"]}""");
        found = (await Call("GET", "find?kind=star&q=Synuefai%20YQ-A%20c15-1")).Json;
        Assert.Equal([("star", -680.5)], found.GetProperty("results").EnumerateArray().Select(star => (star.GetProperty("kind").GetString(), star.GetProperty("x").GetDouble())));

        // Spansh answers without the name: then, and only then, no database has recorded it.
        net.Then("""{"min_max":[],"values":[]}""");
        JsonElement unknown = Assert.Single((await Call("GET", "find?kind=star&q=Synuefai%20YQ-A%20c15-2")).Json.GetProperty("results").EnumerateArray());
        Assert.Equal(("decoded", true), (unknown.GetProperty("kind").GetString(), unknown.GetProperty("checked").GetBoolean()));

        // Name suggestions are not the map's own requests: they leave its budget alone.
        Assert.Equal("""{"used":0,"cap":200}""", Text((await Call("GET", "block")).Json.GetProperty("budget")));
    }

    // ------------------------------------------------------------ looking inside, counting, cancelling

    [Fact]
    public async Task Looking_inside_asks_first_when_it_is_big_then_lists_keeps_and_shows_what_it_found()
    {
        Start();

        // 2,521 systems take six more requests: the player is asked first.
        net.Then(Counted(2521));
        var (status, survey) = await Call("POST", "survey", $$"""{"key":"{{Sphuedu}}"}""");
        Assert.Equal((200, """{"key":"s:38:33:18","count":2521,"capped":false,"needs":{"requests":6,"megabytes":1.16,"left":199}}"""), (status, Text(survey)));
        // The count is kept on this PC at once, with nothing listed yet: it is not asked for again.
        JsonElement counted = (await Call("GET", "area?key=" + Sphuedu)).Json;
        Assert.Equal((true, 2521, false, 0, 6, "[]", 0), (counted.GetProperty("stored").GetBoolean(), counted.GetProperty("count").GetInt32(), counted.GetProperty("complete").GetBoolean(),
            counted.GetProperty("pagesDone").GetInt32(), counted.GetProperty("pagesTotal").GetInt32(), Text(counted.GetProperty("cubes")), counted.GetProperty("unlisted").GetInt32()));

        // Two systems in the commander's sector: one count and one page, at once.
        Star[] two =
        [
            new("Wredguia AA-A c0-0", Address(Player.X, Player.Y, Player.Z, 2, 0), Player.X, Player.Y, Player.Z, BodyCount: 3, Bodies: 3),
            new("Wredguia AA-A c0-1", Address(Player.X, Player.Y, Player.Z, 2, 1), -166.5, Player.Y, Player.Z, BodyCount: null, Bodies: 0),
        ];
        net.Then(Counted(2)).Then(Listed(2, two));
        (status, survey) = await Call("POST", "survey", """{"key":"s:38:32:18","go":false}""");
        Assert.Equal(200, status);
        Assert.Equal((2, true, 1, 1, 160, 8, "[-1345,-25,-1065]", true), (survey.GetProperty("count").GetInt32(), survey.GetProperty("complete").GetBoolean(),
            survey.GetProperty("pagesDone").GetInt32(), survey.GetProperty("pagesTotal").GetInt32(), survey.GetProperty("cubeLy").GetInt32(), survey.GetProperty("n").GetInt32(),
            Text(survey.GetProperty("corner")), survey.GetProperty("stored").GetBoolean()));
        Assert.Equal("""[{"i":7,"j":0,"k":6,"known":2,"full":1,"part":0,"never":1,"missing":0}]""", Text(survey.GetProperty("cubes")));
        Assert.Equal(
            $$"""[{"name":"Wredguia AA-A c0-0","id64":"{{two[0].Id64}}","x":-167.09,"y":26.72,"z":-55.38,"state":"full"},{"name":"Wredguia AA-A c0-1","id64":"{{two[1].Id64}}","x":-166.5,"y":26.72,"z":-55.38,"state":"never"}]""",
            Text(survey.GetProperty("systems")));
        Assert.Equal((JsonValueKind.Null, false), (survey.GetProperty("problem").ValueKind, survey.GetProperty("stopped").GetBoolean()));

        // What was kept is the same from GET area, without asking; and in other cube sizes.
        JsonObject kept = JsonNode.Parse(Text(survey))!.AsObject();
        kept.Remove("problem");
        kept.Remove("stopped");
        var (_, area) = await Call("GET", "area?key=" + Wredguia);
        Assert.Equal(kept.ToJsonString(), Text(area));
        Assert.Equal((80, 16), ((await Call("GET", $"area?key={Wredguia}&cubeLy=80")).Json.GetProperty("cubeLy").GetInt32(), (await Call("GET", $"area?key={Wredguia}&cubeLy=80")).Json.GetProperty("n").GetInt32()));
        Assert.Equal(3, net.Asked.Count);

        // The block shows that the sector was looked inside, and the requests spent.
        JsonElement block = (await Call("GET", "block")).Json;
        JsonElement summary = block.GetProperty("sectors").EnumerateArray().Single(sector => sector.GetProperty("name").GetString() == "Wredguia").GetProperty("survey");
        Assert.Equal((2, false, "complete"), (summary.GetProperty("count").GetInt32(), summary.GetProperty("capped").GetBoolean(), summary.GetProperty("state").GetString()));
        Assert.InRange(summary.GetProperty("at").GetDateTime(), DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("""{"used":3,"cap":200}""", Text(block.GetProperty("budget")));

        // A nebula is named as players know it; the answer spells it as the game does.
        Assert.Equal("""{"key":"n:California Sector","stored":false,"edastroKnown":0}""", Text((await Call("GET", "area?key=n:california%20nebula")).Json));

        // The player's mistakes.
        foreach (string wrong in new[] { "area?key=nowhere", "area?key=s:128:32:18", "area" })
            Assert.Equal((400, "Studio knows no sector or nebula by that name."), ((await Call("GET", wrong)).Status, Error((await Call("GET", wrong)).Json)));
        foreach (string wrong in new[] { "100", "5", "big" })
            Assert.Equal((400, "Cubes are 10, 20, 40, 80, 160, 320, 640 or 1,280 light years on a side."),
                ((await Call("GET", $"area?key={Wredguia}&cubeLy={wrong}")).Status, Error((await Call("GET", $"area?key={Wredguia}&cubeLy={wrong}")).Json)));
        (status, survey) = await Call("POST", "survey", """{"key":"n:Nowhere Nebula"}""");
        Assert.Equal((400, "Studio knows no sector or nebula by that name."), (status, Error(survey)));
        Assert.Equal(3, net.Asked.Count);
    }

    [Fact]
    public async Task When_Spansh_or_EDAstro_cannot_be_reached_the_answer_says_so_and_cube_counts_ask_first_above_five()
    {
        Start();

        // Spansh is out of reach: nothing kept, and the reason.
        var (status, survey) = await Call("POST", "survey", $$"""{"key":"{{Wredguia}}","go":true}""");
        Assert.Equal((200, """{"key":"s:38:32:18","count":0,"capped":false,"stored":false,"edastroKnown":0,"problem":"Spansh could not be reached. Check your internet connection and try again.","stopped":false}"""),
            (status, Text(survey)));
        var (_, count) = await Call("POST", "count", string.Create(CultureInfo.InvariantCulture, $$"""{"kind":"cube","x":{{Player.X}},"y":{{Player.Y}},"z":{{Player.Z}}}"""));
        Assert.Equal("""{"kind":"cube","cubes":[],"needs":null,"problem":"Spansh could not be reached. Check your internet connection and try again.","stopped":false}""", Text(count));

        // "Survey around me" is 27 counts: the player is asked first, and nobody is asked yet. The commander's place is the middle.
        (status, count) = await Call("POST", "count", """{"kind":"around"}""");
        Assert.Equal((200, """{"kind":"around","cubes":[],"needs":{"requests":27,"megabytes":0.13,"left":198},"problem":null,"stopped":false}"""), (status, Text(count)));
        Assert.Equal(2, net.Asked.Count);

        // Spansh is back: the commander's cube of 160 ly, on the grid through the sectors' corners, counted and kept.
        net.Then(Counted(42));
        (_, count) = await Call("POST", "count", string.Create(CultureInfo.InvariantCulture, $$"""{"kind":"CUBE","x":{{Player.X}},"y":{{Player.Y}},"z":{{Player.Z}}}"""));
        JsonElement cube = Assert.Single(count.GetProperty("cubes").EnumerateArray());
        Assert.Equal((-225, -25, -105, 160, 42, false), (cube.GetProperty("x0").GetInt32(), cube.GetProperty("y0").GetInt32(), cube.GetProperty("z0").GetInt32(),
            cube.GetProperty("size").GetInt32(), cube.GetProperty("known").GetInt32(), cube.GetProperty("capped").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, count.GetProperty("problem").ValueKind);
        Assert.Equal(Text(count.GetProperty("cubes")), Text((await Call("GET", "counted")).Json.GetProperty("cubes")));

        // The player's mistakes.
        foreach (var (wrong, why) in new[]
        {
            ("""{"kind":"square","x":0,"y":0,"z":0}""", "Studio counts a cube, a column or the cubes around you."),
            ("""{"kind":"column"}""", "Pick the place to count first."),
            ("""{"kind":"cube","x":1000000,"y":0,"z":0}""", "That place is outside the galaxy's sectors."),
        })
        {
            var (refused, answer) = await Call("POST", "count", wrong);
            Assert.Equal((400, why), (refused, Error(answer)));
        }
        Assert.Equal(3, net.Asked.Count);

        // EDAstro's list of nebulae: none on this PC, and EDAstro out of reach.
        Assert.Equal("""{"have":false,"stale":true,"problem":null}""", Text((await Call("GET", "nebulae")).Json));
        Assert.Equal("""{"have":false,"stale":true,"problem":"EDAstro could not be reached. Check your internet connection and try again."}""", Text((await Call("POST", "nebulae", "{}")).Json));
        Assert.Single(edastro.Asked);
    }

    [Fact]
    public async Task Cancel_stops_the_listing_under_way_and_what_came_back_is_kept()
    {
        var held = new HeldPages(net);
        Start(spanshNet: held);
        net.Then(Counted(600)).Then(Page(600, 0)).Then(Page(600, 1));

        Task<(int Status, JsonElement Json)> listing = Call("POST", "survey", $$"""{"key":"{{Sphuedu}}"}""");   // three requests: at once
        await held.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var (status, cancelled) = await Call("POST", "cancel", "{}");
        held.LetGo.SetResult();
        var (_, answer) = await listing;

        Assert.Equal((200, """{"cancelled":true}"""), (status, Text(cancelled)));
        Assert.Equal((true, JsonValueKind.Null, false, 1, 2, 500), (answer.GetProperty("stopped").GetBoolean(), answer.GetProperty("problem").ValueKind,
            answer.GetProperty("complete").GetBoolean(), answer.GetProperty("pagesDone").GetInt32(), answer.GetProperty("pagesTotal").GetInt32(), answer.GetProperty("systems").GetArrayLength()));
        Assert.Equal(2, net.Asked.Count);   // the count and the first page; the second was never asked for
        Assert.Equal(500, (await Call("GET", "area?key=" + Sphuedu)).Json.GetProperty("systems").GetArrayLength());

        // With nothing under way, Cancel says the same and does nothing.
        Assert.Equal("""{"cancelled":true}""", Text((await Call("POST", "cancel", "{}")).Json));
    }

    [Fact]
    public async Task The_builds_Cancel_leaves_a_survey_under_way_to_finish()
    {
        var held = new HeldPages(net);
        Start(spanshNet: held);
        net.Then(Counted(600)).Then(Page(600, 0)).Then(Page(600, 1));

        Task<(int Status, JsonElement Json)> listing = Call("POST", "survey", $$"""{"key":"{{Sphuedu}}"}""");
        await held.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var (status, cancelled) = await Call("POST", "cancel", """{"what":"finemap"}""");
        held.LetGo.SetResult();
        var (_, answer) = await listing;

        Assert.Equal((200, """{"cancelled":true}"""), (status, Text(cancelled)));
        Assert.Equal((false, true, 600), (answer.GetProperty("stopped").GetBoolean(), answer.GetProperty("complete").GetBoolean(), answer.GetProperty("systems").GetArrayLength()));
        Assert.Equal(3, net.Asked.Count);

        // A Cancel of something the map does not run is the page's mistake.
        var (refused, said) = await Call("POST", "cancel", """{"what":"everything"}""");
        Assert.Equal((400, "Studio stops a job at Spansh (\"survey\") or the build of the detailed map (\"finemap\")."), (refused, Error(said)));
    }

    [Fact]
    public async Task A_surveys_Cancel_leaves_a_build_of_the_detailed_map_under_way_to_finish_and_the_builds_own_stops_it()
    {
        Directory.CreateDirectory(Folder);
        using (var file = File.Create(Path.Combine(Folder, FineMap.DownloadName)))
        using (var packed = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Optimal))
            packed.Write(Encoding.UTF8.GetBytes(SpanshStandIn.Recorded("systems-small.json")));
        // The build is held as it starts until the test lets it go, so a Cancel lands while it is under way.
        var fine = new FineMap(Folder);
        using var reading = new ManualResetEventSlim();
        using var letGo = new ManualResetEventSlim();
        fine.Progress += progress =>
        {
            if (progress is not { State: FineMap.Reading, Percent: 0 }) return;
            reading.Set();
            letGo.Wait(TimeSpan.FromSeconds(30));
        };
        Start(fine: fine);
        CancellationToken cancel = TestContext.Current.CancellationToken;

        // The Cancel of a survey (the side column's, under a listing): the build goes on.
        Task<(int Status, JsonElement Json)> building = Call("POST", "finemap", """{"go":true}""");
        Assert.True(reading.Wait(TimeSpan.FromSeconds(30), cancel));
        Assert.Equal("""{"cancelled":true}""", Text((await Call("POST", "cancel", """{"what":"survey"}""")).Json));
        letGo.Set();
        JsonElement built = (await building).Json;
        Assert.Equal((false, JsonValueKind.Null, 20), (built.GetProperty("stopped").GetBoolean(), built.GetProperty("problem").ValueKind, built.GetProperty("systems").GetInt32()));

        // The build's own Cancel stops it; so does a Cancel from a page that does not say which (as before "what" was there).
        foreach (string body in new[] { """{"what":"finemap"}""", "{}" })
        {
            reading.Reset();
            letGo.Reset();
            building = Call("POST", "finemap", """{"go":true}""");
            Assert.True(reading.Wait(TimeSpan.FromSeconds(30), cancel));
            Assert.Equal(200, (await Call("POST", "cancel", body)).Status);
            letGo.Set();
            Assert.True((await building).Json.GetProperty("stopped").GetBoolean(), body);
        }
        Assert.Empty(net.Asked);
    }

    // ------------------------------------------------------------ the detailed map

    [Fact]
    public async Task The_detailed_map_is_built_from_the_list_on_this_PC_asking_nobody_and_gives_each_sectors_cubes()
    {
        // Spansh's list of every system, downloaded to the 3D map's folder: the small one of FineMapTests.
        Directory.CreateDirectory(Folder);
        using (var file = File.Create(Path.Combine(Folder, FineMap.DownloadName)))
        using (var packed = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Optimal))
            packed.Write(Encoding.UTF8.GetBytes(SpanshStandIn.Recorded("systems-small.json")));
        File.SetLastWriteTimeUtc(Path.Combine(Folder, FineMap.DownloadName), new DateTime(2026, 10, 14, 11, 53, 17, DateTimeKind.Utc));
        long bytes = new FileInfo(Path.Combine(Folder, FineMap.DownloadName)).Length;
        Start();

        // The block round the commander: a list to build from, nothing built yet.
        JsonElement block = (await Call("GET", "block")).Json;
        Assert.Equal($$"""{"built":false,"at":null,"sourceDate":"2026-10-14T11:53:17Z","have":true,"ix":38,"iz":18,"exact":false,"newer":false,"bytes":{{bytes}},"seconds":10,"running":null}""",
            Text(block.GetProperty("fine")));
        Assert.All(block.GetProperty("sectors").EnumerateArray(), sector => Assert.Equal(JsonValueKind.Null, sector.GetProperty("fine").ValueKind));

        // Asked first: what it takes, and that it takes nothing from the internet.
        var (status, asked) = await Call("POST", "finemap", "{}");
        Assert.Equal((200, """{"seconds":10,"reads":"0.0 GB on your PC","internet":false}""", JsonValueKind.Null), (status, Text(asked.GetProperty("needs")), asked.GetProperty("problem").ValueKind));
        Assert.False(File.Exists(Path.Combine(Folder, "fine", "block-38-18.json")));

        // Then built, for the commander's column.
        var (built, answer) = await Call("POST", "finemap", """{"go":true}""");
        Assert.Equal((200, JsonValueKind.Null, false, 20, 9), (built, answer.GetProperty("problem").ValueKind, answer.GetProperty("stopped").GetBoolean(),
            answer.GetProperty("systems").GetInt32(), answer.GetProperty("sectors").GetInt32()));
        Assert.Equal((true, true), (answer.GetProperty("fine").GetProperty("built").GetBoolean(), answer.GetProperty("fine").GetProperty("exact").GetBoolean()));

        // The block says so, with every sector's totals from Spansh's whole list beside EDAstro's count.
        block = (await Call("GET", "block?ix=38&iz=18")).Json;
        Assert.True(block.GetProperty("fine").GetProperty("built").GetBoolean());
        Assert.Equal("""{"known":9,"star":5,"nameOnly":4,"missing":4}""",
            Text(block.GetProperty("sectors").EnumerateArray().Single(sector => sector.GetProperty("name").GetString() == "Wredguia").GetProperty("fine")));

        // Each sector's cubes of 160 ly, or 40.
        var (_, cubes) = await Call("GET", "cubes?ix=38&iy=32&iz=18");
        Assert.Equal((true, 160, 8, "[-1345,-25,-1065]", 5), (cubes.GetProperty("have").GetBoolean(), cubes.GetProperty("size").GetInt32(), cubes.GetProperty("n").GetInt32(),
            Text(cubes.GetProperty("corner")), cubes.GetProperty("cubes").GetArrayLength()));
        Assert.Equal("""{"i":6,"j":0,"k":4,"known":4,"star":2,"nameOnly":2,"missing":4}""", Text(cubes.GetProperty("cubes")[1]));
        Assert.Equal(8, (await Call("GET", "cubes?ix=38&iy=32&iz=18&size=40")).Json.GetProperty("cubes").GetArrayLength());
        Assert.False((await Call("GET", "cubes?ix=41&iy=32&iz=18")).Json.GetProperty("have").GetBoolean());

        // The player's mistakes.
        foreach (var (wrong, why) in new[]
        {
            ("cubes?ix=38&iz=18", "Layers are numbered 0 to 63 from the bottom of the galaxy's grid up."),
            ("cubes?ix=38&iy=64&iz=18", "Layers are numbered 0 to 63 from the bottom of the galaxy's grid up."),
            ("cubes?ix=128&iy=32&iz=18", "Sectors are numbered 0 to 127 from west to east and from south to north."),
            ("cubes?ix=38&iy=32&iz=18&size=80", "The detailed map keeps cubes of 160 and 40 light years."),
        })
        {
            var (refused, error) = await Call("GET", wrong);
            Assert.Equal((400, why), (refused, Error(error)));
        }
        var (outside, said) = await Call("POST", "finemap", """{"ix":200,"iz":18,"go":true}""");
        Assert.Equal((400, "Sectors are numbered 0 to 127 from west to east and from south to north."), (outside, Error(said)));
        Assert.Equal("""{"cancelled":true}""", Text((await Call("POST", "cancel", "{}")).Json));

        // Nobody was asked anything.
        Assert.Empty(net.Asked);
        Assert.Empty(edastro.Asked);
    }

    // ------------------------------------------------------------ the "heatmap" event

    [Fact]
    public async Task A_survey_and_a_count_reach_a_listening_page_as_heatmap_events_request_by_request()
    {
        // POST survey and POST count answer only when the job is over, so the events are how a page shows
        // it getting on. Hence the real server on a port of this PC here, listened to the way a page listens.
        CancellationToken cancel = TestContext.Current.CancellationToken;
        Start();
        Assert.True(web!.Start(47890));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var events = new StreamReader(await http.GetStreamAsync($"http://127.0.0.1:{web.Port}/api/events?t={web.Token}", cancel));

        async Task<string[]> NextHeatmapEvents(int count)
        {
            var got = new List<string>();
            while (got.Count < count)
            {
                string? line = await events.ReadLineAsync(cancel).AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancel);
                Assert.NotNull(line);   // null: the stream ended
                if (line.StartsWith("data: ", StringComparison.Ordinal)) got.Add(line[6..]);
                else Assert.True(line is "" or ": connected" or "event: heatmap", $"unexpected line on the stream: {line}");
            }
            return [.. got];
        }

        // 600 systems: the count and two pages, three requests, so the listing runs at once.
        net.Then(Counted(600)).Then(Page(600, 0)).Then(Page(600, 1));
        var (status, survey) = await Call("POST", "survey", $$"""{"key":"{{Sphuedu}}"}""");
        Assert.Equal((200, true, 600), (status, survey.GetProperty("complete").GetBoolean(), survey.GetProperty("systems").GetArrayLength()));
        Assert.Equal(
            [
                """{"key":"s:38:33:18","page":0,"pages":0,"state":"counting"}""",
                """{"key":"s:38:33:18","page":0,"pages":2,"state":"listing"}""",
                """{"key":"s:38:33:18","page":1,"pages":2,"state":"listing"}""",
                """{"key":"s:38:33:18","page":2,"pages":2,"state":"listing"}""",
                """{"key":"s:38:33:18","page":2,"pages":2,"state":"done"}""",
            ],
            await NextHeatmapEvents(5));

        // A count of cubes goes by its kind.
        net.Then(Counted(42));
        (status, _) = await Call("POST", "count", string.Create(CultureInfo.InvariantCulture, $$"""{"kind":"cube","x":{{Player.X}},"y":{{Player.Y}},"z":{{Player.Z}}}"""));
        Assert.Equal(200, status);
        Assert.Equal(
            [
                """{"key":"c:cube","page":0,"pages":1,"state":"counting"}""",
                """{"key":"c:cube","page":1,"pages":1,"state":"counting"}""",
                """{"key":"c:cube","page":1,"pages":1,"state":"done"}""",
            ],
            await NextHeatmapEvents(3));
        Assert.Equal(4, net.Asked.Count);

        // A survey that stops to ask the player first says it is over too, so no open page goes on showing it.
        net.Then(Counted(2521));
        (status, survey) = await Call("POST", "survey", $$"""{"key":"{{Wredguia}}"}""");
        Assert.Equal((200, 6), (status, survey.GetProperty("needs").GetProperty("requests").GetInt32()));
        Assert.Equal(
            [
                """{"key":"s:38:32:18","page":0,"pages":0,"state":"counting"}""",
                """{"key":"s:38:32:18","page":1,"pages":1,"state":"done"}""",
            ],
            await NextHeatmapEvents(2));
        Assert.Equal(5, net.Asked.Count);
    }

    // ------------------------------------------------------------ the names under the map, your finds, the game's target

    [Fact]
    public async Task The_names_in_a_square_come_from_what_is_kept_on_this_PC_grouped_by_the_games_cubes_asking_nobody()
    {
        Start();
        // Two systems of one of the game's cubes in Wredguia (see FineMapTests), numbered 0 and 3, looked inside: a count and a page.
        Star[] two =
        [
            new("Wredguia WD-K d8-0", 10410264907, -350.5, 10.25, -300.125),
            new("Wredguia WD-K d8-3", 113489480011, -306.5, 50, -266),
        ];
        net.Then(Counted(2)).Then(Listed(2, two));
        Assert.True((await Call("POST", "survey", $$"""{"key":"{{Wredguia}}"}""")).Json.GetProperty("complete").GetBoolean());

        var (status, names) = await Call("GET", "names?kind=sector&key=s:38:32:18");

        Assert.Equal((200, "survey", false, JsonValueKind.Null), (status, names.GetProperty("source").GetString(), names.GetProperty("truncated").GetBoolean(), names.GetProperty("problem").ValueKind));
        Assert.Equal(
            """[{"group":"Wredguia WD-K d8","mass":"d","centre":[-345,15,-305],"count":2,"known":"0, 3","gaps":["Wredguia WD-K d8-1","Wredguia WD-K d8-2"],"next":["Wredguia WD-K d8-4","Wredguia WD-K d8-5","Wredguia WD-K d8-6"],"names":[{"name":"Wredguia WD-K d8-0","id64":"10410264907","x":-350.5,"y":10.25,"z":-300.125},{"name":"Wredguia WD-K d8-3","id64":"113489480011","x":-306.5,"y":50,"z":-266}],"gapsTotal":2}]""",
            Text(names.GetProperty("groups")));
        // One cube's names, as the page asks for them in a busy square; and a cube of the sector, chosen on the map.
        Assert.Equal(2, (await Call("GET", "names?kind=sector&key=s:38:32:18&group=Wredguia%20WD-K%20d8")).Json.GetProperty("groups")[0].GetProperty("names").GetArrayLength());
        JsonElement cube = (await Call("GET", "names?kind=cube&key=s:38:32:18&x0=-385&y0=-25&z0=-305&size=40")).Json;
        Assert.Equal(["Wredguia WD-K d8-0"], cube.GetProperty("groups")[0].GetProperty("names").EnumerateArray().Select(named => named.GetProperty("name").GetString()));

        // Nothing kept for a nebula: no names.
        Assert.Equal("""{"source":null,"at":null,"groups":[],"truncated":false,"problem":null}""", Text((await Call("GET", "names?kind=nebula&key=n:California%20Nebula")).Json));

        // The player's mistakes.
        const string Cube = "A cube is given by its corner (x0, y0, z0) and its size, up to 1,280 light years.";
        foreach (var (wrong, why) in new[]
        {
            ("names?kind=planet&key=s:38:32:18", "Studio lists the names in a sector, a cube or a nebula."),
            ("names?kind=sector&key=nowhere", "Studio knows no sector or nebula by that name."),
            ("names?kind=sector&key=n:California%20Sector", "Studio knows no sector or nebula by that name."),
            ("names?kind=nebula&key=s:38:32:18", "Studio knows no sector or nebula by that name."),
            ("names?kind=cube&key=s:38:32:18&x0=-385&y0=-25&z0=-305", Cube),
            ("names?kind=cube&key=s:38:32:18&x0=-385&y0=-25&z0=-305&size=5000", Cube),
        })
        {
            var (refused, answer) = await Call("GET", wrong);
            Assert.Equal((400, why), (refused, Error(answer)));
        }
        Assert.Equal(2, net.Asked.Count);   // the count and the page: the names asked nobody
    }

    [Fact]
    public async Task The_names_of_a_square_come_with_its_totals_and_take_the_cameras_focus()
    {
        Start();
        Star[] two =
        [
            new("Wredguia WD-K d8-0", 10410264907, -350.5, 10.25, -300.125),
            new("Wredguia WD-K d8-3", 113489480011, -306.5, 50, -266),
        ];
        net.Then(Counted(2)).Then(Listed(2, two));
        Assert.True((await Call("POST", "survey", $$"""{"key":"{{Wredguia}}"}""")).Json.GetProperty("complete").GetBoolean());

        // The focus picks which cubes a busy square lists; the totals are always the whole square's, for the list's heading.
        var (status, names) = await Call("GET", "names?kind=sector&key=s:38:32:18&near=-345.5,15,-305");
        Assert.Equal((200, """{"groups":1,"count":2,"gaps":2}""", 1), (status, Text(names.GetProperty("totals")), names.GetProperty("groups").GetArrayLength()));

        foreach (string wrong in new[] { "near=-345,15", "near=a,b,c", "near=1,2,3,4" })
        {
            var (refused, said) = await Call("GET", "names?kind=sector&key=s:38:32:18&" + wrong);
            Assert.Equal((400, "A point is given as x,y,z in light years."), (refused, Error(said)));
        }
        Assert.Equal(2, net.Asked.Count);
    }

    [Fact]
    public async Task Your_finds_are_listed_taken_off_and_switched_off_and_the_block_marks_the_games_target_and_route_end()
    {
        // A find kept by an earlier session; a route plotted (NavRoute.json of the recordings, ending at Khun) and a system
        // targeted, both before Studio started: history, so nothing is checked.
        long kept = SystemAddress.WithNumber(10410264907, 66), targeted = SystemAddress.WithNumber(10410264907, 67);
        Directory.CreateDirectory(Folder);
        string earlier = $$"""[{"name":"Wredguia WD-K d8-66","id64":"{{kept}}","x":-345,"y":15,"z":-305,"plusMinus":40,"at":"2026-10-14T12:00:00Z","from":"target","how":["not found by the name search at Spansh (14 Oct 2026)"]}]""";
        File.WriteAllText(Path.Combine(Folder, "finds.json"), earlier);
        game.Copy("NavRoute.json", game.JournalDir);
        Start(lines: [$$"""{ "timestamp":"2026-10-14T10:01:00Z", "event":"FSDTarget", "Name":"Wredguia WD-K d8-67", "SystemAddress":{{targeted}}, "StarClass":"M" }"""]);

        // The markers: the route's end where the game says it is; the target, which the game gave no place, from its number.
        JsonElement block = (await Call("GET", "block")).Json;
        Assert.Equal("""{"name":"Khun","id64":"3107241104074","x":-171.59375,"y":19.96875,"z":-56.96875}""", Text(block.GetProperty("routeEnd")));
        Assert.Equal($$"""{"name":"Wredguia WD-K d8-67","id64":"{{targeted}}","x":-345,"y":15,"z":-305,"plusMinus":40}""", Text(block.GetProperty("target")));

        // Your finds, and the switch ("Check my targets", on unless the player turns it off).
        var (status, finds) = await Call("GET", "finds");
        Assert.Equal((200, $$"""{"finds":{{earlier}},"checkTargets":true}"""), (status, Text(finds)));

        // The route plotted before Studio started is history: its stops are not checked by themselves either. The answer
        // says it is there and not checked, and the overlay's line says how to have it checked.
        JsonElement check = (await Call("GET", "route-check")).Json;
        Assert.Equal(("idle", 0, 5, "Khun"), (check.GetProperty("state").GetString(), check.GetProperty("total").GetInt32(),
            check.GetProperty("plotted").GetInt32(), check.GetProperty("plottedTo").GetString()));
        Assert.Equal(("not checked: your route was plotted before Studio started   ·   plot it again, or press Check this route on the 3D map", "plain"),
            (check.GetProperty("line").GetProperty("words").GetString(), check.GetProperty("line").GetProperty("tone").GetString()));

        // Switched off, and kept in settings.json; a body that leaves it out leaves it as it is.
        Assert.Equal("""{"checkTargets":false,"routeCheck":"all","recordedDays":1,"withinReach":true,"reachLy":null,"navScan":false,"navScanVolume":70}""", Text((await Call("POST", "settings", """{"checkTargets":false}""")).Json));
        Assert.False((bool)JsonNode.Parse(File.ReadAllText(Path.Combine(temp, "settings.json")))!["heatmap"]!["checkTargets"]!);
        Assert.Equal("""{"checkTargets":false,"routeCheck":"all","recordedDays":1,"withinReach":true,"reachLy":null,"navScan":false,"navScanVolume":70}""", Text((await Call("POST", "settings", "{}")).Json));
        Assert.False((await Call("GET", "finds")).Json.GetProperty("checkTargets").GetBoolean());
        Assert.Equal("off", (await Call("GET", "route-check")).Json.GetProperty("state").GetString());

        // Taken off by its number, as text as the page sends it; then it is gone.
        var (removedStatus, removed) = await Call("POST", "finds/remove", $$"""{"id64":"{{kept}}","name":"Wredguia WD-K d8-66"}""");
        Assert.Equal((200, true, 0), (removedStatus, removed.GetProperty("removed").GetBoolean(), removed.GetProperty("finds").GetArrayLength()));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(Folder, "finds.json")));
        Assert.False((await Call("POST", "finds/remove", $$"""{"id64":"{{kept}}"}""")).Json.GetProperty("removed").GetBoolean());
        var (refused, error) = await Call("POST", "finds/remove", "{}");
        Assert.Equal((400, "Say which find to take off: its number or its name."), (refused, Error(error)));
        Assert.Empty(net.Asked);
    }

    // ------------------------------------------------------------ the check of every stop of a plotted route

    /// <summary>The stops of NavRoute.json of the recordings after Diaguandri, where it starts, as Spansh lists them; all but one of them.</summary>
    static readonly Star[] RouteStars =
    [
        new("Arietis Sector KJ-F a12-2", 40543954160392, -73.15625, -41.5, -89.40625),
        new("Hyades Sector YB-K b9-1", 2868635510153, -104.3125, -18.78125, -78.875),
        new("Col 285 Sector LS-T d3-92", 3171556526435, -139, 1.625, -76.125),
        new("Khun", 3107241104074, -171.59375, 19.96875, -56.96875),
    ];

    const string RouteAsked =
        """POST /api/systems/search {"filters":{"name":{"value":["Arietis Sector KJ-F a12-2","Hyades Sector YB-K b9-1","Col 285 Sector LS-T d3-92","Col 285 Sector XF-N c7-24","Khun"]}},"size":5,"page":0}""";

    [Fact]
    public async Task The_check_of_every_stop_of_a_plotted_route_is_given_whole_and_its_settings_go_both_ways()
    {
        Start();

        // No route plotted: nothing to check yet. Every stop is what is checked, unless the player chooses otherwise.
        var (status, check) = await Call("GET", "route-check");
        Assert.Equal(
            (200, """{"state":"idle","mode":"all","started":null,"finished":null,"total":0,"checked":0,"recorded":0,"notRecorded":0,"failed":0,"requests":0,"reason":null,"plotted":0,"plottedTo":null,"line":null,"stops":[]}"""),
            (status, Text(check)));
        Assert.Equal("""{"checkTargets":true,"routeCheck":"all","recordedDays":1,"withinReach":true,"reachLy":null,"navScan":false,"navScanVolume":70}""", Text((await Call("GET", "settings")).Json));

        // A route plotted in the game (NavRoute.json of the recordings: five stops after Diaguandri, ending at Khun): one
        // request for the five names. Spansh has all but Col 285 Sector XF-N c7-24.
        net.Then(Listed(4, RouteStars));
        game.Copy("NavRoute.json", game.JournalDir);
        watcher!.Poll();
        await finds!.Idle;
        Assert.Equal(RouteAsked, Assert.Single(net.Asked));

        (status, check) = await Call("GET", "route-check");
        Assert.Equal(200, status);
        Assert.Equal(["state", "mode", "started", "finished", "total", "checked", "recorded", "notRecorded", "failed", "requests", "reason", "plotted", "plottedTo", "line", "stops"],
            check.EnumerateObject().Select(field => field.Name));
        // The overlay's line, which Studio Nav's page prints in the game's nav box as it is given.
        Assert.Equal(("done   ·   5 stops   ·   1 not recorded: Col 285 Sector XF-N c7-24", "gold"),
            (check.GetProperty("line").GetProperty("words").GetString(), check.GetProperty("line").GetProperty("tone").GetString()));
        Assert.Equal(("done", "all", 5, 5, 4, 1, 0, 1, JsonValueKind.Null),
            (check.GetProperty("state").GetString(), check.GetProperty("mode").GetString(), check.GetProperty("total").GetInt32(), check.GetProperty("checked").GetInt32(),
                check.GetProperty("recorded").GetInt32(), check.GetProperty("notRecorded").GetInt32(), check.GetProperty("failed").GetInt32(),
                check.GetProperty("requests").GetInt32(), check.GetProperty("reason").ValueKind));
        Assert.True(check.GetProperty("started").GetDateTime() <= check.GetProperty("finished").GetDateTime());
        JsonElement[] stops = [.. check.GetProperty("stops").EnumerateArray()];
        Assert.Equal(["Arietis Sector KJ-F a12-2", "Hyades Sector YB-K b9-1", "Col 285 Sector LS-T d3-92", "Col 285 Sector XF-N c7-24", "Khun"],
            stops.Select(stop => stop.GetProperty("name").GetString()));   // in route order, its start left out
        // (Text writes an apostrophe as ', which means the same.)
        Assert.Equal(
            """{"name":"Col 285 Sector XF-N c7-24","id64":"6680653861578","x":-165.625,"y":13.53125,"z":-55.6875,"state":"not-recorded","how":"not found by Spansh\u0027s system search","reason":null}""",
            Text(stops[3]));
        Assert.Equal(
            """{"name":"Khun","id64":"3107241104074","x":-171.59375,"y":19.96875,"z":-56.96875,"state":"recorded","how":"found by Spansh\u0027s system search","reason":null}""",
            Text(stops[4]));

        // The one not recorded is one of your finds, from the route; the block's marker of the route's end says Khun is recorded.
        JsonElement find = Assert.Single((await Call("GET", "finds")).Json.GetProperty("finds").EnumerateArray());
        Assert.Equal(("Col 285 Sector XF-N c7-24", "6680653861578", "route"), (find.GetProperty("name").GetString(), find.GetProperty("id64").GetString(), find.GetProperty("from").GetString()));
        Assert.True((await Call("GET", "block")).Json.GetProperty("routeEnd").GetProperty("recorded").GetBoolean());

        // Only the target and the route's end: kept in settings.json, and the check goes.
        Assert.Equal("""{"checkTargets":true,"routeCheck":"ends","recordedDays":1,"withinReach":true,"reachLy":null,"navScan":false,"navScanVolume":70}""", Text((await Call("POST", "settings", """{"routeCheck":"ends","recordedDays":1,"withinReach":true,"reachLy":null}""")).Json));
        Assert.Equal("ends", (string)JsonNode.Parse(File.ReadAllText(Path.Combine(temp, "settings.json")))!["heatmap"]!["routeCheck"]!);
        check = (await Call("GET", "route-check")).Json;
        Assert.Equal(("off", "ends", 0), (check.GetProperty("state").GetString(), check.GetProperty("mode").GetString(), check.GetProperty("stops").GetArrayLength()));

        // A choice Studio does not know changes nothing at all, the switch sent with it neither.
        var (refused, error) = await Call("POST", "settings", """{"routeCheck":"sideways","checkTargets":false}""");
        Assert.Equal((400, "Studio checks the target and the end of a plotted route (\"ends\"), or every stop of the route (\"all\")."), (refused, Error(error)));
        Assert.Equal("""{"checkTargets":true,"routeCheck":"ends","recordedDays":1,"withinReach":true,"reachLy":null,"navScan":false,"navScanVolume":70}""", Text((await Call("GET", "settings")).Json));

        // Every stop again: the route plotted now is checked, from what this session already knows, asking nobody.
        Assert.Equal("""{"checkTargets":true,"routeCheck":"all","recordedDays":1,"withinReach":true,"reachLy":null,"navScan":false,"navScanVolume":70}""", Text((await Call("POST", "settings", """{"routeCheck":"ALL"}""")).Json));
        await finds.Idle;
        check = (await Call("GET", "route-check")).Json;
        Assert.Equal(("done", 5, 0), (check.GetProperty("state").GetString(), check.GetProperty("checked").GetInt32(), check.GetProperty("requests").GetInt32()));
        Assert.Single(net.Asked);
    }

    [Fact]
    public async Task The_check_of_a_route_reaches_a_listening_page_as_heatmap_events_of_its_own_kind()
    {
        // The check goes on by itself after the route is plotted, so the events are how a page follows it. Hence the real
        // server on a port of this PC, listened to the way a page listens.
        CancellationToken cancel = TestContext.Current.CancellationToken;
        Start();
        Assert.True(web!.Start(47890));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var events = new StreamReader(await http.GetStreamAsync($"http://127.0.0.1:{web.Port}/api/events?t={web.Token}", cancel));

        // Spansh has every stop.
        net.Then(Listed(5, [.. RouteStars, new Star("Col 285 Sector XF-N c7-24", 6680653861578, -165.625, 13.53125, -55.6875)]));
        game.Copy("NavRoute.json", game.JournalDir);
        watcher!.Poll();

        var heard = new List<(string Event, JsonElement Data)>();
        string name = "";
        while (heard.Count == 0 || !(Kind(heard[^1].Data) == "route-check" && heard[^1].Data.GetProperty("state").GetString() == "done"))
        {
            string? line = await events.ReadLineAsync(cancel).AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancel);
            Assert.NotNull(line);   // null: the stream ended
            if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal)) heard.Add((name, JsonSerializer.Deserialize<JsonElement>(line[6..])));
        }
        await finds!.Idle;

        // The route's end moved (kind "target"), then the check: begun, under way, done, each a "heatmap" event of kind
        // "route-check" with what GET route-check says but the stops, which the page asks for.
        static string? Kind(JsonElement data) => data.TryGetProperty("kind", out JsonElement kind) ? kind.GetString() : null;
        Assert.All(heard, said => Assert.Equal("heatmap", said.Event));
        Assert.Equal("target", Kind(heard[0].Data));
        (string Event, JsonElement Data)[] checks = [.. heard.Where(said => Kind(said.Data) == "route-check")];
        Assert.Equal(["kind", "state", "mode", "started", "finished", "total", "checked", "recorded", "notRecorded", "failed", "requests", "reason", "plotted", "plottedTo", "line"],
            checks[^1].Data.EnumerateObject().Select(field => field.Name));
        // Each carries the overlay's line, made from the stops it leaves out: the overlay's page needs nothing more.
        Assert.Equal(("checking 0 of 5…", "dim"), (checks[0].Data.GetProperty("line").GetProperty("words").GetString(), checks[0].Data.GetProperty("line").GetProperty("tone").GetString()));
        Assert.Equal(("done   ·   5 stops   ·   all recorded", "green"),
            (checks[^1].Data.GetProperty("line").GetProperty("words").GetString(), checks[^1].Data.GetProperty("line").GetProperty("tone").GetString()));
        Assert.Equal(("checking", 5, 0), (checks[0].Data.GetProperty("state").GetString(), checks[0].Data.GetProperty("total").GetInt32(), checks[0].Data.GetProperty("checked").GetInt32()));
        Assert.True(checks.Length >= 3, $"only {checks.Length} route-check events");
        Assert.Equal((5, 5, 1), (checks[^1].Data.GetProperty("checked").GetInt32(), checks[^1].Data.GetProperty("recorded").GetInt32(), checks[^1].Data.GetProperty("requests").GetInt32()));
        Assert.Equal(RouteAsked, Assert.Single(net.Asked));
    }

    // ------------------------------------------------------------ Within reach, and the memory of checks

    /// <summary>The journal Studio reads at its start (<see cref="Start"/>), which the game goes on writing.</summary>
    string JournalFile => Path.Combine(game.JournalDir, "Journal.2026-10-14T100000.01.log");

    /// <summary>The ship, as the game describes it: no weight given, so it jumps 0.8 of its best with a full hold (30 ly for 37.5).</summary>
    const string Loadout = """{ "timestamp":"2026-10-14T10:00:05Z", "event":"Loadout", "Ship":"krait_mkii", "ShipID":1, "MaxJumpRange":37.5, "CargoCapacity":0, "Modules":[] }""";

    /// <summary>Four systems of the game's cube Wredguia WD-K d8, numbers 0, 1, 3 and 7 (as in FineMapTests): 2, 4, 5 and 6 must exist.</summary>
    static readonly Star D0 = new("Wredguia WD-K d8-0", 10410264907, -350.5, 10.25, -300.125),
        D1 = new("Wredguia WD-K d8-1", 44770003275, -340, 20, -290),
        D3 = new("Wredguia WD-K d8-3", 113489480011, -306.5, 50, -266),
        D7 = new("Wredguia WD-K d8-7", 250928433483, -384.96875, -24.96875, -344.96875);

    /// <summary>Spansh's system search round a point, word for word as Studio sends it.</summary>
    static string AskedAround(double x, double y, double z, string ly) => string.Create(CultureInfo.InvariantCulture,
        $$$"""POST /api/systems/search {"filters":{"distance":{"min":"0","max":"{{{ly}}}"}},"sort":[{"distance":{"direction":"asc"}}],"size":500,"page":0,"reference_coords":{"x":{{{x}}},"y":{{{y}}},"z":{{{z}}}}}""");

    [Fact]
    public async Task Within_reach_is_given_whole_and_its_switch_range_and_days_go_both_ways_with_plain_refusals()
    {
        Start(lines: [Loadout]);
        await reach!.Pending;

        // Studio starts outside the detailed map: nothing is asked by itself, and the answer says when Spansh will be.
        var (status, view) = await Call("GET", "reach");
        Assert.Equal(200, status);
        Assert.Equal(["state", "on", "rangeLy", "from", "at", "centre", "source", "capped", "problem", "moreSystems", "moreMustExist", "inLargeCubes", "systems", "mustExist"],
            view.EnumerateObject().Select(field => field.Name));
        Assert.Equal(("done", true, 30.0, "ship", JsonValueKind.Null, false, 0, 0),
            (view.GetProperty("state").GetString(), view.GetProperty("on").GetBoolean(), view.GetProperty("rangeLy").GetDouble(), view.GetProperty("from").GetString(),
                view.GetProperty("source").ValueKind, view.GetProperty("capped").GetBoolean(), view.GetProperty("systems").GetArrayLength(), view.GetProperty("mustExist").GetArrayLength()));
        Assert.Equal("""{"x":-167.09,"y":26.72,"z":-55.38,"system":"Practice Home"}""", Text(view.GetProperty("centre")));
        Assert.StartsWith("You are outside the detailed map, so Studio asks Spansh for the systems within reach, after your next jump", view.GetProperty("problem").GetString(), StringComparison.Ordinal);
        Assert.Empty(net.Asked);

        // A jump into the cube of Wredguia WD-K d8, 5 ly from d8-1: one request round the commander, given whole.
        net.Then(Listed(2, [D1, D0]));
        game.Append(JournalFile, """{ "timestamp":"2026-10-14T10:05:00Z", "event":"FSDJump", "StarSystem":"Wredguia Practice", "SystemAddress":1002, "StarPos":[-340,20,-295], "JumpDist":300 }""");
        watcher!.Poll();
        await reach.Pending;
        Assert.Equal([AskedAround(-340, 20, -295, "30")], net.Asked);
        view = (await Call("GET", "reach")).Json;
        Assert.Equal(("done", "spansh", false, JsonValueKind.Null, 0, 0L),
            (view.GetProperty("state").GetString(), view.GetProperty("source").GetString(), view.GetProperty("capped").GetBoolean(), view.GetProperty("problem").ValueKind,
                view.GetProperty("moreSystems").GetInt32(), view.GetProperty("moreMustExist").GetInt64()));
        Assert.InRange(view.GetProperty("at").GetDateTime(), DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("""{"x":-340,"y":20,"z":-295,"system":"Wredguia Practice"}""", Text(view.GetProperty("centre")));
        Assert.Equal(
            """[{"name":"Wredguia WD-K d8-1","id64":"44770003275","x":-340,"y":20,"z":-290,"distance":5,"state":"recorded"},{"name":"Wredguia WD-K d8-0","id64":"10410264907","x":-350.5,"y":10.25,"z":-300.125,"distance":15.22,"state":"recorded"}]""",
            Text(view.GetProperty("systems")));
        Assert.Equal(0, view.GetProperty("mustExist").GetArrayLength());   // the cube reaches out of the sphere, where Spansh was not asked

        // A range typed on the page, as text as a field sends it: kept, and listed again at once. The cube lies wholly
        // inside the sphere and Spansh's answer is whole, so the numbers it lacks there must exist: probably in reach.
        net.Then(Listed(4, [D1, D0, D3, D7]));
        (status, JsonElement settings) = await Call("POST", "settings", """{"reachLy":" 100 "}""");
        Assert.Equal((200, """{"checkTargets":true,"routeCheck":"all","recordedDays":1,"withinReach":true,"reachLy":100,"navScan":false,"navScanVolume":70}"""), (status, Text(settings)));
        await reach.Pending;
        Assert.Equal(AskedAround(-340, 20, -295, "100"), net.Asked[^1]);
        view = (await Call("GET", "reach")).Json;
        Assert.Equal(("you", 100.0, 4), (view.GetProperty("from").GetString(), view.GetProperty("rangeLy").GetDouble(), view.GetProperty("systems").GetArrayLength()));
        Assert.Equal(
            """{"name":"Wredguia WD-K d8-2","id64":"79129741643","x":-345,"y":15,"z":-305,"plusMinus":40,"distance":12.25,"certainty":"probably","state":null}""",
            Text(view.GetProperty("mustExist")[0]));
        Assert.Equal(["Wredguia WD-K d8-2", "Wredguia WD-K d8-4", "Wredguia WD-K d8-5", "Wredguia WD-K d8-6"],
            view.GetProperty("mustExist").EnumerateArray().Select(gap => gap.GetProperty("name").GetString()));

        // Anything wrong changes nothing at all, the switch sent with it neither; each is refused in plain words.
        foreach ((string body, string why) in new[]
        {
            ("""{"reachLy":0.5,"withinReach":false}""", HeatmapReach.NoSuchRange),
            ("""{"reachLy":"far","withinReach":false}""", HeatmapReach.NoSuchRange),
            ("""{"reachLy":true}""", HeatmapReach.NoSuchRange),
            ("""{"recordedDays":3,"withinReach":false}""", CheckMemory.NoSuchDays),
            ("""{"recordedDays":"7.5"}""", CheckMemory.NoSuchDays),
            ("""{"recordedDays":null}""", CheckMemory.NoSuchDays),
            ("""{"withinReach":"yes"}""", "Studio could not read those settings: a switch is true or false, a choice is a word, and days and light years are numbers."),
        })
        {
            var (refused, error) = await Call("POST", "settings", body);
            Assert.Equal((400, why), (refused, Error(error)));
        }
        Assert.Equal("""{"checkTargets":true,"routeCheck":"all","recordedDays":1,"withinReach":true,"reachLy":100,"navScan":false,"navScanVolume":70}""", Text((await Call("GET", "settings")).Json));
        Assert.Equal(2, net.Asked.Count);

        // Switched off, with a week for a recorded system and the ship's own range again (an empty field): kept in
        // settings.json, the lists go, and nothing is asked.
        (status, settings) = await Call("POST", "settings", """{"withinReach":false,"recordedDays":"7","reachLy":""}""");
        Assert.Equal((200, """{"checkTargets":true,"routeCheck":"all","recordedDays":7,"withinReach":false,"reachLy":null,"navScan":false,"navScanVolume":70}"""), (status, Text(settings)));
        await reach.Pending;
        view = (await Call("GET", "reach")).Json;
        Assert.Equal(("off", false, 30.0, "ship", JsonValueKind.Null, 0),
            (view.GetProperty("state").GetString(), view.GetProperty("on").GetBoolean(), view.GetProperty("rangeLy").GetDouble(), view.GetProperty("from").GetString(),
                view.GetProperty("centre").ValueKind, view.GetProperty("systems").GetArrayLength()));
        JsonNode saved = JsonNode.Parse(File.ReadAllText(Path.Combine(temp, "settings.json")))!["heatmap"]!;
        Assert.Equal((false, 7, (JsonNode?)null), ((bool)saved["withinReach"]!, (int)saved["recordedDays"]!, saved["reachLy"]));
        Assert.Equal(7, finds!.Memory.RecordedDays);
        Assert.Equal(2, net.Asked.Count);
    }

    [Fact]
    public async Task Within_reach_reaches_a_listening_page_as_heatmap_events_of_its_own_kind()
    {
        // Within reach lists by itself after a jump, so the events are how a page follows it. Hence the real server on a
        // port of this PC, listened to the way a page listens.
        CancellationToken cancel = TestContext.Current.CancellationToken;
        Start(lines: [Loadout]);
        await reach!.Pending;
        Assert.True(web!.Start(47890));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var events = new StreamReader(await http.GetStreamAsync($"http://127.0.0.1:{web.Port}/api/events?t={web.Token}", cancel));

        // A range typed on the page: listed at once, with one request. Two systems of the game's 10 ly cube round the
        // commander, numbered 0 and 2: number 1 must exist, and the whole cube is within 40 ly.
        net.Then(Listed(2,
        [
            new Star("Practice Near 0", Address(Player.X, Player.Y, Player.Z, 0, 0), Player.X, Player.Y, Player.Z + 1),
            new Star("Practice Near 2", Address(Player.X, Player.Y, Player.Z, 0, 2), Player.X, Player.Y, Player.Z + 2),
        ]));
        Assert.Equal(200, (await Call("POST", "settings", """{"reachLy":40}""")).Status);

        var heard = new List<JsonElement>();
        string name = "";
        // Reads the stream until a "reach" event that is done arrives after those heard so far.
        async Task HearDone()
        {
            int before = heard.Count;
            while (heard.Count == before || heard[^1].GetProperty("state").GetString() != "done")
            {
                string? line = await events.ReadLineAsync(cancel).AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancel);
                Assert.NotNull(line);   // null: the stream ended
                if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal) && name == "heatmap")
                {
                    JsonElement data = JsonSerializer.Deserialize<JsonElement>(line[6..]);
                    if (data.TryGetProperty("kind", out JsonElement kind) && kind.GetString() == "reach") heard.Add(data);
                }
            }
        }
        await HearDone();
        await reach.Pending;

        // Begun, then done: each with what GET reach says but the lists, which the page asks for, and how long they are.
        Assert.Equal(["kind", "state", "on", "rangeLy", "from", "at", "centre", "source", "capped", "systems", "mustExist", "problem", "inLargeCubes"],
            heard[^1].EnumerateObject().Select(field => field.Name));
        Assert.Equal(("listing", 40.0, "you", JsonValueKind.Null),
            (heard[0].GetProperty("state").GetString(), heard[0].GetProperty("rangeLy").GetDouble(), heard[0].GetProperty("from").GetString(), heard[0].GetProperty("source").ValueKind));
        Assert.Equal(("done", true, "spansh", false, 2, 1L, JsonValueKind.Null),
            (heard[^1].GetProperty("state").GetString(), heard[^1].GetProperty("on").GetBoolean(), heard[^1].GetProperty("source").GetString(), heard[^1].GetProperty("capped").GetBoolean(),
                heard[^1].GetProperty("systems").GetInt32(), heard[^1].GetProperty("mustExist").GetInt64(), heard[^1].GetProperty("problem").ValueKind));
        Assert.Equal("""{"x":-167.09,"y":26.72,"z":-55.38,"system":"Practice Home"}""", Text(heard[^1].GetProperty("centre")));
        Assert.Equal([AskedAround(Player.X, Player.Y, Player.Z, "40")], net.Asked);
        JsonElement gap = Assert.Single((await Call("GET", "reach")).Json.GetProperty("mustExist").EnumerateArray());
        long gapId64 = Address(Player.X, Player.Y, Player.Z, 0, 1);
        Assert.Equal((gapId64.ToString(CultureInfo.InvariantCulture), JsonValueKind.Null), (gap.GetProperty("id64").GetString(), gap.GetProperty("state").ValueKind));

        // The game then targets the system that must exist, and its check finds it is not recorded: the page is told
        // again, and the list says so.
        net.Then("""{"min_max":[],"values":[]}""");
        Assert.Equal(HeatmapFinds.NotRecorded, (await finds!.Check(gap.GetProperty("name").GetString(), gapId64))!.State);
        await HearDone();
        gap = Assert.Single((await Call("GET", "reach")).Json.GetProperty("mustExist").EnumerateArray());
        Assert.Equal("not-recorded", gap.GetProperty("state").GetString());
        Assert.Equal(2, net.Asked.Count);
    }

    [Fact]
    public async Task Forget_all_checks_empties_the_memory_so_every_system_is_checked_afresh()
    {
        Start();
        long id64 = SystemAddress.WithNumber(10410264907, 66);
        string known = JsonSerializer.Serialize(new
        {
            min_max = new[] { new { id64, name = "Wredguia WD-K d8-66", x = -340.5, y = 20.25, z = -290.125 } },
            values = new[] { "Wredguia WD-K d8-66" },
        });

        // A target Spansh's name search finds: remembered (heatmap\checks.json), so not asked about again.
        net.Then(known);
        Assert.Equal(HeatmapFinds.Recorded, (await finds!.Check("Wredguia WD-K d8-66", id64))!.State);
        Assert.Null(await finds.Check("Wredguia WD-K d8-66", id64));
        Assert.Contains($"\"id64\":\"{id64}\"", File.ReadAllText(Path.Combine(Folder, CheckMemory.FileName)), StringComparison.Ordinal);
        Assert.Single(net.Asked);

        // Forgotten: the memory and its file are emptied.
        var (status, forgot) = await Call("POST", "checks/forget");
        Assert.Equal((200, """{"forgotten":1}"""), (status, Text(forgot)));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(Folder, CheckMemory.FileName)));

        // Checked afresh: what this session learnt of it from Spansh went with the memory, so Spansh is asked again.
        net.Then(known);
        Assert.Equal(HeatmapFinds.Recorded, (await finds.Check("Wredguia WD-K d8-66", id64))!.State);
        Assert.Equal(2, net.Asked.Count);
        Assert.Equal("""{"forgotten":1}""", Text((await Call("POST", "checks/forget")).Json));
    }

    // ------------------------------------------------------------ Nav scan

    /// <summary>A screenshot made now in the test's pictures folder, which the stand-in reader reads as one line, a generated name.</summary>
    string Screenshot(string file, params string[] words)
    {
        texts[file] = new PictureText([new ReadLine([.. words.Select((word, at) => new ReadWord(word, 100 + 120 * at, 100, 110, 20))])]);
        string path = Path.Combine(Pictures, file);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public async Task Nav_scan_is_given_whole_and_its_switch_volume_and_sounds_go_both_ways_with_plain_refusals()
    {
        Start();

        // Off unless the player turns it on.
        Assert.Equal("""{"checkTargets":true,"routeCheck":"all","recordedDays":1,"withinReach":true,"reachLy":null,"navScan":false,"navScanVolume":70}""",
            Text((await Call("GET", "settings")).Json));
        var (status, view) = await Call("GET", "navscan");
        Assert.Equal(200, status);
        Assert.Equal(["state", "on", "volume", "watching", "problem", "line", "pictures", "names"], view.EnumerateObject().Select(field => field.Name));
        Assert.Equal("""{"state":"off","on":false,"volume":70,"watching":[],"problem":null,"line":null,"pictures":[],"names":[]}""", Text(view));

        // Switched on, the volume as a slider may send it (text): kept in settings.json, and the pictures' folder watched.
        (status, JsonElement settings) = await Call("POST", "settings", """{"navScan":true,"navScanVolume":"35"}""");
        Assert.Equal((200, """{"checkTargets":true,"routeCheck":"all","recordedDays":1,"withinReach":true,"reachLy":null,"navScan":true,"navScanVolume":35}"""), (status, Text(settings)));
        JsonNode saved = JsonNode.Parse(File.ReadAllText(Path.Combine(temp, "settings.json")))!["heatmap"]!;
        Assert.Equal((true, 35), ((bool)saved["navScan"]!, (int)saved["navScanVolume"]!));
        view = (await Call("GET", "navscan")).Json;
        Assert.Equal(("watching", true, 35, Pictures, JsonValueKind.Null), (view.GetProperty("state").GetString(), view.GetProperty("on").GetBoolean(), view.GetProperty("volume").GetInt32(),
            view.GetProperty("watching")[0].GetString(), view.GetProperty("problem").ValueKind));
        Assert.Equal(("waiting for a screenshot of the Navigation panel (F12 in Steam, F10 in the game)", "dim"),
            (view.GetProperty("line").GetProperty("words").GetString(), view.GetProperty("line").GetProperty("tone").GetString()));

        // A screenshot: read, its name checked with one name search at Spansh, and kept as a possible find.
        net.Then(Listed(0, []));
        scan!.Offer(Screenshot("20261014101500_1.jpg", "WREDGUIA", "WD-K", "D8-66"));
        await scan.Idle;
        Assert.Contains("\"filters\":{\"name\":{\"value\":[\"Wredguia WD-K d8-66\"]}}", Assert.Single(net.Asked), StringComparison.Ordinal);
        view = (await Call("GET", "navscan")).Json;
        JsonElement picture = Assert.Single(view.GetProperty("pictures").EnumerateArray());
        Assert.Equal(["file", "at", "names", "recorded", "notRecorded", "failed", "unsure", "dropped", "state"], picture.EnumerateObject().Select(field => field.Name));
        Assert.Equal(("20261014101500_1.jpg", 1, 0, 1, "done"), (picture.GetProperty("file").GetString(), picture.GetProperty("names").GetInt32(), picture.GetProperty("recorded").GetInt32(),
            picture.GetProperty("notRecorded").GetInt32(), picture.GetProperty("state").GetString()));
        JsonElement listed = Assert.Single(view.GetProperty("names").EnumerateArray());
        Assert.Equal(["name", "id64", "state", "unsure", "x", "y", "z", "plusMinus", "from", "how"], listed.EnumerateObject().Select(field => field.Name));
        Assert.Equal(("Wredguia WD-K d8-66", "2278152997195", "not-recorded", false, -345.0, 15.0, -305.0, 40.0, "20261014101500_1.jpg", "not found by Spansh's system search"),
            (listed.GetProperty("name").GetString(), listed.GetProperty("id64").GetString(), listed.GetProperty("state").GetString(), listed.GetProperty("unsure").GetBoolean(),
                listed.GetProperty("x").GetDouble(), listed.GetProperty("y").GetDouble(), listed.GetProperty("z").GetDouble(), listed.GetProperty("plusMinus").GetDouble(),
                listed.GetProperty("from").GetString(), listed.GetProperty("how").GetString()));
        Assert.Equal(("picture read   ·   1 name   ·   1 not recorded: Wredguia WD-K d8-66", "gold"),
            (view.GetProperty("line").GetProperty("words").GetString(), view.GetProperty("line").GetProperty("tone").GetString()));
        JsonElement find = Assert.Single((await Call("GET", "finds")).Json.GetProperty("finds").EnumerateArray());
        Assert.Equal(("nav scan", false, "read from a picture; target it in the game to confirm"),
            (find.GetProperty("from").GetString(), find.GetProperty("confirmed").GetBoolean(), find.GetProperty("note").GetString()));
        Assert.Equal([(NavScanSounds.Click, 35), (NavScanSounds.Ping, 35)], played);

        // "Test sound" plays one at the volume set (never aloud here); anything else is refused in plain words.
        var (sounded, ping) = await Call("POST", "navscan/test-sound", """{"sound":"ping"}""");
        Assert.Equal((200, """{"sound":"ping","played":true,"volume":35}"""), (sounded, Text(ping)));
        Assert.Equal("""{"sound":"ping","played":true,"volume":35}""", Text((await Call("POST", "navscan/test-sound", """{"sound":" PING "}""")).Json));
        Assert.Equal([(NavScanSounds.Ping, 35), (NavScanSounds.Ping, 35)], played.Skip(2));
        foreach (string body in new[] { """{"sound":"boom"}""", """{"sound":3}""", "{}" })
        {
            var (refused, error) = await Call("POST", "navscan/test-sound", body);
            Assert.Equal((400, NavScan.NoSuchSound), (refused, Error(error)));
        }

        // Anything wrong in the settings changes nothing at all.
        foreach ((string body, string why) in new[]
        {
            ("""{"navScanVolume":101,"navScan":false}""", NavScan.NoSuchVolume),
            ("""{"navScanVolume":"loud"}""", NavScan.NoSuchVolume),
            ("""{"navScanVolume":-1}""", NavScan.NoSuchVolume),
            ("""{"navScanVolume":null}""", NavScan.NoSuchVolume),
            ("""{"navScan":"yes"}""", "Studio could not read those settings: a switch is true or false, a choice is a word, and days and light years are numbers."),
        })
        {
            var (refused, error) = await Call("POST", "settings", body);
            Assert.Equal((400, why), (refused, Error(error)));
        }
        Assert.Equal("""{"checkTargets":true,"routeCheck":"all","recordedDays":1,"withinReach":true,"reachLy":null,"navScan":true,"navScanVolume":35}""",
            Text((await Call("GET", "settings")).Json));

        // At volume 0 nothing sounds, and the answer says so.
        Assert.Equal(200, (await Call("POST", "settings", """{"navScanVolume":0}""")).Status);
        Assert.Equal("""{"sound":"click","played":false,"volume":0}""", Text((await Call("POST", "navscan/test-sound", """{"sound":"click"}""")).Json));
        Assert.Equal(4, played.Count);

        // Switched off: nothing watched, no line in the game; the pictures of the session stay listed.
        Assert.Equal(200, (await Call("POST", "settings", """{"navScan":false}""")).Status);
        view = (await Call("GET", "navscan")).Json;
        Assert.Equal(("off", 0, JsonValueKind.Null, 1), (view.GetProperty("state").GetString(), view.GetProperty("watching").GetArrayLength(), view.GetProperty("line").ValueKind,
            view.GetProperty("pictures").GetArrayLength()));
    }

    [Fact]
    public async Task Nav_scan_reaches_a_listening_page_as_heatmap_events_of_its_own_kind()
    {
        // Nav scan reads pictures by itself, so the events are how a page and the overlay's nav page follow it. Hence the
        // real server on a port of this PC, listened to the way a page listens.
        CancellationToken cancel = TestContext.Current.CancellationToken;
        Start();
        Assert.True(web!.Start(47890));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var events = new StreamReader(await http.GetStreamAsync($"http://127.0.0.1:{web.Port}/api/events?t={web.Token}", cancel));
        Assert.Equal(200, (await Call("POST", "settings", """{"navScan":true}""")).Status);
        net.Then(Listed(0, []));
        scan!.Offer(Screenshot("20261014101500_1.jpg", "WREDGUIA", "WD-K", "D8-66"));

        var heard = new List<JsonElement>();
        JsonElement? found = null;
        string name = "";
        while (heard.Count == 0 || heard[^1].GetProperty("picture") is not { ValueKind: JsonValueKind.Object } latest || latest.GetProperty("state").GetString() != "done" || found is null)
        {
            string? line = await events.ReadLineAsync(cancel).AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancel);
            Assert.NotNull(line);   // null: the stream ended
            if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                JsonElement data = JsonSerializer.Deserialize<JsonElement>(line[6..]);
                if (name == "heatmap" && data.TryGetProperty("kind", out JsonElement kind) && kind.GetString() == "navscan") heard.Add(data);
                if (name == "heatmap-find") found = data;
            }
        }
        await scan.Idle;

        // Switched on, a picture being read, checked, then done: each with what GET navscan gives but the lists, with the
        // newest picture, the overlay's line, and at the end the name just found not recorded.
        Assert.Equal(["kind", "state", "on", "volume", "watching", "picture", "problem", "line", "found"], heard[^1].EnumerateObject().Select(field => field.Name));
        Assert.Equal(("watching", true, JsonValueKind.Null), (heard[0].GetProperty("state").GetString(), heard[0].GetProperty("on").GetBoolean(), heard[0].GetProperty("picture").ValueKind));
        Assert.Contains(heard, told => told.GetProperty("state").GetString() == "reading");
        Assert.Equal(("watching", "Wredguia WD-K d8-66", "picture read   ·   1 name   ·   1 not recorded: Wredguia WD-K d8-66"),
            (heard[^1].GetProperty("state").GetString(), heard[^1].GetProperty("found")[0].GetString(), heard[^1].GetProperty("line").GetProperty("words").GetString()));
        // The find, as a new find's event says it: read from a picture, not yet confirmed by the game.
        Assert.Equal(("Wredguia WD-K d8-66", "nav scan", false), (found!.Value.GetProperty("name").GetString(), found.Value.GetProperty("from").GetString(), found.Value.GetProperty("confirmed").GetBoolean()));
    }

    // ------------------------------------------------------------ the commander's own firsts

    [Fact]
    public async Task The_commanders_first_discoveries_come_from_all_their_journals()
    {
        // The made-up trip is in an older journal, which Studio does not replay.
        game.Journal("Journal.2026-10-09T100000.01.log", File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nav", "first-discoveries.log")));
        Start();

        var (status, firsts) = await Call("GET", "firsts");

        Assert.Equal(200, status);
        Assert.Equal(
            """{"systems":[{"name":"Practice Sector LX-T d3-7","id64":"3003","x":60,"y":10,"z":80,"at":"2026-10-09T10:08:05Z"},{"name":"Practice Sector MX-T c3-4","id64":"4004","x":90,"y":15,"z":120,"at":"2026-10-09T10:20:40Z"}]}""",
            Text(firsts));
        Assert.Empty(net.Asked);
    }
}
