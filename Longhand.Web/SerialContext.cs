using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Longhand.Web;

/// <summary>
/// One thread that runs everything a page does, one piece at a time. The shared page logic expects to be called from a single
/// thread (the UI thread on Windows), and awaits resume on it, so on a server this plays that part.
/// </summary>
public sealed class SerialContext : SynchronizationContext
{
    readonly BlockingCollection<(SendOrPostCallback Run, object? State)> _queue = new();
    readonly Thread _thread;

    public SerialContext()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Longhand page" };
        _thread.Start();
    }

    void Loop()
    {
        SetSynchronizationContext(this);
        foreach (var (run, state) in _queue.GetConsumingEnumerable())
        {
            try { run(state); }
            catch (Exception ex) { Console.Error.WriteLine("page: " + ex); }
        }
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        try { _queue.Add((d, state)); }
        catch (InvalidOperationException) { /* shut down: nothing more to run */ }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Current == this) { d(state); return; }
        using var done = new ManualResetEventSlim();
        Post(s => { try { d(s); } finally { done.Set(); } }, state);
        done.Wait();
    }

    /// <summary>Starts the work on the page's thread; its failures are written to the console rather than lost.</summary>
    public void Run(Func<Task> work) => Post(_ => _ = Guard(work), null);

    static async Task Guard(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) { Console.Error.WriteLine("page: " + ex); }
    }

    public void Complete() => _queue.CompleteAdding();
}
