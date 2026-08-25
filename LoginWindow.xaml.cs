using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
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
            // Same glass as the main window, from the same stored setting.
            // Every window painted its own solid near-black before this, so
            // opening one dropped an opaque slab on top of a translucent app.
            Glass.Apply(this, RootGlass);
            try { WindowStealth.SetStealthMode(this, SettingsWindow.GetStealthMode()); } catch { }
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
            string password = CurrentPassword;

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

                using var res = await _http.SendAsync(request);
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
                if (!IsLoaded) return;

                LoginSuccess = true;
                this.Close();
            }
            catch (Exception ex)
            {
                DebugWindow.Log("LOGIN", $"Sign-in error: {ex.Message}");
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

                using var res = await _http.SendAsync(request);

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
                    "https://replysis.com/signup") { UseShellExecute = true });
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════
        // GOOGLE SIGN-IN  (RFC 8252 PKCE loopback redirect)
        // ══════════════════════════════════════════════════════════
        private async void GoogleSignIn_Click(object sender, RoutedEventArgs e)
        {
            SetLoading(true);
            HideError();
            try { await DoGoogleSignIn(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GOOGLE] unhandled: {ex.Message}");
                DebugWindow.Log("GOOGLE", $"S0 unhandled {ex.GetType().Name}: {ex.Message}");
                ShowError("Google sign-in failed. Please try again or contact support. (E0)");
            }
            finally { if (!LoginSuccess) SetLoading(false); }
        }

        private async Task DoGoogleSignIn()
        {
            const string ClientId = "745433477203-lvqmnnip9pb241vkfp628qmue8313cre.apps.googleusercontent.com";
            const string Scope    = "openid email profile";

            // 1. PKCE
            string verifier  = PkceVerifier();
            string challenge = PkceChallenge(verifier);
            string state     = Guid.NewGuid().ToString("N");

            DebugWindow.Log("GOOGLE", $"S1 start packaged={IsPackaged()} verifierLen={verifier.Length} challengeLen={challenge.Length}");

            // 2. Loopback listener on a free OS-assigned port
            var tcpListener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            try
            {
                tcpListener.Start();
            }
            catch (Exception ex)
            {
                // A packaged build runs under different network rules than the
                // Visual Studio build, so record why the listener could not bind.
                DebugWindow.Log("GOOGLE", $"S2 listener bind FAILED {ex.GetType().Name}: {ex.Message}");
                ShowError("Google sign-in failed. Please try again or contact support. (E2)");
                return;
            }
            int    port        = ((System.Net.IPEndPoint)tcpListener.LocalEndpoint).Port;
            string redirectUri = $"http://127.0.0.1:{port}/";
            DebugWindow.Log("GOOGLE", $"S2 listener bound port={port} redirectUri={redirectUri}");

            // 3. Open browser to Google OAuth
            string authUrl =
                "https://accounts.google.com/o/oauth2/v2/auth" +
                $"?client_id={Uri.EscapeDataString(ClientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                "&response_type=code" +
                $"&scope={Uri.EscapeDataString(Scope)}" +
                $"&code_challenge={challenge}" +
                "&code_challenge_method=S256" +
                $"&state={state}" +
                "&access_type=offline" +
                "&prompt=select_account";

            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(authUrl) { UseShellExecute = true });
                DebugWindow.Log("GOOGLE", "S3 browser launched");
            }
            catch (Exception ex)
            {
                DebugWindow.Log("GOOGLE", $"S3 browser launch FAILED {ex.GetType().Name}: {ex.Message}");
                tcpListener.Stop();
                ShowError("Google sign-in failed. Please try again or contact support. (E3)");
                return;
            }

            // 4. Accept redirect (120s timeout)
            string? code = null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                using var client = await tcpListener.AcceptTcpClientAsync(cts.Token);
                DebugWindow.Log("GOOGLE", "S4 callback connection accepted");
                using var stream = client.GetStream();

                // Read until we have the full HTTP header block (handles Chrome's long headers)
                var sbReq = new StringBuilder();
                byte[] buf = new byte[4096];
                do
                {
                    int read = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
                    if (read == 0) break;
                    sbReq.Append(Encoding.UTF8.GetString(buf, 0, read));
                } while (!sbReq.ToString().Contains("\r\n\r\n") && !sbReq.ToString().Contains("\n\n"));
                string req = sbReq.ToString();

                // Parse first HTTP line: "GET /?code=...&state=... HTTP/1.1"
                string firstLine = req.Split('\n')[0];
                string qs = firstLine.Contains('?')
                    ? firstLine.Split('?')[1].Split(' ')[0]
                    : "";

                var qd = ParseQs(qs);
                qd.TryGetValue("code",  out code);
                qd.TryGetValue("state", out string? returnedState);

                // Never log the authorization code or the raw query, only shape.
                bool stateMatched = returnedState == state;
                bool hasCode      = !string.IsNullOrEmpty(code);
                qd.TryGetValue("error", out string? oauthError);
                DebugWindow.Log("GOOGLE",
                    $"S5 callback parsed stateMatch={stateMatched} hasCode={hasCode} " +
                    $"codeLen={(code?.Length ?? 0)} googleError={(string.IsNullOrEmpty(oauthError) ? "none" : oauthError)}");

                bool ok = stateMatched && hasCode;
                string html = ok
                    ? "<html><body style='background:#0d1117;color:#4ade80;font-family:sans-serif;text-align:center;padding:60px'><h2>&#10003; Signed in! You can close this tab.</h2></body></html>"
                    : "<html><body style='background:#0d1117;color:#ef4444;font-family:sans-serif;text-align:center;padding:60px'><h2>Sign-in failed. Please close this tab and try again.</h2></body></html>";
                // Content-Length counts bytes, not characters. Both bodies above
                // are ASCII today — the tick is written as an entity — so the two
                // agree by luck rather than by construction, and the first
                // non-ASCII character anyone adds would understate the length and
                // truncate the page in the browser. Measuring the encoded bytes
                // costs nothing and cannot drift.
                byte[] bodyBytes = Encoding.UTF8.GetBytes(html);
                byte[] headerBytes = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n"
                    + $"Content-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
                await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length);

                if (!ok)
                {
                    DebugWindow.Log("GOOGLE", "S5 rejected: state mismatch or missing code");
                    ShowError("Sign-in was not completed. Please try again. (E5)");
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // No callback ever arrived. In a packaged build this is the
                // signature of the loopback redirect not reaching the app.
                DebugWindow.Log("GOOGLE", "S4 TIMEOUT: no loopback callback within 120s");
                ShowError("Sign-in timed out. Please try again. (E4)");
                return;
            }
            catch (Exception ex)
            {
                DebugWindow.Log("GOOGLE", $"S4 callback read FAILED {ex.GetType().Name}: {ex.Message}");
                ShowError("Google sign-in failed. Please try again or contact support. (E4b)");
                return;
            }
            finally
            {
                tcpListener.Stop();
            }

            // 5. Exchange code via backend (backend holds Google client secret).
            // Uses the long-timeout client — exchange calls Google token endpoint + Firebase
            // from the backend server and can take longer than HttpShort's 15 s limit.
            var    exchPayload = new { code, codeVerifier = verifier, redirectUri };
            string backendBase = SettingsWindow.GetBackendUrl();
            string exchangeUrl = $"{backendBase}/api/v1/auth/google/exchange";
            DebugWindow.Log("GOOGLE", $"S6 exchange POST {exchangeUrl}");

            string exchBody;
            System.Net.HttpStatusCode exchStatus;
            try
            {
                using var exchReq = new HttpRequestMessage(HttpMethod.Post, exchangeUrl);
                exchReq.Content = new StringContent(
                    JsonSerializer.Serialize(exchPayload), Encoding.UTF8, "application/json");

                using var exchRes = await SharedHttpClient.Http.SendAsync(exchReq);
                exchStatus = exchRes.StatusCode;
                exchBody   = await exchRes.Content.ReadAsStringAsync();
                DebugWindow.Log("GOOGLE", $"S6 exchange HTTP {(int)exchStatus} bodyLen={exchBody.Length}");

                if (!exchRes.IsSuccessStatusCode)
                {
                    DebugWindow.Log("GOOGLE", $"S6 exchange failed HTTP {(int)exchStatus}");
                    ShowError($"Google sign-in failed. Please try again or contact support. (E6-{(int)exchStatus})");
                    return;
                }
            }
            catch (Exception ex)
            {
                // Network-layer failure: unreachable host, TLS, DNS or timeout.
                DebugWindow.Log("GOOGLE", $"S6 exchange TRANSPORT FAILED {ex.GetType().Name}: {ex.Message}");
                ShowError("Google sign-in failed. Please try again or contact support. (E6-net)");
                return;
            }

            using var exchDoc = JsonDocument.Parse(exchBody);

            // Backend may return Firebase idToken directly or a Google access_token
            string firebaseIdToken = GetStr(exchDoc, "idToken");
            if (string.IsNullOrEmpty(firebaseIdToken))
                firebaseIdToken = GetStr(exchDoc, "firebase_id_token");

            DebugWindow.Log("GOOGLE",
                $"S7 parsed hasIdToken={!string.IsNullOrEmpty(firebaseIdToken)} " +
                $"hasAccessToken={!string.IsNullOrEmpty(GetStr(exchDoc, "access_token"))}");

            if (!string.IsNullOrEmpty(firebaseIdToken))
            {
                string refreshTok  = GetStr(exchDoc, "refreshToken");
                string email       = GetStr(exchDoc, "email");
                string displayName = GetStr(exchDoc, "displayName");
                string uid         = GetStr(exchDoc, "localId");
                string photoUrl    = GetStr(exchDoc, "photoUrl");
                if (string.IsNullOrEmpty(displayName)) displayName = email.Split('@')[0];

                await CompleteSignIn(firebaseIdToken, refreshTok, email, displayName, uid, photoUrl);
                return;
            }

            // Fallback: backend returned Google access_token → call Firebase signInWithIdp
            string accessToken = GetStr(exchDoc, "access_token");
            if (string.IsNullOrEmpty(accessToken))
            {
                DebugWindow.Log("GOOGLE", "S7 response had neither idToken nor access_token");
                ShowError("Google sign-in failed. Please try again or contact support. (E7)");
                return;
            }

            await SignInWithGoogleAccessToken(accessToken);
        }

        private async Task SignInWithGoogleAccessToken(string accessToken)
        {
            string url = $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithIdp?key={FirebaseApiKey}";
            var payload = new
            {
                postBody            = $"access_token={Uri.EscapeDataString(accessToken)}&providerId=google.com",
                requestUri          = "http://localhost",
                returnIdpCredential = true,
                returnSecureToken   = true
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var res  = await _http.SendAsync(req);
            string    body = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                DebugWindow.Log("GOOGLE", $"S8 signInWithIdp HTTP {(int)res.StatusCode}");
                ShowError($"Google sign-in failed. Please try again or contact support. (E8-{(int)res.StatusCode})");
                return;
            }

            using var doc = JsonDocument.Parse(body);
            string idToken      = GetStr(doc, "idToken");
            string refreshToken = GetStr(doc, "refreshToken");
            string email        = GetStr(doc, "email");
            string name         = GetStr(doc, "displayName");
            string uid          = GetStr(doc, "localId");
            string photoUrl     = GetStr(doc, "photoUrl");
            if (string.IsNullOrEmpty(name)) name = email.Split('@')[0];

            if (string.IsNullOrEmpty(idToken))
            {
                DebugWindow.Log("GOOGLE", "S8 signInWithIdp returned no idToken");
                ShowError("Google sign-in failed. Please try again or contact support. (E8)");
                return;
            }

            await CompleteSignIn(idToken, refreshToken, email, name, uid, photoUrl);
        }

        private async Task CompleteSignIn(string idToken, string refreshToken, string email, string name, string uid, string photoUrl = "")
        {
            UserSession.SetSession(idToken, email, name, uid, refreshToken, photoUrl);
            DebugWindow.Log("GOOGLE",
                $"S9 SetSession done isLoggedIn={UserSession.IsLoggedIn} hasUid={!string.IsNullOrEmpty(uid)}");

            IdToken   = idToken;
            UserEmail = email;
            UserName  = name;
            UserId    = uid;

            try
            {
                var cfg = SettingsWindow.LoadConfig();
                cfg.CoopilotEmail = email;
                SettingsWindow.SaveConfig(cfg);
            }
            catch (Exception ex)
            {
                // Non-fatal, but under MSIX a failed write here points at storage
                // redirection, so it must not stay silent.
                DebugWindow.Log("GOOGLE", $"S9 config save failed {ex.GetType().Name}: {ex.Message}");
            }

            ShowSuccess($"Welcome, {name}!");
            await Task.Delay(800);
            if (!IsLoaded) return;
            LoginSuccess = true;
            DebugWindow.Log("GOOGLE", "S10 success, closing login window");
            Close();
        }

        // ── PKCE helpers ──────────────────────────────────────────
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, System.Text.StringBuilder? packageFullName);

        private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

        /// <summary>
        /// True when running from the installed MSIX package. The packaged and
        /// unpackaged builds differ in storage redirection and network rules, so
        /// the sign-in log records which one produced it.
        /// </summary>
        private static bool IsPackaged()
        {
            try
            {
                int length = 0;
                return GetCurrentPackageFullName(ref length, null) != APPMODEL_ERROR_NO_PACKAGE;
            }
            catch { return false; }
        }

        private static string PkceVerifier()
        {
            byte[] bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string PkceChallenge(string verifier)
        {
            byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
            return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static Dictionary<string, string> ParseQs(string qs)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in qs.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                string k = Uri.UnescapeDataString(pair[..eq]);
                string v = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                dict[k] = v;
            }
            return dict;
        }

        private static string GetStr(JsonDocument doc, string key) =>
            doc.RootElement.TryGetProperty(key, out var p) ? p.GetString() ?? "" : "";

        // ══════════════════════════════════════════════════════════
        // UI HELPERS
        // ══════════════════════════════════════════════════════════
        private void SetLoading(bool loading)
        {
            SignInBtn.IsEnabled       = !loading;
            GoogleSignInBtn.IsEnabled = !loading;
            EmailBox.IsEnabled        = !loading;
            PasswordBox.IsEnabled     = !loading;
            // The revealed field is a separate control, so it needs locking too,
            // otherwise the password stays editable while a request is in flight.
            PasswordPlainBox.IsEnabled  = !loading;
            RevealPasswordBtn.IsEnabled = !loading;

            var tmpl = SignInBtn.Template;
            if (tmpl == null) return;
            SignInBtn.ApplyTemplate();

            var btnText   = (System.Windows.Controls.TextBlock?)SignInBtn.Template.FindName("BtnText",     SignInBtn);
            var loadPanel = (System.Windows.Controls.StackPanel?)SignInBtn.Template.FindName("LoadingPanel", SignInBtn);

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
            EmailBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34E08A"));
        private void EmailBox_LostFocus(object sender, RoutedEventArgs e) =>
            EmailBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#26364C"));
        private void PasswordBox_GotFocus(object sender, RoutedEventArgs e) =>
            PasswordBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34E08A"));
        private void PasswordBox_LostFocus(object sender, RoutedEventArgs e) =>
            PasswordBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#26364C"));

        // ══════════════════════════════════════════════════════════════════════
        // PASSWORD REVEAL
        // PasswordBox cannot display its own characters, so revealing swaps in a
        // plain TextBox. Whichever control is visible holds the live value, and
        // CurrentPassword is the single place that decides which one that is, so
        // sign-in can never read a stale copy.
        // ══════════════════════════════════════════════════════════════════════

        private bool _passwordRevealed;

        private string CurrentPassword =>
            _passwordRevealed ? PasswordPlainBox.Text : PasswordBox.Password;

        private void RevealPasswordBtn_Click(object sender, RoutedEventArgs e)
        {
            _passwordRevealed = !_passwordRevealed;

            if (_passwordRevealed)
            {
                PasswordPlainBox.Text = PasswordBox.Password;
                PasswordBox.Visibility = Visibility.Collapsed;
                PasswordPlainBox.Visibility = Visibility.Visible;
                PasswordPlainBox.CaretIndex = PasswordPlainBox.Text.Length;
                PasswordPlainBox.Focus();
                RevealPasswordBtn.ToolTip = "Hide password";
            }
            else
            {
                PasswordBox.Password = PasswordPlainBox.Text;
                PasswordPlainBox.Visibility = Visibility.Collapsed;
                PasswordBox.Visibility = Visibility.Visible;
                PasswordBox.Focus();
                RevealPasswordBtn.ToolTip = "Show password";
            }

            // Same eye glyph throughout, tinted when active. Swapping to a second
            // glyph risks rendering an empty box if that codepoint is missing.
            RevealPasswordIcon.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(_passwordRevealed ? "#34E08A" : "#6F8198"));
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
    }
}
