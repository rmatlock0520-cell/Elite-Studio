// The nebulae and star clusters that Elite's makers placed by hand, each as one or more spheres
// laid over the game's grid of sectors. Inside one, systems are named after it ("Col 285 Sector
// VK-N c7-18") instead of after the grid's sector. The table between the two "generated" lines
// below comes from EDTS, the Elite Dangerous Travel Scripts (https://bitbucket.org/Esvandiary/edts:
// edtslib's pgdata.py and sector.py), as EDTS had it at its latest commit, of 17 December 2025. It
// is copied by a script (generate-ha-regions.cs), never by hand. The few spheres of the regions
// that need a permit are EDTS's rounded guesses, and the game itself has the last word on permits.
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

using System.Text.RegularExpressions;

namespace EliteStudio.Nav;

/// <summary>One sphere of a nebula's own sector, in light years. Most nebulae are one sphere; ICZ is sixteen.</summary>
public readonly record struct NebulaSphere(double X, double Y, double Z, double Radius)
{
    /// <summary>The point lies in the sphere. A point on its surface counts as inside, as EDTS counts it.</summary>
    public bool Contains(double x, double y, double z) => Math.Sqrt((X - x) * (X - x) + (Y - y) * (Y - y) + (Z - z) * (Z - z)) <= Radius;
}

/// <summary>
/// A nebula or star cluster with a sector of its own, placed by the game's makers rather than by the
/// grid's rule. On screen it is a nebula: drawn as its sphere, not as a box.
/// </summary>
/// <param name="Name">The name its systems carry, as the game spells it ("California Sector", "Horsehead Dark Region").</param>
/// <param name="Radius">EDTS's size for it, in light years: the radius of its sphere, or of each of its spheres.</param>
/// <param name="Permit">The game keeps commanders out without a permit (EDTS's list; the game has the last word).</param>
/// <param name="Spheres">What it is made of. Its systems lie inside one sphere or another.</param>
public sealed record Nebula(string Name, double Radius, bool Permit, IReadOnlyList<NebulaSphere> Spheres)
{
    /// <summary>Its middle (with <see cref="Y"/> and <see cref="Z"/>): the mean of its spheres' centres, as EDTS takes it.</summary>
    public double X => Spheres.Average(sphere => sphere.X);

    /// <inheritdoc cref="X"/>
    public double Y => Spheres.Average(sphere => sphere.Y);

    /// <inheritdoc cref="X"/>
    public double Z => Spheres.Average(sphere => sphere.Z);

    /// <summary>
    /// The name to show: the best-known name of the nebula when the list of other names has one
    /// ("California Nebula"), else the name without " Sector" ("Col 285", "Pleiades").
    /// </summary>
    public string Label => HaRegions.LabelOf(Name);

    /// <summary>The box round all its spheres: what "Look inside this nebula" counts.</summary>
    public Box Bounds => new(
        Spheres.Min(sphere => sphere.X - sphere.Radius), Spheres.Min(sphere => sphere.Y - sphere.Radius), Spheres.Min(sphere => sphere.Z - sphere.Radius),
        Spheres.Max(sphere => sphere.X + sphere.Radius), Spheres.Max(sphere => sphere.Y + sphere.Radius), Spheres.Max(sphere => sphere.Z + sphere.Radius));

    /// <summary>The point lies inside it: inside any of its spheres.</summary>
    public bool Contains(double x, double y, double z) => Spheres.Any(sphere => sphere.Contains(x, y, z));
}

/// <summary>
/// The 466 nebulae and star clusters with sectors of their own, as EDTS lists them (see the top of
/// this file). Where two overlap, EDTS takes it that the smaller one names the systems inside both;
/// that is EDTS's model of the game, not something the game's makers have written down.
/// </summary>
public static partial class HaRegions
{
    /// <summary>The date of the EDTS commit the table was copied from.</summary>
    public static readonly DateOnly Snapshot = new(2025, 12, 17);

    /// <summary>Every one of them, smallest first; those of one size in EDTS's order.</summary>
    public static IReadOnlyList<Nebula> All { get; }

    static readonly Dictionary<string, Nebula> ByKey = new(StringComparer.Ordinal);   // by every name it goes by, squeezed
    static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal);  // the first other name of each

    /// <summary>
    /// Other names players know some of them by, written by hand: the common name of the nebula
    /// first, then the sector that holds it. Each was checked on 14 October 2026 against EDAstro's
    /// list of nebulae, whose point for that name lies inside the sector's sphere. (EDTS's Flame
    /// Sector, Barnard's Loop Sector and Messier 78 Sector do not hold EDAstro's points of those
    /// names, so they get no other name here.)
    /// </summary>
    public static readonly IReadOnlyList<(string Alias, string Name)> Aliases =
    [
        ("California Nebula", "California Sector"), ("Orion Nebula", "Orion Sector"), ("Horsehead Nebula", "Horsehead Dark Region"),
        ("Witch Head Nebula", "Witch Head Sector"), ("Lagoon Nebula", "Lagoon Sector"), ("Eagle Nebula", "Eagle Sector"),
        ("Omega Nebula", "Omega Sector"), ("Trifid Nebula", "Trifid Sector"), ("Crab Nebula", "Crab Sector"),
        ("Rosette Nebula", "Rosette Sector"), ("Heart Nebula", "Heart Sector"), ("Soul Nebula", "Soul Sector"),
        ("North America Nebula", "North America Sector"), ("Pelican Nebula", "Pelican Sector"), ("Cone Nebula", "Cone Sector"),
        ("Running Man Nebula", "Running Man Sector"), ("Flaming Star Nebula", "Flaming Star Sector"), ("Jellyfish Nebula", "Jellyfish Sector"),
        ("Monkey Head Nebula", "Monkey Head Sector"), ("Seagull Nebula", "Seagull Sector"), ("Eta Carina Nebula", "Eta Carina Sector"),
        ("Cat's Paw Nebula", "Cat's Paw Sector"), ("Statue of Liberty Nebula", "Statue of Liberty Sector"), ("Crescent Nebula", "Crescent Sector"),
        ("Bubble Nebula", "Bubble Sector"), ("Cave Nebula", "Cave Sector"), ("Cocoon Nebula", "Cocoon Sector"), ("Iris Nebula", "Iris Sector"),
        ("Elephant's Trunk Nebula", "Elephant's Trunk Sector"), ("Helix Nebula", "Helix Sector"), ("Owl Nebula", "Owl Sector"),
        ("Ring Nebula", "Ring Sector"), ("Dumbbell Nebula", "Dumbbell Sector"), ("Little Dumbbell Nebula", "Little Dumbbell Sector"),
        ("Cat's Eye Nebula", "Cat's Eye Sector"), ("Skull Nebula", "Skull Sector"), ("Butterfly Nebula", "Butterfly Sector"),
        ("Bow-Tie Nebula", "Bow-Tie Sector"), ("Saturn Nebula", "Saturn Sector"), ("Blue Snowball Nebula", "Blue Snowball Sector"),
        ("Veil Nebula East", "Veil East Sector"), ("Veil Nebula West", "Veil West Sector"),
        ("Skull and Crossbones Nebula", "Skull and Crossbones Neb. Sector"), ("Spirograph Nebula", "Spirograph Sector"),
        ("Retina Nebula", "Retina Sector"), ("Red Spider Nebula", "Red Spider Sector"), ("Pencil Nebula", "Pencil Sector"),
        ("Little Gem Nebula", "Little Gem Sector"), ("Little Ghost Nebula", "Little Ghost Sector"), ("Lemon Slice Nebula", "Lemon Slice Sector"),
        ("Fine Ring Nebula", "Fine Ring Sector"), ("Blinking Nebula", "Blinking Sector"), ("Blue Flash Nebula", "Blue Flash Sector"),
        ("Box Nebula", "Box Sector"), ("Bug Nebula", "Bug Sector"), ("Fetus Nebula", "Fetus Sector"), ("Eight Burst Nebula", "Eight Burst Sector"),
        ("Spiral Planetary Nebula", "Spiral Planetary Sector"), ("Blue Planetary Nebula", "Blue planetary Sector"), ("Hind Nebula", "Hind Sector"),
        ("Struve's Lost Nebula", "Struve's Lost Sector"),
    ];

    // In a static constructor so that it runs after the table at the bottom of the file has been filled in.
    static HaRegions()
    {
        // EDTS sorts by size and keeps the order of those of one size; so does OrderBy.
        All = [.. Table.OrderBy(nebula => nebula.Radius)];
        foreach (Nebula nebula in All)
        {
            ByKey[Squeeze(nebula.Name)] = nebula;
            ByKey.TryAdd(Squeeze(Short(nebula.Name)), nebula);
        }
        foreach (var (alias, name) in Aliases)
        {
            ByKey.TryAdd(Squeeze(alias), ByKey[Squeeze(name)]);   // a name not in the table stops Studio at once, and a test sees it
            Labels.TryAdd(name, alias);
        }
    }

    /// <summary>
    /// Those a point lies inside, smallest first: the first is the one EDTS takes to name a system
    /// there. Empty in most of the galaxy.
    /// </summary>
    public static IReadOnlyList<Nebula> Containing(double x, double y, double z) => [.. All.Where(nebula => nebula.Contains(x, y, z))];

    /// <summary>
    /// The one with this name, or with this name without " Sector", or with one of its other names
    /// (<see cref="Aliases"/>); upper and lower case and extra spaces do not matter. Null for any other text.
    /// </summary>
    public static Nebula? Named(string? name) => name is null ? null : ByKey.GetValueOrDefault(Squeeze(name));

    /// <summary>
    /// Those whose name, name without " Sector" or other name matches what the player typed, best
    /// first: those it begins, then those with a word it begins, then those that hold it anywhere.
    /// Typing "Rosette Nebula" (or only "Rosette Neb") finds Rosette Sector too, after the others,
    /// because most of these nebulae are known by that name. Within each kind the nearest to
    /// <paramref name="from"/> come first, or without a place the names in order.
    /// </summary>
    public static IReadOnlyList<Nebula> Find(string? text, (double X, double Y, double Z)? from = null)
    {
        string query = Squeeze(text ?? "");
        if (query.Length == 0) return [];
        var best = new Dictionary<Nebula, int>();
        Rank(query, 0);
        // "rosette nebula", "rosette neb": the part before the last word, when that word begins "nebula".
        int space = query.LastIndexOf(' ');
        if (space > 0 && "nebula".StartsWith(query[(space + 1)..], StringComparison.Ordinal)) Rank(query[..space], 3);

        return [.. best.OrderBy(found => found.Value)
            .ThenBy(found => from is { } at ? Distance(found.Key, at) : 0)
            .ThenBy(found => found.Key.Name, StringComparer.OrdinalIgnoreCase)
            .Select(found => found.Key)];

        void Rank(string wanted, int after)
        {
            foreach (var (key, nebula) in ByKey)
            {
                int rank = key.StartsWith(wanted, StringComparison.Ordinal) ? 0
                    : key.Contains(" " + wanted, StringComparison.Ordinal) ? 1
                    : key.Contains(wanted, StringComparison.Ordinal) ? 2
                    : -1;
                if (rank >= 0 && (!best.TryGetValue(nebula, out int had) || rank + after < had)) best[nebula] = rank + after;
            }
        }

        static double Distance(Nebula nebula, (double X, double Y, double Z) at) =>
            Math.Sqrt((nebula.X - at.X) * (nebula.X - at.X) + (nebula.Y - at.Y) * (nebula.Y - at.Y) + (nebula.Z - at.Z) * (nebula.Z - at.Z));
    }

    /// <summary>The name to show for a nebula of this name: see <see cref="Nebula.Label"/>.</summary>
    internal static string LabelOf(string name) => Labels.GetValueOrDefault(name) ?? Short(name);

    /// <summary>The name without " Sector" at its end, the way players say it.</summary>
    static string Short(string name) => name.EndsWith(" Sector", StringComparison.Ordinal) ? name[..^" Sector".Length] : name;

    /// <summary>Text to compare: small letters, single spaces, none at either end.</summary>
    static string Squeeze(string text) => Spaces().Replace(text.Trim(), " ").ToLowerInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>A row of the table: the centre is worked out from the spheres, as EDTS does.</summary>
    static Nebula Region(string name, double size, bool permit, params (double X, double Y, double Z, double Radius)[] spheres) =>
        new(name, size, permit, [.. spheres.Select(sphere => new NebulaSphere(sphere.X, sphere.Y, sphere.Z, sphere.Radius))]);

    // ---- generated table: begin ----
    // Written by generate-ha-regions.cs on 2026-10-14 from EDTS's pgdata.py (SHA-256 55cd8f8885c2ef30)
    // and sector.py (SHA-256 9f2632a0ad9bb93e). Do not edit by hand: run the script again.

    /// <summary>
    /// The 466 sectors named by hand, in EDTS's own order (the static constructor sorts them by size, as
    /// EDTS does): name, size (the radius EDTS gives the whole region), whether a permit is needed, and the
    /// sphere or spheres it is made of, as centre x, y, z and radius in light years.
    /// </summary>
    static readonly Nebula[] Table =
    [
        Region("Trianguli Sector", 50.0, false, (60.85156, -47.94922, -81.32031, 50.0)),
        Region("Crucis Sector", 60.0, false, (75.91016, 8.32812, 44.83984, 60.0)),
        Region("Tascheter Sector", 50.0, false, (1.46094, -22.39844, -62.74023, 50.0)),
        Region("Hydrae Sector", 60.0, false, (77.57031, 84.07031, 69.47070, 60.0)),
        Region("Col 285 Sector", 326.0, false, (-53.46875, 56.27344, -19.35547, 326.0)),
        Region("Scorpii Sector", 60.0, false, (37.69141, 0.51953, 126.83008, 60.0)),
        Region("Shui Wei Sector", 80.0, false, (67.51172, -119.44922, 24.85938, 80.0)),
        Region("Shudun Sector", 30.0, false, (-3.51953, 34.16016, 12.98047, 30.0)),
        Region("Yin Sector", 50.0, false, (6.42969, 20.21094, -46.98047, 50.0)),
        Region("Jastreb Sector", 50.0, false, (-12.51953, 3.82031, -40.75000, 50.0)),
        Region("Pegasi Sector", 100.0, false, (-170.26953, -95.17188, -19.18945, 100.0)),
        Region("Cephei Sector", 50.0, false, (-107.98047, 30.05078, -42.23047, 50.0)),
        Region("Bei Dou Sector", 40.0, false, (-33.64844, 72.48828, -20.64062, 40.0)),
        Region("Puppis Sector", 50.0, false, (56.69141, 5.23828, -28.21094, 50.0)),
        Region("Sharru Sector", 50.0, false, (37.87891, 60.19922, -34.04297, 50.0)),
        Region("Alrai Sector", 70.0, false, (-38.60156, 23.42188, 68.25977, 70.0)),
        Region("Lyncis Sector", 70.0, false, (-68.51953, 65.10156, -141.03906, 70.0)),
        Region("Tucanae Sector", 100.0, false, (105.60938, -218.21875, 159.47070, 100.0)),
        Region("Piscium Sector", 60.0, false, (-44.83984, -54.75000, -29.10938, 60.0)),
        Region("Herculis Sector", 50.0, false, (-73.00000, 70.64844, 38.49023, 50.0)),
        Region("Antliae Sector", 70.0, false, (175.87109, 65.89062, 29.18945, 70.0)),
        Region("Arietis Sector", 80.0, false, (-72.16016, -76.82812, -135.36914, 80.0)),
        Region("Capricorni Sector", 60.0, false, (-58.37891, -119.78906, 107.34961, 60.0)),
        Region("Ceti Sector", 70.0, false, (-14.10156, -116.94922, -32.50000, 70.0)),
        Region("Core Sys Sector", 50.0, false, (0.00000, 0.00000, 0.00000, 50.0)),
        Region("Blanco 1 Sector", 231.0, false, (-42.28906, -864.69922, 157.82031, 231.0)),
        Region("NGC 129 Sector", 309.0, false, (-4571.64062, -231.18359, -2671.45117, 309.0)),
        Region("NGC 225 Sector", 100.0, false, (-1814.48828, -41.08203, -1133.81836, 100.0)),
        Region("NGC 188 Sector", 331.0, false, (-5187.57031, 2556.32422, -3343.16016, 331.0)),
        Region("IC 1590 Sector", 558.0, false, (-7985.20703, -1052.35156, -5205.49023, 558.0)),
        Region("NGC 457 Sector", 461.0, false, (-6340.41797, -593.83203, -4708.80859, 461.0)),
        Region("M103 Sector", 105.0, false, (-5639.37109, -224.90234, -4405.96094, 105.0)),
        Region("NGC 654 Sector", 97.0, false, (-5168.34375, -46.49609, -4200.19922, 97.0)),
        Region("NGC 659 Sector", 92.0, false, (-4882.00391, -165.43750, -4010.12305, 92.0)),
        Region("NGC 663 Sector", 260.0, false, (-4914.64062, -100.05469, -4051.31836, 260.0)),
        Region("Col 463 Sector", 200.0, false, (-1793.73438, 381.90234, -1371.41211, 200.0)),
        Region("NGC 752 Sector", 326.0, false, (-929.80469, -589.36328, -1004.09766, 326.0)),
        Region("NGC 744 Sector", 115.0, false, (-2892.49609, -425.51562, -2641.21289, 115.0)),
        Region("Stock 2 Sector", 130.0, false, (-718.91406, -32.82422, -679.84180, 130.0)),
        Region("h Persei Sector", 355.0, false, (-4817.47266, -437.52734, -4750.67383, 355.0)),
        Region("Chi Persei Sector", 401.0, false, (-5389.26172, -480.34766, -5408.10742, 401.0)),
        Region("IC 1805 Sector", 358.0, false, (-4370.87891, 96.60156, -4325.34375, 358.0)),
        Region("NGC 957 Sector", 190.0, false, (-4085.48438, -278.87109, -4275.21484, 190.0)),
        Region("Tr 2 Sector", 112.0, false, (-1431.65234, -144.19141, -1556.91211, 112.0)),
        Region("M34 Sector", 171.0, false, (-931.64062, -438.33984, -1263.64648, 171.0)),
        Region("NGC 1027 Sector", 147.0, false, (-1756.25391, 65.96484, -1805.99609, 147.0)),
        Region("IC 1848 Sector", 342.0, false, (-4436.20312, 102.57031, -4790.66406, 342.0)),
        Region("NGC 1245 Sector", 246.0, false, (-5101.33984, -1451.18359, -7736.58789, 246.0)),
        Region("NGC 1342 Sector", 95.0, false, (-884.15234, -576.25781, -1896.07422, 95.0)),
        Region("IC 348 Sector", 26.0, false, (-402.66016, -383.08203, -1130.80273, 26.0)),
        Region("Mel 22 Sector", 172.0, false, (-104.13672, -195.38672, -437.12695, 172.0)),
        Region("NGC 1444 Sector", 46.0, false, (-2065.66016, -88.70703, -3318.62500, 46.0)),
        Region("NGC 1502 Sector", 63.0, false, (-1572.28906, 359.08203, -2140.41211, 63.0)),
        Region("NGC 1528 Sector", 118.0, false, (-1183.84766, 13.24609, -2235.89648, 118.0)),
        Region("NGC 1545 Sector", 122.0, false, (-1038.79297, 8.09766, -2074.42578, 122.0)),
        Region("Hyades Sector", 144.0, false, (0.00000, -56.67578, -138.88086, 144.0)),
        Region("NGC 1647 Sector", 205.0, true, (11.76172, -508.69531, -1684.84180, 205.0)),
        Region("NGC 1662 Sector", 83.0, false, (178.12891, -512.99609, -1317.47070, 83.0)),
        Region("NGC 1664 Sector", 171.0, false, (-1227.67969, -27.29688, -3712.16406, 171.0)),
        Region("NGC 1746 Sector", 251.0, false, (-35.15625, -380.61719, -2014.04883, 251.0)),
        Region("NGC 1778 Sector", 98.0, false, (-921.61719, -167.16797, -4697.52930, 98.0)),
        Region("NGC 1817 Sector", 281.0, false, (665.49609, -1457.36719, -6227.20508, 281.0)),
        Region("NGC 1857 Sector", 109.0, false, (-1246.36328, 140.66016, -6071.80273, 109.0)),
        Region("NGC 1893 Sector", 343.0, false, (-1192.19141, -317.42969, -10628.63672, 343.0)),
        Region("M38 Sector", 203.0, false, (-466.23828, 42.51562, -3448.36328, 203.0)),
        Region("Col 69 Sector", 300.0, false, (366.92969, -299.39453, -1359.90039, 300.0)),
        Region("NGC 1981 Sector", 106.0, false, (578.95703, -423.23828, -1084.28711, 106.0)),
        Region("Trapezium Sector", 182.0, false, (594.46875, -431.80859, -1072.44922, 182.0)),
        Region("Col 70 Sector", 514.0, true, (508.68359, -372.59375, -1090.87891, 514.0)),
        Region("M36 Sector", 126.0, false, (-412.07422, 75.04688, -4279.55078, 126.0)),
        Region("M37 Sector", 184.0, false, (-180.73047, 243.89453, -4499.77148, 184.0)),
        Region("NGC 2129 Sector", 72.0, false, (567.78906, 8.62109, -4907.25391, 72.0)),
        Region("NGC 2169 Sector", 50.0, false, (921.21484, -173.53516, -3299.41602, 50.0)),
        Region("M35 Sector", 194.0, false, (305.50781, 102.11328, -2640.42383, 194.0)),
        Region("NGC 2175 Sector", 78.0, false, (940.29688, 37.07031, -5225.95117, 78.0)),
        Region("Col 89 Sector", 593.0, false, (603.48438, 273.61719, -4187.90430, 593.0)),
        Region("NGC 2232 Sector", 154.0, false, (655.20312, -154.73828, -956.90234, 154.0)),
        Region("Col 97 Sector", 250.0, true, (878.88281, -64.39062, -1850.92383, 250.0)),
        Region("NGC 2244 Sector", 412.0, false, (2092.95703, -164.37500, -4216.23242, 412.0)),
        Region("NGC 2251 Sector", 126.0, false, (1733.50781, 7.55859, -3967.84375, 126.0)),
        Region("Col 107 Sector", 578.0, false, (2591.42578, -89.05859, -5042.36914, 578.0)),
        Region("NGC 2264 Sector", 510.0, true, (851.16406, 83.68359, -2005.22070, 510.0)),
        Region("M41 Sector", 350.0, true, (1731.03125, -400.21094, -1396.76758, 350.0)),
        Region("NGC 2286 Sector", 385.0, true, (5456.35547, -379.24609, -7706.28711, 385.0)),
        Region("NGC 2281 Sector", 133.0, false, (-151.60938, 535.15234, -1732.92383, 133.0)),
        Region("NGC 2301 Sector", 116.0, false, (1530.08984, 14.87109, -2392.53125, 116.0)),
        Region("Col 121 Sector", 459.0, true, (1246.80469, -278.00000, -860.11328, 459.0)),
        Region("M50 Sector", 124.0, false, (2015.20703, -63.45703, -2261.81836, 124.0)),
        Region("NGC 2324 Sector", 78.0, false, (2088.35938, 218.74219, -3167.16211, 78.0)),
        Region("NGC 2335 Sector", 135.0, false, (3185.22266, -104.81641, -3344.81250, 135.0)),
        Region("NGC 2345 Sector", 257.0, false, (5319.95703, -294.56641, -5048.45312, 257.0)),
        Region("NGC 2343 Sector", 51.0, false, (2402.10547, -66.03906, -2461.52930, 51.0)),
        Region("NGC 2354 Sector", 500.0, false, (11248.28125, -1574.77344, -6919.98828, 500.0)),
        Region("NGC 2353 Sector", 192.0, false, (2567.32812, 25.48047, -2594.35547, 192.0)),
        Region("Col 132 Sector", 426.0, false, (1355.99609, -235.59766, -690.91602, 426.0)),
        Region("Col 135 Sector", 150.0, false, (942.32812, -198.29688, -365.50586, 150.0)),
        Region("NGC 2360 Sector", 233.0, false, (4695.94141, -150.25781, -3968.37891, 233.0)),
        Region("NGC 2362 Sector", 66.0, false, (3826.82812, -449.91797, -2381.99023, 66.0)),
        Region("NGC 2367 Sector", 77.0, false, (5384.37891, -433.42969, -3686.76172, 77.0)),
        Region("Col 140 Sector", 162.0, false, (1186.89453, -181.42578, -548.42188, 162.0)),
        Region("NGC 2374 Sector", 210.0, false, (3581.40625, 83.59766, -3179.72266, 210.0)),
        Region("NGC 2384 Sector", 101.0, false, (5674.66016, -288.94141, -3914.68555, 101.0)),
        Region("NGC 2395 Sector", 64.0, false, (674.53906, 404.00781, -1473.32031, 64.0)),
        Region("NGC 2414 Sector", 164.0, false, (8802.37109, 393.31641, -7026.83984, 164.0)),
        Region("M47 Sector", 117.0, false, (1241.61328, 86.52734, -1005.43945, 117.0)),
        Region("NGC 2423 Sector", 88.0, false, (1925.25391, 156.97656, -1587.05859, 88.0)),
        Region("Mel 71 Sector", 240.0, false, (7730.26562, 807.34375, -6743.53906, 240.0)),
        Region("NGC 2439 Sector", 330.0, false, (11484.73047, -964.35938, -5017.55664, 330.0)),
        Region("M46 Sector", 261.0, false, (3516.44531, 320.30859, -2757.24609, 261.0)),
        Region("M93 Sector", 99.0, false, (2930.09375, 11.79688, -1684.87891, 99.0)),
        Region("NGC 2451A Sector", 105.0, false, (757.34375, -93.33594, -240.24414, 105.0)),
        Region("NGC 2477 Sector", 175.0, false, (3808.06641, -403.21484, -1120.77539, 175.0)),
        Region("NGC 2467 Sector", 193.0, false, (3941.64844, 30.85547, -1999.71289, 193.0)),
        Region("NGC 2482 Sector", 153.0, false, (3850.51562, 152.85938, -2081.96484, 153.0)),
        Region("NGC 2483 Sector", 142.0, false, (4895.04688, 28.32812, -2303.43359, 142.0)),
        Region("NGC 2489 Sector", 263.0, false, (11855.98828, -180.25000, -5105.99414, 263.0)),
        Region("NGC 2516 Sector", 117.0, false, (1276.15234, -364.36719, 87.00000, 117.0)),
        Region("NGC 2506 Sector", 395.0, false, (8599.23047, 1962.22266, -7063.48828, 395.0)),
        Region("Col 173 Sector", 500.0, false, (1341.08203, -193.03516, -202.82031, 500.0)),
        Region("NGC 2527 Sector", 58.0, false, (1790.95312, 64.98438, -793.64062, 58.0)),
        Region("NGC 2533 Sector", 160.0, false, (10181.95312, 249.56250, -4155.17969, 160.0)),
        Region("NGC 2539 Sector", 117.0, false, (3519.28906, 856.72266, -2585.17578, 117.0)),
        Region("NGC 2547 Sector", 108.0, false, (1457.24609, -218.75781, -137.75000, 108.0)),
        Region("NGC 2546 Sector", 611.0, false, (2894.65234, -104.69922, -781.03711, 611.0)),
        Region("M48 Sector", 220.0, false, (1795.49219, 666.54688, -1622.35156, 220.0)),
        Region("NGC 2567 Sector", 144.0, false, (5126.51953, 286.27734, -1886.19336, 144.0)),
        Region("NGC 2571 Sector", 102.0, false, (4083.74219, -275.02344, -1559.42969, 102.0)),
        Region("NGC 2579 Sector", 89.0, false, (3250.51562, 17.64453, -889.24023, 89.0)),
        Region("Pismis 4 Sector", 102.0, false, (1912.67578, -80.82031, -245.01953, 102.0)),
        Region("NGC 2627 Sector", 193.0, false, (6248.08594, 773.52734, -2078.46094, 193.0)),
        Region("NGC 2645 Sector", 48.0, false, (5410.67188, -275.22656, -492.41016, 48.0)),
        Region("NGC 2632 Sector", 125.0, false, (221.48438, 327.75391, -464.35156, 125.0)),
        Region("IC 2391 Sector", 100.0, false, (565.85938, -68.47656, 3.95117, 100.0)),
        Region("IC 2395 Sector", 114.0, false, (2290.90234, -152.42969, -136.10547, 114.0)),
        Region("NGC 2669 Sector", 199.0, false, (3389.15234, -374.19531, 41.40820, 199.0)),
        Region("NGC 2670 Sector", 91.0, false, (3858.68750, -243.00000, -168.47461, 91.0)),
        Region("Tr 10 Sector", 57.0, false, (1369.04297, 14.44922, -172.95117, 57.0)),
        Region("M67 Sector", 216.0, false, (1466.01953, 1555.39453, -2047.71289, 216.0)),
        Region("IC 2488 Sector", 194.0, false, (3654.96484, -283.85938, 500.66797, 194.0)),
        Region("NGC 2910 Sector", 99.0, false, (8461.80469, -178.01172, 784.97852, 99.0)),
        Region("NGC 2925 Sector", 74.0, false, (2505.64453, -52.77344, 263.35352, 74.0)),
        Region("NGC 3114 Sector", 312.0, false, (2883.98828, -196.83203, 681.74609, 312.0)),
        Region("NGC 3228 Sector", 26.0, false, (1733.04688, 141.95312, 330.59570, 26.0)),
        Region("NGC 3247 Sector", 74.0, false, (4886.86328, -26.44141, 1272.93359, 74.0)),
        Region("IC 2581 Sector", 117.0, false, (7722.32031, 0.00000, 2011.51367, 117.0)),
        Region("NGC 3293 Sector", 133.0, false, (7299.60547, 13.24609, 2079.34766, 133.0)),
        Region("NGC 3324 Sector", 264.0, false, (7259.77734, -26.39062, 2109.16016, 264.0)),
        Region("NGC 3330 Sector", 43.0, false, (2824.55859, 193.51953, 714.72266, 43.0)),
        Region("Col 228 Sector", 293.0, false, (6846.64453, -125.30859, 2158.73828, 293.0)),
        Region("IC 2602 Sector", 155.0, false, (497.46484, -45.26953, 177.13867, 155.0)),
        Region("Tr 14 Sector", 130.0, false, (8501.81641, -93.30469, 2664.30664, 130.0)),
        Region("Tr 16 Sector", 254.0, false, (8311.20312, -106.53125, 2636.46875, 254.0)),
        Region("NGC 3519 Sector", 82.0, false, (4392.18359, -90.03516, 1642.16992, 82.0)),
        Region("Fe 1 Sector", 275.0, false, (3551.95312, 26.39062, 1292.80469, 275.0)),
        Region("NGC 3532 Sector", 232.0, false, (1497.35938, 41.62109, 533.18555, 232.0)),
        Region("NGC 3572 Sector", 95.0, false, (6089.70312, 22.72266, 2301.10742, 95.0)),
        Region("Col 240 Sector", 374.0, false, (4804.97656, 17.94141, 1825.23828, 374.0)),
        Region("NGC 3590 Sector", 47.0, false, (5015.87109, -18.78125, 1945.52734, 47.0)),
        Region("NGC 3680 Sector", 107.0, false, (2802.88672, 889.54688, 846.24219, 107.0)),
        Region("NGC 3766 Sector", 83.0, false, (5194.02734, 0.00000, 2323.40039, 83.0)),
        Region("IC 2944 Sector", 613.0, false, (5317.44531, -142.92969, 2434.51562, 613.0)),
        Region("Stock 14 Sector", 102.0, false, (6333.31641, -85.51953, 2980.23242, 102.0)),
        Region("NGC 4103 Sector", 93.0, false, (4713.57031, 111.41406, 2464.19336, 93.0)),
        Region("NGC 4349 Sector", 207.0, false, (6160.53516, 99.13281, 3528.17188, 207.0)),
        Region("Mel 111 Sector", 109.0, false, (21.80859, 308.30078, -23.96680, 109.0)),
        Region("NGC 4463 Sector", 512.0, false, (2938.90234, -119.35547, 1744.99219, 512.0)),
        Region("NGC 5281 Sector", 512.0, false, (2797.33984, -44.10156, 2281.45508, 512.0)),
        Region("NGC 4609 Sector", 512.0, false, (3387.39062, -6.96484, 2108.46484, 512.0)),
        Region("Jewel Box Sector", 188.0, false, (5383.63281, 280.91016, 3522.95117, 188.0)),
        Region("NGC 5138 Sector", 132.0, false, (5131.33984, 395.59375, 3937.41602, 132.0)),
        Region("NGC 5316 Sector", 250.0, false, (3024.62891, 6.91016, 2556.00781, 250.0)),
        Region("NGC 5460 Sector", 232.0, false, (1503.62891, 482.09766, 1546.21484, 232.0)),
        Region("NGC 5606 Sector", 52.0, false, (4178.73438, 102.79297, 4149.66406, 52.0)),
        Region("NGC 5617 Sector", 146.0, false, (3553.99219, -8.72656, 3516.96875, 146.0)),
        Region("NGC 5662 Sector", 190.0, false, (1479.93750, 132.47656, 1581.49609, 190.0)),
        Region("NGC 5822 Sector", 314.0, false, (1849.48438, 187.74219, 2341.85156, 314.0)),
        Region("NGC 5823 Sector", 136.0, false, (2435.16797, 169.67969, 3028.73828, 136.0)),
        Region("NGC 6025 Sector", 101.0, false, (1426.48047, -258.18359, 1999.84961, 101.0)),
        Region("NGC 6067 Sector", 189.0, false, (2322.23828, -177.35156, 3990.00586, 189.0)),
        Region("NGC 6087 Sector", 119.0, false, (1543.78906, -273.85547, 2451.49414, 119.0)),
        Region("NGC 6124 Sector", 195.0, false, (546.19922, 174.56250, 1568.46875, 195.0)),
        Region("NGC 6134 Sector", 53.0, false, (1264.10547, -10.40234, 2698.57812, 53.0)),
        Region("NGC 6152 Sector", 245.0, false, (1528.39062, -181.70312, 2986.73828, 245.0)),
        Region("NGC 6169 Sector", 105.0, false, (1261.91016, 156.59375, 3357.25586, 105.0)),
        Region("NGC 6167 Sector", 74.0, false, (1508.11328, -81.90234, 3278.87109, 74.0)),
        Region("NGC 6178 Sector", 49.0, false, (1218.22656, 69.32031, 3076.88477, 49.0)),
        Region("NGC 6193 Sector", 154.0, false, (1490.62500, -105.26562, 3461.19336, 154.0)),
        Region("NGC 6200 Sector", 234.0, false, (2509.40234, -128.62109, 6210.98633, 234.0)),
        Region("NGC 6208 Sector", 161.0, false, (1056.18750, -309.23047, 2855.24805, 161.0)),
        Region("NGC 6231 Sector", 165.0, false, (1150.01172, 84.81641, 3882.36914, 165.0)),
        Region("NGC 6242 Sector", 97.0, false, (923.09375, 154.51953, 3569.33203, 97.0)),
        Region("Tr 24 Sector", 500.0, false, (978.63281, 97.11719, 3577.28125, 500.0)),
        Region("NGC 6250 Sector", 83.0, false, (926.94531, -88.57812, 2661.82812, 83.0)),
        Region("NGC 6259 Sector", 118.0, false, (1037.94141, -87.95312, 3194.45508, 118.0)),
        Region("NGC 6281 Sector", 37.0, false, (329.46484, 54.44141, 1523.83984, 37.0)),
        Region("NGC 6322 Sector", 48.0, false, (823.50781, -175.75781, 3139.01953, 48.0)),
        Region("IC 4651 Sector", 85.0, false, (977.73438, -398.58984, 2700.95703, 85.0)),
        Region("NGC 6383 Sector", 187.0, false, (235.09375, 5.60156, 3201.37500, 187.0)),
        Region("M6 Sector", 93.0, false, (94.28906, -19.42578, 1587.08203, 93.0)),
        Region("NGC 6416 Sector", 99.0, false, (126.60547, -67.57031, 2415.74219, 99.0)),
        Region("IC 4665 Sector", 235.0, false, (-559.51953, 338.14453, 946.09570, 235.0)),
        Region("NGC 6425 Sector", 77.0, false, (96.70312, -73.71484, 2637.19922, 77.0)),
        Region("M7 Sector", 229.0, false, (69.85156, -76.89062, 974.47852, 229.0)),
        Region("M23 Sector", 179.0, false, (-348.48438, 103.71484, 2017.50000, 179.0)),
        Region("M20 Sector", 217.0, false, (-324.17188, -9.28516, 2640.15625, 217.0)),
        Region("NGC 6520 Sector", 90.0, false, (-259.73828, -251.08594, 5127.28906, 90.0)),
        Region("M21 Sector", 161.0, false, (-526.55469, -27.43750, 3894.46875, 161.0)),
        Region("NGC 6530 Sector", 177.0, false, (-461.04688, -106.03516, 4314.13867, 177.0)),
        Region("NGC 6546 Sector", 125.0, false, (-388.70312, -74.76172, 3034.29102, 125.0)),
        Region("NGC 6604 Sector", 81.0, false, (-1735.61328, 164.05469, 5248.01172, 81.0)),
        Region("M16 Sector", 100.0, false, (-1666.35547, 79.58594, 5450.40625, 100.0)),
        Region("M18 Sector", 62.0, false, (-1037.49219, -73.82422, 4100.12891, 62.0)),
        Region("M17 Sector", 309.0, false, (-1104.42969, -59.19922, 4093.20508, 309.0)),
        Region("NGC 6633 Sector", 72.0, false, (-717.30078, 175.43359, 983.66602, 72.0)),
        Region("M25 Sector", 177.0, false, (-473.52344, -158.48828, 1957.30859, 177.0)),
        Region("NGC 6664 Sector", 166.0, false, (-1545.53906, -33.16016, 3471.33984, 166.0)),
        Region("IC 4756 Sector", 184.0, false, (-933.74219, 143.19922, 1266.49805, 184.0)),
        Region("M26 Sector", 107.0, false, (-2112.12891, -264.09375, 4766.29297, 107.0)),
        Region("NGC 6705 Sector", 232.0, false, (-2803.58594, -298.96094, 5431.84570, 232.0)),
        Region("NGC 6709 Sector", 143.0, false, (-2349.81250, 287.60547, 2591.48047, 143.0)),
        Region("Col 394 Sector", 144.0, false, (-566.87109, -371.35547, 2145.51953, 144.0)),
        Region("Steph 1 Sector", 74.0, false, (-1125.68750, 339.39453, 480.14648, 74.0)),
        Region("NGC 6716 Sector", 100.0, false, (-672.92188, -428.59375, 2443.02734, 100.0)),
        Region("NGC 6755 Sector", 189.0, false, (-2887.29297, -137.35547, 3616.84766, 189.0)),
        Region("Stock 1 Sector", 243.0, false, (-902.64844, 41.73828, 514.86133, 243.0)),
        Region("NGC 6811 Sector", 162.0, false, (-3810.01172, 816.57031, 706.14453, 162.0)),
        Region("NGC 6819 Sector", 112.0, false, (-7320.41406, 1138.13281, 2099.09570, 112.0)),
        Region("NGC 6823 Sector", 108.0, false, (-5310.76953, -10.76953, 3140.78125, 108.0)),
        Region("NGC 6830 Sector", 187.0, false, (-4635.60938, -168.04688, 2665.59375, 187.0)),
        Region("NGC 6834 Sector", 99.0, false, (-6141.51172, 141.15234, 2772.99805, 99.0)),
        Region("NGC 6866 Sector", 138.0, false, (-4616.57812, 560.05078, 863.96875, 138.0)),
        Region("NGC 6871 Sector", 448.0, false, (-4891.96484, 187.98047, 1533.04883, 448.0)),
        Region("NGC 6885 Sector", 57.0, false, (-1769.88281, -139.42188, 806.58203, 57.0)),
        Region("IC 4996 Sector", 83.0, false, (-5466.14844, 128.18359, 1423.82617, 83.0)),
        Region("Mel 227 Sector", 57.0, false, (238.19531, -198.52734, 236.53906, 57.0)),
        Region("NGC 6910 Sector", 108.0, false, (-3635.86328, 129.47656, 726.51758, 108.0)),
        Region("M29 Sector", 109.0, false, (-3642.46875, 39.16406, 847.62891, 109.0)),
        Region("NGC 6939 Sector", 113.0, false, (-3751.41797, 822.29688, -387.67188, 113.0)),
        Region("NGC 6940 Sector", 183.0, false, (-2338.53906, -314.58594, 855.78320, 183.0)),
        Region("NGC 7039 Sector", 127.0, false, (-3096.74609, -91.96484, 108.14062, 127.0)),
        Region("NGC 7063 Sector", 59.0, false, (-2200.44141, -386.83984, 266.28320, 59.0)),
        Region("NGC 7082 Sector", 342.0, false, (-4692.53125, -245.98047, -98.29492, 342.0)),
        Region("M39 Sector", 93.0, false, (-1058.13672, -42.53906, -46.19922, 93.0)),
        Region("IC 1396 Sector", 500.0, false, (-2678.65234, 175.52734, -438.64648, 500.0)),
        Region("IC 5146 Sector", 73.0, false, (-2759.04688, -266.45312, -212.29688, 73.0)),
        Region("NGC 7160 Sector", 38.0, false, (-2478.12109, 286.47656, -617.86523, 38.0)),
        Region("NGC 7209 Sector", 200.0, false, (-3761.71875, -484.11719, -362.21289, 200.0)),
        Region("NGC 7235 Sector", 134.0, false, (-8983.79688, 128.58984, -2024.58594, 134.0)),
        Region("NGC 7243 Sector", 223.0, false, (-2595.76562, -257.61719, -406.48633, 223.0)),
        Region("NGC 7380 Sector", 422.0, false, (-6928.64453, -113.87891, -2131.52930, 422.0)),
        Region("NGC 7510 Sector", 99.0, false, (-6320.33984, 0.00000, -2426.15039, 99.0)),
        Region("M52 Sector", 203.0, false, (-4268.12109, 32.32422, -1794.15430, 203.0)),
        Region("NGC 7686 Sector", 133.0, false, (-3010.24609, -655.51562, -1065.98438, 133.0)),
        Region("NGC 7789 Sector", 555.0, false, (-6847.17578, -717.10547, -3265.93555, 555.0)),
        Region("NGC 7790 Sector", 336.0, false, (-8582.57422, -167.54297, -4297.83203, 336.0)),
        Region("IC 410 Sector", 150.0, false, (-1225.55469, -345.51953, -10926.05273, 150.0)),
        Region("NGC 3603 Sector", 150.0, true, (18594.82031, -174.53125, 7362.21094, 150.0)),
        Region("NGC 7822 Sector", 100.0, false, (-2443.97266, 302.39844, -1332.49805, 100.0)),
        Region("NGC 281 Sector", 100.0, false, (-6661.27734, -877.87500, -4342.43164, 100.0)),
        Region("LBN 623 Sector", 100.0, false, (-499.50781, -18.84766, -331.87109, 100.0)),
        Region("Heart Sector", 100.0, false, (-5321.12500, 117.80469, -5284.10547, 100.0)),
        Region("Soul Sector", 100.0, false, (-5095.17969, 117.80469, -5502.29492, 100.0)),
        Region("Pleiades Sector", 100.0, false, (-81.75391, -149.41406, -343.34766, 100.0)),
        Region("Perseus Dark Region", 100.0, false, (-359.89844, -316.98438, -1045.22461, 100.0)),
        Region("NGC 1333 Sector", 100.0, false, (-381.21094, -383.42969, -957.94531, 100.0)),
        Region("California Sector", 100.0, false, (-332.56641, -213.03125, -918.70508, 100.0)),
        Region("NGC 1491 Sector", 100.0, false, (-4908.28906, -174.52344, -8710.81152, 100.0)),
        Region("Hind Sector", 100.0, false, (-32.95312, -206.39062, -557.28516, 100.0)),
        Region("Trifid of the North Sector", 100.0, false, (-643.14844, -402.24609, -2486.87695, 100.0)),
        Region("Flaming Star Sector", 100.0, false, (-233.46875, -68.22266, -1682.50977, 100.0)),
        Region("NGC 1931 Sector", 100.0, false, (-743.83984, 36.65234, -6960.26953, 100.0)),
        Region("Crab Sector", 100.0, false, (558.51953, -707.39453, -6941.73242, 100.0)),
        Region("Running Man Sector", 100.0, false, (586.15625, -425.38281, -1079.56836, 100.0)),
        Region("Orion Sector", 100.0, false, (616.52344, -446.42578, -1107.67383, 100.0)),
        Region("Col 359 Sector", 566.0, false, (-393.00781, 175.31641, 686.22852, 566.0)),
        Region("Spirograph Sector", 100.0, false, (577.89844, -452.66406, -819.22266, 100.0)),
        Region("NGC 1999 Sector", 100.0, false, (549.36719, -374.51172, -926.56445, 100.0)),
        Region("Flame Sector", 100.0, false, (428.26172, -280.66797, -858.96289, 100.0)),
        Region("Horsehead Sector", 100.0, false, (411.68359, -272.99219, -811.47461, 100.0)),
        Region("Witch Head Sector", 100.0, false, (369.41406, -401.57812, -715.72852, 100.0)),
        Region("Monkey Head Sector", 100.0, false, (1133.31641, 44.67969, -6298.69922, 100.0)),
        Region("Jellyfish Sector", 100.0, false, (789.77734, 252.96484, -4930.74609, 100.0)),
        Region("Rosette Sector", 100.0, false, (2346.98438, -175.72266, -4748.76562, 100.0)),
        Region("Hubble's Variable Sector", 100.0, false, (1210.32422, 68.06250, -2744.17188, 100.0)),
        Region("Cone Sector", 100.0, true, (855.44141, 84.45312, -2025.11328, 100.0)),
        Region("Seagull Sector", 100.0, false, (2656.38672, -159.12891, -2712.61523, 100.0)),
        Region("Thor's Helmet Sector", 100.0, false, (2704.18750, -19.17578, -2469.26172, 100.0)),
        Region("Skull and Crossbones Neb. Sector", 100.0, false, (13388.46094, 104.71875, -6762.99805, 100.0)),
        Region("Pencil Sector", 100.0, false, (813.80078, 2.84375, -44.07422, 100.0)),
        Region("NGC 3199 Sector", 100.0, false, (14577.19531, -261.78516, 3526.59375, 100.0)),
        Region("Eta Carina Sector", 100.0, false, (8582.39453, -141.36719, 2706.01758, 100.0)),
        Region("Statue of Liberty Sector", 100.0, false, (5589.73047, -73.30078, 2179.34375, 100.0)),
        Region("NGC 5367 Sector", 100.0, false, (1348.62500, 755.99219, 1421.15430, 100.0)),
        Region("NGC 6188 Sector", 100.0, false, (1704.75391, -84.46875, 4055.45117, 100.0)),
        Region("Cat's Paw Sector", 100.0, false, (850.85938, 57.59375, 5433.48047, 100.0)),
        Region("NGC 6357 Sector", 100.0, false, (964.84375, 142.23828, 8091.43555, 100.0)),
        Region("Trifid Sector", 100.0, false, (-633.71094, -27.22656, 5161.16992, 100.0)),
        Region("Lagoon Sector", 100.0, false, (-470.27344, -94.24219, 4474.36719, 100.0)),
        Region("Eagle Sector", 100.0, false, (-2046.40234, 97.73438, 6693.48047, 100.0)),
        Region("Omega Sector", 100.0, false, (-1432.63672, -76.79297, 5309.58203, 100.0)),
        Region("B133 Sector", 100.0, false, (-474.18359, -111.46875, 873.33984, 100.0)),
        Region("IC 1287 Sector", 100.0, false, (-358.35547, -8.72656, 933.54492, 100.0)),
        Region("R CrA Sector", 100.0, false, (0.00000, -128.39062, 399.89453, 100.0)),
        Region("NGC 6820 Sector", 100.0, false, (-5577.41406, -11.34375, 3338.01367, 100.0)),
        Region("Crescent Sector", 100.0, false, (-4836.49219, 209.37891, 1250.80273, 100.0)),
        Region("Sadr Region Sector", 100.0, false, (-1794.68359, 53.71094, 365.84961, 100.0)),
        Region("Veil West Sector", 100.0, false, (-1395.62891, -194.41797, 418.70898, 100.0)),
        Region("North America Sector", 100.0, false, (-1893.85547, -33.16016, 149.04883, 100.0)),
        Region("B352 Sector", 100.0, false, (-1896.42969, 9.94922, 115.99023, 100.0)),
        Region("Pelican Sector", 100.0, false, (-1891.56641, 3.31641, 178.80469, 100.0)),
        Region("Veil East Sector", 100.0, false, (-1914.36328, -305.97266, 491.52539, 100.0)),
        Region("Iris Sector", 100.0, false, (-1410.35547, 367.96094, -354.25781, 100.0)),
        Region("Elephant's Trunk Sector", 100.0, false, (-2658.95703, 174.23828, -435.41992, 100.0)),
        Region("Cocoon Sector", 100.0, false, (-3175.87891, -306.70703, -244.37109, 100.0)),
        Region("Cave Sector", 100.0, false, (-2250.06641, 108.87109, -827.86328, 100.0)),
        Region("NGC 7538 Sector", 100.0, false, (-8372.94141, 125.66016, -3298.18945, 100.0)),
        Region("Bubble Sector", 100.0, false, (-6573.64062, 24.78516, -2682.65234, 100.0)),
        Region("Aries Dark Region", 100.0, false, (-93.57031, -184.53516, -257.08398, 100.0)),
        Region("Taurus Dark Region", 100.0, false, (-62.37891, -103.47656, -443.84766, 100.0)),
        Region("Orion Dark Region", 100.0, false, (596.77344, -311.86719, -1340.37305, 100.0)),
        Region("Messier 78 Sector", 100.0, false, (665.03125, -395.19922, -1400.55469, 100.0)),
        Region("Barnard's Loop Sector", 100.0, false, (726.50391, -365.36328, -1377.93555, 100.0)),
        Region("Puppis Dark Region", 100.0, false, (1440.26562, -286.21484, -306.13672, 100.0)),
        Region("Puppis Dark Region B Sector", 100.0, false, (1352.29688, 0.00000, -362.34570, 100.0)),
        Region("Vela Dark Region", 100.0, false, (991.18750, -121.87109, -51.94531, 100.0)),
        Region("Musca Dark Region", 100.0, false, (415.92578, -68.19531, 249.91211, 100.0)),
        Region("Coalsack Sector", 100.0, false, (418.85938, -0.87109, 273.05078, 100.0)),
        Region("Chamaeleon Sector", 100.0, false, (483.30078, -152.70312, 301.99805, 100.0)),
        Region("Coalsack Dark Region", 100.0, false, (450.26562, -9.07422, 259.96094, 100.0)),
        Region("Lupus Dark Region B Sector", 100.0, false, (173.39062, 81.61328, 429.15625, 100.0)),
        Region("Lupus Dark Region", 100.0, false, (158.46484, 126.79297, 412.81055, 100.0)),
        Region("Scorpius Dark Region", 100.0, false, (110.22656, 0.00000, 477.44141, 100.0)),
        Region("IC 4604 Sector", 100.0, false, (62.72266, 182.41797, 568.14453, 100.0)),
        Region("Pipe (stem) Sector", 100.0, false, (12.15234, 51.39453, 497.20312, 100.0)),
        Region("Ophiuchus Dark Region B Sector", 100.0, false, (-42.85156, 169.29688, 489.79883, 100.0)),
        Region("Scutum Dark Region", 100.0, false, (-274.66016, 11.34375, 589.00977, 100.0)),
        Region("B92 Sector", 100.0, false, (-142.89062, -6.80859, 634.06250, 100.0)),
        Region("Snake Sector", 100.0, false, (-18.70703, 73.12109, 595.23438, 100.0)),
        Region("Pipe (bowl) Sector", 100.0, false, (-11.31250, 36.61719, 498.52930, 100.0)),
        Region("Ophiuchus Dark Region C Sector", 100.0, false, (-9.00781, 63.37109, 516.04492, 100.0)),
        Region("Rho Ophiuchi Sector", 100.0, false, (52.26953, 152.01562, 473.45508, 100.0)),
        Region("Ophiuchus Dark Region", 100.0, false, (43.33984, 152.03516, 495.38672, 100.0)),
        Region("Corona Austr. Dark Region", 100.0, false, (-8.52734, -177.85156, 488.56641, 100.0)),
        Region("Aquila Dark Region", 100.0, false, (-719.23047, -17.45312, 694.55273, 100.0)),
        Region("Vulpecula Dark Region", 100.0, false, (-543.80859, 45.33984, 353.15234, 100.0)),
        Region("Cepheus Dark Region", 100.0, false, (-1373.48438, 243.10938, -120.16406, 100.0)),
        Region("Cepheus Dark Region B Sector", 100.0, false, (-945.42578, 241.92188, -218.26953, 100.0)),
        Region("Horsehead Dark Region", 200.0, true, (608.46094, -404.64453, -1194.16992, 200.0)),
        Region("Parrot's Head Sector", 100.0, false, (19.11719, -90.63281, 995.70117, 100.0)),
        Region("Struve's Lost Sector", 100.0, false, (-30.95703, -178.36719, -466.07617, 100.0)),
        Region("Bow-Tie Sector", 100.0, false, (-2985.95312, 601.75000, -1723.94141, 100.0)),
        Region("Skull Sector", 100.0, false, (-369.61719, -1543.29297, -204.04102, 100.0)),
        Region("Little Dumbbell Sector", 100.0, false, (-1560.71484, -382.69531, -1351.93164, 100.0)),
        Region("IC 289 Sector", 100.0, false, (-1118.43359, 83.04297, -1277.57812, 100.0)),
        Region("NGC 1360 Sector", 100.0, false, (437.24219, -925.14844, -513.75586, 100.0)),
        Region("NGC 1501 Sector", 100.0, false, (-2071.58984, 413.77344, -2915.01367, 100.0)),
        Region("NGC 1514 Sector", 100.0, false, (-202.23438, -218.68750, -807.39844, 100.0)),
        Region("NGC 1535 Sector", 100.0, false, (1422.89844, -2733.25000, -2853.89062, 100.0)),
        Region("NGC 2022 Sector", 100.0, false, (2934.63281, -1966.59375, -9781.63867, 100.0)),
        Region("IC 2149 Sector", 100.0, false, (-1688.68359, 1312.09766, -6875.08203, 100.0)),
        Region("IC 2165 Sector", 100.0, false, (9024.47656, -3006.29297, -10272.34375, 100.0)),
        Region("Butterfly Sector", 100.0, false, (1747.16797, 188.37109, -2431.44336, 100.0)),
        Region("NGC 2371/2 Sector", 100.0, false, (661.47266, 1497.67188, -4084.04688, 100.0)),
        Region("Eskimo Sector", 100.0, false, (234.63281, 239.23438, -726.43945, 100.0)),
        Region("NGC 2438 Sector", 100.0, false, (2508.30469, 228.79297, -1973.84180, 100.0)),
        Region("NGC 2440 Sector", 100.0, false, (4653.64062, 238.69141, -3282.78125, 100.0)),
        Region("NGC 2452 Sector", 100.0, false, (9387.19141, -183.25000, -4700.75391, 100.0)),
        Region("IC 2448 Sector", 100.0, false, (8457.82422, -2355.25391, 2393.32227, 100.0)),
        Region("NGC 2792 Sector", 100.0, false, (8157.05078, 586.27734, -599.01562, 100.0)),
        Region("NGC 2818 Sector", 100.0, false, (8322.63672, 1271.05078, -1169.66992, 100.0)),
        Region("NGC 2867 Sector", 100.0, false, (12208.21094, -1274.62891, 1759.23047, 100.0)),
        Region("NGC 2899 Sector", 100.0, false, (6434.56641, -430.78125, 812.87500, 100.0)),
        Region("IC 2501 Sector", 100.0, false, (18754.05469, -1906.93750, 3645.41797, 100.0)),
        Region("Eight Burst Sector", 100.0, false, (2049.63281, 450.94531, 75.15625, 100.0)),
        Region("IC 2553 Sector", 100.0, false, (12855.33984, -1261.05078, 3565.10156, 100.0)),
        Region("NGC 3195 Sector", 100.0, false, (4656.55469, -1895.47656, 2331.83008, 100.0)),
        Region("NGC 3211 Sector", 100.0, false, (8797.93750, -785.83594, 2572.69727, 100.0)),
        Region("Ghost of Jupiter Sector", 100.0, false, (1171.69141, 743.95703, -183.48242, 100.0)),
        Region("IC 2621 Sector", 100.0, false, (14360.99219, -1297.00781, 5685.91992, 100.0)),
        Region("Owl Sector", 100.0, false, (-624.37891, 1847.16406, -1018.89062, 100.0)),
        Region("NGC 3699 Sector", 100.0, false, (4150.35156, 102.09375, 1736.13086, 100.0)),
        Region("Blue planetary Sector", 100.0, false, (4527.26562, 409.69141, 2082.31055, 100.0)),
        Region("NGC 4361 Sector", 100.0, false, (3106.92969, 3241.21094, 1389.79688, 100.0)),
        Region("Lemon Slice Sector", 100.0, false, (-3085.35938, 2548.82812, -2057.67773, 100.0)),
        Region("IC 4191 Sector", 100.0, false, (11811.59375, -1204.96094, 8148.27148, 100.0)),
        Region("Spiral Planetary Sector", 100.0, false, (1415.32812, -105.56641, 1074.29297, 100.0)),
        Region("NGC 5307 Sector", 100.0, false, (5879.41797, 1490.00781, 5368.64453, 100.0)),
        Region("NGC 5315 Sector", 100.0, false, (6499.57812, -644.44141, 5282.06250, 100.0)),
        Region("Retina Sector", 100.0, false, (1867.97656, 811.80078, 2202.64258, 100.0)),
        Region("NGC 5873 Sector", 100.0, false, (13791.82031, 8670.95312, 25191.27344, 100.0)),
        Region("NGC 5882 Sector", 100.0, false, (4616.64062, 1543.22656, 7331.10352, 100.0)),
        Region("NGC 5979 Sector", 100.0, false, (5443.01172, -831.33594, 7119.16406, 100.0)),
        Region("Fine Ring Sector", 100.0, false, (513.22656, 34.89844, 857.54297, 100.0)),
        Region("NGC 6058 Sector", 100.0, false, (-5472.94922, 6794.40625, 2587.05273, 100.0)),
        Region("White Eyed Pea Sector", 100.0, false, (-3882.09375, 7841.04688, 8212.63281, 100.0)),
        Region("NGC 6153 Sector", 100.0, false, (1670.20703, 508.18359, 5110.00586, 100.0)),
        Region("NGC 6210 Sector", 100.0, false, (-2861.42969, 3248.40625, 3057.78906, 100.0)),
        Region("IC 4634 Sector", 100.0, false, (-51.17578, 1584.93750, 7330.44141, 100.0)),
        Region("Bug Sector", 100.0, false, (619.48828, 65.26953, 3342.45117, 100.0)),
        Region("Box Sector", 100.0, false, (-1759.31250, 2758.81250, 10292.41406, 100.0)),
        Region("NGC 6326 Sector", 100.0, false, (4041.22266, -1606.91406, 10103.77734, 100.0)),
        Region("NGC 6337 Sector", 100.0, false, (901.19531, -94.06641, 4815.49609, 100.0)),
        Region("Little Ghost Sector", 100.0, false, (-204.10547, 503.68359, 4869.76758, 100.0)),
        Region("IC 4663 Sector", 100.0, false, (1523.71094, -927.08984, 6250.50586, 100.0)),
        Region("NGC 6445 Sector", 100.0, false, (-632.58594, 306.07031, 4444.78906, 100.0)),
        Region("Cat's Eye Sector", 100.0, false, (-2809.64062, 1626.06641, -320.11719, 100.0)),
        Region("IC 4673 Sector", 100.0, true, (-840.65625, -561.13281, 13361.82812, 100.0)),
        Region("Red Spider Sector", 100.0, false, (-526.06250, 36.65234, 2953.28906, 100.0)),
        Region("NGC 6565 Sector", 100.0, false, (-359.02734, -473.17188, 5870.02539, 100.0)),
        Region("NGC 6563 Sector", 100.0, false, (80.49219, -393.89844, 3073.81836, 100.0)),
        Region("NGC 6572 Sector", 100.0, false, (-4333.99219, 1608.39453, 6282.48047, 100.0)),
        Region("NGC 6567 Sector", 100.0, false, (-851.64453, -51.31250, 4112.42969, 100.0)),
        Region("IC 4699 Sector", 100.0, false, (4137.37891, -4924.67578, 19464.83203, 100.0)),
        Region("NGC 6629 Sector", 100.0, false, (-1041.14844, -568.92188, 6289.06445, 100.0)),
        Region("NGC 6644 Sector", 100.0, false, (-1420.00781, -1245.23438, 9616.28516, 100.0)),
        Region("IC 4776 Sector", 100.0, false, (-855.50781, -5561.94922, 23330.94141, 100.0)),
        Region("Ring Sector", 100.0, false, (-1977.24219, 552.30859, 998.77734, 100.0)),
        Region("Phantom Streak Sector", 100.0, false, (-3611.90625, -306.19141, 5395.40234, 100.0)),
        Region("NGC 6751 Sector", 100.0, false, (-3105.76172, -657.87109, 5557.10742, 100.0)),
        Region("IC 4846 Sector", 100.0, false, (-11325.47656, -4178.53516, 21663.64062, 100.0)),
        Region("IC 1297 Sector", 100.0, false, (215.14844, -2871.37109, 7249.06445, 100.0)),
        Region("NGC 6781 Sector", 100.0, false, (-3394.65625, -266.91406, 3796.71680, 100.0)),
        Region("NGC 6790 Sector", 100.0, false, (-2014.89844, -362.12500, 2588.25195, 100.0)),
        Region("NGC 6803 Sector", 100.0, false, (-4117.21484, -407.53516, 3920.77148, 100.0)),
        Region("NGC 6804 Sector", 100.0, false, (-3573.00781, -400.99609, 3474.59766, 100.0)),
        Region("Little Gem Sector", 100.0, false, (-2493.94922, -1844.14062, 5136.08398, 100.0)),
        Region("Blinking Sector", 100.0, false, (-1938.14453, 443.09766, 217.39844, 100.0)),
        Region("NGC 6842 Sector", 100.0, false, (-5476.70312, 62.83203, 2449.84766, 100.0)),
        Region("Dumbbell Sector", 100.0, false, (-958.21094, -70.98438, 535.52734, 100.0)),
        Region("NGC 6852 Sector", 100.0, false, (-3276.57812, -1251.89844, 3563.25391, 100.0)),
        Region("NGC 6884 Sector", 100.0, false, (-2457.28516, 309.00391, 340.97656, 100.0)),
        Region("NGC 6879 Sector", 100.0, false, (-17024.14453, -3171.56250, 10971.31250, 100.0)),
        Region("NGC 6886 Sector", 100.0, false, (-7731.72266, -1205.87500, 4445.93750, 100.0)),
        Region("NGC 6891 Sector", 100.0, false, (-6740.87891, -1781.75781, 4861.67578, 100.0)),
        Region("IC 4997 Sector", 100.0, false, (-6681.43359, -1526.47266, 4126.53711, 100.0)),
        Region("Blue Flash Sector", 100.0, false, (-2599.53125, 500.30469, 1411.42969, 100.0)),
        Region("Fetus Sector", 100.0, false, (-2881.56641, 277.95312, -171.19727, 100.0)),
        Region("Saturn Sector", 100.0, false, (-2623.43359, -2952.78906, 3382.10742, 100.0)),
        Region("NGC 7026 Sector", 100.0, false, (-5998.94141, 41.88672, 104.71094, 100.0)),
        Region("NGC 7027 Sector", 100.0, false, (-3380.22266, -207.56641, 301.67773, 100.0)),
        Region("NGC 7048 Sector", 100.0, false, (-5596.30859, -166.13281, 117.22656, 100.0)),
        Region("IC 5117 Sector", 100.0, false, (-2988.11719, -266.68359, 5.21484, 100.0)),
        Region("IC 5148 Sector", 100.0, false, (-86.22656, -2376.86719, 1828.40430, 100.0)),
        Region("IC 5217 Sector", 100.0, false, (-9198.58594, -884.61719, -1721.46875, 100.0)),
        Region("Helix Sector", 100.0, false, (-222.85938, -583.28516, 304.50195, 100.0)),
        Region("NGC 7354 Sector", 100.0, false, (-3995.72266, 168.55469, -1282.88672, 100.0)),
        Region("Blue Snowball Sector", 100.0, false, (-5024.05469, -1663.03516, -1497.73438, 100.0)),
        Region("G2 Dust Cloud Sector", 100.0, false, (27.12500, -22.49609, 27899.97656, 100.0)),
        Region("Regor Sector", 100.0, true, (1099.23828, -146.67188, -133.58008, 100.0)),
        Region("ICZ", 40, false,
            (11, -118, 56, 40),
            (17, -122, 32, 40),
            (32, -170, 13, 40),
            (34, -115, 100, 40),
            (45, -118, 85, 40),
            (53, -130, 14, 40),
            (62, -105, 22, 40),
            (65, -117, 47, 40),
            (67, -119, 24, 40),
            (75, -135, 19, 40),
            (78, -100, 16, 40),
            (79, -167, 25, 40),
            (81, -150, 96, 40),
            (82, -131, 0, 40),
            (92, -95, 11, 40),
            (106, -95, 0, 40)),
        Region("Bleia2", 512, true, (-43, 155, 37000, 512)),
        Region("Bleia3", 512, true, (-43, 155, 36500, 512)),
        Region("Bleia4", 512, true, (450, 155, 37000, 512)),
        Region("Bleia5", 512, true, (-450, 155, 37000, 512)),
        Region("Bleia1", 512, true, (-43, 155, 37500, 512)),
        Region("Bovomit", 512, true, (-20070, 90, -6930, 512)),
        Region("Dryman", 512, true, (19100, 20, 21160, 512)),
        Region("Froadik", 512, true, (-18860, -200, 14300, 512)),
        Region("Hyponia", 512, true, (-23020, -10, 24080, 512)),
        Region("Praei3", 512, true, (-1000, -155, 53600, 512)),
        Region("Praei1", 512, true, (-1000, -155, 54000, 512)),
        Region("Praei2", 512, true, (-1000, -155, 54400, 512)),
        Region("Praei4", 512, true, (-1000, -555, 54000, 512)),
        Region("Praei5", 512, true, (-1000, 455, 54000, 512)),
        Region("Praei6", 512, true, (-500, -100, 53500, 512)),
        Region("Sidgoir", 100, true, (-24120, 10, -1220, 100)),
    ];
    // ---- generated table: end ----
}
