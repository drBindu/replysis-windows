using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace InterviewCopilot
{
    /// <summary>
    /// A request body that goes out in small pieces and steps aside for the live voice.
    ///
    /// 2026-10-16, on the owner's PC: a 651 KB screenshot sent ahead at the moment listening started took the
    /// connection for 8 seconds, and in those 8 seconds the speech service answered nothing, so the question
    /// went out empty. The same sentence through the same speech engine with no upload running came back word
    /// for word. A line test cannot be trusted to prevent this (that evening it measured 1.2 MB a second, and
    /// the real upload could not move 650 KB in 8), so the upload watches the voice itself: before each piece
    /// it asks whether the speech service is falling behind, and waits while it is.
    ///
    /// Screen capture is not touched; only how the picture travels.
    /// </summary>
    internal sealed class PacedContent : HttpContent
    {
        internal const int PieceBytes = 16 * 1024;
        /// <summary>
        /// The fastest the picture may go out while someone is listening: 64 KB a second. Not more, because what is
        /// written to the socket is gone from our control into the system's send buffer, and on the owner's line
        /// (2026-10-16, under 40 KB a second up) 256 KB a second filled it in one go before any check could act.
        /// </summary>
        internal const int ListeningBytesPerSecond = 64 * 1024;
        /// <summary>How long one wait for the voice lasts before asking again.</summary>
        internal static readonly TimeSpan HoldStep = TimeSpan.FromMilliseconds(150);

        private readonly byte[] _body;
        private readonly int _bytesPerSecond;
        private readonly Func<bool> _voiceNeedsTheLine;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        /// <summary>Time spent waiting for the voice, so the line is not judged slow for a wait it chose.</summary>
        internal TimeSpan Held { get; private set; }
        internal int Holds { get; private set; }

        internal PacedContent(byte[] body, string mediaType, int bytesPerSecond, Func<bool> voiceNeedsTheLine,
                              Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            _body = body;
            _bytesPerSecond = Math.Max(PieceBytes, bytesPerSecond);
            _voiceNeedsTheLine = voiceNeedsTheLine;
            _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
            Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            WriteAsync(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            WriteAsync(stream, cancellationToken);

        internal async Task WriteAsync(Stream stream, CancellationToken ct)
        {
            double secondsPerPiece = (double)PieceBytes / _bytesPerSecond;
            for (int offset = 0; offset < _body.Length; offset += PieceBytes)
            {
                bool counted = false;
                while (_voiceNeedsTheLine())
                {
                    if (!counted) { Holds++; counted = true; }
                    await _delay(HoldStep, ct).ConfigureAwait(false);
                    Held += HoldStep;
                }
                int n = Math.Min(PieceBytes, _body.Length - offset);
                await stream.WriteAsync(_body.AsMemory(offset, n), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                if (offset + n < _body.Length)
                    await _delay(TimeSpan.FromSeconds(secondsPerPiece * n / PieceBytes), ct).ConfigureAwait(false);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _body.Length;
            return true;
        }
    }
}
