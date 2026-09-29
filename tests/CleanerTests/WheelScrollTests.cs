using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// Scrolling is slow everywhere (owner, 2026-09-29: "too fast", asked twice).
/// The first attempt divided a notch by three, which is 40 pixels against the
/// default 48, and nobody could tell the difference.
/// </summary>
internal static class WheelScrollTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        const int Notch = 120;              // one click of a wheel
        const double WindowsDefault = 48;   // three lines of 16 pixels
        double moved = WheelScroll.Target(1000, 5000, -Notch) - 1000;

        Check(moved > 0 && moved < WindowsDefault * 0.5, $"a notch moves {moved:0} px, under half the default {WindowsDefault} px");
        Check(WheelScroll.Target(1000, 5000, Notch) < 1000, "wheel up moves up");
        Check(WheelScroll.Target(0, 5000, Notch) == 0, "cannot scroll above the top");
        Check(WheelScroll.Target(4990, 5000, -Notch) == 5000, "cannot scroll past the bottom");
        Check(WheelScroll.Target(100, 0, -Notch) == 0, "a box with nothing to scroll stays put");

        // A box at its end hands the wheel to the page behind it.
        Check(!WheelScroll.CanMove(0, 800, Notch), "at the top, wheel up passes to the outer box");
        Check(!WheelScroll.CanMove(800, 800, -Notch), "at the bottom, wheel down passes to the outer box");
        Check(WheelScroll.CanMove(400, 800, Notch) && WheelScroll.CanMove(400, 800, -Notch), "in the middle it scrolls both ways");
        Check(!WheelScroll.CanMove(0, 0, -Notch), "no scrollable content means it never handles the wheel");

        // A touchpad sends many small deltas; twelve of 10 add up to one notch.
        double offset = 1000;
        for (int i = 0; i < 12; i++) offset = WheelScroll.Target(offset, 5000, -10);
        Check(Math.Abs((offset - 1000) - moved) < 0.01, "touchpad deltas add up to the same distance as a notch");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "wheel scroll: all passed" : $"wheel scroll: {failed} FAILED");
        return failed;
    }
}
