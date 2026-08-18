using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace InterviewCopilot
{
    /// <summary>
    /// Parses resume text and extracts real facts (dates, jobs, skills)
    /// so the AI cannot hallucinate experience or skills.
    /// </summary>
    public static class ResumeParser
    {
        private static readonly Dictionary<string, int> MonthMap =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                {"jan",1},{"january",1},{"feb",2},{"february",2},
                {"mar",3},{"march",3},{"apr",4},{"april",4},
                {"may",5},{"jun",6},{"june",6},{"jul",7},{"july",7},
                {"aug",8},{"august",8},{"sep",9},{"sept",9},{"september",9},
                {"oct",10},{"october",10},{"nov",11},{"november",11},
                {"dec",12},{"december",12}
            };

        // Current date for "Present" calculations — properties so they
        // always reflect the real date even on long-running sessions.
        private static int NOW_YEAR  => DateTime.Now.Year;
        private static int NOW_MONTH => DateTime.Now.Month;

        // Cache: avoid re-parsing the same resume text on every AI call
        private static string? _cachedResumeText;
        private static string? _cachedFacts;

        public static void InvalidateCache()
        {
            _cachedResumeText = null;
            _cachedFacts = null;
        }

        /// <summary>
        /// Returns a clean summary of real resume facts for use in AI prompts.
        /// </summary>
        public static string ExtractFacts(string resume)
        {
            if (string.IsNullOrWhiteSpace(resume) || resume.Length < 50)
                return "No resume provided.";

            if (_cachedResumeText == resume && _cachedFacts != null)
                return _cachedFacts;

            var sb = new StringBuilder();

            // 1. Name — first short non-label, non-header line
            string[] nameSkip = { "resume", "curriculum vitae", "cv", "profile", "summary",
                                  "objective", "contact", "address", "phone", "email", "linkedin" };
            foreach (var line in resume.Split('\n'))
            {
                string t = line.Trim();
                string tl = t.ToLower();
                if (t.Length > 2 && t.Length < 50 && !t.Contains(":") && !t.StartsWith("•")
                    && !System.Array.Exists(nameSkip, w => tl.Contains(w))
                    && !RxStartsWithDigit.IsMatch(t)
                    && !RxEmailOrUrlChar.IsMatch(t))
                {
                    sb.AppendLine("Name: " + t);
                    break;
                }
            }

            // 2. Jobs and durations
            var jobs = ExtractJobs(resume);
            int totalMonths = CalculateTotalMonths(jobs);

            // State a total only when the dates were actually understood.
            //
            // Resumes are written every way a person can imagine, and no pattern
            // will read all of them. What it must never do is answer with
            // confidence from a misreading: this said "1 year" to a candidate
            // with eleven, because four of their five roles had been discarded
            // and the survivor was the oldest.
            //
            // So when nothing parsed, no number is claimed, and the raw dates
            // are handed over for the model to read instead. An answer built
            // from the resume's own words is worth far more than a total that
            // is wrong and sounds certain.
            if (jobs.Count > 0)
            {
                sb.AppendLine($"Total Experience: {totalMonths / 12} years {totalMonths % 12} months");
                sb.AppendLine("Work History:");
                foreach (var job in jobs)
                    sb.AppendLine($"  - {job.Label} | {job.DateRange} ({job.Months} months)");
            }
            else
            {
                sb.AppendLine("Total Experience: could not be read from this resume.");
                sb.AppendLine("Do not state a number of years. Describe the roles below instead,");
                sb.AppendLine("and if asked directly for a total, give the range of years covered.");
                sb.AppendLine("Lines mentioning dates:");
                foreach (var line in resume.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.Length is > 4 and < 200 && RxHasYear.IsMatch(t))
                        sb.AppendLine("  " + t);
                }
            }

            // 3. Skills
            sb.AppendLine("Skills:");
            bool inSkills = false;
            foreach (var line in resume.Split('\n'))
            {
                string t = line.Trim();
                string lower = t.ToLower();

                if (lower.Contains("skill") || lower.Contains("expertise") || lower.Contains("key skill"))
                    inSkills = true;
                else if (inSkills && (lower.Contains("experience") || lower.Contains("education") || lower.Contains("employment")))
                    inSkills = false;

                if (inSkills && t.Length > 5 && !lower.Contains("skill") && !lower.Contains("expertise"))
                    sb.AppendLine("  - " + t.TrimStart('•', '-', ' ', '\t'));
            }

            _cachedResumeText = resume;
            _cachedFacts = sb.ToString();
            return _cachedFacts;
        }

        // ── Job extraction ──────────────────────────────────────────

        public class JobEntry
        {
            public string Label { get; set; } = "";
            public string DateRange { get; set; } = "";
            public int Months { get; set; }
            public int StartIdx { get; set; }
            public int EndIdx { get; set; }
        }

        /// <summary>Any line carrying a plausible year, for the fallback above.</summary>
        private static readonly Regex RxHasYear = new(@"(19|20)\d{2}", RegexOptions.Compiled);

        private static readonly Regex RxStartsWithDigit = new(@"^\d",      RegexOptions.Compiled);
        private static readonly Regex RxEmailOrUrlChar   = new(@"[@|/\\]", RegexOptions.Compiled);

        /// <summary>
        /// Month names spelled out, rather than "three to nine letters".
        ///
        /// Text pulled out of a .docx loses the spacing between formatting runs,
        /// so a job title runs straight into the date after it: "neerApril 2024",
        /// "IDecember 2022", "entistFeb 2017". A pattern that accepts any short
        /// run of letters captured those whole, the month lookup failed, and the
        /// job was discarded without a word.
        ///
        /// On a real five-job resume four were thrown away and only the oldest
        /// survived, because it happened to be the one preceded by a space. The
        /// app then told the candidate they had one year of experience, and named
        /// a job they left in 2017, in the middle of an interview. Naming the
        /// months means the match starts at the month wherever it begins, and the
        /// glued prefix is simply not part of it.
        /// </summary>
        private const string MonthNames =
            "Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|" +
            "Aug(?:ust)?|Sep(?:t)?(?:ember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?";

        private static readonly Regex DatePattern = new Regex(
            $@"({MonthNames})\s*(\d{{4}})\s*[-–—]\s*(Present|Till\s*Date|(?:{MonthNames})\s*\d{{4}})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<JobEntry> ExtractJobs(string resume)
        {
            var jobs = new List<JobEntry>();
            var lines = resume.Split('\n');
            string lastHeader = "";

            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                var match = DatePattern.Match(t);

                if (match.Success)
                {
                    var entry = ParseMatch(match);
                    if (entry == null) continue;

                    // The label was taken from this line or the one above it, and
                    // resumes very commonly put the employer on the line BELOW the
                    // dates:
                    //
                    //     Role: Gen AI Engineer    April 2024 - Present
                    //     UHG, Minneapolis, MN
                    //
                    // Looking only backwards captured the job title and left the
                    // employer out, so asked to name the companies they had worked
                    // for, the app could only offer the single one that happened to
                    // be glued onto its own date line. Four employers were in the
                    // document and absent from everything built out of it.
                    string label = t.Replace(match.Value, "").Trim().TrimEnd('|', '-', ' ');
                    if (label.Length < 3) label = lastHeader;

                    string below = NextLineIfEmployer(lines, i);
                    if (below.Length > 0)
                        label = label.Length > 0 ? label + " | " + below : below;

                    entry.Label = label;
                    jobs.Add(entry);
                }

                if (t.Length > 3 && !match.Success)
                    lastHeader = t;
            }

            return jobs;
        }


        /// <summary>
        /// The line after a date range, when it reads like an employer rather
        /// than the start of the duties.
        ///
        /// Deliberately cautious: a wrong employer attached to a role is worse
        /// than none, because the candidate would read it out. Section headings
        /// and bullet lists are excluded, and anything long enough to be a
        /// sentence is left alone.
        /// </summary>
        private static readonly string[] NotAnEmployer =
        {
            "responsibilit", "environment", "duties", "description", "project",
            "client", "technolog", "skills", "achievement", "summary", "role:",
        };

        private static string NextLineIfEmployer(string[] lines, int dateLineIndex)
        {
            for (int j = dateLineIndex + 1; j < lines.Length && j <= dateLineIndex + 2; j++)
            {
                string next = lines[j].Trim();
                if (next.Length == 0) continue;

                // Bullets and long lines are the work, not the employer.
                if (next.Length > 80) return "";
                if (next[0] is '•' or '-' or '*' or '●') return "";

                string lower = next.ToLowerInvariant();
                foreach (string word in NotAnEmployer)
                    if (lower.StartsWith(word)) return "";

                // A second date range means the next job began; this is not an employer.
                if (DatePattern.IsMatch(next)) return "";

                return next.TrimEnd('.', ',', ' ');
            }
            return "";
        }

        private static JobEntry? ParseMatch(Match m)
        {
            try
            {
                string startMonthStr = m.Groups[1].Value;
                int startYear = int.Parse(m.Groups[2].Value);
                string endStr = m.Groups[3].Value.Trim();

                if (!MonthMap.TryGetValue(startMonthStr, out int startMonth)) return null;

                int startIdx = startYear * 12 + (startMonth - 1);
                int endIdx;

                if (endStr.ToLower().Contains("present") || endStr.ToLower().Replace(" ", "").Contains("tilldate"))
                {
                    endIdx = NOW_YEAR * 12 + (NOW_MONTH - 1);
                }
                else
                {
                    var parts = endStr.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) return null;
                    if (!MonthMap.TryGetValue(parts[0], out int endMonth)) return null;
                    if (!int.TryParse(parts[1], out int endYear)) return null;
                    endIdx = endYear * 12 + (endMonth - 1);
                }

                int months = Math.Max(0, endIdx - startIdx + 1);

                return new JobEntry
                {
                    DateRange = m.Value,
                    Months = months,
                    StartIdx = startIdx,
                    EndIdx = endIdx
                };
            }
            catch { return null; }
        }

        private static int CalculateTotalMonths(List<JobEntry> jobs)
        {
            if (jobs.Count == 0) return 0;

            // Merge overlapping intervals to avoid double-counting
            var intervals = jobs
                .Select(j => (j.StartIdx, j.EndIdx))
                .OrderBy(x => x.StartIdx)
                .ToList();

            var merged = new List<(int s, int e)>();
            var cur = intervals[0];

            for (int i = 1; i < intervals.Count; i++)
            {
                if (intervals[i].StartIdx <= cur.EndIdx)
                    cur = (cur.StartIdx, Math.Max(cur.EndIdx, intervals[i].EndIdx));
                else
                {
                    merged.Add(cur);
                    cur = intervals[i];
                }
            }
            merged.Add(cur);

            return merged.Sum(x => x.e - x.s + 1);
        }
    }
}