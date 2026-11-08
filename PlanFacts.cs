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
        /// <summary>
        /// The Practice plan (website, 2026-10-27): mock interviews with Maya plus 100 answers a month (50 in India). It sits
        /// on a free account; the server's credits reply says "practice": true and the app treats the plan as "practice".
        /// </summary>
        internal const int PracticeCredits = 500;
        /// <summary>
        /// The free trial every new account starts on (website productFacts TRIAL_CREDITS, server TRIAL_CREDITS): 50 answers
        /// for 1 day. It runs on plan "pro" on the server, whose credits reply says "trial": true, and the app treats the
        /// plan as "trial", so it is never shown as Pro, never promised a monthly refill, and gets the upgrade prompt.
        /// </summary>
        internal const int TrialCredits = 250;

        /// <summary>One real interview is about 30 answers with Auto on (it sometimes answers twice).</summary>
        internal const int AnswersPerInterview = 30;

        internal static int MonthlyCredits(string? plan) => (plan ?? "").Trim().ToLowerInvariant() switch
        {
            "pro" => ProCredits,
            "max" or "lifetime" => MaxCredits,
            "teams" => TeamsCredits,
            "practice" => PracticeCredits,
            "trial" => TrialCredits,
            _ => FreeCredits,
        };

        /// <summary>Whether this person is on free answers that never refill: the trial, free, or a guest.</summary>
        internal static bool IsFreeTrial(string? plan, bool signedIn) =>
            !signedIn || Normal(plan) == "trial" || MonthlyCredits(plan) == FreeCredits;

        /// <summary>
        /// The plan as the server's credits reply should be read: the trial runs on "pro" but is "trial", and Practice sits
        /// on "free" but is "practice".
        /// </summary>
        /// <summary>The server's refreshesAt (ISO, UTC), or null when missing or unreadable.</summary>
        internal static DateTime? ParseRefreshesAt(string? text) =>
            DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at.UtcDateTime : null;

        /// <summary>
        /// The refresh date to show (round 38, 2026-11-07): the server's, in local time, when it said one that is still
        /// ahead; otherwise the 1st of next month, the old guess. A monthly subscription refills on its own renewal day, so
        /// the guess told a customer who subscribed on the 15th "Dec 1" for a refill on Dec 15.
        /// </summary>
        internal static DateTime RefreshDateShown(DateTime? refreshesAtUtc, DateTime nowLocal)
        {
            if (refreshesAtUtc is DateTime at && at.ToLocalTime() > nowLocal) return at.ToLocalTime();
            return new DateTime(nowLocal.Year, nowLocal.Month, 1).AddMonths(1);
        }

        internal static string PlanFromReply(string? plan, bool trial, bool practice)
        {
            string p = Normal(plan);
            if (p.Length == 0) p = "free";
            if (trial && p != "free") return "trial";
            if (practice && p == "free") return "practice";
            return p;
        }

        /// <summary>The plan's name for people: "Free trial", "Starter plan", "Pro plan". Never "pro plan", never Pro for the trial.</summary>
        internal static string PlanLabel(string? plan, bool signedIn)
        {
            if (!signedIn) return "Free trial";
            return Normal(plan) switch
            {
                "trial" => "Free trial",
                "" or "free" => "Starter plan",
                "practice" => "Practice plan",
                "pro" => "Pro plan",
                "max" => "Max plan",
                "lifetime" => "Legacy Lifetime plan",
                "teams" => "Legacy Teams plan",
                var other => char.ToUpperInvariant(other[0]) + other[1..] + " plan",
            };
        }

        private static string Normal(string? plan) => (plan ?? "").Trim().ToLowerInvariant();

        /// <summary>What a balance can actually buy: rounded down, never overstated (12 credits is 2 answers).</summary>
        internal static int Answers(int credits) => Math.Max(0, credits) / AnswerCost;

        /// <summary>
        /// What the server writes into the answer stream when the charge finds no answers left (InterviewController
        /// noAnswersLeftEvent). The question's own reply, so the app must recognise it as out of answers, not a fault.
        /// </summary>
        internal const string ServerNoAnswersLeft = "You have no answers left.";

        internal static bool IsServerNoAnswersLeft(string? said) =>
            string.Equals((said ?? "").Trim(), ServerNoAnswersLeft, StringComparison.OrdinalIgnoreCase);

        /// <summary>The app's own words for being out of answers, the same whichever way the server said it.</summary>
        internal const string OutOfAnswersMessage = "You are out of answers. Open the answers badge at the top to add more.";

        /// <summary>
        /// The problem name an answer failure is reported under. Running out of answers is a normal state, not a fault, so
        /// it carries "nocredits", which the server's alert rule (DiagnosticsController.isRealError) never counts. Reported
        /// as an ordinary failure, every customer reaching the end of their answers counted towards an outage email.
        /// </summary>
        internal static string AnswerProblemKind(bool noAnswersLeft, string slug) =>
            noAnswersLeft ? "answer-nocredits" : "answer-" + slug;

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
            // A guest has the device's free answers. A signed in free account has used its trial; the website says the same.
            if (!signedIn) return $"{Answers(FreeCredits)} answers, one time";
            if (Normal(plan) == "trial") return $"{Answers(TrialCredits)} answers to try everything";
            if (IsFreeTrial(plan, signedIn)) return "Free answers do not refresh";
            if ((plan ?? "").Trim().ToLowerInvariant() == "practice") return "Practice plan, answers refill each month";
            return $"{Answers(MonthlyCredits(plan)):N0} answers each month";
        }
    }
}
