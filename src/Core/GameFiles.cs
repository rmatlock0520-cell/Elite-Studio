using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EliteStudio.Core;

/// <summary>
/// Field reading for the game's JSON. A field that is missing or of another type gives the plain
/// default ("" / 0 / false) instead of an error: the game leaves fields out freely, and one odd
/// line must never stop the journal being read.
/// </summary>
static class JsonFields
{
    public static JsonElement? Field(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement value) ? value : null;

    public static string Text(this JsonElement e, string name) =>
        e.Field(name) is { ValueKind: JsonValueKind.String } value ? value.GetString()! : "";

    public static double Number(this JsonElement e, string name) =>
        e.Field(name) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : 0.0;

    /// <summary>A whole number. 64 bits wide because market ids and system addresses do not fit in 32.</summary>
    public static long Whole(this JsonElement e, string name) =>
        e.Field(name) is { ValueKind: JsonValueKind.Number } value
            ? value.TryGetInt64(out long whole) ? whole : (long)Math.Round(value.GetDouble())
            : 0;

    public static bool Flag(this JsonElement e, string name) => e.Field(name) is { ValueKind: JsonValueKind.True };

    public static IEnumerable<JsonElement> List(this JsonElement e, string name) =>
        e.Field(name) is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();

    /// <summary>A position such as StarPos, [x, y, z] in light years; null when the field is not there.</summary>
    public static double[]? Position(this JsonElement e, string name)
    {
        double[] numbers = [.. e.List(name).Where(n => n.ValueKind == JsonValueKind.Number).Select(n => n.GetDouble())];
        return numbers.Length == 3 ? numbers : null;
    }
}

/// <summary>The ship as the journal's newest Loadout event describes it. Kept whole for Studio Loadouts.</summary>
/// <param name="Ship">Journal ship code in lower case, e.g. "type8".</param>
/// <param name="ShipName">The player's own name for the ship; "" when it has none.</param>
/// <param name="FuelMain">Main fuel tank, tons.</param>
public sealed record ShipLoadout(
    string Ship, int ShipId, string ShipName, string ShipIdent, long HullValue, long ModulesValue, double HullHealth,
    double UnladenMass, int CargoCapacity, double MaxJumpRange, double FuelMain, double FuelReserve, long Rebuy,
    IReadOnlyList<ShipModule> Modules)
{
    internal static ShipLoadout Parse(JsonElement e)
    {
        JsonElement fuel = e.Field("FuelCapacity").GetValueOrDefault();
        return new(
            e.Text("Ship").ToLowerInvariant(), (int)e.Whole("ShipID"), e.Text("ShipName").Trim(), e.Text("ShipIdent").Trim(),
            e.Whole("HullValue"), e.Whole("ModulesValue"), e.Number("HullHealth"),
            e.Number("UnladenMass"), (int)e.Whole("CargoCapacity"), e.Number("MaxJumpRange"),
            fuel.Number("Main"), fuel.Number("Reserve"), e.Whole("Rebuy"),
            [.. e.List("Modules").Select(ShipModule.Parse)]);
    }
}

/// <summary>One fitted module.</summary>
/// <param name="Slot">Where it is fitted, e.g. "FrameShiftDrive", "Slot02_Size5".</param>
/// <param name="Item">The game's module code, e.g. "int_hyperdrive_overcharge_size5_class5".</param>
/// <param name="Class">The module's size number (the 5 of "5A"); 0 when the code does not carry one.</param>
/// <param name="Rating">"E" to "A" (the A of "5A"); "" when the code does not carry one.</param>
/// <param name="Value">Credits; 0 when the journal leaves it out.</param>
public sealed partial record ShipModule(
    string Slot, string Item, int Class, string Rating, bool On, int Priority, double Health, long Value,
    ModuleEngineering? Engineering)
{
    internal static ShipModule Parse(JsonElement e)
    {
        string item = e.Text("Item");
        // The code calls the size "size" and the rating "class": 1 = E up to 5 = A. Some modules
        // come in sizes but not in ratings, e.g. "int_guardianfsdbooster_size5".
        Match code = SizeAndRating().Match(item);
        int rating = code.Groups[2].Success ? code.Groups[2].Value[0] - '0' : 0;
        return new(
            e.Text("Slot"), item,
            code.Success ? code.Groups[1].Value[0] - '0' : 0,
            rating is >= 1 and <= 5 ? "EDCBA"[rating - 1].ToString() : "",
            e.Flag("On"), (int)e.Whole("Priority"), e.Number("Health"), e.Whole("Value"),
            e.Field("Engineering") is { ValueKind: JsonValueKind.Object } work ? ModuleEngineering.Parse(work) : null);
    }

    [GeneratedRegex("_size([0-9])(?:_class([0-9]))?", RegexOptions.IgnoreCase)]
    private static partial Regex SizeAndRating();
}

/// <summary>What an engineer did to a module.</summary>
/// <param name="Blueprint">The game's blueprint code, e.g. "FSD_LongRange".</param>
/// <param name="Experimental">The experimental effect's code; "" when none.</param>
/// <param name="ExperimentalName">The experimental effect as the game shows it; "" when none.</param>
public sealed record ModuleEngineering(
    string Engineer, string Blueprint, int Level, double Quality, string Experimental, string ExperimentalName,
    IReadOnlyList<ModuleModifier> Modifiers)
{
    internal static ModuleEngineering Parse(JsonElement e) => new(
        e.Text("Engineer"), e.Text("BlueprintName"), (int)e.Whole("Level"), e.Number("Quality"),
        e.Text("ExperimentalEffect"), e.Text("ExperimentalEffect_Localised"),
        [.. e.List("Modifiers").Select(m => new ModuleModifier(
            m.Text("Label"), m.Number("Value"), m.Number("OriginalValue"), m.Whole("LessIsGood") != 0, m.Text("ValueStr")))]);
}

/// <summary>One number an engineer changed, e.g. FSDOptimalMass from 1175 to 1586.25.</summary>
/// <param name="Text">Set instead of the numbers for the few changes that are words, e.g. a damage type.</param>
public sealed record ModuleModifier(string Label, double Value, double OriginalValue, bool LessIsGood, string Text);

/// <summary>How the commander stands with one engineer (the journal's EngineerProgress).</summary>
/// <param name="Progress">"Known" (heard of), "Invited" (may visit) or "Unlocked" (works for the commander).</param>
/// <param name="Rank">The highest grade the engineer offers the commander, 1 to 5; 0 until unlocked.</param>
public sealed record EngineerStanding(string Progress, int Rank)
{
    /// <summary>
    /// One engineer of an EngineerProgress line. The line for a rank gained no longer says
    /// "Unlocked", and one for an invitation carries no rank: what a line leaves out stays as
    /// it was <paramref name="before"/>.
    /// </summary>
    internal static EngineerStanding Parse(JsonElement e, EngineerStanding? before) => new(
        e.Text("Progress") is { Length: > 0 } progress ? progress : before?.Progress ?? "",
        e.Field("Rank") is null ? before?.Rank ?? 0 : (int)e.Whole("Rank"));
}

/// <summary>
/// Status.json: what the player is doing this second. The game rewrites it whenever something
/// changes while it runs, and leaves only "Flags":0 behind when it closes.
/// </summary>
/// <param name="GuiFocus">Which screen is open: 0 none, 1 right panel, 2 left panel, 3 comms, 4 role panel,
/// 5 station services, 6 galaxy map, 7 system map, 8 orrery, 9 full spectrum scanner, 10 surface scanner, 11 codex.</param>
/// <param name="Cargo">Tons on board the vehicle the player is in.</param>
public sealed record GameStatus(
    uint Flags, uint Flags2, int GuiFocus, double FuelMain, double FuelReservoir, double Cargo, StatusDestination? Destination)
{
    public static readonly GameStatus None = new(0, 0, 0, 0, 0, 0, null);

    public bool Docked => (Flags & 1u << 0) != 0;
    public bool Landed => (Flags & 1u << 1) != 0;
    public bool Supercruise => (Flags & 1u << 4) != 0;
    public bool HardpointsDeployed => (Flags & 1u << 6) != 0;
    public bool FsdCharging => (Flags & 1u << 17) != 0;
    public bool InDanger => (Flags & 1u << 22) != 0;
    public bool BeingInterdicted => (Flags & 1u << 23) != 0;
    public bool InMainShip => (Flags & 1u << 24) != 0;
    public bool InFighter => (Flags & 1u << 25) != 0;
    public bool InSrv => (Flags & 1u << 26) != 0;
    public bool FsdJumping => (Flags & 1u << 30) != 0;
    public bool OnFoot => (Flags2 & 1u << 0) != 0;

    /// <summary>ship, fighter, srv, foot, or unknown while the game is closed or at its menu.</summary>
    public string Mode => InMainShip ? "ship" : InFighter ? "fighter" : InSrv ? "srv" : OnFoot ? "foot" : "unknown";

    internal static GameStatus Parse(JsonElement e)
    {
        JsonElement fuel = e.Field("Fuel").GetValueOrDefault();
        return new(
            (uint)e.Whole("Flags"), (uint)e.Whole("Flags2"), (int)e.Whole("GuiFocus"),
            fuel.Number("FuelMain"), fuel.Number("FuelReservoir"), e.Number("Cargo"),
            e.Field("Destination") is { ValueKind: JsonValueKind.Object } target
                ? new StatusDestination(
                    target.Whole("System"), (int)target.Whole("Body"),
                    target.Text("Name_Localised") is { Length: > 0 } shown ? shown : target.Text("Name"))
                : null);
    }
}

/// <summary>What the player has selected to fly to: a system address, a body in it, and its name.</summary>
public sealed record StatusDestination(long System, int Body, string Name);

/// <summary>One kind of cargo in the hold.</summary>
/// <param name="Id">The game's commodity code in lower case, e.g. "bauxite".</param>
public sealed record CargoItem(string Id, string Name, int Count)
{
    /// <summary>
    /// Cargo.json: tons on board, and the hold's contents biggest first with limpets left out.
    /// While the player drives an SRV the file describes the SRV, which counts as an empty hold.
    /// </summary>
    internal static (int Tons, IReadOnlyList<CargoItem> Items) ParseHold(JsonElement e)
    {
        string vessel = e.Text("Vessel");
        if (vessel.Length > 0 && !vessel.Equals("Ship", StringComparison.OrdinalIgnoreCase)) return (0, []);
        CargoItem[] items =
        [
            .. e.List("Inventory")
                .Select(i => (Id: i.Text("Name").ToLowerInvariant(), Shown: i.Text("Name_Localised"), Count: (int)i.Whole("Count")))
                .Where(i => i.Count > 0 && i.Id != "drones")
                .OrderByDescending(i => i.Count)
                // the game leaves the display name out when it is just the code with a capital letter
                .Select(i => new CargoItem(i.Id, i.Shown.Length > 0 ? i.Shown : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(i.Id), i.Count)),
        ];
        return ((int)e.Whole("Count"), items);
    }
}

/// <summary>
/// Market.json: the price list of the station whose commodities screen the player opened last.
/// <see cref="GameState.Market"/> hands it out only while the player is docked at that station.
/// </summary>
/// <param name="Items">By commodity code in lower case, e.g. "platinum".</param>
/// <param name="Type">The kind of station as the journal spells it ("Coriolis"); "" when the file does not say.</param>
/// <param name="Seen">When the game wrote the list (UTC); null when the file does not say.</param>
public sealed record StationMarket(long MarketId, string StationName, string StarSystem, IReadOnlyDictionary<string, MarketItem> Items, string Type = "", DateTime? Seen = null)
{
    internal static StationMarket Parse(JsonElement e)
    {
        var items = new Dictionary<string, MarketItem>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement i in e.List("Items"))
        {
            // "$platinum_name;" -> "platinum"
            string id = i.Text("Name");
            if (id.StartsWith('$')) id = id[1..];
            if (id.EndsWith("_name;", StringComparison.OrdinalIgnoreCase)) id = id[..^6];
            id = id.ToLowerInvariant();
            if (id.Length > 0)
                items[id] = new MarketItem(id, i.Text("Name_Localised"), (int)i.Whole("BuyPrice"), (int)i.Whole("SellPrice"), (int)i.Whole("Stock"), (int)i.Whole("Demand"));
        }
        DateTime? seen = DateTime.TryParse(e.Text("timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime written)
            ? written : null;
        return new(e.Whole("MarketID"), e.Text("StationName"), e.Text("StarSystem"), items, e.Text("StationType"), seen);
    }
}

/// <summary>One row of a station's price list.</summary>
/// <param name="BuyPrice">Credits per ton the player pays here; 0 when the station does not sell it.</param>
/// <param name="SellPrice">Credits per ton the station pays the player.</param>
public sealed record MarketItem(string Id, string Name, int BuyPrice, int SellPrice, int Stock, int Demand);

/// <summary>One star system on the plotted route (NavRoute.json). The first stop is where the route starts.</summary>
public sealed record RouteStop(string StarSystem, long SystemAddress, double[]? StarPos, string StarClass)
{
    internal static IReadOnlyList<RouteStop> ParseRoute(JsonElement e) =>
        [.. e.List("Route").Select(s => new RouteStop(s.Text("StarSystem"), s.Whole("SystemAddress"), s.Position("StarPos"), s.Text("StarClass")))];
}
