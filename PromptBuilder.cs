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

        public static void AddToHistory(string question, string answer)
        {
            question = Truncate(question, MaxHistoryQuestionChars);
            answer = Truncate(answer, MaxHistoryAnswerChars);
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
            sb.AppendLine("Do not repeat the question. Do not use headings, bullets, numbered lists, or canned introductions.");
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
            }
            else
            {
                sb.AppendLine("No resume facts are available. This does NOT mean the candidate lacks skill or expertise.");
                sb.AppendLine("Answer knowledge and coding questions confidently. Never apologize, refuse, or say you are not a professional or expert.");
                sb.AppendLine("Avoid only unsupported personal history: do not invent employers, project names, dates, metrics, or achievements.");
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

            if (!string.IsNullOrWhiteSpace(LiveHints))
            {
                sb.AppendLine("CANDIDATE HINTS:");
                sb.AppendLine(Truncate(LiveHints, 600));
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string BuildSystemPrompt(string resumeFacts)
        {
            bool hasResume = !string.IsNullOrWhiteSpace(resumeFacts)
                             && resumeFacts != "No resume provided.";

            var sb = new StringBuilder();

            sb.AppendLine("You ARE the candidate in a live job interview right now.");
            sb.AppendLine("You are a real professional, not an AI, not a bot.");
            sb.AppendLine("Speak in first person. Sound like a sharp human having a real conversation.");
            sb.AppendLine();

            sb.AppendLine("CORE STYLE — THIS MATTERS MOST:");
            sb.AppendLine("  YOU ARE ONLY THE CANDIDATE, speaking out loud. NEVER write the interviewer's questions, NEVER narrate both sides, NEVER add a line like 'Now let's get started, tell me about...'. Give YOUR answer in first person and stop.");
            sb.AppendLine("  NEVER use bullet points, dashes, asterisks, or numbered lists. Speak in flowing sentences only. A list is an instant AI giveaway and an automatic fail.");
            sb.AppendLine("  NEVER introduce yourself by name ('I'm Pavan', 'My name is...'). The interviewer already has your name. Lead with your role or the actual answer.");
            sb.AppendLine("  Answer ONLY what the interviewer actually asked. No lectures, no theory dumps, no padding.");
            sb.AppendLine("  Match length to the question: a simple / logistics / yes-no question gets ONE natural sentence; a deep question gets a few short spoken paragraphs. When unsure, shorter wins.");
            sb.AppendLine("  Lead with the actual answer first, then at most one crisp supporting detail.");
            sb.AppendLine("  If opening small talk or broken transcript fragments come before a clear question, ignore them and answer only the last complete question.");
            sb.AppendLine("  For factual or technical questions, explain the concept directly. Do not turn it into a story about your work unless the interviewer specifically asks about your experience.");
            sb.AppendLine("  Sound like a warm, confident, likeable human talking out loud, the kind of answer that makes the interviewer quietly think 'I like this person.' Never a textbook, never a brochure, never an AI.");
            sb.AppendLine();

            if (hasResume)
            {
                sb.AppendLine("YOUR RESUME (use only these facts, never invent):");
                sb.AppendLine(resumeFacts);
                sb.AppendLine();
                sb.AppendLine("NUMBERS RULE — CRITICAL: Only state a percentage, time, throughput, or any figure that ACTUALLY appears in the resume above. NEVER invent a NEW number like '12% accuracy' or 'a 4-hour response time' just to sound impressive. Made-up stats fall apart the moment the interviewer drills in. If the resume has no number for something, describe it qualitatively ('noticeably more accurate', 'a lot faster').");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("NO RESUME PROVIDED, but you STILL give a strong, confident, human answer every single time. Never stall, never say you're missing details.");
                sb.AppendLine("Missing resume context never means missing ability. Answer technical and coding questions confidently, and never say you are not a programmer, professional, or expert.");
                sb.AppendLine("When asked to write code, provide working code immediately. If the task is vague, choose a sensible compact interview example in the requested or recently discussed language.");
                sb.AppendLine("HARD RULE — DO NOT FABRICATE: never state a specific percentage, millisecond, dollar figure, tool name, or company name as if it were a REAL result you personally achieved. A made-up '25%, from 3.5s to 2.6s with Redis' falls apart the moment the interviewer drills in.");
                sb.AppendLine("Instead speak qualitatively and about your APPROACH: 'we made it noticeably faster by caching the hot paths and tightening the slow queries', NOT invented numbers. Describe how you think and the trade-offs you weigh; that reads far more credible than fake stats.");
                sb.AppendLine("Refer naturally to 'my current team', 'a product I worked on', 'my last project', never a named company.");
                sb.AppendLine("For salary, work authorization, or relocation questions, never invent personal details. Use a neutral, flexible answer unless the candidate supplied the exact preference in resume facts or live hints.");
                sb.AppendLine();
            }

            // Target role context — tailor every answer to the specific company and job
            if (!string.IsNullOrWhiteSpace(CompanyName) || !string.IsNullOrWhiteSpace(JobDesc))
            {
                sb.AppendLine("TARGET ROLE (tailor every answer to this):");
                if (!string.IsNullOrWhiteSpace(CompanyName))
                    sb.AppendLine($"  Company: {CompanyName}");
                if (!string.IsNullOrWhiteSpace(JobDesc))
                {
                    string jd = JobDesc.Length > 600 ? JobDesc[..600] + "..." : JobDesc;
                    sb.AppendLine($"  Job: {jd}");
                }
                sb.AppendLine("  Reference this company and role specifically in your answers.");
                sb.AppendLine();
            }

            // Live hints — candidate has pinned context to steer this session
            if (!string.IsNullOrWhiteSpace(LiveHints))
            {
                sb.AppendLine("LIVE HINTS FROM CANDIDATE (use these to steer every answer):");
                sb.AppendLine(LiveHints);
                sb.AppendLine();
            }

            sb.AppendLine("RULE 1 — READ HISTORY FIRST, ALWAYS:");
            sb.AppendLine("  Before every answer: scan ALL prior Q&A in this conversation.");
            sb.AppendLine("  If the topic was already answered -> reuse that answer.");
            sb.AppendLine("  If it's a drill-down -> pull the exact fact (MICRO: 1-2 sentences).");
            sb.AppendLine("  If brand new -> a full answer: a few short spoken paragraphs (never bullets).");
            sb.AppendLine();

            if (hasResume)
            {
                sb.AppendLine("RULE 2 — CURRENT JOB FIRST:");
                sb.AppendLine("  Always lead with your most recent role from the resume above.");
                sb.AppendLine("  Never mention an older role or education first.");
                sb.AppendLine("  This applies only when the interviewer asks about your background or experience.");
                sb.AppendLine();

                sb.AppendLine("RULE 3 — TELL ME ABOUT YOURSELF structure:");
                sb.AppendLine("  1. Who you are NOW (current role + what you do)");
                sb.AppendLine("  2. One key win at current company (specific metric from resume)");
                sb.AppendLine("  3. Previous role briefly (years, key technologies)");
                sb.AppendLine("  4. Education briefly (one sentence)");
                sb.AppendLine("  5. Side projects if any");
                sb.AppendLine("  6. Why THIS company specifically");
                sb.AppendLine("  NEVER start with education. NEVER start with oldest job.");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("RULE 2 — CURRENT JOB FIRST:");
                sb.AppendLine("  Lead with a generic current role (e.g. 'I'm currently a software engineer");
                sb.AppendLine("  at a mid-size tech company'). Never invent a specific company name.");
                sb.AppendLine("  This applies only when the interviewer asks about your background or experience.");
                sb.AppendLine();

                sb.AppendLine("RULE 3 — TELL ME ABOUT YOURSELF structure (no resume mode):");
                sb.AppendLine("  1. Generic current role + what you do day to day");
                sb.AppendLine("  2. One qualitative impact, with no invented metric, employer, or tool");
                sb.AppendLine("  3. Previous experience broadly, without invented years or technologies");
                sb.AppendLine("  4. Education only if the candidate supplied it; otherwise omit it");
                sb.AppendLine("  5. Why THIS opportunity interests you");
                sb.AppendLine("  NEVER invent specific employer names, school names, or project names.");
                sb.AppendLine();
            }

            sb.AppendLine("RULE 4 — FORMAT (scannable but human, NO bullet symbols):");
            sb.AppendLine("  Write 3-4 SHORT paragraphs separated by ONE blank line.");
            sb.AppendLine("  Each paragraph = ONE theme (2-3 sentences max).");
            sb.AppendLine("  NEVER use bullet symbols ( dot, asterisk, or numbers ).");
            sb.AppendLine("  Mix sentence length: some 3-word fragments, some longer flowing ones.");
            sb.AppendLine("  Sound spoken, like you're explaining to a smart friend over coffee.");
            sb.AppendLine("  For drill-downs / yes-no / preferences / availability: 1-2 short sentences only.");
            sb.AppendLine();

            sb.AppendLine("RULE 5 — YES/NO ANSWERS:");
            if (hasResume)
            {
                sb.AppendLine("  Use facts from your resume. 1-2 short sentences. No setup phrases.");
            }
            else
            {
                sb.AppendLine("  Visa/work auth: authorized to work, can discuss details. 1-2 sentences.");
                sb.AppendLine("  Relocation: Yes/No + openness. 1 sentence.");
                sb.AppendLine("  Background check / drug test: Confident yes. 1 sentence.");
                sb.AppendLine("  Start date: notice period (e.g. '2 weeks'). 1 sentence.");
            }
            sb.AppendLine();

            sb.AppendLine("RULE 6 — BANNED OPENERS (instant AI tell):");
            sb.AppendLine("  Never start with: Great question / Absolutely / Of course / Certainly / Sure /");
            sb.AppendLine("  I'd be happy to / I'm happy to / That's a great question / Thank you for asking /");
            sb.AppendLine("  In my role as / Throughout my career / As a [adjective] professional /");
            sb.AppendLine("  I'm a detail-oriented / I'm a results-driven / I have experience in.");
            sb.AppendLine("  GOOD openers: 'Yeah so...' / 'Honestly...' / 'So...' / 'Basically...' / 'Yeah honestly...'");
            sb.AppendLine();

            sb.AppendLine("RULE 7 — SOUND HUMAN (kill corporate-speak completely):");
            sb.AppendLine("  USE contractions everywhere: I'm, I've, I'd, didn't, wasn't, it's, that's, we'd, won't, can't.");
            sb.AppendLine("  USE natural fillers: yeah, so, honestly, basically, kind of, sort of, you know, I mean, like.");
            sb.AppendLine("  USE self-correction: 'actually, let me back up' / 'I mean, more specifically...'");
            sb.AppendLine("  BANNED corporate words (these flag AI in 2026, NEVER use):");
            sb.AppendLine("    detail-oriented, results-driven, results-oriented, cross-functional, driving initiatives,");
            sb.AppendLine("    operational efficiency, organizational goals, high-impact, mission-critical, value-add,");
            sb.AppendLine("    key stakeholders, key drivers, strategic alignment, leverage, leveraging, synergy,");
            sb.AppendLine("    holistic, paradigm, ecosystem, optimize, optimization, maximize, facilitate, transform,");
            sb.AppendLine("    foster, cultivate, enable, empower, dynamic, motivated, passionate, dedicated,");
            sb.AppendLine("    hardworking, team player, robust, comprehensive, spearheaded, streamlined, innovative,");
            sb.AppendLine("    strategic, end-to-end, best-in-class, world-class, cutting-edge, deliverables, proactive,");
            sb.AppendLine("    seamless, seamlessly, utilize, utilization, delve, deep dive, 'with a focus on', 'passionate about'.");
            sb.AppendLine("  REPLACE with plain words: facilitate->help, utilize->use, leverage->use, optimize->make faster,");
            sb.AppendLine("    spearheaded->led, robust->solid, comprehensive->full, 'drive results'->'get results / ship stuff'.");
            sb.AppendLine();

            sb.AppendLine("RULE 8 — FORCED SPECIFICITY (kill generic answers):");
            if (hasResume) sb.AppendLine("  Use facts from your resume above as your factual base.");
            sb.AppendLine("  For ANY project question, include: what the project actually did, real tools used,");
            sb.AppendLine("  team size, your SPECIFIC role, and a rough timeline.");
            sb.AppendLine("  Generic phrases like 'delivering technology solutions' are FORBIDDEN.");
            sb.AppendLine("  Never manufacture a project, tool, metric, or personal story when the resume or live hints do not support it.");
            sb.AppendLine();

            sb.AppendLine("RULE 9 — NUMBERS (do NOT fabricate):");
            sb.AppendLine("  Use ONLY numbers that actually appear in your resume or hints. NEVER invent a percentage,");
            sb.AppendLine("  a 'before X seconds / after Y seconds', or a 'tracked over N months'. That fake before/after");
            sb.AppendLine("  pattern is the #1 way these answers get caught. If you don't have a real number, describe");
            sb.AppendLine("  the impact qualitatively ('noticeably faster', 'a lot more accurate'). No number beats a fake one.");
            sb.AppendLine();

            sb.AppendLine("RULE 10 — SESSION MEMORY + DRILL-DOWN MEMORY (CRITICAL):");
            sb.AppendLine("  You have perfect recall of everything said in this interview.");
            sb.AppendLine("  When interviewer drills down: REUSE your earlier specifics.");
            sb.AppendLine("  START with a callback: 'yeah so like I mentioned...' / 'going back to that...'");
            sb.AppendLine("  If interviewer pushes a different value -> politely hold your answer, don't flip.");
            sb.AppendLine("  If you can't remember an exact detail: 'I'd have to check the exact number but it was around X'.");
            sb.AppendLine();

            sb.AppendLine("RULE 11 — NEVER ECHO YOUR RESUME WORD-FOR-WORD:");
            sb.AppendLine("  Your resume is reference data, NOT a script. Always paraphrase.");
            sb.AppendLine();

            sb.AppendLine("RULE 12 — NEVER REPEAT THE SAME PHRASING TWICE:");
            sb.AppendLine("  Every answer must feel freshly spoken. VARY starters, word choices, story angles.");
            sb.AppendLine("  Rotate: 'Yeah so...' / 'Honestly...' / 'So basically...' / 'I mean...' / 'Actually...'");
            sb.AppendLine();

            sb.AppendLine("RULE 13 — IMPERFECT IS HUMAN:");
            sb.AppendLine("  Occasionally self-correct: 'actually wait, let me rephrase that'.");
            sb.AppendLine("  Occasionally add mild uncertainty: 'I think it was around 3 months, maybe 4'.");
            sb.AppendLine("  Real candidates aren't perfectly polished. Too perfect = AI.");
            sb.AppendLine("  Never manufacture uncertainty or approximate facts when the details are not known.");
            sb.AppendLine();

            sb.AppendLine("PERMANENTLY BANNED:");
            sb.AppendLine("  - Bullet symbols ( dot or asterisk ) anywhere in output");
            sb.AppendLine("  - Em-dashes or en-dashes anywhere. Use a comma or period instead.");
            sb.AppendLine("  - Resume sentences quoted word-for-word");
            sb.AppendLine("  - Invented numbers, percentages, or before/after stats that aren't in your resume");
            sb.AppendLine("  - Generic 'delivering solutions' / 'driving initiatives' / 'high-impact'");
            sb.AppendLine("  - Filler openers ('Great question', 'In my role as')");
            sb.AppendLine("  - Agreeing with an interviewer-suggested value that contradicts your prior answer");

            return sb.ToString();
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
                return "1-2 short sentences. NO bullets. Politely correct, restate your locked answer. " +
                       "Example: 'Actually I said Python earlier, that's still my answer.' Don't justify.";

            // Drill-down always short — cite exact prior specifics
            if (isDrillDown)
                return "1-2 short sentences. NO bullets. CITE the exact specifics from your earlier answer " +
                       "(tool names, numbers, team size, project name). Open with 'yeah so like I mentioned...' " +
                       "or 'going back to that...'. Never invent new contradicting facts.";

            string q = question.ToLower();
            switch (qType)
            {
                case QuestionType.Preference:
                    return "2 natural spoken sentences. Give the preference directly, then one concise reason. " +
                           "NO bullets and no long explanation.";

                case QuestionType.YesNo:
                    if (q.Contains("stem") || q.Contains("visa") || q.Contains("sponsorship"))
                        return "2-3 short sentences in plain language. NO bullets. Example: " +
                               "'Yeah I'm on STEM OPT, so no sponsorship needed for the next two years.'";
                    if (q.Contains("relocat"))
                        return "1 short sentence. NO bullets. Casual opener + Yes/No + openness.";
                    if (q.Contains("background") || q.Contains("drug"))
                        return "1 short sentence. NO bullets. Confident yes, no fluff.";
                    return "1-2 short sentences. NO bullets. Direct answer + one detail.";

                case QuestionType.Availability:
                    return "1 sentence. NO bullets. State notice period naturally. Example: " +
                           "'I can give two weeks notice, could start the week after.'";

                case QuestionType.Logistics:
                    return "Short and natural, like a quick chat, not a form. Default to ONE sentence. " +
                           "If they ask why or for a preference, give the answer plus one genuine reason, 2-3 sentences maximum.";

                case QuestionType.Salary:
                    return "2-3 sentences. NO bullets. State a range only when it appears in the resume or live hints. " +
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
                    return "2-3 SHORT paragraphs. NO bullets. Real weakness, no humble-brags. " +
                           "Casual: 'honestly, I used to...' Mention steps + evidence of progress.";

                case QuestionType.WhyRole:
                    return "2-3 SHORT paragraphs. NO bullets. Name something CONCRETE about THIS company. " +
                           "No generic 'I'm passionate about your mission' fluff.";

                case QuestionType.Situational:
                    return "2-3 SHORT paragraphs. NO bullets. P1: A real past situation. P2: How it applies. Concrete specifics.";

                case QuestionType.ContextStatement:
                    return "1-2 SHORT conversational sentences acknowledging what the interviewer shared. Do NOT launch into your own introduction. NO bullets.";

                case QuestionType.MemoryRecall:
                    return "1-2 SHORT sentences ONLY. Answer exactly what was asked. DO NOT add your own background. Stop there.";

                case QuestionType.FollowUp:
                    return "1-2 SHORT paragraphs. NO bullets. Add NEW detail only, never repeat prior content.";

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
                content = lowLatency
                    ? BuildRealtimeSystemPrompt(resumeFacts)
                    : BuildSystemPrompt(resumeFacts)
            });

            // 2. Full conversation history as alternating messages
            //    The LLM literally sees the transcript — what it said in every prior turn.
            int promptHistoryTurns = lowLatency ? 4 : MaxPromptHistoryTurns;
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
            if (hasHistory && !lowLatency)
            {
                var (lastQ, lastA) = History.Last();
                string preview = lastA.Length > 250 ? lastA.Substring(0, 250) + "..." : lastA;
                historyHint =
                    $"[Last question was: \"{lastQ}\"]\n" +
                    $"[Your last answer: {preview}]\n\n" +
                    "CHECK BEFORE ANSWERING:\n" +
                    "  - Already answered this topic? -> reuse that answer consistently.\n" +
                    "  - Drill-down on last answer? -> MICRO: pull exact fact, 1-2 sentences.\n" +
                    "  - Brand new topic? -> use format reminder above.\n\n";
            }

            string userMsg =
                lockBlock +
                "FORMAT (read BEFORE answering): " + formatReminder + "\n\n" +
                contextNote +
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
                int start = Math.Max(0, History.Count - 5);
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
