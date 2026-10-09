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

        /// <summary>
        /// The request that tells our own server "this app is open right now". lastActive says somebody is here but not which app or
        /// the website, because all of them write it; the server writes the rest from this ping (see PresenceController). The platform
        /// and version ride along as headers added by AppIdentityHandler.
        /// </summary>
        internal static HttpRequestMessage BuildPingRequest(string backendUrl, string token, bool listening = false)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, backendUrl.TrimEnd('/') + "/api/v1/presence" + (listening ? "?listening=1" : ""));
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            return req;
        }

        /// <summary>The request that says "this app was closed", so the admin page shows it gone at once and not after the ping times out.</summary>
        internal static HttpRequestMessage BuildLeaveRequest(string backendUrl, string token)
        {
            var req = new HttpRequestMessage(HttpMethod.Delete, backendUrl.TrimEnd('/') + "/api/v1/presence");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            return req;
        }

        private static volatile bool _listening;

        /// <summary>
        /// A session started or stopped. Told to our server at once, because the audio report that normally stamps "listening" only
        /// arrives after a full minute, and the once a minute ping keeps saying so while it lasts.
        /// </summary>
        public static void SetListening(bool on)
        {
            if (_listening == on) return;
            _listening = on;
            _ = PingNowAsync();
        }

        private static async Task PingNowAsync()
        {
            try
            {
                if (!UserSession.IsLoggedIn || string.IsNullOrEmpty(UserSession.IdToken)) return;
                await PingServerAsync(UserSession.IdToken);
            }
            catch { }
        }

        /// <summary>The app is closing. Waits a moment for the server to hear it, never longer, and never fails the close.</summary>
        public static void Leave()
        {
            try
            {
                _listening = false;
                if (!UserSession.IsLoggedIn || string.IsNullOrEmpty(UserSession.IdToken)) return;
                string token = UserSession.IdToken;
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                Task.Run(async () =>
                {
                    using var req = BuildLeaveRequest(SettingsWindow.GetBackendUrl(), token);
                    using var res = await Http.SendAsync(req, cts.Token);
                }).Wait(1800);
            }
            catch { }
        }

        private static async Task PingServerAsync(string token)
        {
            try
            {
                using var req = BuildPingRequest(SettingsWindow.GetBackendUrl(), token, _listening);
                using var res = await Http.SendAsync(req);
            }
            catch (Exception ex)
            {
                DebugWindow.Log("PRESENCE", $"ping failed: {ex.Message}");
            }
        }

        private static async Task<bool> BeatAsync(bool includeLogin)
        {
            try
            {
                if (!UserSession.IsLoggedIn || string.IsNullOrEmpty(UserSession.UserId))
                    return false;

                // The admin portal reads users/{uid}, while Firebase Auth and
                // Firestore are separate stores. Ensure the profile exists
                // before the update-only presence PATCH; retry each heartbeat
                // until a transient network/server failure clears.
                if (!await UserProfileSync.EnsureCurrentUserAsync())
                    return false;

                // Keep the ID token fresh (Firebase tokens expire hourly)
                if (UserSession.IsTokenExpired())
                    await UserSession.TryRefreshAsync();

                string token = UserSession.IdToken;
                if (string.IsNullOrEmpty(token)) return false;

                // Does not wait for it and cannot fail the beat.
                _ = PingServerAsync(token);

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
                    DebugWindow.Log("PRESENCE", $"HTTP {(int)res.StatusCode}");
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
