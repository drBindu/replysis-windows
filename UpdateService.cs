using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace InterviewCopilot
{
    /// <summary>
    /// Keeps installed copies of Replysis current without ever interrupting the
    /// user.
    ///
    /// The shape of this deliberately mirrors how Slack and VS Code behave, and
    /// for the same reason: this app is used while someone is in a live job
    /// interview. Anything that steals focus, restarts the process, or blocks the
    /// window at the wrong second costs them the interview, so the rules here are
    /// strict.
    ///
    ///   1. Checking and downloading happen quietly in the background. Failures
    ///      are logged and otherwise invisible, so a GitHub outage is a non-event.
    ///   2. Nothing is ever applied while the app is running. The new version is
    ///      staged on disk and swapped in by the updater after the process exits.
    ///   3. The only restart is one the user asks for by name.
    ///
    /// Everything is a no-op unless the app is running from a Velopack install,
    /// so debug builds, and copies started straight out of a build folder, behave
    /// exactly as they did before.
    /// </summary>
    internal static class UpdateService
    {
        /// <summary>
        /// Where released builds are published. Public releases only: a
        /// prerelease must never reach a customer through the update channel.
        /// </summary>
        private const string ReleaseRepositoryUrl = "https://github.com/drBindu/replysis-windows";

        private static UpdateManager? _manager;
        private static UpdateInfo? _staged;

        // One update operation at a time. The launch check and the Settings
        // button can both land here, and Velopack's own lock throws rather than
        // queues, which would surface as a scary failure for a user who simply
        // pressed a button while the quiet check was running.
        private static readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>
        /// True when this copy was installed by the Replysis installer and can
        /// therefore be updated in place. False for a developer build or a copy
        /// run from an unzipped folder, where updating would be meaningless.
        /// </summary>
        internal static bool IsManaged
        {
            get
            {
                try { return Manager?.IsInstalled == true; }
                catch { return false; }
            }
        }

        /// <summary>The version staged and waiting for the next restart, if any.</summary>
        internal static string? PendingVersion { get; private set; }

        private static UpdateManager? Manager
        {
            get
            {
                if (_manager != null) return _manager;
                try
                {
                    _manager = new UpdateManager(
                        new GithubSource(ReleaseRepositoryUrl, accessToken: null, prerelease: false));
                }
                catch (Exception ex)
                {
                    DebugWindow.Log("UPDATE", $"Update source unavailable: {ex.GetType().Name}");
                }
                return _manager;
            }
        }

        /// <summary>
        /// Looks for a newer release and, if there is one, downloads it in the
        /// background. Returns the version now waiting to be installed, or null
        /// if the app is already current, is not an installed copy, or the check
        /// could not be completed. Never throws.
        /// </summary>
        internal static async Task<string?> CheckAndStageAsync(CancellationToken ct = default)
        {
            if (!IsManaged) return null;

            // Already staged from an earlier check. Re-downloading would waste the
            // user's bandwidth to arrive at the same place.
            if (PendingVersion != null) return PendingVersion;

            // Queue behind any check already running rather than giving up on it.
            // Returning early here is what made the Settings button answer "you
            // are up to date" while the quiet launch check was still downloading
            // the update it was about to find.
            await _gate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                // The check we waited on may have already found it.
                if (PendingVersion != null) return PendingVersion;

                UpdateManager? mgr = Manager;
                if (mgr == null) return null;

                UpdateInfo? update = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
                if (update == null) return null;

                string version = update.TargetFullRelease.Version.ToString();
                DebugWindow.Log("UPDATE", $"Downloading {version}");

                await mgr.DownloadUpdatesAsync(update, cancelToken: ct).ConfigureAwait(false);

                _staged = update;
                PendingVersion = version;
                DebugWindow.Log("UPDATE", $"{version} staged, will install on next restart");
                return version;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                DebugWindow.Log("UPDATE", $"Update check failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Hands the staged update to the updater, which waits for this process to
        /// exit and then swaps the files in. Call this on the way out, after the
        /// app has finished saving. No window, no prompt, no restart: the user gets
        /// the new version the next time they open Replysis.
        /// </summary>
        internal static void ApplyOnExit()
        {
            if (_staged == null) return;

            try
            {
                Manager?.WaitExitThenApplyUpdates(_staged, silent: true, restart: false);
            }
            catch (Exception ex)
            {
                // Worst case the update stays staged and installs after the next
                // exit. Never let this stop the app from closing.
                DebugWindow.Log("UPDATE", $"Could not hand off update: {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// Closes Replysis, installs the staged update, and opens it again. Only
        /// ever called because the user pressed a button asking for exactly that.
        /// </summary>
        internal static void ApplyAndRestart()
        {
            if (_staged == null) return;

            try
            {
                Manager?.ApplyUpdatesAndRestart(_staged);
            }
            catch (Exception ex)
            {
                DebugWindow.Log("UPDATE", $"Could not restart to update: {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// The running version, taken from the installed package when there is one
        /// so it always matches what the updater compares against, and from the
        /// assembly otherwise.
        /// </summary>
        internal static string CurrentVersion
        {
            get
            {
                try
                {
                    var installed = Manager?.CurrentVersion;
                    if (installed != null) return installed.ToString();
                }
                catch { }

                Version? v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v?.ToString(3) ?? "0.0.0";
            }
        }
    }
}
