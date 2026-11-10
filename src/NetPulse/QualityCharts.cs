using System.Drawing.Drawing2D;
using NetPulse.Core;
using static NetPulse.Core.Lang;

namespace NetPulse;

internal static class QualityLook
{
    public static readonly Color GatewayLine = System.Drawing.Color.FromArgb(16, 185, 129);
    public static readonly Color ExternalLine = System.Drawing.Color.FromArgb(59, 130, 246);
    public static readonly Color SignalLine = System.Drawing.Color.FromArgb(148, 163, 184);

    public static Color For(QualityState state) => state switch
    {
        QualityState.Ok => System.Drawing.Color.FromArgb(16, 185, 129),
        QualityState.HighLatency => System.Drawing.Color.FromArgb(245, 158, 11),
        QualityState.Loss => System.Drawing.Color.FromArgb(250, 204, 21),
        QualityState.Disconnected => System.Drawing.Color.FromArgb(148, 163, 184),
        QualityState.DnsOutage => System.Drawing.Color.FromArgb(6, 182, 212),
        QualityState.ExternalOutage => System.Drawing.Color.FromArgb(168, 85, 247),
        _ => System.Drawing.Color.FromArgb(239, 68, 68),
    };

    public static string Name(QualityState state) => state switch
    {
        QualityState.Ok => T("正常", "OK"),
        QualityState.HighLatency => T("延迟高", "High latency"),
        QualityState.Loss => T("丢包", "Packet loss"),
        QualityState.Disconnected => T("未连接", "Not connected"),
        QualityState.DnsOutage => T("DNS 问题", "DNS problem"),
        QualityState.ExternalOutage => T("外网问题", "Internet problem"),
        _ => T("本地问题", "Local problem"),
    };

    public static string Name(OutageKind kind) => Name(kind switch
    {
        OutageKind.Local => QualityState.LocalOutage,
        OutageKind.External => QualityState.ExternalOutage,
        _ => QualityState.DnsOutage,
    });
}

/// <summary>The last 24 hours: one colored cell per minute on top, the gateway and internet latency below, hover for values.</summary>
internal sealed class QualityTimeline : Control
{
    private const int StripHeight = 18;
    private readonly ToolTip _toolTip = new();
    private IReadOnlyList<QualityMinute> _minutes = [];
    private DateTime _end = DateTime.Now;
    private int _hoverMinute = -1;

    public QualityTimeline()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = SystemColors.Window;
    }

    /// <summary>How much of the stored 24 hours is shown; shorter spans make single minutes readable.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public TimeSpan Span { get; set; } = QualityLedger.TimelineSpan;

    private DateTime Start => _end - Span;

    public void SetData(IReadOnlyList<QualityMinute> minutes, DateTime now)
    {
        _minutes = minutes;
        _end = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind).AddMinutes(1);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var (strip, plot) = Areas();
        var total = Span.TotalMinutes;
        float X(DateTime t) => strip.Left + (float)((t - Start).TotalMinutes / total * strip.Width);

        // Unmonitored time stays this faint background.
        using (var empty = new SolidBrush(SystemColors.Control))
        {
            g.FillRectangle(empty, strip);
        }

        foreach (var minute in _minutes)
        {
            using var brush = new SolidBrush(QualityLook.For(minute.State));
            var x = X(minute.Minute);
            g.FillRectangle(brush, x, strip.Top, Math.Max(1f, X(minute.Minute.AddMinutes(1)) - x), strip.Height);
        }

        var max = Math.Max(10, _minutes.Select(m => Math.Max(m.GatewayMs ?? 0, m.ExternalMs ?? 0)).DefaultIfEmpty(0).Max());
        max = NiceMs(max);
        using var gridPen = new Pen(SystemColors.ControlLight) { DashStyle = DashStyle.Dot };
        g.DrawLine(gridPen, plot.Left, plot.Top, plot.Right, plot.Top);
        g.DrawLine(SystemPens.ControlDark, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
        TextRenderer.DrawText(g, $"{max:0} ms", Font, new Point(plot.Left, plot.Top), SystemColors.GrayText);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Wi-Fi signal on its own 0–100 % scale, dashed and behind the latency lines.
        DrawLine(g, QualityLook.SignalLine, m => m.SignalPercent, X, plot, 100, dashed: true);
        DrawLine(g, QualityLook.GatewayLine, m => m.GatewayMs, X, plot, max);
        DrawLine(g, QualityLook.ExternalLine, m => m.ExternalMs, X, plot, max);
        g.SmoothingMode = SmoothingMode.None;

        for (var quarter = 0; quarter <= 4; quarter++)
        {
            var t = Start + (Span * quarter / 4);
            var label = quarter == 4 ? T("现在", "now") : t.ToString("HH:mm");
            var size = TextRenderer.MeasureText(label, Font);
            var x = Math.Clamp((int)X(t) - (size.Width / 2), plot.Left, plot.Right - size.Width);
            TextRenderer.DrawText(g, label, Font, new Point(x, plot.Bottom + 2), SystemColors.GrayText);
        }

        if (_hoverMinute >= 0)
        {
            using var hoverPen = new Pen(SystemColors.Highlight);
            var x = X(Start.AddMinutes(_hoverMinute));
            g.DrawLine(hoverPen, x, strip.Top, x, plot.Bottom);
        }
    }

    private void DrawLine(Graphics g, Color color, Func<QualityMinute, double?> value, Func<DateTime, float> x, Rectangle plot, double max, bool dashed = false)
    {
        using var pen = new Pen(color, 1.5f) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid };
        using var dot = new SolidBrush(color);
        PointF? previous = null;
        DateTime previousMinute = default;
        foreach (var minute in _minutes)
        {
            if (value(minute) is not { } ms)
            {
                previous = null; // a minute without replies breaks the line
                continue;
            }

            var point = new PointF(x(minute.Minute.AddSeconds(30)), plot.Bottom - (float)(Math.Min(ms, max) / max * plot.Height));
            if (previous is { } p && minute.Minute - previousMinute <= TimeSpan.FromMinutes(1))
            {
                g.DrawLine(pen, p, point);
            }
            else
            {
                g.FillEllipse(dot, point.X - 1, point.Y - 1, 2, 2);
            }

            previous = point;
            previousMinute = minute.Minute;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (strip, plot) = Areas();
        var index = e.X < strip.Left || e.X > strip.Right || e.Y < strip.Top || e.Y > plot.Bottom
            ? -1
            : Math.Clamp((int)((e.X - strip.Left) / (float)strip.Width * Span.TotalMinutes), 0, (int)Span.TotalMinutes - 1);
        if (index == _hoverMinute)
        {
            return;
        }

        _hoverMinute = index;
        Invalidate();
        if (index < 0)
        {
            _toolTip.Hide(this);
            return;
        }

        var time = Start.AddMinutes(index);
        var minute = _minutes.FirstOrDefault(m => m.Minute == time);
        var text = minute is null
            ? $"{time:HH:mm}\n" + T("未监测", "Not monitored")
            : $"{time:HH:mm}  {QualityLook.Name(minute.State)}\n" + T("网关 ", "Gateway ") + NetPulseContext.FormatLatency(minute.GatewayMs)
                + (minute.ExternalReplies > 0 || minute.State == QualityState.ExternalOutage ? "\n" + T("外网 ", "Internet ") + NetPulseContext.FormatLatency(minute.ExternalMs) : string.Empty);
        if (minute?.SignalPercent is { } signal)
        {
            text += "\n" + T($"Wi-Fi 信号 {signal:0}%", $"Wi-Fi signal {signal:0}%");
        }

        _toolTip.Show(text, this, e.X + 12, e.Y + 12);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverMinute = -1;
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

    private static double NiceMs(double value)
    {
        foreach (var step in new double[] { 10, 20, 50, 100, 200, 500, 1000 })
        {
            if (value <= step)
            {
                return step;
            }
        }

        return 1000;
    }

    private (Rectangle Strip, Rectangle Plot) Areas()
    {
        var textHeight = Font.Height;
        var strip = new Rectangle(4, 2, Math.Max(1, Width - 8), StripHeight);
        var plot = new Rectangle(4, strip.Bottom + 6, strip.Width, Math.Max(1, Height - strip.Bottom - 6 - textHeight - 6));
        return (strip, plot);
    }
}

/// <summary>Down time per day as bars; hover shows availability, outages and latency.</summary>
internal sealed class QualityDailyChart : Control
{
    private readonly ToolTip _toolTip = new();
    private IReadOnlyList<(DateOnly Date, QualitySummary Summary)> _days = [];
    private int _hoverIndex = -1;

    public QualityDailyChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = SystemColors.Window;
    }

    public void SetData(IReadOnlyList<(DateOnly Date, QualitySummary Summary)> days)
    {
        _days = days;
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
        var maxMinutes = Math.Max(1, _days.Max(d => d.Summary.DownSeconds) / 60);
        using var gridPen = new Pen(SystemColors.ControlLight) { DashStyle = DashStyle.Dot };
        g.DrawLine(gridPen, plot.Left, plot.Top, plot.Right, plot.Top);
        g.DrawLine(SystemPens.ControlDark, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
        var anyDown = _days.Any(d => d.Summary.DownSeconds > 0);
        TextRenderer.DrawText(
            g,
            anyDown ? T($"每天断线时长（最多 {maxMinutes:0.#} 分钟）", $"Down time per day (up to {maxMinutes:0.#} min)") : T("每天断线时长：没有断线", "Down time per day: no outages"),
            Font, new Point(plot.Left, 0), SystemColors.GrayText);

        var slot = plot.Width / (float)_days.Count;
        var barWidth = Math.Max(1f, slot * 0.7f);
        using var downBrush = new SolidBrush(QualityLook.For(QualityState.LocalOutage));
        using var okBrush = new SolidBrush(QualityLook.For(QualityState.Ok));
        using var hoverBrush = new SolidBrush(Color.FromArgb(40, SystemColors.Highlight));
        for (var i = 0; i < _days.Count; i++)
        {
            var summary = _days[i].Summary;
            var x = plot.Left + (i * slot) + ((slot - barWidth) / 2);
            if (i == _hoverIndex)
            {
                g.FillRectangle(hoverBrush, plot.Left + (i * slot), plot.Top, slot, plot.Height);
            }

            if (summary.MonitoredSeconds <= 0)
            {
                continue; // not monitored: no mark at all
            }

            var height = (float)(summary.DownSeconds / 60 / maxMinutes * plot.Height);
            if (height < 1)
            {
                g.FillRectangle(okBrush, x, plot.Bottom - 2, barWidth, 2); // monitored, no outage
            }
            else
            {
                g.FillRectangle(downBrush, x, plot.Bottom - height, barWidth, height);
            }
        }

        var first = _days[0].Date.ToString("M/d");
        var last = _days[^1].Date.ToString("M/d");
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

        var (date, summary) = _days[index];
        _toolTip.Show($"{date:yyyy-MM-dd}\n" + QualityPage.Describe(summary), this, e.X + 12, e.Y + 12);
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
