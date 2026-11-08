using System.Linq;
using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// A question over the prompt's size cap keeps its end, where the actual question is (bug round 47). A long brief
/// read out by the interviewer used to lose its last sentence, the question itself.
/// </summary>
internal static class PromptTruncationTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        Check(PromptBuilder.TruncateKeepingEnd("What is Java?", 4000) == "What is Java?", "a short question is unchanged");

        string brief = string.Concat(Enumerable.Repeat("The service takes orders from many regions and stores them for later. ", 80));
        string asked = brief + "So design the backend for that.";
        string kept = PromptBuilder.TruncateKeepingEnd(asked, 4000);
        Check(kept.EndsWith("So design the backend for that."), "the question at the end is kept");
        Check(kept.StartsWith("[earlier part cut]\nThe ") || kept.StartsWith("[earlier part cut]\n"), "the cut is marked");
        Check(kept.Length <= 4000 + "[earlier part cut]\n".Length, "the kept part stays within the cap");

        var messages = PromptBuilder.BuildMessages("Worked at Acme.", asked);
        string last = System.Text.Json.JsonSerializer.Serialize(messages[^1]);
        Check(last.Contains("So design the backend for that."), "the prompt sent carries the question at the end");

        return failed;
    }
}
