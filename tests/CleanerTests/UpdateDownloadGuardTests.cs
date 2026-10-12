using InterviewCopilot;
using System.IO;

namespace CleanerTests;

/// <summary>
/// An update download that stops moving must be given up and tried again, not waited for forever (found on the real installed app,
/// 2026-10-10: complete except for its last 460 bytes, stuck on "Downloading..." with nothing to say why).
/// </summary>
internal static class UpdateDownloadGuardTests
{
    internal static int Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<int> RunAsync()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        var stall = TimeSpan.FromMilliseconds(250);
        var none = TimeSpan.Zero;

        // a download that finishes is simply done, once
        int calls = 0;
        bool done = await UpdateDownloadGuard.RunAsync((progress, token) => { calls++; progress(100); return Task.CompletedTask; },
            CancellationToken.None, stall, 3, none);
        Check(done && calls == 1, "a download that finishes is done after one attempt");

        // a download that stops moving is given up and started again
        calls = 0;
        var log = new List<string>();
        done = await UpdateDownloadGuard.RunAsync(async (progress, token) =>
        {
            calls++;
            if (calls == 1) { progress(40); await Task.Delay(Timeout.Infinite, token); }   // the connection died silently
            progress(100);
        }, CancellationToken.None, stall, 3, none, log.Add);
        Check(done && calls == 2, "a download that stalls is cancelled and the second attempt completes");
        Check(log.Count == 1 && log[0].Contains("stalled"), "the stall is logged as a stall");

        // every attempt stalling ends in a failure, not a wait without end
        calls = 0;
        var started = DateTime.UtcNow;
        done = await UpdateDownloadGuard.RunAsync(async (progress, token) =>
        {
            calls++;
            await Task.Delay(Timeout.Infinite, token);
        }, CancellationToken.None, stall, 2, none);
        Check(!done && calls == 2, "if every attempt stalls it reports failure after the last one");
        Check(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "and it does so in bounded time");

        // an ordinary error is retried too
        calls = 0;
        done = await UpdateDownloadGuard.RunAsync((progress, token) =>
        {
            calls++;
            if (calls == 1) throw new IOException("the connection was reset");
            return Task.CompletedTask;
        }, CancellationToken.None, stall, 3, none);
        Check(done && calls == 2, "a dropped connection is retried");

        // a slow download that keeps making progress is never cut off
        calls = 0;
        done = await UpdateDownloadGuard.RunAsync(async (progress, token) =>
        {
            calls++;
            for (int p = 1; p <= 10; p++) { await Task.Delay(100, token); progress(p * 10); }   // a second in all, a step every 100 ms
        }, CancellationToken.None, stall, 3, none);
        Check(done && calls == 1, "a slow download that keeps moving is left alone (longer than the stall limit in total)");

        // the person closing the app is not a failure to retry
        using var cts = new CancellationTokenSource();
        calls = 0;
        var pending = UpdateDownloadGuard.RunAsync(async (progress, token) => { calls++; await Task.Delay(Timeout.Infinite, token); },
            cts.Token, TimeSpan.FromSeconds(30), 3, none);
        cts.CancelAfter(100);
        bool cancelled = false;
        try { await pending; } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && calls == 1, "a cancel from outside stops it at once and is not retried");

        Console.WriteLine(failed == 0 ? "\nupdate download guard: all passed" : $"\nupdate download guard: {failed} FAILED");
        return failed;
    }
}
