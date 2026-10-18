using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Nav;

namespace EliteStudio.Tests;

/// <summary>
/// "Your finds" (<see cref="HeatmapFinds"/>): a system the game targets or plots a route to is checked against what
/// is kept on this PC, then with at most one name lookup at Spansh, and kept when no database has it. Nothing goes
/// near the internet: <see cref="SpanshStandIn"/> answers Spansh's name search in its own layout (the answer for a name
/// Spansh knows is the shape of tests\Fixtures\nav\system-names.json; for one it does not know, its empty answer), and
/// with its script used up behaves like a PC that is offline. The game's lines are written as the game writes them
/// (see the FSDTarget, NavRoute and NavRouteClear lines of the recordings in tests\Fixtures\journals).
/// </summary>
public sealed class HeatmapFindsTests : IDisposable
{
    const string Unreachable = "Spansh could not be reached. Check your internet connection and try again.";
    const string NoSuchName = """{"min_max":[],"values":[]}""";

    /// <summary>A guessed name in the cube of Wredguia WD-K d8 (see FineMapTests), which no list here has.</summary>
    const string Guessed = "Wredguia WD-K d8-66";
    static readonly long GuessedId64 = SystemAddress.WithNumber(10410264907, 66);

    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioHeatmapFinds-").FullName;
    readonly SpanshStandIn net = new();
    readonly GameState game = new();
    readonly List<Spansh> clients = [];
    HeatmapSurvey? survey;
    FineMap? fine;
    DateTime clock = DateTime.UtcNow;   // the memory of checks' clock, which a test moves on

    public void Dispose()
    {
        foreach (Spansh client in clients) client.Dispose();
        Directory.Delete(temp, recursive: true);
    }

    string SettingsFile => Path.Combine(temp, "settings.json");

    /// <summary>A fresh start of Studio: a new session, with what earlier ones kept on this PC.</summary>
    /// <param name="routeCheck">What of a plotted route the player chose to check, saved before the start; as saved before (or the default) when left out.</param>
    /// <param name="handler">What stands between Spansh and the client (it passes on to <see cref="net"/>); the stand-in itself when left out.</param>
    HeatmapFinds Start(string? routeCheck = null, HttpMessageHandler? handler = null)
    {
        if (routeCheck is not null) new Settings(SettingsFile).Set("heatmap", new HeatmapSettings { RouteCheck = routeCheck });
        var spansh = new Spansh(handler ?? net);
        clients.Add(spansh);
        survey = new HeatmapSurvey(spansh, temp);
        fine = new FineMap(temp);
        // The memory of checks kept in the 3D map's folder, as the app's is (heatmap\checks.json), on the test's clock.
        return new HeatmapFinds(game, survey, fine, new Settings(SettingsFile), new CheckMemory(temp) { Now = () => clock });
    }

    static string Found(string name, long id64, double x, double y, double z) => JsonSerializer.Serialize(new
    {
        min_max = new[] { new { id64, name, x, y, z } },
        values = new[] { name },
    });

    static string Asked(string name) => "GET /api/systems/field_values/system_names?q=" + Uri.EscapeDataString(name);

    /// <summary>A line of the journal as the game writes it, taken in as it comes (not at start-up).</summary>
    void Line(string json, bool replay = false)
    {
        using JsonDocument line = JsonDocument.Parse(json);
        game.Apply(line.RootElement.Clone(), replay);
    }

    // ------------------------------------------------------------ the check

    [Fact]
    public async Task A_target_no_list_has_is_looked_up_once_at_Spansh_and_kept_as_a_find()
    {
        HeatmapFinds finds = Start();
        var found = new List<HeatmapFind>();
        finds.Found += found.Add;
        net.Then(NoSuchName);

        TargetNews? news = await finds.Check(Guessed, GuessedId64, null, HeatmapFinds.FromTarget);

        Assert.Equal(new TargetNews(HeatmapFinds.FromTarget, Guessed, GuessedId64, HeatmapFinds.NotRecorded, null), news);
        Assert.Equal([Asked(Guessed)], net.Asked);
        Assert.Equal(1, survey!.Used);   // one of the 3D map's own requests
        HeatmapFind find = Assert.Single(finds.Finds);
        Assert.Equal(find, Assert.Single(found));
        // Placed from its number: the middle of its cube, to within half the cube.
        Assert.Equal<(string, long, double?, double?, double?, double?, string)>((Guessed, GuessedId64, -345, 15, -305, 40, "target"),
            (find.Name, find.Id64, find.X, find.Y, find.Z, find.PlusMinus, find.From));
        Assert.Equal(["not found by Spansh's name search (" + DateTime.UtcNow.ToString("d MMM yyyy", CultureInfo.InvariantCulture) + ")"], find.How);
        Assert.InRange(find.At, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(1));

        // Kept for good: the next session has it, with its number as text in the file.
        Assert.Contains($"\"id64\": \"{GuessedId64}\"", File.ReadAllText(Path.Combine(temp, "finds.json")), StringComparison.Ordinal);
        Assert.Equal(JsonSerializer.Serialize(find), JsonSerializer.Serialize(Assert.Single(Start().Finds)));

        // Its marker: where its number puts it, with how far off that may be, and what the check found.
        MapMark? mark = await finds.Place(new GameNamed(Guessed, GuessedId64, null));
        Assert.Equal(new MapMark(Guessed, GuessedId64, -345, 15, -305) { PlusMinus = 40, Recorded = false }, mark);
    }

    [Fact]
    public async Task A_target_Spansh_knows_is_recorded_and_not_kept()
    {
        HeatmapFinds finds = Start();
        net.Then(Found(Guessed, GuessedId64, -340.5, 20.25, -290.125));

        TargetNews? news = await finds.Check(Guessed, GuessedId64, null, HeatmapFinds.FromRouteEnd);

        Assert.Equal((HeatmapFinds.Recorded, HeatmapFinds.FromRouteEnd), (news!.State, news.From));
        Assert.Empty(finds.Finds);
        Assert.False(File.Exists(Path.Combine(temp, "finds.json")));
        Assert.Equal(1, survey!.Used);
        // Spansh's own place for it is the marker's now.
        Assert.Equal(new MapMark(Guessed, GuessedId64, -340.5, 20.25, -290.125) { Recorded = true }, await finds.Place(new GameNamed(Guessed, GuessedId64, null)));
    }

    [Fact]
    public async Task What_is_kept_on_this_PC_is_looked_in_first_and_Spansh_only_when_it_has_nothing()
    {
        // The detailed map of the block round Wredguia (see FineMapTests): the cube of Wredguia WD-K d8 has 0, 1, 3 and 7.
        using (var file = File.Create(Path.Combine(temp, FineMap.DownloadName)))
        using (var packed = new GZipStream(file, CompressionLevel.Optimal))
            packed.Write(new UTF8Encoding(false).GetBytes(SpanshStandIn.Recorded("systems-small.json")));
        File.SetLastWriteTimeUtc(Path.Combine(temp, FineMap.DownloadName), new DateTime(2026, 10, 14, 11, 53, 17, DateTimeKind.Utc));
        HeatmapFinds finds = Start();
        Assert.Null((await fine!.Build(38, 18)).Problem);

        // In the detailed map: recorded, and nobody is asked.
        TargetNews? news = await finds.Check("Wredguia WD-K d8-3", 113489480011);
        Assert.Equal(HeatmapFinds.Recorded, news!.State);
        Assert.Empty(net.Asked);
        Assert.Equal(0, survey!.Used);

        // Not in it, though the detailed map covers its sector: Spansh is asked, and the find says what was looked in.
        long fifth = SystemAddress.WithNumber(10410264907, 5);
        net.Then(NoSuchName);
        news = await finds.Check("Wredguia WD-K d8-5", fifth);
        Assert.Equal(HeatmapFinds.NotRecorded, news!.State);
        Assert.Equal([Asked("Wredguia WD-K d8-5")], net.Asked);
        Assert.Equal("not in the detailed map (Spansh's list of 14 Oct 2026)", Assert.Single(finds.Finds).How[0]);

        // In a survey kept on this PC: recorded, and nobody is asked.
        net.Then("""{"count":1,"from":0,"results":[],"size":1}""")
            .Then("""{"count":1,"from":0,"results":[{"bodies":[],"body_count":null,"id64":5306264359626,"name":"Suhte","updated_at":"2026-10-14T10:00:00Z","x":-166.53125,"y":31.46875,"z":-43.4375}],"size":500}""");
        Assert.True((await survey.Survey("s:38:32:18", go: false)).Area!.Complete);
        int asked = net.Asked.Count;
        news = await finds.Check("Suhte", 5306264359626);
        Assert.Equal((HeatmapFinds.Recorded, asked), (news!.State, net.Asked.Count));
        Assert.Equal(new MapMark("Suhte", 5306264359626, -166.53125, 31.46875, -43.4375) { Recorded = true }, await finds.Place(new GameNamed("Suhte", 5306264359626, null)));
    }

    [Fact]
    public async Task A_system_is_not_checked_again_while_the_memory_keeps_its_answer_a_failure_ten_minutes_and_not_recorded_a_day()
    {
        HeatmapFinds finds = Start();

        // Offline: no find, and the problem in plain words.
        TargetNews? news = await finds.Check(Guessed, GuessedId64);
        Assert.Equal(new TargetNews(HeatmapFinds.FromTarget, Guessed, GuessedId64, HeatmapFinds.Unchecked, Unreachable), news);
        Assert.Empty(finds.Finds);
        Assert.Equal(1, survey!.Used);   // counted, though it was not answered
        Assert.Equal((CheckMemory.Failed, Unreachable), (finds.Memory.Get(Guessed, GuessedId64)!.Result, finds.Memory.Get(Guessed, GuessedId64)!.Reason));

        // Not again for ten minutes, whatever the capitals, nor after Studio is started again: no request, no news.
        net.Then(NoSuchName);
        Assert.Null(await finds.Check(Guessed, GuessedId64));
        Assert.Null(await finds.Check(Guessed.ToUpperInvariant(), GuessedId64, null, HeatmapFinds.FromRouteEnd));
        Assert.Null(await Start().Check(Guessed, GuessedId64));
        Assert.Single(net.Asked);

        // Ten minutes later it is checked again.
        clock += CheckMemory.FailedKept + TimeSpan.FromSeconds(1);
        HeatmapFinds later = Start();
        news = await later.Check(Guessed, GuessedId64);
        Assert.Equal(HeatmapFinds.NotRecorded, news!.State);
        Assert.Equal(2, net.Asked.Count);

        // Not recorded: not asked about again for a day (a new start of Studio included), then once more at the next
        // sighting, in the same session too (Spansh is asked afresh, not answered from what it said a day ago).
        clock += CheckMemory.NotRecordedKept - TimeSpan.FromMinutes(1);
        Assert.Null(await Start().Check(Guessed, GuessedId64));
        Assert.Null(await later.Check(Guessed, GuessedId64));
        Assert.Equal(2, net.Asked.Count);
        clock += TimeSpan.FromMinutes(2);
        net.Then(Found(Guessed, GuessedId64, -340, 20, -290));   // somebody has recorded it meanwhile
        Assert.Equal(HeatmapFinds.Recorded, (await later.Check(Guessed, GuessedId64))!.State);
        Assert.Equal(3, net.Asked.Count);

        // Nothing to check without a name and a number.
        Assert.Null(await finds.Check("", 5));
        Assert.Null(await finds.Check("Sol", 0));
        Assert.Equal(3, net.Asked.Count);
    }

    [Fact]
    public async Task Check_my_targets_off_checks_nothing_and_is_kept_in_settings()
    {
        HeatmapFinds finds = Start();
        Assert.True(finds.CheckTargets);   // on unless the player says otherwise

        finds.CheckTargets = false;

        Assert.Null(await finds.Check(Guessed, GuessedId64));
        Assert.Empty(net.Asked);
        Assert.False((bool)JsonNode.Parse(File.ReadAllText(SettingsFile))!["heatmap"]!["checkTargets"]!);
        Assert.False(Start().CheckTargets);   // the next session too

        // On again: checked (it was not checked while off).
        finds.CheckTargets = true;
        net.Then(NoSuchName);
        Assert.Equal(HeatmapFinds.NotRecorded, (await finds.Check(Guessed, GuessedId64))!.State);
    }

    [Fact]
    public async Task The_lookup_counts_in_the_3D_maps_budget_and_waits_its_turn_once_the_budget_is_spent()
    {
        HeatmapFinds finds = Start();
        // The session's 200 requests spent on counts that found the PC offline (each counts whether answered or not).
        for (int count = 0; count < HeatmapSurvey.SessionCap; count++) await survey!.Count("cube", -167.09, 26.72, -55.38, go: true);
        Assert.Equal(HeatmapSurvey.SessionCap, survey!.Used);
        net.Then(NoSuchName);

        TargetNews? news = await finds.Check(Guessed, GuessedId64);

        Assert.Equal(HeatmapFinds.Unchecked, news!.State);
        Assert.StartsWith("Studio allows itself 200 requests to Spansh for the 3D map each time it runs", news.Problem, StringComparison.Ordinal);
        Assert.Equal(HeatmapSurvey.SessionCap, net.Asked.Count);   // the lookup was not sent
        Assert.Empty(finds.Finds);
    }

    // ------------------------------------------------------------ what the game names (only the route's end checked, as before 15 October 2026)

    [Fact]
    public async Task The_games_targets_and_route_ends_are_checked_as_they_come_but_not_a_routes_hops_nor_what_was_there_at_the_start()
    {
        // At Studio's start the journal is read again, and a route is already plotted: history, not checked.
        game.SetRoute([new RouteStop("Practice Home", 1001, [0, 0, 0], "K"), new RouteStop("Old End", 77777, [10, 0, 10], "M")]);
        HeatmapFinds finds = Start(routeCheck: HeatmapFinds.CheckEnds);
        var news = new List<TargetNews>();
        finds.News += said =>
        {
            lock (news) news.Add(said);   // a check ends on another thread than the game's in a test (no UI thread here)
        };
        Line("""{ "timestamp":"2026-10-15T09:00:00Z", "event":"FSDTarget", "Name":"Old Target", "SystemAddress":88888, "StarClass":"K" }""", replay: true);
        game.RaiseChanged();
        await finds.Idle;
        Assert.Empty(net.Asked);
        Assert.Empty(news);
        (GameNamed? target, GameNamed? end) = finds.Marks();
        Assert.Equal(("Old Target", "Old End"), (target?.Name, end?.Name));   // the markers still show them

        // A system targeted without a route: checked at once.
        net.Then(NoSuchName);
        Line($$"""{ "timestamp":"2026-10-15T10:00:00Z", "event":"FSDTarget", "Name":"{{Guessed}}", "SystemAddress":{{GuessedId64}}, "StarClass":"M" }""");
        game.RaiseChanged();
        await finds.Idle;
        Assert.Equal([Asked(Guessed)], net.Asked);
        Assert.Equal(Guessed, Assert.Single(finds.Finds).Name);
        Assert.Equal(
            [
                new TargetNews(HeatmapFinds.FromTarget, Guessed, GuessedId64, HeatmapFinds.Moved, null),
                new TargetNews(HeatmapFinds.FromTarget, Guessed, GuessedId64, HeatmapFinds.NotRecorded, null),
            ],
            news.OrderBy(said => said.State == HeatmapFinds.Moved ? 0 : 1));

        // A route plotted: the game's first hop is not checked, the route's end is, once the new route's file has been read.
        Line("""{ "timestamp":"2026-10-15T10:05:00Z", "event":"NavRoute" }""");
        Line("""{ "timestamp":"2026-10-15T10:05:00Z", "event":"FSDTarget", "Name":"First Hop", "SystemAddress":99999, "StarClass":"M", "RemainingJumpsInRoute":2 }""");
        game.RaiseChanged();   // the route's file not written yet
        await finds.Idle;
        Assert.Single(net.Asked);
        net.Then(Found("Far End", 123456, 100, 0, 100));
        game.SetRoute([new RouteStop("Practice Home", 1001, [0, 0, 0], "K"), new RouteStop("First Hop", 99999, [5, 0, 5], "M"), new RouteStop("Far End", 123456, [100, 0, 100], "K")]);
        game.RaiseChanged();
        await finds.Idle;
        Assert.Equal([Asked(Guessed), Asked("Far End")], net.Asked);
        Assert.Single(finds.Finds);   // Spansh knows it
        (_, end) = finds.Marks();
        Assert.Equal(new MapMark("Far End", 123456, 100, 0, 100) { Recorded = true }, await finds.Place(end));

        // The destination in Status.json, named by the game: checked too, once.
        net.Then(NoSuchName);
        game.SetStatus(GameStatus.None with { Destination = new StatusDestination(4242, 0, "Wredguia AB-C d1") });
        game.RaiseChanged();
        game.RaiseChanged();
        await finds.Idle;
        Assert.Equal(3, net.Asked.Count);
        Assert.Equal(Asked("Wredguia AB-C d1"), net.Asked[2]);
        (target, _) = finds.Marks();
        Assert.Equal(("Wredguia AB-C d1", 4242L), (target!.Name, target.Id64));
    }

    [Fact]
    public async Task The_nav_panels_target_on_a_plotted_route_is_the_games_pick_so_only_the_routes_end_is_checked_as_the_routes_end()
    {
        HeatmapFinds finds = Start(routeCheck: HeatmapFinds.CheckEnds);
        game.RaiseChanged();   // Studio's start: nothing there yet
        await finds.Idle;

        // A route of four stops plotted. The game aims at the first hop at once, and Status.json's destination (the nav
        // panel's target) names it in the same Poll, before the route's file has been read: the line says it is a hop.
        Line("""{ "timestamp":"2026-10-15T11:00:00Z", "event":"NavRoute" }""");
        Line("""{ "timestamp":"2026-10-15T11:00:00Z", "event":"FSDTarget", "Name":"First Hop", "SystemAddress":99999, "StarClass":"M", "RemainingJumpsInRoute":3 }""");
        game.SetStatus(GameStatus.None with { Destination = new StatusDestination(99999, 0, "First Hop") });
        game.RaiseChanged();
        await finds.Idle;
        Assert.Empty(net.Asked);

        // The route's file: its end is checked, as the route's end.
        net.Then(NoSuchName);
        game.SetRoute(
        [
            new RouteStop("Practice Home", 1001, [0, 0, 0], "K"), new RouteStop("First Hop", 99999, [5, 0, 5], "M"),
            new RouteStop("Second Hop", 88888, [10, 0, 10], "M"), new RouteStop(Guessed, GuessedId64, [15, 0, 15], "M"),
        ]);
        game.RaiseChanged();
        await finds.Idle;
        Assert.Equal([Asked(Guessed)], net.Asked);
        Assert.Equal(HeatmapFinds.FromRouteEnd, Assert.Single(finds.Finds).From);

        // A jump later the game aims at the next hop, and the nav panel names it: a stop of the route, not checked.
        Line("""{ "timestamp":"2026-10-15T11:01:00Z", "event":"FSDTarget", "Name":"Second Hop", "SystemAddress":88888, "StarClass":"M", "RemainingJumpsInRoute":2 }""");
        game.SetStatus(GameStatus.None with { Destination = new StatusDestination(88888, 0, "Second Hop") });
        game.RaiseChanged();
        // The middle hop named by Status.json alone (the FSDTarget line aims elsewhere): a stop of the route all the same.
        Line($$"""{ "timestamp":"2026-10-15T11:02:00Z", "event":"FSDTarget", "Name":"{{Guessed}}", "SystemAddress":{{GuessedId64}}, "StarClass":"M", "RemainingJumpsInRoute":1 }""");
        game.SetStatus(GameStatus.None with { Destination = new StatusDestination(99999, 0, "First Hop") });
        game.RaiseChanged();
        // And the route's end named by the nav panel: already checked as the route's end, and not again as a target.
        game.SetStatus(GameStatus.None with { Destination = new StatusDestination(GuessedId64, 0, Guessed) });
        game.RaiseChanged();
        await finds.Idle;
        Assert.Single(net.Asked);
        Assert.Equal(HeatmapFinds.FromRouteEnd, Assert.Single(finds.Finds).From);

        // A one-jump route whose end the nav panel names before the route's file is read: checked as the route's end.
        Line("""{ "timestamp":"2026-10-15T11:05:00Z", "event":"NavRoute" }""");
        Line("""{ "timestamp":"2026-10-15T11:05:00Z", "event":"FSDTarget", "Name":"Wredguia AB-C d1", "SystemAddress":4242, "StarClass":"M", "RemainingJumpsInRoute":1 }""");
        game.SetStatus(GameStatus.None with { Destination = new StatusDestination(4242, 0, "Wredguia AB-C d1") });
        game.RaiseChanged();
        await finds.Idle;
        Assert.Single(net.Asked);
        net.Then(NoSuchName);
        game.SetRoute([new RouteStop("Practice Home", 1001, [0, 0, 0], "K"), new RouteStop("Wredguia AB-C d1", 4242, [20, 0, 20], "M")]);
        game.RaiseChanged();
        await finds.Idle;
        Assert.Equal([Asked(Guessed), Asked("Wredguia AB-C d1")], net.Asked);
        Assert.Equal(HeatmapFinds.FromRouteEnd, Assert.Single(finds.Finds, find => find.Id64 == 4242).From);

        // The check of every stop is not in use: nothing to show of it.
        RouteCheckView check = finds.RouteCheckNow();
        Assert.Equal((HeatmapFinds.Off, HeatmapFinds.CheckEnds, 0), (check.State, check.Mode, check.Stops.Count));
    }

    [Fact]
    public async Task A_route_whose_file_comes_a_Poll_before_its_journal_line_has_its_end_checked_when_the_line_comes()
    {
        game.SetRoute([new RouteStop("Practice Home", 1001, [0, 0, 0], "K"), new RouteStop("Old End", 77777, [10, 0, 10], "M")]);
        HeatmapFinds finds = Start(routeCheck: HeatmapFinds.CheckEnds);
        game.RaiseChanged();   // Studio's start: the route there is history
        await finds.Idle;

        // The game writes NavRoute.json a moment before its NavRoute line: one Poll reads the new file, the next the line.
        game.SetRoute([new RouteStop("Practice Home", 1001, [0, 0, 0], "K"), new RouteStop("First Hop", 99999, [5, 0, 5], "M"), new RouteStop(Guessed, GuessedId64, [15, 0, 15], "M")]);
        game.RaiseChanged();
        await finds.Idle;
        Assert.Empty(net.Asked);   // a file alone is no route the player plotted just now

        net.Then(NoSuchName);
        Line("""{ "timestamp":"2026-10-15T12:00:00Z", "event":"NavRoute" }""");
        Line("""{ "timestamp":"2026-10-15T12:00:00Z", "event":"FSDTarget", "Name":"First Hop", "SystemAddress":99999, "StarClass":"M", "RemainingJumpsInRoute":2 }""");
        game.RaiseChanged();
        await finds.Idle;

        // At once, not ten changes of the game's state later (by when the route may have been flown and cleared).
        Assert.Equal([Asked(Guessed)], net.Asked);
        HeatmapFind find = Assert.Single(finds.Finds);
        Assert.Equal((Guessed, HeatmapFinds.FromRouteEnd, (double?)15), (find.Name, find.From, find.X));

        // The route flown and cleared: nothing more is asked.
        Line("""{ "timestamp":"2026-10-15T12:03:00Z", "event":"NavRouteClear" }""");
        game.SetRoute([]);
        game.RaiseChanged();
        await finds.Idle;
        Assert.Single(net.Asked);
    }

    // ------------------------------------------------------------ every stop of a plotted route (the owner's choice of 15 October 2026)

    /// <summary>Practice Home, where the route starts, then that many stops no list here has: "Stop 1" on, numbered <see cref="StopId"/>, at (i, 0, −i).</summary>
    static RouteStop[] Route(int stops, int from = 1) =>
    [
        new RouteStop("Practice Home", 1001, [0, 0, 0], "K"),
        .. Enumerable.Range(from, stops).Select(Stop),
    ];

    static RouteStop Stop(int i) => new($"Stop {i}", StopId(i), [i, 0, -i], "M");

    static long StopId(int i) => 7_000_000 + i;

    static IEnumerable<string> Stops(int from, int to) => Enumerable.Range(from, to - from + 1).Select(i => $"Stop {i}");

    /// <summary>The request for the systems under these exact names, word for word as Studio sends it.</summary>
    static string NamesAsked(IEnumerable<string> names)
    {
        string[] all = [.. names];
        return "POST /api/systems/search {\"filters\":{\"name\":{\"value\":[" + string.Join(",", all.Select(name => $"\"{name}\"")) + "]}},\"size\":"
            + all.Length.ToString(CultureInfo.InvariantCulture) + ",\"page\":0}";
    }

    /// <summary>Spansh's answer to a search by names, in its layout: these systems (no number written for 0), as many as it found.</summary>
    static string Named(IEnumerable<(string Name, long Id64)> systems)
    {
        (string Name, long Id64)[] all = [.. systems];
        return JsonSerializer.Serialize(new
        {
            count = all.Length,
            from = 0,
            results = all.Select(system => system.Id64 > 0
                ? new Dictionary<string, object> { ["bodies"] = Array.Empty<object>(), ["id64"] = system.Id64, ["name"] = system.Name, ["x"] = 1.5, ["y"] = 2.5, ["z"] = 3.5 }
                : new Dictionary<string, object> { ["bodies"] = Array.Empty<object>(), ["name"] = system.Name, ["x"] = 1.5, ["y"] = 2.5, ["z"] = 3.5 }),
            size = Math.Max(1, all.Length),
        });
    }

    /// <summary>Spansh has every one of these stops.</summary>
    static string AllOf(int from, int to) => Named(Enumerable.Range(from, to - from + 1).Select(i => ($"Stop {i}", StopId(i))));

    static RouteCheckStop StopNamed(RouteCheckView check, string name) => check.Stops.Single(stop => stop.Name == name);

    /// <summary>Holds back every request to Spansh until it is let go; from then on everything passes straight on.</summary>
    sealed class Held(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LetGo { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Holding.TrySetResult();
            await LetGo.Task.WaitAsync(cancel);
            return await base.SendAsync(request, cancel);
        }
    }

    [Fact]
    public async Task Every_stop_of_a_plotted_route_is_asked_about_100_names_to_a_request_and_matched_by_name_and_number()
    {
        HeatmapFinds finds = Start();
        Assert.Equal(HeatmapFinds.CheckAll, finds.RouteCheck);   // what the owner asked for, unless the player chooses otherwise
        var found = new List<HeatmapFind>();
        var news = new List<TargetNews>();
        var told = new List<RouteCheckNews>();
        // Checks end on other threads than the game's in a test (no UI thread here).
        finds.Found += find =>
        {
            lock (found) found.Add(find);
        };
        finds.News += said =>
        {
            lock (news) news.Add(said);
        };
        finds.RouteNews += said =>
        {
            lock (told) told.Add(said);
        };
        game.RaiseChanged();   // Studio's start: no route
        await finds.Idle;
        Assert.Equal((HeatmapFinds.NoRoute, 0), (finds.RouteCheckNow().State, finds.RouteCheckNow().Total));

        // 230 stops no list here has. Spansh has every one but three: Stop 7 (not in its answer), Stop 8 (it has another
        // system of that name, with another number) and Stop 230, the route's end. Stop 9 comes back in other capitals and
        // Stop 10 without a number: both count.
        IEnumerable<(string, long)> Answer(int from, int to, int missing) => Enumerable.Range(from, to - from + 1).Where(i => i != missing).Select(i => i switch
        {
            8 => ("Stop 8", 123L),
            9 => ("STOP 9", StopId(9)),
            10 => ("Stop 10", 0L),
            _ => ($"Stop {i}", StopId(i)),
        });
        net.Then(Named(Answer(1, 100, missing: 7))).Then(AllOf(101, 200)).Then(Named(Answer(201, 230, missing: 230)));
        game.SetRoute(Route(230));
        game.RaiseChanged();
        await finds.Idle;

        // Three requests of 100, 100 and 30 names, each one of the 3D map's own.
        Assert.Equal([NamesAsked(Stops(1, 100)), NamesAsked(Stops(101, 200)), NamesAsked(Stops(201, 230))], net.Asked);
        Assert.Equal(
            """POST /api/systems/search {"filters":{"name":{"value":["Stop 201","Stop 202","Stop 203","Stop 204","Stop 205","Stop 206","Stop 207","Stop 208","Stop 209","Stop 210","Stop 211","Stop 212","Stop 213","Stop 214","Stop 215","Stop 216","Stop 217","Stop 218","Stop 219","Stop 220","Stop 221","Stop 222","Stop 223","Stop 224","Stop 225","Stop 226","Stop 227","Stop 228","Stop 229","Stop 230"]}},"size":30,"page":0}""",
            net.Asked[2]);
        Assert.Equal(3, survey!.Used);

        RouteCheckView check = finds.RouteCheckNow();
        Assert.Equal((HeatmapFinds.Done, HeatmapFinds.CheckAll, 230, 230, 227, 3, 0, 3, (string?)null),
            (check.State, check.Mode, check.Total, check.Checked, check.Recorded, check.NotRecorded, check.Failed, check.Requests, check.Reason));
        Assert.Equal(Stops(1, 230), check.Stops.Select(stop => stop.Name));   // in route order, its start left out
        Assert.Equal(["Stop 7", "Stop 8", "Stop 230"], check.Stops.Where(stop => stop.State == HeatmapFinds.NotRecorded).Select(stop => stop.Name));
        Assert.Equal(new RouteCheckStop("Stop 9", StopId(9), 9, 0, -9, HeatmapFinds.Recorded, "found by Spansh's system search", null), StopNamed(check, "Stop 9"));
        Assert.Equal(HeatmapFinds.Recorded, StopNamed(check, "Stop 10").State);
        Assert.Equal(new RouteCheckStop("Stop 8", StopId(8), 8, 0, -8, HeatmapFinds.NotRecorded, "not found by Spansh's system search", null), StopNamed(check, "Stop 8"));
        Assert.InRange(check.Started!.Value, DateTime.UtcNow.AddMinutes(-5), check.Finished!.Value);

        // The three are kept as finds, placed where the game says, from the route; the recorded ones are not.
        Assert.Equal(["Stop 7", "Stop 8", "Stop 230"], found.Select(find => find.Name));
        Assert.Equal(found.Select(find => find.Id64).Order(), finds.Finds.Select(find => find.Id64).Order());
        HeatmapFind end = found[2];
        Assert.Equal<(long, double?, double?, double?, double?, string)>((StopId(230), 230, 0, -230, null, HeatmapFinds.FromRoute), (end.Id64, end.X, end.Y, end.Z, end.PlusMinus, end.From));
        Assert.Equal("not found by Spansh's system search (" + DateTime.UtcNow.ToString("d MMM yyyy", CultureInfo.InvariantCulture) + ")", end.How[^1]);
        Assert.Contains("\"from\": \"route\"", File.ReadAllText(Path.Combine(temp, "finds.json")), StringComparison.Ordinal);

        // The route's end is told as the route's end, as the check of the end alone tells it, so its marker says so too.
        Assert.Contains(new TargetNews(HeatmapFinds.FromRouteEnd, "Stop 230", StopId(230), HeatmapFinds.NotRecorded, null), news);
        Assert.Equal(new MapMark("Stop 230", StopId(230), 230, 0, -230) { Recorded = false }, await finds.Place(finds.Marks().RouteEnd));
        // The page and the overlay heard it begin, go on and end.
        Assert.Equal((HeatmapFinds.Checking, 230, 0), (told[0].State, told[0].Total, told[0].Checked));
        Assert.Contains(told, said => said.State == HeatmapFinds.Checking);
        Assert.Equal((HeatmapFinds.Done, 230, 3), (told[^1].State, told[^1].Checked, told[^1].Requests));
        Assert.Equal("route-check", told[0].Kind);
        Assert.StartsWith("""{"kind":"route-check","state":"done","mode":"all",""", JsonSerializer.Serialize(told[^1], new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.Ordinal);

        // A new route through two of them: settled at once from the memory of checks, with no request and no find twice.
        game.SetRoute([.. Route(0), Stop(7), Stop(9)]);
        game.RaiseChanged();
        await finds.Idle;
        Assert.Equal(3, net.Asked.Count);
        check = finds.RouteCheckNow();
        Assert.Equal([(HeatmapFinds.NotRecorded, "checked earlier: not found by Spansh's system search"), (HeatmapFinds.Recorded, "checked earlier: found by Spansh's system search")],
            check.Stops.Select(stop => (stop.State, stop.How)));
        Assert.Equal((HeatmapFinds.Done, 0), (check.State, check.Requests));
        Assert.Equal(3, found.Count);
    }

    [Fact]
    public async Task Stops_kept_on_this_PC_never_reach_Spansh_and_a_system_checked_earlier_this_session_is_not_asked_again()
    {
        // The detailed map round Wredguia (see above), a survey of Wredguia's own sector, and a find of an earlier session.
        using (var file = File.Create(Path.Combine(temp, FineMap.DownloadName)))
        using (var packed = new GZipStream(file, CompressionLevel.Optimal))
            packed.Write(new UTF8Encoding(false).GetBytes(SpanshStandIn.Recorded("systems-small.json")));
        File.SetLastWriteTimeUtc(Path.Combine(temp, FineMap.DownloadName), new DateTime(2026, 10, 14, 11, 53, 17, DateTimeKind.Utc));
        File.WriteAllText(Path.Combine(temp, "finds.json"),
            $$"""[{"name":"{{Guessed}}","id64":"{{GuessedId64}}","x":-345,"y":15,"z":-305,"plusMinus":40,"at":"2026-10-14T12:00:00Z","from":"target","how":[]}]""");
        HeatmapFinds finds = Start();
        Assert.Null((await fine!.Build(38, 18)).Problem);
        net.Then("""{"count":1,"from":0,"results":[],"size":1}""")
            .Then("""{"count":1,"from":0,"results":[{"bodies":[],"body_count":null,"id64":5306264359626,"name":"Suhte","updated_at":"2026-10-14T10:00:00Z","x":-166.53125,"y":31.46875,"z":-43.4375}],"size":500}""");
        Assert.True((await survey!.Survey("s:38:32:18", go: false)).Area!.Complete);
        // A target checked earlier this session: Spansh's name search had it.
        net.Then(Found("Wredguia AB-C d1", 4242, -340, 20, -290));
        Assert.Equal(HeatmapFinds.Recorded, (await finds.Check("Wredguia AB-C d1", 4242))!.State);
        game.RaiseChanged();   // Studio's start
        await finds.Idle;
        int before = net.Asked.Count;

        // A route through all of them, and one system nothing here has: only that one is asked about.
        net.Then(Named([]));
        game.SetRoute(
        [
            new RouteStop("Practice Home", 1001, [0, 0, 0], "K"), new RouteStop("Wredguia WD-K d8-3", 113489480011, null, "M"),
            new RouteStop("Suhte", 5306264359626, [-166.53125, 31.46875, -43.4375], "K"), new RouteStop(Guessed, GuessedId64, null, "M"),
            new RouteStop("Wredguia AB-C d1", 4242, null, "M"), new RouteStop("Unknown One", 5151, [1, 2, 3], "M"),
        ]);
        game.RaiseChanged();
        await finds.Idle;
        Assert.Equal([NamesAsked(["Unknown One"])], net.Asked.Skip(before));
        RouteCheckView check = finds.RouteCheckNow();
        Assert.Equal(
            [
                (HeatmapFinds.Recorded, "in the detailed map (Spansh's list of 14 Oct 2026)"),
                (HeatmapFinds.Recorded, "in Spansh's listing of Wredguia kept on this PC (" + DateTime.UtcNow.ToString("d MMM yyyy", CultureInfo.InvariantCulture) + ")"),
                (HeatmapFinds.NotRecorded, "in your finds"),
                (HeatmapFinds.Recorded, "checked earlier: found by Spansh's name search"),
                (HeatmapFinds.NotRecorded, "not found by Spansh's system search"),
            ],
            check.Stops.Select(stop => (stop.State, stop.How!)));
        Assert.Equal((5, 1, 3, 2), (check.Checked, check.Requests, check.Recorded, check.NotRecorded));
        Assert.Equal(["Unknown One", Guessed], finds.Finds.OrderByDescending(find => find.At).Select(find => find.Name));   // the earlier find not kept twice

        // A second route through the unknown one and a system the detailed map covers but does not have: only the latter is asked about.
        long fifth = SystemAddress.WithNumber(10410264907, 5);
        net.Then(Named([("Wredguia WD-K d8-5", fifth)]));
        game.SetRoute([new RouteStop("Practice Home", 1001, [0, 0, 0], "K"), new RouteStop("Unknown One", 5151, [1, 2, 3], "M"), new RouteStop("Wredguia WD-K d8-5", fifth, null, "M")]);
        game.RaiseChanged();
        await finds.Idle;
        Assert.Equal([NamesAsked(["Unknown One"]), NamesAsked(["Wredguia WD-K d8-5"])], net.Asked.Skip(before));
        Assert.Equal([HeatmapFinds.NotRecorded, HeatmapFinds.Recorded], finds.RouteCheckNow().Stops.Select(stop => stop.State));
    }

    [Fact]
    public async Task Past_the_sessions_requests_the_stops_left_are_not_checked_and_the_check_says_it_stopped()
    {
        HeatmapFinds finds = Start();
        // All but one of the session's 200 requests spent on counts that found the PC offline.
        for (int count = 0; count < HeatmapSurvey.SessionCap - 1; count++) await survey!.Count("cube", -167.09, 26.72, -55.38, go: true);
        game.RaiseChanged();
        await finds.Idle;

        net.Then(AllOf(1, 100));
        game.SetRoute(Route(230));
        game.RaiseChanged();
        await finds.Idle;

        // The one request left asks about the first 100; the other 130 are not asked about, and say why.
        Assert.Equal(HeatmapSurvey.SessionCap, net.Asked.Count);
        Assert.Equal(NamesAsked(Stops(1, 100)), net.Asked[^1]);
        RouteCheckView check = finds.RouteCheckNow();
        Assert.Equal((HeatmapFinds.Stopped, HeatmapFinds.LimitReached, 1, 100, 130), (check.State, check.Reason, check.Requests, check.Checked, check.Failed));
        Assert.All(check.Stops.Skip(100), stop => Assert.Equal((HeatmapFinds.Failed, (string?)"request limit reached", (string?)null), (stop.State, stop.Reason, stop.How)));
        Assert.NotNull(check.Finished);
        Assert.Empty(finds.Finds);
    }

    [Fact]
    public async Task A_batch_that_fails_marks_its_stops_with_the_reason_is_not_sent_again_and_the_batches_after_it_still_run()
    {
        HeatmapFinds finds = Start();
        game.RaiseChanged();
        await finds.Idle;

        net.ThenOffline().Then(AllOf(101, 150));
        game.SetRoute(Route(150));
        game.RaiseChanged();
        await finds.Idle;

        Assert.Equal([NamesAsked(Stops(1, 100)), NamesAsked(Stops(101, 150))], net.Asked);   // no second try of the first
        RouteCheckView check = finds.RouteCheckNow();
        Assert.Equal((HeatmapFinds.Done, 2, 50, 100), (check.State, check.Requests, check.Recorded, check.Failed));
        Assert.Equal((HeatmapFinds.Failed, (string?)Unreachable), (check.Stops[0].State, check.Stops[0].Reason));
        Assert.Empty(finds.Finds);   // a stop that could not be checked is no find

        // On a new route a stop that could not be checked is not asked about again for ten minutes, and still says why.
        game.SetRoute([.. Route(0), Stop(1)]);
        game.RaiseChanged();
        await finds.Idle;
        Assert.Equal(2, net.Asked.Count);
        Assert.Equal((HeatmapFinds.Failed, (string?)Unreachable), (finds.RouteCheckNow().Stops[0].State, finds.RouteCheckNow().Stops[0].Reason));

        // Ten minutes later, on the next route through it, it is.
        clock += CheckMemory.FailedKept + TimeSpan.FromSeconds(1);
        net.Then(AllOf(1, 1));
        game.SetRoute([.. Route(0), Stop(500), Stop(1)]);
        game.RaiseChanged();
        await finds.Idle;
        Assert.Equal(NamesAsked(["Stop 500", "Stop 1"]), net.Asked[^1]);
        Assert.Equal(HeatmapFinds.Recorded, StopNamed(finds.RouteCheckNow(), "Stop 1").State);
    }

    [Fact]
    public async Task A_new_route_during_a_check_stops_the_old_one_after_its_request_under_way()
    {
        var held = new Held(net);
        HeatmapFinds finds = Start(handler: held);
        game.RaiseChanged();
        await finds.Idle;

        // A route of 150 stops: its first request goes, and is held on the way. Spansh has all of it but Stop 3.
        net.Then(Named(Enumerable.Range(1, 100).Where(i => i != 3).Select(i => ($"Stop {i}", StopId(i)))));
        game.SetRoute(Route(150));
        game.RaiseChanged();
        await held.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Meanwhile a new route, through a stop of the old one's second batch and two more: its check waits its turn.
        game.SetRoute([.. Route(0), Stop(120), Stop(500), Stop(501)]);
        game.RaiseChanged();
        RouteCheckView waiting = finds.RouteCheckNow();
        Assert.Equal((HeatmapFinds.Waiting, 3), (waiting.State, waiting.Total));
        Assert.All(waiting.Stops, stop => Assert.Equal(HeatmapFinds.Waiting, stop.State));

        net.Then(Named([("Stop 120", StopId(120)), ("Stop 500", StopId(500)), ("Stop 501", StopId(501))]));
        held.LetGo.SetResult();
        await finds.Idle;

        // The old route's request under way was answered (and its find kept); its second batch was never asked.
        Assert.Equal([NamesAsked(Stops(1, 100)), NamesAsked(["Stop 120", "Stop 500", "Stop 501"])], net.Asked);
        Assert.Equal("Stop 3", Assert.Single(finds.Finds).Name);
        RouteCheckView check = finds.RouteCheckNow();
        Assert.Equal((HeatmapFinds.Done, 3, 3, 1), (check.State, check.Total, check.Recorded, check.Requests));
    }

    [Fact]
    public async Task A_route_cleared_takes_its_check_with_it_and_the_same_route_plotted_again_is_checked_afresh()
    {
        var held = new Held(net);
        HeatmapFinds finds = Start(handler: held);
        var told = new List<RouteCheckNews>();
        finds.RouteNews += said =>
        {
            lock (told) told.Add(said);
        };
        game.RaiseChanged();
        await finds.Idle;

        net.Then(AllOf(1, 100));
        game.SetRoute(Route(150));
        game.RaiseChanged();
        await held.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The route cleared in the game: the check goes at once, and stops after its request under way.
        Line("""{ "timestamp":"2026-10-15T13:00:00Z", "event":"NavRouteClear" }""");
        Assert.Equal((HeatmapFinds.NoRoute, 0), (finds.RouteCheckNow().State, finds.RouteCheckNow().Stops.Count));
        Assert.Equal(HeatmapFinds.NoRoute, told[^1].State);
        game.RaiseChanged();
        held.LetGo.SetResult();
        await finds.Idle;
        Assert.Single(net.Asked);
        Assert.Equal(HeatmapFinds.NoRoute, finds.RouteCheckNow().State);

        // The same route plotted again: a check of its own, which asks only about what the first did not.
        net.Then(AllOf(101, 150));
        game.SetRoute(Route(150));
        game.RaiseChanged();
        await finds.Idle;
        Assert.Equal([NamesAsked(Stops(1, 100)), NamesAsked(Stops(101, 150))], net.Asked);
        RouteCheckView check = finds.RouteCheckNow();
        Assert.Equal((HeatmapFinds.Done, 150, 1), (check.State, check.Recorded, check.Requests));
        Assert.Equal("checked earlier: found by Spansh's system search", check.Stops[0].How);
    }

    [Fact]
    public async Task What_of_a_route_is_checked_is_the_players_choice_kept_in_settings_and_with_the_switch_off_nothing_is_checked()
    {
        // At Studio's start a route is already plotted: history, not checked, so there is no check to show. But the page
        // and the overlay are told it is there and not checked, and how to have it checked, rather than "no route".
        game.SetRoute(Route(3));
        HeatmapFinds finds = Start();
        game.RaiseChanged();
        await finds.Idle;
        Assert.Empty(net.Asked);
        RouteCheckView before = finds.RouteCheckNow();
        Assert.Equal((HeatmapFinds.NoRoute, HeatmapFinds.CheckAll, 0, 3, "Stop 3"), (before.State, before.Mode, before.Total, before.Plotted, before.PlottedTo));
        Assert.Equal(new RouteCheckLine("not checked: your route was plotted before Studio started   ·   plot it again, or press Check this route on the 3D map", RouteCheckLine.Plain),
            before.Line);

        // The player chooses every stop on the page: the route plotted now is checked, at the player's word.
        net.Then(AllOf(1, 3));
        finds.RouteCheck = HeatmapFinds.CheckAll;
        await finds.Idle;
        Assert.Equal([NamesAsked(Stops(1, 3))], net.Asked);
        Assert.Equal((HeatmapFinds.Done, 0), (finds.RouteCheckNow().State, finds.RouteCheckNow().Plotted));
        Assert.Equal(new RouteCheckLine("done   ·   3 stops   ·   all recorded", RouteCheckLine.Green), finds.RouteCheckNow().Line);
        finds.RouteCheck = HeatmapFinds.CheckAll;   // already checked: nothing more
        await finds.Idle;
        Assert.Single(net.Asked);

        // Only the target and the route's end: the check goes, and the choice is kept in settings.json, beside the switch.
        finds.RouteCheck = " Ends ";
        Assert.Equal((HeatmapFinds.CheckEnds, HeatmapFinds.Off, HeatmapFinds.CheckEnds), (finds.RouteCheck, finds.RouteCheckNow().State, finds.RouteCheckNow().Mode));
        JsonNode saved = JsonNode.Parse(File.ReadAllText(SettingsFile))!["heatmap"]!;
        Assert.Equal(("ends", true), ((string)saved["routeCheck"]!, (bool)saved["checkTargets"]!));
        Assert.Throws<ArgumentException>(() => finds.RouteCheck = "sideways");
        Assert.Equal(HeatmapFinds.CheckEnds, finds.RouteCheck);

        // "Check my targets" off: with every stop chosen again, a new route is not checked at all.
        finds.CheckTargets = false;
        finds.RouteCheck = HeatmapFinds.CheckAll;
        game.SetRoute(Route(2, from: 10));
        game.RaiseChanged();
        await finds.Idle;
        Assert.Single(net.Asked);
        Assert.Equal(HeatmapFinds.Off, finds.RouteCheckNow().State);

        // Switched on again: the route plotted now is checked (the player's click asks for it).
        net.Then(AllOf(10, 11));
        finds.CheckTargets = true;
        await finds.Idle;
        Assert.Equal(NamesAsked(Stops(10, 11)), net.Asked[^1]);
        Assert.Equal((HeatmapFinds.Done, 2), (finds.RouteCheckNow().State, finds.RouteCheckNow().Recorded));

        // A value typed into the file by hand that means neither is taken for every stop.
        new Settings(SettingsFile).Set("heatmap", new HeatmapSettings { RouteCheck = "most" });
        Assert.Equal(HeatmapFinds.CheckAll, Start().RouteCheck);
    }

    [Fact]
    public async Task A_route_there_at_Studios_start_is_said_to_be_not_checked_until_it_is_cleared()
    {
        game.SetRoute(Route(5));
        HeatmapFinds finds = Start();
        var told = new List<RouteCheckNews>();
        finds.RouteNews += said =>
        {
            lock (told) told.Add(said);
        };
        game.RaiseChanged();   // Studio's start
        await finds.Idle;
        Assert.Equal((5, "Stop 5"), (finds.RouteCheckNow().Plotted, finds.RouteCheckNow().PlottedTo));
        // A page open already hears of it, with the overlay's line.
        Assert.Equal((HeatmapFinds.NoRoute, 5), (told[^1].State, told[^1].Plotted));
        Assert.StartsWith("not checked: your route was plotted before Studio started", told[^1].Line!.Words, StringComparison.Ordinal);

        // The route cleared in the game: no route, and nothing more to say.
        Line("""{ "timestamp":"2026-10-15T13:00:00Z", "event":"NavRouteClear" }""");
        game.SetRoute([]);
        game.RaiseChanged();
        await finds.Idle;
        Assert.Equal((HeatmapFinds.NoRoute, 0, (string?)null, (RouteCheckLine?)null),
            (finds.RouteCheckNow().State, finds.RouteCheckNow().Plotted, finds.RouteCheckNow().PlottedTo, finds.RouteCheckNow().Line));
        Assert.Equal((HeatmapFinds.NoRoute, 0, (RouteCheckLine?)null), (told[^1].State, told[^1].Plotted, told[^1].Line));
        Assert.Empty(net.Asked);
    }

    /// <summary>Waits, ten seconds at most, for what a check going on by itself is to come to.</summary>
    static async Task Until(Func<bool> done)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "gave up waiting");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A search of Spansh's by exact names, as the check of a route's stops sends it (a count of a cube is a search too, by place).</summary>
    static bool ByNames(string asked) => asked.Contains("\"name\":{\"value\":", StringComparison.Ordinal);

    [Fact]
    public async Task A_batch_still_waiting_its_turn_at_the_3D_maps_gate_is_never_sent_once_the_check_is_switched_off()
    {
        var held = new Held(net);
        HeatmapFinds finds = Start(handler: held);
        game.RaiseChanged();   // Studio's start: no route
        await finds.Idle;

        // A count of the 3D map's under way, held on its way to Spansh: the map's gate is taken.
        Task<CountAnswer> counting = survey!.Count("cube", -167.09, 26.72, -55.38, go: true);
        await held.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // A route plotted meanwhile: its stops are looked for on this PC, then wait their turn at the gate.
        game.SetRoute(Route(3));
        game.RaiseChanged();
        await Until(() => finds.RouteCheckNow() is { State: HeatmapFinds.Waiting } view && view.Stops.All(stop => stop.State == HeatmapFinds.Checking));

        // "Check my targets" switched off while it waits: the check goes at once (it does not wait for the count), and
        // the batch is never sent, nor counted in the session's requests, once the count is done.
        finds.CheckTargets = false;
        await finds.Idle.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(HeatmapFinds.Off, finds.RouteCheckNow().State);
        held.LetGo.SetResult();
        await counting;
        await finds.Idle;
        Assert.DoesNotContain(net.Asked, ByNames);
        Assert.Equal(1, survey.Used);

        // Switched on again: the stops it never asked about are anybody's to check again, and the route is checked afresh.
        net.Then(AllOf(1, 3));
        finds.CheckTargets = true;
        await finds.Idle;
        Assert.Equal(NamesAsked(Stops(1, 3)), Assert.Single(net.Asked, ByNames));
        Assert.Equal((HeatmapFinds.Done, 3, 1), (finds.RouteCheckNow().State, finds.RouteCheckNow().Recorded, finds.RouteCheckNow().Requests));
        Assert.Equal(2, survey.Used);
    }

    [Fact]
    public async Task A_batch_still_waiting_its_turn_at_the_gate_is_never_sent_when_a_new_route_takes_its_place()
    {
        var held = new Held(net);
        HeatmapFinds finds = Start(handler: held);
        game.RaiseChanged();
        await finds.Idle;

        Task<CountAnswer> counting = survey!.Count("cube", -167.09, 26.72, -55.38, go: true);
        await held.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        game.SetRoute(Route(3));
        game.RaiseChanged();
        await Until(() => finds.RouteCheckNow() is { State: HeatmapFinds.Waiting, Total: 3 } view && view.Stops.All(stop => stop.State == HeatmapFinds.Checking));

        // A new route while the old one's batch waits, through one of its stops: the old batch is dropped at once, and the
        // new route's own batch (that stop with it) takes its turn at the gate.
        game.SetRoute([.. Route(0), Stop(2), Stop(500)]);
        game.RaiseChanged();
        await Until(() => finds.RouteCheckNow() is { State: HeatmapFinds.Waiting, Total: 2 } view && view.Stops.All(stop => stop.State == HeatmapFinds.Checking));

        // The count held at the gate is answered first (the PC offline), then the new route's batch.
        net.ThenOffline().Then(Named([("Stop 2", StopId(2)), ("Stop 500", StopId(500))]));
        held.LetGo.SetResult();
        await counting;
        await finds.Idle;
        Assert.Equal([NamesAsked(["Stop 2", "Stop 500"])], net.Asked.Where(ByNames));
        Assert.Equal(2, survey.Used);
        Assert.Equal((HeatmapFinds.Done, 2, 1), (finds.RouteCheckNow().State, finds.RouteCheckNow().Recorded, finds.RouteCheckNow().Requests));
    }

    [Fact]
    public async Task Stops_waiting_their_turn_behind_Within_reachs_request_are_settled_from_its_answer_and_only_the_rest_are_asked_about()
    {
        var held = new Held(net);
        HeatmapFinds finds = Start(handler: held);
        game.RaiseChanged();
        await finds.Idle;

        // Within reach's request after a jump, on its way to Spansh (held here: a search round a point takes seconds),
        // holding the 3D map's gate. Its answer will list two stops of the route plotted meanwhile.
        net.Then(Named([("Stop 1", StopId(1)), ("Stop 2", StopId(2))]));
        Task<NearLookUp> near = survey!.SystemsNear(0, 0, 0, 30, stopping: TestContext.Current.CancellationToken);
        await held.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        game.SetRoute(Route(3));
        game.RaiseChanged();
        // Nothing listed them yet: all three wait their turn at the gate, to be asked about.
        await Until(() => finds.RouteCheckNow() is { State: HeatmapFinds.Waiting, Total: 3 } view && view.Stops.All(stop => stop.State == HeatmapFinds.Checking));

        // The answer comes: the two it lists are settled from it, and only the third is asked about.
        net.Then(Named([("Stop 3", StopId(3))]));
        held.LetGo.SetResult();
        Assert.Equal(2, (await near).Found!.Count);
        await finds.Idle;
        Assert.Equal([NamesAsked(["Stop 3"])], net.Asked.Where(ByNames));
        Assert.Equal(2, survey.Used);
        RouteCheckView check = finds.RouteCheckNow();
        Assert.Equal((HeatmapFinds.Done, 3, 1), (check.State, check.Recorded, check.Requests));
        Assert.Equal(["found by Spansh's system search (within reach)", "found by Spansh's system search (within reach)", "found by Spansh's system search"],
            check.Stops.Select(stop => stop.How));
    }

    [Fact]
    public async Task The_stops_a_batch_finds_not_recorded_are_kept_with_one_write_before_they_are_told()
    {
        HeatmapFinds finds = Start();
        var inFile = new List<int>();
        finds.Found += _ =>
        {
            lock (inFile) inFile.Add(JsonNode.Parse(File.ReadAllText(Path.Combine(temp, "finds.json")))!.AsArray().Count);
        };
        game.RaiseChanged();
        await finds.Idle;

        // Spansh has Stop 2 only: Stops 1, 3 and 4 are not recorded, and all three are on disk by the time the first is told.
        net.Then(Named([("Stop 2", StopId(2))]));
        game.SetRoute(Route(4));
        game.RaiseChanged();
        await finds.Idle;
        Assert.Equal([3, 3, 3], inFile);
        Assert.Equal(["Stop 1", "Stop 3", "Stop 4"], finds.Finds.Select(find => find.Name).Order());
    }

    [Fact]
    public async Task A_fault_of_Studios_own_fails_only_the_stop_it_came_with_and_gives_back_the_stops_never_asked_about()
    {
        HeatmapFinds finds = Start();
        game.RaiseChanged();
        await finds.Idle;

        // Looking for Stop 2 on this PC goes wrong in Studio itself: that stop alone could not be checked, the others are.
        finds.Searching = name =>
        {
            if (name == "Stop 2") throw new InvalidOperationException("a fault of Studio's own, for the test");
        };
        net.Then(Named([("Stop 1", StopId(1)), ("Stop 3", StopId(3))]));
        game.SetRoute(Route(3));
        game.RaiseChanged();
        await finds.Idle;
        Assert.Equal([NamesAsked(["Stop 1", "Stop 3"])], net.Asked);
        RouteCheckView check = finds.RouteCheckNow();
        Assert.Equal((HeatmapFinds.Done, 2, 1), (check.State, check.Recorded, check.Failed));
        Assert.Equal((HeatmapFinds.Failed, (string?)"Studio could not check this system."), (StopNamed(check, "Stop 2").State, StopNamed(check, "Stop 2").Reason));
        finds.Searching = null;

        // A fault after a request went (here in whoever is told of a find): the stops asked about keep their answers, and
        // those the check took to ask about later but never did are given back, so the next check asks about them.
        finds.Found += _ => throw new InvalidOperationException("a fault of Studio's own, for the test");
        net.Then(Named(Enumerable.Range(4, 100).Where(i => i != 6).Select(i => ($"Stop {i}", StopId(i)))));
        game.SetRoute(Route(150, from: 4));
        game.RaiseChanged();
        await finds.Idle;
        Assert.Equal(NamesAsked(Stops(4, 103)), net.Asked[^1]);
        check = finds.RouteCheckNow();
        Assert.Equal((HeatmapFinds.Done, 99, 1, 50), (check.State, check.Recorded, check.NotRecorded, check.Failed));
        Assert.All(check.Stops.Skip(100), stop => Assert.Equal((HeatmapFinds.Failed, (string?)"Studio could not check this system."), (stop.State, stop.Reason)));
        Assert.Equal("Stop 6", Assert.Single(finds.Finds).Name);

        net.Then(Named([("Stop 120", StopId(120))]));
        game.SetRoute([.. Route(0), Stop(120), Stop(5)]);
        game.RaiseChanged();
        await finds.Idle;
        Assert.Equal(NamesAsked(["Stop 120"]), net.Asked[^1]);
        Assert.Equal([(HeatmapFinds.Recorded, "found by Spansh's system search"), (HeatmapFinds.Recorded, "checked earlier: found by Spansh's system search")],
            finds.RouteCheckNow().Stops.Select(stop => (stop.State, stop.How)));
    }

    // ------------------------------------------------------------ the memory of checks, which every check shares

    [Fact]
    public async Task A_system_recorded_by_the_route_check_is_not_asked_of_Spansh_again_by_another_path()
    {
        HeatmapFinds finds = Start();
        game.RaiseChanged();   // Studio's start: no route
        await finds.Idle;

        // A route checked: Spansh has its three stops, and the memory keeps what it said, with how.
        net.Then(AllOf(1, 3));
        game.SetRoute(Route(3));
        game.RaiseChanged();
        await finds.Idle;
        Assert.Equal([NamesAsked(Stops(1, 3))], net.Asked);
        CheckEntry kept = finds.Memory.Get("Stop 2", StopId(2))!;
        Assert.Equal((CheckMemory.Recorded, CheckMemory.BySpansh, "found by Spansh's system search"), (kept.Result, kept.By, kept.How));
        Assert.Contains($"\"id64\":\"{StopId(2)}\"", File.ReadAllText(Path.Combine(temp, CheckMemory.FileName)), StringComparison.Ordinal);

        // The player then targets one of them: the target check takes the memory's answer, and asks nobody.
        Line($$"""{ "timestamp":"2026-10-15T14:00:00Z", "event":"FSDTarget", "Name":"Stop 2", "SystemAddress":{{StopId(2)}}, "StarClass":"M" }""");
        game.RaiseChanged();
        await finds.Idle;
        Assert.Null(await finds.Check("STOP 2", StopId(2)));
        Assert.Single(net.Asked);

        // Nor after Studio is started again; and its marker says it is recorded.
        HeatmapFinds next = Start();
        Assert.Null(await next.Check("Stop 2", StopId(2)));
        Assert.Equal(new MapMark("Stop 2", StopId(2), 2, 0, -2) { Recorded = true }, await next.Place(new GameNamed("Stop 2", StopId(2), [2, 0, -2])));
        Assert.Single(net.Asked);

        // A day on (a recorded system is remembered for a day unless the player chose longer), it is checked again.
        clock += TimeSpan.FromDays(1);
        net.Then(Found("Stop 2", StopId(2), 2, 0, -2));
        Assert.Equal(HeatmapFinds.Recorded, (await next.Check("Stop 2", StopId(2)))!.State);
        Assert.Equal(Asked("Stop 2"), net.Asked[^1]);
    }

    [Fact]
    public async Task A_target_checked_first_settles_that_stop_of_a_later_route_from_the_memory()
    {
        HeatmapFinds finds = Start();
        game.RaiseChanged();
        await finds.Idle;

        net.Then(NoSuchName);
        Assert.Equal(HeatmapFinds.NotRecorded, (await finds.Check("Stop 2", StopId(2)))!.State);

        // A route through it: only the other two are asked about; it is not recorded, from the memory, and not kept twice.
        net.Then(Named([("Stop 1", StopId(1)), ("Stop 3", StopId(3))]));
        game.SetRoute(Route(3));
        game.RaiseChanged();
        await finds.Idle;
        Assert.Equal([Asked("Stop 2"), NamesAsked(["Stop 1", "Stop 3"])], net.Asked);
        Assert.Equal((HeatmapFinds.NotRecorded, (string?)"checked earlier: not found by Spansh's name search"),
            (StopNamed(finds.RouteCheckNow(), "Stop 2").State, StopNamed(finds.RouteCheckNow(), "Stop 2").How));
        Assert.Equal("Stop 2", Assert.Single(finds.Finds).Name);
    }

    [Fact]
    public async Task The_route_check_and_Nav_scan_at_once_on_crossing_orders_never_wait_on_each_other()
    {
        HeatmapFinds finds = Start();
        game.RaiseChanged();   // Studio's start: no route
        await finds.Idle;
        // Spansh has the three stops: each check's request is answered so, whichever goes first.
        net.Then(AllOf(1, 3)).Then(AllOf(1, 3));

        // The two checks are made to cross. A stop is taken by a check just before it is looked for on this PC, so: the
        // route takes Stop 1 and waits there until Nav scan has taken Stop 3, which waits until the route has taken Stop 2.
        // Then Nav scan comes to Stop 2 and the route to Stop 3, each held by the other.
        var threeTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        finds.Searching = name =>
        {
            if (name == "Stop 1") threeTaken.Task.Wait(TimeSpan.FromSeconds(10));
            else if (name == "Stop 3")
            {
                threeTaken.TrySetResult();
                twoTaken.Task.Wait(TimeSpan.FromSeconds(10));
            }
            else if (name == "Stop 2") twoTaken.TrySetResult();
        };
        game.SetRoute(Route(3));
        game.RaiseChanged();
        // The Navigation panel's rows, nearest first: the route's stops the other way round.
        ScanEntry[] rows = [.. new[] { 3, 2, 1 }.Select(i => new ScanEntry([new ScanVariant($"Stop {i}", StopId(i), i, 0, -i, null)]))];
        Task<ScanTally> scanning = finds.CheckScanned(rows, "panel.jpg", stopping: TestContext.Current.CancellationToken);

        // Both end: each asks about the stops it took, gives them up, and only then waits for those the other took, which
        // it settles from what the other found. (Before, each waited for the other's for good.)
        ScanTally tally = await scanning.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await finds.Idle.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(2, net.Asked.Count);
        Assert.Contains(NamesAsked(Stops(1, 2)), net.Asked);
        Assert.Contains(NamesAsked(["Stop 3"]), net.Asked);
        RouteCheckView check = finds.RouteCheckNow();
        Assert.Equal((HeatmapFinds.Done, 3, 0, 1), (check.State, check.Recorded, check.Failed, check.Requests));
        Assert.Equal(["found by Spansh's system search", "found by Spansh's system search", "checked earlier: found by Spansh's system search"], check.Stops.Select(stop => stop.How));
        Assert.Equal((3, 3, 0, 1), (tally.Entries, tally.Recorded, tally.Failed, tally.Requests));
        Assert.Equal(["found by Spansh's system search", "checked earlier: found by Spansh's system search", "checked earlier: found by Spansh's system search"], tally.Names.Select(name => name.How));
        Assert.Equal(2, survey!.Used);
    }

    [Fact]
    public async Task A_route_check_waiting_for_a_targets_check_stops_waiting_as_soon_as_a_new_route_comes()
    {
        var held = new Held(net);
        HeatmapFinds finds = Start(handler: held);
        game.RaiseChanged();
        await finds.Idle;

        // The game's target, Stop 2, is being checked: its lookup is held on its way to Spansh (a slow answer, say).
        Task<TargetNews?> target = finds.Check("Stop 2", StopId(2));
        await held.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // A route through it: its check takes Stop 1, then comes to Stop 2 and waits for the target's answer.
        var looked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        finds.Searching = name =>
        {
            if (name == "Stop 1") looked.TrySetResult();
        };
        game.SetRoute(Route(3));
        game.RaiseChanged();
        await looked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // A new route: the old check stops waiting at once (before, it waited for the target's answer however long that
        // took), so the new route's check begins at once, its stop taken to ask Spansh about while the answer is still held.
        game.SetRoute([.. Route(0), Stop(7)]);
        game.RaiseChanged();
        await Until(() => StopNamed(finds.RouteCheckNow(), "Stop 7").State == HeatmapFinds.Checking);
        Assert.Empty(net.Asked);

        net.Then(NoSuchName).Then(AllOf(7, 7));
        held.LetGo.SetResult();
        Assert.Equal(HeatmapFinds.NotRecorded, (await target)!.State);
        await finds.Idle.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal([Asked("Stop 2"), NamesAsked(["Stop 7"])], net.Asked);
        Assert.Equal((HeatmapFinds.Done, 1, 1), (finds.RouteCheckNow().State, finds.RouteCheckNow().Total, finds.RouteCheckNow().Recorded));
    }

    [Fact]
    public void How_long_a_recorded_system_is_remembered_is_kept_in_settings_beside_the_rest_of_the_3D_maps()
    {
        // Within reach's settings, kept by an earlier session: nothing here writes over them.
        new Settings(SettingsFile).Set("heatmap", new HeatmapSettings { WithinReach = false, ReachLy = 42 });
        HeatmapFinds finds = Start();
        Assert.Equal(1, finds.RecordedDays);   // the owner's choice of 15 October 2026

        finds.RecordedDays = 7;
        finds.CheckTargets = false;

        Assert.Equal(7, finds.Memory.RecordedDays);
        JsonNode saved = JsonNode.Parse(File.ReadAllText(SettingsFile))!["heatmap"]!;
        Assert.Equal((7, false, false, 42.0, "all"),
            ((int)saved["recordedDays"]!, (bool)saved["checkTargets"]!, (bool)saved["withinReach"]!, (double)saved["reachLy"]!, (string)saved["routeCheck"]!));
        Assert.Equal(7, Start().RecordedDays);
        ArgumentException wrong = Assert.Throws<ArgumentException>(() => finds.RecordedDays = 2);
        Assert.StartsWith(CheckMemory.NoSuchDays, wrong.Message, StringComparison.Ordinal);
        Assert.Equal(7, finds.RecordedDays);

        // A number the page does not offer, typed into the file by hand, is taken for the default.
        new Settings(SettingsFile).Set("heatmap", new HeatmapSettings { RecordedDays = 3 });
        Assert.Equal(1, Start().RecordedDays);
    }
}
