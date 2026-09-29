using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace InterviewCopilot
{
    /// <summary>
    /// Slows mouse wheel and touchpad scrolling in every scrollable area of the
    /// app: the answer, the transcript, Setup, Settings, Past Sessions.
    ///
    /// Windows scrolls three lines a notch, about 48 pixels, which is fine in a
    /// document and far too much in a small answer box: one notch skips half of
    /// what someone was reading, and they are reading it aloud (owner,
    /// 2026-09-29, asked twice). Registered once for every ScrollViewer, so a
    /// new window or box is covered without anyone remembering to opt in.
    ///
    /// The wheel event bubbles from the control under the pointer outwards, so
    /// the innermost scrollable box handles it first. A box that cannot move any
    /// further in that direction passes it on, so the page behind it still
    /// scrolls when an answer box has reached its end.
    /// </summary>
    internal static class WheelScroll
    {
        /// <summary>Fraction of the default distance. 0.18 is about 22 pixels a notch, under half of the default.</summary>
        internal const double Factor = 0.18;

        private static bool _installed;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            EventManager.RegisterClassHandler(
                typeof(ScrollViewer), UIElement.MouseWheelEvent, new MouseWheelEventHandler(OnMouseWheel));
        }

        /// <summary>Where a scroll lands. Pure, so it can be tested.</summary>
        internal static double Target(double offset, double scrollableHeight, int delta)
            => Math.Clamp(offset - delta * Factor, 0, Math.Max(0, scrollableHeight));

        /// <summary>Whether the box can move in the direction of the wheel at all.</summary>
        internal static bool CanMove(double offset, double scrollableHeight, int delta)
            => scrollableHeight > 0 && (delta > 0 ? offset > 0 : offset < scrollableHeight);

        private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || sender is not ScrollViewer sv) return;

            // A list that scrolls by item (virtualized) measures its offset in
            // items, not pixels, and has its own sensible step. Leave it alone.
            if (sv.CanContentScroll) return;

            // Nothing to scroll here, or already at the end in this direction:
            // let the box behind it have the wheel.
            if (!CanMove(sv.VerticalOffset, sv.ScrollableHeight, e.Delta)) return;

            sv.ScrollToVerticalOffset(Target(sv.VerticalOffset, sv.ScrollableHeight, e.Delta));
            e.Handled = true;
        }
    }
}
