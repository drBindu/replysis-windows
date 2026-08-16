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

        // Suppression window so a repeating fault (e.g. a timer throwing every
        // tick) cannot stack dozens of identical dialogs on top of each other.
        private static DateTime _lastCrashNoticeUtc = DateTime.MinValue;
        private static readonly TimeSpan CrashNoticeCooldown = TimeSpan.FromSeconds(30);
        private static readonly object _crashNoticeLock = new();

        protected override void OnStartup(StartupEventArgs e)
        {
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

            // The main window restores the saved Firebase session before requesting a
            // transcription credential. Do not prefetch here: doing so treats a returning
            // Pro user as a guest for a few milliseconds and can create a false 402 retry.
            base.OnStartup(e);
        }

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
                e.Handled = true;
                ShowRecoveryNotice();
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
            }
            catch { /* logging must never itself crash the handler */ }
        }

        // Calm, plain-language notice with no stack trace, exception text, or
        // internal detail. The wording is deliberately limited to work that has
        // already been written to disk: this handler cannot know whether the
        // operation that just failed had persisted anything.
        private static void ShowRecoveryNotice()
        {
            lock (_crashNoticeLock)
            {
                if (DateTime.UtcNow - _lastCrashNoticeUtc < CrashNoticeCooldown) return;
                _lastCrashNoticeUtc = DateTime.UtcNow;
            }

            // Deliberately not a MessageBox. This can fire at any moment, including
            // mid-interview: a dialog is a separate top-level window, so it never
            // carries the exclude-from-capture flag and would appear on a screen
            // share. MainWindow.Alert keeps the notice inside the hidden window,
            // and falls back to a dialog only before any window exists.
            try
            {
                InterviewCopilot.MainWindow.Alert(
                    "Replysis recovered from an unexpected problem",
                    "Anything already saved remains available. You can keep working.");
            }
            catch { /* a failed notice must not escalate into a second crash */ }
        }

        protected override void OnExit(ExitEventArgs e)
        {
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
            UpdateService.ApplyOnExit();

            base.OnExit(e);
        }
    }
}
