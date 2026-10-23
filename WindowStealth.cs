using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;

namespace InterviewCopilot
{
    public static class WindowStealth
    {
        #region Windows API
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowDisplayAffinity(IntPtr hwnd, out uint pdwAffinity);

        private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd,
            int objectId, int childId, uint eventThread, uint eventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
            WinEventDelegate callback, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        private const uint WDA_NONE = 0x00000000;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        private const uint WDA_MONITOR = 0x00000001;   // the window shows as a black box in a capture

        private static int _hideFailureReports;
        private static int _warned;

        /// <summary>Set once Windows refuses full hiding on this computer: true when the black box took over, false when nothing hides.</summary>
        internal static bool? HidingRefusedBlackBox { get; private set; }

        /// <summary>Raised (once, on any thread) when full hiding is refused, so the user is warned before they share their screen.</summary>
        internal static event Action<bool>? HidingRefused;

        /// <summary>
        /// Windows before 10 version 2004 (build 19041) accepts the hiding request but only blacks the window out, and
        /// says nothing; treated the same as a refusal so the user is warned.
        /// </summary>
        internal static bool HidingUnsupportedByWindows => Environment.OSVersion.Version.Build < 19041;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const int WS_EX_LAYERED = 0x00080000;

        /// <summary>
        /// Hides a window from screen capture. When full exclusion is refused, the black-box level (WDA_MONITOR) is used
        /// so a screen share shows a black rectangle instead of the answers.
        ///
        /// Three Windows 10 computers refused with error 8 (2026-10-22 and 23, none on Windows 11), and the report could not
        /// say which window: the guard hides every window of the app, including dropdowns and tooltips, and the first refusal
        /// of any of them raised the warning. Now each report names the window (its kind, whether it is layered, its size),
        /// and the customer is warned only when a window that shows answers (the main window or the answer window) is the one
        /// refused. A refused dropdown or tooltip is still reported, as minor, so the cause can be found from real computers.
        /// </summary>
        private static bool ApplyHide(IntPtr hwnd)
        {
            if (SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE) != 0) return true;
            int err = Marshal.GetLastWin32Error();
            bool blackBox = SetWindowDisplayAffinity(hwnd, WDA_MONITOR) != 0;
            var (kind, showsAnswers) = Describe(hwnd);
            DebugWindow.Log("STEALTH", $"Full hiding refused for {kind} (err {err}); black box {(blackBox ? "applied" : "also refused")}.");
            if (System.Threading.Interlocked.Increment(ref _hideFailureReports) <= 3)
            {
                string gpu = GraphicsDriverName();
                ClientErrorReporter.NoteEngineLine($">>> STEALTH: full hiding refused for {kind} (err {err}); black box {(blackBox ? "applied" : "refused")}; graphics device: {gpu}; Windows {Environment.OSVersion.Version}");
                ClientErrorReporter.ReportProblem("stealth", showsAnswers ? "stealth-refused" : "stealth-refused-minor",
                    $"Hiding from screen share was refused for {kind} (err {err}); black box {(blackBox ? "on" : "also refused")}; graphics: {gpu}");
            }
            if (showsAnswers && System.Threading.Interlocked.Exchange(ref _warned, 1) == 0)
            {
                HidingRefusedBlackBox = blackBox;
                try { HidingRefused?.Invoke(blackBox); } catch { }
            }
            return blackBox;
        }

        /// <summary>What Windows says a window's capture setting is now: hidden, black box, or none. For the self-test's report.</summary>
        internal static string AffinityOf(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || GetWindowDisplayAffinity(hwnd, out uint a) == 0) return "unknown";
                return a == WDA_EXCLUDEFROMCAPTURE ? "hidden" : a == WDA_MONITOR ? "black box" : "none";
            }
            catch { return "unknown"; }
        }

        /// <summary>"MainWindow, layered, 930x560" and whether that window shows answers. Never fails.</summary>
        private static (string kind, bool showsAnswers) Describe(IntPtr hwnd)
        {
            string name = "";
            bool answers = false;
            try
            {
                if (Application.Current?.Dispatcher.CheckAccess() == true && HwndSource.FromHwnd(hwnd)?.RootVisual is Window w)
                {
                    name = w.GetType().Name;
                    answers = w is MainWindow || w is AnswerWindow;
                }
                else if (HwndSource.FromHwnd(hwnd) == null)
                {
                    var cls = new System.Text.StringBuilder(64);
                    GetClassName(hwnd, cls, cls.Capacity);
                    name = "class " + cls;
                }
                else name = "a popup";   // a WPF dropdown or tooltip, or a window looked at off the UI thread
            }
            catch { name = "unknown"; }
            try
            {
                bool layered = (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_LAYERED) != 0;
                string size = GetWindowRect(hwnd, out RECT r) ? $"{r.Right - r.Left}x{r.Bottom - r.Top}" : "?";
                return ($"{name}, {(layered ? "layered" : "not layered")}, {size}", answers);
            }
            catch { return (name, answers); }
        }

        /// <summary>The display adapter's name from the registry, e.g. "Microsoft Basic Display Adapter" when no driver is installed.</summary>
        internal static string GraphicsDriverName()
        {
            try
            {
                using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (cls == null) return "unknown";
                var names = new System.Collections.Generic.List<string>();
                foreach (string sub in cls.GetSubKeyNames())
                {
                    if (sub.Length != 4) continue;
                    using var k = cls.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc") is string d && !names.Contains(d)) names.Add(d);
                }
                return names.Count == 0 ? "unknown" : string.Join(" + ", names);
            }
            catch { return "unknown"; }
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint WINEVENT_OUTOFCONTEXT = 0;

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        private const uint GA_ROOT = 2;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);
        #endregion

        // ── Global guard ────────────────────────────────────────────────────────
        // Every top-level window this process shows is hidden from capture, whatever made it: WPF windows, the mode menu
        // and Compact menu popups, tooltips, context menus, message boxes, the update window. The per-window calls only
        // covered windows that remembered to call SetStealthMode, and popups are separate windows, so the mode menu could
        // appear in a screen share (owner, 2026-10-21: "no window has to be visible"). A hook catches each window the
        // moment it shows; a once-a-second sweep catches anything the hook missed.
        private static WinEventDelegate? _guardCallback;   // kept alive: the hook calls it from native code
        private static IntPtr _guardHook = IntPtr.Zero;
        private static System.Windows.Threading.DispatcherTimer? _guardSweep;
        private static Func<bool>? _guardEnabled;

        public static void StartGlobalGuard(Func<bool> stealthOn)
        {
            _guardEnabled = stealthOn;
            if (_guardHook != IntPtr.Zero) return;
            uint pid = (uint)Environment.ProcessId;
            _guardCallback = (hook, evt, hwnd, objectId, childId, thread, time) =>
            {
                if (objectId != 0 || hwnd == IntPtr.Zero) return;   // OBJID_WINDOW only
                GuardOne(hwnd);
            };
            _guardHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _guardCallback, pid, 0, WINEVENT_OUTOFCONTEXT);
            _guardSweep = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _guardSweep.Tick += (_, _) => SweepAll();
            _guardSweep.Start();
            SweepAll();
        }

        private static void GuardOne(IntPtr hwnd)
        {
            try
            {
                if (_guardEnabled == null || !_guardEnabled()) return;
                if (GetAncestor(hwnd, GA_ROOT) != hwnd) return;      // only top-level windows can be excluded
                GetWindowThreadProcessId(hwnd, out uint owner);
                if (owner != (uint)Environment.ProcessId) return;
                if (GetWindowDisplayAffinity(hwnd, out uint current) != 0 &&
                    (current == WDA_EXCLUDEFROMCAPTURE || current == WDA_MONITOR)) return;
                ApplyHide(hwnd);
            }
            catch { }
        }

        private static void SweepAll()
        {
            try
            {
                if (_guardEnabled == null || !_guardEnabled()) return;
                uint pid = (uint)Environment.ProcessId;
                EnumWindows((hwnd, _) =>
                {
                    GetWindowThreadProcessId(hwnd, out uint owner);
                    if (owner == pid && IsWindowVisible(hwnd)) GuardOne(hwnd);
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>
        /// Apply (or clear) screen-capture exclusion on a raw HWND. WPF Popups (dropdowns,
        /// menus) live in their OWN top-level window, so the main window's stealth does NOT
        /// cover them — without this they flash into screen recordings even in stealth mode.
        /// </summary>
        public static bool TrySetCaptureExclusion(IntPtr hwnd, bool enable)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                return enable ? ApplyHide(hwnd) : SetWindowDisplayAffinity(hwnd, WDA_NONE) != 0;
            }
            catch (Exception ex)
            {
                DebugWindow.Log("STEALTH", "SetCaptureExclusion error: " + ex.Message);
                return false;
            }
        }

        public static void SetCaptureExclusion(IntPtr hwnd, bool enable)
            => TrySetCaptureExclusion(hwnd, enable);

        /// <summary>
        /// Runs a native common dialog while applying capture exclusion to every
        /// top-level window this process shows during the call.
        /// </summary>
        public static T ShowNativeDialogProtected<T>(Func<T> show)
        {
            if (!SettingsWindow.GetStealthMode()) return show();

            uint thisProcess = (uint)Process.GetCurrentProcess().Id;
            WinEventDelegate callback = (_, _, hwnd, objectId, _, _, _) =>
            {
                if (hwnd == IntPtr.Zero || objectId != 0) return;
                GetWindowThreadProcessId(hwnd, out uint ownerProcess);
                if (ownerProcess == thisProcess) TrySetCaptureExclusion(hwnd, true);
            };
            IntPtr hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero,
                callback, thisProcess, 0, WINEVENT_OUTOFCONTEXT);
            try { return show(); }
            finally
            {
                if (hook != IntPtr.Zero) UnhookWinEvent(hook);
                GC.KeepAlive(callback);
            }
        }

        /// <summary>
        /// Makes a window invisible to screen capture so a screenshot can be taken
        /// without hiding it first.
        ///
        /// This is what lets Screen Analyze feel instant. Hiding a window and
        /// waiting for the compositor is visible to the user as the app blinking
        /// out and back, every single capture. A window carrying
        /// WDA_EXCLUDEFROMCAPTURE is simply absent from the copied pixels, so
        /// there is nothing to hide and nothing to wait for.
        ///
        /// Returns false when Windows will not apply it, which means the caller
        /// must fall back to hiding. That happens below Windows 10 version 2004,
        /// where the flag does not exist.
        /// </summary>
        /// <param name="alreadyExcluded">
        /// True when the window was already excluded, usually because Stealth mode
        /// is on. The caller can then capture with no wait at all, since the screen
        /// has never contained the window. When false the flag was just applied and
        /// the caller should let one frame compose before capturing.
        /// </param>
        /// <summary>
        /// Whether this window can be hidden from a screen capture at all.
        ///
        /// Asked before doing something repeatedly. Where the answer is no, the
        /// caller falls back to dropping the opacity, which is fine once and a
        /// flickering window when it happens every two seconds.
        ///
        /// Sets the flag and puts it back, because there is no way to ask
        /// without trying. A window already excluded is left alone.
        /// </summary>
        public static bool CanHideFromCapture(Window? window)
        {
            if (window == null) return false;
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return false;

                if (GetWindowDisplayAffinity(hwnd, out uint current) != 0 &&
                    current == WDA_EXCLUDEFROMCAPTURE)
                    return true;

                if (SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE) == 0) return false;
                SetWindowDisplayAffinity(hwnd, WDA_NONE);
                return true;
            }
            catch { return false; }
        }

        public static bool TryBeginCaptureHidden(Window? window, out bool alreadyExcluded)
        {
            alreadyExcluded = false;
            if (window == null) return false;

            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return false;

                if (GetWindowDisplayAffinity(hwnd, out uint current) != 0 &&
                    current == WDA_EXCLUDEFROMCAPTURE)
                {
                    alreadyExcluded = true;
                    return true;
                }

                return SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE) != 0;
            }
            catch (Exception ex)
            {
                DebugWindow.Log("STEALTH", "TryBeginCaptureHidden error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Undoes <see cref="TryBeginCaptureHidden"/>. Does nothing when the window
        /// was already excluded before the capture, so a user running in Stealth
        /// mode is never quietly taken out of it by taking a screenshot.
        /// </summary>
        public static void EndCaptureHidden(Window? window, bool wasAlreadyExcluded)
        {
            if (window == null || wasAlreadyExcluded) return;

            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero) SetWindowDisplayAffinity(hwnd, WDA_NONE);
            }
            catch (Exception ex)
            {
                DebugWindow.Log("STEALTH", "EndCaptureHidden error: " + ex.Message);
            }
        }

        public static void SetStealthMode(Window window, bool enable)
        {
            if (window == null) return;
            try
            {
                // Always set WPF property immediately (works even before HWND handle creation)
                window.ShowInTaskbar = !enable;

                void ApplyHwndStealth()
                {
                    try
                    {
                        var helper = new WindowInteropHelper(window);
                        IntPtr hwnd = helper.Handle;
                        if (hwnd == IntPtr.Zero) return;

                        if (enable)
                        {
                            window.ShowInTaskbar = false;
                            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                            exStyle = (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
                            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

                            // Exclude from screen capture (OBS, Teams, screen share, etc.)
                            ApplyHide(hwnd);
                        }
                        else
                        {
                            // If window is AnswerWindow, keep ShowInTaskbar = false
                            if (!(window is AnswerWindow))
                            {
                                window.ShowInTaskbar = true;
                            }
                            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                            exStyle = (exStyle & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW;
                            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

                            SetWindowDisplayAffinity(hwnd, WDA_NONE);
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log("STEALTH", "ApplyHwndStealth error: " + ex.Message);
                    }
                }

                var helper = new WindowInteropHelper(window);
                if (helper.Handle == IntPtr.Zero)
                {
                    EventHandler? handler = null;
                    handler = (s, e) =>
                    {
                        window.SourceInitialized -= handler;
                        ApplyHwndStealth();
                    };
                    window.SourceInitialized += handler;
                }
                else
                {
                    ApplyHwndStealth();
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log("STEALTH", "SetStealthMode error: " + ex.Message);
            }
        }
    }
}
