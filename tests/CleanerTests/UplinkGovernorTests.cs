using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// A screenshot sent ahead must never hold up the question or the speech connection (2026-10-01: on a hotspot
/// uploading 20 to 50 KB a second, a 500 KB picture every 16 seconds never finished and kept the connection
/// full, so answers waited 4 to 9 seconds and the speech link dropped every minute).
/// </summary>
internal static class UplinkGovernorTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        var t0 = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

        var g = new UplinkGovernor();
        Check(g.MayUpload(t0), "a fresh start may send ahead");

        var quiet = g.Record(t0, true, TimeSpan.FromMilliseconds(700));
        Check(quiet == TimeSpan.Zero && g.MayUpload(t0.AddSeconds(1)) && g.FailureStreak == 0, "a quick upload is trusted and changes nothing");

        quiet = g.Record(t0, false, UplinkGovernor.UploadTimeout);
        Check(quiet == TimeSpan.FromSeconds(60) && !g.MayUpload(t0.AddSeconds(59)) && g.MayUpload(t0.AddSeconds(60)),
            "one failed upload pauses sending ahead for a minute");

        quiet = g.Record(t0.AddSeconds(60), false, UplinkGovernor.UploadTimeout);
        Check(quiet == TimeSpan.FromSeconds(120), "failing again doubles the pause");
        quiet = g.Record(t0.AddSeconds(200), false, UplinkGovernor.UploadTimeout);
        Check(quiet == TimeSpan.FromSeconds(240), "and again");

        for (int i = 0; i < 12; i++) quiet = g.Record(t0.AddHours(1), false, UplinkGovernor.UploadTimeout);
        Check(quiet == UplinkGovernor.MaxBackoff, "the pause never grows past ten minutes, so a recovered connection is found again");

        quiet = g.Record(t0.AddHours(2), true, TimeSpan.FromMilliseconds(900));
        Check(quiet == TimeSpan.Zero && g.FailureStreak == 0 && g.MayUpload(t0.AddHours(2)),
            "one quick upload after a bad patch trusts the connection again");

        var slow = new UplinkGovernor();
        quiet = slow.Record(t0, true, TimeSpan.FromSeconds(9));
        Check(quiet > TimeSpan.Zero, "an upload that works but takes nine seconds is not 'ahead' of anything, so it counts as slow");

        Check(UplinkGovernor.UploadTimeout < TimeSpan.FromSeconds(15) && UplinkGovernor.UploadTimeout > UplinkGovernor.MaxUsefulUpload,
            "a doomed upload is given up on sooner than the client's own fifteen seconds");

        // The numbers from the real hotspot: about 30 KB a second, 500 KB picture.
        double seconds = 500.0 / 30.0;
        Check(seconds > UplinkGovernor.MaxUsefulUpload.TotalSeconds, "on that connection a screenshot takes longer than is useful, so it is paused");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "uplink governor: all passed" : $"uplink governor: {failed} FAILED");
        return failed;
    }
}
