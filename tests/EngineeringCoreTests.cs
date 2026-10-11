using EliteStudio.Core;

namespace EliteStudio.Tests;

/// <summary>
/// What the core knows for engineering: the commander's materials, the standing with the
/// engineers, and what was ever unlocked at a technology broker. The recordings hold the game's
/// lists at start-up and three finds; every other line here is written in the shape the
/// community's journal schemas give it (github.com/jixxed/ed-journal-schemas), because no
/// recording has one.
/// </summary>
public class EngineeringCoreTests
{
    const string OnFoot = "Journal.2025-04-17T013240.01.log";       // a new commander's first materials, and three finds
    const string Hauling = "Journal.2026-10-05T152112.01.log";      // an earlier session
    const string Unfinished = "Journal.2026-10-06T040304.01.log";   // 165 manganese on board
    const string Engineered = "Journal.2026-10-08T080236.01.log";   // fourteen engineers known, two of them unlocked
    [Fact]
    public void Materials_follow_the_games_list_and_every_line_that_changes_a_count()
    {
        using var game = new PracticeGame();
        string journal = game.Journal(OnFoot);
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;
        var collected = new List<string>();
        state.Journal += line =>
        {
            if (line.Event == "MaterialCollected") collected.Add($"{line.Data.GetProperty("Name")} {state.Materials[line.Data.GetProperty("Name").GetString()!]}");
        };

        watcher.Poll();

        // The list the game wrote at the start (8 materials), and the three finds of that session on top of it.
        Assert.Equal(new[] { "uncutfocuscrystals 3", "galvanisingalloys 3", "salvagedalloys 6" }, collected);
        Assert.Equal(10, state.Materials.Count);
        Assert.Equal((6, 6, 3, 12), (state.Materials["heatresistantceramics"], state.Materials["SalvagedAlloys"], state.Materials["shieldingsensors"], state.Materials["decodedemissiondata"]));
        Assert.False(state.Materials.ContainsKey("iron"));   // that commander had no raw materials yet

        int Held(string code) => state.Materials.GetValueOrDefault(code);
        void Written(params string[] lines)
        {
            game.Append(journal, lines);
            watcher.Poll();
        }

        // No recording holds the lines below, so each is written in the shape the community's journal
        // schemas give it (github.com/jixxed/ed-journal-schemas), with this commander's own materials.
        Written("""{ "timestamp":"2025-04-17T07:10:00Z", "event":"MaterialDiscarded", "Category":"Manufactured", "Name":"crystalshards", "Name_Localised":"Crystal Shards", "Count":2 }""");
        Assert.Equal(4, Held("crystalshards"));

        // Six of one grade for one of the next grade up at a material trader. Paying out the last of a material takes it off the list.
        Written("""{ "timestamp":"2025-04-17T07:11:00Z", "event":"MaterialTrade", "MarketID":3223343616, "TraderType":"manufactured", "Paid":{ "Material":"salvagedalloys", "Material_Localised":"Salvaged Alloys", "Category":"Manufactured", "Quantity":6 }, "Received":{ "Material":"galvanisingalloys", "Material_Localised":"Galvanising Alloys", "Category":"Manufactured", "Quantity":1 } }""");
        Assert.Equal((0, 4, false), (Held("salvagedalloys"), Held("galvanisingalloys"), state.Materials.ContainsKey("salvagedalloys")));

        Written("""{ "timestamp":"2025-04-17T07:12:00Z", "event":"EngineerCraft", "Slot":"FrameShiftDrive", "Module":"int_hyperdrive_size3_class5", "Ingredients":[ { "Name":"galvanisingalloys", "Name_Localised":"Galvanising Alloys", "Count":1 }, { "Name":"decodedemissiondata", "Name_Localised":"Decoded Emission Data", "Count":2 } ], "Engineer":"Felicity Farseer", "EngineerID":300100, "BlueprintID":128673694, "BlueprintName":"FSD_LongRange", "Level":1, "Quality":1.000000 }""");
        Assert.Equal((3, 10), (Held("galvanisingalloys"), Held("decodedemissiondata")));

        Written("""{ "timestamp":"2025-04-17T07:13:00Z", "event":"Synthesis", "Name":"Repair Basic", "Materials":[ { "Name":"heatresistantceramics", "Name_Localised":"Heat Resistant Ceramics", "Count":2 }, { "Name":"shieldingsensors", "Name_Localised":"Shielding Sensors", "Count":1 } ] }""");
        Assert.Equal((4, 2), (Held("heatresistantceramics"), Held("shieldingsensors")));

        Written("""{ "timestamp":"2025-04-17T07:14:00Z", "event":"TechnologyBroker", "BrokerType":"human", "MarketID":3223343616, "ItemsUnlocked":[ { "Name":"Hpt_PlasmaShockCannon_Fixed_Small", "Name_Localised":"Shock Cannon" } ], "Commodities":[ { "Name":"powerconverter", "Name_Localised":"Power Converter", "Count":4 } ], "Materials":[ { "Name":"uncutfocuscrystals", "Name_Localised":"Flawed Focus Crystals", "Count":3, "Category":"Manufactured" }, { "Name":"archivedemissiondata", "Name_Localised":"Irregular Emission Data", "Count":1, "Category":"Encoded" } ] }""");
        Assert.Equal((0, 2), (Held("uncutfocuscrystals"), Held("archivedemissiondata")));

        // An engineer asking for materials before working for the commander; one asking for a commodity changes nothing here.
        Written(
            """{ "timestamp":"2025-04-17T07:15:00Z", "event":"EngineerContribution", "Engineer":"The Sarge", "EngineerID":300040, "Type":"Materials", "Material":"shieldsoakanalysis", "Material_Localised":"Inconsistent Shield Soak Analysis", "Quantity":2, "TotalQuantity":2 }""",
            """{ "timestamp":"2025-04-17T07:15:10Z", "event":"EngineerContribution", "Engineer":"Liz Ryder", "EngineerID":300080, "Type":"Commodity", "Commodity":"landmines", "Quantity":2, "TotalQuantity":2 }""");
        Assert.Equal(1, Held("shieldsoakanalysis"));

        Written("""{ "timestamp":"2025-04-17T07:16:00Z", "event":"ScientificResearch", "MarketID":3223343616, "Name":"shieldcyclerecordings", "Name_Localised":"Distorted Shield Cycle Recordings", "Category":"Encoded", "Count":3 }""");
        Assert.Equal(0, Held("shieldcyclerecordings"));

        // A mission pays in ship materials and in things for use on foot, in one list: only the first kind counts.
        Written("""{ "timestamp":"2025-04-17T07:17:00Z", "event":"MissionCompleted", "Faction":"A Faction", "Name":"Mission_Courier_name", "MissionID":1, "Reward":1000, "MaterialsReward":[ { "Name":"Arsenic", "Category":"$MICRORESOURCE_CATEGORY_Elements;", "Category_Localised":"Elements", "Count":4 }, { "Name":"ChemicalProcessors", "Name_Localised":"Chemical Processors", "Category":"$MICRORESOURCE_CATEGORY_Manufactured;", "Category_Localised":"Manufactured", "Count":5 }, { "Name":"ChemicalInventory", "Name_Localised":"Chemical Inventory", "Category":"$MICRORESOURCE_CATEGORY_Data;", "Category_Localised":"Data", "Count":2 } ] }""");
        Assert.Equal((4, 5, 0), (Held("arsenic"), Held("chemicalprocessors"), Held("chemicalinventory")));
        Assert.Equal(9, state.Materials.Count);

        // The next start: the game's own list replaces whatever was worked out.
        Written("""{ "timestamp":"2025-04-18T05:00:00Z", "event":"Materials", "Raw":[ { "Name":"iron", "Count":5 } ], "Manufactured":[ { "Name":"crystalshards", "Name_Localised":"Crystal Shards", "Count":9 } ], "Encoded":[  ] }""");
        Assert.Equal(new[] { ("crystalshards", 9), ("iron", 5) }, state.Materials.OrderBy(m => m.Key).Select(m => (m.Key, m.Value)));
    }

    [Fact]
    public void Engineers_standing_follows_the_list_at_start_and_the_single_updates_after_it()
    {
        using var game = new PracticeGame();
        string journal = game.Journal(Engineered);
        using GameWatcher watcher = game.Watch();
        GameState state = watcher.State;

        watcher.Poll();

        Assert.Equal(14, state.Engineers.Count);
        Assert.Equal(new EngineerStanding("Unlocked", 4), state.Engineers[300160]);   // Elvira Martuuk
        Assert.Equal(new EngineerStanding("Unlocked", 3), state.Engineers[300180]);   // The Dweller
        Assert.Equal(new EngineerStanding("Invited", 0), state.Engineers[300100]);    // Felicity Farseer
        Assert.Equal(new EngineerStanding("Known", 0), state.Engineers[400011]);      // Eleanor Bresa
        Assert.False(state.Engineers.ContainsKey(300110));   // Ram Tah: not heard of yet

        // The single lines the game writes in between are in no recording: these are in the shape of the community's journal schemas.
        game.Append(journal, """{ "timestamp":"2026-10-08T13:00:00Z", "event":"EngineerProgress", "Engineer":"Felicity Farseer", "EngineerID":300100, "Progress":"Unlocked", "Rank":1 }""");
        watcher.Poll();
        Assert.Equal(new EngineerStanding("Unlocked", 1), state.Engineers[300100]);

        // A rank gained: the line no longer says "Unlocked".
        game.Append(journal,
            """{ "timestamp":"2026-10-08T13:05:00Z", "event":"EngineerProgress", "Engineer":"Felicity Farseer", "EngineerID":300100, "Rank":2 }""",
            """{ "timestamp":"2026-10-08T13:06:00Z", "event":"EngineerProgress", "Engineer":"Ram Tah", "EngineerID":300110, "Progress":"Known" }""");
        watcher.Poll();
        Assert.Equal(new EngineerStanding("Unlocked", 2), state.Engineers[300100]);
        Assert.Equal(new EngineerStanding("Known", 0), state.Engineers[300110]);
        Assert.Equal(15, state.Engineers.Count);
    }

    [Fact]
    public void Technology_broker_unlocks_are_found_in_older_journals_once_and_in_the_one_being_followed()
    {
        // No recording holds a technology-broker line, so these are in the shape of the community's journal schemas:
        // one as the game writes it now (a list: one unlock opens every size), one as it wrote it before version 3.03.
        const string booster = """{ "timestamp":"2024-03-02T11:28:44Z", "event":"TechnologyBroker", "BrokerType":"guardian", "MarketID":128151032, "ItemsUnlocked":[ { "Name":"Int_GuardianFSDBooster_Size1", "Name_Localised":"Guardian FSD Booster" }, { "Name":"Int_GuardianFSDBooster_Size5", "Name_Localised":"Guardian FSD Booster" } ], "Commodities":[ { "Name":"hnshockmount", "Name_Localised":"HN Shock Mount", "Count":8 } ], "Materials":[ { "Name":"guardian_moduleblueprint", "Name_Localised":"Guardian Module Blueprint Fragment", "Count":1, "Category":"Encoded" }, { "Name":"guardian_powercell", "Name_Localised":"Guardian Power Cell", "Count":21, "Category":"Manufactured" } ] }""";
        const string oldStyle = """{ "timestamp":"2018-03-02T11:28:44Z", "event":"TechnologyBroker", "BrokerType":"Human", "MarketID":128151032, "ItemUnlocked":"Hpt_PlasmaShockCannon_Fixed_Medium", "Commodities":[ { "Name":"iondistributor", "Count":6 } ], "Materials":[ { "Name":"vanadium", "Count":24, "Category":"Raw" } ] }""";
        const string gauss = """{ "timestamp":"2026-10-06T08:20:00Z", "event":"TechnologyBroker", "BrokerType":"guardian", "MarketID":128151032, "ItemsUnlocked":[ { "Name":"Hpt_Guardian_GaussCannon_Fixed_Medium", "Name_Localised":"Guardian Gauss Cannon" } ], "Commodities":[ { "Name":"magneticemittercoil", "Name_Localised":"Magnetic Emitter Coil", "Count":6 } ], "Materials":[ { "Name":"manganese", "Count":15, "Category":"Raw" } ] }""";
        using var game = new PracticeGame();
        string years = game.Journal("Journal.180302112000.01.log", [PracticeGame.Line(PracticeGame.Recording(OnFoot), "Fileheader"), oldStyle]);
        game.Journal(Hauling, [.. PracticeGame.Recording(Hauling).Take(40), booster, booster[..90]]);   // ... and a line cut short
        string current = game.Journal(Unfinished);

        // Polls until the background search of the older journals has been taken in.
        static GameState Searched(GameWatcher watcher)
        {
            DateTime before = watcher.State.UnlocksReadTo;
            for (int i = 0; watcher.State.UnlocksReadTo == before; i++)
            {
                Assert.True(i < 500, "the search of the older journals never finished");
                Thread.Sleep(10);
                watcher.Poll();
            }
            return watcher.State;
        }

        string[] known;
        DateTime readTo;
        using (GameWatcher watcher = game.Watch())
        {
            GameState state = watcher.State;
            int changed = 0;
            state.Changed += () => changed++;
            watcher.Poll();
            Assert.Equal((1, 165), (changed, state.Materials["manganese"]));
            Assert.Equal((0, default), (state.Unlocked.Count, state.UnlocksReadTo));   // the start-up does not wait for the search
            Searched(watcher);
            Assert.Equal(2, changed);   // the answer is one more change, for whoever shows or keeps the unlocks
            Assert.Equal(3, state.Unlocked.Count);
            Assert.True(state.Unlocked.Contains("int_guardianfsdbooster_size5") && state.Unlocked.Contains("HPT_PlasmaShockCannon_Fixed_Medium"));
            Assert.Equal(File.GetLastWriteTimeUtc(current), state.UnlocksReadTo);
            Assert.Equal(165, state.Materials["manganese"]);   // what an old unlock cost was paid long ago: today's counts stand

            // Unlocking while Studio follows the journal: known at once, and the materials are gone.
            game.Append(current, gauss);
            watcher.Poll();
            Assert.Contains("hpt_guardian_gausscannon_fixed_medium", state.Unlocked);
            Assert.Equal(150, state.Materials["manganese"]);
            (known, readTo) = ([.. state.Unlocked], state.UnlocksReadTo);
        }

        // The next start, with what the last one found handed back: journals searched then are left alone,
        // which shows in the shock cannon's file staying unread although it is no longer remembered.
        using (GameWatcher watcher = game.Watch())
        {
            watcher.State.AddUnlocks(known.Where(code => !code.Contains("ShockCannon")), readTo);
            watcher.Poll();
            Assert.Equal(3, watcher.State.Unlocked.Count);
            Assert.DoesNotContain("Hpt_PlasmaShockCannon_Fixed_Medium", watcher.State.Unlocked);
            Assert.Equal(File.GetLastWriteTimeUtc(current), watcher.State.UnlocksReadTo);
        }

        // Played on without Studio: the journal Studio last followed got more lines, and a newer one was begun.
        game.Append(current, oldStyle.Replace("Medium", "Large"));
        game.Journal("Journal.2026-10-07T090000.01.log", PracticeGame.Recording(Unfinished).Take(10));
        using (GameWatcher watcher = game.Watch())
        {
            watcher.State.AddUnlocks(known, readTo);
            watcher.Poll();
            Assert.DoesNotContain("Hpt_PlasmaShockCannon_Fixed_Large", watcher.State.Unlocked);
            Assert.Contains("Hpt_PlasmaShockCannon_Fixed_Large", Searched(watcher).Unlocked);   // only that one journal was new to the search
            Assert.Equal(5, watcher.State.Unlocked.Count);
        }

        // A journal another program holds open cannot be searched: said in the log, and tried again at the next start.
        using (new FileStream(years, FileMode.Open, FileAccess.Read, FileShare.None))
        using (GameWatcher watcher = game.Watch())
        {
            watcher.Poll();
            for (int i = 0; watcher.State.Unlocked.Count < 4; i++)
            {
                Assert.True(i < 500, "the search of the older journals never finished");
                Thread.Sleep(10);
                watcher.Poll();
            }
            Assert.Equal(default, watcher.State.UnlocksReadTo);
            Assert.DoesNotContain("Hpt_PlasmaShockCannon_Fixed_Medium", watcher.State.Unlocked);
        }
    }
}
