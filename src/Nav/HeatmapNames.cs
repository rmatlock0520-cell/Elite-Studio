using System.Globalization;
using System.Text.Json.Serialization;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Nav;

/// <summary>A known system in the lists under the 3D map (<see cref="HeatmapNames"/>).</summary>
/// <param name="Name">The name as the list it comes from spells it.</param>
/// <param name="Id64">The game's own number for the system; 0 when the list gave none. Written as text, because these numbers can be larger than a web page can hold exactly.</param>
/// <param name="X">Where it is, in light years (with <paramref name="Y"/> and <paramref name="Z"/>).</param>
public sealed record ListedName(
    string Name,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Id64,
    double X, double Y, double Z);

/// <summary>
/// One of the game's own cubes in the list of a square (<see cref="HeatmapNames"/>): the systems known in it, and the
/// names to try. The game numbers the systems of each of its cubes 0, 1, 2 and so on with no gaps, so a number below
/// the highest known that no database has must exist, and the numbers after the highest may.
/// </summary>
/// <param name="Group">
/// The cube's part of its systems' names, all but the number: "Wredguia WD-K d8" for "Wredguia WD-K d8-65",
/// "Eephonth AA-A h" for "Eephonth AA-A h0", "Col 285 Sector VK-N c7" inside that nebula.
/// </param>
/// <param name="Mass">The cube's mass code, "a" (10 ly) to "h" (1,280 ly); "" for systems that could not be placed in a cube.</param>
/// <param name="Centre">The middle of the cube, in light years; null for systems that could not be placed in a cube.</param>
/// <param name="Count">Known systems of the cube in the square.</param>
/// <param name="Known">The numbers known in the cube, in short ("0–9, 11"), from everything the list knows of it, inside the square or not.</param>
/// <param name="Gaps">Full names for the numbers below the highest known that the list does not have: they must exist.</param>
/// <param name="Next">Full names for the three numbers after the highest known: they may exist.</param>
/// <param name="Names">The cube's known systems in the square, by number; null when the square holds too many names to send at once (they are sent for one cube at a time).</param>
public sealed record NameGroup(string Group, string Mass, double[]? Centre, int Count, string Known,
    IReadOnlyList<string> Gaps, IReadOnlyList<string> Next, IReadOnlyList<ListedName>? Names)
{
    /// <summary>
    /// All the numbers that must exist, of which <see cref="Gaps"/> may name only some (or none) when the square is busy:
    /// they come in full with the cube's own answer. 0 when the list may not know every system of the cube (a listing
    /// stopped part way, a cube reaching past the area listed), since a gap could then be a system known elsewhere.
    /// </summary>
    public long GapsTotal { get; init; }
}

/// <summary>The answer to GET /api/nav/heatmap/names (<see cref="HeatmapNames.Names"/>).</summary>
/// <param name="Source">
/// Where the names come from: "survey" (Spansh's listing of the sector or nebula, kept on this PC, which knows the
/// bodies too, so it wins where both exist) or "detailed" (the detailed map, built from Spansh's list of every
/// system on this PC). Null when neither has this square.
/// </param>
/// <param name="At">When that listing was made, or the detailed map built (UTC).</param>
/// <param name="Groups">
/// The game's cubes that hold any known system of the square, by their names; in a square with more than
/// <see cref="HeatmapNames.MostGroups"/> of them, only those nearest the point asked about (<see cref="Totals"/> has them all).
/// </param>
/// <param name="Truncated">The square holds more than <see cref="HeatmapNames.MostNames"/> names, so no cube's names were sent: ask for one cube at a time.</param>
/// <param name="Problem">Why the names could not be listed, in the player's words; null when they were.</param>
public sealed record NamesAnswer(string? Source, DateTime? At, IReadOnlyList<NameGroup> Groups, bool Truncated, string? Problem)
{
    /// <summary>For the detailed map: the date of Spansh's list it was built from.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? SourceDate { get; init; }

    /// <summary>The whole square, for the heading of its list: its cubes may not all be in <see cref="Groups"/>. Null for a square with nothing listed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NameTotals? Totals { get; init; }
}

/// <summary>A whole square, however many of its cubes one answer lists (<see cref="NamesAnswer.Totals"/>).</summary>
/// <param name="Groups">The game's cubes that hold any known system of the square.</param>
/// <param name="Count">Known systems in the square.</param>
/// <param name="Gaps">Numbers that must exist in those cubes, all told (<see cref="NameGroup.GapsTotal"/>).</param>
public sealed record NameTotals(int Groups, long Count, long Gaps);

/// <summary>What the names are listed for (GET /api/nav/heatmap/names).</summary>
/// <param name="Kind">"sector", "cube" or "nebula".</param>
/// <param name="Area">The sector or nebula whose survey kept on this PC may hold the names; for a cube, the area it was chosen in.</param>
/// <param name="Box">The square itself: the sector, the box round the nebula (as its survey has it), or the cube.</param>
public sealed record NameSquare(string Kind, SurveyArea Area, Box Box)
{
    public const string Sector = "sector", Cube = "cube", Nebula = "nebula";
}

/// <summary>
/// The names under the 3D map ("Systems in this square"): every system known in a sector, a cube or a nebula,
/// grouped by the game's own cube each belongs to, with the names a player can try in the galaxy map's search. A
/// player finds systems nobody has recorded by guessing names: when "… d8-9" is known, "… d8-10" may exist, and every
/// number below the highest known must. The names to try are built here from the cube's own known names (the same
/// sector, letters and mass code; only the number changes), so they are exactly what the game expects, nebula-named
/// systems included; where a cube's known systems all have catalogue names, by the game's rule (<see cref="ProcGenNames"/>).
///
/// The names come from what is on this PC, never from a request: the survey of the sector or nebula kept by
/// <see cref="HeatmapSurvey"/> (thin sectors), or the detailed map (<see cref="FineMap"/>, busy sectors). A busy sector
/// has up to some 600,000 names, so no answer sends more than <see cref="MostNames"/>: past that, the cubes come
/// without their names, and each cube's names come when the page asks for that cube. Nor does one answer list more
/// than <see cref="MostGroups"/> of the game's cubes: past that, those nearest the camera's focus, which the page asks
/// for again when the camera has moved on. The square listed last is kept in memory meanwhile (a busy sector's file
/// takes a second or two to read), for ten minutes after it was last used; a cube chosen inside it is taken from it,
/// not read again.
/// </summary>
public sealed class HeatmapNames
{
    /// <summary>Names one answer sends at most (known names and names to try together).</summary>
    public const int MostNames = 5000;

    /// <summary>
    /// The game's cubes one answer lists at most. A busy sector holds some 150,000 of them (Wredguia, in the detailed
    /// map built from Spansh's list of 14 October 2026: 152,384), some 20 MB of JSON, and a list nobody could read
    /// through: past this many, the answer lists those nearest the point the page asks about (the camera's focus).
    /// </summary>
    public const int MostGroups = 2000;

    /// <summary>The source words of <see cref="NamesAnswer.Source"/>.</summary>
    public const string FromSurvey = "survey", FromDetailed = "detailed";

    /// <summary>Numbers after the highest known that are offered to try.</summary>
    const int NextTried = 3;

    /// <summary>Runs of numbers in a cube's summary at most; a longer summary keeps its first ones and the last ("0–40, 42, … 3001–3050").</summary>
    const int MostRuns = 20;

    /// <summary>Names of a cube read for its name before it is named by the game's rule instead: its systems may all have catalogue names.</summary>
    const int NamesTriedForLabel = 8;

    /// <summary>
    /// Systems of the detailed map read for one square at most. The busiest sector has some 600,000, so a sector or a
    /// cube is always read; a big nebula over several busy sectors could take millions (and seconds, and hundreds of
    /// megabytes), so the player is asked to pick a sector or a cube of it instead.
    /// </summary>
    const long MostRead = 1_500_000;

    const string TooBig = "This nebula reaches over more systems in the detailed map than Studio lists at once. Pick a sector or a cube inside it to see their names.";

    /// <summary>The words for systems that could not be placed in any of the game's cubes (no number, and no generated name).</summary>
    const string Unplaced = "Other systems";

    const string Unreadable = "The detailed map of this area could not be read. Build it again (in the column on the left); nothing is fetched for it.";
    const string Held = "The names kept on this PC could not be read just now: another program may have the file open. Try again in a moment.";

    readonly HeatmapSurvey survey;
    readonly FineMap fine;
    readonly Lock guard = new();
    readonly Timer forget;
    Listing? last;   // the square read last
    Listing? part;   // the cube of it chosen last, taken from it

    /// <param name="survey">The surveys kept on this PC.</param>
    /// <param name="fine">The detailed map.</param>
    public HeatmapNames(HeatmapSurvey survey, FineMap fine)
    {
        (this.survey, this.fine) = (survey, fine);
        // The kept square can run to tens of megabytes: let go of it once the player has moved on.
        forget = new Timer(_ =>
        {
            lock (guard) (last, part) = (null, null);
        });
    }

    /// <summary>Names one answer sends at most; tests use fewer.</summary>
    internal int Most { get; init; } = MostNames;

    /// <summary>The game's cubes one answer lists at most; tests use fewer.</summary>
    internal int MostCubes { get; init; } = MostGroups;

    /// <summary>
    /// The known systems of a square by the game's cubes, with the names to try; with <paramref name="group"/>, that one
    /// cube in full (its names, all its gaps and the next numbers). Asks nobody: it reads what is kept on this PC, away
    /// from the calling thread (the server's, which is the UI's).
    /// </summary>
    /// <param name="near">
    /// Where the camera's focus is: a square with more than <see cref="MostGroups"/> of the game's cubes lists those
    /// nearest it (the square's middle when left out).
    /// </param>
    public async Task<NamesAnswer> Names(NameSquare square, string? group = null, (double X, double Y, double Z)? near = null)
    {
        Listing listing;
        try
        {
            // Even what comes before reading the names (the survey's heading, the detailed map's block files, which run
            // to megabytes when they are not in memory) is read off this thread: the calling one is the UI's.
            listing = await Task.Run(() => Load(square));
        }
        catch (InvalidDataException damaged)
        {
            Log.Error("The 3D map's names could not be read from the detailed map", damaged);
            return new NamesAnswer(FromDetailed, null, [], false, Unreadable);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error("The 3D map's names could not be read just now", problem);
            return new NamesAnswer(null, null, [], false, Held);
        }
        return group is null ? listing.Whole(Most, MostCubes, near) : listing.One(group, Most);
    }

    // ------------------------------------------------------------ what the names come from

    /// <summary>
    /// The square's names, from its survey kept on this PC when that is a complete listing, else from the detailed map
    /// when it covers every sector the square touches, else from a listing that stopped part way (names, but no numbers
    /// to try: the rest of the listing may hold them). The same square from the same source is not read twice, nor a
    /// cube inside the square read last.
    /// </summary>
    async Task<Listing> Load(NameSquare square)
    {
        SurveySummary? kept = survey.Summary(square.Area.Key);
        (int X, int Y, int Z)[] sectors = SectorsIn(square.Box);
        bool covered = sectors.Length > 0 && sectors.All(sector => fine.Covers(sector.X, sector.Z));
        string? source = kept is { State: SurveySummary.Complete } ? FromSurvey
            : covered ? FromDetailed
            : kept is { State: SurveySummary.Partial } ? FromSurvey
            : null;
        if (source is null) return Listing.Empty;

        // What says whether the square's names have changed since they were read: the survey's time, or the detailed map's.
        DateTime? at, sourceDate = null;
        if (source == FromSurvey) at = kept!.At;
        else
        {
            FineCubes[] built = [.. sectors.Select(sector => fine.Cubes(sector.X, sector.Y, sector.Z))];
            at = built.Max(cubes => cubes.At);
            sourceDate = built.Max(cubes => cubes.SourceDate);
            if (built.Sum(cubes => (long)cubes.Known) > MostRead) return Listing.Refused(source, at, sourceDate, TooBig);
        }
        Listing? around = null;
        lock (guard)
        {
            if ((last is { } known && known.Same(square, source, at) ? known : part is { } cut && cut.Same(square, source, at) ? cut : null) is { } listed)
            {
                forget.Change(TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
                return listed;
            }
            if (last is { } whole && whole.Holds(square, source, at)) around = whole;
        }
        if (around is not null)
        {
            // A cube of the square read last (a busy sector, then a cube of it): its systems, and every number of the game's
            // cubes it touches, are in memory already. Its file read again would take a second or two each time.
            Listing narrowed = around.Narrow(square);
            lock (guard) part = narrowed;
            forget.Change(TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
            return narrowed;
        }

        Listing read;
        if (source == FromSurvey)
        {
            if (await survey.View(square.Area.Key) is not { } view) return Listing.Empty;   // gone, or unreadable just now
            Box extent = square.Area.Box;
            bool complete = view.Complete;
            read = await Task.Run(() => Build(square, source, view.At, null,
                [new Batch(null, view.Systems.Select(system => new Found(system.Name, system.Id64, system.X, system.Y, system.Z)))],
                // A cube reaching past the area listed may have systems known outside it: its gaps are not certain.
                place => complete && Within(extent, place)));
        }
        else
        {
            var readSectors = new HashSet<(int X, int Y, int Z)>(sectors);
            read = await Task.Run(() => Build(square, source, at, sourceDate,
                sectors.Select(sector => new Batch(sector, fine.Systems(sector.X, sector.Y, sector.Z).Select(system => new Found(system.Name, system.Id64, system.X, system.Y, system.Z)))),
                // Spansh's whole list knows every system it has of a cube whose sector was read (a cube never spans two).
                place => readSectors.Contains((place.SectorX, place.SectorY, place.SectorZ))));
        }
        lock (guard) (last, part) = (read, null);
        forget.Change(TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
        return read;
    }

    /// <summary>The sectors of the grid a box reaches into, from the bottom up.</summary>
    static (int X, int Y, int Z)[] SectorsIn(Box box)
    {
        // The far faces belong to the next sector: a hair is taken off them.
        (int x0, int y0, int z0) = Galaxy.SectorOf(box.X0, box.Y0, box.Z0);
        (int x1, int y1, int z1) = Galaxy.SectorOf(box.X1 - 1e-6, box.Y1 - 1e-6, box.Z1 - 1e-6);
        var sectors = new List<(int X, int Y, int Z)>();
        for (int y = Math.Max(0, y0); y <= Math.Min(Galaxy.Layers - 1, y1); y++)
            for (int z = Math.Max(0, z0); z <= Math.Min(Galaxy.SectorsAcross - 1, z1); z++)
                for (int x = Math.Max(0, x0); x <= Math.Min(Galaxy.SectorsAcross - 1, x1); x++)
                    sectors.Add((x, y, z));
        return [.. sectors];
    }

    /// <summary>The game's cube lies wholly inside the box.</summary>
    static bool Within(Box box, CubePlace place)
    {
        (double x, double y, double z) = place.Corner;
        double side = place.CubeLy;
        return x >= box.X0 && y >= box.Y0 && z >= box.Z0 && x + side <= box.X1 && y + side <= box.Y1 && z + side <= box.Z1;
    }

    /// <summary>A system as the survey or the detailed map has it.</summary>
    readonly record struct Found(string Name, long Id64, double X, double Y, double Z);

    /// <summary>Systems read together: a sector of the detailed map, or a whole survey (no sector).</summary>
    sealed record Batch((int X, int Y, int Z)? Sector, IEnumerable<Found> Systems);

    // ------------------------------------------------------------ sorting into the game's cubes

    /// <summary>The address of system 0 of the game's cube a system's number belongs to: the same for every system of that cube.</summary>
    internal static long CubeOf(long id64) => id64 & ((1L << (44 - 3 * (int)(id64 & 7))) - 1);

    /// <summary>A system's number in its cube: the last number of a generated name.</summary>
    internal static long NumberOf(long id64)
    {
        int mass = (int)(id64 & 7);
        return (id64 >> (44 - 3 * mass)) & CubePlace.NumberMask(mass);
    }

    /// <summary>The sector a system's number says it belongs in (the layout is in <see cref="SystemAddress"/>), without making a record for each of a busy sector's systems.</summary>
    static (int X, int Y, int Z) SectorOf(long id64)
    {
        int bits = 7 - (int)(id64 & 7);
        long rest = id64 >> 3;
        int z = (int)((rest >> bits) & 127);
        rest >>= bits + 7;
        int y = (int)((rest >> bits) & 63);
        rest >>= bits + 6;
        int x = (int)((rest >> bits) & 127);
        return (x, y, z);
    }

    /// <summary>
    /// The square's systems sorted into the game's cubes, by their numbers (or, for a system the list gave no number,
    /// by its generated name). Every system read counts towards its cube's known numbers, inside the square or not;
    /// only those inside are listed. The numbers are held one sector at a time: a system numbered in another sector
    /// than the one it lies in (one on a sector's very edge) is kept aside until every sector has been read.
    /// </summary>
    static Listing Build(NameSquare square, string source, DateTime? at, DateTime? sourceDate, IEnumerable<Batch> batches, Func<CubePlace, bool> whole)
    {
        var groups = new Dictionary<long, Group>();
        var strays = new List<(long Cube, long Number)>();
        foreach (Batch batch in batches)
        {
            var numbers = new List<(long Cube, long Number)>();
            foreach (Found system in batch.Systems)
            {
                long cube = 0, number = -1;
                if (system.Id64 > 0) (cube, number) = (CubeOf(system.Id64), NumberOf(system.Id64));
                else if (ProcGenNames.Decode(system.Name) is { } decoded) (cube, number) = ((decoded.Cube with { Number = 0 }).Address, decoded.Cube.Number);
                if (cube != 0)
                {
                    if (batch.Sector is { } read && SectorOf(cube) != read) strays.Add((cube, number));
                    else numbers.Add((cube, number));
                }
                if (!square.Box.Contains(system.X, system.Y, system.Z)) continue;
                if (!groups.TryGetValue(cube, out Group? group)) groups[cube] = group = new Group(cube);
                group.Names.Add((new ListedName(system.Name, system.Id64, system.X, system.Y, system.Z), number < 0 ? long.MaxValue : number));
            }
            foreach (var (cube, number) in numbers)
                if (groups.TryGetValue(cube, out Group? group)) group.Numbers.Add(number);
        }
        foreach (var (cube, number) in strays)
            if (groups.TryGetValue(cube, out Group? group)) group.Numbers.Add(number);

        foreach (Group group in groups.Values) group.Finish(whole);
        Group[] sorted = [.. groups.Values.OrderBy(group => group.Cube == 0).ThenBy(group => group.Label, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.Cube)];
        return new Listing(square, source, at, sourceDate, sorted);
    }

    /// <summary>One of the game's cubes while a square is listed.</summary>
    sealed class Group(long cube)
    {
        /// <summary>The address of system 0 of the cube; 0 for the systems that could not be placed in one.</summary>
        public readonly long Cube = cube;

        /// <summary>The cube's systems in the square, with their numbers (long.MaxValue for none).</summary>
        public readonly List<(ListedName Name, long Number)> Names = [];

        /// <summary>Every number the list knows in the cube, inside the square or not.</summary>
        public HashSet<long> Numbers { get; private init; } = [];

        public CubePlace? Place;
        public string Label = Unplaced;

        /// <summary>The name of the cube's system with a number; null where no name can be given (the gaps are then counted, not named).</summary>
        public Func<long, string>? Namer;

        /// <summary>The list knows every system of the cube that it knows at all, so the numbers it lacks must exist.</summary>
        public bool Whole;

        public long Highest = -1;
        public string Known = "";

        /// <summary>Numbers below the highest known that the list does not have; 0 when the cube is not <see cref="Whole"/>.</summary>
        public long GapsTotal => Whole ? Highest + 1 - Numbers.Count : 0;

        /// <summary>The next numbers to try, after the highest known, as far as the cube's numbers go.</summary>
        public IEnumerable<long> NextNumbers => Whole && Place is { } place && Highest >= 0
            ? Enumerable.Range(1, NextTried).Select(step => Highest + step).Where(number => number <= CubePlace.NumberMask(place.Mass))
            : [];

        public void Finish(Func<CubePlace, bool> whole)
        {
            Names.Sort((a, b) => a.Number != b.Number ? a.Number.CompareTo(b.Number) : string.Compare(a.Name.Name, b.Name.Name, StringComparison.OrdinalIgnoreCase));
            if (Cube == 0 || SystemAddress.Place(Cube) is not { } place) return;
            Place = place;
            Whole = whole(place);
            Highest = Numbers.Count == 0 ? -1 : Numbers.Max();
            Known = Summary(Numbers);
            (Label, Namer) = NameOf(place, Names.Select(name => name.Name.Name));
        }

        /// <summary>
        /// The same cube in a smaller square: only the names inside it, and all that is known of the cube (its numbers,
        /// shared, never changed once finished; its name and whether the list knows all of it). Null when none is inside.
        /// </summary>
        public Group? Inside(Box box)
        {
            var copy = new Group(Cube) { Numbers = Numbers, Place = Place, Label = Label, Namer = Namer, Whole = Whole, Highest = Highest, Known = Known };
            foreach (var named in Names)
                if (box.Contains(named.Name.X, named.Name.Y, named.Name.Z)) copy.Names.Add(named);
            return copy.Names.Count > 0 ? copy : null;
        }
    }

    /// <summary>
    /// How the systems of one of the game's cubes are named: as a known system of the cube with a generated name is,
    /// the number left off ("Wredguia WD-K d8" of "Wredguia WD-K d8-65"). When the names read are all catalogue names,
    /// by the game's rule: after the nebula the cube's middle lies in (the smallest, as EDTS has it), else after the
    /// grid's sector. Where neither gives a name, the cube's letters and its sector's numbers, and no names to try.
    /// Within reach names the systems that must exist this same way (<see cref="HeatmapReach"/>).
    /// </summary>
    internal static (string Label, Func<long, string>? Namer) NameOf(CubePlace zero, IEnumerable<string> known)
    {
        long address = zero.Address;
        foreach (string name in known.Take(NamesTriedForLabel))
            if (ProcGenNames.Decode(name) is { } decoded && (decoded.Cube with { Number = 0 }).Address == address)
                return Numbered(decoded.Name);
        (double x, double y, double z) = zero.Centre;
        string? ruled = HaRegions.Containing(x, y, z).Select(nebula => ProcGenNames.SystemName(zero, nebula.Name)).FirstOrDefault(name => name is not null)
            ?? ProcGenNames.SystemName(zero);
        if (ruled is not null) return Numbered(ruled);
        return (string.Create(CultureInfo.InvariantCulture, $"{Galaxy.CubeName(null, (zero.CubeX, zero.CubeY, zero.CubeZ), zero.Mass)} in sector {zero.SectorX},{zero.SectorY},{zero.SectorZ}"), null);

        // The number is always last, after a dash when the cube's part ends in a digit (Galaxy.SystemName's rule).
        static (string, Func<long, string>) Numbered(string name)
        {
            string stem = name.TrimEnd("0123456789".ToCharArray());
            string label = stem.EndsWith('-') ? stem[..^1] : stem;
            return (label, number => stem + number.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Numbers in short, the way a player reads them: runs of three or more as "0–9", others one by one ("0–9, 11, 12").
    /// Past <see cref="MostRuns"/> runs, the first ones, "…" and the last, so the highest is always there. No thousands
    /// separators: the page reads the numbers back out of it.
    /// </summary>
    static string Summary(IEnumerable<long> numbers)
    {
        long[] sorted = [.. numbers.Order()];
        var runs = new List<string>();
        for (int at = 0; at < sorted.Length;)
        {
            int end = at;
            while (end + 1 < sorted.Length && sorted[end + 1] == sorted[end] + 1) end++;
            if (end - at >= 2) runs.Add(string.Create(CultureInfo.InvariantCulture, $"{sorted[at]}–{sorted[end]}"));
            else
                for (int one = at; one <= end; one++) runs.Add(sorted[one].ToString(CultureInfo.InvariantCulture));
            at = end + 1;
        }
        if (runs.Count > MostRuns) runs = [.. runs.Take(MostRuns - 1), "…", runs[^1]];
        return string.Join(", ", runs);
    }

    // ------------------------------------------------------------ the answers

    /// <summary>A square sorted into the game's cubes, kept for the answers about it.</summary>
    sealed class Listing(NameSquare? square, string? source, DateTime? at, DateTime? sourceDate, IReadOnlyList<Group> groups)
    {
        public static readonly Listing Empty = new(null, null, null, null, []);

        string? problem;

        /// <summary>A square that is not listed, and why, in the player's words.</summary>
        public static Listing Refused(string? source, DateTime? at, DateTime? sourceDate, string why) => new(null, source, at, sourceDate, []) { problem = why };

        NameTotals? totals;

        public NameSquare? Square => square;
        public string? Source => source;
        public DateTime? At => at;

        /// <summary>This is the listing of that square, from that source as it stands.</summary>
        public bool Same(NameSquare asked, string from, DateTime? when) => square == asked && source == from && at == when;

        /// <summary>
        /// A cube can be taken from this listing (<see cref="Narrow"/>): this is a whole sector or nebula, listed from the
        /// same source as it stands, that holds the cube; so every system read for the cube is here, and every number of
        /// the game's cubes it touches.
        /// </summary>
        public bool Holds(NameSquare cube, string from, DateTime? when) =>
            problem is null && square is { Kind: not NameSquare.Cube } whole && cube.Kind == NameSquare.Cube && whole.Area.Key == cube.Area.Key
            && source == from && at == when
            && cube.Box.X0 >= whole.Box.X0 && cube.Box.Y0 >= whole.Box.Y0 && cube.Box.Z0 >= whole.Box.Z0
            && cube.Box.X1 <= whole.Box.X1 && cube.Box.Y1 <= whole.Box.Y1 && cube.Box.Z1 <= whole.Box.Z1;

        /// <summary>A cube of this square (<see cref="Holds"/>): the names inside it, the rest as this listing knows it.</summary>
        public Listing Narrow(NameSquare cube) => new(cube, source, at, sourceDate, [.. groups.Select(group => group.Inside(cube.Box)).OfType<Group>()]);

        /// <summary>
        /// The cubes nearest <paramref name="near"/> (the square's middle when it is not given) up to
        /// <paramref name="mostCubes"/>, with their names and the names to try when all of those come to no more than
        /// <paramref name="most"/>. Past that, no cube's names (the page asks for one cube at a time), but still the
        /// names to try when those alone come to no more than <paramref name="most"/>; past that too, only how many must
        /// exist. The totals are always the whole square's.
        /// </summary>
        public NamesAnswer Whole(int most, int mostCubes, (double X, double Y, double Z)? near)
        {
            if (problem is not null) return new NamesAnswer(source, at, [], false, problem) { SourceDate = sourceDate };
            IReadOnlyList<Group> sent = groups;
            if (groups.Count > mostCubes && square is { } whole)
            {
                (double X, double Y, double Z) from = near ?? whole.Box.Centre;
                // The systems placed in no cube are one group, listed whatever its distance; the rest by their middles.
                var nearest = new HashSet<Group>(groups.OrderBy(group => group.Place is { } place ? Far(place.Centre, from) : -1).Take(mostCubes));
                sent = [.. groups.Where(nearest.Contains)];
            }
            long names = sent.Sum(group => (long)group.Names.Count);
            long tries = sent.Sum(group => group.Namer is null ? 0 : group.GapsTotal + group.NextNumbers.Count());
            bool all = names + tries <= most;
            bool tried = tries <= most;
            totals ??= groups.Count == 0 ? null : new NameTotals(groups.Count, groups.Sum(group => (long)group.Names.Count), groups.Sum(group => group.GapsTotal));
            return new NamesAnswer(source, at, [.. sent.Select(group => Shape(group, withNames: all, withTries: tried, most))], !all, null)
            {
                SourceDate = sourceDate,
                Totals = totals,
            };

            static double Far((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
                (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);
        }

        /// <summary>One cube of the square in full: all its numbers to try, and as many of its names as fit beside them.</summary>
        public NamesAnswer One(string name, int most)
        {
            if (problem is not null) return new NamesAnswer(source, at, [], false, problem) { SourceDate = sourceDate };
            name = name.Trim();
            Group? group = groups.FirstOrDefault(found => found.Label.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (group is null)
                return new NamesAnswer(source, at, [], false, source is null ? null : "No cube by that name is listed in this square.") { SourceDate = sourceDate };
            NameGroup tries = Shape(group, withNames: false, withTries: true, most);
            int room = Math.Max(0, most - tries.Gaps.Count - tries.Next.Count);
            return new NamesAnswer(source, at, [tries with { Names = [.. group.Names.Take(room).Select(named => named.Name)] }], group.Names.Count > room, null) { SourceDate = sourceDate };
        }

        static NameGroup Shape(Group group, bool withNames, bool withTries, int most)
        {
            var gaps = new List<string>();
            var next = new List<string>();
            if (withTries && group.Namer is { } name)
            {
                if (group.GapsTotal > 0)
                    for (long number = 0; number < group.Highest && gaps.Count < most; number++)
                        if (!group.Numbers.Contains(number)) gaps.Add(name(number));
                next.AddRange(group.NextNumbers.Select(name));
            }
            CubePlace? place = group.Place;
            double[]? centre = place is null ? null : [place.Centre.X, place.Centre.Y, place.Centre.Z];
            return new NameGroup(group.Label, place is null ? "" : ((char)('a' + place.Mass)).ToString(), centre, group.Names.Count, group.Known,
                gaps, next, withNames ? [.. group.Names.Select(named => named.Name)] : null)
            {
                GapsTotal = group.GapsTotal,
            };
        }
    }
}
