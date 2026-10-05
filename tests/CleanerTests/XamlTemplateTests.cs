using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;

namespace CleanerTests;

/// <summary>
/// A trigger inside a ControlTemplate can only animate or set elements the template itself defines. Naming one defined
/// outside it compiles, looks fine, and throws "'X' name cannot be found in the name scope of ControlTemplate" the first
/// time the mouse touches the control. That is exactly what the Sign In and Google buttons did on every computer since
/// 1.0.25, shown to people as "Replysis recovered from an unexpected problem" (2026-10-05). Nothing in the app's other
/// tests could see it, because no test touches a button with a mouse. This reads the XAML instead.
/// </summary>
internal static class XamlTemplateTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "InterviewCopilot.csproj")))
            root = Path.GetDirectoryName(root);
        if (root == null) { Check(false, "could not find the app's source folder"); return failed; }

        int templates = 0, bad = 0;
        foreach (string file in Directory.GetFiles(root, "*.xaml"))
        {
            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"<ControlTemplate\b[^>]*>(.*?)</ControlTemplate>", RegexOptions.Singleline))
            {
                templates++;
                string body = m.Groups[1].Value;
                var defined = new HashSet<string>(Regex.Matches(body, "x:Name=\"([^\"]+)\"").Select(x => x.Groups[1].Value));
                foreach (Match t in Regex.Matches(body, "(?:Storyboard\\.TargetName|TargetName)=\"([^\"]+)\""))
                {
                    string name = t.Groups[1].Value;
                    if (defined.Contains(name)) continue;
                    bad++;
                    int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                    Check(false, $"{Path.GetFileName(file)} line {line}: a template targets '{name}', which it does not define");
                }
            }
        }
        Check(templates > 5, $"looked at {templates} control templates");
        Check(bad == 0, "every template only targets names it defines");

        // And the real thing: put each sign-in button on a window and drive its triggers the way the mouse does.
        string loginXaml = File.ReadAllText(Path.Combine(root, "LoginWindow.xaml"));
        foreach (string name in new[] { "SignInBtn", "GoogleSignInBtn" })
        {
            string? error = DriveTriggers(loginXaml, name);
            Check(error == null, $"{name}: hovering and pressing it works" + (error == null ? "" : $" ({error})"));
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "xaml templates: all passed" : $"xaml templates: {failed} FAILED");
        return failed;
    }

    /// <summary>Returns null when the button's template survives IsMouseOver and IsPressed, otherwise what went wrong.</summary>
    private static string? DriveTriggers(string loginXaml, string buttonName)
    {
        string? result = "did not run";
        var thread = new Thread(() =>
        {
            try
            {
                var m = Regex.Match(loginXaml, "<Button x:Name=\"" + buttonName + "\".*?</Button>", RegexOptions.Singleline);
                if (!m.Success) { result = "button not found in LoginWindow.xaml"; return; }
                string button = m.Value;
                button = Regex.Replace(button, @"\s+Click=""[^""]*""", "");
                button = Regex.Replace(button, @"\s+FocusVisualStyle=""[^""]*""", "");
                string xaml =
                    "<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                    "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" WindowStyle=\"None\" ShowInTaskbar=\"False\" " +
                    "ShowActivated=\"False\" Left=\"-4000\" Top=\"-4000\" Width=\"300\" Height=\"90\"><Grid>" + button + "</Grid></Window>";
                var window = (Window)XamlReader.Parse(xaml);
                window.Show();
                var btn = (Button)window.FindName(buttonName);
                btn.ApplyTemplate();
                window.UpdateLayout();

                var mouseOver = typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as DependencyPropertyKey;
                var pressed = typeof(ButtonBase).GetField("IsPressedPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as DependencyPropertyKey;
                if (mouseOver == null || pressed == null) { result = "could not reach the mouse-over and pressed properties"; window.Close(); return; }

                btn.SetValue(mouseOver, true);    // the mouse enters
                btn.SetValue(pressed, true);      // is pressed
                btn.SetValue(pressed, false);     // released
                btn.SetValue(mouseOver, false);   // and leaves
                window.Close();
                result = null;
            }
            catch (Exception ex)
            {
                result = ex.GetBaseException().Message;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) return "timed out";
        return result;
    }
}
