using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InterviewCopilot
{
    public class GlobalHotkey : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int VK_SPACE = 0x20;
        private const int VK_F8  = 0x77;   // F8  = Analyze all screens (global)
        private const int VK_F9  = 0x78;   // F9  = Analyze primary screen only (global)
        private const int VK_F12 = 0x7B;
        private const int VK_F4  = 0x73;   // Ctrl+Shift+F4 = kill app (no tray, no taskbar)

        private IntPtr _hookId = IntPtr.Zero;
        private readonly LowLevelKeyboardProc _proc;
        private readonly Action _onSpacePressed;
        private readonly Action? _onF12Pressed;
        private readonly Action? _onKillPressed;
        private readonly Action? _onScreenAnalysisPressed;
        private readonly Action? _onPrimaryScreenAnalysisPressed;

        public IntPtr OwnerWindowHandle { get; set; } = IntPtr.Zero;
        public bool Enabled { get; set; } = true;

        public GlobalHotkey(
            Action onSpacePressed,
            Action? onF12Pressed = null,
            Action? onKillPressed = null,
            Action? onScreenAnalysisPressed = null,
            Action? onPrimaryScreenAnalysisPressed = null)
        {
            _onSpacePressed                  = onSpacePressed;
            _onF12Pressed                    = onF12Pressed;
            _onKillPressed                   = onKillPressed;
            _onScreenAnalysisPressed         = onScreenAnalysisPressed;
            _onPrimaryScreenAnalysisPressed  = onPrimaryScreenAnalysisPressed;
            _proc = HookCallback;
            _hookId = SetHook(_proc);

            if (_hookId == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                DebugWindow.Log("HOOK", $"SetWindowsHookEx FAILED! Error code: {err}");
            }
            else
            {
                DebugWindow.Log("HOOK", $"SetWindowsHookEx SUCCESS. Hook ID: {_hookId}");
            }
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            DebugWindow.Log("HOOK", $"Installing hook with module: {curModule.ModuleName}");
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc,
                GetModuleHandle(curModule.ModuleName), 0);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                int vkCode = Marshal.ReadInt32(lParam);

                // F8 — Analyze all screens (fires globally, always, regardless of focus)
                if (vkCode == VK_F8 && _onScreenAnalysisPressed != null)
                {
                    DebugWindow.Log("HOOK", "F8 detected → screen analysis (all monitors)");
                    _onScreenAnalysisPressed.Invoke();
                }

                // F9 — Analyze primary screen only (faster, smaller payload)
                if (vkCode == VK_F9 && _onPrimaryScreenAnalysisPressed != null)
                {
                    DebugWindow.Log("HOOK", "F9 detected → screen analysis (primary only)");
                    _onPrimaryScreenAnalysisPressed.Invoke();
                }

                // F12 — toggle debug window only when app is NOT focused
                if (vkCode == VK_F12 && _onF12Pressed != null)
                {
                    IntPtr fg = GetForegroundWindow();
                    if (OwnerWindowHandle == IntPtr.Zero || fg != OwnerWindowHandle)
                        _onF12Pressed.Invoke();
                }

                // Ctrl+Shift+F4 � kill app completely (since no tray, no taskbar)
                if (vkCode == VK_F4)
                {
                    bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0; // VK_CONTROL
                    bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0; // VK_SHIFT
                    if (ctrl && shift && _onKillPressed != null)
                    {
                        DebugWindow.Log("HOOK", "Ctrl+Shift+F4 detected � killing app");
                        _onKillPressed.Invoke();
                    }
                }

                // Space � only when app is NOT focused
                if (vkCode == VK_SPACE && Enabled)
                {
                    IntPtr foreground = GetForegroundWindow();

                    if (OwnerWindowHandle != IntPtr.Zero && foreground == OwnerWindowHandle)
                    {
                        // App is focused � PreviewKeyDown handles it
                    }
                    else
                    {
                        DebugWindow.Log("HOOK", $"SPACE detected, app NOT focused � firing global callback");
                        _onSpacePressed?.Invoke();
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                DebugWindow.Log("HOOK", "Hook disposed");
                _hookId = IntPtr.Zero;
            }
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
    }
}