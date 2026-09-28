using System.Net;
using System.Net.Http;
using System.Text.Json;
using InterviewCopilot;

namespace CleanerTests;

internal static class UserProfileSyncTests
{
    internal static int Run()
    {
        int failed = 0;
        void Case(string label, bool ok)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        const string token = "firebase-id-token-value";
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var result = UserProfileSync.PostProfileAsync(
            http, "https://replysis.com/", token, CancellationToken.None).GetAwaiter().GetResult();

        Case("desktop profile uses the website session endpoint",
            handler.Uri == "https://replysis.com/api/auth/session");
        Case("Firebase token is sent as JSON without account defaults",
            handler.Body != null &&
            JsonDocument.Parse(handler.Body).RootElement.GetProperty("idToken").GetString() == token &&
            !handler.Body.Contains("credits", StringComparison.OrdinalIgnoreCase) &&
            !handler.Body.Contains("plan", StringComparison.OrdinalIgnoreCase));
        Case("server ok response completes profile sync", result == UserProfileSync.PostResult.Success);

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "user profile sync: all passed" : $"user profile sync: {failed} FAILED");
        return failed;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal string? Uri { get; private set; }
        internal string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri?.ToString();
            Body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}")
            };
        }
    }
}
