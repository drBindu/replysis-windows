using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// An answer stops on its last point. The model sometimes ends by handing the conversation back ("Let me know if you'd
/// like more detail", "Would you like me to go deeper?", a question to the interviewer), and the candidate reads that
/// out loud (owner, 2026-10-06: "it is asking a reverse question"). It comes off the end; advice that merely
/// contains the same words, code, and a one-sentence answer never do.
/// </summary>
internal static class AnswerClosersTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }
        string Strip(string s, bool allowQuestion = false) => MainWindow.CleanAiOutput(s, allowQuestion);

        const string body = "I moved the nightly jobs to a queue. That cut the failures by half. ";

        Check(Strip(body + "Let me know if you'd like more detail.") == "I moved the nightly jobs to a queue. That cut the failures by half.",
            "\"Let me know if you'd like more detail\" comes off");
        Check(Strip(body + "Would you like me to go deeper?") == "I moved the nightly jobs to a queue. That cut the failures by half.",
            "\"Would you like me to go deeper?\" comes off");
        Check(Strip(body + "Does that make sense?") == "I moved the nightly jobs to a queue. That cut the failures by half.",
            "\"Does that make sense?\" comes off");
        Check(Strip(body + "Happy to elaborate on any of that. Feel free to ask.") == "I moved the nightly jobs to a queue. That cut the failures by half.",
            "two offers in a row both come off");
        Check(Strip(body + "I can also walk you through the design if that helps.") == "I moved the nightly jobs to a queue. That cut the failures by half.",
            "\"I can also walk you through...\" comes off");

        // A question handed back to the interviewer.
        Check(Strip(body + "What does your team use today?") == "I moved the nightly jobs to a queue. That cut the failures by half.",
            "a question put to the interviewer comes off");
        Check(Strip(body + "What does your team use today?", allowQuestion: true).EndsWith("What does your team use today?"),
            "but it stays when the interviewer has just invited the candidate's questions");
        Check(!Strip(body + "Happy to go deeper.", allowQuestion: true).Contains("Happy to"),
            "an offer to say more comes off even then");

        // Streaming: the closer is hidden as it arrives, not shown and then removed.
        Check(Strip(body + "Let me know if") == "I moved the nightly jobs to a queue. That cut the failures by half.",
            "half a closing offer is already hidden while it is still arriving");

        // Never touched.
        Check(Strip("If you want fast lookups, use a hash map. If you need ordering, use a tree.") ==
              "If you want fast lookups, use a hash map. If you need ordering, use a tree.",
            "advice that starts with \"if you want\" is content, not an offer");
        Check(Strip("Would you like me to go deeper?") == "Would you like me to go deeper?",
            "the only sentence of an answer is never removed");
        Check(Strip(body + "I used it daily at Contoso.") == body.Trim() + " I used it daily at Contoso.",
            "an ordinary last sentence stays");
        Check(Strip("A mutex guards one resource. A semaphore allows N holders. Is a semaphore always better? No, a mutex is simpler and safer.") ==
              "A mutex guards one resource. A semaphore allows N holders. Is a semaphore always better? No, a mutex is simpler and safer.",
            "a question in the middle of an answer stays; only the END is tidied");

        // Code.
        string code = "Here is the loop.\n\n```python\nfor x in items:\n    print(x)\n```";
        Check(Strip(code) == code, "an answer ending in code is left exactly as it is");
        string openCode = "Here is the loop.\n\n```python\nfor x in items:\n    # Does that make sense?";
        Check(Strip(openCode) == openCode.Trim(), "code that is still arriving is never touched");
        string afterCode = code + "\n\nThat is O(n). Let me know if you want the recursive version.";
        Check(Strip(afterCode) == code + "\n\nThat is O(n).", "an offer after the code comes off, the code does not move");

        // The bullets under MORE TO SAY.
        string more = body + "Does that help?\n\nMORE TO SAY\n• I added retries with backoff.\n• Let me know if you want more.";
        string cleaned = Strip(more);
        Check(!cleaned.Contains("Does that help") && !cleaned.Contains("Let me know") &&
              cleaned.Contains("MORE TO SAY") && cleaned.Contains("I added retries with backoff."),
            "offers come off the spoken part and off the last bullet, the real bullets stay");

        // The invitation flow itself still works: when the interviewer asks for questions, the answer is one.
        Check(PromptBuilder.IsCandidateQuestionInvitation("Do you have any questions for me?"),
            "\"do you have any questions\" is recognised as an invitation, so a closing question is allowed there");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "answer closers: all passed" : $"answer closers: {failed} FAILED");
        return failed;
    }
}
