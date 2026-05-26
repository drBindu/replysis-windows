using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace InterviewCopilot
{
    public partial class LoginWindow : Window
    {
        private const string FirebaseProjectId = "copilotx-ai";
        private static string FirebaseApiKey => SettingsWindow.GetFirebaseApiKey();

        private static HttpClient _http => SharedHttpClient.HttpShort;

        // Result — set when login succeeds
        public bool LoginSuccess { get; private set; } = false;
        public string IdToken    { get; private set; } = "";
        public string UserEmail  { get; private set; } = "";
        public string UserName   { get; private set; } = "";
        public string UserId     { get; private set; } = "";

        // (reserved for future use)
        // private DispatcherTimer? _dotTimer;

        public LoginWindow()
        {
            InitializeComponent();
            EmailBox.Focus();

            // Pre-fill email if saved
            string saved = SettingsWindow.GetCoopilotEmail();
            if (!string.IsNullOrEmpty(saved))
                EmailBox.Text = saved;
        }

        // ══════════════════════════════════════════════════════════
        // SIGN IN
        // ══════════════════════════════════════════════════════════
        private async void SignInBtn_Click(object sender, RoutedEventArgs e)
        {
            await DoSignIn();
        }

        private async void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return) await DoSignIn();
        }

        private async Task DoSignIn()
        {
            string email    = EmailBox.Text.Trim();
            string password = PasswordBox.Password;

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                ShowError("Please enter your email and password.");
                return;
            }

            SetLoading(true);
            HideError();

            try
            {
                // ── Call Firebase Auth REST API ──
                string url = $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={FirebaseApiKey}";

                var payload = new
                {
                    email,
                    password,
                    returnSecureToken = true
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var res  = await _http.SendAsync(request);
                string body = await res.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(body);

                if (!res.IsSuccessStatusCode)
                {
                    // Parse Firebase error
                    string errMsg = "Login failed. Check your email and password.";
                    if (doc.RootElement.TryGetProperty("error", out var err))
                    {
                        string code = err.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                        errMsg = code switch
                        {
                            "EMAIL_NOT_FOUND"      => "No account found with this email.",
                            "INVALID_PASSWORD"     => "Incorrect password. Please try again.",
                            "INVALID_EMAIL"        => "Invalid email address.",
                            "USER_DISABLED"        => "This account has been disabled.",
                            "TOO_MANY_ATTEMPTS_TRY_LATER" => "Too many attempts. Try again later.",
                            "INVALID_LOGIN_CREDENTIALS" => "Incorrect email or password.",
                            _ => $"Login failed: {code}"
                        };
                    }
                    ShowError(errMsg);
                    SetLoading(false);
                    return;
                }

                // ── Success — extract token + user info ──
                IdToken   = doc.RootElement.TryGetProperty("idToken",      out var t)  ? t.GetString()  ?? "" : "";
                string refreshToken = doc.RootElement.TryGetProperty("refreshToken", out var rt) ? rt.GetString() ?? "" : "";
                UserEmail = doc.RootElement.TryGetProperty("email",        out var em) ? em.GetString() ?? "" : "";
                UserId    = doc.RootElement.TryGetProperty("localId",      out var id) ? id.GetString() ?? "" : "";
                UserName  = doc.RootElement.TryGetProperty("displayName",  out var dn) ? dn.GetString() ?? email : email;

                if (string.IsNullOrEmpty(UserName) || UserName == UserEmail)
                    UserName = email.Contains('@') ? email.Split('@')[0] : email; // guard: no crash on malformed email

                // Save email for next time
                var cfg = SettingsWindow.LoadConfig();
                cfg.CoopilotEmail = UserEmail;
                SettingsWindow.SaveConfig(cfg);

                // Save token to session
                UserSession.SetSession(IdToken, UserEmail, UserName, UserId, refreshToken);

                ShowSuccess($"Welcome back, {UserName}!");
                await Task.Delay(800);

                LoginSuccess = true;
                this.Close();
            }
            catch (Exception)
            {
                ShowError("Connection error. Check your internet connection.");
                SetLoading(false);
            }
        }

        // ══════════════════════════════════════════════════════════
        // FORGOT PASSWORD
        // ══════════════════════════════════════════════════════════
        private async void ForgotLink_Click(object sender, RoutedEventArgs e)
        {
            string email = EmailBox.Text.Trim();
            if (string.IsNullOrEmpty(email))
            {
                ShowError("Enter your email first, then click Forgot Password.");
                return;
            }

            SetLoading(true);
            HideError();

            try
            {
                string url = $"https://identitytoolkit.googleapis.com/v1/accounts:sendOobCode?key={FirebaseApiKey}";
                var payload = new { requestType = "PASSWORD_RESET", email };

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var res = await _http.SendAsync(request);

                if (res.IsSuccessStatusCode)
                    ShowSuccess($"Password reset email sent to {email}");
                else
                    ShowError("Could not send reset email. Check your email address.");
            }
            catch
            {
                ShowError("Connection error. Try again.");
            }
            finally
            {
                SetLoading(false);
            }
        }

        // ══════════════════════════════════════════════════════════
        // REGISTER LINK
        // ══════════════════════════════════════════════════════════
        private void RegisterLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://coopilotxai.com/signup") { UseShellExecute = true });
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════
        // UI HELPERS
        // ══════════════════════════════════════════════════════════
        private void SetLoading(bool loading)
        {
            SignInBtn.IsEnabled = !loading;
            EmailBox.IsEnabled  = !loading;
            PasswordBox.IsEnabled = !loading;

            var tmpl = SignInBtn.Template;
            if (tmpl == null) return;
            SignInBtn.ApplyTemplate();

            var btnText     = (System.Windows.Controls.TextBlock?)SignInBtn.Template.FindName("BtnText",     SignInBtn);
            var loadPanel   = (System.Windows.Controls.StackPanel?)SignInBtn.Template.FindName("LoadingPanel", SignInBtn);

            if (btnText != null)   btnText.Visibility   = loading ? Visibility.Collapsed : Visibility.Visible;
            if (loadPanel != null) loadPanel.Visibility = loading ? Visibility.Visible   : Visibility.Collapsed;
        }

        private void ShowError(string msg)
        {
            ErrorText.Text        = msg;
            ErrorBanner.Visibility  = Visibility.Visible;
            SuccessBanner.Visibility = Visibility.Collapsed;
        }

        private void ShowSuccess(string msg)
        {
            SuccessText.Text       = msg;
            SuccessBanner.Visibility = Visibility.Visible;
            ErrorBanner.Visibility  = Visibility.Collapsed;
        }

        private void HideError()
        {
            ErrorBanner.Visibility  = Visibility.Collapsed;
            SuccessBanner.Visibility = Visibility.Collapsed;
        }

        // Input focus highlight
        private void EmailBox_GotFocus(object sender, RoutedEventArgs e) =>
            EmailBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));
        private void EmailBox_LostFocus(object sender, RoutedEventArgs e) =>
            EmailBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#30363d"));
        private void PasswordBox_GotFocus(object sender, RoutedEventArgs e) =>
            PasswordBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));
        private void PasswordBox_LostFocus(object sender, RoutedEventArgs e) =>
            PasswordBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#30363d"));

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
    }
}
