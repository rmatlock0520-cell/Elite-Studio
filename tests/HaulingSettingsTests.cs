using System.Text.Json;
using EliteStudio.Market;
using EliteStudio.Overlay;
using static EliteStudio.Tests.HaulRig;   // its places, Hold, Noon, and OldFile for the old tool's sample files

namespace EliteStudio.Tests;

/// <summary>
/// The settings side of hauling: what a change does to the search, the typed jump ranges, presets,
/// the overlay menu's data, the first-run copy from the Elite Haul Overlay's settings file
/// (small files in that tool's own format are in Fixtures\hauling), and what the settings file
/// keeps for a restart of Studio: the accepted trade and an open lookup.
/// </summary>
public class HaulingSettingsTests
{
    static Dictionary<string, JsonElement> Values(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    // ---- changing settings ----

    [Fact]
    public void A_changed_search_setting_searches_after_a_pause_and_other_changes_never_do()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;
        Assert.Single(rig.Search.Asked);

        Assert.True(rig.Change("""{ "maxLy": 60 }"""));
        Assert.Equal((60, 60), (hauling.Settings.MaxLy, rig.Saved().MaxLy));   // saved at once
        rig.After(0.4);
        Assert.True(rig.Change("""{ "MinSupply": 1000 }"""));       // capitals are not minded; the pause starts again
        rig.After(0.6);
        Assert.Single(rig.Search.Asked);
        rig.After(0.1);                                             // 700 ms after the last change: one search for both
        Assert.Equal(2, rig.Search.Asked.Count);
        Assert.Equal((60, 1000), (rig.Search.Asked[1].MaxLy, rig.Search.Asked[1].MinSupply));

        Assert.True(rig.Change("""{ "listSort": "hour" }"""));      // the order of the short list is not a search setting
        hauling.StepListSort(1);
        int changes = rig.Changes;
        DateTime saved = File.GetLastWriteTimeUtc(rig.SettingsFile);
        Assert.True(rig.Change("""{ "maxLy": 60 }"""));             // already so: nothing to save, nothing to draw
        Assert.Equal((changes, saved), (rig.Changes, File.GetLastWriteTimeUtc(rig.SettingsFile)));
        rig.After(5);
        Assert.Equal(2, rig.Search.Asked.Count);
        Assert.Equal("ton", rig.Saved().ListSort);

        hauling.ToggleAccept("F6");                                 // a changed setting is a search the player asked for:
        TradeRow accepted = hauling.Accepted!;                      // it runs with a trade accepted, and the overlay stays on that trade
        Assert.True(rig.Change("""{ "returns": false }"""));
        rig.After(0.7);
        Assert.Equal(3, rig.Search.Asked.Count);
        Assert.False(rig.Search.Asked[2].Returns);
        Assert.Same(accepted, hauling.Shown);
        Assert.EndsWith($"   ·   {Keeper} stays on your accepted trade until you drop it", hauling.ListMessage);
    }

    [Fact]
    public void Values_that_do_not_fit_change_nothing_and_values_out_of_range_are_brought_in()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;

        Assert.False(rig.Change("""{ "maxLy": "far" }"""));
        Assert.False(rig.Change("""{ "maxLy": 40, "carriers": "yes" }"""));     // one bad value: none of it is taken
        Assert.False(rig.Change("""{ "maxLy": 40, "pad": null }"""));           // no value at all fits no setting
        Assert.Equal(new MarketSettings() with { Presets = hauling.Settings.Presets }, hauling.Settings);

        Assert.True(rig.Change("""{ "maxLy": "45" }"""));                       // digits in text are a number
        Assert.True(rig.Change("""{ "notASetting": 1, "presets": [] }"""));     // not settings to change this way
        Assert.Equal((45, 2), (hauling.Settings.MaxLy, hauling.Settings.Presets.Count));

        Assert.True(rig.Change("""
            { "buyLy": 900, "maxLy": 0, "minSupply": 750, "minDemand": -5, "cargoOverride": 5000, "jumpRange": 500, "maxAgeHours": 0,
              "rankBy": "TRIP", "surface": "moon", "startMode": "Anywhere", "listSort": "sideways", "srcPower": "  ", "pad": "m" }
            """));
        MarketSettings now = hauling.Settings;
        Assert.Equal((500, 1, 500, 0, 2000, 200.0, 1), (now.BuyLy, now.MaxLy, now.MinSupply, now.MinDemand, now.CargoOverride, now.JumpRange, now.MaxAgeHours));
        Assert.Equal(("trip", "odyssey", "anywhere", "rank", "any", "M"), (now.RankBy, now.Surface, now.StartMode, now.ListSort, now.SrcPower, now.Pad));
        MarketSettings saved = rig.Saved();
        Assert.Equal(now with { Presets = saved.Presets }, saved);
    }

    [Fact]
    public void Typed_jump_ranges_win_over_the_ship_and_are_swapped_when_typed_the_wrong_way_round()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;
        Assert.Equal((27.8, 14.4, false), (hauling.JumpEmpty, hauling.JumpFull, hauling.JumpRangesSwapped));   // worked out from the ship
        Assert.Equal((400, 2), (hauling.CargoTons, hauling.Pad));

        rig.Change("""{ "jumpRange": 27, "jumpEmpty": 14 }""");     // "full" typed bigger than "empty"
        Assert.Equal((27.0, 14.0, true), (hauling.JumpEmpty, hauling.JumpFull, hauling.JumpRangesSwapped));
        Assert.Equal((27.0, 14.0), (rig.Saved().JumpRange, rig.Saved().JumpEmpty));     // the saved numbers stay as typed
        rig.After(0.7);
        Assert.Equal((14.0, 27.0), (rig.Search.Asked[1].Jump, rig.Search.Asked[1].JumpEmpty));

        rig.Change("""{ "jumpRange": 20, "jumpEmpty": 0 }""");      // only the full range typed
        Assert.Equal((27.8, 20.0, false), (hauling.JumpEmpty, hauling.JumpFull, hauling.JumpRangesSwapped));
        rig.Change("""{ "jumpRange": 30 }""");                      // an empty hold never jumps less far than a full one
        Assert.Equal((30.0, 30.0), (hauling.JumpEmpty, hauling.JumpFull));
        rig.Change("""{ "jumpRange": 0, "jumpEmpty": 40.5 }""");
        Assert.Equal((40.5, 14.4), (hauling.JumpEmpty, hauling.JumpFull));

        rig.Change("""{ "jumpEmpty": 0, "cargoOverride": 100 }""");  // a smaller hold: the full-hold range grows with it
        Assert.Equal((100, 27.8, 22.6), (hauling.CargoTons, hauling.JumpEmpty, hauling.JumpFull));

        rig.Change("""{ "pad": "l" }""");
        Assert.Equal(("L", 3), (hauling.Settings.Pad, hauling.Pad));
        rig.Change("""{ "pad": "S" }""");
        Assert.Equal(1, hauling.Pad);
        rig.Change("""{ "pad": "auto" }""");
        Assert.Equal(2, hauling.Pad);                               // the Type-8 again
        rig.After(0.7);
        Assert.Equal((100, 22.6, 27.8, 2), (rig.Search.Asked[^1].Cargo, rig.Search.Asked[^1].Jump, rig.Search.Asked[^1].JumpEmpty, rig.Search.Asked[^1].Pad));
    }

    // ---- presets ----

    [Fact]
    public void Presets_are_used_saved_replaced_and_removed_and_there_is_room_for_eight()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;
        Assert.Equal(new[]
        {
            new PresetLine("Quick one-way hops", "one way · routes to 40 ly · ranked by profit per hour", Active: false),
            new PresetLine("Long round trips", "round trip · routes to 120 ly · ranked by trip profit", Active: false),
        }, hauling.PresetLines);

        Assert.True(hauling.UsePreset("quick ONE-way hops"));
        MarketSettings now = hauling.Settings;
        Assert.Equal((false, "hour", 40, 15), (now.Returns, now.RankBy, now.MaxLy, now.BuyLy));     // only what the preset holds changed
        Assert.Equal(new[] { true, false }, hauling.PresetLines.Select(line => line.Active));
        rig.After(0.7);
        Assert.Equal((false, 40), (rig.Search.Asked[1].Returns, rig.Search.Asked[1].MaxLy));

        Assert.True(hauling.UsePreset("Quick one-way hops"));       // in use already: nothing changes, nothing is searched
        Assert.False(hauling.UsePreset("No such preset"));
        rig.After(0.7);
        Assert.Equal(2, rig.Search.Asked.Count);

        Assert.Equal(new Outcome(false, "Type a name for the preset first."), hauling.SavePreset("   "));
        Assert.Equal(new Outcome(true, "Saved \"Mine\"."), hauling.SavePreset("  Mine "));
        Preset mine = hauling.Settings.Presets[2];
        Assert.Equal(MarketSettings.PresetKeys, mine.Set.Keys);     // a saved preset holds every search setting, and nothing else
        Assert.Equal(new[] { true, false, true }, hauling.PresetLines.Select(line => line.Active));     // several can be in use at once

        for (int n = 4; n <= 8; n++) Assert.True(hauling.SavePreset($"Number {n}").Ok);
        Assert.Equal(new Outcome(false, "There is room for 8 presets. Delete one first."), hauling.SavePreset("One too many"));
        Assert.Equal(8, hauling.Settings.Presets.Count);

        rig.Change("""{ "maxLy": 80 }""");
        Assert.False(hauling.PresetLines[2].Active);
        Assert.Equal(new Outcome(true, "Updated \"MINE\" with the settings as they are now."), hauling.SavePreset("MINE"));    // same name, other capitals
        Assert.Equal(8, hauling.Settings.Presets.Count);
        Assert.Equal(("MINE", 80), (hauling.Settings.Presets[2].Name, hauling.Settings.Presets[2].Set["maxLy"].GetInt32()));
        Assert.Equal("one way · buy within 15 ly · routes to 80 ly · prices under 2 days · ranked by profit per hour", hauling.PresetLines[2].Text);

        hauling.RemovePreset("number 8");
        hauling.RemovePreset("Long round trips");
        Assert.Equal(new string('x', 28), hauling.SavePreset(new string('x', 40)) is { Ok: true } ? hauling.Settings.Presets[^1].Name : "not saved");
        Assert.Equal(80, hauling.Settings.MaxLy);                   // removing a preset leaves the settings alone

        // Studio closes and starts again: the presets are its own now, and the old tool's file is not looked at any more.
        using var again = new HaulRig(HaulRig.In(Alpha), savedSettings: File.ReadAllText(rig.SettingsFile), oldSettings: OldFile("owner.json"));
        Assert.Equal(new[] { "Quick one-way hops", "MINE", "Number 4", "Number 5", "Number 6", "Number 7", new string('x', 28) },
            again.Hauling.Settings.Presets.Select(preset => preset.Name));
        Assert.Equal(hauling.PresetLines, again.Hauling.PresetLines);
        Assert.Equal(80, again.Hauling.Settings.MaxLy);
    }

    [Fact]
    public void A_presets_few_words_say_where_trades_start_and_how_old_prices_may_be()
    {
        Assert.Equal("anywhere · prices under 3 days", HaulMenu.PresetText(new Preset("a", Values("""{ "startMode": "anywhere", "buyLy": 20, "maxAgeHours": 72 }"""))));
        Assert.Equal("buy within 20 ly · prices under 12 h", HaulMenu.PresetText(new Preset("b", Values("""{ "startMode": "near", "buyLy": 20, "maxAgeHours": 12 }"""))));
        Assert.Equal("", HaulMenu.PresetText(new Preset("c", Values("""{ "carriers": true }"""))));
        Assert.Equal(new[] { "1 h", "47 h", "2 days", "7 days", "30 days" }, new[] { 1, 47, 48, 168, 720 }.Select(HaulMenu.HoursText));
    }

    // ---- the overlay menu's data ----

    [Fact]
    public void The_menus_settings_page_steps_through_fixed_choices()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;
        Assert.Equal(new[] { "trades", "settings", "presets" }, HaulMenu.Pages.Select(page => page.Key));
        Assert.Equal(new[]
        {
            "returns: Trip = Round trip (bring a load back)", "rankBy: Rank trades by = Profit per hour", "maxLy: Max. route distance = 30 ly, buy to sell",
            "buyLy: Buy within = 15 ly of me", "maxAgeHours: Max. price age = 2 days", "startMode: Trades start = Near me",
            "minSupply: Min. supply = 100 t or more", "minDemand: Min. demand = 100 t or more",
        }, hauling.SettingLines.Select(line => $"{line.Key}: {line.Label} = {line.Text}"));
        Assert.All(hauling.SettingLines, line => Assert.True(line.Usable));
        Assert.Equal(new[] { "Any", "100 t or more", "500 t or more", "1,000 t or more", "2,500 t or more", "5,000 t or more", "10,000 t or more", "50,000 t or more" },
            hauling.SettingLines[6].Choices.Select(choice => choice.Text));
        Assert.Equal(new object[] { true, false }, hauling.SettingLines[0].Choices.Select(choice => choice.Value));

        rig.Change("""{ "maxLy": 75 }""");                          // typed on the Market page, between two steps
        Assert.True(hauling.StepSetting("maxLy", 1));
        Assert.Equal(80, hauling.Settings.MaxLy);
        Assert.True(hauling.StepSetting("maxLy", 1));
        Assert.Equal(100, hauling.Settings.MaxLy);
        rig.Change("""{ "maxLy": 75 }""");
        Assert.True(hauling.StepSetting("maxLy", -1));
        Assert.Equal(60, hauling.Settings.MaxLy);
        rig.Change("""{ "maxLy": 300 }""");
        Assert.False(hauling.StepSetting("maxLy", 1));              // the end of a list that does not come round
        rig.Change("""{ "maxLy": 400 }""");
        Assert.True(hauling.StepSetting("maxLy", 1));               // beyond the last step: forward goes to the last step
        Assert.Equal(300, hauling.Settings.MaxLy);

        Assert.True(hauling.StepSetting("returns", 1));             // two choices that come round
        Assert.True(hauling.StepSetting("returns", 1));
        Assert.True(hauling.Settings.Returns);
        Assert.True(hauling.StepSetting("minSupply", -1) && hauling.StepSetting("buyLy", -1) && hauling.StepSetting("buyLy", -1) && hauling.StepSetting("buyLy", -1));
        Assert.False(hauling.StepSetting("buyLy", -1));
        Assert.False(hauling.StepSetting("minSupply", -1));
        Assert.False(hauling.StepSetting("jumpRange", 1));          // not on the menu
        Assert.Equal(("Any", "The system I am in"), (hauling.SettingLines[6].Text, hauling.SettingLines[3].Text));

        Assert.True(hauling.StepSetting("startMode", 1));           // "anywhere" has no distance from the player
        Assert.Equal(("Anywhere in the galaxy", false, "Not used when trades start anywhere"),
            (hauling.SettingLines[5].Text, hauling.SettingLines[3].Usable, hauling.SettingLines[3].Text));
        Assert.False(hauling.StepSetting("buyLy", 1));
        Assert.Equal(0, hauling.Settings.BuyLy);

        rig.After(0.7);                                             // all that stepping: one search
        Assert.Equal(2, rig.Search.Asked.Count);
    }

    [Fact]
    public void The_short_list_has_seven_orders_and_shows_five_lines_around_the_lit_one()
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        Hauling hauling = rig.Hauling;
        string Order() => string.Join(" ", hauling.SortedTrades.Select(trade => trade.CommodityId));

        var orders = new List<string>();
        foreach (ListSort _ in HaulMenu.ListSorts)
        {
            orders.Add($"{hauling.Settings.ListSort}: {Order()}");
            hauling.StepListSort(1);
        }
        Assert.Equal(new[]
        {
            "rank: gold bauxite silver",        // the search's own order
            "hour: gold bauxite silver",        // 30M, 20M, 10M an hour
            "ton: bauxite gold silver",         // 5,000, 3,000, 1,000 a ton
            "load: bauxite gold silver",        // 2.0M; 1.2M and 0.4M for the load back; 0.4M
            "jumps: gold bauxite silver",       // 4 for the whole trip; 8 and 8, where the search's order decides
            "ly: gold silver bauxite",          // 20, 36.1, 50 ly
            "age: bauxite gold silver",         // 1, 2, 30 hours
        }, orders);
        Assert.Equal("rank", hauling.Settings.ListSort);            // round to the start again
        hauling.SortListBy("jumps");                                // a column title clicked in mouse mode
        Assert.Equal("jumps", hauling.Settings.ListSort);
        hauling.SortListBy("sideways");                             // no such order: the search's own
        Assert.Equal("rank", hauling.Settings.ListSort);
        hauling.StepListSort(-1);
        Assert.Equal(("age", "age"), (hauling.Settings.ListSort, rig.Saved().ListSort));
        rig.After(1);
        Assert.Single(rig.Search.Asked);                            // the order is no reason to search

        Assert.Equal(5, HaulMenu.ListRows);
        Assert.Equal(0, HaulMenu.WindowTop(top: 0, lit: -1, count: 40));    // on the "order" line: the list stays where it is
        Assert.Equal(0, HaulMenu.WindowTop(top: 0, lit: 4, count: 40));
        Assert.Equal(1, HaulMenu.WindowTop(top: 0, lit: 5, count: 40));     // one past the last line shown: the list moves up one
        Assert.Equal(3, HaulMenu.WindowTop(top: 10, lit: 3, count: 40));    // above the first line shown
        Assert.Equal(35, HaulMenu.WindowTop(top: 0, lit: 39, count: 40));
        Assert.Equal(7, HaulMenu.WindowTop(top: 35, lit: -1, count: 12));   // a new, shorter list
        Assert.Equal(0, HaulMenu.WindowTop(top: 2, lit: 2, count: 3));      // fewer trades than lines
    }

    // ---- the first run: settings copied from the Elite Haul Overlay ----

    [Fact]
    public void The_first_run_copies_the_owners_haul_overlay_settings_and_never_touches_that_file()
    {
        string old = OldFile("owner.json");
        byte[] bytes = File.ReadAllBytes(old);
        DateTime written = File.GetLastWriteTimeUtc(old);

        using var rig = new HaulRig(HaulRig.In(Alpha), oldSettings: old);
        MarketSettings copied = rig.Hauling.Settings;
        Assert.Equal(("near", "trip", true, 14.0, 27.0, "auto", 100, 150, 12, 5000), (copied.StartMode, copied.RankBy, copied.Returns, copied.JumpRange,
            copied.JumpEmpty, copied.Pad, copied.BuyLy, copied.MaxLy, copied.MaxAgeHours, copied.MaxStarLs));
        Assert.Equal((0, 0, false, "horizons", 388, "any", "any", "any", "any", "rank"), (copied.MinSupply, copied.MinDemand, copied.Carriers, copied.Surface,
            copied.CargoOverride, copied.SrcPower, copied.DstPower, copied.SrcState, copied.DstState, copied.ListSort));
        Assert.Equal(new[] { "Quick one-way hops", "Long round trips" }, copied.Presets.Select(preset => preset.Name));
        Assert.Equal(new[] { "returns", "rankBy", "maxLy" }, copied.Presets[0].Set.Keys);
        Assert.All(rig.Hauling.PresetLines, line => Assert.False(line.Active));     // the owner's routes go to 150 ly, neither preset's distance

        SearchRequest first = rig.Search.Asked[0];                  // in use from the very first search
        Assert.Equal((100, 150, 12.0, 388, 14.0, 27.0, "trip", "horizons", 0, 0),
            (first.BuyLy, first.MaxLy, first.MaxAgeHours, first.Cargo, first.Jump, first.JumpEmpty, first.RankBy, first.Surface, first.MinSupply, first.MinDemand));
        MarketSettings saved = rig.Saved();                         // Studio's own from now on
        Assert.Equal(copied with { Presets = saved.Presets }, saved);
        Assert.Equal(new[] { "Quick one-way hops", "Long round trips" }, saved.Presets.Select(preset => preset.Name));
        Assert.Equal(bytes, File.ReadAllBytes(old));
        Assert.Equal(written, File.GetLastWriteTimeUtc(old));

        // The overlay's colour and background come from the same reading. The owner's are Studio's standard ones, and
        // nothing else is taken: the old keys, mouse mode, place and size have no counterpart in Studio's four boxes.
        (MarketSettings market, OverlaySettings own) = OldHaulSettings.Read(old)!;
        Assert.Equal((0.92, true, "#3D8BFF"), (own.Opacity, own.OnlyWhenElite, own.Accent));
        Assert.Equal(new OverlaySettings(), own);
        Assert.Equal(copied with { Presets = market.Presets, CopiedFrom = null }, market);

        // Hauling says that it copied, and from where, until the player has read that. It is not a preset's to hold or to change.
        Assert.Equal(old, rig.Hauling.CopiedFrom);
        Assert.True(rig.Hauling.SavePreset("Mine").Ok);
        Assert.DoesNotContain("copiedFrom", rig.Hauling.Settings.Presets[^1].Set.Keys);
        Assert.True(rig.Hauling.PresetLines[^1].Active);

        // What the player changes in Studio stays: the old file is copied from once. The copy is still news after a restart...
        rig.Change("""{ "maxLy": 60 }""");
        using var again = new HaulRig(HaulRig.In(Alpha), savedSettings: File.ReadAllText(rig.SettingsFile), oldSettings: old);
        Assert.Equal((60, 100, old), (again.Hauling.Settings.MaxLy, again.Hauling.Settings.BuyLy, again.Hauling.CopiedFrom));

        // ...until it has been read: that is saved, tells the screens, and starts no search.
        (int changes, int searches) = (again.Changes, again.Search.Asked.Count);
        again.Hauling.CopyNoteRead();
        again.After(1);
        Assert.Equal(((string?)null, changes + 1, searches), (again.Hauling.CopiedFrom, again.Changes, again.Search.Asked.Count));
        Assert.DoesNotContain("copiedFrom", File.ReadAllText(again.SettingsFile));
        using var later = new HaulRig(HaulRig.In(Alpha), savedSettings: File.ReadAllText(again.SettingsFile), oldSettings: old);
        Assert.Equal((60, (string?)null), (later.Hauling.Settings.MaxLy, later.Hauling.CopiedFrom));
    }

    [Fact]
    public void A_version_1_0_file_gets_every_correction_the_old_tool_made_to_it()
    {
        (MarketSettings market, OverlaySettings own) = OldHaulSettings.Read(OldFile("version-1.0.json"))!;
        Assert.Equal("horizons", market.Surface);                           // "settlements: false" is now one of the surface choices
        Assert.Equal((500, 50000), (market.MinSupply, market.MinDemand));   // typed 750 and 60000: the choice below
        Assert.Equal(new[] { "Quick one-way hops", "Long round trips" }, market.Presets.Select(preset => preset.Name));    // no presets yet: the two starters

        Assert.Equal((false, 22.5, 0.0, "M", 25, 60, 24, true), (market.Returns, market.JumpRange, market.JumpEmpty, market.Pad, market.BuyLy, market.MaxLy, market.MaxAgeHours, market.Carriers));
        Assert.Equal(("near", "hour", 5000, 0, "rank"), (market.StartMode, market.RankBy, market.MaxStarLs, market.CargoOverride, market.ListSort));
        // Of the overlay only the background and "only while Elite is in front" are taken; its keys, place and size are not.
        Assert.Equal(new OverlaySettings { Opacity = 0.5, OnlyWhenElite = false }, own);
    }

    [Fact]
    public void A_test_build_file_keeps_only_presets_that_hold_something()
    {
        (MarketSettings market, OverlaySettings own) = OldHaulSettings.Read(OldFile("test-build.json"))!;
        Assert.Equal("odyssey", market.Surface);                    // "settlements: true"
        Assert.Equal(0, market.MinSupply);                          // 99: below the first choice
        Assert.Equal((80, 15), (market.MaxLy, market.BuyLy));       // "80" is a number; "far" costs only itself
        Assert.Equal(("auto", 5000), (market.Pad, market.MaxStarLs));   // and so does a null
        Assert.Equal(new OverlaySettings(), own);                   // its keys and its size are not the overlay's to take

        Assert.Equal(new[] { "Padded name", "Typed supply", "Three", "Four", "Five", "Six", "Seven", "Eight" }, market.Presets.Select(preset => preset.Name));
        Assert.Equal("""{"maxLy":200}""", JsonSerializer.Serialize(market.Presets[0].Set));     // not the jump range or a key: a preset may not hold those
        Assert.Equal("""{"minSupply":500,"returns":false}""", JsonSerializer.Serialize(market.Presets[1].Set));   // 750 t: the choice below; "RETURNS": the setting's own spelling
    }

    [Fact]
    public void A_newer_file_is_taken_as_it_is()
    {
        MarketSettings market = OldHaulSettings.Read(OldFile("newer-keys-win.json"))!.Market;
        Assert.Equal("none", market.Surface);                   // it has "surface": the old "settlements" no longer counts
        Assert.Equal(1000, market.MinSupply);
        Assert.Empty(market.Presets);                           // an empty list: the player deleted the starters
    }

    [Fact]
    public void Without_a_readable_old_file_the_standard_settings_are_used()
    {
        Assert.Null(OldHaulSettings.Read(Path.Combine(Path.GetTempPath(), "EliteStudio no such folder", "settings.json")));
        Assert.Null(OldHaulSettings.Read(OldFile("cut-short.json")));

        string cutShort = File.ReadAllText(OldFile("cut-short.json"));
        using var rig = new HaulRig(HaulRig.In(Alpha), oldSettings: OldFile("cut-short.json"));
        Assert.Equal(new MarketSettings() with { Presets = rig.Hauling.Settings.Presets }, rig.Hauling.Settings);
        Assert.Equal(2, rig.Saved().Presets.Count);
        Assert.Equal(cutShort, File.ReadAllText(OldFile("cut-short.json")));    // left as it was found
    }

    [Fact]
    public void A_hand_edited_studio_settings_file_is_tidied_and_an_odd_value_costs_only_itself()
    {
        using var odd = new HaulRig(HaulRig.In(Alpha), oldSettings: OldFile("owner.json"), savedSettings: """
            { "market": { "maxLy": 9999, "minSupply": 750, "pad": "x", "buyLy": "far", "carriers": null, "maxAgeHours": 24, "presets": [
                { "name": " Kept ", "set": { "maxLy": 50, "hotNext": "F1" } }, { "name": "", "set": { "maxLy": 60 } }, { "name": "Empty", "set": { } },
                7, { "name": "Does not fit", "set": { "maxLy": "far" } } ] } }
            """);
        MarketSettings tidied = odd.Hauling.Settings;
        Assert.Equal((500, 500, "auto"), (tidied.MaxLy, tidied.MinSupply, tidied.Pad));
        Assert.Equal((15, false, 24), (tidied.BuyLy, tidied.Carriers, tidied.MaxAgeHours));    // "far" and null stay standard, not the old tool's 100; the rest is kept
        Assert.Equal(new PresetLine("Kept", "routes to 50 ly", Active: false), Assert.Single(odd.Hauling.PresetLines));

        // "market" is there but holds no settings at all: the standard ones, not a second copy from the old tool.
        using var unreadable = new HaulRig(HaulRig.In(Alpha), savedSettings: """{ "market": [ 1, 2 ] }""", oldSettings: OldFile("owner.json"));
        Assert.Equal(new MarketSettings() with { Presets = unreadable.Hauling.Settings.Presets }, unreadable.Hauling.Settings);
        Assert.Equal(2, unreadable.Hauling.Settings.Presets.Count);
        Assert.Single(unreadable.Search.Asked);
    }

    // ---- what a restart of Studio keeps: the accepted trade and an open lookup ----

    /// <summary>Studio's settings file as it is left at noon with the gold round trip (Alpha to Beta, silver back) accepted and flown up to one of its legs.</summary>
    static string LeftWithGold(string leg)
    {
        using var rig = new HaulRig(HaulRig.In(Alpha));
        rig.Hauling.ToggleAccept("F6");
        if (leg != "buy") rig.Play(HaulRig.Buy(Alpha, "gold"));
        if (leg == "back") rig.Play(HaulRig.Undock(Alpha), HaulRig.Jump(Beta), HaulRig.Dock(Beta), HaulRig.Sell(Beta, "gold"));
        Assert.Equal(("gold", leg, leg), (rig.Hauling.Accepted?.CommodityId, rig.Hauling.Leg, rig.Kept().Leg));
        return File.ReadAllText(rig.SettingsFile);
    }

    [Fact]
    public void An_accepted_trade_is_still_accepted_after_a_restart_with_its_prices_aged_and_a_list_under_it()
    {
        // Forty minutes after Studio was closed it starts again. The player has flown on meanwhile and is in space in Gamma.
        using var rig = new HaulRig(HaulRig.In(Gamma, docked: false), savedSettings: LeftWithGold("buy"), clock: Noon.AddMinutes(40));
        Hauling hauling = rig.Hauling;
        Assert.Null(hauling.Accepted);                              // (in space nothing asks for a search until the app's timer comes round)
        rig.After(0.5);

        TradeRow gold = hauling.Accepted!;
        Assert.Equal(("gold", "buy", false), (gold.CommodityId, hauling.Leg, hauling.ReturnLoaded));
        Assert.Same(gold, hauling.Shown);
        Assert.Equal("3h ago", gold.Updated);                       // its prices were two hours old forty minutes ago
        Assert.Equal((50.0, "BUY", "Alpha"), (gold.BuyDistanceLy, hauling.NextStop?.What, hauling.NextStop?.System));   // measured from Gamma now
        // The list under it is made this once without the player asking for it.
        Assert.Equal("Gamma", Assert.Single(rig.Search.Asked).StarSystem);
        Assert.Equal($"3 trades from Gamma   ·   {Keeper} stays on your accepted trade until you drop it", hauling.ListMessage);
        Assert.NotSame(gold, hauling.Trades[0]);                    // the list's own gold has today's prices; the accepted one keeps its own
        Assert.Equal(("buy", rig.Clock), (rig.Kept().Leg, rig.Kept().AcceptedAt));

        // From here on it is an accepted trade like any other.
        rig.Play(HaulRig.Jump(Alpha), HaulRig.Dock(Alpha), HaulRig.Buy(Alpha, "gold"));
        Assert.Equal(("sell", "sell"), (hauling.Leg, rig.Kept().Leg));
        Assert.Single(rig.Search.Asked);
        hauling.Drop();
        Assert.Null(rig.Kept().Accepted);
    }

    [Fact]
    public void After_a_restart_the_hold_and_the_dock_say_how_far_along_the_accepted_trade_is()
    {
        string gold = Hold(("gold", 400)), silver = Hold(("silver", 400)), nothing = Hold();
        static string Restarted(string leg, Place dockedAt, string hold)
        {
            using var rig = new HaulRig(HaulRig.In(dockedAt), savedSettings: LeftWithGold(leg), hold: hold, clock: Noon.AddMinutes(10));
            Assert.Single(rig.Search.Asked);
            // What is on disk afterwards is what came back: the next restart starts from there.
            Assert.Equal((rig.Hauling.Accepted?.CommodityId, rig.Hauling.Leg), (rig.Kept().Accepted?.CommodityId, rig.Kept().Leg));
            return rig.Hauling.Accepted is null ? "dropped" : rig.Hauling.Leg + (rig.Hauling.ReturnLoaded ? " loaded" : "");
        }

        Assert.Equal("buy", Restarted("buy", Delta, nothing));          // not bought yet, wherever the player is by now
        Assert.Equal("sell", Restarted("buy", Alpha, gold));            // bought while Studio was closed
        Assert.Equal("sell", Restarted("sell", Beta, gold));            // still on board
        Assert.Equal("dropped", Restarted("sell", Beta, nothing));      // sold meanwhile, here or anywhere: that trade is over
        Assert.Equal("back loaded", Restarted("back", Beta, silver));   // the load for the way back is in the hold
        Assert.Equal("back", Restarted("back", Gamma, nothing));
        Assert.Equal("dropped", Restarted("back", Alpha, silver));      // docked where the round trip ends
    }

    [Fact]
    public void An_accepted_trade_whose_prices_are_too_old_by_now_does_not_come_back()
    {
        string left = LeftWithGold("sell"), gold = Hold(("gold", 400));

        // The gold's prices were two hours old when it was accepted, and the player lets prices be 48 hours old.
        using var inTime = new HaulRig(HaulRig.In(Beta), savedSettings: left, hold: gold, clock: Noon.AddHours(46));
        Assert.Equal(("sell", 48.0, "2d ago"), (inTime.Hauling.Leg, inTime.Hauling.Accepted?.AgeHours, inTime.Hauling.Accepted?.Updated));
        using var tooLate = new HaulRig(HaulRig.In(Beta), savedSettings: left, hold: gold, clock: Noon.AddHours(46.1));
        Assert.Null(tooLate.Hauling.Accepted);
        Assert.Equal("3 trades from Beta", tooLate.Hauling.ListMessage);
        Assert.Null(tooLate.Kept().Accepted);                       // and it is forgotten for good

        // Time is counted on from the last restart, not from the start again.
        using var again = new HaulRig(HaulRig.In(Beta), savedSettings: File.ReadAllText(inTime.SettingsFile), hold: gold, clock: Noon.AddHours(46));
        Assert.Equal(48.0, again.Hauling.Accepted?.AgeHours);
    }

    [Fact]
    public void An_open_lookup_is_asked_again_after_a_restart_for_what_is_in_the_hold_by_then()
    {
        static SearchResult Answer(SearchRequest request) =>
            request.Mode == "lookup" ? HaulRig.Found(request, HaulRig.SellAt(request, Beta)) : HaulRig.Found(request, HaulRig.Gold());
        string cargo, picked, closed;
        using (var before = new HaulRig(HaulRig.In(Alpha), Answer, hold: Hold(("gold", 300), ("silver", 100))))
        {
            before.Hauling.SellMyCargo();
            before.Hauling.SellMyCargo();                           // on to the silver, the smaller load
            Assert.Equal(("cargo", "silver"), (before.Kept().LookSource, before.Kept().LookId));
            cargo = File.ReadAllText(before.SettingsFile);
            before.Hauling.WhereToSell("gold");
            picked = File.ReadAllText(before.SettingsFile);
            before.Hauling.BackToTrades();
            closed = File.ReadAllText(before.SettingsFile);
        }

        // Some of the silver is still on board, and by now it is the bigger load.
        using var rig = new HaulRig(HaulRig.In(Gamma), Answer, savedSettings: cargo, hold: Hold(("gold", 50), ("silver", 80)));
        Lookup look = rig.Hauling.Look!;
        Assert.Equal(("cargo", "silver", 80, "done"), (look.Source, look.Id, look.Count, look.Status));
        Assert.Equal(new[] { "lookup", "near" }, rig.Search.Asked.Select(request => request.Mode));     // asked afresh, with the trade list under it
        Assert.Equal(("Gamma", "silver", 80), (rig.Search.Asked[0].StarSystem, rig.Search.Asked[0].LookItem, rig.Search.Asked[0].LookCount));
        rig.Hauling.SellMyCargo();                                  // the key steps on through the hold as it is now
        Assert.Equal("gold", rig.Hauling.Look?.Id);

        // The silver is gone: there is nothing to look up, and nothing is kept of it.
        using var sold = new HaulRig(HaulRig.In(Gamma), Answer, savedSettings: cargo, hold: Hold(("gold", 50)));
        Assert.Null(sold.Hauling.Look);
        Assert.Equal("", sold.Kept().LookSource);

        // A picked commodity is looked up again as it was; a lookup the player had left stays shut.
        using var pick = new HaulRig(HaulRig.In(Gamma), Answer, savedSettings: picked);
        Assert.Equal(("pick", "gold", "done"), (pick.Hauling.Look?.Source, pick.Hauling.Look?.Id, pick.Hauling.Look?.Status));
        using var left = new HaulRig(HaulRig.In(Gamma), Answer, savedSettings: closed);
        Assert.Null(left.Hauling.Look);
    }

    [Fact]
    public void What_was_kept_waits_for_the_game_to_say_where_the_player_is_and_an_odd_file_brings_back_nothing()
    {
        // Studio starts before the game has written a journal: the trade it was closed with is neither shown nor forgotten.
        using var rig = new HaulRig([], savedSettings: LeftWithGold("buy"));
        rig.After(0.5);
        rig.Hauling.SellMyCargo();                                  // "your cargo hold is empty", and back again
        rig.Hauling.SellMyCargo();
        Assert.Null(rig.Hauling.Accepted);
        Assert.Equal("gold", rig.Kept().Accepted?.CommodityId);
        rig.Play(HaulRig.In(Alpha));
        Assert.Equal(("gold", "buy"), (rig.Hauling.Accepted?.CommodityId, rig.Hauling.Leg));

        // A file changed by hand: a trade with a text missing, a trade of nothing, a section that is none, a lookup of nothing.
        foreach (string odd in (string[])[
            """{ "hauling": { "accepted": { "commodityId": "gold", "commodity": null, "buyMarketId": 101 }, "leg": "buy", "acceptedAt": "2026-10-09T12:00:00" } }""",
            """{ "hauling": { "accepted": { "commodity": "Gold" }, "leg": "buy", "acceptedAt": "2026-10-09T12:00:00" } }""",
            """{ "hauling": "yes" }""",
            """{ "hauling": { "leg": null, "lookSource": "pick", "lookId": null } }""",
            """{ "hauling": { "lookSource": "cargo" } }"""])
        {
            using var started = new HaulRig(HaulRig.In(Alpha), savedSettings: odd);
            Assert.Null(started.Hauling.Accepted);
            Assert.Null(started.Hauling.Look);
            Assert.Equal(3, started.Hauling.Trades.Count);
        }
    }
}
