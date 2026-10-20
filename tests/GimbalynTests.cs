using System.Text.Json;
using System.Text.Json.Nodes;
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
}
