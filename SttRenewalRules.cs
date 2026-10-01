using System;

namespace InterviewCopilot
{
    /// <summary>
    /// When to swap the hourly speech pass for a new one while the app is open.
    ///
    /// The pass lasts an hour and is checked only when a connection is made. A connection that is already open
    /// carries on after the hour, so a long interview is not cut off. But the engine holds the pass it was
    /// started with: the first time the connection drops after the hour (a network blip, a laptop waking), it
    /// reconnects with an expired pass, is refused, and the app has to notice, fetch a new one and restart the
    /// engine, which is tens of seconds of deafness at the worst moment.
    ///
    /// The code meant to prevent that was attached to the listening timer and returned at once while listening,
    /// so it never ran (found 2026-10-01 while asking whether the engine can stay ready for a whole day). It now
    /// runs on its own timer, and in Auto, which is "listening" between questions, it waits for a quiet moment
    /// rather than for a state that never comes.
    /// </summary>
    internal static class SttRenewalRules
    {
        /// <summary>Renew when this little time is left on the pass.</summary>
        internal static readonly TimeSpan RenewWithin = TimeSpan.FromMinutes(8);

        /// <summary>
        /// At least this long between attempts. The server allows a signed-in account 12 speech passes an hour, shared
        /// with every other reason one is asked for, so this stays well under that if renewing keeps failing.
        /// </summary>
        internal static readonly TimeSpan MinGap = TimeSpan.FromMinutes(10);

        internal static bool ShouldRenew(DateTime nowUtc, DateTime passExpiresUtc, DateTime lastAttemptUtc,
                                         bool engineStarting, bool answering, bool midQuestion, TimeSpan window)
        {
            if (passExpiresUtc == DateTime.MinValue) return false;       // no pass yet; the normal start fetches one
            if (engineStarting || answering || midQuestion) return false; // never in the middle of a question
            if (passExpiresUtc - nowUtc > window) return false;           // plenty left
            return lastAttemptUtc == DateTime.MinValue || nowUtc - lastAttemptUtc >= MinGap;
        }
    }
}
