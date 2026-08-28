namespace InterviewCopilot
{
    internal static class SpeechRecognitionExitCodes
    {
        internal const int AuthenticationFailure = 2;
        internal const int AudioUsageLimit = 3;

        /// <summary>
        /// The account is at its limit of sessions running at once.
        ///
        /// Distinct from AuthenticationFailure because the two need opposite
        /// responses and were sharing one. A wrong key is permanent and only
        /// the user can fix it; a full account is temporary and fixes itself
        /// the moment another device stops.
        ///
        /// Sharing code 2 meant a busy account was reported as "fix your
        /// Speechmatics key in Settings" - which is wrong, unactionable, and
        /// sent somebody looking at a key that was perfectly good. It also
        /// stopped the app retrying, so it stayed dead until relaunched even
        /// after the other device had finished.
        /// </summary>
        internal const int ConcurrentSessionLimit = 4;
    }
}
