using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InterviewCopilot
{
    /// <summary>
    /// Tells the server, quietly, that something broke on this computer.
    ///
    /// Added 2026-10-01. A new person's first launch showed "Replysis recovered from an unexpected problem" on a
    /// PC nobody could look at, and the only record was a log file on that machine. Every new computer was a new
    /// unknown, found one at a time by somebody photographing a screen.
    ///
    /// It sends the app version, the Windows version, the kind of fault, a trimmed message and the METHOD NAMES
    /// from the stack. No account, address, file path, answer or resume: the server strips emails and user names
    /// from paths as well, and keeps the report in its log only. It never shows the person anything, never waits
    /// for a reply, sends at most a few a run, and never twice for the same fault.
    /// </summary>
    internal static class ClientErrorReporter
    {
        internal const int MaxPerRun = 6;

        private static readonly HashSet<string> _seen = new();
        private static int _sent;

        private static readonly Regex FrameName = new(@"^\s*at\s+([^\s(]+)", RegexOptions.Compiled);

        /// <summary>The method names from a stack trace, innermost first, and nothing else (no paths, no line text).</summary>
        internal static string Frames(string? stackTrace, int max = 8)
        {
            if (string.IsNullOrEmpty(stackTrace)) return "";
            var names = new List<string>();
            foreach (string line in stackTrace.Split('\n'))
            {
                var m = FrameName.Match(line);
                if (!m.Success) continue;
                names.Add(m.Groups[1].Value);
                if (names.Count >= max) break;
            }
            return string.Join(" < ", names);
        }

        /// <summary>One fault is one fingerprint: its type and where it came from. Used to send each only once a run.</summary>
        internal static string Fingerprint(Exception ex) =>
            ex.GetType().Name + "|" + Frames(ex.StackTrace, 2);

        /// <summary>Whether this fault should be sent now. At most <see cref="MaxPerRun"/>, each fault once.</summary>
        internal static bool ShouldSend(string fingerprint)
        {
            lock (_seen)
            {
                if (_sent >= MaxPerRun) return false;
                if (!_seen.Add(fingerprint)) return false;
                _sent++;
                return true;
            }
        }

        internal static void Report(string source, Exception? ex)
        {
#if DEBUG
            return;   // a developer build does not report to the live server
#else
            if (ex == null) return;
            try
            {
                if (!ShouldSend(Fingerprint(ex))) return;

                string version = "";
                try { version = FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? "").ProductVersion ?? ""; } catch { }

                var payload = new
                {
                    version,
                    os = $"{Environment.OSVersion.Version} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}",
                    source,
                    type = ex.GetType().FullName ?? ex.GetType().Name,
                    message = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message,
                    frames = Frames(ex.StackTrace),
                };
                string url = SettingsWindow.GetBackendUrl().TrimEnd('/') + "/api/v1/diagnostics/client-error";

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        using var req = new HttpRequestMessage(HttpMethod.Post, url)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                        };
                        using var _ = await SharedHttpClient.HttpShort.SendAsync(req, cts.Token).ConfigureAwait(false);
                    }
                    catch { /* a report that cannot be sent is not worth a second fault */ }
                });
            }
            catch { /* reporting must never itself crash the handler */ }
#endif
        }
    }
}
