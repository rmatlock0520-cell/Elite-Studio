using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Nav;

/// <summary>
/// Studio Nav's routes, all under /api/nav/, and its "nav" event.
///   GET  systems?q=   system names starting with q, for the name boxes: {"systems":[...]}
///   POST plot         {"kind":"neutron"|"exact","from":"","to":"","range":0,"efficiency":60} starts a request
///   POST cancel       stops the request under way
///   GET  route        the current route, the progress along it and the request under way (<see cref="NavView"/>)
///   POST clear        forgets the current route
///   POST trip         {"name":"Shopping for Explorer","stops":[{"system":"","address":"0","x":0,"y":0,"z":0,"note":""}]}:
///                     stops another studio's page hands over become the route, each with its note on what to do there
/// The four POSTs answer with the same <see cref="NavView"/>, and so does every "nav" event,
/// which is sent whenever any of it changes: that is how a page hears that Spansh has answered.
/// </summary>
public static class NavApi
{
    /// <summary>What the page sends to plot. The ship's numbers are not the page's to send: they come from the game.</summary>
    sealed class PlotBody
    {
        public string Kind { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public double Range { get; set; }
        public int Efficiency { get; set; } = 60;
    }

    /// <summary>What a page sends to hand a trip over.</summary>
    sealed class TripBody
    {
        public string Name { get; set; } = "";
        public List<TripStop?>? Stops { get; set; }
    }

    /// <summary>Registers the routes. Call on the UI thread, which is also where the server runs them.</summary>
    /// <param name="spansh">The app's one client for Spansh, so what it remembers lasts the session; tests pass one that never touches the internet.</param>
    /// <returns>The route being followed, for whoever else shows the next waypoint (the in-game overlay).</returns>
    public static NavRoutes Map(WebServer web, GameState game, Settings settings, Spansh spansh)
    {
        var routes = new NavRoutes(game, settings, spansh);
        routes.Changed += () => web.Send("nav", routes.View);
        string namesProblem = "";

        web.Map("GET", "/api/nav/systems", async request =>
        {
            try
            {
                return WebResponse.Json(new { systems = await spansh.SystemNames(request.Query.GetValueOrDefault("q") ?? "") });
            }
            catch (SpanshException problem)
            {
                // A name box without suggestions still works, and Plot will say what is wrong. The
                // box asks again with every few letters typed, so the same problem is logged once.
                if (problem.Message != namesProblem) Log.Error("System names could not be fetched", problem);
                namesProblem = problem.Message;
                return WebResponse.Json(new { systems = Array.Empty<string>() });
            }
        });

        web.Map("GET", "/api/nav/route", _ => WebResponse.Json(routes.View));

        web.Map("POST", "/api/nav/plot", request =>
        {
            if (ToPlotRequest(request.Read<PlotBody>(), game, out string problem) is not { } plot) return WebResponse.Error(problem, 400);
            // Not awaited: Spansh may take a minute, and the page hears the outcome through "nav" events.
            _ = routes.Plot(plot);
            return WebResponse.Json(routes.View);
        });

        web.Map("POST", "/api/nav/cancel", _ =>
        {
            routes.Cancel();
            return WebResponse.Json(routes.View);
        });

        web.Map("POST", "/api/nav/clear", _ =>
        {
            routes.Clear();
            return WebResponse.Json(routes.View);
        });

        web.Map("POST", "/api/nav/trip", request =>
        {
            TripBody body = request.Read<TripBody>();
            // (What a page leaves out of a stop arrives as null.)
            TripStop[] stops =
            [
                .. (body.Stops ?? []).OfType<TripStop>().Where(stop => !string.IsNullOrWhiteSpace(stop.System))
                    .Select(stop => stop with { System = stop.System.Trim(), Note = stop.Note?.Trim() ?? "" }),
            ];
            if (stops.Length == 0) return WebResponse.Error("This trip has no stops to fly to.", 400);
            routes.Follow((body.Name ?? "").Trim(), stops);
            return WebResponse.Json(routes.View);
        });

        return routes;
    }

    /// <summary>
    /// Turns what the page sent into a request for Spansh, adding what only the game knows about
    /// the ship. Null, with the reason in the player's words, when something is missing.
    /// </summary>
    static PlotRequest? ToPlotRequest(PlotBody body, GameState game, out string problem)
    {
        (string from, string to) = (body.From.Trim(), body.To.Trim());
        ShipNumbers? ship = ShipNumbers.Of(game, out string missing);
        problem = from.Length == 0 ? "Type the system the route starts in."
            : to.Length == 0 ? "Type the system you want to go to."
            : body.Kind == PlotRequest.Neutron && !(body.Range > 0) ? "Type your ship's jump range in light years."
            : body.Kind == PlotRequest.Exact && ship is null ? $"The exact plotter works from your own ship, and Studio does not have {missing}."
            : body.Kind is PlotRequest.Neutron or PlotRequest.Exact ? ""
            : "Unknown kind of route.";
        if (problem.Length > 0) return null;

        return body.Kind == PlotRequest.Exact
            ? new PlotRequest { Kind = PlotRequest.Exact, From = from, To = to, Ship = ship, Cargo = game.CargoCount }
            : new PlotRequest
            {
                Kind = PlotRequest.Neutron,
                From = from,
                To = to,
                Range = Math.Round(body.Range, 2),
                Efficiency = body.Efficiency < 1 ? 60 : Math.Min(body.Efficiency, 100),   // a box left empty means Spansh's usual 60
                Supercharge = ship?.SuperchargeMultiplier ?? 4,
            };
    }
}
