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

        // A small test upload decides whether pictures go ahead at all, so a slow line is found out for the price of
        // 64 KB and not by losing a whole picture in front of the first question (2026-10-06: 9.8 s to the first word).
        var fresh = new UplinkGovernor();
        Check(!fresh.Verified && fresh.NeedsProbe(t0), "a new start knows nothing about the line, so it tests it first");

        quiet = fresh.RecordProbe(t0, true, TimeSpan.FromMilliseconds(180));
        Check(quiet == TimeSpan.Zero && fresh.Verified && !fresh.NeedsProbe(t0) && fresh.MayUpload(t0),
            "a test that is back within 0.7 s trusts the line");

        var hotspot = new UplinkGovernor();
        // 64 KB at 30 KB a second is about two seconds.
        quiet = hotspot.RecordProbe(t0, true, TimeSpan.FromSeconds(2.1));
        Check(quiet == TimeSpan.FromSeconds(60) && !hotspot.Verified && !hotspot.NeedsProbe(t0.AddSeconds(59)) && hotspot.NeedsProbe(t0.AddSeconds(60)),
            "the hotspot fails the test, no picture is sent, and the line is tested again a minute later");
        quiet = hotspot.RecordProbe(t0.AddSeconds(60), false, UplinkGovernor.ProbeTimeout);
        Check(quiet == TimeSpan.FromSeconds(120), "a test that does not finish doubles the pause, the same as a picture");
        Check(UplinkGovernor.ProbeBytes * 6 < 500 * 1024,
            "a test costs a sixth of a picture or less, so testing a slow line now and then does not clog it");

        // A line that was fine and then loses a picture is in doubt again, and the way back is a test, not another picture.
        var wobble = new UplinkGovernor();
        wobble.RecordProbe(t0, true, TimeSpan.FromMilliseconds(150));
        wobble.Record(t0.AddMinutes(5), false, UplinkGovernor.UploadTimeout);
        Check(!wobble.Verified && !wobble.NeedsProbe(t0.AddMinutes(5)) && wobble.NeedsProbe(t0.AddMinutes(6)),
            "after a lost picture the line is in doubt and gets a small test when the pause ends");
        wobble.RecordProbe(t0.AddMinutes(6), true, TimeSpan.FromMilliseconds(200));
        Check(wobble.Verified && wobble.FailureStreak == 0, "and one good test brings pictures back");

        wobble.Reset();
        Check(!wobble.Verified && wobble.NeedsProbe(t0), "a changed network is tested again from scratch");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "uplink governor: all passed" : $"uplink governor: {failed} FAILED");
        return failed;
    }
}
