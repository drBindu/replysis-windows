using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// Existing users still on the old 25% default move to 30% once; anyone who chose a level keeps it.
/// </summary>
internal static class OpacityDefaultTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        var old = new SettingsWindow.AppConfig { MainWindowOpacity = 0.62, OverlayOpacity = 0.62 };
        Check(SettingsWindow.MoveOldOpacityDefault(old) && old.MainWindowOpacity == 0.646 && old.OverlayOpacity == 0.646,
              "the old 25% default becomes 30%, main window and Compact");
        old.MainWindowOpacity = 0.62;
        Check(!SettingsWindow.MoveOldOpacityDefault(old) && old.MainWindowOpacity == 0.62,
              "it happens once: a later choice of 25% is kept");

        var chosen = new SettingsWindow.AppConfig { MainWindowOpacity = 0.85, OverlayOpacity = 0.85 };
        SettingsWindow.MoveOldOpacityDefault(chosen);
        Check(chosen.MainWindowOpacity == 0.85, "a level the user chose is kept");

        Check(new SettingsWindow.AppConfig().MainWindowOpacity == 0.646, "a new install starts at 30%");
        double shown = 1 + (0.646 - 0.50) / 0.50 * 99;
        Check(Math.Round(shown) == 30, "0.646 shows as 30% on the slider");
        double saved = Math.Round(0.50 + (30 - 1) / 99.0 * 0.50, 3);
        Check(Math.Round(1 + (saved - 0.50) / 0.50 * 99) == 30, "30% saved and reopened still shows 30%");
        return failed;
    }
}
