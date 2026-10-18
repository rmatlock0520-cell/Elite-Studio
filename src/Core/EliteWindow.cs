using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Xml;
using System.Xml.Linq;

namespace EliteStudio.Core;

/// <summary>How the game is set to draw itself. An overlay cannot show over <see cref="Fullscreen"/>.</summary>
public enum DisplayMode { Unknown, Windowed, Fullscreen, Borderless }

/// <summary>
/// Elite's own window: is the game running, is it the window in front, and which monitor is it on.
/// Looking a process up by name is slow, so that is done at most every two seconds however often
/// these are asked; the "in front" test itself is cheap and always fresh. UI thread only.
/// </summary>
public static class EliteWindow
{
    /// <summary>The game itself, as Windows names its process and (with ".exe") its program file; the launcher does not count.</summary>
    internal const string ProcessName = "EliteDangerous64";
    const long CheckEveryMs = 2000;

    static long checkedAt = -CheckEveryMs;
    static uint processId;
    static IntPtr handle;

    /// <summary>The game's process exists.</summary>
    public static bool Running
    {
        get { Refresh(); return processId != 0; }
    }

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

    /// <summary>
    /// Reads the window mode from the text of the game's DisplaySettings.xml, where FullScreen is
    /// 0 for windowed, 1 for fullscreen and 2 for borderless. Null when the text is not whole XML:
    /// the file was caught half-written and has to be read again.
    /// </summary>
    internal static DisplayMode? ParseDisplayMode(string displaySettingsXml)
    {
        try
        {
            return ((string?)XDocument.Parse(displaySettingsXml).Root?.Element("FullScreen"))?.Trim() switch
            {
                "0" => DisplayMode.Windowed,
                "1" => DisplayMode.Fullscreen,
                "2" => DisplayMode.Borderless,
                _ => DisplayMode.Unknown,
            };
        }
        catch (XmlException)
        {
            return null;
        }
    }

    static void Refresh()
    {
        long now = Environment.TickCount64;
        if (now - checkedAt < CheckEveryMs) return;
        checkedAt = now;
        // Worked out first and stored together at the end, so nothing asking meanwhile (the tests ask from several
        // threads at once) sees the game gone for the moment of the look.
        uint id = 0;
        IntPtr window = IntPtr.Zero;
        // A fresh Process object each time: an old one keeps the window handle it first saw.
        Process[] found = Process.GetProcessesByName(ProcessName);
        try
        {
            if (found.Length > 0)
            {
                window = found[0].MainWindowHandle;
                id = (uint)found[0].Id;
            }
        }
        catch (InvalidOperationException)
        {
            // the game closed while it was being looked at
            (id, window) = (0, IntPtr.Zero);
        }
        finally
        {
            foreach (Process process in found) process.Dispose();
        }
        (processId, handle) = (id, window);
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
