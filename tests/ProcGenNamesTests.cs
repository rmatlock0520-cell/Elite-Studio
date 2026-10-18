using System.Globalization;
using EliteStudio.Core;
using EliteStudio.Nav;

namespace EliteStudio.Tests;

/// <summary>
/// Elite's generated names read both ways, as EDTS reads them. In tests\Fixtures\nav:
///   boxel-verification.csv  75 systems Spansh recorded on 14 October 2026 round three points
///                           (the player, 1,900 ly above and 700 ly below), with each one's name,
///                           position and address, the centre of the cube its address names and,
///                           for generated names, the centre its name gives; worked out while the 3D
///                           map was planned and copied byte for byte
///   sector-names.csv        EDAstro's sector list of 14 October 2026 cut down from its 11,649
///                           generated sectors to every 40th, plus the sectors round the player and
///                           the two rows whose sector numbers EDAstro has wrong (Aucopp, Juenae);
///                           the lines are EDAstro's own
///   sector-list.csv         the cut-down list of 8 October 2026 that ExploreCoverageTests reads
/// Before these tests were written the port was checked once against the whole list of 14 October:
/// every one of the 11,649 names gave its sector and every sector its name, but for those two rows.
/// </summary>
public sealed class ProcGenNamesTests(ITestOutputHelper output)
{
    /// <summary>The generated sectors of a copy of EDAstro's list: name and EDAstro's sector numbers, and the sector of the middle of its known systems.</summary>
    static IEnumerable<(string Name, (int X, int Y, int Z) Numbers, (int X, int Y, int Z) Middle)> Sectors(string csv)
    {
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] titles = lines[0].Split(',');
        int Column(string title) => Array.IndexOf(titles, title);
        foreach (string line in lines.Skip(1))
        {
            string[] cells = line.Split(',');
            if (cells[Column("id64 X")] == "") continue;   // a nebula's own sector
            double Number(string title) => double.Parse(cells[Column(title)], CultureInfo.InvariantCulture);
            yield return (cells[Column("Sector")], ((int)Number("id64 X"), (int)Number("id64 Y"), (int)Number("id64 Z")),
                Galaxy.SectorOf(Number("Avg X"), Number("Avg Y"), Number("Avg Z")));
        }
    }

    [Fact]
    public void Every_generated_sector_in_EDAstros_list_is_named_from_its_place_and_found_from_its_name()
    {
        var sectors = Sectors(SpanshStandIn.Recorded("sector-names.csv")).Concat(Sectors(SpanshStandIn.Recorded("sector-list.csv"))).ToList();
        Assert.Equal(297 + 26, sectors.Count);
        foreach (var (name, numbers, middle) in sectors)
        {
            // Aucopp and Juenae: EDAstro's numbers are wrong (0, 25, 2 and 0, 0, 0), the middle of their systems right.
            (int X, int Y, int Z) place = name is "Aucopp" or "Juenae" ? middle : numbers;
            Assert.Equal(name, ProcGenNames.SectorName(place.X, place.Y, place.Z));
            Assert.Equal(place, ProcGenNames.SectorIndex(name));
            Assert.Equal(place, ProcGenNames.SectorIndex(name.ToUpperInvariant()));
            Assert.Equal(place, ProcGenNames.SectorIndex(" " + name.ToLowerInvariant() + " "));
        }
        Assert.Equal((37, 31, 20), ProcGenNames.SectorIndex("Aucopp"));
        Assert.Equal((39, 31, 39), ProcGenNames.SectorIndex("Juenae"));

        // The player's sector, Sol's, and both kinds of name.
        Assert.Equal("Wredguia", ProcGenNames.SectorName(38, 32, 18));
        Assert.Equal("Wregoe", ProcGenNames.SectorName(39, 32, 18));
        Assert.Equal("Eorm Fraae", ProcGenNames.SectorName(12, 32, 34));
    }

    [Fact]
    public void Every_sector_over_the_galaxy_in_the_six_named_layers_has_a_name_that_leads_back_to_it()
    {
        // The galaxy's footprint on EDAstro's map: sectors 4 to 70 along x and along z.
        int named = 0;
        for (int layer = Galaxy.FirstNamedLayer; layer <= Galaxy.LastNamedLayer; layer++)
            for (int z = 4; z <= 70; z++)
                for (int x = 4; x <= 70; x++)
                {
                    string? name = ProcGenNames.SectorName(x, layer, z);
                    Assert.True(name is not null && ProcGenNames.SectorIndex(name) == (x, layer, z), $"sector {x}, {layer}, {z} ({name})");
                    named++;
                }
        Assert.Equal(6 * 67 * 67, named);
        Assert.Null(ProcGenNames.SectorName(128, 32, 18));
        Assert.Null(ProcGenNames.SectorName(38, -1, 18));
    }

    [Theory]
    [InlineData("Col 285 Sector")]                 // a nebula's own sector: no generated name
    [InlineData("Sol")]
    [InlineData("Wredguiaa")]                      // pieces that build no name
    [InlineData("Wred guia x")]
    [InlineData("Th")]                             // one piece is not a name
    [InlineData("Thaae Traae Eorm")]               // too many pieces
    [InlineData("")]
    [InlineData(null)]
    public void A_name_that_is_not_a_generated_sectors_names_no_sector(string? name) => Assert.Null(ProcGenNames.SectorIndex(name));

    [Fact]
    public void A_generated_name_gives_the_centre_of_its_cube_and_how_far_off_the_system_may_be()
    {
        // The three of the plan.
        DecodedName below = ProcGenNames.Decode("Synuefai YQ-A c15-1")!;
        Assert.Equal(((-685.0, -685.0, -405.0), 20.0, false), (below.Cube.Centre, below.HalfWidth, below.HandAuthored));
        Assert.Equal(357589029506, below.Cube.Address);   // the address Spansh recorded for it
        DecodedName california = ProcGenNames.Decode("California Sector LX-T b3-0")!;
        Assert.Equal(((-335.0, -215.0, -935.0), 10.0, true, "California Sector"), (california.Cube.Centre, california.HalfWidth, california.HandAuthored, california.Sector));
        DecodedName wredguia = ProcGenNames.Decode("Wredguia WD-K d8-65")!;
        Assert.Equal(((-345.0, 15.0, -305.0), 40.0, 65L), (wredguia.Cube.Centre, wredguia.HalfWidth, wredguia.Cube.Number));
        Assert.Equal(new CubePlace(38, 32, 18, 12, 0, 9, 3, 65), wredguia.Cube);

        // Case and stray spaces do not matter; the name comes back as the game writes it.
        Assert.Equal("Wredguia WD-K d8-65", ProcGenNames.Decode("  wredguia wd-k D8-65 ")!.Name);
        Assert.Equal("Col 285 Sector VK-N c7-18", ProcGenNames.Decode("col 285 sector vk-n c7-18")!.Name);
        Assert.Equal("Loihee AA-A h1", ProcGenNames.Decode("Loihee AA-A h0-1")!.Name);   // a first number of 0 is left out
        // The biggest cube is the whole sector.
        Assert.Equal((-705.0, 615.0, -425.0), ProcGenNames.Decode("Wredguia AA-A h0")!.Cube.Centre);
    }

    [Theory]
    [InlineData("Suhte")]                          // named by hand
    [InlineData("HIP 64267")]                      // a catalogue's name
    [InlineData("Sol")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Notasector AB-C d1-2")]           // no such sector
    [InlineData("Wredguia WD-K i8-65")]            // mass codes run from a to h
    [InlineData("Wredguia WD-K d8-65 A")]          // a body, not a system
    [InlineData("Wredguia WD-K d16-0")]            // letters and numbers that count past the sector's edge
    [InlineData("Wredguia BA-A h0")]               // a second cube east in cubes of 1,280 ly: past the edge
    [InlineData("Wredguia AA-A a1-2048")]          // more systems than a cube of mass code a can number
    [InlineData("Col 285 Sector CA-A h0")]         // a nebula's sector may reach half a cube past its edge, but not two cubes
    public void A_name_that_does_not_lead_to_a_cube_reads_as_nothing(string? name) => Assert.Null(ProcGenNames.Decode(name));

    [Fact]
    public void Every_recorded_system_lies_in_the_cube_its_address_and_its_name_give()
    {
        string[] lines = SpanshStandIn.Recorded("boxel-verification.csv").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] titles = [.. lines[0].Split(',').Select(title => title.Trim('"'))];
        int generated = 0, nebula = 0;
        foreach (string line in lines.Skip(1))
        {
            // Every field is quoted; the centres hold commas inside their quotes.
            string[] cells = [.. System.Text.RegularExpressions.Regex.Matches(line, "\"([^\"]*)\"|(?<=,|^)(?=,|$)").Select(field => field.Groups[1].Value)];
            Assert.Equal(titles.Length, cells.Length);
            string Cell(string title) => cells[Array.IndexOf(titles, title)];
            double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);
            string name = Cell("name");
            long address = long.Parse(Cell("id64"), CultureInfo.InvariantCulture);
            (double x, double y, double z) = (Number(Cell("x")), Number(Cell("y")), Number(Cell("z")));

            CubePlace place = SystemAddress.Place(address)!;
            Assert.Equal(Cell("mc")[0] - 'a', place.Mass);
            Assert.Equal(Cell("idBoxelCentre"), Centre(place.Centre));
            (double cx, double cy, double cz) = place.Centre;
            Assert.True(Math.Max(Math.Abs(x - cx), Math.Max(Math.Abs(y - cy), Math.Abs(z - cz))) <= place.CubeLy / 2, $"{name} lies outside its cube");

            DecodedName? decoded = ProcGenNames.Decode(name);
            if (Cell("kind") == "non-PG")
            {
                Assert.Null(decoded);
                continue;
            }
            // The name gives the very cube and number the address does.
            Assert.NotNull(decoded);
            Assert.Equal(Cell("nameBoxelCentre"), Centre(decoded.Cube.Centre));
            Assert.Equal((name, place, address), (decoded.Name, decoded.Cube, decoded.Cube.Address));
            Assert.Equal(Cell("kind") == "HA", decoded.HandAuthored);
            // And the way back: the address gives the name, counted from the grid's sector or from the nebula's.
            Assert.Equal(name, decoded.HandAuthored ? ProcGenNames.SystemName(place, decoded.Sector) : ProcGenNames.SystemName(place));
            if (decoded.HandAuthored) nebula++;
            else generated++;
        }
        Assert.Equal((38, 19), (generated, nebula));

        static string Centre((double X, double Y, double Z) at) => string.Create(CultureInfo.InvariantCulture, $"{at.X},{at.Y},{at.Z}");
    }

    [Fact]
    public void A_place_is_named_from_the_grids_sector_or_from_a_nebulas_own()
    {
        // Suhte's cube, numbered 18: by the grid's sector, and by Col 285 Sector, which is what the game calls it.
        CubePlace place = SystemAddress.Place(5031386452682)!;
        Assert.Equal("Wredguia XD-I c23-18", ProcGenNames.SystemName(place));
        Assert.Equal("Col 285 Sector VK-N c7-18", ProcGenNames.SystemName(place, "Col 285 Sector"));
        Assert.Equal("Col 285 Sector VK-N c7-18", ProcGenNames.SystemName(place, "col 285 sector"));
        // A nebula's letters reach a whole sector's width from the corner of its box, far past the
        // nebula itself, so which name the game gives depends on whether the system lies in the
        // nebula; that is for the caller to know. Trianguli Sector's box begins east of this cube.
        Assert.Equal("California Sector HK-L c22-18", ProcGenNames.SystemName(place, "California Sector"));
        Assert.Null(ProcGenNames.SystemName(place, "Trianguli Sector"));
        Assert.Null(ProcGenNames.SystemName(place, "Wredguia"));   // not a nebula's sector
        // Sol's own cube, by the grid's sector.
        Assert.Equal("Wregoe AC-D d12-0", ProcGenNames.SystemName(SystemAddress.Place(10477373803)!));
    }

    /// <summary>
    /// Goes out to the internet once, for EDAstro's whole sector list (about 1.5 MB), and reads
    /// every generated sector in it both ways. Skipped unless the environment variable
    /// ELITE_STUDIO_LIVE is 1; run it on its own, as the top of TradeSearchLiveTests.cs shows, with
    /// -class EliteStudio.Tests.ProcGenNamesTests. Nothing is kept on disk.
    /// </summary>
    [Fact]
    public async Task Live_every_generated_sector_in_EDAstros_whole_list_reads_both_ways()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ELITE_STUDIO_LIVE") == "1", "Set ELITE_STUDIO_LIVE=1 to fetch EDAstro's sector list over the internet.");
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(App.Program.UserAgent);
        string csv = await http.GetStringAsync("https://edastro.com/mapcharts/files/sector-list.csv", TestContext.Current.CancellationToken);

        int read = 0;
        var wrong = new List<string>();
        foreach (var (name, numbers, middle) in Sectors(csv))
        {
            read++;
            bool Fits((int X, int Y, int Z) at) => ProcGenNames.SectorName(at.X, at.Y, at.Z) == name && ProcGenNames.SectorIndex(name) == at;
            if (!Fits(numbers) && !Fits(middle)) wrong.Add(name);   // EDAstro has the numbers of a few rows wrong, and the middle of their systems right
        }
        output.WriteLine($"{read:N0} generated sectors read both ways; {wrong.Count} did not: {string.Join(", ", wrong)}");
        Assert.InRange(read, 11_000, 20_000);
        Assert.Empty(wrong);
    }
}
