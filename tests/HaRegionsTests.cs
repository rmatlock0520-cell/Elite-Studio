using EliteStudio.Core;
using EliteStudio.Nav;

namespace EliteStudio.Tests;

/// <summary>
/// The nebulae and star clusters with sectors of their own, from EDTS's table (as EDTS had it on
/// 17 December 2025). The figures are EDTS's own, checked while the 3D map was planned (14 October
/// 2026): the player at -167.09, 26.72, -55.38 lies inside Col 285 Sector, 123 ly from its middle
/// and well inside its 326 ly, and inside no other.
/// </summary>
public sealed class HaRegionsTests
{
    static readonly (double X, double Y, double Z) Player = (-167.09375, 26.71875, -55.375);

    [Fact]
    public void The_table_holds_EDTSs_466_nebulae_smallest_first()
    {
        Assert.Equal(466, HaRegions.All.Count);
        Assert.Equal(466, HaRegions.All.Select(nebula => nebula.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(HaRegions.All.Select(nebula => nebula.Radius).Order(), HaRegions.All.Select(nebula => nebula.Radius));
        Assert.Equal(28, HaRegions.All.Count(nebula => nebula.Permit));
        Assert.Equal(new DateOnly(2025, 12, 17), HaRegions.Snapshot);

        // A row as EDTS writes it, and the one nebula made of many spheres: its middle is the mean of theirs.
        Nebula col285 = HaRegions.Named("Col 285 Sector")!;
        Assert.Equal((326.0, -53.46875, 56.27344, -19.35547), (col285.Radius, col285.X, col285.Y, col285.Z));
        Assert.Equal(new Box(-53.46875 - 326, 56.27344 - 326, -19.35547 - 326, -53.46875 + 326, 56.27344 + 326, -19.35547 + 326), col285.Bounds);
        Nebula icz = HaRegions.Named("ICZ")!;
        Assert.Equal(16, icz.Spheres.Count);
        Assert.Equal((61.1875, -124.1875, 35.0), (icz.X, icz.Y, icz.Z));
        Assert.Equal(new Box(11 - 40, -170 - 40, 0 - 40, 106 + 40, -95 + 40, 100 + 40), icz.Bounds);
        Assert.True(icz.Contains(106, -95, -40));    // on the surface of one of its spheres: inside, as EDTS counts it
        Assert.False(icz.Contains(106, -95, -40.01));
    }

    [Fact]
    public void The_player_is_inside_Col_285_Sector_and_no_other()
    {
        Nebula only = Assert.Single(HaRegions.Containing(Player.X, Player.Y, Player.Z));
        Assert.Equal(("Col 285 Sector", 326.0, false), (only.Name, only.Radius, only.Permit));
        double away = ShipMath.Distance([Player.X, Player.Y, Player.Z], [only.X, only.Y, only.Z]);
        Assert.InRange(away, 122, 123);

        // Far from every nebula there is none.
        Assert.Empty(HaRegions.Containing(-9530.5, -910.28125, 19808.125));   // Colonia
    }

    [Fact]
    public void Where_nebulae_overlap_the_smallest_comes_first_as_the_one_that_names_the_systems()
    {
        // The Pleiades' middle lies in three: the Pleiades' own sector first, of 100 ly like Aries Dark Region
        // but before it in EDTS's order, then Mel 22 Sector, the cluster's catalogue name, of 172 ly.
        Nebula pleiades = HaRegions.Named("Pleiades")!;
        Assert.Equal(["Pleiades Sector", "Aries Dark Region", "Mel 22 Sector"], HaRegions.Containing(pleiades.X, pleiades.Y, pleiades.Z).Select(nebula => nebula.Name));

        // Sol: in Jastreb Sector and Core Sys Sector, both of 50 ly (in EDTS's order), and Col 285 Sector round them.
        Assert.Equal(["Jastreb Sector", "Core Sys Sector", "Col 285 Sector"], HaRegions.Containing(0, 0, 0).Select(nebula => nebula.Name));

        // The middle of the Horsehead Dark Region lies in four, two of which need a permit.
        Nebula horsehead = HaRegions.Named("Horsehead Dark Region")!;
        IReadOnlyList<Nebula> there = HaRegions.Containing(horsehead.X, horsehead.Y, horsehead.Z);
        Assert.Equal(["Orion Sector", "Trapezium Sector", "Horsehead Dark Region", "Col 70 Sector"], there.Select(nebula => nebula.Name));
        Assert.Equal([false, false, true, true], there.Select(nebula => nebula.Permit));
    }

    [Fact]
    public void Some_nebulae_need_a_permit()
    {
        Assert.True(HaRegions.Named("Horsehead Dark Region")!.Permit);
        Assert.True(HaRegions.Named("Cone Sector")!.Permit);
        Assert.True(HaRegions.Named("hyponia")!.Permit);       // one of the permit areas far out, a rounded sphere of 512 ly
        Assert.False(HaRegions.Named("Horsehead Sector")!.Permit);
        Assert.False(HaRegions.Named("California Sector")!.Permit);
    }

    [Fact]
    public void A_nebula_is_known_by_its_name_by_its_name_without_Sector_and_by_the_name_players_know()
    {
        Nebula california = HaRegions.Named("California Sector")!;
        Assert.Same(california, HaRegions.Named("california"));
        Assert.Same(california, HaRegions.Named("  CALIFORNIA   NEBULA "));
        Assert.Equal("California Nebula", california.Label);
        Assert.Equal("Horsehead Nebula", HaRegions.Named("Horsehead Nebula")!.Label);
        Assert.Equal("Horsehead Dark Region", HaRegions.Named("Horsehead Nebula")!.Name);
        Assert.Equal("Skull and Crossbones Neb. Sector", HaRegions.Named("Skull and Crossbones Nebula")!.Name);
        // Without another name, the name without " Sector".
        Assert.Equal("Col 285", HaRegions.Named("Col 285 Sector")!.Label);
        Assert.Equal("Pleiades", HaRegions.Named("Pleiades")!.Label);
        Assert.Equal("ICZ", HaRegions.Named("ICZ")!.Label);

        Assert.Null(HaRegions.Named("Wredguia"));
        Assert.Null(HaRegions.Named(""));
        Assert.Null(HaRegions.Named(null));
        // Every other name leads to a nebula of the table, and the first of a nebula's other names is its label.
        foreach (var (alias, name) in HaRegions.Aliases)
            Assert.Equal(name, HaRegions.Named(alias)?.Name);
        Assert.Equal(HaRegions.Aliases.Count, HaRegions.Aliases.Select(alias => alias.Alias).Distinct().Count());
    }

    [Fact]
    public void A_search_puts_names_that_begin_with_the_text_first_then_words_then_the_rest_nearest_first()
    {
        Assert.Equal("California Sector", HaRegions.Find("calif")[0].Name);
        Assert.Equal("Witch Head Sector", HaRegions.Find("Witch")[0].Name);

        // Both Horseheads begin with it. From the player the plain Horsehead Sector is nearer; with no place, in order of name.
        Assert.Equal(["Horsehead Sector", "Horsehead Dark Region"], HaRegions.Find("horse", Player).Take(2).Select(nebula => nebula.Name));
        Assert.Equal(["Horsehead Dark Region", "Horsehead Sector"], HaRegions.Find("horse").Take(2).Select(nebula => nebula.Name));

        // "head": no name begins with it, three have a word that does, and the Horseheads hold it inside a word.
        IReadOnlyList<Nebula> heads = HaRegions.Find("head");
        Assert.Equal(["Monkey Head Sector", "Parrot's Head Sector", "Witch Head Sector"], heads.Take(3).Select(nebula => nebula.Name));
        Assert.Equal(["Horsehead Dark Region", "Horsehead Sector"], heads.Skip(3).Select(nebula => nebula.Name).Order());

        // Most nebulae are known as "<name> Nebula", even without another name written down for them.
        Assert.Equal("Rosette Sector", HaRegions.Find("Rosette Nebula")[0].Name);
        Assert.Equal("Rosette Sector", HaRegions.Find("rosette neb")[0].Name);
        Assert.Equal("Iris Sector", HaRegions.Find("Iris Nebula")[0].Name);

        Assert.Empty(HaRegions.Find(""));
        Assert.Empty(HaRegions.Find("   "));
        Assert.Empty(HaRegions.Find(null));
        Assert.Empty(HaRegions.Find("no such nebula"));
        Assert.Equal(HaRegions.Find("col 2").Count, HaRegions.Find("col 2").Distinct().Count());   // each nebula once
    }

    [Fact]
    public void Every_nebula_of_the_table_names_its_own_systems_the_way_ProcGenNames_reads_them()
    {
        foreach (Nebula nebula in HaRegions.All)
        {
            // The 10 ly cube at the nebula's middle, named from the nebula, reads back to the nebula and that cube.
            (int sx, int sy, int sz) = Galaxy.SectorOf(nebula.X, nebula.Y, nebula.Z);
            Box home = Galaxy.SectorBounds(sx, sy, sz);
            var cube = new CubePlace(sx, sy, sz, (int)Math.Floor((nebula.X - home.X0) / 10), (int)Math.Floor((nebula.Y - home.Y0) / 10), (int)Math.Floor((nebula.Z - home.Z0) / 10), 0, 1);
            string? name = ProcGenNames.SystemName(cube, nebula.Name);
            Assert.True(name is not null, nebula.Name);
            DecodedName decoded = ProcGenNames.Decode(name)!;
            Assert.Equal((nebula.Name, true, cube), (decoded.Sector, decoded.HandAuthored, decoded.Cube));
        }
    }
}
