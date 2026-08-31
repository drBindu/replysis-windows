using System.Text.RegularExpressions;

namespace CleanerTests;

/// <summary>
/// Is the candidate reading our answer, or asking something new?
///
/// The product exists so an answer appears and the candidate says it. In
/// practice mode the microphone hears them do exactly that, the words return as
/// a transcript, and the app treated them as a new question - replacing the
/// answer they were half-way through reading with an answer to itself, and
/// charging a credit for it. Then again.
///
/// Nobody reports this as missing echo suppression. They report that the answer
/// disappears while they are reading it.
///
/// The real answer text below is taken verbatim from the owner's screen when
/// he hit this.
/// </summary>
internal static class ReadBackTests
{
    // What was on screen when the answer vanished.
    private const string Shown =
        "I handle data by first cleaning and transforming it with Pandas and NumPy, then " +
        "building feature sets that feed into models like XGBoost or LightGBM. After " +
        "training, I evaluate with cross-validation and fine-tune hyperparameters to " +
        "improve accuracy. Finally, I deploy the model using Docker and Kubernetes, " +
        "monitoring drift with an ELK stack. MORE TO SAY I use PySpark for large-scale " +
        "transformations when data exceeds memory limits. For real-time scoring, I expose " +
        "the model via a FastAPI endpoint behind an NGINX reverse proxy.";

    private static string[] Words(string s) =>
        Regex.Matches(s.ToLowerInvariant(), @"[\p{L}\p{N}']+").Select(m => m.Value).ToArray();

    private static bool IsReadingBack(string candidate)
    {
        if (string.IsNullOrWhiteSpace(Shown) || string.IsNullOrWhiteSpace(candidate)) return false;
        string[] said = Words(candidate);
        if (said.Length < 8) return false;
        var onScreen = new HashSet<string>(Words(Shown));
        if (onScreen.Count < 20) return false;
        return (double)said.Count(onScreen.Contains) / said.Length >= 0.55;
    }

    internal static int Run()
    {
        int failed = 0;
        void Case(string label, bool got, bool want)
        {
            bool ok = got == want;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) { failed++; Console.WriteLine($"        expected {want}, got {got}"); }
        }

        // ── Reading it aloud: must be ignored ────────────────────────────────
        Case("reading the answer verbatim",
            IsReadingBack("I handle data by first cleaning and transforming it with Pandas "
                        + "and NumPy then building feature sets that feed into models"), true);

        Case("reading it with stumbles and filler",
            IsReadingBack("so um I handle data by first cleaning and transforming it with "
                        + "Pandas and NumPy and then building feature sets"), true);

        Case("paraphrasing while reading",
            IsReadingBack("I deploy the model using Docker and Kubernetes and I monitor "
                        + "drift with an ELK stack"), true);

        // ── Real questions: must get through ─────────────────────────────────
        Case("a genuine follow-up sharing vocabulary",
            IsReadingBack("why did you choose XGBoost over a neural network for that"), false);

        Case("a new question on the same topic",
            IsReadingBack("how would you handle data that does not fit in memory at all"), false);

        Case("a completely different question",
            IsReadingBack("tell me about a time you disagreed with your manager"), false);

        Case("short follow-up, never judged",
            IsReadingBack("and the complexity"), false);

        Case("short answer-like phrase, never judged",
            IsReadingBack("I use Docker"), false);

        return failed;
    }
}
