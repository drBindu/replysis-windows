using System;
using System.IO;
using System.IO.Compression;

namespace InterviewCopilot;

internal static class ResumeFileSafety
{
    internal const int MaxBytes = 10 * 1024 * 1024;
    internal const long MaxExpandedBytes = 30 * 1024 * 1024;
    internal static byte[] ReadFile(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ReadBounded(input, MaxBytes);
    }

    internal static byte[] ReadBounded(Stream input, int maximum)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("Choose a resume smaller than 10 MB.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    internal static void ValidateDocx(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > 2000) throw new InvalidDataException("The document contains too many parts.");
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > MaxExpandedBytes - expanded)
                throw new InvalidDataException("The expanded document is too large. Please use a smaller resume.");
            expanded += entry.Length;
        }
    }
}
