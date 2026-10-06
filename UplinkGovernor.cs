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

        // ── Testing the line before a picture is trusted to it ───────────────────────────
        //
        // The governor above only learned the line was slow by losing a whole 300 to 450 KB picture on it: the first
        // picture of every launch went up on a slow hotspot, was still going 8 seconds later when the first question
        // was asked, and the question's answer waited behind it (measured 2026-10-06: 9.8 s to the first word, the
        // server having answered in half a second). The same happened again at the end of every pause, because the
        // test of whether the line had recovered was another full picture. So a line is tested with a small, throwaway
        // upload first, and pictures go ahead only once it has shown it can carry them in good time.

        /// <summary>
        /// Size of the test upload. Big enough to run past the burst a mobile line allows at the start (a 32 KB test passed on
        /// a hotspot that then could not finish a 480 KB picture in eight seconds), small enough to cost a slow line only two
        /// or three seconds, once per pause.
        /// </summary>
        internal const int ProbeBytes = 64 * 1024;

        /// <summary>A test upload still going after this is a slow line.</summary>
        internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

        /// <summary>
        /// The test upload must be done within this, round trip included: 64 KB in 0.7 s is about 90 KB a second at
        /// the very least, so a whole picture follows in about five seconds even on the worst line that passes, and in
        /// a second or two on a normal one.
        /// </summary>
        internal static readonly TimeSpan ProbePassWithin = TimeSpan.FromMilliseconds(700);

        private bool _verified;

        /// <summary>The line has shown it can carry a picture in good time since it was last in doubt.</summary>
        internal bool Verified => _verified;

        /// <summary>Pictures wait until a test has passed; this is true when one should be run now.</summary>
        internal bool NeedsProbe(DateTime nowUtc) => !_verified && nowUtc >= _quietUntilUtc;

        /// <summary>
        /// Records how a test upload went. A pass trusts the line; anything else starts the same growing pause as a
        /// failed picture, and returns it.
        /// </summary>
        internal TimeSpan RecordProbe(DateTime nowUtc, bool succeeded, TimeSpan elapsed)
        {
            if (succeeded && elapsed <= ProbePassWithin)
            {
                _verified = true;
                _failureStreak = 0;
                _quietUntilUtc = DateTime.MinValue;
                return TimeSpan.Zero;
            }
            return StartPause(nowUtc);
        }

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
                _verified = true;
                _failureStreak = 0;
                _quietUntilUtc = DateTime.MinValue;
                return TimeSpan.Zero;
            }

            return StartPause(nowUtc);
        }

        private TimeSpan StartPause(DateTime nowUtc)
        {
            // In doubt again: the next thing sent is a small test, not another full picture.
            _verified = false;
            _failureStreak++;
            double seconds = FirstBackoff.TotalSeconds * Math.Pow(2, Math.Min(_failureStreak - 1, 10));
            TimeSpan backoff = TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
            _quietUntilUtc = nowUtc + backoff;
            return backoff;
        }

        /// <summary>Forget everything, for a new session or a changed network.</summary>
        internal void Reset()
        {
            _verified = false;
            _failureStreak = 0;
            _quietUntilUtc = DateTime.MinValue;
        }
    }
}
