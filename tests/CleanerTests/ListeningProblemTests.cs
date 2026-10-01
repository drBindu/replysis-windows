using System.Text.RegularExpressions;
using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// The rule behind "no more silent problems" (owner, 2026-09-29): every reason
/// the app can be open and deaf has to be explained in words, and a new reason
/// cannot be added without one.
///
/// It exists because a Free-plan tester with 55 credits and no listening time
/// saw a small red label, spoke to a silent app for minutes, and decided her
/// laptop was broken.
/// </summary>
internal static class ListeningProblemTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        // Every value of the enum, so adding one without describing it fails here.
        foreach (ListeningProblems.Kind kind in Enum.GetValues<ListeningProblems.Kind>())
        {
            var d = ListeningProblems.Describe(kind);
            Check(d.Title.Length is >= 8 and <= 60, $"{kind}: has a short title");
            Check(d.Body.Length >= 60, $"{kind}: explains itself in a full sentence or two");
            Check(d.Label.Length > 0 && d.Label == d.Label.ToUpperInvariant(), $"{kind}: has the mic label");

            // Numbers belong to the server and the website. A copy here goes stale.
            // Key names such as F8 and Ctrl+Alt+F12 are not plan numbers.
            string words = Regex.Replace(d.Title + d.Body, @"\bF\d{1,2}\b", "");
            Check(!Regex.IsMatch(words, @"\d"), $"{kind}: states no number that could drift from the server");
            Check(!Regex.IsMatch(words, @"\b(minutes?|hours?|mins?)\b", RegexOptions.IgnoreCase),
                $"{kind}: never talks in minutes or hours, credits are the only meter");

            // The owner's copy rules: no dashes, no middle dots.
            Check(!(d.Title + d.Body).Any(ch => ch is '—' or '–' or '·' or '•'),
                $"{kind}: no dashes or middle dots");
            Check(!d.Body.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                  !d.Body.Contains("exception", StringComparison.OrdinalIgnoreCase),
                $"{kind}: says what to do, not what the code called it");
        }

        // The limits are told apart, and the two meters are named.
        var time = ListeningProblems.Describe(ListeningProblems.Kind.NoListeningTime);
        Check(time.Body.Contains("You still have answers left"),
            "monthly limit: says answers are fine, so answers left over is not a contradiction");
        Check(time.Body.Contains("fair use"), "monthly limit: calls it a fair use limit");
        Check(time.Body.Contains("F8"), "no listening time: says what still works");
        Check(time.Step == ListeningProblems.NextStep.SeePlans, "no listening time: offers plans");
        Check(ListeningProblems.Describe(ListeningProblems.Kind.NoCredits).Step == ListeningProblems.NextStep.MoreAnswers,
            "no answers: offers to add answers or upgrade");

        // Detection
        ListeningProblems.Kind? D(bool online = false, int code = 0, bool audio = false, bool wait = false,
                                  bool noMic = false, bool stalled = false, bool credits = false) =>
            ListeningProblems.Detect(online, code, audio, wait, noMic, stalled, credits);
        Check(D(online: true, code: 402) is null, "nothing is wrong while it is hearing");
        Check(D(code: 402, audio: true) == ListeningProblems.Kind.NoListeningTime, "402 for listening time");
        Check(D(code: 402, audio: false) == ListeningProblems.Kind.NoCredits, "402 for credits");
        Check(D(code: 401) == ListeningProblems.Kind.SignInExpired, "401 is a sign in problem");
        Check(D(code: 503) == ListeningProblems.Kind.ServiceUnavailable, "503 is the service");
        Check(D(wait: true) == ListeningProblems.Kind.WaitingToReconnect, "a backoff is a reconnect");
        Check(D(noMic: true) == ListeningProblems.Kind.NoMicrophone, "no microphone");
        Check(D(stalled: true) == ListeningProblems.Kind.NoSpeechService, "a stalled connection");
        Check(D() is null, "just starting is not a problem");

        // The bug a tester met: after "no listening time" the app kept asking, hit the
        // hourly request limit, and the rate limit reply hid the real reason.
        Check(D(code: 429, audio: true, wait: true) == ListeningProblems.Kind.NoListeningTime,
            "a rate limit reply does not hide 'no listening time'");
        Check(D(code: 429, credits: true, wait: true) == ListeningProblems.Kind.NoCredits,
            "a rate limit reply does not hide 'no credits'");
        Check(D(code: 0, audio: true) == ListeningProblems.Kind.NoListeningTime,
            "the reason survives the status code being cleared");
        Check(D(online: true, code: 429, audio: true) is null, "once it is hearing, nothing is wrong");
        Check(D(code: 429, wait: true) == ListeningProblems.Kind.WaitingToReconnect,
            "a rate limit with no known reason is just a reconnect");

        // What one answer costs in the words matches the server's number.
        Check(MainWindow.AnswerCreditCost == 5, "an answer costs 5 credits, as INTERVIEW_QUESTION_COST on the server");
        Check(MainWindow.ExplainCredits(55, false).Contains("About 11 answers left"),
            "55 credits reads as about 11 answers");
        Check(MainWindow.ExplainCredits(100, false).Contains("About 20 answers"),
            "100 credits reads as 20 answers");
        Check(MainWindow.ExplainCredits(55, false).Contains("uses one answer") &&
              !Regex.IsMatch(MainWindow.ExplainCredits(55, false), "credits?", RegexOptions.IgnoreCase),
            "the tooltip talks in answers and never says credits");
        Check(!Regex.IsMatch(MainWindow.ExplainCredits(55, true), @"\b(minutes?|hours?)\b"),
            "even at the limit, the tooltip never talks in minutes");

        // A connection that was up and keeps breaking is named as that, not blamed on a VPN.
        var now = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        Check(!SpeechHealth.ConnectionKeepsDropping(new[] { now.AddSeconds(-30) }, now), "one drop is a hiccup, not a pattern");
        Check(SpeechHealth.ConnectionKeepsDropping(new[] { now.AddSeconds(-110), now.AddSeconds(-50) }, now), "two drops within minutes keep dropping");
        Check(!SpeechHealth.ConnectionKeepsDropping(new[] { now.AddMinutes(-20), now.AddSeconds(-50) }, now), "an old drop does not count");
        Check(ListeningProblems.Detect(false, 0, false, false, false, true, connectionKeepsDropping: true) == ListeningProblems.Kind.UnstableConnection,
            "an engine that keeps dropping says so, instead of blaming a VPN");
        Check(ListeningProblems.Detect(false, 402, true, false, false, false, connectionKeepsDropping: true) == ListeningProblems.Kind.NoListeningTime,
            "a definite refusal still wins over a bad connection");
        var unstable = ListeningProblems.Describe(ListeningProblems.Kind.UnstableConnection);
        Check(!unstable.Body.Contains("VPN", StringComparison.OrdinalIgnoreCase) && unstable.Body.Contains("hotspot", StringComparison.OrdinalIgnoreCase),
            "the text names the real cause and does not send someone to a hotspot they may already be on");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "listening problems: all passed" : $"listening problems: {failed} FAILED");
        return failed;
    }
}
