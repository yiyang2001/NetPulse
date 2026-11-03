using NetPulse.Core;

namespace NetPulse;

/// <summary>
/// Small borderless always-on-top window showing download and upload speed.
/// It never takes focus, stays out of Alt+Tab and the taskbar, and can be made click-through.
/// </summary>
internal sealed class SpeedWidget : Form
{
    private const int SnapDistance = 12;
    private static readonly TimeSpan MiniGraphWindow = TimeSpan.FromSeconds(60);

    private readonly ToolTip _toolTip = new() { InitialDelay = 400, ReshowDelay = 200 };
    private WidgetPalette _palette = WidgetPalette.Dark;
    private SpeedUnit _unit = SpeedUnit.Bytes;
    private string _downText = "0 B/s";
    private string _upText = "0 B/s";
    private string _totalText = "0 B/s";
    private WidgetLayout _layout = WidgetLayout.TwoLines;
    private bool _showGraph;
    private SpeedHistory? _history;
    private int _graphWidth;
    private int _graphGap;
    private int _triangleSize;
    private int _valueWidth;
    private int _cellGap;
    private string _toolTipText = string.Empty;
    private bool _locked;
    private bool _clickThrough;
    private bool _topMost = true;
    private Padding _textPadding;
    private int _arrowWidth;
    private int _lineHeight;

    public SpeedWidget()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        Text = "NetPulse";
        Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        RecalculateLayout();
    }

    /// <summary>Double-click on the widget.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>The user finished dragging the widget to a new place.</summary>
    public event EventHandler? Moved;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            if (_topMost)
            {
                cp.ExStyle |= NativeMethods.WS_EX_TOPMOST;
            }

            return cp;
        }
    }

    public void ApplySettings(AppSettings settings)
    {
        _unit = settings.Unit;
        _palette = WidgetPalette.For(settings.Theme);
        _locked = settings.LockPosition;
        _layout = settings.Layout;
        _showGraph = settings.ShowMiniGraph && _history is not null;
        Opacity = settings.Opacity;
        BackColor = _palette.Background;

        if (Math.Abs(Font.SizeInPoints - settings.FontSize) > 0.01f)
        {
            var old = Font;
            Font = new Font(old.FontFamily, settings.FontSize, old.Style, GraphicsUnit.Point);
            old.Dispose();
        }

        SetTopMost(settings.AlwaysOnTop);
        SetClickThrough(settings.ClickThrough);

        // A bigger or smaller widget keeps its anchored corner instead of growing off the screen.
        var before = IsHandleCreated && Visible ? CapturePlacement() : null;
        RecalculateLayout();
        if (before is not null)
        {
            var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == before.Screen) ?? Screen.FromRectangle(Bounds);
            Location = before.Resolve(screen.WorkingArea, Size, DeviceDpi / 96.0);
        }

        Invalidate();
    }

    /// <summary>Source of the mini graph; the owner keeps adding samples to it.</summary>
    public void SetHistory(SpeedHistory history) => _history = history;

    public void ShowSpeed(SpeedSample sample)
    {
        var down = ByteFormatter.FormatSpeed(sample.DownloadBytesPerSecond, _unit);
        var up = ByteFormatter.FormatSpeed(sample.UploadBytesPerSecond, _unit);
        var total = ByteFormatter.FormatSpeed(sample.DownloadBytesPerSecond + sample.UploadBytesPerSecond, _unit);
        // The graph moves every sample even when the texts stay the same.
        if (_showGraph || down != _downText || up != _upText || total != _totalText)
        {
            _downText = down;
            _upText = up;
            _totalText = total;
            Invalidate();
        }
    }

    public void SetToolTipText(string text)
    {
        if (text != _toolTipText)
        {
            _toolTipText = text;
            _toolTip.SetToolTip(this, text);
        }
    }

    public WidgetPlacement CapturePlacement()
    {
        var screen = Screen.FromRectangle(Bounds);
        return WidgetPlacement.Capture(screen.DeviceName, screen.WorkingArea, Bounds, DeviceDpi / 96.0);
    }

    /// <summary>
    /// Places the widget at the saved spot on the screen it was on; falls back to the primary screen's
    /// bottom-right corner when nothing was saved or the screen is gone.
    /// </summary>
    public void RestorePosition(WidgetPlacement? placement, int? x, int? y)
    {
        if (placement is not null)
        {
            var target = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == placement.Screen)
                ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
            Location = placement.Resolve(target.WorkingArea, Size, DeviceDpi / 96.0);
            return;
        }

        if (x is { } left && y is { } top)
        {
            var center = new Point(left + Width / 2, top + Height / 2);
            if (Screen.AllScreens.Any(s => s.Bounds.Contains(center)))
            {
                Location = new Point(left, top);
                return;
            }
        }

        var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
    }

    /// <summary>Re-asserts z-order; other topmost windows such as the taskbar can cover the widget.</summary>
    public void BringToTopIfTopMost()
    {
        if (_topMost && IsHandleCreated && Visible)
        {
            NativeMethods.SetWindowPos(
                Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.TryUseRoundedCorners(Handle);
        SetClickThrough(_clickThrough, force: true);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        RecalculateLayout();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (e.Clicks >= 2)
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (!_locked)
        {
            // Let Windows run its own move loop, as if the caption bar had been grabbed.
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, NativeMethods.HTCAPTION, 0);
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == NativeMethods.WM_EXITSIZEMOVE)
        {
            SnapToScreenEdges();
            Moved?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_palette.Background);
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Right;

        var left = _textPadding.Left;
        var top = _textPadding.Top;
        switch (_layout)
        {
            case WidgetLayout.OneLine:
                DrawCell(g, left, top, _downText, flags, down: true, up: false);
                DrawCell(g, left + _arrowWidth + _valueWidth + _cellGap, top, _upText, flags, down: false, up: true);
                break;
            case WidgetLayout.TotalOnly:
                DrawCell(g, left, top, _totalText, flags, down: true, up: true);
                break;
            default:
                DrawCell(g, left, top, _downText, flags, down: true, up: false);
                DrawCell(g, left, top + _lineHeight, _upText, flags, down: false, up: true);
                break;
        }

        if (_showGraph && _history is not null)
        {
            var graph = new Rectangle(
                ClientSize.Width - _textPadding.Right - _graphWidth,
                _textPadding.Top,
                _graphWidth,
                ClientSize.Height - _textPadding.Vertical);
            DrawMiniGraph(g, graph, _history.Recent(MiniGraphWindow));
        }
    }

    /// <summary>One arrow (both for the total) and a right-aligned value.</summary>
    private void DrawCell(Graphics g, int left, int lineTop, string text, TextFormatFlags flags, bool down, bool up)
    {
        var x = left;
        if (down)
        {
            DrawTriangle(g, x, lineTop, pointsDown: true, _palette.Download);
            x += _triangleSize + 1;
        }

        if (up)
        {
            DrawTriangle(g, x, lineTop, pointsDown: false, _palette.Upload);
            x += _triangleSize + 1;
        }

        var valueLeft = left + (down && up ? TotalArrowBlock : _arrowWidth);
        TextRenderer.DrawText(g, text, Font, new Rectangle(valueLeft, lineTop, _valueWidth, _lineHeight), _palette.Text, flags);
    }

    private int TotalArrowBlock => (2 * _triangleSize) + 2 + (_arrowWidth - _triangleSize);

    private void DrawMiniGraph(Graphics g, Rectangle area, IReadOnlyList<SpeedPoint> points)
    {
        if (points.Count < 2 || area.Width < 4 || area.Height < 4)
        {
            return;
        }

        var peak = points.Max(p => Math.Max(p.DownloadBytesPerSecond, p.UploadBytesPerSecond));
        var top = NiceScale.Ceiling(peak, _unit);
        var newest = points[^1].Time;
        PointF Map(SpeedPoint p, double value) => new(
            area.Right - (float)((newest - p.Time).TotalSeconds / MiniGraphWindow.TotalSeconds * area.Width),
            area.Bottom - (float)(Math.Min(value / top, 1) * area.Height));

        var smoothing = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var baseline = new Pen(Color.FromArgb(70, _palette.Text), 1f))
        {
            g.DrawLine(baseline, area.Left, area.Bottom, area.Right, area.Bottom);
        }

        using (var downPen = new Pen(_palette.Download, 1.5f))
        using (var upPen = new Pen(_palette.Upload, 1.5f))
        {
            g.DrawLines(downPen, points.Select(p => Map(p, p.DownloadBytesPerSecond)).ToArray());
            g.DrawLines(upPen, points.Select(p => Map(p, p.UploadBytesPerSecond)).ToArray());
        }

        g.SmoothingMode = smoothing;
    }

    /// <summary>Solid triangles read better than the thin ↓/↑ glyphs of most UI fonts at small sizes.</summary>
    private void DrawTriangle(Graphics g, int left, int lineTop, bool pointsDown, Color color)
    {
        var size = (float)_triangleSize;
        var cx = left + (size / 2f);
        var cy = lineTop + (_lineHeight / 2f);
        var half = size / 2f;
        PointF[] points = pointsDown
            ? [new(cx - half, cy - (half * 0.8f)), new(cx + half, cy - (half * 0.8f)), new(cx, cy + (half * 0.9f))]
            : [new(cx - half, cy + (half * 0.8f)), new(cx + half, cy + (half * 0.8f)), new(cx, cy - (half * 0.9f))];

        var smoothing = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, points);
        g.SmoothingMode = smoothing;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Sizes the window for the widest value the current unit can print, so it never jitters.</summary>
    private void RecalculateLayout()
    {
        var widest = _unit == SpeedUnit.Bits ? "888.8 Mbps" : "888.8 MB/s";
        var valueSize = TextRenderer.MeasureText(widest, Font, Size.Empty, TextFormatFlags.NoPadding);
        var scale = DeviceDpi / 96f;

        _lineHeight = valueSize.Height + (int)(1 * scale);
        _triangleSize = (int)(_lineHeight * 0.42f);
        _arrowWidth = _triangleSize + (int)(8 * scale);
        _valueWidth = valueSize.Width;
        _cellGap = (int)(10 * scale);
        _graphGap = (int)(8 * scale);
        _graphWidth = (int)(56 * scale);
        _textPadding = new Padding((int)(8 * scale), (int)(4 * scale), (int)(8 * scale), (int)(4 * scale));

        var textWidth = _layout switch
        {
            WidgetLayout.OneLine => (2 * (_arrowWidth + _valueWidth)) + _cellGap,
            WidgetLayout.TotalOnly => TotalArrowBlock + _valueWidth,
            _ => _arrowWidth + _valueWidth,
        };
        var textHeight = _layout == WidgetLayout.TwoLines ? 2 * _lineHeight : _lineHeight;
        var graph = _showGraph ? _graphGap + _graphWidth : 0;
        ClientSize = new Size(
            _textPadding.Horizontal + textWidth + graph,
            _textPadding.Vertical + textHeight);
    }

    private void SetTopMost(bool topMost)
    {
        _topMost = topMost;
        if (IsHandleCreated)
        {
            NativeMethods.SetWindowPos(
                Handle, topMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    private void SetClickThrough(bool enabled, bool force = false)
    {
        if (!force && enabled == _clickThrough)
        {
            return;
        }

        _clickThrough = enabled;
        if (!IsHandleCreated)
        {
            return;
        }

        var style = NativeMethods.GetExStyle(Handle);
        style = enabled
            ? style | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED
            : style & ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetExStyle(Handle, style);
        NativeMethods.SetWindowPos(
            Handle, 0, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOZORDER);
    }

    private void SnapToScreenEdges()
    {
        var area = Screen.FromRectangle(Bounds).WorkingArea;
        var x = Left;
        var y = Top;
        if (Math.Abs(x - area.Left) <= SnapDistance)
        {
            x = area.Left;
        }
        else if (Math.Abs(area.Right - Right) <= SnapDistance)
        {
            x = area.Right - Width;
        }

        if (Math.Abs(y - area.Top) <= SnapDistance)
        {
            y = area.Top;
        }
        else if (Math.Abs(area.Bottom - Bottom) <= SnapDistance)
        {
            y = area.Bottom - Height;
        }

        Location = new Point(x, y);
    }
}
