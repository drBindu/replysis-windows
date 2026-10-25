using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InterviewCopilot
{
    /// <summary>
    /// The phone app's Companion screen, from the laptop side (2026-10-25). The answers already reach the phone through
    /// our server as they stream; this adds what the laptop hears, word by word, whether it is listening, and the phone's
    /// Stop button.
    ///
    /// Sent to POST /api/v1/companion/laptop only while a phone is watching (the reply says how many are). With no phone
    /// open it sends just "listening or not" once every 15 seconds, without the words, to find out whether one has
    /// opened. The server keeps only the latest value in memory; nothing is stored.
    /// </summary>
    public static class CompanionLink
    {
        private static readonly TimeSpan WordsEvery = TimeSpan.FromMilliseconds(350);
        private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(3);    // how soon a Stop from the phone lands
        private static readonly TimeSpan ProbeEvery = TimeSpan.FromSeconds(15);

        private static HttpClient Http => SharedHttpClient.HttpShort;
        private static CancellationTokenSource? _cts;
        private static volatile string _heard = "";
        private static volatile bool _listening;
        private static volatile bool _changed;
        private static int _phones;
        private static DateTime _lastSent = DateTime.MinValue;

        /// <summary>The phone asked the laptop to stop. Called on a background thread.</summary>
        public static Action? OnStopRequested { get; set; }

        /// <summary>The phone's Answer button: answer what was just heard, like Space. Called on a background thread.</summary>
        public static Action? OnAnswerRequested { get; set; }

        public static void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            _ = LoopAsync(_cts.Token);
        }

        public static void Stop()
        {
            _cts?.Cancel();
            _cts = null;
        }

        /// <summary>The words on screen now (the interviewer's question so far).</summary>
        public static void Heard(string text)
        {
            text ??= "";
            if (text == _heard) return;
            _heard = text;
            _changed = true;
        }

        public static void SetListening(bool on)
        {
            if (_listening == on) return;
            _listening = on;
            if (!on) _heard = "";
            _changed = true;
        }

        private static async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var since = DateTime.UtcNow - _lastSent;
                    bool watching = _phones > 0;
                    bool due = watching
                        ? (_changed && since >= WordsEvery) || (_listening && since >= ReportEvery)
                        : (_changed || _listening) && since >= ProbeEvery;
                    if (due && UserSession.IsLoggedIn) await ReportAsync(watching, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    DebugWindow.Log("COMPANION", $"report failed: {ex.Message}");
                    _lastSent = DateTime.UtcNow;   // try again on the normal schedule, never in a tight loop
                }

                try { await Task.Delay(150, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        private static async Task ReportAsync(bool watching, CancellationToken ct)
        {
            _changed = false;
            _lastSent = DateTime.UtcNow;
            await UserSession.EnsureFreshTokenAsync();
            string body = JsonSerializer.Serialize(new { listening = _listening, heard = watching ? _heard : "" });
            using var req = new HttpRequestMessage(HttpMethod.Post, SettingsWindow.GetBackendUrl().TrimEnd('/') + "/api/v1/companion/laptop")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", UserSession.IdToken);
            using var res = await Http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return;   // an older server without Companion: nothing to do
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            int phones = doc.RootElement.TryGetProperty("phones", out var p) && p.TryGetInt32(out int n) ? n : 0;
            if (phones > 0 && _phones == 0) _changed = true;   // a phone just opened: send it the words at once
            _phones = phones;
            string command = doc.RootElement.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
            if (command == "stop")
            {
                DebugWindow.Log("COMPANION", "Stop asked from the phone");
                OnStopRequested?.Invoke();
            }
            else if (command == "answer")
            {
                DebugWindow.Log("COMPANION", "Answer asked from the phone");
                OnAnswerRequested?.Invoke();
            }
        }
    }
}
