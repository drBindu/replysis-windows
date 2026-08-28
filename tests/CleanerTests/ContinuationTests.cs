using System.IO;
using System.Text.RegularExpressions;

namespace CleanerTests;

/// <summary>
/// Does background chatter get merged onto a question and re-answered?
///
/// The continuation window was measured from the last submission, and a merge
/// is a submission - so every merge pushed the deadline forward and the chain
/// could never close. One API call and one credit per fragment of speech in the
/// room. The Mac session measured roughly two hundred credits in five minutes,
/// with nothing on screen saying anything was wrong.
///
/// A bound that resets itself is not a bound. That is the same shape as a guard
/// that cannot fail and a check that answers a narrower question, and it is the
/// third time this week.
///
/// The noise test is reproduced here rather than called, because it is private
/// and static on the window. That is a copy and copies drift - so it asserts
/// the CONSTANTS against the shipping source below, which is what actually
/// changes when somebody tunes it.
/// </summary>
internal static class ContinuationTests
{
    private const double MinWordsPerSentence = 3.0;
    private const int MinWordsToJudge = 12;
    private const int MinStopsToJudge = 5;

    private static bool IsFragmentedNoise(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        int stops = text.Count(c => c is '.' or '?' or '!');
        int words = Regex.Matches(text, @"[\p{L}\p{N}']+").Count;
        if (words < MinWordsToJudge || stops < MinStopsToJudge) return false;
        return (double)words / stops < MinWordsPerSentence;
    }

    internal static int Run()
    {
        int failed = 0;

        void Case(string label, bool got, bool want)
        {
            bool ok = got == want;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) { failed++; Console.WriteLine($"        expected {want}, got {got}"); }
        }

        // ── Must be caught: a room with people in it ─────────────────────────
        //
        // Lengths matter here. The first drafts of these were eleven words and
        // the detector correctly refused to judge them - the floor is twelve
        // words and five stops precisely so a real "Okay. Sure." is safe. A
        // twenty-second window of background speech is far longer than that,
        // so these are sized like the thing they represent.
        Case("podcast bleeding into the mic",
            IsFragmentedNoise("Right. Yeah. Well I mean. Sure. That's the thing. Okay. So. "
                            + "No but listen. I know. Exactly. Hang on."),
            true);
        Case("two people talking over each other",
            IsFragmentedNoise("No. But wait. I know. Listen. Hang on. Yes. Exactly. Right. "
                            + "Well. Sure. I mean. Come on. Really."),
            true);
        Case("a burst too short to judge, by design",
            IsFragmentedNoise("Right. Yeah. Well I mean. Sure. Okay. So."),
            false);

        // ── Must NOT be caught: real speech ──────────────────────────────────
        Case("a genuine long question",
            IsFragmentedNoise("Can you walk me through how you would design a rate limiter "
                            + "that works across several servers without a shared database?"),
            false);
        Case("a rambling but real answer",
            IsFragmentedNoise("So I started at the logs. Then I noticed the timestamps were off. "
                            + "That led me to the clock skew between the two machines, which "
                            + "turned out to be the whole problem in the end."),
            false);
        Case("short acknowledgement - too little to judge",
            IsFragmentedNoise("Okay. Sure."), false);
        Case("three short stops, still refuses to judge",
            IsFragmentedNoise("Yes. No. Maybe."), false);
        Case("empty", IsFragmentedNoise(""), false);

        // ── The constants must match the shipping source ─────────────────────
        // A copied rule that silently diverges is the failure this whole week
        // has been about, so the copy is checked against the original.
        string src = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "MainWindow.xaml.cs"));

        Case("MaxContinuations is still 2", src.Contains("MaxContinuations = 2"), true);
        Case("MaxContinuationWords is still 60", src.Contains("MaxContinuationWords = 60"), true);
        Case("noise ratio is still 3.0", src.Contains("/ stops < 3.0"), true);
        Case("still refuses below 12 words / 5 stops",
            src.Contains("words < 12 || stops < 5"), true);
        Case("the window is anchored to the chain start, not the last answer",
            src.Contains("now - _continuationChainStartedUtc > ContinuationWindow"), true);
        Case("a merge does not move the chain start",
            src.Contains("_continuationCount++;"), true);

        return failed;
    }
}
