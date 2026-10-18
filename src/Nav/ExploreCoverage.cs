using System.Globalization;
using System.Net;
using System.Net.Http;
using EliteStudio.App;

namespace EliteStudio.Nav;

/// <summary>A thinly known square of the <see cref="SectorMap"/>.</summary>
/// <param name="Known">Systems known in it.</param>
/// <param name="X">The middle of those known systems, in light years (with <paramref name="Y"/> and <paramref name="Z"/>): a point where there surely are stars.</param>
/// <param name="Here">The commander is in this square already.</param>
public sealed record ThinSquare(int Known, double X, double Y, double Z, bool Here);

/// <summary>
/// The galaxy seen from above, cut into squares of 1,280 light years (the game's own sectors, every
/// layer of them added up), with the number of star systems known in each. "Known" is EDAstro's
/// count: systems visited by a commander whose tools share their flight data. It says how well
/// travelled a square is, not how much of it is explored: the galaxy has some 400 billion systems
/// and about 100 million are known, so every square is mostly unexplored. A low number means few
/// visitors, and towards the rim also few stars.
/// </summary>
public sealed class SectorMap
{
    /// <summary>The columns read from EDAstro's list, by the names in its first line.</summary>
    static readonly string[] Columns = ["Systems", "Avg X", "Avg Y", "Avg Z", "MapSector X", "MapSector Y"];

    const int Widest = 200;   // squares; the real map is 67 across. More than this is not a map of the galaxy.

    readonly int[] known;
    readonly double[] sumX, sumY, sumZ;   // of the known systems' places, to find their middle

    SectorMap(DateTime fetchedUtc, int width, int height)
    {
        (FetchedUtc, Width, Height) = (fetchedUtc, width, height);
        (known, sumX, sumY, sumZ) = (new int[width * height], new double[width * height], new double[width * height], new double[width * height]);
    }

    /// <summary>When the list was fetched from EDAstro.</summary>
    public DateTime FetchedUtc { get; }

    /// <summary>Squares from west to east (larger x).</summary>
    public int Width { get; }

    /// <summary>Squares from south to north (larger z).</summary>
    public int Height { get; }

    /// <summary>Light years along one side of a square.</summary>
    public double Square => 1280;

    /// <summary>The x of the map's west edge, in light years.</summary>
    public double West => -44865;

    /// <summary>The z of the map's south edge, in light years.</summary>
    public double South => -18985;

    /// <summary>Systems known in each square, row by row from the south-west corner.</summary>
    public IReadOnlyList<int> Known => known;

    /// <summary>A square with fewer known systems than this (but not none) counts as thinly known: the least known quarter of the squares that have any.</summary>
    public int ThinBelow { get; private set; }

    /// <summary>
    /// Reads EDAstro's sector list: one line per sector with the systems known in it, where they lie
    /// on average and which square of the map it belongs to. No field of the list holds a comma.
    /// </summary>
    /// <exception cref="InvalidDataException">Not that list, or not as Studio knows it. The message is written for the player.</exception>
    public static SectorMap Parse(string csv, DateTime fetchedUtc)
    {
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] titles = lines.Length > 0 ? lines[0].Split(',') : [];
        int[] at = [.. Columns.Select(column => Array.IndexOf(titles, column))];
        var sectors = new List<(int Column, int Row, int Systems, double X, double Y, double Z)>();
        foreach (string line in lines.Skip(1))
        {
            string[] cells = line.Split(',');
            if (!at.Contains(-1) && cells.Length == titles.Length
                && int.TryParse(cells[at[0]], NumberStyles.None, CultureInfo.InvariantCulture, out int systems) && systems > 0
                && Number(cells[at[1]], out double x) && Number(cells[at[2]], out double y) && Number(cells[at[3]], out double z)
                && int.TryParse(cells[at[4]], NumberStyles.None, CultureInfo.InvariantCulture, out int column) && column < Widest
                && int.TryParse(cells[at[5]], NumberStyles.None, CultureInfo.InvariantCulture, out int row) && row < Widest)
                sectors.Add((column, row, systems, x, y, z));
        }
        if (sectors.Count == 0) throw new InvalidDataException("EDAstro's sector list is not laid out the way Studio knows it, so no map could be made from it.");

        var map = new SectorMap(fetchedUtc, sectors.Max(sector => sector.Column) + 1, sectors.Max(sector => sector.Row) + 1);
        foreach (var sector in sectors)
        {
            int square = sector.Row * map.Width + sector.Column;
            map.known[square] += sector.Systems;
            map.sumX[square] += sector.X * sector.Systems;
            map.sumY[square] += sector.Y * sector.Systems;
            map.sumZ[square] += sector.Z * sector.Systems;
        }
        int[] counts = [.. map.known.Where(count => count > 0).Order()];
        map.ThinBelow = counts[counts.Length / 4];
        return map;

        static bool Number(string text, out double number) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>
    /// The first thinly known square met from a place on the map along a bearing (degrees clockwise
    /// from north, as <see cref="ExploreTrip.Heading"/>); null when there is none before the map
    /// ends. Squares where nothing is known are passed over: mostly empty space, and nowhere to aim for.
    /// </summary>
    public ThinSquare? Ahead(double x, double z, double bearing)
    {
        (double east, double north) = Math.SinCos(bearing * Math.PI / 180);
        double step = Square / 8, farthest = (Width + Height) * Square;   // no straight line across the map is longer
        for (double ly = 0; ly <= farthest; ly += step)
        {
            int column = (int)Math.Floor((x + east * ly - West) / Square), row = (int)Math.Floor((z + north * ly - South) / Square);
            if (column < 0 || row < 0 || column >= Width || row >= Height) continue;   // off the map: the commander may be, and fly back onto it
            int square = row * Width + column;
            if (known[square] > 0 && known[square] < ThinBelow)
                return new ThinSquare(known[square], sumX[square] / known[square], sumY[square] / known[square], sumZ[square] / known[square], Here: ly == 0);
        }
        return null;
    }
}

/// <summary>
/// Where few have been: EDAstro's public list of sectors with the systems known in each, as a
/// <see cref="SectorMap"/>. EDAstro makes the list anew every day and keeps all rights to it, so
/// Studio is a careful guest: it fetches the list only when the player asks, keeps its copy for a
/// week before it will ask again, says who it is, and the page credits EDAstro with a link. No
/// copy travels with Studio.
/// </summary>
/// <param name="folder">Where the copy is kept: Studio's data folder.</param>
/// <param name="handler">Tests pass a stand-in for the network; null means the real internet.</param>
public sealed class ExploreCoverage(string folder, HttpMessageHandler? handler = null)
{
    const string Source = "https://edastro.com/mapcharts/files/sector-list.csv";
    static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    readonly string file = Path.Combine(folder, "edastro-sector-list.csv");
    SectorMap? map;
    bool looked;                // the copy on disk has been looked for
    Task<string?>? fetching;    // the request under way, if any

    /// <summary>The map from the copy on this PC; null when there is none yet.</summary>
    public SectorMap? Map
    {
        get
        {
            if (looked) return map;
            looked = true;
            try
            {
                if (File.Exists(file)) map = SectorMap.Parse(File.ReadAllText(file), File.GetLastWriteTimeUtc(file));
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log.Error("The kept copy of EDAstro's sector list could not be read", problem);
            }
            return map;
        }
    }

    /// <summary>There is no map, or it is old enough for EDAstro to be asked again.</summary>
    public bool Stale => Map is not { } kept || DateTime.UtcNow - kept.FetchedUtc >= KeepFor;

    /// <summary>
    /// Where the copy of the list is kept on this PC (it may not be there yet). The 3D map reads
    /// the same copy for its own needs (<see cref="SectorCubes"/>), so EDAstro is asked only once for both.
    /// </summary>
    public string FilePath => file;

    /// <summary>
    /// Raised when a new list has come from EDAstro and taken the place of the old map, with the
    /// list's own text and when it was fetched, after the copy on disk has been written. It is raised
    /// when the copy could not be written as well: the text comes with it so that whoever reads the
    /// list (<see cref="SectorCubes"/>) takes the same new one the flat map now shows, not the old copy
    /// that is still on disk (or no copy at all).
    /// </summary>
    public event Action<string, DateTime>? Refreshed;

    /// <summary>
    /// Fetches the list from EDAstro, unless the copy on this PC is younger than a week. Gives null
    /// when the map is there afterwards, or the reason it could not be fetched, in the player's
    /// words; an older map then stays in use. A second call while one is under way waits for the same answer.
    /// </summary>
    public async Task<string?> Refresh()
    {
        if (!Stale) return null;
        Task<string?> asked = fetching ??= Fetch();
        try
        {
            return await asked;
        }
        finally
        {
            if (fetching == asked) fetching = null;
        }
    }

    async Task<string?> Fetch()
    {
        try
        {
            using var http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }, disposeHandler: handler is null)
            {
                Timeout = TimeSpan.FromSeconds(60),
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(Program.UserAgent);
            // Started on a background thread, as every call to Spansh is: looking up Windows' proxy
            // settings can take seconds, and the UI thread must never stand still.
            string csv = await Task.Run(() => http.GetStringAsync(Source));
            DateTime fetched = DateTime.UtcNow;
            map = SectorMap.Parse(csv, fetched);
            Log.Info($"EDAstro's sector list fetched: {map.Known.Count(count => count > 0)} squares with known systems");
            try
            {
                File.WriteAllText(file + ".tmp", csv);
                File.Move(file + ".tmp", file, overwrite: true);
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                Log.Error("EDAstro's sector list could not be kept on disk, so it will be fetched again next time", problem);
            }
            Refreshed?.Invoke(csv, fetched);
            return null;
        }
        catch (HttpRequestException problem)
        {
            return problem.StatusCode is { } refused
                ? $"EDAstro answered with error {(int)refused}. Try again later."
                : "EDAstro could not be reached. Check your internet connection and try again.";
        }
        catch (OperationCanceledException)
        {
            return "EDAstro did not answer in time. Try again in a moment.";
        }
        catch (InvalidDataException problem)
        {
            return problem.Message;
        }
    }
}
