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

        // Screen keys and the debug key
        Check(GlobalHotkey.ScreenKeyAllowed(ctrlAltHeld: false, plainKeysEnabled: true),
            "plain F8 reads the screen by default");
        Check(!GlobalHotkey.ScreenKeyAllowed(ctrlAltHeld: false, plainKeysEnabled: false),
            "plain F8 goes to the other app when screen keys are turned off");
        Check(GlobalHotkey.ScreenKeyAllowed(ctrlAltHeld: true, plainKeysEnabled: false),
            "Ctrl+Alt+F8 always reads the screen");
        Check(!GlobalHotkey.DebugKeyAllowed(ctrlAltHeld: false),
            "plain F12 is left to the browser and the IDE");
        Check(GlobalHotkey.DebugKeyAllowed(ctrlAltHeld: true),
            "Ctrl+Alt+F12 opens the debug window");

        // Defaults a fresh install and an old config file both get
        var defaults = new SettingsWindow.AppConfig();
        Check(!defaults.SaveSessionAudio, "session audio is not saved unless turned on");
        Check(defaults.ScreenKeysEverywhere, "screen keys work everywhere by default");
        Check(defaults.WatchScreenEnabled, "continuous screen reading stays on by default");

        return failed;
    }
}
