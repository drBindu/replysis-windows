using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// A screenshot sent ahead steps aside for the live voice (2026-10-16: an unpaced 651 KB upload at the start of
/// listening left the speech service answering nothing for 8 seconds, and the question went out empty).
/// </summary>
internal static class PacedUploadTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        byte[] body = new byte[100 * 1024];
        new Random(7).NextBytes(body);

        // No waiting for the voice: every byte arrives, in order, in pieces no bigger than 16 KB, paced at the rate.
        var waits = new List<TimeSpan>();
        var c = new PacedContent(body, "application/json", PacedContent.ListeningBytesPerSecond, () => false,
                                 (t, ct) => { waits.Add(t); return Task.CompletedTask; });
        var ms = new MemoryStream();
        c.WriteAsync(ms, CancellationToken.None).GetAwaiter().GetResult();
        Check(ms.ToArray().AsSpan().SequenceEqual(body), "the picture arrives whole and in order");
        double paced = 0; foreach (var w in waits) paced += w.TotalSeconds;
        double expected = (double)(body.Length - (body.Length % PacedContent.PieceBytes == 0 ? PacedContent.PieceBytes : body.Length % PacedContent.PieceBytes))
                          / PacedContent.ListeningBytesPerSecond;
        Check(Math.Abs(paced - expected) < 0.05, $"paced at 256 KB a second while listening ({paced:F2}s for 100 KB)");
        Check(c.Holds == 0 && c.Held == TimeSpan.Zero, "nothing held when the voice is keeping up");

        // The voice falls behind for the first three looks: the upload waits, counts one hold, and then goes on.
        int looks = 0;
        var held = new PacedContent(body, "application/json", PacedContent.ListeningBytesPerSecond, () => looks++ < 3,
                                    (t, ct) => Task.CompletedTask);
        ms = new MemoryStream();
        held.WriteAsync(ms, CancellationToken.None).GetAwaiter().GetResult();
        Check(held.Holds == 1 && held.Held == TimeSpan.FromMilliseconds(450), "waits 3 x 150 ms while the speech service is behind");
        Check(ms.Length == body.Length, "and still sends the whole picture after");

        // Muted, nothing to protect: the rate is so high the waits round to nothing.
        waits.Clear();
        var fast = new PacedContent(body, "application/json", int.MaxValue / 2, () => false,
                                    (t, ct) => { waits.Add(t); return Task.CompletedTask; });
        fast.WriteAsync(new MemoryStream(), CancellationToken.None).GetAwaiter().GetResult();
        double total = 0; foreach (var w in waits) total += w.TotalMilliseconds;
        Check(total < 1, "not listening: no pacing worth the name");

        // The length is known up front, so the request is not sent chunked.
        Check(fast.Headers.ContentLength == body.Length, "content length is the picture's size");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "paced upload: all passed" : $"paced upload: {failed} FAILED");
        return failed;
    }
}
