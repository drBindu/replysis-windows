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
        return failed;
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
