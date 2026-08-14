using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace InterviewCopilot
{
    public partial class PreflightWindow : Window
    {
        private readonly Func<bool> _audioReady;
        private readonly bool _stealthEnabled;

        public bool ChecksPassed { get; private set; }

        public PreflightWindow(Func<bool> audioReady, bool stealthEnabled)
        {
            InitializeComponent();
            _audioReady = audioReady;
            _stealthEnabled = stealthEnabled;
            Loaded += async (_, _) => await RunChecksAsync();
        }

        private async void Run_Click(object sender, RoutedEventArgs e) => await RunChecksAsync();

        private async Task RunChecksAsync()
        {
            RunButton.IsEnabled = false;
            RunButton.Content = "Checking…";
            ContinueButton.IsEnabled = false;
            SetState(NetworkState, NetworkDetail, "CHECKING", "Contacting Replysis…", "#93C5FD");
            SetState(AudioState, AudioDetail, "CHECKING", "Checking the transcription engine…", "#93C5FD");
            SetState(PrivacyState, PrivacyDetail, "CHECKING", "Testing the Windows capture-protection API…", "#93C5FD");

            bool networkOk = false;
            try
            {
                var timer = Stopwatch.StartNew();
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{SettingsWindow.GetBackendUrl().TrimEnd('/')}/api/healthz");
                using var response = await SharedHttpClient.HttpShort.SendAsync(request);
                timer.Stop();
                networkOk = response.IsSuccessStatusCode;
                SetState(NetworkState, NetworkDetail, networkOk ? "READY" : "FIX NEEDED",
                    networkOk ? $"Connected in {timer.ElapsedMilliseconds} ms." : "Replysis did not return a healthy response. Check the network or VPN.",
                    networkOk ? "#4ADE80" : "#F87171");
            }
            catch
            {
                SetState(NetworkState, NetworkDetail, "FIX NEEDED", "Replysis could not be reached. Check the network or VPN, then run again.", "#F87171");
            }

            bool audioOk = _audioReady();
            SetState(AudioState, AudioDetail, audioOk ? "READY" : "FIX NEEDED",
                audioOk ? "The transcription engine is online and ready to listen." : "The audio engine is still connecting. Wait a moment, confirm the selected input, then run again.",
                audioOk ? "#4ADE80" : "#F87171");

            bool privacyApiOk = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
            if (_stealthEnabled && Owner != null)
            {
                var handle = new WindowInteropHelper(Owner).Handle;
                privacyApiOk = privacyApiOk && WindowStealth.TrySetCaptureExclusion(handle, true);
            }
            bool privacyOk = _stealthEnabled && privacyApiOk;
            SetState(PrivacyState, PrivacyDetail, privacyOk ? "READY" : "CHECK",
                privacyOk
                    ? "Capture protection was applied. Verify it once in the meeting app you will use."
                    : _stealthEnabled
                        ? "Windows could not confirm capture protection. Update Windows and verify in your meeting app."
                        : "Stealth mode is off. Turn it on in Settings if you want capture-protection controls.",
                privacyOk ? "#4ADE80" : "#FBBF24");

            ChecksPassed = networkOk && audioOk;
            ContinueButton.IsEnabled = ChecksPassed;
            RunButton.IsEnabled = true;
            RunButton.Content = ChecksPassed ? "Run again" : "Retry checks";
        }

        private static void SetState(System.Windows.Controls.TextBlock state, System.Windows.Controls.TextBlock detail, string label, string message, string color)
        {
            state.Text = label;
            state.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            detail.Text = message;
        }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            if (!ChecksPassed) return;
            DialogResult = true;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
