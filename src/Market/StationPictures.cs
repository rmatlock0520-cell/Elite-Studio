using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EliteStudio.Market;

/// <summary>
/// The small pictures of the kinds of station that the overlay draws before a station's name. The
/// pages show the same ones by the same rules (Studio.stationIcon in web\studio.js): a change to
/// which kind gets which picture belongs in both. The pictures are the files in Market\Stations,
/// carried inside the program; only their shape is used, so one picture takes any colour.
/// </summary>
static class StationPictures
{
    // What the kind's name holds, as the journal or Spansh spells it with only its letters kept, and the picture
    // for it. The first that fits counts: a "Planetary Outpost" is a port, not an outpost. A building site has none.
    static readonly (string Holds, string? Picture)[] Kinds =
    [
        ("construction", null), ("carrier", "carrier"), ("settlement", "settlement"), ("surfacestation", "settlement"),
        ("craterport", "port"), ("planetaryport", "port"), ("crateroutpost", "port"), ("planetaryoutpost", "port"),
        ("coriolis", "coriolis"), ("dodec", "coriolis"), ("orbis", "orbis"), ("ocellus", "ocellus"), ("bernal", "ocellus"),
        ("asteroid", "asteroid"), ("megaship", "megaship"), ("outpost", "outpost"),
    ];

    static readonly ConcurrentDictionary<string, ImageSource> loaded = new();

    /// <summary>The name of the picture for a kind of station ("port", "carrier"); null for a kind that has none, or is not known.</summary>
    public static string? Kind(string? stationType)
    {
        string name = string.Concat((stationType ?? "").Where(char.IsAsciiLetter)).ToLowerInvariant();
        return Kinds.FirstOrDefault(kind => name.Contains(kind.Holds, StringComparison.Ordinal)).Picture;
    }

    /// <summary>
    /// The colour a station's picture is drawn in, which says where it is: purple in space, orange
    /// on the ground, yellow for a fleet carrier. The pages use the same three (.st-icon in web\studio.css).
    /// </summary>
    public static string Colour(string? stationType) => Kind(stationType) switch
    {
        "port" or "settlement" => Ground,
        "carrier" => Carrier,
        _ => Space,
    };

    public const string Space = "#FFB48CFF", Ground = "#FFFF9A2E", Carrier = "#FFFFD83D";

    /// <summary>The picture for a kind of station; null for a kind that has none, or is not known.</summary>
    public static ImageSource? Of(string? stationType) => Kind(stationType) is { } kind ? loaded.GetOrAdd(kind, Load) : null;

    static ImageSource Load(string kind)
    {
        using Stream file = typeof(StationPictures).Assembly.GetManifestResourceStream($"Station.{kind}.png")
            ?? throw new InvalidOperationException($"The station picture {kind}.png is missing from the program");
        var picture = new BitmapImage();
        picture.BeginInit();
        picture.CacheOption = BitmapCacheOption.OnLoad;   // read now: the file is closed when this returns
        picture.StreamSource = file;
        picture.EndInit();
        picture.Freeze();
        return picture;
    }
}
