using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Tests;

/// <summary>The app shell: start-up options, the settings file, the one-copy rule, the file-name rule, and the app-level routes' own logic.</summary>
public sealed class AppTests : IDisposable
{
    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioTests-").FullName;

    public void Dispose() => Directory.Delete(temp, recursive: true);

    sealed class Sample
    {
        public string Name { get; set; } = "default";
        public int Count { get; set; } = 3;
        public double Ratio { get; set; } = 0.5;
    }

    // ---- start-up options ----

    [Fact]
    public void Options_are_name_value_pairs_and_bare_flags()
    {
        var options = Program.ParseOptions(
            ["--headless", "--port", "47999", "--data-dir", @"C:\Practice Data\Studio", "--smoke", "--game-dir", @"D:\Games\Elite"]);

        Assert.Equal(5, options.Count);
        Assert.Equal("", options["headless"]);
        Assert.Equal("", options["smoke"]);
        Assert.Equal("47999", options["port"]);
        Assert.Equal(@"C:\Practice Data\Studio", options["data-dir"]);
        Assert.Equal(@"D:\Games\Elite", options["game-dir"]);
    }

    [Fact]
    public void Option_names_ignore_case_and_stray_words_are_skipped()
    {
        var options = Program.ParseOptions(["stray", "--Data-Dir", "D", "another", "--HEADLESS"]);

        Assert.Equal(2, options.Count);
        Assert.Equal("D", options["data-dir"]);
        Assert.True(options.ContainsKey("headless"));
        Assert.False(options.ContainsKey("smoke"));
    }

    [Fact]
    public void No_arguments_give_no_options() => Assert.Empty(Program.ParseOptions([]));

    [Theory]
    [InlineData("47830", 47830)]
    [InlineData("1", 1)]
    [InlineData("abc", Program.PreferredPort)]    // the old tool turned this into port 0
    [InlineData("", Program.PreferredPort)]
    [InlineData("0", Program.PreferredPort)]
    [InlineData("-5", Program.PreferredPort)]
    [InlineData("65530", Program.PreferredPort)]  // no room for the nine ports after it
    public void Port_option_is_used_only_when_it_is_a_usable_number(string given, int expected) =>
        Assert.Equal(expected, Program.FirstPort(Program.ParseOptions(["--port", given])));

    [Fact]
    public void Without_a_port_option_the_preferred_port_is_used()
    {
        Assert.Equal(47820, Program.PreferredPort);
        Assert.Equal(47820, Program.FirstPort(Program.ParseOptions(["--headless"])));
    }

    // ---- settings.json ----

    [Fact]
    public void Settings_survive_a_restart_and_sections_do_not_disturb_each_other()
    {
        string file = Path.Combine(temp, "settings.json");
        var first = new Settings(file);
        Assert.Equal("default", first.Get<Sample>("ui").Name);   // nothing saved yet: the class's own defaults

        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            // A PC set to German writes 1,5 for one and a half; the file must not.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            first.Set("ui", new Sample { Name = "Firefly Grove", Count = 7, Ratio = 1.5 });
            first.Set("market", new Sample { Name = "hauling", Count = 720, Ratio = 0.25 });
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }

        var second = new Settings(file);
        Sample ui = second.Get<Sample>("ui");
        Assert.Equal(("Firefly Grove", 7, 1.5), (ui.Name, ui.Count, ui.Ratio));
        Assert.Equal(720, second.Get<Sample>("market").Count);
        Assert.Equal(3, second.Get<Sample>("nav").Count);

        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal(1.5, saved.RootElement.GetProperty("ui").GetProperty("ratio").GetDouble());
        Assert.Contains("\"ratio\": 1.5", File.ReadAllText(file));
        Assert.Equal(file, Assert.Single(Directory.GetFiles(temp)));   // no half-written file left behind
    }

    [Fact]
    public void An_unreadable_settings_file_is_kept_aside_and_defaults_are_used()
    {
        string file = Path.Combine(temp, "settings.json");
        File.WriteAllText(file, "{ \"ui\": { \"name\": \"cut off in the mid");

        var settings = new Settings(file);

        Assert.Equal("default", settings.Get<Sample>("ui").Name);
        Assert.False(File.Exists(file));
        Assert.Equal("{ \"ui\": { \"name\": \"cut off in the mid", File.ReadAllText(file + ".unreadable"));

        settings.Set("ui", new Sample { Name = "fresh start" });
        Assert.Equal("fresh start", new Settings(file).Get<Sample>("ui").Name);
    }

    [Fact]
    public void A_settings_file_that_is_not_an_object_gives_defaults()
    {
        string file = Path.Combine(temp, "settings.json");
        File.WriteAllText(file, "[1, 2, 3]");

        Assert.Equal(3, new Settings(file).Get<Sample>("ui").Count);
    }

    // ---- one copy per data folder ----

    [Fact]
    public void Mutex_name_is_the_same_however_the_data_folder_is_written()
    {
        string name = Program.MutexName(temp);

        Assert.Matches(@"^Local\\EliteStudio-[0-9a-f]{8}$", name);
        Assert.Equal(name, Program.MutexName(temp + Path.DirectorySeparatorChar));
        Assert.Equal(name, Program.MutexName(temp.ToUpperInvariant()));
        Assert.Equal(name, Program.MutexName(Path.Combine(temp, "sub", "..")));
        Assert.NotEqual(name, Program.MutexName(Path.Combine(temp, "practice")));
    }

    [Fact]
    public void A_second_copy_on_the_same_data_folder_is_noticed_and_one_on_another_folder_is_not()
    {
        using var first = new Mutex(true, Program.MutexName(temp), out bool firstIsNew);
        using var second = new Mutex(true, Program.MutexName(temp), out bool secondIsNew);
        using var practice = new Mutex(true, Program.MutexName(Path.Combine(temp, "practice")), out bool practiceIsNew);

        Assert.True(firstIsNew);
        Assert.False(secondIsNew);
        Assert.True(practiceIsNew);
    }

    [Fact]
    public void Running_file_tells_a_second_launch_where_the_first_one_listens()
    {
        string json = JsonSerializer.Serialize(new RunningCopy(47821, "0123abcd", 4242), Settings.Json);

        Assert.Contains("\"port\": 47821", json);
        Assert.Contains("\"token\": \"0123abcd\"", json);
        Assert.Equal(new RunningCopy(47821, "0123abcd", 4242), JsonSerializer.Deserialize<RunningCopy>(json, Settings.Json));
    }

    // ---- the file-name rule for what the player names: Studio UI's themes, Studio Loadouts' builds ----

    [Theory]
    [InlineData("Explorer", "Explorer")]
    [InlineData("  Explorer  ", "Explorer")]
    [InlineData("Café ☕ über", "Café ☕ über")]
    [InlineData("v1.2 (long range)", "v1.2 (long range)")]
    [InlineData("To Colonia: 54 ly?", "To Colonia- 54 ly-")]          // what Windows forbids becomes "-"
    [InlineData("a<b>c\"d|e/f\\g*h", "a-b-c-d-e-f-g-h")]
    [InlineData("two\tparts\nhere", "two-parts-here")]                // a pasted tab or line break as well
    [InlineData("Ends in dots...", "Ends in dots")]                   // Windows drops dots and spaces at the end by itself,
    [InlineData("Dot, then space. .", "Dot, then space")]             // so the file would not be found again under the longer name
    [InlineData("console", "console")]                                // only the device names themselves are kept by Windows
    [InlineData("com10", "com10")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("...", null)]
    [InlineData(".hidden", null)]
    [InlineData("CON", null)]
    [InlineData("nul", null)]
    [InlineData("Aux.dark", null)]                                    // still the device to Windows 10, whatever follows the dot
    [InlineData("COM1", null)]
    [InlineData("lpt9.old", null)]
    public void A_typed_name_becomes_a_file_name_Windows_can_use_or_none(string typed, string? fileName)
    {
        Assert.Equal(fileName, Paths.FileNameFor(typed));
        if (fileName is not null) Assert.True(Paths.IsFileName(fileName));
    }

    [Fact]
    public void A_file_name_holds_up_to_80_characters_and_never_leads_out_of_its_folder()
    {
        Assert.Equal(new string('x', 80), Paths.FileNameFor(new string('x', 80)));
        Assert.Null(Paths.FileNameFor(new string('x', 81)));
        Assert.Equal(new string('x', 80), Paths.FileNameFor("  " + new string('x', 80) + "  "));   // counted as it will be used

        // What a page sends back as the name of a saved thing is checked the same way before it is put into a path.
        Assert.All(new[] { "Explorer", "To Colonia- 54 ly-", "v1.2", new string('x', 80) }, name => Assert.True(Paths.IsFileName(name)));
        Assert.All(
            new[] { "", @"..\settings", "../settings", @"sub\build", "C:build", "build.", "build ", ".", "..", ".hidden", "nul", "CON.json", "a\0b", new string('x', 81) },
            name => Assert.False(Paths.IsFileName(name)));
    }

    [Fact]
    public void The_characters_kept_out_of_file_names_are_Windows_own_on_every_system()
    {
        // So a theme or a build named on Linux can be used on Windows too, and the other way round.
        Assert.Equal(41, Paths.NotForFileNames.Length);
        Assert.All(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*', '\0', '\t', '\n', '\u001f' }, c => Assert.Contains(c, Paths.NotForFileNames));
        if (OperatingSystem.IsWindows()) Assert.Equal(Path.GetInvalidFileNameChars().Order(), Paths.NotForFileNames.Order());
    }

    // ---- the folder picker's search rule ----

    [Theory]
    [InlineData("")]
    [InlineData("Products/elite-dangerous-odyssey-64")]
    [InlineData("elite-dangerous-odyssey-64")]
    [InlineData("EDLaunch/Products/elite-dangerous-odyssey-64")]
    public void Picked_folder_leads_to_the_folder_that_holds_the_game(string place)
    {
        string picked = Path.Combine(temp, "Elite Dangerous");
        string game = Path.Combine(picked, place.Replace('/', Path.DirectorySeparatorChar));   // real folders on every system
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "EliteDangerous64.exe"), "");

        Assert.Equal(game, AppApi.FindGameFolder(picked));
    }

    [Fact]
    public void Picked_folder_itself_wins_over_the_places_below_it()
    {
        string below = Path.Combine(temp, "Products", "elite-dangerous-odyssey-64");
        Directory.CreateDirectory(below);
        File.WriteAllText(Path.Combine(below, "EliteDangerous64.exe"), "");
        Assert.Equal(below, AppApi.FindGameFolder(temp));

        File.WriteAllText(Path.Combine(temp, "EliteDangerous64.exe"), "");
        Assert.Equal(temp, AppApi.FindGameFolder(temp));
    }

    [Fact]
    public void Picked_folder_without_the_game_comes_back_marked_with_a_question_mark()
    {
        // The game somewhere the rule does not look (too deep, or another product) does not count.
        string elsewhere = Path.Combine(temp, "Games", "Products", "elite-dangerous-odyssey-64");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "EliteDangerous64.exe"), "");
        Directory.CreateDirectory(Path.Combine(temp, "Products", "elite-dangerous-odyssey-64"));

        Assert.Equal("?" + temp, AppApi.FindGameFolder(temp));
    }

    // ---- the route table ----

    [Fact]
    public async Task Routes_have_the_names_methods_and_answers_the_pages_rely_on()
    {
        Paths.Init(new Dictionary<string, string> { ["data-dir"] = temp });   // the report route writes below the data folder
        using var web = new WebServer(temp);
        using var watcher = new GameWatcher(temp, temp);
        AppApi.Map(web, watcher.State, window: null);

        async Task<JsonElement> Ask(string method, string path, string body = "")
        {
            WebResponse answer = await web.Handle(new WebRequest(method, path, new Dictionary<string, string>(), body));
            Assert.True(answer.Status == 200, $"{method} {path} answered {answer.Status}");
            return JsonDocument.Parse(answer.Body).RootElement;
        }

        Assert.Equal(JsonValueKind.False, (await Ask("GET", "/api/state")).GetProperty("gameRunning").ValueKind);

        await Ask("POST", "/api/app/dirty", "{\"count\":3}");
        Assert.Equal(3, AppApi.UnsavedChanges);
        await Ask("POST", "/api/app/dirty", "{\"count\":0}");
        Assert.Equal(0, AppApi.UnsavedChanges);

        // No window (a --headless run): the picker answers as if the player had cancelled it.
        Assert.Equal(JsonValueKind.Null, (await Ask("POST", "/api/app/pick-folder")).GetProperty("path").ValueKind);

        string report = (await Ask("POST", "/api/app/report", "{\"text\":\"Theme: Firefly Grove\"}")).GetProperty("path").GetString()!;
        Assert.Equal(Path.Combine(temp, "logs"), Path.GetDirectoryName(report));
        Assert.Contains("Theme: Firefly Grove", File.ReadAllText(report));

        Assert.True((await Ask("POST", "/api/app/log", "{\"message\":\"from a test\"}")).GetProperty("ok").GetBoolean());
        Assert.True((await Ask("POST", "/api/app/show")).GetProperty("ok").GetBoolean());
        // POST /api/app/quit is left out on purpose: it ends the program, so only a real --headless run can check it.

        // Only links to websites are opened in the browser. (A real one is not tried: it would open the browser.)
        foreach (string link in new[] { "", "file:///C:/Windows/win.ini", "javascript:alert(1)", @"C:\Windows\notepad.exe", "ftp://example.com/" })
        {
            WebResponse refused = await web.Handle(new WebRequest("POST", "/api/app/open", new Dictionary<string, string>(), JsonSerializer.Serialize(new { url = link })));
            Assert.Equal(400, refused.Status);
            Assert.Equal("Only links to websites can be opened.", JsonDocument.Parse(refused.Body).RootElement.GetProperty("error").GetString());
        }

        // The journal folder Studio reads, and whether it is there.
        JsonElement journal = await Ask("GET", "/api/app/journal");
        Assert.Equal(Paths.JournalDir, journal.GetProperty("path").GetString());
        Assert.Equal(Directory.Exists(Paths.JournalDir), journal.GetProperty("found").GetBoolean());
        // No window: the journal picker answers as if cancelled, and nothing is saved.
        Assert.Equal(JsonValueKind.Null, (await Ask("POST", "/api/app/pick-journal")).GetProperty("path").ValueKind);
    }

    [Fact]
    public void The_journal_folder_is_the_one_asked_for_then_the_one_found_then_the_systems_own()
    {
        string asked = Path.Combine(temp, "asked"), found = Path.Combine(temp, "found");
        try
        {
            Paths.Init(new Dictionary<string, string> { ["data-dir"] = temp, ["journal-dir"] = asked, ["bindings-dir"] = asked }, found, found);
            Assert.Equal((asked, asked), (Paths.JournalDir, Paths.BindingsDir));

            Paths.Init(new Dictionary<string, string> { ["data-dir"] = temp }, found, found);
            Assert.Equal((found, found), (Paths.JournalDir, Paths.BindingsDir));

            Paths.Init(new Dictionary<string, string> { ["data-dir"] = temp });
            if (OperatingSystem.IsWindows())
                Assert.EndsWith(Path.Combine("Saved Games", "Frontier Developments", "Elite Dangerous"), Paths.JournalDir);
            else   // no game found: Studio's own empty folders, until the player chooses one
                Assert.Equal((Path.Combine(temp, "no-game-found", "Journal"), Path.Combine(temp, "no-game-found", "Bindings")), (Paths.JournalDir, Paths.BindingsDir));
        }
        finally
        {
            Paths.Init(new Dictionary<string, string> { ["data-dir"] = temp });
        }
    }

    // ---- reports and zoom ----

    [Fact]
    public void Report_file_holds_the_page_text_and_this_pc_details()
    {
        DateTime before = DateTime.Now;
        string path = AppApi.SaveReport(temp, "Theme: Firefly Grove\nGame folder: not found");

        Assert.Equal(Path.Combine(temp, "logs"), Path.GetDirectoryName(path));
        Assert.Matches(@"^report-\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\.txt$", Path.GetFileName(path));
        DateTime stamp = DateTime.ParseExact(Path.GetFileNameWithoutExtension(path)["report-".Length..], "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        Assert.InRange(stamp, before.AddSeconds(-1), DateTime.Now);

        string report = File.ReadAllText(path);
        Assert.StartsWith("ELITE STUDIO REPORT", report);
        Assert.Matches(@"^\d+\.\d+\.\d+$", Program.Version);   // three numbers, as everywhere Studio names its version
        Assert.Contains($"Version: {Program.Version}{Environment.NewLine}", report);
        Assert.Contains("Theme: Firefly Grove\nGame folder: not found", report);
        // Windows is named by its version, as before; elsewhere by the system's own name.
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("Windows: " + Environment.OSVersion.VersionString, report);
            Assert.Contains($"WebView2: not found{Environment.NewLine}", report);   // no window, no engine to name
        }
        else
        {
            Assert.Contains("System: " + RuntimeInformation.OSDescription, report);
            Assert.Contains($"Web engine: no window{Environment.NewLine}", report);
        }
        Assert.Contains("Data folder: " + temp, report);

        // With a window, its web engine is named.
        string withWindow = File.ReadAllText(AppApi.SaveReport(Path.Combine(temp, "second"), "", "131.0.2903.70"));
        Assert.Contains((OperatingSystem.IsWindows() ? "WebView2: " : "Web engine: ") + "131.0.2903.70", withWindow);
    }

    [Theory]
    [InlineData(1400, 1.0)]
    [InlineData(2560, 1.0)]    // never zooms in
    [InlineData(1280, 0.91)]   // the owner's 3840-wide display at 300% scaling
    [InlineData(1000, 0.71)]
    [InlineData(840, 0.6)]
    [InlineData(500, 0.6)]     // never below 60%
    public void Window_zoom_follows_the_old_rule(double width, double expected) =>
        Assert.Equal(expected, IStudioWindow.ZoomFor(width));
}
