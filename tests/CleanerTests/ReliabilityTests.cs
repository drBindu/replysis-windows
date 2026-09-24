using InterviewCopilot;
using System.IO;
using System.Text;

namespace CleanerTests;

internal static class ReliabilityTests
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

        var identity = new OperationEpoch();
        string token = "account A";
        var old = identity.Capture(() => token);
        identity.Advance(() => token = "signed out");
        Check(!identity.TryApply(old.Generation, () => token = "old refresh"), "late refresh cannot undo logout");
        Check(token == "signed out", "logout state is retained");
        identity.Advance(() => token = "account B");
        Check(!identity.TryApply(old.Generation, () => token = "account A"), "late account A result cannot replace account B");
        long current = identity.Current;
        Check(identity.TryApply(current, () => token = "fresh B") && token == "fresh B", "current account refresh is applied");
        identity.Advance(() => token = "fresh B");
        Check(!identity.IsCurrent(current), "signing into the same account still invalidates old requests");

        var writer = new OrderedSessionWriter();
        var release = new ManualResetEventSlim();
        var order = new List<int>();
        Task first = writer.Enqueue(() => { release.Wait(); order.Add(1); });
        Task second = writer.Enqueue(() => order.Add(2));
        Task end = writer.Enqueue(() => order.Add(3));
        Check(!writer.Drain(TimeSpan.FromMilliseconds(20)), "shutdown flush is bounded while a write is blocked");
        release.Set();
        await Task.WhenAll(first, second, end);
        Check(order.SequenceEqual(new[] { 1, 2, 3 }), "finalization cannot overtake the last answer");
        Check(writer.Drain(TimeSpan.FromSeconds(1)), "shutdown drains completed writes");
        Task bad = writer.Enqueue(() => throw new IOException("simulated failure"));
        Task next = writer.Enqueue(() => order.Add(4));
        try { await bad; } catch (IOException) { }
        await next;
        Check(order.Last() == 4, "one failed write does not block later sessions");
        release.Dispose();

        string testDirectory = Path.Combine(Path.GetTempPath(), "replysis-save-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        try
        {
            string path = Path.Combine(testDirectory, "session.txt");
            SecureDataProtector.WriteProtectedFile(path, "SESSION TEST\n");
            var diskWriter = new OrderedSessionWriter();
            for (int i = 0; i < 40; i++)
            {
                int turn = i;
                _ = diskWriter.Enqueue(() => SecureDataProtector.WriteProtectedFile(path,
                    SecureDataProtector.ReadProtectedFile(path) + $"Q: {turn}\nA: answer {turn}\n"));
            }
            await diskWriter.Enqueue(() => SecureDataProtector.WriteProtectedFile(path,
                SecureDataProtector.ReadProtectedFile(path) + "DURATION_SECONDS: 3600\n"));
            string saved = SecureDataProtector.ReadProtectedFile(path);
            Check(saved.Contains("A: answer 39\nDURATION_SECONDS: 3600"), "encrypted file retains the last answer before finalization");
            Check(File.ReadAllText(path).StartsWith("dpapi:"), "ordered writes retain encrypted-at-rest session storage");
        }
        finally { Directory.Delete(testDirectory, recursive: true); }

        DateTime now = DateTime.UtcNow;
        Check(AudioSourceRules.QuietDuration(now, now.AddMinutes(-4), DateTime.MinValue) == TimeSpan.FromMinutes(4),
            "never hearing a first word still triggers the practice tip");
        Check(AudioSourceRules.QuietDuration(now, now.AddMinutes(-4), now.AddSeconds(-5)) == TimeSpan.FromSeconds(5),
            "recent words reset the quiet interval");
        Check(AudioSourceRules.QuietDuration(now, DateTime.MinValue, DateTime.MinValue) == TimeSpan.Zero,
            "not listening has no quiet interval");
        Check(!GlobalHotkey.ListeningShortcutAllowed(9000, 1000, false, false, false, false),
            "slow typing in another app never toggles with safe defaults");
        Check(GlobalHotkey.ListeningShortcutAllowed(1001, 1000, true, true, false, false),
            "explicit listening shortcut works immediately after typing");
        Check(GlobalHotkey.ListeningShortcutAllowed(9000, 1000, false, false, true, false),
            "plain Space still works in Replysis");
        Check(GlobalHotkey.ListeningShortcutAllowed(9000, 1000, false, false, false, true),
            "plain global Space remains an explicit opt-in");
        Check(!new SettingsWindow.AppConfig().PlainSpaceEverywhere, "safe shortcut defaults are off for plain global Space");

        using (var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("data: one\ndata: two\n"))))
        {
            var lines = new List<string>();
            await foreach (string line in StreamLines.ReadAsync(reader)) lines.Add(line);
            Check(lines.SequenceEqual(new[] { "data: one", "data: two" }), "stream reader returns every line and terminates at EOF");
        }
        using (var reader = new StreamReader(new StalledStream()))
        {
            bool timedOut = false;
            try { await foreach (string line in StreamLines.ReadAsync(reader, idleTimeout: TimeSpan.FromMilliseconds(30))) { } }
            catch (TimeoutException) { timedOut = true; }
            Check(timedOut, "a stalled stream times out without synchronous EndOfStream blocking");
        }
        using (var reader = new StreamReader(new StalledStream()))
        using (var cts = new CancellationTokenSource(30))
        {
            bool cancelled = false;
            try { await foreach (string line in StreamLines.ReadAsync(reader, cts.Token)) { } }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "interrupt cancels a stalled answer read");
        }
        return failed;
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous read must never be used");
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
