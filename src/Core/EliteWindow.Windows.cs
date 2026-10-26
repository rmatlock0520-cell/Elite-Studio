using System.Runtime.InteropServices;
using System.Windows;

namespace EliteStudio.Core;

// The window half of EliteWindow: Windows' own calls about windows and monitors, which only the overlay needs.
public static partial class EliteWindow
{
    /// <summary>The game's main window; zero while it is not running or has no window yet.</summary>
    public static IntPtr Handle
    {
        get { Refresh(); return handle; }
    }

    /// <summary>The window the player is typing into belongs to the game.</summary>
    public static bool IsForeground
    {
        get
        {
            Refresh();
            if (processId == 0) return false;
            GetWindowThreadProcessId(GetForegroundWindow(), out uint front);
            return front == processId;
        }
    }

    /// <summary>
    /// The whole monitor the game's window is on (not just the part free of the taskbar), in the
    /// screen pixels Windows reports to this program; null while the game has no window.
    /// </summary>
    public static Int32Rect? MonitorBounds
    {
        get
        {
            Refresh();
            if (handle == IntPtr.Zero) return null;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            const uint nearestMonitor = 2;
            if (!GetMonitorInfo(MonitorFromWindow(handle, nearestMonitor), ref info)) return null;
            return new Int32Rect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
