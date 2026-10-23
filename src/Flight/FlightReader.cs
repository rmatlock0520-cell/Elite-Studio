using System.Diagnostics;
using EliteStudio.App;
using EliteStudio.Gimbalyn;

namespace EliteStudio.Flight;

/// <summary>
/// One reading of what the player's hands are doing: every stick, and which of the keys Studio
/// watches are held (by the game's names, "Key_W").
/// </summary>
/// <param name="Time">Milliseconds on a fine clock, each reading's later than the one before: how far apart two readings are.</param>
public sealed record FlightFrame(long Time, IReadOnlyList<StickState> Sticks, IReadOnlyList<string> Keys);

/// <summary>
/// Reads the sticks and the watched keys 62.5 times a second, but only while somebody wants them: the
/// flight overlay while it is on screen, or a page that asked for the stream in the last few seconds
/// (the trainer asks every second or two while it runs). The readings are taken on a thread of their
/// own, kept in step by Gimbalyn's clock, so they come evenly however busy the window thread is (it also
/// answers every page and draws the overlay). Each reading goes to every page as the live event
/// "flight-input" while one has asked, straight from that thread. <see cref="Read"/> and
/// <see cref="OverlayWants"/> touch the window, so they are called on the thread that called
/// <see cref="Start"/> when it has a message loop (the app's window thread); without one (tests), on the reading thread.
/// </summary>
public sealed class FlightReader : IDisposable
{
    const int ReadEveryMs = 16;      // 62.5 readings a second
    const int IdleMs = 200;          // how often, while nobody wants readings, the overlay is asked again
    const int LookEveryMs = 3000;    // how often places with no stick are looked at again
    const int StreamMs = 4000;       // how long a page's ask keeps the stream going

    readonly Func<bool, IReadOnlyList<StickState>> readSticks;
    readonly Func<int, bool> held;
    readonly Action<FlightFrame>? send;
    volatile IReadOnlyList<(string Name, int Key)> keys = [];
    long streamUntil;
    volatile bool stopped, overlayWanted;
    int telling;                     // 1 while a reading is on its way to the window thread
    SynchronizationContext? home;    // the window thread's, from Start
    string? readProblem, tellProblem;

    /// <param name="sticks">How the sticks are read (null: Windows' joystick calls); given true it also looks for sticks plugged in since.</param>
    /// <param name="held">Whether a key (Windows' number for it) is held; null: Windows'.</param>
    /// <param name="send">Where a reading goes while a page asked for the stream (the live event); called on the reading thread.</param>
    public FlightReader(Func<bool, IReadOnlyList<StickState>>? sticks, Func<int, bool>? held, Action<FlightFrame>? send)
    {
        readSticks = sticks ?? new WindowsSticks().Read;
        this.held = held ?? KeyNames.Held;
        this.send = send;
    }

    /// <summary>
    /// A new reading, raised on the thread that called <see cref="Start"/> (see the class). While that thread is busy the
    /// readings in between are not queued up for it: once it is free it gets the newest.
    /// </summary>
    public event Action<FlightFrame>? Read;

    /// <summary>The newest reading; null before the first.</summary>
    public FlightFrame? Latest { get; private set; }

    /// <summary>Whether the overlay wants readings now (it is on screen). Asked on the thread that called <see cref="Start"/>, with every reading and five times a second between.</summary>
    public Func<bool> OverlayWants { get; set; } = () => false;

    /// <summary>A page asks for the readings as live events for the next few seconds.</summary>
    public void Stream() => Volatile.Write(ref streamUntil, Environment.TickCount64 + StreamMs);

    bool Streaming => Environment.TickCount64 < Volatile.Read(ref streamUntil);

    /// <summary>The keys to watch, by the game's names; the ones Windows has no key for are left out.</summary>
    public void Watch(IEnumerable<string> keyNames) =>
        keys = [.. keyNames.Distinct(StringComparer.OrdinalIgnoreCase).Select(name => (name, KeyNames.VirtualKey(name))).Where(k => k.Item2 != 0)];

    public void Start()
    {
        home = SynchronizationContext.Current;
        new Thread(ReadOn) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "Studio Flight sticks" }.Start();
    }

    public void Dispose() => stopped = true;

    void ReadOn()
    {
        long looked = 0, last = 0;
        while (!stopped)
        {
            if (!Streaming && !overlayWanted)
            {
                Home(() => Tell(reading: false));
                Thread.Sleep(IdleMs);
                continue;
            }
            bool first = true;
            using var pacer = new Pacer(ReadEveryMs);   // only while readings are wanted
            while (!stopped && (Streaming || overlayWanted))
            {
                try
                {
                    long now = Environment.TickCount64;
                    bool look = first || now - looked >= LookEveryMs;
                    if (look) looked = now;
                    first = false;
                    IReadOnlyList<StickState> sticks = readSticks(look);
                    last = Math.Max(Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency, last + 1);
                    FlightFrame frame = new(last, sticks, [.. keys.Where(k => held(k.Key)).Select(k => k.Name)]);
                    Latest = frame;
                    if (Streaming) send?.Invoke(frame);
                    if (Interlocked.Exchange(ref telling, 1) == 0) Home(() => Tell(reading: true));   // else the one on its way takes the newest
                    readProblem = null;
                }
                catch (Exception problem)
                {
                    // Sixty times a second: a problem that keeps coming back is logged once.
                    if (problem.Message != readProblem) Log.Error("Reading the sticks for Studio Flight failed", problem);
                    readProblem = problem.Message;
                }
                pacer.Wait();
            }
        }
    }

    /// <summary>Runs on the window thread when there is one (posted, never waited for), else here.</summary>
    void Home(Action work)
    {
        if (home is null) work();
        else home.Post(_ => work(), null);
    }

    /// <summary>On the window thread: whether the overlay wants readings, and with reading the newest one to <see cref="Read"/>.</summary>
    void Tell(bool reading)
    {
        if (reading) Volatile.Write(ref telling, 0);
        try
        {
            overlayWanted = OverlayWants();
            if (reading && Latest is { } frame) Read?.Invoke(frame);
            tellProblem = null;
        }
        catch (Exception problem)
        {
            // Up to sixty times a second too: logged once.
            if (problem.Message != tellProblem) Log.Error("Showing the sticks in the flight box failed", problem);
            tellProblem = problem.Message;
        }
    }
}
