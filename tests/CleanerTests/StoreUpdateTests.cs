using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// A Store update may only ever happen at launch.
///
/// The Microsoft Store closes the app to install a package. This app is open
/// during live job interviews, so the cost of getting this wrong is not a
/// reload, it is somebody's interview. The owner's rule, in his words: a user
/// who opened Replysis at 9 AM and is still in it at 8 PM is left completely
/// alone, whatever was published at lunchtime.
///
/// These cases pin that rule where it can be checked without a Store, a package
/// identity or a network.
/// </summary>
internal static class StoreUpdateTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        // The launch moment, and nothing else
        Check(StoreUpdateRules.MayCheckNow(packaged: true, alreadyCheckedThisProcess: false, mainWindowShown: false),
              "a Store install checks once while the app is still starting");
        Check(!StoreUpdateRules.MayCheckNow(packaged: true, alreadyCheckedThisProcess: false, mainWindowShown: true),
              "never once the main window is up, which is someone using the app");
        Check(!StoreUpdateRules.MayCheckNow(packaged: true, alreadyCheckedThisProcess: true, mainWindowShown: false),
              "never twice in one process, so no timer can resurrect it");
        Check(!StoreUpdateRules.MayCheckNow(packaged: false, alreadyCheckedThisProcess: false, mainWindowShown: false),
              "the direct download build has no Store to ask");
        Check(!StoreUpdateRules.MayCheckNow(packaged: false, alreadyCheckedThisProcess: true, mainWindowShown: true),
              "nothing about an unpackaged copy ever checks");

        // Only Microsoft's own mandatory flag blocks a launch
        Check(StoreUpdateRules.ShouldBlockLaunch(anyUpdate: true, anyMandatory: true),
              "an update marked mandatory in Partner Center blocks the launch");
        Check(!StoreUpdateRules.ShouldBlockLaunch(anyUpdate: true, anyMandatory: false),
              "an ordinary update is left to the Store and says nothing");
        Check(!StoreUpdateRules.ShouldBlockLaunch(anyUpdate: false, anyMandatory: false),
              "nothing waiting, nothing shown");
        Check(!StoreUpdateRules.ShouldBlockLaunch(anyUpdate: false, anyMandatory: true),
              "a mandatory flag with no update behind it is not a reason to block");

        // A gate that cannot finish must not become a locked door
        Check(StoreUpdateRules.ContinueWhenUpdateCannotBeDone(),
              "a failed or unreachable update still lets the user into the app");

        // The waits are sized for someone about to walk into an interview
        Check(StoreUpdateRules.LaunchCheckTimeout <= TimeSpan.FromSeconds(10),
              "a dead network costs seconds at launch, not a hang");
        Check(StoreUpdateRules.LaunchCheckTimeout >= TimeSpan.FromSeconds(3),
              "but long enough for a real check on a slow connection");
        Check(StoreUpdateRules.InstallTimeout >= TimeSpan.FromMinutes(2),
              "an install is allowed the minutes a package download actually takes");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "store updates: all passed" : $"store updates: {failed} FAILED");
        return failed;
    }
}
