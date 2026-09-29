using System;

namespace InterviewCopilot
{
    /// <summary>
    /// Makes the live transcript appear the way typing does, instead of in whole phrases.
    ///
    /// The speech service sends a new version of the sentence a few times a second, and the
    /// screen used to swap to each one, so words arrived in jumps of five or ten at a time.
    /// That reads as lag even when the words are on time. This types them in a few characters
    /// every frame instead.
    ///
    /// Two promises, both tested:
    ///   - Never behind for long. The rate rises with the backlog, so the text on screen is
    ///     never more than about a third of a second behind what was heard, however long
    ///     the phrase that just arrived.
    ///   - Always a prefix of the real text. When the service revises earlier words, the
    ///     changed part is taken back and typed again; the screen never shows words that are
    ///     not in the transcript.
    /// </summary>
    internal static class TranscriptTyper
    {
        /// <summary>A fast typist. Applies when only a few characters are waiting.</summary>
        internal const double MinCharsPerSecond = 70;

        /// <summary>The backlog is worked off in about this long, so long phrases speed up.</summary>
        internal const double CatchUpSeconds = 0.12;

        /// <summary>Where the on-screen text should be after typing for <paramref name="elapsedSeconds"/> more.</summary>
        internal static string Advance(string? shown, string? target, double elapsedSeconds)
        {
            shown ??= "";
            target ??= "";
            if (target.Length == 0) return "";

            // Whatever already on screen still matches the real text is kept.
            int have = CommonPrefixLength(shown, target);
            int backlog = target.Length - have;
            if (backlog <= 0) return target;

            double perSecond = Math.Max(MinCharsPerSecond, backlog / CatchUpSeconds);
            int step = Math.Max(1, (int)Math.Round(perSecond * Math.Max(0, elapsedSeconds)));
            int end = Math.Min(target.Length, have + step);

            // Never split a character made of two UTF-16 units (an emoji, a rare script).
            if (end < target.Length && end > 0 && char.IsHighSurrogate(target[end - 1])) end++;
            return target.Substring(0, end);
        }

        internal static int CommonPrefixLength(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length);
            int i = 0;
            while (i < n && a[i] == b[i]) i++;
            return i;
        }
    }
}
