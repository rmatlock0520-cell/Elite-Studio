using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EliteStudio.App;
using EliteStudio.Core;
using Microsoft.Win32;

namespace EliteStudio.Nav;

/// <summary>A picture Nav scan read, and what came of its names (GET /api/nav/heatmap/navscan, "pictures", the newest first).</summary>
/// <param name="File">The picture's file name.</param>
/// <param name="At">When Studio began reading it (UTC).</param>
/// <param name="Names">System names read off it: one read unclearly counts once, whatever it may be.</param>
/// <param name="Failed">Names that could not be checked.</param>
/// <param name="Unsure">Names read unclearly that no recorded system settled (see <see cref="NavScanName.Unsure"/>).</param>
/// <param name="Dropped">Catalogue names ("HIP 12766") the detailed map does not have near you: nothing can tell whether they were read right, so they were not checked.</param>
/// <param name="State"><see cref="NavScan.Reading"/>, <see cref="NavScan.Checking"/>, <see cref="NavScan.Done"/> or <see cref="NavScan.Failed"/> (see <see cref="Problem"/>).</param>
public sealed record NavScanPicture(string File, DateTime At, int Names, int Recorded, int NotRecorded, int Failed, int Unsure, int Dropped, string State)
{
    /// <summary>Why it could not be read, in the player's words; null otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Problem { get; init; }

    /// <summary>
    /// Read (state <see cref="NavScan.Checking"/>), its check waits its turn: behind the check of the picture before, or
    /// behind another of the 3D map's requests to Spansh (a survey's, say). Left out when false.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Waiting { get; init; }

    /// <summary>
    /// Rows with a system name that could not be made out (<see cref="PictureNames.Unreadable"/>: the reader read them in
    /// too many different ways), so they were not checked. Not among <see cref="Names"/>. Left out when 0.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Unreadable { get; init; }
}

/// <summary>
/// The line the in-game overlay shows of Nav scan, after its "NAV SCAN" label (<see cref="NavScan.LineOf"/>), as
/// <see cref="RouteCheckLine"/> is the route check's: Studio Nav's page prints it as it comes ("line" in GET navscan and
/// in every "navscan" event), and the hauling overlay's own drawing of the nav box (--render-shots) prints the same words.
/// </summary>
/// <param name="Tone">As <see cref="RouteCheckLine.Tone"/>: dim while it goes on, gold once a name is not recorded, green when all are, plain otherwise.</param>
public sealed record NavScanLine(string Words, string Tone);

/// <summary>Nav scan as it stands (GET /api/nav/heatmap/navscan).</summary>
/// <param name="On">The switch (setting "navScan").</param>
/// <param name="Volume">Its sounds' volume, 0 to 100 (setting "navScanVolume").</param>
/// <param name="Watching">The folders watched for new pictures just now.</param>
/// <param name="Pictures">The pictures read this session, the newest first, <see cref="NavScan.MostPictures"/> at most.</param>
/// <param name="Names">The names read off those pictures, the newest picture's first.</param>
/// <param name="Problem">What stands in the way, in the player's words; null when nothing does.</param>
public sealed record NavScanView(
    bool On, int Volume, IReadOnlyList<string> Watching,
    [property: JsonPropertyOrder(1)] IReadOnlyList<NavScanPicture> Pictures,
    [property: JsonPropertyOrder(1)] IReadOnlyList<NavScanName> Names,
    string? Problem)
{
    /// <summary><see cref="NavScan.Off"/>, <see cref="NavScan.Watching"/> (on, waiting for a picture), <see cref="NavScan.Reading"/> or <see cref="NavScan.Checking"/>.</summary>
    [JsonPropertyOrder(-1)]
    public string State { get; init; } = NavScan.Off;

    /// <summary>The overlay's line of it; null while it is off.</summary>
    public NavScanLine? Line { get; init; }
}

/// <summary>
/// News of Nav scan, sent as a "heatmap" event with kind "navscan" on every change (switched, a folder found, a picture
/// being read, checked, done): what GET navscan gives without the lists, with the newest picture and the overlay's line.
/// </summary>
public sealed record NavScanNews(string State, bool On, int Volume, IReadOnlyList<string> Watching, NavScanPicture? Picture, string? Problem, NavScanLine? Line)
{
    /// <summary>Always "navscan": it tells these events from the 3D map's others.</summary>
    [JsonPropertyOrder(-1)]
    public string Kind => "navscan";

    /// <summary>The names found not recorded just now, by the check of the newest picture (for a message on the page); empty otherwise.</summary>
    public IReadOnlyList<string> Found { get; init; } = [];

    /// <summary>The news that a picture's check just ended: what the memory of checks says of some systems may have changed. Not sent to the page.</summary>
    [JsonIgnore]
    internal bool Checked { get; init; }
}

/// <summary>
/// "Nav scan" (the owner, 15 October 2026): the player takes a screenshot of the game's Navigation panel (F12 in Steam,
/// F10 in the game), and Studio reads the system names on it and says which nobody has recorded, with a sound, in the
/// game's overlay and on the 3D map, without the player leaving the cockpit.
///
/// While it is on, Studio watches the folders the pictures go to: Steam's screenshots of Elite for every Steam user of
/// this PC (Steam\userdata\&lt;id&gt;\760\remote\359320\screenshots, Steam's own folder from the registry) and the game's own
/// (Pictures\Frontier Developments\Elite Dangerous), each as soon as it exists. Only pictures made after it was switched
/// on are read, each once ever (heatmap\navscan-read.json remembers them for 30 days, across restarts), when the file is
/// whole; Studio never copies, moves or deletes a picture. A picture is read with Windows' own text reader
/// (<see cref="NavScanOcr"/>), its names made out (<see cref="NavScanNames"/>), and checked the careful way every other
/// check is (<see cref="HeatmapFinds.CheckScanned"/>): the memory of checks, this PC, then Spansh. A quiet click says a
/// picture was read; a sonar ping that a system nobody has recorded turned up (one already known stays quiet, and so does
/// the system the commander is in). Pictures are read one after another as they come, and their names checked one
/// picture after another in the same order, apart: a check waiting its turn at Spansh (behind a survey, say) never keeps
/// the next picture from being read, and the overlay says it waits.
///
/// Lives on the UI thread like the game state it follows; the files and the reading are worked away from it.
/// </summary>
public sealed class NavScan : IDisposable
{
    /// <summary>What <see cref="NavScanView.State"/> says.</summary>
    public const string Off = "off", Watching = "watching", Reading = "reading", Checking = "checking";

    /// <summary>What <see cref="NavScanPicture.State"/> says, with <see cref="Reading"/> and <see cref="Checking"/>.</summary>
    public const string Done = "done", Failed = "failed";

    /// <summary>Pictures kept to show, the newest.</summary>
    public const int MostPictures = 20;

    /// <summary>The file in the 3D map's folder that remembers the pictures read.</summary>
    public const string ReadFile = "navscan-read.json";

    /// <summary>How long a picture read is remembered: long past any screenshot's being taken.</summary>
    public static readonly TimeSpan ReadKept = TimeSpan.FromDays(30);

    /// <summary>Why a number is no volume, in the player's words.</summary>
    public const string NoSuchVolume = "The Nav scan volume is a number from 0 to 100.";

    /// <summary>Why a sound is none, in the player's words.</summary>
    public const string NoSuchSound = "Nav scan has a ping and a click.";

    /// <summary>Steam's number for Elite Dangerous: its screenshots of the game are kept under it.</summary>
    const string EliteOnSteam = "359320";

    /// <summary>The kinds of picture Steam (.jpg, or .png uncompressed) and the game (.bmp, high-resolution .png) make.</summary>
    static readonly string[] PictureKinds = [".jpg", ".jpeg", ".png", ".bmp"];

    /// <summary>A picture made this long before the switch was turned on still counts: the file's time and the clock may differ that much.</summary>
    static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    const string Section = "heatmap";
    const string NoFolder = "Studio found no folder where your screenshots go yet. It watches Steam's screenshots of Elite (F12) and the game's own (F10), and starts as soon as the first one is taken.";
    const string Unexpected = "Studio could not read this picture.";
    const string SwitchedOff = "Nav scan was switched off while this picture was read.";

    /// <summary>Between the parts of the overlay's line: the overlay's own wide dot, as the route check's line has it.</summary>
    const string LineDot = "   ·   ";

    readonly GameState game;
    readonly HeatmapFinds finds;
    readonly Settings? settings;
    readonly Func<IReadOnlyList<string>> places;
    readonly SynchronizationContext? context;
    readonly ReadPictures read;
    readonly Lock guard = new();
    readonly Dictionary<string, FileSystemWatcher> watchers = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> queued = new(StringComparer.OrdinalIgnoreCase);   // pictures waiting their turn, being read or being checked
    readonly List<Picture> pictures = [];   // the newest first
    bool on;
    int volume;
    DateTime since;                          // when it was switched on (or Studio started with it on)
    (double X, double Y, double Z)? here;    // where the commander is
    long system;                             // the game's number for the system the commander is in; 0 while not known
    string? problem;
    Timer? looking;
    CancellationTokenSource stopping = new();
    // The pictures are read one at a time, and their names checked one picture at a time, in the same order, on a chain
    // of their own: a check waiting its turn at Spansh (behind a survey, say) never stops the next picture being read,
    // so the pilot hears the click and sees "reading…" at once.
    Task reading = Task.CompletedTask;
    Task checking = Task.CompletedTask;

    /// <param name="game">The game state: where the commander is, for which spellings of a name can be right.</param>
    /// <param name="finds">The 3D map's checks, which check the names and keep the finds.</param>
    /// <param name="folder">The 3D map's folder, for <see cref="ReadFile"/>.</param>
    /// <param name="settings">Where the switch and the volume are kept (section "heatmap"); without it, only for the session.</param>
    /// <param name="places">The folders pictures may come to, there or not yet; Steam's and the game's when left out (<see cref="PicturePlaces"/>). Tests give their own.</param>
    public NavScan(GameState game, HeatmapFinds finds, string folder, Settings? settings = null, Func<IReadOnlyList<string>>? places = null)
    {
        (this.game, this.finds, this.settings) = (game, finds, settings);
        this.places = places ?? PicturePlaces;
        read = new ReadPictures(Path.Combine(folder, ReadFile), () => Now());
        context = SynchronizationContext.Current;
        HeatmapSettings kept = settings?.Get<HeatmapSettings>(Section) ?? new HeatmapSettings();
        on = kept.NavScan;
        // A volume typed into the file by hand that the page would refuse is brought into range.
        volume = Math.Clamp(kept.NavScanVolume, 0, 100);
        game.Changed += Look;
        Look();
        if (on) Start();
    }

    /// <summary>Raised on every change (<see cref="NavScanNews"/>). Raised on the thread the change is made on: the UI thread, or the timer's.</summary>
    public event Action<NavScanNews>? News;

    /// <summary>Reads a picture's text; tests give a stand-in, so no test needs Windows' text reader.</summary>
    internal Func<string, CancellationToken, Task<PictureText>> Reader { get; init; } = NavScanOcr.Read;

    /// <summary>Plays a sound at a volume of 1 to 100; tests give a stand-in, so no test makes a sound.</summary>
    internal Action<string, int> Play { get; init; } = NavScanSounds.Play;

    /// <summary>The wait between two looks at a picture being written; tests wait none.</summary>
    internal TimeSpan SettleWait { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>A picture still being written after this long is given up.</summary>
    internal TimeSpan GiveUp { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The clock (UTC); tests move it on.</summary>
    internal Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    /// <summary>How often Studio looks for a folder that is not there yet (the game's own appears with its first picture); tests look more often.</summary>
    internal TimeSpan LookEvery { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The pictures offered so far, all of them read and checked (or passed over); for tests, which wait for them.</summary>
    internal Task Idle
    {
        get
        {
            lock (guard) return checking;
        }
    }

    /// <summary>The pictures offered so far, all of them read (or passed over), whether or not their checks are done; for tests.</summary>
    internal Task ReadIdle
    {
        get
        {
            lock (guard) return reading;
        }
    }

    // ------------------------------------------------------------ the switch and the volume

    /// <summary>
    /// "Nav scan": on, new pictures are read; off, nothing is watched, and a picture being read stops (a request already
    /// sent is let finish). Kept in settings.json. Only pictures made after it is switched on are read.
    /// </summary>
    public bool On
    {
        get
        {
            lock (guard) return on;
        }
        set
        {
            lock (guard)
            {
                if (on == value) return;
                on = value;
            }
            Save("switch");
            if (value) Start();
            else Stop();
            Tell();
        }
    }

    /// <summary>How loud the click and the ping are: 0 (silent) to 100. Kept in settings.json.</summary>
    /// <exception cref="ArgumentException">Not from 0 to 100.</exception>
    public int Volume
    {
        get
        {
            lock (guard) return volume;
        }
        set
        {
            if (value is < 0 or > 100) throw new ArgumentException(NoSuchVolume, nameof(value));
            lock (guard)
            {
                if (volume == value) return;
                volume = value;
            }
            Save("volume");
            Tell();
        }
    }

    /// <summary>Plays one of the sounds at the volume set, for the player to hear (the page's "Test sound"). False when the volume is 0: nothing is played.</summary>
    /// <exception cref="ArgumentException">Neither <see cref="NavScanSounds.Ping"/> nor <see cref="NavScanSounds.Click"/>.</exception>
    public bool TestSound(string sound)
    {
        if (sound is not (NavScanSounds.Ping or NavScanSounds.Click)) throw new ArgumentException(NoSuchSound, nameof(sound));
        return Sound(sound);
    }

    void Save(string what)
    {
        if (settings is null) return;
        HeatmapSettings now = settings.Get<HeatmapSettings>(Section);
        lock (guard) (now.NavScan, now.NavScanVolume) = (on, volume);
        try
        {
            settings.Set(Section, now);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Nav scan's {what} could not be kept in settings.json; it holds until Studio is closed", problem);
        }
    }

    bool Sound(string sound)
    {
        int loud;
        lock (guard) loud = volume;
        if (loud <= 0) return false;
        try
        {
            Play(sound, loud);
        }
        catch (Exception problem)
        {
            // A sound is never worth a picture's check.
            Log.Error($"Nav scan's {sound} could not be played", problem);
        }
        return true;
    }

    // ------------------------------------------------------------ watching

    void Look()
    {
        (double X, double Y, double Z)? now = game.StarPos is [var x, var y, var z] ? (x, y, z) : null;
        long address = game.SystemAddress;
        lock (guard) (here, system) = (now, address);
    }

    void Start()
    {
        lock (guard)
        {
            // The real clock, as the files' times are.
            since = DateTime.UtcNow;
            stopping = new CancellationTokenSource();
            looking?.Dispose();
            looking = new Timer(_ => Watch(), null, LookEvery, LookEvery);
        }
        Watch(switchedOn: true);
    }

    void Stop()
    {
        CancellationTokenSource stopped;
        List<FileSystemWatcher> gone;
        lock (guard)
        {
            stopped = stopping;
            gone = [.. watchers.Values];
            watchers.Clear();
            queued.Clear();
            looking?.Dispose();
            looking = null;
            problem = null;
        }
        stopped.Cancel();
        foreach (FileSystemWatcher watcher in gone) watcher.Dispose();
    }

    public void Dispose()
    {
        game.Changed -= Look;
        Stop();
    }

    /// <summary>
    /// Watches the folders pictures come to that exist now, and lets go of those that went. A folder that appears while
    /// Nav scan is on (Steam makes its folder for Elite, and the game its own, with the first picture) is looked through
    /// once for pictures made since it was switched on: the first of them came before the folder could be watched. Not
    /// the folders there as it is switched on (<paramref name="switchedOn"/>): nothing in them is new yet.
    /// </summary>
    void Watch(bool switchedOn = false)
    {
        IReadOnlyList<string> wanted;
        try
        {
            wanted = places();
        }
        catch (Exception problem)
        {
            Log.Error("Nav scan could not work out where screenshots go", problem);
            wanted = [];
        }
        var appeared = new List<string>();
        bool changed = false;
        DateTime from;
        lock (guard)
        {
            if (!on) return;
            from = since;
            foreach (string folder in watchers.Keys.Where(folder => !Directory.Exists(folder)).ToList())
            {
                watchers.Remove(folder, out FileSystemWatcher? gone);
                gone!.Dispose();
                changed = true;
            }
            foreach (string place in wanted.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (watchers.ContainsKey(place) || !Directory.Exists(place)) continue;
                try
                {
                    watchers[place] = Watcher(place);
                    appeared.Add(place);
                    changed = true;
                }
                catch (Exception problem) when (problem is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    Log.Error($"Nav scan could not watch {place}", problem);
                }
            }
            string? now = watchers.Count == 0 ? NoFolder : null;
            changed |= now != problem;
            problem = now;
        }
        foreach (string folder in appeared)
        {
            Log.Info($"Nav scan watches {folder} for new pictures");
            if (switchedOn) continue;
            try
            {
                foreach (FileInfo picture in new DirectoryInfo(folder).EnumerateFiles())
                    if (picture.CreationTimeUtc >= from - Slack) Seen(picture.FullName);
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Nav scan could not look through {folder}", problem);
            }
        }
        if (changed) Tell();
    }

    FileSystemWatcher Watcher(string folder)
    {
        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,   // not Steam's thumbnails
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Created += (_, e) => Seen(e.FullPath);
        watcher.Renamed += (_, e) => Seen(e.FullPath);
        watcher.Changed += (_, e) => Seen(e.FullPath);
        watcher.Error += (_, e) =>
        {
            // Windows lost track (too much at once, or the folder went): the next look watches it afresh.
            Log.Error($"Nav scan lost track of {folder}; it watches it again in a moment", e.GetException());
            lock (guard)
            {
                if (watchers.Remove(folder, out FileSystemWatcher? lost)) lost.Dispose();
            }
        };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    /// <summary>Windows says a file came or changed in a watched folder: offered on the UI thread.</summary>
    void Seen(string path)
    {
        if (context is null) Offer(path);
        else context.Post(_ => Offer(path), null);
    }

    static bool IsPicture(string path) => PictureKinds.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A file that may be a new picture: read after the ones before it, and checked after theirs, unless it is being
    /// read or checked already, Nav scan is off, or it is no picture. Where the commander is now is where it was taken.
    /// </summary>
    internal void Offer(string path)
    {
        if (!IsPicture(path)) return;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception problem) when (problem is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }
        lock (guard)
        {
            if (!on || !queued.Add(full)) return;
            var taken = new Taken(full, here, system, since, stopping.Token);
            Task<(Picture, PictureNames)?> named = ReadAfter(reading, taken);
            reading = named;
            checking = CheckAfter(checking, named, taken);
        }
    }

    /// <summary>A picture offered, with where the commander was when it was taken and what it is read under.</summary>
    /// <param name="System">The game's number for the system the commander was in (0 when not known).</param>
    /// <param name="From">When Nav scan was switched on: a picture made before is not the player's new one.</param>
    /// <param name="Token">Cancelled when Nav scan is switched off.</param>
    sealed record Taken(string Path, (double X, double Y, double Z)? At, long System, DateTime From, CancellationToken Token);

    /// <summary>A picture read after the ones offered before it (each never throws).</summary>
    async Task<(Picture, PictureNames)?> ReadAfter(Task before, Taken taken)
    {
        await before;
        return await ReadOne(taken);
    }

    /// <summary>A picture's names checked once it is read, after the checks of the pictures offered before it (each never throws).</summary>
    async Task CheckAfter(Task before, Task<(Picture, PictureNames)?> named, Taken taken)
    {
        try
        {
            await before;
            if (await named is (Picture picture, PictureNames names)) await CheckOne(picture, names, taken);
        }
        finally
        {
            lock (guard) queued.Remove(taken.Path);
        }
    }

    // ------------------------------------------------------------ one picture

    /// <summary>Mutable under the guard: a picture as it is read and checked.</summary>
    sealed class Picture(string file, DateTime at)
    {
        public string File { get; } = file;
        public DateTime At { get; } = at;
        public string State { get; set; } = Reading;
        public string? Problem { get; set; }
        public int Dropped { get; set; }
        public int Unreadable { get; set; }
        public ScanTally? Tally { get; set; }

        /// <summary>Read, its check waits its turn: behind the check of the picture before, or behind another of the 3D map's requests to Spansh.</summary>
        public bool Waiting { get; set; }

        public NavScanPicture View => new(File, At, Tally?.Entries ?? 0, Tally?.Recorded ?? 0, Tally?.NotRecorded ?? 0, Tally?.Failed ?? 0, Tally?.Unsure ?? 0, Dropped, State)
        {
            Problem = Problem,
            Waiting = State == Checking && Waiting,
            Unreadable = Unreadable,
        };
    }

    /// <summary>
    /// One picture's reading: waited for until it is whole, passed over when it was there before the switch or was read
    /// before, read, the click played, and its names made out. Its check comes after (<see cref="CheckOne"/>), on the
    /// chain of checks. Null when there is nothing to check (passed over, or not read). Never throws: what goes wrong is
    /// logged and said.
    /// </summary>
    async Task<(Picture, PictureNames)?> ReadOne(Taken taken)
    {
        (string path, var at, CancellationToken token) = (taken.Path, taken.At, taken.Token);
        Picture? picture = null;
        try
        {
            if (await Whole(path, token) is not { } file) return null;
            // There before Nav scan was switched on (a picture changed since, or put there from elsewhere): not the player's new one.
            if (file.CreationTimeUtc < taken.From - Slack) return null;
            if (!await Task.Run(() => read.Take(file.FullName, file.Length, file.LastWriteTimeUtc), token)) return null;

            picture = new Picture(file.Name, Now());
            lock (guard)
            {
                pictures.Insert(0, picture);
                if (pictures.Count > MostPictures) pictures.RemoveAt(pictures.Count - 1);
            }
            Tell();
            PictureText text = await Reader(path, token);
            if (text.Problem is { } unread)
            {
                Log.Error($"Nav scan could not read {file.Name}: {unread}");
                Fail(picture, unread);
                return null;
            }
            Sound(NavScanSounds.Click);

            PictureNames names = await Task.Run(() => NavScanNames.Read(text, at), token);
            // Its check waits its turn, behind the check of the picture before if that is not done.
            lock (guard) (picture.State, picture.Waiting, picture.Unreadable) = (Checking, true, names.Unreadable);
            Tell();
            return (picture, names);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (picture is not null) Fail(picture, SwitchedOff);
        }
        catch (Exception unexpected)
        {
            Log.Error($"Nav scan could not read {Path.GetFileName(path)}", unexpected);
            if (picture is not null) Fail(picture, Unexpected);
        }
        return null;
    }

    /// <summary>
    /// One picture's check, its turn come: which systems its names are, then the memory of checks, this PC and Spansh
    /// (whose request may wait its turn behind another of the 3D map's: said on the page and the overlay), and the ping
    /// for a system nobody has recorded. Never throws: what goes wrong is logged and said.
    /// </summary>
    async Task CheckOne(Picture picture, PictureNames names, Taken taken)
    {
        CancellationToken token = taken.Token;
        try
        {
            lock (guard) picture.Waiting = false;
            Tell();
            (IReadOnlyList<ScanEntry> entries, int dropped) = await finds.SortScanned(names, taken.At, taken.System);
            lock (guard) picture.Dropped = dropped;
            ScanTally tally = await finds.CheckScanned(entries, picture.File, now =>
            {
                lock (guard) picture.Tally = now;
                Tell();
            }, token, waiting =>
            {
                lock (guard) picture.Waiting = waiting;
                Tell();
            }, taken.System);
            lock (guard) (picture.Tally, picture.State, picture.Waiting) = (tally, Done, false);
            Log.Info($"Nav scan read {picture.File}: {tally.Entries} names, {tally.Recorded} recorded, {tally.NotRecorded} not recorded"
                + (tally.NotRecordedNames.Count > 0 ? $" ({string.Join(", ", tally.NotRecordedNames)})" : "")
                + $", {tally.Failed} not checked, {dropped} catalogue names dropped, {names.Unreadable} names that could not be read, {tally.Requests} requests to Spansh");
            Tell(tally.New);
            // Only a system nobody has recorded that was not known already: one in the memory of checks or Your finds stays
            // quiet, and so does the system the commander is in.
            if (tally.New.Count > 0) Sound(NavScanSounds.Ping);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Fail(picture, SwitchedOff);
        }
        catch (Exception unexpected)
        {
            Log.Error($"Nav scan could not check the names read off {picture.File}", unexpected);
            Fail(picture, Unexpected);
        }
    }

    void Fail(Picture picture, string why)
    {
        lock (guard) (picture.State, picture.Problem) = (Failed, why);
        Tell();
    }

    /// <summary>
    /// The picture once it is whole: its size the same at two looks, and nobody writing it (it opens for reading with
    /// writers shut out). Null when it went (a file Steam renamed or took away) or never settled.
    /// </summary>
    async Task<FileInfo?> Whole(string path, CancellationToken token)
    {
        long size = -1;
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            if (file.Length > 0 && file.Length == size && Opens(path))
            {
                file.Refresh();
                return file;
            }
            if (waited.Elapsed > GiveUp)
            {
                Log.Error($"Nav scan gave up waiting for {file.Name} to be written");
                return null;
            }
            size = file.Length;
            await Task.Delay(SettleWait, token);
        }

        static bool Opens(string path)
        {
            try
            {
                using var reading = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return true;
            }
            catch (Exception held) when (held is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    // ------------------------------------------------------------ what the page and the overlay are given

    /// <summary>Nav scan as it stands.</summary>
    public NavScanView View()
    {
        lock (guard) return ViewLocked();
    }

    NavScanView ViewLocked()
    {
        NavScanPicture[] shown = [.. pictures.Select(picture => picture.View)];
        Picture? going = pictures.FirstOrDefault(picture => picture.State is Reading or Checking);
        string state = !on ? Off : going?.State ?? Watching;
        string[] folders = on ? [.. watchers.Keys.Order(StringComparer.OrdinalIgnoreCase)] : [];
        return new NavScanView(on, volume, folders, shown, [.. pictures.SelectMany(picture => picture.Tally?.Names ?? [])], on ? problem : null)
        {
            State = state,
            Line = LineOf(on, folders.Length, shown.FirstOrDefault(), pictures.FirstOrDefault()?.Tally?.NotRecordedNames ?? []),
        };
    }

    /// <summary>Tells the page and the overlay how Nav scan stands, with the names just found not recorded.</summary>
    void Tell(IReadOnlyList<string>? found = null)
    {
        NavScanNews news;
        lock (guard)
        {
            NavScanView view = ViewLocked();
            news = new NavScanNews(view.State, view.On, view.Volume, view.Watching, view.Pictures.FirstOrDefault(), view.Problem, view.Line)
            {
                Found = found ?? [],
                Checked = found is not null,
            };
        }
        News?.Invoke(news);
    }

    /// <summary>
    /// The overlay's line of Nav scan, after its label: while a picture is read, "reading…" (dim); while its check waits
    /// its turn, "picture read · waiting its turn at Spansh…" (dim: the pilot knows the screenshot was taken, and need not
    /// take it again); once it is checked, "picture read · 18 names · 17 recorded · 1 not recorded: …", gold when a name
    /// is not recorded (naming the first three), green when all are, plain when some could not be checked or a name on
    /// the picture could not be read ("1 name could not be read"). Before the first picture it says how to take one.
    /// Null while Nav scan is off.
    /// </summary>
    /// <param name="watching">Folders watched: none means none was found yet.</param>
    /// <param name="latest">The newest picture; null before the first.</param>
    /// <param name="notRecorded">The names of that picture found not recorded, as the overlay names them.</param>
    public static NavScanLine? LineOf(bool on, int watching, NavScanPicture? latest, IReadOnlyList<string> notRecorded)
    {
        if (!on) return null;
        if (latest is null)
            return watching > 0
                ? new NavScanLine("waiting for a screenshot of the Navigation panel (F12 in Steam, F10 in the game)", RouteCheckLine.Dim)
                : new NavScanLine("no screenshot folder found yet: take a screenshot (F12 in Steam, F10 in the game)", RouteCheckLine.Plain);
        string names = latest.Names == 1 ? "1 name" : $"{Whole(latest.Names)} names";
        switch (latest.State)
        {
            case Reading:
                return new NavScanLine("reading…", RouteCheckLine.Dim);
            case Checking when latest.Waiting:
                return new NavScanLine($"picture read{LineDot}waiting its turn at Spansh…", RouteCheckLine.Dim);
            case Checking:
                return new NavScanLine($"picture read{LineDot}{names}{LineDot}checking…", RouteCheckLine.Dim);
            case Failed:
                return new NavScanLine($"picture not read: {latest.Problem}", RouteCheckLine.Plain);
        }
        // A name the reader could not make out was not checked: said, so that the pilot may take the picture again.
        string unread = latest.Unreadable switch
        {
            0 => "",
            1 => "1 name could not be read",
            _ => $"{Whole(latest.Unreadable)} names could not be read",
        };
        if (latest.Names == 0) return new NavScanLine($"picture read{LineDot}{(unread.Length > 0 ? unread : "no system names found")}", RouteCheckLine.Plain);
        string recorded = latest.Recorded > 0 ? $"{LineDot}{Whole(latest.Recorded)} recorded" : "";
        string missing = latest.NotRecorded > 0 ? $"{LineDot}{Whole(latest.NotRecorded)} not recorded: {Shown(notRecorded)}" : "";
        string failed = latest.Failed > 0 ? $"{LineDot}{Whole(latest.Failed)} not checked" : "";
        string notRead = unread.Length > 0 ? LineDot + unread : "";
        if (missing.Length == 0 && failed.Length == 0 && notRead.Length == 0) return new NavScanLine($"picture read{LineDot}{names}{LineDot}all recorded", RouteCheckLine.Green);
        return new NavScanLine($"picture read{LineDot}{names}{recorded}{missing}{failed}{notRead}", latest.NotRecorded > 0 ? RouteCheckLine.Gold : RouteCheckLine.Plain);

        static string Whole(int count) => count.ToString("N0", CultureInfo.InvariantCulture);

        // The first three by name, so the line stays one line.
        static string Shown(IReadOnlyList<string> all) => string.Join(", ", all.Take(3)) + (all.Count > 3 ? $" and {Whole(all.Count - 3)} more" : "");
    }

    // ------------------------------------------------------------ where pictures go

    /// <summary>
    /// The folders Elite's pictures go to, there or not yet: Steam's screenshots of the game for each Steam user of this
    /// PC (Steam's folder as the registry gives it, else where Steam installs itself), and the game's own (F10).
    /// </summary>
    internal static IReadOnlyList<string> PicturePlaces()
    {
        var places = new List<string>();
        foreach (string steam in SteamFolders())
        {
            string users = Path.Combine(steam, "userdata");
            if (!Directory.Exists(users)) continue;
            try
            {
                foreach (string user in Directory.EnumerateDirectories(users)) places.Add(Path.Combine(user, "760", "remote", EliteOnSteam, "screenshots"));
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Nav scan could not look through {users}", problem);
            }
        }
        string mine = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (mine.Length > 0) places.Add(Path.Combine(mine, "Frontier Developments", "Elite Dangerous"));
        return places;
    }

    /// <summary>Steam's folder as the registry says (written with forward slashes), and where Steam installs itself by default.</summary>
    static List<string> SteamFolders()
    {
        var folders = new List<string>();
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string { Length: > 0 } steam)
                folders.Add(Path.GetFullPath(steam.Replace('/', '\\')));
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            Log.Error("Nav scan could not read where Steam is from the registry", problem);
        }
        const string usual = @"C:\Program Files (x86)\Steam";
        if (!folders.Contains(usual, StringComparer.OrdinalIgnoreCase)) folders.Add(usual);
        return folders;
    }

    // ------------------------------------------------------------ the pictures read

    /// <summary>
    /// The pictures read, each by its path, size and last write (a picture written again is a new picture), for
    /// <see cref="ReadKept"/>: in heatmap\navscan-read.json, written beside its name and then moved there. A file that is
    /// not a list of pictures is moved aside, never written over; one another program holds is read again next time.
    /// </summary>
    sealed class ReadPictures(string file, Func<DateTime> now)
    {
        sealed record Seen(string File, long Bytes, DateTime Written, DateTime At);

        static readonly JsonSerializerOptions FileJson = new(Settings.Json) { WriteIndented = false };

        readonly Lock guard = new();
        readonly List<Seen> waiting = [];   // read while the file could not be
        List<Seen>? list;

        /// <summary>True when the picture was never read: it is noted as read now (and written). False when it was.</summary>
        public bool Take(string path, long bytes, DateTime written)
        {
            lock (guard)
            {
                List<Seen> seen = Load() ?? waiting;
                DateTime clock = now();
                seen.RemoveAll(picture => clock - picture.At > ReadKept);
                if (seen.Any(picture => picture.File.Equals(path, StringComparison.OrdinalIgnoreCase) && picture.Bytes == bytes && picture.Written == written)) return false;
                seen.Add(new Seen(path, bytes, written, clock));
                if (list is not null) Save(list);
                return true;
            }
        }

        /// <summary>The file, read the first time it can be, with the pictures read meanwhile. Null while it cannot be read.</summary>
        List<Seen>? Load()
        {
            if (list is not null) return list;
            List<Seen> got;
            try
            {
                got = File.Exists(file)
                    ? [.. (JsonSerializer.Deserialize<Seen?[]>(File.ReadAllText(file), FileJson) ?? throw new JsonException("The file holds no list")).OfType<Seen>().Where(picture => picture.File is { Length: > 0 })]
                    : [];
            }
            catch (JsonException problem)
            {
                string aside = file + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                try
                {
                    File.Move(file, aside);
                }
                catch (Exception held) when (held is IOException or UnauthorizedAccessException)
                {
                    Log.Error("Nav scan's list of pictures read could not be read, nor moved aside; it is left as it is", held);
                    return null;
                }
                Log.Error($"Nav scan's list of pictures read could not be read; the file was kept as {Path.GetFileName(aside)} and the list starts afresh", problem);
                got = [];
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                Log.Error("Nav scan's list of pictures read could not be read just now; it is read again next time, and nothing is written over it meanwhile", problem);
                return null;
            }
            got.AddRange(waiting);
            waiting.Clear();
            return list = got;
        }

        void Save(List<Seen> seen)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(seen, FileJson));
                File.Move(file + ".tmp", file, overwrite: true);
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                Log.Error("Nav scan's list of pictures read could not be kept on disk; it holds until Studio is closed", problem);
            }
        }
    }
}
