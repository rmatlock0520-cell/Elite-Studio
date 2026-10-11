using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EliteStudio.App;
using EliteStudio.Overlay;

namespace EliteStudio.Market;

/// <summary>
/// A named set of search settings, switched from the overlay menu or the Market page. It holds only
/// the settings it changes, under their names in settings.json: the two that come with Studio hold
/// three each, one the player saves holds all of <see cref="MarketSettings.PresetKeys"/>.
/// </summary>
public sealed record Preset(string Name, IReadOnlyDictionary<string, JsonElement> Set);

/// <summary>
/// The "market" section of settings.json: how trades are searched for, the order of the overlay's
/// short trade list, and the presets. It is never changed in place. <see cref="Hauling"/> swaps in a
/// changed copy, so no setting can change without being saved and without the search hearing of it.
/// </summary>
public sealed record MarketSettings
{
    /// <summary>The section's name in settings.json.</summary>
    internal const string Section = "market";

    /// <summary>There is room for this many presets.</summary>
    public const int MaxPresets = 8;

    /// <summary>The longest name a preset may have, in letters, so it fits its line in the overlay menu.</summary>
    public const int MaxPresetName = 28;

    /// <summary>What a preset remembers: the search settings, not the ship, the keys or the look of the overlay.</summary>
    public static readonly IReadOnlyList<string> PresetKeys =
    [
        "startMode", "buyLy", "maxLy", "maxAgeHours", "returns", "rankBy", "pad", "maxStarLs", "surface",
        "minSupply", "minDemand", "carriers", "srcPower", "dstPower", "srcState", "dstState",
    ];

    /// <summary>The choices for minimum supply and minimum demand, in tons; 0 means any.</summary>
    public static readonly IReadOnlyList<int> VolumeSteps = [0, 100, 500, 1000, 2500, 5000, 10000, 50000];

    static readonly IReadOnlyList<Preset> Starters =
    [
        new("Quick one-way hops", Values("""{ "returns": false, "rankBy": "hour", "maxLy": 40 }""")),
        new("Long round trips", Values("""{ "returns": true, "rankBy": "trip", "maxLy": 120 }""")),
    ];

    public string StartMode { get; init; } = "near";        // near | anywhere: where trades may start
    public string RankBy { get; init; } = "hour";           // hour | trip
    public bool Returns { get; init; } = true;              // round trip (true) or one way
    public double JumpRange { get; init; }                  // light years per jump with a FULL hold, as typed by the player; 0 = work it out from the ship
    public double JumpEmpty { get; init; }                  // ... with an empty hold; 0 = work it out from the ship
    public string Pad { get; init; } = "auto";              // auto (from the ship) | S | M | L
    public int BuyLy { get; init; } = 15;                   // near: buy within this distance of the player; 0 = only the system they are in
    public int MaxLy { get; init; } = 30;                   // longest buy-to-sell distance; also how far the lookups look
    public int MaxAgeHours { get; init; } = 48;             // how old prices may be
    public int MaxStarLs { get; init; } = 5000;             // station distance from its star in light seconds; 0 = any
    public int MinSupply { get; init; } = 100;              // tons, one of VolumeSteps
    public int MinDemand { get; init; } = 100;
    public bool Carriers { get; init; }                     // include fleet carriers
    public string Surface { get; init; } = "odyssey";       // odyssey (all surface stations) | horizons (no settlements) | none
    public int CargoOverride { get; init; }                 // tons the hold takes; 0 = read from the ship
    public string SrcPower { get; init; } = "any";          // Powerplay filters, in the data service's spelling; "any" = not used
    public string DstPower { get; init; } = "any";
    public string SrcState { get; init; } = "any";
    public string DstState { get; init; } = "any";
    public string ListSort { get; init; } = "rank";         // order of the overlay menu's short trade list: a key of HaulMenu.ListSorts
    public IReadOnlyList<Preset> Presets { get; init; } = Starters;

    /// <summary>
    /// Not a setting, a marker saved with them: the Elite Haul Overlay's settings file these were
    /// copied from on the very first run, until the player has read the note that says so. No
    /// request and no preset can change it (<see cref="With"/>); <see cref="Hauling.CopyNoteRead"/> clears it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CopiedFrom { get; init; }

    /// <summary>
    /// The saved settings. On Studio's very first run there is no "market" section yet: the search
    /// settings of the Elite Haul Overlay are then copied in (its file is only read) and saved, so
    /// that happens once and what the player changes in Studio afterwards stays.
    /// </summary>
    internal static MarketSettings Load(Settings settings, string oldSettingsFile)
    {
        // Once "market" has been saved it is Studio's own, even when a hand edit left it unreadable
        // (the standard settings are used then): the old tool's file is copied from only on the very first run.
        if (settings.Has(Section)) return FromValues(settings.Get<Dictionary<string, JsonElement>>(Section));

        OldHaulSettings? old = OldHaulSettings.Read(oldSettingsFile);
        if (old is not null) Log.Info($"First run: search settings copied from {oldSettingsFile}");
        MarketSettings first = old is null ? new() : old.Market with { CopiedFrom = oldSettingsFile };
        try { settings.Set(Section, first); }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            Log.Error("The market settings could not be saved, so the first run's copy will be made again next time", problem);
        }
        return first;
    }

    /// <summary>
    /// These settings with some of them changed, named as in settings.json ("maxLy"), capitals not
    /// minded. Names that are not a setting are skipped, and so is "presets": presets have their own
    /// operations. Null when a value does not fit its setting (text for a number, say, or no value at all).
    /// </summary>
    public MarketSettings? With(IEnumerable<KeyValuePair<string, JsonElement>> changes) =>
        // The same presets list is put back: two settings records are equal only when they share that
        // list, and both "nothing changed, so nothing to save" and "this preset is in use" rest on that.
        // The first run's marker stays as it is: it is no setting.
        Merge(this, changes) is { } changed ? (changed with { Presets = Presets, CopiedFrom = CopiedFrom }).Tidied() : null;

    /// <summary>Using this preset now would change nothing: every setting it holds is already so.</summary>
    public bool IsUsing(Preset preset) => With(preset.Set) == this;

    /// <summary>The named settings as a preset holds them, in the order named. Names a preset may not hold are left out.</summary>
    internal IReadOnlyDictionary<string, JsonElement> Only(IEnumerable<string> names)
    {
        JsonElement all = JsonSerializer.SerializeToElement(this, Settings.Json);
        return names.Select(name => PresetKeys.FirstOrDefault(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .OfType<string>().Distinct().ToDictionary(key => key, key => all.GetProperty(key));
    }

    /// <summary>
    /// Presets as they may be kept: each with a name, holding only what a preset may hold in values
    /// that fit, and no more than there is room for.
    /// </summary>
    static IReadOnlyList<Preset> Tidy(IEnumerable<Preset?> presets)
    {
        var kept = new List<Preset>();
        foreach (Preset? preset in presets)
        {
            if (kept.Count >= MaxPresets) break;
            if (string.IsNullOrWhiteSpace(preset?.Name) || preset.Set is null) continue;
            // Through the settings and back, so every kept value is spelt the way Studio saves it.
            if (new MarketSettings().With(preset.Set.Where(value => PresetKeys.Contains(value.Key, StringComparer.OrdinalIgnoreCase))) is not { } fits) continue;
            IReadOnlyDictionary<string, JsonElement> set = fits.Only(preset.Set.Keys);
            if (set.Count > 0) kept.Add(new(preset.Name.Trim(), set));
        }
        return kept;
    }

    /// <summary>
    /// Lays named values over a settings record, matching its JSON names without minding capitals.
    /// Names the record does not have are skipped, and so are its lists. Null when a value does not fit.
    /// </summary>
    internal static T? Merge<T>(T onto, IEnumerable<KeyValuePair<string, JsonElement>> values) where T : class
    {
        JsonObject all = JsonSerializer.SerializeToNode(onto, Settings.Json)!.AsObject();
        foreach ((string name, JsonElement value) in values)
        {
            string? known = all.Select(entry => entry.Key).FirstOrDefault(key => key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (known is null || all[known] is not JsonValue) continue;
            // "null" fits no setting. Left in, it would be taken for text and come back as a setting that holds nothing at all.
            if (value.ValueKind == JsonValueKind.Null) return null;
            all[known] = JsonSerializer.SerializeToNode(value, Settings.Json);
        }
        try { return all.Deserialize<T>(Settings.Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Every value brought inside what the search understands. A hand-edited file or a web request
    /// may hold anything; a choice nobody offers falls back to the first one.
    /// </summary>
    MarketSettings Tidied() => this with
    {
        StartMode = Choice(StartMode, "near", "anywhere"),
        RankBy = Choice(RankBy, "hour", "trip"),
        Pad = Choice(Pad, "auto", "S", "M", "L"),
        Surface = Choice(Surface, "odyssey", "horizons", "none"),
        ListSort = Choice(ListSort, [.. HaulMenu.ListSorts.Select(sort => sort.Key)]),
        BuyLy = Math.Clamp(BuyLy, 0, 500),
        MaxLy = Math.Clamp(MaxLy, 1, 500),
        MaxAgeHours = Math.Max(1, MaxAgeHours),
        MaxStarLs = Math.Max(0, MaxStarLs),
        // supply and demand are picked from a list: a number in between moves to the choice below it
        MinSupply = VolumeSteps.Last(step => step <= Math.Max(0, MinSupply)),
        MinDemand = VolumeSteps.Last(step => step <= Math.Max(0, MinDemand)),
        CargoOverride = Math.Clamp(CargoOverride, 0, 2000),
        JumpRange = Math.Clamp(JumpRange, 0, 200),
        JumpEmpty = Math.Clamp(JumpEmpty, 0, 200),
        SrcPower = Named(SrcPower),
        DstPower = Named(DstPower),
        SrcState = Named(SrcState),
        DstState = Named(DstState),
    };

    static string Choice(string? value, params string[] choices) =>
        choices.FirstOrDefault(choice => choice.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? choices[0];

    static string Named(string? value) => string.IsNullOrWhiteSpace(value) ? "any" : value.Trim();

    static Dictionary<string, JsonElement> Values(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    /// <summary>
    /// The settings a settings file holds, Studio's own or the old tool's, tidied. They are taken
    /// value by value and preset by preset, so one that does not fit (a hand edit) costs only itself.
    /// </summary>
    static MarketSettings FromValues(IEnumerable<KeyValuePair<string, JsonElement>> values)
    {
        var market = new MarketSettings();
        JsonElement? presets = null;
        string? copiedFrom = null;
        foreach (KeyValuePair<string, JsonElement> value in values)
        {
            if (value.Key.Equals("presets", StringComparison.OrdinalIgnoreCase)) presets = value.Value;
            else if (value.Key.Equals("copiedFrom", StringComparison.OrdinalIgnoreCase)) copiedFrom = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
            else market = Merge(market, [value]) ?? market;
        }
        // No "presets" at all brings the two starters; an empty list means the player deleted them.
        return market.Tidied() with { Presets = presets is { } saved ? Tidy(ReadPresets(saved)) : Starters, CopiedFrom = copiedFrom };
    }

    /// <summary>
    /// The search settings in the Elite Haul Overlay's settings file, as that tool itself would have
    /// loaded them. <see cref="OldHaulSettings.Read"/> is the way in.
    /// </summary>
    /// <param name="entry">The file's value under a name, capitals not minded; null when it has none.</param>
    internal static MarketSettings FromOldFile(JsonElement old, Func<string, JsonElement?> entry)
    {
        MarketSettings market = FromValues(old.EnumerateObject().Select(value => KeyValuePair.Create(value.Name, value.Value)));
        // Version 1.0 had a yes/no "include Odyssey settlements" box; it is now one of the "surface stations" choices.
        return entry("settlements") is { } settlements && entry("surface") is null
            ? market with { Surface = settlements.ValueKind == JsonValueKind.True ? "odyssey" : "horizons" }
            : market;
    }

    static IEnumerable<Preset?> ReadPresets(JsonElement presets)
    {
        if (presets.ValueKind != JsonValueKind.Array) yield break;
        foreach (JsonElement preset in presets.EnumerateArray())
        {
            Preset? read = null;
            try { read = preset.Deserialize<Preset>(Settings.Json); }
            catch (JsonException) { }   // not a preset: left behind
            yield return read;
        }
    }
}

/// <summary>
/// The settings file of the Elite Haul Overlay, read the way that tool read it: every value it knew,
/// then every correction it made to files written by its own older versions. The file is only ever
/// read. Studio Market copies <see cref="Market"/> on its first run and the overlay copies
/// <see cref="Overlay"/> on its own, from the same reading, so the old tool's rules are written down once.
/// </summary>
/// <param name="Market">The search settings, the list order and the presets, ready to be Studio's own.</param>
/// <param name="Overlay">
/// Studio's standard overlay settings with the old tool's colour, background and "only while Elite
/// is in front" in them, not yet brought inside what Studio's overlay takes.
/// </param>
public sealed record OldHaulSettings(MarketSettings Market, OverlaySettings Overlay)
{
    /// <summary>Where the Elite Haul Overlay keeps its settings.</summary>
    public static string DefaultFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Elite Haul Overlay", "settings.json");

    /// <summary>Null when the old tool was never used here, or its file cannot be read (that is logged).</summary>
    public static OldHaulSettings? Read(string file)
    {
        JsonElement old;
        try
        {
            if (!File.Exists(file)) return null;
            using JsonDocument parsed = JsonDocument.Parse(File.ReadAllText(file));
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("it does not hold settings");
            old = parsed.RootElement.Clone();
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error($"{file} could not be read, so nothing was taken from the Elite Haul Overlay", problem);
            return null;
        }

        JsonElement? Entry(string name) =>
            old.EnumerateObject().Where(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(entry => (JsonElement?)entry.Value).FirstOrDefault();

        // Value by value, so one that does not fit costs only itself. Only what Studio's boxes share with the old tool's one
        // box is there to take: the colour, the background, and whether it shows over other programs. Its keys, place and
        // size have no counterpart.
        var box = new OverlaySettings();
        foreach (JsonProperty value in old.EnumerateObject())
            if (new[] { "accent", "opacity", "onlyWhenElite" }.Contains(value.Name, StringComparer.OrdinalIgnoreCase))
                box = MarketSettings.Merge(box, [new(value.Name, value.Value)]) ?? box;
        return new(MarketSettings.FromOldFile(old, Entry), box);
    }
}

/// <summary>Whether what was asked for was done, with a line for the player saying so.</summary>
public sealed record Outcome(bool Ok, string Message);

/// <summary>One page of the overlay menu: the name code uses and the title on its tab.</summary>
public sealed record MenuPage(string Key, string Title);

/// <summary>One way to order the menu's short trade list. The column title's arrow points down when the biggest comes first.</summary>
public sealed record ListSort(string Key, string Name, bool BiggestFirst);

/// <summary>One value a setting can be stepped to, and how it reads.</summary>
public sealed record SettingChoice(object Value, string Text);

/// <summary>One line of the menu's settings page as it stands now.</summary>
/// <param name="Key">The setting's name in settings.json; what <see cref="Hauling.StepSetting"/> takes.</param>
/// <param name="Text">The value in use, in words. A line that is not usable says why instead.</param>
/// <param name="Usable">False when the setting has no effect at the moment and so cannot be changed.</param>
public sealed record SettingLine(string Key, string Label, string Text, bool Usable, IReadOnlyList<SettingChoice> Choices);

/// <summary>One line of the menu's presets page.</summary>
/// <param name="Text">What the preset sets, in a few words.</param>
/// <param name="Active">The settings in use are this preset's.</param>
public sealed record PresetLine(string Name, string Text, bool Active);

/// <summary>
/// What the overlay menu holds, as plain data and rules. Open to the overlay are the fixed parts:
/// its pages, the orders of its short trade list and which five of the list's lines show. The
/// settings it steps through fixed choices, the sorted list and the presets' words are reached
/// through <see cref="Hauling"/>, which gives them for the settings in use and saves what is
/// changed; the Market page can build its drop-down lists from the same lines. Which line is lit,
/// whether the menu is open and how it is drawn belong to the overlay.
/// </summary>
public static class HaulMenu
{
    /// <summary>Trades the short list shows at a time.</summary>
    public const int ListRows = 5;

    public static readonly IReadOnlyList<MenuPage> Pages = [new("trades", "TRADES"), new("settings", "SETTINGS"), new("presets", "PRESETS")];

    public static readonly IReadOnlyList<ListSort> ListSorts =
    [
        new("rank", "Best first", false), new("hour", "Profit per hour", true), new("ton", "Profit per ton", true),
        new("load", "Profit per trip", true), new("jumps", "Fewest jumps", false), new("ly", "Shortest trip", false),
        new("age", "Newest prices", false),
    ];

    /// <param name="Wrap">Stepping past the last choice comes round to the first.</param>
    sealed record Field(string Key, string Label, bool Wrap, object[] Choices, Func<MarketSettings, object> Value, Func<object, string> Text);

    static readonly Field[] Fields =
    [
        new("returns", "Trip", true, [true, false], s => s.Returns,
            v => (bool)v ? "Round trip (bring a load back)" : "One way (pick up and drop off)"),
        new("rankBy", "Rank trades by", true, ["hour", "trip"], s => s.RankBy,
            v => (string)v == "trip" ? "Profit per trip" : "Profit per hour"),
        new("maxLy", "Max. route distance", false, [10, 15, 20, 30, 40, 60, 80, 100, 120, 160, 200, 300], s => s.MaxLy,
            v => $"{v} ly, buy to sell"),
        new("buyLy", "Buy within", false, [0, 5, 10, 15, 20, 25, 30, 40, 50, 75, 100], s => s.BuyLy,
            v => (int)v <= 0 ? "The system I am in" : $"{v} ly of me"),
        new("maxAgeHours", "Max. price age", false, [1, 2, 3, 4, 6, 8, 12, 24, 48, 72, 168, 720], s => s.MaxAgeHours,
            v => HoursText((int)v)),
        new("startMode", "Trades start", true, ["near", "anywhere"], s => s.StartMode,
            v => (string)v == "anywhere" ? "Anywhere in the galaxy" : "Near me"),
        new("minSupply", "Min. supply", false, [.. MarketSettings.VolumeSteps], s => s.MinSupply, VolumeText),
        new("minDemand", "Min. demand", false, [.. MarketSettings.VolumeSteps], s => s.MinDemand, VolumeText),
    ];

    /// <summary>The settings page for these settings, line by line.</summary>
    internal static IReadOnlyList<SettingLine> SettingLines(MarketSettings settings) =>
    [
        .. Fields.Select(field => new SettingLine(
            field.Key, field.Label,
            Usable(field, settings) ? field.Text(field.Value(settings)) : "Not used when trades start anywhere",
            Usable(field, settings),
            [.. field.Choices.Select(choice => new SettingChoice(choice, field.Text(choice)))])),
    ];

    /// <summary>
    /// The choice one step before (-1) or after (+1) the value in use, or null when there is nothing
    /// to change: no such line, a line that is not usable, or already at the end of a list that
    /// does not come round.
    /// </summary>
    internal static object? NextChoice(MarketSettings settings, string key, int direction)
    {
        Field? field = Fields.FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (field is null || !Usable(field, settings)) return null;
        object now = field.Value(settings);
        object[] choices = field.Choices;
        int to = Array.IndexOf(choices, now);
        if (to >= 0) to += direction;
        else
        {
            // A number typed on the Market page that sits between two steps: the step just above or just below it.
            to = direction > 0 ? choices.Length - 1 : 0;
            if (now is int typed)
            {
                int near = direction > 0 ? Array.FindIndex(choices, choice => (int)choice > typed) : Array.FindLastIndex(choices, choice => (int)choice < typed);
                if (near >= 0) to = near;
            }
        }
        to = field.Wrap ? (to % choices.Length + choices.Length) % choices.Length : Math.Clamp(to, 0, choices.Length - 1);
        return Equals(choices[to], now) ? null : choices[to];
    }

    /// <summary>The list order one step before (-1) or after (+1) this one; it comes round at both ends.</summary>
    internal static string NextSort(string listSort, int direction)
    {
        int at = Math.Max(0, ListSorts.ToList().FindIndex(sort => sort.Key == listSort));
        return ListSorts[((at + direction) % ListSorts.Count + ListSorts.Count) % ListSorts.Count].Key;
    }

    /// <summary>The trades in one of the <see cref="ListSorts"/> orders. Equal trades stay in the search's own order.</summary>
    internal static IReadOnlyList<TradeRow> Sorted(IReadOnlyList<TradeRow> trades, string listSort) => listSort switch
    {
        "hour" => [.. trades.OrderByDescending(t => t.PerHour).ThenBy(t => t.Rank)],
        "ton" => [.. trades.OrderByDescending(t => t.ProfitPerTon).ThenBy(t => t.Rank)],
        "load" => [.. trades.OrderByDescending(t => t.RoundTripProfit).ThenBy(t => t.Rank)],
        "jumps" => [.. trades.OrderBy(t => t.JumpsToBuy + t.JumpsOut + t.JumpsBack).ThenBy(t => t.Rank)],
        "ly" => [.. trades.OrderBy(t => t.DistanceLy).ThenBy(t => t.Rank)],
        "age" => [.. trades.OrderBy(t => t.AgeHours).ThenBy(t => t.Rank)],
        _ => trades,
    };

    /// <summary>
    /// Which line of the sorted list is the first of the <see cref="ListRows"/> on show, so that the
    /// lit line stays in view. <paramref name="top"/> is the first line shown until now and
    /// <paramref name="lit"/> the lit line, both counted from 0; -1 when no trade line is lit.
    /// </summary>
    public static int WindowTop(int top, int lit, int count)
    {
        if (lit >= 0 && lit < top) top = lit;
        else if (lit >= top + ListRows) top = lit - ListRows + 1;
        return Math.Max(0, Math.Min(top, count - ListRows));
    }

    /// <summary>What a preset sets, in a few words: "one way · routes to 40 ly · ranked by profit per hour".</summary>
    internal static string PresetText(Preset preset)
    {
        if (new MarketSettings().With(preset.Set) is not { } values) return "";
        bool Has(string key) => preset.Set.Keys.Contains(key, StringComparer.OrdinalIgnoreCase);
        var bits = new List<string>();
        if (Has("returns")) bits.Add(values.Returns ? "round trip" : "one way");
        if (Has("startMode") && values.StartMode == "anywhere") bits.Add("anywhere");
        else if (Has("buyLy")) bits.Add($"buy within {values.BuyLy} ly");
        if (Has("maxLy")) bits.Add($"routes to {values.MaxLy} ly");
        if (Has("maxAgeHours")) bits.Add($"prices under {HoursText(values.MaxAgeHours)}");
        if (Has("rankBy")) bits.Add(values.RankBy == "trip" ? "ranked by trip profit" : "ranked by profit per hour");
        return string.Join(" · ", bits);
    }

    /// <summary>"12 h" up to two days, "3 days" from there.</summary>
    public static string HoursText(int hours) => hours < 48 ? $"{hours} h" : $"{(int)Math.Round(hours / 24.0)} days";

    // "anywhere" has no distance from the player
    static bool Usable(Field field, MarketSettings settings) => !(field.Key == "buyLy" && settings.StartMode == "anywhere");

    static string VolumeText(object tons) => (int)tons <= 0 ? "Any" : ((int)tons).ToString("N0", CultureInfo.InvariantCulture) + " t or more";
}
