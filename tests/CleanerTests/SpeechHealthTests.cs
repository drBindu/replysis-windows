using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// Watches the deafness detector fire, and watches it stay quiet.
///
/// The engine can fail in a way that looks exactly like success: still
/// connected, still reporting online, microphone level still moving, and no
/// words ever coming back. Three separate audio-reader bugs produced precisely
/// that shape, and a person noticed the empty transcript every time.
///
/// This is the net for that, and until now nobody had seen it catch anything.
/// A safety net nobody has watched work is a belief. These call the shipping
/// decision, so what passes here is what runs.
/// </summary>
internal static class SpeechHealthTests
{
    private static readonly DateTime T0 = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Never = DateTime.MinValue;

    private static bool Warn(
        DateTime now, DateTime speech, DateTime words,
        DateTime since, DateTime warned, out int secs) =>
        SpeechHealth.ShouldWarn(true, now, speech, words, since, warned, out secs);

    internal static int Run()
    {
        int failed = 0;

        void Case(string label, bool got, bool want, string why)
        {
            bool ok = got == want;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) { failed++; Console.WriteLine($"        expected {want}: {why}"); }
        }

        // ── It fires ──────────────────────────────────────────────────────────
        // Speech arriving now, nothing ever returned, listening for 20s.
        // This is the exact shape a broken FIFO reader produces.
        Case("fires: speech now, nothing ever returned, 20s in",
            Warn(T0.AddSeconds(20), T0.AddSeconds(19), Never, T0, Never, out int s1),
            true, "the failure it exists to catch");
        Console.WriteLine($"        reported {s1}s silent");
        if (s1 != 20) { failed++; Console.WriteLine("FAIL  silent seconds should be 20"); }

        // Words came back once, then stopped, while speech kept arriving.
        Case("fires: words stopped 15s ago, speech still arriving",
            Warn(T0.AddSeconds(30), T0.AddSeconds(29), T0.AddSeconds(15), T0, Never, out _),
            true, "transcription stopped mid-session");

        // Fires exactly at the threshold boundary, not a tick later.
        Case("fires: at 12s exactly",
            Warn(T0.AddSeconds(12), T0.AddSeconds(11), Never, T0, Never, out _),
            true, "12s is the threshold, not 12s+1");

        // The threshold is the contract; the poll interval is not. This is
        // consulted every 5s from the listening meter, so the ticks land at
        // 5, 10, 15 and a user observes 15s, never 12. Asserted so that
        // changing either number breaks a test rather than quietly moving a
        // figure the two platforms compare against each other.
        Case("quiet at the 10s tick, fires at the 15s tick",
            Warn(T0.AddSeconds(10), T0.AddSeconds(9), Never, T0, Never, out _),
            false, "10s < 12s threshold - the tick before is still silent");
        Case("observed latency is one poll past the threshold",
            Warn(T0.AddSeconds(15), T0.AddSeconds(14), Never, T0, Never, out int s15),
            true, "15s is the first tick past 12s");
        if (SpeechHealth.PollInterval != TimeSpan.FromSeconds(5))
        {
            failed++;
            Console.WriteLine("FAIL  poll interval changed; the observed-latency note is now wrong");
        }
        Console.WriteLine($"        threshold {SpeechHealth.DeafnessThreshold.TotalSeconds}s, "
                        + $"poll {SpeechHealth.PollInterval.TotalSeconds}s, observed {s15}s");

        // ── It stays quiet ────────────────────────────────────────────────────
        Case("quiet: engine offline",
            SpeechHealth.ShouldWarn(false, T0.AddSeconds(20), T0.AddSeconds(19), Never, T0, Never, out _),
            false, "an offline engine already says so itself");

        Case("quiet: a silent room",
            Warn(T0.AddSeconds(20), Never, Never, T0, Never, out _),
            false, "no speech has ever arrived - this is the false positive that would matter most");

        Case("quiet: speech stopped 5s ago",
            Warn(T0.AddSeconds(25), T0.AddSeconds(20), Never, T0, Never, out _),
            false, "they stopped talking; nothing is wrong");

        Case("quiet: words came back 2s ago",
            Warn(T0.AddSeconds(30), T0.AddSeconds(29), T0.AddSeconds(28), T0, Never, out _),
            false, "it is plainly working");

        Case("quiet: session only 5s old",
            Warn(T0.AddSeconds(5), T0.AddSeconds(4), Never, T0, Never, out _),
            false, "no time to return anything yet");

        Case("quiet: warned 30s ago",
            Warn(T0.AddSeconds(60), T0.AddSeconds(59), Never, T0, T0.AddSeconds(30), out _),
            false, "once a minute at most");

        Case("fires again: warned 61s ago",
            Warn(T0.AddSeconds(120), T0.AddSeconds(119), Never, T0, T0.AddSeconds(59), out _),
            true, "the minute has passed and it is still broken");

        Case("quiet: listening start unknown",
            Warn(T0.AddSeconds(20), T0.AddSeconds(19), Never, Never, Never, out _),
            false, "nothing to measure against");

        return failed;
    }
}
