using System.Globalization;
using System.Text.Json;

namespace EliteStudio.Core;

/// <summary>
/// The game's fixed things as the community has tabled them, carried inside the program (the files
/// in Core\Data): the names of commodities, materials, modules, ships and engineers from EDCD's
/// FDevIDs, and every ship, module and engineering recipe with its numbers from EDCD's
/// coriolis-data. Each table is read once, when first asked for. Look-ups by the game's code (its
/// "symbol") ignore capitals, because the game writes one code in several ways ("Krait_MkII" in one
/// journal line, "krait_mkii" in the next). Of each name table only the columns Studio uses are
/// read. CLAUDE.md says how the files are refreshed.
/// </summary>
public static class GameData
{
    public sealed record Commodity(string Symbol, string Name);

    /// <param name="Grade">How rare it is, 1 (very common) to 5 (very rare).</param>
    /// <param name="Kind">"Raw", "Manufactured" or "Encoded".</param>
    /// <param name="Category">The group it is traded within at a material trader; a number for raw materials.</param>
    public sealed record Material(string Symbol, int Grade, string Kind, string Category, string Name);

    /// <param name="Mount">"Fixed", "Gimballed" or "Turreted" for a weapon, otherwise "".</param>
    /// <param name="Rating">"A" to "I".</param>
    public sealed record Module(string Symbol, string Name, string Mount, int Class, string Rating);

    public sealed record Ship(long Id, string Symbol, string Name);

    public sealed record Engineer(long Id, string Name);

    static readonly Lazy<Dictionary<string, Commodity>> commodities = new(() => BySymbol(
        Rows("commodity.csv").Select(r => new Commodity(r[1], r[3])).Concat(Rows("rare_commodity.csv").Select(r => new Commodity(r[1], r[4]))),
        c => c.Symbol));

    static readonly Lazy<Dictionary<string, Material>> materials = new(() => BySymbol(
        Rows("material.csv").Select(r => new Material(r[1], (int)Whole(r[2]), r[3], r[4], r[5])), m => m.Symbol));

    static readonly Lazy<Dictionary<string, Module>> modules = new(() => BySymbol(
        Rows("outfitting.csv").Select(r => new Module(r[1], r[3], r[4], (int)Whole(r[7]), r[8])), m => m.Symbol));

    // A module's code -> the name of its last row, for the codes the outfitting table lists more than once.
    static readonly Lazy<Dictionary<string, string>> editions = new(() =>
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var later = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string[] r in Rows("outfitting.csv"))
            if (!seen.Add(r[1])) later[r[1]] = r[3];
        return later;
    });

    static readonly Lazy<Dictionary<string, Ship>> ships = new(() => BySymbol(
        Rows("shipyard.csv").Select(r => new Ship(Whole(r[0]), r[1], r[2])), s => s.Symbol));

    static readonly Lazy<Engineer[]> engineers = new(() =>
        [.. Rows("engineers.csv").Select(r => new Engineer(Whole(r[0]), r[3]))]);

    static readonly Lazy<JsonElement> coriolis = new(() =>
    {
        using Stream file = Open("coriolis.json");
        return JsonDocument.Parse(file).RootElement;
    });

    /// <summary>Every commodity, rare ones included, by its code ("LowTemperatureDiamond").</summary>
    public static IReadOnlyDictionary<string, Commodity> Commodities => commodities.Value;

    /// <summary>Every engineering material by its code ("MechanicalScrap").</summary>
    public static IReadOnlyDictionary<string, Material> Materials => materials.Value;

    /// <summary>Every module a station can sell, by its code ("Int_Hyperdrive_Size5_Class5").</summary>
    public static IReadOnlyDictionary<string, Module> Modules => modules.Value;

    /// <summary>
    /// The special edition's name the outfitting table also lists a module's code under ("Extended
    /// Cargo Rack" for the plain size 5 rack), or null for a code listed once. Spansh files such a
    /// module under this name, so a question to Spansh has to use it.
    /// </summary>
    public static string? SpecialEdition(string code) => editions.Value.GetValueOrDefault(code);

    /// <summary>Every ship by its code ("Type9_Military").</summary>
    public static IReadOnlyDictionary<string, Ship> Ships => ships.Value;

    public static IReadOnlyList<Engineer> Engineers => engineers.Value;

    /// <summary>
    /// coriolis-data, whole: "ships", "standard", "hardpoints" and "internal" are lists holding that
    /// folder's files one after the other, each just as it is there; "blueprints", "modules",
    /// "modifications", "specials" and "modifierActions" are the files of its modifications folder.
    /// </summary>
    public static JsonElement Coriolis => coriolis.Value;

    /// <summary>
    /// The one place a commodity gets the name Studio shows: the game's own ("Agri-Medicines" for
    /// "agriculturalmedicines"). A commodity newer than the table gets its code with a capital.
    /// </summary>
    public static string CommodityName(string symbol) =>
        Commodities.TryGetValue(symbol, out Commodity? known) ? known.Name : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(symbol);

    static Stream Open(string file) =>
        typeof(GameData).Assembly.GetManifestResourceStream("Data." + file)
        ?? throw new InvalidOperationException($"The data file {file} is missing from the program");

    /// <summary>
    /// The rows of one FDevIDs file without its heading. None of the files quotes a field, so a comma
    /// always ends one; a space at either end of a field is not part of it (one material's name has one).
    /// </summary>
    static IEnumerable<string[]> Rows(string file)
    {
        using var reader = new StreamReader(Open(file));
        reader.ReadLine();
        while (reader.ReadLine() is { } line)
            if (line.Length > 0) yield return line.Split(',', StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// A code's first row is the one kept. Only the module table has a code twice: 22 everyday modules
    /// are listed again further down as pre-engineered special editions (a "Double Screaming Fragment
    /// Cannon" under the plain fragment cannon's code).
    /// </summary>
    static Dictionary<string, T> BySymbol<T>(IEnumerable<T> rows, Func<T, string> symbol)
    {
        var table = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (T row in rows) table.TryAdd(symbol(row), row);
        return table;
    }

    static long Whole(string digits) => long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole) ? whole : 0;
}
