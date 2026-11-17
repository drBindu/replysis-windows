using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InterviewCopilot
{
    /// <summary>One line of the activity diary: what happened, when, with a few fixed labels.</summary>
    internal sealed class DiaryEvent
    {
        public long T { get; set; }
        public string SessionId { get; set; } = "";
        public string V { get; set; } = "";
        public string Event { get; set; } = "";
        public Dictionary<string, object> Props { get; set; } = new();
    }

    /// <summary>
    /// The activity diary (owner, 2026-11-14): which buttons, menus and steps a person went through, so the problems
    /// users hit can be found and fixed. The Mac app keeps the same diary. In the owner's words it is "only to see
    /// problems or issues users face and fix them; we never take users' data or passwords".
    ///
    /// Only names, numbers, true/false and short fixed labels are ever recorded. Never anything anyone said, never an
    /// answer, never what is on the screen, never a resume or job text, never a password, a key press, a file name or
    /// another app's window title. The rule is enforced here, in the one place every event passes through: a label with
    /// a space in it, or longer than 40 characters, is dropped before it is stored and again before it is sent, so free
    /// text cannot reach the server even if a caller passes it by mistake.
    ///
    /// Events wait in a small file in the app's data folder (at most 5,000, oldest dropped) and go to
    /// POST /api/v1/activity every 10 seconds when there are any, and once more when the app closes (never longer than
    /// 1.5 s). Sending runs on a background task only: never on the answer path, never on the window's thread. Only
    /// while someone is signed in, with the same sign-in token and app headers as the presence ping.
    /// </summary>
    internal static class ActivityDiary
    {
        internal const int MaxQueued = 5000;
        internal const int MaxBatch = 200;
        internal const int MaxValueLength = 40;
        internal const int MaxProps = 12;
        /// <summary>A runaway loop must not fill the file in one run.</summary>
        internal const int MaxPerRun = 6000;
        internal const string FileName = "activity-diary.json";
        internal const string MarkerName = "running.flag";
        internal static readonly TimeSpan SendEvery = TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan QuitCap = TimeSpan.FromMilliseconds(1500);

        /// <summary>One random id per launch of the app.</summary>
        internal static readonly string SessionId = Guid.NewGuid().ToString("N");

        /// <summary>The app version, set once at start.</summary>
        internal static string Version = "";

        private static readonly object _lock = new();
        private static readonly List<DiaryEvent> _queue = new();
        private static bool _dirty;
        private static int _trackedThisRun;
        private static string? _folder;
        private static CancellationTokenSource? _loop;
        private static Task? _loaded;
        private static readonly SemaphoreSlim _sending = new(1, 1);
        private static int _batchSize = MaxBatch;
        private static DateTime _lastFailureLogUtc = DateTime.MinValue;
        /// <summary>
        /// After the sign-in is refused (a revoked account, or the server refusing a fresh token) the diary stays quiet for
        /// a while, instead of asking the token service twice every 10 s for as long as the app is open (review 2026-11-16).
        /// </summary>
        private static DateTime _pauseUntilUtc = DateTime.MinValue;
        internal static readonly TimeSpan RefusedPause = TimeSpan.FromMinutes(10);
        private static readonly Dictionary<string, DateTime> _lastByKey = new();

        private static readonly Regex SafeName = new("^[a-z][a-z0-9_]{0,39}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // Letters, digits and . _ - : + only. No space, no slash, no @, so an address, a path or a sentence never fits.
        private static readonly Regex SafeLabel = new("^[A-Za-z0-9._:+-]{1,40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // ── The privacy rule ─────────────────────────────────────────────────────────────────────────────

        /// <summary>An event or property name: lower case, digits and underscores, at most 40.</summary>
        internal static bool IsSafeName(string? name) => name != null && SafeName.IsMatch(name);

        /// <summary>
        /// Whether a property value may be recorded: true/false, a whole or finite number, or a short fixed label with
        /// no spaces and at most 40 characters. Everything else (free text, a sentence, a long string) is refused.
        /// </summary>
        internal static bool IsSafeValue(object? value) => value switch
        {
            null => false,
            bool => true,
            int or long or short or byte or uint or ushort or sbyte => true,
            ulong u => u <= long.MaxValue,
            double d => double.IsFinite(d),
            float f => float.IsFinite(f),
            decimal => true,
            string s => SafeLabel.IsMatch(s),
            _ => false,
        };

        /// <summary>The properties that pass the rule; the rest are dropped.</summary>
        internal static Dictionary<string, object> CleanProps(IEnumerable<KeyValuePair<string, object?>>? props)
        {
            var clean = new Dictionary<string, object>(StringComparer.Ordinal);
            if (props == null) return clean;
            foreach (var (key, value) in props)
            {
                if (clean.Count >= MaxProps) break;
                if (!IsSafeName(key) || !IsSafeValue(value)) continue;
                clean[key] = value is float f ? (double)f : value!;
            }
            return clean;
        }

        /// <summary>
        /// A problem or notice kind as a short label: lower case, anything other than a-z 0-9 . _ - becomes "-", at most
        /// 40 characters. A fatal engine line is only "engine-fatal": its text comes from the engine and could carry a
        /// device name.
        /// </summary>
        internal static string Kind(string? raw)
        {
            string k = (raw ?? "").Trim().ToLowerInvariant();
            if (k.StartsWith("engine-fatal", StringComparison.Ordinal)) return "engine-fatal";
            var sb = new StringBuilder(k.Length);
            foreach (char c in k)
                sb.Append((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-' ? c : '-');
            string s = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
            if (s.Length > MaxValueLength) s = s[..MaxValueLength].TrimEnd('-');
            return s.Length == 0 ? "unknown" : s;
        }

        // ── Recording ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Builds the event, or null when its name breaks the rule. Properties that break it are dropped.</summary>
        internal static DiaryEvent? Make(string name, IEnumerable<KeyValuePair<string, object?>>? props, DateTime utcNow)
        {
            if (!IsSafeName(name)) return null;
            return new DiaryEvent
            {
                T = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                SessionId = SessionId,
                V = Version,
                Event = name,
                Props = CleanProps(props),
            };
        }

        /// <summary>Notes that something happened. Cheap (a list add under a lock), never throws, never waits on anything.</summary>
        internal static void Track(string name, params (string Key, object? Value)[] props)
        {
            try
            {
                var list = new List<KeyValuePair<string, object?>>(props?.Length ?? 0);
                if (props != null) foreach (var (k, v) in props) list.Add(new(k, v));
                DiaryEvent? e = Make(name, list, DateTime.UtcNow);
                if (e == null) return;
                Enqueue(e);
#if DEBUG
                DebugWindow.Log("DIARY", e.Event);
#endif
            }
            catch { /* the diary must never be the reason anything fails */ }
        }

        /// <summary>The same as Track, at most once per <paramref name="every"/> for one name and key (a repeating notice).</summary>
        internal static void TrackThrottled(TimeSpan every, string name, string throttleKey, params (string Key, object? Value)[] props)
        {
            try
            {
                string key = name + "|" + throttleKey;
                lock (_lastByKey)
                {
                    if (_lastByKey.TryGetValue(key, out var last) && DateTime.UtcNow - last < every) return;
                    _lastByKey[key] = DateTime.UtcNow;
                    if (_lastByKey.Count > 500) _lastByKey.Clear();
                }
                Track(name, props);
            }
            catch { }
        }

        /// <summary>A problem the app reported (the same kinds as /api/v1/diagnostics/client-error), once a minute per kind.</summary>
        internal static void ProblemShown(string? kind)
        {
            string k = Kind(kind);
            TrackThrottled(TimeSpan.FromMinutes(1), "problem_shown", k, ("kind", k));
        }

        /// <summary>A notice the person was shown, once a minute per kind.</summary>
        internal static void NoticeShown(string? kind)
        {
            string k = Kind(kind);
            TrackThrottled(TimeSpan.FromMinutes(1), "notice_shown", k, ("kind", k));
        }

        internal static void Enqueue(DiaryEvent e)
        {
            lock (_lock)
            {
                if (_trackedThisRun >= MaxPerRun) return;
                _trackedThisRun++;
                _queue.Add(e);
                if (_queue.Count > MaxQueued) _queue.RemoveRange(0, _queue.Count - MaxQueued);   // the oldest go first
                _dirty = true;
            }
        }

        internal static int QueuedCount { get { lock (_lock) return _queue.Count; } }

        internal static List<DiaryEvent> Snapshot() { lock (_lock) return new List<DiaryEvent>(_queue); }

        /// <summary>Tests only: empties the queue and the counters.</summary>
        internal static void ResetForTest()
        {
            lock (_lock) { _queue.Clear(); _trackedThisRun = 0; _dirty = false; _batchSize = MaxBatch; }
            lock (_lastByKey) _lastByKey.Clear();
        }

        // ── The wire format ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The request body for these events: {"events":[{"t","session_id","app","v","event","props"}]}, at most 200. The
        /// rule is applied again here, so an event read back from a damaged or edited file cannot carry free text out.
        /// </summary>
        internal static string BuildBody(IReadOnlyList<DiaryEvent> events)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteStartArray("events");
                int n = 0;
                foreach (var e in events)
                {
                    if (n >= MaxBatch) break;
                    if (!IsSafeName(e.Event)) continue;
                    n++;
                    w.WriteStartObject();
                    w.WriteNumber("t", e.T);
                    w.WriteString("session_id", SafeLabel.IsMatch(e.SessionId ?? "") ? e.SessionId : SessionId);
                    w.WriteString("app", "windows");
                    string v = string.IsNullOrEmpty(e.V) ? Version : e.V;
                    w.WriteString("v", SafeLabel.IsMatch(v ?? "") ? v : "0.0.0");
                    w.WriteString("event", e.Event);
                    w.WriteStartObject("props");
                    foreach (var (key, value) in CleanProps(ToNullable(e.Props)))
                    {
                        switch (value)
                        {
                            case bool b: w.WriteBoolean(key, b); break;
                            case string s: w.WriteString(key, s); break;
                            case double d: w.WriteNumber(key, d); break;
                            case decimal m: w.WriteNumber(key, m); break;
                            case ulong u: w.WriteNumber(key, u); break;
                            default: w.WriteNumber(key, Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
                        }
                    }
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static IEnumerable<KeyValuePair<string, object?>> ToNullable(Dictionary<string, object>? props)
        {
            if (props == null) yield break;
            foreach (var (k, v) in props) yield return new(k, v);
        }

        internal static HttpRequestMessage BuildRequest(string backendUrl, string token, string body)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, backendUrl.TrimEnd('/') + "/api/v1/activity");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return req;
        }

        internal enum Outcome { Stored, Unauthorized, Discard, TooLarge, Retry }

        /// <summary>
        /// What a reply means. 2xx (204) stored; 401 refresh the token once and try again; 413 send smaller batches;
        /// 400 and 422 are a batch the server will never accept, dropped so it cannot block the queue for ever; 404,
        /// 429, 5xx and everything else are kept and sent later.
        /// </summary>
        internal static Outcome Classify(int status) => status switch
        {
            >= 200 and < 300 => Outcome.Stored,
            401 => Outcome.Unauthorized,
            413 => Outcome.TooLarge,
            400 or 422 => Outcome.Discard,
            _ => Outcome.Retry,
        };

        // ── The file ─────────────────────────────────────────────────────────────────────────────────────

        internal static void SaveTo(string folder, IReadOnlyList<DiaryEvent> events)
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, FileName);
            string tmp = path + ".tmp";
            var list = new List<object>(events.Count);
            foreach (var e in events)
                list.Add(new Dictionary<string, object>
                {
                    ["t"] = e.T, ["session_id"] = e.SessionId, ["v"] = e.V, ["event"] = e.Event, ["props"] = e.Props,
                });
            File.WriteAllText(tmp, JsonSerializer.Serialize(list));
            File.Move(tmp, path, overwrite: true);
        }

        /// <summary>The events kept in the file, checked against the rule again. A damaged file gives an empty list.</summary>
        internal static List<DiaryEvent> LoadFrom(string folder)
        {
            var result = new List<DiaryEvent>();
            try
            {
                string path = Path.Combine(folder, FileName);
                if (!File.Exists(path)) return result;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string name = item.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.String ? ev.GetString() ?? "" : "";
                    if (!IsSafeName(name)) continue;
                    var props = new List<KeyValuePair<string, object?>>();
                    if (item.TryGetProperty("props", out var p) && p.ValueKind == JsonValueKind.Object)
                        foreach (var prop in p.EnumerateObject())
                            props.Add(new(prop.Name, prop.Value.ValueKind switch
                            {
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                JsonValueKind.Number => prop.Value.TryGetInt64(out long l) ? l : prop.Value.GetDouble(),
                                JsonValueKind.String => prop.Value.GetString(),
                                _ => null,
                            }));
                    result.Add(new DiaryEvent
                    {
                        T = item.TryGetProperty("t", out var t) && t.TryGetInt64(out long tl) ? tl : 0,
                        SessionId = item.TryGetProperty("session_id", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "",
                        V = item.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "",
                        Event = name,
                        Props = CleanProps(props),
                    });
                }
                if (result.Count > MaxQueued) result.RemoveRange(0, result.Count - MaxQueued);
            }
            catch { result.Clear(); }
            return result;
        }

        private static void SaveIfDirty()
        {
            try
            {
                string? folder = _folder;
                if (folder == null) return;
                // Never write the file before last run's events are read back in, or they would be overwritten.
                try { if (_loaded != null && !_loaded.Wait(TimeSpan.FromMilliseconds(300))) return; } catch { }
                List<DiaryEvent> copy;
                lock (_lock)
                {
                    if (!_dirty) return;
                    _dirty = false;
                    copy = new List<DiaryEvent>(_queue);
                }
                SaveTo(folder, copy);
            }
            catch { }
        }

        // ── Launch, crash marker and the sender ──────────────────────────────────────────────────────────

        /// <summary>
        /// Called once at launch by the copy that owns the app. Reads what was left unsent, notes whether the last run
        /// ended without closing properly, writes this run's marker, and starts the background sender.
        /// </summary>
        internal static void Start(string folder)
        {
            if (_loop != null) return;
            try
            {
                _folder = folder;
                try { Version = AppUpdates.CurrentVersion; } catch { Version = "0.0.0"; }

                if (TakeRunningMarker(folder)) Track("app_crashed_previous_run");
                WriteRunningMarker(folder);

                Track("app_opened",
                    ("os", Environment.OSVersion.Version.ToString()),
                    ("cpu_arch", System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()));

                _loop = new CancellationTokenSource();
                var ct = _loop.Token;
                // What the last run left unsent goes in front of this run's events, read off the window's thread.
                _loaded = Task.Run(() =>
                {
                    var saved = LoadFrom(folder);
                    lock (_lock)
                    {
                        _queue.InsertRange(0, saved);
                        if (_queue.Count > MaxQueued) _queue.RemoveRange(0, _queue.Count - MaxQueued);
                        if (saved.Count > 0) _dirty = true;
                    }
                });
                _ = Task.Run(async () =>
                {
                    try { await _loaded.ConfigureAwait(false); } catch { }
                    await LoopAsync(ct).ConfigureAwait(false);
                });
            }
            catch { }
        }

        /// <summary>True when the last run left its marker behind, which means it never reached a clean exit. Removes it.</summary>
        internal static bool TakeRunningMarker(string folder)
        {
            try
            {
                string path = Path.Combine(folder, MarkerName);
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch { return false; }
        }

        internal static void WriteRunningMarker(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, MarkerName), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            }
            catch { }
        }

        private static async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(SendEvery, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                try
                {
                    if (QueuedCount > 0) await SendPendingAsync(null, maxBatches: 5, ct).ConfigureAwait(false);
                }
                catch { }
                SaveIfDirty();
            }
        }

        private static bool SendingAllowed()
        {
#if DEBUG
            // A developer build does not write to the live diary unless asked to (the same as problem reports).
            return Environment.GetEnvironmentVariable("REPLYSIS_DIARY") == "1";
#else
            return true;
#endif
        }

        /// <summary>
        /// Sends what is waiting, oldest first, in batches of at most 200. <paramref name="tokenOverride"/> is for the
        /// moment of signing out, when the session is about to be cleared. One send at a time.
        /// </summary>
        internal static async Task SendPendingAsync(string? tokenOverride, int maxBatches, CancellationToken ct)
        {
            if (!SendingAllowed()) return;
            if (tokenOverride == null && DateTime.UtcNow < _pauseUntilUtc) return;
            // Signing out waits its turn (up to 5 s): returning at once while the 10 s loop was mid-send left this
            // account's events to go out under the next sign-in.
            bool got;
            try { got = await _sending.WaitAsync(tokenOverride != null ? TimeSpan.FromSeconds(5) : TimeSpan.Zero, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (!got) return;
            try
            {
                for (int round = 0; round < maxBatches && !ct.IsCancellationRequested; round++)
                {
                    string token = tokenOverride ?? "";
                    if (tokenOverride == null)
                    {
                        if (!UserSession.IsLoggedIn || UserSession.IsGuestSession) return;
                        if (UserSession.IsTokenExpired() && !await UserSession.TryRefreshAsync().ConfigureAwait(false))
                        {
                            _pauseUntilUtc = DateTime.UtcNow + RefusedPause;
                            LogFailure("sign-in could not be refreshed; paused 10 minutes");
                            return;
                        }
                        token = UserSession.IdToken;
                    }
                    if (string.IsNullOrEmpty(token)) return;

                    List<DiaryEvent> batch;
                    lock (_lock)
                    {
                        if (_queue.Count == 0) return;
                        batch = _queue.GetRange(0, Math.Min(_batchSize, _queue.Count));
                    }
                    string body = BuildBody(batch);
                    string backend = SettingsWindow.GetBackendUrl();

                    Outcome outcome = await PostAsync(backend, token, body, ct).ConfigureAwait(false);
                    if (outcome == Outcome.Unauthorized && tokenOverride == null)
                    {
                        // Once: a token the server refuses after a refresh is not fixed by a second.
                        if (await UserSession.TryRefreshAsync(force: true).ConfigureAwait(false))
                            outcome = await PostAsync(backend, UserSession.IdToken, body, ct).ConfigureAwait(false);
                        if (outcome == Outcome.Unauthorized)
                        {
                            _pauseUntilUtc = DateTime.UtcNow + RefusedPause;
                            LogFailure("sign-in refused after a refresh; paused 10 minutes");
                            return;
                        }
                    }

                    switch (outcome)
                    {
                        case Outcome.Stored:
                        case Outcome.Discard:
                            RemoveSent(batch);
                            if (outcome == Outcome.Discard) LogFailure("a batch was refused as malformed and dropped");
                            break;
                        case Outcome.TooLarge:
                            lock (_lock) _batchSize = Math.Max(10, _batchSize / 2);
                            return;
                        default:
                            return;   // kept, tried again in 10 seconds
                    }
                }
            }
            finally { _sending.Release(); }
        }

        private static async Task<Outcome> PostAsync(string backend, string token, string body, CancellationToken ct)
        {
            try
            {
                using var req = BuildRequest(backend, token, body);
                using var res = await SharedHttpClient.HttpShort.SendAsync(req, ct).ConfigureAwait(false);
                Outcome o = Classify((int)res.StatusCode);
                if (o == Outcome.Retry) LogFailure($"HTTP {(int)res.StatusCode}; kept for later");
                return o;
            }
            catch (Exception ex)
            {
                LogFailure($"{ex.GetType().Name}; kept for later");
                return Outcome.Retry;
            }
        }

        private static void RemoveSent(List<DiaryEvent> batch)
        {
            var sent = new HashSet<DiaryEvent>(batch, ReferenceEqualityComparer.Instance);
            lock (_lock)
            {
                _queue.RemoveAll(e => sent.Contains(e));
                _dirty = true;
            }
        }

        private static void LogFailure(string what)
        {
            // Once every ten minutes at most: the endpoint may not exist yet, and the log must not fill with it.
            if (DateTime.UtcNow - _lastFailureLogUtc < TimeSpan.FromMinutes(10)) return;
            _lastFailureLogUtc = DateTime.UtcNow;
            DebugWindow.Log("DIARY", "send: " + what);
        }

        /// <summary>
        /// Signing out: noted, then sent in the background with the token that is about to be cleared, so this account's
        /// diary is not sent later under the next person's sign-in. Never waits.
        /// </summary>
        internal static void SignedOut(string why)
        {
            try
            {
                Track("signed_out", ("why", why));
                string token = UserSession.IdToken;
                // Everything up to now belongs to this account. Whatever is not sent with its token is dropped, never
                // sent later under the next person's sign-in (review 2026-11-16).
                HashSet<DiaryEvent> mine;
                lock (_lock) mine = new HashSet<DiaryEvent>(_queue, ReferenceEqualityComparer.Instance);
                _pauseUntilUtc = DateTime.MinValue;   // the next sign-in starts fresh
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(token))
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                            await SendPendingAsync(token, maxBatches: 3, cts.Token).ConfigureAwait(false);
                        }
                    }
                    catch { }
                    try
                    {
                        lock (_lock)
                        {
                            if (_queue.RemoveAll(e => mine.Contains(e)) > 0) _dirty = true;
                        }
                    }
                    catch { }
                    SaveIfDirty();
                });
            }
            catch { }
        }

        /// <summary>
        /// The app is closing: notes why, sends what it can within 1.5 s, saves the rest for next time, and removes this
        /// run's marker (a clean exit). Never throws, never waits longer than the cap.
        /// </summary>
        internal static void Quit(string reason)
        {
            try
            {
                Track("app_closed", ("reason", reason));
                try { _loop?.Cancel(); } catch { }
                using var cts = new CancellationTokenSource(QuitCap);
                try
                {
                    Task.Run(() => SendPendingAsync(null, maxBatches: 3, cts.Token)).Wait(QuitCap);
                }
                catch { }
                lock (_lock) _dirty = true;
                SaveIfDirty();
            }
            catch { }
            finally
            {
                try { if (_folder != null) File.Delete(Path.Combine(_folder, MarkerName)); } catch { }
            }
        }
    }
}
