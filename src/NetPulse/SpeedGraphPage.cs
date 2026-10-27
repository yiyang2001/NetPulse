using System.Drawing.Drawing2D;
using NetPulse.Core;
using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>Live speed graph over the last minute or ten minutes, fed by the app's sampling timer.</summary>
internal sealed class SpeedGraphPage : UserControl, IActivatablePage
{
    private static readonly TimeSpan ShortWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LongWindow = TimeSpan.FromMinutes(10);

    private readonly SpeedHistory _history;
    private readonly AppSettings _settings;
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 500 };
    private readonly SpeedGraph _graph = new() { Dock = DockStyle.Fill };
    private readonly RadioButton _shortButton = new() { Text = T("最近 60 秒", "Last 60 seconds"), AutoSize = true, Checked = true };
    private readonly RadioButton _longButton = new() { Text = T("最近 10 分钟", "Last 10 minutes"), AutoSize = true };
    private readonly Label _currentValue = ValueLabel();
    private readonly Label _peakValue = ValueLabel();
    private readonly Label _averageValue = ValueLabel();

    public SpeedGraphPage(SpeedHistory history, AppSettings settings)
    {
        _history = history;
        _settings = settings;
        Padding = new Padding(12);

        _shortButton.CheckedChanged += (_, _) => RefreshData();
        var toolbar = Ui.Toolbar(
            _shortButton,
            _longButton,
            new Label { Text = "   " }, // spacer; Toolbar resets margins
            new Label { Text = T("■ 下载", "■ Download"), ForeColor = SpeedGraph.DownloadColor },
            new Label { Text = T("■ 上传", "■ Upload"), ForeColor = SpeedGraph.UploadColor });

        var stats = new TableLayoutPanel { Dock = DockStyle.Top, Height = 64, ColumnCount = 3, RowCount = 1 };
        for (var i = 0; i < 3; i++)
        {
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        }

        stats.Controls.Add(Card(T("当前", "Current"), _currentValue), 0, 0);
        stats.Controls.Add(Card(T("峰值", "Peak"), _peakValue), 1, 0);
        stats.Controls.Add(Card(T("平均", "Average"), _averageValue), 2, 0);

        var graphPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        graphPanel.Controls.Add(_graph);

        // Dock order: last added is laid out first.
        Controls.Add(graphPanel);
        Controls.Add(stats);
        Controls.Add(toolbar);
        _refreshTimer.Tick += (_, _) => RefreshData();
    }

    public void OnActivated()
    {
        RefreshData();
        _refreshTimer.Start();
    }

    public void OnDeactivated() => _refreshTimer.Stop();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void RefreshData()
    {
        var window = _shortButton.Checked ? ShortWindow : LongWindow;
        var points = _history.Recent(window);
        var stats = SpeedHistory.Summarize(points);
        var unit = _settings.Unit;
        var last = points.Count > 0 ? points[^1] : default;

        _currentValue.Text = Pair(last.DownloadBytesPerSecond, last.UploadBytesPerSecond, unit);
        _peakValue.Text = Pair(stats.PeakDownload, stats.PeakUpload, unit);
        _averageValue.Text = Pair(stats.AverageDownload, stats.AverageUpload, unit);
        _graph.SetData(points, window, unit);
    }

    private static string Pair(double down, double up, SpeedUnit unit) =>
        $"↓ {ByteFormatter.FormatSpeed(down, unit)}\n↑ {ByteFormatter.FormatSpeed(up, unit)}";

    private static Label ValueLabel() => new() { Dock = DockStyle.Fill, AutoSize = false };

    private static Control Card(string title, Label value)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 8, 0) };
        var titleLabel = new Label { Text = title, Dock = DockStyle.Top, AutoSize = true, ForeColor = SystemColors.GrayText };
        panel.Controls.Add(value);
        panel.Controls.Add(titleLabel);
        return panel;
    }
}

/// <summary>Download as a filled area, upload as a line; time runs left to right, newest at the right edge.</summary>
internal sealed class SpeedGraph : Control
{
    public static readonly Color DownloadColor = Color.FromArgb(16, 185, 129);
    public static readonly Color UploadColor = Color.FromArgb(59, 130, 246);

    private readonly ToolTip _toolTip = new();
    private IReadOnlyList<SpeedPoint> _points = [];
    private TimeSpan _window = TimeSpan.FromSeconds(60);
    private SpeedUnit _unit;
    private int _hoverX = -1;

    public SpeedGraph()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = SystemColors.Window;
    }

    public void SetData(IReadOnlyList<SpeedPoint> points, TimeSpan window, SpeedUnit unit)
    {
        _points = points;
        _window = window;
        _unit = unit;
        Invalidate();
        if (_hoverX >= 0)
        {
            UpdateToolTip();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var plot = PlotArea();
        var max = NiceScale.Ceiling(
            _points.Count == 0 ? 0 : _points.Max(p => Math.Max(p.DownloadBytesPerSecond, p.UploadBytesPerSecond)), _unit);

        using var gridPen = new Pen(SystemColors.ControlDark) { DashStyle = DashStyle.Dot };
        g.DrawLine(gridPen, plot.Left, plot.Top, plot.Right, plot.Top);
        g.DrawLine(gridPen, plot.Left, plot.Top + (plot.Height / 2), plot.Right, plot.Top + (plot.Height / 2));
        g.DrawLine(SystemPens.ControlDark, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
        TextRenderer.DrawText(g, ByteFormatter.FormatSpeed(max, _unit, trimZeros: true), Font, new Point(plot.Left, plot.Top - Font.Height - 2), SystemColors.GrayText);
        TextRenderer.DrawText(g, ByteFormatter.FormatSpeed(max / 2, _unit, trimZeros: true), Font, new Point(plot.Left, plot.Top + (plot.Height / 2) - Font.Height - 2), SystemColors.GrayText);

        var windowLabel = _window > TimeSpan.FromMinutes(1) ? T($"{_window.TotalMinutes:0} 分钟前", $"{_window.TotalMinutes:0} min ago") : T($"{_window.TotalSeconds:0} 秒前", $"{_window.TotalSeconds:0} s ago");
        TextRenderer.DrawText(g, windowLabel, Font, new Point(plot.Left, plot.Bottom + 2), SystemColors.GrayText);
        var nowSize = TextRenderer.MeasureText(T("现在", "now"), Font);
        TextRenderer.DrawText(g, T("现在", "now"), Font, new Point(plot.Right - nowSize.Width, plot.Bottom + 2), SystemColors.GrayText);

        if (_points.Count < 2)
        {
            return;
        }

        var down = ToScreen(plot, max, p => p.DownloadBytesPerSecond);
        var up = ToScreen(plot, max, p => p.UploadBytesPerSecond);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var area = new GraphicsPath())
        {
            area.AddLines(down);
            area.AddLine(down[^1], new PointF(down[^1].X, plot.Bottom));
            area.AddLine(new PointF(down[^1].X, plot.Bottom), new PointF(down[0].X, plot.Bottom));
            area.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(60, DownloadColor));
            g.FillPath(fill, area);
        }

        using var downPen = new Pen(DownloadColor, 1.6f * DeviceDpi / 96f);
        using var upPen = new Pen(UploadColor, 1.6f * DeviceDpi / 96f);
        g.DrawLines(downPen, down);
        g.DrawLines(upPen, up);

        if (_hoverX >= plot.Left && _hoverX <= plot.Right)
        {
            g.SmoothingMode = SmoothingMode.None;
            g.DrawLine(SystemPens.GrayText, _hoverX, plot.Top, _hoverX, plot.Bottom);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hoverX = PlotArea().Contains(e.Location) ? e.X : -1;
        Invalidate();
        UpdateToolTip();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverX = -1;
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

    private void UpdateToolTip()
    {
        var plot = PlotArea();
        if (_hoverX < 0 || _points.Count == 0)
        {
            _toolTip.Hide(this);
            return;
        }

        var newest = _points[^1].Time;
        var time = newest - (_window * ((plot.Right - _hoverX) / (double)plot.Width));
        var nearest = _points.MinBy(p => Math.Abs((p.Time - time).Ticks));
        var ago = (newest - nearest.Time).TotalSeconds;
        _toolTip.Show(
            $"{(ago < 0.5 ? T("现在", "now") : T($"{ago:0} 秒前", $"{ago:0} s ago"))}\n↓ {ByteFormatter.FormatSpeed(nearest.DownloadBytesPerSecond, _unit)}\n↑ {ByteFormatter.FormatSpeed(nearest.UploadBytesPerSecond, _unit)}",
            this, _hoverX + 12, plot.Top + 8);
    }

    private PointF[] ToScreen(Rectangle plot, double max, Func<SpeedPoint, double> value)
    {
        var newest = _points[^1].Time;
        var result = new PointF[_points.Count];
        for (var i = 0; i < _points.Count; i++)
        {
            var age = (newest - _points[i].Time) / _window;
            var x = plot.Right - (float)(age * plot.Width);
            var y = plot.Bottom - (float)(Math.Min(1, value(_points[i]) / max) * plot.Height);
            result[i] = new PointF(x, y);
        }

        return result;
    }

    private Rectangle PlotArea()
    {
        var textHeight = Font.Height;
        return new Rectangle(4, textHeight + 6, Math.Max(1, Width - 8), Math.Max(1, Height - (2 * textHeight) - 12));
    }
}
