using System.Diagnostics;
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
internal sealed record GameFolder([property: JsonPropertyName("path")] string Dir, string Store, bool Edhm)
{
    /// <summary>
    /// Where the game sits below a launcher's folder (the game's own folder, Steam's EDLaunch folder, the
    /// launcher's own). Only the live (Odyssey / Horizons 4) product is looked for.
    /// </summary>
    public static readonly string[] ProductFolders =
        [@"Products\elite-dangerous-odyssey-64", @"EDLaunch\Products\elite-dangerous-odyssey-64", "elite-dangerous-odyssey-64"];

    public static GameFolder At(string dir, string store) => new(dir, store, File.Exists(Path.Combine(dir, "d3dx.ini")));

    public static bool HasGame(string dir) => File.Exists(Path.Combine(dir, EliteWindow.ProcessName + ".exe"));

    /// <summary>Every install found through Steam, Epic and Frontier's own launcher, in that order.</summary>
    public static List<GameFolder> FindAll() => FindIn(LauncherFolders());

    /// <summary>The folders in which each launcher would keep Elite, with the shop's name.</summary>
    static List<(string Root, string Store)> LauncherFolders()
    {
        var roots = new List<(string, string)>();
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string { Length: > 0 } steam)
                foreach (string library in SteamLibraries(steam.Replace('/', '\\')))
                    roots.Add((Path.Combine(library, @"steamapps\common\Elite Dangerous"), "Steam"));
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }

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
        string dir = Path.Combine(Dir, "EDHM-ini");
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>The game's build, from the VersionInfo.txt it ships with.</summary>
    public string? Version()
    {
        string file = Path.Combine(Dir, "VersionInfo.txt");
        JsonElement? version = File.Exists(file) ? Loose.Field(Loose.Object(File.ReadAllText(file)), "Version") : null;
        return version is null ? null : Loose.Text(version);
    }

    public ModInfo Mod()
    {
        // The mod's loader file names its own version on a comment line, e.g.
        // "; Elite Dangerous HUD Mod (EDHM) for Odyssey & Horizons Live v22.03 (23 September 2026) for FDev Update 4.4.1.1"
        string loader = Path.Combine(Dir, "d3dx.ini");
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
        string[] missing = [.. new[] { "d3dx.ini", "d3d11.dll" }.Where(file => !File.Exists(Path.Combine(Dir, file))), .. iniDir is null ? ["EDHM-ini"] : Array.Empty<string>()];
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

    public static bool IsRunning()
    {
        Process[] elite = Process.GetProcessesByName(EliteWindow.ProcessName);
        foreach (Process process in elite) process.Dispose();
        return elite.Length > 0;
    }

    /// <summary>
    /// Elite reloads the HUD colours when F11 is pressed in the game (the mod's own key). A page
    /// cannot press a key in another program, so this brings Elite to the front and presses it.
    /// </summary>
    public static Reload PressReloadKey()
    {
        Process[] elite = Process.GetProcessesByName(EliteWindow.ProcessName);
        if (elite.Length == 0) return new Reload(false, false, null);
        try
        {
            if (elite[0].MainWindowHandle == IntPtr.Zero) throw new InvalidOperationException("Elite Dangerous is running but its main window handle is not available.");
            Microsoft.VisualBasic.Interaction.AppActivate(elite[0].Id);
            Thread.Sleep(200);   // give Windows time to switch windows before the key goes out
            System.Windows.Forms.SendKeys.SendWait("{F11}");
            Log.Info("Sent F11 to Elite Dangerous to reload the HUD colours.");
            return new Reload(true, true, null);
        }
        catch (Exception problem)
        {
            // Whatever went wrong, the save itself worked: the page tells the player to press F11 themselves.
            Log.Error("Could not send F11 to Elite Dangerous", problem);
            return new Reload(true, false, problem.Message);
        }
        finally
        {
            foreach (Process process in elite) process.Dispose();
        }
    }
}
