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

            // The numbers above are device-independent units at the main screen's scaling. A window that starts on a
            // second monitor with different scaling (150% beside 100%) is laid out at THAT monitor's scaling, so it
            // came out the wrong size and did not cover the whole desktop: part of the screen could not be picked.
            // Once the window exists it is placed in real pixels, which have no scaling to get wrong.
            SourceInitialized += (_, _) => CoverVirtualScreenInPixels();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private void CoverVirtualScreenInPixels()
        {
            try
            {
                const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
                const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                SetWindowPos(hwnd, IntPtr.Zero,
                    GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
                    GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN),
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch (Exception ex) { DebugWindow.Log("SCREEN", $"Could not size the area picker in pixels: {ex.GetType().Name}"); }
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
