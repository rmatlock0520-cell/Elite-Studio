using System.Text.Json;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.Loadouts;

/// <summary>
/// The engineering planner's routes, all under /api/loadouts/engineering/, and its "engineering" event.
///   GET  blueprints   what can be pinned: {"kinds":[{"code","name","blueprints":[{"code","name","grades":[1,2,...]}],"experimentals":[{"code","name"}]}]}
///   GET  plan         the pinned blueprints, what they take and what is missing (<see cref="EngineeringPlan"/>)
///   POST pin          {"module":"fsd","blueprint":"FSD_LongRange","grade":5,"experimental":"special_fsd_lightweight"} ("" for no effect)
///   POST unpin        {"id":3}
///   POST rolls        {"rolls":[1,2,3,4,5]}: how many rolls to plan for each grade
/// The three POSTs answer with the same <see cref="EngineeringPlan"/>, and so does every "engineering"
/// event, sent whenever any of it changes: a pin, or the game (materials picked up, an engineer unlocked, a jump).
/// (A whole build's engineering is pinned from the "My ship" page: POST /api/loadouts/pin, see <see cref="LoadoutsApi"/>.)
/// </summary>
public static class EngineeringApi
{
    sealed class PinBody
    {
        public string Module { get; set; } = "";
        public string Blueprint { get; set; } = "";
        public int Grade { get; set; }
        public string Experimental { get; set; } = "";
    }

    sealed class UnpinBody
    {
        public int Id { get; set; }
    }

    sealed class RollsBody
    {
        public int[] Rolls { get; set; } = [];
    }

    /// <summary>Registers the routes. Call on the UI thread, which is also where the server runs them.</summary>
    /// <returns>The planner, for whoever else pins blueprints (the "My ship" page, for a whole build).</returns>
    public static EngineeringPlanner Map(WebServer web, GameState game, Settings settings)
    {
        var planner = new EngineeringPlanner(game, settings);
        planner.Changed += () => web.Send("engineering", planner.View());

        web.Map("GET", "/api/loadouts/engineering/blueprints", _ => WebResponse.Json(new
        {
            kinds = EngineeringData.Kinds.Select(kind => new
            {
                kind.Code,
                kind.Name,
                blueprints = kind.Blueprints.OrderBy(blueprint => blueprint.Name, StringComparer.Ordinal)
                    .Select(blueprint => new { blueprint.Code, blueprint.Name, grades = blueprint.Grades.Select(grade => grade.Number) }),
                experimentals = kind.Experimentals.Select(effect => new { effect.Code, effect.Name }),
            }),
        }));

        web.Map("GET", "/api/loadouts/engineering/plan", _ => WebResponse.Json(planner.View()));

        web.Map("POST", "/api/loadouts/engineering/pin", request =>
        {
            PinBody body = request.Read<PinBody>();
            return planner.Pin(body.Module, body.Blueprint, body.Grade, body.Experimental)
                ? WebResponse.Json(planner.View())
                : WebResponse.Error("Choose a module, a blueprint and a grade that an engineer offers.", 400);
        });

        web.Map("POST", "/api/loadouts/engineering/unpin", request =>
        {
            planner.Unpin(request.Read<UnpinBody>().Id);
            return WebResponse.Json(planner.View());
        });

        web.Map("POST", "/api/loadouts/engineering/rolls", request =>
            planner.SetRolls(request.Read<RollsBody>().Rolls)
                ? WebResponse.Json(planner.View())
                : WebResponse.Error($"Give a number of rolls from 1 to {EngineeringPlanner.MostRolls} for each of the five grades.", 400));

        return planner;
    }
}

/// <summary>One blueprint the player means to have done: a kind of module, a blueprint, the grade to reach and, perhaps, an experimental effect.</summary>
/// <param name="Module">coriolis-data's short name for the kind of module: "fsd".</param>
/// <param name="Experimental">The effect's code; "" for none.</param>
public sealed record EngineeringPin(int Id, string Module, string Blueprint, int Grade, string Experimental);

/// <summary>The "engineering" section of settings.json.</summary>
public sealed class EngineeringSettings
{
    public List<EngineeringPin> Pins { get; set; } = [];
    public int[] Rolls { get; set; } = [.. EngineeringData.UsualRolls];
}

/// <summary>A material one pin takes.</summary>
/// <param name="Code">The game's code for the material: "ChemicalProcessors".</param>
/// <param name="Kind">"Raw", "Manufactured" or "Encoded".</param>
/// <param name="Grade">How rare it is, 1 to 5.</param>
public sealed record PlannedMaterial(string Code, string Name, string Kind, int Grade, int Need, int Have);

/// <summary>A trade at a material trader that fills a gap: give this many of this material, get that many of the one missing.</summary>
public sealed record MaterialSwap(string Code, string Name, int Grade, int Give, int Get);

/// <summary>A material over all the pins together.</summary>
/// <param name="Short">How many are missing; 0 when enough are held.</param>
/// <param name="Trader">The kind of material trader that deals in it, in the words Find by name uses; null for the few materials no trader takes.</param>
/// <param name="Swap">
/// When it is short and the commander holds enough of another grade in the same trade category,
/// over and above what the pins themselves take: the trade that fills the gap. All the swaps of a
/// plan can be made together: none counts on materials another one gives away.
/// </param>
public sealed record MaterialTotal(string Code, string Name, string Kind, int Grade, int Need, int Have, int Short, string? Trader, MaterialSwap? Swap);

/// <summary>An engineer who offers a pin's grade, where they work, and how the commander stands with them.</summary>
/// <param name="Ly">Light years from the system the ship is in; null while that is not known.</param>
/// <param name="Progress">"Unlocked", "Invited", "Known", or "" for an engineer the commander has not heard of.</param>
/// <param name="Rank">The highest grade the engineer offers the commander; 0 until unlocked.</param>
public sealed record PlannedEngineer(long Id, string Name, string System, string Base, double? Ly, string Progress, int Rank);

/// <summary>A pin as the page shows it.</summary>
/// <param name="Module">The kind of module, in words.</param>
/// <param name="Blueprint">The blueprint, in words.</param>
/// <param name="Experimental">The experimental effect, in words; null for none.</param>
/// <param name="Materials">What the rolls for every grade up to <paramref name="Grade"/> take, and the effect.</param>
/// <param name="Engineers">Those who offer <paramref name="Grade"/> itself: the ones working for the commander first, then the nearest.</param>
public sealed record PinView(
    int Id, string Module, string Blueprint, int Grade, string? Experimental,
    IReadOnlyList<PlannedMaterial> Materials, IReadOnlyList<PlannedEngineer> Engineers);

/// <summary>Everything the planner's page shows: the answer to GET plan and the data of every "engineering" event.</summary>
/// <param name="Rolls">The rolls planned for grades 1 to 5.</param>
/// <param name="Totals">Every material the pins take, raw first, then manufactured, then encoded; within a kind by grade.</param>
/// <param name="MaterialsKnown">False until the game has told what the commander holds: every "have" is 0 until then.</param>
public sealed record EngineeringPlan(IReadOnlyList<int> Rolls, IReadOnlyList<PinView> Pins, IReadOnlyList<MaterialTotal> Totals, bool MaterialsKnown);

/// <summary>
/// The pinned blueprints and what they come to, against what the commander holds. Lives on the UI
/// thread like the game state it reads. The picture is worked out again whenever the game state
/// changes; <see cref="Changed"/> is raised only when it came out different.
/// </summary>
public sealed class EngineeringPlanner
{
    /// <summary>The most rolls the planner takes for one grade: far beyond what any grade needs, and it keeps the sums small.</summary>
    public const int MostRolls = 50;

    readonly GameState game;
    readonly Settings settings;
    readonly List<EngineeringPin> pins;
    int[] rolls;
    string told;   // the picture as it was last told, to see whether a change in the game changed it

    public EngineeringPlanner(GameState game, Settings settings)
    {
        (this.game, this.settings) = (game, settings);
        // The saved pins are a file on disk: from an older Studio, changed by hand, or naming a blueprint
        // the game's tables no longer have. What cannot be used is dropped, never a reason not to start.
        EngineeringSettings saved = settings.Get<EngineeringSettings>("engineering");
        pins = [.. (saved.Pins ?? []).Where(pin => pin is not null && Find(pin.Module, pin.Blueprint, pin.Grade, pin.Experimental) is not null)];
        rolls = Usable(saved.Rolls) ? saved.Rolls : [.. EngineeringData.UsualRolls];
        told = JsonSerializer.Serialize(View());
        game.Changed += () => Tell(save: false);
    }

    /// <summary>Raised after the picture changed: a pin, the rolls, or something in the game that shows in it.</summary>
    public event Action? Changed;

    /// <summary>Pins a blueprint. False when no engineer offers it as asked.</summary>
    public bool Pin(string module, string blueprint, int grade, string experimental)
    {
        if (Find(module, blueprint, grade, experimental) is null) return false;
        pins.Add(new EngineeringPin(pins.Count == 0 ? 1 : pins.Max(pin => pin.Id) + 1, module, blueprint, grade, experimental));
        Tell(save: true);
        return true;
    }

    /// <summary>
    /// Pins the engineering of a whole build: for every engineered module its blueprint to the
    /// grade it has, with its experimental effect. A build names a blueprint by the game's code
    /// and a module by its own; the pin is made in the planner's terms, the kind of module and its
    /// recipe. What is pinned just so already is not pinned again, so asking twice adds nothing;
    /// two modules alike still get a pin each.
    /// </summary>
    /// <returns>How many pins were added, how many were there already, and the modules whose engineering no engineer offers, in words.</returns>
    public (int Added, int Already, IReadOnlyList<string> NotOffered) PinBuild(ShipLoadout build)
    {
        var wanted = new List<(string Module, string Blueprint, int Grade, string Experimental)>();
        var notOffered = new List<string>();
        foreach (ShipModule module in build.Modules)
        {
            if (module.Engineering is not { } work) continue;
            EngineeringData.ModuleKind? kind = ShipData.Find(module.Item) is { } part ? EngineeringData.Kinds.FirstOrDefault(k => k.Code == part.Group) : null;
            EngineeringData.Blueprint? blueprint = kind is null ? null : EngineeringData.BlueprintOf(kind, work.Blueprint);
            // A grade beyond what anybody offers (a module that came ready-made) is planned as far as engineers go.
            if (blueprint?.Grades.LastOrDefault(grade => grade.Number <= work.Level) is not { } grade)
            {
                notOffered.Add($"{Names.Module(module.Item).Name}: {Names.Blueprint(work.Blueprint)}");
                continue;
            }
            // An effect from before today's engineering has no recipe: the blueprint is pinned without it.
            string effect = kind!.Experimentals.FirstOrDefault(e => e.Code.Equals(work.Experimental, StringComparison.OrdinalIgnoreCase))?.Code ?? "";
            wanted.Add((kind.Code, blueprint.Code, grade.Number, effect));
        }

        int added = 0;
        foreach (var alike in wanted.GroupBy(pin => pin))
        {
            var (module, blueprint, grade, effect) = alike.Key;
            int there = pins.Count(pin => (pin.Module, pin.Blueprint, pin.Grade, pin.Experimental) == alike.Key);
            for (int n = there; n < alike.Count(); n++, added++)
                pins.Add(new EngineeringPin(pins.Count == 0 ? 1 : pins.Max(pin => pin.Id) + 1, module, blueprint, grade, effect));
        }
        if (added > 0) Tell(save: true);
        return (added, wanted.Count - added, notOffered);
    }

    public void Unpin(int id)
    {
        if (pins.RemoveAll(pin => pin.Id == id) > 0) Tell(save: true);
    }

    /// <summary>Sets the rolls planned for grades 1 to 5. False when these are not five numbers from 1 to <see cref="MostRolls"/>.</summary>
    public bool SetRolls(int[] perGrade)
    {
        if (!Usable(perGrade)) return false;
        rolls = perGrade;
        Tell(save: true);
        return true;
    }

    public EngineeringPlan View()
    {
        var need = new Dictionary<string, int>();   // over all the pins
        var views = new List<PinView>();
        foreach (EngineeringPin pin in pins)
        {
            var (kind, blueprint, effect) = Find(pin.Module, pin.Blueprint, pin.Grade, pin.Experimental)!.Value;
            // Every grade on the way up is rolled the planned number of times; the effect is put on once.
            var takes = new Dictionary<string, int>();
            foreach (EngineeringData.Grade grade in blueprint.Grades.Where(grade => grade.Number <= pin.Grade)) Add(takes, grade.Cost, rolls[grade.Number - 1]);
            if (effect is not null) Add(takes, effect.Cost, 1);
            Add(need, takes, 1);

            views.Add(new PinView(
                pin.Id, kind.Name, blueprint.Name, pin.Grade, effect?.Name,
                [.. InOrder(takes.Keys).Select(m => new PlannedMaterial(m.Symbol, m.Name, m.Kind, m.Grade, takes[m.Symbol], Held(m)))],
                [
                    .. blueprint.Grades.Single(grade => grade.Number == pin.Grade).Engineers.Select(Engineer)
                        .OrderByDescending(e => e.Progress switch { "Unlocked" => 10 + e.Rank, "Invited" => 2, "Known" => 1, _ => 0 })
                        .ThenBy(e => e.Ly ?? double.MaxValue),
                ]));
        }

        // What is held beyond the pins' own needs is free to be traded; each swap found takes its share, so the next cannot count on it too.
        Dictionary<string, int> spare = GameData.Materials.Values.ToDictionary(m => m.Symbol, m => Held(m) - need.GetValueOrDefault(m.Symbol));
        Dictionary<string, MaterialSwap?> swaps = InOrder(need.Keys).Where(m => spare[m.Symbol] < 0)
            .OrderByDescending(m => m.Grade)   // the rarest first: it has the fewest ways to be filled
            .ToDictionary(m => m.Symbol, m => SwapFor(m, -spare[m.Symbol], spare));
        MaterialTotal[] totals =
        [
            .. InOrder(need.Keys).Select(m => new MaterialTotal(
                m.Symbol, m.Name, m.Kind, m.Grade, need[m.Symbol], Held(m), Math.Max(0, need[m.Symbol] - Held(m)),
                m.Category == "None" ? null : $"{m.Kind} material trader", swaps.GetValueOrDefault(m.Symbol))),
        ];
        return new EngineeringPlan(rolls, views, totals, game.Materials.Count > 0);
    }

    /// <summary>The kind of module, the blueprint and the effect a pin names; null when no engineer offers that.</summary>
    static (EngineeringData.ModuleKind Kind, EngineeringData.Blueprint Blueprint, EngineeringData.Experimental? Effect)? Find(string? module, string? blueprint, int grade, string? experimental)
    {
        if (EngineeringData.Kinds.FirstOrDefault(k => k.Code == module) is not { } kind) return null;
        if (kind.Blueprints.FirstOrDefault(b => b.Code == blueprint) is not { } found || !found.Grades.Any(g => g.Number == grade)) return null;
        EngineeringData.Experimental? effect = kind.Experimentals.FirstOrDefault(e => e.Code == experimental);
        return string.IsNullOrEmpty(experimental) || effect is not null ? (kind, found, effect) : null;
    }

    static bool Usable(int[]? perGrade) => perGrade is { Length: 5 } && perGrade.All(n => n is >= 1 and <= MostRolls);

    static void Add(Dictionary<string, int> sum, IReadOnlyDictionary<string, int> cost, int times)
    {
        foreach ((string code, int count) in cost) sum[code] = sum.GetValueOrDefault(code) + count * times;
    }

    /// <summary>Materials by their codes, in the order the page lists them: raw, manufactured, encoded; then by grade and name.</summary>
    static IEnumerable<GameData.Material> InOrder(IEnumerable<string> codes) =>
        codes.Select(code => GameData.Materials[code]).OrderBy(m => Array.IndexOf(GameState.MaterialKinds, m.Kind)).ThenBy(m => m.Grade).ThenBy(m => m.Name);

    int Held(GameData.Material material) => game.Materials.GetValueOrDefault(material.Symbol);

    PlannedEngineer Engineer(long id)
    {
        EngineeringData.Place place = EngineeringData.Places[id];
        EngineerStanding? standing = game.Engineers.GetValueOrDefault(id);
        return new PlannedEngineer(
            id, GameData.Engineers.First(engineer => engineer.Id == id).Name, place.System, place.Base,
            game.StarPos is { } here ? Math.Round(ShipMath.Distance(here, [place.X, place.Y, place.Z]), 1) : null,
            standing?.Progress ?? "", standing?.Rank ?? 0);
    }

    /// <summary>
    /// The trade at a material trader that fills a gap from one other material of the same trade
    /// category, or null when none held is enough. The nearest grade is tried first, and of two
    /// equally near the higher one: going down the grades is the cheap direction.
    /// </summary>
    /// <param name="spare">What is free to be traded, by material code; the swap found is taken off it.</param>
    static MaterialSwap? SwapFor(GameData.Material missing, int shortBy, Dictionary<string, int> spare)
    {
        if (missing.Category == "None") return null;
        foreach (GameData.Material other in GameData.Materials.Values
            .Where(m => m.Kind == missing.Kind && m.Category == missing.Category && m.Grade != missing.Grade)
            .OrderBy(m => Math.Abs(m.Grade - missing.Grade)).ThenByDescending(m => m.Grade))
        {
            int steps = other.Grade - missing.Grade;
            int rate = (int)Math.Pow(steps > 0 ? EngineeringData.DownRate : EngineeringData.UpRate, Math.Abs(steps));
            // Down the grades one buys several, so the last trade may bring more than was missing; up the grades several buy one.
            int give = steps > 0 ? (shortBy + rate - 1) / rate : shortBy * rate;
            if (give > spare[other.Symbol]) continue;
            spare[other.Symbol] -= give;
            return new MaterialSwap(other.Symbol, other.Name, other.Grade, give, steps > 0 ? give * rate : shortBy);
        }
        return null;
    }

    /// <summary>Tells the pages when the picture is not the one told last, and saves the pins when they are what changed.</summary>
    void Tell(bool save)
    {
        if (save)
        {
            try
            {
                settings.Set("engineering", new EngineeringSettings { Pins = pins, Rolls = rolls });
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                Log.Error("The pinned blueprints could not be saved, so they will not survive a restart", problem);
            }
        }
        string now = JsonSerializer.Serialize(View());
        if (now == told) return;
        told = now;
        Changed?.Invoke();
    }
}
