using System;
using System.Collections.Generic;

namespace InterviewCopilot
{
    /// <summary>
    /// Which speech language the engine is actually started with (round 53, 2026-11-07).
    /// </summary>
    internal static class LanguageRules
    {
        // Languages the engine routes to Sarvam AI (Speechmatics can't do them).
        // Keep in sync with SARVAM_LANG_MAP in speechmatics_engine.py.
        private static readonly HashSet<string> SarvamLanguages =
            new(StringComparer.OrdinalIgnoreCase) { "te", "kn", "ml", "gu", "pa", "or", "as" };

        internal static bool IsSarvamLanguage(string? code) => SarvamLanguages.Contains((code ?? "").Trim());

        /// <summary>
        /// The code passed to the engine. A Sarvam language needs a Sarvam key, and no customer can enter one: Settings has
        /// no field for it and our server issues none. Telugu was in the picker, so a customer who chose it got an engine
        /// that stopped at once with "SARVAM_API_KEY not set", read as FATAL: listening never started, the app said their
        /// antivirus might be blocking Replysis, the restart loop stopped, and every launch sent an engine-fatal report.
        /// Without a key such a language listens in English instead, which an interview in India is usually held in.
        /// </summary>
        internal static string EngineLanguage(string? saved, string? sarvamKey)
        {
            string code = string.IsNullOrWhiteSpace(saved) ? "en" : saved.Trim();
            if (IsSarvamLanguage(code) && string.IsNullOrWhiteSpace(sarvamKey)) return "en";
            return code;
        }
    }
}
