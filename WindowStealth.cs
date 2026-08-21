using System;
using System.Runtime.InteropServices;
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

        private const uint WDA_NONE = 0x00000000;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;
        #endregion

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
                bool applied = SetWindowDisplayAffinity(hwnd, enable ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE) != 0;
                if (!applied)
                    DebugWindow.Log("STEALTH", $"Capture exclusion was not applied (err {Marshal.GetLastWin32Error()}).");
                return applied;
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
                            if (SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE) == 0)
                            {
                                DebugWindow.Log("STEALTH",
                                    $"SetWindowDisplayAffinity failed (err {Marshal.GetLastWin32Error()}) — " +
                                    "window may be visible in screen capture. Requires Windows 10 v2004+.");
                            }
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
