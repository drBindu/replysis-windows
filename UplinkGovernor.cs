using System;

namespace InterviewCopilot
{
    /// <summary>
    /// Decides whether a speculative upload, the screenshot sent ahead of any question, may use the
    /// connection right now.
    ///
    /// Found 2026-10-01 on a phone hotspot that uploads 20 to 50 KB a second. Every sixteen seconds the
    /// app sent a 500 KB screenshot ahead, gave up after fifteen seconds, and sent the next one. That is
    /// one upload that never finished, running nearly all the time, on a connection whose whole capacity
    /// the speech audio alone nearly fills. The speech connection missed its keepalive and dropped every
    /// minute, answers waited 4 to 9 seconds behind the picture instead of 0.3 to 0.8, and the screen said
    /// "cannot reach the speech service". On a fast connection the same upload takes a fraction of a second
    /// and does exactly what it was meant to.
    ///
    /// A picture sent ahead is only worth anything if it arrives quickly, and it must never get in the way
    /// of what the person is actually waiting for. So: one upload that fails or takes too long and the app
    /// stops sending ahead for a while, doubling each time it fails again, and the next question reads the
    /// screen on demand instead. One quick upload and it is trusted again.
    /// </summary>
    internal sealed class UplinkGovernor
    {
        /// <summary>An upload that takes longer than this is not "ahead" of anything.</summary>
        internal static readonly TimeSpan MaxUsefulUpload = TimeSpan.FromSeconds(6);

        /// <summary>
        /// Gives up on one upload after this long. Shorter than the HTTP client's own fifteen seconds, so a
        /// doomed upload occupies the connection for as little time as possible.
        /// </summary>
        internal static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(8);

        internal static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(60);
        internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);

        private DateTime _quietUntilUtc = DateTime.MinValue;
        private int _failureStreak;

        /// <summary>How many sends in a row were too slow or failed. Zero means the connection is fine.</summary>
        internal int FailureStreak => _failureStreak;

        /// <summary>When sending ahead is allowed again. MinValue when it is allowed now.</summary>
        internal DateTime QuietUntilUtc => _quietUntilUtc;

        /// <summary>
        /// Whether to capture and send a picture ahead right now. After a quiet period ends this lets
        /// exactly one attempt through; its outcome decides what happens next.
        /// </summary>
        internal bool MayUpload(DateTime nowUtc) => nowUtc >= _quietUntilUtc;

        /// <summary>
        /// Records how a send ahead went. Returns the quiet period it started, or zero when the connection
        /// handled it well.
        /// </summary>
        internal TimeSpan Record(DateTime nowUtc, bool succeeded, TimeSpan elapsed)
        {
            if (succeeded && elapsed <= MaxUsefulUpload)
            {
                _failureStreak = 0;
                _quietUntilUtc = DateTime.MinValue;
                return TimeSpan.Zero;
            }

            _failureStreak++;
            double seconds = FirstBackoff.TotalSeconds * Math.Pow(2, Math.Min(_failureStreak - 1, 10));
            TimeSpan backoff = TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
            _quietUntilUtc = nowUtc + backoff;
            return backoff;
        }

        /// <summary>Forget everything, for a new session or a changed network.</summary>
        internal void Reset()
        {
            _failureStreak = 0;
            _quietUntilUtc = DateTime.MinValue;
        }
    }
}
