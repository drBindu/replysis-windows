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
            "llama-3.3-70b-versatile",    // 3 — Groq free
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
        public SettingsWindow(int currentDeviceId)
        {
            InitializeComponent();
            LoadDevices();

            // Select current audio device
            if (currentDeviceId > -1)
            {
                for (int i = 0; i < deviceIndices.Count; i++)
                {
                    if (deviceIndices[i] == currentDeviceId)
                    {
                        AudioDeviceCombo.SelectedIndex = i;
                        break;
                    }
                }
            }

            // Load saved config into UI
            var cfg = LoadConfig();
            ModelCombo.SelectedIndex = Math.Clamp(cfg.ModelIndex, 0, ModelIds.Length - 1);
            ApiKeyBox.Text = cfg.ApiKey ?? "";
            SpeechmaticsKeyBox.Text = cfg.SpeechmaticsKey ?? "";
            BackendUrlBox.Text = cfg.BackendUrl ?? "";
            CoopilotEmailBox.Text = cfg.CoopilotEmail ?? "";
            TempSlider.Value = cfg.Temperature;
            TempLabel.Text = cfg.Temperature.ToString("F1");

            double mainOpPct = Math.Round(cfg.MainWindowOpacity * 100);
            double overlayOpPct = Math.Round(cfg.OverlayOpacity * 100);
            MainOpacitySlider.Value = Math.Clamp(mainOpPct, 10, 100);
            OverlayOpacitySlider.Value = Math.Clamp(overlayOpPct, 10, 100);
            MainOpacityLabel.Text = $"{(int)MainOpacitySlider.Value}%";
            OverlayOpacityLabel.Text = $"{(int)OverlayOpacitySlider.Value}%";
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
                        var parts = line.Split('|');
                        // Guard: skip malformed lines — int.Parse would throw and abort ALL devices
                        if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), out int deviceIndex))
                        {
                            deviceIndices.Add(deviceIndex);
                            AudioDeviceCombo.Items.Add(new ComboBoxItem
                            {
                                Content = parts[1].Trim(),
                                Foreground = System.Windows.Media.Brushes.White
                            });
                        }
                    }
                    if (AudioDeviceCombo.SelectedIndex == -1 && AudioDeviceCombo.Items.Count > 0)
                        AudioDeviceCombo.SelectedIndex = 0;
                }
                else
                {
                    AudioDeviceCombo.Items.Add(new ComboBoxItem
                    {
                        Content = "No devices — run engine first",
                        Foreground = System.Windows.Media.Brushes.Gray
                    });
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SETTINGS] LoadDevices failed: {ex.Message}"); }
        }

        // ═══════════════════════════════════════════════════════════════
        // EVENT HANDLERS
        // ═══════════════════════════════════════════════════════════════
        private void TempSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TempLabel != null)
                TempLabel.Text = e.NewValue.ToString("F1");
        }

        private void MainOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MainOpacityLabel != null)
                MainOpacityLabel.Text = $"{(int)e.NewValue}%";
        }

        private void OverlayOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (OverlayOpacityLabel != null)
                OverlayOpacityLabel.Text = $"{(int)e.NewValue}%";
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (AudioDeviceCombo.SelectedIndex > -1 && deviceIndices.Count > AudioDeviceCombo.SelectedIndex)
                SelectedDeviceIndex = deviceIndices[AudioDeviceCombo.SelectedIndex];

            var cfg = new AppConfig
            {
                ModelIndex = ModelCombo.SelectedIndex >= 0 ? ModelCombo.SelectedIndex : 0,
                ApiKey = ApiKeyBox.Text?.Trim() ?? "",
                SpeechmaticsKey = SpeechmaticsKeyBox.Text?.Trim() ?? "",
                BackendUrl = BackendUrlBox.Text?.Trim() ?? "",
                CoopilotEmail = CoopilotEmailBox.Text?.Trim() ?? "",
                Temperature = Math.Round(TempSlider.Value, 1),
                MainWindowOpacity = Math.Round(MainOpacitySlider.Value / 100.0, 2),
                OverlayOpacity = Math.Round(OverlayOpacitySlider.Value / 100.0, 2),
            };
            SaveConfig(cfg);

            SettingsChanged = true;
            this.Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e) => this.Close();

        // ═══════════════════════════════════════════════════════════════
        // CONFIG MODEL
        // ═══════════════════════════════════════════════════════════════
        public class AppConfig
        {
            public int ModelIndex { get; set; } = 0;
            public string ApiKey { get; set; } = "";
            public string SpeechmaticsKey { get; set; } = "";
            public string BackendUrl { get; set; } = "https://ai-powered-developer-assistance-platform.onrender.com/api/config/keys";
            public string CoopilotEmail { get; set; } = "";
            // WARNING: this default is a per-project Firebase Web API key (not a secret key).
            // It is safe to ship in a desktop app config for your own Firebase project,
            // but do NOT commit this to a public source repository.
            public string FirebaseApiKey { get; set; } = "AIzaSyAGGmuFpR0qkCHLI3q2cPv_o3cQlbIU8lE";
            public double Temperature { get; set; } = 0.2;
            public double MainWindowOpacity { get; set; } = 0.98;
            public double OverlayOpacity { get; set; } = 0.90;
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
                    string json = File.ReadAllText(ConfigPath);
                    _cachedConfig = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                    _cacheTime    = DateTime.UtcNow;
                    return _cachedConfig;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SETTINGS] LoadConfig failed: {ex.Message}"); }

            // First run / missing file — cache defaults too so getters don't keep hitting disk
            _cachedConfig = new AppConfig();
            _cacheTime    = DateTime.UtcNow;
            return _cachedConfig;
        }

        public static void SaveConfig(AppConfig cfg)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                string json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
                // Immediately update the in-memory cache so the next getter call
                // gets the new values without a disk round-trip.
                _cachedConfig = cfg;
                _cacheTime    = DateTime.UtcNow;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SETTINGS] SaveConfig failed: {ex.Message}"); }
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

        public static string GetApiKey() => LoadConfig().ApiKey ?? "";
        public static string GetFirebaseApiKey() => LoadConfig().FirebaseApiKey ?? "AIzaSyAGGmuFpR0qkCHLI3q2cPv_o3cQlbIU8lE";
        public static string GetSpeechmaticsKey() => LoadConfig().SpeechmaticsKey ?? "";
        public static string GetBackendUrl() => LoadConfig().BackendUrl ?? "";
        public static string GetCoopilotEmail() => LoadConfig().CoopilotEmail ?? "";
        public static double GetTemperature() => LoadConfig().Temperature;
        public static bool IsGemini() => LoadConfig().ModelIndex == 4;
        public static bool IsGroq() => LoadConfig().ModelIndex == 3;

        public static double GetMainWindowOpacity()
        {
            double v = LoadConfig().MainWindowOpacity;
            return Math.Clamp(v > 0 ? v : 0.98, 0.10, 1.0);
        }

        public static double GetOverlayOpacity()
        {
            double v = LoadConfig().OverlayOpacity;
            return Math.Clamp(v > 0 ? v : 0.90, 0.10, 1.0);
        }
    }
}