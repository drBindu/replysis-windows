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

        public static void SetStealthMode(Window window, bool enable)
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
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
            }
            else
            {
                window.ShowInTaskbar = true;

                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                exStyle = (exStyle & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW;
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

                // Restore normal capture visibility
                SetWindowDisplayAffinity(hwnd, WDA_NONE);
            }
        }
   }
}