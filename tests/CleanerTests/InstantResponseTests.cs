using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// The product is the speed (owner, 2026-09-29: "we made the app for instant response").
/// Recorded questions played into the real app showed Auto sitting 1.1 to 2.6 seconds after
/// the speech service had already said the speaker stopped, and words arriving in phrases
/// instead of typing in. These fail if either creeps back.
/// </summary>
internal static class InstantResponseTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        // ── When Auto sends a question that plainly ended ────────────────────────────
        long t0 = DateTime.UtcNow.Ticks;
        DateTime started = new DateTime(t0 - 10 * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        long sec = TimeSpan.TicksPerSecond;

        Check(AutoTurnRules.SpeechFinalIsLatest(t0, t0 - sec, t0 - 10, started),
            "speech final after the last words counts as the end of the speech");
        Check(!AutoTurnRules.SpeechFinalIsLatest(t0, t0 + 1, t0 - 10, started),
            "words heard after it cancel it (a partial came in later)");
        Check(!AutoTurnRules.SpeechFinalIsLatest(t0, t0 - sec, t0 + 1, started),
            "words confirmed after it cancel it (a final came in later)");
        Check(!AutoTurnRules.SpeechFinalIsLatest(0, 0, 0, started), "no signal, no end");
        Check(!AutoTurnRules.SpeechFinalIsLatest(t0 - 20 * sec, t0 - 30 * sec, t0 - 30 * sec, started),
            "one from before this listening turn started is not evidence about this one");

        Check(AutoTurnRules.QuickSendWaitMs(false, 0, 2800) == AutoTurnRules.SpeechFinalConfirmMs,
            "after speech final, a plain question waits only the short confirmation");
        Check(AutoTurnRules.QuickSendWaitMs(true, 0, 2800) == AutoTurnRules.UtteranceEndConfirmMs,
            "after utterance end (a full second of silence) it waits less still");
        Check(AutoTurnRules.QuickSendWaitMs(false, 1950, 2800) == 1950,
            "a speaker who pauses 1.5 s inside a sentence keeps their own longer wait");
        Check(AutoTurnRules.QuickSendWaitMs(false, 9000, 2800) == 2800, "the wait is capped");
        Check(AutoTurnRules.QuickSendWaitMs(true, 100, 2800) == AutoTurnRules.UtteranceEndConfirmMs,
            "a tiny pace floor does not raise the minimum");

        // What the person feels: the service takes about 0.3 s of silence to say "speech final",
        // plus the trip. Measured on 2026-09-29 the whole wait after it was 1.8 s.
        Check(AutoTurnRules.SpeechFinalConfirmMs <= 800,
            $"the confirmation after speech final is {AutoTurnRules.SpeechFinalConfirmMs} ms, well under the 1,500 ms it used to be");
        Check(AutoTurnRules.UtteranceEndConfirmMs <= 400, "and after utterance end it is short");
        Check(AutoTurnRules.SpeechFinalConfirmMs >= 400,
            "but not so short that a pause between two clauses is taken for the end");

        Check(AutoTurnRules.FlushStableChecks(true, false) == 1,
            "once the service called the end of the speech, nothing is in flight: one look");
        Check(AutoTurnRules.FlushStableChecks(false, false) == 5, "otherwise 100 ms of no change");
        Check(AutoTurnRules.FlushStableChecks(true, true) == 40 && AutoTurnRules.FlushStableChecks(false, true) == 40,
            "a sentence that plainly is not over still waits, whatever the signal says");

        // The slow-speaker floor must learn from the speaker, not from the connection.
        DateTime prev = new DateTime(t0, DateTimeKind.Utc);
        DateTime later = prev.AddSeconds(1.5);
        long ms = TimeSpan.TicksPerMillisecond;
        Check(!AutoTurnRules.GapWasASpeakerPause(true, 0, 0, prev, later),
            "an update gap with no pause heard by the service is the connection's pace, not the speaker's");
        Check(AutoTurnRules.GapWasASpeakerPause(true, t0 + sec / 2, 0, prev, later),
            "a speech final in the gap, then more words, means the speaker did pause");
        Check(AutoTurnRules.GapWasASpeakerPause(true, 0, t0 + sec, prev, later.AddSeconds(1)),
            "so does an utterance end");
        Check(!AutoTurnRules.GapWasASpeakerPause(true, later.Ticks - 20 * ms, 0, prev, later),
            "but a speech final that arrives WITH the words closing the gap is the end of the speech, not a pause in it");
        Check(AutoTurnRules.GapWasASpeakerPause(true, t0 - 60 * ms, 0, prev, later),
            "a speech final a few milliseconds before the file was polled still counts");
        Check(!AutoTurnRules.GapWasASpeakerPause(true, t0 - 2 * sec, t0 - 3 * sec, prev, later),
            "and one from an earlier pause does not count again");
        Check(AutoTurnRules.GapWasASpeakerPause(false, 0, 0, prev, later),
            "an engine that reports no endpoints (the fallbacks) is judged as it always was");

        // ── The upload is smaller ────────────────────────────────────────────────────
        var prompt = System.Text.Json.JsonSerializer.Serialize(new
        {
            question = "What is dependency injection?",
            resume = "",
            messages = PromptBuilder.BuildMessages("", "What is dependency injection?", true)
        });
        byte[] promptBytes = System.Text.Encoding.UTF8.GetBytes(prompt);
        byte[] packed = RequestCompression.Gzip(promptBytes);
        Check(packed.Length < promptBytes.Length * 0.55,
            $"the real prompt goes from {promptBytes.Length:N0} to {packed.Length:N0} bytes on the wire");

        using (var back = new System.IO.Compression.GZipStream(new System.IO.MemoryStream(packed), System.IO.Compression.CompressionMode.Decompress))
        using (var restored = new System.IO.MemoryStream())
        {
            back.CopyTo(restored);
            Check(restored.ToArray().AsSpan().SequenceEqual(promptBytes), "and comes back byte for byte, including non-ASCII text");
        }
        byte[] accents = System.Text.Encoding.UTF8.GetBytes(new string('é', 700) + " — ünïcode ✓ 😀");
        using (var back = new System.IO.Compression.GZipStream(new System.IO.MemoryStream(RequestCompression.Gzip(accents)), System.IO.Compression.CompressionMode.Decompress))
        using (var restored = new System.IO.MemoryStream())
        {
            back.CopyTo(restored);
            Check(restored.ToArray().AsSpan().SequenceEqual(accents), "accented and emoji text survives");
        }

        var content = RequestCompression.Content(promptBytes);
        Check(content.Headers.ContentEncoding.Contains("gzip") && content.Headers.ContentType?.MediaType == "application/json",
            "the request says it is gzip and still says it is JSON");
        Check(RequestCompression.ShouldCompress(20_000) && !RequestCompression.ShouldCompress(200),
            "a large request is compressed, a tiny one is not worth it");
        Check(RequestCompression.ServerRefusedCompression(400) && RequestCompression.ServerRefusedCompression(415) && RequestCompression.ServerRefusedCompression(501),
            "a server that cannot read the body triggers one plain retry");
        Check(!RequestCompression.ServerRefusedCompression(200) && !RequestCompression.ServerRefusedCompression(401) &&
              !RequestCompression.ServerRefusedCompression(402) && !RequestCompression.ServerRefusedCompression(429) &&
              !RequestCompression.ServerRefusedCompression(500) && !RequestCompression.ServerRefusedCompression(503),
            "and nothing else is ever repeated: not a sign-in, not credits, not a rate limit, not a failure that may have charged");

        // ── The transcript types in ──────────────────────────────────────────────────
        const double Frame = 1.0 / 60;

        Check(TranscriptTyper.Advance("anything", "", Frame) == "", "clearing shows nothing");
        Check(TranscriptTyper.Advance(null, null, Frame) == "", "nothing at all is nothing");
        Check(TranscriptTyper.Advance("What is Java?", "What is Java?", Frame) == "What is Java?", "already complete stays complete");
        Check(TranscriptTyper.Advance("What is Jav", "What is Java?", Frame) is "What is Java" or "What is Java?",
            "a couple of characters waiting appear within one frame");
        Check(TranscriptTyper.Advance("", "What", 0).Length >= 1, "the first character never waits, even with no time elapsed");

        // How far behind it falls when a phrase lands all at once.
        double Seconds(int chars)
        {
            string target = new string('a', chars);
            string shown = "";
            double t = 0;
            while (shown != target && t < 5) { shown = TranscriptTyper.Advance(shown, target, Frame); t += Frame; }
            return t;
        }
        Check(Seconds(15) <= 0.25, $"a 15 character phrase is fully typed in {Seconds(15):0.00} s");
        Check(Seconds(40) <= 0.40, $"a 40 character phrase in {Seconds(40):0.00} s, not a slow crawl");
        Check(Seconds(100) <= 0.55, $"even 100 characters at once in {Seconds(100):0.00} s");
        Check(Seconds(400) <= 0.90, $"a whole paragraph in {Seconds(400):0.00} s, so it can never fall far behind");

        // Never anything that is not in the real text.
        var rng = new Random(29);
        bool alwaysPrefix = true, neverLonger = true;
        string cur = "";
        string tgt = "";
        for (int i = 0; i < 4000; i++)
        {
            if (rng.Next(12) == 0)
            {
                // the service sends a longer sentence, or revises earlier words
                string[] words = { "what", "is", "dependency", "injection", "tell", "me", "about", "yourself", "kubernetes" };
                int n = rng.Next(1, 12);
                tgt = string.Join(" ", Enumerable.Range(0, n).Select(_ => words[rng.Next(words.Length)]));
                if (rng.Next(3) == 0) tgt = "";
            }
            cur = TranscriptTyper.Advance(cur, tgt, Frame * rng.Next(1, 4));
            if (!tgt.StartsWith(cur, StringComparison.Ordinal)) alwaysPrefix = false;
            if (cur.Length > tgt.Length) neverLonger = false;
        }
        Check(alwaysPrefix, "4,000 random updates and revisions: the screen always shows a prefix of the real text");
        Check(neverLonger, "and never shows more than exists");

        string revised = TranscriptTyper.Advance("Tell me about your self", "Tell me about yourself.", Frame);
        Check(revised.StartsWith("Tell me about your", StringComparison.Ordinal) && "Tell me about yourself.".StartsWith(revised, StringComparison.Ordinal),
            "when the service changes its mind, the changed words are taken back and typed again");

        bool splitPair = false;
        string emoji = "Hi \U0001F600 there";
        string shownEmoji = "";
        for (int i = 0; i < 40 && shownEmoji != emoji; i++)
        {
            shownEmoji = TranscriptTyper.Advance(shownEmoji, emoji, 0.001);
            if (shownEmoji.Length > 0 && char.IsHighSurrogate(shownEmoji[^1])) splitPair = true;
        }
        Check(!splitPair && shownEmoji == emoji, "a character made of two units is never shown half typed");

        // ── A laptop wakes up and the network is not there yet (2026-09-29) ─────────────
        // The app must keep asking, in seconds, for as long as it is open.
        Check(RecoveryPolicy.KeyRetryAfterNoConnection(1) == TimeSpan.FromSeconds(2),
            "the first retry after no connection is 2 seconds, not half a minute");
        Check(RecoveryPolicy.KeyRetryAfterNoConnection(5) == TimeSpan.FromSeconds(30) &&
              RecoveryPolicy.KeyRetryAfterNoConnection(500) == TimeSpan.FromSeconds(30),
            "and settles at 30 seconds however long it lasts");
        bool nonDecreasing = true;
        for (int n = 1; n < 20; n++)
            if (RecoveryPolicy.KeyRetryAfterNoConnection(n + 1) < RecoveryPolicy.KeyRetryAfterNoConnection(n)) nonDecreasing = false;
        Check(nonDecreasing, "it backs off, never speeds up");
        Check(RecoveryPolicy.CredentialRenewalWaitSeconds(0) == 5 && RecoveryPolicy.CredentialRenewalWaitSeconds(1) == 15 &&
              RecoveryPolicy.CredentialRenewalWaitSeconds(2) == 30 && RecoveryPolicy.CredentialRenewalWaitSeconds(3) == 60,
            "renewing rejected speech credentials: 5, 15, 30, then a minute");
        Check(RecoveryPolicy.CredentialRenewalWaitSeconds(100) == 60 && RecoveryPolicy.CredentialRenewalWaitSeconds(-1) == 5,
            "there is always a next attempt: it never gives up, and never spins");

        // The quick send wait is 450 ms, and goes back to 650 ms once an interviewer has added a tail to a question.
        Check(AutoTurnRules.QuickSendWaitMs(false, 0, 2800, 0) == 450, "a finished question is sent 450 ms after the service says the speaker stopped");
        Check(AutoTurnRules.QuickSendWaitMs(false, 0, 2800, 1) == 650 && AutoTurnRules.QuickSendWaitMs(false, 0, 2800, 3) == 650,
            "after a tail has been merged this interview, it is cautious again");
        Check(AutoTurnRules.QuickSendWaitMs(true, 0, 2800, 5) == AutoTurnRules.UtteranceEndConfirmMs,
            "an utterance end is still the short wait whatever happened before");
        Check(AutoTurnRules.QuickSendWaitMs(false, 1950, 2800, 0) == 1950, "a slow speaker's own pause still sets the floor");

        // A brand-new account's first speech key request was refused for credits (the server had not yet written the
        // account), and the app then said "no answers" with five answers on the badge and waited five minutes.
        Check(RecoveryPolicy.CreditsRefusalIsStale(true, false, 25, 5),
            "refused for credits but the balance shows five answers: the refusal is stale, ask again now");
        Check(RecoveryPolicy.CreditsRefusalIsStale(true, false, 5, 5),
            "exactly one answer is enough to be stale");
        Check(!RecoveryPolicy.CreditsRefusalIsStale(true, false, 0, 5) && !RecoveryPolicy.CreditsRefusalIsStale(true, false, 4, 5),
            "a balance below one answer still believes the refusal, so a really empty account is not hammered");
        Check(!RecoveryPolicy.CreditsRefusalIsStale(false, false, 25, 5),
            "with no refusal remembered there is nothing to forget");
        Check(RecoveryPolicy.CreditsRefusalIsStale(true, true, 0, 5),
            "an unlimited account is never really out of credits");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "instant response: all passed" : $"instant response: {failed} FAILED");
        return failed;
    }
}
