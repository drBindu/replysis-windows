using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace InterviewCopilot
{
    public partial class AnswerWindow : Window
    {
        public bool IsCameraModeActive = false;
        public event Action? CameraModeClosedByUser;
        public event Action? AnalyzeRequested;

        private bool _isListening  = false;
        private bool _isProcessing = false;
        private bool _hasQuestion  = false;
        private bool _hasAnswer    = false;

        // ── Drag state (bounded drag — mirrors Mac's screen-clamped reposition) ─
        private bool   _isDragging        = false;
        private double _dragLastScreenX   = 0;
        private double _dragLastScreenY   = 0;

        public AnswerWindow()
        {
            InitializeComponent();
            try { WindowStealth.SetStealthMode(this, SettingsWindow.GetStealthMode()); } catch { }

            // Suppress ALL key processing inside the overlay.
            // Critical: prevents Space from firing the focused Analyze button
            // while the global keyboard hook is simultaneously handling it.
            this.PreviewKeyDown += (s, e) => { e.Handled = true; };

            this.Loaded += (s, e) =>
            {
                // Top-centre of screen, pinned near webcam — matches Mac "top-edge pinned"
                var screen = SystemParameters.WorkArea;
                this.Left = screen.Left + (screen.Width - this.Width) / 2;
                this.Top = screen.Top + 10;
                ApplyOverlayOpacity();
            };
        }

        // ── Bounded drag (replaces DragMove — Mac clamps to screen bounds) ────
        private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            // Record absolute screen position at drag start (physical pixels via PointToScreen)
            Point screenPt = this.PointToScreen(e.GetPosition(this));
            _dragLastScreenX = screenPt.X;
            _dragLastScreenY = screenPt.Y;
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        private void DragHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || !((UIElement)sender).IsMouseCaptured) return;

            Point screenPt = this.PointToScreen(e.GetPosition(this));
            double dxPx = screenPt.X - _dragLastScreenX;
            double dyPx = screenPt.Y - _dragLastScreenY;
            _dragLastScreenX = screenPt.X;
            _dragLastScreenY = screenPt.Y;

            // Convert physical-pixel delta → WPF DIPs
            var src = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            double sx = 96.0 / src.PixelsPerInchX;
            double sy = 96.0 / src.PixelsPerInchY;

            // Clamp to work area — same as Mac: max(minX, min(newX, maxX - w))
            var wa = SystemParameters.WorkArea;
            this.Left = WindowBounds.ClampPosition(this.Left + dxPx * sx, wa.Left, wa.Right, this.ActualWidth);
            this.Top  = WindowBounds.ClampPosition(this.Top  + dyPx * sy, wa.Top, wa.Bottom, this.ActualHeight);
        }

        private void DragHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            ((UIElement)sender).ReleaseMouseCapture();
            e.Handled = true;
        }

        // ── Opacity ──────────────────────────────────────────────────────────
        public void ApplyOverlayOpacity()
        {
            if (!IsCameraModeActive)
            {
                MainBorder.Opacity = 0;
                return;
            }

            double opacity = SettingsWindow.GetOverlayOpacity();
            double backdropOpacity = Math.Clamp((opacity - 0.50) / 0.50, 0.06, 1.0);
            byte alpha = (byte)Math.Clamp(backdropOpacity * 255.0, 0, 255);
            MainBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x09, 0x0B, 0x12));
            MainBorder.Opacity = 1;
        }

        public void ToggleCameraMode(bool active)
        {
            IsCameraModeActive = active;
            if (active)
            {
                this.Show();
                ApplyOverlayOpacity();
                RefreshIdleHint();
            }
            else
            {
                MainBorder.Opacity = 0;
                this.Hide();
            }
        }

        // ── Mic state ────────────────────────────────────────────────────────
        public void UpdateMicState(bool isListening, bool isProcessing)
        {
            _isListening  = isListening;
            _isProcessing = isProcessing;

            if (isProcessing)
            {
                MicStatusLabel.Text       = "Thinking...";
                MicStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(210, 153, 34));
                MiniMicIndicator.Fill     = new SolidColorBrush(Color.FromRgb(210, 153, 34));
                MiniMicGlow.Color         = Color.FromRgb(210, 153, 34);
                MiniMicGlow.BlurRadius    = 8;
            }
            else if (isListening)
            {
                MicStatusLabel.Text       = "Listening";
                MicStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(63, 185, 80));
                MiniMicIndicator.Fill     = new SolidColorBrush(Color.FromRgb(63, 185, 80));
                MiniMicGlow.Color         = Color.FromRgb(63, 185, 80);
                MiniMicGlow.BlurRadius    = 8;
            }
            else
            {
                MicStatusLabel.Text       = "Ready";
                MicStatusLabel.Foreground = new SolidColorBrush(Colors.White);
                MiniMicIndicator.Fill     = new SolidColorBrush(Color.FromRgb(248, 81, 73));
                MiniMicGlow.Color         = Color.FromRgb(248, 81, 73);
                MiniMicGlow.BlurRadius    = 6;
            }

            // Mirror Mac: show transcript row as soon as listening starts, even before text
            if (isListening && !_hasQuestion)
            {
                TranscriptDivider.Visibility = Visibility.Visible;
                TranscriptRow.Visibility     = Visibility.Visible;
            }
            else if (!isListening && !_hasQuestion)
            {
                TranscriptDivider.Visibility = Visibility.Collapsed;
                TranscriptRow.Visibility     = Visibility.Collapsed;
            }

            RefreshIdleHint();
        }

        // ── Question / transcript ─────────────────────────────────────────────
        public void ShowServiceUnavailable(string status, string message)
        {
            _isListening = false;
            _isProcessing = false;
            MicStatusLabel.Text = status;
            MicStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(255, 107, 115));
            MiniMicIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 107, 115));
            MiniMicGlow.Color = Color.FromRgb(255, 107, 115);
            MiniMicGlow.BlurRadius = 0;

            UpdateQuestion("Speech transcription is unavailable");
            UpdateAnswer(message);
        }

        public void UpdateQuestion(string text)
        {
            bool hasText = !string.IsNullOrWhiteSpace(text);
            _hasQuestion = hasText;

            if (hasText)
            {
                QuestionTextBlock.Text       = text;
                QuestionTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)); // #cbd5e1
                TranscriptDivider.Visibility = Visibility.Visible;
                TranscriptRow.Visibility     = Visibility.Visible;
                // Past eight lines the transcript scrolls; the newest words are the
                // ones the candidate is waiting on, so keep them in view.
                TranscriptScroll.ScrollToEnd();
            }
            else
            {
                QuestionTextBlock.Text       = "Listening...";
                QuestionTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)); // #64748b
                TranscriptScroll.ScrollToHome();
                if (!_isListening)
                {
                    TranscriptDivider.Visibility = Visibility.Collapsed;
                    TranscriptRow.Visibility     = Visibility.Collapsed;
                }
            }

            RefreshIdleHint();
        }

        // ── Answer ────────────────────────────────────────────────────────────
        public void UpdateAnswer(string text)
        {
            bool hasText   = !string.IsNullOrWhiteSpace(text);
            bool wasEmpty  = !_hasAnswer;
            _hasAnswer     = hasText;

            if (hasText)
            {
                // Split what is spoken from what is only glanced at.
                //
                // Both used to land in one block at one size, so the literal words
                // MORE TO SAY printed as a sentence in the middle of the answer and
                // the four follow-up bullets looked exactly as urgent as the words
                // the candidate was in the middle of saying out loud. The marker is
                // now a quiet label and the bullets are smaller and dimmer, so the
                // eye lands on the spoken part first.
                //
                // A partially streamed marker is left alone: until the whole phrase
                // has arrived it stays in the spoken block, rather than the heading
                // appearing one letter at a time.
                string formatted = FormatForReading(text);
                int moreAt = formatted.IndexOf("MORE TO SAY", StringComparison.OrdinalIgnoreCase);

                if (moreAt >= 0)
                {
                    AnswerTextBlock.Text = formatted[..moreAt].TrimEnd();
                    MoreTextBlock.Text   = formatted[(moreAt + "MORE TO SAY".Length)..].Trim();

                    bool hasMore = MoreTextBlock.Text.Length > 0;
                    MoreDivider.Visibility   = hasMore ? Visibility.Visible : Visibility.Collapsed;
                    MoreLabel.Visibility     = hasMore ? Visibility.Visible : Visibility.Collapsed;
                    MoreTextBlock.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
                }
                else
                {
                    AnswerTextBlock.Text     = formatted;
                    MoreTextBlock.Text       = "";
                    MoreDivider.Visibility   = Visibility.Collapsed;
                    MoreLabel.Visibility     = Visibility.Collapsed;
                    MoreTextBlock.Visibility = Visibility.Collapsed;
                }

                AnswerDivider.Visibility  = Visibility.Visible;
                AnswerScroller.Visibility = Visibility.Visible;

                // Show the START of a new answer, not the end of it.
                //
                // This scrolled to the bottom on every update, which is right for a
                // chat log and wrong here: the overlay is what a candidate reads out
                // loud, and the words they need first are the first ones. By the time
                // an answer finished streaming they were looking at the MORE TO SAY
                // bullets at the end, with the spoken answer scrolled off the top.
                //
                // Only on a new answer. While one keeps streaming the position is left
                // alone, so a reader who has scrolled is not dragged somewhere else
                // mid-sentence.
                if (wasEmpty)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        new Action(() => AnswerScroller.ScrollToTop()));
            }
            else
            {
                AnswerTextBlock.Text      = "";
                MoreTextBlock.Text        = "";
                MoreDivider.Visibility    = Visibility.Collapsed;
                MoreLabel.Visibility      = Visibility.Collapsed;
                MoreTextBlock.Visibility  = Visibility.Collapsed;
                AnswerDivider.Visibility  = Visibility.Collapsed;
                AnswerScroller.Visibility = Visibility.Collapsed;
            }

            RefreshIdleHint();
        }

        // ── Idle hint ─────────────────────────────────────────────────────────
        private void RefreshIdleHint()
        {
            bool idle = !_isListening && !_isProcessing && !_hasAnswer;
            IdleHintLabel.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Mirrors the main window's watch switch, so the two windows never
        /// disagree about whether the screen is being read.
        ///
        /// The label and icon live inside the button's ControlTemplate, which has
        /// its own namescope, so they are not fields on this class and have to be
        /// looked up through the template once it has been applied.
        /// </summary>
        public void SetWatchScreenState(bool watching)
        {
            try
            {
                if (AnalyzeBtn == null) return;
                AnalyzeBtn.ApplyTemplate();

                if (AnalyzeBtn.Template?.FindName("AnalyzeBtnLabel", AnalyzeBtn) is not System.Windows.Controls.TextBlock label ||
                    AnalyzeBtn.Template?.FindName("AnalyzeBtnIcon",  AnalyzeBtn) is not System.Windows.Shapes.Path icon)
                    return;

                // The button reads the screen once; it is not a switch, so the
                // label stays put. Only the colour moves, to show whether the
                // app is also watching on its own - which is a Settings
                // preference and not something this button changes.
                label.Text = "Read screen";

                var brush = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                        watching ? "#EDF4FF" : "#A7B6C8"));
                label.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xF4, 0xF7, 0xFC));
                icon.Stroke = brush;
            }
            catch
            {
                // A label that fails to repaint is not worth taking the overlay
                // down for mid-interview.
            }
        }

        // ── Read the screen once (same as F8) ──────────────────────────────────────────────────────
        private void AnalyzeBtn_Click(object sender, RoutedEventArgs e)
        {
            AnalyzeRequested?.Invoke();
        }

        // ── Close ─────────────────────────────────────────────────────────────
        private void HideOverlay_Click(object sender, RoutedEventArgs e)
        {
            ToggleCameraMode(false);
            CameraModeClosedByUser?.Invoke();
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static string FormatForReading(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            // Take the fences off without taking the code out.
            //
            // The main window lifts fenced code into a monospace panel of its
            // own, and stopped stripping the fences so that it could find them.
            // This overlay has no such panel and was handed the same text, so a
            // candidate in compact mode — the mode used when somebody is sitting
            // across a desk from them — read their answer with ```cpp printed
            // through the middle of it.
            //
            // The code itself stays. In compact mode this window is the only
            // place it appears.
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"^[ \t]*```[A-Za-z0-9+#_-]*[ \t]*$\n?", "",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            text = text.Replace("```", "");
            if (!text.Contains("•")) return text.Trim();

            var lines  = text.Split('\n');
            var result = new System.Text.StringBuilder();
            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("•"))
                {
                    // No blank line between bullets any more. It was there to keep
                    // them apart when they shared one block with the spoken answer;
                    // they now have a block of their own, where line height already
                    // separates them and the blank lines only cost vertical space
                    // the scroller does not have.
                    result.AppendLine(trimmed);
                }
                else if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    result.AppendLine(trimmed);
                }
            }
            return result.ToString().Trim();
        }
    }
}
