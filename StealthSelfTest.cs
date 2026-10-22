using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace InterviewCopilot
{
    /// <summary>
    /// Proves, on this computer, whether a screen capture can see a Replysis window, instead of trusting Windows to
    /// say so. A Windows 10 desktop refused hiding with an error (2026-10-22); a computer could also accept the request
    /// and still show the window, and then nobody would be warned. The owner: "it has to detect".
    ///
    /// A 16 by 16 magenta square is shown for a moment in a screen corner with hiding applied, the corner is captured
    /// the way screen sharing captures it (a copy of the screen), and the copy is compared with the same corner before
    /// the square appeared:
    ///   Hidden:   the copy matches the corner without the square.
    ///   BlackBox: the copy is black where the square is (Windows' fallback level).
    ///   Visible:  the magenta square is in the copy.
    /// </summary>
    internal static class StealthSelfTest
    {
        internal enum Result { Hidden, BlackBox, Visible, Unknown }

        private const int Size = 16;

        /// <summary>What the last run saw, for the log and the problem report.</summary>
        internal static string LastDetail = "";

        internal static async Task<Result> RunAsync(bool applyHiding = true)
        {
            Window? probe = null;
            try
            {
                var area = SystemParameters.WorkArea;
                probe = new Window
                {
                    Width = Size, Height = Size, Left = area.Right - Size - 2, Top = area.Bottom - Size - 2,
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                    ShowActivated = false, Topmost = true, Background = new SolidColorBrush(Colors.Magenta),
                    Focusable = false,
                };
                var helper = new WindowInteropHelper(probe);
                helper.EnsureHandle();
                if (applyHiding) WindowStealth.TrySetCaptureExclusion(helper.Handle, true);
                probe.Show();
                await Task.Delay(350);   // let the compositor draw it

                // The display scale is only right once the square is on screen (175% read as 100% before it).
                var source = PresentationSource.FromVisual(probe);
                double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                int px = (int)Math.Round((area.Right - Size - 2) * scale);
                int py = (int)Math.Round((area.Bottom - Size - 2) * scale);
                int side = (int)Math.Floor(Size * scale);

                byte[] after = ScreenAnalyzer.GrabPixels(px, py, side, side);
                probe.Close();
                probe = null;
                await Task.Delay(200);
                byte[] before = ScreenAnalyzer.GrabPixels(px, py, side, side);   // the same corner without the square
                return Compare(before, after, side);
            }
            catch (Exception ex)
            {
                DebugWindow.Log("STEALTH", $"Self-test could not run: {ex.GetType().Name}");
                return Result.Unknown;
            }
            finally
            {
                try { probe?.Close(); } catch { }
            }
        }

        private static string Avg(byte[] p)
        {
            long r = 0, g = 0, b = 0; int n = p.Length / 4;
            for (int i = 0; i < p.Length; i += 4) { b += p[i]; g += p[i + 1]; r += p[i + 2]; }
            return n == 0 ? "none" : $"rgb({r / n},{g / n},{b / n})";
        }

        private static Result Compare(byte[] before, byte[] after, int side)
        {
            LastDetail = $"side {side}px, before {Avg(before)}, after {Avg(after)}";
            int magenta = 0, black = 0, same = 0, total = 0;
            for (int y = 2; y < side - 2; y++)
                for (int x = 2; x < side - 2; x++)
                {
                    int o = (y * side + x) * 4;   // B, G, R, A
                    int ab = after[o], ag = after[o + 1], ar = after[o + 2];
                    int bb = before[o], bg = before[o + 1], br = before[o + 2];
                    total++;
                    if (ar > 200 && ag < 60 && ab > 200) magenta++;
                    else if (ar < 16 && ag < 16 && ab < 16 && !(br < 16 && bg < 16 && bb < 16)) black++;
                    if (Math.Abs(ar - br) + Math.Abs(ag - bg) + Math.Abs(ab - bb) < 24) same++;
                }
            if (total == 0) return Result.Unknown;
            if (magenta * 2 > total) return Result.Visible;
            if (black * 2 > total) return Result.BlackBox;
            if (same * 10 >= total * 8) return Result.Hidden;
            return Result.Unknown;   // the corner changed for another reason (a clock tick, a notification)
        }
    }
}
