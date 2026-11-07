using System;
using System.IO;
using System.Text.RegularExpressions;

namespace CleanerTests;

/// <summary>
/// The sign-in window's fields and icon buttons must have names a screen reader can say (round 32, 2026-11-07). The email
/// and password boxes show their labels as separate text, and the close and show password buttons are icons only, so
/// Narrator announced each of them as a blank edit box or a blank button.
/// </summary>
internal static class SignInAccessibilityTests
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}");
            if (!ok) failed++;
        }

        string xaml = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "LoginWindow.xaml")));

        bool Named(string tagStart, string name) =>
            Regex.IsMatch(xaml, Regex.Escape(tagStart) + "[^>]*AutomationProperties\\.Name=\"" + Regex.Escape(name) + "\"");

        Check(Named("x:Name=\"EmailBox\"", "Email"), "the email box is named Email");
        Check(Named("x:Name=\"PasswordBox\"", "Password"), "the password box is named Password");
        Check(Named("x:Name=\"PasswordPlainBox\"", "Password"), "the shown password box is named Password");
        Check(Named("x:Name=\"RevealPasswordBtn\"", "Show password"), "the show password button is named");
        Check(Named("Click=\"CloseBtn_Click\"", "Close"), "the close button is named Close");

        string code = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "LoginWindow.xaml.cs")));
        Check(code.Contains("AutomationProperties.SetName(RevealPasswordBtn, \"Hide password\")")
              && code.Contains("AutomationProperties.SetName(RevealPasswordBtn, \"Show password\")"),
            "the show password button's spoken name follows its tooltip");

        return failed;
    }
}
