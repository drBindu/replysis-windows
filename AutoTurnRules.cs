using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace InterviewCopilot
{
    /// <summary>
    /// Pure rules for Auto mode turn-taking, kept out of MainWindow so they can be tested.
    ///
    /// A 12-question mock interview through the real app found three ways one
    /// question was mistaken for another:
    ///   - The next question's opening words, heard a few seconds after a fast answer
    ///     ("Before we wrap up,", "And how do you see a role"), were joined onto the
    ///     previous question as its "continuation" and answered together.
    ///   - Speech recognition sent a corrected copy of a question already answered
    ///     ("do you have questions for me" became "do you have for me"), and it was
    ///     answered again as new. Without "questions" it no longer looked like the
    ///     wrap-up, so the candidate asked three more questions.
    ///   - Garbled letters ("Capital m o t o g p f") were joined onto "What is Java".
    /// </summary>
    internal static class AutoTurnRules
    {
        /// <summary>
        /// A continuation is the interviewer carrying on after a pause, so its first
        /// words arrive right after the previous submission. Words starting later
        /// than this are the next question, however they begin.
        /// </summary>
        internal static readonly TimeSpan ContinuationStartWindow = TimeSpan.FromSeconds(4);

        internal static bool StartedSoonEnoughToContinue(DateTime lastSubmitUtc, DateTime firstWordsAfterUtc) =>
            lastSubmitUtc != DateTime.MinValue &&
            firstWordsAfterUtc != DateTime.MinValue &&
            firstWordsAfterUtc >= lastSubmitUtc &&
            firstWordsAfterUtc - lastSubmitUtc <= ContinuationStartWindow;

        private static string[] Words(string s) =>
            Regex.Matches((s ?? "").ToLowerInvariant(), @"[\p{L}\p{N}']+").Select(m => m.Value).ToArray();

        /// <summary>Mostly single letters: recognition hearing noise, not a sentence.</summary>
        internal static bool IsSpelledOutNoise(string text)
        {
            string[] words = Words(text);
            if (words.Length < 3) return false;
            int single = words.Count(w => w.Length == 1 && w != "i" && w != "a");
            return single * 2 >= words.Length;
        }

        /// <summary>
        /// "Can you tell me", "Could you walk me through", "Tell me about": the opening
        /// of a request with nothing asked yet. The recogniser often delivers these a
        /// beat before the rest, and answering them lost the rest of the question.
        /// </summary>
        internal static bool IsBareRequestOpener(string question) =>
            Regex.IsMatch((question ?? "").Trim().TrimEnd('?', '.', '!', ',').ToLowerInvariant(),
                @"^(?:so |and |okay |ok |now |alright )?" +
                @"(?:(?:can|could|would|will) (?:you|u) (?:please )?)?" +
                @"(?:tell|walk|describe|explain|talk|share|give|go over|go through|take)" +
                @"(?: (?:me|us))?(?: (?:about|through|a bit about|more about|how|what|why))?$");

        private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "and", "or", "but", "if", "so", "to", "of", "in", "on", "at", "by", "for", "with",
            "from", "into", "about", "as", "is", "are", "was", "were", "be", "been", "am", "do", "does", "did",
            "have", "has", "had", "can", "could", "would", "will", "should", "may", "might", "must",
            "i", "i'm", "i've", "me", "my", "we", "our", "us", "you", "your", "you're", "they", "them", "their",
            "it", "it's", "its", "this", "that", "these", "those", "what", "which", "who", "how", "when", "where",
            "why", "there", "here", "not", "no", "yes", "all", "any", "some", "more", "most", "also", "just",
            "than", "then", "very", "really", "work", "use", "used", "using", "like", "make", "get", "need",
        };

        /// <summary>
        /// The share of the meaningful words just heard that are already in the
        /// answer on screen. Counting every word made an ordinary question look like
        /// the candidate reading the answer aloud: "Are you authorized to work in the
        /// US, and will you need sponsorship in the future?" shares "you", "to",
        /// "work", "in", "the", "and", "will" with almost any answer, and it was
        /// ignored in a mock interview. Returns 0 when there is too little to judge.
        /// </summary>
        internal static double MeaningfulShareOnScreen(string heard, string shown)
        {
            string[] said = Words(heard).Where(w => !CommonWords.Contains(w)).ToArray();
            if (said.Length < 5) return 0;
            var onScreen = new HashSet<string>(Words(shown).Where(w => !CommonWords.Contains(w)));
            if (onScreen.Count < 15) return 0;
            return (double)said.Count(onScreen.Contains) / said.Length;
        }

        /// <summary>
        /// True when a tail is a new question with a subject of its own, however it
        /// begins. "And what is a memory leak?" opens with a joining word but asks
        /// about something new, and merging it answered three questions as one.
        ///
        /// A tail that points back at what was just asked - "and where have you used
        /// it?", "with an example" - is a genuine addition, and still merges.
        /// </summary>
        internal static bool AsksItsOwnQuestion(string tail)
        {
            string q = Regex.Replace((tail ?? "").Trim().ToLowerInvariant(),
                @"^(?:and|or|but|also|plus|so|then|okay|ok)\s+", "");
            if (q.Length == 0) return false;

            // Pointing back: the subject is the question before, not a new one.
            if (Regex.IsMatch(q, @"\b(it|that|this|them|those|these|there|the same)\b")) return false;

            // Interrogative opening with a word of its own after it.
            return Regex.IsMatch(q,
                @"^(?:what|what's|whats|how|why|which|when|where|who|whose|can you|could you|do you|does|did|is|are|tell me about|explain|describe|define)\b" +
                @"[^?]*\b[a-z]{3,}\b");
        }

        /// <summary>
        /// Removes the question already answered from the front of a transcript and
        /// returns what was said after it. Recognition re-delivers the last sentence
        /// with the next words attached: "Before we wrap up, do you have any questions
        /// for me? Good. Our top priority is..." was read as the wrap-up invitation
        /// again, so a closing line appeared while the interviewer was still answering.
        /// Returns the text unchanged when it does not start with that question.
        /// </summary>
        internal static string StripAnsweredPrefix(string transcript, string answered)
        {
            string text = transcript ?? "";
            string[] done = Words(answered);
            if (done.Length < 3) return text;

            var matches = Regex.Matches(text, @"[\p{L}\p{N}']+");
            if (matches.Count <= done.Length) return text;
            for (int i = 0; i < done.Length; i++)
                if (!string.Equals(matches[i].Value, done[i], StringComparison.OrdinalIgnoreCase))
                    return text;

            Match last = matches[done.Length - 1];
            return text[(last.Index + last.Length)..].TrimStart(' ', '?', '.', '!', ',', ';', ':');
        }

        /// <summary>
        /// True when this is the question just answered, heard again or corrected by
        /// recognition, rather than a new one: nearly every word of it is already in
        /// that question and it is no longer.
        /// </summary>
        internal static bool IsRevisionOf(string candidate, string lastQuestion)
        {
            string[] said = Words(candidate);
            string[] last = Words(lastQuestion);
            if (said.Length < 3 || last.Length == 0 || said.Length > last.Length) return false;
            var known = new HashSet<string>(last);
            int inLast = said.Count(known.Contains);
            return inLast >= Math.Ceiling(said.Length * 0.9);
        }
    }
}
