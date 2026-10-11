using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Nav;

/// <summary>Where to head for: the answer to POST /api/nav/explore/waypoint.</summary>
/// <param name="Bearing">The direction looked along, in degrees as <see cref="ExploreTrip.Heading"/>: the one the page sent, or the commander's own.</param>
/// <param name="Square">The first thinly known square that way; null when there is none before the map ends.</param>
/// <param name="System">The system Spansh knows nearest to the middle of that square's known systems: a name for the galaxy map and the route plotter. Null with <paramref name="Problem"/>, and when the commander is in the square already.</param>
/// <param name="Problem">Why there is no name, in the player's words.</param>
public sealed record ExploreWaypoint(double Bearing, ThinSquare? Square, NearSystem? System, string? Problem);

/// <summary>
/// The unexplored finder's routes, all under /api/nav/explore/, and its "explore" event.
///   GET  trip        the live count of this trip's firsts (<see cref="ExploreView"/>)
///   POST reset       starts a new trip now; answers with the same
///   GET  coverage    the map of known systems kept on this PC: {"map":<see cref="SectorMap"/> or null,"stale":true when EDAstro may be asked (again),"problem":null}
///   POST coverage    asks EDAstro for the list when "stale"; the same answer, with "problem" when it could not be fetched
///   POST waypoint    {"bearing":degrees, or null for the way the commander is travelling} gives an <see cref="ExploreWaypoint"/>
/// Every "explore" event carries the <see cref="ExploreView"/>, and is sent whenever the game writes something that changes it.
/// </summary>
public static class ExploreApi
{
    /// <summary>What the page sends for a waypoint. Where the commander is comes from the game.</summary>
    sealed class WaypointBody
    {
        public double? Bearing { get; set; }
    }

    /// <summary>Registers the routes. Call on the UI thread, which is also where the server runs them.</summary>
    /// <param name="spansh">The app's one client for Spansh, the same one the route plotter uses.</param>
    /// <returns>The trip being counted, for whoever else shows it (the in-game overlay).</returns>
    public static ExploreTrip Map(WebServer web, GameState game, Settings settings, Spansh spansh, ExploreCoverage coverage)
    {
        var trip = new ExploreTrip(game, settings);
        trip.Changed += () => web.Send("explore", trip.View);

        web.Map("GET", "/api/nav/explore/trip", _ => WebResponse.Json(trip.View));

        web.Map("POST", "/api/nav/explore/reset", _ =>
        {
            trip.Reset();
            return WebResponse.Json(trip.View);
        });

        web.Map("GET", "/api/nav/explore/coverage", _ => WebResponse.Json(new { map = coverage.Map, stale = coverage.Stale, problem = (string?)null }));

        web.Map("POST", "/api/nav/explore/coverage", async _ =>
        {
            string? problem = await coverage.Refresh();
            if (problem is not null) Log.Error("EDAstro's sector list could not be fetched: " + problem);
            return WebResponse.Json(new { map = coverage.Map, stale = coverage.Stale, problem });
        });

        web.Map("POST", "/api/nav/explore/waypoint", async request =>
        {
            double? picked = request.Read<WaypointBody>().Bearing;
            if (coverage.Map is not { } map) return WebResponse.Error("Get the map first: without it Studio cannot tell where few have been.", 400);
            if (game.StarPos is not { } here) return WebResponse.Error("Studio does not know where you are yet. Start Elite, and it will follow you.", 400);
            if ((picked ?? trip.Heading) is not { } bearing || !double.IsFinite(bearing))
                return WebResponse.Error("Studio cannot tell yet which way you are travelling. Make a few jumps, or click the map to pick a direction.", 400);

            ThinSquare? square = map.Ahead(here[0], here[2], bearing);
            if (square is null or { Here: true }) return WebResponse.Json(new ExploreWaypoint(bearing, square, null, null));
            try
            {
                NearSystem? system = await spansh.Nearest(square.X, square.Y, square.Z);
                return WebResponse.Json(new ExploreWaypoint(bearing, square, system,
                    system is null ? "Spansh knows no system near the middle of that square, so Studio has no name to give you." : null));
            }
            catch (SpanshException unanswered)
            {
                Log.Error("Spansh could not be asked for a system near the waypoint", unanswered);
                return WebResponse.Json(new ExploreWaypoint(bearing, square, null, unanswered.Message));
            }
        });

        return trip;
    }
}
