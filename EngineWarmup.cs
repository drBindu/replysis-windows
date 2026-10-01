using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace InterviewCopilot
{
    /// <summary>
    /// Runs the speech engine once, in the background, at launch, only to load itself and exit.
    ///
    /// On a new computer the first real start of the engine opens about seventy freshly installed files, and Windows
    /// scans each of them the first time. That is a few seconds on a fast machine and more on a slow disk or a strict
    /// antivirus, and it lands exactly while the person is looking at "connecting" (2026-10-01: "when a new user
    /// installs and opens the app it says connecting or takes time to start"). Doing it at launch moves that cost to
    /// the seconds the person spends signing in or filling in Setup, so the real start finds everything cached.
    /// It needs no account, no network and no audio device.
    /// </summary>
    internal static class EngineWarmup
    {
        internal static string? EnginePath()
        {
            string candidate = Path.Combine(AppContext.BaseDirectory, "engine", "speechmatics_engine.exe");
            return File.Exists(candidate) ? candidate : null;
        }

        internal static void Start()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    string? exe = EnginePath();
                    if (exe == null) return;
                    var info = new ProcessStartInfo(exe, "--warmup")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = Path.GetDirectoryName(exe)!,
                    };
                    var sw = Stopwatch.StartNew();
                    using var p = Process.Start(info);
                    if (p == null) return;
                    try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                    // Never leaves a process behind: it should take a second or two, and is stopped if it does not.
                    if (!p.WaitForExit(20_000)) { try { p.Kill(true); } catch { } }
                    DebugWindow.Log("ENGINE", $"Warm-up finished in {sw.ElapsedMilliseconds}ms.");
                }
                catch (Exception ex)
                {
                    DebugWindow.Log("ENGINE", $"Warm-up skipped: {ex.GetType().Name}");
                }
            });
        }
    }
}
