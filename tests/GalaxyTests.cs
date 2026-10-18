using EliteStudio.Nav;

namespace EliteStudio.Tests;

/// <summary>
/// The game's grid of sectors, layers and cubes. The figures are the ones checked by hand and
/// against EDAstro and Spansh while the 3D map was planned (14 October 2026): the player at
/// -167.09, 26.72, -55.38 is in Wredguia, sector 38, 32, 18, whose box EDAstro's own sector page
/// gives as starting at -1345, -25, -1065.
/// </summary>
public sealed class GalaxyTests
{
    static readonly (double X, double Y, double Z) Player = (-167.09375, 26.71875, -55.375);

    [Fact]
    public void A_place_lies_in_the_sector_whose_box_holds_it()
    {
        Assert.Equal((38, 32, 18), Galaxy.SectorOf(Player.X, Player.Y, Player.Z));
        Assert.Equal(new Box(-1345, -25, -1065, -65, 1255, 215), Galaxy.SectorBounds(38, 32, 18));
        Assert.True(Galaxy.SectorBounds(38, 32, 18).Contains(Player.X, Player.Y, Player.Z));
        Assert.Equal((-705.0, 615.0, -425.0), Galaxy.SectorBounds(38, 32, 18).Centre);

        // Sol, and the corner of its sector that the cubes of every nebula's own sector are counted through.
        Assert.Equal((39, 32, 18), Galaxy.SectorOf(0, 0, 0));
        Box sol = Galaxy.SectorBounds(39, 32, 18);
        Assert.Equal((Galaxy.BaseX, Galaxy.BaseY, Galaxy.BaseZ), (sol.X0, sol.Y0, sol.Z0));

        // A face belongs to the box beyond it: Wredguia's east face is Wregoe's west face.
        Assert.Equal((39, 32, 18), Galaxy.SectorOf(-65, 0, 0));
        Assert.False(Galaxy.SectorBounds(38, 32, 18).Contains(-65, 0, 0));
        Assert.Equal((38, 32, 18), Galaxy.SectorOf(-65.001, 0, 0));
        // Colonia, far to the north-west and a little below the middle, and the grid's own corner.
        Assert.Equal((31, 31, 34), Galaxy.SectorOf(-9530.5, -910.28125, 19808.125));
        Assert.Equal((0, 0, 0), Galaxy.SectorOf(Galaxy.OriginX, Galaxy.OriginY, Galaxy.OriginZ));
    }

    [Fact]
    public void Layers_have_fixed_names_and_the_seam_near_the_middle_is_at_minus_25()
    {
        Assert.Equal(32, Galaxy.LayerOf(27));
        Assert.Equal(32, Galaxy.LayerOf(-25));       // on the seam: the layer above
        Assert.Equal(31, Galaxy.LayerOf(-25.03));
        Assert.Equal(31, Galaxy.LayerOf(Galaxy.MiddleY - 5));
        Assert.Equal(32, Galaxy.LayerOf(Galaxy.MiddleY));

        Assert.Equal(
            ["Deep below", "Far below", "Just below the middle", "Just above the middle", "Far above", "Deep above"],
            Enumerable.Range(Galaxy.FirstNamedLayer, 6).Select(Galaxy.LayerName));
        Assert.Equal("Just above the middle", Galaxy.LayerName(Galaxy.LayerOf(Player.Y)));
        // Past the six named layers a layer is counted from the middle, so no two share a name.
        Assert.Equal("4 layers below the middle", Galaxy.LayerName(28));
        Assert.Equal("4 layers above the middle", Galaxy.LayerName(35));
        Assert.Equal(64, Enumerable.Range(0, Galaxy.Layers).Select(Galaxy.LayerName).Distinct().Count());
    }

    [Fact]
    public void The_remainder_the_grid_needs_never_goes_below_zero()
    {
        Assert.Equal(-35, -315 % 40);                  // C#'s own, for comparison
        Assert.Equal(5, Galaxy.Mod(-315.0, 40));
        Assert.Equal(5L, Galaxy.Mod(-315L, 40L));
        Assert.Equal(1279L, Galaxy.Mod(-1L, 1280L));
        Assert.Equal(1278.5, Galaxy.Mod(-1.5, 1280));
        Assert.Equal(0, Galaxy.Mod(-1280.0, 1280));
        Assert.Equal(17, Galaxy.Mod(17.0, 40));
    }

    [Fact]
    public void Cubes_line_up_across_sectors_and_every_size_divides_a_sector()
    {
        Assert.Equal([10.0, 20, 40, 80, 160, 320, 640, 1280], Enumerable.Range(0, 8).Select(Galaxy.CubeLy));
        Assert.Throws<ArgumentOutOfRangeException>(() => Galaxy.CubeLy(8));

        // The player's cube of 160 ly is the last one along x in Wredguia, the lowest one up, the seventh north.
        Assert.Equal(new Box(-225, -25, -105, -65, 135, 55), Galaxy.CubeAt(Player.X, Player.Y, Player.Z, 160));
        // Just east of Wredguia's edge the next cube begins, in Wregoe; just below the seam, the cube under it.
        Assert.Equal(new Box(-65, -25, -105, 95, 135, 55), Galaxy.CubeAt(-65, 0, 0, 160));
        Assert.Equal(new Box(-225, -185, -105, -65, -25, 55), Galaxy.CubeAt(-100, -25.5, 0, 160));
        // A cube of 1,280 ly is the sector itself.
        Assert.Equal(Galaxy.SectorBounds(38, 32, 18), Galaxy.CubeAt(Player.X, Player.Y, Player.Z, 1280));
    }

    [Fact]
    public void A_cube_is_named_by_letters_that_count_it_and_a_system_by_its_number_after_them()
    {
        // 12 cubes east, 0 up and 9 north among Wredguia's cubes of 80 ly: count 12 + 128 x 0 + 128 x 128 x 9 = 147,468.
        Assert.Equal("Wredguia WD-K d8", Galaxy.CubeName("Wredguia", (12, 0, 9), 3));
        Assert.Equal("WD-K d8", Galaxy.CubeName(null, (12, 0, 9), 3));
        Assert.Equal("Wredguia WD-K d8-65", Galaxy.SystemName("Wredguia", (12, 0, 9), 3, 65));
        // A count below 26 x 26 x 26 has no first number, and the system's number follows the mass code at once.
        Assert.Equal("Loihee AA-A h", Galaxy.CubeName("Loihee", (0, 0, 0), 7));
        Assert.Equal("Loihee AA-A h1", Galaxy.SystemName("Loihee", (0, 0, 0), 7, 1));
        Assert.Equal("Synuefai YQ-A c15-1", Galaxy.SystemName("Synuefai", (16, 15, 16), 2, 1));

        Assert.Throws<ArgumentOutOfRangeException>(() => Galaxy.CubeName("Wredguia", (128, 0, 0), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Galaxy.CubeName("Wredguia", (0, -1, 0), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Galaxy.CubeName("Wredguia", (0, 0, 0), 8));
    }
}
