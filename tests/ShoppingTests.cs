using System.Text.Json;
using System.Text.Json.Nodes;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Loadouts;
using EliteStudio.Market;
using EliteStudio.Nav;
using static System.FormattableString;
using WebRequest = EliteStudio.App.WebRequest;     // System.Net has older types with the same names
using WebResponse = EliteStudio.App.WebResponse;

namespace EliteStudio.Tests;

/// <summary>
/// A saved build handed on to the other studios: what the ship being flown lacks to become it, the
/// stations to buy that at in the fewest stops, and its engineering pinned in the planner. The ship
/// is the owner's engineered Mandalay from the "Engineered" recording, parked in Sol, with Studio
/// Market's standard station filters (5,000 ls from the star at most, all surface stations, no fleet
/// carriers). Spansh is played back by <see cref="RecordedServices"/>: its lists of names are the
/// recorded ones, cut down to a dozen modules and ten ships, and each test says whether its stations
/// are Spansh's own answers or made up for the test.
/// </summary>
public sealed class ShoppingTests : IDisposable
{
    const string Search = "https://spansh.co.uk/api/stations/search";
    const string InSol = """{ "timestamp":"2026-10-09T12:00:00Z", "event":"Location", "Docked":false, "StarSystem":"Sol", "SystemAddress":10477373803, "StarPos":[0.0,0.0,0.0] }""";
    static readonly string Mandalay = PracticeGame.Line(PracticeGame.Recording("Journal.2026-10-08T080236.01.log"), "Loadout");

    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioShopping-").FullName;
    readonly RecordedServices net = new();
    readonly MarketData data;
    readonly Spansh noRoutes = new(new SpanshStandIn());   // Studio Nav's own client: no route is plotted here
    readonly HaulRig rig = new([InSol]);
    readonly WebServer web;
    readonly EngineeringPlanner planner;
    readonly NavRoutes nav;

    public ShoppingTests()
    {
        data = new MarketData(net);
        web = new WebServer(temp);
        var settings = new Settings(Path.Combine(temp, "settings.json"));
        planner = new EngineeringPlanner(rig.Watcher.State, settings);
        nav = NavApi.Map(web, rig.Watcher.State, settings, noRoutes);
        Finder finder = FindApi.Map(web, rig.Watcher.State, rig.Hauling, data, () => RecordedServices.RecordedUtc);
        var loadouts = new StudioLoadouts(Builds);
        loadouts.Map(web, rig.Watcher.State);
        loadouts.MapHandOffs(web, rig.Watcher.State, planner, finder);
        rig.Play(Mandalay);   // the game reports the ship
    }

    public void Dispose()
    {
        web.Dispose();
        rig.Dispose();
        noRoutes.Dispose();
        data.Dispose();
        Directory.Delete(temp, recursive: true);
    }

    string Builds => Path.Combine(temp, "loadouts");

    async Task<(int Status, JsonElement Json)> Call(string method, string route, object? body = null)
    {
        string[] parts = route.Split('?', 2);
        WebResponse response = await web.Handle(new WebRequest(method, "/api/" + parts[0], WebServer.ParseQuery(parts.Length == 2 ? parts[1] : ""), body is null ? "" : JsonSerializer.Serialize(body)));
        return (response.Status, JsonSerializer.Deserialize<JsonElement>(response.Body));
    }

    /// <summary>A call that must succeed.</summary>
    async Task<JsonElement> Ok(string method, string route, object? body = null)
    {
        var (status, json) = await Call(method, route, body);
        Assert.True(status == 200, $"{method} {route} answered {status}: {json}");
        return json;
    }

    static ShipLoadout Parse(JsonNode loadout) => ShipLoadout.Parse(JsonSerializer.SerializeToElement(loadout));

    /// <summary>The Mandalay's own Loadout event with some of its modules changed.</summary>
    static JsonObject Changed(Action<JsonArray> change)
    {
        JsonObject loadout = JsonNode.Parse(Mandalay)!.AsObject();
        change(loadout["Modules"]!.AsArray());
        return loadout;
    }

    /// <summary>Puts a module in a slot as it leaves the shop: whatever was there goes, and its engineering with it.</summary>
    static void Fit(JsonArray modules, string slot, string item)
    {
        if (modules.OfType<JsonObject>().FirstOrDefault(module => (string?)module["Slot"] == slot) is { } fitted) modules.Remove(fitted);
        modules.Add(new JsonObject { ["Slot"] = slot, ["Item"] = item, ["On"] = true, ["Priority"] = 0, ["Health"] = 1.0 });
    }

    /// <summary>Saves a build the way the designer does; its name is its id.</summary>
    async Task Save(string name, JsonNode loadout) => await Ok("POST", "loadouts/designer/save", new { name, loadout });

    static string[] Labels(JsonElement list) => [.. list.GetProperty("modules").EnumerateArray().Select(module => $"{module.GetProperty("count")} × {module.GetProperty("label").GetString()}")];

    /// <summary>The questions put to Spansh's stations search so far, without their address.</summary>
    string[] Questions() => [.. net.Asked.Where(asked => asked.StartsWith(Search, StringComparison.Ordinal)).Select(asked => asked[(Search.Length + 1)..])];

    // Made-up stations, in the layout of Spansh's answers.
    static string Answer(params string[] stations) => $$"""{"count":{{stations.Length}},"results":[{{string.Join(",", stations)}}]}""";

    static string Station(string name, string system, double x, double y, double z, string[] modules, string[]? ships = null) => Invariant(
        $$"""{"name":"{{name}}","system_name":"{{system}}","system_id64":{{1000 + (long)x}},"system_x":{{x}},"system_y":{{y}},"system_z":{{z}},"type":"Coriolis Starport","distance":{{Math.Sqrt(x * x + y * y + z * z)}},"distance_to_arrival":250,"large_pads":4,"medium_pads":8,"small_pads":6,"outfitting_updated_at":"2026-10-09T08:00:00Z","modules":[{{string.Join(",", modules.Select(symbol => $$"""{"ed_symbol":"{{symbol}}","price":1000}"""))}}],"ships":[{{string.Join(",", (ships ?? []).Select(ship => $$"""{"name":"{{ship}}","price":50000}"""))}}]}""");

    static Seller Shop(string name, double x, string[] modules, string[]? ships = null) => new(
        name, name + " System", 0, [x, 0, 0], Math.Abs(x), 250, "L", "Coriolis Starport", null,
        modules.ToDictionary(module => module, _ => 1000L, StringComparer.OrdinalIgnoreCase), (ships ?? []).ToDictionary(ship => ship, _ => 50000L, StringComparer.OrdinalIgnoreCase));

    static Lacking Need(string item, int count = 1) => new(item, item.ToUpperInvariant(), "", 1, "A", count);

    /// <summary>A stop as one line: the station, the leg to it, and what is bought there.</summary>
    static string Line(ShoppingStop stop) => $"{stop.Station} {stop.Ly} ly: {string.Join(", ", stop.Buy.Select(thing => $"{thing.Count} × {thing.What}"))}";

    // ------------------------------------------------------------ what the ship lacks

    [Fact]
    public void What_the_ship_lacks_is_counted_kind_by_kind_and_only_what_outfitting_sells_counts()
    {
        ShipLoadout flown = Parse(JsonNode.Parse(Mandalay)!);
        // The build: heat sink launchers where the chaff and the countermeasure were (the ship has one launcher already),
        // a lighter life support, the same drive as it leaves the shop, and another paint job.
        ShipLoadout build = Parse(Changed(modules =>
        {
            Fit(modules, "TinyHardpoint3", "hpt_heatsinklauncher_turret_tiny");
            Fit(modules, "TinyHardpoint4", "Hpt_HeatSinkLauncher_Turret_Tiny");
            Fit(modules, "LifeSupport", "int_lifesupport_size4_class2");
            Fit(modules, "FrameShiftDrive", "int_hyperdrive_overcharge_size5_class5");
            Fit(modules, "PaintJob", "paintjob_mandalay_tactical_blue");
        }));

        ShoppingList list = Shopping.List(build, flown.Modules, newHull: false);

        // Three launchers in the build, one on the ship: two to buy. The drive is the same module with or without an
        // engineer's work, and paint is not bought at outfitting. Utility mounts come before the core, as on the game's screen.
        Assert.Equal(new[]
        {
            new Lacking("hpt_heatsinklauncher_turret_tiny", "Heat Sink Launcher", "", 0, "I", 2),
            new Lacking("int_lifesupport_size4_class2", "Life Support", "", 4, "D", 1),
        }, list.Modules);
        Assert.Equal(new[] { "I Heat Sink Launcher", "4D Life Support" }, list.Modules.Select(module => module.Label));
        Assert.Equal(("Mandalay", false, 5), (list.ShipType, list.NewHull, list.Engineered));   // six engineered modules on the ship, the drive's work gone

        Assert.Empty(Shopping.List(flown, flown.Modules, newHull: false).Modules);
        // The other way round the ship lacks what the build took out, and a weapon says how it is mounted.
        Assert.Equal(new[] { "I Chaff Launcher", "F Electronic Countermeasure", "4E Life Support" }, Shopping.List(flown, build.Modules, newHull: false).Modules.Select(module => module.Label));
        Assert.Equal(("2E Advanced Multi-Cannon, fixed", 4), (Shopping.List(flown, [], newHull: true).Modules[0].Label, Shopping.List(flown, [], newHull: true).Modules[0].Count));
        // A starter ship's free module is the plain one.
        ShipLoadout starter = Parse(Changed(modules => Fit(modules, "LifeSupport", "int_lifesupport_size4_class2_free")));
        Assert.DoesNotContain(Shopping.List(build, starter.Modules, newHull: false).Modules, module => module.Name == "Life Support");
    }

    // ------------------------------------------------------------ choosing the stops

    [Fact]
    public void One_station_that_sells_everything_is_the_whole_trip_however_near_the_others_are()
    {
        Lacking[] list = [Need("a"), Need("b", 2), Need("c")];
        Seller[] found = [Shop("Near One", 5, ["a", "b"]), Shop("Near Two", 8, ["c"]), Shop("Has It All", 90, ["A", "B", "C", "d"]), Shop("Also All", 120, ["a", "b", "c"])];

        var (stops, notFound) = Shopping.Choose(list, null, found, [0, 0, 0]);

        ShoppingStop stop = Assert.Single(stops);
        Assert.Equal("Has It All 90 ly: 1 × 1A A, 2 × 1A B, 1 × 1A C", Line(stop));
        Assert.Equal("Dock at Has It All and buy 1A A, 2 × 1A B, 1A C.", stop.Note);
        Assert.Equal(("Has It All System", 90, 0, 0, 250, "L", "Coriolis Starport"), (stop.System, stop.X, stop.Y, stop.Z, stop.StarLs, stop.Pad, stop.Type));
        Assert.Equal(new Purchase("1A B", 2, 1000), stop.Buy[1]);
        Assert.Empty(notFound);
    }

    [Fact]
    public void Without_such_a_station_the_fewest_stops_are_picked_and_flown_nearest_first()
    {
        // No station sells all six. "Big" sells the most (four) and is picked first. For what is left, "East" and "West"
        // sell one thing each, and so does "Tiny", which is nearer than "East": but "East" sells more of the whole list.
        // Between them the two sell everything "Big" was picked for, so "Big" goes again, and two stops are left of three.
        Lacking[] list = [Need("a"), Need("b"), Need("c"), Need("d"), Need("e"), Need("f")];
        Seller[] found = [Shop("Big", 100, ["a", "b", "c", "f"]), Shop("East", 10, ["d", "a", "b"]), Shop("West", -20, ["e", "c", "f"]), Shop("Tiny", 2, ["d"])];
        var (stops, notFound) = Shopping.Choose(list, null, found, [0, 0, 0]);
        Assert.Equal(new[] { "East 10 ly: 1 × 1A A, 1 × 1A B, 1 × 1A D", "West 30 ly: 1 × 1A C, 1 × 1A E, 1 × 1A F" }, stops.Select(Line));
        Assert.Empty(notFound);

        // The same list where the far station is the only one with "f": three stops, the far one last, each thing bought at the first stop that has it.
        found = [Shop("Big", 100, ["a", "b", "c", "f"]), Shop("East", 10, ["d", "a", "b"]), Shop("West", -20, ["e", "c"]), Shop("Tiny", 2, ["d"])];
        (stops, notFound) = Shopping.Choose(list, null, found, [0, 0, 0]);
        Assert.Equal(new[] { "East 10 ly: 1 × 1A A, 1 × 1A B, 1 × 1A D", "West 30 ly: 1 × 1A C, 1 × 1A E", "Big 120 ly: 1 × 1A F" }, stops.Select(Line));

        // Of two stations that sell as much, the one nearer the ship. What nobody sells is said, with how many.
        (stops, notFound) = Shopping.Choose([Need("a"), Need("z", 3)], null, [Shop("Far", 50, ["a"]), Shop("Close", -7, ["a"])], [0, 0, 0]);
        Assert.Equal(new[] { "Close 7 ly: 1 × 1A A" }, stops.Select(Line));
        Assert.Equal(new[] { "3 × 1A Z" }, notFound);

        // Nothing found at all, and nothing to find.
        Assert.Equal((0, 1), (Shopping.Choose([Need("a")], null, [], [0, 0, 0]).Stops.Count, Shopping.Choose([Need("a")], null, [], [0, 0, 0]).NotFound.Count));
        Assert.Equal((0, 0), (Shopping.Choose([], null, found, [0, 0, 0]).Stops.Count, Shopping.Choose([], null, found, [0, 0, 0]).NotFound.Count));

        // Where the ship is has not been said: the stations' own distances from it decide the first stop.
        (stops, _) = Shopping.Choose([Need("a"), Need("b")], null, [Shop("Far", 50, ["a"]), Shop("Close", -7, ["b"])], null);
        Assert.Equal(new[] { "Close 7 ly: 1 × 1A B", "Far 57 ly: 1 × 1A A" }, stops.Select(Line));
    }

    [Fact]
    public void Another_ship_is_bought_first_at_the_shipyard_that_sells_the_most_of_the_rest()
    {
        Lacking[] list = [Need("a"), Need("b"), Need("c")];
        Seller[] found =
        [
            Shop("Parts", 5, ["a", "b", "c"]), Shop("Yard Only", 10, [], ["Hauler"]), Shop("Yard And Parts", 60, ["a", "b"], ["hauler", "Python"]), Shop("Other Yard", 3, ["a", "b", "c"], ["Python"]),
        ];

        // Nearer stations sell every module, but not the ship: the first stop is the shipyard that also sells two of the three.
        var (stops, notFound) = Shopping.Choose(list, "Hauler", found, [0, 0, 0]);
        Assert.Equal(new[] { "Yard And Parts 60 ly: 1 × the Hauler itself, 1 × 1A A, 1 × 1A B", "Other Yard 57 ly: 1 × 1A C" }, stops.Select(Line));
        Assert.Equal("Dock at Yard And Parts and buy the Hauler itself, 1A A, 1A B.", stops[0].Note);
        Assert.Equal(new Purchase("the Hauler itself", 1, 50000), stops[0].Buy[0]);
        Assert.Empty(notFound);

        // A ship no station found sells: said first, and the modules are planned all the same.
        (stops, notFound) = Shopping.Choose(list, "Anaconda", found, [0, 0, 0]);
        Assert.Equal(new[] { "Other Yard 3 ly: 1 × 1A A, 1 × 1A B, 1 × 1A C" }, stops.Select(Line));
        Assert.Equal(new[] { "the Anaconda itself" }, notFound);

        // Only the ship to buy.
        (stops, notFound) = Shopping.Choose([], "Python", found, [0, 0, 0]);
        Assert.Equal(new[] { "Other Yard 3 ly: 1 × the Python itself" }, stops.Select(Line));
    }

    // ------------------------------------------------------------ the routes

    [Fact]
    public async Task The_shopping_list_of_a_saved_build_is_what_the_ship_flown_lacks()
    {
        await Save("Explorer", Changed(modules =>
        {
            Fit(modules, "Armour", "mandalay_armour_grade3");
            Fit(modules, "Slot01_Size6", "int_fuelscoop_size6_class5");
            Fit(modules, "Slot04_Size4", "int_shieldgenerator_size4_class5");
        }));
        JsonElement list = await Ok("GET", "loadouts/shopping?id=Explorer");
        Assert.Equal(("Mandalay", false, 5), (list.GetProperty("shipType").GetString(), list.GetProperty("newHull").GetBoolean(), list.GetProperty("engineered").GetInt32()));
        Assert.Equal(new[] { "1 × 1I Military Grade Composite", "1 × 6A Fuel Scoop", "1 × 4A Shield Generator" }, Labels(list));
        Assert.Equal(
            """{"item":"mandalay_armour_grade3","name":"Military Grade Composite","mount":"","size":1,"rating":"I","count":1,"label":"1I Military Grade Composite"}""",
            JsonSerializer.Serialize(list.GetProperty("modules")[0]));

        // A build of another ship: that ship comes with its plainest modules (the designer's empty hull), so its
        // life support, sensors and fuel tank are not on the list, and neither is the cargo hatch.
        Directory.CreateDirectory(Builds);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "loadouts", "type9-design.json"), Path.Combine(Builds, "Hauling.json"));
        list = await Ok("GET", "loadouts/shopping?id=Hauling");
        Assert.Equal(("Type-9 Heavy", true, 10), (list.GetProperty("shipType").GetString(), list.GetProperty("newHull").GetBoolean(), list.GetProperty("engineered").GetInt32()));
        Assert.Equal(new[]
        {
            "1 × 2F Multi-Cannon, gimballed", "1 × 2D Beam Laser, fixed", "2 × A Shield Booster", "1 × 1I Military Grade Composite", "1 × 6A Power Plant", "1 × 7A Thrusters",
            "1 × 6A Frame Shift Drive", "1 × 6A Power Distributor", "2 × 8E Cargo Rack", "1 × 7A Shield Generator", "1 × 5D Hull Reinforcement Package", "1 × 5H Guardian FSD Booster",
        }, Labels(list));

        // No such build; and no ship known yet (a Studio that has never seen the game).
        var (status, json) = await Call("GET", "loadouts/shopping?id=Nothing");
        Assert.Equal((404, "That saved build was not found."), (status, json.GetProperty("error").GetString()));
        using var fresh = new WebServer(temp);
        var unseen = new StudioLoadouts(Builds);
        unseen.Map(fresh, new GameState());
        unseen.MapHandOffs(fresh, new GameState(), planner, null!);
        WebResponse answer = await fresh.Handle(new WebRequest("GET", "/api/loadouts/shopping", new Dictionary<string, string> { ["id"] = "Explorer" }, ""));
        Assert.Equal((409, "No ship is known yet. Start Elite Dangerous and board your ship."), (answer.Status, JsonSerializer.Deserialize<JsonElement>(answer.Body).GetProperty("error").GetString()));
    }

    // The rest of every question from Sol with the standard filters, after what is looked for: the station types the
    // filters let through (in Spansh's words), the distance from the star, nearest first, ten stations, Sol by where it lies.
    const string FromSol = "\"type\":" + """{"value":["Coriolis Starport","Orbis Starport","Ocellus Starport","Dodec Starport","Outpost","Asteroid base","Mega ship","Planetary Port","Planetary Outpost","Surface Settlement","Settlement"]},"distance_to_arrival":{"comparison":"<=>","value":[0,5000]}},"sort":[{"distance":{"direction":"asc"}}],"size":10,"page":0,"reference_coords":{"x":0,"y":0,"z":0}}""";

    [Fact]
    public async Task A_build_for_the_ship_flown_is_bought_at_the_nearest_station_that_really_sells_all_of_it_and_the_trip_goes_to_Studio_Nav()
    {
        // (Spansh's own answer, recorded on 9 October 2026.) Stronger armour, a bigger fuel scoop and a bigger shield generator for the Mandalay.
        await Save("Explorer", Changed(modules =>
        {
            Fit(modules, "Armour", "mandalay_armour_grade3");
            Fit(modules, "Slot01_Size6", "int_fuelscoop_size6_class5");
            Fit(modules, "Slot04_Size4", "int_shieldgenerator_size4_class5");
        }));

        JsonElement trip = await Ok("POST", "loadouts/shopping/stops", new { id = "Explorer" });

        // One question: the nearest stations that sell all three, by Spansh's names for them.
        Assert.Equal(new[]
        {
            """{"filters":{"modules":[{"name":{"value":["Military Grade Composite"]},"class":{"value":["1"]},"rating":{"value":["I"]}},{"name":{"value":["Fuel Scoop"]},"class":{"value":["6"]},"rating":{"value":["A"]}},{"name":{"value":["Shield Generator"]},"class":{"value":["4"]},"rating":{"value":["A"]}}],""" + FromSol,
        }, Questions());
        Assert.Equal([Program.UserAgent], net.UserAgents);
        // Spansh's nearest is Li Qing Jao, in Sol itself. It does sell "Military Grade Composite", but for other ships than
        // the Mandalay: a station is taken at the game's own codes for what it sells, so the one stop is the next station,
        // 4.38 ly away, with the prices last reported there.
        Assert.Equal(("Shopping for Explorer", "Sol", 1, false, 0),
            (trip.GetProperty("name").GetString(), trip.GetProperty("from").GetString(), trip.GetProperty("questions").GetInt32(), trip.GetProperty("cutShort").GetBoolean(), trip.GetProperty("notFound").GetArrayLength()));
        JsonElement stop = Assert.Single(trip.GetProperty("stops").EnumerateArray());
        Assert.Equal(
            """{"station":"Stronghold Carrier","system":"Alpha Centauri","address":"1178708478315","x":3.03125,"y":-0.09375,"z":3.15625,"ly":4.38,"starLs":4460.352766,"pad":"L","type":"Stronghold Carrier","updated":"2026-10-09T10:31:00Z","buy":[{"what":"1I Military Grade Composite","count":1,"price":15875299},{"what":"6A Fuel Scoop","count":1,"price":28763610},{"what":"4A Shield Generator","count":1,"price":1610080}],"note":"Dock at Stronghold Carrier and buy 1I Military Grade Composite, 6A Fuel Scoop, 4A Shield Generator."}""",
            JsonSerializer.Serialize(stop));

        // The page hands the stops to Studio Nav just as they came: the trip is the route, from where the ship is, and
        // the next stop says where to dock and what to buy.
        JsonElement view = await Ok("POST", "nav/trip", new
        {
            name = trip.GetProperty("name").GetString(),
            stops = trip.GetProperty("stops").EnumerateArray().Select(at => new
            {
                system = at.GetProperty("system").GetString(), address = at.GetProperty("address").GetString(),
                x = at.GetProperty("x").GetDouble(), y = at.GetProperty("y").GetDouble(), z = at.GetProperty("z").GetDouble(), note = at.GetProperty("note").GetString(),
            }),
        });
        Assert.Equal(new PlotRequest { Kind = PlotRequest.Trip, From = "Sol", To = "Alpha Centauri", Name = "Shopping for Explorer" }, nav.Route!.Request);
        Assert.Equal(new[] { ("Sol", 10477373803, ""), ("Alpha Centauri", 1178708478315, stop.GetProperty("note").GetString()!) }, nav.Route.Hops.Select(hop => (hop.System, hop.Address, hop.Note)));
        Assert.Equal(("Alpha Centauri", 4.4, 1, stop.GetProperty("note").GetString()), (nav.Progress!.Next, nav.Progress.NextLy, nav.Progress.HopsLeft, nav.Progress.NextNote));
        Assert.Equal("trip", view.GetProperty("route").GetProperty("request").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Another_ship_and_its_modules_are_found_at_one_station_when_one_sells_them_all()
    {
        // (Spansh's own answer, recorded on 9 October 2026.) A Hauler for a pilot who flies the Mandalay, with a better drive,
        // a fuel scoop and a Guardian booster: the nearest shipyard that also sells the three is 11.1 ly from Sol.
        JsonObject hauler = JsonNode.Parse((await Ok("GET", "loadouts/designer/start?ship=hauler")).GetProperty("loadout").GetRawText())!.AsObject();
        Fit(hauler["Modules"]!.AsArray(), "FrameShiftDrive", "int_hyperdrive_size2_class5");
        Fit(hauler["Modules"]!.AsArray(), "Slot01_Size3", "int_fuelscoop_size3_class5");
        Fit(hauler["Modules"]!.AsArray(), "Slot04_Size1", "int_guardianfsdbooster_size1");
        await Save("Runabout", hauler);

        JsonElement trip = await Ok("POST", "loadouts/shopping/stops", new { id = "Runabout" });

        Assert.Equal(new[]
        {
            """{"filters":{"modules":[{"name":{"value":["Frame Shift Drive"]},"class":{"value":["2"]},"rating":{"value":["A"]}},{"name":{"value":["Fuel Scoop"]},"class":{"value":["3"]},"rating":{"value":["A"]}},{"name":{"value":["Guardian FSD Booster"]},"class":{"value":["1"]},"rating":{"value":["H"]}}],"ships":[{"name":{"value":["Hauler"]}}],""" + FromSol,
        }, Questions());
        Assert.Empty(trip.GetProperty("notFound").EnumerateArray());
        Assert.Equal(
            """{"station":"Magnus Gateway","system":"EZ Aquarii","address":"22661186987433","x":-4.4375,"y":-9.3125,"z":4.09375,"ly":11.1,"starLs":452.572096,"pad":"L","type":"Coriolis Starport","updated":"2026-10-09T17:38:09Z","buy":[{"what":"the Hauler itself","count":1,"price":52720},{"what":"2A Frame Shift Drive","count":1,"price":160220},{"what":"3A Fuel Scoop","count":1,"price":902950},{"what":"1H Guardian FSD Booster","count":1,"price":405020}],"note":"Dock at Magnus Gateway and buy the Hauler itself, 2A Frame Shift Drive, 3A Fuel Scoop, 1H Guardian FSD Booster."}""",
            JsonSerializer.Serialize(Assert.Single(trip.GetProperty("stops").EnumerateArray())));
    }

    [Fact]
    public async Task A_module_Spansh_files_under_a_special_editions_name_is_asked_for_by_that_name()
    {
        // The game's tables list an "Extended Cargo Rack" under the plain size 5 rack's code, after the plain one, and Spansh
        // calls every size 5 rack by that name. (Seen in its real answers for some twenty everyday modules: the plain 5A power
        // distributor is its "Balanced Power Distributor". The list of names and the station here are made up.)
        await Save("Two racks", Changed(modules => Fit(modules, "Slot02_Size5", "int_cargorack_size5_class1")));   // a second rack where the Guardian booster was
        net.Instead = asked => asked.EndsWith("/field_values/modules", StringComparison.Ordinal) ? """{"values":{"name":["Cargo Rack","Extended Cargo Rack"],"class":[5],"rating":["E"]}}"""
            : asked.StartsWith(Search, StringComparison.Ordinal) ? Answer(Station("Rack Shop", "Racks", 6, 0, 8, ["Int_CargoRack_Size5_Class1"])) : null;

        JsonElement trip = await Ok("POST", "loadouts/shopping/stops", new { id = "Two racks" });

        Assert.Equal(new[] { """{"filters":{"modules":[{"name":{"value":["Extended Cargo Rack"]},"class":{"value":["5"]},"rating":{"value":["E"]}}],""" + FromSol }, Questions());
        // To the player it is the cargo rack it is.
        Assert.Equal("Dock at Rack Shop and buy 5E Cargo Rack.", Assert.Single(trip.GetProperty("stops").EnumerateArray()).GetProperty("note").GetString());
    }

    [Fact]
    public async Task When_no_station_sells_it_all_Spansh_is_asked_about_one_thing_at_a_time_and_only_about_what_no_station_seen_sells()
    {
        // (The stations here are made up.) A Hauler for a pilot who flies the Mandalay: a better drive, a fuel scoop and a
        // Guardian booster. The first question is for all of it at one station; nobody has that. So the ship is asked
        // about: two shipyards, one of which also sells the drive and the scoop. Those are then not asked about again;
        // the booster is, and one far station has it. A fourth question is never needed.
        JsonObject hauler = JsonNode.Parse((await Ok("GET", "loadouts/designer/start?ship=hauler")).GetProperty("loadout").GetRawText())!.AsObject();
        Fit(hauler["Modules"]!.AsArray(), "FrameShiftDrive", "int_hyperdrive_size2_class5");
        Fit(hauler["Modules"]!.AsArray(), "Slot01_Size3", "int_fuelscoop_size3_class5");
        Fit(hauler["Modules"]!.AsArray(), "Slot04_Size1", "int_guardianfsdbooster_size1");
        await Save("Runabout", hauler);
        const string Drive = """{"name":{"value":["Frame Shift Drive"]},"class":{"value":["2"]},"rating":{"value":["A"]}}""";
        const string Scoop = """{"name":{"value":["Fuel Scoop"]},"class":{"value":["3"]},"rating":{"value":["A"]}}""";
        const string Booster = """{"name":{"value":["Guardian FSD Booster"]},"class":{"value":["1"]},"rating":{"value":["H"]}}""";
        const string Ship = "\"ships\":[{\"name\":{\"value\":[\"Hauler\"]}}]";
        net.Instead = asked => !asked.StartsWith(Search, StringComparison.Ordinal) ? null
            : asked.Contains(Ship, StringComparison.Ordinal) && asked.Contains("\"modules\"", StringComparison.Ordinal) ? Answer()
            : asked.Contains(Ship, StringComparison.Ordinal) ? Answer(
                Station("Yard One", "Near", 3, 4, 0, [], ["Hauler", "Sidewinder"]),
                Station("Yard Two", "Nearby", 12, 0, 0, ["Int_Hyperdrive_Size2_Class5", "Int_FuelScoop_Size3_Class5", "Int_FuelScoop_Size2_Class5"], ["Hauler"]))
            : asked.Contains(Booster, StringComparison.Ordinal) ? Answer(Station("Tech Port", "Far Off", 12, 0, 80, ["Int_GuardianFSDBooster_Size1"]))
            : throw new InvalidOperationException("Not a question this test expects: " + asked);

        JsonElement list = await Ok("GET", "loadouts/shopping?id=Runabout");
        Assert.Equal(new[] { "1 × 2A Frame Shift Drive", "1 × 3A Fuel Scoop", "1 × 1H Guardian FSD Booster" }, Labels(list));
        Assert.True(list.GetProperty("newHull").GetBoolean());

        JsonElement trip = await Ok("POST", "loadouts/shopping/stops", new { id = "Runabout" });

        Assert.Equal(new[]
        {
            $$"""{"filters":{"modules":[{{Drive}},{{Scoop}},{{Booster}}],{{Ship}},{{FromSol}}""",
            $$"""{"filters":{{{Ship}},{{FromSol}}""",
            $$"""{"filters":{"modules":[{{Booster}}],{{FromSol}}""",
        }, Questions());
        Assert.Equal(("Shopping for Runabout", "Sol", 3, false), (trip.GetProperty("name").GetString(), trip.GetProperty("from").GetString(), trip.GetProperty("questions").GetInt32(), trip.GetProperty("cutShort").GetBoolean()));
        Assert.Equal(RecordedServices.RecordedUtc, trip.GetProperty("askedUtc").GetDateTime());
        Assert.Empty(trip.GetProperty("notFound").EnumerateArray());
        // The shipyard that sells the most of the rest first, although the other is nearer; then the booster, 80 ly on.
        JsonElement[] stops = [.. trip.GetProperty("stops").EnumerateArray()];
        Assert.Equal(
            """{"station":"Yard Two","system":"Nearby","address":"1012","x":12,"y":0,"z":0,"ly":12,"starLs":250,"pad":"L","type":"Coriolis Starport","updated":"2026-10-09T08:00:00Z","buy":[{"what":"the Hauler itself","count":1,"price":50000},{"what":"2A Frame Shift Drive","count":1,"price":1000},{"what":"3A Fuel Scoop","count":1,"price":1000}],"note":"Dock at Yard Two and buy the Hauler itself, 2A Frame Shift Drive, 3A Fuel Scoop."}""",
            JsonSerializer.Serialize(stops[0]));
        Assert.Equal(("Tech Port", "Far Off", 80, "Dock at Tech Port and buy 1H Guardian FSD Booster."),
            (stops[1].GetProperty("station").GetString(), stops[1].GetProperty("system").GetString(), stops[1].GetProperty("ly").GetDouble(), stops[1].GetProperty("note").GetString()));
        Assert.Equal(2, stops.Length);

        // Asked again within ten minutes, Spansh is not: its answers are remembered.
        await Ok("POST", "loadouts/shopping/stops", new { id = "Runabout" });
        Assert.Equal(3, Questions().Length);
    }

    [Fact]
    public async Task The_asking_stops_at_its_limit_and_says_so_and_what_Spansh_does_not_list_is_not_asked_for()
    {
        // (No station here: Spansh answers every question with none.) Ten kinds of fuel scoop that no station sells,
        // and two heat sink launchers, which the recorded list of Spansh's module names does not have.
        string[] scoops = ["size6_class5", "size5_class5", "size4_class5", "size4_class4", "size3_class4", "size3_class3", "size2_class5", "size1_class5", "size1_class4", "size1_class3"];
        await Save("Scoops", Changed(modules =>
        {
            for (int slot = 1; slot <= 10; slot++) Fit(modules, Invariant($"Slot{slot:00}_Size{"6544332111"[slot - 1]}"), "int_fuelscoop_" + scoops[slot - 1]);
            Fit(modules, "TinyHardpoint3", "hpt_heatsinklauncher_turret_tiny");
            Fit(modules, "TinyHardpoint4", "hpt_heatsinklauncher_turret_tiny");
        }));
        net.Instead = asked => asked.StartsWith(Search, StringComparison.Ordinal) ? Answer() : null;

        JsonElement trip = await Ok("POST", "loadouts/shopping/stops", new { id = "Scoops" });

        // One question for all ten at once, then one each for the first eight: nine, and no more.
        Assert.Equal(StudioLoadouts.MostQuestions, Questions().Length);
        Assert.Equal(10, Questions()[0].Split("\"name\":{\"value\":[\"Fuel Scoop\"]}").Length - 1);
        Assert.All(Questions().Skip(1), question => Assert.Single(question.Split("\"name\":").Skip(1)));
        Assert.DoesNotContain(Questions(), question => question.Contains("Heat Sink", StringComparison.Ordinal));
        Assert.Equal((9, true, 0), (trip.GetProperty("questions").GetInt32(), trip.GetProperty("cutShort").GetBoolean(), trip.GetProperty("stops").GetArrayLength()));
        Assert.Equal(new[] { "2 × I Heat Sink Launcher", "6A Fuel Scoop", "5A Fuel Scoop", "4A Fuel Scoop", "4B Fuel Scoop", "3B Fuel Scoop", "3C Fuel Scoop", "2A Fuel Scoop", "1A Fuel Scoop", "1B Fuel Scoop", "1C Fuel Scoop" },
            trip.GetProperty("notFound").EnumerateArray().Select(thing => thing.GetString()));
    }

    [Fact]
    public async Task The_players_station_filters_and_the_pad_of_the_bigger_ship_go_into_the_question()
    {
        await Save("Scoop", Changed(modules => Fit(modules, "Slot01_Size6", "int_fuelscoop_size6_class5")));
        Directory.CreateDirectory(Builds);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "loadouts", "type9-design.json"), Path.Combine(Builds, "Hauling.json"));
        net.Instead = asked => asked.StartsWith(Search, StringComparison.Ordinal) ? Answer(
            Station("Small Pads", "One", 1, 0, 0, ["int_fuelscoop_size6_class5"]).Replace("\"large_pads\":4,\"medium_pads\":8", "\"large_pads\":0,\"medium_pads\":0"),
            Station("Far From Its Star", "Two", 2, 0, 0, ["int_fuelscoop_size6_class5"]).Replace("\"distance_to_arrival\":250", "\"distance_to_arrival\":900"),
            Station("A Carrier", "Three", 3, 0, 0, ["int_fuelscoop_size6_class5"]).Replace("Coriolis Starport", "Drake-Class Carrier"),
            Station("Fits", "Four", 4, 0, 0, ["int_fuelscoop_size6_class5"])) : null;

        // The player's own filters: close to the star, no surface stations. The Mandalay needs a medium pad.
        Assert.True(rig.Change("""{ "maxStarLs": 500, "surface": "none" }"""));
        JsonElement trip = await Ok("POST", "loadouts/shopping/stops", new { id = "Scoop" });
        Assert.Equal(
            """{"filters":{"modules":[{"name":{"value":["Fuel Scoop"]},"class":{"value":["6"]},"rating":{"value":["A"]}}],"type":{"value":["Coriolis Starport","Orbis Starport","Ocellus Starport","Dodec Starport","Outpost","Asteroid base","Mega ship","Planetary Outpost"]},"distance_to_arrival":{"comparison":"<=>","value":[0,500]}},"sort":[{"distance":{"direction":"asc"}}],"size":10,"page":0,"reference_coords":{"x":0,"y":0,"z":0}}""",
            Questions()[0]);
        // What Spansh sends is still held to the letter of the filters: no small pads only, not too far from the star, no fleet carrier.
        Assert.Equal(new[] { "Fits" }, trip.GetProperty("stops").EnumerateArray().Select(stop => stop.GetProperty("station").GetString()));

        // A Type-9 has to land where its modules are bought: the question asks for a large pad, whatever the ship flown
        // needs. (The recorded list of Spansh's ship names is cut down to ten and has no Type-9, so the ship itself is
        // said to be not found and is not asked for; the same goes for the modules that list does not have.)
        trip = await Ok("POST", "loadouts/shopping/stops", new { id = "Hauling" });
        Assert.StartsWith("""{"filters":{"modules":[{"name":{"value":["Military Grade Composite"]},"class":{"value":["1"]},"rating":{"value":["I"]}},{"name":{"value":["Power Plant"]},"class":{"value":["6"]},"rating":{"value":["A"]}},""", Questions()[1]);
        Assert.Contains("\"has_large_pad\":{\"value\":true}", Questions()[1]);
        Assert.DoesNotContain("\"ships\"", Questions()[1]);
        Assert.Equal(new[] { "the Type-9 Heavy itself", "2F Multi-Cannon, gimballed", "2D Beam Laser, fixed", "2 × A Shield Booster" }, trip.GetProperty("notFound").EnumerateArray().Take(4).Select(thing => thing.GetString()));
    }

    [Fact]
    public async Task What_goes_wrong_is_said_in_words_and_nothing_is_asked_without_need()
    {
        await Save("Same", JsonNode.Parse(Mandalay)!);
        await Save("Scoop", Changed(modules => Fit(modules, "Slot01_Size6", "int_fuelscoop_size6_class5")));

        // Nothing lacking: no stops, and Spansh's stations are not asked about.
        JsonElement trip = await Ok("POST", "loadouts/shopping/stops", new { id = "Same" });
        Assert.Equal((0, 0, 0), (trip.GetProperty("stops").GetArrayLength(), trip.GetProperty("notFound").GetArrayLength(), trip.GetProperty("questions").GetInt32()));
        Assert.Empty(Questions());

        // Spansh cannot be reached.
        net.Down = "spansh.co.uk/api/stations/search";
        var (status, json) = await Call("POST", "loadouts/shopping/stops", new { id = "Scoop" });
        Assert.Equal((502, "Couldn't get an answer from Spansh: No such host is known."), (status, json.GetProperty("error").GetString()));

        (status, json) = await Call("POST", "loadouts/shopping/stops", new { id = "Gone" });
        Assert.Equal((404, "That saved build was not found."), (status, json.GetProperty("error").GetString()));
    }

    // ------------------------------------------------------------ the build's engineering, pinned

    [Fact]
    public async Task A_builds_engineering_is_pinned_in_the_planners_own_terms_and_only_once()
    {
        // The Type-9 design of Fixtures\loadouts: ten engineered modules, made in the designer.
        Directory.CreateDirectory(Builds);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "loadouts", "type9-design.json"), Path.Combine(Builds, "Hauling.json"));

        JsonElement answer = await Ok("POST", "loadouts/pin", new { id = "Hauling" });

        Assert.Equal("""{"added":10,"already":0,"notOffered":[]}""", JsonSerializer.Serialize(answer));
        // The build says "Weapon_Overcharged" for the multi-cannon; the planner knows that recipe as the multi-cannon's own.
        // Every pin names the kind of module, the blueprint as all pages call it, the grade and the effect.
        Assert.Equal(new[]
        {
            "Armour: Heavy Duty to grade 5 with Deep Plating", "Power Plant: Overcharged to grade 3 with Stripped Down", "Thrusters: Dirty Drive Tuning to grade 5 with Drag Drives",
            "Frame Shift Drive: Increased Range to grade 5 with Mass Manager", "Power Distributor: Engine Focused to grade 5 with Cluster Capacitors",
            "Shield Generator: Reinforced to grade 5 with Hi-Cap", "Hull Reinforcement Package: Heavy Duty to grade 5 with Deep Plating",
            "Shield Booster: Heavy Duty to grade 5 with Super Capacitors", "Multi-Cannon: Overcharged to grade 5 with Stripped Down", "Beam Laser: Efficient to grade 5",
        }, planner.View().Pins.Select(pin => $"{pin.Module}: {pin.Blueprint} to grade {pin.Grade}{(pin.Experimental is null ? "" : " with " + pin.Experimental)}"));
        Assert.Equal(("mc", "MC_Overcharged", 5, "special_weapon_lightweight"), (Saved()[8].Module, Saved()[8].Blueprint, Saved()[8].Grade, Saved()[8].Experimental));

        // Asked again: everything is pinned already. A blueprint taken away by hand comes back alone.
        Assert.Equal("""{"added":0,"already":10,"notOffered":[]}""", JsonSerializer.Serialize(await Ok("POST", "loadouts/pin", new { id = "Hauling" })));
        planner.Unpin(3);
        Assert.Equal("""{"added":1,"already":9,"notOffered":[]}""", JsonSerializer.Serialize(await Ok("POST", "loadouts/pin", new { id = "Hauling" })));
        Assert.Equal(10, planner.View().Pins.Count);

        var (status, json) = await Call("POST", "loadouts/pin", new { id = "Gone" });
        Assert.Equal((404, "That saved build was not found."), (status, json.GetProperty("error").GetString()));

        List<EngineeringPin> Saved() => new Settings(Path.Combine(temp, "settings.json")).Get<EngineeringSettings>("engineering").Pins;
    }

    [Fact]
    public void A_real_ships_engineering_is_pinned_to_the_grade_it_has_and_two_modules_alike_get_a_pin_each()
    {
        // The owner's Mandalay: six engineered modules, some without an effect. One more is added as the journal can write
        // it: the same recipe under a longer code, to a grade past what anybody offers, with an effect from before today's
        // engineering; and a second pulse laser is made just like the first. An expanded cargo rack comes ready-made.
        ShipLoadout ship = Parse(Changed(modules =>
        {
            JsonObject Module(string slot) => modules.OfType<JsonObject>().First(module => (string?)module["Slot"] == slot);
            Module("Radar")["Engineering"] = new JsonObject { ["BlueprintName"] = "Sensor_Sensor_LightWeight", ["Level"] = 5, ["Quality"] = 1.0, ["ExperimentalEffect"] = "special_old_effect" };
            Module("SmallHardpoint2")["Engineering"] = Module("SmallHardpoint1")["Engineering"]!.DeepClone();
            Module("Slot01_Size6")["Engineering"] = new JsonObject { ["BlueprintName"] = "CargoRack_IncreasedCapacity", ["Level"] = 1, ["Quality"] = 1.0 };
        }));

        var (added, already, notOffered) = planner.PinBuild(ship);

        Assert.Equal((7, 0), (added, already));
        Assert.Equal(new[] { "Cargo Rack: Expanded Capacity" }, notOffered);
        Assert.Equal(new[]
        {
            "Pulse Laser: Overcharged to grade 1", "Pulse Laser: Overcharged to grade 1", "Thrusters: Drive Strengthening to grade 1",
            "Frame Shift Drive: Increased Range to grade 3 with Stripped Down", "Power Distributor: Weapon Focused to grade 1 with Flow Control",
            "Sensors: Lightweight to grade 5", "Shield Generator: Enhanced Low Power to grade 3 with Fast Charge",
        }, planner.View().Pins.Select(pin => $"{pin.Module}: {pin.Blueprint} to grade {pin.Grade}{(pin.Experimental is null ? "" : " with " + pin.Experimental)}"));
        Assert.Equal((0, 7), (planner.PinBuild(ship).Added, planner.PinBuild(ship).Already));
    }
}
