using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InterviewCopilot;

/// <summary>
/// Makes a Firebase-authenticated desktop account visible to the rest of the
/// Replysis product. The website's session endpoint owns creation of users/{uid}
/// so plan, credits and billing defaults stay in one server-side implementation.
/// </summary>
internal static class UserProfileSync
{
    internal enum PostResult { Success, Unauthorized, Failed }

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static long _syncedIdentity = -1;

    internal static string Endpoint(string backendBase) =>
        backendBase.TrimEnd('/') + "/api/auth/session";

    internal static string Payload(string idToken) =>
        JsonSerializer.Serialize(new { idToken });

    /// <summary>
    /// Ensures the current desktop identity has the same Firestore profile that
    /// a replysis.com login creates. Failure is deliberately non-fatal: callers
    /// can continue signing in and PresenceTracker retries on later heartbeats.
    /// </summary>
    internal static async Task<bool> EnsureCurrentUserAsync()
    {
        if (!UserSession.IsLoggedIn || string.IsNullOrWhiteSpace(UserSession.UserId))
            return false;

        await UserSession.EnsureFreshTokenAsync().ConfigureAwait(false);
        long identity = UserSession.Identity.Current;
        if (Interlocked.Read(ref _syncedIdentity) == identity) return true;

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            identity = UserSession.Identity.Current;
            if (Interlocked.Read(ref _syncedIdentity) == identity) return true;

            var snapshot = UserSession.Identity.Capture(() =>
                (Token: UserSession.IdToken, UserId: UserSession.UserId));
            if (snapshot.Generation != identity ||
                string.IsNullOrWhiteSpace(snapshot.Value.Token) ||
                string.IsNullOrWhiteSpace(snapshot.Value.UserId))
                return false;

            PostResult result = await PostAsync(snapshot.Value.Token).ConfigureAwait(false);
            if (result == PostResult.Unauthorized)
            {
                // The server is authoritative about token validity. Repair a
                // revoked/stale token once, then retry with the new token.
                if (!await UserSession.TryRefreshAsync(force: true).ConfigureAwait(false))
                    return false;

                var refreshed = UserSession.Identity.Capture(() =>
                    (Token: UserSession.IdToken, UserId: UserSession.UserId));
                if (refreshed.Generation != identity || refreshed.Value.UserId != snapshot.Value.UserId)
                    return false;
                result = await PostAsync(refreshed.Value.Token).ConfigureAwait(false);
            }

            if (result != PostResult.Success) return false;
            if (!UserSession.Identity.IsCurrent(identity) || UserSession.UserId != snapshot.Value.UserId)
                return false;

            Interlocked.Exchange(ref _syncedIdentity, identity);
            DebugWindow.Log("PROFILE_SYNC", "Admin-visible user profile is ready");
            return true;
        }
        catch (Exception ex)
        {
            DebugWindow.Log("PROFILE_SYNC", $"sync deferred: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<PostResult> PostAsync(string token)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await PostProfileAsync(
            SharedHttpClient.HttpShort,
            SettingsWindow.GetBackendUrl(),
            token,
            timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Separated from session state so the exact server contract is testable.</summary>
    internal static async Task<PostResult> PostProfileAsync(
        HttpClient http,
        string backendBase,
        string idToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(backendBase));
        request.Content = new StringContent(Payload(idToken), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return PostResult.Unauthorized;
        if (!response.IsSuccessStatusCode)
        {
            DebugWindow.Log("PROFILE_SYNC", $"HTTP {(int)response.StatusCode}; will retry later");
            return PostResult.Failed;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using JsonDocument json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True
                ? PostResult.Success
                : PostResult.Failed;
        }
        catch (JsonException)
        {
            return PostResult.Failed;
        }
    }
}
