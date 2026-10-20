using EliteStudio.App;

namespace EliteStudio.Flight;

/// <summary>
/// One reading of what the player's hands are doing: every stick, and which of the keys Studio
/// watches are held (by the game's names, "Key_W").
/// </summary>
/// <param name="Time">Milliseconds, Windows' tick count: how far apart two readings are.</param>
public sealed record FlightFrame(long Time, IReadOnlyList<StickState> Sticks, IReadOnlyList<string> Keys);

/// <summary>
/// Reads the sticks and the watched keys about sixty times a second, but only while somebody wants
/// them: the flight overlay while it is on screen, or a page that asked for the stream in the last
/// few seconds (the trainer asks every second or two while it runs). Each reading goes to
/// <see cref="Read"/>'s listeners on the thread that called <see cref="Start"/>, and to every page
/// as the live event "flight-input" while one has asked.
/// </summary>
public sealed class FlightReader : IDisposable
{
    const int ReadEveryMs = 15;      // Windows rounds this up to its clock's tick: about sixty readings a second
    const int LookEveryMs = 3000;    // how often places with no stick are looked at again
    const int StreamMs = 4000;       // how long a page's ask keeps the stream going

    readonly Func<bool, IReadOnlyList<StickState>> readSticks;
    readonly Func<int, bool> held;
    readonly Action<FlightFrame>? send;
    IReadOnlyList<(string Name, int Key)> keys = [];
    long streamUntil;
    bool stopped;
    string? readProblem;

    /// <param name="sticks">How the sticks are read (null: Windows' joystick calls); given true it also looks for sticks plugged in since.</param>
    /// <param name="held">Whether a key (Windows' number for it) is held; null: Windows'.</param>
    /// <param name="send">Where a reading goes while a page asked for the stream (the live event).</param>
    public FlightReader(Func<bool, IReadOnlyList<StickState>>? sticks, Func<int, bool>? held, Action<FlightFrame>? send)
    {
        readSticks = sticks ?? new WindowsSticks().Read;
        this.held = held ?? KeyNames.Held;
        this.send = send;
    }

    /// <summary>A new reading. Raised on the thread that called <see cref="Start"/>.</summary>
    public event Action<FlightFrame>? Read;

    /// <summary>The newest reading; null before the first.</summary>
    public FlightFrame? Latest { get; private set; }

    /// <summary>Whether the overlay wants readings now (it is on screen). Asked before every reading.</summary>
    public Func<bool> OverlayWants { get; set; } = () => false;

    /// <summary>A page asks for the readings as live events for the next few seconds.</summary>
    public void Stream() => streamUntil = Environment.TickCount64 + StreamMs;

    bool Streaming => Environment.TickCount64 < streamUntil;

    /// <summary>The keys to watch, by the game's names; the ones Windows has no key for are left out.</summary>
    public void Watch(IEnumerable<string> keyNames) =>
        keys = [.. keyNames.Distinct(StringComparer.OrdinalIgnoreCase).Select(name => (name, KeyNames.VirtualKey(name))).Where(k => k.Item2 != 0)];

    public void Start() => _ = ReadOn();

    public void Dispose() => stopped = true;

    async Task ReadOn()
    {
        long looked = 0;
        bool wasWanted = false;
        while (!stopped)
        {
            bool wanted = Streaming || OverlayWants();
            if (!wanted)
            {
                wasWanted = false;
                await Task.Delay(200);
                continue;
            }
            try
            {
                long now = Environment.TickCount64;
                bool look = !wasWanted || now - looked >= LookEveryMs;
                if (look) looked = now;
                wasWanted = true;
                IReadOnlyList<StickState> sticks = await Task.Run(() => readSticks(look));
                FlightFrame frame = new(Environment.TickCount64, sticks, [.. keys.Where(k => held(k.Key)).Select(k => k.Name)]);
                Latest = frame;
                Read?.Invoke(frame);
                if (Streaming) send?.Invoke(frame);
                readProblem = null;
            }
            catch (Exception problem)
            {
                // Sixty times a second: a problem that keeps coming back is logged once.
                if (problem.Message != readProblem) Log.Error("Reading the sticks for Studio Flight failed", problem);
                readProblem = problem.Message;
            }
            await Task.Delay(ReadEveryMs);
        }
    }
}
