using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace InterviewCopilot
{
    public partial class App : Application
    {
        private static Mutex? _singleInstanceMutex;
        private static bool _ownsSingleInstanceMutex;
        private bool _showAuthenticationPreview;

        // Suppression window so a repeating fault (e.g. a timer throwing every
        // tick) cannot stack dozens of identical dialogs on top of each other.

        protected override void OnStartup(StartupEventArgs e)
        {
            // Slower wheel scrolling in every box, before any window exists.
            WheelScroll.Install();
            _showAuthenticationPreview = Array.Exists(e.Args,
                arg => string.Equals(arg, "--auth-preview", StringComparison.OrdinalIgnoreCase));
            // Install crash protection before anything else can throw. Without
            // this an unhandled exception tears the process down and Windows
            // shows a raw .NET stack trace, losing the session.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

            // Local\ scopes the lock to the current user's session. Global\ spanned
            // every account on the machine, so on a shared PC a second user was told
            // the app was "already running" and sent to look in a system tray they
            // cannot see, because the running copy belonged to another session.
            const string MutexName = "Local\\InterviewCopilot_SingleInstance";
            _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
            _ownsSingleInstanceMutex = createdNew;
            if (!createdNew)
            {
                MessageBox.Show(
                    "Replysis AI is already running.\n\nCheck your system tray or taskbar.",
                    "Already Running", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            // Nothing is allowed to end the process while there is no window on
            // screen. The launch gate closes its own window before the main one
            // is created, and with the default rule that moment - zero windows
            // open - would shut the app down before it ever started.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Load the speech engine's files now, while the person is signing in or filling in Setup, so the real
            // start does not pay for a new computer scanning them (see EngineWarmup).
            EngineWarmup.Start();

            // The main window restores the saved Firebase session before requesting a
            // transcription credential. Do not prefetch here: doing so treats a returning
            // Pro user as a guest for a few milliseconds and can create a false 402 retry.
            base.OnStartup(e);

            // The main window used to be created by StartupUri, which gave no
            // moment in which to ask the Store anything. It is created below
            // instead, after the gate, because a mandatory update can only be
            // installed while nobody is using the app.
            _ = OpenMainWindowAsync();
        }

        /// <summary>
        /// Runs the update gate, then opens the app.
        ///
        /// The gate is a launch-time event and nothing else: once the main
        /// window is up, no update may take the app away from the person using
        /// it. They may be nine hours into a working day with an interview in
        /// ten minutes, and a release published at lunchtime is not a reason to
        /// interrupt that.
        /// </summary>
        private async Task OpenMainWindowAsync()
        {
            bool proceed = true;

            try
            {
                proceed = await AppUpdates.RunLaunchGateAsync();
            }
            catch (Exception ex)
            {
                // A gate that throws must not be a gate that locks.
                LogCrash("UPDATE-GATE", ex);
            }

            if (!proceed)
            {
                Shutdown();
                return;
            }

            try
            {
                // Restore the last signed-in account before creating any window.
                // Firebase ID tokens expire after an hour, but the protected local
                // session also contains a refresh token, so returning users can be
                // signed in silently rather than seeing the account page every time.
                bool sessionRestored = !_showAuthenticationPreview && UserSession.TryLoadFromDisk();
                if (!sessionRestored && !string.IsNullOrEmpty(UserSession.RefreshToken))
                {
                    DebugWindow.Log("AUTH", "Saved ID token expired - refreshing session before launch");
                    var outcome = await UserSession.RefreshAsync(force: true);

                    // A laptop opened before its Wi-Fi is up, a hotspot still connecting: wait a moment and ask
                    // once more before deciding anything. Most of the time the network is back within seconds.
                    if (outcome == UserSession.RefreshOutcome.NoConnection)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3));
                        outcome = await UserSession.RefreshAsync(force: true);
                    }

                    if (outcome == UserSession.RefreshOutcome.Refreshed)
                    {
                        sessionRestored = true;
                        DebugWindow.Log("AUTH", "Saved session restored");
                    }
                    else if (outcome == UserSession.RefreshOutcome.NoConnection)
                    {
                        // No connection is not a sign-out. Open signed in with what was saved; every request
                        // refreshes first and retries, so the app catches up by itself when the network is back.
                        // Showing the sign-in screen here made people sign in again every time they opened the
                        // app before their Wi-Fi had connected.
                        sessionRestored = UserSession.ContinueOfflineWithSavedSession();
                        DebugWindow.Log("AUTH", sessionRestored
                            ? "No connection at launch; opening signed in and refreshing when the network is back"
                            : "No connection at launch and no saved sign-in to continue with");
                    }
                    else
                    {
                        DebugWindow.Log("AUTH", "Saved session could not be refreshed: the sign-in was refused");
                    }
                }

                bool interactiveLogin = !sessionRestored;
                if (interactiveLogin)
                {
                    // No dashboard is created behind this page. It is the only
                    // first-launch surface until authentication succeeds.
                    var loginWindow = new LoginWindow();
                    MainWindow = loginWindow;
                    loginWindow.ShowDialog();

                    if (!loginWindow.LoginSuccess || !UserSession.IsLoggedIn)
                    {
                        Shutdown();
                        return;
                    }
                }

                // Begin the credential warm-up before constructing the workspace.
                // MainWindow shares this in-flight request and starts its audio
                // process in its constructor, giving the engine the entire window
                // creation/render interval to become ready before a user can press Space.
                _ = UserSession.EnsureSpeechmaticsKeyAsync(DeviceIdentity.Current);

                var window = new MainWindow();
                MainWindow = window;
                window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();
                window.Focus();

                if (interactiveLogin)
                {
                    // The Google OAuth callback returns through the system browser.
                    // Pulse the new workspace above it once so the user lands inside
                    // Replysis without manually minimizing Chrome or Edge. The main
                    // window's own pin preference remains unchanged afterward.
                    window.Topmost = true;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        window.Topmost = false;
                        window.Activate();
                        window.Focus();
                    }, DispatcherPriority.ApplicationIdle);
                }

                // Back to the ordinary rule now that there is something on screen:
                // closing the app's windows closes the app.
                ShutdownMode = ShutdownMode.OnLastWindowClose;
            }
            catch (Exception ex)
            {
                // Nothing above this catches it. The window is created from an
                // async continuation rather than by the framework, so a throw here
                // would be an unobserved task exception, and with the shutdown rule
                // still set to explicit the process would sit alive with no window
                // and no way out but Task Manager. Say what happened and leave.
                LogCrash("STARTUP", ex);
                MessageBox.Show(
                    "Replysis could not start.\n\n" + ex.Message,
                    "Replysis AI", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        /// <summary>
        /// Present only because App.xaml needs a Startup handler to replace the
        /// StartupUri it used to carry. The work is in OnStartup.
        /// </summary>
        private void OnStartupHook(object sender, StartupEventArgs e) { }

        // ── UI thread ────────────────────────────────────────────────────────
        // Only faults we can name are swallowed. Treating every exception as
        // recoverable would keep the app alive on top of corrupt state, which is
        // how a session ends up half-written or a later save silently fails.
        // Anything unrecognised is treated as fatal: flush what is safe to flush,
        // say so plainly, and exit without the raw .NET crash dialog.
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogCrash("UI", e.Exception);

            if (IsRecoverable(e.Exception))
            {
                // Survived and logged, nothing shown. "Replysis recovered from an unexpected problem" gave the
                // person nothing to do, and on a first launch (before any window exists) it was a dialog the
                // owner's own testers saw on every start (2026-10-01). The report still goes to the server, so the
                // cause gets fixed without anyone being interrupted.
                e.Handled = true;
                return;
            }

            e.Handled = true;          // suppress the default crash dialog only
            ShutdownAfterFatal();      // then leave deliberately
        }

        /// <summary>
        /// Whether the app can keep running after this fault.
        ///
        /// This used to name the survivable exceptions and treat everything else
        /// as fatal. That inverted the risk for this product. A
        /// NullReferenceException in any handler, the commonest fault in .NET,
        /// closed the whole app, and the moment that hurts most is the one it is
        /// most likely to happen: mid-interview, with the user relying on it.
        /// Losing the session costs them the interview. Carrying on with a
        /// missed click or a blank panel usually costs them nothing.
        ///
        /// So only faults that genuinely cannot be continued from are fatal now:
        /// the process is out of memory, the runtime state is corrupt, or the
        /// deployment is broken. Everything else is logged and survived. This is
        /// safe for the user's work because the transcript is streamed to disk as
        /// it arrives and the recording is written by a separate process, so
        /// staying alive never risks more than it saves.
        /// </summary>
        private static bool IsRecoverable(Exception? ex) => ex switch
        {
            null                                 => false,
            OutOfMemoryException                 => false,
            System.Runtime.InteropServices.SEHException => false,
            AccessViolationException             => false,
            BadImageFormatException              => false,
            TypeLoadException                    => false,
            MissingMemberException               => false,
            _                                    => true,
        };

        /// <summary>
        /// Last rites: persist what can be persisted without touching suspect
        /// state, tell the user in plain language, then shut down cleanly.
        /// </summary>
        private void ShutdownAfterFatal()
        {
            // A crash flag is a single tiny write with no dependency on the
            // failing subsystem, so it is safe even now. Interview transcripts
            // are already streamed to disk as they arrive.
            try
            {
                string folder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "InterviewCopilot");
                System.IO.Directory.CreateDirectory(folder);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(folder, "crash.flag"),
                    DateTime.UtcNow.ToString("o"));
            }
            catch { /* nothing further to do if even this fails */ }

            try
            {
                MessageBox.Show(
                    "Replysis has to close.\n\n" +
                    "Anything already saved remains available. Please start Replysis again to continue.",
                    "Replysis AI", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }

            try { Shutdown(); }
            catch { Environment.Exit(1); }
        }

        // ── Unawaited Task ───────────────────────────────────────────────────
        // Observed and logged only. These are background faults the user cannot
        // act on, so a dialog would be noise.
        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogCrash("TASK", e.Exception);
            e.SetObserved();
        }

        // ── Non-UI thread ────────────────────────────────────────────────────
        // Not recoverable by contract; log so the cause is available afterwards.
        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            LogCrash("FATAL", e.ExceptionObject as Exception);
        }

        // Technical detail goes to the debug log for developers only; it is never
        // shown to the user.
        private static void LogCrash(string source, Exception? ex)
        {
            try
            {
                DebugWindow.Log("CRASH", $"[{source}] {ex?.GetType().Name}: {ex?.Message}");
                if (ex?.StackTrace != null) DebugWindow.Log("CRASH", ex.StackTrace);
                ClientErrorReporter.Report(source, ex);
            }
            catch { /* logging must never itself crash the handler */ }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // The last line of the run, and the one that answers "why is it not
            // open any more". Written first, before any teardown can throw and
            // take the answer with it. The log survives the next launch now, so
            // this is readable after the fact rather than only in the moment.
            DebugWindow.Log("EXIT", $"Process exiting, code {e.ApplicationExitCode}");

            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;

            if (_ownsSingleInstanceMutex)
                _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();

            // Last, once the app has finished saving and is on its way out. The
            // updater waits for this process to disappear and then swaps the files
            // in, so the new version is simply there the next time Replysis opens.
            // Doing it here rather than mid-session is the whole point: an update
            // can never take the app away from someone during an interview.
            AppUpdates.ApplyOnExit();

            base.OnExit(e);
        }
    }
}
