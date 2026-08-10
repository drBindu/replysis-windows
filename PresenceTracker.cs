using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InterviewCopilot
{
    /// <summary>
    /// Reports the signed-in user's presence to Firestore so the admin dashboard
    /// shows them as "Live" with an accurate last-seen. Writes <c>lastActive</c>
    /// every 60s (plus <c>lastLogin</c> on the first beat) straight to the
    /// users/{uid} document via the Firestore REST API, using the current Firebase
    /// ID token. Both fields are whitelisted by the Firestore security rules.
    ///
    /// Start() is safe to call once on app open: the loop runs for the app's
    /// lifetime, re-checks login state on every beat (so it no-ops while logged
    /// out and resumes after login or account switch), and never needs Stop().
    /// </summary>
    public static class PresenceTracker
    {
        private const string ProjectId = "copilotx-ai";
        private static HttpClient Http => SharedHttpClient.HttpShort;
        private static CancellationTokenSource? _cts;

        public static void Start()
        {
            if (_cts != null) return; // already running
            _cts = new CancellationTokenSource();
            _ = LoopAsync(_cts.Token);
        }

        public static void Stop()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        private static async Task LoopAsync(CancellationToken ct)
        {
            bool firstBeatSent = false;
            while (!ct.IsCancellationRequested)
            {
                // Include lastLogin only on the first successful-eligible beat
                bool ok = await BeatAsync(includeLogin: !firstBeatSent);
                if (ok) firstBeatSent = true;

                try { await Task.Delay(TimeSpan.FromSeconds(60), ct); }
                catch (TaskCanceledException) { break; }
            }
        }

        private static async Task<bool> BeatAsync(bool includeLogin)
        {
            try
            {
                if (!UserSession.IsLoggedIn || string.IsNullOrEmpty(UserSession.UserId))
                    return false;

                // Keep the ID token fresh (Firebase tokens expire hourly)
                if (UserSession.IsTokenExpired())
                    await UserSession.TryRefreshAsync();

                string token = UserSession.IdToken;
                if (string.IsNullOrEmpty(token)) return false;

                string nowIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                string fields = includeLogin
                    ? $"\"lastActive\":{{\"timestampValue\":\"{nowIso}\"}},\"lastLogin\":{{\"timestampValue\":\"{nowIso}\"}}"
                    : $"\"lastActive\":{{\"timestampValue\":\"{nowIso}\"}}";
                string mask = includeLogin
                    ? "updateMask.fieldPaths=lastActive&updateMask.fieldPaths=lastLogin"
                    : "updateMask.fieldPaths=lastActive";

                // currentDocument.exists=true → only ever update an existing user doc
                string url = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/users/{UserSession.UserId}?currentDocument.exists=true&{mask}";

                using var req = new HttpRequestMessage(HttpMethod.Patch, url);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                req.Content = new StringContent("{\"fields\":{" + fields + "}}", Encoding.UTF8, "application/json");

                using var res = await Http.SendAsync(req);
                if (!res.IsSuccessStatusCode)
                {
                    string body = await res.Content.ReadAsStringAsync();
                    DebugWindow.Log("PRESENCE", $"HTTP {(int)res.StatusCode}: {body[..Math.Min(body.Length, 160)]}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                DebugWindow.Log("PRESENCE", $"beat failed: {ex.Message}");
                return false;
            }
        }
    }
}
