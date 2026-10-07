using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InterviewCopilot
{
    /// <summary>
    /// Keeps Windows from putting the app, or the speech engine it runs, to sleep.
    ///
    /// Two different things put a program to sleep, and both are switched off here.
    ///
    /// The first is power throttling. Windows 11 quietly runs programs it thinks nobody is looking at in "efficiency mode": a
    /// lower clock, timers that fire late and in batches. An interview app is exactly that program, since the meeting is in front
    /// and this window is underneath or hidden from the capture. The speech engine then answers late for no visible reason.
    /// Opting out costs nothing while the computer is plugged in and very little on battery, and it only applies to this program.
    ///
    /// The second is the computer itself going to sleep. During an interview it must not, whatever the idle timer says: nobody
    /// moves the mouse for forty minutes of a call. That is held only while a session is running and let go the moment it ends,
    /// so a laptop left open overnight still sleeps normally.
    /// </summary>
    internal static class AlwaysReady
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessPowerThrottlingState
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        private const int ProcessPowerThrottling = 4;
        private const uint ProcessPowerThrottlingCurrentVersion = 1;
        private const uint ExecutionSpeed = 0x1;           // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
        private const uint IgnoreTimerResolution = 0x4;    // PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr process, int informationClass,
            ref ProcessPowerThrottlingState information, int size);

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint flags);

        private const uint EsContinuous = 0x80000000;
        private const uint EsSystemRequired = 0x00000001;

        /// <summary>Tells Windows never to throttle this process or its timers. Safe to call more than once.</summary>
        internal static bool OptOutOfThrottling(Process process)
        {
            try
            {
                // ControlMask names what is being decided; StateMask 0 means "do not throttle it".
                var state = new ProcessPowerThrottlingState
                {
                    Version = ProcessPowerThrottlingCurrentVersion,
                    ControlMask = ExecutionSpeed | IgnoreTimerResolution,
                    StateMask = 0,
                };
                bool ok = SetProcessInformation(process.Handle, ProcessPowerThrottling, ref state,
                                                Marshal.SizeOf<ProcessPowerThrottlingState>());
                if (!ok)
                    DebugWindow.Log("POWER", $"Could not opt out of throttling (error {Marshal.GetLastWin32Error()}); older Windows ignores this.");
                return ok;
            }
            catch (Exception ex)
            {
                DebugWindow.Log("POWER", $"Opt-out of throttling skipped: {ex.GetType().Name}");
                return false;
            }
        }

        private static bool _holding;

        /// <summary>
        /// Holds the computer awake (the screen may still turn off) for as long as a session runs, or lets go.
        /// Must be called from the same thread each time, because Windows ties the request to the thread that made it.
        /// </summary>
        internal static void HoldAwake(bool hold)
        {
            if (hold == _holding) return;
            try
            {
                SetThreadExecutionState(hold ? (EsContinuous | EsSystemRequired) : EsContinuous);
                _holding = hold;
                DebugWindow.Log("POWER", hold
                    ? "Session running: the computer will not go to sleep until it ends."
                    : "Session over: the computer may sleep again.");
            }
            catch (Exception ex)
            {
                DebugWindow.Log("POWER", $"Keeping the computer awake failed: {ex.GetType().Name}");
            }
        }
    }
}
