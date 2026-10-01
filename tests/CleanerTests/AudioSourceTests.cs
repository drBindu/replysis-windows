using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// Interview hears the meeting only; Practice also hears the microphone.
///
/// The choice used to be a checkbox in Settings called "microphone", and people
/// left it on during real interviews, so the app heard the candidate's own answers
/// and treated them as new questions. These cases pin when the app offers to
/// switch, which has to be rare enough that nobody learns to ignore it.
/// </summary>
internal static class AudioSourceTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        // Offer Interview only when the microphone is open with a meeting running
        Check(AudioSourceRules.ShouldSuggestInterview(practiceOn: true, meetingAppRunning: true, alreadySuggested: false),
              "microphone open with Zoom running offers Interview");
        Check(!AudioSourceRules.ShouldSuggestInterview(practiceOn: true, meetingAppRunning: false, alreadySuggested: false),
              "practising with no meeting app is left alone");
        Check(!AudioSourceRules.ShouldSuggestInterview(practiceOn: false, meetingAppRunning: true, alreadySuggested: false),
              "already in Interview says nothing");
        Check(!AudioSourceRules.ShouldSuggestInterview(practiceOn: true, meetingAppRunning: true, alreadySuggested: true),
              "the offer is made once, not on a loop");

        // Offer Practice only after real silence with no meeting anywhere
        var quiet = AudioSourceRules.QuietBeforePracticeTip;
        Check(AudioSourceRules.ShouldSuggestPractice(interviewOn: true, listening: true, meetingAppRunning: false,
                  quietFor: quiet, alreadySuggested: false),
              "listening to silence alone offers Practice");
        Check(!AudioSourceRules.ShouldSuggestPractice(interviewOn: true, listening: true, meetingAppRunning: false,
                  quietFor: TimeSpan.FromSeconds(40), alreadySuggested: false),
              "a normal pause in an interview says nothing");
        Check(!AudioSourceRules.ShouldSuggestPractice(interviewOn: true, listening: true, meetingAppRunning: true,
                  quietFor: quiet + TimeSpan.FromMinutes(5), alreadySuggested: false),
              "a quiet stretch with Teams running is still an interview");
        Check(!AudioSourceRules.ShouldSuggestPractice(interviewOn: true, listening: false, meetingAppRunning: false,
                  quietFor: quiet, alreadySuggested: false),
              "not listening yet, nothing to suggest");
        Check(!AudioSourceRules.ShouldSuggestPractice(interviewOn: false, listening: true, meetingAppRunning: false,
                  quietFor: quiet, alreadySuggested: false),
              "already in Practice says nothing");
        Check(AudioSourceRules.QuietBeforePracticeTip >= TimeSpan.FromMinutes(2),
              "the silence needed is minutes, not seconds");

        // Which processes count as a meeting
        Check(AudioSourceRules.IsMeetingProcessName("Zoom"), "Zoom counts");
        Check(AudioSourceRules.IsMeetingProcessName("ms-teams"), "Teams counts");
        Check(AudioSourceRules.IsMeetingProcessName("Webex"), "Webex counts");
        Check(!AudioSourceRules.IsMeetingProcessName("chrome"), "a browser alone is not a meeting");
        Check(!AudioSourceRules.IsMeetingProcessName("InterviewCopilot"), "Replysis itself is not a meeting");

        // What the toolbar says
        Check(AudioSourceRules.HearingLine(true).Contains("microphone"), "Practice says the microphone is heard");
        Check(!AudioSourceRules.HearingLine(false).Contains("microphone"), "Interview never claims to hear the microphone");

        // New users start in Interview
        var defaults = new SettingsWindow.AppConfig();
        Check(!defaults.MicCaptureEnabled, "a new install starts in Interview, hearing the meeting only");

        // Choosing Practice while Auto is idle must switch now. It used to wait "until this question is over",
        // and in Auto nothing is ever a question while it sits silent, so the microphone never opened.
        var now = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        Check(!AudioSourceRules.SwitchMustWait(true, true, false, false, now, now.AddSeconds(-30)),
            "Auto, listening, nobody has spoken for a while: switch now");
        Check(!AudioSourceRules.SwitchMustWait(true, true, false, false, now, DateTime.MinValue),
            "Auto, listening, nothing has ever been heard: switch now");
        Check(AudioSourceRules.SwitchMustWait(true, true, false, false, now, now.AddSeconds(-1)),
            "Auto, somebody is speaking right now: wait for them");
        Check(AudioSourceRules.SwitchMustWait(true, false, true, false, now, now.AddSeconds(-30)),
            "an answer is being produced: wait");
        Check(AudioSourceRules.SwitchMustWait(true, false, false, true, now, now.AddSeconds(-30)),
            "a question is being flushed: wait");
        Check(AudioSourceRules.SwitchMustWait(false, true, false, false, now, now.AddSeconds(-30)),
            "Manual, Space is down: a question is being taken, wait");
        Check(!AudioSourceRules.SwitchMustWait(false, false, false, false, now, DateTime.MinValue),
            "not listening at all: switch now");

        // The hourly speech pass is swapped in a quiet moment, so a reconnect after the hour does not meet an expired one.
        var t0 = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var window = SttRenewalRules.RenewWithin;
        Check(!SttRenewalRules.ShouldRenew(t0, t0.AddMinutes(40), DateTime.MinValue, false, false, false, window),
            "40 minutes left: nothing to do yet");
        Check(SttRenewalRules.ShouldRenew(t0, t0.AddMinutes(7), DateTime.MinValue, false, false, false, window),
            "7 minutes left and a quiet moment: renew");
        Check(!SttRenewalRules.ShouldRenew(t0, t0.AddMinutes(7), DateTime.MinValue, false, true, false, window) &&
              !SttRenewalRules.ShouldRenew(t0, t0.AddMinutes(7), DateTime.MinValue, false, false, true, window) &&
              !SttRenewalRules.ShouldRenew(t0, t0.AddMinutes(7), DateTime.MinValue, true, false, false, window),
            "never while an answer is produced, someone is mid-question, or the engine is starting");
        Check(SttRenewalRules.ShouldRenew(t0, t0.AddMinutes(-3), DateTime.MinValue, false, false, false, window),
            "a pass that has already expired is renewed too");
        Check(!SttRenewalRules.ShouldRenew(t0, t0.AddMinutes(5), t0.AddMinutes(-3), false, false, false, window) &&
              SttRenewalRules.ShouldRenew(t0, t0.AddMinutes(5), t0.AddMinutes(-11), false, false, false, window),
            "if renewing keeps failing it waits ten minutes between tries, well under the server's twelve an hour");
        Check(!SttRenewalRules.ShouldRenew(t0, DateTime.MinValue, DateTime.MinValue, false, false, false, window),
            "with no pass at all the normal start fetches one");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "audio source: all passed" : $"audio source: {failed} FAILED");
        return failed;
    }
}
