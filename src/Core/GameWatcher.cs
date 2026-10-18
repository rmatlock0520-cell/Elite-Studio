using System.Text;
using System.Text.Json;
using System.Xml;
using EliteStudio.App;

namespace EliteStudio.Core;

/// <summary>
/// Reads the game's files and keeps <see cref="State"/> current. The app calls <see cref="Poll"/>
/// about twice a second on the UI thread; each call reads only what was added since the last one.
/// The first call replays the newest journal file so the state is right even if Elite is not running.
/// </summary>
/// <param name="gameDir">Finds the game's install folder, where the control presets that ship with the game are; without it only the player's own binding files are read.</param>
public sealed class GameWatcher(string journalDir, string bindingsDir, Func<string?>? gameDir = null) : IDisposable
{
    internal const FileShare GameMayWrite = FileShare.ReadWrite | FileShare.Delete;   // the game keeps its files open

    readonly WatchedFile status = new(Path.Combine(journalDir, "Status.json"));
    readonly WatchedFile cargo = new(Path.Combine(journalDir, "Cargo.json"));
    readonly WatchedFile market = new(Path.Combine(journalDir, "Market.json"));
    readonly WatchedFile route = new(Path.Combine(journalDir, "NavRoute.json"));
    // the game keeps its options in sibling folders: Options\Bindings, Options\Graphics
    readonly WatchedFile display = new(Path.Combine(bindingsDir, "..", "Graphics", "DisplaySettings.xml"));

    // journals not being followed: how far into each the reading got, should it become the newest again
    readonly Dictionary<string, long> leftAt = new(StringComparer.OrdinalIgnoreCase);

    string? journal;            // the file being followed
    long position;              // bytes of it already taken in
    byte[] unfinished = [];     // the start of a line whose end the game has not written yet
    bool replaying = true;      // still catching up with what was in the first file found
    bool problemLogged;         // one log entry per journal file is enough to know it has bad lines
    string bindingsSeen = "";   // the bindings folder (names, times, sizes) as it was when last read in full
    string bindingsBroken = ""; // ... and when its XML last could not be read, so that is logged once, not every Poll

    // The search of the older journals for technology-broker unlocks, while it runs on a background thread.
    readonly CancellationTokenSource stopSearch = new();
    Task<(List<string> Unlocked, bool AllRead)>? search;
    DateTime searchTo;          // once it has finished, every journal last written before this has been searched

    public GameState State { get; } = new();

    public void Poll()
    {
        bool changed = false;
        if (search is { IsCompleted: true } finished)
        {
            search = null;
            (List<string> found, bool allRead) = finished.Result;
            // A journal that could not be read is searched again at the next start: the time does not move on.
            State.AddUnlocks(found, allRead ? searchTo : default);
            changed = true;
        }
        changed |= ReadJournal();

        // In flight the game rewrites Status.json about once a second (the fuel gauge alone sees to that).
        // Only what the snapshot shows of it counts as a change: every change is told to all the pages and
        // draws the overlay again, and hauling sends its whole trade list with it.
        (string vehicle, int screen) = (State.Status.Mode, State.Status.GuiFocus);
        ReadJson(status, e => State.SetStatus(GameStatus.Parse(e)));
        changed |= (State.Status.Mode, State.Status.GuiFocus) != (vehicle, screen);
        changed |= ReadJson(cargo, e => { var (tons, items, ship) = CargoItem.ParseHold(e); State.SetCargo(tons, items, ship); });
        changed |= ReadJson(market, e => State.SetMarket(StationMarket.Parse(e)));
        changed |= ReadJson(route, e => State.SetRoute(RouteStop.ParseRoute(e)));
        if (display.Changed(out string settings))
        {
            if (EliteWindow.ParseDisplayMode(settings) is { } mode)
            {
                State.SetDisplayMode(mode);
                changed = true;
            }
            else display.ReadAgain();   // caught half-written: the mode known so far stands
        }
        changed |= ReadBindings();

        bool running = EliteWindow.Running;
        changed |= running != State.GameRunning;
        State.GameRunning = running;

        if (changed) State.RaiseChanged();
    }

    public void Dispose()
    {
        // The search may still be reading: tell it to stop and wait for it, so no journal is left open behind this.
        stopSearch.Cancel();
        search?.Wait();
        stopSearch.Dispose();
    }

    bool ReadJournal()
    {
        FileInfo[] journals;
        try
        {
            // Newest first by time written, as the Haul Overlay chose: file names are not a safe order
            // (the game has used two naming styles). The name only settles a tie.
            journals = [.. new DirectoryInfo(journalDir).EnumerateFiles("Journal.*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal)];
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            return false;   // no journal folder yet: the game makes it on its first run
        }
        if (journals.Length == 0) return false;

        bool read = false;
        if (!journals[0].FullName.Equals(journal, StringComparison.OrdinalIgnoreCase))
        {
            if (journal is null)
            {
                // Older files are history and are not replayed. Noting how long each is means that if
                // something touches one later (a backup tool, an editor), nothing in it is taken for news.
                foreach (FileInfo older in journals.Skip(1)) leftAt[older.FullName] = older.Length;
                ReadOlderPurchases(journals.Skip(1).Take(5));
                SearchOlderJournals(journals);
            }
            else
            {
                read = ReadAppended();   // the last lines of the file the game just left
                leftAt[journal] = position - unfinished.Length;
            }
            journal = journals[0].FullName;
            position = leftAt.GetValueOrDefault(journal);   // 0 for a file the game has just started
            unfinished = [];
            problemLogged = false;
            Log.Info($"Reading journal {journals[0].Name}");
        }
        return ReadAppended() | read;
    }

    /// <summary>Takes in the bytes added to the journal since last time. False when there were none.</summary>
    bool ReadAppended()
    {
        byte[] added;
        try
        {
            using var stream = new FileStream(journal!, FileMode.Open, FileAccess.Read, GameMayWrite);
            if (stream.Length < position)
            {
                // The file got shorter, so it is not the file that was being followed: start over.
                position = 0;
                unfinished = [];
                replaying = true;
            }
            added = new byte[stream.Length - position];
            stream.Seek(position, SeekOrigin.Begin);
            stream.ReadExactly(added);
            position += added.Length;
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            return false;   // try again next Poll
        }

        // Lines are cut out of the raw bytes and only whole ones are decoded, so neither a line nor
        // a multi-byte character can be split by where the game happened to stop writing.
        bool any = false;
        if (added.Length > 0)
        {
            byte[] bytes = unfinished.Length == 0 ? added : [.. unfinished, .. added];
            int start = 0;
            for (int end; (end = Array.IndexOf(bytes, (byte)'\n', start)) >= 0; start = end + 1)
                any |= ApplyLine(bytes.AsMemory(start, end - start));
            unfinished = bytes[start..];
        }
        replaying = false;   // the end of the file has been reached: whatever comes next is happening now
        return any;
    }

    bool ApplyLine(ReadOnlyMemory<byte> line)
    {
        if (line.Span.Trim(" \t\r"u8).IsEmpty) return false;
        try
        {
            JsonElement e;
            using (JsonDocument parsed = JsonDocument.Parse(line))
                e = parsed.RootElement.Clone();   // studios may keep the line after this call returns
            if (e.Text("event").Length == 0) throw new JsonException("no event name");
            State.Apply(e, replaying);
            return true;
        }
        catch (Exception problem)
        {
            // A broken line must not stop the lines after it. Not only JsonException: bytes that are
            // not UTF-8 inside a string pass the parser and only fail when that field is taken as
            // text, while the state is being updated. (A studio that fails on a line never gets
            // here: GameState.Apply deals with each listener on its own.)
            if (!problemLogged) Log.Error($"{Path.GetFileName(journal)}: a journal line could not be taken in (only the first one in a file is logged)", problem);
            problemLogged = true;
            return false;
        }
    }

    /// <summary>
    /// Prices paid before the newest journal began: the last purchase of each commodity in the few
    /// files before it. Without this, cargo bought in an earlier session has no known price.
    /// </summary>
    void ReadOlderPurchases(IEnumerable<FileInfo> newestFirst)
    {
        foreach (FileInfo file in newestFirst.Reverse())   // oldest first, so a newer purchase replaces an older one
        {
            try
            {
                using var reader = new StreamReader(new FileStream(file.FullName, FileMode.Open, FileAccess.Read, GameMayWrite), Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    if (!line.Contains("\"event\":\"MarketBuy\"", StringComparison.Ordinal)) continue;
                    using JsonDocument parsed = JsonDocument.Parse(line);
                    State.NotePaid(parsed.RootElement);
                }
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or JsonException)
            {
                Log.Error($"{file.Name}: could not be searched for prices paid", problem);
            }
        }
    }

    /// <summary>
    /// What the commander unlocked at a technology broker stays unlocked for good, and the one line
    /// that says so can sit in a journal years old. So every older journal not searched by an
    /// earlier run of Studio (<see cref="GameState.UnlocksReadTo"/>) is read once, on a background
    /// thread: a long-time player has thousands of them, and neither the start-up nor the UI
    /// thread waits for that. <see cref="Poll"/> takes the answer in when it is there. The newest
    /// journal is left out: its lines are replayed anyway.
    /// </summary>
    void SearchOlderJournals(FileInfo[] newestFirst)
    {
        DateTime readTo = State.UnlocksReadTo;
        // "Not before" rather than "after": the journal that was the newest at that time may have been written on after Studio closed.
        string[] files = [.. newestFirst.Skip(1).Where(file => file.LastWriteTimeUtc >= readTo).Select(file => file.FullName)];
        searchTo = newestFirst[0].LastWriteTimeUtc;
        if (files.Length == 0)
        {
            State.AddUnlocks([], searchTo);   // nothing older is new: the usual start
            return;
        }
        CancellationToken stop = stopSearch.Token;
        search = Task.Run(() => UnlocksIn(files, stop));
    }

    /// <summary>The background search itself. Touches nothing but the files, so it needs no care about threads.</summary>
    static (List<string> Unlocked, bool AllRead) UnlocksIn(string[] files, CancellationToken stop)
    {
        var unlocked = new List<string>();
        bool allRead = true;
        foreach (string file in files)
        {
            try
            {
                using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, GameMayWrite), Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    if (stop.IsCancellationRequested) return (unlocked, false);
                    if (!line.Contains("\"event\":\"TechnologyBroker\"", StringComparison.Ordinal)) continue;
                    try
                    {
                        using JsonDocument parsed = JsonDocument.Parse(line);
                        unlocked.AddRange(GameState.UnlockedBy(parsed.RootElement));
                    }
                    catch (JsonException)
                    {
                        // a line cut short when the game stopped: there is nothing more to be had from it, now or later
                    }
                }
            }
            catch (Exception problem)
            {
                // Whatever it is (the file is locked or gone, most likely), the other journals are still searched.
                Log.Error($"{Path.GetFileName(file)}: could not be searched for technology-broker unlocks; it is tried again at the next start", problem);
                allRead = false;
            }
        }
        return (unlocked, allRead);
    }

    /// <summary>Re-reads one of the game's small JSON files when it changed. False when there was nothing new.</summary>
    static bool ReadJson(WatchedFile file, Action<JsonElement> take)
    {
        if (!file.Changed(out string text)) return false;
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(text);
            take(parsed.RootElement);
            return true;
        }
        catch (JsonException)
        {
            file.ReadAgain();   // caught half-written
            return false;
        }
    }

    bool ReadBindings()
    {
        // Few and small files: their names, times and sizes together say whether anything changed.
        string seen = string.Join('|', KeyBindings.Files(bindingsDir).Select(f => $"{f.Name}:{f.LastWriteTimeUtc.Ticks}:{f.Length}"));
        if (seen == bindingsSeen) return false;
        try
        {
            State.SetBindings(KeyBindings.Load(bindingsDir, gameDir));
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or XmlException)
        {
            // Locked or half-written, most likely by the game this moment. The keys known so far
            // stand (dropping them would call every key free) and the next Poll tries again, quietly:
            // only XML that cannot be read is worth a log entry, in case it stays that way.
            if (problem is XmlException && seen != bindingsBroken)
            {
                Log.Error("Key bindings could not be read; the keys known so far are kept", problem);
                bindingsBroken = seen;
            }
            return false;
        }
        bindingsSeen = seen;
        return true;
    }

    /// <summary>
    /// One small file the game rewrites in place. Remembers the time and size it had when last read,
    /// so it is opened again only after the game wrote it again.
    /// </summary>
    sealed class WatchedFile(string path)
    {
        DateTime readTime;
        long readSize = -1;

        /// <summary>True, with the file's text, when the file is new or was rewritten since the last read.</summary>
        public bool Changed(out string text)
        {
            text = "";
            var file = new FileInfo(path);
            if (!file.Exists || (file.LastWriteTimeUtc == readTime && file.Length == readSize)) return false;
            try
            {
                using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, GameMayWrite), Encoding.UTF8);
                text = reader.ReadToEnd();
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                return false;   // the game is writing it this moment: quietly try again next Poll
            }
            (readTime, readSize) = (file.LastWriteTimeUtc, file.Length);
            return true;
        }

        /// <summary>Forgets the last read, so the next Poll reads the file again whether it changed or not.</summary>
        public void ReadAgain() => readSize = -1;
    }
}
