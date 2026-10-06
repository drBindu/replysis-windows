using InterviewCopilot;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace CleanerTests;

internal static class AuditHardeningTests
{
    internal static int Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task<int> RunAsync()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }
        Check(WindowBounds.ClampPosition(100, 0, 600, 800) == 0, "oversized overlay drag stays anchored without throwing");
        Check(WindowBounds.ClampPosition(-500, -1920, 0, 400) == -500, "bounds helper accepts negative monitor coordinates");
        Check(WindowBounds.ClampPosition(900, 0, 1000, 300) == 700, "normal window is clamped at its right edge");

        using (var input = new MemoryStream(new byte[10]))
            Check(ResumeFileSafety.ReadBounded(input, 10).Length == 10, "resume file at its size boundary is accepted");
        bool tooLarge = false;
        using (var input = new MemoryStream(new byte[11]))
            try { ResumeFileSafety.ReadBounded(input, 10); } catch (InvalidDataException) { tooLarge = true; }
        Check(tooLarge, "resume reads enforce the limit while reading, not only before opening");
        ResumeFileSafety.ValidateDocx(CreateZip(1024));
        Check(true, "small DOCX archive passes expansion bounds");
        bool bombRejected = false;
        try { ResumeFileSafety.ValidateDocx(CreateZip((int)ResumeFileSafety.MaxExpandedBytes + 1)); }
        catch (InvalidDataException) { bombRejected = true; }
        Check(bombRejected, "highly compressed oversized DOCX is rejected before document parsing");
        bool malformedRejected = false;
        try { ResumeFileSafety.ValidateDocx(Encoding.UTF8.GetBytes("not a ZIP")); }
        catch (InvalidDataException) { malformedRejected = true; }
        Check(malformedRejected, "malformed DOCX fails safely");

        string valid = "GET /?code=abc%2B123&state=expected HTTP/1.1\r\nHost: localhost\r\n\r\n";
        Check(OAuthCallbackReader.TryGetCode(valid, "expected", out string code) && code == "abc+123", "valid OAuth callback verifies state and decodes the authorization code");
        Check(!OAuthCallbackReader.TryGetCode(valid, "another attempt", out _), "callback from another login attempt is rejected");
        Check(!OAuthCallbackReader.TryGetCode(valid.Replace("GET ", "POST "), "expected", out _), "unexpected callback method is rejected");
        Check(!OAuthCallbackReader.TryGetCode(valid.Replace("&state=", "&code=duplicate&state="), "expected", out _), "duplicate OAuth parameters are rejected");
        Check(!OAuthCallbackReader.TryGetCode(valid.Replace("&state=", "&error=denied&state="), "expected", out _), "OAuth error cannot be mistaken for success");
        Check(!OAuthCallbackReader.TryGetCode("GET /favicon.ico HTTP/1.1\r\n\r\n", "expected", out _), "browser ancillary request is not a login");
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(valid)))
            Check(await OAuthCallbackReader.ReadAsync(input, default) == valid, "bounded callback reader accepts normal headers");
        bool incomplete = false;
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes("GET /")))
            try { await OAuthCallbackReader.ReadAsync(input, default); } catch (InvalidDataException) { incomplete = true; }
        Check(incomplete, "partial callback is rejected at EOF");
        bool oversized = false;
        using (var input = new MemoryStream(new byte[OAuthCallbackReader.MaxHeaderBytes + 1]))
            try { await OAuthCallbackReader.ReadAsync(input, default); } catch (InvalidDataException) { oversized = true; }
        Check(oversized, "unbounded callback cannot grow memory indefinitely");
        using (var cts = new CancellationTokenSource())
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(valid)))
        {
            cts.Cancel();
            bool cancelled = false;
            try { await OAuthCallbackReader.ReadAsync(input, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "closed login cancels callback reading");
        }

        // Pressing Cancel at Google ends the attempt; nothing else that is not an answer does.
        string declinedRequest = "GET /?error=access_denied&state=expected HTTP/1.1\r\nHost: localhost\r\n\r\n";
        Check(OAuthCallbackReader.IsDeclined(declinedRequest, "expected"), "Cancel at Google ends this sign-in attempt");
        Check(!OAuthCallbackReader.IsDeclined(declinedRequest, "another attempt"), "a refusal for another attempt does not end this one");
        Check(!OAuthCallbackReader.IsDeclined("GET /favicon.ico HTTP/1.1\r\n\r\n", "expected"), "an icon request does not end the attempt");
        Check(!OAuthCallbackReader.IsDeclined(valid, "expected"), "a real answer is not a refusal");

        // The wait itself, over real sockets: a browser's spare connection that says nothing, its request for an icon
        // and a request from another attempt all arrive before the real answer. None of them may be taken for it.
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                Task<(OAuthCallbackReader.Answer Result, string Code)> waiting =
                    OAuthCallbackReader.WaitForAnswerAsync(listener, "expected", overall.Token);

                using var spare = new System.Net.Sockets.TcpClient();      // connects and never speaks
                await spare.ConnectAsync(System.Net.IPAddress.Loopback, port);

                string icon = await RoundTripAsync(port, "GET /favicon.ico HTTP/1.1\r\nHost: localhost\r\n\r\n");
                Check(icon.StartsWith("HTTP/1.1 404"), "an icon request is told not found while the wait goes on");
                string other = await RoundTripAsync(port, valid.Replace("state=expected", "state=somebody-else"));
                Check(other.StartsWith("HTTP/1.1 404"), "an answer for another attempt is ignored while the wait goes on");
                Check(!waiting.IsCompleted, "none of those ended the wait");

                string real = await RoundTripAsync(port, valid);
                Check(real.StartsWith("HTTP/1.1 200") && real.Contains("Signed in to Replysis"), "the real answer gets the signed-in page");
                var answer = await waiting;
                Check(answer.Result == OAuthCallbackReader.Answer.Code && answer.Code == "abc+123",
                    "the code arrives even though a silent connection came first");
            }
            finally { listener.Stop(); }
        }
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var waiting = OAuthCallbackReader.WaitForAnswerAsync(listener, "expected", overall.Token);
                string page = await RoundTripAsync(port, declinedRequest);
                var answer = await waiting;
                Check(answer.Result == OAuthCallbackReader.Answer.Declined && page.Contains("not completed"),
                    "Cancel at Google ends the wait at once with an explanation");
            }
            finally { listener.Stop(); }
        }
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using var shortWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                bool timedOut = false;
                try { await OAuthCallbackReader.WaitForAnswerAsync(listener, "expected", shortWait.Token); }
                catch (OperationCanceledException) { timedOut = true; }
                Check(timedOut, "with no answer at all the wait ends when the time is up");
            }
            finally { listener.Stop(); }
        }
        return failed;
    }

    /// <summary>Connects, sends one request, and returns everything the server says before it closes.</summary>
    private static async Task<string> RoundTripAsync(int port, string request)
    {
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
        var stream = client.GetStream();
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);
        var reply = new StringBuilder();
        byte[] buffer = new byte[4096];
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            int read = await stream.ReadAsync(buffer, limit.Token);
            if (read == 0) break;
            reply.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
        return reply.ToString();
    }

    private static byte[] CreateZip(int expandedBytes)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        using (var part = zip.CreateEntry("word/document.xml", CompressionLevel.Optimal).Open())
        {
            byte[] zeros = new byte[8192];
            while (expandedBytes > 0)
            {
                int count = Math.Min(expandedBytes, zeros.Length);
                part.Write(zeros, 0, count);
                expandedBytes -= count;
            }
        }
        return memory.ToArray();
    }
}
