using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// Space toggles listening from anywhere, because the meeting window has focus
/// during an interview. It must not toggle while the candidate types somewhere
/// else: the owner's log showed the microphone muting and unmuting between every
/// word of a chat message, every one to two seconds.
/// </summary>
internal static class SpaceHotkeyTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        // Which keys count as typing
        Check(GlobalHotkey.IsTypingKey(0x41), "a letter is typing");
        Check(GlobalHotkey.IsTypingKey(0x35), "a digit is typing");
        Check(GlobalHotkey.IsTypingKey(0xBC), "a comma is typing");
        Check(GlobalHotkey.IsTypingKey(0x08), "backspace is typing");
        Check(!GlobalHotkey.IsTypingKey(0x20), "space itself is not a typing key");
        Check(!GlobalHotkey.IsTypingKey(0x77), "F8 is not typing");
        Check(!GlobalHotkey.IsTypingKey(0x11), "Ctrl is not typing");
        Check(!GlobalHotkey.IsTypingKey(0x09), "Tab is not typing (switching fields)");

        // Typing "hello world" at an ordinary pace, 120ms between keys
        long t = 100_000;
        long lastKey = t;                 // the "o" of hello
        Check(!GlobalHotkey.IsSpaceAToggle(t + 120, lastKey, false),
            "space between words does not toggle");

        // A deliberate press after a pause
        Check(GlobalHotkey.IsSpaceAToggle(t + 5_000, lastKey, false),
            "space after a pause toggles");
        Check(GlobalHotkey.IsSpaceAToggle(t + GlobalHotkey.TypingWindowMs, lastKey, false),
            "space exactly one window after typing toggles");
        Check(!GlobalHotkey.IsSpaceAToggle(t + GlobalHotkey.TypingWindowMs - 1, lastKey, false),
            "space just inside the window does not toggle");

        // Nothing typed since the app started
        Check(GlobalHotkey.IsSpaceAToggle(50, -GlobalHotkey.TypingWindowMs, false),
            "first ever press toggles");

        // System shortcuts
        Check(!GlobalHotkey.IsSpaceAToggle(t + 5_000, lastKey, true),
            "Ctrl, Shift or Win plus Space never toggles");

        return failed;
    }
}
