using System.Diagnostics;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace EliteStudio.Core;

/// <summary>How the game is set to draw itself. An overlay cannot show over <see cref="Fullscreen"/>.</summary>
public enum DisplayMode { Unknown, Windowed, Fullscreen, Borderless }

/// <summary>
/// Elite's own window: is the game running, is it the window in front, and which monitor is it on.
/// Looking a process up by name is slow, so that is done at most every two seconds however often
/// these are asked; the "in front" test itself is cheap and always fresh. UI thread only.
/// (The window half, Windows only, is in EliteWindow.Windows.cs.)
/// </summary>
public static partial class EliteWindow
{
    /// <summary>The game itself, as Windows names its process and (with ".exe") its program file; the launcher does not count.</summary>
    internal const string ProcessName = "EliteDangerous64";
    const long CheckEveryMs = 2000;

    static long checkedAt = -CheckEveryMs;
    static uint processId;
    static IntPtr handle;   // read by the window half

    /// <summary>The game's process exists.</summary>
    public static bool Running
    {
        get { Refresh(); return processId != 0; }
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
        if (!OperatingSystem.IsWindows())
        {
            (processId, handle) = (UnderProton(), IntPtr.Zero);
            return;
        }
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

    /// <summary>
    /// Off Windows the game runs under Proton, as a Wine process: its name is cut short there, but its command line is the
    /// Windows path of the game's program, so the game is the process whose command line names EliteDangerous64.exe.
    /// Zero when there is none. Inside a Flatpak, /proc shows no process outside the sandbox, so there the game always
    /// reads as closed (the "Elite running" chip then says closed).
    /// </summary>
    /// <param name="proc">The processes' folder; tests give their own.</param>
    internal static uint UnderProton(string proc = "/proc")
    {
        try
        {
            foreach (string process in Directory.EnumerateDirectories(proc))
            {
                if (!uint.TryParse(Path.GetFileName(process), NumberStyles.None, CultureInfo.InvariantCulture, out uint id)) continue;
                try
                {
                    // The words of a command line are separated by '\0'.
                    if (File.ReadAllText(Path.Combine(process, "cmdline")).Contains(ProcessName + ".exe", StringComparison.OrdinalIgnoreCase)) return id;
                }
                catch (Exception gone) when (gone is IOException or UnauthorizedAccessException)
                {
                    // it ended while being looked at, or it is not this user's to read
                }
            }
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException) { }
        return 0;
    }
}
