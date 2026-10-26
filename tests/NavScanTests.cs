using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Nav;

namespace EliteStudio.Tests;

/// <summary>
/// Nav scan (<see cref="NavScan"/>): screenshots of the game's Navigation panel read for system names, which are checked
/// as every other check is. Nothing goes near the internet (<see cref="SpanshStandIn"/> answers Spansh's search by exact
/// names), no sound is made (the sounds go to a list), and no picture of the owner's is used: the reader's text is
/// made up here, line by line where the panel has it, from the misreads Windows' text reader really made of the owner's
/// own pictures on 15 October 2026 ("COL 285 SECTOR WO-J A3B-O", "VT-R B4-5SYSTEM", "NX-G" for NX-Q, "1159B"). The
/// pictures themselves are a few bytes in a folder of the test's own. The one test of Windows' text reader reads a
/// picture it draws itself, and is skipped on a PC without an English reader. The commander is in Ngarawe, by the
/// systems of Col 285 Sector the owner's picture shows, as the owner's journal places it.
/// </summary>
public sealed partial class NavScanTests : IDisposable
{
    static readonly (double X, double Y, double Z) Ngarawe = (158.96875, -74.65625, 52.9375);

    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioNavScan-").FullName;
    readonly SpanshStandIn net = new();
    readonly GameState game = new();
    readonly List<Spansh> clients = [];
    readonly List<NavScan> scans = [];
    readonly Dictionary<string, PictureText> texts = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> read = [];                     // the pictures the reader was given, by file name
    readonly List<(string Sound, int Volume)> played = [];
    HeatmapSurvey? survey;
    FineMap? fine;
    HeatmapFinds? finds;
    DateTime clock = DateTime.UtcNow;                    // the memory of checks' clock and Nav scan's

    public NavScanTests() => Directory.CreateDirectory(Pictures);

    public void Dispose()
    {
        foreach (NavScan scan in scans) scan.Dispose();
        foreach (Spansh client in clients) client.Dispose();
        Directory.Delete(temp, recursive: true);
    }

    string Folder => Path.Combine(temp, "heatmap");
    string Pictures => Path.Combine(temp, "pictures");
    string SettingsFile => Path.Combine(temp, "settings.json");

    /// <summary>
    /// A fresh start of Studio: a new session with what earlier ones kept, the commander where <paramref name="at"/> says
    /// (Ngarawe unless told). Nav scan then switched as <paramref name="on"/> says; left as it was saved with null.
    /// </summary>
    NavScan Start(bool? on = true, (double X, double Y, double Z)? at = null, Func<IReadOnlyList<string>>? places = null, TimeSpan? lookEvery = null,
        System.Net.Http.HttpMessageHandler? handler = null)
    {
        (double x, double y, double z) = at ?? Ngarawe;
        Line(string.Create(CultureInfo.InvariantCulture, $$"""{ "event":"Location", "StarSystem":"Here", "SystemAddress":1001, "StarPos":[{{x}},{{y}},{{z}}] }"""));
        var spansh = new Spansh(handler ?? net);
        clients.Add(spansh);
        survey = new HeatmapSurvey(spansh, Folder);
        fine = new FineMap(Folder);
        finds = new HeatmapFinds(game, survey, fine, new Settings(SettingsFile), new CheckMemory(Folder) { Now = () => clock });
        var scan = new NavScan(game, finds, Folder, new Settings(SettingsFile), places ?? (() => [Pictures]))
        {
            Reader = (path, _) =>
            {
                lock (read) read.Add(Path.GetFileName(path));
                return Task.FromResult(texts.GetValueOrDefault(Path.GetFileName(path)) ?? new PictureText([]));
            },
            Play = (sound, volume) =>
            {
                lock (played) played.Add((sound, volume));
            },
            SettleWait = TimeSpan.Zero,
            Now = () => clock,
            LookEvery = lookEvery ?? TimeSpan.FromSeconds(30),
        };
        scans.Add(scan);
        if (on is { } switched) scan.On = switched;
        return scan;
    }

    void Line(string json)
    {
        using JsonDocument line = JsonDocument.Parse(json);
        game.Apply(line.RootElement.Clone(), replay: false);
        game.RaiseChanged();
    }

    /// <summary>A screenshot made now, a few bytes in the pictures' folder, which the reader reads as <paramref name="text"/>.</summary>
    string Picture(string name, PictureText text, int bytes = 3)
    {
        texts[name] = text;
        string path = Path.Combine(Pictures, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>
    /// What the reader makes of the Navigation panel, row by row: each row's words from the left as the reader gives
    /// them, and its distance in the panel's own column far to the right ("5.34Ly"), level with it.
    /// </summary>
    static PictureText Panel(params (string Read, double? Ly)[] rows)
    {
        var lines = new List<ReadLine>();
        for (int row = 0; row < rows.Length; row++)
        {
            double y = 600 + 35 * row, x = 590;
            var words = new List<ReadWord>();
            foreach (string word in rows[row].Read.Split(' '))
            {
                words.Add(new ReadWord(word, x, y, 14 * word.Length, 20));
                x += 14 * word.Length + 10;
            }
            lines.Add(new ReadLine(words));
            if (rows[row].Ly is { } ly) lines.Add(new ReadLine([new ReadWord(ly.ToString("0.00", CultureInfo.InvariantCulture) + "Ly", 1220, y, 80, 20)]));
        }
        return new PictureText(lines);
    }

    static long Id(string name) => ProcGenNames.Decode(name)!.Cube.Address;

    /// <summary>The names and spellings a picture's text gives, as the page would list the spellings.</summary>
    static string[][] Spelt(PictureText text, (double X, double Y, double Z)? here) =>
        [.. NavScanNames.Read(text, here).Names.Select(name => name.Variants.Select(variant => variant.Name).ToArray())];

    /// <summary>The request for the systems under these exact names, word for word as Studio sends it.</summary>
    static string NamesAsked(params string[] names) =>
        "POST /api/systems/search {\"filters\":{\"name\":{\"value\":[" + string.Join(",", names.Select(name => $"\"{name}\"")) + "]}},\"size\":"
        + names.Length.ToString(CultureInfo.InvariantCulture) + ",\"page\":0}";

    /// <summary>Spansh's answer to a search by names, in its layout: these systems, as many as it found.</summary>
    static string Named(params string[] names) => JsonSerializer.Serialize(new
    {
        count = names.Length,
        from = 0,
        results = names.Select(name => new Dictionary<string, object> { ["bodies"] = Array.Empty<object>(), ["id64"] = Id(name), ["name"] = name, ["x"] = 1.5, ["y"] = 2.5, ["z"] = 3.5 }),
        size = Math.Max(1, names.Length),
    });

    static async Task Until(Func<bool> done)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "gave up waiting");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    // ------------------------------------------------------------ making out the names

    [Fact]
    public void The_readers_real_misreads_of_the_owners_pictures_are_put_right()
    {
        // As the reader read them, with the panel's icons, glued "SYSTEM"s and look-alike letters and digits.
        PictureText text = Panel(
            ("COL 285 SECTOR WO-J A3B-O", 5.34),           // a38-0: B for 8, O for 0
            ("O COL 285 SECTOR wo-J A38-o", 5.34),         // the row's icon read as an O
            ("COL 285 SECTOR RI-J C9-1B", 5.29),           // c9-18
            ("@ COL 285 SECTOR RI-J cg-17", 5.55),         // a small g for 9
            ("COL 285 SECTOR JR-S BIB-I", 4.23),           // b18-1
            ("NAV BEACON", null), ("KREUTZ ORBITAL 2,687LS", null), ("DRAGOONS REST", null), ("NGARAWE SYSTEM", null));

        Assert.Equal(
        [
            ["Col 285 Sector WO-J a38-0"], ["Col 285 Sector WO-J a38-0"], ["Col 285 Sector RI-J c9-18"], ["Col 285 Sector RI-J c9-17"], ["Col 285 Sector JR-S b18-1"],
        ], Spelt(text, Ngarawe));

        // The owner's other picture, taken in Shui Wei Sector VT-R b4-5 (as the owner's journal places it).
        PictureText shuiWei = Panel(
            ("SHUI WE-I SECTOR", null),                    // the panel's header: the commander's own system over two lines,
            ("VT-R B4-5SYSTEM", null),                     // with "SYSTEM" glued on
            ("SHUI WEI SECTOR VT-R B4-4", 2.97),
            ("SHUI WE-I SECTOR UT-R B4-2", 9.98),          // a hyphen in the sector's name
            ("SHIJI WEI SECTOR VT-R B4-4", null));         // a sector that is none: no name
        Assert.Equal([["Shui Wei Sector VT-R b4-5"], ["Shui Wei Sector VT-R b4-4"], ["Shui Wei Sector UT-R b4-2"]], Spelt(shuiWei, (117.65625, -131.71875, 42.1875)));

        // Read in a piece of the picture without the header's first line: the sectors the picture names are tried.
        Assert.Equal([["Shui Wei Sector UT-R b4-2"], ["Shui Wei Sector VT-R b4-5"]], Spelt(Panel(("SHUI WEI SECTOR UT-R B4-2", null), ("VT-R B4-5-SYSTE", null)), Ngarawe));

        // Each decoded into its cube: the number the game gives the system, and where it lies to within half the cube.
        DecodedName decoded = NavScanNames.Read(text, Ngarawe).Names[0].Variants[0];
        Assert.Equal((5384277740416L, (160.0, -70.0, 60.0), 5.0), (decoded.Cube.Address, decoded.Cube.Centre, decoded.HalfWidth));
        Assert.Equal(("COL 285 SECTOR WO-J A3B-O", 5.34), (NavScanNames.Read(text, Ngarawe).Names[0].Read, NavScanNames.Read(text, Ngarawe).Names[0].DistanceLy));

        // Tidying: capitals, dashes, a distance at the end taken off (light years kept), "SYSTEM" taken off.
        Assert.Equal(("VT-R B4-5", (double?)null), NavScanNames.Tidy("VT-R B4-5SYSTEM"));
        Assert.Equal(("O HIP 12766", (double?)5.02), NavScanNames.Tidy("O HIP 12766 S.02Ly"));
        Assert.Equal(("COL 285 SECTOR NX-Q B19-5", (double?)null), NavScanNames.Tidy("COL 285 SECTOR NX–Q B19 - 5"));
        Assert.Equal(("KREUTZ ORBITAL", (double?)null), NavScanNames.Tidy("KREUTZ ORBITAL 2,687Ls"));
        Assert.Equal(("TAWARET", (double?)null), NavScanNames.Tidy("TAWARET 420Ly"));   // a distance without its point: a point the reader lost
    }

    [Fact]
    public void Q_G_and_O_give_each_other_and_the_distance_on_the_row_keeps_the_one_that_can_be_there()
    {
        // WO-J may be WQ-J or WG-J: all three name cubes of Col 285 Sector, within 3,000 ly of the commander.
        string[][] unclear = Spelt(Panel(("COL 285 SECTOR WO-J A3B-O", null)), Ngarawe);
        Assert.Equal([["Col 285 Sector WO-J a38-0", "Col 285 Sector WQ-J a38-0", "Col 285 Sector WG-J a38-0"]], unclear);

        // The panel says 5.34 ly: only WO-J's cube is that far from Ngarawe (WQ-J's lies some 520 ly east).
        Assert.Equal([["Col 285 Sector WO-J a38-0"]], Spelt(Panel(("COL 285 SECTOR WO-J A3B-O", 5.34)), Ngarawe));

        // NX-G as the reader read NX-Q: of the three, only Q names a cube of the sector at all.
        Assert.Equal([["Col 285 Sector NX-Q b19-5"]], Spelt(Panel(("COL 285 SECTOR NX-G B19-5", null)), Ngarawe));
        Assert.Equal([["Col 285 Sector NX-Q b19-5"]], Spelt(Panel(("COL 285 SECTOR NX-0 819-5", 5.34)), Ngarawe));
    }

    [Fact]
    public void A_name_is_kept_only_where_it_can_be()
    {
        PictureText far = Panel(("EEPHONTH AA-A H0", null));   // the far side of the galaxy

        // A galaxy map picture (no distance): within 3,000 ly of the commander.
        Assert.Empty(Spelt(far, Ngarawe));
        Assert.Equal([["Eephonth AA-A h0"]], Spelt(far, (-35000, -100, 17000)));

        // A distance on the row: the cube's middle that far, give or take half its diagonal and a light year. WO-J a38-0's
        // cube of 10 ly lies 8.5 ly from Ngarawe: 5.34 ly fits (8.5 - 5.34 is less than 8.66 + 1), 25 ly does not.
        Assert.Single(Spelt(Panel(("COL 285 SECTOR WO-J A38-0", 5.34)), Ngarawe));
        Assert.Empty(Spelt(Panel(("COL 285 SECTOR WO-J A38-0", 25)), Ngarawe));

        // Studio does not know where the commander is: every spelling that decodes is kept.
        Assert.Equal([["Eephonth AA-A h0"]], Spelt(far, null));
        Assert.Equal(3, Spelt(Panel(("COL 285 SECTOR WO-J A3B-O", 5.34)), null)[0].Length);
    }

    /// <summary>
    /// What the reader makes of one row of the Navigation panel in several looks (the picture whole and in pieces, at two
    /// sizes): each look's line where the row is, and the row's distance beside it, as each look read it.
    /// </summary>
    static PictureText Looked(double? ly, params string[] looks)
    {
        var lines = new List<ReadLine>();
        foreach (string look in looks)
        {
            double x = 590;
            var words = new List<ReadWord>();
            foreach (string word in look.Split(' '))
            {
                words.Add(new ReadWord(word, x, 600, 14 * word.Length, 20));
                x += 14 * word.Length + 10;
            }
            lines.Add(new ReadLine(words));
            if (ly is { } distance) lines.Add(new ReadLine([new ReadWord(distance.ToString("0.00", CultureInfo.InvariantCulture) + "Ly", 1220, 600, 80, 20)]));
        }
        return new PictureText(lines);
    }

    [Fact]
    public async Task A_slip_of_one_look_at_a_row_is_out_voted_by_the_other_looks_and_never_becomes_a_find()
    {
        // Four looks at a row read RI-J c9-18; one reads its 8 as a 3: RI-J c9-13, another system of the same cube (no
        // distance can tell the two apart), which does not exist.
        PictureText text = Looked(5.29, "COL 285 SECTOR RI-J C9-18", "COL 285 SECTOR RI-J C9-1B", "COL 285 SECTOR RI-J C9-13", "@ COL 285 SECTOR RI-J C9-18", "COL 285 SECTOR RI-J C9-18");
        NameRead row = Assert.Single(NavScanNames.Read(text, Ngarawe).Names);
        Assert.Equal(["Col 285 Sector RI-J c9-18"], row.Variants.Select(variant => variant.Name));
        Assert.Equal(((double?)5.29, "COL 285 SECTOR RI-J C9-18"), (row.DistanceLy, row.Read));

        // Checked: one name, recorded. The slip is never asked about, never pings, and is never kept as a find.
        NavScan scan = Start();
        net.Then(Named("Col 285 Sector RI-J c9-18"));
        scan.Offer(Picture("panel.jpg", text));
        await scan.Idle;
        Assert.Equal([NamesAsked("Col 285 Sector RI-J c9-18")], net.Asked);
        NavScanName name = Assert.Single(scan.View().Names);
        Assert.Equal(("Col 285 Sector RI-J c9-18", HeatmapFinds.Recorded, false), (name.Name, name.State, name.Unsure));
        Assert.Empty(finds!.Finds);
        Assert.Equal([(NavScanSounds.Click, 70)], played);

        // A row whose two looks disagree, once each, is as unsure as they are: both spellings, checked together.
        Assert.Equal([["Col 285 Sector RI-J c9-18", "Col 285 Sector RI-J c9-13"]], Spelt(Looked(5.29, "COL 285 SECTOR RI-J C9-18", "COL 285 SECTOR RI-J C9-13"), Ngarawe));
    }

    [Fact]
    public async Task A_spelling_that_only_a_misread_left_behind_is_not_taken_for_the_name()
    {
        NavScan scan = Start();
        // An earlier misread left WQ-J a38-0 in the memory of checks as not recorded: no proof that it exists.
        finds!.Memory.Put("Col 285 Sector WQ-J a38-0", Id("Col 285 Sector WQ-J a38-0"), CheckMemory.NotRecorded, CheckMemory.BySpansh, "not found by Spansh's system search");

        // The row read as unclearly again: its three spellings are all still the name's, and WO-J, which Spansh has,
        // settles it. (Before, the misread was preferred, and WO-J was never asked about.)
        net.Then(Named("Col 285 Sector WO-J a38-0"));
        scan.Offer(Picture("galaxy-map.jpg", Panel(("COL 285 SECTOR WO-J A3B-O", null))));
        await scan.Idle;
        Assert.Equal([NamesAsked("Col 285 Sector WO-J a38-0", "Col 285 Sector WG-J a38-0")], net.Asked);
        NavScanName name = Assert.Single(scan.View().Names);
        Assert.Equal(("Col 285 Sector WO-J a38-0", HeatmapFinds.Recorded, false), (name.Name, name.State, name.Unsure));
        Assert.Equal([(NavScanSounds.Click, 70)], played);
    }

    [Fact]
    public void A_row_of_the_panel_read_without_its_distance_keeps_only_the_spellings_within_the_panels_reach()
    {
        // The Navigation panel (it has distances on its rows), with one row whose distance the reader missed and whose
        // letters hold a G: WG-J, WO-J or WQ-J. Only WO-J's cube lies within the panel's 60 ly of the commander.
        Assert.Equal([["Col 285 Sector JR-S b18-1"], ["Col 285 Sector WO-J a38-0"]], Spelt(Panel(("COL 285 SECTOR JR-S BIB-I", 4.23), ("COL 285 SECTOR WG-J A3B-O", null)), Ngarawe));
        // On a picture with no distance at all (the galaxy map), all three, within 3,000 ly.
        Assert.Equal([["Col 285 Sector WG-J a38-0", "Col 285 Sector WO-J a38-0", "Col 285 Sector WQ-J a38-0"]], Spelt(Panel(("COL 285 SECTOR WG-J A3B-O", null)), Ngarawe));

        // A row read one way may still be many systems: its last number read "GG" may be 66, 69, 96 or 99, of three cubes
        // on the galaxy map: 12, checked together (one reading, so not unreadable). On the panel, its distance leaves one
        // cube: 4.
        PictureNames many = NavScanNames.Read(Panel(("COL 285 SECTOR WO-J A3B-GG", null)), Ngarawe);
        Assert.Equal((12, 0), (Assert.Single(many.Names).Variants.Count, many.Unreadable));
        Assert.Equal([["Col 285 Sector WO-J a38-66", "Col 285 Sector WO-J a38-69", "Col 285 Sector WO-J a38-96", "Col 285 Sector WO-J a38-99"]],
            Spelt(Panel(("COL 285 SECTOR WO-J A3B-GG", 5.34)), Ngarawe));

        // A row the looks read in nine different ways, none twice, is unreadable: not checked, but counted.
        PictureNames unreadable = NavScanNames.Read(Looked(null, [.. Enumerable.Range(11, 9).Select(n => $"COL 285 SECTOR RI-J C9-{n}")]), Ngarawe);
        Assert.Equal((0, 1), (unreadable.Names.Count, unreadable.Unreadable));
        // Eight are as unsure as they are: each of them, checked together.
        Assert.Equal(8, Assert.Single(NavScanNames.Read(Looked(null, [.. Enumerable.Range(11, 8).Select(n => $"COL 285 SECTOR RI-J C9-{n}")]), Ngarawe).Names).Variants.Count);
    }

    [Fact]
    public void A_light_year_figure_on_a_galaxy_map_picture_does_not_make_its_star_names_the_panels()
    {
        // The galaxy map, with two labels of systems far from the commander (380 and 700 ly from Ngarawe), no distance
        // beside either, and one light-year figure elsewhere: the selected system's distance on its card. The panel's
        // 60 ly would leave neither; the galaxy map's 3,000 ly keeps both.
        PictureText map = new(
        [
            Words(300, 200, "COL 285 SECTOR HN-T B17-3"),
            Words(1500, 900, "COL 285 SECTOR AA-A A0"),
            Words(100, 1000, "400.12", "LY"),
            Words(1500, 900, "COL 285 SECTOR AA-A A0"),   // another look at the same label
        ]);
        Assert.Equal([["Col 285 Sector HN-T b17-3"], ["Col 285 Sector AA-A a0"]], Spelt(map, Ngarawe));

        // Two such figures (a plotted route's length too) are no column either.
        PictureText routed = new([.. map.Lines, Words(100, 1040, "412.5", "LY")]);
        Assert.Equal(2, Spelt(routed, Ngarawe).Length);

        // The panel's column of distances, even with no distance read beside a row: every row is the panel's. A row of a
        // far system read without its distance there is no name of the panel's (the panel lists none so far).
        PictureText panel = Panel(("COL 285 SECTOR JR-S BIB-I", 4.23), ("COL 285 SECTOR RI-J C9-18", 5.29), ("COL 285 SECTOR WO-J A38-0", 5.34));
        Assert.Equal([["Col 285 Sector JR-S b18-1"], ["Col 285 Sector RI-J c9-18"], ["Col 285 Sector WO-J a38-0"]],
            Spelt(new PictureText([.. panel.Lines, Words(590, 2000, "COL 285 SECTOR HN-T B17-3")]), Ngarawe));
        // Without the column, a row of the panel next to one with its distance is the panel's too: and a label far below,
        // with no distance beside it or its neighbours, is judged by the galaxy map's rule.
        PictureText two = Panel(("COL 285 SECTOR JR-S BIB-I", 4.23), ("COL 285 SECTOR HN-T B17-3", null));
        Assert.Equal([["Col 285 Sector JR-S b18-1"]], Spelt(two, Ngarawe));
        Assert.Equal([["Col 285 Sector JR-S b18-1"], ["Col 285 Sector HN-T b17-3"]], Spelt(new PictureText([two.Lines[0], two.Lines[1], Words(590, 1400, "COL 285 SECTOR HN-T B17-3")]), Ngarawe));
    }

    /// <summary>A line of words the reader gave, from <paramref name="x"/> along <paramref name="y"/>, as <see cref="Panel"/> spaces them.</summary>
    static ReadLine Words(double x, double y, params string[] words)
    {
        var line = new List<ReadWord>();
        foreach (string word in words)
        {
            line.Add(new ReadWord(word, x, y, 14 * word.Length, 20));
            x += 14 * word.Length + 10;
        }
        return new ReadLine(line);
    }

    [Fact]
    public async Task A_name_every_look_read_alike_is_kept_unsure_however_many_systems_its_letters_may_be()
    {
        // "GO-Q", read so by every look of a galaxy map picture (no distances): G, O and Q give each other, so the row may
        // be any of 27 systems of Col 285 Sector, every one within 3,000 ly. One reading, one unsure name: kept.
        PictureText text = Looked(null, "COL 285 SECTOR GO-Q A5-0", "COL 285 SECTOR GO-Q A5-0", "COL 285 SECTOR GO-Q A5-0", "@ COL 285 SECTOR GO-Q A5-0", "COL 285 SECTOR GO-Q A5-0");
        PictureNames names = NavScanNames.Read(text, Ngarawe);
        NameRead row = Assert.Single(names.Names);
        string[] spellings = [.. row.Variants.Select(variant => variant.Name)];
        Assert.Equal([.. "GOQ".SelectMany(first => "OQG".SelectMany(second => "QOG".Select(third => $"Col 285 Sector {first}{second}-{third} a5-0")))], spellings);
        Assert.Equal(0, names.Unreadable);

        // Checked as one unsure name, all 27 in the one request; the one Spansh has was the name.
        NavScan scan = Start();
        net.Then(Named("Col 285 Sector OQ-G a5-0"));
        scan.Offer(Picture("galaxy-map.jpg", text));
        await scan.Idle;
        Assert.Equal([NamesAsked(spellings)], net.Asked);
        NavScanName name = Assert.Single(scan.View().Names);
        Assert.Equal(("Col 285 Sector OQ-G a5-0", HeatmapFinds.Recorded, false), (name.Name, name.State, name.Unsure));
        Assert.Equal((1, 1, 0), (scan.View().Pictures[0].Names, scan.View().Pictures[0].Recorded, scan.View().Pictures[0].Unreadable));
    }

    [Fact]
    public async Task A_name_that_could_not_be_read_is_counted_and_said()
    {
        // One row clear, one read nine different ways by nine looks (none twice): that one is not checked, and the page and
        // the overlay say a name could not be read.
        var lines = new List<ReadLine>(Panel(("COL 285 SECTOR JR-S BIB-I", 4.23)).Lines);
        foreach (int n in Enumerable.Range(11, 9)) lines.Add(Words(590, 635, "COL", "285", "SECTOR", "RI-J", $"C9-{n}"));
        NavScan scan = Start();
        net.Then(Named("Col 285 Sector JR-S b18-1"));
        scan.Offer(Picture("panel.jpg", new PictureText(lines)));
        await scan.Idle;
        Assert.Equal([NamesAsked("Col 285 Sector JR-S b18-1")], net.Asked);
        NavScanPicture picture = scan.View().Pictures[0];
        Assert.Equal((1, 1, 1), (picture.Names, picture.Recorded, picture.Unreadable));
        Assert.Equal(new NavScanLine("picture read   ·   1 name   ·   1 recorded   ·   1 name could not be read", RouteCheckLine.Plain), scan.View().Line);
        Assert.Contains("\"unreadable\":1", JsonSerializer.Serialize(picture, Settings.Json).Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void The_distance_is_the_one_on_the_names_own_row_of_the_tilted_panel()
    {
        // The panel tilts up to the right by 1 in 40, its rows 35 pixels apart: the distance column, 600 pixels right of the
        // names' ends, stands 15 pixels higher than the row's start, nearer the row above than a level look would think.
        const double slope = -1 / 40.0;
        string[] names = ["COL 285 SECTOR JR-S B18-1", "COL 285 SECTOR RI-J C9-18", "COL 285 SECTOR WO-J A38-0", "COL 285 SECTOR NX-Q B19-5"];
        double[] distances = [4.23, 5.29, 5.34, 5.34];
        var lines = new List<ReadLine>();
        for (int row = 0; row < names.Length; row++)
        {
            double y = 600 + 35 * row, x = 590;
            var words = new List<ReadWord>();
            foreach (string word in names[row].Split(' '))
            {
                words.Add(new ReadWord(word, x, y + slope * (x - 590), 14 * word.Length, 20));
                x += 14 * word.Length + 10;
            }
            lines.Add(new ReadLine(words));
            // The column, read as a line of its own, the reader's way: "S.34Ly" for 5.34.
            string shown = distances[row].ToString("0.00", CultureInfo.InvariantCulture).Replace("5.", "S.", StringComparison.Ordinal) + "Ly";
            lines.Add(new ReadLine([new ReadWord(shown, 1250, y + slope * (1250 + 40 - 590), 80, 20)]));
        }

        Assert.Equal(distances.Select(ly => (double?)ly), NavScanNames.Read(new PictureText(lines), Ngarawe).Names.Select(name => name.DistanceLy));
    }

    [Fact]
    public void Catalogue_names_are_read_with_their_digits_put_right_and_stations_and_words_are_passed_over()
    {
        PictureText text = Panel(("@ HIP 1159B", 12.0), ("—GLIESE 110", 9.98), ("O HIP 12766", null), ("BD+47 2112", null), ("G 139-21", null),
            ("G 5", null), ("a—GUESE 110", null), ("LOUNGE REACH", null), ("NAV BEACON", null), ("SET FILTERS LOCATION I FILTERS ACTIVE", null), ("HEATSINK 1/2", null));

        PictureNames names = NavScanNames.Read(text, Ngarawe);

        Assert.Empty(names.Names);
        Assert.Equal([new CatalogueRead("HIP 11598", 12.0), new CatalogueRead("GLIESE 110", 9.98), new CatalogueRead("HIP 12766", null),
            new CatalogueRead("BD+47 2112", null), new CatalogueRead("G 139-21", null)], names.Catalogue);
    }

    // ------------------------------------------------------------ pictures, read once

    [Fact]
    public async Task A_new_picture_is_read_once_its_names_checked_memory_first_then_Spansh_in_one_request_and_a_new_find_pings()
    {
        NavScan scan = Start();
        var found = new List<HeatmapFind>();
        finds!.Found += find =>
        {
            lock (found) found.Add(find);
        };
        var news = new List<NavScanNews>();
        scan.News += told =>
        {
            lock (news) news.Add(told);
        };
        // A system the game targeted earlier, which its check found recorded: in the 3D map's memory of checks, so Nav scan
        // does not ask about it again.
        string asked = "GET /api/systems/field_values/system_names?q=" + Uri.EscapeDataString("Col 285 Sector JR-S b18-1");
        net.Then(JsonSerializer.Serialize(new { min_max = new[] { new { id64 = Id("Col 285 Sector JR-S b18-1"), name = "Col 285 Sector JR-S b18-1", x = 165.5, y = -75.5, z = 45.5 } }, values = new[] { "Col 285 Sector JR-S b18-1" } }));
        Assert.Equal(HeatmapFinds.Recorded, (await finds.Check("Col 285 Sector JR-S b18-1", Id("Col 285 Sector JR-S b18-1")))!.State);
        net.Then(Named("Col 285 Sector WO-J a38-0"));

        string path = Picture("20261015120000_1.jpg", Panel(("COL 285 SECTOR JR-S BIB-I", 4.23), ("O COL 285 SECTOR wo-J A3B-O", 5.34), ("COL 285 SECTOR RI-J C9-1B", 5.29),
            ("NAV BEACON", null)));
        scan.Offer(path);
        await scan.Idle;

        // One request, for the two the memory did not know: one of the 3D map's own.
        Assert.Equal([asked, NamesAsked("Col 285 Sector WO-J a38-0", "Col 285 Sector RI-J c9-18")], net.Asked);
        Assert.Equal(2, survey!.Used);

        // The one Spansh has not: a possible find, placed at its cube's middle, until the game confirms it.
        HeatmapFind find = Assert.Single(finds.Finds);
        Assert.Equal(find, Assert.Single(found));
        Assert.Equal<(string, long, double?, double?, double?, double?, string, string?, bool?, string?)>(
            ("Col 285 Sector RI-J c9-18", 5031923225306, 155, -85, 35, 20, HeatmapFinds.FromNavScan, "read from a picture; target it in the game to confirm", false, null),
            (find.Name, find.Id64, find.X, find.Y, find.Z, find.PlusMinus, find.From, find.Note, find.Confirmed, find.Readings));
        Assert.Equal(["not found by Spansh's system search (" + DateTime.UtcNow.ToString("d MMM yyyy", CultureInfo.InvariantCulture) + ")"], find.How);

        // A click once read, then the ping: a system nobody has recorded.
        Assert.Equal([(NavScanSounds.Click, 70), (NavScanSounds.Ping, 70)], played);

        NavScanView view = scan.View();
        Assert.Equal((NavScan.Watching, true, 70, Pictures, (string?)null), (view.State, view.On, view.Volume, Assert.Single(view.Watching), view.Problem));
        NavScanPicture picture = Assert.Single(view.Pictures);
        Assert.Equal(("20261015120000_1.jpg", 3, 2, 1, 0, 0, 0, NavScan.Done), (picture.File, picture.Names, picture.Recorded, picture.NotRecorded, picture.Failed, picture.Unsure, picture.Dropped, picture.State));
        Assert.Equal(
        [
            ("Col 285 Sector JR-S b18-1", HeatmapFinds.Recorded, "checked earlier: found by Spansh's name search"),
            ("Col 285 Sector WO-J a38-0", HeatmapFinds.Recorded, "found by Spansh's system search"),
            ("Col 285 Sector RI-J c9-18", HeatmapFinds.NotRecorded, "not found by Spansh's system search"),
        ], view.Names.Select(name => (name.Name, name.State, name.How)));
        Assert.All(view.Names, name => Assert.Equal(("20261015120000_1.jpg", false), (name.From, name.Unsure)));
        Assert.Equal(new NavScanLine("picture read   ·   3 names   ·   2 recorded   ·   1 not recorded: Col 285 Sector RI-J c9-18", RouteCheckLine.Gold), view.Line);

        // The page heard it being read, checked and done, the last time with the name to say.
        Assert.Equal([NavScan.Reading, NavScan.Checking], news.Select(told => told.State).Distinct().Where(state => state != NavScan.Watching).Take(2));
        Assert.Equal(["Col 285 Sector RI-J c9-18"], news[^1].Found);
        Assert.Equal((NavScan.Done, view.Line), (news[^1].Picture!.State, news[^1].Line));

        // Windows says the file changed (Steam touches it again): read once only.
        scan.Offer(path);
        await scan.Idle;
        Assert.Equal(["20261015120000_1.jpg"], read);
        Assert.Equal(2, net.Asked.Count);

        // What was learnt is in the memory for the other checks, kept on disk: the game targeting WO-J asks nobody.
        Assert.Equal(CheckMemory.NotRecorded, finds.Memory.Get("Col 285 Sector RI-J c9-18", 5031923225306)!.Result);
        Assert.Contains("\"Col 285 Sector WO-J a38-0\"", File.ReadAllText(Path.Combine(Folder, CheckMemory.FileName)), StringComparison.Ordinal);
        Assert.Null(await finds.Check("Col 285 Sector WO-J a38-0", Id("Col 285 Sector WO-J a38-0")));
        Assert.Equal(2, net.Asked.Count);
    }

    [Fact]
    public async Task A_name_known_already_stays_quiet_and_volume_0_is_silent()
    {
        NavScan scan = Start();
        net.Then(Named());
        scan.Offer(Picture("first.jpg", Panel(("COL 285 SECTOR RI-J C9-18", 5.29))));
        await scan.Idle;
        Assert.Equal([(NavScanSounds.Click, 70), (NavScanSounds.Ping, 70)], played);

        // The same name on the next picture: not recorded, from the memory of checks. A click, no ping, nothing asked.
        scan.Offer(Picture("second.jpg", Panel(("COL 285 SECTOR RI-J C9-1B", 5.29))));
        await scan.Idle;
        Assert.Equal([(NavScanSounds.Click, 70), (NavScanSounds.Ping, 70), (NavScanSounds.Click, 70)], played);
        Assert.Single(net.Asked);
        Assert.Single(finds!.Finds);
        Assert.Equal((1, "checked earlier: not found by Spansh's system search"), (scan.View().Pictures[0].NotRecorded, scan.View().Names[0].How));

        // Your finds count as known too, after the memory has let the answer go (24 hours).
        clock = clock.AddHours(25);
        scan.Offer(Picture("third.jpg", Panel(("COL 285 SECTOR RI-J C9-18", 5.29))));
        await scan.Idle;
        Assert.Equal((NavScanSounds.Click, 70), played[^1]);
        Assert.Equal("in your finds", scan.View().Names[0].How);

        // At volume 0 Nav scan makes no sound at all, and says so when asked for one.
        scan.Volume = 0;
        net.Then(Named());
        scan.Offer(Picture("fourth.jpg", Panel(("COL 285 SECTOR RI-J C9-17", 5.55))));
        await scan.Idle;
        Assert.Equal(4, played.Count);
        Assert.Equal(2, finds.Finds.Count);
        Assert.False(scan.TestSound(NavScanSounds.Ping));
        scan.Volume = 35;
        Assert.True(scan.TestSound(NavScanSounds.Click));
        Assert.Equal((NavScanSounds.Click, 35), played[^1]);
        Assert.Throws<ArgumentException>(() => scan.Volume = 101);
        Assert.Throws<ArgumentException>(() => scan.TestSound("boom"));
        Assert.Equal(35, scan.Volume);
    }

    [Fact]
    public async Task A_picture_is_read_once_across_a_restart_and_forgotten_after_30_days()
    {
        NavScan first = Start();
        net.Then(Named("Col 285 Sector WO-J a38-0"));
        string path = Picture("20261015120000_1.jpg", Panel(("COL 285 SECTOR WO-J A38-0", 5.34)));
        first.Offer(path);
        await first.Idle;
        Assert.Single(read);
        Assert.Contains("20261015120000_1.jpg", File.ReadAllText(Path.Combine(Folder, NavScan.ReadFile)), StringComparison.Ordinal);
        first.Dispose();

        // Studio starts again with Nav scan on (it was saved so). Windows tells of the picture once more, as made just now
        // (a copy put back, say): it was read, by its path, size and last write, and is not read again.
        NavScan again = Start(on: null);
        Assert.True(again.On);
        // (Only Windows lets the time a file was made be set; elsewhere the picture is simply told of again.)
        if (OperatingSystem.IsWindows()) File.SetCreationTimeUtc(path, DateTime.UtcNow);
        again.Offer(path);
        await again.Idle;
        Assert.Single(read);

        // Written again, with more in it: a new picture.
        File.WriteAllBytes(path, new byte[9]);
        again.Offer(path);
        await again.Idle;
        Assert.Equal(2, read.Count);

        // A month on, a picture read is forgotten: the list holds the last 30 days only.
        clock = clock.AddDays(31);
        again.Offer(Picture("20261115120000_1.jpg", Panel()));
        await again.Idle;
        string kept = File.ReadAllText(Path.Combine(Folder, NavScan.ReadFile));
        Assert.DoesNotContain("20261015120000_1.jpg", kept, StringComparison.Ordinal);
        Assert.Contains("20261115120000_1.jpg", kept, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_pictures_made_after_it_is_switched_on_are_read_and_none_while_it_is_off()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows lets a test set the time a file was made (and Nav scan is Windows only).");
        // A picture of the morning, there before Nav scan was switched on: never read, though Windows tells of it.
        string before = Picture("morning.jpg", Panel(("COL 285 SECTOR WO-J A38-0", 5.34)));
        File.SetCreationTimeUtc(before, DateTime.UtcNow.AddHours(-3));
        NavScan scan = Start(on: false);
        NavScanView off = scan.View();
        Assert.Equal((NavScan.Off, false, 0, (NavScanLine?)null), (off.State, off.On, off.Watching.Count, off.Line));
        scan.Offer(Picture("while-off.jpg", Panel()));
        await scan.Idle;
        Assert.Empty(read);

        scan.On = true;
        scan.Offer(before);
        await scan.Idle;
        Assert.Empty(read);
        Assert.Equal(new NavScanLine("waiting for a screenshot of the Navigation panel (F12 in Steam, F10 in the game)", RouteCheckLine.Dim), scan.View().Line);

        // One made now is.
        scan.Offer(Picture("now.jpg", Panel()));
        await scan.Idle;
        Assert.Equal(["now.jpg"], read);
        Assert.Equal(new NavScanLine("picture read   ·   no system names found", RouteCheckLine.Plain), scan.View().Line);

        // Not a picture: passed over.
        scan.Offer(Path.Combine(Pictures, "notes.txt"));
        await scan.Idle;
        Assert.Single(read);

        // Kept in settings.json, beside the 3D map's other settings.
        JsonElement saved = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(SettingsFile)).GetProperty("heatmap");
        Assert.Equal((true, 70, "all"), (saved.GetProperty("navScan").GetBoolean(), saved.GetProperty("navScanVolume").GetInt32(), saved.GetProperty("routeCheck").GetString()));
    }

    [Fact]
    public async Task New_pictures_are_noticed_in_the_folders_watched_and_in_a_folder_that_appears_later()
    {
        // Steam's folder is there; the game's own (F10) is not yet: it comes with the first picture taken that way.
        string game10 = Path.Combine(temp, "Frontier Developments", "Elite Dangerous");
        NavScan scan = Start(on: false, places: () => [Pictures, game10], lookEvery: TimeSpan.FromMilliseconds(50));
        scan.On = true;
        Assert.Equal([Pictures], scan.View().Watching);

        Picture("20261015120000_1.jpg", Panel());
        await Until(() => read.Count == 1);

        Directory.CreateDirectory(game10);
        texts["Screenshot_0000.bmp"] = Panel();
        File.WriteAllBytes(Path.Combine(game10, "Screenshot_0000.bmp"), new byte[5]);
        await Until(() => read.Count == 2 && scan.View().Watching.Count == 2);
        Assert.Equal(["20261015120000_1.jpg", "Screenshot_0000.bmp"], read);

        // Switched off: nothing is watched, and a new picture is not read.
        scan.On = false;
        Picture("20261015120500_1.jpg", Panel());
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await scan.Idle;
        Assert.Equal(2, read.Count);
        Assert.Empty(scan.View().Watching);
    }

    [Fact]
    public async Task Names_go_to_Spansh_100_to_a_request()
    {
        NavScan scan = Start();
        // A galaxy map picture of 230 systems of one cube (no distances on it).
        string[] names = [.. Enumerable.Range(0, 230).Select(i => $"Col 285 Sector RI-J c9-{i}")];
        net.Then(Named(names[..100])).Then(Named(names[100..200])).Then(Named(names[200..]));

        scan.Offer(Picture("galaxy-map.jpg", Panel([.. Enumerable.Range(0, 230).Select(i => ($"COL 285 SECTOR RI-J C9-{i}", (double?)null))])));
        await scan.Idle;

        Assert.Equal([NamesAsked(names[..100]), NamesAsked(names[100..200]), NamesAsked(names[200..])], net.Asked);
        Assert.Equal(3, survey!.Used);
        Assert.Equal((230, 230, 0), (scan.View().Pictures[0].Names, scan.View().Pictures[0].Recorded, scan.View().Pictures[0].NotRecorded));
        Assert.Equal(new NavScanLine("picture read   ·   230 names   ·   all recorded", RouteCheckLine.Green), scan.View().Line);
        Assert.Equal([(NavScanSounds.Click, 70)], played);
    }

    [Fact]
    public async Task An_unclear_reading_is_settled_by_its_spelling_that_is_recorded()
    {
        NavScan scan = Start();
        net.Then(Named("Col 285 Sector WO-J a38-0"));

        scan.Offer(Picture("galaxy-map.jpg", Panel(("COL 285 SECTOR WO-J A3B-O", null))));
        await scan.Idle;

        // All three spellings in the one request; the one Spansh has was the name, and the others were misreads.
        Assert.Equal([NamesAsked("Col 285 Sector WO-J a38-0", "Col 285 Sector WQ-J a38-0", "Col 285 Sector WG-J a38-0")], net.Asked);
        NavScanName name = Assert.Single(scan.View().Names);
        Assert.Equal(("Col 285 Sector WO-J a38-0", HeatmapFinds.Recorded, false), (name.Name, name.State, name.Unsure));
        Assert.Equal((1, 1, 0), (scan.View().Pictures[0].Names, scan.View().Pictures[0].Recorded, scan.View().Pictures[0].Unsure));
        Assert.Empty(finds!.Finds);
        Assert.Equal([(NavScanSounds.Click, 70)], played);

        // Read again as unclearly: the memory has each spelling's answer now, so nobody is asked, and the recorded one is the name.
        scan.Offer(Picture("galaxy-map-2.jpg", Panel(("COL 285 SECTOR WO-J A3B-O", null))));
        await scan.Idle;
        Assert.Single(net.Asked);
        Assert.Equal(("Col 285 Sector WO-J a38-0", false), (scan.View().Names[0].Name, scan.View().Names[0].Unsure));
    }

    [Fact]
    public async Task An_unclear_reading_nobody_has_is_kept_whole_until_the_game_names_one_spelling()
    {
        NavScan scan = Start();
        var confirmed = new List<HeatmapFind>();
        finds!.Confirmed += find =>
        {
            lock (confirmed) confirmed.Add(find);
        };
        net.Then(Named());

        scan.Offer(Picture("galaxy-map.jpg", Panel(("COL 285 SECTOR WO-J A3B-O", null))));
        await scan.Idle;

        // None recorded: each spelling a possible find, said to be one of three; the ping, for the one name.
        string[] spellings = ["Col 285 Sector WO-J a38-0", "Col 285 Sector WQ-J a38-0", "Col 285 Sector WG-J a38-0"];
        Assert.Equal(spellings.Order(), finds.Finds.Select(find => find.Name).Order());
        Assert.All(finds.Finds, find => Assert.Equal(("read from a picture, one of 3 ways the name may be spelt; target it in the game to confirm", false, string.Join(" or ", spellings)),
            (find.Note, find.Confirmed, find.Readings)));
        Assert.Equal([(NavScanSounds.Click, 70), (NavScanSounds.Ping, 70)], played);
        NavScanView view = scan.View();
        Assert.Equal(spellings, view.Names.Select(name => name.Name));
        Assert.All(view.Names, name => Assert.True(name.Unsure));
        Assert.Equal((1, 1, 1), (view.Pictures[0].Names, view.Pictures[0].NotRecorded, view.Pictures[0].Unsure));
        Assert.Equal("picture read   ·   1 name   ·   1 not recorded: Col 285 Sector WO-J a38-0 (unsure)", view.Line!.Words);

        // The player targets WQ-J in the galaxy map, and the game names it with its number: it exists. Its find is
        // confirmed, the other spellings go, and nothing is asked (the memory has its answer from a moment ago).
        long wq = Id("Col 285 Sector WQ-J a38-0");
        Line(string.Create(CultureInfo.InvariantCulture, $$"""{ "event":"FSDTarget", "Name":"Col 285 Sector WQ-J a38-0", "SystemAddress":{{wq}}, "StarClass":"M" }"""));
        await finds.Idle;
        HeatmapFind kept = Assert.Single(finds.Finds);
        Assert.Equal<(string, bool?, string?)>(("Col 285 Sector WQ-J a38-0", true, "read from a picture; confirmed: the game named it when you targeted it, plotted a route through it or arrived there"),
            (kept.Name, kept.Confirmed, kept.Note));
        Assert.Equal(kept, Assert.Single(confirmed));
        Assert.Single(net.Asked);
        Assert.Contains("\"confirmed\": true", File.ReadAllText(Path.Combine(Folder, "finds.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_catalogue_name_is_checked_only_when_the_detailed_map_has_it_near_the_commander()
    {
        // The detailed map of the block round Wredguia (see FineMapTests): it has HIP 2001 at (-1345, 100, 0).
        Directory.CreateDirectory(Folder);
        string list = Path.Combine(Folder, FineMap.DownloadName);
        using (var file = File.Create(list))
        using (var packed = new GZipStream(file, CompressionLevel.Optimal))
            packed.Write(new UTF8Encoding(false).GetBytes(SpanshStandIn.Recorded("systems-small.json")));
        File.SetLastWriteTimeUtc(list, new DateTime(2026, 10, 14, 11, 53, 17, DateTimeKind.Utc));
        NavScan scan = Start(at: (-1340, 100, 0));
        Assert.Null((await fine!.Build(38, 18)).Problem);

        scan.Offer(Picture("panel.jpg", Panel(("@ HIP 2OO1", 5.00), ("HIP 9999", 7.5), ("GLIESE 110", null))));
        await scan.Idle;

        // HIP 2001 as the detailed map spells and places it: recorded, from this PC. The two it does not have near the
        // commander are dropped, and counted. Nobody is asked anything.
        Assert.Empty(net.Asked);
        NavScanName name = Assert.Single(scan.View().Names);
        Assert.Equal<(string, long, double?, double?, double?, double?, string, string?)>(("HIP 2001", 5223218694992, -1345, 100, 0, null, HeatmapFinds.Recorded, "in the detailed map (Spansh's list of 14 Oct 2026)"),
            (name.Name, name.Id64, name.X, name.Y, name.Z, name.PlusMinus, name.State, name.How));
        Assert.Equal((1, 1, 2), (scan.View().Pictures[0].Names, scan.View().Pictures[0].Recorded, scan.View().Pictures[0].Dropped));
    }

    /// <summary>Spansh's answer to a search round a point, in its layout: these systems, all it knows there.</summary>
    static string Around(params (string Name, double X, double Y, double Z)[] systems) => JsonSerializer.Serialize(new
    {
        count = systems.Length,
        from = 0,
        results = systems.Select(system => new Dictionary<string, object> { ["bodies"] = Array.Empty<object>(), ["id64"] = Id(system.Name), ["name"] = system.Name, ["x"] = system.X, ["y"] = system.Y, ["z"] = system.Z }),
        size = 500,
    });

    [Fact]
    public async Task Names_Within_reach_has_just_listed_are_settled_from_its_answer_with_no_request_of_their_own()
    {
        NavScan scan = Start();
        // Within reach on the same 3D map: after a jump outside the detailed map it asks Spansh, once, for the systems
        // within reach, three of Col 285 Sector among them.
        var reach = new HeatmapReach(game, survey!, fine!, finds!.Memory) { RangeWait = TimeSpan.Zero };
        Line("""{ "event":"Loadout", "Ship":"krait_mkii", "ShipID":1, "MaxJumpRange":37.5, "CargoCapacity":0, "Modules":[] }""");
        await reach.Pending;
        Assert.Empty(net.Asked);
        net.Then(Around(("Col 285 Sector JR-S b18-1", 162.5, -72.5, 49.5), ("Col 285 Sector RI-J c9-18", 156.5, -78.25, 50.75), ("Col 285 Sector WO-J a38-0", 161.25, -71.5, 57.125)));
        Line(string.Create(CultureInfo.InvariantCulture, $$"""{ "event":"FSDJump", "StarSystem":"Ngarawe", "SystemAddress":1001, "StarPos":[{{Ngarawe.X}},{{Ngarawe.Y}},{{Ngarawe.Z}}] }"""));
        await reach.Pending;
        Assert.Equal((1, 1, 3), (net.Asked.Count, survey!.Used, reach.Now().Systems.Count));

        // F12 on the Navigation panel just after: both names were in that answer, so nothing more is asked.
        scan.Offer(Picture("panel.jpg", Panel(("O COL 285 SECTOR wo-J A3B-O", 5.34), ("COL 285 SECTOR RI-J C9-1B", 5.29))));
        await scan.Idle;
        Assert.Single(net.Asked);
        Assert.Equal(
        [
            ("Col 285 Sector WO-J a38-0", HeatmapFinds.Recorded, "found by Spansh's system search (within reach)"),
            ("Col 285 Sector RI-J c9-18", HeatmapFinds.Recorded, "found by Spansh's system search (within reach)"),
        ], scan.View().Names.Select(name => (name.Name, name.State, name.How)));
        Assert.Equal([(NavScanSounds.Click, 70)], played);

        // The game targets the third: settled from the same answer, with no request either.
        Assert.Equal(HeatmapFinds.Recorded, (await finds.Check("Col 285 Sector JR-S b18-1", Id("Col 285 Sector JR-S b18-1")))!.State);
        Assert.Equal((1, 1), (net.Asked.Count, survey.Used));
    }

    [Fact]
    public async Task Names_waiting_their_turn_behind_Within_reachs_request_are_settled_from_its_answer_when_it_comes()
    {
        var held = new Held(net);
        NavScan scan = Start(handler: held);
        var searched = new List<string>();
        finds!.Searching = name =>
        {
            lock (searched) searched.Add(name);
        };
        var reach = new HeatmapReach(game, survey!, fine!, finds.Memory) { RangeWait = TimeSpan.Zero };
        Line("""{ "event":"Loadout", "Ship":"krait_mkii", "ShipID":1, "MaxJumpRange":37.5, "CargoCapacity":0, "Modules":[] }""");
        await reach.Pending;
        // A jump outside the detailed map: Within reach asks Spansh for the systems within reach, and its request is on its
        // way (held here: Spansh's search round a point takes some seconds), holding the 3D map's gate.
        net.Then(Around(("Col 285 Sector JR-S b18-1", 162.5, -72.5, 49.5), ("Col 285 Sector RI-J c9-18", 156.5, -78.25, 50.75), ("Col 285 Sector WO-J a38-0", 161.25, -71.5, 57.125)));
        Line(string.Create(CultureInfo.InvariantCulture, $$"""{ "event":"FSDJump", "StarSystem":"Ngarawe", "SystemAddress":1001, "StarPos":[{{Ngarawe.X}},{{Ngarawe.Y}},{{Ngarawe.Z}}] }"""));
        await held.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // F12 on the Navigation panel meanwhile: no answer has its names yet, so its check waits its turn behind that request.
        scan.Offer(Picture("panel.jpg", Panel(("O COL 285 SECTOR wo-J A3B-O", 5.34), ("COL 285 SECTOR RI-J C9-1B", 5.29))));
        await Until(() => scan.View().Pictures is [{ Names: 2, Waiting: true }]);
        // The game targets the third meanwhile: its check goes to the gate too, after what is kept on this PC.
        long jr = Id("Col 285 Sector JR-S b18-1");
        Task<TargetNews?> target = finds.Check("Col 285 Sector JR-S b18-1", jr);
        await Until(() =>
        {
            lock (searched) return searched.Contains("Col 285 Sector JR-S b18-1");
        });
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Within reach's answer comes, listing all three: the picture's check and the target's take them from it once
        // their turn comes, and send nothing. One request in all.
        held.LetGo.SetResult();
        await reach.Pending;
        await scan.Idle;
        Assert.Equal(HeatmapFinds.Recorded, (await target)!.State);
        Assert.Equal((1, 1), (net.Asked.Count, survey!.Used));
        Assert.Equal(
        [
            ("Col 285 Sector WO-J a38-0", HeatmapFinds.Recorded, "found by Spansh's system search (within reach)"),
            ("Col 285 Sector RI-J c9-18", HeatmapFinds.Recorded, "found by Spansh's system search (within reach)"),
        ], scan.View().Names.Select(name => (name.Name, name.State, name.How)));
        Assert.Equal("found by Spansh's system search (within reach)", finds.Memory.Get("Col 285 Sector JR-S b18-1", jr)!.How);
        Assert.Equal(new NavScanLine("picture read   ·   2 names   ·   all recorded", RouteCheckLine.Green), scan.View().Line);
        lock (played) Assert.Equal([(NavScanSounds.Click, 70)], played);
    }

    [Fact]
    public async Task A_find_is_confirmed_when_the_commander_arrives_there_and_the_system_they_are_in_is_confirmed_at_once()
    {
        NavScan scan = Start();
        var confirmed = new List<HeatmapFind>();
        finds!.Confirmed += find =>
        {
            lock (confirmed) confirmed.Add(find);
        };
        net.Then(Named());
        scan.Offer(Picture("first.jpg", Panel(("COL 285 SECTOR RI-J C9-18", 5.29))));
        await scan.Idle;
        Assert.Equal(false, Assert.Single(finds.Finds).Confirmed);

        // The player flies there (along a route the game picked, say: no target line of the player's): the game names the
        // system with its number as the commander arrives. The find is confirmed, and the page is told (as "heatmap-find").
        long c918 = Id("Col 285 Sector RI-J c9-18");
        Line(string.Create(CultureInfo.InvariantCulture, $$"""{ "event":"FSDJump", "StarSystem":"Col 285 Sector RI-J c9-18", "SystemAddress":{{c918}}, "StarPos":[150.5,-80.25,40.5] }"""));
        await finds.Idle;
        HeatmapFind arrived;
        lock (confirmed) arrived = Assert.Single(confirmed);
        Assert.Equal<(string, bool?, string?)>(("Col 285 Sector RI-J c9-18", true, "read from a picture; confirmed: the game named it when you targeted it, plotted a route through it or arrived there"),
            (arrived.Name, arrived.Confirmed, arrived.Note));
        Assert.Equal(true, Assert.Single(finds.Finds).Confirmed);

        // A picture taken in a system nobody has recorded, its name in the panel's header: the system the commander is in
        // exists (the game named it on arrival), so its find is confirmed at once, and there is no ping: the player is there.
        long c917 = Id("Col 285 Sector RI-J c9-17");
        Line(string.Create(CultureInfo.InvariantCulture, $$"""{ "event":"FSDJump", "StarSystem":"Col 285 Sector RI-J c9-17", "SystemAddress":{{c917}}, "StarPos":[151.5,-82.25,38.5] }"""));
        await finds.Idle;
        net.Then(Named());
        scan.Offer(Picture("second.jpg", Panel(("COL 285 SECTOR", null), ("RI-J C9-17 SYSTEM", null))));
        await scan.Idle;
        HeatmapFind here = finds.Finds.Single(find => find.Id64 == c917);
        Assert.Equal<(bool?, string?, string?)>((true, "read from a picture; confirmed: the game named it when you targeted it, plotted a route through it or arrived there", null),
            (here.Confirmed, here.Note, here.Readings));
        Assert.Equal((1, "picture read   ·   1 name   ·   1 not recorded: Col 285 Sector RI-J c9-17"), (scan.View().Pictures[0].NotRecorded, scan.View().Line!.Words));
        Assert.Equal([(NavScanSounds.Click, 70), (NavScanSounds.Ping, 70), (NavScanSounds.Click, 70)], played);
    }

    /// <summary>Holds back every request to Spansh until it is let go; from then on everything passes straight on.</summary>
    sealed class Held(System.Net.Http.HttpMessageHandler inner) : System.Net.Http.DelegatingHandler(inner)
    {
        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LetGo { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancel)
        {
            Holding.TrySetResult();
            await LetGo.Task.WaitAsync(cancel);
            return await base.SendAsync(request, cancel);
        }
    }

    [Fact]
    public async Task A_check_waiting_its_turn_at_Spansh_does_not_stop_the_next_picture_being_read()
    {
        var held = new Held(net);
        NavScan scan = Start(handler: held);
        // A count of the 3D map's under way (a survey's, say), held on its way to Spansh: the map's gate is taken.
        Task<CountAnswer> counting = survey!.Count("cube", -167.09, 26.72, -55.38, go: true);
        await held.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // F12: the picture is read at once and clicks; its check waits its turn at Spansh, and the overlay says so.
        scan.Offer(Picture("first.jpg", Panel(("COL 285 SECTOR RI-J C9-18", 5.29))));
        await Until(() => scan.View().Pictures is [{ Names: 1, Waiting: true }]);
        Assert.Equal(new NavScanLine("picture read   ·   waiting its turn at Spansh…", RouteCheckLine.Dim), scan.View().Line);
        lock (played) Assert.Equal([(NavScanSounds.Click, 70)], played);

        // F12 again while the first still waits: read too, with its click; its check waits behind the first's.
        scan.Offer(Picture("second.jpg", Panel(("COL 285 SECTOR RI-J C9-17", 5.55))));
        await scan.ReadIdle;
        Assert.Equal(["first.jpg", "second.jpg"], read);
        lock (played) Assert.Equal([(NavScanSounds.Click, 70), (NavScanSounds.Click, 70)], played);
        Assert.Equal((NavScan.Checking, true, "picture read   ·   waiting its turn at Spansh…"), (scan.View().Pictures[0].State, scan.View().Pictures[0].Waiting, scan.View().Line!.Words));
        Assert.Empty(net.Asked);   // the count itself is still held on its way

        // The count's turn ends: the two checks go, in the order the pictures were taken.
        net.ThenOffline().Then(Named()).Then(Named());
        held.LetGo.SetResult();
        await counting;
        await scan.Idle;
        Assert.Equal([NamesAsked("Col 285 Sector RI-J c9-18"), NamesAsked("Col 285 Sector RI-J c9-17")], net.Asked.Skip(1));
        Assert.All(scan.View().Pictures, picture => Assert.Equal((NavScan.Done, false), (picture.State, picture.Waiting)));
        lock (played) Assert.Equal([(NavScanSounds.Click, 70), (NavScanSounds.Click, 70), (NavScanSounds.Ping, 70), (NavScanSounds.Ping, 70)], played);
    }

    // ------------------------------------------------------------ the overlay's line, and the sounds

    [Fact]
    public void The_overlay_line_says_how_Nav_scan_stands()
    {
        static NavScanPicture Shot(string state, int names = 0, int recorded = 0, int notRecorded = 0, int failed = 0) =>
            new("p.jpg", DateTime.UtcNow, names, recorded, notRecorded, failed, 0, 0, state) { Problem = state == NavScan.Failed ? "Studio could not open this picture: it may not be a picture, or it is damaged." : null };

        Assert.Null(NavScan.LineOf(false, 1, Shot(NavScan.Done, 3, 3), []));
        Assert.Equal(new NavScanLine("no screenshot folder found yet: take a screenshot (F12 in Steam, F10 in the game)", RouteCheckLine.Plain), NavScan.LineOf(true, 0, null, []));
        Assert.Equal(new NavScanLine("reading…", RouteCheckLine.Dim), NavScan.LineOf(true, 1, Shot(NavScan.Reading), []));
        Assert.Equal(new NavScanLine("picture read   ·   18 names   ·   checking…", RouteCheckLine.Dim), NavScan.LineOf(true, 1, Shot(NavScan.Checking, 18), []));
        Assert.Equal(new NavScanLine("picture read   ·   18 names   ·   17 recorded   ·   1 not recorded: Col 285 Sector RI-J c9-18", RouteCheckLine.Gold),
            NavScan.LineOf(true, 1, Shot(NavScan.Done, 18, 17, 1), ["Col 285 Sector RI-J c9-18"]));
        Assert.Equal(new NavScanLine("picture read   ·   18 names   ·   all recorded", RouteCheckLine.Green), NavScan.LineOf(true, 1, Shot(NavScan.Done, 18, 18), []));
        Assert.Equal(new NavScanLine("picture read   ·   18 names   ·   13 recorded   ·   4 not recorded: A, B, C and 1 more   ·   1 not checked", RouteCheckLine.Gold),
            NavScan.LineOf(true, 1, Shot(NavScan.Done, 18, 13, 4, 1), ["A", "B", "C", "D"]));
        Assert.Equal(new NavScanLine("picture read   ·   18 names   ·   16 recorded   ·   2 not checked", RouteCheckLine.Plain), NavScan.LineOf(true, 1, Shot(NavScan.Done, 18, 16, 0, 2), []));
        // A name the reader could not make out was not checked: said, alone or after the rest.
        Assert.Equal(new NavScanLine("picture read   ·   2 names could not be read", RouteCheckLine.Plain), NavScan.LineOf(true, 1, Shot(NavScan.Done) with { Unreadable = 2 }, []));
        Assert.Equal(new NavScanLine("picture read   ·   18 names   ·   18 recorded   ·   1 name could not be read", RouteCheckLine.Plain),
            NavScan.LineOf(true, 1, Shot(NavScan.Done, 18, 18) with { Unreadable = 1 }, []));
        Assert.Equal(new NavScanLine("picture read   ·   18 names   ·   17 recorded   ·   1 not recorded: A   ·   1 name could not be read", RouteCheckLine.Gold),
            NavScan.LineOf(true, 1, Shot(NavScan.Done, 18, 17, 1) with { Unreadable = 1 }, ["A"]));
        Assert.Equal(new NavScanLine("picture not read: Studio could not open this picture: it may not be a picture, or it is damaged.", RouteCheckLine.Plain),
            NavScan.LineOf(true, 1, Shot(NavScan.Failed), []));
    }

    [Fact]
    public void The_ping_is_one_deep_note_with_two_fainter_echoes_unlike_the_snifferss_chime_and_the_click_is_a_tick()
    {
        const int rate = 44100;
        short[] ping = NavScanSounds.PingSamples(1);
        // Well over a second: the note rings out, and is heard twice again.
        Assert.InRange(ping.Length / (double)rate, 1.8, 2.5);

        // One deep note: about 330 crossings of the middle a second, against the chime's first note of 880 Hz.
        int Crossings(short[] wave, double from, double seconds)
        {
            int count = 0;
            for (int at = (int)(from * rate) + 1; at < (int)((from + seconds) * rate); at++)
                if ((wave[at - 1] < 0) != (wave[at] < 0)) count++;
            return count;
        }
        Assert.InRange(Crossings(ping, 0.05, 0.2) / 0.2 / 2, 300, 360);
        using MemoryStream chimeWave = Market.Chime.Wave(1);
        short[] chime = Samples(chimeWave);
        Assert.InRange(Crossings(chime, 0.05, 0.1) / 0.1 / 2, 800, 960);

        // Two echoes, each fainter: the loudest moment just after 0.45 s and 0.9 s rises above what came just before.
        double Loudest(short[] wave, double from, double seconds) => wave.Skip((int)(from * rate)).Take((int)(seconds * rate)).Max(sample => Math.Abs((int)sample));
        double first = Loudest(ping, 0, 0.05), echo = Loudest(ping, 0.46, 0.05), second = Loudest(ping, 0.91, 0.05);
        Assert.True(echo > Loudest(ping, 0.40, 0.04) && second > Loudest(ping, 0.85, 0.04), "each echo is a new sound");
        Assert.True(first > echo && echo > second * 1.5, $"{first}, {echo}, {second}");

        // The click: a few hundredths of a second, quieter than the ping.
        using MemoryStream clickWave = NavScanSounds.Wave(NavScanSounds.Click, 1);
        short[] click = Samples(clickWave);
        Assert.InRange(click.Length / (double)rate, 0.01, 0.06);
        Assert.True(click.Max(sample => Math.Abs((int)sample)) < first / 2);

        // Both are proper WAV files, made in memory.
        using MemoryStream pingWave = NavScanSounds.Wave(NavScanSounds.Ping, 0.7);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(pingWave.ToArray(), 0, 4));
        Assert.Throws<ArgumentException>(() => NavScanSounds.Wave("boom", 1));

        static short[] Samples(MemoryStream wav)
        {
            byte[] bytes = wav.ToArray();
            short[] samples = new short[(bytes.Length - 44) / 2];
            Buffer.BlockCopy(bytes, 44, samples, 0, samples.Length * 2);
            return samples;
        }
    }
}
