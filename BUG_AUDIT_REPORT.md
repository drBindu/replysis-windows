# Interview Copilot — Deep Bug Audit Report
**Audited:** April 24, 2026  
**Files reviewed:** All 12 source files (C# + Python)  
**Total bugs found:** 20  

---

## 🔴 CRITICAL — App-Breaking or Security

---

### BUG #1 — Hard-coded developer path breaks app on all other machines
**File:** `MainWindow.xaml.cs` — `FindScriptFolder()`, line 305  
**Code:**
```csharp
string known = @"C:\Users\krish\Desktop\windowsNative";
if (File.Exists(Path.Combine(known, "speechmatics_engine.py"))) return known;
```
**Problem:** This path is checked FIRST, before the real app directory. On your machine it works. On anyone else's machine this path doesn't exist, the fallback logic runs, but ultimately the fallback default is ALSO set to `C:\Users\krish\Desktop\windowsNative`. The Python speech engine will never start for any other user.  
**Fix:**
```csharp
private static string FindScriptFolder(string startDir)
{
    if (File.Exists(Path.Combine(startDir, "speechmatics_engine.py"))) return startDir;
    string? dir = startDir;
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir, "speechmatics_engine.py"))) return dir;
        dir = Directory.GetParent(dir)?.FullName;
    }
    return startDir; // fallback to app directory, not a personal path
}
```

---

### BUG #2 — `NuclearKillOldProcesses()` kills ALL Python processes on the system
**File:** `MainWindow.xaml.cs` — lines 686, 746  
**Code:**
```csharp
private void NuclearKillOldProcesses() {
    foreach (var p in Process.GetProcessesByName("python")) try { p.Kill(); } catch { }
}
```
**Problem:** This kills every Python process running on the whole machine — Jupyter notebooks, Django servers, data science scripts, any background Python tool. Same call happens on `OnClosed()` at line 746. This is a critical data-loss bug for any user with Python tooling running.  
**Fix:** Track the PID of the process you spawned and kill only that:
```csharp
private void KillOwnPythonProcess()
{
    if (speechmaticsProcess != null && !speechmaticsProcess.HasExited)
        try { speechmaticsProcess.Kill(); } catch { }
}
```

---

### BUG #3 — Markdown cleanup regex strips `C#` from AI answers
**File:** `MainWindow.xaml.cs` — `CleanAiOutput()`, line 555  
**Code:**
```csharp
ans = Regex.Replace(ans, @"(?<!\s)[*#_]{1,3}(?!\s)", "").Trim();
```
**Problem:** This regex matches `#` characters not surrounded by whitespace. So `C#,` becomes `C,` and `C#.` becomes `C.` and `C#;` becomes `C;`. Any AI response mentioning "C#" followed by punctuation (which is extremely common in tech interviews) gets corrupted silently.  
**Examples of broken output:**
- "I know C#, Python..." → "I know C, Python..."
- "worked with C#." → "worked with C."

**Fix:** Either remove this aggressive regex entirely (the first line already handles code fences), or exclude `#` preceded by letters:
```csharp
// Only remove standalone markdown markers, not language suffixes
ans = Regex.Replace(ans, @"(?<!\w)[*_]{1,3}(?!\w)", "").Trim();
```

---

### BUG #4 — SSL certificate verification completely disabled in Python engine
**File:** `speechmatics_engine.py` — lines 277–279  
**Code:**
```python
bypass_ssl_ctx = ssl.create_default_context()
bypass_ssl_ctx.check_hostname = False
bypass_ssl_ctx.verify_mode = ssl.CERT_NONE
```
**Problem:** All TLS certificate validation is disabled for every Speechmatics WebSocket connection. Your API key is sent over a connection that cannot verify the server's identity. This enables man-in-the-middle attacks — anyone on the same network can intercept and steal the Speechmatics API key.  
**Fix:** Remove the custom SSL context and use the default:
```python
settings = ConnectionSettings(
    url=endpoint,
    auth_token=args.key.strip(),
    # Remove ssl_context entirely — use default secure context
)
```

---

### BUG #5 — Speechmatics API key fully visible in Task Manager and Process Monitor
**File:** `MainWindow.xaml.cs` — line 669  
**Code:**
```csharp
speechmaticsProcess.StartInfo.Arguments = $"\"{pyScript}\" --key {smKey}{deviceArg}";
```
**Problem:** The Speechmatics API key is passed as a plain command-line argument. On Windows, any user with Task Manager, Process Hacker, or Process Monitor can read the full key. If you ever share your screen or run monitoring tools, the key is exposed.  
**Fix:** Pass the key via an environment variable instead:
```csharp
speechmaticsProcess.StartInfo.EnvironmentVariables["SM_API_KEY"] = smKey;
speechmaticsProcess.StartInfo.Arguments = $"\"{pyScript}\"{deviceArg}";
```
And in Python: `auth_token = os.environ["SM_API_KEY"]`

---

## 🟠 HIGH — Significant Functional Bugs

---

### BUG #6 — `devices.txt` path navigates 3 directories up — fails in production
**File:** `SettingsWindow.xaml.cs` — line 86  
**Code:**
```csharp
string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..", "devices.txt");
```
**Problem:** This goes 3 parent directories above the base directory to find `devices.txt`. This only works if the app is run from the exact `bin\Debug\net8.0-windows\` folder inside your dev workspace. If the app is published/installed anywhere else, this navigates to an unrelated folder and the file is never found → audio device list shows "No devices" → user can't select their microphone.  
**Fix:**
```csharp
// Check app directory first, then AppData
string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "devices.txt");
if (!File.Exists(path))
    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "InterviewCopilot", "devices.txt");
```

---

### BUG #7 — `ResumeParser` hard-codes current date as March 2026
**File:** `ResumeParser.cs` — lines 27–28  
**Code:**
```csharp
private const int NOW_YEAR = 2026;
private const int NOW_MONTH = 3; // March
```
**Problem:** All "Present" job duration calculations are frozen at March 2026. After that date, current-job durations are wrong. If someone's resume says "Jan 2025 - Present" and it's July 2026, the app calculates 15 months instead of 19. The AI then gives incorrect total experience numbers.  
**Fix:**
```csharp
private static readonly int NOW_YEAR = DateTime.Now.Year;
private static readonly int NOW_MONTH = DateTime.Now.Month;
```

---

### BUG #8 — Firebase session expires after 55 min with no auto-refresh — forces re-login
**File:** `UserSession.cs` — lines 97–101  
**Problem:** Firebase ID tokens are valid for 1 hour. The app expires them at 55 minutes and forces the user to log in again. There is no token refresh mechanism (the comment at line 117 acknowledges this). During a long interview session, the user could be prompted to re-login mid-conversation, losing their session context.  
**Fix:** Store the `refreshToken` returned by Firebase at login and use the Firebase token refresh endpoint:
```csharp
// In SessionData:
public string RefreshToken { get; set; } = "";

// Add a method to silently refresh before expiry:
public static async Task<bool> RefreshTokenIfNeededAsync()
{
    if ((DateTime.UtcNow - LoadSavedAt()).TotalMinutes < 50) return true;
    // POST to https://securetoken.googleapis.com/v1/token with refresh_token
}
```

---

### BUG #9 — `PromptBuilder.BuildMessages()` is never called — all session history goes unused
**File:** `MainWindow.xaml.cs` — `SendBackendRequestAsync()`, line 490  
**Code:**
```csharp
var payload = new { question, resume = resume ?? "", provider = "groq" };
```
**Problem:** The 600+ lines of `PromptBuilder.cs` — question type detection, session history, format rules, drill-down detection, all of it — is never actually sent to the backend. The payload only sends `question`, `resume`, and `provider`. `PromptBuilder.AddToHistory()` IS called (line 441) so history accumulates, but it's never included in any API request. The backend has no memory of previous questions.  
**Impact:** Every question is answered in isolation. The AI can't give MICRO answers to drill-downs, can't avoid repeating examples, can't recognize "you said earlier..." questions.  
**Fix:** If the backend supports a `messages` array, include it:
```csharp
var messages = PromptBuilder.BuildMessages(ResumeParser.ExtractFacts(resume), question);
var payload = new { messages, provider = "groq" };
```
Or confirm the backend does its own prompting — in which case the `PromptBuilder.cs` file is dead code and should be removed to avoid confusion.

---

### BUG #10 — `HttpResponseMessage` never disposed — memory leak on every AI call
**File:** `MainWindow.xaml.cs` — `StreamFromBackend()`, line 466  
**Code:**
```csharp
HttpResponseMessage res = await SendBackendRequestAsync(question, resume, ct);
```
**Problem:** `HttpResponseMessage` implements `IDisposable` and holds the response stream open. It's assigned to a local variable with no `using` statement. After each AI call, the response object leaks until GC runs. Over many calls in a long interview session, this accumulates.  
**Fix:** Restructure to dispose properly. Since this is an async iterator, use a helper:
```csharp
using HttpResponseMessage res = await SendBackendRequestAsync(question, resume, ct);
```

---

## 🟡 MEDIUM — Logic Errors and UX Issues

---

### BUG #11 — F12 global hotkey fires in ALL apps (Chrome DevTools, other tools)
**File:** `GlobalHotkey.cs` — lines 59–62  
**Code:**
```csharp
if (vkCode == VK_F12 && _onF12Pressed != null)
{
    _onF12Pressed.Invoke();
}
```
**Problem:** Unlike the Space hotkey (which checks if the app is NOT focused), F12 fires unconditionally from every app. Pressing F12 in Chrome to open DevTools also toggles the debug window. Pressing F12 in VS Code activates the go-to-definition shortcut AND toggles the debug window.  
**Fix:** Apply the same focus check as Space — only fire when the owner window is NOT focused:
```csharp
if (vkCode == VK_F12 && _onF12Pressed != null)
{
    IntPtr foreground = GetForegroundWindow();
    if (OwnerWindowHandle == IntPtr.Zero || foreground != OwnerWindowHandle)
        _onF12Pressed.Invoke();
}
```

---

### BUG #12 — Camera mode moves window off-screen instead of hiding it
**File:** `MainWindow.xaml.cs` — `CameraMode_Click()`, lines 323–325  
**Code:**
```csharp
this.Height = 1; this.Width = 1;
this.Top = -200; this.Left = -200;
```
**Problem:** The window is shrunk to 1×1 pixel and moved to (-200, -200). It's still "visible" to the OS, still processes input, and can conflict with apps in the top-left corner. The simpler and correct approach is `this.Hide()`.  
**Fix:**
```csharp
private void CameraMode_Click(object sender, RoutedEventArgs e)
{
    _isCameraMode = true;
    NormalModeGrid.Visibility = Visibility.Collapsed;
    this.Hide(); // Proper hide
    if (answerWindow != null)
    {
        answerWindow.ToggleCameraMode(true);
        try { WindowStealth.SetStealthMode(answerWindow, true); } catch { }
    }
}

private void ExitCameraMode()
{
    _isCameraMode = false;
    this.Show();
    // Restore to center
    this.Top = (SystemParameters.PrimaryScreenHeight - 740) / 2;
    this.Left = (SystemParameters.PrimaryScreenWidth - 1120) / 2;
    NormalModeGrid.Visibility = Visibility.Visible;
    if (answerWindow != null) answerWindow.ToggleCameraMode(false);
}
```

---

### BUG #13 — API key first 8 characters leaked to debug log
**File:** `speechmatics_engine.py` — line 124  
**Code:**
```python
print(f">>> API key       : {args.key[:8]}...", flush=True)
```
**Problem:** The first 8 characters of the Speechmatics API key are printed to stdout, which gets captured by the C# debug window. This is visible to anyone who presses F12. While partial, key prefixes can help narrow brute-force attacks.  
**Fix:**
```python
print(f">>> API key       : {'*' * 8}...", flush=True)
```

---

### BUG #14 — Error detection in Python uses string matching on `"401"` — fragile
**File:** `speechmatics_engine.py` — line 433  
**Code:**
```python
if "401" in err or "Unauthorized" in err:
    print(">>> FATAL: API key rejected (401). Check your Speechmatics key.", flush=True)
    exit(1)
```
**Problem:** Checking if the string `"401"` appears anywhere in an error message will false-positive on paths like `C:\Users\user401\...`, port numbers, or any error containing that digit sequence. A legitimate transient error could trigger a fatal exit.  
**Fix:** Check for specific exception types or HTTP status codes:
```python
if hasattr(e, 'status_code') and e.status_code == 401:
    exit(1)
elif "Unauthorized" in err and ("401" in err or "authentication" in err.lower()):
    exit(1)
```

---

### BUG #15 — Race condition: `RESET_FLAG` is deleted in `handle_final` but not `handle_partial`
**File:** `speechmatics_engine.py` — lines 297–304 vs 321–323  
**Problem:** When Space is pressed and the transcript is cleared, `reset.flag` is written. In `handle_final`, when the flag is found, `confirmed_text` is cleared AND the flag is deleted. But in `handle_partial`, the flag is only checked — it's not deleted. This means if a partial transcript fires AFTER the reset is processed by a final transcript, the partial callback will still see `confirmed_text = ""` correctly. However, if only partial callbacks fire and no final comes, the reset flag is never deleted and subsequent partial transcripts never accumulate → transcript stays blank indefinitely.  
**Fix:** Delete the reset flag in `handle_partial` as well:
```python
def handle_partial(msg):
    nonlocal partial_text
    if os.path.exists(PAUSE_FLAG):
        return
    if os.path.exists(RESET_FLAG):
        try: os.remove(RESET_FLAG)
        except: pass
        return
```

---

## 🔵 LOW — Code Quality and Minor Issues

---

### BUG #16 — `Window_KeyDown` event handler is empty dead code
**File:** `MainWindow.xaml.cs` — line 699  
**Code:**
```csharp
private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { }
```
If this is bound in XAML, it consumes keyboard events silently. Remove it.

---

### BUG #17 — `VerifyResume_Click` is an empty handler
**File:** `MainWindow.xaml.cs` — line 720  
**Code:**
```csharp
private void VerifyResume_Click(object sender, RoutedEventArgs e) { }
```
The "Verify Resume" button does nothing. If visible in the UI, users will click it expecting feedback. Either implement it (call `ResumeParser.ExtractFacts()` and show a preview popup) or hide the button.

---

### BUG #18 — `sessionNumber++` in button click handler causes session number gaps
**File:** `MainWindow.xaml.cs` — line 615  
**Code:**
```csharp
EndSession();
// ...
sessionNumber++;  // Manual increment here
```
**Problem:** `StartNewSession()` already advances `sessionNumber` via its `while (File.Exists(...))` loop. The manual `sessionNumber++` here means after stopping a session, the counter is already at N+1. When starting again, `StartNewSession()` checks if `interview_(N+1).txt` exists — it doesn't (it was never created) — so it uses N+1 correctly. BUT the extra pre-increment means session 1, 3, 5... are used and 2, 4, 6... are skipped. Remove the manual `sessionNumber++`.

---

### BUG #19 — Firebase API key hardcoded in source
**File:** `LoginWindow.xaml.cs` — line 16  
**Code:**
```csharp
private const string FirebaseApiKey = "AIzaSyAGGmuFpR0qkCHLI3q2cPv_o3cQlbIU8lE";
```
Firebase client-side keys are semi-public by design, but this key is now committed to source and visible in the compiled binary. Anyone can decompile the binary and extract it, then use it to make authentication calls against your Firebase project. Move it to a build-time variable or config file excluded from source control.

---

### BUG #20 — Name detection in `ResumeParser` can pick the wrong line
**File:** `ResumeParser.cs` — lines 43–48  
**Code:**
```csharp
if (t.Length > 2 && t.Length < 50 && !t.Contains(":") && !t.StartsWith("•"))
{
    sb.AppendLine("Name: " + t);
    break;
}
```
**Problem:** If the resume starts with "RESUME", "Chicago, IL", or a section header, that gets reported as the name. This corrupts the facts fed to the AI.  
**Fix:** Skip known header words:
```csharp
string[] skipWords = { "resume", "curriculum vitae", "cv", "profile", "summary" };
string lower = t.ToLower();
if (skipWords.Any(w => lower.Contains(w))) continue;
```

---

## Summary Table

| # | Severity | File | Description |
|---|----------|------|-------------|
| 1 | 🔴 Critical | MainWindow.xaml.cs:305 | Hard-coded personal path — app broken for all other users |
| 2 | 🔴 Critical | MainWindow.xaml.cs:686 | Kills ALL system Python processes on startup and close |
| 3 | 🔴 Critical | MainWindow.xaml.cs:555 | `C#` stripped from AI responses by broken regex |
| 4 | 🔴 Critical | speechmatics_engine.py:277 | SSL verification disabled — MITM attack possible |
| 5 | 🔴 Critical | MainWindow.xaml.cs:669 | API key visible in Task Manager (command-line arg) |
| 6 | 🟠 High | SettingsWindow.xaml.cs:86 | `../../..` path for devices.txt fails outside dev env |
| 7 | 🟠 High | ResumeParser.cs:27 | Hardcoded date March 2026 — wrong experience durations |
| 8 | 🟠 High | UserSession.cs:97 | No token refresh — forced re-login every 55 minutes |
| 9 | 🟠 High | MainWindow.xaml.cs:490 | PromptBuilder never called — session history unused |
| 10 | 🟠 High | MainWindow.xaml.cs:466 | HttpResponseMessage never disposed — memory leak |
| 11 | 🟡 Medium | GlobalHotkey.cs:59 | F12 fires globally, conflicts with other apps |
| 12 | 🟡 Medium | MainWindow.xaml.cs:323 | Camera mode hides window off-screen instead of Hide() |
| 13 | 🟡 Medium | speechmatics_engine.py:124 | First 8 chars of API key printed to debug log |
| 14 | 🟡 Medium | speechmatics_engine.py:433 | String `"401"` matching causes false fatal exits |
| 15 | 🟡 Medium | speechmatics_engine.py:321 | RESET_FLAG not deleted in handle_partial — stale transcript |
| 16 | 🔵 Low | MainWindow.xaml.cs:699 | Empty `Window_KeyDown` handler (dead code) |
| 17 | 🔵 Low | MainWindow.xaml.cs:720 | Empty `VerifyResume_Click` handler (dead code) |
| 18 | 🔵 Low | MainWindow.xaml.cs:615 | Manual `sessionNumber++` causes session file gaps |
| 19 | 🔵 Low | LoginWindow.xaml.cs:16 | Firebase API key hardcoded in source |
| 20 | 🔵 Low | ResumeParser.cs:43 | Name heuristic picks wrong line from some resumes |

---

*Report generated by deep static analysis of all source files.*
