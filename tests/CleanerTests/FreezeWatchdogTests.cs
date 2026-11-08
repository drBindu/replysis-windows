using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// A window that stops responding is noticed and reported quietly (bug round 40, 2026-11-07), and closing the app no longer
/// waits on a cloud sync that needed the closing thread to go on.
/// </summary>
internal static class FreezeWatchdogTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        // Kinds: short stalls stay out of the outage email, long ones count.
        Check(FreezeWatchdog.KindFor(TimeSpan.FromSeconds(12)) == "ui-freeze-10s-minor", "a 12 s stall is a minor report");
        Check(FreezeWatchdog.KindFor(TimeSpan.FromSeconds(30)) == "ui-freeze-30s", "30 s counts as a real freeze");
        Check(FreezeWatchdog.KindFor(TimeSpan.FromSeconds(95)) == "ui-freeze-60s", "a minute and more has its own bucket");

        // The decision, with a fake clock: posts every 2 s, the window stops answering, then answers again.
        var t0 = new DateTime(2026, 11, 7, 20, 0, 0, DateTimeKind.Utc);
        var w = new FreezeWatchdog();
        Check(w.Tick(t0) == FreezeWatchdog.Step.Nothing, "the first post starts the wait");
        Check(w.Answered(t0, t0.AddMilliseconds(5), out _) == FreezeWatchdog.Step.Nothing, "a prompt answer is not a freeze");

        var stuck = t0.AddSeconds(2);
        FreezeWatchdog.Step started = FreezeWatchdog.Step.Nothing;
        int startedCount = 0;
        for (int s = 0; s <= 20; s += 2)
        {
            var step = w.Tick(stuck.AddSeconds(s));
            if (step == FreezeWatchdog.Step.FreezeStarted) { started = step; startedCount++; }
        }
        Check(started == FreezeWatchdog.Step.FreezeStarted, "no answer for over 10 s is noticed");
        Check(startedCount == 1, "and noticed once, not on every tick");

        var ended = w.Answered(stuck, stuck.AddSeconds(25), out TimeSpan frozen);
        Check(ended == FreezeWatchdog.Step.FreezeEnded && Math.Abs(frozen.TotalSeconds - 25) < 0.01,
            "the window answering ends the freeze and says how long it lasted");
        Check(w.Answered(stuck.AddSeconds(4), stuck.AddSeconds(25), out _) == FreezeWatchdog.Step.Nothing,
            "the posts queued behind it answering do not report it again");
        Check(w.Tick(stuck.AddSeconds(26)) == FreezeWatchdog.Step.Nothing, "after it recovers the next post starts a fresh wait");

        // The flag, for a freeze the app is closed in.
        string folder = Path.Combine(Path.GetTempPath(), "replysis-freeze-" + Guid.NewGuid().ToString("N"));
        try
        {
            Check(FreezeWatchdog.TakeFlag(folder) == null, "no flag means the last run did not end frozen");
            FreezeWatchdog.WriteFlag(folder, t0);
            string? text = FreezeWatchdog.TakeFlag(folder);
            Check(text != null && text.StartsWith("2026-11-07T20:00:00"), "a run ended while frozen is found on the next launch");
            Check(FreezeWatchdog.TakeFlag(folder) == null, "and reported only once");
        }
        finally { try { Directory.Delete(folder, true); } catch { } }

        string root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        string app = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        Check(app.Contains("FreezeWatchdog.Start(Dispatcher") && app.Contains("\"previous-run-froze\""),
            "launch starts the watchdog and reports a previous frozen run");

        string sync = File.ReadAllText(Path.Combine(root, "CloudSessionSync.cs"));
        var awaits = Regex.Matches(sync, @"await [^;]*;").Select(m => m.Value).ToList();
        Check(awaits.Count >= 4 && awaits.All(a => a.Contains("ConfigureAwait(false)")),
            "every await in the cloud sync is free of the window thread, so closing never waits on it");

        string debug = File.ReadAllText(Path.Combine(root, "DebugWindow.cs"));
        Check(debug.Contains("LastSource = source"), "the freeze line names the last thing logged, its tag only");
        return failed;
    }
}
