using System.Globalization;
using System.Net;
using System.Net.Http;
using EliteStudio.App;

namespace EliteStudio.Nav;

/// <summary>A nebula on EDAstro's list of nebulae.</summary>
/// <param name="Name">The name players know it by ("California Nebula"), or for a nebula without one the name of a system in it.</param>
/// <param name="System">A system to fly to for it: one Spansh and the galaxy map know.</param>
/// <param name="X">Where it is, in light years (with <paramref name="Y"/> and <paramref name="Z"/>).</param>
/// <param name="Kind">EDAstro's kind of nebula: "real" for the nebulae and clouds of the sky, "planetary" for the many small shells round dying stars, "procgen" for those the game adds by its own rule.</param>
/// <param name="Region">EDAstro's number for the region of the galaxy it is in (as on its region key); null when the list does not say.</param>
public sealed record ListedNebula(string Name, string System, double X, double Y, double Z, string Kind, int? Region)
{
    /// <summary>One of the nebulae of the real sky, with a name players know, rather than a planetary nebula or one the game adds by its rule.</summary>
    public bool Real => Kind.Equals("real", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The names players know nebulae by ("Orion Nebula"), with where each lies: EDAstro's public list
/// of nebulae, which EDAstro builds from the community's Catalogue of Galactic Nebulae (a shared
/// spreadsheet) and its own records. The table that comes with Studio (<see cref="HaRegions"/>)
/// knows the nebulae's own sectors but few of these names. EDAstro keeps all rights to its list, so
/// Studio is a careful guest, as with the sector list (<see cref="ExploreCoverage"/>): it fetches
/// the list only when the player asks, keeps its copy on this PC and asks again no sooner than a
/// week later, says who it is, and the page credits EDAstro and the catalogue. No copy travels with Studio.
/// </summary>
/// <param name="folder">Where the copy is kept: the 3D map's folder in Studio's data folder. It is made when the first copy is kept.</param>
/// <param name="handler">Tests pass a stand-in for the network; null means the real internet.</param>
public sealed class NebulaList(string folder, HttpMessageHandler? handler = null)
{
    const string Source = "https://edastro.com/mapcharts/files/nebulae-coordinates.csv";
    static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    /// <summary>The columns read, by the names in the list's first line. Others may come and go.</summary>
    static readonly string[] Columns = ["Name", "System", "X", "Y", "Z", "Type"];
    const string RegionColumn = "RegionID";

    readonly string file = Path.Combine(folder, "nebulae.csv");
    readonly Lock gate = new();
    IReadOnlyList<ListedNebula>? nebulae;
    DateTime fetchedUtc;
    bool looked;                // the copy on disk has been looked for
    Task<string?>? fetching;    // the request under way, if any

    /// <summary>Where the copy is kept on this PC (it may not be there yet).</summary>
    public string FilePath => file;

    /// <summary>The nebulae from the copy on this PC, in the list's order; null when there is none yet.</summary>
    public IReadOnlyList<ListedNebula>? Nebulae
    {
        get
        {
            lock (gate)
            {
                if (looked) return nebulae;
                looked = true;
                try
                {
                    if (File.Exists(file)) (nebulae, fetchedUtc) = (Parse(File.ReadAllText(file)), File.GetLastWriteTimeUtc(file));
                }
                catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Log.Error("The kept copy of EDAstro's nebula list could not be read", problem);
                }
                return nebulae;
            }
        }
    }

    /// <summary>When the list on this PC was fetched from EDAstro; null when there is none.</summary>
    public DateTime? FetchedUtc => Nebulae is null ? null : fetchedUtc;

    /// <summary>There is no list, or it is old enough for EDAstro to be asked again.</summary>
    public bool Stale => FetchedUtc is not { } fetched || DateTime.UtcNow - fetched >= KeepFor;

    /// <summary>
    /// Reads EDAstro's list of nebulae by the names of its columns, so that a column added or moved
    /// does not matter. A name may stand in double quotes and hold a comma. Rows that cannot be read are left out.
    /// </summary>
    /// <exception cref="InvalidDataException">Not that list, or not as Studio knows it. The message is written for the player.</exception>
    public static IReadOnlyList<ListedNebula> Parse(string csv)
    {
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] titles = lines.Length > 0 ? Csv.Cells(lines[0]) : [];
        int[] at = [.. Columns.Select(column => Array.IndexOf(titles, column))];
        int region = Array.IndexOf(titles, RegionColumn);
        var found = new List<ListedNebula>();
        if (!at.Contains(-1))
            foreach (string line in lines.Skip(1))
            {
                string[] cells = Csv.Cells(line);
                if (cells.Length != titles.Length) continue;
                string name = cells[at[0]].Trim(), system = cells[at[1]].Trim(), kind = cells[at[5]].Trim();
                if (name.Length > 0 && system.Length > 0 && Number(cells[at[2]]) is { } x && Number(cells[at[3]]) is { } y && Number(cells[at[4]]) is { } z)
                    found.Add(new ListedNebula(name, system, x, y, z, kind,
                        region >= 0 && int.TryParse(cells[region].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : null));
            }
        if (found.Count == 0) throw new InvalidDataException("EDAstro's nebula list is not laid out the way Studio knows it, so Studio cannot use it.");
        return found;

        static double? Number(string text) => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
    }

    /// <summary>
    /// Fetches the list from EDAstro, unless the copy on this PC is younger than a week. Gives null
    /// when the list is there afterwards, or the reason it could not be fetched, in the player's
    /// words; an older list then stays in use. A second call while one is under way waits for the
    /// same answer. Nothing is tried again by itself.
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
            // Started on a background thread, as every call to the community's services is: looking up
            // Windows' proxy settings can take seconds, and the UI thread must never stand still.
            string csv = await Task.Run(() => http.GetStringAsync(Source));
            IReadOnlyList<ListedNebula> fetched = Parse(csv);
            lock (gate) (nebulae, fetchedUtc, looked) = (fetched, DateTime.UtcNow, true);
            Log.Info($"EDAstro's nebula list fetched: {fetched.Count} nebulae");
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(file + ".tmp", csv);
                File.Move(file + ".tmp", file, overwrite: true);
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                Log.Error("EDAstro's nebula list could not be kept on disk, so it will be fetched again next time", problem);
            }
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
