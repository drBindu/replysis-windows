using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// A new user's first run must not depend on the Windows it lands on (round 16, 2026-11-07): an antivirus that removed
/// the engine, a Thai calendar, a Store copy with the microphone switched off.
/// </summary>
internal static class FirstRunTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        // An engine folder with the exe removed is an install that lost its engine, not a developer checkout.
        string root = Path.Combine(Path.GetTempPath(), "replysis-firstrun-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "engine", "_internal"));
            Check(FirstRunRules.BundledEngineRemoved(root), "an engine folder without its exe counts as removed");
            File.WriteAllText(Path.Combine(root, "engine", "speechmatics_engine.exe"), "");
            Check(!FirstRunRules.BundledEngineRemoved(root), "an engine folder with its exe is fine");
            string dev = Path.Combine(root, "dev");
            Directory.CreateDirectory(dev);
            Check(!FirstRunRules.BundledEngineRemoved(dev), "a developer folder with no engine folder still uses Python");
            Check(!FirstRunRules.BundledEngineRemoved(""), "no folder is never called removed");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        Check(!FirstRunRules.EngineRemovedMessage.Contains("Python") && !FirstRunRules.EngineRemovedMessage.Contains("internet")
              && !FirstRunRules.EngineRemovedMessage.Contains("network") && !FirstRunRules.EngineRemovedMessage.Contains(" - ")
              && !FirstRunRules.EngineRemovedMessage.Contains("—"),
              "the removed engine message names no Python, no network and has no dash");

        // Times for the server and the session file are the same on every Windows calendar.
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            var when = new DateTime(2026, 11, 7, 9, 5, 3, 120, DateTimeKind.Utc);
            foreach (var name in new[] { "th-TH", "ar-SA", "fi-FI", "de-DE", "hi-IN", "en-US" })
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo(name);
                Check(FirstRunRules.ServerTimestamp(when) == "2026-11-07T09:05:03.120Z", $"server time is ISO on {name}");
                bool read = FirstRunRules.TryReadSessionHeaderTime(" 2026-11-07 09:05:03 ", out var parsed);
                Check(read && parsed.Year == 2026 && parsed.Month == 11 && parsed.Day == 7 && parsed.Hour == 9,
                      $"a session header date reads back as 2026 on {name}");
            }
        }
        finally { Thread.CurrentThread.CurrentCulture = saved; }
        Check(!FirstRunRules.TryReadSessionHeaderTime("not a date", out _), "a broken header date is not read");

        // Microphone privacy: the Store copy's own switch counts; the desktop apps switch is for the website copy.
        const string basePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        Func<string, string?> Reader(Dictionary<string, string> d) => p => d.TryGetValue(p, out var v) ? v : null;
        var none = new Dictionary<string, string>();
        const string pfn = "Varoxel.Replysis_abc123";
        Check(FirstRunRules.MicrophoneRefused(Reader(none), Reader(new() { [basePath + "\\" + pfn] = "Deny" }), pfn),
              "the Store copy switched off is seen");
        Check(!FirstRunRules.MicrophoneRefused(Reader(none), Reader(new() { [basePath + "\\" + pfn] = "Allow" }), pfn),
              "the Store copy switched on is not refused");
        Check(!FirstRunRules.MicrophoneRefused(Reader(none), Reader(new() { [basePath + @"\NonPackaged"] = "Deny" }), pfn),
              "the desktop apps switch does not apply to the Store copy");
        Check(FirstRunRules.MicrophoneRefused(Reader(none), Reader(new() { [basePath + @"\NonPackaged"] = "Deny" }), null),
              "the desktop apps switch applies to the website copy");
        Check(FirstRunRules.MicrophoneRefused(Reader(new() { [basePath] = "Deny" }), Reader(none), pfn),
              "the whole computer switch applies to both");
        Check(FirstRunRules.MicrophoneRefused(Reader(none), Reader(new() { [basePath] = "deny" }), null),
              "the user switch applies to both, whatever the case");
        Check(!FirstRunRules.MicrophoneRefused(Reader(none), Reader(none), null), "nothing set is not refused");

        // The Store package asks for the microphone.
        string manifest = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "InterviewCopilotPackager", "Package.appxmanifest");
        if (File.Exists(manifest))
            Check(File.ReadAllText(manifest).Contains("<DeviceCapability Name=\"microphone\" />"), "the Store manifest declares the microphone");

        // Round 17 (2026-11-07): with phone only on every window is hidden and Replysis is not in the taskbar. Opening it
        // again must bring the running copy back, not say "check your system tray" where it is not.
        string app = System.IO.File.ReadAllText(System.IO.Path.GetFullPath(System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "App.xaml.cs")));
        int gate = app.IndexOf("if (!_ownsSingleInstanceMutex)", StringComparison.Ordinal);
        int ask = gate < 0 ? -1 : app.IndexOf("AskRunningCopyToShow()", gate, StringComparison.Ordinal);
        int box = gate < 0 ? -1 : app.IndexOf("MessageBox.Show(", gate, StringComparison.Ordinal);
        Check(ask > 0 && box > ask, "a second launch asks the running copy to show itself before any message");
        Check(app.Contains("ListenForShowRequests();") && app.Contains("main.ShowFromSecondLaunch()"),
            "the running copy listens for a second launch and shows its window");
        Check(InterviewCopilot.App.ShowSignalName.StartsWith("Local\\", StringComparison.Ordinal),
            "the show request stays within this user's session, like the single instance lock");
        using (var running = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset,
                   @"Local\ReplysisTest_Show_" + Environment.ProcessId))
        using (var second = System.Threading.EventWaitHandle.OpenExisting(@"Local\ReplysisTest_Show_" + Environment.ProcessId))
        {
            second.Set();
            Check(running.WaitOne(1000), "a signal from a second copy reaches the running one");
        }

        return failed;
    }
}
