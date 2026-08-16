using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InterviewCopilot
{
    public class GlobalHotkey : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP   = 0x0101;
        private const int VK_SPACE = 0x20;
        private const int VK_F8  = 0x77;   // F8  = Analyze the active screen (global)
        private const int VK_F9  = 0x78;   // F9  = Analyze primary screen only (global)
        private const int VK_F12 = 0x7B;
        private const int VK_F4  = 0x73;   // Ctrl+Shift+F4 = kill app (no tray, no taskbar)

        private IntPtr _hookId = IntPtr.Zero;
        private readonly LowLevelKeyboardProc _proc;
        private readonly Action _onSpacePressed;   // Space TOGGLE → start/stop listening
        private readonly Action? _onSpaceReleased;
        private readonly Action? _onF12Pressed;
        private readonly Action? _onKillPressed;
        private readonly Action? _onScreenAnalysisPressed;
        private readonly Action? _onPrimaryScreenAnalysisPressed;

        private bool _spaceDown = false;
        private bool _f8Down = false;
        private bool _f9Down = false;
        private bool _f12Down = false;
        private bool _killChordDown = false;

        public IntPtr OwnerWindowHandle { get; set; } = IntPtr.Zero;
        public bool Enabled { get; set; } = true;

        public GlobalHotkey(
            Action onSpacePressed,
            Action? onSpaceReleased = null,
            Action? onF12Pressed = null,
            Action? onKillPressed = null,
            Action? onScreenAnalysisPressed = null,
            Action? onPrimaryScreenAnalysisPressed = null)
        {
            _onSpacePressed                  = onSpacePressed;
            _onSpaceReleased                 = onSpaceReleased;
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
            using var curModule = curProcess.MainModule
                ?? throw new InvalidOperationException("MainModule is null — cannot install keyboard hook");
            DebugWindow.Log("HOOK", $"Installing hook with module: {curModule.ModuleName}");
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc,
                GetModuleHandle(curModule.ModuleName), 0);
        }

        // KBDLLHOOKSTRUCT.flags bits marking a SendInput/keybd_event-synthesized key
        // rather than a real hardware press (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED).
        private const int LLKHF_INJECTED = 0x10;
        private const int LLKHF_LOWER_IL_INJECTED = 0x2;

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return CallNextHookEx(_hookId, nCode, wParam, lParam);

            int vkCode = Marshal.ReadInt32(lParam);
            int flags  = Marshal.ReadInt32(lParam, 8);
            bool isDown = wParam == (IntPtr)WM_KEYDOWN;
            bool isUp   = wParam == (IntPtr)WM_KEYUP;

            // Ignore synthetic key events entirely — e.g. dictation tools like Wispr Flow
            // "type" their transcribed text by simulating real keystrokes (spaces between
            // every word). Our WH_KEYBOARD_LL hook otherwise can't tell that apart from a
            // genuine hardware Space press, so it was toggling push-to-talk mid-sentence
            // every time another app on the system typed a space anywhere.
            if ((flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0)
                return CallNextHookEx(_hookId, nCode, wParam, lParam);

            if (isDown)
            {
                // F8 - Analyze the screen the user is working on
                if (vkCode == VK_F8 && !IsOwnerWindowForeground() && _onScreenAnalysisPressed != null)
                {
                    if (!_f8Down)
                    {
                        _f8Down = true;
                        DebugWindow.Log("HOOK", "F8 detected → screen analysis (active screen)");
                        _onScreenAnalysisPressed.Invoke();
                    }
                    return (IntPtr)1;
                }

                // F9 — Analyze primary screen only
                if (vkCode == VK_F9 && !IsOwnerWindowForeground() && _onPrimaryScreenAnalysisPressed != null)
                {
                    if (!_f9Down)
                    {
                        _f9Down = true;
                        DebugWindow.Log("HOOK", "F9 detected → screen analysis (primary only)");
                        _onPrimaryScreenAnalysisPressed.Invoke();
                    }
                    return (IntPtr)1;
                }

                // F12 — toggle debug window (only when app is NOT focused)
                if (vkCode == VK_F12 && !IsOwnerWindowForeground() && _onF12Pressed != null)
                {
                    if (!_f12Down)
                    {
                        _f12Down = true;
                        _onF12Pressed.Invoke();
                    }
                    return (IntPtr)1;
                }

                // Ctrl+Shift+F4 — kill app
                if (vkCode == VK_F4)
                {
                    bool ctrl  = (GetAsyncKeyState(0x11) & 0x8000) != 0;
                    bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
                    if (ctrl && shift && _onKillPressed != null)
                    {
                        if (!_killChordDown)
                        {
                            _killChordDown = true;
                            DebugWindow.Log("HOOK", "Ctrl+Shift+F4 detected — killing app");
                            _onKillPressed.Invoke();
                        }
                        return (IntPtr)1;
                    }
                }
            }

            // Space — toggle mode: press once to start listening, press again to answer.
            // Fires unconditionally, focused or not, via this single low-level hook — this is
            // the ONE path for Space now. It used to skip firing while the app window had
            // focus (deferring instead to a separate in-window WPF key handler), which meant
            // Space silently did nothing unless the user first clicked into the app. Since the
            // hook already debounces key-repeat via _spaceDown, unifying onto it removes that
            // focus-dependent inconsistency entirely.
            if (isUp)
            {
                if (vkCode == VK_F8) _f8Down = false;
                if (vkCode == VK_F9) _f9Down = false;
                if (vkCode == VK_F12) _f12Down = false;
                if (vkCode == VK_F4) _killChordDown = false;
            }

            if (vkCode == VK_SPACE && Enabled)
            {
                if (isDown && !_spaceDown)
                {
                    _spaceDown = true;
                    DebugWindow.Log("HOOK", "SPACE TOGGLE PRESS");
                    _onSpacePressed?.Invoke();
                }
                else if (isUp)
                {
                    _spaceDown = false;
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private bool IsOwnerWindowForeground()
        {
            if (OwnerWindowHandle == IntPtr.Zero) return false;
            return GetForegroundWindow() == OwnerWindowHandle;
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
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
