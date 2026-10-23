using System.Runtime.InteropServices;

namespace EliteStudio.Flight;

/// <summary>
/// Xbox-style game pads as Elite itself reads them: through XInput (Elite's own program asks xinput9_1_0.dll), not Windows'
/// older joystick calls. Each stick and trigger comes by the game's own name ("LStickX", "RStickY", "LTrigger"), so there is
/// nothing to guess or to find with Detect. And XInput answers whichever program is in front, where the older calls give a
/// newer Xbox pad's readings only to the program in front (the game, while the player flies).
/// </summary>
/// <remarks>
/// A pad is a <see cref="StickState"/> whose device is "XInput#1" to "XInput#4" by its XInput slot. Its sticks run from -1 to 1
/// with right and DOWN positive, as Windows' joystick calls give every stick (Studio's pictures draw that way). XInput's own up
/// is positive, and Elite takes it as it comes: the game's pad presets have pitch on "LStickY" not inverted where its stick
/// presets invert Windows' down-positive Y (and walking ahead is "LStickY" not inverted against "Joy_YAxis" inverted). So a pad's
/// Y is turned over here, and the game's invert for it turned over too (<see cref="FlightControls"/>). Its triggers run from 0
/// (let go) to 1. Its buttons are in <see cref="StickState.Down"/> by <see cref="Bit"/>.
/// </remarks>
internal sealed class XInputPads(Func<uint, PadReading?>? read = null)
{
    /// <summary>The start of a pad's device: the slot follows ("XInput#1").</summary>
    public const string Prefix = "XInput#";
    const int Slots = 4;   // XInput knows four pads (XUSER_MAX_COUNT)

    readonly Func<uint, PadReading?> read = read ?? Native.Read;
    readonly bool[] there = new bool[Slots];

    /// <summary>The pads that answer. Empty slots are asked only when <paramref name="lookForNew"/> (Microsoft: not every frame).</summary>
    public IReadOnlyList<StickState> Read(bool lookForNew)
    {
        var pads = new List<StickState>();
        for (uint slot = 0; slot < Slots; slot++)
        {
            if (!there[slot] && !lookForNew) continue;
            PadReading? now = read(slot);
            there[slot] = now is not null;
            if (now is { } pad) pads.Add(State(slot, pad));
        }
        return pads;
    }

    /// <summary>One reading as Studio's sticks are: the game's names for the axes, -1 to 1 down and right positive (0 to 1 for a trigger).</summary>
    internal static StickState State(uint slot, PadReading pad) => new(
        Prefix + (slot + 1),
        slot == 0 ? "Xbox-style controller" : $"Xbox-style controller {slot + 1}",
        new Dictionary<string, double>
        {
            ["LStickX"] = Stick(pad.ThumbLX), ["LStickY"] = -Stick(pad.ThumbLY),
            ["RStickX"] = Stick(pad.ThumbRX), ["RStickY"] = -Stick(pad.ThumbRY),
            ["LTrigger"] = pad.LeftTrigger / 255.0, ["RTrigger"] = pad.RightTrigger / 255.0,
        },
        Buttons(pad));

    /// <summary>A stick from XInput's -32768 to 32767 as -1 to 1, each side by its own end (so full is 1 either way).</summary>
    internal static double Stick(short value) => value < 0 ? value / 32768.0 : value / 32767.0;

    /// <summary>
    /// The game's names for a pad's buttons (both spellings: "GamePad_RBumper" and the older "Pad_RBumper"; a trigger, with or
    /// without "Pos_", pulled past XInput's own threshold) as bits of <see cref="StickState.Down"/>; null for a name that is not a pad's button.
    /// </summary>
    internal static int? Bit(string key)
    {
        string name = key.StartsWith("GamePad_", StringComparison.Ordinal) ? key[8..]
            : key.StartsWith("Pad_", StringComparison.Ordinal) ? key[4..]
            : key.StartsWith("Pos_GamePad_", StringComparison.Ordinal) ? "Pos_" + key[12..]
            : key.StartsWith("Pos_Pad_", StringComparison.Ordinal) ? "Pos_" + key[8..]
            : "";
        return name switch
        {
            "FaceDown" or "A" => 0, "FaceRight" or "B" => 1, "FaceLeft" or "X" => 2, "FaceUp" or "Y" => 3,
            "LBumper" => 4, "RBumper" => 5, "Back" => 6, "Start" => 7, "LThumb" => 8, "RThumb" => 9,
            "DPadUp" => 10, "DPadRight" => 11, "DPadDown" => 12, "DPadLeft" => 13,
            "LTrigger" or "Pos_LTrigger" => 14, "RTrigger" or "Pos_RTrigger" => 15,   // a trigger bound to a button press (fire)
            _ => null,
        };
    }

    static ulong Buttons(PadReading pad)
    {
        // XInput's masks (xinput.h) in the order of Bit: A, B, X, Y, the shoulders, Back, Start, the stick clicks, the pad's cross
        ReadOnlySpan<ushort> masks = [0x1000, 0x2000, 0x4000, 0x8000, 0x0100, 0x0200, 0x0020, 0x0010, 0x0040, 0x0080, 0x0001, 0x0008, 0x0002, 0x0004];
        ulong down = 0;
        for (int i = 0; i < masks.Length; i++) if ((pad.Buttons & masks[i]) != 0) down |= 1UL << i;
        const byte Threshold = 30;   // XINPUT_GAMEPAD_TRIGGER_THRESHOLD
        if (pad.LeftTrigger > Threshold) down |= 1UL << 14;
        if (pad.RightTrigger > Threshold) down |= 1UL << 15;
        return down;
    }

    /// <summary>XInput itself: xinput1_4.dll (in every Windows from 8 on), else xinput9_1_0.dll (from Vista on, the one Elite asks).</summary>
    static class Native
    {
        const uint Connected = 0;   // ERROR_SUCCESS; ERROR_DEVICE_NOT_CONNECTED (1167) for an empty slot
        static int which;           // 0 not tried yet, 1 xinput1_4, 2 xinput9_1_0, 3 neither is there

        public static PadReading? Read(uint slot)
        {
            State state;
            uint answer;
            try
            {
                if (which <= 1) (answer, which) = (GetState14(slot, out state), 1);
                else if (which == 2) answer = GetState910(slot, out state);
                else return null;
            }
            catch (Exception problem) when (problem is DllNotFoundException or EntryPointNotFoundException)
            {
                which = which <= 1 ? 2 : 3;   // the other one, then none
                return which == 2 ? Read(slot) : null;
            }
            if (answer != Connected) return null;
            Gamepad g = state.Pad;
            return new PadReading(g.Buttons, g.LeftTrigger, g.RightTrigger, g.ThumbLX, g.ThumbLY, g.ThumbRX, g.ThumbRY);
        }

        [StructLayout(LayoutKind.Sequential)]   // XINPUT_GAMEPAD: 12 bytes
        struct Gamepad
        {
            public ushort Buttons;
            public byte LeftTrigger, RightTrigger;
            public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]   // XINPUT_STATE: 16 bytes
        struct State
        {
            public uint PacketNumber;
            public Gamepad Pad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        static extern uint GetState14(uint slot, out State state);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        static extern uint GetState910(uint slot, out State state);
    }
}

/// <summary>One pad as XInput gives it (XINPUT_GAMEPAD): the button mask, the triggers 0 to 255, the sticks -32768 to 32767 with up and right positive.</summary>
internal readonly record struct PadReading(ushort Buttons, byte LeftTrigger, byte RightTrigger, short ThumbLX, short ThumbLY, short ThumbRX, short ThumbRY);
