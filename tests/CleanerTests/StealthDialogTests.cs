using System.Threading;
using System.Windows;
using System.Windows.Controls;
using InterviewCopilot;

namespace CleanerTests;

/// <summary>
/// Every message the app shows while stealth mode hides it from screen sharing is a StealthDialog. Show and Confirm cast the
/// window's content to the wrong type, so they threw the moment they were used, which no test could see because the dialogs
/// are modal and nothing builds one without showing it. This builds one, and adds buttons the way Show and Confirm do.
/// </summary>
internal static class StealthDialogTests
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
        int children = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var dialog = StealthDialog.Create(null, "Close Replysis?", "An answer is still being written.");
                var panel = StealthDialog.PanelOf(dialog);
                children = panel.Children.Count;
                panel.Children.Add(new Button { Content = "OK" });   // what Show and Confirm do next
                if (panel.Children.Count != children + 1) problem = "a button could not be added";
            }
            catch (Exception ex) { problem = ex.GetBaseException().Message; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(20));

        Check(problem == null, "a stealth dialog can be built and given buttons" + (problem == null ? "" : $" ({problem})"));
        Check(children == 2, "it has a title and a message");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "stealth dialog: all passed" : $"stealth dialog: {failed} FAILED");
        return failed;
    }
}
