using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// The activity diary (2026-11-14) records which buttons, menus and steps a person used, so problems can be found and fixed.
/// It must never carry anything a person said, typed or was shown. These tests hold the one rule that guarantees it: a
/// property value with a space in it, or longer than 40 characters, never leaves the app, and the sender itself drops it.
/// </summary>
internal static class ActivityDiaryTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        Console.WriteLine("\n37. The activity diary carries labels only, never free text");
        ActivityDiary.ResetForTest();

        // ── The rule itself ──
        Check(!ActivityDiary.IsSafeValue("has a space"), "a value with a space is refused");
        Check(!ActivityDiary.IsSafeValue("tab\there") && !ActivityDiary.IsSafeValue("line\nbreak"), "tabs and line breaks are refused");
        Check(!ActivityDiary.IsSafeValue(new string('a', 41)), "a value of 41 characters is refused");
        Check(ActivityDiary.IsSafeValue(new string('a', 40)), "a value of exactly 40 characters is allowed");
        Check(!ActivityDiary.IsSafeValue(""), "an empty value is refused");
        Check(!ActivityDiary.IsSafeValue("someone@example.com") && !ActivityDiary.IsSafeValue(@"C:\Users\x\resume.pdf"),
            "an email address or a file path never fits");
        Check(ActivityDiary.IsSafeValue("auto") && ActivityDiary.IsSafeValue("1.0.53") && ActivityDiary.IsSafeValue("google-e6-500"),
            "fixed labels and versions are allowed");
        Check(ActivityDiary.IsSafeValue(1234) && ActivityDiary.IsSafeValue(12L) && ActivityDiary.IsSafeValue(true) && ActivityDiary.IsSafeValue(2.5),
            "numbers and true/false are allowed");
        Check(!ActivityDiary.IsSafeValue(double.NaN) && !ActivityDiary.IsSafeValue(new object()) && !ActivityDiary.IsSafeValue(null),
            "anything else is refused");
        Check(ActivityDiary.IsSafeName("answer_shown") && !ActivityDiary.IsSafeName("Answer Shown") && !ActivityDiary.IsSafeName(new string('a', 41)),
            "event and property names are short snake_case");

        // ── Recording drops what breaks it ──
        var e = ActivityDiary.Make("button_clicked", new Dictionary<string, object?>
        {
            ["id"] = "copy",
            ["text"] = "Tell me about yourself",            // a sentence: dropped
            ["long"] = new string('x', 41),                   // too long: dropped
            ["Bad Key"] = "x",                                // bad name: dropped
            ["ms"] = 840,
        }, DateTime.UtcNow);
        Check(e != null && e.Props.Count == 2 && (string)e.Props["id"] == "copy" && e.Props.ContainsKey("ms"),
            "Make keeps the labels and drops the sentence, the long value and the bad name");
        Check(ActivityDiary.Make("Not A Name", null, DateTime.UtcNow) == null, "an event with a bad name is not recorded at all");

        ActivityDiary.Track("setting_changed", ("name", "language"), ("value", "what the interviewer said"));
        var tracked = ActivityDiary.Snapshot().LastOrDefault();
        Check(tracked != null && tracked.Event == "setting_changed" && !tracked.Props.ContainsKey("value"),
            "Track drops a free text value on the way in");

        // ── The sender enforces it again ──
        var smuggled = new List<DiaryEvent>
        {
            new() { T = 1, SessionId = ActivityDiary.SessionId, V = "1.0.53", Event = "notice_shown",
                    Props = new Dictionary<string, object> { ["kind"] = "a sentence somebody typed", ["n"] = 3 } },
            new() { T = 2, SessionId = ActivityDiary.SessionId, V = "1.0.53", Event = "notice_shown",
                    Props = new Dictionary<string, object> { ["kind"] = new string('k', 60) } },
        };
        string body = ActivityDiary.BuildBody(smuggled);
        Check(!body.Contains("somebody typed") && !body.Contains(new string('k', 41)), "the body never contains a value that breaks the rule");
        Check(EveryStringValueIsSafe(body, out string why), "every value in a sent body has no space and is at most 40 characters" + why);
        using (var doc = JsonDocument.Parse(body))
        {
            var first = doc.RootElement.GetProperty("events")[0];
            Check(first.GetProperty("app").GetString() == "windows" && first.GetProperty("t").GetInt64() == 1 &&
                  first.GetProperty("session_id").GetString() == ActivityDiary.SessionId &&
                  first.GetProperty("props").GetProperty("n").GetInt64() == 3,
                "the body has the agreed shape: t, session_id, app, v, event, props");
        }

        // ── Batches, cap, file ──
        ActivityDiary.ResetForTest();
        for (int i = 0; i < ActivityDiary.MaxQueued + 1; i++)
            ActivityDiary.Enqueue(new DiaryEvent { T = i, SessionId = ActivityDiary.SessionId, V = "1", Event = "answer_copied" });
        var all = ActivityDiary.Snapshot();
        Check(all.Count == ActivityDiary.MaxQueued && all[0].T == 1, "at most 5,000 kept, the oldest dropped first");

        using (var doc = JsonDocument.Parse(ActivityDiary.BuildBody(all)))
            Check(doc.RootElement.GetProperty("events").GetArrayLength() == 200, "one request carries at most 200 events");

        string folder = Path.Combine(Path.GetTempPath(), "replysis-diary-" + Guid.NewGuid().ToString("N"));
        try
        {
            ActivityDiary.SaveTo(folder, all.Take(3).Concat(smuggled).ToList());
            var back = ActivityDiary.LoadFrom(folder);
            Check(back.Count == 5 && back[0].T == 1 && back[0].Event == "answer_copied", "unsent events survive a restart");
            Check(back.All(x => x.Props.Values.All(ActivityDiary.IsSafeValue)) && !back[3].Props.ContainsKey("kind"),
                "events read back from the file are checked against the rule again");

            File.WriteAllText(Path.Combine(folder, ActivityDiary.FileName), "{ not json");
            Check(ActivityDiary.LoadFrom(folder).Count == 0, "a damaged file is an empty diary, not a crash");

            Check(!ActivityDiary.TakeRunningMarker(folder), "no marker: the last run closed cleanly");
            ActivityDiary.WriteRunningMarker(folder);
            Check(ActivityDiary.TakeRunningMarker(folder), "a marker left behind means the last run never closed cleanly");
            Check(!ActivityDiary.TakeRunningMarker(folder), "and it is reported once");
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
        ActivityDiary.ResetForTest();

        // ── Replies ──
        Check(ActivityDiary.Classify(204) == ActivityDiary.Outcome.Stored, "204 means stored");
        Check(ActivityDiary.Classify(401) == ActivityDiary.Outcome.Unauthorized, "401 means refresh the sign-in once and retry");
        Check(new[] { 404, 429, 500, 502, 503 }.All(s => ActivityDiary.Classify(s) == ActivityDiary.Outcome.Retry),
            "404, 429 and 5xx keep the batch for later");
        Check(ActivityDiary.Classify(413) == ActivityDiary.Outcome.TooLarge, "413 keeps the batch and sends smaller ones");

        using (var req = ActivityDiary.BuildRequest("https://replysis.com/", "tok", "{\"events\":[]}"))
            Check(req.Method == System.Net.Http.HttpMethod.Post && req.RequestUri!.ToString() == "https://replysis.com/api/v1/activity" &&
                  req.Headers.Authorization?.Scheme == "Bearer" && req.Content!.Headers.ContentType!.MediaType == "application/json",
                "POST /api/v1/activity with the sign-in token, as JSON");

        // ── Kinds and labels made from app text ──
        Check(ActivityDiary.Kind("engine-fatal-microphone-headset-of-someone") == "engine-fatal", "a fatal engine line is only engine-fatal");
        Check(ActivityDiary.Kind("alert-" + new string('x', 80)).Length <= 40, "a long kind is cut to 40");
        Check(ActivityDiary.Kind("Some Kind/With:Odd chars") == "some-kind-with-odd-chars", "a kind is lower case with no spaces");
        Check(LoginWindow.SignInFailKind("Google sign-in failed. Please try again or contact support. (E6-500)") == "google-e6-500" &&
              LoginWindow.SignInFailKind("Incorrect email or password.") == "invalid_login_credentials" &&
              LoginWindow.SignInFailKind("anything else at all") == "other",
            "sign-in failures are fixed labels, never the message");
        Check(NativeAudioFeed.DeviceType(3, "BTHENUM") == "bluetooth" && NativeAudioFeed.DeviceType(5, "USB") == "headphones" &&
              NativeAudioFeed.DeviceType(1, "HDAUDIO") == "speakers" && NativeAudioFeed.DeviceType(null, null) == "other",
            "an audio device is described by kind, never by name");

        // ── Review 2026-11-16: refused sign-in, sign-out, reconnect storms ──
        {
            string src = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ActivityDiary.cs")));
            Check(ActivityDiary.RefusedPause >= TimeSpan.FromMinutes(5) && src.Contains("DateTime.UtcNow < _pauseUntilUtc) return;") &&
                  src.Split("_pauseUntilUtc = DateTime.UtcNow + RefusedPause;").Length == 3,
                "a refused sign-in pauses sending instead of asking the token service every 10 s");
            Check(src.Contains("TimeSpan.FromSeconds(5) : TimeSpan.Zero") && src.Contains("_queue.RemoveAll(e => mine.Contains(e))"),
                "signing out waits its turn to send, and what is left is dropped, never sent under the next sign-in");
            string main = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "MainWindow.xaml.cs")));
            Check(!main.Contains("ActivityDiary.Track(\"speech_dropped\"") && !main.Contains("ActivityDiary.Track(\"speech_reconnected\""),
                "speech drops and reconnects are recorded at most once a minute");
        }

        // ── Every call in the app ──
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var call = new Regex(@"ActivityDiary\.Track(?:Throttled)?\((?:[^;""]*?,\s*)?""([^""]+)""");
        var risky = new Regex(@"ActivityDiary\.\w+\(.*(\.Text\b|transcript|question\b|Resume|Email|FriendlyName|DeviceName|Password|_jobDescription|_companyName)",
                              RegexOptions.IgnoreCase);
        int calls = 0;
        var badNames = new List<string>();
        var riskyLines = new List<string>();
        foreach (string file in Directory.GetFiles(root, "*.cs"))
        {
            foreach (string line in File.ReadAllLines(file))
            {
                if (!line.Contains("ActivityDiary.")) continue;
                foreach (Match m in call.Matches(line))
                {
                    calls++;
                    if (!ActivityDiary.IsSafeName(m.Groups[1].Value)) badNames.Add(m.Groups[1].Value);
                }
                // Judged on the code only: fixed labels in quotes ("saved_resumes") and comments are not what is passed.
                string code = Regex.Replace(line, @"//.*$", "");
                code = Regex.Replace(code, @"""[^""]*""", "\"\"");
                if (risky.IsMatch(code)) riskyLines.Add(Path.GetFileName(file) + ": " + line.Trim());
            }
        }
        Check(calls >= 40, $"the app records the diary in many places ({calls} calls found)");
        Check(badNames.Count == 0, "every event name in the app is a safe name" + (badNames.Count > 0 ? ": " + string.Join(", ", badNames) : ""));
        Check(riskyLines.Count == 0, "no diary call is handed text, a transcript, a resume, an email or a device name" +
              (riskyLines.Count > 0 ? "\n        " + string.Join("\n        ", riskyLines) : ""));

        return failed;
    }

    /// <summary>Every string value under "props" in a request body: no whitespace, at most 40 characters.</summary>
    private static bool EveryStringValueIsSafe(string body, out string why)
    {
        why = "";
        using var doc = JsonDocument.Parse(body);
        foreach (var ev in doc.RootElement.GetProperty("events").EnumerateArray())
            foreach (var p in ev.GetProperty("props").EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String) continue;
                string s = p.Value.GetString() ?? "";
                if (s.Length > 40 || s.Any(char.IsWhiteSpace))
                {
                    why = $" (found \"{s}\")";
                    return false;
                }
            }
        return true;
    }
}
