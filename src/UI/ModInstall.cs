using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using EliteStudio.App;

namespace EliteStudio.UI;

/// <summary>
/// Getting the HUD mod (EDHM) for a player whose game does not have it. The mod's licence lets a
/// player download it for themselves and lets no other program carry it, so Studio holds no copy:
/// on the player's click the newest zip is fetched from the author's own page and unpacked into the
/// game folder, which is how the author says to install it by hand.
/// </summary>
internal static class ModInstall
{
    const string Listing = "https://api.github.com/repos/psychicEgg/EDHM/contents/Odyssey";
    const string Files = "https://raw.githubusercontent.com/psychicEgg/EDHM/main/Odyssey/";
    /// <summary>The author names the zip after the mod's version: EDHM_Odyssey_v22.03.zip.</summary>
    static readonly Regex ZipName = new(@"^EDHM_Odyssey_v(\d+(?:\.\d+){1,3})\.zip$");
    /// <summary>The zip is under 4 MB today; anything far bigger is not the mod.</summary>
    const int LargestZip = 50_000_000;

    /// <summary>The newest zip on the author's page and the version in its name.</summary>
    public static (string Version, byte[] Zip) Download(HttpMessageHandler? network)
    {
        try
        {
            using var http = new HttpClient(network ?? new SocketsHttpHandler(), disposeHandler: network is null) { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(Program.UserAgent);
            // Waiting here is fine: a request's work runs on the thread pool, never on the window's thread (StudioUi.Run).
            JsonElement listing = JsonSerializer.Deserialize<JsonElement>(http.GetStringAsync(Listing).GetAwaiter().GetResult());
            if (listing.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("its page did not list its files");
            var newest = listing.EnumerateArray()
                .Select(file => ZipName.Match(Loose.Text(Loose.Field(file, "name"))))
                .Where(name => name.Success)
                .MaxBy(name => Version.Parse(name.Groups[1].Value))
                ?? throw new InvalidOperationException("its page lists no download Studio knows");
            using HttpResponseMessage answer = http.GetAsync(Files + newest.Value, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            answer.EnsureSuccessStatusCode();
            if (answer.Content.Headers.ContentLength > LargestZip) throw new InvalidOperationException("the download is far bigger than the mod");
            byte[] zip = answer.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (zip.Length > LargestZip) throw new InvalidOperationException("the download is far bigger than the mod");
            return (newest.Groups[1].Value, zip);
        }
        catch (Exception problem) when (problem is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"The HUD mod could not be downloaded ({problem.Message}). Check your internet connection and try again, or get it from the mod's page.");
        }
    }

    /// <summary>
    /// Unpacks the mod into the game folder and returns how many files were written. What is already
    /// in the colour folder is kept, so a player who lost only the loader keeps their colours.
    /// </summary>
    public static int Unpack(byte[] zip, string gameDir)
    {
        string root = Path.GetFullPath(gameDir).TrimEnd('\\') + '\\';
        string colours = root + @"EDHM-ini\";
        var entries = new List<(ZipArchiveEntry Entry, string Path)>();
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string path = Path.GetFullPath(Path.Combine(root, entry.FullName));
                // Checked for the whole zip before anything is written: a name that leads out of the game folder refuses it all.
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                entries.Add((entry, path));
            }
            bool Holds(string name) => entries.Exists(entry => GameFolder.SamePath(entry.Path, root + name));
            if (!Holds("d3dx.ini") || !Holds("d3d11.dll") || !entries.Exists(entry => entry.Path.StartsWith(colours, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException();

            int written = 0;
            foreach (var (entry, path) in entries)
            {
                if (entry.Name.Length == 0) { Directory.CreateDirectory(path); continue; }   // a folder
                if (path.StartsWith(colours, StringComparison.OrdinalIgnoreCase) && File.Exists(path)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: true);
                written++;
            }
            return written;
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException("The download was not the HUD mod as Studio knows it, so nothing was installed. Get it from the mod's page instead.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Windows did not let Elite Studio write into the game folder. Install the HUD mod from its page instead (it says how).");
        }
    }
}
