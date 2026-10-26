using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using EliteStudio.UI;

namespace EliteStudio.Tests;

/// <summary>Studio UI, second half: finding and choosing the game folder, links, long paths, Elite's own colour file, the theme library.</summary>
public sealed partial class UiTests
{
    // ------------------------------------------------------------ the game folder

    [Fact]
    public async Task Without_the_mod_or_without_a_game_the_routes_say_so_instead_of_failing()
    {
        Rig bare = NewRig("bare");
        StageGame(bare.Game, withMod: false);
        bare.Start(bare.Game);
        JsonElement status = await bare.Ok("GET", "/status");
        Assert.Equal(bare.Game, status.GetProperty("game").GetProperty("path").GetString());
        Assert.Equal("Custom", status.GetProperty("game").GetProperty("store").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("game").GetProperty("version").ValueKind);
        Assert.Equal("""{"installed":false,"version":null,"date":null,"forGame":null,"iniDir":null,"linkTarget":null,"files":{},"missing":["d3dx.ini","d3d11.dll","EDHM-ini"]}""", Compact(status.GetProperty("edhm")));
        Assert.Equal("""{"ok":true,"files":{}}""", Compact(await bare.Ok("GET", "/ini")));
        Assert.Equal("HUD color files were not found in the selected game folder.", await bare.Refused("POST", "/save", OneChange));
        Assert.Equal("No HUD color files were found, so there is nothing to back up.", await bare.Refused("POST", "/backup", "{}"));
        Assert.Equal("That folder was not found.", await bare.Refused("POST", "/open", """{"what":"edhm"}"""));

        // The practice folder named at start-up is not there: no game at all, and the real one is never looked for.
        Rig none = NewRig("none");
        none.Found = [GameFolder.At(bare.Game, "Steam")];
        none.Start(Path.Combine(temp, "not there"));
        status = await none.Ok("GET", "/status");
        Assert.Equal(JsonValueKind.Null, status.GetProperty("game").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("edhm").ValueKind);
        Assert.False(status.TryGetProperty("gameConfirmed", out _));   // absent, not false: there is nothing to confirm
        Assert.Equal(0, status.GetProperty("games").GetArrayLength());
        Assert.Equal("No game folder to confirm.", await none.Refused("POST", "/confirm-game", "{}"));
        Assert.Equal("That folder was not found.", await none.Refused("POST", "/open", """{"what":"game"}"""));
        Assert.Equal("""{"ok":true,"files":{}}""", Compact(await none.Ok("GET", "/ini")));
    }

    [Fact]
    public void Installs_are_found_in_each_launchers_folders_once_each()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows' launchers, with Windows paths: Linux has its own check of Steam's folders (SteamGamesTests).");
        string steam = Path.Combine(temp, "Steam"), second = Path.Combine(temp, "Second Library");
        string viaSteam = Path.Combine(steam, @"steamapps\common\Elite Dangerous\Products\elite-dangerous-odyssey-64");
        string viaLauncher = Path.Combine(temp, @"Frontier\EDLaunch\Products\elite-dangerous-odyssey-64");
        string viaEpic = Path.Combine(temp, @"Epic\EliteDangerous\elite-dangerous-odyssey-64");
        StageGame(viaSteam);
        StageGame(viaLauncher, withMod: false);
        StageGame(viaEpic, withMod: false);
        Directory.CreateDirectory(Path.Combine(temp, @"Empty\Products\elite-dangerous-odyssey-64"));   // the folder, but no game in it

        // Steam lists its libraries with doubled backslashes, and names its own folder again in lower case.
        File.WriteAllText(Path.Combine(steam, @"steamapps\libraryfolders.vdf"),
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + steam.ToLowerInvariant().Replace(@"\", @"\\") + "\"\n\t}\n"
            + "\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + second.Replace(@"\", @"\\") + "\"\n\t}\n}\n");
        Assert.Equal([steam, second], GameFolder.SteamLibraries(steam));

        List<GameFolder> found = GameFolder.FindIn(
        [
            (Path.Combine(steam, @"steamapps\common\Elite Dangerous"), "Steam"),
            (Path.Combine(steam.ToUpperInvariant(), @"steamapps\common\Elite Dangerous"), "Steam"),   // same folder, other spelling
            (Path.Combine(second, @"steamapps\common\Elite Dangerous"), "Steam"),                     // library without the game
            (Path.Combine(temp, "Epic", "EliteDangerous"), "Epic"),
            ("", "Epic"),
            (Path.Combine(temp, "Frontier"), "Frontier"),
            (Path.Combine(temp, "Empty"), "Frontier"),
        ]);
        Assert.Equal([new GameFolder(viaSteam, "Steam", true), new GameFolder(viaEpic, "Epic", false), new GameFolder(viaLauncher, "Frontier", false)], found);
    }

    [Fact]
    public async Task The_game_is_chosen_by_the_player_first_then_by_where_the_mod_is_and_remembered()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The player types the folder in other capitals, which only Windows takes as the same folder.");
        string plain = Path.Combine(temp, "plain"), modded = Path.Combine(temp, "modded"), custom = Path.Combine(temp, "custom");
        StageGame(plain, withMod: false);
        StageGame(modded);
        StageGame(custom, withMod: false);
        Rig rig = NewRig();
        rig.Found = [GameFolder.At(plain, "Steam"), GameFolder.At(modded, "Epic")];
        rig.Start(null);

        // Nothing chosen yet: the install that has the mod wins over the first one found.
        JsonElement status = await rig.Ok("GET", "/status");
        Assert.Equal("""{"path":"MODDED","store":"Epic","version":"2026.09.03.332841"}""", Compact(status.GetProperty("game")).Replace(JsonSerializer.Serialize(modded).Trim('"'), "MODDED"));
        // The page lists the installs by these exact names and sends "path" back to choose one.
        Assert.Equal(2, status.GetProperty("games").GetArrayLength());
        Assert.Equal($$"""{"path":{{JsonSerializer.Serialize(plain)}},"store":"Steam","edhm":false}""", Compact(status.GetProperty("games")[0]));
        Assert.Equal($$"""{"path":{{JsonSerializer.Serialize(modded)}},"store":"Epic","edhm":true}""", Compact(status.GetProperty("games")[1]));
        Assert.False(status.GetProperty("testMode").GetBoolean());
        Assert.False(status.GetProperty("gameConfirmed").GetBoolean());

        status = await rig.Ok("POST", "/confirm-game", "{}");
        Assert.True(status.GetProperty("gameConfirmed").GetBoolean());
        Assert.Equal(modded, SavedUi(rig).GetProperty("gameConfirmed").GetString());
        Assert.Equal(JsonValueKind.Null, SavedUi(rig).GetProperty("gameDir").ValueKind);   // confirmed, but not chosen by hand

        // The player picks the other install, typed in another case: it is the found one, so it keeps its shop.
        Assert.Equal("That folder does not contain EliteDangerous64.exe.", await rig.Refused("POST", "/select-game", JsonSerializer.Serialize(new { path = temp })));
        status = await rig.Ok("POST", "/select-game", JsonSerializer.Serialize(new { path = plain.ToUpperInvariant() }));
        Assert.Equal(plain, status.GetProperty("game").GetProperty("path").GetString());
        Assert.Equal("Steam", status.GetProperty("game").GetProperty("store").GetString());
        Assert.True(status.GetProperty("gameConfirmed").GetBoolean());
        Assert.False(status.GetProperty("edhm").GetProperty("installed").GetBoolean());

        // A folder no launcher knows about.
        status = await rig.Ok("POST", "/select-game", JsonSerializer.Serialize(new { path = custom }));
        Assert.Equal("Custom", status.GetProperty("game").GetProperty("store").GetString());
        Assert.Equal(custom, SavedUi(rig).GetProperty("gameDir").GetString());
        Assert.Equal(custom, SavedUi(rig).GetProperty("gameConfirmed").GetString());

        // Next launch: still that folder, still confirmed.
        status = await rig.Start(null).Ok("GET", "/status");
        Assert.Equal(custom, status.GetProperty("game").GetProperty("path").GetString());
        Assert.True(status.GetProperty("gameConfirmed").GetBoolean());

        // The chosen folder no longer holds the game: back to what can be found, and the page asks again.
        File.Delete(Path.Combine(custom, "EliteDangerous64.exe"));
        status = await rig.Start(null).Ok("GET", "/status");
        Assert.Equal(modded, status.GetProperty("game").GetProperty("path").GetString());
        Assert.False(status.GetProperty("gameConfirmed").GetBoolean());

        // No install has the mod: the first one found.
        rig.Found = [GameFolder.At(plain, "Steam"), GameFolder.At(custom, "Frontier")];
        Assert.Equal(plain, (await rig.Start(null).Ok("GET", "/status")).GetProperty("game").GetProperty("path").GetString());
    }

    // ------------------------------------------------------------ installing the HUD mod

    /// <summary>A zip laid out like the mod's own: the fixture game folder's mod files plus stand-ins for the big ones.</summary>
    static byte[] ModZip(params (string Name, string Text)[] extra)
    {
        string from = Path.Combine(Fixtures, "game");
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create))
        {
            void Add(string name, byte[] content)
            {
                using Stream entry = zip.CreateEntry(name).Open();
                entry.Write(content);
            }
            zip.CreateEntry("EDHM-ini/");
            zip.CreateEntry("EDHM-ini/3rdPartyMods/");
            Add("d3dx.ini", File.ReadAllBytes(Path.Combine(from, "d3dx.ini")));
            foreach (string name in ColourFiles.Names) Add($"EDHM-ini/{name}.ini", File.ReadAllBytes(Path.Combine(from, "EDHM-ini", name + ".ini")));
            Add("d3d11.dll", "the mod's loader"u8.ToArray());
            Add("ShaderFixes/one-hlsl.txt", "a shader"u8.ToArray());
            foreach (var (name, text) in extra) Add(name, System.Text.Encoding.UTF8.GetBytes(text));
        }
        return buffer.ToArray();
    }

    [Fact]
    public async Task The_mod_is_installed_into_a_game_without_it_and_colours_can_be_saved_at_once()
    {
        Rig rig = NewRig();
        StageGame(rig.Game, withMod: false);
        rig.Found = [GameFolder.At(rig.Game, "Frontier")];
        rig.Start(null);
        Assert.False((await rig.Ok("GET", "/status")).GetProperty("games")[0].GetProperty("edhm").GetBoolean());
        rig.ModDownload = () => ("22.03", ModZip());

        JsonElement done = await rig.Ok("POST", "/install-mod", "{}");
        Assert.Equal("22.03", done.GetProperty("version").GetString());
        Assert.Equal(7, done.GetProperty("written").GetInt32());
        JsonElement mod = done.GetProperty("status").GetProperty("edhm");
        Assert.True(mod.GetProperty("installed").GetBoolean());
        Assert.Equal("22.03", mod.GetProperty("version").GetString());
        Assert.False(mod.TryGetProperty("missing", out _));   // left out once nothing is
        Assert.True(GameFolder.At(rig.Game, "Frontier").Edhm);   // what the next search for installs will say
        Assert.Equal(["d3d11.dll", "d3dx.ini", "EliteDangerous64.exe"], Directory.GetFiles(rig.Game).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(Directory.Exists(Path.Combine(rig.IniDir, "3rdPartyMods")));
        Assert.True(File.Exists(Path.Combine(rig.Game, "ShaderFixes", "one-hlsl.txt")));
        Assert.Equal(0, new FileInfo(Path.Combine(rig.Game, "EliteDangerous64.exe")).Length);   // the game's own file is as it was

        Assert.Equal(1, (await rig.Ok("POST", "/save", OneChange)).GetProperty("written").GetInt32());
        Assert.Equal("The HUD mod is already in this game folder.", await rig.Refused("POST", "/install-mod", "{}"));
    }

    [Fact]
    public async Task Installing_over_a_mod_that_lost_a_file_says_which_and_keeps_the_players_colours()
    {
        Rig rig = NewRig();
        StageGame(rig.Game);
        File.Delete(Path.Combine(rig.Game, "d3d11.dll"));   // what an antivirus program does
        string coloured = ColourFiles.PathOf(rig.IniDir, "Advanced");
        File.WriteAllText(coloured, File.ReadAllText(coloured).Replace("x232 = 0.5", "x232 = 0.9"));
        File.Delete(ColourFiles.PathOf(rig.IniDir, "SuitHud"));
        rig.Start(rig.Game);

        JsonElement mod = (await rig.Ok("GET", "/status")).GetProperty("edhm");
        Assert.False(mod.GetProperty("installed").GetBoolean());
        Assert.Equal("""["d3d11.dll"]""", Compact(mod.GetProperty("missing")));

        rig.ModDownload = () => ("22.03", ModZip());
        JsonElement done = await rig.Ok("POST", "/install-mod", "{}");
        Assert.True(done.GetProperty("status").GetProperty("edhm").GetProperty("installed").GetBoolean());
        Assert.Contains("x232 = 0.9", File.ReadAllText(coloured));                    // kept
        Assert.True(File.Exists(ColourFiles.PathOf(rig.IniDir, "SuitHud")));           // put back
        Assert.Equal("the mod's loader", File.ReadAllText(Path.Combine(rig.Game, "d3d11.dll")));
    }

    [Fact]
    public async Task The_mod_is_not_installed_while_Elite_runs_without_a_game_or_from_a_download_that_is_not_the_mod()
    {
        Rig none = NewRig("none").Start(Path.Combine(temp, "not there"));
        Assert.Equal("No game folder to install the HUD mod into.", await none.Refused("POST", "/install-mod", "{}"));

        Rig rig = NewRig();
        StageGame(rig.Game, withMod: false);
        rig.Start(rig.Game);
        await rig.Ok("GET", "/status");
        string[] before = Directory.GetFileSystemEntries(Path.Combine(temp, "pc", "game"), "*", SearchOption.AllDirectories);
        void NothingWritten() => Assert.Equal(before, Directory.GetFileSystemEntries(Path.Combine(temp, "pc", "game"), "*", SearchOption.AllDirectories));

        // The PC is offline: the reason is passed on in words.
        Assert.Equal("The HUD mod could not be downloaded (No such host is known.).", await rig.Refused("POST", "/install-mod", "{}"));

        int downloads = 0;
        rig.ModDownload = () => { downloads++; return ("22.03", ModZip()); };
        rig.Running = true;
        Assert.Equal("Close Elite Dangerous first: the HUD mod cannot be installed while the game is running.", await rig.Refused("POST", "/install-mod", "{}"));
        Assert.Equal(0, downloads);   // not even fetched
        rig.Running = false;

        const string notTheMod = "The download was not the HUD mod as Studio knows it, so nothing was installed. Get it from the mod's page instead.";
        rig.ModDownload = () => ("22.03", "<html>Not Found</html>"u8.ToArray());
        Assert.Equal(notTheMod, await rig.Refused("POST", "/install-mod", "{}"));
        var other = new MemoryStream();
        using (var zip = new ZipArchive(other, ZipArchiveMode.Create, leaveOpen: true)) zip.CreateEntry("readme.txt");
        rig.ModDownload = () => ("22.03", other.ToArray());
        Assert.Equal(notTheMod, await rig.Refused("POST", "/install-mod", "{}"));
        // The colour files alone are not the mod either: without its loader nothing would be coloured.
        var half = new MemoryStream();
        using (var zip = new ZipArchive(half, ZipArchiveMode.Create, leaveOpen: true)) { zip.CreateEntry("EDHM-ini/Advanced.ini"); zip.CreateEntry("d3dx.ini"); }
        rig.ModDownload = () => ("22.03", half.ToArray());
        Assert.Equal(notTheMod, await rig.Refused("POST", "/install-mod", "{}"));
        // A name that leads out of the game folder refuses the whole zip, the good files too.
        rig.ModDownload = () => ("22.03", ModZip((@"..\..\outside.txt", "x")));
        Assert.Equal(notTheMod, await rig.Refused("POST", "/install-mod", "{}"));
        rig.ModDownload = () => ("22.03", ModZip(("../../../outside.txt", "x")));
        Assert.Equal(notTheMod, await rig.Refused("POST", "/install-mod", "{}"));
        NothingWritten();
        Assert.Empty(Directory.GetFiles(temp, "outside.txt", SearchOption.AllDirectories));
    }

    /// <summary>Stands in for the mod's page on GitHub: its list of files, then the file asked for.</summary>
    sealed class ModPage(string listing, byte[] zip) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];
        public string UserAgent { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Asked.Add(request.RequestUri!.AbsoluteUri);
            UserAgent = request.Headers.UserAgent.ToString();
            HttpContent content = request.RequestUri.Host == "api.github.com" ? new StringContent(listing) : new ByteArrayContent(zip);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    [Fact]
    public void The_newest_zip_on_the_mods_page_is_the_one_downloaded()
    {
        // The page as it is, in short: folders, notes, and (as could happen) older zips left beside the new one.
        const string listing = """
            [{"name":"3rdPartyMods","type":"dir"},{"name":"EDHM Patch Notes - FDev U22.x.md","type":"file"},
             {"name":"EDHM_Odyssey_v9.11.zip","type":"file"},{"name":"EDHM_Odyssey_v22.03.zip","type":"file"},{"name":"EDHM_Odyssey_v22.1.zip","type":"file"},
             {"name":"EDHM_Odyssey_v99.zip.exe","type":"file"},{"name":"README.md","type":"file"}]
            """;
        var page = new ModPage(listing, [1, 2, 3]);
        var (version, zip) = ModInstall.Download(page);
        Assert.Equal("22.03", version);
        Assert.Equal([1, 2, 3], zip);
        Assert.Equal(["https://api.github.com/repos/psychicEgg/EDHM/contents/Odyssey", "https://raw.githubusercontent.com/psychicEgg/EDHM/main/Odyssey/EDHM_Odyssey_v22.03.zip"], page.Asked);
        Assert.StartsWith("EliteStudio/", page.UserAgent);

        // Whatever goes wrong is said in words the page can show.
        static string Refused(HttpMessageHandler network) => Assert.Throws<InvalidOperationException>(() => ModInstall.Download(network)).Message;
        Assert.StartsWith("The HUD mod could not be downloaded (its page lists no download Studio knows).", Refused(new ModPage("""[{"name":"README.md"}]""", [])));
        Assert.StartsWith("The HUD mod could not be downloaded (its page did not list its files).", Refused(new ModPage("""{"message":"API rate limit exceeded"}""", [])));
        Assert.StartsWith("The HUD mod could not be downloaded (", Refused(new ModPage("<html>", [])));
        Assert.StartsWith("The HUD mod could not be downloaded (the download is far bigger than the mod).", Refused(new ModPage(listing, new byte[50_000_001])));
        Assert.StartsWith("The HUD mod could not be downloaded (No such host is known.).", Refused(new SpanshStandIn()));
    }

    /// <summary>
    /// Goes out to the internet, with two requests to the mod's page on GitHub, to see that the newest
    /// zip is still found there and still unpacks into a working install. Skipped unless the environment
    /// variable ELITE_STUDIO_LIVE is 1; run it on its own, as the top of TradeSearchLiveTests.cs shows, with
    /// -method "*Live_the_mods_page*". The practice folder goes when the test ends: no copy of the mod is kept.
    /// </summary>
    [Fact]
    public void Live_the_mods_page_gives_a_zip_that_installs()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ELITE_STUDIO_LIVE") == "1", "Set ELITE_STUDIO_LIVE=1 to download the HUD mod from its page over the internet.");
        Rig rig = NewRig();
        StageGame(rig.Game, withMod: false);

        var (version, zip) = ModInstall.Download(null);
        int written = ModInstall.Unpack(zip, rig.Game);

        ModInfo mod = GameFolder.At(rig.Game, "Custom").Mod();
        Assert.True(mod.Installed);
        Assert.Equal(version, mod.Version);   // the zip's name and the mod's own settings file agree
        Assert.All(mod.Files, file => Assert.True(file.Value, file.Key));
        Assert.True(written > 50);
        Assert.Equal(0, new FileInfo(Path.Combine(rig.Game, "EliteDangerous64.exe")).Length);
    }

    [Fact]
    public async Task A_search_for_the_game_that_fails_is_made_again_by_the_next_request()
    {
        string game = Path.Combine(temp, "game");
        StageGame(game);
        Rig rig = NewRig();
        rig.Found = [GameFolder.At(game, "Steam")];
        rig.SearchFails = new IOException("The device is not ready.");
        rig.Start(null);

        Assert.Equal("The device is not ready.", await rig.Refused("GET", "/status"));
        rig.SearchFails = null;
        Assert.Equal(game, (await rig.Ok("GET", "/status")).GetProperty("game").GetProperty("path").GetString());
    }

    [Fact]
    public async Task A_data_folder_given_as_a_relative_path_is_used_by_its_full_path()
    {
        // Windows only uses the Recycle Bin for a file named by its full path: given less, it deletes the file for good.
        string full = Path.Combine(temp, "pc");
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, full);
        Assert.SkipWhen(Path.IsPathFullyQualified(relative), "The practice folder is on another drive than the tests, so it has no relative path.");
        Rig rig = new(relative);
        StageGame(rig.Game);
        rig.Start(rig.Game);

        await rig.Ok("POST", "/mythemes", """{"name":"Gone","values":{"Advanced":{"x232":"0.1"}}}""");
        await rig.Ok("POST", "/mythemes/delete", """{"id":"Gone"}""");
        Assert.Equal([Path.Combine(full, "data", "ui", "My Themes", "Gone.json")], rig.Recycled);

        await rig.Ok("POST", "/open", """{"what":"tool"}""");
        Assert.Equal([Path.Combine(full, "data")], rig.Shown);
        JsonElement status = await rig.Ok("GET", "/status");
        Assert.Equal(Path.Combine(full, "data"), status.GetProperty("root").GetString());
        Assert.Equal(Path.Combine(full, "data", "ui", "Backups"), status.GetProperty("backupsDir").GetString());
    }

    [Fact]
    public async Task Colour_files_kept_elsewhere_behind_a_link_are_read_and_saved_through_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The link is made by Windows' own command (mklink).");
        Rig rig = NewRig();
        StageGame(rig.Game);
        string store = Path.Combine(temp, "mod manager", "EDHM-ini");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        Directory.Move(rig.IniDir, store);
        using (Process mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{rig.IniDir}\" \"{store}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }
        links.Add(rig.IniDir);
        rig.Start(rig.Game);

        JsonElement mod = (await rig.Ok("GET", "/status")).GetProperty("edhm");
        Assert.True(mod.GetProperty("installed").GetBoolean());
        Assert.Equal(rig.IniDir, mod.GetProperty("iniDir").GetString());
        Assert.Equal(store, mod.GetProperty("linkTarget").GetString());

        JsonElement saved = await rig.Ok("POST", "/save", OneChange);
        Assert.Equal(1, saved.GetProperty("written").GetInt32());
        Assert.Contains("\nx232 = 0.1\n", File.ReadAllText(Path.Combine(store, "Advanced.ini")));
        Assert.Equal(4, Directory.GetFiles(store).Length);   // nothing left behind next to the real files
        Assert.Contains("x232 = 0.5", File.ReadAllText(Path.Combine(rig.Backups, saved.GetProperty("backup").GetString()!, "Advanced.ini")));
    }

    [Fact]
    public async Task A_mod_unpacked_with_other_capitals_is_read_and_saved_under_its_own_names()
    {
        // Windows ignores capitals in file names by itself. Linux does not, so there Studio finds the real names.
        Rig rig = NewRig();
        StageGame(rig.Game);
        static void Rename(string from, string to)
        {
            // Through another name: a rename that only changes capitals is not one on Windows.
            if (Directory.Exists(from)) { Directory.Move(from, from + ".moving"); Directory.Move(from + ".moving", to); }
            else { File.Move(from, from + ".moving"); File.Move(from + ".moving", to); }
        }
        Rename(Path.Combine(rig.Game, "d3dx.ini"), Path.Combine(rig.Game, "D3DX.INI"));
        Rename(Path.Combine(rig.Game, "d3d11.dll"), Path.Combine(rig.Game, "D3D11.DLL"));
        Rename(Path.Combine(rig.Game, "VersionInfo.txt"), Path.Combine(rig.Game, "versioninfo.txt"));
        string colours = Path.Combine(rig.Game, "edhm-INI");
        Rename(rig.IniDir, colours);
        Rename(Path.Combine(colours, "Advanced.ini"), Path.Combine(colours, "ADVANCED.ini"));
        rig.Start(rig.Game);

        JsonElement status = await rig.Ok("GET", "/status");
        JsonElement mod = status.GetProperty("edhm");
        Assert.True(mod.GetProperty("installed").GetBoolean(), Compact(mod));
        Assert.Equal(JsonValueKind.String, mod.GetProperty("version").ValueKind);
        Assert.Equal(JsonValueKind.String, status.GetProperty("game").GetProperty("version").ValueKind);
        Assert.All(mod.GetProperty("files").EnumerateObject(), file => Assert.True(file.Value.GetBoolean(), file.Name));
        Assert.Equal(4, (await rig.Ok("GET", "/ini")).GetProperty("files").EnumerateObject().Count());

        Assert.Equal(1, (await rig.Ok("POST", "/save", OneChange)).GetProperty("written").GetInt32());
        // Written into the file that is there, not a second one beside it.
        Assert.Equal(4, Directory.GetFiles(colours).Length);
        Assert.Contains("\nx232 = 0.1\n", File.ReadAllText(Path.Combine(colours, "ADVANCED.ini")));
    }

    [Fact]
    public async Task Folders_deeper_than_the_old_260_character_limit_work()
    {
        string deep = Path.Combine(temp, new string('a', 120), new string('b', 120));
        Rig rig = new(deep);
        StageGame(rig.Game);
        rig.Start(rig.Game);
        Assert.True(rig.IniDir.Length > 300 && rig.Backups.Length > 280);

        JsonElement saved = await rig.Ok("POST", "/save", OneChange);
        Assert.Equal(1, saved.GetProperty("written").GetInt32());
        Assert.Contains("\nx232 = 0.1\n", File.ReadAllText(ColourFiles.PathOf(rig.IniDir, "Advanced")));
        Assert.Equal(4, Directory.GetFiles(rig.IniDir).Length);

        string name = (await rig.Ok("POST", "/backup", "{}")).GetProperty("name").GetString()!;
        JsonElement backup = await rig.Ok("GET", "/backup?name=" + Uri.EscapeDataString(name));
        Assert.Contains("\nx232 = 0.1\n", backup.GetProperty("files").GetProperty("Advanced").GetString());
        Assert.Equal(2, (await rig.Ok("GET", "/backups")).GetProperty("items").GetArrayLength());

        await rig.Ok("POST", "/mythemes", """{"name":"Deep","values":{"Advanced":{"x232":"0.1"}}}""");
        Assert.Equal("Deep", (await rig.Ok("GET", "/theme?src=mine&id=Deep")).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Open_shows_the_folder_asked_for_and_makes_Studios_own_folders_if_needed()
    {
        Rig rig = Practice();
        foreach (string what in new[] { "tool", "backups", "mythemes", "game", "edhm" })
            Assert.Equal("""{"ok":true}""", Compact(await rig.Ok("POST", "/open", JsonSerializer.Serialize(new { what }))));
        Assert.Equal([rig.Data, rig.Backups, rig.Themes, rig.Game, rig.IniDir], rig.Shown);
        Assert.True(Directory.Exists(rig.Backups) && Directory.Exists(rig.Themes));
        Assert.Equal("That folder was not found.", await rig.Refused("POST", "/open", """{"what":"themes"}"""));
    }

    [Theory]
    [InlineData(" 1, 0, 0 ", " 0, 1, 0 ", " 0, 0, 1 ", "true", "[[1,0,0],[0,1,0],[0,0,1]]")]
    [InlineData("1.0005,0,0", "0,0.9995,0", "0,0,1", "true", "[[1.0005,0,0],[0,0.9995,0],[0,0,1]]")]            // within a thousandth still counts as default
    [InlineData("0.2, 0.8, 0.3", "0.5, 1, 0.2", "-0.5, -1, 1", "false", "[[0.2,0.8,0.3],[0.5,1,0.2],[-0.5,-1,1]]")]
    [InlineData("1, 0", "0, 1, 0", "0, 0, 1", "null", "[[1,0],[0,1,0],[0,0,1]]")]                                // a short row cannot be judged
    [InlineData("1, 0, x", "0, 1, 0", "0, 0, 1", "null", "null")]                                                // not numbers
    [InlineData("", "0, 1, 0", "0, 0, 1", "null", "null")]
    public void Elites_own_colour_file_is_default_custom_or_unreadable(string red, string green, string blue, string isDefault, string matrix)
    {
        string file = Path.Combine(temp, "Graphics", "GraphicsConfigurationOverride.xml");
        StageXml(file, red, green, blue);
        string path = JsonSerializer.Serialize(file);
        Assert.Equal($$"""{"path":{{path}},"exists":true,"isDefault":{{isDefault}},"matrix":{{matrix}}}""", JsonSerializer.Serialize(GameFolder.XmlStatus(file)));
    }

    [Fact]
    public void Elites_own_colour_file_may_be_absent_empty_or_broken()
    {
        string file = Path.Combine(temp, "GraphicsConfigurationOverride.xml");
        string path = JsonSerializer.Serialize(file);
        Assert.Equal($$"""{"path":{{path}},"exists":false,"isDefault":true,"matrix":null}""", JsonSerializer.Serialize(GameFolder.XmlStatus(file)));

        File.WriteAllText(file, "<GraphicsConfig><GUIColour></GUIColour></GraphicsConfig>");   // no colours set: default
        Assert.Equal($$"""{"path":{{path}},"exists":true,"isDefault":true,"matrix":null}""", JsonSerializer.Serialize(GameFolder.XmlStatus(file)));

        File.WriteAllText(file, "<GraphicsConfig><GUIColour>");
        Assert.Equal($$"""{"path":{{path}},"exists":true,"isDefault":null,"matrix":null}""", JsonSerializer.Serialize(GameFolder.XmlStatus(file)));
    }

    // ------------------------------------------------------------ themes

    [Fact]
    public async Task A_theme_whose_name_has_accents_can_be_read_back_by_that_name()
    {
        // The old program read the address as Windows-1252 and could never find such a theme; its script edition could.
        Rig rig = Practice();
        JsonElement saved = await rig.Ok("POST", "/mythemes", """{"name":"Café ☕ über","values":{"SuitHud":{"x30":"100"}}}""");
        Assert.Equal("Café ☕ über", saved.GetProperty("id").GetString());
        JsonElement theme = await rig.Ok("GET", "/theme?src=mine&id=Caf%C3%A9%20%E2%98%95%20%C3%BCber");
        Assert.Equal("Café ☕ über", theme.GetProperty("name").GetString());
        Assert.Equal("""{"SuitHud":{"x30":"100"}}""", Compact(theme.GetProperty("values")));

        // Accented letters also survive in a backup's reason; punctuation does not.
        string name = (await rig.Ok("POST", "/backup", """{"reason":"Před změnou: (é/ü)!"}""")).GetProperty("name").GetString()!;
        Assert.EndsWith(" Před změnou éü", name);
        Assert.Equal("Před změnou éü", (await rig.Ok("GET", "/backups")).GetProperty("items")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_new_theme_is_filed_by_the_rule_all_studios_share_and_themes_filed_before_still_open()
    {
        Rig rig = Practice();
        static string Theme(string name) => JsonSerializer.Serialize(new { name, values = new { SuitHud = new { x30 = "100" } } });

        // The one kind of name that was taken before and is refused now: a name Windows keeps for a
        // device. Elite HUD Studio and Studio until now answered "saved" for it, while Windows 10
        // sent the theme to the device and made no file.
        foreach (string device in new[] { "Con", "nul", "COM1", "Aux.dark" })
            Assert.Equal("That name cannot be used as a file name.", await rig.Refused("POST", "/mythemes", Theme(device)));
        Assert.False(Directory.Exists(rig.Themes));

        // A name with a pasted tab could not be saved before (Windows refused the file); now the tab is replaced like the rest.
        Assert.Equal("Night-shift", (await rig.Ok("POST", "/mythemes", Theme("Night\tshift"))).GetProperty("id").GetString());
        // A space left at the end once the dots are gone is dropped too; before, this was filed as "Blue ".
        Assert.Equal("Blue", (await rig.Ok("POST", "/mythemes", Theme("Blue ."))).GetProperty("id").GetString());
        Assert.Equal("Blue .", (await rig.Ok("GET", "/theme?src=mine&id=Blue")).GetProperty("name").GetString());   // the name itself is kept as typed

        // Themes already on disk are found by the looser check, so they open and delete as before
        // whatever they are called: one filed by the old rule, one copied in by hand with a very long name.
        string oldRule = "Green ", byHand = new string('n', 120);
        foreach (string id in new[] { oldRule, byHand })
            File.WriteAllText(Path.Combine(rig.Themes, id + ".json"), """{"app":"hud-studio","version":1,"name":"From before","values":{"SuitHud":{"x30":"50"}}}""");
        string[] listed = [.. (await rig.Ok("GET", "/themes")).GetProperty("mine").EnumerateArray().Select(theme => theme.GetProperty("id").GetString()!)];
        Assert.Equal(new[] { "Blue", "Green ", "Night-shift", byHand }, listed.Order(StringComparer.Ordinal));
        foreach (string id in new[] { oldRule, byHand })
        {
            Assert.Equal("""{"SuitHud":{"x30":"50"}}""", Compact((await rig.Ok("GET", "/theme?src=mine&id=" + Uri.EscapeDataString(id))).GetProperty("values")));
            await rig.Ok("POST", "/mythemes/delete", JsonSerializer.Serialize(new { id }));
        }
        Assert.Equal(new[] { Path.Combine(rig.Themes, "Green .json"), Path.Combine(rig.Themes, byHand + ".json") }, rig.Recycled);
    }

    [Fact]
    public async Task The_first_run_copies_themes_saved_with_the_old_tool_once_without_overwriting()
    {
        string besideProgram = Path.Combine(temp, "Elite HUD Studio", "My Themes"), userFolder = Path.Combine(temp, "AppData", "Elite HUD Studio", "My Themes");
        Directory.CreateDirectory(besideProgram);
        Directory.CreateDirectory(userFolder);
        File.WriteAllText(Path.Combine(besideProgram, "Mine.json"), """{"name":"Mine","values":{}}""");
        File.WriteAllText(Path.Combine(besideProgram, "Both.json"), """{"name":"Both, beside the program"}""");
        File.WriteAllText(Path.Combine(besideProgram, "Shared.zip"), "not a theme");
        File.WriteAllText(Path.Combine(userFolder, "Both.json"), """{"name":"Both, in the user folder"}""");
        File.WriteAllText(Path.Combine(userFolder, "Newer.json"), """{"name":"Newer"}""");
        File.WriteAllText(Path.Combine(userFolder, "Kept.json"), """{"name":"Kept, old tool's copy"}""");
        File.SetLastWriteTime(Path.Combine(besideProgram, "Mine.json"), new DateTime(2026, 9, 30, 23, 10, 57));

        Rig rig = NewRig();
        Directory.CreateDirectory(rig.Themes);
        File.WriteAllText(Path.Combine(rig.Themes, "Kept.json"), """{"name":"Kept, already in Studio"}""");
        string missing = Path.Combine(temp, "never existed", "My Themes");
        rig.Start(null, besideProgram, missing, userFolder);
        Assert.False(File.Exists(Path.Combine(rig.Themes, "Mine.json")));   // nothing happens until a page asks for something

        JsonElement mine = (await rig.Ok("GET", "/themes")).GetProperty("mine");
        Assert.Equal(["Both, beside the program", "Kept, already in Studio", "Mine", "Newer"], mine.EnumerateArray().Select(theme => theme.GetProperty("name").GetString()).Order());
        Assert.Equal(["Both.json", "Kept.json", "Mine.json", "Newer.json"], Directory.GetFiles(rig.Themes).Select(Path.GetFileName).Order());
        Assert.Equal(new DateTime(2026, 9, 30, 23, 10, 57), File.GetLastWriteTime(Path.Combine(rig.Themes, "Mine.json")));   // keeps its place in the list
        // Copied, not moved: the old tool still has everything.
        Assert.Equal(3, Directory.GetFiles(besideProgram).Length);
        Assert.Equal(3, Directory.GetFiles(userFolder).Length);
        Assert.True(SavedUi(rig).GetProperty("oldThemesCopied").GetBoolean());

        // Later launches leave the folder alone, even if the player deleted a copied theme on purpose.
        File.Delete(Path.Combine(rig.Themes, "Mine.json"));
        File.WriteAllText(Path.Combine(userFolder, "Later.json"), """{"name":"Later"}""");
        await rig.Start(null, besideProgram, missing, userFolder).Ok("GET", "/themes");
        Assert.Equal(["Both.json", "Kept.json", "Newer.json"], Directory.GetFiles(rig.Themes).Select(Path.GetFileName).Order());
    }
}
