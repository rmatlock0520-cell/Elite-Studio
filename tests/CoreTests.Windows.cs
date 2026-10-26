using EliteStudio.Core;

namespace EliteStudio.Tests;

// Elite's own window, which only the Windows build looks at.
public partial class CoreTests
{
    [Fact]
    public void Elite_window_answers_whether_or_not_the_game_runs()
    {
        // Elite may or may not be running on the PC the tests run on: check that the answers agree with each other.
        if (EliteWindow.Running) return;
        Assert.Equal(IntPtr.Zero, EliteWindow.Handle);
        Assert.False(EliteWindow.IsForeground);
        Assert.Null(EliteWindow.MonitorBounds);
    }
}
