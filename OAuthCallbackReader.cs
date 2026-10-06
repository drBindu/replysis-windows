using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InterviewCopilot;

internal static class OAuthCallbackReader
{
    internal const int MaxHeaderBytes = 16 * 1024;
    internal static async Task<string> ReadAsync(Stream input, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[2048];
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException("Incomplete sign-in callback.");
            if (bytes.Length + read > MaxHeaderBytes) throw new InvalidDataException("Sign-in callback header is too large.");
            bytes.Write(buffer, 0, read);
            string header = Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
            if (header.Contains("\r\n\r\n", StringComparison.Ordinal)) return header;
        }
    }

    internal enum Answer { Code, Declined }

    /// <summary>How long a connection may stay silent before it is set aside as a spare the browser opened ahead of time.</summary>
    internal static readonly TimeSpan SilentConnectionTimeout = TimeSpan.FromSeconds(2);

    private static async Task RespondAsync(Stream stream, string status, string html, CancellationToken ct)
    {
        // Content-Length counts bytes, not characters, so it is measured from the encoded body.
        byte[] body = Encoding.UTF8.GetBytes(html);
        byte[] head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\n"
            + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(head, 0, head.Length, ct).ConfigureAwait(false);
            if (body.Length > 0) await stream.WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);
        }
        catch (IOException) { /* the browser went away; nothing to tell it */ }
    }

    // Browsers only permit window.close() when their security model considers the tab script-opened. A native app
    // opens the system browser through ShellExecute, so some close this tab and others deliberately refuse. The close
    // is attempted, and a calm fallback is left when it is refused. ASCII only: the tick is written as an entity.
    private const string SignedInPage =
        "<!doctype html><html><head><meta charset='utf-8'><title>Replysis</title></head>" +
        "<body style='margin:0;background:#FEFEFC;color:#16150F;font-family:Segoe UI,sans-serif;display:grid;place-items:center;height:100vh'>" +
        "<div style='text-align:center'><h2 style='color:#1C7A3E'>Signed in to Replysis</h2>" +
        "<p>Returning to the Replysis desktop app...</p></div>" +
        "<script>setTimeout(function(){window.open('','_self');window.close();},150);</script></body></html>";

    private const string NotCompletedPage =
        "<!doctype html><html><body style='margin:0;background:#FEFEFC;color:#9A2E24;font-family:Segoe UI,sans-serif;display:grid;place-items:center;height:100vh'>" +
        "<h2>Sign-in was not completed. Return to Replysis and try again.</h2></body></html>";

    /// <summary>
    /// Waits on the loopback listener for this sign-in attempt's answer, and ignores everything else.
    ///
    /// Browsers open spare connections to the address they are about to visit and ask for its icon. The first thing
    /// to connect used to be taken as the answer, so a spare connection that never said anything made sign-in time
    /// out and an icon request made it fail, although the real answer was still on its way. Anything that is not this
    /// attempt's answer is told "not found" and the wait carries on. Ends with the code, with a refusal (the person
    /// pressed Cancel), or by throwing OperationCanceledException when <paramref name="ct"/> runs out.
    /// </summary>
    internal static async Task<(Answer Result, string Code)> WaitForAnswerAsync(
        System.Net.Sockets.TcpListener listener, string expectedState, CancellationToken ct, Action<string>? log = null)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            log?.Invoke("connection accepted");
            using var stream = client.GetStream();

            string header;
            try
            {
                using var silent = CancellationTokenSource.CreateLinkedTokenSource(ct);
                silent.CancelAfter(SilentConnectionTimeout);
                header = await ReadAsync(stream, silent.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log?.Invoke("a connection said nothing; ignored");
                continue;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Net.Sockets.SocketException)
            {
                log?.Invoke($"a connection could not be read ({ex.GetType().Name}); ignored");
                continue;
            }

            if (TryGetCode(header, expectedState, out string code))
            {
                await RespondAsync(stream, "200 OK", SignedInPage, ct).ConfigureAwait(false);
                return (Answer.Code, code);
            }
            if (IsDeclined(header, expectedState))
            {
                await RespondAsync(stream, "200 OK", NotCompletedPage, ct).ConfigureAwait(false);
                return (Answer.Declined, "");
            }

            await RespondAsync(stream, "404 Not Found", "", ct).ConfigureAwait(false);
            log?.Invoke("a request that was not the sign-in answer; ignored");
        }
    }

    /// <summary>The query of a "GET /?..." request, or null for anything else or a malformed one.</summary>
    private static Dictionary<string, string>? ReadQuery(string header)
    {
        string firstLine = header.Split('\n')[0].TrimEnd('\r');
        string[] parts = firstLine.Split(' ');
        if (parts.Length != 3 || parts[0] != "GET" || !parts[1].StartsWith("/?", StringComparison.Ordinal)
            || parts[2] != "HTTP/1.1") return null;
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (string pair in parts[1][2..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int equal = pair.IndexOf('=');
                if (equal < 0) continue;
                string key = Uri.UnescapeDataString(pair[..equal]);
                string value = Uri.UnescapeDataString(pair[(equal + 1)..].Replace('+', ' '));
                if (!query.TryAdd(key, value)) return null;
            }
        }
        catch (UriFormatException) { return null; }
        return query;
    }

    internal static bool TryGetCode(string header, string expectedState, out string code)
    {
        code = "";
        if (string.IsNullOrEmpty(expectedState)) return false;
        var query = ReadQuery(header);
        if (query == null) return false;
        if (query.ContainsKey("error") || !query.TryGetValue("state", out string? state)
            || !string.Equals(state, expectedState, StringComparison.Ordinal)
            || !query.TryGetValue("code", out string? result) || string.IsNullOrWhiteSpace(result)) return false;
        code = result;
        return true;
    }

    /// <summary>
    /// Google sent this very sign-in attempt back with an error and no code: the person pressed Cancel, or refused
    /// the permissions. Only then is the attempt over; any other request that is not the answer (the browser asking
    /// for its icon, a spare connection, something else on the computer) is ignored and the wait goes on.
    /// </summary>
    internal static bool IsDeclined(string header, string expectedState)
    {
        if (string.IsNullOrEmpty(expectedState)) return false;
        var query = ReadQuery(header);
        return query != null && query.ContainsKey("error")
            && query.TryGetValue("state", out string? state)
            && string.Equals(state, expectedState, StringComparison.Ordinal);
    }
}
