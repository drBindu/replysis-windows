using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// A screen with nothing to answer shows one plain sentence, not an invented task and a line nobody could say aloud.
/// </summary>
internal static class NothingAskedTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        string chat = ScreenAnalyzer.RewriteNothingAsked("NOTHING ASKED\nA chat with an AI assistant about testing the app.");
        Check(chat.StartsWith("No question on this screen."), "a screen with nothing asked says so first");
        Check(chat.Contains("It shows: A chat with an AI assistant about testing the app."), "and says what is on it, without a doubled full stop");
        Check(chat.Contains("Press F8 when a question or code is showing."), "and says what to do next");
        Check(!chat.Contains("NOTHING ASKED") && !chat.Contains("ignore", StringComparison.OrdinalIgnoreCase), "the heading and any telling off are gone");

        Check(ScreenAnalyzer.RewriteNothingAsked("nothing asked") .StartsWith("No question on this screen."), "the heading is matched in any case");
        Check(ScreenAnalyzer.RewriteNothingAsked("NOTHING ASKED\n").Contains("Press F8"), "a heading with no line still gives the next step");

        string coding = "APPROACH\nTwo pointers.\nSAY THIS\nI would use two pointers.";
        Check(ScreenAnalyzer.RewriteNothingAsked(coding) == coding, "a real answer is never touched");
        Check(ScreenAnalyzer.RewriteNothingAsked("SAY THIS\nNothing asked of me here.") == "SAY THIS\nNothing asked of me here.", "the words appearing inside an answer do not trigger it");
        Check(ScreenAnalyzer.RewriteNothingAsked("") == "" && ScreenAnalyzer.RewriteNothingAsked(null!) == null, "empty input is left alone");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "nothing asked: all passed" : $"nothing asked: {failed} FAILED");
        return failed;
    }
}
