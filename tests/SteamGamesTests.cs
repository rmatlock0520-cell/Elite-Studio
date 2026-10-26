using EliteStudio.Core;

namespace EliteStudio.Tests;

/// <summary>
/// Where Steam keeps Elite on Linux (<see cref="SteamGames"/>), on a practice home folder built here: Steam's folders,
/// its list of libraries, the game's install and the game's Proton prefix with the journal folder inside. The layout is
/// Steam's and Proton's own (a Steam Deck's, and Steam's documentation of libraryfolders.vdf). Nothing reads the PC's
/// own Steam, so these checks run on any system.
/// </summary>
public sealed class SteamGamesTests : IDisposable
{
    static readonly IReadOnlyDictionary<string, string?> NoEnv = new Dictionary<string, string?>();
    static readonly DateTime Morning = new(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);

    readonly string home = Directory.CreateTempSubdirectory("EliteStudioSteam-").FullName;

    public void Dispose() => Directory.Delete(home, recursive: true);

    /// <summary>A folder below the practice home, made if it is not there.</summary>
    string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine([home, .. parts])).FullName;

    /// <summary>Steam's own folder where Steam installed from the system's packages keeps it.</summary>
    string Steam => Folder(".local", "share", "Steam");

    static void Vdf(string steam, string text)
    {
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), text);
    }

    /// <summary>A folder as Steam writes it in its files: each backslash doubled (there are only any on Windows).</summary>
    static string Quoted(string folder) => folder.Replace(@"\", @"\\");

    /// <summary>The game installed in a library: the product's folder with the game's program in it.</summary>
    static string Install(string library, string product = "elite-dangerous-odyssey-64")
    {
        string dir = Directory.CreateDirectory(Path.Combine(library, "steamapps", "common", "Elite Dangerous", "Products", product)).FullName;
        File.WriteAllBytes(Path.Combine(dir, "EliteDangerous64.exe"), []);
        return dir;
    }

    /// <summary>A Proton prefix in a library: Elite's own (359320) unless another game's number is given.</summary>
    static string Prefix(string library, string game = "359320") =>
        Directory.CreateDirectory(Path.Combine(library, "steamapps", "compatdata", game, "pfx")).FullName;

    static string User(string prefix, string user = "steamuser") => Path.Combine(prefix, "drive_c", "users", user);

    static string JournalOf(string prefix, string user = "steamuser") => Path.Combine(User(prefix, user), "Saved Games", "Frontier Developments", "Elite Dangerous");

    static string BindingsOf(string prefix, string user = "steamuser") =>
        Path.Combine(User(prefix, user), "AppData", "Local", "Frontier Developments", "Elite Dangerous", "Options", "Bindings");

    /// <summary>The game's journal folder for that Windows user, with one journal in it written at that time (none without one).</summary>
    static string Journal(string prefix, string user = "steamuser", DateTime? written = null)
    {
        string folder = Directory.CreateDirectory(JournalOf(prefix, user)).FullName;
        if (written is { } when)
        {
            string file = Path.Combine(folder, $"Journal.{when:yyyy-MM-ddTHHmmss}.01.log");
            File.WriteAllText(file, "{ \"event\":\"Fileheader\" }\r\n");
            File.SetLastWriteTimeUtc(file, when);
        }
        return folder;
    }

    // ------------------------------------------------------------ Steam's libraries

    [Fact]
    public void Without_Steam_nothing_is_found()
    {
        Assert.Empty(SteamGames.Libraries(home, NoEnv));
        Assert.Equal((null, null, 0), Unpacked(SteamGames.Find(home, NoEnv)));
        // A STEAM_DIR that is not there changes nothing.
        Assert.Empty(SteamGames.Libraries(home, new Dictionary<string, string?> { ["STEAM_DIR"] = Path.Combine(home, "gone") }));
    }

    static (string?, string?, int) Unpacked(SteamGames.Found found) => (found.Journal, found.Bindings, found.Installs.Count);

    [Fact]
    public void Each_Steam_folder_is_read_with_the_libraries_its_list_names_in_either_layout()
    {
        string custom = Folder("custom-steam"), native = Steam, flatpak = Folder(".var", "app", "com.valvesoftware.Steam", "data", "Steam");
        string card = Folder("media", "card"), second = Folder("games", "SteamLibrary"), gone = Path.Combine(home, "unplugged");

        // Today's layout: a block for each library with its "path"; the numbered lines inside "apps" are games and their sizes.
        Vdf(native, $$"""
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"{{Quoted(native)}}"
            		"label"		""
            		"apps"
            		{
            			"359320"		"52428800"
            		}
            	}
            	"1"
            	{
            		"path"		"{{Quoted(card)}}"
            	}
            	"2"
            	{
            		"path"		"{{Quoted(gone)}}"
            	}
            }
            """);
        // The older layout names each library straight after its number.
        Vdf(flatpak, $$"""
            "LibraryFolders"
            {
            	"TimeNextStatsReport"		"1760000000"
            	"ContentStatsID"		"-4242"
            	"1"		"{{Quoted(second)}}"
            }
            """);

        // STEAM_DIR first, then the usual places in turn, each followed by what its list names. A library that is not
        // there (an SD card taken out) is left out; Steam's own folder, listed again in its list, counts once.
        Assert.Equal([custom, native, card, flatpak, second], SteamGames.Libraries(home, new Dictionary<string, string?> { ["STEAM_DIR"] = custom }));
        Assert.Equal([native, card, flatpak, second], SteamGames.Libraries(home, NoEnv));
    }

    [Fact]
    public void A_Steam_folder_reached_through_a_link_counts_once()
    {
        // Steam itself makes ~/.steam/steam and ~/.steam/root links to the folder it is in.
        string native = Steam;
        Folder(".steam");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(home, ".steam", "steam"), native);
            Directory.CreateSymbolicLink(Path.Combine(home, ".steam", "root"), native);
        }
        catch (Exception refused) when (refused is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("This PC does not let a test make folder links (Windows needs Developer Mode for it).");
        }
        Assert.Equal([native], SteamGames.Libraries(home, NoEnv));
    }

    // ------------------------------------------------------------ the game and its prefix

    [Fact]
    public void The_game_is_found_in_any_library_the_live_game_before_the_Legacy_one()
    {
        string native = Steam, card = Folder("media", "card");
        Vdf(native, $$"""
            "libraryfolders" { "0" { "path" "{{Quoted(native)}}" } "1" { "path" "{{Quoted(card)}}" } }
            """);
        string legacy = Install(native, "elite-dangerous-64");
        string live = Install(card);
        Directory.CreateDirectory(Path.Combine(native, "steamapps", "common", "Elite Dangerous", "Products", "elite-dangerous-odyssey-64"));   // the folder, without the game

        Assert.Equal([live, legacy], SteamGames.Find(home, NoEnv).Installs);
    }

    [Fact]
    public void The_journal_is_where_Proton_keeps_it_in_the_games_prefix_and_the_newest_one_wins()
    {
        // The game on the card, its prefix in Steam's own folder (Steam may keep them in different libraries).
        string native = Steam, card = Folder("media", "card");
        Vdf(native, $$"""
            "libraryfolders" { "0" { "path" "{{Quoted(native)}}" } "1" { "path" "{{Quoted(card)}}" } }
            """);
        Install(card);
        string prefix = Prefix(native);
        Directory.CreateDirectory(User(prefix, "Public"));                   // a Windows user without the game's folders
        string proton = Journal(prefix), deck = Journal(prefix, "deck");

        // Neither has written a journal yet: Proton's own user first.
        SteamGames.Found found = SteamGames.Find(home, NoEnv);
        Assert.Equal((proton, BindingsOf(prefix)), (found.Journal, found.Bindings));

        // The one the game wrote to last wins, and the key bindings are that same user's.
        Journal(prefix, "deck", Morning);
        found = SteamGames.Find(home, NoEnv);
        Assert.Equal((deck, BindingsOf(prefix, "deck")), (found.Journal, found.Bindings));
        Journal(prefix, "steamuser", Morning.AddHours(1));
        Assert.Equal(proton, SteamGames.Find(home, NoEnv).Journal);
    }

    [Fact]
    public void With_the_game_in_two_libraries_the_prefix_Proton_used_last_counts()
    {
        string native = Steam, card = Folder("media", "card");
        Vdf(native, $$"""
            "libraryfolders" { "0" { "path" "{{Quoted(native)}}" } "1" { "path" "{{Quoted(card)}}" } }
            """);
        string old = Prefix(native), used = Prefix(card);
        Journal(old, written: Morning.AddDays(1));       // a newer journal, but in the prefix that is not used any more
        Journal(used, written: Morning);

        // Proton touches pfx.lock beside the prefix each time the game starts.
        string oldLock = Path.Combine(native, "steamapps", "compatdata", "359320", "pfx.lock"), usedLock = Path.Combine(card, "steamapps", "compatdata", "359320", "pfx.lock");
        File.WriteAllText(oldLock, "");
        File.WriteAllText(usedLock, "");
        File.SetLastWriteTimeUtc(oldLock, Morning.AddDays(-30));
        File.SetLastWriteTimeUtc(usedLock, Morning);
        Assert.Equal(JournalOf(used), SteamGames.Find(home, NoEnv).Journal);

        // Without pfx.lock, the prefix folder's own time.
        File.Delete(oldLock);
        File.Delete(usedLock);
        Directory.SetLastWriteTimeUtc(old, Morning.AddDays(2));
        Directory.SetLastWriteTimeUtc(used, Morning);
        Assert.Equal(JournalOf(old), SteamGames.Find(home, NoEnv).Journal);
    }

    [Fact]
    public void A_game_never_started_has_its_journal_where_Proton_will_keep_it()
    {
        // Steam made the prefix's folder, but the game has not run in it yet.
        string prefix = Prefix(Steam);
        SteamGames.Found found = SteamGames.Find(home, NoEnv);
        Assert.Equal((JournalOf(prefix), BindingsOf(prefix)), (found.Journal, found.Bindings));
        Assert.False(Directory.Exists(found.Journal));
    }

    [Fact]
    public void The_Frontier_launcher_added_to_Steam_as_a_game_of_its_own_is_looked_in_too()
    {
        // Added by the player, the launcher gets a prefix under a number of its own.
        string native = Steam;
        string launcher = Prefix(native, "2734418453");
        Prefix(native, "1245620");                                           // another game's prefix, without Elite's folders
        Assert.Equal(JournalOf(launcher), Journal(launcher, written: Morning));
        Assert.Equal(JournalOf(launcher), SteamGames.Find(home, NoEnv).Journal);

        // With the game's own prefix too, the journal written last still wins.
        string game = Prefix(native);
        Journal(game, written: Morning.AddHours(-1));
        Assert.Equal(JournalOf(launcher), SteamGames.Find(home, NoEnv).Journal);
        Journal(game, written: Morning.AddHours(1));
        Assert.Equal((JournalOf(game), BindingsOf(game)), (SteamGames.Find(home, NoEnv).Journal, SteamGames.Find(home, NoEnv).Bindings));
    }
}
