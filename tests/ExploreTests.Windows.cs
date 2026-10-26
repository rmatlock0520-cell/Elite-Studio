using System.Windows.Controls;
using EliteStudio.Nav;
using static EliteStudio.Tests.OverlayTests;   // OnWindowThread and TextOf

namespace EliteStudio.Tests;

// The exploring trip's line in the overlay, which only the Windows build has.
public sealed partial class ExploreTests
{
    static partial void NothingForTheOverlay(ExploreTrip trip) => Assert.Null(trip.OverlayLine("#3D8BFF"));   // nothing to tell

    // ------------------------------------------------------------ the overlay's line

    [Fact]
    public void The_overlay_line_tells_of_the_system_and_the_trip_once_there_is_a_first_to_tell_of() => OnWindowThread(() =>
    {
        var trip = new ExploreTrip(watcher.State, SettingsFile());
        watcher.Poll();
        Play(Legs[1]);
        Assert.Null(trip.OverlayLine("#FF8800"));   // out of the station, but nothing new yet

        Play(Legs[2][..2]);                         // the jump, and its barycentre: the star's scan has not come yet
        Assert.Null(trip.OverlayLine("#FF8800"));
        Play(Legs[2][2..]);
        TextBlock line = trip.OverlayLine("#FF8800")!;
        Assert.Equal("EXPLORING   new system   ·   5 of 9 bodies   ·   trip: 1 new system, 5 first discoveries, 1 first mapped", TextOf(line));
        Assert.Equal(14.5, line.FontSize);

        Play(Legs[3]);
        Assert.Equal("EXPLORING   already discovered   ·   2 bodies scanned   ·   trip: 1 new system, 6 first discoveries, 1 first mapped", TextOf(trip.OverlayLine("#FF8800")!));

        // Docked there is no line, and none once the data is sold.
        Play("""{ "timestamp":"2026-10-09T11:00:00Z", "event":"Docked", "StationName":"Far Outpost", "StationType":"Outpost", "StarSystem":"Practice Sector MX-T c3-4", "SystemAddress":4004, "MarketID":3200000002 }""");
        Assert.Null(trip.OverlayLine("#FF8800"));
        Play("""{ "timestamp":"2026-10-09T11:01:00Z", "event":"SellExplorationData", "Systems":[ "Practice Sector LX-T d3-7" ], "Discovered":[ "Practice Sector LX-T d3-7 A" ], "BaseValue":1202000, "Bonus":601000, "TotalEarnings":1803000 }""",
            """{ "timestamp":"2026-10-09T11:05:00Z", "event":"Undocked", "StationName":"Far Outpost", "StationType":"Outpost", "MarketID":3200000002 }""");
        Assert.Null(trip.OverlayLine("#FF8800"));
    });

    [Fact]
    public void The_nav_box_shows_the_exploring_line_only_when_the_route_has_nothing_to_show_and_the_haul_box_never_does() => OnWindowThread(() =>
    {
        // Wired as the app wires it, without a window: no trade in reach, a Studio Nav route saved, and the trip.
        using var rig = new BoxRig(HaulRig.In(HaulRig.Alpha, docked: false), BoxRig.Nothing, route: BoxRig.RouteVia(HaulRig.Alpha, HaulRig.Beta));
        List<string> Nav() => HaulOverlayDrawingTests.Read(rig.Box.NavContent());
        List<string> Haul() => HaulOverlayDrawingTests.Read(rig.Box.Content());

        rig.Haul.Play([.. Legs[1], .. Legs[2]]);
        Assert.StartsWith("NEXT STOP   Beta", Assert.Single(Nav()));   // the route's next stop comes first

        rig.Nav.Clear();
        Assert.Equal(["EXPLORING   new system   ·   5 of 9 bodies   ·   trip: 1 new system, 5 first discoveries, 1 first mapped"], Nav());

        // The haul box has nothing of the trip: where the player is with hauling's message, and then a trade, which takes nothing from the nav box.
        Assert.StartsWith("ELITE STUDIO   " + New, Haul()[0]);
        rig.Haul.Search.Answer = BoxRig.Trades;
        rig.Hauling.SearchAgain();
        Assert.StartsWith("GOLD", Haul()[0]);
        Assert.DoesNotContain(Haul(), line => line.StartsWith("EXPLORING", StringComparison.Ordinal));
        Assert.StartsWith("EXPLORING", Assert.Single(Nav()));

        // The trip put aside: the nav box says that no route is being followed.
        rig.Explore.Reset();
        Assert.Equal(["No route is being followed. Plot one in Studio Nav."], Nav());
    });
}
