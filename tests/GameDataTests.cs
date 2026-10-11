using System.Text.Json;
using EliteStudio.Core;

namespace EliteStudio.Tests;

/// <summary>The community's tables carried inside the program: they load, and a look-up ignores capitals.</summary>
public class GameDataTests
{
    [Fact]
    public void A_commodity_is_found_by_its_code_however_it_is_spelled()
    {
        Assert.Equal("Low Temperature Diamonds", GameData.CommodityName("lowtemperaturediamond"));   // Ardent Insight itself spells this one "low temp. diamonds"
        Assert.Equal("Agri-Medicines", GameData.CommodityName("AgriculturalMedicines"));             // the code is not the name without its spaces
        Assert.Equal("Agronomic Treatment", GameData.CommodityName("agronomictreatment"));
        Assert.Equal("Gold", GameData.CommodityName("GOLD"));
        Assert.Equal("Lavian Brandy", GameData.CommodityName("lavianbrandy"));                       // rare goods too
    }

    [Fact]
    public void A_commodity_the_table_does_not_have_gets_its_code_with_a_capital()
    {
        Assert.Equal("Somethingnew", GameData.CommodityName("somethingnew"));
    }

    [Fact]
    public void Materials_carry_their_kind_and_grade()
    {
        GameData.Material scrap = GameData.Materials["mechanicalscrap"];
        Assert.Equal(("Mechanical Scrap", "Manufactured", 1), (scrap.Name, scrap.Kind, scrap.Grade));
        Assert.Equal("Raw", GameData.Materials["iron"].Kind);
        Assert.Equal("Encoded", GameData.Materials["embeddedfirmware"].Kind);
        Assert.Equal("Guardian Sentinel Weapon Parts", GameData.Materials["guardian_sentinel_weaponparts"].Name);
        Assert.Equal("Untypical Shield Scans", GameData.Materials["shielddensityreports"].Name);   // the table has a space after this one
    }

    [Fact]
    public void Modules_carry_their_name_size_and_rating()
    {
        GameData.Module drive = GameData.Modules["int_hyperdrive_size5_class5"];
        Assert.Equal(("Frame Shift Drive", 5, "A"), (drive.Name, drive.Class, drive.Rating));
        GameData.Module flak = GameData.Modules["Hpt_FlakMortar_Fixed_Medium"];
        Assert.Equal(("Remote Release Flak Launcher", "Fixed"), (flak.Name, flak.Mount));
    }

    [Fact]
    public void A_module_the_table_lists_twice_is_known_by_its_first_everyday_row()
    {
        // 22 everyday modules are listed again further down, under the same code, as pre-engineered special
        // editions. The first row is the one kept, so no module goes by its edition's name.
        Assert.Equal("Fragment Cannon", GameData.Modules["hpt_slugshot_gimbal_large"].Name);   // not the "Double Screaming Fragment Cannon"
        Assert.Equal("Seeker Missile Rack", GameData.Modules["Hpt_BasicMissileRack_Fixed_Medium"].Name);   // listed four times
        Assert.Equal("Power Distributor", GameData.Modules["int_powerdistributor_size5_class5"].Name);
        Assert.Equal(22, GameData.Modules.Values.Count(module => GameData.SpecialEdition(module.Symbol) is not null));
        Assert.DoesNotContain(GameData.Modules.Values, module => module.Name == GameData.SpecialEdition(module.Symbol));
        // The edition's name is still there to ask for, because Spansh files the module under it.
        Assert.Equal("Double Screaming Fragment Cannon", GameData.SpecialEdition("hpt_slugshot_gimbal_large"));
        Assert.Equal("Balanced Power Distributor", GameData.SpecialEdition("Int_PowerDistributor_Size5_Class5"));
        Assert.Null(GameData.SpecialEdition("int_hyperdrive_size5_class5"));
    }

    [Fact]
    public void Ships_and_engineers_are_listed()
    {
        Assert.Equal("Type-10 Defender", GameData.Ships["type9_military"].Name);
        Assert.Equal("Type-11 Prospector", GameData.Ships["lakonminer"].Name);
        Assert.Contains(GameData.Engineers, e => e.Name == "Felicity Farseer" && e.Id == 300100);
    }

    [Fact]
    public void Every_ship_in_the_numbers_table_is_found_in_the_names_table()
    {
        // By the game's id, or else by name: for three of the newest ships (Corsair, Caspian Explorer,
        // Kestrel Mk II) the two tables disagree about the id.
        var ids = GameData.Ships.Values.Select(s => s.Id).ToHashSet();
        var names = GameData.Ships.Values.Select(s => s.Name).ToHashSet();
        var ships = GameData.Coriolis.GetProperty("ships").EnumerateArray().SelectMany(file => file.EnumerateObject()).ToList();
        Assert.True(ships.Count >= 47);
        Assert.All(ships, ship => Assert.True(
            ids.Contains(ship.Value.Whole("edID")) || names.Contains(ship.Value.GetProperty("properties").Text("name")), ship.Name));
    }

    [Fact]
    public void The_numbers_table_holds_modules_and_engineering_recipes()
    {
        JsonElement all = GameData.Coriolis;
        JsonElement drives = all.GetProperty("standard").EnumerateArray().Select(file => file.Field("fsd")).First(list => list is not null)!.Value;
        JsonElement drive = drives.EnumerateArray().First(d => d.Text("symbol") == "Int_Hyperdrive_Size5_Class5");
        Assert.Equal(1050, drive.Number("optmass"));
        Assert.True(all.GetProperty("hardpoints").GetArrayLength() > 30);
        Assert.True(all.GetProperty("internal").GetArrayLength() > 30);
        JsonElement range = all.GetProperty("blueprints").GetProperty("FSD_LongRange");
        Assert.Equal("Increased range", range.Text("name"));
        Assert.True(range.GetProperty("grades").GetProperty("5").GetProperty("components").EnumerateObject().Any());
        Assert.True(all.GetProperty("modules").TryGetProperty("fsd", out _));
        Assert.True(all.GetProperty("modifications").TryGetProperty("optmass", out _));
        Assert.True(all.GetProperty("specials").TryGetProperty("special_fsd_heavy", out _));
        Assert.True(all.GetProperty("modifierActions").TryGetProperty("special_fsd_heavy", out _));
    }

    [Fact]
    public void The_type_11_lands_on_a_medium_pad()
    {
        Assert.Equal(("Type-11 Prospector", 2), (ShipMath.ShipName("lakonminer"), ShipMath.ShipPad("lakonminer")));
    }
}
