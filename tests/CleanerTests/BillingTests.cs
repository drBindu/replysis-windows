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

        // And the error runs the other way below the floor. Thirty rapid
        // exchanges - an entirely normal screen - are free.
        //
        // This inverts the comment defending the floor, which exists because
        // "rounding every short turn down to nothing would make an interview of
        // brief exchanges free". It prevents that at 30s and above. Below 30s
        // it produces exactly the outcome it was written to prevent.
        double[] thirtyShort = Enumerable.Repeat(25.0, 30).ToArray();
        Case("thirty 25s turns (12.5 min of listening)",
             ListeningBilling.MinutesForSession(thirtyShort), 0);

        // A tenth of a second either side of the floor is the difference
        // between free and a whole minute.
        Case("one 29.9s turn", ListeningBilling.MinutesForSession(29.9), 0);
        Case("one 30.0s turn", ListeningBilling.MinutesForSession(30.0), 1);

        double actual = tenTurns.Sum() / 60.0;
        int billed = ListeningBilling.MinutesForSession(tenTurns);
        Console.WriteLine($"        ten short turns: {actual:0.0} min listened, {billed} min billed "
                        + $"({(billed / actual - 1) * 100:0}% over)");

        return failed;
    }
}
