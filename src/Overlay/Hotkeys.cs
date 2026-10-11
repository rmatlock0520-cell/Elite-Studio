using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Overlay;

/// <summary>
/// The keys that show and hide the overlay's boxes from inside the game (F2 to F5 unless changed). Windows gives a
/// registered key to Studio whichever program is in front, so the keys are taken only while Elite is
/// the window in front and handed back the moment it is not: F2 keeps working in every other program.
/// </summary>
public sealed class Hotkeys : IDisposable
{
    const int KeyPressedMessage = 0x0312;   // WM_HOTKEY
    const uint NoRepeat = 0x4000;           // holding a key down counts as one press
    const uint ReloadKey = 0x7A;            // F11
    static readonly IntPtr MessagesOnly = new(-3);

    static readonly Dictionary<string, Key> KeyNames =
        Enum.GetNames<Key>().ToDictionary(name => name, name => Enum.Parse<Key>(name), StringComparer.OrdinalIgnoreCase);

    // Windows sends key presses to a window. This one exists only for that: it is never shown and is in no list of windows.
    readonly HwndSource sink = new(new HwndSourceParameters("Elite Studio hotkeys") { ParentWindow = MessagesOnly, WindowStyle = 0 });
    readonly GameState game;
    readonly Dictionary<string, (string Key, bool Registered)> tried = [];
    readonly HashSet<string> logged = [];
    string[] held = [];     // the actions whose keys are taken now; Windows knows each by its place here + 1
    string holding = "";    // the key texts they were taken with; empty while nothing is taken

    public Hotkeys(GameState game)
    {
        this.game = game;
        sink.AddHook(Receive);
    }

    /// <summary>A hotkey was pressed: the name of its action.</summary>
    public event Action<string>? Pressed;

    /// <summary>
    /// A key as the settings hold it ("F2", "Ctrl+Shift+H", "Alt+D1": Ctrl, Alt, Shift and Win, then one
    /// key by its Windows name) as the two numbers Windows wants. Capitals and spaces do not matter.
    /// Null when the text names no key, more than one, or has a word that is neither. (The Haul Overlay
    /// skipped words it did not know. Its key texts only came from its own key box; here they come from
    /// a page, and skipping would quietly take another key than the player chose: F5 on its own for
    /// "Control+F5".)
    /// </summary>
    internal static (uint Modifiers, uint Key)? Parse(string text)
    {
        uint modifiers = 0;
        uint? key = null;
        foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "alt": modifiers |= 1; break;
                case "ctrl": modifiers |= 2; break;
                case "shift": modifiers |= 4; break;
                case "win": modifiers |= 8; break;
                default:
                    if (key is not null || !KeyNames.TryGetValue(part, out Key named)) return null;
                    key = (uint)KeyInterop.VirtualKeyFromKey(named);
                    break;
            }
        }
        return key is > 0 ? (modifiers, key.Value) : null;   // 0 is a name Windows has no key for, such as "None"
    }

    /// <summary>
    /// What the game does on a hotkey's key: the actions on it in the game's own key bindings and, for
    /// F11, the HUD colour reload. That one is in no binding file, and Studio UI presses F11 in the
    /// game itself after every theme save, which would set off an overlay action given the same key.
    /// </summary>
    internal static IReadOnlyList<string> GameUses(KeyBindings bindings, string key)
    {
        IReadOnlyList<string> actions = bindings.UsedBy(key);
        return Parse(key)?.Key == ReloadKey ? [.. actions, "HUD colour reload (Studio presses F11 in the game after every theme save)"] : actions;
    }

    /// <summary>
    /// Takes the keys when Elite has come to the front (or the keys were changed while it is), and
    /// gives them back when it no longer is. Call on every tick. A key Windows will not give, and a key
    /// the game itself uses, is no error: it is logged once and reported through <see cref="Registered"/>
    /// and the settings route, so a page can warn the player. True when the keys were just asked of
    /// Windows, so what <see cref="Registered"/> says may be new.
    /// </summary>
    public bool Sync(bool eliteInFront, OverlaySettings settings)
    {
        var keys = settings.Hotkeys();
        string wanted = eliteInFront ? string.Join('\n', keys.Select(hotkey => hotkey.Key)) : "";
        if (wanted == holding) return false;
        Release();
        holding = wanted;
        if (!eliteInFront) return false;

        held = [.. keys.Select(hotkey => hotkey.Action)];
        for (int i = 0; i < keys.Count; i++)
        {
            (string action, _, string key) = keys[i];
            (uint Modifiers, uint Key)? parsed = Parse(key);
            bool registered = parsed is { } known && RegisterHotKey(sink.Handle, i + 1, known.Modifiers | NoRepeat, known.Key);
            tried[action] = (key, registered);

            string? remark = parsed is null ? "is not a key Studio knows, so it does nothing"
                : !registered ? "is already taken by another program or by another Studio hotkey, so it does nothing"
                : GameUses(game.Bindings, key) is { Count: > 0 } uses ? $"is also a key the game itself uses ({string.Join(", ", uses)})"
                : null;
            if (remark is not null && logged.Add($"{action} {key} {remark}")) Log.Info($"Hotkey \"{action}\": {key} {remark}");
        }
        return true;
    }

    /// <summary>
    /// Whether Windows gave Studio that action's key the last time the keys were taken. Null when
    /// that has not been tried with this key yet: it is tried the next time Elite is in front.
    /// </summary>
    public bool? Registered(string action, string key) =>
        tried.TryGetValue(action, out var last) && last.Key == key ? last.Registered : null;

    public void Dispose()
    {
        Release();
        sink.Dispose();
    }

    /// <summary>What Windows sends to the hidden window: for a hotkey, the number it was registered under.</summary>
    IntPtr Receive(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == KeyPressedMessage && wParam.ToInt64() - 1 is >= 0 and long index && index < held.Length)
        {
            handled = true;
            Pressed?.Invoke(held[index]);
        }
        return IntPtr.Zero;
    }

    void Release()
    {
        for (int id = 1; id <= held.Length; id++) UnregisterHotKey(sink.Handle, id);
        held = [];
    }

    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr window, int id);
}
