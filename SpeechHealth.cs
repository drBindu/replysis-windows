using System;
using System.Runtime.CompilerServices;

// The test project calls the shipping decision directly. Granting it access is
// preferable to widening these types to public: the alternative is a test that
// re-implements the rule and then agrees with itself, which is the failure the
// old string-presence test demonstrated for a fortnight.
[assembly: InternalsVisibleTo("CleanerTests")]

namespace InterviewCopilot
{
    /// <summary>
    /// Whether the speech engine has gone deaf while still reporting itself
    /// healthy.
    ///
    /// The decision lives here, apart from the window that acts on it, for one
    /// reason: it can be tested. It used to be a private method on MainWindow
    /// reading five instance fields, which meant the only way to see it work
    /// was to run a real interview and hope the engine broke — so nobody had
    /// ever watched it fire, and a safety net nobody has seen catch anything is
    /// a belief rather than a net. Three separate audio-reader bugs produced
    /// exactly the failure this exists to catch, and every one of them was
    /// found by a person noticing an empty transcript.
    ///
    /// Pure and static, so a test calls the shipping decision rather than a
    /// second copy of the rule written to match it.
    /// </summary>
    internal static class SpeechHealth
    {
        /// <summary>
        /// How long speech may arrive with nothing coming back before this is a
        /// fault rather than a pause. Speechmatics runs about 0.7s behind live
        /// speech, so anything under a few seconds is ordinary; twelve seconds
        /// of continuous speech returning nothing is not.
        ///
        /// THE THRESHOLD IS THE CONTRACT; THE POLL INTERVAL IS NOT.
        ///
        /// This is consulted from ListeningMeterTick, which runs every 5
        /// seconds, and the session clock starts when listening starts — so the
        /// ticks land at 5, 10, 15. Twelve is not one of them. The warning
        /// therefore appears at 15s, quantised upward, and 12 is a threshold
        /// nobody will ever observe directly.
        ///
        /// Stated because the Mac session measured exactly 15.000s against its
        /// own 12s threshold and a 5s meter, and two platforms reporting
        /// different numbers for identical logic would otherwise look like a
        /// bug in one of them. The honest statement of the behaviour is
        /// "warns between the threshold and the threshold plus one poll
        /// interval". If either platform changes its poll, the observed number
        /// moves, and that should read as a documented consequence rather than
        /// a mystery.
        /// </summary>
        internal static readonly TimeSpan DeafnessThreshold = TimeSpan.FromSeconds(12);

        /// <summary>
        /// How often <see cref="ShouldWarn"/> is actually consulted. Not used
        /// by the decision — recorded here so the observed latency above can be
        /// derived from the source rather than from a comment that drifts.
        /// </summary>
        internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

        /// <summary>How recently speech must have arrived to count as "now".</summary>
        internal static readonly TimeSpan SpeechIsCurrent = TimeSpan.FromSeconds(3);

        /// <summary>How often the warning may be repeated.</summary>
        internal static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(60);

        /// <param name="engineOnline">An offline engine already says so itself.</param>
        /// <param name="lastSpeechUtc">When the microphone last heard speech.</param>
        /// <param name="lastWordsUtc">When words last came back. MinValue if never.</param>
        /// <param name="listeningSinceUtc">When this listening session began.</param>
        /// <param name="lastWarningUtc">When this warning was last given.</param>
        /// <param name="silentSeconds">How long it has been hearing and returning nothing.</param>
        internal static bool ShouldWarn(
            bool engineOnline,
            DateTime now,
            DateTime lastSpeechUtc,
            DateTime lastWordsUtc,
            DateTime listeningSinceUtc,
            DateTime lastWarningUtc,
            out int silentSeconds)
        {
            silentSeconds = 0;

            if (!engineOnline) return false;

            // Speech has to be arriving right now. Without this, a quiet room
            // would look identical to a broken transcriber.
            if (lastSpeechUtc == DateTime.MinValue) return false;
            if (now - lastSpeechUtc > SpeechIsCurrent) return false;

            // Words at any point recently mean it is working.
            if (lastWordsUtc != DateTime.MinValue &&
                now - lastWordsUtc < DeafnessThreshold) return false;

            // And it must have been going on a while. A session that has only
            // just started has not had time to return anything yet.
            DateTime since = lastWordsUtc == DateTime.MinValue ? listeningSinceUtc : lastWordsUtc;
            if (since == DateTime.MinValue || now - since < DeafnessThreshold) return false;

            // Said once a minute at most. Repeating it every tick would bury
            // the answer under the warning about the answer.
            if (now - lastWarningUtc < WarningInterval) return false;

            silentSeconds = (int)(now - since).TotalSeconds;
            return true;
        }
    }
}
