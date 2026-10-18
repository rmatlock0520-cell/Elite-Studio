using System.Text;
using System.Text.Json;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Loadouts;
using WebRequest = EliteStudio.App.WebRequest;     // System.Net has older types with the same names
using WebResponse = EliteStudio.App.WebResponse;

namespace EliteStudio.Tests;

/// <summary>
/// The Guardian helper: its table of recipes against the game's own tables, the checklist against a
/// commander, and the /api/loadouts/guardian/ route. The commander is the owner's, from the
/// "Engineered" recording: the Guardian materials are the ones held on 8 October 2026, with the
/// ship in HIP 12099. No recording holds a technology-broker line, so the unlocks here are written
/// in the shape the community's journal schemas give that line (github.com/jixxed/ed-journal-schemas).
/// </summary>
public sealed class GuardianTests : IDisposable
{
    const string Engineered = "Journal.2026-10-08T080236.01.log";
    const string BoosterUnlocked = """{ "timestamp":"2026-10-08T13:00:00Z", "event":"TechnologyBroker", "BrokerType":"guardian", "MarketID":3223343616, "ItemsUnlocked":[ { "Name":"Int_GuardianFSDBooster_Size1", "Name_Localised":"Guardian FSD Booster" }, { "Name":"Int_GuardianFSDBooster_Size2", "Name_Localised":"Guardian FSD Booster" }, { "Name":"Int_GuardianFSDBooster_Size3", "Name_Localised":"Guardian FSD Booster" }, { "Name":"Int_GuardianFSDBooster_Size4", "Name_Localised":"Guardian FSD Booster" }, { "Name":"Int_GuardianFSDBooster_Size5", "Name_Localised":"Guardian FSD Booster" } ], "Commodities":[ { "Name":"hnshockmount", "Name_Localised":"HN Shock Mount", "Count":8 } ], "Materials":[ { "Name":"guardian_moduleblueprint", "Name_Localised":"Guardian Module Blueprint Fragment", "Count":1, "Category":"Encoded" }, { "Name":"guardian_powercell", "Name_Localised":"Guardian Power Cell", "Count":21, "Category":"Manufactured" }, { "Name":"guardian_techcomponent", "Name_Localised":"Guardian Technology Component", "Count":21, "Category":"Manufactured" }, { "Name":"focuscrystals", "Name_Localised":"Focus Crystals", "Count":24, "Category":"Manufactured" } ] }""";

    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioGuardian-").FullName;
    readonly PracticeGame game = new();

    public void Dispose()
    {
        game.Dispose();
        Directory.Delete(temp, recursive: true);
    }

    Settings SettingsFile() => new(Path.Combine(temp, "settings.json"));

    GuardianSettings Saved() => SettingsFile().Get<GuardianSettings>("guardian");

    static string Line(GuardianItem item) =>
        $"{item.Name} ({item.Detail}){(item.Unlocked ? " UNLOCKED" : "")}: " + string.Join(", ", item.Recipe.Select(part => $"{part.Need} {part.Name} [{part.Kind}, have {part.Have}]"));

    // ------------------------------------------------------------ the table of recipes

    [Fact]
    public void The_recipes_name_real_modules_materials_and_commodities_and_add_up_as_the_wiki_says()
    {
        GuardianHelper.Unlock[] unlocks = GuardianHelper.Unlocks;
        Assert.Equal(new[] { ("Modules", 6), ("Weapons", 14), ("Fighters", 3) }, unlocks.GroupBy(unlock => unlock.Group).Select(group => (group.Key, group.Count())));
        Assert.Equal(unlocks.Length, unlocks.Select(unlock => unlock.Code).Distinct().Count());

        foreach (GuardianHelper.Unlock unlock in unlocks)
        {
            // A blueprint segment first, then four or (for the small weapons) three other things, each a material or a commodity the game has.
            Assert.EndsWith("Blueprint", unlock.Recipe[0].Code);
            Assert.InRange(unlock.Recipe.Length, 4, 5);
            Assert.All(unlock.Recipe, part => Assert.True(GameData.Materials.ContainsKey(part.Code) != GameData.Commodities.ContainsKey(part.Code), part.Code));
            Assert.Equal(unlock.Recipe.Length, unlock.Recipe.Select(part => part.Code).Distinct().Count());

            // The game's own module table knows every weapon by this code, name, mount and size, and every module in these sizes.
            if (unlock.Group == "Weapons")
            {
                GameData.Module weapon = GameData.Modules[unlock.Code];
                Assert.Equal((unlock.Name, unlock.Detail), (weapon.Name, $"{weapon.Mount}, {new[] { "small", "medium", "large" }[weapon.Class - 1]}"));
            }
            else if (unlock.Group == "Modules")
            {
                GameData.Module[] sizes = [.. GameData.Modules.Values.Where(module => module.Symbol.StartsWith(unlock.Code + "_Size", StringComparison.OrdinalIgnoreCase))];
                Assert.All(sizes, size => Assert.Equal(unlock.Name, size.Name));
                Assert.Equal(unlock.Detail, $"Sizes {sizes.Min(size => size.Class)} to {sizes.Max(size => size.Class)}");
            }
        }
        // The game sells no Guardian weapon or module that the table lacks.
        Assert.Equal(
            GameData.Modules.Values.Where(module => module.Symbol.Contains("Guardian", StringComparison.OrdinalIgnoreCase)).Select(module => module.Symbol.Split("_Size")[0]).Distinct().Order(),
            unlocks.Where(unlock => unlock.Group != "Fighters").Select(unlock => unlock.Code).Order());

        // The wiki's pages on the three segments say how many it takes to unlock everything: 18, 6 and 3.
        int Segments(string code) => unlocks.Sum(unlock => unlock.Recipe.Where(part => part.Code == code).Sum(part => part.Count));
        Assert.Equal((18, 6, 3), (Segments("Guardian_WeaponBlueprint"), Segments("Guardian_ModuleBlueprint"), Segments("Guardian_VesselBlueprint")));
    }

    // ------------------------------------------------------------ the checklist against a commander

    [Fact]
    public void The_checklist_shows_what_is_held_toward_each_recipe_and_follows_an_unlock()
    {
        string journal = game.Journal(Engineered);
        var settings = SettingsFile();
        using GameWatcher watcher = game.Watch();
        var helper = new GuardianHelper(watcher.State, settings);
        int told = 0;
        helper.Changed += () => told++;
        Assert.False(helper.View().HistoryRead);   // nothing read yet

        watcher.Poll();

        Assert.Equal(1, told);
        GuardianView view = helper.View();
        Assert.True(view.HistoryRead);   // there is no older journal to search
        Assert.Equal(23, view.Items.Count);
        Assert.DoesNotContain(view.Items, item => item.Unlocked);
        Assert.Equal(new[]
        {
            "Guardian FSD Booster (Sizes 1 to 5): 1 Guardian Module Blueprint Segment [Encoded, have 0], 21 Guardian Power Cell [Manufactured, have 93], 21 Guardian Technology Component [Manufactured, have 6], 24 Focus Crystals [Manufactured, have 6], 8 HN Shock Mount [Commodity, have 0]",
            "Guardian Hybrid Power Plant (Sizes 2 to 8): 1 Guardian Module Blueprint Segment [Encoded, have 0], 18 Guardian Power Conduit [Manufactured, have 72], 21 Pattern Epsilon Obelisk Data [Encoded, have 6], 15 Heat Resistant Ceramics [Manufactured, have 6], 10 Energy Grid Assembly [Commodity, have 0]",
        }, view.Items.Take(2).Select(Line));
        Assert.Equal(
            "Guardian Gauss Cannon (Fixed, medium): 1 Guardian Weapon Blueprint Segment [Encoded, have 0], 18 Guardian Power Cell [Manufactured, have 93], 20 Guardian Technology Component [Manufactured, have 6], 15 Manganese [Raw, have 165], 6 Magnetic Emitter Coil [Commodity, have 0]",
            Line(view.Items[7]));
        Assert.Equal(
            "XG7 Trident (Ship-launched fighter): 1 Guardian Vessel Blueprint Segment [Encoded, have 0], 25 Guardian Power Cell [Manufactured, have 93], 26 Pattern Epsilon Obelisk Data [Encoded, have 6], 18 Pattern Beta Obelisk Data [Encoded, have 9], 25 Guardian Technology Component [Manufactured, have 6]",
            Line(view.Items[20]));

        // Where the segments come from: everything is locked, so all 18, 6 and 3 are still to be found. Distances are from HIP 12099.
        Assert.Equal(new[]
        {
            ("Guardian_WeaponBlueprint", "Guardian Weapon Blueprint Segment", 0, 18, "Bear, Bowl, Hammerbot", "Guardian Relic"),
            ("Guardian_ModuleBlueprint", "Guardian Module Blueprint Segment", 0, 6, "Turtle", "Guardian Relic"),
            ("Guardian_VesselBlueprint", "Guardian Vessel Blueprint Segment", 0, 3, "Squid, Stickyhand, Robolobster", "Ancient Key"),
        }, view.Blueprints.Select(b => (b.Code, b.Name, b.Have, b.Need, string.Join(", ", b.Layouts), b.Key)));
        // The three nearest sites of each kind, from the whole list, nearest first (worked out apart from the program
        // from guardian_sites.csv and HIP 12099's place). A fighter site comes with the beacon nearest to it.
        Assert.Equal(new[]
        {
            new GuardianSite("Synuefe GV-T b50-4", "B 1", 789.9, null, null),
            new GuardianSite("Synuefe PM-L c24-24", "C 6", 830, null, null),
            new GuardianSite("NGC 2451A Sector EB-X c1-18", "C 2", 840.9, null, null),
        }, view.Blueprints[0].Sites);
        Assert.Equal(new GuardianSite("Synuefe PX-J c25-8", "7 a", 777.8, null, null), view.Blueprints[1].Sites[0]);
        Assert.Equal(new[]
        {
            new GuardianSite("IC 2391 Sector CA-A d43", "12 a", 705.6, "IC 2391 Sector HG-X b1-7", "4"),
            new GuardianSite("IC 2391 Sector HG-X b1-8", "C 2", 713.5, "IC 2391 Sector HG-X b1-7", "4"),
            new GuardianSite("IC 2391 Sector FL-X b1-7", "A 3", 722.5, "IC 2391 Sector HG-X b1-7", "4"),
        }, view.Blueprints[2].Sites);

        // Every site, nearest first: 168 structures and beacons, and the 335 ruins on 159 bodies, one place per body.
        Assert.Equal((168, 159, 335), (view.Places.Count(place => place.Kind != "ruins"), view.Places.Count(place => place.Kind == "ruins"),
            view.Places.Where(place => place.Kind == "ruins").Sum(place => place.Layouts.Count)));
        Assert.Equal(view.Places.Select(place => place.Ly).Order(), view.Places.Select(place => place.Ly));
        GuardianPlace first = view.Places[0];
        Assert.Equal(("ruins", "Beta", "Synuefe XR-H d11-102", "1 b", (double?)470.4), (first.Kind, string.Join(", ", first.Layouts), first.System, first.Body, first.Ly));
        foreach ((GuardianBlueprint blueprint, string kind) in view.Blueprints.Zip(new[] { "weapon", "module", "vessel" }))
            Assert.Equal(view.Places.Where(place => place.Kind == kind).Take(3).Select(place => (place.System, place.Body, place.Ly)),
                blueprint.Sites.Select(site => (site.System, site.Body, site.Ly)));

        // The commodity of a recipe is counted from the hold, the segment from the materials.
        game.Write(Path.Combine(game.JournalDir, "Cargo.json"), """{ "timestamp":"2026-10-08T12:50:00Z", "event":"Cargo", "Vessel":"Ship", "Count":10, "Inventory":[ { "Name":"hnshockmount", "Name_Localised":"HN Shock Mount", "Count":8, "Stolen":0 }, { "Name":"drones", "Name_Localised":"Limpet", "Count":2, "Stolen":0 } ] }""");
        game.Append(journal, """{ "timestamp":"2026-10-08T12:55:00Z", "event":"MaterialCollected", "Category":"Encoded", "Name":"guardian_moduleblueprint", "Name_Localised":"Guardian Module Blueprint Fragment", "Count":1 }""");
        watcher.Poll();
        Assert.Equal(2, told);
        Assert.EndsWith("8 HN Shock Mount [Commodity, have 8]", Line(helper.View().Items[0]));
        Assert.StartsWith("Guardian FSD Booster (Sizes 1 to 5): 1 Guardian Module Blueprint Segment [Encoded, have 1]", Line(helper.View().Items[0]));
        Assert.Equal(1, helper.View().Blueprints[1].Have);

        // The booster is unlocked at a Guardian technology broker. (The game takes the commodity out of Cargo.json itself.)
        game.Append(journal, BoosterUnlocked);
        watcher.Poll();
        Assert.Equal(3, told);
        view = helper.View();
        Assert.Equal(new[] { "Guardian FSD Booster" }, view.Items.Where(item => item.Unlocked).Select(item => item.Name));
        Assert.Equal((0, 5), (view.Blueprints[1].Have, view.Blueprints[1].Need));   // one segment spent, five modules still locked
        Assert.StartsWith("Guardian Hybrid Power Distributor (Sizes 1 to 8): 1 Guardian Module Blueprint Segment [Encoded, have 0], 20 Pattern Alpha Obelisk Data [Encoded, have 12], 24 Guardian Power Cell [Manufactured, have 72]", Line(view.Items[2]));

        // It is kept for the next start, with how far the journals have been read.
        GuardianSettings saved = Saved();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }.Select(size => $"Int_GuardianFSDBooster_Size{size}"), saved.Unlocked);
        Assert.Equal(watcher.State.UnlocksReadTo, saved.JournalsReadTo);
        Assert.NotEqual(default, saved.JournalsReadTo);

        // A line that changes nothing on the page is not passed on.
        game.Append(journal, """{ "timestamp":"2026-10-08T13:03:00Z", "event":"Music", "MusicTrack":"Exploration" }""");
        watcher.Poll();
        Assert.Equal(3, told);
    }

    [Fact]
    public void Unlocks_from_older_journals_and_from_earlier_runs_are_known_without_searching_twice()
    {
        // An old journal with two unlocks: a weapon, and (in the game's wording of before version 3.03) a fighter.
        game.Journal("Journal.180302112000.01.log",
        [
            """{ "timestamp":"2025-11-02T11:28:44Z", "event":"TechnologyBroker", "BrokerType":"guardian", "MarketID":128151032, "ItemsUnlocked":[ { "Name":"Hpt_Guardian_ShardCannon_Turret_Large", "Name_Localised":"Guardian Shard Cannon" } ], "Commodities":[ { "Name":"microcontrollers", "Name_Localised":"Micro Controllers", "Count":12 } ], "Materials":[ { "Name":"guardian_weaponblueprint", "Name_Localised":"Guardian Weapon Blueprint Fragment", "Count":2, "Category":"Encoded" } ] }""",
            """{ "timestamp":"2018-09-02T11:28:44Z", "event":"TechnologyBroker", "BrokerType":"guardian", "MarketID":128151032, "ItemUnlocked":"gdn_hybrid_fighter_v2", "Commodities":[ ], "Materials":[ { "Name":"guardian_vesselblueprint", "Count":1, "Category":"Encoded" } ] }""",
        ]);
        game.Journal(Engineered);

        using (GameWatcher watcher = game.Watch())
        {
            var helper = new GuardianHelper(watcher.State, SettingsFile());
            int told = 0;
            helper.Changed += () => told++;
            watcher.Poll();
            Assert.Equal((1, false), (told, helper.View().HistoryRead));   // the page is told the search is still under way
            Assert.DoesNotContain(helper.View().Items, item => item.Unlocked);

            for (int i = 0; !helper.View().HistoryRead; i++)
            {
                Assert.True(i < 500, "the search of the older journals never finished");
                Thread.Sleep(10);
                watcher.Poll();
            }
            Assert.Equal(2, told);
            GuardianView view = helper.View();
            Assert.Equal(new[] { "Guardian Shard Cannon (Turreted, large)", "XG8 Javelin (Ship-launched fighter)" }, view.Items.Where(item => item.Unlocked).Select(item => $"{item.Name} ({item.Detail})"));
            // 16 of the 18 weapon segments and two of the three vessel segments are still to be found; the old line's cost changes nothing held today.
            Assert.Equal(new[] { (0, 16), (0, 6), (0, 2) }, view.Blueprints.Select(blueprint => (blueprint.Have, blueprint.Need)));
        }
        GuardianSettings saved = Saved();
        Assert.Equal(new[] { "gdn_hybrid_fighter_v2", "Hpt_Guardian_ShardCannon_Turret_Large" }, saved.Unlocked);

        // The next start. The old journal is gone (a new PC, say): what was found is still known, at once.
        File.Delete(Path.Combine(game.JournalDir, "Journal.180302112000.01.log"));
        using (GameWatcher watcher = game.Watch())
        {
            var helper = new GuardianHelper(watcher.State, SettingsFile());
            Assert.Equal(2, helper.View().Items.Count(item => item.Unlocked));
            watcher.Poll();
            Assert.Equal((2, true), (helper.View().Items.Count(item => item.Unlocked), helper.View().HistoryRead));
        }

        // A settings file changed by hand costs nothing but the memory.
        File.WriteAllText(Path.Combine(temp, "settings.json"), """{ "guardian": { "unlocked": [ null, "", "GDN_HYBRID_FIGHTER_V3" ], "journalsReadTo": "0001-01-01T00:00:00" } }""");
        using (GameWatcher watcher = game.Watch())
        {
            var helper = new GuardianHelper(watcher.State, SettingsFile());
            watcher.Poll();
            Assert.Equal(new[] { "XG9 Lance" }, helper.View().Items.Where(item => item.Unlocked).Select(item => item.Name));
        }
        Assert.Equal(new[] { "GDN_HYBRID_FIGHTER_V3" }, Saved().Unlocked);
    }

    // ------------------------------------------------------------ the /api/loadouts/guardian/ route

    [Fact]
    public async Task The_route_gives_the_whole_checklist()
    {
        game.Journal(Engineered, [.. PracticeGame.Recording(Engineered), BoosterUnlocked]);
        using GameWatcher watcher = game.Watch();
        using var web = new WebServer(temp);
        GuardianApi.Map(web, watcher.State, SettingsFile());
        watcher.Poll();

        WebResponse answer = await web.Handle(new WebRequest("GET", "/api/loadouts/guardian/list", new Dictionary<string, string>(), ""));
        JsonElement json = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(answer.Body));

        Assert.Equal((200, 23, true), (answer.Status, json.GetProperty("items").GetArrayLength(), json.GetProperty("historyRead").GetBoolean()));
        Assert.Equal(
            """{"group":"Modules","name":"Guardian FSD Booster","detail":"Sizes 1 to 5","unlocked":true,"blueprint":"Guardian_ModuleBlueprint","recipe":[{"name":"Guardian Module Blueprint Segment","kind":"Encoded","need":1,"have":0},{"name":"Guardian Power Cell","kind":"Manufactured","need":21,"have":72},{"name":"Guardian Technology Component","kind":"Manufactured","need":21,"have":0},{"name":"Focus Crystals","kind":"Manufactured","need":24,"have":0},{"name":"HN Shock Mount","kind":"Commodity","need":8,"have":0}]}""",
            JsonSerializer.Serialize(json.GetProperty("items")[0]));
        Assert.Equal(
            """{"code":"Guardian_VesselBlueprint","name":"Guardian Vessel Blueprint Segment","have":0,"need":3,"layouts":["Squid","Stickyhand","Robolobster"],"key":"Ancient Key","sites":[{"system":"IC 2391 Sector CA-A d43","body":"12 a","ly":705.6,"beaconSystem":"IC 2391 Sector HG-X b1-7","beaconBody":"4"},{"system":"IC 2391 Sector HG-X b1-8","body":"C 2","ly":713.5,"beaconSystem":"IC 2391 Sector HG-X b1-7","beaconBody":"4"},{"system":"IC 2391 Sector FL-X b1-7","body":"A 3","ly":722.5,"beaconSystem":"IC 2391 Sector HG-X b1-7","beaconBody":"4"}]}""",
            JsonSerializer.Serialize(json.GetProperty("blueprints")[2]));
        Assert.Equal(5, json.GetProperty("blueprints")[1].GetProperty("need").GetInt32());
        // Every site, as the page reads it.
        JsonElement places = json.GetProperty("places");
        Assert.Equal(168 + 159, places.GetArrayLength());
        Assert.Equal("""{"kind":"ruins","layouts":["Beta"],"system":"Synuefe XR-H d11-102","body":"1 b","ly":470.4}""", JsonSerializer.Serialize(places[0]));
    }

    // ------------------------------------------------------------ the list of every Guardian site

    [Fact]
    public void The_site_list_holds_every_kind_of_Guardian_site_once_by_what_it_is_for()
    {
        IReadOnlyList<GuardianHelper.Site> sites = GuardianHelper.Sites;
        // Canonn's survey and ED Odyssey Materials Helper's list together: 98 blueprint structures, 40 small ones
        // with no data terminal, 30 beacons and 335 ruins.
        Assert.Equal(new[] { ("beacon", 30), ("materials", 40), ("module", 17), ("ruins", 335), ("vessel", 31), ("weapon", 50) },
            sites.GroupBy(site => site.Kind).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => (group.Key, group.Count())));
        // Each shape is one kind of site, as Canonn's survey has it.
        var kindOf = new Dictionary<string, string>
        {
            ["Bear"] = "weapon", ["Bowl"] = "weapon", ["Hammerbot"] = "weapon", ["Turtle"] = "module", ["Squid"] = "vessel", ["Stickyhand"] = "vessel",
            ["Robolobster"] = "vessel", ["Lacrosse"] = "materials", ["Crossroads"] = "materials", ["Fistbump"] = "materials", ["Beacon"] = "beacon",
            ["Alpha"] = "ruins", ["Beta"] = "ruins", ["Gamma"] = "ruins",
        };
        Assert.All(sites, site => Assert.Equal(kindOf[site.Layout], site.Kind));
        // A structure or a beacon is in the list once, however the two sources wrote its name.
        GuardianHelper.Site[] structures = [.. sites.Where(site => site.Kind != "ruins")];
        Assert.Equal(structures.Length, structures.Select(site => (site.System.ToUpperInvariant(), site.Body.Replace(" ", "").ToUpperInvariant())).Distinct().Count());
        // A body is written as the game writes it: the star's letters in capitals, the planet's number, its moons in small letters.
        Assert.All(sites.Where(site => site.Kind != "beacon"), site => Assert.Matches(@"^(([A-Z]+ )?\d+( [a-z]+)*|[A-Z]+)$", site.Body));
        // Every site has its system's place, out where the Guardians were: the nearest a few hundred light years from
        // Sol, the farthest ruins some 21,200 in the Eorl Auwsy sector.
        Assert.All(sites, site => Assert.InRange(Math.Sqrt(site.X * site.X + site.Y * site.Y + site.Z * site.Z), 300, 22000));

        // The sites the page named by hand before the list came, each checked then against Canonn, the wiki and EDSM,
        // are in it as the same kind; so is the best-known ruin.
        foreach ((string kind, string system, string body) in new[]
        {
            ("weapon", "Synuefe EU-Q c21-10", "A 3"), ("weapon", "NGC 2451A Sector IR-W d1-77", "AB 1 a"), ("weapon", "Synuefe FK-P c22-2", "E 1"),
            ("module", "Synuefe PX-J c25-8", "7 a"), ("module", "Synuefe GT-H b43-1", "C 4"), ("module", "Synuefe NL-N c23-4", "B 3"),
            ("vessel", "IC 2391 Sector CA-A d43", "12 a"), ("vessel", "IC 2391 Sector HG-X b1-8", "C 2"), ("vessel", "IC 2391 Sector FL-X b1-7", "A 3"),
            ("beacon", "IC 2391 Sector CQ-Y c16", "2"), ("beacon", "IC 2391 Sector FG-X b1-9", "8"), ("beacon", "IC 2391 Sector MX-T b3-6", "A 1"),
            ("ruins", "Synuefe XR-H d11-102", "1 b"),
        })
            Assert.Contains(sites, site => (site.Kind, site.System, site.Body) == (kind, system, body));
    }

    [Fact]
    public void Before_the_ship_is_placed_the_sites_are_nearest_to_Sol_first_with_no_distance()
    {
        using GameWatcher watcher = game.Watch();
        GuardianView view = new GuardianHelper(watcher.State, SettingsFile()).View();   // nothing read: where the ship is is not known

        Assert.All(view.Places, place => Assert.Null(place.Ly));
        Assert.All(view.Blueprints.SelectMany(blueprint => blueprint.Sites), site => Assert.Null(site.Ly));
        double FromSol(GuardianPlace place)
        {
            GuardianHelper.Site site = GuardianHelper.Sites.First(site => site.System == place.System && site.Body == place.Body);
            return Math.Sqrt(site.X * site.X + site.Y * site.Y + site.Z * site.Z);
        }
        Assert.Equal(view.Places.Select(FromSol).Order(), view.Places.Select(FromSol));
    }
}
