using System;
using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// A second Replysis account on the same Windows login must not see the first account's local transcripts (round 33,
/// 2026-11-07). New session files carry an account tag; untagged older files are shown as before.
/// </summary>
internal static class SessionAccountTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        string? a = SessionAccount.Tag("uidA123");
        string? b = SessionAccount.Tag("uidB456");
        Check(a != null && a.Length == 16, "an account gets a 16 character tag");
        Check(a == SessionAccount.Tag("uidA123"), "the same account always gets the same tag");
        Check(a != b, "two accounts get different tags");
        Check(a != null && !a.Contains("uidA123"), "the tag does not contain the account id");
        Check(SessionAccount.Tag("") == null && SessionAccount.Tag(null) == null, "nobody signed in has no tag");
        Check(SessionAccount.HeaderSuffix(null) == "", "a header written signed out is unchanged");

        string header = "SESSION 3 | gpt-oss-120b | 2026-11-07 09:05:03" + SessionAccount.HeaderSuffix("uidA123");
        string[] parts = header.Split('|');
        Check(parts.Length == 4, "the tag is one more header field after the date");
        Check(parts[2].Trim() == "2026-11-07 09:05:03", "the date field stays where the old parser reads it");
        Check(SessionAccount.ReadTag(parts) == a, "the tag reads back from the header");
        Check(SessionAccount.ReadTag("SESSION 3 | model | 2026-11-07 09:05:03".Split('|')) == null, "an old header has no tag");

        Check(SessionAccount.Visible(a, "uidA123"), "an account sees its own sessions");
        Check(!SessionAccount.Visible(a, "uidB456"), "another account does not see them");
        Check(!SessionAccount.Visible(a, ""), "signed out does not see a tagged session");
        Check(SessionAccount.Visible(null, "uidB456"), "an untagged older session is still listed");

        return failed;
    }
}
