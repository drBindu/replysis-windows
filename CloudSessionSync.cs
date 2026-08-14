using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InterviewCopilot
{
    /// <summary>
    /// Mirrors each Q&amp;A pair to the Replysis session API so
    /// interview transcripts are backed up and visible in the Past Sessions panel
    /// across devices — matching the Mac app's cloud-backup behaviour.
    /// Fire-and-forget: never blocks the UI thread, never surfaces errors to the user.
    /// Skipped entirely when the user is not signed in.
    /// </summary>
    internal static class CloudSessionSync
    {
        private static string Endpoint => $"{SettingsWindow.GetBackendUrl()}/api/sessions";
        private static HttpClient Http => SharedHttpClient.HttpShort;

        // Firestore document ID for the current session (null = not yet created)
        private static string? _cloudSessionId;

        // Full turn history built up during the session; sent on every sync so the
        // server always has the complete transcript (matches Mac upsert behaviour).
        // Capped at 200 entries (100 Q&A pairs) to prevent unbounded payload growth.
        private static readonly List<object> _turns = new();
        private static readonly object StateLock = new();
        private static readonly SemaphoreSlim SyncGate = new(1, 1);
        private static long _sessionGeneration;
        private const int MaxTurns = 200;

        // ── Reset when a new local session starts ────────────────────────────
        public static Task ResetSessionAsync()
        {
            lock (StateLock)
            {
                _sessionGeneration++;
                _cloudSessionId = null;
                _turns.Clear();
            }
            return Task.CompletedTask;
        }

        // ── Called after every Q&A pair — fire and forget ────────────────────
        // Appends the new turn pair to the in-memory list, then POSTs the full
        // session to /api/sessions.  On the first call the server creates a
        // Firestore doc and returns its ID; subsequent calls update the same doc.
        public static async Task SyncTurnAsync(string q, string a, string resume, int durationSecs)
        {
            if (!UserSession.IsLoggedIn || !SettingsWindow.IsCloudSyncEnabled()) return;

            long requestedGeneration;
            lock (StateLock)
                requestedGeneration = _sessionGeneration;

            await SyncGate.WaitAsync();

            try
            {
                if (!UserSession.IsLoggedIn || !SettingsWindow.IsCloudSyncEnabled()) return;

                long sessionGeneration;
                string? cloudSessionId;
                object[] turns;
                string userId = UserSession.UserId;

                lock (StateLock)
                {
                    if (requestedGeneration != _sessionGeneration) return;

                    _turns.Add(new { role = "interviewer", text = q });
                    _turns.Add(new { role = "candidate",   text = a });
                    if (_turns.Count > MaxTurns)
                        _turns.RemoveRange(0, _turns.Count - MaxTurns);

                    sessionGeneration = requestedGeneration;
                    cloudSessionId = _cloudSessionId;
                    turns = _turns.ToArray();
                }

                // Keep the Firebase ID token fresh (expires after 1 hour)
                if (UserSession.IsTokenExpired() && !await UserSession.TryRefreshAsync())
                    return;

                string token = UserSession.IdToken;
                if (string.IsNullOrEmpty(token) || UserSession.UserId != userId) return;

                // Truncate resume to 300 chars, matching what the server stores
                string resumeSnippet = (resume ?? "").Length > 300
                    ? resume!.Substring(0, 300)
                    : (resume ?? "");

                // Build payload — conditionally include sessionId so the server
                // knows whether to create a new doc or update the existing one
                var payload = new Dictionary<string, object?>
                {
                    ["userEmail"]    = UserSession.Email,
                    ["companyName"]  = "Unknown",
                    ["role"]         = "Unknown",
                    ["resume"]       = resumeSnippet,
                    ["turns"]        = turns,
                    ["durationSecs"] = durationSecs,
                };
                if (cloudSessionId != null)
                    payload["sessionId"] = cloudSessionId;

                string json = JsonSerializer.Serialize(payload);
                using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                req.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var res = await Http.SendAsync(req);
                string respBody = await res.Content.ReadAsStringAsync();

                if (!res.IsSuccessStatusCode)
                {
                    DebugWindow.Log("CLOUD_SYNC", $"HTTP {(int)res.StatusCode}: {respBody[..Math.Min(respBody.Length, 160)]}");
                    return;
                }

                // On the first successful create, store the doc ID for future updates
                if (cloudSessionId == null)
                {
                    using var doc = JsonDocument.Parse(respBody);
                    if (doc.RootElement.TryGetProperty("sessionId", out var sid))
                    {
                        lock (StateLock)
                        {
                            if (sessionGeneration == _sessionGeneration && _cloudSessionId == null)
                                _cloudSessionId = sid.GetString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log("CLOUD_SYNC", $"sync failed: {ex.Message}");
            }
            finally
            {
                SyncGate.Release();
            }
        }
    }
}
