using System.Windows.Controls;
using EliteStudio.Overlay;

namespace EliteStudio.Nav;

// The trip's line in the in-game overlay, drawn with WPF (Windows only).
public sealed partial class ExploreTrip
{
    /// <summary>
    /// The in-game overlay's one quiet line while the commander is out exploring, or null when not:
    /// docked, the trip's finds sold, or nothing on this trip yet that nobody had found before.
    /// </summary>
    public TextBlock? OverlayLine(string accent)
    {
        ExploreView view = View;
        if (game.Docked || view.HandedIn || view.NewSystems + view.FirstDiscoveries + view.FirstMapped == 0) return null;

        TextBlock line = Draw.Text(14.5).Add("EXPLORING   ", accent, bold: true);
        if (view.IsNew is { } isNew) line.Add(isNew ? "new system" : "already discovered", isNew ? Draw.Green : null, bold: isNew).Add(Draw.Dot, Draw.Dim);
        line.Add(view.BodyCount > 0 ? $"{view.Scanned} of {view.BodyCount} bodies" : Counted(view.Scanned, "body scanned", "bodies scanned"));
        string finds = $"{Counted(view.NewSystems, "new system", "new systems")}, {Counted(view.FirstDiscoveries, "first discovery", "first discoveries")}"
            + (view.FirstMapped > 0 ? $", {Draw.Whole(view.FirstMapped)} first mapped" : "");
        return line.Add($"{Draw.Dot}trip: {finds}", Draw.Dim);
    }

    static string Counted(int number, string one, string many) => $"{Draw.Whole(number)} {(number == 1 ? one : many)}";
}
