using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Nav;

/// <summary>A star system where the commander was the first to discover something (<see cref="FirstsLog"/>).</summary>
/// <param name="Name">The system's name as the game wrote it.</param>
/// <param name="Id64">
/// The game's own number for the system (its "SystemAddress"). Written as text, because these
/// numbers can be larger than a web page can hold exactly.
/// </param>
/// <param name="X">
/// Where the system is, in light years (with <paramref name="Y"/> and <paramref name="Z"/>): where the
/// journal said the ship arrived. In the rare case that the journal did not say, the middle of the
/// game's cube the system's number names, which is never more than half that cube off.
/// </param>
/// <param name="At">When the commander first scanned a star, planet or moon there that nobody had discovered before (UTC).</param>
public sealed record FirstFind(
    string Name,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Id64,
    double X, double Y, double Z, DateTime At);

/// <summary>
/// Every star system where the commander has made a first discovery, over all the years of their
/// journals, for the 3D map's "Your first discoveries". The game says so itself: each scan it writes
/// carries "WasDiscovered", false when nobody had handed in data on that star, planet or moon before
/// (the star the ship arrives at is scanned by itself, so a new system always shows). The trip's
/// count (<see cref="ExploreTrip"/>) cannot serve here, because it starts over whenever the finds are sold.
///
/// The journals are searched once, on a background thread, the first time the list is asked for: a
/// long-time player has thousands of them, and neither the start-up nor the UI thread waits for that.
/// What was found is kept in the 3D map's folder with the time the search reached, so the next start
/// of Studio reads only the journals written since. From then on the journal lines the game writes
/// are followed as they come (<see cref="GameState.Journal"/>).
///
/// Only the commander's own journals are read. Nothing is asked of anybody.
/// </summary>
public sealed class FirstsLog
{
    const string FileName = "firsts.json";

    /// <summary>What is kept on disk: the systems found, and the time the search of the journals reached.</summary>
    /// <param name="ReadTo">Every journal last written before this time has been searched.</param>
    sealed record Kept(DateTime ReadTo, FirstFind[] Systems);

    readonly GameState game;
    readonly string journalDir, file;
    readonly Lock guard = new();                  // the journal lines come on the UI thread, the search's finds from the background
    readonly Dictionary<long, FirstFind> found = [];
    Task? search;

    /// <param name="game">The game state, whose journal lines are followed from now on.</param>
    /// <param name="journalDir">The game's journal folder.</param>
    /// <param name="folder">Where what was found is kept: the 3D map's folder in Studio's data folder. It is made when the first list is kept.</param>
    public FirstsLog(GameState game, string journalDir, string folder)
    {
        (this.game, this.journalDir, file) = (game, journalDir, Path.Combine(folder, FileName));
        game.Journal += Follow;
    }

    /// <summary>
    /// The systems with a first discovery, the earliest first. The first call waits for the search of
    /// the older journals (on a background thread); later calls answer at once.
    /// </summary>
    public async Task<IReadOnlyList<FirstFind>> Systems()
    {
        Task searching;
        lock (guard) searching = search ??= Task.Run(SearchJournals);
        await searching;
        lock (guard) return [.. found.Values.OrderBy(system => system.At).ThenBy(system => system.Id64)];
    }

    /// <summary>A new journal line, as the game writes it (and those of the newest journal again at start-up, which change nothing).</summary>
    void Follow(JournalLine line)
    {
        if (line.Event != "Scan") return;
        // The game has already taken the line in: the system the ship is in is the one it arrived in last.
        if (Find(line.Data, line.TimeUtc, (game.SystemAddress, game.StarSystem ?? "", game.StarPos)) is { } first)
            lock (guard) Add(first);
    }

    /// <summary>
    /// The search itself, on a background thread: what earlier runs kept, then every journal not
    /// searched by them. Touches nothing but the files and <see cref="found"/> (under the lock).
    /// </summary>
    void SearchJournals()
    {
        Kept? kept = Load();
        lock (guard)
            foreach (FirstFind? system in kept?.Systems ?? [])
                if (system is { Name: not null, Id64: > 0 }) Add(system);   // the file is on disk, and may have been changed by hand

        FileInfo[] journals;
        try
        {
            // Oldest first, as the game wrote them: the earliest find of a system is the one kept.
            journals = [.. new DirectoryInfo(journalDir).EnumerateFiles("Journal.*.log")
                .OrderBy(journal => journal.LastWriteTimeUtc).ThenBy(journal => journal.Name, StringComparer.Ordinal)];
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            return;   // no journal folder yet: the game makes it on its first run
        }
        if (journals.Length == 0) return;

        // "Not before" rather than "after": the journal that was the newest last time may have been written on since.
        DateTime readTo = kept?.ReadTo ?? DateTime.MinValue, newest = journals[^1].LastWriteTimeUtc;
        bool allRead = true;
        int searched = 0;
        foreach (FileInfo journal in journals.Where(journal => journal.LastWriteTimeUtc >= readTo))
        {
            allRead &= Search(journal.FullName);
            searched++;
        }
        FirstFind[] all;
        lock (guard) all = [.. found.Values.OrderBy(system => system.At).ThenBy(system => system.Id64)];
        Log.Info($"3D map: {searched} journals searched for first discoveries; {all.Length} systems in all");
        // A journal that could not be read is searched again next time: the time does not move on.
        Save(new Kept(allRead ? newest : readTo, all));
    }

    /// <summary>One journal's first discoveries. False when the file could not be read to its end.</summary>
    bool Search(string journal)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(journal, FileMode.Open, FileAccess.Read, GameWatcher.GameMayWrite), Encoding.UTF8);
            // The line of the last arrival, read only when a first is found after it: most arrivals have none.
            string? arrival = null;
            (long Address, string Name, double[]? At)? here = null;
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains("\"event\":\"FSDJump\"", StringComparison.Ordinal) || line.Contains("\"event\":\"Location\"", StringComparison.Ordinal)
                    || line.Contains("\"event\":\"CarrierJump\"", StringComparison.Ordinal))
                {
                    (arrival, here) = (line, null);
                    continue;
                }
                if (!line.Contains("\"event\":\"Scan\"", StringComparison.Ordinal) || !line.Contains("\"WasDiscovered\":false", StringComparison.Ordinal)) continue;
                try
                {
                    here ??= Arrival(arrival);
                    using JsonDocument parsed = JsonDocument.Parse(line);
                    JsonElement scan = parsed.RootElement;
                    DateTime at = scan.Field("timestamp") is { ValueKind: JsonValueKind.String } stamp && stamp.TryGetDateTime(out DateTime time) ? time.ToUniversalTime() : default;
                    if (Find(scan, at, here.Value) is { } first)
                        lock (guard) Add(first);
                }
                catch (JsonException)
                {
                    // a line cut short when the game stopped: there is nothing more to be had from it, now or later
                }
            }
            return true;
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error($"{Path.GetFileName(journal)}: could not be searched for first discoveries; it is tried again at the next start", problem);
            return false;
        }
    }

    /// <summary>The system an arrival line puts the ship in: its number, name and place. Nothing known without one.</summary>
    static (long Address, string Name, double[]? At) Arrival(string? line)
    {
        if (line is null) return (0, "", null);
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(line);
            JsonElement e = parsed.RootElement;
            return (e.Whole("SystemAddress"), e.Text("StarSystem"), e.Position("StarPos"));
        }
        catch (JsonException)
        {
            return (0, "", null);
        }
    }

    /// <summary>
    /// The system a scan makes a first discovery in; null when it makes none. As on the trip, only
    /// stars, planets and moons count: the game also writes scans of belt clusters and rings.
    /// </summary>
    /// <param name="here">The system the ship last arrived in, which is where the scan was made.</param>
    static FirstFind? Find(JsonElement scan, DateTime at, (long Address, string Name, double[]? At) here)
    {
        if (scan.Field("StarType") is null && scan.Field("PlanetClass") is null) return null;
        if (scan.Field("WasDiscovered") is not { ValueKind: JsonValueKind.False } || at == default) return null;
        long address = scan.Whole("SystemAddress") is not 0 and var named ? named : here.Address;
        if (address <= 0) return null;   // the game has written the number since 2018, and every scan that says "WasDiscovered" has it
        bool there = address == here.Address;
        CubePlace? cube = SystemAddress.Place(address);
        string name = scan.Text("StarSystem") is { Length: > 0 } system ? system
            : there && here.Name.Length > 0 ? here.Name
            : cube is null ? "" : ProcGenNames.SystemName(cube) ?? "";
        if (there && here.At is [var x, var y, var z]) return new FirstFind(name, address, x, y, z, at);
        if (cube is null) return null;
        (double cx, double cy, double cz) = cube.Centre;
        return new FirstFind(name, address, cx, cy, cz, at);
    }

    /// <summary>Keeps a find, unless an earlier one of the same system is kept already. Call under the lock.</summary>
    void Add(FirstFind first)
    {
        if (!found.TryGetValue(first.Id64, out FirstFind? had) || first.At < had.At) found[first.Id64] = first;
    }

    Kept? Load()
    {
        try
        {
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<Kept>(File.ReadAllText(file), Settings.Json) is { Systems: not null } kept
                ? kept
                : throw new JsonException("The file holds no list");
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error("The 3D map's list of your first discoveries could not be read, so every journal is searched again", problem);
            return null;
        }
    }

    void Save(Kept kept)
    {
        try
        {
            // Written beside its place and then moved there, so that a crash mid-write never leaves half a file.
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(kept, Settings.Json));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error("The 3D map's list of your first discoveries could not be kept on disk, so the journals are searched again next time", problem);
        }
    }
}
