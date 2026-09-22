using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// Going back to an answer that was taken away.
///
/// The case this was built for, in the owner's words: the answer changes, or it
/// moves to the next question because of a random voice, and the candidate
/// wants the previous answer at that instant. These cases pin that it is always
/// reachable, that it is never more than a keystroke away, and above all that a
/// new answer arriving does not snap the screen away from someone who is
/// reading an older one - which would recreate the exact problem, one second
/// later.
/// </summary>
internal static class AnswerHistoryTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        // Nothing yet
        var h = new AnswerHistory();
        Check(h.Count == 0 && h.Current == null, "an empty session has nothing to go back to");
        Check(!h.CanGoBack && !h.CanGoForward, "neither arrow works before the first answer");
        Check(h.Back() == null && h.Forward() == null, "pressing either key does nothing, and does not throw");
        Check(h.Label() == "", "no counter until there is a second answer to move between");

        // Ordinary use: each answer replaces the last
        h.Append("What is Java?", "Java is a language...");
        Check(h.Count == 1 && h.Current?.Answer.StartsWith("Java is") == true, "the first answer is on screen");
        Check(h.Label() == "", "one answer still shows no counter");
        Check(h.Append("What is a thread?", "A thread is..."), "a second answer takes the screen when nobody is browsing");
        Check(h.Current?.Question == "What is a thread?", "and it is the one being shown");
        Check(h.Label() == "2 of 2", "the counter reads position and total");

        // The case the feature exists for
        var back = h.Back();
        Check(back?.Question == "What is Java?", "Ctrl+Alt+Left returns the answer that was taken away");
        Check(!h.IsFollowingNewest, "stepping back hands the screen to the user");
        Check(h.Label() == "1 of 2", "the counter follows them back");

        // A stray voice answers a question nobody asked, mid-read
        bool tookScreen = h.Append("Anything else?", "An answer to nobody's question");
        Check(!tookScreen, "an answer arriving while they read an older one does NOT take the screen");
        Check(h.Current?.Question == "What is Java?", "they are still reading what they were reading");
        Check(h.NewerWaiting == 2, "but they are told how many arrived while they were away");
        Check(h.Count == 3, "and nothing was thrown away");

        // Getting back to the present
        var fwd = h.Forward();
        Check(fwd?.Question == "What is a thread?", "forward steps one at a time");
        Check(!h.IsFollowingNewest, "still theirs, because there is something newer");
        Check(h.NewerWaiting == 1, "one still waiting");
        h.Forward();
        Check(h.IsFollowingNewest, "arriving at the newest resumes following by itself");
        Check(h.NewerWaiting == 0, "and the waiting count clears");
        Check(h.Forward() == null, "forward from the newest does nothing");

        // The counter itself is the way out
        h.Back(); h.Back();
        Check(h.Position == 1 && !h.IsFollowingNewest, "two steps back");
        var jumped = h.JumpToNewest();
        Check(jumped?.Question == "Anything else?" && h.IsFollowingNewest,
              "pressing the counter returns to the newest in one action");

        // Following resumes properly after the jump
        h.Append("Next question", "Next answer");
        Check(h.Current?.Answer == "Next answer", "and the next answer appears by itself again");

        // Nothing empty is ever kept
        int before = h.Count;
        Check(!h.Append("q", "   "), "a blank answer is not recorded");
        Check(h.Count == before, "and does not move the counter");

        // The cap, and what it must not do to someone mid-read
        var big = new AnswerHistory();
        for (int i = 1; i <= AnswerHistory.MaxEntries + 5; i++) big.Append($"q{i}", $"a{i}");
        Check(big.Count == AnswerHistory.MaxEntries, "the history stops growing at the cap");
        Check(big.Current?.Answer == $"a{AnswerHistory.MaxEntries + 5}", "the newest is still the newest");

        var capped = new AnswerHistory();
        for (int i = 1; i <= AnswerHistory.MaxEntries; i++) capped.Append($"q{i}", $"a{i}");
        capped.Back(); capped.Back();                 // reading a{58}
        string beingRead = capped.Current!.Value.Answer;
        capped.Append("new", "new answer");           // pushes the oldest out
        Check(capped.Current?.Answer == beingRead,
              "dropping the oldest does not slide the screen onto a different answer");

        // Clearing the conversation clears what the arrows reach
        h.Clear();
        Check(h.Count == 0 && h.Current == null && h.IsFollowingNewest,
              "clearing the conversation leaves no answers to step back to");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "answer history: all passed" : $"answer history: {failed} FAILED");
        return failed;
    }
}
