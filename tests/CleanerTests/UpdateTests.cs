using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// The public repository changed from windows-v* tags to v* tags. If the
/// parser does not recognize both, installed builds silently stop finding
/// updates.
/// </summary>
internal static class UpdateTests
{
    internal static int RunPolicy()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        // A tester's laptop never showed the 1.0.28 notice: one check, twelve seconds after
        // opening, and a rule that hid it while an error notice was up.
        Check(UpdatePolicy.RecheckEvery <= TimeSpan.FromHours(2) && UpdatePolicy.RecheckEvery >= TimeSpan.FromMinutes(15),
            "an open app looks again, every 15 minutes to 2 hours (GitHub allows an address 60 checks an hour)");
        Check(UpdatePolicy.FirstCheck <= TimeSpan.FromSeconds(30), "the first look is soon after opening");
        Check(UpdatePolicy.ShouldAnnounce("1.0.28", null), "a new version is announced");
        Check(!UpdatePolicy.ShouldAnnounce("1.0.28", "1.0.28"), "the same version is not announced twice");
        Check(!UpdatePolicy.ShouldAnnounce("1.0.28", "1.0.28".ToUpperInvariant()), "case does not matter");
        Check(UpdatePolicy.ShouldAnnounce("1.0.29", "1.0.28"), "a still newer version is announced");
        Check(!UpdatePolicy.ShouldAnnounce(null, null) && !UpdatePolicy.ShouldAnnounce("", null) && !UpdatePolicy.ShouldAnnounce("  ", "x"),
            "nothing found means nothing announced");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "update policy: all passed" : $"update policy: {failed} FAILED");
        return failed;
    }

    internal static int Run()
    {
        int failed = 0;

        void Check(bool ok, string label, string? detail = null)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok)
            {
                failed++;
                if (detail != null) Console.WriteLine($"        {detail}");
            }
        }

        string? current = SettingsWindow.ParseWindowsReleaseTag("v1.0.19");
        Check(current == "1.0.19", "current v* release tag is recognized", current);

        string? legacy = SettingsWindow.ParseWindowsReleaseTag("windows-v1.0.11.0");
        Check(legacy == "1.0.11.0", "legacy Windows release tag remains recognized", legacy);

        Check(SettingsWindow.ParseWindowsReleaseTag("mac-v1.0.19") == null,
            "unrelated platform tag is rejected");
        Check(SettingsWindow.ParseWindowsReleaseTag("vnot-a-version") == null,
            "malformed release tag is rejected");
        Check(SettingsWindow.ParseWindowsReleaseTag(null) == null,
            "missing release tag is rejected");

        return failed;
    }
}
