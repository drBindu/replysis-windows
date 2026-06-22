using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace InterviewCopilot
{
    public partial class MainWindow : Window
    {
        private const string BackendUrl = "https://coopilotxai.com";

        // ── Tunable constants (change here, takes effect everywhere) ──────────
        private const int    TranscriptPollMs        = 150;   // how often to read latest.txt
        private const int    ThinkingAnimMs          = 800;   // thinking dot animation interval
        private const int    CreditRefreshMinutes    = 5;     // background credits refresh
        private const int    EngineMonitorSecs       = 3;     // how often to check engine health
        private const int    CreditsLowThreshold     = 20;    // amber warning below this
        private const int    CreditsCriticalThreshold= 5;     // red warning / block below this
        private const int    TranscriptRetryCount    = 3;     // retries on torn file read
        private const int    TranscriptRetryDelayMs  = 10;    // delay between retries

        private bool isMuted = true;
        private bool isListening = false;
        private bool isProcessing = false;
        private bool isRecording = false;
        private bool _resumeCollapsed = false;
        private bool _isCameraMode = false;
        private int _audioDeviceId = -1;
        private bool _justStartedListening = false;  // suppress stale reads for 400ms after unmute
        private int  _listenStartTicks = 0;

        private DispatcherTimer? transcriptTimer;
        private DispatcherTimer? thinkingTimer;
        private DispatcherTimer? creditsRefreshTimer;
        private DispatcherTimer? _sessionTimer;
        private int _sessionSeconds = 0;
        private int thinkingStep = 0;

        private Process? speechmaticsProcess;
        private CancellationTokenSource _engineCts = new CancellationTokenSource();
        private string projectRoot = "";
        private string scriptFolder = "";
        private AnswerWindow? answerWindow;
        private Action? _cameraModeClosedHandler;   // stored so we can -= it in OnClosed

        private int sessionNumber = 1;
        private string sessionLogPath = "";

        private GlobalHotkey? _globalHotkey;
        private DebugWindow? _debugWindow;
        private DispatcherTimer? _engineMonitorTimer;

        // HTTP — shared singletons defined in SharedHttpClient.cs
        private static HttpClient _backendClient => SharedHttpClient.Http;
        private static HttpClient _creditsClient => SharedHttpClient.HttpShort;

        private string AppDataFolder
        {
            get
            {
                var p = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "InterviewCopilot");
                Directory.CreateDirectory(p);
                return p;
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            projectRoot = AppDomain.CurrentDomain.BaseDirectory;
            scriptFolder = FindScriptFolder(projectRoot);
            this.PreviewKeyDown += Window_PreviewKeyDown;

            this.Loaded += async (s, e) =>
            {
                // Report presence to Firestore so the admin dashboard shows
                // accurate Live/last-seen (self-guards while logged out)
                PresenceTracker.Start();

                try { IntroPlayer.Play(); } catch { IntroLayer.Visibility = Visibility.Collapsed; }

                try
                {
                    NuclearKillOldProcesses();
                    try { WindowStealth.SetStealthMode(this, true); } catch (Exception ex) { DebugWindow.Log("STEALTH", ex.Message); }

                    answerWindow = new AnswerWindow();
                    answerWindow.ShowInTaskbar = false;
                    // Store the delegate so we can -= it in OnClosed (prevents memory leak)
                    _cameraModeClosedHandler = () => Dispatcher.Invoke(() => ExitCameraMode());
                    answerWindow.CameraModeClosedByUser += _cameraModeClosedHandler;

                    isMuted = true;
                    isListening = false;
                    WritePauseFlag();
                    UpdateMicUi();
                    SavePathLabel.Text = AppDataFolder;
                    _debugWindow = new DebugWindow();

                    ApplyMainWindowOpacity();
                    StartSpeechmaticsEngine();

                    IntPtr mainHwnd = new WindowInteropHelper(this).Handle;
                    try
                    {
                        _globalHotkey = new GlobalHotkey(
                            onSpacePressed:                 () => Dispatcher.Invoke(() => HandleSpacePress("GLOBAL")),
                            onF12Pressed:                   () => Dispatcher.Invoke(() => ToggleDebugWindow()),
                            onKillPressed:                  () => Dispatcher.Invoke(() => Close()),
                            onScreenAnalysisPressed:        () => _ = Dispatcher.InvokeAsync(HandleScreenAnalysisAsync),
                            onPrimaryScreenAnalysisPressed: () => _ = Dispatcher.InvokeAsync(HandlePrimaryScreenAnalysisAsync)
                        );
                        _globalHotkey.OwnerWindowHandle = mainHwnd;
                    }
                    catch (Exception ex) { DebugWindow.Log("HOTKEY_ERR", $"Global hotkey registration failed: {ex.Message}"); }

                    _engineMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(EngineMonitorSecs) };
                    _engineMonitorTimer.Tick += (s2, e2) => MonitorEngine();
                    _engineMonitorTimer.Start();

                    // ── Session restore with silent token refresh ─────────────────
                    // TryLoadFromDisk() returns false when the saved idToken is > 55 min old,
                    // but it still loads RefreshToken into memory. TryRefreshAsync() uses that
                    // refresh token to get a new idToken from Firebase silently — the user
                    // never sees a login screen unless the refresh token itself is invalid.
                    bool sessionRestored = UserSession.TryLoadFromDisk();
                    if (!sessionRestored && !string.IsNullOrEmpty(UserSession.RefreshToken))
                    {
                        DebugWindow.Log("AUTH", "idToken expired — attempting silent refresh…");
                        sessionRestored = await UserSession.TryRefreshAsync();
                        if (sessionRestored)
                            DebugWindow.Log("AUTH", "Silent token refresh succeeded");
                        else
                            DebugWindow.Log("AUTH", "Silent refresh failed — user must re-login");
                    }

                    if (sessionRestored)
                    {
                        await FetchAndDisplayCreditsAsync();
                        UpdateProfileUI();
                        StartNewSession(); // Auto-start recording immediately on app open
                    }
                    else
                    {
                        SetLoggedOutUI();
                    }

                    creditsRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(CreditRefreshMinutes) };
                    creditsRefreshTimer.Tick += async (s2, e2) =>
                    {
                        try
                        {
                            if (UserSession.IsLoggedIn) await FetchAndDisplayCreditsAsync();
                        }
                        catch (Exception ex) { DebugWindow.Log("CREDITS_TIMER", ex.Message); }
                    };
                    creditsRefreshTimer.Start();
                }
                catch (Exception ex)
                {
                    DebugWindow.Log("STARTUP_ERR", $"Window load failed: {ex.Message}");
                }
            };

            transcriptTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TranscriptPollMs) };
            transcriptTimer.Tick += (s, e) => UpdateTranscript();
            transcriptTimer.Start();

            thinkingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ThinkingAnimMs) };
            thinkingTimer.Tick += (s, e) =>
            {
                // Don't overwrite the label when screen analysis is running
                if (_isScreenAnalyzing) return;
                thinkingStep++;
                string dots = new string('.', thinkingStep % 4);
                ThinkingLabel.Text = "Thinking" + dots;
                if (_isCameraMode && answerWindow != null)
                    answerWindow.UpdateAnswer("Thinking" + dots);
            };
        }

        // ══════════════════════════════════════════════════════════════════════
        // LOGIN / LOGOUT
        // ══════════════════════════════════════════════════════════════════════
        private async void SignInHeaderBtn_Click(object sender, RoutedEventArgs e)
        {
            var loginWin = new LoginWindow();
            loginWin.Owner = this;
            loginWin.ShowDialog();
            if (loginWin.LoginSuccess)
            {
                UpdateProfileUI();
                await FetchAndDisplayCreditsAsync();
                StartNewSession(); // Auto-start recording right after sign-in
                DebugWindow.Log("AUTH", $"Logged in: {UserSession.Email}");
            }
        }

        private void ProfileBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var result = MessageBox.Show(
                $"Signed in as {UserSession.Email}\n\nDo you want to sign out?",
                "CoopilotX Account", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                UserSession.Clear();
                SetLoggedOutUI();
                DebugWindow.Log("AUTH", "Signed out");
            }
        }

        private void UpdateProfileUI()
        {
            if (!UserSession.IsLoggedIn) { SetLoggedOutUI(); return; }
            ProfileBadge.Visibility = Visibility.Visible;
            SignInHeaderBtn.Visibility = Visibility.Collapsed;
            SessionsBtn.Visibility = Visibility.Visible;   // show Sessions when logged in
            AvatarInitials.Text = UserSession.Initials;
            ProfileNameLabel.Text = UserSession.Name.Split(' ')[0];
            ProfilePlanLabel.Text = $"{UserSession.Plan} plan";
            if (UserSession.IsUnlimited)
            {
                AvatarInitials.Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#a78bfa"));
                ProfilePlanLabel.Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#a78bfa"));
            }
        }

        private void SetLoggedOutUI()
        {
            ProfileBadge.Visibility = Visibility.Collapsed;
            SignInHeaderBtn.Visibility = Visibility.Visible;
            SessionsBtn.Visibility = Visibility.Collapsed;  // hide Sessions when not logged in
            CreditsLabel.Text = "Sign in";
            CreditsPlanLabel.Visibility = Visibility.Collapsed;
            CreditsIcon.Text = "⚡";
            SetCreditsBadgeStyle("#1a1f2e", "#33FFFFFF");
            CreditsLabel.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#6b7280"));
            // End any active session on sign-out
            if (isRecording) EndSession();
        }

        // ══════════════════════════════════════════════════════════════════════
        // CREDITS
        // ══════════════════════════════════════════════════════════════════════
        private async Task FetchAndDisplayCreditsAsync()
        {
            if (!UserSession.IsLoggedIn) return;
            await UserSession.TryRefreshAsync();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{BackendUrl}/api/v1/interview/credits");
                req.Headers.Add("Authorization", $"Bearer {UserSession.IdToken}");

                var res = await _creditsClient.SendAsync(req);
                string body = await res.Content.ReadAsStringAsync();

                if (!res.IsSuccessStatusCode)
                {
                    DebugWindow.Log("CREDITS", $"HTTP {(int)res.StatusCode}: {body}");
                    Dispatcher.Invoke(() => CreditsLabel.Text = "Offline");
                    return;
                }

                using var doc = JsonDocument.Parse(body);
                int credits = doc.RootElement.TryGetProperty("credits", out var c) ? c.GetInt32() : 0;
                string plan = doc.RootElement.TryGetProperty("plan", out var p) ? p.GetString() ?? "free" : "free";
                bool isUnlimited = doc.RootElement.TryGetProperty("isUnlimited", out var u) ? u.GetBoolean() : false;

                UserSession.Credits = credits;
                UserSession.Plan = plan;

                Dispatcher.Invoke(() =>
                {
                    CreditsPlanLabel.Visibility = Visibility.Visible;
                    ProfilePlanLabel.Text = $"{plan} plan";

                    if (isUnlimited)
                    {
                        CreditsLabel.Text = "∞  Pro";
                        CreditsPlanLabel.Text = "Unlimited";
                        CreditsIcon.Text = "👑";
                        SetCreditsBadgeStyle("#1a0a2e", "#7c3aed");
                        CreditsLabel.Foreground = new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString("#a78bfa"));
                    }
                    else
                    {
                        string display = credits >= 1000 ? $"{credits / 1000.0:F1}k" : credits.ToString("N0");
                        CreditsLabel.Text = $"⚡ {display}";
                        CreditsPlanLabel.Text = plan;
                        CreditsIcon.Text = "";

                        if (credits > CreditsLowThreshold)
                        {
                            SetCreditsBadgeStyle("#0f2a1a", "#1a6b3a");
                            CreditsLabel.Foreground = new SolidColorBrush(
                                (Color)ColorConverter.ConvertFromString("#4ade80"));
                        }
                        else if (credits > CreditsCriticalThreshold)
                        {
                            SetCreditsBadgeStyle("#2a1a0a", "#6b4a1a");
                            CreditsLabel.Foreground = new SolidColorBrush(
                                (Color)ColorConverter.ConvertFromString("#f59e0b"));
                        }
                        else
                        {
                            SetCreditsBadgeStyle("#2a0a0a", "#6b1a1a");
                            CreditsLabel.Foreground = new SolidColorBrush(
                                (Color)ColorConverter.ConvertFromString("#ef4444"));
                        }
                    }
                });

                DebugWindow.Log("CREDITS", $"{(isUnlimited ? "Unlimited" : $"{credits} credits")} | {plan}");
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => CreditsLabel.Text = "—");
                DebugWindow.Log("CREDITS_ERR", ex.Message);
            }
        }

        private void SetCreditsBadgeStyle(string bg, string border)
        {
            CreditsBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
            CreditsBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border));
        }

        private void CreditsBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!UserSession.IsLoggedIn) { SignInHeaderBtn_Click(sender, new RoutedEventArgs()); return; }
            _ = FetchAndDisplayCreditsAsync().ContinueWith(t => {
                if (t.IsFaulted) DebugWindow.Log("CREDITS_ERR", t.Exception?.GetBaseException().Message ?? "unknown");
            }, TaskScheduler.Default);
        }

        // ══════════════════════════════════════════════════════════════════════
        // OPACITY / SCRIPT FOLDER
        // ══════════════════════════════════════════════════════════════════════
        private void ApplyMainWindowOpacity() =>
            this.Opacity = SettingsWindow.GetMainWindowOpacity();

        private static string FindScriptFolder(string startDir)
        {
            if (File.Exists(Path.Combine(startDir, "speechmatics_engine.py"))) return startDir;
            string? dir = startDir;
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir, "speechmatics_engine.py"))) return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }
            return startDir; // fallback to app directory
        }

        // ══════════════════════════════════════════════════════════════════════
        // CAMERA MODE
        // ══════════════════════════════════════════════════════════════════════
        private void CameraMode_Click(object sender, RoutedEventArgs e)
        {
            _isCameraMode = true;
            NormalModeGrid.Visibility = Visibility.Collapsed;
            this.Hide();
            if (answerWindow != null)
            {
                answerWindow.ToggleCameraMode(true);
                try { WindowStealth.SetStealthMode(answerWindow, true); } catch (Exception ex) { DebugWindow.Log("STEALTH", ex.Message); }
            }
        }

        private void ExitCameraMode_Click(object sender, RoutedEventArgs e) => ExitCameraMode();

        private void ExitCameraMode()
        {
            _isCameraMode = false;
            this.Show();
            this.Height = 740; this.Width = 1120;
            this.Top = (SystemParameters.PrimaryScreenHeight - 740) / 2;
            this.Left = (SystemParameters.PrimaryScreenWidth - 1120) / 2;
            NormalModeGrid.Visibility = Visibility.Visible;
            if (answerWindow != null) answerWindow.ToggleCameraMode(false);
        }

        // ══════════════════════════════════════════════════════════════════════
        // MIC
        // ══════════════════════════════════════════════════════════════════════
        private bool _spaceHandling = false;

        private void HandleSpacePress(string source)
        {
            if (_spaceHandling || isProcessing) return;
            _spaceHandling = true;
            try
            {
                if (isMuted)
                {
                    isMuted = false; isListening = true;
                    // Aggressively clear transcript: delete + recreate so the Python engine
                    // can't race its buffered content back before we start suppressing reads.
                    string latestPath = Path.Combine(AppDataFolder, "latest.txt");
                    try { File.Delete(latestPath); } catch { }
                    try { File.WriteAllText(latestPath, ""); } catch (Exception ex) { DebugWindow.Log("FILE", $"latest.txt clear failed: {ex.Message}"); }
                    try { File.WriteAllText(Path.Combine(AppDataFolder, "reset.flag"), "1"); } catch (Exception ex) { DebugWindow.Log("FILE", $"reset.flag write failed: {ex.Message}"); }
                    _justStartedListening = true;
                    _listenStartTicks = 0;
                    TranscriptTextBlock.Text = "";
                    TranscriptHint.Visibility = Visibility.Visible;
                    AiAnswerBox.Text = "";
                    if (answerWindow != null) { answerWindow.UpdateAnswer(""); answerWindow.UpdateQuestion(""); }
                    DeletePauseFlag();
                    DebugWindow.Log("MIC", $"[{source}] UNMUTED");
                    UpdateMicUi();
                }
                else
                {
                    isListening = false; WritePauseFlag(); isMuted = true;
                    DebugWindow.Log("MIC", $"[{source}] MUTED — firing AI");
                    UpdateMicUi();
                    // Fire the AI task; attach a last-resort fault handler so any
                    // unexpected exception that escapes AskAiAsync is shown in the UI
                    // rather than silently swallowed as an unobserved task.
                    _ = AskAiAsync().ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            Exception ex = t.Exception?.GetBaseException() ?? t.Exception!;
                            DebugWindow.Log("AI_FATAL", ex.Message);
                            Dispatcher.Invoke(() =>
                            {
                                AiAnswerBox.Text = "⚠ Unexpected error. Please try again. Press F12 for details.";
                                StopThinkingUi();
                            });
                        }
                    }, TaskScheduler.Default);
                }
            }
            finally { _spaceHandling = false; }
        }

        private void MicBtn_Click(object sender, RoutedEventArgs e) => HandleSpacePress("BUTTON");

        // ══════════════════════════════════════════════════════════════════════
        // AI — routes through CoopilotX backend (credits deducted server-side)
        // ══════════════════════════════════════════════════════════════════════
        private async Task AskAiAsync()
        {
            if (isProcessing) return;

            // Outer try/catch wraps the ENTIRE method body — including setup awaits —
            // so no exception can ever escape this Task unobserved.
            string q = "";
            try
            {
                q = TranscriptTextBlock.Text.Trim();
                if (string.IsNullOrWhiteSpace(q)) { UpdateMicUi(); return; }

                if (UserSession.IsLoggedIn) await UserSession.TryRefreshAsync();

                if (!UserSession.IsLoggedIn)
                {
                    AiAnswerBox.Text = "⚠ Please sign in to use AI answers.\n\nClick the Sign In button in the top right.";
                    UpdateMicUi();
                    return;
                }

                if (!UserSession.IsUnlimited && UserSession.Credits < CreditsCriticalThreshold)
                {
                    AiAnswerBox.Text = $"⚠ Not enough credits ({UserSession.Credits} remaining).\n\nVisit coopilotxai.com/pricing to upgrade.";
                    UpdateMicUi();
                    return;
                }

                isProcessing = true; thinkingStep = 0;
                ThinkingPanel.Visibility = Visibility.Visible;
                thinkingTimer?.Start();
                UpdateMicUi();

                string sep = "\n" + new string('─', 45) + "\n\n";
                string old = AiAnswerBox.Text == "Results will appear here..." ? "" : AiAnswerBox.Text;
                AiAnswerBox.Text = $"Q: {q}\n\n";
                if (answerWindow != null) { answerWindow.UpdateAnswer(""); answerWindow.UpdateQuestion(q); }

                var sb = new StringBuilder();
                int tokenCount = 0;

                await foreach (var token in StreamFromBackend(q, ResumeTextBox.Text))
                {
                    sb.Append(token); tokenCount++;
                    if (tokenCount == 1)
                    {
                        thinkingTimer?.Stop();
                        ThinkingPanel.Visibility = Visibility.Collapsed;
                    }
                    if (tokenCount % 3 == 0 || token.Contains('\n'))
                    {
                        string soFar = CleanAiOutput(sb.ToString());
                        AiAnswerBox.Text = $"Q: {q}\n\n{soFar}";
                        AiAnswerBox.ScrollToEnd();
                        if (answerWindow != null) answerWindow.UpdateAnswer(soFar);
                    }
                }

                string final = CleanAiOutput(sb.ToString());
                AiAnswerBox.Text = $"Q: {q}\n\n{final}\n{sep}{old}";
                AiAnswerBox.ScrollToEnd();
                if (answerWindow != null) { answerWindow.UpdateAnswer(final); answerWindow.UpdateQuestion(q); }
                PromptBuilder.AddToHistory(q, final);
                AppendToSessionLog(q, final);
                DebugWindow.Log("AI", $"Done — {tokenCount} tokens");

                // Refresh credits display after AI call
                _ = FetchAndDisplayCreditsAsync().ContinueWith(t => {
                    if (t.IsFaulted) DebugWindow.Log("CREDITS_ERR", t.Exception?.GetBaseException().Message ?? "unknown");
                }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                DebugWindow.Log("AI_ERR", ex.Message);
                string label = string.IsNullOrWhiteSpace(q) ? "" : $"Q: {q}\n\n";
                AiAnswerBox.Text = $"{label}⚠ Something went wrong. Press F12 for details.";
            }
            finally { StopThinkingUi(); }
        }

        // ── Streaming iterator — NO try/catch wrapping yield statements ────────
        private async IAsyncEnumerable<string> StreamFromBackend(
            string question, string resume,
            [EnumeratorCancellation] System.Threading.CancellationToken ct = default)
        {
            // Fast-path: local responses that need no network call
            if (PromptBuilder.IsGreeting(question)) { yield return PromptBuilder.GetGreetingResponse(); yield break; }
            if (PromptBuilder.IsSmallTalk(question)) { yield return PromptBuilder.GetSmallTalkResponse(); yield break; }

            // ── 1. Send request — errors handled outside the iterator ──────────
            using HttpResponseMessage res = await SendBackendRequestAsync(question, resume, ct);

            // ── 2. Handle non-200 status codes with plain yields (no try/catch) ─
            int status = (int)res.StatusCode;
            if (status == 402) { yield return "⚠ Not enough credits. Visit coopilotxai.com/pricing to upgrade."; yield break; }
            if (status == 401)
            {
                UserSession.Clear();
                Dispatcher.Invoke(() => SetLoggedOutUI());
                yield return "⚠ Session expired. Please sign in again.";
                yield break;
            }
            if (!res.IsSuccessStatusCode) { yield return $"Backend error ({status}). Try again."; yield break; }

            // ── 3. Stream SSE lines — collect then yield (no try/catch around yield) ─
            List<string> tokens = await ReadSseTokensAsync(res, ct);
            foreach (string token in tokens)
                yield return token;
        }

        // Handles the HTTP request; exceptions bubble up to AskAiAsync's catch block.
        private async Task<HttpResponseMessage> SendBackendRequestAsync(
            string question, string resume, System.Threading.CancellationToken ct)
        {
            string resumeFacts = ResumeParser.ExtractFacts(resume);
            var messages = PromptBuilder.BuildMessages(resumeFacts, question);
            // Embed context + locked facts + format rule directly in the question field
            // so the backend model always sees them, even if it ignores the messages array.
            string enhancedQuestion = PromptBuilder.BuildEnhancedQuestion(question, resumeFacts);
            var payload = new { question = enhancedQuestion, resume = resume ?? "", provider = "groq", messages };
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{BackendUrl}/api/v1/interview/ask");
            request.Headers.Add("Authorization", $"Bearer {UserSession.IdToken}");
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            try
            {
                return await _backendClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex)
            {
                DebugWindow.Log("AI_ERR", ex.Message);
                // Return a fake 503 so the iterator can yield a friendly message
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
            }
        }

        // Reads all SSE tokens from the response stream into a list (no yield here).
        private static async Task<List<string>> ReadSseTokensAsync(
            HttpResponseMessage res, System.Threading.CancellationToken ct)
        {
            var tokens = new List<string>();
            try
            {
                using var stream = await res.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);

                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;
                    string data = line["data: ".Length..];
                    if (data == "[DONE]") break;

                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        if (doc.RootElement.TryGetProperty("error", out var errProp))
                        {
                            tokens.Add($"Error: {errProp.GetString()}");
                            break;
                        }
                        var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
                        if (delta.TryGetProperty("content", out var content))
                        {
                            string token = content.GetString() ?? "";
                            if (!string.IsNullOrEmpty(token)) tokens.Add(token);
                        }
                    }
                    catch { /* skip malformed SSE line */ }
                }
            }
            catch (Exception ex)
            {
                tokens.Add($"Stream error: {ex.Message}");
            }
            return tokens;
        }

        private string CleanAiOutput(string ans)
        {
            ans = Regex.Replace(ans, @"```[a-z]*|```", "").Trim();
            ans = Regex.Replace(ans, @"\*{1,3}([^*\n]+)\*{1,3}", "$1");   // **bold** → bold
            ans = Regex.Replace(ans, @"_{1,3}([^_\n]+)_{1,3}", "$1");      // _italic_ → italic
            ans = Regex.Replace(ans, @"(?m)^#{1,6}\s+", "");               // # Header → Header (line-start only, preserves C#)
            ans = ans.Replace("\r\n", "\n").Replace("\r", "\n");
            ans = Regex.Replace(ans, @"\n{3,}", "\n\n");
            return ans.Trim();
        }

        private void StopThinkingUi()
        {
            thinkingTimer?.Stop();
            ThinkingPanel.Visibility = Visibility.Collapsed;
            ThinkingHintLabel.Visibility = Visibility.Visible;   // restore for next use
            ThinkingLabel.Text = "Thinking...";                  // reset to default
            isProcessing = false;
            _isScreenAnalyzing = false;
            UpdateMicUi();
        }

        // ══════════════════════════════════════════════════════════════════════
        // SESSION
        // ══════════════════════════════════════════════════════════════════════
        private void StartNewSession()
        {
            // Find next available session number
            while (File.Exists(Path.Combine(AppDataFolder, "interview_" + sessionNumber + ".txt")))
                sessionNumber++;
            sessionLogPath = Path.Combine(AppDataFolder, "interview_" + sessionNumber + ".txt");

            // Write header with timestamp so the Sessions window can parse it
            string header = $"SESSION {sessionNumber} | {SettingsWindow.GetActiveModelId()} | {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            File.WriteAllText(sessionLogPath, header + "\n\n");
            File.WriteAllText(Path.Combine(AppDataFolder, "record.flag"), "1");
            isRecording = true;

            // Clear conversation history for a fresh session
            PromptBuilder.ClearHistory();

            // Reset and start session timer
            _sessionSeconds = 0;
            SessionTimerLabel.Text = "0:00";
            SessionTimerBadge.Visibility = Visibility.Visible;
            _sessionTimer?.Stop();
            _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _sessionTimer.Tick += (s, e) =>
            {
                _sessionSeconds++;
                int m = _sessionSeconds / 60, s2 = _sessionSeconds % 60;
                SessionTimerLabel.Text = $"{m}:{s2:D2}";
            };
            _sessionTimer.Start();

            // Show the pulsing REC badge automatically
            RecordingBadge.Visibility = Visibility.Visible;
            DebugWindow.Log("SESSION", $"Auto-started session #{sessionNumber}");
        }

        private void AppendToSessionLog(string q, string a)
        {
            if (!string.IsNullOrEmpty(sessionLogPath))
                File.AppendAllText(sessionLogPath, $"Q: {q}\nA: {a}\n\n");
        }

        private void EndSession()
        {
            string f = Path.Combine(AppDataFolder, "record.flag");
            if (File.Exists(f)) File.Delete(f);
            isRecording = false;
            sessionLogPath = "";

            // Stop and hide session timer
            _sessionTimer?.Stop();
            SessionTimerBadge.Visibility = Visibility.Collapsed;

            // Hide REC badge
            RecordingBadge.Visibility = Visibility.Collapsed;
            PromptBuilder.ClearHistory();
            DebugWindow.Log("SESSION", "Session ended");
        }

        // ══════════════════════════════════════════════════════════════════════
        // UI
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>Opens the My Sessions recordings window.</summary>
        private void SessionsBtn_Click(object sender, RoutedEventArgs e)
        {
            var sessionsWin = new SessionsWindow();
            sessionsWin.Owner = this;
            sessionsWin.Show();
        }

        private void UpdateMicUi()
        {
            Color c; string label;
            if (isProcessing) { c = Colors.Orange; label = "THINKING"; }
            else if (isListening) { c = Colors.LimeGreen; label = "LISTENING"; }
            else if (isMuted) { c = Color.FromRgb(239, 68, 68); label = "MUTED"; }
            else { c = Color.FromRgb(239, 68, 68); label = isRecording ? "RECORDING" : "READY"; }

            var brush = new SolidColorBrush(c);
            MicIndicator.Fill = brush;
            MicGlow.Color = c;
            MicGlow.BlurRadius = isListening ? 20 : (isProcessing ? 12 : 0);
            MicBtn.BorderBrush = brush;
            MicIndicatorText.Text = label;
            if (answerWindow != null) answerWindow.UpdateMicState(isListening, isProcessing);
        }

        private void UpdateTranscript()
        {
            if (!isListening) return;

            // Suppress stale engine output for ~400 ms after unmute (≈3 ticks at 150 ms)
            if (_justStartedListening)
            {
                _listenStartTicks++;
                TranscriptTextBlock.Text = "";          // force blank during suppression
                if (_listenStartTicks >= 7) _justStartedListening = false;  // ~1050ms
                return;
            }

            try
            {
                string text = ReadLatestTxtSafe();
                if (text != TranscriptTextBlock.Text)
                {
                    TranscriptTextBlock.Text = text;
                    TranscriptHint.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;
                    TranscriptScroll.ScrollToBottom();
                    if (_isCameraMode && answerWindow != null)
                        answerWindow.UpdateQuestion(text);
                }
            }
            catch (Exception ex) { DebugWindow.Log("TRANSCRIPT", $"UpdateTranscript failed: {ex.Message}"); }
        }

        /// <summary>
        /// Reads latest.txt with short retries to tolerate torn writes from the
        /// Python engine writing at the same time (no shared file lock cross-process).
        /// </summary>
        private string ReadLatestTxtSafe()
        {
            string path = Path.Combine(AppDataFolder, "latest.txt");
            for (int i = 0; i < TranscriptRetryCount; i++)
            {
                try { return File.ReadAllText(path); }
                catch (IOException) when (i < TranscriptRetryCount - 1)
                {
                    System.Threading.Thread.Sleep(TranscriptRetryDelayMs);
                }
            }
            return TranscriptTextBlock.Text; // fallback: keep current text on persistent failure
        }

        // ══════════════════════════════════════════════════════════════════════
        // ENGINE
        // ══════════════════════════════════════════════════════════════════════
        private void StartSpeechmaticsEngine()
        {
            try
            {
                // Cancel any existing stream-reader tasks, then issue a fresh token
                _engineCts.Cancel();
                _engineCts.Dispose();
                _engineCts = new CancellationTokenSource();
                var ct = _engineCts.Token;

                KillAndDisposeEngine();

                string pyScript = Path.Combine(scriptFolder, "speechmatics_engine.py");
                if (!File.Exists(pyScript)) { DebugWindow.Log("ENGINE", "❌ Not found: " + scriptFolder); return; }
                string smKey = SettingsWindow.GetSpeechmaticsKey();
                if (string.IsNullOrWhiteSpace(smKey)) { DebugWindow.Log("ENGINE", "❌ No SM key."); return; }

                // Resolve Python executable: try "py" launcher first (Windows standard),
                // then "python", then "python3" — log which one succeeds.
                string pyExe = ResolvePythonExecutable();
                DebugWindow.Log("ENGINE", $"Python executable: {pyExe}");

                speechmaticsProcess = new Process();
                speechmaticsProcess.StartInfo.FileName = pyExe;
                string deviceArg = _audioDeviceId >= 0 ? $" --device {_audioDeviceId}" : "";
                speechmaticsProcess.StartInfo.Arguments = $"\"{pyScript}\"{deviceArg}";
                speechmaticsProcess.StartInfo.EnvironmentVariables["SM_API_KEY"] = smKey;
                speechmaticsProcess.StartInfo.WorkingDirectory = scriptFolder;
                speechmaticsProcess.StartInfo.CreateNoWindow = true;
                speechmaticsProcess.StartInfo.UseShellExecute = false;
                speechmaticsProcess.StartInfo.RedirectStandardOutput = true;
                speechmaticsProcess.StartInfo.RedirectStandardError = true;
                speechmaticsProcess.Start();
                DebugWindow.Log("ENGINE", $"STARTED | PID: {speechmaticsProcess.Id}");

                // Save PID so NuclearKillOldProcesses can target only this process on next startup
                try { File.WriteAllText(EnginePidPath, speechmaticsProcess.Id.ToString()); } catch { }

                // Stream-reader tasks exit cleanly when ct is cancelled or process EOF
                var proc = speechmaticsProcess; // capture snapshot for the lambda
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            string? line = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                            if (line == null) break; // EOF
                            Dispatcher.Invoke(() => DebugWindow.Log("PY", line));
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { DebugWindow.Log("PY_OUT", ex.Message); }
                }, ct);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            string? line = await proc.StandardError.ReadLineAsync(ct).ConfigureAwait(false);
                            if (line == null) break; // EOF
                            Dispatcher.Invoke(() => DebugWindow.Log("PY_ERR", line));
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { DebugWindow.Log("PY_ERR_READER", ex.Message); }
                }, ct);
            }
            catch (Exception ex) { DebugWindow.Log("ENGINE_ERR", ex.Message); }
        }

        /// <summary>Kill + Dispose the Python process and null the reference.</summary>
        private void KillAndDisposeEngine()
        {
            var proc = speechmaticsProcess;
            speechmaticsProcess = null;
            if (proc == null) return;
            try { proc.Kill(); } catch { }
            try { proc.Dispose(); } catch { }
            // Clean up PID file on a normal kill so NuclearKill doesn't double-attempt it
            try { if (File.Exists(EnginePidPath)) File.Delete(EnginePidPath); } catch { }
        }

        private void WritePauseFlag() { try { File.WriteAllText(Path.Combine(AppDataFolder, "pause.flag"), "1"); } catch (Exception ex) { DebugWindow.Log("FILE", $"pause.flag write failed: {ex.Message}"); } }
        private void DeletePauseFlag() { try { string f = Path.Combine(AppDataFolder, "pause.flag"); if (File.Exists(f)) File.Delete(f); } catch (Exception ex) { DebugWindow.Log("FILE", $"pause.flag delete failed: {ex.Message}"); } }
        // ── PID file path — stores our engine's PID for crash-recovery cleanup ─
        private string EnginePidPath => Path.Combine(AppDataFolder, "engine.pid");

        /// <summary>
        /// Kills orphaned speechmatics_engine processes from a previous crash.
        /// Uses a saved PID file rather than sweeping all Python processes — the
        /// old approach would blindly kill the user's Jupyter notebooks, other scripts, etc.
        /// </summary>
        private void NuclearKillOldProcesses()
        {
            KillAndDisposeEngine();

            // Kill only the specific PID we saved last time we started the engine.
            // If the file doesn't exist there are no orphans to clean up.
            try
            {
                if (File.Exists(EnginePidPath))
                {
                    string pidText = File.ReadAllText(EnginePidPath).Trim();
                    if (int.TryParse(pidText, out int savedPid))
                    {
                        try
                        {
                            var orphan = Process.GetProcessById(savedPid);
                            if (!orphan.HasExited)
                            {
                                orphan.Kill();
                                DebugWindow.Log("ENGINE", $"Killed orphaned engine PID {savedPid}");
                            }
                            orphan.Dispose();
                        }
                        catch { } // Process already gone — normal after a clean shutdown
                    }
                    File.Delete(EnginePidPath);
                }
            }
            catch (Exception ex) { DebugWindow.Log("ENGINE", $"NuclearKill: {ex.Message}"); }
        }

        /// <summary>
        /// Finds a working Python executable on the current PATH.
        /// Tries "py" (Windows launcher), then "python", then "python3".
        /// Result is cached after first resolution so UI thread never blocks twice.
        /// </summary>
        private static string? _cachedPythonExe;
        private static string ResolvePythonExecutable()
        {
            if (_cachedPythonExe != null) return _cachedPythonExe;
            foreach (string candidate in new[] { "py", "python", "python3" })
            {
                try
                {
                    using var probe = new Process();
                    probe.StartInfo.FileName = candidate;
                    probe.StartInfo.Arguments = "--version";
                    probe.StartInfo.CreateNoWindow = true;
                    probe.StartInfo.UseShellExecute = false;
                    probe.StartInfo.RedirectStandardOutput = true;
                    probe.StartInfo.RedirectStandardError = true;
                    probe.Start();
                    probe.WaitForExit(2000);
                    if (probe.ExitCode == 0) { _cachedPythonExe = candidate; return candidate; }
                }
                catch { }
            }
            _cachedPythonExe = "python"; // last-resort fallback
            return "python";
        }
        private void MonitorEngine() { if (speechmaticsProcess == null || speechmaticsProcess.HasExited) { DebugWindow.Log("ENGINE", "Dead — restarting..."); StartSpeechmaticsEngine(); } }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.F12) { e.Handled = true; ToggleDebugWindow(); return; }
            if (e.Key == System.Windows.Input.Key.F8)  { e.Handled = true; _ = HandleScreenAnalysisAsync(); return; }
            if (e.Key == System.Windows.Input.Key.F9)  { e.Handled = true; _ = HandlePrimaryScreenAnalysisAsync(); return; }
            if (e.Key == System.Windows.Input.Key.Space)
            {
                if (ResumeTextBox.IsFocused || ResumeTextBox.IsKeyboardFocusWithin) return;
                e.Handled = true; HandleSpacePress("PREVIEW");
            }
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { /* handled by PreviewKeyDown */ }
        private void Border_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) { try { DragMove(); } catch { } }
        private void IntroPlayer_MediaEnded(object sender, RoutedEventArgs e) => IntroLayer.Visibility = Visibility.Collapsed;
        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
        private void MinimizeBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            var sw = new SettingsWindow(_audioDeviceId);
            sw.Owner = this;
            sw.ShowDialog();
            if (sw.SettingsChanged)
            {
                if (sw.SelectedDeviceIndex >= 0) _audioDeviceId = sw.SelectedDeviceIndex;
                StartSpeechmaticsEngine();
                ApplyMainWindowOpacity();
                answerWindow?.ApplyOverlayOpacity();
                if (UserSession.IsLoggedIn)
                    _ = FetchAndDisplayCreditsAsync().ContinueWith(t => {
                        if (t.IsFaulted) DebugWindow.Log("CREDITS_ERR", t.Exception?.GetBaseException().Message ?? "unknown");
                    }, TaskScheduler.Default);
            }
        }

        private void VerifyResume_Click(object sender, RoutedEventArgs e)
        {
            string facts = ResumeParser.ExtractFacts(ResumeTextBox.Text);
            MessageBox.Show(facts, "Resume Facts Preview", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(AiAnswerBox.Text);

        // ── Toolbar: Copy answer ──────────────────────────────────────────────
        private void CopyAnswerBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(AiAnswerBox.Text))
                Clipboard.SetText(AiAnswerBox.Text);
        }

        // ── Toolbar: Clear transcript + answer ───────────────────────────────
        private void ClearAnswerBtn_Click(object sender, RoutedEventArgs e)
        {
            TranscriptTextBlock.Text = "";
            TranscriptHint.Visibility = Visibility.Visible;
            AiAnswerBox.Text = "Ready — press SPACE to start listening, then SPACE again to get your answer.";   // starts with "Ready" → treated as default
            if (answerWindow != null) { answerWindow.UpdateAnswer(""); answerWindow.UpdateQuestion(""); }
            PromptBuilder.ClearHistory();
            // Clear latest.txt so stale transcript can't bleed back into the new listen cycle
            try { File.WriteAllText(Path.Combine(AppDataFolder, "latest.txt"), ""); } catch { }
        }

        // ── Toolbar: End current session and immediately start a new one ─────
        private void NewSessionBtn_Click(object sender, RoutedEventArgs e)
        {
            if (isProcessing) return;   // don't interrupt an in-flight AI call
            EndSession();
            sessionNumber++;
            TranscriptTextBlock.Text = "";
            TranscriptHint.Visibility = Visibility.Visible;
            AiAnswerBox.Text = "New session started — press SPACE to begin.";
            if (answerWindow != null) { answerWindow.UpdateAnswer(""); answerWindow.UpdateQuestion(""); }
            StartNewSession();
        }

        // ── Resume panel: show/hide watermark based on content ───────────────
        private void ResumeTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (ResumeWatermark != null)
                ResumeWatermark.Visibility = string.IsNullOrWhiteSpace(ResumeTextBox.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ScreenAnalyzeBtn_Click(object sender, RoutedEventArgs e)
            => _ = HandleScreenAnalysisAsync();

        // ══════════════════════════════════════════════════════════════════════
        // SCREEN ANALYSIS  (F8 = all monitors  |  F9 = primary monitor only)
        // ══════════════════════════════════════════════════════════════════════
        private bool _isScreenAnalyzing = false;

        private Task HandleScreenAnalysisAsync()            => RunScreenAnalysis(primaryOnly: false);
        private Task HandlePrimaryScreenAnalysisAsync()     => RunScreenAnalysis(primaryOnly: true);

        private async Task RunScreenAnalysis(bool primaryOnly)
        {
            // Guard: don't double-fire while already processing
            if (isProcessing || _isScreenAnalyzing) return;

            // Must be logged in
            if (!UserSession.IsLoggedIn)
            {
                AiAnswerBox.Text = "⚠ Please sign in to use Screen Analysis.\n\nClick the Sign In button in the top right.";
                return;
            }

            // Must have a vision API key
            if (string.IsNullOrWhiteSpace(SettingsWindow.GetApiKey()))
            {
                AiAnswerBox.Text = "⚠ Screen Analysis needs an API key.\n\n" +
                                   "• Open ⚙ Settings → paste your key\n" +
                                   "• Groq key (gsk_…) → free, uses Llama 4 Scout vision\n" +
                                   "• OpenAI key (sk-…) → GPT-4o Vision\n" +
                                   "• Speech-to-text answers still work without a vision key";
                return;
            }

            _isScreenAnalyzing = true;
            isProcessing       = true;
            UpdateMicUi();

            // ── Phase 1: scanning state ───────────────────────────────────────
            string captureLabel = primaryOnly ? "primary screen" : "all monitors";
            ThinkingLabel.Text = $"🔍  Capturing {captureLabel}…";
            ThinkingHintLabel.Visibility = Visibility.Collapsed;
            ThinkingPanel.Visibility = Visibility.Visible;

            // Hide both windows so they don't appear in the screenshot
            double savedOpacity = this.Opacity;
            this.Opacity = 0;
            bool answerWasVisible = answerWindow?.IsVisible == true;
            if (answerWasVisible && answerWindow != null) answerWindow.Opacity = 0;

            // Give DWM time to flush the transparent frame before BitBlt
            await Task.Delay(300);

            // ── Phase 2: capture ──────────────────────────────────────────────
            byte[] imageBytes;
            try
            {
                imageBytes = primaryOnly
                    ? ScreenAnalyzer.CapturePrimaryScreen()
                    : ScreenAnalyzer.CaptureScreen();
                DebugWindow.Log("SCREEN", $"Captured {imageBytes.Length / 1024} KB ({captureLabel})");
            }
            catch (Exception ex)
            {
                DebugWindow.Log("SCREEN_ERR", ex.Message);
                this.Opacity = savedOpacity;
                if (answerWasVisible && answerWindow != null) answerWindow.Opacity = SettingsWindow.GetOverlayOpacity();
                AiAnswerBox.Text = "⚠ Screen capture failed. Press F12 for details.";
                _isScreenAnalyzing = false;
                StopThinkingUi();
                return;
            }

            // Restore windows immediately after capture
            this.Opacity = savedOpacity;
            if (answerWasVisible && answerWindow != null) answerWindow.Opacity = SettingsWindow.GetOverlayOpacity();

            // ── Phase 3: stream from vision AI ───────────────────────────────
            string visionLabel = SettingsWindow.IsGroq() ? "Llama 4 Scout" : "GPT-4o";
            ThinkingLabel.Text = $"🤖  {visionLabel} analyzing…";

            string resumeCtx  = ResumeParser.ExtractFacts(ResumeTextBox.Text);
            string timestamp  = DateTime.Now.ToString("h:mm tt");
            string header     = $"📸 SCREEN ANALYSIS  [{timestamp}]\n\n";
            string sep        = "\n" + new string('─', 45) + "\n\n";

            // Save previous answers so we can prepend the new one on top.
            // Treat placeholder/welcome messages as "empty" so they aren't carried forward.
            bool isDefaultText = string.IsNullOrWhiteSpace(AiAnswerBox.Text)
                || AiAnswerBox.Text.StartsWith("Ready")
                || AiAnswerBox.Text.StartsWith("New session")
                || AiAnswerBox.Text.StartsWith("Results will appear");
            string previousAnswers = isDefaultText ? "" : AiAnswerBox.Text;

            var sb = new StringBuilder();
            int tokenCount = 0;

            try
            {
                await foreach (var token in ScreenAnalyzer.AnalyzeStreamAsync(imageBytes, resumeCtx))
                {
                    sb.Append(token);
                    tokenCount++;

                    // First token → hide the "analyzing…" indicator
                    if (tokenCount == 1)
                        ThinkingPanel.Visibility = Visibility.Collapsed;

                    // Update display every 3 tokens or on newlines (smooth streaming)
                    if (tokenCount % 3 == 0 || token.Contains('\n'))
                    {
                        AiAnswerBox.Text = $"{header}{sb}";
                        AiAnswerBox.ScrollToEnd();
                        if (_isCameraMode && answerWindow != null)
                            answerWindow.UpdateAnswer(sb.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log("SCREEN_ERR", $"Stream failed: {ex.Message}");
                sb.Append($"\n⚠ Stream interrupted: {ex.Message}");
            }

            // ── Phase 4: post-process + finalise display ──────────────────────
            // PostProcess normalises section headers, removes stray markdown,
            // and collapses excess blank lines — runs instantly on the final string.
            string finalResult = ScreenAnalyzer.PostProcess(sb.ToString());

            AiAnswerBox.Text = string.IsNullOrWhiteSpace(previousAnswers)
                ? $"{header}{finalResult}"
                : $"{header}{finalResult}\n{sep}{previousAnswers}";
            AiAnswerBox.ScrollToEnd();

            if (_isCameraMode && answerWindow != null)
            {
                answerWindow.UpdateAnswer(finalResult);
                answerWindow.UpdateQuestion("[Screen Analysis]");
            }

            // Persist to session log + history for smart follow-up voice questions
            AppendToSessionLog("[Screen Analysis]", finalResult);
            PromptBuilder.AddToHistory("Analyze what is currently on my screen", finalResult);

            DebugWindow.Log("SCREEN", $"Done — {tokenCount} tokens, {finalResult.Length} chars");

            _isScreenAnalyzing = false;
            StopThinkingUi();
        }

        private void ResumeToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            _resumeCollapsed = !_resumeCollapsed;
            ResumePanel.Visibility = _resumeCollapsed ? Visibility.Collapsed : Visibility.Visible;
            ResumeColumn.Width = _resumeCollapsed ? new GridLength(0) : new GridLength(270);
            ResumeToggleBtn.Content = _resumeCollapsed ? "▶" : "◀";
        }

        private void ToggleDebugWindow()
        {
            if (_debugWindow == null) return;
            if (_debugWindow.IsVisible) _debugWindow.Hide();
            else { _debugWindow.Show(); _debugWindow.Activate(); }
        }

        protected override void OnClosed(EventArgs e)
        {
            // Stop all timers first so no callbacks fire during teardown
            transcriptTimer?.Stop();
            thinkingTimer?.Stop();
            creditsRefreshTimer?.Stop();
            _engineMonitorTimer?.Stop();
            _sessionTimer?.Stop();

            // Unsubscribe camera event and close overlay window
            if (answerWindow != null && _cameraModeClosedHandler != null)
                answerWindow.CameraModeClosedByUser -= _cameraModeClosedHandler;
            try { answerWindow?.Close(); } catch { }
            answerWindow = null;

            _globalHotkey?.Dispose();
            _debugWindow?.ForceClose();
            EndSession();

            // Signal Python for graceful shutdown, then cancel stream readers, then kill
            try { File.WriteAllText(Path.Combine(AppDataFolder, "shutdown.flag"), "1"); } catch { }
            _engineCts.Cancel();
            _engineCts.Dispose();
            KillAndDisposeEngine();

            base.OnClosed(e);
        }
    }
}