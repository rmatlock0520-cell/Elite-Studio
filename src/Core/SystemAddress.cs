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
    const double SectorLy = 1280;
    // The corner of sector 0, 0, 0 as the galaxy map counts light years.
    const double OriginX = -49985, OriginY = -40985, OriginZ = -24105;
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
}
