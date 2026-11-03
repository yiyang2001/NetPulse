using System.Drawing.Drawing2D;
using NetPulse.Core;

namespace NetPulse;

/// <summary>Stacked daily bars (download below, upload on top) with a hover tooltip.</summary>
internal sealed class UsageChart : Control
{
    private static readonly Color DownloadColor = Color.FromArgb(16, 185, 129);
    private static readonly Color UploadColor = Color.FromArgb(59, 130, 246);
    private readonly ToolTip _toolTip = new();
    private IReadOnlyList<DailyUsage> _days = [];
    private int _hoverIndex = -1;
    private bool _monthly;

    public UsageChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = SystemColors.Window;
    }

    /// <param name="monthly">Each entry is a whole month (dated the 1st) and is labelled as one.</param>
    public void SetData(IReadOnlyList<DailyUsage> days, bool monthly = false)
    {
        _days = days;
        _monthly = monthly;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_days.Count == 0)
        {
            return;
        }

        var plot = PlotArea();
        var max = Math.Max(1, _days.Max(d => d.Total));
        using var gridPen = new Pen(SystemColors.ControlLight) { DashStyle = DashStyle.Dot };
        g.DrawLine(gridPen, plot.Left, plot.Top, plot.Right, plot.Top);
        g.DrawLine(SystemPens.ControlDark, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
        TextRenderer.DrawText(g, ByteFormatter.FormatSize(max), Font, new Point(plot.Left, 0), SystemColors.GrayText);

        var slot = plot.Width / (float)_days.Count;
        var barWidth = Math.Max(1f, slot * 0.7f);
        using var downBrush = new SolidBrush(DownloadColor);
        using var upBrush = new SolidBrush(UploadColor);
        using var hoverBrush = new SolidBrush(Color.FromArgb(40, SystemColors.Highlight));
        for (var i = 0; i < _days.Count; i++)
        {
            var x = plot.Left + (i * slot) + ((slot - barWidth) / 2);
            if (i == _hoverIndex)
            {
                g.FillRectangle(hoverBrush, plot.Left + (i * slot), plot.Top, slot, plot.Height);
            }

            var downHeight = (float)(_days[i].Downloaded / (double)max * plot.Height);
            var upHeight = (float)(_days[i].Uploaded / (double)max * plot.Height);
            g.FillRectangle(downBrush, x, plot.Bottom - downHeight, barWidth, downHeight);
            g.FillRectangle(upBrush, x, plot.Bottom - downHeight - upHeight, barWidth, upHeight);
        }

        var first = _days[0].Date.ToString(_monthly ? "yyyy-MM" : "M/d");
        var last = _days[^1].Date.ToString(_monthly ? "yyyy-MM" : "M/d");
        TextRenderer.DrawText(g, first, Font, new Point(plot.Left, plot.Bottom + 2), SystemColors.GrayText);
        var lastSize = TextRenderer.MeasureText(last, Font);
        TextRenderer.DrawText(g, last, Font, new Point(plot.Right - lastSize.Width, plot.Bottom + 2), SystemColors.GrayText);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var plot = PlotArea();
        var index = _days.Count == 0 || !plot.Contains(e.Location)
            ? -1
            : Math.Clamp((int)((e.X - plot.Left) / (plot.Width / (float)_days.Count)), 0, _days.Count - 1);
        if (index == _hoverIndex)
        {
            return;
        }

        _hoverIndex = index;
        Invalidate();
        if (index < 0)
        {
            _toolTip.Hide(this);
            return;
        }

        var day = _days[index];
        _toolTip.Show(
            $"{day.Date.ToString(_monthly ? "yyyy-MM" : "yyyy-MM-dd")}\n↓ {ByteFormatter.FormatSize(day.Downloaded)}\n↑ {ByteFormatter.FormatSize(day.Uploaded)}",
            this, e.X + 12, e.Y + 12);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
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

    private Rectangle PlotArea()
    {
        var textHeight = Font.Height;
        return new Rectangle(4, textHeight + 4, Math.Max(1, Width - 8), Math.Max(1, Height - (2 * textHeight) - 10));
    }
}
