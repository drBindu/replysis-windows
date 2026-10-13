using System;

namespace InterviewCopilot
{
    /// <summary>
    /// Which resume text the speech engine's custom dictionary is built from.
    ///
    /// Found 2026-10-12 in a real log: at launch the engine was started (and its
    /// dictionary file written) about 1.4 seconds BEFORE the saved resume was put
    /// back in the box, so a returning person ran the whole session with
    /// "No terms written; STT loses its custom dictionary" and the names, tools
    /// and companies on their resume were left to be guessed. The dictionary is
    /// read once when the engine starts, so writing it later changes nothing.
    ///
    /// Until the startup restore has happened, an empty box therefore means "not
    /// restored yet", not "the person has no resume", and the saved one is used.
    /// After it, an empty box is respected: someone who cleared their resume does
    /// not get the old one's terms.
    /// </summary>
    internal static class VocabResume
    {
        internal static string Choose(string? boxText, bool startupRestoreDone, Func<string> latestSaved)
        {
            if (!string.IsNullOrWhiteSpace(boxText)) return boxText!;
            if (startupRestoreDone) return "";
            try { return latestSaved() ?? ""; }
            catch { return ""; }
        }
    }
}
