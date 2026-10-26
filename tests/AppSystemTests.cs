using System.Diagnostics;
using EliteStudio.App;

namespace EliteStudio.Tests;

/// <summary>
/// What the app shell does its own way off Windows, checked on every system: the lock that keeps one copy per data
/// folder, and how a link is handed to the player's browser.
/// </summary>
public sealed class AppSystemTests : IDisposable
{
    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioTests-").FullName;

    public void Dispose() => Directory.Delete(temp, recursive: true);

    [Fact]
    public void A_second_copy_finds_the_data_folder_locked_until_the_first_one_ends()
    {
        string practice = Directory.CreateDirectory(Path.Combine(temp, "practice")).FullName;
        FileStream? first = Program.LockDataFolder(temp);
        try
        {
            Assert.NotNull(first);
            Assert.Null(Program.LockDataFolder(temp));
            using (FileStream? other = Program.LockDataFolder(practice)) Assert.NotNull(other);   // a practice copy on its own folder is not stopped
            if (!OperatingSystem.IsWindows())
            {
                // The lock holds for a copy started in another login session too (each terminal is one of its own), which
                // a Local\ mutex does not. flock asks for the same lock from a new session; 1 is its "held by another".
                using Process flock = Process.Start("setsid", ["--wait", "flock", "--nonblock", Path.Combine(temp, "running.lock"), "true"]);
                flock.WaitForExit();
                Assert.Equal(1, flock.ExitCode);
            }
        }
        finally
        {
            first?.Dispose();
        }
        using FileStream? next = Program.LockDataFolder(temp);
        Assert.NotNull(next);   // the first copy has ended, however it ended: the next start is the only one
    }

    [Fact]
    public void Off_Windows_a_link_opens_in_a_session_of_its_own_and_on_Windows_through_the_shell_as_before()
    {
        const string link = "https://inara.cz/elite/";

        ProcessStartInfo own = AppApi.Opener(link, ownSession: true);
        Assert.Equal("setsid", own.FileName);
        Assert.Equal(new[] { "-f", "xdg-open", link }, own.ArgumentList);
        Assert.False(own.UseShellExecute);

        ProcessStartInfo shell = AppApi.Opener(link, ownSession: false);
        Assert.Equal(link, shell.FileName);
        Assert.Empty(shell.ArgumentList);
        Assert.True(shell.UseShellExecute);
    }
}
