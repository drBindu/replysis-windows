using System.IO;
using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// Saving Settings starts from what is stored (round 6, 2026-11-07). It used to build a blank AppConfig, so every save
/// reset Keep on top, Detailed answers and the opacity move marker, and the next start put the chosen opacity back to 30%.
/// </summary>
internal static class SettingsSaveTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        var stored = new SettingsWindow.AppConfig
        {
            KeepOnTop = false, DetailedAnswers = true, OpacityDefaultVersion = 3, SarvamApiKey = "kept",
            AudioDeviceIndex = 4, AudioDeviceName = "Headset Microphone",
        };
        var copy = SettingsWindow.CopyConfig(stored);
        copy.MainWindowOpacity = 0.85;
        Check(!ReferenceEquals(copy, stored) && stored.MainWindowOpacity == 0.646,
              "the form works on a copy, so a failed save leaves the settings in use alone");
        Check(copy.KeepOnTop == false && copy.DetailedAnswers && copy.SarvamApiKey == "kept",
              "settings not on the form (Keep on top, Detailed answers, Sarvam key) survive a save");
        Check(copy.AudioDeviceIndex == 4 && copy.AudioDeviceName == "Headset Microphone",
              "the saved microphone is carried over when none is picked");
        Check(!SettingsWindow.MoveOldOpacityDefault(copy) && copy.MainWindowOpacity == 0.85,
              "an opacity chosen in Settings is not put back to 30% on the next start");

        var fresh = SettingsWindow.FreshConfig();
        fresh.MainWindowOpacity = 0.9;
        Check(!SettingsWindow.MoveOldOpacityDefault(fresh) && fresh.MainWindowOpacity == 0.9,
              "a new install's first chosen opacity is kept after a restart");

        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "InterviewCopilot.csproj")))
            root = Path.GetDirectoryName(root);
        if (root == null) { Check(false, "could not find the app's source folder"); return failed; }
        string src = File.ReadAllText(Path.Combine(root, "SettingsWindow.xaml.cs"));
        int save = src.IndexOf("private void SaveBtn_Click", StringComparison.Ordinal);
        int end = src.IndexOf("private void CancelBtn_Click", save, StringComparison.Ordinal);
        string body = save >= 0 && end > save ? src[save..end] : "";
        Check(body.Contains("CopyConfig(LoadConfig())") && !body.Contains("new AppConfig"),
              "the Settings save starts from the stored settings, not a blank one");
        return failed;
    }
}
