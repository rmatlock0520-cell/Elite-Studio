using System.Text.Json;
using EliteStudio.App;

namespace EliteStudio.Core;

/// <summary>The short summary of the game that every page and the overlay shows. Sent as JSON by /api/state.</summary>
public sealed record GameSnapshot
{
    public bool GameRunning { get; init; }          // the Elite process exists
    public string? Commander { get; init; }
    public string? ShipType { get; init; }          // display name, e.g. "Type-8 Transporter"
    public string? ShipName { get; init; }          // the player's own name for the ship
    public string? StarSystem { get; init; }
    public double[]? StarPos { get; init; }         // [x, y, z] in light years
    public bool Docked { get; init; }
    public string? Station { get; init; }           // set while docked
    public int CargoCapacity { get; init; }         // tons
    public int CargoCount { get; init; }            // tons on board
    public double JumpLaden { get; init; }          // light years with a full hold
    public double JumpEmpty { get; init; }          // light years with an empty hold
    public string? RouteDestination { get; init; }  // last system of the plotted route
    public int JumpsLeft { get; init; }             // on the plotted route; 0 when none
    public string Mode { get; init; } = "unknown";  // ship | fighter | srv | foot | unknown
    public int GuiFocus { get; init; }              // which game screen is open (Status.json GuiFocus)
    public DateTime? LastEventUtc { get; init; }    // time of the newest journal line read
}

/// <summary>
/// One journal line handed to the studios after the state has been updated from it. What the state
/// takes from the game's other files (Cargo, CargoCount, Market, Route, Status) is not part of
/// that: see <see cref="GameState.Journal"/>.
/// </summary>
/// <param name="Replay">True for lines read while catching up at start-up: react to state, not to the event.</param>
public readonly record struct JournalLine(string Event, JsonElement Data, DateTime TimeUtc, bool Replay);

/// <summary>
/// One live picture of the game, rebuilt from the journal and the companion files.
/// Lives on the UI thread; only <see cref="GameWatcher"/> changes it (and <see cref="AddUnlocks"/>,
/// for the one thing the game's files of today cannot tell).
/// </summary>
public sealed class GameState
{
    /// <summary>The JetConeBoost value of a white dwarf's jet; a neutron star's is 4. See <see cref="Supercharged"/>.</summary>
    const double WhiteDwarfBoost = 1.5;

    /// <summary>
    /// The three kinds of engineering material, in the game's own order: the names of the three lists
    /// of its Materials line, and the words the bundled table sorts each material under.
    /// </summary>
    internal static readonly string[] MaterialKinds = ["Raw", "Manufactured", "Encoded"];

    /// <summary>
    /// A mission's material rewards come in one list, those for use on foot included; these are
    /// the categories of the on-foot ones, which are no ship materials.
    /// </summary>
    static readonly HashSet<string> OnFootRewards =
    [
        "$MICRORESOURCE_CATEGORY_Item;", "$MICRORESOURCE_CATEGORY_Component;", "$MICRORESOURCE_CATEGORY_Data;", "$MICRORESOURCE_CATEGORY_Consumable;",
    ];

    readonly Dictionary<string, int> paid = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> materials = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<long, EngineerStanding> engineers = [];
    readonly HashSet<string> unlocked = new(StringComparer.OrdinalIgnoreCase);
    StationMarket? market;

    /// <summary>Raised after the state changed, at most once per <see cref="GameWatcher.Poll"/>.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised for every journal line, in file order, once the state holds what the line itself says.
    /// <see cref="Cargo"/>, <see cref="CargoCount"/>, <see cref="Market"/>, <see cref="Route"/> and
    /// <see cref="Status"/> come from the game's other files, which are read after all the lines of
    /// a Poll. A listener handling a "Cargo", "Market" or "NavRoute" line therefore still sees the
    /// old ones: use those five in <see cref="Changed"/>, raised at the end of the Poll when the
    /// files have been read (one the game is still writing arrives with a later Poll; for
    /// <see cref="Status"/> see there).
    /// </summary>
    public event Action<JournalLine>? Journal;

    /// <summary>The Elite process exists. Checked at most every two seconds.</summary>
    public bool GameRunning { get; internal set; }

    public string? Commander { get; private set; }

    public string? StarSystem { get; private set; }

    /// <summary>
    /// The game's own number for the current system, which tells two systems apart for certain
    /// where names can be spelled differently or change; 0 when the journal did not give one.
    /// </summary>
    public long SystemAddress { get; private set; }

    /// <summary>Where the current system is: [x, y, z] in light years. Docking does not update it; jumping does.</summary>
    public double[]? StarPos { get; private set; }

    public bool Docked { get; private set; }

    /// <summary>The station's name while docked, otherwise null.</summary>
    public string? Station { get; private set; }

    /// <summary>The docked station's market id, otherwise 0.</summary>
    public long MarketId { get; private set; }

    /// <summary>Journal ship code in lower case, e.g. "type8"; <see cref="ShipMath.ShipName"/> gives the display name.</summary>
    public string? Ship { get; private set; }

    /// <summary>The whole ship as last reported: its name, id and every module. Null until the game reports one.</summary>
    public ShipLoadout? Loadout { get; private set; }

    /// <summary>Tons the hold takes.</summary>
    public int CargoCapacity { get; private set; }

    /// <summary>Best possible jump in light years: no cargo and only the fuel for one jump on board.</summary>
    public double MaxJumpRange { get; private set; }

    /// <summary>The ship's weight in tons without fuel and cargo.</summary>
    public double UnladenMass { get; private set; }

    /// <summary>Main fuel tank, tons.</summary>
    public double FuelCapacity { get; private set; }

    /// <summary>The most fuel one jump burns, tons.</summary>
    public double FsdFuel { get; private set; }

    /// <summary>Light years a fitted Guardian FSD Booster adds; 0 without one.</summary>
    public double BoosterLy { get; private set; }

    /// <summary>
    /// The frame shift drive holds a neutron star's supercharge, so the next jump reaches about
    /// four times as far. The game says so once, with a JetConeBoost line, when the ship has flown
    /// through the star's jet; nothing in its files repeats it. The next jump spends the charge.
    /// Studio also counts it as gone when the ship is destroyed, when the player boards another
    /// ship and at every log-in, where the journal no longer says either way: a route planned on a
    /// charge that is not there cannot be flown, while one planned without a charge that is there
    /// only wastes it. A white dwarf's weaker boost and fuel injection do not count: route
    /// plotters mean the neutron kind.
    /// </summary>
    public bool Supercharged { get; private set; }

    /// <summary>Light years per jump with a full tank and an empty hold; 0 while the ship is not known.</summary>
    public double JumpEmpty => JumpRanges.Empty;

    /// <summary>Light years per jump with a full tank and a full hold; 0 while the ship is not known.</summary>
    public double JumpLaden => JumpRanges.Laden;

    (double Empty, double Laden) JumpRanges => ShipMath.JumpRanges(MaxJumpRange, UnladenMass, FuelCapacity, FsdFuel, BoosterLy, CargoCapacity);

    /// <summary>
    /// Jumps left on the plotted route as the game itself counts them; 0 with no route. The game
    /// sends a new count when it plots the route again and during every jump (already the count
    /// for after that jump), so this is always its newest number.
    /// </summary>
    public int RouteLeft { get; private set; }

    /// <summary>The plotted route from NavRoute.json, starting with the system it was plotted from. Empty with no route.</summary>
    public IReadOnlyList<RouteStop> Route { get; private set; } = [];

    /// <summary>What is in the hold, biggest load first, limpets left out (Cargo.json).</summary>
    public IReadOnlyList<CargoItem> Cargo { get; private set; } = [];

    /// <summary>Tons on board, limpets included.</summary>
    public int CargoCount { get; private set; }

    /// <summary>
    /// The price list of the station the player is docked at, or null: the game only writes it when
    /// the commodities screen is opened, so the file on disk may still belong to another station.
    /// </summary>
    public StationMarket? Market => Docked && MarketId != 0 && market?.MarketId == MarketId ? market : null;

    /// <summary>
    /// What the player is doing this second (Status.json). The game rewrites that file about once a
    /// second in flight, so <see cref="Changed"/> is raised for it only when what the snapshot shows
    /// of it is different (the vehicle the player is in, the game screen that is open).
    /// </summary>
    public GameStatus Status { get; private set; } = GameStatus.None;

    /// <summary>
    /// The engineering materials the commander holds (raw, manufactured and encoded in one list), by
    /// the game's code without regard to capitals: "chemicalprocessors" gives 17. One that is not
    /// held is not in the list. The game lists them all at every start and writes a line for each
    /// change after that; a change it writes no line for is put right by the next start's list.
    /// </summary>
    public IReadOnlyDictionary<string, int> Materials => materials;

    /// <summary>How the commander stands with each engineer the game has named, by the game's number for the engineer: 300100 is Felicity Farseer.</summary>
    public IReadOnlyDictionary<long, EngineerStanding> Engineers => engineers;

    /// <summary>
    /// Everything the commander has ever unlocked at a technology broker, by the game's code without
    /// regard to capitals ("hpt_guardian_gausscannon_fixed_medium", "int_guardianfsdbooster_size5").
    /// The game never lists these: each is written once, on the day, and that line can sit in a
    /// journal years old. So the list is put together from three places: the lines of the journal
    /// being followed, the older journals (searched in the background, see <see cref="GameWatcher"/>)
    /// and what earlier runs of Studio found in them (<see cref="AddUnlocks"/>).
    /// </summary>
    public IReadOnlySet<string> Unlocked => unlocked;

    /// <summary>
    /// Every journal last written before this time has been searched for unlocks; the first moment
    /// of all while none has. It moves on when the background search finishes, which is the moment
    /// to save it with <see cref="Unlocked"/> so the next start searches only what is newer.
    /// </summary>
    public DateTime UnlocksReadTo { get; private set; }

    /// <summary>The keyboard keys the game itself uses.</summary>
    public KeyBindings Bindings { get; private set; } = KeyBindings.None;

    /// <summary>Windowed, fullscreen or borderless, from the game's display settings.</summary>
    public DisplayMode DisplayMode { get; private set; }

    /// <summary>The time stamped on the newest journal line read.</summary>
    public DateTime? LastEventUtc { get; private set; }

    /// <summary>
    /// Credits per ton paid at the last purchase of a commodity (code such as "bauxite"), from this
    /// journal and the five before it; 0 when not known.
    /// </summary>
    public int PaidFor(string commodity) => paid.GetValueOrDefault(commodity);

    /// <summary>
    /// Exact jumps left to a system, or 0 when the plotted route does not end there (the caller
    /// then estimates with <see cref="ShipMath.JumpsFor"/>). The game's own count comes first: it
    /// plots the route again without rewriting the list of stops.
    /// </summary>
    public int PlottedJumps(string? toSystem)
    {
        if (Route.Count < 2 || string.IsNullOrEmpty(toSystem) || string.IsNullOrEmpty(StarSystem)) return 0;
        if (!Same(Route[^1].StarSystem, toSystem)) return 0;
        if (RouteLeft > 0) return RouteLeft;
        for (int i = Route.Count - 2; i >= 0; i--)
            if (Same(Route[i].StarSystem, StarSystem)) return Route.Count - 1 - i;
        return 0;
    }

    public GameSnapshot Snapshot()
    {
        string? destination = Route.Count > 0 ? Route[^1].StarSystem : null;
        return new GameSnapshot
        {
            GameRunning = GameRunning,
            Commander = Commander,
            ShipType = Ship is null ? null : ShipMath.ShipName(Ship),
            ShipName = Loadout is { ShipName.Length: > 0 } ? Loadout.ShipName : null,
            StarSystem = StarSystem,
            StarPos = StarPos,
            Docked = Docked,
            Station = Station,
            CargoCapacity = CargoCapacity,
            CargoCount = CargoCount,
            JumpLaden = JumpLaden,
            JumpEmpty = JumpEmpty,
            RouteDestination = destination,
            JumpsLeft = PlottedJumps(destination),
            Mode = Status.Mode,
            GuiFocus = Status.GuiFocus,
            LastEventUtc = LastEventUtc,
        };
    }

    /// <summary>Takes one journal line into the state, then hands it to the studios. Events it does not know change nothing.</summary>
    internal void Apply(JsonElement e, bool replay)
    {
        string name = e.Text("event");
        switch (name)
        {
            case "Commander":
                if (e.Text("Name") is { Length: > 0 } commander) Commander = commander;
                break;

            case "LoadGame":
                if (e.Text("Commander") is { Length: > 0 } loaded) Commander = loaded;
                // "Krait_MkII" here, "krait_mkii" in Loadout: one spelling for both
                if (e.Text("Ship") is { Length: > 0 } ship) Ship = ship.ToLowerInvariant();
                RouteLeft = 0;   // a route still plotted is announced again right after
                Supercharged = false;
                break;

            case "Loadout":
                ApplyLoadout(e);
                break;

            case "ShipyardSwap":
                // only the ship changes here; the Loadout the game writes next brings its numbers
                if (e.Text("ShipType") is { Length: > 0 } swapped) Ship = swapped.ToLowerInvariant();
                break;

            case "Location":
            case "CarrierJump":
                Docked = e.Flag("Docked");
                Station = Docked ? e.Text("StationName") : null;
                MarketId = Docked ? e.Whole("MarketID") : 0;
                Arrive(e);
                break;

            case "FSDJump":
                // RouteLeft is left alone: the count for after this jump arrived while it was under way.
                Docked = false;
                Station = null;
                MarketId = 0;
                Supercharged = false;   // this jump spent it
                Arrive(e);
                break;

            case "JetConeBoost":
                // BoostValue is how many times farther the next jump goes: 4 from a neutron star, 1.5 from a white dwarf.
                Supercharged = e.Number("BoostValue") > WhiteDwarfBoost;
                break;

            case "Died":
                Supercharged = false;
                break;

            case "FSDTarget":
                RouteLeft = (int)e.Whole("RemainingJumpsInRoute");   // absent for a target picked without a route
                break;

            case "NavRouteClear":
                RouteLeft = 0;
                Route = [];
                break;

            case "Docked":
                Docked = true;
                Station = e.Text("StationName");
                MarketId = e.Whole("MarketID");
                Arrive(e);
                break;

            case "Undocked":
                Docked = false;
                Station = null;
                MarketId = 0;
                break;

            case "MarketBuy":
                NotePaid(e);
                break;

            case "Materials":
                materials.Clear();
                foreach (string kind in MaterialKinds)
                    foreach (JsonElement held in e.List(kind)) AddMaterial(held.Text("Name"), held.Whole("Count"));
                break;

            case "MaterialCollected":
                AddMaterial(e.Text("Name"), e.Whole("Count"));
                break;

            case "MaterialDiscarded":
            case "ScientificResearch":   // handed in for a research community goal
                AddMaterial(e.Text("Name"), -e.Whole("Count"));
                break;

            case "MaterialTrade":
                if (e.Field("Paid") is { } given) AddMaterial(given.Text("Material"), -given.Whole("Quantity"));
                if (e.Field("Received") is { } got) AddMaterial(got.Text("Material"), got.Whole("Quantity"));
                break;

            case "EngineerCraft":
                Spend(e.List("Ingredients"));
                break;

            case "Synthesis":
                Spend(e.List("Materials"));
                break;

            case "TechnologyBroker":
                Spend(e.List("Materials"));
                unlocked.UnionWith(UnlockedBy(e));
                break;

            case "EngineerContribution":
                // What an engineer asks for before working for the commander; only some ask for materials.
                if (e.Text("Type") == "Materials") AddMaterial(e.Text("Material"), -e.Whole("Quantity"));
                break;

            case "MissionCompleted":
                foreach (JsonElement reward in e.List("MaterialsReward"))
                    if (!OnFootRewards.Contains(reward.Text("Category"))) AddMaterial(reward.Text("Name"), reward.Whole("Count"));
                break;

            case "EngineerProgress":
                // At every start the game lists all the engineers; in between it writes a line for the one whose standing changed.
                if (e.Field("Engineers") is { ValueKind: JsonValueKind.Array } all)
                {
                    engineers.Clear();
                    foreach (JsonElement one in all.EnumerateArray()) NoteEngineer(one);
                }
                else NoteEngineer(e);
                break;
        }

        DateTime time = e.Field("timestamp") is { ValueKind: JsonValueKind.String } stamp && stamp.TryGetDateTime(out DateTime parsed)
            ? parsed.ToUniversalTime()
            : default;
        if (time != default) LastEventUtc = time;

        var line = new JournalLine(name, e, time, replay);
        foreach (Action<JournalLine> studio in Journal?.GetInvocationList() ?? [])
        {
            try { studio(line); }
            catch (Exception problem) { Failed(studio, $"journal line {name}", problem); }
        }
    }

    /// <summary>Remembers the price of a MarketBuy line. Also fed from older journals, which are not replayed otherwise.</summary>
    internal void NotePaid(JsonElement marketBuy)
    {
        if (marketBuy.Text("Type") is { Length: > 0 } commodity) paid[commodity] = (int)marketBuy.Whole("BuyPrice");
    }

    /// <summary>
    /// The codes of what one TechnologyBroker line unlocked. One unlock can open several modules
    /// (every size of a power plant), so the game writes a list; before its version 3.03 it wrote
    /// a single name instead, and such lines are still in old journals.
    /// </summary>
    internal static IEnumerable<string> UnlockedBy(JsonElement technologyBroker) =>
        technologyBroker.List("ItemsUnlocked").Select(item => item.Text("Name"))
            .Append(technologyBroker.Text("ItemUnlocked"))
            .Where(code => code.Length > 0);

    /// <summary>
    /// Adds unlocks found outside the journal being followed. <see cref="GameWatcher"/> brings what
    /// its background search of the older journals found. Studio Loadouts brings what earlier runs
    /// found, which it keeps in settings.json: it does so at start-up, before the first Poll, so
    /// the journals searched then are not searched again (brought later, or never, the search
    /// simply covers every older journal).
    /// </summary>
    /// <param name="readTo">Every journal last written before this time has been searched; left out when that cannot be said.</param>
    public void AddUnlocks(IEnumerable<string> codes, DateTime readTo = default)
    {
        unlocked.UnionWith(codes);
        if (readTo > UnlocksReadTo) UnlocksReadTo = readTo;
    }

    internal void SetStatus(GameStatus status) => Status = status;

    internal void SetCargo(int tons, IReadOnlyList<CargoItem> items) => (CargoCount, Cargo) = (tons, items);

    internal void SetMarket(StationMarket stationMarket) => market = stationMarket;

    internal void SetRoute(IReadOnlyList<RouteStop> route) => Route = route;

    internal void SetBindings(KeyBindings bindings) => Bindings = bindings;

    internal void SetDisplayMode(DisplayMode mode) => DisplayMode = mode;

    internal void RaiseChanged()
    {
        foreach (Action studio in Changed?.GetInvocationList() ?? [])
        {
            try { studio(); }
            catch (Exception problem) { Failed(studio, "a change of state", problem); }
        }
    }

    // Listeners are called one by one, not as one event call, which would stop at the first that
    // throws: a studio that fails must not keep a line or a change from the studios after it, nor
    // stop the journal being read. Each failure is logged with where the listener lives.
    static void Failed(Delegate studio, string on, Exception problem) =>
        Log.Error($"A listener in {studio.Method.DeclaringType?.FullName} failed on {on}", problem);

    /// <summary>Counts a material up or down. One that runs out leaves the list, as in the game's own list at start-up.</summary>
    void AddMaterial(string code, long count)
    {
        if (code.Length == 0) return;
        long now = materials.GetValueOrDefault(code) + count;
        if (now > 0) materials[code] = (int)now;
        else materials.Remove(code);
    }

    /// <summary>Takes away what a recipe used up: the list of names and counts an engineer, a synthesis or a technology broker line carries.</summary>
    void Spend(IEnumerable<JsonElement> used)
    {
        foreach (JsonElement one in used) AddMaterial(one.Text("Name"), -one.Whole("Count"));
    }

    void NoteEngineer(JsonElement e)
    {
        long id = e.Whole("EngineerID");
        // (The game leaves an on-foot engineer's fields out while it runs without Odyssey: no number, nothing to note.)
        if (id != 0) engineers[id] = EngineerStanding.Parse(e, engineers.GetValueOrDefault(id));
    }

    void Arrive(JsonElement e)
    {
        if (e.Position("StarPos") is { } position) StarPos = position;
        // Name and number are taken together, so a name is never left paired with another system's number.
        if (e.Text("StarSystem") is { Length: > 0 } system) (StarSystem, SystemAddress) = (system, e.Whole("SystemAddress"));
    }

    void ApplyLoadout(JsonElement e)
    {
        int? shipBefore = Loadout?.ShipId;
        Loadout = ShipLoadout.Parse(e);
        if (shipBefore is { } id && id != Loadout.ShipId) Supercharged = false;   // another ship, another drive
        if (Loadout.Ship.Length > 0) Ship = Loadout.Ship;
        if (e.Field("CargoCapacity") is not null) CargoCapacity = Loadout.CargoCapacity;   // 0 is a real answer: no racks
        // A Loadout without these (or with 0) keeps the last known numbers rather than wiping them.
        if (Loadout.MaxJumpRange != 0) MaxJumpRange = Loadout.MaxJumpRange;
        if (Loadout.UnladenMass != 0) UnladenMass = Loadout.UnladenMass;
        if (e.Field("FuelCapacity")?.Field("Main") is not null) FuelCapacity = Loadout.FuelMain;
        (FsdFuel, BoosterLy) = ShipMath.DriveNumbers(Loadout.Modules);
    }

    // Names are compared the way the Haul Overlay did: without regard to capitals.
    static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
