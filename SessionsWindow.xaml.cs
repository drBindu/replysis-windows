using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
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
        private readonly System.Threading.CancellationTokenSource _cts = new();

        public SessionsWindow()
        {
            InitializeComponent();
            try { WindowStealth.SetStealthMode(this, SettingsWindow.GetStealthMode()); } catch { }
            Loaded += (s, e) => { LoadSessions(); _ = FetchCloudSessionsAsync(); };
        }

        protected override void OnClosed(EventArgs e) { _cts.Cancel(); _cts.Dispose(); base.OnClosed(e); }

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
                    if (info.QuestionCount <= 0) continue;
                    _sessions.Add(info);
                    SessionsList.Items.Add(info);
                }
                catch { /* skip corrupted file */ }
            }

            if (_sessions.Count == 0)
            {
                ShowEmptyState();
                return;
            }

            SubtitleLabel.Text = $"{_sessions.Count} session{(_sessions.Count != 1 ? "s" : "")} recorded";
            SessionsList.SelectedIndex = 0;
        }

        private void ShowEmptyState()
        {
            EmptyState.Visibility = Visibility.Visible;
            SubtitleLabel.Text = "No sessions yet";
        }

        // ── Fetch cloud sessions and merge with the local list ────────────────
        // Runs after LoadSessions() completes. Skipped if not signed in.
        // Cloud sessions are appended and the combined list is re-sorted newest-first.
        // Matches the Mac app's merge behaviour exactly.
        private async Task FetchCloudSessionsAsync()
        {
            if (!UserSession.IsLoggedIn) return;
            try
            {
                Dispatcher.Invoke(() =>
                {
                    SubtitleLabel.Text = _sessions.Count == 0
                        ? "Checking cloud backup..."
                        : $"{_sessions.Count} local · checking cloud...";
                });

                if (UserSession.IsTokenExpired())
                    await UserSession.TryRefreshAsync();

                string token = UserSession.IdToken;
                if (string.IsNullOrEmpty(token)) return;

                string? rawEmail = UserSession.Email;
                if (string.IsNullOrEmpty(rawEmail)) return;
                string email = Uri.EscapeDataString(rawEmail);
                using var req = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{SettingsWindow.GetBackendUrl()}/api/sessions?email={email}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var res = await SharedHttpClient.HttpShort.SendAsync(req, _cts.Token);
                if (!res.IsSuccessStatusCode) return;

                string body = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("sessions", out var sessionsEl)) return;

                var cloudSessions = new List<SessionInfo>();
                foreach (var s in sessionsEl.EnumerateArray())
                {
                    var info = ParseCloudSession(s);
                    if (info != null) cloudSessions.Add(info);
                }

                if (cloudSessions.Count == 0) return;
                if (_cts.IsCancellationRequested) return;

                Dispatcher.Invoke(() =>
                {
                    foreach (var cs in cloudSessions)
                    {
                        if (!_sessions.Any(existing => SessionsMatch(existing, cs)))
                            _sessions.Add(cs);
                    }

                    // Re-sort combined list newest-first
                    var sorted = _sessions.OrderByDescending(s => s.CreatedAt).ToList();
                    _sessions.Clear();
                    SessionsList.Items.Clear();
                    foreach (var s in sorted)
                    {
                        _sessions.Add(s);
                        SessionsList.Items.Add(s);
                    }

                    EmptyState.Visibility = Visibility.Collapsed;
                    SubtitleLabel.Text =
                        $"{_sessions.Count} session{(_sessions.Count != 1 ? "s" : "")} recorded";
                    if (SessionsList.SelectedIndex < 0 && SessionsList.Items.Count > 0)
                        SessionsList.SelectedIndex = 0;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SESSIONS_CLOUD] fetch failed: {ex.Message}");
            }
            finally
            {
                if (!_cts.IsCancellationRequested)
                    Dispatcher.Invoke(() => SubtitleLabel.Text = _sessions.Count == 0
                        ? "No sessions yet"
                        : $"{_sessions.Count} session{(_sessions.Count != 1 ? "s" : "")} recorded");
            }
        }

        // ── Convert a cloud session JSON element into a SessionInfo ───────────
        private static SessionInfo? ParseCloudSession(JsonElement s)
        {
            try
            {
                // Resolve creation time from the pre-computed seconds field the API returns
                long createdSeconds = 0;
                if (s.TryGetProperty("_createdAtSeconds", out var csec))
                    createdSeconds = csec.GetInt64();

                DateTime createdAt = createdSeconds > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(createdSeconds).LocalDateTime
                    : DateTime.UtcNow;

                // Rebuild the turns into the same Q:/A: line format as local .txt files
                // so all existing parsing / rendering code works unchanged.
                var lineList = new List<string> { "CLOUD SESSION" };
                if (s.TryGetProperty("turns", out var turnsEl))
                {
                    string pendingQ = "";
                    foreach (var turn in turnsEl.EnumerateArray())
                    {
                        string role = turn.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
                        string text = turn.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                        if (role == "interviewer")
                        {
                            if (!string.IsNullOrEmpty(pendingQ))
                            { lineList.Add($"Q: {pendingQ}"); lineList.Add("A: "); lineList.Add(""); }
                            pendingQ = text;
                        }
                        else if (role == "candidate")
                        {
                            lineList.Add($"Q: {pendingQ}");
                            lineList.Add($"A: {text}");
                            lineList.Add("");
                            pendingQ = "";
                        }
                    }
                    if (!string.IsNullOrEmpty(pendingQ))
                    { lineList.Add($"Q: {pendingQ}"); lineList.Add("A: "); lineList.Add(""); }
                }

                int qCount = s.TryGetProperty("questionCount", out var qc) ? qc.GetInt32() : 0;

                return new SessionInfo
                {
                    FilePath      = "",            // no local file
                    SessionNumber = 0,
                    IsCloud       = true,
                    CloudSessionId = s.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    CreatedAt     = createdAt,
                    QuestionCount = qCount,
                    AnswerCount   = qCount,
                    ModelName     = "Web",
                    AllLines      = lineList.ToArray(),
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SESSIONS_CLOUD] parse failed: {ex.Message}");
                return null;
            }
        }

        // ── Parse a session file into a SessionInfo object ────────────────────
        private SessionInfo ParseSessionFile(string path)
        {
            var lines = SecureDataProtector.ReadProtectedFile(path)
                .Replace("\r\n", "\n").Replace("\r", "\n")
                .Split('\n');
            var created = File.GetLastWriteTime(path);

            // Header format: "SESSION 3 | model-name | 2026-05-01 14:22:05"
            string header = lines.Length > 0 ? lines[0].Trim() : "";
            int sessionNum = 0;
            string modelName = "";
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

        private static bool SessionsMatch(SessionInfo first, SessionInfo second)
        {
            var firstPairs = ParseQaPairs(first.AllLines);
            var secondPairs = ParseQaPairs(second.AllLines);
            if (firstPairs.Count == 0 || firstPairs.Count != secondPairs.Count) return false;

            for (int index = 0; index < firstPairs.Count; index++)
            {
                if (!string.Equals(firstPairs[index].Q, secondPairs[index].Q, StringComparison.Ordinal) ||
                    !string.Equals(firstPairs[index].A, secondPairs[index].A, StringComparison.Ordinal))
                    return false;
            }

            return true;
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

            // Cloud sessions have no delete API — hide the button (matches Mac behaviour)
            DeleteBtn.Visibility = info.IsCloud ? Visibility.Collapsed : Visibility.Visible;

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
                AddTextBlock("No Q&A recorded in this session.", "#90AABC", 13, false);
                return;
            }

            // Render each Q&A exchange
            int i = 1;
            foreach (var (q, a) in pairs)
            {
                // Exchange number badge
                var badge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 3, 8, 3),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                badge.Child = new TextBlock
                {
                    Text = $"Q{i}",
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF")),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
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
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#90AABC")),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Segoe UI"),
                    Margin = new Thickness(0, 0, 0, 5)
                });
                qStack.Children.Add(new TextBlock
                {
                    Text = q,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E2EEF8")),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Segoe UI"),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20
                });
                qBorder.Child = qStack;
                TranscriptPanel.Children.Add(qBorder);

                // Answer block
                if (!string.IsNullOrWhiteSpace(a))
                {
                    var aBorder = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                        CornerRadius = new CornerRadius(8),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(14, 10, 14, 10),
                        Margin = new Thickness(20, 0, 0, 16)
                    };
                    var aStack = new StackPanel();
                    aStack.Children.Add(new TextBlock
                    {
                        Text = "AI ANSWER",
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF")),
                        FontSize = 9,
                        FontWeight = FontWeights.Bold,
                        FontFamily = new FontFamily("Segoe UI"),
                        Margin = new Thickness(0, 0, 0, 5)
                    });
                    aStack.Children.Add(new TextBlock
                    {
                        Text = a,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F0F5FF")),
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        FontFamily = new FontFamily("Segoe UI"),
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 21
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
            if (_selectedSession == null || _selectedSession.IsCloud) return;

            var result = MessageBox.Show(this,
                $"Delete \"{_selectedSession.DisplayTitle}\" ({_selectedSession.DisplayDate})?\n\nThis cannot be undone.",
                "Delete Session", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                if (File.Exists(_selectedSession.FilePath))
                    File.Delete(_selectedSession.FilePath);

                string recordingBasePath = Path.Combine(
                    AppDataFolder, $"interview_{_selectedSession.SessionNumber}.wav");
                foreach (string recordingPath in new[] { recordingBasePath, recordingBasePath + ".dpapi" })
                {
                    if (File.Exists(recordingPath)) File.Delete(recordingPath);
                }

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
                MessageBox.Show(this, $"Could not delete session: {ex.Message}", "Error",
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
                MessageBox.Show(this, $"Copy failed: {ex.Message}", "Error",
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
                if (_selectedSession.IsCloud)
                {
                    // Cloud sessions have no local file — write AllLines to disk
                    File.WriteAllLines(dlg.FileName, _selectedSession.AllLines, Encoding.UTF8);
                }
                else
                {
                    File.WriteAllLines(dlg.FileName, _selectedSession.AllLines, Encoding.UTF8);
                }
                MessageBox.Show(this, "Session exported successfully.", "Export Complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Export failed: {ex.Message}", "Error",
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
        public string   FilePath       { get; set; } = "";
        public int      SessionNumber  { get; set; }
        public bool     IsCloud        { get; set; }
        public string   CloudSessionId { get; set; } = "";
        public DateTime CreatedAt      { get; set; }
        public int      QuestionCount  { get; set; }
        public int      AnswerCount    { get; set; }
        public string   ModelName      { get; set; } = "";
        public string[] AllLines       { get; set; } = Array.Empty<string>();

        // ── Formatted display properties (used by XAML DataTemplate bindings) ──
        public string DisplayTitle =>
            IsCloud ? "☁ Web Session" :
            SessionNumber > 0 ? $"Session #{SessionNumber}" : "Session";

        public string DisplayDate =>
            CreatedAt.ToString("MMM dd, yyyy  ·  h:mm tt");

        public string DisplayStats =>
            $"{QuestionCount} question{(QuestionCount != 1 ? "s" : "")}";

        public string DisplayModel =>
            string.IsNullOrWhiteSpace(ModelName)
                ? ""
                : ModelName.Length > 30 ? ModelName.Substring(0, 30) + "…" : ModelName;
    }
}
