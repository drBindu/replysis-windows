using System;
using System.Windows;
using System.Windows.Media;

namespace InterviewCopilot
{
    public partial class AnswerWindow : Window
    {
        public bool IsCameraModeActive = false;
        public event Action? CameraModeClosedByUser;

        public AnswerWindow()
        {
            InitializeComponent();

            this.Loaded += (s, e) =>
            {
                // Position: top-center of screen, just below the camera
                var screen = SystemParameters.WorkArea;
                this.Left = (screen.Width - this.Width) / 2;
                this.Top = 8;

                // Apply saved overlay opacity
                ApplyOverlayOpacity();
            };
        }

        // Call this after settings are saved to refresh opacity without reopening
        public void ApplyOverlayOpacity()
        {
            double opacity = SettingsWindow.GetOverlayOpacity();
            MainBorder.Opacity = IsCameraModeActive ? opacity : 0;
        }

        public void ToggleCameraMode(bool active)
        {
            IsCameraModeActive = active;
            if (active)
            {
                this.Show();
                MainBorder.Opacity = SettingsWindow.GetOverlayOpacity();
            }
            else
            {
                MainBorder.Opacity = 0;
                this.Hide();
            }
        }

        public void UpdateMicState(bool isListening, bool isProcessing)
        {
            if (isProcessing)
            {
                MicStatusLabel.Text = "THINKING...";
                MiniMicIndicator.Fill = Brushes.Orange;
                MiniMicGlow.Color = Colors.Orange;
                MiniMicGlow.BlurRadius = 14;
            }
            else if (isListening)
            {
                MicStatusLabel.Text = "LISTENING";
                MiniMicIndicator.Fill = Brushes.LimeGreen;
                MiniMicGlow.Color = Colors.LimeGreen;
                MiniMicGlow.BlurRadius = 16;
            }
            else
            {
                MicStatusLabel.Text = "READY";
                MiniMicIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                MiniMicGlow.Color = Color.FromRgb(239, 68, 68);
                MiniMicGlow.BlurRadius = 8;
            }
        }

        public void UpdateQuestion(string text)
        {
            QuestionTextBlock.Text = string.IsNullOrWhiteSpace(text)
                ? "Waiting for audio..."
                : text;
        }

        public void UpdateAnswer(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                AnswerTextBlock.Text = "";
                return;
            }
            AnswerTextBlock.Text = FormatForReading(text);
        }

        // Adds blank line between each bullet for easy eye scanning
        private static string FormatForReading(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            if (text.Contains("•"))
            {
                var lines = text.Split('\n');
                var result = new System.Text.StringBuilder();
                foreach (var line in lines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("•"))
                    {
                        if (result.Length > 0)
                            result.AppendLine();
                        result.AppendLine(trimmed);
                    }
                    else if (!string.IsNullOrWhiteSpace(trimmed))
                    {
                        result.AppendLine(trimmed);
                    }
                }
                return result.ToString().Trim();
            }
            return text.Trim();
        }

        private void HideOverlay_Click(object sender, RoutedEventArgs e)
        {
            ToggleCameraMode(false);
            CameraModeClosedByUser?.Invoke();
        }
    }
}