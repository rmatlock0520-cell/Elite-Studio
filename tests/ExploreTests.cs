using System.Windows.Controls;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Nav;
using static EliteStudio.Tests.OverlayTests;   // OnWindowThread and TextOf

namespace EliteStudio.Tests;

/// <summary>
/// The live count of an exploring trip, and its line in the overlay. No recording of real play
/// holds a first discovery (the owner's journals are from well-travelled space: every scan in them
/// says "WasDiscovered":true), so the trip flown here is tests\Fixtures\nav\first-discoveries.log:
/// written by hand in the game's own layout, each line cut down from a recorded one, with made-up
/// systems and "WasDiscovered" and "WasMapped" set to false where the story needs it. The story:
/// docked at home; a system someone discovered before; a new one with two stars, three planets
/// and a belt cluster, one planet mapped; a system whose star is known but whose planet is not;
/// then back to the new one, where its star is scanned again and another planet is mapped.
/// The recordings themselves are replayed too: they must count their jumps and scans and no firsts.
/// </summary>
public sealed class ExploreTests : IDisposable
{
    const string Home = "Practice Home", Known = "Practice Sector KX-T c3-0", New = "Practice Sector LX-T d3-7", HalfKnown = "Practice Sector MX-T c3-4";

    /// <summary>The made-up trip, cut where the ship jumps: at home, then what happens in each system in turn.</summary>
    static readonly string[][] Legs = Cut(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nav", "first-discoveries.log")));

    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioExplore-").FullName;
    readonly PracticeGame game = new();
    readonly string journal;
    GameWatcher watcher;

    public ExploreTests()
    {
        journal = game.Journal("Journal.2026-10-09T100000.01.log", Legs[0]);
        watcher = game.Watch();
    }

    public void Dispose()
    {
        watcher.Dispose();
        game.Dispose();
        Directory.Delete(temp, recursive: true);
    }

    static string[][] Cut(string[] lines)
    {
        var legs = new List<List<string>> { new() };
        foreach (string line in lines)
        {
            if (line.Contains("\"event\":\"FSDJump\"")) legs.Add([]);
            legs[^1].Add(line);
        }
        return [.. legs.Select(leg => leg.ToArray())];
    }

    Settings SettingsFile() => new(Path.Combine(temp, "settings.json"));

    /// <summary>The game writes these lines now, and Studio reads them.</summary>
    void Play(params string[] lines)
    {
        game.Append(journal, lines);
        watcher.Poll();
    }

    /// <summary>Studio is closed and started again: a new watcher catches up with the journal, and a new trip reads what was saved.</summary>
    ExploreTrip Restart()
    {
        watcher.Dispose();
        watcher = game.Watch();
        var trip = new ExploreTrip(watcher.State, SettingsFile());
        watcher.Poll();
        return trip;
    }

    /// <summary>Systems visited, new systems, bodies first discovered, bodies first mapped.</summary>
    static (int, int, int, int) Counts(ExploreTrip trip) => (trip.View.Visited, trip.View.NewSystems, trip.View.FirstDiscoveries, trip.View.FirstMapped);

    /// <summary>The system the commander is in: its name, whether it is new, bodies scanned, bodies it has.</summary>
    static (string?, bool?, int, int) Here(ExploreTrip trip) => (trip.View.System, trip.View.IsNew, trip.View.Scanned, trip.View.BodyCount);

    static DateTime Time(string clock) => DateTime.Parse($"2026-10-09T{clock}Z").ToUniversalTime();

    // ------------------------------------------------------------ the count

    [Fact]
    public void A_first_is_counted_once_however_often_the_game_writes_about_it()
    {
        var trip = new ExploreTrip(watcher.State, SettingsFile());
        int told = 0;
        trip.Changed += () => told++;

        // Docked at home: the system the trip begins in is the first one visited.
        watcher.Poll();
        Assert.Equal((1, 0, 0, 0), Counts(trip));
        Assert.Equal((Home, null, 0, 0), Here(trip));
        Assert.Equal((Time("10:00:10"), false, null), (trip.View.SinceUtc, trip.View.HandedIn, trip.View.Heading));
        Assert.Equal(new double[] { 0, 0, 0 }, trip.View.Position);

        // A system someone discovered before: its star and one of its three bodies scanned, nothing new.
        Play(Legs[1]);
        Assert.Equal((2, 0, 0, 0), Counts(trip));
        Assert.Equal((Known, false, 2, 3), Here(trip));

        // A new one. Both stars and three planets are first discoveries; the belt cluster is no body.
        // One planet is mapped, and the scan the game writes after mapping does not count it again.
        Play(Legs[2]);
        Assert.Equal((3, 1, 5, 1), Counts(trip));
        Assert.Equal((New, true, 5, 9), Here(trip));

        // The star is on record but the planet is not: a first discovery in a system that is not new. No discovery scan here.
        Play(Legs[3]);
        Assert.Equal((4, 1, 6, 1), Counts(trip));
        Assert.Equal((HalfKnown, false, 2, 0), Here(trip));

        // Back in the new system. Its star is scanned on arrival again and still says "not discovered":
        // neither it nor the system counts twice. Mapping a planet found on the first visit does count.
        Play(Legs[4]);
        Assert.Equal((5, 1, 6, 2), Counts(trip));
        Assert.Equal((New, true, 5, 9), Here(trip));

        Assert.Equal(5, told);   // once for each reading of the journal, not once a line
        // 30 ly east and 40 ly north with every jump out: the heading is taken over them all.
        Assert.Equal(37, trip.View.Heading);
        Assert.Equal(new double[] { 60, 10, 80 }, trip.View.Position);
    }

    [Fact]
    public void A_restart_counts_nothing_twice_and_takes_in_what_the_game_wrote_while_Studio_was_closed()
    {
        var trip = new ExploreTrip(watcher.State, SettingsFile());
        watcher.Poll();
        Play(Legs[1]);
        Play(Legs[2]);
        Assert.Equal((3, 1, 5, 1), Counts(trip));

        // Studio is closed; the game goes on writing. The next start reads the whole journal again:
        // what was counted before is not counted again, what is new is.
        game.Append(journal, Legs[3]);
        trip = Restart();
        Assert.Equal((4, 1, 6, 1), Counts(trip));
        Assert.Equal((HalfKnown, false, 2, 0), Here(trip));
        Assert.Equal(Time("10:00:10"), trip.View.SinceUtc);

        // And what the game writes from then on is taken in as it comes.
        Play(Legs[4]);
        Assert.Equal((5, 1, 6, 2), Counts(trip));

        // A start with nothing new in the journal: the same picture, the heading included.
        trip = Restart();
        Assert.Equal((5, 1, 6, 2), Counts(trip));
        Assert.Equal((New, true, 5, 9), Here(trip));
        Assert.Equal(37, trip.View.Heading);
    }

    [Fact]
    public void Selling_the_data_ends_the_trip_and_the_next_jump_begins_a_new_one()
    {
        var trip = new ExploreTrip(watcher.State, SettingsFile());
        watcher.Poll();
        Play([.. Legs[1], .. Legs[2]]);

        // Sold, a page at a time (the layout of these two lines is the journal manual's: no recording holds a sale).
        Play("""{ "timestamp":"2026-10-09T11:00:00Z", "event":"Docked", "StationName":"Far Outpost", "StationType":"Outpost", "StarSystem":"Practice Sector LX-T d3-7", "SystemAddress":3003, "MarketID":3200000002 }""",
            """{ "timestamp":"2026-10-09T11:01:00Z", "event":"MultiSellExplorationData", "Discovered":[ { "SystemName":"Practice Sector LX-T d3-7", "NumBodies":5 } ], "BaseValue":1202000, "Bonus":601000, "TotalEarnings":1803000 }""",
            """{ "timestamp":"2026-10-09T11:01:05Z", "event":"MultiSellExplorationData", "Discovered":[ { "SystemName":"Practice Sector KX-T c3-0", "NumBodies":2 } ], "BaseValue":2500, "Bonus":0, "TotalEarnings":2500 }""");
        Assert.True(trip.View.HandedIn);
        Assert.Equal((3, 1, 5, 1), Counts(trip));   // still on show, for a last look

        // Leaving the station changes nothing yet. The next jump is the first of a new trip, which begins with that jump's line.
        Play("""{ "timestamp":"2026-10-09T11:05:00Z", "event":"Undocked", "StationName":"Far Outpost", "StationType":"Outpost", "MarketID":3200000002 }""");
        Assert.Equal((true, 5), (trip.View.HandedIn, trip.View.FirstDiscoveries));
        Play(Legs[3]);
        Assert.False(trip.View.HandedIn);
        Assert.Equal((1, 0, 1, 0), Counts(trip));
        Assert.Equal(Time("10:20:00"), trip.View.SinceUtc);
    }

    [Fact]
    public void A_ship_destroyed_and_bought_back_takes_its_unsold_finds_with_it()
    {
        var trip = new ExploreTrip(watcher.State, SettingsFile());
        watcher.Poll();
        Play([.. Legs[1], .. Legs[2]]);
        Assert.Equal((3, 1, 5, 1), Counts(trip));

        // Dying alone changes nothing: on foot, the ship and its data are safe. The rebuy is what tells of a lost ship.
        // (Both lines as the game wrote them in the recording of 30 September, where the owner's ship was destroyed twice.)
        Play("""{ "timestamp":"2026-10-09T10:16:00Z", "event":"Died" }""");
        Assert.Equal((3, 1, 5, 1), Counts(trip));
        Play("""{ "timestamp":"2026-10-09T10:17:00Z", "event":"Resurrect", "Option":"rebuy", "Cost":1888101, "Bankrupt":false }""",
            Legs[0][1].Replace("10:00:10", "10:17:04"));   // and the game puts the commander back at the station the trip began at
        Assert.Equal((1, 0, 0, 0), Counts(trip));
        Assert.Equal((Home, null, 0, 0), Here(trip));
        Assert.Equal(Time("10:17:00"), trip.View.SinceUtc);
    }

    [Fact]
    public void Reset_starts_a_new_trip_where_the_commander_is_and_a_restart_does_not_bring_the_old_one_back()
    {
        var trip = new ExploreTrip(watcher.State, SettingsFile(), () => Time("10:10:00"));
        int told = 0;
        trip.Changed += () => told++;
        watcher.Poll();
        Play([.. Legs[1], .. Legs[2][..^2]]);   // in the new system, up to ten past ten: the planet is not mapped yet
        Assert.Equal((3, 1, 5, 0), Counts(trip));

        trip.Reset();
        Assert.Equal(3, told);
        Assert.Equal((1, 0, 0, 0), Counts(trip));    // the system the commander is in is the new trip's first
        Assert.Equal((New, null, 0, 0), Here(trip)); // and what was scanned in it belongs to the trip put aside
        Assert.Equal(Time("10:10:00"), trip.View.SinceUtc);

        // A restart reads the journal again: what the game wrote before the reset stays put aside.
        trip = Restart();
        Assert.Equal((1, 0, 0, 0), Counts(trip));
        // The planet is mapped now, and the game writes its scan once more: both count for the new trip,
        // which knows nothing of the scan counted before.
        Play(Legs[2][^2..]);
        Assert.Equal((1, 0, 1, 1), Counts(trip));
    }

    [Theory]
    // recording, systems visited, the system it ends in, whether its star was already on record (null: not scanned), bodies scanned, bodies by its discovery scan
    [InlineData("Journal.2025-04-17T013240.01.log", 2, "Owaha", false, 3, 9)]                    // its star and two bodies, scanned on the way to a station
    [InlineData("Journal.2026-09-24T191106.01.log", 10, "Crucis Sector AQ-Y c25", null, 0, 0)]   // ten systems in a row; docked in the last without a scan
    [InlineData("Journal.2026-09-30T231247.01.log", 1, "Anyanwu", null, 0, 0)]                   // the ship was destroyed and bought back twice: the trip starts over where it comes back
    [InlineData("Journal.2026-10-08T080236.01.log", 1, "HIP 12099", null, 2, 26)]                // two bodies mapped that others had mapped before
    public void The_recordings_of_real_play_count_their_jumps_and_scans_and_not_one_first(string recording, int visited, string endsIn, bool? isNew, int scanned, int bodies)
    {
        using var played = new PracticeGame();
        played.Journal(recording);
        using GameWatcher replay = played.Watch();
        var trip = new ExploreTrip(replay.State, SettingsFile());

        replay.Poll();

        Assert.Equal((visited, 0, 0, 0), Counts(trip));
        Assert.Equal((endsIn, isNew, scanned, bodies), Here(trip));
        Assert.Null(trip.OverlayLine("#3D8BFF"));   // nothing to tell
    }

    [Fact]
    public void A_navigation_beacon_hands_over_a_systems_bodies_and_its_belt_clusters_are_not_among_them()
    {
        // The recording of 30 September up to the moment the ship is destroyed: at Anyanwu's beacon the game
        // wrote 20 scans in one go, 3 of them belt clusters. All of it was on record: nothing new.
        using var played = new PracticeGame();
        played.Journal("Journal.2026-09-30T231247.01.log", PracticeGame.Recording("Journal.2026-09-30T231247.01.log").TakeWhile(line => !line.Contains("\"event\":\"Died\"")));
        using GameWatcher replay = played.Watch();
        var trip = new ExploreTrip(replay.State, SettingsFile());

        replay.Poll();

        Assert.Equal((2, 0, 0, 0), Counts(trip));
        Assert.Equal(("Anyanwu", false, 17, 0), Here(trip));
    }

    [Fact]
    public void A_saved_trip_that_cannot_be_used_is_put_aside_without_stopping_the_start()
    {
        File.WriteAllText(Path.Combine(temp, "settings.json"), """{ "explore": { "visited": 7, "systems": { "3003": null }, "trail": [ [ 1, 2 ] ] } }""");

        var trip = new ExploreTrip(watcher.State, SettingsFile());
        watcher.Poll();

        Assert.Equal((1, 0, 0, 0), Counts(trip));
    }

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
