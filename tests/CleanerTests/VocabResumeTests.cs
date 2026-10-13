using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// The speech dictionary is built from the resume the person has, including the one restored at launch (found in a real log: it was
/// built 1.4 s before the restore, so a returning person's names and tools were left out for the whole session).
/// </summary>
internal static class VocabResumeTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        int reads = 0;
        string Saved() { reads++; return "Senior engineer. Kubernetes, Kafka, Redis."; }

        Check(VocabResume.Choose("My resume text", false, Saved) == "My resume text" && reads == 0,
              "a resume in the box is used, and the saved one is not even read");
        Check(VocabResume.Choose("", false, Saved).Contains("Kubernetes"),
              "an empty box before the startup restore falls back to the saved resume");
        Check(VocabResume.Choose("   ", false, Saved).Contains("Kafka"),
              "so does a box with only spaces");
        reads = 0;
        Check(VocabResume.Choose("", true, Saved) == "" && reads == 0,
              "after the restore an empty box is respected: a cleared resume gives no old terms");
        Check(VocabResume.Choose(null, false, () => throw new InvalidOperationException("could not decrypt")) == "",
              "a saved resume that cannot be read gives an empty dictionary, not a crash");
        Check(VocabResume.Choose("", false, () => null!) == "", "a missing saved resume gives an empty dictionary");

        Console.WriteLine(failed == 0 ? "\nvocabulary resume source: all passed" : $"\nvocabulary resume source: {failed} FAILED");
        return failed;
    }
}
