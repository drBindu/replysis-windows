using System;
using System.IO;
using System.Threading;
using System.Windows.Threading;

namespace InterviewCopilot
{
    /// <summary>
    /// Notices when the window stops responding and says so quietly (bug round 40, 2026-11-07).
    ///
    /// A hard freeze throws nothing, so none of the crash handlers ever saw it: the customer saw a window that would not
    /// move and the owner heard nothing. A background thread now posts an empty job to the window every two seconds. When
    /// one has waited more than ten seconds the debug log says so once, with the last thing the app logged, and a small
    /// freeze.flag is written. When the window answers again the flag goes and one report is sent; if the app is ended
    /// while frozen (Task Manager), the next launch finds the flag and reports it instead. Nothing is shown to the
    /// customer, and the cost is one empty job every two seconds.
    /// </summary>
    internal sealed class FreezeWatchdog
    {
        internal const string FlagName = "freeze.flag";
        internal static readonly TimeSpan PostEvery = TimeSpan.FromSeconds(2);
        internal static readonly TimeSpan FrozenAfter = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The report kind for a freeze of this length. Under 30 s is a stall worth seeing in admin but not an outage, so it
        /// is "-minor" (the server's alert rule skips those); 30 s and longer counts, and still needs two computers.
        /// </summary>
        internal static string KindFor(TimeSpan frozen) =>
            frozen.TotalSeconds >= 60 ? "ui-freeze-60s"
            : frozen.TotalSeconds >= 30 ? "ui-freeze-30s"
            : "ui-freeze-10s-minor";

        // ── The decision, kept apart from the thread so it can be tested with a fake clock ──

        /// <summary>What the watchdog should do at one tick. Discarded: the wait was the computer sleeping, so a freeze already
        /// noticed is dropped (its flag removed) rather than reported.</summary>
        internal enum Step { Nothing, FreezeStarted, FreezeEnded, Discarded }

        /// <summary>A gap between ticks this much longer than PostEvery means the watchdog itself was stopped: the computer slept.</summary>
        internal static readonly TimeSpan SuspendedAfter = TimeSpan.FromSeconds(5);

        private DateTime? _waitingSince;   // when the oldest unanswered job was posted
        private bool _reported;            // FreezeStarted already returned for this freeze
        private DateTime? _lastTick;       // when Tick last ran

        /// <summary>The window answered the job posted at <paramref name="postedAt"/>.</summary>
        internal Step Answered(DateTime postedAt, DateTime now, out TimeSpan frozenFor)
        {
            frozenFor = TimeSpan.Zero;
            if (_waitingSince == null || postedAt < _waitingSince.Value) return Step.Nothing;
            frozenFor = now - _waitingSince.Value;
            bool wasFrozen = _reported;
            _waitingSince = null;
            _reported = false;
            return wasFrozen ? Step.FreezeEnded : Step.Nothing;
        }

        /// <summary>A job is about to be posted at <paramref name="now"/>; says whether the oldest one has waited too long.</summary>
        internal Step Tick(DateTime now)
        {
            // Sleep and hibernate stop this thread and the window together, and the clock jumps on waking (bug round 41):
            // without this the first tick after a night's sleep saw a job "waiting" for hours, reported ui-freeze-60s, which
            // counts toward the outage email, and two laptops waking within ten minutes would have sent it.
            bool slept = _lastTick != null && now - _lastTick.Value > PostEvery + SuspendedAfter;
            _lastTick = now;
            if (slept)
            {
                bool wasReported = _reported;
                _waitingSince = now;
                _reported = false;
                return wasReported ? Step.Discarded : Step.Nothing;
            }

            _waitingSince ??= now;
            if (!_reported && now - _waitingSince.Value > FrozenAfter)
            {
                _reported = true;
                return Step.FreezeStarted;
            }
            return Step.Nothing;
        }

        // ── The thread ──

        private static Thread? _thread;
        private static volatile bool _stopped;

        /// <summary>
        /// Closing: the watchdog stops and any freeze.flag goes (bug round 55). Closing blocks the window's thread with
        /// bounded waits (meter flush, session and cloud drains, recording save, engine close) that can add up past 10 s,
        /// and that is a slow close, not a freeze. Without this the flag could outlive the process and the next launch
        /// reported previous-run-froze, which counts toward the outage email.
        /// </summary>
        internal static void Stop(string folder)
        {
            _stopped = true;
            TakeFlag(folder);
        }

        /// <summary>Starts watching the app's main dispatcher. Safe to call once; later calls do nothing.</summary>
        internal static void Start(Dispatcher dispatcher, string folder)
        {
            if (_thread != null) return;
            var watch = new FreezeWatchdog();
            _thread = new Thread(() => watch.Run(dispatcher, folder))
            {
                IsBackground = true,
                Name = "FreezeWatchdog",
                Priority = ThreadPriority.BelowNormal,
            };
            _thread.Start();
        }

        private void Run(Dispatcher dispatcher, string folder)
        {
            object gate = new();
            while (!dispatcher.HasShutdownStarted && !_stopped)
            {
                try
                {
                    DateTime posted = DateTime.UtcNow;
                    Step step;
                    lock (gate) step = Tick(posted);
                    if (step == Step.FreezeStarted && !_stopped)
                    {
                        DebugWindow.Log("FREEZE", $"the window has not responded for over {FrozenAfter.TotalSeconds:0} s; last activity: {DebugWindow.LastSource}");
                        WriteFlag(folder, posted);
                    }
                    else if (step == Step.Discarded)
                    {
                        DebugWindow.Log("FREEZE", "the computer slept; the wait was not the window");
                        TakeFlag(folder);
                    }

                    dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        DateTime answered = DateTime.UtcNow;
                        Step done;
                        TimeSpan frozen;
                        lock (gate) done = Answered(posted, answered, out frozen);
                        if (done == Step.FreezeEnded)
                        {
                            // Off the window's thread: the report and the file are not its business.
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                DebugWindow.Log("FREEZE", $"the window answered again after {frozen.TotalSeconds:0} s");
                                TakeFlag(folder);
                                ClientErrorReporter.ReportProblem("UI", KindFor(frozen),
                                    $"The window did not respond for {frozen.TotalSeconds:0} s");
                            });
                        }
                    }));
                }
                catch { /* the watchdog must never be the thing that fails */ }
                Thread.Sleep(PostEvery);
            }
        }

        // ── The flag, for a freeze that ends with the app being closed ──

        internal static void WriteFlag(string folder, DateTime utcNow)
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, FlagName), utcNow.ToString("o") + " " + DebugWindow.LastSource);
            }
            catch { }
        }

        /// <summary>The flag's text when the last run froze and never recovered, and removes it. Null otherwise. Never throws.</summary>
        internal static string? TakeFlag(string folder)
        {
            try
            {
                string path = Path.Combine(folder, FlagName);
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path).Trim();
                File.Delete(path);
                return text.Length > 80 ? text[..80] : text;
            }
            catch { return null; }
        }
    }
}
