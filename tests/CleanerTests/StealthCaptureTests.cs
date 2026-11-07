using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// Reading the screen hides Replysis's own windows from that one capture, then undoes it. Two things went wrong (round 5,
/// 2026-11-07): on a computer where Windows refused full hiding, the undo cleared the black box, so the answers showed in a
/// screen share after every screen read until the guard put it back; and the undo could clear hiding that stealth needed.
/// Also: on two screens the screen holding Replysis was read instead of the window the user worked in.
/// </summary>
internal static class StealthCaptureTests
{
    private const uint None = 0x0, BlackBox = 0x1, Hidden = 0x11;

    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        Check(WindowStealth.PlanCloak(true, Hidden) == WindowStealth.CloakStart.AlreadyHidden,
            "a window already hidden by stealth is left as it is");
        Check(WindowStealth.PlanCloak(true, BlackBox) == WindowStealth.CloakStart.KeepBlackBox,
            "a black box window is never touched by a screen read, so the black box stays up");
        Check(WindowStealth.PlanCloak(true, None) == WindowStealth.CloakStart.TryHide,
            "a visible window is hidden for the capture");
        Check(WindowStealth.PlanCloak(false, Hidden) == WindowStealth.CloakStart.TryHide,
            "when Windows cannot say, the window is hidden for the capture");

        Check(WindowStealth.ShouldUndoCloak(hiddenByThisRead: true, stealthOnNow: false),
            "hiding put on by the read is taken off when stealth is off");
        Check(!WindowStealth.ShouldUndoCloak(hiddenByThisRead: true, stealthOnNow: true),
            "stealth turned on during the read keeps the window hidden");
        Check(!WindowStealth.ShouldUndoCloak(hiddenByThisRead: false, stealthOnNow: false),
            "a read that changed nothing undoes nothing (black box, refused, already hidden)");

        IntPtr work = new(101), ours = new(202);
        Check(ScreenAnalyzer.ChooseMonitorWindow(ours, frontIsOurs: true, work, lastExternalAlive: true) == work,
            "with Replysis in front, the screen of the last work window is read");
        Check(ScreenAnalyzer.ChooseMonitorWindow(work, frontIsOurs: false, new IntPtr(303), lastExternalAlive: true) == work,
            "with another app in front, its own screen is read");
        Check(ScreenAnalyzer.ChooseMonitorWindow(ours, frontIsOurs: true, work, lastExternalAlive: false) == ours,
            "a closed work window falls back to the window in front");
        Check(ScreenAnalyzer.ChooseMonitorWindow(IntPtr.Zero, frontIsOurs: false, work, lastExternalAlive: true) == work,
            "nothing in front uses the last work window");

        return failed;
    }
}
