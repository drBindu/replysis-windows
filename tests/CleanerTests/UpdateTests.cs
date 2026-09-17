using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// The public repository changed from windows-v* tags to v* tags. If the
/// parser does not recognize both, installed builds silently stop finding
/// updates.
/// </summary>
internal static class UpdateTests
{
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
