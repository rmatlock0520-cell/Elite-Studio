using System.ComponentModel;
using System.Diagnostics;

// The few things the shared code asks of Windows, as Linux has them (or says it has not).

namespace EliteStudio.App
{
    /// <summary>
    /// Plays the sounds Studio makes in memory (the sniffer's chime, Nav scan's ping and click) with the player the
    /// desktop has: PipeWire's (pw-play), else PulseAudio's (paplay), else ALSA's (aplay). A PC with none stays silent.
    /// </summary>
    internal static class Sound
    {
        static readonly string? Player = ((string[])["pw-play", "paplay", "aplay"]).FirstOrDefault(OnPath);

        /// <summary>Plays a WAV file held in memory and returns at once.</summary>
        public static void Play(byte[] wav)
        {
            if (Player is null) return;
            // A file of its own each time, so one sound never cuts off another; it is deleted once its player is done.
            string file = Path.Combine(Path.GetTempPath(), $"elite-studio-{Guid.NewGuid():N}.wav");
            try
            {
                File.WriteAllBytes(file, wav);
                var player = new Process { StartInfo = { FileName = Player, ArgumentList = { file } }, EnableRaisingEvents = true };
                player.Exited += (_, _) =>
                {
                    try { File.Delete(file); }
                    catch (Exception kept) when (kept is IOException or UnauthorizedAccessException) { }
                    player.Dispose();
                };
                player.Start();
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or Win32Exception)
            {
                Log.Error("A sound could not be played", problem);
            }
        }

        static bool OnPath(string program) => (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries).Any(folder => File.Exists(Path.Combine(folder, program)));
    }

    /// <summary>
    /// The player's own desktop, for when Studio has no window of its own: their folder picker. (Their web browser is
    /// opened by AppApi.OpenInBrowser, as for every link.)
    /// </summary>
    internal static class Desktop
    {
        /// <summary>
        /// The desktop's own folder picker: KDE's (kdialog, as on a Steam Deck), else GNOME's (zenity). The folder chosen,
        /// or null when the player cancelled or the desktop has neither.
        /// </summary>
        public static string? PickFolder(string title)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (string[] picker in (string[][])[["kdialog", "--title", title, "--getexistingdirectory", home], ["zenity", "--file-selection", "--directory", "--title=" + title]])
            {
                try
                {
                    using Process asking = Process.Start(new ProcessStartInfo(picker[0], picker[1..]) { RedirectStandardOutput = true })!;
                    string chosen = asking.StandardOutput.ReadToEnd().Trim();
                    asking.WaitForExit();
                    return asking.ExitCode == 0 && chosen.Length > 0 ? chosen : null;
                }
                catch (Win32Exception)   // not on this desktop: try the next
                {
                }
            }
            return null;
        }
    }
}

namespace EliteStudio.UI
{
    partial record GameFolder
    {
        /// <summary>
        /// On Windows Studio presses F11 in the game after a theme is saved. On Linux one program cannot reliably press
        /// keys in another one's window (Wayland desktops do not allow it), so the page asks the player to press F11 in the game.
        /// </summary>
        public static Reload PressReloadKey() => new(false, false, null);
    }
}

namespace EliteStudio.Nav
{
    /// <summary>Nav scan reads screenshots with Windows' own text reader, which Linux has no match for, so the Linux version hides it.</summary>
    internal static class NavScanOcr
    {
        public static Task<PictureText> Read(string path, CancellationToken cancel) =>
            Task.FromResult(new PictureText([], "Nav scan is not available on Linux yet."));
    }
}
