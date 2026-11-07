using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// The pipes that carry the microphone and the computer's sound to the speech engine (bug round 18). Windows' default
/// pipe security lets Everyone and Anonymous open a pipe for reading, so another account on a shared PC could listen.
/// The pipe must allow this Windows user only, and this user must still be able to read it, as the engine does.
/// </summary>
internal static class EnginePipeSecurityTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        string name = $"replysis-test-{Environment.ProcessId}-{Guid.NewGuid():N}";
        using var server = NativeAudioFeed.CreateEnginePipe(name);

        var rules = server.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                          .Cast<PipeAccessRule>().Where(r => r.AccessControlType == AccessControlType.Allow).ToList();
        var me = WindowsIdentity.GetCurrent().User!;
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var anonymous = new SecurityIdentifier(WellKnownSidType.AnonymousSid, null);

        Check(rules.Count > 0, "engine pipe has an access list");
        Check(rules.All(r => r.IdentityReference.Equals(me)), "engine pipe allows only this Windows user");
        Check(!rules.Any(r => r.IdentityReference.Equals(everyone) || r.IdentityReference.Equals(anonymous)),
              "engine pipe gives Everyone and Anonymous nothing");

        // The engine opens it as this same user and reads; that must keep working.
        var waiting = server.WaitForConnectionAsync();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.In);
        client.Connect(2000);
        waiting.Wait(2000);
        server.Write(new byte[] { 1, 2, 3, 4 });
        server.Flush();
        var buffer = new byte[4];
        int read = client.Read(buffer, 0, 4);
        Check(read == 4 && buffer[3] == 4, "this Windows user still reads the engine pipe");

        return failed;
    }
}
