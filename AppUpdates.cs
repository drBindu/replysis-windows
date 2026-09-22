using System;
using System.Threading.Tasks;
using System.Windows;

namespace InterviewCopilot
{
    /// <summary>
    /// One place that decides how this copy of Replysis gets its updates, so
    /// the rest of the app never has to know which channel it came from.
    ///
    /// There are two channels and they cannot both be right for the same copy:
    ///
    ///   Store          installed from the Microsoft Store. The Store owns
    ///                  updates. The app checks once at launch and, when
    ///                  Microsoft marks a release mandatory, offers to install
    ///                  it before the main window opens.
    ///   DirectDownload installed by the .exe from the website. Velopack stages
    ///                  updates quietly and applies them after the app exits.
    ///
    /// The channel is decided at runtime from the package identity rather than
    /// at compile time. One binary then behaves correctly whichever way it was
    /// installed, which matters because the same build is packaged both ways and
    /// a compile-time switch is a thing that can be got wrong silently in a
    /// release workflow.
    ///
    /// The Velopack path is never entered on a Store install: an app that
    /// downloaded its own replacement outside the Store would be both a policy
    /// problem and a way for two updaters to fight over the same install.
    /// </summary>
    internal static class AppUpdates
    {
        internal enum Channel
        {
            /// <summary>Installed from the Microsoft Store.</summary>
            Store,
            /// <summary>Installed by the .exe installer; Velopack keeps it current.</summary>
            DirectDownload,
            /// <summary>A developer build, or a copy run from a folder. Nothing updates it.</summary>
            Unmanaged,
        }

        private static Channel? _channel;

        internal static Channel Current
        {
            get
            {
                if (_channel != null) return _channel.Value;

                if (StoreUpdateService.IsStoreInstall) _channel = Channel.Store;
                else if (UpdateService.IsManaged) _channel = Channel.DirectDownload;
                else _channel = Channel.Unmanaged;

                DebugWindow.Log("UPDATE", $"Update channel: {_channel}");
                return _channel.Value;
            }
        }

        /// <summary>The running version, however this copy was installed.</summary>
        internal static string CurrentVersion
        {
            get
            {
                if (Current == Channel.DirectDownload) return UpdateService.CurrentVersion;

                Version? v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v?.ToString(3) ?? "0.0.0";
            }
        }

        /// <summary>
        /// The launch gate. Returns false when the app should not start, which
        /// happens only because the user chose to close rather than update.
        ///
        /// Called once, before the main window exists. Everything it can do
        /// takes place in that window of time on purpose: after this returns,
        /// nothing in the app may interrupt the user to update, for the rest of
        /// the process's life, whether that is ten minutes or ten hours.
        /// </summary>
        internal static async Task<bool> RunLaunchGateAsync()
        {
            if (Current != Channel.Store) return true;

            // The gate window is created first because the Store needs a window
            // handle to own its dialogs, and because a user on a slow connection
            // should see Replysis rather than an empty desktop while the check
            // runs.
            var gate = new UpdateRequiredWindow(null);

            try
            {
                // The handle is created without showing the window. Showing an
                // invisible one would take the keyboard focus for the length of
                // the check, and a launch that swallows the first thing someone
                // types is its own bug.
                IntPtr handle = gate.Handle;

                StoreUpdateService.LaunchCheck result =
                    await StoreUpdateService.CheckAtLaunchAsync(handle, mainWindowShown: false);

                if (result != StoreUpdateService.LaunchCheck.MandatoryWaiting)
                {
                    gate.Close();
                    return true;
                }

                gate.ShowVersion(StoreUpdateService.PendingVersion);
                gate.Show();
                gate.Activate();

                // Blocks here until the user updates, continues, or closes.
                await gate.WaitForDecisionAsync();
                return gate.MayContinue;
            }
            catch (Exception ex)
            {
                DebugWindow.Log("UPDATE", $"Launch gate failed: {ex.GetType().Name}: {ex.Message}");
                try { gate.Close(); } catch { }

                // A broken gate must never stop the app from opening.
                return true;
            }
        }

        /// <summary>
        /// Hands any staged direct-download update to the updater on the way
        /// out. Does nothing on a Store install, where the Store applies its own
        /// updates once the app is closed.
        /// </summary>
        internal static void ApplyOnExit()
        {
            if (Current != Channel.DirectDownload) return;
            UpdateService.ApplyOnExit();
        }

        /// <summary>
        /// The quiet background check the main window runs after launch. Only
        /// the direct-download channel has one: on the Store, checking again
        /// after launch could only lead to interrupting someone, which is the
        /// one thing this app must never do.
        /// </summary>
        internal static async Task<string?> CheckAndStageAsync()
        {
            if (Current != Channel.DirectDownload) return null;
            return await UpdateService.CheckAndStageAsync().ConfigureAwait(false);
        }

        /// <summary>What the Settings button should say about updates on this channel.</summary>
        internal static string DescribeChannel() => Current switch
        {
            Channel.Store          => "Updates are installed by the Microsoft Store.",
            Channel.DirectDownload => "Updates install automatically when you close Replysis.",
            _                      => "This copy was not installed, so it does not update.",
        };
    }
}
