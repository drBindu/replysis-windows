using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace InterviewCopilot
{
    public partial class DebugWindow : Window
    {
        // Mirrors every log line to disk so the full session's log survives past the
        // UI's 200-line window (and can be inspected without screenshotting the app).
        private static readonly string LogFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InterviewCopilot", "debug.log");
        private static readonly object _fileLock = new object();
        private static volatile DebugWindow? _instance;
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

            var headerRow = new System.Windows.Controls.Grid { Margin = new Thickness(10, 10, 10, 5) };
            headerRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });

            var header = new System.Windows.Controls.TextBlock
            {
                Text = "SPACE KEY DEBUG LOG  (F12 to hide)",
                Foreground = System.Windows.Media.Brushes.Cyan,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            System.Windows.Controls.Grid.SetColumn(header, 0);
            headerRow.Children.Add(header);

            var copyButton = new System.Windows.Controls.Button
            {
                Content = "Copy full log",
                Padding = new Thickness(10, 3, 10, 3),
                Background = System.Windows.Media.Brushes.DarkSlateGray,
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0)
            };
            copyButton.Click += (s, e) =>
            {
                try
                {
                    string fullLog;
                    lock (_fileLock) { fullLog = File.Exists(LogFilePath) ? File.ReadAllText(LogFilePath) : string.Join("\n", _logs); }
                    Clipboard.SetText(fullLog);
                    copyButton.Content = "Copied!";
                    var revertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                    revertTimer.Tick += (s2, e2) => { copyButton.Content = "Copy full log"; revertTimer.Stop(); };
                    revertTimer.Start();
                }
                catch (Exception ex) { copyButton.Content = "Copy failed"; System.Diagnostics.Debug.WriteLine(ex); }
            };
            System.Windows.Controls.Grid.SetColumn(copyButton, 1);
            headerRow.Children.Add(copyButton);

            System.Windows.Controls.Grid.SetRow(headerRow, 0);
            grid.Children.Add(headerRow);

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

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
                File.WriteAllText(LogFilePath, "");   // fresh file each app launch
            }
            catch { }

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

            try
            {
                lock (_fileLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
                    File.AppendAllText(LogFilePath, line + Environment.NewLine);
                }
            }
            catch { }

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