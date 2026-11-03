using NetPulse.Core;
using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>Download and upload of past speed tests, oldest on the left, with a hover tooltip.</summary>
internal sealed class SpeedHistoryChart : Control
{
    private static readonly Color DownloadColor = Color.FromArgb(16, 185, 129);
    private static readonly Color UploadColor = Color.FromArgb(59, 130, 246);
    private readonly ToolTip _toolTip = new();
    private IReadOnlyList<SpeedTestResult> _results = [];
    private int _hover = -1;

    public SpeedHistoryChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = SystemColors.Window;
    }

    /// <param name="newestFirst">As stored in <see cref="SpeedTestHistory"/>.</param>
    public void SetData(IReadOnlyList<SpeedTestResult> newestFirst, int maxPoints = 60)
    {
        _results = [.. newestFirst.Take(maxPoints).Reverse()];
        _hover = -1;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var plot = PlotArea();
        if (_results.Count == 0)
        {
            TextRenderer.DrawText(g, T("还没有测速记录", "No speed tests yet"), Font, plot, SystemColors.GrayText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var max = Math.Max(1.0, _results.Max(r => Math.Max(r.DownloadBytesPerSecond, r.UploadBytesPerSecond)));
        var top = NiceScale.Ceiling(max, SpeedUnit.Bits);
        using var gridPen = new Pen(SystemColors.ControlLight) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
        g.DrawLine(gridPen, plot.Left, plot.Top, plot.Right, plot.Top);
        g.DrawLine(SystemPens.ControlDark, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
        TextRenderer.DrawText(g, ByteFormatter.FormatSpeed(top, SpeedUnit.Bits, trimZeros: true), Font, new Point(plot.Left, 0), SystemColors.GrayText);

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        DrawSeries(g, plot, top, r => r.DownloadBytesPerSecond, DownloadColor);
        DrawSeries(g, plot, top, r => r.UploadBytesPerSecond, UploadColor);
        if (_hover >= 0)
        {
            var x = XOf(_hover, plot);
            using var pen = new Pen(Color.FromArgb(120, SystemColors.GrayText));
            g.DrawLine(pen, x, plot.Top, x, plot.Bottom);
        }

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        TextRenderer.DrawText(g, _results[0].StartedAt.ToString("M/d HH:mm"), Font, new Point(plot.Left, plot.Bottom + 2), SystemColors.GrayText);
        var last = _results[^1].StartedAt.ToString("M/d HH:mm");
        TextRenderer.DrawText(g, last, Font, new Point(plot.Right - TextRenderer.MeasureText(last, Font).Width, plot.Bottom + 2), SystemColors.GrayText);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var plot = PlotArea();
        var index = -1;
        if (_results.Count > 0 && plot.Contains(e.Location))
        {
            index = _results.Count == 1 ? 0 : (int)Math.Round((e.X - plot.Left) / (double)plot.Width * (_results.Count - 1));
            index = Math.Clamp(index, 0, _results.Count - 1);
        }

        if (index == _hover)
        {
            return;
        }

        _hover = index;
        Invalidate();
        if (index < 0)
        {
            _toolTip.Hide(this);
            return;
        }

        var r = _results[index];
        var latency = r.Latency is { } l ? $"\n{T("延迟", "Latency")} {l.MedianMs:0} ms" : string.Empty;
        _toolTip.Show(
            $"{r.StartedAt:yyyy-MM-dd HH:mm}\n↓ {ByteFormatter.FormatSpeed(r.DownloadBytesPerSecond, SpeedUnit.Bits)}\n↑ {ByteFormatter.FormatSpeed(r.UploadBytesPerSecond, SpeedUnit.Bits)}{latency}",
            this, e.X + 12, e.Y + 12);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _toolTip.Hide(this);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private void DrawSeries(Graphics g, Rectangle plot, double top, Func<SpeedTestResult, double> value, Color color)
    {
        var points = _results.Select((r, i) => new PointF(XOf(i, plot), plot.Bottom - (float)(Math.Min(value(r) / top, 1) * plot.Height))).ToArray();
        using var pen = new Pen(color, 2f);
        using var brush = new SolidBrush(color);
        if (points.Length > 1)
        {
            g.DrawLines(pen, points);
        }

        foreach (var point in points)
        {
            g.FillEllipse(brush, point.X - 2.5f, point.Y - 2.5f, 5f, 5f);
        }
    }

    private float XOf(int index, Rectangle plot) =>
        _results.Count == 1 ? plot.Left + (plot.Width / 2f) : plot.Left + (index * plot.Width / (float)(_results.Count - 1));

    private Rectangle PlotArea()
    {
        var text = Font.Height;
        return new Rectangle(8, text + 4, Math.Max(1, Width - 16), Math.Max(1, Height - (2 * text) - 12));
    }
}
