using System.Text.Json;
using EliteStudio.Core;

namespace EliteStudio.Market;

/// <summary>One minor faction in a system, with its share of the influence there in hundredths.</summary>
public sealed record FactionLine(string Name, string Allegiance, string Government, double Influence, string State);

/// <summary>One station of a system, as the system's own sheet lists it.</summary>
/// <param name="Type">The kind of station, in Spansh's words.</param>
/// <param name="Pad">The largest landing pad: "S", "M" or "L"; "" when not known.</param>
public sealed record SystemStationLine(string Name, string Type, double? StarLs, string Pad, bool Market, bool Shipyard, bool Outfitting);

/// <summary>
/// A star system as its own sheet shows it, from Spansh: who runs it, how many live there, the
/// factions in it and every station it has. A text that is not known is "", a number null.
/// </summary>
/// <param name="DistanceLy">Light years from the system the player is in; null while the game has not said where that is.</param>
/// <param name="Economy">The primary economy, and the secondary one after a comma when there is one.</param>
/// <param name="Powers">Every Power with a hand in the system; the one in control is <paramref name="Power"/>.</param>
/// <param name="Stations">Stations and settlements first, nearest the star first; fleet carriers after them, by name.</param>
public sealed record SystemSheet(
    string Name, double? DistanceLy, string Allegiance, string Government, string Security, long? Population, string Economy, string Power, string PowerState,
    IReadOnlyList<string> Powers, string Faction, string FactionState, string Region, bool NeedsPermit, int Bodies, DateTime? Updated,
    IReadOnlyList<FactionLine> Factions, IReadOnlyList<SystemStationLine> Stations);

/// <summary>One star system of a sector, as "Find by name" lists it.</summary>
/// <param name="DistanceLy">Light years from the system the search measures from; null when that is not known.</param>
/// <param name="Economy">The primary economy, and the secondary one after a comma; "" for a system nobody lives in.</param>
/// <param name="Stations">How many stations, settlements and fleet carriers are known there.</param>
public sealed record SystemLine(
    string System, double? DistanceLy, long? Population, string Allegiance, string Government, string Security, string Economy, string Power, int Stations, int Bodies, DateTime? Updated);

/// <summary>What a sector search found: the same parts as a <see cref="FindAnswer"/>, with star systems for rows.</summary>
public sealed record SectorAnswer(string What, string From, IReadOnlyList<SystemLine> Rows, string Source, string? Link, string? Note, int? More, DateTime AskedUtc);

// The part of Finder behind a name the player opens: the sheet of one station by its exact name (and its
// system, when the page knows it), and the sheet of one star system. Both come from Spansh. Routes, under
// /api/market/inspect/: GET station?name=&system= (a StationDetails) and GET system?name= (a SystemSheet).
// The class is described in FindApi.cs.
sealed partial class Finder
{
    const string Systems = "https://spansh.co.uk/api/systems/search";
    const int NamesakesAsked = 5;   // stations of one name that are asked for: a call sign is one of a kind, a starport's name seldom is not
    const int SectorPage = 50;      // star systems asked of Spansh at a time for a sector: each comes with its bodies and stations

    sealed record SystemPage(int Count, List<StarSystem?>? Results);

    /// <summary>What a system's sheet reads of Spansh's answer about it.</summary>
    sealed record StarSystem(
        string? Name, string? Allegiance, string? Government, string? Security, double? Population, string? PrimaryEconomy, string? SecondaryEconomy,
        string? ControllingPower, string? PowerState, List<string?>? Power, string? ControllingMinorFaction, string? ControllingMinorFactionState, string? Region,
        bool? NeedsPermit, double? X, double? Y, double? Z, double? BodyCount, string? UpdatedAt, List<Presence?>? MinorFactionPresences, List<Berth?>? Stations,
        double? Distance = null);

    sealed record Presence(string? Name, string? Allegiance, string? Government, double? Influence, string? State);

    sealed record Berth(string? Name, string? Type, double? DistanceToArrival, double? LargePads, double? MediumPads, double? SmallPads, bool? HasMarket, bool? HasShipyard, bool? HasOutfitting);

    /// <summary>
    /// The sheet of one station, found by its exact name. Several stations may share a name, so the
    /// system counts when the page gives it; without one the first Spansh sends is taken. What Spansh
    /// says of its prices is kept as well, so the price list can be opened from the sheet.
    /// </summary>
    public Task<StationDetails> StationSheet(string name, string system)
    {
        (name, system) = (name.Trim(), system.Trim());
        if (name.Length == 0) throw new FindProblem("No station was named.");
        return Asked(SpanshName, $"Inspect station \"{name}\"", async ask =>
        {
            var filters = new Dictionary<string, object> { ["name"] = Value(name) };
            if (system.Length > 0) filters["system_name"] = Value(system);
            var question = new Dictionary<string, object> { ["filters"] = filters, ["size"] = NamesakesAsked, ["page"] = 0 };
            if (game.StarPos is { } here) question["reference_coords"] = new { x = here[0], y = here[1], z = here[2] };
            (StationPage page, _) = await Page(question, null, ask);
            Station[] sent = [.. page.Results?.OfType<Station>().Where(station => Same(station.Name, name)) ?? []];
            Station found = sent.FirstOrDefault(station => system.Length == 0 || Same(station.SystemName, system))
                ?? throw new FindProblem(system.Length > 0 ? $"Spansh knows no station called \"{name}\" in {system}." : $"Spansh knows no station called \"{name}\".", 404);
            Remember(found);
            return Marked(Details(found, found.Name!))!;
        });
    }

    /// <summary>The sheet of one star system, found by its exact name.</summary>
    public Task<SystemSheet> SystemSheet(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new FindProblem("No system was named.");
        return Asked(SpanshName, $"Inspect system \"{name}\"", async ask =>
        {
            string question = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["filters"] = new Dictionary<string, object> { ["name"] = Value(name) }, ["size"] = 1, ["page"] = 0,
            }, Readable);
            (SystemPage page, _) = await ask.Answer(Systems, question, StationMinutes,
                async (body, stop) => await JsonSerializer.DeserializeAsync<SystemPage>(body, MarketData.SpanshNames, stop).ConfigureAwait(false) ?? new SystemPage(0, null));
            StarSystem found = page.Results?.OfType<StarSystem>().FirstOrDefault(star => Same(star.Name, name))
                ?? throw new FindProblem($"Spansh knows no system called \"{name}\".", 404);
            return Sheet(found, game.StarPos);
        });
    }

    /// <summary>
    /// The star systems of a sector: those whose names start with what was typed ("Wregoe YV-W", "Col
    /// 285 Sector"), nearest first, with what is known of each. Spansh is asked for names like the one
    /// typed, best match first, a page at a time; of each page the ones that really start so are kept.
    /// "more" is the page after the last one read, while that page still had some.
    /// </summary>
    public Task<SectorAnswer> Sector(FindRequest request)
    {
        string typed = request.Name.Trim();
        if (typed.Length == 0) throw new FindProblem("Type a sector's name, or the start of one: Wregoe, Col 285 Sector.");
        string from = request.From.Trim() is { Length: > 0 } named ? named : game.StarSystem ?? "";
        double[]? here = Same(from, game.StarSystem) ? game.StarPos : null;
        return Asked(SpanshName, $"Find sector \"{typed}\" from {from}", async ask =>
        {
            var filters = new Dictionary<string, object> { ["name"] = new { value = typed } };
            if (request.Inhabited) filters["population"] = new { comparison = "<=>", value = new[] { 1, 1_000_000_000_000 } };   // one person or more
            int page = Math.Max(0, request.More);
            var question = new Dictionary<string, object> { ["filters"] = filters, ["size"] = SectorPage, ["page"] = page };
            if (here is not null) question["reference_coords"] = new { x = here[0], y = here[1], z = here[2] };
            else if (from.Length > 0) question["reference_system"] = from;
            Task<(SystemPage Page, DateTime Asked)> answer = ask.Answer(Systems, JsonSerializer.Serialize(question, Readable), StationMinutes,
                async (body, stop) => await JsonSerializer.DeserializeAsync<SystemPage>(body, MarketData.SpanshNames, stop).ConfigureAwait(false) ?? new SystemPage(0, null));
            (SystemPage sent, DateTime asked) = here is null && from.Length > 0 ? await KnownSystem(SpanshName, from, answer) : await answer;

            StarSystem[] all = [.. sent.Results?.OfType<StarSystem>() ?? []];
            SystemLine[] rows =
            [
                .. from star in all
                   where star.Name is { Length: > 0 } && star.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)
                   orderby star.Distance ?? double.MaxValue, star.Name
                   select new SystemLine(star.Name!, star.Distance is { } ly ? Math.Round(ly, 2) : null, star.Population is > 0 ? (long)Math.Round(star.Population.Value) : null,
                       star.Allegiance ?? "", star.Government is null or "None" ? "" : star.Government, star.Security is null or "Anarchy" && star.Population is not > 0 ? "" : star.Security ?? "",
                       string.Join(", ", new[] { star.PrimaryEconomy, star.SecondaryEconomy }.Where(economy => economy is { Length: > 0 } && economy != "None")),
                       star.ControllingPower ?? "", star.Stations?.Count ?? 0, (int)Math.Clamp(Math.Round(star.BodyCount ?? 0), 0, 99999), MarketData.Time(star.UpdatedAt)),
            ];
            // Best match first: a page that was full and still held names that start so may have more of them after it.
            bool more = all.Length >= SectorPage && rows.Length > 0;
            string? note = rows.Length == 0
                ? (page > 0 ? $"No more systems whose names start with \"{typed}\" are known." : $"No {(request.Inhabited ? "inhabited " : "")}system whose name starts with \"{typed}\" is known. Check its spelling, or type less of it.")
                : more ? $"Spansh sends its best matches {SectorPage} at a time: there may be more." : null;
            return new SectorAnswer($"Systems of {typed}{(request.Inhabited ? " where people live" : "")}", from, rows, SpanshName, null, note, more ? page + 1 : null, asked);
        });
    }

    static SystemSheet Sheet(StarSystem star, double[]? here)
    {
        static bool Carrier(Berth berth) => (berth.Type ?? "").Contains("Carrier", StringComparison.OrdinalIgnoreCase);
        static string Pad(Berth berth) => StationFilter.PadLetter(berth.LargePads > 0 ? 3 : berth.MediumPads > 0 ? 2 : berth.SmallPads > 0 ? 1 : null);
        double? ly = here is not null && star is { X: { } x, Y: { } y, Z: { } z } ? Math.Round(ShipMath.Distance(here, [x, y, z]), 2) : null;
        return new SystemSheet(star.Name ?? "", ly, star.Allegiance ?? "", star.Government ?? "", star.Security ?? "",
            star.Population is > 0 ? (long)Math.Round(star.Population.Value) : null,
            string.Join(", ", new[] { star.PrimaryEconomy, star.SecondaryEconomy }.Where(economy => economy is { Length: > 0 } && economy != "None")),
            star.ControllingPower ?? "", star.PowerState ?? "", [.. (star.Power ?? []).OfType<string>().Where(power => power.Length > 0)],
            star.ControllingMinorFaction ?? "", star.ControllingMinorFactionState ?? "", star.Region ?? "", star.NeedsPermit == true,
            (int)Math.Clamp(Math.Round(star.BodyCount ?? 0), 0, 99999), MarketData.Time(star.UpdatedAt),
            [.. from presence in (star.MinorFactionPresences ?? []).OfType<Presence>()
                where presence.Name is { Length: > 0 }
                orderby presence.Influence descending
                select new FactionLine(presence.Name!, presence.Allegiance ?? "", presence.Government ?? "", Math.Round((presence.Influence ?? 0) * 100, 1), presence.State is null or "None" ? "" : presence.State)],
            [.. from berth in (star.Stations ?? []).OfType<Berth>()
                where berth.Name is { Length: > 0 }
                orderby Carrier(berth), Carrier(berth) ? 0 : berth.DistanceToArrival ?? double.MaxValue, berth.Name
                select new SystemStationLine(berth.Name!, berth.Type ?? "", berth.DistanceToArrival, Pad(berth), berth.HasMarket == true, berth.HasShipyard == true, berth.HasOutfitting == true)]);
    }
}
