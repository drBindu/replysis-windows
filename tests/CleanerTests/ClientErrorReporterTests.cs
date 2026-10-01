using InterviewCopilot;

namespace CleanerTests;

/// <summary>What is sent about a fault on somebody's computer: method names only, once each, a few a run.</summary>
internal static class ClientErrorReporterTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        string trace =
            @"   at InterviewCopilot.LoginWindow.Window_Loaded(Object sender, RoutedEventArgs e) in C:\Users\Swathi\src\LoginWindow.xaml.cs:line 88" + "\n" +
            "   at System.Windows.RoutedEventHandlerInfo.InvokeHandler(Object target, RoutedEventArgs routedEventArgs)\n" +
            "   at System.Windows.EventRoute.InvokeHandlersImpl(Object source, RoutedEventArgs args, Boolean reRaised)";
        string frames = ClientErrorReporter.Frames(trace);
        Check(frames.StartsWith("InterviewCopilot.LoginWindow.Window_Loaded <"), "frames are method names, innermost first");
        Check(!frames.Contains("Swathi") && !frames.Contains("C:") && !frames.Contains("line 88"),
            "no user name, path or line text leaves the computer");
        Check(ClientErrorReporter.Frames(trace, 2).Split(" < ").Length == 2, "and it is bounded");
        Check(ClientErrorReporter.Frames(null) == "" && ClientErrorReporter.Frames("") == "", "no stack, no frames");

        Check(ClientErrorReporter.ShouldSend("a|x") && !ClientErrorReporter.ShouldSend("a|x"), "the same fault is sent once a run");
        int sent = 0;
        for (int i = 0; i < 20; i++) if (ClientErrorReporter.ShouldSend("fault-" + i)) sent++;
        Check(sent + 1 <= ClientErrorReporter.MaxPerRun, "never more than a few a run");

        // A sign-in refresh that fails for want of a connection is not a sign-out.
        Check(UserSession.ClassifyRefreshStatus(200) == UserSession.RefreshOutcome.Refreshed, "a good reply refreshes");
        Check(UserSession.ClassifyRefreshStatus(400) == UserSession.RefreshOutcome.Refused &&
              UserSession.ClassifyRefreshStatus(401) == UserSession.RefreshOutcome.Refused &&
              UserSession.ClassifyRefreshStatus(403) == UserSession.RefreshOutcome.Refused,
            "Google refusing the saved sign-in (revoked, disabled) is the only thing that asks for a new one");
        Check(UserSession.ClassifyRefreshStatus(500) == UserSession.RefreshOutcome.NoConnection &&
              UserSession.ClassifyRefreshStatus(503) == UserSession.RefreshOutcome.NoConnection &&
              UserSession.ClassifyRefreshStatus(429) == UserSession.RefreshOutcome.NoConnection &&
              UserSession.ClassifyRefreshStatus(408) == UserSession.RefreshOutcome.NoConnection,
            "an outage, a rate limit or a timeout never signs anybody out");
        Check(!UserSession.ContinueOfflineWithSavedSession(), "with nothing saved there is nothing to continue with");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "client error reporter: all passed" : $"client error reporter: {failed} FAILED");
        return failed;
    }
}
