using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EliteStudio.Market;

// The pictures themselves, as WPF draws them in the overlay (Windows only).
static partial class StationPictures
{
    static readonly ConcurrentDictionary<string, ImageSource> loaded = new();

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
