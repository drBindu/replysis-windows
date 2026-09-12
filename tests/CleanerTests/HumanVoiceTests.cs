using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// The punctuation the owner reads as "AI generated", checked against the real
/// cleaner rather than a description of it.
///
/// The em dash and en dash were already rewritten here. The non-breaking hyphen
/// was not: it is not a dash, so the dash rules never saw it, and the model
/// reaches for it constantly in compound adjectives ("statically-typed",
/// "key-value", "real-time", "self-healing"). It renders as an ordinary hyphen
/// that refuses to wrap, which in the compact overlay drops a long compound onto
/// a line of its own.
///
/// The characters are built with escapes rather than pasted, because a sweep for
/// exactly these characters was once written in a shell heredoc that collapsed
/// the backslashes, found none in a file that had eight, and the zero was
/// reported as clean.
/// </summary>
internal static class HumanVoiceTests
{
    private const char EmDash  = '—';
    private const char EnDash  = '–';
    private const char NbHyph  = '‑';   // non-breaking hyphen
    private const char FigDash = '‒';   // figure dash
    private const char Minus   = '−';   // minus sign

    public static int Run()
    {
        int failed = 0;

        void Check(bool ok, string label, string? detail = null)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) { failed++; if (detail != null) Console.WriteLine($"        {detail}"); }
        }

        // Prove the detector fires before trusting any clean result below.
        Check("a‑b".IndexOf(NbHyph) >= 0,
              "self-check: the test can see a non-breaking hyphen at all");

        string outp = MainWindow.CleanAiOutput(
            $"Java is statically{NbHyph}typed and handles key{NbHyph}value data in real{NbHyph}time.");
        Check(outp.IndexOf(NbHyph) < 0 && outp.Contains("statically-typed"),
              "non-breaking hyphen becomes a plain hyphen", outp);

        outp = MainWindow.CleanAiOutput($"Latency ran 20{FigDash}30ms, so it was {Minus}5 percent.");
        Check(outp.IndexOf(FigDash) < 0 && outp.IndexOf(Minus) < 0,
              "figure dash and minus sign become plain hyphens", outp);

        // What was already working, so the new rules cannot quietly undo it.
        outp = MainWindow.CleanAiOutput($"I owned the pipeline {EmDash} end to end {EmDash} for two years.");
        Check(outp.IndexOf(EmDash) < 0, "em dash still removed", outp);

        outp = MainWindow.CleanAiOutput($"I was there 2020{EnDash}2023.");
        Check(outp.IndexOf(EnDash) < 0 && outp.Contains("2020-2023"),
              "tight en dash range still becomes a hyphen", outp);

        // A hyphen inside code must survive: these rules run over prose only, and
        // a decrement or a flag rewritten silently is code the candidate pastes.
        string code = "```c\nfor (int i = n; i-- > 0;) { run(--x); }\n```";
        outp = MainWindow.CleanAiOutput(code);
        Check(outp.Contains("i-- > 0") && outp.Contains("--x"),
              "hyphens inside a fenced block are untouched", outp);

        return failed;
    }
}
