using System;
using System.Threading.Tasks;

namespace InterviewCopilot;

internal sealed class OrderedSessionWriter
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    // Queue on the calling thread, not inside Task.Run: worker scheduling must
    // never reorder two answers or let finalization overtake the last answer.
    internal Task Enqueue(Action write)
    {
        lock (_gate)
            return _tail = _tail.ContinueWith(_ => write(), default,
                TaskContinuationOptions.None, TaskScheduler.Default);
    }

    internal bool Drain(TimeSpan timeout)
    {
        Task pending;
        lock (_gate) pending = _tail;
        try { return pending.Wait(timeout); }
        catch (AggregateException) { return false; }
    }
}
