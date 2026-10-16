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

        // ── How soon a plainly finished question is sent ──────────────────────────────
        //
        // Recorded questions played into the real app (2026-09-29) showed Auto waiting
        // 1.1 to 2.6 seconds, 1.8 on average, AFTER the speech service had already said the
        // speaker stopped, before it asked. That wait was ours: the service had already sat
        // through 300 ms of silence to say so, and Auto then asked for 1.5 s more.
        //
        // The service says it twice. "speech final" arrives about 0.3 s after the last word;
        // "utterance end" about a second after it. A question that is plainly finished (a
        // question mark, or a request like "Tell me about yourself.") needs only a short
        // confirmation on top of either one. A speaker who pauses a lot mid sentence still
        // gets their own longer floor, and a tail that does arrive is merged as a
        // continuation, exactly as before.
        //
        // 450 ms, down from 650 (2026-10-01, owner: "as much speed as instant"). Measured with recorded questions, the
        // time from the speaker's last word to the first word of the answer is the speech service saying they stopped
        // (about 0.55 s), this confirmation, and the answer starting (0.25 to 0.4 s). The confirmation is the only
        // part that is ours. The cost of too short a wait is a tail that arrives just after the question was sent,
        // which re-answers the whole question, so the first time that happens in an interview the wait goes back to
        // the cautious 650 ms for the rest of it (SpeechFinalConfirmAfterTailMs).
        // 250 ms, down from 450 (2026-10-14, owner: "I want it instant, 0.3"). A tail that arrives after the answer started is
        // merged and the answer redone, and after the first such tail the wait goes back to 650 ms for that interview.
        internal const int SpeechFinalConfirmMs = 250;
        internal const int SpeechFinalConfirmAfterTailMs = 650;
        internal const int UtteranceEndConfirmMs = 250;
        internal const int AmbiguousNoEndpointWaitMs = 1200;

        /// <summary>
        /// The speech service called the end of the speech, and nothing has been heard since.
        /// Ordered by the ticks the engine's own lines arrived with, so the 40 ms poll of the
        /// transcript file cannot make a fresh signal look old.
        /// </summary>
        internal static bool SpeechFinalIsLatest(
            long speechFinalTicks, long lastPartialTicks, long lastFinalTicks, DateTime listeningStarted) =>
            speechFinalTicks > 0 &&
            speechFinalTicks >= lastPartialTicks &&
            speechFinalTicks >= lastFinalTicks &&
            speechFinalTicks >= listeningStarted.Ticks;

        /// <summary>
        /// How long a plainly finished question must sit unchanged before it is sent.
        /// <paramref name="paceFloorMs"/> is the speaker's own longest pause inside a sentence,
        /// so someone who talks slowly is not cut off; <paramref name="ceilingMs"/> caps it.
        /// </summary>
        internal static int QuickSendWaitMs(bool utteranceEnded, int paceFloorMs, int ceilingMs, int tailMergesThisInterview = 0) =>
            Math.Min(ceilingMs, Math.Max(
                utteranceEnded ? UtteranceEndConfirmMs
                               : (tailMergesThisInterview > 0 ? SpeechFinalConfirmAfterTailMs : SpeechFinalConfirmMs),
                paceFloorMs));

        /// <summary>
        /// An unpunctuated question needs more room for a follow-up clause than a
        /// plainly finished one. A fresh provider endpoint is evidence that the
        /// speaker stopped, so it can use a shorter wait than the no-endpoint
        /// fallback. Keep the learned mid-sentence pause and increase the wait
        /// after this interview has already produced a late continuation.
        /// </summary>
        internal static int AmbiguousWaitMs(bool utteranceEnded, bool speechFinalEnded,
            int paceFloorMs, int ceilingMs, int tailMergesThisInterview = 0)
        {
            int minimum = utteranceEnded ? 650 : speechFinalEnded ? 850 : AmbiguousNoEndpointWaitMs;
            if (tailMergesThisInterview > 0 && (utteranceEnded || speechFinalEnded))
                minimum = Math.Max(minimum, utteranceEnded ? 850 : 1100);
            return Math.Min(ceilingMs, Math.Max(minimum, paceFloorMs));
        }

        // A recognizer may report an end after a greeting or a bare opener and
        // then deliver the actual question. That gap says little about how long
        // the interviewer pauses *inside a usable question*. Treating it as a
        // pace sample made the first real question wait over two seconds in a
        // Practice test, despite a confirmed final and an ordinary fast path.
        internal static bool CanLearnPaceFrom(string previousFragment) =>
            IsLikelyCompleteQuestion(PromptBuilder.NormalizeInterviewerQuestion(previousFragment));

        // With microphone and system audio mixed, timing alone cannot identify
        // the speaker. A candidate's short statement after an answer must not
        // become a paid re-answer to the previous question. In mixed mode only
        // a clear extension is eligible for automatic merging; the system-only
        // Interview mode retains its existing continuation behavior.
        //
        // A question that was sent ending on "and", "or", "also"... was cut mid-sentence: the speaker paused and then
        // carried on. Its continuation is the rest of the SAME question whatever it begins with, so it merges too.
        // Found in a real Practice test (2026-10-14): "Tell me what is Java and also tell me about your experience. And"
        // was answered, then "what work you've done before" was answered again as a separate question.
        internal static bool CanMergeContinuationFromMixedAudio(string candidate, bool mixedAudio, string previousQuestion = "") =>
            !mixedAudio || IsExplicitExtension(candidate) || EndsOnConnector(previousQuestion);

        /// <summary>
        /// The words point back at what was just asked ("that language", "it", "those projects") rather than at something new.
        /// Only consulted for a follow-up within seconds of a question that stopped on "and"/"or".
        /// </summary>
        internal static bool RefersBack(string text) =>
            Regex.IsMatch(text ?? "", @"\b(?:that|this|it|its|those|these|them|same|there)\b", RegexOptions.IgnoreCase);

        /// <summary>
        /// A short follow-up about the question just answered, whatever it starts with: "Give me an example of that",
        /// "Why did you choose it?", "Tell me more about that project", "Can you elaborate?". The owner (2026-10-14): when the
        /// interviewer asks one more thing about the same question, the answer has to be updated, not a new card started.
        /// A new topic ("What work have you done before?") does not match and gets its own answer.
        /// </summary>
        internal static bool IsFollowUpOnPrevious(string text)
        {
            string t = (text ?? "").Trim();
            int words = Regex.Matches(t, @"[\p{L}\p{N}']+").Count;
            if (words < 2 || words > 14) return false;
            if (Regex.IsMatch(t, @"\bwhat(?:'s| is) it like\b", RegexOptions.IgnoreCase)) return false;
            return Regex.IsMatch(t,
                @"\b(?:elaborate|expand on|more about (?:that|this|it|them)|more detail|go deeper|for example|an example|example of|tell me more)\b" +
                @"|\b(?:about|of|with|on|for|in|behind|use|used|using|choose|chose|like|prefer|explain|describe|do|did|does)\s+(?:that|this|it|those|these|them)\b" +
                @"|\b(?:that|this|those|these|the same)\s+(?:language|project|projects|tool|tools|framework|one|role|company|technology|approach|example|experience|part|feature|system|team|problem|code)\b",
                RegexOptions.IgnoreCase);
        }

        /// <summary>The text stops on a joining word, so the sentence plainly goes on.</summary>
        internal static bool EndsOnConnector(string text) =>
            Regex.IsMatch((text ?? "").Trim(),
                @"\b(?:and|or|but|also|plus|then|with|because|like)[\s.,;:!?…-]*$", RegexOptions.IgnoreCase);

        // The mixed Practice stream can have microphone audio in flight even when
        // Deepgram's latest text still looks settled. This is a short, bounded
        // guard against sending an answer between the sound and its next partial.
        // It does not change the normal question wait when no such audio exists.
        internal static bool HoldForInFlightMicAudio(DateTime now, DateTime transcriptChanged,
            long lastMicActivityTicks, int requiredSilenceMs, bool microphoneInStream)
        {
            if (!microphoneInStream || lastMicActivityTicks <= transcriptChanged.Ticks)
                return false;
            DateTime lastActivity = new(lastMicActivityTicks, DateTimeKind.Utc);
            TimeSpan sinceActivity = now - lastActivity;
            TimeSpan sinceTranscript = now - transcriptChanged;
            // 900 ms, up from 450: the words for a sound arrive 0.5 to 1 s after it. In a real test (2026-10-14) a question
            // was sent 0.5 s after the speaker started again ("...experience. And"), the new words landing a moment later.
            return sinceActivity >= TimeSpan.Zero && sinceActivity < TimeSpan.FromMilliseconds(900) &&
                   sinceTranscript < TimeSpan.FromMilliseconds(requiredSilenceMs + 1000);
        }

        /// <summary>
        /// Whether a gap between two changes of the transcript was the speaker pausing.
        ///
        /// It used to be assumed. The longest gap between updates became "this speaker's pause",
        /// and the wait before answering was raised to match. But the speech service sends an
        /// update roughly every second on a slow connection, so ordinary delivery gaps were
        /// learned as pauses, and questions that plainly ended waited 1.2 to 1.6 s for nothing
        /// (recorded questions through the real app, 2026-09-29).
        ///
        /// When the service reports endpoints, only a gap it heard a pause in counts: it called
        /// an end (a speech final or an utterance end) after the previous change, and the
        /// speaker then carried on. That last part matters. The call to end the speech arrives
        /// with the final words themselves, so when the change that closes the gap IS that
        /// final, the call came at the gap's end, not in the middle of it, and nobody paused.
        /// A service that reports neither (the fallback engines) is judged as it always was.
        /// </summary>
        internal static bool GapWasASpeakerPause(
            bool serviceReportsEndpoints, long speechFinalTicks, long utteranceEndTicks,
            DateTime previousChange, DateTime now)
        {
            if (!serviceReportsEndpoints) return true;
            long after = previousChange.Ticks - TimeSpan.FromMilliseconds(100).Ticks;   // the file is polled every 40 ms
            long before = now.Ticks - TimeSpan.FromMilliseconds(150).Ticks;             // words that came later, not the same instant
            return (speechFinalTicks > 0 && speechFinalTicks >= after && speechFinalTicks <= before) ||
                   (utteranceEndTicks > 0 && utteranceEndTicks >= after && utteranceEndTicks <= before);
        }

        /// <summary>
        /// How many 20 ms reads with no change the last look at the transcript needs before
        /// the question is sent. When the service has already called the end of the speech
        /// nothing is in flight, so one read is enough; otherwise 300 ms, or 800 ms when the
        /// sentence plainly is not over.
        ///
        /// 300 and not 100 since 2026-10-16: the speech service (Flux) updates the words every 240 ms and
        /// runs about half a second behind the voice, so 100 ms of no change is the gap between two
        /// updates, not the end of the words. A real log showed the question sent with 78 of its 98
        /// characters and shown that way on screen. Only reached when no end of speech has been called yet.
        /// </summary>
        internal static int FlushStableChecks(bool speechFinalIsLatest, bool looksUnfinished) =>
            looksUnfinished ? 40 : speechFinalIsLatest ? 1 : 15;

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

        /// <summary>How long a pause must be before words already judged not to be a question are set aside.</summary>
        internal static readonly TimeSpan StaleRejectedSpeechPause = TimeSpan.FromSeconds(3);

        private static readonly HashSet<string> JoiningWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "or", "and", "but", "nor", "to", "of", "for", "with", "without", "from", "into",
            "in", "on", "at", "by", "about", "over", "between", "the", "a", "an", "my", "our",
            "your", "their", "its", "than", "because", "while", "if", "like", "is", "are", "was",
        };

        /// <summary>Ends on a word no finished sentence ends on, punctuated or not.</summary>
        internal static bool EndsOnJoiningWord(string text)
        {
            string[] w = Words(text);
            return w.Length > 0 && JoiningWords.Contains(w[^1]);
        }

        /// <summary>
        /// How many words at the end of what was already answered the recogniser
        /// may still rewrite. The snapshot is taken while the last words can still
        /// be a partial result; the final version often differs ("rest full"
        /// becomes "RESTful").
        /// </summary>
        private const int RevisableTailWords = 10;

        /// <summary>
        /// What was said after the part already answered.
        ///
        /// In Auto the transcript is never cleared between turns, so it holds the
        /// whole interview, and the answered part is found by matching words from
        /// the start. That match used to be exact: one word revised by the
        /// recogniser after the snapshot, and the whole interview so far came back
        /// as "new" speech, old questions included (2026-09-28 audit). A mismatch
        /// confined to the last few answered words is now treated as the revision
        /// it is. A transcript that differs earlier than that is a fresh one (the
        /// engine restarted) and is returned whole, as before.
        /// </summary>
        internal static string UnconsumedTranscript(string captured, string consumed)
        {
            string[] done = Words(consumed);
            var matches = Regex.Matches(captured ?? "", @"[\p{L}\p{N}']+");
            if (done.Length == 0) return captured ?? "";

            int same = 0;
            while (same < done.Length && same < matches.Count &&
                   string.Equals(matches[same].Value, done[same], StringComparison.OrdinalIgnoreCase))
                same++;

            int cut;
            if (same == done.Length)
            {
                cut = done.Length;
            }
            // A revision rewrites the end of what was answered, never most of it.
            // Short answered text that barely matches is a fresh transcript.
            else if (same >= done.Length - RevisableTailWords && same * 2 >= done.Length && same > 0)
            {
                // Align the revised tail: find each answered word a little further on.
                int pos = same, lastFound = -1;
                for (int i = same; i < done.Length; i++)
                {
                    for (int j = pos; j < Math.Min(matches.Count, pos + 3); j++)
                    {
                        if (string.Equals(matches[j].Value, done[i], StringComparison.OrdinalIgnoreCase))
                        {
                            lastFound = j;
                            pos = j + 1;
                            break;
                        }
                    }
                }
                int estimate = lastFound >= 0 ? lastFound + 1 : done.Length;

                // The answered question ended where the recogniser put its full
                // stop or question mark once it finalised. Prefer that boundary
                // when it sits near the estimate: word counts change in a
                // revision ("rest full" -> "RESTful"), punctuation does not lie.
                int best = -1;
                for (int j = Math.Max(0, same - 1); j < matches.Count; j++)
                {
                    int after = matches[j].Index + matches[j].Length;
                    int next = j + 1 < matches.Count ? matches[j + 1].Index : captured!.Length;
                    if (captured![after..next].IndexOfAny(new[] { '?', '.', '!' }) < 0) continue;
                    int boundary = j + 1;
                    if (Math.Abs(boundary - estimate) <= 2 &&
                        (best < 0 || Math.Abs(boundary - estimate) < Math.Abs(best - estimate)))
                        best = boundary;
                    if (boundary > estimate + 2) break;
                }
                cut = Math.Min(best >= 0 ? best : estimate, matches.Count);
            }
            else
            {
                return captured ?? "";
            }

            if (cut >= matches.Count) return "";
            if (cut == 0) return captured ?? "";
            Match last = matches[cut - 1];
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
                @"(?: (?:me|us))?(?: (?:about|through|a bit about|more about|how|what|why))?" +
                @"(?: a time(?: when| where| that)?)?$");

        /// <summary>
        /// A recognizer may punctuate an opening clause as a complete question
        /// when the interviewer pauses: "What is?" or "Why did you?". Neither
        /// contains the subject needed for a useful answer. Hold these exact
        /// openers without slowing a finished short question like "What is Java?".
        /// </summary>
        internal static bool IsBareQuestionOpener(string question) =>
            Regex.IsMatch((question ?? "").Trim().TrimEnd('?', '.', '!', ',').ToLowerInvariant(),
                @"^(?:so |and |okay |ok |now |alright )?" +
                @"(?:what (?:is|are|was|were|do|does|did)|" +
                @"why (?:do|does|did|would|should|can|could) (?:you|we|they)|" +
                @"how (?:do|does|did|would|should|can|could) (?:you|we|they)|" +
                @"(?:can|could|would|will|should|do|did|have|are) you(?: please)?)$");

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
            if (IsSpelledOutNoise(value) || IsBareRequestOpener(value) || IsBareQuestionOpener(value)) return false;

            string normalized = string.Join(" ", words);
            if (normalized is "okay thank you" or "yes thank you" or "that is fine" or
                              "that sounds good" or "nice to meet you" or "thanks for that")
                return false;
            // This fallback only fired by luck until 2026-09-28: a timing race
            // expired the provider's end-of-speech signal before a punctuated
            // utterance could be sent. With that fixed it would have answered
            // "Great, thanks for that." and "Let me pull up my notes.", replacing
            // the answer the candidate was still reading.
            if (PromptBuilder.IsAcknowledgement(value) || IsInterviewerAside(value))
                return false;
            // Only a prompt aimed at the candidate. The interviewer describing
            // the role, or anything else audible on the computer, is not one.
            if (!AddressesTheCandidate(value))
                return false;

            // An utterance boundary is useful evidence, not permission to answer a
            // paragraph of background conversation.
            int stops = value.Count(c => c is '.' or '?' or '!');
            return stops <= 3;
        }

        // Practice audio mixes the meeting with the candidate's microphone, so a
        // generic provider boundary is not enough to answer arbitrary statements.
        // These elliptical prompts explicitly address the candidate, however, and
        // are common in interviews even when STT supplies no question mark.
        internal static bool IsDirectBoundaryPromptInMixedAudio(string text)
        {
            if (!IsSubstantiveBoundaryUtterance(text)) return false;
            string core = PromptBuilder.StripLeadingDiscourse(DropLeadingAcknowledgements(text));
            return Regex.IsMatch(core, @"^(?:your (?:experience|background|resume|cv|projects?|work|skills|approach|thoughts|career)|you (?:mentioned|described|built|led|worked))\b",
                RegexOptions.IgnoreCase);
        }

        internal static string IncompleteQuestionReason(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "empty transcript";
            if (IsBareRequestOpener(text) || IsBareQuestionOpener(text)) return "unfinished opener";
            if (IsSpelledOutNoise(text)) return "recognition noise";
            if (IsInterviewerAside(text)) return "interviewer aside";
            if (PromptBuilder.IsAcknowledgement(text) || PromptBuilder.IsGreeting(text) ||
                PromptBuilder.IsSmallTalk(text)) return "pleasantry";
            if (!AddressesTheCandidate(text)) return "not candidate-directed";
            return "no complete question pattern";
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
        /// <summary>
        /// A question put to the candidate, as opposed to a line of our own answer
        /// being read aloud.
        ///
        /// The read-back guard compares vocabulary with the answer on screen, and a
        /// follow-up about that answer shares its vocabulary by nature: "Why did you
        /// move from Baxter to Ava?" scored 0.80 against an answer naming both, and
        /// was ignored as reading back. In Practice mode that left Auto silent until
        /// the next unrelated question, minutes later (tester report, 2026-09-28).
        ///
        /// What separates the two is who is being spoken to. Our answers are in
        /// the first person; an interviewer's question opens like a question and
        /// addresses "you". A bullet read aloud that happens to open with "How"
        /// ("How I prioritize multiple concurrent tasks") is still first person
        /// straight after the opener, so it stays a read-back.
        /// </summary>
        /// <summary>
        /// Whether Auto should treat this as a question worth answering now.
        ///
        /// Moved here from MainWindow (2026-09-28) so it can be tested, and taught
        /// what comes in front of a question in a real interview. "Got it. So walk
        /// me through your resume." used to fail twice: two sentences read as
        /// background talk, and "So" is not a question word. The only thing that
        /// could rescue it was the system-audio boundary fallback, which a timing
        /// race kept from firing on punctuated speech. So the interviewer's
        /// acknowledgement and "So, / Now, / Next question," are set aside first,
        /// and requests that are not phrased as questions ("Let's talk about your
        /// Kubernetes work", "I'd like to hear about...") count as questions.
        /// </summary>
        internal static bool IsLikelyCompleteQuestion(string question)
        {
            if (string.IsNullOrWhiteSpace(question)) return false;

            string core = PromptBuilder.StripLeadingDiscourse(DropLeadingAcknowledgements(question));
            if (core.Length == 0) return false;
            if (IsInterviewerAside(core)) return false;
            if (IsBareRequestOpener(core) || IsBareQuestionOpener(core)) return false;

            string[] words = Words(core);
            if (words.Length == 0) return false;

            string normalized = string.Join(" ", words);
            if (normalized is "okay" or "okay sir" or "yes" or "yes sir" or "no" or
                              "no sir" or "thanks" or "thank you" or "hello" or "hi" or
                              "la la la")
                return false;

            bool hasQuestionMark = core.Contains('?');
            string first = words[0];

            if (CompleteQuestionStarters.Contains(first))
                return words.Length >= 2 || (hasQuestionMark && first is "what" or "why" or "how");

            if (CompleteQuestionCommands.Contains(first))
            {
                if (first == "tell" && words.Length == 2 && words[1] == "me") return false;
                return words.Length >= 2;
            }

            // Requests that are not phrased as questions, and are how many
            // interviewers actually ask: "Let's talk about...", "I'd like to hear
            // about...", "I'm curious about...".
            if (words.Length >= 4 && Regex.IsMatch(normalized,
                    @"^(?:let's|let us) (?:talk|discuss|move|go|dive|start|switch|turn|focus|look|get)\b|" +
                    @"^(?:i'd|i would|i) (?:really )?(?:like|love|want|wanted) to (?:hear|know|understand|learn|see|talk|discuss|ask)\b|" +
                    @"^(?:i'm|i am) (?:curious|interested)\b"))
                return true;

            if (hasQuestionMark) return words.Length >= 2;

            // One sentence of context, then the question that leans on it: "Your
            // resume says you led a team of six. Walk me through that." The
            // normaliser keeps that sentence on purpose, so judge the last one.
            string[] sentences = Regex.Split(core, @"(?<=[?.!])\s+")
                                      .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            if (sentences.Length is >= 2 and <= 3 && Words(sentences[^1]).Length >= 3 &&
                PromptBuilder.IsCompleteInterviewQuestion(sentences[^1]))
                return true;

            // Multiple declarative sentences are normally background conversation, not
            // a question. Question starters and coding/command requests returned above,
            // so this guard no longer blocks a valid question followed by a constraint.
            int periodCount = core.Count(character => character == '.');
            if (periodCount >= 2) return false;

            char last = core.TrimEnd()[^1];
            bool hasClosingPunctuation = last is '.' or '!';
            // A statement is a prompt only when it is aimed at the candidate
            // ("Your resume mentions Kafka.", "I see you led the migration.").
            // Any five-word sentence used to count, so a film playing on the
            // computer was answered line by line in a live Practice test, and
            // an interviewer describing their own team got an answer too.
            return words.Length >= 5 && core.Length >= 20 && hasClosingPunctuation &&
                   AddressesTheCandidate(core);
        }

        /// <summary>Speaks to the candidate: second person, or a request.</summary>
        internal static bool AddressesTheCandidate(string text)
        {
            string[] w = Words(text);
            if (w.Any(SecondPerson.Contains) || w.Contains("u")) return true;
            string n = string.Join(" ", w);
            return Regex.IsMatch(n,
                @"^(?:let's|let us) (?:talk|discuss|move|go|dive|start|switch|turn|focus|look|get)\b|" +
                @"^(?:i'd|i would|i) (?:really )?(?:like|love|want|wanted) to (?:hear|know|understand|learn|see|talk|discuss|ask)\b|" +
                @"^(?:i'm|i am) (?:curious|interested)\b");
        }

        /// <summary>
        /// The interviewer talking to themselves or asking for a moment: "Let me
        /// pull up my notes", "One second", "Bear with me". Not a question, and
        /// answering it replaces the answer on screen.
        /// </summary>
        internal static bool IsInterviewerAside(string text) =>
            Regex.IsMatch(PromptBuilder.StripLeadingDiscourse(DropLeadingAcknowledgements(text ?? "")).ToLowerInvariant(),
                @"^(?:let me (?:see|check|look|pull|find|open|share|just|grab|note|write|make)|" +
                @"(?:one|just a|give me a|wait a) (?:second|sec|moment|minute)|hold on|bear with me|" +
                @"sorry,? (?:one|just a|give me a) (?:second|sec|moment|minute)|i'm just|i am just|" +
                @"my (?:dog|kid|internet|connection|camera|mic)\b)");

        /// <summary>Removes "Got it.", "Great, thanks for that." and greetings from the front.</summary>
        internal static string DropLeadingAcknowledgements(string text)
        {
            string[] sentences = Regex.Split((text ?? "").Trim(), @"(?<=[?.!])\s+");
            int i = 0;
            while (i < sentences.Length - 1 && PromptBuilder.IsAcknowledgement(sentences[i]))
                i++;
            return string.Join(" ", sentences.Skip(i)).Trim();
        }

        private static readonly HashSet<string> CompleteQuestionStarters = new(StringComparer.OrdinalIgnoreCase)
        {
            "what", "why", "how", "when", "where", "who", "which", "can", "could",
            "would", "will", "do", "does", "did", "are", "is", "was", "were",
            "have", "has", "should", "what's", "whats", "how's", "where's", "who's",
        };

        private static readonly HashSet<string> CompleteQuestionCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "tell", "explain", "describe", "walk", "share", "discuss", "design",
            "implement", "compare", "define", "introduce", "summarize", "summarise", "write",
            "create", "build", "code", "program", "solve", "develop", "generate", "show",
            "talk", "take", "give", "elaborate", "list", "name",
        };

        internal static bool IsQuestionToTheCandidate(string text)
        {
            string[] w = Words(text);
            if (w.Length < 3) return false;
            if (!QuestionOpeners.Contains(w[0])) return false;
            if (FirstPerson.Contains(w[1])) return false;
            return w.Any(SecondPerson.Contains);
        }

        private static readonly HashSet<string> QuestionOpeners = new(StringComparer.Ordinal)
        {
            "what", "what's", "whats", "why", "how", "when", "where", "which", "who", "whose",
            "can", "could", "would", "will", "do", "does", "did", "are", "is", "was", "were",
            "have", "has", "should", "tell", "walk", "explain", "describe", "share", "give",
            "talk", "so", "and", "okay", "ok", "now",
        };
        private static readonly HashSet<string> FirstPerson = new(StringComparer.Ordinal)
        {
            "i", "i'm", "i've", "i'd", "i'll", "my", "we", "we've", "we're", "our",
        };
        private static readonly HashSet<string> SecondPerson = new(StringComparer.Ordinal)
        {
            "you", "your", "yours", "yourself", "you've", "you're", "you'd", "you'll",
        };

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
        private static readonly Regex EmbeddedClause = new(
            @"^(?:what|how|why|when|where|which|who)\s+" +
            @"(?:(?!(?:am|is|are|was|were|do|does|did|have|has|had|can|could|will|would|shall|should|may|might|must)\b)[a-z'\-]+\s+){0,3}" +
            @"(?:(?:i|you|we|they|he|she)(?:'d|'ll|'ve|'re)?|your team|the team|your company|the company)\s+[a-z']{2,}",
            RegexOptions.Compiled);

        internal static bool AsksItsOwnQuestion(string tail)
        {
            string q = Regex.Replace((tail ?? "").Trim().ToLowerInvariant(),
                @"^(?:and|or|but|also|plus|so|then|okay|ok)\s+", "");
            if (q.Length == 0) return false;

            // Pointing back: the subject is the question before, not a new one.
            if (Regex.IsMatch(q, @"\b(it|that|this|them|those|these|there|the same)\b")) return false;

            // A clause that is still part of the sentence before it. "Can you tell me how you would monitor a service
            // ... and what alerts YOU WOULD set up?" is one question with a long pause in the middle, and the second
            // half was being answered as a question of its own. The grammar tells them apart: a new question puts the
            // verb ahead of its subject ("what alerts WOULD YOU set up?"), and only a clause inside a sentence can put
            // the subject first ("what alerts you would set up").
            if (EmbeddedClause.IsMatch(q.Replace('\u2019', '\'')) ) return false;

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
