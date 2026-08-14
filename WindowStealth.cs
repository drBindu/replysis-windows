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
