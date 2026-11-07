using System;
using System.Globalization;
using System.IO;

namespace InterviewCopilot
{
    /// <summary>
    /// Rules for a new user's first run that must not depend on how a particular Windows is set up (round 16, 2026-11-07).
    /// </summary>
    internal static class FirstRunRules
    {
        /// <summary>
        /// The speech engine shipped with the app has been taken away, most often by an antivirus that quarantined
        /// speechmatics_engine.exe and left the rest of the engine folder behind. The app then fell back to the developer
        /// path and told a customer to "Install Python 3.11 or newer, then run: pip install -r requirements.txt".
        /// </summary>
        internal static bool BundledEngineRemoved(string? appFolder)
        {
            if (string.IsNullOrEmpty(appFolder)) return false;
            string engineFolder = Path.Combine(appFolder, "engine");
            return Directory.Exists(engineFolder) && !File.Exists(Path.Combine(engineFolder, "speechmatics_engine.exe"));
        }

        internal const string EngineRemovedMessage =
            "Part of Replysis is missing on this computer, so listening can not start. Your antivirus may have removed it. " +
            "Allow Replysis in your antivirus, then install Replysis again from replysis.com.";

        /// <summary>
        /// A time for the server, written the same on every Windows. A custom format on its own still follows the
        /// computer's calendar: on a Thai Windows "yyyy" is the Buddhist year (2569), so lastActive was sent 543 years ahead.
        /// </summary>
        internal static string ServerTimestamp(DateTime utc) =>
            utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        /// <summary>
        /// Reads the time the app itself wrote in a session file header ("yyyy-MM-dd HH:mm:ss", written with the invariant
        /// culture). Read with the computer's culture, a Thai Windows took 2026 as a Buddhist year and showed the session
        /// in 1483.
        /// </summary>
        internal static bool TryReadSessionHeaderTime(string text, out DateTime value) =>
            DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
            || DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

        /// <summary>
        /// Whether Windows' microphone privacy settings refuse Replysis. The Store copy is a packaged app, so its own
        /// switch sits under the package family name, not under NonPackaged; with only the desktop apps switch checked, a
        /// Store user who had turned Replysis off was told to restart the computer instead.
        /// </summary>
        internal static bool MicrophoneRefused(Func<string, string?> machineValue, Func<string, string?> userValue, string? packageFamilyName)
        {
            const string path = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
            static bool Deny(string? v) => string.Equals(v, "Deny", StringComparison.OrdinalIgnoreCase);
            if (Deny(machineValue(path)) || Deny(userValue(path))) return true;
            return string.IsNullOrEmpty(packageFamilyName)
                ? Deny(userValue(path + @"\NonPackaged"))
                : Deny(userValue(path + "\\" + packageFamilyName));
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetCurrentPackageFamilyName(ref int length, System.Text.StringBuilder? name);

        /// <summary>The Store package family name, or null for the website build (not packaged).</summary>
        internal static string? PackageFamilyName()
        {
            try
            {
                int length = 0;
                if (GetCurrentPackageFamilyName(ref length, null) != 122 /* ERROR_INSUFFICIENT_BUFFER */ || length <= 0) return null;
                var sb = new System.Text.StringBuilder(length);
                return GetCurrentPackageFamilyName(ref length, sb) == 0 ? sb.ToString() : null;
            }
            catch { return null; }
        }
    }
}
