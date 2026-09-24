using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace InterviewCopilot;

internal static class StreamLines
{
    internal static async IAsyncEnumerable<string> ReadAsync(StreamReader reader,
        [EnumeratorCancellation] CancellationToken ct = default, TimeSpan? idleTimeout = null)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            idle.CancelAfter(idleTimeout ?? TimeSpan.FromSeconds(45));
            string? line;
            try { line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException("The answer stream stopped responding. Please try again."); }
            idle.CancelAfter(Timeout.InfiniteTimeSpan);
            if (line == null) yield break;
            ct.ThrowIfCancellationRequested();
            yield return line;
        }
    }
}
