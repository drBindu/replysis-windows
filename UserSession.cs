using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
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
        public static bool   IsLoggedIn   => !string.IsNullOrEmpty(IdToken);

        // ── Cached SavedAt — avoids reading disk on every IsTokenExpired() call ──
        private static DateTime _savedAt = DateTime.MinValue;

        // ── Credits (refreshed from Firestore) ──
        public static int    Credits    { get; set; } = 0;
        public static string Plan       { get; set; } = "free";
        public static bool   IsUnlimited => Plan == "pro" || Plan == "enterprise";

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
        public static void SetSession(string idToken, string email, string name, string userId, string refreshToken = "")
        {
            IdToken      = idToken;
            RefreshToken = refreshToken;
            Email        = email;
            Name         = string.IsNullOrEmpty(name) ? email.Split('@')[0] : name;
            UserId       = userId;
            SaveToDisk();
        }

        // ── Clear on logout ──
        public static void Clear()
        {
            IdToken      = "";
            RefreshToken = "";
            Email        = "";
            Name         = "";
            UserId       = "";
            Credits      = 0;
            Plan         = "free";
            _savedAt     = DateTime.MinValue;   // reset in-memory cache
            try { if (File.Exists(SessionPath)) File.Delete(SessionPath); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SESSION] Delete session file failed: {ex.Message}"); }
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
                    SavedAt      = _savedAt
                };
                File.WriteAllText(SessionPath,
                    JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SESSION] SaveToDisk failed: {ex.Message}"); }
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

                string json = File.ReadAllText(SessionPath);
                var data = JsonSerializer.Deserialize<SessionData>(json);
                if (data == null) return false;

                // Cache the SavedAt timestamp in memory so IsTokenExpired() never reads disk again
                _savedAt = data.SavedAt;

                // Always load identity fields + refresh token so caller can attempt silent refresh
                RefreshToken = data.RefreshToken ?? "";
                Email        = data.Email        ?? "";
                Name         = data.Name         ?? "";
                UserId       = data.UserId        ?? "";

                // Token expires after 1 hour — if saved > 55 min ago return false but keep
                // RefreshToken so the caller can call TryRefreshAsync() without forcing re-login.
                if ((DateTime.UtcNow - data.SavedAt).TotalMinutes > 55)
                    return false;

                IdToken = data.IdToken ?? "";
                return IsLoggedIn;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SESSION] TryLoadFromDisk failed: {ex.Message}");
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
                string json = File.ReadAllText(SessionPath);
                var data = JsonSerializer.Deserialize<SessionData>(json);
                if (data == null) return true;
                _savedAt = data.SavedAt;    // prime the cache
                return (DateTime.UtcNow - _savedAt).TotalMinutes > 55;
            }
            catch { return true; }
        }

        // ── Silently refresh the Firebase ID token using the stored refresh token ──
        public static async Task<bool> TryRefreshAsync()
        {
            if (string.IsNullOrEmpty(RefreshToken)) return false;
            if (!IsTokenExpired()) return true; // still valid, nothing to do
            try
            {
                string url = $"https://securetoken.googleapis.com/v1/token?key={FirebaseApiKey}";
                var content = new System.Net.Http.FormUrlEncodedContent(new[]
                {
                    new System.Collections.Generic.KeyValuePair<string,string>("grant_type",    "refresh_token"),
                    new System.Collections.Generic.KeyValuePair<string,string>("refresh_token", RefreshToken),
                });
                var res  = await _http.PostAsync(url, content);
                string body = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode) return false;

                using var doc = JsonDocument.Parse(body);
                string newIdToken  = doc.RootElement.TryGetProperty("id_token",      out var t)  ? t.GetString()  ?? "" : "";
                string newRefresh  = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(newIdToken)) return false;

                IdToken      = newIdToken;
                RefreshToken = newRefresh;
                SaveToDisk();
                return true;
            }
            catch { return false; }
        }

        private class SessionData
        {
            public string   IdToken      { get; set; } = "";
            public string   RefreshToken { get; set; } = "";
            public string   Email        { get; set; } = "";
            public string   Name         { get; set; } = "";
            public string   UserId       { get; set; } = "";
            public DateTime SavedAt      { get; set; } = DateTime.UtcNow;
        }
    }
}
