using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InterviewCopilot
{
    /// <summary>
    /// Captures ALL connected monitors and streams the AI vision analysis token-by-token.
    /// F8 = full virtual screen (all monitors). F9 = primary monitor only (faster).
    /// No extra NuGet packages — pure Win32 P/Invoke + WPF imaging.
    /// </summary>
    public static class ScreenAnalyzer
    {
        // Pre-compiled markdown-strip patterns used in CleanContent (called after every SSE response)
        private static readonly Regex RxBold     = new(@"\*{2}([^*\n]+)\*{2}", RegexOptions.Compiled);
        private static readonly Regex RxItalic   = new(@"\*([^*\n]+)\*",       RegexOptions.Compiled);
        private static readonly Regex RxUnder    = new(@"_{1,2}([^_\n]+)_{1,2}", RegexOptions.Compiled);
        private static readonly Regex RxHeading  = new(@"(?m)^#{1,6}\s+",       RegexOptions.Compiled);
        private static readonly Regex RxBlankRun = new(@"\n{3,}",               RegexOptions.Compiled);
        private const int MaxResponseChars = 30_000;
        private const int MaxResumeContextChars = 6_000;

        #region ── Win32 GDI (no System.Drawing required) ───────────────────────
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(
            IntPtr hdcDst, int xDst, int yDst, int cx, int cy,
            IntPtr hdcSrc, int xSrc, int ySrc, int rop);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);

        private const int SRCCOPY          = 0x00CC0020;
        private const int SM_CXSCREEN      = 0;   // primary monitor width
        private const int SM_CYSCREEN      = 1;   // primary monitor height
        private const int SM_XVIRTUALSCREEN  = 76; // virtual screen left edge (negative for left monitors)
        private const int SM_YVIRTUALSCREEN  = 77; // virtual screen top edge
        private const int SM_CXVIRTUALSCREEN = 78; // total width of all monitors combined
        private const int SM_CYVIRTUALSCREEN = 79; // total height of all monitors combined
        #endregion

        // ── Last captured context (injected into follow-up voice questions) ───
        public static string LastScreenContext { get; private set; } = "";

        // ═════════════════════════════════════════════════════════════════════
        // CAPTURE
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Captures ALL connected monitors as a single wide JPEG.
        /// On a single-monitor setup this is the primary screen only.
        /// On dual/triple monitor setups this captures the full desktop.
        /// JPEG quality 90 — higher than before (was 78) for better text legibility.
        /// </summary>
        public static byte[] CaptureScreen()
        {
            // Virtual screen covers all monitors; vx/vy can be negative (left/top monitors)
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            // Fallback: if virtual screen metrics are unavailable, use primary only
            if (vw <= 0 || vh <= 0) { vx = 0; vy = 0; vw = GetSystemMetrics(SM_CXSCREEN); vh = GetSystemMetrics(SM_CYSCREEN); }

            return CaptureRegionCore(vx, vy, vw, vh, maxW: 2560, maxH: 1600);
        }

        /// <summary>
        /// Captures only the primary monitor — smaller payload, faster API response.
        /// Useful when all interview content is on the main display.
        /// </summary>
        public static byte[] CapturePrimaryScreen()
        {
            return CaptureRegionCore(0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN),
                                 maxW: 1920, maxH: 1200);
        }

        /// <summary>
        /// Captures a user-selected desktop rectangle. Coordinates are physical
        /// virtual-screen pixels and can be negative on left/top monitors.
        /// </summary>
        public static byte[] CaptureRegion(int x, int y, int width, int height)
        {
            return CaptureRegionCore(x, y, width, height, maxW: 1920, maxH: 1400);
        }

        /// <summary>
        /// Core capture: BitBlt a region of the desktop DC into a JPEG byte array.
        /// </summary>
        private static byte[] CaptureRegionCore(int srcX, int srcY, int srcW, int srcH, int maxW, int maxH)
        {
            if (srcW <= 0 || srcH <= 0)
                throw new InvalidOperationException("The requested screen region is invalid.");

            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                throw new InvalidOperationException($"Could not access the desktop ({Marshal.GetLastWin32Error()}).");
            IntPtr memDc    = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, screenDc);
                throw new InvalidOperationException($"Could not create a memory device context ({Marshal.GetLastWin32Error()}).");
            }
            IntPtr hBitmap  = CreateCompatibleBitmap(screenDc, srcW, srcH);
            if (hBitmap == IntPtr.Zero)
            {
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
                throw new InvalidOperationException($"Could not allocate a capture bitmap ({Marshal.GetLastWin32Error()}).");
            }
            IntPtr oldObj   = SelectObject(memDc, hBitmap);
            if (oldObj == IntPtr.Zero || oldObj == new IntPtr(-1))
            {
                DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
                throw new InvalidOperationException($"Could not select the capture bitmap ({Marshal.GetLastWin32Error()}).");
            }

            if (!BitBlt(memDc, 0, 0, srcW, srcH, screenDc, srcX, srcY, SRCCOPY))
            {
                SelectObject(memDc, oldObj);
                DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
                throw new InvalidOperationException($"Could not copy the screen image ({Marshal.GetLastWin32Error()}).");
            }
            SelectObject(memDc, oldObj);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);

            BitmapSource bmp;
            try
            {
                bmp = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DeleteObject(hBitmap);
            }

            // Scale down if needed — preserves aspect ratio
            if (srcW > maxW || srcH > maxH)
            {
                double scale = Math.Min((double)maxW / srcW, (double)maxH / srcH);
                bmp = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = 90 }; // was 78 — higher = better text
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        // ═════════════════════════════════════════════════════════════════════
        // VISION PROVIDER — server picks the actual model; client only says which
        // ═════════════════════════════════════════════════════════════════════

        private static string GetProvider() => SettingsWindow.IsGroq() ? "groq" : "openai";

        // ═════════════════════════════════════════════════════════════════════
        // PROMPT
        // ═════════════════════════════════════════════════════════════════════

        private static string BuildScreenPrompt(string? resumeContext)
        {
            if (!string.IsNullOrWhiteSpace(resumeContext) && resumeContext.Length > MaxResumeContextChars)
                resumeContext = resumeContext[..MaxResumeContextChars] + "\n[Candidate background truncated]";

            var sb = new StringBuilder();

            // ── Role ─────────────────────────────────────────────────────────
            sb.AppendLine("You are an expert interview coach helping a candidate in a live interview.");
            sb.AppendLine("Analyze the screenshot and respond using the EXACT structure shown below for the matching content type.");
            sb.AppendLine("Base every claim on text or visuals that are actually visible. Never invent unreadable text, requirements, code, experience, or metrics.");
            sb.AppendLine("Choose one matching content type before answering. Prioritize the interview prompt, code, error, or diagram over unrelated application chrome.");
            sb.AppendLine("If the important text is unreadable or missing, say exactly what is visible and ask for a clearer capture instead of guessing.");
            sb.AppendLine();

            // ── Critical output rules ─────────────────────────────────────────
            sb.AppendLine("CRITICAL OUTPUT RULES — OBEY EXACTLY:");
            sb.AppendLine("1. Use ━━━ TITLE ━━━ as section headers — nothing else (no ##, no **, no ---).");
            sb.AppendLine("2. One blank line after each section header, one blank line before the next header.");
            sb.AppendLine("3. Bullets use the • character only (never -, *, numbers).");
            sb.AppendLine("4. No markdown: no **bold**, no _italic_, no backtick code fences.");
            sb.AppendLine("5. Code goes directly after ━━━ SOLUTION ━━━ with no fences.");
            sb.AppendLine("6. Never truncate code — write the complete solution even if it's long.");
            sb.AppendLine("7. Keep non-code sections short and scannable.");
            sb.AppendLine();

            // ── Template: Coding problem ──────────────────────────────────────
            sb.AppendLine("─────────────────────────────────────────────────────");
            sb.AppendLine("IF SCREEN SHOWS A CODING / ALGORITHM PROBLEM, output:");
            sb.AppendLine("─────────────────────────────────────────────────────");
            sb.AppendLine();
            sb.AppendLine("━━━ PROBLEM ━━━");
            sb.AppendLine("[One sentence: what the problem is asking for]");
            sb.AppendLine();
            sb.AppendLine("━━━ APPROACH ━━━");
            sb.AppendLine("Brute force:  [brief — 1 sentence]  →  O(n²) time");
            sb.AppendLine("Optimal:      [brief — 1 sentence]  →  O(n) time");
            sb.AppendLine();
            sb.AppendLine("━━━ SOLUTION ━━━");
            sb.AppendLine("[Complete working code. Language = whatever is on screen, default Python.]");
            sb.AppendLine("[Inline comments on non-obvious lines. Handle edge cases. No truncation.]");
            sb.AppendLine();
            sb.AppendLine("━━━ TESTS ━━━");
            sb.AppendLine("• Normal: [input] → [expected output]");
            sb.AppendLine("• Edge: [empty/min/max/duplicate case] → [expected output]");
            sb.AppendLine("• Failure or invalid case when relevant: [behavior]");
            sb.AppendLine();
            sb.AppendLine("━━━ COMPLEXITY ━━━");
            sb.AppendLine("Time: O(?)   |   Space: O(?)");
            sb.AppendLine();
            sb.AppendLine("━━━ EXPLANATION ━━━");
            sb.AppendLine("[Why the approach is correct, including the key invariant or proof idea.]");
            sb.AppendLine();
            sb.AppendLine("━━━ SAY THIS ━━━");
            sb.AppendLine("• \"[Opening line to say to the interviewer before coding]\"");
            sb.AppendLine("• \"[What to narrate as you write the key part]\"");
            sb.AppendLine("• \"[How to wrap up and state the complexity]\"");
            sb.AppendLine();

            // ── Template: System design ───────────────────────────────────────
            sb.AppendLine("─────────────────────────────────────────────────────");
            sb.AppendLine("IF SCREEN SHOWS A SYSTEM DESIGN / ARCHITECTURE DIAGRAM, output:");
            sb.AppendLine("─────────────────────────────────────────────────────");
            sb.AppendLine();
            sb.AppendLine("━━━ REQUIREMENTS & ESTIMATES ━━━");
            sb.AppendLine("• Functional: [core user behavior]");
            sb.AppendLine("• Non-functional: [scale, latency, availability, durability]");
            sb.AppendLine("• Assumptions: [traffic/storage estimates, clearly labeled]");
            sb.AppendLine();
            sb.AppendLine("━━━ COMPONENTS ━━━");
            sb.AppendLine("• [Component name] — [what it does in 1 sentence]");
            sb.AppendLine("• [repeat for each component]");
            sb.AppendLine();
            sb.AppendLine("━━━ DATA FLOW ━━━");
            sb.AppendLine("[2-3 sentences describing how data moves through the system]");
            sb.AppendLine();
            sb.AppendLine("━━━ TRADE-OFFS ━━━");
            sb.AppendLine("• [Scalability / bottleneck / consistency issue]");
            sb.AppendLine("• [Another trade-off worth discussing]");
            sb.AppendLine();
            sb.AppendLine("━━━ IMPROVEMENT ━━━");
            sb.AppendLine("[One concrete suggestion, e.g. \"Add Redis cache between API and DB\"]");
            sb.AppendLine();
            sb.AppendLine("━━━ RELIABILITY & OBSERVABILITY ━━━");
            sb.AppendLine("• [Failure handling, retries, idempotency, and degradation]");
            sb.AppendLine("• [Metrics, logs, traces, alerts, and an SLO]");
            sb.AppendLine();
            sb.AppendLine("━━━ SAY THIS ━━━");
            sb.AppendLine("• \"[How to open with requirements and assumptions]\"");
            sb.AppendLine("• \"[How to narrate the critical data path]\"");
            sb.AppendLine("• \"[How to close with the main trade-off]\"");
            sb.AppendLine();

            // ── Template: Multiple choice ─────────────────────────────────────
            sb.AppendLine("─────────────────────────────────────────────────────");
            sb.AppendLine("IF SCREEN SHOWS A MULTIPLE CHOICE / QUIZ QUESTION, output:");
            sb.AppendLine("─────────────────────────────────────────────────────");
            sb.AppendLine();
            sb.AppendLine("━━━ ANSWER ━━━");
            sb.AppendLine("[Correct option — state it directly, e.g. \"Option B\"]");
            sb.AppendLine();
            sb.AppendLine("━━━ WHY ━━━");
            sb.AppendLine("• Correct ([option]): [why it's right — 1 sentence]");
            sb.AppendLine("• Wrong ([option]):   [why it's wrong — 1 sentence]");
            sb.AppendLine("• Wrong ([option]):   [why it's wrong — 1 sentence]");
            sb.AppendLine();
            sb.AppendLine("━━━ WATCH OUT ━━━");
            sb.AppendLine("[Any trick or common misconception in this question]");
            sb.AppendLine();

            // ── Template: Behavioral ──────────────────────────────────────────
            sb.AppendLine("─────────────────────────────────────────────────────");
            sb.AppendLine("IF SCREEN SHOWS A BEHAVIORAL / SITUATIONAL TEXT QUESTION, output:");
            sb.AppendLine("─────────────────────────────────────────────────────");
            sb.AppendLine();
            sb.AppendLine("━━━ SITUATION ━━━");
            sb.AppendLine("[Context: where, when, what was at stake — 1-2 sentences]");
            sb.AppendLine();
            sb.AppendLine("━━━ ACTION ━━━");
            sb.AppendLine("• [Specific step you took]");
            sb.AppendLine("• [Another step — concrete, not vague]");
            sb.AppendLine("• [Third step if needed]");
            sb.AppendLine();
            sb.AppendLine("━━━ RESULT ━━━");
            sb.AppendLine("[Outcome with a specific number or metric]");
            sb.AppendLine();

            // ── Template: Error / bug ─────────────────────────────────────────
            sb.AppendLine("─────────────────────────────────────────────────────");
            sb.AppendLine("IF SCREEN SHOWS AN ERROR, BUG, OR STACK TRACE, output:");
            sb.AppendLine("─────────────────────────────────────────────────────");
            sb.AppendLine();
            sb.AppendLine("━━━ ROOT CAUSE ━━━");
            sb.AppendLine("[One sentence — the actual problem]");
            sb.AppendLine();
            sb.AppendLine("━━━ FIX ━━━");
            sb.AppendLine("[The corrected code line(s) — complete, ready to paste]");
            sb.AppendLine();
            sb.AppendLine("━━━ EXPLAIN ━━━");
            sb.AppendLine("[One sentence to say out loud to the interviewer]");
            sb.AppendLine();

            // ── Template: General ─────────────────────────────────────────────
            sb.AppendLine("─────────────────────────────────────────────────────");
            sb.AppendLine("IF SCREEN CONTENT DOES NOT MATCH ANY ABOVE, output:");
            sb.AppendLine("─────────────────────────────────────────────────────");
            sb.AppendLine();
            sb.AppendLine("━━━ WHAT I SEE ━━━");
            sb.AppendLine("[Brief description of what's on screen]");
            sb.AppendLine();
            sb.AppendLine("━━━ GUIDANCE ━━━");
            sb.AppendLine("• [Most relevant interview advice]");
            sb.AppendLine("• [Second point if needed]");

            if (!string.IsNullOrWhiteSpace(resumeContext))
            {
                sb.AppendLine();
                sb.AppendLine("─────────────────────────────────────────────────────");
                sb.AppendLine("CANDIDATE BACKGROUND (reference only if directly relevant):");
                sb.AppendLine("─────────────────────────────────────────────────────");
                sb.AppendLine(resumeContext);
            }

            return sb.ToString();
        }

        // ═════════════════════════════════════════════════════════════════════
        // POST-PROCESSOR  — runs once on the completed response before display
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Normalises the AI response so section headers are consistently spaced,
        /// stray markdown is removed, and no more than one blank line appears anywhere.
        /// </summary>
        public static string PostProcess(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;

            // 1. Strip any residual markdown that leaked through despite the prompt
            raw = Regex.Replace(raw, @"\*{2}([^*\n]+)\*{2}", "$1");   // **bold**
            raw = Regex.Replace(raw, @"\*([^*\n]+)\*",       "$1");   // *italic*
            raw = Regex.Replace(raw, @"_{1,2}([^_\n]+)_{1,2}", "$1"); // _italic_
            raw = Regex.Replace(raw, @"(?m)^#{1,6}\s+", "");          // ## headers
            raw = Regex.Replace(raw, @"```[a-zA-Z]*\r?\n?", "");      // code fences

            // Rewrite AI-tell long dashes into plain human punctuation. The ━ (U+2501)
            // used for section headers is a different character and is left untouched.
            raw = Regex.Replace(raw, @"(\S)[ \t]*[—–][ \t]+", "$1, "); // mid-sentence break -> comma
            raw = raw.Replace("—", "-").Replace("–", "-");             // any remaining -> hyphen

            raw = raw.Replace("\r\n", "\n").Replace("\r", "\n");

            // 2. Normalize section headers: ensure exactly one blank line above and below each
            var lines  = raw.Split('\n');
            var result = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd();
                string trimmedLine = line.TrimStart();
                bool isHeader = trimmedLine.StartsWith("━━━") && trimmedLine.EndsWith("━━━");

                if (isHeader)
                {
                    // Remove any trailing blank lines before this header
                    while (result.Count > 0 && result[^1] == "")
                        result.RemoveAt(result.Count - 1);
                    // One blank line above (unless this is the very first content)
                    if (result.Count > 0) result.Add("");
                    result.Add(line);
                    // One blank line after the header
                    result.Add("");
                }
                else
                {
                    // Collapse runs of more than 1 blank line
                    if (line == "" && result.Count > 0 && result[^1] == "")
                        continue;
                    result.Add(line);
                }
            }

            // 3. Strip leading/trailing blank lines
            while (result.Count > 0 && result[0]  == "") result.RemoveAt(0);
            while (result.Count > 0 && result[^1] == "") result.RemoveAt(result.Count - 1);

            return string.Join("\n", result);
        }

        // ═════════════════════════════════════════════════════════════════════
        // STREAMING ANALYSIS — primary API (tokens yielded as they arrive)
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Sends the screenshot to the vision AI and streams tokens as they arrive.
        /// Uses helper methods for all error-prone work so yield statements never
        /// appear inside try-catch blocks (C# iterator restriction).
        /// </summary>
        public static async IAsyncEnumerable<string> AnalyzeStreamAsync(
            byte[] imageBytes, string? resumeContext = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            string base64 = Convert.ToBase64String(imageBytes);
            string prompt = BuildScreenPrompt(resumeContext);
            string provider = GetProvider();
            string payloadJson = JsonSerializer.Serialize(new { image = base64, prompt, provider });

            // ── Send request via helper (never throws) ────────────────────────
            var (res, sendError) = await SendVisionRequestSafeAsync(payloadJson, ct);
            if (res == null)
            {
                throw new InvalidOperationException(sendError);
            }

            // ── Handle non-200 via helper (never throws) ──────────────────────
            if (!res.IsSuccessStatusCode)
            {
                string errMsg = DescribeVisionError(res.StatusCode);
                res.Dispose();
                throw new InvalidOperationException(errMsg);
            }

            // ── Stream SSE tokens ─────────────────────────────────────────────
            // yield inside try-finally is legal; only try-catch is forbidden.
            var accumulated = new StringBuilder();
            int responseLength = 0;
            try
            {
                using var stream = await res.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);

                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ct);
                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;
                    string data = line["data: ".Length..];
                    if (data == "[DONE]") break;

                    // ParseSseToken never throws — returns "" on any malformed line
                    string token = ParseSseToken(data);
                    if (!string.IsNullOrEmpty(token))
                    {
                        int remaining = MaxResponseChars - responseLength;
                        if (remaining <= 0) break;
                        if (token.Length > remaining) token = token[..remaining];
                        accumulated.Append(token);
                        responseLength += token.Length;
                        yield return token;
                        if (responseLength >= MaxResponseChars)
                        {
                            yield return "\n[Response truncated]";
                            break;
                        }
                    }
                }
            }
            finally
            {
                // Always update context so follow-up voice questions work
                string full = CleanContent(accumulated.ToString());
                if (!string.IsNullOrWhiteSpace(full)) LastScreenContext = full;
                res.Dispose();
            }
        }

        /// <summary>
        /// Sends the screenshot and prompt to the secured backend (server-side Groq/OpenAI
        /// keys, credits deducted server-side) — mirrors the /ask flow, no personal API key
        /// ever required or accepted from the user. Catches all exceptions and returns null
        /// on failure. Returns a (response, errorMessage) tuple — error is "" on success.
        /// </summary>
        private static async Task<(HttpResponseMessage? Response, string Error)>
            SendVisionRequestSafeAsync(string payloadJson, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post,
                    $"{SettingsWindow.GetBackendUrl()}/api/v1/interview/analyze-screen");
                if (!string.IsNullOrEmpty(UserSession.IdToken))
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {UserSession.IdToken}");
                req.Headers.TryAddWithoutValidation("X-Device-Id", DeviceIdentity.Current);
                req.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                var response = await SharedHttpClient.Http.SendAsync(
                    req, HttpCompletionOption.ResponseHeadersRead, ct);
                return (response, "");
            }
            catch (TaskCanceledException)
            {
                return (null, "⚠ Request timed out. Check your connection and try again.");
            }
            catch (Exception ex)
            {
                return (null, $"⚠ Network error: {ex.Message}");
            }
        }

        /// <summary>Maps a non-200 status from the backend to a friendly message.</summary>
        private static string DescribeVisionError(System.Net.HttpStatusCode status) => status switch
        {
            System.Net.HttpStatusCode.Unauthorized => "⚠ Please sign in to use Screen AI.",
            System.Net.HttpStatusCode.PaymentRequired =>
                "⚠ Insufficient credits.\n\nUpgrade your Replysis AI plan to continue using Screen AI.",
            System.Net.HttpStatusCode.BadRequest => "⚠ Could not analyze this screenshot. Please try again.",
            _ => "⚠ Screen AI is temporarily unavailable. Please try again."
        };

        /// <summary>Parses one SSE data line; returns "" on any error (never throws).</summary>
        private static string ParseSseToken(string data)
        {
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("error", out var errProp))
                    throw new InvalidOperationException(errProp.GetString()
                        ?? "Screen AI could not complete this request.");
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0) return "";
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var cp))
                    return cp.GetString() ?? "";
                return "";
            }
            catch (JsonException) { return ""; }
        }

        // ═════════════════════════════════════════════════════════════════════
        // NON-STREAMING WRAPPER (backward compat — collects all tokens)
        // ═════════════════════════════════════════════════════════════════════

        public static async Task<string> AnalyzeAsync(byte[] imageBytes, string? resumeContext = null)
        {
            var sb = new StringBuilder();
            await foreach (var token in AnalyzeStreamAsync(imageBytes, resumeContext))
                sb.Append(token);
            return sb.ToString();
        }

        // ═════════════════════════════════════════════════════════════════════
        // HELPERS
        // ═════════════════════════════════════════════════════════════════════

        private static string CleanContent(string content)
        {
            if (string.IsNullOrEmpty(content)) return content;
            content = RxBold.Replace(content, "$1");
            content = RxItalic.Replace(content, "$1");
            content = RxUnder.Replace(content, "$1");
            content = RxHeading.Replace(content, "");
            content = content.Replace("\r\n", "\n").Replace("\r", "\n");
            content = RxBlankRun.Replace(content, "\n\n");
            return content.Trim();
        }
    }
}
