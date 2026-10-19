using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Input;

namespace InterviewCopilot
{
    /// <summary>A capture-excluded WPF replacement for native message boxes.</summary>
    internal static class StealthDialog
    {
        internal static bool Confirm(Window? owner, string title, string message,
                                     string confirm = "Continue", string cancel = "Cancel", bool destructive = false)
        {
            var dialog = Create(owner, title, message);
            bool accepted = false;
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };
            var cancelButton = MakeButton(cancel, false, (_, _) => dialog.Close());
            cancelButton.IsCancel = true;
            cancelButton.IsDefault = true;
            buttons.Children.Add(cancelButton);
            buttons.Children.Add(MakeButton(confirm, true, (_, _) => { accepted = true; dialog.Close(); }, destructive));
            dialog.Loaded += (_, _) => cancelButton.Focus();
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
            var okButton = MakeButton("OK", true, (_, _) => dialog.Close());
            okButton.IsDefault = true;
            okButton.IsCancel = true;
            buttons.Children.Add(okButton);
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
            // The real logo and the name in plain case, instead of "REPLYSIS" in green capitals (owner, 2026-10-19).
            var heading = new StackPanel();
            var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
            try
            {
                brand.Children.Add(new System.Windows.Controls.Image
                {
                    Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://siteoforigin:,,,/Assets/replysis-icon-1024.png")),
                    Width = 22, Height = 22, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center
                });
            }
            catch { /* the name alone is fine */ }
            brand.Children.Add(new TextBlock
            {
                Text = "Replysis",
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                VerticalAlignment = VerticalAlignment.Center
            });
            heading.Children.Add(brand);
            heading.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = new FontFamily("Segoe UI Variable Display"),
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(heading);
            panel.Children.Add(new TextBlock
            {
                Text = message,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                FontSize = 13.5,
                LineHeight = 21,
                Foreground = new SolidColorBrush(Color.FromRgb(170, 182, 200))
            });

            return new Window
            {
                Owner = owner?.IsVisible == true ? owner : null,
                Title = title,
                Width = 468,
                SizeToContent = SizeToContent.Height,
                MaxHeight = 520,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                ShowInTaskbar = false,
                Topmost = owner?.Topmost == true,
                Background = Brushes.Transparent,
                WindowStartupLocation = owner?.IsVisible == true
                    ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Content = new Border
                {
                    Margin = new Thickness(14),   // room for the shadow
                    Padding = new Thickness(26, 24, 26, 22),
                    CornerRadius = new CornerRadius(18),
                    Background = new LinearGradientBrush(Color.FromRgb(17, 26, 40), Color.FromRgb(11, 17, 27), 90),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x94, 0xA3, 0xB8)),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Opacity = 0.55, Color = Colors.Black },
                    Child = panel
                }
            };
        }

        internal static Button MakeButton(string text, bool primary, RoutedEventHandler click, bool destructive = false)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 112,
                Height = 42,
                Margin = new Thickness(10, 0, 0, 0),
                Padding = new Thickness(18, 0, 18, 0),
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(primary && destructive ? Color.FromRgb(254, 202, 202) : Color.FromRgb(241, 245, 249)),
                Background = new SolidColorBrush(primary
                    ? (destructive ? Color.FromRgb(69, 26, 34) : Color.FromRgb(22, 101, 66))
                    : Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(primary
                    ? (destructive ? Color.FromRgb(127, 45, 58) : Color.FromRgb(34, 139, 90))
                    : Color.FromRgb(51, 65, 85)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            // Own every interaction state: the OS theme otherwise paints a
            // pale blue hover background under our white text.
            button.Template = (ControlTemplate)XamlReader.Parse("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
                    <Border x:Name="Surface" CornerRadius="10" Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}">
                        <Grid>
                            <Border x:Name="Hover" Background="#FFFFFF" Opacity="0" CornerRadius="10"/>
                            <ContentPresenter Margin="{TemplateBinding Padding}" RecognizesAccessKey="True"
                                              HorizontalAlignment="Center" VerticalAlignment="Center"
                                              TextElement.Foreground="{TemplateBinding Foreground}"/>
                        </Grid>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Hover" Property="Opacity" Value="0.08"/></Trigger>
                        <Trigger Property="IsPressed" Value="True"><Setter TargetName="Hover" Property="Opacity" Value="0.14"/></Trigger>
                        <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Surface" Property="BorderBrush" Value="#84E7B6"/></Trigger>
                        <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Surface" Property="Opacity" Value="0.45"/></Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
                """);
            button.Click += click;
            return button;
        }
    }
}
