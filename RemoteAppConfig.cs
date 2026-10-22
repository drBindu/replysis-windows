using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InterviewCopilot
{
    /// <summary>
    /// Settings read from https://replysis.com/app-config.json, so a fix can reach every installed copy without
    /// an update (owner 2026-10-21: "fix without giving again and again update"). Read when the speech engine
    /// starts, at most every five minutes. Anything missing, unreachable or malformed keeps the built-in value,
    /// so the file can only change behaviour, never break it.
    /// </summary>
    internal static class RemoteAppConfig
    {
        private const string Url = "https://replysis.com/app-config.json";

        /// <summary>Capture sound through Windows itself (NativeAudioFeed). Off means the engine's old capture.</summary>
        internal static volatile bool NativeAudio = true;

        /// <summary>
        /// Engine tuning numbers from "windows.engine" in the file, passed to the speech engine as environment variables
        /// (REPLYSIS_*), so a threshold can be corrected for everyone without an update (2026-10-22). Only names that
        /// start with REPLYSIS_ and whole numbers are passed; the engine clamps each to a safe range.
        /// </summary>
        internal static volatile System.Collections.Generic.Dictionary<string, string> EngineSettings = new();

        private static DateTime _fetchedUtc = DateTime.MinValue;
        private static readonly SemaphoreSlim _gate = new(1, 1);

        internal static async Task RefreshAsync()
        {
            if (DateTime.UtcNow - _fetchedUtc < TimeSpan.FromMinutes(5)) return;
            if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                string json = await SharedHttpClient.HttpShort.GetStringAsync(Url, cts.Token).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("windows", out JsonElement win) &&
                    win.TryGetProperty("nativeAudio", out JsonElement native) &&
                    (native.ValueKind == JsonValueKind.True || native.ValueKind == JsonValueKind.False))
                    NativeAudio = native.GetBoolean();
                if (doc.RootElement.TryGetProperty("windows", out JsonElement w2) &&
                    w2.TryGetProperty("engine", out JsonElement eng) && eng.ValueKind == JsonValueKind.Object)
                {
                    var map = new System.Collections.Generic.Dictionary<string, string>();
                    foreach (JsonProperty p in eng.EnumerateObject())
                        if (p.Name.StartsWith("REPLYSIS_", StringComparison.Ordinal) && p.Value.ValueKind == JsonValueKind.Number &&
                            p.Value.TryGetInt32(out int n))
                            map[p.Name] = n.ToString();
                    EngineSettings = map;
                }
                DebugWindow.Log("CONFIG", $"Remote settings: nativeAudio={NativeAudio}");
            }
            catch (Exception ex)
            {
                DebugWindow.Log("CONFIG", $"Remote settings unavailable ({ex.GetType().Name}); keeping nativeAudio={NativeAudio}");
            }
            finally
            {
                _fetchedUtc = DateTime.UtcNow;   // a failure is remembered too, so it is not retried on every engine start
                _gate.Release();
            }
        }
    }
}
