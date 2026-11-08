using System;
using System.IO;
using System.Threading.Tasks;
using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// A crash must reach admin even when the process dies before its own report gets out (bug round 39, 2026-11-07): the fatal
/// path leaves crash.flag, and the next launch reports it once and removes it.
/// </summary>
internal static class CrashMarkerTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        string folder = Path.Combine(Path.GetTempPath(), "replysis-crashmarker-" + Guid.NewGuid().ToString("N"));
        try
        {
            Check(CrashMarker.TakePrevious(folder) == null, "no marker means the last run ended normally");

            var when = new DateTime(2026, 11, 7, 18, 30, 0, DateTimeKind.Utc);
            CrashMarker.Write(folder, "FATAL", when);
            Check(File.Exists(Path.Combine(folder, CrashMarker.FileName)), "a crash leaves the marker, creating the folder");

            string? text = CrashMarker.TakePrevious(folder);
            Check(text != null && text.StartsWith("2026-11-07T18:30:00") && text.EndsWith("FATAL"), "the next launch reads when and where it crashed");
            Check(!File.Exists(Path.Combine(folder, CrashMarker.FileName)), "the marker is removed once read");
            Check(CrashMarker.TakePrevious(folder) == null, "so it is reported only once");

            File.WriteAllText(Path.Combine(folder, CrashMarker.FileName), new string('x', 500));
            string? longText = CrashMarker.TakePrevious(folder);
            Check(longText != null && longText.Length == 80, "a damaged marker is cut short before it is sent");

            CrashMarker.Write(Path.Combine(folder, "a\0b"), "FATAL", when);
            Check(true, "a marker that cannot be written does not throw");

            Task sent = ClientErrorReporter.Report("TEST", null);
            Check(sent != null && sent.IsCompleted, "a report with nothing to send finishes at once, so the fatal path never waits on it");
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }

        string app = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "App.xaml.cs"));
        Check(app.Contains("CrashMarker.TakePrevious(CrashMarker.DefaultFolder)") && app.Contains("\"previous-run-crashed\""),
            "launch reports a previous crash");
        Check(app.Contains("sending.Wait(TimeSpan.FromSeconds(2))"), "a fatal background crash waits up to two seconds for its report");
        return failed;
    }
}
