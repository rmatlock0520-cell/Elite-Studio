using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Nav;

/// <summary>A system of the detailed map (<see cref="FineMap"/>), as Spansh's list of every system has it.</summary>
/// <param name="Name">The system's name as the game spells it.</param>
/// <param name="Id64">The game's own number for the system. Written as text, because these numbers can be larger than a web page can hold exactly.</param>
/// <param name="X">Where it is, in light years (with <paramref name="Y"/> and <paramref name="Z"/>).</param>
/// <param name="Star">The list names the system's main star, so its star type is recorded; false when the list has its name only.</param>
/// <param name="Updated">The day Spansh last heard of the system (yyyy-MM-dd); null when the list does not say.</param>
public sealed record FineSystem(
    string Name,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Id64,
    double X, double Y, double Z, bool Star, string? Updated);

/// <summary>Spansh's list of every system, downloaded to this PC (<see cref="FineMap.Download"/>).</summary>
/// <param name="Bytes">Its size on disk, packed.</param>
/// <param name="Date">Its last-write time (UTC): when the download finished, which is as near as Studio can tell to the list's own date.</param>
public sealed record FineSource(string Path, long Bytes, DateTime Date);

/// <summary>What building the detailed map takes, told to the player before it starts (<see cref="FineMap.Needs"/>).</summary>
/// <param name="Seconds">About how long it takes on this PC.</param>
/// <param name="Reads">What it reads, in the player's words ("6.4 GB on your PC").</param>
/// <param name="Internet">Always false: nothing is fetched.</param>
public sealed record FineNeeds(int Seconds, string Reads, bool Internet);

/// <summary>
/// What there is of the detailed map for a block of sectors the page shows (<see cref="FineMap.Status"/>).
/// </summary>
/// <param name="Built">A detailed map covers the block's centre column.</param>
/// <param name="At">When that detailed map was built (UTC); null when there is none.</param>
/// <param name="SourceDate">The date of Spansh's list it was built from; while none is built, the date of the list on this PC.</param>
/// <param name="Have">Spansh's list of every system is on this PC, so a detailed map can be built.</param>
/// <param name="Ix">The centre column of the detailed map that covers the block (with <paramref name="Iz"/>); the block's own while none does.</param>
public sealed record FineStatus(bool Built, DateTime? At, DateTime? SourceDate, bool Have, int Ix, int Iz)
{
    /// <summary>The detailed map was built for exactly this block, so all nine of its columns are in it.</summary>
    public bool Exact { get; init; }

    /// <summary>The list on this PC is not the one the detailed map was built from (a newer download): a rebuild would bring it up to date.</summary>
    public bool Newer { get; init; }

    /// <summary>The size of the list on this PC, packed; null when there is none.</summary>
    public long? Bytes { get; init; }

    /// <summary>About how long a build takes on this PC (<see cref="FineNeeds.Seconds"/>); 0 when there is no list to build from.</summary>
    public int Seconds { get; init; }

    /// <summary>How far a build under way has got; null when none is.</summary>
    public FineMapProgress? Running { get; init; }
}

/// <summary>A sector's totals in the detailed map.</summary>
/// <param name="Known">Systems Spansh's list has in the sector.</param>
/// <param name="Star">Of those, the ones whose main star is recorded (its type is known).</param>
/// <param name="NameOnly">The others: the list has their names and places only.</param>
/// <param name="Missing">Systems that must exist but no database has: the gaps in the game's numbering of each of its own cubes.</param>
public sealed record FineSectorCounts(int Known, int Star, int NameOnly, long Missing);

/// <summary>One cube of a sector in the detailed map (<see cref="FineMap.Cubes"/>); the words are those of <see cref="FineSectorCounts"/>.</summary>
/// <param name="I">Where it is among the sector's cubes, counted from 0 at the sector's west face (with <paramref name="J"/> from its bottom and <paramref name="K"/> from its south face).</param>
public sealed record FineCube(int I, int J, int K, int Known, int Star, int NameOnly, long Missing);

/// <summary>A sector's cubes in the detailed map (GET /api/nav/heatmap/cubes).</summary>
/// <param name="Have">A detailed map covers the sector. When false, everything else is empty.</param>
/// <param name="Size">Light years along a side of the cubes: 160 (8 to a sector's side) or 40 (32).</param>
/// <param name="N">Cubes along each side of the sector.</param>
/// <param name="Corner">The sector's corner with the smallest x, y and z, from which the cubes are counted.</param>
/// <param name="At">When the detailed map was built (UTC).</param>
/// <param name="SourceDate">The date of Spansh's list it was built from.</param>
/// <param name="Cubes">The cubes that hold any system, known or missing. Layer by layer from the bottom, then from south to north, then from west to east.</param>
public sealed record FineCubes(bool Have, int Size, int N, double[] Corner, DateTime? At, DateTime? SourceDate,
    int Known, int Star, int NameOnly, long Missing, IReadOnlyList<FineCube> Cubes);

/// <summary>How a build of the detailed map is getting on, sent as a "heatmap" event (<see cref="FineMap.Progress"/>).</summary>
/// <param name="Key">"f:" and the block's centre column ("f:38:18"), as a survey's events carry "s:" and its sector.</param>
/// <param name="State">One of <see cref="FineMap.Reading"/>, <see cref="FineMap.Writing"/>, <see cref="FineMap.Done"/>, <see cref="FineMap.Stopped"/> or <see cref="FineMap.Failed"/>.</param>
/// <param name="Percent">How much of the whole job is done, 0 to 100. Reading the list is nearly all of it.</param>
/// <param name="Systems">Systems of the block found so far.</param>
/// <param name="Seconds">Since the build began.</param>
public sealed record FineMapProgress(string Key, int Ix, int Iz, string State, double Percent, long Systems, double Seconds)
{
    /// <summary>Always "finemap": it tells these events from those of a survey (<see cref="SurveyProgress"/>).</summary>
    [JsonPropertyOrder(-1)]
    public string Kind => "finemap";
}

/// <summary>The answer to a build of the detailed map (<see cref="FineMap.Build"/>).</summary>
/// <param name="Fine">What there is of the detailed map for the block now.</param>
/// <param name="Problem">Why it was not built, in the player's words; null when it was (or when the player stopped it).</param>
/// <param name="Stopped">The player stopped it. Nothing was changed: what was built before is still there.</param>
/// <param name="Systems">Systems of the block found in Spansh's list.</param>
/// <param name="Sectors">Sectors of the block that hold any of them.</param>
/// <param name="Seconds">How long it took.</param>
public sealed record FineBuildAnswer(FineStatus? Fine, string? Problem, bool Stopped, long Systems, int Sectors, double Seconds);

/// <summary>Whether the detailed map knows a system (<see cref="FineMap.Lookup"/>).</summary>
/// <param name="Covered">A detailed map covers the sector the system belongs in, so "not found" means Spansh's list does not have it.</param>
/// <param name="System">The system as the list has it; null when it is not there (or nothing covers its sector).</param>
/// <param name="SourceDate">The date of Spansh's list the detailed map was built from.</param>
public sealed record FineLookup(bool Covered, FineSystem? System, DateTime? SourceDate);

/// <summary>
/// The 3D map's detailed map: every system Spansh knows round a column of sectors, taken from Spansh's
/// list of every system in the galaxy once it is on this PC (%LOCALAPPDATA%\Elite Studio\heatmap\systems.json.gz,
/// 6.4 GB packed in October 2026, one system per line). The busiest sectors hold far more than Spansh's
/// search will list (it stops at 10,000), and the list on this PC answers for them without a single request.
///
/// On the player's word the list is read once, start to end, on a thread of its own: every system in the
/// block of 3 by 3 columns of sectors round the column asked for, at every height, is kept, and the rest are
/// passed over (most of them by their number alone, which says what sector they belong in, so only the few in
/// and near the block are read in full). What is kept goes into heatmap\fine\:
/// - sector-ix-iy-iz.jsonl.gz, one file a sector, one system a line (<see cref="FineSystem"/>), after a first line
///   that says what the file holds;
/// - block-ix-iz.json: when it was built, from which list, and each sector's totals and counts in cubes of 160 and
///   40 ly, with the star-type split and the gaps in the game's numbering (systems that must exist).
/// Nothing is fetched from the internet, and the list itself is only ever read: never moved, changed or deleted.
///
/// A build works in a folder of its own and puts its files in place only when it has finished, sector files first
/// and the block file last, each in one move; so a build that is stopped or fails, or a Studio that is closed part
/// way, never leaves half a file behind or less than there was. A finished build that could not all be moved in (a
/// file held by another program for seconds on end, or Studio closed while it was being moved) waits whole in its
/// folder and is put in place at the next look at the detailed map (<see cref="Resume"/>). The player can stop a
/// build (<see cref="Cancel"/>); one build runs at a time.
/// </summary>
public sealed class FineMap
{
    /// <summary>The file name of Spansh's list in the 3D map's folder.</summary>
    public const string DownloadName = "systems.json.gz";

    /// <summary>What <see cref="FineMapProgress.State"/> says.</summary>
    public const string Reading = "reading", Writing = "writing", Done = "done", Stopped = "stopped", Failed = "failed";

    /// <summary>The cube sizes the detailed map counts in: the game's own cubes of mass codes e (160 ly) and c (40 ly).</summary>
    public const int CubeLy = 160, SmallCubeLy = 40;

    const int Version = 1;

    /// <summary>
    /// How fast a PC reads the packed list, for the time a build is said to take until this PC has built one: the
    /// owner's whole list (6,366,597,790 bytes) in 141 seconds, measured on their PC (an 8-core Ryzen of 2018, an SSD)
    /// on 14 October 2026. Builds since are a fifth quicker, so this errs long. After a build, its own pace is used.
    /// </summary>
    const double BytesPerSecond = 45e6;

    /// <summary>The share of a build's progress that is the reading of the list; the rest is writing the files.</summary>
    const double ReadingShare = 97;

    /// <summary>No line of Spansh's list comes near this; a file with longer lines is not the list Studio knows.</summary>
    const int LongestLine = 1 << 24;

    /// <summary>
    /// How hard the sector files are packed: zlib's level 2, measured on the owner's PC in October 2026 at twice the speed
    /// of its usual level for files a tenth bigger. Packing was a tenth of the whole build at the usual level.
    /// </summary>
    static readonly ZLibCompressionOptions Packing = new() { CompressionLevel = 2 };

    /// <summary>The columns of a sector file's lines, in order, as its first line names them.</summary>
    static readonly string[] Columns = ["name", "id64", "x", "y", "z", "star", "updated"];

    // One line per system and one line for a block, so the files stay small; the rest of Studio's style otherwise.
    static readonly JsonSerializerOptions FileJson = new(Settings.Json) { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    const string NoList = "Studio has no copy of Spansh's list of every system on this PC (systems.json.gz in Studio's 3D map folder), so it cannot build the detailed map. Nothing was asked of anybody.";
    const string InUse = "Spansh's list on this PC could not be opened just now: another program may still be writing it. Try again once the download has finished.";
    const string Damaged = "Spansh's list on this PC is damaged or cut short (the download may not have finished), so the detailed map was not built. What was built before is unchanged.";
    const string NotTheList = "The file on this PC is not Spansh's list of every system as Studio knows it, so the detailed map was not built. What was built before is unchanged.";
    const string Busy = "Studio is already building the detailed map of another area. Wait for it to finish, or press Cancel.";

    /// <summary>
    /// Block files kept in memory at most, the least recently used let go first: the page's 3 by 3 columns can need
    /// several at once where maps built round neighbouring columns overlap (each column is read from the newest that
    /// covers it), and two were thrown out and read again sector by sector, a second or so each time.
    /// </summary>
    const int BlocksKept = 6;

    /// <summary>
    /// The block files kept, by their size on disk, at most: a busy block's file of 7.8 MB takes some 25 MB once read,
    /// so this keeps four of the busiest (and the one read last, however big).
    /// </summary>
    const long BlockBytesKept = 32L << 20;

    readonly string source, folder;
    readonly Lock guard = new();   // the build under way and the block files read, which pages and the build touch from different threads
    readonly Lock placing = new(); // files being put in place, or working folders cleared: one at a time
    readonly Dictionary<string, (DateTime Written, BlockFile? Block, long Bytes, long Used)> blocks = new(StringComparer.OrdinalIgnoreCase);
    long uses;                     // counts the uses of the block files kept, for which was used least recently
    int blockReads;                // block files read from disk, for tests
    bool resumeDue = true;         // a finished build may wait in a working folder (looked for at the first look, and after a failure)
    DateTime resumeAfter;          // not looked for again before this, after a failure
    Job? job;

    /// <param name="folder">The 3D map's folder in Studio's data folder. The detailed map goes in its "fine" folder, made by the first build.</param>
    /// <param name="source">Spansh's list of every system; <see cref="DownloadName"/> in <paramref name="folder"/> when left out.</param>
    public FineMap(string folder, string? source = null)
    {
        this.folder = Path.Combine(folder, "fine");
        this.source = source ?? Path.Combine(folder, DownloadName);
    }

    /// <summary>
    /// Raised as a build gets on: when it starts, every 2 % of the list read, as the files are written, and when it
    /// ends; and with <see cref="Done"/> when a build that had to wait is put in place (<see cref="Resume"/>). Raised
    /// on the build's own thread or a page's request's, never the UI thread.
    /// </summary>
    public event Action<FineMapProgress>? Progress;

    /// <summary>Tries at moving a file over another before a build gives up putting its files in place; tests use fewer.</summary>
    internal int MoveTries { get; init; } = 10;

    /// <summary>How long a build that could not all be put in place waits before it is tried again; tests wait none.</summary>
    internal TimeSpan ResumeWait { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Block files read from disk so far (not those kept in memory); for tests.</summary>
    internal int BlockReads => Volatile.Read(ref blockReads);

    /// <summary>Spansh's list of every system as it is on this PC; null when there is none.</summary>
    public FineSource? Download
    {
        get
        {
            var info = new FileInfo(source);
            return info.Exists ? new FineSource(info.FullName, info.Length, info.LastWriteTimeUtc) : null;
        }
    }

    /// <summary>What a build would take, to tell the player first; null when there is no list to build from.</summary>
    public FineNeeds? Needs() => Download is { } list
        ? new FineNeeds(Estimate(list.Bytes), string.Create(CultureInfo.InvariantCulture, $"{list.Bytes / 1e9:0.0} GB on your PC"), Internet: false)
        : null;

    /// <summary>How far the build under way has got; null when none is (one that has just ended included).</summary>
    public FineMapProgress? Running
    {
        get
        {
            lock (guard) return job?.Last is { State: Reading or Writing } last ? last : null;
        }
    }

    /// <summary>
    /// What there is of the detailed map for the block of sectors round a column: the map built for exactly that
    /// block, else the newest that covers its centre column. Null when there is no list on this PC, no map, and no
    /// build under way: nothing to say. A finished build still waiting to be put in place is put in place first.
    /// </summary>
    public FineStatus? Status(int ix, int iz)
    {
        Resume();
        FineSource? list = Download;
        BlockFile? exact = Load(BlockPath(ix, iz));
        BlockFile? block = exact ?? Covering(ix, iz);
        FineMapProgress? running = Running;
        if (list is null && block is null && running is null) return null;
        return new FineStatus(block is not null, block?.Built, block?.SourceDate ?? list?.Date, list is not null, block?.Ix ?? ix, block?.Iz ?? iz)
        {
            Exact = exact is not null,
            Newer = list is not null && block is not null && (list.Date != block.SourceDate || list.Bytes != block.SourceBytes),
            Bytes = list?.Bytes,
            Seconds = list is null ? 0 : Estimate(list.Bytes),
            Running = running,
        };
    }

    /// <summary>A sector's totals in the detailed map; null when no detailed map covers its column.</summary>
    public FineSectorCounts? Summary(int ix, int iy, int iz)
    {
        if (Covering(ix, iz) is not { } block) return null;
        BlockSector? sector = block.Sector(ix, iy, iz);
        return sector is null ? new FineSectorCounts(0, 0, 0, 0) : new FineSectorCounts(sector.Known, sector.Star, sector.Known - sector.Star, sector.Missing);
    }

    /// <summary>A sector's cubes of 160 or 40 ly in the detailed map, from the block file alone (no system is read).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not 160 or 40 ly.</exception>
    public FineCubes Cubes(int ix, int iy, int iz, int size = CubeLy)
    {
        if (size is not (CubeLy or SmallCubeLy)) throw new ArgumentOutOfRangeException(nameof(size), size, "The detailed map keeps cubes of 160 and 40 light years.");
        int n = (int)(Galaxy.SectorLy / size);
        Box box = Galaxy.SectorBounds(ix, iy, iz);
        double[] corner = [box.X0, box.Y0, box.Z0];
        if (Covering(ix, iz) is not { } block) return new FineCubes(false, size, n, corner, null, null, 0, 0, 0, 0, []);
        if (block.Sector(ix, iy, iz) is not { } sector) return new FineCubes(true, size, n, corner, block.Built, block.SourceDate, 0, 0, 0, 0, []);
        int[] flat = (size == CubeLy ? sector.C160 : sector.C40) ?? [];
        var cubes = new List<FineCube>(flat.Length / 6);
        for (int at = 0; at + 5 < flat.Length; at += 6)
            cubes.Add(new FineCube(flat[at], flat[at + 1], flat[at + 2], flat[at + 3], flat[at + 4], flat[at + 3] - flat[at + 4], flat[at + 5]));
        return new FineCubes(true, size, n, corner, block.Built, block.SourceDate, sector.Known, sector.Star, sector.Known - sector.Star, sector.Missing, cubes);
    }

    /// <summary>A detailed map covers this column of sectors.</summary>
    public bool Covers(int ix, int iz) => Covering(ix, iz) is not null;

    /// <summary>
    /// Every system of a sector in the detailed map, read from its file as they are asked for (a busy sector has
    /// over half a million). None when the detailed map has no file for the sector: see <see cref="Covers"/> for
    /// whether that means the list has nothing there.
    /// </summary>
    /// <exception cref="InvalidDataException">The sector's file is damaged or cut short.</exception>
    public IEnumerable<FineSystem> Systems(int ix, int iy, int iz)
    {
        string file = SectorPath(ix, iy, iz);
        if (!File.Exists(file)) yield break;
        // Shared for deleting, so a rebuild can put a new file in its place while a page reads the old one.
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        using var unpacked = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(unpacked, new UTF8Encoding(false));
        SectorHeading heading;
        try
        {
            heading = JsonSerializer.Deserialize<SectorHeading>(reader.ReadLine() ?? "", FileJson) ?? throw new JsonException("The file is empty");
        }
        catch (JsonException problem)
        {
            throw new InvalidDataException($"{Path.GetFileName(file)} of the detailed map could not be read.", problem);
        }
        if (heading.Sector is not [var hx, var hy, var hz] || (hx, hy, hz) != (ix, iy, iz))
            throw new InvalidDataException($"{Path.GetFileName(file)} of the detailed map is not the file of that sector.");
        byte[] bytes = new byte[512];
        int count = 0;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            int length = Encoding.UTF8.GetMaxByteCount(line.Length);
            if (length > bytes.Length) bytes = new byte[length];
            length = Encoding.UTF8.GetBytes(line, bytes);
            yield return ParseKept(bytes.AsSpan(0, length)) ?? throw new InvalidDataException($"A line of {Path.GetFileName(file)} of the detailed map could not be read.");
            count++;
        }
        if (count != heading.Systems) throw new InvalidDataException($"{Path.GetFileName(file)} of the detailed map is cut short.");
    }

    /// <summary>
    /// Whether the detailed map has a system, by its number, or by its name when that is a generated one (which says
    /// what sector it lies in). Reads the file of that sector, and only when the system is not there and its cube
    /// touches the sector's faces, the files of the sectors over them (a place on the very edge can be rounded across
    /// it). Never asks anybody.
    /// </summary>
    public FineLookup Lookup(string? name, long id64 = 0)
    {
        var home = new List<(int X, int Y, int Z)>();
        var over = new List<(int X, int Y, int Z)>();
        foreach (CubePlace place in new[] { SystemAddress.Place(id64), ProcGenNames.Decode(name)?.Cube }.OfType<CubePlace>())
        {
            if (!home.Contains((place.SectorX, place.SectorY, place.SectorZ))) home.Add((place.SectorX, place.SectorY, place.SectorZ));
            over.AddRange(Over(place));
        }
        bool covered = false;
        DateTime? date = null;
        foreach ((int x, int y, int z) in home.Concat(over.Distinct().Except(home)))
        {
            if (Covering(x, z) is not { } block) continue;
            bool beside = !home.Contains((x, y, z));
            if (beside && !covered) continue;   // a sector over the edge is looked in only when the system's own was
            try
            {
                foreach (FineSystem system in Systems(x, y, z))
                    if ((id64 > 0 && system.Id64 == id64) || (!string.IsNullOrWhiteSpace(name) && system.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
                        return new FineLookup(true, system, block.SourceDate);
            }
            catch (Exception problem) when (problem is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // A file that cannot be read says nothing either way: the system may well be in it.
                Log.Error($"The detailed map of sector {x},{y},{z} could not be searched", problem);
                if (beside) covered = false;
                continue;
            }
            if (!beside) (covered, date) = (true, block.SourceDate);
        }
        return new FineLookup(covered, null, date);

        // The sectors over the faces a cube touches, corners included; none for a cube as big as its sector, whose
        // systems are no likelier to lie on its edge than anywhere else.
        static IEnumerable<(int X, int Y, int Z)> Over(CubePlace place)
        {
            int last = (int)(Galaxy.SectorLy / place.CubeLy) - 1;
            if (last == 0) yield break;
            int[] Sides(int cube) => cube == 0 ? [-1, 0] : cube == last ? [0, 1] : [0];
            foreach (int dx in Sides(place.CubeX))
                foreach (int dy in Sides(place.CubeY))
                    foreach (int dz in Sides(place.CubeZ))
                        if ((dx, dy, dz) != (0, 0, 0)) yield return (place.SectorX + dx, place.SectorY + dy, place.SectorZ + dz);
        }
    }

    // ------------------------------------------------------------ building

    /// <summary>
    /// Builds the detailed map of the block of 3 by 3 columns of sectors round column (<paramref name="ix"/>,
    /// <paramref name="iz"/>), at every height, from Spansh's list on this PC: a few minutes' reading on a thread of
    /// its own. A second call for the same block while it runs waits for the same answer; one for another block is
    /// turned down (one build at a time). It asks nobody anything. A build of a block built before puts the new
    /// files in place of the old only once it has finished.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Not a column of the grid.</exception>
    public Task<FineBuildAnswer> Build(int ix, int iz)
    {
        if (ix is < 0 or >= Galaxy.SectorsAcross) throw new ArgumentOutOfRangeException(nameof(ix), ix, "Sectors are numbered 0 to 127 from west to east.");
        if (iz is < 0 or >= Galaxy.SectorsAcross) throw new ArgumentOutOfRangeException(nameof(iz), iz, "Sectors are numbered 0 to 127 from south to north.");
        lock (guard)
        {
            if (job is { } busy)
                return busy.Ix == ix && busy.Iz == iz ? busy.Task! : Task.FromResult(new FineBuildAnswer(null, Busy, false, 0, 0, 0));
            var started = new Job(ix, iz);
            job = started;
            // A thread of its own: the reading takes minutes, and the thread pool's threads are for short work.
            started.Task = Task.Factory.StartNew(() =>
            {
                try
                {
                    return Run(started);
                }
                finally
                {
                    lock (guard) if (job == started) job = null;
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return started.Task;
        }
    }

    /// <summary>Stops the build under way at its next look (within a fraction of a second); nothing it wrote is kept. Gives true when there was one.</summary>
    public bool Cancel()
    {
        lock (guard)
        {
            if (job is null) return false;
            job.Stop.Cancel();
            return true;
        }
    }

    sealed class Job(int ix, int iz)
    {
        public readonly int Ix = ix, Iz = iz;
        public readonly CancellationTokenSource Stop = new();
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public Task<FineBuildAnswer>? Task;
        public FineMapProgress? Last;   // under the guard
    }

    FineBuildAnswer Run(Job job)
    {
        CancellationToken stop = job.Stop.Token;
        string staging = StagingPath(job.Ix, job.Iz);
        Report(job, Reading, 0, 0);
        long kept = 0;
        string? problem = null;
        bool stopped = false, waiting = false;
        int sectors = 0;
        try
        {
            if (Download is not { } list)
            {
                problem = NoList;
                return Answer();
            }
            FileStream file;
            try
            {
                // Only ever read. Shared for reading: a program still writing it keeps it from being opened, which is right,
                // as a list being written is not whole.
                file = new FileStream(list.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            }
            catch (Exception locked) when (locked is IOException or UnauthorizedAccessException)
            {
                Log.Error("The detailed map could not open Spansh's list", locked);
                problem = InUse;
                return Answer();
            }
            using (file)
            {
                // A build that finished before and is still waiting to be put in place goes in first (this one may be of
                // another block); then the working folders of builds that never finished go.
                Resume(now: true);
                ClearStaging(job.Ix, job.Iz);
                Directory.CreateDirectory(staging);
                var split = new Splitter(job.Ix, job.Iz, staging);
                try
                {
                    // Unpacked as it is read: some 30 GB unpacked, of which only the lines being looked at are ever held.
                    using var unpacked = new GZipStream(file, CompressionMode.Decompress);
                    double next = 2;
                    split.Unpacking = ForEachLine(unpacked, split.Take, () =>
                    {
                        stop.ThrowIfCancellationRequested();
                        double percent = ReadingShare * file.Position / Math.Max(1, list.Bytes);
                        if (percent < next) return;
                        next = Math.Floor(percent / 2) * 2 + 2;
                        Report(job, Reading, percent, split.Kept);
                    });
                    split.CloseAll();
                    if (!split.Closed) throw new InvalidDataException("The list ends early: its last line is missing.");
                    kept = split.Kept;
                    stop.ThrowIfCancellationRequested();
                    BlockFile block = Finish(job, split, list, staging, stop);
                    stop.ThrowIfCancellationRequested();
                    // From here on the new files go in place, and the build is no longer stopped part way.
                    Report(job, Writing, 99.5, kept);
                    try
                    {
                        lock (placing) Commit(job.Ix, job.Iz, staging, FilesOf(block));
                    }
                    catch (Exception held) when (held is IOException or UnauthorizedAccessException)
                    {
                        // Some of the new files may be in place and some not, beside the block file from before: the map
                        // is not "unchanged". The rest waits whole in the working folder, to be put in place at the next
                        // look at the detailed map (Resume), and the player is told so.
                        Log.Error("The detailed map was built but could not all be put in place; the rest waits to be put in place", held);
                        problem = $"Studio built the new detailed map but could not put all of it in place ({held.Message}). Another program may be holding one of its files: Studio puts the rest in place by itself in a minute or so, and until then part of this area may still show the map from before.";
                        lock (guard) (resumeDue, resumeAfter) = (true, DateTime.UtcNow + ResumeWait);
                        waiting = true;
                        return Answer();
                    }
                    sectors = block.Sectors.Count(sector => sector.Known > 0);
                    Log.Info(string.Create(CultureInfo.InvariantCulture,
                        $"3D map: detailed map of {job.Ix},{job.Iz} built in {job.Clock.Elapsed.TotalSeconds:0.0} s ({split.Unpacking.TotalSeconds:0.0} s of it unpacking the list): {kept} systems in {sectors} sectors, from {split.Lines} systems in Spansh's list ({split.Unreadable} lines unreadable, {split.Strays.Count} numbered in another sector than they lie in)"));
                    return Answer();
                }
                finally
                {
                    split.CloseAll();
                }
            }
        }
        catch (OperationCanceledException)
        {
            stopped = true;
            return Answer();
        }
        catch (InvalidDataException damaged)
        {
            Log.Error("The detailed map could not be built from Spansh's list", damaged);
            problem = damaged.Message.StartsWith("Not the list", StringComparison.Ordinal) ? NotTheList : Damaged;
            return Answer();
        }
        catch (Exception unwritten) when (unwritten is IOException or UnauthorizedAccessException)
        {
            Log.Error("The detailed map could not be written", unwritten);
            problem = $"Studio could not write the detailed map on this PC ({unwritten.Message}). What was built before is unchanged.";
            return Answer();
        }
        finally
        {
            // Whatever was written along the way goes: only a finished build is ever moved into place (one still waiting
            // to be, whole, stays for Resume).
            if (!waiting) DeleteFolder(staging);
        }

        // The last word is said first, so the status in the answer no longer shows the build as under way.
        FineBuildAnswer Answer()
        {
            string state = stopped ? Stopped : problem is null ? Done : Failed;
            Report(job, state, state == Done ? 100 : Running?.Percent ?? 0, kept);
            return new FineBuildAnswer(Status(job.Ix, job.Iz), problem, stopped, problem is null && !stopped ? kept : 0, sectors, Math.Round(job.Clock.Elapsed.TotalSeconds, 1));
        }
    }

    void Report(Job job, string state, double percent, long systems)
    {
        var progress = new FineMapProgress(string.Create(CultureInfo.InvariantCulture, $"f:{job.Ix}:{job.Iz}"), job.Ix, job.Iz, state,
            Math.Round(Math.Clamp(percent, 0, 100), 1), systems, Math.Round(job.Clock.Elapsed.TotalSeconds, 1));
        lock (guard) job.Last = progress;
        Progress?.Invoke(progress);
    }

    /// <summary>
    /// About how long a build takes on this PC, to the nearest 10 seconds: at the pace of the last build here, or at
    /// the owner's pace (<see cref="BytesPerSecond"/>) before the first.
    /// </summary>
    int Estimate(long bytes)
    {
        double pace = BytesPerSecond;
        if (Newest() is { Seconds: > 1 } last && last.SourceBytes > 0) pace = last.SourceBytes / last.Seconds;
        return (int)Math.Max(10, Math.Round(bytes / pace / 10) * 10);
    }

    /// <summary>
    /// A line at a time, as bytes without the line's end, from a stream of lines ending in "\n" ("\r" is left for the
    /// caller to trim). After each read from the stream, <paramref name="between"/> may look in (to stop the work, or
    /// to say how far it has got). Reading bytes rather than text, and only looking into the few lines that matter,
    /// is what keeps the reading of a 30 GB list down to minutes. Gives the time spent reading the stream itself
    /// (for a packed stream, the unpacking), so the log can say where a build's minutes went.
    /// </summary>
    static TimeSpan ForEachLine(Stream stream, LineHandler handle, Action? between = null)
    {
        byte[] buffer = new byte[1 << 22];
        int filled = 0;
        long reading = 0;
        while (true)
        {
            if (filled == buffer.Length)
            {
                if (buffer.Length >= LongestLine) throw new InvalidDataException("Not the list: a line is far too long.");
                Array.Resize(ref buffer, buffer.Length * 2);
            }
            long before = Stopwatch.GetTimestamp();
            int read = stream.Read(buffer, filled, buffer.Length - filled);
            reading += Stopwatch.GetTimestamp() - before;
            if (read == 0) break;
            filled += read;
            int start = 0;
            while (buffer.AsSpan(start, filled - start).IndexOf((byte)'\n') is >= 0 and var end)
            {
                handle(buffer.AsSpan(start, end));
                start += end + 1;
            }
            if (start > 0)
            {
                Buffer.BlockCopy(buffer, start, buffer, 0, filled - start);
                filled -= start;
            }
            between?.Invoke();
        }
        if (filled > 0) handle(buffer.AsSpan(0, filled));
        return Stopwatch.GetElapsedTime(0, reading);
    }

    delegate void LineHandler(ReadOnlySpan<byte> line);

    static bool IsBlank(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r';

    static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> text)
    {
        int from = 0, to = text.Length;
        while (from < to && IsBlank(text[from])) from++;
        while (to > from && IsBlank(text[to - 1])) to--;
        return text[from..to];
    }

    /// <summary>The sector a system's number says it belongs in (the layout is in <see cref="SystemAddress"/>), without making a record.</summary>
    static (int X, int Y, int Z) SectorOfAddress(long id64)
    {
        int mass = (int)(id64 & 7), bits = 7 - mass;
        long rest = id64 >> 3;
        int z = (int)((rest >> bits) & 127);
        rest >>= bits + 7;
        int y = (int)((rest >> bits) & 63);
        rest >>= bits + 6;
        int x = (int)((rest >> bits) & 127);
        return (x, y, z);
    }

    /// <summary>The address of system 0 of the game's cube a system's number belongs to: the same for every system of that cube.</summary>
    static long CubeOf(long id64) => id64 & ((1L << (44 - 3 * (int)(id64 & 7))) - 1);

    /// <summary>A system's number in its cube: the last number of a generated name.</summary>
    static long NumberOf(long id64)
    {
        int mass = (int)(id64 & 7);
        return (id64 >> (44 - 3 * mass)) & CubePlace.NumberMask(mass);
    }

    /// <summary>
    /// The first pass: Spansh's list, line by line, into one working file per sector of the block, with each
    /// sector's counts per cube as they come. A line is "\t{...}," inside a JSON array that opens on the first line
    /// and closes on the last.
    /// </summary>
    sealed class Splitter
    {
        readonly int cx, cz;
        readonly string staging;
        readonly ArrayBufferWriter<byte> line = new(256);

        public readonly SectorTally?[] Sectors = new SectorTally?[3 * Galaxy.Layers * 3];

        /// <summary>
        /// Systems whose number puts them in a sector of the block other than the one their place does (a system on a
        /// sector's very edge, its place rounded across it), or in the block though their place lies outside it: their
        /// numbers count in their own cube's numbering. By the slot of the sector the number names.
        /// </summary>
        public readonly List<(int Slot, long Cube, long Number)> Strays = [];

        public long Lines, Kept, Unreadable;
        public bool Opened, Closed;

        /// <summary>Of the reading, the time spent unpacking the list (the rest is looking at its lines).</summary>
        public TimeSpan Unpacking;

        public Splitter(int cx, int cz, string staging) => (this.cx, this.cz, this.staging) = (cx, cz, staging);

        /// <summary>
        /// The system a number names may lie in the block: its sector is one of the nine columns, or the next one out
        /// and its cube lies against the block's side, where a place on the very edge can be rounded across into it.
        /// A system any further out cannot be in the block (positions are never more than a fraction of a light year off).
        /// </summary>
        bool Reaches(long id64)
        {
            int mass = (int)(id64 & 7), bits = 7 - mass, last = (1 << bits) - 1;
            long rest = id64 >> 3;
            int cubeZ = (int)(rest & last), z = (int)((rest >> bits) & 127);
            rest >>= bits + 7 + bits + 6;
            int cubeX = (int)(rest & last), x = (int)((rest >> bits) & 127);
            return Along(x - cx, cubeX) && Along(z - cz, cubeZ);

            bool Along(int off, int cube) => off switch
            {
                >= -1 and <= 1 => true,
                -2 => cube == last,   // the next sector west (or south): its east (north) row of cubes
                2 => cube == 0,       // the next sector east (or north): its west (south) row
                _ => false,
            };
        }

        /// <summary>Where a sector's tallies are kept; -1 for a sector outside the nine columns or the grid.</summary>
        public int Slot(int x, int y, int z) =>
            Math.Abs(x - cx) > 1 || Math.Abs(z - cz) > 1 || x is < 0 or >= Galaxy.SectorsAcross || z is < 0 or >= Galaxy.SectorsAcross || y is < 0 or >= Galaxy.Layers
                ? -1
                : ((x - cx + 1) * Galaxy.Layers + y) * 3 + (z - cz + 1);

        public void Take(ReadOnlySpan<byte> raw)
        {
            int start = 0;
            if (!Opened && raw.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) start = 3;   // a byte-order mark before the first line
            while (start < raw.Length && IsBlank(raw[start])) start++;
            if (start == raw.Length) return;
            ReadOnlySpan<byte> text = raw[start..];
            if (text[0] != (byte)'{')
            {
                text = Trim(text);
                if (text.SequenceEqual("["u8) && !Opened) Opened = true;
                else if (text.SequenceEqual("]"u8) && Opened && !Closed) Closed = true;
                else throw new InvalidDataException("Not the list: a line is neither a system nor the list's start or end.");
                return;
            }
            if (!Opened || Closed) throw new InvalidDataException("Not the list: a system stands outside the list.");
            Lines++;

            // Most lines are far away, and their number says so: they are passed over without being read.
            if (text.StartsWith("{\"id64\":"u8) && Utf8Parser.TryParse(text[8..], out long quick, out _) && quick > 0 && !Reaches(quick)) return;

            text = Trim(text);
            if (text.Length > 0 && text[^1] == (byte)',') text = Trim(text[..^1]);
            if (!Listed.TryRead(text, out Listed system))
            {
                Unreadable++;
                return;
            }
            // Half-open, as the game's grid is: a system on a sector's near face is in it, one on its far face in the next.
            (int sx, int sy, int sz) = Galaxy.SectorOf(system.X, system.Y, system.Z);
            int slot = Slot(sx, sy, sz);
            if (system.Id64 > 0 && SectorOfAddress(system.Id64) is var (ax, ay, az) && Slot(ax, ay, az) is >= 0 and var home && home != slot)
                Strays.Add((home, CubeOf(system.Id64), NumberOf(system.Id64)));
            if (slot < 0) return;

            SectorTally sector = Sectors[slot] ??= new SectorTally(sx, sy, sz, Path.Combine(staging, $"sector-{sx}-{sy}-{sz}.jsonl"));
            sector.Add(system.X, system.Y, system.Z, system.Star);
            Write(sector.Output(), system);
            Kept++;
        }

        /// <summary>
        /// One system as a line of a sector file: ["name","id64",x,y,z,"s" or "n","yyyy-mm-dd" or null]. The name, the
        /// numbers and the date are copied as the list wrote them, byte for byte (escapes and all), so nothing in them
        /// is changed on the way: a coordinate reads back exactly as Spansh gave it.
        /// </summary>
        void Write(Stream to, scoped in Listed system)
        {
            line.ResetWrittenCount();
            Put("[\""u8);
            Put(system.Name);
            Put("\",\""u8);
            Utf8Formatter.TryFormat(system.Id64, line.GetSpan(20), out int digits);
            line.Advance(digits);
            Put("\","u8);
            Put(system.RawX);
            Put(","u8);
            Put(system.RawY);
            Put(","u8);
            Put(system.RawZ);
            Put(system.Star ? ",\"s\","u8 : ",\"n\","u8);
            if (system.Updated.IsEmpty) Put("null"u8);
            else
            {
                Put("\""u8);
                Put(system.Updated);
                Put("\""u8);
            }
            Put("]\n"u8);
            to.Write(line.WrittenSpan);

            void Put(scoped ReadOnlySpan<byte> bytes)
            {
                bytes.CopyTo(line.GetSpan(bytes.Length));
                line.Advance(bytes.Length);
            }
        }

        public void CloseAll()
        {
            foreach (SectorTally? sector in Sectors) sector?.Close();
        }
    }

    /// <summary>A sector of the block while it is built: its working file and its counts per cube.</summary>
    sealed class SectorTally(int x, int y, int z, string staged)
    {
        public readonly int X = x, Y = y, Z = z;
        public readonly string Staged = staged;
        public int Known, Star;
        // By the cube's place: layer by layer from the bottom, then south to north, then west to east.
        public readonly int[] Known160 = new int[8 * 8 * 8], Star160 = new int[8 * 8 * 8];
        public readonly int[] Known40 = new int[32 * 32 * 32], Star40 = new int[32 * 32 * 32];
        FileStream? output;

        public Stream Output() => output ??= new FileStream(Staged, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);

        public void Close()
        {
            output?.Dispose();
            output = null;
        }

        public void Add(double x, double y, double z, bool star)
        {
            Known++;
            int big = Index(x, y, z, CubeLy), small = Index(x, y, z, SmallCubeLy);
            Known160[big]++;
            Known40[small]++;
            if (!star) return;
            Star++;
            Star160[big]++;
            Star40[small]++;
        }

        /// <summary>The cube of the sector a point lies in, its near faces its own (the point is in the sector).</summary>
        public int Index(double x, double y, double z, int size)
        {
            Box box = Galaxy.SectorBounds(X, Y, Z);
            int across = (int)(Galaxy.SectorLy / size);
            int Along(double at, double from) => Math.Clamp((int)Math.Floor((at - from) / size), 0, across - 1);
            return (Along(y, box.Y0) * across + Along(z, box.Z0)) * across + Along(x, box.X0);
        }
    }

    /// <summary>
    /// The second pass, sector by sector: each working file packed into its sector file under its first line, and the
    /// gaps in each of the game's cubes' numbering worked out from the numbers (only one sector's numbers are held at
    /// a time). The files are written in the build's own folder; nothing is put in place yet.
    /// </summary>
    BlockFile Finish(Job job, Splitter split, FineSource list, string staging, CancellationToken stop)
    {
        DateTime built = DateTime.UtcNow;
        ILookup<int, (int Slot, long Cube, long Number)> strays = split.Strays.ToLookup(stray => stray.Slot);
        // Every sector that holds a system, or a number of one placed outside it, from the bottom up.
        int[] slots = [.. Enumerable.Range(0, split.Sectors.Length).Where(slot => split.Sectors[slot] is not null || strays.Contains(slot))
            .OrderBy(slot => slot / 3 % Galaxy.Layers).ThenBy(slot => slot % 3).ThenBy(slot => slot / (3 * Galaxy.Layers))];
        var sectors = new List<BlockSector>(slots.Length);
        long written = 0;
        foreach (int slot in slots)
        {
            stop.ThrowIfCancellationRequested();
            (int x, int y, int z) = (split.Sectors[slot] is { } at ? (at.X, at.Y, at.Z) : (job.Ix - 1 + slot / (3 * Galaxy.Layers), slot / 3 % Galaxy.Layers, job.Iz - 1 + slot % 3));
            SectorTally tally = split.Sectors[slot] ?? new SectorTally(x, y, z, "");
            var numbers = new Dictionary<long, (int Count, long Highest)>();
            void Count(long cube, long number)
            {
                numbers.TryGetValue(cube, out var seen);
                numbers[cube] = (seen.Count + 1, Math.Max(seen.Count == 0 ? -1 : seen.Highest, number));
            }

            if (split.Sectors[slot] is not null)
            {
                var heading = new SectorHeading([x, y, z], ProcGenNames.SectorName(x, y, z), tally.Known, built, list.Date, Columns);
                int lines = 0;
                using (var output = new FileStream(Path.Combine(staging, SectorName(x, y, z)), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                using (var packed = new GZipStream(output, Packing))
                using (var buffered = new BufferedStream(packed, 1 << 16))
                {
                    buffered.Write(JsonSerializer.SerializeToUtf8Bytes(heading, FileJson));
                    buffered.WriteByte((byte)'\n');
                    using var input = new FileStream(tally.Staged, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 16, FileOptions.SequentialScan);
                    ForEachLine(input, kept =>
                    {
                        if (kept.IsEmpty) return;
                        buffered.Write(kept);
                        buffered.WriteByte((byte)'\n');
                        lines++;
                        // Only the numbers this sector's cubes give out: a system numbered in another sector counts there (a stray).
                        if (IdOfKept(kept) is > 0 and var id64 && SectorOfAddress(id64) == (x, y, z)) Count(CubeOf(id64), NumberOf(id64));
                    });
                }
                if (lines != tally.Known) throw new IOException("A working file of the detailed map was cut short.");
                File.Delete(tally.Staged);
                written += lines;
            }
            foreach (var stray in strays[slot]) Count(stray.Cube, stray.Number);

            // The game numbers the systems of each of its cubes 0, 1, 2 and so on: every number below the highest known
            // that the list does not have is a system that must exist. Each is counted in the cube holding its own cube's middle.
            var missing160 = new long[Known160Length];
            var missing40 = new long[Known40Length];
            long missing = 0;
            foreach (var (cube, seen) in numbers)
            {
                long gaps = seen.Highest + 1 - seen.Count;   // a number listed twice would make this less than the truth, never more
                if (gaps <= 0 || SystemAddress.Place(cube) is not { } place) continue;
                (double mx, double my, double mz) = place.Centre;
                missing160[tally.Index(mx, my, mz, CubeLy)] += gaps;
                missing40[tally.Index(mx, my, mz, SmallCubeLy)] += gaps;
                missing += gaps;
            }
            sectors.Add(new BlockSector(x, y, z, ProcGenNames.SectorName(x, y, z), tally.Known, tally.Star, missing,
                Flat(8, tally.Known160, tally.Star160, missing160), Flat(32, tally.Known40, tally.Star40, missing40)));
            Report(job, Writing, ReadingShare + (99 - ReadingShare) * sectors.Count / slots.Length, split.Kept);
        }

        var block = new BlockFile(Version, job.Ix, job.Iz, built, Math.Round(job.Clock.Elapsed.TotalSeconds, 1), DownloadName, list.Bytes, list.Date,
            split.Lines, written, split.Unreadable, split.Strays.Count, [.. sectors])
        {
            UnpackSeconds = Math.Round(split.Unpacking.TotalSeconds, 1),
        };
        // Written beside its name and then moved there, so a working folder that holds a block file holds a whole build
        // (Resume puts such a folder in place), even after a Studio closed in the middle of writing it.
        string blockFile = Path.Combine(staging, BlockName(job.Ix, job.Iz));
        using (var output = new FileStream(blockFile + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            JsonSerializer.Serialize(output, block, FileJson);
        File.Move(blockFile + ".part", blockFile, overwrite: true);
        return block;
    }

    const int Known160Length = 8 * 8 * 8, Known40Length = 32 * 32 * 32;

    /// <summary>
    /// A sector's cubes that hold anything, six numbers each: i, j, k, known, star recorded, missing. Whole numbers of
    /// the ordinary size: a busy block has a quarter of a million of these cubes, kept in memory while the page looks.
    /// </summary>
    static int[] Flat(int across, int[] known, int[] star, long[] missing)
    {
        var flat = new List<int>();
        for (int index = 0; index < known.Length; index++)
            if (known[index] > 0 || missing[index] > 0)
            {
                flat.Add(index % across);
                flat.Add(index / (across * across));
                flat.Add(index / across % across);
                flat.Add(known[index]);
                flat.Add(star[index]);
                flat.Add((int)Math.Min(missing[index], int.MaxValue));
            }
        return [.. flat];
    }

    /// <summary>
    /// Puts a finished build in place (call under <see cref="placing"/>): the sector files first and the block file
    /// last, each in one move over the old one, then the files of sectors in these nine columns that the build has
    /// none of (the list no longer has anything in them). A page reading meanwhile finds every file whole, old or new.
    /// Only what is still in the working folder is moved, so a commit cut short is finished by doing it again.
    /// </summary>
    /// <param name="sectorFiles">The build's own sector files, by name (<see cref="FilesOf"/>).</param>
    /// <exception cref="IOException">A file could not be moved, after <see cref="MoveTries"/> tries: the rest is left where it is.</exception>
    void Commit(int cx, int cz, string staging, IReadOnlyCollection<string> sectorFiles)
    {
        Directory.CreateDirectory(folder);
        foreach (string name in sectorFiles)
        {
            string from = Path.Combine(staging, name);
            if (File.Exists(from)) Move(from, Path.Combine(folder, name));
        }
        Move(Path.Combine(staging, BlockName(cx, cz)), BlockPath(cx, cz));
        var own = new HashSet<string>(sectorFiles, StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(folder, "sector-*.jsonl.gz"))
            if (!own.Contains(Path.GetFileName(file)) && SectorOfFile(file) is { } sector && Math.Abs(sector.X - cx) <= 1 && Math.Abs(sector.Z - cz) <= 1)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
                {
                    Log.Error($"{Path.GetFileName(file)} of the detailed map is out of date and could not be deleted", problem);
                }
            }
    }

    /// <summary>The sector files a build wrote: those of its sectors with any system in them.</summary>
    static string[] FilesOf(BlockFile block) => [.. block.Sectors.Where(sector => sector.Known > 0).Select(sector => SectorName(sector.Ix, sector.Iy, sector.Iz))];

    /// <summary>
    /// One file moved over another. A virus scanner can hold a file it has just seen written for a moment, and a page
    /// may be reading the old one: so a refused move is tried again a few times, a little later each time. (This is a
    /// file on this PC, not a request to anybody.)
    /// </summary>
    void Move(string from, string to)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return;
            }
            catch (Exception problem) when ((problem is IOException or UnauthorizedAccessException) && attempt < MoveTries)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    /// <summary>
    /// Deletes the working folders builds leave behind when Studio is closed part way (one build runs at a time, so any
    /// there now is old), before a build of the block round (<paramref name="cx"/>, <paramref name="cz"/>). A finished
    /// build of another block still waiting to be put in place is kept for <see cref="Resume"/>; one of this block is
    /// not, as this build replaces it.
    /// </summary>
    void ClearStaging(int cx, int cz)
    {
        lock (placing)
        {
            if (!Directory.Exists(folder)) return;
            foreach (string old in Directory.GetDirectories(folder, "building-*"))
                if (Path.GetFileName(old).Equals(Path.GetFileName(StagingPath(cx, cz)), StringComparison.OrdinalIgnoreCase) || Staged(old) is null)
                    DeleteFolder(old);
        }
    }

    string StagingPath(int x, int z) => Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"building-{x}-{z}"));

    /// <summary>The block file of a finished build in a working folder ("building-ix-iz"); null when the folder holds no whole build of that block.</summary>
    static BlockFile? Staged(string staging)
    {
        string[] parts = Path.GetFileName(staging).Split('-');
        if (parts is not ["building", var x, var z] || !Whole(x, out int ix) || !Whole(z, out int iz)) return null;
        string file = Path.Combine(staging, BlockName(ix, iz));
        try
        {
            if (!File.Exists(file)) return null;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return JsonSerializer.Deserialize<BlockFile>(stream, FileJson) is { Version: Version, Sectors: not null } block && block.Ix == ix && block.Iz == iz ? block : null;
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts in place a finished build that could not all be moved in when it was built (a file held by another program
    /// for seconds on end), or that Studio was closed in the middle of moving: its working folder holds its block file,
    /// which a build writes last. Looked for at the first look at the detailed map of a session, and again after such a
    /// failure once <see cref="ResumeWait"/> has passed; never while a build runs (<paramref name="now"/>: the build
    /// itself, before it starts, and with no wait).
    /// </summary>
    void Resume(bool now = false)
    {
        lock (guard)
        {
            if (!resumeDue || (!now && (job is not null || DateTime.UtcNow < resumeAfter))) return;
            resumeDue = false;
        }
        if (!placing.TryEnter())
        {
            lock (guard) resumeDue = true;   // another thread is putting files in place just now: looked for next time
            return;
        }
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (string staging in Directory.GetDirectories(folder, "building-*"))
            {
                if (Staged(staging) is not { } block) continue;   // a build that never finished: the next build clears it
                Commit(block.Ix, block.Iz, staging, FilesOf(block));
                DeleteFolder(staging);
                Log.Info(string.Create(CultureInfo.InvariantCulture, $"3D map: the detailed map of {block.Ix},{block.Iz} built {block.Built:u} is now in place"));
                Progress?.Invoke(new FineMapProgress(string.Create(CultureInfo.InvariantCulture, $"f:{block.Ix}:{block.Iz}"), block.Ix, block.Iz, Done, 100, block.Systems, 0));
            }
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            Log.Error("A detailed map built earlier could not be put in place yet; Studio tries again in a while", held);
            lock (guard) (resumeDue, resumeAfter) = (true, DateTime.UtcNow + ResumeWait);
        }
        finally
        {
            placing.Exit();
        }
    }

    static void DeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error($"The detailed map's working folder {Path.GetFileName(path)} could not be deleted; the next build deletes it", problem);
        }
    }

    // ------------------------------------------------------------ the files

    static string SectorName(int x, int y, int z) => string.Create(CultureInfo.InvariantCulture, $"sector-{x}-{y}-{z}.jsonl.gz");

    static string BlockName(int x, int z) => string.Create(CultureInfo.InvariantCulture, $"block-{x}-{z}.json");

    string SectorPath(int x, int y, int z) => Path.Combine(folder, SectorName(x, y, z));

    string BlockPath(int x, int z) => Path.Combine(folder, BlockName(x, z));

    static (int X, int Y, int Z)? SectorOfFile(string file)
    {
        string[] parts = Path.GetFileName(file).Split('-', '.');
        return parts is ["sector", var x, var y, var z, "jsonl", "gz"] && Whole(x, out int ix) && Whole(y, out int iy) && Whole(z, out int iz) ? (ix, iy, iz) : null;
    }

    static (int X, int Z)? BlockOfFile(string file)
    {
        string[] parts = Path.GetFileName(file).Split('-', '.');
        return parts is ["block", var x, var z, "json"] && Whole(x, out int ix) && Whole(z, out int iz) ? (ix, iz) : null;
    }

    static bool Whole(string text, out int number) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    /// <summary>The newest block file whose nine columns hold this column; null when there is none.</summary>
    BlockFile? Covering(int ix, int iz)
    {
        if (!Directory.Exists(folder)) return null;
        var near = new List<(string File, DateTime Written)>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "block-*-*.json"))
                if (BlockOfFile(file) is { } at && Math.Abs(at.X - ix) <= 1 && Math.Abs(at.Z - iz) <= 1)
                    near.Add((file, File.GetLastWriteTimeUtc(file)));
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        foreach (var (file, _) in near.OrderByDescending(found => found.Written))
            if (Load(file) is { } block) return block;
        return null;
    }

    /// <summary>The newest block file of all, for the pace of the last build.</summary>
    BlockFile? Newest()
    {
        if (!Directory.Exists(folder)) return null;
        try
        {
            return Directory.EnumerateFiles(folder, "block-*-*.json").Where(file => BlockOfFile(file) is not null)
                .OrderByDescending(File.GetLastWriteTimeUtc).Select(Load).FirstOrDefault(block => block is not null);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A block file, read once and kept until it is written again (the block files of the busiest areas run to
    /// megabytes), or until it is the least recently used of more than <see cref="BlocksKept"/> (or of more than
    /// <see cref="BlockBytesKept"/> of them). Null when there is none, or it cannot be read; a file another program
    /// holds just now is tried again next time.
    /// </summary>
    BlockFile? Load(string file)
    {
        DateTime written;
        long bytes;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return null;
            (written, bytes) = (info.LastWriteTimeUtc, info.Length);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        lock (guard)
        {
            if (blocks.TryGetValue(file, out var kept) && kept.Written == written)
            {
                blocks[file] = (kept.Written, kept.Block, kept.Bytes, ++uses);
                return kept.Block;
            }
        }
        BlockFile? block = null;
        Interlocked.Increment(ref blockReads);
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16);
            block = JsonSerializer.Deserialize<BlockFile>(stream, FileJson);
            if (block is not { Version: Version, Sectors: not null }) throw new JsonException("Not a block file of this version");
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error($"{Path.GetFileName(file)} of the detailed map could not be read just now", problem);
            return null;
        }
        catch (JsonException problem)
        {
            // Remembered as unreadable until it is written again, so the log says it once.
            Log.Error($"{Path.GetFileName(file)} of the detailed map could not be read; build the detailed map again", problem);
            block = null;
        }
        lock (guard)
        {
            // A busy block takes some 25 MB once read: the least recently used go first, but the one just read stays.
            blocks[file] = (written, block, bytes, ++uses);
            while (blocks.Count > 1 && (blocks.Count > BlocksKept || blocks.Values.Sum(kept => kept.Bytes) > BlockBytesKept))
                blocks.Remove(blocks.MinBy(kept => kept.Value.Used).Key);
        }
        return block;
    }

    /// <summary>The first line of a sector file.</summary>
    /// <param name="Sector">The sector's numbers along x, y and z.</param>
    /// <param name="Systems">The lines that follow it: a file cut short is not taken for the whole.</param>
    /// <param name="Built">When the build that wrote it began writing (UTC).</param>
    /// <param name="SourceDate">The date of Spansh's list it was built from.</param>
    /// <param name="Columns">What the lines hold, in order (<see cref="FineMap.Columns"/>).</param>
    sealed record SectorHeading(int[] Sector, string? Name, int Systems, DateTime Built, DateTime SourceDate, string[] Columns);

    /// <summary>A block file (block-ix-iz.json).</summary>
    /// <param name="Seconds">How long the build took, for the time the next is said to take.</param>
    /// <param name="Source">The list's file name.</param>
    /// <param name="Lines">Systems in the whole list.</param>
    /// <param name="Systems">Systems of the block, in its sector files.</param>
    /// <param name="Unreadable">Lines of the list in or near the block that could not be read (passed over).</param>
    /// <param name="Strays">Systems numbered in another sector than the one they lie in.</param>
    /// <param name="Sectors">The sectors that hold any system or missing system, from the bottom up.</param>
    sealed record BlockFile(int Version, int Ix, int Iz, DateTime Built, double Seconds, string Source, long SourceBytes, DateTime SourceDate,
        long Lines, long Systems, long Unreadable, long Strays, BlockSector[] Sectors)
    {
        Dictionary<(int, int, int), BlockSector>? index;

        /// <summary>Of <see cref="Seconds"/>, the time spent unpacking the list; the rest went on reading its lines and writing the files.</summary>
        public double UnpackSeconds { get; init; }

        public BlockSector? Sector(int x, int y, int z) =>
            (index ??= Sectors.GroupBy(sector => (sector.Ix, sector.Iy, sector.Iz)).ToDictionary(group => group.Key, group => group.First())).GetValueOrDefault((x, y, z));
    }

    /// <summary>A sector of a block file.</summary>
    /// <param name="C160">Its cubes of 160 ly that hold anything, six numbers each: i, j, k, known, star recorded, missing (<see cref="Flat"/>).</param>
    /// <param name="C40">The same for its cubes of 40 ly.</param>
    sealed record BlockSector(int Ix, int Iy, int Iz, string? Name, int Known, int Star, long Missing, int[] C160, int[] C40);

    // ------------------------------------------------------------ reading lines

    /// <summary>
    /// A line of Spansh's list as read: {"id64":…,"name":"…","mainStar":"…","coords":{"x":…,"y":…,"z":…},"updateTime":"…"},
    /// the main star left out when the list does not know it, the fields in any order and others passed over. The name,
    /// the place's numbers and the day are kept as the list wrote them, to be copied into a sector file as they are.
    /// </summary>
    ref struct Listed
    {
        public long Id64;
        public double X, Y, Z;
        public bool Star;

        /// <summary>The name between its quotes as written, escapes and all.</summary>
        public ReadOnlySpan<byte> Name;

        /// <summary>The place's numbers as written.</summary>
        public ReadOnlySpan<byte> RawX, RawY, RawZ;

        /// <summary>The day of the update time (its first ten characters); empty when the list does not say.</summary>
        public ReadOnlySpan<byte> Updated;

        /// <summary>Reads a line; false for one that is not JSON, or has no name or no place.</summary>
        public static bool TryRead(ReadOnlySpan<byte> text, out Listed listed)
        {
            listed = default;
            listed.X = listed.Y = listed.Z = double.NaN;
            try
            {
                var reader = new Utf8JsonReader(text);
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("id64"u8))
                    {
                        reader.Read();
                        listed.Id64 = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long number) ? number
                            : reader.TokenType == JsonTokenType.String && Utf8Parser.TryParse(reader.ValueSpan, out long written, out int used) && used == reader.ValueSpan.Length ? written
                            : 0;
                    }
                    else if (reader.ValueTextEquals("name"u8))
                    {
                        reader.Read();
                        if (reader.TokenType == JsonTokenType.String) listed.Name = reader.ValueSpan;
                    }
                    else if (reader.ValueTextEquals("mainStar"u8))
                    {
                        reader.Read();
                        listed.Star = reader.TokenType == JsonTokenType.String && reader.ValueSpan.Length > 0;
                    }
                    else if (reader.ValueTextEquals("coords"u8))
                    {
                        reader.Read();
                        if (reader.TokenType != JsonTokenType.StartObject)
                        {
                            reader.Skip();
                            continue;
                        }
                        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                        {
                            bool x = reader.ValueTextEquals("x"u8), y = !x && reader.ValueTextEquals("y"u8), z = !x && !y && reader.ValueTextEquals("z"u8);
                            reader.Read();
                            if (!(x || y || z) || reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out double at))
                            {
                                reader.Skip();
                                continue;
                            }
                            if (x)
                            {
                                listed.X = at;
                                listed.RawX = reader.ValueSpan;
                            }
                            else if (y)
                            {
                                listed.Y = at;
                                listed.RawY = reader.ValueSpan;
                            }
                            else
                            {
                                listed.Z = at;
                                listed.RawZ = reader.ValueSpan;
                            }
                        }
                    }
                    else if (reader.ValueTextEquals("updateTime"u8))
                    {
                        reader.Read();
                        // A date has no escapes; one that had them could not be cut at ten characters.
                        if (reader.TokenType == JsonTokenType.String && !reader.ValueIsEscaped && reader.ValueSpan.Length >= 10) listed.Updated = reader.ValueSpan[..10];
                    }
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }
            }
            catch (Exception unreadable) when (unreadable is JsonException or InvalidOperationException)
            {
                return false;
            }
            return !Trim(listed.Name).IsEmpty && double.IsFinite(listed.X) && double.IsFinite(listed.Y) && double.IsFinite(listed.Z);
        }
    }

    /// <summary>A line of a sector file (<see cref="Columns"/>); null when it is not one.</summary>
    static FineSystem? ParseKept(ReadOnlySpan<byte> text)
    {
        try
        {
            var reader = new Utf8JsonReader(text);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return null;
            reader.Read();
            string name = reader.GetString() ?? "";
            reader.Read();
            long id64 = long.Parse(reader.GetString() ?? "", NumberStyles.None, CultureInfo.InvariantCulture);
            reader.Read();
            double x = reader.GetDouble();
            reader.Read();
            double y = reader.GetDouble();
            reader.Read();
            double z = reader.GetDouble();
            reader.Read();
            bool star = reader.ValueTextEquals("s"u8);
            reader.Read();
            string? updated = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            return new FineSystem(name, id64, x, y, z, star, updated);
        }
        catch (Exception unreadable) when (unreadable is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>The number of a line of a sector file, read no further than it; 0 when it cannot be read.</summary>
    static long IdOfKept(ReadOnlySpan<byte> text)
    {
        try
        {
            var reader = new Utf8JsonReader(text);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray || !reader.Read() || !reader.Read() || reader.TokenType != JsonTokenType.String) return 0;
            return Utf8Parser.TryParse(reader.ValueSpan, out long id64, out int used) && used == reader.ValueSpan.Length ? id64 : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
