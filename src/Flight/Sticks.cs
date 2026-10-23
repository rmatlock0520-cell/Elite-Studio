using System.Runtime.InteropServices;
using EliteStudio.Overlay;

namespace EliteStudio.Flight;

/// <summary>
/// One game controller this moment, axes and all: how the flight overlay and the trainer see a stick.
/// Device as <see cref="PadState.Device"/> writes it ("046D:C215", "#2" behind the second of two
/// alike). Axes are Windows' six, each from -1 to 1 (null where the device has none): X, Y, Z, R
/// (the twist or rudder, which the game calls RZ), U and V. Buttons as <see cref="PadState.Down"/>.
/// </summary>
public sealed record StickState(string Device, string Name, IReadOnlyDictionary<string, double> Axes, ulong Down);

/// <summary>
/// The sticks as Windows' own joystick calls show them (winmm.dll, as <see cref="WindowsPads"/>, which
/// reads only the buttons). Those calls know six axes by their own letters; which of the game's names
/// each one is depends on the device (a throttle slider can be Z on one and U on another), so a page
/// can have the player move a control to find out (see the Flight setup page).
/// </summary>
internal sealed class WindowsSticks
{
    const int Places = 16;
    const uint WantAll = 0xFF | 0x200;                 // JOY_RETURNALL | JOY_RETURNPOVCTS
    const uint HasZ = 0x1, HasR = 0x2, HasU = 0x4, HasV = 0x8, HasHat = 0x10;
    internal static readonly string[] Letters = ["X", "Y", "Z", "R", "U", "V"];

    sealed record Found(string Device, string Name, bool Hat, (uint Min, uint Max)?[] Ranges);
    readonly Found?[] found = new Found?[Places];
    readonly XInputPads xinput = new();   // Xbox-style pads also as Elite reads them (they are in Windows' joystick list too, as a copy)

    /// <summary>Every controller that answers, and after them every Xbox-style pad as XInput gives it (see <see cref="XInputPads"/>).</summary>
    /// <param name="lookForNew">Look at all the places, not only at those a controller was on last time (slow: do it now and then).</param>
    public IReadOnlyList<StickState> Read(bool lookForNew)
    {
        var sticks = new List<StickState>();
        for (int place = 0; place < Places; place++)
        {
            if (found[place] is null && !lookForNew) continue;
            var now = new JoyInfo { Size = 52, Flags = WantAll };
            if (joyGetPosEx((uint)place, ref now) != 0 || (lookForNew && !Name(place)))
            {
                found[place] = null;
                continue;
            }
            Found stick = found[place]!;
            uint[] raw = [now.X, now.Y, now.Z, now.R, now.U, now.V];
            var axes = new Dictionary<string, double>();
            for (int i = 0; i < 6; i++)
                if (stick.Ranges[i] is { } range) axes[Letters[i]] = Scale(raw[i], range.Min, range.Max);
            sticks.Add(new StickState(stick.Device, stick.Name, axes, now.Buttons | (stick.Hat ? WindowsPads.HatButtons(now.Hat) << 32 : 0)));
        }
        sticks.AddRange(xinput.Read(lookForNew));
        return sticks;
    }

    /// <summary>A reading between the device's own ends as -1 to 1, the middle 0.</summary>
    internal static double Scale(uint value, uint min, uint max) =>
        max <= min ? 0 : Math.Clamp((value - (double)min) / (max - (double)min) * 2 - 1, -1, 1);

    bool Name(int place)
    {
        if (joyGetDevCapsW((UIntPtr)place, out JoyCaps caps, 728) != 0) return false;
        string device = $"{caps.Maker:X4}:{caps.Product:X4}";
        int alike = found.Take(place).Count(earlier => earlier?.Device.Split('#')[0] == device);
        string id = alike == 0 ? device : $"{device}#{alike + 1}";
        (uint, uint)?[] ranges =
        [
            (caps.XMin, caps.XMax), (caps.YMin, caps.YMax),
            (caps.Has & HasZ) != 0 ? (caps.ZMin, caps.ZMax) : null,
            (caps.Has & HasR) != 0 ? (caps.RMin, caps.RMax) : null,
            (caps.Has & HasU) != 0 ? (caps.UMin, caps.UMax) : null,
            (caps.Has & HasV) != 0 ? (caps.VMin, caps.VMax) : null,
        ];
        found[place] = new Found(id, WindowsPads.NameOf(id), (caps.Has & HasHat) != 0, ranges);
        return true;
    }

    [StructLayout(LayoutKind.Explicit, Size = 52)]    // JOYINFOEX
    struct JoyInfo
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public uint X;
        [FieldOffset(12)] public uint Y;
        [FieldOffset(16)] public uint Z;
        [FieldOffset(20)] public uint R;
        [FieldOffset(24)] public uint U;
        [FieldOffset(28)] public uint V;
        [FieldOffset(32)] public uint Buttons;
        [FieldOffset(40)] public uint Hat;
    }

    [StructLayout(LayoutKind.Explicit, Size = 728)]   // JOYCAPSW; only the parts used are named
    struct JoyCaps
    {
        [FieldOffset(0)] public ushort Maker;
        [FieldOffset(2)] public ushort Product;
        [FieldOffset(68)] public uint XMin;
        [FieldOffset(72)] public uint XMax;
        [FieldOffset(76)] public uint YMin;
        [FieldOffset(80)] public uint YMax;
        [FieldOffset(84)] public uint ZMin;
        [FieldOffset(88)] public uint ZMax;
        [FieldOffset(104)] public uint RMin;
        [FieldOffset(108)] public uint RMax;
        [FieldOffset(112)] public uint UMin;
        [FieldOffset(116)] public uint UMax;
        [FieldOffset(120)] public uint VMin;
        [FieldOffset(124)] public uint VMax;
        [FieldOffset(128)] public uint Has;
    }

    [DllImport("winmm.dll")]
    static extern uint joyGetPosEx(uint place, ref JoyInfo now);

    [DllImport("winmm.dll")]
    static extern uint joyGetDevCapsW(UIntPtr place, out JoyCaps caps, uint size);
}

/// <summary>
/// The game's names for keyboard keys ("Key_W", "Key_LeftShift", "Key_Numpad_8") as the key's place on
/// the keyboard: its scan code, which is what the game itself goes by, so "Key_Z" is the key left of X
/// whatever the keyboard's language. From the scan code come the key Windows reports for it now (for
/// reading whether it is held) and the number Joystick Gremlin stores a key under.
/// </summary>
public static class KeyNames
{
    // Scan codes of the first set; 0xE000 marks the "extended" keys, which share a code with a key of the number pad.
    static readonly Dictionary<string, int> Scan = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Escape"] = 0x01, ["1"] = 0x02, ["2"] = 0x03, ["3"] = 0x04, ["4"] = 0x05, ["5"] = 0x06, ["6"] = 0x07, ["7"] = 0x08,
        ["8"] = 0x09, ["9"] = 0x0A, ["0"] = 0x0B, ["Minus"] = 0x0C, ["Equals"] = 0x0D, ["Backspace"] = 0x0E, ["Tab"] = 0x0F,
        ["Q"] = 0x10, ["W"] = 0x11, ["E"] = 0x12, ["R"] = 0x13, ["T"] = 0x14, ["Y"] = 0x15, ["U"] = 0x16, ["I"] = 0x17,
        ["O"] = 0x18, ["P"] = 0x19, ["LeftBracket"] = 0x1A, ["RightBracket"] = 0x1B, ["Enter"] = 0x1C, ["LeftControl"] = 0x1D,
        ["A"] = 0x1E, ["S"] = 0x1F, ["D"] = 0x20, ["F"] = 0x21, ["G"] = 0x22, ["H"] = 0x23, ["J"] = 0x24, ["K"] = 0x25,
        ["L"] = 0x26, ["SemiColon"] = 0x27, ["Apostrophe"] = 0x28, ["Grave"] = 0x29, ["LeftShift"] = 0x2A, ["BackSlash"] = 0x2B,
        ["Z"] = 0x2C, ["X"] = 0x2D, ["C"] = 0x2E, ["V"] = 0x2F, ["B"] = 0x30, ["N"] = 0x31, ["M"] = 0x32, ["Comma"] = 0x33,
        ["Period"] = 0x34, ["Slash"] = 0x35, ["RightShift"] = 0x36, ["Numpad_Multiply"] = 0x37, ["LeftAlt"] = 0x38,
        ["Space"] = 0x39, ["CapsLock"] = 0x3A, ["F1"] = 0x3B, ["F2"] = 0x3C, ["F3"] = 0x3D, ["F4"] = 0x3E, ["F5"] = 0x3F,
        ["F6"] = 0x40, ["F7"] = 0x41, ["F8"] = 0x42, ["F9"] = 0x43, ["F10"] = 0x44, ["NumLock"] = 0x45, ["ScrollLock"] = 0x46,
        ["Numpad_7"] = 0x47, ["Numpad_8"] = 0x48, ["Numpad_9"] = 0x49, ["Numpad_Subtract"] = 0x4A, ["Numpad_4"] = 0x4B,
        ["Numpad_5"] = 0x4C, ["Numpad_6"] = 0x4D, ["Numpad_Add"] = 0x4E, ["Numpad_1"] = 0x4F, ["Numpad_2"] = 0x50,
        ["Numpad_3"] = 0x51, ["Numpad_0"] = 0x52, ["Numpad_Decimal"] = 0x53, ["F11"] = 0x57, ["F12"] = 0x58,
        ["Numpad_Enter"] = 0xE01C, ["RightControl"] = 0xE01D, ["Numpad_Divide"] = 0xE035, ["RightAlt"] = 0xE038,
        ["Home"] = 0xE047, ["UpArrow"] = 0xE048, ["PageUp"] = 0xE049, ["LeftArrow"] = 0xE04B, ["RightArrow"] = 0xE04D,
        ["End"] = 0xE04F, ["DownArrow"] = 0xE050, ["PageDown"] = 0xE051, ["Insert"] = 0xE052, ["Delete"] = 0xE053,
    };

    /// <summary>
    /// The number pad's digits and point: Windows' key for each with NumLock on (VK_NUMPAD0 to 9, VK_DECIMAL), by scan code,
    /// and the key the same place gives with NumLock off (Home, Up, PgUp...). Windows' own mapping gives only the second,
    /// which a held key never reports while NumLock is on.
    /// </summary>
    static readonly Dictionary<int, (int NumLockOn, int NumLockOff)> NumberPad = new()
    {
        [0x47] = (0x67, 0x24), [0x48] = (0x68, 0x26), [0x49] = (0x69, 0x21), [0x4B] = (0x64, 0x25), [0x4C] = (0x65, 0x0C),
        [0x4D] = (0x66, 0x27), [0x4F] = (0x61, 0x23), [0x50] = (0x62, 0x28), [0x51] = (0x63, 0x22), [0x52] = (0x60, 0x2D),
        [0x53] = (0x6E, 0x2E),
    };

    /// <summary>The key's scan code, with 0xE000 added for an extended key; null for a name Studio does not know.</summary>
    public static int? ScanCode(string? key) =>
        key is not null && Scan.TryGetValue(key.StartsWith("Key_", StringComparison.OrdinalIgnoreCase) ? key[4..] : key, out int code) ? code : null;

    /// <summary>How Joystick Gremlin numbers a key in a profile: the extended flag times 256, plus the scan code (Z is 44).</summary>
    public static int? GremlinId(string key) => ScanCode(key) is { } code ? ((code & 0xE000) != 0 ? 256 : 0) | (code & 0xFF) : null;

    /// <summary>
    /// The key Windows reports for that place on the keyboard with the keyboard language in use now; 0 when none. A
    /// number-pad digit is its NumLock-on key (VK_NUMPAD8 for Key_Numpad_8); <see cref="Held"/> also takes its NumLock-off one.
    /// </summary>
    public static int VirtualKey(string key) => ScanCode(key) is not { } code ? 0
        : NumberPad.TryGetValue(code, out var pad) ? pad.NumLockOn
        : (int)MapVirtualKeyW((uint)code, 3);   // MAPVK_VSC_TO_VK_EX

    /// <summary>
    /// Whether a key is held down this moment, whichever program is in front. A number-pad digit with NumLock off comes as
    /// its other key (8 as Up), which the grey arrow keys share, so for a digit either counts: Windows cannot tell them apart
    /// here, and NumLock's state cannot be read reliably from a program that is not in front.
    /// </summary>
    internal static bool Held(int virtualKey)
    {
        if (virtualKey == 0) return false;
        if ((GetAsyncKeyState(virtualKey) & 0x8000) != 0) return true;
        foreach ((int on, int off) in NumberPad.Values)
            if (on == virtualKey) return (GetAsyncKeyState(off) & 0x8000) != 0;
        return false;
    }

    [DllImport("user32.dll")]
    static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int virtualKey);
}
