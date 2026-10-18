namespace EliteStudio.Core;

/// <summary>
/// A star system's address: the number the game itself knows the system by ("SystemAddress" in the
/// journal). It is not a serial number. It says which cube of the galaxy the system lies in, so it
/// can be held against a position somebody else claims for that system.
///
/// Why that matters: on 11 October 2026 a data service listed a station with its own system's name
/// and address but another system's position, 556 light years off and next to the player. Every
/// distance filter was applied to the false position, and the trade search ranked a station the
/// player could not reach in a day as "1 jump away".
///
/// The layout, from the lowest bit: 3 bits of mass code (0 to 7, the letters a to h in a generated
/// name); then for z, y and x in turn the cube's place inside its sector (7 bits less the mass
/// code) and the sector (7 bits for z and x, 6 for y); then the system's number in its cube and the
/// body. A sector is 1,280 light years across and a cube 10 light years times two to the mass code.
/// </summary>
public static class SystemAddress
{
    internal const double SectorLy = 1280;
    // The corner of sector 0, 0, 0 as the galaxy map counts light years.
    internal const double OriginX = -49985, OriginY = -40985, OriginZ = -24105;
    // A system on the very edge of its cube, and positions rounded to 1/32 ly.
    const double MarginLy = 1;

    /// <summary>
    /// False only when the position cannot be this system's: it lies outside the cube the address
    /// names. An address that is not known (0 or less) says nothing against any position.
    /// </summary>
    public static bool Holds(long address, double x, double y, double z)
    {
        if (address <= 0) return true;
        int mass = (int)(address & 7);
        int bits = 7 - mass;
        long rest = address >> 3;
        long Take(int count)
        {
            long value = rest & ((1L << count) - 1);
            rest >>= count;
            return value;
        }
        long cubeZ = Take(bits), sectorZ = Take(7), cubeY = Take(bits), sectorY = Take(6), cubeX = Take(bits), sectorX = Take(7);
        double cubeLy = 10 * (1 << mass);
        return Inside(x, OriginX + sectorX * SectorLy + cubeX * cubeLy) && Inside(y, OriginY + sectorY * SectorLy + cubeY * cubeLy)
            && Inside(z, OriginZ + sectorZ * SectorLy + cubeZ * cubeLy);

        bool Inside(double at, double corner) => at >= corner - MarginLy && at <= corner + cubeLy + MarginLy;
    }

    /// <summary>
    /// The cube an address names and the system's number in it; null for an address that is not
    /// known (0 or less). The bits above the number, which name a body, are left out: every body of
    /// a system lies in the system's cube.
    /// </summary>
    public static CubePlace? Place(long address)
    {
        if (address <= 0) return null;
        int mass = (int)(address & 7);
        int bits = 7 - mass;
        long rest = address >> 3;
        int Take(int count)
        {
            int value = (int)(rest & ((1L << count) - 1));
            rest >>= count;
            return value;
        }
        int cubeZ = Take(bits), sectorZ = Take(7), cubeY = Take(bits), sectorY = Take(6), cubeX = Take(bits), sectorX = Take(7);
        return new CubePlace(sectorX, sectorY, sectorZ, cubeX, cubeY, cubeZ, mass, rest & CubePlace.NumberMask(mass));
    }

    /// <summary>
    /// The address of another system in the same cube: the one with this number (and no body).
    /// Two systems of one cube differ only in their numbers, so a number no service has recorded
    /// can be named and placed from any system of its cube that is recorded.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The address is not known, or the number is more than its cube can hold.</exception>
    public static long WithNumber(long address, long number)
    {
        CubePlace place = Place(address) ?? throw new ArgumentOutOfRangeException(nameof(address), address, "Not a system's address.");
        if (number < 0 || number > CubePlace.NumberMask(place.Mass))
            throw new ArgumentOutOfRangeException(nameof(number), number, "More systems than one cube can hold.");
        return (place with { Number = number }).Address;
    }
}

/// <summary>
/// The cube of the galaxy a system lies in, as its address names it, and the system's number in
/// that cube. Every system of one cube shares everything here but the number.
/// </summary>
/// <param name="SectorX">The sector, counted from the galaxy's west edge (0 to 127; with <paramref name="SectorY"/> from the bottom, 0 to 63, and <paramref name="SectorZ"/> from the south, 0 to 127).</param>
/// <param name="CubeX">The cube inside its sector, counted from the sector's west, bottom and south faces (with <paramref name="CubeY"/> and <paramref name="CubeZ"/>).</param>
/// <param name="Mass">The mass code: 0 to 7 for the letters a to h in a generated name. Heavier stars sit in bigger cubes.</param>
/// <param name="Number">The system's number in its cube: the last number of a generated name ("65" in "Wredguia WD-K d8-65").</param>
public sealed record CubePlace(int SectorX, int SectorY, int SectorZ, int CubeX, int CubeY, int CubeZ, int Mass, long Number)
{
    /// <summary>Light years along one side of the cube: 10 for mass code a, doubling with each letter up to 1,280 for h (a whole sector).</summary>
    public double CubeLy => 10 * (1 << Mass);

    /// <summary>The cube's corner with the smallest x, y and z, in light years.</summary>
    public (double X, double Y, double Z) Corner => (
        SystemAddress.OriginX + SectorX * SystemAddress.SectorLy + CubeX * CubeLy,
        SystemAddress.OriginY + SectorY * SystemAddress.SectorLy + CubeY * CubeLy,
        SystemAddress.OriginZ + SectorZ * SystemAddress.SectorLy + CubeZ * CubeLy);

    /// <summary>The middle of the cube, in light years: the best guess for a system known only by its address or its generated name, never more than half a cube off.</summary>
    public (double X, double Y, double Z) Centre
    {
        get
        {
            (double x, double y, double z) = Corner;
            double half = CubeLy / 2;
            return (x + half, y + half, z + half);
        }
    }

    /// <summary>The system's address, built back from the parts (the layout is in <see cref="SystemAddress"/>).</summary>
    public long Address
    {
        get
        {
            int bits = 7 - Mass;
            long address = Number;
            void Put(long value, int count) => address = (address << count) | value;
            Put(SectorX, 7);
            Put(CubeX, bits);
            Put(SectorY, 6);
            Put(CubeY, bits);
            Put(SectorZ, 7);
            Put(CubeZ, bits);
            Put(Mass, 3);
            return address;
        }
    }

    /// <summary>The highest number a cube of this mass code can give a system: the address keeps 11 bits for it in the smallest cubes and 3 more for each letter up.</summary>
    internal static long NumberMask(int mass) => (1L << (11 + 3 * mass)) - 1;
}
