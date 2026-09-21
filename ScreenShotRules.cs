using System;

namespace InterviewCopilot
{
    /// <summary>
    /// How long a prepared screenshot id stays usable.
    ///
    /// The server deletes a cached image 90 seconds after it was uploaded
    /// (STASHED_IMAGE_TTL_MS). The app skips the upload while the screen has not
    /// changed, which is right - the pixels are identical - but it also pushed its
    /// own clock forward each time it skipped. The id then looked fresh forever
    /// while the picture behind it had been deleted, and a question asked after
    /// two minutes on the same screen referenced nothing at all. That is exactly
    /// the case the feature exists for: a problem statement is read for minutes.
    ///
    /// So the clock now measures the upload, not the check, and an unchanged
    /// screen is sent again once the id gets old enough to be worth replacing.
    /// </summary>
    internal static class ScreenShotRules
    {
        /// <summary>The server's own lifetime for a cached image.</summary>
        internal static readonly TimeSpan ServerImageLifetime = TimeSpan.FromSeconds(90);

        /// <summary>Past this, the app stops offering the id with a question.</summary>
        internal static readonly TimeSpan IdMaxAge = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Past this, an unchanged screen is uploaded again so a fresh id exists
        /// before the old one ages out. Comfortably inside both lifetimes above.
        /// </summary>
        internal static readonly TimeSpan RefreshUnchangedAfter = TimeSpan.FromSeconds(45);

        internal static bool ShouldReuseUnchangedShot(TimeSpan sinceUpload) =>
            sinceUpload < RefreshUnchangedAfter;

        internal static bool IdStillUsable(TimeSpan sinceUpload) => sinceUpload <= IdMaxAge;
    }
}
