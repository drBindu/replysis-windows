using System;

namespace InterviewCopilot
{
    /// <summary>
    /// Every reason the app can be running and still not hear anyone, with the
    /// words that tell the person what is wrong and what to do.
    ///
    /// Until 2026-09-29 most of these were a small coloured label beside the mic
    /// and nothing else. A tester on the Free plan with 55 credits and no
    /// listening time spoke to a silent app for minutes and concluded her laptop
    /// was broken, because "NO LISTENING TIME" next to "55 credits" reads as a
    /// contradiction, not an explanation.
    ///
    /// Two rules keep this from happening again, and a test enforces both:
    ///   1. Every problem here has a title, a plain-language body and a next step.
    ///      Adding a value to <see cref="Kind"/> without describing it fails the
    ///      build's tests, so a new failure state cannot ship as a bare label.
    ///   2. No numbers appear in the words. A plan's minutes and credits live on
    ///      the server and the website; a copy of them here goes stale the day
    ///      either changes. The words point at the pricing page for the numbers.
    /// </summary>
    internal static class ListeningProblems
    {
        internal enum Kind
        {
            NoListeningTime,
            NoCredits,
            SignInExpired,
            ServiceUnavailable,
            WaitingToReconnect,
            NoMicrophone,
            NoSpeechService,
        }

        internal enum NextStep { None, SeePlans }

        internal readonly record struct Description(string Label, string Title, string Body, NextStep Step);

        /// <param name="freeTrial">
        /// True for the one-time free answers (and a guest). Running out of those is not a monthly
        /// limit that renews, it is the end of a trial, so it says what Pro gives instead. Words
        /// only, no numbers: they belong to the server and the website.
        /// </param>
        internal static Description Describe(Kind kind, bool freeTrial = false) => kind switch
        {
            Kind.NoCredits when freeTrial => new(
                "NO CREDITS",
                "Your free answers are used",
                "That is what Replysis does in a real interview. Pro gives you a whole month of answers, " +
                "enough for many interviews, and you can cancel any time.",
                NextStep.SeePlans),

            Kind.NoListeningTime => new(
                "MONTHLY LIMIT",
                "Monthly listening limit reached",
                "You have reached this month's fair use limit for listening. You still have credits, but nothing " +
                "more can be heard until the limit renews or you upgrade. Reading your screen with F8 still works.",
                NextStep.SeePlans),

            Kind.NoCredits => new(
                "NO CREDITS",
                "No credits left this month",
                "Credits pay for every answer and screen read. Yours are used up, so Replysis cannot answer " +
                "until they renew or you upgrade.",
                NextStep.SeePlans),

            Kind.SignInExpired => new(
                "SIGN IN",
                "Please sign in again",
                "Your sign in has expired, so Replysis cannot connect to the speech service. " +
                "Open your profile menu at the top right, choose Sign out, then sign in again.",
                NextStep.None),

            Kind.ServiceUnavailable => new(
                "SERVICE OFFLINE",
                "The speech service is busy",
                "Our speech service is temporarily unavailable. Replysis keeps trying on its own and will start " +
                "listening again as soon as it is back. Nothing needs to be done.",
                NextStep.None),

            Kind.WaitingToReconnect => new(
                "RECONNECTING",
                "Reconnecting to speech",
                "Replysis is waiting a moment before it reconnects to the speech service. " +
                "It will start listening again by itself.",
                NextStep.None),

            Kind.NoMicrophone => new(
                "NO MICROPHONE",
                "No microphone found",
                "Practice mode needs a microphone. Plug one in, or turn on microphone access in Windows Settings, " +
                "under Privacy and security, then Microphone. Interview mode does not need a microphone.",
                NextStep.None),

            Kind.NoSpeechService => new(
                "NO SPEECH SERVICE",
                "Cannot reach the speech service",
                "This is usually a network that blocks it, such as a work or school network or a VPN. " +
                "Try a phone hotspot. Press Ctrl+Alt+F12 to see details.",
                NextStep.None),

            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Every problem needs a description."),
        };

        /// <summary>The state the app is in, from the facts it already tracks. Null when nothing is wrong.</summary>
        internal static Kind? Detect(
            bool engineOnline, int speechStatusCode, bool outOfListeningTime,
            bool waitingToRetry, bool fatalNoMicrophone, bool connectionStalled,
            bool outOfCredits = false)
        {
            if (engineOnline) return null;

            // A definite refusal outlives whatever the server said most recently. After a
            // "no listening time" the app kept asking, hit the hourly request limit, and the
            // latest status became "too many requests", which read as a passing reconnect:
            // the person saw nothing wrong and nothing explaining it (2026-09-29).
            if (outOfListeningTime) return Kind.NoListeningTime;
            if (outOfCredits) return Kind.NoCredits;
            if (speechStatusCode == 402) return Kind.NoCredits;
            if (speechStatusCode == 401) return Kind.SignInExpired;
            if (speechStatusCode is 502 or 503) return Kind.ServiceUnavailable;
            if (waitingToRetry) return Kind.WaitingToReconnect;
            if (fatalNoMicrophone) return Kind.NoMicrophone;
            if (connectionStalled) return Kind.NoSpeechService;
            return null;
        }
    }
}
