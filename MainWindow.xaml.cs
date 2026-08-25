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
        private const int    TranscriptPollMs        = 40;    // how often to read latest.txt
        private const int    ThinkingAnimMs          = 800;   // thinking dot animation interval
        private const int    CreditRefreshMinutes    = 5;     // background credits refresh
        private const int    EngineMonitorSecs       = 3;     // how often to check engine health
        private const int    CreditsLowThreshold     = 20;    // amber warning below this
        private const int    CreditsCriticalThreshold= 5;     // red warning / block below this
        private const int    TranscriptRetryCount    = 5;     // retries on torn file read
        private const int    TranscriptRetryDelayMs  = 5;     // delay between retries
        // How long a pause has to last before Auto decides the interviewer has
        // finished. This is dead time on top of the 0.7s the recogniser already
        // holds words for, and it is the largest remaining delay between a
        // question ending and an answer starting, since the model itself replies
        // in about 0.15s.
        //
        // The two cases deserve different patience. A transcript ending in "?"
        // or "." means the recogniser itself decided the sentence closed, which
        // is a strong signal on its own and does not need most of a second of
        // corroboration; 650ms was spending that on a question already known to
        // be over. A pause with no punctuation is genuinely ambiguous, because
        // people stop mid-sentence to think, so that one stays close to a full
        // second on purpose.
        //
        // Firing slightly early is recoverable: the question is deduplicated, and
        // Space interrupts an answer instantly. Firing late is not, because the
        // candidate has already been silent in front of someone.
        private const int    AutoTurnFinishedSilenceMs = 300;   // punctuated, lands on a real word
        private const int    AutoTurnNaturalSilenceMs  = 820;   // could still be a pause
        private const int    AutoTurnMaxSilenceMs      = 1_250; // never wait longer than this
        private const int    AutoTurnMinimumSpeechMs = 500;   // reject clicks/noise bursts
        private const int    AutoTurnMinimumChars    = 4;     // reject empty or tiny fragments
        private const int    RecordingSaveTimeoutMs  = 10_000;
        // Shorter on exit: the app must still close promptly, and anything left
        // unprotected is secured by SecurePendingAudioRecordings on next launch.
        private const int    ShutdownRecordingSaveTimeoutMs = 3_000;
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
        private const double ResumePanelExpandedWidth = 260;
        private bool _isCameraMode = false;
        private bool _stealthMode = SettingsWindow.GetStealthMode();
        // Load the persisted mic choice so it survives restarts (-1 = Windows default).
        private int _audioDeviceId = SettingsWindow.GetAudioDeviceIndex();
        private bool _justStartedListening = false;  // suppress stale reads for 400ms after unmute
        private int  _listenStartTicks = 0;
        // Cancels the in-flight AI answer (and the transcript-flush grace window) so
        // pressing Space mid-answer or mid-flush interrupts instantly and re-listens.
        private System.Threading.CancellationTokenSource? _aiCts;
        // True during the brief "flushing final transcript" window after you stop talking,
        // before the AI fires. A Space press here must re-listen in ONE press, not be
        // swallowed, so HandleSpacePress treats this exactly like the answering state.
        private volatile bool _flushing = false;

        // ── Feature state ────────────────────────────────────────────────────
        private string _liveHints        = "";
        private string _companyName      = "";
        private string _jobDescription   = "";
        private List<(string Name, string Content)> _savedResumes = new();
        private bool _answerIsBehavioral = false;
        private enum ListeningMode
        {
            Manual,
            InterviewAuto,
            PracticeAuto
        }

        private ListeningMode _listeningMode = ListeningMode.Manual;
        private bool AutoModeEnabled => _listeningMode != ListeningMode.Manual;
        private bool _autoTurnSubmitting;
        private string _autoLastTranscript = "";
        private string _lastAutoRejectedTranscript = "";
        private string _lastAutoSubmittedQuestion = "";
        private DateTime _lastAutoSubmitUtc = DateTime.MinValue;
        private DateTime _autoTranscriptChangedUtc = DateTime.MinValue;

        // The question already asked, waiting to be joined to the tail still
        // arriving. Empty except between recognising a continuation and sending it.
        private string _autoContinuationPrefix = "";

        // The tail last joined on, so the same one cannot be joined on twice.
        private string _lastAutoSubmittedFragment = "";

        // The longest gap this speaker has left in the MIDDLE of the current turn.
        // Used to tell a slow talker's pause from the end of their question.
        private double _autoLongestMidTurnGapMs = 0;
        private DateTime _autoListeningStartedUtc = DateTime.MinValue;

        private DispatcherTimer? transcriptTimer;
        private DispatcherTimer? thinkingTimer;
        private DispatcherTimer? creditsRefreshTimer;
        private DispatcherTimer? warmupTimer;
        private DispatcherTimer? _sessionTimer;
        private DispatcherTimer? _jobContextSaveTimer;
        private DispatcherTimer? _autoModeNoticeTimer;
        private readonly SemaphoreSlim _creditsFetchGate = new(1, 1);
        private DateTime _lastCreditsFetchUtc = DateTime.MinValue;
        private CreditsWindow? _creditsWindow;
        private int _sessionSeconds = 0;
        private int thinkingStep = 0;

        private Process? speechmaticsProcess;
        private CancellationTokenSource _engineCts = new CancellationTokenSource();
        private int _engineStartGeneration;
        private bool _engineStarting;
        private bool _engineRecoveryInProgress;
        private bool _engineTokenRefreshAttempted;
        private int _engineRestartCount;
        private DateTime _nextEngineRestartUtc = DateTime.MinValue;
        // True once the Python engine reports "STATUS: ONLINE" (Speechmatics session
        // ready). Until then the transcriber can't hear anything, so the mic pill shows
        // CONNECTING and we tell the user not to speak yet.
        private volatile bool _engineOnline = false;
        private string projectRoot = "";
        private string scriptFolder = "";
        private AnswerWindow? answerWindow;
        private Action? _cameraModeClosedHandler;   // stored so we can -= it in OnClosed
        private bool _guestTransitionInProgress;

        private sealed class BackendRequestException : Exception
        {
            public BackendRequestException(string message) : base(message) { }
        }

        private int sessionNumber = 1;
        private string sessionLogPath = "";
        private string _recordingSessionId = "";

        private GlobalHotkey? _globalHotkey;
        private DebugWindow? _debugWindow;
        private DispatcherTimer? _engineMonitorTimer;

        // HTTP — shared singletons defined in SharedHttpClient.cs
        private static HttpClient _backendClient => SharedHttpClient.Http;
        private static HttpClient _creditsClient => SharedHttpClient.HttpShort;

        // Cached once — Directory.CreateDirectory on every access was a redundant syscall per tick
        private readonly string AppDataFolder = InitAppDataFolder();
        private static string InitAppDataFolder()
        {
            var p = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "InterviewCopilot");
            Directory.CreateDirectory(p);
            return p;
        }

        public MainWindow()
        {
            InitializeComponent();
            projectRoot = AppDomain.CurrentDomain.BaseDirectory;
            scriptFolder = FindScriptFolder(projectRoot);

            // Bring the transcription engine up before this window is shown. A user
            // can press Space as soon as the UI appears, so waiting for Loaded leaves
            // the first words exposed to the key/Python/WebSocket startup race.
            isMuted = true;
            isListening = false;
            WritePauseFlag();
            NuclearKillOldProcesses();
            _ = ResolvePythonExecutableAsync();

            this.PreviewKeyDown += Window_PreviewKeyDown;
            this.PreviewKeyUp   += Window_PreviewKeyUp;

            // Close floating popups the moment the app loses focus. These popups are
            // StaysOpen=True (so an in-app click elsewhere doesn't dismiss them), but that
            // also meant switching to another window (e.g. Claude) left them hovering on
            // top of it. Deactivated fires when the whole app loses activation — dismiss
            // them there so nothing bleeds over other apps.
            this.Deactivated += (_, __) => CloseFloatingPopups();

            // Position top-center on the primary screen
            var workArea = SystemParameters.WorkArea;
            this.Left = workArea.Left + (workArea.Width - this.Width) / 2;
            this.Top  = workArea.Top + 12;

            this.Loaded += async (s, e) =>
            {
                // Report presence to Firestore so the admin dashboard shows
                // accurate Live/last-seen (self-guards while logged out)
                PresenceTracker.Start();

                // Keep track of the window the user is actually working in, so
                // pressing Analyze on our own window still reads their work
                // rather than falling back to a picture of the whole desktop.
                ScreenAnalyzer.StartTrackingActiveWindow();

                try
                {
                    try { WindowStealth.SetStealthMode(this, _stealthMode); } catch (Exception ex) { DebugWindow.Log("STEALTH", ex.Message); }
                    UpdateStealthBtn();

                    answerWindow = new AnswerWindow();
                    answerWindow.ShowInTaskbar = false;
                    // Store the delegate so we can -= it in OnClosed (prevents memory leak)
                    _cameraModeClosedHandler = () => Dispatcher.Invoke(() => ExitCameraMode());
                    answerWindow.CameraModeClosedByUser += _cameraModeClosedHandler;
                    // Reads the screen once, the same as F8 and the same as the
                    // READ SCREEN pill in the main toolbar.
                    //
                    // This used to toggle Watch Screen, which is the setting that
                    // moved into Settings precisely because it is not something
                    // anyone changes mid-interview. Compact was left carrying the
                    // old control, so the slim bar — the thing on screen during an
                    // in-person interview, where there is no main window to fall
                    // back on — had no way to actually read the screen. Pressing
                    // it silently changed a preference instead.
                    answerWindow.AnalyzeRequested += () => Dispatcher.Invoke(() =>
                    {
                        _ = HandleScreenAnalysisAsync().ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                                Dispatcher.Invoke(() =>
                                {
                                    DebugWindow.Log("SCREEN_ERR",
                                        t.Exception?.GetBaseException().Message ?? "unknown");
                                    StopThinkingUi();
                                });
                        }, TaskScheduler.Default);
                    });

                    _debugWindow = new DebugWindow();

                    SecurePendingAudioRecordings();
                    SweepStaleRecordingFlags();
                    _ = NotifyIfUpdateAvailableAsync();
                    UpdateMicUi();
                    SavePathLabel.Text = AppDataFolder;
                    LoadHints(); LoadJobContext(); LoadSavedResumes();
                    UpdateSavedResumesButton();
                    RestoreLastResume();
                    // Expand the resume panel WITHOUT focusing the resume text box.
                    // SwitchToResumeTab() would call ResumeTextBox.Focus(), which left a
                    // text field holding keyboard focus on startup — and the global Space
                    // toggle bails out via IsTypingInTextField() whenever a text field is
                    // focused. That's exactly why Space did nothing until the user clicked
                    // somewhere else in the app first (which moved focus off the text box).
                    ExpandResumeContent();
                    ApplyMainWindowOpacity();

                    IntPtr mainHwnd = new WindowInteropHelper(this).Handle;

                    // Fix "first click only activates the window, doesn't reach the button"
                    // — a well-known Windows quirk for borderless layered windows
                    // (WindowStyle=None + AllowsTransparency=True, exactly this window).
                    // Without this hook, WM_MOUSEACTIVATE consumes the very first click
                    // purely to bring the window forward, and it never reaches whatever
                    // control (mic button, Screen AI, etc.) was actually clicked — forcing
                    // the user to click once to focus, then again to do anything.
                    try
                    {
                        HwndSource.FromHwnd(mainHwnd)?.AddHook(WndProcActivateFix);
                    }
                    catch (Exception ex) { DebugWindow.Log("FOCUS_FIX", $"Mouse-activate hook failed: {ex.Message}"); }

                    try
                    {
                        _globalHotkey = new GlobalHotkey(
                            onSpacePressed:                 () => Dispatcher.BeginInvoke(() => HandleSpacePress("GLOBAL")),
                            onSpaceReleased:                null,
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
                            }),
                            onRegionAnalysisPressed:        () => _ = Dispatcher.InvokeAsync(async () =>
                            {
                                try { await HandleRegionScreenAnalysisAsync(); }
                                catch (Exception ex) { DebugWindow.Log("SCREEN_ERR", ex.Message); StopThinkingUi(); }
                            })
                        );
                        _globalHotkey.OwnerWindowHandle = mainHwnd;
                        // Cleanup is deliberately left to OnClosed rather than an
                        // app-lifetime event. A DispatcherUnhandledException handler
                        // would fire for recoverable errors and kill the Space hotkey
                        // mid-session, and an AppDomain.ProcessExit handler would pin
                        // this window in memory for the life of the process.
                        // OnClosed disposes it, Dispose() is idempotent, and Windows
                        // releases a WH_KEYBOARD_LL hook itself if the process dies.
                    }
                    catch (Exception ex) { DebugWindow.Log("HOTKEY_ERR", $"Global hotkey registration failed: {ex.Message}"); }

                    _engineMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(EngineMonitorSecs) };
                    _engineMonitorTimer.Tick += (s2, e2) => MonitorEngine();

                    // ── Session restore with silent token refresh ─────────────────
                    // TryLoadFromDisk() returns false when the saved idToken is > 55 min old,
                    // but it still loads RefreshToken into memory. TryRefreshAsync() uses that
                    // refresh token to get a new idToken from Firebase silently — the user
                    // never sees a login screen unless the refresh token itself is invalid.
                    bool sessionRestored = UserSession.TryLoadFromDisk();
                    if (!sessionRestored && !string.IsNullOrEmpty(UserSession.RefreshToken))
                    {
                        DebugWindow.Log("AUTH", "idToken expired — attempting silent refresh...");
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
                        await InitializeSpeechPipelineAsync();
                        await StartNewSessionAsync(); // Auto-start recording immediately on app open
                    }
                    else
                    {
                        SetLoggedOutUI();
                        // Fetch real guest credits from backend using device ID.
                        // The backend tracks this device's monthly 100-credit allowance.
                        Task guestCreditsTask = FetchAndDisplayCreditsAsync();
                        // Fetch SM key for guest — backend identifies device via X-Device-Id.
                        await Task.WhenAll(guestCreditsTask, InitializeSpeechPipelineAsync());
                        await StartNewSessionAsync();
                    }

                    _engineMonitorTimer.Start();

                    creditsRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(CreditRefreshMinutes) };
                    creditsRefreshTimer.Tick += async (s2, e2) =>
                    {
                        try { await FetchAndDisplayCreditsAsync(); }
                        catch (Exception ex) { DebugWindow.Log("CREDITS_TIMER", ex.Message); }
                    };
                    creditsRefreshTimer.Start();

                    // KEEP-WARM: the answer backend spins its container down when idle, so the
                    // first question after a quiet gap pays a 2-3s cold start ("thinking so long
                    // sometimes"). Ping it every 75s — well under the idle timeout — so the
                    // container stays hot and first-token stays at its ~0.7s warm number.
                    _ = WarmBackendAsync();
                    warmupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(75) };
                    warmupTimer.Tick += (s2, e2) => _ = WarmBackendAsync();
                    warmupTimer.Start();

                    // Defense-in-depth for the "Space does nothing until I click in the app"
                    // bug: after ALL startup work settles, if keyboard focus still landed in
                    // one of the text fields, move it to the window itself so the global Space
                    // toggle isn't swallowed by IsTypingInTextField(). Runs at Input priority
                    // so it fires after any focus changes queued during load.
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (IsTypingInTextField())
                            {
                                this.Focusable = true;
                                System.Windows.Input.Keyboard.Focus(this);
                            }
                        }
                        catch (Exception ex) { DebugWindow.Log("FOCUS_RESET", ex.Message); }
                    }), System.Windows.Threading.DispatcherPriority.Input);

                    // Extend stealth (screen-capture exclusion) to the popups — they're
                    // separate windows the main-window stealth doesn't reach.
                    HookPopupStealth(ProfileDropdownPopup);
                    HookPopupStealth(SavedResumesPopup);
                    HookPopupStealth(ListeningModePopup);
                    UpdateListeningModeUi();

                    // On by default, and remembered. It used to start off every
                    // time, so the feature most likely to matter in a coding
                    // round was the one somebody had to remember to switch on
                    // with an interviewer already speaking.
                    _watchScreenMode = SettingsWindow.GetWatchScreenEnabled();
                    if (_watchScreenMode) StartPreparedShots();
                    UpdateWatchScreenUi();

                    // First launch (no seen-flag yet): show the onboarding so new users
                    // immediately understand the flow, resume/company setup and stealth.
                    if (!File.Exists(OnboardingSeenPath)) ShowOnboarding();
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
                    await InitializeSpeechPipelineAsync();
                    if (isRecording) EndSession();
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

        // Popups are their own top-level windows, so the main window's stealth doesn't cover
        // them — apply capture-exclusion each time one opens (matching current stealth state)
        // so dropdowns never flash into a screen recording during an interview.
        private void HookPopupStealth(System.Windows.Controls.Primitives.Popup popup)
        {
            if (popup == null) return;
            popup.Opened += (s, e) =>
            {
                try
                {
                    if (popup.Child != null &&
                        PresentationSource.FromVisual(popup.Child) is HwndSource src)
                        WindowStealth.SetCaptureExclusion(src.Handle, _stealthMode);
                }
                catch (Exception ex) { DebugWindow.Log("STEALTH", $"popup stealth failed: {ex.Message}"); }
            };
        }

        // Dismiss every floating popup — called when the app loses focus so none of them
        // stay painted on top of whatever window the user switched to.
        private void CloseFloatingPopups()
        {
            try
            {
                if (SavedResumesPopup   != null) SavedResumesPopup.IsOpen   = false;
                if (ProfileDropdownPopup != null) ProfileDropdownPopup.IsOpen = false;
                if (ListeningModePopup  != null) ListeningModePopup.IsOpen  = false;
            }
            catch { }
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

            if (ListeningModePopup.IsOpen)
            {
                var modeSource = e.OriginalSource as DependencyObject;
                var modePopupChild = ListeningModePopup.Child as FrameworkElement;
                bool insideModePopup = modePopupChild != null && modeSource != null &&
                                       IsDescendantOf(modeSource, modePopupChild);
                bool onModePill = modeSource != null && IsDescendantOf(modeSource, AutoModePill);
                if (!insideModePopup && !onModePill) ListeningModePopup.IsOpen = false;
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
            string status   = loggedIn ? (UserSession.Email ?? "") : "Free trial (guest)";

            PopupAvatarInitials.Text  = initials;
            PopupProfileName.Text     = name;
            PopupProfileStatus.Text   = status;
            ApplyAvatarPhoto();

            // Credits card — always use real backend value (guests get 100/month by device ID)
            if (loggedIn && UserSession.IsUnlimited)
            {
                PopupCreditsAmount.Text = "Unlimited";
                PopupCreditsAmount.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
                PopupCreditsPlan.Text = $"{UserSession.Plan} plan";
                PopupSignInCardBtn.Visibility = Visibility.Collapsed;
            }
            else if (!_creditsFetched)
            {
                // Backend hasn't responded yet — show loading state and trigger a fresh fetch.
                // When the fetch completes it will re-invoke UpdateProfileDropdown if the popup is still open.
                PopupCreditsAmount.Text = "Loading...";
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
                string color = credits > 5 ? "#FFFFFF" : "#F87171";
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
            ApplyGlassOpacity(opacity);
            // Persist the shared preference for the main and eye-mode windows.
            var cfg = SettingsWindow.LoadConfig();
            cfg.MainWindowOpacity = Math.Round(opacity, 2);
            cfg.OverlayOpacity    = cfg.MainWindowOpacity;
            SettingsWindow.SaveConfig(cfg);

            // Every other window that happens to be open follows the slider too.
            // They each read this setting when they open, so without this a
            // window already on screen keeps the old glass while the main window
            // changes underneath it — the two sitting side by side at different
            // transparencies is more obviously wrong than either value alone.
            Glass.ApplyToOpenWindows(opacity);
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

        private async void PopupSignOut_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ProfileDropdownPopup.IsOpen = false;
            UserSession.Clear();
            _creditsFetched = false;  // reset so the popup shows Loading... then real guest credits
            SetLoggedOutUI();
            await Task.WhenAll(FetchAndDisplayCreditsAsync(), InitializeSpeechPipelineAsync());
            await StartNewSessionAsync();
            DebugWindow.Log("AUTH", "Signed out");
        }

        private async Task SwitchToGuestSessionAsync()
        {
            if (_guestTransitionInProgress) return;
            _guestTransitionInProgress = true;
            try
            {
                _creditsFetched = false;
                SetLoggedOutUI();
                await Task.WhenAll(FetchAndDisplayCreditsAsync(), InitializeSpeechPipelineAsync());
                await StartNewSessionAsync();
            }
            catch (Exception ex)
            {
                DebugWindow.Log("GUEST", $"Could not restore guest mode: {ex.Message}");
            }
            finally
            {
                _guestTransitionInProgress = false;
            }
        }

        private void UpdateProfileUI()
        {
            // Header profile badge — always visible
            ProfileBadge.Visibility    = Visibility.Visible;
            SignInHeaderBtn.Visibility = Visibility.Collapsed;

            string initials = UserSession.IsLoggedIn ? (UserSession.Initials ?? "?") : "GU";
            string firstName = UserSession.IsLoggedIn
                ? (UserSession.Name?.Split(' ')[0] ?? UserSession.Email ?? "")
                : "";
            AvatarInitials.Text    = initials;
            ProfileNameLabel.Text  = firstName;
            AvatarInitials.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#FFFFFF"));
            ApplyAvatarPhoto();
        }

        // ── Google/account profile photo ──────────────────────────────────────
        private ImageBrush? _avatarBrush;
        private string      _avatarBrushUrl = "";

        private void ApplyAvatarPhoto()
        {
            string url = UserSession.IsLoggedIn ? (UserSession.PhotoUrl ?? "") : "";
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                HideAvatarPhoto();
                return;
            }
            try
            {
                if (_avatarBrush == null || _avatarBrushUrl != url)
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                    bmp.UriSource = uri;
                    bmp.EndInit();
                    _avatarBrush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                    _avatarBrushUrl = url;
                }
                if (AvatarPhoto != null)        { AvatarPhoto.Fill = _avatarBrush;      AvatarPhoto.Visibility = Visibility.Visible; }
                if (PopupAvatarPhoto != null)   { PopupAvatarPhoto.Fill = _avatarBrush; PopupAvatarPhoto.Visibility = Visibility.Visible; }
                if (AvatarInitials != null)      AvatarInitials.Visibility = Visibility.Hidden;
                if (PopupAvatarInitials != null) PopupAvatarInitials.Visibility = Visibility.Hidden;
            }
            catch (Exception ex)
            {
                DebugWindow.Log("AVATAR", $"photo load failed: {ex.Message}");
                HideAvatarPhoto();
            }
        }

        private void HideAvatarPhoto()
        {
            if (AvatarPhoto != null)         AvatarPhoto.Visibility = Visibility.Collapsed;
            if (PopupAvatarPhoto != null)    PopupAvatarPhoto.Visibility = Visibility.Collapsed;
            if (AvatarInitials != null)      AvatarInitials.Visibility = Visibility.Visible;
            if (PopupAvatarInitials != null) PopupAvatarInitials.Visibility = Visibility.Visible;
        }

        private void SetLoggedOutUI()
        {
            // Header — show guest profile badge (no separate Sign In button)
            ProfileBadge.Visibility    = Visibility.Visible;
            SignInHeaderBtn.Visibility = Visibility.Collapsed;
            AvatarInitials.Text        = "GU";
            ProfileNameLabel.Text      = "";
            AvatarInitials.Foreground  = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#FFFFFF"));
            HideAvatarPhoto();

            // Credits badge — show loading state; real value fetched from backend via device ID
            CreditsLabel.Text           = "⚡ ···";
            CreditsPlanLabel.Visibility = Visibility.Collapsed;
            CreditsIcon.Text            = "";
            CreditsLabel.Foreground     = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#FFFFFF"));
            SetCreditsBadgeStyle("#0f2a1a", "#1a6b3a");

            UserSession.IsGuestSession = true;

            if (isRecording) EndSession();
        }

        // ══════════════════════════════════════════════════════════════════════
        // CREDITS
        // ══════════════════════════════════════════════════════════════════════
        private async Task FetchAndDisplayCreditsAsync(bool force = false)
        {
            await _creditsFetchGate.WaitAsync();
            try
            {
                if (!force && _creditsFetched &&
                    DateTime.UtcNow - _lastCreditsFetchUtc < TimeSpan.FromSeconds(2))
                    return;

            // Refresh token for signed-in users only; guests have no token to refresh.
            if (UserSession.IsLoggedIn)
                await UserSession.TryRefreshAsync();

            await FetchListeningTimeAsync();

            void CLog(string msg) => DebugWindow.Log("CREDITS", msg);

            CLog($"Fetching... guest={UserSession.IsGuestSession}");

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

                using var res = await _creditsClient.SendAsync(req);
                string body = await res.Content.ReadAsStringAsync();
                CLog($"HTTP {(int)res.StatusCode} body={body[..Math.Min(body.Length, 200)]}");

                if (!res.IsSuccessStatusCode)
                {
                    Dispatcher.Invoke(() => { CreditsLabel.Text = "⚡"; CreditsPlanLabel.Visibility = Visibility.Collapsed; });
                    return;
                }

                using var doc = JsonDocument.Parse(body);
                int credits = doc.RootElement.TryGetProperty("credits", out var c) ? c.GetInt32() : 0;
                string plan = doc.RootElement.TryGetProperty("plan", out var p) ? p.GetString() ?? "free" : "free";
                bool isUnlimited = doc.RootElement.TryGetProperty("isUnlimited", out var u) ? u.GetBoolean() : false;

                UserSession.Credits = credits;
                UserSession.Plan = plan;
                UserSession.IsUnlimited = isUnlimited;
                CLog($"Parsed: {credits} credits | {plan} | unlimited={isUnlimited}");

                Dispatcher.Invoke(() =>
                {
                    CreditsPlanLabel.Visibility = Visibility.Collapsed;

                    if (isUnlimited)
                    {
                        // "Unlimited" here means the owner allow-list, not a specific
                        // plan tier, so name whichever plan the backend actually sent
                        // rather than assuming Pro. A Max subscriber must not see "Pro".
                        string planName = string.IsNullOrWhiteSpace(plan)
                            ? "Pro"
                            : char.ToUpperInvariant(plan[0]) + plan[1..];
                        CreditsLabel.Text = $"∞  {planName}";
                        CreditsIcon.Text = "";
                        SetCreditsBadgeStyle("", "");
                        CreditsLabel.Foreground = new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString("#FFFFFF"));
                    }
                    else
                    {
                        string display = credits >= 1000 ? $"{credits / 1000.0:F1}k" : credits.ToString("N0");

                        // Two limits stop an interview, and showing only one of them
                        // is how a support ticket starts. Somebody with two thousand
                        // credits on the badge and no listening time left reads a
                        // healthy number and a dead microphone, and concludes the
                        // app is broken rather than that they reached a limit.
                        //
                        // So both are shown, and whichever one is actually about to
                        // stop them is the one coloured. Credits alone were never
                        // the whole truth: they meter questions, and the microphone
                        // bills by the hour.
                        CreditsLabel.Text = _audioMinutesRemaining >= 0
                            ? $"⚡ {display}   ⏱ {FormatListeningTime(_audioMinutesRemaining)}"
                            : $"⚡ {display}";
                        CreditsIcon.Text = "";

                        // Pure glass: badge stays neutral; only the numeral flips to soft
                        // red when the balance is genuinely critical.
                        SetCreditsBadgeStyle("", "");
                        bool creditsLow = credits <= CreditsCriticalThreshold;
                        bool timeLow    = _audioMinutesRemaining >= 0 && _audioMinutesRemaining <= 15;
                        string creditColor = (creditsLow || timeLow) ? "#F87171" : "#FFFFFF";
                        CreditsLabel.Foreground = new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString(creditColor));

                        CreditsPlanLabel.Text = _audioMinutesRemaining == 0
                            ? "  no listening time left"
                            : "";
                        CreditsPlanLabel.Visibility = _audioMinutesRemaining == 0
                            ? Visibility.Visible : Visibility.Collapsed;
                    }
                    CLog($"Badge set to: {CreditsLabel.Text}");
                });

                _creditsFetched = true;
                _lastCreditsFetchUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => { CreditsLabel.Text = "⚡"; CreditsPlanLabel.Visibility = Visibility.Collapsed; });
                CLog($"EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }
            }
            finally
            {
                _creditsFetchGate.Release();
            }
        }

        private void SetCreditsBadgeStyle(string bg, string border)
        {
            // Pure-glass theme: the credits badge is always neutral frosted glass,
            // regardless of balance. (Status is conveyed by the numeral only.)
            CreditsBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0DFFFFFF"));
            CreditsBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1AFFFFFF"));
        }

        private async void CreditsBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            await OpenCreditsDetailsAsync();
        }

        private async void PopupCreditsCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && IsDescendantOf(source, PopupSignInCardBtn))
                return;
            e.Handled = true;
            ProfileDropdownPopup.IsOpen = false;
            await OpenCreditsDetailsAsync();
        }

        private async Task OpenCreditsDetailsAsync()
        {
            if (_creditsWindow?.IsVisible == true)
            {
                _creditsWindow.Activate();
                return;
            }

            _creditsWindow = new CreditsWindow { Owner = this };
            _creditsWindow.Closed += (_, _) => _creditsWindow = null;
            _creditsWindow.Show();

            await FetchAndDisplayCreditsAsync();
            _creditsWindow?.RefreshFromSession();
        }

        // ══════════════════════════════════════════════════════════════════════
        // OPACITY / SCRIPT FOLDER
        // ══════════════════════════════════════════════════════════════════════
        private void ApplyMainWindowOpacity()
        {
            ApplyGlassOpacity(SettingsWindow.GetMainWindowOpacity());
        }

        // Glass model: content (text, buttons, surfaces) stays fully opaque and crisp;
        // ONLY the near-black backdrop fades. At 100% the backdrop is solid black; as the
        // slider drops, the desktop shows through the glass. No flat "tint" over the text.
        private void ApplyGlassOpacity(double op)
        {
            this.Opacity = 1.0;
            // Map the stored 0.50-1.0 preference onto backdrop alpha 0-100% so the
            // slider percentage reads directly as glass darkness (25% slider = ~25%
            // black backdrop = mostly see-through glass; 100% = solid). A small floor
            // keeps the frame faintly visible at the extreme.
            double bd = Math.Clamp((op - 0.50) / 0.50, 0.06, 1.0);
            byte a = (byte)Math.Clamp(bd * 255.0, 0, 255);
            if (MainAppBorder != null)
                MainAppBorder.Background = new SolidColorBrush(Color.FromArgb(a, 0x09, 0x0B, 0x12));
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
        // STEALTH MODE
        // ══════════════════════════════════════════════════════════════════════
        private void StealthBtn_Click(object sender, RoutedEventArgs e) => ToggleStealth();
        private void StealthToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ToggleStealth();

        private void ToggleStealth()
        {
            _stealthMode = !_stealthMode;

            // Persist BEFORE applying: every other window reads
            // SettingsWindow.GetStealthMode() in its constructor, so one created
            // right after this point must see the new value, not the old one.
            // Persist so the Settings toggle and the account-menu toggle stay in sync.
            try
            {
                var cfg = SettingsWindow.LoadConfig();
                cfg.StealthMode = _stealthMode;
                SettingsWindow.SaveConfig(cfg);
            }
            catch (Exception ex) { DebugWindow.Log("STEALTH", $"persist failed: {ex.Message}"); }

            ApplyStealthToAllWindows();
            UpdateStealthBtn();
        }

        /// <summary>
        /// Applies the current stealth setting to every open window rather than only
        /// the main one. The answer/camera overlay is a separate top-level window with
        /// its own HWND, so its capture-exclusion flag stays exactly as it was set when
        /// the window was constructed until something clears it. That is why turning
        /// stealth off used to leave the overlay hidden from screen capture while the
        /// main window correctly became visible again.
        /// </summary>
        private void ApplyStealthToAllWindows()
        {
            try { WindowStealth.SetStealthMode(this, _stealthMode); }
            catch (Exception ex) { DebugWindow.Log("STEALTH", ex.Message); }

            // The overlay may not be in Application.Current.Windows yet if it has
            // never been shown, so set it explicitly.
            if (answerWindow != null)
            {
                try { WindowStealth.SetStealthMode(answerWindow, _stealthMode); }
                catch (Exception ex) { DebugWindow.Log("STEALTH", $"overlay: {ex.Message}"); }
            }

            try
            {
                var app = Application.Current;
                if (app == null) return;
                foreach (Window w in app.Windows)
                {
                    if (w == null || ReferenceEquals(w, this) || ReferenceEquals(w, answerWindow)) continue;
                    try { WindowStealth.SetStealthMode(w, _stealthMode); }
                    catch (Exception ex) { DebugWindow.Log("STEALTH", $"{w.GetType().Name}: {ex.Message}"); }
                }
            }
            catch (Exception ex) { DebugWindow.Log("STEALTH", $"enumerate failed: {ex.Message}"); }
        }

        // Clicking the stealth pill toggles stealth — a fast, discoverable control.

        // Drive the premium toolbar stealth pill (dot + icon + label + halo glow) so the
        // user can SEE at a glance whether they're hidden from screen capture.
        /// <summary>
        /// Kept as a no-op. The stealth pill was removed from the toolbar and the
        /// setting now lives in Settings alone, but the toggle paths still call this
        /// and there is no toolbar element left to repaint.
        /// </summary>
        private void UpdateStealthBtn() { }

        // ── FIRST-RUN ONBOARDING ────────────────────────────────────────────
        private string OnboardingSeenPath => Path.Combine(AppDataFolder, "onboarding_seen.flag");

        private void ShowOnboarding()
        {
            try { if (OnboardingOverlay != null) OnboardingOverlay.Visibility = Visibility.Visible; }
            catch (Exception ex) { DebugWindow.Log("ONBOARD", ex.Message); }
        }

        private async void OnboardingClose_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (OnboardingOverlay != null) OnboardingOverlay.Visibility = Visibility.Collapsed;
                try { File.WriteAllText(OnboardingSeenPath, "1"); } catch { }
                // Hand keyboard focus back to the window so SPACE works right away.
                this.Focusable = true;
                System.Windows.Input.Keyboard.Focus(this);
            }
            catch (Exception ex) { DebugWindow.Log("ONBOARD", ex.Message); }
        }

        private void HelpBtn_Click(object sender, RoutedEventArgs e) => ShowOnboarding();

        private void ProfileHowItWorks_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try { if (ProfileDropdownPopup != null) ProfileDropdownPopup.IsOpen = false; } catch { }
            ShowOnboarding();
        }

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

            // The switch may have been set before compact was opened, and the
            // overlay starts from its XAML default, so it would have shown OFF
            // while the screen was being watched.
            answerWindow.SetWatchScreenState(_watchScreenMode);
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

        private void AutoModePill_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            ProfileDropdownPopup.IsOpen = false;
            SavedResumesPopup.IsOpen = false;
            ListeningModePopup.PlacementTarget = AutoModePill;
            UpdateListeningModePopupSelection();
            ListeningModePopup.IsOpen = !ListeningModePopup.IsOpen;
        }

        // ══════════════════════════════════════════════════════════════════════
        // IN-APP ALERT
        // Replaces MessageBox for anything that can fire while an interview is
        // running. A MessageBox is its own top-level window, so it never gets the
        // WDA_EXCLUDEFROMCAPTURE flag WindowStealth applies, and it showed up on
        // the interviewer's screen share. This banner is inside a window that is
        // already excluded from capture, and it is not modal.
        // ══════════════════════════════════════════════════════════════════════

        private DispatcherTimer? _alertTimer;

        /// <summary>Routes an alert to whichever surface is currently on screen.</summary>
        internal static void Alert(string title, string message)
        {
            var main = System.Windows.Application.Current?.MainWindow as MainWindow;
            if (main == null)
            {
                // No window yet (startup faults). A dialog is the only option left,
                // and nothing is being shared at this point anyway.
                try
                {
                    MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch { }
                return;
            }

            try { main.Dispatcher.Invoke(() => main.ShowInAppAlert(title, message)); }
            catch { }
        }

        /// <param name="persist">
        /// Keep the banner up until dismissed. Used for faults the user has to act
        /// on, which a timed banner would hide again before they had read it.
        /// </param>
        internal void ShowInAppAlert(string title, string message, bool persist = false)
        {
            // In compact overlay the main window is hidden, so the banner would
            // never be seen. The overlay is the visible surface there.
            if (_isCameraMode && answerWindow != null)
            {
                answerWindow.UpdateAnswer(string.IsNullOrWhiteSpace(message) ? title : title + "\n\n" + message);
                return;
            }

            if (InAppAlert == null) return;

            InAppAlertTitle.Text = title;
            InAppAlertBody.Text = message;
            InAppAlertBody.Visibility = string.IsNullOrWhiteSpace(message)
                ? Visibility.Collapsed
                : Visibility.Visible;
            InAppAlert.Visibility = Visibility.Visible;

            _alertTimer?.Stop();
            if (persist) return;

            _alertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
            _alertTimer.Tick += (_, _) =>
            {
                _alertTimer?.Stop();
                if (InAppAlert != null) InAppAlert.Visibility = Visibility.Collapsed;
            };
            _alertTimer.Start();
        }

        /// <summary>
        /// Keeps an installed copy current on its own. On an installed copy the
        /// new version is fetched in the background and swapped in the next time
        /// the app closes, so the user never has to go looking for it and is never
        /// interrupted to get it. On a copy that cannot update itself, a build
        /// straight out of a folder or an older packaged install, it falls back to
        /// simply saying a newer version exists.
        ///
        /// Deliberately quiet either way: it waits so it never competes with the
        /// engine starting, says nothing at all when there is nothing to say, and
        /// uses the in-app banner rather than a dialog, which would show up on a
        /// screen share.
        /// </summary>
        private async Task NotifyIfUpdateAvailableAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(12));

                if (UpdateService.IsManaged)
                {
                    string? staged = await UpdateService.CheckAndStageAsync();
                    if (string.IsNullOrEmpty(staged)) return;

                    await Dispatcher.InvokeAsync(() =>
                        ShowInAppAlert(
                            $"Replysis {staged} is ready",
                            "It installs by itself the next time you close and reopen Replysis. Nothing will interrupt you before then.",
                            persist: true));
                    return;
                }

                string? newer = await SettingsWindow.GetNewerVersionOrNullAsync();
                if (string.IsNullOrEmpty(newer)) return;

                await Dispatcher.InvokeAsync(() =>
                    ShowInAppAlert(
                        $"Version {newer} is available",
                        $"You are on {SettingsWindow.InstalledVersion()}. Download the new version from replysis.com when you have a moment.",
                        persist: true));
            }
            catch (Exception ex)
            {
                DebugWindow.Log("UPDATE", $"Startup update check skipped: {ex.GetType().Name}");
            }
        }

        private void InAppAlertDismiss_Click(object sender, RoutedEventArgs e)
        {
            _alertTimer?.Stop();
            if (InAppAlert != null) InAppAlert.Visibility = Visibility.Collapsed;
        }

        private void CompactOverlayPill_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            ProfileDropdownPopup.IsOpen = false;
            SavedResumesPopup.IsOpen = false;
            ListeningModePopup.IsOpen = false;
            CameraMode_Click(sender, new RoutedEventArgs());
        }

        private void ManualModeOption_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            SelectListeningMode(ListeningMode.Manual);
        }

        private void InterviewModeOption_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            SelectListeningMode(ListeningMode.InterviewAuto);
        }

        private void PracticeModeOption_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            SelectListeningMode(ListeningMode.PracticeAuto);
        }

        private void SelectListeningMode(ListeningMode mode)
        {
            ListeningModePopup.IsOpen = false;
            if (_listeningMode == mode)
            {
                if (AutoModeEnabled) StartAutoListeningIfReady();
                return;
            }

            string previousCaptureMode = CaptureModeFor(_listeningMode);
            string nextCaptureMode = CaptureModeFor(mode);

            // Mode changes never submit a half-finished transcript. They stop capture,
            // clear only the auto-turn detector, and leave the user's saved Settings intact.
            if (isListening)
            {
                isListening = false;
                isMuted = true;
                WritePauseFlag();
                UpdateMicUi();
            }

            _listeningMode = mode;
            _lastAutoSubmittedQuestion = "";
            _lastAutoRejectedTranscript = "";
            _lastAutoSubmitUtc = DateTime.MinValue;
            ResetAutoTurnDetection();
            UpdateListeningModeUi();

            if (!string.Equals(previousCaptureMode, nextCaptureMode, StringComparison.Ordinal))
            {
                ShowListeningModeNotice("SWITCHING");
                StartSpeechmaticsEngine();
            }
            else if (AutoModeEnabled)
            {
                StartAutoListeningIfReady();
            }

            string modeName = mode switch
            {
                ListeningMode.InterviewAuto => "Interview mode: hears the interviewer, not you",
                ListeningMode.PracticeAuto => "Practice mode: hears you",
                _ => "Press Space"
            };
            DebugWindow.Log("MODE", $"Selected {modeName}. Saved audio preference was not changed.");
        }

        private static string CaptureModeFor(ListeningMode mode) => mode switch
        {
            ListeningMode.InterviewAuto => "system",
            ListeningMode.PracticeAuto => "both",
            _ => SettingsWindow.GetMicCaptureEnabled() ? "both" : "system"
        };

        private void UpdateListeningModeUi()
        {
            if (AutoModePill == null) return;
            static SolidColorBrush Brush(string hex) =>
                new((Color)ColorConverter.ConvertFromString(hex));

            switch (_listeningMode)
            {
                case ListeningMode.InterviewAuto:
                    AutoModePill.Background = Brush("#102A1D");
                    AutoModePill.BorderBrush = Brush("#2C7B50");
                    AutoModeDot.Fill = Brush("#34E08A");
                    AutoModeLabel.Text = "INTERVIEW";
                    AutoModeLabel.Foreground = Brush("#B8F5D3");
                    AutoModeChevron.Foreground = Brush("#73C998");
                    AutoModeGlow.Color = (Color)ColorConverter.ConvertFromString("#34E08A");
                    AutoModeGlow.Opacity = 0.42;
                    AutoModePill.ToolTip = "Hears the interviewer and answers on its own. Your microphone stays off.";
                    break;

                case ListeningMode.PracticeAuto:
                    AutoModePill.Background = Brush("#0C2731");
                    AutoModePill.BorderBrush = Brush("#22768B");
                    AutoModeDot.Fill = Brush("#38CFF2");
                    AutoModeLabel.Text = "PRACTICE";
                    AutoModeLabel.Foreground = Brush("#BDEFFC");
                    AutoModeChevron.Foreground = Brush("#70C6DA");
                    AutoModeGlow.Color = (Color)ColorConverter.ConvertFromString("#38CFF2");
                    AutoModeGlow.Opacity = 0.38;
                    AutoModePill.ToolTip = "Hears you and answers on its own. No interviewer needed.";
                    break;

                default:
                    AutoModePill.Background = Brush("#101827");
                    AutoModePill.BorderBrush = Brush("#26364C");
                    AutoModeDot.Fill = Brush("#607086");
                    AutoModeLabel.Text = "PRESS SPACE";
                    AutoModeLabel.Foreground = Brush("#A9B6C8");
                    AutoModeChevron.Foreground = Brush("#6F8198");
                    AutoModeGlow.Opacity = 0;
                    AutoModePill.ToolTip = "Press Space to listen, Space again for the answer.";
                    break;
            }

            UpdateListeningModePopupSelection();
        }

        private void UpdateListeningModePopupSelection()
        {
            if (ManualModeCheck == null) return;
            static SolidColorBrush Brush(string hex) =>
                new((Color)ColorConverter.ConvertFromString(hex));

            ManualModeCheck.Visibility = _listeningMode == ListeningMode.Manual
                ? Visibility.Visible : Visibility.Collapsed;
            InterviewModeCheck.Visibility = _listeningMode == ListeningMode.InterviewAuto
                ? Visibility.Visible : Visibility.Collapsed;
            PracticeModeCheck.Visibility = _listeningMode == ListeningMode.PracticeAuto
                ? Visibility.Visible : Visibility.Collapsed;

            SetModeRowSelection(ManualModeRow, _listeningMode == ListeningMode.Manual,
                Brush("#152337"), Brush("#40536B"));
            SetModeRowSelection(InterviewModeRow, _listeningMode == ListeningMode.InterviewAuto,
                Brush("#102A1D"), Brush("#2C7B50"));
            SetModeRowSelection(PracticeModeRow, _listeningMode == ListeningMode.PracticeAuto,
                Brush("#0C2731"), Brush("#22768B"));
        }

        private static void SetModeRowSelection(System.Windows.Controls.Border row, bool selected,
            SolidColorBrush selectedBackground, SolidColorBrush selectedBorder)
        {
            row.Background = selected ? selectedBackground : Brushes.Transparent;
            row.BorderBrush = selected ? selectedBorder : Brushes.Transparent;
            row.BorderThickness = selected ? new Thickness(1) : new Thickness(0);
        }

        private void ShowListeningModeNotice(string message)
        {
            if (AutoModePill == null) return;
            static SolidColorBrush Brush(string hex) =>
                new((Color)ColorConverter.ConvertFromString(hex));

            AutoModePill.Background = Brush("#2A2110");
            AutoModePill.BorderBrush = Brush("#8A6825");
            AutoModeDot.Fill = Brush("#F5B83D");
            AutoModeLabel.Text = message;
            AutoModeLabel.Foreground = Brush("#F8D58B");
            AutoModeChevron.Foreground = Brush("#C99D49");
            AutoModePill.ToolTip = "Switching the existing transcription engine to the selected capture mode";

            _autoModeNoticeTimer?.Stop();
            _autoModeNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _autoModeNoticeTimer.Tick += (_, _) =>
            {
                _autoModeNoticeTimer?.Stop();
                UpdateListeningModeUi();
            };
            _autoModeNoticeTimer.Start();
        }

        private void StartAutoListeningIfReady()
        {
            if (!AutoModeEnabled || !_engineOnline || isListening || isProcessing ||
                _flushing || _isScreenAnalyzing)
                return;

            this.Focusable = true;
            System.Windows.Input.Keyboard.Focus(this);
            HandleSpaceDown("AUTO");
        }

        // ══════════════════════════════════════════════════════════════════════
        // LISTENING TIME
        //
        // Credits count questions. Speechmatics charges by the hour of audio,
        // and nothing was counting that, so the expensive half of the bill was
        // invisible: a microphone left open all afternoon cost real money and
        // showed up nowhere.
        //
        // Two halves. This side measures the time and reports it as it goes,
        // rather than at the end, because an app that is closed, crashes or
        // loses its connection would otherwise have listened for free. The
        // server keeps the running total and refuses a new speech token once
        // the month's allowance is gone.
        //
        // And the mic switches itself off after a long silence. Most of the
        // waste will never be anybody being greedy, it will be somebody who
        // opened the app and went to lunch, and an empty room costs the same
        // per hour as an interview.
        // ══════════════════════════════════════════════════════════════════════
        private static readonly TimeSpan ListeningReportInterval = TimeSpan.FromMinutes(1);

        /// <summary>
        /// How long a silence has to run before the microphone gives up.
        ///
        /// Three minutes, because in a real interview somebody speaks every few
        /// seconds and even a long thinking pause is well under a minute. It is
        /// meant to catch an empty room, never a person deciding what to say.
        /// </summary>
        private static readonly TimeSpan IdleListeningTimeout = TimeSpan.FromMinutes(3);

        /// <summary>
        /// How long to wait when nothing has been said at all.
        ///
        /// Three minutes is right for a pause inside a conversation. It is far
        /// too patient for a session where not one word arrived: pressing Space
        /// and saying nothing is a stray keypress, not a pause, and waiting
        /// three minutes to notice meant the notice appeared over and over while
        /// somebody was reading an answer with the microphone still open.
        /// </summary>
        private static readonly TimeSpan SilentSessionTimeout = TimeSpan.FromSeconds(45);

        // Whether anything was heard at all in the current listening session.
        private bool _heardAnythingThisSession;

        private DispatcherTimer? _listeningMeterTimer;
        private DateTime _listeningSinceUtc = DateTime.MinValue;
        private DateTime _lastSpeechHeardUtc = DateTime.MinValue;
        private double _unreportedListeningSeconds;
        private int _audioMinutesRemaining = -1;   // -1 = unknown or unlimited

        private void StartListeningMeter()
        {
            _listeningSinceUtc  = DateTime.UtcNow;
            _lastSpeechHeardUtc = DateTime.UtcNow;
            _heardAnythingThisSession = false;

            if (_listeningMeterTimer == null)
            {
                _listeningMeterTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _listeningMeterTimer.Tick += (_, _) => ListeningMeterTick();
            }
            _listeningMeterTimer.Start();
        }

        private void StopListeningMeter()
        {
            _listeningMeterTimer?.Stop();
            if (_listeningSinceUtc != DateTime.MinValue)
            {
                _unreportedListeningSeconds += (DateTime.UtcNow - _listeningSinceUtc).TotalSeconds;
                _listeningSinceUtc = DateTime.MinValue;
            }
            // Whole minutes only, and the remainder carries into the next turn.
            //
            // This used to round the remainder up to a whole minute here, at
            // the end of every turn, on top of the whole minutes the tick had
            // already reported. Since both Manual and Auto stop the meter after
            // every exchange, that landed once per turn rather than once per
            // sitting, and what a user paid depended on how they happened to
            // talk: ten 40-second turns billed 50% over, while thirty
            // 25-second turns fell under the floor every time and billed
            // nothing at all for twelve and a half minutes of listening.
            //
            // The session is the billing unit now. The floor still exists — a
            // brief interview is not free — but it is applied once, on the way
            // out, in FlushListeningMeterOnExit.
            int minutes = ListeningBilling.MinutesOnTick(
                _unreportedListeningSeconds, out _unreportedListeningSeconds);
            if (minutes > 0) _ = ReportListeningMinutesAsync(minutes);
        }

        private void ListeningMeterTick()
        {
            if (!isListening) { StopListeningMeter(); return; }

            var now = DateTime.UtcNow;

            WarnIfHearingButNotTranscribing(now);

            TimeSpan patience = _heardAnythingThisSession
                ? IdleListeningTimeout
                : SilentSessionTimeout;

            if (now - _lastSpeechHeardUtc >= patience)
            {
                DebugWindow.Log("METER", _heardAnythingThisSession
                    ? $"No speech for {IdleListeningTimeout.TotalMinutes:0} minutes; stopping the microphone."
                    : $"Nothing heard in {SilentSessionTimeout.TotalSeconds:0}s; stopping the microphone.");
                StopForIdle();
                return;
            }

            if (_listeningSinceUtc != DateTime.MinValue)
            {
                _unreportedListeningSeconds += (now - _listeningSinceUtc).TotalSeconds;
                _listeningSinceUtc = now;
            }

            if (_unreportedListeningSeconds >= ListeningReportInterval.TotalSeconds)
            {
                int minutes = ListeningBilling.MinutesOnTick(
                    _unreportedListeningSeconds, out _unreportedListeningSeconds);
                _ = ReportListeningMinutesAsync(minutes);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // HEARING, BUT NOT TRANSCRIBING
        //
        // The speech engine can fail in a way that looks exactly like success.
        // It stays connected, it reports itself online, the microphone level
        // moves — and no words ever come back. Three separate bugs in the audio
        // reader produced precisely that shape, and every one of them was found
        // by a person noticing an empty transcript rather than by the app
        // noticing anything at all.
        //
        // A user cannot diagnose this and cannot work around it. The app looks
        // healthy, so they assume they are doing something wrong, and the
        // interview is over before anybody works out otherwise.
        //
        // The engine already says enough to catch it: it prints when the
        // microphone hears speech, and it prints how many characters came back.
        // Speech arriving with nothing returned, for long enough that no normal
        // pause explains it, is the signature. Either line alone is worthless —
        // silence is normal, and so is a lull — but the pair is decisive.
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// How long speech may arrive with nothing coming back before this is a
        /// fault rather than a pause. Speechmatics runs about 0.7s behind live
        /// speech, so anything under a few seconds is ordinary; twelve seconds
        /// of continuous speech returning nothing is not.
        /// </summary>
        /// <summary>
        /// The character count in "PARTIAL received (12 chars)". Only
        /// "received (", the digits, and " char" are load-bearing; the rest of
        /// the sentence can be reworded without breaking the detector.
        /// </summary>
        private static readonly Regex EngineCharCount =
            new(@"received \((\d+) chars?\)", RegexOptions.Compiled);

        private DateTime _lastSpeechDetectedUtc = DateTime.MinValue;
        private DateTime _lastWordsReceivedUtc = DateTime.MinValue;
        private DateTime _lastDeafnessWarningUtc = DateTime.MinValue;

        // The decision itself is in SpeechHealth, apart from the acting on it,
        // so a test can call the shipping rule instead of a copy written to
        // match it. It sat here as a private method over five instance fields,
        // which meant the only way to see it work was to run a real interview
        // and hope the engine broke - so nobody ever had.
        private void WarnIfHearingButNotTranscribing(DateTime now)
        {
            if (!SpeechHealth.ShouldWarn(
                    _engineOnline, now,
                    _lastSpeechDetectedUtc, _lastWordsReceivedUtc,
                    _listeningSinceUtc, _lastDeafnessWarningUtc,
                    out int silentSeconds))
                return;

            _lastDeafnessWarningUtc = now;

            DebugWindow.Log("ENGINE",
                $"Hearing speech but no words for {silentSeconds}s — "
                + "transcription appears to have stopped while the engine still reports online.");
            ShowListeningModeNotice("HEARING YOU BUT NOT TRANSCRIBING — RESTART THE APP");
        }

        /// <summary>
        /// Ends a listening session nobody is using, and says so plainly.
        ///
        /// Silently muting would be worse than the waste it prevents: someone
        /// coming back to the app would speak into a microphone they believed
        /// was on. So it stops, explains, and says how to start again.
        /// </summary>
        private void StopForIdle()
        {
            StopListeningMeter();
            isListening = false;
            isMuted = true;
            WritePauseFlag();
            if (AutoModeEnabled) _autoTurnSubmitting = false;

            // Said in the badge, not in the answer.
            //
            // It used to overwrite AiAnswerBox, so an answer somebody was still
            // reading was replaced by a notice about the microphone. The
            // microphone going quiet is worth knowing and is not worth losing
            // the answer over, and it happened every time they read a long one
            // with the mic still on — which is exactly when the answer mattered
            // most.
            ShowListeningModeNotice(_heardAnythingThisSession
                ? $"MIC OFF AFTER {IdleListeningTimeout.TotalMinutes:0} MIN QUIET — SPACE TO RESUME"
                : "MIC OFF — NOTHING HEARD");
            DebugWindow.Log("METER", "Microphone stopped after the idle timeout.");
            UpdateMicUi();
        }

        /// <summary>
        /// Listening time in the units a person would say it in.
        ///
        /// "1672" is a number nobody can price against their afternoon; "27h
        /// 52m" is. Minutes only below an hour, because that is when it starts
        /// to matter and precision is worth the width.
        /// </summary>
        private static string FormatListeningTime(int minutes)
        {
            if (minutes <= 0) return "0m";
            if (minutes < 60) return $"{minutes}m";
            int hours = minutes / 60;
            int rest  = minutes % 60;
            return rest == 0 ? $"{hours}h" : $"{hours}h {rest}m";
        }

        /// <summary>
        /// Reads the listening allowance without spending any of it.
        ///
        /// Called beside the credits fetch, so the badge is right from the
        /// moment the app opens rather than only after the first minute of an
        /// interview has been reported.
        /// </summary>
        private async Task FetchListeningTimeAsync()
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{BackendUrl}/api/v1/usage/listening");
                if (!string.IsNullOrEmpty(UserSession.IdToken))
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {UserSession.IdToken}");
                req.Headers.TryAddWithoutValidation("X-Device-Id", DeviceIdentity.Current);

                using var res = await _creditsClient.SendAsync(req);
                if (!res.IsSuccessStatusCode) return;

                string body = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("remainingMinutes", out var rem))
                {
                    _audioMinutesRemaining = rem.GetInt32();
                    DebugWindow.Log("METER", $"{_audioMinutesRemaining} listening minutes left this month.");
                }
            }
            catch (Exception ex) { DebugWindow.Log("METER", $"Allowance fetch failed: {ex.Message}"); }
        }

        /// <summary>Best-effort final report, on the way out.</summary>
        private void FlushListeningMeterOnExit()
        {
            try
            {
                if (_listeningSinceUtc != DateTime.MinValue)
                {
                    _unreportedListeningSeconds += (DateTime.UtcNow - _listeningSinceUtc).TotalSeconds;
                    _listeningSinceUtc = DateTime.MinValue;
                }
                // The one place the remainder is rounded up, once per sitting.
                int minutes = ListeningBilling.MinutesAtSessionEnd(_unreportedListeningSeconds);
                if (minutes <= 0) return;

                _unreportedListeningSeconds = 0;

                // Sent synchronously, and NOT by blocking on the async version.
                //
                // This runs on the closing UI thread. ReportListeningMinutesAsync
                // awaits without ConfigureAwait(false), so its continuation needs
                // the UI thread to resume - and .Wait() here is holding it. The
                // continuation could never run, the wait always burned its full
                // timeout, and the result was never read. The Mac session found
                // the same shape on its side: a flush that existed, ran,
                // computed the right number, and sent nothing.
                //
                // Bounded, because a lost minute is better than an app that will
                // not close.
                ReportListeningMinutesOnExit(minutes, TimeSpan.FromSeconds(3));
            }
            catch { }
        }

        /// <summary>
        /// The last report, sent on the way out, on the thread that is closing.
        ///
        /// Separate from the async version rather than sharing it, because the
        /// two have opposite requirements. The async one resumes on the UI
        /// thread on purpose: it updates the remaining-minutes display and the
        /// low-allowance warning. This one must never touch the UI and must
        /// never await back onto a thread it is itself blocking - it exists to
        /// get one number onto the wire before the process goes away.
        ///
        /// Nothing is read from the response. There is no longer a window to
        /// show it in.
        /// </summary>
        private void ReportListeningMinutesOnExit(int minutes, TimeSpan timeout)
        {
            if (minutes <= 0) return;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post,
                    $"{BackendUrl}/api/v1/usage/listening");
                if (!string.IsNullOrEmpty(UserSession.IdToken))
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {UserSession.IdToken}");
                req.Headers.TryAddWithoutValidation("X-Device-Id", DeviceIdentity.Current);
                req.Content = new StringContent("{\"minutes\":" + minutes + "}",
                    System.Text.Encoding.UTF8, "application/json");

                using var cts = new System.Threading.CancellationTokenSource(timeout);
                using var res = _creditsClient.Send(req, cts.Token);
                DebugWindow.Log("METER",
                    $"Final report on exit: {minutes} min, HTTP {(int)res.StatusCode}");
            }
            catch (Exception ex) { DebugWindow.Log("METER", $"Final report failed: {ex.Message}"); }
        }

        private async Task ReportListeningMinutesAsync(int minutes)
        {
            if (minutes <= 0) return;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post,
                    $"{BackendUrl}/api/v1/usage/listening");
                if (!string.IsNullOrEmpty(UserSession.IdToken))
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {UserSession.IdToken}");
                req.Headers.TryAddWithoutValidation("X-Device-Id", DeviceIdentity.Current);
                req.Content = new StringContent("{\"minutes\":" + minutes + "}",
                    System.Text.Encoding.UTF8, "application/json");

                using var res = await _creditsClient.SendAsync(req);
                if (!res.IsSuccessStatusCode)
                {
                    DebugWindow.Log("METER", $"Report failed: HTTP {(int)res.StatusCode}");
                    return;
                }

                string body = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("remainingMinutes", out var rem))
                {
                    _audioMinutesRemaining = rem.GetInt32();
                    DebugWindow.Log("METER", $"Reported {minutes} min; {_audioMinutesRemaining} left this month.");
                    WarnIfListeningTimeLow();
                    _ = FetchAndDisplayCreditsAsync(force: true);
                }
            }
            catch (Exception ex) { DebugWindow.Log("METER", $"Report error: {ex.Message}"); }
        }

        /// <summary>
        /// Warns before the allowance runs out, not once it already has.
        ///
        /// Transcription stopping without warning in the middle of an interview
        /// is the worst possible way to learn a limit exists.
        /// </summary>
        private void WarnIfListeningTimeLow()
        {
            if (_audioMinutesRemaining < 0) return;   // unlimited, or not known yet
            if (_audioMinutesRemaining > 15) return;

            Dispatcher.Invoke(() => ShowListeningModeNotice(
                _audioMinutesRemaining <= 0
                    ? "LISTENING TIME USED UP"
                    : $"{_audioMinutesRemaining} MIN LEFT THIS MONTH"));
        }

        private void ResetAutoTurnDetection()
        {
            _autoTurnSubmitting = false;
            _autoContinuationPrefix = "";
            _autoLastTranscript = "";
            _autoListeningStartedUtc = DateTime.UtcNow;
            _autoTranscriptChangedUtc = DateTime.UtcNow;
            _autoLongestMidTurnGapMs = 0;
        }

        // ══════════════════════════════════════════════════════════════════════
        // CONTINUATIONS
        //
        // Silence cannot tell "finished" from "still thinking". An interviewer
        // who says "what's the difference between W2 and C2C", pauses, and then
        // adds "and full time" leaves a gap identical to the end of a question.
        // Slow speakers, laggy calls and people who think mid-sentence all
        // produce it, and no threshold fixes it: longer feels sluggish for
        // everyone, shorter cuts people off.
        //
        // So the guess is allowed to be wrong and then corrected. When the tail
        // arrives it is joined to what was already asked and the whole question
        // is answered again, replacing the half answer on screen. The candidate
        // sees an answer immediately either way, and a better one if there was
        // more coming.
        //
        // Without this the tail was not merely unmerged, it was discarded: "and
        // full time" is not a question by itself, so it was rejected as an
        // incomplete fragment and never answered at all.
        // ══════════════════════════════════════════════════════════════════════
        private static readonly HashSet<string> QuestionStarters = new(StringComparer.OrdinalIgnoreCase)
        {
            "what", "why", "how", "when", "where", "who", "which", "can", "could",
            "would", "will", "do", "does", "did", "are", "is", "was", "were",
            "have", "has", "should",
        };

        private static readonly HashSet<string> InterviewCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "tell", "explain", "describe", "walk", "share", "discuss", "design",
            "implement", "compare", "define", "introduce", "summarize", "write",
            "create", "build", "code", "program", "solve", "develop", "generate", "show",
        };

        private static readonly TimeSpan ContinuationWindow = TimeSpan.FromSeconds(12);

        /// <summary>Words that join a tail onto the sentence before it.</summary>
        private static readonly HashSet<string> ContinuationOpeners = new(StringComparer.OrdinalIgnoreCase)
        {
            "and", "or", "but", "also", "plus", "versus", "vs", "nor",
            "instead", "rather", "besides", "along", "including", "except",
        };

        /// <summary>
        /// Noises that are not a continuation of anything. Without this, an
        /// interviewer saying "okay" after an answer would re-run the previous
        /// question and charge a credit for it.
        /// </summary>
        private static readonly HashSet<string> ContinuationFillers = new(StringComparer.OrdinalIgnoreCase)
        {
            "okay", "ok", "yes", "yeah", "yep", "no", "nope", "right", "sure",
            "thanks", "thank you", "got it", "great", "good", "nice", "cool",
            "perfect", "understood", "makes sense", "fine", "alright", "mm hmm",
            "uh huh", "hmm", "sorry", "hello", "hi",
        };

        private bool LooksLikeContinuation(string candidate, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(_lastAutoSubmittedQuestion)) return false;
            if (now - _lastAutoSubmitUtc > ContinuationWindow) return false;

            string[] words = Regex.Matches(candidate, @"[\p{L}\p{N}']+")
                                  .Cast<Match>()
                                  .Select(m => m.Value.ToLowerInvariant())
                                  .ToArray();
            if (words.Length < 2) return false;

            // A tail is short. Anything longer is the interviewer moving on, even
            // if they happened to begin it with "and".
            if (words.Length > 8) return false;

            if (ContinuationFillers.Contains(string.Join(" ", words))) return false;

            // It has to add something. "And, um, and" is not more question.
            bool addsContent = words.Any(w => !ContinuationOpeners.Contains(w) &&
                                              !ContinuationFillers.Contains(w));
            if (!addsContent) return false;

            // Either it is joined on explicitly, or it does not ask anything by
            // itself, which is exactly what the tail of an interrupted sentence
            // looks like.
            //
            // "Asks nothing" is the test, not "is not a sentence". "C2C or W2 or
            // full time." is a well-formed sentence and still obviously the rest
            // of "what are you looking for": it names options and poses no
            // question. Judged on sentence-completeness it was missed.
            bool asksSomething = candidate.Contains('?') ||
                                 QuestionStarters.Contains(words[0]) ||
                                 InterviewCommands.Contains(words[0]);

            return ContinuationOpeners.Contains(words[0]) || !asksSomething;
        }

        /// <summary>
        /// Joins a tail onto the question it belongs to, without repeating the
        /// joining word or doubling the punctuation.
        /// </summary>
        private static string MergeContinuation(string asked, string tail)
        {
            string left = (asked ?? "").TrimEnd().TrimEnd('?', '.', '!', ',', ' ');
            string right = (tail ?? "").Trim();
            if (left.Length == 0) return right;
            if (right.Length == 0) return asked ?? "";
            return left + " " + right;
        }

        private void TrySubmitAutomaticTurn(string transcript)
        {
            if (!AutoModeEnabled || _autoTurnSubmitting || !isListening ||
                isProcessing || _flushing || _isScreenAnalyzing)
                return;

            string question = transcript.Trim();
            string candidateQuestion = PromptBuilder.NormalizeInterviewerQuestion(question);
            DateTime now = DateTime.UtcNow;
            bool isContinuation = LooksLikeContinuation(candidateQuestion, now);
            bool isCompleteQuestion = isContinuation ||
                                      IsLikelyCompleteAutomaticQuestion(candidateQuestion);
            // A continuation skips the duplicate check against the merged
            // question, since the tail never equals it. It still needs one of
            // its own: HandleSpaceUp has early exits that release the latch
            // without clearing the transcript, and the very same tail would
            // then be seen again, still inside the window, and joined on a
            // second time. "...and full time. And full time."
            bool isRepeatedFragment = isContinuation &&
                                      string.Equals(
                                          candidateQuestion,
                                          _lastAutoSubmittedFragment,
                                          StringComparison.OrdinalIgnoreCase) &&
                                      now - _lastAutoSubmitUtc < TimeSpan.FromSeconds(12);

            bool isRecentDuplicate = isRepeatedFragment ||
                                     (!isContinuation &&
                                      string.Equals(
                                          candidateQuestion,
                                          _lastAutoSubmittedQuestion,
                                          StringComparison.OrdinalIgnoreCase) &&
                                      now - _lastAutoSubmitUtc < TimeSpan.FromSeconds(12));
            if (!isCompleteQuestion || isRecentDuplicate)
            {
                if (!isCompleteQuestion &&
                    !string.Equals(question, _lastAutoRejectedTranscript, StringComparison.Ordinal))
                {
                    _lastAutoRejectedTranscript = question;
                    DebugWindow.Log("AUTO", $"Waiting for a complete question ({question.Length} chars). No AI request sent.");
                }
                return;
            }

            TurnEnding ending = ClassifyTurnEnding(candidateQuestion);
            if (ending == TurnEnding.Unfinished)
            {
                if (!string.Equals(question, _lastAutoRejectedTranscript, StringComparison.Ordinal))
                {
                    _lastAutoRejectedTranscript = question;
                    DebugWindow.Log("AUTO", "Ends on a word that cannot finish a sentence; still listening.");
                }
                return;
            }

            // Waiting longer is only ever right when it is genuinely unclear
            // whether they stopped. Making every question wait for the worst
            // case is the same bug in the other direction: the candidate sits
            // in silence after a question that plainly ended.
            int requiredSilenceMs;
            if (ending == TurnEnding.Finished)
            {
                requiredSilenceMs = AutoTurnFinishedSilenceMs;
            }
            else
            {
                requiredSilenceMs = AutoTurnNaturalSilenceMs;
                int paceFloorMs = (int)Math.Round(_autoLongestMidTurnGapMs * 1.3);
                if (paceFloorMs > requiredSilenceMs)
                    requiredSilenceMs = Math.Min(paceFloorMs, AutoTurnMaxSilenceMs);
            }

            if (question.Length < AutoTurnMinimumChars ||
                now - _autoListeningStartedUtc < TimeSpan.FromMilliseconds(AutoTurnMinimumSpeechMs) ||
                now - _autoTranscriptChangedUtc < TimeSpan.FromMilliseconds(requiredSilenceMs))
                return;

            _autoTurnSubmitting = true;
            _autoContinuationPrefix = isContinuation ? _lastAutoSubmittedQuestion : "";
            _lastAutoSubmittedFragment = isContinuation ? candidateQuestion : "";
            _lastAutoSubmittedQuestion = isContinuation
                ? MergeContinuation(_lastAutoSubmittedQuestion, candidateQuestion)
                : candidateQuestion;
            _lastAutoSubmitUtc = now;
            if (isContinuation)
                DebugWindow.Log("AUTO", $"Continuation heard; re-answering the whole question: {_lastAutoSubmittedQuestion}");
            DebugWindow.Log("AUTO", $"{ending} ending, stable for {requiredSilenceMs}ms; submitting {candidateQuestion.Length} normalized characters.");
            HandleSpaceUp("AUTO");
        }

        /// <summary>
        /// Remembers the longest pause this speaker has taken while still talking.
        ///
        /// The silence thresholds are one number for everybody, and people do not
        /// share a speaking speed. A slow speaker leaves 700ms between phrases,
        /// which is longer than the 380ms that counts as "they have finished",
        /// so their question was submitted while they were still asking it. Their
        /// remaining words then arrived as the start of the next question.
        ///
        /// Their own pauses are the only fair yardstick, so the wait is measured
        /// against those rather than against an average of everyone.
        /// </summary>
        private void NoteAutoSpeechPace(DateTime now)
        {
            if (_autoTranscriptChangedUtc == DateTime.MinValue) return;
            double gapMs = (now - _autoTranscriptChangedUtc).TotalMilliseconds;

            // Above two seconds it is no longer a pause inside a sentence: they
            // stopped, and something else (a slow packet, thinking) explains it.
            if (gapMs > _autoLongestMidTurnGapMs && gapMs <= 2_000)
                _autoLongestMidTurnGapMs = gapMs;
        }

        /// <summary>
        /// How the transcript ends, which decides how long to wait before answering.
        ///
        /// The wait was one number for every question, and that cannot be right:
        /// too short and it answers while the interviewer is still asking, too
        /// long and the candidate sits in silence after a question that clearly
        /// ended. Both were reported, days apart, from the same setting.
        ///
        /// So the two are told apart instead of averaged. Most questions end
        /// unmistakably and get answered faster than before; only the genuinely
        /// unclear ones wait, and only they pay for it.
        /// </summary>
        private enum TurnEnding
        {
            /// <summary>Ended on a word no sentence can end on. Never submit.</summary>
            Unfinished,
            /// <summary>Punctuated and lands on a real word. Answer quickly.</summary>
            Finished,
            /// <summary>Could go either way. Give them room.</summary>
            Unclear,
        }

        /// <summary>
        /// Words no English sentence can end on, whatever the punctuation.
        ///
        /// "Are you looking for C2C or W2 or full time" is punctuated by the
        /// engine the moment it hears "for", and the options arrive after. A
        /// question mark after a preposition or a conjunction is the engine
        /// hearing a pause, not the speaker finishing.
        ///
        /// Conjunctions, prepositions and determiners only. Pronouns are
        /// deliberately absent: "How would you scale this?" and "Have you done
        /// that?" are finished questions, and treating them as half-spoken
        /// would slow down the ordinary case to guard against a rare one.
        /// </summary>
        private static readonly HashSet<string> NeverEndsSentence = new(StringComparer.OrdinalIgnoreCase)
        {
            "or", "and", "but", "nor", "plus", "versus", "vs",
            "to", "of", "for", "with", "without", "from", "into", "onto",
            "in", "on", "at", "by", "about", "over", "under", "between",
            "the", "a", "an", "my", "our", "your", "their", "its",
            "than", "because", "while", "if", "such", "like", "per",
        };

        /// <summary>
        /// Words that, with no punctuation after them, mean the sentence is
        /// still running. Wider than the list above, because without a full
        /// stop even "do you" or "have they" is plainly mid-air.
        /// </summary>
        private static readonly HashSet<string> DanglingTailWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "is", "are", "was", "were", "be", "been", "being", "am",
            "do", "does", "did", "have", "has", "had",
            "can", "could", "would", "should", "will", "shall", "may", "might", "must",
            "you", "we", "they", "he", "she", "it", "i", "that", "this",
            "any", "some", "more", "most", "very", "really", "so",
            "when", "then", "what", "which", "who", "how",
        };

        private static TurnEnding ClassifyTurnEnding(string question)
        {
            if (string.IsNullOrWhiteSpace(question)) return TurnEnding.Unclear;

            string trimmed = question.TrimEnd();
            char last = trimmed[^1];
            bool punctuated = last is '?' or '.' or '!';

            var words = Regex.Matches(trimmed, @"[\p{L}\p{N}']+");
            if (words.Count == 0) return TurnEnding.Unclear;

            string tail = words[^1].Value;

            // Nothing can follow these and still be a finished sentence, so the
            // speaker is mid-air no matter what the engine punctuated.
            if (NeverEndsSentence.Contains(tail))
                return punctuated ? TurnEnding.Unclear : TurnEnding.Unfinished;

            // No full stop yet, and hanging on an auxiliary or a pronoun: still
            // going. Waiting costs nothing, because their next word submits it.
            if (!punctuated && DanglingTailWords.Contains(tail))
                return TurnEnding.Unfinished;

            // Punctuated and landing on a real word: "Tell me about yourself.",
            // "How would you scale this?", "What is a closure?". The ordinary
            // case, and it should feel immediate.
            if (punctuated) return TurnEnding.Finished;

            return TurnEnding.Unclear;
        }

        private static bool IsLikelyCompleteAutomaticQuestion(string question)
        {
            if (string.IsNullOrWhiteSpace(question)) return false;

            string[] words = Regex.Matches(question, @"[\p{L}\p{N}']+")
                                  .Cast<Match>()
                                  .Select(match => match.Value.ToLowerInvariant())
                                  .ToArray();
            if (words.Length == 0) return false;

            string normalized = string.Join(" ", words);
            if (normalized is "okay" or "okay sir" or "yes" or "yes sir" or "no" or
                              "no sir" or "thanks" or "thank you" or "hello" or "hi" or
                              "la la la")
                return false;

            bool hasQuestionMark = question.Contains('?');

            string first = words[0];
            bool isQuestionStarter = QuestionStarters.Contains(first);
            if (isQuestionStarter)
                return words.Length >= 2 || (hasQuestionMark && first is "what" or "why" or "how");

            bool isInterviewCommand = InterviewCommands.Contains(first);
            if (isInterviewCommand)
            {
                if (first == "tell" && words.Length == 2 && words[1] == "me") return false;
                return words.Length >= 2;
            }

            if (hasQuestionMark) return words.Length >= 2;

            // Multiple declarative sentences are normally background conversation, not
            // a question. Question starters and coding/command requests returned above,
            // so this guard no longer blocks a valid question followed by a constraint.
            int periodCount = question.Count(character => character == '.');
            if (periodCount >= 2) return false;

            char last = question[^1];
            bool hasClosingPunctuation = last is '.' or '!';
            return words.Length >= 5 && question.Length >= 20 && hasClosingPunctuation;
        }

        // Push-to-talk: DOWN = start listening, UP = fire AI.
        // Toggle callers (button, in-app Space) call both in sequence.
        private string _listeningInitiator = "";
        private long _listeningStartTicks = 0;

        // True while the user is actively typing in one of the app's own text fields —
        // Space must insert a space character there, not toggle the mic. Checked here
        // (rather than only in a WPF key handler) so the global hook's unconditional
        // Space toggle still respects it regardless of call source.
        private bool IsTypingInTextField() =>
            ResumeTextBox.IsKeyboardFocusWithin || AskBox.IsKeyboardFocusWithin ||
            CompanyNameBox.IsKeyboardFocusWithin || JobDescBox.IsKeyboardFocusWithin;

        private void HandleSpaceDown(string source)
        {
            if (source != "BUTTON" && IsTypingInTextField()) return;
            if (_engineUsageLimitReached)
            {
                ShowEngineUsageLimitError();
                return;
            }
            if (_spaceHandling || isProcessing || !isMuted) return;
            _spaceHandling = true;
            try
            {
                _listeningInitiator = source;
                _listeningStartTicks = Environment.TickCount64;
                isMuted = false; isListening = true;
                SetResumePanelCollapsed(true, animate: true);
                string latestPath = Path.Combine(AppDataFolder, "latest.txt");
                try { File.Delete(latestPath); } catch { }
                try { File.WriteAllText(latestPath, ""); } catch (Exception ex) { DebugWindow.Log("FILE", $"latest.txt clear failed: {ex.Message}"); }
                try { File.WriteAllText(Path.Combine(AppDataFolder, "reset.flag"), "1"); } catch (Exception ex) { DebugWindow.Log("FILE", $"reset.flag write failed: {ex.Message}"); }
                _justStartedListening = true;
                _listenStartTicks = 0;
                TranscriptTextBlock.Text = "";
                TranscriptHint.Visibility = Visibility.Visible;
                // Manual listening starts a fresh visual turn. Auto modes resume listening
                // immediately after an answer, so keep that answer visible until the next
                // answer actually begins instead of flashing it and clearing it at once.
                if (source != "AUTO")
                {
                    ClearAnswer();
                    if (answerWindow != null) answerWindow.UpdateAnswer("");
                }
                if (answerWindow != null) answerWindow.UpdateQuestion("");
                DeletePauseFlag();
                DebugWindow.Log("MIC", $"[{source}] UNMUTED — listening");
                _lastMicUseUtc = DateTime.UtcNow;
                StartListeningMeter();
                if (AutoModeEnabled) ResetAutoTurnDetection();
                UpdateMicUi();
            }
            finally { _spaceHandling = false; }
        }

        private async void HandleSpaceUp(string source)
        {
            // Auto mode latches _autoTurnSubmitting immediately before calling this, so
            // every early return below has to release it. Without that the latch stays
            // set, TrySubmitAutomaticTurn refuses every later turn, and the restart in
            // UpdateTranscript never runs because it waits for listening to stop, which
            // it never does. Auto mode then goes quiet for the rest of the interview
            // with nothing on screen to say why. Releasing it simply lets the detector
            // try again on the next poll. Reachable in practice when a manual Space
            // lands while an automatic turn is being submitted: _spaceHandling is set
            // by the other handler and this call bails.
            void ReleaseAutoLatch() { if (source == "AUTO") _autoTurnSubmitting = false; }

            if (source != "BUTTON" && IsTypingInTextField()) { ReleaseAutoLatch(); return; }
            if (_spaceHandling || isProcessing || _flushing || isMuted) { ReleaseAutoLatch(); return; }

            // If listening was started by UI button, do NOT let space key release mute it!
            if (source != "BUTTON" && _listeningInitiator == "BUTTON") { ReleaseAutoLatch(); return; }

            // GLOBAL fires HandleSpacePress once per discrete toggle press. A sub-200ms hold
            // is an accidental double-fire, not a real utterance, so re-mute and bail.
            // PREVIEW pairs one physical KeyDown with its own KeyUp (a human tap is ~100-160ms),
            // so the floor is not applied there.
            if (source != "PREVIEW")
            {
                long heldMs = Environment.TickCount64 - _listeningStartTicks;
                if (heldMs < 200)
                {
                    DebugWindow.Log("MIC", $"[{source}] Quick space tap ignored ({heldMs}ms) — re-muting");
                    StopListeningMeter();
                    isListening = false; WritePauseFlag(); isMuted = true;
                    ReleaseAutoLatch();
                    UpdateMicUi();
                    return;
                }
            }

            // Enter the flush phase IMMEDIATELY. Setting _flushing here (instead of holding
            // _spaceHandling across the async grace window) means a Space press during this
            // window routes straight to InterruptAiAndListen and re-listens in ONE press,
            // instead of being swallowed until the AI happened to start.
            // The clock the user actually experiences starts here, when they
            // stop speaking, not when the request is sent.
            _turnStopwatch = Stopwatch.StartNew();
            StopListeningMeter();
            isListening = false; isMuted = true; _flushing = true;
            try { _aiCts?.Dispose(); } catch { }
            _aiCts = new System.Threading.CancellationTokenSource();
            var ct = _aiCts.Token;
            DebugWindow.Log("MIC", $"[{source}] MUTED — flushing final transcript");
            UpdateMicUi();

            // Stop capturing NEW audio, but do NOT set pause.flag yet: Speechmatics runs
            // ~max_delay behind live speech, so the tail of the utterance is still in flight.
            // Poll latest.txt for a brief grace window so the complete question lands first.
            string question = "";
            try
            {
                // Speechmatics runs behind live speech, so after the key is
                // released the tail of the sentence is still arriving. This waits
                // for it, and how long it waits is felt directly: every
                // millisecond here happens before the request is even sent, and
                // the model itself answers in about 0.15s. Measured end to end,
                // this wait was the largest single part of the delay between
                // speaking and reading, larger than the model and the network
                // together.
                //
                // Polling four times more often finds the same moment sooner. The
                // cap is unchanged, and the file being read is a few hundred
                // bytes on local disk.
                question = ReadLatestTxtSafe().Trim();
                int stableCount = 0;
                int emptyCount  = 0;
                for (int i = 0; i < 64; i++)   // 20ms x 64 = the same ~1.28s cap
                {
                    await Task.Delay(20, ct);  // throws if the user interrupted
                    string t = ReadLatestTxtSafe().Trim();
                    if (t.Length > question.Length)
                    {
                        question = t;
                        stableCount = 0;
                        emptyCount  = 0;
                    }
                    else if (!string.IsNullOrWhiteSpace(question))
                    {
                        // Text has stopped growing: the utterance has landed.
                        //
                        // Unless it plainly has not. A question ending on "or",
                        // "to" or "you" with no full stop is a sentence still in
                        // the air, and pressing Space is not proof it finished —
                        // people press it the moment they stop talking, and
                        // often a beat before.
                        //
                        // Every listening mode reads a sentence the same way
                        // now. Auto had this and Press Space did not, so the
                        // mode most people use was the one that cut them off:
                        // "do you know coding or coding language? You" went to
                        // the model exactly like that, and came back an answer
                        // to nothing.
                        //
                        // Waiting is bounded and costs nothing when they really
                        // had finished, because a finished sentence does not
                        // reach this branch at all.
                        stableCount++;
                        int stableNeeded =
                            ClassifyTurnEnding(PromptBuilder.NormalizeInterviewerQuestion(question))
                                == TurnEnding.Unfinished ? 40 : 5;   // 800ms, or 100ms

                        if (stableCount >= stableNeeded)
                        {
                            if (stableNeeded > 5)
                                DebugWindow.Log("MIC",
                                    "Question looked unfinished; waited for the rest before sending.");
                            break;
                        }
                    }
                    else
                    {
                        // Nothing has been transcribed at all. Waiting out the
                        // full cap for a sentence that was never spoken cost more
                        // than a second before the app could even say it had
                        // heard nothing, which is the case where the delay is
                        // most obvious because there is no answer at the end of
                        // it either.
                        emptyCount++;
                        if (emptyCount >= 20) break;   // 400ms of silence
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // User pressed Space during the flush: a fresh listening session already
                // took over in InterruptAiAndListen. Leave that state alone.
                DebugWindow.Log("MIC", $"[{source}] flush interrupted — re-listening");
                return;
            }
            catch (Exception ex) { DebugWindow.Log("MIC", $"HandleSpaceUp flush error: {ex.Message}"); }
            finally { _flushing = false; }

            // A tail joins the question it belongs to. The transcript was cleared
            // when listening restarted, so what arrived here is only "and full
            // time"; sent alone it would be answered as if that were the whole
            // question, which is how it read on screen before this existed.
            if (source == "AUTO" && !string.IsNullOrWhiteSpace(_autoContinuationPrefix))
            {
                if (!string.IsNullOrWhiteSpace(question))
                    question = MergeContinuation(_autoContinuationPrefix, question);
                _autoContinuationPrefix = "";
            }

            if (!string.IsNullOrWhiteSpace(question))
                TranscriptTextBlock.Text = question;
            WritePauseFlag();   // final has landed — safe to pause the engine
            _waitedForWordsMs = _turnStopwatch?.ElapsedMilliseconds ?? 0;
            DebugWindow.Log("MIC", $"[{source}] firing AI ({question.Length} chars)");

            if (string.IsNullOrWhiteSpace(question))
            {
                // Empty because they said nothing is normal and needs no comment.
                // Empty because the speech service never started is not, and it
                // looked identical: press Space, speak, get nothing, with the
                // only clue in a debug window nobody has open.
                if (!_engineOnline)
                {
                    var wait = UserSession.SpeechmaticsRetryAfterUtc - DateTime.UtcNow;
                    AiAnswerBox.Text = wait > TimeSpan.Zero
                        ? $"Speech is still starting up. It should be listening again in about "
                          + (wait.TotalMinutes >= 1
                             ? $"{(int)Math.Ceiling(wait.TotalMinutes)} minute" + (wait.TotalMinutes >= 2 ? "s" : "")
                             : $"{Math.Max(1, (int)wait.TotalSeconds)} seconds")
                          + ". Nothing you said was lost, because nothing was being heard yet."
                        : "Speech has not connected yet, so nothing was heard. It is retrying on its own.";
                    if (answerWindow != null) answerWindow.UpdateAnswer(AiAnswerBox.Text);
                }
                UpdateMicUi();   // nothing said — plain mute
                return;
            }

            try { await AskAiAsync(question, ct); }
            catch (Exception ex) { DebugWindow.Log("MIC", $"HandleSpaceUp ask error: {ex.Message}"); }
        }

        // Space is a single always-responsive toggle. Whatever state we're in, one
        // press does the obvious next thing instantly:
        //   ANSWERING  -> cancel the answer and start listening again (interrupt anytime)
        //   MUTED/IDLE -> start listening
        //   LISTENING  -> stop and fire the AI
        private void HandleSpacePress(string source)
        {
            // Auto owns the listening lifecycle. Global Space presses in an Auto mode used
            // to cancel the active answer and launch many tiny requests, exhausting Groq and
            // forcing a slow fallback. Manual Space behavior is unchanged in Manual mode.
            if (AutoModeEnabled && source != "AUTO")
            {
                ShowListeningModeNotice("AUTO ACTIVE");
                DebugWindow.Log("MODE", $"Ignored {source} Space while {_listeningMode} controls the turn.");
                return;
            }

            // Answering OR flushing the final transcript -> one press cancels and re-listens.
            if (isProcessing || _flushing) { InterruptAiAndListen(source); return; }
            if (isMuted)      HandleSpaceDown(source);
            else              HandleSpaceUp(source);
        }

        // Cancel an in-flight answer (or the transcript-flush window) and immediately begin
        // a fresh listening session, so the user is never "stuck" waiting before they can
        // ask the next thing. This is what makes unmute feel instant, every time.
        private void InterruptAiAndListen(string source)
        {
            if (source != "BUTTON" && IsTypingInTextField()) return;
            DebugWindow.Log("MIC", $"[{source}] interrupt — cancelling, back to listening");
            try { _aiCts?.Cancel(); } catch { }
            isProcessing = false;
            _flushing = false;
            thinkingTimer?.Stop();
            ThinkingPanel.Visibility = Visibility.Collapsed;
            // isMuted is already true during processing/flushing, so this starts listening cleanly.
            HandleSpaceDown(source);
        }

        private void MicBtn_Click(object sender, RoutedEventArgs e) => HandleSpacePress("BUTTON");

        // ══════════════════════════════════════════════════════════════════════
        // AI requests route through the secured backend, where credits are deducted.
        // ══════════════════════════════════════════════════════════════════════
        private async Task AskAiAsync(string? customQuestion = null,
                                      System.Threading.CancellationToken externalCt = default)
        {
            if (isProcessing) return;
            isProcessing = true; // guard: set before any await so no second call can sneak through

            // Reuse the caller's cancellation token when the voice flow already owns one
            // (so a single _aiCts.Cancel() interrupts flush + answer together); otherwise
            // (e.g. the typed AskBox path) create a fresh source for this answer.
            System.Threading.CancellationToken aiCt;
            if (externalCt.CanBeCanceled)
            {
                aiCt = externalCt;
            }
            else
            {
                try { _aiCts?.Dispose(); } catch { }
                _aiCts = new System.Threading.CancellationTokenSource();
                aiCt = _aiCts.Token;
            }

            // Outer try/catch wraps the ENTIRE method body — including setup awaits —
            // so no exception can ever escape this Task unobserved.
            string q = "";
            var streamedAnswer = new StringBuilder();
            try
            {
                string rawQuestion = string.IsNullOrWhiteSpace(customQuestion)
                    ? TranscriptTextBlock.Text.Trim()
                    : customQuestion.Trim();
                q = PromptBuilder.NormalizeInterviewerQuestion(rawQuestion);
                if (string.IsNullOrWhiteSpace(q)) { isProcessing = false; UpdateMicUi(); return; }
                if (!string.Equals(rawQuestion, q, StringComparison.Ordinal))
                    DebugWindow.Log("AI", $"Ignored opening transcript filler: {rawQuestion.Length - q.Length} chars");
                var answerTimer = System.Diagnostics.Stopwatch.StartNew();

                if (UserSession.IsLoggedIn) await UserSession.TryRefreshAsync();

                // Block once credits have been fetched and confirmed exhausted.
                // _creditsFetched prevents false-blocking before the first backend response.
                if (!UserSession.IsUnlimited && _creditsFetched && UserSession.Credits < CreditsCriticalThreshold)
                {
                    // This fires the instant a user asks a question, so it used to
                    // put a Windows dialog on screen in the middle of a live,
                    // possibly shared, interview. Shown in-app instead.
                    ShowInAppAlert(
                        $"You have {UserSession.Credits} credit{(UserSession.Credits == 1 ? "" : "s")} left",
                        "Open the Replysis pricing page to top up. Your session stays open.");
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

                // The answer panel shows ONLY the current answer. The question already
                // appears in the Interviewer transcript, so we don't repeat a "Q:" label
                // here, and each new question replaces the previous answer instead of
                // stacking old ones underneath.
                ClearAnswer();
                if (answerWindow != null) { answerWindow.UpdateAnswer(""); answerWindow.UpdateQuestion(q); }

                int tokenCount = 0;

                await foreach (var token in StreamFromBackend(q, ResumeTextBox.Text, aiCt))
                {
                    aiCt.ThrowIfCancellationRequested();
                    streamedAnswer.Append(token); tokenCount++;
                    if (tokenCount == 1)
                    {
                        thinkingTimer?.Stop();
                        ThinkingPanel.Visibility = Visibility.Collapsed;
                        long total = _turnStopwatch?.ElapsedMilliseconds ?? 0;
                        long model = answerTimer.ElapsedMilliseconds;
                        DebugWindow.Log("SPEED",
                            $"stopped speaking -> first word: {total}ms  " +
                            $"(waiting for transcript {_waitedForWordsMs}ms, " +
                            $"network + model {model}ms)");
                    }
                    // Paint the first token immediately. After that, repaint every two
                    // tokens (or on a newline) to keep the stream smooth without
                    // thrashing the UI thread.
                    if (tokenCount == 1 || tokenCount % 2 == 0 || token.Contains('\n'))
                    {
                        string soFar = CleanAiOutput(streamedAnswer.ToString());
                        ShowAnswer(soFar, scrollToEnd: true);
                        if (answerWindow != null) answerWindow.UpdateAnswer(soFar);
                    }
                }

                string final = CleanAiOutput(streamedAnswer.ToString());
                if (string.IsNullOrWhiteSpace(final))
                    throw new BackendRequestException("No answer was returned. Please try again.");
                ShowAnswer(final, scrollToEnd: false);
                AiAnswerBox.ScrollToHome();   // land at the top so the answer reads from the start
                if (answerWindow != null) { answerWindow.UpdateAnswer(final); answerWindow.UpdateQuestion(q); }
                PromptBuilder.AddToHistory(q, final);
                AppendToSessionLog(q, final);
                DebugWindow.Log("AI", $"Done — {tokenCount} tokens");

                // Refresh credits display after AI call
                _ = FetchAndDisplayCreditsAsync().ContinueWith(t => {
                    if (t.IsFaulted) DebugWindow.Log("CREDITS_ERR", t.Exception?.GetBaseException().Message ?? "unknown");
                }, TaskScheduler.Default);
            }
            catch (OperationCanceledException)
            {
                // User pressed Space to interrupt this answer — a fresh listening session
                // has already taken over. Stay silent: no error text, no partial dump.
                DebugWindow.Log("AI", "Answer interrupted by user.");
            }
            catch (BackendRequestException ex)
            {
                DebugWindow.Log("AI_ERR", ex.Message);
                string partial = CleanAiOutput(streamedAnswer.ToString());
                AiAnswerBox.Text = string.IsNullOrWhiteSpace(partial)
                    ? ex.Message
                    : $"{partial}\n\n{ex.Message}";
                if (answerWindow != null)
                    answerWindow.UpdateAnswer(AiAnswerBox.Text);
                _ = FetchAndDisplayCreditsAsync();
            }
            catch (Exception ex)
            {
                // The type and the inner exception, not just the message. A
                // message alone cannot distinguish a dropped socket from a
                // rejected payload from a bug in our own capture path, and those
                // need completely different fixes. Several failures have already
                // been diagnosed twice over because this line threw away the one
                // fact that identified them.
                DebugWindow.Log("AI_ERR",
                    $"{ex.GetType().Name}: {ex.Message}" +
                    (ex.InnerException is null ? "" : $"  <- {ex.InnerException.GetType().Name}: {ex.InnerException.Message}"));

                string partial = CleanAiOutput(streamedAnswer.ToString());

                // "Connection interrupted" used to cover every failure that was not
                // a BackendRequestException, including the ones that say exactly
                // what went wrong and what to do: out of credits, signed out,
                // asking too fast. Telling someone their connection dropped when
                // their credits ran out sends them to check their wifi.
                string failure = ex is InvalidOperationException && !string.IsNullOrWhiteSpace(ex.Message)
                    ? ex.Message
                    : "Connection interrupted. Please try again.";
                AiAnswerBox.Text = string.IsNullOrWhiteSpace(partial)
                    ? failure
                    : $"{partial}\n\n{failure}";
                if (answerWindow != null)
                    answerWindow.UpdateAnswer(string.IsNullOrWhiteSpace(partial)
                        ? failure
                        : $"{partial}\n\n{failure}");
            }
            finally
            {
                // Only tear down the thinking UI if THIS answer is still the active one.
                // If the user already interrupted and started a new listening session,
                // leave that state alone so we don't stomp the live mic UI.
                if (!isListening) StopThinkingUi();
                else { isProcessing = false; }
            }
        }

        /// <summary>
        /// Captures the screen with our own windows kept out of the picture,
        /// returning null if the capture fails.
        ///
        /// Shared by the Analyze hotkey and by questions that turn out to be
        /// about the screen, so both get the same instant, blink-free capture.
        /// </summary>
        // ══════════════════════════════════════════════════════════════════════
        // SCREENSHOT TAKEN BEFORE IT IS ASKED FOR
        //
        // Everything between the question ending and the answer starting is time
        // the candidate spends silent, and capturing the screen sits right in the
        // middle of it: hide our own windows, wait for the frame, grab the pixels,
        // downscale them, encode a PNG, then encode a second one to compare sizes.
        // None of that depends on the question.
        //
        // In Watch Screen mode every question is a screen question, so it can all
        // happen beforehand and be waiting. The measured cost of the capture then
        // leaves the path entirely and what is left is the upload and the model,
        // which answered in 0.72s when timed directly.
        //
        // Deliberately only in Watch Screen mode. Grabbing the screen on a timer
        // when the user has not asked for that is not a performance decision, and
        // it is not one to make quietly on their behalf.
        // ══════════════════════════════════════════════════════════════════════
        private static readonly TimeSpan PreparedShotInterval = TimeSpan.FromSeconds(2);

        // How stale a prepared shot may be and still be used. An interview screen
        // is a problem statement that sits still; four seconds of it is the same
        // screen. Past that it is captured fresh, because being fast about the
        // wrong screen is worse than being slow about the right one.
        private static readonly TimeSpan PreparedShotMaxAge = TimeSpan.FromMilliseconds(1_500);

        private DispatcherTimer? _preparedShotTimer;
        private byte[]? _preparedShot;
        private DateTime _preparedShotUtc = DateTime.MinValue;
        private volatile bool _preparingShot;

        // How recently the microphone must have been used for screen preparation
        // to be worth doing. Long enough to cover a gap between questions, short
        // enough that an app left open all afternoon captures nothing.
        private static readonly TimeSpan PrepareShotsAfterMicWithin = TimeSpan.FromMinutes(5);

        private DateTime _lastMicUseUtc = DateTime.MinValue;
        private bool _warnedNoCloak;

        // Whether this machine can hide the app from a capture. Null until asked.
        private bool? _canHideFromCapture;

        // How many more questions may be treated as follow-ups about the screen
        // without saying so. Three covers the run that actually happens after
        // "solve this" — the complexity, the edge case, the alternative — and
        // stops a conversation that has moved on from still being answered with
        // a photograph of a code editor.
        private const int ScreenFollowUpBudget = 3;
        private int _screenFollowUpsLeft;

        // The commit and source hash the running engine was built from, as it
        // reported them on startup. "unknown" means it has not spoken yet.
        private string _engineBuildId = "unknown";

        private void StartPreparedShots()
        {
            if (_preparedShotTimer == null)
            {
                _preparedShotTimer = new DispatcherTimer { Interval = PreparedShotInterval };
                _preparedShotTimer.Tick += (_, _) => _ = PrepareShotAsync();
            }
            _preparedShotTimer.Start();
            _ = PrepareShotAsync();   // one straight away, so the first question benefits
        }

        private void StopPreparedShots()
        {
            _preparedShotTimer?.Stop();
            _preparedShot = null;
            _preparedShotUtc = DateTime.MinValue;
        }

        private async Task PrepareShotAsync()
        {
            // Never while an answer is being produced: the capture would fight the
            // request for the network, and cloaking our own windows mid-answer is
            // visible to the user.
            if (!_watchScreenMode || _preparingShot || isProcessing || _isScreenAnalyzing) return;

            // And never when no interview is happening.
            //
            // Screen answers became the default, which quietly turned this into
            // a screenshot every two seconds for as long as the app was open —
            // uploaded whenever the picture changed, which while somebody is
            // working is constantly. Nobody asked for that, it is their screen,
            // and it is around eleven megabytes a minute of their connection.
            //
            // The microphone having been used recently is what "an interview is
            // happening" means here. Open the app and leave it, and nothing is
            // captured at all.
            if (DateTime.UtcNow - _lastMicUseUtc > PrepareShotsAfterMicWithin) return;

            // If our own windows cannot be hidden from a capture, the fallback
            // drops the opacity to zero for ninety milliseconds. Once, before an
            // answer, that is invisible. Every two seconds, forever, it is a
            // flickering window. On such a machine the screen is read on demand
            // only, which costs a fraction of a second and nothing else.
            // Asked once. The probe sets the exclusion flag and puts it back, so
            // running it every two seconds would churn that flag forever and
            // leave brief windows where the app really is capturable.
            _canHideFromCapture ??= WindowStealth.CanHideFromCapture(this);

            if (_canHideFromCapture == false)
            {
                if (!_warnedNoCloak)
                {
                    _warnedNoCloak = true;
                    DebugWindow.Log("SCREEN",
                        "This machine cannot hide the app from a capture; preparing screenshots is off "
                        + "to avoid flicker. F8 still reads the screen on demand.");
                }
                return;
            }

            _preparingShot = true;
            try
            {
                byte[]? shot = await CaptureScreenUnseenAsync();
                if (shot != null && shot.Length > 0)
                {
                    _preparedShot = shot;
                    _preparedShotUtc = DateTime.UtcNow;
                    await UploadPreparedShotAsync(shot);
                }
            }
            catch { /* a missed prepared shot just means capturing on demand */ }
            finally { _preparingShot = false; }
        }

        // The picture, sent while nobody is waiting.
        //
        // Timed on a real capture: 1,483ms from the candidate stopping speaking
        // to the first word, of which the model was 720ms. Most of the rest was
        // the screenshot going up the wire, and taking it early only removed the
        // capture, not the upload.
        //
        // So the upload moves too. The shot is already taken before the question
        // exists, so it is sent then, and what travels on the path that matters
        // is an id short enough to fit in a tweet.
        //
        // The server holds it in memory for ninety seconds, hands it back only
        // to the identity that sent it, and gives it back exactly once.
        private string _preparedShotId = "";
        private DateTime _preparedShotIdUtc = DateTime.MinValue;

        // The last few views of the screen, oldest first.
        //
        // A coding problem rarely fits on one screen. The candidate scrolls to
        // read it, and any single screenshot then holds the statement or the
        // constraints but not both, so the answer was built from half a
        // question with no way to tell from reading it.
        //
        // Keeping the views that differ turns scrolling into exactly the right
        // behaviour: by the time they ask, the app has seen the whole problem,
        // in the order they read it.
        private readonly List<(string Id, int Bytes)> _recentShotIds = new();

        // How much picture one question may carry.
        //
        // Raising the full-screen resolution so the text could be read took a
        // capture to about 380 KB. Three of those is a megabyte and a half of
        // base64, which the provider refuses outright with a 413, and the
        // candidate reads "temporarily unavailable" with an interviewer
        // waiting. Views are added newest-first until this is reached, so the
        // screen they are looking at now is always the one that fits.
        //
        // 700 KB was wrong and quietly disabled the whole feature. A full-screen
        // capture is about 384 KB, so two of them is 768 KB, so the second was
        // always dropped and every question went out with a single view. The log
        // said nothing, because the line that reports multiple views only prints
        // when there is more than one.
        //
        // Measured against the provider directly: 1.9 MB of base64 in one request
        // is accepted. Two captures is roughly 1 MB of PNG, near 1.4 MB encoded,
        // which sits inside that with room for the prompt. Two views is also what
        // the problem actually needs — the half above the fold and the half
        // below — and the server drops to the newest one if a request is refused
        // anyway.
        private const int MaxTotalShotBytes = 1_000 * 1024;

        // Three is a scrolled problem statement. More is a screen recording,
        // and the cost of reading them lands on somebody waiting to speak.
        private const int MaxShotsPerQuestion = 3;

        // How much of the coarse signature must differ before two captures count
        // as different parts of the page. Sixteen of 256 cells is a scroll; a
        // counter ticking over moves none of them.
        private const int MinSignatureChange = 16;

        private string _lastKeptSignature = "";

        // Its own lifetime, shorter than the server's ninety seconds, so the app
        // gives up on an id before the server does rather than sending one that
        // has just gone.
        private static readonly TimeSpan PreparedShotIdMaxAge = TimeSpan.FromSeconds(60);

        // What was last sent, so the same still screen is not sent again.
        private string _uploadedShotFingerprint = "";

        /// <summary>How many of the 256 cells differ. 256 when either is missing.</summary>
        private static int SignatureDistance(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a.Length != b.Length) return 256;

            int differing = 0;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) differing++;
            return differing;
        }

        private async Task UploadPreparedShotAsync(byte[] shot)
        {
            try
            {
                // An interview screen sits still. A problem statement is read for
                // minutes, and every two seconds the app was sending the same
                // pixels again, purely to hold an id it already had.
                //
                // Only a screen that changed is worth sending. The id already on
                // hand still points at an identical picture, so nothing is lost
                // by staying quiet, and the traffic drops to what actually
                // happens rather than what the timer does.
                string fingerprint = Convert.ToBase64String(
                    System.Security.Cryptography.SHA256.HashData(shot));
                if (fingerprint == _uploadedShotFingerprint && !string.IsNullOrEmpty(_preparedShotId))
                {
                    // Keep the existing id alive against its own clock.
                    _preparedShotIdUtc = DateTime.UtcNow;
                    return;
                }

                var sw = Stopwatch.StartNew();
                using var req = new HttpRequestMessage(HttpMethod.Post,
                    $"{BackendUrl}/api/v1/interview/screen-cache");
                if (!string.IsNullOrEmpty(UserSession.IdToken))
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {UserSession.IdToken}");
                req.Headers.TryAddWithoutValidation("X-Device-Id", DeviceIdentity.Current);
                req.Content = new StringContent(
                    JsonSerializer.Serialize(new { image = Convert.ToBase64String(shot) }),
                    System.Text.Encoding.UTF8, "application/json");

                using var res = await _creditsClient.SendAsync(req);
                if (!res.IsSuccessStatusCode)
                {
                    // Not worth a word to the user: the next question simply
                    // sends the bytes the old way and is a little slower.
                    DebugWindow.Log("SCREEN", $"Early upload declined: HTTP {(int)res.StatusCode}");
                    _preparedShotId = "";
                    _uploadedShotFingerprint = "";
                    return;
                }

                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                _preparedShotId = doc.RootElement.TryGetProperty("imageId", out var id)
                    ? id.GetString() ?? "" : "";
                _preparedShotIdUtc = DateTime.UtcNow;
                _uploadedShotFingerprint = fingerprint;

                if (!string.IsNullOrEmpty(_preparedShotId))
                {
                    // Only a screen that moved is a second view worth sending.
                    //
                    // A page with a live "2,332 Online" counter and a blinking
                    // caret produces a different image every two seconds, so
                    // every capture was kept as a new view and every question
                    // carried two pictures of the same screen. Twice the tokens
                    // for nothing, against an allowance of eight thousand a
                    // minute, which is why testing kept ending in "temporarily
                    // unavailable".
                    string signature = ScreenAnalyzer.LastCaptureSignature;
                    bool pageMoved = SignatureDistance(signature, _lastKeptSignature) >= MinSignatureChange;

                    if (pageMoved || _recentShotIds.Count == 0)
                    {
                        _lastKeptSignature = signature;
                        _recentShotIds.Add((_preparedShotId, shot.Length));
                        Dispatcher.Invoke(RescanAfterScrollIfArmed);
                    }
                    else
                    {
                        // Same view, newer picture: replace rather than add.
                        _recentShotIds[^1] = (_preparedShotId, shot.Length);
                    }
                    while (_recentShotIds.Count > MaxShotsPerQuestion)
                        _recentShotIds.RemoveAt(0);
                }
                DebugWindow.Log("SCREEN",
                    $"Screenshot sent ahead in {sw.ElapsedMilliseconds}ms; the question now carries an id.");
            }
            catch (Exception ex)
            {
                DebugWindow.Log("SCREEN", $"Early upload failed: {ex.Message}");
                _preparedShotId = "";
            }
        }

        /// <summary>
        /// The id of a screenshot already sitting on the server, or empty when
        /// there is none to use.
        /// </summary>
        /// <summary>
        /// Every view held on the server for this question, oldest first, or an
        /// empty list when there is nothing usable.
        /// </summary>
        private List<string> TakePreparedShotIds()
        {
            var ids = new List<string>();
            if (string.IsNullOrEmpty(_preparedShotId)) return ids;
            if (_recentShotIds.Count == 0) { ids.Add(_preparedShotId); _preparedShotId = ""; return ids; }
            if (DateTime.UtcNow - _preparedShotIdUtc > PreparedShotIdMaxAge) return ids;
            if (DateTime.UtcNow - _preparedShotUtc > PreparedShotMaxAge) return ids;

            // The newest view and the oldest one, not the last two.
            //
            // Someone reading a problem scrolls from the top down, so the oldest
            // view held is the top of the statement and the newest is wherever
            // they are now. Taking the last two instead loses the statement
            // whenever they scroll slowly enough to fill the buffer — three
            // views of the bottom half, and the question itself gone.
            //
            // The newest is added first so it is never the one dropped for size:
            // it is the screen they are actually looking at.
            int total = 0;
            var chosen = new List<(string Id, int Bytes)>();

            var newest = _recentShotIds[^1];
            chosen.Add(newest);
            total += newest.Bytes;

            if (_recentShotIds.Count > 1)
            {
                var oldest = _recentShotIds[0];
                if (total + oldest.Bytes <= MaxTotalShotBytes)
                {
                    chosen.Insert(0, oldest);   // top of the page first
                    total += oldest.Bytes;
                }
            }

            foreach (var (id, _) in chosen) ids.Add(id);

            if (!ids.Contains(_preparedShotId)) ids.Add(_preparedShotId);

            // Each id is good for one question on the server too, so the whole
            // set is spent together and the next question starts fresh.
            _recentShotIds.Clear();
            _preparedShotId = "";

            DebugWindow.Log("SCREEN", ids.Count > 1
                ? $"Sending {ids.Count} views of the screen, as scrolled ({total / 1024} KB)."
                : $"Sending 1 view ({total / 1024} KB); no earlier view fit the budget.");
            return ids;
        }

        /// <summary>
        /// The prepared screenshot when there is a fresh one, otherwise a new
        /// capture. Same bytes either way; the only difference is who waited.
        /// </summary>
        private async Task<byte[]?> GetScreenshotForAnswerAsync()
        {
            byte[]? prepared = _preparedShot;
            if (prepared != null && DateTime.UtcNow - _preparedShotUtc <= PreparedShotMaxAge)
            {
                int ageMs = (int)(DateTime.UtcNow - _preparedShotUtc).TotalMilliseconds;
                DebugWindow.Log("SCREEN", $"Using a screenshot taken {ageMs}ms ago; no capture wait.");
                return prepared;
            }
            return await CaptureScreenUnseenAsync();
        }

        /// <summary>
        /// Where the private notes section starts, or -1.
        ///
        /// Matches a line that is exactly "SCREEN" or begins "SCREEN NOTES".
        /// Written as a line test rather than a phrase test because the model
        /// does not always finish the heading, and a half-written one used to
        /// reach the user: an answer that ended on the word "SCREEN".
        ///
        /// A spoken reply does not begin a line with SCREEN in capitals, so
        /// this cannot swallow anything the candidate was meant to say.
        /// </summary>
        private static int IndexOfNotesHeading(StringBuilder buffer)
        {
            string text = buffer.ToString();
            int from = 0;
            while (true)
            {
                int at = text.IndexOf("SCREEN", from, StringComparison.Ordinal);
                if (at < 0) return -1;

                // A line beginning with SCREEN in capitals is the heading,
                // whatever follows it on that line.
                //
                // The real failure was not a tidy "SCREEN NOTES" on its own
                // line. The model wrote "SCREEN what is this claude is open and
                // behind that vs studio", echoing the question straight into
                // the answer, and requiring the word NOTES or a line break
                // missed exactly that.
                bool atLineStart = at == 0 || text[at - 1] == '\n' || text[at - 1] == '\r';
                if (atLineStart) return at;
                from = at + 1;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // ANSWERING AGAIN ONCE THEY HAVE SCROLLED
        //
        // The answer asked for the rest of the page — "let me scroll down and
        // read the constraints" — and then nothing happened. The candidate
        // scrolled, and waited, and no better answer came, because the app only
        // answers when it is asked and scrolling is not asking.
        //
        // That is the app telling somebody to do something and then ignoring
        // them for doing it. Having asked for the rest of the page, it waits for
        // the screen to change and answers the same question again, now with
        // both halves.
        //
        // Once only, and briefly. Answering repeatedly as somebody scrolls
        // through a document would spend a credit per scroll and put a new
        // answer on screen every two seconds while they are trying to read.
        // ══════════════════════════════════════════════════════════════════════
        private static readonly TimeSpan RescanWindow = TimeSpan.FromSeconds(25);

        private string _rescanQuestion = "";
        private DateTime _rescanArmedUtc = DateTime.MinValue;

        /// <summary>
        /// Phrases the answer uses when it cannot see all of the question. NEED:
        /// is the marker the prompt asks for; the rest are what the model
        /// writes when it follows the spirit and not the letter.
        /// </summary>
        private static readonly string[] AskedForMorePage =
        {
            "NEED:", "scroll down", "scroll up", "see the rest", "read the rest",
            "the constraints before", "rest of the examples", "rest of the question",
        };

        private void ArmRescanIfAnswerAskedToScroll(string answer, string question)
        {
            if (string.IsNullOrWhiteSpace(answer) || string.IsNullOrWhiteSpace(question)) return;

            bool askedForMore = AskedForMorePage.Any(
                phrase => answer.Contains(phrase, StringComparison.OrdinalIgnoreCase));
            if (!askedForMore) return;

            _rescanQuestion = question;
            _rescanArmedUtc = DateTime.UtcNow;
            DebugWindow.Log("SCREEN", "Answer asked for the rest of the page; will answer again once it changes.");

            Dispatcher.Invoke(() => ShowListeningModeNotice("SCROLL — I WILL ANSWER AGAIN"));
        }

        /// <summary>
        /// Called when a newly captured screen differs from the last one. Answers
        /// the same question again if that is what the previous answer asked for.
        /// </summary>
        private void RescanAfterScrollIfArmed()
        {
            if (string.IsNullOrEmpty(_rescanQuestion)) return;

            if (DateTime.UtcNow - _rescanArmedUtc > RescanWindow)
            {
                _rescanQuestion = "";
                return;
            }

            // Never while an answer is already being produced, or while they are
            // speaking: they have moved on and this would talk over them.
            if (isProcessing || isListening || _flushing) return;

            string question = _rescanQuestion;
            _rescanQuestion = "";
            DebugWindow.Log("SCREEN", "Screen changed; answering the same question with the rest of the page.");
            _ = AskAiAsync(question);
        }

        private async Task<byte[]?> CaptureScreenUnseenAsync()
        {
            bool answerWasVisible = answerWindow?.IsVisible == true;
            AnswerWindow? cloakedAnswer = answerWasVisible ? answerWindow : null;

            bool mainCloaked = WindowStealth.TryBeginCaptureHidden(this, out bool mainWasExcluded);
            bool answerCloaked = true, answerWasExcluded = true;
            if (cloakedAnswer != null)
                answerCloaked = WindowStealth.TryBeginCaptureHidden(cloakedAnswer, out answerWasExcluded);

            bool cloaked = mainCloaked && answerCloaked;
            double savedOpacity = this.Opacity;

            if (!cloaked)
            {
                this.Opacity = 0;
                if (cloakedAnswer != null) cloakedAnswer.Opacity = 0;
                await WaitForRenderedFrameAsync();
                await Task.Delay(90);
            }
            else if (!mainWasExcluded || !answerWasExcluded)
            {
                await WaitForRenderedFrameAsync();
            }

            try
            {
                var sw = Stopwatch.StartNew();
                // Watching means the whole monitor. Pointing at one window is
                // for F8, where the user chose the window by being in it.
                bool wholeScreen = _watchScreenMode;
                byte[]? shot = await Task.Run(() => ScreenAnalyzer.CaptureScreen(wholeScreen));
                DebugWindow.Log("SCREEN",
                    $"Capture+encode took {sw.ElapsedMilliseconds}ms ({(shot?.Length ?? 0) / 1024} KB)");
                return shot;
            }
            catch (Exception ex)
            {
                DebugWindow.Log("SCREEN_ERR", ex.Message);
                return null;
            }
            finally
            {
                WindowStealth.EndCaptureHidden(this, mainWasExcluded);
                WindowStealth.EndCaptureHidden(cloakedAnswer, answerWasExcluded);
                if (!cloaked)
                {
                    this.Opacity = savedOpacity;
                    if (cloakedAnswer != null) cloakedAnswer.Opacity = 1.0;
                }
            }
        }

        // ── Streaming iterator — NO try/catch wrapping yield statements ────────
        private async IAsyncEnumerable<string> StreamFromBackend(
            string question, string resume,
            [EnumeratorCancellation] System.Threading.CancellationToken ct = default)
        {
            // Fast-path: local responses that need no network call
            if (PromptBuilder.IsGreeting(question)) { yield return PromptBuilder.GetGreetingResponse(); yield break; }
            if (PromptBuilder.IsSmallTalk(question)) { yield return PromptBuilder.GetSmallTalkResponse(); yield break; }

            // Some questions cannot be answered from the words in them. An
            // interviewer who says "have a look at this and walk me through it"
            // has put everything that matters on the screen and almost nothing in
            // the sentence, so a text model receives a request with the substance
            // missing and fills the gap confidently, which in the middle of an
            // interview is the worst thing it could do.
            //
            // So the screen is captured and the question is answered from the
            // picture instead. This sits in the one funnel every answer passes
            // through, which means it works the same whether the user pressed
            // Space or the app is running the turn itself in Auto.
            // Watching a screen does not make every question about it. A
            // behavioural question sent down this path comes back as an answer
            // about a code editor, in the wrong shape, having paid to read a
            // picture nobody asked about.
            // The screen is used when the question is about the screen, not
            // whenever the screen happens to be being watched.
            //
            // This was the wrong way round: everything except a short list of
            // personal questions went down the screen path. So "tell me what is
            // Java?" was answered by sending a photograph of the desktop — three
            // times the tokens, a worse answer, and it exhausted the minute's
            // allowance on a question that never needed a picture. Watching
            // became a tax on every question rather than a feature for some.
            //
            // A question is about the screen when it says so — "solve this",
            // "this error", "can you see" — or when the last answer came from
            // the screen and this one continues it, which is how "and what is
            // the time complexity?" keeps working after "solve this".
            // Both clauses check the setting. The first one did not, which meant
            // a user who had turned screen answers off and then said "can you
            // solve this?" still had their screen captured and uploaded — the
            // opposite of what they had just asked for. It was not a decision,
            // it was the order the clauses happened to be written in. Pressing
            // F8 still reads the screen whatever the setting says, because that
            // is someone deliberately asking for it rather than a phrase caught
            // in passing.
            bool askedAboutScreen =
                _watchScreenMode && PromptBuilder.RefersToScreen(question);

            // A follow-up keeps looking at the same thing, but not forever.
            //
            // This used to be a sticky bool with no bound: once an answer came
            // from the screen, every later question that was not on the personal
            // list came from the screen too, however much the conversation had
            // moved on. The limit is a turn count rather than a stopwatch
            // because what ends the topic is drift, not elapsed time — "what is
            // the complexity?" three minutes later is still about the screen,
            // and "so tell me about yourself" ten seconds later is not.
            bool continuesScreen =
                _watchScreenMode
                && _screenFollowUpsLeft > 0
                && !PromptBuilder.IsPersonalQuestion(question);

            bool aboutTheScreen = askedAboutScreen || continuesScreen;

            // Asking about the screen refills the follow-up budget; spending a
            // follow-up draws it down; anything else ends the topic. Tracked as
            // a count here rather than guessed from the question text, because
            // the helper for that only matched questions containing the word
            // "screen", which "can you solve this?" does not.
            if (askedAboutScreen)     _screenFollowUpsLeft = ScreenFollowUpBudget;
            else if (continuesScreen) _screenFollowUpsLeft--;
            else                      _screenFollowUpsLeft = 0;

            if (aboutTheScreen)
            {
                byte[]? shot = await GetScreenshotForAnswerAsync();
                if (shot != null)
                {
                    DebugWindow.Log("SCREEN",
                        $"Question is about the screen; answering from a {shot.Length / 1024} KB capture " +
                        $"of {ScreenAnalyzer.LastCaptureTarget}");

                    // SCREEN NOTES is the model's private record of what was on
                    // screen, kept so the next question has something to work
                    // from. It leaked into the spoken answer here because this
                    // path streams straight to the display and never passes
                    // through the post-processor that strips it. The stream is
                    // still consumed to the end after the marker, so the notes
                    // reach the context they exist for.
                    var visionSoFar = new StringBuilder();
                    var visionClock = Stopwatch.StartNew();
                    long visionFirstTokenMs = -1;
                    bool reachedNotes = false;

                    await foreach (var visionToken in
                        ScreenAnalyzer.AnalyzeStreamAsync(shot, ResumeParser.ExtractFacts(resume), question,
                                                          TakePreparedShotIds(), ct))
                    {
                        if (reachedNotes) continue;

                        if (visionFirstTokenMs < 0 && !string.IsNullOrEmpty(visionToken))
                        {
                            visionFirstTokenMs = visionClock.ElapsedMilliseconds;
                            DebugWindow.Log("SCREEN",
                                $"First word back in {visionFirstTokenMs}ms after the question landed.");
                        }

                        visionSoFar.Append(visionToken);

                        // Cut at the heading, however the model writes it.
                        //
                        // This looked only for "SCREEN NOTES" and the model wrote
                        // "SCREEN" on its own line, so the heading was shown to the
                        // user as the last word of their answer. The notes are for
                        // the next question, never for this one, so the line itself
                        // is the marker rather than the exact phrase.
                        int notesAt = IndexOfNotesHeading(visionSoFar);
                        if (notesAt < 0)
                        {
                            yield return visionToken;
                            continue;
                        }

                        // The marker can arrive split across tokens, so trim from
                        // the accumulated text rather than from this token alone.
                        reachedNotes = true;
                        int alreadyShown = visionSoFar.Length - visionToken.Length;
                        if (notesAt > alreadyShown)
                            yield return visionSoFar.ToString(alreadyShown, notesAt - alreadyShown);
                    }

                    ArmRescanIfAnswerAskedToScroll(visionSoFar.ToString(), question);
                    DebugWindow.Log("SCREEN",
                        $"Screen answer finished in {visionClock.ElapsedMilliseconds}ms.");
                    yield break;
                }
                // Capture failed. Fall through and answer from the words alone,
                // which is weak but better than returning nothing mid-interview.
                DebugWindow.Log("SCREEN", "Capture failed; answering from the transcript alone.");
            }

            // ── 1. Send request — errors handled outside the iterator ──────────
            using HttpResponseMessage res = await SendBackendRequestAsync(question, resume, ct);

            // ── 2. Handle non-200 status codes with plain yields (no try/catch) ─
            int status = (int)res.StatusCode;
            if (status == 402)
                throw new BackendRequestException("Not enough credits. Open Replysis AI pricing to continue.");
            if (status == 401)
            {
                UserSession.Clear();
                Dispatcher.Invoke(() => { _ = SwitchToGuestSessionAsync(); });
                throw new BackendRequestException("Your sign-in expired. Guest mode is ready; sign in again when convenient.");
            }
            if (!res.IsSuccessStatusCode)
            {
                // Keep the real status for diagnosis; the user gets plain language.
                DebugWindow.Log("AI_ERR", $"Answer request failed with HTTP {status}");
                throw new BackendRequestException(FriendlyBackendMessage(status));
            }

            // ── 3. Stream SSE lines — collect then yield (no try/catch around yield) ─
            await foreach (string token in StreamSseTokensAsync(res, ct))
                yield return token;
        }

        // Handles the HTTP request; exceptions bubble up to AskAiAsync's catch block.
        /// <summary>
        /// Fire-and-forget lightweight request that keeps the answer backend's container
        /// warm so the next real question doesn't pay a cold-start. Cheap GET, short
        /// timeout, all errors swallowed — this must never affect the UI or throw.
        /// </summary>
        private async Task WarmBackendAsync()
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{BackendUrl}/health");
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));
                using var res = await _creditsClient.SendAsync(req, cts.Token);
                // Any response (even 404) means the container is awake — that's all we need.
            }
            catch { /* offline / cold / 404 — nothing to do, this is best-effort */ }
        }

        private async Task<HttpResponseMessage> SendBackendRequestAsync(
            string question, string resume, System.Threading.CancellationToken ct)
        {
            PromptBuilder.SetContext(_liveHints, _companyName, _jobDescription);
            string resumeFacts = ResumeParser.ExtractFacts(resume);
            var messages = PromptBuilder.BuildMessages(resumeFacts, question, AutoModeEnabled);
            var provider = SettingsWindow.IsGroq() ? "groq" : "openai";

            // The raw resume is no longer sent at all.
            //
            // The backend reads that field in one place only: a fallback for when
            // the client sends no messages array. This client always sends one,
            // and it already carries the curated resume facts, so the field was
            // uploaded with every question and dropped on arrival. Up to 30KB of
            // it, over a home connection, before the answer could even be asked
            // for.
            //
            // Auto mode had already stopped sending it for exactly this reason.
            // Manual mode, which is the default, kept paying for it every time.
            string transportResume = string.Empty;
            var payload = new { question, resume = transportResume, provider, messages };
            string payloadJson = JsonSerializer.Serialize(payload);
            DebugWindow.Log("AI", $"Request prepared: {messages.Count} messages, {Encoding.UTF8.GetByteCount(payloadJson)} bytes");
            // An hour-old token is rejected, and the 401 handler treats that as a
            // sign-out: plan lost, guest credits, mid-answer.
            await UserSession.EnsureFreshTokenAsync();

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{BackendUrl}/api/v1/interview/ask");
            if (!string.IsNullOrEmpty(UserSession.IdToken))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {UserSession.IdToken}");
            request.Headers.TryAddWithoutValidation("X-Device-Id", DeviceIdentity.Current);
            request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

            // One silent retry on a network failure.
            //
            // Wifi drops a packet, a phone hotspot switches cell, a VPN
            // reconnects: all of it lasts a moment and all of it lost the
            // question outright, telling someone mid-interview to check their
            // connection and ask again. They cannot ask again. The interviewer
            // has moved on.
            //
            // Retrying here is safe in a way retrying later would not be. Nothing
            // has been streamed yet, so the server saw no answer delivered and
            // refunded the credit itself, and a second attempt cannot bill twice
            // or duplicate half an answer on screen.
            Exception? firstFailure = null;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var requestTimer = Stopwatch.StartNew();
                    HttpResponseMessage response = await _backendClient.SendAsync(
                        attempt == 1 ? request : CloneRequest(request, payloadJson),
                        HttpCompletionOption.ResponseHeadersRead, ct);
                    DebugWindow.Log("AI", $"Backend headers in {requestTimer.ElapsedMilliseconds}ms (HTTP {(int)response.StatusCode}).");
                    return response;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // User interrupted with Space. Let this propagate so AskAiAsync's
                    // cancellation catch stays silent instead of showing a fake 503 error.
                    throw;
                }
                catch (Exception ex)
                {
                    firstFailure = ex;
                    DebugWindow.Log("AI_ERR", $"{ex.GetType().Name}: {ex.Message}" +
                                              (attempt == 1 ? " — retrying once" : ""));
                    if (attempt == 2) break;
                    await Task.Delay(250, ct);
                }
            }

            throw new BackendRequestException(
                "The answer service could not be reached. Please check your connection and try again.");
        }

        /// <summary>
        /// A fresh copy of a request, because an HttpRequestMessage that has been
        /// sent cannot be sent again: .NET disposes its content and throws on the
        /// second attempt, which would turn a retry into a different error.
        /// </summary>
        private static HttpRequestMessage CloneRequest(HttpRequestMessage original, string payloadJson)
        {
            var copy = new HttpRequestMessage(original.Method, original.RequestUri)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };
            foreach (var header in original.Headers)
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return copy;
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

        // Turns a failed backend response into something worth reading. The status
        // code itself stays in the debug log: it means nothing to the person in the
        // middle of an interview, and every one of these cases is recoverable.
        private static string FriendlyBackendMessage(int status) => status switch
        {
            408 or 504 => "That took longer than expected. No credits were used. Press space to try again.",
            429        => "Too many requests in a short time. Wait a few seconds, then press space to try again.",
            503        => "The answer service is briefly unavailable. Your session is safe. Press space to try again.",
            _          => "We could not generate an answer this time. No credits were used. Press space to try again.",
        };

        private static bool TryReadSseToken(string data, out string token, out bool isTerminal)
        {
            token = "";
            isTerminal = false;

            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    throw new BackendRequestException(error.GetString()
                        ?? "The answer service could not complete this request.");
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

        /// <summary>
        /// Words that do not survive being said out loud. Each maps to what a
        /// person would have used instead, chosen to keep the grammar intact so
        /// the swap is invisible rather than obviously patched.
        /// </summary>
        private static readonly (string Tell, string Plain)[] AiTellReplacements =
        {
            ("leverages",     "uses"),
            ("leveraging",    "using"),
            ("leverage",      "use"),
            ("utilizes",      "uses"),
            ("utilizing",     "using"),
            ("utilize",       "use"),
            ("facilitates",   "helps"),
            ("facilitate",    "help"),
            ("streamlines",   "speeds up"),
            ("streamline",    "speed up"),
            ("robust",        "solid"),
            ("seamless",      "smooth"),
            ("seamlessly",    "smoothly"),
            ("comprehensive", "complete"),
            ("myriad",        "many"),
            ("plethora",      "plenty"),
            ("pivotal",       "key"),
            ("holistic",      "overall"),
            ("cutting-edge",  "modern"),
            ("best-in-class", "best"),
            ("delve into",    "get into"),
            ("delve",         "dig"),
            ("showcase",      "show"),
            ("underscores",   "shows"),
            ("underscore",    "show"),
        };

        private string CleanAiOutput(string ans)
        {
            // Fences stay. They were stripped here because everything was shown in
            // one prose box and a stray ``` was just noise on screen. The code
            // panel needs them: they are how it knows where code starts and ends,
            // and removing them first put the code straight back into the
            // paragraph it was meant to be lifted out of.
            ans = ans.Trim();

            // Markdown cleanup runs over the prose and never over the code.
            //
            // These three rules used to run across the whole answer, fences and
            // all, and the emphasis characters they strip are ordinary syntax in
            // most languages. The italic rule matched from one asterisk to the
            // next and removed both, so a spoken answer that included
            // "ListNode* next = curr->next;" arrived with the pointer gone and
            // would not compile; the underscore rule had the same reach over
            // snake_case names sharing a line. The screen path had this exact
            // defect and it cost a long evening to find, because the server was
            // sending correct code and the damage happened after it arrived.
            // Both paths now share one implementation so neither can drift.
            ans = ScreenAnalyzer.TransformProseOnly(ans, prose =>
            {
                // One shared implementation, not a second copy of the rule. The
                // copy that used to live here was not even the same rule - it
                // stripped one to three asterisks where the screen path stripped
                // two - and rules that are nearly the same in two places are how
                // a fix lands in one path and misses the other.
                return ScreenAnalyzer.StripEmphasis(prose);
            });

            // Rewrite the punctuation that most makes text read as AI-generated into
            // plain human writing. A long dash used mid-sentence as a break (word,
            // space, dash, space) becomes a comma the way most people actually write.
            // Only horizontal whitespace is matched so line breaks and bullets are
            // never merged; anything left (e.g. a tight number range like 2020–2023)
            // falls through to a plain hyphen.
            ans = ScreenAnalyzer.TransformProseOnly(ans, prose =>
            {
                prose = Regex.Replace(prose, @"(\S)[ \t]*[—–―][ \t]+", "$1, ");   // mid-sentence break -> comma
                prose = prose.Replace("—", "-").Replace("–", "-").Replace("―", "-");  // any remaining -> hyphen
                prose = Regex.Replace(prose, @",\s*,", ",");           // collapse accidental double commas
                return prose;
            });

            // Drop an opening callback to something that was never said.
            //
            // Shown the previous turn as context, the model reaches for
            // continuity it has not earned: "Yeah so like I mentioned, Java is a
            // statically typed language" as the very first answer about Java. The
            // one person who cannot be fooled by that is the interviewer, who
            // knows exactly what was said, and it reads as evasion or as not
            // listening.
            //
            // Stripped here as well as forbidden in the prompt, because this
            // arrives mid-sentence in a live interview and a rule the model can
            // quietly ignore is not enough on its own.
            ans = Regex.Replace(
                ans,
                @"^\s*(?:yeah[,\s]+|yes[,\s]+|so[,\s]+|well[,\s]+|right[,\s]+)*" +
                @"(?:so\s+)?(?:like|as)\s+(?:i|I)\s+(?:mentioned|said|noted|touched on|explained)" +
                @"(?:\s+(?:earlier|before|already|just now))?\s*[,:]?\s*",
                "",
                RegexOptions.IgnoreCase);

            // Swap the words that give a machine away.
            //
            // The prompt forbids these, and a prompt is a request. This runs on
            // the finished answer, so the interviewer never hears "leverage" or
            // "robust" because one instruction out of forty got less attention.
            // Every replacement is the plain word a person would have said, so
            // the sentence still reads correctly after the swap.
            // Word swaps and whitespace tidying are prose work, so they run on the
            // prose only.
            //
            // Collapsing runs of whitespace is the dangerous one: applied to a
            // whole answer it rewrites every indented line of code to a single
            // leading space, so a class arrives flattened against the left margin
            // and is unreadable in the panel the candidate is about to paste
            // from. The word swaps are the same kind of reach - a variable
            // legitimately named "robust" or "leverage" would be renamed inside
            // working code by a rule written for spoken English.
            ans = ScreenAnalyzer.TransformProseOnly(ans, prose =>
            {
                foreach (var (tell, plain) in AiTellReplacements)
                    prose = Regex.Replace(prose, $@"\b{Regex.Escape(tell)}\b", plain, RegexOptions.IgnoreCase);

                prose = Regex.Replace(prose, @"[ \t]{2,}", " ");   // collapse doubled spaces
                return prose;
            });

            // Re-capitalise whatever now starts the answer.
            if (ans.Length > 0 && char.IsLower(ans[0]))
                ans = char.ToUpper(ans[0]) + ans[1..];

            ans = ans.Replace("\r\n", "\n").Replace("\r", "\n");

            // Normalise the depth section's bullets.
            //
            // Asked for a bullet the model reaches for a hyphen, which reads as a
            // dash mid-sentence rather than as a list, and the whole point of this
            // section is being scannable in one glance while somebody is talking.
            // Only lines after the MORE TO SAY marker are touched, so a hyphen
            // inside the spoken answer above stays a hyphen.
            int moreAt = ans.IndexOf("MORE TO SAY", StringComparison.OrdinalIgnoreCase);
            if (moreAt >= 0)
            {
                string head = ans[..moreAt];
                // Prose only. A code line that begins with - or * is not a bullet,
                // and rewriting it to one silently edits code the candidate is
                // about to paste — a pointer declaration, a decrement, a comment.
                string tail = ScreenAnalyzer.TransformProseOnly(ans[moreAt..], prose =>
                    Regex.Replace(prose, @"(?m)^[ \t]*[-*–—]\s+", "• "));
                ans = head + tail;
            }
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
            DebugWindow.Log("SESSION", $"Auto-started session #{sessionNumber}");
        }

        // Trailing marker in a session log holding the elapsed seconds.
        // The sessions panel reads it; kept in one place so both sides agree.
        internal const string SessionDurationTag = "DURATION_SECONDS:";

        /// <summary>
        /// Serialises session-log writes onto one background worker.
        ///
        /// The file is encrypted as a whole, so appending a turn means decrypting
        /// all of it, concatenating, and encrypting all of it again. That ran on
        /// the UI thread at the end of every answer, and its cost grows with the
        /// length of the interview: by the fortieth question the app was
        /// decrypting and re-encrypting the entire transcript before it could
        /// repaint, during the interview it was recording.
        ///
        /// The gate keeps turns in order now that they no longer run on the one
        /// thread that used to guarantee it.
        /// </summary>
        private static readonly SemaphoreSlim _sessionLogGate = new(1, 1);

        private void AppendToSessionLog(string q, string a)
        {
            string path = sessionLogPath;
            if (!string.IsNullOrEmpty(path))
            {
                _ = Task.Run(async () =>
                {
                    await _sessionLogGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        string content = SecureDataProtector.ReadProtectedFile(path);
                        SecureDataProtector.WriteProtectedFile(path, content + $"Q: {q}\nA: {a}\n\n");
                    }
                    catch (Exception ex) { DebugWindow.Log("SESSION", $"Log write failed: {ex.Message}"); }
                    finally { _sessionLogGate.Release(); }
                });
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

            // Stamp how long the interview ran so the session report can show it.
            // Written as a trailing line, so session files from older builds stay readable.
            if (!string.IsNullOrEmpty(sessionLogPath) && _sessionSeconds > 0)
            {
                try
                {
                    string content = SecureDataProtector.ReadProtectedFile(sessionLogPath);
                    if (!content.Contains(SessionDurationTag))
                        SecureDataProtector.WriteProtectedFile(
                            sessionLogPath, content + SessionDurationTag + " " + _sessionSeconds + "\n");
                }
                catch (Exception ex) { DebugWindow.Log("SESSION", $"Could not record duration: {ex.Message}"); }
            }

            sessionLogPath = "";

            // Stop and hide session timer
            _sessionTimer?.Stop();
            SessionTimerBadge.Visibility = Visibility.Collapsed;
            PromptBuilder.ClearHistory();
            UnlockResume();
            DebugWindow.Log("SESSION", "Session ended");
        }

        // ══════════════════════════════════════════════════════════════════════
        // UI
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Shows past sessions over the interview screen.
        ///
        /// This used to open a second window. One window means one taskbar
        /// entry, nothing to lose behind another app, and - the reason that
        /// decided it - one fewer surface to keep hidden from a screen capture
        /// while a screen is being shared.
        /// </summary>
        private void SessionsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (SessionsPanelHost == null) return;

            if (SessionsPanelHost.Visibility == Visibility.Visible)
            {
                CloseSessionsPanel();
                return;
            }

            if (!_sessionsPanelWired)
            {
                SessionsPanelHost.CloseRequested += CloseSessionsPanel;
                _sessionsPanelWired = true;
            }

            // A real view switch: the interview screen is hidden, not covered.
            //
            // Layering the panel on top and relying on its background to hide
            // what was underneath left both visible at once - the panel is
            // translucent by design, so the interview screen showed straight
            // through it and clicking Past Sessions looked like it did nothing.
            if (InterviewContent != null) InterviewContent.Visibility = Visibility.Collapsed;
            SessionsPanelHost.Visibility = Visibility.Visible;
            SessionsPanelHost.Open();
        }

        private bool _sessionsPanelWired;

        private void CloseSessionsPanel()
        {
            if (SessionsPanelHost != null)
                SessionsPanelHost.Visibility = Visibility.Collapsed;
            if (InterviewContent != null)
                InterviewContent.Visibility = Visibility.Visible;
        }

        private void UpdateMicUi()
        {
            Color c; string label;
            if (isProcessing) { c = Colors.Orange; label = "THINKING"; }
            else if (isListening) { c = Colors.LimeGreen; label = "LISTENING"; }
            else if (!_engineOnline && UserSession.SpeechmaticsLastStatusCode == 402)
            {
                // Two limits, two messages. "NO CREDITS" beside a badge showing
                // two thousand credits reads as a bug rather than a limit.
                c = Color.FromRgb(239, 68, 68);
                label = UserSession.SpeechmaticsOutOfListeningTime
                    ? "NO LISTENING TIME"
                    : "NO CREDITS";
            }
            else if (!_engineOnline && UserSession.SpeechmaticsLastStatusCode == 401)
            {
                c = Color.FromRgb(239, 68, 68);
                label = "SIGN IN";
            }
            else if (!_engineOnline && UserSession.SpeechmaticsLastStatusCode is 502 or 503)
            {
                c = Color.FromRgb(239, 68, 68);
                label = "SERVICE OFFLINE";
            }
            // Engine still handshaking with Speechmatics — it literally can't hear yet,
            // so tell the user to wait a moment rather than letting them speak into a void.
            else if (!_engineOnline && DateTime.UtcNow < UserSession.SpeechmaticsRetryAfterUtc)
            {
                // A bare "WAITING" is indistinguishable from the app being idle,
                // and this state can last the best part of an hour. Someone sat
                // pressing Space and speaking into it, with nothing on screen
                // saying the speech service had not started. Counting down at
                // least says it is a wait with an end.
                var left = UserSession.SpeechmaticsRetryAfterUtc - DateTime.UtcNow;
                c = Color.FromRgb(245, 178, 60);
                label = left.TotalMinutes >= 1
                    ? $"WAITING {(int)left.TotalMinutes}m"
                    : $"WAITING {Math.Max(1, (int)left.TotalSeconds)}s";
            }
            // Backing off between restart attempts. Distinct from CONNECTING so a
            // first connection is not confused with a recovery that is already
            // several attempts in.
            else if (!_engineOnline && _engineRestartCount > 0 && DateTime.UtcNow < _nextEngineRestartUtc)
            {
                c = Color.FromRgb(245, 178, 60);
                label = "RETRYING";
            }
            else if (!_engineOnline) { c = Color.FromRgb(245, 178, 60); label = "CONNECTING"; }
            else if (isMuted) { c = Color.FromRgb(239, 68, 68); label = "MUTED"; }
            else { c = Color.FromRgb(239, 68, 68); label = isRecording ? "RECORDING" : "READY"; }

            var brush = new SolidColorBrush(c);
            MicIndicator.Fill = brush;
            MicGlow.Color = c;
            MicBtn.BorderBrush = brush;
            MicIndicatorText.Text = label;

            // Premium: tint the whole status pill + a soft halo to match the state color,
            // with state-coloured (lightened) text — so MUTED reads red, LISTENING green, etc.
            try
            {
                Color lite = Color.FromRgb(
                    (byte)(c.R + (255 - c.R) * 0.45),
                    (byte)(c.G + (255 - c.G) * 0.45),
                    (byte)(c.B + (255 - c.B) * 0.45));
                // The status no longer sits in its own bordered box: the mic beside it
                // already shows the same state as a coloured ring and glow, so a second
                // tinted container repeated it and read as another button in the row.
                // The dot and the state-coloured text carry it now.
                MicIndicatorText.Foreground = new SolidColorBrush(lite);
                if (MicPillGlow != null) { MicPillGlow.Opacity = 0; }
            }
            catch { }

            // Premium micro-interaction: a soft "breathing" glow while listening so the
            // active state feels alive and hand-crafted rather than a flat static control.
            // Animating the effect object directly (not via a Storyboard) keeps it fully
            // contained here — no XAML plumbing, nothing else can be affected.
            if (isListening)
            {
                // Kept within the header row. At 26 the halo reached 13px past the
                // button on every side, more than the row could show, so while
                // listening the glow and the ring under it were sliced flat by the
                // window edge. The breathing still reads at this range.
                var pulse = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 6,
                    To = 12,
                    Duration = TimeSpan.FromMilliseconds(950),
                    AutoReverse = true,
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                    EasingFunction = new System.Windows.Media.Animation.SineEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
                    }
                };
                MicGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, pulse);
            }
            else
            {
                // Stop any running pulse, then settle on a static glow for the state.
                MicGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, null);
                MicGlow.BlurRadius = isProcessing ? 12 : 0;
            }

            if (answerWindow != null) answerWindow.UpdateMicState(isListening, isProcessing);
        }

        private void UpdateTranscript()
        {
            // Auto mode continuously returns to listening after each answer. It uses the
            // existing transcript only; no microphone, engine or provider behavior changes.
            if (AutoModeEnabled && !isListening && !isProcessing && !_flushing)
            {
                StartAutoListeningIfReady();
                return;
            }

            if (!isListening) return;

            // Briefly suppress stale engine output right after unmute so the tail of a
            // previous utterance can't flash before reset.flag takes effect. 4 ticks at
            // the 60ms poll ≈ 240ms — long enough for the reset to land, short enough that
            // your live words show almost immediately for a real-time feel.
            if (_justStartedListening)
            {
                _listenStartTicks++;
                TranscriptTextBlock.Text = "";          // force blank during suppression
                if (_listenStartTicks >= 4) _justStartedListening = false;
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

                    // Speech arriving is speech arriving, whichever mode is on.
                    // The idle timer used to read _autoTranscriptChangedUtc, which
                    // only Auto winds, so in Press Space mode it saw silence while
                    // somebody was talking and would cut the microphone off three
                    // minutes into a long answer.
                    _lastSpeechHeardUtc = DateTime.UtcNow;
                    _heardAnythingThisSession = true;

                    if (AutoModeEnabled)
                    {
                        NoteAutoSpeechPace(DateTime.UtcNow);
                        _autoLastTranscript = text;
                        _autoTranscriptChangedUtc = DateTime.UtcNow;
                    }
                }

                if (AutoModeEnabled)
                    TrySubmitAutomaticTurn(_autoLastTranscript);
            }
            catch (Exception ex) { DebugWindow.Log("TRANSCRIPT", $"UpdateTranscript failed: {ex.Message}"); }
        }

        private string ReadLatestTxtSafe()
        {
            string path = Path.Combine(AppDataFolder, "latest.txt");
            for (int i = 0; i < TranscriptRetryCount; i++)
            {
                try
                {
                    // FileShare.Delete allows Python's os.replace() atomic rename to succeed
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
                    return sr.ReadToEnd();
                }
                catch { }
            }
            return TranscriptTextBlock.Text;
        }

        // ══════════════════════════════════════════════════════════════════════
        // ENGINE
        // ══════════════════════════════════════════════════════════════════════
        private async Task InitializeSpeechPipelineAsync()
        {
            try
            {
                _engineAuthFailed = false;
                _engineUsageLimitReached = false;
                UpdateMicUi();
                bool keyReady = await UserSession.EnsureSpeechmaticsKeyAsync(DeviceIdentity.Current);
                if (!keyReady)
                {
                    UpdateMicUi();
                    return;
                }
                StartSpeechmaticsEngine();
            }
            catch (Exception ex)
            {
                DebugWindow.Log("ENGINE", $"Audio initialization failed: {ex.Message}");
                UpdateMicUi();
            }
        }

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
                _engineOnline      = false; // fresh process: not ready to transcribe yet
                _engineAuthFailed  = false; // reset so MonitorEngine can restart after a key change
                _engineUsageLimitReached = false;
                _engineFatalReason = "";

                string pyScript = Path.Combine(scriptFolder, "speechmatics_engine.py");
                string bundledEngine = BundledEnginePath();
                bool haveBundledEngine = bundledEngine.Length > 0;

                // Only the script path needs a Python interpreter behind it. When the
                // self-contained engine ships alongside the app there is nothing for
                // the user to install, so a missing script is not a problem.
                if (!haveBundledEngine && !File.Exists(pyScript))
                {
                    DebugWindow.Log("ENGINE", "Engine not found under: " + scriptFolder);
                    return;
                }
                // Speechmatics key is always allocated server-side per device/user —
                // there is no user-facing override; keys are admin-managed only.
                string smKey = UserSession.HasValidSpeechmaticsKey
                    ? UserSession.SpeechmaticsKey
                    : "";
                if (string.IsNullOrWhiteSpace(smKey))
                {
                    DebugWindow.Log("ENGINE", "SM key not yet available, awaiting warm fetch...");
                    // Await the SAME fetch started at the top of Loaded — no second
                    // serialized round-trip; usually already complete by now.
                    await UserSession.EnsureSpeechmaticsKeyAsync(DeviceIdentity.Current).ConfigureAwait(false);
                    smKey = UserSession.SpeechmaticsKey;
                }
                if (string.IsNullOrWhiteSpace(smKey))
                {
                    if (DateTime.UtcNow < UserSession.SpeechmaticsRetryAfterUtc)
                        DebugWindow.Log("ENGINE", $"Speechmatics key rate-limited; waiting until {UserSession.SpeechmaticsRetryAfterUtc:HH:mm:ss} UTC");
                    else
                        DebugWindow.Log("ENGINE", "Speechmatics key unavailable; waiting for automatic retry.");
                    _ = Dispatcher.BeginInvoke(new Action(UpdateMicUi));
                    return;
                }

                // Prefer the engine we ship. Falling back to a system Python keeps
                // working for anyone running from source, where engine\ is absent.
                string engineExe;
                string scriptArg;

                if (haveBundledEngine)
                {
                    engineExe = bundledEngine;
                    scriptArg = "";
                    DebugWindow.Log("ENGINE", "Using the bundled speech engine");
                }
                else
                {
                    // Note: only the "py" launcher is probed, so the -3 below is
                    // always a launcher argument and never passed to python.exe.
                    string? pyExe = await ResolvePythonExecutableAsync().ConfigureAwait(false);
                    if (generation != Volatile.Read(ref _engineStartGeneration)) return;
                    if (string.IsNullOrWhiteSpace(pyExe))
                    {
                        // Python alone is not enough: the engine also needs the packages
                        // in requirements.txt, so naming only the runtime sent people
                        // away to install it and hit the same wall again.
                        _engineFatalReason =
                            "Install Python 3.11 or newer, then run: pip install -r requirements.txt";
                        _engineAuthFailed = true;
                        _ = Dispatcher.BeginInvoke(new Action(ShowEngineAuthError));
                        return;
                    }
                    engineExe = pyExe;
                    scriptArg = $"-3 \"{pyScript}\"";
                    DebugWindow.Log("ENGINE", $"No bundled engine; using Python at {pyExe}");
                }

                speechmaticsProcess = new Process();
                speechmaticsProcess.StartInfo.FileName = engineExe;
                WriteVocabFile();   // interview-specific terms → better STT accuracy
                string deviceArg = _audioDeviceId >= 0 ? $" --device {_audioDeviceId}" : "";
                string modeArg = $" --mode {CaptureModeFor(_listeningMode)}";
                string langArg   = $" --language {SettingsWindow.GetTranscriptLanguage()}";
                speechmaticsProcess.StartInfo.Arguments = $"{scriptArg}{deviceArg}{modeArg}{langArg}";
                speechmaticsProcess.StartInfo.EnvironmentVariables["SM_API_KEY"] = smKey;
                // Sarvam key for Telugu / other Speechmatics-unsupported languages. Passed via
                // env (never on the command line) exactly like the Speechmatics key.
                speechmaticsProcess.StartInfo.EnvironmentVariables["SARVAM_API_KEY"] = SettingsWindow.GetSarvamApiKey();
                speechmaticsProcess.StartInfo.WorkingDirectory = scriptFolder;
                speechmaticsProcess.StartInfo.CreateNoWindow = true;
                speechmaticsProcess.StartInfo.UseShellExecute = false;
                speechmaticsProcess.StartInfo.RedirectStandardOutput = true;
                speechmaticsProcess.StartInfo.RedirectStandardError = true;
                // The engine forces its pipes to UTF-8; decode them the same way,
                // otherwise any non-ASCII it prints arrives as mojibake.
                speechmaticsProcess.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                speechmaticsProcess.StartInfo.StandardErrorEncoding = Encoding.UTF8;
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
                            // The engine prints "STATUS: ONLINE" the moment Speechmatics
                            // accepts the session — that's when transcription actually works.
                            // Flip the readiness flag so the mic pill can show READY.
                            // Transcription has stopped and is retrying. The flag
                            // was set true on the first success and never cleared,
                            // so a dropped session left the app believing speech
                            // still worked: the user spoke, nothing was recorded,
                            // and the question arrived empty with nothing on
                            // screen to explain why.
                            // Which engine this actually is. Logged rather than
                            // merely passed through, so a support log answers
                            // "what was running?" instead of inviting a guess.
                            // The Mac app shipped a fork of the engine for
                            // months without anything noticing, and nothing in
                            // either app would have caught it recurring.
                            if (line.StartsWith(">>> ENGINE BUILD:", StringComparison.Ordinal))
                            {
                                _engineBuildId = line[">>> ENGINE BUILD:".Length..].Trim();
                                DebugWindow.Log("ENGINE", $"Build {_engineBuildId}");
                            }

                            if (_engineOnline && line.Contains("STATUS: OFFLINE"))
                            {
                                _engineOnline = false;
                                DebugWindow.Log("ENGINE", "Speech connection lost; reconnecting.");
                                _ = Dispatcher.BeginInvoke(new Action(UpdateMicUi));
                            }

                            // The engine says when it hears speech and when words
                            // come back. Either alone means nothing; the pair is
                            // the only thing that can tell a quiet room from a
                            // transcriber that has stopped working.
                            if (line.Contains("MIC SIGNAL DETECTED"))
                                _lastSpeechDetectedUtc = DateTime.UtcNow;
                            else if (line.Contains("PARTIAL received") || line.Contains("FINAL received"))
                            {
                                // Read the count out of the line rather than
                                // matching the phrasing of the empty case.
                                //
                                // This tested line.Contains("(0 chars)"), a
                                // literal that appears nowhere in the engine -
                                // it is built by
                                // print(f">>> FINAL received ({len(display)} chars)").
                                // So half the deafness detector was coupled to
                                // the wording of a log line: reword it and the
                                // app silently stops telling an empty result
                                // from a real one, with nothing in C# changing.
                                // The Mac session had the identical coupling.
                                var count = EngineCharCount.Match(line);
                                bool empty = count.Success
                                             && int.TryParse(count.Groups[1].Value, out int n)
                                             && n == 0;
                                if (!empty) _lastWordsReceivedUtc = DateTime.UtcNow;
                            }

                            if (!_engineOnline && line.Contains("STATUS: ONLINE"))
                            {
                                _engineOnline = true;
                                _engineRestartCount = 0;
                                _nextEngineRestartUtc = DateTime.MinValue;
                                _engineTokenRefreshAttempted = false;
                                _ = Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    UpdateMicUi();
                                    StartAutoListeningIfReady();
                                }));
                            }
                            // Logged straight from this reader thread. Marshalling to the UI
                            // thread first meant every line the engine printed did a
                            // synchronous file write there; DebugWindow.Log is already
                            // thread-safe and dispatches its own UI update.
                            DebugWindow.Log("PY", line);
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
                            DebugWindow.Log("PY_ERR", line);

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

        // Languages the engine routes to Sarvam AI (Speechmatics can't do them).
        // Keep in sync with SARVAM_LANG_MAP in speechmatics_engine.py.
        private static readonly HashSet<string> _sarvamLangs =
            new(StringComparer.OrdinalIgnoreCase) { "te", "kn", "ml", "gu", "pa", "or", "as" };
        private static bool IsSarvamLanguage(string code) => _sarvamLangs.Contains((code ?? "").Trim());

        // Common words that get capitalized at sentence starts — never useful as STT vocab.
        private static readonly HashSet<string> _vocabStop = new(StringComparer.OrdinalIgnoreCase)
        {
            "The","And","For","With","You","Your","Our","We","This","That","Are","Is","As","At",
            "Or","By","Be","It","If","So","But","Not","All","Can","Will","Job","Role","Team","Work",
            "Years","Year","Experience","Skills","Company","About","Requirements","Responsibilities",
            "Description","Position","Please","Must","Have","Strong","Good","New","More","Who","What",
            "When","Where","Why","How","We're","You'll","We'll","Their","There","Here","Then","Than"
        };

        /// <summary>
        /// Write the current interview's distinctive terms (company, role tech stack, resume
        /// names/projects) to vocab.txt so the speech engine recognises them instead of
        /// guessing. Big accuracy win on exactly the words that matter in THIS interview.
        /// </summary>
        private void WriteVocabFile()
        {
            try
            {
                string resume = "";
                try { resume = ResumeTextBox?.Text ?? ""; } catch { }
                string blob = $"{_companyName}\n{_jobDescription}\n{_liveHints}\n{resume}";
                var terms = ExtractVocabTerms(blob, _companyName);
                File.WriteAllLines(Path.Combine(AppDataFolder, "vocab.txt"), terms);
                DebugWindow.Log("VOCAB", $"Wrote {terms.Count} interview terms for STT accuracy");
            }
            catch (Exception ex) { DebugWindow.Log("VOCAB", $"write failed: {ex.Message}"); }
        }

        /// <summary>
        /// Contact details and other fragments that are neither speech nor
        /// anybody's business.
        ///
        /// This list is written to vocab.txt in plain text, because the speech
        /// engine reads that file directly and cannot decrypt what everything
        /// else on disk is protected with. It was carrying the candidate's
        /// email address, their LinkedIn URL and their city, lifted straight
        /// out of the resume and left unencrypted beside files that are not.
        ///
        /// None of it belongs there on its own terms either. Nobody says their
        /// email address out loud in an interview, so as speech vocabulary it
        /// is dead weight, and short fragments are worse than useless: a resume
        /// from Illinois put "IL" in the list, and a vocabulary hint is exactly
        /// the nudge that turns "I'll" into "IL" in a transcript.
        /// </summary>
        private static bool IsPersonalDetail(string word)
        {
            if (word.Contains('@')) return true;                       // email
            if (word.Contains('/')) return true;                       // URL or handle

            // A domain, but only where something precedes the dot. ".NET" is a
            // framework this candidate may well be asked about, and filtering it
            // would remove a term the vocabulary exists to protect.
            if (IndexOfDomain(word) > 0) return true;

            // Mostly digits: a phone number, a postcode, a house number.
            int digits = word.Count(char.IsDigit);
            if (digits > 0 && digits * 2 >= word.Length) return true;

            // A long word ending in a run of digits is a username or the local
            // part of an email — "pavankrishna2528" — rather than a technology.
            // Four in a row, so "gpt-oss-20b" and "llama3" survive.
            if (word.Length >= 8 &&
                System.Text.RegularExpressions.Regex.IsMatch(word, @"\d{4}")) return true;

            // Two capitals is a state code far more often than a term worth
            // recognising, and the cost of a wrong one is high.
            if (word.Length == 2 && word.All(char.IsUpper)) return true;

            return false;
        }

        /// <summary>Where a domain suffix starts, or -1. Position 0 is not a domain.</summary>
        private static int IndexOfDomain(string word)
        {
            foreach (string suffix in new[] { ".com", ".org", ".net", ".io", ".co", ".dev" })
            {
                int at = word.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
                if (at > 0) return at;
            }
            return -1;
        }

        private static List<string> ExtractVocabTerms(string text, string company)
        {
            var found = new List<string>();
            var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string w)
            {
                w = w.Trim().Trim('.', ',', ';', ':', '(', ')', '"', '\'', '!', '?');
                if (w.Length < 2 || w.Length > 40) return;
                if (_vocabStop.Contains(w)) return;
                if (IsPersonalDetail(w)) return;
                if (seen.Add(w)) found.Add(w);
            }

            if (!string.IsNullOrWhiteSpace(company)) Add(company.Trim());

            text ??= "";
            // Count frequencies so we can keep repeated proper nouns and drop one-off
            // sentence-start capitalizations (which are usually just ordinary words).
            var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var tokens = System.Text.RegularExpressions.Regex.Matches(text, @"[A-Za-z][A-Za-z0-9\.\+/#\-]*");
            foreach (System.Text.RegularExpressions.Match m in tokens)
                freq[m.Value] = freq.TryGetValue(m.Value, out var c) ? c + 1 : 1;

            foreach (System.Text.RegularExpressions.Match m in tokens)
            {
                string w = m.Value;
                if (found.Count >= 170) break;
                bool acronym = w.Length is >= 2 and <= 6 && w.All(ch => char.IsUpper(ch) || char.IsDigit(ch)) && w.Any(char.IsLetter);
                bool tech    = w.IndexOfAny(new[] { '.', '+', '#', '/' }) >= 0 || (w.Any(char.IsDigit) && w.Any(char.IsLetter));
                bool internalCaps = w.Length >= 3 && w.Skip(1).Any(char.IsUpper);          // React, MongoDB, TypeScript
                bool repeatedProper = char.IsUpper(w[0]) && w.Length >= 3 && freq[w] >= 2;  // a recurring proper noun
                if (acronym || tech || internalCaps || repeatedProper) Add(w);
            }
            return found;
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
        /// Removes recording-saved markers that no longer refer to anything.
        ///
        /// One of these is written per recording so a crash mid-save can be
        /// recovered on the next launch, and nothing ever removed them again:
        /// measured on a working install, 97 of them going back five weeks,
        /// one byte each and one more every session forever. Harmless
        /// individually and untidy at any scale — a support engineer opening
        /// the data folder should see the handful of files that mean something,
        /// not a wall of them.
        ///
        /// A week is far longer than the window they are useful for: they are
        /// read once, on the launch immediately after the recording, and never
        /// again. Deleting on a timer rather than on read keeps the crash-
        /// recovery path exactly as it was.
        /// </summary>
        private void SweepStaleRecordingFlags()
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow.AddDays(-7);
                foreach (string path in Directory.EnumerateFiles(
                             AppDataFolder, "recording_saved_*.flag"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
                    }
                    catch { /* one stuck file must not stop the sweep */ }
                }

                // The live transcript is deleted on the way out, but only on a
                // clean way out. Ending the app from Task Manager, or a crash,
                // skips the closing handler entirely and strands the last thing
                // an interviewer said in plain text until the next launch — the
                // one file the app keeps that is not encrypted for this user.
                //
                // Swept here as well as deleted on exit, because a leak that
                // only happens when something already went wrong is still a
                // leak. It costs nothing: the engine rewrites this file from
                // scratch as soon as it starts listening.
                try
                {
                    string livePath = Path.Combine(AppDataFolder, "latest.txt");
                    if (File.Exists(livePath)) File.Delete(livePath);
                }
                catch { }
            }
            catch (Exception ex)
            {
                DebugWindow.Log("FILE", $"Stale recording-flag sweep failed: {ex.Message}");
            }
        }

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
        private static readonly object PythonResolutionLock = new();
        private static Task<string?>? _pythonResolutionTask;

        private static Task<string?> ResolvePythonExecutableAsync()
        {
            if (_cachedPythonExe != null) return Task.FromResult<string?>(_cachedPythonExe);

            lock (PythonResolutionLock)
            {
                if (_cachedPythonExe != null) return Task.FromResult<string?>(_cachedPythonExe);
                if (_pythonResolutionTask is { IsCompleted: false }) return _pythonResolutionTask;

                _pythonResolutionTask = Task.Run(ResolvePythonExecutable);
                return _pythonResolutionTask;
            }
        }

        /// <summary>
        /// Path to the self-contained speech engine shipped beside the app, or ""
        /// when running from source without a build of it.
        /// </summary>
        private string BundledEnginePath()
        {
            foreach (string root in new[] { AppContext.BaseDirectory, scriptFolder })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string candidate = Path.Combine(root, "engine", "speechmatics_engine.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return "";
        }

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
            if (_engineRecoveryInProgress) return;
            if (DateTime.UtcNow < _nextEngineRestartUtc) return;
            if (_engineStarting || _engineAuthFailed) return; // auth failure — do not restart
            if (string.IsNullOrWhiteSpace(UserSession.SpeechmaticsKey) &&
                DateTime.UtcNow < UserSession.SpeechmaticsRetryAfterUtc) return;
            if (speechmaticsProcess == null || speechmaticsProcess.HasExited)
            {
                // The engine is gone, so nothing is transcribing. A dropped
                // session prints STATUS: OFFLINE on its way down and clears this,
                // but a crashed process prints nothing at all, and the flag stayed
                // true through every branch below including the ones that give up
                // entirely. The app went on believing speech worked while the
                // process that provides it no longer existed.
                if (_engineOnline)
                {
                    _engineOnline = false;
                    Dispatcher.Invoke(UpdateMicUi);
                }

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
                    if (!IsSarvamLanguage(SettingsWindow.GetTranscriptLanguage()) && !_engineTokenRefreshAttempted)
                    {
                        _engineTokenRefreshAttempted = true;
                        _ = RecoverSpeechmaticsAuthenticationAsync();
                        return;
                    }
                    _engineAuthFailed = true;
                    // Name the right engine: Telugu (and the other Sarvam-routed languages)
                    // authenticate with the Sarvam key, not the Speechmatics key.
                    string whichKey = IsSarvamLanguage(SettingsWindow.GetTranscriptLanguage())
                        ? "Sarvam" : "Speechmatics";
                    DebugWindow.Log("ENGINE", $"❌ Auth failure (exit 2) — engine stopped. Fix your {whichKey} key in Settings.");
                    Dispatcher.Invoke(() => ShowEngineAuthError());
                    return;
                }
                int retrySeconds = Math.Min(30, 1 << Math.Min(_engineRestartCount, 5));
                _engineRestartCount++;
                _nextEngineRestartUtc = DateTime.UtcNow.AddSeconds(retrySeconds);
                DebugWindow.Log("ENGINE", $"Engine stopped; retrying with {retrySeconds}-second backoff.");
                if (isListening)
                {
                    isListening = false;
                    try { File.WriteAllText(Path.Combine(AppDataFolder, "latest.txt"), ""); } catch { }
                    Dispatcher.Invoke(() => UpdateMicUi());
                }
                StartSpeechmaticsEngine();
            }
        }

        private async Task RecoverSpeechmaticsAuthenticationAsync()
        {
            if (_engineRecoveryInProgress) return;
            _engineRecoveryInProgress = true;
            _engineAuthFailed = true;
            try
            {
                DebugWindow.Log("ENGINE", "Temporary transcription token expired; requesting a fresh token.");
                UserSession.InvalidateSpeechmaticsKey();
                bool refreshed = await UserSession.EnsureSpeechmaticsKeyAsync(DeviceIdentity.Current);
                _engineAuthFailed = false;
                if (refreshed)
                {
                    _nextEngineRestartUtc = DateTime.MinValue;
                    StartSpeechmaticsEngine();
                }
                else
                {
                    _engineTokenRefreshAttempted = false;
                    UpdateMicUi();
                }
            }
            catch (Exception ex)
            {
                _engineAuthFailed = false;
                _engineTokenRefreshAttempted = false;
                DebugWindow.Log("ENGINE", $"Token recovery failed: {ex.Message}");
                UpdateMicUi();
            }
            finally
            {
                _engineRecoveryInProgress = false;
            }
        }

        private void ShowEngineAuthError()
        {
            // "KEY INVALID" named a credential the person using the app does not
            // own and cannot replace. The specific cause is in the debug log.
            string label = string.IsNullOrEmpty(_engineFatalReason) ? "UNAVAILABLE" : "SETUP NEEDED";
            MicIndicator.Fill     = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ef4444"));
            MicIndicatorText.Text = label;
            MicBtn.BorderBrush    = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ef4444"));

            // The reason was computed and then thrown away: the only signal was a
            // red dot reading "SETUP NEEDED", with the explanation buried in a
            // debug log the user has no reason to open. Someone whose machine is
            // missing the speech engine just saw transcription silently not work.
            string detail = string.IsNullOrEmpty(_engineFatalReason)
                ? "Speech transcription could not start. Press F12 for details."
                : $"Speech transcription could not start. {_engineFatalReason}.";

            if (_isCameraMode && answerWindow != null)
                answerWindow.ShowServiceUnavailable("Setup needed", detail);
            else
                ShowInAppAlert("Speech transcription is not available", detail, persist: true);
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
            if (e.IsRepeat) return; // Ignore auto-repeat key down events from holding space down
            if (e.Key == System.Windows.Input.Key.F12) { e.Handled = true; ToggleDebugWindow(); return; }

            // Esc leaves the sessions view. Handled only while that view is up,
            // so Esc keeps whatever meaning it already had everywhere else -
            // and the button's tooltip promises this, which is reason enough
            // for it to actually work.
            if (e.Key == System.Windows.Input.Key.Escape
                && SessionsPanelHost != null
                && SessionsPanelHost.Visibility == Visibility.Visible)
            {
                e.Handled = true;
                CloseSessionsPanel();
                return;
            }
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
            // The global hook (GlobalHotkey.cs, WH_KEYBOARD_LL) is the single source of truth
            // for what Space DOES — it operates below WPF's routed-event system entirely, so
            // marking the event handled here cannot stop it from firing, and does not create
            // a second path. What it does stop is WPF's own default behavior: any Button that
            // currently has keyboard focus (e.g. the Screen AI button after it was clicked)
            // activates itself on Space and fires its own Click handler. Without this, one
            // physical Space press could both toggle the mic (via the hook) AND re-trigger
            // Screen Analysis (via the focused button), which is what a user reported seeing.
            // Typing a literal space in a text field is unaffected: WPF checks focus before
            // this handler runs, so a focused TextBox never reaches this branch.
            if (e.Key == System.Windows.Input.Key.Space && !IsTypingInTextField())
            {
                e.Handled = true;
            }
        }

        private void Window_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { /* handled by PreviewKeyDown */ }
        private void Border_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) { try { DragMove(); } catch { } }

        // ── WM_MOUSEACTIVATE fix ─────────────────────────────────────────────
        // Borderless, layered windows (WindowStyle=None + AllowsTransparency=True)
        // otherwise consume the very first click purely to activate the window,
        // never delivering it to the control underneath — so every button (mic,
        // Screen AI, etc.) needs an extra "wasted" click first. Returning
        // MA_ACTIVATE tells Windows: activate AND let this same click go through.
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_ACTIVATE = 1;

        private IntPtr WndProcActivateFix(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE)
            {
                handled = true;
                return (IntPtr)MA_ACTIVATE;
            }
            return IntPtr.Zero;
        }
        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
        private void MinimizeBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            var sw = new SettingsWindow(
                _audioDeviceId,
                AutoModeEnabled,
                _listeningMode == ListeningMode.PracticeAuto);
            sw.Owner = this;
            sw.ShowDialog();
            if (sw.SignInRequested) { SignInHeaderBtn_Click(sender, e); return; }
            if (sw.SettingsChanged)
            {
                if (sw.SelectedDeviceIndex >= 0) _audioDeviceId = sw.SelectedDeviceIndex;
                StartSpeechmaticsEngine();
                ApplyMainWindowOpacity();
                // Re-apply stealth in case it was changed in Settings. This must reach
                // the overlay too, not just the main window.
                _stealthMode = SettingsWindow.GetStealthMode();
                ApplyStealthToAllWindows();
                UpdateStealthBtn();
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
            ClearAnswer();
            StarBadge.Visibility = Visibility.Collapsed;
            if (answerWindow != null) { answerWindow.UpdateAnswer(""); answerWindow.UpdateQuestion(""); }
            PromptBuilder.ClearHistory();
            try { File.WriteAllText(Path.Combine(AppDataFolder, "latest.txt"), ""); } catch { }
        }

        private bool _lastAnswerEmpty = true;
        // ══════════════════════════════════════════════════════════════════════
        // THE ANSWER, AND THE CODE, SHOWN SEPARATELY
        //
        // Both used to land in the same box: a wrapped, proportional font sized
        // for prose. Indentation collapsed, long lines folded mid-expression,
        // and the part a candidate has to read most carefully was the hardest
        // thing on the screen to read. It also had to be picked out of the
        // surrounding sentences by eye before it could be copied.
        //
        // They are different things and are treated as such. Prose stays above
        // in the reading font; code goes below in a monospace panel that does
        // not wrap, with a copy button, because pasting it into the editor is
        // the next thing that happens to it.
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>Everything between fences in the current answer.</summary>
        private string _currentAnswerCode = "";

        private static readonly Regex FencedCode =
            new(@"```[ \t]*([A-Za-z0-9+#_-]*)[ \t]*\r?\n(.*?)(?:```|$)",
                RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>
        /// Complexity, when the answer states it. Matched loosely because models
        /// write it every way there is: "O(n)", "Time: O(n log n)", "time
        /// complexity is O(1) space".
        /// </summary>
        private static readonly Regex ComplexityLine =
            new(@"^.*\bO\s*\(\s*[^)]{1,24}\)\s*.*$",
                RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>Clears both halves, so no answer is shown beside older code.</summary>
        private void ClearAnswer()
        {
            AiAnswerBox.Text = "";
            _currentAnswerCode = "";
            if (CodePanel != null) CodePanel.Visibility = Visibility.Collapsed;
            if (ComplexityBar != null) ComplexityBar.Visibility = Visibility.Collapsed;
        }

        private void ShowAnswer(string answer, bool scrollToEnd)
        {
            string prose = answer;
            var code = new StringBuilder();
            string language = "";

            foreach (Match m in FencedCode.Matches(answer))
            {
                if (language.Length == 0) language = m.Groups[1].Value.Trim();
                if (code.Length > 0) code.AppendLine();
                code.Append(m.Groups[2].Value.TrimEnd());
            }

            // A fence that has opened and not yet closed is still arriving. Its
            // text is already in the panel, so leaving the half-written fence in
            // the prose as well would show the same code twice, once badly.
            prose = FencedCode.Replace(prose, "").Trim();

            string codeText = code.ToString().Trim();
            if (codeText.Length == 0)
            {
                _currentAnswerCode = "";
                CodePanel.Visibility = Visibility.Collapsed;
                ComplexityBar.Visibility = Visibility.Collapsed;
                AiAnswerBox.Text = StripScaffolding(prose);
                if (scrollToEnd) AiAnswerBox.ScrollToEnd();
                return;
            }

            _currentAnswerCode = codeText;
            CodeBox.Text = codeText;
            CodeLanguageLabel.Text = language.Length > 0 ? language.ToLowerInvariant() : "";
            CodePanel.Visibility = Visibility.Visible;

            // Read the complexity out of the prose before the prose is trimmed,
            // because trimming removes that very line.
            ShowComplexity(prose);

            AiAnswerBox.Text = StripScaffolding(prose);
            if (scrollToEnd) AiAnswerBox.ScrollToEnd();
        }

        /// <summary>
        /// Section headings the model is told to write, which the layout has
        /// already replaced. "SAY THIS" labels the spoken line — but that box IS
        /// the spoken line, so the label says nothing the position does not.
        /// "DETAIL" labels the code, which now lives in its own panel titled
        /// SOLUTION, so after the fence is lifted out the word is left pointing
        /// at nothing.
        /// </summary>
        private static readonly Regex ScaffoldHeading =
            new(@"^[ \t]*(SAY THIS|DETAIL|NEED|CAUSE|FIX|APPROACH|SOLUTION|COMPLEXITY)[ \t]*:?[ \t]*\r?$",
                RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// What is left of the answer once every part of it that has its own
        /// place on screen has been moved there.
        ///
        /// The model is asked for headed sections because that is what makes its
        /// output parseable; the person reading it mid-interview should never see
        /// the headings. They were being shown "SAY THIS", then the sentence,
        /// then "DETAIL" pointing at a code block that had been lifted into a
        /// separate panel, then a complexity line repeated underneath the copy of
        /// it already in the complexity bar. Four labels and a duplicate around
        /// one sentence, read while somebody is waiting for an answer.
        ///
        /// The complexity line goes for the same reason as the headings: it is
        /// not being deleted, it is being shown once instead of twice, in the bar
        /// directly beneath the code it describes.
        /// </summary>
        private string StripScaffolding(string prose)
        {
            if (string.IsNullOrWhiteSpace(prose)) return "";

            string cleaned = ScaffoldHeading.Replace(prose, "");

            // Only when the bar is actually showing it — otherwise removing it
            // here would lose it altogether.
            if (ComplexityBar != null && ComplexityBar.Visibility == Visibility.Visible)
                cleaned = ComplexityLine.Replace(cleaned, "");

            // Collapse the blank lines those removals leave behind.
            cleaned = Regex.Replace(cleaned, @"(\r?\n){3,}", "\n\n");
            return cleaned.Trim();
        }

        /// <summary>
        /// Lifts a complexity line out of the prose and shows it under the code,
        /// where it is read at the same moment as the code it describes.
        /// </summary>
        private void ShowComplexity(string prose)
        {
            foreach (Match m in ComplexityLine.Matches(prose))
            {
                string line = m.Value.Trim(' ', '\t', '-', '*', '\u2022');
                if (line.Length is < 4 or > 160) continue;

                ComplexityLabel.Text = line;
                ComplexityBar.Visibility = Visibility.Visible;
                return;
            }
            ComplexityBar.Visibility = Visibility.Collapsed;
        }

        private void CopyCodeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_currentAnswerCode)) return;
            try
            {
                Clipboard.SetText(_currentAnswerCode);
                CopyCodeBtn.Content = "Copied";
                var revert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                revert.Tick += (_, _) => { revert.Stop(); CopyCodeBtn.Content = "Copy code"; };
                revert.Start();
            }
            catch (Exception ex) { DebugWindow.Log("UI", $"Copy failed: {ex.Message}"); }
        }

        private void AiAnswerBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            bool empty = string.IsNullOrEmpty(AiAnswerBox.Text);
            if (AiAnswerHint != null)
                AiAnswerHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

            // Motion: gently fade the answer in the instant it first appears (once per answer,
            // not on every streamed token) for a premium reveal instead of a hard pop.
            if (_lastAnswerEmpty && !empty)
            {
                var fade = new System.Windows.Media.Animation.DoubleAnimation(0.25, 1.0,
                    new Duration(TimeSpan.FromMilliseconds(240)))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                AiAnswerBox.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            _lastAnswerEmpty = empty;
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
            int previousSessionNumber = sessionNumber;
            bool previousSessionSaved = false;
            EndSession();
            if (hadActiveRecording && await WaitForRecordingSaveAsync(recordingId, RecordingSaveTimeoutMs))
            {
                ProtectRecording(sessionNumber);
                previousSessionSaved = true;
            }
            // Don't increment here — StartNewSession's while(File.Exists) scan is the sole source of truth
            TranscriptTextBlock.Text = "";
            TranscriptHint.Visibility = Visibility.Visible;
            ClearAnswer();
            StarBadge.Visibility = Visibility.Collapsed;
            if (answerWindow != null) { answerWindow.UpdateAnswer(""); answerWindow.UpdateQuestion(""); }
            await StartNewSessionAsync();

            if (!isRecording)
                AiAnswerBox.Text = "We could not start a new session. Please try again.";
            else if (previousSessionSaved)
                AiAnswerBox.Text = $"Session {previousSessionNumber} saved. You are now in session {sessionNumber}, with your resume and role still loaded.";
            else
                AiAnswerBox.Text = $"Session {sessionNumber} started. Your resume and role are still loaded.";
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

        // Filename of the loaded resume, shown on the loaded card ("" = none yet).
        private string _loadedResumeName = "";

        // ── Resume card: swap between the empty upload prompt and the loaded state ──
        private void ResumeTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateResumeCardState();
            ResumeParser.InvalidateCache();
        }

        private void UpdateResumeCardState()
        {
            bool loaded = !string.IsNullOrWhiteSpace(ResumeTextBox.Text);
            if (ResumeEmptyState != null)
                ResumeEmptyState.Visibility = loaded ? Visibility.Collapsed : Visibility.Visible;
            if (ResumeLoadedState != null)
                ResumeLoadedState.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
            if (loaded && ResumeLoadedName != null)
                ResumeLoadedName.Text = string.IsNullOrWhiteSpace(_loadedResumeName)
                    ? "Resume loaded" : _loadedResumeName;
        }

        // Clicking anywhere on the resume card opens the file picker (drop also works).
        private void ResumeCard_Click(object sender, RoutedEventArgs e)
            => ResumeUploadBtn_Click(sender, e);

        private void ScreenAnalyzeBtn_Click(object sender, RoutedEventArgs e)
            => _ = HandleScreenAnalysisAsync();

        private void RegionAnalyzeBtn_Click(object sender, RoutedEventArgs e)
            => _ = HandleRegionScreenAnalysisAsync();

        // ── Resume: Upload button ─────────────────────────────────────────────
        private void ResumeUploadBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Upload Resume",
                Filter = "Document files|*.pdf;*.docx;*.txt|All files|*.*"
            };
            // The always-on-top overlay window would otherwise cover the file dialog,
            // making a click look like it did nothing. Lower it while the dialog is open
            // and own the dialog to this window so it comes to the front reliably.
            bool wasTopmost = answerWindow != null && answerWindow.Topmost;
            if (wasTopmost && answerWindow != null) answerWindow.Topmost = false;
            try
            {
                if (dlg.ShowDialog(this) == true)
                    LoadResumeFromFile(dlg.FileName);
            }
            finally
            {
                if (wasTopmost && answerWindow != null) answerWindow.Topmost = true;
            }
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
                    ".txt"  => System.Text.Encoding.UTF8.GetString(ReadFileShared(filePath)),
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

                _loadedResumeName = Path.GetFileName(filePath);
                ResumeTextBox.Text = text;   // fires TextChanged -> shows the loaded card
                DebugWindow.Log("RESUME", $"Loaded {text.Length} chars from {_loadedResumeName}");
                SaveCurrentResume();   // auto-save silently so it appears in history
                CollapseResumeForAsk();
                // Rebuild the speech engine's interview vocabulary from this resume (tools,
                // projects, names) and restart so it recognises those words during the
                // interview instead of mishearing them. Setup-time action — safe to restart.
                try { StartSpeechmaticsEngine(); } catch (Exception ex) { DebugWindow.Log("VOCAB", $"engine refresh skipped: {ex.Message}"); }
            }
            catch (Exception ex)
            {
                DebugWindow.Log("RESUME", $"File load failed: {ex.Message}");
                MessageBox.Show(this, $"Could not load file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CollapseResumeForAsk()
        {
            // The card reflects loaded/empty automatically via UpdateResumeCardState().
            UpdateResumeCardState();
        }

        private void ExpandResumeContent()
        {
            UpdateResumeCardState();
        }

        /// <summary>
        /// Text out of a .docx, with the gaps the document actually contains.
        ///
        /// Two things were being lost, both silently, and together they made the
        /// app misread a resume badly enough to misstate someone's career.
        ///
        /// Tabs produce no character. InnerText concatenates the runs and drops
        /// the tab elements between them, so a line laid out as
        /// "AI Engineer [tab] April 2024" arrives as "AI EngineerApril 2024".
        /// One resume held 23 of them. Everything that reads this text
        /// afterwards then has to cope with words fused to the words after them.
        ///
        /// Tables were skipped entirely. Elements&lt;Paragraph&gt;() returns only
        /// the paragraphs that are direct children of the body, and a paragraph
        /// inside a table is a child of a cell. Resumes are very often laid out
        /// in tables, and every one of those lines was simply absent.
        ///
        /// Tabs and line breaks become spaces and newlines. Nothing is inserted
        /// anywhere the document did not already have a gap, so a word split
        /// across two runs for formatting stays one word.
        /// </summary>
        /// <summary>
        /// Reads a file that another program may currently have open.
        ///
        /// Word, LibreOffice and Acrobat all hold a lock on the document while it
        /// is open, and every reader here used to open the path directly - which
        /// Windows refuses with "the process cannot access the file because it is
        /// being used by another process". Someone reading their own resume and
        /// then uploading it, which is the obvious order to do those two things
        /// in, hit that every time and the app simply said no.
        ///
        /// FileShare.ReadWrite says "I do not mind who else has this open". The
        /// content is copied into memory immediately, so nothing downstream holds
        /// the handle either.
        /// </summary>
        private static byte[] ReadFileShared(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.ToArray();
        }

        private static string ExtractDocxText(string filePath)
        {
            using var stream = new MemoryStream(ReadFileShared(filePath));
            using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(stream, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null) return "";

            var sb = new System.Text.StringBuilder();

            // Descendants, not Elements: this reaches paragraphs inside tables.
            foreach (var para in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
            {
                var line = new System.Text.StringBuilder();
                foreach (var node in para.Descendants())
                {
                    switch (node)
                    {
                        case DocumentFormat.OpenXml.Wordprocessing.TabChar:
                            line.Append(' ');
                            break;
                        case DocumentFormat.OpenXml.Wordprocessing.Break:
                            line.Append(' ');
                            break;
                        case DocumentFormat.OpenXml.Wordprocessing.Text t:
                            line.Append(t.Text);
                            break;
                    }
                }

                string text = line.ToString().Trim();
                if (text.Length == 0) continue;

                if (sb.Length + text.Length + Environment.NewLine.Length > MaxResumeTextChars)
                    throw new InvalidDataException("The document contains too much text.");
                sb.AppendLine(text);
            }

            return sb.ToString().Trim();
        }

        private static string ExtractPdfText(string filePath)
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(ReadFileShared(filePath));
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
        // SCREEN ANALYSIS  (F8 = the active screen  |  F9 = the primary monitor)
        // ══════════════════════════════════════════════════════════════════════
        private bool _isScreenAnalyzing = false;

        /// <summary>
        /// Size of the last screenshot sent, in KB. Recorded because the wait
        /// after pressing F8 is mostly this number travelling up a home
        /// connection, and that is the one part of the delay we can act on.
        /// </summary>
        private int _lastCaptureKb;

        private Task HandleScreenAnalysisAsync()            => RunScreenAnalysis(primaryOnly: false);
        private Task HandlePrimaryScreenAnalysisAsync()     => RunScreenAnalysis(primaryOnly: true);

        /// <summary>
        /// True while the interviewer is sharing their screen, so every question
        /// is answered from the screen without the user pressing anything.
        ///
        /// Deciding when to press Analyze is the hard part of this feature in a
        /// real interview. The interviewer says "so, solve this one" and by the
        /// time the user has thought about which key to press, they have been
        /// silent for three seconds in front of someone. Turning this on once,
        /// when sharing starts, removes the decision for the rest of the call.
        /// </summary>
        private bool _watchScreenMode;

        private void WatchScreenOption_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            ProfileDropdownPopup.IsOpen = false;
            SavedResumesPopup.IsOpen = false;
            ListeningModePopup.IsOpen = false;
            ToggleWatchScreen();
        }

        /// <summary>Reads the screen once, the same as F8 and the compact bar.</summary>
        private void ReadScreenPill_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => _ = HandleScreenAnalysisAsync();

        /// <summary>
        /// Flips the watch preference and tells the compact bar, which shows it
        /// as a colour on its Read screen button.
        ///
        /// Reached from Settings only. Both toolbars used to carry it as a
        /// switch, and it is not a thing anyone changes with an interviewer
        /// waiting — the button in each toolbar is now the one-shot read, which
        /// is what someone actually presses mid-interview.
        /// </summary>
        private void ToggleWatchScreen()
        {
            _watchScreenMode = !_watchScreenMode;
            SettingsWindow.SetWatchScreenEnabled(_watchScreenMode);
            UpdateWatchScreenUi();
            DebugWindow.Log("SCREEN", $"Screen-share watch {(_watchScreenMode ? "ON" : "OFF")}");

            // Watching means every question is a screen question, so the screen
            // can be captured before one is asked instead of during the silence
            // after it. Only while watching: taking the screen on a timer when
            // the user has not asked for that is not a performance decision.
            if (_watchScreenMode) StartPreparedShots();
            else                  StopPreparedShots();

            AiAnswerBox.Text = _watchScreenMode
                ? "Watching the shared screen.\n\n" +
                  "Every question now gets answered from your whole screen, so you do not need " +
                  "to press anything when they ask you to look at something, and it does not " +
                  "matter which window you clicked last.\n\n" +
                  "Keep the problem large on screen. The whole monitor is read at once, so a " +
                  "maximised window is easier for it than a small one.\n\n" +
                  "Turn this off when they stop sharing. F8 still reads just the window you are in."
                : "Stopped watching the screen.\n\n" +
                  "Questions are answered from what is said again. Press F8 any time you want " +
                  "the screen read.";
        }

        private void UpdateWatchScreenUi()
        {
            if (WatchScreenPillLabel == null || WatchScreenIcon == null) return;

            // The button is an action now, so the label stays put and only the
            // colour says whether screen answers are armed. A control whose
            // label changes under the cursor is read as a switch, and this one
            // is not: pressing it reads the screen either way.
            WatchScreenPillLabel.Text = "READ SCREEN";

            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                _watchScreenMode ? "#34E08A" : "#8FA3BA"));
            WatchScreenPillLabel.Foreground = brush;
            WatchScreenIcon.Foreground = brush;

            answerWindow?.SetWatchScreenState(_watchScreenMode);
        }

        /// <summary>
        /// True while the region picker is on screen. isProcessing and
        /// _isScreenAnalyzing are only set once a capture starts, and the picker
        /// runs before that, so nothing stopped a second F7 from opening a second
        /// full-screen selection overlay on top of the first. Both would be
        /// waiting for a drag, over the interview.
        /// </summary>
        private bool _regionPickerOpen;

        /// <summary>
        /// Times the wait the user actually feels: from releasing the key to the
        /// first word appearing. Server-side measurements kept saying the model
        /// answers in 0.15s while the person in front of the app was waiting
        /// seconds, because almost none of that time was the model.
        /// </summary>
        private Stopwatch? _turnStopwatch;
        private long _waitedForWordsMs;

        private async Task HandleRegionScreenAnalysisAsync()
        {
            if (isProcessing || _isScreenAnalyzing || _regionPickerOpen) return;

            bool? selected;
            RegionCaptureWindow picker;
            _regionPickerOpen = true;
            try
            {
                picker = new RegionCaptureWindow();
                selected = picker.ShowDialog();
            }
            finally { _regionPickerOpen = false; }
            if (selected != true || picker.SelectedRegion.Width <= 0 || picker.SelectedRegion.Height <= 0)
                return;

            await RunScreenAnalysis(primaryOnly: false, selectedRegion: picker.SelectedRegion);
        }

        /// <summary>
        /// Completes once the compositor has drawn a frame, so a caller that just
        /// hid a window knows the screen no longer contains it. Falls back to a
        /// short wait if no frame arrives, which keeps a capture from hanging on a
        /// machine where rendering has stalled.
        /// </summary>
        private static Task WaitForRenderedFrameAsync()
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            EventHandler? onRendered = null;
            onRendered = (_, _) =>
            {
                CompositionTarget.Rendering -= onRendered;
                done.TrySetResult(true);
            };
            CompositionTarget.Rendering += onRendered;

            return Task.WhenAny(done.Task, Task.Delay(250))
                       .ContinueWith(_ => CompositionTarget.Rendering -= onRendered,
                                     TaskScheduler.FromCurrentSynchronizationContext());
        }

        private async Task RunScreenAnalysis(bool primaryOnly, Int32Rect? selectedRegion = null)
        {
            // Guard: don't double-fire while already processing
            if (isProcessing || _isScreenAnalyzing) return;

            // Credits check — guests get 100 credits/month (tracked by device ID)
            if (!UserSession.IsUnlimited && _creditsFetched && UserSession.Credits < CreditsCriticalThreshold)
            {
                int remaining = UserSession.Credits;
                bool isGuest = !UserSession.IsLoggedIn;
                AiAnswerBox.Text = isGuest
                    ? $"Your guest session has {remaining} credit{(remaining == 1 ? "" : "s")} remaining.\n\n" +
                      "Screen AI and all features are available on a free account.\n\n" +
                      "Create a free Replysis AI account to get 100 credits each month,\n" +
                      "or upgrade to Pro for 5,000 credits/month with priority processing."
                    : $"Insufficient credits ({remaining} remaining).\n\n" +
                      "Upgrade your Replysis AI plan to continue\n" +
                      "using Screen AI and other advanced features.";
                return;
            }

            _isScreenAnalyzing = true;
            isProcessing       = true;
            UpdateMicUi();

            // ── Phase 1: scanning state ───────────────────────────────────────
            string captureLabel = selectedRegion.HasValue ? "the selected area" : primaryOnly ? "the main screen" : "your screen";
            ThinkingLabel.Text = $"Capturing {captureLabel}...";
            ThinkingHintLabel.Visibility = Visibility.Collapsed;
            ThinkingPanel.Visibility = Visibility.Visible;

            // Keep our own windows out of the screenshot.
            //
            // The way to do that is not to hide them. Windows can mark a window as
            // invisible to screen capture, and a marked window is simply absent
            // from the copied pixels while staying on screen for the person using
            // it. So there is nothing to hide, and nothing to wait for.
            //
            // This used to drop both windows to zero opacity and wait for the
            // compositor, which the user saw as the app blinking out and back on
            // every single capture. That blink is the whole reason this felt like
            // a screenshot tool instead of part of the app.
            //
            // Hiding remains as the fallback for Windows 10 builds older than
            // version 2004, where the capture flag does not exist.
            bool answerWasVisible = answerWindow?.IsVisible == true;
            AnswerWindow? cloakedAnswer = answerWasVisible ? answerWindow : null;

            bool mainCloaked = WindowStealth.TryBeginCaptureHidden(this, out bool mainWasExcluded);

            bool answerCloaked = true, answerWasExcluded = true;
            if (cloakedAnswer != null)
                answerCloaked = WindowStealth.TryBeginCaptureHidden(cloakedAnswer, out answerWasExcluded);

            bool cloaked = mainCloaked && answerCloaked;
            double savedOpacity = this.Opacity;

            void RestoreWindows()
            {
                WindowStealth.EndCaptureHidden(this, mainWasExcluded);
                WindowStealth.EndCaptureHidden(cloakedAnswer, answerWasExcluded);

                if (cloaked) return;
                this.Opacity = savedOpacity;
                // Window.Opacity back to 1.0; the visual opacity the user chose
                // stays controlled by MainBorder.Opacity.
                if (cloakedAnswer != null) cloakedAnswer.Opacity = 1.0;
            }

            if (!cloaked)
            {
                this.Opacity = 0;
                if (cloakedAnswer != null) cloakedAnswer.Opacity = 0;
                await WaitForRenderedFrameAsync();
                await Task.Delay(90);
            }
            else if (!mainWasExcluded || !answerWasExcluded)
            {
                // The flag was applied just now rather than already being on, so
                // give the compositor the single frame it needs to take effect.
                await WaitForRenderedFrameAsync();
            }

            // ── Phase 2: capture ──────────────────────────────────────────────
            byte[] imageBytes;
            try
            {
                imageBytes = await Task.Run(() => selectedRegion.HasValue
                    ? ScreenAnalyzer.CaptureRegion(
                        selectedRegion.Value.X, selectedRegion.Value.Y,
                        selectedRegion.Value.Width, selectedRegion.Value.Height)
                    : primaryOnly
                        ? ScreenAnalyzer.CapturePrimaryScreen()
                        : ScreenAnalyzer.CaptureScreen());
                _lastCaptureKb = imageBytes.Length / 1024;
                DebugWindow.Log("SCREEN", $"Captured {_lastCaptureKb} KB from {ScreenAnalyzer.LastCaptureTarget}");
            }
            catch (Exception ex)
            {
                DebugWindow.Log("SCREEN_ERR", ex.Message);
                RestoreWindows();
                AiAnswerBox.Text = "Screen capture failed. Press F12 for details.";
                _isScreenAnalyzing = false;
                StopThinkingUi();
                return;
            }

            // Undo whatever was needed to keep us out of the shot. When the capture
            // flag did the work this is a no-op the user never saw.
            RestoreWindows();

            // Clear overlay content so user sees a clean state while analysis streams in
            if (_isCameraMode && answerWindow != null)
            {
                answerWindow.UpdateAnswer("");
                answerWindow.UpdateQuestion("Analyzing screen...");
            }

            // ── Phase 3: stream from vision AI ───────────────────────────────
            // Deliberately not naming the model. The Groq vision model this used to
            // name is decommissioned and requests fall through to the other
            // provider, so a named label told the user something untrue.
            ThinkingLabel.Text = "Reading your screen...";

            string resumeCtx  = ResumeParser.ExtractFacts(ResumeTextBox.Text);
            string timestamp  = DateTime.Now.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture);

            // Name what was captured. An answer about the wrong window used to be
            // indistinguishable from a bad answer about the right one, so the only
            // way to tell was to reason about which window happened to be in front
            // when the key was pressed. Saying it outright turns that into a
            // mistake the user spots instantly.
            string target     = ScreenAnalyzer.LastCaptureTarget;
            string header     = string.IsNullOrWhiteSpace(target)
                ? $"📸 SCREEN  ·  {timestamp}\n\n"
                : $"📸 SCREEN  ·  {timestamp}  ·  {target}\n\n";

            // A hairline between this answer and the ones before it. A rule of 45
            // box characters read as the start of another section rather than the
            // end of one, and it competed with the answer for attention.
            string sep        = "\n" + new string('·', 12) + "\n\n";

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

                        // First token → hide the "analyzing..." indicator
                        if (tokenCount == 1)
                            ThinkingPanel.Visibility = Visibility.Collapsed;

                        // Paint the first few tokens the moment they land, then
                        // settle into batches of three. Batching from the start
                        // left a gap where the indicator had gone and no words had
                        // appeared yet, so the fastest part of the wait was the
                        // part that looked emptiest.
                        if (tokenCount <= 3 || tokenCount % 3 == 0 || token.Contains('\n'))
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
                    AiAnswerBox.Text = ex.Message;
                    if (_isCameraMode && answerWindow != null)
                        answerWindow.UpdateAnswer(ex.Message);
                    _ = FetchAndDisplayCreditsAsync();
                    return;
                }

                // ── Phase 4: post-process + finalise display ──────────────────────
                // PostProcess normalises section headers, removes stray markdown,
                // and collapses excess blank lines — runs instantly on the final string.
                string finalResult = ScreenAnalyzer.PostProcess(sb.ToString());
                if (string.IsNullOrWhiteSpace(finalResult))
                {
                    AiAnswerBox.Text = "Screen AI returned no answer. Please try again.";
                    return;
                }

                // The code goes to the SOLUTION panel, the same as it does for a
                // spoken answer.
                //
                // This path wrote straight to the text box instead, so the panel
                // and its Copy code button never appeared for a screen answer —
                // the one place they matter most, because a screen answer is
                // usually the code the candidate is about to paste. They were
                // left copying it out of a paragraph by hand, fence markers and
                // all, guessing where the code started and stopped. The panel
                // has existed the whole time; F8 simply never reached it.
                //
                // ShowAnswer fills the panel and leaves the prose behind, so the
                // header and the earlier answers are put back around that prose
                // afterwards rather than being fed through it — passing the whole
                // composed string in would pull code out of previous answers too.
                ShowAnswer(finalResult, scrollToEnd: false);

                string prose = AiAnswerBox.Text;
                AiAnswerBox.Text = string.IsNullOrWhiteSpace(previousAnswers)
                    ? $"{header}{prose}"
                    : $"{header}{prose}\n{sep}{previousAnswers}";
                AiAnswerBox.ScrollToHome();

                if (_isCameraMode && answerWindow != null)
                {
                    answerWindow.UpdateAnswer(finalResult);
                    answerWindow.UpdateQuestion("[Screen Analysis]");
                }

                // Persist to session log + history for smart follow-up voice questions
                AppendToSessionLog("[Screen Analysis]", finalResult);
                PromptBuilder.AddToHistory("Analyze what is currently on my screen", finalResult);
                _ = FetchAndDisplayCreditsAsync();

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
            SetResumePanelCollapsed(!_resumeCollapsed, animate: true);
        }

        private void SetResumePanelCollapsed(bool collapse, bool animate)
        {
            if (_resumeCollapsed == collapse) return;

            _resumeCollapsed = collapse;
            ResumeToggleBtn.Content = collapse ? "" : "";

            if (!animate)
            {
                ResumeColumn.BeginAnimation(System.Windows.Controls.ColumnDefinition.WidthProperty, null);
                ResumePanel.BeginAnimation(UIElement.OpacityProperty, null);
                ResumeColumn.Width = collapse ? new GridLength(0) : new GridLength(ResumePanelExpandedWidth);
                ResumePanel.Opacity = 1;
                ResumePanel.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
                return;
            }

            double fromWidth = Math.Max(0, ResumeColumn.ActualWidth);
            double toWidth = collapse ? 0 : ResumePanelExpandedWidth;
            double fromOpacity = collapse ? ResumePanel.Opacity : 0;
            double toOpacity = collapse ? 0 : 1;

            ResumePanel.Visibility = Visibility.Visible;
            if (!collapse)
            {
                ResumeColumn.BeginAnimation(System.Windows.Controls.ColumnDefinition.WidthProperty, null);
                ResumeColumn.Width = new GridLength(0);
                ResumePanel.Opacity = 0;
                fromWidth = 0;
            }

            var duration = new Duration(TimeSpan.FromMilliseconds(180));
            var widthAnimation = new GridLengthAnimation
            {
                From = new GridLength(fromWidth),
                To = new GridLength(toWidth),
                Duration = duration
            };
            var opacityAnimation = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = fromOpacity,
                To = toOpacity,
                Duration = duration
            };

            widthAnimation.Completed += (_, _) =>
            {
                if (_resumeCollapsed != collapse) return;

                ResumeColumn.BeginAnimation(System.Windows.Controls.ColumnDefinition.WidthProperty, null);
                ResumePanel.BeginAnimation(UIElement.OpacityProperty, null);
                ResumeColumn.Width = new GridLength(toWidth);
                ResumePanel.Opacity = 1;
                ResumePanel.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
            };

            ResumeColumn.BeginAnimation(System.Windows.Controls.ColumnDefinition.WidthProperty, widthAnimation);
            ResumePanel.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
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
            warmupTimer?.Stop();
            _engineMonitorTimer?.Stop();
            _sessionTimer?.Stop();
            _autoModeNoticeTimer?.Stop();
            _listeningMeterTimer?.Stop();
            _preparedShotTimer?.Stop();

            // Listening time accumulates between reports, and closing the app
            // mid-interview would have thrown that away. Reporting as you go
            // was meant to stop exactly this, and the last minute was the one
            // case still going unpaid: closing the window is the most likely
            // moment for it, not the least.
            //
            // Bounded, because a shutdown must not hang on a network call. If
            // it does not land in a second and a half the minute is lost, which
            // is the same as before and no worse.
            FlushListeningMeterOnExit();

            // The live transcript is the one thing left in plain text.
            //
            // Everything else the app keeps — the session transcripts, the saved
            // resumes, the job context, the cached speech token — is encrypted
            // for this Windows user. latest.txt is not, because the speech
            // engine writes it and cannot encrypt anything, and it held the last
            // thing an interviewer said until the next launch overwrote it.
            //
            // It has no value once the app is closed: it is a scratch file for
            // the turn in progress. Deleting it on the way out costs nothing and
            // leaves nothing behind.
            try
            {
                string livePath = Path.Combine(AppDataFolder, "latest.txt");
                if (File.Exists(livePath)) File.Delete(livePath);
            }
            catch { }

            // The job-context save is debounced by 500ms, so an edit typed just
            // before closing is still sitting in that window. Flush it before
            // stopping the timer, otherwise the edit is silently discarded.
            if (_jobContextSaveTimer?.IsEnabled == true) SaveJobContext();
            _jobContextSaveTimer?.Stop();

            // Unsubscribe camera events and close overlay window
            if (answerWindow != null && _cameraModeClosedHandler != null)
                answerWindow.CameraModeClosedByUser -= _cameraModeClosedHandler;
            try { answerWindow?.Close(); } catch { }
            answerWindow = null;
            try { _creditsWindow?.Close(); } catch { }
            _creditsWindow = null;
            // Sessions is a panel inside this window now, so there is nothing to
            // close - only its in-flight cloud requests to cancel.
            try { SessionsPanelHost?.Shutdown(); } catch { }

            _globalHotkey?.Dispose();
            _debugWindow?.ForceClose();
            bool hadActiveRecording = isRecording;
            string recordingId = _recordingSessionId;
            try { File.WriteAllText(Path.Combine(AppDataFolder, "shutdown.flag"), "1"); } catch { }
            EndSession();
            PresenceTracker.Stop();

            // Let the engine finish writing the current recording before killing it.
            // This used to be fire-and-forget running alongside the kill below, which
            // lost the race two ways: the process died mid-write, and once
            // KillAndDisposeEngine had nulled the field the wait saw no process and
            // reported success immediately, so an incomplete file was protected.
            // The process also exits as soon as this method returns, so a detached
            // task had no chance to finish. Bounded and shorter than the in-session
            // timeout so closing the app still feels immediate; anything still
            // unprotected is picked up by SecurePendingAudioRecordings on next launch.
            if (hadActiveRecording)
            {
                try
                {
                    bool saved = Task.Run(() =>
                        WaitForRecordingSaveAsync(recordingId, ShutdownRecordingSaveTimeoutMs))
                        .GetAwaiter().GetResult();
                    if (saved) ProtectRecording(sessionNumber);
                }
                catch (Exception ex)
                {
                    DebugWindow.Log("SESSION", $"Could not finish saving the recording on exit: {ex.Message}");
                }
            }

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
        // Tab-less side panel: Resume and Target Role are both always visible.
        // These remain so existing callers keep working.
        private void SwitchToResumeTab()
        {
            ExpandResumeContent();
            if (ResumeTextBox != null) ResumeTextBox.Focus();
        }

        private void SwitchToJobTab()
        {
            if (JobDescBox != null) JobDescBox.Focus();
        }

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

        // ══════════════════════════════════════════════════════════════════════
        // SCREENING DETAILS
        //
        // Work type, authorization, availability and pay are asked in the first
        // two minutes of nearly every contract screen, and none of them are on
        // a resume. Without them the app could only guess, and it guessed like
        // a model rather than like this candidate: asked "C2C, W2 or full time?"
        // it produced a paragraph about wanting to grow, which answers nothing
        // and sounds evasive to a recruiter who asked a yes/no question.
        //
        // The user is the only one who knows, so they are asked once, in setup,
        // and it is remembered. Anything left on "Not specified" is left out of
        // the prompt entirely, so a blank stays vague instead of becoming an
        // invented number.
        // ══════════════════════════════════════════════════════════════════════
        private const string PrefUnset = "Not specified";

        private static readonly string[] WorkTypeOptions =
        {
            PrefUnset, "C2C (corp to corp)", "W2 contract", "C2H (contract to hire)",
            "Full time / permanent", "1099 independent", "Open to any of these",
        };

        private static readonly string[] WorkAuthOptions =
        {
            PrefUnset, "US citizen", "Green card", "H1B", "H4 EAD", "OPT", "CPT",
            "TN visa", "L2 EAD", "Authorized, no sponsorship needed",
            "Will need sponsorship", "Prefer not to answer",
        };

        private static readonly string[] AvailabilityOptions =
        {
            PrefUnset, "Immediately", "1 week", "2 weeks", "3 weeks",
            "1 month", "2 months", "Flexible",
        };

        private static readonly string[] WorkLocationOptions =
        {
            PrefUnset, "Remote only", "Hybrid", "Onsite", "Open to relocation",
            "Remote, can travel", "Open to any of these",
        };

        private string _workType     = PrefUnset;
        private string _workAuth     = PrefUnset;
        private string _availability = PrefUnset;
        private string _workLocation = PrefUnset;
        private string _payExpectation = "";
        private bool   _screeningPrefsLoading;

        private void InitScreeningPrefCombos()
        {
            FillCombo(WorkTypeCombo,     WorkTypeOptions,     _workType);
            FillCombo(WorkAuthCombo,     WorkAuthOptions,     _workAuth);
            FillCombo(AvailabilityCombo, AvailabilityOptions, _availability);
            FillCombo(WorkLocationCombo, WorkLocationOptions, _workLocation);

            static void FillCombo(System.Windows.Controls.ComboBox box, string[] options, string selected)
            {
                if (box == null) return;
                box.Items.Clear();
                foreach (string option in options) box.Items.Add(option);
                box.SelectedItem = options.Contains(selected) ? selected : options[0];
            }
        }

        private void ScreeningPref_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_screeningPrefsLoading) return;
            _workType     = WorkTypeCombo?.SelectedItem     as string ?? PrefUnset;
            _workAuth     = WorkAuthCombo?.SelectedItem     as string ?? PrefUnset;
            _availability = AvailabilityCombo?.SelectedItem as string ?? PrefUnset;
            _workLocation = WorkLocationCombo?.SelectedItem as string ?? PrefUnset;
            PushScreeningPrefsToPrompt();
            ScheduleJobContextSave();
        }

        private void PayExpectationBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            _payExpectation = PayExpectationBox.Text;
            if (PayExpectationWatermark != null)
                PayExpectationWatermark.Visibility = string.IsNullOrWhiteSpace(_payExpectation)
                    ? Visibility.Visible : Visibility.Collapsed;
            if (_screeningPrefsLoading) return;
            PushScreeningPrefsToPrompt();
            ScheduleJobContextSave();
        }

        private void PushScreeningPrefsToPrompt()
        {
            PromptBuilder.WorkType       = Clean(_workType);
            PromptBuilder.WorkAuth       = Clean(_workAuth);
            PromptBuilder.Availability   = Clean(_availability);
            PromptBuilder.WorkLocation   = Clean(_workLocation);
            PromptBuilder.PayExpectation = (_payExpectation ?? "").Trim();

            static string Clean(string value) =>
                string.IsNullOrWhiteSpace(value) || value == PrefUnset ? "" : value;
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

            // Two lines, not one, and no icon chip.
            //
            // Every row here used to be built the same way as the Save and Clear
            // actions above and below it: a 26px icon chip and one line of text,
            // same size, same weight. Seven identical rows where one is a primary
            // action, five are data and one is destructive - so the list read as
            // filler rather than as a considered menu, and the name and the time
            // it was saved were crushed into a single string.
            //
            // A saved resume has two facts worth showing, and they are not equally
            // important: which resume it is, and when it was kept. Splitting them
            // gives the row a shape - a strong first line, a quiet second - which
            // is what makes a list look designed rather than generated. The chip
            // goes because six identical icons carry no information; a thin rail
            // marks the row instead, and turns accent green on the one currently
            // loaded, which is the one thing the list could never tell you before.
            foreach (var (name, content) in _savedResumes)
            {
                string cnt = content;
                bool isCurrent = !string.IsNullOrEmpty(_loadedResumeName)
                                 && string.Equals(name, _loadedResumeName, StringComparison.OrdinalIgnoreCase);

                // A real file name is the title, whole and unaltered - it is what
                // the person recognises. Only the older entries saved before the
                // name was carried through look like "Resume - Aug 18, 1:07 PM",
                // and for those the date is all there is, so it becomes the title
                // rather than being hidden as a subtitle under the word "Resume".
                string title = name, when = "";
                int sep = name.IndexOf('\u00b7');
                bool legacyDatedEntry = sep > 0
                    && name[..sep].Trim().Equals("Resume", StringComparison.OrdinalIgnoreCase);
                if (legacyDatedEntry)
                {
                    title = name[(sep + 1)..].Trim();   // the date, shown plainly
                    when  = "Saved before file names were kept";
                }

                var row = new System.Windows.Controls.Border
                {
                    Padding      = new Thickness(10, 7, 11, 7),
                    Margin       = new Thickness(1, 1, 1, 1),
                    CornerRadius = new CornerRadius(8),
                    Cursor       = System.Windows.Input.Cursors.Hand,
                    Background   = System.Windows.Media.Brushes.Transparent,
                };

                var layout = new System.Windows.Controls.Grid();
                layout.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
                    { Width = new GridLength(2) });
                layout.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
                    { Width = new GridLength(11) });
                layout.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());

                var rail = new System.Windows.Controls.Border
                {
                    CornerRadius = new CornerRadius(1),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                        isCurrent ? "#34E08A" : "#24405F")),
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                System.Windows.Controls.Grid.SetColumn(rail, 0);
                layout.Children.Add(rail);

                var text = new System.Windows.Controls.StackPanel();
                text.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text       = title,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                        isCurrent ? "#EAF1F8" : "#DCE6F0")),
                    FontSize   = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                if (when.Length > 0)
                {
                    text.Children.Add(new System.Windows.Controls.TextBlock
                    {
                        Text       = isCurrent ? when + "   ·   in use" : when,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                            isCurrent ? "#7FD8AC" : "#7E90A8")),
                        FontSize   = 10.5,
                        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                        Margin     = new Thickness(0, 2, 0, 0),
                    });
                }
                System.Windows.Controls.Grid.SetColumn(text, 2);
                layout.Children.Add(text);

                row.Child = layout;
                row.MouseEnter += (s2, _) => ((System.Windows.Controls.Border)s2).Background =
                    new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1AFFFFFF"));
                row.MouseLeave += (s2, _) => ((System.Windows.Controls.Border)s2).Background =
                    System.Windows.Media.Brushes.Transparent;
                string rowName = name;
                row.MouseLeftButtonDown += (_, _2) =>
                {
                    LoadSavedResume(cnt, rowName);
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
            string name = string.IsNullOrWhiteSpace(_loadedResumeName)
                ? "Resume · " + DateTime.Now.ToString("MMM d, h:mm tt", System.Globalization.CultureInfo.InvariantCulture)
                : _loadedResumeName;
            _savedResumes.Insert(0, (name, content));
            if (_savedResumes.Count > 10) _savedResumes.RemoveAt(_savedResumes.Count - 1);
            PersistSavedResumes();
            UpdateSavedResumesButton();
        }

        /// <summary>
        /// Puts a saved resume back in the box, and remembers what it is called.
        ///
        /// The name used to be dropped here: only the text was restored, so
        /// _loadedResumeName went empty and the next Save fell back to naming the
        /// entry "Resume - <date>". That is why a list of saved resumes filled up
        /// with timestamps instead of file names - the real name was known when
        /// the file was first uploaded and thrown away on the first reload.
        /// </summary>
        private void LoadSavedResume(string content, string name = "")
        {
            ResumeTextBox.Text = content;
            if (!string.IsNullOrWhiteSpace(name)) _loadedResumeName = name;
            PopulateSavedResumesPopup();
        }

        /// <summary>
        /// Puts the most recently saved resume back in the box on startup.
        ///
        /// The resumes were being saved and listed, but never reloaded, so every
        /// launch began with an empty box. Nothing said so: the panel is collapsed
        /// by default and an empty card looks like a normal one, and the user had
        /// uploaded the file days earlier and reasonably believed the app still
        /// had it.
        ///
        /// With no resume the model has no facts, so it answers as a generic
        /// software engineer. In session #277 it told a Gen AI and Python
        /// candidate to say they wanted to build on their "backend experience,
        /// especially with Java and Spring Boot". Every word was fluent and
        /// none of it was them. That is worse than an error message, because
        /// they were about to read it out loud.
        /// </summary>
        private void RestoreLastResume()
        {
            try
            {
                if (_savedResumes.Count == 0) return;
                if (!string.IsNullOrWhiteSpace(ResumeTextBox.Text)) return;

                var (name, content) = _savedResumes[0];
                if (string.IsNullOrWhiteSpace(content)) return;

                _loadedResumeName = name;
                ResumeTextBox.Text = content;   // fires TextChanged -> shows the loaded card
                DebugWindow.Log("RESUME", $"Restored last resume ({content.Length} chars): {name}");
            }
            catch { }
        }

        private void UpdateSavedResumesButton()
        {
            if (SavedResumesBtn == null) return;
            SavedResumesBtn.Visibility = _savedResumes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ══════════════════════════════════════════════════════════════════════
        // RESUME LOCK / UNLOCK
        // ══════════════════════════════════════════════════════════════════════
        // The resume is now upload-only (no editable text box), so there is nothing to
        // lock during an interview. Kept as no-ops so existing callers stay valid.
        private void LockResume()   { }
        private void UnlockResume() { }

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

                _workType     = Read("workType");
                _workAuth     = Read("workAuth");
                _availability = Read("availability");
                _workLocation = Read("workLocation");
                _payExpectation = doc.TryGetProperty("pay", out var p) ? p.GetString() ?? "" : "";

                string Read(string key) =>
                    doc.TryGetProperty(key, out var v) ? v.GetString() ?? PrefUnset : PrefUnset;
            }
            catch { }
            finally
            {
                // Filling the boxes raises SelectionChanged, which would write the
                // defaults straight back over what was just restored.
                _screeningPrefsLoading = true;
                try
                {
                    InitScreeningPrefCombos();
                    if (PayExpectationBox != null) PayExpectationBox.Text = _payExpectation;
                }
                finally { _screeningPrefsLoading = false; }
                PushScreeningPrefsToPrompt();
            }
        }

        private void SaveJobContext()
        {
            try
            {
                var obj = new
                {
                    company      = _companyName,
                    job          = _jobDescription,
                    workType     = _workType,
                    workAuth     = _workAuth,
                    availability = _availability,
                    workLocation = _workLocation,
                    pay          = _payExpectation,
                };
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
