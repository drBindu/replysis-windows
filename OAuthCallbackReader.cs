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

    internal static bool TryGetCode(string header, string expectedState, out string code)
    {
        code = "";
        string firstLine = header.Split('\n')[0].TrimEnd('\r');
        string[] parts = firstLine.Split(' ');
        if (parts.Length != 3 || parts[0] != "GET" || !parts[1].StartsWith("/?", StringComparison.Ordinal)
            || parts[2] != "HTTP/1.1" || string.IsNullOrEmpty(expectedState)) return false;
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (string pair in parts[1][2..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int equal = pair.IndexOf('=');
                if (equal < 0) continue;
                string key = Uri.UnescapeDataString(pair[..equal]);
                string value = Uri.UnescapeDataString(pair[(equal + 1)..].Replace('+', ' '));
                if (!query.TryAdd(key, value)) return false;
            }
        }
        catch (UriFormatException) { return false; }
        if (query.ContainsKey("error") || !query.TryGetValue("state", out string? state)
            || !string.Equals(state, expectedState, StringComparison.Ordinal)
            || !query.TryGetValue("code", out string? result) || string.IsNullOrWhiteSpace(result)) return false;
        code = result;
        return true;
    }
}
