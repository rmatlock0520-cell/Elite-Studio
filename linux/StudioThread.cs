using System.Collections.Concurrent;

namespace EliteStudio.App;

/// <summary>
/// The thread Studio's game state lives on, named "Studio": on Linux what WPF's own thread is on Windows. The window's
/// toolkit (GTK) needs the main thread for itself, so the game watcher, the web server's handlers and the twice-a-second
/// update run here instead, one piece of work at a time, in the order it arrives. Whatever they await comes back to this
/// thread, so the rule "one thread owns the game state" holds as on Windows.
/// </summary>
internal sealed class StudioThread : SynchronizationContext
{
    readonly BlockingCollection<(SendOrPostCallback Work, object? State)> queue = [];

    StudioThread() { }

    /// <summary>Starts the thread and runs work on it; the thread ends with work, and the task gives work's answer.</summary>
    public static Task<T> Run<T>(Func<Task<T>> work)
    {
        var studio = new StudioThread();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        studio.Post(async _ =>
        {
            try { done.SetResult(await work()); }
            catch (Exception problem) { done.SetException(problem); }
            finally { studio.queue.CompleteAdding(); }
        }, null);
        new Thread(() =>
        {
            SetSynchronizationContext(studio);
            foreach ((SendOrPostCallback next, object? state) in studio.queue.GetConsumingEnumerable())
            {
                try { next(state); }
                catch (Exception problem) { Log.Error($"Unexpected problem: {problem}"); }   // logged, as WPF's thread does on Windows; never the end of Studio
            }
        }) { Name = "Studio", IsBackground = true }.Start();
        return done.Task;
    }

    public override void Post(SendOrPostCallback work, object? state)
    {
        // Work that arrives once Studio has ended (an answer to a page that was still asking) is dropped.
        try { queue.Add((work, state)); }
        catch (InvalidOperationException) { }
    }

    public override SynchronizationContext CreateCopy() => this;
}
