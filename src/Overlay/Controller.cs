using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using EliteStudio.App;
using Microsoft.Win32;

namespace EliteStudio.Overlay;

/// <summary>
/// One game controller as Windows shows it this moment. Device says which one: its maker's and its
/// product's number as Windows knows them ("046D:C215"), with "#2" behind the second of two alike.
/// Down holds the buttons that are pressed: button 1 in the lowest bit up to button 32, then the hat
/// pushed up, right, down and left as buttons 33 to 36.
/// </summary>
public readonly record struct PadState(string Device, ulong Down);

/// <summary>One button of one controller, named and numbered as in <see cref="PadState"/>.</summary>
internal readonly record struct PadButton(string Device, int Number)
{
    internal static readonly string[] Hat = ["up", "right", "down", "left"];

    /// <summary>For the player: "7", or "hat up".</summary>
    public string Name => Number <= 32 ? Number.ToString(CultureInfo.InvariantCulture) : "hat " + Hat[Number - 33];

    /// <summary>As the settings hold it: "046D:C215 7", "046D:C215 up".</summary>
    public override string ToString() => $"{Device} {(Number <= 32 ? Name : Hat[Number - 33])}";
}

/// <summary>
/// What an overlay action is bound to on a controller: one button, or one button to hold while
/// another is pressed (the two may be on different controllers: a throttle and a stick).
/// </summary>
internal readonly partial record struct PadChord(PadButton? Hold, PadButton Press)
{
    const string Button = @"([0-9A-F]{4}:[0-9A-F]{4}(?:#\d{1,2})?) ([1-9]|[12]\d|3[0-2]|up|right|down|left)";

    /// <summary>As the settings hold it: "046D:C215 7", or "046D:C215 3 + 046D:C215 7" for "hold 3, press 7".</summary>
    public override string ToString() => Hold is { } hold ? $"{hold} + {Press}" : Press.ToString();

    /// <summary>Reads what <see cref="ToString"/> wrote. Null for anything else: the settings file can be edited by hand, and a page may send anything.</summary>
    public static PadChord? Parse(string? text)
    {
        Match parts = Text().Match(text?.Trim() ?? "");
        if (!parts.Success) return null;
        static PadButton Read(Group device, Group button) =>
            new(device.Value, int.TryParse(button.Value, out int number) ? number : 33 + Array.IndexOf(PadButton.Hat, button.Value));
        PadButton press = Read(parts.Groups[3], parts.Groups[4]);
        PadButton? hold = parts.Groups[1].Success ? Read(parts.Groups[1], parts.Groups[2]) : null;
        return hold == press ? null : new PadChord(hold, press);   // no button can be held and then pressed
    }

    /// <summary>
    /// For the player: "Button 7 on Logitech Extreme 3D", "Hat up on ...", "Hold 3 + 7 on ...", and
    /// with two controllers "Hold 3 on ... + 7 on ...". The buttons come first, so they stay in view
    /// in a box too narrow for the whole line.
    /// </summary>
    /// <param name="nameOf">The name of a controller, from what <see cref="PadState.Device"/> holds.</param>
    public string Label(Func<string, string> nameOf)
    {
        if (Hold is not { } hold) return $"{(Press.Number <= 32 ? "Button " + Press.Name : "Hat " + PadButton.Hat[Press.Number - 33])} on {nameOf(Press.Device)}";
        return hold.Device == Press.Device ? $"Hold {hold.Name} + {Press.Name} on {nameOf(Press.Device)}"
            : $"Hold {hold.Name} on {nameOf(hold.Device)} + {Press.Name} on {nameOf(Press.Device)}";
    }

    [GeneratedRegex($@"^(?:{Button} \+ )?{Button}$")]
    private static partial Regex Text();
}

/// <summary>
/// What came of a page's wait for a controller button: the buttons as the settings hold them, or
/// what to tell the player when there are none. Neither when something else ended the wait.
/// </summary>
public readonly record struct Heard(string? Buttons, string? Said);

/// <summary>
/// Game controller buttons as a second way to the overlay's actions, for players who fly with a
/// stick, a HOTAS or a gamepad and have no keyboard in reach. An action is given one button, or
/// "hold this button and press that one", so that no button has to be taken away from the game:
/// Studio only reads the buttons, and the game still sees every press.
///
/// The buttons are read about thirty times a second, and only while somebody wants them: the
/// player is in the game and an action has a button, or a page waits for the next button to bind
/// (<see cref="Listen"/>). Everything here happens on the thread that called <see cref="Start"/>
/// except the reading itself, because Windows can take its time over that.
/// </summary>
public sealed class Controller : IDisposable
{
    /// <summary>How long a page's wait for a button lasts.</summary>
    internal const int ListenMs = 10_000;
    /// <summary>
    /// A second button this soon after the first is the same press seen twice, not "hold one, press
    /// another": a virtual stick (vJoy with Joystick Gremlin) repeats every button of the real one.
    /// </summary>
    internal const int SamePressMs = 150;
    const int ReadEveryMs = 20;     // Windows rounds this up to its clock's next tick: about thirty readings a second, and a tap on a button lasts longer
    const int LookEveryMs = 3000;   // how often a bound controller that is not there is looked for

    readonly Func<bool, IReadOnlyList<PadState>> read;
    OverlaySettings? boundFrom;                         // the settings "bound" was made from
    (string Action, PadChord Buttons)[] bound = [];     // in the order of the hotkeys
    Dictionary<string, ulong> down = [];                // per controller, the buttons that were down at the last reading
    bool inGame, lookNow, stopped;
    TaskCompletionSource? asleep;                       // what the reading waits on while nobody wants the buttons
    string? readProblem;
    TaskCompletionSource<Heard>? wait;                  // a page waits for the next button
    long waitUntil;                                     // 0 until the first reading of that wait
    (PadButton Button, long At)? first;                 // the first button pressed during that wait

    /// <param name="read">
    /// How the buttons are read: null for Windows' own joystick calls; tests put made-up controllers
    /// here. Given true it looks for controllers that have come since last time, which can be slow;
    /// given false it reads only those it knows of. It is called off the UI thread, never twice at once.
    /// </param>
    public Controller(Func<bool, IReadOnlyList<PadState>>? read) => this.read = read ?? new WindowsPads().Read;

    /// <summary>
    /// A bound button was pressed in the game (or one of a bound pair, the other being held): the
    /// name of its action, as <see cref="Hotkeys.Pressed"/> names them.
    /// </summary>
    public event Action<string>? Pressed;

    /// <summary>Starts the reading. Until someone wants the buttons, nothing is read.</summary>
    public void Start() => _ = ReadOn();

    /// <summary>
    /// Says whether the player is in the game, and which buttons are bound. Call on every tick and
    /// whenever the settings changed.
    /// </summary>
    public void Watch(bool inGame, OverlaySettings settings)
    {
        this.inGame = inGame;
        if (!ReferenceEquals(settings, boundFrom))
        {
            boundFrom = settings;
            bound = [.. from pad in settings.PadButtons() let buttons = PadChord.Parse(pad.Buttons) where buttons is not null select (pad.Action, buttons.Value)];
        }
        Wake();
    }

    /// <summary>
    /// Waits for the player to press a button on any controller, or to hold one and press another,
    /// whichever program is in front: a page asks for this while Studio's own window is. The wait
    /// ends after <see cref="ListenMs"/> at the latest. A new wait ends the one before it.
    /// </summary>
    public Task<Heard> Listen()
    {
        StopListening();
        wait = new TaskCompletionSource<Heard>(TaskCreationOptions.RunContinuationsAsynchronously);
        lookNow = true;   // the controller to bind may have been plugged in a moment ago
        Task<Heard> heard = wait.Task;
        Wake();
        return heard;
    }

    /// <summary>Ends a page's wait, if there is one, with no button heard.</summary>
    public void StopListening() => Finish(default);

    public void Dispose()
    {
        stopped = true;
        StopListening();
        asleep?.TrySetResult();
    }

    /// <summary>
    /// Takes in one reading of every controller's buttons, and returns the actions set off by what
    /// was pressed since the reading before. "Hold A, press B" goes before B on its own, so B can
    /// be both; of two actions bound alike the first gets the press, as with the keys. While a page
    /// waits for a button the press goes to that page and sets nothing off.
    /// </summary>
    /// <param name="now">The time of the reading, in milliseconds.</param>
    internal IReadOnlyList<string> Take(IReadOnlyList<PadState> pads, long now)
    {
        var pressed = new List<PadButton>();
        foreach (PadState pad in pads)
        {
            // On a controller that was not in the reading before, what is down was not pressed just
            // now: a switch left on, a button held since before the reading began.
            ulong fresh = pad.Down & ~down.GetValueOrDefault(pad.Device, pad.Down);
            for (int number = 1; fresh != 0; number++, fresh >>= 1)
                if ((fresh & 1) != 0) pressed.Add(new PadButton(pad.Device, number));
        }
        down = pads.ToDictionary(pad => pad.Device, pad => pad.Down);

        if (wait is not null)
        {
            Hear(pressed, now);
            return [];
        }
        if (!inGame) return [];
        var actions = new List<string>();
        foreach (PadButton press in pressed)
        {
            var onIt = bound.Where(pad => pad.Buttons.Press == press).ToList();
            actions.AddRange(onIt.Where(pad => pad.Buttons.Hold is { } hold && IsDown(hold))
                .Concat(onIt.Where(pad => pad.Buttons.Hold is null)).Take(1).Select(pad => pad.Action));
        }
        return actions;
    }

    bool IsDown(PadButton button) => (down.GetValueOrDefault(button.Device) >> (button.Number - 1) & 1) != 0;

    bool Wanted => wait is not null || (inGame && bound.Length > 0);

    void Wake()
    {
        if (Wanted) asleep?.TrySetResult();
    }

    /// <summary>
    /// A page waits for a button. The first one pressed is it, once it is let go again; a second one
    /// pressed while the first is still held makes it "hold the first, press the second". Buttons
    /// that were down already (a switch left on) are never part of it.
    /// </summary>
    void Hear(List<PadButton> pressed, long now)
    {
        if (down.Count == 0 && !lookNow)   // looked for since the wait began, and none is there
        {
            Finish(new Heard(null, "Windows shows Studio no game controller. Plug in your stick or gamepad, then click the box again."));
            return;
        }
        if (waitUntil == 0) waitUntil = now + ListenMs;
        foreach (PadButton press in pressed)
        {
            if (first is not { } held) first = (press, now);
            else if (now - held.At >= SamePressMs && IsDown(held.Button))
            {
                Finish(new Heard(new PadChord(held.Button, press).ToString(), null));
                return;
            }
        }
        if (first is { } only && (!IsDown(only.Button) || now >= waitUntil)) Finish(new Heard(new PadChord(null, only.Button).ToString(), null));
        else if (now >= waitUntil) Finish(new Heard(null, $"No button was pressed. Click the box, then press a button on your controller within {ListenMs / 1000} seconds."));
    }

    void Finish(Heard heard)
    {
        TaskCompletionSource<Heard>? ended = wait;
        (wait, waitUntil, first) = (null, 0, null);
        ended?.TrySetResult(heard);
    }

    /// <summary>The reading itself, until <see cref="Dispose"/>: asleep while nobody wants the buttons, else a reading every <see cref="ReadEveryMs"/>.</summary>
    async Task ReadOn()
    {
        long looked = 0;
        while (!stopped)
        {
            if (!Wanted)
            {
                down.Clear();     // what is down when the reading begins again was not pressed just then
                lookNow = true;   // and a controller may have come or gone meanwhile
                await (asleep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                continue;
            }
            try
            {
                // Every place a controller can be on is looked at only when one may have come: when the
                // reading begins, for a page that waits, and every few seconds while a bound one is missing.
                // In between, only the controllers known of are read, which takes no time at all.
                long now = Environment.TickCount64;
                bool look = lookNow || (now - looked >= LookEveryMs
                    && bound.Any(pad => !down.ContainsKey(pad.Buttons.Press.Device) || (pad.Buttons.Hold is { } hold && !down.ContainsKey(hold.Device))));
                if (look) (lookNow, looked) = (false, now);
                IReadOnlyList<PadState> pads = await Task.Run(() => read(look));
                foreach (string action in Take(pads, Environment.TickCount64)) Pressed?.Invoke(action);
                readProblem = null;
            }
            catch (Exception problem)
            {
                // This runs thirty times a second, so a problem that keeps coming back is logged once, not every time.
                if (problem.Message != readProblem) Log.Error("Reading the game controllers, or carrying out a button's action, failed", problem);
                readProblem = problem.Message;
                Finish(new Heard(null, "The game controllers could not be read: " + problem.Message));   // a page must not wait for ever
            }
            await Task.Delay(ReadEveryMs);
        }
    }
}

/// <summary>
/// The controllers as Windows' own joystick calls show them (winmm.dll, part of every Windows; no
/// extra library). What these calls cannot see: buttons beyond the 32nd, a second to fourth hat,
/// more than 16 controllers, and a gamepad's two triggers, which are an axis to them, not buttons.
/// </summary>
internal sealed class WindowsPads
{
    const int Places = 16;                            // Windows numbers the controllers 0 to 15, and may renumber them when one comes or goes
    const uint WantButtons = 0x80, WantHat = 0x200;   // JOY_RETURNBUTTONS, JOY_RETURNPOVCTS
    const uint HasHat = 0x10;                         // JOYCAPS_HASPOV

    readonly (string Device, bool Hat)?[] found = new (string, bool)?[Places];

    /// <summary>
    /// The maker's name for a controller ("Logitech Extreme 3D"), from what <see cref="PadState.Device"/>
    /// holds. Windows keeps the name by maker and product number, also while the controller is
    /// unplugged. (The joystick calls themselves name every controller "Microsoft PC-joystick driver".)
    /// </summary>
    public static string NameOf(string device)
    {
        string[] ids = device.Split(':', '#');
        using RegistryKey? known = Registry.CurrentUser.OpenSubKey($@"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{ids[0]}&PID_{ids[1]}");
        string name = known?.GetValue("OEMName") is string { Length: > 0 } named ? named : $"controller {ids[0]}:{ids[1]}";
        return ids.Length > 2 ? $"{name} #{ids[2]}" : name;
    }

    /// <summary>Every controller that answers, with its buttons that are down.</summary>
    /// <param name="lookForNew">Look at all the places, not only at those a controller was on last time, and name them afresh.</param>
    public IReadOnlyList<PadState> Read(bool lookForNew)
    {
        var pads = new List<PadState>();
        for (int place = 0; place < Places; place++)
        {
            if (found[place] is null && !lookForNew) continue;
            var now = new JoyInfo { Size = 52, Flags = WantButtons | WantHat };
            if (joyGetPosEx((uint)place, ref now) != 0 || (lookForNew && !Name(place)))
            {
                found[place] = null;   // nothing there (any more)
                continue;
            }
            (string device, bool hat) = found[place]!.Value;
            pads.Add(new PadState(device, now.Buttons | (hat ? HatButtons(now.Hat) << 32 : 0)));
        }
        return pads;
    }

    /// <summary>
    /// The hat as four buttons: up, right, down, left. Windows gives its direction in hundredths of a
    /// degree, clockwise from up, and 65535 while it is not pushed. Pushed to a corner, two are down.
    /// </summary>
    internal static ulong HatButtons(uint direction) => direction > 35999 ? 0
        : (direction is <= 4500 or >= 31500 ? 1UL : 0) | (direction is >= 4500 and <= 13500 ? 2UL : 0)
        | (direction is >= 13500 and <= 22500 ? 4UL : 0) | (direction is >= 22500 and <= 31500 ? 8UL : 0);

    /// <summary>Notes which controller is on a place. False when Windows will not say.</summary>
    bool Name(int place)
    {
        if (joyGetDevCapsW((UIntPtr)place, out JoyCaps caps, 728) != 0) return false;
        string device = $"{caps.Maker:X4}:{caps.Product:X4}";
        int alike = found.Take(place).Count(earlier => earlier?.Device.Split('#')[0] == device);
        // A controller without a hat can still report one, pushed up for ever (seen with vJoy): only a hat it says it has counts.
        found[place] = (alike == 0 ? device : $"{device}#{alike + 1}", (caps.Has & HasHat) != 0);
        return true;
    }

    [StructLayout(LayoutKind.Explicit, Size = 52)]    // JOYINFOEX; only the parts used are named
    struct JoyInfo
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(32)] public uint Buttons;
        [FieldOffset(40)] public uint Hat;
    }

    [StructLayout(LayoutKind.Explicit, Size = 728)]   // JOYCAPSW; only the parts used are named
    struct JoyCaps
    {
        [FieldOffset(0)] public ushort Maker;
        [FieldOffset(2)] public ushort Product;
        [FieldOffset(128)] public uint Has;
    }

    [DllImport("winmm.dll")]
    static extern uint joyGetPosEx(uint place, ref JoyInfo now);

    [DllImport("winmm.dll")]
    static extern uint joyGetDevCapsW(UIntPtr place, out JoyCaps caps, uint size);
}
