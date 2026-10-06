using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace InterviewCopilot
{
    /// <summary>
    /// A two way switch that looks and moves like a macOS segmented control.
    ///
    /// It never decides anything. A click or an arrow key RAISES <see cref="SelectionRequested"/>, the window runs the same
    /// code it always ran for that choice, and that code calls <see cref="SetSelected"/>. So there is still exactly one place
    /// that knows what is selected, and a request the window declines or defers simply leaves the thumb where it was.
    /// </summary>
    public partial class MacSegmented : UserControl
    {
        private static readonly Brush OnText  = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
        private static readonly Brush OffText = Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0xA7, 0xB7)));
        private static readonly Brush HoverText = Freeze(new SolidColorBrush(Color.FromRgb(0xC9, 0xD1, 0xDC)));
        private static readonly Brush FocusRing = Freeze(new SolidColorBrush(Color.FromRgb(0x5E, 0x8F, 0xC7)));
        private static readonly Brush TrackEdge = Freeze(new SolidColorBrush(Color.FromRgb(0x2B, 0x32, 0x3D)));

        private int _selected;
        private bool _placed;

        public MacSegmented()
        {
            InitializeComponent();
            GotKeyboardFocus += (_, _) => Track.BorderBrush = FocusRing;
            LostKeyboardFocus += (_, _) => Track.BorderBrush = TrackEdge;
            Paint();
        }

        /// <summary>The person asked for this segment (0 left, 1 right). The window decides what that means.</summary>
        public event EventHandler<int>? SelectionRequested;

        public string LeftText
        {
            get => LeftLabel.Text;
            set { LeftLabel.Text = value; AutomationProperties_Update(); }
        }

        public string RightText
        {
            get => RightLabel.Text;
            set { RightLabel.Text = value; AutomationProperties_Update(); }
        }

        public int SelectedIndex => _selected;

        /// <summary>Moves the thumb. Animated once the control is on screen; the first placement is instant.</summary>
        public void SetSelected(int index)
        {
            index = index == 1 ? 1 : 0;
            if (index == _selected && _placed) { Paint(); return; }
            _selected = index;
            Paint();
            MoveThumb(animate: _placed && IsLoaded);
            _placed = true;
        }

        /// <summary>What a click or an arrow key does: ask, unless it is already selected.</summary>
        public void Request(int index)
        {
            index = index == 1 ? 1 : 0;
            if (index == _selected) return;
            SelectionRequested?.Invoke(this, index);
        }

        private void AutomationProperties_Update() =>
            System.Windows.Automation.AutomationProperties.SetHelpText(this, $"{LeftLabel.Text} or {RightLabel.Text}");

        private void Left_Down(object sender, MouseButtonEventArgs e)  { Focus(); Request(0); e.Handled = true; }
        private void Right_Down(object sender, MouseButtonEventArgs e) { Focus(); Request(1); e.Handled = true; }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Left)  { Request(0); e.Handled = true; }
            if (e.Key == Key.Right) { Request(1); e.Handled = true; }
        }

        private void Hit_Enter(object sender, MouseEventArgs e)
        {
            var label = ReferenceEquals(sender, LeftHit) ? LeftLabel : RightLabel;
            bool isSelected = ReferenceEquals(sender, LeftHit) ? _selected == 0 : _selected == 1;
            if (!isSelected) label.Foreground = HoverText;
        }

        private void Hit_Leave(object sender, MouseEventArgs e) => Paint();

        private void Host_SizeChanged(object sender, SizeChangedEventArgs e) => MoveThumb(animate: false);

        private void Paint()
        {
            LeftLabel.Foreground  = _selected == 0 ? OnText : OffText;
            RightLabel.Foreground = _selected == 1 ? OnText : OffText;
        }

        private void MoveThumb(bool animate)
        {
            double target = _selected == 1 ? Host.ActualWidth / 2 : 0;
            if (!animate || SystemParameters.ClientAreaAnimation == false)
            {
                ThumbMove.BeginAnimation(TranslateTransform.XProperty, null);
                ThumbMove.X = target;
                return;
            }
            ThumbMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    }
}
