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
            new(@"```[A-Za-z0-9+#_-]*?
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
            "what do you see", "can you see", "do you see", "you can see",
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

        public static bool RefersToScreen(string question)
        {
            if (string.IsNullOrWhiteSpace(question)) return false;

            string q = question.ToLowerInvariant();
            foreach (string phrase in ScreenReferencePhrases)
                if (q.Contains(phrase, StringComparison.Ordinal)) return true;

            return NamesTheScreen.IsMatch(q);
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

        public static bool IsSmallTalk(string q)
        {
            string t = q.Trim().ToLower();

            // Only treat as pure small talk when the whole utterance is SHORT. A real
            // interview question that merely contains "how are you" (or follows a
            // greeting in the same breath) must never get the canned small-talk reply.
            if (t.Length > 60) return false;

            bool hasSmallTalk =
                t.Contains("how are you") || t.Contains("how's it going") ||
                t.Contains("how you doing") || t.Contains("how have you been") ||
                t.Contains("how is your day") || t.Contains("how's your day") ||
                t.Contains("how was your day") || t.Contains("how is your evening") ||
                t.Contains("how's your evening") || t.Contains("how is your night") ||
                t.Contains("nice to meet") || t.Contains("thanks for coming") ||
                t.Contains("pleasure to meet");
            if (!hasSmallTalk) return false;

            // If it also carries a substantive question, it's a real question, not chit-chat.
            string[] realQuestion =
            {
                "what", "how do", "how does", "why", "explain", "difference", "describe",
                "write", "implement", "design", "tell me about", "walk me through",
                "java", "python", "spring", "sql", "code", "algorithm", "project", "experience"
            };
            foreach (var k in realQuestion)
                if (t.Contains(k)) return false;

            return true;
        }

        public static string GetGreetingResponse() =>
            "Hey, great to be here, really looking forward to this conversation!";

        public static string GetSmallTalkResponse() =>
            "Doing really well, thanks! Excited to be here and learn more about the role.";

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

        private enum QuestionType
        {
            YesNo, Intro, Technical, Coding, Behavioral, Situational,
            Weakness, WhyRole, Salary, Availability, FollowUp,
            Preference, Logistics, ContextStatement, MemoryRecall, General
        }

        private static QuestionType DetectType(string q)
        {
            string t = q.ToLower().Trim();
            bool hasQuestionMark = t.Contains('?');

            bool startsWithInterviewerInfo =
                t.StartsWith("my name is") || t.StartsWith("i am ") || t.StartsWith("i'm ") ||
                t.StartsWith("we are ") || t.StartsWith("we're ") || t.StartsWith("this role") ||
                t.StartsWith("this position") || t.StartsWith("our company") ||
                t.StartsWith("the company") || t.StartsWith("i work at") ||
                t.StartsWith("i work for") || t.StartsWith("i currently") ||
                t.StartsWith("just so you know") || t.StartsWith("fyi") || t.StartsWith("by the way");
            if (startsWithInterviewerInfo && !hasQuestionMark)
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

            // Coding requests must be detected before the generic yes/no check.
            // "Can you write code?" is an instruction to produce code, not a yes/no question.
            if (IsCodingRequest(t))
                return QuestionType.Coding;

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
                       @"\bimplement\s+(a|an|the)?\s*[a-z0-9+#. -]{2,60}$");
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
            sb.AppendLine("Give a complete answer without wasting time: simple questions get 2-3 natural sentences; normal questions get 2-3 short spoken paragraphs.");
            sb.AppendLine("For open, behavioral, or technical questions, provide enough useful depth to speak for roughly 30-45 seconds.");
            sb.AppendLine("For behavioral questions, tell a concise STAR story without naming the STAR sections.");
            sb.AppendLine("For technical questions, give the direct answer first, then explain how it works, why it matters, and one relevant tradeoff or example.");
            sb.AppendLine("If asked to write, implement, or show code, output complete runnable code immediately. Never only describe the code, never refuse, and never claim you are not a programmer.");
            sb.AppendLine("When a coding request is vague, make one sensible interview-style assumption, use the requested or most recently discussed language, and provide a compact working example.");
            sb.AppendLine("Never invent employers, tools, dates, percentages, metrics, or achievements.");
            sb.AppendLine("Be specific and credible. Do not cut off a useful explanation, but never pad the answer with generic filler.");
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
            sb.AppendLine("    \"Java is a statically typed, object-oriented programming language that");
            sb.AppendLine("     runs on the JVM. It is known for its write once, run anywhere");
            sb.AppendLine("     philosophy.\"");
            sb.AppendLine("  This:");
            sb.AppendLine("    \"Java's what most of the backend work I've done is in. It's statically");
            sb.AppendLine("     typed, runs on the JVM, so the same build runs anywhere. Day to day");
            sb.AppendLine("     that mostly means Spring Boot services for me.\"");
            sb.AppendLine();
            sb.AppendLine("  How real speech differs from written prose:");
            sb.AppendLine("    Contractions throughout. It's, I've, that's, doesn't, we'd. Always.");
            sb.AppendLine("    Sentence lengths vary. A long one, then a short one. Never three");
            sb.AppendLine("    evenly balanced sentences in a row, which is the rhythm nothing but a");
            sb.AppendLine("    machine produces.");
            sb.AppendLine("    One idea per sentence. Nobody speaks in subordinate clauses.");
            sb.AppendLine("    Say \"so\" or \"basically\" or \"honestly\" where a person naturally");
            sb.AppendLine("    would, at most once in an answer. Not as a decoration on every one.");
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
            sb.AppendLine("  followed by 4 to 6 short lines, each opening with the character • and");
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
            sb.AppendLine("  and anything already answered in one sentence. There is nothing to add");
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
            QuestionType qType, string question, bool isDrillDown)
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
                    if (q.Contains("stem") || q.Contains("visa") || q.Contains("sponsorship"))
                        return "2-3 short sentences in plain language. Example: " +
                               "'Yeah I'm on STEM OPT, so no sponsorship needed for the next two years.'";
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
                    return "3-4 SHORT scannable paragraphs separated by blank lines. NO bullet symbols. " +
                           "P1: Who you are now + current role. P2: One specific win WITH a metric (only if it's in your resume) + tools used. " +
                           "P3: Previous role briefly. P4: Why this company (something specific). Mix sentence length. Use 'yeah', 'so', 'honestly'.";

                case QuestionType.Technical:
                    if (IsSimpleDefinitionQuestion(question))
                        return "3 concise spoken sentences. Give a plain-English definition first, explain the key mechanism or purpose, then add one practical detail or example. " +
                               "Do NOT mention your background, job, project, company, or personal experience unless the interviewer explicitly asks about it.";

                    return "3-4 SHORT paragraphs separated by blank lines. NO bullet symbols. " +
                           "Give a COMPLETE, substantive answer — enough depth to actually speak for 30-45 seconds. " +
                           "Start with the direct explanation in plain words, then go a level deeper: the how and the why, a trade-off or a concrete detail that shows real understanding. " +
                           "Use a real, resume-backed work example only when the interviewer asks about your experience or it genuinely clarifies the answer. " +
                           "Never invent a project, tool, result, or personal story. Don't stop after one thin sentence — flesh it out like a strong candidate who knows the topic.";

                case QuestionType.Coding:
                    return "CODING TASK. Output complete runnable code, not an explanation-only response. " +
                           "Use the language the interviewer requested or the most recently discussed language. " +
                           "If requirements are vague, state one short reasonable assumption and choose a compact interview-relevant example. " +
                           "Put the code first, include all required imports and a runnable entry point when appropriate, then add only 2-4 concise sentences explaining the approach and complexity. " +
                           "Never refuse, never ask the interviewer to repeat a vague request, and never say you are not a programmer or expert.";

                case QuestionType.Behavioral:
                    return "3-5 SHORT paragraphs separated by blank lines. NO bullet symbols. NOT textbook STAR. " +
                           "P1: Scene casually. P2: Concrete problem. P3: What YOU personally did. " +
                           "P4: How it turned out (use a real number ONLY if your resume has one, otherwise describe it qualitatively). NEVER invent stats.";

                case QuestionType.Weakness:
                    return "2-3 SHORT paragraphs. Real weakness, no humble-brags. " +
                           "Casual: 'honestly, I used to...' Mention steps + evidence of progress.";

                case QuestionType.WhyRole:
                    return "2-3 SHORT paragraphs. Name something CONCRETE about THIS company. " +
                           "No generic 'I'm passionate about your mission' fluff.";

                case QuestionType.Situational:
                    return "2-3 SHORT paragraphs. P1: A real past situation. P2: How it applies. Concrete specifics.";

                case QuestionType.ContextStatement:
                    return "1-2 SHORT conversational sentences acknowledging what the interviewer shared. Do NOT launch into your own introduction.";

                case QuestionType.MemoryRecall:
                    return "1-2 SHORT sentences ONLY. Answer exactly what was asked. DO NOT add your own background. Stop there.";

                case QuestionType.FollowUp:
                    return "1-2 SHORT paragraphs. Add NEW detail only, never repeat prior content.";

                default:
                    return "This is a general question, use your judgment. Read what the interviewer is ACTUALLY " +
                           "asking and answer it directly, the way a sharp human would. Match length to the question: " +
                           "a quick or factual one gets 1-2 sentences; a deep or open one gets 3-4 short paragraphs with real substance. " +
                           "When it's an open question, give a COMPLETE answer with enough depth to speak for 30-45 seconds — don't cut it short. " +
                           "Stay specific and human, NO bullet symbols, don't pad with filler.";
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
            string formatReminder = BuildFormatReminder(qType, currentQuestion, drillDown);
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

            string userMsg =
                lockBlock +
                // Stated here as well as in the system prompt. This sits directly
                // above the question, which is where the model is actually
                // looking, and the per-type reminders it follows most closely
                // said nothing about the second part.
                "FORMAT (read BEFORE answering): " + formatReminder + "\n" +
                "Then, unless this was a greeting, small talk, or a one-sentence " +
                "yes/no, add a blank line, the words MORE TO SAY on their own " +
                "line, and 4 to 6 lines of what you could add if pushed, each " +
                "beginning with the bullet character and a space. " +
                "That section is the only place bullets belong.\n" +
                "Nothing in either part may be invented: no employer, percentage, " +
                "metric, team size, salary or project name that is not in the " +
                "verified facts. Where a figure belongs and none is known, write " +
                "[your number] rather than choosing one.\n\n" +
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
            string fmt = BuildFormatReminder(qType, rawQuestion, isDrill);
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
