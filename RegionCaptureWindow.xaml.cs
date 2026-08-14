using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace InterviewCopilot
{
    public partial class RegionCaptureWindow : Window
    {
        private Point _start;
        private bool _selecting;

        public Int32Rect SelectedRegion { get; private set; }

        public RegionCaptureWindow()
        {
            InitializeComponent();

            // Every other window in the app excludes itself from capture; this one
            // did not. It covers the entire desktop with a dark wash, an
            // instruction card and a bright selection rectangle, so choosing
            // "pick just one part" during a shared interview drew all of that on
            // the interviewer's screen and gave the tool away outright.
            try { WindowStealth.SetStealthMode(this, SettingsWindow.GetStealthMode()); } catch { }

            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _start = e.GetPosition(this);
            _selecting = true;
            CaptureMouse();
            SelectionBorder.Visibility = Visibility.Visible;
            UpdateSelection(_start);
            e.Handled = true;
        }

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (_selecting) UpdateSelection(e.GetPosition(this));
        }

        private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_selecting) return;

            Point end = e.GetPosition(this);
            Point startScreen = PointToScreen(_start);
            Point endScreen = PointToScreen(end);
            int x = (int)Math.Round(Math.Min(startScreen.X, endScreen.X));
            int y = (int)Math.Round(Math.Min(startScreen.Y, endScreen.Y));
            int width = (int)Math.Round(Math.Abs(endScreen.X - startScreen.X));
            int height = (int)Math.Round(Math.Abs(endScreen.Y - startScreen.Y));

            _selecting = false;
            ReleaseMouseCapture();

            if (width < 24 || height < 24)
            {
                SelectionBorder.Visibility = Visibility.Collapsed;
                return;
            }

            SelectedRegion = new Int32Rect(x, y, width, height);
            DialogResult = true;
            Close();
        }

        private void UpdateSelection(Point current)
        {
            double left = Math.Min(_start.X, current.X);
            double top = Math.Min(_start.Y, current.Y);
            Canvas.SetLeft(SelectionBorder, left);
            Canvas.SetTop(SelectionBorder, top);
            SelectionBorder.Width = Math.Abs(current.X - _start.X);
            SelectionBorder.Height = Math.Abs(current.Y - _start.Y);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            DialogResult = false;
            Close();
        }
    }
}
