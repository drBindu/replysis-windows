using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace InterviewCopilot
{
    /// <summary>
    /// Reads the words on a screen with the text reader that ships with Windows.
    ///
    /// Why it exists: on a weak connection a screenshot takes seconds to upload, and the speech already uses much of
    /// what the line has. The words on the screen are a few kilobytes, so they can go ahead of the question on any line
    /// and the question is answered from a screen that was read before it was asked. Nothing is installed and nothing
    /// is sent anywhere to do the reading; it happens on this computer.
    ///
    /// It is lossy on code: brackets, digits in small monospace type and symbols such as ^ and -> come back wrong or
    /// missing, and a prose statement comes back reliably. So a fast line keeps sending the picture, and this is for the
    /// lines where the picture is the thing that makes the answer late.
    /// </summary>
    internal static class ScreenOcr
    {
        private static readonly object Gate = new();
        private static OcrEngine? _engine;
        private static bool _tried;

        /// <summary>What the server accepts in one text. Longer is cut at a line.</summary>
        internal const int MaxChars = 24_000;

        /// <summary>Fewer letters than this is a picture or a blank page, not something to answer from.</summary>
        internal const int MinUsefulChars = 40;

        // Small type reads better when it is larger: measured on a 1080p-class screen, a 1.5x enlargement brought back
        // "2 <= nums.length <= 10^4" where the original lost both operators. Past this size the reader refuses anyway.
        private const double Enlarge = 1.5;
        private const int MaxSide = 8_000;

        internal static bool IsAvailable => Engine() != null;

        private static OcrEngine? Engine()
        {
            lock (Gate)
            {
                if (_tried) return _engine;
                _tried = true;
                try
                {
                    _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                              ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
                    DebugWindow.Log("SCREEN", _engine == null
                        ? "No text reader is installed for this Windows language; screens are sent as pictures."
                        : $"Text reader ready ({_engine.RecognizerLanguage.LanguageTag}).");
                }
                catch (Exception ex)
                {
                    DebugWindow.Log("SCREEN", $"Text reader unavailable: {ex.GetType().Name}");
                    _engine = null;
                }
                return _engine;
            }
        }

        /// <summary>
        /// The words on a bitmap, laid out for reading, or null when there are none worth sending or the reader failed.
        /// Slow-ish (a few hundred milliseconds): call from a background thread.
        /// </summary>
        internal static string? Read(BitmapSource source)
        {
            try
            {
                var engine = Engine();
                if (engine == null) return null;

                BitmapSource prepared = Enlarged(source);
                var converted = new FormatConvertedBitmap(prepared, PixelFormats.Pbgra32, null, 0);
                int w = converted.PixelWidth, h = converted.PixelHeight;
                if (w <= 0 || h <= 0 || w > OcrEngine.MaxImageDimension || h > OcrEngine.MaxImageDimension) return null;

                var pixels = new byte[w * h * 4];
                converted.CopyPixels(pixels, w * 4, 0);

                using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                    System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(pixels),
                    BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);

                OcrResult result;
                lock (Gate) result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();

                var words = new List<OcrWord>();
                foreach (var line in result.Lines)
                    foreach (var word in line.Words)
                        words.Add(new OcrWord(word.Text, word.BoundingRect.X, word.BoundingRect.Y,
                                              word.BoundingRect.Width, word.BoundingRect.Height));

                string text = Fit(OcrLayout.ToText(words));
                return text.Count(c => !char.IsWhiteSpace(c)) < MinUsefulChars ? null : text;
            }
            catch (Exception ex)
            {
                DebugWindow.Log("SCREEN", $"Reading the screen's words failed: {ex.GetType().Name}");
                return null;
            }
        }

        private static BitmapSource Enlarged(BitmapSource source)
        {
            int w = (int)Math.Round(source.PixelWidth * Enlarge), h = (int)Math.Round(source.PixelHeight * Enlarge);
            if (w > MaxSide || h > MaxSide) return source;

            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (DrawingContext dc = visual.RenderOpen())
                dc.DrawImage(source, new Rect(0, 0, w, h));
            var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            return target;
        }

        /// <summary>Cut at a line end, so a very long page never ends in half a word.</summary>
        internal static string Fit(string text)
        {
            if (text.Length <= MaxChars) return text;
            int cut = text.LastIndexOf('\n', MaxChars - 1);
            return text[..(cut > MaxChars / 2 ? cut : MaxChars)];
        }
    }
}
