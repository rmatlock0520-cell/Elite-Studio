using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EliteStudio.App;
using EliteStudio.UI;

namespace EliteStudio.Tests;

/// <summary>
/// Studio UI (HUD colour themes) on practice folders. The first test replays a conversation that was
/// recorded with the old Elite HUD Studio 1.6 helper (its exe, run with --helper-only on the same
/// practice folders): Fixtures\ui\conversation.json holds the requests, Fixtures\ui\old-helper what
/// that helper answered and the files it wrote, with folder names and times replaced by &lt;GAME&gt;,
/// &lt;ROOT&gt;, &lt;TIME&gt; and so on. That recording is the reference for the port and cannot be made
/// again once the old tool is gone, so change it only on purpose.
/// Two things in it were changed by hand, on purpose. The old helper listed the PC's real installs
/// under "games" even on a practice folder; Studio does not, so the recording has an empty list
/// there. And the theme "whose name needs care" had the PC's account name in it, which was swapped
/// for "Jameson" (the helper only echoes the name back).
/// The other tests (here and in UiGameTests.cs) cover what the recording cannot: the game running,
/// several installs, links, long paths, the first run. Nothing here presses a key, opens a window
/// or looks for the player's real game.
/// </summary>
public sealed partial class UiTests : IDisposable
{
    static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ui");
    static readonly Regex Time = new(@"\d{4}-\d\d-\d\d[T_]\d\d[-:]\d\d[-:]\d\d");

    readonly string temp = Directory.CreateTempSubdirectory("studio-ui-").FullName;
    readonly List<string> links = [];

    public void Dispose()
    {
        foreach (string link in links) Directory.Delete(link);   // a link has to go before the tree it sits in
        Directory.Delete(temp, recursive: true);
    }

    // ------------------------------------------------------------ practice folders

    /// <summary>One Studio UI on its own practice folders, with the desktop replaced by a notebook.</summary>
    sealed class Rig(string root)
    {
        public readonly string Data = Path.Combine(root, "data");
        public readonly string Game = Path.Combine(root, "game", "Products", "elite-dangerous-odyssey-64");
        public readonly string Xml = Path.Combine(root, "Options", "Graphics", "GraphicsConfigurationOverride.xml");
        public string Backups => Path.Combine(Data, "ui", "Backups");
        public string Themes => Path.Combine(Data, "ui", "My Themes");
        public string IniDir => Path.Combine(Game, "EDHM-ini");
        public string SettingsFile => Path.Combine(Data, "settings.json");
        /// <summary>The player's home folder, where an exported theme goes (its Downloads folder).</summary>
        public readonly string Home = Path.Combine(root, "home");

        public WebServer Web = new(Path.Combine(root, "web"));
        public bool Running;
        public Reload KeyResult = new(true, true, null);
        public int KeyPresses;
        public List<GameFolder> Found = [];
        public Exception? SearchFails;
        /// <summary>What the HUD mod's page hands over; a test that installs the mod says what.</summary>
        public Func<(string Version, byte[] Zip)> ModDownload = () => throw new InvalidOperationException("The HUD mod could not be downloaded (No such host is known.).");
        public readonly List<string> Shown = [], Recycled = [];

        /// <summary>Starts Studio UI the way a fresh launch of the app would. forcedGame null means "find the game".</summary>
        public Rig Start(string? forcedGame, params string[] oldThemeDirs)
        {
            Directory.CreateDirectory(Data);
            Web = new WebServer(Path.Combine(root, "web"));
            new StudioUi(new Settings(SettingsFile), Data, forcedGame, Xml, oldThemeDirs)
            {
                FindGames = () => SearchFails is null ? Found : throw SearchFails,
                GameRunning = () => Running,
                // like the real thing: nothing to press while Elite is closed
                PressReloadKey = () => { if (!Running) return new Reload(false, false, null); KeyPresses++; return KeyResult; },
                ShowFolder = Shown.Add,
                Recycle = file => { Recycled.Add(file); File.Move(file, file + ".recycled"); },
                DownloadMod = () => ModDownload(),
                Home = Home,
            }.Map(Web);
            return this;
        }

        public async Task<(int Status, JsonElement Json)> Call(string method, string route, string body = "")
        {
            string[] parts = route.Split('?', 2);
            WebResponse response = await Web.Handle(new WebRequest(method, "/api/ui" + parts[0], WebServer.ParseQuery(parts.Length == 2 ? parts[1] : ""), body));
            return (response.Status, JsonSerializer.Deserialize<JsonElement>(response.Body));
        }

        /// <summary>A call that must succeed.</summary>
        public async Task<JsonElement> Ok(string method, string route, string body = "")
        {
            var (status, json) = await Call(method, route, body);
            Assert.True(status == 200, $"{method} {route} answered {status}: {json}");
            return json;
        }

        /// <summary>A call that must be refused; returns the reason given.</summary>
        public async Task<string> Refused(string method, string route, string body = "")
        {
            var (status, json) = await Call(method, route, body);
            Assert.Equal(500, status);
            Assert.False(json.GetProperty("ok").GetBoolean());
            return json.GetProperty("error").GetString()!;
        }

        /// <summary>Replaces this run's folders and times, so answers from different runs (and from the old helper) can be compared.</summary>
        public string Plain(string text)
        {
            foreach (var (folder, name) in new[] { (Backups, "<BACKUPS>"), (Themes, "<THEMES>"), (Data, "<ROOT>"), (Game, "<GAME>"), (Xml, "<XML>") })
                text = text.Replace(folder, name, StringComparison.OrdinalIgnoreCase);
            return Time.Replace(text, "<TIME>");
        }
    }

    Rig NewRig(string name = "pc") => new(Path.Combine(temp, name));

    /// <summary>A rig with the fixture game folder, the mod in it, and Studio told to use exactly that folder.</summary>
    Rig Practice()
    {
        Rig rig = NewRig();
        StageGame(rig.Game);
        return rig.Start(rig.Game);
    }

    /// <summary>The fixture game folder plus stand-ins for the two big files Studio only checks for.</summary>
    static void StageGame(string game, bool withMod = true)
    {
        Directory.CreateDirectory(game);
        File.WriteAllBytes(Path.Combine(game, "EliteDangerous64.exe"), []);
        if (!withMod) return;
        CopyTree(Path.Combine(Fixtures, "game"), game);
        File.WriteAllText(Path.Combine(game, "d3d11.dll"), "stand-in for the mod's loader");
    }

    /// <summary>Backups and themes that were "already there": written by older versions, interrupted, or not JSON at all.</summary>
    static void StageData(string backups, string themes)
    {
        CopyTree(Path.Combine(Fixtures, "data", "Backups"), backups);
        CopyTree(Path.Combine(Fixtures, "data", "My Themes"), themes);
        // The theme list is ordered by file time, which Git does not keep: give each file a known one.
        string[] files = [.. Directory.GetFiles(themes).Order(StringComparer.Ordinal)];
        for (int i = 0; i < files.Length; i++) File.SetLastWriteTime(files[i], new DateTime(2026, 9, 1).AddDays(i));
    }

    /// <summary>Elite's own colour file as the game ships it.</summary>
    static void StageXml(string file, string red = " 1, 0, 0 ", string green = " 0, 1, 0 ", string blue = " 0, 0, 1 ")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <GraphicsConfig>
              <GUIColour>
                <Default>
                  <MatrixRed>{red}</MatrixRed>
                  <MatrixGreen>{green}</MatrixGreen>
                  <MatrixBlue>{blue}</MatrixBlue>
                </Default>
              </GUIColour>
            </GraphicsConfig>
            """, new UTF8Encoding(true));
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    /// <summary>Every value in a JSON answer as "path = value" lines, so two answers can be compared field by field whatever their layout.</summary>
    static SortedDictionary<string, string> Flat(JsonElement json, Func<string, string> plain)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        Walk(json, "");
        return fields;

        void Walk(JsonElement value, string path)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    if (!value.EnumerateObject().Any()) fields[path] = "{}";
                    foreach (JsonProperty field in value.EnumerateObject()) Walk(field.Value, $"{path}/{field.Name}");
                    break;
                case JsonValueKind.Array:
                    if (value.GetArrayLength() == 0) fields[path] = "[]";
                    int index = 0;
                    foreach (JsonElement item in value.EnumerateArray()) Walk(item, $"{path}[{index++}]");
                    break;
                case JsonValueKind.String: fields[path] = '"' + plain(value.GetString()!) + '"'; break;
                case JsonValueKind.Number: fields[path] = value.GetDouble().ToString("R", CultureInfo.InvariantCulture); break;
                default: fields[path] = value.GetRawText(); break;
            }
        }
    }

    static string Timeless(string file) => Time.Replace(File.ReadAllText(file), "<TIME>");

    /// <summary>The "ui" section as it is in settings.json on disk right now.</summary>
    static JsonElement SavedUi(Rig rig) => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(rig.SettingsFile)).GetProperty("ui");

    // ------------------------------------------------------------ against the old helper's recording

    [Fact]
    public async Task Answers_and_files_match_what_the_old_helper_recorded()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The old helper's recording was made on Windows: its answers hold Windows paths.");
        Rig rig = NewRig();
        StageGame(rig.Game);
        StageData(rig.Backups, rig.Themes);
        StageXml(rig.Xml);
        rig.Start(rig.Game);
        string[] gameFilesBefore = [.. Directory.GetFiles(rig.Game, "*", SearchOption.AllDirectories).Order()];

        JsonElement steps = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(Fixtures, "conversation.json")));
        JsonElement recorded = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(Fixtures, "old-helper", "replies.json")));
        Assert.Equal(steps.GetArrayLength(), recorded.GetArrayLength());

        var differences = new List<string>();
        string newest = "";
        for (int i = 0; i < steps.GetArrayLength(); i++)
        {
            JsonElement step = steps[i], old = recorded[i];
            string label = step.GetProperty("label").GetString()!;
            Assert.Equal(label, old.GetProperty("label").GetString());
            string route = step.GetProperty("route").GetString()!.Replace("{newest}", Uri.EscapeDataString(newest));
            string body = step.TryGetProperty("body", out JsonElement given) ? given.GetRawText().Replace("{game}", rig.Game.Replace(@"\", @"\\")) : "";

            var (status, json) = await rig.Call(step.GetProperty("method").GetString()!, route, body);
            if (json.TryGetProperty("items", out JsonElement items) && items.GetArrayLength() > 0) newest = items[0].GetProperty("name").GetString()!;

            if (status != old.GetProperty("status").GetInt32()) differences.Add($"{label}: status {status}, the old helper gave {old.GetProperty("status")}");
            var now = Flat(json, rig.Plain);
            var then = Flat(old.GetProperty("reply"), text => text);
            foreach (string field in now.Keys.Union(then.Keys))
                if (now.GetValueOrDefault(field, "(absent)") != then.GetValueOrDefault(field, "(absent)"))
                    differences.Add($"{label}: {field} is {now.GetValueOrDefault(field, "(absent)")}, the old helper gave {then.GetValueOrDefault(field, "(absent)")}");
        }
        Assert.Empty(differences);

        // The colour files, byte for byte as the old helper left them.
        string oldFiles = Path.Combine(Fixtures, "old-helper", "files");
        foreach (string name in ColourFiles.Names)
            Assert.Equal(File.ReadAllBytes(Path.Combine(oldFiles, name + ".ini")), File.ReadAllBytes(ColourFiles.PathOf(rig.IniDir, name)));
        // Nothing else in the game folder was touched, added or left behind.
        Assert.Equal(gameFilesBefore, Directory.GetFiles(rig.Game, "*", SearchOption.AllDirectories).Order());
        foreach (string untouched in new[] { "d3dx.ini", "VersionInfo.txt" })
            Assert.Equal(File.ReadAllBytes(Path.Combine(Fixtures, "game", untouched)), File.ReadAllBytes(Path.Combine(rig.Game, untouched)));

        // The two theme files it wrote (one was then deleted, which here means set aside).
        string deleted = Path.Combine(rig.Themes, "Helper Test- one.json");
        Assert.Equal(new[] { deleted }, rig.Recycled);
        Assert.Equal(File.ReadAllText(Path.Combine(oldFiles, "theme Helper Test- one.json")), Timeless(deleted + ".recycled"));
        Assert.Equal(File.ReadAllText(Path.Combine(oldFiles, "theme with a name needing care.json")), Timeless(Path.Combine(rig.Themes, "Café & Jameson's -best- + -more-.json")));

        // The three backups it made: the files as they were at that moment, and the info file written last.
        string[] made = [.. Directory.GetDirectories(rig.Backups).Select(Path.GetFileName).Where(name => !name!.StartsWith("2026-09-", StringComparison.Ordinal)).Select(name => Time.Replace(name!, "<TIME>")).Order()];
        Assert.Equal(["<TIME> Before save", "<TIME> Manual backup", "<TIME> My test backup"], made);
        foreach (string reason in new[] { "Before save", "Manual backup", "My test backup" })
        {
            string backup = Directory.GetDirectories(rig.Backups, "* " + reason).Single(dir => !Path.GetFileName(dir).StartsWith("2026-09-", StringComparison.Ordinal));
            Assert.Equal(File.ReadAllText(Path.Combine(oldFiles, $"info {reason}.json")), Timeless(Path.Combine(backup, "info.json")));
            Assert.Equal(5, Directory.GetFiles(backup).Length);
        }
        string beforeSave = Directory.GetDirectories(rig.Backups, "* Before save").Single(dir => !Path.GetFileName(dir).StartsWith("2026-09-", StringComparison.Ordinal));
        foreach (string name in ColourFiles.Names)
            Assert.Equal(File.ReadAllBytes(Path.Combine(Fixtures, "game", "EDHM-ini", name + ".ini")), File.ReadAllBytes(ColourFiles.PathOf(beforeSave, name)));

        Assert.Empty(rig.Shown);
        Assert.Equal(0, rig.KeyPresses);
    }

    // ------------------------------------------------------------ the rewrite itself

    [Theory]
    [InlineData("[Constants]\nx1 = 0.5\n", "[Constants]\nx1 = 0.25\n")]
    [InlineData("[Constants]\r\n  x1\t=   0.5 ; note\r\n", "[Constants]\r\n  x1\t=   0.25 ; note\r\n")]
    [InlineData("[Constants]\nx1=-.5e+3", "[Constants]\nx1=0.25")]
    [InlineData("[constants] ; any case\nx10 = 1\nx1 = 2\nx1 = 3\n[Next]\nx1 = 4\n", "[constants] ; any case\nx10 = 1\nx1 = 0.25\nx1 = 3\n[Next]\nx1 = 4\n")]
    [InlineData("x1 = 9\n[Other]\nx1 = 8\n \t[Constants]\nx1 = 7", "x1 = 9\n[Other]\nx1 = 8\n \t[Constants]\nx1 = 0.25")]
    public void Update_swaps_only_the_number_on_the_first_matching_line_of_the_section(string before, string after)
    {
        var missing = new List<string>();
        string text = ColourFiles.Update(before, new() { ["x1"] = "0.25" }, out int count, missing);
        Assert.Equal(after, text);
        Assert.Equal(1, count);
        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("[Constants]\n;x1 = 1\n; x1 = 1\nX1 = 1\nx1 = abc\nx1 =\nx1 = +1\nx11 = 1\n[Other]\nx1 = 1\n")]
    [InlineData("[Constants]")]
    public void Update_reports_keys_it_cannot_find_and_adds_nothing(string before)
    {
        var missing = new List<string>();
        string text = ColourFiles.Update(before, new() { ["x1"] = "0.25", ["w9"] = "1" }, out int count, missing);
        Assert.Equal(before, text);
        Assert.Equal(0, count);
        Assert.Equal(["x1", "w9"], missing);
    }

    [Fact]
    public void Update_refuses_a_file_without_the_section()
    {
        var problem = Assert.Throws<InvalidOperationException>(() => ColourFiles.Update("; nothing\nx1 = 1\n", new() { ["x1"] = "2" }, out _, []));
        Assert.Equal("The file has no [Constants] section.", problem.Message);
    }

    [Fact]
    public void Write_keeps_or_omits_the_byte_order_mark_as_the_file_had_it_and_leaves_nothing_behind()
    {
        string marked = Path.Combine(temp, "marked.ini"), plain = Path.Combine(temp, "plain.ini"), fresh = Path.Combine(temp, "fresh.ini");
        File.WriteAllBytes(marked, [0xEF, 0xBB, 0xBF, (byte)'a']);
        File.WriteAllBytes(plain, [(byte)'a']);

        foreach (string file in new[] { marked, plain, fresh }) ColourFiles.Write(file, "x1 = é\n");

        byte[] text = Encoding.UTF8.GetBytes("x1 = é\n");
        byte[] markedText = [0xEF, 0xBB, 0xBF, .. text];
        Assert.Equal(markedText, File.ReadAllBytes(marked));
        Assert.Equal(text, File.ReadAllBytes(plain));
        Assert.Equal(text, File.ReadAllBytes(fresh));
        Assert.Equal(["fresh.ini", "marked.ini", "plain.ini"], Directory.GetFiles(temp).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Write_that_cannot_change_the_file_leaves_it_as_it_was_with_nothing_beside_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to change a file another program has open.");
        string readOnly = Path.Combine(temp, "read-only.ini"), open = Path.Combine(temp, "open.ini");
        File.WriteAllText(readOnly, "x1 = 1\n");
        File.WriteAllText(open, "x1 = 1\n");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => ColourFiles.Write(readOnly, "x1 = 2\n"));
            using (new FileStream(open, FileMode.Open, FileAccess.Read, FileShare.Read))   // another program has it open
                Assert.Throws<IOException>(() => ColourFiles.Write(open, "x1 = 2\n"));
        }
        finally
        {
            File.SetAttributes(readOnly, FileAttributes.Normal);   // so the practice folder can be removed
        }

        Assert.Equal(["open.ini", "read-only.ini"], Directory.GetFiles(temp).Select(Path.GetFileName).Order());
        Assert.Equal("x1 = 1\n", File.ReadAllText(readOnly));
        Assert.Equal("x1 = 1\n", File.ReadAllText(open));
    }

    [Fact]
    public void Off_Windows_a_deleted_file_goes_to_the_desktops_Trash_where_the_file_manager_can_put_it_back()
    {
        string trash = Path.Combine(temp, "Trash"), folder = Directory.CreateDirectory(Path.Combine(temp, "My Themes")).FullName;
        string theme = Path.Combine(folder, "Explorer one.json");
        File.WriteAllText(theme, "first");
        var when = new DateTime(2026, 10, 26, 21, 5, 9);
        RecycleBin.ToTrash(theme, trash, when);

        Assert.False(File.Exists(theme));
        Assert.Equal("first", File.ReadAllText(Path.Combine(trash, "files", "Explorer one.json")));
        // The note beside it says where it was, written as in a web address, and when it was deleted (the PC's own time).
        string[] note = File.ReadAllText(Path.Combine(trash, "info", "Explorer one.json.trashinfo")).Split('\n');
        Assert.Equal("[Trash Info]", note[0]);
        Assert.StartsWith("Path=", note[1]);
        Assert.DoesNotContain(" ", note[1]);
        Assert.Equal(theme, Uri.UnescapeDataString(note[1]["Path=".Length..]));
        Assert.Equal(["DeletionDate=2026-10-26T21:05:09", ""], note[2..]);

        // Another file of that name: the next name not taken, for the file and its note alike.
        File.WriteAllText(theme, "second");
        RecycleBin.ToTrash(theme, trash, when);
        File.WriteAllText(theme, "third");
        RecycleBin.ToTrash(theme, trash, when);
        Assert.Equal(["Explorer one.2.json", "Explorer one.3.json", "Explorer one.json"],
            Directory.GetFiles(Path.Combine(trash, "files")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("third", File.ReadAllText(Path.Combine(trash, "files", "Explorer one.3.json")));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(trash, "info"), "*.trashinfo").Length);

        // A file that cannot be moved leaves no note behind.
        Assert.Throws<FileNotFoundException>(() => RecycleBin.ToTrash(Path.Combine(folder, "gone.json"), trash, when));
        Assert.False(File.Exists(Path.Combine(trash, "info", "gone.json.trashinfo")));
    }

    [Fact]
    public void The_Trash_is_the_one_the_players_file_manager_shows_even_from_inside_the_Flatpak()
    {
        string home = Path.Combine(temp, "home"), usual = Path.Combine(home, ".local", "share", "Trash");
        string TrashWith(Dictionary<string, string?> env) => RecycleBin.TrashFolder(name => env.GetValueOrDefault(name), home);
        const string App = "io.github.rmatlock0520_cell.EliteStudio", Own = "/home/deck/.var/app/" + App + "/data";

        Assert.Equal(usual, TrashWith(new()));
        Assert.Equal(Path.Combine("/data", "Trash"), TrashWith(new() { ["XDG_DATA_HOME"] = "/data" }));
        // The Flatpak points XDG_DATA_HOME at the app's own hidden folder, and keeps the player's own in HOST_XDG_DATA_HOME
        // when they set one.
        Assert.Equal(usual, TrashWith(new() { ["FLATPAK_ID"] = App, ["XDG_DATA_HOME"] = Own }));
        Assert.Equal(Path.Combine("/data", "Trash"), TrashWith(new() { ["FLATPAK_ID"] = App, ["XDG_DATA_HOME"] = Own, ["HOST_XDG_DATA_HOME"] = "/data" }));
    }

    // ------------------------------------------------------------ saving

    const string OneChange = """{"changes":{"Advanced":{"x232":"0.1"}}}""";

    [Fact]
    public async Task The_first_save_of_a_session_backs_up_first_and_later_ones_only_when_asked()
    {
        Rig rig = Practice();
        byte[] original = File.ReadAllBytes(ColourFiles.PathOf(rig.IniDir, "Advanced"));

        // Saves that write nothing are not "the first save".
        Assert.Equal(JsonValueKind.Null, (await rig.Ok("POST", "/save", """{"changes":{}}""")).GetProperty("backup").ValueKind);
        await rig.Refused("POST", "/save", """{"changes":{"Advanced":{"x232":"oops"}}}""");
        Assert.False(Directory.Exists(rig.Backups));

        JsonElement first = await rig.Ok("POST", "/save", OneChange);
        string name = first.GetProperty("backup").GetString()!;
        Assert.Matches(@"^\d{4}-\d\d-\d\d_\d\d-\d\d-\d\d Before save$", name);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(rig.Backups, name, "Advanced.ini")));
        Assert.NotEqual(original, File.ReadAllBytes(ColourFiles.PathOf(rig.IniDir, "Advanced")));
        Assert.Matches("""^\{"kind":"colors","reason":"Before save","time":"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d","edhm":"22\.03"\}$""", File.ReadAllText(Path.Combine(rig.Backups, name, "info.json")));

        JsonElement second = await rig.Ok("POST", "/save", """{"changes":{"Advanced":{"x232":"0.2"}}}""");
        Assert.Equal(JsonValueKind.Null, second.GetProperty("backup").ValueKind);
        Assert.Equal(1, second.GetProperty("written").GetInt32());

        JsonElement asked = await rig.Ok("POST", "/save", """{"changes":{"Advanced":{"x232":"0.3"}},"backup":true}""");
        Assert.EndsWith(" Before save", asked.GetProperty("backup").GetString());

        // Studio closed and opened again: a new session, so the next save backs up again.
        rig.Start(rig.Game);
        Assert.EndsWith(" Before save", (await rig.Ok("POST", "/save", OneChange)).GetProperty("backup").GetString());
    }

    [Fact]
    public async Task Saving_presses_the_reload_key_only_while_Elite_runs_and_only_if_something_changed()
    {
        Rig rig = Practice();
        rig.Running = true;

        JsonElement saved = await rig.Ok("POST", "/save", OneChange);
        Assert.Equal(1, rig.KeyPresses);
        Assert.True(saved.GetProperty("running").GetBoolean());
        Assert.Equal("""{"attempted":true,"sent":true,"error":null}""", Compact(saved.GetProperty("reload")));

        JsonElement nothingFound = await rig.Ok("POST", "/save", """{"changes":{"Advanced":{"w999":"1"}}}""");
        Assert.Equal(1, rig.KeyPresses);
        Assert.Equal(0, nothingFound.GetProperty("written").GetInt32());
        Assert.Equal("""{"attempted":false,"sent":false,"error":null}""", Compact(nothingFound.GetProperty("reload")));

        // The key could not be pressed: the save still counts, and the page tells the player to press F11.
        rig.KeyResult = new Reload(true, false, "Elite Dangerous is running but its main window handle is not available.");
        JsonElement notPressed = await rig.Ok("POST", "/save", """{"changes":{"Advanced":{"x232":"0.4"}}}""");
        Assert.Equal(1, notPressed.GetProperty("written").GetInt32());
        Assert.Equal("""{"attempted":true,"sent":false,"error":"Elite Dangerous is running but its main window handle is not available."}""", Compact(notPressed.GetProperty("reload")));

        // The reload route answers with just that result (no "ok" field), as the old helper did.
        Assert.Equal(Compact(notPressed.GetProperty("reload")), Compact(await rig.Ok("POST", "/reload", "{}")));
        Assert.Equal(3, rig.KeyPresses);

        rig.Running = false;
        JsonElement closed = await rig.Ok("POST", "/save", """{"changes":{"Advanced":{"x232":"0.5"}}}""");
        Assert.Equal(3, rig.KeyPresses);
        Assert.False(closed.GetProperty("running").GetBoolean());
        Assert.False((await rig.Ok("GET", "/status")).GetProperty("running").GetBoolean());
    }

    static string Compact(JsonElement json) => JsonSerializer.Serialize(json);

    [Fact]
    public async Task A_missing_colour_file_is_reported_and_a_file_without_the_section_stops_the_save_where_it_is()
    {
        Rig rig = NewRig();
        StageGame(rig.Game);
        File.WriteAllText(ColourFiles.PathOf(rig.IniDir, "Advanced"), "; no section header in this file\nx232 = 0.5\n");
        File.Delete(ColourFiles.PathOf(rig.IniDir, "XML-Profile"));
        rig.Start(rig.Game);

        JsonElement files = (await rig.Ok("GET", "/status")).GetProperty("edhm").GetProperty("files");
        Assert.Equal("""{"Startup-Profile":true,"Advanced":true,"SuitHud":true,"XML-Profile":false}""", Compact(files));
        Assert.Equal(["Startup-Profile", "Advanced", "SuitHud"], (await rig.Ok("GET", "/ini")).GetProperty("files").EnumerateObject().Select(file => file.Name));

        JsonElement missing = await rig.Ok("POST", "/save", """{"changes":{"XML-Profile":{"x150":"1","y150":"1"}}}""");
        Assert.Equal(0, missing.GetProperty("written").GetInt32());
        Assert.Equal(["XML-Profile:x150", "XML-Profile:y150"], missing.GetProperty("missing").EnumerateArray().Select(key => key.GetString()));
        Assert.False(File.Exists(ColourFiles.PathOf(rig.IniDir, "XML-Profile")));

        // Files are done in the order sent; what was written before the bad file stays written (as in the old helper).
        string error = await rig.Refused("POST", "/save", """{"changes":{"Startup-Profile":{"z105":"101"},"Advanced":{"x232":"0.1"},"SuitHud":{"x30":"100"}}}""");
        Assert.Equal("The file has no [Constants] section.", error);
        Assert.Contains("\nz105 = 101\n", File.ReadAllText(ColourFiles.PathOf(rig.IniDir, "Startup-Profile")));
        Assert.Contains("x30 = 199", File.ReadAllText(ColourFiles.PathOf(rig.IniDir, "SuitHud")));
    }
}
