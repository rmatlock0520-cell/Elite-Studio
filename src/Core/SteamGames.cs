using System.Text.RegularExpressions;

namespace EliteStudio.Core;

/// <summary>
/// Where Steam keeps Elite on Linux: Steam's libraries, the game's install, and the game's Proton prefix (the Windows
/// folders Proton makes for it), inside which the game writes its journal and keeps its key bindings. Only folders and
/// files are read, so it works on any system; the Linux build asks it at start-up, Windows never does.
/// </summary>
public static partial class SteamGames
{
    /// <summary>What was found.</summary>
    /// <param name="Journal">The game's journal folder; null when there is none. It may not exist yet when the game has never been started.</param>
    /// <param name="Bindings">The key bindings folder of the same Windows user in the same prefix; null with no journal folder.</param>
    /// <param name="Installs">The folders that hold EliteDangerous64.exe, the live game's (Odyssey, Horizons 4) before the Legacy one's.</param>
    public sealed record Found(string? Journal, string? Bindings, IReadOnlyList<string> Installs);

    const string EliteOnSteam = "359320";

    /// <summary>This PC's home folder, for <see cref="Find(string, IReadOnlyDictionary{string, string?})"/> outside the checks.</summary>
    internal static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>This PC's environment, as far as it is asked.</summary>
    internal static IReadOnlyDictionary<string, string?> Env => new Dictionary<string, string?> { ["STEAM_DIR"] = Environment.GetEnvironmentVariable("STEAM_DIR") };

    /// <summary>Looks on this PC: in its home folder, and in STEAM_DIR when that is set.</summary>
    public static Found Find() => Find(Home, Env);

    /// <param name="home">The home folder, "~".</param>
    /// <param name="env">The environment's variables; only STEAM_DIR (the Steam folder, as protontricks reads it) is asked.</param>
    public static Found Find(string home, IReadOnlyDictionary<string, string?> env)
    {
        List<string> libraries = Libraries(home, env);
        string[] installs =
        [
            .. from product in (string[])["elite-dangerous-odyssey-64", "elite-dangerous-64"]
               from library in libraries
               let dir = Path.Combine(library, "steamapps", "common", "Elite Dangerous", "Products", product)
               where File.Exists(Path.Combine(dir, EliteWindow.ProcessName + ".exe"))
               select dir,
        ];

        // The game's own prefix is in one of the libraries (not always the game's). With several, the one Proton used last
        // counts, as protontricks has it: its pfx.lock changes whenever the game starts.
        string? prefix = libraries.Select(library => Path.Combine(library, "steamapps", "compatdata", EliteOnSteam, "pfx")).Where(Directory.Exists).MaxBy(LastUsed);
        // The Windows users that have a journal folder: the game's prefix first, then any other prefix that has one (the
        // Frontier launcher added to Steam as a game of its own gets a prefix of its own, under another number).
        var users = new List<string>();
        if (prefix is not null) users.AddRange(UsersWithJournal(prefix));
        foreach (string library in libraries)
            foreach (string other in Folders(Path.Combine(library, "steamapps", "compatdata")))
                if (Path.GetFileName(other) != EliteOnSteam) users.AddRange(UsersWithJournal(Path.Combine(other, "pfx")));
        // Several: the one the game wrote to last. None, but the game has a prefix: it has not been started yet, and its
        // journal will be where Proton's own user keeps it, so Studio picks it up after the game's first start.
        string? user = users.Count > 0 ? users.MaxBy(found => NewestJournal(JournalOf(found)))
            : prefix is not null ? Path.Combine(prefix, "drive_c", "users", "steamuser") : null;
        return new Found(user is null ? null : JournalOf(user), user is null ? null : BindingsOf(user), installs);
    }

    /// <summary>
    /// Steam's libraries: each Steam folder (native, Flatpak, Snap, or STEAM_DIR), and every library its
    /// steamapps/libraryfolders.vdf lists (an SD card, another drive). Each real folder once.
    /// </summary>
    internal static List<string> Libraries(string home, IReadOnlyDictionary<string, string?> env)
    {
        string[] steamFolders =
        [
            env.GetValueOrDefault("STEAM_DIR") ?? "",
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root"),
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"),
            Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"),
        ];
        var libraries = new List<string>();
        var read = new HashSet<string>();
        foreach (string steam in steamFolders.Where(folder => folder.Length > 0 && Directory.Exists(folder)).Select(Real))
        {
            if (!read.Add(steam)) continue;
            if (!libraries.Contains(steam)) libraries.Add(steam);
            foreach (string library in LibraryList(Path.Combine(steam, "steamapps", "libraryfolders.vdf")).Where(Directory.Exists).Select(Real))
                if (!libraries.Contains(library)) libraries.Add(library);
        }
        return libraries;
    }

    /// <summary>
    /// The libraries a libraryfolders.vdf lists. It is Steam's own text format: "libraryfolders" { "0" { "path" "/home/deck/.local/share/Steam" ... } ... };
    /// older files name them straight away: "1" "/run/media/mmcblk0p1". The numbered lines of a library's "apps" are
    /// its games and their sizes, never a folder.
    /// </summary>
    static List<string> LibraryList(string vdf)
    {
        string text;
        try
        {
            if (!File.Exists(vdf)) return [];
            text = File.ReadAllText(vdf);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return
        [
            .. from Match line in LibraryLine().Matches(text)
               let folder = Unescaped().Replace(line.Groups[2].Value, "$1")
               where line.Groups[1].Value == "path" || folder.Contains('/') || folder.Contains('\\')
               select folder,
        ];
    }

    /// <summary>A key and its text: "path" "...", or a number and its text.</summary>
    [GeneratedRegex("""
        "(path|\d+)"\s+"((?:[^"\\]|\\.)*)"
        """)]
    private static partial Regex LibraryLine();

    /// <summary>A character written with a backslash in front ("\\" for one backslash).</summary>
    [GeneratedRegex(@"\\(.)")]
    private static partial Regex Unescaped();

    // The same folder reached through a link (~/.steam/steam is one to ~/.local/share/Steam) counts once.
    static string Real(string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        try
        {
            return Directory.ResolveLinkTarget(full, returnFinalTarget: true) is { } target ? Path.TrimEndingDirectorySeparator(target.FullName) : full;
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return full;
        }
    }

    /// <summary>When Proton last used a prefix: its pfx.lock beside it, else the prefix folder itself.</summary>
    static DateTime LastUsed(string prefix)
    {
        string lockFile = Path.Combine(Path.GetDirectoryName(prefix)!, "pfx.lock");
        return File.Exists(lockFile) ? File.GetLastWriteTimeUtc(lockFile) : Directory.GetLastWriteTimeUtc(prefix);
    }

    /// <summary>The prefix's Windows users that have the game's journal folder: Proton's own ("steamuser") first, then the others.</summary>
    static IEnumerable<string> UsersWithJournal(string prefix) =>
        Folders(Path.Combine(prefix, "drive_c", "users"))
            .OrderBy(user => Path.GetFileName(user) == "steamuser" ? 0 : 1)
            .Where(user => Directory.Exists(JournalOf(user)));

    static string JournalOf(string user) => Path.Combine(user, "Saved Games", "Frontier Developments", "Elite Dangerous");

    static string BindingsOf(string user) => Path.Combine(user, "AppData", "Local", "Frontier Developments", "Elite Dangerous", "Options", "Bindings");

    /// <summary>When the game last wrote a journal into this folder; the earliest time there is when it holds none.</summary>
    static DateTime NewestJournal(string journal)
    {
        try
        {
            return new DirectoryInfo(journal).GetFiles("Journal.*.log").Select(file => file.LastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>The folders in a folder, in name order; none when it is not there or cannot be read.</summary>
    static string[] Folders(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? [.. Directory.GetDirectories(folder).Order(StringComparer.Ordinal)] : [];
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
