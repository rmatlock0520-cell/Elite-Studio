using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using EliteStudio.App;
using EliteStudio.Flight;
using EliteStudio.Gimbalyn;

namespace EliteStudio.Tests;

/// <summary>
/// Gimbalyn without a stick or vJoy: the engine against stand-ins (shaping, writing only what changed, modes and the mode
/// key, and every way out of feeding centring the axes first), the profile made usable, the flight recorder, the control
/// pipe, and the clock.
/// </summary>
public sealed class GimbalynTests : IDisposable
{
    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioTests-").FullName;

    public void Dispose() => Directory.Delete(temp, recursive: true);

    /// <summary>Sticks by Studio's name, each with its six axes; a stick not in the list is not there.</summary>
    sealed class Sticks : IStickInput
    {
        public readonly Dictionary<string, double[]> Now = new() { ["046D:C215"] = [0, 0, -1, 0, double.NaN, double.NaN] };

        public bool Read(string device, Span<double> axes)
        {
            if (!Now.TryGetValue(device, out double[]? values)) return false;
            values.AsSpan().CopyTo(axes);
            return true;
        }

        public int Forgets;
        public void Forget() => Forgets++;
    }

    /// <summary>vJoy device 1 with all eight axes (0 to 32767), free unless an owner is named; every write is kept.</summary>
    sealed class FakeVJoy : IVJoyOutput
    {
        public string? Owner;
        public string? Problem;
        public bool Taken, Failing;
        public int Takes, Releases;
        public HashSet<string> Axes = ["X", "Y", "Z", "RX", "RY", "RZ", "U", "V"];
        public readonly List<(string Axis, int Raw)> Writes = [];
        public readonly Dictionary<string, int> Now = [];

        public VJoyTake Take(int device)
        {
            Takes++;
            if (Owner is not null) return new VJoyTake(false, Owner);
            if (Problem is not null) return new VJoyTake(false, Problem: Problem);
            Taken = true;
            return new VJoyTake(true);
        }

        public bool? OwnsAnswer;   // what Owns says, when not simply whether it is taken
        public bool Owns(int device) => OwnsAnswer ?? Taken;

        public (int Min, int Max)? Range(int device, string axis) => Axes.Contains(axis) ? (0, 32767) : null;

        public bool Set(int device, string axis, int raw)
        {
            if (!Taken || Failing) return false;
            Writes.Add((axis, raw));
            Now[axis] = raw;
            return true;
        }

        public void Release(int device) { Taken = false; Releases++; }
    }

    const int Middle = 16384;
    static readonly StickCurve Soft = new(4, 4, 92, 100, 35);
    static readonly StickCurve Gremlins = new(13, 5, 92, 68, 20);

    /// <summary>The owner's setup: the Logitech's Y, X and twist (R) to vJoy Y, X and RZ, with an FA off and an FA on mode.</summary>
    static GimbalynProfile Owners(string mode = "FA off") => new GimbalynProfile
    {
        Routes = [new("046D:C215", "Y", "Y"), new("046D:C215", "X", "X"), new("046D:C215", "R", "RZ")],
        Modes = new()
        {
            ["FA off"] = new() { ["Y"] = Soft, ["X"] = Soft, ["RZ"] = Soft },
            ["FA on"] = new() { ["Y"] = Gremlins, ["X"] = Gremlins, ["RZ"] = Gremlins },
        },
        Mode = mode,
    }.Cleaned();

    static (Engine Engine, Sticks Sticks, FakeVJoy VJoy, List<string> Log) Make(GimbalynProfile? profile = null, Func<int, bool>? keys = null, Func<string?>? front = null, Recorder? recorder = null)
    {
        var sticks = new Sticks();
        var vjoy = new FakeVJoy();
        var log = new List<string>();
        var engine = new Engine(sticks, vjoy, keys, front, recorder, log.Add);
        engine.Use(profile ?? Owners());
        return (engine, sticks, vjoy, log);
    }

    [Fact]
    public void Raw_values_are_rounded_as_Gremlin_rounds_them()
    {
        Assert.Equal((0, Middle, 32767), (Engine.ToRaw(-1, 0, 32767), Engine.ToRaw(0, 0, 32767), Engine.ToRaw(1, 0, 32767)));
        Assert.Equal((1, 32768), (Engine.ToRaw(-2, 1, 32768), Engine.ToRaw(5, 1, 32768)));   // clamped; another range
        Assert.Equal(Middle, Engine.ToRaw(double.NaN, 0, 32767));
    }

    [Fact]
    public void Starting_takes_vJoy_and_puts_every_routed_axis_in_the_middle()
    {
        (Engine engine, _, FakeVJoy vjoy, _) = Make();
        Assert.True(engine.Start());
        Assert.Equal("running", engine.Status().State);
        Assert.Equal(new Dictionary<string, int> { ["Y"] = Middle, ["X"] = Middle, ["RZ"] = Middle }, vjoy.Now);
    }

    [Fact]
    public void Each_axis_is_shaped_by_its_curve_and_only_changes_are_written()
    {
        (Engine engine, Sticks sticks, FakeVJoy vjoy, _) = Make();
        engine.Start();
        vjoy.Writes.Clear();
        sticks.Now["046D:C215"] = [0.02, 0.5, -1, -0.6, double.NaN, double.NaN];
        engine.Tick(10);
        Assert.Equal(Middle, vjoy.Now["X"]);                                  // inside the dead zone: the middle
        Assert.Equal(Engine.ToRaw(Soft.At(0.5), 0, 32767), vjoy.Now["Y"]);    // the curve, as the trainer shows it
        Assert.Equal(Engine.ToRaw(Soft.At(-0.6), 0, 32767), vjoy.Now["RZ"]);
        int written = vjoy.Writes.Count;
        engine.Tick(14);
        Assert.Equal(written, vjoy.Writes.Count);                             // nothing moved, nothing written
        // the other mode, the other curve
        Assert.True(engine.SetMode("FA on"));
        engine.Tick(18);
        Assert.Equal(Engine.ToRaw(Gremlins.At(0.5), 0, 32767), vjoy.Now["Y"]);
        Assert.False(engine.SetMode("no such mode"));
    }

    [Fact]
    public void A_route_can_turn_its_axis_round_and_an_axis_the_stick_lacks_stays_in_the_middle()
    {
        GimbalynProfile profile = new GimbalynProfile { Routes = [new("046D:C215", "Y", "Y", Invert: true), new("046D:C215", "U", "U")], Modes = new() { ["Plain"] = [] } }.Cleaned();
        (Engine engine, Sticks sticks, FakeVJoy vjoy, _) = Make(profile);
        engine.Start();
        sticks.Now["046D:C215"] = [0, 0.25, 0, 0, double.NaN, double.NaN];
        engine.Tick(10);
        Assert.Equal(Engine.ToRaw(-0.25, 0, 32767), vjoy.Now["Y"]);   // no curve in the mode: straight, turned round
        Assert.Equal(Middle, vjoy.Now["U"]);
    }

    [Fact]
    public void The_mode_key_moves_on_once_per_press_and_only_with_the_named_program_in_front()
    {
        bool held = false;
        string front = "EliteDangerous64";
        (Engine engine, _, _, _) = Make(Owners() with { ModeKey = "Key_Z", ModeKeyOnlyWith = "EliteDangerous64" }, keys: _ => held, front: () => front);
        engine.Start();
        held = true;
        engine.Tick(10);
        engine.Tick(14);                                   // still held: once only
        Assert.Equal("FA on", engine.Status().Mode);
        held = false;
        engine.Tick(18);
        held = true;
        engine.Tick(22);
        Assert.Equal("FA off", engine.Status().Mode);
        held = false;
        engine.Tick(26);
        front = "chrome";                                  // typing elsewhere does not switch
        held = true;
        engine.Tick(30);
        Assert.Equal("FA off", engine.Status().Mode);
    }

    [Fact]
    public void A_stick_that_goes_away_has_its_axes_held_in_the_middle_until_it_is_back()
    {
        (Engine engine, Sticks sticks, FakeVJoy vjoy, List<string> log) = Make();
        engine.Start();
        sticks.Now["046D:C215"] = [0.8, 0.8, 0, 0.8, double.NaN, double.NaN];
        engine.Tick(10);
        Assert.NotEqual(Middle, vjoy.Now["Y"]);
        sticks.Now.Remove("046D:C215");
        engine.Tick(14);
        engine.Tick(18);
        Assert.Equal((Middle, Middle, Middle), (vjoy.Now["Y"], vjoy.Now["X"], vjoy.Now["RZ"]));
        Assert.Equal(new[] { "046D:C215" }, engine.Status().Lost);
        Assert.All(engine.Status().Inputs, v => Assert.Null(v));   // a gone stick reads as nothing...
        Assert.Contains("null", JsonSerializer.Serialize(engine.Status(), Host.Json));   // ...and the status still goes over the pipe
        Assert.Single(log, l => l.Contains("is gone"));    // said once
        sticks.Now["046D:C215"] = [0.8, 0.8, 0, 0.8, double.NaN, double.NaN];
        engine.Tick(22);
        Assert.NotEqual(Middle, vjoy.Now["Y"]);
        Assert.Empty(engine.Status().Lost);
    }

    [Fact]
    public void A_vJoy_another_program_holds_is_never_taken_from_it()
    {
        (Engine engine, _, FakeVJoy vjoy, _) = Make();
        vjoy.Owner = "joystick_gremlin";
        Assert.False(engine.Start());
        GimbalynStatus status = engine.Status();
        Assert.Equal(("busy", "joystick_gremlin"), (status.State, status.Owner));
        vjoy.Owner = null;   // Gremlin let go: Gimbalyn does not grab it by itself
        engine.Tick(10);
        engine.Tick(2000);
        Assert.Equal(1, vjoy.Takes);
        Assert.Empty(vjoy.Writes);
    }

    [Fact]
    public void A_vJoy_device_without_the_axes_is_let_go_and_said()
    {
        (Engine engine, _, FakeVJoy vjoy, _) = Make();
        vjoy.Axes.Remove("RZ");
        Assert.False(engine.Start());
        Assert.Equal("noVJoy", engine.Status().State);
        Assert.Contains("RZ", engine.Status().Problem);
        Assert.False(vjoy.Taken);
        // vJoy that cannot be fed at all says why, word for word
        (Engine off, _, FakeVJoy notRunning, _) = Make();
        notRunning.Problem = "vJoy is installed but not running.";
        Assert.False(off.Start());
        Assert.Equal(("noVJoy", "vJoy is installed but not running."), (off.Status().State, off.Status().Problem));
    }

    [Fact]
    public void Stopping_sleeping_and_a_new_profile_all_centre_the_axes_first()
    {
        (Engine engine, Sticks sticks, FakeVJoy vjoy, _) = Make();
        engine.Start();
        sticks.Now["046D:C215"] = [0.9, -0.9, 0, 0.9, double.NaN, double.NaN];
        engine.Tick(10);
        engine.Stop("test");
        Assert.Equal((Middle, Middle, Middle, false), (vjoy.Now["Y"], vjoy.Now["X"], vjoy.Now["RZ"], vjoy.Taken));
        Assert.Equal("stopped", engine.Status().State);

        engine.Start();
        engine.Tick(20);
        engine.Sleep();
        Assert.Equal((Middle, false, "asleep"), (vjoy.Now["Y"], vjoy.Taken, engine.Status().State));
        engine.Wake();
        Assert.Equal(("running", true), (engine.Status().State, vjoy.Taken));

        // a profile without the twist: the twist is centred and no longer fed; the mode in use stays
        engine.SetMode("FA on");
        engine.Tick(30);
        engine.Use(Owners() with { Routes = [new("046D:C215", "Y", "Y")] });
        Assert.Equal(Middle, vjoy.Now["RZ"]);
        Assert.Equal("FA on", engine.Status().Mode);
        engine.Tick(40);
        Assert.Equal(Middle, vjoy.Now["RZ"]);
        Assert.Equal(new[] { "Y" }, engine.Status().Targets);
    }

    [Fact]
    public void A_failed_write_centres_what_vJoy_still_takes_at_once_and_tries_again_soon_then_less_often()
    {
        (Engine engine, Sticks sticks, FakeVJoy vjoy, List<string> log) = Make();
        engine.Start();
        sticks.Now["046D:C215"] = [0.5, 0.5, 0, 0.5, double.NaN, double.NaN];
        engine.Tick(10);
        Assert.NotEqual(Middle, vjoy.Now["Y"]);
        vjoy.Failing = true;
        sticks.Now["046D:C215"] = [0.6, 0.6, 0, 0.6, double.NaN, double.NaN];
        engine.Tick(1000);
        Assert.NotNull(engine.Status().Problem);
        Assert.Single(log, l => l.Contains("stopped taking values"));
        int takes = vjoy.Takes;
        engine.Tick(1020);                                 // the first try again comes after 50 ms
        Assert.Equal(takes, vjoy.Takes);
        engine.Tick(1060);
        Assert.Equal(takes + 1, vjoy.Takes);               // taken (vJoy still says yes), but it still takes no values: next in 100 ms
        engine.Tick(1100);
        Assert.Equal(takes + 1, vjoy.Takes);
        vjoy.Failing = false;
        engine.Tick(1200);
        Assert.Null(engine.Status().Problem);
        Assert.Equal(Engine.ToRaw(Soft.At(0.6), 0, 32767), vjoy.Now["Y"]);   // centred on taking it again, then fed on

        vjoy.Failing = true;
        sticks.Now["046D:C215"] = [0.7, 0.7, 0, 0.7, double.NaN, double.NaN];
        engine.Tick(1300);
        vjoy.Owner = "joystick_gremlin";                   // meanwhile another program took it
        engine.Tick(1400);
        Assert.Equal(("busy", "joystick_gremlin"), (engine.Status().State, engine.Status().Owner));
    }

    [Fact]
    public void A_failed_write_puts_the_axes_vJoy_still_takes_in_the_middle_at_once()
    {
        var sticks = new Sticks();
        var inner = new FakeVJoy();
        var vjoy = new FakeVJoyFailingOne(inner, "X");
        var engine = new Engine(sticks, vjoy);
        engine.Use(Owners());
        engine.Start();
        sticks.Now["046D:C215"] = [0.9, 0.9, 0, 0.9, double.NaN, double.NaN];
        engine.Tick(10);
        Assert.NotEqual(Middle, inner.Now["Y"]);
        vjoy.Failing = true;   // X alone refuses values from now on
        sticks.Now["046D:C215"] = [0.5, 0.95, 0, 0.95, double.NaN, double.NaN];
        engine.Tick(30);
        Assert.Equal((Middle, Middle), (inner.Now["Y"], inner.Now["RZ"]));   // not left deflected while vJoy is tried again
    }
    /// <summary>vJoy as <see cref="FakeVJoy"/>, but one axis refuses values while Failing is set.</summary>
    sealed class FakeVJoyFailingOne(FakeVJoy inner, string axis) : IVJoyOutput
    {
        public readonly FakeVJoy Inner = inner;
        public bool Failing;
        public VJoyTake Take(int device) => Inner.Take(device);
        public bool Owns(int device) => Inner.Owns(device);
        public (int Min, int Max)? Range(int device, string a) => Inner.Range(device, a);
        public bool Set(int device, string a, int raw) => !(Failing && a == axis) && Inner.Set(device, a, raw);
        public void Release(int device) => Inner.Release(device);
    }

    [Fact]
    public void A_vJoy_changed_behind_Gimbalyns_back_is_put_right_within_a_quarter_second_and_one_taken_from_it_is_taken_back_only_when_free()
    {
        (Engine engine, Sticks sticks, FakeVJoy vjoy, _) = Make();
        engine.Start();
        engine.Tick(10);
        vjoy.Now["RZ"] = 32767;   // something else wrote vJoy while the stick rests
        engine.Tick(100);
        Assert.Equal(32767, vjoy.Now["RZ"]);               // unchanged input: not yet
        engine.Tick(260);
        Assert.Equal(Middle, vjoy.Now["RZ"]);              // every axis written again four times a second
        // vJoy let go of Gimbalyn (its driver restarted): noticed within a second, and taken back while it is free
        vjoy.Taken = false;
        engine.Tick(1100);
        engine.Tick(1110);
        Assert.True(vjoy.Taken);
        Assert.Equal("running", engine.Status().State);
        // ...but never from a program that has it by then
        vjoy.Taken = false;
        vjoy.Owner = "joystick_gremlin";
        engine.Tick(2200);
        engine.Tick(2210);
        Assert.Equal(("busy", false), (engine.Status().State, vjoy.Taken));
    }
    [Fact]
    public void A_vJoy_that_is_no_longer_Gimbalyns_gets_the_middle_first()
    {
        (Engine engine, Sticks sticks, FakeVJoy vjoy, _) = Make();
        engine.Start();
        sticks.Now["046D:C215"] = [0.9, 0.9, 0, 0.9, double.NaN, double.NaN];
        engine.Tick(10);
        Assert.NotEqual(Middle, vjoy.Now["Y"]);
        vjoy.OwnsAnswer = false;                           // vJoy says it is not Gimbalyn's, yet still takes values
        vjoy.Problem = "vJoy device 1 is not set up";      // and it cannot be taken again just now
        engine.Tick(1100);
        Assert.Equal((Middle, Middle, Middle), (vjoy.Now["Y"], vjoy.Now["X"], vjoy.Now["RZ"]));
        engine.Tick(1200);
        engine.Tick(3000);
        Assert.Equal(Middle, vjoy.Now["Y"]);               // and stays there while vJoy is tried again
    }

    [Fact]
    public void A_profile_that_cannot_be_saved_is_not_put_in_use()
    {
        var recorder = new Recorder();
        (Engine engine, _, _, _) = Make(new GimbalynProfile(), recorder: recorder);
        using var quit = new ManualResetEventSlim();
        string blocked = Path.Combine(temp, "blocked");
        Directory.CreateDirectory(blocked);   // a folder where the file should go: the save fails
        using var server = new ControlServer(engine, recorder, "secret", "unused", blocked, Path.Combine(temp, "logs"), new GimbalynLog(Path.Combine(temp, "logs", "gimbalyn.log")), quit);
        JsonObject answer = (JsonObject)JsonNode.Parse(server.Handle("{\"token\":\"secret\",\"command\":\"profile\",\"profile\":" + JsonSerializer.Serialize(Owners(), Host.Json) + "}"))!;
        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.Contains("could not be saved", answer["error"]!.GetValue<string>());
        Assert.Empty(engine.Profile.Routes);               // the one before stays in use
    }
    [Fact]
    public void The_profile_is_made_usable()
    {
        GimbalynProfile cleaned = new GimbalynProfile
        {
            Device = 40,
            Routes = [new("046d:c215", "Y", "Y"), new("not a stick", "X", "X"), new("046D:C215", "Q", "X"), new("046D:C215", "X", "Y"), new("046D:C215", "R", "RZ"), null!],
            Modes = new() { ["FA off"] = new() { ["Y"] = new StickCurve(-5, 99, 10, 200, 50), ["W"] = Soft }, ["bad/name"] = [] },
            Mode = "missing",
            ModeKey = "Key_NoSuchKey",
            ModeKeyOnlyWith = "Elite Dangerous!",
            VJoyFolder = @"\\server\share\vJoy",
        }.Cleaned();
        Assert.Equal(16, cleaned.Device);
        Assert.Equal(new[] { "Y", "RZ" }, cleaned.Routes.Select(r => r.Target));   // one route per vJoy axis, the first wins
        Assert.Equal("046D:C215", cleaned.Routes[0].Device);
        Assert.Equal(new[] { "FA off" }, cleaned.Modes.Keys);
        Assert.Equal(new StickCurve(0, 40, 50, 100, 50), cleaned.Modes["FA off"]["Y"]);
        Assert.False(cleaned.Modes["FA off"].ContainsKey("W"));
        Assert.Equal("FA off", cleaned.Mode);
        Assert.Null(cleaned.ModeKey);
        Assert.Null(cleaned.ModeKeyOnlyWith);
        Assert.Equal("", cleaned.VJoyFolder);
        Assert.Equal("Key_Z", (new GimbalynProfile { ModeKey = "Key_Z" }).Cleaned().ModeKey);
    }

    [Fact]
    public void The_flight_recorder_keeps_the_last_frames_oldest_first_with_the_names_they_were_made_with()
    {
        var recorder = new Recorder(frames: 3, routes: 2);
        string[] modes = ["FA off", "FA on"], routes = ["Y", "X"];
        for (int i = 1; i <= 4; i++) recorder.Add(i * 100, 1, i % 2, modes, routes, [i / 10.0, -i / 10.0], [i / 20.0, 0], 0);
        recorder.Add(500, 1, 0, ["FA off"], ["Y"], [double.NaN], [0], 1);   // a new profile, and the stick gone
        Assert.Equal(3, recorder.Count);
        string[] lines = recorder.Csv().TrimEnd().Split('\n');
        Assert.StartsWith("# Gimbalyn flight recorder: 3 frames", lines[0]);
        Assert.Equal(new[]
        {
            "seconds,state,mode,sticks gone,Y in,Y out,X in,X out",
            "-0.200,running,FA on,,0.3000,0.1500,-0.3000,0.0000",
            "-0.100,running,FA off,,0.4000,0.2000,-0.4000,0.0000",
            "# the profile changed here",
            "seconds,state,mode,sticks gone,Y in,Y out",
            "0.000,running,FA off,0x1,,0.0000",                    // a gone stick's input is left empty, not shown as at rest
        }, lines[1..]);
    }

    [Fact]
    public void The_engine_records_in_every_state_so_the_moments_that_matter_are_kept()
    {
        var recorder = new Recorder(frames: 20);
        (Engine engine, Sticks sticks, FakeVJoy vjoy, _) = Make(recorder: recorder);
        engine.Tick(1);                                    // stopped: recorded too
        engine.Start();
        engine.Tick(5);
        sticks.Now.Remove("046D:C215");
        engine.Tick(9);
        vjoy.Failing = true;
        sticks.Now["046D:C215"] = [0.5, 0.5, 0, 0.5, double.NaN, double.NaN];
        engine.Tick(13);
        engine.Tick(17);
        Assert.Equal(5, recorder.Count);
        string csv = recorder.Csv();
        Assert.Contains(",stopped,", csv);
        Assert.Contains(",vJoy failing,", csv);
        Assert.Contains(",0x1,,", csv);                    // the gone stick
    }

    [Fact]
    public void The_mode_key_counts_in_every_state_and_a_key_held_as_the_profile_arrives_is_not_a_press()
    {
        bool held = true;
        (Engine engine, _, _, _) = Make(Owners() with { ModeKey = "Key_Z" }, keys: _ => held);
        engine.Tick(1);
        Assert.Equal("FA off", engine.Status().Mode);       // held all along: not a press
        held = false;
        engine.Tick(2);
        held = true;
        engine.Tick(3);                                    // pressed while stopped: counted
        Assert.Equal("FA on", engine.Status().Mode);
        engine.Start();
        Assert.Equal("FA on", engine.Status().Mode);
    }

    [Fact]
    public void Starting_looks_for_the_sticks_afresh_and_a_profile_is_written_to_the_log()
    {
        (Engine engine, Sticks sticks, _, List<string> log) = Make();
        Assert.Contains(log, l => l.StartsWith("profile: vJoy device 1; 046D:C215 Y -> Y, 046D:C215 X -> X, 046D:C215 R -> RZ;") && l.Contains("FA off: Y dead 4/4 satX 92 satY 100 bend 35"));
        engine.Start();
        Assert.Equal(1, sticks.Forgets);
        engine.Sleep();
        engine.Wake();
        Assert.Equal(2, sticks.Forgets);
    }

    [Fact]
    public void Stopping_says_so_when_vJoy_would_not_take_the_middle()
    {
        (Engine engine, _, FakeVJoy vjoy, List<string> log) = Make();
        engine.Start();
        vjoy.Failing = true;
        engine.Stop("test");
        Assert.Contains(log, l => l.Contains("could NOT centre"));
        Assert.True(engine.TryStop("again"));               // and stopping again is harmless
    }

    [Fact]
    public void The_one_off_centre_centres_the_device_and_axes_Gimbalyn_fed_and_leaves_a_held_device_alone()
    {
        var vjoy = new FakeVJoy();
        Assert.Equal("centred 2 axes of vJoy device 2", VJoyFeeder.CentreOnce(vjoy, 2, ["Y", "RZ"]));
        Assert.Equal(new[] { ("Y", Middle), ("RZ", Middle) }, vjoy.Writes);
        Assert.False(vjoy.Taken);                          // let go again
        vjoy.Writes.Clear();
        Assert.Equal("centred 8 axes of vJoy device 1", VJoyFeeder.CentreOnce(vjoy, 1, []));   // no routes known: every axis
        vjoy.Writes.Clear();
        vjoy.Owner = "joystick_gremlin";
        Assert.StartsWith("left alone", VJoyFeeder.CentreOnce(vjoy, 1, ["Y"]));
        Assert.Empty(vjoy.Writes);
    }

    [Fact]
    public void A_Gimbalyn_written_down_but_not_running_is_known_for_what_it_is()
    {
        Assert.False(GimbalynClient.Running(temp));                                    // none written down
        File.WriteAllText(Path.Combine(temp, Host.InfoFile), JsonSerializer.Serialize(new GimbalynInfo("p", "t", int.MaxValue - 7, "test", 1), Host.Json));
        Assert.False(GimbalynClient.Running(temp));                                    // its process is gone
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(temp, Host.InfoFile), JsonSerializer.Serialize(new GimbalynInfo("p", "t", me.Id, "test", 12345), Host.Json));
        Assert.False(GimbalynClient.Running(temp));                                    // a process of that number, started at another time
        File.WriteAllText(Path.Combine(temp, Host.InfoFile), JsonSerializer.Serialize(new GimbalynInfo("p", "t", me.Id, "test", me.StartTime.ToUniversalTime().Ticks), Host.Json));
        Assert.True(GimbalynClient.Running(temp));
    }
    ControlServer Server(Engine engine, Recorder recorder, ManualResetEventSlim quit, string pipe = "unused") =>
        new(engine, recorder, "secret", pipe, Path.Combine(temp, Host.ProfileFile), Path.Combine(temp, "logs"), new GimbalynLog(Path.Combine(temp, "logs", "gimbalyn.log")), quit);

    [Fact]
    public void The_control_pipe_answers_only_with_the_token_and_saves_a_profile_it_is_sent()
    {
        var recorder = new Recorder();
        (Engine engine, _, FakeVJoy vjoy, _) = Make(new GimbalynProfile(), recorder: recorder);
        using var quit = new ManualResetEventSlim();
        using ControlServer server = Server(engine, recorder, quit);
        JsonObject Ask(string json) => (JsonObject)JsonNode.Parse(server.Handle(json))!;

        Assert.False(Ask("{\"token\":\"wrong\",\"command\":\"status\"}")["ok"]!.GetValue<bool>());
        Assert.False(Ask("{\"command\":\"status\"}")["ok"]!.GetValue<bool>());
        Assert.False(Ask("{\"token\":5,\"command\":\"status\"}")["ok"]!.GetValue<bool>());   // a field of the wrong kind
        Assert.False(Ask("not json")["ok"]!.GetValue<bool>());
        Assert.False(Ask("{\"token\":\"secret\",\"token\":\"secret\",\"command\":\"status\"}")["ok"]!.GetValue<bool>());   // a name twice: refused, not a crash
        Assert.Equal("stopped", Ask("{\"token\":\"secret\",\"command\":\"status\"}")["status"]!["state"]!.GetValue<string>());

        string profile = JsonSerializer.Serialize(Owners(), Host.Json);
        JsonObject answer = Ask("{\"token\":\"secret\",\"command\":\"profile\",\"profile\":" + profile + "}");
        Assert.True(answer["ok"]!.GetValue<bool>());
        Assert.Equal(3, JsonSerializer.Deserialize<GimbalynProfile>(File.ReadAllText(Path.Combine(temp, Host.ProfileFile)), Host.Json)!.Routes.Count);
        Assert.Equal("running", Ask("{\"token\":\"secret\",\"command\":\"start\"}")["status"]!["state"]!.GetValue<string>());
        Assert.True(vjoy.Taken);
        Assert.False(Ask("{\"token\":\"secret\",\"command\":\"mode\",\"mode\":\"nope\"}")["ok"]!.GetValue<bool>());
        Assert.Equal("FA on", Ask("{\"token\":\"secret\",\"command\":\"mode\",\"mode\":\"FA on\"}")["status"]!["mode"]!.GetValue<string>());
        engine.Tick(5);
        string record = Ask("{\"token\":\"secret\",\"command\":\"record\"}")["path"]!.GetValue<string>();
        Assert.Contains("seconds,state,mode,sticks gone,Y in", File.ReadAllText(record));
        Assert.Equal("stopped", Ask("{\"token\":\"secret\",\"command\":\"stop\"}")["status"]!["state"]!.GetValue<string>());
        Assert.False(vjoy.Taken);
        Assert.False(Ask("{\"token\":\"secret\",\"command\":\"fly\"}")["ok"]!.GetValue<bool>());
        Assert.True(Ask("{\"token\":\"secret\",\"command\":\"quit\"}")["ok"]!.GetValue<bool>());
        Assert.True(server.QuitAsked);   // done once the answer has gone out
    }

    [Fact]
    public async Task Studio_reaches_Gimbalyn_through_its_pipe_with_the_token_from_the_data_folder()
    {
        var recorder = new Recorder();
        (Engine engine, _, _, _) = Make(recorder: recorder);
        using var quit = new ManualResetEventSlim();
        string pipe = "Gimbalyn-test-" + Guid.NewGuid().ToString("N");
        using ControlServer server = Server(engine, recorder, quit, pipe);
        server.Start();
        File.WriteAllText(Path.Combine(temp, Host.InfoFile), JsonSerializer.Serialize(new GimbalynInfo(pipe, "secret", Environment.ProcessId, "test"), Host.Json));
        JsonObject? answer = await GimbalynClient.Send(temp, new JsonObject { ["command"] = "status" }, 5000);
        Assert.NotNull(answer);
        Assert.True(answer["ok"]!.GetValue<bool>());
        Assert.Equal("FA off", answer["status"]!["mode"]!.GetValue<string>());
        // a second request on a fresh connection is served too
        answer = await GimbalynClient.Send(temp, new JsonObject { ["command"] = "mode", ["mode"] = "FA on" }, 5000);
        Assert.Equal("FA on", answer!["status"]!["mode"]!.GetValue<string>());
        // no Gimbalyn written down: no answer, quickly
        Assert.Null(await GimbalynClient.Send(Path.Combine(temp, "nowhere"), new JsonObject { ["command"] = "status" }));
    }

    [Fact]
    public void The_clock_beats_every_few_milliseconds()
    {
        using var pacer = new Pacer(4);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int beats = 0;
        while (clock.ElapsedMilliseconds < 200) { pacer.Wait(); beats++; }
        Assert.InRange(beats, 10, 400);   // loose: a busy test machine is slow, never stuck
    }

    [Fact]
    public void The_options_start_Gimbalyn_with_the_program_it_ends_with()
    {
        Dictionary<string, string> options = Program.ParseOptions(["--gimbalyn", "--with", "4242", "--start"]);
        Assert.True(options.ContainsKey(Host.Option));
        Assert.Equal("4242", options["with"]);
        Assert.True(options.ContainsKey("start"));
    }

    // ---- Flight Assist's two curve sets through Gimbalyn (the profile Studio makes, and its dealings with a running Gimbalyn).
    // Everything here is made-up: a stand-in for the process and the pipe, so no test starts Gimbalyn or touches vJoy.

    static FlightBinds OwnersBinds()
    {
        XElement root = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gamefiles", "Custom.4.2.binds")).Root!;
        return FlightBinds.Read("Custom", "Custom.4.2.binds", root, deviceList: null);
    }

    static readonly FlightCurves FaOnCurves = new(new StickCurve(2, 2, 100, 100, 0), new StickCurve(3, 3, 100, 90, 10), new StickCurve(4, 4, 100, 80, 20));
    static readonly FlightCurves FaOffCurves = new(new StickCurve(5, 5, 100, 100, 40), new StickCurve(6, 6, 100, 95, 50), new StickCurve(7, 7, 100, 85, 60));

    /// <summary>The Logitech stick behind each turning control, by Windows' letters (the twist is R).</summary>
    static (string Device, string Letter)? Logitech(string control) => control switch
    {
        "pitch" => ("046D:C215", "Y"), "roll" => ("046D:C215", "X"), "yaw" => ("046D:C215", "R"), _ => null,
    };

    static ShaperProfile.Made MakeProfile(FlightBinds? binds = null, Func<string, (string, string)?>? source = null, bool switching = true, bool faOff = false) =>
        ShaperProfile.Build(binds ?? OwnersBinds(), source ?? Logitech, FaOnCurves, FaOffCurves, switching, faOff, vjoyFolder: "");

    [Fact]
    public void Pitch_roll_and_yaw_go_to_the_vJoy_axes_the_game_reads_with_a_curve_set_each_for_Flight_Assist_on_and_off()
    {
        ShaperProfile.Made made = MakeProfile();

        Assert.Null(made.Problem);
        GimbalynProfile profile = made.Profile!;
        Assert.Equal(1, profile.Device);
        Assert.Equal([new AxisRoute("046D:C215", "Y", "Y"), new AxisRoute("046D:C215", "X", "X"), new AxisRoute("046D:C215", "R", "RZ")], profile.Routes);
        Assert.Equal(["FA On", "FA Off"], profile.Modes.Keys);
        Assert.Equal(FaOnCurves.Pitch, profile.Modes["FA On"]["Y"]);
        Assert.Equal(FaOnCurves.Roll, profile.Modes["FA On"]["X"]);
        Assert.Equal(FaOnCurves.Yaw, profile.Modes["FA On"]["RZ"]);
        Assert.Equal(FaOffCurves.Pitch, profile.Modes["FA Off"]["Y"]);
        Assert.Equal(FaOffCurves.Roll, profile.Modes["FA Off"]["X"]);
        Assert.Equal(FaOffCurves.Yaw, profile.Modes["FA Off"]["RZ"]);
        Assert.Equal(profile.Routes.Count, profile.Cleaned().Routes.Count);   // nothing Gimbalyn would throw away
        Assert.Equal(2, profile.Cleaned().Modes.Count);
    }

    [Theory]
    [InlineData(false, "FA On")]
    [InlineData(true, "FA Off")]
    public void The_profile_starts_in_the_mode_the_game_is_in(bool faOff, string mode) =>
        Assert.Equal(mode, MakeProfile(faOff: faOff).Profile!.Mode);

    [Fact]
    public void With_switching_off_the_game_always_gets_the_Flight_Assist_on_curves()
    {
        GimbalynProfile profile = MakeProfile(switching: false, faOff: true).Profile!;

        Assert.Equal(["FA On"], profile.Modes.Keys);
        Assert.Equal("FA On", profile.Mode);
        Assert.Null(profile.ModeKey);
        Assert.Equal(FaOnCurves.Pitch, profile.Modes["FA On"]["Y"]);
    }

    [Fact]
    public void The_Flight_Assist_key_switches_the_mode_at_once_while_Elite_is_in_front()
    {
        GimbalynProfile profile = MakeProfile().Profile!;

        Assert.Equal("Key_Z", profile.ModeKey);
        Assert.Equal("EliteDangerous64", profile.ModeKeyOnlyWith);
    }

    [Fact]
    public void A_Flight_Assist_that_is_held_or_on_a_stick_button_has_no_key_so_only_the_game_s_own_flag_switches()
    {
        FlightBinds binds = OwnersBinds();
        FlightBinds hold = binds with { Toggles = new Dictionary<string, bool>(binds.Toggles) { ["fa"] = false } };
        FlightBinds button = binds with { Buttons = new Dictionary<string, IReadOnlyList<FlightButton>>(binds.Buttons) { ["fa"] = [new FlightButton(new FlightKey("LogitechExtreme3DPro", "Joy_5"), [])] } };

        Assert.Null(MakeProfile(hold).Profile!.ModeKey);
        Assert.Null(MakeProfile(button).Profile!.ModeKey);
        Assert.Equal(2, MakeProfile(hold).Profile!.Modes.Count);   // still two modes: the flag switches them
    }

    [Fact]
    public void An_axis_the_game_reads_from_a_real_stick_or_that_has_no_stick_is_left_out_and_said()
    {
        FlightBinds binds = OwnersBinds();
        FlightBinds direct = binds with { Axes = new Dictionary<string, FlightAxis>(binds.Axes) { ["roll"] = new FlightAxis("LogitechExtreme3DPro", "X", false, 0) } };

        ShaperProfile.Made rollDirect = MakeProfile(direct);
        Assert.Equal(["Y", "RZ"], rollDirect.Profile!.Routes.Select(r => r.Target));
        Assert.Contains("roll", Assert.Single(rollDirect.Notes));

        ShaperProfile.Made noYawStick = MakeProfile(source: c => c == "yaw" ? null : Logitech(c));
        Assert.Equal(["Y", "X"], noYawStick.Profile!.Routes.Select(r => r.Target));
        Assert.Contains("yaw", Assert.Single(noYawStick.Notes));
    }

    [Fact]
    public void Elite_s_own_dead_zone_on_an_axis_is_said_because_it_stacks_on_the_curve()
    {
        FlightBinds binds = OwnersBinds();
        FlightBinds withDead = binds with { Axes = new Dictionary<string, FlightAxis>(binds.Axes) { ["pitch"] = binds.Axes["pitch"] with { Deadzone = 0.2 } } };

        ShaperProfile.Made made = MakeProfile(withDead);

        Assert.Equal(3, made.Profile!.Routes.Count);   // still routed
        string note = Assert.Single(made.Notes);
        Assert.Contains("20%", note);
        Assert.Contains("pitch", note);
    }

    [Fact]
    public void With_nothing_to_route_there_is_no_profile_and_a_reason()
    {
        Assert.Contains("bindings", ShaperProfile.Build(null, Logitech, FaOnCurves, FaOffCurves, switching: true, faOff: false, vjoyFolder: "").Problem);
        ShaperProfile.Made none = MakeProfile(source: _ => null);
        Assert.Null(none.Profile);
        Assert.False(string.IsNullOrEmpty(none.Problem));
    }

    /// <summary>A Gimbalyn that is or is not running, answering as the real one does, and every request it was sent.</summary>
    internal sealed class FakeGimbalyn
    {
        public bool Running, CanStart = true;
        public int Launches;
        public readonly List<JsonObject> Requests = [];
        public string StateAnswer = "running";
        public string? Problem;
        public bool Refuses;
        public string Mode = "";   // the mode it is in: the profile's, then what it was told (or what a press of its key made of it)
        public string? OnStart;    // when set, a "start" puts it in that state (vJoy was free again)
        public bool Mute;          // takes the request but never answers it in time (a start that timed out while vJoy's library loaded)

        public bool IsRunning() => Running;
        public bool Launch()
        {
            Launches++;
            Running = CanStart;
            return CanStart;
        }

        public Task<JsonObject?> Send(JsonObject request)
        {
            if (!Running) return Task.FromResult<JsonObject?>(null);
            Requests.Add(request);
            if (Mute) return Task.FromResult<JsonObject?>(null);
            if (Refuses) return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = false, ["error"] = "There is no mode of that name." });
            // As the real engine: a mode in use stays in use when the new profile has a mode of that name; else it starts in the profile's.
            if ((string?)request["command"] == "profile")
                Mode = Mode != "" && request["profile"]!["modes"] is JsonObject modes && modes.ContainsKey(Mode) ? Mode : (string?)request["profile"]!["mode"] ?? "";
            if ((string?)request["command"] == "mode") Mode = (string)request["mode"]!;
            if ((string?)request["command"] == "start" && OnStart is not null) (StateAnswer, Problem) = (OnStart, null);
            return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true, ["status"] = new JsonObject { ["state"] = StateAnswer, ["problem"] = Problem, ["mode"] = Mode } });
        }

        public string[] Commands => [.. Requests.Select(r => (string)r["command"]!)];
    }

    internal static FlightShaper ShaperFor(FakeGimbalyn gimbalyn) => new(gimbalyn.IsRunning, gimbalyn.Launch, gimbalyn.Send, _ => { }, waitMs: 0);

    [Fact]
    public async Task A_profile_starts_Gimbalyn_when_it_is_not_running_then_is_sent_and_fed()
    {
        var gimbalyn = new FakeGimbalyn { Running = false };

        string? problem = await ShaperFor(gimbalyn).Use(MakeProfile().Profile!);

        Assert.Null(problem);
        Assert.Equal(1, gimbalyn.Launches);
        Assert.Equal(["profile", "start"], gimbalyn.Commands);
        Assert.Equal("Key_Z", (string?)gimbalyn.Requests[0]["profile"]!["modeKey"]);
    }

    [Fact]
    public async Task The_same_profile_is_not_sent_twice_but_a_changed_one_is_and_Gimbalyn_is_not_started_again()
    {
        var gimbalyn = new FakeGimbalyn { Running = true };
        FlightShaper shaper = ShaperFor(gimbalyn);
        GimbalynProfile profile = MakeProfile().Profile!;

        await shaper.Use(profile);
        await shaper.Use(profile with { Mode = profile.Mode });   // equal in every way that matters
        Assert.Equal(["profile", "start"], gimbalyn.Commands);

        await shaper.Use(MakeProfile(switching: false).Profile!);
        Assert.Equal(["profile", "start", "profile", "start"], gimbalyn.Commands);
        Assert.Equal(0, gimbalyn.Launches);
    }

    [Fact]
    public async Task The_mode_alone_changing_does_not_send_the_profile_again()
    {
        var gimbalyn = new FakeGimbalyn { Running = true };
        FlightShaper shaper = ShaperFor(gimbalyn);

        await shaper.Use(MakeProfile(faOff: false).Profile!);
        await shaper.Use(MakeProfile(faOff: true).Profile!);   // the same profile, started in the other mode

        Assert.Equal(["profile", "start"], gimbalyn.Commands);
    }

    [Fact]
    public async Task A_Gimbalyn_that_ended_is_started_again_and_gets_the_profile_again()
    {
        var gimbalyn = new FakeGimbalyn { Running = true };
        FlightShaper shaper = ShaperFor(gimbalyn);
        GimbalynProfile profile = MakeProfile().Profile!;
        await shaper.Use(profile);

        gimbalyn.Running = false;   // closed since
        Assert.Null(await shaper.Use(profile));

        Assert.Equal(1, gimbalyn.Launches);
        Assert.Equal(["profile", "start", "profile", "start"], gimbalyn.Commands);
    }

    [Fact]
    public async Task Flight_Assist_changing_sends_the_mode_once_per_change_while_switching()
    {
        var gimbalyn = new FakeGimbalyn { Running = true };
        FlightShaper shaper = ShaperFor(gimbalyn);
        await shaper.Use(MakeProfile().Profile!);
        gimbalyn.Requests.Clear();

        await shaper.Follow(faOff: true, now: 0);
        await shaper.Follow(faOff: true, now: 10);
        await shaper.Follow(faOff: false, now: 20);

        Assert.Equal(["mode", "mode"], gimbalyn.Commands);
        Assert.Equal(["FA Off", "FA On"], gimbalyn.Requests.Select(r => (string)r["mode"]!));
    }

    [Fact]
    public async Task A_mode_the_key_changed_without_the_game_following_is_put_right_at_the_next_look()
    {
        var gimbalyn = new FakeGimbalyn { Running = true };
        FlightShaper shaper = ShaperFor(gimbalyn);
        await shaper.Use(MakeProfile().Profile!);   // in FA On, the game agrees
        gimbalyn.Requests.Clear();

        await shaper.Follow(faOff: false, now: 0);      // the first look asks and finds nothing wrong
        gimbalyn.Requests.Clear();
        gimbalyn.Mode = "FA Off";                        // the key cycled it, the game did not take the press
        await shaper.Follow(faOff: false, now: 500);     // not yet a second
        Assert.Empty(gimbalyn.Requests);

        await shaper.Follow(faOff: false, now: 1200);    // asked, found apart: the game's flag may only be late, so not yet told
        Assert.Equal(["status"], gimbalyn.Commands);
        Assert.Equal("FA Off", gimbalyn.Mode);

        await shaper.Follow(faOff: false, now: 2300);    // asked again, still apart: told
        Assert.Equal(["status", "status", "mode"], gimbalyn.Commands);
        Assert.Equal("FA On", gimbalyn.Mode);

        await shaper.Follow(faOff: false, now: 3400);    // asked again: right now, nothing sent
        Assert.Equal(["status", "status", "mode", "status"], gimbalyn.Commands);
    }

    [Fact]
    public async Task A_press_of_the_mode_key_is_not_undone_while_the_game_s_flag_catches_up()
    {
        var gimbalyn = new FakeGimbalyn { Running = true };
        FlightShaper shaper = ShaperFor(gimbalyn);
        await shaper.Use(MakeProfile().Profile!);   // in FA On
        await shaper.Follow(faOff: false, now: 0);
        gimbalyn.Requests.Clear();

        gimbalyn.Mode = "FA Off";                        // the player pressed the key: Gimbalyn went at once
        await shaper.Follow(faOff: false, now: 1200);    // the check lands before the game's flag shows it
        await shaper.Follow(faOff: true, now: 1300);     // the flag arrives
        await shaper.Follow(faOff: true, now: 2400);

        Assert.DoesNotContain(gimbalyn.Requests, r => (string?)r["mode"] == "FA On");
        Assert.Equal("FA Off", gimbalyn.Mode);
    }

    [Fact]
    public async Task A_Gimbalyn_that_cannot_feed_vJoy_any_more_is_asked_to_start_again_every_few_seconds_and_the_player_is_told_why()
    {
        var gimbalyn = new FakeGimbalyn { Running = true };
        FlightShaper shaper = ShaperFor(gimbalyn);
        Assert.Null(await shaper.Look(Want(), now: 0));
        gimbalyn.Requests.Clear();

        // The app looks four times a second, each look Update and then Follow, whatever Update said (FlightShaper.Look).
        // The PC slept, or Joystick Gremlin took vJoy: Gimbalyn still runs and has the profile, but feeds nothing.
        const string why = "vJoy device 1 is held by joystick_gremlin.";
        Dictionary<long, string?> said = [];
        for (long t = 250; t <= 7500; t += 250)
        {
            if (t == 1500) (gimbalyn.StateAnswer, gimbalyn.Problem) = ("busy", why);
            if (t == 6250) gimbalyn.OnStart = "running";   // Gremlin let go
            said[t] = await shaper.Look(Want(), t);
            if (t == 6750) Assert.Equal(1, gimbalyn.Commands.Count(c => c == "start"));   // asked once, and only again after a few seconds
        }

        Assert.Equal(2, gimbalyn.Commands.Count(c => c == "start"));
        Assert.Equal(why, said[2250]);       // said, not left looking fine
        Assert.Equal(why, said[6500]);
        Assert.Null(said[7500]);             // fed again, and not said any more
        Assert.Equal("running", shaper.State);
        Assert.Equal(0, gimbalyn.Launches);
    }

    [Fact]
    public async Task A_Gimbalyn_that_was_asleep_is_left_to_wake_by_itself()
    {
        var gimbalyn = new FakeGimbalyn { Running = true };
        FlightShaper shaper = ShaperFor(gimbalyn);
        await shaper.Look(Want(), now: 0);
        gimbalyn.Requests.Clear();

        gimbalyn.StateAnswer = "asleep";   // Windows sleeps: Gimbalyn takes vJoy again by itself when it wakes
        Assert.Null(await shaper.Look(Want(), now: 1000));
        Assert.Null(await shaper.Look(Want(), now: 1250));

        Assert.Equal(["status"], gimbalyn.Commands);
    }

    [Fact]
    public async Task A_restart_that_took_clears_the_words_at_once_though_the_last_try_s_clock_has_not_run_out()
    {
        var gimbalyn = new FakeGimbalyn { Running = true };
        FlightShaper shaper = ShaperFor(gimbalyn);
        await shaper.Look(Want(), now: 0);
        (gimbalyn.StateAnswer, gimbalyn.Problem) = ("busy", "vJoy device 1 is held by joystick_gremlin.");
        await shaper.Look(Want(), now: 1000);                                  // found busy, asked to start: still busy
        Assert.Equal(gimbalyn.Problem, await shaper.Look(Want(), now: 1250));   // said (and the next try waits until 6250)

        gimbalyn.OnStart = "running";   // Gremlin let go
        foreach (long t in new long[] { 2000, 3000, 4000, 5000 }) await shaper.Look(Want(), t);
        await shaper.Look(Want(), now: 6000);                                  // the start is asked again: it takes

        Assert.Null(await shaper.Look(Want(), now: 6100));   // before the clock of the last try (6250) ran out
        Assert.Equal("running", shaper.State);
    }

    [Fact]
    public async Task A_Gimbalyn_that_ended_while_feeding_has_vJoy_centred_once_and_is_started_again_at_the_next_look()
    {
        var gimbalyn = new FakeGimbalyn { Running = true };
        int centred = 0;
        var shaper = new FlightShaper(gimbalyn.IsRunning, gimbalyn.Launch, gimbalyn.Send, _ => { }, waitMs: 0, centre: () => centred++);
        // Looking every quarter second for a good while: each look that finds nothing wrong pushes the retry clock on
        for (long t = 0; t < 20000; t += 250) Assert.Null(await shaper.Look(Want(), t));

        gimbalyn.Running = false;   // it ended (vJoy keeps the axes where they were)
        Assert.Null(await shaper.Look(Want(), 20000));

        Assert.Equal((1, 1), (centred, gimbalyn.Launches));   // not left to wait out a retry clock: the axes are deflected meanwhile
        Assert.Equal((2, 2), (gimbalyn.Commands.Count(c => c == "profile"), gimbalyn.Commands.Count(c => c == "start")));   // fed again, from the start
    }

    [Fact]
    public async Task A_Gimbalyn_that_ended_just_after_it_was_fed_is_centred_once_and_started_again_after_the_wait()
    {
        var gimbalyn = new FakeGimbalyn { Running = true };
        int centred = 0;
        var shaper = new FlightShaper(gimbalyn.IsRunning, gimbalyn.Launch, gimbalyn.Send, _ => { }, waitMs: 0, centre: () => centred++);
        Assert.Null(await shaper.Look(Want(), now: 0));

        gimbalyn.Running = false;
        Assert.Contains("stopped", await shaper.Look(Want(), now: 250));
        Assert.Contains("stopped", await shaper.Look(Want(), now: 4000));
        Assert.Equal((1, 0), (centred, gimbalyn.Launches));   // centred once, not at every look

        Assert.Null(await shaper.Look(Want(), now: 5500));
        Assert.Equal((1, 1), (centred, gimbalyn.Launches));
    }

    [Fact]
    public async Task A_new_profile_does_not_move_a_Gimbalyn_that_already_has_the_mode_so_the_game_s_mode_is_put_in_at_once()
    {
        // The last session's Gimbalyn is in FA On and Flight Assist is off in the game: the profile asks for FA Off,
        // the engine keeps the mode in use (the name is in both profiles), so it is the status that tells where it is
        var gimbalyn = new FakeGimbalyn { Running = true, Mode = "FA On" };
        FlightShaper shaper = ShaperFor(gimbalyn);

        Assert.Null(await shaper.Look(Want(faOff: true), now: 0));

        Assert.Equal(["profile", "start", "mode"], gimbalyn.Commands);
        Assert.Equal("FA Off", gimbalyn.Mode);
    }

    [Fact]
    public async Task A_mode_found_wrong_once_is_not_changed_when_the_next_check_finds_it_right()
    {
        var gimbalyn = new FakeGimbalyn { Running = true };
        FlightShaper shaper = ShaperFor(gimbalyn);
        await shaper.Use(MakeProfile().Profile!);   // in FA On, the game agrees
        await shaper.Follow(faOff: false, now: 0);
        gimbalyn.Requests.Clear();

        gimbalyn.Mode = "FA Off";                        // a key press, the game's flag not there yet
        await shaper.Follow(faOff: false, now: 1200);
        gimbalyn.Mode = "FA On";                         // pressed back, or the flag caught up in the same mode
        await shaper.Follow(faOff: false, now: 2300);
        gimbalyn.Mode = "FA Off";                        // a new press, the flag late again
        await shaper.Follow(faOff: false, now: 3400);

        Assert.DoesNotContain("mode", gimbalyn.Commands);   // apart once, right, apart once: never apart twice in a row
    }

    [Fact]
    public async Task Turned_off_while_a_try_that_was_not_answered_is_still_pending_Gimbalyn_is_let_go_and_asked_again_until_it_goes()
    {
        var gimbalyn = new FakeGimbalyn { Running = false, Mute = true };
        FlightShaper shaper = ShaperFor(gimbalyn);

        Assert.Contains("did not answer", await shaper.Look(Want(), now: 0));   // started, then no answer in time: nothing fed as far as Studio knows
        Assert.Null(await shaper.Look(Want(on: false), now: 100));
        Assert.Null(await shaper.Look(Want(on: false), now: 200));              // (not at every look)
        Assert.Equal(["profile", "quit"], gimbalyn.Commands);

        gimbalyn.Mute = false;
        Assert.Null(await shaper.Look(Want(on: false), now: 5200));             // the quit that was not answered is asked again
        Assert.Equal(["profile", "quit", "quit"], gimbalyn.Commands);

        gimbalyn.Running = false;                                               // it went
        Assert.Null(await shaper.Look(Want(on: false), now: 10400));
        Assert.Equal(["profile", "quit", "quit"], gimbalyn.Commands);
    }

    [Fact]
    public async Task With_no_profile_fed_Flight_Assist_sends_nothing_and_with_switching_off_it_sends_no_mode()
    {
        var gimbalyn = new FakeGimbalyn { Running = true };
        FlightShaper shaper = ShaperFor(gimbalyn);
        await shaper.Follow(faOff: true, now: 0);   // no profile yet
        Assert.Empty(gimbalyn.Requests);

        await shaper.Use(MakeProfile(switching: false).Profile!);
        gimbalyn.Requests.Clear();
        await shaper.Follow(faOff: true, now: 10);
        await shaper.Follow(faOff: true, now: 5000);

        Assert.DoesNotContain("mode", gimbalyn.Commands);   // (it is only asked how it is, once a second)
        Assert.Equal("running", shaper.State);
    }

    [Fact]
    public async Task What_Gimbalyn_says_of_itself_is_kept_for_the_pages_and_a_vJoy_lost_later_is_noticed_even_without_switching()
    {
        var gimbalyn = new FakeGimbalyn { Running = true };
        FlightShaper shaper = ShaperFor(gimbalyn);
        Assert.Null(shaper.State);

        await shaper.Use(MakeProfile(switching: false).Profile!);
        Assert.Equal(("running", "FA On"), (shaper.State, shaper.Mode));

        gimbalyn.StateAnswer = "busy";   // Joystick Gremlin took vJoy afterwards
        await shaper.Follow(faOff: false, now: 2000);
        Assert.Equal("busy", shaper.State);
    }

    [Fact]
    public async Task The_mode_the_game_is_in_is_what_the_page_is_told_after_a_move()
    {
        var gimbalyn = new FakeGimbalyn { Running = true };
        FlightShaper shaper = ShaperFor(gimbalyn);
        await shaper.Use(MakeProfile().Profile!);

        await shaper.Follow(faOff: true, now: 0);

        Assert.Equal("FA Off", shaper.Mode);
    }

    [Fact]
    public async Task A_Gimbalyn_that_ended_just_after_it_was_fed_is_started_again_only_after_a_few_seconds()
    {
        var gimbalyn = new FakeGimbalyn { Running = true };
        FlightShaper shaper = ShaperFor(gimbalyn);
        Assert.Null(await shaper.Update(Want(), now: 0));

        gimbalyn.Running = false;   // it ended at once (a crash, vJoy's library...)
        Assert.Contains("stopped", await shaper.Update(Want(), now: 250));
        Assert.Contains("stopped", await shaper.Update(Want(), now: 4000));
        Assert.Equal(0, gimbalyn.Launches);

        Assert.Null(await shaper.Update(Want(), now: 5500));
        Assert.Equal(1, gimbalyn.Launches);
    }

    [Fact]
    public async Task The_notes_of_the_last_look_are_kept_and_let_go_with_the_shaper()
    {
        var gimbalyn = new FakeGimbalyn { Running = true };
        FlightShaper shaper = ShaperFor(gimbalyn);

        await shaper.Update(Want(source: c => c == "yaw" ? null : Logitech(c)), now: 0);
        Assert.Contains("yaw", Assert.Single(shaper.Notes));

        await shaper.Update(Want(on: false), now: 100);
        Assert.Empty(shaper.Notes);
        Assert.Null(shaper.State);
    }

    [Fact]
    public void Gimbalyn_is_started_for_this_Studio_and_ends_with_it()
    {
        string[] arguments = GimbalynClient.LaunchArguments(4242);

        Assert.Equal(["--gimbalyn", "--with", "4242"], arguments);
        Dictionary<string, string> options = Program.ParseOptions(arguments);
        Assert.True(options.ContainsKey(Host.Option));
        Assert.Equal("4242", options["with"]);
        Assert.False(options.ContainsKey("data-dir"));   // (that would make it a practice copy)
    }

    [Fact]
    public async Task A_mode_that_was_refused_is_sent_again_at_the_next_look()
    {
        var gimbalyn = new FakeGimbalyn { Running = true };
        FlightShaper shaper = ShaperFor(gimbalyn);
        await shaper.Use(MakeProfile().Profile!);
        gimbalyn.Requests.Clear();

        gimbalyn.Refuses = true;
        await shaper.Follow(faOff: true, now: 0);
        gimbalyn.Refuses = false;
        await shaper.Follow(faOff: true, now: 10);

        Assert.Equal(["mode", "mode"], gimbalyn.Commands);
    }

    [Fact]
    public async Task Reasons_Gimbalyn_cannot_feed_come_back_as_words_and_the_profile_is_sent_again_next_time()
    {
        var gimbalyn = new FakeGimbalyn { Running = true, StateAnswer = "busy", Problem = "vJoy device 1 is held by joystick_gremlin." };
        FlightShaper shaper = ShaperFor(gimbalyn);
        GimbalynProfile profile = MakeProfile().Profile!;

        Assert.Equal("vJoy device 1 is held by joystick_gremlin.", await shaper.Use(profile));

        gimbalyn.StateAnswer = "running";
        gimbalyn.Problem = null;
        Assert.Null(await shaper.Use(profile));
        Assert.Equal(["profile", "start", "profile", "start"], gimbalyn.Commands);
    }

    [Fact]
    public async Task A_Gimbalyn_that_cannot_be_started_is_said()
    {
        var cannot = new FakeGimbalyn { Running = false, CanStart = false };

        Assert.Contains("start", await ShaperFor(cannot).Use(MakeProfile().Profile!), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cannot.Requests);
    }

    // ---- what is wanted, looked at again and again

    static ShaperAsk Want(bool on = true, bool switching = true, bool faOff = false, FlightBinds? binds = null, bool practice = false,
        Func<string, (string, string)?>? source = null) =>
        new(on, switching, faOff, binds ?? OwnersBinds(), FaOnCurves, FaOffCurves, source ?? Logitech, VJoyFolder: "", Practice: practice);

    [Fact]
    public async Task Looking_again_sends_nothing_new_and_Flight_Assist_changing_is_left_to_Follow()
    {
        var gimbalyn = new FakeGimbalyn { Running = false };
        FlightShaper shaper = ShaperFor(gimbalyn);

        Assert.Null(await shaper.Update(Want(faOff: true), now: 0));
        Assert.Null(await shaper.Update(Want(faOff: true), now: 250));
        Assert.Null(await shaper.Update(Want(faOff: false), now: 500));

        Assert.Equal(["profile", "start"], gimbalyn.Commands);
        Assert.Equal("FA Off", (string?)gimbalyn.Requests[0]["profile"]!["mode"]);   // it started in the mode the game was in
    }

    [Fact]
    public async Task Turned_off_Gimbalyn_is_let_go_once_and_not_started_by_looking()
    {
        var gimbalyn = new FakeGimbalyn { Running = false };
        FlightShaper shaper = ShaperFor(gimbalyn);

        Assert.Null(await shaper.Update(Want(on: false), now: 0));   // never fed: nothing to let go
        Assert.Equal(0, gimbalyn.Launches);
        await shaper.Update(Want(), now: 100);
        gimbalyn.Requests.Clear();

        Assert.Null(await shaper.Update(Want(on: false), now: 200));
        Assert.Null(await shaper.Update(Want(on: false), now: 400));

        Assert.Equal(["quit"], gimbalyn.Commands);
    }

    [Fact]
    public async Task A_try_that_failed_waits_a_few_seconds_before_the_next()
    {
        var gimbalyn = new FakeGimbalyn { Running = true, StateAnswer = "busy", Problem = "vJoy device 1 is held by joystick_gremlin." };
        FlightShaper shaper = ShaperFor(gimbalyn);

        Assert.Equal(gimbalyn.Problem, await shaper.Update(Want(), now: 0));
        gimbalyn.Requests.Clear();
        Assert.Equal(gimbalyn.Problem, await shaper.Update(Want(), now: 1000));   // asked about again, not tried again
        Assert.Empty(gimbalyn.Requests);

        gimbalyn.StateAnswer = "running";
        gimbalyn.Problem = null;
        Assert.Null(await shaper.Update(Want(), now: 6000));   // Gremlin was closed meanwhile
        Assert.Equal(["profile", "start"], gimbalyn.Commands);
    }

    [Fact]
    public async Task A_changed_wish_is_tried_at_once_even_after_a_failure()
    {
        var gimbalyn = new FakeGimbalyn { Running = true, StateAnswer = "busy", Problem = "held" };
        FlightShaper shaper = ShaperFor(gimbalyn);
        await shaper.Update(Want(), now: 0);
        gimbalyn.Requests.Clear();

        await shaper.Update(Want(switching: false), now: 500);

        Assert.Equal(["profile", "start"], gimbalyn.Commands);
    }

    [Fact]
    public async Task Without_bindings_or_a_stick_the_reason_is_given_and_Gimbalyn_is_not_touched()
    {
        var gimbalyn = new FakeGimbalyn { Running = false };
        FlightShaper shaper = ShaperFor(gimbalyn);

        string? problem = await shaper.Update(new ShaperAsk(true, true, false, null, FaOnCurves, FaOffCurves, Logitech, ""), now: 0);

        Assert.Contains("bindings", problem);
        Assert.Equal(0, gimbalyn.Launches);
        Assert.Empty(gimbalyn.Requests);
    }

    [Fact]
    public async Task A_practice_copy_never_starts_or_talks_to_Gimbalyn_whatever_is_asked()
    {
        foreach (bool running in new[] { false, true })
        {
            var gimbalyn = new FakeGimbalyn { Running = running };
            FlightShaper shaper = ShaperFor(gimbalyn);

            Assert.Contains("practice", await shaper.Update(Want(practice: true), now: 0), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("practice", await shaper.Update(Want(on: false, practice: true), now: 10), StringComparison.OrdinalIgnoreCase);

            Assert.Equal(0, gimbalyn.Launches);
            Assert.Empty(gimbalyn.Requests);   // not even to let go of the real Studio's Gimbalyn
        }
    }
}
