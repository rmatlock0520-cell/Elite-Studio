using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EliteStudio.UI;

/// <summary>
/// The four HUD colour files of the game's HUD mod (EDHM-ini\*.ini): reading them whole, checking
/// the values a page wants to store, and changing those values without disturbing anything else.
///
/// This is the same rewrite the old Elite HUD Studio helper did, and tests\Fixtures\ui holds its
/// exact output: only the number after "key =" inside [Constants] changes. Comments, spacing, line
/// endings, key order, other sections and a UTF-8 byte-order mark all stay byte for byte, because
/// the mod's files are hand-laid-out and players compare them with other tools.
/// </summary>
internal static class ColourFiles
{
    /// <summary>File names without ".ini". The order is the order every answer lists them in.</summary>
    public static readonly string[] Names = ["Startup-Profile", "Advanced", "SuitHud", "XML-Profile"];

    public static string PathOf(string dir, string name) => Path.Combine(dir, name + ".ini");

    /// <summary>The whole text of each colour file that exists in a folder (the game's, or a backup's).</summary>
    public static Dictionary<string, string> ReadAll(string dir)
    {
        var texts = new Dictionary<string, string>();
        foreach (string name in Names)
            if (File.Exists(PathOf(dir, name))) texts[name] = File.ReadAllText(PathOf(dir, name));
        return texts;
    }

    public static void CopyAll(string fromDir, string toDir)
    {
        Directory.CreateDirectory(toDir);
        foreach (string name in Names)
            if (File.Exists(PathOf(fromDir, name))) File.Copy(PathOf(fromDir, name), PathOf(toDir, name), overwrite: true);
    }

    /// <summary>
    /// Checks {"Advanced": {"x232": "0.3278"}, ...} from a page and returns it as file -> key -> value text.
    /// One bad entry refuses the lot. Values are kept as the text that was sent and never pass through
    /// a number type, so what the page rounded is exactly what lands in the file.
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>> Plan(JsonElement? changes)
    {
        var plan = new Dictionary<string, Dictionary<string, string>>();
        if (changes is not { ValueKind: JsonValueKind.Object } files) return plan;
        foreach (JsonProperty file in files.EnumerateObject())
        {
            if (!Names.Contains(file.Name)) throw new InvalidOperationException($"Unknown settings file: {file.Name}");
            var values = new Dictionary<string, string>();
            if (file.Value.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty setting in file.Value.EnumerateObject())
                {
                    if (!Regex.IsMatch(setting.Name, @"^[xyzw]\d{1,3}$")) throw new InvalidOperationException($"Bad setting name: {setting.Name}");
                    string value = Loose.Text(setting.Value);
                    if (!Regex.IsMatch(value, @"^-?\d{1,4}(\.\d{1,6})?$")) throw new InvalidOperationException($"Bad value for {setting.Name}: {value}");
                    values[setting.Name] = value;
                }
            if (values.Count > 0) plan[file.Name] = values;
        }
        return plan;
    }

    /// <summary>
    /// Puts each value in place of the number on the first "key = number" line inside [Constants].
    /// Keys with no such line (absent, commented out, or not a number) are added to missing; nothing is appended.
    /// </summary>
    public static string Update(string text, Dictionary<string, string> values, out int count, List<string> missing)
    {
        count = 0;
        // The section runs from its header line to the next line that starts another section.
        Match header = Regex.Match(text, @"(?im)^[ \t]*\[Constants\][^\n]*");
        if (!header.Success) throw new InvalidOperationException("The file has no [Constants] section.");
        int start = header.Index + header.Length;
        Match next = Regex.Match(text[start..], @"(?m)^[ \t]*\[");
        int end = next.Success ? start + next.Index : text.Length;

        string body = text[start..end];
        foreach (var (key, value) in values)
        {
            // The key goes into the pattern as it is: Plan only lets through a letter and up to three digits.
            Match line = Regex.Match(body, @"(?m)^([ \t]*" + key + @"[ \t]*=[ \t]*)(-?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)");
            if (!line.Success) { missing.Add(key); continue; }
            Group number = line.Groups[2];
            body = body[..number.Index] + value + body[(number.Index + number.Length)..];
            count++;
        }
        return text[..start] + body + text[end..];
    }

    /// <summary>
    /// Replaces a colour file in one step, so the game never reads a half-written one: the text goes
    /// to a file beside it first and is then swapped in. A UTF-8 byte-order mark is kept if the file had one.
    /// </summary>
    public static void Write(string path, string text)
    {
        bool hadMark = false;
        if (File.Exists(path))
        {
            using FileStream stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[3];
            hadMark = stream.ReadAtLeast(head, 3, throwOnEndOfStream: false) == 3 && head.SequenceEqual<byte>([0xEF, 0xBB, 0xBF]);
        }
        string temp = path + ".elitestudio-tmp";
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(hadMark));
            if (!File.Exists(path)) { File.Move(temp, path); return; }
            try
            {
                File.Replace(temp, path, null);
            }
            catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
            {
                // Some folders allow changing a file but not swapping it for another: write over it instead.
                File.Copy(temp, path, overwrite: true);
            }
        }
        finally
        {
            // Nothing of Studio's may stay in the game's folder, least of all when the save failed.
            // (After a swap that worked the file is already gone, and this does nothing.)
            File.Delete(temp);
        }
    }
}
