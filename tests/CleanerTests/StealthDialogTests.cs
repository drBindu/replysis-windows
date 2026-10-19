using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Reflection;
using System.IO;
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
                var dialog = StealthDialog.Create(null, "Close Replysis?",
                    "Your session is still active. Closing stops listening and any answer in progress. Completed answers stay saved on this computer.");
                var panel = StealthDialog.PanelOf(dialog);
                children = panel.Children.Count;
                var buttons = new StackPanel { Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
                buttons.Children.Add(StealthDialog.MakeButton("Keep running", false, (_, _) => { }));
                var close = StealthDialog.MakeButton("Close app", true, (_, _) => { }, true);
                buttons.Children.Add(close);
                panel.Children.Add(buttons);
                if (panel.Children.Count != children + 1) problem = "a button could not be added";
                var content = (Border)dialog.Content;
                content.Measure(new Size(440, double.PositiveInfinity));
                content.Arrange(new Rect(new Point(), content.DesiredSize));
                content.UpdateLayout();
                close.ApplyTemplate();
                var surface = (Border)close.Template.FindName("Surface", close);
                var hover = (Border)close.Template.FindName("Hover", close);
                var initialBackground = surface.Background;
                var mouseOver = (DependencyPropertyKey?)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                var pressed = (DependencyPropertyKey?)typeof(ButtonBase).GetField("IsPressedPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                if (mouseOver == null || pressed == null) throw new Exception("interaction properties unavailable");
                close.SetValue(mouseOver, true);
                if (hover.Opacity != 0.08 || surface.Background != initialBackground)
                    throw new Exception("hover replaced the readable button surface");
                close.SetValue(pressed, true);
                if (hover.Opacity != 0.14 || surface.Background != initialBackground)
                    throw new Exception("press replaced the readable button surface");
                close.SetValue(pressed, false);
                close.SetValue(mouseOver, false);
                string? preview = Environment.GetEnvironmentVariable("REPLYSIS_DIALOG_PREVIEW");
                if (!string.IsNullOrWhiteSpace(preview))
                {
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(preview);
                    encoder.Save(file);
                }
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
