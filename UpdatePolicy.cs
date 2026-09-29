using System;

namespace InterviewCopilot
{
    /// <summary>
    /// When the app looks for a newer version, and when it tells the person.
    ///
    /// A tester's laptop never showed the "update ready" notice for 1.0.28 (2026-09-29).
    /// Two reasons, both ours:
    ///   1. The check ran once, twelve seconds after the app opened, and never again. An app
    ///      left open when a release came out would not look until it was closed and reopened.
    ///   2. A rule added in 1.0.27 skipped the notice while an error notice was on screen,
    ///      so a person stuck on a problem that the update fixes was never told about it.
    ///
    /// The check now repeats, and an update is announced once per version whatever else is
    /// showing. Nothing installs by itself while the app is open; the notice only says so.
    /// </summary>
    internal static class UpdatePolicy
    {
        /// <summary>After the app opens; long enough not to compete with the speech engine starting.</summary>
        internal static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(12);

        /// <summary>
        /// Between checks while the app stays open. GitHub allows an address 60 unauthenticated
        /// requests an hour, and a check is one, so 30 minutes leaves room for about 30 people
        /// behind the same address.
        /// </summary>
        internal static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(30);

        /// <summary>A version is announced once, so a dismissed notice does not come back every half hour.</summary>
        internal static bool ShouldAnnounce(string? found, string? alreadyAnnounced) =>
            !string.IsNullOrWhiteSpace(found) &&
            !string.Equals(found, alreadyAnnounced, StringComparison.OrdinalIgnoreCase);
    }
}
