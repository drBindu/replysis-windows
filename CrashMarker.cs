using System;
using System.IO;

namespace InterviewCopilot
{
    /// <summary>
    /// A small file that says the last run ended in a crash, read and removed on the next launch (bug round 39, 2026-11-07).
    ///
    /// A crash on a background thread ends the process the moment its handler returns, and the report to the server was sent
    /// fire and forget, so the worst crash of all usually never reached admin. The fatal path already wrote crash.flag, but
    /// nothing ever read it. Now the next launch reports "the previous run crashed" once, so the owner still finds out.
    /// </summary>
    internal static class CrashMarker
    {
        internal const string FileName = "crash.flag";

        internal static string DefaultFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InterviewCopilot");

        /// <summary>Writes the marker with the crash time and where it came from. Never throws.</summary>
        internal static void Write(string folder, string source, DateTime utcNow)
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, FileName), utcNow.ToString("o") + " " + source);
            }
            catch { /* nothing further to do if even this fails */ }
        }

        /// <summary>
        /// The marker's text when the last run crashed, and removes it so it is reported once. Null when the last run ended
        /// normally, or the marker cannot be read. A marker that cannot be removed is not reported, so it never repeats.
        /// </summary>
        internal static string? TakePrevious(string folder)
        {
            try
            {
                string path = Path.Combine(folder, FileName);
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path).Trim();
                File.Delete(path);
                return text.Length > 80 ? text[..80] : text;
            }
            catch { return null; }
        }
    }
}
