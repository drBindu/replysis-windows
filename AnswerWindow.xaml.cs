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
            this.Left = Math.Clamp(this.Left + dxPx * sx, wa.Left, wa.Right  - this.ActualWidth);
            this.Top  = Math.Clamp(this.Top  + dyPx * sy, wa.Top,  wa.Bottom - this.ActualHeight);
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
            // This reuses the main window's glass-opacity preference, but the main
            // window's glass look works because its individual cards and buttons have
            // their own opaque backing — the outer frame can be very transparent and
            // everything on it stays readable. This overlay has no such inner panel:
            // MainBorder is the ONLY background layer behind the answer text. At the
            // old 0.06 floor, and even at the 0.62 default (~24% opaque), whatever sat
            // behind the window on the user's screen read through clearly enough to
            // overlap and obscure the answer they opened this window to read. The
            // floor is raised so the backdrop never drops below legible, while the
            // slider still visibly darkens it further above that floor.
            double backdropOpacity = Math.Clamp((opacity - 0.50) / 0.50, 0.55, 1.0);
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
                MicStatusLabel.Text       = "Thinking…";
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
            }
            else
            {
                QuestionTextBlock.Text       = "Listening…";
                QuestionTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)); // #64748b
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
                AnswerTextBlock.Text      = FormatForReading(text);
                AnswerDivider.Visibility  = Visibility.Visible;
                AnswerScroller.Visibility = Visibility.Visible;

                // Scroll to bottom so the latest streamed content is always visible
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(() => AnswerScroller.ScrollToBottom()));
            }
            else
            {
                AnswerTextBlock.Text      = "";
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

        // ── Analyze button ────────────────────────────────────────────────────
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
            if (!text.Contains("•")) return text.Trim();

            var lines  = text.Split('\n');
            var result = new System.Text.StringBuilder();
            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("•"))
                {
                    if (result.Length > 0) result.AppendLine();
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
