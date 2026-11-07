using System;
using System.Threading;
using System.Windows.Controls;
using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// Round 20 (2026-11-07): Space in the app's own fields, repeated spaces while indenting code, and code in the compact
/// answer window. No window is shown: the controls are only created.
/// </summary>
internal static class UiHotkeyTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        // Any editable field counts as typing, a read-only answer box does not.
        string? fieldResult = null;
        var thread = new Thread(() =>
        {
            try
            {
                var pay = new TextBox();
                var answer = new TextBox { IsReadOnly = true };
                var password = new PasswordBox();
                var button = new Button();
                fieldResult =
                    (GlobalHotkey.IsEditableTextFocus(pay) ? "1" : "0") +
                    (GlobalHotkey.IsEditableTextFocus(password) ? "1" : "0") +
                    (GlobalHotkey.IsEditableTextFocus(answer) ? "1" : "0") +
                    (GlobalHotkey.IsEditableTextFocus(button) ? "1" : "0") +
                    (GlobalHotkey.IsEditableTextFocus(null) ? "1" : "0");
            }
            catch (Exception ex) { fieldResult = "error " + ex.GetBaseException().Message; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(20));
        Check(fieldResult?[0] == '1', "a space in an editable field such as the pay box is text, not a toggle");
        Check(fieldResult?[1] == '1', "a space in a password box is text");
        Check(fieldResult?[2] == '0', "a read-only answer box with focus does not block Space");
        Check(fieldResult?[3] == '0', "a focused button does not count as typing");
        Check(fieldResult?[4] == '0', "nothing focused does not count as typing");

        // Repeated spaces after a pause: the first toggles, the next ones within the typing window are typing.
        long now = 100_000, longAgo = now - 60_000;
        Check(GlobalHotkey.ListeningShortcutAllowed(now, longAgo, false, false, false, true, longAgo),
              "the first Space after a pause still toggles");
        Check(!GlobalHotkey.ListeningShortcutAllowed(now + 150, longAgo, false, false, false, true, now),
              "a second Space 150 ms later while indenting is typing");
        Check(GlobalHotkey.ListeningShortcutAllowed(now + 150, longAgo, false, true, false, true, now),
              "Ctrl+Alt+Space always toggles, even right after another Space");
        Check(GlobalHotkey.ListeningShortcutAllowed(now + GlobalHotkey.TypingWindowMs, longAgo, false, false, true, false, now),
              "a Space a full typing window after the last one toggles again");
        Check(GlobalHotkey.ListeningShortcutAllowed(now, longAgo, false, false, true, false),
              "callers that do not pass a last Space time behave as before");

        // Code in the compact window keeps its indentation when the answer also has bullets.
        string answer = "Use a loop.\n```python\ndef f(xs):\n    for x in xs:\n        print(x)\n```\nMORE TO SAY\n• It is O(n)\n•   Mention edge cases";
        string shown = AnswerWindow.FormatForReading(answer).Replace("\r\n", "\n");
        Check(shown.Contains("\n    for x in xs:\n        print(x)"), "Python indentation survives in the compact window");
        Check(!shown.Contains("```") && !shown.Contains("python\n"), "fences and the language tag are taken off");
        Check(shown.Contains("• Mention edge cases") || shown.Contains("•   Mention edge cases"), "bullets are still shown");
        Check(shown.StartsWith("Use a loop."), "the spoken part comes first");
        Check(AnswerWindow.FormatForReading("Plain answer.\n```js\n  let a = 1;\n```") .Contains("  let a = 1;"),
              "without bullets code indentation was and stays kept");
        Check(AnswerWindow.FormatForReading("• one\n\n• two") == "• one\r\n• two" || AnswerWindow.FormatForReading("• one\n\n• two") == "• one\n• two",
              "blank lines between bullets are still removed");

        return failed;
    }
}
