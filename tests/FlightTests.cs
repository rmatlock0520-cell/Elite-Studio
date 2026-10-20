using System.Text.Json;
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
}
