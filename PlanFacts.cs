using System;

namespace InterviewCopilot
{
    /// <summary>
    /// The plan sizes as this app states them. Decided with the owner on 2026-09-29.
    ///
    ///   Free   25 credits     5 answers, ONCE (never refilled)
    ///   Pro    2,500 credits  500 answers a month
    ///   Max    7,500 credits  1,500 answers a month
    ///   An answer or a screen read costs 5 credits.
    ///
    /// These must equal PLAN_MONTHLY_CREDITS in the website's data/productFacts.ts and the Java
    /// server's FirestoreCreditsService. The server is what actually enforces them; this only
    /// decides what the app SAYS. scripts/check-sync.mjs (in the website repo) compares all three,
    /// so changing one of them alone fails a release check rather than reaching a customer.
    /// </summary>
    internal static class PlanFacts
    {
        internal const int AnswerCost = 5;
        internal const int FreeCredits = 25;
        internal const int ProCredits = 2_500;
        internal const int MaxCredits = 7_500;
        internal const int TeamsCredits = 10_000;   // retired plan, kept so an old account reads correctly

        /// <summary>One real interview is about 30 answers with Auto on (it sometimes answers twice).</summary>
        internal const int AnswersPerInterview = 30;

        internal static int MonthlyCredits(string? plan) => (plan ?? "").Trim().ToLowerInvariant() switch
        {
            "pro" => ProCredits,
            "max" or "lifetime" => MaxCredits,
            "teams" => TeamsCredits,
            _ => FreeCredits,
        };

        /// <summary>Whether this person is on the one-time free trial (a guest counts).</summary>
        internal static bool IsFreeTrial(string? plan, bool signedIn) =>
            !signedIn || MonthlyCredits(plan) == FreeCredits;

        /// <summary>What a balance can actually buy: rounded down, never overstated (12 credits is 2 answers).</summary>
        internal static int Answers(int credits) => Math.Max(0, credits) / AnswerCost;

        /// <summary>"12", or "1.5k" on the badge.</summary>
        internal static string AnswersShort(int credits)
        {
            int a = Answers(credits);
            return a >= 1000 ? $"{a / 1000.0:F1}k" : a.ToString("N0");
        }

        /// <summary>"1 answer" or "12 answers".</summary>
        internal static string AnswersLabel(int credits)
        {
            int a = Answers(credits);
            return $"{a:N0} {(a == 1 ? "answer" : "answers")}";
        }

        /// <summary>The badge text at the top of the app: "12 answers", "1.5k answers".</summary>
        internal static string BadgeText(int credits) =>
            $"{AnswersShort(credits)} {(Answers(credits) == 1 ? "answer" : "answers")}";

        /// <summary>Interviews worth of answers, rounded down to a multiple of five, at least one.</summary>
        internal static int InterviewsFor(int credits) =>
            Math.Max(1, Answers(credits) / AnswersPerInterview / 5 * 5);

        /// <summary>The allowance line on the credits window.</summary>
        internal static string AllowanceText(string? plan, bool signedIn)
        {
            if (IsFreeTrial(plan, signedIn)) return $"{Answers(FreeCredits)} answers, one time";
            return $"{Answers(MonthlyCredits(plan)):N0} answers each month";
        }
    }
}
