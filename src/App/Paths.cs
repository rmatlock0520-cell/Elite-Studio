using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace EliteStudio.App;

/// <summary>
/// Where things live on disk, and how a file is named after what the player typed. The folders are
/// set once at start-up; command-line options point them at practice folders so tests never touch
/// the player's real files.
/// </summary>
public static partial class Paths
{
    /// <summary>The longest name a file may be given, in characters, not counting its ending such as ".json".</summary>
    public const int LongestFileName = 80;

    static readonly char[] NotForFileNames = Path.GetInvalidFileNameChars();

    /// <summary>Studio's own files: settings.json, logs, backups, themes. Default %LOCALAPPDATA%\Elite Studio.</summary>
    public static string DataDir { get; private set; } = "";
    /// <summary>The desktop pages, next to the program.</summary>
    public static string WebRoot { get; private set; } = "";
    /// <summary>The game's journal folder (Saved Games\Frontier Developments\Elite Dangerous).</summary>
    public static string JournalDir { get; private set; } = "";
    /// <summary>The game's key-binding files.</summary>
    public static string BindingsDir { get; private set; } = "";
    /// <summary>Forces the game install folder (practice copies in tests); null means find it.</summary>
    public static string? GameDir { get; private set; }
    /// <summary>
    /// A practice copy: Studio was started on a data folder of its own (--data-dir). The one rule for
    /// every studio: a practice copy's first run copies nothing in from the older tools (Elite HUD
    /// Studio's saved themes, the Elite Haul Overlay's settings), so it never pulls in the player's real files.
    /// </summary>
    public static bool Practice { get; private set; }

    /// <summary>Options: --data-dir, --web-root, --journal-dir, --bindings-dir, --game-dir.</summary>
    public static void Init(IReadOnlyDictionary<string, string> options)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Practice = options.ContainsKey("data-dir");
        // Always full paths: some Windows calls (the Recycle Bin, Explorer) misbehave with relative ones.
        DataDir = Path.GetFullPath(options.GetValueOrDefault("data-dir") ?? Path.Combine(local, "Elite Studio"));
        WebRoot = Path.GetFullPath(options.GetValueOrDefault("web-root") ?? Path.Combine(AppContext.BaseDirectory, "web"));
        JournalDir = Path.GetFullPath(options.GetValueOrDefault("journal-dir")
            ?? Path.Combine(SavedGames(), "Frontier Developments", "Elite Dangerous"));
        BindingsDir = Path.GetFullPath(options.GetValueOrDefault("bindings-dir")
            ?? Path.Combine(local, "Frontier Developments", "Elite Dangerous", "Options", "Bindings"));
        GameDir = options.GetValueOrDefault("game-dir") is string game ? Path.GetFullPath(game) : null;
        Directory.CreateDirectory(DataDir);
    }

    /// <summary>
    /// The file name (without its ending) under which Studio keeps something the player named: a
    /// theme, a ship build. It is the name as typed, with every character Windows forbids in file
    /// names replaced by "-" and dots and spaces at the end dropped. Null when no usable name is
    /// left: nothing typed, more than <see cref="LongestFileName"/> characters, a dot in front, or one of the names Windows
    /// keeps for devices (CON, NUL, COM1 and the like). One rule for every studio.
    /// </summary>
    public static string? FileNameFor(string typed)
    {
        typed = typed.Trim();
        if (typed.Length is 0 or > LongestFileName) return null;
        string name = string.Concat(typed.Select(c => NotForFileNames.Contains(c) ? '-' : c)).TrimEnd('.', ' ');
        return IsFileName(name) ? name : null;
    }

    /// <summary>
    /// True for a name <see cref="FileNameFor"/> could have made. Asked of every such name a page
    /// sends back, so that it can only ever lead to a file directly inside the studio's own folder.
    /// </summary>
    public static bool IsFileName(string name) =>
        name.Length is > 0 and <= LongestFileName && name.IndexOfAny(NotForFileNames) < 0
        && name[0] != '.' && name[^1] is not ('.' or ' ') && !DeviceName().IsMatch(name);

    // Players can move "Saved Games", so ask Windows where it is instead of assuming the profile folder.
    static string SavedGames()
    {
        var savedGames = new Guid("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4");
        if (SHGetKnownFolderPath(savedGames, 0, IntPtr.Zero, out IntPtr raw) == 0)
        {
            try { return Marshal.PtrToStringUni(raw)!; }
            finally { Marshal.FreeCoTaskMem(raw); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    // Windows 10 still takes "con.json" for the console, not for a file.
    [GeneratedRegex(@"^(con|prn|aux|nul|com[0-9]|lpt[0-9])(\.|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceName();
}
