using System;

namespace InterviewCopilot;

// An asynchronous result belongs to the identity that started it, even if the
// user signs out and back into the same account before it finishes.
internal sealed class OperationEpoch
{
    private readonly object _gate = new();
    private long _generation;
    internal long Current { get { lock (_gate) return _generation; } }
    internal bool IsCurrent(long generation) { lock (_gate) return generation == _generation; }
    internal (long Generation, T Value) Capture<T>(Func<T> read)
    {
        lock (_gate) return (_generation, read());
    }
    internal void Advance(Action change)
    {
        lock (_gate) { _generation++; change(); }
    }
    internal bool TryApply(long generation, Action apply)
    {
        lock (_gate)
        {
            if (generation != _generation) return false;
            apply();
            return true;
        }
    }
}
