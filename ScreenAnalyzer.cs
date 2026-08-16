using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InterviewCopilot
{
    /// <summary>
    /// Captures the screen and streams the AI vision analysis token-by-token.
    /// F8 = the monitor the user is working on. F9 = the primary monitor.
    /// No extra NuGet packages — pure Win32 P/Invoke + WPF imaging.
    /// </summary>
    public static class ScreenAnalyzer
    {
        // Pre-compiled markdown-strip patterns used in CleanContent (called after every SSE response)
        private static readonly Regex RxBold     = new(@"\*{2}([^*\n]+)\*{2}", RegexOptions.Compiled);
        private static readonly Regex RxItalic   = new(@"\*([^*\n]+)\*",       RegexOptions.Compiled);
        private static readonly Regex RxUnder    = new(@"_{1,2}([^_\n]+)_{1,2}", RegexOptions.Compiled);
        private static readonly Regex RxHeading  = new(@"(?m)^#{1,6}\s+",       RegexOptions.Compiled);
        private static readonly Regex RxBlankRun = new(@"\n{3,}",               RegexOptions.Compiled);
        private const int MaxResponseChars = 30_000;
        private const int MaxResumeContextChars = 6_000;

        #region ── Win32 GDI (no System.Drawing required) ───────────────────────
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(
            IntPtr hdcDst, int xDst, int yDst, int cx, int cy,
            IntPtr hdcSrc, int xSrc, int ySrc, int rop);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(
            IntPtr hwnd, int attribute, out RECT value, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private const int SRCCOPY          = 0x00CC0020;
        private const int SM_CXSCREEN      = 0;   // primary monitor width
        private const int SM_CYSCREEN      = 1;   // primary monitor height
        private const int SM_XVIRTUALSCREEN  = 76; // virtual screen left edge (negative for left monitors)
        private const int SM_YVIRTUALSCREEN  = 77; // virtual screen top edge
        private const int SM_CXVIRTUALSCREEN = 78; // total width of all monitors combined
        private const int SM_CYVIRTUALSCREEN = 79; // total height of all monitors combined
        private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
        #endregion

        /// <summary>
        /// How large an image is worth sending.
        ///
        /// The model does its own resizing before it reads anything: the image is
        /// fitted into a 2048 square, then scaled again until its shortest side is
        /// 768 pixels. Everything above that is uploaded, paid for, and thrown
        /// away before a single pixel is read.
        ///
        /// That matters here because the upload is the slowest part of pressing
        /// F8. A full screen sent at 2048 wide is several megabytes of PNG that
        /// the model immediately shrinks to the same picture it would have got
        /// from a third of the bytes. Sending the shortest side at 768 costs
        /// nothing in legibility and returns the answer noticeably sooner.
        /// </summary>
        private const int MaxShortEdge = 768;
        private const int MaxLongEdge  = 2048;

        // ── Last captured context (injected into follow-up voice questions) ───
        public static string LastScreenContext { get; private set; } = "";

        /// <summary>
        /// What the last capture was pointed at, for showing above the answer.
        ///
        /// Without this the feature is guesswork. The user presses a key, an
        /// answer appears, and nothing says which window it came from, so an
        /// answer about the wrong window is indistinguishable from a bad answer
        /// about the right one. Naming the window turns a mystery into an obvious
        /// mistake the user can correct in one second.
        /// </summary>
        public static string LastCaptureTarget { get; private set; } = "";

        // ── The window the user was last actually working in ──────────────────
        //
        // Pressing the F8 hotkey leaves the other application in front, so the
        // window to read is simply whatever is in front. Clicking the Analyze
        // button does not: that brings us to the front, and we refuse to read
        // ourselves, so the capture fell back to the whole screen. The user had
        // pressed the button on our window while looking at their work, and got
        // an answer about the desktop.
        //
        // So the last window that was not ours is remembered, and used when we
        // are the one in front. Polled rather than hooked: one call every half
        // second costs nothing, and a foreground hook is a global system hook
        // this app does not otherwise need.
        private static IntPtr _lastExternalWindow;
        private static Timer? _foregroundWatcher;

        public static void StartTrackingActiveWindow()
        {
            _foregroundWatcher ??= new Timer(_ =>
            {
                try
                {
                    IntPtr hwnd = GetForegroundWindow();
                    if (hwnd == IntPtr.Zero) return;
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid != (uint)Environment.ProcessId) _lastExternalWindow = hwnd;
                }
                catch { /* a foreground query is never worth surfacing */ }
            }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
        }

        // ═════════════════════════════════════════════════════════════════════
        // CAPTURE
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Captures the monitor the user is actually working on, meaning the one
        /// holding the window that had focus when they pressed the key.
        ///
        /// This used to stitch every connected monitor into one wide image. On a
        /// dual 1920x1080 setup that produced a 3840x1080 strip which was then
        /// squashed to 2560x720, so every character on screen arrived at about two
        /// thirds size, on a second screen that usually held nothing relevant. On
        /// three monitors it was half size. That single decision is why answers
        /// read as though the model was guessing: it was, because the question was
        /// no longer legible by the time it arrived.
        /// </summary>
        public static byte[] CaptureScreen()
        {
            // The window in front, not the whole monitor, whenever that is a real
            // window belonging to something else.
            //
            // This is both the faster and the more accurate choice, for the same
            // reason. The model shrinks whatever it receives until the shortest
            // side is 768 pixels, so a full 1080p screen is read at 1365x768 no
            // matter what is sent. Small interface text does not survive that. A
            // window that occupies half the screen arrives at close to its real
            // size instead, which is the difference between the model reading an
            // error code and inventing one, and it costs less to send.
            if (TryGetForegroundWindowBounds(out int wx, out int wy, out int ww, out int wh))
            {
                LastCaptureTarget = WindowTitle(_capturedWindow);
                return CaptureRegionCore(wx, wy, ww, wh);
            }

            LastCaptureTarget = "your screen";
            if (TryGetActiveMonitorBounds(out int x, out int y, out int w, out int h))
                return CaptureRegionCore(x, y, w, h);

            return CapturePrimaryScreen();
        }

        /// <summary>
        /// Bounds of the window in front, clipped to the monitor showing it.
        ///
        /// Refuses in the cases where the answer would be wrong rather than
        /// merely different: a window of ours, since capturing ourselves would
        /// feed the model its own last answer, and anything too small to hold a
        /// question, where the user almost certainly means the screen behind it.
        /// </summary>
        private static IntPtr _capturedWindow;

        private static bool TryGetForegroundWindowBounds(out int x, out int y, out int w, out int h)
        {
            x = y = w = h = 0;
            _capturedWindow = IntPtr.Zero;
            try
            {
                IntPtr hwnd = GetForegroundWindow();

                // We are in front, which means the user pressed the button on our
                // window rather than the hotkey. Read what they were working in
                // before they reached for us.
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (hwnd == IntPtr.Zero || pid == (uint)Environment.ProcessId)
                    hwnd = _lastExternalWindow;

                if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || IsIconic(hwnd)) return false;

                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == (uint)Environment.ProcessId) return false;

                // The window's own frame, not the invisible resize border Windows
                // draws around it. GetWindowRect includes that padding, which puts
                // a strip of whatever is behind into the capture.
                const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
                RECT r;
                if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                                          out r, Marshal.SizeOf<RECT>()) != 0 &&
                    !GetWindowRect(hwnd, out r))
                    return false;

                // Clip to the monitor. A maximised window reports slightly beyond
                // its screen, and off-screen coordinates make the copy fail.
                if (TryGetActiveMonitorBounds(out int mx, out int my, out int mw, out int mh))
                {
                    int left   = Math.Max(r.Left,   mx);
                    int top    = Math.Max(r.Top,    my);
                    int right  = Math.Min(r.Right,  mx + mw);
                    int bottom = Math.Min(r.Bottom, my + mh);
                    r = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
                }

                w = r.Right - r.Left;
                h = r.Bottom - r.Top;
                x = r.Left;
                y = r.Top;

                // Too small to be holding the thing they want read.
                if (w < 480 || h < 360) return false;

                _capturedWindow = hwnd;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Captures the primary monitor, whichever screen currently has focus.
        /// </summary>
        public static byte[] CapturePrimaryScreen()
        {
            LastCaptureTarget = "the main screen";
            return CaptureRegionCore(0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
        }

        /// <summary>
        /// Captures a user-selected desktop rectangle. Coordinates are physical
        /// virtual-screen pixels and can be negative on left/top monitors.
        /// This is the sharpest option available: a selection is usually well under
        /// the size limit, so it is sent at full resolution with nothing resampled.
        /// </summary>
        public static byte[] CaptureRegion(int x, int y, int width, int height)
        {
            LastCaptureTarget = "the area you selected";
            return CaptureRegionCore(x, y, width, height);
        }

        /// <summary>Title of a window, trimmed for display.</summary>
        private static string WindowTitle(IntPtr hwnd)
        {
            try
            {
                var sb = new StringBuilder(256);
                if (hwnd == IntPtr.Zero || GetWindowText(hwnd, sb, sb.Capacity) <= 0)
                    return "the window in front";

                string title = sb.ToString().Trim();
                if (title.Length == 0) return "the window in front";
                return title.Length > 60 ? title[..57] + "..." : title;
            }
            catch
            {
                return "the window in front";
            }
        }

        /// <summary>
        /// Bounds of the monitor holding the foreground window, in the same
        /// physical desktop pixels BitBlt expects. False if Windows cannot say,
        /// which happens when nothing holds focus.
        /// </summary>
        private static bool TryGetActiveMonitorBounds(out int x, out int y, out int w, out int h)
        {
            x = y = w = h = 0;
            try
            {
                IntPtr monitor = MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTOPRIMARY);
                if (monitor == IntPtr.Zero) return false;

                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfoW(monitor, ref info)) return false;

                x = info.rcMonitor.Left;
                y = info.rcMonitor.Top;
                w = info.rcMonitor.Right  - info.rcMonitor.Left;
                h = info.rcMonitor.Bottom - info.rcMonitor.Top;
                return w > 0 && h > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Core capture: BitBlt a region of the desktop DC into a JPEG byte array.
        /// </summary>
        private static byte[] CaptureRegionCore(int srcX, int srcY, int srcW, int srcH)
        {
            if (srcW <= 0 || srcH <= 0)
                throw new InvalidOperationException("The requested screen region is invalid.");

            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                throw new InvalidOperationException($"Could not access the desktop ({Marshal.GetLastWin32Error()}).");
            IntPtr memDc    = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, screenDc);
                throw new InvalidOperationException($"Could not create a memory device context ({Marshal.GetLastWin32Error()}).");
            }
            IntPtr hBitmap  = CreateCompatibleBitmap(screenDc, srcW, srcH);
            if (hBitmap == IntPtr.Zero)
            {
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
                throw new InvalidOperationException($"Could not allocate a capture bitmap ({Marshal.GetLastWin32Error()}).");
            }
            IntPtr oldObj   = SelectObject(memDc, hBitmap);
            if (oldObj == IntPtr.Zero || oldObj == new IntPtr(-1))
            {
                DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
                throw new InvalidOperationException($"Could not select the capture bitmap ({Marshal.GetLastWin32Error()}).");
            }

            if (!BitBlt(memDc, 0, 0, srcW, srcH, screenDc, srcX, srcY, SRCCOPY))
            {
                SelectObject(memDc, oldObj);
                DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
                throw new InvalidOperationException($"Could not copy the screen image ({Marshal.GetLastWin32Error()}).");
            }
            SelectObject(memDc, oldObj);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);

            BitmapSource bmp;
            try
            {
                bmp = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DeleteObject(hBitmap);
            }

            // Shrink to what the model will actually read, and shrink with a real
            // resampling filter. TransformedBitmap picks whatever scaling mode it
            // likes, which on text produces the broken, speckled glyphs that make
            // a model misread a line of code.
            double shortEdge = Math.Min(srcW, srcH);
            double longEdge  = Math.Max(srcW, srcH);
            double scale = Math.Min(MaxShortEdge / shortEdge, MaxLongEdge / longEdge);

            if (scale < 1.0)
            {
                int dstW = Math.Max(1, (int)Math.Round(srcW * scale));
                int dstH = Math.Max(1, (int)Math.Round(srcH * scale));

                var visual = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
                using (DrawingContext dc = visual.RenderOpen())
                    dc.DrawImage(bmp, new Rect(0, 0, dstW, dstH));

                var target = new RenderTargetBitmap(dstW, dstH, 96, 96, PixelFormats.Pbgra32);
                target.Render(visual);
                bmp = target;
            }

            // PNG, not JPEG. A screenshot is text on flat colour, which is the
            // exact case JPEG handles worst: its compression rings around every
            // letter edge, and at small sizes that is the difference between the
            // model reading 'l' and reading '1'. PNG is lossless and, on this kind
            // of image, usually no larger than the JPEG it replaces.
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        // ═════════════════════════════════════════════════════════════════════
        // VISION PROVIDER — server picks the actual model; client only says which
        // ═════════════════════════════════════════════════════════════════════

        private static string GetProvider() => SettingsWindow.IsGroq() ? "groq" : "openai";

        // ═════════════════════════════════════════════════════════════════════
        // PROMPT
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The instructions sent with every screenshot.
        ///
        /// This used to be six full output templates, roughly three thousand
        /// tokens, shipped on every single request. It read well on the page and
        /// worked badly in practice. The model had to classify the screen, recall
        /// the matching template, and satisfy a page of formatting rules before it
        /// had spent any attention on actually reading the screenshot, and the
        /// reading is the hard part. Long instruction blocks are exactly what
        /// pushes a vision model toward a fluent, generic, wrong answer.
        ///
        /// So it is short now, and ordered the way the work actually happens: read
        /// the screen, then answer. The templates are reduced to their section
        /// names, so an answer still looks the same in the app.
        ///
        /// One rule earns its place above all the others. The old behavioural
        /// template demanded a result "with a specific number or metric" while the
        /// header forbade inventing the candidate's experience. Faced with a
        /// contradiction the model took the concrete instruction, and produced
        /// achievements the user never had, for them to read out to an interviewer
        /// who may well check. Nothing here asks the model to speak as the user
        /// about their own past.
        /// </summary>
        private static string BuildScreenPrompt(string? resumeContext)
        {
            if (!string.IsNullOrWhiteSpace(resumeContext) && resumeContext.Length > MaxResumeContextChars)
                resumeContext = resumeContext[..MaxResumeContextChars] + "\n[Candidate background truncated]";

            var sb = new StringBuilder();

            sb.AppendLine("""
                You are sitting beside someone who is in a live interview right now. They
                have just captured their screen and need something they can use within
                seconds.

                Work in this order:
                1. Read the screen. Find the one thing they need help with: a question, a
                   coding problem, an error, a diagram, or a form. Ignore tabs, toolbars,
                   chat panels, notifications, and anything else around it.
                2. Answer that. Lead with the answer. Do not describe the screenshot back
                   to them.

                Rules that matter more than the format:
                - Use only what you can actually see. If the part that matters is too
                  small, cut off, or blurred, say which part you cannot read and stop.
                  A confident wrong answer can cost them the job.
                - Never invent their experience. No employers, projects, metrics, or
                  numbers about them that are not on the screen. Where their own detail
                  belongs, write [your example] and let them fill it in.
                - Answer this screen, not the general topic. If an error code or a
                  message is shown, work out what it means here, in this program,
                  using everything else visible around it. Reciting what the code
                  usually means is not an answer, and it is usually the wrong one.
                - Code must be complete and runnable. Never write "..." or "rest of the
                  code unchanged".
                - Everything that is not code stays short. They are reading this while
                  another person is talking to them.

                Match the shape of your answer to what is on the screen.

                A coding or algorithm problem:
                APPROACH
                One or two lines. Name the technique.
                SOLUTION
                Complete code, in whatever language is on screen, Python if none is.
                Comment only the lines whose logic is not obvious.
                COMPLEXITY
                Time: O(?)   Space: O(?)
                SAY THIS
                One sentence they can speak while writing it.

                An error, failing test, or stack trace:
                CAUSE
                One line. The real cause, not the symptom.
                FIX
                The corrected code, ready to paste.
                SAY THIS
                One sentence they can speak.

                A multiple choice or quiz question:
                ANSWER
                The option, stated flatly.
                WHY
                One line for why it is right. One line for why the closest wrong option
                is wrong.

                A system design or architecture diagram:
                SCOPE
                What it has to do, and the scale you are assuming.
                DESIGN
                The components, and how one request travels through them.
                TRADE-OFF
                The one an interviewer will push on.
                SAY THIS
                One sentence to open with.

                A question about them, such as "tell me about a time":
                STRUCTURE
                Situation, action, result, with [your example] everywhere their own
                detail belongs.
                SAY THIS
                An opening sentence that is safe to say exactly as written.

                Anything else:
                WHAT THIS IS
                One line.
                DO THIS
                The single most useful next step.

                Format: plain text, nothing decorative. A section title is the bare word on
                its own line, in capitals, with its content on the very next line and
                no blank line between them. No lines of dashes, no markdown, no
                asterisks, no backtick fences. Bullets, where you need them, use the
                • character.

                Keep the whole thing as short as it can be and still answer. Three
                clean lines beat three decorated sections.
                """);

            if (!string.IsNullOrWhiteSpace(resumeContext))
            {
                sb.AppendLine();
                sb.AppendLine(
                    "The candidate's background is below. Use it only to choose which of " +
                    "their real experiences fits, and only when the screen is asking about " +
                    "them. It is never a licence to invent detail that is not in it.");
                sb.AppendLine(resumeContext);
            }

            return sb.ToString();
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST-PROCESSOR  — runs once on the completed response before display
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Normalises the AI response so section headers are consistently spaced,
        /// stray markdown is removed, and no more than one blank line appears anywhere.
        /// </summary>
        public static string PostProcess(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;

            // 1. Strip any residual markdown that leaked through despite the prompt
            raw = Regex.Replace(raw, @"\*{2}([^*\n]+)\*{2}", "$1");   // **bold**
            raw = Regex.Replace(raw, @"\*([^*\n]+)\*",       "$1");   // *italic*
            raw = Regex.Replace(raw, @"_{1,2}([^_\n]+)_{1,2}", "$1"); // _italic_
            raw = Regex.Replace(raw, @"(?m)^#{1,6}\s+", "");          // ## headers
            raw = Regex.Replace(raw, @"```[a-zA-Z]*\r?\n?", "");      // code fences

            // Rewrite AI-tell long dashes into plain human punctuation. The ━ (U+2501)
            // used for section headers is a different character and is left untouched.
            raw = Regex.Replace(raw, @"(\S)[ \t]*[—–][ \t]+", "$1, "); // mid-sentence break -> comma
            raw = raw.Replace("—", "-").Replace("–", "-");             // any remaining -> hyphen

            raw = raw.Replace("\r\n", "\n").Replace("\r", "\n");

            // Section titles are bare words now. They used to be wrapped in heavy
            // rules, ━━━ CAUSE ━━━, which looked like decoration around an answer
            // rather than an answer, and turned a three line reply into something
            // that had to be visually decoded before it could be read. Anything
            // still arriving in the old shape is unwrapped here.
            raw = Regex.Replace(raw, @"(?m)^[ \t]*[━─—=-]{2,}[ \t]*(.*?)[ \t]*[━─—=-]{2,}[ \t]*$", "$1");

            // 2. A title sits directly on top of what it describes, with a blank
            //    line separating it from the section before. Nothing between a
            //    title and its own content: that gap is what made the old output
            //    feel spread out and hard to scan.
            var lines  = raw.Split('\n');
            var result = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd();

                if (IsSectionTitle(line))
                {
                    while (result.Count > 0 && result[^1] == "")
                        result.RemoveAt(result.Count - 1);
                    if (result.Count > 0) result.Add("");
                    result.Add(line.Trim());
                }
                else
                {
                    // Collapse runs of more than 1 blank line
                    if (line == "" && result.Count > 0 && result[^1] == "")
                        continue;
                    result.Add(line);
                }
            }

            // 3. Strip leading/trailing blank lines
            while (result.Count > 0 && result[0]  == "") result.RemoveAt(0);
            while (result.Count > 0 && result[^1] == "") result.RemoveAt(result.Count - 1);

            return string.Join("\n", result);
        }

        /// <summary>
        /// Whether a line is one of the short all-capitals labels the answer is
        /// built from, such as CAUSE or SAY THIS. Deliberately strict: a sentence
        /// the model happened to shout, a line of code in capitals, or anything
        /// carrying punctuation is left alone, so ordinary content is never
        /// reformatted as a heading.
        /// </summary>
        private static bool IsSectionTitle(string line)
        {
            string t = line.Trim();
            if (t.Length is 0 or > 24) return false;

            bool hasLetter = false;
            foreach (char c in t)
            {
                if (char.IsLetter(c))
                {
                    if (char.IsLower(c)) return false;
                    hasLetter = true;
                }
                else if (c != ' ')
                {
                    return false;
                }
            }
            return hasLetter;
        }

        // ═════════════════════════════════════════════════════════════════════
        // STREAMING ANALYSIS — primary API (tokens yielded as they arrive)
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Sends the screenshot to the vision AI and streams tokens as they arrive.
        /// Uses helper methods for all error-prone work so yield statements never
        /// appear inside try-catch blocks (C# iterator restriction).
        /// </summary>
        public static async IAsyncEnumerable<string> AnalyzeStreamAsync(
            byte[] imageBytes, string? resumeContext = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            string base64 = Convert.ToBase64String(imageBytes);
            string prompt = BuildScreenPrompt(resumeContext);
            string provider = GetProvider();
            string payloadJson = JsonSerializer.Serialize(new { image = base64, prompt, provider });

            // ── Send request via helper (never throws) ────────────────────────
            var (res, sendError) = await SendVisionRequestSafeAsync(payloadJson, ct);
            if (res == null)
            {
                throw new InvalidOperationException(sendError);
            }

            // ── Handle non-200 via helper (never throws) ──────────────────────
            if (!res.IsSuccessStatusCode)
            {
                string errMsg = DescribeVisionError(res.StatusCode);
                res.Dispose();
                throw new InvalidOperationException(errMsg);
            }

            // ── Stream SSE tokens ─────────────────────────────────────────────
            // yield inside try-finally is legal; only try-catch is forbidden.
            var accumulated = new StringBuilder();
            int responseLength = 0;
            try
            {
                using var stream = await res.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);

                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ct);
                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;
                    string data = line["data: ".Length..];
                    if (data == "[DONE]") break;

                    // ParseSseToken never throws — returns "" on any malformed line
                    string token = ParseSseToken(data);
                    if (!string.IsNullOrEmpty(token))
                    {
                        int remaining = MaxResponseChars - responseLength;
                        if (remaining <= 0) break;
                        if (token.Length > remaining) token = token[..remaining];
                        accumulated.Append(token);
                        responseLength += token.Length;
                        yield return token;
                        if (responseLength >= MaxResponseChars)
                        {
                            yield return "\n[Response truncated]";
                            break;
                        }
                    }
                }
            }
            finally
            {
                // Always update context so follow-up voice questions work
                string full = CleanContent(accumulated.ToString());
                if (!string.IsNullOrWhiteSpace(full)) LastScreenContext = full;
                res.Dispose();
            }
        }

        /// <summary>
        /// Sends the screenshot and prompt to the secured backend (server-side Groq/OpenAI
        /// keys, credits deducted server-side) — mirrors the /ask flow, no personal API key
        /// ever required or accepted from the user. Catches all exceptions and returns null
        /// on failure. Returns a (response, errorMessage) tuple — error is "" on success.
        /// </summary>
        private static async Task<(HttpResponseMessage? Response, string Error)>
            SendVisionRequestSafeAsync(string payloadJson, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post,
                    $"{SettingsWindow.GetBackendUrl()}/api/v1/interview/analyze-screen");
                if (!string.IsNullOrEmpty(UserSession.IdToken))
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {UserSession.IdToken}");
                req.Headers.TryAddWithoutValidation("X-Device-Id", DeviceIdentity.Current);
                req.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                var response = await SharedHttpClient.Http.SendAsync(
                    req, HttpCompletionOption.ResponseHeadersRead, ct);
                return (response, "");
            }
            catch (TaskCanceledException)
            {
                return (null, "Request timed out. Check your connection and try again.");
            }
            catch (Exception ex)
            {
                return (null, $"Network error: {ex.Message}");
            }
        }

        /// <summary>Maps a non-200 status from the backend to a friendly message.</summary>
        private static string DescribeVisionError(System.Net.HttpStatusCode status) => status switch
        {
            System.Net.HttpStatusCode.Unauthorized => "Please sign in to use Screen AI.",
            System.Net.HttpStatusCode.PaymentRequired =>
                "Insufficient credits.\n\nUpgrade your Replysis AI plan to continue using Screen AI.",
            System.Net.HttpStatusCode.BadRequest => "Could not analyze this screenshot. Please try again.",
            _ => "Screen AI is temporarily unavailable. Please try again."
        };

        /// <summary>Parses one SSE data line; returns "" on any error (never throws).</summary>
        private static string ParseSseToken(string data)
        {
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("error", out var errProp))
                    throw new InvalidOperationException(errProp.GetString()
                        ?? "Screen AI could not complete this request.");
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0) return "";
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var cp))
                    return cp.GetString() ?? "";
                return "";
            }
            catch (JsonException) { return ""; }
        }

        // ═════════════════════════════════════════════════════════════════════
        // NON-STREAMING WRAPPER (backward compat — collects all tokens)
        // ═════════════════════════════════════════════════════════════════════

        public static async Task<string> AnalyzeAsync(byte[] imageBytes, string? resumeContext = null)
        {
            var sb = new StringBuilder();
            await foreach (var token in AnalyzeStreamAsync(imageBytes, resumeContext))
                sb.Append(token);
            return sb.ToString();
        }

        // ═════════════════════════════════════════════════════════════════════
        // HELPERS
        // ═════════════════════════════════════════════════════════════════════

        private static string CleanContent(string content)
        {
            if (string.IsNullOrEmpty(content)) return content;
            content = RxBold.Replace(content, "$1");
            content = RxItalic.Replace(content, "$1");
            content = RxUnder.Replace(content, "$1");
            content = RxHeading.Replace(content, "");
            content = content.Replace("\r\n", "\n").Replace("\r", "\n");
            content = RxBlankRun.Replace(content, "\n\n");
            return content.Trim();
        }
    }
}
