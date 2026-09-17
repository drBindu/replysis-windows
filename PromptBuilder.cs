using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace InterviewCopilot
{
    public static class PromptBuilder
    {
        // ── Per-session conversation history (max 80 turns = full interview) ──
        private static readonly List<(string Q, string A)> History = new();
        private const int MaxHistoryTurns = 20;
        private const int MaxHistoryQuestionChars = 4_000;
        private const int MaxHistoryAnswerChars = 8_000;
        private const int MaxPromptHistoryTurns = 12;

        // ── Topics + companies already used this session ──────────────────────
        private static readonly HashSet<string> CoveredTopics =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> MentionedExamples =
            new(StringComparer.OrdinalIgnoreCase);

        // ── UI context — set by MainWindow before each request ────────────────
        public static string LiveHints   { get; private set; } = "";
        public static string CompanyName { get; private set; } = "";
        public static string JobDesc     { get; private set; } = "";

        // What the candidate is looking for. Set from the setup panel, and empty
        // whenever they left the field alone, so an unanswered field never turns
        // into a confident answer.
        public static string WorkType       { get; set; } = "";
        public static string WorkAuth       { get; set; } = "";
        public static string Availability   { get; set; } = "";
        public static string WorkLocation   { get; set; } = "";
        public static string PayExpectation { get; set; } = "";

        private static bool HasScreeningPrefs =>
            !string.IsNullOrWhiteSpace(WorkType) || !string.IsNullOrWhiteSpace(WorkAuth) ||
            !string.IsNullOrWhiteSpace(Availability) || !string.IsNullOrWhiteSpace(WorkLocation) ||
            !string.IsNullOrWhiteSpace(PayExpectation);

        /// <summary>
        /// The answers to the questions a recruiter opens with, which no resume
        /// carries: work type, authorization, notice period, location, pay.
        ///
        /// Asked "C2C, W2 or full time?" with none of this, the model produced
        /// a paragraph about wanting to grow. That reads as dodging a direct
        /// question, and it is the first thing a screener writes down.
        ///
        /// Only fields the candidate actually filled in appear here. A blank one
        /// is deliberately left out, so the answer stays honestly vague rather
        /// than inventing a rate or a visa status on their behalf.
        /// </summary>
        private static void AppendScreeningPrefs(StringBuilder sb)
        {
            if (!HasScreeningPrefs) return;

            sb.AppendLine("WHAT THIS CANDIDATE IS LOOKING FOR (they told us; treat as fact):");
            if (!string.IsNullOrWhiteSpace(WorkType))
                sb.AppendLine($"Work type: {Truncate(WorkType, 60)}");
            if (!string.IsNullOrWhiteSpace(WorkAuth))
                sb.AppendLine($"Work authorization: {Truncate(WorkAuth, 60)}");
            if (!string.IsNullOrWhiteSpace(Availability))
                sb.AppendLine($"Can start: {Truncate(Availability, 40)}");
            if (!string.IsNullOrWhiteSpace(WorkLocation))
                sb.AppendLine($"Location: {Truncate(WorkLocation, 60)}");
            if (!string.IsNullOrWhiteSpace(PayExpectation))
                sb.AppendLine($"Pay: {Truncate(PayExpectation, 120)}");
            sb.AppendLine();
            sb.AppendLine("Asked any of these, lead with the answer in the first few words, then");
            sb.AppendLine("one short line of flexibility if it is true. \"I'm looking for C2C, and");
            sb.AppendLine("I can start in two weeks\" is the whole answer. Do not open with a");
            sb.AppendLine("paragraph about growth; a screener asked a direct question and is");
            sb.AppendLine("waiting to tick a box.");
            sb.AppendLine("Say nothing about a field not listed above. Asked about one, say it is");
            sb.AppendLine("open or ask what the role offers. Never invent a rate, a visa status or");
            sb.AppendLine("a notice period.");
            sb.AppendLine();
        }

        public static void SetContext(string hints, string company, string job)
        {
            LiveHints   = Truncate(hints?.Trim()   ?? "", 4_000);
            CompanyName = Truncate(company?.Trim() ?? "", 512);
            JobDesc     = Truncate(job?.Trim()     ?? "", 4_000);
        }

        public static bool IsBehavioral(string q) => DetectType(q) == QuestionType.Behavioral;

        // ── LOCKED FACTS: first answer for each topic wins, never changes ─────
        private static readonly Dictionary<string, string> LockedFacts =
            new(StringComparer.OrdinalIgnoreCase);

        // ── Fact extraction patterns ──────────────────────────────────────────
        // (factKey, question trigger words, answer keywords to detect)
        private static readonly (string Key, string[] QTriggers, string[] AKeywords)[] FactPatterns =
        {
            ("best_language",
                new[] { "language", "lang", "favorite lang", "best lang", "strongest lang",
                        "code in", "coding language", "programming language" },
                new[] { "Python", "Java", "JavaScript", "TypeScript", "Go", "Golang", "Rust",
                        "C#", "C++", "Kotlin", "Swift", "Ruby", "PHP", "Scala", "Dart" }),

            ("years_experience",
                new[] { "years", "experience", "how long", "long have you", "how many year",
                        "total experience" },
                new[] { "1 year", "2 year", "3 year", "4 year", "5 year", "6 year",
                        "1.5", "2.5", "3.5", "4.5", "half a year", "one year", "two year",
                        "three year", "four year", "five year" }),

            ("current_employer",
                new[] { "current company", "current employer", "where do you work",
                        "currently work", "working now", "current job", "current role" },
                new[] { "Renasant", "Wipro", "Google", "Microsoft", "Amazon", "Apple",
                        "Meta", "Netflix", "Uber", "Airbnb", "Stripe" }),

            ("salary_expectation",
                new[] { "salary", "compensation", "pay", "ctc", "how much",
                        "expected salary", "rate expectation", "pay expectation" },
                new[] { "$", "k ", "thousand", "lakh", "USD" }),

            ("best_strength",
                new[] { "strength", "best at", "strongest", "excel at", "good at",
                        "top skill", "superpower" },
                new[] { "Java", "Python", "leadership", "problem solving", "architecture",
                        "backend", "frontend", "DevOps", "cloud", "communication" }),

            ("education",
                new[] { "education", "degree", "study", "university", "college",
                        "school", "master", "bachelor", "graduate" },
                new[] { "Bachelor", "Master", "MS", "BS", "PhD", "B.Tech", "M.Tech",
                        "Computer Science", "Engineering", "Roosevelt" }),

            ("relocation",
                new[] { "relocat", "move", "open to moving", "willing to move" },
                new[] { "yes", "no", "absolutely", "open to", "not willing" }),

            ("visa_status",
                new[] { "visa", "stem opt", "work authorization", "sponsorship",
                        "authorized to work", "citizen", "green card", "h1b", "h-1b" },
                new[] { "STEM OPT", "H-1B", "citizen", "green card", "EAD", "F-1" }),

            ("start_date",
                new[] { "start date", "when can you start", "notice period",
                        "available to join", "earliest start", "join us" },
                new[] { "week", "month", "immediately", "right away", "2 weeks",
                        "4 weeks", "30 days" }),
        };

        // =====================================================================
        // PUBLIC API
        // =====================================================================

        /// <summary>
        /// Replaces a fenced code block with a note that one was given.
        ///
        /// Every prompt carries the last few turns, and a coding answer puts its
        /// whole solution in there — so a behavioural question asked after one
        /// arrives at the model with sixty lines of C++ attached, which it does
        /// not need and which is charged for on every request from then on. On
        /// an allowance of eight thousand tokens a minute that is not a rounding
        /// error.
        ///
        /// The most recent turn keeps its code, because "can you optimise that?"
        /// is a real follow-up and needs the thing being optimised. Older turns
        /// keep only the fact that code was given, which is all the continuity
        /// they were providing.
        /// </summary>
        private static readonly Regex HistoryCodeBlock =
            new(@"```[A-Za-z0-9+#_-]*
?
.*?(?:```|$)",
                RegexOptions.Singleline | RegexOptions.Compiled);

        private static string CollapseCode(string answer) =>
            HistoryCodeBlock.Replace(answer, "[code given]").Trim();

        public static void AddToHistory(string question, string answer)
        {
            question = Truncate(question, MaxHistoryQuestionChars);
            answer = Truncate(answer, MaxHistoryAnswerChars);

            // The turn that was most recent becomes an older turn now, so its
            // code is collapsed as this one arrives.
            if (History.Count > 0)
            {
                var (previousQuestion, previousAnswer) = History[^1];
                History[^1] = (previousQuestion, CollapseCode(previousAnswer));
            }

            History.Add((question, answer));
            if (History.Count > MaxHistoryTurns) History.RemoveAt(0);
            TrackCoveredContent(question + " " + answer);
            // Don't try to extract personal facts from screen analysis entries
            if (!question.Contains("screen", StringComparison.OrdinalIgnoreCase))
                ExtractAndLockFacts(question, answer);
        }

        /// <summary>
        /// Returns true if the most recent history entry was a screen analysis.
        /// Used to inject "you just analyzed the screen" context into the next question.
        /// </summary>
        public static bool LastEntryWasScreenAnalysis()
        {
            if (History.Count == 0) return false;
            return History[^1].Q.Contains("screen", StringComparison.OrdinalIgnoreCase);
        }

        public static void ClearHistory()
        {
            History.Clear();
            CoveredTopics.Clear();
            MentionedExamples.Clear();
            LockedFacts.Clear();
            LiveHints = "";  // hints reset each session; company/job persist
        }

        // Phrases that mean the answer is on the screen rather than in the words.
        //
        // An interviewer sharing a coding problem says "have a look at this" and
        // then stops talking. Everything needed to answer is on the screen and
        // almost none of it is in the sentence, so sending the sentence alone to a
        // text model produces confident nonsense, which is the worst possible
        // output in the middle of an interview.
        private static readonly string[] ScreenReferencePhrases =
        {
            "on the screen", "on my screen", "on your screen", "on screen",
            "look at this", "look at the screen", "have a look", "take a look",
            // "do you see" and "can you see" were here, and "How do you see a role
            // like this fitting into that path?" went to the screen reader in a
            // real interview, which answered with template text. They are matched
            // below only when they point at something, not at a role or a future.
            "you can see",
            "sharing my screen", "share my screen", "shared my screen",
            "in front of you", "shown here", "displayed here", "up on the",
            "solve this", "fix this", "debug this", "explain this",
            "this code", "this error", "this problem", "this question",
            "this diagram", "this snippet", "this function", "this output",
            "what is this", "what's this", "read this", "walk me through this",
            // "What website is open now?" answered "I'm not able to see which
            // website is open" — a text model honestly saying it was never
            // shown a picture, because none of the phrases above cover asking
            // what is currently open or visible rather than pointing at it.
            "website is open", "website open", "what website", "which website",
            "what tab", "which tab", "what app", "which app", "what application",
            "what program", "what's open", "what is open", "currently open",
            "currently on your screen", "in your browser", "in your editor",
            "in your ide", "what ide", "which ide",
        };

        /// <summary>
        /// Questions about the person, which no screenshot can help with.
        ///
        /// While Watch Screen is on, every question was going through the screen
        /// path, so "which language do you prefer?" came back as an answer about
        /// a code editor. That is a non-answer, it costs the tokens of reading a
        /// picture nobody asked about, and it quietly tells the interviewer that
        /// something is looking at the screen.
        /// </summary>
        private static readonly string[] PersonalQuestionPhrases =
        {
            "tell me about yourself", "about yourself", "walk me through your",
            "your experience", "your background", "your resume", "your cv",
            "your strength", "your weakness", "your biggest", "your greatest",
            "why do you want", "why are you leaving", "why did you leave",
            "where do you see yourself", "your career", "your goal",
            "do you prefer", "which language do you", "favourite", "favorite",
            "how are you", "salary", "expectation", "notice period",
            "c2c", "w2", "full time", "relocat", "visa", "sponsor",
            "any questions for", "tell me a time", "tell me about a time",
            "have you worked with", "how many years", "comfortable with",
        };

        /// <summary>
        /// True when the question is plainly about the candidate. Deliberately
        /// narrow: anything it is unsure about stays on the screen path, because
        /// while a screen is being shared most questions really are about it.
        /// </summary>
        public static bool IsPersonalQuestion(string question)
        {
            if (string.IsNullOrWhiteSpace(question)) return false;
            if (RefersToScreen(question)) return false;   // "this code" wins

            string q = question.ToLowerInvariant();
            foreach (string phrase in PersonalQuestionPhrases)
                if (q.Contains(phrase, StringComparison.Ordinal)) return true;

            return false;
        }

        /// <summary>
        /// Whether a question is about what is on screen rather than about the
        /// candidate. When it is, the screenshot is the question, and answering
        /// from the transcript alone cannot work however good the model is.
        /// </summary>
        /// <summary>
        /// Anyone naming the screen with a determiner in front of it.
        ///
        /// The phrase list below could not do this. It carried "on my screen"
        /// and not "in my screen", so "what is there in my screen now, you tell
        /// me" was answered by a text model explaining it has no access to the
        /// user's computer — the single worst answer the product can give,
        /// because it says out loud that something was expected to be looking.
        /// A list of exact phrases will always be one preposition short of what
        /// somebody actually said.
        ///
        /// The determiner is what keeps this safe. Bare "screen" is interview
        /// vocabulary — a phone screen, a technical screen, a recruiter screen
        /// are all conversations, not displays — and matching it would send a
        /// screenshot every time somebody described the hiring process. "My
        /// screen", "the screen", "this screen" are not ambiguous in the same
        /// way.
        /// </summary>
        private static readonly Regex NamesTheScreen =
            new(@"\b(?:my|your|the|this|that)\s+screens?\b", RegexOptions.Compiled);

        private static readonly Regex SeesSomething = new(
            @"\b(?:can|do|could) you see (?:this|that|it|what i|anything|my|the (?:code|error|output|diagram|question|page|window|chart|problem))\b" +
            @"(?!\s+(?:role|position|job|team|company|opportunity|as|fitting|working|going|yourself))" +
            @"|\bwhat do you see\b(?!\s+(?:yourself|as|in|for|when))",
            RegexOptions.Compiled);

        public static bool RefersToScreen(string question)
        {
            if (string.IsNullOrWhiteSpace(question)) return false;

            string q = question.ToLowerInvariant();
            foreach (string phrase in ScreenReferencePhrases)
                if (q.Contains(phrase, StringComparison.Ordinal)) return true;

            return NamesTheScreen.IsMatch(q) || SeesSomething.IsMatch(q);
        }

        public static bool IsGreeting(string q)
        {
            string t = q.Trim().ToLower().Trim('.', '!', '?', ',', ' ');
            if (t is "hi" or "hello" or "hey" or "hi there" or
                "good morning" or "good afternoon" or "good evening" or
                "greetings" or "hey there")
                return true;

            // Catch Speechmatics repetition artifacts like "hello hello", "hi hi", "hey hey hey".
            string[] baseGreetings = { "hi", "hello", "hey", "greetings" };
            var words = t.Split(' ')
                         .Select(w => new string(w.Where(char.IsLetter).ToArray()))
                         .Where(w => w.Length > 0)
                         .ToArray();
            if (words.Length >= 1 && words.Length <= 4 && words.All(w => baseGreetings.Contains(w)))
                return true;
            return false;
        }

        /// <summary>The pleasantries themselves, matched and then subtracted.</summary>
        private static readonly string[] SmallTalkPhrases =
        {
            "how are you", "how's it going", "how is it going", "how you doing",
            "how have you been", "how is your day", "how's your day",
            "how was your day", "how is your evening", "how's your evening",
            "how is your night", "nice to meet", "thanks for coming",
            "pleasure to meet",
        };

        /// <summary>
        /// Words that can trail a pleasantry without making it a question.
        ///
        /// Deliberately generous: a word wrongly listed here costs a canned
        /// reply to chit-chat, and a word wrongly missing costs the model a
        /// round trip. Only one of those is visible to an interviewer.
        /// </summary>
        private static readonly HashSet<string> PleasantryFiller = new(StringComparer.Ordinal)
        {
            "hi", "hello", "hey", "there", "greetings",
            "good", "great", "fine", "well", "ok", "okay", "alright",
            "morning", "afternoon", "evening", "night", "day", "today",
            "you", "your", "yours", "yourself", "u", "i", "im", "me", "my",
            "we", "us", "it", "its", "and", "so", "too", "very", "much",
            "thanks", "thank", "thankyou", "welcome", "please",
            "sir", "maam", "madam", "mam",
            "am", "are", "is", "was", "be", "been", "doing", "do", "did",
            "how", "hope", "glad", "happy", "nice", "meet", "meeting",
            "pleasure", "coming", "come", "in", "for", "to", "the", "a", "an",
            "yeah", "yes", "yep", "no", "um", "uh", "er", "oh", "hmm",
        };

        public static bool IsSmallTalk(string q)
        {
            string t = q.Trim().ToLower();

            // Only treat as pure small talk when the whole utterance is SHORT. A real
            // interview question that merely contains "how are you" (or follows a
            // greeting in the same breath) must never get the canned small-talk reply.
            if (t.Length > 60) return false;

            bool hasSmallTalk = SmallTalkPhrases.Any(t.Contains);
            if (!hasSmallTalk) return false;

            // Take the pleasantry away and see whether anything is left standing.
            //
            // This used to be a list of about twenty words - what, why, java,
            // python, code - that a question had to contain to escape being
            // treated as chit-chat. "How are you handling state in React?"
            // contains none of them, is under sixty characters, and matched
            // "how are you", so it was answered with "Doing really well,
            // thanks! Excited to be here" while the panel waited. So were "How
            // are you deploying to AWS?" and "Nice to meet you, shall we start
            // with your background?".
            //
            // A list can only name the technologies somebody thought of on the
            // day. Subtracting asks the question the code actually cares about:
            // after the greeting, the pleasantry and the filler are removed, did
            // the interviewer say anything else? If they did, it is a question,
            // whatever it happens to be about.
            string stripped = t;
            foreach (var phrase in SmallTalkPhrases)
                stripped = stripped.Replace(phrase, " ");

            var leftovers = stripped
                .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
                .Aggregate(new StringBuilder(), (sb, c) => sb.Append(c))
                .ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !PleasantryFiller.Contains(w))
                .ToArray();

            // Anything substantive left means they asked something.
            return leftovers.Length == 0;
        }

        public static string GetGreetingResponse() =>
            "Hey, great to be here, really looking forward to this conversation!";

        public static string GetSmallTalkResponse() =>
            "Doing really well, thanks! Excited to be here and learn more about the role.";

        /// <summary>
        /// Words that begin a genuine question or task. When one appears AFTER a
        /// closing phrase, the turn did not end there.
        ///
        /// Checked on the words after the phrase, never on the whole turn and
        /// never by sentence punctuation, which speech recognition often drops:
        /// "does that answer your question so how would you test this service"
        /// arrives with no punctuation at all, and a recap that mentions earlier
        /// questions must not stop a final thank-you from counting.
        /// </summary>
        private static readonly Regex RequestCue = new(
            @"\b(how|what|why|when|where|which|who|can you|could you|would you|will you|" +
            @"tell me|tell us|walk me|walk us|explain|describe|design|write|implement|" +
            @"build|create|solve|show me|let's|let us)\b",
            RegexOptions.Compiled);

        private static bool RequestFollows(string t, int phraseEnd) =>
            phraseEnd < t.Length && RequestCue.IsMatch(t, phraseEnd);

        /// <summary>
        /// "Is there another angle on the role, the tech, or the team that you'd
        /// like me to focus on?" is a repeat invitation. "What other angle would
        /// you take to reduce the latency?" is a technical question. The difference
        /// is that the first is about the role or team and asks what to focus on.
        /// </summary>
        private static readonly Regex AnotherAngleOnRole = new(
            @"\banother angle\b[^?.!]{0,60}\b(role|team|company|position|job|tech|product)\b" +
            @"[^?.!]{0,60}\b(focus on|like to know|want to know|like me to cover)\b",
            RegexOptions.Compiled);

        /// <summary>
        /// True when the interviewer has handed the conversation to the candidate
        /// for questions, or is checking whether an earlier candidate question was
        /// answered. These are closing turns, not invitations to give another
        /// resume answer.
        /// </summary>
        internal static bool IsCandidateQuestionInvitation(string question)
        {
            string t = Regex.Replace(question ?? "", @"\s+", " ").Trim().ToLowerInvariant();
            if (t.Length == 0) return false;

            string[] invitations =
            {
                "do you have any questions", "have any questions for me",
                "have any questions for us", "any questions for me", "any questions for us",
                "are there any questions", "is there any questions", "is there any question you have",
                "is there any question do you have", "any question you have",
                "do you still have questions", "do you have still questions", "still have any questions",
                "any other questions", "anything you'd like to ask", "anything you would like to ask",
                "anything you want to ask", "is there anything you want to ask",
                "anything else you'd like to ask", "anything else you would like to ask",
                "what questions do you have", "what else would you like to know",
                "what else do you want to know", "what else do you want to ask",
                // Not "what other angle" or "would you like me to focus on":
                // those are ordinary technical questions ("What other angle
                // would you take to reduce the latency?") and were being
                // answered with a fixed sign-off.
                "is that the level of detail", "did that answer your question",
                "does that answer your question", "did that cover your question",
                "does that cover your question", "do you want me to go deeper",
                "would you like me to go deeper", "want me to go a bit deeper",
                "want me to go deeper", "do you want more detail",
            };

            // Find where the last invitation phrase ends, then ask whether a real
            // question or task follows it. Splitting on sentence punctuation was
            // not enough: "does that answer your question so how would you test
            // this service" arrives unpunctuated and got a fixed wrap-up reply.
            int end = -1;
            foreach (string phrase in invitations)
            {
                int at = t.LastIndexOf(phrase, StringComparison.Ordinal);
                if (at >= 0) end = Math.Max(end, at + phrase.Length);
            }
            Match angle = AnotherAngleOnRole.Match(t);
            if (angle.Success) end = Math.Max(end, angle.Index + angle.Length);
            // The list above names exact phrases, and an interviewer's wording is
            // never on it: tested against twenty ordinary ways of asking, it caught
            // seven. "Any more questions?", "Any final questions?", "Do you want to
            // ask anything else?" all reached the model, which asked yet another
            // question, and a candidate who keeps asking questions on cue is the
            // clearest sign that something is answering for them.
            foreach (Match m in InvitationPattern.Matches(t))
                end = Math.Max(end, m.Index + m.Length);

            return end >= 0 && !RequestFollows(t, end);
        }

        /// <summary>
        /// Detects a real sign-off. A final thank-you used to be treated as a new
        /// interview question, which produced a long technical answer after the
        /// interviewer had already closed the call.
        /// </summary>
        internal static bool IsInterviewEndStatement(string question)
        {
            string t = Regex.Replace(question ?? "", @"\s+", " ").Trim().ToLowerInvariant();
            if (t.Length == 0) return false;

            // Strong closings end the interview unless a question follows them.
            // "We'll be in touch with next steps, but first can you explain your
            // testing approach?" returned a goodbye when these returned at once.
            string[] strong =
            {
                "we'll be in touch", "we will be in touch", "we'll follow up",
                "we will follow up", "that concludes the interview",
                "this concludes the interview", "that wraps up the interview",
                "this wraps up the interview",
            };
            int strongEnd = -1;
            foreach (string phrase in strong)
            {
                int at = t.LastIndexOf(phrase, StringComparison.Ordinal);
                if (at >= 0) strongEnd = Math.Max(strongEnd, at + phrase.Length);
            }
            if (strongEnd >= 0 && !RequestFollows(t, strongEnd) && t.IndexOf('?', strongEnd) < 0)
                return true;

            // A thank-you sign-off is judged from the LAST thank-you onward. A final
            // turn that recaps earlier questions before thanking the candidate is
            // still a goodbye; scanning the whole turn for "?" and "question" made
            // it look like a new question. And thanking someone for their time is
            // how interviews START as often as how they end: "Thank you for taking
            // the time... Can you start by telling me about yourself?" carries on.
            int thanksAt = Math.Max(t.LastIndexOf("thank you", StringComparison.Ordinal),
                                    t.LastIndexOf("thanks", StringComparison.Ordinal));
            if (thanksAt < 0) return false;
            string tail = t.Substring(thanksAt);

            bool signOff = tail.Contains("taking the time") || tail.Contains("for your time") ||
                           tail.Contains("speaking with me") || tail.Contains("speaking with us") ||
                           tail.Contains("meeting with me") || tail.Contains("meeting with us") ||
                           tail.Contains("joining us today") || tail.Contains("talking with me") ||
                           tail.Contains("talking with us");
            if (!signOff) return false;
            if (tail.Contains('?') || RequestCue.IsMatch(tail)) return false;

            string[] carriesOn =
            {
                "start", "begin", "move on", "next question", "next round",
                "next one", "now ", "go ahead", "welcome", "introduce",
                "background", "coding", "anything", "any final", "thoughts",
            };
            return !carriesOn.Any(tail.Contains);
        }

        internal static bool IsWorkAuthorizationQuestion(string q) =>
            Regex.IsMatch(q ?? "",
                @"\b(stem opt|opt|cpt|ead|h-?1-?b|h 1 b|cap[- ]gap|cap extension|green card|i-?20|i-?983|sponsor(?:ship)?|visa|work authori[sz]ation|authori[sz]ed to work)\b",
                RegexOptions.IgnoreCase);

        private static readonly Regex InvitationPattern = new(
            // Not "any questions on the approach before you start coding?": that is
            // about a task, and a wrap-up reply to it ends the exercise.
            @"\bany (?:other |more |further |final |last |additional |follow[- ]?up )?questions?\b" +
            @"(?![^?.!]*\b(?:approach|problem|task|exercise|code|coding|design|requirements?|solution|assignment|start|begin)\b)" +
            @"|\bany (?:other )?thing (?:else )?(?:you|u) (?:want|like|would like|wanna) to (?:ask|know)\b" +
            @"|\b(?:do|would|did) (?:you|u) (?:want|wanna|like|have anything) to ask\b" +
            @"|\b(?:want|like) to ask (?:me |us )?(?:anything|something)\b" +
            @"|\b(?:anything|something) (?:else )?(?:that )?(?:you|you'd|you would|u) (?:like|want|wanna|would like) to (?:ask|know)\b" +
            @"|\bis there (?:anything|something) (?:else )?(?:you|you'd|you would|u)\b[^?.!]{0,30}\b(?:ask|know)\b" +
            @"|\bquestions? for (?:me|us)\b",
            RegexOptions.Compiled);

        /// <summary>
        /// Short follow-ups that only mean "any more questions?" once the candidate
        /// has already been invited to ask: "Anything else?", "Did that help?".
        /// Earlier in an interview "Anything else?" asks for more on the last
        /// answer, so these are never matched on their own.
        /// </summary>
        private static readonly Regex CandidateQuestionFollowUp = new(
            @"^(?:(?:ok|okay|sure|great|cool|alright|all right|perfect|good|yeah|yes|so|and|right|got it)[,.!]?\s+)*" +
            @"(?:anything else|anything more|something else|is that all|is there anything else" +
            @"|anything else i can (?:answer|help|clarify|tell)[^?.!]*" +
            @"|did that help|does that help|was that clear|was that helpful|does that make sense|that make sense|did that make sense)" +
            @"(?:\s+(?:for you|you want to know|you'd like to know|at all|then))?\s*[?.!]*\s*$",
            RegexOptions.Compiled);

        internal static bool IsCandidateQuestionFollowUp(string question) =>
            CandidateQuestionFollowUp.IsMatch(Regex.Replace(question ?? "", @"\s+", " ").Trim().ToLowerInvariant());

        private static bool InCandidateQuestionPhase() => PriorCandidateQuestionInvitations() > 0;

        private static int PriorClosingTurns() =>
            History.Count(turn => IsCandidateQuestionInvitation(turn.Q) || IsCandidateQuestionFollowUp(turn.Q));

        private static int PriorCandidateQuestionInvitations() =>
            History.Count(turn => IsCandidateQuestionInvitation(turn.Q));

        /// <summary>
        /// Returns a guaranteed short, human closing response when another model
        /// keeps asking whether the candidate has more questions. The first
        /// invitation still goes to the answer model so it can ask one relevant
        /// question; only repeats and the final sign-off are handled here.
        /// </summary>
        internal static bool TryGetClosingResponse(string question, out string response)
        {
            response = "";

            bool followUp = InCandidateQuestionPhase() && IsCandidateQuestionFollowUp(question);
            if (IsCandidateQuestionInvitation(question) || followUp)
            {
                if (PriorCandidateQuestionInvitations() == 0) return false;

                // Varied, so the same sentence is never said twice in a row, and
                // never a new question.
                bool checkingItHelped = Regex.IsMatch(question.ToLowerInvariant(),
                    @"\b(help|helpful|clear|make sense|answer your question|cover your question|level of detail|go deeper|more detail)\b");
                int prior = PriorClosingTurns();
                if (checkingItHelped)
                    response = prior <= 1
                        ? "Yes, that was really helpful, thank you. That covers my questions."
                        : "Yes, it did, thank you. That's everything from me.";
                else
                    response = prior switch
                    {
                        <= 1 => "That answered what I wanted to know, thank you. I think that covers my questions.",
                        2 => "No, I'm all set. Thank you for walking me through it.",
                        _ => "No, that's everything from me. Thanks again for your time.",
                    };
                return true;
            }

            if (!IsInterviewEndStatement(question)) return false;

            response = "Thank you for your time. I enjoyed learning more about the role and the team.";
            return true;
        }

        public static string NormalizeInterviewerQuestion(string question)
        {
            string normalized = Regex.Replace(question ?? "", @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(normalized)) return "";

            normalized = Regex.Replace(normalized,
                @"\bdependency\s*[?.!]\s+(?:the\s+)?injection\b",
                "dependency injection", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized,
                @"\b(what|why|how|when|where|who|which|is|are|was|were|do|does|did|define|describe|explain)\s*[?.!]\s+(?=[a-z])",
                "$1 ", RegexOptions.IgnoreCase);

            string[] segments = Regex.Split(normalized, @"(?<=[?.!])\s+");
            int firstQuestion = 0;
            while (firstQuestion < segments.Length - 1 && IsOpeningConversationFiller(segments[firstQuestion]))
                firstQuestion++;

            string[] remaining = segments.Skip(firstQuestion)
                .Select(segment => segment.Trim())
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .ToArray();
            if (remaining.Length == 0) return "";

            int lastMeaningful = remaining.Length - 1;
            while (lastMeaningful > 0 && IsOpeningConversationFiller(remaining[lastMeaningful]))
                lastMeaningful--;
            remaining = remaining.Take(lastMeaningful + 1).ToArray();

            // Speech recognition can retain a complete earlier question before the
            // interviewer reaches the real one. Keep the final complete question plus
            // any short constraint that follows it, while removing trailing "okay/yes"
            // filler that previously prevented Auto mode from ever submitting.
            for (int index = remaining.Length - 1; index >= 0; index--)
            {
                if (IsCompleteInterviewQuestion(remaining[index]))
                    return string.Join(" ", remaining.Skip(index));
            }

            return string.Join(" ", remaining);
        }

        private static bool IsCompleteInterviewQuestion(string segment)
        {
            if (IsOpeningConversationFiller(segment)) return false;

            string text = segment.Trim().ToLower();
            return Regex.IsMatch(text,
                @"^(what|why|how|when|where|who|which|do|does|did|is|are|can|could|would|will|have|has|tell|describe|explain|define|compare|walk|give|share|introduce|write|create|build|implement|develop|generate|code|program|solve|show)\b") ||
                (text.EndsWith('?') && text.Count(char.IsLetter) >= 2);
        }

        private static bool IsOpeningConversationFiller(string segment)
        {
            string text = segment.Trim().ToLower().Trim('.', '!', '?', ',', ' ');
            if (string.IsNullOrWhiteSpace(text)) return true;

            return text is "hi" or "hello" or "hey" or "hi there" or "hey there" or
                "good morning" or "good afternoon" or "good evening" or "greetings" or
                "how are you" or "how's it going" or "how you doing" or "how have you been" or
                "i'm fine" or "i am fine" or "i'm good" or "i am good" or
                "fine thank you" or "good thanks" or "doing well" or
                "sorry" or "no sorry" or "okay" or "okay sir" or "yes" or "yes sir" or
                "no" or "no sir" or "thanks" or "thank you";
        }

        public static string BuildVerifyPrompt() =>
            "State my most recent degree, current employer, and city of residence in 1 short sentence.";

        // =====================================================================
        // QUESTION TYPE
        // =====================================================================

        internal enum QuestionType
        {
            YesNo, Intro, Technical, Coding, Behavioral, Situational,
            Weakness, WhyRole, Salary, Availability, FollowUp,
            Preference, Logistics, CandidateQuestions, InterviewClosing,
            ContextStatement, MemoryRecall, General
        }

        internal static QuestionType DetectType(string q)
        {
            string t = q.ToLower().Trim();
            bool hasQuestionMark = t.Contains('?');

            // Closing turns have to win over generic yes/no and follow-up rules.
            // "Do you have any questions?" otherwise becomes a yes/no answer, and
            // "want me to go deeper?" otherwise asks for yet another long answer.
            if (IsCandidateQuestionInvitation(t))
                return QuestionType.CandidateQuestions;
            if (InCandidateQuestionPhase() && IsCandidateQuestionFollowUp(t))
                return QuestionType.CandidateQuestions;
            if (IsInterviewEndStatement(t))
                return QuestionType.InterviewClosing;

            // Once the candidate has been invited to ask questions, a long turn from
            // the interviewer is their answer. A real one ("Then there is the trading
            // impact side ... the bar is basically, does it move the needle enough?")
            // was answered with a paragraph reciting it back.
            if (PriorCandidateQuestionInvitations() > 0 &&
                Regex.Matches(t, @"[\p{L}\p{N}']+").Count >= 45 &&
                !t.TrimEnd().EndsWith("?") &&
                !Regex.IsMatch(t, @"\b(your|yourself|tell me|walk me|can you|could you|would you|do you)\b"))
                return QuestionType.ContextStatement;

            // Coding and task requests first. "I'm going to give you an exercise:
            // write a function..." starts like an introduction, and "For this next
            // exercise I want a function..." is long with no question mark; both
            // were only acknowledged because the rules below ran first.
            if (IsCodingRequest(t))
                return QuestionType.Coding;

            bool startsWithInterviewerInfo =
                t.StartsWith("my name is") || t.StartsWith("i am ") || t.StartsWith("i'm ") ||
                t.StartsWith("we are ") || t.StartsWith("we're ") || t.StartsWith("this role") ||
                t.StartsWith("this position") || t.StartsWith("our company") ||
                t.StartsWith("the company") || t.StartsWith("i work at") ||
                t.StartsWith("i work for") || t.StartsWith("i currently") ||
                t.StartsWith("just so you know") || t.StartsWith("fyi") || t.StartsWith("by the way");
            if (startsWithInterviewerInfo && !hasQuestionMark)
                return QuestionType.ContextStatement;

            // Interviewers often explain a process or answer the candidate's
            // question in a long declarative turn. Treat that as conversation to
            // acknowledge, not as a prompt to recite the same content back.
            bool startsLikeQuestionOrCommand = Regex.IsMatch(t,
                @"^(what|why|how|when|where|who|which|do|does|did|is|are|can|could|would|will|have|has|tell|describe|explain|define|compare|walk|give|share|write|create|build|implement|develop|generate|code|program|solve|show)\b");
            // Spoken requests rarely open with the textbook question word, and
            // recognition often drops the question mark: "So for this next one I
            // want you to describe how you would design a URL shortener", "I'd
            // like you to walk me through...", "Now imagine you are leading...".
            // All of those were only acknowledged. An explanation talks about the
            // team and the process; a request is addressed to the candidate.
            bool addressesCandidate = Regex.IsMatch(t,
                @"\b(you|your|yourself|walk me|tell me|imagine|suppose|let's say|lets say|assume|design|debug|describe|explain|implement|build)\b");
            // And positive evidence that the interviewer is describing something
            // (we, our, the team, the role), rather than treating any long
            // unpunctuated sentence as context. When unsure, answer it.
            bool explainsSomething = Regex.IsMatch(t,
                @"\b(we|we're|we've|we'll|our|the team|this team|the company|the role|this role|the position|the process|the interview|the project|the product)\b");
            if (!hasQuestionMark && !startsLikeQuestionOrCommand && !addressesCandidate && explainsSomething &&
                Regex.Matches(t, @"[\p{L}\p{N}']+").Count >= 18)
                return QuestionType.ContextStatement;

            if ((t.Contains("what") || t.Contains("tell me")) &&
                (t.Contains("my name") || t.Contains("what i do") || t.Contains("what do i do") ||
                 t.Contains("who am i") || t.Contains("where do i work") ||
                 t.Contains("what i said") || t.Contains("what i told") ||
                 t.Contains("what did i say") || t.Contains("what i just said")))
                return QuestionType.MemoryRecall;

            if (t.Contains("tell me more") || t.Contains("can you elaborate") ||
                t.Contains("expand on that") || t.Contains("go deeper") ||
                t.Contains("what do you mean by") || t.Contains("elaborate on") ||
                t.Contains("go on") || t.Contains("continue"))
                return QuestionType.FollowUp;

            // Coding requests are detected at the top of this method, before the
            // acknowledge-only rules and the generic yes/no check.

            // Story requests that happen to open like a yes/no question. "Can you
            // think of a specific project where you and a researcher disagreed?" was
            // classed YesNo in a real session and got a short yes/no style answer
            // where the interviewer wanted the story.
            if (Regex.IsMatch(t,
                    @"\b(can you think of|could you think of|can you recall|do you remember a|a specific (project|time|situation|example|case)|a time (when|where)|(project|situation|case) where)\b"))
                return QuestionType.Behavioral;

            // "Can you tell me about the RESTful services you built?" is a request.
            // It was classed YesNo and answered in one or two sentences: in one real
            // interview 14 of 31 turns opened "Can you tell me", "Can you describe"
            // or "Can you please describe", and every one came back thin. The
            // request is classified by what is asked for, without the polite prefix.
            var politeRequest = Regex.Match(t,
                @"^(?:so |and |okay |ok |now |alright )?(?:can|could|would|will) (?:you|u) (?:please )?(?=(?:tell|walk|describe|explain|talk|share|give|go over|go through|elaborate|expand|read|list|summari[sz]e|brief|take me|help me understand)\b)");
            if (politeRequest.Success)
            {
                string rest = t[politeRequest.Length..];
                if (Regex.IsMatch(rest, @"\b(your|ur) (?:past |previous |work |professional |overall )?(experience|background|resume|career|journey)\b(?! (?:with|in|on|using|of|at)\b)"))
                    return QuestionType.Intro;
                var inner = DetectType(rest);
                return inner == QuestionType.YesNo ? QuestionType.General : inner;
            }

            // "How do you see a role like this fitting into that path?" and "Where do
            // you see yourself in five years?" are about the candidate's direction,
            // not a technical explanation.
            if (Regex.IsMatch(t, @"\b(how|where) do (you|u) see\b"))
                return QuestionType.General;

            // Work authorization and visa questions. "what is cap extension?" (the
            // H-1B cap-gap) was explained like a technical term, stating immigration
            // rules as fact. These are answered from the profile only.
            if (IsWorkAuthorizationQuestion(t))
                return QuestionType.YesNo;

            if (Regex.IsMatch(t, @"^(are you|do you|can you|will you|have you|is your|would you|did you|are u|r u)"))
                return QuestionType.YesNo;

            if (t.Contains("stem opt") || t.Contains("work authorization") ||
                t.Contains("sponsorship") || t.Contains("relocat") ||
                t.Contains("visa") || t.Contains("authorized to work") ||
                t.Contains("willing to") || t.Contains("open to remote") ||
                t.Contains("background check") || t.Contains("drug test") ||
                t.Contains("citizen") || t.Contains("green card") ||
                t.Contains("overtime") || t.Contains("travel required") ||
                t.Contains("hybrid") || t.Contains("on-site") || t.Contains("onsite"))
                return QuestionType.YesNo;

            if (t.Contains("salary") || t.Contains("compensation") ||
                t.Contains("pay expectation") || t.Contains("how much") ||
                t.Contains("rate expectation") || t.Contains("package") || t.Contains("ctc"))
                return QuestionType.Salary;

            if (t.Contains("start date") || t.Contains("when can you start") ||
                t.Contains("notice period") || t.Contains("available to join") ||
                t.Contains("earliest start") || t.Contains("join us"))
                return QuestionType.Availability;

            if (t.Contains("where are you") || t.Contains("where do you live") ||
                t.Contains("where are you based") || t.Contains("where are you located") ||
                t.Contains("your location") || t.Contains("current location") ||
                t.Contains("which city") || t.Contains("what city") || t.Contains("which country") ||
                t.Contains("what state") || t.Contains("your address") ||
                t.Contains("time zone") || t.Contains("timezone") ||
                t.Contains("where are you from") || t.Contains("are you local") ||
                t.Contains("prefer to work") || t.Contains("preferred location") ||
                t.Contains("prefer location") || t.Contains("prefer to be based") ||
                t.Contains("where would you like to work") || t.Contains("work from home") ||
                t.Contains("remote or office") || t.Contains("remote or in") ||
                t.Contains("your age") || t.Contains("how old are you") ||
                t.Contains("are you available") || t.Contains("contact number") ||
                t.Contains("phone number") || t.Contains("your email"))
                return QuestionType.Logistics;

            if (t.Contains("tell me about yourself") || t.Contains("walk me through") ||
                t.Contains("introduce yourself") || t.Contains("tell us about you") ||
                (t.Contains("background") && t.Contains("yourself")))
                return QuestionType.Intro;

            if (t.Contains("tell me a time") || t.Contains("tell me about a time") ||
                t.Contains("give me an example") || t.Contains("describe a situation") ||
                t.Contains("walk me through a time") || t.Contains("share an example") ||
                t.Contains("have you ever faced") || t.Contains("when did you"))
                return QuestionType.Behavioral;

            if (t.Contains("weakness") || t.Contains("weaknesses") ||
                t.Contains("biggest failure") || t.Contains("made a mistake") ||
                t.Contains("area of improvement") || t.Contains("improve yourself") ||
                t.Contains("constructive feedback"))
                return QuestionType.Weakness;

            if ((t.Contains("why") && (t.Contains("role") || t.Contains("company") ||
                 t.Contains("this job") || t.Contains("position") ||
                 t.Contains("us") || t.Contains("here"))) ||
                t.Contains("what interest you") || t.Contains("what attracted") ||
                t.Contains("what excites you") || t.Contains("what motivates") ||
                t.Contains("why should we hire") || t.Contains("strengths") ||
                t.Contains("what makes you"))
                return QuestionType.WhyRole;

            // "How do you handle a disagreement with a teammate?" matched "how do you"
            // in the technical rule below and was answered as a technical explanation.
            if (Regex.IsMatch(t, @"\bhow do (you|u) (handle|deal with|manage|approach|respond to|react to|work through|resolve)\b.*\b(disagree|conflict|pressure|stress|criticism|feedback|deadline|difficult|failure|mistake|setback|ambiguity|priorit|change|stakeholder|teammate|coworker|co-worker|manager|boss|colleague|collaborat|researcher|research team|cross-functional|other teams)"))
                return QuestionType.Situational;

            if (t.Contains("what would you do") || t.Contains("how would you handle") ||
                t.Contains("if you were") || t.Contains("hypothetically") ||
                t.Contains("imagine you") || t.Contains("scenario where"))
                return QuestionType.Situational;

            // Preference check MUST come BEFORE Technical — "What is your favorite X?"
            // contains "what is" which would wrongly fire as Technical otherwise.
            if (t.Contains("favorite") || t.Contains("favourite") ||
                t.Contains("preferred") || t.Contains("prefer") ||
                t.Contains("best language") || t.Contains("strongest language") ||
                t.Contains("best at") || t.Contains("strongest in") ||
                t.Contains("what language") || t.Contains("which language") ||
                t.Contains("go-to language") || t.Contains("language you") ||
                t.Contains("you like most") || t.Contains("you enjoy most") ||
                t.Contains("what tool") || t.Contains("which tool") ||
                t.Contains("which framework") || t.Contains("what framework") ||
                t.Contains("which database") || t.Contains("which cloud"))
                return QuestionType.Preference;

            if (t.Contains("what is") || t.Contains("explain") ||
                t.Contains("how does") || t.Contains("describe how") ||
                t.Contains("what are") || t.Contains("difference between") ||
                t.Contains("how do you") || t.Contains("what do you know about") ||
                t.Contains("define") || t.Contains("compare") ||
                t.Contains("architecture") || t.Contains("implement"))
                return QuestionType.Technical;

            return QuestionType.General;
        }

        public static bool IsCodingRequest(string question)
        {
            string text = Regex.Replace(question ?? "", @"\s+", " ").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(text)) return false;

            return Regex.IsMatch(text,
                       @"\b(write|show|provide|create|build|implement|develop|generate|code|program|solve)\b.{0,80}\b(code|program|function|method|class|algorithm|solution|snippet|application|api|query|sql)\b") ||
                   Regex.IsMatch(text,
                       @"\b(code|program)\s+(this|that|it|me|for me|a|an|the)\b") ||
                   Regex.IsMatch(text,
                       @"\bimplement\s+(a|an|the)?\s*[a-z0-9+#. -]{2,60}$") ||
                   // Tasks named rather than commanded: "for this next exercise I want
                   // a function that...", "the next exercise is a SQL query returning..."
                   Regex.IsMatch(text,
                       @"\b(i want|i'd like|i would like|please|next exercise|next task|next problem|coding exercise|coding problem|exercise is|task is|problem is)\b.{0,80}\b(function|method|class|algorithm|query|sql|api|endpoint|program|script|code)\b");
        }

        // =====================================================================
        // DRILL-DOWN DETECTION
        // =====================================================================

        private static bool IsDrillDown(string q)
        {
            if (History.Count == 0) return false;
            if (IsSimpleDefinitionQuestion(q)) return false;
            string t = q.ToLower().Trim().TrimEnd('.', '?', '!');

            if (Regex.IsMatch(t, @"^how (many|long|much|often|far|soon|old)")) return true;
            if (Regex.IsMatch(t, @"^which (version|one|tool|language|framework|company|team|project|platform|stack|cloud|database|year|month|role|position)")) return true;
            if (Regex.IsMatch(t, @"^what (version|year|company|team|tool|language|framework|platform|stack|size|number|percentage|percent|metric|result|outcome|role|position|project)")) return true;
            if (Regex.IsMatch(t, @"^who (said|was|were|is|told|mentioned|managed|led)")) return true;
            if (Regex.IsMatch(t, @"^when (was|did|were|is|did you)")) return true;
            if (Regex.IsMatch(t, @"^where (was|did|were|is)")) return true;

            // "What you said" / "You said X" — interviewer referencing a prior answer
            if (t.Contains("what you said") || t.Contains("you said") ||
                t.Contains("you mentioned") || t.Contains("u said") ||
                t.Contains("you told") || t.Contains("you stated") ||
                t.Contains("you just said") || t.Contains("earlier you") ||
                t.Contains("you previously"))
                return true;

            if (Regex.IsMatch(t, @"(years? of|year experience|how many years|years? experience)")) return true;

            // Short question (<=6 words) with a reference word = drill-down
            string[] words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= 6)
            {
                bool hasRef = t.Contains("how") || t.Contains("which") ||
                              t.Contains("what") || t.Contains("who") ||
                              t.Contains("when") || t.Contains("where") ||
                              t.Contains("years") || t.Contains("version") ||
                              t.Contains("size") || t.Contains("team") ||
                              t.Contains("number") || t.Contains("much") ||
                              t.Contains("many") || t.Contains("long") ||
                              t.Contains("old") || t.Contains("big") ||
                              t.Contains("use") || t.Contains("used");
                if (hasRef) return true;
            }
            return false;
        }

        // =====================================================================
        // LOCKED FACTS — EXTRACT & ENFORCE
        // =====================================================================

        /// <summary>
        /// Scans a Q&A pair and stores detectable facts. First answer wins — never overwritten.
        /// </summary>
        private static void ExtractAndLockFacts(string question, string answer)
        {
            string qLow = question.ToLower();
            string aLow = answer.ToLower();

            foreach (var (key, qTriggers, aKeywords) in FactPatterns)
            {
                if (LockedFacts.ContainsKey(key)) continue;  // first answer wins

                bool qMatch = qTriggers.Any(t => qLow.Contains(t));
                if (!qMatch) continue;

                foreach (var kw in aKeywords)
                {
                    if (aLow.Contains(kw.ToLower()))
                    {
                        int idx   = aLow.IndexOf(kw.ToLower());
                        int start = Math.Max(0, idx - 15);
                        int end   = Math.Min(answer.Length, idx + kw.Length + 50);
                        string snippet = answer.Substring(start, end - start).Trim();
                        if (snippet.Length > 80) snippet = snippet.Substring(0, 80) + "...";
                        LockedFacts[key] = $"{kw} (you said: \"{snippet}\")";
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Builds a hard lock block injected at the top of every user message.
        /// Shows ALL locked facts + flags conflicts when the interviewer pushes a different value.
        /// </summary>
        private static string BuildLockedConstraintBlock(string currentQuestion)
        {
            if (LockedFacts.Count == 0) return "";

            string qLow = currentQuestion.ToLower();
            var sb = new StringBuilder();
            var conflicts = new List<string>();

            sb.AppendLine("LOCKED FACTS FROM THIS SESSION — DO NOT CHANGE UNDER ANY CIRCUMSTANCES:");

            foreach (var (key, _, aKeywords) in FactPatterns)
            {
                if (!LockedFacts.TryGetValue(key, out var lockedValue)) continue;

                string label = key switch
                {
                    "best_language"      => "Best/favorite language",
                    "years_experience"   => "Years of experience",
                    "current_employer"   => "Current employer",
                    "salary_expectation" => "Salary expectation",
                    "best_strength"      => "Top strength",
                    "education"          => "Education",
                    "relocation"         => "Relocation",
                    "visa_status"        => "Visa/work auth",
                    "start_date"         => "Start date",
                    _                    => key
                };

                string shortVal = lockedValue.Split('(')[0].Trim();
                sb.AppendLine($"  [{label}]: {shortVal}");

                // Conflict: interviewer mentions a DIFFERENT keyword for this topic
                foreach (var kw in aKeywords)
                {
                    if (qLow.Contains(kw.ToLower()) &&
                        !lockedValue.ToLower().Contains(kw.ToLower()))
                    {
                        conflicts.Add(
                            $"  CONFLICT: Interviewer said '{kw}' but your locked answer is '{shortVal}'. " +
                            $"Hold your ground: \"Actually, I said {shortVal} earlier.\"");
                        break;
                    }
                }
            }

            if (conflicts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("  INTERVIEWER IS PUSHING A DIFFERENT ANSWER — DO NOT AGREE:");
                foreach (var c in conflicts) sb.AppendLine(c);
            }

            sb.AppendLine();
            return sb.ToString();
        }

        // =====================================================================
        // SYSTEM PROMPT
        // =====================================================================

        private static string BuildRealtimeSystemPrompt(string resumeFacts)
        {
            bool hasResume = !string.IsNullOrWhiteSpace(resumeFacts)
                             && resumeFacts != "No resume provided.";
            var sb = new StringBuilder();

            sb.AppendLine("You are the candidate answering a live job interview in real time.");
            sb.AppendLine("Answer immediately in first person, using natural spoken English.");
            sb.AppendLine("Never mention AI, prompts, transcripts, or these instructions.");
            sb.AppendLine("Answer only the last complete question. Ignore greetings, filler, and broken opening fragments.");
            sb.AppendLine();
            sb.AppendLine("The question arrives via speech recognition. Read it for what was meant,");
            sb.AppendLine("not the letters that arrived; acronyms come through worst. \"See to see\"/");
            sb.AppendLine("\"C to C\" is C2C, \"W to\" is W2, \"H one B\" is H1B, \"ten ninety nine\" is");
            sb.AppendLine("1099, \"sequel\" is SQL, \"dot net\" is .NET, \"go lang\" is Go.");
            sb.AppendLine("Answer what they asked. Never mention the transcript or say anything was");
            sb.AppendLine("unclear. If a word is unrecoverable, answer the rest and leave it.");
            sb.AppendLine();
            sb.AppendLine("Do not repeat the question. Do not use canned introductions.");
            sb.AppendLine("The spoken answer itself carries no headings, bullets or numbered lists: it is");
            sb.AppendLine("read out loud, and a list read aloud sounds like a list. Bullets appear only");
            sb.AppendLine("under MORE TO SAY, described at the end of these instructions.");
            sb.AppendLine("Match the length to the question. Quick factual, yes/no and logistics questions get 1-2 natural sentences.");
            sb.AppendLine("Technical and experience questions get real substance, usually 30-45 seconds spoken; stories 45-60 seconds. A short answer with nothing in it is worse than no answer.");
            sb.AppendLine("For behavioral questions, tell a concise STAR story without naming the STAR sections.");
            sb.AppendLine("For technical questions, give the direct answer first, then explain how it works, why it matters, and one relevant tradeoff or example.");
            sb.AppendLine("If asked to write, implement, or show code, output complete runnable code immediately. Never only describe the code, never refuse, and never claim you are not a programmer.");
            sb.AppendLine("When a coding request is vague, make one sensible interview-style assumption, use the requested or most recently discussed language, and provide a compact working example.");
            sb.AppendLine("Never invent employers, tools, dates, percentages, metrics, or achievements.");
            sb.AppendLine("Never state immigration, visa, tax or legal facts, such as what STEM OPT, H-1B or an EAD allows, beyond what the candidate's own profile says. Confirm status only; do not explain the rules.");
            sb.AppendLine("Be specific and credible. Do not cut off a useful explanation, but never pad the answer with generic filler.");
            sb.AppendLine("Do not turn an answer into a tour of the resume. Use one relevant example, and name at most two tools unless the interviewer specifically asks for the stack.");
            sb.AppendLine("When the interviewer is explaining or wrapping up, react conversationally. Do not paraphrase their whole statement back to them.");
            sb.AppendLine();

            if (hasResume)
            {
                sb.AppendLine("VERIFIED CANDIDATE FACTS:");
                sb.AppendLine(Truncate(resumeFacts, 4_500));
                sb.AppendLine("Use only facts and numbers present above. If a detail is absent, speak qualitatively.");
                sb.AppendLine();
                sb.AppendLine("The employers listed above are the only ones this candidate has worked");
                sb.AppendLine("for. Name no other company as somewhere they worked, ever, in any");
                sb.AppendLine("answer or example. Asked what came before a role you cannot place,");
                sb.AppendLine("say which of the roles above you mean, or ask which one they mean.");
                sb.AppendLine("Asked about a company that is not listed, say you did not work there.");
                sb.AppendLine();
                sb.AppendLine("This is not hypothetical. Asked what came before Macy's, the answer");
                sb.AppendLine("began \"I spent one year at Uber\", and a later answer described work");
                sb.AppendLine("in \"Uber's real-time dispatch system\". There is no Uber above. An");
                sb.AppendLine("interviewer holding the CV sees a company that is not on it.");
                sb.AppendLine();
                sb.AppendLine("A technical example needs no employer. \"In a dispatch system\" is");
                sb.AppendLine("safe and makes the same point; \"at Uber\" is a claim about their life.");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("No resume facts are available. This does NOT mean the candidate lacks skill or expertise.");
                sb.AppendLine("Answer knowledge and coding questions confidently. Never apologize, refuse, or say you are not a professional or expert.");
                sb.AppendLine("Avoid only unsupported personal history: do not invent employers, project names, dates, metrics, or achievements.");
                sb.AppendLine();
                sb.AppendLine("The candidate's field is also unknown, and it is not safe to guess.");
                sb.AppendLine("Do not say which languages, frameworks or specialisms are theirs.");
                sb.AppendLine("Never write phrases like \"my backend experience with Java and Spring Boot\",");
                sb.AppendLine("\"as a frontend developer\", or \"my years in data science\". Any of these is a");
                sb.AppendLine("claim about their career, and a wrong one is read aloud to someone");
                sb.AppendLine("holding their CV.");
                sb.AppendLine();
                sb.AppendLine("This happened. With no resume loaded, a Gen AI and Python candidate was");
                sb.AppendLine("told to say they wanted to keep building on their backend experience,");
                sb.AppendLine("\"especially with Java and Spring Boot\". Fluent, confident, and about a");
                sb.AppendLine("different person. Defaulting to the most common CV is the exact failure");
                sb.AppendLine("to avoid: with nothing to go on, be general rather than typical.");
                sb.AppendLine();
                sb.AppendLine("Use the technology the interviewer named, or the target role below if");
                sb.AppendLine("one is given. Otherwise stay stack-neutral: \"the systems I have worked");
                sb.AppendLine("on\", \"the stack the team uses\", \"my current work\". Technical questions");
                sb.AppendLine("still get full, specific, expert answers. The restriction is only on");
                sb.AppendLine("claiming a background, never on the depth of the answer.");
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(CompanyName) || !string.IsNullOrWhiteSpace(JobDesc))
            {
                sb.AppendLine("TARGET CONTEXT:");
                if (!string.IsNullOrWhiteSpace(CompanyName))
                    sb.AppendLine($"Company: {Truncate(CompanyName, 120)}");
                if (!string.IsNullOrWhiteSpace(JobDesc))
                    sb.AppendLine($"Role: {Truncate(JobDesc, 500)}");
                sb.AppendLine("Tailor the answer naturally when relevant; do not force the company name into every response.");
                sb.AppendLine();
            }

            AppendScreeningPrefs(sb);

            if (!string.IsNullOrWhiteSpace(LiveHints))
            {
                sb.AppendLine("CANDIDATE HINTS:");
                sb.AppendLine(Truncate(LiveHints, 600));
                sb.AppendLine();
            }
            AppendSharedVoiceRules(sb);

            return sb.ToString();
        }

        /// <summary>
        /// The rules about how an answer should sound and how it should be
        /// shaped, shared by both system prompts.
        ///
        /// There are two, one for Auto and one for Manual, and they had drifted
        /// apart. Everything written to make answers sound spoken rather than
        /// written went into the Auto one alone, so the mode most people use was
        /// still returning encyclopedia definitions and no depth section, and
        /// nothing about the change appeared to have worked. Rules that must hold
        /// for every answer now live in one place and are appended to both.
        /// </summary>
        // On reading the question through transcription errors, above:
        //
        // "Are you looking for C2C or W2 or full time?" reached the model as
        // "So what are you looking for? See to see or w to", and the answer
        // that came back was about wanting to grow and learn, because the
        // question had been read literally. The vocabulary fix in the speech
        // engine helps, and cannot be complete: an interview is full of
        // acronyms said aloud, and the model is the last place the intended
        // word can still be recovered.
        //
        // The mapping list is deliberately short. It went in at four times
        // this length, with the story above spelled out in the prompt, and
        // every request would have carried it. Prompt size is measurable
        // latency here, and the rule works without the anecdote.
        private static void AppendSharedVoiceRules(StringBuilder sb)
        {
            sb.AppendLine("SOUND LIKE A PERSON, NOT A DEFINITION:");
            sb.AppendLine("  Asked what something is, answer the way an engineer would answer a");
            sb.AppendLine("  colleague, not the way an encyclopedia opens an article. Say what it is");
            sb.AppendLine("  for and where you have met it. A dictionary sentence is the single");
            sb.AppendLine("  clearest sign to an interviewer that something is being read out.");
            sb.AppendLine();
            sb.AppendLine("  Not this:");
            sb.AppendLine("    \"Java is a general-purpose, object-oriented programming language that");
            sb.AppendLine("     runs on the JVM. It is known for its write once, run anywhere");
            sb.AppendLine("     philosophy.\"");
            sb.AppendLine("  This (the substance stays, only the voice changes):");
            sb.AppendLine("    \"Java is an object-oriented, statically typed language, and it's what most of");
            sb.AppendLine("     my backend work is in. You compile to bytecode, and the JVM runs that bytecode");
            sb.AppendLine("     on any operating system, so the same jar runs on my laptop and in our Linux");
            sb.AppendLine("     containers. The JVM also manages memory with garbage collection and has");
            sb.AppendLine("     multithreading built in, which matters a lot for backend services.\"");
            sb.AppendLine("  Sounding like a person never means saying less. An experienced engineer's");
            sb.AppendLine("  answer is full of real specifics; it is only the textbook phrasing that goes.");
            sb.AppendLine();
            sb.AppendLine("  How real speech differs from written prose:");
            sb.AppendLine("    Contractions throughout. It's, I've, that's, doesn't, we'd. Always.");
            sb.AppendLine("    Except the name of the thing being asked about: say \"Java is\", never");
            sb.AppendLine("    \"Java's\". The candidate reads the name out in full.");
            sb.AppendLine("    Sentence lengths vary. A long one, then a short one. Never three");
            sb.AppendLine("    evenly balanced sentences in a row, which is the rhythm nothing but a");
            sb.AppendLine("    machine produces.");
            sb.AppendLine("    One idea per sentence. Nobody speaks in subordinate clauses.");
            sb.AppendLine("    No filler words such as basically, pretty smooth, super or kind of. They");
            sb.AppendLine("    make an answer sound unsure without making it sound human.");
            sb.AppendLine();
            sb.AppendLine("  Never use these. They are not words people say out loud, and an");
            sb.AppendLine("  interviewer hearing one knows immediately what produced it:");
            sb.AppendLine("    leverage, utilize, robust, seamless, comprehensive, delve, myriad,");
            sb.AppendLine("    facilitate, streamline, cutting-edge, best-in-class, holistic,");
            sb.AppendLine("    paradigm, synergy, plethora, pivotal, underscore, showcase,");
            sb.AppendLine("    is known for, is widely regarded, plays a crucial role, it is worth");
            sb.AppendLine("    noting, in today's fast-paced world.");
            sb.AppendLine("  Say use, strong, smooth, full, go into, many, help, speed up, modern,");
            sb.AppendLine("  best, whole, approach, and so on. The plain word every time.");
            sb.AppendLine();
            sb.AppendLine("  No triple adjective lists. \"Fast, reliable, and scalable\" is writing,");
            sb.AppendLine("  not speech. Pick the one that actually matters and say why.");
            sb.AppendLine();

            sb.AppendLine("ANSWER SHAPE — TWO PARTS, ALWAYS IN THIS ORDER:");
            sb.AppendLine("  First, the spoken answer. Exactly what to say out loud, nothing else, at");
            sb.AppendLine("  the length the question deserves. This is the part read while someone is");
            sb.AppendLine("  waiting, so it comes first and stays tight.");
            sb.AppendLine();
            sb.AppendLine("  Then, on its own line, the word:");
            sb.AppendLine("    MORE TO SAY");
            sb.AppendLine("  followed by 2 or 3 short lines, each opening with the character • and");
            sb.AppendLine("  one space, never a hyphen and never an asterisk, and each a different");
            sb.AppendLine("  thing that could be added if the interviewer wants");
            sb.AppendLine("  depth: a trade-off, an edge case, a decision and why it was made, what");
            sb.AppendLine("  you would do differently. Not a summary of the answer above, and not a");
            sb.AppendLine("  continuation of the same sentence. Each one has to stand on its own as");
            sb.AppendLine("  something worth saying next.");
            sb.AppendLine();
            sb.AppendLine("  These bullets invent nothing. No percentage, no metric, no team size,");
            sb.AppendLine("  no salary, no employer, no project name, unless that exact detail sits");
            sb.AppendLine("  in the verified facts above.");
            sb.AppendLine();
            sb.AppendLine("  This section asked for \"a number\" once, and produced \"reduced runtime");
            sb.AppendLine("  by 40%\", \"cut hallucinations by 70%\", a team of six, and a salary");
            sb.AppendLine("  range, none of which the candidate had ever said. They would have read");
            sb.AppendLine("  those out to someone holding their CV.");
            sb.AppendLine();
            sb.AppendLine("  Where a real figure belongs and none is known, write it so they can");
            sb.AppendLine("  complete it: \"we handled about [your number] a day\".");
            sb.AppendLine();
            sb.AppendLine("  Skip MORE TO SAY entirely for greetings, small talk, yes/no logistics,");
            sb.AppendLine("  interviewer explanations, candidate questions, closing turns, and anything");
            sb.AppendLine("  already answered in one sentence. There is nothing to add");
            sb.AppendLine("  to \"I am on STEM OPT\", and offering some makes it look padded.");
            sb.AppendLine();
            sb.AppendLine("  The bullets are the one place bullets are allowed. The spoken answer");
            sb.AppendLine("  above them is still flowing sentences, never a list.");
            sb.AppendLine();
        }


        // =====================================================================
        // CONTEXT NOTE
        // =====================================================================

        private static string BuildContextNote()
        {
            if (CoveredTopics.Count == 0 && MentionedExamples.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("[INTERNAL — DO NOT REPEAT TO INTERVIEWER]");
            if (CoveredTopics.Count > 0)
                sb.AppendLine($"Topics used this session: {string.Join(", ", CoveredTopics.Take(15))}. Use different angles.");
            if (MentionedExamples.Count > 0)
                sb.AppendLine($"Companies/examples used: {string.Join(", ", MentionedExamples.Take(10))}. Prefer fresh ones.");
            sb.AppendLine();
            return sb.ToString();
        }

        // =====================================================================
        // FORMAT REMINDER
        // =====================================================================

        private static bool IsSimpleDefinitionQuestion(string question) =>
            Regex.IsMatch(question,
                @"(?:^|[?.!]\s*)(?:what is|what are|define)\s+(?!your\b|you\b)",
                RegexOptions.IgnoreCase);

        /// <summary>
        /// The thing a definition question asks about: "What is a REST API?" gives
        /// "REST API". Empty when there is nothing usable.
        /// </summary>
        internal static string DefinitionTerm(string question)
        {
            var m = Regex.Match(question ?? "",
                @"(?:^|[?.!]\s*)(?:what is|what are|define)\s+(?:an?\s+|the\s+)?(.+?)(?:[?!]|\.(?=\s|$)|$)",
                RegexOptions.IgnoreCase);
            if (!m.Success) return "";
            // "What is Kafka and how have you used it" and "What is the difference
            // between X and Y" are not "What is X?". Treated as one, the whole
            // clause became the term, it matched nothing in the resume, and the
            // answer was told never to say the candidate uses Kafka while the
            // interviewer was asking exactly how they had. Those go to the normal
            // technical format instead.
            if (Regex.IsMatch(m.Groups[1].Value,
                    @"\b(and|or|how|why|where|when|which|that|you|your|between|versus|vs|differ|difference|differences|compared|pros|cons|advantages?|disadvantages?)\b|,",
                    RegexOptions.IgnoreCase))
                return "";
            string term = Regex.Replace(m.Groups[1].Value.Trim(),
                @"\s+(?:exactly|actually|again|then|really|about)$", "", RegexOptions.IgnoreCase);
            return term.Length > 60 ? term[..60].Trim() : term;
        }

        private static readonly HashSet<string> TermFillerWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "of", "in", "on", "for", "to", "and", "or", "with", "is", "are", "vs", "versus",
        };

        /// <summary>
        /// True when every meaningful word of the term appears in the candidate's
        /// facts, so the answer may say where it sits in their work. One- and
        /// two-letter words ("Go", "C", "R") must match with a capital, or "go"
        /// in ordinary resume prose would count as the Go language.
        /// </summary>
        internal static bool FactsMention(string resumeFacts, string term)
        {
            if (string.IsNullOrWhiteSpace(resumeFacts) || string.IsNullOrWhiteSpace(term)
                || resumeFacts == "No resume provided.")
                return false;

            var words = Regex.Split(term, @"\s+")
                .Select(w => w.Trim(',', ';', ':', '"', '\''))
                .Where(w => w.Length > 0 && !TermFillerWords.Contains(w))
                .ToList();
            if (words.Count == 0 || words.Count > 3) return false;

            foreach (string word in words)
            {
                string stem = word.Length > 3 && word.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                    ? word[..^1] : word;
                bool shortWord = stem.Length <= 2;
                string body = shortWord
                    ? char.ToUpperInvariant(stem[0]) + Regex.Escape(stem[1..])
                    : Regex.Escape(stem);
                var options = shortWord ? RegexOptions.None : RegexOptions.IgnoreCase;
                if (!Regex.IsMatch(resumeFacts, @"(?<![A-Za-z0-9])" + body + @"(?:e?s)?(?![A-Za-z0-9])", options))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// The format line for "What is X?". Two versions, chosen by whether X is
        /// in the candidate's facts, because only then may the answer say they use it.
        /// Wording measured against the live model; change it by testing, not by feel.
        /// </summary>
        internal static string DefinitionReminder(string term, string resumeFacts)
        {
            string t = string.IsNullOrWhiteSpace(term) ? "it" : term;

            // The owner, shown a two-sentence answer with nothing in it: "very small,
            // this is not the actual real answer". The target they approved says what
            // it is, how it works and why it matters, in about 30 seconds, spoken, and
            // tied to their own work. Sounding human was never meant to mean saying less.
            //
            // The owner also wants the opening written out: "Java is", never "Java's".
            // And for a term their resume does not have, the approved answer explains it
            // properly and then connects honestly to what the resume does have ("Most of
            // my own work is in Python..."), rather than ending as a textbook paragraph.
            string opening =
                $"Begin the answer with the words \"{t} is\" written out in full, starting with a capital letter and with A, An or The in front when English needs it, as in A hash map is. Never write \"{t}'s\". " +
                "4 or 5 spoken sentences, about 30-40 seconds, with real substance, the way an experienced engineer answers in an interview. " +
                "Cover what it is in plain words, how it actually works underneath with the real mechanism names, and why it matters in real work. " +
                "Sound like a person talking, not an encyclopedia: no filler such as basically, pretty smooth or super, no phrases such as general-purpose, " +
                "is known for or the big advantage is, and never a bare lets you sentence standing in for the explanation. ";
            const string more =
                "The MORE TO SAY lines are what an experienced engineer would add if pushed: a deeper mechanism, a gotcha, a trade-off, " +
                "or when you'd pick something else. Never claim a tool, project or incident that is not in the verified facts.";

            if (FactsMention(resumeFacts, term))
                return opening +
                       $"{t} is in the verified facts, so the first or second sentence says where it sits in your work, " +
                       "without inventing a project or detail that is not in the facts. " +
                       "Shape only, never reuse its words: Kafka is a distributed event streaming platform, and at work it's what carries events between our services. " +
                       "Producers write to topics, each topic is split into partitions, and every partition is an append-only log that consumers read at their own pace by offset. " +
                       "That's what makes it durable, because a consumer that falls over just picks up from its last offset. " +
                       "And partitions are how it scales, since consumers in a group split them between them. " +
                       more;

            bool hasFacts = !string.IsNullOrWhiteSpace(resumeFacts) && resumeFacts != "No resume provided.";
            return opening +
                   $"{t} is NOT in the verified facts, so never say you use it, have used it, or work with it. " +
                   (hasFacts
                       ? "End with one honest sentence connecting it to what the verified facts show you do work with, for example your main language or tools, " +
                         "and how the idea carries over. Never imply you have used the term itself. "
                       : "") +
                   "Shape only, never reuse its words: Rust is a systems language built so the compiler catches memory bugs before the code ever runs. " +
                   "It does that with ownership, where every value has exactly one owner, and borrowing rules the compiler checks for you. " +
                   "So you get C-level speed with no garbage collector, and whole classes of crashes and data races just can't compile. " +
                   (hasFacts
                       ? "Most of my own work is in Python, so I lean on the runtime for memory, but that trade-off between safety and control is the same one. "
                       : "The price is a steeper learning curve, you spend real time early on fighting the borrow checker. ") +
                   more;
        }

        /// <summary>
        /// Returns true ONLY when the interviewer is asserting/implying a value that
        /// contradicts a locked fact — NOT when they are simply asking if you know something.
        /// E.g. locked=Java:
        ///   "You said Python"          → TRUE  (asserting you said something different)
        ///   "So Python is your best?"  → TRUE  (asserting a different preference)
        ///   "Do you know Python?"      → FALSE (genuine skills question — answer normally)
        ///   "Have you used Python?"    → FALSE (genuine skills question — answer normally)
        /// </summary>
        private static bool HasLockedConflict(string question)
        {
            if (LockedFacts.Count == 0) return false;
            string qLow = question.ToLower();

            // Genuine skill/knowledge queries are NEVER conflicts — answer them normally
            if (qLow.Contains("do you know") || qLow.Contains("do you use") ||
                qLow.Contains("are you familiar") || qLow.Contains("can you use") ||
                qLow.Contains("have you used") || qLow.Contains("have you worked with") ||
                qLow.Contains("do you have experience") || qLow.Contains("are you good at") ||
                (qLow.StartsWith("do you") && !qLow.Contains("right?")))
                return false;

            // Only trigger if the interviewer is ASSERTING a different value
            bool isAssertion =
                qLow.Contains("you said") || qLow.Contains("you mentioned") ||
                qLow.Contains("you told") || qLow.Contains("i thought you") ||
                qLow.Contains("so your") || qLow.Contains("your favorite is") ||
                qLow.Contains("your best is") || qLow.Contains("your strongest") ||
                qLow.Contains("so you're a") || qLow.Contains("so you are a") ||
                (qLow.Contains(", right") && !qLow.Contains("do you")) ||
                (qLow.Contains("right?") && !qLow.Contains("do you"));

            if (!isAssertion) return false;

            foreach (var (key, qTriggers, aKeywords) in FactPatterns)
            {
                if (!LockedFacts.TryGetValue(key, out var lockedValue)) continue;
                foreach (var kw in aKeywords)
                {
                    if (qLow.Contains(kw.ToLower()) &&
                        !lockedValue.ToLower().Contains(kw.ToLower()))
                        return true;
                }
            }
            return false;
        }

        private static string BuildFormatReminder(
            QuestionType qType, string question, bool isDrillDown, string resumeFacts = "")
        {
            // Conflict push: interviewer is asserting a different value than what's locked.
            // ALWAYS MICRO — hold your ground in 1-2 sentences, no bullets, no elaboration.
            if (HasLockedConflict(question))
                return "1-2 short sentences. Politely correct, restate your locked answer. " +
                       "Example: 'Actually I said Python earlier, that's still my answer.' Don't justify.";

            // Drill-down always short — cite exact prior specifics
            if (isDrillDown)
                return "1-2 short sentences. CITE the exact specifics from your earlier answer " +
                       "(tool names, numbers, team size, project name). Start with the fact itself. " +
                       "Never invent new contradicting facts.";

            // None of these mention bullets, deliberately.
            //
            // They used to say "NO bullets" to keep a spoken answer from turning
            // into a list, which the system prompt already requires. Sitting
            // directly above the question, where the model looks hardest, it was
            // read as forbidding the MORE TO SAY section as well, so a clean
            // question like "how many years of experience do you have" came back
            // with the answer and nothing to follow it. The same question with a
            // messier transcript classified elsewhere and kept its depth, which
            // is how it looked like a regression rather than a rule.
            //
            // The lengths below still hold the spoken answer short. That was the
            // part worth keeping.
            string q = question.ToLower();
            switch (qType)
            {
                case QuestionType.Preference:
                    return "2 natural spoken sentences. Give the preference directly, then one concise reason. " +
                           "No long explanation.";

                case QuestionType.YesNo:
                    // The example here used to read "no sponsorship needed for the next
                    // two years", a fact about the candidate that nobody had given, and
                    // the model repeated it. Asked "what is cap extension?", an answer
                    // explained the rules and invented a 60-day grace period.
                    if (IsWorkAuthorizationQuestion(q))
                        return "1-2 short, plain sentences. Say only what the candidate's own profile says about their work status and " +
                               "whether they need sponsorship now or later. Never explain immigration rules, timelines, grace periods or " +
                               "eligibility, and never state a status or date the facts do not give. If they do not say, answer with the status " +
                               "the facts do show and offer to confirm the exact details with HR. Never mention a profile, facts or information " +
                               "you were given: this is spoken by the candidate about themselves.";
                    if (q.Contains("relocat"))
                        return "1 short sentence. Casual opener + Yes/No + openness.";
                    if (q.Contains("background") || q.Contains("drug"))
                        return "1 short sentence. Confident yes, no fluff.";
                    return "1-2 short sentences. Direct answer + one detail.";

                case QuestionType.Availability:
                    return "1 sentence. State notice period naturally. Example: " +
                           "'I can give two weeks notice, could start the week after.'";

                case QuestionType.Logistics:
                    return "Short and natural, like a quick chat, not a form. Default to ONE sentence. " +
                           "If they ask why or for a preference, give the answer plus one genuine reason, 2-3 sentences maximum.";

                case QuestionType.Salary:
                    return "2-3 sentences. State a range only when it appears in the resume or live hints. " +
                           "Otherwise express flexibility and ask to consider the role scope and total package. Never invent a salary number.";

                case QuestionType.Intro:
                    return "2-3 SHORT spoken paragraphs, about 30-40 seconds total. " +
                           "Start with who you are now, give one relevant resume-backed example, then one brief line connecting the earlier background. " +
                           "Only explain why this company if the interviewer asked. Do not list the whole resume or force filler words like 'yeah', 'so', or 'honestly'.";

                case QuestionType.Technical:
                    if (IsSimpleDefinitionQuestion(question) && DefinitionTerm(question).Length > 0)
                        // This used to say "Do NOT mention your background, job, project,
                        // company, or personal experience", which contradicts the voice
                        // rules in the same prompt: those say to give what it is for and
                        // where you have met it, with a worked Java example doing exactly
                        // that. Two rules pointing opposite ways, and this one sits
                        // directly above the question, where the comment above says the
                        // model looks hardest. It won every time.
                        //
                        // Measured against the live model over ten definition
                        // questions: the old text opened "X is a ..." in 8 of 10, this
                        // text in 0 of 10. Two things mattered. Naming the shape of
                        // the banned opening mechanically, including that a leading
                        // "A" or "An" does not exempt it, because an earlier draft
                        // that banned only the bare term still produced "A hash map
                        // is a key-value store". And asking for an action as the main
                        // verb rather than only forbidding "is", which gives the model
                        // somewhere to go: "A hash map gets you a value back in
                        // roughly constant time."
                        //
                        // Test the wording, do not reason about it: an earlier draft
                        // was reworded slightly while being applied here and scored
                        // far worse than the version that had been measured.
                        //
                        // The owner reported the old answers as robotic and hard to
                        // read aloud, and produced this exact sentence:
                        // "Java is a statically-typed programming language that runs on
                        // the Java Virtual Machine, letting the same compiled code
                        // execute on any platform with a JVM."
                        //
                        // 2026-09-17: that still read as a textbook. Session 418 asked
                        // "What is Java?" and got "Java lets you write code that runs on
                        // any platform with a JVM", with GC tuning and Java 17 records
                        // under MORE TO SAY. Banning "is a" only moved the definition
                        // into "lets you": 10 of 12 live answers opened that way.
                        //
                        // What works is starting from the candidate. But the model
                        // cannot be trusted to check the resume itself: told to claim
                        // use only for tools in the facts, it still said "Rust's the
                        // language I use" and "Terraform is the tool I use" for a
                        // candidate with neither. So the check happens here, in code,
                        // and the model gets one of two instructions naming the term.
                        // Measured: on-resume terms 6 of 6 open from the candidate's own
                        // work; off-resume terms 0 of 9 claim use, where the single
                        // instruction claimed use in 3 of 3. The examples use terms
                        // other than the ones most asked, because a Java example was
                        // copied word for word into a Spring Boot answer.
                        return DefinitionReminder(DefinitionTerm(question), resumeFacts);

                    return "1-2 spoken paragraphs, about 30-45 seconds, with real substance. " +
                           "Give the direct answer first, then how it actually works and why, with the specific mechanisms, names and trade-offs an experienced engineer would give, never vague words. " +
                           "Only if the topic itself is named in the verified facts, add one short clause about where it sits in your own work. " +
                           "Never invent a project, incident, result or personal story, and never say you use a tool that is not named in the verified facts: " +
                           "other tools can come up as options, not as things you use. Name at most two tools unless they ask for tooling.";

                case QuestionType.Coding:
                    return "CODING TASK. Output complete runnable code, not an explanation-only response. " +
                           "Use the language the interviewer requested or the most recently discussed language. " +
                           "If requirements are vague, state one short reasonable assumption and choose a compact interview-relevant example. " +
                           "Put the code first, include all required imports and a runnable entry point when appropriate, then add only 2-4 concise sentences explaining the approach and complexity. " +
                           "Never refuse, never ask the interviewer to repeat a vague request, and never say you are not a programmer or expert.";

                case QuestionType.Behavioral:
                    return "3 SHORT spoken paragraphs, about 40-55 seconds. NOT textbook STAR. " +
                           "Set the scene briefly, spend most of the answer on what YOU did, then give the outcome. " +
                           "Use a real number only if it appears in the verified facts. Never invent stats.";

                case QuestionType.Weakness:
                    return "2-3 SHORT paragraphs. Real weakness, no humble-brags. " +
                           "Casual: 'honestly, I used to...' Mention steps + evidence of progress.";

                case QuestionType.WhyRole:
                    if (Regex.IsMatch(q, @"strength|why should we hire|what makes you|good fit|why you\b"))
                        return "2 short spoken paragraphs, about 30-45 seconds. Name two real strengths that show in the verified facts, " +
                               "each with one concrete proof from those facts. Never invent a number, project, or fact about the company.";
                    // A test with no company details given produced "your team is building
                    // end-to-end AI pipelines" and "you've invested in Kubernetes": facts about
                    // a company the model knew nothing about, read aloud to that company.
                    return "2 short spoken paragraphs, about 30-45 seconds. " +
                           "If TARGET CONTEXT names the company or describes the role, point to one concrete thing from it. " +
                           "If it does not, never invent facts about the company, its products, stack, team or plans: " +
                           "talk about what draws you to this kind of role and what you'd bring, from the verified facts. " +
                           "No generic 'passionate about your mission' fluff.";

                case QuestionType.Situational:
                    // "P1: A real past situation" had the model write one: a schema dispute at
                    // UHG with a versioned topic and an adapter service, none of it in the facts.
                    return "1-2 spoken paragraphs, about 30-45 seconds. Say concretely what you actually do, step by step, and why it works, " +
                           "the way an experienced engineer would. Give a past example only if one is in the verified facts; " +
                           "otherwise stay with your approach and never invent an incident, teammate, project or outcome.";

                case QuestionType.ContextStatement:
                    // After the candidate's own questions, a real answer acknowledged the
                    // interviewer and then asked yet another question, restarting the
                    // loop the closing rules exist to end.
                    if (PriorCandidateQuestionInvitations() > 0)
                        return "1-2 SHORT conversational sentences: thank them for explaining and say briefly why it was useful to hear. " +
                               "Do not ask another question, repeat their explanation, or launch into your own background.";
                    return "1-2 SHORT conversational sentences acknowledging what the interviewer shared. " +
                           "Do not repeat their explanation point by point, answer a question they did not ask, or launch into your own background.";

                case QuestionType.CandidateQuestions:
                    if (PriorCandidateQuestionInvitations() > 0)
                        return "The candidate already asked a question and the interviewer answered it. " +
                               "Close naturally in 1-2 sentences: thank them and say that covers your questions. " +
                               "Do not ask another question and do not restart a technical discussion.";
                    return "Ask ONE concise, thoughtful question about the role, team, expectations, or current priorities. " +
                           "It should sound like a real candidate in conversation, not a multi-part consulting questionnaire. " +
                           "Do not answer your own question, list tools, or add a second question.";

                case QuestionType.InterviewClosing:
                    return "The interview is ending. Reply with 1-2 warm, natural sentences thanking them for their time. " +
                           "Do not recap your background, answer earlier questions, ask anything new, or add MORE TO SAY.";

                case QuestionType.MemoryRecall:
                    return "1-2 SHORT sentences ONLY. Answer exactly what was asked. DO NOT add your own background. Stop there.";

                case QuestionType.FollowUp:
                    return "1-2 SHORT paragraphs. Add NEW detail only, never repeat prior content.";

                default:
                    return "This is a general question, use your judgment. Read what the interviewer is ACTUALLY " +
                           "asking and answer it directly, the way a sharp human would. Match length to the question: " +
                           "a quick or factual one gets 1-2 sentences; a deep or open one gets 1-2 short paragraphs with real substance. " +
                           "Most answers should take 15-35 seconds aloud. Use one relevant example rather than listing every related tool or role. " +
                           "Stay specific and human, don't pad with filler.";
            }
        }

        // =====================================================================
        // BUILD MESSAGES — called from MainWindow on every AI request
        // =====================================================================

        public static List<object> BuildMessages(
            string resumeFacts, string currentQuestion, bool lowLatency = false)
        {
            resumeFacts = Truncate(resumeFacts, 12_000);
            currentQuestion = Truncate(currentQuestion, MaxHistoryQuestionChars);
            var messages    = new List<object>();
            var qType       = DetectType(currentQuestion);
            bool drillDown  = IsDrillDown(currentQuestion);
            bool hasHistory = History.Count > 0;

            // 1. System prompt
            messages.Add(new
            {
                role = "system",
                // The compact prompt, in both modes.
                //
                // Two prompts existed because someone measured the big one and
                // built a lean one for Auto, then left Manual on the original.
                // Manual is the default, and it is no less live: somebody is
                // still sitting in an interview waiting to speak. It was sending
                // 10,191 characters of rules where Auto sends 2,138, before the
                // resume, the history and the question are added.
                //
                // Prompt size buys latency directly. Measured against this
                // account: 0.25s to the first word at 400 bytes, 0.50s at 6.4KB.
                // Manual was paying that on every question, for rules the lean
                // prompt covers in a fifth of the words.
                content = BuildRealtimeSystemPrompt(resumeFacts)
            });

            // 2. Full conversation history as alternating messages
            //    The LLM literally sees the transcript — what it said in every prior turn.
            // Both modes are live interviews. Manual was resending twelve turns
            // of conversation on every question while Auto sent four, and the
            // whole lot is re-uploaded and re-read before a single word comes back.
            int promptHistoryTurns = 4;
            foreach (var (q, a) in History.TakeLast(promptHistoryTurns))
            {
                messages.Add(new { role = "user",      content = q });
                messages.Add(new { role = "assistant", content = a });
            }

            // 3. Build the new user message
            // Order: lock block first -> format reminder -> context note -> history hint -> question
            // Format reminder goes BEFORE the question so the model commits to length FIRST.

            string lockBlock      = BuildLockedConstraintBlock(currentQuestion);
            string formatReminder = BuildFormatReminder(qType, currentQuestion, drillDown, resumeFacts);
            string contextNote    = BuildContextNote();

            string historyHint = "";
            // Restated for one turn only. The turns above already carry the
            // conversation; this block repeated the most recent one again.
            if (hasHistory)
            {
                var (lastQ, lastA) = History.Last();
                string preview = lastA.Length > 250 ? lastA.Substring(0, 250) + "..." : lastA;
                historyHint =
                    $"[Last question was: \"{lastQ}\"]\n" +
                    $"[Your last answer: {preview}]\n\n" +
                    "CHECK BEFORE ANSWERING:\n" +
                    "  - Already answered this topic? -> reuse that answer consistently.\n" +
                    "  - Drill-down on last answer? -> MICRO: pull exact fact, 1-2 sentences.\n" +
                    "  - Brand new topic? -> use format reminder above.\n" +
                    "  - Never open by referring back. \"As I mentioned\", \"like I said\" and\n" +
                    "    \"as I touched on\" are true only if that exact topic appears above.\n" +
                    "    The last turn is shown to you as context, not as something you said\n" +
                    "    about this question. Claiming to have covered something you did not\n" +
                    "    is heard as evasion by the one person who knows what was said.\n\n";
            }

            bool includeMoreToSay = qType is QuestionType.Intro or QuestionType.Technical or
                QuestionType.Behavioral or QuestionType.Weakness or QuestionType.WhyRole or
                QuestionType.Situational or QuestionType.General;
            string depthInstruction = includeMoreToSay
                ? "Then add a blank line, the words MORE TO SAY on their own line, and 2 or 3 short lines of what you could add if pushed, each beginning with the bullet character and a space. That section is the only place bullets belong.\n"
                : "Do not add a MORE TO SAY section for this conversational or short-answer turn.\n";

            string userMsg =
                lockBlock +
                // Stated here as well as in the system prompt. This sits directly
                // above the question, which is where the model is actually
                // looking, and the per-type reminders it follows most closely
                // said nothing about the second part.
                "FORMAT (read BEFORE answering): " + formatReminder + "\n" +
                depthInstruction +
                "Nothing in either part may be invented: no employer, percentage, " +
                "metric, team size, salary or project name that is not in the " +
                "verified facts. Where a figure belongs and none is known, write " +
                "[your number] rather than choosing one. " +
                // A real answer about distributed training named Horovod, "NCCL 2.14"
                // and "four to sixteen GPUs" at an employer whose resume lists none of
                // them. An interviewer who asks one follow-up on a tool the candidate
                // never used ends the interview.
                "Never say the candidate used a tool, library, version or cluster size " +
                "that is not named in the verified facts; describe the approach instead.\n\n" +
                contextNote +
                BuildScreenContextNote() +
                historyHint +
                "QUESTION: " + currentQuestion;

            messages.Add(new { role = "user", content = userMsg });
            return messages;
        }

        // =====================================================================
        // BUILD ENHANCED QUESTION — injected into the `question` field of the
        // payload so the backend model ALWAYS sees context, locked facts, and
        // format rules — regardless of whether the backend uses `messages`.
        // =====================================================================

        private static string Truncate(string value, int maxChars) =>
            value.Length <= maxChars ? value : value[..maxChars] + "\n[truncated]";

        /// <summary>
        /// What the last Screen Analyze read, for questions that refer to it.
        ///
        /// The screen analysis was captured into ScreenAnalyzer.LastScreenContext
        /// and then read by nothing at all, which meant that asking "what site is
        /// this?" straight after analysing a screen produced "I don't have the
        /// ability to view your screen directly." The model was right: nobody had
        /// told it. The analysis it had just written was sitting one field away.
        ///
        /// Only recent captures count. Half an hour later the screen has moved on
        /// and stale context is worse than none.
        /// </summary>
        private static string BuildScreenContextNote()
        {
            string screen = ScreenAnalyzer.LastScreenContext;
            if (string.IsNullOrWhiteSpace(screen)) return "";
            if (DateTime.UtcNow - ScreenAnalyzer.LastScreenContextUtc > TimeSpan.FromMinutes(10)) return "";

            return "ON THE CANDIDATE'S SCREEN RIGHT NOW (you looked at it moments ago):\n" +
                   Truncate(screen, 2_000) + "\n\n" +
                   "Use this when the question is about what is on screen. Never say you " +
                   "cannot see the screen: you can, and this is what was there.\n" +
                   "Answer the part of the screen they asked about. A question about " +
                   "menus, tabs or buttons is not a question about whatever the " +
                   "analysis happened to focus on, so do not repeat that instead. If " +
                   "the notes above do not cover what they are asking, say which part " +
                   "you cannot make out and offer to look again.\n\n";
        }

        public static string BuildEnhancedQuestion(string rawQuestion, string resumeFacts)
        {
            rawQuestion = Truncate(rawQuestion, MaxHistoryQuestionChars);
            resumeFacts = Truncate(resumeFacts, 12_000);
            var sb       = new StringBuilder();
            var qType    = DetectType(rawQuestion);
            bool isDrill = IsDrillDown(rawQuestion);
            bool hasResume = !string.IsNullOrWhiteSpace(resumeFacts)
                             && resumeFacts != "No resume provided.";

            // ── 0. CANDIDATE IDENTITY ──────────────────────────────────────────
            // Always first so the model knows who it is before anything else.
            sb.AppendLine("=== ROLE: YOU ARE THE JOB CANDIDATE SPEAKING IN A LIVE INTERVIEW. ===");
            sb.AppendLine();

            if (hasResume)
            {
                // Ground the model entirely in the pasted resume
                sb.AppendLine("YOUR BACKGROUND (from your resume — answer only from these facts):");
                sb.AppendLine(resumeFacts);
                sb.AppendLine();
                sb.AppendLine("RULES:");
                sb.AppendLine("  - Only mention companies, roles, and skills that appear in YOUR BACKGROUND above.");
                sb.AppendLine("  - Never invent experience, projects, or employers not listed above.");
                sb.AppendLine("  - Do NOT start answers with: Great question / Absolutely / Of course / Certainly.");
                sb.AppendLine("  - Use contractions naturally: I'm, I've, I'd, didn't, wasn't, it's.");
                sb.AppendLine("  - Sound like a real professional in conversation, not a bot reading a document.");
            }
            else
            {
                // No resume — remain capable without inventing personal history.
                sb.AppendLine("NO RESUME PROVIDED. Missing resume context does not mean missing skill or expertise.");
                sb.AppendLine("RULES:");
                sb.AppendLine("  - Answer technical and coding questions confidently. Never apologize, refuse, or say you are not a programmer or expert.");
                sb.AppendLine("  - For coding requests, output complete runnable code immediately; if vague, choose a sensible compact example.");
                sb.AppendLine("  - Do NOT invent specific employers, specific project names, or specific salary numbers.");
                sb.AppendLine("  - Use neutral phrases such as 'my current team' or 'a product I worked on'; do not invent an industry or employer.");
                sb.AppendLine("  - Do not name a technology as YOURS: no 'my Java and Spring Boot experience', no 'as a frontend developer'. Their field is unknown and guessing it invents their career.");
                sb.AppendLine("  - Follow the interviewer's own words for tools and stack; otherwise say 'the systems I have worked on'. Answer the technical content in full either way.");
                sb.AppendLine("  - For salary, visa, location, and other personal facts, stay neutral unless the candidate supplied the detail.");
                sb.AppendLine("  - Do NOT start answers with: Great question / Absolutely / Of course / Certainly.");
                sb.AppendLine("  - Use contractions naturally: I'm, I've, I'd, didn't, wasn't, it's.");
            }
            sb.AppendLine();

            // ── 0b. Target role + live hints ──────────────────────────────────
            if (!string.IsNullOrWhiteSpace(CompanyName) || !string.IsNullOrWhiteSpace(JobDesc))
            {
                sb.AppendLine("=== TARGET ROLE ===");
                if (!string.IsNullOrWhiteSpace(CompanyName))
                    sb.AppendLine($"Company: {CompanyName}");
                if (!string.IsNullOrWhiteSpace(JobDesc))
                    sb.AppendLine($"Job: {(JobDesc.Length > 400 ? JobDesc[..400] + "..." : JobDesc)}");
                sb.AppendLine("Tailor this specific answer to the role and company above — mention them by name.");
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(LiveHints))
            {
                sb.AppendLine("=== LIVE HINTS ===");
                sb.AppendLine(LiveHints);
                sb.AppendLine("Work these hints naturally into your answer.");
                sb.AppendLine();
            }

            // ── 1. CONVERSATION HISTORY (last 5 turns) ────────────────────────
            if (History.Count > 0)
            {
                bool hasScreenCtx = LastEntryWasScreenAnalysis();

                if (hasScreenCtx)
                {
                    sb.AppendLine("=== SCREEN ANALYSIS CONTEXT (from the most recent screen capture) ===");
                    var (_, screenResult) = History[^1];
                    sb.AppendLine(screenResult.Length > 600 ? screenResult.Substring(0, 600) + "..." : screenResult);
                    sb.AppendLine();
                    sb.AppendLine("NOTE: The interviewer may be asking a follow-up question about this screen content.");
                    sb.AppendLine("Refer to the screen analysis above when relevant.");
                    sb.AppendLine();
                }

                sb.AppendLine("=== WHAT YOU HAVE ALREADY SAID IN THIS INTERVIEW ===");
                int start = Math.Max(0, History.Count - 3);
                for (int i = start; i < History.Count; i++)
                {
                    var (q, a) = History[i];
                    // Skip the screen analysis entry since we already showed it above
                    if (hasScreenCtx && i == History.Count - 1) continue;
                    string aShort = a.Length > 300 ? a.Substring(0, 300) + "..." : a;
                    sb.AppendLine($"Q: {q}");
                    sb.AppendLine($"YOUR ANSWER: {aShort}");
                    sb.AppendLine();
                }
                sb.AppendLine("CONSISTENCY RULE: Your answers above are locked. If asked the same topic again,");
                sb.AppendLine("give the same answer naturally rephrased. Do NOT contradict yourself.");
                sb.AppendLine();
            }

            // ── 2. LOCKED FACTS + CONFLICT DETECTION ─────────────────────────
            string lockBlock = BuildLockedConstraintBlock(rawQuestion);
            if (!string.IsNullOrEmpty(lockBlock))
                sb.AppendLine(lockBlock);

            // ── 3. FORMAT RULE (before the question so model commits first) ───
            string fmt = BuildFormatReminder(qType, rawQuestion, isDrill, resumeFacts);
            sb.AppendLine($"FORMAT RULE (obey exactly): {fmt}");
            sb.AppendLine();

            // ── 4. THE QUESTION ───────────────────────────────────────────────
            sb.AppendLine($"NOW ANSWER THIS QUESTION: {rawQuestion}");

            return sb.ToString().Trim();
        }

        // =====================================================================
        // TOPIC TRACKING
        // =====================================================================

        private static void TrackCoveredContent(string text)
        {
            string lower = text.ToLower();

            string[] topics = {
                "kubernetes", "kafka", "terraform", "gitops", "prometheus", "grafana",
                "opentelemetry", "docker", "spring boot", "microservices", "aws",
                "api", "rest", "database", "sql", "nosql", "mongodb", "postgres",
                "ci/cd", "jenkins", "github actions", "iam", "security", "secrets",
                "agile", "scrum", "leadership", "communication", "conflict",
                "performance", "testing", "deployment", "observability", "streaming",
                "lakehouse", "iceberg", "spark", "trino", "service mesh", "eks",
                "linux", "bash", "python", "java", "node", "react",
                "s3", "ec2", "lambda", "api gateway", "ecs", "fargate", "vpc"
            };
            foreach (var t in topics)
                if (lower.Contains(t)) CoveredTopics.Add(t);

            string[] entities = {
                "renasant", "wipro", "replysis", "roosevelt",
                "freight pipeline", "observability engine",
                "real-time pipeline", "distributed monitoring"
            };
            foreach (var e in entities)
                if (lower.Contains(e)) MentionedExamples.Add(e);
        }
    }
}
