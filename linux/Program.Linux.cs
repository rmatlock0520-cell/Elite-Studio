using System.Runtime.InteropServices;
using System.Text.Json;
using EliteStudio.Core;
using EliteStudio.UI;

namespace EliteStudio.App;

/// <summary>
/// Start-up on Linux: the same as on Windows (Program.cs) without what the Linux version leaves out, which is Studio
/// Flight, the in-game overlay with its boxes and keys, and Nav scan's reading of screenshots. Reads the options, makes
/// sure one copy runs per data folder, starts the game watcher and the local web server, then shows the window until
/// Studio ends.
///
/// Options: as on Windows --data-dir, --web-root, --journal-dir, --bindings-dir, --game-dir, --port N, --headless,
/// --smoke and --offline, plus --browser: no window, Studio opens in the player's web browser (as it does by itself
/// when the window cannot start).
///
/// The window's toolkit (GTK) needs the main thread, so the game state, the web server's handlers and the
/// twice-a-second update live on a thread of their own, "Studio" (<see cref="StudioThread"/>), as they live on WPF's
/// own thread on Windows.
/// </summary>
public static partial class Program
{
    public static int Main(string[] args)
    {
        Dictionary<string, string> options = ParseOptions(args);
        try
        {
            return Start(options);
        }
        catch (Exception problem)
        {
            Log.Error($"Elite Studio stopped: {problem}");
            if (options.ContainsKey("smoke")) Log.Error($"SMOKE FAILED: {problem.Message}");
            // If the log itself could not start, the reason goes to the terminal (or the desktop's own log) instead.
            if (Log.FilePath is null) Console.Error.WriteLine($"Elite Studio stopped: {problem}");
            return 1;
        }
    }

    static int Start(Dictionary<string, string> options)
    {
        // (Until the log is started below, what these two write goes nowhere; leaving quietly still holds.)
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error($"Fatal problem: {e.ExceptionObject}");
            Environment.Exit(1);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error($"Background problem: {e.Exception}");
            e.SetObserved();
        };
        Features = StudioFeatures.Linux;
        // The data folder first, so the log runs before anything else can go wrong; the game's folders follow below.
        Paths.Init(options);
        Log.Start(Paths.DataDir);

        // Elite runs on Linux through Steam's Proton, which keeps the game's journal and key bindings inside a Windows
        // folder of the game's own (its prefix). Studio looks for it there, unless the player chose another journal
        // folder in Studio (kept in the data folder's settings) or an option names one.
        SteamGames.Found found;
        try
        {
            found = SteamGames.Find();
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            // Studio still starts, and the player can choose the journal folder in Studio UI.
            Log.Error("Steam's folders could not be read", unreadable);
            found = new SteamGames.Found(null, null, []);
        }
        Paths.Init(options, found.Journal, found.Bindings);
        string? chosen = new Settings(Path.Combine(Paths.DataDir, "settings.json")).Get<LinuxSettings>("linux").JournalDir;
        if (chosen is not null && Directory.Exists(chosen)) Paths.Init(options, chosen, BindingsBeside(chosen) ?? found.Bindings);
        using var oneCopy = new Mutex(true, MutexName(Paths.DataDir), out bool first);
        if (!first)
        {
            if (options.ContainsKey("smoke"))
            {
                // A check run has to report on itself. Bringing the open Studio forward and leaving with 0 would read
                // as a pass although nothing was checked.
                Log.Error("SMOKE FAILED: Elite Studio is already running on this data folder");
                return 1;
            }
            ShowRunningCopy();
            return 0;
        }
        // Where the game's files were found: the first thing to know when Studio sees nothing of the game.
        Log.Info(found.Journal is null ? "No Elite Dangerous journal folder was found in Steam's folders" : $"Elite Dangerous journal folder found: {found.Journal}");
        if (chosen is not null) Log.Info(Directory.Exists(chosen) ? $"Journal folder chosen in Studio: {chosen}" : $"The journal folder chosen in Studio is not there: {chosen}");
        Log.Info($"Reading the journal from {Paths.JournalDir} and the key bindings from {Paths.BindingsDir}");
        Log.Info(found.Installs.Count == 0 ? "No Elite Dangerous install was found in Steam's libraries" : $"Elite Dangerous installs found: {string.Join(", ", found.Installs)}");
        string runningFile = Path.Combine(Paths.DataDir, RunningFile);
        try
        {
            return Run(options, runningFile, found);
        }
        finally
        {
            File.Delete(runningFile);
        }
    }

    /// <summary>
    /// The key bindings that belong with a journal folder the player chose: those of the same Windows user in the same
    /// Proton prefix (users/NAME/Saved Games/Frontier Developments/Elite Dangerous beside users/NAME/AppData/Local/...).
    /// Null when that folder is not there.
    /// </summary>
    static string? BindingsBeside(string journal)
    {
        string bindings = Path.GetFullPath(Path.Combine(journal, "..", "..", "..", "AppData", "Local", "Frontier Developments", "Elite Dangerous", "Options", "Bindings"));
        return Directory.Exists(bindings) ? bindings : null;
    }

    static int Run(IReadOnlyDictionary<string, string> options, string runningFile, SteamGames.Found found)
    {
        bool headless = options.ContainsKey("headless");
        var window = new StudioWindow(headless, smoke: options.ContainsKey("smoke") && !headless,
            browser: options.ContainsKey("browser") || Environment.GetEnvironmentVariable("ELITE_STUDIO_BROWSER") == "1");
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        Task<int> studio = StudioThread.Run(() => Serve(options, runningFile, found, window, started, stop.Token));
        // Studio could not start (no free port, or a problem): leave with its answer, or its problem for Main to log.
        if (Task.WaitAny(started.Task, studio) == 1) return studio.GetAwaiter().GetResult();

        // Ctrl+C in a terminal, or the system asking Studio to stop: Studio leaves as on Exit.
        void Stop(PosixSignalContext signal)
        {
            signal.Cancel = true;
            window.Quit();
        }
        using var interrupted = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
        using var terminated = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);

        window.Show(started.Task.Result);
        stop.Cancel();
        // The studios close on their own thread. One that hangs there must not keep Studio from ending.
        if (!studio.Wait(TimeSpan.FromSeconds(10))) Log.Error("Elite Studio did not finish closing within 10 seconds, so it left anyway");
        Log.Info("Exit");
        return window.ExitCode;
    }

    /// <summary>
    /// Everything that runs on WPF's thread on Windows, here on Studio's own: the studios, wired as on Windows and in the
    /// same order, the server, and the twice-a-second update until stop. started gets the server's address once it answers.
    /// </summary>
    static async Task<int> Serve(IReadOnlyDictionary<string, string> options, string runningFile, SteamGames.Found found,
        StudioWindow window, TaskCompletionSource<string> started, CancellationToken stop)
    {
        bool smoke = options.ContainsKey("smoke");
        var settings = new Settings(Path.Combine(Paths.DataDir, "settings.json"));
        // The control presets that ship with the game are in its install folder: the practice one, else the first one
        // found in Steam's libraries, else the one the player picked by hand in Studio UI.
        string? GameDir() => Paths.GameDir ?? found.Installs.FirstOrDefault() ?? settings.Get<UiSettings>("ui").GameDir;
        using var watcher = new GameWatcher(Paths.JournalDir, Paths.BindingsDir, gameDir: GameDir);
        using var web = new WebServer(Paths.WebRoot);
        AppApi.Map(web, watcher.State, window, settings);
        // A --smoke run passes once the window's page asks for the game state, which the pages do as soon as they load.
        if (smoke) web.Map("GET", "/api/state", _ =>
        {
            window.PageLoaded();
            return WebResponse.Json(watcher.State.Snapshot());
        });
        ThemeApi.Map(web, settings, options.ContainsKey("offline") ? new Offline() : null);
        // The clients for the community's services are made here and closed when the app ends. Studio Nav's, for Spansh
        // (--offline: a practice run reaches neither Spansh nor EDAstro):
        using var spansh = new Nav.Spansh(options.ContainsKey("offline") ? new Offline() : null);
        Nav.NavApi.Map(web, watcher.State, settings, spansh);
        // One copy of EDAstro's sector list for the flat map and the 3D map, so a new list reaches both.
        var coverage = new Nav.ExploreCoverage(Paths.DataDir, options.ContainsKey("offline") ? new Offline() : null);
        Nav.ExploreTrip trip = Nav.ExploreApi.Map(web, watcher.State, settings, spansh, coverage);
        // The 3D map: the same Spansh client and sector list, and a folder of its own for what it keeps.
        string heatmap = Path.Combine(Paths.DataDir, "heatmap");
        var heatmapSurvey = new Nav.HeatmapSurvey(spansh, heatmap);
        var fineMap = new Nav.FineMap(heatmap);
        var routeCheck = new Nav.HeatmapFinds(watcher.State, heatmapSurvey, fineMap, settings);
        // Nav scan cannot read pictures on Linux: it watches no folder, so its routes answer but it stays off.
        using var navScan = new Nav.NavScan(watcher.State, routeCheck, heatmap, settings, places: () => []);
        Nav.HeatmapApi.Map(web, watcher.State, trip, spansh, coverage, heatmapSurvey,
            new Nav.NebulaList(heatmap, options.ContainsKey("offline") ? new Offline() : null), new Nav.FirstsLog(watcher.State, Paths.JournalDir, heatmap),
            fineMap, settings, routeCheck, navScan: navScan);
        Loadouts.EngineeringPlanner planner = Loadouts.EngineeringApi.Map(web, watcher.State, settings);
        Loadouts.GuardianApi.Map(web, watcher.State, settings);
        // And Studio Market's: one for its two kinds of search, so they share one queue to the community's servers, one memory
        // of answers and one price overview. --offline: practice runs must not send a real search every time they start.
        using var marketData = new Market.MarketData(options.ContainsKey("offline") ? new Offline() : null);
        // A practice copy must not pull in the player's real files: for it there is no Elite Haul Overlay to copy from.
        string? oldTool = Paths.Practice ? "" : null;
        var hauling = new Market.Hauling(watcher.State, settings, new Market.TradeSearch(marketData), oldSettingsFile: oldTool);
        Market.MarketApi.Map(web, hauling);
        // The price list of every station whose commodities screen the player opens, kept for "Find by name".
        var stationBook = new Market.StationBook(watcher.State, Path.Combine(Paths.DataDir, "stations.json"));
        // With the technology brokers the player saw themselves.
        Market.Finder finder = Market.FindApi.Map(web, watcher.State, hauling, marketData, book: stationBook, brokers: new Market.BrokerBook(settings));
        // The market sniffer: saved commodity searches run once an hour, with a chime when one is met.
        Market.Sniffer sniffer = Market.SnifferApi.Map(web, watcher.State, hauling, marketData, settings);
        // Studio Loadouts last: a saved build is handed on to the planner and, through Studio Market's searches, to a shopping trip.
        Loadouts.LoadoutsApi.Map(web, watcher.State, planner, finder);

        // Each part of the update in a try of its own, so one that fails never stops the others. This runs twice a
        // second, so a problem that keeps coming back is logged once, not every time.
        var tickProblems = new Dictionary<string, string?>();
        void Part(string name, Action work)
        {
            try
            {
                work();
                tickProblems[name] = null;
            }
            catch (Exception problem)
            {
                if (problem.Message != tickProblems.GetValueOrDefault(name)) Log.Error($"The twice-a-second update of {name} failed", problem);
                tickProblems[name] = problem.Message;
            }
        }
        void Tick()
        {
            Part("the game watcher", watcher.Poll);
            Part("hauling", hauling.Tick);
            Part("the market sniffer", sniffer.Tick);
        }

        // Catch up with the journal before the server opens, so the very first /api/state is already right.
        Tick();
        int firstPort = FirstPort(options);
        if (!web.Start(firstPort))
        {
            string problem = $"No free port from {firstPort} to {firstPort + 9}; close other copies of Elite Studio and start it again";
            Log.Error(problem);
            if (smoke) Log.Error($"SMOKE FAILED: {problem}");
            return 1;
        }
        File.WriteAllText(runningFile, JsonSerializer.Serialize(new RunningCopy(web.Port, web.Token, Environment.ProcessId), Settings.Json));
        Log.Info($"Elite Studio for Linux {Version} started on port {web.Port}");

        watcher.State.Changed += () => web.Send("state", watcher.State.Snapshot());
        started.SetResult(web.Url);
        // The twice-a-second update. Each tick waits for this thread, so a slow one never piles up behind another.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        using CancellationTokenRegistration stopping = stop.Register(timer.Dispose);
        while (await timer.WaitForNextTickAsync()) Tick();
        return 0;
    }
}
