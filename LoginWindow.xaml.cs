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
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace InterviewCopilot
{
    public partial class LoginWindow : Window
    {
        private const string FirebaseProjectId = "copilotx-ai";
        private static string FirebaseApiKey => SettingsWindow.GetFirebaseApiKey();

        private static HttpClient _http => SharedHttpClient.HttpShort;
        private readonly CancellationTokenSource _windowLifetime = new();
        private readonly CancellationToken _windowToken;
        private bool _authBusy;
        private bool _isRegistrationMode;

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
            _windowToken = _windowLifetime.Token;
            InitializeComponent();
            Closed += (_, _) =>
            {
                _windowLifetime.Cancel();
                _windowLifetime.Dispose();
                PasswordBox.Clear();
                PasswordPlainBox.Clear();
            };
            // Authentication is intentionally opaque and fixed-size. Applying
            // the workspace glass/opacity setting here made the first screen
            // look unfinished and exposed whatever was behind the app.
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
            if (_authBusy || _windowToken.IsCancellationRequested) return;
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
                // Firebase keeps registration and password sign-in as separate
                // endpoints. Keeping both inside this window means a first-time
                // desktop user does not have to visit the website just to create
                // their account.
                string action = _isRegistrationMode ? "signUp" : "signInWithPassword";
                string url = $"https://identitytoolkit.googleapis.com/v1/accounts:{action}?key={FirebaseApiKey}";

                var payload = new
                {
                    email,
                    password,
                    returnSecureToken = true
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var res = await _http.SendAsync(request, _windowToken);
                string body = await res.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(body);

                if (!res.IsSuccessStatusCode)
                {
                    // Parse Firebase error
                    string errMsg = _isRegistrationMode
                        ? "Could not create your account. Please try again."
                        : "Login failed. Check your email and password.";
                    if (doc.RootElement.TryGetProperty("error", out var err))
                    {
                        string code = err.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                        errMsg = code switch
                        {
                            "EMAIL_NOT_FOUND"      => "No account found with this email.",
                            "EMAIL_EXISTS"          => "An account already exists with this email. Sign in instead.",
                            "WEAK_PASSWORD"         => "Use a password with at least 6 characters.",
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

                await CompleteSignIn(IdToken, refreshToken, UserEmail, UserName, UserId);
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
            if (_authBusy || _windowToken.IsCancellationRequested) return;
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

                using var res = await _http.SendAsync(request, _windowToken);

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
            if (_authBusy) return;
            SetRegistrationMode(!_isRegistrationMode);
        }

        // Sign in and Create account are one flow's two steps, so switching
        // between them slides the header out, swaps every label at the
        // midpoint where nothing is visible to see it happen, then slides
        // the new step in from the opposite side - a real step transition
        // rather than text changing mid-frame.
        private void SetRegistrationMode(bool registrationMode)
        {
            _isRegistrationMode = registrationMode;
            bool forward = registrationMode;
            const double travel = 16;
            var outEase = new CubicEase { EasingMode = EasingMode.EaseIn };
            var inEase  = new CubicEase { EasingMode = EasingMode.EaseOut };

            var outStoryboard = new Storyboard();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120)) { EasingFunction = outEase };
            Storyboard.SetTarget(fadeOut, AuthHeader);
            Storyboard.SetTargetProperty(fadeOut, new PropertyPath(UIElement.OpacityProperty));
            var slideOut = new DoubleAnimation(0, forward ? -travel : travel, TimeSpan.FromMilliseconds(120)) { EasingFunction = outEase };
            Storyboard.SetTarget(slideOut, AuthHeaderShift);
            Storyboard.SetTargetProperty(slideOut, new PropertyPath(TranslateTransform.XProperty));
            outStoryboard.Children.Add(fadeOut);
            outStoryboard.Children.Add(slideOut);

            outStoryboard.Completed += (_, _) =>
            {
                Title = registrationMode ? "Create your Replysis AI account" : "Replysis AI Sign In";
                AuthTitle.Text = registrationMode ? "Create your account" : "Welcome back";
                LoginSubtitle.Text = registrationMode
                    ? "Set up your secure Replysis workspace"
                    : "Sign in to continue to your workspace";
                SetPrimaryButtonCaption(registrationMode ? "Create account" : "Sign In");
                RegisterPromptText.Text = registrationMode ? "Already have an account? " : "New here? ";
                RegisterLink.Inlines.Clear();
                RegisterLink.Inlines.Add(registrationMode ? "Sign in" : "Create an account");

                AuthHeaderShift.X = forward ? travel : -travel;
                var inStoryboard = new Storyboard();
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = inEase };
                Storyboard.SetTarget(fadeIn, AuthHeader);
                Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));
                var slideIn = new DoubleAnimation(AuthHeaderShift.X, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = inEase };
                Storyboard.SetTarget(slideIn, AuthHeaderShift);
                Storyboard.SetTargetProperty(slideIn, new PropertyPath(TranslateTransform.XProperty));
                inStoryboard.Children.Add(fadeIn);
                inStoryboard.Children.Add(slideIn);
                inStoryboard.Begin();
            };
            outStoryboard.Begin();

            // Forgot-password only makes sense while signing in, so it fades
            // out on the way to Create account and fades back in on the way
            // back, instead of popping in and out of layout.
            ForgotPasswordRow.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(registrationMode ? 0 : 1, TimeSpan.FromMilliseconds(160)));
            if (!registrationMode) ForgotPasswordRow.Visibility = Visibility.Visible;
            var forgotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
            forgotTimer.Tick += (_, _) =>
            {
                forgotTimer.Stop();
                if (registrationMode) ForgotPasswordRow.Visibility = Visibility.Collapsed;
            };
            forgotTimer.Start();

            HideError();
            PasswordBox.Focus();
        }

        private void SetPrimaryButtonCaption(string caption)
        {
            // BtnText lives inside SignInBtn's ControlTemplate, so WPF creates it
            // at template scope rather than as a window field.
            SignInBtn.ApplyTemplate();
            if (SignInBtn.Template.FindName("BtnText", SignInBtn) is System.Windows.Controls.TextBlock text)
                text.Text = caption;
        }

        // ══════════════════════════════════════════════════════════
        // GOOGLE SIGN-IN  (RFC 8252 PKCE loopback redirect)
        // ══════════════════════════════════════════════════════════
        private async void GoogleSignIn_Click(object sender, RoutedEventArgs e)
        {
            if (_authBusy || _windowToken.IsCancellationRequested) return;
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

            // 4. Wait for this attempt's answer (120s). Anything else that connects, such as a browser's spare
            // connection or its request for an icon, is ignored; see OAuthCallbackReader.WaitForAnswerAsync.
            string? code = null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_windowToken);
            cts.CancelAfter(TimeSpan.FromSeconds(120));
            try
            {
                var answer = await OAuthCallbackReader.WaitForAnswerAsync(
                    tcpListener, state, cts.Token, m => DebugWindow.Log("GOOGLE", "S4 " + m));
                DebugWindow.Log("GOOGLE", $"S5 callback accepted={answer.Result == OAuthCallbackReader.Answer.Code}; callback values omitted.");

                if (answer.Result != OAuthCallbackReader.Answer.Code)
                {
                    DebugWindow.Log("GOOGLE", "S5 declined: Google sent this attempt back without a code");
                    ShowError("Sign-in was not completed. Please try again. (E5)");
                    return;
                }
                code = answer.Code;

                // Do not leave the user looking at localhost. The browser may
                // keep the tab because of its own close-tab policy, but Replysis
                // becomes the active foreground window immediately in either case.
                BringAuthenticationWindowForward();
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

                using var exchRes = await SharedHttpClient.Http.SendAsync(exchReq, _windowToken);
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

            using var res  = await _http.SendAsync(req, _windowToken);
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
            _windowToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(email))
                throw new InvalidOperationException("The sign-in response is missing account information.");
            UserSession.SetSession(idToken, email, name, uid, refreshToken, photoUrl);
            // Warm speech credentials during the success transition, while the
            // welcome state is already on screen. By the time MainWindow exists,
            // the engine can start without a cold network round-trip.
            _ = UserSession.EnsureSpeechmaticsKeyAsync(DeviceIdentity.Current);
            // Firebase Authentication alone does not create the users/{uid}
            // document listed by the admin portal. Use the website's verified,
            // server-side initializer so desktop-only users appear there too.
            await UserProfileSync.EnsureCurrentUserAsync();
            // A manual close during the welcome animation must still report a
            // completed login to the owner, since the session is already saved.
            LoginSuccess = true;
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
            await Task.Delay(800, _windowToken);
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

        private static string GetStr(JsonDocument doc, string key) =>
            doc.RootElement.TryGetProperty(key, out var p) ? p.GetString() ?? "" : "";

        // ══════════════════════════════════════════════════════════
        // UI HELPERS
        // ══════════════════════════════════════════════════════════
        private void SetLoading(bool loading)
        {
            _authBusy = loading;
            if (_windowToken.IsCancellationRequested) return;
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

        // A banner drops in and settles rather than appearing mid-frame — the
        // same small settle the auth form itself does on window open, so an
        // error or success message reads as the page responding to you
        // rather than a value flipping.
        private static void AnimateBannerIn(System.Windows.Controls.Border banner, TranslateTransform shift)
        {
            banner.Visibility = Visibility.Visible;
            banner.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
            shift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        private static void HideBanner(System.Windows.Controls.Border banner)
        {
            banner.BeginAnimation(UIElement.OpacityProperty, null);
            banner.Opacity = 0;
            banner.Visibility = Visibility.Collapsed;
        }

        private void ShowError(string msg)
        {
            if (_windowToken.IsCancellationRequested) return;
            ErrorText.Text = msg;
            HideBanner(SuccessBanner);
            AnimateBannerIn(ErrorBanner, ErrorBannerShift);
        }

        private void ShowSuccess(string msg)
        {
            if (_windowToken.IsCancellationRequested) return;
            SuccessText.Text = msg;
            HideBanner(ErrorBanner);
            AnimateBannerIn(SuccessBanner, SuccessBannerShift);
        }

        private void HideError()
        {
            HideBanner(ErrorBanner);
            HideBanner(SuccessBanner);
        }

        // Input focus highlight — animated rather than an instant Setter, so
        // the field visibly settles into focus: the border eases to green and
        // a soft matching glow fades in behind it, then both reverse on blur.
        private static readonly Color FieldRestColor  = (Color)ColorConverter.ConvertFromString("#DCE4D8");
        private static readonly Color FieldFocusColor = (Color)ColorConverter.ConvertFromString("#21924A");

        private static void AnimateFieldFocus(System.Windows.Controls.Border border, DropShadowEffect glow, bool focused)
        {
            var duration = TimeSpan.FromMilliseconds(160);
            border.BorderBrush = new SolidColorBrush(border.BorderBrush is SolidColorBrush b ? b.Color : FieldRestColor);
            ((SolidColorBrush)border.BorderBrush).BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(focused ? FieldFocusColor : FieldRestColor, duration));
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(focused ? 0.22 : 0, duration));
            glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(focused ? 16 : 0, duration));
        }

        private void EmailBox_GotFocus(object sender, RoutedEventArgs e) => AnimateFieldFocus(EmailBorder, EmailGlow, true);
        private void EmailBox_LostFocus(object sender, RoutedEventArgs e) => AnimateFieldFocus(EmailBorder, EmailGlow, false);
        private void PasswordBox_GotFocus(object sender, RoutedEventArgs e) => AnimateFieldFocus(PasswordBorder, PasswordGlow, true);
        private void PasswordBox_LostFocus(object sender, RoutedEventArgs e) => AnimateFieldFocus(PasswordBorder, PasswordGlow, false);

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
            var eyeColor = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(_passwordRevealed ? "#21924A" : "#8A9086"));
            RevealPasswordIcon.Stroke = eyeColor;
            RevealPasswordPupil.Stroke = eyeColor;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        private void BringAuthenticationWindowForward()
        {
            try
            {
                // Chrome/Edge correctly refuse script-closing a tab launched by
                // a native process. Minimize the exact foreground browser window
                // that completed OAuth instead. Never terminate the browser or
                // touch its tabs, because the same window can contain user work.
                IntPtr browserWindow = GetForegroundWindow();
                if (browserWindow != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(browserWindow, out uint browserPid);
                    try
                    {
                        string processName = System.Diagnostics.Process
                            .GetProcessById(unchecked((int)browserPid)).ProcessName.ToLowerInvariant();
                        if (processName is "chrome" or "msedge" or "firefox" or "brave" or "opera")
                            ShowWindowAsync(browserWindow, 6); // SW_MINIMIZE
                    }
                    catch (Exception ex)
                    {
                        DebugWindow.Log("GOOGLE", $"Could not identify OAuth browser: {ex.Message}");
                    }
                }

                Dispatcher.BeginInvoke(() =>
                {
                    if (!IsLoaded) return;
                    WindowState = WindowState.Normal;
                    Show();
                    Activate();
                    Focus();
                    IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        ShowWindowAsync(hwnd, 9); // SW_RESTORE
                        SetForegroundWindow(hwnd);
                    }
                }, DispatcherPriority.Send);
            }
            catch (Exception ex)
            {
                DebugWindow.Log("GOOGLE", $"Could not restore app focus: {ex.Message}");
            }
        }
    }
}
