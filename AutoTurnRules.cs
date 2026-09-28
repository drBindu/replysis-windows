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

        internal static bool IsExplicitExtension(string text) =>
            Regex.IsMatch((text ?? "").Trim(),
                @"^(?:and|also|but|including|with|without|especially|specifically|for example|in particular|what about|how about)\b",
                RegexOptions.IgnoreCase) && !AsksItsOwnQuestion(text ?? "") &&
            !IsSpelledOutNoise(text ?? "") && Words(text ?? "").Length >= 2;

        internal static bool CanContinueAfterPause(string text, DateTime submitted, DateTime firstWords) =>
            StartedSoonEnoughToContinue(submitted, firstWords) ||
            (submitted != DateTime.MinValue && firstWords >= submitted &&
             firstWords - submitted <= TimeSpan.FromSeconds(8) && IsExplicitExtension(text));

        /// <summary>
        /// Whether recognition has genuinely stopped changing. Providers often
        /// repeat the same partial text while audio is still arriving; text
        /// stability alone therefore cuts a live sentence in half.
        /// </summary>
        internal static bool RecognitionSettled(
            DateTime now, DateTime transcriptChanged, DateTime lastPartial,
            DateTime lastFinal, DateTime listeningStarted, bool providerEnded)
        {
            if (providerEnded) return true;
            bool finalForTurn = lastFinal >= listeningStarted && lastFinal >= lastPartial;
            if (finalForTurn) return true;
            if (transcriptChanged == DateTime.MinValue || now - transcriptChanged < TimeSpan.FromSeconds(2))
                return false;
            return lastPartial == DateTime.MinValue || now - lastPartial >= TimeSpan.FromSeconds(2);
        }

        /// <summary>
        /// Speech recognizers often put a full stop on a request when the speaker
        /// merely pauses: "Could you explain dependency injection? ...and describe
        /// when you use it?"  Treating '.' like a real question mark produced two
        /// answers (and two charges) for one interviewer turn. The provider may infer
        /// either '.' or '?' from the same audio, so punctuation cannot safely shorten
        /// the continuation grace period. Any new partial resets transcriptChanged,
        /// so the second clause cancels the pending submission naturally.
        /// </summary>
        internal static int CompletionGraceMs(int continuationGraceMs) => continuationGraceMs;

        internal static bool HoldCompletedQuestionOnEmptyRestart(
            bool autoMode, string shownQuestion, string incomingTranscript) =>
            autoMode && string.IsNullOrWhiteSpace(incomingTranscript) &&
            !string.IsNullOrWhiteSpace(shownQuestion);

        internal static bool StartedSoonEnoughToContinue(DateTime lastSubmitUtc, DateTime firstWordsAfterUtc) =>
            lastSubmitUtc != DateTime.MinValue &&
            firstWordsAfterUtc != DateTime.MinValue &&
            firstWordsAfterUtc >= lastSubmitUtc &&
            firstWordsAfterUtc - lastSubmitUtc <= ContinuationStartWindow;

        private static string[] Words(string s) =>
            Regex.Matches((s ?? "").ToLowerInvariant(), @"[\p{L}\p{N}']+").Select(m => m.Value).ToArray();

        internal static string UnconsumedTranscript(string captured, string consumed)
        {
            string[] done = Words(consumed);
            var matches = Regex.Matches(captured ?? "", @"[\p{L}\p{N}']+");
            if (done.Length == 0 || matches.Count < done.Length) return captured ?? "";
            for (int i = 0; i < done.Length; i++)
                if (!string.Equals(matches[i].Value, done[i], StringComparison.OrdinalIgnoreCase))
                    return captured ?? "";
            if (matches.Count == done.Length) return "";
            Match last = matches[done.Length - 1];
            return captured![(last.Index + last.Length)..].TrimStart(' ', '?', '.', '!', ',', ';', ':');
        }

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

        /// <summary>
        /// A recogniser-confirmed utterance that contains enough substance to answer even
        /// when punctuation or the first word was transcribed imperfectly.
        ///
        /// This is deliberately used only for system-audio Auto mode after the speech
        /// provider emits an explicit utterance boundary. It recovers real recruiter
        /// prompts such as "Your experience with Kubernetes" and "I'd like to hear about
        /// the migration" without turning mic noise, acknowledgements, or half a request
        /// into paid answer calls.
        /// </summary>
        internal static bool IsSubstantiveBoundaryUtterance(string text)
        {
            string value = text ?? "";
            string[] words = Words(value);
            if (words.Length < 3 || value.Trim().Length < 12) return false;
            if (IsSpelledOutNoise(value) || IsBareRequestOpener(value)) return false;

            string normalized = string.Join(" ", words);
            if (normalized is "okay thank you" or "yes thank you" or "that is fine" or
                              "that sounds good" or "nice to meet you" or "thanks for that")
                return false;

            // An utterance boundary is useful evidence, not permission to answer a
            // paragraph of background conversation.
            int stops = value.Count(c => c is '.' or '?' or '!');
            return stops <= 3;
        }

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
            // Same vocabulary does not imply the same request. Reversing the
            // operands or removing a negation changes the answer.
            static bool Negative(string word) => word is "not" or "never" or "no" || word.EndsWith("n't");
            if (said.Any(Negative) != last.Any(Negative)) return false;
            var interrogatives = new HashSet<string> { "what", "why", "how", "when", "where", "who", "which" };
            if (interrogatives.Contains(said[0]) && said[0] != last[0]) return false;
            int cursor = 0;
            foreach (string word in said)
            {
                while (cursor < last.Length && last[cursor] != word) cursor++;
                if (cursor == last.Length) return false;
                cursor++;
            }
            return true;
        }
    }
}
