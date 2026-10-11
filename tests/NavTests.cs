using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Nav;
using WebRequest = EliteStudio.App.WebRequest;     // System.Net has older types with the same names
using WebResponse = EliteStudio.App.WebResponse;

namespace EliteStudio.Tests;

/// <summary>
/// The route being followed, and the /api/nav/ routes. A practice journal is flown along the two
/// routes recorded from Spansh (tests\Fixtures\nav): jump lines are appended and the watcher is
/// polled, exactly as the app does while the game runs. The ship is the owner's engineered
/// Mandalay from the "Engineered" recording, parked in Sol, where both recorded routes start.
/// </summary>
public sealed class NavTests : IDisposable
{
    static readonly string NeutronResult = SpanshStandIn.Recorded("neutron-result.json");
    static readonly string ExactResult = SpanshStandIn.Recorded("exact-result.json");
    static readonly IReadOnlyDictionary<string, string> NoQuery = new Dictionary<string, string>();

    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioNav-").FullName;
    readonly PracticeGame game = new();
    readonly GameWatcher watcher;
    readonly string journal;

    public NavTests()
    {
        string[] engineered = PracticeGame.Recording("Journal.2026-10-08T080236.01.log");
        journal = game.Journal("Journal.2026-10-09T030000.01.log", [PracticeGame.Line(engineered, "Loadout"), Arrival("Location", "Sol", 0, 0, 0, SolAddress)]);
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

    /// <summary>Spansh taking each request on and having its recorded result ready at the first look.</summary>
    static SpanshStandIn Answering(params string[] results)
    {
        var net = new SpanshStandIn();
        foreach (string result in results) net.Then(SpanshStandIn.Accepted(SpanshStandIn.Job(result)), HttpStatusCode.Accepted).Then(result);
        return net;
    }

    const long SolAddress = 10477373803;   // the game's own number for Sol, and Spansh's

    /// <summary>A journal line that puts the ship in a system. The game always adds the system's number; 0 leaves it out.</summary>
    static string Arrival(string eventName, string system, double x, double y, double z, long address = 0)
    {
        string number = address == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"\"SystemAddress\":{address}, ");
        return string.Create(CultureInfo.InvariantCulture,
            $$"""{ "timestamp":"2026-10-09T03:00:00Z", "event":"{{eventName}}", "StarSystem":"{{system}}", {{number}}"StarPos":[{{x}},{{y}},{{z}}] }""");
    }

    void Jump(string system, double x, double y, double z, long address = 0)
    {
        game.Append(journal, Arrival("FSDJump", system, x, y, z, address));
        watcher.Poll();
    }

    void JumpTo(RouteHop stop) => Jump(stop.System, stop.X, stop.Y, stop.Z, stop.Address);

    // ------------------------------------------------------------ progress along a route

    [Fact]
    public async Task Progress_follows_the_journal_stop_by_stop_through_detours_skips_and_turning_back()
    {
        using Spansh spansh = NavSpanshTests.Quick(Answering(NeutronResult));
        var nav = new NavRoutes(watcher.State, SettingsFile(), spansh);
        var told = new List<string>();   // what each Changed event had to say
        nav.Changed += () => told.Add(nav.Plotting is not null ? "asking Spansh" : nav.Progress?.Next ?? "arrived");

        await nav.Plot(NavSpanshTests.NeutronRequest);
        IReadOnlyList<RouteHop> stops = nav.Route!.Hops;

        // In Sol, the route's start: the first leg, with the jumps Spansh reckons for it. 24 stops,
        // 63 jumps and 5,303.6 ly in all. (The same numbers the real program gave on the day of the recording.)
        Assert.Equal(new NavProgress(0, 0, "PSR J1752-2806", 407.5, 9, false, 23, 63, 5303.6), nav.Progress);
        Assert.Equal("PSR J1752-2806", nav.Progress?.Next);

        // Under way on that leg, in a system that is no stop: the last stop stands, and the
        // distances are measured from the ship. No route plotted in the game: Studio's own estimate of the jumps.
        Jump("Between The Stops", stops[1].X + 48, stops[1].Y + 64, stops[1].Z);
        Assert.Equal(new NavProgress(0, -1, "PSR J1752-2806", 80, 2, false, 23, 56, 4976.1), nav.Progress);

        // The player pastes the name into the galaxy map and plots: now the game's own count is used.
        game.Write(Path.Combine(game.JournalDir, "NavRoute.json"), $$"""
            { "timestamp":"2026-10-09T03:00:00Z", "event":"NavRoute", "Route":[
            { "StarSystem":"Between The Stops", "SystemAddress":1, "StarPos":[19.03125,33.15625,407.28125], "StarClass":"K" },
            { "StarSystem":"Half Way", "SystemAddress":2, "StarPos":[4.0,13.0,407.0], "StarClass":"M" },
            { "StarSystem":"{{stops[1].System}}", "SystemAddress":{{stops[1].Address}}, "StarPos":[-10.96875,-6.84375,407.28125], "StarClass":"N" }
            ] }
            """);
        game.Append(journal, """{ "timestamp":"2026-10-09T03:00:00Z", "event":"FSDTarget", "Name":"Half Way", "SystemAddress":2, "StarClass":"M", "RemainingJumpsInRoute":3 }""");
        watcher.Poll();
        Assert.Equal(new NavProgress(0, -1, "PSR J1752-2806", 80, 3, true, 23, 57, 4976.1), nav.Progress);

        // Reaching a stop makes the one after it the next.
        JumpTo(stops[1]);
        Assert.Equal(new NavProgress(1, 1, "Col 359 Sector NN-T e3-3", 534.4, 9, false, 22, 54, 4896.1), nav.Progress);

        // Skipping a stop: arriving at any later one counts.
        JumpTo(stops[3]);
        Assert.Equal(new NavProgress(3, 3, "Col 359 Sector RZ-F d11-21", 190.6, 1, false, 20, 44, 4220.8), nav.Progress);

        // Turning back to an earlier stop counts as well.
        JumpTo(stops[2]);
        Assert.Equal(new NavProgress(2, 2, "B133 Sector DB-X d1-30", 140.9, 1, false, 21, 45, 4361.7), nav.Progress);

        // Leaving the route altogether changes nothing but the distances and the estimate that goes with them.
        Jump("Far Off The Route", stops[3].X, stops[3].Y + 1000, stops[3].Z);
        Assert.Equal(new NavProgress(2, -1, "B133 Sector DB-X d1-30", 1000, 21, false, 21, 65, 5220.8), nav.Progress);

        int before = told.Count;
        game.Append(journal, """{ "timestamp":"2026-10-09T03:00:00Z", "event":"Music", "MusicTrack":"Exploration" }""");
        watcher.Poll();
        Assert.Equal(before, told.Count);   // the game state changed, but nothing Studio Nav shows did

        // The last stop: arrived.
        JumpTo(stops[^1]);
        Assert.Equal(new NavProgress(stops.Count - 1, stops.Count - 1, null, 0, 0, false, 0, 0, 0), nav.Progress);
        Assert.Null(nav.Progress?.Next);

        Assert.Equal(new[]
        {
            "asking Spansh", "PSR J1752-2806", "PSR J1752-2806", "PSR J1752-2806", "Col 359 Sector NN-T e3-3",
            "Col 359 Sector RZ-F d11-21", "B133 Sector DB-X d1-30", "B133 Sector DB-X d1-30", "arrived",
        }, told);
    }

    [Fact]
    public async Task A_stop_is_told_by_the_systems_number_when_Spansh_and_the_game_both_give_one()
    {
        using Spansh spansh = NavSpanshTests.Quick(Answering(NeutronResult));
        var nav = new NavRoutes(watcher.State, SettingsFile(), spansh);
        await nav.Plot(NavSpanshTests.NeutronRequest);
        IReadOnlyList<RouteHop> stops = nav.Route!.Hops;
        Assert.Equal((SolAddress, 22712681061), (stops[0].Address, stops[1].Address));   // Spansh's id64 is the game's SystemAddress

        // The game spells a name another way than Spansh does: by its number it is the stop all the same,
        // and with the ship at the start of the next leg, the jumps for that leg are Spansh's.
        Jump("PSR J1752 2806", stops[1].X, stops[1].Y, stops[1].Z, stops[1].Address);
        Assert.Equal((1, stops[2].System, stops[2].Jumps), (nav.Progress!.At, nav.Progress.Next, nav.Progress.NextJumps));

        // The number counts for more than the name: another system that goes by a stop's name is not that stop.
        Jump(stops[3].System, stops[3].X, stops[3].Y, stops[3].Z + 30, address: 42);
        Assert.Equal((1, stops[2].System), (nav.Progress.At, nav.Progress.Next));

        // The same goes for the route plotted in the galaxy map: it ends at the next stop by number, whatever it is called there.
        game.Write(Path.Combine(game.JournalDir, "NavRoute.json"), $$"""
            { "timestamp":"2026-10-09T03:00:00Z", "event":"NavRoute", "Route":[
            { "StarSystem":"{{stops[3].System}}", "SystemAddress":42, "StarPos":[0,0,0], "StarClass":"K" },
            { "StarSystem":"Col 359 NN-T e3-3", "SystemAddress":{{stops[2].Address}}, "StarPos":[0,0,0], "StarClass":"N" }
            ] }
            """);
        game.Append(journal, """{ "timestamp":"2026-10-09T03:00:00Z", "event":"FSDTarget", "Name":"Col 359 NN-T e3-3", "SystemAddress":14183040220, "StarClass":"N", "RemainingJumpsInRoute":7 }""");
        watcher.Poll();
        Assert.Equal((true, 7), (nav.Progress.NextPlotted, nav.Progress.NextJumps));

        // A line without a number (none from the real game, but a route saved by an earlier Studio has none either): the name decides.
        Jump(stops[3].System.ToUpperInvariant(), stops[3].X, stops[3].Y, stops[3].Z);
        Assert.Equal((3, stops[4].System), (nav.Progress.At, nav.Progress.Next));
    }

    [Fact]
    public async Task On_an_exact_route_every_stop_is_one_jump()
    {
        using Spansh spansh = NavSpanshTests.Quick(Answering(ExactResult));
        var nav = new NavRoutes(watcher.State, SettingsFile(), spansh);

        await nav.Plot(NavSpanshTests.ExactRequest);
        IReadOnlyList<RouteHop> stops = nav.Route!.Hops;

        for (int at = 0; at < stops.Count - 1; at++)
        {
            Assert.Equal((at, stops[at + 1].System, 1, stops.Count - 1 - at, stops.Count - 1 - at),
                (nav.Progress!.At, nav.Progress.Next, nav.Progress.NextJumps, nav.Progress.HopsLeft, nav.Progress.JumpsLeft));
            Assert.Equal(Math.Round(stops[at + 1].Distance, 1), nav.Progress.NextLy);
            JumpTo(stops[at + 1]);
        }
        Assert.Null(nav.Progress?.Next);
    }

    [Fact]
    public async Task A_route_plotted_from_somewhere_else_starts_by_sending_the_player_to_its_first_stop()
    {
        Jump("Sirius", 6.25, -1.28125, -5.75);
        using Spansh spansh = NavSpanshTests.Quick(Answering(NeutronResult));
        var nav = new NavRoutes(watcher.State, SettingsFile(), spansh);

        await nav.Plot(NavSpanshTests.NeutronRequest);

        // 8.6 ly to Sol: one jump for this ship, then the whole route.
        Assert.Equal(new NavProgress(-1, -1, "Sol", 8.6, 1, false, 24, 64, 5312.2), nav.Progress);
    }

    // ------------------------------------------------------------ the route survives a restart

    [Fact]
    public async Task The_route_and_the_place_on_it_survive_a_restart()
    {
        using Spansh spansh = NavSpanshTests.Quick(Answering(NeutronResult));
        var nav = new NavRoutes(watcher.State, SettingsFile(), spansh);
        await nav.Plot(NavSpanshTests.NeutronRequest);
        IReadOnlyList<RouteHop> stops = nav.Route!.Hops;
        JumpTo(stops[2]);
        Jump("Between The Stops", stops[2].X, stops[2].Y, stops[2].Z + 20);

        // Studio starts again: the saved route is there before the journal has been read...
        using GameWatcher restarted = game.Watch();
        using var offline = new Spansh(new SpanshStandIn());
        var again = new NavRoutes(restarted.State, SettingsFile(), offline);
        Assert.Equal(nav.Route.Request, again.Route!.Request);
        Assert.Equal((nav.Route.Job, nav.Route.PlottedUtc), (again.Route.Job, again.Route.PlottedUtc));
        Assert.Equal(stops, again.Route.Hops);
        Assert.Equal((2, stops[3].System), (again.Progress!.At, again.Progress.Next));

        // ...and once it has, the progress is the same as before, although the player sits between two stops.
        restarted.Poll();
        Assert.Equal(nav.Progress, again.Progress);

        again.Clear();
        Assert.Null(again.Route);
        Assert.Null(again.Progress);
        Assert.Null(new NavRoutes(restarted.State, SettingsFile(), offline).Route);
    }

    // A route of two stops as Studio saved it before stops carried the system's number, so it also shows that such a
    // route still loads and is followed by name. Written with ' for " so that the tests below can name parts of it.
    const string SavedRoute =
        "{ 'request': { 'kind': 'neutron', 'from': 'Sol', 'to': 'Sirius', 'range': 50, 'efficiency': 60, 'supercharge': 4, 'ship': null, 'cargo': 0 },"
        + " 'job': 'J', 'plottedUtc': '2026-10-09T03:00:00Z', 'hops': ["
        + " { 'system': 'Sol', 'x': 0, 'y': 0, 'z': 0, 'distance': 0, 'distanceLeft': 8.6, 'jumps': 0, 'neutron': false, 'refuel': false, 'fuel': 0 },"
        + " { 'system': 'Sirius', 'x': 6.25, 'y': -1.28125, 'z': -5.75, 'distance': 8.6, 'distanceLeft': 0, 'jumps': 1, 'neutron': false, 'refuel': false, 'fuel': 0 } ] }";

    void SaveAsRoute(string route) =>
        File.WriteAllText(Path.Combine(temp, "settings.json"), ("{ 'nav': { 'route': " + route + ", 'reached': 0 } }").Replace('\'', '"'));

    [Fact]
    public void A_saved_route_that_cannot_be_read_is_left_out_instead_of_stopping_the_start()
    {
        SaveAsRoute("'not a route'");
        using var offline = new Spansh(new SpanshStandIn());

        var nav = new NavRoutes(watcher.State, SettingsFile(), offline);

        Assert.Equal(new NavView(null, null, null, null), nav.View);
    }

    [Theory]
    [InlineData("'system': 'Sirius',", "")]                // a stop without its name
    [InlineData("'system': 'Sirius'", "'system': ''")]
    [InlineData("'hops': [", "'hops': [ null,")]           // a stop that is nothing at all
    [InlineData("'hops': [", "'stops': [")]                // no stops
    [InlineData("'kind': 'neutron'", "'kind': 'scenic'")]
    [InlineData("'request': {", "'asked': {")]             // no request
    [InlineData("'job': 'J',", "")]
    public void A_saved_route_that_can_be_read_but_has_a_part_missing_is_left_out_as_well(string part, string broken)
    {
        using var offline = new Spansh(new SpanshStandIn());
        // As Studio wrote it, the route comes back: so it is the one part broken below that keeps it out.
        SaveAsRoute(SavedRoute);
        Assert.Equal("Sirius", new NavRoutes(watcher.State, SettingsFile(), offline).Progress?.Next);
        Assert.Contains(part, SavedRoute);
        SaveAsRoute(SavedRoute.Replace(part, broken));

        var nav = new NavRoutes(watcher.State, SettingsFile(), offline);

        Assert.Equal(new NavView(null, null, null, null), nav.View);
    }

    [Fact]
    public void On_a_route_that_passes_one_system_twice_the_ship_stays_on_the_visit_it_is_making()
    {
        // Sol, Sirius, Sol again, Alpha Centauri, with the ship in Sol at the start.
        SaveAsRoute(SavedRoute.Replace(" ] }",
            ", { 'system': 'Sol', 'x': 0, 'y': 0, 'z': 0, 'distance': 8.6, 'distanceLeft': 4.4, 'jumps': 1, 'neutron': false, 'refuel': false, 'fuel': 0 },"
            + " { 'system': 'Alpha Centauri', 'x': 3.03125, 'y': -0.09375, 'z': 3.15625, 'distance': 4.4, 'distanceLeft': 0, 'jumps': 1, 'neutron': false, 'refuel': false, 'fuel': 0 } ] }"));
        using var offline = new Spansh(new SpanshStandIn());
        var nav = new NavRoutes(watcher.State, SettingsFile(), offline);
        int told = 0;
        nav.Changed += () => told++;
        (int At, int Here, string? Next) Place() => (nav.Progress!.At, nav.Progress.Here, nav.Progress.Next);
        void SomethingElseHappens()
        {
            game.Append(journal, """{ "timestamp":"2026-10-09T03:00:00Z", "event":"Music", "MusicTrack":"Exploration" }""");
            watcher.Poll();
        }

        // The first visit stands whatever else happens in the game: it is not taken for the second, nor the second for the first.
        Assert.Equal((0, 0, "Sirius"), Place());
        SomethingElseHappens();
        SomethingElseHappens();
        Assert.Equal(((0, 0, "Sirius"), 0), (Place(), told));

        Jump("Sirius", 6.25, -1.28125, -5.75);
        Assert.Equal((1, 1, "Sol"), Place());
        Jump("Sol", 0, 0, 0);
        Assert.Equal((2, 2, "Alpha Centauri"), Place());
        SomethingElseHappens();
        SomethingElseHappens();
        Assert.Equal(((2, 2, "Alpha Centauri"), 2), (Place(), told));   // told of the two arrivals and of nothing else
        Assert.Equal(2, SettingsFile().Get<NavSettings>("nav").Reached);
    }

    // ------------------------------------------------------------ requests that fail, are cancelled, or are replaced

    [Fact]
    public async Task A_request_that_fails_keeps_the_current_route_and_says_why_until_the_next_request()
    {
        var net = Answering(NeutronResult).Then("""{"error":"Could not find finishing system"}""", HttpStatusCode.BadRequest);
        using Spansh spansh = NavSpanshTests.Quick(net);
        var nav = new NavRoutes(watcher.State, SettingsFile(), spansh);
        await nav.Plot(NavSpanshTests.NeutronRequest);
        NavRoute current = nav.Route!;

        await nav.Plot(NavSpanshTests.NeutronRequest with { To = "Lagoon Sector NI-S b4-1O" });

        Assert.Same(current, nav.Route);
        Assert.Null(nav.Plotting);
        Assert.Equal("Spansh does not know the system typed under To. Check its spelling, or pick a name from the list.", nav.Problem);

        await nav.Plot(NavSpanshTests.NeutronRequest);   // kept from before, so Spansh is not asked again
        Assert.Null(nav.Problem);
        Assert.Equal(3, net.Asked.Count);
    }

    [Fact]
    public async Task Cancel_stops_the_request_under_way_and_a_new_request_replaces_it()
    {
        // The first two requests are left waiting for their first look; the third gets its result at once.
        string job = SpanshStandIn.Job(ExactResult);
        var net = new SpanshStandIn().Then(SpanshStandIn.Accepted("NEVER")).Then(SpanshStandIn.Accepted("NEVER")).Then(SpanshStandIn.Accepted(job)).Then(ExactResult);
        bool hold = true;
        using var takenOn = new SemaphoreSlim(0);   // one count for each request Spansh has taken on and whose first look is waited for
        using var spansh = new Spansh(net)
        {
            Wait = (_, stop) =>
            {
                takenOn.Release();
                return hold ? Task.Delay(Timeout.Infinite, stop) : Task.CompletedTask;
            },
        };
        var nav = new NavRoutes(watcher.State, SettingsFile(), spansh);
        var states = new List<string>();
        nav.Changed += () => states.Add(nav.Plotting?.Request.To ?? (nav.Route is null ? "nothing" : "route"));

        Task cancelled = nav.Plot(NavSpanshTests.NeutronRequest with { To = "First" });
        Assert.Equal(("First", "queued"), (nav.Plotting!.Request.To, nav.Plotting.State));
        await takenOn.WaitAsync(TestContext.Current.CancellationToken);
        nav.Cancel();
        await cancelled;
        Assert.Equal(new NavView(null, null, null, null), nav.View);

        Task replaced = nav.Plot(NavSpanshTests.NeutronRequest with { To = "Second" });
        await takenOn.WaitAsync(TestContext.Current.CancellationToken);
        hold = false;
        await nav.Plot(NavSpanshTests.ExactRequest);
        await replaced;

        Assert.Equal(NavSpanshTests.ExactRequest, nav.Route!.Request);
        Assert.Null(nav.Plotting);
        Assert.Null(nav.Problem);
        Assert.Equal(new[] { "First", "nothing", "Second", "PSR J1752-2806", "route" }, states);
        Assert.Equal(4, net.Asked.Count);   // three requests and one look: the two abandoned jobs were never looked for
    }

    // ------------------------------------------------------------ the /api/nav/ routes

    static async Task<(int Status, JsonElement Json)> Call(WebServer web, string method, string path, string body = "", string? q = null)
    {
        IReadOnlyDictionary<string, string> query = q is null ? NoQuery : new Dictionary<string, string> { ["q"] = q };
        WebResponse answer = await web.Handle(new WebRequest(method, path, query, body));
        return (answer.Status, JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(answer.Body)));
    }

    static async Task Finished(NavRoutes nav)
    {
        for (int i = 0; nav.Plotting is not null; i++)
        {
            Assert.True(i < 500, "the request never finished");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Routes_plot_show_and_clear_a_route()
    {
        var net = Answering(NeutronResult, ExactResult);
        using Spansh spansh = NavSpanshTests.Quick(net);
        using var web = new WebServer(temp);
        NavRoutes nav = NavApi.Map(web, watcher.State, SettingsFile(), spansh);

        var (status, view) = await Call(web, "GET", "/api/nav/route");
        Assert.Equal(200, status);
        Assert.Equal("""{"route":null,"progress":null,"plotting":null,"problem":null}""", JsonSerializer.Serialize(view));

        // Names are tidied, the numbers are brought into range, and what only the game knows is added:
        // the Mandalay's drive is an ordinary one, so a neutron star stretches a jump four times.
        (status, _) = await Call(web, "POST", "/api/nav/plot", """{"kind":"neutron","from":" Sol ","to":"Lagoon Sector NI-S b4-10 ","range":50.9004,"efficiency":250}""");
        Assert.Equal(200, status);
        await Finished(nav);
        Assert.Equal("POST /api/route from=Sol&to=Lagoon+Sector+NI-S+b4-10&range=50.9&efficiency=100&supercharge_multiplier=4", net.Asked[0]);

        (_, view) = await Call(web, "GET", "/api/nav/route");
        JsonElement route = view.GetProperty("route");
        Assert.Equal("88D87E68-C3AB-11F1-B135-C0BBE25E9E71", route.GetProperty("job").GetString());
        Assert.Equal("neutron", route.GetProperty("request").GetProperty("kind").GetString());
        Assert.Equal(24, route.GetProperty("hops").GetArrayLength());
        Assert.Equal(
            """{"system":"PSR J1752-2806","address":"22712681061","x":-10.96875,"y":-6.84375,"z":407.28125,"distance":407.486400990496,"distanceLeft":4075.43668025472,"jumps":9,"neutron":true,"refuel":false,"fuel":0,"note":""}""",
            JsonSerializer.Serialize(route.GetProperty("hops")[1]));
        Assert.Equal(
            """{"at":0,"here":0,"next":"PSR J1752-2806","nextLy":407.5,"nextJumps":9,"nextPlotted":false,"hopsLeft":23,"jumpsLeft":63,"lyLeft":5303.6,"nextNote":"","hereNote":""}""",
            JsonSerializer.Serialize(view.GetProperty("progress")));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("plotting").ValueKind);

        // The exact plotter is sent this ship, and the 16 tons Cargo.json says are on board.
        game.Write(Path.Combine(game.JournalDir, "Cargo.json"), """{ "timestamp":"2026-10-09T03:00:00Z", "event":"Cargo", "Vessel":"Ship", "Count":16, "Inventory":[ { "Name":"gold", "Count":16, "Stolen":0 } ] }""");
        watcher.Poll();
        (status, _) = await Call(web, "POST", "/api/nav/plot", """{"kind":"exact","from":"Sol","to":"PSR J1752-2806"}""");
        Assert.Equal(200, status);
        await Finished(nav);
        Assert.Equal(NavSpanshTests.ExactRequest with { Cargo = 16 }, nav.Route!.Request);
        Assert.Contains("&optimal_mass=1586.25&base_mass=389.100006&", net.Asked[2]);
        Assert.Contains("&cargo=16&", net.Asked[2]);

        (status, view) = await Call(web, "POST", "/api/nav/clear", "{}");
        Assert.Equal((200, JsonValueKind.Null), (status, view.GetProperty("route").ValueKind));
        Assert.Null(nav.Route);
    }

    [Fact]
    public async Task An_exact_route_plotted_on_a_supercharged_drive_tells_Spansh_so()
    {
        const string Plot = """{"kind":"exact","from":"Sol","to":"PSR J1752-2806"}""";
        var net = Answering(ExactResult, ExactResult);
        using Spansh spansh = NavSpanshTests.Quick(net);
        using var web = new WebServer(temp);
        NavRoutes nav = NavApi.Map(web, watcher.State, SettingsFile(), spansh);

        // The ship has flown through a neutron star's jet, as the journal tells it.
        game.Append(journal, """{ "timestamp":"2026-10-09T03:05:00Z", "event":"JetConeBoost", "BoostValue":4.000000 }""");
        watcher.Poll();
        await Call(web, "POST", "/api/nav/plot", Plot);
        await Finished(nav);
        Assert.Contains("&is_supercharged=1&", net.Asked[0]);
        Assert.True(nav.Route!.Request.Ship!.IsSupercharged);

        // The next jump spends the charge. Asked again, the same route is a new request, and says so.
        JumpTo(nav.Route.Hops[1]);
        await Call(web, "POST", "/api/nav/plot", Plot);
        await Finished(nav);
        Assert.Contains("&is_supercharged=0&", net.Asked[2]);
        Assert.Equal(NavSpanshTests.ExactRequest, nav.Route!.Request);
    }

    [Fact]
    public async Task A_supercharge_counts_only_for_a_route_that_starts_where_the_ship_is()
    {
        var net = Answering(ExactResult, ExactResult);
        using Spansh spansh = NavSpanshTests.Quick(net);
        using var web = new WebServer(temp);
        NavRoutes nav = NavApi.Map(web, watcher.State, SettingsFile(), spansh);
        game.Append(journal, """{ "timestamp":"2026-10-09T03:05:00Z", "event":"JetConeBoost", "BoostValue":4.000000 }""");
        watcher.Poll();

        // The ship sits charged in Sol and the player types another start. The jump to Sirius
        // spends the charge, so Spansh must not plan the first jump out of Sirius on it.
        await Call(web, "POST", "/api/nav/plot", """{"kind":"exact","from":"Sirius","to":"PSR J1752-2806"}""");
        await Finished(nav);
        Assert.StartsWith("POST /api/generic/route source=Sirius&destination=PSR+J1752-2806&is_supercharged=0&", net.Asked[0]);
        Assert.False(nav.Route!.Request.Ship!.IsSupercharged);   // what the page is shown and what is saved say the same
        Assert.True(watcher.State.Supercharged);                 // the drive itself still holds the charge

        // The ship's own system typed by hand, in the player's own capitals, is still where the ship is.
        await Call(web, "POST", "/api/nav/plot", """{"kind":"exact","from":"sol","to":"PSR J1752-2806"}""");
        await Finished(nav);
        Assert.StartsWith("POST /api/generic/route source=sol&destination=PSR+J1752-2806&is_supercharged=1&", net.Asked[2]);
    }

    // ------------------------------------------------------------ a trip another studio hands over

    [Fact]
    public async Task A_trip_handed_over_becomes_the_route_and_is_followed_with_its_notes_across_a_restart()
    {
        // Spansh still has a request from before to work on when the trip comes.
        var net = new SpanshStandIn().Then(SpanshStandIn.Accepted(SpanshStandIn.Job(NeutronResult))).Then(NeutronResult);
        bool hold = true;
        using var takenOn = new SemaphoreSlim(0);
        using var spansh = new Spansh(net)
        {
            Wait = (_, stop) =>
            {
                takenOn.Release();
                return hold ? Task.Delay(Timeout.Infinite, stop) : Task.CompletedTask;
            },
        };
        using var web = new WebServer(temp);
        NavRoutes nav = NavApi.Map(web, watcher.State, SettingsFile(), spansh);
        Task plotting = nav.Plot(NavSpanshTests.NeutronRequest);
        await takenOn.WaitAsync(TestContext.Current.CancellationToken);

        // Four stops and two that are none. The first is where the ship is, so the route starts with it; the two in
        // Alpha Centauri are one stop of the route (the second has no number of its own and is told by its name).
        var (status, view) = await Call(web, "POST", "/api/nav/trip", """
            { "name": " Shopping for Explorer ", "stops": [
              { "system": "Sol", "address": "10477373803", "x": 0, "y": 0, "z": 0, "note": "Dock at Abraham Lincoln and buy 5A Frame Shift Drive." },
              { "system": "Alpha Centauri", "address": "1178708478315", "x": 3.03125, "y": -0.09375, "z": 3.15625, "note": "Dock at Hutton Orbital and buy 2 × A Shield Booster." },
              { "system": " alpha centauri ", "x": 3.03125, "y": -0.09375, "z": 3.15625, "note": "Dock at al-Din Prospect and buy 4E Fuel Scoop." },
              null, { "system": " " },
              { "system": "Sirius", "x": 6.25, "y": -1.28125, "z": -5.75 } ] }
            """);

        Assert.Equal((200, JsonValueKind.Null), (status, view.GetProperty("plotting").ValueKind));
        NavRoute trip = nav.Route!;
        Assert.Equal(new PlotRequest { Kind = PlotRequest.Trip, From = "Sol", To = "Sirius", Name = "Shopping for Explorer" }, trip.Request);
        Assert.Equal("", trip.Job);
        const string Hutton = "Dock at Hutton Orbital and buy 2 × A Shield Booster. Dock at al-Din Prospect and buy 4E Fuel Scoop.";
        Assert.Equal(new[]
        {
            new RouteHop("Sol", SolAddress, 0, 0, 0, 0, 8.59, 0, false, false, 0, "Dock at Abraham Lincoln and buy 5A Frame Shift Drive."),
            new RouteHop("Alpha Centauri", 1178708478315, 3.03125, -0.09375, 3.15625, 4.38, 9.54, 1, false, false, 0, Hutton),
            new RouteHop("Sirius", 0, 6.25, -1.28125, -5.75, 9.54, 0, 1, false, false, 0),
        }, trip.Hops.Select(hop => hop with { Distance = Math.Round(hop.Distance, 2), DistanceLeft = Math.Round(hop.DistanceLeft, 2) }));
        // What to do here, and where to go next and what to do there.
        Assert.Equal(new NavProgress(0, 0, "Alpha Centauri", 4.4, 1, false, 2, 2, 13.9, Hutton, "Dock at Abraham Lincoln and buy 5A Frame Shift Drive."), nav.Progress);
        Assert.Equal(Hutton, view.GetProperty("progress").GetProperty("nextNote").GetString());
        Assert.Equal("1178708478315", view.GetProperty("route").GetProperty("hops")[1].GetProperty("address").GetString());

        // The request Spansh was working on was dropped: its route does not take the trip's place when it comes.
        hold = false;
        await plotting;
        Assert.Same(trip, nav.Route);
        Assert.Single(net.Asked);   // the job was never looked for again

        Jump("Alpha Centauri", 3.03125, -0.09375, 3.15625, 1178708478315);
        Assert.Equal(new NavProgress(1, 1, "Sirius", 9.5, 1, false, 1, 1, 9.5, "", Hutton), nav.Progress);

        // Studio starts again in the middle of the trip.
        using GameWatcher restarted = game.Watch();
        restarted.Poll();
        var again = new NavRoutes(restarted.State, SettingsFile(), spansh);
        Assert.Equal((trip.Request, trip.PlottedUtc), (again.Route!.Request, again.Route.PlottedUtc));
        Assert.Equal(trip.Hops, again.Route.Hops);
        Assert.Equal(nav.Progress, again.Progress);

        // At the last stop there is no next one, and nothing to do there either.
        Jump("Sirius", 6.25, -1.28125, -5.75);
        Assert.Equal(new NavProgress(2, 2, null, 0, 0, false, 0, 0, 0), nav.Progress);

        // A trip without one stop that can be flown to is turned down, and the route stays.
        (status, view) = await Call(web, "POST", "/api/nav/trip", """{ "name": "Nothing", "stops": [ null, { "system": "" } ] }""");
        Assert.Equal((400, "This trip has no stops to fly to."), (status, view.GetProperty("error").GetString()));
        Assert.Same(trip, nav.Route);
    }

    [Fact]
    public void A_trip_starts_where_the_ship_is_and_estimates_each_leg_for_the_ship()
    {
        using var offline = new Spansh(new SpanshStandIn());
        var nav = new NavRoutes(watcher.State, SettingsFile(), offline);

        // One stop, 500 ly from Sol: the route is Sol and the stop.
        nav.Follow("Shopping for Hauler", [new TripStop("Far Away", 0, 300, 0, 400, "Dock at The Shop and buy the Hauler itself.")]);

        Assert.Equal(new[] { "Sol", "Far Away" }, nav.Route!.Hops.Select(hop => hop.System));
        Assert.Equal((500, ShipMath.JumpsFor(500, watcher.State.JumpLaden)), (nav.Route.Hops[1].Distance, nav.Route.Hops[1].Jumps));
        Assert.InRange(nav.Route.Hops[1].Jumps, 10, 12);   // the Mandalay does about 50 ly with a full hold
        Assert.Equal(("Far Away", 500, "Dock at The Shop and buy the Hauler itself.", ""), (nav.Progress!.Next, nav.Progress.NextLy, nav.Progress.NextNote, nav.Progress.HereNote));
    }

    [Theory]
    [InlineData("""{"kind":"neutron","from":"","to":"Colonia","range":50}""", "Type the system the route starts in.")]
    [InlineData("""{"kind":"neutron","from":"Sol","to":"  ","range":50}""", "Type the system you want to go to.")]
    [InlineData("""{"kind":"neutron","from":"Sol","to":"Colonia"}""", "Type your ship's jump range in light years.")]
    [InlineData("""{"kind":"neutron","from":"Sol","to":"Colonia","range":-4}""", "Type your ship's jump range in light years.")]
    [InlineData("""{"kind":"scenic","from":"Sol","to":"Colonia","range":50}""", "Unknown kind of route.")]
    public async Task A_request_with_something_missing_is_turned_down_before_Spansh_is_asked(string body, string expected)
    {
        var net = new SpanshStandIn();
        using Spansh spansh = NavSpanshTests.Quick(net);
        using var web = new WebServer(temp);
        NavRoutes nav = NavApi.Map(web, watcher.State, SettingsFile(), spansh);

        var (status, answer) = await Call(web, "POST", "/api/nav/plot", body);

        Assert.Equal(400, status);
        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Equal(expected, answer.GetProperty("error").GetString());
        Assert.Null(nav.Plotting);
        Assert.Empty(net.Asked);
    }

    [Fact]
    public async Task An_exact_route_without_a_known_ship_says_what_is_missing()
    {
        using var noShip = new PracticeGame();
        noShip.Journal("Journal.2026-10-09T030000.01.log", [Arrival("Location", "Sol", 0, 0, 0)]);
        using GameWatcher noShipWatcher = noShip.Watch();
        noShipWatcher.Poll();
        var net = Answering(NeutronResult);
        using Spansh spansh = NavSpanshTests.Quick(net);
        using var web = new WebServer(temp);
        NavRoutes nav = NavApi.Map(web, noShipWatcher.State, SettingsFile(), spansh);

        var (status, answer) = await Call(web, "POST", "/api/nav/plot", """{"kind":"exact","from":"Sol","to":"Colonia"}""");
        Assert.Equal(400, status);
        Assert.Equal(
            "The exact plotter works from your own ship, and Studio does not have your ship's details: the game writes them when you board your ship, so start Elite first.",
            answer.GetProperty("error").GetString());
        Assert.Empty(net.Asked);

        // A neutron route needs no ship: only the jump range the player typed. (An efficiency box left empty arrives as 0.)
        (status, _) = await Call(web, "POST", "/api/nav/plot", """{"kind":"neutron","from":"Sol","to":"Lagoon Sector NI-S b4-10","range":50.9,"efficiency":0}""");
        Assert.Equal(200, status);
        await Finished(nav);
        Assert.Equal(NavSpanshTests.NeutronRequest, nav.Route!.Request);
    }

    [Fact]
    public async Task Cancel_route_stops_a_request_under_way()
    {
        var net = new SpanshStandIn().Then(SpanshStandIn.Accepted("NEVER"));
        using var spansh = new Spansh(net) { Wait = (_, stop) => Task.Delay(Timeout.Infinite, stop) };
        using var web = new WebServer(temp);
        NavRoutes nav = NavApi.Map(web, watcher.State, SettingsFile(), spansh);

        var (_, view) = await Call(web, "POST", "/api/nav/plot", """{"kind":"neutron","from":"Sol","to":"Colonia","range":50}""");
        Assert.Equal("queued", view.GetProperty("plotting").GetProperty("state").GetString());
        Assert.Equal("Colonia", view.GetProperty("plotting").GetProperty("request").GetProperty("to").GetString());

        (_, view) = await Call(web, "POST", "/api/nav/cancel", "{}");
        Assert.Equal(JsonValueKind.Null, view.GetProperty("plotting").ValueKind);
        Assert.Null(nav.Plotting);
    }

    [Fact]
    public async Task System_names_route_answers_with_names_and_with_none_when_Spansh_cannot_be_reached()
    {
        var net = new SpanshStandIn().Then(SpanshStandIn.Recorded("system-names.json"));
        using Spansh spansh = NavSpanshTests.Quick(net);
        using var web = new WebServer(temp);
        NavApi.Map(web, watcher.State, SettingsFile(), spansh);

        var (status, answer) = await Call(web, "GET", "/api/nav/systems", q: "Maia");
        Assert.Equal(200, status);
        Assert.Equal(new[] { "Maia", "Maiadiwobog", "Maiawa" }, answer.GetProperty("systems").EnumerateArray().Select(name => name.GetString()));

        (status, answer) = await Call(web, "GET", "/api/nav/systems", q: "Ma");
        Assert.Equal((200, 0), (status, answer.GetProperty("systems").GetArrayLength()));

        (status, answer) = await Call(web, "GET", "/api/nav/systems", q: "Colonia");   // the stand-in is "offline" by now
        Assert.Equal((200, 0), (status, answer.GetProperty("systems").GetArrayLength()));
        Assert.Equal(2, net.Asked.Count);
    }

    // ------------------------------------------------------------ the "nav" event

    [Fact]
    public async Task Every_change_reaches_a_listening_page_as_a_nav_event()
    {
        // POST plot answers before Spansh does, so the event is the only way a page hears of the
        // route. Hence the real server on a port here, listened to the way a page listens.
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using Spansh spansh = NavSpanshTests.Quick(Answering(NeutronResult));
        using var web = new WebServer(temp);
        NavRoutes nav = NavApi.Map(web, watcher.State, SettingsFile(), spansh);
        Assert.True(web.Start(47830));
        using var http = new HttpClient();
        using var events = new StreamReader(await http.GetStreamAsync($"http://127.0.0.1:{web.Port}/api/events?t={web.Token}", cancel));

        async Task<JsonElement> NextNavEvent()
        {
            while (true)
            {
                string? line = await events.ReadLineAsync(cancel).AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancel);
                Assert.NotNull(line);   // null: the stream ended
                if (line.StartsWith("data: ", StringComparison.Ordinal)) return JsonSerializer.Deserialize<JsonElement>(line[6..]);
                Assert.True(line is "" or ": connected" or "event: nav", $"unexpected line on the stream: {line}");
            }
        }

        await Call(web, "POST", "/api/nav/plot", """{"kind":"neutron","from":"Sol","to":"Lagoon Sector NI-S b4-10","range":50.9}""");

        JsonElement asking = await NextNavEvent();
        Assert.Equal("Lagoon Sector NI-S b4-10", asking.GetProperty("plotting").GetProperty("request").GetProperty("to").GetString());
        Assert.Equal(JsonValueKind.Null, asking.GetProperty("route").ValueKind);

        JsonElement answered = await NextNavEvent();
        Assert.Equal(JsonValueKind.Null, answered.GetProperty("plotting").ValueKind);
        Assert.Equal(24, answered.GetProperty("route").GetProperty("hops").GetArrayLength());
        Assert.Equal("PSR J1752-2806", answered.GetProperty("progress").GetProperty("next").GetString());

        JumpTo(nav.Route!.Hops[1]);

        JsonElement flown = await NextNavEvent();
        Assert.Equal((1, 1), (flown.GetProperty("progress").GetProperty("at").GetInt32(), flown.GetProperty("progress").GetProperty("here").GetInt32()));
        Assert.Equal("Col 359 Sector NN-T e3-3", flown.GetProperty("progress").GetProperty("next").GetString());
    }
}
