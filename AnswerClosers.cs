using System;
using System.Text.RegularExpressions;

namespace InterviewCopilot
{
    /// <summary>
    /// Takes the closing line off an answer when it is the model handing the conversation back instead of finishing:
    /// "Let me know if you'd like more detail.", "Would you like me to go deeper?", "Does that make sense?", or a
    /// question put to the interviewer ("What does your team use?").
    ///
    /// The person reading the answer says it out loud. A candidate who ends every answer by inviting the interviewer
    /// to ask for more, or by turning the question around, sounds like a support chat and not like someone who has
    /// answered (owner, 2026-10-06: "it is asking a reverse question, like let me know more"). The prompt now says to
    /// stop on the last point; this is the net under it, because a prompt is a request and the model sometimes ignores it.
    ///
    /// Deliberately narrow. A sentence goes only when it starts like a hand-back, so advice that happens to begin with
    /// "If you want fast lookups, use a hash map." is never touched, and the only sentence of an answer is never removed.
    /// Code is left exactly as it is.
    /// </summary>
    internal static class AnswerClosers
    {
        private const string MoreMarker = "MORE TO SAY";

        // A sentence that starts like an offer to say more or a check that the interviewer is satisfied.
        private static readonly Regex Offer = new(
            @"^(?:and |so |but |also |anyway,? |okay,? )?(?:please )?(?:" +
            @"let me know|feel free to (?:ask|reach|let|interrupt|stop)|" +
            @"happy to (?:elaborate|expand|go|dive|walk|share|explain|discuss|clarify|help|answer)|" +
            @"glad to (?:elaborate|expand|go|dive|walk|share|explain|discuss|clarify|answer)|" +
            @"i(?:'d| would) be (?:happy|glad) to|" +
            @"i can (?:also )?(?:go (?:deeper|into|further|over)|dive|elaborate|expand|walk (?:you )?through|give (?:you )?(?:more|an example)|share more|explain (?:more|further))|" +
            @"would you like|do you want (?:me|to hear|more)|want me to|" +
            @"does (?:that|this) (?:make sense|help|answer|cover|sound|clear)|did (?:that|this) (?:make sense|help|answer|cover)|" +
            @"is there (?:anything|something|a particular|any)|" +
            @"hope (?:that|this) (?:helps|answers|clears)|how does (?:that|this) sound|" +
            @"if you(?:'d| would)? (?:like|want|prefer),? (?:me )?to |if you(?:'d| would) like,? i |if you want,? i |" +
            @"if that(?:'s| is) (?:helpful|useful))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SentenceBreak = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);

        /// <param name="allowClosingQuestion">
        /// True when the interviewer has just invited the candidate's questions. Then a closing question IS the answer
        /// and stays; offers to say more are still taken off.
        /// </param>
        internal static string StripTrailingOffer(string text, bool allowClosingQuestion = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            // Inside code, or ending on code: nothing here is prose to tidy.
            if (CountFences(text) % 2 == 1) return text;
            int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            string head = lastFence >= 0 ? text[..(lastFence + 3)] : "";
            string prose = lastFence >= 0 ? text[(lastFence + 3)..] : text;
            if (string.IsNullOrWhiteSpace(prose)) return text;

            int marker = prose.IndexOf(MoreMarker, StringComparison.OrdinalIgnoreCase);
            string spoken = marker >= 0 ? prose[..marker] : prose;
            string more = marker >= 0 ? prose[marker..] : "";

            string newSpoken = StripSpoken(spoken, allowClosingQuestion);
            string newMore = more.Length > 0 ? StripBullets(more) : "";
            if (ReferenceEquals(newSpoken, spoken) && newMore == more) return text;

            string joined = newSpoken;
            if (newMore.Length > 0) joined = newSpoken.TrimEnd() + (newSpoken.Length > 0 ? "\n\n" : "") + newMore;
            return head + joined;
        }

        private static string StripSpoken(string spoken, bool allowClosingQuestion)
        {
            string trimmed = spoken.TrimEnd();
            string result = trimmed;
            while (true)
            {
                int start = LastSentenceStart(result);
                if (start <= 0) break;                       // never remove the only sentence
                string last = result[start..].Trim();
                bool question = last.TrimEnd('"', '\'', ')', '”', '’').EndsWith('?');
                bool drop = Offer.IsMatch(last) || (question && !allowClosingQuestion);
                if (!drop) break;
                result = result[..start].TrimEnd();
            }
            return result.Length == trimmed.Length ? spoken : result;
        }

        // Offers at the end of the bullets under MORE TO SAY.
        private static string StripBullets(string more)
        {
            string[] lines = more.Replace("\r\n", "\n").Split('\n');
            int keep = lines.Length;
            while (keep > 1)
            {
                string line = lines[keep - 1].Trim().TrimStart('•', '-', '*', ' ').Trim();
                if (line.Length == 0 || Offer.IsMatch(line)) keep--;
                else break;
            }
            return keep == lines.Length ? more : string.Join("\n", lines, 0, keep).TrimEnd();
        }

        private static int LastSentenceStart(string s)
        {
            Match last = null!;
            foreach (Match m in SentenceBreak.Matches(s)) last = m;
            return last == null ? 0 : last.Index + last.Length;
        }

        private static int CountFences(string s)
        {
            int count = 0, at = 0;
            while ((at = s.IndexOf("```", at, StringComparison.Ordinal)) >= 0) { count++; at += 3; }
            return count;
        }
    }
}
