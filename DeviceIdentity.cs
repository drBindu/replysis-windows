using Microsoft.Win32;
using System;
using System.IO;

namespace InterviewCopilot
{
    // Hardware-tied identifier so the backend can grant a free trial per physical machine
    // without an account, and cap + reset it monthly server-side.
    // Survives app uninstall because the Machine GUID lives in the OS registry —
    // deleting or reinstalling the app cannot reset the device's credit counter.
    internal static class DeviceIdentity
    {
        private static string? _cached;
        public static string Current => _cached ??= Resolve();

        private static string Resolve()
        {
            // Primary: Windows MachineGuid — written by the OS installer, never changes
            // across app installs/uninstalls, unique per physical machine activation.
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                if (key?.GetValue("MachineGuid") is string guid && !string.IsNullOrWhiteSpace(guid))
                    return guid;
            }
            catch { }

            // Fallback: persist a random UUID to AppData.
            // Weaker than the registry path (a user could delete the file), but works
            // on any configuration and is a far better UX than refusing to start.
            return PersistFallbackId();
        }

        private static string PersistFallbackId()
        {
            string dir  = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "InterviewCopilot");
            string path = Path.Combine(dir, "device_id.txt");
            try
            {
                if (File.Exists(path))
                {
                    string existing = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrWhiteSpace(existing)) return existing;
                }
                Directory.CreateDirectory(dir);
                string newId = Guid.NewGuid().ToString();
                File.WriteAllText(path, newId);
                return newId;
            }
            catch { return Guid.NewGuid().ToString(); }
        }
    }
}
