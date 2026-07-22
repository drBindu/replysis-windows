using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        private static string BackendUrl => SettingsWindow.GetBackendUrl();

        // ── Tunable constants (change here, takes effect everywhere) ──────────
        private const int    TranscriptPollMs        = 150;   // how often to read latest.txt
        private const int    ThinkingAnimMs          = 800;   // thinking dot animation interval
        private const int    CreditRefreshMinutes    = 5;     // background credits refresh
        private const int    EngineMonitorSecs       = 3;     // how often to check engine health
        private const int    CreditsLowThreshold     = 20;    // amber warning below this
        private const int    CreditsCriticalThreshold= 5;     // red warning / block below this
        private const int    TranscriptRetryCount    = 3;     // retries on torn file read
        private const int    TranscriptRetryDelayMs  = 10;    // delay between retries
        private const int    RecordingSaveTimeoutMs  = 10_000;
        private const long   MaxResumeFileBytes      = 10 * 1024 * 1024;
        private const int    MaxResumeTextChars      = 100_000;
        private const int    MaxAiResponseChars      = 100_000;
        private const double DefaultMainWindowWidth  = 880;
        private const double DefaultMainWindowHeight = 580;

        private bool _suppressOpacitySlider = false;
        private bool isMuted = true;
        private bool isListening = false;
        private bool isProcessing = false;
        private bool   _engineAuthFailed  = false;
        private bool   _engineUsageLimitReached = false;
        private string _engineFatalReason = ""; // non-empty = fatal, used by ShowEngineAuthError
        private bool _creditsFetched = false;
        private bool isRecording = false;
        private bool _newSessionInProgress;
        private bool _resumeCollapsed = false;
        private bool _isCameraMode = false;
        private int _audioDeviceId = -1;
        private bool _justStartedListening = false;  // suppress stale reads for 400ms after unmute
        private int  _listenStartTicks = 0;

        // ── Feature state ────────────────────────────────────────────────────
        private string _liveHints        = "";
        private string _companyName      = "";
        private string _jobDescription   = "";
        private List<(string Name, string Content)> _savedResumes = new();
        private bool _answerIsBehavioral = false;

        private DispatcherTimer? transcriptTimer;
        private DispatcherTimer? thinkingTimer;
        private DispatcherTimer? creditsRefreshTimer;
        private DispatcherTimer? _sessionTimer;
        private DispatcherTimer? _jobContextSaveTimer;
        private int _sessionSeconds = 0;
        private int thinkingStep = 0;

        private Process? speechmaticsProcess;
        private CancellationTokenSource _engineCts = new CancellationTokenSource();
        private int _engineStartGeneration;
        private bool _engineStarting;
        private string projectRoot = "";
        private string scriptFolder = "";
        private AnswerWindow? answerWindow;
        private Action? _cameraModeClosedHandler;   // stored so we can -= it in OnClosed

        private int sessionNumber = 1;
        private string sessionLogPath = "";
        private string _recordingSessionId = "";

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

            // Position top-right on the primary screen, 16px from edges
            var workArea = SystemParameters.WorkArea;
            this.Left = workArea.Right - this.Width - 16;
            this.Top  = workArea.Top + 16;

            this.Loaded += async (s, e) =>
            {
                // Report presence to Firestore so the admin dashboard shows
                // accurate Live/last-seen (self-guards while logged out)
                PresenceTracker.Start();

                try
                {
                    NuclearKillOldProcesses();
                    // try { WindowStealth.SetStealthMode(this, true); } catch (Exception ex) { DebugWindow.Log("STEALTH", ex.Message); } // TEMP disabled for screenshot

                    answerWindow = new AnswerWindow();
                    answerWindow.ShowInTaskbar = false;
                    // Store the delegate so we can -= it in OnClosed (prevents memory leak)
                    _cameraModeClosedHandler = () => Dispatcher.Invoke(() => ExitCameraMode());
                    answerWindow.CameraModeClosedByUser += _cameraModeClosedHandler;
                    answerWindow.AnalyzeRequested += () => Dispatcher.Invoke(() => _ = HandleScreenAnalysisAsync());

                    isMuted = true;
                    isListening = false;
                    WritePauseFlag();
                    SecurePendingAudioRecordings();
                    UpdateMicUi();
                    SavePathLabel.Text = AppDataFolder;
                    LoadHints(); LoadJobContext(); LoadSavedResumes();
                    UpdateSavedResumesButton();
                    SwitchToResumeTab();
                    _debugWindow = new DebugWindow();

                    ApplyMainWindowOpacity();
                    StartSpeechmaticsEngine();

                    IntPtr mainHwnd = new WindowInteropHelper(this).Handle;
                    try
                    {
                        _globalHotkey = new GlobalHotkey(
                            onSpacePressed:                 () => Dispatcher.BeginInvoke(() => HandleSpaceDown("GLOBAL")),
                            onSpaceReleased:                () => Dispatcher.BeginInvoke(() => HandleSpaceUp("GLOBAL")),
                            onF12Pressed:                   () => Dispatcher.BeginInvoke(() => ToggleDebugWindow()),
                            onKillPressed:                  () => Dispatcher.BeginInvoke(() => Close()),
                            onScreenAnalysisPressed:        () => _ = Dispatcher.InvokeAsync(async () =>
                            {
                                try { await HandleScreenAnalysisAsync(); }
                                catch (Exception ex) { DebugWindow.Log("SCREEN_ERR", ex.Message); StopThinkingUi(); }
                            }),
                            onPrimaryScreenAnalysisPressed: () => _ = Dispatcher.InvokeAsync(async () =>
                            {
                                try { await HandlePrimaryScreenAnalysisAsync(); }
                                catch (Exception ex) { DebugWindow.Log("SCREEN_ERR", ex.Message); StopThinkingUi(); }
                            })
                        );
                        _globalHotkey.OwnerWindowHandle = mainHwnd;
                        // If the app crashes before OnClosed fires, still release the system-wide hook
                        Application.Current.DispatcherUnhandledException += (_, __) => _globalHotkey?.Dispose();
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
                        // Fetch SM key for logged-in user (backend returns server key)
                        _ = UserSession.FetchSpeechmaticsKeyAsync(DeviceIdentity.Current).ContinueWith(
                            t => { if (t.IsFaulted) DebugWindow.Log("STT_KEY", t.Exception?.GetBaseException().Message ?? "fetch failed"); },
                            TaskScheduler.Default);
                        UpdateProfileUI();
                        await StartNewSessionAsync(); // Auto-start recording immediately on app open
                    }
                    else
                    {
                        SetLoggedOutUI();
                        // Fetch real guest credits from backend using device ID.
                        // The backend tracks this device's monthly 100-credit allowance.
                        _ = FetchAndDisplayCreditsAsync().ContinueWith(t => {
                            if (t.IsFaulted) DebugWindow.Log("GUEST_CREDITS", t.Exception?.GetBaseException().Message ?? "fetch failed");
                        }, TaskScheduler.Default);
                        // Fetch SM key for guest — backend identifies device via X-Device-Id.
                        _ = UserSession.FetchSpeechmaticsKeyAsync(DeviceIdentity.Current).ContinueWith(
                            t => { if (t.IsFaulted) DebugWindow.Log("STT_KEY", t.Exception?.GetBaseException().Message ?? "fetch failed"); },
                            TaskScheduler.Default);
                    }

                    creditsRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(CreditRefreshMinutes) };
                    creditsRefreshTimer.Tick += async (s2, e2) =>
                    {
                        try { await FetchAndDisplayCreditsAsync(); }
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
            try
            {
                var loginWin = new LoginWindow();
                loginWin.Owner = this;
                // AnswerWindow is Topmost=True; lower it so the login dialog isn't hidden behind it.
                bool wasTopmost = answerWindow != null && answerWindow.Topmost;
                if (wasTopmost && answerWindow != null) answerWindow.Topmost = false;
                loginWin.ShowDialog();
                if (wasTopmost && answerWindow != null) answerWindow.Topmost = true;
                if (loginWin.LoginSuccess)
                {
                    UpdateProfileUI();
                    await FetchAndDisplayCreditsAsync();
                    // Fetch SM key now that we have a valid token
                    _ = UserSession.FetchSpeechmaticsKeyAsync(DeviceIdentity.Current).ContinueWith(
                        t => { if (t.IsFaulted) DebugWindow.Log("STT_KEY", t.Exception?.GetBaseException().Message ?? "fetch failed"); },
                        TaskScheduler.Default);
                    await StartNewSessionAsync();
                    DebugWindow.Log("AUTH", $"Logged in: {UserSession.Email}");
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log("AUTH_ERR", ex.Message);
            }
        }

        private void ProfileBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => ToggleProfileDropdown(ProfileBadge);

        private void ToggleProfileDropdown(FrameworkElement? anchor = null)
        {
            if (ProfileDropdownPopup.IsOpen)
            {
                ProfileDropdownPopup.IsOpen = false;
                return;
            }
            if (anchor != null)
                ProfileDropdownPopup.PlacementTarget = anchor;
            UpdateProfileDropdown();
            ProfileDropdownPopup.IsOpen = true;
        }

        // Close popups when the user clicks anywhere outside them
        private void Window_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Close saved-resumes popup
            if (SavedResumesPopup.IsOpen)
            {
                var src2 = e.OriginalSource as DependencyObject;
                var spc = SavedResumesPopup.Child as FrameworkElement;
                bool insidePopup2 = spc != null && src2 != null && IsDescendantOf(src2, spc);
                bool onBtn = src2 != null && IsDescendantOf(src2, SavedResumesBtn);
                if (!insidePopup2 && !onBtn) SavedResumesPopup.IsOpen = false;
            }

            if (!ProfileDropdownPopup.IsOpen) return;

            var srcP = e.OriginalSource as DependencyObject;
            var ppc  = ProfileDropdownPopup.Child as FrameworkElement;
            bool insideProfile = ppc != null && srcP != null && IsDescendantOf(srcP, ppc);
            bool onBadge       = srcP != null && IsDescendantOf(srcP, ProfileBadge);
            if (insideProfile || onBadge) return;

            ProfileDropdownPopup.IsOpen = false;
        }

        private static bool IsDescendantOf(DependencyObject child, DependencyObject parent)
        {
            var cur = child;
            while (cur != null)
            {
                if (cur == parent) return true;
                cur = System.Windows.Media.VisualTreeHelper.GetParent(cur) ??
                      System.Windows.LogicalTreeHelper.GetParent(cur);
            }
            return false;
        }

        private void UpdateProfileDropdown()
        {
            bool loggedIn = UserSession.IsLoggedIn;

            // Avatar + name + status
            string initials = loggedIn ? (UserSession.Initials ?? "?") : "GU";
            string name     = loggedIn ? (UserSession.Name ?? UserSession.Email ?? "User") : "Guest";
            string status   = loggedIn ? (UserSession.Email ?? "") : "Free trial — not signed in";

            PopupAvatarInitials.Text  = initials;
            PopupProfileName.Text     = name;
            PopupProfileStatus.Text   = status;

            // Credits card — always use real backend value (guests get 100/month by device ID)
            if (loggedIn && UserSession.IsUnlimited)
            {
                PopupCreditsAmount.Text = "Unlimited";
                PopupCreditsAmount.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#a78bfa"));
                PopupCreditsPlan.Text = $"{UserSession.Plan} plan";
                PopupSignInCardBtn.Visibility = Visibility.Collapsed;
            }
            else if (!_creditsFetched)
            {
                // Backend hasn't responded yet — show loading state and trigger a fresh fetch.
                // When the fetch completes it will re-invoke UpdateProfileDropdown if the popup is still open.
                PopupCreditsAmount.Text = "Loading…";
                PopupCreditsAmount.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6b7280"));
                PopupCreditsPlan.Text = loggedIn ? $"{UserSession.Plan} plan" : "Free trial";
                PopupSignInCardBtn.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
                _ = FetchAndDisplayCreditsAsync().ContinueWith(_ =>
                    Dispatcher.InvokeAsync(() => { if (ProfileDropdownPopup.IsOpen) UpdateProfileDropdown(); }),
                    TaskScheduler.Default);
            }
            else
            {
                int credits = UserSession.Credits;
                string creditStr = credits >= 1000 ? $"{credits / 1000.0:F1}k credits" : credits > 0 ? $"{credits} credits" : "0 credits";
                PopupCreditsAmount.Text = creditStr;
                string color = credits > 20 ? "#4ade80" : credits > 5 ? "#f59e0b" : "#ef4444";
                PopupCreditsAmount.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                PopupCreditsPlan.Text = loggedIn ? $"{UserSession.Plan} plan" : "Free trial";
                PopupSignInCardBtn.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
            }

            // The slider's 1-100 range maps to a safe real-opacity range of 50-100%.
            double storedOp = SettingsWindow.GetMainWindowOpacity();
            double opPct    = Math.Clamp(1 + (storedOp - 0.50) / 0.50 * 99, 1, 100);
            _suppressOpacitySlider = true;
            PopupOpacitySlider.Value = opPct;
            _suppressOpacitySlider = false;
            PopupOpacityLabel.Text   = $"{(int)opPct}%";

            // Sign In / Sign Out rows
            PopupSignInRow.Visibility  = loggedIn ? Visibility.Collapsed : Visibility.Visible;
            PopupSignOutRow.Visibility = loggedIn ? Visibility.Visible   : Visibility.Collapsed;
        }

        private void PopupOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressOpacitySlider) return;
            if (PopupOpacityLabel != null)
                PopupOpacityLabel.Text = $"{(int)e.NewValue}%";
            // Slider maps to shared app opacity from 50%-100%.
            double opacity = 0.50 + (e.NewValue - 1) / 99.0 * 0.50;
            this.Opacity = opacity;
            answerWindow?.ApplyOverlayOpacity();
            // Persist the shared preference for the main and eye-mode windows.
            var cfg = SettingsWindow.LoadConfig();
            cfg.MainWindowOpacity = Math.Round(opacity, 2);
            cfg.OverlayOpacity    = cfg.MainWindowOpacity;
            SettingsWindow.SaveConfig(cfg);
        }

        private void PopupSettings_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ProfileDropdownPopup.IsOpen = false;
            SettingsBtn_Click(sender, new RoutedEventArgs());
        }

        private void PopupSessions_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ProfileDropdownPopup.IsOpen = false;
            SessionsBtn_Click(sender, new RoutedEventArgs());
        }

        private void PopupSignIn_Click(object sender, RoutedEventArgs e)
        {
            ProfileDropdownPopup.IsOpen = false;
            SignInHeaderBtn_Click(sender, new RoutedEventArgs());
        }

        private void PopupSignOut_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ProfileDropdownPopup.IsOpen = false;
            UserSession.Clear();
            _creditsFetched = false;  // reset so the popup shows Loading... then real guest credits
            SetLoggedOutUI();
            _ = FetchAndDisplayCreditsAsync().ContinueWith(t => {
                if (t.IsFaulted) DebugWindow.Log("CREDITS", t.Exception?.GetBaseException().Message ?? "refresh failed");
            }, TaskScheduler.Default);
            // Re-fetch SM key for the new guest session (same device, same key)
            _ = UserSession.FetchSpeechmaticsKeyAsync(DeviceIdentity.Current).ContinueWith(
                t => { if (t.IsFaulted) DebugWindow.Log("STT_KEY", t.Exception?.GetBaseException().Message ?? "fetch failed"); },
                TaskScheduler.Default);
            DebugWindow.Log("AUTH", "Signed out");
        }

        private void UpdateProfileUI()
        {
            // Header profile badge — always visible
            ProfileBadge.Visibility    = Visibility.Visible;
            SignInHeaderBtn.Visibility = Visibility.Collapsed;
            SessionsBtn.Visibility     = Visibility.Visible;

            string initials = UserSession.IsLoggedIn ? (UserSession.Initials ?? "?") : "GU";
            string firstName = UserSession.IsLoggedIn
                ? (UserSession.Name?.Split(' ')[0] ?? UserSession.Email ?? "")
                : "";
            AvatarInitials.Text    = initials;
            ProfileNameLabel.Text  = firstName;

            if (UserSession.IsLoggedIn && UserSession.IsUnlimited)
                AvatarInitials.Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#a78bfa"));
            else
                AvatarInitials.Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#38BDF8"));
        }

        private void SetLoggedOutUI()
        {
            // Header — show guest profile badge (no separate Sign In button)
            ProfileBadge.Visibility    = Visibility.Visible;
            SignInHeaderBtn.Visibility = Visibility.Collapsed;
            SessionsBtn.Visibility     = Visibility.Collapsed;
            AvatarInitials.Text        = "GU";
            ProfileNameLabel.Text      = "";
            AvatarInitials.Foreground  = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#38BDF8"));

            // Credits badge — show loading state; real value fetched from backend via device ID
            CreditsLabel.Text           = "⚡ ···";
            CreditsPlanLabel.Visibility = Visibility.Collapsed;
            CreditsIcon.Text            = "";
            CreditsLabel.Foreground     = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#4ade80"));
            SetCreditsBadgeStyle("#0f2a1a", "#1a6b3a");

            UserSession.IsGuestSession = true;

            if (isRecording) EndSession();
        }

        // ══════════════════════════════════════════════════════════════════════
        // CREDITS
        // ══════════════════════════════════════════════════════════════════════
        private async Task FetchAndDisplayCreditsAsync()
        {
            // Refresh token for signed-in users only; guests have no token to refresh.
            if (UserSession.IsLoggedIn)
                await UserSession.TryRefreshAsync();

            void CLog(string msg) => DebugWindow.Log("CREDITS", msg);

            CLog($"Fetching… guest={UserSession.IsGuestSession}");

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{BackendUrl}/api/v1/interview/credits");

                // Only add Authorization header when we have a real token;
                // sending "Bearer " (empty) can throw a header parse exception in .NET.
                if (!string.IsNullOrEmpty(UserSession.IdToken))
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {UserSession.IdToken}");

                // Always send device ID — backend uses this to track per-device guest credits
                // and enforce the monthly 100-credit limit per physical machine.
                req.Headers.TryAddWithoutValidation("X-Device-Id", DeviceIdentity.Current);

                var res = await _creditsClient.SendAsync(req);
                string body = await res.Content.ReadAsStringAsync();
                CLog($"HTTP {(int)res.StatusCode} body={body[..Math.Min(body.Length, 200)]}");

                if (!res.IsSuccessStatusCode)
                {
                    Dispatcher.Invoke(() => { CreditsLabel.Text = "⚡ —"; CreditsPlanLabel.Visibility = Visibility.Collapsed; });
                    return;
                }

                using var doc = JsonDocument.Parse(body);
                int credits = doc.RootElement.TryGetProperty("credits", out var c) ? c.GetInt32() : 0;
                string plan = doc.RootElement.TryGetProperty("plan", out var p) ? p.GetString() ?? "free" : "free";
                bool isUnlimited = doc.RootElement.TryGetProperty("isUnlimited", out var u) ? u.GetBoolean() : false;

                UserSession.Credits = credits;
                UserSession.Plan = plan;
                CLog($"Parsed: {credits} credits | {plan} | unlimited={isUnlimited}");

                Dispatcher.Invoke(() =>
                {
                    CreditsPlanLabel.Visibility = Visibility.Collapsed;

                    if (isUnlimited)
                    {
                        CreditsLabel.Text = "∞  Pro";
                        CreditsIcon.Text = "👑";
                        SetCreditsBadgeStyle("#1a0a2e", "#7c3aed");
                        CreditsLabel.Foreground = new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString("#a78bfa"));
                    }
                    else
                    {
                        string display = credits >= 1000 ? $"{credits / 1000.0:F1}k" : credits.ToString("N0");
                        CreditsLabel.Text = $"⚡ {display}";
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
                    CLog($"Badge set to: {CreditsLabel.Text}");
                });

                _creditsFetched = true;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => { CreditsLabel.Text = "⚡ —"; CreditsPlanLabel.Visibility = Visibility.Collapsed; });
                CLog($"EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void SetCreditsBadgeStyle(string bg, string border)
        {
            CreditsBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
            CreditsBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border));
        }

        private void CreditsBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Credits badge = quick action, NOT the profile dropdown.
            // Guest  → open pricing page so they can see plans and get more credits.
            // Signed in → silently refresh credit count from backend.
            if (!UserSession.IsLoggedIn)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://coopilotxai.com/pricing") { UseShellExecute = true }); }
                catch { }
            }
            else
                _ = FetchAndDisplayCreditsAsync().ContinueWith(t => {
                    if (t.IsFaulted) DebugWindow.Log("CREDITS", t.Exception?.GetBaseException().Message ?? "refresh failed");
                }, TaskScheduler.Default);
        }

        // ══════════════════════════════════════════════════════════════════════
        // OPACITY / SCRIPT FOLDER
        // ══════════════════════════════════════════════════════════════════════
        private void ApplyMainWindowOpacity()
        {
            this.Opacity = SettingsWindow.GetMainWindowOpacity();
            answerWindow?.ApplyOverlayOpacity();
        }

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
            if (answerWindow == null)
            {
                DebugWindow.Log("CAMERA", "Overlay is unavailable; keeping the main workspace visible.");
                return;
            }

            _isCameraMode = true;
            NormalModeGrid.Visibility = Visibility.Collapsed;
            answerWindow.ToggleCameraMode(true);
            this.Hide();
        }

        private void ExitCameraMode_Click(object sender, RoutedEventArgs e) => ExitCameraMode();

        private void ExitCameraMode()
        {
            _isCameraMode = false;
            this.Show();
            RestoreMainWindowFrame();
            NormalModeGrid.Visibility = Visibility.Visible;
            if (answerWindow != null) answerWindow.ToggleCameraMode(false);
        }

        private void RestoreMainWindowFrame()
        {
            var workArea = SystemParameters.WorkArea;
            Width = Math.Min(DefaultMainWindowWidth, workArea.Width);
            Height = Math.Min(DefaultMainWindowHeight, workArea.Height);
            Top = workArea.Top + (workArea.Height - Height) / 2;
            Left = workArea.Left + (workArea.Width - Width) / 2;
        }

        // ══════════════════════════════════════════════════════════════════════
        // MIC
        // ══════════════════════════════════════════════════════════════════════
        private bool _spaceHandling = false;

        // Push-to-talk: DOWN = start listening, UP = fire AI.
        // Toggle callers (button, in-app Space) call both in sequence.
        private void HandleSpaceDown(string source)
        {
            if (_engineUsageLimitReached)
            {
                ShowEngineUsageLimitError();
                return;
            }
            if (_spaceHandling || isProcessing || !isMuted) return;
            _spaceHandling = true;
            try
            {
                isMuted = false; isListening = true;
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
                DebugWindow.Log("MIC", $"[{source}] UNMUTED — listening");
                UpdateMicUi();
            }
            finally { _spaceHandling = false; }
        }

        private void HandleSpaceUp(string source)
        {
            if (_spaceHandling || isProcessing || isMuted) return;
            _spaceHandling = true;
            try
            {
                isListening = false; WritePauseFlag(); isMuted = true;
                DebugWindow.Log("MIC", $"[{source}] MUTED — firing AI");
                UpdateMicUi();
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
            finally { _spaceHandling = false; }
        }

        // Button and in-app Space toggle: treat as push-to-talk in sequence
        private void HandleSpacePress(string source)
        {
            if (isMuted) HandleSpaceDown(source);
            else          HandleSpaceUp(source);
        }

        private void MicBtn_Click(object sender, RoutedEventArgs e) => HandleSpacePress("BUTTON");

        // ══════════════════════════════════════════════════════════════════════
        // AI — routes through CoopilotX backend (credits deducted server-side)
        // ══════════════════════════════════════════════════════════════════════
        private async Task AskAiAsync(string? customQuestion = null)
        {
            if (isProcessing) return;
            isProcessing = true; // guard: set before any await so no second call can sneak through

            // Outer try/catch wraps the ENTIRE method body — including setup awaits —
            // so no exception can ever escape this Task unobserved.
            string q = "";
            var streamedAnswer = new StringBuilder();
            try
            {
                q = string.IsNullOrWhiteSpace(customQuestion)
                    ? TranscriptTextBlock.Text.Trim()
                    : customQuestion.Trim();
                if (string.IsNullOrWhiteSpace(q)) { isProcessing = false; UpdateMicUi(); return; }

                if (UserSession.IsLoggedIn) await UserSession.TryRefreshAsync();

                // Block once credits have been fetched and confirmed exhausted.
                // _creditsFetched prevents false-blocking before the first backend response.
                if (!UserSession.IsUnlimited && _creditsFetched && UserSession.Credits < CreditsCriticalThreshold)
                {
                    AiAnswerBox.Text = $"⚠ Not enough credits ({UserSession.Credits} remaining).\n\nVisit coopilotxai.com/pricing to upgrade.";
                    isProcessing = false;
                    UpdateMicUi();
                    return;
                }

                thinkingStep = 0;
                _answerIsBehavioral = PromptBuilder.IsBehavioral(q);
                StarBadge.Visibility = _answerIsBehavioral ? Visibility.Visible : Visibility.Collapsed;
                ThinkingPanel.Visibility = Visibility.Visible;
                thinkingTimer?.Start();
                UpdateMicUi();

                string sep = "\n" + new string('─', 45) + "\n\n";
                string old = AiAnswerBox.Text;
                AiAnswerBox.Text = $"Q: {q}\n\n";
                if (answerWindow != null) { answerWindow.UpdateAnswer(""); answerWindow.UpdateQuestion(q); }

                int tokenCount = 0;

                await foreach (var token in StreamFromBackend(q, ResumeTextBox.Text))
                {
                    streamedAnswer.Append(token); tokenCount++;
                    if (tokenCount == 1)
                    {
                        thinkingTimer?.Stop();
                        ThinkingPanel.Visibility = Visibility.Collapsed;
                    }
                    if (tokenCount % 3 == 0 || token.Contains('\n'))
                    {
                        string soFar = CleanAiOutput(streamedAnswer.ToString());
                        AiAnswerBox.Text = $"Q: {q}\n\n{soFar}";
                        AiAnswerBox.ScrollToEnd();
                        if (answerWindow != null) answerWindow.UpdateAnswer(soFar);
                    }
                }

                string final = CleanAiOutput(streamedAnswer.ToString());
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
                string partial = CleanAiOutput(streamedAnswer.ToString());
                string failure = "Connection interrupted. Please try again.";
                AiAnswerBox.Text = string.IsNullOrWhiteSpace(partial)
                    ? $"{label}{failure}"
                    : $"{label}{partial}\n\n{failure}";
                if (answerWindow != null)
                    answerWindow.UpdateAnswer(string.IsNullOrWhiteSpace(partial)
                        ? failure
                        : $"{partial}\n\n{failure}");
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
            await foreach (string token in StreamSseTokensAsync(res, ct))
                yield return token;
        }

        // Handles the HTTP request; exceptions bubble up to AskAiAsync's catch block.
        private async Task<HttpResponseMessage> SendBackendRequestAsync(
            string question, string resume, System.Threading.CancellationToken ct)
        {
            PromptBuilder.SetContext(_liveHints, _companyName, _jobDescription);
            string resumeFacts = ResumeParser.ExtractFacts(resume);
            var messages = PromptBuilder.BuildMessages(resumeFacts, question);
            // Embed context + locked facts + format rule directly in the question field
            // so the backend model always sees them, even if it ignores the messages array.
            string enhancedQuestion = PromptBuilder.BuildEnhancedQuestion(question, resumeFacts);
            const int MaxResumeChars = 30_000;
            string safeResume = (resume ?? "").Length > MaxResumeChars
                ? resume!.Substring(0, MaxResumeChars) + "\n[truncated]"
                : (resume ?? "");
            var provider = SettingsWindow.IsGroq() ? "groq" : "openai";
            var payload = new { question = enhancedQuestion, resume = safeResume, provider, messages };
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{BackendUrl}/api/v1/interview/ask");
            if (!string.IsNullOrEmpty(UserSession.IdToken))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {UserSession.IdToken}");
            request.Headers.TryAddWithoutValidation("X-Device-Id", DeviceIdentity.Current);
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

        private static async IAsyncEnumerable<string> StreamSseTokensAsync(
            HttpResponseMessage res,
            [EnumeratorCancellation] System.Threading.CancellationToken ct = default)
        {
            int responseLength = 0;
            using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;
                string data = line["data: ".Length..];
                if (data == "[DONE]") yield break;
                if (!TryReadSseToken(data, out string token, out bool isTerminal)) continue;

                int remaining = MaxAiResponseChars - responseLength;
                if (remaining <= 0)
                {
                    yield return "\n[Response truncated]";
                    yield break;
                }

                if (token.Length > remaining) token = token[..remaining];
                responseLength += token.Length;
                yield return token;

                if (isTerminal || responseLength >= MaxAiResponseChars)
                {
                    if (!isTerminal && responseLength >= MaxAiResponseChars)
                        yield return "\n[Response truncated]";
                    yield break;
                }
            }
        }

        private static bool TryReadSseToken(string data, out string token, out bool isTerminal)
        {
            token = "";
            isTerminal = false;

            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    token = $"Server error: {error.GetString() ?? "Unknown error"}";
                    isTerminal = true;
                    return true;
                }

                if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                    choices.GetArrayLength() == 0 ||
                    !choices[0].TryGetProperty("delta", out var delta) ||
                    !delta.TryGetProperty("content", out var content))
                    return false;

                token = content.GetString() ?? "";
                return !string.IsNullOrEmpty(token);
            }
            catch (JsonException)
            {
                return false;
            }
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
            // If the timer was still ticking (error before any token), the overlay
            // is stuck showing "Thinking..." dots — clear them now.
            bool wasThinking = thinkingTimer?.IsEnabled == true;
            thinkingTimer?.Stop();
            ThinkingPanel.Visibility     = Visibility.Collapsed;
            ThinkingHintLabel.Visibility = Visibility.Visible;
            ThinkingLabel.Text           = "Thinking...";
            isProcessing       = false;
            _isScreenAnalyzing = false;
            if (_isCameraMode && wasThinking && answerWindow != null)
                answerWindow.UpdateAnswer(""); // clear stuck thinking dots
            UpdateMicUi();
        }

        // ══════════════════════════════════════════════════════════════════════
        // SESSION
        // ══════════════════════════════════════════════════════════════════════
        private async Task StartNewSessionAsync()
        {
            await CloudSessionSync.ResetSessionAsync();
            // Find next available session number
            while (File.Exists(Path.Combine(AppDataFolder, "interview_" + sessionNumber + ".txt")))
                sessionNumber++;
            sessionLogPath = Path.Combine(AppDataFolder, "interview_" + sessionNumber + ".txt");
            _recordingSessionId = Guid.NewGuid().ToString("N");

            // Write header with timestamp so the Sessions window can parse it
            string header = $"SESSION {sessionNumber} | {SettingsWindow.GetActiveModelId()} | {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}";
            try
            {
                SecureDataProtector.WriteProtectedFile(sessionLogPath, header + "\n\n");
                File.WriteAllText(RecordingIdPath, _recordingSessionId);
                File.WriteAllText(RecordingSessionNumberPath, sessionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
                File.WriteAllText(Path.Combine(AppDataFolder, "record.flag"), "1");
            }
            catch (Exception ex)
            {
                DebugWindow.Log("SESSION", $"Could not create session log: {ex.Message}");
                sessionLogPath = "";
                return;
            }
            isRecording = true;

            // Clear conversation history for a fresh session
            PromptBuilder.ClearHistory();
            LockResume();
            PromptBuilder.SetContext(_liveHints, _companyName, _jobDescription);

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
            RecordingBadge.Visibility = isRecording ? Visibility.Visible : Visibility.Collapsed;
            DebugWindow.Log("SESSION", $"Auto-started session #{sessionNumber}");
        }

        private void AppendToSessionLog(string q, string a)
        {
            if (!string.IsNullOrEmpty(sessionLogPath))
            {
                try
                {
                    string content = SecureDataProtector.ReadProtectedFile(sessionLogPath);
                    SecureDataProtector.WriteProtectedFile(sessionLogPath, content + $"Q: {q}\nA: {a}\n\n");
                }
                catch (Exception ex) { DebugWindow.Log("SESSION", $"Log write failed: {ex.Message}"); }
            }
            _ = CloudSessionSync.SyncTurnAsync(q, a, ResumeTextBox.Text, _sessionSeconds);
        }

        private void EndSession()
        {
            try
            {
                string f = Path.Combine(AppDataFolder, "record.flag");
                if (File.Exists(f)) File.Delete(f);
            }
            catch (Exception ex)
            {
                DebugWindow.Log("SESSION", $"Could not stop recording: {ex.Message}");
            }
            isRecording = false;
            sessionLogPath = "";

            // Stop and hide session timer
            _sessionTimer?.Stop();
            SessionTimerBadge.Visibility = Visibility.Collapsed;

            // Hide REC badge
            RecordingBadge.Visibility = Visibility.Collapsed;
            PromptBuilder.ClearHistory();
            UnlockResume();
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

        private string ReadLatestTxtSafe()
        {
            string path = Path.Combine(AppDataFolder, "latest.txt");
            for (int i = 0; i < TranscriptRetryCount; i++)
            {
                try { return File.ReadAllText(path); }
                catch { }
            }
            return TranscriptTextBlock.Text;
        }

        // ══════════════════════════════════════════════════════════════════════
        // ENGINE
        // ══════════════════════════════════════════════════════════════════════
        private async void StartSpeechmaticsEngine()
        {
            int generation = Interlocked.Increment(ref _engineStartGeneration);
            _engineStarting = true;
            try
            {
                // Cancel any existing stream-reader tasks, then issue a fresh token
                _engineCts.Cancel();
                _engineCts.Dispose();
                _engineCts = new CancellationTokenSource();
                var ct = _engineCts.Token;

                KillAndDisposeEngine();
                _engineAuthFailed  = false; // reset so MonitorEngine can restart after a key change
                _engineUsageLimitReached = false;
                _engineFatalReason = "";

                string pyScript = Path.Combine(scriptFolder, "speechmatics_engine.py");
                if (!File.Exists(pyScript)) { DebugWindow.Log("ENGINE", "❌ Not found: " + scriptFolder); return; }
                // Key priority:
                // 1. Backend-fetched key (guests always use this; logged-in users too)
                // 2. Retry backend fetch if #1 is empty (startup race: engine started before fetch completed)
                // 3. Settings key — only for non-guests who entered their own SM account key
                // Never use the Settings key for guests — it's likely an old/expired key that causes KEY INVALID.
                string smKey = UserSession.SpeechmaticsKey;
                if (string.IsNullOrWhiteSpace(smKey))
                {
                    DebugWindow.Log("ENGINE", "SM key not yet available, waiting for fetch…");
                    await UserSession.FetchSpeechmaticsKeyAsync(DeviceIdentity.Current);
                    smKey = UserSession.SpeechmaticsKey;
                }
                // For non-guest users who entered their own key in Settings, allow that as fallback.
                if (string.IsNullOrWhiteSpace(smKey) && !UserSession.IsGuestSession)
                    smKey = SettingsWindow.GetSpeechmaticsKey();
                if (string.IsNullOrWhiteSpace(smKey)) { DebugWindow.Log("ENGINE", "❌ No SM key — server unreachable."); return; }

                // Resolve Python executable: try "py" launcher first (Windows standard),
                // then "python", then "python3" — log which one succeeds.
                string? pyExe = await Task.Run(ResolvePythonExecutable);
                if (generation != Volatile.Read(ref _engineStartGeneration)) return;
                if (string.IsNullOrWhiteSpace(pyExe))
                {
                    _engineFatalReason = "Install Python 3.11+ using the official Windows installer";
                    _engineAuthFailed = true;
                    ShowEngineAuthError();
                    return;
                }
                DebugWindow.Log("ENGINE", $"Python executable: {pyExe}");

                speechmaticsProcess = new Process();
                speechmaticsProcess.StartInfo.FileName = pyExe;
                string deviceArg = _audioDeviceId >= 0 ? $" --device {_audioDeviceId}" : "";
                string modeArg   = SettingsWindow.GetMicCaptureEnabled() ? " --mode both" : " --mode system";
                speechmaticsProcess.StartInfo.Arguments = $"-3 \"{pyScript}\"{deviceArg}{modeArg}";
                speechmaticsProcess.StartInfo.EnvironmentVariables["SM_API_KEY"] = smKey;
                speechmaticsProcess.StartInfo.WorkingDirectory = scriptFolder;
                speechmaticsProcess.StartInfo.CreateNoWindow = true;
                speechmaticsProcess.StartInfo.UseShellExecute = false;
                speechmaticsProcess.StartInfo.RedirectStandardOutput = true;
                speechmaticsProcess.StartInfo.RedirectStandardError = true;
                speechmaticsProcess.Start();
                DebugWindow.Log("ENGINE", $"STARTED | PID: {speechmaticsProcess.Id}");

                // Save PID so NuclearKillOldProcesses can target only this process on next startup
                try
                {
                    long startedAt = speechmaticsProcess.StartTime.ToFileTimeUtc();
                    File.WriteAllText(EnginePidPath, $"{speechmaticsProcess.Id}|{startedAt}");
                }
                catch { }

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
                            _ = Dispatcher.BeginInvoke(new Action(() => DebugWindow.Log("PY", line)));
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
                            _ = Dispatcher.BeginInvoke(new Action(() => DebugWindow.Log("PY_ERR", line)));

                            // Fatal Python errors that cannot self-recover — stop the restart loop.
                            // ModuleNotFoundError / ImportError means a pip package is missing.
                            if (line.Contains("ModuleNotFoundError") || line.Contains("ImportError"))
                            {
                                string missing = line.Contains("'") ? line.Split('\'')[1] : "a required package";
                                _engineFatalReason = $"pip install {missing}";
                                _engineAuthFailed  = true;
                                _ = Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    DebugWindow.Log("ENGINE", $"❌ Missing Python package: {missing}  →  run: pip install {missing}");
                                    ShowEngineAuthError();
                                }));
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { DebugWindow.Log("PY_ERR_READER", ex.Message); }
                }, ct);
            }
            catch (Exception ex) { DebugWindow.Log("ENGINE_ERR", ex.Message); }
            finally
            {
                if (generation == Volatile.Read(ref _engineStartGeneration))
                    _engineStarting = false;
            }
        }

        /// <summary>Kill + Dispose the Python process and null the reference.</summary>
        private void KillAndDisposeEngine()
        {
            var proc = speechmaticsProcess;
            speechmaticsProcess = null;
            if (proc == null) return;
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { proc.Dispose(); } catch { }
            // Clean up PID file on a normal kill so NuclearKill doesn't double-attempt it
            try { if (File.Exists(EnginePidPath)) File.Delete(EnginePidPath); } catch { }
        }

        private void WritePauseFlag() { try { File.WriteAllText(Path.Combine(AppDataFolder, "pause.flag"), "1"); } catch (Exception ex) { DebugWindow.Log("FILE", $"pause.flag write failed: {ex.Message}"); } }
        private void DeletePauseFlag() { try { string f = Path.Combine(AppDataFolder, "pause.flag"); if (File.Exists(f)) File.Delete(f); } catch (Exception ex) { DebugWindow.Log("FILE", $"pause.flag delete failed: {ex.Message}"); } }
        // ── PID file path — stores our engine's PID for crash-recovery cleanup ─
        private string EnginePidPath => Path.Combine(AppDataFolder, "engine.pid");
        private string RecordingIdPath => Path.Combine(AppDataFolder, "recording.id");
        private string RecordingSessionNumberPath => Path.Combine(AppDataFolder, "recording_session_number.txt");

        private string RecordingSavedPath(string recordingId) => Path.Combine(
            AppDataFolder, $"recording_saved_{recordingId}.flag");

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
                    string[] parts = File.ReadAllText(EnginePidPath).Trim().Split('|');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int savedPid) &&
                        long.TryParse(parts[1], out long savedStartTime))
                    {
                        try
                        {
                            var orphan = Process.GetProcessById(savedPid);
                            if (!orphan.HasExited && orphan.StartTime.ToFileTimeUtc() == savedStartTime)
                            {
                                orphan.Kill(entireProcessTree: true);
                                DebugWindow.Log("ENGINE", $"Killed orphaned engine PID {savedPid}");
                            }
                            else if (!orphan.HasExited)
                                DebugWindow.Log("ENGINE", $"Skipped reused PID {savedPid}");
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
        /// Finds the Windows Python launcher from the system directory.
        /// Avoids executing an untrusted python.exe supplied by the working directory or PATH.
        /// Result is cached after first resolution so UI thread never blocks twice.
        /// </summary>
        private static string? _cachedPythonExe;
        private static string? ResolvePythonExecutable()
        {
            if (_cachedPythonExe != null) return _cachedPythonExe;
            string launcher = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "py.exe");
            if (File.Exists(launcher))
            {
                try
                {
                    using var probe = new Process();
                    probe.StartInfo.FileName = launcher;
                    probe.StartInfo.Arguments = "-3 --version";
                    probe.StartInfo.CreateNoWindow = true;
                    probe.StartInfo.UseShellExecute = false;
                    probe.StartInfo.RedirectStandardOutput = true;
                    probe.StartInfo.RedirectStandardError = true;
                    probe.Start();
                    probe.WaitForExit(2000);
                    if (probe.ExitCode == 0) { _cachedPythonExe = launcher; return launcher; }
                }
                catch { }
            }
            return null;
        }
        private void MonitorEngine()
        {
            if (_engineUsageLimitReached) return;
            if (_engineStarting || _engineAuthFailed) return; // auth failure — do not restart
            if (speechmaticsProcess == null || speechmaticsProcess.HasExited)
            {
                int code = -1;
                try { code = speechmaticsProcess?.ExitCode ?? -1; } catch { }
                if (code == SpeechRecognitionExitCodes.AudioUsageLimit)
                {
                    _engineUsageLimitReached = true;
                    DebugWindow.Log("ENGINE", "Speechmatics audio usage limit reached; engine stopped without retrying.");
                    Dispatcher.Invoke(ShowEngineUsageLimitError);
                    return;
                }
                if (code == SpeechRecognitionExitCodes.AuthenticationFailure)
                {
                    _engineAuthFailed = true;
                    DebugWindow.Log("ENGINE", "❌ Auth failure (exit 2) — engine stopped. Fix your Speechmatics key in Settings.");
                    Dispatcher.Invoke(() => ShowEngineAuthError());
                    return;
                }
                DebugWindow.Log("ENGINE", "Dead — restarting...");
                if (isListening)
                {
                    isListening = false;
                    try { File.WriteAllText(Path.Combine(AppDataFolder, "latest.txt"), ""); } catch { }
                    Dispatcher.Invoke(() => UpdateMicUi());
                }
                StartSpeechmaticsEngine();
            }
        }

        private void ShowEngineAuthError()
        {
            string label = string.IsNullOrEmpty(_engineFatalReason) ? "KEY INVALID" : "INSTALL NEEDED";
            MicIndicator.Fill     = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ef4444"));
            MicIndicatorText.Text = label;
            MicBtn.BorderBrush    = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ef4444"));
        }

        private void ShowEngineUsageLimitError()
        {
            const string message = "Speech transcription is unavailable because the audio usage limit has been reached. Add available transcription capacity, then restart the audio service from Settings.";
            isListening = false;
            isMuted = true;
            MicIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF6B73"));
            MicIndicatorText.Text = "AUDIO LIMIT";
            MicBtn.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF6B73"));
            if (_isCameraMode && answerWindow != null)
                answerWindow.ShowServiceUnavailable("Audio limit", message);
            else
                AiAnswerBox.Text = message;
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.F12) { e.Handled = true; ToggleDebugWindow(); return; }
            if (e.Key == System.Windows.Input.Key.F8)
            {
                e.Handled = true;
                _ = HandleScreenAnalysisAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Dispatcher.Invoke(() => { DebugWindow.Log("SCREEN_ERR", t.Exception?.GetBaseException().Message ?? "unknown"); StopThinkingUi(); });
                }, TaskScheduler.Default);
                return;
            }
            if (e.Key == System.Windows.Input.Key.F9)
            {
                e.Handled = true;
                _ = HandlePrimaryScreenAnalysisAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Dispatcher.Invoke(() => { DebugWindow.Log("SCREEN_ERR", t.Exception?.GetBaseException().Message ?? "unknown"); StopThinkingUi(); });
                }, TaskScheduler.Default);
                return;
            }
            if (e.Key == System.Windows.Input.Key.Space)
            {
                if (ResumeTextBox.IsFocused   || ResumeTextBox.IsKeyboardFocusWithin)   return;
                if (AskBox.IsFocused          || AskBox.IsKeyboardFocusWithin)           return;
                if (CompanyNameBox.IsFocused  || CompanyNameBox.IsKeyboardFocusWithin)   return;
                if (JobDescBox.IsFocused      || JobDescBox.IsKeyboardFocusWithin)       return;
                e.Handled = true; HandleSpacePress("PREVIEW");
            }
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { /* handled by PreviewKeyDown */ }
        private void Border_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) { try { DragMove(); } catch { } }
        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
        private void MinimizeBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            var sw = new SettingsWindow(_audioDeviceId);
            sw.Owner = this;
            sw.ShowDialog();
            if (sw.SignInRequested) { SignInHeaderBtn_Click(sender, e); return; }
            if (sw.SettingsChanged)
            {
                if (sw.SelectedDeviceIndex >= 0) _audioDeviceId = sw.SelectedDeviceIndex;
                StartSpeechmaticsEngine();
                ApplyMainWindowOpacity();
                if (UserSession.IsLoggedIn)
                    _ = FetchAndDisplayCreditsAsync().ContinueWith(t => {
                        if (t.IsFaulted) DebugWindow.Log("CREDITS_ERR", t.Exception?.GetBaseException().Message ?? "unknown");
                    }, TaskScheduler.Default);
            }
        }

        private void VerifyResume_Click(object sender, RoutedEventArgs e)
        {
            string facts = ResumeParser.ExtractFacts(ResumeTextBox.Text);
            MessageBox.Show(this, facts, "Resume Facts Preview", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(AiAnswerBox.Text); }
            catch (Exception ex) { DebugWindow.Log("CLIPBOARD", ex.Message); }
        }

        // ── Toolbar: Copy answer ──────────────────────────────────────────────
        private void CopyAnswerBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(AiAnswerBox.Text))
            {
                try { Clipboard.SetText(AiAnswerBox.Text); }
                catch (Exception ex) { DebugWindow.Log("CLIPBOARD", ex.Message); }
            }
        }

        // ── Toolbar: Clear transcript + answer ───────────────────────────────
        private void ClearAnswerBtn_Click(object sender, RoutedEventArgs e)
        {
            TranscriptTextBlock.Text = "";
            TranscriptHint.Visibility = Visibility.Visible;
            AiAnswerBox.Text = "";
            StarBadge.Visibility = Visibility.Collapsed;
            if (answerWindow != null) { answerWindow.UpdateAnswer(""); answerWindow.UpdateQuestion(""); }
            PromptBuilder.ClearHistory();
            try { File.WriteAllText(Path.Combine(AppDataFolder, "latest.txt"), ""); } catch { }
        }

        private void AiAnswerBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (AiAnswerHint != null)
                AiAnswerHint.Visibility = string.IsNullOrEmpty(AiAnswerBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Toolbar: End current session and immediately start a new one ─────
        private async void NewSessionBtn_Click(object sender, RoutedEventArgs e)
        {
            if (isProcessing || _newSessionInProgress) return;

            _newSessionInProgress = true;
            var button = sender as System.Windows.Controls.Button;
            object? originalContent = button?.Content;
            if (button != null)
            {
                button.IsEnabled = false;
                button.Content = "Starting...";
            }
            try
            {
            string recordingId = _recordingSessionId;
            bool hadActiveRecording = isRecording;
            EndSession();
            if (hadActiveRecording && await WaitForRecordingSaveAsync(recordingId, RecordingSaveTimeoutMs))
                ProtectRecording(sessionNumber);
            // Don't increment here — StartNewSession's while(File.Exists) scan is the sole source of truth
            TranscriptTextBlock.Text = "";
            TranscriptHint.Visibility = Visibility.Visible;
            AiAnswerBox.Text = "";
            StarBadge.Visibility = Visibility.Collapsed;
            if (answerWindow != null) { answerWindow.UpdateAnswer(""); answerWindow.UpdateQuestion(""); }
            await StartNewSessionAsync();

            if (!isRecording)
                AiAnswerBox.Text = "We could not start a new session. Please try again.";
            }
            catch (Exception ex)
            {
                DebugWindow.Log("SESSION", $"New session failed: {ex.Message}");
                AiAnswerBox.Text = "We could not start a new session. Please try again.";
            }
            finally
            {
                _newSessionInProgress = false;
                if (button != null)
                {
                    button.IsEnabled = true;
                    button.Content = originalContent;
                }
            }
        }

        // ── Resume panel: show/hide watermark based on content ───────────────
        private void ResumeTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (ResumeWatermark != null)
                ResumeWatermark.Visibility = string.IsNullOrWhiteSpace(ResumeTextBox.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
            ResumeParser.InvalidateCache();
        }

        private void ScreenAnalyzeBtn_Click(object sender, RoutedEventArgs e)
            => _ = HandleScreenAnalysisAsync();

        // ── Resume: Upload button ─────────────────────────────────────────────
        private void ResumeUploadBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Upload Resume",
                Filter = "Document files|*.pdf;*.docx;*.txt|All files|*.*"
            };
            if (dlg.ShowDialog() == true)
                LoadResumeFromFile(dlg.FileName);
        }

        private void LoadResumeFromFile(string filePath)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                if (!fileInfo.Exists || fileInfo.Length > MaxResumeFileBytes)
                {
                    MessageBox.Show(this, "Choose a resume smaller than 10 MB.",
                                    "File Too Large", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                string text = ext switch
                {
                    ".txt"  => File.ReadAllText(filePath),
                    ".docx" => ExtractDocxText(filePath),
                    ".pdf"  => ExtractPdfText(filePath),
                    _       => null!
                };

                if (text == null)
                {
                    MessageBox.Show(this, "Unsupported file type. Please upload a PDF, DOCX, or TXT file.",
                                    "Unsupported Format", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    MessageBox.Show(this, "No readable text was found in the file. The document may be image-based or protected.\n\nPlease paste your resume text manually.",
                                    "No Text Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (text.Length > MaxResumeTextChars)
                {
                    MessageBox.Show(this, "The extracted resume text is too large. Please upload a shorter document.",
                                    "Document Too Large", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                ResumeTextBox.Text = text;
                DebugWindow.Log("RESUME", $"Loaded {text.Length} chars from {Path.GetFileName(filePath)}");
                SaveCurrentResume();   // auto-save silently so it appears in history
                CollapseResumeForAsk();
            }
            catch (Exception ex)
            {
                DebugWindow.Log("RESUME", $"File load failed: {ex.Message}");
                MessageBox.Show(this, $"Could not load file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CollapseResumeForAsk()
        {
            ResumeTabPanel.Visibility    = Visibility.Collapsed;
            JobTabPanel.Visibility       = Visibility.Collapsed;
            ResumeLoadedBadge.Visibility = Visibility.Visible;
            ContentRow.Height            = GridLength.Auto;
            AskGuideRow.Height           = new GridLength(1, GridUnitType.Star);
            AskBox.Focus();
        }

        private void ExpandResumeContent()
        {
            ResumeLoadedBadge.Visibility = Visibility.Collapsed;
            ContentRow.Height            = new GridLength(1, GridUnitType.Star);
            AskGuideRow.Height           = new GridLength(170);
        }

        private void ResumeLoadedBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => SwitchToResumeTab();

        private static string ExtractDocxText(string filePath)
        {
            using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(filePath, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var para in body.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
            {
                string line = para.InnerText;
                if (!string.IsNullOrWhiteSpace(line))
                {
                    if (sb.Length + line.Length + Environment.NewLine.Length > MaxResumeTextChars)
                        throw new InvalidDataException("The document contains too much text.");
                    sb.AppendLine(line);
                }
            }
            return sb.ToString().Trim();
        }

        private static string ExtractPdfText(string filePath)
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(filePath);
            var sb = new System.Text.StringBuilder();
            int pageCount = 0;
            foreach (var page in doc.GetPages())
            {
                if (++pageCount > 100)
                    throw new InvalidDataException("The document has too many pages.");
                var words = page.GetWords();
                string line = string.Join(" ", words.Select(w => w.Text));
                if (!string.IsNullOrWhiteSpace(line))
                {
                    if (sb.Length + line.Length + Environment.NewLine.Length > MaxResumeTextChars)
                        throw new InvalidDataException("The document contains too much text.");
                    sb.AppendLine(line);
                }
            }
            return sb.ToString().Trim();
        }

        // ── Resume: Drag & drop ───────────────────────────────────────────────
        private void ResumeDropBorder_DragEnter(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
            {
                e.Effects = DragDropEffects.Copy;
                ResumeDragOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void ResumeDropBorder_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void ResumeDropBorder_DragLeave(object sender, System.Windows.DragEventArgs e)
        {
            ResumeDragOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        private void ResumeDropBorder_Drop(object sender, System.Windows.DragEventArgs e)
        {
            ResumeDragOverlay.Visibility = Visibility.Collapsed;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                    LoadResumeFromFile(files[0]);
            }
            else if (e.Data.GetDataPresent(DataFormats.Text))
            {
                ResumeTextBox.Text = (string)e.Data.GetData(DataFormats.Text);
            }
            e.Handled = true;
        }

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
            // Hide overlay by setting Window.Opacity (not MainBorder.Opacity, which stays set)
            if (answerWasVisible && answerWindow != null) answerWindow.Opacity = 0;

            // Give DWM time to flush the transparent frame before BitBlt
            await Task.Delay(300);

            // ── Phase 2: capture ──────────────────────────────────────────────
            byte[] imageBytes;
            try
            {
                imageBytes = await Task.Run(() => primaryOnly
                    ? ScreenAnalyzer.CapturePrimaryScreen()
                    : ScreenAnalyzer.CaptureScreen());
                DebugWindow.Log("SCREEN", $"Captured {imageBytes.Length / 1024} KB ({captureLabel})");
            }
            catch (Exception ex)
            {
                DebugWindow.Log("SCREEN_ERR", ex.Message);
                this.Opacity = savedOpacity;
                // Restore Window.Opacity to 1.0 — visual opacity stays controlled by MainBorder.Opacity
                if (answerWasVisible && answerWindow != null) answerWindow.Opacity = 1.0;
                AiAnswerBox.Text = "⚠ Screen capture failed. Press F12 for details.";
                _isScreenAnalyzing = false;
                StopThinkingUi();
                return;
            }

            // Restore windows immediately after capture
            this.Opacity = savedOpacity;
            // Restore Window.Opacity to 1.0 — visual opacity stays controlled by MainBorder.Opacity
            if (answerWasVisible && answerWindow != null) answerWindow.Opacity = 1.0;

            // Clear overlay content so user sees a clean state while analysis streams in
            if (_isCameraMode && answerWindow != null)
            {
                answerWindow.UpdateAnswer("");
                answerWindow.UpdateQuestion("Analyzing screen…");
            }

            // ── Phase 3: stream from vision AI ───────────────────────────────
            string visionLabel = SettingsWindow.IsGroq() ? "Llama 4 Scout" : "GPT-4o";
            ThinkingLabel.Text = $"🤖  {visionLabel} analyzing…";

            string resumeCtx  = ResumeParser.ExtractFacts(ResumeTextBox.Text);
            string timestamp  = DateTime.Now.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
            string header     = $"📸 SCREEN ANALYSIS  [{timestamp}]\n\n";
            string sep        = "\n" + new string('─', 45) + "\n\n";

            // Save previous answers so we can prepend the new one on top.
            // Treat placeholder/welcome messages as "empty" so they aren't carried forward.
            bool isDefaultText = string.IsNullOrWhiteSpace(AiAnswerBox.Text);
            string previousAnswers = isDefaultText ? "" : AiAnswerBox.Text;

            var sb = new StringBuilder();
            int tokenCount = 0;

            try
            {
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
            }
            finally
            {
                _isScreenAnalyzing = false;
                StopThinkingUi();
            }
        }

        private void ResumeToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            _resumeCollapsed = !_resumeCollapsed;
            ResumePanel.Visibility = _resumeCollapsed ? Visibility.Collapsed : Visibility.Visible;
            ResumeColumn.Width = _resumeCollapsed ? new GridLength(0) : new GridLength(260);
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

            // Unsubscribe camera events and close overlay window
            if (answerWindow != null && _cameraModeClosedHandler != null)
                answerWindow.CameraModeClosedByUser -= _cameraModeClosedHandler;
            try { answerWindow?.Close(); } catch { }
            answerWindow = null;

            _globalHotkey?.Dispose();
            _debugWindow?.ForceClose();
            bool hadActiveRecording = isRecording;
            string recordingId = _recordingSessionId;
            try { File.WriteAllText(Path.Combine(AppDataFolder, "shutdown.flag"), "1"); } catch { }
            EndSession();
            PresenceTracker.Stop();

            if (hadActiveRecording && WaitForRecordingSaveAsync(recordingId, RecordingSaveTimeoutMs).GetAwaiter().GetResult())
                ProtectRecording(sessionNumber);
            Interlocked.Increment(ref _engineStartGeneration);
            _engineCts.Cancel();
            _engineCts.Dispose();
            KillAndDisposeEngine();

            base.OnClosed(e);
        }

        private async Task<bool> WaitForRecordingSaveAsync(string recordingId, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(recordingId) || speechmaticsProcess == null) return true;

            string path = RecordingSavedPath(recordingId);
            var started = Stopwatch.StartNew();
            while (started.ElapsedMilliseconds < timeoutMs)
            {
                if (File.Exists(path)) return true;
                try
                {
                    if (speechmaticsProcess.HasExited) return false;
                }
                catch
                {
                    return false;
                }
                await Task.Delay(50).ConfigureAwait(false);
            }

            DebugWindow.Log("SESSION", "Timed out waiting for recording save.");
            return false;
        }

        private void ProtectRecording(int completedSessionNumber)
        {
            string rawPath = Path.Combine(AppDataFolder, $"interview_{completedSessionNumber}.wav");
            if (!File.Exists(rawPath)) return;

            string protectedPath = rawPath + ".dpapi";
            if (!SecureDataProtector.TryProtectBinaryFile(rawPath, protectedPath))
                DebugWindow.Log("SESSION", "Could not protect the completed audio recording; the raw file was retained for recovery.");
        }

        private void SecurePendingAudioRecordings()
        {
            try
            {
                if (!Directory.Exists(AppDataFolder)) return;
                foreach (string rawPath in Directory.EnumerateFiles(AppDataFolder, "interview_*.wav"))
                {
                    if (!SecureDataProtector.TryProtectBinaryFile(rawPath, rawPath + ".dpapi"))
                        DebugWindow.Log("SESSION", $"Could not protect recovered recording: {Path.GetFileName(rawPath)}");
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log("SESSION", $"Audio recovery cleanup failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // TAB SWITCHING
        // ══════════════════════════════════════════════════════════════════════
        private void SwitchToResumeTab()
        {
            ExpandResumeContent();
            ResumeTabPanel.Visibility = Visibility.Visible;
            JobTabPanel.Visibility    = Visibility.Collapsed;
            ResumeTabBorder.Background  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0D1B2E"));
            ResumeTabBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1e3a5f"));
            ResumeTabLabel.Foreground   = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));
            JobTabBorder.Background   = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#09111e"));
            JobTabBorder.BorderBrush  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#161f30"));
            JobTabLabel.Foreground    = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4b6070"));
            SavedResumesBtn.Visibility = Visibility.Visible;
            ResumeUploadBtn.Visibility = Visibility.Visible;
        }

        private void SwitchToJobTab()
        {
            ExpandResumeContent();
            ResumeTabPanel.Visibility = Visibility.Collapsed;
            JobTabPanel.Visibility    = Visibility.Visible;
            JobTabBorder.Background   = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0D1B2E"));
            JobTabBorder.BorderBrush  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1e3a5f"));
            JobTabLabel.Foreground    = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));
            ResumeTabBorder.Background  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#09111e"));
            ResumeTabBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#161f30"));
            ResumeTabLabel.Foreground   = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4b6070"));
            SavedResumesBtn.Visibility = Visibility.Collapsed;
            ResumeUploadBtn.Visibility = Visibility.Collapsed;
        }

        private void ResumeTabBtn_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => SwitchToResumeTab();
        private void JobTabBtn_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)    => SwitchToJobTab();

        // ══════════════════════════════════════════════════════════════════════
        // TARGET ROLE (company + job description)
        // ══════════════════════════════════════════════════════════════════════
        private void CompanyNameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            _companyName = CompanyNameBox.Text;
            if (CompanyNameWatermark != null)
                CompanyNameWatermark.Visibility = string.IsNullOrWhiteSpace(_companyName)
                    ? Visibility.Visible : Visibility.Collapsed;
            ScheduleJobContextSave();
        }

        private void JobDescBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            _jobDescription = JobDescBox.Text;
            if (JobDescWatermark != null)
                JobDescWatermark.Visibility = string.IsNullOrWhiteSpace(_jobDescription)
                    ? Visibility.Visible : Visibility.Collapsed;
            ScheduleJobContextSave();
        }

        private void ScheduleJobContextSave()
        {
            _jobContextSaveTimer?.Stop();
            if (_jobContextSaveTimer == null)
            {
                _jobContextSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _jobContextSaveTimer.Tick += (s, _) => { _jobContextSaveTimer.Stop(); SaveJobContext(); };
            }
            _jobContextSaveTimer.Start();
        }

        // ══════════════════════════════════════════════════════════════════════
        // ASK or GUIDE
        // ══════════════════════════════════════════════════════════════════════
        private void AskBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (AskBoxWatermark != null)
                AskBoxWatermark.Visibility = string.IsNullOrWhiteSpace(AskBox.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AskBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter &&
                (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control)
                 == System.Windows.Input.ModifierKeys.Control)
            {
                e.Handled = true;
                _ = AskAiAsync(AskBox.Text);
            }
        }

        private void PinBtn_Click(object sender, RoutedEventArgs e)
        {
            string text = AskBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;
            _liveHints = string.IsNullOrWhiteSpace(_liveHints)
                ? text
                : _liveHints + "\n" + text;
            AskBox.Clear();
            UpdatePinnedHintsDisplay();
            SaveHints();
        }

        private void ClearHintsBtn_Click(object sender, RoutedEventArgs e)
        {
            _liveHints = "";
            UpdatePinnedHintsDisplay();
            SaveHints();
        }

        private void AskSubmitBtn_Click(object sender, RoutedEventArgs e)
        {
            string question = AskBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(question)) return;
            _ = AskAiAsync(question);
        }

        private void UpdatePinnedHintsDisplay()
        {
            if (PinnedHintsBar == null || PinnedHintsText == null) return;
            if (string.IsNullOrWhiteSpace(_liveHints))
            {
                PinnedHintsBar.Visibility = Visibility.Collapsed;
            }
            else
            {
                PinnedHintsText.Text = _liveHints;
                PinnedHintsBar.Visibility = Visibility.Visible;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // SAVED RESUMES
        // ══════════════════════════════════════════════════════════════════════
        private void SavedResumesBtn_Click(object sender, RoutedEventArgs e)
        {
            if (SavedResumesPopup.IsOpen) { SavedResumesPopup.IsOpen = false; return; }
            SavedResumesPopup.PlacementTarget = SavedResumesBtn;
            PopulateSavedResumesPopup();
            SavedResumesPopup.IsOpen = true;
        }

        private void PopulateSavedResumesPopup()
        {
            SavedResumesListPanel.Children.Clear();
            bool hasSaved = _savedResumes.Count > 0;

            foreach (var (name, content) in _savedResumes)
            {
                string cnt = content;
                var row = new System.Windows.Controls.Border
                {
                    Padding     = new Thickness(14, 9, 14, 9),
                    Cursor      = System.Windows.Input.Cursors.Hand,
                    Background  = System.Windows.Media.Brushes.Transparent,
                };
                var sp = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                sp.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text       = "",
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize   = 12,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4b6070")),
                    Margin     = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                sp.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text       = name,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#cbd5e1")),
                    FontSize   = 12,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Child = sp;
                row.MouseEnter += (s, _) => ((System.Windows.Controls.Border)s).Background =
                    new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1e2a3a"));
                row.MouseLeave += (s, _) => ((System.Windows.Controls.Border)s).Background =
                    System.Windows.Media.Brushes.Transparent;
                row.MouseLeftButtonDown += (_, _2) =>
                {
                    LoadSavedResume(cnt);
                    SavedResumesPopup.IsOpen = false;
                };
                SavedResumesListPanel.Children.Add(row);
            }

            ClearSeparator.Visibility       = hasSaved ? Visibility.Visible   : Visibility.Collapsed;
            ClearResumesPopupItem.Visibility = hasSaved ? Visibility.Visible   : Visibility.Collapsed;
        }

        private void SaveResumePopupItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SaveCurrentResume();
            SavedResumesPopup.IsOpen = false;
        }

        private void ClearResumesPopupItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _savedResumes.Clear();
            PersistSavedResumes();
            UpdateSavedResumesButton();
            SavedResumesPopup.IsOpen = false;
        }

        private void SaveCurrentResume()
        {
            string content = ResumeTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                MessageBox.Show(this, "No resume text to save.", "Save Resume", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string name = "Resume · " + DateTime.Now.ToString("MMM d, HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            _savedResumes.Insert(0, (name, content));
            if (_savedResumes.Count > 10) _savedResumes.RemoveAt(_savedResumes.Count - 1);
            PersistSavedResumes();
            UpdateSavedResumesButton();
        }

        private void LoadSavedResume(string content) => ResumeTextBox.Text = content;

        private void UpdateSavedResumesButton()
        {
            if (SavedResumesBtn == null) return;
            SavedResumesBtn.Visibility = _savedResumes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ══════════════════════════════════════════════════════════════════════
        // RESUME LOCK / UNLOCK
        // ══════════════════════════════════════════════════════════════════════
        private void LockResume()
        {
            if (ResumeLockOverlay != null) ResumeLockOverlay.Visibility = Visibility.Visible;
        }

        private void UnlockResume()
        {
            if (ResumeLockOverlay != null) ResumeLockOverlay.Visibility = Visibility.Collapsed;
        }

        // ══════════════════════════════════════════════════════════════════════
        // PERSISTENCE  (hints · job context · saved resumes)
        // ══════════════════════════════════════════════════════════════════════
        private void LoadHints()
        {
            // Hints are intentionally not restored between sessions — always start fresh.
            _liveHints = "";
            UpdatePinnedHintsDisplay();
        }

        private void SaveHints()
        {
            try { WriteProtectedLocalText(Path.Combine(AppDataFolder, "hints.txt"), _liveHints); }
            catch { }
        }

        private void LoadJobContext()
        {
            try
            {
                string path = Path.Combine(AppDataFolder, "jobcontext.json");
                if (!File.Exists(path)) return;
                var doc = JsonSerializer.Deserialize<System.Text.Json.JsonElement>(ReadProtectedLocalText(path));
                _companyName    = doc.TryGetProperty("company", out var c) ? c.GetString() ?? "" : "";
                _jobDescription = doc.TryGetProperty("job",     out var j) ? j.GetString() ?? "" : "";

                CompanyNameBox.Text = _companyName;
                JobDescBox.Text     = _jobDescription;
                if (CompanyNameWatermark != null)
                    CompanyNameWatermark.Visibility = string.IsNullOrWhiteSpace(_companyName)
                        ? Visibility.Visible : Visibility.Collapsed;
                if (JobDescWatermark != null)
                    JobDescWatermark.Visibility = string.IsNullOrWhiteSpace(_jobDescription)
                        ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        private void SaveJobContext()
        {
            try
            {
                var obj  = new { company = _companyName, job = _jobDescription };
                string json = JsonSerializer.Serialize(obj);
                string path = Path.Combine(AppDataFolder, "jobcontext.json");
                WriteProtectedLocalText(path, json);
            }
            catch { }
        }

        private void LoadSavedResumes()
        {
            try
            {
                string path = Path.Combine(AppDataFolder, "savedresumes.json");
                if (!File.Exists(path)) return;
                var raw = JsonSerializer.Deserialize<List<SavedResumeEntry>>(ReadProtectedLocalText(path));
                if (raw == null) return;
                _savedResumes = raw.ConvertAll(r => (r.Name, r.Content));
            }
            catch { }
        }

        private void PersistSavedResumes()
        {
            try
            {
                var list = _savedResumes.ConvertAll(r => new SavedResumeEntry { Name = r.Name, Content = r.Content });
                string path = Path.Combine(AppDataFolder, "savedresumes.json");
                WriteProtectedLocalText(path, JsonSerializer.Serialize(list));
            }
            catch { }
        }

        private static string ReadProtectedLocalText(string path)
        {
            string raw = File.ReadAllText(path);
            if (SecureDataProtector.IsProtected(raw))
            {
                if (!SecureDataProtector.TryUnprotect(raw, out string value))
                    throw new InvalidDataException("Could not decrypt local data.");
                return value;
            }

            WriteProtectedLocalText(path, raw);
            return raw;
        }

        private static void WriteProtectedLocalText(string path, string value)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, SecureDataProtector.Protect(value));
            File.Move(tmp, path, overwrite: true);
        }

        private class SavedResumeEntry
        {
            public string Name    { get; set; } = "";
            public string Content { get; set; } = "";
        }
    }
}
