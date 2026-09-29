using System;

namespace InterviewCopilot
{
    /// <summary>
    /// How fast the app tries again after the connection or the sign-in fails, and how long
    /// it keeps trying. The answer to the second question is: for as long as the app is open.
    ///
    /// Found 2026-09-29 by leaving a running app on a laptop that went to sleep for two hours.
    /// On waking, the network was not up yet, so:
    ///   1. the sign-in token could not be refreshed, and the speech key was requested with the
    ///      expired one and refused with 401, which the app read as "sign in again";
    ///   2. the speech engine counted the DNS failures as a broken provider and dropped to the
    ///      slower fallback;
    ///   3. renewing the rejected credentials was allowed once a minute, and a second rejection
    ///      inside that minute marked the engine permanently failed ("fix your key in Settings").
    /// The network came back a few seconds later and nothing ever tried again. Closing and
    /// reopening a laptop is the most ordinary thing a person does before an interview.
    ///
    /// Nothing about a lost connection or an expired token is permanent, so nothing here gives up.
    /// </summary>
    internal static class RecoveryPolicy
    {
        /// <summary>
        /// The wait before asking for a speech key again after the request could not be made at
        /// all (no network, no DNS). Short at first, because a laptop waking up is usually
        /// online again within seconds and every second here is a second nobody is heard.
        /// Nothing reached the server, so nothing counts against its limits.
        /// </summary>
        internal static TimeSpan KeyRetryAfterNoConnection(int consecutiveFailures) =>
            TimeSpan.FromSeconds(consecutiveFailures switch
            {
                <= 1 => 2,
                2 => 4,
                3 => 8,
                4 => 15,
                _ => 30,
            });

        /// <summary>
        /// The wait between attempts to renew speech credentials the service rejected. The
        /// first is quick; then it settles at a minute, which is slow enough not to matter to
        /// the server and fast enough that a fixed problem is noticed within one.
        /// </summary>
        internal static int CredentialRenewalWaitSeconds(int attempt) =>
            attempt switch
            {
                <= 0 => 5,
                1 => 15,
                2 => 30,
                _ => 60,
            };
    }
}
