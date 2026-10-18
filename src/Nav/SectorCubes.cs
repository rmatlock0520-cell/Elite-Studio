using System.Globalization;
using EliteStudio.App;

namespace EliteStudio.Nav;

/// <summary>A sector of the game's grid on EDAstro's list.</summary>
/// <param name="Name">The sector's name as EDAstro spells it.</param>
/// <param name="X">The sector's numbers along x, y (its layer) and z, as <see cref="Galaxy.SectorOf"/> gives them (with <paramref name="Y"/> and <paramref name="Z"/>).</param>
/// <param name="Known">Systems named after the sector that EDAstro knows of. Catalogue stars ("HIP 1234") and the systems inside a nebula's own sector are not among them.</param>
/// <param name="First">The day EDAstro first saw a system of the sector; null when the list does not say.</param>
/// <param name="Repaired">EDAstro's own sector numbers for this row were wrong, so Studio placed it by its name (or, when its name cannot be read, by where its systems lie on average).</param>
public sealed record SectorRow(string Name, int X, int Y, int Z, int Known, DateOnly? First, bool Repaired);

/// <summary>A nebula's own sector on EDAstro's list. It has no box in the grid, only the middle of its known systems.</summary>
/// <param name="Name">The name its systems carry ("Col 285 Sector").</param>
/// <param name="Known">Systems named after it that EDAstro knows of.</param>
/// <param name="First">The day EDAstro first saw one of them; null when the list does not say.</param>
/// <param name="X">The middle of its known systems, in light years (with <paramref name="Y"/> and <paramref name="Z"/>).</param>
public sealed record NebulaRow(string Name, int Known, DateOnly? First, double X, double Y, double Z);

/// <summary>A layer of a <see cref="SectorBlock"/>.</summary>
/// <param name="Layer">Its number, as <see cref="Galaxy.LayerOf"/> gives it.</param>
/// <param name="Name">Its fixed name for the player (<see cref="Galaxy.LayerName"/>).</param>
/// <param name="YFrom">Its floor and ceiling, in light years (with <paramref name="YTo"/>).</param>
/// <param name="Known">Systems EDAstro knows in the block's sectors of this layer; null when there is no list on this PC.</param>
public sealed record BlockLayer(int Layer, string Name, double YFrom, double YTo, long? Known);

/// <summary>A sector of a <see cref="SectorBlock"/>.</summary>
/// <param name="X">Its numbers along x, y (its layer) and z (with <paramref name="Y"/> and <paramref name="Z"/>).</param>
/// <param name="Name">EDAstro's name for it, or the name the game's rule gives it when EDAstro has no row for it; null only far past the galaxy's rim.</param>
/// <param name="Known">Systems EDAstro knows in it: 0 when EDAstro has no row for it, null when there is no list on this PC.</param>
/// <param name="First">The day EDAstro first saw a system of it, when the list says.</param>
public sealed record BlockSector(int X, int Y, int Z, string? Name, int? Known, DateOnly? First);

/// <summary>
/// What the 3D map shows before anything is asked of anybody: the column of sectors at
/// <paramref name="X"/>, <paramref name="Z"/> with the eight around it, in every named layer that
/// holds any of EDAstro's rows there.
/// </summary>
/// <param name="X">The middle column's numbers along x and z (with <paramref name="Z"/>).</param>
/// <param name="Bounds">The box round all the block's sectors.</param>
/// <param name="Layers">Its layers, lowest first.</param>
/// <param name="Sectors">Its sectors, layer by layer from the lowest, each layer row by row from the south, each row from the west.</param>
public sealed record SectorBlock(int X, int Z, Box Bounds, IReadOnlyList<BlockLayer> Layers, IReadOnlyList<BlockSector> Sectors);

/// <summary>
/// EDAstro's sector list read sector by sector, for the 3D map: each sector of the grid by its
/// numbers along x, y and z, and each nebula's own sector by its name. The flat map
/// (<see cref="SectorMap"/>) reads the same list but adds every layer up, and places nebulae by
/// EDAstro's map column; this keeps them apart.
///
/// The list's Min and Max columns are not read: they hold far outliers (Synuefai's smallest x is
/// 3,900 light years outside its box). A sector's box comes from the grid instead
/// (<see cref="Galaxy.SectorBounds"/>). EDAstro's sector numbers (its "id64" columns) are checked
/// against where the row's systems lie on average; two rows of 14 October 2026 (Aucopp and Juenae)
/// have numbers that are plainly wrong, and those are repaired.
/// </summary>
public sealed class SectorList
{
    /// <summary>The columns read, by the names in the list's first line. Others may come and go.</summary>
    const string NameColumn = "Sector", SystemsColumn = "Systems", FirstColumn = "First Encountered";

    static readonly string[] Middle = ["Avg X", "Avg Y", "Avg Z"], Numbers = ["id64 X", "id64 Y", "id64 Z"];

    readonly Dictionary<(int X, int Y, int Z), SectorRow> byPlace = [];
    readonly Dictionary<string, SectorRow> byName = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, NebulaRow> nebulaByName = new(StringComparer.OrdinalIgnoreCase);

    SectorList(DateTime fetchedUtc) => FetchedUtc = fetchedUtc;

    /// <summary>When the list was fetched from EDAstro.</summary>
    public DateTime FetchedUtc { get; }

    /// <summary>The sectors of the grid, in the list's order.</summary>
    public IReadOnlyList<SectorRow> Sectors { get; private set; } = [];

    /// <summary>The nebulae's own sectors, in the list's order.</summary>
    public IReadOnlyList<NebulaRow> Nebulae { get; private set; } = [];

    /// <summary>Rows that were left out: unreadable, without a place, or a second row for a sector that has one already.</summary>
    public int Dropped { get; private set; }

    /// <summary>The sector of the grid with these numbers, if EDAstro lists it.</summary>
    public SectorRow? At(int x, int y, int z) => byPlace.GetValueOrDefault((x, y, z));

    /// <summary>The sector of the grid with this name, if EDAstro lists it; upper and lower case do not matter.</summary>
    public SectorRow? Named(string name) => byName.GetValueOrDefault(name.Trim());

    /// <summary>The nebula's own sector with this name ("Col 285 Sector"), if EDAstro lists it; upper and lower case do not matter.</summary>
    public NebulaRow? Nebula(string name) => nebulaByName.GetValueOrDefault(name.Trim());

    /// <summary>The nebulae's own sectors whose known systems lie, on average, in a box.</summary>
    public IEnumerable<NebulaRow> NebulaeIn(Box box) => Nebulae.Where(nebula => box.Contains(nebula.X, nebula.Y, nebula.Z));

    /// <summary>The block of sectors round a column: see <see cref="SectorCubes.Block(SectorList, int, int)"/>.</summary>
    public SectorBlock Block(int x, int z) => SectorCubes.Block(this, x, z);

    /// <summary>
    /// Reads EDAstro's sector list by the names of its columns, so that a column added or moved
    /// does not matter. A row with no sector numbers at all is a nebula's own sector (that is how
    /// EDAstro writes them). Any other row is a sector of the grid. Its numbers are kept when they
    /// hold its systems' average place. When they do not, the row is placed by its name, which the
    /// game's naming rule ties to one sector (<see cref="ProcGenNames.SectorIndex"/>). When the name
    /// cannot be read either, it is placed by the average. A row that cannot be read, or has no
    /// place, is left out.
    /// </summary>
    /// <exception cref="InvalidDataException">Not that list, or not as Studio knows it. The message is written for the player.</exception>
    public static SectorList Parse(string csv, DateTime fetchedUtc)
    {
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] titles = lines.Length > 0 ? Csv.Cells(lines[0]) : [];
        int name = Array.IndexOf(titles, NameColumn), systems = Array.IndexOf(titles, SystemsColumn), first = Array.IndexOf(titles, FirstColumn);
        int[] middle = [.. Middle.Select(column => Array.IndexOf(titles, column))], numbers = [.. Numbers.Select(column => Array.IndexOf(titles, column))];
        if (name < 0 || systems < 0 || middle.Contains(-1) || numbers.Contains(-1)) throw NotTheList();

        var list = new SectorList(fetchedUtc);
        var sectors = new List<SectorRow>();
        var nebulae = new List<NebulaRow>();
        foreach (string line in lines.Skip(1))
        {
            string[] cells = Csv.Cells(line);
            string sector = cells.Length == titles.Length ? cells[name].Trim() : "";
            if (sector.Length == 0 || !int.TryParse(cells[systems], NumberStyles.None, CultureInfo.InvariantCulture, out int known))
            {
                list.Dropped++;
                continue;
            }
            DateOnly? day = first >= 0 ? Day(cells[first]) : null;
            (double X, double Y, double Z)? average = Number(cells[middle[0]]) is { } ax && Number(cells[middle[1]]) is { } ay && Number(cells[middle[2]]) is { } az ? (ax, ay, az) : null;

            if (numbers.All(column => cells[column].Trim().Length == 0))
            {
                if (average is { } at) nebulae.Add(new NebulaRow(sector, known, day, at.X, at.Y, at.Z));
                else list.Dropped++;   // a nebula's sector with no place at all
                continue;
            }

            (int X, int Y, int Z)? given = Whole(cells[numbers[0]]) is { } gx && Whole(cells[numbers[1]]) is { } gy && Whole(cells[numbers[2]]) is { } gz ? InGrid((gx, gy, gz)) : null;
            (int X, int Y, int Z)? averaged = average is { } point ? InGrid(Galaxy.SectorOf(point.X, point.Y, point.Z)) : null;
            (int X, int Y, int Z)? place;
            if (given is not null && (averaged is null || averaged == given)) place = given;   // nearly every row
            // EDAstro's numbers and its average disagree. Its name settles it where the rule can read it (only
            // these few rows are read: reading every name would take a noticeable moment); else the average.
            else place = (ProcGenNames.SectorIndex(sector) is { } named ? InGrid(named) : null) ?? averaged;

            if (place is not { } found)
            {
                list.Dropped++;
                continue;
            }
            sectors.Add(new SectorRow(sector, found.X, found.Y, found.Z, known, day, Repaired: found != given));
        }
        if (sectors.Count == 0 && nebulae.Count == 0) throw NotTheList();

        foreach (SectorRow row in sectors)
        {
            // Two rows for one sector: one of them was placed wrongly. A row whose own numbers held wins over a
            // repaired one; else the first stays.
            if (list.byPlace.TryGetValue((row.X, row.Y, row.Z), out SectorRow? had))
            {
                list.Dropped++;
                if (!had.Repaired || row.Repaired) continue;
            }
            list.byPlace[(row.X, row.Y, row.Z)] = row;
        }
        list.Sectors = [.. sectors.Where(row => ReferenceEquals(list.byPlace[(row.X, row.Y, row.Z)], row))];
        foreach (SectorRow row in list.Sectors) list.byName.TryAdd(row.Name, row);
        list.Nebulae = nebulae;
        foreach (NebulaRow row in nebulae) list.nebulaByName.TryAdd(row.Name, row);
        return list;

        static double? Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
        static int? Whole(string text) => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : null;
        // The sector itself when it lies in the game's grid, else none.
        static (int X, int Y, int Z)? InGrid((int X, int Y, int Z) at) =>
            at.X is >= 0 and < Galaxy.SectorsAcross && at.Y is >= 0 and < Galaxy.Layers && at.Z is >= 0 and < Galaxy.SectorsAcross ? at : null;
        static DateOnly? Day(string text) =>
            DateTime.TryParseExact(text.Trim(), ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime when)
                ? DateOnly.FromDateTime(when) : null;
        static InvalidDataException NotTheList() => new("EDAstro's sector list is not laid out the way Studio knows it, so no map could be made from it.");
    }
}

/// <summary>
/// EDAstro's sector list as the 3D map needs it (<see cref="SectorList"/>), read from the copy that
/// <see cref="ExploreCoverage"/> keeps for the flat map. Nothing is fetched here: EDAstro is asked
/// only when the player presses the flat map's button (or the 3D map's, which calls the same). When
/// a new list comes in, this takes the very text the flat map was made from, so the two maps always
/// show the same list, even when the new copy could not be written to disk.
/// </summary>
public sealed class SectorCubes
{
    readonly ExploreCoverage coverage;
    readonly Lock gate = new();
    SectorList? list;
    bool looked;                                     // the list has been read since the last new one came
    (string Csv, DateTime FetchedUtc)? arrived;      // a new list from EDAstro, read the next time it is needed

    public SectorCubes(ExploreCoverage coverage)
    {
        this.coverage = coverage;
        // Read when it is next needed rather than here: this is the thread the flat map's fetch ends on (the
        // UI thread), and the 3D map reads its list away from it.
        coverage.Refreshed += (csv, fetchedUtc) =>
        {
            lock (gate) (list, looked, arrived) = (null, false, (csv, fetchedUtc));
        };
    }

    /// <summary>The list: the newest from EDAstro, else the copy on this PC; null when there is none yet, or it cannot be read.</summary>
    public SectorList? List
    {
        get
        {
            lock (gate)
            {
                if (looked) return list;
                looked = true;
                string file = coverage.FilePath;
                try
                {
                    if (arrived is { } fresh) list = SectorList.Parse(fresh.Csv, fresh.FetchedUtc);
                    else if (File.Exists(file)) list = SectorList.Parse(File.ReadAllText(file), File.GetLastWriteTimeUtc(file));
                }
                catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Log.Error("EDAstro's sector list could not be read for the 3D map", problem);
                }
                arrived = null;   // read (or found unreadable): the text is not held any longer
                return list;
            }
        }
    }

    /// <summary>The block of sectors round a column, from the list on this PC: see <see cref="Block(SectorList, int, int)"/>.</summary>
    public SectorBlock Block(int x, int z) => Block(List, x, z);

    /// <summary>
    /// The column of sectors at <paramref name="x"/>, <paramref name="z"/> and the eight around it
    /// (fewer at the grid's edge), in every named layer (29 to 34) where the list has a row for any
    /// of them. Where it has none, or there is no list, the two layers on either side of the
    /// galaxy's middle (31 and 32), so that there is always something to show. Without a list the
    /// sectors still have their names, but no counts.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The column is not in the grid (0 to 127 along x and z).</exception>
    public static SectorBlock Block(SectorList? list, int x, int z)
    {
        if (x is < 0 or >= Galaxy.SectorsAcross) throw new ArgumentOutOfRangeException(nameof(x), x, "Sectors are numbered 0 to 127.");
        if (z is < 0 or >= Galaxy.SectorsAcross) throw new ArgumentOutOfRangeException(nameof(z), z, "Sectors are numbered 0 to 127.");
        int[] xs = [.. Enumerable.Range(x - 1, 3).Where(column => column is >= 0 and < Galaxy.SectorsAcross)];
        int[] zs = [.. Enumerable.Range(z - 1, 3).Where(row => row is >= 0 and < Galaxy.SectorsAcross)];

        int[] layers = [.. Enumerable.Range(Galaxy.FirstNamedLayer, Galaxy.LastNamedLayer - Galaxy.FirstNamedLayer + 1)
            .Where(layer => list is not null && zs.Any(row => xs.Any(column => list.At(column, layer, row) is not null)))];
        if (layers.Length == 0) layers = [31, 32];

        var sectors = new List<BlockSector>();
        var shown = new List<BlockLayer>();
        foreach (int layer in layers)
        {
            long known = 0;
            foreach (int row in zs)
                foreach (int column in xs)
                {
                    SectorRow? listed = list?.At(column, layer, row);
                    sectors.Add(new BlockSector(column, layer, row, listed?.Name ?? ProcGenNames.SectorName(column, layer, row),
                        list is null ? null : listed?.Known ?? 0, listed?.First));
                    known += listed?.Known ?? 0;
                }
            Box bounds = Galaxy.SectorBounds(x, layer, z);
            shown.Add(new BlockLayer(layer, Galaxy.LayerName(layer), bounds.Y0, bounds.Y1, list is null ? null : known));
        }
        Box low = Galaxy.SectorBounds(xs[0], layers[0], zs[0]), high = Galaxy.SectorBounds(xs[^1], layers[^1], zs[^1]);
        return new SectorBlock(x, z, new Box(low.X0, low.Y0, low.Z0, high.X1, high.Y1, high.Z1), shown, sectors);
    }
}

/// <summary>Lines of the community's CSV files, as EDAstro writes them.</summary>
internal static class Csv
{
    /// <summary>
    /// The cells of one line, split at commas. A cell in double quotes may hold commas, and a
    /// doubled quote inside it stands for one; the quotes themselves are not kept.
    /// </summary>
    public static string[] Cells(string line)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        bool quoted = false;
        for (int at = 0; at < line.Length; at++)
        {
            char c = line[at];
            if (quoted)
            {
                if (c != '"') cell.Append(c);
                else if (at + 1 < line.Length && line[at + 1] == '"') cell.Append(line[++at]);
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ',')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else cell.Append(c);
        }
        cells.Add(cell.ToString());
        return [.. cells];
    }
}
