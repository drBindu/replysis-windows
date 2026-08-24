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

            // Present only on sessions ended by a build that records it.
            int durationSeconds = 0;
            var durationLine = lines.FirstOrDefault(l => l.StartsWith(MainWindow.SessionDurationTag));
            if (durationLine != null)
                int.TryParse(durationLine.Substring(MainWindow.SessionDurationTag.Length).Trim(),
                             out durationSeconds);

            return new SessionInfo
            {
                FilePath = path,
                SessionNumber = sessionNum,
                CreatedAt = sessionDate,
                QuestionCount = qCount,
                AnswerCount = aCount,
                DurationSeconds = durationSeconds,
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
                // Trailing metadata, not transcript. Without this it would be
                // appended onto the end of the final answer.
                if (line.StartsWith(MainWindow.SessionDurationTag)) continue;

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
        // QUESTION CLASSIFICATION
        // Keyword matching over the question text, not a model call. It is here
        // to show the shape of a session at a glance, so it stays deliberately
        // conservative and falls back to "General" rather than guessing.
        // ══════════════════════════════════════════════════════════════════════

        private static readonly string[] BehaviouralCues =
        {
            "tell me about", "describe a time", "give me an example", "walk me through a time",
            "conflict", "disagree", "weakness", "strength", "proud of", "failure", "failed",
            "challenge you", "difficult team", "why do you want", "where do you see yourself",
            "tell us about yourself", "about yourself"
        };

        private static readonly string[] SystemDesignCues =
        {
            "system design", "design a", "design an", "architecture", "scale", "scalable",
            "load balancer", "microservice", "distributed", "throughput", "sharding",
            "caching", "cache", "high availability", "fault tolerant", "rate limit"
        };

        private static readonly string[] CodingCues =
        {
            "algorithm", "time complexity", "space complexity", "big o", "o(n", "binary tree",
            "linked list", "array", "hash map", "hashmap", "recursion", "sort", "sorting",
            "implement", "write a function", "write code", "leetcode", "optimize this",
            "data structure", "pointer", "traverse"
        };

        internal enum QuestionKind { Screen, Behavioural, SystemDesign, Coding, General }

        internal static QuestionKind ClassifyQuestion(string question)
        {
            if (string.IsNullOrWhiteSpace(question)) return QuestionKind.General;

            string q = question.ToLowerInvariant();

            if (q.Contains("[screen analysis]")) return QuestionKind.Screen;
            if (BehaviouralCues.Any(c => q.Contains(c))) return QuestionKind.Behavioural;
            if (SystemDesignCues.Any(c => q.Contains(c))) return QuestionKind.SystemDesign;
            if (CodingCues.Any(c => q.Contains(c))) return QuestionKind.Coding;

            return QuestionKind.General;
        }

        internal static string KindLabel(QuestionKind kind) => kind switch
        {
            QuestionKind.Screen       => "FROM SCREEN",
            QuestionKind.Behavioural  => "BEHAVIOURAL",
            QuestionKind.SystemDesign => "SYSTEM DESIGN",
            QuestionKind.Coding       => "CODING",
            _                         => "GENERAL"
        };

        // Accent per category so a long transcript can be skimmed by colour.
        private static string KindColour(QuestionKind kind) => kind switch
        {
            QuestionKind.Screen       => "#7FB4E8",
            QuestionKind.Behavioural  => "#E8C07F",
            QuestionKind.SystemDesign => "#C79FE8",
            QuestionKind.Coding       => "#7FE8B4",
            _                         => "#7E90A8"
        };

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

            RenderSessionStats(info);
            RenderTranscript(info);
        }

        // ── Summary chips: how long, how much was asked, and of what kind ─────
        private void RenderSessionStats(SessionInfo info)
        {
            SessionStats.Items.Clear();

            var pairs = ParseQaPairs(info.AllLines);
            if (pairs.Count == 0) return;

            if (info.DurationSeconds > 0)
                SessionStats.Items.Add(BuildStatChip("Lasted " + FormatDuration(info.DurationSeconds), "#7E90A8"));

            SessionStats.Items.Add(BuildStatChip(
                pairs.Count + (pairs.Count != 1 ? " questions" : " question"), "#7E90A8"));

            // Longest answer is a better signal of a heavy question than an average,
            // which a couple of one-line replies would flatten.
            int longest = pairs.Max(p => WordCount(p.A));
            if (longest > 0)
                SessionStats.Items.Add(BuildStatChip("Longest answer " + longest + " words", "#7E90A8"));

            foreach (var group in pairs
                        .GroupBy(p => ClassifyQuestion(p.Q))
                        .OrderByDescending(g => g.Count()))
            {
                SessionStats.Items.Add(BuildStatChip(
                    group.Count() + " " + KindLabel(group.Key).ToLowerInvariant(),
                    KindColour(group.Key)));
            }
        }

        private static int WordCount(string text) =>
            string.IsNullOrWhiteSpace(text)
                ? 0
                : text.Split(new[] { ' ', '\n', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;

        private static string FormatDuration(int seconds)
        {
            if (seconds < 60) return seconds + "s";
            int m = seconds / 60, s = seconds % 60;
            if (m < 60) return s == 0 ? m + "m" : m + "m " + s + "s";
            return (m / 60) + "h " + (m % 60) + "m";
        }

        private static Border BuildStatChip(string text, string hex)
        {
            var chip = new Border
            {
                Background      = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                BorderBrush     = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(8, 3, 8, 3),
                Margin          = new Thickness(0, 0, 6, 6)
            };
            chip.Child = new TextBlock
            {
                Text       = text,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                FontSize   = 10.5,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI")
            };
            return chip;
        }

        private void RenderTranscript(SessionInfo info)
        {
            TranscriptPanel.Children.Clear();
            NoSelectionState.Visibility = Visibility.Collapsed;
            TranscriptScroll.Visibility = Visibility.Visible;

            var pairs = ParseQaPairs(info.AllLines);

            if (pairs.Count == 0)
            {
                AddTextBlock("No Q&A recorded in this session.", "#7E90A8", 13, false);
                return;
            }

            // Render each Q&A exchange
            int i = 1;
            foreach (var (q, a) in pairs)
            {
                var kind = ClassifyQuestion(q);

                // Exchange number plus what kind of question it was, so a long
                // transcript can be skimmed for "where were the coding rounds".
                var badgeRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var badge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 3, 8, 3),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                badge.Child = new TextBlock
                {
                    Text = $"Q{i}",
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF")),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Segoe UI")
                };
                badgeRow.Children.Add(badge);

                badgeRow.Children.Add(new TextBlock
                {
                    Text = KindLabel(kind),
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(KindColour(kind))),
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(9, 0, 0, 0)
                });

                TranscriptPanel.Children.Add(badgeRow);

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
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7E90A8")),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Segoe UI"),
                    Margin = new Thickness(0, 0, 0, 5)
                });
                qStack.Children.Add(new TextBlock
                {
                    Text = q,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C6D4E8")),
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
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EAF1F8")),
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
        // TRANSCRIPT DOCUMENT
        // Shared by Copy and Export so the two can never drift apart.
        // ══════════════════════════════════════════════════════════════════════

        private string BuildTranscriptDocument(SessionInfo info)
        {
            var pairs = ParseQaPairs(info.AllLines);
            var sb = new System.Text.StringBuilder();

            sb.AppendLine(info.DisplayTitle);
            sb.AppendLine(info.DisplayDate);

            var summary = new List<string> { info.DisplayStats };
            if (info.DurationSeconds > 0) summary.Add("lasted " + FormatDuration(info.DurationSeconds));
            sb.AppendLine(string.Join("  |  ", summary));

            foreach (var group in pairs.GroupBy(p => ClassifyQuestion(p.Q)).OrderByDescending(g => g.Count()))
                sb.AppendLine($"  {group.Count()} x {KindLabel(group.Key).ToLowerInvariant()}");

            sb.AppendLine(new string('-', 60));
            sb.AppendLine();

            int exchangeNum = 1;
            foreach (var (q, a) in pairs)
            {
                sb.AppendLine($"[{exchangeNum++}] {KindLabel(ClassifyQuestion(q))}");
                sb.AppendLine($"INTERVIEWER:   {q}");
                // This is what the assistant suggested, not what the candidate
                // actually said. Labelling it CANDIDATE misrepresented the record.
                sb.AppendLine($"AI SUGGESTED:  {a}");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        // ══════════════════════════════════════════════════════════════════════
        // COPY TRANSCRIPT
        // ══════════════════════════════════════════════════════════════════════

        private void CopyTranscriptBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSession == null) return;

            try
            {
                Clipboard.SetText(BuildTranscriptDocument(_selectedSession));
                // Flash the button label for 2 seconds to confirm copy
                CopyTranscriptBtn.Content = "Copied";
                var timer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (s, _) => { CopyTranscriptBtn.Content = "Copy Text"; timer.Stop(); };
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
                // Was dumping the raw internal lines, so the exported file still
                // carried the "SESSION n | model | date" header and Q:/A: prefixes.
                File.WriteAllText(dlg.FileName, BuildTranscriptDocument(_selectedSession), Encoding.UTF8);
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
        public int      DurationSeconds { get; set; }
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
                : ModelName.Length > 30 ? ModelName.Substring(0, 30) + "..." : ModelName;
    }
}
