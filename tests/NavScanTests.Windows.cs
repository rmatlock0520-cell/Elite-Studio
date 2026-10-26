using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using EliteStudio.Nav;

namespace EliteStudio.Tests;

// Windows' own text reader, which only the Windows build has (the picture is drawn with Windows' drawing kit too).
public sealed partial class NavScanTests
{
    [Fact]
    public async Task Windows_text_reader_reads_a_tilted_panel_drawn_white_on_dark()
    {
        Assert.SkipUnless(NavScanOcr.HasEnglish, "Windows has no English text reader on this PC.");

        // A Navigation panel of the test's own: white capitals on near-black, tilted a little as the cockpit tilts it,
        // with the distance column far to the right.
        string path = Path.Combine(temp, "drawn.png");
        using (var picture = new Bitmap(1920, 1080))
        {
            using (Graphics drawing = Graphics.FromImage(picture))
            {
                drawing.Clear(Color.FromArgb(16, 20, 28));
                drawing.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                drawing.SmoothingMode = SmoothingMode.AntiAlias;
                drawing.TranslateTransform(560, 600);
                drawing.RotateTransform(-1.3f);
                using var font = new Font("Arial", 20, FontStyle.Regular, GraphicsUnit.Pixel);
                string[] rows = ["COL 285 SECTOR JR-S B18-1", "COL 285 SECTOR RI-J C9-18", "COL 285 SECTOR WO-J A38-0"];
                string[] distances = ["4.23Ly", "5.29Ly", "5.34Ly"];
                for (int row = 0; row < rows.Length; row++)
                {
                    drawing.DrawString(rows[row], font, Brushes.White, 30, row * 36);
                    drawing.DrawString(distances[row], font, Brushes.White, 660, row * 36);
                }
            }
            picture.Save(path, ImageFormat.Png);
        }

        PictureText text = await NavScanOcr.Read(path, TestContext.Current.CancellationToken);

        Assert.Null(text.Problem);
        Assert.Contains(text.Lines, line => line.Text.Contains("SECTOR", StringComparison.OrdinalIgnoreCase));
        PictureNames names = NavScanNames.Read(text, Ngarawe);
        Assert.Contains(names.Names, name => name.Variants.Any(variant => variant.Name == "Col 285 Sector WO-J a38-0"));
        Assert.Contains(names.Names, name => name.Variants.Any(variant => variant.Name == "Col 285 Sector RI-J c9-18"));
        // Each line in the picture's own pixels, though it was read at twice the size; and where it stands, though the
        // reader straightened the tilted text of each piece by its own angle: every look puts the row at one height.
        Assert.All(text.Lines.SelectMany(line => line.Words), word => Assert.InRange(word.X, 0, 1920));
        double[] heights = [.. text.Lines.Where(line => line.Text.Contains("RI-J", StringComparison.OrdinalIgnoreCase)).Select(line => line.Words[0].CentreY)];
        Assert.True(heights.Length >= 2 && heights.Max() - heights.Min() <= 4, string.Join(", ", heights));

        // A file that is no picture is said to be none.
        string notes = Path.Combine(temp, "notes.jpg");
        File.WriteAllText(notes, "not a picture");
        Assert.Equal("Studio could not open this picture: it may not be a picture, or it is damaged.", (await NavScanOcr.Read(notes, TestContext.Current.CancellationToken)).Problem);
    }
}
