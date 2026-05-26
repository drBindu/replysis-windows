using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace InterviewCopilot
{
    public partial class DebugWindow : Window
    {
        private static DebugWindow? _instance;
        private readonly List<string> _logs = new List<string>();
        private readonly DispatcherTimer _refreshTimer;
        private bool _allowClose = false;

        public DebugWindow()
        {
            Title = "DEBUG: Space Key Diagnostics";
            Width = 520;
            Height = 440;
            Background = System.Windows.Media.Brushes.Black;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 20;
            Top = 20;

            ShowInTaskbar = false;
            WindowStyle = WindowStyle.ToolWindow;

            var grid = new System.Windows.Controls.Grid();

            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

            var header = new System.Windows.Controls.TextBlock
            {
                Text = "SPACE KEY DEBUG LOG  (F12 to hide)",
                Foreground = System.Windows.Media.Brushes.Cyan,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(10, 10, 10, 5)
            };
            System.Windows.Controls.Grid.SetRow(header, 0);
            grid.Children.Add(header);

            var scroll = new System.Windows.Controls.ScrollViewer
            {
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Margin = new Thickness(10, 0, 10, 10)
            };
            var logBlock = new System.Windows.Controls.TextBlock
            {
                Foreground = System.Windows.Media.Brushes.LimeGreen,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            scroll.Content = logBlock;
            System.Windows.Controls.Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            var statusBar = new System.Windows.Controls.TextBlock
            {
                Text = "Waiting for events...",
                Foreground = System.Windows.Media.Brushes.Yellow,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 11,
                Margin = new Thickness(10, 5, 10, 10)
            };
            System.Windows.Controls.Grid.SetRow(statusBar, 2);
            grid.Children.Add(statusBar);

            Content = grid;

            _instance = this;

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _refreshTimer.Tick += (s, e) =>
            {
                if (_logs.Count > 0)
                {
                    while (_logs.Count > 200)
                        _logs.RemoveAt(0);

                    logBlock.Text = string.Join("\n", _logs);
                    scroll.ScrollToBottom();
                    statusBar.Text = $"Total events: {_logs.Count} | Last: {DateTime.Now:HH:mm:ss.fff}";
                }
            };
            _refreshTimer.Start();

            Log("DEBUG", "Debug window started");
            Log("DEBUG", "Waiting for Space key events...");
            Log("DEBUG", "Press F12 to hide/show this window");
            Log("DEBUG", "-----------------------------------");
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                this.Hide();
                return;
            }
            base.OnClosing(e);
        }

        public void ForceClose()
        {
            _allowClose = true;
            _refreshTimer.Stop();
            _instance = null;
            try { this.Close(); } catch { }
        }

        public static void Log(string source, string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{source}] {message}";

            // Capture local reference to avoid race condition
            var inst = _instance;
            if (inst != null)
            {
                try
                {
                    inst.Dispatcher.BeginInvoke(() =>
                    {
                        // Double-check inside the invoke — instance may have been
                        // nulled between the outer check and when this runs
                        try
                        {
                            inst._logs?.Add(line);
                        }
                        catch { }
                    });
                }
                catch { }
            }

            System.Diagnostics.Debug.WriteLine(line);
        }

        protected override void OnClosed(EventArgs e)
        {
            _refreshTimer.Stop();
            _instance = null;
            base.OnClosed(e);
        }
    }
}