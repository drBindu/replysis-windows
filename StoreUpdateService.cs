using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Services.Store;

namespace InterviewCopilot
{
    /// <summary>
    /// Talks to the Microsoft Store about updates to this app.
    ///
    /// Every method here is a no-op unless the running copy was installed from
    /// the Store, so the direct-download build and developer builds behave
    /// exactly as they did before this file existed. Nothing here is ever called
    /// on a timer or from the running app: <see cref="StoreUpdateRules"/> says
    /// why, and the launch gate is the only caller.
    ///
    /// Two things about the Store are worth knowing before changing this:
    ///
    ///   - A package update closes the app to apply. That is the Store's
    ///     behaviour, not ours, and it is the whole reason the check happens
    ///     before the main window exists.
    ///   - "Mandatory" is a flag set on the submission in Partner Center. Only
    ///     an update marked there blocks a launch here, which keeps the decision
    ///     in the release you publish rather than in code someone has to ship an
    ///     update to change.
    /// </summary>
    internal static class StoreUpdateService
    {
        private static bool _checkedThisProcess;
        private static StoreContext? _context;

        /// <summary>
        /// True when this copy carries a package identity, which is what the
        /// Store APIs need and what distinguishes a Store install from the .exe.
        ///
        /// Asked through the Win32 call rather than Package.Current because the
        /// latter throws for an unpackaged app, and the unpackaged case is the
        /// normal one during development.
        /// </summary>
        internal static bool IsStoreInstall
        {
            get
            {
                try
                {
                    int length = 0;
                    // 15700L is APPMODEL_ERROR_NO_PACKAGE: no identity, so not a
                    // Store install. Anything else means there is one.
                    return GetCurrentPackageFullName(ref length, null) != 15700L;
                }
                catch
                {
                    // The API is absent only on Windows versions this app does
                    // not support. Treat it as "not a Store install".
                    return false;
                }
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetCurrentPackageFullName(ref int packageFullNameLength,
                                                             System.Text.StringBuilder? packageFullName);

        /// <summary>What the launch check found.</summary>
        internal enum LaunchCheck
        {
            /// <summary>Not a Store install, or the check has already run.</summary>
            NotApplicable,
            /// <summary>Nothing waiting, or nothing that blocks. Carry on.</summary>
            Clear,
            /// <summary>An update Microsoft marked mandatory is waiting.</summary>
            MandatoryWaiting,
        }

        /// <summary>The updates found by the last check, held for the install step.</summary>
        private static IReadOnlyList<StorePackageUpdate>? _pending;

        /// <summary>The version being offered, for the gate to show.</summary>
        internal static string? PendingVersion { get; private set; }

        /// <summary>
        /// Asks the Store whether an update is waiting. Runs once per process,
        /// at launch, and answers <see cref="LaunchCheck.NotApplicable"/> every
        /// time after that.
        ///
        /// Never throws and never blocks longer than
        /// <see cref="StoreUpdateRules.LaunchCheckTimeout"/>: a Store that is
        /// slow or unreachable must cost the user nothing but the wait.
        /// </summary>
        internal static async Task<LaunchCheck> CheckAtLaunchAsync(IntPtr ownerWindow, bool mainWindowShown)
        {
            if (!StoreUpdateRules.MayCheckNow(IsStoreInstall, _checkedThisProcess, mainWindowShown))
                return LaunchCheck.NotApplicable;

            _checkedThisProcess = true;

            try
            {
                StoreContext ctx = GetContext(ownerWindow);

                using var timeout = new CancellationTokenSource(StoreUpdateRules.LaunchCheckTimeout);
                IReadOnlyList<StorePackageUpdate> updates =
                    await ctx.GetAppAndOptionalStorePackageUpdatesAsync().AsTask(timeout.Token).ConfigureAwait(false);

                if (updates == null || updates.Count == 0)
                {
                    DebugWindow.Log("UPDATE", "Store: up to date");
                    return LaunchCheck.Clear;
                }

                bool mandatory = false;
                foreach (StorePackageUpdate u in updates)
                    if (u.Mandatory) { mandatory = true; break; }

                _pending = updates;
                PendingVersion = DescribeVersion(updates);

                DebugWindow.Log("UPDATE",
                    $"Store: {updates.Count} update(s) waiting, mandatory={mandatory}, version={PendingVersion ?? "unknown"}");

                // An optional update is deliberately left alone. The Store's own
                // background service installs it when the app is closed, which
                // is the one moment it cannot cost anybody an interview.
                return StoreUpdateRules.ShouldBlockLaunch(anyUpdate: true, anyMandatory: mandatory)
                    ? LaunchCheck.MandatoryWaiting
                    : LaunchCheck.Clear;
            }
            catch (OperationCanceledException)
            {
                DebugWindow.Log("UPDATE", "Store check timed out; starting normally");
                return LaunchCheck.Clear;
            }
            catch (Exception ex)
            {
                // The HRESULT is named because the message is routinely empty.
                // A copy that is packaged but not installed from the Store, which
                // is every developer build and every sideload, fails here with
                // nothing to read, and without the number there is no way to tell
                // that apart from a real fault on a customer's machine.
                DebugWindow.Log("UPDATE",
                    $"Store check failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}".TrimEnd());
                return LaunchCheck.Clear;
            }
        }

        /// <summary>
        /// Downloads and installs what the check found. The Store closes the app
        /// to apply the package, so this usually does not return; when it does,
        /// the bool says whether the update actually went in.
        ///
        /// Only ever called from the launch gate, after the user pressed Update.
        /// </summary>
        internal static async Task<bool> DownloadAndInstallAsync(IntPtr ownerWindow,
                                                                 IProgress<double>? progress = null)
        {
            IReadOnlyList<StorePackageUpdate>? updates = _pending;
            if (updates == null || updates.Count == 0) return false;

            try
            {
                StoreContext ctx = GetContext(ownerWindow);

                IAsyncOperationWithProgressBridge op = Bridge(
                    ctx.RequestDownloadAndInstallStorePackageUpdatesAsync(updates), progress);

                using var timeout = new CancellationTokenSource(StoreUpdateRules.InstallTimeout);
                StorePackageUpdateResult result = await op.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

                bool ok = result.OverallState == StorePackageUpdateState.Completed;
                DebugWindow.Log("UPDATE", $"Store install finished: {result.OverallState}");
                return ok;
            }
            catch (OperationCanceledException)
            {
                DebugWindow.Log("UPDATE", "Store install timed out");
                return false;
            }
            catch (Exception ex)
            {
                DebugWindow.Log("UPDATE", $"Store install failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Opens this app's page in the Store. The fallback offered when an
        /// in-app update cannot be completed, so the user is never left with
        /// nothing to press.
        /// </summary>
        internal static void OpenStoreListing()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "ms-windows-store://pdp/?productid=9N13GQC3MKK9") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                DebugWindow.Log("UPDATE", $"Could not open the Store: {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// The Store context, told which window owns any dialog it shows.
        ///
        /// A desktop app has to supply that handle itself; without it the call
        /// throws, which is why the gate window is created before the check runs
        /// rather than after it.
        /// </summary>
        private static StoreContext GetContext(IntPtr ownerWindow)
        {
            if (_context == null)
            {
                StoreContext context = StoreContext.GetDefault();
                if (ownerWindow != IntPtr.Zero)
                {
                    // Through the interop helper, not a cast.
                    //
                    // A hand-declared [ComImport] IInitializeWithWindow and a
                    // cast is the pattern every sample from the C++/WRL era
                    // shows, and under CsWinRT it throws: StoreContext is a
                    // projected .NET class, not a COM callable wrapper, so the
                    // cast is invalid. It failed every check with
                    // InvalidCastException, which the catch below turned into
                    // "carry on" - a mandatory update would never have been
                    // shown to anybody, and nothing would have looked wrong.
                    WinRT.Interop.InitializeWithWindow.Initialize(context, ownerWindow);
                }
                _context = context;
            }
            return _context;
        }

        /// <summary>The highest version among the waiting updates, for display.</summary>
        private static string? DescribeVersion(IReadOnlyList<StorePackageUpdate> updates)
        {
            try
            {
                Version? best = null;
                foreach (StorePackageUpdate u in updates)
                {
                    var id = u.Package?.Id;
                    if (id == null) continue;
                    var v = id.Version;
                    var candidate = new Version(v.Major, v.Minor, v.Build, v.Revision);
                    if (best == null || candidate > best) best = candidate;
                }
                return best?.ToString(3);
            }
            catch { return null; }
        }

        // ── WinRT plumbing ───────────────────────────────────────────────────

        /// <summary>
        /// Turns the Store's progress-reporting async operation into a plain
        /// Task plus progress callbacks, so the gate can show a bar without
        /// knowing anything about WinRT.
        /// </summary>
        private readonly struct IAsyncOperationWithProgressBridge
        {
            internal IAsyncOperationWithProgressBridge(Task<StorePackageUpdateResult> task) { Task = task; }
            internal Task<StorePackageUpdateResult> Task { get; }
        }

        private static IAsyncOperationWithProgressBridge Bridge(
            Windows.Foundation.IAsyncOperationWithProgress<StorePackageUpdateResult, StorePackageUpdateStatus> op,
            IProgress<double>? progress)
        {
            if (progress != null)
            {
                op.Progress = (_, status) =>
                {
                    try { progress.Report(status.PackageDownloadProgress); } catch { }
                };
            }
            return new IAsyncOperationWithProgressBridge(op.AsTask());
        }
    }
}
