using System.Globalization;

namespace EliteStudio.Nav;

/// <summary>
/// A box in the galaxy, in light years, from its corner with the smallest x, y and z
/// (<see cref="X0"/>, <see cref="Y0"/>, <see cref="Z0"/>) to the opposite one. The far faces belong
/// to the next box, as they do in the game's grid.
/// </summary>
public readonly record struct Box(double X0, double Y0, double Z0, double X1, double Y1, double Z1)
{
    /// <summary>The middle of the box.</summary>
    public (double X, double Y, double Z) Centre => ((X0 + X1) / 2, (Y0 + Y1) / 2, (Z0 + Z1) / 2);

    /// <summary>The point lies in the box: on or past the near faces, short of the far ones.</summary>
    public bool Contains(double x, double y, double z) => x >= X0 && x < X1 && y >= Y0 && y < Y1 && z >= Z0 && z < Z1;
}

/// <summary>
/// The game's grid over the galaxy. Space is cut into sectors, boxes 1,280 light years on a side,
/// counted from a fixed corner far to the south-west and below; every sector is cut again into
/// cubes, whose size goes with how heavy a star is (10 light years for the lightest, doubling up to
/// a whole sector). The same grid is in every system's address (<see cref="Core.SystemAddress"/>)
/// and in every generated name (<see cref="ProcGenNames"/>). East is +x, up is +y, and north,
/// towards the galaxy's core, is +z.
///
/// A layer is the slice of sectors at one height. The galaxy is thin, so the systems anybody has
/// recorded lie in six layers, 29 to 34; those have fixed names that say where they are against the
/// galaxy's middle, never against the player.
/// </summary>
public static class Galaxy
{
    /// <summary>Light years along one side of a sector.</summary>
    public const double SectorLy = 1280;

    /// <summary>The corner of sector 0, 0, 0 (with <see cref="OriginY"/> and <see cref="OriginZ"/>), in light years.</summary>
    public const double OriginX = -49985, OriginY = -40985, OriginZ = -24105;

    /// <summary>
    /// The corner of the sector Sol lies in (39, 32, 18), with <see cref="BaseY"/> and <see cref="BaseZ"/>.
    /// The cubes inside a nebula's own sector are counted on a grid through this corner.
    /// </summary>
    public const double BaseX = -65, BaseY = -25, BaseZ = -1065;

    /// <summary>
    /// About where the galaxy's middle lies in height. The seam between layers 31 and 32 is at
    /// -25, close below it, so most of the stars near the middle are in those two layers.
    /// </summary>
    public const double MiddleY = -21;

    /// <summary>Sectors from west to east and from south to north. Up and down there are <see cref="Layers"/>.</summary>
    public const int SectorsAcross = 128;

    /// <summary>Layers of sectors from the bottom of the grid to its top: the address keeps 6 bits for them.</summary>
    public const int Layers = 64;

    /// <summary>The lowest and highest layers with fixed names: every sector anybody has recorded lies in one of them.</summary>
    public const int FirstNamedLayer = 29, LastNamedLayer = 34;

    static readonly string[] LayerNames = ["Deep below", "Far below", "Just below the middle", "Just above the middle", "Far above", "Deep above"];

    /// <summary>
    /// The remainder of a division that never goes below 0, as the grid needs: C#'s own % keeps the
    /// sign of the number divided, so -315 % 40 is -35 where the grid wants 5.
    /// </summary>
    public static double Mod(double value, double by) => ((value % by) + by) % by;

    /// <inheritdoc cref="Mod(double, double)"/>
    public static long Mod(long value, long by) => ((value % by) + by) % by;

    /// <summary>The sector a point lies in, by its numbers along x, y and z (y is the layer).</summary>
    public static (int X, int Y, int Z) SectorOf(double x, double y, double z) =>
        ((int)Math.Floor((x - OriginX) / SectorLy), LayerOf(y), (int)Math.Floor((z - OriginZ) / SectorLy));

    /// <summary>The layer a height lies in. A point on the seam belongs to the layer above it.</summary>
    public static int LayerOf(double y) => (int)Math.Floor((y - OriginY) / SectorLy);

    /// <summary>The box of a sector, by its numbers along x, y and z.</summary>
    public static Box SectorBounds(int x, int y, int z)
    {
        double x0 = OriginX + x * SectorLy, y0 = OriginY + y * SectorLy, z0 = OriginZ + z * SectorLy;
        return new Box(x0, y0, z0, x0 + SectorLy, y0 + SectorLy, z0 + SectorLy);
    }

    /// <summary>
    /// A layer's name for the player. Layers 29 to 34 have fixed names; any other is counted from
    /// the middle, which no recorded sector needs but a far flight might.
    /// </summary>
    public static string LayerName(int layer) => layer switch
    {
        >= FirstNamedLayer and <= LastNamedLayer => LayerNames[layer - FirstNamedLayer],
        < FirstNamedLayer => $"{32 - layer} layers below the middle",
        _ => $"{layer - 31} layers above the middle",
    };

    /// <summary>Light years along one side of a cube of this mass code (0 to 7 for the letters a to h).</summary>
    public static double CubeLy(int mass) => mass is >= 0 and <= 7 ? 10 * (1 << mass) : throw new ArgumentOutOfRangeException(nameof(mass), mass, "Mass codes run from 0 (a) to 7 (h).");

    /// <summary>
    /// The cube of the given size a point lies in. Every cube size divides a sector exactly, so the
    /// cubes of one size line up across sector edges, and a cube never spans two sectors.
    /// </summary>
    public static Box CubeAt(double x, double y, double z, double cubeLy)
    {
        double x0 = x - Mod(x - OriginX, cubeLy), y0 = y - Mod(y - OriginY, cubeLy), z0 = z - Mod(z - OriginZ, cubeLy);
        return new Box(x0, y0, z0, x0 + cubeLy, y0 + cubeLy, z0 + cubeLy);
    }

    /// <summary>
    /// A cube's part of a generated name, e.g. "Wredguia WD-K d8" for the cube 12 east, 0 up and 9
    /// north (counted from the sector's corner) among the cubes of mass code d. The letters and
    /// first number count the cubes along x first, then up, then north, 128 to a row whatever their
    /// size; the letter after them is the mass code. With no sector name only the letters come back.
    /// </summary>
    public static string CubeName(string? sectorName, (int X, int Y, int Z) cube, int mass)
    {
        if (mass is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(mass), mass, "Mass codes run from 0 (a) to 7 (h).");
        if (cube.X is < 0 or >= 128 || cube.Y is < 0 or >= 128 || cube.Z is < 0 or >= 128)
            throw new ArgumentOutOfRangeException(nameof(cube), cube, "A name counts at most 128 cubes along each side.");
        long offset = cube.X + 128L * cube.Y + 128L * 128 * cube.Z;
        long first = offset / (26 * 26 * 26);
        string letters = $"{(char)('A' + offset % 26)}{(char)('A' + offset / 26 % 26)}-{(char)('A' + offset / (26 * 26) % 26)} {(char)('a' + mass)}{(first == 0 ? "" : first.ToString(CultureInfo.InvariantCulture))}";
        return string.IsNullOrEmpty(sectorName) ? letters : $"{sectorName} {letters}";
    }

    /// <summary>
    /// A whole generated name: the cube's part (<see cref="CubeName"/>) and the system's number in
    /// the cube, after a dash when the cube's part ends in a number ("Wredguia WD-K d8-65", "Loihee AA-A h1").
    /// </summary>
    public static string SystemName(string? sectorName, (int X, int Y, int Z) cube, int mass, long number)
    {
        string name = CubeName(sectorName, cube, mass);
        string numberText = number.ToString(CultureInfo.InvariantCulture);
        return char.IsAsciiDigit(name[^1]) ? $"{name}-{numberText}" : name + numberText;
    }
}
