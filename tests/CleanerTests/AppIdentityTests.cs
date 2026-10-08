using System.Net;
using System.Net.Http;
using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// The app tells its own server which app it is, and nobody else.
/// </summary>
internal static class AppIdentityTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public HttpRequestMessage? Seen;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    internal static int Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<int> RunAsync()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        string Header(HttpRequestMessage? r, string name) =>
            r != null && r.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : "";

        var inner = new Recorder();
        using var client = new HttpClient(new AppIdentityHandler(inner, () => "1.0.31", () => "replysis.com"));

        await client.GetAsync("https://replysis.com/api/v1/interview/credits");
        Check(Header(inner.Seen, "X-App-Platform") == "windows" && Header(inner.Seen, "X-App-Version") == "1.0.31",
            "a request to our server says it is the Windows app and which version");

        await client.GetAsync("https://REPLYSIS.com/api/v1/stt/key");
        Check(Header(inner.Seen, "X-App-Platform") == "windows", "the host is matched without regard to case");

        await client.GetAsync("https://api.github.com/repos/drBindu/replysis-windows/releases/latest");
        Check(Header(inner.Seen, "X-App-Platform") == "" && Header(inner.Seen, "X-App-Version") == "",
            "a request to GitHub carries nothing extra");

        await client.GetAsync("https://firestore.googleapis.com/v1/projects/x/databases/(default)/documents/users/y");
        Check(Header(inner.Seen, "X-App-Platform") == "", "a request to Google carries nothing extra");

        await client.GetAsync("https://replysis.com.evil.example/steal");
        Check(Header(inner.Seen, "X-App-Platform") == "", "a look-alike address never gets the headers");

        await client.GetAsync("https://evil-replysis.com/steal");
        Check(Header(inner.Seen, "X-App-Platform") == "", "a different domain ending the same way never gets the headers");

        var request = new HttpRequestMessage(HttpMethod.Get, "https://replysis.com/api/v1/health/ready");
        request.Headers.TryAddWithoutValidation("X-App-Platform", "mac");
        await client.SendAsync(request);
        Check(Header(inner.Seen, "X-App-Platform") == "mac", "a label already set by the caller is left alone");

        using var broken = new HttpClient(new AppIdentityHandler(new Recorder(), () => throw new InvalidOperationException("no version"), () => "replysis.com"));
        var response = await broken.GetAsync("https://replysis.com/");
        Check(response.StatusCode == HttpStatusCode.OK, "a failure while finding the version never fails the request");

        Check(!AppIdentityHandler.IsOurServer(null, "replysis.com") && !AppIdentityHandler.IsOurServer(new Uri("https://replysis.com/"), ""),
            "no address, or no known server, means no headers");

        using (var ping = PresenceTracker.BuildPingRequest("https://replysis.com/", "token-123"))
        {
            Check(ping.Method == HttpMethod.Post && ping.RequestUri!.ToString() == "https://replysis.com/api/v1/presence",
                "the open-now ping is a POST to our own presence address, whatever trailing slash the setting has");
            Check(ping.Headers.Authorization?.Scheme == "Bearer" && ping.Headers.Authorization?.Parameter == "token-123",
                "the ping says who is signed in, so the server can mark that person's app as open");
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "app identity: all passed" : $"app identity: {failed} FAILED");
        return failed;
    }
}
