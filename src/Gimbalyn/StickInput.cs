using System.Runtime.InteropServices;

namespace EliteStudio.Gimbalyn;

/// <summary>
/// Reads real sticks fast through Windows' joystick calls (winmm), the same calls and the same calibration as Studio's
/// Stick check and trainer, so the dead zones measured there are the ones applied here. A device is named as Studio names
/// it ("046D:C215", "#2" behind the second of two alike, counted in Windows' order); its place in Windows' list is looked
/// up afresh at every start (see <see cref="Forget"/>) and whenever it stops answering (at most once a second), and once a
/// second the place is checked to still hold that kind of stick, so one unplugged cannot leave another, or vJoy, feeding
/// in its stead. Nothing is allocated per reading. vJoy itself (1234:BEAD) is never a source: Gimbalyn would read back what it writes.
/// </summary>
public sealed class WinmmSticks : IStickInput
{
    const int Places = 16;
    const uint WantAll = 0xFF;   // JOY_RETURNALL
    const uint HasZ = 0x1, HasR = 0x2, HasU = 0x4, HasV = 0x8;
    const string VJoy = "1234:BEAD";

    /// <summary>Where a device was found, its axes' ranges, and when its place was last checked to still hold it.</summary>
    sealed class Known(int place, (uint Min, uint Max)?[] ranges, long checkedAt)
    {
        public readonly int Place = place;
        public readonly (uint Min, uint Max)?[] Ranges = ranges;
        public long CheckedAt = checkedAt;
    }

    readonly Dictionary<string, Known?> devices = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> lookedAt = new(StringComparer.OrdinalIgnoreCase);   // when each device was last looked up
    readonly Func<long> clock;

    /// <param name="clock">Milliseconds now (tests hand in their own).</param>
    public WinmmSticks(Func<long>? clock = null) => this.clock = clock ?? (() => Environment.TickCount64);

    public void Forget()
    {
        devices.Clear();
        lookedAt.Clear();
    }

    public bool Read(string device, Span<double> axes)
    {
        if (device.StartsWith(VJoy, StringComparison.OrdinalIgnoreCase)) return false;
        devices.TryGetValue(device, out Known? known);
        long now = clock();
        if (known is null)
        {
            if (lookedAt.TryGetValue(device, out long at) && now - at < 1000) return false;   // looked for it less than a second ago
            lookedAt[device] = now;
            known = devices[device] = Find(device, now);
            if (known is null) return false;
        }
        else if (now - known.CheckedAt >= 1000)
        {
            // Once a second: is it still the same kind of stick in that place? One unplugged can leave another, or vJoy, there.
            known.CheckedAt = now;
            if (!Holds(known.Place, device)) return Drop(device, now);
        }
        var info = new JoyInfo { Size = 52, Flags = WantAll };
        if (joyGetPosEx((uint)known.Place, ref info) != 0) return Drop(device, now);
        Span<uint> raw = [info.X, info.Y, info.Z, info.R, info.U, info.V];
        for (int i = 0; i < 6 && i < axes.Length; i++)
            axes[i] = known.Ranges[i] is { } range ? Scale(raw[i], range.Min, range.Max) : double.NaN;
        return true;
    }

    /// <summary>The device is gone from its place (or another sits there): looked up again in a second.</summary>
    bool Drop(string device, long now)
    {
        devices[device] = null;
        lookedAt[device] = now;
        return false;
    }

    /// <summary>Whether the device in a place is still of the kind named (its maker and product), and not vJoy.</summary>
    static bool Holds(int place, string device)
    {
        if (joyGetDevCapsW((UIntPtr)place, out JoyCaps caps, 728) != 0) return false;
        string id = $"{caps.Maker:X4}:{caps.Product:X4}";
        return !id.Equals(VJoy, StringComparison.OrdinalIgnoreCase) && device.Split('#')[0].Equals(id, StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>The device's place in Windows' list and its axes' ranges, by Studio's name for it; null when it is not there.</summary>
    static Known? Find(string device, long now)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int place = 0; place < Places; place++)
        {
            var info = new JoyInfo { Size = 52, Flags = WantAll };
            if (joyGetPosEx((uint)place, ref info) != 0 || joyGetDevCapsW((UIntPtr)place, out JoyCaps caps, 728) != 0) continue;
            string id = $"{caps.Maker:X4}:{caps.Product:X4}";
            int alike = seen.GetValueOrDefault(id);
            seen[id] = alike + 1;
            string name = alike == 0 ? id : $"{id}#{alike + 1}";
            if (!name.Equals(device, StringComparison.OrdinalIgnoreCase)) continue;
            return new Known(place,
            [
                (caps.XMin, caps.XMax), (caps.YMin, caps.YMax),
                (caps.Has & HasZ) != 0 ? (caps.ZMin, caps.ZMax) : null,
                (caps.Has & HasR) != 0 ? (caps.RMin, caps.RMax) : null,
                (caps.Has & HasU) != 0 ? (caps.UMin, caps.UMax) : null,
                (caps.Has & HasV) != 0 ? (caps.VMin, caps.VMax) : null,
            ], now);
        }
        return null;
    }

    /// <summary>A reading between the device's own ends as -1 to 1, the middle 0 (as Studio's own reader scales it).</summary>
    internal static double Scale(uint value, uint min, uint max) =>
        max <= min ? 0 : Math.Clamp((value - (double)min) / (max - (double)min) * 2 - 1, -1, 1);

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
/// Gimbalyn's clock: wakes its feeding thread every few milliseconds with a high-resolution waitable timer (Windows 10
/// 1803 and later), which keeps time without raising Windows' timer resolution for every program. Where that timer is not
/// to be had, a 1 ms sleep is used instead (the old way, which does raise it while Gimbalyn runs).
/// </summary>
public sealed class Pacer : IDisposable
{
    readonly IntPtr timer;
    readonly int every;
    readonly bool raised;

    public Pacer(int everyMs = 4)
    {
        every = Math.Clamp(everyMs, 1, 100);
        timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (timer != IntPtr.Zero)
        {
            long due = -every * 10_000L;   // relative, in 100 ns
            if (SetWaitableTimer(timer, ref due, every, IntPtr.Zero, IntPtr.Zero, false)) return;
            CloseHandle(timer);
            timer = IntPtr.Zero;
        }
        raised = timeBeginPeriod(1) == 0;
    }

    /// <summary>Waits for the next beat.</summary>
    public void Wait()
    {
        if (timer != IntPtr.Zero) _ = WaitForSingleObject(timer, (uint)(every * 10));
        else Thread.Sleep(every);
    }

    public void Dispose()
    {
        if (timer != IntPtr.Zero) { CancelWaitableTimer(timer); CloseHandle(timer); }
        if (raised) timeEndPeriod(1);
    }

    const uint CreateWaitableTimerHighResolution = 0x2, TimerAllAccess = 0x1F0003;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWaitableTimer(IntPtr timer, ref long due, int periodMs, IntPtr completion, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CancelWaitableTimer(IntPtr timer);

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint ms);

    [DllImport("winmm.dll")]
    static extern uint timeEndPeriod(uint ms);
}
