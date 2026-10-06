using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InterviewCopilot
{
    /// <summary>A capture-excluded WPF replacement for native message boxes.</summary>
    internal static class StealthDialog
    {
        internal static bool Confirm(Window? owner, string title, string message,
                                     string confirm = "Continue", string cancel = "Cancel")
        {
            var dialog = Create(owner, title, message);
            bool accepted = false;
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };
            buttons.Children.Add(MakeButton(cancel, false, (_, _) => dialog.Close()));
            buttons.Children.Add(MakeButton(confirm, true, (_, _) => { accepted = true; dialog.Close(); }));
            PanelOf(dialog).Children.Add(buttons);
            WindowStealth.SetStealthMode(dialog, SettingsWindow.GetStealthMode());
            dialog.ShowDialog();
            return accepted;
        }

        internal static void Show(Window? owner, string title, string message)
        {
            var dialog = Create(owner, title, message);
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };
            buttons.Children.Add(MakeButton("OK", true, (_, _) => dialog.Close()));
            PanelOf(dialog).Children.Add(buttons);
            WindowStealth.SetStealthMode(dialog, SettingsWindow.GetStealthMode());
            dialog.ShowDialog();
        }

        /// <summary>
        /// The dialog's content is a Border (for the hairline edge) holding the StackPanel. Both Show and Confirm cast the
        /// content straight to a StackPanel, which threw InvalidCastException every time any of these dialogs was shown,
        /// including "Replysis is still working, close anyway?" (found 2026-10-05 by closing the app after a test run).
        /// </summary>
        internal static StackPanel PanelOf(Window dialog) => (StackPanel)((Border)dialog.Content).Child;

        internal static Window Create(Window? owner, string title, string message)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = new FontFamily("Segoe UI Variable Display"),
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(241, 245, 249))
            });
            panel.Children.Add(new TextBlock
            {
                Text = message,
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                FontSize = 13,
                LineHeight = 20,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184))
            });

            return new Window
            {
                Owner = owner?.IsVisible == true ? owner : null,
                Title = title,
                Width = 440,
                SizeToContent = SizeToContent.Height,
                MaxHeight = 520,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = false,
                ShowInTaskbar = false,
                Topmost = owner?.Topmost == true,
                Background = new SolidColorBrush(Color.FromRgb(13, 17, 23)),
                WindowStartupLocation = owner?.IsVisible == true
                    ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Content = new Border
                {
                    Padding = new Thickness(24),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                    Child = panel
                }
            };
        }

        private static Button MakeButton(string text, bool primary, RoutedEventHandler click)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 96,
                Height = 36,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(16, 0, 16, 0),
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                Background = new SolidColorBrush(primary
                    ? Color.FromRgb(30, 58, 95) : Color.FromRgb(24, 29, 38)),
                BorderBrush = new SolidColorBrush(primary
                    ? Color.FromRgb(51, 93, 143) : Color.FromRgb(55, 65, 81)),
                BorderThickness = new Thickness(1)
            };
            button.Click += click;
            return button;
        }
    }
}
