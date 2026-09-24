using System;
using System.Diagnostics;
using System.Linq;

namespace InterviewCopilot
{
    /// <summary>
    /// Which sound the app listens to, and when to say something about it.
    ///
    /// The choice was a checkbox in Settings called "microphone", and people in a
    /// real interview left it on. The app then heard the candidate answering and
    /// treated their own words as the next question. The owner: "everyone attends
    /// interviews in meetings, and this is the main part" - so the choice belongs
    /// in the toolbar, named for the situation rather than the hardware:
    ///
    ///   Interview - the meeting only. Your own voice is never picked up.
    ///   Practice  - the meeting and your microphone, for practising alone.
    /// </summary>
    internal static class AudioSourceRules
    {
        /// <summary>Meeting apps that can be seen from outside. Google Meet runs in a
        /// browser tab and cannot, which is what the second tip is for.</summary>
        private static readonly string[] MeetingProcesses =
        {
            "zoom", "teams", "ms-teams", "msteams", "webex", "ciscocollabhost", "webexmta",
            "bluejeans", "gotomeeting", "g2mui", "skype", "lync", "ringcentral", "slack", "discord",
        };

        internal static bool IsMeetingProcessName(string name) =>
            !string.IsNullOrWhiteSpace(name) &&
            MeetingProcesses.Contains(name.Trim().ToLowerInvariant());

        /// <summary>True when a meeting app is running on this machine right now.</summary>
        internal static bool MeetingAppRunning()
        {
            try
            {
                return Process.GetProcesses().Any(p =>
                {
                    try { return IsMeetingProcessName(p.ProcessName); }
                    catch { return false; }
                });
            }
            catch { return false; }
        }

        /// <summary>
        /// Offer to switch to Interview: the microphone is open and a meeting app is
        /// running, which is the exact setup where the app hears the candidate.
        /// Said once per run, so it never nags.
        /// </summary>
        internal static bool ShouldSuggestInterview(bool practiceOn, bool meetingAppRunning, bool alreadySuggested) =>
            practiceOn && meetingAppRunning && !alreadySuggested;

        /// <summary>
        /// Offer to switch to Practice: listening in Interview with no meeting app and
        /// nothing heard for a few minutes, which is what practising alone looks like
        /// from here. Anything shorter would fire during a quiet stretch of a real
        /// interview.
        /// </summary>
        internal static readonly TimeSpan QuietBeforePracticeTip = TimeSpan.FromMinutes(3);

        internal static TimeSpan QuietDuration(DateTime now, DateTime listeningStarted, DateTime lastWords)
        {
            if (listeningStarted == DateTime.MinValue) return TimeSpan.Zero;
            DateTime since = lastWords > listeningStarted ? lastWords : listeningStarted;
            return now > since ? now - since : TimeSpan.Zero;
        }

        internal static bool ShouldSuggestPractice(bool interviewOn, bool listening, bool meetingAppRunning,
                                                   TimeSpan quietFor, bool alreadySuggested) =>
            interviewOn && listening && !meetingAppRunning && !alreadySuggested &&
            quietFor >= QuietBeforePracticeTip;

        /// <summary>What the toolbar says the app is hearing.</summary>
        internal static string HearingLine(bool practiceOn) =>
            practiceOn ? "Hearing the meeting and your microphone" : "Hearing the meeting only";
    }
}
