using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace InterviewCopilot
{
    internal static class SecureDataProtector
    {
        private const string Prefix = "dpapi:";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("InterviewCopilot.SecureStorage.v1");

        public static bool IsProtected(string value) =>
            value.StartsWith(Prefix, StringComparison.Ordinal);

        public static string Protect(string value)
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(value);
            byte[]? protectedBytes = null;
            try
            {
                protectedBytes = ProtectedData.Protect(
                    plainBytes, Entropy, DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
                if (protectedBytes != null)
                    CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }

        public static bool TryUnprotect(string value, out string plainText)
        {
            plainText = "";
            if (!IsProtected(value)) return false;

            byte[]? protectedBytes = null;
            byte[]? plainBytes = null;
            try
            {
                protectedBytes = Convert.FromBase64String(value[Prefix.Length..]);
                plainBytes = ProtectedData.Unprotect(
                    protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                plainText = Encoding.UTF8.GetString(plainBytes);
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
            finally
            {
                if (protectedBytes != null)
                    CryptographicOperations.ZeroMemory(protectedBytes);
                if (plainBytes != null)
                    CryptographicOperations.ZeroMemory(plainBytes);
            }
        }

        public static string ReadProtectedFile(string path)
        {
            string raw = File.ReadAllText(path);
            if (IsProtected(raw))
            {
                if (!TryUnprotect(raw, out string value))
                    throw new InvalidDataException("Could not decrypt protected data.");
                return value;
            }

            // Legacy plaintext file: opportunistically upgrade it to encrypted, but never
            // let a failed upgrade-write break the read itself. Returning the data the
            // caller asked for matters more than the best-effort re-encryption.
            try { WriteProtectedFile(path, raw); }
            catch (Exception ex) { DebugWindow.Log("SECURE", $"Legacy file upgrade skipped: {ex.Message}"); }
            return raw;
        }

        public static void WriteProtectedFile(string path, string value)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            // Unique temp name so two writes to the same path can never clobber each
            // other's half-written temp file before the atomic move completes.
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, Protect(value));
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        public static bool TryProtectBinaryFile(string sourcePath, string destinationPath)
        {
            byte[]? plainBytes = null;
            byte[]? protectedBytes = null;
            string tempPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                plainBytes = File.ReadAllBytes(sourcePath);
                protectedBytes = ProtectedData.Protect(
                    plainBytes, Entropy, DataProtectionScope.CurrentUser);

                string? directory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllBytes(tempPath, protectedBytes);
                File.Move(tempPath, destinationPath, overwrite: true);
                File.Delete(sourcePath);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                if (plainBytes != null)
                    CryptographicOperations.ZeroMemory(plainBytes);
                if (protectedBytes != null)
                    CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }
}
