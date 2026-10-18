using System.Net;
using EliteStudio.Nav;

namespace EliteStudio.Tests;

/// <summary>
/// EDAstro's sector list read sector by sector for the 3D map, and the block of sectors the map
/// opens on. In tests\Fixtures\nav:
///   sector-list.csv           EDAstro's real list of 8 October 2026, cut down to 28 sectors (see
///                             ExploreCoverageTests): six columns side by side far out in the west of
///                             the galaxy, each in four layers, and near Sol two sectors of one column
///                             and two nebulae's own sectors
///   sector-list-bad-rows.csv  HAND-MADE, not recorded: the rows Studio must repair or leave out. Some
///                             are EDAstro's own rows of 14 October 2026 (Aucopp and Juenae, whose
///                             sector numbers EDAstro has wrong), the others are made from EDAstro's
///                             rows by hand. Its first column, "Note", which EDAstro's list does not
///                             have, says on each line what is wrong with it and what Studio should do;
///                             it also shows that columns are found by their names
/// </summary>
public sealed class SectorCubesTests(ITestOutputHelper output) : IDisposable
{
    static readonly string ListText = SpanshStandIn.Recorded("sector-list.csv");
    static readonly string BadRows = SpanshStandIn.Recorded("sector-list-bad-rows.csv");
    static readonly DateTime Fetched = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    const string NotTheList = "EDAstro's sector list is not laid out the way Studio knows it, so no map could be made from it.";

    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioSectorCubes-").FullName;

    public void Dispose() => Directory.Delete(temp, recursive: true);

    static IEnumerable<string> Names(IEnumerable<BlockLayer> layers) => layers.Select(layer => layer.Name);

    [Fact]
    public void Each_sector_is_kept_by_its_numbers_and_each_nebulas_own_sector_by_its_name()
    {
        var list = SectorList.Parse(ListText, Fetched);

        Assert.Equal((26, 2, 0, 0), (list.Sectors.Count, list.Nebulae.Count, list.Dropped, list.Sectors.Count(row => row.Repaired)));
        Assert.Equal(Fetched, list.FetchedUtc);
        Assert.Equal(new SectorRow("Eorm Fraae", 12, 32, 34, 3650, new DateOnly(2016, 9, 17), false), list.At(12, 32, 34));
        Assert.Same(list.At(12, 32, 34), list.Named("eorm fraae "));
        Assert.Null(list.At(12, 34, 34));
        Assert.Null(list.Named("Wredguia"));   // not in the cut-down list
        Assert.Equal(new NebulaRow("Tr 10 Sector", 1035, new DateOnly(2015, 11, 7), 1368.92, 10.12, -171.91), list.Nebula("TR 10 SECTOR"));
        Assert.Null(list.Nebula("Eorm Fraae"));
        Assert.Equal(["Eight Burst Sector", "Tr 10 Sector"], list.NebulaeIn(Galaxy.SectorBounds(40, 32, 18)).Select(nebula => nebula.Name));
        // EDAstro ends its lines the Windows way: the same.
        Assert.Equal(list.Sectors, SectorList.Parse(ListText.ReplaceLineEndings("\r\n"), Fetched).Sectors);
    }

    [Fact]
    public void A_block_is_the_column_and_the_eight_round_it_in_every_layer_that_has_rows_there()
    {
        var list = SectorList.Parse(ListText, Fetched);

        // The middle of the six western columns: four layers, the lowest and the highest named ones empty there.
        SectorBlock west = list.Block(13, 34);
        Assert.Equal([30, 31, 32, 33], west.Layers.Select(layer => layer.Layer));
        Assert.Equal(["Far below", "Just below the middle", "Just above the middle", "Far above"], Names(west.Layers));
        Assert.Equal([37L, 5546, 12059, 46], west.Layers.Select(layer => layer.Known!.Value));
        Assert.Equal((-2585.0, -1305.0), (west.Layers[0].YFrom, west.Layers[0].YTo));
        Assert.Equal((-25.0, 1255.0), (west.Layers[2].YFrom, west.Layers[2].YTo));
        Assert.Equal(new Box(-34625, -2585, 18135, -30785, 2535, 21975), west.Bounds);
        Assert.Equal((13, 34), (west.X, west.Z));

        // Layer by layer from the lowest, row by row from the south, each row from the west.
        Assert.Equal(36, west.Sectors.Count);
        Assert.Equal((12, 30, 33), (west.Sectors[0].X, west.Sectors[0].Y, west.Sectors[0].Z));
        Assert.Equal((14, 33, 35), (west.Sectors[^1].X, west.Sectors[^1].Y, west.Sectors[^1].Z));
        Assert.Equal(new BlockSector(12, 30, 34, "Eorld Prae", 9, new DateOnly(2026, 7, 30)), west.Sectors[3]);
        // A sector EDAstro has no row for: none known, and the name the game's rule gives it.
        BlockSector empty = west.Sectors[0];
        Assert.Equal((0, ProcGenNames.SectorName(12, 30, 33), (DateOnly?)null), (empty.Known, empty.Name, empty.First));
        Assert.Equal(list.Sectors.Where(row => row.X is >= 12 and <= 14 && row.Z == 34).Sum(row => row.Known), west.Sectors.Sum(sector => sector.Known));

        // Near Sol the cut-down list has only the column east of Sol's (40, 18), in two layers.
        SectorBlock sol = list.Block(39, 18);
        Assert.Equal([31, 32], sol.Layers.Select(layer => layer.Layer));
        Assert.Equal([156257L, 237198], sol.Layers.Select(layer => layer.Known!.Value));
        Assert.Equal(new BlockSector(38, 32, 18, "Wredguia", 0, null), sol.Sectors.Single(sector => (sector.X, sector.Y, sector.Z) == (38, 32, 18)));
        // Round the player's column it has no row at all: the two layers on either side of the middle, nothing known in them.
        SectorBlock home = list.Block(38, 18);
        Assert.Equal([31, 32], home.Layers.Select(layer => layer.Layer));
        Assert.Equal([0L, 0], home.Layers.Select(layer => layer.Known!.Value));
    }

    [Fact]
    public void Rows_with_wrong_sector_numbers_are_repaired_and_rows_that_cannot_be_placed_are_left_out()
    {
        var list = SectorList.Parse(BadRows, Fetched);

        Assert.Equal(
            [
                new SectorRow("Aucopp", 37, 31, 20, 224678, new DateOnly(2015, 5, 27), true),
                new SectorRow("Juenae", 39, 31, 39, 309223, new DateOnly(2015, 7, 14), true),
                new SectorRow("Wredguia", 38, 32, 18, 348581, new DateOnly(2015, 5, 12), false),
                new SectorRow("Synuefai", 38, 31, 18, 386879, new DateOnly(2015, 5, 12), true),
                new SectorRow("Sphuedu", 38, 33, 18, 2110, new DateOnly(2016, 3, 2), false),
                new SectorRow("Wregoe", 39, 32, 18, 423048, new DateOnly(2015, 5, 12), true),
                new SectorRow("Unreadable", 41, 32, 18, 12, null, true),
            ],
            list.Sectors);
        Assert.Equal(["Col 285 Sector"], list.Nebulae.Select(nebula => nebula.Name));
        // Unreadable Too (Wredguia's own row won its place), Eorm Fraae, Loihea and California Sector.
        Assert.Equal(4, list.Dropped);
        Assert.Same(list.Named("Wredguia"), list.At(38, 32, 18));
        Assert.Null(list.Named("Unreadable Too"));

        SectorBlock home = list.Block(38, 18);
        Assert.Equal([31, 32, 33], home.Layers.Select(layer => layer.Layer));
        Assert.Equal([386879L, 348581 + 423048, 2110], home.Layers.Select(layer => layer.Known!.Value));
    }

    [Theory]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("")]
    [InlineData("Sector,Systems,Avg X,Avg Y,Avg Z,Min X,Min Y,Min Z,Max X,Max Y,Max Z,MapSector X,MapSector Y,First Encountered,id64 X,id64 Y,id64 Z\n")]   // titles and nothing under them
    [InlineData("Sector,Systems,Avg X,Avg Y,Avg Z,MapSector X,MapSector Y\nLoihee,1859,-27631.31,-377.73,20027.85,13,30\n")]                                       // the sector numbers are gone
    [InlineData("Sector,Systems,Avg X,Avg Y,Avg Z,id64 X,id64 Y,id64 Z\nLoihee,many,-27631.31,-377.73,20027.85,17,31,34\nLoihea,12,x,y,z,,,\n")]                    // not one row can be read
    public void Anything_that_is_not_the_list_as_Studio_knows_it_is_turned_down_in_plain_words(string text)
    {
        var problem = Assert.Throws<InvalidDataException>(() => SectorList.Parse(text, Fetched));

        Assert.Equal(NotTheList, problem.Message);
    }

    [Fact]
    public void Without_a_list_the_block_still_has_its_sectors_and_their_names_but_no_counts()
    {
        var cubes = new SectorCubes(new ExploreCoverage(temp, new SpanshStandIn()));
        Assert.Null(cubes.List);

        SectorBlock home = cubes.Block(38, 18);
        Assert.Equal([31, 32], home.Layers.Select(layer => layer.Layer));
        Assert.All(home.Layers, layer => Assert.Null(layer.Known));
        Assert.All(home.Sectors, sector => Assert.Null(sector.Known));
        Assert.Equal("Wredguia", home.Sectors.Single(sector => (sector.X, sector.Y, sector.Z) == (38, 32, 18)).Name);
        Assert.Equal("Synuefai", home.Sectors.Single(sector => (sector.X, sector.Y, sector.Z) == (38, 31, 18)).Name);

        // At the grid's corner the block is cut short: two columns along x and two along z.
        SectorBlock corner = cubes.Block(0, 127);
        Assert.Equal(2 * 2 * 2, corner.Sectors.Count);
        Assert.Equal(new Box(-49985, -1305, 137175, -47425, 1255, 139735), corner.Bounds);
        Assert.Throws<ArgumentOutOfRangeException>(() => cubes.Block(128, 18));
        Assert.Throws<ArgumentOutOfRangeException>(() => cubes.Block(38, -1));
    }

    [Fact]
    public async Task The_copy_the_flat_map_keeps_is_read_and_read_again_when_a_new_list_comes()
    {
        string kept = Path.Combine(temp, "edastro-sector-list.csv");
        File.WriteAllText(kept, ListText);
        File.SetLastWriteTimeUtc(kept, DateTime.UtcNow.AddDays(-8));   // old enough for EDAstro to be asked again
        var edastro = new SpanshStandIn().Then(ListText.Replace("Eorm Fraae,3650,", "Eorm Fraae,3700,", StringComparison.Ordinal));
        var coverage = new ExploreCoverage(temp, edastro);
        var cubes = new SectorCubes(coverage);
        Assert.Equal(kept, coverage.FilePath);

        SectorList before = cubes.List!;
        Assert.Equal(3650, before.At(12, 32, 34)!.Known);
        Assert.Same(before, cubes.List);   // read once
        Assert.Empty(edastro.Asked);       // reading the copy asks nobody

        int refreshed = 0;
        coverage.Refreshed += (_, _) => refreshed++;
        Assert.Null(await coverage.Refresh());
        Assert.Equal((1, 1), (refreshed, edastro.Asked.Count));
        Assert.NotSame(before, cubes.List);
        Assert.Equal(3700, cubes.List!.At(12, 32, 34)!.Known);
        Assert.Equal(12109, cubes.Block(13, 34).Layers.Single(layer => layer.Layer == 32).Known);

        // Within the week nothing is fetched, and nothing read again.
        SectorList after = cubes.List;
        Assert.Null(await coverage.Refresh());
        Assert.Equal((1, 1), (refreshed, edastro.Asked.Count));
        Assert.Same(after, cubes.List);
    }

    [Fact]
    public async Task A_copy_that_cannot_be_read_counts_as_none_and_a_failed_fetch_changes_nothing()
    {
        File.WriteAllText(Path.Combine(temp, "edastro-sector-list.csv"), "not the list");
        var coverage = new ExploreCoverage(temp, new SpanshStandIn().Then("Too many requests", HttpStatusCode.TooManyRequests));
        var cubes = new SectorCubes(coverage);
        int refreshed = 0;
        coverage.Refreshed += (_, _) => refreshed++;

        Assert.Null(cubes.List);
        Assert.Equal("EDAstro answered with error 429. Try again later.", await coverage.Refresh());
        Assert.Equal(0, refreshed);
        Assert.Null(cubes.List);
    }

    [Fact]
    public async Task A_new_list_that_cannot_be_written_to_disk_is_still_the_one_the_3D_map_shows()
    {
        // A week-old copy on disk, and a folder where the new copy's half-written file would go: Studio cannot write it.
        string kept = Path.Combine(temp, "edastro-sector-list.csv");
        File.WriteAllText(kept, ListText);
        File.SetLastWriteTimeUtc(kept, DateTime.UtcNow.AddDays(-8));
        Directory.CreateDirectory(kept + ".tmp");
        var edastro = new SpanshStandIn().Then(ListText.Replace("Eorm Fraae,3650,", "Eorm Fraae,3700,", StringComparison.Ordinal));
        var coverage = new ExploreCoverage(temp, edastro);
        var cubes = new SectorCubes(coverage);
        Assert.Equal(3650, cubes.List!.At(12, 32, 34)!.Known);

        Assert.Null(await coverage.Refresh());

        Assert.Equal(ListText, File.ReadAllText(kept));   // the old copy is still the one on disk
        Assert.False(coverage.Stale);                     // the flat map has the new list
        Assert.Equal(3700, cubes.List!.At(12, 32, 34)!.Known);   // and so has the 3D map: the two agree
        Assert.InRange(cubes.List.FetchedUtc, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(1));

        // Even with no copy on disk at all before: the 3D map has its counts at once, not "no sector list yet".
        string other = Directory.CreateTempSubdirectory("EliteStudioSectorCubes-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(other, "edastro-sector-list.csv.tmp"));
            var first = new ExploreCoverage(other, new SpanshStandIn().Then(ListText));
            var firstCubes = new SectorCubes(first);
            Assert.Null(firstCubes.List);
            Assert.Null(await first.Refresh());
            Assert.False(File.Exists(first.FilePath));
            Assert.Equal(3650, firstCubes.List!.At(12, 32, 34)!.Known);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    /// <summary>
    /// Reads the player's own copy of EDAstro's list, kept by Studio in its data folder, to see that
    /// the whole list reads as it should: only its two known bad rows repaired, none left out, and
    /// round the player the four layers the plan describes, the middle two holding nearly everything.
    /// Nothing goes out to the internet, but it depends on this PC, so it is skipped unless the
    /// environment variable ELITE_STUDIO_LIVE is 1 (and there is a copy).
    /// </summary>
    [Fact]
    public void Live_the_players_own_copy_of_the_list_reads_whole()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ELITE_STUDIO_LIVE") == "1", "Set ELITE_STUDIO_LIVE=1 to read the player's own copy of EDAstro's list.");
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Elite Studio", "edastro-sector-list.csv");
        Assert.SkipUnless(File.Exists(file), "There is no copy of EDAstro's list on this PC yet.");

        string text = File.ReadAllText(file);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var list = SectorList.Parse(text, File.GetLastWriteTimeUtc(file));
        SectorBlock home = list.Block(38, 18);
        output.WriteLine($"Read in {clock.ElapsedMilliseconds} ms: {list.Sectors.Count:N0} sectors, {list.Nebulae.Count} nebulae, {list.Dropped} left out; repaired: {string.Join(", ", list.Sectors.Where(row => row.Repaired).Select(row => row.Name))}");
        foreach (BlockLayer layer in home.Layers) output.WriteLine($"  layer {layer.Layer} {layer.Name}: {layer.Known:N0}");

        Assert.InRange(list.Sectors.Count, 11_000, 13_000);
        Assert.InRange(list.Nebulae.Count, 400, 500);
        Assert.Equal(0, list.Dropped);
        Assert.Equal(["Aucopp", "Juenae"], list.Sectors.Where(row => row.Repaired).Select(row => row.Name).Order());
        Assert.All(list.Sectors, row => Assert.InRange(row.Y, Galaxy.FirstNamedLayer, Galaxy.LastNamedLayer));
        Assert.Equal([30, 31, 32, 33], home.Layers.Select(layer => layer.Layer));
        Assert.InRange((double)(home.Layers[1].Known + home.Layers[2].Known)!.Value / home.Layers.Sum(layer => layer.Known!.Value), 0.95, 1);
        // Every nebula's own sector on the list is one of EDTS's, but for one EDTS does not have (on 14 October 2026).
        Assert.Equal(["NGC 2392 Sector"], list.Nebulae.Where(nebula => HaRegions.Named(nebula.Name) is null).Select(nebula => nebula.Name));
    }
}
