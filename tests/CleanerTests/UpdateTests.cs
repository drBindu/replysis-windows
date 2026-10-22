using System.IO;
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
        Check(UpdatePolicy.RecheckEvery <= TimeSpan.FromMinutes(15) && UpdatePolicy.RecheckEvery >= TimeSpan.FromMinutes(5),
            "an open app looks again every 5 to 15 minutes (GitHub allows an address 60 checks an hour)");
        Check(UpdatePolicy.FirstCheck <= TimeSpan.FromSeconds(30), "the first look is soon after opening");
        if (Environment.GetEnvironmentVariable("REPLYSIS_LIVE_NET_TEST") == "1")
        {
            // The real check, against the real latest release, without the GitHub API (2026-10-22).
            string? found = SettingsWindow.GetNewerVersionOrNullAsync().GetAwaiter().GetResult();
            Check(found != null, $"the live check finds the newest release from the feed file ({found ?? "nothing"})");
            // The installer's own updater, as a copy installed at 1.0.40 would run it, through the feed file.
            string pk = Path.Combine(Path.GetTempPath(), "replysis-velopack-test");
            Directory.CreateDirectory(pk);
            var mgr = new Velopack.UpdateManager(new Velopack.Sources.SimpleWebSource(InterviewCopilot.UpdateService.LatestFeedUrl), null,
                new Velopack.Locators.TestVelopackLocator("Replysis", "1.0.40", pk, null));
            var info = mgr.CheckForUpdatesAsync().GetAwaiter().GetResult();
            Check(info != null, $"Velopack on 1.0.40 sees the update through the feed file ({info?.TargetFullRelease.Version.ToString() ?? "nothing"})");
        }
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
