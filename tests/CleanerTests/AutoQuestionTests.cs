using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// What Auto decides is a question, and what it sends, for the way people
/// actually talk in interviews and the way Deepgram actually punctuates them.
///
/// From the 2026-09-28 Auto audit, after a tester reported answers arriving
/// minutes late. Each case below was replayed through the shipping rules first
/// and failed before the fix: an acknowledgement in front of the question hid
/// it, a question split at a 300ms pause was sent without its first half, and
/// an answered question could come back when one word of it was revised.
/// </summary>
internal static class AutoQuestionTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }
        string N(string s) => PromptBuilder.NormalizeInterviewerQuestion(s);
        bool Q(string s) => AutoTurnRules.IsLikelyCompleteQuestion(N(s));

        // Acknowledgement, then the question
        foreach (var said in new[]
        {
            "Got it. So walk me through your resume.",
            "Great, thanks for that. That's really helpful. Now, walk me through your most recent project.",
            "Alright. So let's talk about your experience with Kubernetes.",
            "That makes sense. I'd like to hear about your biggest achievement.",
            "Okay, cool. And what about your salary expectations?",
            "I'm curious about the migration project you mentioned.",
            "Let's move on to system design. Design a URL shortener.",
            "Talk to me about your strengths and weaknesses.",
        })
            Check(Q(said), $"answered: {said}");
        Check(!N("Got it. So walk me through your resume.").StartsWith("Got it"),
            "the acknowledgement is not sent as part of the question");

        // Split at a pause: the whole question is sent
        Check(N("What is the. Difference between an abstract class and an interface?")
                .StartsWith("What is the Difference"), "a question split after 'the' is rejoined");
        Check(N("Can you walk me. Through your last project?").StartsWith("Can you walk me"),
            "a question split after 'walk me' is rejoined");
        Check(N("Tell me about a time. You disagreed with your manager?").StartsWith("Tell me about a time"),
            "'Tell me about a time.' is not dropped from a behavioural question");
        Check(N("Tell me about yourself. What are your strengths?") == "What are your strengths?",
            "two separate questions are not glued together");

        // The sentence a question points back to stays with it
        Check(N("Your resume says you led a team of six. Walk me through that.") ==
              "Your resume says you led a team of six. Walk me through that.",
            "context kept for 'walk me through that', with its full stop");
        Check(Q("Your resume says you led a team of six. Walk me through that."),
            "a question after one context sentence is answered");
        Check(N("What is Python? And where have you used it?") == "What is Python? And where have you used it?",
            "'and where have you used it' keeps the question 'it' refers to");

        // Not questions, however they end
        foreach (var said in new[]
        {
            "Okay.", "Great, thanks for that.", "That makes sense.", "Mm hmm. Yeah. Okay.",
            "Let me pull up my notes.", "One second, my dog is barking.", "Bear with me.",
        })
        {
            Check(!Q(said), $"not answered: {said}");
            Check(!AutoTurnRules.IsSubstantiveBoundaryUtterance(N(said)),
                $"end-of-speech signal does not make it a question: {said}");
        }
        Check(AutoTurnRules.IsSubstantiveBoundaryUtterance("Your experience with Kubernetes"),
            "a recruiter prompt without a question word still counts at an utterance end");

        // Behavioural openers are not a whole question
        Check(AutoTurnRules.IsBareRequestOpener("Tell me about a time"), "'Tell me about a time' waits for the rest");
        Check(AutoTurnRules.IsBareRequestOpener("Can you tell me about a time when"), "'...a time when' waits for the rest");
        Check(!AutoTurnRules.IsBareRequestOpener("Tell me about a time you failed"), "a full behavioural question is not an opener");
        Check(AutoTurnRules.EndsOnJoiningWord("My next question is"), "a question still being spoken is recognised");

        // What was said after the answered question, when the recogniser revised it
        Check(AutoTurnRules.UnconsumedTranscript(
                  "Tell me about the RESTful services? And what about SOAP?",
                  "Tell me about the rest full services") == "And what about SOAP?",
            "a revised word in the answered question does not bring it back");
        Check(AutoTurnRules.UnconsumedTranscript(
                  "Tell me about the RESTful? And what about SOAP?",
                  "Tell me about the rest full") == "And what about SOAP?",
            "a revision that changes the word count still cuts at the question mark");
        Check(AutoTurnRules.UnconsumedTranscript(
                  "Tell me about yourself. Why this company? How do you handle pressure?",
                  "Tell me about yourself. Why this company") == "How do you handle pressure?",
            "earlier questions in a long Auto transcript stay answered");
        Check(AutoTurnRules.UnconsumedTranscript("Hello there", "Tell me about yourself and your background please") ==
              "Hello there", "a fresh transcript after an engine restart is returned whole");
        Check(AutoTurnRules.UnconsumedTranscript("Tell me about the RESTful", "Tell me about the rest full") == "",
            "nothing new yet means nothing new");

        // From the owner's live Practice test (2026-09-28): a film was playing on
        // the computer, and "So what is Java?" got a textbook answer.
        Check(N("So what is Java?") == "What is Java?", "'So' is dropped, so the definition voice applies");
        Check(N("Okay so, tell me about yourself.") == "Tell me about yourself.", "'Okay so,' is dropped");
        Check(N("And where have you used it?").StartsWith("And"), "'And' stays, continuation merging reads it");
        foreach (var line in new[]
        {
            "Two days of counting my fingers.",
            "The dreams just keep getting worse. Every time I close my eyes.",
            "Everyone's safe for themselves.",
            "I have been at the company for five years.",
        })
        {
            Check(!Q(line), $"not answered as a question: {line}");
            Check(!AutoTurnRules.IsSubstantiveBoundaryUtterance(line), $"not answered at an utterance end: {line}");
        }
        Check(Q("I see you led the migration at Amazon."), "a statement aimed at the candidate is still a prompt");
        Check(Q("Your resume mentions Kafka and Spark."), "a statement about their resume is still a prompt");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "auto questions: all passed" : $"auto questions: {failed} FAILED");
        return failed;
    }
}
