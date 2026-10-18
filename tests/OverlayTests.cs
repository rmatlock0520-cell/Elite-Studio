using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Overlay;

namespace EliteStudio.Tests;

/// <summary>
/// The in-game overlay as far as it can be checked without the game: key texts, where a box goes,
/// when it shows, the four boxes' settings and routes, what is drawn, and taking and giving back
/// hotkeys (with keys no keyboard has, so nothing the owner might press is ever taken).
/// </summary>
public sealed class OverlayTests : IDisposable
{
    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioTests-").FullName;

    public void Dispose() => Directory.Delete(temp, recursive: true);

    /// <summary>WPF elements and windows can only be made on a thread set up for them; test threads are not.</summary>
    internal static void OnWindowThread(Action work)
    {
        Exception? failed = null;
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception problem) { failed = problem; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failed is not null) ExceptionDispatchInfo.Capture(failed).Throw();
    }

    /// <summary>What a line of the overlay says, its runs joined.</summary>
    internal static string TextOf(TextBlock line) => string.Concat(line.Inlines.OfType<Run>().Select(run => run.Text));

    /// <summary>One call to one of the overlay's routes, without the network: the status and the answer.</summary>
    static async Task<(int Status, JsonElement Json)> Ask(WebServer web, string method, string body = "", string route = "settings")
    {
        WebResponse answer = await web.Handle(new WebRequest(method, "/api/overlay/" + route, new Dictionary<string, string>(), body));
        return (answer.Status, JsonDocument.Parse(answer.Body).RootElement);
    }

    // ---- key texts ----

    [Theory]
    [InlineData("F2", 0, 0x71)]
    [InlineData("Ctrl+Shift+H", 2 | 4, 0x48)]
    [InlineData("Alt+D1", 1, 0x31)]                        // the digit 1 is called D1
    [InlineData("Win+NumPad0", 8, 0x60)]
    [InlineData("Ctrl+Alt+Shift+Win+F24", 15, 0x87)]
    [InlineData("Shift+Return", 4, 0x0D)]
    [InlineData(" ctrl + shift + oemtilde ", 2 | 4, 0xC0)] // capitals and spaces did not matter to the old tool either
    [InlineData("F5+Ctrl", 2, 0x74)]                       // nor did the order
    public void Key_text_becomes_the_modifiers_and_key_windows_wants(string text, int modifiers, int key) =>
        Assert.Equal(((uint)modifiers, (uint)key), Hotkeys.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+Nope")]
    [InlineData("5")]            // a number is not a key name ("D5" is)
    [InlineData("None")]
    [InlineData("F2,F3")]
    // A word Studio does not know, or a second key, spoils the whole text. Dropping the word would
    // quietly take another key than the player chose: F5 on its own, in the first two.
    [InlineData("Control+F5")]   // a browser's name for Ctrl
    [InlineData("Meta+F5")]
    [InlineData("F5+Hello")]
    [InlineData("A+B")]
    [InlineData("None+F5")]
    public void Key_text_that_is_not_one_known_key_is_no_hotkey(string text) => Assert.Null(Hotkeys.Parse(text));

    // ---- where the box goes ----

    [Theory]
    // One 1920 x 1080 monitor at 100%. The box is 400 x 60 units in every row; across and down are shares of the room it has left.
    [InlineData(0, 0, 1920, 1080, 1.0, 0, 0, 0, 0)]
    [InlineData(0, 0, 1920, 1080, 1.0, 50, 0, 760, 0)]
    [InlineData(0, 0, 1920, 1080, 1.0, 100, 0, 1520, 0)]
    [InlineData(0, 0, 1920, 1080, 1.0, 50, 100, 760, 1020)]
    [InlineData(0, 0, 1920, 1080, 1.0, 50, 4, 760, 41)]                // where the haul box starts
    // The owner's 3840 x 2160 at 300%: the box is 1200 x 180 pixels there.
    [InlineData(0, 0, 3840, 2160, 3.0, 50, 0, 1320, 0)]
    [InlineData(0, 0, 3840, 2160, 3.0, 100, 100, 2640, 1980)]
    // Elite on a second monitor left of the main one and a little higher, at 150%: 600 x 90 pixels.
    [InlineData(-2560, -200, 2560, 1440, 1.5, 0, 0, -2560, -200)]
    [InlineData(-2560, -200, 2560, 1440, 1.5, 50, 50, -1580, 475)]
    [InlineData(-2560, -200, 2560, 1440, 1.5, 100, 100, -600, 1150)]
    // Elite on a 100% monitor to the right of a 3840-wide main monitor at 200%: only the monitor the
    // box goes to counts. (The old tool's sums used one scaling for every monitor.)
    [InlineData(3840, 0, 1920, 1080, 1.0, 50, 0, 4600, 0)]
    [InlineData(3840, 0, 1920, 1080, 1.0, 100, 0, 5360, 0)]
    public void Box_is_placed_in_the_pixels_and_scaling_of_the_monitor_it_goes_to(
        int left, int top, int width, int height, double scaling, double across, double down, int x, int y) =>
        Assert.Equal((x, y), OverlayWindow.PlaceOn(new Int32Rect(left, top, width, height), scaling, new Size(400, 60), across, down));

    [Fact]
    public void A_bigger_box_keeps_its_share_of_the_room_and_where_a_box_sits_reads_back_as_the_same_shares()
    {
        var monitor = new Int32Rect(0, 0, 1920, 1080);

        // the size setting is already in the box's size: twice as big is still in the middle, and still touches the edge
        Assert.Equal((560, 0), OverlayWindow.PlaceOn(monitor, 1, new Size(800, 120), 50, 0));
        Assert.Equal((1120, 960), OverlayWindow.PlaceOn(monitor, 1, new Size(800, 120), 100, 100));
        // half pixels are rounded, not cut off
        Assert.Equal((1419, 0), OverlayWindow.PlaceOn(monitor, 1.25, new Size(401, 60), 100, 0));

        // The other way round, for a box the player has dragged: on the main monitor and on one to its left and higher.
        foreach (Int32Rect screen in new[] { monitor, new Int32Rect(-2560, -200, 2560, 1440) })
            foreach ((double across, double down) in new[] { (0.0, 0.0), (50.0, 4.0), (25.0, 75.0), (100.0, 100.0) })
            {
                (int x, int y) = OverlayWindow.PlaceOn(screen, 1, new Size(400, 60), across, down);
                Assert.Equal((across, down), OverlayWindow.PlaceOf(screen, new Int32Rect(x, y, 400, 60)));
            }
        // Dragged partly off the monitor it counts as at the edge, and a box with no room to move in as in the middle.
        Assert.Equal((0, 100), OverlayWindow.PlaceOf(monitor, new Int32Rect(-50, 1070, 400, 60)));
        Assert.Equal((50, 50), OverlayWindow.PlaceOf(monitor, new Int32Rect(0, 0, 1920, 1080)));
    }

    [Fact]
    public void A_box_still_drawn_for_another_monitors_scaling_lands_wholly_on_the_new_monitor()
    {
        // Elite on a 100% monitor left of a main monitor at 300%. The box (400 x 60 units) is still drawn
        // at 300%, so it really is 1200 x 180 pixels. Placed by that size it is wholly on Elite's monitor,
        // which is what makes Windows redraw it at 100%; then it is placed again. (Placed by the size it
        // is going to have, the top right corner would be at -400: most of the box would stay on the main
        // monitor, and Windows would leave it three times too big.)
        var monitor = new Int32Rect(-1920, 0, 1920, 1080);
        foreach (double across in new[] { 0.0, 50, 100 })
            foreach (double down in new[] { 0.0, 50, 100 })
            {
                (int x, int y) = OverlayWindow.PlaceOn(monitor, 3, new Size(400, 60), across, down);
                Assert.InRange(x, -1920, -1200);
                Assert.InRange(y, 0, 1080 - 180);
            }
        Assert.Equal((-1200, 0), OverlayWindow.PlaceOn(monitor, 3, new Size(400, 60), 100, 0));
    }

    // ---- when it shows ----

    [Theory]
    [InlineData(true, true, true, false, true)]      // in the game
    [InlineData(true, true, false, true, true)]      // in Studio's own window, to see a change while making it
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, false, false, false)]    // in any other program
    [InlineData(true, false, false, false, true)]    // allowed over any program
    [InlineData(false, true, true, false, false)]    // switched off wins over everything
    [InlineData(false, false, true, true, false)]
    public void Box_shows_by_the_old_rule(bool overlayOn, bool onlyWhenElite, bool eliteInFront, bool studioInFront, bool shown) =>
        Assert.Equal(shown, OverlayWindow.ShouldShow(overlayOn, onlyWhenElite, eliteInFront, studioInFront));

    // ---- settings ----

    [Fact]
    public void Defaults_are_four_hidden_boxes_each_with_its_own_key_and_place()
    {
        var settings = new OverlaySettings();

        Assert.Equal(
            new[] { ("haul", "hotHaul", "F2"), ("market", "hotMarket", "F3"), ("nav", "hotNav", "F4"), ("loadouts", "hotLoadouts", "F5") },
            settings.Hotkeys());
        Assert.Equal(OverlaySettings.Boxes, settings.Hotkeys().Select(hotkey => hotkey.Action));
        Assert.Equal(
            new[] { new BoxLook(false, 50, 4, 1), new BoxLook(false, 2, 35, 1), new BoxLook(false, 50, 96, 1), new BoxLook(false, 98, 35, 1) },
            OverlaySettings.Boxes.Select(settings.Box));
        Assert.Equal((0.92, true, "#3D8BFF"), (settings.Opacity, settings.OnlyWhenElite, settings.Accent));   // shared by the four, and the Haul Overlay's own
        Assert.Equal(settings, settings.Cleaned());

        // one box's part is replaced without touching another's
        Assert.Equal(settings with { NavOn = true, NavX = 10, NavY = 20, NavScale = 1.5 }, settings.With("nav", new BoxLook(true, 10, 20, 1.5)));

        // each hotkey really is kept under the setting name the pages are told
        JsonElement saved = JsonSerializer.SerializeToElement(settings, Settings.Json);
        Assert.All(settings.Hotkeys(), hotkey => Assert.Equal(hotkey.Key, saved.GetProperty(hotkey.Setting).GetString()));
    }

    [Fact]
    public void Settings_survive_a_restart_in_their_own_section()
    {
        string file = Path.Combine(temp, "settings.json");
        var changed = new OverlaySettings { HotHaul = "Ctrl+Shift+H", HaulOn = true, NavX = 10, NavY = 0, MarketScale = 0.86, Opacity = 0.5, OnlyWhenElite = false, Accent = "#FF8800" };

        new Settings(file).Set("overlay", changed);

        Assert.Equal(changed, new Settings(file).Get<OverlaySettings>("overlay"));
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal(0.86, saved.RootElement.GetProperty("overlay").GetProperty("marketScale").GetDouble());
        Assert.Equal("F3", saved.RootElement.GetProperty("overlay").GetProperty("hotMarket").GetString());
    }

    [Fact]
    public void Values_out_of_range_are_brought_back_in()
    {
        OverlaySettings high = new OverlaySettings { Opacity = 7, Accent = "blue", HotMarket = "  ", HotNav = " Ctrl+Shift+C " }.Cleaned();
        Assert.Equal((1.0, "#3D8BFF", "F3", "Ctrl+Shift+C"), (high.Opacity, high.Accent, high.HotMarket, high.HotNav));

        OverlaySettings low = new OverlaySettings { Opacity = 0.01, Accent = "#12345" }.Cleaned();
        Assert.Equal((0.2, "#3D8BFF"), (low.Opacity, low.Accent));

        OverlaySettings fine = new OverlaySettings { Opacity = 0.456, Accent = "#a0B1c2" }.Cleaned();
        Assert.Equal((0.46, "#a0B1c2"), (fine.Opacity, fine.Accent));
    }

    [Fact]
    public void Every_box_is_kept_on_the_screen_and_between_its_smallest_and_biggest_size()
    {
        var standard = new OverlaySettings();
        foreach (string name in OverlaySettings.Boxes)
        {
            // past the right edge, above the top, far too big: the edges, and the biggest size; shown or not is left alone
            OverlaySettings high = standard.With(name, new BoxLook(true, 500, -3, 9)).Cleaned();
            Assert.Equal(new BoxLook(true, 100, 0, 2), high.Box(name));
            Assert.Equal(new BoxLook(false, 0, 100, 0.7), standard.With(name, new BoxLook(false, -0.5, 100.04, 0.1)).Cleaned().Box(name));
            Assert.Equal(new BoxLook(true, 33.3, 66.7, 1.23), standard.With(name, new BoxLook(true, 33.33, 66.66, 1.234)).Cleaned().Box(name));
            // the other three are not touched by it
            Assert.All(OverlaySettings.Boxes.Where(other => other != name), other => Assert.Equal(standard.Box(other), high.Box(other)));
        }
    }

    [Fact]
    public void Controller_buttons_are_kept_with_the_keys()
    {
        // none to begin with: one per hotkey, in the hotkeys' order
        var settings = new OverlaySettings();
        Assert.All(settings.PadButtons(), pad => Assert.Equal("", pad.Buttons));
        Assert.Equal(settings.Hotkeys().Select(hotkey => hotkey.Action), settings.PadButtons().Select(pad => pad.Action));

        var bound = new OverlaySettings { PadHaul = "0000:0001 1", PadMarket = "0000:0001 2", PadNav = "0000:0001 3", PadLoadouts = "0000:0001 4" };
        Assert.Equal(Enumerable.Range(1, 4).Select(button => $"0000:0001 {button}"), bound.PadButtons().Select(pad => pad.Buttons));
        Assert.Equal(bound, bound.Cleaned());
        // each really is kept under the setting name the pages are told
        JsonElement saved = JsonSerializer.SerializeToElement(bound, Settings.Json);
        Assert.All(bound.PadButtons(), pad => Assert.Equal(pad.Buttons, saved.GetProperty(pad.Setting).GetString()));

        // what cannot be read is taken off (the file can be edited by hand), the rest is kept
        OverlaySettings cleaned = (bound with { PadMarket = "Stick 1: button 7", PadNav = null!, PadLoadouts = "  0000:0001 3 + 0000:0002 up " }).Cleaned();
        Assert.Equal(("0000:0001 1", "", "", "0000:0001 3 + 0000:0002 up"), (cleaned.PadHaul, cleaned.PadMarket, cleaned.PadNav, cleaned.PadLoadouts));
    }

    // ---- the routes, and the boxes' keys ----

    [Fact]
    public async Task Settings_routes_answer_change_and_save()
    {
        using var game = new PracticeGame();
        game.Copy("Custom.4.2.binds", game.BindingsDir);
        game.Copy("StartPreset.4.start", game.BindingsDir);
        game.Copy("DisplaySettings.xml", Path.Combine(game.OptionsDir, "Graphics"));
        using GameWatcher watcher = game.Watch();
        watcher.Poll();
        string file = Path.Combine(temp, "settings.json");
        using var web = new WebServer(temp);
        using var overlay = new OverlayHost(web, new Settings(file), watcher.State, headless: true);

        // GET: the settings, one entry per hotkey, and the game's display mode
        (int status, JsonElement json) = await Ask(web, "GET");
        Assert.Equal(200, status);
        Assert.Equal(("F2", 1.0, false), (json.GetProperty("settings").GetProperty("hotHaul").GetString(),
            json.GetProperty("settings").GetProperty("haulScale").GetDouble(), json.GetProperty("settings").GetProperty("haulOn").GetBoolean()));
        Assert.Equal("borderless", json.GetProperty("displayMode").GetString());   // the owner's own display settings
        JsonElement[] keys = [.. json.GetProperty("keys").EnumerateArray()];
        Assert.Equal(OverlaySettings.Boxes, keys.Select(key => key.GetProperty("action").GetString()));
        Assert.Equal(("hotHaul", "F2", true), (keys[0].GetProperty("setting").GetString(), keys[0].GetProperty("key").GetString(), keys[0].GetProperty("valid").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, keys[0].GetProperty("registered").ValueKind);   // never tried: Elite has not been in front
        Assert.Equal(0, keys[0].GetProperty("gameActions").GetArrayLength());

        // POST: only what is named changes; a key the game uses is saved and reported, not refused
        (status, json) = await Ask(web, "POST", "{\"hotMarket\":\"F1\",\"haulScale\":1.25,\"navX\":10,\"haulOn\":true,\"somethingElse\":1}");
        Assert.Equal(200, status);
        Assert.Equal(new OverlaySettings { HotMarket = "F1", HaulScale = 1.25, NavX = 10, HaulOn = true }, overlay.Settings);
        Assert.Equal(overlay.Settings, new Settings(file).Get<OverlaySettings>("overlay"));   // saved
        Assert.Equal(1.25, json.GetProperty("settings").GetProperty("haulScale").GetDouble());
        JsonElement market = json.GetProperty("keys")[1];
        Assert.Equal(("market", "F1"), (market.GetProperty("action").GetString(), market.GetProperty("key").GetString()));
        Assert.Equal(new[] { "ShowPGScoreSummaryInput" }, market.GetProperty("gameActions").EnumerateArray().Select(action => action.GetString()));

        // POST: F11 is in no binding file, but it reloads the HUD colours and Studio UI presses it in the game itself
        (status, json) = await Ask(web, "POST", "{\"hotLoadouts\":\"Shift+F11\"}");
        Assert.Equal(200, status);
        JsonElement loadouts = json.GetProperty("keys")[3];
        Assert.Equal(("loadouts", "Shift+F11", true), (loadouts.GetProperty("action").GetString(), loadouts.GetProperty("key").GetString(), loadouts.GetProperty("valid").GetBoolean()));
        Assert.StartsWith("HUD colour reload", Assert.Single(loadouts.GetProperty("gameActions").EnumerateArray()).GetString());

        // POST: values out of range are brought back in, and a key text Studio cannot use is reported as such
        (status, json) = await Ask(web, "POST", "{\"haulScale\":9,\"opacity\":0,\"haulY\":-5,\"navX\":500,\"accent\":\"red\",\"hotNav\":\"Control+F5\"}");
        Assert.Equal(200, status);
        Assert.Equal((2.0, 0.2, 0.0, 100.0, "#3D8BFF", "Control+F5"),
            (overlay.Settings.HaulScale, overlay.Settings.Opacity, overlay.Settings.HaulY, overlay.Settings.NavX, overlay.Settings.Accent, overlay.Settings.HotNav));
        Assert.False(json.GetProperty("keys")[2].GetProperty("valid").GetBoolean());

        // POST that cannot be read: told so, and nothing changes
        OverlaySettings before = overlay.Settings;
        foreach (string unreadable in new[] { "not json", "", "[1]", "{\"haulScale\":\"big\"}", "{\"haulOn\":null}" })
        {
            (status, json) = await Ask(web, "POST", unreadable);
            Assert.True(status == 400, $"\"{unreadable}\" answered {status}");
            Assert.False(json.GetProperty("ok").GetBoolean());
            Assert.StartsWith("These overlay settings could not be read", json.GetProperty("error").GetString());
        }
        Assert.Equal(before, overlay.Settings);
        Assert.Equal(before, new Settings(file).Get<OverlaySettings>("overlay"));
    }

    [Theory]
    // missing and out-of-range values are repaired one by one, the rest is kept
    [InlineData("{\"hotHaul\":null,\"accent\":null,\"haulScale\":50,\"hotLoadouts\":\"F10\"}", 2.0, "F10")]
    // what the overlay kept before it was four boxes means nothing now and costs nothing
    [InlineData("{\"hotToggle\":\"F9\",\"position\":\"TopRight\",\"scale\":1.5,\"overlayOn\":true,\"mouseMode\":true,\"hotLoadouts\":\"F10\"}", 1.0, "F10")]
    // a value of the wrong kind makes the whole section unreadable: Studio still starts, with the overlay's defaults
    [InlineData("{\"haulOn\":\"yes\",\"hotLoadouts\":\"F10\"}", 1.0, "F5")]
    [InlineData("{\"haulX\":\"left\",\"hotLoadouts\":\"F10\"}", 1.0, "F5")]
    [InlineData("{\"haulScale\":\"1,2\",\"hotLoadouts\":\"F10\"}", 1.0, "F5")]
    [InlineData("5", 1.0, "F5")]
    [InlineData("null", 1.0, "F5")]
    public void A_settings_file_edited_by_hand_cannot_break_the_overlay(string section, double scale, string loadoutsKey)
    {
        string file = Path.Combine(temp, "settings.json");
        File.WriteAllText(file, "{\"overlay\":" + section + "}");
        using var web = new WebServer(temp);
        using var watcher = new GameWatcher(temp, temp);

        using var overlay = new OverlayHost(web, new Settings(file), watcher.State, headless: true);

        Assert.Equal(new OverlaySettings { HaulScale = scale, HotLoadouts = loadoutsKey }, overlay.Settings);
        Assert.All(OverlaySettings.Boxes, name => Assert.Null(overlay.Box(name)));   // a run without windows has no boxes
    }

    [Fact]
    public void A_boxs_key_shows_or_hides_that_box_alone_and_every_press_goes_on_to_the_studios()
    {
        string file = Path.Combine(temp, "settings.json");
        using var web = new WebServer(temp);
        using var watcher = new GameWatcher(temp, temp);
        using var overlay = new OverlayHost(web, new Settings(file), watcher.State, headless: true);
        var heard = new List<string>();
        overlay.KeyPressed += heard.Add;

        overlay.Press("market");
        Assert.Equal(new OverlaySettings { MarketOn = true }, overlay.Settings);   // the other three stay hidden
        Assert.True(new Settings(file).Get<OverlaySettings>("overlay").MarketOn);  // saved at once

        overlay.Press("accept");                    // one of a studio's own actions: passed on, and no box is switched
        Assert.Equal(new OverlaySettings { MarketOn = true }, overlay.Settings);
        overlay.Press("loadouts");
        Assert.Equal(new OverlaySettings { MarketOn = true, LoadoutsOn = true }, overlay.Settings);

        overlay.Press("market");
        Assert.Equal(new OverlaySettings { LoadoutsOn = true }, overlay.Settings);
        Assert.Equal(overlay.Settings, new Settings(file).Get<OverlaySettings>("overlay"));
        Assert.Equal(new[] { "market", "accept", "loadouts", "market" }, heard);
    }

    [Fact]
    public async Task A_page_in_one_box_can_have_another_boxs_page_opened_but_nothing_else()
    {
        using var web = new WebServer(temp);
        using var watcher = new GameWatcher(temp, temp);
        using var overlay = new OverlayHost(web, new Settings(Path.Combine(temp, "settings.json")), watcher.State, headless: true);
        async Task<(int, string)> Open(string body)
        {
            (int status, JsonElement answer) = await Ask(web, "POST", body, "open");
            return (status, answer.GetProperty("error").GetString()!);
        }

        // Only the two boxes that show a page, and only with words for that page: never an address of the asker's choosing.
        Assert.Equal((400, "That part of Studio has no overlay to open."), await Open("""{ "box": "haul", "page": "?to=Sol" }"""));
        Assert.Equal((400, "That part of Studio has no overlay to open."), await Open("""{ "box": "", "page": "" }"""));
        Assert.Equal((400, "An overlay opens with words for its own page only."), await Open("""{ "box": "nav", "page": "//elsewhere.example/" }"""));
        // In a run without windows there is no box to open, and nothing is switched on.
        Assert.Equal((409, "The overlay is not running."), await Open("""{ "box": "nav", "page": "?to=Sol" }"""));
        Assert.Equal(new OverlaySettings(), overlay.Settings);
    }

    [Fact]
    public void The_two_boxes_that_show_a_page_have_an_opacity_of_their_own()
    {
        Assert.Equal(1, new OverlaySettings().PageOpacity);   // solid, as they always were, until the player says otherwise
        Assert.Equal((0.3, 1, 0.55), ((new OverlaySettings { PageOpacity = 0.05 }).Cleaned().PageOpacity, (new OverlaySettings { PageOpacity = 7 }).Cleaned().PageOpacity,
            (new OverlaySettings { PageOpacity = 0.551 }).Cleaned().PageOpacity));
    }

    // ---- the controller buttons' routes (the buttons themselves are in ControllerTests) ----

    [Fact]
    public async Task The_listen_route_keeps_the_next_controller_button_pressed_as_an_actions_own()
    {
        string file = Path.Combine(temp, "settings.json");
        using var web = new WebServer(temp);
        using var watcher = new GameWatcher(temp, temp);
        // A made-up stick: nothing down, then button 7 pressed and let go again, and nothing more.
        var pads = new FakePads([ControllerTests.Stick()], [ControllerTests.Stick(7)], [ControllerTests.Stick()]);
        using var overlay = new OverlayHost(web, new Settings(file), watcher.State, headless: true, pads.Read);
        (string? Action, string? Setting, string? Shown) Pad(JsonElement answer, int at) => (answer.GetProperty("keys")[at].GetProperty("action").GetString(),
            answer.GetProperty("keys")[at].GetProperty("padSetting").GetString(), answer.GetProperty("keys")[at].GetProperty("pad").GetString());

        (int status, JsonElement json) = await Ask(web, "GET");
        Assert.Equal(("market", "padMarket", ""), Pad(json, 1));
        Assert.Empty(pads.Looks);   // nobody has asked for a button yet: no controller was looked at

        // the page waits on this call while the player presses the button; the answer is the settings route's
        (status, json) = await Ask(web, "POST", "{\"setting\":\"padMarket\"}", "listen");
        Assert.Equal(200, status);
        Assert.Equal("0000:0001 7", overlay.Settings.PadMarket);
        Assert.Equal(overlay.Settings, new Settings(file).Get<OverlaySettings>("overlay"));   // saved
        Assert.Equal("0000:0001 7", json.GetProperty("settings").GetProperty("padMarket").GetString());
        Assert.Equal(("market", "padMarket", "Button 7 on controller 0000:0001"), Pad(json, 1));
        Assert.Equal(("haul", "padHaul", ""), Pad(json, 0));

        // a wait is ended by a newer one, and by any change to the settings; each answers how things stand, with nothing kept
        Task<(int Status, JsonElement Json)> first = Ask(web, "POST", "{\"setting\":\"padNav\"}", "listen");
        Task<(int Status, JsonElement Json)> second = Ask(web, "POST", "{\"setting\":\"padLoadouts\"}", "listen");
        Assert.Equal(200, (await first).Status);
        Assert.False(second.IsCompleted);
        (status, json) = await Ask(web, "POST", "{\"haulScale\":1.5}");
        Assert.Equal((200, 1.5), ((await second).Status, (await second).Json.GetProperty("settings").GetProperty("haulScale").GetDouble()));
        Assert.Equal(("0000:0001 7", "", ""), (overlay.Settings.PadMarket, overlay.Settings.PadNav, overlay.Settings.PadLoadouts));

        // the button is taken off again through the settings route, where text that names no button is taken off too
        (status, json) = await Ask(web, "POST", "{\"padMarket\":\"\",\"padHaul\":\"Stick 1: button 7\"}");
        Assert.Equal((200, "", ""), (status, overlay.Settings.PadMarket, overlay.Settings.PadHaul));
        Assert.Equal(("market", "padMarket", ""), Pad(json, 1));

        // only a controller button's setting can be listened for
        foreach (string body in new[] { "{\"setting\":\"hotMarket\"}", "{\"setting\":\"haulScale\"}", "{\"setting\":null}", "{}", "" })
        {
            (status, json) = await Ask(web, "POST", body, "listen");
            Assert.True(status == 400, $"\"{body}\" answered {status}");
            Assert.False(json.GetProperty("ok").GetBoolean());
        }
    }

    [Fact]
    public async Task The_listen_route_says_so_when_there_is_no_controller()
    {
        using var web = new WebServer(temp);
        using var watcher = new GameWatcher(temp, temp);
        using var overlay = new OverlayHost(web, new Settings(Path.Combine(temp, "settings.json")), watcher.State, headless: true, new FakePads([[]]).Read);

        (int status, JsonElement json) = await Ask(web, "POST", "{\"setting\":\"padHaul\"}", "listen");

        Assert.Equal((404, false), (status, json.GetProperty("ok").GetBoolean()));   // not 408: a browser sends such a request again by itself
        Assert.StartsWith("Windows shows Studio no game controller.", json.GetProperty("error").GetString());   // the page shows these words under the boxes
        Assert.Equal("", overlay.Settings.PadHaul);
    }

    // ---- the first run's copy from the Elite Haul Overlay, and the "overlay" event ----

    [Theory]
    [InlineData("owner.json", true)]
    [InlineData("version-1.0.json", true)]     // left with the old overlay showing: Studio's boxes start hidden all the same
    [InlineData("no such file.json", false)]   // the old tool was never used here: nothing was copied
    public async Task The_first_runs_copy_is_told_by_the_settings_route_until_the_player_has_read_of_it(string oldFile, bool copied)
    {
        string file = Path.Combine(temp, "settings.json");
        string old = HaulRig.OldFile(oldFile);
        using var web = new WebServer(temp);
        using var watcher = new GameWatcher(temp, temp);
        using var overlay = new OverlayHost(web, new Settings(file), watcher.State, headless: true);
        overlay.CopyOldSettings(old);

        async Task<string?> Told(WebServer server, string method = "GET", string route = "settings", string body = "")
        {
            (int status, JsonElement json) = await Ask(server, method, body, route);
            Assert.Equal(200, status);
            return json.GetProperty("copiedFrom").GetString();
        }
        string? news = copied ? old : null;

        Assert.Equal(news, await Told(web));
        Assert.DoesNotContain(true, OverlaySettings.Boxes.Select(box => overlay.Settings.Box(box).On));   // copying switches nothing on
        // It stays news whatever is changed meanwhile, and no page can put other words there.
        Assert.Equal(news, await Told(web, "POST", body: "{\"haulScale\":1.5,\"copiedFrom\":\"somewhere else\"}"));
        overlay.Press("haul");
        Assert.Equal(news, await Told(web));

        // Kept with the settings, so it is still news after a restart.
        using var webAgain = new WebServer(temp);
        using var again = new OverlayHost(webAgain, new Settings(file), watcher.State, headless: true);
        again.CopyOldSettings(old);   // "overlay" has been saved: nothing is copied a second time
        Assert.Equal(news, await Told(webAgain));
        Assert.Equal((1.5, true), (again.Settings.HaulScale, again.Settings.HaulOn));

        // Read: forgotten for good, and the settings themselves stay.
        Assert.Null(await Told(webAgain, "POST", "old-tool-read", "{}"));
        Assert.Null(await Told(webAgain));
        Assert.Equal((1.5, true), (again.Settings.HaulScale, again.Settings.HaulOn));
        Assert.DoesNotContain("copiedFrom", File.ReadAllText(file));
    }

    [Fact]
    public async Task What_changes_in_the_settings_answer_reaches_a_listening_page_as_an_overlay_event()
    {
        // A box's key is pressed in the game, where no page asked for anything: the event is the only
        // way a page hears of it. Hence the real server on a port here, listened to the way a page listens.
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using var game = new PracticeGame();
        using GameWatcher watcher = game.Watch();
        using var web = new WebServer(temp);
        using var overlay = new OverlayHost(web, new Settings(Path.Combine(temp, "settings.json")), watcher.State, headless: true);
        Assert.True(web.Start(47860));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var events = new StreamReader(await http.GetStreamAsync($"http://127.0.0.1:{web.Port}/api/events?t={web.Token}", cancel));

        async Task<JsonElement> NextOverlayEvent()
        {
            while (true)
            {
                string? line = await events.ReadLineAsync(cancel).AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancel);
                Assert.NotNull(line);   // null: the stream ended
                if (line.StartsWith("data: ", StringComparison.Ordinal)) return JsonSerializer.Deserialize<JsonElement>(line[6..]);
                Assert.True(line is "" or ": connected" or "event: overlay", $"unexpected line on the stream: {line}");
            }
        }

        overlay.Press("haul");                                      // shown with its key: told at once, as the route answers
        JsonElement shown = await NextOverlayEvent();
        Assert.True(shown.GetProperty("settings").GetProperty("haulOn").GetBoolean());
        Assert.Equal(JsonSerializer.Serialize((await Ask(web, "GET")).Json), JsonSerializer.Serialize(shown));

        overlay.Apply(overlay.Settings with { HaulScale = 1.5 });   // a change made anywhere (another page, a box's own "bigger" button)
        Assert.Equal(1.5, (await NextOverlayEvent()).GetProperty("settings").GetProperty("haulScale").GetDouble());

        // The answer also says how the game is displayed and which keys the game uses: told when that changes, not with every change in the game.
        game.Journal("Journal.2026-10-09T030000.01.log", ["{ \"timestamp\":\"2026-10-09T03:00:00Z\", \"event\":\"Music\", \"MusicTrack\":\"Exploration\" }"]);
        watcher.Poll();
        game.Copy("DisplaySettings.xml", Path.Combine(game.OptionsDir, "Graphics"));
        watcher.Poll();
        Assert.Equal("borderless", (await NextOverlayEvent()).GetProperty("displayMode").GetString());
        game.Copy("Custom.4.2.binds", game.BindingsDir);
        game.Copy("StartPreset.4.start", game.BindingsDir);
        watcher.Poll();
        overlay.Apply(overlay.Settings with { HotMarket = "F1" });
        JsonElement bound = await NextOverlayEvent();               // the bindings' own event: the keys are still as they were
        Assert.Equal("F3", bound.GetProperty("keys")[1].GetProperty("key").GetString());
        Assert.Equal("ShowPGScoreSummaryInput", (await NextOverlayEvent()).GetProperty("keys")[1].GetProperty("gameActions")[0].GetString());
    }

    // ---- what is drawn ----

    [Fact]
    public void Status_line_says_where_the_player_is() => OnWindowThread(() =>
    {
        string Line(GameSnapshot game) => TextOf(OverlayWindow.StatusLine(game, "#FF8800"));

        Assert.Equal("ELITE STUDIO   Waiting for Elite…", Line(new GameSnapshot()));
        Assert.Equal("ELITE STUDIO   Sol", Line(new GameSnapshot { StarSystem = "Sol" }));
        Assert.Equal("ELITE STUDIO   Erlaza   docked at Ito Landing", Line(new GameSnapshot { StarSystem = "Erlaza", Docked = true, Station = "Ito Landing" }));
        Assert.Equal("ELITE STUDIO   Erlaza   docked at Ito Landing   ·   5 jumps left to 61 Cygni",
            Line(new GameSnapshot { StarSystem = "Erlaza", Docked = true, Station = "Ito Landing", RouteDestination = "61 Cygni", JumpsLeft = 5 }));
        Assert.Equal("ELITE STUDIO   Sol   ·   1 jump left to Barnard's Star",
            Line(new GameSnapshot { StarSystem = "Sol", RouteDestination = "Barnard's Star", JumpsLeft = 1 }));

        // the title is in the accent colour, semi-bold, at the old header's size
        Run title = OverlayWindow.StatusLine(new GameSnapshot(), "#FF8800").Inlines.OfType<Run>().First();
        Assert.Equal((Color.FromRgb(0xFF, 0x88, 0x00), FontWeights.SemiBold, 17.0), (((SolidColorBrush)title.Foreground).Color, title.FontWeight, title.FontSize));
    });

    [Fact]
    public void Drawing_pieces_are_laid_out_as_the_old_tool_laid_them_out() => OnWindowThread(() =>
    {
        // a line of runs: colour, weight and size per run, the line's own where none is given
        TextBlock line = Draw.Text(15, Draw.Grey).Add("Ito Landing", bold: true).Add("  (L pad)", Draw.Dim).Add(" big", size: 20);
        Run[] runs = [.. line.Inlines.OfType<Run>()];
        Assert.Equal("Ito Landing  (L pad) big", TextOf(line));
        Assert.Equal((15.0, FontWeights.SemiBold, FontWeights.Normal, 20.0), (line.FontSize, runs[0].FontWeight, runs[1].FontWeight, runs[2].FontSize));
        Assert.Equal(((SolidColorBrush)line.Foreground).Color, ((SolidColorBrush)runs[0].Foreground).Color);   // inherited from the line
        Assert.Equal((Color)ColorConverter.ConvertFromString(Draw.Dim), ((SolidColorBrush)runs[1].Foreground).Color);

        // a table grows as cells are added; cells after the first column keep 20 units from their neighbour
        Grid table = Draw.Table();
        TextBlock first = table.Cell(0, 0, 12.5, Draw.Gold);
        TextBlock far = table.Cell(2, 3, 14);
        Assert.Equal((3, 4, 2), (table.RowDefinitions.Count, table.ColumnDefinitions.Count, table.Children.Count));
        Assert.All(table.ColumnDefinitions, column => Assert.True(column.Width.IsAuto));
        Assert.Equal((new Thickness(0, 1.5, 0, 1.5), new Thickness(20, 1.5, 0, 1.5)), (first.Margin, far.Margin));
        Assert.Equal((2, 3, 14.0), (Grid.GetRow(far), Grid.GetColumn(far), far.FontSize));

        // a line with text at both ends: the right-hand texts read in the order given, the left one takes the rest
        TextBlock left = Draw.Text(17), middle = Draw.Text(12.5), right = Draw.Text(17);
        DockPanel header = Draw.Line(left, middle, right);
        Assert.Equal(new UIElement[] { right, middle, left }, header.Children.Cast<UIElement>());
        Assert.Equal((Dock.Right, Dock.Right), (DockPanel.GetDock(middle), DockPanel.GetDock(right)));
        Assert.Equal((new Thickness(28, 0, 0, 1.125), new Thickness(28, 0, 0, 0)), (middle.Margin, right.Margin));   // small text sits on the big text's baseline

        Assert.Equal(("Searching…", 14.0), (TextOf(Draw.Note("Searching…")), Draw.Note("Searching…").FontSize));
        TextBlock profit = Draw.Text(14.5);
        Border rule = Draw.Rule(profit);
        Assert.Equal(new Thickness(0, 1, 0, 0), rule.BorderThickness);   // a hairline above, nothing at the sides
        Assert.Same(profit, rule.Child);
        Assert.Equal(new UIElement[] { table, rule }, Draw.Stack(table, rule).Children.Cast<UIElement>());
    });

    [Fact]
    public void With_nothing_to_draw_the_box_stays_away_even_when_switched_on_and_allowed_over_any_program() => OnWindowThread(() =>
    {
        var window = new OverlayWindow("haul", new OverlaySettings { HaulOn = true, OnlyWhenElite = false });

        window.Redraw();                // no studio has given it anything yet
        window.SetContent(() => null);  // and one that has nothing to show just now

        Assert.False(window.IsVisible);
        Assert.Null(PresentationSource.FromVisual(window));   // it was never put on screen, not even for a moment
    });

    [Fact]
    public void Being_told_to_close_does_not_end_the_overlay() => OnWindowThread(() =>
    {
        // Hidden, as every box starts, so nothing can appear on the owner's screen while this runs.
        var window = new OverlayWindow("haul", new OverlaySettings());
        bool closed = false;
        window.Closed += (_, _) => closed = true;

        window.Close();   // what Windows asks of every window when a program is told to end politely

        Assert.False(closed);   // a closed window could never be shown again
    });

    [Fact]
    public void Render_shots_draw_the_overlay_into_pictures_without_a_window() => OnWindowThread(() =>
    {
        string shots = Path.Combine(temp, "shots");

        Assert.Equal(0, OverlayWindow.RenderShots(shots));

        BitmapSource Picture(string name)
        {
            using FileStream file = File.OpenRead(Path.Combine(shots, name));
            return new FormatConvertedBitmap(BitmapDecoder.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
        }
        static Color At(BitmapSource picture, int x, int y)
        {
            var bgra = new byte[4];
            picture.CopyPixels(new Int32Rect(x, y, 1, 1), bgra, 4, 0);
            return Color.FromRgb(bgra[2], bgra[1], bgra[0]);
        }

        Assert.Equal(
            new[] { "place-bottom-right.png", "place-top-left.png", "place-top-middle.png",
                "sample-scale-0.7.png", "sample-scale-1.5.png", "sample.png", "status-waiting.png", "status.png" },
            Directory.GetFiles(shots).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal));

        // the status line is one line: far wider than high, and longer once there is something to say
        BitmapSource waiting = Picture("status-waiting.png"), status = Picture("status.png");
        Assert.InRange(waiting.PixelHeight, 100, 200);
        Assert.Equal(waiting.PixelHeight, status.PixelHeight);
        Assert.True(status.PixelWidth > waiting.PixelWidth && waiting.PixelWidth > 2 * waiting.PixelHeight, $"{waiting.PixelWidth} and {status.PixelWidth} wide");

        // the size setting scales the whole box (pictures are twice life size with a 14-unit margin all round)
        double Box(string name) => Picture(name).PixelWidth - 56;
        Assert.InRange(Box("sample-scale-1.5.png") / Box("sample.png"), 1.48, 1.52);
        Assert.InRange(Box("sample-scale-0.7.png") / Box("sample.png"), 0.68, 0.72);

        // on a 1920 x 1080 screen the box is where its place says and nowhere else: against two edges, and where the haul box starts
        var backdrop = Color.FromRgb(0x10, 0x14, 0x1C);
        var corner = new Dictionary<string, (int X, int Y)>
        {
            ["top-left"] = (8, 5), ["top-middle"] = (960, 48), ["bottom-right"] = (1912, 1075),
        };
        foreach (string position in corner.Keys)
        {
            BitmapSource screen = Picture($"place-{position}.png");
            Assert.Equal((1920, 1080), (screen.PixelWidth, screen.PixelHeight));
            foreach ((string place, (int x, int y)) in corner)
                Assert.True(At(screen, x, y) != backdrop == (place == position), $"place {position}: the box {(place == position ? "is missing from" : "is also at")} {place}");
        }
    });

    // ---- taking and giving back keys ----

    [Fact]
    public void Keys_are_taken_only_while_elite_is_in_front_and_trouble_is_reported_not_thrown() => OnWindowThread(() =>
    {
        // Hotkeys belong to the whole PC: test runs in two checkouts at the same moment would refuse
        // each other these keys. So one run at a time, by a marker every run on this PC can see.
        using var oneRunAtATime = new Mutex(false, @"Local\EliteStudioTests-hotkeys");
        try
        {
            Assert.True(oneRunAtATime.WaitOne(TimeSpan.FromMinutes(1)), "another test run has held the hotkey test for a minute");
        }
        catch (AbandonedMutexException)
        {
            // a run that was stopped half-way never gave its turn back; it is ours all the same
        }
        try
        {
            TakeAndGiveBackKeys();
        }
        finally
        {
            oneRunAtATime.ReleaseMutex();
        }
    });

    void TakeAndGiveBackKeys()
    {
        // Keys that exist on no keyboard, so nothing the owner might press is taken while this runs.
        var rare = new OverlaySettings { HotHaul = "Ctrl+Alt+Shift+F13", HotMarket = "Ctrl+Alt+Shift+F14", HotNav = "Ctrl+Alt+Shift+F15", HotLoadouts = "Ctrl+Alt+Shift+F16" };
        using var watcher = new GameWatcher(temp, temp);
        using var first = new Hotkeys(watcher.State);
        using var second = new Hotkeys(watcher.State);
        var pressed = new List<string>();
        first.Pressed += pressed.Add;
        bool?[] Taken(Hotkeys keys, OverlaySettings settings) => [.. settings.Hotkeys().Select(hotkey => keys.Registered(hotkey.Action, hotkey.Key))];

        // Each has a window of its own that only takes messages. A press reaches Studio as Windows
        // delivers it: a message to that window carrying the number the key was registered under.
        // (Really pressing keys here could land in whatever the owner is typing.)
        var windows = new List<IntPtr>();
        for (IntPtr found = IntPtr.Zero; (found = FindWindowEx(new IntPtr(-3), found, null, "Elite Studio hotkeys")) != IntPtr.Zero;)
        {
            GetWindowThreadProcessId(found, out uint owner);
            if (owner == Environment.ProcessId) windows.Add(found);
        }
        Assert.Equal(2, windows.Count);
        void Press(int number, int message = 0x0312)
        {
            foreach (IntPtr window in windows) Assert.True(PostMessage(window, message, number, IntPtr.Zero));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);   // runs the message loop until those have arrived
        }

        // Elite not in front: nothing is taken, nothing is known yet, and a stray press means nothing
        Assert.False(first.Sync(eliteInFront: false, rare));
        Assert.All(Taken(first, rare), taken => Assert.Null(taken));
        Press(1);
        Assert.Empty(pressed);

        // Elite comes to the front: all four are taken (and Sync says the keys were just asked for, which is
        // when the overlay tells the pages), and stay taken on the ticks that follow, with nothing new to tell
        Assert.True(first.Sync(eliteInFront: true, rare));
        Assert.All(Taken(first, rare), taken => Assert.True(taken));
        Assert.False(first.Sync(eliteInFront: true, rare));
        Assert.All(Taken(first, rare), taken => Assert.True(taken));
        Assert.Null(first.Registered("haul", "F2"));   // a key it was never tried with

        // the third key registered is the nav box's, the first the haul box's
        Press(3);
        Press(1);
        Press(9);            // no such key
        Press(2, 0x0400);    // some other message
        Assert.Equal(new[] { "nav", "haul" }, pressed);

        // someone else wanting the same keys is told no, key by key, and nothing is thrown
        Assert.True(second.Sync(eliteInFront: true, rare));   // refused, which is news as well
        Assert.All(Taken(second, rare), taken => Assert.False(taken));
        Assert.False(second.Sync(eliteInFront: false, rare));

        // keys changed while Elite is in front take effect at once: a key used twice goes to the earlier
        // box, and a key text Studio cannot use is simply not taken
        OverlaySettings changed = rare with { HotMarket = rare.HotHaul, HotNav = "Control+F15" };
        first.Sync(eliteInFront: true, changed);
        Assert.Equal(new bool?[] { true, false, false, true }, Taken(first, changed));

        // Elite leaves the front: every key is given back, so the next one to ask gets them all
        first.Sync(eliteInFront: false, changed);
        Press(4);
        Assert.Equal(2, pressed.Count);
        second.Sync(eliteInFront: true, rare);
        Assert.All(Taken(second, rare), taken => Assert.True(taken));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
