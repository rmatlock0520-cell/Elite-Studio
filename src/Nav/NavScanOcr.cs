using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using EliteStudio.App;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinLanguage = Windows.Globalization.Language;

namespace EliteStudio.Nav;

/// <summary>
/// Nav scan's reading of a picture with Windows' own text reader (Windows.Media.Ocr, part of Windows 10 and 11: nothing
/// is fetched, nothing installed). The Navigation panel is tilted and its letters small, so, as the probe of
/// 15 October 2026 found on the owner's own pictures, the reader does best on the picture made larger: it is read at
/// two sizes (3,840 and 2,880 pixels wide), each whole and in overlapping pieces of a quarter of it (half its width and
/// half its height, moved on by half a piece), and every line of every look is given back in the picture's own pixels,
/// where it stands (the reader's own straightening of tilted text turned back), so the looks agree on where each row is.
/// A line touching the edge of a piece is left out: cut there, "C9-18" reads as "C9-1", another real-looking name.
/// The picture is read into memory first, so the file is never held open while it is read. Runs away from the calling
/// thread.
/// </summary>
internal static class NavScanOcr
{
    /// <summary>The widths the picture is read at: the probe's.</summary>
    static readonly int[] Widths = [3840, 2880];

    const string NoReader = "Windows has no English text reader on this PC, so Nav scan cannot read pictures. Add English in Windows Settings (Time & language, Language), then restart Studio.";
    const string Unreadable = "Studio could not open this picture: it may not be a picture, or it is damaged.";

    static readonly Lazy<OcrEngine?> Engine = new(Make, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Windows has an English text reader on this PC (the game's words are English).</summary>
    public static bool HasEnglish => Engine.Value is not null;

    /// <summary>English as the player's Windows has it, else any English Windows has: null when there is none.</summary>
    static OcrEngine? Make()
    {
        try
        {
            foreach (string tag in new[] { "en-US", "en-GB", "en" })
            {
                var language = new WinLanguage(tag);
                if (OcrEngine.IsLanguageSupported(language) && OcrEngine.TryCreateFromLanguage(language) is { } engine) return engine;
            }
            foreach (WinLanguage language in OcrEngine.AvailableRecognizerLanguages)
                if (language.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase) && OcrEngine.TryCreateFromLanguage(language) is { } engine)
                    return engine;
        }
        catch (Exception problem) when (problem is COMException or ArgumentException or TypeLoadException or PlatformNotSupportedException)
        {
            Log.Error("Windows' text reader could not be started", problem);
        }
        return null;
    }

    /// <summary>Every line Windows' text reader finds on the picture, in the picture's own pixels; or why it could not be read.</summary>
    public static Task<PictureText> Read(string path, CancellationToken cancel) => Task.Run(async () =>
    {
        if (Engine.Value is not { } engine) return new PictureText([], NoReader);
        Bitmap source;
        try
        {
            // Read whole first: Steam or the game may want the file again, and a picture is a few megabytes at most.
            source = new Bitmap(new MemoryStream(await File.ReadAllBytesAsync(path, cancel)));
        }
        catch (ArgumentException)
        {
            return new PictureText([], Unreadable);
        }
        using (source)
        {
            var lines = new List<ReadLine>();
            foreach (int width in Widths)
            {
                double scale = (double)width / source.Width;
                int height = (int)Math.Round(source.Height * scale);
                if (height < 8 || Math.Max(width, height) > OcrEngine.MaxImageDimension) continue;
                using var big = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (Graphics drawing = Graphics.FromImage(big))
                {
                    drawing.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    drawing.DrawImage(source, 0, 0, width, height);
                }
                var pieces = new List<Rectangle> { new(0, 0, width, height) };
                int pieceWidth = width / 2, pieceHeight = height / 2;
                for (int y = 0; y + pieceHeight <= height; y += pieceHeight / 2)
                    for (int x = 0; x + pieceWidth <= width; x += pieceWidth / 2)
                        pieces.Add(new Rectangle(x, y, pieceWidth, pieceHeight));
                foreach (Rectangle piece in pieces)
                {
                    cancel.ThrowIfCancellationRequested();
                    OcrResult result = await Recognize(engine, big, piece);
                    double angle = result.TextAngle ?? 0;
                    foreach (OcrLine line in result.Lines)
                    {
                        if (line.Words.Count == 0) continue;
                        Box[] boxes = [.. line.Words.Select(word => AsItStands(word.BoundingRect, angle, piece))];
                        if (Cut(boxes, piece, width, height)) continue;
                        lines.Add(new ReadLine([.. line.Words.Select((word, at) => new ReadWord(word.Text,
                            (piece.X + boxes[at].X) / scale, (piece.Y + boxes[at].Y) / scale, boxes[at].Width / scale, boxes[at].Height / scale))]));
                    }
                }
            }
            return new PictureText(lines);
        }
    }, cancel);

    /// <summary>One look at a piece of the picture.</summary>
    static async Task<OcrResult> Recognize(OcrEngine engine, Bitmap picture, Rectangle piece)
    {
        using Bitmap part = picture.Clone(piece, PixelFormat.Format32bppArgb);
        BitmapData data = part.LockBits(new Rectangle(0, 0, piece.Width, piece.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] pixels;
        try
        {
            pixels = new byte[data.Stride * piece.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        }
        finally
        {
            part.UnlockBits(data);
        }
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, piece.Width, piece.Height, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(pixels.AsBuffer());
        return await engine.RecognizeAsync(bitmap);
    }

    /// <summary>A word's box in a piece's own pixels.</summary>
    readonly record struct Box(double X, double Y, double Width, double Height);

    /// <summary>
    /// A word's box where the word stands in the piece. Windows' reader straightens tilted text before it reads it (the
    /// Navigation panel leans a few degrees, and each piece of it is straightened by its own angle), and gives the boxes
    /// as they stand straightened: turned about the piece's middle. Turned back by the angle it says, every look's boxes
    /// agree again, so a row of the panel read in different pieces falls on the same line of the picture (on the owner's
    /// pictures of 15 October 2026 the same row was otherwise placed up to a row's height apart).
    /// </summary>
    static Box AsItStands(Windows.Foundation.Rect box, double degrees, Rectangle piece)
    {
        if (degrees == 0) return new Box(box.X, box.Y, box.Width, box.Height);
        double turn = degrees * Math.PI / 180, cos = Math.Cos(turn), sin = Math.Sin(turn);
        double midX = piece.Width / 2.0, midY = piece.Height / 2.0;
        double dx = box.X + box.Width / 2 - midX, dy = box.Y + box.Height / 2 - midY;
        double x = midX + dx * cos - dy * sin, y = midY + dx * sin + dy * cos;
        return new Box(x - box.Width / 2, y - box.Height / 2, box.Width, box.Height);
    }

    /// <summary>A line that touches an edge of its piece that is not the picture's own: it may be cut there.</summary>
    static bool Cut(IEnumerable<Box> words, Rectangle piece, int width, int height)
    {
        foreach (Box box in words)
        {
            double margin = Math.Max(2, box.Height / 4);
            if ((piece.X > 0 && box.X < margin) || (piece.Right < width && box.X + box.Width > piece.Width - margin)
                || (piece.Y > 0 && box.Y < margin) || (piece.Bottom < height && box.Y + box.Height > piece.Height - margin))
                return true;
        }
        return false;
    }
}
