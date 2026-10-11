using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.UI;

namespace EliteStudio.Loadouts;

/// <summary>
/// Studio Loadouts: shows the ship being flown, keeps named copies of builds, hands a build to EDSY
/// or Coriolis, and serves the ship designer. All routes live under /api/loadouts/. A saved build
/// is handed on to the other studios (Shopping.cs):
///   GET  shopping?id=        what the ship being flown lacks to become that build (<see cref="ShoppingList"/>)
///   POST shopping/stops      {"id":""} where to buy it all in the fewest stops, asked of Spansh (<see cref="ShoppingTrip"/>);
///                            the page hands the stops to Studio Nav (POST /api/nav/trip)
///   POST pin                 {"id":""} pins the build's engineering in the engineering planner; answers
///                            {"added":2,"already":1,"notOffered":["Cargo Rack: Expanded Capacity"]}
/// The designer's are under /api/loadouts/designer/:
///   GET  ships               every hull that can be designed on: {"ships":[{"ship":"mandalay","name":"Mandalay"}]}
///   GET  start               a build for the bench: ?ship= an empty hull, ?id= a saved build, neither = the ship being flown.
///                            Answers {"name": the saved build's, "loadout": the journal's Loadout event, "design": <see cref="DesignView"/>}
///   POST view                {"loadout":...} as the page has changed it; answers its <see cref="DesignView"/>
///   GET  fits?ship=&amp;slot=    the modules that go in a slot, by kind
///   GET  engineering?item=   the blueprints and experimental effects on offer for a module
///   POST save                {"name":"","loadout":...} keeps the build with the saved builds
/// The designer keeps nothing here between calls: the build on the bench lives in the page.
/// </summary>
public static class LoadoutsApi
{
    /// <summary>Registers the routes. Call on the UI thread, which is also where the server runs them.</summary>
    /// <param name="planner">The engineering planner, which a build's engineering is pinned in.</param>
    /// <param name="finder">Studio Market's searches, which find the stations a shopping list is bought at.</param>
    internal static void Map(WebServer web, GameState game, EngineeringPlanner planner, Market.Finder finder)
    {
        var loadouts = new StudioLoadouts(Path.Combine(Paths.DataDir, "loadouts"));
        loadouts.Map(web, game);
        loadouts.MapHandOffs(web, game, planner, finder);
    }
}

/// <summary>
/// Everything behind the /api/loadouts/ routes. A build is always kept as the game's own "Loadout"
/// journal event, because that is what EDSY and Coriolis read; the page's view is made from it on
/// request. Saved builds are one small JSON file each in <paramref name="folder"/>, named after the
/// build by <see cref="Paths.FileNameFor"/>; that file name without ".json" is the build's id.
/// Runs on the app's main thread like every route; files are read and written without holding it up.
/// (The part that hands a build on to the other studios is in Shopping.cs.)
/// </summary>
/// <param name="folder">Full path: Windows only uses the Recycle Bin for a file named by its full path.</param>
internal sealed partial class StudioLoadouts(string folder)
{
    const string NoShip = "No ship is known yet. Start Elite Dangerous and board your ship.";
    const string NotFound = "That saved build was not found.";
    const string NoNumbers = "Studio has no numbers for this ship yet, so the designer cannot work on it.";
    static readonly string BadName = $"Please give the build a name of up to {Paths.LongestFileName} characters that Windows can use as a file name.";

    /// <summary>
    /// The newest Loadout event of the journal, as the game wrote it, with the time on it; null until
    /// there is one. The page shows the time: with Elite closed this is the last session's ship, and
    /// the game writes no Loadout after an engineer's work, so the picture can be hours or days old.
    /// </summary>
    (JsonElement Loadout, DateTime ReportedUtc)? current;

    // What reaches outside this class. Tests swap these so nothing lands in the real Recycle Bin and times are known.
    internal Action<string> Recycle { get; init; } = RecycleBin.Send;
    internal Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    /// <summary>A saved build's file.</summary>
    /// <param name="Loadout">The journal's Loadout event, whole.</param>
    sealed record SavedFile(string Name, DateTime SavedUtc, JsonElement Loadout);

    /// <summary>What a page sends with a request; each route reads the fields it needs.</summary>
    sealed class Body
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";

        /// <summary>The build on the designer's bench, in the shape of the journal's Loadout event.</summary>
        public JsonElement Loadout { get; set; }
    }

    public void Map(WebServer web, GameState game)
    {
        // Replayed lines count too: the ship is state, and the newest Loadout of the journal is the ship.
        game.Journal += line =>
        {
            if (line.Event != "Loadout") return;
            // A ship-launched fighter or an SRV is not a build anyone hands to a ship designer (EDMC skips them the same way).
            string ship = line.Data.Text("Ship");
            if (ship.Contains("fighter", StringComparison.OrdinalIgnoreCase) || ship.Contains("buggy", StringComparison.OrdinalIgnoreCase)) return;
            current = (line.Data, line.TimeUtc);
            web.Send("loadouts", Current());
        };

        web.Map("GET", "/api/loadouts/ship", _ => WebResponse.Json(Current()));
        web.Map("GET", "/api/loadouts/saved", _ => Saved());
        web.Map("GET", "/api/loadouts/saved/one", request => One(request.Query.GetValueOrDefault("id") ?? ""));
        web.Map("POST", "/api/loadouts/save", request =>
            current is { } ship ? Save(request.Read<Body>().Name, ship.Loadout) : Task.FromResult(WebResponse.Error(NoShip, 409)));
        web.Map("POST", "/api/loadouts/rename", request => Rename(request.Read<Body>()));
        web.Map("POST", "/api/loadouts/delete", request => Delete(request.Read<Body>().Id));
        web.Map("GET", "/api/loadouts/export", request =>
            Export(request.Query.GetValueOrDefault("id") ?? "", request.Query.GetValueOrDefault("to") ?? ""));

        web.Map("GET", "/api/loadouts/designer/ships", _ => WebResponse.Json(new
        {
            ships = ShipData.Hulls.Keys.Select(ship => new { ship, name = ShipMath.ShipName(ship) }).OrderBy(hull => hull.name, StringComparer.Ordinal),
        }));
        web.Map("GET", "/api/loadouts/designer/start", request =>
            Start(request.Query.GetValueOrDefault("ship") ?? "", request.Query.GetValueOrDefault("id") ?? ""));
        web.Map("POST", "/api/loadouts/designer/view", request =>
            DesignView.From(ShipLoadout.Parse(request.Read<Body>().Loadout)) is { } design ? WebResponse.Json(design) : WebResponse.Error(NoNumbers, 400));
        web.Map("GET", "/api/loadouts/designer/fits", request =>
            Fits(request.Query.GetValueOrDefault("ship") ?? "", request.Query.GetValueOrDefault("slot") ?? ""));
        web.Map("GET", "/api/loadouts/designer/engineering", request => Engineering(request.Query.GetValueOrDefault("item") ?? ""));
        web.Map("POST", "/api/loadouts/designer/save", request =>
        {
            Body body = request.Read<Body>();
            return Finished(body.Loadout) is { } loadout ? Save(body.Name, loadout) : Task.FromResult(WebResponse.Error(NoNumbers, 400));
        });
    }

    static ShipView View(JsonElement loadout) => ShipView.From(ShipLoadout.Parse(loadout));

    /// <summary>The ship being flown and when the game last reported it, as the route and the "loadouts" event both give it.</summary>
    object Current() => new { ship = current is { } now ? View(now.Loadout) : null, reportedUtc = current?.ReportedUtc };

    // ------------------------------------------------------------ saved builds

    async Task<WebResponse> Saved()
    {
        var saved = new List<(DateTime When, object Build)>();
        if (Directory.Exists(folder))
        {
            foreach (FileInfo file in new DirectoryInfo(folder).GetFiles("*.json"))
            {
                string id = Path.GetFileNameWithoutExtension(file.Name);
                SavedFile? build;
                try
                {
                    build = await Read(id);
                }
                catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
                {
                    // Another program (antivirus, a backup tool) has this one open: the other builds are still listed.
                    Log.Error($"Saved build {id} could not be read", problem);
                    continue;
                }
                if (build is null) continue;   // not one of ours, or damaged: left alone and not listed
                saved.Add((build.SavedUtc, new { id, build.Name, build.SavedUtc, ShipType = ShipMath.ShipName(ShipLoadout.Parse(build.Loadout).Ship) }));
            }
        }
        return WebResponse.Json(new { saved = saved.OrderByDescending(s => s.When).Select(s => s.Build) });   // newest first
    }

    async Task<WebResponse> One(string id) =>
        await Read(id) is { } build
            ? WebResponse.Json(new { id, build.Name, build.SavedUtc, ship = View(build.Loadout) })
            : WebResponse.Error(NotFound, 404);

    async Task<WebResponse> Save(string name, JsonElement loadout)
    {
        if (Paths.FileNameFor(name) is not string id) return WebResponse.Error(BadName, 400);
        if (File.Exists(FileOf(id))) return Taken(name);
        await Write(id, new SavedFile(name.Trim(), Now(), loadout));
        Log.Info($"Loadout saved: {id}");
        return WebResponse.Json(new { ok = true, id });
    }

    async Task<WebResponse> Rename(Body body)
    {
        if (await Read(body.Id) is not { } build) return WebResponse.Error(NotFound, 404);
        if (Paths.FileNameFor(body.Name) is not string id) return WebResponse.Error(BadName, 400);
        // A change of capitals only is the same file to Windows, so it is not "taken".
        if (!id.Equals(body.Id, StringComparison.OrdinalIgnoreCase) && File.Exists(FileOf(id))) return Taken(body.Name);
        await Write(body.Id, build with { Name = body.Name.Trim() });
        if (id != body.Id) File.Move(FileOf(body.Id), FileOf(id));
        return WebResponse.Json(new { ok = true, id });
    }

    async Task<WebResponse> Delete(string id)
    {
        if (!Paths.IsFileName(id) || !File.Exists(FileOf(id))) return WebResponse.Error(NotFound, 404);
        await Task.Run(() => Recycle(FileOf(id)));   // Windows can take a moment over this
        Log.Info($"Loadout moved to the Recycle Bin: {id}");
        return WebResponse.Json(new { ok = true });
    }

    /// <summary>The build for another program: a link that opens it in EDSY or Coriolis, or SLEF text to paste.</summary>
    /// <param name="id">A saved build, or "" for the ship being flown.</param>
    async Task<WebResponse> Export(string id, string to)
    {
        JsonElement? build = id.Length == 0 ? current?.Loadout : (await Read(id))?.Loadout;
        if (build is not { } loadout) return id.Length == 0 ? WebResponse.Error(NoShip, 409) : WebResponse.Error(NotFound, 404);
        return to switch
        {
            "edsy" => WebResponse.Json(new { url = HandOff.Url(HandOff.Edsy, loadout) }),
            "coriolis" => WebResponse.Json(new { url = HandOff.Url(HandOff.Coriolis, loadout) }),
            "slef" => WebResponse.Json(new { text = HandOff.Slef(loadout, Program.Version) }),
            _ => WebResponse.Error("A build can be exported to edsy, coriolis or slef.", 400),
        };
    }

    // ------------------------------------------------------------ the ship designer

    /// <summary>A build to put on the designer's bench: an empty hull of that ship, or that saved build, or else the ship being flown.</summary>
    async Task<WebResponse> Start(string ship, string id)
    {
        SavedFile? saved = ship.Length == 0 && id.Length > 0 ? await Read(id) : null;
        JsonElement? build = ship.Length > 0 ? (ShipData.Hulls.TryGetValue(ship, out ShipData.Hull? hull) ? EmptyHull(hull) : null)
            : id.Length > 0 ? saved?.Loadout
            : current?.Loadout;
        if (build is not { } loadout)
            return ship.Length > 0 ? WebResponse.Error(NoNumbers, 404) : id.Length > 0 ? WebResponse.Error(NotFound, 404) : WebResponse.Error(NoShip, 409);
        return DesignView.From(ShipLoadout.Parse(loadout)) is { } design
            ? WebResponse.Json(new { name = saved?.Name ?? "", loadout, design })
            : WebResponse.Error(NoNumbers, 409);
    }

    /// <summary>
    /// A ship as it could leave the shipyard, in the journal's shape: the plainest armour and the
    /// cheapest core module of the full size in each core slot, nothing else but the cargo hatch.
    /// </summary>
    JsonElement EmptyHull(ShipData.Hull hull)
    {
        var modules = new JsonArray();
        void Fit(string slot, string item) => modules.Add(new JsonObject { ["Slot"] = slot, ["Item"] = item, ["On"] = true, ["Priority"] = 0, ["Health"] = 1.0 });
        foreach (ShipData.Slot slot in hull.Slots.Where(slot => ShipView.Place(slot.Id)?.Group == 2))
            if (ShipData.Fits(hull, slot).Where(part => part.Class == slot.Size).MinBy(part => part.Cost) is { } part)
                Fit(slot.Id, part.Item.ToLowerInvariant());
        Fit("CargoHatch", "modularcargobaydoor");
        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["timestamp"] = Stamp(), ["event"] = "Loadout", ["Ship"] = hull.Ship, ["ShipName"] = "", ["ShipIdent"] = "", ["HullHealth"] = 1.0, ["Modules"] = modules,
        });
    }

    /// <summary>
    /// A build from the designer as the game would have written that ship. The page sends the
    /// modules; the numbers the game puts at the top of a Loadout event (weight, best jump, hold,
    /// tank, value) are worked out here, so the "My ship" page and the sites a build is handed to
    /// read them as they would a real ship's. Null for a ship the tables do not have.
    /// </summary>
    JsonElement? Finished(JsonElement design)
    {
        if (BuildStats.Of(ShipLoadout.Parse(design)) is not { } stats) return null;
        JsonObject loadout = JsonObject.Create(design)!;
        loadout["timestamp"] = Stamp();
        loadout["event"] = "Loadout";
        loadout["HullValue"] = stats.HullCost;
        loadout["ModulesValue"] = stats.ModulesCost;
        loadout["Rebuy"] = stats.Cost / 20;   // the insurance pays 95%
        loadout["UnladenMass"] = Math.Round(stats.UnladenMass, 6);
        loadout["CargoCapacity"] = stats.CargoCapacity;
        loadout["MaxJumpRange"] = Math.Round(stats.JumpMax, 6);
        loadout["FuelCapacity"] = new JsonObject { ["Main"] = stats.FuelCapacity, ["Reserve"] = stats.ReserveFuel };
        return JsonSerializer.SerializeToElement(loadout);
    }

    /// <summary>The time as the journal writes it.</summary>
    string Stamp() => Now().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>The modules that go in a slot of a ship, by kind in the alphabet, the biggest and best of each kind first.</summary>
    static WebResponse Fits(string ship, string slotId)
    {
        if (!ShipData.Hulls.TryGetValue(ship, out ShipData.Hull? hull)
            || hull.Slots.FirstOrDefault(known => known.Id.Equals(slotId, StringComparison.OrdinalIgnoreCase)) is not { } slot)
            return WebResponse.Error("Studio's tables do not have that slot for this ship, so it cannot say what fits there.", 404);
        return WebResponse.Json(new
        {
            kinds = ShipData.Fits(hull, slot).Select(part => (Part: part, Shown: Names.Module(part.Item)))
                .GroupBy(module => module.Shown.Name).OrderBy(kind => kind.Key, StringComparer.Ordinal)
                .Select(kind => new
                {
                    name = kind.Key,
                    modules = kind.OrderByDescending(module => module.Part.Class).ThenBy(module => module.Part.Rating, StringComparer.Ordinal)
                        .ThenBy(module => module.Shown.Mount, StringComparer.Ordinal)
                        .Select(module => new
                        {
                            item = module.Part.Item.ToLowerInvariant(), size = module.Part.Class, module.Part.Rating, module.Shown.Mount,
                            mass = module.Part.Number("mass"), power = module.Part.Number("power"), module.Part.Cost,
                        }),
                }),
        });
    }

    /// <summary>What engineers offer for a module; two empty lists for one they do not touch.</summary>
    static WebResponse Engineering(string item)
    {
        ShipData.Workshop offers = ShipData.Find(item) is { } part ? ShipData.Engineering(part.Group) : ShipData.Workshop.None;
        return WebResponse.Json(new
        {
            blueprints = offers.Blueprints.Select(blueprint => new { blueprint.Code, name = Names.Blueprint(blueprint.Code), grades = blueprint.Grades.Keys.Order() }),
            offers.Experimentals,
        });
    }

    static WebResponse Taken(string name) =>
        WebResponse.Error($"You already have a saved build called \"{name.Trim()}\". Pick another name.", 409);

    string FileOf(string id) => Path.Combine(folder, id + ".json");

    /// <summary>A saved build by its id, or null when there is none or the file does not hold a build.</summary>
    async Task<SavedFile?> Read(string id)
    {
        if (!Paths.IsFileName(id) || !File.Exists(FileOf(id))) return null;
        try
        {
            SavedFile? build = JsonSerializer.Deserialize<SavedFile>(await File.ReadAllTextAsync(FileOf(id)), Settings.Json);
            return build is { Name: not null, Loadout.ValueKind: JsonValueKind.Object } ? build : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    async Task Write(string id, SavedFile build)
    {
        Directory.CreateDirectory(folder);
        // Written beside the file and swapped in, so a crash mid-save cannot leave half a build.
        string temp = FileOf(id) + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(build, Settings.Json));
        File.Move(temp, FileOf(id), overwrite: true);
    }

}

/// <summary>
/// Hands a build to the community's ship designers the way EDMarketConnector does. Pure functions.
///
/// The link carries the whole Loadout event: written without spaces, gzip-compressed, then base64
/// in the URL-safe alphabet (EDSY cuts its address at every "/") with each "=" written as %3D
/// (Coriolis cuts its address at "="). A real engineered ship makes a link of about 2,600
/// characters. That is past the 2,083 that old browsers take and that Windows' own "open this
/// link" call is known for (EDMarketConnector works around the same limit; not tried on this
/// PC), so the page never opens the link directly: it opens Studio's own short address
/// (/loadouts/?open=edsy), and that page fetches the link and goes there.
/// </summary>
public static class HandOff
{
    public const string Edsy = "https://edsy.org/#/I=";
    public const string Coriolis = "https://coriolis.io/import?data=";

    /// <summary>The fields of a Loadout event that make up a SLEF build, in the order EDMarketConnector writes them.</summary>
    static readonly string[] SlefFields =
    [
        "FuelCapacity", "Ship", "ShipName", "ShipIdent", "HullValue", "ModulesValue", "Rebuy",
        "MaxJumpRange", "UnladenMass", "CargoCapacity", "Modules",
    ];

    // Names typed by the player (the ship's name) stay as typed instead of becoming \u codes.
    static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonSerializerOptions Readable = new(Compact) { WriteIndented = true };

    /// <summary>The link that opens a build on a site.</summary>
    /// <param name="site"><see cref="Edsy"/> or <see cref="Coriolis"/>.</param>
    /// <param name="loadout">The journal's Loadout event. EDSY insists on its "event":"Loadout" field, so it is sent whole.</param>
    public static string Url(string site, JsonElement loadout) => site + Encode(JsonSerializer.Serialize(loadout, Compact));

    /// <summary>Text packed for a link. Do not escape the result again: %3D would become %253D.</summary>
    public static string Encode(string json)
    {
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(json));
        return Convert.ToBase64String(packed.ToArray()).Replace('+', '-').Replace('/', '_').Replace("=", "%3D");
    }

    /// <summary>
    /// The build as SLEF (Ship Loadout Exchange Format) text, which EDSY, Coriolis, Inara and Spansh
    /// all accept when pasted: a list with one build, a header naming this app, and the Loadout
    /// event's own fields as its data.
    /// </summary>
    public static string Slef(JsonElement loadout, string appVersion)
    {
        var data = new JsonObject();
        foreach (string field in SlefFields)
            if (loadout.TryGetProperty(field, out JsonElement value)) data[field] = JsonSerializer.SerializeToNode(value);
        var build = new JsonObject
        {
            ["header"] = new JsonObject { ["appName"] = "Elite Studio", ["appVersion"] = appVersion },
            ["data"] = data,
        };
        return new JsonArray(build).ToJsonString(Readable);
    }
}
