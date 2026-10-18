using System.Globalization;
using System.Text.Json.Serialization;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Nav;

/// <summary>One stop of the plotted route in its check (<see cref="RouteCheckView"/>).</summary>
/// <param name="Name">The name as the game gave it (NavRoute.json).</param>
/// <param name="Id64">
/// The game's own number for the system; 0 when the game gave none. Written as text, because these numbers can be
/// larger than a web page can hold exactly.
/// </param>
/// <param name="X">Where the game says it is, in light years (with <paramref name="Y"/> and <paramref name="Z"/>); null when it gave no place.</param>
/// <param name="State">
/// <see cref="HeatmapFinds.Waiting"/>, <see cref="HeatmapFinds.Checking"/>, <see cref="HeatmapFinds.Recorded"/>,
/// <see cref="HeatmapFinds.NotRecorded"/> or <see cref="HeatmapFinds.Failed"/> (it could not be checked).
/// </param>
/// <param name="How">How it was settled, in a few words ("in the detailed map (Spansh's list of 14 Oct 2026)", "found by Spansh's system search"); null while it is not, or when it could not be.</param>
/// <param name="Reason">Why it could not be checked, in the player's words ("request limit reached"); null otherwise.</param>
public sealed record RouteCheckStop(
    string Name,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Id64,
    double? X, double? Y, double? Z, string State, string? How, string? Reason);

/// <summary>
/// The line the in-game overlay shows of the route's check, after its "ROUTE CHECK" label (<see cref="HeatmapFinds.LineOf"/>).
/// The nav box in the game is Studio Nav's page, which prints it as it comes ("line" in GET route-check and in every
/// "route-check" event); the hauling overlay's own drawing of that box (--render-shots) prints the same words.
/// </summary>
/// <param name="Words">"checking 12 of 34…", "done · 34 stops · 1 not recorded: …", the parts apart by the overlay's wide dot.</param>
/// <param name="Tone">
/// <see cref="Dim"/> while it goes on, <see cref="Gold"/> once a stop is found not recorded (the colour of its dot on
/// the 3D map), <see cref="Green"/> when every stop is recorded, <see cref="Plain"/> otherwise.
/// </param>
public sealed record RouteCheckLine(string Words, string Tone)
{
    /// <summary>What <see cref="Tone"/> says.</summary>
    public const string Dim = "dim", Gold = "gold", Green = "green", Plain = "plain";
}

/// <summary>
/// The check of every stop of the plotted route as it stands (GET /api/nav/heatmap/route-check), and what the
/// overlay's line about it is made from.
/// </summary>
/// <param name="State">
/// <see cref="HeatmapFinds.Off"/> (the route's stops are not checked: "Check my targets" is off, or only the route's
/// end is checked), <see cref="HeatmapFinds.NoRoute"/> (on, with no check of a route: none is plotted, or the one
/// plotted was there at Studio's start, see <see cref="Plotted"/>), <see cref="HeatmapFinds.Waiting"/> (begun, waiting
/// its turn behind another request of the 3D map), <see cref="HeatmapFinds.Checking"/>, <see cref="HeatmapFinds.Done"/>
/// or <see cref="HeatmapFinds.Stopped"/> (ended before every stop could be asked about: see <paramref name="Reason"/>).
/// </param>
/// <param name="Mode"><see cref="HeatmapFinds.CheckEnds"/> or <see cref="HeatmapFinds.CheckAll"/>.</param>
/// <param name="Started">When the check began (UTC); null with none.</param>
/// <param name="Finished">When it ended (UTC); null while it goes on, and with none.</param>
/// <param name="Total">The stops of the route, its start (where the ship was) left out.</param>
/// <param name="Checked">Stops with an answer: recorded or not.</param>
/// <param name="Failed">Stops that could not be checked.</param>
/// <param name="Requests">Requests to Spansh the check has sent.</param>
/// <param name="Reason">Why the check stopped (state <see cref="HeatmapFinds.Stopped"/>), in the player's words; null otherwise.</param>
/// <param name="Stops">Every stop in route order, its start left out. Written last: it may run to hundreds.</param>
public sealed record RouteCheckView(
    string State, string Mode, DateTime? Started, DateTime? Finished,
    int Total, int Checked, int Recorded, int NotRecorded, int Failed, int Requests, string? Reason,
    [property: JsonPropertyOrder(1)] IReadOnlyList<RouteCheckStop> Stops)
{
    /// <summary>
    /// With no check to show (<see cref="HeatmapFinds.NoRoute"/>), the stops of the route plotted in the game all the
    /// same: one that was there at Studio's start, which is history and so not checked by itself until the player asks
    /// (plotting it again, or "Check this route" on the 3D map). 0 otherwise.
    /// </summary>
    public int Plotted { get; init; }

    /// <summary>The end of that route, by name; null without one.</summary>
    public string? PlottedTo { get; init; }

    /// <summary>The overlay's line of it; null when there is nothing to tell (switched off, or no route plotted).</summary>
    public RouteCheckLine? Line => HeatmapFinds.LineOf(this);
}

/// <summary>
/// News of the route's check, sent as a "heatmap" event with kind "route-check" on every change (it began, a stop or
/// a batch of stops was settled, it ended, it went): <see cref="RouteCheckView"/> without its stops, which a page asks
/// for with GET route-check, but with the overlay's line, made from them, so the overlay need ask nothing.
/// </summary>
public sealed record RouteCheckNews(
    string State, string Mode, DateTime? Started, DateTime? Finished,
    int Total, int Checked, int Recorded, int NotRecorded, int Failed, int Requests, string? Reason)
{
    /// <summary>Always "route-check": it tells these events from those of a survey, of the detailed map or of the game's target.</summary>
    [JsonPropertyOrder(-1)]
    public string Kind => "route-check";

    /// <summary>As <see cref="RouteCheckView.Plotted"/>.</summary>
    public int Plotted { get; init; }

    /// <summary>As <see cref="RouteCheckView.PlottedTo"/>.</summary>
    public string? PlottedTo { get; init; }

    /// <summary>As <see cref="RouteCheckView.Line"/>, the stops not recorded named in it.</summary>
    public RouteCheckLine? Line { get; init; }
}

// The check of every stop of a plotted route ("routeCheck": "all"). The owner, 15 October 2026: check every system
// on the plotted route, not only the target and the route's end, and make it plain when the check is complete, in
// the game's overlay too. Every stop goes the careful way a target goes, the many at once:
// - the 3D map's memory of checks first (CheckMemory: what any check found, the target's, Within reach's and Nav
//   scan's included, for as long as it keeps it), then what is kept on this PC: the surveys, the detailed map, and
//   Your finds;
// - the rest by their exact names at Spansh, at most Spansh.MostNames to a request, each request one of the 3D map's
//   own (its gate, its 200 a session), never sent again after a failure (the memory keeps a failure ten minutes); past
//   the session's requests the stops left are not asked about, and say why;
// - what came of each stop goes into the memory, written once for each batch;
// - a stop Spansh does not have, whose number the game gave, is kept as a find.
// A new route takes the place of the check of the one before, which stops after its request under way (a request
// still waiting its turn at the gate is never sent); a route cleared, or the switch turned off, takes its check with it.
//
// The route's check and Nav scan's check of a picture are both checks of many systems, and may run at once (F12 on the
// Navigation panel just after a route is plotted). Each takes the systems it will ask Spansh about as its own, and the
// other must not wait for those while it holds systems of its own: with the route's stops A, B, C and the panel's rows
// C, B, A, each could hold what the other waits for, and both would wait for ever. So a check of many puts another's
// systems aside, asks about its own, gives them up, and only then waits for the ones it put aside (AfterOthers). Every
// wait ends when the check goes (a new route, the switch, Nav scan switched off).
public sealed partial class HeatmapFinds
{
    /// <summary>A system a check took to ask Spansh about (<see cref="underWay"/>).</summary>
    /// <param name="Done">Ends when that check has its answer, or gives the system up.</param>
    /// <param name="Batch">
    /// Taken by a check of many (a route's, a picture's), which may itself wait for others' systems: never waited for
    /// by another check of many while that one holds systems of its own. False for a target's check, which waits for
    /// nothing of anybody's.
    /// </param>
    sealed record Claim(TaskCompletionSource Done, bool Batch);

    /// <summary>How a stop stands once it was looked at without asking Spansh (<see cref="Offline"/>).</summary>
    enum Settling
    {
        /// <summary>Settled: from the memory, this PC or Your finds, or it could not be checked a few minutes ago.</summary>
        Settled,

        /// <summary>Spansh is to be asked: the stop is the check's own until it is.</summary>
        Ask,

        /// <summary>Another check of many took it to ask about: waited for once this check holds none (<see cref="AfterOthers"/>).</summary>
        Aside,
    }

    /// <summary>What <see cref="RouteCheckView.State"/> says; a stop's state is <see cref="Waiting"/> or <see cref="Checking"/> too.</summary>
    public const string Off = "off", NoRoute = "idle", Waiting = "waiting", Checking = "checking", Done = "done", Stopped = "stopped";

    /// <summary>What <see cref="RouteCheckStop.State"/> says of a stop that could not be checked.</summary>
    public const string Failed = "failed";

    /// <summary>Why the stops left are not checked once the 3D map's requests for the session are used up; also why the check stopped.</summary>
    public const string LimitReached = "request limit reached";

    const string InYourFinds = "in your finds";
    const string FoundBySpansh = "found by Spansh's system search";
    const string NotFoundBySpansh = "not found by Spansh's system search";
    const string NotAgain = "it could not be checked a few minutes ago, and Studio waits ten minutes before it asks again";

    /// <summary>How a stop settled from the memory of checks says so: "checked earlier: found by Spansh's system search".</summary>
    static string Earlier(CheckEntry entry) => entry.How is { Length: > 0 } how ? "checked earlier: " + how : "checked earlier";
    const string CutShort = "Spansh found more systems under these names than it sent, so this one may be among those left out";
    const string NoNumber = "the game gave no number for it, which a find needs";

    /// <summary>Between the parts of the overlay's line: the overlay's own wide dot (Draw.Dot), which the pages write as Studio.wideDot.</summary>
    const string LineDot = "   ·   ";

    /// <summary>How often a page and the overlay are told while stops are settled one by one on this PC.</summary>
    static readonly TimeSpan TellEvery = TimeSpan.FromSeconds(0.5);

    string routeKey = "";                   // the plotted route as last seen, by its stops (RouteKey); "" for none. The game's thread.
    (int Stops, string? End) routeNow;      // that route as its check takes it (StopsOf): how many stops, and the last one's name. Under the guard.
    RouteRun? run;                          // the check of the route begun last; null for none. Under the guard.
    Task routeDone = Task.CompletedTask;    // that check, which the next one waits for

    /// <summary>
    /// Raised on every change of the route's check (<see cref="RouteCheckNews"/>): the page asks for the stops again, the
    /// overlay's page shows its line. Raised on the thread the change is made on: in the app, the UI thread.
    /// </summary>
    public event Action<RouteCheckNews>? RouteNews;

    /// <summary>The route's check as it stands, its stops with it.</summary>
    public RouteCheckView RouteCheckNow()
    {
        lock (guard) return ViewOf(run);
    }

    /// <summary>One stop in its check. Changed under the guard only.</summary>
    sealed class StopCheck(RouteStop stop)
    {
        public string Name { get; } = stop.StarSystem.Trim();
        public long Id64 { get; } = stop.SystemAddress;
        public double[]? At { get; } = stop.StarPos is [_, _, _] at ? at : null;
        public string State { get; set; } = Waiting;
        public string? How { get; set; }
        public string? Reason { get; set; }

        /// <summary>What is kept on this PC said of it, for its find.</summary>
        public Located? Kept { get; set; }

        /// <summary>
        /// This check took the system as its own to ask about (its entry among the checks under way is this check's,
        /// until it is answered): given back (<see cref="Release"/>) if the check ends without asking.
        /// </summary>
        public bool Claimed { get; set; }

        public bool Ended => State is Recorded or NotRecorded or Failed;
    }

    /// <summary>One check of a route. Changed under the guard only.</summary>
    sealed class RouteRun(string key, StopCheck[] stops)
    {
        readonly CancellationTokenSource leaving = new();

        public string Key { get; } = key;
        public StopCheck[] Stops { get; } = stops;
        public string State { get; set; } = Waiting;
        public DateTime Started { get; } = DateTime.UtcNow;
        public DateTime? Finished { get; set; }
        public int Requests { get; set; }
        public string? Reason { get; set; }

        /// <summary>
        /// A newer route, the route cleared or the switch: it stops after its request under way, and is nobody's news.
        /// Set under the guard, and <see cref="Leave"/> called after it.
        /// </summary>
        public bool Gone { get; set; }

        /// <summary>Cancelled when it has gone: a batch still waiting its turn at the 3D map's gate is then never sent.</summary>
        public CancellationToken Leaving => leaving.Token;

        /// <summary>Ends a wait at the gate. Called outside the guard: what waited may go on at once on this thread.</summary>
        public void Leave() => leaving.Cancel();

        /// <summary>When the last news of it went out.</summary>
        public DateTime Told { get; set; }
    }

    /// <summary>A plotted route by its stops after the start, which is where the ship was: "" for none.</summary>
    static string RouteKey(IReadOnlyList<RouteStop> route) =>
        string.Join("|", route.Skip(1).Select(stop => stop.SystemAddress > 0 ? stop.SystemAddress.ToString(CultureInfo.InvariantCulture) : stop.StarSystem.Trim().ToUpperInvariant()));

    /// <summary>The stops of a route its check takes: those after its start, each once (the game's routes have no stop twice; a file put together by hand might).</summary>
    static StopCheck[] StopsOf(IReadOnlyList<RouteStop> route) =>
    [
        .. route.Skip(1).Where(stop => stop.StarSystem.Trim().Length > 0)
            .DistinctBy(stop => stop.SystemAddress > 0 ? stop.SystemAddress.ToString(CultureInfo.InvariantCulture) : stop.StarSystem.Trim().ToUpperInvariant())
            .Select(stop => new StopCheck(stop)),
    ];

    /// <summary>
    /// Notes the route plotted now as the one last seen (on the game's thread), with what a page shows of it while it
    /// has no check. True when that changed, so whoever shows it should be told.
    /// </summary>
    bool NoteRoute(string key)
    {
        routeKey = key;
        StopCheck[] stops = key.Length == 0 ? [] : StopsOf(game.Route);
        (int, string?) now = (stops.Length, stops.Length > 0 ? stops[^1].Name : null);
        lock (guard)
        {
            if (routeNow == now) return false;
            routeNow = now;
            return true;
        }
    }

    /// <summary>The route's check follows this route (whatever came of it).</summary>
    bool Following(string key)
    {
        lock (guard) return run is { } check && check.Key == key;
    }

    /// <summary>
    /// The route plotted now is checked, unless it is being checked already: the player switched "Check my targets"
    /// on, chose every stop, or asked for the route that was there at Studio's start ("Check this route"). The page
    /// and the overlay are told either way.
    /// </summary>
    void CheckRouteNow()
    {
        string key = RouteKey(game.Route);
        NoteRoute(key);
        if (key.Length == 0 || Following(key) || !StartRoute()) Tell(null);
    }

    /// <summary>
    /// Begins the check of the route plotted now, when every stop is to be checked: false when not (nothing to check,
    /// or the switches say no). A check of the route before stops after its request under way, and this one waits for it.
    /// </summary>
    bool StartRoute()
    {
        lock (guard)
        {
            if (!checkTargets || routeMode != CheckAll) return false;
        }
        StopCheck[] stops = StopsOf(game.Route);
        if (stops.Length == 0)
        {
            StopRoute();
            return false;
        }
        var mine = new RouteRun(RouteKey(game.Route), stops);
        RouteRun? old;
        Task before;
        lock (guard)
        {
            old = run;
            if (old is not null) old.Gone = true;
            (run, before) = (mine, routeDone);
            // It waits only for the check of a route before that is still under way.
            mine.State = before.IsCompleted ? Checking : Waiting;
        }
        // A batch of the old route's still waiting its turn at the gate is not sent: the old check ends at once then.
        old?.Leave();
        Log.Info($"3D map: checking the {stops.Length} stops of the plotted route");
        Tell(mine);
        Task checking = RunRoute(mine, before);
        lock (guard)
        {
            routeDone = checking;
            idle = idle.IsCompleted ? checking : Task.WhenAll(idle, checking);
        }
        return true;
    }

    /// <summary>
    /// The route's check goes: a check under way stops after its request under way (a request still waiting its turn
    /// is never sent). Told when there was one (or when asked to).
    /// </summary>
    void StopRoute(bool tell = false)
    {
        RouteRun? old;
        lock (guard)
        {
            old = run;
            run = null;
            if (old is not null) old.Gone = true;
        }
        old?.Leave();
        if (old is not null || tell) Tell(null);
    }

    /// <summary>
    /// One check of a route, start to end: every stop on this PC first, then the rest at Spansh in batches, then the
    /// stops another check of many had taken (as that check settles them; one it gave up unasked is looked at again).
    /// Runs by itself, off the game's line or change that began it: whatever goes wrong is logged and said, never thrown.
    /// </summary>
    async Task RunRoute(RouteRun mine, Task before)
    {
        // The game's thread goes on at once; the check of the route before ends after its request under way (it never throws).
        await Task.Yield();
        await before;
        var asking = new List<StopCheck>();     // the stops Spansh is to be asked about, in route order: claimed by this check
        List<StopCheck> sent = [];              // the batch whose request went, until its answer is settled
        try
        {
            if (Gone(mine)) return;
            lock (guard) mine.State = Checking;
            Tell(mine);

            // Your finds as the check begins, read once for all its stops and away from the game's thread: the first read
            // of finds.json is from disk.
            HashSet<long> yours = await Task.Run(FindIds);
            // The game plotted the route through these systems, with their numbers: any of them Nav scan read off a
            // picture exists.
            await Task.Run(() => ConfirmNow([.. mine.Stops.Select(stop => stop.Id64).Where(id64 => id64 > 0)]));
            List<StopCheck> pending = [.. mine.Stops];
            while (pending.Count > 0)
            {
                asking = [];
                var aside = new List<StopCheck>();   // taken by another check of many: waited for once this one holds none
                foreach (StopCheck stop in pending)
                {
                    if (Gone(mine))
                    {
                        Release(asking);
                        return;
                    }
                    switch (await OfflineAlone(stop, yours, mine.Leaving))
                    {
                        case Settling.Ask:
                            asking.Add(stop);
                            break;
                        case Settling.Aside:
                            aside.Add(stop);
                            break;
                    }
                    Tell(mine, always: false);
                }
                // What was settled on this PC, into the memory's file at once: one write for them all.
                await Memory.SaveAsync();
                Tell(mine);

                for (int at = 0; at < asking.Count; at += Spansh.MostNames)
                {
                    List<StopCheck> batch = asking.GetRange(at, Math.Min(Spansh.MostNames, asking.Count - at));
                    if (Gone(mine))
                    {
                        Release(asking.Skip(at));
                        return;
                    }
                    lock (guard)
                    {
                        foreach (StopCheck stop in batch) stop.State = Checking;
                    }
                    Tell(mine);
                    NamesLookUp answer = await survey.LookUpNames([.. batch.Select(stop => (stop.Name, stop.Id64))], waiting =>
                    {
                        lock (guard) mine.State = waiting ? Waiting : Checking;
                        Tell(mine);
                    }, mine.Leaving);
                    if (answer.NotSent)
                    {
                        // Gone while it waited its turn behind another of the map's jobs: nothing was asked, nothing counted,
                        // and the stops are anybody's to check again.
                        Release(asking.Skip(at));
                        return;
                    }
                    // What Within reach listed while the batch waited its turn (the request it waited behind, after a jump)
                    // is settled from its answer: those were left out of the request.
                    List<StopCheck> rest = FromNear(batch, answer);
                    if (answer.UsedUp)
                    {
                        // Nothing more can be asked this session: the stops left say so, and are not asked about again.
                        List<StopCheck> left = [.. rest, .. asking.Skip(at + batch.Count)];
                        Log.Error($"3D map: {left.Count} stops of the plotted route were not checked: the requests to Spansh for this session are used up");
                        foreach (StopCheck stop in left) Settle(stop, null, null, null, LimitReached);
                        lock (guard) (mine.State, mine.Reason) = (Stopped, LimitReached);
                        break;
                    }
                    if (answer.Sent)
                    {
                        sent = rest;
                        lock (guard) mine.Requests++;
                        if (answer.Problem is { } problem)
                        {
                            // Not sent again: the stops say why, and the batches after it are still asked.
                            Log.Error($"3D map: {rest.Count} stops of the plotted route could not be checked at Spansh: {problem}");
                            foreach (StopCheck stop in rest) Settle(stop, null, CheckMemory.BySpansh, null, problem);
                        }
                        else await Settle(rest, answer);
                    }
                    sent = [];
                    // What the batch found, into the memory's file before the next is asked.
                    await Memory.SaveAsync();
                    Tell(mine);
                }

                // This check holds no system now: the ones another check had taken are waited for, and settled from what
                // that check found. One it gave up unasked (it went) is looked at again, as at first.
                asking = [];
                pending = await AfterOthers(aside, mine.Leaving);
                if (Gone(mine)) return;
                Tell(mine);
            }
            Finish(mine);
        }
        catch (Exception unexpected)
        {
            // Only this check's own stops are touched: one asked about and not yet settled gets this for its answer (as a
            // target's check would); one it took to ask about but never did is given back for a later check; the rest
            // keep whatever the memory knows of them, and only show here that this check did not get to them.
            Log.Error("3D map: the check of the plotted route could not be finished", unexpected);
            foreach (StopCheck stop in mine.Stops)
            {
                bool ended;
                lock (guard) ended = stop.Ended;
                if (ended) continue;
                if (sent.Contains(stop)) Settle(stop, null, null, null, Unexpected);
                else
                {
                    Release([stop]);
                    lock (guard) Show(stop, Failed, null, Unexpected);
                }
            }
            Finish(mine);
        }
        finally
        {
            // Whatever was settled and not yet written (a check that ended part way included).
            await Memory.SaveAsync();
        }
    }

    bool Gone(RouteRun check)
    {
        lock (guard) return check.Gone;
    }

    /// <summary>The numbers of Your finds, those held while finds.json could not be read with them. Reads finds.json the first time: call it away from the game's thread.</summary>
    HashSet<long> FindIds()
    {
        lock (guard) return new HashSet<long>((Loaded() ?? []).Concat(unsaved).Select(find => find.Id64));
    }

    /// <summary>
    /// <see cref="Offline"/>, with what goes wrong in Studio itself kept to the one stop, as a target's check keeps it to
    /// its system: an odd name or a kept file misread fails that stop (said and logged), and the route's other stops
    /// are checked as ever.
    /// </summary>
    async Task<Settling> OfflineAlone(StopCheck stop, HashSet<long> yours, CancellationToken leaving)
    {
        try
        {
            return await Offline(stop, yours, leaving);
        }
        catch (Exception unexpected)
        {
            Log.Error($"3D map: {stop.Name}, a stop of the plotted route, could not be checked", unexpected);
            bool claimed;
            lock (guard) claimed = stop.Claimed;
            // Its own answer when this check took it to ask about; otherwise what the memory knew of it stays.
            if (claimed) Settle(stop, null, null, null, Unexpected);
            else
                lock (guard) Show(stop, Failed, null, Unexpected);
            return Settling.Settled;
        }
    }

    /// <summary>
    /// A stop settled without asking Spansh, when it can be: from the 3D map's memory of checks (a target's check of it
    /// under way is waited for), else from the kept surveys and the detailed map, then Your finds (<paramref name="yours"/>,
    /// as the check began; a find made since is in the memory). A system that could not be checked in the last ten
    /// minutes is looked for on this PC again (a detailed map built since may have it), but not asked about again.
    /// <see cref="Settling.Ask"/> when Spansh has to be asked: the stop is then the check's own until it is (no other
    /// check asks about it meanwhile). <see cref="Settling.Aside"/> when another check of many has taken it: never waited
    /// for here, where this check may hold systems that one waits for (see above); and when this check went
    /// (<paramref name="leaving"/>) while it waited for a target's check.
    /// </summary>
    async Task<Settling> Offline(StopCheck stop, HashSet<long> yours, CancellationToken leaving)
    {
        long id64 = stop.Id64;
        CheckEntry? failed;
        while (true)
        {
            Task going;
            lock (guard)
            {
                CheckEntry? remembered = Memory.Get(stop.Name, id64);
                if (remembered is { Result: CheckMemory.Recorded or CheckMemory.NotRecorded })
                {
                    Show(stop, remembered.Result == CheckMemory.Recorded ? Recorded : NotRecorded, Earlier(remembered), null);
                    return Settling.Settled;
                }
                failed = remembered;   // a failure of the last ten minutes, or nothing
                if (id64 <= 0 || !underWay.TryGetValue(id64, out Claim? check))
                {
                    // This check's own to ask about, unless it may not be asked about again so soon.
                    if (id64 > 0 && failed is null)
                    {
                        underWay[id64] = new Claim(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), Batch: true);
                        stop.Claimed = true;
                    }
                    break;
                }
                if (check.Batch) return Settling.Aside;
                going = check.Done.Task;
            }
            // A check of the game's target came to it first (it waits for nothing of this check's): what comes of that is
            // this stop's answer too.
            try
            {
                await going.WaitAsync(leaving);
            }
            catch (OperationCanceledException) when (leaving.IsCancellationRequested)
            {
                return Settling.Aside;   // this check went meanwhile: it ends before it comes back to the stop
            }
        }

        Located kept = await Locate(stop.Name, id64);
        if (kept.Found)
        {
            Settle(stop, true, kept.By, kept.Source ?? "in what is kept on this PC", null);
            return Settling.Settled;
        }
        if (id64 > 0 && yours.Contains(id64))
        {
            Settle(stop, false, CheckMemory.ByFinds, InYourFinds, null);
            return Settling.Settled;
        }
        lock (guard)
        {
            if (failed is not null)
            {
                Show(stop, Failed, null, failed.Reason ?? NotAgain);
                return Settling.Settled;
            }
            stop.Kept = kept;
        }
        return Settling.Ask;
    }

    /// <summary>
    /// The stops another check of many had taken when this one came to them (<see cref="Settling.Aside"/>), waited for
    /// once this check has asked about its own and holds none, so the two can never wait on each other: each settled
    /// from the memory as soon as that check has its answer. Gives those that came to nothing there (that check went
    /// before it asked, or its answer was a failure): this check's to look at again. Nothing when this check went
    /// meanwhile (<paramref name="leaving"/>).
    /// </summary>
    async Task<List<StopCheck>> AfterOthers(List<StopCheck> aside, CancellationToken leaving)
    {
        var again = new List<StopCheck>();
        foreach (StopCheck stop in aside)
        {
            Task? going;
            lock (guard) going = underWay.TryGetValue(stop.Id64, out Claim? claim) ? claim.Done.Task : null;
            if (going is not null)
            {
                try
                {
                    await going.WaitAsync(leaving);
                }
                catch (OperationCanceledException) when (leaving.IsCancellationRequested)
                {
                    return [];
                }
            }
            lock (guard)
            {
                if (Memory.Get(stop.Name, stop.Id64) is { Result: CheckMemory.Recorded or CheckMemory.NotRecorded } remembered)
                {
                    Show(stop, remembered.Result == CheckMemory.Recorded ? Recorded : NotRecorded, Earlier(remembered), null);
                    continue;
                }
            }
            again.Add(stop);
        }
        return again;
    }

    /// <summary>
    /// The stops of a batch that Within reach's answer listed by the time the batch's turn at the gate came
    /// (<see cref="NamesLookUp.Near"/>), settled from it as <see cref="Locate"/> settles them: recorded, and not asked
    /// about. Gives the others, those the request was for.
    /// </summary>
    List<StopCheck> FromNear(List<StopCheck> batch, NamesLookUp answer)
    {
        var rest = new List<StopCheck>();
        for (int at = 0; at < batch.Count; at++)
        {
            if (at < answer.Near.Count && answer.Near[at] is not null) Settle(batch[at], true, CheckMemory.BySpansh, FoundWithinReach, null);
            else rest.Add(batch[at]);
        }
        return rest;
    }

    /// <summary>
    /// A batch of stops by what Spansh answered: a system counts only when its name and its number (when Spansh gives
    /// one) are the stop's. The stops it does not have are kept as finds with one write of finds.json for the batch,
    /// away from the game's thread (a route into space nobody has recorded can bring a hundred at once), and then
    /// told one by one (<see cref="Found"/>).
    /// </summary>
    async Task Settle(List<StopCheck> batch, NamesLookUp answer)
    {
        string day = Day(DateTime.UtcNow);
        var made = new List<HeatmapFind>();
        foreach (StopCheck stop in batch)
        {
            NamedSystem? listed = answer.Found!.FirstOrDefault(system => system.Name.Equals(stop.Name, StringComparison.OrdinalIgnoreCase)
                && (system.Id64 <= 0 || stop.Id64 <= 0 || system.Id64 == stop.Id64));
            if (listed is not null)
            {
                if (stop.Id64 > 0 && listed is { X: { } lx, Y: { } ly, Z: { } lz })
                    lock (guard) places[stop.Id64] = new Located(true, lx, ly, lz, null, [], FoundBySpansh, CheckMemory.BySpansh);
                Settle(stop, true, CheckMemory.BySpansh, FoundBySpansh, null);
            }
            else if (!answer.Complete) Settle(stop, null, CheckMemory.BySpansh, null, CutShort);
            else if (stop.Id64 <= 0) Settle(stop, null, CheckMemory.BySpansh, null, NoNumber);
            else
            {
                // Real (the game plotted a route through it, and gave its number) and in no database: one of Your finds,
                // placed where the game says it is, else where its number says, to within half its cube.
                Located kept;
                lock (guard) kept = stop.Kept ?? Guess(stop.Name, stop.Id64, []);
                List<string> how = [.. kept.How, $"{NotFoundBySpansh} ({day})"];
                made.Add(stop.At is [var x, var y, var z]
                    ? new HeatmapFind(stop.Name, stop.Id64, x, y, z, null, DateTime.UtcNow, FromRoute, how)
                    : new HeatmapFind(stop.Name, stop.Id64, kept.X, kept.Y, kept.Z, kept.PlusMinus, DateTime.UtcNow, FromRoute, how));
                Settle(stop, false, CheckMemory.BySpansh, NotFoundBySpansh, null);
            }
        }
        if (made.Count == 0) return;
        await Task.Run(() => Keep(made));
        Log.Info(made.Count == 1
            ? $"3D map: {made[0].Name}, a stop of the plotted route, is not recorded in any database Studio knows; kept in your finds"
            : $"3D map: {made.Count} stops of the plotted route are not recorded in any database Studio knows; kept in your finds: {string.Join(", ", made.Select(find => find.Name))}");
        foreach (HeatmapFind find in made) Found?.Invoke(find);
    }

    /// <summary>
    /// A stop's answer, kept in the memory of checks for its system too (written by the next save): recorded, not
    /// recorded, or (null) not checkable, with why. A system this check took to ask about is given up to whoever waits for it.
    /// </summary>
    /// <param name="by">Where the answer came from (<see cref="CheckEntry.By"/>).</param>
    void Settle(StopCheck stop, bool? recorded, string? by, string? how, string? reason)
    {
        Claim? waiting = null;
        lock (guard)
        {
            Remember(stop.Name, stop.Id64, recorded, by, how, reason);
            if (stop.Claimed)
            {
                stop.Claimed = false;
                underWay.Remove(stop.Id64, out waiting);
            }
            Show(stop, recorded switch { true => Recorded, false => NotRecorded, null => Failed }, how, reason);
        }
        waiting?.Done.TrySetResult();
    }

    /// <summary>What a stop shows. Call under the guard.</summary>
    static void Show(StopCheck stop, string state, string? how, string? reason) => (stop.State, stop.How, stop.Reason) = (state, how, reason);

    /// <summary>Stops a check ended before asking about: theirs again, for the next check that comes to them. Only the ones it took to ask about (<see cref="StopCheck.Claimed"/>).</summary>
    void Release(IEnumerable<StopCheck> stops)
    {
        var freed = new List<Claim>();
        lock (guard)
        {
            foreach (StopCheck stop in stops)
            {
                if (!stop.Claimed || stop.Ended) continue;
                stop.Claimed = false;
                if (underWay.Remove(stop.Id64, out Claim? claim)) freed.Add(claim);
            }
        }
        foreach (Claim claim in freed) claim.Done.TrySetResult();
    }

    /// <summary>
    /// The check is over: done, or stopped before every stop could be asked about. The route's end is told as the
    /// game's route end, as the check of the end alone tells it, so the 3D map's marker says what came of it.
    /// </summary>
    void Finish(RouteRun mine)
    {
        StopCheck? end;
        RouteCheckView view;
        lock (guard)
        {
            if (mine.State != Stopped) mine.State = Done;
            mine.Finished = DateTime.UtcNow;
            end = mine.Gone ? null : mine.Stops[^1];
            view = ViewOf(mine);
        }
        Log.Info($"3D map: the check of the plotted route is {(view.State == Stopped ? "stopped: " + view.Reason : "done")}: {view.Recorded} recorded, {view.NotRecorded} not recorded, {view.Failed} not checked, {view.Requests} requests to Spansh");
        Tell(mine);
        if (end is not { Id64: > 0 }) return;
        string state, name;
        string? reason;
        lock (guard) (state, name, reason) = (end.State, end.Name, end.Reason);
        News?.Invoke(new TargetNews(FromRouteEnd, name, end.Id64, state switch { Recorded => Recorded, NotRecorded => NotRecorded, _ => Unchecked }, reason));
    }

    /// <summary>
    /// Tells the page and the overlay how the route's check stands. Nothing is told of a check that is no longer the
    /// route's; while stops are settled one by one on this PC (<paramref name="always"/> false), at most every <see cref="TellEvery"/>.
    /// Null for no check in particular: the route's check, or that there is none.
    /// </summary>
    void Tell(RouteRun? mine, bool always = true)
    {
        RouteCheckView view;
        lock (guard)
        {
            if (mine is not null)
            {
                if (mine != run) return;
                DateTime now = DateTime.UtcNow;
                if (!always && now - mine.Told < TellEvery) return;
                mine.Told = now;
            }
            view = ViewOf(run);
        }
        RouteNews?.Invoke(new RouteCheckNews(view.State, view.Mode, view.Started, view.Finished, view.Total, view.Checked, view.Recorded, view.NotRecorded,
            view.Failed, view.Requests, view.Reason) { Plotted = view.Plotted, PlottedTo = view.PlottedTo, Line = view.Line });
    }

    /// <summary>How a check stands, as the page and the overlay are given it. Call under the guard.</summary>
    RouteCheckView ViewOf(RouteRun? check)
    {
        if (check is null)
        {
            bool on = checkTargets && routeMode == CheckAll;
            return new RouteCheckView(on ? NoRoute : Off, routeMode, null, null, 0, 0, 0, 0, 0, 0, null, [])
            {
                Plotted = on ? routeNow.Stops : 0,
                PlottedTo = on ? routeNow.End : null,
            };
        }
        RouteCheckStop[] stops = [.. check.Stops.Select(stop => new RouteCheckStop(stop.Name, stop.Id64, stop.At?[0], stop.At?[1], stop.At?[2], stop.State, stop.How, stop.Reason))];
        int recorded = stops.Count(stop => stop.State == Recorded), notRecorded = stops.Count(stop => stop.State == NotRecorded);
        return new RouteCheckView(check.State, routeMode, check.Started, check.Finished, stops.Length, recorded + notRecorded, recorded, notRecorded,
            stops.Count(stop => stop.State == Failed), check.Requests, check.Reason, stops);
    }

    /// <summary>
    /// The overlay's line of the route's check, after its label: dim while it goes on ("checking 12 of 34…"), and once
    /// over, gold when a stop is not recorded (naming it: the player may want to fly there first), green when every stop
    /// is, plain otherwise (some could not be checked). A route plotted before Studio started says it is not checked, and
    /// how to have it checked. Null while there is nothing to tell: switched off, or no route plotted. The 3D map's page
    /// says the same in its own longer words.
    /// </summary>
    public static RouteCheckLine? LineOf(RouteCheckView? check)
    {
        if (check is null || check.State == Off) return null;
        if (check.State == NoRoute)
            return check.Plotted > 0
                ? new RouteCheckLine($"not checked: your route was plotted before Studio started{LineDot}plot it again, or press Check this route on the 3D map", RouteCheckLine.Plain)
                : null;
        int ended = check.Checked + check.Failed;
        string total = Whole(check.Total);
        string notRecorded = check.NotRecorded == 0 ? ""
            : $"{LineDot}{Whole(check.NotRecorded)} not recorded: {Names(check.Stops.Where(stop => stop.State == NotRecorded).Select(stop => stop.Name))}";
        string notChecked = check.Failed == 0 ? "" : $"{LineDot}{Whole(check.Failed)} not checked";
        string over = check.NotRecorded > 0 ? RouteCheckLine.Gold : check.Failed == 0 ? RouteCheckLine.Green : RouteCheckLine.Plain;
        return check.State switch
        {
            // Waiting its turn: behind the check of the route before (which may be busy on this PC), or behind another of
            // the map's requests. Part way through, the player still reads it as checking.
            Waiting when ended == 0 => new RouteCheckLine($"waiting to check {StopCount(check.Total)}…", RouteCheckLine.Dim),
            Waiting or Checking => new RouteCheckLine($"checking {Whole(ended)} of {total}…", RouteCheckLine.Dim),
            Stopped => new RouteCheckLine($"stopped: {check.Reason}{LineDot}{Whole(check.Checked)} of {total} checked{notRecorded}",
                check.NotRecorded > 0 ? RouteCheckLine.Gold : RouteCheckLine.Plain),
            _ => new RouteCheckLine($"done{LineDot}{StopCount(check.Total)}{(notRecorded.Length > 0 || notChecked.Length > 0 ? notRecorded + notChecked : LineDot + "all recorded")}", over),
        };

        static string Whole(int count) => count.ToString("N0", CultureInfo.InvariantCulture);
        static string StopCount(int count) => count == 1 ? "1 stop" : $"{Whole(count)} stops";

        // The first three by name, so the line stays one line.
        static string Names(IEnumerable<string> names)
        {
            string[] all = [.. names];
            return string.Join(", ", all.Take(3)) + (all.Length > 3 ? $" and {Whole(all.Length - 3)} more" : "");
        }
    }
}
