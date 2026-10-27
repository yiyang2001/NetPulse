using NetPulse.Core;

namespace NetPulse;

/// <summary>
/// Small borderless always-on-top window showing download and upload speed.
/// It never takes focus, stays out of Alt+Tab and the taskbar, and can be made click-through.
/// </summary>
internal sealed class SpeedWidget : Form
{
    private const int SnapDistance = 12;

    private readonly ToolTip _toolTip = new() { InitialDelay = 400, ReshowDelay = 200 };
    private WidgetPalette _palette = WidgetPalette.Dark;
    private SpeedUnit _unit = SpeedUnit.Bytes;
    private string _downText = "0 B/s";
    private string _upText = "0 B/s";
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
        RecalculateLayout();
        Invalidate();
    }

    public void ShowSpeed(SpeedSample sample)
    {
        var down = ByteFormatter.FormatSpeed(sample.DownloadBytesPerSecond, _unit);
        var up = ByteFormatter.FormatSpeed(sample.UploadBytesPerSecond, _unit);
        if (down != _downText || up != _upText)
        {
            _downText = down;
            _upText = up;
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

    /// <summary>Places the widget at the saved spot if it is still on a screen, else bottom-right of the primary screen.</summary>
    public void RestorePosition(int? x, int? y)
    {
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
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

        var left = _textPadding.Left;
        var valueLeft = left + _arrowWidth;
        var valueWidth = ClientSize.Width - valueLeft - _textPadding.Right;
        var top = _textPadding.Top;

        DrawTriangle(g, left, top, pointsDown: true, _palette.Download);
        TextRenderer.DrawText(g, _downText, Font, new Rectangle(valueLeft, top, valueWidth, _lineHeight), _palette.Text, flags | TextFormatFlags.Right);

        top += _lineHeight;
        DrawTriangle(g, left, top, pointsDown: false, _palette.Upload);
        TextRenderer.DrawText(g, _upText, Font, new Rectangle(valueLeft, top, valueWidth, _lineHeight), _palette.Text, flags | TextFormatFlags.Right);
    }

    /// <summary>Solid triangles read better than the thin ↓/↑ glyphs of most UI fonts at small sizes.</summary>
    private void DrawTriangle(Graphics g, int left, int lineTop, bool pointsDown, Color color)
    {
        var size = _lineHeight * 0.42f;
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
        _arrowWidth = (int)(_lineHeight * 0.42f) + (int)(8 * scale);
        _textPadding = new Padding((int)(8 * scale), (int)(4 * scale), (int)(8 * scale), (int)(4 * scale));
        ClientSize = new Size(
            _textPadding.Horizontal + _arrowWidth + valueSize.Width,
            _textPadding.Vertical + (2 * _lineHeight));
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
