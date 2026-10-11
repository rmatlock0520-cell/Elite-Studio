using System.Text.RegularExpressions;
using System.Xml.Linq;
using EliteStudio.App;

namespace EliteStudio.Core;

/// <summary>
/// The keyboard keys Elite itself uses, read from the player's active key-binding files (and, for a
/// preset the player never changed, from the game's own copy of it), so Studio can warn before one
/// of its own hotkeys lands on a key that already does something in the game.
/// </summary>
public sealed class KeyBindings
{
    /// <summary>No binding files read (yet): every key counts as free.</summary>
    public static readonly KeyBindings None = new([], new Dictionary<string, List<string>>());

    // Names Windows (WPF's Key list, which Studio's hotkey settings use) and the game disagree on.
    // Letters, F-keys and the rest are spelled the same by both.
    static readonly Dictionary<string, string> GameName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["D0"] = "0", ["D1"] = "1", ["D2"] = "2", ["D3"] = "3", ["D4"] = "4",
        ["D5"] = "5", ["D6"] = "6", ["D7"] = "7", ["D8"] = "8", ["D9"] = "9",
        ["NumPad0"] = "Numpad_0", ["NumPad1"] = "Numpad_1", ["NumPad2"] = "Numpad_2", ["NumPad3"] = "Numpad_3",
        ["NumPad4"] = "Numpad_4", ["NumPad5"] = "Numpad_5", ["NumPad6"] = "Numpad_6", ["NumPad7"] = "Numpad_7",
        ["NumPad8"] = "Numpad_8", ["NumPad9"] = "Numpad_9",
        ["Add"] = "Numpad_Add", ["Subtract"] = "Numpad_Subtract", ["Multiply"] = "Numpad_Multiply",
        ["Divide"] = "Numpad_Divide", ["Decimal"] = "Numpad_Decimal",
        ["Up"] = "UpArrow", ["Down"] = "DownArrow", ["Left"] = "LeftArrow", ["Right"] = "RightArrow",
        ["Return"] = "Enter", ["Back"] = "Backspace", ["Capital"] = "CapsLock", ["Prior"] = "PageUp", ["Next"] = "PageDown",
        ["LeftCtrl"] = "LeftControl", ["RightCtrl"] = "RightControl",
        ["OemMinus"] = "Minus", ["OemPlus"] = "Equals", ["Oem4"] = "LeftBracket", ["OemOpenBrackets"] = "LeftBracket",
        ["Oem6"] = "RightBracket", ["OemCloseBrackets"] = "RightBracket", ["Oem1"] = "SemiColon", ["OemSemicolon"] = "SemiColon",
        ["Oem7"] = "Apostrophe", ["OemQuotes"] = "Apostrophe", ["OemComma"] = "Comma", ["OemPeriod"] = "Period",
        ["Oem2"] = "Slash", ["OemQuestion"] = "Slash", ["Oem5"] = "BackSlash", ["OemPipe"] = "BackSlash",
        ["Oem3"] = "Grave", ["OemTilde"] = "Grave",
    };

    readonly Dictionary<string, List<string>> actionsByKey;

    KeyBindings(IReadOnlyList<string> presets, Dictionary<string, List<string>> actionsByKey)
    {
        Presets = presets;
        this.actionsByKey = actionsByKey;
    }

    /// <summary>The binding presets the game says are active, e.g. "Custom".</summary>
    public IReadOnlyList<string> Presets { get; }

    /// <summary>Every keyboard key the game uses, by the game's own name without "Key_": "F1", "LeftShift", "Numpad_0".</summary>
    public IReadOnlyCollection<string> Keys => actionsByKey.Keys;

    /// <summary>
    /// The game actions on a key, e.g. "F1" gives "ShowPGScoreSummaryInput"; empty when the key is free.
    /// Takes the game's name or the Windows one ("Up", "D1", "OemTilde"), and a full hotkey such as
    /// "Ctrl+Shift+F2": only its last part is looked up, because the game reads the keyboard directly
    /// and still sees F2 go down whatever is held with it.
    /// </summary>
    public IReadOnlyList<string> UsedBy(string key)
    {
        string name = key[(key.LastIndexOf('+') + 1)..].Trim();
        if (name.StartsWith("Key_", StringComparison.OrdinalIgnoreCase)) name = name[4..];
        if (GameName.TryGetValue(name, out string? theGames)) name = theGames;
        return actionsByKey.TryGetValue(name, out List<string>? actions) ? actions : [];
    }

    /// <summary>The files in the bindings folder that decide the result; the watcher reloads when one changes.</summary>
    internal static FileInfo[] Files(string bindingsDir)
    {
        try
        {
            var dir = new DirectoryInfo(bindingsDir);
            return [.. dir.EnumerateFiles("*.start"), .. dir.EnumerateFiles("*.binds")];
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            return [];   // no bindings folder (yet) is not an error
        }
    }

    /// <summary>
    /// Reads the active presets. StartPreset.*.start lists them (one line each for general, ship,
    /// SRV and on-foot controls); each is a "Name.4.2.binds" file here once the player has changed
    /// a control in it. A preset the player uses as it came ("KeyboardMouseOnly") has no file here:
    /// it is read from the game's own folder, ControlSchemes\Name.binds. Without a game folder, or
    /// with no such file in it, that preset is skipped and its keys stay unknown.
    /// A file that is locked or half-written this moment is not answered with <see cref="None"/>,
    /// which would call every key free: the IOException, UnauthorizedAccessException or
    /// XmlException goes to the caller, who keeps the keys it has and tries again.
    /// </summary>
    /// <param name="gameDir">Finds the folder the game is installed in; null when nothing can. Asked only when a preset in use has no file in the bindings folder.</param>
    internal static KeyBindings Load(string bindingsDir, Func<string?>? gameDir)
    {
        FileInfo[] files = Files(bindingsDir);
        FileInfo? start = files.Where(f => f.Name.StartsWith("StartPreset", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (start is null) return None;

        string[] presets;
        using (var reader = new StreamReader(Open(start.FullName)))
        {
            presets = [.. reader.ReadToEnd().Split('\r', '\n').Select(line => line.Trim()).Where(line => line.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        var actionsByKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();   // for the log, written only after everything was read: a read that fails is tried again every Poll
        var game = new Lazy<string?>(() => gameDir?.Invoke());   // looked for once, however many presets need it
        foreach (string preset in presets)
        {
            // "Custom.4.2.binds" beats "Custom.4.1.binds" beats "Custom.binds": the game writes a new
            // file when the format moves on and leaves the old ones behind.
            var pattern = new Regex($@"^{Regex.Escape(preset)}(\.(\d+)\.(\d+))?\.binds$", RegexOptions.IgnoreCase);
            string? binds = files
                .Select(f => (File: f, Name: pattern.Match(f.Name)))
                .Where(f => f.Name.Success)
                .OrderByDescending(f => f.Name.Groups[1].Success ? new Version(f.Name.Groups[1].Value[1..]) : new Version(0, 0))
                .Select(f => f.File.FullName).FirstOrDefault();
            // A preset used as it came has no file here: the copy that ships with the game is read instead.
            // (The name comes out of a text file, so only a plain file name is looked for in the game's folder.)
            if (binds is null && Paths.IsFileName(preset) && game.Value is { } folder)
            {
                string shipped = Path.Combine(folder, "ControlSchemes", preset + ".binds");
                if (File.Exists(shipped))
                {
                    binds = shipped;
                    notes.Add($"Key bindings: preset \"{preset}\" is read from the game's own copy, {shipped}");
                }
            }
            if (binds is null) notes.Add($"Key bindings: preset \"{preset}\" has no file in the bindings folder or among the game's own presets, so its keys are not known");
            else Read(binds, actionsByKey);
        }
        foreach (string note in notes) Log.Info(note);
        return new KeyBindings(presets, actionsByKey);
    }

    static FileStream Open(string file) => new(file, FileMode.Open, FileAccess.Read, GameWatcher.GameMayWrite);

    // <Root><ActionName><Primary Device="Keyboard" Key="Key_Q"><Modifier Device="Keyboard" Key="Key_LeftShift"/></Primary>...
    static void Read(string file, Dictionary<string, List<string>> actionsByKey)
    {
        using FileStream stream = Open(file);
        foreach (XElement action in XDocument.Load(stream).Root?.Elements() ?? [])
        {
            // the key itself and any key that has to be held with it: both are keys the game listens to
            foreach (XElement key in action.Descendants().Where(e => (string?)e.Attribute("Device") == "Keyboard"))
            {
                string name = (string?)key.Attribute("Key") ?? "";
                if (name.StartsWith("Key_", StringComparison.Ordinal)) name = name[4..];
                if (name.Length == 0) continue;
                if (!actionsByKey.TryGetValue(name, out List<string>? actions)) actionsByKey[name] = actions = [];
                if (!actions.Contains(action.Name.LocalName)) actions.Add(action.Name.LocalName);
            }
        }
    }
}
