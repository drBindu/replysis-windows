using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace InterviewCopilot
{
    public partial class SettingsWindow : Window
    {
        // A clean install has no config.json, so every first run uses this value.
        // It must be the live domain: the old one no longer serves the site.
        private const string DefaultBackendUrl = "https://replysis.com";
        public bool SettingsChanged { get; set; } = false;
        public int SelectedDeviceIndex { get; set; } = -1;
        private List<int> deviceIndices = new List<int>();

        // ── Config file — persists in AppData ──
        private static string ConfigDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InterviewCopilot");

        private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

        // ── Model definitions (index matches ComboBox order in XAML) ──
        public static readonly string[] ModelIds = {
            "gpt-4.1",                    // 0 — Best quality
            "gpt-4o-mini",                // 1 — Fast + cheap
            "gpt-4o",                     // 2 — Balanced
            // Groq shut down the Llama models on 2026-08-16. The backend picks the
            // real model; this list only labels session records, so it has to match
            // or every saved session is stamped with a model that no longer exists.
            "openai/gpt-oss-20b",         // 3 — Groq fast
            "gemini-2.0-flash",           // 4 — Google
        };

        public static readonly string[] ModelEndpoints = {
            "https://api.openai.com/v1/chat/completions",       // 0
            "https://api.openai.com/v1/chat/completions",       // 1
            "https://api.openai.com/v1/chat/completions",       // 2
            "https://api.groq.com/openai/v1/chat/completions",  // 3
            "https://generativelanguage.googleapis.com/v1beta/", // 4
        };

        // ═══════════════════════════════════════════════════════════════
        // CONSTRUCTOR
        // ═══════════════════════════════════════════════════════════════
        // Groq = model index 3, OpenAI GPT-4o = model index 2
        private const int IdxGroq   = 3;
        private const int IdxOpenAi = 2;
        private const double PreferredDialogWidth = 460;
        private const double PreferredDialogHeight = 560;
        private const double OwnerInset = 32;
        private readonly bool _autoModeActive;
        private readonly bool _autoModeUsesMic;
        private bool _savedMicCaptureEnabled;

        public SettingsWindow(int currentDeviceId, bool autoModeActive = false, bool autoModeUsesMic = false)
        {
            InitializeComponent();
            _autoModeActive = autoModeActive;
            _autoModeUsesMic = autoModeUsesMic;
            try { WindowStealth.SetStealthMode(this, GetStealthMode()); } catch { }
            Loaded += SettingsWindow_Loaded;
            LoadDevices();

            if (currentDeviceId > -1)
                for (int i = 0; i < deviceIndices.Count; i++)
                    if (deviceIndices[i] == currentDeviceId) { AudioDeviceCombo.SelectedIndex = i; break; }

            var cfg = LoadConfig();
            _savedMicCaptureEnabled = cfg.MicCaptureEnabled;
            BackendUrlBox.Text      = cfg.BackendUrl        ?? "";
            CoopilotEmailBox.Text   = cfg.CoopilotEmail     ?? "";
            TempSlider.Value        = cfg.Temperature;

            MicBothRadio.IsChecked = _autoModeActive
                ? _autoModeUsesMic
                : cfg.MicCaptureEnabled;
            MicSystemRadio.IsChecked = _autoModeActive
                ? !_autoModeUsesMic
                : !cfg.MicCaptureEnabled;
            AutoModeAudioNotice.Visibility = _autoModeActive ? Visibility.Visible : Visibility.Collapsed;
            MicBothRadio.IsHitTestVisible = !_autoModeActive;
            MicSystemRadio.IsHitTestVisible = !_autoModeActive;
            if (_autoModeActive)
            {
                AutoModeAudioNoticeTitle.Text = _autoModeUsesMic
                    ? "PRACTICE AUTO ACTIVE"
                    : "INTERVIEW AUTO ACTIVE";
                AutoModeAudioNoticeBody.Text = _autoModeUsesMic
                    ? "Your microphone is temporarily included so you can ask questions without a meeting. Your saved manual choice is unchanged."
                    : "System audio only is temporarily active for meeting questions. Your microphone stays off and your saved manual choice is unchanged.";
            }
            CloudSyncCheckBox.IsChecked = cfg.CloudSyncEnabled;
            StealthCheckBox.IsChecked   = cfg.StealthMode;
            RefreshMicCards();

            LoadLanguages(cfg.TranscriptLanguage);

            double sharedSliderVal = Math.Clamp(1 + (cfg.MainWindowOpacity - 0.50) / 0.50 * 99, 1, 100);
            MainOpacitySlider.Value    = Math.Round(sharedSliderVal);
            OverlayOpacitySlider.Value = Math.Round(sharedSliderVal);

            // Map stored model index → Groq or OpenAI radio. Groq (fast) is the default;
            // only an explicit OpenAI pick (index 2) shows OpenAI, matching IsGroq().
            bool useGroq = cfg.ModelIndex != IdxOpenAi;
            ModelGroqRadio.IsChecked   = useGroq;
            ModelOpenAiRadio.IsChecked = !useGroq;
            RefreshModelCards();

            // About
            // On an installed copy this is the package version the updater
            // compares against, so the number here is the one that decides whether
            // an update is offered.
            VersionLabel.Text      = UpdateService.CurrentVersion;
            AccountLabel.Text      = UserSession.IsLoggedIn
                                         ? (UserSession.Email ?? "Signed in")
                                         : "Free trial (not signed in)";
            // Always use real credit count from backend — guests get 100/month tracked by device ID
            if (UserSession.IsUnlimited)
                CreditsAboutLabel.Text = "Unlimited";
            else if (UserSession.Credits > 0)
                CreditsAboutLabel.Text = $"{UserSession.Credits} left";
            else
                CreditsAboutLabel.Text = UserSession.IsLoggedIn ? "0 left" : "Loading...";
            SignInSettingsBtn.Visibility = UserSession.IsLoggedIn
                                         ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            double availableWidth = SystemParameters.WorkArea.Width - OwnerInset;
            double availableHeight = SystemParameters.WorkArea.Height - OwnerInset;

            if (Owner != null && Owner.ActualWidth > 0 && Owner.ActualHeight > 0)
            {
                availableWidth = Math.Min(availableWidth, Owner.ActualWidth - OwnerInset);
                availableHeight = Math.Min(availableHeight, Owner.ActualHeight - OwnerInset);
            }

            MaxWidth = Math.Max(MinWidth, availableWidth);
            MaxHeight = Math.Max(MinHeight, availableHeight);
            Width = Math.Min(PreferredDialogWidth, MaxWidth);
            Height = Math.Min(PreferredDialogHeight, MaxHeight);
        }

        // ═══════════════════════════════════════════════════════════════
        // AUDIO DEVICES
        // ═══════════════════════════════════════════════════════════════
        private void LoadDevices()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "devices.txt");
                if (!File.Exists(path))
                    path = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "InterviewCopilot", "devices.txt");
                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('|', 2);
                        // Guard: skip malformed lines — int.Parse would throw and abort ALL devices
                        if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), out int deviceIndex))
                        {
                            string name = parts[1].Trim();
                            // Hide legacy MME passthroughs — they aren't real capture devices
                            // and cause the engine to close/restart mid-session. Keeping the
                            // list to real microphones is both more stable and more premium.
                            string lower = name.ToLowerInvariant();
                            if (lower.Contains("sound mapper") || lower.Contains("primary sound capture"))
                                continue;
                            deviceIndices.Add(deviceIndex);
                            // Add plain strings (not ComboBoxItem objects) so the dark
                            // themed item styling actually applies.
                            AudioDeviceCombo.Items.Add(name);
                        }
                    }
                    if (AudioDeviceCombo.SelectedIndex == -1 && AudioDeviceCombo.Items.Count > 0)
                        AudioDeviceCombo.SelectedIndex = 0;
                }
                else
                {
                    AudioDeviceCombo.Items.Add("No devices found (start the app first)");
                }
            }
            catch (Exception ex) { DebugWindow.Log("SETTINGS", $"LoadDevices failed: {ex.Message}"); }
        }

        // Interview languages offered in Settings. Left = display name, Right = Speechmatics
        // language code passed to the engine (--language). Kept to widely-used, well-supported
        // languages; extend this list to add more.
        // Languages the engine can transcribe. Most run on Speechmatics ("enhanced"
        // operating point). Telugu (and other Speechmatics-gaps) are routed to Sarvam AI
        // by the engine — those need a Sarvam API key set in config.
        private static readonly (string Name, string Code)[] Languages =
        {
            ("English",              "en"),
            ("Hindi",                "hi"),
            ("Tamil",                "ta"),
            ("Telugu (Sarvam AI)",   "te"),
            ("Bengali",              "bn"),
            ("Marathi",              "mr"),
            ("Urdu",                 "ur"),
            ("Spanish",              "es"),
            ("French",               "fr"),
            ("German",               "de"),
            ("Portuguese",           "pt"),
            ("Italian",              "it"),
            ("Mandarin Chinese",     "cmn"),
            ("Japanese",             "ja"),
            ("Korean",               "ko"),
            ("Arabic",               "ar"),
            ("Russian",              "ru"),
        };

        private void LoadLanguages(string savedCode)
        {
            try
            {
                LanguageCombo.Items.Clear();
                int selected = 0;
                for (int i = 0; i < Languages.Length; i++)
                {
                    LanguageCombo.Items.Add(Languages[i].Name);
                    if (string.Equals(Languages[i].Code, savedCode, StringComparison.OrdinalIgnoreCase))
                        selected = i;
                }
                LanguageCombo.SelectedIndex = selected;
            }
            catch (Exception ex) { DebugWindow.Log("SETTINGS", $"LoadLanguages failed: {ex.Message}"); }
        }

        private string SelectedLanguageCode()
        {
            int i = LanguageCombo.SelectedIndex;
            return (i >= 0 && i < Languages.Length) ? Languages[i].Code : "en";
        }

        // ═══════════════════════════════════════════════════════════════
        // EVENT HANDLERS
        // ═══════════════════════════════════════════════════════════════
        private void ModelRadio_Checked(object sender, RoutedEventArgs e) => RefreshModelCards();
        private void MicRadio_Checked(object sender, RoutedEventArgs e)  => RefreshMicCards();

        private void RefreshModelCards()
        {
            bool groq = ModelGroqRadio.IsChecked == true;
            ApplyCardStyle(GroqCard,   GroqDot,   groq);
            ApplyCardStyle(OpenAiCard, OpenAiDot, !groq);
        }

        private void RefreshMicCards()
        {
            bool sysOnly = MicSystemRadio.IsChecked == true;
            ApplyCardStyle(MicSystemCard, MicSystemDot,  sysOnly);
            ApplyCardStyle(MicBothCard,   MicBothDot,   !sysOnly);
        }

        private static void ApplyCardStyle(System.Windows.Controls.Border card,
                                           System.Windows.Shapes.Ellipse dot, bool selected)
        {
            card.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                    selected ? "#18FFFFFF" : "#0AFFFFFF"));
            card.BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                    selected ? "#40FFFFFF" : "#18FFFFFF"));
            card.BorderThickness = new Thickness(1);
            dot.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }

        public bool SignInRequested { get; private set; } = false;

        private void SignInSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            SignInRequested = true;
            this.Close();
        }

        private async void CheckUpdatesBtn_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as System.Windows.Controls.Button;
            if (btn != null) { btn.IsEnabled = false; btn.Content = "Checking..."; }

            string current = InstalledVersion();

            // An installed copy updates itself, so the button downloads the new
            // version rather than pointing the user at a website and leaving them
            // to reinstall by hand.
            if (UpdateService.IsManaged)
            {
                try
                {
                    if (btn != null) btn.Content = "Downloading...";
                    string? staged = await UpdateService.CheckAndStageAsync();

                    if (staged == null)
                    {
                        MessageBox.Show(this,
                            $"You are up to date ({UpdateService.CurrentVersion}).",
                            "Replysis AI", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    // Restarting is offered, never taken. Someone may be sitting in
                    // an interview with this window open.
                    var answer = MessageBox.Show(this,
                        $"Replysis {staged} has been downloaded and is ready.\n\n" +
                        "It installs by itself the next time you close and reopen Replysis. " +
                        "Restart now instead?",
                        "Update Ready", MessageBoxButton.YesNo, MessageBoxImage.Information);

                    if (answer == MessageBoxResult.Yes) UpdateService.ApplyAndRestart();
                }
                finally
                {
                    if (btn != null) { btn.IsEnabled = true; btn.Content = "Check for Updates..."; }
                }
                return;
            }

            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get,
                    "https://api.github.com/repos/drBindu/replysis-windows/releases/latest");
                req.Headers.TryAddWithoutValidation("User-Agent", "Replysis-Win");
                req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

                using var resp = await SharedHttpClient.Http.SendAsync(req, cts.Token);

                // A failed call is reported as a failed call. Falling through to
                // the comparison here is how this used to answer "up to date"
                // after GitHub returned an error.
                if (!resp.IsSuccessStatusCode)
                {
                    DebugWindow.Log("UPDATE", $"Release lookup failed: HTTP {(int)resp.StatusCode}");
                    ShowUpdateCheckFailed(current);
                    return;
                }

                string json = await resp.Content.ReadAsStringAsync(cts.Token);
                using var doc = System.Text.Json.JsonDocument.Parse(json);

                string? tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                string? latest = ParseWindowsReleaseTag(tag);

                if (latest == null)
                {
                    DebugWindow.Log("UPDATE", $"Unrecognised release tag: {tag ?? "(none)"}");
                    ShowUpdateCheckFailed(current);
                    return;
                }

                if (IsVersionNewer(latest, current))
                {
                    MessageBox.Show(this,
                        $"A new version is available: {latest}\nYou have {current}.\n\n" +
                        "Open Replysis on the web to download it when you are ready.",
                        "Update Available", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(this,
                        $"You are up to date ({current}).",
                        "Replysis AI", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                DebugWindow.Log("UPDATE", $"Update check failed: {ex.GetType().Name}");
                ShowUpdateCheckFailed(current);
            }
            finally
            {
                if (btn != null) { btn.IsEnabled = true; btn.Content = "Check for Updates..."; }
            }
        }

        // Never claims a verdict it does not have.
        private void ShowUpdateCheckFailed(string current)
        {
            MessageBox.Show(this,
                $"We could not check for updates just now. You have {current}.\n\n" +
                "Replysis is still fully usable. Please try again later.",
                "Replysis AI", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>
        /// Releases for this app are tagged <c>windows-v1.0.11.0</c>. The prefix
        /// keeps Windows releases separate from the macOS ones in the same
        /// account, so a tag without it is not a Windows build and must not be
        /// compared against the installed version.
        /// </summary>
        internal static string? ParseWindowsReleaseTag(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;

            string value = tag.Trim();
            const string prefix = "windows-v";
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

            value = value.Substring(prefix.Length);
            return Version.TryParse(value, out Version? parsed) ? parsed.ToString() : null;
        }

        /// <summary>
        /// The shipping version. AssemblyVersion is set from the .csproj and is
        /// kept equal to the Identity version in Package.appxmanifest, so this
        /// is the number the user actually has installed.
        /// </summary>
        internal static string InstalledVersion()
        {
            Version? v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v?.ToString(4) ?? "0.0.0.0";
        }

        private void TempSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TempLabel != null) TempLabel.Text = e.NewValue.ToString("F1");
        }
        private void MainOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MainOpacityLabel != null) MainOpacityLabel.Text = $"{(int)e.NewValue}%";
        }
        private void OverlayOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (OverlayOpacityLabel != null) OverlayOpacityLabel.Text = $"{(int)e.NewValue}%";
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (AudioDeviceCombo.SelectedIndex > -1 && deviceIndices.Count > AudioDeviceCombo.SelectedIndex)
                SelectedDeviceIndex = deviceIndices[AudioDeviceCombo.SelectedIndex];

            int modelIdx = ModelGroqRadio.IsChecked == true ? IdxGroq : IdxOpenAi;

            var cfg = new AppConfig
            {
                ModelIndex        = modelIdx,
                BackendUrl        = BackendUrlBox.Text?.Trim()       ?? "",
                CoopilotEmail     = CoopilotEmailBox.Text?.Trim()    ?? "",
                Temperature       = Math.Round(TempSlider.Value, 1),
                MainWindowOpacity = Math.Round(0.50 + (MainOpacitySlider.Value - 1) / 99.0 * 0.50, 2),
                OverlayOpacity    = Math.Round(0.50 + (MainOpacitySlider.Value - 1) / 99.0 * 0.50, 2),
                MicCaptureEnabled = _autoModeActive
                    ? _savedMicCaptureEnabled
                    : MicBothRadio.IsChecked == true,
                AudioDeviceIndex  = SelectedDeviceIndex,   // persist the chosen mic across restarts
                CloudSyncEnabled  = CloudSyncCheckBox.IsChecked == true,
                StealthMode       = StealthCheckBox.IsChecked == true,
                TranscriptLanguage = SelectedLanguageCode(),
                // Carry the Sarvam key through — it's not an editable field here, so pull the
                // stored value. Without this, saving Settings would wipe it (fresh AppConfig).
                SarvamApiKey      = GetSarvamApiKey(),
            };
            if (!SaveConfig(cfg))
            {
                MessageBox.Show(this,
                    "Your settings could not be saved. Nothing was changed. Please check that the InterviewCopilot folder in Local AppData is writable, then try again.",
                    "Settings Not Saved", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SettingsChanged = true;
            this.Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e) => this.Close();

        /// <summary>
        /// Looks up the latest published Windows release and returns its version if
        /// it is newer than the installed one, otherwise null. Silent by design: no
        /// dialogs, no state changes, and every failure is treated as "nothing to
        /// report", so a GitHub outage never interrupts an interview. The caller
        /// decides how to surface it. Used by the automatic check on launch, which
        /// is how someone who installed an older build learns an update exists
        /// without having to open Settings and press a button.
        /// </summary>
        internal static async System.Threading.Tasks.Task<string?> GetNewerVersionOrNullAsync(
            System.Threading.CancellationToken ct = default)
        {
            try
            {
                using var timeout = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));

                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get,
                    "https://api.github.com/repos/drBindu/replysis-windows/releases/latest");
                req.Headers.TryAddWithoutValidation("User-Agent", "Replysis-Win");
                req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

                using var resp = await SharedHttpClient.Http.SendAsync(req, timeout.Token);
                if (!resp.IsSuccessStatusCode) return null;

                string json = await resp.Content.ReadAsStringAsync(timeout.Token);
                using var doc = System.Text.Json.JsonDocument.Parse(json);

                string? tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                string? latest = ParseWindowsReleaseTag(tag);
                if (latest == null) return null;

                return IsVersionNewer(latest, InstalledVersion()) ? latest : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsVersionNewer(string candidate, string current) =>
            Version.TryParse(candidate, out Version? candidateVersion) &&
            Version.TryParse(current, out Version? currentVersion) &&
            candidateVersion.CompareTo(currentVersion) > 0;

        // ═══════════════════════════════════════════════════════════════
        // CONFIG MODEL
        // ═══════════════════════════════════════════════════════════════
        public class AppConfig
        {
            public int    ModelIndex         { get; set; } = 3;  // 3 = Groq (fast) by default
            public string BackendUrl         { get; set; } = DefaultBackendUrl;
            public string CoopilotEmail      { get; set; } = "";
            // WARNING: this default is a per-project Firebase Web API key (not a secret key).
            // It is safe to ship in a desktop app config for your own Firebase project,
            // but do NOT commit this to a public source repository.
            public string FirebaseApiKey     { get; set; } = "AIzaSyAGGmuFpR0qkCHLI3q2cPv_o3cQlbIU8lE";
            public double Temperature        { get; set; } = 0.2;
            // Default ~25% on the slider = mostly transparent glass out of the box.
            public double MainWindowOpacity  { get; set; } = 0.62;
            public double OverlayOpacity     { get; set; } = 0.62;
            // true  = system audio + mic (default)
            // false = system audio only  (mic never opened — fully invisible, no OS mic indicator)
            public bool   MicCaptureEnabled  { get; set; } = true;
            // Chosen microphone's PyAudio device index. -1 = use the Windows default input.
            // Persisted so the user's mic choice survives app restarts instead of silently
            // reverting to whatever Windows currently calls the default device.
            public int    AudioDeviceIndex   { get; set; } = -1;
            // Cloud backups contain interview transcript content. Keep them off
            // until the signed-in user explicitly opts in from Settings.
            public bool   CloudSyncEnabled   { get; set; } = false;
            // Stealth = exclude from screen capture + hide from taskbar/Alt-Tab.
            // On by default so the window is invisible to recorders out of the box.
            public bool   StealthMode        { get; set; } = true;
            // Speechmatics transcription language code (en, hi, te, ta, es, fr, de, ...).
            // The engine hears ONLY this language, so it must match the interview language;
            // otherwise other-language speech comes out as garbled words in this language.
            public string TranscriptLanguage { get; set; } = "en";
            // Sarvam AI API key — used only for languages Speechmatics can't do (Telugu, etc.).
            // Stored encrypted like every other secret; empty until the user provides one.
            public string SarvamApiKey       { get; set; } = "";
        }

        // ═══════════════════════════════════════════════════════════════
        // PERSISTENCE  (with in-memory cache — avoids disk read on every
        // static getter call; cache is invalidated on every SaveConfig)
        // ═══════════════════════════════════════════════════════════════
        private static AppConfig?  _cachedConfig = null;
        private static DateTime    _cacheTime    = DateTime.MinValue;
        private const  double      CacheTtlSec   = 60.0; // 60-second TTL is safe for config

        public static AppConfig LoadConfig()
        {
            // Fast path — return cached value if still fresh
            if (_cachedConfig != null &&
                (DateTime.UtcNow - _cacheTime).TotalSeconds < CacheTtlSec)
                return _cachedConfig;

            try
            {
                if (File.Exists(ConfigPath))
                {
                    string raw = File.ReadAllText(ConfigPath);
                    bool isProtected = SecureDataProtector.IsProtected(raw);
                    string json = raw;
                    if (isProtected && !SecureDataProtector.TryUnprotect(raw, out json))
                        throw new InvalidDataException("Could not decrypt settings file.");
                    _cachedConfig = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                    _cacheTime    = DateTime.UtcNow;
                    if (!isProtected)
                        SaveConfig(_cachedConfig);
                    return _cachedConfig;
                }
            }
            catch (Exception ex) { DebugWindow.Log("SETTINGS", $"LoadConfig failed: {ex.Message}"); }

            // First run / missing file — cache defaults too so getters don't keep hitting disk
            _cachedConfig = new AppConfig();
            _cacheTime    = DateTime.UtcNow;
            return _cachedConfig;
        }

        public static bool SaveConfig(AppConfig cfg)
        {
            string tmp = "";
            try
            {
                Directory.CreateDirectory(ConfigDir);
                string json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                tmp = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, SecureDataProtector.Protect(json));
                File.Move(tmp, ConfigPath, overwrite: true);
                // Immediately update the in-memory cache so the next getter call
                // gets the new values without a disk round-trip.
                _cachedConfig = cfg;
                _cacheTime    = DateTime.UtcNow;
                return true;
            }
            catch (Exception ex)
            {
                DebugWindow.Log("SETTINGS", $"SaveConfig failed: {ex.Message}");
                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tmp) && File.Exists(tmp))
                {
                    try { File.Delete(tmp); }
                    catch (Exception ex) { DebugWindow.Log("SETTINGS", $"Temporary config cleanup failed: {ex.Message}"); }
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // STATIC HELPERS
        // ═══════════════════════════════════════════════════════════════
        public static string GetActiveModelId()
        {
            var cfg = LoadConfig();
            int idx = Math.Clamp(cfg.ModelIndex, 0, ModelIds.Length - 1);
            return ModelIds[idx];
        }

        public static string GetActiveEndpoint()
        {
            var cfg = LoadConfig();
            int idx = Math.Clamp(cfg.ModelIndex, 0, ModelEndpoints.Length - 1);
            return ModelEndpoints[idx];
        }

        public static string GetFirebaseApiKey() => LoadConfig().FirebaseApiKey ?? "AIzaSyAGGmuFpR0qkCHLI3q2cPv_o3cQlbIU8lE";
        public static string GetBackendUrl()
        {
            string value = (LoadConfig().BackendUrl ?? "").Trim().TrimEnd('/');
            if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
                string.IsNullOrEmpty(uri.Query) &&
                string.IsNullOrEmpty(uri.Fragment) &&
                ((uri.Scheme == Uri.UriSchemeHttps &&
                  (uri.Host.Equals("coopilotxai.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.Equals("www.coopilotxai.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.Equals("replysis.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.Equals("www.replysis.com", StringComparison.OrdinalIgnoreCase))) ||
                 (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
                return value;

            return DefaultBackendUrl;
        }
        public static string GetCoopilotEmail() => LoadConfig().CoopilotEmail ?? "";
        public static bool IsCloudSyncEnabled() => LoadConfig().CloudSyncEnabled;
        public static double GetTemperature() => LoadConfig().Temperature;
        public static bool IsGemini() => LoadConfig().ModelIndex == 4;
        // Groq is the FAST provider and the default for instant answers. Only route
        // to OpenAI when the user has explicitly picked it (index 2) in Settings — every
        // other value, including the legacy default (0), uses fast Groq.
        public static bool IsGroq() => LoadConfig().ModelIndex != IdxOpenAi;

        public static bool   GetMicCaptureEnabled() => LoadConfig().MicCaptureEnabled;
        public static int    GetAudioDeviceIndex()  => LoadConfig().AudioDeviceIndex;
        public static string GetTranscriptLanguage()
        {
            var lang = LoadConfig().TranscriptLanguage;
            return string.IsNullOrWhiteSpace(lang) ? "en" : lang.Trim();
        }
        public static string GetSarvamApiKey() => (LoadConfig().SarvamApiKey ?? "").Trim();
        public static bool   GetStealthMode()       => LoadConfig().StealthMode;

        public static double GetMainWindowOpacity()
        {
            double value = LoadConfig().MainWindowOpacity;
            return Math.Clamp(value > 0 ? value : 0.62, 0.50, 1.0);
        }

        // Eye mode overlay always uses the same opacity as the main window
        public static double GetOverlayOpacity() => GetMainWindowOpacity();
    }
}
