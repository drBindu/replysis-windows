using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// What a listening session actually costs.
///
/// Every figure here is now within one minute of the wall time, whichever way
/// the user talks. That is the property worth protecting: the same interview
/// costs the same whether it arrived as one long answer or thirty short ones.
///
/// It did not hold before. The turn was the billing unit and every turn's
/// remainder was rounded up on top of the minutes already reported, so ten
/// 40-second turns billed 50% over while thirty 25-second turns billed nothing
/// at all. Both numbers are kept below as the "was" column, because a test
/// that only records the right answer cannot show what it is guarding against.
/// </summary>
internal static class BillingTests
{
    /// <summary>Runs the real meter arithmetic over a session, one 5 second tick at a time.</summary>
    private static double SpeechSeconds(TimeSpan length, Func<TimeSpan, bool> wordsAt)
    {
        var t0 = new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);
        DateTime last = DateTime.MinValue, since = t0, wordsSeen = DateTime.MinValue;
        double counted = 0;
        for (var t = TimeSpan.Zero; t < length; t += TimeSpan.FromSeconds(1))
        {
            var now = t0 + t;
            if (wordsAt(t)) wordsSeen = now;
            if (t.TotalSeconds % 5 == 4)
            {
                counted += ListeningBilling.CountableSeconds(since, now, wordsSeen);
                since = now;
            }
        }
        _ = last;
        return counted;
    }

    internal static int Run()
    {
        int failed = 0;

        // Listening time is speech time (owner, 2026-09-29). An open mic that
        // hears nobody must cost nothing, or Auto burns the free minutes while
        // the credits are still full.
        void Speech(string label, double seconds, Func<double, bool> ok)
        {
            bool pass = ok(seconds);
            Console.WriteLine($"{(pass ? "ok  " : "FAIL")}  {label}  ->  {seconds:0} s counted");
            if (!pass) failed++;
        }
        Speech("an hour of Auto with nobody speaking counts nothing",
            SpeechSeconds(TimeSpan.FromHours(1), _ => false), s => s == 0);
        Speech("ten minutes of continuous speech counts about ten minutes",
            SpeechSeconds(TimeSpan.FromMinutes(10), _ => true), s => s is >= 570 and <= 605);
        // The free tier's promise: 100 credits is 20 answers. Twenty 6 second
        // questions in an hour-long Auto session must fit well inside 15 minutes.
        Speech("twenty questions across an hour of Auto stay inside the 15 free minutes",
            SpeechSeconds(TimeSpan.FromHours(1),
                t => (int)t.TotalSeconds % 180 < 6),
            s => s < 15 * 60);
        Speech("a sentence with a two second pause in it is not split",
            SpeechSeconds(TimeSpan.FromSeconds(60),
                t => t.TotalSeconds is >= 10 and < 14 || t.TotalSeconds is >= 16 and < 20),
            s => s is >= 20 and <= 40);
        Speech("a silent room is not billed after the words stop",
            SpeechSeconds(TimeSpan.FromMinutes(5), t => t.TotalSeconds < 10),
            s => s <= 30);
        Speech("an empty result never starts the clock",
            ListeningBilling.CountableSeconds(
                new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 9, 29, 9, 0, 5, DateTimeKind.Utc), DateTime.MinValue),
            s => s == 0);

        void Case(string label, int got, int want)
        {
            bool ok = got == want;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}  ->  {got} min");
            if (!ok) { failed++; Console.WriteLine($"        expected {want}"); }
        }

        // A whole sitting under the floor is free. Under the old rule this was
        // true of every turn under the floor, separately, forever.
        Case("one 20s turn", ListeningBilling.MinutesForSession(20), 0);

        // The property that matters: the same wall time costs the same
        // regardless of how it was broken up. One long answer, or twelve short
        // ones, or a mixture - all six minutes, all billed six.
        Case("6 min as one turn", ListeningBilling.MinutesForSession(360), 6);
        Case("6 min as twelve 30s turns",
             ListeningBilling.MinutesForSession(Enumerable.Repeat(30.0, 12).ToArray()), 6);
        Case("6 min as a ragged mixture",
             ListeningBilling.MinutesForSession(12, 95, 8, 140, 45, 27, 33), 6);
        Case("6 min as six 60s turns",
             ListeningBilling.MinutesForSession(Enumerable.Repeat(60.0, 6).ToArray()), 6);

        // From the Mac session, and a harder case than anything written here:
        // ninety turns of four seconds. Every single one is far below the
        // floor, so under the old per-turn rule the whole six minutes billed
        // nothing at all.
        Case("6 min as ninety 4s turns",
             ListeningBilling.MinutesForSession(Enumerable.Repeat(4.0, 90).ToArray()), 6);

        // Mac's floor boundaries, checked against the same arithmetic.
        Case("25s session", ListeningBilling.MinutesForSession(25), 0);
        Case("35s session", ListeningBilling.MinutesForSession(35), 1);
        Case("59s session", ListeningBilling.MinutesForSession(59), 1);

        // At the floor it becomes a whole minute. Deliberate: an interview of
        // brief exchanges should not be free.
        Case("one 30s turn", ListeningBilling.MinutesForSession(30), 1);

        // 90 seconds pays for two minutes: the tick takes the whole minute,
        // the stop rounds the 30s remainder up to another.
        Case("one 90s turn (1.5 min of listening)", ListeningBilling.MinutesForSession(90), 2);

        // And it compounds per turn, not per sitting. Both Manual and Auto stop
        // the meter at the end of every exchange.
        double[] tenTurns = { 40, 40, 40, 40, 40, 40, 40, 40, 40, 40 };
        Case("ten 40s turns (6m 40s listened) - was 10, a 50% overcharge",
             ListeningBilling.MinutesForSession(tenTurns), 7);

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
        Case("thirty 25s turns (12.5 min listened) - was 0, entirely free",
             ListeningBilling.MinutesForSession(thirtyShort), 13);

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
