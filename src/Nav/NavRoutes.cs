using System.Text.Json;
using System.Text.Json.Serialization;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Nav;

/// <summary>One stop of a trip another studio hands over: a star system, and what to do there.</summary>
/// <param name="Address">The game's own number for the system, as text like <see cref="RouteHop.Address"/>; 0 when not known.</param>
/// <param name="X">Where the system is, in light years (with <paramref name="Y"/> and <paramref name="Z"/>).</param>
/// <param name="Note">What to do there, in words: "Dock at Jameson Memorial and buy the 5A Frame Shift Drive".</param>
public sealed record TripStop(
    string System,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Address,
    double X, double Y, double Z, string Note);

/// <summary>
/// Where the player is on the current route. The game cannot be told to plot a route, so
/// <paramref name="Next"/> is what the player pastes into the galaxy map.
/// </summary>
/// <param name="At">The stop reached last, counted from 0 for the route's start; -1 before the player has been at any stop.</param>
/// <param name="Here">The stop the ship is in right now, counted the same way; -1 when it is in none of them.</param>
/// <param name="Next">The system to fly to now; null on arrival at the last stop.</param>
/// <param name="NextLy">Light years to it in a straight line from where the ship is.</param>
/// <param name="NextJumps">Jumps to it: the game's own count when <paramref name="NextPlotted"/>, otherwise an estimate; 0 when not known.</param>
/// <param name="NextPlotted">The route plotted in the game's galaxy map ends at <paramref name="Next"/>.</param>
/// <param name="HopsLeft">Stops still to reach, <paramref name="Next"/> included.</param>
/// <param name="JumpsLeft">Jumps to the end of the route.</param>
/// <param name="LyLeft">Light years to the end of the route, along it.</param>
/// <param name="NextNote">What to do at <paramref name="Next"/> (<see cref="RouteHop.Note"/>); "" for nothing.</param>
/// <param name="HereNote">What to do at the stop the ship is in right now; "" for nothing, or when it is in none.</param>
public sealed record NavProgress(
    int At, int Here, string? Next, double NextLy, int NextJumps, bool NextPlotted, int HopsLeft, int JumpsLeft, double LyLeft,
    string NextNote = "", string HereNote = "");

/// <summary>A request Spansh is working on right now.</summary>
/// <param name="State">Spansh's word for the job: "queued" while it waits its turn, then "started".</param>
public sealed record NavPlotting(PlotRequest Request, DateTime StartedUtc, string State);

/// <summary>Everything Studio Nav shows: the answer to GET /api/nav/route and the data of every "nav" event.</summary>
/// <param name="Problem">Why the last request gave no route, in the player's words; null when it gave one.</param>
public sealed record NavView(NavRoute? Route, NavProgress? Progress, NavPlotting? Plotting, string? Problem);

/// <summary>The "nav" section of settings.json: the current route, so it survives a restart.</summary>
public sealed class NavSettings
{
    public NavRoute? Route { get; set; }
    public int Reached { get; set; } = -1;
}

/// <summary>
/// The one route the player is following, and how far along it they are. Lives on the UI thread
/// like the game state it reads. The progress is worked out again whenever the game state changes;
/// <see cref="Changed"/> is raised only when something Studio Nav shows is different afterwards.
/// </summary>
public sealed class NavRoutes
{
    readonly GameState game;
    readonly Settings settings;
    readonly Spansh spansh;
    int reached = -1;                    // NavProgress.At: remembered, because between two stops the current system says nothing
    CancellationTokenSource? running;    // the request under way, if any

    public NavRoutes(GameState game, Settings settings, Spansh spansh)
    {
        (this.game, this.settings, this.spansh) = (game, settings, spansh);
        // The saved route is a file on disk: it may be from an older Studio or changed by hand, and
        // whatever is in it must not stop Studio from starting. So any problem at all is caught here.
        try
        {
            NavSettings saved = settings.Get<NavSettings>("nav");
            if (saved.Route is { } route)
            {
                if (!Whole(route)) throw new JsonException("The saved route has parts missing.");
                Route = route;
                reached = Math.Clamp(saved.Reached, -1, route.Hops.Count - 1);
                Locate();
            }
        }
        catch (Exception problem)
        {
            Log.Error("The saved route could not be read, so Studio Nav starts without one", problem);
            (Route, Progress, reached) = (null, null, -1);
        }
        game.Changed += Follow;
    }

    /// <summary>
    /// Has everything the rest of Studio Nav and its page count on. A saved route can be readable
    /// and still lack parts: a request, a job id, a stop or a stop's name the file leaves out arrives as null.
    /// </summary>
    static bool Whole(NavRoute route) =>
        route is { Job: not null, Request.Kind: PlotRequest.Neutron or PlotRequest.Exact or PlotRequest.Trip, Hops.Count: > 0 }
        && route.Hops.All(hop => hop is { System.Length: > 0, Note: not null });

    /// <summary>Raised after the route, the progress along it, or the request under way changed.</summary>
    public event Action? Changed;

    public NavRoute? Route { get; private set; }

    /// <summary>Null without a route.</summary>
    public NavProgress? Progress { get; private set; }

    public NavPlotting? Plotting { get; private set; }

    public string? Problem { get; private set; }

    public NavView View => new(Route, Progress, Plotting, Problem);

    /// <summary>
    /// Asks Spansh for a route. When it comes, it replaces the current route; when it does not,
    /// the current route stays and <see cref="Problem"/> says why. Only one request runs at a time:
    /// a new one takes the place of one still under way. Ends when the request has.
    /// </summary>
    public async Task Plot(PlotRequest request)
    {
        // A neutron star's charge is spent by the ship's next jump, so it stretches the first jump
        // only of a route that starts where the ship is. For any other start it is left out: by
        // the time the ship is there the charge is gone, and Spansh would have planned on it.
        if (request.Ship is { IsSupercharged: true } charged && !request.From.Equals(game.StarSystem, StringComparison.OrdinalIgnoreCase))
            request = request with { Ship = charged with { IsSupercharged = false } };

        // The new request is in place before the old one is stopped, so the old one, on finding
        // itself cancelled, sees it has been replaced and leaves everything alone.
        CancellationTokenSource? replaced = running;
        var mine = running = new CancellationTokenSource();
        replaced?.Cancel();
        Problem = null;
        Plotting = new NavPlotting(request, DateTime.UtcNow, "queued");
        Changed?.Invoke();
        try
        {
            NavRoute route = await spansh.Plot(request, state =>
            {
                if (running != mine || Plotting is not { } now || now.State == state) return;
                Plotting = now with { State = state };
                Changed?.Invoke();
            }, mine.Token);
            if (running != mine) return;
            Route = route;
            reached = -1;
            Locate();
            Save();
        }
        catch (OperationCanceledException)
        {
            // stopped by Cancel or by a newer request: nothing to report
        }
        catch (SpanshException problem)
        {
            if (running == mine) Problem = problem.Message;
        }
        catch (Exception problem)
        {
            // Nobody awaits this in the app, so anything unexpected ends here, not as a request that never finishes.
            Log.Error("Plotting a route failed", problem);
            if (running == mine) Problem = "Something went wrong while plotting this route. The log has the details.";
        }
        finally
        {
            mine.Dispose();
        }
        if (running != mine) return;
        running = null;
        Plotting = null;
        Changed?.Invoke();
    }

    /// <summary>Stops the request under way, if any. The current route stays.</summary>
    public void Cancel()
    {
        if (running is not { } stopped) return;
        running = null;
        Plotting = null;
        stopped.Cancel();
        Changed?.Invoke();
    }

    /// <summary>
    /// Takes stops another studio hands over as the route to follow, in place of the current one:
    /// a shopping trip from Studio Loadouts, say, each stop a system with a note on what to buy
    /// there. It is followed, copied, shown on the overlay and kept across a restart like a route
    /// from Spansh; the game plots each leg. The route starts where the ship is. Stops one after
    /// the other in one system are one stop with both notes. A request Spansh is still working on
    /// is dropped, or its route would take this one's place when it came.
    /// </summary>
    /// <param name="name">What the trip is for, in words.</param>
    /// <param name="stops">In flying order; at least one.</param>
    public void Follow(string name, IReadOnlyList<TripStop> stops)
    {
        var hops = new List<RouteHop>();
        if (game is { StarSystem: { Length: > 0 } system, StarPos: { } here })
            hops.Add(new RouteHop(system, game.SystemAddress, here[0], here[1], here[2], 0, 0, 0, false, false, 0));
        foreach (TripStop stop in stops)
        {
            if (hops is [.., { } last] && Is(last, stop.System, stop.Address))
            {
                hops[^1] = last with { Note = string.Join(" ", new[] { last.Note, stop.Note }.Where(note => note.Length > 0)) };
                continue;
            }
            double ly = hops is [.., { } before] ? ShipMath.Distance([before.X, before.Y, before.Z], [stop.X, stop.Y, stop.Z]) : 0;
            hops.Add(new RouteHop(stop.System, stop.Address, stop.X, stop.Y, stop.Z, ly, 0, ShipMath.JumpsFor(ly, game.JumpLaden), false, false, 0, stop.Note));
        }
        RouteHop end = hops[^1];
        for (int i = 0; i < hops.Count; i++)
            hops[i] = hops[i] with { DistanceLeft = ShipMath.Distance([hops[i].X, hops[i].Y, hops[i].Z], [end.X, end.Y, end.Z]) };

        CancellationTokenSource? stopped = running;
        (running, Plotting, Problem) = (null, null, null);
        stopped?.Cancel();
        Route = new NavRoute(new PlotRequest { Kind = PlotRequest.Trip, From = hops[0].System, To = end.System, Name = name }, "", DateTime.UtcNow, hops);
        reached = -1;
        Locate();
        Save();
        Changed?.Invoke();
    }

    /// <summary>Forgets the current route.</summary>
    public void Clear()
    {
        if (Route is null && Problem is null) return;
        (Route, Progress, Problem, reached) = (null, null, null, -1);
        Save();
        Changed?.Invoke();
    }

    void Follow()
    {
        if (Route is null) return;
        (int was, NavProgress? before) = (reached, Progress);
        Locate();
        if (reached != was) Save();
        if (Progress != before) Changed?.Invoke();
    }

    /// <summary>Works out <see cref="Progress"/> from where the game says the player is now.</summary>
    void Locate()
    {
        IReadOnlyList<RouteHop> hops = Route!.Hops;
        // Being in a stop's system is reaching it. While the ship is still in the stop reached last,
        // that one stands: a route may pass one system twice, and the ship is on the visit it had
        // got to. Otherwise stops ahead are looked at first, so skipping some is fine; a stop behind
        // counts as well, for a player who flew back. In any other system (between two stops of a
        // neutron route, or off the route) the last stop stands.
        int at = reached >= 0 && Is(hops[reached], game.StarSystem, game.SystemAddress) ? reached : Find(hops, reached + 1);
        if (at < 0) at = Find(hops, 0);
        if (at >= 0) reached = at;

        int next = reached + 1;
        string hereNote = at >= 0 ? hops[at].Note : "";
        if (next >= hops.Count)
        {
            Progress = new NavProgress(reached, at, null, 0, 0, false, 0, 0, 0, "", hereNote);
            return;
        }
        RouteHop to = hops[next];
        // Measured from the ship, not from the last stop, so it stays true between stops and off the route.
        double ly = game.StarPos is { } here ? ShipMath.Distance(here, [to.X, to.Y, to.Z]) : to.Distance;
        // The game's own count is exact once the player has plotted to the stop in the galaxy map,
        // which shows in the plotted route ending at it. Before that: Spansh's number for the leg
        // when the ship is at the leg's start, else Studio's usual estimate.
        int plotted = game.Route is [.., { } end] && Is(to, end.StarSystem, end.SystemAddress) ? game.PlottedJumps(end.StarSystem) : 0;
        int jumps = plotted > 0 ? plotted
            : reached >= 0 && Is(hops[reached], game.StarSystem, game.SystemAddress) ? to.Jumps
            : ShipMath.JumpsFor(ly, game.JumpLaden);
        int jumpsAfter = 0;
        double lyAfter = 0;
        for (int i = next + 1; i < hops.Count; i++)
        {
            jumpsAfter += hops[i].Jumps;
            lyAfter += hops[i].Distance;
        }
        Progress = new NavProgress(
            reached, at, to.System, Math.Round(ly, 1), jumps, plotted > 0, hops.Count - next, jumps + jumpsAfter, Math.Round(ly + lyAfter, 1), to.Note, hereNote);
    }

    void Save()
    {
        try
        {
            settings.Set("nav", new NavSettings { Route = Route, Reached = reached });
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error("The current route could not be saved, so it will not survive a restart", problem);
        }
    }

    /// <summary>The place in the list, looking from <paramref name="from"/> on, of the stop the ship is at; -1 when it is at none of them.</summary>
    int Find(IReadOnlyList<RouteHop> hops, int from)
    {
        for (int i = from; i < hops.Count; i++)
            if (Is(hops[i], game.StarSystem, game.SystemAddress)) return i;
        return -1;
    }

    /// <summary>
    /// The stop is that system. Settled by the game's own number for the system when Spansh gave
    /// one for the stop and the game gave one for the system: that holds even where the two spell
    /// a name differently. Otherwise by name, without regard to capitals as everywhere in Studio
    /// (a route saved by an earlier Studio has no numbers).
    /// </summary>
    static bool Is(RouteHop stop, string? system, long address) =>
        stop.Address != 0 && address != 0 ? stop.Address == address : stop.System.Equals(system, StringComparison.OrdinalIgnoreCase);
}
