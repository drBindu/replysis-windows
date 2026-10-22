using System.IO;
using System.Diagnostics;
using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// The Windows capture feed, end to end on this machine's real devices: the mic and the speakers opened through
/// Windows, read by the engine's own pipe reader (the class lifted from speechmatics_engine.py), at real-time rate.
/// Only with REPLYSIS_LIVE_AUDIO_TEST=1, because a build machine has no sound devices.
/// </summary>
internal static class NativeAudioFeedTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }
        if (Environment.GetEnvironmentVariable("REPLYSIS_LIVE_AUDIO_TEST") != "1")
        {
            Console.WriteLine("skip  live audio (set REPLYSIS_LIVE_AUDIO_TEST=1)");
            return 0;
        }

        string engine = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "speechmatics_engine.py"));
        string reader = Path.Combine(Path.GetTempPath(), "replysis_pipe_reader.py");
        File.WriteAllText(reader, """
import sys, time, threading
src = open(sys.argv[1], encoding="utf8").read()
a = src.index("class _NativePipeStream"); b = src.index("def _open_native")
SAMPLE_RATE = 16000
exec(src[a:b])
s = _NativePipeStream(sys.argv[2], "TEST")
t0 = time.monotonic(); total = 0; peak = 0; reads = 0
while time.monotonic() - t0 < 3.0:
    d = s.read(1600); reads += 1
    total += len(d)
    for i in range(0, len(d), 64):
        v = abs(int.from_bytes(d[i:i+2], "little", signed=True)); peak = max(peak, v)
print(f"reads={reads} bytes={total} peak={peak} alive={s.alive} secs={time.monotonic()-t0:.2f}", flush=True)
""");

        foreach (bool loopback in new[] { false, true })
        {
            string what = loopback ? "speakers" : "mic";
            var sw = Stopwatch.StartNew();
            using NativeAudioFeed? feed = NativeAudioFeed.TryStart(loopback, null);
            Check(feed != null, $"{what}: Windows opened the default device \"{feed?.DeviceName}\" in {sw.ElapsedMilliseconds} ms");
            if (feed == null) continue;
            var psi = new ProcessStartInfo("python", $"-I \"{reader}\" \"{engine}\" \"{feed.PipePath}\"")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            // A device change while sound flows to a reading engine: reopening used to wait for the capture thread
            // while holding the lock that thread needed, and froze for good. Ten reopens during the read must finish.
            Task? reopen = null;
            if (!loopback)
            {
                var restart = typeof(NativeAudioFeed).GetMethod("StartCapture", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                reopen = Task.Run(() => { Thread.Sleep(600); for (int i = 0; i < 10; i++) { restart.Invoke(feed, null); Thread.Sleep(150); } });
            }
            string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            Console.WriteLine("      " + outp.Trim());
            var m = System.Text.RegularExpressions.Regex.Match(outp, @"reads=(\d+) bytes=(\d+)");
            int reads = m.Success ? int.Parse(m.Groups[1].Value) : 0;
            // 3 s of 100 ms reads: about 30, each one exactly one chunk, silence-padded when nothing plays.
            Check(reads >= 26 && reads <= 34, $"{what}: the engine reads at real time ({reads} reads of 100 ms in 3 s)");
            if (reopen != null)
                Check(reopen.Wait(TimeSpan.FromSeconds(15)), $"{what}: ten reopens while the engine reads all finish (no freeze)");


        }
        return failed;
    }
}
