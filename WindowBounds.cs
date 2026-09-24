using System;

namespace InterviewCopilot;

internal static class WindowBounds
{
    // A low-resolution display or large text scaling can make the window
    // taller than the work area. Math.Clamp otherwise throws when max < min.
    internal static double ClampPosition(double position, double start, double end, double size) =>
        Math.Clamp(position, start, Math.Max(start, end - size));
}
