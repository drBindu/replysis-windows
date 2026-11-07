using System;
using System.Security.Cryptography;
using System.Text;

namespace InterviewCopilot
{
    /// <summary>
    /// Which account a local session file belongs to (round 33, 2026-11-07). Local transcripts sit in one folder per Windows
    /// user, so a second Replysis account signed in on the same Windows login listed the first account's questions and
    /// answers in Sessions. A new file now carries a short tag made from the account id (not the id itself), and the list
    /// shows only files with the signed-in account's tag. Files written before this, and files written without an account,
    /// carry no tag and are shown as before, since nothing says whose they are.
    /// </summary>
    internal static class SessionAccount
    {
        internal const string HeaderField = "acct ";

        /// <summary>The tag for an account id, or null when nobody is signed in.</summary>
        internal static string? Tag(string? userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return null;
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("replysis-session:" + userId));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        /// <summary>The header field to append for the signed-in account, or "" when there is none.</summary>
        internal static string HeaderSuffix(string? userId)
        {
            string? tag = Tag(userId);
            return tag == null ? "" : " | " + HeaderField + tag;
        }

        /// <summary>The tag written in a header's fields, or null when the header has none.</summary>
        internal static string? ReadTag(string[] headerParts)
        {
            for (int i = 3; i < headerParts.Length; i++)
            {
                string part = headerParts[i].Trim();
                if (part.StartsWith(HeaderField, StringComparison.Ordinal))
                {
                    string tag = part.Substring(HeaderField.Length).Trim();
                    return tag.Length == 0 ? null : tag;
                }
            }
            return null;
        }

        /// <summary>Whether a local file with this tag may be listed for the signed-in account.</summary>
        internal static bool Visible(string? fileTag, string? currentUserId)
        {
            if (fileTag == null) return true;
            return string.Equals(fileTag, Tag(currentUserId), StringComparison.Ordinal);
        }
    }
}
