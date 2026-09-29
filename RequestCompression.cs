using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;

namespace InterviewCopilot
{
    /// <summary>
    /// Compresses the answer request before it is uploaded.
    ///
    /// Every question carries the whole prompt: 19 to 23 KB once a resume is loaded. On a slow
    /// or shared uplink that upload is the slowest, most erratic part of getting an answer.
    /// Measured 2026-09-29 against the real server while the app was also streaming microphone
    /// audio: a tiny request answered in a steady 0.11 to 0.15 s, a 13 KB one took anywhere
    /// from 0.13 s to 1.08 s. The prompt is repetitive text and compresses to under half.
    ///
    /// The server accepts this since GzipRequestFilter. Should a server ever refuse it (an old
    /// one, a proxy that strips the header), the request is sent once more uncompressed and
    /// compression is switched off for the rest of the run. A refusal comes before anything is
    /// charged, so repeating it is safe; nothing else is ever repeated.
    /// </summary>
    internal static class RequestCompression
    {
        /// <summary>Under this, compressing costs more than it saves.</summary>
        internal const int MinBytes = 1_024;

        internal static bool ShouldCompress(int bytes) => bytes >= MinBytes;

        internal static byte[] Gzip(byte[] plain)
        {
            using var output = new MemoryStream(plain.Length / 2 + 32);
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                gzip.Write(plain, 0, plain.Length);
            return output.ToArray();
        }

        internal static HttpContent Content(byte[] plain)
        {
            var content = new ByteArrayContent(Gzip(plain));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            content.Headers.ContentEncoding.Add("gzip");
            return content;
        }

        /// <summary>
        /// The statuses a server gives a body it cannot decode: bad request, unsupported media
        /// type, not implemented. All of them come before the handler, so before any charge.
        /// </summary>
        internal static bool ServerRefusedCompression(int status) => status is 400 or 415 or 501;
    }
}
