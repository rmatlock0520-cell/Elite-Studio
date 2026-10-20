using System.Globalization;
using System.Text;

namespace EliteStudio.Gimbalyn;

/// <summary>
/// The flight recorder: the last half minute of what Gimbalyn read and wrote, axis by axis, with its state, the mode and the
/// sticks that were gone, in every state (also while stopped, failing or asleep: those are the moments that matter). It is
/// kept in a ring laid out once, so nothing is allocated while feeding; each frame keeps a reference to the names it was
/// made with, so a profile changed since does not relabel older frames. Written out as CSV on request, for "the ship turns
/// by itself": the file shows whether the stick moved, the curve let something through, or vJoy was not being fed.
/// </summary>
public sealed class Recorder
{
    /// <summary>The state codes the engine records (see Engine.StateCode).</summary>
    static readonly string[] States = ["stopped", "running", "vJoy failing", "busy", "no vJoy", "asleep"];

    readonly long[] times;
    readonly int[] states, modes, lostMasks;
    readonly string[][] modeNames, targetNames;
    readonly double[] inputs, outputs;   // by frame, then by route
    readonly int width;
    int next, count;
    DateTime newest;                     // the wall-clock time (UTC) of the newest frame
    readonly object gate = new();

    /// <param name="frames">How many rounds are kept (half a minute at 250 a second: 7500).</param>
    /// <param name="routes">The most routes a frame holds (vJoy has eight axes).</param>
    public Recorder(int frames = 7500, int routes = 8)
    {
        (times, states, modes, lostMasks, width) = (new long[frames], new int[frames], new int[frames], new int[frames], routes);
        (modeNames, targetNames) = (new string[frames][], new string[frames][]);
        inputs = new double[frames * routes];
        outputs = new double[frames * routes];
    }

    /// <summary>One round: the time (milliseconds), the state code, the mode in use (its place in <paramref name="modeList"/>), the routes' names, inputs and outputs, and the gone sticks (a bit each).</summary>
    public void Add(long time, int state, int mode, string[] modeList, string[] targets, double[] input, double[] output, int lostMask)
    {
        lock (gate)
        {
            (times[next], states[next], modes[next], lostMasks[next], modeNames[next], targetNames[next]) = (time, state, mode, lostMask, modeList, targets);
            Array.Fill(inputs, double.NaN, next * width, width);
            Array.Fill(outputs, double.NaN, next * width, width);
            Array.Copy(input, 0, inputs, next * width, Math.Min(width, input.Length));
            Array.Copy(output, 0, outputs, next * width, Math.Min(width, output.Length));
            newest = DateTime.UtcNow;   // (local time is worked out only when written out)
            next = (next + 1) % times.Length;
            count = Math.Min(count + 1, times.Length);
        }
    }

    /// <summary>
    /// The frames kept, oldest first, as CSV: a note of when, then for each run of frames made with the same routes a header
    /// and the frames (seconds before the newest frame, state, mode, which sticks were gone, each route's input and output).
    /// </summary>
    public string Csv()
    {
        // Copied out under the lock (well under a millisecond), written out after it, so feeding never waits on the writing.
        long[] t; int[] s, m, l; string[][] mn, tn; double[] i, o; int first, n; DateTime when;
        lock (gate)
        {
            (t, s, m, l, mn, tn, i, o, n, when) = ((long[])times.Clone(), (int[])states.Clone(), (int[])modes.Clone(), (int[])lostMasks.Clone(),
                (string[][])modeNames.Clone(), (string[][])targetNames.Clone(), (double[])inputs.Clone(), (double[])outputs.Clone(), count, newest);
            first = (next - count + times.Length) % times.Length;
        }
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"# Gimbalyn flight recorder: {n} frames, the newest at {when.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}, saved at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        long end = n > 0 ? t[(first + n - 1) % t.Length] : 0;
        string[]? header = null;
        for (int k = 0; k < n; k++)
        {
            int f = (first + k) % t.Length;
            string[] routes = tn[f] ?? [];
            if (!ReferenceEquals(routes, header))
            {
                if (header is not null) text.Append("# the profile changed here\n");
                header = routes;
                text.Append("seconds,state,mode,sticks gone");
                foreach (string r in routes.Take(width)) text.Append(CultureInfo.InvariantCulture, $",{r} in,{r} out");
                text.Append('\n');
            }
            string[] names = mn[f] ?? [];
            string mode = m[f] >= 0 && m[f] < names.Length ? names[m[f]] : "";
            string state = s[f] >= 0 && s[f] < States.Length ? States[s[f]] : "";
            text.Append(CultureInfo.InvariantCulture, $"{(t[f] - end) / 1000.0:0.000},{state},{mode},{(l[f] == 0 ? "" : "0x" + l[f].ToString("X", CultureInfo.InvariantCulture))}");
            for (int r = 0; r < Math.Min(width, routes.Length); r++)
                text.Append(CultureInfo.InvariantCulture, $",{Number(i[f * width + r])},{Number(o[f * width + r])}");
            text.Append('\n');
        }
        return text.ToString();
    }

    static string Number(double v) => double.IsFinite(v) ? v.ToString("0.0000", CultureInfo.InvariantCulture) : "";

    public int Count { get { lock (gate) return count; } }
}
