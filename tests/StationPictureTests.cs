using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using EliteStudio.Market;
using EliteStudio.Overlay;
using static EliteStudio.Tests.OverlayTests;   // OnWindowThread and TextOf

namespace EliteStudio.Tests;

/// <summary>The picture of a station's kind: which kind gets which, and how a line of the overlay carries it.</summary>
public class StationPictureTests
{
    [Theory]
    // as the game's journal and Ardent Insight spell the kinds
    [InlineData("Coriolis", "coriolis")]
    [InlineData("Dodec", "coriolis")]
    [InlineData("Orbis", "orbis")]
    [InlineData("Ocellus", "ocellus")]
    [InlineData("Bernal", "ocellus")]
    [InlineData("Outpost", "outpost")]
    [InlineData("AsteroidBase", "asteroid")]
    [InlineData("MegaShip", "megaship")]
    [InlineData("CraterPort", "port")]
    [InlineData("CraterOutpost", "port")]           // on the ground: not the outpost in space
    [InlineData("SurfaceStation", "settlement")]
    [InlineData("OnFootSettlement", "settlement")]
    [InlineData("FleetCarrier", "carrier")]
    [InlineData("StrongholdCarrier", "carrier")]
    // as Spansh words them
    [InlineData("Coriolis Starport", "coriolis")]
    [InlineData("Asteroid base", "asteroid")]
    [InlineData("Mega ship", "megaship")]
    [InlineData("Planetary Port", "port")]
    [InlineData("Planetary Outpost", "port")]
    [InlineData("Surface Settlement", "settlement")]
    [InlineData("Drake-Class Carrier", "carrier")]
    // a building site has no picture, nor has a kind nobody knows, nor no kind at all
    [InlineData("PlanetaryConstructionDepot", null)]
    [InlineData("Space Construction Depot", null)]
    [InlineData("Something New", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Each_kind_of_station_has_its_picture(string? stationType, string? picture)
    {
        Assert.Equal(picture, StationPictures.Kind(stationType));
        Assert.Equal(picture is not null, StationPictures.Of(stationType) is not null);   // and the file for it is in the program
    }

    [Theory]
    [InlineData("Coriolis", StationPictures.Space)]
    [InlineData("Outpost", StationPictures.Space)]
    [InlineData("MegaShip", StationPictures.Space)]
    [InlineData("Asteroid base", StationPictures.Space)]
    [InlineData("CraterOutpost", StationPictures.Ground)]
    [InlineData("Planetary Port", StationPictures.Ground)]
    [InlineData("OnFootSettlement", StationPictures.Ground)]
    [InlineData("FleetCarrier", StationPictures.Carrier)]
    [InlineData("Drake-Class Carrier", StationPictures.Carrier)]
    public void The_pictures_colour_says_where_the_station_is(string stationType, string colour) =>
        Assert.Equal(colour, StationPictures.Colour(stationType));

    [Fact]
    public void A_line_carries_the_picture_before_the_name_and_reads_as_before() => OnWindowThread(() =>
    {
        TextBlock line = Draw.Text(15).Picture(StationPictures.Of("CraterOutpost")).Add("Stupka Berth", bold: true);

        var shape = Assert.IsType<Rectangle>(Assert.IsType<InlineUIContainer>(line.Inlines.FirstInline).Child);
        Assert.IsType<ImageBrush>(shape.OpacityMask);
        Assert.False(shape.IsHitTestVisible);   // a click goes to the line behind it
        Assert.Equal("Stupka Berth", TextOf(line));

        // The colour given is the picture's; no picture leaves the line as it was.
        var gold = (Rectangle)((InlineUIContainer)Draw.Text(15).Picture(StationPictures.Of("Orbis"), Draw.Gold).Inlines.FirstInline).Child;
        Assert.Equal((Color)ColorConverter.ConvertFromString(Draw.Gold), ((SolidColorBrush)gold.Fill).Color);
        Assert.Empty(Draw.Text(15).Picture(StationPictures.Of("Something New")).Inlines);
    });
}
