using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InterviewCopilot
{
    /// <summary>
    /// The app's glass look, in one place, so every window has the same one.
    ///
    /// Only the main window was ever translucent. Settings, Sessions, Credits
    /// and the login window each painted a solid near-black of their own, so
    /// opening any of them dropped an opaque slab on top of a translucent app —
    /// which is what made them feel bolted on rather than part of it.
    ///
    /// The model is deliberately not window opacity. Setting Window.Opacity
    /// fades the text and the controls along with the background, which reads
    /// as a faded screenshot rather than as glass, and is unreadable at the
    /// settings people actually choose. Instead the window stays fully opaque
    /// and only the backdrop's alpha moves, so content stays crisp at every
    /// level while the desktop shows through behind it.
    ///
    /// The mapping matches what the settings slider promises: the stored value
    /// runs 0.50-1.00, and the percentage shown to the user is how dark the
    /// backdrop is. A small floor keeps the frame faintly visible at the
    /// extreme, so a window can never become genuinely invisible and unfindable.
    /// </summary>
    internal static class Glass
    {
        /// <summary>The backdrop colour, shared by every window.</summary>
        private const byte R = 0x09, G = 0x0B, B = 0x12;

        /// <summary>
        /// The backdrop brush for a stored opacity preference. Frozen, because
        /// these are created on every settings change and never mutated.
        /// </summary>
        internal static Brush BackdropFor(double storedOpacity)
        {
            double backdrop = Math.Clamp((storedOpacity - 0.50) / 0.50, 0.06, 1.0);
            byte alpha = (byte)Math.Clamp(backdrop * 255.0, 0, 255);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, R, G, B));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// Paints a window's root border with the current glass setting.
        ///
        /// Safe to call from a constructor before the visual tree is finished:
        /// a null root is simply ignored, which is why every caller can invoke
        /// it unconditionally rather than guarding at each site.
        /// </summary>
        internal static void Apply(Window window, Border? root)
        {
            if (root == null) return;
            window.Opacity = 1.0;             // content stays crisp; only the backdrop fades
            root.Background = BackdropFor(SettingsWindow.GetMainWindowOpacity());
        }

        /// <summary>
        /// Repaints every window that is already open, for when the setting
        /// changes while more than one is on screen.
        ///
        /// Found by name rather than by keeping a registry of windows: a
        /// registry has to be maintained at every open and close, and a missed
        /// close leaks the window. The root border is named RootGlass in each
        /// of them precisely so this can find it without one.
        /// </summary>
        internal static void ApplyToOpenWindows(double storedOpacity)
        {
            Brush backdrop = BackdropFor(storedOpacity);

            foreach (Window w in Application.Current?.Windows ?? new WindowCollection())
            {
                try
                {
                    if (w.FindName("RootGlass") is Border root)
                    {
                        w.Opacity = 1.0;
                        root.Background = backdrop;
                    }
                }
                catch
                {
                    // A window mid-teardown can throw on FindName. One window
                    // failing must not stop the rest from being repainted.
                }
            }
        }
    }
}
