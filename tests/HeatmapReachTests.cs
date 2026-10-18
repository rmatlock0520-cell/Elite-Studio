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
/// "Within reach" (<see cref="HeatmapReach"/>): after every arrival, the systems within the ship's jump range, from the
/// detailed map inside it and with one request to Spansh outside it, and the systems that must exist there. Nothing
/// goes near the internet: <see cref="SpanshStandIn"/> answers Spansh's system search in its own layout, and with its
/// script used up behaves like a PC that is offline. The detailed map is built here from a list of five systems in
/// Spansh's layout: four of the game's cube Wredguia WD-K d8 (numbers 0, 1, 3 and 7, as in FineMapTests) and one in
/// the sector above Wredguia. The game's lines are written as the game writes them.
/// </summary>
public sealed class HeatmapReachTests : IDisposable
{
    const string Unreachable = "Spansh could not be reached. Check your internet connection and try again.";

    /// <summary>Where the commander is: 5 ly from Wredguia WD-K d8-1, inside the cube of Wredguia WD-K d8.</summary>
    static readonly double[] Home = [-340, 20, -295];

    static readonly (string Name, long Id64, double X, double Y, double Z) D0 = ("Wredguia WD-K d8-0", 10410264907, -350.5, 10.25, -300.125),
        D1 = ("Wredguia WD-K d8-1", 44770003275, -340, 20, -290),
        D3 = ("Wredguia WD-K d8-3", 113489480011, -306.5, 50, -266),
        D7 = ("Wredguia WD-K d8-7", 250928433483, -384.96875, -24.96875, -344.96875);

    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioHeatmapReach-").FullName;
    readonly SpanshStandIn net = new();
    readonly GameState game = new();
    readonly List<Spansh> clients = [];
    HeatmapSurvey? survey;
    FineMap? fine;
    CheckMemory? memory;

    public void Dispose()
    {
        foreach (Spansh client in clients) client.Dispose();
        Directory.Delete(temp, recursive: true);
    }

    string SettingsFile => Path.Combine(temp, "settings.json");

    /// <summary>A start of Studio: Within reach on the game state, with the 3D map's gate, budget, detailed map and memory of checks.</summary>
    HeatmapReach Start(HttpMessageHandler? handler = null)
    {
        var spansh = new Spansh(handler ?? net);
        clients.Add(spansh);
        survey = new HeatmapSurvey(spansh, temp);
        fine = new FineMap(temp);
        memory = new CheckMemory(temp);
        return new HeatmapReach(game, survey, fine, memory, new Settings(SettingsFile)) { RangeWait = TimeSpan.Zero };
    }

    /// <summary>A line of the journal as the game writes it: taken in as it comes, or (<paramref name="replay"/>) read again at Studio's start.</summary>
    void Line(string json, bool replay = false)
    {
        using JsonDocument line = JsonDocument.Parse(json);
        game.Apply(line.RootElement.Clone(), replay);
    }

    /// <summary>The ship, as the game describes it: no weight given, so it jumps 0.8 of its best with a full hold (30 ly for 37.5).</summary>
    void Ship(double best = 37.5, bool replay = true) =>
        Line(string.Create(CultureInfo.InvariantCulture, $$"""{ "timestamp":"2026-10-15T09:00:00Z", "event":"Loadout", "Ship":"krait_mkii", "ShipID":1, "MaxJumpRange":{{best}}, "CargoCapacity":0, "Modules":[] }"""), replay);

    static string At(double[] at, string system, string what = "FSDJump") => string.Create(CultureInfo.InvariantCulture,
        $$"""{ "timestamp":"2026-10-15T10:00:00Z", "event":"{{what}}", "StarSystem":"{{system}}", "SystemAddress":1001, "StarPos":[{{at[0]}},{{at[1]}},{{at[2]}}] }""");

    /// <summary>Studio's start: the journal read again (the ship, where the commander is), then the first look at the game's state.</summary>
    async Task Started(HeatmapReach reach, double[]? at = null)
    {
        Ship();
        Line(At(at ?? Home, "Practice Home", "Location"), replay: true);
        game.RaiseChanged();
        await reach.Pending;
    }

    /// <summary>
    /// Spansh's list of every system on this PC, as Spansh packs it, and the detailed map of the block round Wredguia
    /// built from it (with <paramref name="more"/> systems if given). The sector above Wredguia's file is then broken: a
    /// list that reads it fails.
    /// </summary>
    async Task Detailed(params (string Name, long Id64, double X, double Y, double Z)[] more)
    {
        static string Row((string Name, long Id64, double X, double Y, double Z) system, string? star) => string.Create(CultureInfo.InvariantCulture,
            $$"""	{"id64":{{system.Id64}},"name":"{{system.Name}}",{{(star is null ? "" : $"\"mainStar\":\"{star}\",")}}"coords":{"x":{{system.X}},"y":{{system.Y}},"z":{{system.Z}}},"updateTime":"2026-01-01T00:00:00Z"}""");
        string list = string.Join("\n",
            [
                "[",
                Row(D0, "M (Red dwarf) Star") + ",",
                Row(D1, null) + ",",
                Row(D3, null) + ",",
                Row(D7, "K (Yellow-Orange) Star") + ",",
                .. more.Select(system => Row(system, null) + ","),
                Row(("Sphuedu Star", 652973450241, -1340, 1260, -1060), "T Tauri Star"),
                "]",
            ]);
        string source = Path.Combine(temp, FineMap.DownloadName);
        using (var file = File.Create(source))
        using (var packed = new GZipStream(file, CompressionLevel.Optimal))
            packed.Write(new UTF8Encoding(false).GetBytes(list));
        File.SetLastWriteTimeUtc(source, new DateTime(2026, 10, 14, 11, 53, 17, DateTimeKind.Utc));
        Assert.Null((await fine!.Build(38, 18)).Problem);
        File.WriteAllBytes(Path.Combine(temp, "fine", "sector-38-33-18.jsonl.gz"), [1, 2, 3]);
    }

    /// <summary>Spansh's answer to a search round a point, in its layout: these systems, of how many it knows there.</summary>
    static string Around(int count, params (string Name, long Id64, double X, double Y, double Z)[] systems) => JsonSerializer.Serialize(new
    {
        count,
        from = 0,
        results = systems.Select(system => new Dictionary<string, object>
        {
            ["bodies"] = Array.Empty<object>(), ["id64"] = system.Id64, ["name"] = system.Name, ["x"] = system.X, ["y"] = system.Y, ["z"] = system.Z,
        }),
        size = 500,
    });

    /// <summary>The search round a point, word for word as Studio sends it.</summary>
    static string AskedAround(double[] at, string ly) => string.Create(CultureInfo.InvariantCulture,
        $$$"""POST /api/systems/search {"filters":{"distance":{"min":"0","max":"{{{ly}}}"}},"sort":[{"distance":{"direction":"asc"}}],"size":500,"page":0,"reference_coords":{"x":{{{at[0]}}},"y":{{{at[1]}}},"z":{{{at[2]}}}}}""");

    /// <summary>The systems that must exist in Wredguia WD-K d8 (2, 4, 5 and 6), as Within reach lists them with the commander <paramref name="distance"/> ly from the cube's middle.</summary>
    static ReachMustExist[] D8Gaps(string certainty, double distance) =>
        [.. new[] { 2, 4, 5, 6 }.Select(number => new ReachMustExist($"Wredguia WD-K d8-{number}", SystemAddress.WithNumber(D0.Id64, number), -345, 15, -305, 40, distance, certainty))];

    static IEnumerable<(string, double, string)> Listed(ReachView view) => view.Systems.Select(system => (system.Name, system.Distance, system.State));

    // ------------------------------------------------------------ inside the detailed map

    [Fact]
    public async Task Inside_the_detailed_map_the_systems_within_reach_come_from_the_sector_files_the_sphere_touches_asking_nobody()
    {
        HeatmapReach reach = Start();
        await Detailed();

        // Studio starts with the commander in Wredguia: listed at once, from this PC.
        await Started(reach);

        ReachView view = reach.Now();
        Assert.Equal((HeatmapReach.Done, true, (double?)30, HeatmapReach.FromShip, HeatmapReach.FromDetailed, false, (string?)null),
            (view.State, view.On, view.RangeLy, view.From, view.Source, view.Capped, view.Problem));
        Assert.Equal(new ReachCentre(-340, 20, -295, "Practice Home"), view.Centre);
        // Nearest first; the detailed map knows d8-0's star type, and only d8-1's name and place.
        Assert.Equal(
            [
                new ReachSystem(D1.Name, D1.Id64, D1.X, D1.Y, D1.Z, 5, HeatmapReach.NameOnly),
                new ReachSystem(D0.Name, D0.Id64, D0.X, D0.Y, D0.Z, 15.22, HeatmapReach.Recorded),
            ],
            view.Systems);
        // The commander is inside the cube of Wredguia WD-K d8, which reaches out of the sphere and is 80 ly wide, wider
        // than the sphere of 60: its four gaps are most likely out of reach, so they are counted, not listed.
        Assert.Empty(view.MustExist);
        Assert.Equal((0, 0L, 4L), (view.MoreSystems, view.MoreMustExist, view.InLargeCubes));
        Assert.Empty(net.Asked);
        Assert.Equal(1, reach.SectorReads);   // Wredguia's own: not the sector above it, whose file is broken
        Assert.InRange(view.At!.Value, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);

        // What the memory of checks says comes with each system, as the page asks: d8-1 found recorded by a check since the
        // list was made, d8-5 found not recorded.
        memory!.Put(D1.Name, D1.Id64, CheckMemory.Recorded, CheckMemory.BySpansh, "found by Spansh's system search");
        memory.Put("Wredguia WD-K d8-5", SystemAddress.WithNumber(D0.Id64, 5), CheckMemory.NotRecorded, CheckMemory.BySpansh, "not found by Spansh's system search");
        view = reach.Now();
        Assert.Equal([HeatmapReach.Recorded, HeatmapReach.Recorded], view.Systems.Select(system => system.State));

        // A jump of 11 ly: listed from what was read round the commander, without reading a file again.
        double[] next = [-330, 25, -290];
        Line(At(next, "Next Door"));
        await reach.Pending;
        view = reach.Now();
        Assert.Equal([(D1.Name, 11.18, HeatmapReach.Recorded), (D0.Name, 27.21, HeatmapReach.Recorded)], Listed(view));
        Assert.Equal(1, reach.SectorReads);

        // A range of 100 ly typed: the whole cube is inside the sphere, so its gaps are probably in reach. The sphere reaches
        // into the layer below, so the files it touches are read.
        reach.RangeLy = 100;
        await reach.Pending;
        view = reach.Now();
        Assert.Equal((HeatmapReach.FromYou, (double?)100), (view.From, view.RangeLy));
        Assert.Equal([D1.Name, D0.Name, D3.Name, D7.Name], view.Systems.Select(system => system.Name));
        Assert.Equal(D8Gaps(HeatmapReach.Probably, 23.45).Select(gap => gap with { State = gap.Name.EndsWith("-5", StringComparison.Ordinal) ? HeatmapReach.NotRecorded : null }), view.MustExist);
        Assert.Equal(0L, view.InLargeCubes);
        Assert.True(reach.SectorReads > 1);
        Assert.Empty(net.Asked);
    }

    [Fact]
    public async Task The_gaps_of_a_cube_wider_than_the_sphere_are_counted_not_listed()
    {
        // Three more systems of Wredguia's sector on this PC, each in one of the game's cubes round the commander: a c cube
        // (40 ly wide, its system numbered 1), an f cube (320 ly, numbered 3) and the h cube (the whole sector, 1,280 ly,
        // numbered 2). Below each number, the numbers that must exist: 1, 3 and 2 of them.
        CubePlace c = new(38, 32, 18, 25, 1, 19, 2, 1), f = new(38, 32, 18, 3, 0, 2, 5, 3), h = new(38, 32, 18, 0, 0, 0, 7, 2);
        HeatmapReach reach = Start();
        var told = new List<ReachNews>();
        reach.News += news =>
        {
            lock (told) told.Add(news);
        };
        await Detailed((ProcGenNames.SystemName(c)!, c.Address, -310, 50, -270), (ProcGenNames.SystemName(f)!, f.Address, -100, 250, -150),
            (ProcGenNames.SystemName(h)!, h.Address, -1000, 600, -900));
        await Started(reach);

        // A jump's reach of 30 ly (a sphere 60 ly wide) touches all four cubes round the commander. Only the c cube is no
        // wider than the sphere: its gap may be in reach, listed at its cube's middle. The gaps of the d cube (80 ly), the f
        // cube and the h cube would be rows "maybe" hundreds of light years off: counted apart, and not in "must exist".
        ReachView view = reach.Now();
        Assert.Equal([new ReachMustExist(ProcGenNames.SystemName(c with { Number = 0 })!, (c with { Number = 0 }).Address, -325, 35, -285, 20, 23.45, HeatmapReach.Maybe)],
            view.MustExist);
        Assert.Equal((0L, 4L + 3 + 2), (view.MoreMustExist, view.InLargeCubes));
        lock (told) Assert.Equal((HeatmapReach.Done, 1L, 9L), (told[^1].State, told[^1].MustExist, told[^1].InLargeCubes));
        Assert.Equal(1, reach.SectorReads);

        // A reach of 100 ly holds the whole d cube and the c cube: their gaps are probably in reach. The f and h cubes,
        // wider than 200 ly, are still only counted.
        reach.RangeLy = 100;
        await reach.Pending;
        view = reach.Now();
        Assert.Equal([.. D8Gaps(HeatmapReach.Probably, 12.25), new ReachMustExist(ProcGenNames.SystemName(c with { Number = 0 })!, (c with { Number = 0 }).Address, -325, 35, -285, 20, 23.45, HeatmapReach.Probably)],
            view.MustExist);
        Assert.Equal((0L, 3L + 2), (view.MoreMustExist, view.InLargeCubes));
        Assert.Empty(net.Asked);
    }

    // ------------------------------------------------------------ outside it: Spansh

    [Fact]
    public async Task Outside_the_detailed_map_one_request_to_Spansh_after_each_arrival_through_the_3D_maps_gate_and_budget()
    {
        HeatmapReach reach = Start();

        // Studio starts outside the detailed map: nothing is asked by itself, and the page says when it will be.
        await Started(reach);
        ReachView view = reach.Now();
        Assert.Equal((HeatmapReach.Done, (string?)null, 0), (view.State, view.Source, view.Systems.Count));
        Assert.StartsWith("You are outside the detailed map, so Studio asks Spansh for the systems within reach, after your next jump", view.Problem, StringComparison.Ordinal);
        Assert.Empty(net.Asked);

        // A jump: one request round the commander, one of the 3D map's own.
        net.Then(Around(2, D1, D0));
        Line(At(Home, "Practice Home"));
        await reach.Pending;
        Assert.Equal([AskedAround(Home, "30")], net.Asked);
        Assert.Equal(1, survey!.Used);
        view = reach.Now();
        Assert.Equal((HeatmapReach.Done, HeatmapReach.FromSpansh, false, (string?)null), (view.State, view.Source, view.Capped, view.Problem));
        Assert.Equal([(D1.Name, 5.0, HeatmapReach.Recorded), (D0.Name, 15.22, HeatmapReach.Recorded)], Listed(view));
        Assert.Empty(view.MustExist);   // the cube reaches out of the sphere, where Spansh was not asked

        // The same place again (the game started again there), and a smaller range: answered from what Spansh said.
        Line(At(Home, "Practice Home", "Location"));
        reach.RangeLy = 10;
        await reach.Pending;
        Assert.Single(net.Asked);
        Assert.Equal([(D1.Name, 5.0, HeatmapReach.Recorded)], Listed(reach.Now()));

        // A range of 100 ly reaches further than Spansh was asked: asked again. The cube lies wholly inside, and the answer
        // is whole, so its gaps are certain: probably in reach.
        net.Then(Around(4, D1, D0, D3, D7));
        reach.RangeLy = 100;
        await reach.Pending;
        Assert.Equal(AskedAround(Home, "100"), net.Asked[^1]);
        view = reach.Now();
        Assert.Equal(4, view.Systems.Count);
        Assert.Equal(D8Gaps(HeatmapReach.Probably, 12.25), view.MustExist);

        // Spansh knows more there than it sends at once: said so, and no gaps (the cube's other systems may be among those
        // left out).
        double[] busy = [-1000, 20, -295];
        net.Then(Around(600, ("Busy 1", 0, -1001, 20, -295), ("Busy 2", 0, -1002, 20, -295)));
        Line(At(busy, "Busy Place"));
        await reach.Pending;
        view = reach.Now();
        Assert.Equal((true, 2, 0), (view.Capped, view.Systems.Count, view.MustExist.Count));
        Assert.Equal(3, survey.Used);

        // Spansh cannot be reached: said, and not asked again by itself.
        Line(At([-2000, 20, -295], "Far Place"));
        await reach.Pending;
        view = reach.Now();
        Assert.Equal((HeatmapReach.Done, (string?)Unreachable, 0), (view.State, view.Problem, view.Systems.Count));
        Assert.Equal(4, net.Asked.Count);
    }

    [Fact]
    public async Task Within_reach_leaves_the_sessions_last_50_requests_to_the_route_check_and_Nav_scan()
    {
        HeatmapReach reach = Start();
        await Started(reach);
        // 150 of the session's 200 requests spent on counts that found the PC offline: 50 left, which is still enough.
        for (int count = 0; count < 150; count++) await survey!.Count("cube", -167.09, 26.72, -55.38, go: true);
        net.Then(Around(1, D1));
        Line(At(Home, "Practice Home"));
        await reach.Pending;
        Assert.Equal((151, AskedAround(Home, "30")), (survey!.Used, net.Asked[^1]));

        // 49 left: nothing is asked, and the page says why.
        Line(At([-1000, 20, -295], "Busy Place"));
        await reach.Pending;
        Assert.Equal((151, 151), (survey.Used, net.Asked.Count));
        ReachView view = reach.Now();
        Assert.Equal((HeatmapReach.Done, 0), (view.State, view.Systems.Count));
        Assert.StartsWith("Fewer than 50 of the 3D map's 200 requests to Spansh are left this session, and Within reach leaves those for the route check and Nav scan", view.Problem, StringComparison.Ordinal);

        // Inside the detailed map it still lists, from this PC.
        await Detailed();
        Line(At(Home, "Practice Home"));
        await reach.Pending;
        Assert.Equal((HeatmapReach.FromDetailed, 2, (string?)null), (reach.Now().Source, reach.Now().Systems.Count, reach.Now().Problem));
        Assert.Equal(151, net.Asked.Count);
    }

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

    /// <summary>Waits, ten seconds at most, for what goes on by itself to come to this.</summary>
    static async Task Until(Func<bool> done)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "gave up waiting");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_request_waits_its_turn_at_the_3D_maps_gate_and_a_newer_arrival_takes_its_place_unsent()
    {
        var held = new Held(net);
        HeatmapReach reach = Start(held);
        var told = new List<ReachNews>();
        reach.News += news =>
        {
            lock (told) told.Add(news);
        };
        await Started(reach);

        // A count of the 3D map's under way, held on its way to Spansh: the map's gate is taken.
        Task<CountAnswer> counting = survey!.Count("cube", -167.09, 26.72, -55.38, go: true);
        await held.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // A jump: its request waits its turn, and says so.
        Line(At(Home, "Practice Home"));
        await Until(() => reach.Now().State == HeatmapReach.Waiting);
        Assert.Equal((HeatmapReach.Waiting, (double?)30), (reach.Now().State, reach.Now().RangeLy));

        // Another jump before its turn came: the first is never sent; the second takes its turn.
        double[] next = [-330, 25, -290];
        Line(At(next, "Next Door"));
        await Until(() => reach.Now() is { State: HeatmapReach.Waiting, Centre.X: -330 });
        net.ThenOffline().Then(Around(1, D1));
        held.LetGo.SetResult();
        await counting;
        await reach.Pending;
        Assert.Equal(AskedAround(next, "30"), Assert.Single(net.Asked, asked => asked.Contains("distance", StringComparison.Ordinal)));
        Assert.Equal((2, HeatmapReach.Done, 1), (survey.Used, reach.Now().State, reach.Now().Systems.Count));
        lock (told) Assert.Contains(told, news => news.State == HeatmapReach.Waiting);
    }

    // ------------------------------------------------------------ the switch and the range

    [Fact]
    public async Task Switched_off_nothing_is_listed_or_asked_and_switched_on_the_place_is_listed_at_once()
    {
        // Settings of the 3D map's kept by an earlier session: nothing here writes over them.
        new Settings(SettingsFile).Set("heatmap", new HeatmapSettings { CheckTargets = false, RecordedDays = 7 });
        HeatmapReach reach = Start();
        Assert.True(reach.On);   // on unless the player turns it off
        await Started(reach);

        reach.On = false;
        Line(At(Home, "Practice Home"));
        await reach.Pending;

        Assert.Empty(net.Asked);
        ReachView view = reach.Now();
        Assert.Equal((HeatmapReach.Off, false, (double?)30, HeatmapReach.FromShip, (ReachCentre?)null, (string?)null, 0, 0),
            (view.State, view.On, view.RangeLy, view.From, view.Centre, view.Problem, view.Systems.Count, view.MustExist.Count));
        JsonNode saved = JsonNode.Parse(File.ReadAllText(SettingsFile))!["heatmap"]!;
        Assert.Equal((false, false, 7), ((bool)saved["withinReach"]!, (bool)saved["checkTargets"]!, (int)saved["recordedDays"]!));
        Assert.False(Start().On);   // the next session too

        // On again: the place the commander is in is listed at once (the player's click asks for it).
        net.Then(Around(1, D1));
        reach.On = true;
        await reach.Pending;
        Assert.Equal([AskedAround(Home, "30")], net.Asked);
        Assert.Equal([(D1.Name, 5.0, HeatmapReach.Recorded)], Listed(reach.Now()));
    }

    [Fact]
    public async Task The_range_is_the_ships_for_the_load_it_carries_or_the_one_the_player_typed()
    {
        HeatmapReach reach = Start();
        Assert.Equal(HeatmapReach.Idle, reach.Now().State);

        // No ship known yet: nothing to list, and the page says why.
        Line(At(Home, "Practice Home", "Location"), replay: true);
        game.RaiseChanged();
        await reach.Pending;
        Assert.Equal((HeatmapReach.Idle, (double?)null), (reach.Now().State, reach.Now().RangeLy));
        Assert.StartsWith("Studio does not know your ship's jump range yet", reach.Now().Problem, StringComparison.Ordinal);

        // The ship with its weight, its tank and 32 tons on board: its range for that load.
        game.SetCargo(32, []);
        Line("""{ "timestamp":"2026-10-15T09:00:00Z", "event":"Loadout", "Ship":"krait_mkii", "ShipID":1, "MaxJumpRange":40, "UnladenMass":400, "FuelCapacity":{ "Main":32, "Reserve":0.5 }, "CargoCapacity":64, "Modules":[] }""");
        double laden = ShipMath.JumpRanges(game.MaxJumpRange, game.UnladenMass, game.FuelCapacity, game.FsdFuel, game.BoosterLy, 32).Laden;
        Assert.Equal(laden, reach.ShipRangeLy);
        string ly = laden.ToString("0.##", CultureInfo.InvariantCulture);
        net.Then(Around(1, D1));
        game.RaiseChanged();   // the range changed: listed (here at once; in the app once it has settled)
        await reach.Pending;
        Assert.Equal([AskedAround(Home, ly)], net.Asked);
        Assert.Equal((HeatmapReach.FromShip, (double?)laden), (reach.Now().From, reach.Now().RangeLy));

        // More cargo: a shorter range, answered from what Spansh said. Cargo sold: a longer one, asked.
        game.SetCargo(64, []);
        game.RaiseChanged();
        await reach.Pending;
        Assert.Single(net.Asked);
        Assert.True(reach.Now().RangeLy < laden);
        game.SetCargo(0, []);
        net.Then(Around(1, D1));
        game.RaiseChanged();
        await reach.Pending;
        Assert.Equal(2, net.Asked.Count);
        Assert.True(reach.Now().RangeLy > laden);

        // A range typed by the player, kept in settings.json; empty again for the ship's.
        foreach (double wrong in new[] { 0.5, 200.5, double.NaN })
            Assert.StartsWith(HeatmapReach.NoSuchRange, Assert.Throws<ArgumentException>(() => reach.RangeLy = wrong).Message, StringComparison.Ordinal);
        reach.RangeLy = 12.5;
        await reach.Pending;
        Assert.Equal((HeatmapReach.FromYou, (double?)12.5, 2), (reach.Now().From, reach.Now().RangeLy, net.Asked.Count));
        Assert.Equal(12.5, (double)JsonNode.Parse(File.ReadAllText(SettingsFile))!["heatmap"]!["reachLy"]!);
        Assert.Equal(12.5, Start().RangeLy);
        reach.RangeLy = null;
        await reach.Pending;
        Assert.Equal(HeatmapReach.FromShip, reach.Now().From);
    }

    /// <summary>Cargo.json as the game writes it, read as Studio reads it (GameWatcher).</summary>
    void Hold(string json)
    {
        using JsonDocument file = JsonDocument.Parse(json);
        var (tons, items, ship) = CargoItem.ParseHold(file.RootElement);
        game.SetCargo(tons, items, ship);
    }

    [Fact]
    public async Task Getting_into_the_SRV_keeps_the_ships_range_and_asks_Spansh_nothing()
    {
        HeatmapReach reach = Start();
        // Studio starts with the ship's hold holding 32 tons, outside the detailed map; then a jump lists, with one request.
        Hold("""{ "timestamp":"2026-10-15T09:00:00Z", "event":"Cargo", "Vessel":"Ship", "Count":32, "Inventory":[ { "Name":"gold", "Count":32, "Stolen":0 } ] }""");
        Line("""{ "timestamp":"2026-10-15T09:00:00Z", "event":"Loadout", "Ship":"krait_mkii", "ShipID":1, "MaxJumpRange":40, "UnladenMass":400, "FuelCapacity":{ "Main":32, "Reserve":0.5 }, "CargoCapacity":64, "Modules":[] }""", replay: true);
        Line(At(Home, "Practice Home", "Location"), replay: true);
        game.RaiseChanged();
        await reach.Pending;
        net.Then(Around(1, D1));
        Line(At(Home, "Practice Home"));
        game.RaiseChanged();
        await reach.Pending;
        double laden = reach.ShipRangeLy;
        Assert.Equal(ShipMath.JumpRanges(game.MaxJumpRange, game.UnladenMass, game.FuelCapacity, game.FsdFuel, game.BoosterLy, 32).Laden, laden);
        Assert.Equal((HeatmapReach.Done, (double?)laden, 1), (reach.Now().State, reach.Now().RangeLy, net.Asked.Count));

        // The commander gets into the SRV: Cargo.json now describes the SRV, an empty hold to the rest of Studio. The ship
        // parked meanwhile still carries its 32 tons: its range stays, and nothing is listed again or asked.
        Hold("""{ "timestamp":"2026-10-15T09:10:00Z", "event":"Cargo", "Vessel":"SRV", "Count":0, "Inventory":[] }""");
        game.RaiseChanged();
        await reach.Pending;
        Assert.Equal((0, 32), (game.CargoCount, game.ShipCargoCount));
        Assert.Equal(laden, reach.ShipRangeLy);
        Assert.Equal((HeatmapReach.Done, (double?)laden), (reach.Now().State, reach.Now().RangeLy));
        Assert.Single(net.Asked);

        // Back in the ship, with the same hold: still nothing.
        Hold("""{ "timestamp":"2026-10-15T09:20:00Z", "event":"Cargo", "Vessel":"Ship", "Count":32, "Inventory":[ { "Name":"gold", "Count":32, "Stolen":0 } ] }""");
        game.RaiseChanged();
        await reach.Pending;
        Assert.Equal((laden, 1), (reach.ShipRangeLy, net.Asked.Count));
    }
}
