using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// What a listening session actually costs.
///
/// These do not assert that the current arithmetic is correct. They record
/// what it is, in numbers a customer could check for themselves, because the
/// gap between wall time and billed time turned out to be large and nobody
/// had written it down. If the owner decides to change the rounding, these
/// fail and the new figures go in - which is the point.
/// </summary>
internal static class BillingTests
{
    internal static int Run()
    {
        int failed = 0;

        void Case(string label, int got, int want)
        {
            bool ok = got == want;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}  ->  {got} min");
            if (!ok) { failed++; Console.WriteLine($"        expected {want}"); }
        }

        // A turn under the floor is free.
        Case("one 20s turn", ListeningBilling.MinutesForSession(20), 0);

        // At the floor it becomes a whole minute. Deliberate: an interview of
        // brief exchanges should not be free.
        Case("one 30s turn", ListeningBilling.MinutesForSession(30), 1);

        // 90 seconds pays for two minutes: the tick takes the whole minute,
        // the stop rounds the 30s remainder up to another.
        Case("one 90s turn (1.5 min of listening)", ListeningBilling.MinutesForSession(90), 2);

        // And it compounds per turn, not per sitting. Both Manual and Auto stop
        // the meter at the end of every exchange.
        double[] tenTurns = { 40, 40, 40, 40, 40, 40, 40, 40, 40, 40 };
        Case("ten 40s turns (6m 40s of listening)",
             ListeningBilling.MinutesForSession(tenTurns), 10);

        // A long single turn is charged close to honestly - the damage is in
        // the number of turns, not the duration.
        Case("one 600s turn (10 min of listening)", ListeningBilling.MinutesForSession(600), 10);

        double actual = tenTurns.Sum() / 60.0;
        int billed = ListeningBilling.MinutesForSession(tenTurns);
        Console.WriteLine($"        ten short turns: {actual:0.0} min listened, {billed} min billed "
                        + $"({(billed / actual - 1) * 100:0}% over)");

        return failed;
    }
}
