using System.Text.RegularExpressions;
using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// The plans as agreed with the owner on 2026-09-29, and how the app words them.
///
///   Free   5 answers, once (25 credits)       Pro   500 a month (2,500)      Max   1,500 a month (7,500)
///
/// The server enforces these and the website states them; scripts/check-sync.mjs compares all three.
/// This is the app's own guard: the numbers it says, and the free trial not being described as monthly.
/// </summary>
internal static class PlanFactsTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        // Round 38: the refresh date shown is the server's when it gave one still ahead, else the 1st of next month.
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Local);
        var renewal = PlanFacts.ParseRefreshesAt("2026-12-15T10:00:00Z");
        Check(renewal == new DateTime(2026, 12, 15, 10, 0, 0, DateTimeKind.Utc), "the server's refresh date is read as UTC");
        Check(PlanFacts.RefreshDateShown(renewal, now) == renewal!.Value.ToLocalTime(), "a monthly renewal day is shown, not Dec 1");
        Check(PlanFacts.RefreshDateShown(null, now) == new DateTime(2026, 12, 1), "no server date falls back to the 1st of next month");
        Check(PlanFacts.RefreshDateShown(PlanFacts.ParseRefreshesAt("2026-11-01T00:00:00Z"), now) == new DateTime(2026, 12, 1),
            "a date already past falls back");
        Check(PlanFacts.ParseRefreshesAt("not a date") == null && PlanFacts.ParseRefreshesAt(null) == null, "an unreadable date is ignored");

        Check(PlanFacts.AnswerCost == 5 && MainWindow.AnswerCreditCost == PlanFacts.AnswerCost,
            "an answer costs 5 credits, in both places the app states it");
        Check(PlanFacts.Answers(PlanFacts.FreeCredits) == 5, "Free is 5 answers");
        Check(PlanFacts.Answers(PlanFacts.ProCredits) == 500, "Pro is 500 answers a month");
        Check(PlanFacts.Answers(PlanFacts.MaxCredits) == 1_500, "Max is 1,500 answers a month");
        Check(PlanFacts.MonthlyCredits("pro") == 2_500 && PlanFacts.MonthlyCredits("MAX") == 7_500 &&
              PlanFacts.MonthlyCredits("lifetime") == 7_500 && PlanFacts.MonthlyCredits("free") == 25,
            "each plan resolves to its credits (case does not matter, retired Lifetime reads as Max)");
        Check(PlanFacts.MonthlyCredits("practice") == 500 && !PlanFacts.IsFreeTrial("practice", true)
              && PlanFacts.AllowanceText("practice", true).StartsWith("Practice plan"),
              "the Practice plan is not the one-time free answers");
        Check(PlanFacts.MonthlyCredits("something new") == PlanFacts.FreeCredits && PlanFacts.MonthlyCredits(null) == PlanFacts.FreeCredits,
            "an unknown plan is treated as Free, never as paid");

        Check(PlanFacts.InterviewsFor(PlanFacts.ProCredits) == 15 && PlanFacts.InterviewsFor(PlanFacts.MaxCredits) == 50,
            "Pro is about 15 interviews and Max about 50");
        Check(PlanFacts.InterviewsFor(PlanFacts.FreeCredits) == 1, "there is never a '0 interviews'");

        // Free is a one-time trial, and the words must not promise a refill.
        Check(PlanFacts.IsFreeTrial("free", true) && PlanFacts.IsFreeTrial(null, false) && !PlanFacts.IsFreeTrial("pro", true),
            "Free and guests are the trial; Pro is not");
        Check(PlanFacts.AllowanceText(null, false) == "5 answers, one time", "the credits window says one time for a guest");
        Check(PlanFacts.AllowanceText("free", true) == "Free answers do not refresh",
            "a signed in free account has used its trial and is not promised 5 answers (the website says the same)");

        // The free trial runs on plan "pro" on the server; the credits reply says "trial": true (2026-11-07: a trial
        // customer saw "pro plan", "500 answers each month" and no upgrade prompt when the 50 ran out).
        Check(PlanFacts.PlanFromReply("pro", trial: true, practice: false) == "trial"
              && PlanFacts.PlanFromReply("pro", trial: false, practice: false) == "pro"
              && PlanFacts.PlanFromReply("free", trial: false, practice: true) == "practice"
              && PlanFacts.PlanFromReply("free", trial: true, practice: false) == "free"
              && PlanFacts.PlanFromReply(null, false, false) == "free",
            "the credits reply is read as trial, practice or the plan itself");
        Check(PlanFacts.IsFreeTrial("trial", true) && PlanFacts.MonthlyCredits("trial") == PlanFacts.TrialCredits
              && PlanFacts.Answers(PlanFacts.TrialCredits) == 50,
            "the trial is 50 free answers that never refill, so running out brings the upgrade prompt");
        Check(PlanFacts.AllowanceText("trial", true) == "50 answers to try everything",
            "the credits window states the trial, not a monthly Pro allowance");
        Check(PlanFacts.PlanLabel("trial", true) == "Free trial" && PlanFacts.PlanLabel("pro", true) == "Pro plan"
              && PlanFacts.PlanLabel("free", true) == "Starter plan" && PlanFacts.PlanLabel("max", true) == "Max plan"
              && PlanFacts.PlanLabel("practice", true) == "Practice plan" && PlanFacts.PlanLabel("pro", false) == "Free trial",
            "plan names read like the website: the trial is never Pro and nothing is lower case");
        Check(PlanFacts.AllowanceText("pro", true) == "500 answers each month" && PlanFacts.AllowanceText("max", true) == "1,500 answers each month",
            "the answers window says each month for paid plans");
        Check(PlanFacts.BadgeText(15) == "3 answers" && PlanFacts.BadgeText(5) == "1 answer" && PlanFacts.BadgeText(0) == "0 answers" &&
              PlanFacts.BadgeText(7_500) == "1.5k answers" && PlanFacts.BadgeText(12) == "2 answers",
            "the badge says answers, rounded down so it never overstates (12 credits is 2 answers)");

        var trial = MainWindow.ExplainCredits(15, false, freeTrial: true);
        Check(trial.Contains("3 free answers") && trial.Contains("do not refresh") && !trial.Contains("this month"),
            "the tooltip for a free account does not promise a monthly refill");
        var paid = MainWindow.ExplainCredits(55, false);
        Check(paid.Contains("this month"), "the tooltip for a paid plan still says this month");

        // Running out of the free answers is the moment someone decides. It says what Pro is,
        // in words, with no number that could drift from the server.
        var end = ListeningProblems.Describe(ListeningProblems.Kind.NoCredits, freeTrial: true);
        Check(end.Title == "Your free answers are used", "the end of the trial is named as the end of a trial");
        Check(end.Body.Contains("Pro") && end.Step == ListeningProblems.NextStep.SeePlans,
            "and its button goes to the plans to upgrade (owner, 2026-10-27)");
        Check(!Regex.IsMatch(end.Title + end.Body, @"\d"), "and states no number");
        Check(!end.Body.Contains("renew", StringComparison.OrdinalIgnoreCase),
            "and never says the answers will renew");
        Check(!(end.Title + end.Body).Any(ch => ch is '—' or '–' or '·' or '•'), "and follows the no dashes, no middle dots rule");
        Check(ListeningProblems.Describe(ListeningProblems.Kind.NoCredits).Body.Contains("renew"),
            "a paid plan that runs out is still told they renew");
        Check(!Regex.IsMatch(ListeningProblems.Describe(ListeningProblems.Kind.NoCredits).Title + ListeningProblems.Describe(ListeningProblems.Kind.NoCredits).Body, "credit", RegexOptions.IgnoreCase),
            "and no screen the app shows says credits");

        // The answer path charges inside the stream, so running out arrives as the server's own words, not a 402.
        Check(PlanFacts.IsServerNoAnswersLeft("You have no answers left.") && PlanFacts.IsServerNoAnswersLeft(" you have no answers left. "),
            "the server's in-stream out of answers reply is recognised");
        Check(!PlanFacts.IsServerNoAnswersLeft("The AI service is temporarily unavailable. Please try again.")
              && !PlanFacts.IsServerNoAnswersLeft(null) && !PlanFacts.IsServerNoAnswersLeft(""),
            "and a real fault is not mistaken for it");
        // Running out is reported under a name the server's alert rule never counts (isRealError skips "nocredits").
        Check(PlanFacts.AnswerProblemKind(true, "you-have-no-answers-left") == "answer-nocredits",
            "out of answers is reported as nocredits, never as an outage");
        Check(PlanFacts.AnswerProblemKind(false, "answers-are-busy") == "answer-answers-are-busy",
            "a real failure keeps its own name");
        Check(PlanFacts.OutOfAnswersMessage.Contains("answers badge") && !PlanFacts.OutOfAnswersMessage.Contains("credit", StringComparison.OrdinalIgnoreCase),
            "the app says out of answers the same way whichever path the server used");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "plan facts: all passed" : $"plan facts: {failed} FAILED");
        return failed;
    }
}
