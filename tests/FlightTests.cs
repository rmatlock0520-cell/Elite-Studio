using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Flight;

namespace EliteStudio.Tests;

/// <summary>
/// Studio Flight without a stick or the game: the flight bindings read from a binds file, the keys'
/// places, the curves and Gremlin's profile with its FA Off mode, the controls made of a reading, and the routes.
/// </summary>
public sealed class FlightTests : IDisposable
{
    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioTests-").FullName;

    public void Dispose() => Directory.Delete(temp, recursive: true);

    /// <summary>A Gremlin profile as the owner's: the stick's X, Y and twist (6) each through a curve to the same vJoy axis, in Default.</summary>
    const string Profile = """
        <?xml version="1.0" ?>
        <profile version="14">
            <inputs>
                <input><device-id>13ee11c0-1ad9-11f0-8001-444553540000</device-id><input-type>axis</input-type><mode>Default</mode><input-id>1</input-id>
                    <action-configuration><root-action>root-1</root-action><behavior>axis</behavior></action-configuration></input>
                <input><device-id>13ee11c0-1ad9-11f0-8001-444553540000</device-id><input-type>axis</input-type><mode>Default</mode><input-id>2</input-id>
                    <action-configuration><root-action>root-2</root-action><behavior>axis</behavior></action-configuration></input>
                <input><device-id>13ee11c0-1ad9-11f0-8001-444553540000</device-id><input-type>axis</input-type><mode>Default</mode><input-id>6</input-id>
                    <action-configuration><root-action>root-6</root-action><behavior>axis</behavior></action-configuration></input>
            </inputs>
            <settings><startup-mode>Use Heuristic</startup-mode></settings>
            <logical-device/>
            <library>
                CURVES
            </library>
            <modes><mode>Default</mode></modes>
            <scripts/>
            <devices><device><device-id>13ee11c0-1ad9-11f0-8001-444553540000</device-id><device-name>Logitech Extreme 3D</device-name></device></devices>
        </profile>
        """;

    static string ProfileText()
    {
        string curves = "";
        foreach (int axis in new[] { 1, 2, 6 })
        {
            XElement curve = Gremlin.CurveAction(new StickCurve(8, 5, 92, 68, 20));
            curve.SetAttributeValue("id", "curve-" + axis);
            curves += curve + $"""
                <action id="vjoy-{axis}" type="map-to-vjoy">
                    <property type="int"><name>vjoy-device-id</name><value>1</value></property>
                    <property type="int"><name>vjoy-input-id</name><value>{axis}</value></property>
                    <property type="input_type"><name>vjoy-input-type</name><value>axis</value></property>
                    <property type="axis_mode"><name>axis-mode</name><value>absolute</value></property>
                    <property type="activation-mode"><name>activation-mode</name><value>both</value></property>
                </action>
                <action id="root-{axis}" type="root"><actions><action-id>curve-{axis}</action-id><action-id>vjoy-{axis}</action-id></actions>
                    <property type="activation-mode"><name>activation-mode</name><value>disallowed</value></property></action>
                """;
        }
        // The curves as Gremlin's own editor labels them: this is the player's profile, not one Studio wrote.
        return Profile.Replace("CURVES", curves).Replace("Elite Studio curve", "Response Curve");
    }

    static FlightBinds OwnersBinds()
    {
        XElement root = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gamefiles", "Custom.4.2.binds")).Root!;
        return FlightBinds.Read("Custom", "Custom.4.2.binds", root, deviceList: null);
    }

    [Fact]
    public void The_flight_bindings_are_read_from_the_binds_file()
    {
        FlightBinds binds = OwnersBinds();

        Assert.Equal(new FlightAxis("vJoy", "Y", true, 0), binds.Axes["pitch"]);
        Assert.Equal(new FlightAxis("vJoy", "X", false, 0), binds.Axes["roll"]);
        Assert.Equal("RZ", binds.Axes["yaw"].Axis);
        Assert.Equal(new FlightAxis("LogitechExtreme3DPro", "Z", true, 0), binds.Axes["throttle"]);
        Assert.False(binds.Axes.ContainsKey("lateral"));   // {NoDevice}
        Assert.Equal(new FlightKey("Keyboard", "Key_Z"), Assert.Single(binds.Buttons["fa"]).Press);
        Assert.True(binds.Toggles["fa"]);
        Assert.Equal(new FlightKey("LogitechExtreme3DPro", "Joy_4"), Assert.Single(binds.Buttons["boost"]).Press);
        Assert.Equal(new FlightKey("Keyboard", "Key_W"), binds.Buttons["throttleUp"][0].Press);   // this recording has W on the throttle
        // the devices' numbers come from Studio's own short list when the game's is not there
        Assert.Equal(["046D:C215"], binds.Devices["LogitechExtreme3DPro"]);
        Assert.Equal(["1234:BEAD"], binds.Devices["vJoy"]);
    }

    [Fact]
    public void Devices_are_named_by_the_games_list_or_by_their_numbers()
    {
        string list = Path.Combine(temp, "DeviceMappings.xml");
        File.WriteAllText(list, "<Root><MyStick><PID>00F3</PID><VID>068E</VID><Alternative><PID>00F4</PID><VID>068e</VID></Alternative></MyStick></Root>");
        Dictionary<string, List<string>> named = FlightBinds.DeviceList(list);

        Assert.Equal(["068E:00F3", "068E:00F4"], FlightBinds.NumbersOf("MyStick", named));
        Assert.Equal(["231D:0200"], FlightBinds.NumbersOf("231D0200", named));   // a device the game has no name for
        Assert.Empty(FlightBinds.NumbersOf("SomethingElse", named));
        Assert.Empty(FlightBinds.DeviceList(Path.Combine(temp, "missing.xml")));
    }

    [Theory]
    [InlineData("Key_Z", 0x2C, 44)]
    [InlineData("Key_W", 0x11, 17)]
    [InlineData("Key_UpArrow", 0xE048, 256 + 0x48)]
    [InlineData("Key_Numpad_8", 0x48, 0x48)]
    [InlineData("Key_RightControl", 0xE01D, 256 + 0x1D)]
    public void Keys_are_places_on_the_keyboard(string key, int scan, int gremlin)
    {
        Assert.Equal(scan, KeyNames.ScanCode(key));
        Assert.Equal(gremlin, KeyNames.GremlinId(key));
    }

    [Fact]
    public void Keys_Windows_names_alike_on_every_keyboard()
    {
        Assert.Equal(0x20, KeyNames.VirtualKey("Key_Space"));
        Assert.Equal(0x70, KeyNames.VirtualKey("Key_F1"));
        Assert.Equal(0xA0, KeyNames.VirtualKey("Key_LeftShift"));
        Assert.Equal(0x26, KeyNames.VirtualKey("Key_UpArrow"));
        Assert.Null(KeyNames.ScanCode("Key_Nothing"));
        Assert.Equal(0, KeyNames.VirtualKey("Key_Nothing"));
    }

    [Fact]
    public void A_curve_has_its_dead_zones_its_travel_and_its_top()
    {
        var curve = new StickCurve(8, 5, 92, 68, 20);
        Assert.Equal(0, curve.At(0.049));     // inside the dead zones: 5% on the plus side...
        Assert.Equal(0, curve.At(-0.079));    // ...8% on the minus side
        Assert.True(curve.At(0.06) > 0);
        Assert.Equal(0.68, curve.At(0.95), 6);  // past the travel used: the top
        Assert.Equal(-0.68, curve.At(-1), 6);
        Assert.Equal(0.68 * (0.8 * 0.5 + 0.2 * 0.125), curve.Shape(0.5), 9);

        Assert.Equal(new StickCurve(40, 0, 50, 10, 100), new StickCurve(90, -3, 10, 0, 300).Cleaned());
        Assert.Equal(new StickCurve(0, 0, 50, 10, 0), new StickCurve(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN).Cleaned());
    }

    [Fact]
    public void Gremlins_curves_are_read_back_as_Studios_numbers()
    {
        IReadOnlyList<GremlinAxis> axes = Gremlin.Axes(XDocument.Parse(ProfileText()));

        Assert.Equal(["X", "Y", "RZ"], axes.Select(a => a.Axis));
        Assert.Equal(["X", "Y", "RZ"], axes.Select(a => a.VjoyAxis));
        Assert.All(axes, a => Assert.Equal(("Default", "Logitech Extreme 3D", 1), (a.Mode, a.DeviceName, a.VjoyDevice)));
        Assert.All(axes, a => Assert.Equal(new StickCurve(8, 5, 92, 68, 20), a.Curve));
    }

    [Fact]
    public void A_profile_gets_an_FA_Off_mode_switched_on_the_Flight_Assist_key()
    {
        var on = new StickCurve(4, 4, 92, 68, 20);
        var off = new StickCurve(2, 3, 95, 100, 40);
        var controls = new Dictionary<string, (string, StickCurve?, StickCurve)> { ["pitch"] = ("Y", on, off), ["roll"] = ("X", on, off), ["yaw"] = ("RZ", on, off) };
        FlightButton[] fa = [new FlightButton(new FlightKey("Keyboard", "Key_Z"), [])];

        (XDocument made, IReadOnlyList<string> notes) = Gremlin.WithFaOff(XDocument.Parse(ProfileText()), controls, fa, faToggles: true);
        Assert.Empty(notes);
        Check(made);

        // Done again on its own result (the player saves twice): replaced, not added twice
        (XDocument again, notes) = Gremlin.WithFaOff(made, controls, fa, faToggles: true);
        Assert.Empty(notes);
        Check(again);

        void Check(XDocument profile)
        {
            XElement root = profile.Root!;
            Assert.Equal("14", (string?)root.Attribute("version"));
            Assert.Equal(["Default", "FA Off"], root.Element("modes")!.Elements("mode").Select(m => m.Value));
            Assert.Equal("Default", (string?)root.Element("modes")!.Elements("mode").Last().Attribute("parent"));

            IReadOnlyList<GremlinAxis> axes = Gremlin.Axes(profile);
            Assert.Equal(3, axes.Count(a => a.Mode == "Default"));
            Assert.Equal(3, axes.Count(a => a.Mode == "FA Off"));
            Assert.All(axes.Where(a => a.Mode == "Default"), a => Assert.Equal(on, a.Curve));
            Assert.All(axes.Where(a => a.Mode == "FA Off"), a => Assert.Equal(off, a.Curve));
            Assert.All(axes, a => Assert.Equal(a.Axis, a.VjoyAxis));   // every axis still goes where it went

            // the switch: Z (scan code 44) on the keyboard, in Default only, cycling FA Off first
            XElement key = Assert.Single(root.Element("inputs")!.Elements("input"), i => i.Element("input-type")!.Value == "key");
            Assert.Equal(("6f1d2b61-d5a0-11cf-bfc7-444553540000", "44", "Default"), (key.Element("device-id")!.Value, key.Element("input-id")!.Value, key.Element("mode")!.Value));
            XElement change = Assert.Single(root.Element("library")!.Elements("action"), a => (string?)a.Attribute("type") == "change-mode");
            Assert.Equal(["FA Off", "Default"], change.Elements("target-mode").Select(t => t.Element("property")!.Element("value")!.Value));

            // every action is reached from an input, and every id an input or action names is there
            var ids = root.Element("library")!.Elements("action").Select(a => (string)a.Attribute("id")!).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            var named = root.Descendants("root-action").Concat(root.Descendants("action-id")).Select(e => e.Value.Trim()).ToHashSet();
            Assert.True(named.SetEquals(ids));
        }
    }

    [Fact]
    public void A_Flight_Assist_held_or_unbound_is_said()
    {
        var controls = new Dictionary<string, (string, StickCurve?, StickCurve)> { ["pitch"] = ("Y", new(), new()) };
        (XDocument held, _) = Gremlin.WithFaOff(XDocument.Parse(ProfileText()), controls, [new FlightButton(new FlightKey("Keyboard", "Key_Z"), [])], faToggles: false);
        Assert.Equal("Temporary", held.Root!.Descendants("action").Single(a => (string?)a.Attribute("type") == "change-mode").Elements("property").First().Element("value")!.Value);

        (_, IReadOnlyList<string> notes) = Gremlin.WithFaOff(XDocument.Parse(ProfileText()), controls, [], faToggles: true);
        Assert.Contains(notes, n => n.Contains("no key or button"));
        XDocument nothing;
        (nothing, notes) = Gremlin.WithFaOff(XDocument.Parse(ProfileText()),
            new Dictionary<string, (string, StickCurve?, StickCurve)> { ["yaw"] = ("Z", new(), new()) }, [], faToggles: true);
        Assert.Contains(notes, n => n.Contains("No axis of this profile"));
        Assert.False(Gremlin.IsStudios(nothing));   // nothing of Studio's in it: the app then saves no file at all

        Assert.Throws<InvalidDataException>(() => Gremlin.Axes(XDocument.Parse("<profile version=\"13\"/>")));
    }

    [Fact]
    public void Saving_never_writes_over_the_players_own_profile()
    {
        var off = new Dictionary<string, (string, StickCurve?, StickCurve)> { ["pitch"] = ("Y", null, new StickCurve()) };
        XDocument studios = Gremlin.WithFaOff(XDocument.Parse(ProfileText()), off, [], faToggles: true).Profile;
        Assert.True(Gremlin.IsStudios(studios));
        Assert.False(Gremlin.IsStudios(XDocument.Parse(ProfileText())));

        string own = Path.Combine(temp, "elite.xml");
        File.WriteAllText(own, ProfileText());
        string first = Gremlin.Save(studios, own);
        Assert.Equal(Path.Combine(temp, "elite-fa-off.xml"), first);
        Assert.Equal(ProfileText(), File.ReadAllText(own));
        // saving from the FA Off file itself (the player opened it in Gremlin) writes over it, after a backup
        Assert.Equal(first, Gremlin.Save(studios, first));
        Assert.Single(Directory.GetFiles(temp, "elite-fa-off.xml.backup-*"));

        // a profile of the player's own that only happens to be named so is never written over
        string mine = Path.Combine(temp, "mine-fa-off.xml");
        File.WriteAllText(mine, ProfileText());
        Assert.Equal(Path.Combine(temp, "mine-fa-off-fa-off.xml"), Gremlin.Save(studios, mine));
        Assert.Equal(ProfileText(), File.ReadAllText(mine));
        // nor is a file of the player's own where Studio's copy would go
        string other = Path.Combine(temp, "other.xml"), taken = Path.Combine(temp, "other-fa-off.xml");
        File.WriteAllText(other, ProfileText());
        File.WriteAllText(taken, ProfileText());
        Assert.Equal(Path.Combine(temp, "other-studio-fa-off.xml"), Gremlin.Save(studios, other));
        Assert.Equal(ProfileText(), File.ReadAllText(taken));
    }

    [Fact]
    public void Without_Studio_on_curves_the_players_Default_stays_as_it_was()
    {
        var off = new StickCurve(2, 3, 95, 100, 40);
        var controls = new Dictionary<string, (string, StickCurve?, StickCurve)> { ["pitch"] = ("Y", null, off), ["roll"] = ("X", null, off), ["yaw"] = ("RZ", null, off) };
        XDocument made = Gremlin.WithFaOff(XDocument.Parse(ProfileText()), controls, [], faToggles: true).Profile;

        // Default keeps the player's very own curve actions, untouched
        foreach (int axis in new[] { 1, 2, 6 })
            Assert.Equal(XDocument.Parse(ProfileText()).Root!.Descendants("action").Single(a => (string?)a.Attribute("id") == "curve-" + axis).ToString(),
                made.Root!.Descendants("action").Single(a => (string?)a.Attribute("id") == "curve-" + axis).ToString());
        Assert.All(Gremlin.Axes(made).Where(a => a.Mode == "FA Off"), a => Assert.Equal(off, a.Curve));
    }

    [Fact]
    public void What_the_player_put_in_FA_Off_stays_when_Studio_saves_again()
    {
        // The player's own FA Off mode with a button of their own in it (a description, say).
        string theirs = ProfileText()
            .Replace("</inputs>", """
                <input><device-id>13ee11c0-1ad9-11f0-8001-444553540000</device-id><input-type>button</input-type><mode>FA Off</mode><input-id>3</input-id>
                    <action-configuration><root-action>their-root</root-action><behavior>button</behavior></action-configuration></input>
                </inputs>
                """)
            .Replace("</library>", """
                <action id="their-note" type="description"><property type="string"><name>description</name><value>mine</value></property></action>
                <action id="their-root" type="root"><actions><action-id>their-note</action-id></actions></action>
                </library>
                """)
            .Replace("<modes><mode>Default</mode></modes>", "<modes><mode>Default</mode><mode parent=\"Default\">FA Off</mode></modes>");
        var controls = new Dictionary<string, (string, StickCurve?, StickCurve)> { ["pitch"] = ("Y", null, new StickCurve()) };
        XDocument once = Gremlin.WithFaOff(XDocument.Parse(theirs), controls, [], faToggles: true).Profile;
        XDocument twice = Gremlin.WithFaOff(once, controls, [], faToggles: true).Profile;
        foreach (XDocument made in new[] { once, twice })
        {
            Assert.Single(made.Root!.Element("inputs")!.Elements("input"), i => i.Descendants("root-action").Single().Value == "their-root");
            Assert.Contains(made.Root!.Element("library")!.Elements("action"), a => (string?)a.Attribute("id") == "their-note");
            Assert.Single(Gremlin.Axes(made), a => a.Mode == "FA Off");
            Assert.Single(made.Root!.Element("modes")!.Elements("mode"), m => m.Value == "FA Off");
        }
    }

    [Fact]
    public void Curves_Studio_cannot_have_made_are_not_read_as_its_own()
    {
        XElement Curve(string type, string low, string high, params string[] points) => XElement.Parse($"""
            <action id="c" type="response-curve">
                <deadzone>
                    <property type="float"><name>low</name><value>{low}</value></property>
                    <property type="float"><name>center-low</name><value>-0.04</value></property>
                    <property type="float"><name>center-high</name><value>0.04</value></property>
                    <property type="float"><name>high</name><value>{high}</value></property>
                </deadzone>
                <control-points>{string.Concat(points.Select(p => $"<property type=\"point2d\"><name>point</name><value>{p}</value></property>"))}</control-points>
                <property type="string"><name>curve-type</name><value>{type}</value></property>
            </action>
            """);
        Assert.Equal(new StickCurve(4, 4, 92, 100, 0), Gremlin.ReadCurve(Curve("PiecewiseLinear", "-0.92", "0.92", "-1.0,-1.0", "0.0,0.0", "1.0,1.0")));
        Assert.Equal(new StickCurve(4, 4, 92, 100, 0), Gremlin.ReadCurve(Curve("PiecewiseLinear", "-0.92", "0.92", "-1.0,-1.0", "1.0,1.0")));   // Gremlin's own straight line
        Assert.Null(Gremlin.ReadCurve(Curve("CubicSpline", "-0.92", "0.92", "-1.0,-1.0", "0.0,0.0", "1.0,1.0")));
        Assert.Null(Gremlin.ReadCurve(Curve("PiecewiseLinear", "-0.92", "0.92", "-1.0,1.0", "0.0,0.0", "1.0,-1.0")));     // inverted
        Assert.Null(Gremlin.ReadCurve(Curve("PiecewiseLinear", "-0.80", "0.92", "-1.0,-1.0", "0.0,0.0", "1.0,1.0")));     // a travel either side
        Assert.Null(Gremlin.ReadCurve(Curve("PiecewiseLinear", "-0.92", "0.92", "-1.0,-0.5", "0.0,0.0", "1.0,1.0")));     // lopsided
        Assert.Null(Gremlin.ReadCurve(Curve("PiecewiseLinear", "-0.92", "0.92", "-1.0,-1.0", "-0.5,-0.1", "0.0,0.0", "0.5,0.1", "1.0,1.0")));   // not Studio's shape
    }

    [Fact]
    public void Number_pad_keys_are_their_own_keys()
    {
        Assert.Equal(0x68, KeyNames.VirtualKey("Key_Numpad_8"));    // VK_NUMPAD8, not the Up arrow it shares a place with
        Assert.Equal(0x60, KeyNames.VirtualKey("Key_Numpad_0"));
        Assert.Equal(0x6E, KeyNames.VirtualKey("Key_Numpad_Decimal"));
        Assert.Equal(0x26, KeyNames.VirtualKey("Key_UpArrow"));
        Assert.Null(KeyNames.ScanCode(null));
    }

    [Fact]
    public void A_null_in_a_hand_edited_section_is_left_out_not_fatal()
    {
        var wild = new FlightSettings
        {
            AxisFix = new() { ["046D:C215 RZ"] = null! },
            Sources = new() { ["pitch"] = null! },
            TrainerKeys = new() { ["pitchUp"] = null! },
        };
        FlightSettings clean = wild.Cleaned();
        Assert.Empty(clean.AxisFix);
        Assert.Empty(clean.Sources);
        Assert.Empty(clean.TrainerKeys);

        string file = Path.Combine(temp, "settings.json");
        File.WriteAllText(file, "{\"flight\":{\"sources\":{\"pitch\":null},\"trainerKeys\":{\"pitchUp\":null}}}");
        using var game = new PracticeGame();
        using GameWatcher watcher = game.Watch();
        using var web = new WebServer(temp);
        using var flight = new FlightApi(web, new Settings(file), watcher.State, null, game.BindingsDir, null, gremlinProfile: () => null, sticks: _ => [], held: _ => false);
        Assert.Empty(flight.Current.Sources);
    }

    [Fact]
    public void A_chosen_stick_of_two_alike_is_that_very_one()
    {
        StickState first = Stick("046D:C215", "Logitech Extreme 3D", 0, ("R", 0.1)), second = Stick("046D:C215#2", "Logitech Extreme 3D", 0, ("R", 0.9));
        Assert.Same(second, FlightControls.Find([first, second], new AxisRef(["046D:C215#2"], null, "RZ")));
        Assert.Same(first, FlightControls.Find([first, second], new AxisRef(["046D:C215"], null, "RZ")));
        // its twin unplugged, the chosen stick is the only one left and Windows names it plainly again: still found
        StickState alone = Stick("046D:C215", "Logitech Extreme 3D", 0, ("R", 0.9));
        Assert.Same(alone, FlightControls.Find([alone], new AxisRef(["046D:C215#2"], null, "RZ")));
        var controls = new FlightControls(OwnersBinds(), [], new FlightSettings { Sources = new() { ["yaw"] = "046D:C215#2 RZ" }, AxisFix = new() { ["046D:C215#2 RZ"] = "R" } });
        Assert.Equal(0.9, controls.Take(new FlightFrame(1, [first, second], [])).Controls["yaw"].Real);
    }

    [Fact]
    public void The_flight_box_comes_up_when_Flight_Assist_goes_off_and_goes_again_after_a_restart() => OverlayTests.OnWindowThread(() =>
    {
        using var game = new PracticeGame();
        string status = Path.Combine(game.JournalDir, "Status.json");
        // (the two differ in length as well, so the watcher sees the second however soon it comes)
        void Fa(bool off) => game.Write(status, "{ \"timestamp\":\"2026-10-19T12:00:00Z\", \"event\":\"Status\", \"Flags\":" + ((1 << 24) | (off ? 1 << 5 : 0)) + (off ? ", \"Cargo\":1.0" : "") + " }\r\n");
        using GameWatcher watcher = game.Watch();
        string file = Path.Combine(temp, "settings.json");
        using var web = new WebServer(temp);
        var settings = new Settings(file);   // one for both, as in the app: two would each write their own copy of the file
        using (var overlay = new Overlay.OverlayHost(web, settings, watcher.State, headless: true))
        using (var flight = new FlightApi(web, settings, watcher.State, overlay, game.BindingsDir, null, gremlinProfile: () => null, sticks: _ => [], held: _ => false))
        {
            Fa(off: true);
            watcher.Poll();
            flight.CheckFlightAssist();
            Assert.True(overlay.Settings.FlightOn);
            Assert.True(flight.Current.ShownForFaOff);
        }
        // Studio restarted while Flight Assist was off; it comes back on: the box goes, as it came by itself
        settings = new Settings(file);
        using (var overlay = new Overlay.OverlayHost(web, settings, watcher.State, headless: true))
        using (var flight = new FlightApi(web, settings, watcher.State, overlay, game.BindingsDir, null, gremlinProfile: () => null, sticks: _ => [], held: _ => false))
        {
            Assert.True(overlay.Settings.FlightOn);
            Fa(off: false);
            watcher.Poll();
            flight.CheckFlightAssist();
            Assert.False(overlay.Settings.FlightOn);
            Assert.False(flight.Current.ShownForFaOff);
        }
    });

    [Fact]
    public void Gremlins_last_profile_comes_from_its_settings()
    {
        string profile = Path.Combine(temp, "p.xml");
        File.WriteAllText(profile, ProfileText());
        string settings = Path.Combine(temp, "configuration.json");
        File.WriteAllText(settings, JsonSerializer.Serialize(new { global = new { @internal = new Dictionary<string, object> { ["last-profile"] = new { value = profile } } } }));
        Assert.Equal(profile, Gremlin.LastProfile(settings));
        File.WriteAllText(settings, "{}");
        Assert.Null(Gremlin.LastProfile(settings));
        Assert.Null(Gremlin.LastProfile(Path.Combine(temp, "none.json")));
    }

    [Fact]
    public void Stick_readings_are_scaled_to_the_devices_ends()
    {
        Assert.Equal(-1, WindowsSticks.Scale(0, 0, 65535));
        Assert.Equal(1, WindowsSticks.Scale(65535, 0, 65535));
        Assert.Equal(0, WindowsSticks.Scale(32767, 0, 65535), 3);
        Assert.Equal(0, WindowsSticks.Scale(5, 10, 10));   // a device that says nothing of its ends
    }

    static StickState Stick(string device, string name, ulong down = 0, params (string, double)[] axes) => new(device, name, axes.ToDictionary(a => a.Item1, a => a.Item2), down);

    [Fact]
    public void A_reading_becomes_the_flight_controls()
    {
        FlightBinds binds = OwnersBinds();
        IReadOnlyList<GremlinAxis> gremlin = Gremlin.Axes(XDocument.Parse(ProfileText()));
        var controls = new FlightControls(binds, gremlin, new FlightSettings());

        var frame = new FlightFrame(100, [
            Stick("1234:BEAD", "vJoy Device", 0, ("X", 0.1), ("Y", -0.2), ("Z", 0), ("R", 0.3)),
            Stick("046D:C215", "Logitech Extreme 3D", 1UL << 3, ("X", 0.15), ("Y", -0.3), ("Z", -1), ("R", 0.45)),
        ], ["Key_W", "Key_Numpad_8"]);
        ControlsFrame now = controls.Take(frame);

        // the game gets vJoy; the real stick is the Logitech behind Gremlin (found by its name; twist RZ is Windows' R)
        Assert.Equal(new ControlValue(-0.2, -0.3, true), now.Controls["pitch"]);
        Assert.Equal(new ControlValue(0.1, 0.15, false), now.Controls["roll"]);
        Assert.Equal(new ControlValue(0.3, 0.45, false), now.Controls["yaw"]);
        Assert.Equal(-1, now.Controls["throttle"].Game);   // this recording has the throttle on the Logitech's Z
        Assert.Null(now.Controls["lateral"].Game);
        // W raises the throttle; button 4 of the Logitech is boost; Numpad 8 is the trainer's own pitch down, as the game has none
        Assert.Contains("throttleUp", now.Pressed);
        Assert.Contains("boost", now.Pressed);
        Assert.Contains("pitchDown", now.Pressed);
        Assert.Contains("pitchUp", controls.TrainerOnly);
        Assert.Contains("Key_Z", controls.Keys);

        // the player's fix: the twist is Windows' Z on this stick
        var fixedOne = new FlightControls(binds, gremlin, new FlightSettings { AxisFix = new() { ["046D:C215 RZ"] = "Z" } });
        Assert.Equal(-1, fixedOne.Take(frame).Controls["yaw"].Real);
    }

    [Fact]
    public void Settings_are_cleaned()
    {
        var wild = new FlightSettings
        {
            Ship = "../../x", Input = "mouse",
            AxisFix = new() { ["046D:C215 RZ"] = "R", ["nonsense"] = "R", ["046D:C215 Y"] = "Q" },
            TrainerKeys = new() { ["pitchUp"] = "Key_Numpad_5", ["fire"] = "Key_F", ["yawLeft"] = "Key_Nothing" },
            Progress = new() { ["chase-1"] = new ExerciseRecord(-3, 9, true), ["chase-9"] = new ExerciseRecord(), ["spin-1"] = new ExerciseRecord(1, 3, true) },
            FaOff = new FlightCurves(new StickCurve(99, 0, 0, 0, 0), new StickCurve(), new StickCurve()),
        };
        FlightSettings clean = wild.Cleaned();
        Assert.Equal(("", "stick"), (clean.Ship, clean.Input));
        Assert.Equal(new Dictionary<string, string> { ["046D:C215 RZ"] = "R" }, clean.AxisFix);
        Assert.Equal(new Dictionary<string, string> { ["pitchUp"] = "Key_Numpad_5" }, clean.TrainerKeys);
        Assert.Equal(new ExerciseRecord(0, 5, true), Assert.Single(clean.Progress).Value);
        Assert.Equal(40, clean.FaOff!.Pitch.DeadLow);
    }

    [Fact]
    public void The_flight_overlay_draws_the_hands() => OverlayTests.OnWindowThread(() =>
    {
        using var game = new PracticeGame();
        using GameWatcher watcher = game.Watch();
        string file = Path.Combine(temp, "settings.json");
        using var web = new WebServer(temp);
        using var host = new Overlay.OverlayHost(web, new Settings(file), watcher.State, headless: true);
        var overlay = new FlightOverlay(host, new FlightReader(_ => [], _ => false, null), () => new FlightControls(null, [], new FlightSettings()), watcher.State);
        var now = new ControlsFrame(1000, new Dictionary<string, ControlValue>
        {
            ["pitch"] = new(0.4, 0.6, true), ["roll"] = new(-0.3, -0.45, false), ["yaw"] = new(0.68, 1, false),
            ["throttle"] = new(-0.5, null, false), ["lateral"] = new(null, null, false), ["vertical"] = new(null, null, false),
        }, ["boost", "left", "up", "heat", "forward"], [], []);
        overlay.Show(now);

        var panel = overlay.Panel;
        panel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        panel.Arrange(new System.Windows.Rect(0, 0, panel.Width, panel.Height));
        panel.UpdateLayout();
        var picture = new System.Windows.Media.Imaging.RenderTargetBitmap(1200, 336, 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
        var back = new System.Windows.Media.DrawingVisual();
        using (var paint = back.RenderOpen()) paint.DrawRectangle(Overlay.Draw.Brush("#FF10141C"), null, new System.Windows.Rect(0, 0, 600, 168));
        picture.Render(back);
        picture.Render(panel);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(picture));
        string shot = Environment.GetEnvironmentVariable("ELITE_STUDIO_FLIGHT_SHOT") is { Length: > 0 } wanted ? wanted : Path.Combine(temp, "flight.png");
        using (FileStream stream = File.Create(shot)) png.Save(stream);
        Assert.True(new FileInfo(shot).Length > 2000);
    });

    static async Task<(int Status, JsonElement Json)> Ask(WebServer web, string method, string route, string body = "")
    {
        WebResponse answer = await web.Handle(new WebRequest(method, "/api/flight" + route, new Dictionary<string, string>(), body));
        return (answer.Status, JsonDocument.Parse(answer.Body).RootElement);
    }

    [Fact]
    public async Task The_routes_answer_keep_progress_and_save_the_Gremlin_profile()
    {
        using var game = new PracticeGame();
        game.Copy("Custom.4.2.binds", game.BindingsDir);
        game.Copy("StartPreset.4.start", game.BindingsDir);
        using GameWatcher watcher = game.Watch();
        watcher.Poll();
        string profile = Path.Combine(temp, "gremlin.xml");
        File.WriteAllText(profile, ProfileText());
        string file = Path.Combine(temp, "settings.json");
        using var web = new WebServer(temp);
        using var flight = new FlightApi(web, new Settings(file), watcher.State, null, game.BindingsDir, null,
            gremlinProfile: () => profile, sticks: _ => [], held: _ => false);

        (int status, JsonElement json) = await Ask(web, "GET", "");
        Assert.Equal(200, status);
        Assert.Equal("Custom", json.GetProperty("binds").GetProperty("preset").GetString());
        Assert.Equal(3, json.GetProperty("gremlin").GetProperty("axes").GetArrayLength());
        // the FA on curves come from Gremlin until the player saves some; FA off starts from them, with full stick and a softer middle
        JsonElement pitch = json.GetProperty("settings").GetProperty("faOn").GetProperty("pitch");
        Assert.Equal((5.0, 68.0), (pitch.GetProperty("deadHigh").GetDouble(), pitch.GetProperty("satY").GetDouble()));
        JsonElement offPitch = json.GetProperty("settings").GetProperty("faOff").GetProperty("pitch");
        Assert.Equal((100.0, 35.0), (offPitch.GetProperty("satY").GetDouble(), offPitch.GetProperty("bend").GetDouble()));
        Assert.Contains(json.GetProperty("ships").EnumerateArray(), s => s.GetProperty("id").GetString() == "cobra_mk_iii" && s.GetProperty("yaw").GetDouble() == 10);

        // a set: three of five passes, and stays passed
        (status, json) = await Ask(web, "POST", "/progress", "{\"exercise\":\"chase-1\",\"successes\":3,\"attempts\":4}");
        Assert.Equal(200, status);
        (status, json) = await Ask(web, "POST", "/progress", "{\"exercise\":\"chase-1\",\"successes\":1,\"attempts\":4}");
        JsonElement record = json.GetProperty("settings").GetProperty("progress").GetProperty("chase-1");
        Assert.Equal((2, 3, true), (record.GetProperty("sets").GetInt32(), record.GetProperty("best").GetInt32(), record.GetProperty("passed").GetBoolean()));
        (status, _) = await Ask(web, "POST", "/progress", "{\"exercise\":\"spin-1\",\"successes\":3,\"attempts\":5}");
        Assert.Equal(400, status);

        // settings: named ones change, the progress cannot be sent this way, and it is all saved
        (status, json) = await Ask(web, "POST", "/settings", "{\"ship\":\"eagle\",\"progress\":{},\"showWhenFaOff\":false}");
        Assert.Equal(200, status);
        FlightSettings saved = new Settings(file).Get<FlightSettings>("flight");
        Assert.Equal(("eagle", false, 1), (saved.Ship, saved.ShowWhenFaOff, saved.Progress.Count));
        (status, _) = await Ask(web, "POST", "/settings", "[1]");
        Assert.Equal(400, status);

        // Gremlin: a new profile beside the player's, the player's own untouched
        (status, json) = await Ask(web, "POST", "/gremlin");
        Assert.Equal(200, status);
        Assert.Equal(Path.Combine(temp, "gremlin-fa-off.xml"), json.GetProperty("path").GetString());
        Assert.Equal(ProfileText(), File.ReadAllText(profile));
        Assert.Equal(6, Gremlin.Axes(XDocument.Load(Path.Combine(temp, "gremlin-fa-off.xml"))).Count);
    }

    /// <summary>A control as the pages get it: the "flight-input" event carries it as JSON.</summary>
    static JsonElement AsSent(ControlValue value) => JsonSerializer.SerializeToElement(value, Settings.Json);

    [Fact]
    public void A_control_whose_real_stick_is_not_found_says_so_and_is_never_curved_twice()
    {
        FlightBinds binds = OwnersBinds();   // pitch, roll and yaw on vJoy, as Gremlin feeds it
        IReadOnlyList<GremlinAxis> routed = Gremlin.Axes(XDocument.Parse(ProfileText()));
        StickState vjoy = Stick("1234:BEAD", "vJoy Device", 0, ("X", 0.1), ("Y", -0.2), ("Z", 0), ("R", 0.3));
        StickState logitech = Stick("046D:C215", "Logitech Extreme 3D", 0, ("X", 0.15), ("Y", -0.3), ("Z", -1), ("R", 0.45));
        bool Unknown(ControlsFrame now, string control) => AsSent(now.Controls[control]).GetProperty("realUnknown").GetBoolean();

        // Elite reads vJoy and nothing says which stick feeds it (no Gremlin route for it): vJoy's value is already curved, so it
        // must never pass for the real stick, or the trainer curves it a second time. Studio says it does not know the stick.
        ControlsFrame unrouted = new FlightControls(binds, [], new FlightSettings()).Take(new FlightFrame(1, [vjoy, logitech], []));
        Assert.Equal(-0.2, unrouted.Controls["pitch"].Game);
        Assert.Null(unrouted.Controls["pitch"].Real);
        Assert.All((string[])["pitch", "roll", "yaw"], control => Assert.True(Unknown(unrouted, control)));
        Assert.False(Unknown(unrouted, "throttle"));   // only the turning controls have a real stick behind them

        // Gremlin routes it, but the stick it names is not plugged in: the same
        ControlsFrame gone = new FlightControls(binds, routed, new FlightSettings()).Take(new FlightFrame(2, [vjoy], []));
        Assert.Equal((-0.2, (double?)null), (gone.Controls["pitch"].Game!.Value, gone.Controls["pitch"].Real));
        Assert.True(Unknown(gone, "pitch"));

        // Found through Gremlin, or as the player picked it with Detect: nothing to say
        ControlsFrame found = new FlightControls(binds, routed, new FlightSettings()).Take(new FlightFrame(3, [vjoy, logitech], []));
        Assert.Equal(-0.3, found.Controls["pitch"].Real);
        Assert.False(Unknown(found, "pitch"));
        ControlsFrame picked = new FlightControls(binds, [], new FlightSettings { Sources = new() { ["pitch"] = "046D:C215 Y" } }).Take(new FlightFrame(4, [vjoy, logitech], []));
        Assert.Equal(-0.3, picked.Controls["pitch"].Real);
        Assert.False(Unknown(picked, "pitch"));
        Assert.True(Unknown(picked, "yaw"));
        // Elite reads the stick itself: that is the real stick
        var direct = binds with { Axes = new Dictionary<string, FlightAxis>(binds.Axes) { ["pitch"] = new FlightAxis("LogitechExtreme3DPro", "Y", false, 0) } };
        ControlsFrame itself = new FlightControls(direct, [], new FlightSettings()).Take(new FlightFrame(5, [vjoy, logitech], []));
        Assert.Equal((-0.3, -0.3), (itself.Controls["pitch"].Game!.Value, itself.Controls["pitch"].Real!.Value));
        Assert.False(Unknown(itself, "pitch"));
        // No reading at all (no stick plugged in): nothing to say here; the trainer says it found no stick
        Assert.False(Unknown(new FlightControls(binds, routed, new FlightSettings()).Take(new FlightFrame(6, [], [])), "pitch"));
    }

    [Fact]
    public async Task Stick_setup_is_told_when_the_curves_it_tunes_do_not_reach_Elite()
    {
        using var game = new PracticeGame();
        game.Copy("Custom.4.2.binds", game.BindingsDir);
        game.Copy("StartPreset.4.start", game.BindingsDir);
        using GameWatcher watcher = game.Watch();
        watcher.Poll();
        string own = Path.Combine(temp, "gremlin.xml");
        File.WriteAllText(own, ProfileText());
        string running = own;   // the profile Gremlin opens
        using var web = new WebServer(temp);
        using var flight = new FlightApi(web, new Settings(Path.Combine(temp, "settings.json")), watcher.State, null, game.BindingsDir, null,
            gremlinProfile: () => running, sticks: _ => [], held: _ => false);
        async Task<(bool HasFaOff, bool OutOfDate)> Told()
        {
            JsonElement gremlin = (await Ask(web, "GET", "")).Json.GetProperty("gremlin");
            return (gremlin.GetProperty("hasFaOff").GetBoolean(), gremlin.GetProperty("outOfDate").GetBoolean());
        }
        async Task Change(string set, string control, string field, double by)
        {
            JsonNode curves = JsonNode.Parse((await Ask(web, "GET", "")).Json.GetProperty("settings").GetProperty(set).GetRawText())!;
            curves[control]![field] = curves[control]![field]!.GetValue<double>() + by;
            Assert.Equal(200, (await Ask(web, "POST", "/settings", new JsonObject { [set] = curves }.ToJsonString())).Status);
        }

        // The player's own profile has no FA Off mode, so the curves tuned here do not reach Elite; nothing saved yet to be old.
        Assert.Equal((false, false), await Told());
        // Saved: Studio's curves read back from its 21 points are the curves in use (within their rounding)
        (int status, JsonElement saved) = await Ask(web, "POST", "/gremlin");
        Assert.Equal(200, status);
        Assert.Equal((false, false), await Told());
        // Gremlin opens the saved one: its FA Off mode is there
        running = saved.GetProperty("path").GetString()!;
        Assert.Equal((true, false), await Told());
        // A dead zone moved half a step after saving: the saved profile is older than the curves...
        await Change("faOff", "pitch", "deadLow", 0.5);
        Assert.Equal((true, true), await Told());
        // ...and moved back, the same curves again: the curves are compared, not the times things were saved
        await Change("faOff", "pitch", "deadLow", -0.5);
        Assert.Equal((true, false), await Told());
        Assert.Equal(200, (await Ask(web, "POST", "/settings", "{\"ship\":\"eagle\"}")).Status);   // a setting that is no curve
        Assert.Equal((true, false), await Told());
        // A softer middle for roll: older, until it is saved again
        await Change("faOff", "roll", "bend", 5);
        Assert.Equal((true, true), await Told());
        Assert.Equal(200, (await Ask(web, "POST", "/gremlin")).Status);
        Assert.Equal((true, false), await Told());
        // Flight Assist on curves saved here go into its Default mode: they count too
        await Change("faOn", "yaw", "satY", -10);
        Assert.Equal((true, true), await Told());
        Assert.Equal(200, (await Ask(web, "POST", "/gremlin")).Status);
        Assert.Equal((true, false), await Told());
    }

    /// <summary>Stick setup's Gremlin card with the player's own profile running and a copy saved for Gremlin: the routes, and what the card is told.</summary>
    sealed class SavedForGremlin : IDisposable
    {
        readonly PracticeGame game = new();
        readonly GameWatcher watcher;
        public readonly WebServer Web;
        readonly FlightApi flight;
        public string Running;   // the profile Gremlin opens

        public SavedForGremlin(string temp)
        {
            game.Copy("Custom.4.2.binds", game.BindingsDir);
            game.Copy("StartPreset.4.start", game.BindingsDir);
            watcher = game.Watch();
            watcher.Poll();
            Running = Path.Combine(temp, "gremlin.xml");
            File.WriteAllText(Running, ProfileText());
            Web = new WebServer(temp);
            flight = new FlightApi(Web, new Settings(Path.Combine(temp, "settings.json")), watcher.State, null, game.BindingsDir, null,
                gremlinProfile: () => Running, sticks: _ => [], held: _ => false);
        }

        /// <summary>The card's word: the saved profile is older than the curves, and which of them (Flight Assist off, on).</summary>
        public async Task<(bool OutOfDate, bool FaOff, bool FaOn)> Told()
        {
            JsonElement gremlin = (await Ask(Web, "GET", "")).Json.GetProperty("gremlin");
            JsonElement older = gremlin.GetProperty("older");
            return (gremlin.GetProperty("outOfDate").GetBoolean(), older.GetProperty("faOff").GetBoolean(), older.GetProperty("faOn").GetBoolean());
        }

        public async Task<bool> OutOfDate() => (await Ask(Web, "GET", "")).Json.GetProperty("gremlin").GetProperty("outOfDate").GetBoolean();

        public async Task Change(string set, string control, string field, double by)
        {
            JsonNode curves = JsonNode.Parse((await Ask(Web, "GET", "")).Json.GetProperty("settings").GetProperty(set).GetRawText())!;
            curves[control]![field] = curves[control]![field]!.GetValue<double>() + by;
            Assert.Equal(200, (await Ask(Web, "POST", "/settings", new JsonObject { [set] = curves }.ToJsonString())).Status);
        }

        public async Task<string> Save()
        {
            (int status, JsonElement saved) = await Ask(Web, "POST", "/gremlin");
            Assert.Equal(200, status);
            return saved.GetProperty("path").GetString()!;
        }

        public void Dispose()
        {
            flight.Dispose();
            Web.Dispose();
            watcher.Dispose();
            game.Dispose();
        }
    }

    [Fact]
    public async Task Putting_the_Flight_Assist_on_curves_back_after_a_save_makes_the_saved_profile_older()
    {
        using var card = new SavedForGremlin(temp);
        // Flight Assist on curves of the player's own, saved into the FA Off profile's Default mode: up to date.
        await card.Change("faOn", "yaw", "satY", -10);
        await card.Save();
        Assert.False(await card.OutOfDate());
        // "Back to the suggestion" for the Flight Assist on curves: they are Gremlin's own again, but the saved profile's Default
        // still has the ones saved before. Saving again would change it, so the card says so.
        Assert.Equal(200, (await Ask(card.Web, "POST", "/settings", "{\"faOn\":null}")).Status);
        Assert.True(await card.OutOfDate());
        // Saved again: its Default is the player's own Gremlin curves, as they are in the profile Gremlin opens.
        string saved = await card.Save();
        Assert.False(await card.OutOfDate());
        // Gremlin opens the saved profile itself: saving again keeps its Default as it is, so nothing is older.
        card.Running = saved;
        Assert.False(await card.OutOfDate());
    }

    [Fact]
    public async Task The_older_line_is_told_which_curves_changed_since_the_save()
    {
        using var card = new SavedForGremlin(temp);
        await card.Change("faOn", "yaw", "satY", -10);
        await card.Save();
        Assert.Equal((false, false, false), await card.Told());
        // Only the Flight Assist off curves changed...
        await card.Change("faOff", "pitch", "bend", 5);
        Assert.Equal((true, true, false), await card.Told());
        await card.Save();
        Assert.Equal((false, false, false), await card.Told());
        // ...only the Flight Assist on ones...
        await card.Change("faOn", "pitch", "deadLow", 1);
        Assert.Equal((true, false, true), await card.Told());
        // ...and both
        await card.Change("faOff", "roll", "bend", 5);
        Assert.Equal((true, true, true), await card.Told());
        await card.Save();
        Assert.Equal((false, false, false), await card.Told());
        // The Flight Assist on curves put back to Gremlin's own: the Flight Assist on ones are older, the off ones are not.
        Assert.Equal(200, (await Ask(card.Web, "POST", "/settings", "{\"faOn\":null}")).Status);
        Assert.Equal((true, false, true), await card.Told());
    }

    // ------------------------------------------------------------ Chase the dot's best scores

    /// <summary>The best scores as GET /api/flight answers them, by game ("dot2-medium-faOff" → 62.5).</summary>
    static async Task<Dictionary<string, double>> Scores(WebServer web) =>
        (await Ask(web, "GET", "")).Json.GetProperty("settings").GetProperty("scores").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());

    [Fact]
    public async Task Chase_the_dot_keeps_the_best_score_of_each_level_and_curve_set()
    {
        using var game = new PracticeGame();
        using GameWatcher watcher = game.Watch();
        string file = Path.Combine(temp, "settings.json");
        using var web = new WebServer(temp);
        using var flight = new FlightApi(web, new Settings(file), watcher.State, null, game.BindingsDir, null, gremlinProfile: () => null, sticks: _ => [], held: _ => false);

        (int status, JsonElement json) = await Ask(web, "POST", "/score", "{\"game\":\"dot2-medium-faOff\",\"pct\":62.5}");
        Assert.Equal(200, status);
        // the answer is the page's picture, the score in it
        Assert.Equal(62.5, json.GetProperty("settings").GetProperty("scores").GetProperty("dot2-medium-faOff").GetDouble());
        // A worse round leaves the best as it was, and each level and curve set has its own
        Assert.Equal(200, (await Ask(web, "POST", "/score", "{\"game\":\"dot2-medium-faOff\",\"pct\":40}")).Status);
        Assert.Equal(200, (await Ask(web, "POST", "/score", "{\"game\":\"dot2-medium-faOn\",\"pct\":30}")).Status);
        Assert.Equal(200, (await Ask(web, "POST", "/score", "{\"game\":\"dot2-hard-faOff\",\"pct\":12.5}")).Status);
        Assert.Equal(new Dictionary<string, double> { ["dot2-medium-faOff"] = 62.5, ["dot2-medium-faOn"] = 30, ["dot2-hard-faOff"] = 12.5 }, await Scores(web));
        // A better round takes its place
        Assert.Equal(200, (await Ask(web, "POST", "/score", "{\"game\":\"dot2-medium-faOff\",\"pct\":71.2}")).Status);
        Assert.Equal(71.2, (await Scores(web))["dot2-medium-faOff"]);
        // kept in settings.json, so a restart keeps them
        JsonElement kept = JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("flight").GetProperty("scores");
        Assert.Equal((71.2, 30.0, 12.5), (kept.GetProperty("dot2-medium-faOff").GetDouble(), kept.GetProperty("dot2-medium-faOn").GetDouble(), kept.GetProperty("dot2-hard-faOff").GetDouble()));
    }

    [Theory]
    [InlineData("{\"game\":\"chase-1\",\"pct\":50}")]               // a trainer exercise, not a dot game
    [InlineData("{\"game\":\"dot2-medium-roll\",\"pct\":50}")]      // no such curve set
    [InlineData("{\"game\":\"dot2-extreme-faOff\",\"pct\":50}")]    // no such level
    [InlineData("{\"game\":\"dot5-medium-faOff\",\"pct\":50}")]     // no five-axis dot game: the tunnel is that
    [InlineData("{\"game\":\"dot2-medium-faOff\",\"pct\":140}")]    // more than the whole round
    [InlineData("{\"game\":\"dot2-medium-faOff\",\"pct\":-1}")]
    [InlineData("{\"game\":\"dot2-medium-faOff\"}")]                // no score
    [InlineData("{\"game\":\"dot2-medium-faOff\",\"pct\":\"lots\"}")]
    [InlineData("[1]")]
    public async Task A_score_for_a_game_Studio_does_not_have_is_turned_away(string body)
    {
        using var game = new PracticeGame();
        using GameWatcher watcher = game.Watch();
        string file = Path.Combine(temp, "settings.json");
        using var web = new WebServer(temp);
        using var flight = new FlightApi(web, new Settings(file), watcher.State, null, game.BindingsDir, null, gremlinProfile: () => null, sticks: _ => [], held: _ => false);

        (int status, JsonElement json) = await Ask(web, "POST", "/score", body);
        Assert.Equal(400, status);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Empty(await Scores(web));
    }

    [Fact]
    public async Task The_settings_route_cannot_write_or_wipe_the_scores()
    {
        string file = Path.Combine(temp, "settings.json");
        File.WriteAllText(file, "{\"flight\":{\"scores\":{\"dot2-medium-faOff\":62.5}}}");
        using var game = new PracticeGame();
        using GameWatcher watcher = game.Watch();
        using var web = new WebServer(temp);
        using var flight = new FlightApi(web, new Settings(file), watcher.State, null, game.BindingsDir, null, gremlinProfile: () => null, sticks: _ => [], held: _ => false);
        var only = new Dictionary<string, double> { ["dot2-medium-faOff"] = 62.5 };
        Assert.Equal(only, await Scores(web));

        // The scores have their own route, which keeps only the best: a page sending them with the other settings changes nothing of
        // them, however the name is written, while the other settings it sends still change.
        foreach (string body in new[] { "{\"scores\":{\"dot2-medium-faOff\":100,\"dot2-hard-faOn\":99}}", "{\"Scores\":{\"dot2-medium-faOff\":100}}",
            "{\"scores\":{}}", "{\"scores\":null}", "{\"ship\":\"eagle\",\"SCORES\":{}}" })
        {
            Assert.Equal(200, (await Ask(web, "POST", "/settings", body)).Status);
            Assert.Equal(only, await Scores(web));
        }
        Assert.Equal("eagle", (await Ask(web, "GET", "")).Json.GetProperty("settings").GetProperty("ship").GetString());
        JsonElement kept = JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("flight").GetProperty("scores");
        Assert.Equal(only, kept.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble()));
    }

    [Fact]
    public async Task Scores_edited_by_hand_keep_only_the_dot_games_Studio_has()
    {
        // Typed into settings.json by hand: games that do not exist, a score over the whole round and one under nothing, and a null.
        string file = Path.Combine(temp, "settings.json");
        File.WriteAllText(file, "{\"flight\":{\"ship\":\"eagle\",\"scores\":{\"dot2-easy-faOff\":55,\"dot2-hard-faOn\":150,\"dot2-medium-faOn\":-3,"
            + "\"chase-1\":90,\"dot2-Easy-faOff\":20,\"dot3-easy-faOff\":20,\"dot2-easy-faOn\":null,\"dot2-hard-faOff-x\":40}}}");
        using var game = new PracticeGame();
        using GameWatcher watcher = game.Watch();
        using var web = new WebServer(temp);
        using var flight = new FlightApi(web, new Settings(file), watcher.State, null, game.BindingsDir, null, gremlinProfile: () => null, sticks: _ => [], held: _ => false);

        Assert.Equal(new Dictionary<string, double> { ["dot2-easy-faOff"] = 55, ["dot2-hard-faOn"] = 100, ["dot2-medium-faOn"] = 0 }, await Scores(web));
        // the null is left out, and the rest of the section is still the player's
        Assert.Equal("eagle", (await Ask(web, "GET", "")).Json.GetProperty("settings").GetProperty("ship").GetString());
    }

    // ------------------------------------------------------------ the last ten rounds of each game (flight-rounds.json)

    /// <summary>
    /// Studio Flight with a data folder of its own, as Program.cs hands it Studio's (settings.json is kept outside it here, so
    /// the folder holds only what the rounds put there): the routes of the last rounds, and Studio started again on that folder.
    /// </summary>
    sealed class RoundsKept : IDisposable
    {
        readonly PracticeGame game = new();
        readonly GameWatcher watcher;
        readonly string settings;
        FlightApi flight;
        public readonly WebServer Web;
        public readonly string Data;

        public RoundsKept(string temp)
        {
            Data = Directory.CreateDirectory(Path.Combine(temp, "data")).FullName;
            settings = Path.Combine(temp, "settings.json");
            watcher = game.Watch();
            Web = new WebServer(temp);
            flight = Start();
        }

        FlightApi Start() => new(Web, new Settings(settings), watcher.State, null, game.BindingsDir, null,
            gremlinProfile: () => null, sticks: _ => [], held: _ => false, dataDir: Data);

        /// <summary>The file the rounds are kept in.</summary>
        public string File => Path.Combine(Data, "flight-rounds.json");

        /// <summary>Studio closed and started again on the same data folder.</summary>
        public void Restart()
        {
            flight.Dispose();
            flight = Start();
        }

        public Task<(int Status, JsonElement Json)> Post(JsonNode round) => Post(round.ToJsonString());

        public Task<(int Status, JsonElement Json)> Post(string body) => Ask(Web, "POST", "/rounds", body);

        /// <summary>GET /api/flight/rounds?game=...: the game's rounds as kept, oldest first.</summary>
        public async Task<(int Status, JsonElement Json)> Get(string game)
        {
            WebResponse answer = await Web.Handle(new WebRequest("GET", "/api/flight/rounds", new Dictionary<string, string> { ["game"] = game }, ""));
            return (answer.Status, JsonDocument.Parse(answer.Body).RootElement);
        }

        /// <summary>The scores of a game's rounds as kept, oldest first (Chase the dot's).</summary>
        public async Task<double[]> Scores(string game = "dot2-medium-faOff") =>
            [.. (await Get(game)).Json.GetProperty("rounds").EnumerateArray().Select(r => r.GetProperty("pct").GetDouble())];

        public void Dispose()
        {
            flight.Dispose();
            Web.Dispose();
            watcher.Dispose();
            game.Dispose();
        }
    }

    static JsonObject Curve(double bend) => new() { ["deadLow"] = 4, ["deadHigh"] = 4, ["satX"] = 92, ["satY"] = 100, ["bend"] = bend };

    /// <summary>A round of Chase the dot as the page posts it: its game, its score, the curve set and its curves as flown, and its record.</summary>
    static JsonObject DotRound(double pct, string game = "dot2-medium-faOff", double bend = 35) => new()
    {
        ["game"] = game, ["pct"] = pct, ["set"] = "faOff",
        ["curves"] = new JsonObject { ["pitch"] = Curve(bend), ["yaw"] = Curve(40) },
        ["record"] = new JsonObject { ["frames"] = 1830, ["seconds"] = 30, ["through"] = "curve", ["axes"] = new JsonObject() },
    };

    /// <summary>An attempt of the trainer as its page posts it: its result (good or not, and why) in place of a score.</summary>
    static JsonObject Attempt(bool ok, string why, string game = "chase-1") => new()
    {
        ["game"] = game, ["ok"] = ok, ["why"] = why, ["set"] = "faOff",
        ["curves"] = new JsonObject { ["pitch"] = Curve(35), ["yaw"] = Curve(35), ["roll"] = Curve(20) },
        ["record"] = new JsonObject { ["frames"] = 1220, ["seconds"] = 20, ["through"] = "curve", ["axes"] = new JsonObject() },
    };

    [Fact]
    public async Task The_last_ten_rounds_of_each_game_are_kept_in_the_data_folder_and_a_restart_keeps_them()
    {
        using var kept = new RoundsKept(temp);
        // Eleven rounds of one game, a curve changed before each: the answer is that game's rounds as kept now.
        for (int i = 1; i <= 11; i++)
        {
            (int status, JsonElement answer) = await kept.Post(DotRound(i, bend: 30 + i));
            Assert.Equal(200, status);
            Assert.Equal(("dot2-medium-faOff", Math.Min(i, 10)), (answer.GetProperty("game").GetString(), answer.GetProperty("rounds").GetArrayLength()));
        }
        // Two attempts of the trainer, each in its own game
        Assert.Equal(200, (await kept.Post(Attempt(true, "On target 7.2 of 20 s (6 needed), first on it after 3.1 s"))).Status);
        Assert.Equal(200, (await kept.Post(Attempt(false, "Hit the wall 410 m in, at 220 m/s", "tunnel-2"))).Status);

        async Task Check()
        {
            // the newest ten, oldest first: the first round is gone
            Assert.Equal(new double[] { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, await kept.Scores());
            JsonElement newest = (await kept.Get("dot2-medium-faOff")).Json.GetProperty("rounds")[9];
            // its curve set and the numbers as flown, its record as the page sent it, and when (Studio's clock)
            Assert.Equal("faOff", newest.GetProperty("set").GetString());
            Assert.Equal((41.0, 40.0, 92.0), (newest.GetProperty("curves").GetProperty("pitch").GetProperty("bend").GetDouble(),
                newest.GetProperty("curves").GetProperty("yaw").GetProperty("bend").GetDouble(), newest.GetProperty("curves").GetProperty("pitch").GetProperty("satX").GetDouble()));
            Assert.Equal(1830, newest.GetProperty("record").GetProperty("frames").GetInt32());
            Assert.True(DateTime.TryParse(newest.GetProperty("time").GetString(), System.Globalization.CultureInfo.InvariantCulture, out DateTime when)
                && Math.Abs((DateTime.Now - when).TotalMinutes) < 10, newest.GetProperty("time").GetString());
            // a trainer attempt: its result in place of a score
            JsonElement attempt = Assert.Single((await kept.Get("chase-1")).Json.GetProperty("rounds").EnumerateArray());
            Assert.Equal((true, "On target 7.2 of 20 s (6 needed), first on it after 3.1 s", JsonValueKind.Null),
                (attempt.GetProperty("ok").GetBoolean(), attempt.GetProperty("why").GetString(), attempt.GetProperty("pct").ValueKind));
            Assert.Equal(20, attempt.GetProperty("curves").GetProperty("roll").GetProperty("bend").GetDouble());
            Assert.False(Assert.Single((await kept.Get("tunnel-2")).Json.GetProperty("rounds").EnumerateArray()).GetProperty("ok").GetBoolean());
            // every game its own: another level, the other curve set, another exercise
            Assert.Empty(await kept.Scores("dot2-hard-faOff"));
            Assert.Empty(await kept.Scores("dot2-medium-faOn"));
            Assert.Empty(await kept.Scores("chase-2"));
        }
        await Check();
        kept.Restart();
        await Check();
        // The settings route has no say over them: a page sending "rounds" with its settings changes nothing of them.
        byte[] before = File.ReadAllBytes(kept.File);
        Assert.Equal(200, (await Ask(kept.Web, "POST", "/settings", "{\"rounds\":{},\"Rounds\":null,\"ship\":\"eagle\"}")).Status);
        Assert.Equal(before, File.ReadAllBytes(kept.File));
        await Check();

        // Kept in the data folder Studio was handed, one file and nothing beside it, and nowhere else.
        Assert.Equal([kept.File], Directory.GetFileSystemEntries(kept.Data));
        JsonElement file = JsonDocument.Parse(File.ReadAllText(kept.File)).RootElement;
        Assert.Equal([("chase-1", 1), ("dot2-medium-faOff", 10), ("tunnel-2", 1)], file.EnumerateObject().Select(p => (p.Name, p.Value.GetArrayLength())).OrderBy(p => p.Name).ToArray());
        Assert.False(File.Exists(Path.Combine(Environment.CurrentDirectory, "flight-rounds.json")));
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "flight-rounds.json")));
    }

    [Theory]
    [InlineData("dot2-extreme-faOff", "", null)]     // no such level
    [InlineData("dot5-medium-faOff", "", null)]      // no five-axis dot game: the tunnel is that
    [InlineData("dot2-medium-roll", "", null)]       // no such curve set
    [InlineData("spin-1", "", null)]                 // an exercise the trainer no longer has
    [InlineData("chase-4", "", null)]
    [InlineData("free", "", null)]                   // free flight keeps no score
    [InlineData("", "", null)]
    [InlineData("dot2-medium-faOff", "pct", null)]   // a round of Chase the dot without its score...
    [InlineData("dot2-medium-faOff", "pct", "140")]  // ...more than the whole round...
    [InlineData("dot2-medium-faOff", "pct", "-1")]
    [InlineData("dot2-medium-faOff", "pct", "\"lots\"")]
    [InlineData("chase-1", "ok", null)]              // an attempt without its result
    [InlineData("chase-1", "ok", "\"yes\"")]
    [InlineData("dot2-medium-faOff", "set", "\"roll\"")]   // no such curve set
    [InlineData("dot2-medium-faOff", "set", null)]
    [InlineData("dot2-medium-faOff", "curves", null)]      // no curves...
    [InlineData("dot2-medium-faOff", "curves", "{}")]
    [InlineData("dot2-medium-faOff", "curves", "{\"throttle\":{\"bend\":35}}")]   // ...or none of the turning controls'
    [InlineData("dot2-medium-faOff", "record", null)]      // no record...
    [InlineData("dot2-medium-faOff", "record", "[1]")]     // ...or not one
    [InlineData("dot2-medium-faOff", "record", "\"x\"")]
    public async Task A_round_of_a_game_Studio_does_not_have_or_short_of_what_a_round_is_is_turned_away(string game, string field, string? value)
    {
        using var kept = new RoundsKept(temp);
        JsonObject round = game.StartsWith("dot", StringComparison.Ordinal) ? DotRound(50) : Attempt(true, "On target 7.2 of 20 s (6 needed)");
        round["game"] = game;
        if (field.Length > 0) round[field] = value is null ? null : JsonNode.Parse(value);
        if (value is null && field.Length > 0) round.Remove(field);

        (int status, JsonElement json) = await kept.Post(round);
        Assert.Equal(400, status);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Empty(await kept.Scores());
        Assert.Empty((await kept.Get("chase-1")).Json.GetProperty("rounds").EnumerateArray());
        Assert.Empty(Directory.GetFileSystemEntries(kept.Data));   // nothing written
    }

    [Fact]
    public async Task A_round_over_32_KB_is_turned_away_and_one_at_the_cap_is_kept()
    {
        using var kept = new RoundsKept(temp);
        // A round whose body is exactly so many bytes (Q4's longest record, a four-minute tunnel run, is about 23 KB).
        string Sized(int bytes)
        {
            JsonObject round = DotRound(50);
            round["record"]!["pad"] = "";
            round["record"]!["pad"] = new string('x', bytes - System.Text.Encoding.UTF8.GetByteCount(round.ToJsonString()));
            return round.ToJsonString();
        }
        Assert.Equal(32 * 1024, System.Text.Encoding.UTF8.GetByteCount(Sized(32 * 1024)));
        Assert.Equal(200, (await kept.Post(Sized(32 * 1024))).Status);
        (int status, JsonElement json) = await kept.Post(Sized(32 * 1024 + 1));
        Assert.Equal(400, status);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal(new double[] { 50 }, await kept.Scores());
        // a body that is no round at all, and a game asked for that Studio does not have
        Assert.Equal(400, (await kept.Post("[1]")).Status);
        Assert.Equal(400, (await kept.Post("")).Status);
        Assert.Equal(400, (await kept.Get("dot2-extreme-faOff")).Status);
        Assert.Equal(400, (await kept.Get("")).Status);
        Assert.Equal(new double[] { 50 }, await kept.Scores());
    }

    [Fact]
    public async Task A_failed_write_leaves_the_rounds_kept_before_whole()
    {
        using var kept = new RoundsKept(temp);
        for (int i = 1; i <= 10; i++) Assert.Equal(200, (await kept.Post(DotRound(i))).Status);
        byte[] before = File.ReadAllBytes(kept.File);
        async Task TheTenAsTheyWere((int Status, JsonElement Json) answer)
        {
            Assert.Equal(500, answer.Status);
            Assert.False(answer.Json.GetProperty("ok").GetBoolean());
            Assert.Equal(before, File.ReadAllBytes(kept.File));   // the file whole, as it was
            Assert.Equal(new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, await kept.Scores());   // and Studio still has the ten it had
        }

        // The new file cannot be written beside the old one (a folder in its way here; a full disk or an antivirus does the same)...
        Directory.CreateDirectory(kept.File + ".tmp");
        await TheTenAsTheyWere(await kept.Post(DotRound(11)));
        Directory.Delete(kept.File + ".tmp");
        // ...or it cannot take the old one's place: another program has the file open.
        using (new FileStream(kept.File, FileMode.Open, FileAccess.Read, FileShare.Read))
            await TheTenAsTheyWere(await kept.Post(DotRound(11)));
        Assert.Equal([kept.File], Directory.GetFileSystemEntries(kept.Data));   // nothing left beside it

        // The way clear again, the next round is kept.
        Assert.Equal(200, (await kept.Post(DotRound(11))).Status);
        Assert.Equal(new double[] { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, await kept.Scores());
    }

    [Fact]
    public async Task A_rounds_file_edited_by_hand_or_unreadable_never_stops_the_rounds()
    {
        using var kept = new RoundsKept(temp);
        JsonObject Stored(double pct) => new()
        {
            ["time"] = "2026-10-21T21:05:00", ["pct"] = pct, ["ok"] = null, ["why"] = null, ["set"] = "faOff",
            ["curves"] = new JsonObject { ["pitch"] = Curve(35), ["yaw"] = Curve(40) }, ["record"] = new JsonObject { ["frames"] = 1830 },
        };
        // Typed into the file by hand: twelve rounds of one game and an empty entry among them, a game Studio does not have, a null.
        var twelve = new JsonArray(Enumerable.Range(1, 12).Select(i => (JsonNode?)Stored(i)).ToArray());
        twelve.Insert(5, null);
        File.WriteAllText(kept.File, new JsonObject { ["dot2-easy-faOff"] = twelve, ["spin-1"] = new JsonArray(Stored(5)), ["chase-1"] = null }.ToJsonString());
        kept.Restart();
        Assert.Equal(new double[] { 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, await kept.Scores("dot2-easy-faOff"));
        Assert.Equal(200, (await kept.Get("chase-1")).Status);
        Assert.Empty((await kept.Get("chase-1")).Json.GetProperty("rounds").EnumerateArray());

        // A file Studio cannot read is kept aside for the owner to send in, and the rounds start again.
        File.WriteAllText(kept.File, "{ not json");
        kept.Restart();
        Assert.Empty(await kept.Scores("dot2-easy-faOff"));
        Assert.Equal("{ not json", File.ReadAllText(kept.File + ".unreadable"));
        Assert.Equal(200, (await kept.Post(DotRound(64, "dot2-easy-faOff"))).Status);
        Assert.Equal(new double[] { 64 }, await kept.Scores("dot2-easy-faOff"));
    }

    /// <summary>
    /// The owner's bar for the stick readings: at least 55 a second, never more than 25 ms apart, and each one's time later than the
    /// last (the stick check drops a reading whose time repeats). Windows' tick count moves in steps of about 15.6 ms, so readings
    /// timed by it show gaps of 31 ms and repeated times. A busy test machine can hold up any thread now and then, so the reader has
    /// three tries of a second and a half to show it keeps the beat.
    /// </summary>
    [Fact]
    public void Readings_come_about_sixty_a_second_evenly_and_their_times_always_go_up()
    {
        var tries = new List<string>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var sent = new System.Collections.Concurrent.ConcurrentQueue<(long At, long Time)>();
            using (var reader = new FlightReader(_ => [], _ => false, frame => sent.Enqueue((clock.ElapsedMilliseconds, frame.Time))))
            {
                reader.Stream();
                reader.Start();
                Thread.Sleep(1800);
            }
            // a second and a half, after the reader has had 200 ms to start
            long[] times = [.. sent.Where(s => s.At >= 200 && s.At < 1700).Select(s => s.Time)];
            long[] gaps = [.. times.Skip(1).Select((time, i) => time - times[i])];
            double perSecond = times.Length / 1.5;
            long longest = gaps.Length == 0 ? long.MaxValue : gaps.Max();
            int repeated = gaps.Count(gap => gap <= 0);
            if (perSecond >= 55 && longest <= 25 && repeated == 0) return;
            tries.Add($"{perSecond:0.#} a second, longest gap {longest} ms, {repeated} times not later than the one before");
        }
        Assert.Fail("The readings missed the bar (55 a second, no gap over 25 ms, times always later) in all three tries: " + string.Join("; ", tries));
    }

    /// <summary>
    /// Studio's window thread also answers every page and draws the overlay, so the readings must not wait for it: while it is
    /// busy for a quarter of a second the stream to the pages keeps coming. What touches the window still happens on that thread:
    /// <see cref="FlightReader.Read"/> (the flight box draws itself) and <see cref="FlightReader.OverlayWants"/> (whether it is on screen).
    /// </summary>
    [Fact]
    public void The_stream_keeps_its_beat_while_the_window_thread_is_busy_and_Read_comes_on_that_thread() => OverlayTests.OnWindowThread(() =>
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
        int window = Environment.CurrentManagedThreadId;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var sent = new System.Collections.Concurrent.ConcurrentQueue<long>();   // when each reading went to the pages
        var readOn = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var askedOn = new System.Collections.Concurrent.ConcurrentQueue<int>();
        using var reader = new FlightReader(_ => [], _ => false, _ => sent.Enqueue(clock.ElapsedMilliseconds));
        reader.Read += _ => readOn.Enqueue(Environment.CurrentManagedThreadId);
        reader.OverlayWants = () =>
        {
            askedOn.Enqueue(Environment.CurrentManagedThreadId);
            return true;   // the flight box is on screen
        };
        reader.Stream();
        reader.Start();

        long busyFrom = 0, busyTo = 0;
        var done = new System.Windows.Threading.DispatcherFrame();
        var busy = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(500), System.Windows.Threading.DispatcherPriority.Normal, (timer, _) =>
        {
            ((System.Windows.Threading.DispatcherTimer)timer!).Stop();
            busyFrom = clock.ElapsedMilliseconds;
            Thread.Sleep(250);   // a slow page answer, say
            busyTo = clock.ElapsedMilliseconds;
        }, dispatcher);
        var end = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(1200), System.Windows.Threading.DispatcherPriority.Normal, (timer, _) =>
        {
            ((System.Windows.Threading.DispatcherTimer)timer!).Stop();
            done.Continue = false;
        }, dispatcher);
        System.Windows.Threading.Dispatcher.PushFrame(done);

        Assert.True(busyTo > busyFrom);
        int whileBusy = sent.Count(at => at > busyFrom && at < busyTo);
        Assert.True(whileBusy >= 8, $"{whileBusy} readings went to the pages in the {busyTo - busyFrom} ms the window thread was busy (want 8 or more)");
        Assert.NotEmpty(readOn);
        Assert.All(readOn, thread => Assert.Equal(window, thread));
        Assert.NotEmpty(askedOn);
        Assert.All(askedOn, thread => Assert.Equal(window, thread));
    });

    // ------------------------------------------------------------ the stick shaper: Flight Assist's two curves through Gimbalyn

    [Fact]
    public void The_stick_shaper_is_off_until_turned_on_and_the_curves_follow_Flight_Assist_by_default()
    {
        var fresh = new FlightSettings();
        Assert.Equal((false, true), (fresh.ShaperOn, fresh.SwitchCurvesWithFa));
        JsonElement asSent = JsonSerializer.SerializeToElement(fresh, Settings.Json);
        Assert.Equal((false, true), (asSent.GetProperty("shaperOn").GetBoolean(), asSent.GetProperty("switchCurvesWithFa").GetBoolean()));
        FlightSettings kept = new Settings(Path.Combine(temp, "settings.json")).Get<FlightSettings>("flight");   // a file with none of it
        Assert.Equal((false, true), (kept.ShaperOn, kept.SwitchCurvesWithFa));
    }

    [Fact]
    public void The_stick_behind_a_turning_control_is_found_for_Gimbalyn_by_its_numbers_and_Windows_letters()
    {
        FlightBinds binds = OwnersBinds();
        IReadOnlyList<GremlinAxis> routed = Gremlin.Axes(XDocument.Parse(ProfileText()));
        StickState vjoy = Stick("1234:BEAD", "vJoy Device", 0, ("X", 0.1), ("Y", -0.2), ("Z", 0), ("R", 0.3));
        StickState logitech = Stick("046D:C215", "Logitech Extreme 3D", 0, ("X", 0.15), ("Y", -0.3), ("Z", -1), ("R", 0.45));

        // Through Gremlin (found by the name it keeps): the Logitech, the twist being Windows' R
        var through = new FlightControls(binds, routed, new FlightSettings());
        Assert.Equal(("046D:C215", "Y"), through.RealSource("pitch", [vjoy, logitech]));
        Assert.Equal(("046D:C215", "X"), through.RealSource("roll", [vjoy, logitech]));
        Assert.Equal(("046D:C215", "R"), through.RealSource("yaw", [vjoy, logitech]));
        Assert.Null(through.RealSource("throttle", [vjoy, logitech]));   // only the turning controls
        Assert.Null(through.RealSource("pitch", [vjoy]));                 // its stick is not plugged in
        // With nothing to say which stick feeds vJoy: not known (vJoy itself never stands in for it)
        Assert.Null(new FlightControls(binds, [], new FlightSettings()).RealSource("pitch", [vjoy, logitech]));
        // As the player picked it with Detect, and corrected the letter: that
        var picked = new FlightControls(binds, [], new FlightSettings { Sources = new() { ["yaw"] = "046D:C215 RZ" }, AxisFix = new() { ["046D:C215 RZ"] = "Z" } });
        Assert.Equal(("046D:C215", "Z"), picked.RealSource("yaw", [vjoy, logitech]));
    }

    /// <summary>The flight service with the stick shaper's Gimbalyn made up (a stand-in for the process and its pipe), the owner's bindings and Gremlin profile, and a Logitech stick.</summary>
    sealed class ShaperRig : IDisposable
    {
        readonly PracticeGame game = new();
        readonly GameWatcher watcher;
        readonly string status;
        public readonly WebServer Web;
        public readonly FlightApi Flight;
        public readonly GimbalynTests.FakeGimbalyn Gimbalyn = new() { Running = false };

        public ShaperRig(string temp, bool injected = true)
        {
            game.Copy("Custom.4.2.binds", game.BindingsDir);
            game.Copy("StartPreset.4.start", game.BindingsDir);
            status = Path.Combine(game.JournalDir, "Status.json");
            FlightAssist(off: false);
            watcher = game.Watch();
            watcher.Poll();
            string profile = Path.Combine(temp, "gremlin.xml");
            File.WriteAllText(profile, ProfileText());
            Web = new WebServer(temp);
            Flight = new FlightApi(Web, new Settings(Path.Combine(temp, "settings.json")), watcher.State, null, game.BindingsDir, null,
                gremlinProfile: () => profile, held: _ => false,
                sticks: _ => [Stick("1234:BEAD", "vJoy Device", 0, ("X", 0.1), ("Y", -0.2), ("Z", 0), ("R", 0.3)), Stick("046D:C215", "Logitech Extreme 3D", 0, ("X", 0.15), ("Y", -0.3), ("Z", -1), ("R", 0.45))],
                shaper: injected ? GimbalynTests.ShaperFor(Gimbalyn) : null);
        }

        // (the two differ in length as well, so the watcher sees the second however soon it comes)
        void Write(bool off) => game.Write(status, "{ \"timestamp\":\"2026-10-19T12:00:00Z\", \"event\":\"Status\", \"Flags\":" + ((1 << 24) | (off ? 1 << 5 : 0)) + (off ? ", \"Cargo\":1.0" : "") + " }\r\n");

        /// <summary>Flight Assist switched in the game; Studio sees it as it does in the app.</summary>
        public void FlightAssist(bool off, bool seen = false)
        {
            Write(off);
            if (!seen) return;
            watcher.Poll();
            Flight.CheckFlightAssist();
        }

        /// <summary>Lets the looks at the shaper finish (the one on its way, then one that sees everything as it is now).</summary>
        public async Task Settle()
        {
            await Flight.KeepShaper();
            await Flight.KeepShaper();
        }

        public async Task<JsonElement> Told() => (await Ask(Web, "GET", "")).Json.GetProperty("shaper");

        /// <summary>
        /// What the shaper's page is told, asked again and again until <paramref name="done"/> says so (at most three seconds): for what
        /// Studio does by itself when a switch is turned or Flight Assist changes, without a look being asked for.
        /// </summary>
        public async Task<JsonElement> UntilTold(Func<JsonElement, bool> done)
        {
            JsonElement told = await Told();
            for (int waited = 0; waited < 3000 && !done(told); waited += 20)
            {
                await Task.Delay(20);
                told = await Told();
            }
            Assert.True(done(told), "the shaper was not told what was awaited: " + told);
            await Settle();   // (a look still on its way must be over before the stand-in's list is read)
            return told;
        }

        public string[] Said => [.. Gimbalyn.Commands.Where(c => c != "status")];

        public void Dispose()
        {
            Flight.Dispose();
            Web.Dispose();
            watcher.Dispose();
            game.Dispose();
        }
    }

    [Fact]
    public async Task Turned_on_the_shaper_starts_Gimbalyn_with_both_curve_sets_and_follows_Flight_Assist_until_turned_off()
    {
        using var rig = new ShaperRig(temp);
        await rig.Settle();
        Assert.Empty(rig.Gimbalyn.Requests);   // off: Gimbalyn is never started or spoken to
        Assert.Equal(0, rig.Gimbalyn.Launches);
        JsonElement told = await rig.Told();
        Assert.Equal((false, true, false), (told.GetProperty("on").GetBoolean(), told.GetProperty("switching").GetBoolean(), told.GetProperty("running").GetBoolean()));

        // (no look is asked for here: turning the switch on is enough)
        Assert.Equal(200, (await Ask(rig.Web, "POST", "/settings", "{\"shaperOn\":true}")).Status);
        await rig.UntilTold(t => t.GetProperty("running").GetBoolean());
        Assert.Equal(1, rig.Gimbalyn.Launches);
        Assert.Equal(["profile", "start"], rig.Said);
        JsonNode profile = rig.Gimbalyn.Requests[0]["profile"]!;
        Assert.Equal(["046D:C215 Y>Y", "046D:C215 X>X", "046D:C215 R>RZ"], profile["routes"]!.AsArray().Select(r => $"{(string?)r!["device"]} {(string?)r["axis"]}>{(string?)r["target"]}"));
        Assert.Equal(("FA On", "Key_Z"), (profile["mode"]!.GetValue<string>(), profile["modeKey"]!.GetValue<string>()));
        // the Flight Assist on curves are the player's Gremlin curves (full stick gives 68), the off ones start from them: full stick for full thrust
        Assert.InRange(profile["modes"]!["FA On"]!["Y"]!["satY"]!.GetValue<double>(), 67.5, 68.5);
        Assert.Equal(100, profile["modes"]!["FA Off"]!["Y"]!["satY"]!.GetValue<double>());
        told = await rig.Told();
        Assert.Equal((true, true, "running", "FA On"), (told.GetProperty("on").GetBoolean(), told.GetProperty("running").GetBoolean(), told.GetProperty("state").GetString(), told.GetProperty("mode").GetString()));

        // Flight Assist goes off in the game: Gimbalyn is told the other mode, once; it comes back on: told again
        rig.FlightAssist(off: true, seen: true);
        await rig.UntilTold(t => t.GetProperty("mode").GetString() == "FA Off");
        Assert.Equal(["profile", "start", "mode"], rig.Said);
        Assert.Equal("FA Off", (string?)rig.Gimbalyn.Requests.Last(r => (string?)r["command"] == "mode")["mode"]);
        rig.FlightAssist(off: false, seen: true);
        await rig.UntilTold(t => t.GetProperty("mode").GetString() == "FA On");
        Assert.Equal(["profile", "start", "mode", "mode"], rig.Said);

        // turned off: Gimbalyn is let go once, then left alone
        Assert.Equal(200, (await Ask(rig.Web, "POST", "/settings", "{\"shaperOn\":false}")).Status);
        await rig.UntilTold(t => t.GetProperty("state").ValueKind == JsonValueKind.Null);
        await rig.Settle();
        Assert.Equal(["profile", "start", "mode", "mode", "quit"], rig.Said);
        told = await rig.Told();
        Assert.Equal((false, false, JsonValueKind.Null), (told.GetProperty("on").GetBoolean(), told.GetProperty("running").GetBoolean(), told.GetProperty("state").ValueKind));
    }

    [Fact]
    public async Task With_switching_off_Gimbalyn_gets_only_the_Flight_Assist_on_curves_and_no_mode()
    {
        using var rig = new ShaperRig(temp);
        rig.FlightAssist(off: true, seen: true);   // even with Flight Assist off in the game
        Assert.Equal(200, (await Ask(rig.Web, "POST", "/settings", "{\"shaperOn\":true,\"switchCurvesWithFa\":false}")).Status);
        await rig.Settle();

        JsonNode profile = rig.Gimbalyn.Requests[0]["profile"]!;
        Assert.Equal(["FA On"], profile["modes"]!.AsObject().Select(m => m.Key));
        Assert.Equal("FA On", profile["mode"]!.GetValue<string>());
        Assert.Null(profile["modeKey"]);
        Assert.DoesNotContain("mode", rig.Said);
    }

    [Fact]
    public async Task A_vJoy_held_by_another_program_is_told_in_words_and_the_shaper_does_not_run()
    {
        using var rig = new ShaperRig(temp);
        rig.Gimbalyn.StateAnswer = "busy";
        rig.Gimbalyn.Problem = "vJoy device 1 is held by joystick_gremlin now.";

        Assert.Equal(200, (await Ask(rig.Web, "POST", "/settings", "{\"shaperOn\":true}")).Status);
        await rig.Settle();

        JsonElement told = await rig.Told();
        Assert.Equal((false, "busy"), (told.GetProperty("running").GetBoolean(), told.GetProperty("state").GetString()));
        Assert.Contains("joystick_gremlin", told.GetProperty("problem").GetString());
    }

    [Fact]
    public async Task A_practice_copy_never_starts_Gimbalyn_and_says_so()
    {
        using var rig = new ShaperRig(temp, injected: false);   // no data folder for it to reach Gimbalyn through: as a practice copy

        Assert.Equal(200, (await Ask(rig.Web, "POST", "/settings", "{\"shaperOn\":true}")).Status);
        await rig.Settle();

        JsonElement told = await rig.Told();
        Assert.Equal((true, false), (told.GetProperty("practice").GetBoolean(), told.GetProperty("running").GetBoolean()));
        Assert.Contains("practice", told.GetProperty("problem").GetString());
        Assert.Empty(rig.Gimbalyn.Requests);
    }

    // ------------------------------------------------------------ a game pad instead of a stick

    /// <summary>
    /// Elite's Xbox preset as far as flight goes, as the game ships it (ConsoleX360.binds): the pad is Device="GamePad" and its
    /// sticks "GamePad_LStickX" and so on, pitch NOT inverted (XInput's up is positive); Flight Assist on a bumper with B held.
    /// The throttle on a trigger is added here (the preset has none).
    /// </summary>
    static FlightBinds PadBinds() => FlightBinds.Read("ConsoleX360", "ConsoleX360.binds", XElement.Parse("""
        <Root>
          <YawAxisRaw><Binding Device="GamePad" Key="GamePad_RStickX" /><Inverted Value="0" /><Deadzone Value="DefaultPlatformDeadzone" /></YawAxisRaw>
          <RollAxisRaw><Binding Device="GamePad" Key="GamePad_LStickX" /><Inverted Value="0" /><Deadzone Value="DefaultPlatformDeadzone" /></RollAxisRaw>
          <PitchAxisRaw><Binding Device="GamePad" Key="GamePad_LStickY" /><Inverted Value="0" /><Deadzone Value="DefaultPlatformDeadzone" /></PitchAxisRaw>
          <VerticalThrustRaw><Binding Device="GamePad" Key="GamePad_RStickY" /><Inverted Value="0" /><Deadzone Value="DefaultPlatformDeadzone" /></VerticalThrustRaw>
          <ThrottleAxis><Binding Device="GamePad" Key="GamePad_RTrigger" /><Inverted Value="0" /><Deadzone Value="0.0" /></ThrottleAxis>
          <ToggleFlightAssist><Primary Device="GamePad" Key="GamePad_RBumper"><Modifier Device="GamePad" Key="GamePad_FaceRight" /></Primary><Secondary Device="{NoDevice}" Key="" /><ToggleOn Value="1" /></ToggleFlightAssist>
        </Root>
        """), deviceList: null);

    [Fact]
    public void A_game_pads_sticks_are_flight_axes_in_the_bindings()
    {
        FlightBinds binds = PadBinds();

        Assert.Equal(new FlightAxis("GamePad", "LStickY", false, 0), binds.Axes["pitch"]);
        Assert.Equal(new FlightAxis("GamePad", "LStickX", false, 0), binds.Axes["roll"]);
        Assert.Equal(new FlightAxis("GamePad", "RStickX", false, 0), binds.Axes["yaw"]);
        Assert.Equal("RStickY", binds.Axes["vertical"].Axis);
        Assert.DoesNotContain("throttle", binds.Axes.Keys);   // a trigger is no stick axis (its reading is one for both triggers)
        Assert.Equal(["045E:028E"], binds.Devices["GamePad"]);
        // the older Xbox 360 preset names them "Pad_LStickX", on a device it calls "XB360 Pad"
        FlightBinds older = FlightBinds.Read("ControlPad", "ControlPad.binds",
            XElement.Parse("""<Root><RollAxisRaw><Binding Device="XB360 Pad" Key="Pad_LStickX" /><Inverted Value="0" /><Deadzone Value="0.15" /></RollAxisRaw></Root>"""), deviceList: null);
        Assert.Equal(new FlightAxis("XB360 Pad", "LStickX", false, 0.15), older.Axes["roll"]);
    }

    [Fact]
    public void A_game_pads_sticks_are_read_as_pitch_roll_and_yaw_by_Windows_letters()
    {
        StickState pad = Stick("045E:028E", "Controller (XBOX 360 For Windows)", 0, ("X", 0.2), ("Y", -0.4), ("Z", 0), ("R", 0.6), ("U", -0.7));
        StickState logitech = Stick("046D:C215", "Logitech Extreme 3D", 0, ("X", 0.9), ("Y", 0.9), ("Z", 0.9), ("R", 0.9));
        var controls = new FlightControls(PadBinds(), [], new FlightSettings());

        ControlsFrame now = controls.Take(new FlightFrame(1, [logitech, pad], []));

        // left stick: X and Y; the right stick sideways is U and up and down is R; the pad is what the game reads, so it is the real stick too.
        // Each Y down positive, as Windows gives it; the game's invert for it turned over, as Elite reads the pad up positive (XInput's)
        Assert.Equal(new ControlValue(-0.4, -0.4, true), now.Controls["pitch"]);
        Assert.Equal(new ControlValue(0.2, 0.2, false), now.Controls["roll"]);
        Assert.Equal(new ControlValue(-0.7, -0.7, false), now.Controls["yaw"]);
        Assert.Equal(0.6, now.Controls["vertical"].Game);
        Assert.False(AsSent(now.Controls["yaw"]).GetProperty("realUnknown").GetBoolean());
        // the player's fix, as Detect makes it (it names the game's axis): the right stick sideways is Windows' Z on this pad
        var fixedOne = new FlightControls(PadBinds(), [], new FlightSettings { AxisFix = new() { ["045E:028E RStickX"] = "Z" } }.Cleaned());
        Assert.Equal(0, fixedOne.Take(new FlightFrame(2, [pad], [])).Controls["yaw"].Real);
        Assert.Equal(("045E:028E", "Z"), fixedOne.RealSource("yaw", [pad]));
    }

    [Fact]
    public void A_pad_alone_is_found_though_the_game_s_list_of_pads_is_out_of_reach()
    {
        // an Xbox Series pad: its numbers are in the game's own list, which Studio has not got here, and not in Studio's short one
        StickState pad = Stick("045E:0B12", "Controller (Xbox Series X)", 0, ("X", 0.2), ("Y", -0.4), ("Z", 0), ("R", 0.6), ("U", -0.7));

        ControlsFrame now = new FlightControls(PadBinds(), [], new FlightSettings()).Take(new FlightFrame(1, [pad], []));

        Assert.Equal(-0.7, now.Controls["yaw"].Real);
        Assert.Equal(-0.4, now.Controls["pitch"].Real);
    }

    [Fact]
    public void A_correction_of_a_pad_stick_is_kept_in_the_settings()
    {
        FlightSettings clean = new FlightSettings
        {
            AxisFix = new() { ["045E:028E RStickX"] = "Z", ["045E:028E Nonsense"] = "Z" },
            Sources = new() { ["yaw"] = "045E:028E RStickX", ["pitch"] = "045E:028E Nonsense" },
        }.Cleaned();

        Assert.Equal(new Dictionary<string, string> { ["045E:028E RStickX"] = "Z" }, clean.AxisFix);
        Assert.Equal(new Dictionary<string, string> { ["yaw"] = "045E:028E RStickX" }, clean.Sources);
    }

    // ------------------------------------------------------------ a game pad as Elite reads it (XInput)

    [Fact]
    public void An_XInput_reading_has_the_game_s_names_with_down_and_right_positive_and_triggers_from_0_to_1()
    {
        // A held, the right shoulder, the cross's up; the left trigger full, the right one under XInput's threshold (30 of 255)
        StickState pad = XInputPads.State(0, new PadReading(0x1000 | 0x0200 | 0x0001, 255, 20, 32767, 32767, -32768, -16384));

        Assert.Equal("XInput#1", pad.Device);
        Assert.Equal("Xbox-style controller", pad.Name);
        Assert.Equal(1, pad.Axes["LStickX"]);
        Assert.Equal(-1, pad.Axes["LStickY"]);                   // pushed up: XInput's up is positive; Windows' joystick calls (and Studio) have it negative
        Assert.Equal(-1, pad.Axes["RStickX"]);
        Assert.Equal(16384 / 32768.0, pad.Axes["RStickY"], 9);   // pulled half way down
        Assert.Equal(1, pad.Axes["LTrigger"]);
        Assert.Equal(20 / 255.0, pad.Axes["RTrigger"], 9);
        Assert.Equal((1UL << 0) | (1UL << 5) | (1UL << 10) | (1UL << 14), pad.Down);
        Assert.Equal("Xbox-style controller 3", XInputPads.State(2, default).Name);
        Assert.Equal([0.0, 0.0], [XInputPads.Stick(0), XInputPads.State(0, default).Axes["LStickY"]]);
    }

    [Theory]
    [InlineData("GamePad_FaceDown", 0)]
    [InlineData("GamePad_FaceRight", 1)]
    [InlineData("Pad_RBumper", 5)]
    [InlineData("GamePad_Back", 6)]
    [InlineData("GamePad_Start", 7)]
    [InlineData("GamePad_RThumb", 9)]
    [InlineData("GamePad_DPadLeft", 13)]
    [InlineData("GamePad_LTrigger", 14)]
    [InlineData("Pos_GamePad_RTrigger", 15)]
    [InlineData("Pos_Pad_LTrigger", 14)]
    [InlineData("Pad_A", 0)]
    [InlineData("Pad_B", 1)]
    [InlineData("Pad_X", 2)]
    [InlineData("Pad_Y", 3)]
    [InlineData("GamePad_LStickX", null)]
    [InlineData("Joy_1", null)]
    [InlineData("Key_A", null)]
    public void A_pad_button_s_name_is_its_place_in_the_reading(string key, int? bit) => Assert.Equal(bit, XInputPads.Bit(key));

    [Fact]
    public void Each_button_lands_on_the_place_its_name_gives()
    {
        // every XInput mask and both triggers, one at a time, against the name the game gives that button
        (ushort Mask, string Name)[] buttons =
        [
            (0x1000, "GamePad_FaceDown"), (0x2000, "GamePad_FaceRight"), (0x4000, "GamePad_FaceLeft"), (0x8000, "GamePad_FaceUp"),
            (0x0100, "GamePad_LBumper"), (0x0200, "GamePad_RBumper"), (0x0020, "GamePad_Back"), (0x0010, "GamePad_Start"),
            (0x0040, "GamePad_LThumb"), (0x0080, "GamePad_RThumb"), (0x0001, "GamePad_DPadUp"), (0x0008, "GamePad_DPadRight"),
            (0x0002, "GamePad_DPadDown"), (0x0004, "GamePad_DPadLeft"),
        ];
        foreach ((ushort mask, string name) in buttons)
            Assert.Equal(1UL << XInputPads.Bit(name)!.Value, XInputPads.State(0, new PadReading(mask, 0, 0, 0, 0, 0, 0)).Down);
        Assert.Equal(1UL << XInputPads.Bit("GamePad_LTrigger")!.Value, XInputPads.State(0, new PadReading(0, 31, 30, 0, 0, 0, 0)).Down);
        Assert.Equal(1UL << XInputPads.Bit("GamePad_RTrigger")!.Value, XInputPads.State(0, new PadReading(0, 30, 31, 0, 0, 0, 0)).Down);
    }

    [Fact]
    public void Empty_slots_are_asked_only_when_looking_for_new_pads()
    {
        var asked = new List<uint>();
        bool plugged = true;
        var pads = new XInputPads(slot => { asked.Add(slot); return slot == 1 && plugged ? new PadReading(0, 0, 0, 100, 0, 0, 0) : null; });

        Assert.Equal(["XInput#2"], pads.Read(lookForNew: true).Select(p => p.Device));
        Assert.Equal([0u, 1, 2, 3], asked);
        asked.Clear();
        Assert.Equal(["XInput#2"], pads.Read(lookForNew: false).Select(p => p.Device));
        Assert.Equal([1u], asked);   // only where a pad was
        plugged = false;
        asked.Clear();
        Assert.Empty(pads.Read(lookForNew: false));
        Assert.Empty(pads.Read(lookForNew: false));
        Assert.Equal([1u], asked);   // gone: not asked again until the next look for new ones
    }

    [Fact]
    public void XInput_itself_answers_on_this_computer_without_failing()
    {
        // whatever is plugged in here: no pad, or pads named by their slot
        IReadOnlyList<StickState> found = new XInputPads().Read(lookForNew: true);
        Assert.All(found, pad => Assert.Matches("^XInput#[1-4]$", pad.Device));
        Assert.All(found, pad => Assert.Equal(["LStickX", "LStickY", "RStickX", "RStickY", "LTrigger", "RTrigger"], pad.Axes.Keys));
    }

    [Fact]
    public void A_pad_Elite_reads_is_read_from_XInput_by_the_game_s_names_before_its_copy_in_Windows_joystick_list()
    {
        // the same pad twice: as Windows' joystick calls show it (its numbers are in the bindings, its letters a guess), and as XInput gives it
        StickState copy = Stick("045E:028E", "Controller (XBOX 360 For Windows)", 0, ("X", 0.9), ("Y", 0.9), ("Z", 0.9), ("R", 0.9), ("U", 0.9));
        StickState pad = XInputPads.State(0, new PadReading(0, 0, 0, 6554, -13107, -26214, 0));
        var controls = new FlightControls(PadBinds(), [], new FlightSettings { AxisFix = new() { ["045E:028E RStickX"] = "Z" } }.Cleaned());

        ControlsFrame now = controls.Take(new FlightFrame(1, [copy, pad], []));

        Assert.Equal(new ControlValue(pad.Axes["LStickY"], pad.Axes["LStickY"], true), now.Controls["pitch"]);
        Assert.Equal(new ControlValue(pad.Axes["LStickX"], pad.Axes["LStickX"], false), now.Controls["roll"]);
        Assert.Equal(new ControlValue(pad.Axes["RStickX"], pad.Axes["RStickX"], false), now.Controls["yaw"]);   // the fix is for the copy
        Assert.Equal(("XInput#1", "RStickX"), controls.RealSource("yaw", [copy, pad]));
        // without XInput's pad (a PlayStation pad, or XInput not there) the copy is read as before
        Assert.Equal(0.9, controls.Take(new FlightFrame(2, [copy], [])).Controls["yaw"].Game);
    }

    [Fact]
    public void A_pad_pushed_or_pulled_turns_the_ship_the_way_Elite_turns_it_from_XInput_and_from_its_copy_alike()
    {
        // Elite's own presets: pitch on the pad's "LStickY" not inverted, on a stick's "Joy_YAxis" (down positive) inverted. So the
        // game takes XInput's value as it comes (up positive), and Studio's reading, down positive with the invert turned over,
        // must come to the same: the invert's sign times the reading is the game's.
        var controls = new FlightControls(PadBinds(), [], new FlightSettings());
        StickState back = XInputPads.State(0, new PadReading(0, 0, 0, 0, -32768, 0, 32767));                       // left stick pulled back, right pushed up
        StickState copyBack = Stick("045E:028E", "Controller (XBOX 360 For Windows)", 0, ("X", 0), ("Y", 1), ("R", -1), ("U", 0));   // the same, as Windows shows it
        static double Game(ControlValue c) => (c.Invert ? -1 : 1) * c.Game!.Value;

        foreach (StickState pad in new[] { back, copyBack })
        {
            ControlsFrame now = controls.Take(new FlightFrame(1, [pad], []));
            Assert.Equal(new ControlValue(1, 1, true), now.Controls["pitch"]);   // drawn low, as a stick pulled back is
            Assert.Equal(-1, Game(now.Controls["pitch"]));                       // what Elite gets: XInput's -1, not inverted
            Assert.Equal(1, Game(now.Controls["vertical"]));                     // XInput's +1
        }
        // inverted in the game, the other way round
        var inverted = new FlightControls(FlightBinds.Read("ConsoleX360", "ConsoleX360.binds", XElement.Parse("""
            <Root><PitchAxisRaw><Binding Device="GamePad" Key="GamePad_LStickY" /><Inverted Value="1" /><Deadzone Value="0" /></PitchAxisRaw></Root>
            """), deviceList: null), [], new FlightSettings());
        Assert.Equal(new ControlValue(1, 1, false), inverted.Take(new FlightFrame(1, [back], [])).Controls["pitch"]);
    }

    [Fact]
    public void A_stick_Windows_has_no_name_for_is_not_taken_for_a_pad_that_is_not_plugged_in()
    {
        StickState unnamed = Stick("231D:0200", "controller 231D:0200", 0, ("X", 0.3), ("Y", 0.3), ("R", 0.3), ("U", 0.3));
        StickState x52 = Stick("06A3:0762", "Saitek X52 Flight Controller", 0, ("X", 0.3), ("Y", 0.3), ("R", 0.3), ("U", 0.3));
        StickState vjoy = Stick("1234:BEAD", "vJoy Device", 0, ("X", 0.5));

        foreach (StickState stick in new[] { unnamed, x52 })
        {
            ControlsFrame now = new FlightControls(PadBinds(), [], new FlightSettings()).Take(new FlightFrame(1, [stick, vjoy], []));
            Assert.All(new[] { "pitch", "roll", "yaw", "vertical" }, c => Assert.Null(now.Controls[c].Game));
        }
        // a pad Windows does name still is
        foreach (string name in new[] { "Controller (Xbox Series X)", "Wireless Controller", "Generic USB Gamepad" })
        {
            StickState named = Stick("1A2B:3C4D", name, 0, ("X", 0.3), ("Y", 0.3), ("R", 0.3), ("U", 0.3));
            Assert.Equal(0.3, new FlightControls(PadBinds(), [], new FlightSettings()).Take(new FlightFrame(1, [named, vjoy], [])).Controls["roll"].Game);
        }
    }

    [Fact]
    public void A_stick_named_by_numbers_or_by_Gremlin_is_never_XInput_s_copy_of_a_pad()
    {
        StickState copy = Stick("045E:028E", "Controller (XBOX 360 For Windows)", 0, ("X", 0.4));
        StickState pad = XInputPads.State(0, new PadReading(0, 0, 0, 0, 0, 0, 0));

        // Gremlin's name for the pad differs from Windows' (no name match): "the only real stick there is" is still the copy
        Assert.Equal(copy, FlightControls.Find([pad, copy], new AxisRef([], "Xbox Wireless Controller", "X")));
        Assert.Null(FlightControls.Find([pad], new AxisRef([], "Xbox-style controller", "X")));
        Assert.Null(FlightControls.Find([pad], new AxisRef(["XInput#1"], null, "X")));
    }

    [Fact]
    public void A_pad_stick_Detect_named_before_XInput_was_read_is_read_from_XInput_now()
    {
        // saved while Studio read only Windows' joystick calls: yaw from the pad by its numbers (the copy goes quiet while Elite is in front)
        var controls = new FlightControls(PadBinds(), [], new FlightSettings { Sources = new() { ["yaw"] = "045E:028E RStickX" } }.Cleaned());
        StickState copy = Stick("045E:028E", "Controller (XBOX 360 For Windows)", 0, ("X", 0), ("Y", 0), ("R", 0), ("U", 0));
        StickState pad = XInputPads.State(0, new PadReading(0, 0, 0, 0, 0, 16384, 0));

        ControlsFrame now = controls.Take(new FlightFrame(1, [copy, pad], []));

        Assert.Equal(pad.Axes["RStickX"], now.Controls["yaw"].Real);
        Assert.Equal(("XInput#1", "RStickX"), controls.RealSource("yaw", [copy, pad]));
        // named before Studio read pads at all (1.9.5's Detect): by Windows' letter for the copy, with its fix
        var older = new FlightControls(PadBinds(), [], new FlightSettings { Sources = new() { ["yaw"] = "045E:028E U" }, AxisFix = new() { ["045E:028E U"] = "U" } }.Cleaned());
        Assert.Equal(pad.Axes["RStickX"], older.Take(new FlightFrame(1, [copy, pad], [])).Controls["yaw"].Real);
        Assert.Equal(0, older.Take(new FlightFrame(2, [copy], [])).Controls["yaw"].Real);   // the copy alone: still read
    }

    [Fact]
    public void A_pad_button_with_a_held_one_is_pressed_only_with_both_down_on_the_pad()
    {
        var controls = new FlightControls(PadBinds(), [], new FlightSettings());
        ControlsFrame With(ushort buttons) => controls.Take(new FlightFrame(1, [XInputPads.State(0, new PadReading(buttons, 0, 0, 0, 0, 0, 0))], []));

        Assert.Contains("fa", With(0x0200 | 0x2000).Pressed);       // the right shoulder with B held, as the game's Xbox preset has it
        Assert.DoesNotContain("fa", With(0x0200).Pressed);
        Assert.DoesNotContain("fa", With(0x2000).Pressed);
        // the copy in Windows' joystick list has no names for its buttons: only XInput's pad presses them
        Assert.DoesNotContain("fa", controls.Take(new FlightFrame(1, [Stick("045E:028E", "Controller (XBOX 360 For Windows)", ulong.MaxValue)], [])).Pressed);
    }

    [Fact]
    public async Task A_Gremlin_profile_saved_with_switching_off_has_no_FA_Off_mode_and_is_not_called_old()
    {
        using var game = new PracticeGame();
        game.Copy("Custom.4.2.binds", game.BindingsDir);
        game.Copy("StartPreset.4.start", game.BindingsDir);
        using GameWatcher watcher = game.Watch();
        watcher.Poll();
        string profile = Path.Combine(temp, "gremlin.xml");
        File.WriteAllText(profile, ProfileText());
        using var web = new WebServer(temp);
        using var flight = new FlightApi(web, new Settings(Path.Combine(temp, "settings.json")), watcher.State, null, game.BindingsDir, null,
            gremlinProfile: () => profile, sticks: _ => [], held: _ => false);
        // Flight Assist on curves of the player's own, so Default has Studio's curves to show
        JsonElement faOn = (await Ask(web, "GET", "")).Json.GetProperty("settings").GetProperty("faOn");
        Assert.Equal(200, (await Ask(web, "POST", "/settings", new JsonObject { ["faOn"] = JsonNode.Parse(faOn.GetRawText()), ["switchCurvesWithFa"] = false }.ToJsonString())).Status);

        (int status, JsonElement saved) = await Ask(web, "POST", "/gremlin");

        Assert.Equal(200, status);
        XDocument written = XDocument.Load(saved.GetProperty("path").GetString()!);
        Assert.Equal(3, Gremlin.Axes(written).Count);
        Assert.All(Gremlin.Axes(written), axis => Assert.Equal("Default", axis.Mode));
        Assert.DoesNotContain(written.Root!.Element("modes")!.Elements("mode"), mode => mode.Value.Trim() == Gremlin.FaOffMode);
        Assert.DoesNotContain(written.Descendants("action"), action => (string?)action.Attribute("type") == "change-mode");   // nothing to switch the mode
        Assert.Contains(saved.GetProperty("notes").EnumerateArray(), note => note.GetString()!.Contains("not set to follow Flight Assist"));
        Assert.False((await Ask(web, "GET", "")).Json.GetProperty("gremlin").GetProperty("outOfDate").GetBoolean());

        // Switching on again and saving again: the FA Off mode is back
        Assert.Equal(200, (await Ask(web, "POST", "/settings", "{\"switchCurvesWithFa\":true}")).Status);
        (status, saved) = await Ask(web, "POST", "/gremlin");
        Assert.Equal(200, status);
        Assert.Equal(6, Gremlin.Axes(XDocument.Load(saved.GetProperty("path").GetString()!)).Count);
    }

    [Fact]
    public async Task Switching_turned_off_after_a_save_with_it_marks_that_file_old_and_saving_again_takes_the_switch_out()
    {
        using var game = new PracticeGame();
        game.Copy("Custom.4.2.binds", game.BindingsDir);
        game.Copy("StartPreset.4.start", game.BindingsDir);
        using GameWatcher watcher = game.Watch();
        watcher.Poll();
        string profile = Path.Combine(temp, "gremlin.xml");
        File.WriteAllText(profile, ProfileText());
        using var web = new WebServer(temp);
        using var flight = new FlightApi(web, new Settings(Path.Combine(temp, "settings.json")), watcher.State, null, game.BindingsDir, null,
            gremlinProfile: () => profile, sticks: _ => [], held: _ => false);

        // Saved with the switch (the default), and the player's Default curves untouched: Studio's file has only the FA Off mode in it
        (int status, JsonElement saved) = await Ask(web, "POST", "/gremlin");
        Assert.Equal(200, status);
        string path = saved.GetProperty("path").GetString()!;
        Assert.Contains(Gremlin.Axes(XDocument.Load(path)), axis => axis.Mode == Gremlin.FaOffMode);
        Assert.False((await Ask(web, "GET", "")).Json.GetProperty("gremlin").GetProperty("outOfDate").GetBoolean());

        // Then the switch is turned off: the file Gremlin may still run switches to curves that are no longer wanted
        Assert.Equal(200, (await Ask(web, "POST", "/settings", "{\"switchCurvesWithFa\":false}")).Status);
        Assert.True((await Ask(web, "GET", "")).Json.GetProperty("gremlin").GetProperty("outOfDate").GetBoolean());

        // Saving again is allowed though nothing of Studio's is left to put in: it takes the switch out
        (status, saved) = await Ask(web, "POST", "/gremlin");
        Assert.Equal(200, status);
        Assert.Equal(path, saved.GetProperty("path").GetString());
        XDocument plain = XDocument.Load(path);
        Assert.DoesNotContain(Gremlin.Axes(plain), axis => axis.Mode == Gremlin.FaOffMode);
        Assert.False(Gremlin.IsStudios(plain));        // no action of Studio's left in it...
        Assert.True(Gremlin.IsStudiosFile(path));      // ...but it is still a file Studio wrote, so it is still Studio's to write over
        Assert.False((await Ask(web, "GET", "")).Json.GetProperty("gremlin").GetProperty("outOfDate").GetBoolean());

        // Switching on again and saving again: the same file gets its FA Off mode back (no new file with a longer name), however often
        for (int round = 0; round < 3; round++)
        {
            Assert.Equal(200, (await Ask(web, "POST", "/settings", "{\"switchCurvesWithFa\":true}")).Status);
            (status, saved) = await Ask(web, "POST", "/gremlin");
            Assert.Equal((200, path), (status, saved.GetProperty("path").GetString()));
            Assert.Contains(Gremlin.Axes(XDocument.Load(path)), axis => axis.Mode == Gremlin.FaOffMode);
            Assert.Equal(200, (await Ask(web, "POST", "/settings", "{\"switchCurvesWithFa\":false}")).Status);
            (status, saved) = await Ask(web, "POST", "/gremlin");
            Assert.Equal((200, path), (status, saved.GetProperty("path").GetString()));
            Assert.DoesNotContain(Gremlin.Axes(XDocument.Load(path)), axis => axis.Mode == Gremlin.FaOffMode);
        }
        Assert.Equal(["gremlin-fa-off.xml", "gremlin.xml"], Directory.GetFiles(temp, "gremlin*").Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileName).Order());

        // A profile of the player's with no file of Studio's to take back and nothing to write: still nothing to save
        string other = Path.Combine(temp, "other.xml");
        File.WriteAllText(other, ProfileText());
        profile = other;
        (status, _) = await Ask(web, "POST", "/gremlin");
        Assert.Equal(409, status);
    }

    [Fact]
    public void Studio_s_copy_goes_to_the_first_name_that_is_free_or_its_own_and_never_over_a_file_of_the_players()
    {
        string profile = Path.Combine(temp, "mine.xml");
        File.WriteAllText(profile, ProfileText());
        Assert.Equal(Path.Combine(temp, "mine-fa-off.xml"), Gremlin.TargetFor(profile));
        File.WriteAllText(Path.Combine(temp, "mine-fa-off.xml"), ProfileText());           // a file of the player's own with that name
        Assert.Equal(Path.Combine(temp, "mine-studio-fa-off.xml"), Gremlin.TargetFor(profile));
        File.WriteAllText(Path.Combine(temp, "mine-studio-fa-off.xml"), ProfileText());    // and one with the next name
        Assert.Equal(Path.Combine(temp, "mine-studio-fa-off-2.xml"), Gremlin.TargetFor(profile));

        // What Studio writes there is marked as Studio's, with or without any of Studio's actions in it, so it is its own to write again
        string written = Gremlin.Save(XDocument.Parse(ProfileText()), profile);
        Assert.Equal(Path.Combine(temp, "mine-studio-fa-off-2.xml"), written);
        Assert.False(Gremlin.IsStudios(XDocument.Load(written)));
        Assert.True(Gremlin.IsStudiosFile(written));
        Assert.Equal(written, Gremlin.TargetFor(profile));
        Assert.Equal(ProfileText(), File.ReadAllText(Path.Combine(temp, "mine-fa-off.xml")));   // the player's files are as they were
        Assert.Equal(ProfileText(), File.ReadAllText(Path.Combine(temp, "mine-studio-fa-off.xml")));
    }

    [Fact]
    public void A_player_s_own_FA_Off_mode_is_never_called_old_when_the_switch_is_off()
    {
        XDocument written = XDocument.Parse(ProfileText());
        (XDocument withOwn, _) = Gremlin.WithFaOff(written, new Dictionary<string, (string, StickCurve?, StickCurve)> { ["pitch"] = ("Y", null, new StickCurve()) },
            [], true, switching: true);
        IReadOnlyList<GremlinAxis> studios = Gremlin.Axes(withOwn);   // what Studio's switching save puts there: an FA Off axis with Studio's label
        Assert.Contains(studios, a => a.Mode == Gremlin.FaOffMode && a.Studios);
        FlightBinds binds = FlightBinds.Read("Custom", "Custom.4.2.binds", XElement.Parse("""<Root><PitchAxisRaw><Binding Device="vJoy" Key="Joy_YAxis" /><Inverted Value="0" /><Deadzone Value="0.0" /></PitchAxisRaw></Root>"""), deviceList: null);
        var off = new FlightCurves();

        // saved by Studio with the switch, and the switch is off now: older (saving again takes Studio's axis out)
        Assert.True(FlightApi.Older(studios, binds, null, off, studios, switching: false).Off);
        // the player's own FA Off axis (no label of Studio's) is theirs: never old, whatever Studio does
        GremlinAxis own = studios.First(a => a.Mode == Gremlin.FaOffMode) with { Studios = false };
        IReadOnlyList<GremlinAxis> theirs = [.. studios.Where(a => a.Mode != Gremlin.FaOffMode), own];
        Assert.False(FlightApi.Older(theirs, binds, null, off, theirs, switching: false).Off);
    }

    [Fact]
    public void A_flight_stick_alone_is_not_taken_for_a_pad_that_is_not_plugged_in()
    {
        StickState logitech = Stick("046D:C215", "Logitech Extreme 3D", 0, ("X", 0.9), ("Y", 0.9), ("Z", 0.9), ("R", 0.9));
        StickState vjoy = Stick("1234:BEAD", "vJoy Device", 0, ("X", 0), ("Y", 0), ("Z", 0), ("R", 0));
        var controls = new FlightControls(PadBinds(), [], new FlightSettings());

        // vertical thrust is on the pad's right stick: with the pad gone it reads nothing, not the flight stick's twist
        ControlsFrame now = controls.Take(new FlightFrame(1, [logitech, vjoy], []));
        Assert.Null(now.Controls["vertical"].Game);
        Assert.Null(now.Controls["pitch"].Real);
        // a pad that is there alone, whatever its numbers, is still found
        StickState pad = Stick("045E:0B12", "Controller (Xbox Series X)", 0, ("X", 0.2), ("Y", -0.4), ("Z", 0), ("R", 0.6), ("U", -0.7));
        Assert.Equal(0.6, controls.Take(new FlightFrame(2, [pad, vjoy], [])).Controls["vertical"].Game);
    }

    [Fact]
    public void A_pad_beside_another_stick_that_the_game_s_list_does_not_name_is_found_once_Detect_names_it()
    {
        // an Xbox Series pad (not in the list of pads Studio has), and a flight stick beside it: nothing says which is the game's
        StickState pad = Stick("045E:0B12", "Controller (Xbox Series X)", 0, ("X", 0.2), ("Y", -0.4), ("Z", 0), ("R", 0.6), ("U", -0.7));
        StickState logitech = Stick("046D:C215", "Logitech Extreme 3D", 0, ("X", 0.9), ("Y", 0.9), ("Z", 0.9), ("R", 0.9));
        ControlsFrame lost = new FlightControls(PadBinds(), [], new FlightSettings()).Take(new FlightFrame(1, [logitech, pad], []));
        Assert.Null(lost.Controls["yaw"].Game);
        Assert.Null(lost.Controls["yaw"].Real);

        // Detect names the pad for yaw (the page writes its source and the letter it moved): the game gets what that pad does
        var named = new FlightSettings { Sources = new() { ["yaw"] = "045E:0B12 RStickX" } }.Cleaned();
        ControlsFrame found = new FlightControls(PadBinds(), [], named).Take(new FlightFrame(2, [logitech, pad], []));

        Assert.Equal(new ControlValue(-0.7, -0.7, false), found.Controls["yaw"]);
        Assert.Null(found.Controls["pitch"].Real);   // (only the one named)
    }

    [Fact]
    public void A_stick_Detect_has_named_for_a_pad_control_does_not_follow_the_binding_unless_the_pad_was_not_found()
    {
        // (The page writes the source only when Studio could not read the control: see setup.html's Detect. What Studio does with
        // one that exists: it is the stick the player named, by the letter named.)
        StickState pad = Stick("045E:0B12", "Controller (Xbox Series X)", 0, ("X", 0.2), ("Y", -0.4), ("Z", 0), ("R", 0.6), ("U", -0.7));
        StickState logitech = Stick("046D:C215", "Logitech Extreme 3D", 0, ("X", 0.9), ("Y", 0.9), ("Z", 0.9), ("R", 0.9));
        var named = new FlightSettings { Sources = new() { ["yaw"] = "045E:0B12 RStickX" } }.Cleaned();
        Assert.Equal(-0.7, new FlightControls(PadBinds(), [], named).Take(new FlightFrame(1, [logitech, pad], [])).Controls["yaw"].Real);
    }
}
