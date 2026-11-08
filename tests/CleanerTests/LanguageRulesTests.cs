using System.IO;
using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// A language a customer can pick always starts listening (round 53, 2026-11-07). Telugu needed a Sarvam key nobody can
/// enter, so the engine stopped at once and the app blamed the antivirus.
/// </summary>
internal static class LanguageRulesTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        Check(LanguageRules.EngineLanguage("te", "") == "en", "Telugu saved before, no Sarvam key: listens in English instead of never starting");
        Check(LanguageRules.EngineLanguage("kn", null) == "en", "any Sarvam language without a key falls back to English");
        Check(LanguageRules.EngineLanguage("te", "dev-key") == "te", "a developer with a Sarvam key still gets Telugu");
        Check(LanguageRules.EngineLanguage("hi", "") == "hi", "Hindi stays Hindi (Speechmatics)");
        Check(LanguageRules.EngineLanguage("  es ", "") == "es", "a saved code is trimmed");
        Check(LanguageRules.EngineLanguage(null, null) == "en" && LanguageRules.EngineLanguage(" ", "") == "en", "nothing saved means English");

        string root = FindRepoRoot();
        string settings = File.ReadAllText(Path.Combine(root, "SettingsWindow.xaml.cs"));
        int start = settings.IndexOf("Languages =", StringComparison.Ordinal);
        int end = settings.IndexOf("};", start, StringComparison.Ordinal);
        string list = settings.Substring(start, end - start);
        Check(!list.Contains("\"te\"") && !list.Contains("Sarvam"), "the picker offers no language that needs a key nobody can enter, and names no provider");

        string engine = File.ReadAllText(Path.Combine(root, "speechmatics_engine.py"));
        foreach (string code in new[] { "te", "kn", "ml", "gu", "pa", "or", "as" })
            Check(engine.Contains($"\"{code}\":") && LanguageRules.IsSarvamLanguage(code), $"the app and the engine agree that '{code}' is a Sarvam language");
        return failed;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "InterviewCopilot.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
