using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Nav;

/// <summary>One answer of the 3D map's search (GET /api/nav/heatmap/find).</summary>
/// <param name="Kind">
/// "nebula" (a nebula or star cluster), "sector" (a sector of the game's grid), "star" (a system
/// Spansh knows) or "decoded" (a system placed from its generated name alone, which no database may have).
/// </param>
/// <param name="Name">The name as the game spells it, or as EDAstro's list of nebulae does.</param>
/// <param name="Label">The name to show: for a nebula its best-known name ("California Nebula").</param>
/// <param name="X">
/// Where it is, in light years (with <paramref name="Y"/> and <paramref name="Z"/>): a nebula's
/// middle, a sector's middle, a star's own place, or the middle of the cube a generated name names.
/// </param>
/// <param name="Sector">The sector that point lies in, by its numbers along x, y and z (y is the layer).</param>
/// <param name="Distance">Light years from the commander to that point; null while Studio does not know where they are.</param>
public sealed record MapFind(string Kind, string Name, string Label, double X, double Y, double Z, int[] Sector, double? Distance)
{
    /// <summary>The radius of the nebula's sphere in light years, for the nebulae of the table that comes with Studio (<see cref="HaRegions"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? R { get; init; }

    /// <summary>A system placed from its name lies within this many light years of the point along each of x, y and z.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PlusMinus { get; init; }

    /// <summary>The game's own number for a system, as text: these numbers can be larger than a web page can hold exactly.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id64 { get; init; }

    /// <summary>For a nebula: the game keeps commanders out without a permit (EDTS's list; the game itself has the last word).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Permit { get; init; }

    /// <summary>
    /// For a system placed from its name ("decoded"): true when Spansh was asked and does not know the name, so the
    /// page may say no database has recorded it; false when Spansh could not be asked, so nobody has said either way.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Checked { get; init; }
}

/// <summary>
/// The 3D map's routes, all under /api/nav/heatmap/, and its "heatmap" event. Player mistakes answer
/// with the error shape and 400; a community service that fails answers 200 with a "problem" in the
/// player's words, and whatever could still be given.
///   GET  block?ix=&amp;iz=      the block of sectors round a column (the commander's when left out): its layers,
///                            sectors, nebulae, the commander, and what is on this PC. Asks nobody.
///   GET  find?q=&amp;kind=      kind "place": nebulae and sectors by name, asking nobody. Kind "star": Spansh's
///                            name suggestions with their places, and a generated name read without asking.
///   GET  area?key=&amp;cubeLy=  what is kept of a looked-inside sector ("s:ix:iy:iz") or nebula ("n:name"), in cubes
///                            of cubeLy (the area's own size when left out); asks nobody
///   POST survey             {"key":"","go":false}: looks inside a sector or a nebula (<see cref="HeatmapSurvey.Survey"/>)
///   POST count              {"kind":"cube"|"column"|"around","x":0,"y":0,"z":0,"go":false,"again":false}:
///                            counts single cubes of a busy sector (<see cref="HeatmapSurvey.Count"/>)
///   POST cancel             {"what":"survey"|"finemap"}: stops the map's job under way at Spansh (what came back is kept),
///                            or the build of the detailed map (what was built before stays as it was); both when what is
///                            left out, as pages from before it was there expect
///   GET  counted           every cube counted so far: {"cubes":[...]}
///   GET  firsts             the commander's first discoveries from their journals: {"systems":[...]}
///   GET  nebulae            whether EDAstro's list of nebulae is on this PC: {"have":..,"stale":..,"problem":null}
///   POST nebulae            fetches it when it is missing or a week old; the same answer, with "problem" when it could not be
///   POST finemap            {"ix":0,"iz":0,"go":false}: the detailed map of the block round that column (the commander's
///                            when left out), from Spansh's list of every system on this PC (<see cref="FineMap.Build"/>).
///                            Without go: {"needs":{"seconds","reads","internet":false},"fine":..,"problem":..}, building
///                            nothing; with go: builds it, minutes later {"fine":..,"problem":..,"stopped":..,"systems":..,...}
///   GET  cubes?ix=&amp;iy=&amp;iz=&amp;size=   a sector's cubes of 160 ly (or 40 with size=40) in the detailed map; reads no list
///   GET  names?kind=sector|cube|nebula&amp;key=&amp;x0=&amp;y0=&amp;z0=&amp;size=[&amp;near=x,y,z][&amp;group=]   the systems known in a
///                            square, grouped by the game's own cubes, with the names to try (<see cref="HeatmapNames"/>);
///                            x0..size give the cube for kind "cube"; a square of more than 2,000 of those cubes lists the
///                            ones nearest "near" (the camera's focus), with "totals" for the whole square. With group,
///                            that one cube in full. Asks nobody
///   GET  finds              your finds and the switch for them: {"finds":[...],"checkTargets":true} (<see cref="HeatmapFinds"/>)
///   POST finds/remove       {"id64":"…"} (or {"name":"…"}): takes one off; {"removed":..,"finds":[...]}
///   GET  settings           {"checkTargets":true,"routeCheck":"all","recordedDays":1,"withinReach":true,"reachLy":null,
///                            "navScan":false,"navScanVolume":70}
///   POST settings           any of {"checkTargets":false}, {"routeCheck":"ends"|"all"}, {"recordedDays":1|7|30},
///                            {"withinReach":false}, {"reachLy":42.5} (null or "" for the ship's own range), {"navScan":true},
///                            {"navScanVolume":0..100}: "Check my targets", what of a plotted route is checked, how long a
///                            recorded system is remembered, Within reach's switch and range, and Nav scan's switch and
///                            volume, kept in settings.json; what is left out stays as it is, and nothing changes when any
///                            of it is wrong (400 with the reason). The same answer as GET.
///                            Choosing "all" checks the route plotted now unless it is being checked already (the route
///                            there at Studio's start too, which is not checked by itself); switching Within reach on, or
///                            a new range, lists the systems within reach at once; Nav scan reads the pictures taken after
///                            it is switched on
///   GET  navscan            Nav scan as it stands (<see cref="NavScanView"/>): {"state","on","volume","watching":[folders],
///                            "problem","line":{"words","tone"},"pictures":[{"file","at","names","recorded","notRecorded",
///                            "failed","unsure","dropped","state","problem","waiting","unreadable"}],"names":[{"name","id64","state","unsure","x","y",
///                            "z","plusMinus","from","how","reason"}]}
///   POST navscan/test-sound {"sound":"ping"|"click"}: plays it at Nav scan's volume: {"sound","played","volume"} ("played"
///                            false at volume 0)
///   POST checks/forget      "Forget all checks": the memory of checks (<see cref="CheckMemory"/>) is emptied, so every
///                            system is checked afresh: {"forgotten":12}
///   GET  reach              Within reach as it stands (<see cref="ReachView"/>): {"state","on","rangeLy","from","at",
///                            "centre":{"x","y","z","system"},"source","capped","problem","moreSystems","moreMustExist",
///                            "inLargeCubes","systems":[{"name","id64","x","y","z","distance","state"}],
///                            "mustExist":[{"name","id64","x","y","z","plusMinus","distance","certainty","state"}]}:
///                            "inLargeCubes" the systems that must exist in cubes wider than the sphere that reach into
///                            it, counted and not listed (most likely out of reach)
///   GET  route-check        the check of every stop of the plotted route as it stands (<see cref="RouteCheckView"/>):
///                            {"state","mode","started","finished","total","checked","recorded","notRecorded","failed",
///                            "requests","reason","plotted","plottedTo","line":{"words","tone"},
///                            "stops":[{"name","id64","x","y","z","state","how","reason"}]}: "plotted" the stops of a
///                            route there since Studio's start and so not checked (state "idle"), "line" what the
///                            overlay's nav page shows of it (null for nothing)
/// Every "heatmap" event carries a <see cref="SurveyProgress"/>, sent as a survey or a count gets on, a
/// <see cref="FineMapProgress"/> ("kind":"finemap") as a build of the detailed map does, a <see cref="TargetNews"/>
/// ("kind":"target") when the game's target or route end moves or a check of one ends, a <see cref="RouteCheckNews"/>
/// ("kind":"route-check", what GET route-check gives but the stops) on every change of the route's check, or a
/// <see cref="ReachNews"/> ("kind":"reach", what GET reach gives but the lists, with how many there are) on every change
/// of Within reach, or a <see cref="NavScanNews"/> ("kind":"navscan", what GET navscan gives but the lists, with the newest
/// picture, the overlay's line and "found": the names just found not recorded) on every change of Nav scan. A
/// "heatmap-find" event carries a new <see cref="HeatmapFind"/> (one Nav scan read off a picture says "from":"nav scan",
/// "confirmed":false and its "note"), or one of those the game has since named ("confirmed":true).
/// </summary>
public static class HeatmapApi
{
    /// <summary>What the page sends to look inside an area.</summary>
    sealed class SurveyBody
    {
        public string Key { get; set; } = "";
        public bool Go { get; set; }
    }

    /// <summary>What the page sends to count cubes. For "around" the place may be left out: it is the commander's.</summary>
    sealed class CountBody
    {
        public string Kind { get; set; } = "";
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? Z { get; set; }
        public bool Go { get; set; }
        public bool Again { get; set; }
    }

    /// <summary>What the page sends to build the detailed map. The column may be left out: it is the commander's.</summary>
    sealed class FineBody
    {
        public int? Ix { get; set; }
        public int? Iz { get; set; }
        public bool Go { get; set; }
    }

    /// <summary>What the page sends to take a find off the list: its number (as text, or a number), else its name.</summary>
    sealed class RemoveBody
    {
        public long? Id64 { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>What the page sends to change the 3D map's settings; what is left out stays as it is.</summary>
    sealed class SettingsBody
    {
        public bool? CheckTargets { get; set; }
        public string? RouteCheck { get; set; }
        public bool? WithinReach { get; set; }
        public bool? NavScan { get; set; }

        /// <summary>0 to 100, as a number or as text; left out (Undefined) when not sent.</summary>
        public JsonElement NavScanVolume { get; set; }

        /// <summary>A whole number of days, as a number or as text; left out (Undefined) when not sent.</summary>
        public JsonElement RecordedDays { get; set; }

        /// <summary>Light years as a number or as text, or null or "" for the ship's own range; left out (Undefined) when not sent.</summary>
        public JsonElement ReachLy { get; set; }
    }

    /// <summary>What the page sends to stop a job: which one ("survey" or "finemap"); both when it is left out.</summary>
    sealed class CancelBody
    {
        public string? What { get; set; }
    }

    /// <summary>What the page sends to hear one of Nav scan's sounds: "ping" or "click".</summary>
    sealed class SoundBody
    {
        public string? Sound { get; set; }
    }

    const string NoSuchColumn = "Sectors are numbered 0 to 127 from west to east and from south to north.";
    const string NoSuchLayer = "Layers are numbered 0 to 63 from the bottom of the galaxy's grid up.";
    const string NoSuchFineCube = "The detailed map keeps cubes of 160 and 40 light years.";
    const string NoFineList = "Studio has no copy of Spansh's list of every system on this PC, so it cannot build the detailed map. Nothing is fetched from the internet for it.";
    const string NoSuchArea = "Studio knows no sector or nebula by that name.";
    const string NoSuchCube = "Cubes are 10, 20, 40, 80, 160, 320, 640 or 1,280 light years on a side.";
    const string NoSuchCount = "Studio counts a cube, a column or the cubes around you.";
    const string OutsideTheGrid = "That place is outside the galaxy's sectors.";
    const string NotHere = "Studio does not know where you are yet. Start Elite, and it will follow you.";
    const string NoSuchNamesKind = "Studio lists the names in a sector, a cube or a nebula.";
    const string NoSuchNamesCube = "A cube is given by its corner (x0, y0, z0) and its size, up to 1,280 light years.";
    const string NoSuchFind = "Say which find to take off: its number or its name.";
    const string NoSuchCancel = "Studio stops a job at Spansh (\"survey\") or the build of the detailed map (\"finemap\").";
    const string NoSuchNear = "A point is given as x,y,z in light years.";
    const string NoSuchSettings = "Studio could not read those settings: a switch is true or false, a choice is a word, and days and light years are numbers.";

    /// <summary>Answers of a search, at most.</summary>
    const int MostFound = 20;

    /// <summary>
    /// The looked-inside areas can hold 10,000 systems, a megabyte or two of JSON, so they go out on one line
    /// (Studio's usual answers are indented for reading).
    /// </summary>
    static readonly JsonSerializerOptions Compact = new(Settings.Json) { WriteIndented = false };

    /// <summary>The column the block opens on while Studio does not know where the commander is: Sol's.</summary>
    static readonly (int X, int Y, int Z) Sol = Galaxy.SectorOf(0, 0, 0);

    /// <summary>Every name a nebula of EDTS's table goes by, for the search: its own, without " Sector", and its other names.</summary>
    static readonly Dictionary<Nebula, string[]> NebulaNames = HaRegions.All.ToDictionary(
        nebula => nebula,
        nebula => (string[])[nebula.Name, Short(nebula.Name), .. HaRegions.Aliases.Where(alias => alias.Name == nebula.Name).Select(alias => alias.Alias)]);

    /// <summary>Registers the routes. Call on the UI thread, which is also where the server runs them.</summary>
    /// <param name="trip">The trip being counted, for the way the commander is heading.</param>
    /// <param name="spansh">The app's one client for Spansh, the same one the route plotter and the unexplored finder use.</param>
    /// <param name="coverage">The flat map's copy of EDAstro's sector list. It must be the same one the flat map uses, so the 3D map hears when a new list comes.</param>
    /// <param name="survey">The map's questions to Spansh and what came back; built on the same <paramref name="spansh"/>.</param>
    /// <param name="nebulae">EDAstro's list of nebulae, fetched only when the player asks.</param>
    /// <param name="firsts">The commander's first discoveries, from their journals.</param>
    /// <param name="fineMap">The detailed map from Spansh's list of every system on this PC; when left out, the one in <paramref name="survey"/>'s folder.</param>
    /// <param name="settings">Where "Check my targets", what of a route is checked, how long a recorded system is remembered and Within reach's switch and range are kept (section "heatmap"); when left out, they hold for the session only.</param>
    /// <param name="finds">The finds, the markers of the game's target and the route's check; when left out, made here on <paramref name="game"/>.</param>
    /// <param name="reach">Within reach; when left out, made here on <paramref name="game"/>, with the memory of checks of <paramref name="finds"/>.</param>
    /// <param name="navScan">
    /// Nav scan, made on the same <paramref name="finds"/>. When left out, one is made here that watches no folder and
    /// makes no sound (tests, which give their own when they need one): the app gives the one that watches the player's
    /// screenshots.
    /// </param>
    /// <returns>The finds and the route's check, for the overlay's line about it (and its <see cref="HeatmapFinds.Memory"/>, which Nav scan shares).</returns>
    public static HeatmapFinds Map(WebServer web, GameState game, ExploreTrip trip, Spansh spansh, ExploreCoverage coverage,
        HeatmapSurvey survey, NebulaList nebulae, FirstsLog firsts, FineMap? fineMap = null, Settings? settings = null, HeatmapFinds? finds = null,
        HeatmapReach? reach = null, NavScan? navScan = null)
    {
        var cubes = new SectorCubes(coverage);
        FineMap fine = fineMap ?? new FineMap(survey.Folder);
        var names = new HeatmapNames(survey, fine);
        HeatmapFinds marks = finds ?? new HeatmapFinds(game, survey, fine, settings);
        HeatmapReach near = reach ?? new HeatmapReach(game, survey, fine, marks.Memory, settings);
        NavScan scan = navScan ?? new NavScan(game, marks, survey.Folder, settings, places: () => []) { Play = (_, _) => { } };
        survey.Progress += progress => web.Send("heatmap", progress);
        fine.Progress += progress => web.Send("heatmap", progress);
        // Nav scan's news; a picture's check settles systems Within reach may list (their state comes from the memory).
        scan.News += news =>
        {
            web.Send("heatmap", news);
            if (news.Checked) near.Retell();
        };
        // A find Nav scan read off a picture, now named by the game: the same event as a new find, the find saying it is confirmed.
        marks.Confirmed += find => web.Send("heatmap-find", find);
        // A check that settled a system may have settled one Within reach lists (its state comes from the memory of
        // checks): Within reach tells the page again, which asks for the list again.
        marks.News += news =>
        {
            web.Send("heatmap", news);
            if (news.State is HeatmapFinds.Recorded or HeatmapFinds.NotRecorded) near.Retell();
        };
        marks.RouteNews += news =>
        {
            web.Send("heatmap", news);
            if (news.State is HeatmapFinds.Done or HeatmapFinds.Stopped && news.Finished is not null) near.Retell();
        };
        marks.Found += find => web.Send("heatmap-find", find);
        near.News += news => web.Send("heatmap", news);
        string starsProblem = "";

        web.Map("GET", "/api/nav/heatmap/block", async request =>
        {
            // The game state lives on this thread, and so does the flat map's copy of the list (which Stale reads).
            double[]? at = game.StarPos;
            (int X, int Y, int Z)? mine = at is [var px, var py, var pz] ? Galaxy.SectorOf(px, py, pz) : null;
            (int X, int Y, int Z) home = mine ?? Sol;
            if (!Column(request.Query, "ix", Math.Clamp(home.X, 0, Galaxy.SectorsAcross - 1), out int ix)
                || !Column(request.Query, "iz", Math.Clamp(home.Z, 0, Galaxy.SectorsAcross - 1), out int iz))
                return WebResponse.Error(NoSuchColumn, 400);
            object? player = at is [var x, var y, var z] && mine is { } sector
                ? new { x, y, z, system = game.StarSystem, heading = trip.Heading, sector = new[] { sector.X, sector.Y, sector.Z } }
                : null;
            bool sectorListStale = coverage.Stale;
            // The game's target and the end of its route, as the game names them; placed from what is kept when the game
            // gives no place (reading files, so away from this thread), else from the name with how far off it may be.
            (GameNamed? targeted, GameNamed? ending) = marks.Marks();
            MapMark? target = await marks.Place(targeted), routeEnd = await marks.Place(ending);

            // The rest reads files (the lists, the kept surveys): not on this thread.
            return await Task.Run(() =>
            {
                SectorList? list = cubes.List;
                SectorBlock block = SectorCubes.Block(list, ix, iz);
                // The layer to show first: the commander's own height when the block has it, else its busiest, else the middle.
                int layer = block.Layers.Any(shown => shown.Layer == mine?.Y)
                    ? mine!.Value.Y
                    : block.Layers.OrderByDescending(shown => shown.Known ?? 0).ThenBy(shown => Math.Abs(shown.Layer - 31.5)).ThenByDescending(shown => shown.Layer).First().Layer;
                return WebResponse.Json(new
                {
                    sectorLy = Galaxy.SectorLy,
                    origin = new[] { Galaxy.OriginX, Galaxy.OriginY, Galaxy.OriginZ },
                    centre = new[] { ix, layer, iz },
                    // "here": the commander is at this height. Whether they are in the block as well, player.sector says.
                    layers = block.Layers.Select(shown => new { iy = shown.Layer, name = shown.Name, yFrom = shown.YFrom, yTo = shown.YTo, known = shown.Known, here = shown.Layer == mine?.Y }),
                    // "fine": the sector's totals in the detailed map, when one covers its column (Spansh's whole list, so every
                    // system in the sector's box, where EDAstro's "known" counts those named after the sector).
                    sectors = block.Sectors.Select(shown => new
                    {
                        ix = shown.X, iy = shown.Y, iz = shown.Z, name = shown.Name, known = shown.Known, first = shown.First,
                        survey = survey.Summary(SurveyArea.SectorKey(shown.X, shown.Y, shown.Z)),
                        fine = fine.Summary(shown.X, shown.Y, shown.Z),
                    }),
                    // Nebulae are drawn as their spheres; most are one, a few (ICZ) are many, so all of them are given.
                    nebulae = HaRegions.All.Where(nebula => Touches(nebula, block.Bounds)).Select(nebula => new
                    {
                        name = nebula.Name, label = nebula.Label, x = nebula.X, y = nebula.Y, z = nebula.Z, r = nebula.Radius,
                        known = list is null ? (int?)null : list.Nebula(nebula.Name)?.Known ?? 0,
                        permit = nebula.Permit,
                        spheres = nebula.Spheres.Select(sphere => new[] { sphere.X, sphere.Y, sphere.Z, sphere.Radius }),
                    }),
                    player,
                    sectorList = new { have = list is not null, stale = sectorListStale },
                    nebulaList = new { have = nebulae.Nebulae is not null, stale = nebulae.Stale },
                    budget = new { used = survey.Used, cap = survey.Cap },
                    // The detailed map of this block: built or not, from which list, and whether one can be (null: no list on this PC).
                    fine = fine.Status(ix, iz),
                    target,
                    routeEnd,
                });
            });
        });

        web.Map("GET", "/api/nav/heatmap/find", async request =>
        {
            string text = (request.Query.GetValueOrDefault("q") ?? "").Trim();
            (double X, double Y, double Z)? from = game.StarPos is [var x, var y, var z] ? (x, y, z) : null;
            switch (request.Query.GetValueOrDefault("kind") ?? "place")
            {
                case "place":
                    IReadOnlyList<MapFind> places = await Task.Run(() => Places(text, from, cubes.List, nebulae.Nebulae));
                    return WebResponse.Json(new { results = places, problem = (string?)null });

                case "star":
                    var stars = new List<MapFind>();
                    string? problem = null;
                    IReadOnlyList<LocatedSystem> located = [];
                    try
                    {
                        // Spansh's name suggestions: the same request, and the same kept answer, as the route plotter's name
                        // boxes. Not one of the map's own requests, so neither its gate nor its budget applies.
                        located = await spansh.Locate(text);
                    }
                    catch (SpanshException unanswered)
                    {
                        // The box asks again with every few letters typed, so the same problem is logged once.
                        if (unanswered.Message != starsProblem) Log.Error("Stars could not be looked up for the 3D map", unanswered);
                        (starsProblem, problem) = (unanswered.Message, unanswered.Message);
                    }
                    // A generated name gives its place without asking anybody, to within half its cube: the
                    // way to find a star no database has recorded. When Spansh knows it, its own place is better.
                    // Whether a database has it is known only when Spansh answered: an unanswered question says nothing.
                    if (ProcGenNames.Decode(text) is { } decoded && !located.Any(star => star.Name.Equals(decoded.Name, StringComparison.OrdinalIgnoreCase)))
                        stars.Add(Decoded(decoded, from) with { Checked = problem is null });
                    stars.AddRange(located.Select(star => Point("star", star.Name, star.Name, star.X, star.Y, star.Z, from) with
                    {
                        Id64 = star.Id64 > 0 ? star.Id64.ToString(CultureInfo.InvariantCulture) : null,
                    }));
                    return WebResponse.Json(new { results = stars, problem });

                default:
                    return WebResponse.Error("Search for a place or a star.", 400);
            }
        });

        web.Map("GET", "/api/nav/heatmap/area", async request =>
        {
            if (SurveyArea.Of(request.Query.GetValueOrDefault("key")) is not { } area) return WebResponse.Error(NoSuchArea, 400);
            double? cubeLy = null;
            if (request.Query.GetValueOrDefault("cubeLy") is { Length: > 0 } size)
            {
                if (!double.TryParse(size, NumberStyles.Float, CultureInfo.InvariantCulture, out double ly) || !Enumerable.Range(0, 8).Any(mass => Galaxy.CubeLy(mass) == ly))
                    return WebResponse.Error(NoSuchCube, 400);
                cubeLy = ly;
            }
            AreaView? view = await survey.View(area.Key, cubeLy);
            return await Task.Run(() => view is null
                ? WebResponse.Json(new { key = area.Key, stored = false, edastroKnown = EdastroKnown(area, cubes.List) })
                : Json(view));
        });

        web.Map("POST", "/api/nav/heatmap/survey", async request =>
        {
            SurveyBody body = request.Read<SurveyBody>();
            if (SurveyArea.Of(body.Key) is not { } area) return WebResponse.Error(NoSuchArea, 400);
            SurveyAnswer answer = await survey.Survey(area.Key, body.Go);
            if (answer.Problem is { } problem) Log.Error($"3D map: looking inside {answer.Key} did not finish: {problem}");
            // The player is asked first: nothing was listed, and the answer says what the listing still needs.
            if (answer.Needs is { } needs) return WebResponse.Json(new { key = answer.Key, count = answer.Count, capped = answer.Capped, needs });
            return await Task.Run(() =>
            {
                // Shaped like GET area (or its "nothing kept" answer), with how it went.
                JsonObject json = (answer.Area is { } view
                    ? JsonSerializer.SerializeToNode(view, Compact)
                    : JsonSerializer.SerializeToNode(new { key = answer.Key, count = answer.Count, capped = answer.Capped, stored = false, edastroKnown = EdastroKnown(area, cubes.List) }, Compact))!.AsObject();
                json["problem"] = answer.Problem;
                json["stopped"] = answer.Stopped;
                return Json(json);
            });
        });

        web.Map("POST", "/api/nav/heatmap/count", async request =>
        {
            CountBody body = request.Read<CountBody>();
            string kind = (body.Kind ?? "").Trim().ToLowerInvariant();
            if (kind is not ("cube" or "column" or "around")) return WebResponse.Error(NoSuchCount, 400);
            double[]? at = body.X is { } x && body.Y is { } y && body.Z is { } z ? [x, y, z] : kind == "around" ? game.StarPos : null;
            if (at is null) return WebResponse.Error(kind == "around" ? NotHere : "Pick the place to count first.", 400);
            CountAnswer answer;
            try
            {
                answer = await survey.Count(kind, at[0], at[1], at[2], body.Go, body.Again);
            }
            catch (ArgumentOutOfRangeException)
            {
                return WebResponse.Error(OutsideTheGrid, 400);
            }
            if (answer.Problem is { } problem) Log.Error($"3D map: counting the {kind} did not finish: {problem}");
            return WebResponse.Json(answer);
        });

        web.Map("POST", "/api/nav/heatmap/cancel", request =>
        {
            // Each Cancel on the page stops its own job: a survey's must not throw away minutes of a build, nor the
            // reverse. A page from before "what" was there sends none, and stops both, as it did then.
            string what = (request.Read<CancelBody>().What ?? "").Trim().ToLowerInvariant();
            if (what is not ("" or "survey" or "finemap")) return WebResponse.Error(NoSuchCancel, 400);
            if (what != "finemap") survey.Cancel();
            if (what != "survey") fine.Cancel();
            return WebResponse.Json(new { cancelled = true });
        });

        web.Map("POST", "/api/nav/heatmap/finemap", async request =>
        {
            FineBody body = request.Read<FineBody>();
            (int X, int Y, int Z) home = game.StarPos is [var px, var py, var pz] ? Galaxy.SectorOf(px, py, pz) : Sol;
            int ix = body.Ix ?? Math.Clamp(home.X, 0, Galaxy.SectorsAcross - 1), iz = body.Iz ?? Math.Clamp(home.Z, 0, Galaxy.SectorsAcross - 1);
            if (ix is < 0 or >= Galaxy.SectorsAcross || iz is < 0 or >= Galaxy.SectorsAcross) return WebResponse.Error(NoSuchColumn, 400);
            if (!body.Go)
            {
                // The player is told first what it takes: minutes of reading on this PC, and nothing from the internet.
                // (Stat calls and the block files only: not on this thread all the same.)
                return await Task.Run(() =>
                {
                    FineNeeds? needs = fine.Needs();
                    return WebResponse.Json(new { needs, fine = fine.Status(ix, iz), problem = needs is null ? NoFineList : null });
                });
            }
            FineBuildAnswer answer = await fine.Build(ix, iz);
            if (answer.Problem is { } problem) Log.Error($"3D map: the detailed map of {ix},{iz} was not built: {problem}");
            return WebResponse.Json(answer);
        });

        web.Map("GET", "/api/nav/heatmap/cubes", async request =>
        {
            if (!Whole(request.Query, "ix", Galaxy.SectorsAcross, out int ix) || !Whole(request.Query, "iz", Galaxy.SectorsAcross, out int iz))
                return WebResponse.Error(NoSuchColumn, 400);
            if (!Whole(request.Query, "iy", Galaxy.Layers, out int iy)) return WebResponse.Error(NoSuchLayer, 400);
            int size = FineMap.CubeLy;
            if (request.Query.GetValueOrDefault("size") is { Length: > 0 } asked
                && !(int.TryParse(asked.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out size) && size is FineMap.CubeLy or FineMap.SmallCubeLy))
                return WebResponse.Error(NoSuchFineCube, 400);
            // The block file of a busy area runs to megabytes, read once and kept: not on this thread.
            return Json(await Task.Run(() => fine.Cubes(ix, iy, iz, size)));
        });

        web.Map("GET", "/api/nav/heatmap/names", async request =>
        {
            if (Square(request.Query, out string? wrong) is not { } square) return WebResponse.Error(wrong!, 400);
            string? group = request.Query.GetValueOrDefault("group") is { Length: > 0 } asked && asked.Trim().Length > 0 ? asked.Trim() : null;
            (double X, double Y, double Z)? near = null;
            if (request.Query.GetValueOrDefault("near") is { Length: > 0 } point)
            {
                if (QueryPoint(point) is not { } focus) return WebResponse.Error(NoSuchNear, 400);
                near = focus;
            }
            // Reads the kept survey or the detailed map's files away from this thread.
            NamesAnswer answer = await names.Names(square, group, near);
            return await Task.Run(() => Json(answer));
        });

        web.Map("GET", "/api/nav/heatmap/finds", async _ =>
        {
            // finds.json is read from disk the first time: not on this thread.
            (IReadOnlyList<HeatmapFind> kept, bool on) = await Task.Run(() => (marks.Finds, marks.CheckTargets));
            return WebResponse.Json(new { finds = kept, checkTargets = on });
        });

        web.Map("POST", "/api/nav/heatmap/finds/remove", async request =>
        {
            RemoveBody body = request.Read<RemoveBody>();
            if (body.Id64 is not > 0 && string.IsNullOrWhiteSpace(body.Name)) return WebResponse.Error(NoSuchFind, 400);
            (bool removed, IReadOnlyList<HeatmapFind> kept) = await Task.Run(() => (marks.Remove(body.Id64 ?? 0, body.Name), marks.Finds));
            return WebResponse.Json(new { removed, finds = kept });
        });

        object SettingsNow() => new
        {
            checkTargets = marks.CheckTargets, routeCheck = marks.RouteCheck, recordedDays = marks.RecordedDays,
            withinReach = near.On, reachLy = near.RangeLy, navScan = scan.On, navScanVolume = scan.Volume,
        };

        web.Map("GET", "/api/nav/heatmap/settings", _ => WebResponse.Json(SettingsNow()));

        web.Map("POST", "/api/nav/heatmap/settings", request =>
        {
            SettingsBody body;
            try
            {
                body = request.Read<SettingsBody>();
            }
            catch (JsonException)
            {
                return WebResponse.Error(NoSuchSettings, 400);
            }
            // Nothing is changed when any of it is wrong.
            if (body.RouteCheck is { } mode && mode.Trim().ToLowerInvariant() is not (HeatmapFinds.CheckEnds or HeatmapFinds.CheckAll))
                return WebResponse.Error(HeatmapFinds.NoSuchRouteCheck, 400);
            int? days = null;
            if (body.RecordedDays.ValueKind != JsonValueKind.Undefined)
            {
                if (Number(body.RecordedDays) is not { } number || number != Math.Floor(number) || !CheckMemory.DaysOffered.Contains((int)number))
                    return WebResponse.Error(CheckMemory.NoSuchDays, 400);
                days = (int)number;
            }
            bool rangeSent = body.ReachLy.ValueKind != JsonValueKind.Undefined;
            double? range = null;
            if (rangeSent && !(body.ReachLy.ValueKind is JsonValueKind.Null || (body.ReachLy.ValueKind == JsonValueKind.String && body.ReachLy.GetString()!.Trim().Length == 0)))
            {
                if (Number(body.ReachLy) is not { } ly || !HeatmapReach.Typed(ly, out range)) return WebResponse.Error(HeatmapReach.NoSuchRange, 400);
            }
            int? volume = null;
            if (body.NavScanVolume.ValueKind != JsonValueKind.Undefined)
            {
                // A slider may send 70 or 70.0; the volume is kept whole.
                if (Number(body.NavScanVolume) is not { } loud || loud is < 0 or > 100) return WebResponse.Error(NavScan.NoSuchVolume, 400);
                volume = (int)Math.Round(loud);
            }
            // settings.json is the app's one settings file, written on this thread like every other section of it.
            if (body.CheckTargets is { } on) marks.CheckTargets = on;
            if (body.RouteCheck is { } chosen) marks.RouteCheck = chosen;
            if (days is { } kept) marks.RecordedDays = kept;
            // The range before the switch: switched on with a new range, the systems within reach are listed once.
            if (rangeSent) near.RangeLy = range;
            if (body.WithinReach is { } reaching) near.On = reaching;
            if (volume is { } loudness) scan.Volume = loudness;
            if (body.NavScan is { } scanning) scan.On = scanning;
            return WebResponse.Json(SettingsNow());
        });

        // The names may run to hundreds: one line, as the other long answers.
        web.Map("GET", "/api/nav/heatmap/navscan", _ => Json(scan.View()));

        web.Map("POST", "/api/nav/heatmap/navscan/test-sound", request =>
        {
            string sound;
            try
            {
                sound = (request.Read<SoundBody>().Sound ?? "").Trim().ToLowerInvariant();
            }
            catch (JsonException)
            {
                return WebResponse.Error(NavScan.NoSuchSound, 400);
            }
            if (sound is not (NavScanSounds.Ping or NavScanSounds.Click)) return WebResponse.Error(NavScan.NoSuchSound, 400);
            // At the volume set: nothing at 0, which the answer says.
            bool played = scan.TestSound(sound);
            return WebResponse.Json(new { sound, played, volume = scan.Volume });
        });

        web.Map("POST", "/api/nav/heatmap/checks/forget", async _ =>
        {
            int forgotten = marks.ForgetChecks();
            await marks.Memory.SaveAsync();
            Log.Info($"3D map: the memory of checks was emptied at the player's word ({forgotten} systems)");
            // What Within reach says of each system came from the memory.
            near.Retell();
            return WebResponse.Json(new { forgotten });
        });

        // The lists may run to thousands: one line, as the other long answers, put together away from this thread.
        web.Map("GET", "/api/nav/heatmap/reach", async _ =>
        {
            ReachView view = near.Now();
            return await Task.Run(() => Json(view));
        });

        // The stops may run to hundreds: one line, as the other long answers.
        web.Map("GET", "/api/nav/heatmap/route-check", _ => Json(marks.RouteCheckNow()));

        web.Map("GET", "/api/nav/heatmap/counted", _ => WebResponse.Json(new { cubes = survey.Counted }));

        web.Map("GET", "/api/nav/heatmap/firsts", async _ => WebResponse.Json(new { systems = await firsts.Systems() }));

        // The copy on this PC is read from disk the first time: not on this thread.
        web.Map("GET", "/api/nav/heatmap/nebulae", async _ =>
            WebResponse.Json(await Task.Run(() => new { have = nebulae.Nebulae is not null, stale = nebulae.Stale, problem = (string?)null })));

        web.Map("POST", "/api/nav/heatmap/nebulae", async _ =>
        {
            string? problem = await nebulae.Refresh();
            if (problem is not null) Log.Error("EDAstro's nebula list could not be fetched: " + problem);
            return WebResponse.Json(new { have = nebulae.Nebulae is not null, stale = nebulae.Stale, problem });
        });
        return marks;
    }

    /// <summary>JSON on one line (<see cref="Compact"/>).</summary>
    static WebResponse Json(object value) => new(200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, Compact));

    /// <summary>A number as the page may send it: a JSON number, or text that reads as one. Null for anything else.</summary>
    static double? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out double number) && double.IsFinite(number) => number,
        JsonValueKind.String when double.TryParse(value.GetString()!.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) => number,
        _ => null,
    };

    /// <summary>A number from the query that must be there, from 0 to below <paramref name="below"/>.</summary>
    static bool Whole(IReadOnlyDictionary<string, string> query, string name, int below, out int number)
    {
        number = 0;
        return query.GetValueOrDefault(name) is { Length: > 0 } text
            && int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number) && number >= 0 && number < below;
    }

    /// <summary>A column number from the query: <paramref name="fallback"/> when it is left out; false when it is no column of the grid.</summary>
    static bool Column(IReadOnlyDictionary<string, string> query, string name, int fallback, out int column)
    {
        column = fallback;
        return query.GetValueOrDefault(name) is not { Length: > 0 } text
            || (int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out column) && column is >= 0 and < Galaxy.SectorsAcross);
    }

    /// <summary>
    /// The square GET names asks about: a sector ("s:" key) or a nebula ("n:" key) whole, or a cube given by its corner
    /// and size, inside the sector or nebula its key names (whose kept survey holds its names). Null with the reason
    /// for a square that is none.
    /// </summary>
    static NameSquare? Square(IReadOnlyDictionary<string, string> query, out string? wrong)
    {
        wrong = NoSuchArea;
        string kind = (query.GetValueOrDefault("kind") ?? NameSquare.Sector).Trim().ToLowerInvariant();
        if (kind is not (NameSquare.Sector or NameSquare.Cube or NameSquare.Nebula))
        {
            wrong = NoSuchNamesKind;
            return null;
        }
        if (SurveyArea.Of(query.GetValueOrDefault("key")) is not { } area) return null;
        bool nebula = area.Key.StartsWith("n:", StringComparison.Ordinal);
        if ((kind == NameSquare.Sector && nebula) || (kind == NameSquare.Nebula && !nebula)) return null;
        if (kind != NameSquare.Cube)
        {
            wrong = null;
            return new NameSquare(kind, area, area.Box);
        }

        double[] corner = new double[4];
        string[] given = ["x0", "y0", "z0", "size"];
        for (int at = 0; at < given.Length; at++)
            if (!double.TryParse(query.GetValueOrDefault(given[at]), NumberStyles.Float, CultureInfo.InvariantCulture, out corner[at]) || !double.IsFinite(corner[at]))
            {
                wrong = NoSuchNamesCube;
                return null;
            }
        if (corner[3] is not (> 0 and <= Galaxy.SectorLy))
        {
            wrong = NoSuchNamesCube;
            return null;
        }
        wrong = null;
        return new NameSquare(kind, area, new Box(corner[0], corner[1], corner[2], corner[0] + corner[3], corner[1] + corner[3], corner[2] + corner[3]));
    }

    /// <summary>A point as the page writes it: "x,y,z" in light years. Null for anything else.</summary>
    static (double X, double Y, double Z)? QueryPoint(string text)
    {
        string[] parts = text.Split(',');
        var at = new double[3];
        if (parts.Length != 3) return null;
        for (int axis = 0; axis < 3; axis++)
            if (!double.TryParse(parts[axis].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out at[axis]) || !double.IsFinite(at[axis])) return null;
        return (at[0], at[1], at[2]);
    }

    /// <summary>Some part of the nebula lies in the box: one of its spheres reaches it.</summary>
    static bool Touches(Nebula nebula, Box box) => nebula.Spheres.Any(sphere =>
    {
        double dx = Math.Max(0, Math.Max(box.X0 - sphere.X, sphere.X - box.X1));
        double dy = Math.Max(0, Math.Max(box.Y0 - sphere.Y, sphere.Y - box.Y1));
        double dz = Math.Max(0, Math.Max(box.Z0 - sphere.Z, sphere.Z - box.Z1));
        return dx * dx + dy * dy + dz * dz <= sphere.Radius * sphere.Radius;
    });

    /// <summary>
    /// What EDAstro's sector list says of an area that has not been looked inside: the systems named
    /// after it. Null when there is no list on this PC, 0 when the list has no row for it.
    /// </summary>
    static int? EdastroKnown(SurveyArea area, SectorList? list)
    {
        if (list is null) return null;
        if (area.Key.StartsWith("n:", StringComparison.Ordinal)) return list.Nebula(area.Key[2..])?.Known ?? 0;
        (double x, double y, double z) = area.Box.Centre;
        (int ix, int iy, int iz) = Galaxy.SectorOf(x, y, z);
        return list.At(ix, iy, iz)?.Known ?? 0;
    }

    // ------------------------------------------------------------ the search for a place

    /// <summary>
    /// Nebulae and sectors whose names match what the player typed, best first, asking nobody: the
    /// nebulae of the table that comes with Studio (by name, by name without " Sector", and by their
    /// other names), the nebulae of EDAstro's list when it is on this PC, and the sectors of EDAstro's
    /// sector list (with any sector's name typed in full, even one the list does not have). Those the
    /// text begins come first, then those with a word it begins, then those that hold it anywhere,
    /// then those found by the rule for "… Nebula" (<see cref="HaRegions.Find"/>); within each, the
    /// nearest to the commander first.
    /// </summary>
    static IReadOnlyList<MapFind> Places(string text, (double X, double Y, double Z)? from, SectorList? list, IReadOnlyList<ListedNebula>? listed)
    {
        string query = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (query.Length == 0) return [];
        var found = new List<(int Rank, MapFind Place)>();

        foreach (Nebula nebula in HaRegions.All)
            if (Rank(query, NebulaNames[nebula]) is >= 0 and var rank)
                found.Add((rank, Point("nebula", nebula.Name, nebula.Label, nebula.X, nebula.Y, nebula.Z, from) with { R = nebula.Radius, Permit = nebula.Permit }));

        // EDAstro's names that the table already knows ("California Nebula", "Pleiades") are its nebulae, found above.
        foreach (ListedNebula nebula in listed ?? [])
            if (HaRegions.Named(nebula.Name) is null && Rank(query, [nebula.Name]) is >= 0 and var rank)
                found.Add((rank, Point("nebula", nebula.Name, nebula.Name, nebula.X, nebula.Y, nebula.Z, from) with
                {
                    Permit = HaRegions.Containing(nebula.X, nebula.Y, nebula.Z).Any(around => around.Permit),
                }));

        foreach (SectorRow row in list?.Sectors ?? [])
            if (Rank(query, [row.Name]) is >= 0 and var rank) found.Add((rank, Sector(row.X, row.Y, row.Z, row.Name, from)));
        if (ProcGenNames.SectorIndex(query) is { } index && list?.At(index.X, index.Y, index.Z) is null
            && ProcGenNames.SectorName(index.X, index.Y, index.Z) is { } named)
            found.Add((0, Sector(index.X, index.Y, index.Z, named, from)));

        return [.. found.OrderBy(place => place.Rank).ThenBy(place => place.Place.Distance ?? 0)
            .ThenBy(place => place.Place.Name, StringComparer.OrdinalIgnoreCase).Take(MostFound).Select(place => place.Place)];
    }

    /// <summary>
    /// How well a name matches the text: 0 when one of its names begins with it, 1 when a word in one
    /// does, 2 when one holds it anywhere; 3 to 5 the same for the text without a last word that begins
    /// "nebula" ("Rosette Neb" finds Rosette Sector). -1 for no match. Capitals do not matter.
    /// </summary>
    static int Rank(string query, IEnumerable<string> names)
    {
        if (Match(query, names) is >= 0 and var rank) return rank;
        int space = query.LastIndexOf(' ');
        return space > 0 && "nebula".StartsWith(query[(space + 1)..], StringComparison.OrdinalIgnoreCase) && Match(query[..space], names) is >= 0 and var shorter
            ? shorter + 3
            : -1;

        static int Match(string query, IEnumerable<string> names)
        {
            string word = " " + query;
            int best = -1;
            foreach (string name in names)
            {
                int rank = name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0
                    : name.Contains(word, StringComparison.OrdinalIgnoreCase) ? 1
                    : name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2
                    : -1;
                if (rank >= 0 && (best < 0 || rank < best)) best = rank;
            }
            return best;
        }
    }

    /// <summary>The name without " Sector" at its end, the way players say it.</summary>
    static string Short(string name) => name.EndsWith(" Sector", StringComparison.Ordinal) ? name[..^" Sector".Length] : name;

    /// <summary>A sector of the grid found by the search, at its middle.</summary>
    static MapFind Sector(int x, int y, int z, string name, (double X, double Y, double Z)? from)
    {
        (double cx, double cy, double cz) = Galaxy.SectorBounds(x, y, z).Centre;
        return new MapFind("sector", name, name, cx, cy, cz, [x, y, z], Distance(from, cx, cy, cz));
    }

    /// <summary>A system placed from its generated name: the middle of the cube the name names.</summary>
    static MapFind Decoded(DecodedName decoded, (double X, double Y, double Z)? from)
    {
        (double x, double y, double z) = decoded.Cube.Centre;
        return new MapFind("decoded", decoded.Name, decoded.Name, x, y, z, [decoded.Cube.SectorX, decoded.Cube.SectorY, decoded.Cube.SectorZ], Distance(from, x, y, z))
        {
            PlusMinus = decoded.HalfWidth,
            Id64 = decoded.Cube.Address.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Anything found at a point, with the sector the point lies in.</summary>
    static MapFind Point(string kind, string name, string label, double x, double y, double z, (double X, double Y, double Z)? from)
    {
        (int ix, int iy, int iz) = Galaxy.SectorOf(x, y, z);
        return new MapFind(kind, name, label, x, y, z, [ix, iy, iz], Distance(from, x, y, z));
    }

    static double? Distance((double X, double Y, double Z)? from, double x, double y, double z) =>
        from is { } at ? Math.Sqrt((at.X - x) * (at.X - x) + (at.Y - y) * (at.Y - y) + (at.Z - z) * (at.Z - z)) : null;
}
