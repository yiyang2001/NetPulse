using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace NetPulse;

/// <summary>
/// Draws a speed as a tray icon: up to three characters and, under them, a bar that grows with the
/// unit (K, M, G). The exact value with its unit is in the tray tooltip.
/// </summary>
internal static class TrayIconRenderer
{
    /// <summary>The returned icon does not own its handle: release it with <see cref="Destroy"/>.</summary>
    public static Icon Render(string text, int unitIndex, Color color)
    {
        var size = SystemInformation.SmallIconSize;
        using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // ClearType would fringe on a transparent bitmap
            g.SmoothingMode = SmoothingMode.None;

            var barHeight = Math.Max(2, size.Height / 8);
            var textHeight = size.Height - barHeight - 1;
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
            };

            var pixels = textHeight * 0.95f;
            Font font;
            while (true)
            {
                font = new Font("Segoe UI", pixels, FontStyle.Bold, GraphicsUnit.Pixel);
                if (pixels <= 6f || g.MeasureString(text, font, PointF.Empty, format).Width <= size.Width)
                {
                    break;
                }

                font.Dispose();
                pixels -= 0.5f;
            }

            using (font)
            using (var brush = new SolidBrush(color))
            {
                g.DrawString(text, font, brush, new RectangleF(0, 0, size.Width, textHeight), format);
                if (unitIndex > 0)
                {
                    var width = (int)Math.Round(size.Width * Math.Min(unitIndex, 3) / 3.0);
                    g.FillRectangle(brush, 0, size.Height - barHeight, width, barHeight);
                }
            }
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    public static void Destroy(Icon icon) => NativeMethods.DestroyIcon(icon.Handle);
}
