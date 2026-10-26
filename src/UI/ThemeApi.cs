using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using EliteStudio.App;

namespace EliteStudio.UI;

/// <summary>
/// Studio UI: reads and saves the HUD colour files of the game's HUD mod, with backups and a
/// theme library. All routes live under /api/ui/.
/// </summary>
public static class ThemeApi
{
    /// <summary>network: what the HUD mod is downloaded through; null is the internet.</summary>
    public static void Map(WebServer web, Settings settings, HttpMessageHandler? network = null)
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        new StudioUi(settings, Paths.DataDir, Paths.GameDir,
            // Elite keeps its own colour file in the folder next to its key bindings.
            graphicsXml: Path.GetFullPath(Path.Combine(Paths.BindingsDir, "..", "Graphics", "GraphicsConfigurationOverride.xml")),
            // Elite HUD Studio kept saved themes next to its program up to 1.6 and in the user folder from 1.7.
            // A practice copy must not pull in the player's real files: for it there are no such folders to copy from.
            oldThemeDirs: Paths.Practice ? [] : [Path.Combine(documents, "Elite HUD Studio", "My Themes"), Path.Combine(local, "Elite HUD Studio", "My Themes")]) { DownloadMod = () => ModInstall.Download(network) }.Map(web);
    }
}

/// <summary>The "ui" section of settings.json.</summary>
internal sealed record UiSettings
{
    /// <summary>The game folder the player picked by hand; used while it still holds the game.</summary>
    public string? GameDir { get; init; }
    /// <summary>The game folder the player said "yes, that's it" to, so the page does not ask again.</summary>
    public string? GameConfirmed { get; init; }
    /// <summary>Set once the themes saved with Elite HUD Studio have been brought over.</summary>
    public bool OldThemesCopied { get; init; }
}

/// <summary>
/// Everything behind the /api/ui/ routes. It answers with the same JSON the Elite HUD Studio helper
/// gave, because the theme editor page was written against that helper.
///
/// Requests are served one at a time on the thread pool, never on the caller's thread, so reading
/// and writing files cannot make the app's window or the overlay stutter.
/// </summary>
internal sealed class StudioUi(Settings settings, string dataDir, string? forcedGameDir, string graphicsXml, IReadOnlyList<string> oldThemeDirs)
{
    static readonly UTF8Encoding Utf8NoMark = new(false);
    static readonly JsonSerializerOptions OneLine = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly Regex NotForFileNames = new("[\\\\/:*?\"<>|]");

    /// <summary>
    /// Theme and backup-info files are written byte for byte as Elite HUD Studio wrote them (one line,
    /// the same field order, the same four characters escaped), so a file from either tool can be
    /// used by the other and compared with it. Those characters only ever occur inside JSON text.
    /// </summary>
    static string FileJson(object value)
    {
        string json = JsonSerializer.Serialize(value, OneLine);
        foreach (char escaped in "'<>&") json = json.Replace(escaped.ToString(), $@"\u{(int)escaped:x4}");
        return json;
    }

    // Full paths, even when Studio was started with a relative --data-dir: Windows only uses the Recycle
    // Bin for a file named by its full path (it deletes the file for good otherwise). Each line asks
    // for the full path itself because "dataDir" on the right is still the value as it was passed in.
    readonly string dataDir = Path.GetFullPath(dataDir);
    readonly string backupDir = Path.GetFullPath(Path.Combine(dataDir, "ui", "Backups"));
    readonly string themesDir = Path.GetFullPath(Path.Combine(dataDir, "ui", "My Themes"));
    readonly Lock oneAtATime = new();
    UiSettings saved = settings.Get<UiSettings>("ui");
    UiSettings? unsaved;
    bool started, backedUpThisSession;
    List<GameFolder> games = [];
    GameFolder? game;

    // What reaches outside Studio's own files. Tests swap these so they never press keys, open windows
    // or go near the player's real game.
    internal Func<List<GameFolder>> FindGames { get; init; } = GameFolder.FindAll;
    internal Func<bool> GameRunning { get; init; } = GameFolder.IsRunning;
    internal Func<Reload> PressReloadKey { get; init; } = GameFolder.PressReloadKey;
    // Explorer on Windows, as before; elsewhere the folder is opened as a document, which shows it in the desktop's file manager (xdg-open).
    internal Action<string> ShowFolder { get; init; } = folder =>
        (OperatingSystem.IsWindows() ? Process.Start("explorer.exe", $"\"{folder}\"") : Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }))?.Dispose();
    internal Action<string> Recycle { get; init; } = RecycleBin.Send;
    internal Func<(string Version, byte[] Zip)> DownloadMod { get; init; } = () => ModInstall.Download(null);

    public void Map(WebServer web)
    {
        web.Map("GET", "/api/ui/status", _ => Run(Status));
        web.Map("GET", "/api/ui/ini", _ => Run(() => new { ok = true, files = IniDir() is string dir ? ColourFiles.ReadAll(dir) : [] }));
        web.Map("POST", "/api/ui/save", request => Run(() => Save(Body(request))));
        web.Map("POST", "/api/ui/reload", _ => Run(() => PressReloadKey()));
        web.Map("GET", "/api/ui/backups", _ => Run(() => new { ok = true, dir = backupDir, items = Backups() }));
        web.Map("POST", "/api/ui/backup", request => Run(() => MakeBackup(Loose.Field(Body(request), "reason"))));
        web.Map("GET", "/api/ui/backup", request => Run(() => ReadBackup(request.Query.GetValueOrDefault("name") ?? "")));
        web.Map("GET", "/api/ui/themes", _ => Run(Themes));
        web.Map("GET", "/api/ui/theme", request => Run(() => ReadTheme(request.Query.GetValueOrDefault("src"), request.Query.GetValueOrDefault("id") ?? "")));
        web.Map("POST", "/api/ui/mythemes", request => Run(() => SaveTheme(Body(request))));
        web.Map("POST", "/api/ui/mythemes/delete", request => Run(() => DeleteTheme(Loose.Text(Loose.Field(Body(request), "id")))));
        web.Map("POST", "/api/ui/open", request => Run(() => Open(Loose.Text(Loose.Field(Body(request), "what")))));
        web.Map("POST", "/api/ui/select-game", request => Run(() => SelectGame(Loose.Text(Loose.Field(Body(request), "path")))));
        web.Map("POST", "/api/ui/confirm-game", _ => Run(ConfirmGame));
        web.Map("POST", "/api/ui/install-mod", _ => Run(InstallMod));
    }

    /// <summary>Does one request's work on the thread pool, after any request still being worked on.</summary>
    async Task<WebResponse> Run(Func<object> work)
    {
        try
        {
            return await Task.Run(() =>
            {
                lock (oneAtATime)
                {
                    // Looking for the game and for old themes waits for the first request, so start-up never waits for the disk.
                    if (!started)
                    {
                        BringOverOldThemes();
                        ChooseGame();
                        started = true;   // only once both worked: after a failure the next request tries again
                    }
                    return WebResponse.Json(work());
                }
            });
        }
        finally
        {
            // Back on the caller's thread, which is the only one that may touch the settings file.
            if (Interlocked.Exchange(ref unsaved, null) is UiSettings changed) settings.Set("ui", changed);
        }
    }

    static JsonElement? Body(WebRequest request) =>
        string.IsNullOrWhiteSpace(request.Body) ? null : JsonSerializer.Deserialize<JsonElement>(request.Body);

    static InvalidOperationException Problem(string message) => new(message);

    void Remember(UiSettings changed) => saved = unsaved = changed;

    // ------------------------------------------------------------ the game folder

    void ChooseGame()
    {
        if (forcedGameDir is not null)
        {
            // A practice copy was named at start-up: use only that, and never look at the real installs.
            games = [];
            game = Directory.Exists(forcedGameDir) ? GameFolder.At(Path.TrimEndingDirectorySeparator(Path.GetFullPath(forcedGameDir)), "Custom") : null;
            if (game is null) Log.Error($"Game folder not found: {forcedGameDir}");
            return;
        }
        games = FindGames();
        if (saved.GameDir is { Length: > 0 } chosen && GameFolder.HasGame(chosen))
            game = games.Find(found => GameFolder.SamePath(found.Dir, chosen)) ?? GameFolder.At(chosen, "Custom");
        else
            game = games.Find(found => found.Edhm) ?? games.FirstOrDefault();   // prefer an install that has the mod
    }

    string? IniDir() => game?.IniDir();

    Dictionary<string, object?> Status()
    {
        var status = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["app"] = "hud-studio",
            ["appVersion"] = "1.0",
            ["testMode"] = forcedGameDir is not null,   // the page shows a "TEST MODE" chip on a practice copy
            ["running"] = GameRunning(),
            ["game"] = null,
            ["games"] = games,
            ["edhm"] = null,
            ["xml"] = GameFolder.XmlStatus(graphicsXml),
            ["themesDir"] = null,
            ["backupsDir"] = backupDir,
            ["root"] = dataDir,
        };
        if (game is not null)
        {
            status["game"] = new { path = game.Dir, store = game.Store, version = game.Version() };
            // Left out, not false, when there is no game: the page only asks "is this your game?" when there is one.
            status["gameConfirmed"] = GameFolder.SamePath(saved.GameConfirmed, game.Dir);
            status["edhm"] = game.Mod();
        }
        return status;
    }

    object SelectGame(string dir)
    {
        if (dir.Length == 0 || !GameFolder.HasGame(dir)) throw Problem("That folder does not contain EliteDangerous64.exe.");
        Remember(saved with { GameDir = dir, GameConfirmed = dir });
        ChooseGame();
        return Status();
    }

    object ConfirmGame()
    {
        if (game is null) throw Problem("No game folder to confirm.");
        Remember(saved with { GameConfirmed = game.Dir });
        return Status();
    }

    /// <summary>
    /// The one time besides saving a theme that Studio writes into the game folder, and only on the
    /// player's click: the HUD mod, fetched from its author's page (see ModInstall for why not carried).
    /// </summary>
    object InstallMod()
    {
        if (game is null) throw Problem("No game folder to install the HUD mod into.");
        if (game.Mod().Installed) throw Problem("The HUD mod is already in this game folder.");
        if (GameRunning()) throw Problem("Close Elite Dangerous first: the HUD mod cannot be installed while the game is running.");
        var (version, zip) = DownloadMod();
        int written = ModInstall.Unpack(zip, game.Dir);
        Log.Info($"Installed the HUD mod {version} into {game.Dir} ({written} files)");
        ChooseGame();   // what was known about the folder is from before
        return new { ok = true, version, written, status = Status() };
    }

    object Open(string what)
    {
        if (what is "backups") Directory.CreateDirectory(backupDir);
        if (what is "mythemes") Directory.CreateDirectory(themesDir);
        string? folder = what switch
        {
            "tool" => dataDir,
            "backups" => backupDir,
            "mythemes" => themesDir,
            "game" => game?.Dir,
            "edhm" => IniDir(),
            _ => null,
        };
        if (folder is null || !Directory.Exists(folder)) throw Problem("That folder was not found.");
        ShowFolder(folder);
        return new { ok = true };
    }

    // ------------------------------------------------------------ saving colours to the game

    object Save(JsonElement? body)
    {
        string iniDir = IniDir() ?? throw Problem("HUD color files were not found in the selected game folder.");
        var plan = ColourFiles.Plan(Loose.Field(body, "changes"));
        if (plan.Count == 0) return new { ok = true, written = 0, missing = Array.Empty<string>(), backup = (string?)null };

        // The first save after Studio starts keeps a copy of the files as they were, so a bad edit can be undone.
        string? backup = null;
        if (Loose.Truthy(Loose.Field(body, "backup")) || !backedUpThisSession)
        {
            backup = NewBackup("Before save");
            backedUpThisSession = true;
        }

        int written = 0;
        var missing = new List<string>();
        foreach (var (file, values) in plan)
        {
            string path = ColourFiles.PathOf(iniDir, file);
            if (!File.Exists(path)) { missing.AddRange(values.Keys.Select(key => $"{file}:{key}")); continue; }
            var notFound = new List<string>();
            string text = ColourFiles.Update(File.ReadAllText(path), values, out int count, notFound);
            if (count > 0) ColourFiles.Write(path, text);
            written += count;
            missing.AddRange(notFound.Select(key => $"{file}:{key}"));
        }

        bool running = GameRunning();
        Reload reload = written > 0 && running ? PressReloadKey() : new Reload(false, false, null);
        Log.Info($"Saved {written} setting(s) to the HUD colour files"
            + (reload.Sent ? " - F11 reload sent" : running ? " - F11 reload could not be sent automatically" : ""));
        return new { ok = true, written, missing, backup, running, reload };
    }

    // ------------------------------------------------------------ backups

    /// <summary>Copies the colour files into a new backup folder and returns its name; null when there are none to copy.</summary>
    string? NewBackup(string reason)
    {
        if (IniDir() is not string iniDir) return null;
        DateTime now = DateTime.Now;
        string name = NotForFileNames.Replace($"{now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)} {reason}", "-");
        string dir = Path.Combine(backupDir, name);
        ColourFiles.CopyAll(iniDir, dir);
        // info.json is written last: a backup without it was interrupted and is never offered for loading.
        var info = new { kind = "colors", reason, time = now.ToString("s", CultureInfo.InvariantCulture), edhm = game!.Mod().Version };
        File.WriteAllText(Path.Combine(dir, "info.json"), FileJson(info), Utf8NoMark);
        Log.Info($"Backup made: {name}");
        return name;
    }

    object MakeBackup(JsonElement? reason)
    {
        string why = Loose.Truthy(reason) ? Regex.Replace(Loose.Text(reason), @"[^\w \-]", "") : "Manual backup";
        string name = NewBackup(why) ?? throw Problem("No HUD color files were found, so there is nothing to back up.");
        return new { ok = true, name };
    }

    List<object> Backups()
    {
        var items = new List<object>();
        if (!Directory.Exists(backupDir)) return items;
        // Names start with the date and time, so sorting by name puts the newest first.
        foreach (string dir in Directory.GetDirectories(backupDir).OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            string infoFile = Path.Combine(dir, "info.json");
            JsonElement? info = File.Exists(infoFile) ? Loose.Object(File.ReadAllText(infoFile)) : null;
            JsonElement? kind = Loose.Field(info, "kind"), to = Loose.Field(info, "to");
            items.Add(new
            {
                name = Path.GetFileName(dir),
                // What info.json says is passed on as found; without one the backup was interrupted.
                time = info is null ? Directory.GetCreationTime(dir).ToString("s", CultureInfo.InvariantCulture) : (object?)Loose.Field(info, "time"),
                reason = info is null ? "Incomplete backup" : (object?)Loose.Field(info, "reason"),
                // "to", and a "kind" other than "colors", only appear on backups made by early versions of the old tool.
                kind = info is null ? "incomplete" : Loose.Truthy(kind) ? (object?)kind : "colors",
                edhm = (object?)Loose.Field(info, "edhm"),
                to = Loose.Truthy(to) ? (object?)to : null,
            });
        }
        return items;
    }

    object ReadBackup(string name)
    {
        if (!SafeName(name) || !Directory.Exists(Path.Combine(backupDir, name))) throw Problem("Backup not found.");
        return new { ok = true, name, files = ColourFiles.ReadAll(Path.Combine(backupDir, name)) };
    }

    /// <summary>
    /// True for a name that can only be a file or folder directly inside one of Studio's own folders.
    /// Asked of the names of things already on disk (a backup, a saved theme), so it is looser than
    /// the rule new themes are named by (<see cref="Paths.FileNameFor"/>): a theme file made by
    /// Elite HUD Studio or copied in by hand must still open and delete.
    /// </summary>
    static bool SafeName(string name) => name.Length is > 0 and <= 200 && !NotForFileNames.IsMatch(name) && !name.StartsWith('.');

    // ------------------------------------------------------------ saved themes

    object Themes()
    {
        var mine = new List<object>();
        if (Directory.Exists(themesDir))
            foreach (FileInfo file in new DirectoryInfo(themesDir).GetFiles("*.json").OrderByDescending(file => file.LastWriteTime))
                if (Loose.Object(File.ReadAllText(file.FullName)) is JsonElement theme)
                    mine.Add(new
                    {
                        id = Path.GetFileNameWithoutExtension(file.Name),
                        name = Loose.Text(Loose.Field(theme, "name")),
                        author = Loose.Text(Loose.Field(theme, "author")),
                        created = Loose.Text(Loose.Field(theme, "created")),
                        colors = Loose.Texts(Loose.Field(theme, "colors")),
                    });
        // "dir" and "edhmui" belonged to a theme source the old tool dropped. They are kept so the answer
        // has the same shape as the old helper's; the page reads only "mine".
        return new { ok = true, dir = (string?)null, edhmui = Array.Empty<object>(), mine };
    }

    object ReadTheme(string? source, string id)
    {
        if (!SafeName(id) || source != "mine") throw Problem("Theme not found.");
        string file = Path.Combine(themesDir, id + ".json");
        if (!File.Exists(file)) throw Problem("Theme not found.");
        JsonElement theme = Loose.Object(File.ReadAllText(file)) ?? throw Problem("That theme file could not be read.");
        return new { ok = true, id, name = Loose.Text(Loose.Field(theme, "name")), author = Loose.Text(Loose.Field(theme, "author")), values = Loose.Field(theme, "values") };
    }

    object SaveTheme(JsonElement? body)
    {
        string name = Loose.Text(Loose.Field(body, "name")).Trim();
        if (name.Length is 0 or > Paths.LongestFileName) throw Problem($"Please give the theme a name (up to {Paths.LongestFileName} characters).");
        string id = Paths.FileNameFor(name) ?? throw Problem("That name cannot be used as a file name.");
        var values = ColourFiles.Plan(Loose.Field(body, "values"));
        if (values.Count == 0) throw Problem("The theme has no values.");
        Directory.CreateDirectory(themesDir);
        // "hud-studio" is the name of the file format, shared with themes made by Elite HUD Studio.
        var theme = new
        {
            app = "hud-studio",
            version = 1,
            name,
            author = Loose.Text(Loose.Field(body, "author")),
            created = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
            colors = Loose.Texts(Loose.Field(body, "colors")),
            values,
        };
        File.WriteAllText(Path.Combine(themesDir, id + ".json"), FileJson(theme), Utf8NoMark);
        Log.Info($"Theme saved: {name}");
        return new { ok = true, id };
    }

    object DeleteTheme(string id)
    {
        if (!SafeName(id) || !File.Exists(Path.Combine(themesDir, id + ".json"))) throw Problem("Theme not found.");
        Recycle(Path.Combine(themesDir, id + ".json"));
        return new { ok = true };
    }

    /// <summary>
    /// First run only: copies (never moves) the themes saved with Elite HUD Studio into Studio's own
    /// folder, keeping any file that is already there. A practice copy is given no folders to copy
    /// from (<see cref="Paths.Practice"/>).
    /// </summary>
    void BringOverOldThemes()
    {
        if (saved.OldThemesCopied) return;
        int copied = 0;
        bool complete = true;
        foreach (string oldDir in oldThemeDirs)
        {
            try
            {
                if (!Directory.Exists(oldDir)) continue;
                Directory.CreateDirectory(themesDir);
                foreach (string file in Directory.GetFiles(oldDir, "*.json"))
                {
                    string target = Path.Combine(themesDir, Path.GetFileName(file));
                    if (File.Exists(target)) continue;
                    File.Copy(file, target);
                    copied++;
                }
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                complete = false;   // tried again the next time Studio starts
                Log.Error($"Could not bring over the saved themes from {oldDir}", problem);
            }
        }
        if (copied > 0) Log.Info($"Brought over {copied} saved theme(s) from Elite HUD Studio");
        if (complete) Remember(saved with { OldThemesCopied = true });
    }
}

/// <summary>
/// Reads JSON the forgiving way the old helper did: a missing field, a number where text was
/// expected or a file that is not JSON at all never throws, it just reads as "nothing there".
/// </summary>
internal static class Loose
{
    /// <summary>The JSON as parsed, or null when it is not valid JSON or not an object.</summary>
    public static JsonElement? Object(string json)
    {
        try
        {
            JsonElement parsed = JsonSerializer.Deserialize<JsonElement>(json);
            return parsed.ValueKind == JsonValueKind.Object ? parsed : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A field of an object; null when it is absent, set to null, or the owner is not an object.</summary>
    public static JsonElement? Field(JsonElement? owner, string name) =>
        owner is { ValueKind: JsonValueKind.Object } fields && fields.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value : null;

    /// <summary>Text as it is; a number exactly as it was written; nothing as "".</summary>
    public static string Text(JsonElement? value) => value switch
    {
        null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => "",
        { ValueKind: JsonValueKind.String } text => text.GetString()!,
        { ValueKind: JsonValueKind.True } => "True",     // the old helper's spelling, kept so the two never differ
        { ValueKind: JsonValueKind.False } => "False",
        { } other => other.GetRawText(),
    };

    public static bool Truthy(JsonElement? value) => value switch
    {
        null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.String } text => text.GetString()!.Length > 0,
        _ => true,
    };

    /// <summary>A list of texts from a JSON list (or a single text); anything else is an empty list.</summary>
    public static string[] Texts(JsonElement? list) => list switch
    {
        { ValueKind: JsonValueKind.String } one => [one.GetString()!],
        { ValueKind: JsonValueKind.Array } many => [.. many.EnumerateArray().Where(item => item.ValueKind != JsonValueKind.Null).Select(item => Text(item))],
        _ => [],
    };
}

/// <summary>
/// Deleting to the Windows Recycle Bin (on Linux, the desktop's Trash), so a theme or build deleted by mistake can be put back.
/// </summary>
internal static class RecycleBin
{
    const uint Delete = 3;                 // FO_DELETE
    const ushort Silent = 0x0004;          // FOF_SILENT: no progress window
    const ushort NoQuestions = 0x0010;     // FOF_NOCONFIRMATION
    const ushort ToRecycleBin = 0x0040;    // FOF_ALLOWUNDO
    const ushort NoErrorBox = 0x0400;      // FOF_NOERRORUI: nothing may pop up over the game

    public static void Send(string file)
    {
        if (!OperatingSystem.IsWindows())
        {
            string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } set ? set
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            ToTrash(file, Path.Combine(data, "Trash"), DateTime.Now);
            return;
        }
        // Windows wants a list of paths ending in an empty one, hence the extra '\0'.
        var operation = new FileOperation { Function = Delete, From = file + '\0', Flags = Silent | NoQuestions | ToRecycleBin | NoErrorBox };
        int result = SHFileOperation(ref operation);
        if (result != 0 || operation.Aborted) throw new IOException($"Windows could not move the file to the Recycle Bin (code {result}).");
    }

    /// <summary>
    /// Moves a file into a Trash as freedesktop.org describes it, the one the Linux desktops' file managers show: the
    /// file goes into its "files" folder under a name not taken there, and a note of the same name in its "info" folder
    /// says where it was and when it was deleted, so the file manager can put it back. The note is made first: that
    /// claims the name.
    /// </summary>
    internal static void ToTrash(string file, string trash, DateTime now)
    {
        string files = Path.Combine(trash, "files"), info = Path.Combine(trash, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);
        // Where the file was, written as in a web address, folder by folder ("Elite Studio" is "Elite%20Studio").
        string place = string.Join('/', Path.GetFullPath(file).Split('/').Select(Uri.EscapeDataString));
        string said = $"[Trash Info]\nPath={place}\nDeletionDate={now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\n";
        for (int copy = 1; ; copy++)
        {
            // "Explorer.json", then "Explorer.2.json", "Explorer.3.json"...
            string name = copy == 1 ? Path.GetFileName(file) : $"{Path.GetFileNameWithoutExtension(file)}.{copy}{Path.GetExtension(file)}";
            string note = Path.Combine(info, name + ".trashinfo"), kept = Path.Combine(files, name);
            if (File.Exists(note) || File.Exists(kept)) continue;
            try
            {
                using var writer = new StreamWriter(new FileStream(note, FileMode.CreateNew));
                writer.Write(said);
            }
            catch (IOException) when (File.Exists(note))
            {
                continue;   // another program took the name meanwhile
            }
            try
            {
                File.Move(file, kept);
                return;
            }
            catch
            {
                File.Delete(note);
                throw;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FileOperation
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref FileOperation operation);
}
