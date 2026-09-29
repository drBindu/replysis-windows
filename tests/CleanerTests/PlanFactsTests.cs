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

        Check(PlanFacts.AnswerCost == 5 && MainWindow.AnswerCreditCost == PlanFacts.AnswerCost,
            "an answer costs 5 credits, in both places the app states it");
        Check(PlanFacts.Answers(PlanFacts.FreeCredits) == 5, "Free is 5 answers");
        Check(PlanFacts.Answers(PlanFacts.ProCredits) == 500, "Pro is 500 answers a month");
        Check(PlanFacts.Answers(PlanFacts.MaxCredits) == 1_500, "Max is 1,500 answers a month");
        Check(PlanFacts.MonthlyCredits("pro") == 2_500 && PlanFacts.MonthlyCredits("MAX") == 7_500 &&
              PlanFacts.MonthlyCredits("lifetime") == 7_500 && PlanFacts.MonthlyCredits("free") == 25,
            "each plan resolves to its credits (case does not matter, retired Lifetime reads as Max)");
        Check(PlanFacts.MonthlyCredits("something new") == PlanFacts.FreeCredits && PlanFacts.MonthlyCredits(null) == PlanFacts.FreeCredits,
            "an unknown plan is treated as Free, never as paid");

        Check(PlanFacts.InterviewsFor(PlanFacts.ProCredits) == 15 && PlanFacts.InterviewsFor(PlanFacts.MaxCredits) == 50,
            "Pro is about 15 interviews and Max about 50");
        Check(PlanFacts.InterviewsFor(PlanFacts.FreeCredits) == 1, "there is never a '0 interviews'");

        // Free is a one-time trial, and the words must not promise a refill.
        Check(PlanFacts.IsFreeTrial("free", true) && PlanFacts.IsFreeTrial(null, false) && !PlanFacts.IsFreeTrial("pro", true),
            "Free and guests are the trial; Pro is not");
        Check(PlanFacts.AllowanceText("free", true) == "5 answers, one time", "the credits window says one time for Free");
        Check(PlanFacts.AllowanceText("pro", true) == "2,500 credits each month" && PlanFacts.AllowanceText("max", true) == "7,500 credits each month",
            "the credits window says each month for paid plans");

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
            "and points to Pro with a See plans button");
        Check(!Regex.IsMatch(end.Title + end.Body, @"\d"), "and states no number");
        Check(!end.Body.Contains("renew", StringComparison.OrdinalIgnoreCase),
            "and never says the answers will renew");
        Check(!(end.Title + end.Body).Any(ch => ch is '—' or '–' or '·' or '•'), "and follows the no dashes, no middle dots rule");
        Check(ListeningProblems.Describe(ListeningProblems.Kind.NoCredits).Body.Contains("renew"),
            "a paid plan that runs out is still told they renew");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "plan facts: all passed" : $"plan facts: {failed} FAILED");
        return failed;
    }
}
