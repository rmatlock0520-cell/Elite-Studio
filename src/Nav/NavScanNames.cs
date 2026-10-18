using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EliteStudio.Nav;

/// <summary>A word Windows' text reader found on a picture, and where: its box in the picture's own pixels.</summary>
/// <param name="X">The box's left edge (with <paramref name="Y"/> its top edge).</param>
public sealed record ReadWord(string Text, double X, double Y, double Width, double Height)
{
    public double CentreX => X + Width / 2;
    public double CentreY => Y + Height / 2;
}

/// <summary>A line of words as Windows' text reader put them together.</summary>
public sealed record ReadLine(IReadOnlyList<ReadWord> Words)
{
    public string Text => string.Join(' ', Words.Select(word => word.Text));
}

/// <summary>What Windows' text reader made of a picture (<see cref="NavScanOcr"/>): every line of every look at it, in the picture's own pixels.</summary>
/// <param name="Problem">Why the picture could not be read, in the player's words; null when it was.</param>
public sealed record PictureText(IReadOnlyList<ReadLine> Lines, string? Problem = null);

/// <summary>A generated system name read off a picture (<see cref="NavScanNames.Read"/>).</summary>
/// <param name="Read">The line as read, tidied ("COL 285 SECTOR WO-J A3B-O").</param>
/// <param name="DistanceLy">The distance the Navigation panel gave on the same row, in light years; null when none was read.</param>
/// <param name="Variants">
/// The names it may be, each decoded into its cube (and so its number and place), and each where it may lie: one when
/// the reading is clear, more when letters the reader mixes up (Q, G and O) leave a choice.
/// </param>
public sealed record NameRead(string Read, double? DistanceLy, IReadOnlyList<DecodedName> Variants);

/// <summary>A catalogue's name read off a picture ("HIP 12766"), whose place is not in its name.</summary>
/// <param name="Name">As read, tidied: capitals, one space between words, misread digits put right.</param>
/// <param name="DistanceLy">The distance read on the same row; null when none was.</param>
public sealed record CatalogueRead(string Name, double? DistanceLy);

/// <summary>The system names read off one picture, each once.</summary>
public sealed record PictureNames(IReadOnlyList<NameRead> Names, IReadOnlyList<CatalogueRead> Catalogue)
{
    /// <summary>
    /// Rows with a generated name that could not be made out (<see cref="NavScanNames.MostReadings"/>,
    /// <see cref="NavScanNames.MostSpellings"/>): not checked, but counted, so the page and the overlay can say a name
    /// on the picture could not be read rather than pass over it without a word.
    /// </summary>
    public int Unreadable { get; init; }
}

/// <summary>
/// Nav scan's reading of system names off what Windows' text reader saw on a picture of the game's Navigation panel (or
/// of its galaxy map). The reader mixes up letters and digits that look alike, glues the panel's "SYSTEM" onto the
/// name above it, and reads the same row many times (the picture is read whole and in pieces, at two sizes), so:
/// - a generated name ("Col 285 Sector WO-J a38-0") is put right by where each part stands: the two letters, the dash
///   and the letter after it take letters (0 is O, 8 is B, 1 is I, 5 is S, 2 is Z), the mass code is a to h (8 is b),
///   and the numbers take digits (O is 0, B is 8, I and L are 1, S is 5, Z is 2, G is 6 or 9). Q, G and O look so alike
///   that each gives the others as well. Every spelling is decoded (<see cref="ProcGenNames.Decode"/>), which also says
///   where it lies; a spelling that names no cube is no name;
/// - a spelling is kept only where it can be: when the panel gave its distance on the row ("33.2 LY"), its cube must be
///   that far from the commander, give or take half the cube's diagonal and a light year; a row of the Navigation panel
///   without one (the panel writes one on every row, but the reader misses some), within the panel's
///   <see cref="PanelLy"/>; any other row, within <see cref="AnywhereLy"/>: the galaxy map shows light years too (the
///   selected system's distance, a route's length), but beside no star's name. A row is the panel's when the picture
///   has the panel's column of distances (<see cref="Column"/>), or when a row next to it has its distance beside it;
/// - the lines of all the looks are gathered into the picture's rows (the same height along the panel's tilt, starting
///   at the same place), and each row is one name: a spelling read in two looks or more that most looks agree on, a
///   slip of one look (a 3 read for an 8 gives another system of the same cube, which no distance can tell apart) left
///   out; with no such spelling, the row's spellings together, unsure. Looks that read a row alike count as one reading
///   however many systems their letters may be (Q, G and O give each other): a row read in more than
///   <see cref="MostReadings"/> different ways, or that may be more than <see cref="MostSpellings"/> systems, is
///   unreadable, not checked, and counted (<see cref="PictureNames.Unreadable"/>);
/// - a catalogue's name ("HIP 12766", "Gliese 110") says nothing of where it lies: it is passed on as read, for the
///   detailed map to say whether it knows it;
/// - stations, nav beacons, carriers and the panel's own words are no system names, and are passed over.
/// Asks nobody anything, and touches no file: pure reading, so the tests can give it the reader's real misreads.
/// </summary>
public static partial class NavScanNames
{
    /// <summary>Light years from the commander a name read without a distance may lie when it is not a row of the Navigation panel: a galaxy map picture shows far more than one jump.</summary>
    public const double AnywhereLy = 3000;

    /// <summary>Light years from the commander the Navigation panel lists systems: a row of it read without its distance lies within this, give or take half its cube's diagonal.</summary>
    public const double PanelLy = 60;

    /// <summary>
    /// Different readings of one row at most, when no reading wins the looks' vote: a row the looks read in more ways is
    /// unreadable, and not checked. Looks that read the row alike are one reading, however many systems its letters
    /// may be (a clear "GO-Q" may be nine systems or more: Q, G and O give each other).
    /// </summary>
    public const int MostReadings = 8;

    /// <summary>Systems one row may be at most, all its readings together: as many as one request to Spansh asks about. A row that may be more is unreadable, and not checked.</summary>
    public const int MostSpellings = Spansh.MostNames;

    /// <summary>Rows of the panel's column of distances at least (<see cref="Column"/>): one or two figures are what a galaxy map shows too.</summary>
    const int ColumnRows = 3;

    /// <summary>Light years a cube may be off the distance the panel gave, past half its diagonal: the panel rounds, and the commander's place is a star's, not the panel's.</summary>
    public const double DistanceSlackLy = 1;

    /// <summary>Words of a sector name at most ("Col 285 Sector" is three): an icon read as a letter before it is tried without.</summary>
    const int MostSectorWords = 5;

    /// <summary>
    /// The system names on a picture: one for each row with a generated name, with the spellings it may be (only those
    /// that can be where they are said to be, and of a row read differently by different looks only the one most looks
    /// agree on), top to bottom; and catalogue names as read, each once. <paramref name="here"/> is where the commander
    /// was when the picture was taken; while Studio does not know, every spelling that decodes is kept.
    /// </summary>
    public static PictureNames Read(PictureText text, (double X, double Y, double Z)? here)
    {
        IReadOnlyList<Mark> marks = Distances(text.Lines);
        double slope = Slope(text.Lines);
        // The Navigation panel writes a distance on every row, in a column: a picture with that column is the panel's,
        // every row of it. The galaxy map shows a light-year figure or two as well (the selected system's distance, a
        // route's length), never beside a star's name: a picture without the column is judged row by row (below).
        bool column = Column(marks);

        // Every line of every look with a generated name, with the distance on its row and where its row is; the lines
        // that are a sector's name and nothing else, with where they stand; the catalogue names; and where the lines
        // with a distance beside them are, of either kind (the panel's rows).
        var readings = new List<Reading>();
        var sectorLines = new List<(string Sector, ReadLine Line)>();
        var wholeSectors = new Dictionary<string, string?>(StringComparer.Ordinal);   // each line's text once
        var catalogue = new Dictionary<string, CatalogueRead>(StringComparer.Ordinal);
        var besideDistance = new List<(double Level, double Left)>();
        foreach (ReadLine line in text.Lines)
        {
            if (line.Words.Count == 0) continue;
            (string clean, double? sameLine) = Tidy(line.Text);
            if (clean.Length == 0) continue;
            // A sector's name alone first: "SHUI WE-I SECTOR" has a generated name's ending to the eye of a pattern
            // ("WE-I S" and a number of letters), which spells no name.
            if (!wholeSectors.TryGetValue(clean, out string? sector)) wholeSectors[clean] = sector = WholeSector(clean);
            if (sector is not null)
            {
                sectorLines.Add((sector, line));
                continue;
            }
            Parts? parts = Procedural(clean);
            string? named = parts is null ? Catalogue(clean) : null;
            if (parts is null && named is null) continue;
            double? ly = sameLine ?? DistanceBeside(line, marks, slope);
            if (ly is not null) besideDistance.Add((Level(line, slope), line.Words[0].X));
            if (parts is not null) readings.Add(new Reading(clean, ly, parts, line, Level(line, slope), line.Words[0].X, line.Words.Average(word => word.Height)));
            else if (!catalogue.TryGetValue(named!, out CatalogueRead? before) || (before.DistanceLy is null && ly is not null))
                catalogue[named!] = new CatalogueRead(named!, ly);
        }

        List<List<Reading>> rows = Rows(readings);
        // Each row's distance: the one most of its looks found beside it. And whether it is a row of the panel: on a
        // picture with the panel's column, every row is; else a row with a distance read beside it, or with one beside
        // a row next to it (within two of the panel's rows, starting where it starts: the reader misses a figure now and
        // then). A star's name on the galaxy map has none beside it, nor beside its neighbours.
        var rowLy = new Dictionary<List<Reading>, double?>();
        var rowPanel = new Dictionary<List<Reading>, bool>();
        foreach (List<Reading> row in rows)
        {
            rowLy[row] = row.Where(reading => reading.Ly is not null).GroupBy(reading => reading.Ly).OrderByDescending(same => same.Count()).Select(same => same.Key).FirstOrDefault();
            double level = row.Average(reading => reading.Level), height = row.Average(reading => reading.Height);
            rowPanel[row] = column || rowLy[row] is not null
                || besideDistance.Any(other => Math.Abs(other.Level - level) <= NeighbourRows * height && Math.Abs(other.Left - row[0].Left) <= 4 * height);
        }

        // The spellings of each line with its sector, then of each without (whose sector comes from the line above it,
        // else from the sectors the picture names, those of the names read whole included).
        var sectors = new HashSet<string>(sectorLines.Select(line => line.Sector), StringComparer.OrdinalIgnoreCase);
        foreach (List<Reading> row in rows)
            foreach (Reading reading in row.Where(reading => reading.Parts.Sector.Length > 0))
            {
                reading.Variants = Plausible(Spellings(reading.Parts, []), here, rowLy[row], rowPanel[row]);
                foreach (DecodedName variant in reading.Variants) sectors.Add(variant.Sector);
            }
        // The panel's header writes the commander's own system over two lines, the sector on the first ("SHUI WEI
        // SECTOR", then "VT-R B4-5 SYSTEM").
        foreach (List<Reading> row in rows)
            foreach (Reading reading in row.Where(reading => reading.Parts.Sector.Length == 0))
            {
                IReadOnlyCollection<string> above = Above(reading.Line, sectorLines);
                reading.Variants = Plausible(Spellings(reading.Parts, above.Count > 0 ? above : sectors), here, rowLy[row], rowPanel[row]);
            }

        var names = new List<NameRead>();
        int unreadable = 0;
        foreach (List<Reading> row in rows)
        {
            (IReadOnlyList<DecodedName>? chosen, bool unclear) = Voted(row);
            if (unclear) unreadable++;
            if (chosen is null) continue;
            // The row as most of the looks behind the name read it.
            string read = row.Where(reading => reading.Variants.Any(variant => chosen.Any(kept => kept.Cube.Address == variant.Cube.Address)))
                .GroupBy(reading => reading.Clean, StringComparer.Ordinal).OrderByDescending(same => same.Count()).First().Key;
            names.Add(new NameRead(read, rowLy[row], chosen));
        }
        return new PictureNames(names, [.. catalogue.Values]) { Unreadable = unreadable };
    }

    /// <summary>
    /// Line heights up or down a row of the panel without its distance looks for one with its distance (<see cref="Read"/>):
    /// the panel's rows are a little over two heights apart, so this reaches the row above and below, and the one past each.
    /// </summary>
    const double NeighbourRows = 5;

    /// <summary>A line of one look with a generated name, and where its row is (<see cref="Rows"/>).</summary>
    /// <param name="Level">Where the line's row crosses the picture's left edge, along the panel's tilt: the same for every look at the row.</param>
    /// <param name="Left">Where the line starts.</param>
    sealed record Reading(string Clean, double? Ly, Parts Parts, ReadLine Line, double Level, double Left, double Height)
    {
        /// <summary>The systems it may be (where they can be), each once.</summary>
        public IReadOnlyList<DecodedName> Variants { get; set; } = [];
    }

    /// <summary>
    /// Where a line's row crosses the picture's left edge, followed along the picture's slope: the middle of its words'
    /// (an icon read as a word, taller than the letters, does not move it).
    /// </summary>
    static double Level(ReadLine line, double slope)
    {
        double[] levels = [.. line.Words.Select(word => word.CentreY - slope * word.CentreX).Order()];
        return levels.Length % 2 == 1 ? levels[levels.Length / 2] : (levels[levels.Length / 2 - 1] + levels[levels.Length / 2]) / 2;
    }

    /// <summary>
    /// The lines of all the looks gathered into the picture's rows: the same row is at the same level along the tilt
    /// (within half a line's height: the panel's rows are two heights and more apart) and starts at the same place
    /// (within four heights: an icon read as a letter before it moves its start; a label elsewhere on a galaxy map at
    /// the same height is another row). Top to bottom.
    /// </summary>
    static List<List<Reading>> Rows(List<Reading> readings)
    {
        var rows = new List<List<Reading>>();
        foreach (Reading reading in readings.OrderBy(reading => reading.Level))
        {
            List<Reading>? row = rows.LastOrDefault(row => Math.Abs(row.Average(other => other.Level) - reading.Level) <= reading.Height / 2
                && Math.Abs(row[0].Left - reading.Left) <= 4 * reading.Height);
            if (row is null) rows.Add(row = []);
            row.Add(reading);
        }
        return rows;
    }

    /// <summary>
    /// The name a row is, from what its looks read (each look's line votes for each system it may be): the one read in
    /// two looks or more that has more votes than any other, alone, the others the slips of a look or two; with none
    /// such, every spelling the row may be, unsure, the slips of a single look left out when some were read more often.
    /// Chosen is null for a row that is no name, and for one that is unreadable (Unreadable): its looks read it in more
    /// than <see cref="MostReadings"/> ways, or it may be more than <see cref="MostSpellings"/> systems. Looks that read
    /// the row alike are one reading, whatever choice its letters leave: a "GO-Q" every look read so may be nine systems
    /// of its sector (Q, G and O give each other), which tie in every vote, and is as clear a reading as the reader gives.
    /// </summary>
    static (IReadOnlyList<DecodedName>? Chosen, bool Unreadable) Voted(List<Reading> row)
    {
        var votes = new Dictionary<long, (DecodedName Name, int Count, int First)>();
        foreach (Reading reading in row)
            foreach (DecodedName variant in reading.Variants)
                votes[variant.Cube.Address] = votes.TryGetValue(variant.Cube.Address, out var seen) ? (seen.Name, seen.Count + 1, seen.First) : (variant, 1, votes.Count);
        if (votes.Count == 0) return (null, false);
        var ranked = votes.Values.OrderByDescending(vote => vote.Count).ThenBy(vote => vote.First).ToList();
        if (ranked[0].Count >= 2 && (ranked.Count == 1 || ranked[0].Count > ranked[1].Count)) return ([ranked[0].Name], false);
        List<DecodedName> spellings = [.. ranked.Where(vote => ranked[0].Count == 1 || vote.Count >= 2).OrderBy(vote => vote.First).Select(vote => vote.Name)];
        // The different ways the looks behind those spellings read the row: each look's systems, as a set.
        var kept = new HashSet<long>(spellings.Select(name => name.Cube.Address));
        int readings = row.Where(reading => reading.Variants.Any(variant => kept.Contains(variant.Cube.Address)))
            .Select(reading => string.Join(',', reading.Variants.Select(variant => variant.Cube.Address).Order()))
            .Distinct(StringComparer.Ordinal).Count();
        return readings <= MostReadings && spellings.Count <= MostSpellings ? (spellings, false) : (null, true);
    }

    /// <summary>A tidied line that is a sector's name, all of it ("SHUI WE-I SECTOR"), as a sector is spelt; null for any other.</summary>
    static string? WholeSector(string clean)
    {
        int words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return words > MostSectorWords ? null : Sectors(clean).FirstOrDefault(sector => sector.Split(' ').Length == words);
    }

    /// <summary>The sectors on lines just above this one (at most a line and a half up), starting where it starts.</summary>
    static IReadOnlyCollection<string> Above(ReadLine line, IReadOnlyList<(string Sector, ReadLine Line)> sectorLines)
    {
        double top = line.Words.Min(word => word.Y), left = line.Words[0].X, height = line.Words.Average(word => word.Height);
        var above = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string sector, ReadLine over) in sectorLines)
        {
            double gap = top - over.Words.Max(word => word.Y + word.Height);
            if (gap >= -height / 2 && gap <= 1.5 * height && Math.Abs(over.Words[0].X - left) <= 4 * height) above.Add(sector);
        }
        return above;
    }

    // ------------------------------------------------------------ one line

    /// <summary>
    /// A line as the reader gave it, tidied: capitals, the dashes the reader may make of a hyphen made one, no marks
    /// but those names have, one space between words, none round a hyphen; a distance at its end taken off (and given
    /// when it is in light years), and the panel's "SYSTEM" taken off, glued on or not.
    /// </summary>
    internal static (string Clean, double? DistanceLy) Tidy(string text)
    {
        var kept = new StringBuilder(text.Length);
        foreach (char raw in text.Normalize(NormalizationForm.FormKC))
        {
            char c = char.ToUpperInvariant(raw);
            if (c is '‐' or '‑' or '‒' or '–' or '—' or '−' or '_' or '~') c = '-';
            kept.Append(c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '+' or '.' or ',' or '\'' ? c : ' ');
        }
        string clean = Hyphens().Replace(Spaces().Replace(kept.ToString(), " ").Trim(), "-");
        double? ly = null;
        if (TrailingLy().Match(clean) is { Success: true } distance)
        {
            ly = LightYears(distance.Groups["v"].Value);
            clean = clean[..distance.Index];
        }
        else if (TrailingOther().Match(clean) is { Success: true } other) clean = clean[..other.Index];
        clean = TrailingSystem().Replace(clean, "");
        return (clean.Trim(' ', '-', '.', ','), ly);
    }

    /// <summary>The parts of a generated name as read, before anything is put right; the sector is "" when the line has none.</summary>
    internal sealed record Parts(string Sector, char L1, char L2, char L3, char Mass, string N1, string? N2);

    /// <summary>The parts of a tidied line that has a generated name's ending ("WO-J A3B-O"); null for any other.</summary>
    internal static Parts? Procedural(string clean) => GeneratedEnding().Match(clean) is { Success: true } m
        ? new Parts(m.Groups["pre"].Value.Trim(), m.Groups["l1"].Value[0], m.Groups["l2"].Value[0], m.Groups["l3"].Value[0], m.Groups["m"].Value[0],
            m.Groups["n1"].Value, m.Groups["n2"].Success ? m.Groups["n2"].Value : null)
        : null;

    /// <summary>
    /// Every way a generated name read may be spelt, decoded: each a different system (by its number), in the order
    /// found. A line without its sector takes each of <paramref name="sectors"/> instead.
    /// </summary>
    internal static IReadOnlyList<DecodedName> Spellings(Parts parts, IReadOnlyCollection<string> sectors)
    {
        if (MassCode(parts.Mass) is not { } mass) return [];
        IReadOnlyList<string> numbers = parts.N2 is null
            ? [.. Digits(parts.N1).Select(first => mass + first)]
            : [.. Digits(parts.N1).SelectMany(first => Digits(parts.N2).Select(second => $"{mass}{first}-{second}"))];
        IReadOnlyList<string> sectorNames = parts.Sector.Length > 0 ? Sectors(parts.Sector) : [.. sectors];
        var found = new List<DecodedName>();
        var numbered = new HashSet<long>();
        foreach (char a in Letters(parts.L1))
            foreach (char b in Letters(parts.L2))
                foreach (char c in Letters(parts.L3))
                    foreach (string number in numbers)
                        foreach (string sector in sectorNames)
                            if (ProcGenNames.Decode($"{sector} {a}{b}-{c} {number}") is { } decoded && numbered.Add(decoded.Cube.Address))
                                found.Add(decoded);
        return found;
    }

    /// <summary>
    /// The decoded names that can be where the picture says: <paramref name="ly"/> from the commander when the panel
    /// gave a distance (the cube's middle that far, give or take half its diagonal and <see cref="DistanceSlackLy"/>);
    /// without one, within the Navigation panel's <see cref="PanelLy"/> (give or take as much) on a row of the panel
    /// (<paramref name="panel"/>: see <see cref="Read"/>), else within <see cref="AnywhereLy"/>. All of them while the
    /// commander's place is not known.
    /// </summary>
    internal static IReadOnlyList<DecodedName> Plausible(IReadOnlyList<DecodedName> names, (double X, double Y, double Z)? here, double? ly, bool panel = false)
    {
        if (here is not { } at) return names;
        return [.. names.Where(name =>
        {
            (double x, double y, double z) = name.Cube.Centre;
            double far = Math.Sqrt((x - at.X) * (x - at.X) + (y - at.Y) * (y - at.Y) + (z - at.Z) * (z - at.Z));
            double halfDiagonal = name.Cube.CubeLy * Math.Sqrt(3) / 2;
            return ly is { } read ? Math.Abs(far - read) <= halfDiagonal + DistanceSlackLy
                : panel ? far <= PanelLy + halfDiagonal + DistanceSlackLy
                : far <= AnywhereLy;
        })];
    }

    /// <summary>
    /// The sectors the words before a generated name's ending may be: the last one, two … five words (an icon at the
    /// row's start is read as a letter or two), each also without the hyphens the reader puts into a word ("WE-I") and
    /// with digits read in a word of letters put right ("C0L", "SECT0R"); only those that are sectors.
    /// </summary>
    static IReadOnlyList<string> Sectors(string pre)
    {
        string[] words = pre.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var sectors = new List<string>();
        for (int count = 1; count <= Math.Min(MostSectorWords, words.Length); count++)
        {
            string[] last = words[^count..];
            foreach (string text in new[]
            {
                string.Join(' ', last),
                string.Join(' ', last.Select(word => word.Replace("-", "", StringComparison.Ordinal))),
                string.Join(' ', last.Select(word => LettersInWord(word.Replace("-", "", StringComparison.Ordinal)))),
            })
                if (text.Length > 0 && tried.Add(text) && IsSector(text)) sectors.Add(text);
        }
        return sectors;
    }

    /// <summary>A sector of the grid, or a nebula's own sector: the first cube of it has a name.</summary>
    static bool IsSector(string text) => ProcGenNames.Decode(text + " AA-A a0") is not null;

    /// <summary>A word with letters and digits in it has the digits that look like letters made letters ("C0L" is COL); a number ("285") is left alone.</summary>
    static string LettersInWord(string word) =>
        word.Any(char.IsAsciiLetter) && word.Any(char.IsAsciiDigit) ? new string([.. word.Select(c => LetterFor(c) ?? c)]) : word;

    /// <summary>The letters a letter of a name's middle may be: Q, G and O for any of them, else the one read; none for a digit that looks like no letter.</summary>
    static IEnumerable<char> Letters(char read)
    {
        char letter = char.IsAsciiDigit(read) ? LetterFor(read) ?? '?' : read;
        if (letter is < 'A' or > 'Z') return [];
        return letter is 'O' or 'Q' or 'G' ? [letter, .. "OQG".Where(other => other != letter)] : [letter];
    }

    /// <summary>The letter a digit stands for in a word: 0 for O, 8 for B, 1 for I, 5 for S, 2 for Z; null for the others.</summary>
    static char? LetterFor(char digit) => digit switch
    {
        '0' => 'O',
        '8' => 'B',
        '1' => 'I',
        '5' => 'S',
        '2' => 'Z',
        _ => null,
    };

    /// <summary>The mass code, a to h, as the game writes it (small); 8 is b and 6 is g. Null for anything else.</summary>
    static char? MassCode(char read)
    {
        char mass = read switch
        {
            '8' => 'B',
            '6' => 'G',
            _ => read,
        };
        return mass is >= 'A' and <= 'H' ? char.ToLowerInvariant(mass) : null;
    }

    /// <summary>Ways of spelling a number part read at most: a few G's in a row are already a misread line.</summary>
    const int MostNumbers = 8;

    /// <summary>
    /// The ways a number part may be, the letters that look like digits made digits: O, Q and D are 0, B is 8, I and L
    /// are 1, S is 5, Z is 2, and G is 6 or 9 (a capital G looks like 6, a small one like 9, and the line was made
    /// capitals). None when another letter is left.
    /// </summary>
    static IReadOnlyList<string> Digits(string read)
    {
        List<string> ways = [""];
        foreach (char c in read)
        {
            string digits = c switch
            {
                >= '0' and <= '9' => c.ToString(),
                'O' or 'Q' or 'D' => "0",
                'B' => "8",
                'I' or 'L' => "1",
                'S' => "5",
                'Z' => "2",
                'G' => "69",
                _ => "",
            };
            if (digits.Length == 0) return [];
            ways = [.. ways.SelectMany(way => digits.Select(digit => way + digit))];
            if (ways.Count > MostNumbers) return [];
        }
        return ways.Count == 1 && ways[0].Length == 0 ? [] : ways;
    }

    /// <summary>
    /// A catalogue's name in a tidied line ("HIP 12766", "GLIESE 11O" read for Gliese 110, "BD+47 2112"), with the
    /// digits of its number put right; null for a line that holds none. An icon before it is passed over.
    /// </summary>
    internal static string? Catalogue(string clean)
    {
        if (CatalogueName().Match(clean) is not { Success: true } m) return null;
        string prefix = m.Groups["p"].Value;
        string number = string.Join(' ', m.Groups["n"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => new string([.. part.Select(c => c switch
        {
            'O' or 'Q' or 'D' => '0',
            'I' or 'L' => '1',
            'B' => '8',
            'S' => '5',
            'Z' => '2',
            _ => c,
        })])));
        // Each part of the number a number now; a word left over means the line was something else.
        if (number.Any(c => c is not ((>= '0' and <= '9') or '+' or '-' or ' ' or '.'))) return null;
        // The one-letter catalogues (G 139-21, L 26-27) look like too much else to be taken without their dash.
        if (prefix is "G" or "L" && !OneLetterNumber().IsMatch(number)) return null;
        string joiner = number.StartsWith('+') || number.StartsWith('-') ? "" : " ";
        return prefix + joiner + number;
    }

    // ------------------------------------------------------------ distances, and which row they are on

    /// <summary>A distance in light years the panel gave, and where on the picture.</summary>
    sealed record Mark(double Ly, double Left, double CentreX, double CentreY, double Height)
    {
        public double Right => 2 * CentreX - Left;
    }

    /// <summary>
    /// The picture has the Navigation panel's column of distances: figures on <see cref="ColumnRows"/> rows or more, one
    /// above another (their left or right edges within two heights of one figure's: the panel is seen in perspective, and
    /// the column leans a little), each row within a few rows of the next (the reader misses a figure now and then). The
    /// same figure read by several looks is one row. A galaxy map shows a light-year figure here and there (the selected
    /// system's distance, a route's length), not such a column.
    /// </summary>
    static bool Column(IReadOnlyList<Mark> marks)
    {
        foreach (Mark one in marks)
        {
            double near = 2 * one.Height;
            List<double> levels = [.. marks.Where(mark => Math.Abs(mark.Left - one.Left) <= near || Math.Abs(mark.Right - one.Right) <= near)
                .Select(mark => mark.CentreY).Order()];
            int run = 1;
            for (int at = 1; at < levels.Count; at++)
            {
                double gap = levels[at] - levels[at - 1];
                if (gap <= one.Height / 2) continue;           // the same row, read again
                run = gap <= NeighbourRows * one.Height ? run + 1 : 1;
                if (run >= ColumnRows) return true;
            }
        }
        return false;
    }

    /// <summary>The distances in light years on the picture ("4.20Ly"): a line of their own (the panel's column of them), or a word of one.</summary>
    static IReadOnlyList<Mark> Distances(IReadOnlyList<ReadLine> lines)
    {
        var marks = new List<Mark>();
        foreach (ReadLine line in lines)
        {
            if (line.Words.Count == 0) continue;
            if (Ly(string.Concat(line.Words.Select(word => word.Text))) is { } whole)
            {
                marks.Add(Of(whole, line.Words));
                continue;
            }
            for (int at = 0; at < line.Words.Count; at++)
            {
                if (Ly(line.Words[at].Text) is { } one) marks.Add(Of(one, [line.Words[at]]));
                else if (at + 1 < line.Words.Count && Ly(line.Words[at].Text + line.Words[at + 1].Text) is { } two) marks.Add(Of(two, [line.Words[at], line.Words[at + 1]]));
            }
        }
        return marks;

        static Mark Of(double ly, IReadOnlyList<ReadWord> words)
        {
            double left = words.Min(word => word.X), right = words.Max(word => word.X + word.Width);
            double top = words.Min(word => word.Y), bottom = words.Max(word => word.Y + word.Height);
            return new Mark(ly, left, (left + right) / 2, (top + bottom) / 2, bottom - top);
        }
    }

    /// <summary>A distance in light years as the panel writes it ("4.20Ly", "10.8Ly"), with digits that were read as letters put right; null for anything else.</summary>
    static double? Ly(string text)
    {
        string tidy = text.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        return WholeLy().Match(tidy) is { Success: true } m ? LightYears(m.Groups["v"].Value) : null;
    }

    /// <summary>
    /// The number of a distance, its look-alike letters made digits. The panel always writes light years with a decimal
    /// point: a number without one is a point the reader lost (4.20 read as 420), so it is no distance at all.
    /// </summary>
    static double? LightYears(string read)
    {
        string digits = new([.. read.Select(c => c switch
        {
            'O' or 'Q' or 'D' => '0',
            'S' => '5',
            'B' => '8',
            'I' or 'L' => '1',
            'Z' => '2',
            _ => c,
        })]);
        return digits.Contains('.') && double.TryParse(digits.Replace(",", "", StringComparison.Ordinal), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double ly)
            ? ly
            : null;
    }

    /// <summary>
    /// How the picture's rows slope (down per pixel across): the panel is tilted towards the pilot. The middle of the
    /// slopes of the lines long enough to tell, each from its first word to its last; 0 when none is.
    /// </summary>
    static double Slope(IReadOnlyList<ReadLine> lines)
    {
        var slopes = new List<double>();
        foreach (ReadLine line in lines)
        {
            if (line.Words.Count < 2) continue;
            ReadWord first = line.Words[0], last = line.Words[^1];
            double across = last.CentreX - first.CentreX, height = line.Words.Average(word => word.Height);
            if (across > 4 * height) slopes.Add((last.CentreY - first.CentreY) / across);
        }
        if (slopes.Count == 0) return 0;
        slopes.Sort();
        return slopes[slopes.Count / 2];
    }

    /// <summary>
    /// The distance on a name's row: the panel's distance column is far to the right of the names, the rows tilted,
    /// so the row is followed from the name's last word at its own slope (the panel is seen in perspective, so the rows
    /// lean a little more the lower they are; a line too short to tell takes the picture's), and the distance whose
    /// middle is nearest that line (within under half a row) is the row's. Null when none is.
    /// </summary>
    static double? DistanceBeside(ReadLine line, IReadOnlyList<Mark> marks, double slope)
    {
        ReadWord first = line.Words[0], last = line.Words[^1];
        double right = last.X + last.Width;
        double height = line.Words.Average(word => word.Height);
        if (last.CentreX - first.CentreX > 4 * height) slope = (last.CentreY - first.CentreY) / (last.CentreX - first.CentreX);
        Mark? best = null;
        double bestOff = double.MaxValue;
        foreach (Mark mark in marks)
        {
            if (mark.Left <= right || mark.Left - right > 40 * height) continue;
            double expected = last.CentreY + slope * (mark.CentreX - last.CentreX);
            double off = Math.Abs(mark.CentreY - expected);
            if (off <= 0.45 * Math.Max(height, mark.Height) && off < bestOff) (best, bestOff) = (mark, off);
        }
        return best?.Ly;
    }

    // ------------------------------------------------------------ patterns

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s?-\s?")]
    private static partial Regex Hyphens();

    // A distance in light years at the end of a line: digits (or letters read for them), a point, one or two more.
    [GeneratedRegex(@"\s?(?<v>[0-9OSBILZ][0-9OSBILZ,]*\.[0-9OSBILZ]{1,2})\s?L[YV]$")]
    private static partial Regex TrailingLy();

    // Any other distance at the end of a line: light seconds, kilometres or metres (stations, bodies), or light years without their point.
    [GeneratedRegex(@"\s?[0-9][0-9,.]*\s?(?:LS|LY|KM|M)$")]
    private static partial Regex TrailingOther();

    // The panel's "SYSTEM" after the commander's own system, glued on or not, cut short or not.
    [GeneratedRegex(@"[\s-]*SYST(?:E(?:MS?)?)?$")]
    private static partial Regex TrailingSystem();

    // A generated name's ending: two letters, a hyphen, a letter, the mass code, a number, and maybe a hyphen and
    // another; digits and letters either way, as the reader mixes them. What stands before it may be the sector.
    [GeneratedRegex(@"^(?:(?<pre>.*?)\s)?(?<l1>[A-Z0-9])(?<l2>[A-Z0-9])-(?<l3>[A-Z0-9])\s?(?<m>[A-Z0-9])(?<n1>[A-Z0-9]{1,6})(?:-(?<n2>[A-Z0-9]{1,6}))?$")]
    private static partial Regex GeneratedEnding();

    // The catalogues whose names players meet in the Navigation panel, then the number (with the letters a reader
    // makes of digits). Bonner (BD), Cordoba (CD) and Cape (CPD) numbers start with their sign.
    [GeneratedRegex(@"(?:^|\s)(?<p>HIP|HD|HR|GLIESE|GJ|LHS|LTT|LP|LFT|NLTT|ROSS|WOLF|LAWD|LALANDE|STKM|STEIN|TYC|BPM|LDS|LPM|WISE|2MASS|BD|CD|CPD|G|L)\s?(?<n>[+\-]?[0-9OQDILBSZ][0-9OQDILBSZ +\-.]*)$")]
    private static partial Regex CatalogueName();

    [GeneratedRegex(@"^\d+-\d+$")]
    private static partial Regex OneLetterNumber();

    // A whole distance in light years, spaces taken out.
    [GeneratedRegex(@"^(?<v>[0-9OSBILZ]{1,3}(?:,[0-9OSBILZ]{3})*\.[0-9OSBILZ]{1,2})L[YV]$")]
    private static partial Regex WholeLy();
}
