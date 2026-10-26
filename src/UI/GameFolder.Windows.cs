using System.Diagnostics;
using EliteStudio.App;
using EliteStudio.Core;

namespace EliteStudio.UI;

// The one key Studio presses in another program, which only Windows lets it do this way.
internal sealed partial record GameFolder
{
    /// <summary>
    /// Elite reloads the HUD colours when F11 is pressed in the game (the mod's own key). A page
    /// cannot press a key in another program, so this brings Elite to the front and presses it.
    /// </summary>
    public static Reload PressReloadKey()
    {
        Process[] elite = Process.GetProcessesByName(EliteWindow.ProcessName);
        if (elite.Length == 0) return new Reload(false, false, null);
        try
        {
            if (elite[0].MainWindowHandle == IntPtr.Zero) throw new InvalidOperationException("Elite Dangerous is running but its main window handle is not available.");
            Microsoft.VisualBasic.Interaction.AppActivate(elite[0].Id);
            Thread.Sleep(200);   // give Windows time to switch windows before the key goes out
            System.Windows.Forms.SendKeys.SendWait("{F11}");
            Log.Info("Sent F11 to Elite Dangerous to reload the HUD colours.");
            return new Reload(true, true, null);
        }
        catch (Exception problem)
        {
            // Whatever went wrong, the save itself worked: the page tells the player to press F11 themselves.
            Log.Error("Could not send F11 to Elite Dangerous", problem);
            return new Reload(true, false, problem.Message);
        }
        finally
        {
            foreach (Process process in elite) process.Dispose();
        }
    }
}
