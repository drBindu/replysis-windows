using System;

namespace InterviewCopilot
{
    /// <summary>
    /// When a Store update may interrupt someone, and when it may not.
    ///
    /// The Microsoft Store is the primary Windows channel, and a Store package
    /// update cannot be applied while the app is running: the installer closes
    /// the process to swap the files. For most apps that is a shrug. This one is
    /// open during live job interviews, so a package update that arrives at the
    /// wrong second does not cost a reload, it costs the interview.
    ///
    /// The rule is therefore absolute and lives here rather than in the WinRT
    /// code, so it can be tested without a Store, a package identity, or a
    /// network:
    ///
    ///   A running copy of Replysis is never interrupted. Not for a mandatory
    ///   update, not after ten hours, not ever. The check happens once, during
    ///   launch, before the main window exists - which is the only moment when
    ///   nobody is mid-interview - and never again for the life of the process.
    ///
    /// Everything below exists to make that rule impossible to break by
    /// accident.
    /// </summary>
    internal static class StoreUpdateRules
    {
        /// <summary>
        /// How long the launch check may take before the app gives up and starts
        /// normally.
        ///
        /// A Store lookup is a network call, and a user on hotel wifi twenty
        /// minutes before an interview must not be staring at a splash screen
        /// because of it. Eight seconds is long enough for a healthy connection
        /// and short enough that a dead one is not felt as a hang.
        /// </summary>
        internal static readonly TimeSpan LaunchCheckTimeout = TimeSpan.FromSeconds(8);

        /// <summary>
        /// How long the download and install may take before the gate gives up
        /// and lets the user in with the version they already have.
        ///
        /// A mandatory update that cannot be downloaded must not become a locked
        /// door. Being one version behind is a problem; being unable to open the
        /// app at all, minutes before an interview, is a much larger one.
        /// </summary>
        internal static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Whether the launch check may run at all.
        ///
        /// <paramref name="packaged"/> is false for the direct-download build and
        /// for developer builds, where there is no Store to ask.
        /// <paramref name="alreadyCheckedThisProcess"/> is the guard that keeps
        /// this a launch-time event: once the process has checked, it never
        /// checks again, so no timer, no retry and no later code path can put an
        /// update in front of someone who is working.
        /// </summary>
        internal static bool MayCheckNow(bool packaged, bool alreadyCheckedThisProcess, bool mainWindowShown)
        {
            if (!packaged) return false;
            if (alreadyCheckedThisProcess) return false;

            // The main window being up means the launch moment has passed. Any
            // caller arriving here after that is a bug, and answering "no" is
            // what keeps it a harmless one.
            if (mainWindowShown) return false;

            return true;
        }

        /// <summary>
        /// Whether the user has to update before they can use this launch.
        ///
        /// Only an update Microsoft itself marks mandatory blocks, and only at
        /// launch. An ordinary update is left to the Store's own background
        /// service; the app says nothing about it, because a notice nobody asked
        /// for is the first step towards a popup during an interview.
        /// </summary>
        internal static bool ShouldBlockLaunch(bool anyUpdate, bool anyMandatory) => anyUpdate && anyMandatory;

        /// <summary>
        /// What the gate does when the Store cannot be reached, or the install
        /// fails, or either takes longer than the timeouts above.
        ///
        /// Always the same answer: let them in. Named as a method so the
        /// intention is readable at the call site and cannot be quietly inverted
        /// by someone adding a "temporarily" to it.
        /// </summary>
        internal static bool ContinueWhenUpdateCannotBeDone() => true;
    }
}
