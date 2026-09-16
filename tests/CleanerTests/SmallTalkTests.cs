using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// Is this pleasantry, or a real question wearing one?
///
/// Two answers never reach the model: a greeting and small talk both return a
/// canned line. That is right for "hi" and for "how are you", and it is the
/// worst possible failure for anything else, because the candidate is not told
/// it happened. They hear a technical question, the panel hears "Doing really
/// well, thanks! Excited to be here", and the interview is over.
///
/// It shipped that way. The guard was a list of about twenty words - what, why,
/// java, python, code - and a question had to contain one of them to escape.
/// "How are you handling state in React?" contains none: it matched "how are
/// you", was under sixty characters, and got the chit-chat reply. So did "How
/// are you deploying to AWS?" and "Nice to meet you, shall we start with your
/// background?".
///
/// The rule now is subtractive rather than a word list: take the pleasantry and
/// the filler away, and if anything is left standing, a person asked something.
/// A list can only ever name the technologies somebody thought of; this asks
/// the question the code actually cares about.
/// </summary>
internal static class SmallTalkTests
{
    internal static int Run()
    {
        int failed = 0;

        void Case(string label, bool got, bool want)
        {
            bool ok = got == want;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) { failed++; Console.WriteLine($"        expected {want}, got {got}"); }
        }

        // ── Real questions that used to get the canned chit-chat reply ───────
        Case("how are you handling state in React",
            PromptBuilder.IsSmallTalk("How are you handling state in React?"), false);
        Case("how are you deploying to AWS",
            PromptBuilder.IsSmallTalk("How are you deploying to AWS?"), false);
        Case("how are you testing this",
            PromptBuilder.IsSmallTalk("How are you testing this?"), false);
        Case("nice to meet you, start with background",
            PromptBuilder.IsSmallTalk("Nice to meet you, shall we start with your background?"), false);
        Case("how have you been scaling the service",
            PromptBuilder.IsSmallTalk("How have you been scaling the service?"), false);

        // ── Genuine pleasantry, which must still skip the model ─────────────
        Case("how are you", PromptBuilder.IsSmallTalk("How are you?"), true);
        Case("how are you doing today",
            PromptBuilder.IsSmallTalk("How are you doing today?"), true);
        Case("how's it going", PromptBuilder.IsSmallTalk("How's it going?"), true);
        Case("hi, how are you", PromptBuilder.IsSmallTalk("Hi, how are you?"), true);
        Case("nice to meet you", PromptBuilder.IsSmallTalk("Nice to meet you."), true);
        Case("nice to meet you too, thanks",
            PromptBuilder.IsSmallTalk("Nice to meet you too, thanks!"), true);
        Case("thanks for coming in today",
            PromptBuilder.IsSmallTalk("Thanks for coming in today."), true);
        Case("how was your day", PromptBuilder.IsSmallTalk("How was your day?"), true);

        // ── Not small talk at all, and never was ────────────────────────────
        Case("tell me about yourself",
            PromptBuilder.IsSmallTalk("Tell me about yourself."), false);
        Case("what is a hash map",
            PromptBuilder.IsSmallTalk("What is a hash map?"), false);

        // ── Greetings stay tight: only a greeting is a greeting ─────────────
        Case("hi", PromptBuilder.IsGreeting("Hi"), true);
        Case("hello hello (repeat artifact)", PromptBuilder.IsGreeting("hello hello"), true);
        Case("good morning", PromptBuilder.IsGreeting("Good morning!"), true);
        Case("hi, what is dependency injection",
            PromptBuilder.IsGreeting("Hi, what is dependency injection?"), false);
        Case("hey, walk me through your project",
            PromptBuilder.IsGreeting("Hey, walk me through your project."), false);

        return failed;
    }
}
