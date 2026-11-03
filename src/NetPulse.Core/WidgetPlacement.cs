using System.Drawing;

namespace NetPulse.Core;

/// <summary>
/// Where the widget sits, stored as the distance from the nearest corner of a screen's work area in
/// 96-dpi units. Unlike absolute pixels this keeps the widget in the same visual spot after a
/// resolution or scaling change, and lets it return to the right monitor.
/// </summary>
public sealed class WidgetPlacement
{
    /// <summary>Device name of the screen (e.g. \\.\DISPLAY2); null or unknown falls back to the primary screen.</summary>
    public string? Screen { get; set; }

    /// <summary>Anchored to the right edge instead of the left.</summary>
    public bool Right { get; set; }

    /// <summary>Anchored to the bottom edge instead of the top.</summary>
    public bool Bottom { get; set; }

    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public bool IsValid => double.IsFinite(OffsetX) && double.IsFinite(OffsetY);

    /// <param name="scale">DPI of the screen divided by 96.</param>
    public static WidgetPlacement Capture(string? screen, Rectangle workArea, Rectangle bounds, double scale)
    {
        scale = scale > 0 ? scale : 1;
        var right = bounds.Left + (bounds.Width / 2) > workArea.Left + (workArea.Width / 2);
        var bottom = bounds.Top + (bounds.Height / 2) > workArea.Top + (workArea.Height / 2);
        return new WidgetPlacement
        {
            Screen = screen,
            Right = right,
            Bottom = bottom,
            OffsetX = (right ? workArea.Right - bounds.Right : bounds.Left - workArea.Left) / scale,
            OffsetY = (bottom ? workArea.Bottom - bounds.Bottom : bounds.Top - workArea.Top) / scale,
        };
    }

    /// <summary>Top-left corner for a widget of <paramref name="size"/>, kept inside <paramref name="workArea"/>.</summary>
    public Point Resolve(Rectangle workArea, Size size, double scale)
    {
        scale = scale > 0 ? scale : 1;
        var x = Right
            ? workArea.Right - size.Width - (int)Math.Round(OffsetX * scale)
            : workArea.Left + (int)Math.Round(OffsetX * scale);
        var y = Bottom
            ? workArea.Bottom - size.Height - (int)Math.Round(OffsetY * scale)
            : workArea.Top + (int)Math.Round(OffsetY * scale);

        return new Point(
            Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - size.Width)),
            Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size.Height)));
    }
}
