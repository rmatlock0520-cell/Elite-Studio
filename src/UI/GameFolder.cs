using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using EliteStudio.App;
using EliteStudio.Core;
using Microsoft.Win32;

namespace EliteStudio.UI;

/// <summary>
/// What is known about the HUD mod (EDHM) in a game folder. The names are the ones the page reads.
/// Missing names what a folder without the whole mod lacks, and is left out when nothing is.
/// </summary>
internal sealed record ModInfo(bool Installed, string? Version, string? Date, string? ForGame, string? IniDir, string? LinkTarget, Dictionary<string, bool> Files,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Missing);

/// <summary>What happened when Elite was asked to reload its HUD colours.</summary>
internal sealed record Reload(bool Attempted, bool Sent, string? Error);

/// <summary>
/// One Elite Dangerous install: its folder (the one holding EliteDangerous64.exe), the shop it came
/// from, and whether the HUD mod's loader file is in it. Also everything Studio UI asks about the
/// game on this PC: where it is installed, what the mod looks like, and whether it is running.
/// </summary>
internal sealed partial record GameFolder([property: JsonPropertyName("path")] string Dir, string Store, bool Edhm)
{
    /// <summary>
    /// Where the game sits below a launcher's folder (the game's own folder, Steam's EDLaunch folder, the
    /// launcher's own). Only the live (Odyssey / Horizons 4) product is looked for.
    /// </summary>
    public static readonly string[] ProductFolders =
        [Path.Combine("Products", "elite-dangerous-odyssey-64"), Path.Combine("EDLaunch", "Products", "elite-dangerous-odyssey-64"), "elite-dangerous-odyssey-64"];

    // The mod's own files and folder are looked up whatever their capitals (Paths.InFolder), as Windows does by itself.
    public static GameFolder At(string dir, string store) => new(dir, store, File.Exists(Paths.InFolder(dir, "d3dx.ini")));

    public static bool HasGame(string dir) => File.Exists(Path.Combine(dir, EliteWindow.ProcessName + ".exe"));

    /// <summary>Every install found through Steam, Epic and Frontier's own launcher, in that order.</summary>
    public static List<GameFolder> FindAll() => FindIn(LauncherFolders());

    /// <summary>The folders in which each launcher would keep Elite, with the shop's name.</summary>
    static List<(string Root, string Store)> LauncherFolders()
    {
        // Off Windows only Steam is looked through: where it keeps its libraries there is known (Core's SteamGames).
        if (!OperatingSystem.IsWindows())
            return [.. SteamGames.Libraries(SteamGames.Home, SteamGames.Env).Select(library => (Path.Combine(library, "steamapps", "common", "Elite Dangerous"), "Steam"))];

        var roots = new List<(string, string)>();
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string { Length: > 0 } steam)
                foreach (string library in SteamLibraries(steam.Replace('/', '\\')))
                    roots.Add((Path.Combine(library, @"steamapps\common\Elite Dangerous"), "Steam"));
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or System.Security.SecurityException or PlatformNotSupportedException) { }

        string epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        string[] manifests = [];
        // Epic's folder being unreadable must not stop the search through the other shops.
        try { if (Directory.Exists(epic)) manifests = Directory.GetFiles(epic, "*.item"); }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException) { }
        foreach (string manifest in manifests)
        {
            // Nor must one unreadable manifest (any game's) hide the others.
            try
            {
                JsonElement? item = Loose.Object(File.ReadAllText(manifest));
                if (Loose.Text(Loose.Field(item, "DisplayName")).Contains("Elite Dangerous", StringComparison.OrdinalIgnoreCase))
                    roots.Add((Loose.Text(Loose.Field(item, "InstallLocation")), "Epic"));
            }
            catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException) { }
        }

        foreach (string variable in (string[])["ProgramFiles(x86)", "ProgramFiles"])
            if (Environment.GetEnvironmentVariable(variable) is string { Length: > 0 } programs) roots.Add((Path.Combine(programs, "Frontier"), "Frontier"));
        roots.Add((Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Frontier_Developments"), "Frontier"));
        return roots;
    }

    /// <summary>Steam's own folder plus every extra library listed in its libraryfolders.vdf.</summary>
    internal static List<string> SteamLibraries(string steam)
    {
        var libraries = new List<string> { steam };
        string list = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
        if (File.Exists(list))
            foreach (Match entry in Regex.Matches(File.ReadAllText(list), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(entry.Groups[1].Value.Replace(@"\\", @"\"));
        return [.. libraries.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    internal static List<GameFolder> FindIn(IEnumerable<(string Root, string Store)> roots)
    {
        var found = new List<GameFolder>();
        foreach (var (root, store) in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            foreach (string product in ProductFolders)
            {
                string dir = Path.Combine(root, product);
                if (HasGame(dir) && !found.Exists(game => SamePath(game.Dir, dir))) found.Add(At(dir, store));
            }
        }
        return found;
    }

    /// <summary>Steam writes its path in lower case, so folder names are compared the way Windows does.</summary>
    public static bool SamePath(string? one, string? other) => string.Equals(one, other, StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder holding the four colour files, or null when the mod is not in this install.</summary>
    public string? IniDir()
    {
        string dir = Paths.InFolder(Dir, "EDHM-ini");
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>The game's build, from the VersionInfo.txt it ships with.</summary>
    public string? Version()
    {
        string file = Paths.InFolder(Dir, "VersionInfo.txt");
        JsonElement? version = File.Exists(file) ? Loose.Field(Loose.Object(File.ReadAllText(file)), "Version") : null;
        return version is null ? null : Loose.Text(version);
    }

    public ModInfo Mod()
    {
        // The mod's loader file names its own version on a comment line, e.g.
        // "; Elite Dangerous HUD Mod (EDHM) for Odyssey & Horizons Live v22.03 (23 September 2026) for FDev Update 4.4.1.1"
        string loader = Paths.InFolder(Dir, "d3dx.ini");
        string? version = null, date = null, forGame = null;
        if (File.Exists(loader))
        {
            Match line = Regex.Match(File.ReadAllText(loader), @"EDHM\)[^\r\n]*?v(\d+(?:\.\d+)+)\s*\(([^)]*)\)(?:\s*for FDev Update\s*([\d.]+))?");
            if (line.Success) (version, date, forGame) = (line.Groups[1].Value, line.Groups[2].Value, line.Groups[3].Value);
        }
        string? iniDir = IniDir();
        var files = new Dictionary<string, bool>();
        if (iniDir is not null)
            foreach (string name in ColourFiles.Names) files[name] = File.Exists(ColourFiles.PathOf(iniDir, name));
        // The three things the mod cannot work without: its settings file, its loader and the colour folder.
        string[] missing = [.. new[] { "d3dx.ini", "d3d11.dll" }.Where(file => !File.Exists(Paths.InFolder(Dir, file))), .. iniDir is null ? ["EDHM-ini"] : Array.Empty<string>()];
        // Some mod managers keep the colour files elsewhere and leave a link here. Files are still read
        // and written through the link; the target is only shown to the player.
        string? linkTarget = iniDir is null ? null : new DirectoryInfo(iniDir).LinkTarget;
        return new ModInfo(missing.Length == 0, version, date, forGame, iniDir, linkTarget, files, missing.Length == 0 ? null : missing);
    }

    /// <summary>
    /// Elite's own colour file (GraphicsConfigurationOverride.xml), read only. The mod's colours look
    /// right when it is left at default, so the page warns when it is not. isDefault is null when the
    /// file cannot be understood.
    /// </summary>
    public static object XmlStatus(string file)
    {
        bool exists = File.Exists(file);
        bool? isDefault = true;
        List<double[]>? matrix = null;
        if (exists)
        {
            try
            {
                var xml = new XmlDocument();
                xml.LoadXml(File.ReadAllText(file));
                if (xml.SelectSingleNode("/GraphicsConfig/GUIColour/Default") is XmlNode colours)
                {
                    var rows = new List<double[]>();
                    foreach (string row in (string[])["MatrixRed", "MatrixGreen", "MatrixBlue"])
                    {
                        string[] cells = (colours.SelectSingleNode(row)?.InnerText ?? "").Split(',');
                        rows.Add([.. cells.Select(cell => double.Parse(cell.Trim(), CultureInfo.InvariantCulture))]);
                    }
                    matrix = rows;
                    for (int i = 0; i < 3; i++)
                        for (int k = 0; k < 3; k++)
                            if (Math.Abs(rows[i][k] - (i == k ? 1 : 0)) > 0.001) isDefault = false;
                }
            }
            catch (Exception odd) when (odd is XmlException or FormatException or OverflowException or IndexOutOfRangeException or IOException or UnauthorizedAccessException)
            {
                isDefault = null;
            }
        }
        return new { path = file, exists, isDefault, matrix };
    }

    /// <summary>The game is running: the one look-up Core makes (<see cref="EliteWindow.Running"/>), at most every two seconds.</summary>
    public static bool IsRunning() => EliteWindow.Running;
}
