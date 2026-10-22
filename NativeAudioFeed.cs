using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace InterviewCopilot
{
    /// <summary>
    /// Captures one sound source the way Zoom and Teams do: straight from Windows (WASAPI), on the device
    /// Windows says is the default, and hands it to the speech engine as 16 kHz mono 16-bit through a named
    /// pipe (REPLYSIS_MIC_PIPE / REPLYSIS_SYS_PIPE).
    ///
    /// Why it exists (2026-10-21): the engine used to find the mic and speakers itself through PortAudio, by
    /// walking a list of devices that is different on every laptop. On a customer's laptop every speaker
    /// failed to open (-9996) and the search had nothing left to try. Windows already knows which mic and
    /// speakers the person is using; asking it removes the guessing.
    ///
    /// It follows the default: a headset plugged in, Bluetooth connecting, or a change in Windows sound
    /// settings moves the capture within two seconds, while the engine keeps the same pipe. If anything here
    /// fails at start, TryStart returns null and the engine uses its old capture, so this can never leave a
    /// user with less than before.
    /// </summary>
    internal sealed class NativeAudioFeed : IDisposable
    {
        private const int OutRate = 16000;

        private readonly bool _loopback;
        private readonly string? _preferredName;
        private readonly string _label;
        private readonly NamedPipeServerStream _pipe;
        private readonly object _lock = new();
        private readonly Timer _watch;

        private volatile bool _connected;
        private volatile bool _pipeBroken;
        private volatile bool _disposed;
        private volatile bool _captureStopped;

        private IWaveIn? _capture;
        private MMDevice? _device;
        private string? _deviceId;
        private WaveFormat? _format;
        private bool _isFloat;
        private DateTime _lastDataUtc = DateTime.UtcNow;
        private DateTime _lastRestartUtc = DateTime.MinValue;

        // Resampler state, carried across buffers so no sample is lost or doubled at a buffer edge.
        private readonly List<float> _src = new();
        private double _pos;
        private byte[] _out = new byte[8192];

        internal string PipePath { get; }
        internal string DeviceName { get; private set; } = "";

        private NativeAudioFeed(bool loopback, string? preferredName)
        {
            _loopback = loopback;
            _preferredName = preferredName;
            _label = loopback ? "SYSTEM AUDIO" : "MIC";
            string name = $"replysis-{(loopback ? "sys" : "mic")}-{Environment.ProcessId}-{Guid.NewGuid():N}";
            PipePath = @"\\.\pipe\" + name;
            _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                                              PipeOptions.Asynchronous, 0, 1 << 16);
            _ = WaitForEngineAsync();

            try
            {
                StartCapture();   // throws when the device cannot be opened; TryStart turns that into "use the old way"
            }
            catch
            {
                try { _pipe.Dispose(); } catch { }
                throw;
            }
            _watch = new Timer(_ => Watch(), null, 2000, 2000);
        }

        /// <summary>The feed, or null when Windows would not give us the device (the engine then captures as before).</summary>
        internal static NativeAudioFeed? TryStart(bool loopback, string? preferredName)
        {
            try
            {
                return new NativeAudioFeed(loopback, preferredName);
            }
            catch (Exception ex)
            {
                Note($">>> NATIVE {(loopback ? "SYSTEM AUDIO" : "MIC")}: could not open ({ex.GetType().Name} 0x{ex.HResult:X8}); " +
                     "the engine uses its old capture.");
                return null;
            }
        }

        private async System.Threading.Tasks.Task WaitForEngineAsync()
        {
            try
            {
                await _pipe.WaitForConnectionAsync().ConfigureAwait(false);
                _connected = true;
            }
            catch { }
        }

        private static void Note(string line)
        {
            DebugWindow.Log("AUDIO", line);
            ClientErrorReporter.NoteEngineLine(line);
        }

        // ── Which device ────────────────────────────────────────────────────────────────────────────

        private static readonly string[] VirtualWords =
            { "cable", "stereo mix", "voicemeeter", "virtual", "wave out mix", "what u hear" };

        // Capture devices that record the computer's own sound, never a voice. Stereo Mix was enabled on a laptop with
        // no microphone, Windows made it the default input, and Replysis listened to it while the person spoke
        // (2026-10-22). Not "virtual" in general: a SteelSeries Sonar virtual mic is a real mic routed through software.
        private static readonly string[] NotAMicWords =
            { "stereo mix", "what u hear", "what you hear", "wave out mix", "wave out", "cable output", "voicemeeter out" };

        internal static bool IsNotAMic(string name)
        {
            foreach (string w in NotAMicWords)
                if (name.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Active capture devices that can hear a voice (Stereo Mix and the like are not counted).</summary>
        internal static uint CountRealMics()
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                uint n = 0;
                foreach (MMDevice d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    if (!IsNotAMic(d.FriendlyName ?? "")) n++;
                return n;
            }
            catch { return 0; }
        }

        private static bool LooksVirtual(string name)
        {
            foreach (string w in VirtualWords)
                if (name.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private MMDevice PickDevice(MMDeviceEnumerator en)
        {
            DataFlow flow = _loopback ? DataFlow.Render : DataFlow.Capture;

            // A microphone the person chose in Settings is honoured while it is plugged in, unless it is a
            // virtual device: those carry silence unless another app feeds them, which is how a laptop ended
            // up listening to an empty cable.
            if (!_loopback && !string.IsNullOrWhiteSpace(_preferredName) && !LooksVirtual(_preferredName!))
            {
                string want = _preferredName!.Trim();
                foreach (MMDevice d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    string have = d.FriendlyName ?? "";
                    if (have.StartsWith(want, StringComparison.OrdinalIgnoreCase) ||
                        want.StartsWith(have, StringComparison.OrdinalIgnoreCase))
                        return d;
                }
            }

            // The Windows default, as set in Sound settings. For the mic, never an input that only records the
            // computer's sound.
            foreach (Role role in new[] { Role.Console, Role.Communications, Role.Multimedia })
            {
                try
                {
                    if (!en.HasDefaultAudioEndpoint(flow, role)) continue;
                    MMDevice d = en.GetDefaultAudioEndpoint(flow, role);
                    if (_loopback || !IsNotAMic(d.FriendlyName ?? "")) return d;
                }
                catch { }
            }

            // No usable default: any working device that is not virtual, then anything that can be a mic.
            MMDevice? any = null;
            foreach (MMDevice d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                string n = d.FriendlyName ?? "";
                if (!_loopback && IsNotAMic(n)) continue;
                if (!LooksVirtual(n)) return d;
                any ??= d;
            }
            return any ?? throw new InvalidOperationException("no active " + (_loopback ? "speaker" : "microphone"));
        }

        private string? CurrentDefaultId()
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                return PickDevice(en).ID;
            }
            catch { return null; }
        }

        // ── Capture ─────────────────────────────────────────────────────────────────────────────────

        private void StartCapture()
        {
            lock (_lock)
            {
                StopCaptureLocked();
                using var en = new MMDeviceEnumerator();
                MMDevice device = PickDevice(en);

                IWaveIn capture = _loopback
                    ? new WasapiLoopbackCapture(device)
                    : new WasapiCapture(device, true, 20);
                WaveFormat fmt = capture.WaveFormat;

                _isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (fmt is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
                if (!_isFloat && fmt.BitsPerSample != 16 && fmt.BitsPerSample != 24 && fmt.BitsPerSample != 32)
                {
                    capture.Dispose();
                    throw new NotSupportedException($"sample format {fmt.Encoding} {fmt.BitsPerSample} bit");
                }

                capture.DataAvailable += OnData;
                capture.RecordingStopped += OnStopped;
                _src.Clear();
                _pos = 0;
                _format = fmt;
                _device = device;
                _deviceId = device.ID;
                DeviceName = device.FriendlyName ?? "";
                _captureStopped = false;
                _lastDataUtc = DateTime.UtcNow;
                _lastRestartUtc = DateTime.UtcNow;
                capture.StartRecording();
                _capture = capture;
                Note($">>> NATIVE {_label}: listening on \"{DeviceName}\" ({fmt.SampleRate}Hz {fmt.Channels}ch).");
            }
        }

        private void StopCaptureLocked()
        {
            IWaveIn? old = _capture;
            _capture = null;
            if (old != null)
            {
                old.DataAvailable -= OnData;
                old.RecordingStopped -= OnStopped;
                try { old.StopRecording(); } catch { }
                try { old.Dispose(); } catch { }
            }
            try { _device?.Dispose(); } catch { }
            _device = null;
        }

        private void OnStopped(object? sender, StoppedEventArgs e)
        {
            if (_disposed) return;
            _captureStopped = true;
            if (e.Exception != null)
                Note($">>> NATIVE {_label}: the device stopped ({e.Exception.GetType().Name} 0x{e.Exception.HResult:X8}); reopening.");
        }

        /// <summary>Every two seconds: follow a new Windows default, and reopen a device that stopped.</summary>
        private void Watch()
        {
            if (_disposed) return;
            try
            {
                string? want = CurrentDefaultId();
                bool moved = want != null && want != _deviceId;
                // A microphone that delivers nothing for five seconds has died without saying so (a USB headset
                // on some drivers). A loopback is quiet whenever nothing plays, so it is never judged by this.
                bool silentDeath = !_loopback && DateTime.UtcNow - _lastDataUtc > TimeSpan.FromSeconds(5);
                bool backoffOver = DateTime.UtcNow - _lastRestartUtc > TimeSpan.FromSeconds(5);

                if ((moved || _captureStopped || silentDeath) && backoffOver)
                {
                    if (moved) Note($">>> NATIVE {_label}: Windows default changed; moving to it.");
                    else if (silentDeath) Note($">>> NATIVE {_label}: no sound packets for 5 s; reopening the device.");
                    try { StartCapture(); }
                    catch (Exception ex)
                    {
                        _lastRestartUtc = DateTime.UtcNow;
                        Note($">>> NATIVE {_label}: reopen failed ({ex.GetType().Name} 0x{ex.HResult:X8}); trying again shortly.");
                    }
                }
            }
            catch { }
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            if (_disposed || e.BytesRecorded <= 0) return;
            _lastDataUtc = DateTime.UtcNow;
            if (!_connected || _pipeBroken) return;

            int count;
            lock (_lock)
            {
                if (_format == null || !ReferenceEquals(sender, _capture)) return;
                count = Convert(e.Buffer, e.BytesRecorded, _format);
                if (count <= 0) return;
                try
                {
                    _pipe.Write(_out, 0, count);
                }
                catch (Exception)
                {
                    // The engine has gone; it is restarted with a fresh feed.
                    _pipeBroken = true;
                }
            }
        }

        /// <summary>Device format to 16 kHz mono s16 in _out. Returns the byte count.</summary>
        private int Convert(byte[] buf, int bytes, WaveFormat fmt)
        {
            int ch = Math.Max(1, fmt.Channels);
            int bps = fmt.BitsPerSample / 8;
            int frames = bytes / (bps * ch);

            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                int baseOff = f * bps * ch;
                for (int c = 0; c < ch; c++)
                {
                    int o = baseOff + c * bps;
                    float v;
                    if (_isFloat) v = BitConverter.ToSingle(buf, o);
                    else if (bps == 2) v = BitConverter.ToInt16(buf, o) / 32768f;
                    else if (bps == 3) v = ((buf[o] << 8 | buf[o + 1] << 16 | buf[o + 2] << 24) >> 8) / 8388608f;
                    else v = BitConverter.ToInt32(buf, o) / 2147483648f;
                    sum += v;
                }
                _src.Add(sum / ch);
            }

            // Average over each output sample's span of input: a cheap low-pass, enough for speech.
            double step = (double)fmt.SampleRate / OutRate;
            int needed = (int)(_src.Count / step) * 2 + 4;
            if (_out.Length < needed) _out = new byte[needed * 2];
            int n = 0;
            while (_pos + step <= _src.Count)
            {
                int a = (int)_pos;
                int b = Math.Max(a + 1, (int)(_pos + step));
                float acc = 0;
                for (int i = a; i < b; i++) acc += _src[i];
                float s = acc / (b - a);
                int v = (int)Math.Round(s * 32767f);
                if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                _out[n++] = (byte)(v & 0xFF);
                _out[n++] = (byte)((v >> 8) & 0xFF);
                _pos += step;
            }
            int used = Math.Min((int)_pos, _src.Count);
            if (used > 0)
            {
                _src.RemoveRange(0, used);
                _pos -= used;
            }
            return n;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _watch?.Dispose(); } catch { }
            // The pipe first: a write the engine is not reading would hold the lock, and closing the pipe is
            // what releases it.
            try { _pipe.Dispose(); } catch { }
            lock (_lock) StopCaptureLocked();
        }
    }
}
