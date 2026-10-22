// Trial check for one-mode listening (2026-10-22). Feeds generated speech (never a real microphone) into the real
// engine through its two sound pipes and records who the engine says spoke each turn.
//   A: speech on the computer-sound pipe only          -> interviewer
//   B: speech on the mic pipe only                     -> candidate
//   C: the same speech on both, quieter on the mic     -> interviewer (no headphones, echo)
//   D: speech on the mic pipe only                     -> candidate
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text;
using InterviewCopilot;

internal static class Program
{
    static readonly StringBuilder Report = new();
    static void Say(string s) { Console.WriteLine(s); lock (Report) Report.AppendLine(s); }

    [STAThread]
    static int Main(string[] args) => Run(args).GetAwaiter().GetResult();

    static async Task<int> Run(string[] args)
    {
        string outFile = args.Length > 0 ? args[0] : "onemode-result.txt";
        try
        {
            bool signedIn = UserSession.TryLoadFromDisk();
            if (!signedIn && !string.IsNullOrEmpty(UserSession.RefreshToken)) signedIn = await UserSession.TryRefreshAsync();
            if (!signedIn) { Say("SKIP no saved sign in"); return 2; }
            await UserSession.EnsureFreshTokenAsync();
            if (!await UserSession.EnsureSpeechmaticsKeyAsync() || string.IsNullOrEmpty(UserSession.DeepgramToken)) { Say("SKIP no Deepgram token"); return 2; }

            var turns = new (string label, string text, double sys, double mic, string want)[] {
                ("A", "Tell me about a time you led a team through a difficult deadline.", 1.0, 0.0, "interviewer"),
                ("B", "Sorry, could you please repeat the question?", 0.0, 1.0, "candidate"),
                ("C", "What is your biggest strength as an engineer?", 1.0, 0.35, "interviewer"),
                ("D", "What is dependency injection?", 0.0, 1.0, "candidate"),
            };
            var pcm = turns.Select(t => Speak(t.text)).ToArray();

            string id = Guid.NewGuid().ToString("N");
            using var micPipe = new NamedPipeServerStream("onemode-mic-" + id, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var sysPipe = new NamedPipeServerStream("onemode-sys-" + id, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            string data = Path.Combine(Path.GetTempPath(), "onemode-" + id);
            Directory.CreateDirectory(data);

            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string script = Path.Combine(root, "speechmatics_engine.py");
            if (!File.Exists(script)) { Say("ERROR engine script not found at " + script); return 3; }
            var psi = new ProcessStartInfo(@"C:\Python313\python.exe", $"-u \"{script}\" --mode both --language en")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root };
            psi.Environment["DG_TOKEN"] = UserSession.DeepgramToken;
            psi.Environment["SM_API_KEY"] = UserSession.SpeechmaticsKey;
            psi.Environment["REPLYSIS_DEEPGRAM_ONLY"] = "1";
            psi.Environment["REPLYSIS_DEEPGRAM_MODEL"] = "flux";
            psi.Environment["REPLYSIS_MIC_PIPE"] = @"\\.\pipe\onemode-mic-" + id;
            psi.Environment["REPLYSIS_SYS_PIPE"] = @"\\.\pipe\onemode-sys-" + id;
            psi.Environment["APP_DATA_DIR"] = data;
            using var engine = Process.Start(psi)!;
            var sources = new List<string>();
            var online = new TaskCompletionSource();
            string? lastLabel = null;
            _ = Task.Run(async () => {
                string? line;
                while ((line = await engine.StandardOutput.ReadLineAsync()) != null)
                {
                    if (line.Contains("STATUS: ONLINE")) online.TrySetResult();
                    // One label per sentence: the last one before the sentence's UTTERANCE END.
                    if (line.StartsWith(">>> SOURCE ")) { lastLabel = line[11..].Trim(); Say("   engine: " + line); }
                    else if (line.Contains("UTTERANCE END")) { if (lastLabel != null) lock (sources) sources.Add(lastLabel); lastLabel = null; Say("   engine: " + line); }
                    else if (line.Contains("NATIVE") || line.Contains("FATAL")) Say("   engine: " + line);
                }
            });
            _ = Task.Run(async () => { string? l; while ((l = await engine.StandardError.ReadLineAsync()) != null) if (l.Contains("Error") || l.Contains("Traceback")) Say("   stderr: " + l); });

            await Task.WhenAll(micPipe.WaitForConnectionAsync(), sysPipe.WaitForConnectionAsync()).WaitAsync(TimeSpan.FromSeconds(40));
            Say("engine attached to both pipes");

            var queueSys = new Queue<byte[]>(); var queueMic = new Queue<byte[]>();
            void Enqueue(byte[] audio, double sysGain, double micGain)
            {
                for (int i = 0; i < audio.Length; i += 3200)
                {
                    int n = Math.Min(3200, audio.Length - i);
                    var s = new byte[3200]; var m = new byte[3200];
                    for (int k = 0; k + 1 < n; k += 2)
                    {
                        short v = BitConverter.ToInt16(audio, i + k);
                        BitConverter.GetBytes((short)(v * sysGain)).CopyTo(s, k);
                        BitConverter.GetBytes((short)(v * micGain)).CopyTo(m, k);
                    }
                    lock (queueSys) { queueSys.Enqueue(s); queueMic.Enqueue(m); }
                }
            }
            void Silence(double seconds) { for (int i = 0; i < seconds * 10; i++) lock (queueSys) { queueSys.Enqueue(new byte[3200]); queueMic.Enqueue(new byte[3200]); } }

            var writing = new CancellationTokenSource();
            var writer = Task.Run(async () => {
                var clock = Stopwatch.StartNew(); long tick = 0;
                while (!writing.IsCancellationRequested)
                {
                    byte[] s, m;
                    lock (queueSys) { s = queueSys.Count > 0 ? queueSys.Dequeue() : new byte[3200]; m = queueMic.Count > 0 ? queueMic.Dequeue() : new byte[3200]; }
                    try { sysPipe.Write(s); micPipe.Write(m); } catch { return; }
                    tick++;
                    long wait = tick * 100 - clock.ElapsedMilliseconds;
                    if (wait > 0) await Task.Delay((int)wait);
                }
            });

            Silence(3);
            await online.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Say("engine online");
            Silence(2);
            for (int t = 0; t < turns.Length; t++)
            {
                Enqueue(pcm[t], turns[t].sys, turns[t].mic);
                Silence(5);
            }
            int queued; lock (queueSys) queued = queueSys.Count;
            await Task.Delay(queued * 100 + 4000);
            writing.Cancel();
            try { engine.Kill(true); } catch { }

            int failed = 0;
            List<string> got; lock (sources) got = sources.ToList();
            Say($"turns labelled: {string.Join(", ", got)}");
            for (int t = 0; t < turns.Length; t++)
            {
                string g = t < got.Count ? got[t] : "(none)";
                bool ok = g == turns[t].want;
                if (!ok) failed++;
                Say($"{(ok ? "PASS" : "FAIL")}  {turns[t].label}: \"{turns[t].text}\" sys {turns[t].sys} mic {turns[t].mic} -> {g} (want {turns[t].want})");
            }
            Say(failed == 0 ? "ALL PASSED" : $"{failed} FAILED");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex) { Say("ERROR " + ex); return 3; }
        finally { File.WriteAllText(outFile, Report.ToString()); }
    }

    static byte[] Speak(string sentence)
    {
        using var synthesizer = new SpeechSynthesizer();
        using var wave = new MemoryStream();
        synthesizer.SetOutputToAudioStream(wave, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synthesizer.Speak(sentence);
        synthesizer.SetOutputToNull();
        return wave.ToArray();
    }
}
