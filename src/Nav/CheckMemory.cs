using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EliteStudio.App;

namespace EliteStudio.Nav;

/// <summary>One answer to "is this system recorded?", as <see cref="CheckMemory"/> keeps it.</summary>
/// <param name="Name">The system's name as it was checked.</param>
/// <param name="Id64">
/// The game's own number for the system; 0 when it was checked by its name alone. Written as text, because these
/// numbers can be larger than a web page can hold exactly.
/// </param>
/// <param name="Result"><see cref="CheckMemory.Recorded"/>, <see cref="CheckMemory.NotRecorded"/> or <see cref="CheckMemory.Failed"/> (it could not be checked).</param>
/// <param name="At">When it was checked (UTC).</param>
/// <param name="By">
/// Where the answer came from: on this PC <see cref="CheckMemory.ByDetailed"/>, <see cref="CheckMemory.BySurvey"/> or
/// <see cref="CheckMemory.ByFinds"/>; else <see cref="CheckMemory.BySpansh"/>. Null for a check that failed before
/// anything could be asked (a fault in Studio itself, a name too short to ask about).
/// </param>
/// <param name="How">What was found, in a few words ("found by Spansh's system search"); null for a check that failed.</param>
/// <param name="Reason">Why it could not be checked, in the player's words; null otherwise.</param>
public sealed record CheckEntry(
    string Name,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Id64,
    string Result, DateTime At, string? By, string? How, string? Reason);

/// <summary>
/// The 3D map's one memory of "is this system recorded?": the answers of the target check, the route check, Within
/// reach and Nav scan, so that a system one of them has settled is not asked about again by another (the owner,
/// 15 October 2026). Every check asks this memory first, then what is kept on this PC (the detailed map, the surveys,
/// Your finds), and only then Spansh.
///
/// An answer is kept as long as it is likely to hold: a system found recorded for <see cref="RecordedDays"/> (1 day
/// unless the player chose 7 or 30: a recorded system stays recorded, but a check now and then costs little), one not
/// recorded for <see cref="NotRecordedKept"/> (somebody may record it any day, the player included, so the next
/// sighting after that checks it once more), and one that could not be checked for <see cref="FailedKept"/> (long
/// enough that a route full of stops Spansh could not answer is not asked about again and again, short enough that a
/// hiccup is soon forgotten).
///
/// A system is known by its number (id64, as text) when the game or a list gave it, else by its name without regard to
/// capitals or spacing; one checked by its name is found by its number too once the two have been seen together.
///
/// Kept in heatmap\checks.json, written beside its name and then moved there, so a crash mid-write never leaves half
/// a file; written away from the calling thread (<see cref="SaveAsync"/>). A file that is not a list of answers is
/// moved aside for the owner to send in, never written over, and the memory starts afresh; one another program holds
/// just now is read again later, and nothing is written over it meanwhile (what is learnt meanwhile is held in memory).
/// </summary>
public sealed class CheckMemory
{
    /// <summary>The memory's file in the 3D map's folder.</summary>
    public const string FileName = "checks.json";

    /// <summary>What <see cref="CheckEntry.Result"/> says.</summary>
    public const string Recorded = "recorded", NotRecorded = "not-recorded", Failed = "failed";

    /// <summary>What <see cref="CheckEntry.By"/> says.</summary>
    public const string ByDetailed = "detailed map", BySurvey = "survey", ByFinds = "finds", BySpansh = "spansh";

    /// <summary>The days a recorded system may be kept for, as the page offers them.</summary>
    public static readonly IReadOnlyList<int> DaysOffered = [1, 7, 30];

    /// <summary>How long a system found not recorded is kept: then the next sighting checks it once more.</summary>
    public static readonly TimeSpan NotRecordedKept = TimeSpan.FromHours(24);

    /// <summary>How long a system that could not be checked is not asked about again.</summary>
    public static readonly TimeSpan FailedKept = TimeSpan.FromMinutes(10);

    /// <summary>Why a number is no <see cref="RecordedDays"/>, in the player's words.</summary>
    public const string NoSuchDays = "Studio keeps a recorded system for 1, 7 or 30 days.";

    // One line per answer, so the file stays small; the rest of Studio's style otherwise.
    static readonly JsonSerializerOptions FileJson = new(Settings.Json) { WriteIndented = false };

    readonly string file;
    readonly Lock guard = new();     // the answers, which checks on several threads touch
    readonly Lock disk = new();      // one write of the file at a time
    readonly Dictionary<string, CheckEntry> known = new(StringComparer.Ordinal);   // by key (Key)
    readonly Dictionary<string, string> named = new(StringComparer.Ordinal);       // a name (Canonical) to the key it is kept under
    bool loaded;                     // the file was read (or there was none): it may be written
    bool dirty;                      // answers not written yet
    DateTime retryAfter;             // a file held by another program is not tried again before this
    DateTime forgotten;              // answers from before this were forgotten by the player (a file read later brings none of them back)
    int recordedDays = 1;

    /// <param name="folder">
    /// The 3D map's folder in Studio's data folder; made when the first answer is written. The file is read the first
    /// time the memory is asked (it holds a few days' answers at most: a moment's reading).
    /// </param>
    public CheckMemory(string folder) => file = Path.Combine(folder, FileName);

    /// <summary>The clock (UTC); tests move it on.</summary>
    internal Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    /// <summary>How long a file another program holds is left alone before it is tried again (every check asks the memory); tests wait none.</summary>
    internal TimeSpan HeldWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Days a recorded system is kept: 1 (the owner's choice of 15 October 2026), 7 or 30 (the setting "recordedDays"
    /// in settings.json, kept by <see cref="HeatmapFinds.RecordedDays"/>). It applies to the answers kept already too.
    /// </summary>
    /// <exception cref="ArgumentException">Not 1, 7 or 30.</exception>
    public int RecordedDays
    {
        get
        {
            lock (guard) return recordedDays;
        }
        set
        {
            if (!DaysOffered.Contains(value)) throw new ArgumentException(NoSuchDays, nameof(value));
            lock (guard) recordedDays = value;
        }
    }

    /// <summary>The answers kept and not yet out of date.</summary>
    public int Count
    {
        get
        {
            lock (guard)
            {
                Load();
                DateTime now = Now();
                return known.Values.Count(entry => Fresh(entry, now));
            }
        }
    }

    /// <summary>
    /// The answer kept for a system, by its number when it is given (else, or when nothing is kept under the number, by
    /// its name); null when there is none, or the one kept is out of date. An answer kept under the same name for
    /// another number is another system's.
    /// </summary>
    public CheckEntry? Get(string? name, long id64)
    {
        lock (guard)
        {
            Load();
            CheckEntry? entry = Find(name, id64);
            return entry is not null && Fresh(entry, Now()) ? entry : null;
        }
    }

    /// <summary>
    /// Keeps an answer, in place of any kept for the system before. Written to the file by the next
    /// <see cref="SaveAsync"/>: a check that settles many systems at once writes the file once.
    /// </summary>
    /// <param name="result"><see cref="Recorded"/>, <see cref="NotRecorded"/> or <see cref="Failed"/>.</param>
    /// <exception cref="ArgumentException">No name, or no such result.</exception>
    public CheckEntry Put(string name, long id64, string result, string? by, string? how, string? reason = null)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A system needs a name.", nameof(name));
        if (result is not (Recorded or NotRecorded or Failed)) throw new ArgumentException("A check's result is recorded, not-recorded or failed.", nameof(result));
        lock (guard)
        {
            Load();
            var entry = new CheckEntry(name, Math.Max(0, id64), result, Now(), by, how, reason);
            Keep(entry);
            dirty = true;
            return entry;
        }
    }

    /// <summary>
    /// "Forget all checks": every answer goes, so every system is checked afresh. Gives how many there were (those not
    /// out of date). Written to the file by the next <see cref="SaveAsync"/>.
    /// </summary>
    public int Forget()
    {
        lock (guard)
        {
            Load();
            DateTime now = Now();
            int count = known.Values.Count(entry => Fresh(entry, now));
            known.Clear();
            named.Clear();
            forgotten = now;
            dirty = true;
            return count;
        }
    }

    /// <summary>Writes what changed to the file, away from the calling thread. Nothing goes wrong outside it: a file that cannot be written is said in the log, and tried again by the next save.</summary>
    public Task SaveAsync() => Task.Run(Save);

    /// <summary>
    /// Writes the answers kept to the file when any changed, the out-of-date ones left out: beside the file and then
    /// moved into its place. Not while a file that could not be read is there: it is never written over.
    /// </summary>
    internal void Save()
    {
        lock (disk)
        {
            CheckEntry[] all;
            lock (guard)
            {
                Load();
                if (!loaded || !dirty) return;
                DateTime now = Now();
                foreach (string key in known.Where(pair => !Fresh(pair.Value, now)).Select(pair => pair.Key).ToList()) Drop(key);
                all = [.. known.Values.OrderBy(entry => entry.At).ThenBy(entry => entry.Name, StringComparer.Ordinal)];
                dirty = false;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(all, FileJson));
                File.Move(file + ".tmp", file, overwrite: true);
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                Log.Error("The 3D map's memory of checked systems could not be kept on disk; it holds until Studio is closed, and is written again with the next check", problem);
                lock (guard) dirty = true;
            }
        }
    }

    // ------------------------------------------------------------ keys

    /// <summary>A name as the memory compares it: no spaces at the ends, one between words, capitals made all the same.</summary>
    internal static string Canonical(string? name) =>
        string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    /// <summary>The key a system is kept under: its number as text, else its name (<see cref="Canonical"/>), marked so the two never meet.</summary>
    static string Key(string name, long id64) => id64 > 0 ? id64.ToString(CultureInfo.InvariantCulture) : "name:" + Canonical(name);

    /// <summary>The answer kept for a system, out of date or not. Call under the guard.</summary>
    CheckEntry? Find(string? name, long id64)
    {
        if (id64 > 0 && known.TryGetValue(Key("", id64), out CheckEntry? numbered)) return numbered;
        string canonical = Canonical(name);
        if (canonical.Length == 0 || !named.TryGetValue(canonical, out string? key) || !known.TryGetValue(key, out CheckEntry? byName)) return null;
        // The same name with another number is another system (two systems can share a name).
        return id64 > 0 && byName.Id64 > 0 && byName.Id64 != id64 ? null : byName;
    }

    /// <summary>
    /// Keeps an answer under its key, in place of the one kept for the system before. A system checked by its name
    /// alone and then by its number is kept once, under its number; one checked by its name after its number was
    /// seen keeps the number. Call under the guard.
    /// </summary>
    void Keep(CheckEntry entry)
    {
        string canonical = Canonical(entry.Name);
        if (entry.Id64 <= 0 && named.TryGetValue(canonical, out string? kept) && known.TryGetValue(kept, out CheckEntry? before))
            entry = entry with { Id64 = before.Id64 };
        if (entry.Id64 > 0 && named.TryGetValue(canonical, out string? byName) && byName == Key(canonical, 0)) Drop(byName);
        string key = Key(entry.Name, entry.Id64);
        if (known.TryGetValue(key, out CheckEntry? replaced) && Canonical(replaced.Name) != canonical && named.GetValueOrDefault(Canonical(replaced.Name)) == key)
            named.Remove(Canonical(replaced.Name));
        known[key] = entry;
        named[canonical] = key;
    }

    /// <summary>Takes an answer out, with its name. Call under the guard.</summary>
    void Drop(string key)
    {
        if (!known.Remove(key, out CheckEntry? gone)) return;
        string canonical = Canonical(gone.Name);
        if (named.GetValueOrDefault(canonical) == key) named.Remove(canonical);
    }

    /// <summary>The answer still holds: it is younger than its kind is kept (a time ahead of the clock counts as out of date once it is as far ahead).</summary>
    bool Fresh(CheckEntry entry, DateTime now)
    {
        TimeSpan kept = entry.Result switch
        {
            Recorded => TimeSpan.FromDays(recordedDays),
            NotRecorded => NotRecordedKept,
            _ => FailedKept,
        };
        TimeSpan age = now - entry.At;
        return age < kept && age > -kept;
    }

    // ------------------------------------------------------------ the file

    /// <summary>
    /// Reads the file the first time it can be (call under the guard), taking in its answers that still hold beside
    /// those learnt meanwhile (the newer of two for one system wins). False while it cannot be read.
    /// </summary>
    bool Load()
    {
        if (loaded) return true;
        DateTime clock = DateTime.UtcNow;
        if (clock < retryAfter) return false;
        CheckEntry?[] read;
        try
        {
            read = File.Exists(file)
                ? JsonSerializer.Deserialize<CheckEntry?[]>(File.ReadAllText(file), FileJson) ?? throw new JsonException("The file holds no list")
                : [];
        }
        catch (JsonException problem)
        {
            string aside = file + ".unreadable-" + clock.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            try
            {
                File.Move(file, aside);
            }
            catch (Exception held) when (held is IOException or UnauthorizedAccessException)
            {
                Log.Error("The 3D map's memory of checked systems could not be read, nor moved aside; it is left as it is, and tried again in a moment", held);
                retryAfter = clock + HeldWait;
                return false;
            }
            Log.Error($"The 3D map's memory of checked systems could not be read; the file was kept as {Path.GetFileName(aside)} and the memory starts afresh", problem);
            read = [];
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            if (retryAfter == default) Log.Error("The 3D map's memory of checked systems could not be read just now; it is read again in a moment, and nothing is written over it meanwhile", problem);
            retryAfter = clock + HeldWait;
            return false;
        }
        loaded = true;
        DateTime now = Now();
        foreach (CheckEntry? entry in read)
        {
            // An answer typed into the file by hand that Studio cannot use is passed over.
            if (entry is not { Name.Length: > 0, Result: Recorded or NotRecorded or Failed } || entry.At <= forgotten || !Fresh(entry, now)) continue;
            CheckEntry? newer = Find(entry.Name, entry.Id64);
            if (newer is not null && newer.At >= entry.At) continue;
            Keep(entry with { Id64 = Math.Max(0, entry.Id64) });
        }
        // What was learnt (or forgotten) while the file could not be read is written with the next save: Put and
        // Forget marked it so.
        return true;
    }
}
