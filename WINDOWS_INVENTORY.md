# Windows app inventory

Written for the Mac session's mutual-inventory exchange, 2026-08-25, at commit
`5aaf54c`.

**Scope, stated honestly up front.** Sections 2, 3, 4 and 5 are complete for
the surfaces named. Section 1 enumerates every control declared in
`MainWindow.xaml` and every key handled in `Window_PreviewKeyDown`, but the
"what happens at a bad moment" column is **verified only where a line is
cited** — where it says *unverified*, I read the handler and did not exercise
it. That distinction is the whole point of the exercise, so it is marked rather
than smoothed over.

Everything here is `MainWindow.xaml.cs` unless another file is named.

---

## 5. ANSWERED FIRST: the renderer is fence-driven

**Windows keys on fences, not headers.** `ShowAnswer` (`:4950`) extracts code
with `FencedCode` (`:4928`):

```csharp
new(@"```[ \t]*([A-Za-z0-9+#_-]*)[ \t]*\r?\n(.*?)(?:```|$)",
    RegexOptions.Singleline | RegexOptions.Compiled)
```

Every match goes to `CodeBox`; the fences are then removed from the prose so
the same code is not shown twice (`:4966`). If no fence is present, `CodePanel`
collapses and the whole answer renders as prose (`:4969-4976`). The language tag
after the opening fence becomes `CodeLanguageLabel`.

**So MAC_CATCHUP §12 is accurate about Windows.** It is not wrong about both
apps.

**And the shared backend prompt already asks for fences** —
`InterviewController.java:1732`:

> "The complete solution, inside a fence: \`\`\`language on its own line before
> it and \`\`\` after."

That makes the Mac divergence sharper than "a missing feature": a Mac prompt
that forbids fences is contradicting the shared server prompt, and
`NetworkClient.swift:645` then strips what the server was asked to produce. Two
layers each undoing the other. Worth checking whether the Mac local prompt
overrides or merely supplements the backend one, because if the server prompt
wins, Mac has been stripping fences that were arriving correctly.

**Caveat on my own side:** the fence-driven renderer means an unfenced answer
silently loses its code panel and renders as prose. That is graceful, but it is
the same class of dependency-on-model-habit that the cleaner had — and the
prompt instruction is the only thing making it hold. I have not measured how
often the model complies.

---

## 1. USER-VISIBLE CONTROLS

### Global hotkeys

Low-level keyboard hook, `WH_KEYBOARD_LL` (`GlobalHotkey.cs:9`, installed
`:75`). Works when the app is not focused, which is the point — it is used
while another application is in front.

| Key | Does | Depends on | Bad-moment behaviour |
|---|---|---|---|
| **Space** | Start/stop a listening turn — `HandleSpaceDown` (`:2146`) | `isMuted`, not typing in a field | Ignored if `_spaceHandling`, `isProcessing`, or already unmuted (`:2154`). Blocked with a message if `_engineUsageLimitReached` (`:2149`). Taps under 200 ms are discarded as accidental (`:2215`) |
| **F8** | Read the foreground window now — `HandleScreenAnalysisAsync` (`:4784`) | Nothing. Works with the watch setting off | *unverified* mid-answer |
| **F9** | Read the whole screen (`:4794`) | Nothing | *unverified* mid-answer |
| **F12** | Toggle the debug window (`:4770`) | — | Follows the stealth setting (`DebugWindow.cs:51`) |
| **Esc** | Close the sessions panel (`:4776`) | Only while that panel is visible | Otherwise not handled |

**Typing guard:** `IsTypingInTextField()` (`:2142`) checks focus in the resume,
ask, company and job-description boxes, and is applied in `HandleSpaceDown`
itself rather than only in the WPF handler — so the global hook's unconditional
Space cannot bypass it.

### Buttons (`MainWindow.xaml`, 19 declared)

`MicBtn` · `AskSubmitBtn` · `NewSessionBtn` · `ClearAnswerBtn` ·
`CopyAnswerBtn` · `CopyCodeBtn` · `SavedResumesBtn` · `ResumeCard` ·
`ResumeToggleBtn` · `ClearHintsBtn` · `SignInHeaderBtn` · `PopupSignIn` ·
`PinBtn` · `MinimizeBtn` · `CloseBtn` · `ExitCameraMode` ·
`InAppAlertDismiss` · `OnboardingClose` · sessions panel "Back to interview"
(`SessionsPanel.xaml`)

Handlers are `<Name>_Click` in `MainWindow.xaml.cs`. **I have not walked all
nineteen for bad-moment behaviour** — the ones I have are Mic (above), Copy
Code (`_currentAnswerCode`, empty when no fence arrived), and the sessions
toggle (`SessionsBtn_Click`, collapses `InterviewContent`).

---

## 2. BACKGROUND BEHAVIOUR — twelve timers

| Timer | Interval | Starts | Stops | Worst moment |
|---|---|---|---|---|
| `transcriptTimer` (`:426`) | 40 ms | window load | app exit | Polls `latest.txt`. Highest-frequency thing in the app |
| `thinkingTimer` (`:430`) | anim | answer starts | answer ends | Cosmetic |
| `_engineMonitorTimer` (`:327`) | `EngineMonitorSecs` | startup | — | Restarts a dead engine with exponential backoff to 30 s (`:4602`) |
| `creditsRefreshTimer` (`:366`) | 5 min | after sign-in | — | Background fetch; failure is logged, not surfaced |
| `warmupTimer` (`:379`) | 75 s | startup | — | Keeps the backend warm |
| `_listeningMeterTimer` (`:1513`) | **5 s** | `StartListeningMeter` (`:1505`) | `!isListening` at tick (`:1540`) | **Billing and the deafness detector both ride this.** Its interval is why the 12 s deafness threshold is observed at 15 s |
| `_preparedShotTimer` (`:2711`) | `PreparedShotInterval` | watch mode on | watch mode off | Pre-captures so a screen question does not wait |
| `_sessionTimer` (`:3745`) | 1 s | session start | session end | Elapsed display |
| `_alertTimer` (`:1200`) | 9 s | alert shown | dismissed | Cosmetic |
| `_autoModeNoticeTimer` (`:1435`) | 2.5 s | notice shown | — | Cosmetic |
| `_jobContextSaveTimer` (`:6176`) | 500 ms | typing | flushed on exit (`:5801`) | Debounced save; **flushed before the timer stops**, or an edit typed just before closing is lost |
| opacity revert (`:5061`) | 2 s | preview | — | Cosmetic |

**Metering.** `ListeningMeterTick` (`:1538`) accumulates by delta and re-stamps,
so ticks cannot double-count. `StopListeningMeter` (`:1519`) reports whole
minutes and carries the remainder — **the session is the billing unit, not the
turn** (decided 2026-08-25). `FlushListeningMeterOnExit` (`:1704`) is the only
place a remainder is rounded up, and it sends **synchronously**
(`ReportListeningMinutesOnExit`) because the async version deadlocks against
the closing UI thread.

**Deafness detector.** `SpeechHealth.ShouldWarn` (`SpeechHealth.cs`), consulted
from the meter tick. 12 s threshold, 5 s poll, so users observe 15 s. Once a
minute at most. Covers **all modes**, bounded by turn length.

---

## 3. STACK

| | |
|---|---|
| Runtime | .NET 8, WPF, `net8.0-windows` |
| STT | Speechmatics real-time via `speechmatics-python`, run as a **separate process** — a PyInstaller one-folder build of the shared `speechmatics_engine.py`, ~21.9 MB, launched at `:4139` |
| Engine transport | stdout line matching. Contract asserted by `tests/verify_engine_contract.py` |
| Audio | `pyaudiowpatch` for WASAPI loopback — collected explicitly in the build, because PyInstaller's static analysis walks past the try/except import and the frozen engine silently falls back to stock pyaudio and loses system audio |
| Screen capture | GDI `BitBlt` → WPF `BitmapSource`, **PNG** (`PngBitmapEncoder`), palette-reduced to 256 colours when smaller, budgeted to 700 KB (`ScreenAnalyzer.WithinUploadBudget`) |
| Hotkeys | `SetWindowsHookEx(WH_KEYBOARD_LL)` |
| Stealth | `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` (`WindowStealth.cs`) |
| Updates | **Velopack** (`Program.cs:27`) for the direct EXE; MSIX/Store updates via Partner Center |
| Storage | `%APPDATA%`, DPAPI per-user (`SecureDataProtector.cs`, `ProtectedData.Protect`) |
| Secrets | None in the binary. The Speechmatics key is minted per session by the backend |
| Single instance | Named mutex `Local\InterviewCopilot_SingleInstance` (`App.xaml.cs:33`) |

**Found while writing this:** `InterviewCopilot.csproj` still declares
`<Version>1.0.13.0</Version>` while the shipped build and the MSIX manifest are
1.0.14. The assembly version is stale. Not user-visible, but any crash report
or support log naming a version will name the wrong one.

---

## 4. STABILITY

| Scenario | What happens | Where |
|---|---|---|
| Network drops mid-answer | SSE stream ends; error surfaced, credits refunded server-side if nothing was delivered | `InterviewController.java:385` |
| STT session dies | `STATUS: OFFLINE` clears `_engineOnline`; engine monitor restarts with backoff to 30 s | `:4229`, `:4602` |
| Credits hit zero mid-answer | Charged up front, refunded if no answer was delivered. Mid-stream exhaustion is **unverified** | `InterviewController.java:281` |
| Quit mid-answer | Closing handler flushes the meter synchronously, bounded 3 s | `:1704` |
| **Force-quit / crash** | **Banked minute is lost.** Decided deliberately — at-least-once delivery would risk double billing | MAC_CATCHUP |
| Two instances | Second exits on the mutex | `App.xaml.cs:33` |
| Backend 401 | Sign-in prompt | *unverified end-to-end* |
| Backend 413 | Was reachable at 1 MB; now `client_max_body_size 3m` and the app budgets to 700 KB | resolved 2026-08-25 |
| Backend returns HTML not JSON | **Unverified.** `JsonDocument.Parse` would throw into a catch that logs. I have not tested it |
| Machine sleeps | **Unverified.** Timers resume; the meter would bank wall-clock time across the sleep, which is a billing question I have not measured |

### Things that could hang, named as asked

1. **`ReportListeningMinutesOnExit`** — bounded at 3 s, deliberately. Was
   unbounded-in-effect before: `.Wait(1500)` on the UI thread against a method
   that awaits without `ConfigureAwait(false)`, which could never complete.
2. **Engine stdout readers** — `ReadLineAsync(ct)` with a cancellation token;
   if the engine wedges without closing stdout, the reader waits. The deafness
   detector is the backstop, and it only covers the transcribing case.
3. **`_creditsFetchGate`** (`:132`) — a `SemaphoreSlim(1,1)`. If a holder
   faulted without releasing, credit refreshes would stop silently. I have not
   audited every path for a missing release.

---

## 6. WHERE I THINK WINDOWS IS RIGHT AND MAC SHOULD MATCH

1. **Fence-driven code extraction.** The server is already asked to produce
   fences. A renderer keyed on `━━━ TITLE ━━━` headers depends on the model
   reproducing decorative characters exactly — a stricter requirement than a
   fence, and one no prompt enforces as well.
2. **PNG over JPEG for captures.** JPEG rings around glyph edges; at small
   sizes that is the difference between the model reading `l` and `1`.
3. **The deafness detector covering all modes.** A candidate in a manual mode
   with a dead transcriber gets the same warning.
4. **Charging on delivery, refunding on failure** rather than charging on
   request.

## And where I am not confident Windows is right

- **The 40 ms transcript poll** is a file read 25 times a second for the whole
  session. It works, but if Mac reads the transcript by callback rather than
  polling, Mac is likely correct and I should change.
- **Fence dependency** — see §5 caveat. Graceful when it fails, but it fails
  silently, and I do not know the compliance rate.
- **Twelve timers** is a lot of independent state for one window. Two of the
  worst bugs this month were timers interacting.
