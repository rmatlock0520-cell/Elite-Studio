// Elite's generated sector and system names, read both ways. This is a port to C# of the name code
// in EDTS, the Elite Dangerous Travel Scripts (https://bitbucket.org/Esvandiary/edts: edtslib's
// pgnames.py, pgdata.py, sector.py and util.py). The scheme itself was worked out by CMDR Jackie
// Silver and others in the community. The tables between the two "generated" lines below are
// copied out of pgdata.py and pgnames.py by a script (generate-procgen-tables.cs), never by hand.
// EDTS carries this notice, kept here as its licence asks:
//
// Copyright (c) 2016, Andy Martin
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
//     * Redistributions of source code must retain the above copyright
//       notice, this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above copyright
//       notice, this list of conditions and the following disclaimer in the
//       documentation and/or other materials provided with the distribution.
//     * Neither the name of the EDTS Project nor the
//       names of its contributors may be used to endorse or promote products
//       derived from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
// ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE EDTS PROJECT OR ANDY MARTIN BE LIABLE FOR ANY
// DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
// ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

using System.Globalization;
using System.Text.RegularExpressions;
using EliteStudio.Core;

namespace EliteStudio.Nav;

/// <summary>A generated system name read back into the cube it names.</summary>
/// <param name="Name">The name as the game writes it: the sector as it is spelt, capital letters before the dash and after it, a small letter for the mass code, no leading zeros.</param>
/// <param name="Sector">The sector part of the name: a generated sector ("Wredguia") or a nebula's own sector ("Col 285 Sector").</param>
/// <param name="HandAuthored">The name counts its cubes from a nebula's own sector, which the game's makers placed by hand, not from a sector of the grid.</param>
/// <param name="Cube">The cube in the game's grid, and the system's number in it. Its <see cref="CubePlace.Address"/> is the system's address.</param>
public sealed record DecodedName(string Name, string Sector, bool HandAuthored, CubePlace Cube)
{
    /// <summary>The system lies within this many light years of the cube's centre along each of x, y and z: half the cube.</summary>
    public double HalfWidth => Cube.CubeLy / 2;
}

/// <summary>
/// Elite's generated names, read without asking anybody. Every sector of the game's grid that the
/// game's makers did not name by hand is named by a rule from where it lies ("Wredguia"), and so is
/// every system no catalogue names: the sector, then letters and numbers that count the system's
/// cube inside the sector, then the system's number in its cube ("Wredguia WD-K d8-65"). Near
/// nebulae and star clusters the cubes are counted from a box round the nebula instead ("Col 285
/// Sector VK-N c7-18"). So a name alone tells where a system is, to within half a cube, even when
/// no database has heard of it, and a place alone tells what its sector is called.
/// </summary>
public static partial class ProcGenNames
{
    static readonly HashSet<string> PrefixSet, VowelInfixSet, ConsonantInfixSet;
    // Where each prefix's and each infix's run starts in the sequence of all of them, and how long it is.
    static readonly Dictionary<string, (int Start, int Length)> PrefixRuns = new(StringComparer.Ordinal), InfixRuns = new(StringComparer.Ordinal);
    static readonly int PrefixTotalRun, VowelInfixTotalRun, ConsonantInfixTotalRun;
    // EDTS's lists by its own numbers: [1] the vowel-ish sequence, [2] the consonant-ish one ([0] is unused).
    static readonly string[][] C1Infixes, C1Suffixes, C2Suffixes;
    static readonly Dictionary<string, string[]?> PrefixSuffixes = new(StringComparer.Ordinal);
    static readonly Dictionary<string, (string Name, int X, int Y, int Z)> Nebulae = new(StringComparer.Ordinal);

    // In a static constructor so that it runs after every table below has been filled in, wherever it stands in the file.
    static ProcGenNames()
    {
        PrefixSet = new HashSet<string>(Prefixes, StringComparer.Ordinal);
        VowelInfixSet = new HashSet<string>(VowelInfixes, StringComparer.Ordinal);
        ConsonantInfixSet = new HashSet<string>(ConsonantInfixes, StringComparer.Ordinal);
        C1Infixes = [[], VowelInfixes, ConsonantInfixes];
        C1Suffixes = [[], VowelSuffixes, ConsonantSuffixes];
        C2Suffixes = [[], VowelSuffixes, ConsonantSuffixes[..ShortConsonantSuffixes]];
        PrefixTotalRun = Runs(Prefixes, PrefixRuns, PrefixRun);
        VowelInfixTotalRun = Runs(VowelInfixes, InfixRuns, InfixRun);
        ConsonantInfixTotalRun = Runs(ConsonantInfixes, InfixRuns, InfixRun);
        foreach (string prefix in Prefixes) PrefixSuffixes[prefix] = Fragments(prefix) is { } pieces ? Suffixes(pieces, all: false) : null;
        foreach (var nebula in NebulaCorners) Nebulae[nebula.Name.ToLowerInvariant()] = nebula;

        static int Runs(string[] fragments, Dictionary<string, (int Start, int Length)> runs, Func<string, int> length)
        {
            int start = 0;
            foreach (string fragment in fragments)
            {
                runs[fragment] = (start, length(fragment));
                start += length(fragment);
            }
            return start;
        }
    }

    // ------------------------------------------------------------ what the rest of Studio asks

    /// <summary>
    /// The generated name of a sector of the grid, by its numbers along x, y and z (as
    /// <see cref="Galaxy.SectorOf"/> gives them). Where the game's makers named a nebula's sector by
    /// hand, that name is not this one: the grid's sector keeps its generated name underneath.
    /// Null for some sectors far beyond the galaxy's northern rim, where EDTS's tables run out; no
    /// star lies there, and every sector over the galaxy's own footprint has its name (the tests
    /// check them all in the layers where stars are recorded).
    /// </summary>
    public static string? SectorName(int x, int y, int z)
    {
        if (x is < 0 or >= GridSize || y is < 0 or >= GridSize || z is < 0 or >= GridSize) return null;
        int offset = (z * GridSize + y) * GridSize + x;
        return (SectorClass(offset) == 1 ? C1Name(offset) : C2Name(offset)) is { } fragments ? Format(fragments) : null;
    }

    /// <summary>
    /// The sector of the grid a generated sector name belongs to, by its numbers along x, y and z;
    /// null for any other name. Upper and lower case and spaces do not matter. A name that the rule
    /// would spell but never gives to any place is turned down too: the way back must give the same
    /// name. (Far past the galaxy's northern rim some names cannot be read back this way.)
    /// </summary>
    public static (int X, int Y, int Z)? SectorIndex(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || Fragments(name) is not { } fragments) return null;
        int? offset = SectorClass(fragments) switch
        {
            1 => C1Offset(fragments),
            2 => C2Offset(fragments),
            _ => null,
        };
        if (offset is not (>= 0 and < GridSize * GridSize * GridSize)) return null;
        (int x, int y, int z) = (offset.Value % GridSize, offset.Value / GridSize % GridSize, offset.Value / (GridSize * GridSize));
        return SectorName(x, y, z) == Format(fragments) ? (x, y, z) : null;
    }

    /// <summary>
    /// Where a system with a generated name lies: its cube, and its number in the cube. Null for a
    /// name that is not generated (a catalogue's "HIP 64267", a hand-given "Suhte"), a sector that
    /// does not exist, or letters and numbers that would put the cube outside its sector.
    /// </summary>
    public static DecodedName? Decode(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) return null;
        Match parts = SystemNamePattern().Match(name.Trim());
        if (!parts.Success) return null;
        int mass = char.ToLowerInvariant(parts.Groups["mcode"].Value[0]) - 'a';
        if (mass is < 0 or > 7) return null;
        double cubeLy = Galaxy.CubeLy(mass), half = cubeLy / 2;
        if (!Whole(parts.Groups["n1"], out long first) || !Whole(parts.Groups["n2"], out long number) || first > 1_000_000 || number > CubePlace.NumberMask(mass)) return null;

        // The corner the name's cubes are counted from, and how far past the sector a cube may lie.
        string sectorText = parts.Groups["sector"].Value;
        string sector;
        bool handAuthored;
        (double X, double Y, double Z) corner;
        double leeway;
        if (Nebulae.TryGetValue(sectorText.ToLowerInvariant(), out var nebula))
        {
            (sector, handAuthored, corner, leeway) = (nebula.Name, true, NebulaCorner(nebula, cubeLy), half);
        }
        else
        {
            if (SectorIndex(sectorText) is not { } index) return null;
            Box box = Galaxy.SectorBounds(index.X, index.Y, index.Z);
            (sector, handAuthored, corner, leeway) = (SectorName(index.X, index.Y, index.Z)!, false, (box.X0, box.Y0, box.Z0), 0);   // read back already
        }

        // The letters and the first number count the cubes: x first, 128 to a row, then up, then north.
        long count = first * Letters * Letters * Letters
            + Letter(parts.Groups["l3"]) * Letters * Letters + Letter(parts.Groups["l2"]) * Letters + Letter(parts.Groups["l1"]);
        long north = count / (RowLength * RowLength), up = count / RowLength % RowLength, east = count % RowLength;
        if (north >= RowLength || new[] { east, up, north }.Any(cubes => cubes * cubeLy + half > Galaxy.SectorLy + leeway)) return null;

        (double x, double y, double z) = (corner.X + east * cubeLy + half, corner.Y + up * cubeLy + half, corner.Z + north * cubeLy + half);
        (int sx, int sy, int sz) = Galaxy.SectorOf(x, y, z);
        if (sx is < 0 or >= GridSize || sy is < 0 or >= Galaxy.Layers || sz is < 0 or >= GridSize) return null;
        Box home = Galaxy.SectorBounds(sx, sy, sz);
        var place = new CubePlace(sx, sy, sz, (int)Math.Floor((x - home.X0) / cubeLy), (int)Math.Floor((y - home.Y0) / cubeLy), (int)Math.Floor((z - home.Z0) / cubeLy), mass, number);
        return new DecodedName(Galaxy.SystemName(sector, ((int)east, (int)up, (int)north), mass, number), sector, handAuthored, place);

        static bool Whole(Group digits, out long value)
        {
            value = 0;
            return !digits.Success || (digits.Value.Length <= 12 && long.TryParse(digits.Value, NumberStyles.None, CultureInfo.InvariantCulture, out value));
        }

        static long Letter(Group letter) => char.ToUpperInvariant(letter.Value[0]) - 'A';
    }

    /// <summary>
    /// The generated name of the system with this place and number, counted in the sector of the
    /// grid the cube lies in ("Synuefai YQ-A c15-1"); null where the sector has no name
    /// (<see cref="SectorName"/>). Inside a nebula's own sector the game names its systems from
    /// that sector instead: see <see cref="SystemName(CubePlace, string)"/>.
    /// </summary>
    public static string? SystemName(CubePlace place) =>
        SectorName(place.SectorX, place.SectorY, place.SectorZ) is { } sector
            ? Galaxy.SystemName(sector, (place.CubeX, place.CubeY, place.CubeZ), place.Mass, place.Number)
            : null;

    /// <summary>
    /// The name of the system with this place and number counted from a nebula's own sector
    /// ("Col 285 Sector VK-N c7-18"); null when that sector is not one of the hand-named ones, or
    /// counts no cube there. The letters reach a whole sector's width from the corner of the box
    /// round the nebula, far past the nebula itself: whether the game names a system this way
    /// depends on whether the system lies inside the nebula, which the caller must know.
    /// </summary>
    public static string? SystemName(CubePlace place, string nebulaSector)
    {
        if (!Nebulae.TryGetValue(nebulaSector.ToLowerInvariant(), out var nebula)) return null;
        double cubeLy = place.CubeLy;
        (double X, double Y, double Z) from = NebulaCorner(nebula, cubeLy), at = place.Corner;
        (long east, long up, long north) = ((long)Math.Round((at.X - from.X) / cubeLy), (long)Math.Round((at.Y - from.Y) / cubeLy), (long)Math.Round((at.Z - from.Z) / cubeLy));
        if (east is < 0 or >= RowLength || up is < 0 or >= RowLength || north is < 0 or >= RowLength) return null;
        string name = Galaxy.SystemName(nebula.Name, ((int)east, (int)up, (int)north), place.Mass, place.Number);
        return Decode(name)?.Cube == place ? name : null;   // the same rule as reading a name: no cube past the sector's edge
    }

    // ------------------------------------------------------------ EDTS's rule, step by step

    const int GridSize = 128;   // sectors along each side as the rule counts them (EDTS's galaxy_size)
    const int Letters = 26;

    /// <summary>
    /// A nebula's corner for cubes of one size: the corner of the box round its spheres, moved down
    /// onto the grid of those cubes through Sol's sector (EDTS's HARegion.get_origin).
    /// </summary>
    static (double X, double Y, double Z) NebulaCorner((string Name, int X, int Y, int Z) nebula, double cubeLy) => (
        nebula.X - Galaxy.Mod(nebula.X - Galaxy.BaseX, cubeLy),
        nebula.Y - Galaxy.Mod(nebula.Y - Galaxy.BaseY, cubeLy),
        nebula.Z - Galaxy.Mod(nebula.Z - Galaxy.BaseZ, cubeLy));

    /// <summary>
    /// A sector name cut into the pieces it is built from ("Dryau Aowsy" into Dry, au, Ao, wsy),
    /// taking the longest piece that fits each time; null when it cannot be cut, or into more pieces
    /// than a name has (EDTS's get_sector_fragments). Spaces are dropped and the case put right first.
    /// </summary>
    static string[]? Fragments(string sectorName)
    {
        string text = TitleCase(sectorName).Replace(" ", "", StringComparison.Ordinal);
        var found = new List<string>();
        for (int at = 0; at < text.Length;)
        {
            string? piece = null;
            foreach (string fragment in FragmentsLongestFirst)
                if (text.AsSpan(at).StartsWith(fragment, StringComparison.Ordinal)) { piece = fragment; break; }
            if (piece is null) return null;
            found.Add(piece);
            at += piece.Length;
        }
        return found.Count is > 0 and <= FragmentLimit ? [.. found] : null;
    }

    /// <summary>Python's str.title(), which EDTS runs every name through: a capital at the start of each word, small letters after.</summary>
    static string TitleCase(string text)
    {
        char[] letters = text.ToCharArray();
        bool inWord = false;
        for (int at = 0; at < letters.Length; at++)
        {
            bool letter = char.IsLetter(letters[at]);
            if (letter) letters[at] = inWord ? char.ToLowerInvariant(letters[at]) : char.ToUpperInvariant(letters[at]);
            inWord = letter;
        }
        return new string(letters);
    }

    /// <summary>The pieces put together: two words for a name of the second kind ("Eorm Fraae"), one for the first ("Wredguia").</summary>
    static string Format(string[] fragments) =>
        fragments.Length == 4 && PrefixSet.Contains(fragments[2]) ? $"{fragments[0]}{fragments[1]} {fragments[2]}{fragments[3]}" : string.Concat(fragments);

    /// <summary>Which of the two kinds of name a sector has, from its pieces: 2 for two words, 1 for one, 0 for no generated name.</summary>
    static int SectorClass(string[] fragments) =>
        fragments.Length == 4 && PrefixSet.Contains(fragments[0]) && PrefixSet.Contains(fragments[2]) ? 2
        : fragments.Length is 3 or 4 && PrefixSet.Contains(fragments[0]) ? 1
        : 0;

    /// <summary>Which of the two kinds of name the sector at this place in the sequence has: a hash of the place picks it, 1 or 2.</summary>
    static int SectorClass(int offset) => (int)(Jenkins32((uint)offset) % 2) + 1;

    /// <summary>A 32-bit hash ascribed to Bob Jenkins, as EDTS's util.jenkins32 has it.</summary>
    static uint Jenkins32(uint key)
    {
        unchecked
        {
            key += key << 12;
            key ^= key >> 22;
            key += key << 4;
            key ^= key >> 9;
            key += key << 10;
            key ^= key >> 2;
            key += key << 7;
            key ^= key >> 12;
            return key;
        }
    }

    /// <summary>Two numbers woven into one bit by bit, the first in the even bits (EDTS's util.interleave).</summary>
    static long Interleave(long first, long second, int bits)
    {
        long woven = 0;
        for (int bit = 0; bit <= bits / 2; bit++)
        {
            woven |= ((first >> bit) & 1) << (bit * 2);
            woven |= ((second >> bit) & 1) << (bit * 2 + 1);
        }
        return woven & ((1L << bits) - 1);
    }

    /// <summary>The two numbers back out of a woven one (EDTS's util.deinterleave).</summary>
    static (int First, int Second) Deinterleave(long woven, int bits)
    {
        int first = 0, second = 0;
        for (int bit = 0; bit < bits; bit += 2) first |= (int)((woven >> bit) & 1) << (bit / 2);
        for (int bit = 1; bit < bits; bit += 2) second |= (int)((woven >> bit) & 1) << (bit / 2);
        return (first, second);
    }

    static int PrefixRun(string prefix) => PrefixRunOverrides.GetValueOrDefault(prefix, PrefixRunDefault);

    // An infix's run is as long as the list of suffixes that can follow it, unless the tables say otherwise.
    static int InfixRun(string infix) =>
        InfixRunOverrides.GetValueOrDefault(infix, VowelInfixSet.Contains(infix) ? ConsonantSuffixes.Length : VowelSuffixes.Length);

    static int InfixTotalRun(string infix) => VowelInfixSet.Contains(infix) ? VowelInfixTotalRun : ConsonantInfixTotalRun;

    /// <summary>The run a place in the sequence falls in (EDTS's _get_entry_from_offset); null past the end.</summary>
    static string? EntryAt(int offset, string[] fragments, Dictionary<string, (int Start, int Length)> runs)
    {
        foreach (string fragment in fragments)
        {
            (int start, int length) = runs[fragment];
            if (offset >= start && offset < start + length) return fragment;
        }
        return null;
    }

    /// <summary>The infixes that may come after these pieces (EDTS's _c1_get_infixes): vowels after consonants and the other way round; null when none may.</summary>
    static string[]? InfixesAfter(IReadOnlyList<string> fragments)
    {
        string last = fragments[^1];
        if (PrefixSet.Contains(last)) return C1Infixes[C1InfixListOf.GetValueOrDefault(last, 1)];
        if (VowelInfixSet.Contains(last)) return C1Infixes[2];
        if (ConsonantInfixSet.Contains(last)) return C1Infixes[1];
        return null;
    }

    /// <summary>
    /// The suffixes that may end a name after these pieces (EDTS's _get_suffixes). Unless all are
    /// asked for, only as many as the word's first piece has in its run.
    /// </summary>
    static string[] Suffixes(IReadOnlyList<string> fragments, bool all)
    {
        string wordStart = fragments[0], last = fragments[^1];
        string[] list;
        if (PrefixSet.Contains(last))
        {
            list = C2Suffixes[C2SuffixListOf.GetValueOrDefault(last, 1)];
            wordStart = last;
        }
        else list = ConsonantInfixSet.Contains(last) ? C1Suffixes[1] : C1Suffixes[2];
        return all ? list : list[..Math.Min(PrefixRun(wordStart), list.Length)];
    }

    /// <summary>The suffixes for a word that starts with this prefix, worked out once (EDTS cuts the prefix into pieces first, which gives the prefix itself).</summary>
    static string[]? SuffixesOfPrefix(string prefix) => PrefixSuffixes.GetValueOrDefault(prefix);

    /// <summary>A one-word name's place in the sequence of all sectors (EDTS's _c1_get_offset_from_name); null when a piece is not where it may stand.</summary>
    static int? C1Offset(string[] fragments)
    {
        int suffixAt = Array.IndexOf(Suffixes(fragments[..^1], all: true), fragments[^1]);
        if (suffixAt < 0) return null;
        int third = suffixAt;
        if (fragments.Length > 3)
        {
            // Four pieces: from the suffix's place up to the third piece's runs first.
            if (!InfixRuns.TryGetValue(fragments[2], out var run3)) return null;
            int run = InfixRun(fragments[2]), total = InfixTotalRun(fragments[2]);
            (int whole, int part) = Math.DivRem(suffixAt + suffixAt / run * total, run);
            third = whole * total + part + run3.Start;
        }
        if (!InfixRuns.TryGetValue(fragments[1], out var run2) || !PrefixRuns.TryGetValue(fragments[0], out var run1)) return null;
        (int whole2, int part2) = Math.DivRem(third, InfixRun(fragments[1]));
        int second = whole2 * InfixTotalRun(fragments[1]) + part2 + run2.Start;
        (int whole1, int part1) = Math.DivRem(second, PrefixRun(fragments[0]));
        return whole1 * PrefixTotalRun + part1 + run1.Start;
    }

    /// <summary>The one-word name at a place in the sequence of all sectors (EDTS's _c1_get_name); null if the tables run out.</summary>
    static string[]? C1Name(int offset)
    {
        (int prefixCount, int at) = Math.DivRem(offset, PrefixTotalRun);
        if (EntryAt(at, Prefixes, PrefixRuns) is not { } prefix) return null;
        at -= PrefixRuns[prefix].Start;

        string[] firstInfixes = InfixesAfter([prefix])!;   // after a prefix there are always infixes
        (int infixCount, at) = Math.DivRem(prefixCount * PrefixRun(prefix) + at, InfixTotalRun(firstInfixes[0]));
        if (EntryAt(at, firstInfixes, InfixRuns) is not { } infix) return null;
        at -= InfixRuns[infix].Start;

        var fragments = new List<string> { prefix, infix };
        string[] suffixes = Suffixes(fragments, all: true);
        int next = InfixRun(infix) * infixCount + at;
        if (next >= suffixes.Length)
        {
            // Every three-piece name of this run is used up: a second infix comes before the suffix.
            string[] secondInfixes = InfixesAfter(fragments)!;
            (int secondCount, at) = Math.DivRem(infixCount * InfixRun(infix) + at, InfixTotalRun(secondInfixes[0]));
            if (EntryAt(at, secondInfixes, InfixRuns) is not { } second) return null;
            at -= InfixRuns[second].Start;
            fragments.Add(second);
            suffixes = Suffixes(fragments, all: true);
            next = InfixRun(second) * secondCount + at;
        }
        if (next >= suffixes.Length) return null;
        fragments.Add(suffixes[next]);
        return [.. fragments];
    }

    /// <summary>A two-word name's place in the sequence of all sectors (EDTS's _c2_get_offset_from_name): each word's place in the prefix runs, woven together.</summary>
    static int? C2Offset(string[] fragments)
    {
        if (!PrefixRuns.TryGetValue(fragments[0], out var first) || !PrefixRuns.TryGetValue(fragments[2], out var second)) return null;
        int firstAt = SuffixesOfPrefix(fragments[0]) is { } firstSuffixes ? Array.IndexOf(firstSuffixes, fragments[1]) : -1;
        int secondAt = SuffixesOfPrefix(fragments[2]) is { } secondSuffixes ? Array.IndexOf(secondSuffixes, fragments[3]) : -1;
        if (firstAt < 0 || secondAt < 0) return null;
        long woven = Interleave(first.Start + firstAt, second.Start + secondAt, 32);
        return woven < int.MaxValue ? (int)woven : null;
    }

    /// <summary>The two-word name at a place in the sequence of all sectors (EDTS's _c2_get_name_from_offset); null if the tables run out.</summary>
    static string[]? C2Name(int offset)
    {
        (int first, int second) = Deinterleave(offset, 32);
        if (EntryAt(first, Prefixes, PrefixRuns) is not { } prefix1 || EntryAt(second, Prefixes, PrefixRuns) is not { } prefix2) return null;
        if (SuffixesOfPrefix(prefix1) is not { } suffixes1 || SuffixesOfPrefix(prefix2) is not { } suffixes2) return null;
        int at1 = first - PrefixRuns[prefix1].Start, at2 = second - PrefixRuns[prefix2].Start;
        return at1 < suffixes1.Length && at2 < suffixes2.Length ? [prefix1, suffixes1[at1], prefix2, suffixes2[at2]] : null;
    }

    // ---- generated tables: begin ----
    // Written by generate-procgen-tables.cs on 2026-10-14 from EDTS's pgdata.py (SHA-256 55cd8f8885c2ef30)
    // and pgnames.py (SHA-256 598d1606a7bc06a9). Do not edit by hand: run the script again.

    /// <summary>Every piece a sector name is built from, longest first (EDTS's cx_fragments): a name is cut by taking the longest piece that fits each time.</summary>
    static readonly string[] FragmentsLongestFirst =
    [
        "rshch", "Syst", "Hyph", "Phyl", "Lych", "Pyth", "Schr", "vsky", "rsch", "rphs", "wnst", "rsts", "Sly", "Dry",
        "Phl", "Sch", "Hyp", "Kyl", "Phr", "Eae", "Scr", "Shr", "Fly", "Pry", "Chr", "Tyr", "Cry", "Aae", "Myc", "Gyr",
        "Myl", "Myn", "Myr", "Pyr", "Syn", "Str", "Syr", "Sty", "Sph", "Spl", "Lyr", "Thr", "Lys", "tch", "scs", "wsy",
        "sms", "dst", "nts", "rld", "lls", "rgh", "wyg", "rbs", "tts", "lch", "nks", "rrs", "sts", "sys", "sty", "tls",
        "rds", "nch", "rns", "wls", "rnt", "rdy", "rst", "pps", "sks", "ppy", "sps", "sky", "lts", "rth", "ths", "rts",
        "mly", "nds", "wry", "nns", "lks", "sly", "rph", "vvy", "rks", "rps", "wns", "nth", "phs", "oea", "eia", "ooe",
        "iae", "oae", "uae", "eae", "aea", "eou", "aei", "uia", "aae", "eau", "Th", "Eo", "Oo", "Eu", "Tr", "Ou", "Tz",
        "Ae", "Ai", "Ph", "Fl", "Ao", "Pl", "Fr", "Au", "Pr", "Py", "Bl", "Gl", "Br", "Gr", "By", "Ly", "Ch", "Cl",
        "Rh", "Wh", "Cr", "Cy", "Wr", "Hy", "My", "Sc", "Sh", "Sk", "Io", "Dr", "Sl", "Sm", "Sp", "Sq", "St", "Ny",
        "Sw", "Ea", "Ee", "Ei", "Oe", "ll", "ss", "dg", "ng", "mb", "gn", "th", "ch", "wh", "ck", "ph", "sh", "ct",
        "wr", "ai", "oi", "ea", "ie", "ee", "oo", "ue", "oa", "au", "ae", "oe", "rb", "rd", "rg", "hm", "hn", "rk",
        "rl", "rm", "cs", "rn", "hs", "rp", "wn", "ms", "rr", "mt", "rs", "cy", "rt", "ws", "my", "ry", "nd", "sc",
        "nk", "sk", "nn", "ds", "sm", "sp", "ns", "nt", "dy", "st", "xt", "nz", "sy", "xy", "tl", "ts", "tt", "tz",
        "ff", "kh", "fs", "pp", "ft", "ks", "pr", "ps", "pt", "fy", "ky", "py", "bb", "zz", "ld", "lf", "gh", "lk",
        "ln", "bs", "gs", "ls", "lt", "qs", "gy", "lz", "io", "aa", "ua", "ou", "ao", "ia", "A", "B", "C", "D", "E",
        "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z", "b",
        "c", "d", "f", "g", "h", "j", "k", "l", "m", "n", "p", "q", "r", "s", "t", "v", "w", "x", "y", "z", "o", "a",
        "u", "e", "i",
    ];

    /// <summary>The pieces a name can begin with, in EDTS's order, which matters: their runs follow each other in it (cx_prefixes).</summary>
    static readonly string[] Prefixes =
    [
        "Th", "Eo", "Oo", "Eu", "Tr", "Sly", "Dry", "Ou", "Tz", "Phl", "Ae", "Sch", "Hyp", "Syst", "Ai", "Kyl", "Phr",
        "Eae", "Ph", "Fl", "Ao", "Scr", "Shr", "Fly", "Pl", "Fr", "Au", "Pry", "Pr", "Hyph", "Py", "Chr", "Phyl", "Tyr",
        "Bl", "Cry", "Gl", "Br", "Gr", "By", "Aae", "Myc", "Gyr", "Ly", "Myl", "Lych", "Myn", "Ch", "Myr", "Cl", "Rh",
        "Wh", "Pyr", "Cr", "Syn", "Str", "Syr", "Cy", "Wr", "Hy", "My", "Sty", "Sc", "Sph", "Spl", "A", "Sh", "B", "C",
        "D", "Sk", "Io", "Dr", "E", "Sl", "F", "Sm", "G", "H", "I", "Sp", "J", "Sq", "K", "L", "Pyth", "M", "St", "N",
        "O", "Ny", "Lyr", "P", "Sw", "Thr", "Lys", "Q", "R", "S", "T", "Ea", "U", "V", "W", "Schr", "X", "Ee", "Y", "Z",
        "Ei", "Oe",
    ];

    /// <summary>Vowel-ish infixes (c1_infixes_s1).</summary>
    static readonly string[] VowelInfixes =
    [
        "o", "ai", "a", "oi", "ea", "ie", "u", "e", "ee", "oo", "ue", "i", "oa", "au", "ae", "oe",
    ];

    /// <summary>Consonant-ish infixes (c1_infixes_s2).</summary>
    static readonly string[] ConsonantInfixes =
    [
        "ll", "ss", "b", "c", "d", "f", "dg", "g", "ng", "h", "j", "k", "l", "m", "n", "mb", "p", "q", "gn", "th", "r",
        "s", "t", "ch", "tch", "v", "w", "wh", "ck", "x", "y", "z", "ph", "sh", "ct", "wr",
    ];

    /// <summary>Vowel-ish suffixes (cx_suffixes_s1).</summary>
    static readonly string[] VowelSuffixes =
    [
        "oe", "io", "oea", "oi", "aa", "ua", "eia", "ae", "ooe", "oo", "a", "ue", "ai", "e", "iae", "oae", "ou", "uae",
        "i", "ao", "au", "o", "eae", "u", "aea", "ia", "ie", "eou", "aei", "ea", "uia", "oa", "aae", "eau", "ee",
    ];

    /// <summary>Consonant-ish suffixes (c1_suffixes_s2). Names of the second kind use only the first of them (c2_suffixes_s2).</summary>
    static readonly string[] ConsonantSuffixes =
    [
        "b", "scs", "wsy", "c", "d", "vsky", "f", "sms", "dst", "g", "rb", "h", "nts", "ch", "rd", "rld", "k", "lls",
        "ck", "rgh", "l", "rg", "m", "n", "hm", "p", "hn", "rk", "q", "rl", "r", "rm", "s", "cs", "wyg", "rn", "ct",
        "t", "hs", "rbs", "rp", "tts", "v", "wn", "ms", "w", "rr", "mt", "x", "rs", "cy", "y", "rt", "z", "ws", "lch",
        "my", "ry", "nks", "nd", "sc", "ng", "sh", "nk", "sk", "nn", "ds", "sm", "sp", "ns", "nt", "dy", "ss", "st",
        "rrs", "xt", "nz", "sy", "xy", "rsch", "rphs", "sts", "sys", "sty", "th", "tl", "tls", "rds", "nch", "rns",
        "ts", "wls", "rnt", "tt", "rdy", "rst", "pps", "tz", "tch", "sks", "ppy", "ff", "sps", "kh", "sky", "ph", "lts",
        "wnst", "rth", "ths", "fs", "pp", "ft", "ks", "pr", "ps", "pt", "fy", "rts", "ky", "rshch", "mly", "py", "bb",
        "nds", "wry", "zz", "nns", "ld", "lf", "gh", "lks", "sly", "lk", "ll", "rph", "ln", "bs", "rsts", "gs", "ls",
        "vvy", "lt", "rks", "qs", "rps", "gy", "wns", "lz", "nth", "phs",
    ];

    const int ShortConsonantSuffixes = 35;   // len(cx_suffixes_s1)

    /// <summary>Prefixes that take their suffixes from another list for names of the second kind (c2_prefix_suffix_override_map).</summary>
    static readonly Dictionary<string, int> C2SuffixListOf = new(StringComparer.Ordinal)
    {
        ["Eo"] = 2, ["Oo"] = 2, ["Eu"] = 2, ["Ou"] = 2, ["Ae"] = 2, ["Ai"] = 2, ["Eae"] = 2, ["Ao"] = 2, ["Au"] = 2,
        ["Aae"] = 2,
    };

    /// <summary>Prefixes that take their infixes from another list (c1_prefix_infix_override_map).</summary>
    static readonly Dictionary<string, int> C1InfixListOf = new(StringComparer.Ordinal)
    {
        ["Eo"] = 2, ["Oo"] = 2, ["Eu"] = 2, ["Ou"] = 2, ["Ae"] = 2, ["Ai"] = 2, ["Eae"] = 2, ["Ao"] = 2, ["Au"] = 2,
        ["Aae"] = 2, ["A"] = 2, ["Io"] = 2, ["E"] = 2, ["I"] = 2, ["O"] = 2, ["Ea"] = 2, ["U"] = 2, ["Ee"] = 2,
        ["Ei"] = 2, ["Oe"] = 2,
    };

    const int PrefixRunDefault = 35;   // cx_prefix_length_default

    /// <summary>Prefixes with shorter runs than the rest (cx_prefix_length_overrides).</summary>
    static readonly Dictionary<string, int> PrefixRunOverrides = new(StringComparer.Ordinal)
    {
        ["Eu"] = 31, ["Sly"] = 4, ["Tz"] = 1, ["Phl"] = 13, ["Ae"] = 12, ["Hyp"] = 25, ["Kyl"] = 30, ["Phr"] = 10,
        ["Eae"] = 4, ["Ao"] = 5, ["Scr"] = 24, ["Shr"] = 11, ["Fly"] = 20, ["Pry"] = 3, ["Hyph"] = 14, ["Py"] = 12,
        ["Phyl"] = 8, ["Tyr"] = 25, ["Cry"] = 5, ["Aae"] = 5, ["Myc"] = 2, ["Gyr"] = 10, ["Myl"] = 12, ["Lych"] = 3,
        ["Myn"] = 10, ["Myr"] = 4, ["Rh"] = 15, ["Wr"] = 31, ["Sty"] = 4, ["Spl"] = 16, ["Sk"] = 27, ["Sq"] = 7,
        ["Pyth"] = 1, ["Lyr"] = 10, ["Sw"] = 24, ["Thr"] = 32, ["Lys"] = 10, ["Schr"] = 3, ["Z"] = 34,
    };

    /// <summary>Infixes with runs of their own length (c1_infix_length_overrides).</summary>
    static readonly Dictionary<string, int> InfixRunOverrides = new(StringComparer.Ordinal)
    {
        ["oi"] = 88, ["ue"] = 147, ["oa"] = 57, ["au"] = 119, ["ae"] = 12, ["oe"] = 39, ["dg"] = 31, ["tch"] = 20,
        ["wr"] = 31,
    };

    const int RowLength = 128;   // cubes to a row in the letters and numbers of a system name (pgnames.py's _srp_rowlength)
    const int FragmentLimit = 4;   // pieces in a sector name at most (_expected_fragment_limit)

    /// <summary>
    /// The 466 sectors named by hand (EDTS's ha_regions), each with the corner of the box round its sphere or
    /// spheres, rounded down to whole light years: the cubes of the names in it are counted from there.
    /// </summary>
    static readonly (string Name, int X, int Y, int Z)[] NebulaCorners =
    [
        ("Trianguli Sector", 10, -98, -132),
        ("Crucis Sector", 15, -52, -16),
        ("Tascheter Sector", -49, -73, -113),
        ("Hydrae Sector", 17, 24, 9),
        ("Col 285 Sector", -380, -270, -346),
        ("Scorpii Sector", -23, -60, 66),
        ("Shui Wei Sector", -13, -200, -56),
        ("Shudun Sector", -34, 4, -18),
        ("Yin Sector", -44, -30, -97),
        ("Jastreb Sector", -63, -47, -91),
        ("Pegasi Sector", -271, -196, -120),
        ("Cephei Sector", -158, -20, -93),
        ("Bei Dou Sector", -74, 32, -61),
        ("Puppis Sector", 6, -45, -79),
        ("Sharru Sector", -13, 10, -85),
        ("Alrai Sector", -109, -47, -2),
        ("Lyncis Sector", -139, -5, -212),
        ("Tucanae Sector", 5, -319, 59),
        ("Piscium Sector", -105, -115, -90),
        ("Herculis Sector", -123, 20, -12),
        ("Antliae Sector", 105, -5, -41),
        ("Arietis Sector", -153, -157, -216),
        ("Capricorni Sector", -119, -180, 47),
        ("Ceti Sector", -85, -187, -103),
        ("Core Sys Sector", -50, -50, -50),
        ("Blanco 1 Sector", -274, -1096, -74),
        ("NGC 129 Sector", -4881, -541, -2981),
        ("NGC 225 Sector", -1915, -142, -1234),
        ("NGC 188 Sector", -5519, 2225, -3675),
        ("IC 1590 Sector", -8544, -1611, -5764),
        ("NGC 457 Sector", -6802, -1055, -5170),
        ("M103 Sector", -5745, -330, -4511),
        ("NGC 654 Sector", -5266, -144, -4298),
        ("NGC 659 Sector", -4975, -258, -4103),
        ("NGC 663 Sector", -5175, -361, -4312),
        ("Col 463 Sector", -1994, 181, -1572),
        ("NGC 752 Sector", -1256, -916, -1331),
        ("NGC 744 Sector", -3008, -541, -2757),
        ("Stock 2 Sector", -849, -163, -810),
        ("h Persei Sector", -5173, -793, -5106),
        ("Chi Persei Sector", -5791, -882, -5810),
        ("IC 1805 Sector", -4729, -262, -4684),
        ("NGC 957 Sector", -4276, -469, -4466),
        ("Tr 2 Sector", -1544, -257, -1669),
        ("M34 Sector", -1103, -610, -1435),
        ("NGC 1027 Sector", -1904, -82, -1953),
        ("IC 1848 Sector", -4779, -240, -5133),
        ("NGC 1245 Sector", -5348, -1698, -7983),
        ("NGC 1342 Sector", -980, -672, -1992),
        ("IC 348 Sector", -429, -410, -1157),
        ("Mel 22 Sector", -277, -368, -610),
        ("NGC 1444 Sector", -2112, -135, -3365),
        ("NGC 1502 Sector", -1636, 296, -2204),
        ("NGC 1528 Sector", -1302, -105, -2354),
        ("NGC 1545 Sector", -1161, -114, -2197),
        ("Hyades Sector", -144, -201, -283),
        ("NGC 1647 Sector", -194, -714, -1890),
        ("NGC 1662 Sector", 95, -596, -1401),
        ("NGC 1664 Sector", -1399, -199, -3884),
        ("NGC 1746 Sector", -287, -632, -2266),
        ("NGC 1778 Sector", -1020, -266, -4796),
        ("NGC 1817 Sector", 384, -1739, -6509),
        ("NGC 1857 Sector", -1356, 31, -6181),
        ("NGC 1893 Sector", -1536, -661, -10972),
        ("M38 Sector", -670, -161, -3652),
        ("Col 69 Sector", 66, -600, -1660),
        ("NGC 1981 Sector", 472, -530, -1191),
        ("Trapezium Sector", 412, -614, -1255),
        ("Col 70 Sector", -6, -887, -1605),
        ("M36 Sector", -539, -51, -4406),
        ("M37 Sector", -365, 59, -4684),
        ("NGC 2129 Sector", 495, -64, -4980),
        ("NGC 2169 Sector", 871, -224, -3350),
        ("M35 Sector", 111, -92, -2835),
        ("NGC 2175 Sector", 862, -41, -5304),
        ("Col 89 Sector", 10, -320, -4781),
        ("NGC 2232 Sector", 501, -309, -1111),
        ("Col 97 Sector", 628, -315, -2101),
        ("NGC 2244 Sector", 1680, -577, -4629),
        ("NGC 2251 Sector", 1607, -119, -4094),
        ("Col 107 Sector", 2013, -668, -5621),
        ("NGC 2264 Sector", 341, -427, -2516),
        ("M41 Sector", 1381, -751, -1747),
        ("NGC 2286 Sector", 5071, -765, -8092),
        ("NGC 2281 Sector", -285, 402, -1866),
        ("NGC 2301 Sector", 1414, -102, -2509),
        ("Col 121 Sector", 787, -737, -1320),
        ("M50 Sector", 1891, -188, -2386),
        ("NGC 2324 Sector", 2010, 140, -3246),
        ("NGC 2335 Sector", 3050, -240, -3480),
        ("NGC 2345 Sector", 5062, -552, -5306),
        ("NGC 2343 Sector", 2351, -118, -2513),
        ("NGC 2354 Sector", 10748, -2075, -7420),
        ("NGC 2353 Sector", 2375, -167, -2787),
        ("Col 132 Sector", 929, -662, -1117),
        ("Col 135 Sector", 792, -349, -516),
        ("NGC 2360 Sector", 4462, -384, -4202),
        ("NGC 2362 Sector", 3760, -516, -2448),
        ("NGC 2367 Sector", 5307, -511, -3764),
        ("Col 140 Sector", 1024, -344, -711),
        ("NGC 2374 Sector", 3371, -127, -3390),
        ("NGC 2384 Sector", 5573, -390, -4016),
        ("NGC 2395 Sector", 610, 340, -1538),
        ("NGC 2414 Sector", 8638, 229, -7191),
        ("M47 Sector", 1124, -31, -1123),
        ("NGC 2423 Sector", 1837, 68, -1676),
        ("Mel 71 Sector", 7490, 567, -6984),
        ("NGC 2439 Sector", 11154, -1295, -5348),
        ("M46 Sector", 3255, 59, -3019),
        ("M93 Sector", 2831, -88, -1784),
        ("NGC 2451A Sector", 652, -199, -346),
        ("NGC 2477 Sector", 3633, -579, -1296),
        ("NGC 2467 Sector", 3748, -163, -2193),
        ("NGC 2482 Sector", 3697, -1, -2235),
        ("NGC 2483 Sector", 4753, -114, -2446),
        ("NGC 2489 Sector", 11592, -444, -5369),
        ("NGC 2516 Sector", 1159, -482, -30),
        ("NGC 2506 Sector", 8204, 1567, -7459),
        ("Col 173 Sector", 841, -694, -703),
        ("NGC 2527 Sector", 1732, 6, -852),
        ("NGC 2533 Sector", 10021, 89, -4316),
        ("NGC 2539 Sector", 3402, 739, -2703),
        ("NGC 2547 Sector", 1349, -327, -246),
        ("NGC 2546 Sector", 2283, -716, -1393),
        ("M48 Sector", 1575, 446, -1843),
        ("NGC 2567 Sector", 4982, 142, -2031),
        ("NGC 2571 Sector", 3981, -378, -1662),
        ("NGC 2579 Sector", 3161, -72, -979),
        ("Pismis 4 Sector", 1810, -183, -348),
        ("NGC 2627 Sector", 6055, 580, -2272),
        ("NGC 2645 Sector", 5362, -324, -541),
        ("NGC 2632 Sector", 96, 202, -590),
        ("IC 2391 Sector", 465, -169, -97),
        ("IC 2395 Sector", 2176, -267, -251),
        ("NGC 2669 Sector", 3190, -574, -158),
        ("NGC 2670 Sector", 3767, -334, -260),
        ("Tr 10 Sector", 1312, -43, -230),
        ("M67 Sector", 1250, 1339, -2264),
        ("IC 2488 Sector", 3460, -478, 306),
        ("NGC 2910 Sector", 8362, -278, 685),
        ("NGC 2925 Sector", 2431, -127, 189),
        ("NGC 3114 Sector", 2571, -509, 369),
        ("NGC 3228 Sector", 1707, 115, 304),
        ("NGC 3247 Sector", 4812, -101, 1198),
        ("IC 2581 Sector", 7605, -117, 1894),
        ("NGC 3293 Sector", 7166, -120, 1946),
        ("NGC 3324 Sector", 6995, -291, 1845),
        ("NGC 3330 Sector", 2781, 150, 671),
        ("Col 228 Sector", 6553, -419, 1865),
        ("IC 2602 Sector", 342, -201, 22),
        ("Tr 14 Sector", 8371, -224, 2534),
        ("Tr 16 Sector", 8057, -361, 2382),
        ("NGC 3519 Sector", 4310, -173, 1560),
        ("Fe 1 Sector", 3276, -249, 1017),
        ("NGC 3532 Sector", 1265, -191, 301),
        ("NGC 3572 Sector", 5994, -73, 2206),
        ("Col 240 Sector", 4430, -357, 1451),
        ("NGC 3590 Sector", 4968, -66, 1898),
        ("NGC 3680 Sector", 2695, 782, 739),
        ("NGC 3766 Sector", 5111, -83, 2240),
        ("IC 2944 Sector", 4704, -756, 1821),
        ("Stock 14 Sector", 6231, -188, 2878),
        ("NGC 4103 Sector", 4620, 18, 2371),
        ("NGC 4349 Sector", 5953, -108, 3321),
        ("Mel 111 Sector", -88, 199, -133),
        ("NGC 4463 Sector", 2426, -632, 1232),
        ("NGC 5281 Sector", 2285, -557, 1769),
        ("NGC 4609 Sector", 2875, -519, 1596),
        ("Jewel Box Sector", 5195, 92, 3334),
        ("NGC 5138 Sector", 4999, 263, 3805),
        ("NGC 5316 Sector", 2774, -244, 2306),
        ("NGC 5460 Sector", 1271, 250, 1314),
        ("NGC 5606 Sector", 4126, 50, 4097),
        ("NGC 5617 Sector", 3407, -155, 3370),
        ("NGC 5662 Sector", 1289, -58, 1391),
        ("NGC 5822 Sector", 1535, -127, 2027),
        ("NGC 5823 Sector", 2299, 33, 2892),
        ("NGC 6025 Sector", 1325, -360, 1898),
        ("NGC 6067 Sector", 2133, -367, 3801),
        ("NGC 6087 Sector", 1424, -393, 2332),
        ("NGC 6124 Sector", 351, -21, 1373),
        ("NGC 6134 Sector", 1211, -64, 2645),
        ("NGC 6152 Sector", 1283, -427, 2741),
        ("NGC 6169 Sector", 1156, 51, 3252),
        ("NGC 6167 Sector", 1434, -156, 3204),
        ("NGC 6178 Sector", 1169, 20, 3027),
        ("NGC 6193 Sector", 1336, -260, 3307),
        ("NGC 6200 Sector", 2275, -363, 5976),
        ("NGC 6208 Sector", 895, -471, 2694),
        ("NGC 6231 Sector", 985, -81, 3717),
        ("NGC 6242 Sector", 826, 57, 3472),
        ("Tr 24 Sector", 478, -403, 3077),
        ("NGC 6250 Sector", 843, -172, 2578),
        ("NGC 6259 Sector", 919, -206, 3076),
        ("NGC 6281 Sector", 292, 17, 1486),
        ("NGC 6322 Sector", 775, -224, 3091),
        ("IC 4651 Sector", 892, -484, 2615),
        ("NGC 6383 Sector", 48, -182, 3014),
        ("M6 Sector", 1, -113, 1494),
        ("NGC 6416 Sector", 27, -167, 2316),
        ("IC 4665 Sector", -795, 103, 711),
        ("NGC 6425 Sector", 19, -151, 2560),
        ("M7 Sector", -160, -306, 745),
        ("M23 Sector", -528, -76, 1838),
        ("M20 Sector", -542, -227, 2423),
        ("NGC 6520 Sector", -350, -342, 5037),
        ("M21 Sector", -688, -189, 3733),
        ("NGC 6530 Sector", -639, -284, 4137),
        ("NGC 6546 Sector", -514, -200, 2909),
        ("NGC 6604 Sector", -1817, 83, 5167),
        ("M16 Sector", -1767, -21, 5350),
        ("M18 Sector", -1100, -136, 4038),
        ("M17 Sector", -1414, -369, 3784),
        ("NGC 6633 Sector", -790, 103, 911),
        ("M25 Sector", -651, -336, 1780),
        ("NGC 6664 Sector", -1712, -200, 3305),
        ("IC 4756 Sector", -1118, -41, 1082),
        ("M26 Sector", -2220, -372, 4659),
        ("NGC 6705 Sector", -3036, -531, 5199),
        ("NGC 6709 Sector", -2493, 144, 2448),
        ("Col 394 Sector", -711, -516, 2001),
        ("Steph 1 Sector", -1200, 265, 406),
        ("NGC 6716 Sector", -773, -529, 2343),
        ("NGC 6755 Sector", -3077, -327, 3427),
        ("Stock 1 Sector", -1146, -202, 271),
        ("NGC 6811 Sector", -3973, 654, 544),
        ("NGC 6819 Sector", -7433, 1026, 1987),
        ("NGC 6823 Sector", -5419, -119, 3032),
        ("NGC 6830 Sector", -4823, -356, 2478),
        ("NGC 6834 Sector", -6241, 42, 2673),
        ("NGC 6866 Sector", -4755, 422, 725),
        ("NGC 6871 Sector", -5340, -261, 1085),
        ("NGC 6885 Sector", -1827, -197, 749),
        ("IC 4996 Sector", -5550, 45, 1340),
        ("Mel 227 Sector", 181, -256, 179),
        ("NGC 6910 Sector", -3744, 21, 618),
        ("M29 Sector", -3752, -70, 738),
        ("NGC 6939 Sector", -3865, 709, -501),
        ("NGC 6940 Sector", -2522, -498, 672),
        ("NGC 7039 Sector", -3224, -219, -19),
        ("NGC 7063 Sector", -2260, -446, 207),
        ("NGC 7082 Sector", -5035, -588, -441),
        ("M39 Sector", -1152, -136, -140),
        ("IC 1396 Sector", -3179, -325, -939),
        ("IC 5146 Sector", -2833, -340, -286),
        ("NGC 7160 Sector", -2517, 248, -656),
        ("NGC 7209 Sector", -3962, -685, -563),
        ("NGC 7235 Sector", -9118, -6, -2159),
        ("NGC 7243 Sector", -2819, -481, -630),
        ("NGC 7380 Sector", -7351, -536, -2554),
        ("NGC 7510 Sector", -6420, -99, -2526),
        ("M52 Sector", -4472, -171, -1998),
        ("NGC 7686 Sector", -3144, -789, -1199),
        ("NGC 7789 Sector", -7403, -1273, -3821),
        ("NGC 7790 Sector", -8919, -504, -4634),
        ("IC 410 Sector", -1376, -496, -11077),
        ("NGC 3603 Sector", 18444, -325, 7212),
        ("NGC 7822 Sector", -2544, 202, -1433),
        ("NGC 281 Sector", -6762, -978, -4443),
        ("LBN 623 Sector", -600, -119, -432),
        ("Heart Sector", -5422, 17, -5385),
        ("Soul Sector", -5196, 17, -5603),
        ("Pleiades Sector", -182, -250, -444),
        ("Perseus Dark Region", -460, -417, -1146),
        ("NGC 1333 Sector", -482, -484, -1058),
        ("California Sector", -433, -314, -1019),
        ("NGC 1491 Sector", -5009, -275, -8811),
        ("Hind Sector", -133, -307, -658),
        ("Trifid of the North Sector", -744, -503, -2587),
        ("Flaming Star Sector", -334, -169, -1783),
        ("NGC 1931 Sector", -844, -64, -7061),
        ("Crab Sector", 458, -808, -7042),
        ("Running Man Sector", 486, -526, -1180),
        ("Orion Sector", 516, -547, -1208),
        ("Col 359 Sector", -960, -391, 120),
        ("Spirograph Sector", 477, -553, -920),
        ("NGC 1999 Sector", 449, -475, -1027),
        ("Flame Sector", 328, -381, -959),
        ("Horsehead Sector", 311, -373, -912),
        ("Witch Head Sector", 269, -502, -816),
        ("Monkey Head Sector", 1033, -56, -6399),
        ("Jellyfish Sector", 689, 152, -5031),
        ("Rosette Sector", 2246, -276, -4849),
        ("Hubble's Variable Sector", 1110, -32, -2845),
        ("Cone Sector", 755, -16, -2126),
        ("Seagull Sector", 2556, -260, -2813),
        ("Thor's Helmet Sector", 2604, -120, -2570),
        ("Skull and Crossbones Neb. Sector", 13288, 4, -6863),
        ("Pencil Sector", 713, -98, -145),
        ("NGC 3199 Sector", 14477, -362, 3426),
        ("Eta Carina Sector", 8482, -242, 2606),
        ("Statue of Liberty Sector", 5489, -174, 2079),
        ("NGC 5367 Sector", 1248, 655, 1321),
        ("NGC 6188 Sector", 1604, -185, 3955),
        ("Cat's Paw Sector", 750, -43, 5333),
        ("NGC 6357 Sector", 864, 42, 7991),
        ("Trifid Sector", -734, -128, 5061),
        ("Lagoon Sector", -571, -195, 4374),
        ("Eagle Sector", -2147, -3, 6593),
        ("Omega Sector", -1533, -177, 5209),
        ("B133 Sector", -575, -212, 773),
        ("IC 1287 Sector", -459, -109, 833),
        ("R CrA Sector", -100, -229, 299),
        ("NGC 6820 Sector", -5678, -112, 3238),
        ("Crescent Sector", -4937, 109, 1150),
        ("Sadr Region Sector", -1895, -47, 265),
        ("Veil West Sector", -1496, -295, 318),
        ("North America Sector", -1994, -134, 49),
        ("B352 Sector", -1997, -91, 15),
        ("Pelican Sector", -1992, -97, 78),
        ("Veil East Sector", -2015, -406, 391),
        ("Iris Sector", -1511, 267, -455),
        ("Elephant's Trunk Sector", -2759, 74, -536),
        ("Cocoon Sector", -3276, -407, -345),
        ("Cave Sector", -2351, 8, -928),
        ("NGC 7538 Sector", -8473, 25, -3399),
        ("Bubble Sector", -6674, -76, -2783),
        ("Aries Dark Region", -194, -285, -358),
        ("Taurus Dark Region", -163, -204, -544),
        ("Orion Dark Region", 496, -412, -1441),
        ("Messier 78 Sector", 565, -496, -1501),
        ("Barnard's Loop Sector", 626, -466, -1478),
        ("Puppis Dark Region", 1340, -387, -407),
        ("Puppis Dark Region B Sector", 1252, -100, -463),
        ("Vela Dark Region", 891, -222, -152),
        ("Musca Dark Region", 315, -169, 149),
        ("Coalsack Sector", 318, -101, 173),
        ("Chamaeleon Sector", 383, -253, 201),
        ("Coalsack Dark Region", 350, -110, 159),
        ("Lupus Dark Region B Sector", 73, -19, 329),
        ("Lupus Dark Region", 58, 26, 312),
        ("Scorpius Dark Region", 10, -100, 377),
        ("IC 4604 Sector", -38, 82, 468),
        ("Pipe (stem) Sector", -88, -49, 397),
        ("Ophiuchus Dark Region B Sector", -143, 69, 389),
        ("Scutum Dark Region", -375, -89, 489),
        ("B92 Sector", -243, -107, 534),
        ("Snake Sector", -119, -27, 495),
        ("Pipe (bowl) Sector", -112, -64, 398),
        ("Ophiuchus Dark Region C Sector", -110, -37, 416),
        ("Rho Ophiuchi Sector", -48, 52, 373),
        ("Ophiuchus Dark Region", -57, 52, 395),
        ("Corona Austr. Dark Region", -109, -278, 388),
        ("Aquila Dark Region", -820, -118, 594),
        ("Vulpecula Dark Region", -644, -55, 253),
        ("Cepheus Dark Region", -1474, 143, -221),
        ("Cepheus Dark Region B Sector", -1046, 141, -319),
        ("Horsehead Dark Region", 408, -605, -1395),
        ("Parrot's Head Sector", -81, -191, 895),
        ("Struve's Lost Sector", -131, -279, -567),
        ("Bow-Tie Sector", -3086, 501, -1824),
        ("Skull Sector", -470, -1644, -305),
        ("Little Dumbbell Sector", -1661, -483, -1452),
        ("IC 289 Sector", -1219, -17, -1378),
        ("NGC 1360 Sector", 337, -1026, -614),
        ("NGC 1501 Sector", -2172, 313, -3016),
        ("NGC 1514 Sector", -303, -319, -908),
        ("NGC 1535 Sector", 1322, -2834, -2954),
        ("NGC 2022 Sector", 2834, -2067, -9882),
        ("IC 2149 Sector", -1789, 1212, -6976),
        ("IC 2165 Sector", 8924, -3107, -10373),
        ("Butterfly Sector", 1647, 88, -2532),
        ("NGC 2371/2 Sector", 561, 1397, -4185),
        ("Eskimo Sector", 134, 139, -827),
        ("NGC 2438 Sector", 2408, 128, -2074),
        ("NGC 2440 Sector", 4553, 138, -3383),
        ("NGC 2452 Sector", 9287, -284, -4801),
        ("IC 2448 Sector", 8357, -2456, 2293),
        ("NGC 2792 Sector", 8057, 486, -700),
        ("NGC 2818 Sector", 8222, 1171, -1270),
        ("NGC 2867 Sector", 12108, -1375, 1659),
        ("NGC 2899 Sector", 6334, -531, 712),
        ("IC 2501 Sector", 18654, -2007, 3545),
        ("Eight Burst Sector", 1949, 350, -25),
        ("IC 2553 Sector", 12755, -1362, 3465),
        ("NGC 3195 Sector", 4556, -1996, 2231),
        ("NGC 3211 Sector", 8697, -886, 2472),
        ("Ghost of Jupiter Sector", 1071, 643, -284),
        ("IC 2621 Sector", 14260, -1398, 5585),
        ("Owl Sector", -725, 1747, -1119),
        ("NGC 3699 Sector", 4050, 2, 1636),
        ("Blue planetary Sector", 4427, 309, 1982),
        ("NGC 4361 Sector", 3006, 3141, 1289),
        ("Lemon Slice Sector", -3186, 2448, -2158),
        ("IC 4191 Sector", 11711, -1305, 8048),
        ("Spiral Planetary Sector", 1315, -206, 974),
        ("NGC 5307 Sector", 5779, 1390, 5268),
        ("NGC 5315 Sector", 6399, -745, 5182),
        ("Retina Sector", 1767, 711, 2102),
        ("NGC 5873 Sector", 13691, 8570, 25091),
        ("NGC 5882 Sector", 4516, 1443, 7231),
        ("NGC 5979 Sector", 5343, -932, 7019),
        ("Fine Ring Sector", 413, -66, 757),
        ("NGC 6058 Sector", -5573, 6694, 2487),
        ("White Eyed Pea Sector", -3983, 7741, 8112),
        ("NGC 6153 Sector", 1570, 408, 5010),
        ("NGC 6210 Sector", -2962, 3148, 2957),
        ("IC 4634 Sector", -152, 1484, 7230),
        ("Bug Sector", 519, -35, 3242),
        ("Box Sector", -1860, 2658, 10192),
        ("NGC 6326 Sector", 3941, -1707, 10003),
        ("NGC 6337 Sector", 801, -195, 4715),
        ("Little Ghost Sector", -305, 403, 4769),
        ("IC 4663 Sector", 1423, -1028, 6150),
        ("NGC 6445 Sector", -733, 206, 4344),
        ("Cat's Eye Sector", -2910, 1526, -421),
        ("IC 4673 Sector", -941, -662, 13261),
        ("Red Spider Sector", -627, -64, 2853),
        ("NGC 6565 Sector", -460, -574, 5770),
        ("NGC 6563 Sector", -20, -494, 2973),
        ("NGC 6572 Sector", -4434, 1508, 6182),
        ("NGC 6567 Sector", -952, -152, 4012),
        ("IC 4699 Sector", 4037, -5025, 19364),
        ("NGC 6629 Sector", -1142, -669, 6189),
        ("NGC 6644 Sector", -1521, -1346, 9516),
        ("IC 4776 Sector", -956, -5662, 23230),
        ("Ring Sector", -2078, 452, 898),
        ("Phantom Streak Sector", -3712, -407, 5295),
        ("NGC 6751 Sector", -3206, -758, 5457),
        ("IC 4846 Sector", -11426, -4279, 21563),
        ("IC 1297 Sector", 115, -2972, 7149),
        ("NGC 6781 Sector", -3495, -367, 3696),
        ("NGC 6790 Sector", -2115, -463, 2488),
        ("NGC 6803 Sector", -4218, -508, 3820),
        ("NGC 6804 Sector", -3674, -501, 3374),
        ("Little Gem Sector", -2594, -1945, 5036),
        ("Blinking Sector", -2039, 343, 117),
        ("NGC 6842 Sector", -5577, -38, 2349),
        ("Dumbbell Sector", -1059, -171, 435),
        ("NGC 6852 Sector", -3377, -1352, 3463),
        ("NGC 6884 Sector", -2558, 209, 240),
        ("NGC 6879 Sector", -17125, -3272, 10871),
        ("NGC 6886 Sector", -7832, -1306, 4345),
        ("NGC 6891 Sector", -6841, -1882, 4761),
        ("IC 4997 Sector", -6782, -1627, 4026),
        ("Blue Flash Sector", -2700, 400, 1311),
        ("Fetus Sector", -2982, 177, -272),
        ("Saturn Sector", -2724, -3053, 3282),
        ("NGC 7026 Sector", -6099, -59, 4),
        ("NGC 7027 Sector", -3481, -308, 201),
        ("NGC 7048 Sector", -5697, -267, 17),
        ("IC 5117 Sector", -3089, -367, -95),
        ("IC 5148 Sector", -187, -2477, 1728),
        ("IC 5217 Sector", -9299, -985, -1822),
        ("Helix Sector", -323, -684, 204),
        ("NGC 7354 Sector", -4096, 68, -1383),
        ("Blue Snowball Sector", -5125, -1764, -1598),
        ("G2 Dust Cloud Sector", -73, -123, 27799),
        ("Regor Sector", 999, -247, -234),
        ("ICZ", -29, -210, -40),
        ("Bleia2", -555, -357, 36488),
        ("Bleia3", -555, -357, 35988),
        ("Bleia4", -62, -357, 36488),
        ("Bleia5", -962, -357, 36488),
        ("Bleia1", -555, -357, 36988),
        ("Bovomit", -20582, -422, -7442),
        ("Dryman", 18588, -492, 20648),
        ("Froadik", -19372, -712, 13788),
        ("Hyponia", -23532, -522, 23568),
        ("Praei3", -1512, -667, 53088),
        ("Praei1", -1512, -667, 53488),
        ("Praei2", -1512, -667, 53888),
        ("Praei4", -1512, -1067, 53488),
        ("Praei5", -1512, -57, 53488),
        ("Praei6", -1012, -612, 52988),
        ("Sidgoir", -24220, -90, -1320),
    ];

    /// <summary>The shape of a generated system name, whatever its sector (pgdata.py's pg_system_regex).</summary>
    [GeneratedRegex(@"^(?<sector>[\w\s'.()/-]+) (?<l1>[A-Za-z])(?<l2>[A-Za-z])-(?<l3>[A-Za-z]) (?<mcode>[A-Za-z])(?:(?<n1>\d+)-)?(?<n2>\d+)$")]
    private static partial Regex SystemNamePattern();
    // ---- generated tables: end ----
}
