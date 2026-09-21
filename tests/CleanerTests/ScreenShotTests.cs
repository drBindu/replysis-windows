using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// A prepared screenshot id must never outlive the picture behind it.
///
/// The app skips the upload while the screen has not changed, which is right,
/// but it also reset its own clock each time it skipped. The id then looked
/// fresh forever while the server deleted the image after ninety seconds, so a
/// question asked two minutes into the same problem statement referenced an
/// image that was already gone.
/// </summary>
internal static class ScreenShotTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        Check(ScreenShotRules.ShouldReuseUnchangedShot(TimeSpan.FromSeconds(5)),
              "a screenshot seconds old is reused, no upload");
        Check(ScreenShotRules.ShouldReuseUnchangedShot(TimeSpan.FromSeconds(44)),
              "just under the refresh age, still reused");
        Check(!ScreenShotRules.ShouldReuseUnchangedShot(TimeSpan.FromSeconds(46)),
              "past the refresh age, the same screen is uploaded again");
        Check(!ScreenShotRules.ShouldReuseUnchangedShot(TimeSpan.FromMinutes(3)),
              "a problem statement read for minutes gets a fresh id");

        Check(ScreenShotRules.RefreshUnchangedAfter < ScreenShotRules.IdMaxAge,
              "the refresh happens before the app stops offering the id");
        Check(ScreenShotRules.IdMaxAge < ScreenShotRules.ServerImageLifetime,
              "the app gives up on an id before the server deletes the image");
        Check(ScreenShotRules.ServerImageLifetime == TimeSpan.FromSeconds(90),
              "the server lifetime matches STASHED_IMAGE_TTL_MS");

        Check(ScreenShotRules.IdStillUsable(TimeSpan.FromSeconds(59)), "an id under a minute is sent with the question");
        Check(!ScreenShotRules.IdStillUsable(TimeSpan.FromSeconds(75)), "an older id is dropped rather than sent");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "screenshot ids: all passed" : $"screenshot ids: {failed} FAILED");
        return failed;
    }
}
