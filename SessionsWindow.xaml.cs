using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;

namespace InterviewCopilot
{
    public partial class SessionsWindow : Window
    {
        // ── Path to where sessions are saved ──────────────────────────────────
        private string AppDataFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InterviewCopilot");

        private List<SessionInfo> _sessions = new();
        private SessionInfo? _selectedSession;

        public SessionsWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => LoadSessions();
        }

        // ══════════════════════════════════════════════════════════════════════
        // LOAD & DISPLAY SESSION LIST
        // ══════════════════════════════════════════════════════════════════════

        private void LoadSessions()
        {
            _sessions.Clear();
            SessionsList.Items.Clear();

            if (!Directory.Exists(AppDataFolder))
            {
                ShowEmptyState();
                return;
            }

            var files = Directory.GetFiles(AppDataFolder, "interview_*.txt")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            if (files.Count == 0)
            {
                ShowEmptyState();
                return;
            }

            EmptyState.Visibility = Visibility.Collapsed;

            foreach (var file in files)
            {
                try
                {
                    var info = ParseSessionFile(file);
                    _sessions.Add(info);
                    SessionsList.Items.Add(info);
                }
                catch { /* skip corrupted file */ }
            }

            SubtitleLabel.Text = $"{_sessions.Count} session{(_sessions.Count != 1 ? "s" : "")} recorded";
        }

        private void ShowEmptyState()
        {
            EmptyState.Visibility = Visibility.Visible;
            SubtitleLabel.Text = "No sessions yet";
        }

        // ── Parse a session file into a SessionInfo object ────────────────────
        private SessionInfo ParseSessionFile(string path)
        {
            var lines = File.ReadAllLines(path);
            var created = File.GetLastWriteTime(path);

            // Header format: "SESSION 3 | model-name | 2026-05-01 14:22:05"
            string header = lines.Length > 0 ? lines[0].Trim() : "";
            int sessionNum = 0;
            string modelName = "—";
            DateTime sessionDate = created;

            if (header.StartsWith("SESSION "))
            {
                var parts = header.Split('|');
                if (parts.Length >= 1)
                    int.TryParse(parts[0].Replace("SESSION", "").Trim(), out sessionNum);
                if (parts.Length >= 2)
                    modelName = parts[1].Trim();
                if (parts.Length >= 3 && DateTime.TryParse(parts[2].Trim(), out var parsedDate))
                    sessionDate = parsedDate;
            }

            int qCount = lines.Count(l => l.StartsWith("Q: "));
            int aCount = lines.Count(l => l.StartsWith("A: "));

            return new SessionInfo
            {
                FilePath = path,
                SessionNumber = sessionNum,
                CreatedAt = sessionDate,
                QuestionCount = qCount,
                AnswerCount = aCount,
                ModelName = modelName,
                AllLines = lines
            };
        }

        // ══════════════════════════════════════════════════════════════════════
        // SHARED Q&A PARSER — single source of truth for RenderTranscript
        //                     and CopyTranscriptBtn_Click
        // ══════════════════════════════════════════════════════════════════════

        private static List<(string Q, string A)> ParseQaPairs(string[] lines)
        {
            var pairs    = new List<(string Q, string A)>();
            string currentQ = "";
            var    currentA = new System.Text.StringBuilder();
            bool   inAnswer = false;

            foreach (var line in lines)
            {
                if (line.StartsWith("Q: "))
                {
                    if (!string.IsNullOrWhiteSpace(currentQ))
                        pairs.Add((currentQ, currentA.ToString().Trim()));

                    currentQ = line.Substring(3).Trim();
                    currentA.Clear();
                    inAnswer = false;
                }
                else if (line.StartsWith("A: "))
                {
                    currentA.Clear();
                    currentA.AppendLine(line.Substring(3).Trim());
                    inAnswer = true;
                }
                else if (inAnswer && !string.IsNullOrWhiteSpace(line))
                {
                    currentA.AppendLine(line.Trim());
                }
            }

            if (!string.IsNullOrWhiteSpace(currentQ))
                pairs.Add((currentQ, currentA.ToString().Trim()));

            return pairs;
        }

        // ══════════════════════════════════════════════════════════════════════
        // TRANSCRIPT RENDERING
        // ══════════════════════════════════════════════════════════════════════

        private void SessionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SessionsList.SelectedItem is not SessionInfo info) return;
            _selectedSession = info;

            // Show detail header
            DetailHeader.Visibility = Visibility.Visible;
            DetailTitle.Text = info.DisplayTitle;
            DetailMeta.Text = $"{info.DisplayDate}  ·  {info.DisplayStats}  ·  {info.DisplayModel}";

            // Show delete button
            DeleteBtn.Visibility = Visibility.Visible;

            // Render transcript
            RenderTranscript(info);
        }

        private void RenderTranscript(SessionInfo info)
        {
            TranscriptPanel.Children.Clear();
            NoSelectionState.Visibility = Visibility.Collapsed;
            TranscriptScroll.Visibility = Visibility.Visible;

            var pairs = ParseQaPairs(info.AllLines);

            if (pairs.Count == 0)
            {
                AddTextBlock("No Q&A recorded in this session.", "#6b7280", 13, false);
                return;
            }

            // Render each Q&A exchange
            int i = 1;
            foreach (var (q, a) in pairs)
            {
                // Exchange number badge
                var badge = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E3A5F")),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 3, 8, 3),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                badge.Child = new TextBlock
                {
                    Text = $"Exchange {i}",
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8")),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Segoe UI")
                };
                TranscriptPanel.Children.Add(badge);

                // Question block
                var qBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(180, 15, 20, 40)),
                    CornerRadius = new CornerRadius(8),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(14, 10, 14, 10),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                var qStack = new StackPanel();
                qStack.Children.Add(new TextBlock
                {
                    Text = "INTERVIEWER",
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6b7280")),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Segoe UI"),
                    Margin = new Thickness(0, 0, 0, 4)
                });
                qStack.Children.Add(new TextBlock
                {
                    Text = q,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1")),
                    FontSize = 13,
                    FontFamily = new FontFamily("Segoe UI"),
                    TextWrapping = TextWrapping.Wrap
                });
                qBorder.Child = qStack;
                TranscriptPanel.Children.Add(qBorder);

                // Answer block
                if (!string.IsNullOrWhiteSpace(a))
                {
                    var aBorder = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(180, 8, 15, 30)),
                        CornerRadius = new CornerRadius(8),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(80, 30, 90, 80)),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(14, 10, 14, 10),
                        Margin = new Thickness(20, 0, 0, 16)
                    };
                    var aStack = new StackPanel();
                    aStack.Children.Add(new TextBlock
                    {
                        Text = "CANDIDATE",
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4ade80")),
                        FontSize = 9,
                        FontWeight = FontWeights.Bold,
                        FontFamily = new FontFamily("Segoe UI"),
                        Margin = new Thickness(0, 0, 0, 4)
                    });
                    aStack.Children.Add(new TextBlock
                    {
                        Text = a,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E2E8F0")),
                        FontSize = 13,
                        FontFamily = new FontFamily("Segoe UI"),
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 20
                    });
                    aBorder.Child = aStack;
                    TranscriptPanel.Children.Add(aBorder);
                }
                i++;
            }

            TranscriptScroll.ScrollToTop();
        }

        private void AddTextBlock(string text, string hex, int size, bool bold)
        {
            TranscriptPanel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                FontSize = size,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
        }

        // ══════════════════════════════════════════════════════════════════════
        // DELETE
        // ══════════════════════════════════════════════════════════════════════

        private void DeleteBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSession == null) return;

            var result = MessageBox.Show(
                $"Delete \"{_selectedSession.DisplayTitle}\" ({_selectedSession.DisplayDate})?\n\nThis cannot be undone.",
                "Delete Session", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                if (File.Exists(_selectedSession.FilePath))
                    File.Delete(_selectedSession.FilePath);

                // Clear transcript view
                TranscriptPanel.Children.Clear();
                TranscriptScroll.Visibility = Visibility.Collapsed;
                NoSelectionState.Visibility = Visibility.Visible;
                DetailHeader.Visibility = Visibility.Collapsed;
                DeleteBtn.Visibility = Visibility.Collapsed;
                _selectedSession = null;

                // Reload list
                LoadSessions();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not delete session: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // COPY TRANSCRIPT
        // ══════════════════════════════════════════════════════════════════════

        private void CopyTranscriptBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSession == null) return;

            var pairs = ParseQaPairs(_selectedSession.AllLines);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(_selectedSession.DisplayTitle);
            sb.AppendLine(_selectedSession.DisplayDate);
            sb.AppendLine(_selectedSession.DisplayStats);
            sb.AppendLine(new string('─', 50));
            sb.AppendLine();

            int exchangeNum = 1;
            foreach (var (q, a) in pairs)
            {
                sb.AppendLine($"[Exchange {exchangeNum++}]");
                sb.AppendLine($"INTERVIEWER: {q}");
                sb.AppendLine($"CANDIDATE:   {a}");
                sb.AppendLine();
            }

            try
            {
                Clipboard.SetText(sb.ToString());
                // Flash the button label for 2 seconds to confirm copy
                CopyTranscriptBtn.Content = "✓ Copied!";
                var timer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (s, _) => { CopyTranscriptBtn.Content = "📋 Copy Text"; timer.Stop(); };
                timer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Copy failed: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // EXPORT
        // ══════════════════════════════════════════════════════════════════════

        private void ExportBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSession == null) return;

            var dlg = new SaveFileDialog
            {
                Title = "Export Session Transcript",
                Filter = "Text File (*.txt)|*.txt",
                FileName = $"InterviewSession_{_selectedSession.SessionNumber}_{_selectedSession.CreatedAt:yyyy-MM-dd}.txt",
                DefaultExt = ".txt"
            };

            if (dlg.ShowDialog() != true) return;

            try
            {
                File.Copy(_selectedSession.FilePath, dlg.FileName, overwrite: true);
                MessageBox.Show("Session exported successfully.", "Export Complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // WINDOW CHROME
        // ══════════════════════════════════════════════════════════════════════

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // DATA MODEL
    // ══════════════════════════════════════════════════════════════════════════

    public class SessionInfo
    {
        public string   FilePath      { get; set; } = "";
        public int      SessionNumber { get; set; }
        public DateTime CreatedAt     { get; set; }
        public int      QuestionCount { get; set; }
        public int      AnswerCount   { get; set; }
        public string   ModelName     { get; set; } = "";
        public string[] AllLines      { get; set; } = Array.Empty<string>();

        // ── Formatted display properties (used by XAML DataTemplate bindings) ──
        public string DisplayTitle =>
            SessionNumber > 0 ? $"Session #{SessionNumber}" : "Session";

        public string DisplayDate =>
            CreatedAt.ToString("MMM dd, yyyy  ·  h:mm tt");

        public string DisplayStats =>
            $"{QuestionCount} question{(QuestionCount != 1 ? "s" : "")}";

        public string DisplayModel =>
            string.IsNullOrWhiteSpace(ModelName) || ModelName == "—"
                ? ""
                : ModelName.Length > 20 ? ModelName.Substring(0, 20) + "…" : ModelName;
    }
}
