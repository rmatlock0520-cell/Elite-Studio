using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Nav;

/// <summary>A system name Nav scan read off a picture, as its check stands (GET /api/nav/heatmap/navscan, "names").</summary>
/// <param name="Name">The name as the game spells it: decoded from what was read, or as the detailed map has a catalogue's name.</param>
/// <param name="Id64">
/// The game's own number for it: worked out from its generated name (as the game works it out), or the detailed map's
/// for a catalogue name. Written as text, because these numbers can be larger than a web page can hold exactly.
/// </param>
/// <param name="State">
/// As a stop of a route's check: <see cref="HeatmapFinds.Waiting"/>, <see cref="HeatmapFinds.Checking"/>,
/// <see cref="HeatmapFinds.Recorded"/>, <see cref="HeatmapFinds.NotRecorded"/> or <see cref="HeatmapFinds.Failed"/>.
/// </param>
/// <param name="Unsure">
/// One of the names a reading Nav scan could not make out for certain may be ("NX-Q" or "NX-G": Q, G and O look
/// alike). They are checked together: if any of them is recorded, that one was meant, and only it is listed.
/// </param>
/// <param name="X">Where it is (with <paramref name="Y"/> and <paramref name="Z"/>): the middle of its cube, or the detailed map's own place for a catalogue name.</param>
/// <param name="PlusMinus">It lies within this many light years of that point along each of x, y and z: half its cube. Null for a place the detailed map gave.</param>
/// <param name="From">The picture it was read off, by its file name.</param>
public sealed record NavScanName(
    string Name,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Id64,
    string State, bool Unsure, double? X, double? Y, double? Z, double? PlusMinus, string From)
{
    /// <summary>How it was settled, in a few words; null while it is not, or when it could not be.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? How { get; init; }

    /// <summary>Why it could not be checked, in the player's words; null otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}

/// <summary>A system a name read off a picture may be.</summary>
/// <param name="PlusMinus">Half its cube, for a place worked out from its name; null for a place the detailed map gave.</param>
internal sealed record ScanVariant(string Name, long Id64, double X, double Y, double Z, double? PlusMinus);

/// <summary>A name read off a picture: the one system it is, or (unsure) the systems it may be.</summary>
internal sealed record ScanEntry(IReadOnlyList<ScanVariant> Variants)
{
    public bool Unsure => Variants.Count > 1;
}

/// <summary>How the check of one picture's names stands (<see cref="HeatmapFinds.CheckScanned"/>).</summary>
/// <param name="Names">Each system as the page lists it: the systems an unsure name may be, until one turns out recorded.</param>
/// <param name="Entries">The names read: a name read unclearly counts once, whatever it may be.</param>
/// <param name="Unsure">Names read unclearly that no recorded system settled.</param>
/// <param name="NotRecordedNames">The names found not recorded, as the overlay names them ("… (unsure)" for one read unclearly).</param>
/// <param name="New">Of those, the ones found not recorded just now, by Spansh's answer: not in the memory of checks or Your finds before.</param>
/// <param name="Requests">Requests to Spansh the check sent.</param>
internal sealed record ScanTally(IReadOnlyList<NavScanName> Names, int Entries, int Recorded, int NotRecorded, int Failed, int Unsure,
    IReadOnlyList<string> NotRecordedNames, IReadOnlyList<string> New, int Requests);

// Nav scan's checking of the names read off a picture (NavScan.cs reads them). The owner, 15 October 2026: a name read
// from a screenshot of the Navigation panel is checked the careful way every other name is:
// - which of an unclear reading's spellings it is: one proved to exist is preferred (recorded in the 3D map's memory of
//   checks, in the kept surveys or the detailed map, named by the game: a find of a target or a route, a confirmed one,
//   the system the commander is in); with none proved, all of them are checked together, and if any is recorded that
//   was the name;
// - a catalogue's name ("HIP 12766") says nothing of where it lies, so it is kept only when the detailed map has it
//   near the commander (and is then recorded); the others are dropped, and counted;
// - each system the memory of checks first, then what is kept on this PC, then Spansh's search by exact names, at most
//   Spansh.MostNames to a request, each request one of the 3D map's own (its gate, its 200 a session), never sent again;
// - a name read is not proof the system exists (the reader may have misread a letter): one no database has is a
//   possible find, kept in Your finds as read from a picture, until the game names it with its number (a target, a
//   hop of a plotted route, an arrival), which confirms it; the system the commander is in, the game has named already.
public sealed partial class HeatmapFinds
{
    const string ReadNote = "read from a picture; target it in the game to confirm";
    const string ReadingsNote = "read from a picture, one of {0} ways the name may be spelt; target it in the game to confirm";
    const string ConfirmedNote = "read from a picture; confirmed: the game named it when you targeted it, plotted a route through it or arrived there";
    const string SwitchedOff = "Nav scan was switched off before it could be checked";

    /// <summary>Light years round the commander a catalogue name read without a distance is looked for in the detailed map: the Navigation panel's reach.</summary>
    const double NearbyLy = NavScanNames.PanelLy;

    /// <summary>Light years round the commander a catalogue name is looked for at most, whatever distance was read: the panel lists no farther.</summary>
    const double FarthestCatalogueLy = 100;

    /// <summary>Sectors whose catalogue names are kept in memory, the least recently used let go first.</summary>
    const int CatalogueSectorsKept = 8;

    readonly Lock catalogueGuard = new();
    // The systems of a sector of the detailed map with names that are not generated (catalogue and hand-given ones), by
    // the name as the memory compares it: a sector's file is read once for them, not for each picture.
    readonly List<((int X, int Y, int Z) Sector, Dictionary<string, FineSystem> Names)> catalogue = [];

    // ------------------------------------------------------------ confirmed by the game

    /// <summary>
    /// The game names these systems with their numbers: any of them Nav scan read off a picture exists, and its find is
    /// confirmed. Away from the calling thread (finds.json may still have to be read), and only when one of them may
    /// be such a find.
    /// </summary>
    void Confirm(IReadOnlyCollection<long> ids)
    {
        lock (guard)
        {
            if (finds is not null && !finds.Concat(unsaved).Any(find => find.Confirmed == false && ids.Contains(find.Id64))) return;
        }
        Task confirming = Task.Run(() => ConfirmNow(ids));
        lock (guard) idle = idle.IsCompleted ? confirming : Task.WhenAll(idle, confirming);
    }

    /// <summary>
    /// The finds Nav scan read off a picture among these systems are confirmed (the game named them), and the other
    /// spellings of a name read unclearly are taken off: they were misreads. Reads finds.json the first time: call it
    /// away from the game's thread.
    /// </summary>
    void ConfirmNow(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0) return;
        var confirmed = new List<HeatmapFind>();
        lock (guard)
        {
            if (Loaded() is not { } list) return;   // finds.json cannot be read just now: the next time the game names it
            for (int at = 0; at < list.Count; at++)
                if (list[at] is { Confirmed: false } read && ids.Contains(read.Id64))
                    confirmed.Add(list[at] = read with { Confirmed = true, Note = ConfirmedNote });
            if (confirmed.Count == 0) return;
            HashSet<string> spelt = [.. confirmed.Select(find => find.Readings).OfType<string>()];
            list.RemoveAll(find => find is { Confirmed: false, Readings: { } readings } && spelt.Contains(readings));
            Save(list);
        }
        foreach (HeatmapFind find in confirmed)
        {
            Log.Info($"3D map: {find.Name}, read off a picture by Nav scan, is confirmed: the game named it");
            Confirmed?.Invoke(find);
        }
    }

    // ------------------------------------------------------------ which systems the names are

    /// <summary>
    /// The systems the names read off one picture are: of an unclear reading's spellings, those proved to exist when any
    /// are (else all of them, unsure); catalogue names the detailed map has near <paramref name="here"/> (the others
    /// dropped, and counted); each system once, and an unclear reading left out when a clear reading of the same row
    /// named one of its spellings. Reads files away from the calling thread.
    ///
    /// A spelling is proved only by what says the system exists: the memory of checks found it recorded, the surveys or
    /// the detailed map on this PC have it, the game named it (a find of a target or a route, a Nav scan find the game
    /// has confirmed, or <paramref name="current"/>, the system the commander is in). Not by a "not recorded" in the
    /// memory, nor by a find Nav scan only read: both are what a misread leaves behind, and preferring them would make a
    /// misread stick.
    /// </summary>
    internal async Task<(IReadOnlyList<ScanEntry> Entries, int Dropped)> SortScanned(PictureNames read, (double X, double Y, double Z)? here, long current = 0)
    {
        HashSet<long> proved = await Task.Run(ProvedFindIds);
        if (current > 0) proved.Add(current);
        var readings = new List<IReadOnlyList<ScanVariant>>();
        foreach (NameRead name in read.Names)
        {
            IReadOnlyList<DecodedName> variants = name.Variants;
            if (variants.Count > 1)
            {
                var known = new List<DecodedName>();
                foreach (DecodedName variant in variants)
                    if (proved.Contains(variant.Cube.Address)
                        || Memory.Get(variant.Name, variant.Cube.Address) is { Result: CheckMemory.Recorded }
                        || (await Locate(variant.Name, variant.Cube.Address)).Found)
                        known.Add(variant);
                if (known.Count > 0) variants = known;
            }
            readings.Add([.. variants.Select(Scanned)]);
        }
        (List<ScanVariant> listed, int dropped) = await Task.Run(() => CatalogueNamed(read.Catalogue, here));
        readings.AddRange(listed.Select(system => (IReadOnlyList<ScanVariant>)[system]));
        return (Grouped(readings), dropped);
    }

    /// <summary>
    /// The numbers of the finds the game named (a target's, a route's, a Nav scan find it confirmed): those systems exist.
    /// Reads finds.json the first time: call it away from the game's thread.
    /// </summary>
    HashSet<long> ProvedFindIds()
    {
        lock (guard) return new HashSet<long>((Loaded() ?? []).Concat(unsaved).Where(find => find.Confirmed != false).Select(find => find.Id64));
    }

    static ScanVariant Scanned(DecodedName name)
    {
        (double x, double y, double z) = name.Cube.Centre;
        return new ScanVariant(name.Name, name.Cube.Address, x, y, z, name.HalfWidth);
    }

    /// <summary>
    /// The readings as names to check: each system read clearly once; a reading with several spellings left out when
    /// one of them was read clearly too (the same row, read better in another piece of the picture), and two that share
    /// spellings made one, of the spellings both allow.
    /// </summary>
    static List<ScanEntry> Grouped(IReadOnlyList<IReadOnlyList<ScanVariant>> readings)
    {
        var entries = new List<ScanEntry>();
        var sure = new HashSet<long>();
        foreach (IReadOnlyList<ScanVariant> reading in readings)
            if (reading.Count == 1 && sure.Add(reading[0].Id64)) entries.Add(new ScanEntry(reading));
        var groups = new List<List<ScanVariant>>();
        foreach (IReadOnlyList<ScanVariant> reading in readings)
        {
            if (reading.Count < 2 || reading.Any(variant => sure.Contains(variant.Id64))) continue;
            List<ScanVariant> group = [.. reading];
            for (int at = groups.Count - 1; at >= 0; at--)
            {
                List<ScanVariant> other = groups[at];
                if (!other.Any(seen => group.Any(variant => variant.Id64 == seen.Id64))) continue;
                group = [.. group.Where(variant => other.Any(seen => seen.Id64 == variant.Id64))];
                groups.RemoveAt(at);
            }
            groups.Add(group);
        }
        foreach (List<ScanVariant> group in groups)
            if (group.Count > 1 || sure.Add(group[0].Id64)) entries.Add(new ScanEntry(group));
        return entries;
    }

    /// <summary>
    /// The catalogue names the detailed map has round the commander (as far as the distances read say, else the
    /// Navigation panel's reach), as the map spells and places them; and how many it does not have, which are dropped:
    /// such a name says nothing of where it lies, so nothing else can tell whether it was read right. Call away from
    /// the game's thread.
    /// </summary>
    (List<ScanVariant> Found, int Dropped) CatalogueNamed(IReadOnlyList<CatalogueRead> reads, (double X, double Y, double Z)? here)
    {
        if (reads.Count == 0) return ([], 0);
        var wanted = new HashSet<string>(reads.Select(read => CheckMemory.Canonical(read.Name)), StringComparer.Ordinal);
        if (here is not { } at) return ([], wanted.Count);
        double reach = Math.Min(FarthestCatalogueLy, reads.Max(read => read.DistanceLy ?? NearbyLy)) + NavScanNames.DistanceSlackLy;
        var found = new Dictionary<string, ScanVariant>(StringComparer.Ordinal);
        foreach ((int sx, int sy, int sz) in HeatmapReach.SectorsOf(at.X, at.Y, at.Z, reach))
        {
            if (!fine.Covers(sx, sz)) continue;
            foreach ((string key, FineSystem system) in CatalogueOf(sx, sy, sz))
            {
                double far = Math.Sqrt((system.X - at.X) * (system.X - at.X) + (system.Y - at.Y) * (system.Y - at.Y) + (system.Z - at.Z) * (system.Z - at.Z));
                if (far <= reach && wanted.Contains(key)) found.TryAdd(key, new ScanVariant(system.Name, system.Id64, system.X, system.Y, system.Z, null));
            }
        }
        return ([.. found.Values], wanted.Count - found.Count);
    }

    /// <summary>The systems of a sector of the detailed map whose names are not generated, by name; read once and kept (<see cref="CatalogueSectorsKept"/>).</summary>
    IReadOnlyDictionary<string, FineSystem> CatalogueOf(int x, int y, int z)
    {
        lock (catalogueGuard)
        {
            int kept = catalogue.FindIndex(sector => sector.Sector == (x, y, z));
            if (kept >= 0)
            {
                var sector = catalogue[kept];
                catalogue.RemoveAt(kept);
                catalogue.Insert(0, sector);
                return sector.Names;
            }
        }
        var named = new Dictionary<string, FineSystem>(StringComparer.Ordinal);
        try
        {
            foreach (FineSystem system in fine.Systems(x, y, z))
                if (!GeneratedMiddle().IsMatch(system.Name)) named.TryAdd(CheckMemory.Canonical(system.Name), system);
        }
        catch (Exception unreadable) when (unreadable is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Says nothing either way; read again next time.
            Log.Error($"The detailed map of sector {x},{y},{z} could not be searched for catalogue names", unreadable);
            return named;
        }
        lock (catalogueGuard)
        {
            catalogue.Insert(0, ((x, y, z), named));
            if (catalogue.Count > CatalogueSectorsKept) catalogue.RemoveAt(catalogue.Count - 1);
        }
        return named;
    }

    void ForgetCatalogue()
    {
        lock (catalogueGuard) catalogue.Clear();
    }

    // A generated name's middle, which no catalogue or hand-given name has: " WO-J a38".
    [GeneratedRegex(@"\s[A-Za-z]{2}-[A-Za-z]\s[A-Ha-h]\d")]
    private static partial Regex GeneratedMiddle();

    // ------------------------------------------------------------ the check

    /// <summary>
    /// Checks the systems one picture's names are, each once: the memory of checks first (a target's check of it under
    /// way is waited for), what is kept on this PC and Your finds next, then Spansh's search by exact names for the rest,
    /// at most <see cref="Spansh.MostNames"/> to a request, each one of the 3D map's own (its gate, its 200 a session),
    /// none sent again; last, the systems the route's check had taken, as it settles them (HeatmapRouteCheck.cs: the
    /// two never wait on each other). What came of each goes into the memory. A name nobody has recorded (every spelling
    /// of an unsure one) is kept in Your finds as read from a picture, unconfirmed (but the system the commander is in,
    /// which the game named), and raises <see cref="Found"/>. Never throws: what goes wrong is logged and said.
    /// <paramref name="told"/> hears how it stands as it goes.
    /// </summary>
    /// <param name="from">The picture's file name, for the names as listed.</param>
    /// <param name="stopping">Nav scan switched off: a request still waiting its turn at the gate is never sent, and what was not reached says so.</param>
    /// <param name="waiting">Told true when a request has to wait its turn behind another of the 3D map's, and false when its turn has come.</param>
    /// <param name="current">
    /// The system the commander was in when the picture was taken, by the game's number for it (0 when not known): the
    /// game named it, so it exists. Not recorded, its find is confirmed at once, and it is not news (no ping: the player
    /// is there).
    /// </param>
    internal async Task<ScanTally> CheckScanned(IReadOnlyList<ScanEntry> entries, string from, Action<ScanTally>? told = null, CancellationToken stopping = default,
        Action<bool>? waiting = null, long current = 0)
    {
        // One check of each system, though two names may be one (an unclear reading's spelling of a name read clearly).
        var checks = new Dictionary<long, StopCheck>();
        StopCheck[][] shape =
        [
            .. entries.Select(entry => entry.Variants.Select(variant => checks.TryGetValue(variant.Id64, out StopCheck? same)
                ? same
                : checks[variant.Id64] = new StopCheck(new RouteStop(variant.Name, variant.Id64, null, ""))).ToArray()),
        ];
        var fresh = new HashSet<StopCheck>();   // found not recorded by Spansh's answer just now: new
        var asking = new List<StopCheck>();     // claimed by this check, to ask Spansh about
        List<StopCheck> sent = [];              // the batch whose request went, until its answer is settled
        int requests = 0;
        ScanTally Now()
        {
            lock (guard) return Tally(entries, shape, fresh, from, requests, current);
        }

        try
        {
            HashSet<long> yours = await Task.Run(FindIds);
            List<StopCheck> pending = [.. checks.Values];
            while (pending.Count > 0 && !stopping.IsCancellationRequested)
            {
                asking = [];
                var aside = new List<StopCheck>();   // taken by another check of many (the route's): waited for once this one holds none
                foreach (StopCheck stop in pending)
                {
                    if (stopping.IsCancellationRequested) break;
                    switch (await OfflineAlone(stop, yours, stopping))
                    {
                        case Settling.Ask:
                            asking.Add(stop);
                            break;
                        case Settling.Aside:
                            aside.Add(stop);
                            break;
                    }
                }
                await Memory.SaveAsync();
                told?.Invoke(Now());

                for (int at = 0; at < asking.Count && !stopping.IsCancellationRequested; at += Spansh.MostNames)
                {
                    List<StopCheck> batch = asking.GetRange(at, Math.Min(Spansh.MostNames, asking.Count - at));
                    lock (guard)
                    {
                        foreach (StopCheck stop in batch) stop.State = Checking;
                    }
                    told?.Invoke(Now());
                    NamesLookUp answer = await survey.LookUpNames([.. batch.Select(stop => (stop.Name, stop.Id64))], waiting, stopping);
                    if (answer.NotSent) break;   // switched off while it waited its turn: nothing asked, nothing counted
                    // What Within reach listed while the batch waited its turn (a picture taken just after a jump waits behind
                    // that very request) is settled from its answer: those were left out of the request.
                    List<StopCheck> rest = FromNear(batch, answer);
                    if (answer.UsedUp)
                    {
                        List<StopCheck> left = [.. rest, .. asking.Skip(at + batch.Count)];
                        Log.Error($"3D map: {left.Count} names read by Nav scan were not checked: the requests to Spansh for this session are used up");
                        foreach (StopCheck stop in left) Settle(stop, null, null, null, LimitReached);
                        break;
                    }
                    // Not sent (nor counted) when every one of them was in Within reach's answer: nothing is left to settle.
                    if (answer.Sent)
                    {
                        sent = rest;
                        requests++;
                        if (answer.Problem is { } problem)
                        {
                            Log.Error($"3D map: {rest.Count} names read by Nav scan could not be checked at Spansh: {problem}");
                            foreach (StopCheck stop in rest) Settle(stop, null, CheckMemory.BySpansh, null, problem);
                        }
                        else
                            foreach (StopCheck stop in rest)
                            {
                                NamedSystem? listed = answer.Found!.FirstOrDefault(system => system.Name.Equals(stop.Name, StringComparison.OrdinalIgnoreCase)
                                    && (system.Id64 <= 0 || system.Id64 == stop.Id64));
                                if (listed is not null)
                                {
                                    if (listed is { X: { } lx, Y: { } ly, Z: { } lz })
                                        lock (guard) places[stop.Id64] = new Located(true, lx, ly, lz, null, [], FoundBySpansh, CheckMemory.BySpansh);
                                    Settle(stop, true, CheckMemory.BySpansh, FoundBySpansh, null);
                                }
                                else if (!answer.Complete) Settle(stop, null, CheckMemory.BySpansh, null, CutShort);
                                else
                                {
                                    Settle(stop, false, CheckMemory.BySpansh, NotFoundBySpansh, null);
                                    lock (guard) fresh.Add(stop);
                                }
                            }
                    }
                    sent = [];
                    // What the batch found, into the memory's file before the next is asked.
                    await Memory.SaveAsync();
                    told?.Invoke(Now());
                }

                // This check holds no system now: those the route's check had taken are waited for, and settled from what
                // it found; one it gave up unasked (the route went) is looked at again, as at first.
                asking = [];
                pending = await AfterOthers(aside, stopping);
            }

            List<HeatmapFind> made = ScanFinds(entries, shape, fresh, current);
            if (made.Count > 0)
            {
                await Task.Run(() => Keep(made));
                Log.Info($"3D map: Nav scan read {string.Join(", ", made.Select(find => find.Name))} off {from}, which no database Studio knows has; kept in your finds until the game confirms {(made.Count == 1 ? "it" : "them")}");
                foreach (HeatmapFind find in made) Found?.Invoke(find);
            }
        }
        catch (Exception unexpected)
        {
            // As the route's check: a system asked about and not yet settled gets this for its answer; one taken to ask
            // about but never asked is given back; the rest keep whatever the memory knows of them.
            Log.Error($"3D map: the names Nav scan read off {from} could not all be checked", unexpected);
            foreach (StopCheck stop in checks.Values)
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
        }
        finally
        {
            // Not reached (Nav scan was switched off): given back for a later check, and said.
            List<StopCheck> left;
            lock (guard) left = [.. checks.Values.Where(stop => !stop.Ended)];
            Release(left);
            lock (guard)
            {
                foreach (StopCheck stop in left) Show(stop, Failed, null, SwitchedOff);
            }
            await Memory.SaveAsync();
        }
        return Now();
    }

    /// <summary>
    /// The finds a picture's check makes: each name nobody has recorded that Spansh said so of just now (every spelling
    /// of an unsure one: one recorded means that was the name), placed at its cube's middle, unconfirmed; the system
    /// the commander is in (<paramref name="current"/>), which the game named, confirmed at once.
    /// </summary>
    List<HeatmapFind> ScanFinds(IReadOnlyList<ScanEntry> entries, StopCheck[][] shape, HashSet<StopCheck> fresh, long current)
    {
        string day = Day(DateTime.UtcNow);
        var made = new List<HeatmapFind>();
        lock (guard)
        {
            for (int at = 0; at < entries.Count; at++)
            {
                StopCheck[] stops = shape[at];
                if (!stops.All(stop => stop.State == NotRecorded) || !stops.Any(fresh.Contains)) continue;
                string? readings = stops.Length > 1 ? string.Join(" or ", stops.Select(stop => stop.Name)) : null;
                for (int one = 0; one < stops.Length; one++)
                {
                    if (!fresh.Contains(stops[one])) continue;
                    ScanVariant variant = entries[at].Variants[one];
                    List<string> how = [.. stops[one].Kept?.How ?? [], $"{NotFoundBySpansh} ({day})"];
                    bool here = current > 0 && variant.Id64 == current;
                    made.Add(new HeatmapFind(variant.Name, variant.Id64, variant.X, variant.Y, variant.Z, variant.PlusMinus, DateTime.UtcNow, FromNavScan, how)
                    {
                        Note = here ? ConfirmedNote : readings is null ? ReadNote : string.Format(System.Globalization.CultureInfo.InvariantCulture, ReadingsNote, stops.Length),
                        Confirmed = here,
                        Readings = here ? null : readings,
                    });
                }
            }
        }
        return made;
    }

    /// <summary>How the check of a picture's names stands. Call under the guard.</summary>
    /// <param name="current">The system the commander is in: found not recorded, it is not news (<see cref="ScanTally.New"/>), for the player is there.</param>
    static ScanTally Tally(IReadOnlyList<ScanEntry> entries, StopCheck[][] shape, HashSet<StopCheck> fresh, string from, int requests, long current)
    {
        var names = new List<NavScanName>();
        var notRecordedNames = new List<string>();
        var news = new List<string>();
        int recorded = 0, notRecorded = 0, failed = 0, unsure = 0;
        for (int at = 0; at < entries.Count; at++)
        {
            StopCheck[] stops = shape[at];
            bool any = stops.Any(stop => stop.State == Recorded);
            bool sure = stops.Length == 1 || any;
            for (int one = 0; one < stops.Length; one++)
            {
                // A spelling of an unsure name that is not the recorded one was a misread.
                if (stops.Length > 1 && any && stops[one].State != Recorded) continue;
                ScanVariant variant = entries[at].Variants[one];
                names.Add(new NavScanName(stops[one].Name, stops[one].Id64, stops[one].State, !sure, variant.X, variant.Y, variant.Z, variant.PlusMinus, from)
                {
                    How = stops[one].How,
                    Reason = stops[one].Reason,
                });
            }
            if (!sure) unsure++;
            if (any) recorded++;
            else if (stops.Any(stop => stop.State == Failed)) failed++;
            else if (stops.All(stop => stop.State == NotRecorded))
            {
                notRecorded++;
                string shown = sure ? stops[0].Name : stops[0].Name + " (unsure)";
                notRecordedNames.Add(shown);
                if (stops.Any(fresh.Contains) && !stops.Any(stop => current > 0 && stop.Id64 == current)) news.Add(shown);
            }
        }
        return new ScanTally(names, entries.Count, recorded, notRecorded, failed, unsure, notRecordedNames, news, requests);
    }
}
