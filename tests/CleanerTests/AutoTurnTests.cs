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

        // A request's opening is not the request
        Check(AutoTurnRules.IsBareRequestOpener("Can you tell me"), "'Can you tell me' waits for the rest");
        Check(AutoTurnRules.IsBareRequestOpener("Could you walk me through"), "'Could you walk me through' waits");
        Check(AutoTurnRules.IsBareRequestOpener("Tell me about"), "'Tell me about' waits");
        Check(!AutoTurnRules.IsBareRequestOpener("Can you tell me about yourself?"), "'Can you tell me about yourself' is a whole question");
        Check(!AutoTurnRules.IsBareRequestOpener("Tell me about Kafka"), "'Tell me about Kafka' is a whole question");

        // Reading the answer aloud versus asking something new
        const string restAnswer = "A RESTful API is an HTTP based interface that follows the principles of Representational State Transfer. " +
            "Each resource is identified by a URL, you use standard verbs like GET, POST, PUT and DELETE to operate on those resources, " +
            "and the server is stateless between calls. In my work I have built services that expose risk analytics endpoints behind a Flask layer, " +
            "containerized them with Docker, and deployed them on Kubernetes for the team to use in the future.";
        Check(AutoTurnRules.MeaningfulShareOnScreen("Are you authorized to work in the US, and will you need sponsorship in the future?", restAnswer) < 0.55,
              "a work authorization question is not reading the REST answer back");
        Check(AutoTurnRules.MeaningfulShareOnScreen("A RESTful API is an HTTP based interface that follows the principles of Representational State Transfer", restAnswer) >= 0.55,
              "reading the answer aloud is still recognized");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "auto turns: all passed" : $"auto turns: {failed} FAILED");
        return failed;
    }
}
