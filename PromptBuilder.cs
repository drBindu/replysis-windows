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

        // ── Topics + companies already used this session ──────────────────────
        private static readonly HashSet<string> CoveredTopics =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> MentionedExamples =
            new(StringComparer.OrdinalIgnoreCase);

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
            History.Add((question, answer));
            if (History.Count > 80) History.RemoveAt(0);
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
        }

        public static bool IsGreeting(string q)
        {
            string t = q.Trim().ToLower().TrimEnd('.', '!', '?', ' ');
            return t is "hi" or "hello" or "hey" or "hi there" or
                   "good morning" or "good afternoon" or "good evening" or
                   "greetings" or "hey there";
        }

        public static bool IsSmallTalk(string q)
        {
            string t = q.ToLower();
            return t.Contains("how are you") || t.Contains("how's it going") ||
                   t.Contains("how you doing") || t.Contains("how have you been") ||
                   t.Contains("nice to meet") || t.Contains("thanks for coming") ||
                   t.Contains("pleasure to meet");
        }

        public static string GetGreetingResponse() =>
            "Hey, great to be here — really looking forward to this conversation!";

        public static string GetSmallTalkResponse() =>
            "Doing really well, thanks! Excited to be here and learn more about the role.";

        public static string BuildVerifyPrompt() =>
            "State my most recent degree, current employer, and city of residence in 1 short sentence.";

        // =====================================================================
        // QUESTION TYPE
        // =====================================================================

        private enum QuestionType
        {
            YesNo, Intro, Technical, Behavioral, Situational,
            Weakness, WhyRole, Salary, Availability, FollowUp,
            Preference, General
        }

        private static QuestionType DetectType(string q)
        {
            string t = q.ToLower().Trim();

            if (t.Contains("tell me more") || t.Contains("can you elaborate") ||
                t.Contains("expand on that") || t.Contains("go deeper") ||
                t.Contains("what do you mean by") || t.Contains("elaborate on") ||
                t.Contains("go on") || t.Contains("continue"))
                return QuestionType.FollowUp;

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

        // =====================================================================
        // DRILL-DOWN DETECTION
        // =====================================================================

        private static bool IsDrillDown(string q)
        {
            if (History.Count == 0) return false;
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

        private static string BuildSystemPrompt(string resumeFacts)
        {
            bool hasResume = !string.IsNullOrWhiteSpace(resumeFacts)
                             && resumeFacts != "No resume provided.";

            var sb = new StringBuilder();

            sb.AppendLine("You ARE the candidate in a live job interview right now.");
            sb.AppendLine("You are a real professional — not an AI, not a bot.");
            sb.AppendLine("Speak in first person. Sound like a sharp human having a real conversation.");
            sb.AppendLine();

            if (hasResume)
            {
                sb.AppendLine("YOUR RESUME (use only these facts, never invent):");
                sb.AppendLine(resumeFacts);
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("NO RESUME PROVIDED.");
                sb.AppendLine("Answer as a generic software engineering professional.");
                sb.AppendLine("CRITICAL: Do NOT invent or use any specific employer names, company names,");
                sb.AppendLine("project names, university names, or salary numbers.");
                sb.AppendLine("Use generic placeholders like 'a mid-size tech company' or 'my previous employer'.");
                sb.AppendLine("For salary: give a range like $100k-$130k base, open to discussion.");
                sb.AppendLine("For visa: say you are authorized to work and can discuss details if needed.");
                sb.AppendLine();
            }

            sb.AppendLine("RULE 1 — READ HISTORY FIRST, ALWAYS:");
            sb.AppendLine("  Before every answer: scan ALL prior Q&A in this conversation.");
            sb.AppendLine("  If the topic was already answered -> reuse that answer.");
            sb.AppendLine("  If it's a drill-down -> pull the exact fact (MICRO: 1-2 sentences).");
            sb.AppendLine("  If brand new -> FULL mode with bullets.");
            sb.AppendLine();

            if (hasResume)
            {
                sb.AppendLine("RULE 2 — CURRENT JOB FIRST:");
                sb.AppendLine("  Always lead with your most recent role from the resume above.");
                sb.AppendLine("  Never mention an older role or education first.");
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
                sb.AppendLine();

                sb.AppendLine("RULE 3 — TELL ME ABOUT YOURSELF structure (no resume mode):");
                sb.AppendLine("  1. Generic current role + what you do day to day");
                sb.AppendLine("  2. One generic win (e.g. 'reduced build time by 30%' — no company name)");
                sb.AppendLine("  3. Previous experience briefly (years, technologies — no company name)");
                sb.AppendLine("  4. Education briefly — say 'Computer Science background' without naming a school");
                sb.AppendLine("  5. Why THIS opportunity interests you");
                sb.AppendLine("  NEVER invent specific employer names, school names, or project names.");
                sb.AppendLine();
            }

            sb.AppendLine("RULE 4 — ANSWER FORMATS:");
            sb.AppendLine("  MICRO  (1-2 sentences, NO bullets): drill-downs, yes/no, availability, repeat questions.");
            sb.AppendLine("  MEDIUM (2-3 bullets, using dot): follow-ups going deeper.");
            sb.AppendLine("  FULL   (4-5 bullets, using dot): new technical/behavioral/intro topics.");
            sb.AppendLine("  Bullets use dot symbol only. Never -, *, or numbers.");
            sb.AppendLine("  Each bullet = 1-2 sentences. Short. Spoken. Punchy.");
            sb.AppendLine();

            sb.AppendLine("RULE 5 — YES/NO ANSWERS (always MICRO):");
            if (hasResume)
            {
                sb.AppendLine("  Use specific facts from your resume above for visa, relocation, start date.");
            }
            else
            {
                sb.AppendLine("  Visa/work auth: say you are authorized to work, can discuss details. 1-2 sentences.");
                sb.AppendLine("  Relocation: Yes/No + openness. 1 sentence. No specific city unless asked.");
                sb.AppendLine("  Background check / drug test: Confident yes. 1 sentence.");
                sb.AppendLine("  Start date: state a generic notice period (e.g. '2 weeks'). 1 sentence.");
            }
            sb.AppendLine();

            sb.AppendLine("RULE 6 — BANNED OPENERS:");
            sb.AppendLine("  Never start with: Great question / Absolutely / Of course / Certainly / Sure.");
            sb.AppendLine("  Start with the answer, or: Yeah so... / Honestly... / So...");
            sb.AppendLine();

            sb.AppendLine("RULE 7 — SOUND HUMAN (contractions always):");
            sb.AppendLine("  Use: I'm, I've, I'd, didn't, wasn't, it's, that's, we'd, couldn't.");
            sb.AppendLine("  Natural openers: 'Yeah so...' / 'Honestly...' / 'What I found was...'");
            sb.AppendLine("  BANNED words: robust, comprehensive, spearheaded, streamlined, leverage,");
            sb.AppendLine("  synergy, utilize, delve, passionate about, results-driven, innovative.");
            sb.AppendLine("  BANNED phrases: 'I am proficient in' / 'I possess' / 'I am responsible for'");
            sb.AppendLine("  Say instead: 'I work with' / 'I have' / 'I handle'");
            sb.AppendLine();

            sb.AppendLine("RULE 8 — BE SPECIFIC (but only with facts you actually have):");
            if (hasResume)
            {
                sb.AppendLine("  Name the company (from resume). Name the tool. Give the number. State the outcome.");
                sb.AppendLine("  BAD: 'I worked on cloud infra and improved things.'");
                sb.AppendLine("  GOOD: 'At [company from resume], using [tool], we cut [metric] by [number].'");
            }
            else
            {
                sb.AppendLine("  Give realistic generic metrics. Do NOT name specific companies or schools.");
                sb.AppendLine("  BAD: 'At Renasant Bank / Microsoft / Google...'");
                sb.AppendLine("  GOOD: 'At my previous company, using Terraform, we cut setup time by 40%.'");
            }
            sb.AppendLine();

            sb.AppendLine("RULE 9 — SESSION MEMORY (most important rule):");
            sb.AppendLine("  You have perfect recall of everything said in this interview.");
            sb.AppendLine("  Every prior Q&A is something YOU said. Those facts are locked.");
            sb.AppendLine("  If asked the same topic again -> give the SAME answer, naturally rephrased.");
            sb.AppendLine("  If interviewer pushes a different value -> politely hold your answer.");
            sb.AppendLine();

            sb.AppendLine("RULE 10 — NATURAL MEMORY CALLBACKS:");
            sb.AppendLine("  'Yeah, like I mentioned...' / 'Going back to what I said...'");
            sb.AppendLine("  NEVER say 'As I mentioned in my previous answer' - robotic.");
            sb.AppendLine();

            sb.AppendLine("PERMANENTLY BANNED:");
            sb.AppendLine("  - Filler openers");
            sb.AppendLine("  - Inventing employers, schools, projects, or salary numbers not in your resume");
            sb.AppendLine("  - Bullets when MICRO mode required");
            sb.AppendLine("  - Paragraphs/theory when asked a simple preference");
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
                return "MICRO: 1-2 sentences MAX. No bullets. Politely correct the interviewer and " +
                       "restate your locked answer. EXAMPLE: 'Actually I said Python earlier — " +
                       "that's still my answer.' Do NOT explain or justify with bullets.";

            // Drill-down always MICRO regardless of question type
            if (isDrillDown)
                return "MICRO: 1-2 sentences MAX. No bullets. Pull the exact fact. Nothing else.";

            switch (qType)
            {
                case QuestionType.Preference:
                    return "MICRO: 1 sentence ONLY. Say the name + one short reason. " +
                           "CORRECT: 'Java — that's what I've worked with the most.' " +
                           "WRONG: bullets, theory, history, explanation. " +
                           "Check locked facts first. If topic was already answered, use that answer.";

                case QuestionType.YesNo:
                    string q = question.ToLower();
                    if (q.Contains("stem") || q.Contains("visa") || q.Contains("sponsorship"))
                        return "MICRO: 2-3 sentences. No bullets. Confirm work authorization status.";
                    if (q.Contains("relocat"))
                        return "MICRO: 1 sentence. No bullets. Yes/No + openness to location.";
                    if (q.Contains("background") || q.Contains("drug"))
                        return "MICRO: 1 sentence. No bullets. Confident yes.";
                    return "MICRO: 1-2 sentences. No bullets. Direct answer + one supporting fact.";

                case QuestionType.Availability:
                    return "MICRO: 1 sentence. No bullets. State notice period directly.";

                case QuestionType.Salary:
                    return "MICRO: 2-3 sentences. No bullets. Give a range. Total comp flexible.";

                case QuestionType.Intro:
                    return "FULL: 5-6 bullets (dot only). " +
                           "Order: current role -> key win (metric) -> previous role briefly -> " +
                           "education briefly -> side projects -> why this company. Never start with education.";

                case QuestionType.Technical:
                    return "FULL: 4-5 bullets (dot only). " +
                           "Bullet 1 = 1-sentence definition. Bullet 2 = example from your experience + outcome. " +
                           "Practitioner tone, not textbook.";

                case QuestionType.Behavioral:
                    return "FULL: 4-5 bullets (dot only). " +
                           "STAR: Situation -> Action -> Result with numbers. Most recent experience first.";

                case QuestionType.Weakness:
                    return "FULL: 3-4 bullets (dot only). Real weakness -> steps taken -> evidence of progress.";

                case QuestionType.WhyRole:
                    return "FULL: 3-4 bullets (dot only). Specific to this company's tech stack. No generic answers.";

                case QuestionType.Situational:
                    return "FULL: 3-4 bullets (dot only). Real past situation first, then apply to scenario.";

                case QuestionType.FollowUp:
                    return "MEDIUM: 2-3 bullets (dot only). New detail only. Never repeat prior content.";

                default:
                    return "FULL: 4-5 bullets (dot only). Use examples from your experience. Specific tools + numbers.";
            }
        }

        // =====================================================================
        // BUILD MESSAGES — called from MainWindow on every AI request
        // =====================================================================

        public static List<object> BuildMessages(string resumeFacts, string currentQuestion)
        {
            var messages    = new List<object>();
            var qType       = DetectType(currentQuestion);
            bool drillDown  = IsDrillDown(currentQuestion);
            bool hasHistory = History.Count > 0;

            // 1. System prompt
            messages.Add(new { role = "system", content = BuildSystemPrompt(resumeFacts) });

            // 2. Full conversation history as alternating messages
            //    The LLM literally sees the transcript — what it said in every prior turn.
            foreach (var (q, a) in History)
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

        public static string BuildEnhancedQuestion(string rawQuestion, string resumeFacts)
        {
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
                // No resume — give generic professional answers without hallucinating specifics
                sb.AppendLine("NO RESUME PROVIDED. Answer as a generic software engineering professional.");
                sb.AppendLine("RULES:");
                sb.AppendLine("  - Do NOT invent specific employers, specific project names, or specific salary numbers.");
                sb.AppendLine("  - Give plausible, honest-sounding generic answers (e.g. 'a mid-size fintech company').");
                sb.AppendLine("  - If asked about specific companies or projects you have not mentioned, say you'd prefer");
                sb.AppendLine("    to share more detail once you know more about the role.");
                sb.AppendLine("  - For salary: give a range like $100k-$130k base, open to discussion.");
                sb.AppendLine("  - For visa: say you are authorized to work and can discuss details if needed.");
                sb.AppendLine("  - Do NOT start answers with: Great question / Absolutely / Of course / Certainly.");
                sb.AppendLine("  - Use contractions naturally: I'm, I've, I'd, didn't, wasn't, it's.");
            }
            sb.AppendLine();

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
                "renasant", "wipro", "coopilotx", "roosevelt",
                "freight pipeline", "observability engine",
                "real-time pipeline", "distributed monitoring"
            };
            foreach (var e in entities)
                if (lower.Contains(e)) MentionedExamples.Add(e);
        }
    }
}
