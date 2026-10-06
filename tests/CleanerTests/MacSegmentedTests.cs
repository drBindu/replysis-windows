using System.Threading;
using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// The Setup choices are macOS style sliding switches. The switch only ASKS (an event); the window runs the same code it
/// always ran for the choice and then tells the switch where to sit. These pin that contract, so a click can never move the
/// thumb to a place the app is not actually in.
/// </summary>
internal static class MacSegmentedTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        string? problem = null;
        var requests = new List<int>();
        int selectedAfterRequest = -1;

        var thread = new Thread(() =>
        {
            try
            {
                var toggle = new MacSegmented { LeftText = "Auto", RightText = "Manual" };
                toggle.SelectionRequested += (_, index) => requests.Add(index);

                toggle.SetSelected(1);
                if (toggle.SelectedIndex != 1) problem = "SetSelected(1) did not select the right side";

                toggle.Request(1);                       // already there
                if (requests.Count != 0) problem ??= "asking for the side it is already on raised a request";

                toggle.Request(0);                       // the person clicks the other side
                selectedAfterRequest = toggle.SelectedIndex;
                if (requests.Count != 1 || requests[0] != 0) problem ??= "clicking the other side did not raise exactly one request for it";

                toggle.SetSelected(0);                   // the window did what was asked and says where to sit
                if (toggle.SelectedIndex != 0) problem ??= "SetSelected(0) did not move it back";
                toggle.SetSelected(5);                   // nonsense falls back to the first choice, the default
                if (toggle.SelectedIndex != 0) problem ??= "an out of range index did not fall back to the first choice";
            }
            catch (Exception ex) { problem = ex.GetBaseException().Message; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(20));

        Check(problem == null, "the switch selects, requests and ignores a repeat" + (problem == null ? "" : $" ({problem})"));
        Check(selectedAfterRequest == 1, "a request does not move the thumb by itself; only the window does, after it has acted");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "mac segmented: all passed" : $"mac segmented: {failed} FAILED");
        return failed;
    }
}
