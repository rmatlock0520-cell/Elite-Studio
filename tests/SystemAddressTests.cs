using System.Text;
using System.Text.Json;
using EliteStudio.Core;
using EliteStudio.Market;

namespace EliteStudio.Tests;

/// <summary>
/// A system's address against a position claimed for it, and the price listings that are left out
/// when the two cannot both be true. The case that started it is the first test: real figures of
/// 11 October 2026. Then the cube an address names, for the 3D map, and the other systems of a cube.
/// </summary>
public sealed class SystemAddressTests
{
    // The game's own figures for three systems.
    static readonly (long Address, double X, double Y, double Z) Sol = (10477373803, 0, 0, 0);
    static readonly (long Address, double X, double Y, double Z) Hip74116 = (800768182659, 31.53125, 252.6875, 275.125);
    static readonly (long Address, double X, double Y, double Z) Col285 = (7266949866905, -65.75, -189.78125, -39.875);

    [Fact]
    public void A_position_is_held_against_the_cube_the_address_names()
    {
        foreach ((long address, double x, double y, double z) in new[] { Sol, Hip74116, Col285 }) Assert.True(SystemAddress.Holds(address, x, y, z));

        // Sangweni Gateway as Ardent Insight listed it: Col 285 Sector TO-Y b14-3's address with HIP 74116's position, 556 ly away.
        Assert.False(SystemAddress.Holds(Col285.Address, Hip74116.X, Hip74116.Y, Hip74116.Z));
        Assert.False(SystemAddress.Holds(Hip74116.Address, Col285.X, Col285.Y, Col285.Z));
        // Sol's cube is 80 ly across (mass code d): a neighbour 4 ly off shares it, one 100 ly off does not.
        Assert.True(SystemAddress.Holds(Sol.Address, 3.03, -0.09, 3.16));
        Assert.False(SystemAddress.Holds(Sol.Address, 100, 0, 0));
        // An address that is not known says nothing against any position.
        Assert.True(SystemAddress.Holds(0, 12345, -678, 9));
    }

    [Fact]
    public void Every_recorded_listing_lies_where_its_address_says()
    {
        // The services' real answers, recorded for the other tests: hundreds of systems of every kind of name.
        int held = 0;
        var strays = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.json", SearchOption.AllDirectories))
        {
            JsonElement root;
            try { root = JsonDocument.Parse(File.ReadAllBytes(file)).RootElement; }
            catch (JsonException) { continue; }   // a file that is broken on purpose
            Walk(root);
        }
        Assert.Empty(strays);
        Assert.True(held > 100, $"only {held} recorded listings carry an address and a position");

        void Walk(JsonElement at)
        {
            if (at.ValueKind == JsonValueKind.Array) foreach (JsonElement item in at.EnumerateArray()) Walk(item);
            if (at.ValueKind != JsonValueKind.Object) return;
            if (at.TryGetProperty("systemAddress", out JsonElement address) && address.ValueKind == JsonValueKind.Number
                && at.TryGetProperty("systemX", out JsonElement x) && x.ValueKind == JsonValueKind.Number)
            {
                if (SystemAddress.Holds(address.GetInt64(), x.GetDouble(), at.GetProperty("systemY").GetDouble(), at.GetProperty("systemZ").GetDouble())) held++;
                else strays.Add(at.TryGetProperty("systemName", out JsonElement name) ? name.ToString() : address.ToString());
            }
            foreach (JsonProperty part in at.EnumerateObject()) Walk(part.Value);
        }
    }

    // Systems near the player, as Spansh recorded them on 14 October 2026 (tests\Fixtures\nav\boxel-verification.csv).
    const long Suhte = 5306264359626, Maia = 8216113749, VkN18 = 5031386452682, VkN17 = 4756508545738, VkN23 = 6405775987402;

    [Fact]
    public void An_address_names_the_sector_the_cube_and_the_systems_number_in_the_cube()
    {
        // Sol: mass code d, so cubes of 80 ly; the 14th cube north in sector 39, 32, 18, number 0 in it.
        CubePlace sol = SystemAddress.Place(Sol.Address)!;
        Assert.Equal(new CubePlace(39, 32, 18, 0, 0, 13, 3, 0), sol);
        Assert.Equal((80.0, (-65.0, -25.0, -25.0), (-25.0, 15.0, 15.0)), (sol.CubeLy, sol.Corner, sol.Centre));

        // Suhte, next to the player: mass code c, cubes of 40 ly, and the 20th system of its cube.
        CubePlace suhte = SystemAddress.Place(Suhte)!;
        Assert.Equal((38, 32, 18, 2, 40.0, 19L), (suhte.SectorX, suhte.SectorY, suhte.SectorZ, suhte.Mass, suhte.CubeLy, suhte.Number));
        Assert.Equal((-165.0, 35.0, -45.0), suhte.Centre);
        // Maia in the Pleiades: mass code f, cubes of 320 ly.
        CubePlace maia = SystemAddress.Place(Maia)!;
        Assert.Equal((5, 320.0, (-225.0, -185.0, -265.0)), (maia.Mass, maia.CubeLy, maia.Centre));

        // The parts give the address back, and every system lies within half a cube of its cube's centre.
        foreach ((long address, double x, double y, double z) in new[] { Sol, Hip74116, Col285 })
        {
            CubePlace place = SystemAddress.Place(address)!;
            Assert.Equal(address, place.Address);
            (double cx, double cy, double cz) = place.Centre;
            Assert.True(Math.Max(Math.Abs(x - cx), Math.Max(Math.Abs(y - cy), Math.Abs(z - cz))) <= place.CubeLy / 2, $"{address} lies outside its cube");
        }
        Assert.Equal([Suhte, Maia], new[] { suhte.Address, maia.Address });

        // A body's address (the body's number above the system's) names the system's cube and number.
        Assert.Equal(suhte, SystemAddress.Place(Suhte | (7L << 55)));
        // An address that is not known names nothing.
        Assert.Null(SystemAddress.Place(0));
        Assert.Null(SystemAddress.Place(-1));
    }

    [Fact]
    public void Another_number_in_the_same_cube_is_another_system_of_that_cube()
    {
        // Col 285 Sector VK-N c7-18, c7-17 and c7-23 share Suhte's cube, as Spansh recorded them.
        Assert.Equal(VkN17, SystemAddress.WithNumber(VkN18, 17));
        Assert.Equal(VkN23, SystemAddress.WithNumber(VkN18, 23));
        Assert.Equal(Suhte, SystemAddress.WithNumber(VkN18, 19));
        Assert.Equal(VkN18, SystemAddress.WithNumber(VkN18 | (3L << 55), 18));   // from a body's address: the system's, no body
        Assert.Equal(SystemAddress.Place(VkN18)! with { Number = 0 }, SystemAddress.Place(SystemAddress.WithNumber(VkN18, 0)));

        // Mass code c keeps 17 bits for the number: 0 to 131,071, and no more.
        Assert.Equal(131071, SystemAddress.Place(SystemAddress.WithNumber(VkN18, 131071))!.Number);
        Assert.Throws<ArgumentOutOfRangeException>(() => SystemAddress.WithNumber(VkN18, 131072));
        Assert.Throws<ArgumentOutOfRangeException>(() => SystemAddress.WithNumber(VkN18, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SystemAddress.WithNumber(0, 1));
        // Sol's cube (mass code d) keeps 20 bits.
        Assert.Equal((1L << 20) - 1, SystemAddress.Place(SystemAddress.WithNumber(Sol.Address, (1L << 20) - 1))!.Number);
    }

    [Fact]
    public async Task A_listing_with_another_systems_position_is_left_out()
    {
        // The listing itself, as it was sent, between two that are sound (one of them with no address at all).
        const string sent = """
            [{"commodityName":"cryolite","marketId":1,"stationName":"Zero Squared","stationType":"MegaShip","systemAddress":800768182659,"systemName":"HIP 74116","systemX":31.53125,"systemY":252.6875,"systemZ":275.125,"buyPrice":11000,"stock":500,"updatedAt":"2026-10-11T09:56:23.000Z"},
             {"commodityName":"cryolite","marketId":4316788739,"stationName":"Sangweni Gateway","stationType":"CraterPort","systemAddress":7266949866905,"systemName":"Col 285 Sector TO-Y b14-3","systemX":31.53125,"systemY":252.6875,"systemZ":275.125,"buyPrice":11062,"stock":6288295,"updatedAt":"2026-10-11T09:56:23.000Z"},
             {"commodityName":"cryolite","marketId":3,"stationName":"Old Record","stationType":"Coriolis","systemName":"Somewhere","systemX":1,"systemY":2,"systemZ":3,"buyPrice":12000,"stock":500,"updatedAt":"2026-10-11T09:56:23.000Z"}]
            """;
        IReadOnlyList<Offer> read = await Offer.ReadList(new MemoryStream(Encoding.UTF8.GetBytes(sent)), CancellationToken.None);
        Assert.Equal(["Zero Squared", "Old Record"], read.Select(offer => offer.Station));
    }
}
