using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// Auto mode must not glue the next question onto the last one, answer a corrected
/// copy of a question twice, or treat spelled-out noise as more question. All three
/// happened in a mock interview run through the real app.
/// </summary>
internal static class AutoTurnTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        var submit = new DateTime(2026, 9, 17, 12, 35, 24, DateTimeKind.Utc);
        Check(!AutoTurnRules.RecognitionSettled(
                  submit.AddSeconds(3), submit, submit.AddSeconds(2.4), DateTime.MinValue, submit, false),
              "repeated partials keep Auto listening even when the text itself is unchanged");
        Check(AutoTurnRules.RecognitionSettled(
                  submit.AddSeconds(4.5), submit, submit.AddSeconds(2.4), DateTime.MinValue, submit, false),
              "two seconds without any provider result permits the fallback");
        Check(AutoTurnRules.RecognitionSettled(
                  submit.AddSeconds(1), submit, submit.AddMilliseconds(900), submit.AddMilliseconds(950), submit, false),
              "a final newer than the partial completes the recognition turn");
        Check(AutoTurnRules.RecognitionSettled(
                  submit.AddMilliseconds(400), submit, submit.AddMilliseconds(350), DateTime.MinValue, submit, true),
              "an explicit provider boundary completes the recognition turn");
        Check(AutoTurnRules.CompletionGraceMs(2800) == 2800,
              "a recognizer full stop gets continuation grace instead of causing a first-clause answer");
        Check(AutoTurnRules.CompletionGraceMs(2800) == 2800,
              "provider-inferred question punctuation cannot bypass continuation grace");
        Check(AutoTurnRules.HoldCompletedQuestionOnEmptyRestart(true, "What is dependency injection?", ""),
              "an empty Auto restart keeps the completed interviewer question visible");
        Check(!AutoTurnRules.HoldCompletedQuestionOnEmptyRestart(true, "What is dependency injection?", "And where did you use it?"),
              "real words replace the completed interviewer question");
        Check(!AutoTurnRules.HoldCompletedQuestionOnEmptyRestart(false, "What is dependency injection?", ""),
              "Manual mode still clears for an explicit new turn");
        Check(!AutoTurnRules.IsRevisionOf("Can Redis replace Postgres?", "Can Postgres replace Redis?"),
              "reversing the comparison must produce a new answer");
        Check(!AutoTurnRules.IsRevisionOf("Should I use Redis?", "Should I not use Redis?"),
              "removing negation changes intent and must not be discarded");
        Check(!AutoTurnRules.IsRevisionOf("Why use Redis?", "How and why use Redis?"),
              "a focused follow-up remains answerable");
        Check(!AutoTurnRules.IsRevisionOf("Why did you choose Redis?", "How did you choose Redis?"),
              "a why follow-up must not be suppressed as a how revision");
        Check(!AutoTurnRules.IsRevisionOf("Would you not use caching?", "Would you use caching?"),
              "a negated follow-up remains answerable");
        Check(!AutoTurnRules.IsRevisionOf("What are the disadvantages of using Redis for this service?", "What are the advantages of using Redis for this service?"),
              "a one-word change in intent must still get an answer");
        Check(AutoTurnRules.UnconsumedTranscript("Explain Redis. With an example.", "Explain Redis.") == "With an example.",
              "two-word answered prompts do not swallow or repeat the next clause");
        Check(AutoTurnRules.StartedSoonEnoughToContinue(submit, submit.AddSeconds(1.2)), "words 1.2s after the submission can continue it");
        Check(!AutoTurnRules.StartedSoonEnoughToContinue(submit, submit.AddSeconds(8)), "words 8s later are the next question");
        Check(!AutoTurnRules.StartedSoonEnoughToContinue(submit, DateTime.MinValue), "no words yet cannot continue anything");
        Check(!AutoTurnRules.StartedSoonEnoughToContinue(DateTime.MinValue, submit), "nothing submitted yet, nothing to continue");

        Check(AutoTurnRules.IsSpelledOutNoise("Capital m o t o g p f."), "spelled-out letters are noise");
        Check(!AutoTurnRules.IsSpelledOutNoise("and full time"), "a real tail is not noise");
        Check(!AutoTurnRules.IsSpelledOutNoise("C2C or W2 or full time."), "C2C and W2 are not noise");

        Check(AutoTurnRules.IsRevisionOf("Before we wrap up, do you have for me?", "Before we wrap up, do you have questions for me?"), "a corrected copy is the same question");
        Check(AutoTurnRules.IsRevisionOf("Are you authorized to work in the US", "Are you authorized to work in the US and will you need sponsorship in the future?"), "a shorter copy is the same question");
        Check(!AutoTurnRules.IsRevisionOf("What is Kafka?", "What is Java?"), "a different subject is a new question");
        Check(!AutoTurnRules.IsRevisionOf("And how do you see a role like this fitting into your career path?", "Are you authorized to work in the US?"), "the next question is not a revision");
        Check(!AutoTurnRules.IsRevisionOf("What is Java and where do you use it?", "What is Java?"), "a longer question adds something");

        Check(AutoTurnRules.IsRevisionOf("and will you need sponsorship in the future?", "Are you authorized to work in the US and will you need sponsorship in the future?"), "a re-delivered tail of the sent question is not more question");
        Check(AutoTurnRules.IsRevisionOf("the restful services you have built?", "Can you tell me about the restful services you have built?"), "the late end of a sent question is not a new question");

        // The interviewer's reply arrives stuck to the question just answered
        Check(AutoTurnRules.StripAnsweredPrefix("Before we wrap up, do you have any questions for me? Good. Our top priority is scaling.",
                  "Before we wrap up, do you have any questions for me?") == "Good. Our top priority is scaling.",
              "the answered question is removed from the front");
        Check(AutoTurnRules.StripAnsweredPrefix("What is Kafka?", "What is Java?") == "What is Kafka?",
              "a different question is left alone");
        Check(AutoTurnRules.StripAnsweredPrefix("What is Java?", "What is Java?") == "What is Java?",
              "the same question with nothing after it is left alone");

        // A tail that asks about something new is its own turn, even after "and"
        Check(AutoTurnRules.AsksItsOwnQuestion("And what is a memory leak?"), "'And what is a memory leak' is a new question");
        Check(AutoTurnRules.AsksItsOwnQuestion("what is garbage collection"), "a bare new question is a new question");
        Check(AutoTurnRules.AsksItsOwnQuestion("So how does a thread pool work?"), "'So how does...' is a new question");
        Check(!AutoTurnRules.AsksItsOwnQuestion("and where have you used it?"), "pointing back at it is an addition");
        Check(!AutoTurnRules.AsksItsOwnQuestion("with an example from Spring"), "an added constraint is an addition");
        Check(!AutoTurnRules.AsksItsOwnQuestion("or full time"), "a list of options is an addition");
        Check(!AutoTurnRules.AsksItsOwnQuestion("and that one too"), "'that one' points back");

        // The second half of one question with a long pause in the middle (soak round 12, 2026-10-06): the subject comes
        // before the verb, which only a clause inside a sentence can do. A new question puts the verb first.
        Check(!AutoTurnRules.AsksItsOwnQuestion("and what alerts you would set up?"), "'and what alerts you would set up' is the rest of the sentence");
        Check(!AutoTurnRules.AsksItsOwnQuestion("And how you would roll it back"), "'how you would roll it back' is the rest of the sentence");
        Check(!AutoTurnRules.AsksItsOwnQuestion("and what kind of alerts you would set up"), "a longer noun phrase before the subject is still the rest of the sentence");
        Check(!AutoTurnRules.AsksItsOwnQuestion("and how you'd handle failures"), "'you'd' counts as a subject");
        Check(!AutoTurnRules.AsksItsOwnQuestion("and what your team would use"), "'your team' counts as a subject");
        Check(AutoTurnRules.AsksItsOwnQuestion("and what alerts would you set up?"), "the same words with the verb first are a new question");
        Check(AutoTurnRules.AsksItsOwnQuestion("And how do you handle conflict?"), "'how do you handle conflict' is a new question");
        Check(AutoTurnRules.AsksItsOwnQuestion("and what would you do about it in production") == false,
            "pointing back at 'it' still counts as an addition (unchanged)");
        Check(AutoTurnRules.AsksItsOwnQuestion("and what can you tell me about caching?"), "'what can you tell me' is a new question");

        // A request's opening is not the request
        Check(AutoTurnRules.IsBareRequestOpener("Can you tell me"), "'Can you tell me' waits for the rest");
        Check(AutoTurnRules.IsBareRequestOpener("Could you walk me through"), "'Could you walk me through' waits");
        Check(AutoTurnRules.IsBareRequestOpener("Tell me about"), "'Tell me about' waits");
        Check(!AutoTurnRules.IsBareRequestOpener("Can you tell me about yourself?"), "'Can you tell me about yourself' is a whole question");
        Check(!AutoTurnRules.IsBareRequestOpener("Tell me about Kafka"), "'Tell me about Kafka' is a whole question");

        // Deepgram's explicit utterance boundary recovers questions whose punctuation or
        // opening was imperfect, but never acknowledgements, noise, or half a request.
        Check(AutoTurnRules.IsSubstantiveBoundaryUtterance("Your experience with Kubernetes"),
              "an explicit boundary can recover a noun-phrase interview question");
        Check(AutoTurnRules.IsSubstantiveBoundaryUtterance("I'd like to hear about the migration"),
              "an explicit boundary can recover a polite declarative request");
        Check(!AutoTurnRules.IsSubstantiveBoundaryUtterance("Okay, thank you."),
              "an acknowledgement is not a question at an utterance boundary");
        Check(!AutoTurnRules.IsSubstantiveBoundaryUtterance("Can you tell me"),
              "a boundary does not submit a bare request opener");
        Check(!AutoTurnRules.IsSubstantiveBoundaryUtterance("Capital m o t o g p f"),
              "a boundary does not submit spelled-out noise");

        // Reading the answer aloud versus asking something new
        DateTime submitted = DateTime.UtcNow;
        foreach (double gap in new[] { 3.0, 4.0, 5.0, 7.0 })
        {
            Check(AutoTurnRules.CanContinueAfterPause(
                "and explain how you tested it under load in production", submitted, submitted.AddSeconds(gap)),
                $"long linked clause survives {gap}s arrival gap");
        }

        // Replay repeated turns through the actual cumulative-transcript rule.
        // Each answer consumes exactly its snapshot; incoming speech remains
        // available, and polling unchanged snapshots never creates another turn.
        string cumulative = "";
        string consumed = "";
        bool replayPassed = true;
        for (int turn = 0; turn < 50; turn++)
        {
            string next = $"Explain scenario {turn}.";
            cumulative = (cumulative + " " + next).Trim();
            replayPassed &= AutoTurnRules.UnconsumedTranscript(cumulative, consumed) == next;
            consumed = cumulative;
            replayPassed &= AutoTurnRules.UnconsumedTranscript(cumulative, consumed) == "";
            cumulative += " And give an example with the failure handling included.";
            replayPassed &= AutoTurnRules.UnconsumedTranscript(cumulative, consumed) ==
                "And give an example with the failure handling included.";
            consumed = cumulative;
        }
        Check(replayPassed, "50 successive turns preserve arriving extensions without duplicate replay");
        Check(AutoTurnRules.CanContinueAfterPause("and where have you used it in your last project?", submitted, submitted.AddSeconds(5)),
              "linked extension survives four seconds of silence plus recognition delay");
        Check(!AutoTurnRules.CanContinueAfterPause("And what is Kubernetes?", submitted, submitted.AddSeconds(5)),
              "a separate topic after a pause is not merged");
        Check(!AutoTurnRules.CanContinueAfterPause("with an example", submitted, submitted.AddSeconds(20)),
              "old questions do not absorb unrelated later fragments");
        Check(AutoTurnRules.UnconsumedTranscript("What is Java?", "What is Java?") == "",
              "continuous capture does not resubmit the answered question");
        Check(AutoTurnRules.UnconsumedTranscript("What is Java? And where have you used it?", "What is Java?") == "And where have you used it?",
              "speech captured during answer generation survives for the next turn");

        // Reading the answer aloud versus asking something new
        const string restAnswer = "A RESTful API is an HTTP based interface that follows the principles of Representational State Transfer. " +
            "Each resource is identified by a URL, you use standard verbs like GET, POST, PUT and DELETE to operate on those resources, " +
            "and the server is stateless between calls. In my work I have built services that expose risk analytics endpoints behind a Flask layer, " +
            "containerized them with Docker, and deployed them on Kubernetes for the team to use in the future.";
        Check(AutoTurnRules.MeaningfulShareOnScreen("Are you authorized to work in the US, and will you need sponsorship in the future?", restAnswer) < 0.55,
              "a work authorization question is not reading the REST answer back");
        Check(AutoTurnRules.MeaningfulShareOnScreen("A RESTful API is an HTTP based interface that follows the principles of Representational State Transfer", restAnswer) >= 0.55,
              "reading the answer aloud is still recognized");

        // Follow-ups about the answer on screen, from a tester's Practice session
        // (2026-09-28). Each shares most of its words with the answer and was
        // ignored as "reading back", leaving Auto silent for minutes.
        const string qcAnswer = "I'm currently the QC Lab Supervisor at Ava Inc., where I lead day-to-day operations of the quality control lab, " +
            "overseeing assay and impurity method development, validation, and troubleshooting on Agilent HPLC and GC systems. " +
            "Before this, I built a solid foundation as a Research Associate at Baxter Pharmaceuticals and as a QC Analyst at Neuland Laboratories. " +
            "MORE TO SAY - How I prioritize multiple concurrent tasks while maintaining strict documentation standards.";
        foreach (var followUp in new[]
        {
            "Can you tell me more about your experience as a QC lab supervisor at Ava?",
            "Why did you move from Baxter Pharmaceuticals to Ava Inc?",
            "Tell me about a root cause analysis you led for an assay discrepancy.",
            "How did you handle method development and validation at Ava?",
        })
        {

            Check(AutoTurnRules.IsQuestionToTheCandidate(followUp), $"follow-up is a question, not read-back: {followUp}");
        }
        foreach (var readBack in new[]
        {
            "I'm currently the QC lab supervisor at Ava where I lead day to day operations of the quality control lab",
            "How I prioritize multiple concurrent tasks while maintaining strict documentation standards.",
            "Before this I built a solid foundation as a research associate at Baxter Pharmaceuticals",
            "So my role at Ava is overseeing assay and impurity method development, do you see",
        })
            Check(!AutoTurnRules.IsQuestionToTheCandidate(readBack), $"reading our answer aloud stays a read-back: {readBack}");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "auto turns: all passed" : $"auto turns: {failed} FAILED");
        return failed;
    }
}
