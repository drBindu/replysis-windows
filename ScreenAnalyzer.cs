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

        /// <summary>
        /// The same caps, raised, for a capture of the whole monitor.
        ///
        /// A single window arrives close to its real size and reads well at 768.
        /// A 1920x1080 screen does not: it is shrunk to 1365x768, and LeetCode's
        /// body text goes from about fourteen pixels to ten, which is the edge of
        /// what a vision model reads reliably.
        ///
        /// The evidence that it had gone over that edge was an answer that named
        /// Two Sum, described the right approach, and never mentioned the words
        /// "Compile Error" printed in large red type across the right-hand half
        /// of the same screen. Two Sum is the most memorised problem there is, so
        /// a recalled answer and a read one look the same — until the question is
        /// about something the model has not seen before.
        ///
        /// The 768 cap was there to keep the upload short. The upload now happens
        /// before the question is asked, so on the path anyone waits on it costs
        /// nothing, and the only remaining cost is the model reading more pixels.
        /// Reading the screen correctly is what the feature is for.
        /// </summary>
        private const int MaxShortEdgeFullScreen = 1_100;
        private const int MaxLongEdgeFullScreen  = 2_560;

        // ── Last captured context (injected into follow-up voice questions) ───
        public static string LastScreenContext { get; private set; } = "";

        /// <summary>
        /// When the above was captured. A screen from half an hour ago is not the
        /// screen the question is about, and answering from it confidently is
        /// worse than admitting nothing was seen.
        /// </summary>
        public static DateTime LastScreenContextUtc { get; private set; } = DateTime.MinValue;

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

        // Which caps the downscale should use. Set by CaptureScreen before the
        // pixels are touched, read by the resize; a whole monitor needs more
        // room than a single window to stay readable.
        private static bool _capturingWholeScreen;

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
        public static byte[] CaptureScreen() => CaptureScreen(wholeScreen: false);

        /// <summary>
        /// A picture of what the user is looking at.
        /// </summary>
        /// <param name="wholeScreen">
        /// True while Watch Screen is on, where the whole monitor is the honest
        /// reading of what was switched on.
        ///
        /// Targeting the window in front is right for a deliberate F8: the user
        /// is pointing at something. It is wrong for a mode left running, where
        /// the target then depends on which window happened to be clicked last
        /// — and during a screen share that is very often the wrong one. Asked
        /// about a coding problem after clicking back into a chat window, the
        /// app read the chat window and answered about that, and nothing on
        /// screen explained why.
        ///
        /// The cost is real and worth stating: a 1920x1080 screen is read at
        /// 1365x768, so small text is smaller than it would be from a single
        /// window. Predictable and slightly softer beats sharp and pointed at
        /// the wrong thing.
        /// </param>
        public static byte[] CaptureScreen(bool wholeScreen)
        {
            _capturingWholeScreen = wholeScreen;
            if (wholeScreen)
            {
                LastCaptureTarget = "your screen";
                if (TryGetActiveMonitorBounds(out int mx, out int my, out int mw, out int mh))
                    return CaptureRegionCore(mx, my, mw, mh);
                return CapturePrimaryScreen();
            }

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
            double shortCap = _capturingWholeScreen ? MaxShortEdgeFullScreen : MaxShortEdge;
            double longCap  = _capturingWholeScreen ? MaxLongEdgeFullScreen  : MaxLongEdge;
            double scale = Math.Min(shortCap / shortEdge, longCap / longEdge);

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
            LastCaptureSignature = CoarseSignature(bmp);

            byte[] full = EncodePng(bmp);

            // Then take the colours out, which is where the size actually is.
            //
            // A real capture measured 239 KB, and every byte is uploaded twice:
            // once to our server and again to the model. On a normal home
            // connection that upload is most of the wait, more than the model,
            // which answers in about half a second.
            //
            // An editor or a browser is a handful of flat colours and text. Two
            // hundred and fifty six of them, chosen from the image itself, is
            // more than such a screen contains, so the glyphs come through
            // exactly as sharp while the file gets several times smaller. This
            // is not JPEG's kind of loss: no edge is softened, only shades that
            // were never there are removed.
            //
            // Photographs and gradients are the case it cannot help, and there
            // it can even come out larger. So both are measured and the smaller
            // one is sent, which also means a failure here costs nothing.
            byte[] indexed = TryEncodeIndexedPng(bmp);
            if (indexed != null && indexed.Length > 0 && indexed.Length < full.Length)
            {
                DebugWindow.Log("SCREEN",
                    $"Capture {full.Length / 1024} KB -> {indexed.Length / 1024} KB after palette reduction");
                return indexed;
            }
            return full;
        }

        /// <summary>
        /// A rough fingerprint of what the screen looks like, ignoring detail.
        ///
        /// Two captures are only worth sending together when they show
        /// different parts of the page. Comparing the exact bytes cannot tell
        /// that: a LeetCode page has a "2,332 Online" counter and a caret that
        /// change every second, so every capture differed, every one was kept
        /// as a new view, and every question went out carrying two pictures of
        /// the same screen — twice the tokens for nothing, on an allowance of
        /// eight thousand a minute.
        ///
        /// Sixteen by sixteen, sixteen levels of grey. A scroll moves whole
        /// blocks of the page and changes this a lot; a ticking counter and a
        /// blinking cursor do not move it at all.
        /// </summary>
        public static string LastCaptureSignature { get; private set; } = "";

        private static string CoarseSignature(BitmapSource bmp)
        {
            try
            {
                var small = new TransformedBitmap(bmp,
                    new ScaleTransform(16.0 / bmp.PixelWidth, 16.0 / bmp.PixelHeight));
                var grey = new FormatConvertedBitmap(small, PixelFormats.Gray8, null, 0);

                var pixels = new byte[16 * 16];
                grey.CopyPixels(pixels, 16, 0);

                var sb = new StringBuilder(256);
                foreach (byte value in pixels) sb.Append((value >> 4).ToString("x1"));
                return sb.ToString();
            }
            catch
            {
                // No signature means "treat it as different", which is the old
                // behaviour: correct, just not as cheap.
                return Guid.NewGuid().ToString("N");
            }
        }

        private static byte[] EncodePng(BitmapSource bmp)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        /// <summary>
        /// The same image reduced to a 256-colour palette built from its own
        /// pixels. Returns null if anything about it fails, so the caller simply
        /// keeps the full-colour version.
        /// </summary>
        private static byte[]? TryEncodeIndexedPng(BitmapSource bmp)
        {
            try
            {
                // The source has to be frozen first. A FormatConvertedBitmap
                // built on a live RenderTargetBitmap cannot itself be frozen,
                // and freezing it threw "This Freezable cannot be frozen" —
                // which the catch below swallowed, so every capture quietly
                // went out at full colour and the saving never happened once.
                if (bmp.CanFreeze && !bmp.IsFrozen) bmp.Freeze();

                var palette = new BitmapPalette(bmp, 256);
                var converted = new FormatConvertedBitmap(bmp, PixelFormats.Indexed8, palette, 0);

                // Freeze when it will, carry on when it will not. This runs on a
                // background thread that owns the bitmap either way, so freezing
                // is a nicety here rather than a requirement, and it is not worth
                // losing the whole saving over.
                if (converted.CanFreeze && !converted.IsFrozen) converted.Freeze();

                return EncodePng(converted);
            }
            catch (Exception ex)
            {
                DebugWindow.Log("SCREEN", $"Palette reduction skipped: {ex.Message}");
                return null;
            }
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
        /// <summary>
        /// The fallback prompt. It says what to do and nothing about who is
        /// asking or why, because everything else is what the refusal was
        /// reacting to. Output keeps the same section names so the display and
        /// the follow-up context do not need to know which attempt answered.
        /// </summary>
        private static string BuildPlainPrompt(string? spokenQuestion)
        {
            var sb = new StringBuilder();
            sb.AppendLine("The image is the user's own screen, as it looks right now. They are");
            sb.AppendLine("looking at it. Answer the question about it in their voice, as someone");
            sb.AppendLine("who can see it. Asked whether they can see the screen, the answer is");
            sb.AppendLine("yes, followed by what is on it.");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(spokenQuestion))
            {
                sb.AppendLine("QUESTION:");
                sb.AppendLine(spokenQuestion.Trim());
                sb.AppendLine();
            }
            sb.AppendLine("Reply in this shape:");
            sb.AppendLine();
            sb.AppendLine("SAY THIS");
            sb.AppendLine("The answer written in the first person, two to four sentences, ready");
            sb.AppendLine("to read aloud.");
            sb.AppendLine();
            sb.AppendLine("DETAIL");
            sb.AppendLine("Code, numbers or steps, only if the answer needs them. Complete, never");
            sb.AppendLine("abbreviated. Leave out entirely otherwise.");
            sb.AppendLine();
            sb.AppendLine("SCREEN NOTES");
            sb.AppendLine("One line listing what is visible: window name, menu and tab labels,");
            sb.AppendLine("button labels, headings, figures. Facts only, comma separated.");
            sb.AppendLine();
            sb.AppendLine("Use only what is visible in the image. Never claim you cannot see it.");
            sb.AppendLine("Plain text, no markdown.");
            return sb.ToString();
        }

        private static string BuildScreenPrompt(string? resumeContext, string? spokenQuestion = null)
        {
            if (!string.IsNullOrWhiteSpace(resumeContext) && resumeContext.Length > MaxResumeContextChars)
                resumeContext = resumeContext[..MaxResumeContextChars] + "\n[Candidate background truncated]";

            var sb = new StringBuilder();

            // A question was asked out loud, so the reply has to be something the
            // person can say. Describing a screen is right for the Analyze
            // hotkey, where nobody is waiting and the user reads it themselves.
            // Here the only useful output is the sentence to say back.
            //
            // The framing is deliberately plain. An earlier version opened with
            // "you are helping someone in a live interview, in the seconds before
            // they have to reply", and the model began answering "I'm sorry, I
            // can't help with that" to ordinary questions like "what do you see
            // on my screen". Read coldly, that sentence describes feeding someone
            // answers during an assessment, and the model declined. The task is
            // the same either way: read the screen, answer the question that was
            // asked, phrased so it can be spoken. Saying only that gets it done
            // instead of argued about, and an intermittent refusal is worse than
            // a slow answer because it lands with no warning while someone waits.
            if (!string.IsNullOrWhiteSpace(spokenQuestion))
            {
                sb.AppendLine("The image below is the user's own screen, as it looks right now.");
                sb.AppendLine("They are looking at it. Someone has asked them about it and they");
                sb.AppendLine("want to reply out loud.");
                sb.AppendLine();
                sb.AppendLine("You are writing their reply, so you are answering as someone who can");
                sb.AppendLine("see this screen, because they can. Never write a reply that denies");
                sb.AppendLine("being able to see it.");
                sb.AppendLine();
                sb.AppendLine("SAY WHEN SOMETHING IS BROKEN, EVEN IF NOBODY ASKED.");
                sb.AppendLine("If the screen shows a compile error, a failed test case, a stack");
                sb.AppendLine("trace, \"Wrong Answer\", \"Time Limit Exceeded\", or a red error");
                sb.AppendLine("panel, that is the most useful thing on it and the candidate may");
                sb.AppendLine("not have noticed yet. Nobody in an interview says \"can you solve");
                sb.AppendLine("that error\": they wait to see whether you spot it.");
                sb.AppendLine();
                sb.AppendLine("So put it first, in one short line they can say out loud:");
                sb.AppendLine("  \"I have got a compile error on line 63, let me fix that first.\"");
                sb.AppendLine("  \"Case 3 is failing, looks like the empty input case.\"");
                sb.AppendLine("Then answer whatever was actually asked. Name the line number and");
                sb.AppendLine("the message if they are readable, and give the corrected code in");
                sb.AppendLine("DETAIL. Never invent an error that is not on the screen.");
                sb.AppendLine();
                sb.AppendLine("NOT EVERY QUESTION IS ABOUT THE SCREEN. While the app is watching a");
                sb.AppendLine("shared screen it sends you the screen with every question, including");
                sb.AppendLine("the ones that have nothing to do with it. \"Which language do you");
                sb.AppendLine("prefer?\", \"tell me about yourself\", \"why are you leaving your");
                sb.AppendLine("current role?\" are ordinary interview questions that happen to have");
                sb.AppendLine("arrived while a screen was on show.");
                sb.AppendLine();
                sb.AppendLine("Answer those normally, as the candidate, and ignore the screen");
                sb.AppendLine("completely. Do not mention it, do not work it into the answer, do not");
                sb.AppendLine("say you can see it. Nobody asked. An answer about a code editor to");
                sb.AppendLine("\"which language do you prefer\" is a non-answer, and it tells the");
                sb.AppendLine("interviewer something is reading the screen.");
                sb.AppendLine();
                sb.AppendLine("Use the screen only when the question is about what is on it: solve");
                sb.AppendLine("this, what is this error, walk me through this code, can you see my");
                sb.AppendLine("screen.");
                sb.AppendLine();
                sb.AppendLine("DO THE TASK. Confirming you can see the screen is never the answer.");
                sb.AppendLine("Interviewers put the two together in one breath: \"you can see my");
                sb.AppendLine("screen, right? Can you solve this?\" There is one real question there");
                sb.AppendLine("and it is the second one. Answer it.");
                sb.AppendLine();
                sb.AppendLine("This happened. Asked \"so you see my screen, right? Can you solve the");
                sb.AppendLine("problem?\", the reply was a description of the screen — the panels,");
                sb.AppendLine("the compile error, the editor — and no solution at all. Everything in");
                sb.AppendLine("it was accurate and it answered nothing, in front of somebody waiting");
                sb.AppendLine("to hear how the candidate would solve it.");
                sb.AppendLine();
                sb.AppendLine("An unclear question is asked about, not answered around. When the");
                sb.AppendLine("question arrives half-transcribed — \"do you know coding or coding");
                sb.AppendLine("language? You\" — ask for it again in one short line and stop:");
                sb.AppendLine("\"Sorry, could you say that again?\" Do not fill the gap with an");
                sb.AppendLine("inventory of the screen. Listing the problem number, the language");
                sb.AppendLine("selected and which panel it is in reads as stalling, and it tells");
                sb.AppendLine("them nothing they cannot see.");
                sb.AppendLine();
                sb.AppendLine("Confirming sight is at most four words, and only when they asked:");
                sb.AppendLine("\"Yes, I can see it.\" Then the actual answer, immediately. Asked to");
                sb.AppendLine("solve something, solve it, with the code. Asked how you would");
                sb.AppendLine("approach it, give the approach. Never describe the screen back to");
                sb.AppendLine("them: they are looking at it, and they know what is on it.");
                sb.AppendLine();
                sb.AppendLine("THE QUESTION:");
                sb.AppendLine(spokenQuestion.Trim());
                sb.AppendLine();
                sb.AppendLine("Answer in this shape, and nothing else:");
                sb.AppendLine();
                sb.AppendLine("SAY THIS");
                sb.AppendLine("The reply, written in the user's voice, first person, ready to say");
                sb.AppendLine("out loud with no editing. Two to four sentences. Not a description");
                sb.AppendLine("of the screen and not advice about what to do: the actual reply.");
                sb.AppendLine();
                sb.AppendLine("DETAIL");
                sb.AppendLine("Only when the answer needs code, numbers, or steps to work through.");
                sb.AppendLine("Complete code, never abbreviated. Leave this section out entirely");
                sb.AppendLine("when the spoken reply is the whole answer.");
                sb.AppendLine();
                sb.AppendLine("SCREEN NOTES");
                sb.AppendLine("One dense line of what is visible: window name, menu and tab labels,");
                sb.AppendLine("button labels, headings, figures. Facts only, comma separated. Not");
                sb.AppendLine("shown to the user. It is what you will be given if they ask a");
                sb.AppendLine("follow-up about this same screen.");
                sb.AppendLine();
                sb.AppendLine("Rules:");
                sb.AppendLine("- Name things. \"Visual Studio\", \"Chrome\", \"the LeetCode Two Sum");
                sb.AppendLine("  page\", \"a Postgres query in DBeaver\". Never \"an application\", \"an");
                sb.AppendLine("  IDE\", \"a code editor\", \"a document\". A person looking at their own");
                sb.AppendLine("  screen says what it is, and hedging is the one thing that makes a");
                sb.AppendLine("  reply sound like it came from something that cannot really see.");
                sb.AppendLine("  Title bars, tabs, logos and menu names are all in the image; read");
                sb.AppendLine("  them. Only if the name is genuinely not visible, describe it by");
                sb.AppendLine("  what it does rather than calling it \"an application\".");
                sb.AppendLine("- Say when the question is cut off, and ask for the rest.");
                sb.AppendLine("  A coding problem often runs past the bottom of the screen. If the");
                sb.AppendLine("  statement, the examples or the constraints are clearly incomplete —");
                sb.AppendLine("  text ends mid-sentence, a section is missing, a scrollbar shows more");
                sb.AppendLine("  below — do not answer from half of it. Say so in the user's own");
                sb.AppendLine("  voice, as a line they can speak out loud while they scroll:");
                sb.AppendLine("    \"Let me scroll down and read the constraints before I answer.\"");
                sb.AppendLine("    \"Give me a second, I want to see the rest of the examples.\"");
                sb.AppendLine("  Then add one line beginning NEED: naming exactly what is missing,");
                sb.AppendLine("  such as NEED: the constraints and the third example.");
                sb.AppendLine("  Scrolling is captured, so the next answer will have both halves.");
                sb.AppendLine("  Answering a half-read question confidently is the worst outcome");
                sb.AppendLine("  here: it sounds right and it is wrong, and nobody can tell which.");
                sb.AppendLine("- Describe only what is visible. If you cannot read the part being");
                sb.AppendLine("  asked about, SAY THIS becomes a natural line that buys a moment,");
                sb.AppendLine("  such as \"Let me scroll up so I get the exact wording.\" Never guess.");
                sb.AppendLine("- Never invent the user's own history, employers, projects, or");
                sb.AppendLine("  numbers. Where their own detail belongs, write [your example].");
                sb.AppendLine("- Plain text and section titles exactly as above. No markdown, with");
                sb.AppendLine("  one exception: code goes inside a fence, ```language on its own");
                sb.AppendLine("  line before it and ``` on its own line after. The app lifts");
                sb.AppendLine("  anything fenced into a monospace panel of its own, so fence every");
                sb.AppendLine("  line of code and nothing else. Code left outside a fence is shown");
                sb.AppendLine("  in a proportional font with its indentation flattened.");

                return sb.ToString();
            }

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

                After the answer, and always, add one final section:

                SCREEN NOTES
                A single dense line listing what is actually visible: the page or
                window name, menu and tab labels, button labels, headings, and any
                figures or identifiers on screen. Facts only, comma separated, no
                commentary. This is not shown to the user. It is what you will be
                given if they ask you something about this screen later, so include
                the things your answer did not need but a follow-up question might.

                Format: plain text, nothing decorative. A section title is the bare word on
                its own line, in capitals, with its content on the very next line and
                no blank line between them. No lines of dashes, no markdown, no
                asterisks. Code is the one exception and must be fenced: ```language
                on its own line before it, ``` on its own line after, so the app can
                show it in a monospace panel instead of flattening it into prose.
                Bullets, where you need them, use the
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
            // Fences are kept on purpose. The code panel uses them to find where
            // the code starts and ends; stripping them here put the code straight
            // back into the paragraph it was meant to be lifted out of.

            // Rewrite AI-tell long dashes into plain human punctuation. The ━ (U+2501)
            // used for section headers is a different character and is left untouched.
            raw = Regex.Replace(raw, @"(\S)[ \t]*[—–][ \t]+", "$1, "); // mid-sentence break -> comma
            raw = raw.Replace("—", "-").Replace("–", "-");             // any remaining -> hyphen

            raw = raw.Replace("\r\n", "\n").Replace("\r", "\n");

            // SCREEN NOTES is written for the next question, not for this answer,
            // so it is removed before display. It survives in LastScreenContext,
            // which keeps the raw response.
            //
            // It exists because the context handed to a follow-up question used to
            // be the answer itself, and an answer only mentions what it needed. A
            // reply about credit packages said nothing about the navigation bar
            // above it, so "what are those options at the top?" got the credit
            // packages again, twice, with total confidence. The notes carry the
            // parts of the screen this particular answer had no reason to name.
            int notesAt = raw.IndexOf("\nSCREEN NOTES", StringComparison.OrdinalIgnoreCase);
            if (notesAt >= 0) raw = raw[..notesAt];

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
        /// <summary>
        /// Reads the screen and answers, retrying once in plainer words if the
        /// model declines.
        ///
        /// It does decline, on ordinary questions like "can you see my screen?",
        /// and the wording that sets it off is not obviously different from the
        /// wording that does not. A refusal is the worst failure this feature
        /// has: it arrives with no warning, looks like a considered answer, and
        /// leaves someone silent in front of an interviewer with nothing to say.
        /// Rewording the prompt reduces it. Only asking again removes it.
        ///
        /// A refusal is short and arrives at the very start, so the opening of
        /// the answer is held back briefly and checked before any of it is shown.
        /// The wait is a few dozen characters, which at streaming speed is not a
        /// perceptible delay.
        /// </summary>
        public static async IAsyncEnumerable<string> AnalyzeStreamAsync(
            byte[] imageBytes, string? resumeContext = null, string? spokenQuestion = null,
            System.Collections.Generic.IReadOnlyList<string>? preparedImageIds = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var held = new List<string>();
            var head = new StringBuilder();
            bool refused = false, released = false;

            await foreach (string token in
                StreamOnceAsync(imageBytes, resumeContext, spokenQuestion, plainly: false,
                                preparedImageIds, ct))
            {
                if (released) { yield return token; continue; }

                held.Add(token);
                head.Append(token);
                if (head.Length < RefusalProbeChars) continue;

                if (LooksLikeRefusal(head.ToString())) { refused = true; break; }

                released = true;
                foreach (string h in held) yield return h;
                held.Clear();
            }

            if (!refused)
            {
                // Answer ended before the probe filled, so it was never released.
                foreach (string h in held) yield return h;
                yield break;
            }

            DebugWindow.Log("SCREEN", "Model declined; asking again in plainer words.");
            await foreach (string token in
                StreamOnceAsync(imageBytes, resumeContext, spokenQuestion, plainly: true,
                                preparedImageIds: null, ct))
                yield return token;
        }

        /// <summary>How much of the answer to hold back while checking for a refusal.</summary>
        private const int RefusalProbeChars = 64;

        private static readonly string[] RefusalOpenings =
        {
            "i'm sorry", "i am sorry", "sorry, i can", "sorry, but i",
            "i can't help", "i cannot help", "i can't assist", "i cannot assist",
            "i'm not able to help", "i am unable to help", "i won't be able to help",
        };

        private static bool LooksLikeRefusal(string opening)
        {
            string o = opening.TrimStart().ToLowerInvariant();
            foreach (string r in RefusalOpenings)
                if (o.StartsWith(r, StringComparison.Ordinal)) return true;
            return false;
        }

        private static async IAsyncEnumerable<string> StreamOnceAsync(
            byte[] imageBytes, string? resumeContext, string? spokenQuestion, bool plainly,
            System.Collections.Generic.IReadOnlyList<string>? preparedImageIds = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            string prompt = plainly
                ? BuildPlainPrompt(spokenQuestion)
                : BuildScreenPrompt(resumeContext, spokenQuestion);
            string provider = GetProvider();

            // When the picture went up before the question, send the id instead.
            // The bytes are still carried on the fallback path, and on a retry,
            // because an id is spent the moment the server hands it back.
            string payloadJson = preparedImageIds is { Count: > 0 }
                ? JsonSerializer.Serialize(new { imageIds = preparedImageIds, prompt, provider })
                : JsonSerializer.Serialize(new
                  {
                      image = Convert.ToBase64String(imageBytes),
                      prompt,
                      provider
                  });

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
                if (!string.IsNullOrWhiteSpace(full))
                {
                    LastScreenContext    = full;
                    LastScreenContextUtc = DateTime.UtcNow;
                }
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
                await UserSession.EnsureFreshTokenAsync();

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
