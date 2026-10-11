using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using EliteStudio.Core;
using EliteStudio.Nav;

namespace EliteStudio.Tests;

/// <summary>
/// Stands in for spansh.co.uk, so no test goes near the internet: gives the scripted answers in
/// order and notes what was asked. With the script used up it behaves like a PC that is offline.
/// </summary>
sealed class SpanshStandIn : HttpMessageHandler
{
    readonly Queue<(HttpStatusCode Status, string Body)> script = new();

    /// <summary>Each call as "METHOD /path?query", followed by the form it carried, if any.</summary>
    public List<string> Asked { get; } = [];

    public string UserAgent { get; private set; } = "";

    /// <summary>Given again and again once the script is used up, instead of being offline.</summary>
    public string? Forever { get; set; }

    public SpanshStandIn Then(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        script.Enqueue((status, body));
        return this;
    }

    /// <summary>One call that finds the PC offline, in the middle of the script.</summary>
    public SpanshStandIn ThenOffline() => Then("", Offline);

    const HttpStatusCode Offline = 0;

    /// <summary>A recorded answer from tests\Fixtures\nav.</summary>
    public static string Recorded(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nav", name));

    /// <summary>Spansh's id for the job a recorded result belongs to.</summary>
    public static string Job(string recorded)
    {
        using JsonDocument answer = JsonDocument.Parse(recorded);
        return answer.RootElement.GetProperty("job").GetString()!;
    }

    /// <summary>What Spansh answers when it takes a request on (seen live; HTTP 202).</summary>
    public static string Accepted(string job) => $$"""{"job":"{{job}}","status":"queued"}""";

    /// <summary>
    /// What Spansh answers while a job is unfinished (seen live; HTTP 202): the envelope of the
    /// finished answer without its result, with "status" still "queued" while "state" says "started".
    /// </summary>
    public static string Unfinished(string job) =>
        $$"""{"created_at":"2026-10-09T06:34:52","job":"{{job}}","parameters":{},"state":"started","status":"queued","updated_at":"2026-10-09T06:34:53"}""";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        string form = request.Content is null ? "" : " " + await request.Content.ReadAsStringAsync(cancel);
        Asked.Add($"{request.Method} {request.RequestUri!.PathAndQuery}{form}");
        UserAgent = request.Headers.UserAgent.ToString();
        if (!script.TryDequeue(out var answer)) answer = Forever is null ? (Offline, "") : (HttpStatusCode.Accepted, Forever);
        if (answer.Status == Offline) throw new HttpRequestException("No such host is known.");
        return new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Body, Encoding.UTF8, "application/json") };
    }
}

/// <summary>
/// The Spansh client against answers recorded from the real service on 9 October 2026, and the
/// ship numbers the exact plotter is sent. In tests\Fixtures\nav, byte for byte as Spansh sent them:
///   neutron-result.json  the finished job for <see cref="NeutronRequest"/> (24 stops; it took Spansh 40 seconds)
///   exact-result.json    the finished job for <see cref="ExactRequest"/> (8 jumps; it took Spansh 61 seconds)
///   system-names.json    the name search for "Maia"
/// Both jobs were asked for by Studio itself, running against a practice journal; the two short
/// answers that come before a result (job taken on, job unfinished) are written out in
/// <see cref="SpanshStandIn"/> as they were seen then.
/// </summary>
public class NavSpanshTests
{
    static readonly string NeutronResult = SpanshStandIn.Recorded("neutron-result.json");
    static readonly string ExactResult = SpanshStandIn.Recorded("exact-result.json");

    internal static readonly PlotRequest NeutronRequest = new()
    {
        Kind = PlotRequest.Neutron, From = "Sol", To = "Lagoon Sector NI-S b4-10", Range = 50.9, Efficiency = 60, Supercharge = 4,
    };

    // the owner's engineered Mandalay, as ShipNumbers.Of reads it from the "Engineered" recording
    internal static readonly ShipNumbers Mandalay = new(2.45, 0.013, 1586.25, 5.2, 388.600006 + 0.5, 32, 0.5, 10.5, 4, IsSupercharged: false);

    internal static readonly PlotRequest ExactRequest = new() { Kind = PlotRequest.Exact, From = "Sol", To = "PSR J1752-2806", Ship = Mandalay };

    /// <summary>A client that does not really wait between looks; the waits it would have made are noted.</summary>
    internal static Spansh Quick(SpanshStandIn net, List<double>? waits = null) => new(net)
    {
        Wait = (time, cancel) =>
        {
            waits?.Add(time.TotalSeconds);
            return Task.CompletedTask;
        },
    };

    // ------------------------------------------------------------ plotting

    [Fact]
    public async Task Neutron_route_is_sent_as_a_job_then_looked_for_until_it_is_there()
    {
        string job = SpanshStandIn.Job(NeutronResult);
        var net = new SpanshStandIn()
            .Then(SpanshStandIn.Accepted(job), HttpStatusCode.Accepted)
            .Then(SpanshStandIn.Unfinished(job), HttpStatusCode.Accepted)
            .Then(SpanshStandIn.Unfinished(job), HttpStatusCode.Accepted)
            .Then(NeutronResult);
        var waits = new List<double>();
        var states = new List<string>();
        using Spansh spansh = Quick(net, waits);

        NavRoute route = await spansh.Plot(NeutronRequest, states.Add, TestContext.Current.CancellationToken);

        Assert.Equal(new[]
        {
            "POST /api/route from=Sol&to=Lagoon+Sector+NI-S+b4-10&range=50.9&efficiency=60&supercharge_multiplier=4",
            $"GET /api/results/{job}", $"GET /api/results/{job}", $"GET /api/results/{job}",
        }, net.Asked);
        Assert.Equal(new double[] { 1, 2, 4 }, waits);
        Assert.Equal(new[] { "started", "started" }, states);
        Assert.Matches(@"^EliteStudio/\d+\.\d+\.\d+ \(Elite Dangerous companion app; \+https://github\.com/[\w-]+/Elite-Studio\)$", net.UserAgent);   // the program and its public home

        Assert.Equal(NeutronRequest, route.Request);
        Assert.Equal("88D87E68-C3AB-11F1-B135-C0BBE25E9E71", route.Job);
        Assert.Equal(24, route.Hops.Count);
        Assert.Equal(new RouteHop("Sol", 10477373803, 0, 0, 0, 0, 4481.56059734724, 0, false, false, 0), route.Hops[0]);
        Assert.Equal(new RouteHop("PSR J1752-2806", 22712681061, -10.96875, -6.84375, 407.28125, 407.486400990496, 4075.43668025472, 9, true, false, 0), route.Hops[1]);
        Assert.Equal(
            new RouteHop("Lagoon Sector NI-S b4-10", 22654744341665, -469.1875, -84.84375, 4456.125, 265.35644880401, 0, 3, false, false, 0),
            route.Hops[^1]);
        Assert.Equal(63, route.Hops.Sum(hop => hop.Jumps));       // Spansh's own "total_jumps" says 23: it counts legs
        Assert.Equal(22, route.Hops.Count(hop => hop.Neutron));   // every stop but the two ends
    }

    [Fact]
    public async Task Exact_route_is_asked_for_with_the_ships_numbers_and_every_row_is_one_jump()
    {
        string job = SpanshStandIn.Job(ExactResult);
        var net = new SpanshStandIn().Then(SpanshStandIn.Accepted(job), HttpStatusCode.Accepted).Then(ExactResult);
        using Spansh spansh = Quick(net);

        NavRoute route = await spansh.Plot(ExactRequest, cancel: TestContext.Current.CancellationToken);

        Assert.Equal(
            "POST /api/generic/route source=Sol&destination=PSR+J1752-2806&is_supercharged=0&use_supercharge=1&use_injections=0"
            + "&use_injections_when_required=0&exclude_secondary=0&refuel_every_scoopable=1&fuel_power=2.45&fuel_multiplier=0.013&optimal_mass=1586.25"
            + "&base_mass=389.100006&tank_size=32&internal_tank_size=0.5&max_fuel_per_jump=5.2&range_boost=10.5&reserve_size=0"
            + "&cargo=0&supercharge_multiplier=4&algorithm=optimistic&max_time=60",
            net.Asked[0]);
        Assert.Equal($"GET /api/results/{job}", net.Asked[1]);

        Assert.Equal(9, route.Hops.Count);
        Assert.Equal(new RouteHop("Sol", 10477373803, 0, 0, 0, 0, 407.486400990496, 0, false, false, 32), route.Hops[0]);
        Assert.Equal(new RouteHop("CD-46 12902", 4481899041498, 6.59375, -21.28125, 47.53125, 52.4937019348145, 360.46767582198, 1, false, true, 32), route.Hops[1]);
        Assert.Equal(
            new RouteHop("Scorpii Sector IH-V a2-1", 22959284439968, -3.40625, -32.625, 99.3125, 53.9442176818848, 309.138504936501, 1, false, false, 26.8149967193604),
            route.Hops[2]);
        Assert.All(route.Hops.Skip(1), hop => Assert.Equal(1, hop.Jumps));
        Assert.Equal(new[] { "CD-46 12902", "Col 359 Sector FN-I b11-7" }, route.Hops.Where(hop => hop.Refuel).Select(hop => hop.System));
        Assert.DoesNotContain(route.Hops, hop => hop.Neutron);   // too short a trip for one; the next test has a neutron star
    }

    [Fact]
    public async Task A_supercharged_start_is_told_to_Spansh_and_is_a_route_of_its_own()
    {
        string job = SpanshStandIn.Job(ExactResult);
        var net = new SpanshStandIn().Then(SpanshStandIn.Accepted(job)).Then(ExactResult).Then(SpanshStandIn.Accepted(job)).Then(ExactResult);
        using Spansh spansh = Quick(net);
        PlotRequest charged = ExactRequest with { Ship = Mandalay with { IsSupercharged = true } };

        await spansh.Plot(ExactRequest, cancel: TestContext.Current.CancellationToken);
        await spansh.Plot(charged, cancel: TestContext.Current.CancellationToken);
        await spansh.Plot(charged with { }, cancel: TestContext.Current.CancellationToken);   // kept from the call before

        // The route kept for the uncharged ship is not handed out for the charged one: its first jump may be four times as long.
        Assert.Equal(4, net.Asked.Count);
        Assert.StartsWith("POST /api/generic/route source=Sol&destination=PSR+J1752-2806&is_supercharged=0&use_supercharge=1&", net.Asked[0]);
        Assert.StartsWith("POST /api/generic/route source=Sol&destination=PSR+J1752-2806&is_supercharged=1&use_supercharge=1&", net.Asked[2]);
        Assert.Equal(net.Asked[0].Replace("is_supercharged=0", "is_supercharged=1"), net.Asked[2]);   // nothing else differs
    }

    [Fact]
    public async Task Numbers_sent_as_text_and_yes_or_no_sent_as_1_or_0_are_read_all_the_same()
    {
        // Spansh echoes "range":"50" as text, and Auto_Neutron's authors have seen must_refuel arrive as 0 or 1.
        // A system's number is the exception: it is too long to be read as loosely, so one that
        // arrives as text counts as not given, like one left out. (9007199254740993 is the first
        // whole number a web page, or a number with a decimal point, cannot hold.)
        const string Loose = """
            {"job":"J","status":"ok","result":{"jumps":[
              {"name":"A","id64":9007199254740993,"x":"1.5","y":-2,"z":0,"distance":0,"distance_to_destination":"12.25","fuel_in_tank":"8","has_neutron":0,"must_refuel":1},
              {"name":"B","id64":"22712681061","x":1.5,"y":-2,"z":12.25,"distance":"12.25","distance_to_destination":0,"fuel_in_tank":6.5,"has_neutron":1,"must_refuel":0},
              {"name":"C","x":1.5,"y":-2,"z":20,"distance":7.75,"distance_to_destination":0,"fuel_in_tank":5,"has_neutron":false,"must_refuel":false}]}}
            """;
        using Spansh spansh = Quick(new SpanshStandIn().Then(SpanshStandIn.Accepted("J")).Then(Loose));

        NavRoute route = await spansh.Plot(ExactRequest, cancel: TestContext.Current.CancellationToken);

        Assert.Equal(new[]
        {
            new RouteHop("A", 9007199254740993, 1.5, -2, 0, 0, 12.25, 0, false, true, 8),
            new RouteHop("B", 0, 1.5, -2, 12.25, 12.25, 0, 1, true, false, 6.5),
            new RouteHop("C", 0, 1.5, -2, 20, 7.75, 0, 1, false, false, 5),
        }, route.Hops);
        // To a page, and into settings.json, the number goes as text, so that no digit of it is lost on the way.
        string sent = JsonSerializer.Serialize(route.Hops[0], EliteStudio.App.Settings.Json);
        Assert.Contains("\"address\": \"9007199254740993\"", sent);
        Assert.Equal(route.Hops[0], JsonSerializer.Deserialize<RouteHop>(sent, EliteStudio.App.Settings.Json));
    }

    [Fact]
    public async Task A_finished_route_is_kept_for_the_session_and_not_asked_for_twice()
    {
        string job = SpanshStandIn.Job(NeutronResult);
        var net = new SpanshStandIn()
            .Then(SpanshStandIn.Accepted(job)).Then(NeutronResult)
            .Then(SpanshStandIn.Accepted(job)).Then(NeutronResult);
        using Spansh spansh = Quick(net);

        NavRoute first = await spansh.Plot(NeutronRequest, cancel: TestContext.Current.CancellationToken);
        NavRoute again = await spansh.Plot(NeutronRequest with { }, cancel: TestContext.Current.CancellationToken);
        Assert.Same(first, again);
        Assert.Equal(2, net.Asked.Count);

        await spansh.Plot(NeutronRequest with { Efficiency = 80 }, cancel: TestContext.Current.CancellationToken);
        Assert.Equal(4, net.Asked.Count);
        Assert.Contains("efficiency=80", net.Asked[2]);
    }

    // ------------------------------------------------------------ when it does not work

    [Theory]
    [InlineData("""{"error":"Could not find starting system"}""", 400, "Spansh does not know the system typed under From. Check its spelling, or pick a name from the list.")]
    [InlineData("""{"error":"Could not find finishing system"}""", 400, "Spansh does not know the system typed under To. Check its spelling, or pick a name from the list.")]
    [InlineData("""{"error":"from, to and range are required"}""", 400, "Spansh said: from, to and range are required")]
    [InlineData("""{"status":"queued"}""", 202, "Spansh sent an answer Studio could not read. Try again in a moment.")]   // no job id
    [InlineData("<html><body>502 Bad Gateway</body></html>", 502, "Spansh sent an answer Studio could not read. Try again in a moment.")]
    [InlineData("{}", 503, "Spansh answered with error 503. Try again in a moment.")]
    public async Task A_refused_request_says_why_in_plain_words(string answer, int status, string expected)
    {
        var net = new SpanshStandIn().Then(answer, (HttpStatusCode)status);
        using Spansh spansh = Quick(net);

        var problem = await Assert.ThrowsAsync<SpanshException>(() => spansh.Plot(NeutronRequest, cancel: TestContext.Current.CancellationToken));

        Assert.Equal(expected, problem.Message);
        Assert.Single(net.Asked);   // no second try
    }

    [Theory]
    [InlineData("""{"error":"Unable to find route"}""", 404, "Spansh said: Unable to find route")]                     // the job is gone
    [InlineData("""{"job":"J","state":"failed","status":"error"}""", 200, "Spansh sent an answer Studio could not read. Try again in a moment.")]
    [InlineData("""{"job":"J","status":"ok","result":{"system_jumps":[]}}""", 200, "Spansh found no route between these two systems.")]
    public async Task A_job_that_goes_wrong_says_why_in_plain_words(string answer, int status, string expected)
    {
        var net = new SpanshStandIn().Then(SpanshStandIn.Accepted("J")).Then(answer, (HttpStatusCode)status);
        using Spansh spansh = Quick(net);

        var problem = await Assert.ThrowsAsync<SpanshException>(() => spansh.Plot(NeutronRequest, cancel: TestContext.Current.CancellationToken));

        Assert.Equal(expected, problem.Message);
        Assert.Equal(2, net.Asked.Count);
    }

    [Fact]
    public async Task Offline_is_reported_as_such()
    {
        var net = new SpanshStandIn();
        using Spansh spansh = Quick(net);

        var problem = await Assert.ThrowsAsync<SpanshException>(() => spansh.Plot(NeutronRequest, cancel: TestContext.Current.CancellationToken));

        Assert.Equal("Spansh could not be reached. Check your internet connection and try again.", problem.Message);
        Assert.Single(net.Asked);   // the request is not sent a second time
    }

    [Fact]
    public async Task A_look_lost_on_the_way_is_followed_by_the_next_look_not_by_a_second_job()
    {
        // The job carries on at Spansh whatever happens to a look at it: the connection dropping for
        // a moment, Spansh's front door answering with an error page or a bare 503.
        string job = SpanshStandIn.Job(NeutronResult);
        var net = new SpanshStandIn()
            .Then(SpanshStandIn.Accepted(job), HttpStatusCode.Accepted)
            .ThenOffline()
            .Then("<html><body>502 Bad Gateway</body></html>", HttpStatusCode.BadGateway)
            .Then(SpanshStandIn.Unfinished(job), HttpStatusCode.Accepted)
            .ThenOffline()
            .Then("{}", HttpStatusCode.ServiceUnavailable)
            .Then(NeutronResult);
        var waits = new List<double>();
        using Spansh spansh = Quick(net, waits);

        NavRoute route = await spansh.Plot(NeutronRequest, cancel: TestContext.Current.CancellationToken);

        Assert.Equal(24, route.Hops.Count);
        Assert.Single(net.Asked, asked => asked.StartsWith("POST ", StringComparison.Ordinal));
        Assert.Equal(1 + 6, net.Asked.Count);
        Assert.Equal(new double[] { 1, 2, 4, 4, 4, 6 }, waits);   // the usual pace throughout: a lost look is not hurried after
    }

    [Fact]
    public async Task Three_looks_lost_in_a_row_end_the_wait_with_the_reason()
    {
        var net = new SpanshStandIn().Then(SpanshStandIn.Accepted("J"), HttpStatusCode.Accepted);   // and offline from then on
        using Spansh spansh = Quick(net);

        var problem = await Assert.ThrowsAsync<SpanshException>(() => spansh.Plot(NeutronRequest, cancel: TestContext.Current.CancellationToken));

        Assert.Equal("Spansh could not be reached. Check your internet connection and try again.", problem.Message);
        Assert.Equal(1 + 3, net.Asked.Count);
    }

    [Fact]
    public async Task A_job_that_never_finishes_is_given_up_after_three_minutes_of_unhurried_looks()
    {
        var net = new SpanshStandIn { Forever = SpanshStandIn.Unfinished("J") }.Then(SpanshStandIn.Accepted("J"));
        var waits = new List<double>();
        using Spansh spansh = Quick(net, waits);

        var problem = await Assert.ThrowsAsync<SpanshException>(() => spansh.Plot(ExactRequest, cancel: TestContext.Current.CancellationToken));

        Assert.Equal("Spansh is still working on this route after 3 minutes. It may be busy: try again later, or try a shorter route.", problem.Message);
        // Quick looks first, then one every ten seconds: 177 seconds in all, and one more look would pass 180.
        double[] unhurried = [1, 2, 4, 4, 4, 6, 6, .. Enumerable.Repeat(10.0, 15)];
        Assert.Equal(unhurried, waits);
        Assert.Equal(1 + waits.Count, net.Asked.Count);
    }

    [Fact]
    public async Task Cancelling_stops_the_waiting_and_asks_nothing_more()
    {
        var net = new SpanshStandIn { Forever = SpanshStandIn.Unfinished("J") }.Then(SpanshStandIn.Accepted("J"));
        using var cancel = new CancellationTokenSource();
        var waiting = new TaskCompletionSource();
        using var spansh = new Spansh(net)
        {
            Wait = (_, stop) =>
            {
                waiting.TrySetResult();
                return Task.Delay(Timeout.Infinite, stop);
            },
        };

        Task<NavRoute> plot = spansh.Plot(NeutronRequest, cancel: cancel.Token);
        await waiting.Task;
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plot);
        Assert.Single(net.Asked);   // the request itself; the job's result was never looked for
    }

    // ------------------------------------------------------------ system names

    [Fact]
    public async Task System_names_are_asked_for_from_three_letters_on_and_each_answer_is_kept()
    {
        var net = new SpanshStandIn().Then(SpanshStandIn.Recorded("system-names.json")).Then("""{"min_max":[],"values":[]}""");
        using Spansh spansh = Quick(net);

        Assert.Empty(await spansh.SystemNames("So"));
        Assert.Empty(net.Asked);

        Assert.Equal(new[] { "Maia", "Maiadiwobog", "Maiawa" }, await spansh.SystemNames(" Maia "));
        Assert.Equal(3, (await spansh.SystemNames("maia")).Count);   // the same letters again: not asked again
        Assert.Empty(await spansh.SystemNames("Jackson's & co"));
        Assert.Equal(new[]
        {
            "GET /api/systems/field_values/system_names?q=Maia",
            "GET /api/systems/field_values/system_names?q=Jackson%27s%20%26%20co",
        }, net.Asked);

        await Assert.ThrowsAsync<SpanshException>(() => spansh.SystemNames("Offline now"));
    }

    // ------------------------------------------------------------ the ship numbers the exact plotter is sent

    static GameState Ship(PracticeGame game, params string[] journalLines)
    {
        game.Journal("Journal.2026-10-09T030000.01.log", journalLines);
        using GameWatcher watcher = game.Watch();
        watcher.Poll();
        return watcher.State;
    }

    static string Loadout(string drive) => string.Create(CultureInfo.InvariantCulture, $$"""
        { "timestamp":"2026-10-09T03:00:00Z", "event":"Loadout", "Ship":"asp", "UnladenMass":280.5, "CargoCapacity":64, "MaxJumpRange":30.1, "FuelCapacity":{ "Main":32.0, "Reserve":0.63 }, "Modules":[ { "Slot":"FrameShiftDrive", "Item":"{{drive}}", "On":true } ] }
        """).Trim();

    [Fact]
    public void Ship_numbers_come_from_the_loadout_with_engineering_on_top()
    {
        using var game = new PracticeGame();
        string[] engineered = PracticeGame.Recording("Journal.2026-10-08T080236.01.log");

        ShipNumbers? ship = ShipNumbers.Of(Ship(game, PracticeGame.Line(engineered, "Loadout")), out string missing);

        // A 5A overcharge drive: 5.2 t a jump from the table, but the optimal mass an engineer raised from 1175 to 1586.25.
        // 388.600006 t of ship plus the 0.5 t reserve tank; a size 5 Guardian booster.
        Assert.Equal(Mandalay, ship);
        Assert.Equal("", missing);
    }

    [Theory]
    [InlineData("int_hyperdrive_size2_class1", 2.0, 0.011, 48, 0.6, 4)]
    [InlineData("int_hyperdrive_size4_class4", 2.3, 0.010, 437.5, 2.5, 4)]
    [InlineData("Int_Hyperdrive_Size5_Class3", 2.45, 0.008, 700, 3.3, 4)]
    [InlineData("int_hyperdrive_size7_class5", 2.75, 0.012, 2700, 12.8, 4)]
    [InlineData("int_hyperdrive_overcharge_size3_class5", 2.15, 0.013, 167, 1.9, 4)]
    [InlineData("int_hyperdrive_overcharge_size6_class1", 2.6, 0.008, 1200, 5.3, 4)]
    [InlineData("int_hyperdrive_overcharge_size8_class3", 2.9, 0.012, 4200, 20.4, 4)]
    [InlineData("int_hyperdrive_overcharge_size8_class5_overchargebooster_mkii", 2.5025, 0.011, 4670, 6.8, 6)]
    public void Every_drive_has_its_own_numbers(string drive, double power, double multiplier, double optimalMass, double fuel, int supercharge)
    {
        using var game = new PracticeGame();

        ShipNumbers? ship = ShipNumbers.Of(Ship(game, Loadout(drive)), out _);

        Assert.Equal(new ShipNumbers(power, multiplier, optimalMass, fuel, 280.5 + 0.63, 32, 0.63, 0, supercharge, IsSupercharged: false), ship);
    }

    [Fact]
    public void A_drive_that_holds_a_neutron_stars_supercharge_is_reported_as_supercharged()
    {
        using var game = new PracticeGame();
        const string Boost = """{ "timestamp":"2026-10-09T03:01:00Z", "event":"JetConeBoost", "BoostValue":4.000000 }""";

        ShipNumbers? ship = ShipNumbers.Of(Ship(game, Loadout("int_hyperdrive_size5_class5"), Boost), out _);

        Assert.Equal(new ShipNumbers(2.45, 0.012, 1050, 5.0, 280.5 + 0.63, 32, 0.63, 0, 4, IsSupercharged: true), ship);
    }

    [Fact]
    public void What_the_game_has_not_said_about_the_ship_is_named()
    {
        using var noShip = new PracticeGame();
        Assert.Null(ShipNumbers.Of(Ship(noShip, """{ "timestamp":"2026-10-09T03:00:00Z", "event":"Music", "MusicTrack":"MainMenu" }"""), out string missing));
        Assert.Equal("your ship's details: the game writes them when you board your ship, so start Elite first", missing);

        using var newDrive = new PracticeGame();
        Assert.Null(ShipNumbers.Of(Ship(newDrive, Loadout("int_hyperdrive_size8_class5")), out missing));   // no such drive in the table
        Assert.Equal("the numbers for your frame shift drive (int_hyperdrive_size8_class5), which is new to Studio", missing);

        using var noDrive = new PracticeGame();
        Assert.Null(ShipNumbers.Of(Ship(noDrive, Loadout("int_hyperdrive_size5_class5").Replace("FrameShiftDrive", "Slot01_Size5")), out missing));
        Assert.Equal("your frame shift drive: the game listed none for this ship", missing);
    }
}
