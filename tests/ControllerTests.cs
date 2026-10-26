using EliteStudio.Overlay;

namespace EliteStudio.Tests;

/// <summary>
/// Made-up controllers for the tests: every reading hands out the next state in the script, and the
/// last one from then on. No test presses a real button or needs a controller on the PC.
/// </summary>
sealed class FakePads(params PadState[][] script)
{
    readonly List<bool> looks = [];

    /// <summary>Per reading so far, whether new controllers were looked for.</summary>
    public bool[] Looks
    {
        get { lock (looks) return [.. looks]; }
    }

    public IReadOnlyList<PadState> Read(bool lookForNew)
    {
        lock (looks)
        {
            looks.Add(lookForNew);
            return script[Math.Min(looks.Count, script.Length) - 1];
        }
    }
}

/// <summary>
/// Controller buttons for the overlay's actions, with made-up controllers: how a button is written
/// and shown, which presses set off which action, what a page's wait for a button hears, and when
/// the buttons are read at all. (What Windows' joystick calls answer on a real controller is the
/// owner's to try.)
/// </summary>
public sealed class ControllerTests
{
    // Maker and product numbers no real controller has, so nothing here depends on what was ever plugged into this PC.
    internal const string StickId = "0000:0001", ThrottleId = "0000:0002";

    /// <summary>The stick with these buttons down (33 to 36: its hat up, right, down, left).</summary>
    internal static PadState Stick(params int[] down) => Pad(StickId, down);

    static PadState Throttle(params int[] down) => Pad(ThrottleId, down);

    static PadState Pad(string device, int[] down) => new(device, down.Aggregate(0UL, (all, button) => all | 1UL << (button - 1)));

    /// <summary>A controller whose readings are handed to it by the test (<see cref="Controller.Take"/>), with made-up times: it is never started, so it reads nothing itself.</summary>
    static Controller ByHand() => new(_ => []);

    /// <summary>The same, told that the player is in the game with these buttons bound.</summary>
    static Controller InGame(OverlaySettings settings)
    {
        Controller controller = ByHand();
        controller.Watch(inGame: true, settings.Cleaned());
        return controller;
    }

    // ---- how a button is written and shown ----

    [Theory]
    [InlineData("0000:0001 7", "Button 7 on Stick")]
    [InlineData("0000:0001 32", "Button 32 on Stick")]
    [InlineData("0000:0001 up", "Hat up on Stick")]
    [InlineData("0000:0001 3 + 0000:0001 7", "Hold 3 + 7 on Stick")]
    [InlineData("0000:0001 3 + 0000:0001 left", "Hold 3 + hat left on Stick")]
    [InlineData("0000:0001 down + 0000:0001 1", "Hold hat down + 1 on Stick")]
    [InlineData("0000:0002 12 + 0000:0001 1", "Hold 12 on Throttle + 1 on Stick")]      // a throttle and a stick are two controllers
    [InlineData("0000:0001 2 + 0000:0001#2 2", "Hold 2 on Stick + 2 on Second stick")]  // two sticks alike
    [InlineData("  0000:0001 7 ", "Button 7 on Stick")]
    public void Buttons_are_kept_as_text_and_shown_in_words(string text, string shown)
    {
        PadChord? buttons = PadChord.Parse(text);

        Assert.NotNull(buttons);
        Assert.Equal(text.Trim(), buttons.Value.ToString());
        Assert.Equal(shown, buttons.Value.Label(device => device switch { StickId => "Stick", ThrottleId => "Throttle", _ => "Second stick" }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("7")]
    [InlineData("0000:0001")]
    [InlineData("0000:0001 0")]
    [InlineData("0000:0001 33")]                                  // the calls know 32 buttons
    [InlineData("0000:0001 sideways")]
    [InlineData("000g:0001 7")]
    [InlineData("0000:0001 7 + 0000:0001 7")]                     // held and pressed: one button cannot be both
    [InlineData("0000:0001 1 + 0000:0001 2 + 0000:0001 3")]
    [InlineData("Stick 1: button 7")]
    [InlineData(@"0000:0001\..\Mouse 7")]
    public void Text_that_is_no_button_is_none(string? text) => Assert.Null(PadChord.Parse(text));

    [Fact]
    public void A_controller_windows_has_no_name_for_is_called_by_its_numbers()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Controller names are looked up in Windows' registry (the overlay is Windows only).");
        Assert.Equal("controller 0000:0001", WindowsPads.NameOf("0000:0001"));
        Assert.Equal("controller 0000:0001 #2", WindowsPads.NameOf("0000:0001#2"));
    }

    [Theory]
    [InlineData(65535, 0)]         // not pushed
    [InlineData(0, 1)]             // up
    [InlineData(2000, 1)]
    [InlineData(4500, 1 | 2)]      // to a corner: two at once
    [InlineData(9000, 2)]          // right
    [InlineData(13500, 2 | 4)]
    [InlineData(18000, 4)]         // down
    [InlineData(22500, 4 | 8)]
    [InlineData(27000, 8)]         // left
    [InlineData(31500, 8 | 1)]
    [InlineData(35999, 1)]
    public void The_hat_is_four_buttons(int direction, int buttons) => Assert.Equal((ulong)buttons, WindowsPads.HatButtons((uint)direction));

    // ---- which press sets off which action ----

    [Fact]
    public void A_bound_button_sets_off_its_action_once_for_every_press()
    {
        Controller pad = InGame(new OverlaySettings { PadMarket = "0000:0001 7", PadNav = "0000:0001 right" });

        Assert.Empty(pad.Take([Stick()], 0));
        Assert.Equal(new[] { "market" }, pad.Take([Stick(7)], 30));
        Assert.Empty(pad.Take([Stick(7)], 60));              // still held: one press
        Assert.Empty(pad.Take([Stick()], 90));
        Assert.Equal(new[] { "market" }, pad.Take([Stick(7)], 120));
        Assert.Empty(pad.Take([Stick(1, 2)], 150));          // buttons nothing is bound to
        Assert.Equal(new[] { "market" }, pad.Take([Stick(1, 2, 7)], 180));   // whatever else is held: the trigger, say
        Assert.Equal(new[] { "nav" }, pad.Take([Stick(1, 34)], 210));  // the hat pushed right
        Assert.Empty(pad.Take([Stick(1, 34, 35)], 240));     // on to the corner: "right" was not pressed again
    }

    [Fact]
    public void What_is_down_when_the_reading_begins_was_not_pressed_just_then()
    {
        Controller pad = InGame(new OverlaySettings { PadMarket = "0000:0001 7", PadNav = "0000:0002 1" });

        Assert.Empty(pad.Take([Stick(7)], 0));               // held while clicking into the game, or a switch left on
        Assert.Empty(pad.Take([Stick(7)], 30));
        Assert.Empty(pad.Take([Stick()], 60));
        Assert.Equal(new[] { "market" }, pad.Take([Stick(7)], 90));

        // A controller that comes later (plugged in, or woken up) is treated the same, wherever Windows puts it in the list.
        Assert.Empty(pad.Take([Throttle(1), Stick(7)], 120));
        Assert.Empty(pad.Take([Throttle(), Stick()], 150));
        Assert.Equal(new[] { "nav", "market" }, pad.Take([Throttle(1), Stick(7)], 180));
        // ... and one that went away and is back with a button down has not pressed it either.
        Assert.Empty(pad.Take([Stick()], 210));
        Assert.Empty(pad.Take([Stick(), Throttle(1)], 240));
    }

    [Fact]
    public void Holding_one_button_and_pressing_another_is_an_action_of_its_own()
    {
        Controller pad = InGame(new OverlaySettings
        {
            PadMarket = "0000:0001 7",                       // 7 on its own
            PadNav = "0000:0001 3 + 0000:0001 7",            // hold 3, press 7
            PadLoadouts = "0000:0002 5 + 0000:0001 1",       // hold 5 on the throttle, press 1 on the stick
        });
        pad.Take([Stick(), Throttle()], 0);

        Assert.Equal(new[] { "market" }, pad.Take([Stick(7), Throttle()], 30));
        pad.Take([Stick(), Throttle()], 60);
        Assert.Empty(pad.Take([Stick(3), Throttle()], 90));                    // the held button alone does nothing
        Assert.Equal(new[] { "nav" }, pad.Take([Stick(3, 7), Throttle()], 120));   // and 7 is "nav" now, not "market" as well
        pad.Take([Stick(3), Throttle()], 150);
        Assert.Equal(new[] { "nav" }, pad.Take([Stick(3, 7), Throttle()], 180));   // 3 stays held, 7 again
        pad.Take([Stick(), Throttle()], 210);
        Assert.Equal(new[] { "nav" }, pad.Take([Stick(3, 7), Throttle()], 240));   // both between two readings: taken as held and pressed
        pad.Take([Stick(), Throttle()], 270);

        Assert.Equal(new[] { "market" }, pad.Take([Stick(7), Throttle()], 300));
        Assert.Empty(pad.Take([Stick(7, 3), Throttle()], 330));                // the wrong way round: 3 is nobody's button to press
        pad.Take([Stick(), Throttle()], 360);

        Assert.Empty(pad.Take([Stick(1), Throttle()], 390));                   // 1 without its held button: bound to nothing
        pad.Take([Stick(), Throttle(5)], 420);
        Assert.Equal(new[] { "loadouts" }, pad.Take([Stick(1), Throttle(5)], 450));
        pad.Take([Stick(), Throttle(5)], 480);
        Assert.Empty(pad.Take([Stick(1)], 510));                               // the throttle has gone: its button is not held
    }

    [Fact]
    public void Of_two_actions_bound_alike_the_first_gets_the_press_and_outside_the_game_nobody_does()
    {
        var settings = new OverlaySettings { PadMarket = "0000:0001 4", PadHaul = "0000:0001 4", PadNav = "0000:0001 2 + 0000:0001 4", PadLoadouts = "0000:0001 2 + 0000:0001 4" };
        Controller pad = InGame(settings);
        pad.Take([Stick()], 0);

        Assert.Equal(new[] { "haul" }, pad.Take([Stick(4)], 30));          // "haul" comes before "market" in the hotkeys' order
        pad.Take([Stick(2)], 60);
        Assert.Equal(new[] { "nav" }, pad.Take([Stick(2, 4)], 90));        // and "nav" before "loadouts"
        pad.Take([Stick()], 120);

        pad.Watch(inGame: false, settings);                                // another program is in front
        Assert.Empty(pad.Take([Stick(4)], 150));
        pad.Take([Stick()], 180);
        pad.Watch(inGame: true, settings with { PadHaul = "" });           // back in the game, with a button taken off
        Assert.Equal(new[] { "market" }, pad.Take([Stick(4)], 210));
    }

    // ---- what a page's wait for a button hears ----

    [Fact]
    public async Task A_wait_hears_one_button_once_it_is_let_go()
    {
        Controller pad = InGame(new OverlaySettings { PadMarket = "0000:0001 7" });
        Task<Heard> heard = pad.Listen();

        pad.Take([Stick(9)], 0);                             // a switch that is on all the time
        Assert.Empty(pad.Take([Stick(9, 7)], 30));           // heard for the page: it sets nothing off, although 7 is "market"
        pad.Take([Stick(9, 7)], 5000);
        Assert.False(heard.IsCompleted);                     // still held: a second button may follow
        pad.Take([Stick(9)], 5030);

        Assert.Equal(new Heard("0000:0001 7", null), await heard);
        Assert.Equal(new[] { "market" }, pad.Take([Stick(9, 7)], 5060));   // the wait is over: buttons work again
    }

    [Fact]
    public async Task A_wait_hears_hold_one_press_another()
    {
        Controller pad = ByHand();

        Task<Heard> heard = pad.Listen();
        pad.Take([Stick(9), Throttle()], 0);
        pad.Take([Stick(9, 3), Throttle()], 30);
        pad.Take([Stick(9, 3, 7), Throttle()], 400);
        Assert.Equal(new Heard("0000:0001 3 + 0000:0001 7", null), await heard);   // the switch that was on is no part of it

        heard = pad.Listen();                                // the next wait begins while 3 and 7 are still down: neither is pressed anew
        pad.Take([Stick(9, 3, 7), Throttle()], 430);
        pad.Take([Stick(9), Throttle(5)], 460);
        pad.Take([Stick(9, 34), Throttle(5)], 900);          // held on the throttle, the stick's hat pushed right
        Assert.Equal(new Heard("0000:0002 5 + 0000:0001 right", null), await heard);

        heard = pad.Listen();
        pad.Take([Stick(9), Throttle()], 930);
        pad.Take([Stick(9), Throttle(5)], 960);
        pad.Take([Stick(9), Throttle()], 990);               // let go ...
        Assert.Equal(new Heard("0000:0002 5", null), await heard);   // ... so it is that button alone, whatever comes after
    }

    [Fact]
    public async Task A_virtual_stick_that_repeats_the_real_ones_buttons_is_not_heard_as_a_second_button()
    {
        // vJoy with Joystick Gremlin: every button of the real stick goes down on the virtual one a moment later.
        Controller pad = ByHand();

        Task<Heard> heard = pad.Listen();
        pad.Take([Stick(), Throttle()], 0);
        pad.Take([Stick(1), Throttle()], 30);
        pad.Take([Stick(1), Throttle(1)], 60);
        pad.Take([Stick(), Throttle()], 300);
        Assert.Equal(new Heard("0000:0001 1", null), await heard);

        heard = pad.Listen();                                // both in one reading: the same press too
        pad.Take([Stick(1), Throttle(1)], 330);
        pad.Take([Stick(), Throttle()], 600);
        Assert.Equal(new Heard("0000:0001 1", null), await heard);

        heard = pad.Listen();                                // held, and after a moment another: both sticks repeat it, the first one heard counts
        pad.Take([Stick(1), Throttle(1)], 630);
        pad.Take([Stick(1, 2), Throttle(1, 2)], 630 + Controller.SamePressMs);
        Assert.Equal(new Heard("0000:0001 1 + 0000:0001 2", null), await heard);
    }

    [Fact]
    public async Task A_wait_ends_by_itself_and_a_new_one_ends_the_one_before()
    {
        Controller pad = ByHand();

        Task<Heard> heard = pad.Listen();
        pad.Take([Stick()], 1000);
        pad.Take([Stick()], 1000 + Controller.ListenMs - 1);
        Assert.False(heard.IsCompleted);
        pad.Take([Stick()], 1000 + Controller.ListenMs);
        Assert.Equal(new Heard(null, "No button was pressed. Click the box, then press a button on your controller within 10 seconds."), await heard);

        heard = pad.Listen();                                // a button still held when the time is up is the button
        pad.Take([Stick()], 20_000);
        pad.Take([Stick(6)], 25_000);
        pad.Take([Stick(6)], 20_000 + Controller.ListenMs);
        Assert.Equal(new Heard("0000:0001 6", null), await heard);

        Task<Heard> first = pad.Listen(), second = pad.Listen();
        Assert.Equal(default, await first);                  // ended with nothing heard and nothing to say
        Assert.False(second.IsCompleted);
        pad.StopListening();
        Assert.Equal(default, await second);
        pad.StopListening();                                 // nobody waits: nothing happens
    }

    // ---- when the buttons are read at all ----

    [Fact]
    public async Task Nothing_is_read_until_somebody_wants_the_buttons()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        var pads = new FakePads([Stick()], [Stick(7)], [Stick()]);
        using var pad = new Controller(pads.Read);
        var pressed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        pad.Pressed += action => pressed.TrySetResult(action);
        var settings = new OverlaySettings { PadMarket = "0000:0001 7" };

        pad.Start();
        pad.Watch(inGame: false, settings);                  // a button is bound, but the player is not in the game
        pad.Watch(inGame: true, new OverlaySettings());      // in the game, with no button bound: a keyboard player
        await Task.Delay(150, cancel);
        Assert.Empty(pads.Looks);

        pad.Watch(inGame: true, settings);
        Assert.Equal("market", await pressed.Task.WaitAsync(TimeSpan.FromSeconds(10), cancel));
        // New controllers were looked for once, when the reading began; after that only the known ones are read.
        Assert.Equal(new[] { true, false }, pads.Looks.Take(2));
        Assert.DoesNotContain(true, pads.Looks.Skip(1));

        pad.Watch(inGame: false, settings);                  // out of the game: the reading stops
        await Task.Delay(200, cancel);
        int reads = pads.Looks.Length;
        await Task.Delay(200, cancel);
        Assert.Equal(reads, pads.Looks.Length);
    }

    [Fact]
    public async Task A_page_that_waits_is_read_for_wherever_the_player_is_and_told_when_no_controller_is_there()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        var none = new FakePads([[]]);                       // every reading: no controller at all
        using var without = new Controller(none.Read);
        without.Start();

        Heard heard = await without.Listen().WaitAsync(TimeSpan.FromSeconds(10), cancel);

        Assert.Equal(new Heard(null, "Windows shows Studio no game controller. Plug in your stick or gamepad, then click the box again."), heard);
        Assert.Equal(new[] { true }, none.Looks);            // looked for, in case one was plugged in a moment ago; and the reading stops again

        // Button 3 held for a good quarter of a second of real readings, then 7 with it.
        var pads = new FakePads([[Stick()], .. Enumerable.Repeat<PadState[]>([Stick(3)], 10), [Stick(3, 7)]]);
        using var pad = new Controller(pads.Read);
        pad.Start();
        heard = await pad.Listen().WaitAsync(TimeSpan.FromSeconds(10), cancel);
        Assert.Equal(new Heard("0000:0001 3 + 0000:0001 7", null), heard);
        Assert.True(pads.Looks[0]);
    }
}
