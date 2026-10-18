using System.Net;
using EliteStudio.Nav;

namespace EliteStudio.Tests;

/// <summary>
/// EDAstro's list of nebulae, fetched only when the player asks and kept on this PC. In tests\Fixtures\nav:
///   nebulae-coordinates.csv  EDAstro's real list as fetched on 14 October 2026 (dated 10 October on its
///                            files page), cut down from 5,843 nebulae to 11, the lines byte for byte:
///                            eight of the real sky's nebulae, one planetary nebula and two the game
///                            adds by its rule, two of those three with their names in double quotes
/// EDAstro itself stands in as <see cref="SpanshStandIn"/>, so nothing goes near the internet.
/// </summary>
public sealed class NebulaListTests : IDisposable
{
    static readonly string Recorded = SpanshStandIn.Recorded("nebulae-coordinates.csv");
    const string Unreachable = "EDAstro could not be reached. Check your internet connection and try again.";
    const string NotTheList = "EDAstro's nebula list is not laid out the way Studio knows it, so Studio cannot use it.";

    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioNebulae-").FullName;

    public void Dispose() => Directory.Delete(temp, recursive: true);

    /// <summary>The 3D map's own folder in Studio's data folder; not made until something is kept.</summary>
    string Folder => Path.Combine(temp, "heatmap");

    [Fact]
    public void The_list_is_read_by_its_column_names_with_names_in_quotes_as_they_are()
    {
        IReadOnlyList<ListedNebula> nebulae = NebulaList.Parse(Recorded);

        Assert.Equal(11, nebulae.Count);
        Assert.Equal(new ListedNebula("California Nebula", "California Sector LX-T b3-0", -337.906, -211.938, -927.031, "real", 18), nebulae.Single(nebula => nebula.Name == "California Nebula"));
        Assert.Equal(["Aemonz WZ-Y e6771 (Lazurite Nebula)", "Agnairt AA-A h36", "Dr. Kay's Soul"], nebulae.Take(3).Select(nebula => nebula.Name));
        Assert.Equal(("Greae Hypa DF-R d4-23", "procgen", false), (nebulae[2].System, nebulae[2].Kind, nebulae[2].Real));
        Assert.Equal(8, nebulae.Count(nebula => nebula.Real));

        // Columns moved and one more added: the same nebula. Without the region column: no region.
        Assert.Equal(new ListedNebula("Pleiades", "Maia", -81.7812, -149.438, -343.375, "real", null),
            NebulaList.Parse("Type,Z,Y,X,Name,System,Note\nreal,-343.375,-149.438,-81.7812,Pleiades,Maia,\"a note, with a comma\"\n").Single());
        // EDAstro ends its lines the Windows way, but the other way reads the same.
        Assert.Equal(nebulae, NebulaList.Parse(Recorded.ReplaceLineEndings("\n")));
    }

    [Theory]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("")]
    [InlineData("Name,System,X,Y,Z,Type,RegionID\n")]                                                                       // titles and nothing under them
    [InlineData("Name,System,X,Y,Type,RegionID\nCalifornia Nebula,California Sector LX-T b3-0,-337.906,-211.938,real,18\n")]   // a column Studio needs is gone
    [InlineData("Name,System,X,Y,Z,Type,RegionID\nCalifornia Nebula,California Sector LX-T b3-0,west,-211.938,-927.031,real,18\n,Maia,1,2,3,real,18\n")]
    public void Anything_that_is_not_the_list_as_Studio_knows_it_is_turned_down_in_plain_words(string text)
    {
        var problem = Assert.Throws<InvalidDataException>(() => NebulaList.Parse(text));

        Assert.Equal(NotTheList, problem.Message);
    }

    [Fact]
    public async Task The_list_is_fetched_when_asked_kept_for_a_week_and_an_older_copy_serves_while_EDAstro_cannot_be_reached()
    {
        var net = new SpanshStandIn().Then(Recorded);
        var list = new NebulaList(Folder, net);
        Assert.Equal(Path.Combine(Folder, "nebulae.csv"), list.FilePath);
        Assert.Null(list.Nebulae);
        Assert.Null(list.FetchedUtc);
        Assert.True(list.Stale);
        Assert.Empty(net.Asked);   // nothing is fetched until the player asks

        Assert.Null(await list.Refresh());
        Assert.Equal(["GET /mapcharts/files/nebulae-coordinates.csv"], net.Asked);
        Assert.Matches(@"^EliteStudio/\d+\.\d+\.\d+ \(Elite Dangerous companion app; \+https://github\.com/[\w-]+/Elite-Studio\)$", net.UserAgent);
        Assert.Equal((11, false), (list.Nebulae?.Count, list.Stale));
        Assert.InRange(list.FetchedUtc!.Value, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow);
        // Kept as it came, in a folder made for it, with nothing half-written left beside it.
        Assert.Equal(Recorded, File.ReadAllText(list.FilePath));
        Assert.Equal(["nebulae.csv"], Directory.GetFiles(Folder).Select(path => Path.GetFileName(path)));

        // Asked for again within the week: the copy serves, and EDAstro is left alone.
        Assert.Null(await list.Refresh());
        Assert.Single(net.Asked);

        // The next start finds the copy on disk, and a PC that is offline does not matter.
        var offline = new SpanshStandIn();
        var nextStart = new NebulaList(Folder, offline);
        Assert.Equal((11, false), (nextStart.Nebulae?.Count, nextStart.Stale));
        Assert.Null(await nextStart.Refresh());
        Assert.Empty(offline.Asked);

        // Eight days on EDAstro may be asked again. It cannot be reached: the old list stays, and the answer says why.
        File.SetLastWriteTimeUtc(list.FilePath, DateTime.UtcNow.AddDays(-8));
        var weekLater = new NebulaList(Folder, offline);
        Assert.True(weekLater.Stale);
        Assert.Equal(Unreachable, await weekLater.Refresh());
        Assert.Equal(11, weekLater.Nebulae?.Count);
        // EDAstro says no, or something else than the list comes back: the same, and nothing is tried again by itself.
        offline.Then("Too many requests", HttpStatusCode.TooManyRequests).Then("<html><body>Maintenance</body></html>");
        Assert.Equal("EDAstro answered with error 429. Try again later.", await weekLater.Refresh());
        Assert.Equal(NotTheList, await weekLater.Refresh());
        Assert.Equal((11, true), (weekLater.Nebulae?.Count, weekLater.Stale));
        Assert.Equal(3, offline.Asked.Count);
        // And then the list itself, with one nebula fewer: that is the list from now on, and the copy kept.
        string shorter = Recorded[..Recorded.LastIndexOf("Witch Head Nebula", StringComparison.Ordinal)];
        offline.Then(shorter);
        Assert.Null(await weekLater.Refresh());
        Assert.Equal((10, false), (weekLater.Nebulae?.Count, weekLater.Stale));
        Assert.Equal(10, new NebulaList(Folder, offline).Nebulae?.Count);
        Assert.Equal(4, offline.Asked.Count);
    }

    [Fact]
    public async Task Two_requests_at_once_share_one_fetch_and_an_unreadable_copy_counts_as_none()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "nebulae.csv"), "not the list");
        var net = new SpanshStandIn().Then(Recorded);
        var list = new NebulaList(Folder, net);
        Assert.Null(list.Nebulae);
        Assert.True(list.Stale);

        Task<string?> first = list.Refresh(), second = list.Refresh();
        Assert.All(await Task.WhenAll(first, second), Assert.Null);
        Assert.Single(net.Asked);
        Assert.Equal(11, list.Nebulae?.Count);
    }
}
