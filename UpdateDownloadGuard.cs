using System;
using System.Threading;
using System.Threading.Tasks;

namespace InterviewCopilot
{
    /// <summary>
    /// Keeps an update download from hanging forever.
    ///
    /// Found 2026-10-10 on the real installed app: the update to 1.0.32 sat on
    /// "Downloading..." for good, with the package on disk complete except for its
    /// last 460 bytes. The connection had dropped without closing, and the
    /// downloader has no read timeout of its own, so it waited for bytes that were
    /// never going to arrive. Nothing told the person, and nothing tried again.
    ///
    /// This watches the download's own progress. If it does not move for a while
    /// the attempt is cancelled and started again, a few times, and if it still
    /// cannot finish it reports that it failed instead of waiting without end. A
    /// failed check costs nothing: the next launch checks again.
    /// </summary>
    internal static class UpdateDownloadGuard
    {
        /// <summary>
        /// How long the progress may stand still before an attempt is given up.
        /// Progress is reported in whole percents of a roughly 100 MB package, so
        /// a step is about 1 MB: two minutes still covers a very slow line.
        /// </summary>
        internal static readonly TimeSpan DefaultStall = TimeSpan.FromMinutes(2);
        internal const int DefaultAttempts = 3;
        internal static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Runs <paramref name="attempt"/> until it completes, watching the
        /// progress it reports. Returns true when one attempt finished, false when
        /// every attempt stalled or failed. A cancel from <paramref name="outer"/>
        /// is not a failure to retry: it propagates.
        /// </summary>
        internal static async Task<bool> RunAsync(
            Func<Action<int>, CancellationToken, Task> attempt,
            CancellationToken outer,
            TimeSpan stall,
            int attempts,
            TimeSpan retryDelay,
            Action<string>? log = null)
        {
            for (int n = 1; n <= Math.Max(1, attempts); n++)
            {
                outer.ThrowIfCancellationRequested();

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
                long lastMove = Environment.TickCount64;
                int lastValue = -1;
                bool stalled = false;

                void Progress(int value)
                {
                    if (value != lastValue)
                    {
                        lastValue = value;
                        Interlocked.Exchange(ref lastMove, Environment.TickCount64);
                    }
                }

                using var watchdogStop = new CancellationTokenSource();
                Task watchdog = Task.Run(async () =>
                {
                    try
                    {
                        while (!watchdogStop.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(20, Math.Min(1000, stall.TotalMilliseconds / 4))),
                                watchdogStop.Token).ConfigureAwait(false);
                            long idle = Environment.TickCount64 - Interlocked.Read(ref lastMove);
                            if (idle >= stall.TotalMilliseconds)
                            {
                                stalled = true;
                                linked.Cancel();
                                return;
                            }
                        }
                    }
                    catch (OperationCanceledException) { /* the attempt ended first */ }
                });

                try
                {
                    await attempt(Progress, linked.Token).ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException) when (outer.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log?.Invoke(stalled
                        ? $"Download attempt {n} of {attempts} stalled, no progress for {(int)stall.TotalSeconds}s."
                        : $"Download attempt {n} of {attempts} failed: {ex.GetType().Name}.");
                }
                finally
                {
                    watchdogStop.Cancel();
                    try { await watchdog.ConfigureAwait(false); } catch { /* the watchdog never throws out */ }
                }

                if (n < attempts)
                {
                    try { await Task.Delay(retryDelay, outer).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                }
            }

            return false;
        }
    }
}
