using System.Text.Json;
using System.Text.Json.Nodes;

namespace EliteStudio.App;

/// <summary>
/// The one settings file, settings.json in the data folder. Each studio keeps its own section
/// ("app", "ui", "market", ...) as a plain class, so adding a setting is adding a property.
/// </summary>
public sealed class Settings
{
    /// <summary>JSON style used everywhere in Studio: camelCase names, readable by the web pages.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly string file;
    readonly JsonObject root;

    public Settings(string file)
    {
        this.file = file;
        root = Load(file);
    }

    /// <summary>True once a section has been saved, whatever it holds: how a studio tells its very first run.</summary>
    public bool Has(string section) => root[section] is not null;

    /// <summary>True when a section has a value of that name saved (in any case, as sections are read): for a setting newer than the file.</summary>
    public bool Has(string section, string name) => root[section] is JsonObject saved && saved.Any(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A studio's section, or a fresh one with that class's defaults. A value of the wrong kind typed
    /// into the file by hand ("overlayOn": "yes") costs that section its saved values, never the start-up.
    /// </summary>
    public T Get<T>(string section) where T : new()
    {
        try
        {
            return root[section] is JsonNode node ? node.Deserialize<T>(Json) ?? new T() : new T();
        }
        catch (JsonException problem)
        {
            Log.Error($"settings.json: the \"{section}\" section could not be read, so its defaults are in use", problem);
            return new T();
        }
    }

    /// <summary>Replaces a studio's section and saves the file.</summary>
    public void Set<T>(string section, T value)
    {
        root[section] = JsonSerializer.SerializeToNode(value, Json);
        // Write beside the file and swap, so a crash mid-save cannot leave half a file.
        string temp = file + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(Json));
        File.Move(temp, file, overwrite: true);
    }

    static JsonObject Load(string file)
    {
        if (!File.Exists(file)) return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            // Keep the unreadable file for the owner to send in, and start clean.
            File.Move(file, file + ".unreadable", overwrite: true);
            Log.Error("settings.json could not be read; it was kept as settings.json.unreadable and defaults are in use");
            return new JsonObject();
        }
    }
}
