using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InterviewCopilot
{
    /// <summary>
    /// Holds the current logged-in user's session data in memory + persists to disk.
    /// </summary>
    public static class UserSession
    {
        private static string FirebaseApiKey => SettingsWindow.GetFirebaseApiKey();
        private static HttpClient _http => SharedHttpClient.HttpShort;

        private static string SessionDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InterviewCopilot");

        private static string SessionPath => Path.Combine(SessionDir, "session.json");

        // ── In-memory state ──
        public static string IdToken      { get; private set; } = "";
        public static string RefreshToken { get; private set; } = "";
        public static string Email        { get; private set; } = "";
        public static string Name         { get; private set; } = "";
        public static string UserId       { get; private set; } = "";
        public static string PhotoUrl     { get; private set; } = "";
        public static bool   IsLoggedIn   => !string.IsNullOrEmpty(IdToken);

        // ── Cached SavedAt — avoids reading disk on every IsTokenExpired() call ──
        private static DateTime _savedAt = DateTime.MinValue;

        // ── Refresh token concurrency guard — prevents double-POST on simultaneous expiry ──
        private static readonly SemaphoreSlim _refreshSem = new(1, 1);

        // ── Guest session flag ──
        // True when the user is using the app without signing in.
        // The backend identifies the device via X-Device-Id and caps credits per month.
        public static bool IsGuestSession { get; set; } = false;

        // ── Credits (refreshed from backend) ──
        public static int    Credits    { get; set; } = 0;
        public static string Plan       { get; set; } = "free";
        public static bool   IsUnlimited { get; set; } = false;

        // ── Speechmatics key (fetched from backend; works for guests via X-Device-Id) ──
        public static string SpeechmaticsKey { get; private set; } = "";

        /// <summary>
        /// How much of a token's life must remain for it to be worth reusing.
        ///
        /// Was sixty seconds, which is long enough to start a session and not
        /// long enough to finish a question in one. A token accepted with
        /// sixty-one seconds left opens a connection that dies mid-answer, and
        /// the failure lands in the worst possible place: halfway through
        /// speaking to an interviewer.
        ///
        /// Five minutes costs nothing. Tokens last an hour, so renewing at
        /// fifty-five minutes instead of fifty-nine is still about one an hour
        /// against an allowance of twelve. The Mac session picked five minutes
        /// independently and was right; sixty seconds had no reasoning behind
        /// it beyond being a round number.
        /// </summary>
        private static readonly TimeSpan TokenRenewalMargin = TimeSpan.FromMinutes(5);

        private static readonly object _smKeyLock = new();
        private static DateTime _speechmaticsRetryAfterUtc = DateTime.MinValue;
        private static DateTime _speechmaticsExpiresAtUtc = DateTime.MinValue;
        public static int SpeechmaticsLastStatusCode { get; private set; }

        /// <summary>
        /// Why a 402 came back, since there are now two reasons and they need
        /// different words.
        ///
        /// Credits meter questions and listening time meters the microphone,
        /// and either can run out first. Both arrive as 402, so without this
        /// somebody who had spent their thirty hours but still had two thousand
        /// credits was told "NO CREDITS" while the badge showed those credits.
        /// A message that contradicts the screen beside it is worse than none.
        /// </summary>
        public static bool SpeechmaticsOutOfListeningTime { get; private set; }
        public static DateTime SpeechmaticsRetryAfterUtc
        {
            get { lock (_smKeyLock) return _speechmaticsRetryAfterUtc; }
        }

        public static bool HasValidSpeechmaticsKey
        {
            get
            {
                lock (_smKeyLock)
                    return !string.IsNullOrWhiteSpace(SpeechmaticsKey)
                        && DateTime.UtcNow < _speechmaticsExpiresAtUtc.Subtract(TokenRenewalMargin);
            }
        }

        // A single in-flight key fetch shared by every caller. Warming this up at the
        // very start of app launch (in parallel with process-kill + UI setup) means the
        // engine no longer stalls on a cold network round-trip before it can spawn
        // Python — by the time StartSpeechmaticsEngine runs, the key is already here.
        private static Task<bool>? _smKeyInFlight;

        public static Task<bool> EnsureSpeechmaticsKeyAsync(string deviceId = "")
        {
            lock (_smKeyLock)
            {
                if (HasValidSpeechmaticsKey) return Task.FromResult(true);
                if (TryLoadCachedSpeechmaticsKey()) return Task.FromResult(true);
                SpeechmaticsKey = "";
                _speechmaticsExpiresAtUtc = DateTime.MinValue;
                if (DateTime.UtcNow < _speechmaticsRetryAfterUtc) return Task.FromResult(false);
                if (_smKeyInFlight != null && !_smKeyInFlight.IsCompleted) return _smKeyInFlight;

                _smKeyInFlight = FetchSpeechmaticsKeyCoreAsync(deviceId);
                return _smKeyInFlight;
            }
        }

        // Preserve the old public method for callers, but route every request through
        // the same lock, in-flight task, and retry window.
        public static Task<bool> FetchSpeechmaticsKeyAsync(string deviceId = "") =>
            EnsureSpeechmaticsKeyAsync(deviceId);

        private static async Task<bool> FetchSpeechmaticsKeyCoreAsync(string deviceId)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{SettingsWindow.GetBackendUrl()}/api/v1/stt/key");

                // Only add Authorization if we have a real token.
                if (!string.IsNullOrEmpty(IdToken))
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {IdToken}");
                if (!string.IsNullOrEmpty(deviceId))
                    req.Headers.TryAddWithoutValidation("X-Device-Id", deviceId);

                using var res = await _http.SendAsync(req);
                string body = await res.Content.ReadAsStringAsync();

                if (!res.IsSuccessStatusCode)
                {
                    SpeechmaticsLastStatusCode = (int)res.StatusCode;
                    if ((int)res.StatusCode == 429)
                    {
                        TimeSpan delay = res.Headers.RetryAfter?.Delta
                            ?? (res.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                            ?? TimeSpan.FromSeconds(60);
                        if (delay < TimeSpan.FromSeconds(15)) delay = TimeSpan.FromSeconds(15);
                        if (delay > TimeSpan.FromMinutes(5)) delay = TimeSpan.FromMinutes(5);
                        lock (_smKeyLock)
                            _speechmaticsRetryAfterUtc = DateTime.UtcNow.Add(delay);
                        DebugWindow.Log("STT_KEY", $"Rate limited; retrying automatically in {Math.Ceiling(delay.TotalSeconds)} seconds");
                        return false;
                    }

                    if ((int)res.StatusCode == 402)
                    {
                        // The backend names which limit was hit. Anything else
                        // stays "credits", which is what it was before both
                        // limits existed.
                        bool audioLimit = body.Contains("audio-limit", StringComparison.OrdinalIgnoreCase)
                                       || body.Contains("listening time", StringComparison.OrdinalIgnoreCase);
                        lock (_smKeyLock) SpeechmaticsOutOfListeningTime = audioLimit;
                        DebugWindow.Log("STT_KEY", audioLimit
                            ? "402: monthly listening time used up"
                            : "402: out of credits");
                    }

                    lock (_smKeyLock)
                        _speechmaticsRetryAfterUtc = DateTime.UtcNow.AddSeconds(30);
                    DebugWindow.Log("STT_KEY", $"HTTP {(int)res.StatusCode}: {body[..Math.Min(body.Length, 120)]}");
                    return false;
                }

                using var doc = System.Text.Json.JsonDocument.Parse(body);
                string key = doc.RootElement.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                int expiresIn = doc.RootElement.TryGetProperty("expiresIn", out var expiry) && expiry.TryGetInt32(out int ttl)
                    ? Math.Clamp(ttl, 60, 86_400)
                    : 3_600;
                if (string.IsNullOrEmpty(key))
                {
                    lock (_smKeyLock)
                        _speechmaticsRetryAfterUtc = DateTime.UtcNow.AddSeconds(30);
                    DebugWindow.Log("STT_KEY", "200 OK but no 'key' field in response");
                    return false;
                }

                lock (_smKeyLock)
                {
                    SpeechmaticsKey = key;
                    _speechmaticsExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn);
                    _speechmaticsRetryAfterUtc = DateTime.MinValue;
                    SpeechmaticsLastStatusCode = 0;
                    SpeechmaticsOutOfListeningTime = false;
                }
                SaveCachedSpeechmaticsKey();
                DebugWindow.Log("STT_KEY", $"Temporary key fetched; valid for {expiresIn} seconds");
                ReadPlanLimitsFromToken(key);
                return true;
            }
            catch (Exception ex)
            {
                lock (_smKeyLock)
                    _speechmaticsRetryAfterUtc = DateTime.UtcNow.AddSeconds(30);
                DebugWindow.Log("STT_KEY", $"FetchSpeechmaticsKeyAsync failed: {ex.Message}");
                return false;
            }
        }

        // ── Token cache on disk ──────────────────────────────────────────────
        //
        // The token the server mints is good for about an hour, and it was kept
        // only in memory: closing the app threw away a perfectly valid one and
        // the next launch asked for another. The server allows twelve per
        // account per hour, so a dozen restarts, or an afternoon of testing,
        // exhausted the allowance and transcription stopped for the rest of the
        // hour with no way to hurry it along.
        //
        // The limit itself is right, and is there to stop someone scripting the
        // endpoint to drain quota. The waste was on this side. Reusing the token
        // until it actually expires means a normal user spends about one an hour
        // however many times they open the app.
        //
        // It also explains the Mac reporting the same fault at the same moment:
        // the allowance is per account, so both apps were locked out together
        // and neither had done anything wrong.
        //
        // Written through DPAPI like the session file, so it is readable only by
        // this Windows user on this machine.
        private static string SpeechmaticsKeyPath => Path.Combine(SessionDir, "sttkey.json");

        private sealed class CachedSttKey
        {
            public string Key { get; set; } = "";
            public DateTime ExpiresAtUtc { get; set; }
        }

        private static bool TryLoadCachedSpeechmaticsKey()
        {
            try
            {
                if (!File.Exists(SpeechmaticsKeyPath)) return false;

                string raw = File.ReadAllText(SpeechmaticsKeyPath);
                if (SecureDataProtector.IsProtected(raw) &&
                    !SecureDataProtector.TryUnprotect(raw, out raw)) return false;

                var cached = JsonSerializer.Deserialize<CachedSttKey>(raw);
                if (cached == null || string.IsNullOrWhiteSpace(cached.Key)) return false;

                // The same minute of headroom the in-memory check uses, so a token
                // is never handed to the engine moments before it dies.
                if (DateTime.UtcNow >= cached.ExpiresAtUtc.Subtract(TokenRenewalMargin)) return false;

                SpeechmaticsKey = cached.Key;
                _speechmaticsExpiresAtUtc = cached.ExpiresAtUtc;
                DebugWindow.Log("STT_KEY",
                    $"Reusing cached key; {(int)(cached.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds}s left");
                return true;
            }
            catch { return false; }
        }

        private static void SaveCachedSpeechmaticsKey()
        {
            try
            {
                Directory.CreateDirectory(SessionDir);
                string json;
                lock (_smKeyLock)
                    json = JsonSerializer.Serialize(new CachedSttKey
                    {
                        Key = SpeechmaticsKey,
                        ExpiresAtUtc = _speechmaticsExpiresAtUtc,
                    });
                File.WriteAllText(SpeechmaticsKeyPath, SecureDataProtector.Protect(json));
            }
            catch (Exception ex) { DebugWindow.Log("STT_KEY", $"Key cache write failed: {ex.Message}"); }
        }

        private static void DeleteCachedSpeechmaticsKey()
        {
            try { if (File.Exists(SpeechmaticsKeyPath)) File.Delete(SpeechmaticsKeyPath); }
            catch { }
        }

        /// <summary>How many sessions this plan allows at once. 0 until known.</summary>
        public static int SpeechConcurrencyLimit { get; private set; }

        /// <summary>"free", "pro", and so on. Empty until known.</summary>
        public static string SpeechAccountType { get; private set; } = "";

        /// <summary>
        /// Reads the plan's real limits out of the token we were just handed.
        ///
        /// The Speechmatics token is a JWT, and its claims carry the numbers
        /// that decide whether the product works: connection_quota is how many
        /// people may transcribe at once, across the whole account, and
        /// account_type says which plan that is.
        ///
        /// Nobody knew this number. It was two — meaning two customers, ever,
        /// simultaneously — and it was discovered only after a day of chasing
        /// symptoms: a second machine that would not transcribe, ghost sessions
        /// filling a ceiling nobody could see, and a swapped account key that
        /// appeared to change nothing. The number was sitting in every token
        /// the app had ever received.
        ///
        /// The Mac session's suggestion, and the best one of the week: read it
        /// here rather than leaving a customer to discover it mid-interview.
        /// Costs one base64 decode of a string already in memory, needs no API
        /// call and no portal access.
        ///
        /// Never throws. A token whose shape changes must not stop the app
        /// transcribing — this is information, not a gate.
        /// </summary>
        private static void ReadPlanLimitsFromToken(string jwt)
        {
            try
            {
                string[] parts = jwt.Split('.');
                if (parts.Length < 2) return;

                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

                using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
                var root = doc.RootElement;

                if (root.TryGetProperty("account_type", out var t))
                    SpeechAccountType = t.GetString() ?? "";

                // Speechmatics sends it as a string; tolerate a number too.
                if (root.TryGetProperty("connection_quota", out var q))
                {
                    SpeechConcurrencyLimit =
                        q.ValueKind == JsonValueKind.Number ? q.GetInt32()
                        : int.TryParse(q.GetString(), out int n) ? n : 0;
                }

                if (SpeechConcurrencyLimit > 0)
                {
                    DebugWindow.Log("STT_KEY",
                        $"Plan: {SpeechAccountType}, {SpeechConcurrencyLimit} simultaneous "
                        + "session" + (SpeechConcurrencyLimit == 1 ? "" : "s") + " allowed.");

                    // Two is one interview plus one other person anywhere in the
                    // world. Said plainly at startup rather than found out
                    // during an interview, which is how it was found out.
                    if (SpeechConcurrencyLimit <= 2)
                        DebugWindow.Log("STT_KEY",
                            $"WARNING: only {SpeechConcurrencyLimit} people can transcribe at "
                            + "once on this plan, across every device using this account.");
                }
            }
            catch { /* information, not a gate */ }
        }

        public static void InvalidateSpeechmaticsKey()
        {
            lock (_smKeyLock)
            {
                SpeechmaticsKey = "";
                _speechmaticsExpiresAtUtc = DateTime.MinValue;
                _speechmaticsRetryAfterUtc = DateTime.MinValue;
                SpeechmaticsLastStatusCode = 0;
                _smKeyInFlight = null;
            }
            DeleteCachedSpeechmaticsKey();
        }

        // ── Avatar initials ──
        public static string Initials
        {
            get
            {
                if (string.IsNullOrEmpty(Name)) return "?";
                var parts = Name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                    return $"{parts[0][0]}{parts[1][0]}".ToUpper();
                return Name.Length >= 2 ? Name.Substring(0, 2).ToUpper() : Name.ToUpper();
            }
        }

        // ── Set session after login ──
        public static void SetSession(string idToken, string email, string name, string userId, string refreshToken = "", string photoUrl = "")
        {
            InvalidateSpeechmaticsKey();
            IdToken        = idToken;
            RefreshToken   = refreshToken;
            Email          = email;
            Name           = string.IsNullOrEmpty(name) ? email.Split('@')[0] : name;
            UserId         = userId;
            PhotoUrl       = photoUrl ?? "";
            IsGuestSession = false;  // clear guest flag on real login
            SaveToDisk();
        }

        // ── Clear on logout ──
        public static void Clear()
        {
            IdToken          = "";
            RefreshToken     = "";
            Email            = "";
            Name             = "";
            UserId           = "";
            PhotoUrl         = "";
            Credits          = 0;
            Plan             = "free";
            IsUnlimited      = false;
            lock (_smKeyLock)
            {
                SpeechmaticsKey = "";
                _speechmaticsExpiresAtUtc = DateTime.MinValue;
                _speechmaticsRetryAfterUtc = DateTime.MinValue;
                SpeechmaticsLastStatusCode = 0;
                _smKeyInFlight = null;
            }
            IsGuestSession   = false;
            _savedAt         = DateTime.MinValue;
            try { if (File.Exists(SessionPath)) File.Delete(SessionPath); } catch (Exception ex) { DebugWindow.Log("SESSION", $"Delete session file failed: {ex.Message}"); }
        }

        // ── Persist session so user stays logged in between app restarts ──
        private static void SaveToDisk()
        {
            try
            {
                Directory.CreateDirectory(SessionDir);
                _savedAt = DateTime.UtcNow;     // update in-memory cache before writing disk
                var data = new SessionData
                {
                    IdToken      = IdToken,
                    RefreshToken = RefreshToken,
                    Email        = Email,
                    Name         = Name,
                    UserId       = UserId,
                    PhotoUrl     = PhotoUrl,
                    SavedAt      = _savedAt
                };
                WriteProtectedSession(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { DebugWindow.Log("SESSION", $"SaveToDisk failed: {ex.Message}"); }
        }

        // ── Load session from disk (on app start) ──
        // Returns true  → idToken valid, user is logged in.
        // Returns false → idToken expired OR missing.  If RefreshToken is non-empty after
        //                 this call, the caller should attempt TryRefreshAsync() for a
        //                 silent re-login instead of immediately showing the login screen.
        public static bool TryLoadFromDisk()
        {
            try
            {
                if (!File.Exists(SessionPath)) return false;

                string raw = File.ReadAllText(SessionPath);
                bool isProtected = SecureDataProtector.IsProtected(raw);
                string json = raw;
                if (isProtected && !SecureDataProtector.TryUnprotect(raw, out json))
                {
                    DebugWindow.Log("SESSION", "Could not decrypt session file.");
                    return false;
                }
                var data = JsonSerializer.Deserialize<SessionData>(json);
                if (data == null) return false;

                if (!isProtected)
                    WriteProtectedSession(json);

                // Cache the SavedAt timestamp in memory so IsTokenExpired() never reads disk again
                _savedAt = data.SavedAt;

                // Always load identity fields + refresh token so caller can attempt silent refresh
                RefreshToken = data.RefreshToken ?? "";
                Email        = data.Email        ?? "";
                Name         = data.Name         ?? "";
                UserId       = data.UserId        ?? "";
                PhotoUrl     = data.PhotoUrl      ?? "";

                // Token expires after 1 hour — if saved > 55 min ago return false but keep
                // RefreshToken so the caller can call TryRefreshAsync() without forcing re-login.
                if ((DateTime.UtcNow - data.SavedAt).TotalMinutes > 55)
                    return false;

                IdToken = data.IdToken ?? "";
                return IsLoggedIn;
            }
            catch (Exception ex)
            {
                DebugWindow.Log("SESSION", $"TryLoadFromDisk failed: {ex.Message}");
                return false;
            }
        }

        public static bool IsTokenExpired()
        {
            // Fast path — use in-memory cached timestamp (no disk I/O)
            if (_savedAt != DateTime.MinValue)
                return (DateTime.UtcNow - _savedAt).TotalMinutes > 55;

            // Slow path — first call after a cold start (no TryLoadFromDisk yet)
            try
            {
                if (!File.Exists(SessionPath)) return true;
                string raw = File.ReadAllText(SessionPath);
                string json = raw;
                if (SecureDataProtector.IsProtected(raw) && !SecureDataProtector.TryUnprotect(raw, out json))
                    return true;
                var data = JsonSerializer.Deserialize<SessionData>(json);
                if (data == null) return true;
                _savedAt = data.SavedAt;    // prime the cache
                return (DateTime.UtcNow - _savedAt).TotalMinutes > 55;
            }
            catch { return true; }
        }

        private static void WriteProtectedSession(string json)
        {
            Directory.CreateDirectory(SessionDir);
            string tmp = SessionPath + ".tmp";
            File.WriteAllText(tmp, SecureDataProtector.Protect(json));
            File.Move(tmp, SessionPath, true);
        }

        // ── Silently refresh the Firebase ID token using the stored refresh token ──
        /// <summary>
        /// Refreshes the sign-in token when it is about to expire, so a request
        /// is never sent with one the server will reject.
        ///
        /// Firebase tokens last an hour. Nothing on the answer path checked, so
        /// an app opened before an interview, or an interview that ran past the
        /// hour, sent an expired token, got a 401, and the 401 handler cleared
        /// the session and dropped the user to guest credits. In the middle of
        /// an interview, in place of an answer. The refresh token was sitting
        /// there the whole time.
        ///
        /// Safe to call before every request: it returns immediately unless the
        /// token is actually near expiry, and concurrent callers share one
        /// refresh through the semaphore inside TryRefreshAsync.
        /// </summary>
        public static async Task EnsureFreshTokenAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(IdToken) || string.IsNullOrEmpty(RefreshToken)) return;
                if (!IsTokenExpired()) return;
                await TryRefreshAsync().ConfigureAwait(false);
            }
            catch
            {
                // A failed refresh must not stop the request: the server may
                // still accept the token, and a 401 is handled downstream.
            }
        }

        public static async Task<bool> TryRefreshAsync()
        {
            if (string.IsNullOrEmpty(RefreshToken)) return false;
            if (!IsTokenExpired()) return true;
            await _refreshSem.WaitAsync();
            try
            {
                // Re-check after acquiring: a concurrent caller may have already refreshed
                if (!IsTokenExpired()) return true;

                string url = $"https://securetoken.googleapis.com/v1/token?key={FirebaseApiKey}";
                using var content = new System.Net.Http.FormUrlEncodedContent(new[]
                {
                    new System.Collections.Generic.KeyValuePair<string,string>("grant_type",    "refresh_token"),
                    new System.Collections.Generic.KeyValuePair<string,string>("refresh_token", RefreshToken),
                });
                using var res = await _http.PostAsync(url, content);
                string body = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode) return false;

                using var doc = JsonDocument.Parse(body);
                string newIdToken  = doc.RootElement.TryGetProperty("id_token",      out var t)  ? t.GetString()  ?? "" : "";
                string newRefresh  = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(newIdToken)) return false;

                IdToken = newIdToken;
                if (!string.IsNullOrEmpty(newRefresh))
                    RefreshToken = newRefresh;
                SaveToDisk();
                return true;
            }
            catch { return false; }
            finally { _refreshSem.Release(); }
        }

        private class SessionData
        {
            public string   IdToken      { get; set; } = "";
            public string   RefreshToken { get; set; } = "";
            public string   Email        { get; set; } = "";
            public string   Name         { get; set; } = "";
            public string   UserId       { get; set; } = "";
            public string   PhotoUrl     { get; set; } = "";
            public DateTime SavedAt      { get; set; } = DateTime.UtcNow;
        }
    }
}
