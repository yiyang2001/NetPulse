using System.ComponentModel;
using NetPulse.Core;
using NetPulse.Platform;
using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>
/// Nearby networks from Windows' own scan results (Native Wifi, no netsh) and how crowded each channel is.
/// Scanning is manual; it only listens, it does not connect to anything.
/// </summary>
internal sealed class WifiScanPanel : UserControl
{
    private static readonly TimeSpan ScanWait = TimeSpan.FromSeconds(4);

    private readonly Button _scan = new() { Text = T("扫描", "Scan"), AutoSize = true };
    private readonly ComboBox _band = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly LinkLabel _locationHint = new()
    {
        AutoSize = true,
        Visible = false,
        Dock = DockStyle.Top,
        Text = T("Windows 要求开启“位置”权限后，应用才能读取附近的 Wi-Fi 网络。打开位置设置", "Windows lets apps see nearby Wi-Fi networks only with the Location permission on. Open location settings"),
    };
    private readonly ListView _list = new FlickerFreeListView
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly ChannelChart _chart = new() { Dock = DockStyle.Fill };
    private readonly Label _advice = new() { Dock = DockStyle.Top, AutoSize = false, Height = 48, Padding = new Padding(0, 4, 0, 0) };
    private IReadOnlyList<WifiAccessPoint> _accessPoints = [];
    private int _connectedChannel;
    private CancellationTokenSource? _scanning;

    public WifiScanPanel()
    {
        Padding = new Padding(8);
        _band.Items.AddRange(["2.4 GHz", "5 GHz", "6 GHz"]);
        _band.SelectedIndex = 0;
        _band.SelectedIndexChanged += (_, _) => ShowResults();

        _list.Columns.Add(T("网络名称 (SSID)", "Network (SSID)"), 170);
        _list.Columns.Add(T("信号", "Signal"), 140, HorizontalAlignment.Right);
        _list.Columns.Add(T("信道", "Channel"), 70, HorizontalAlignment.Right);
        _list.Columns.Add(T("频段", "Band"), 70);
        _list.Columns.Add(T("协议", "Standard"), 170);
        _list.Columns.Add("BSSID", 130);

        var chartBox = new GroupBox { Text = T("信道占用", "Channel congestion"), Dock = DockStyle.Bottom, Height = 225, Padding = new Padding(8, 4, 8, 8) };
        chartBox.Controls.Add(_chart);
        chartBox.Controls.Add(_advice);

        var linkText = T("打开位置设置", "Open location settings");
        _locationHint.LinkArea = new LinkArea(_locationHint.Text.Length - linkText.Length, linkText.Length);
        _locationHint.LinkClicked += (_, _) => Ui.OpenUri("ms-settings:privacy-location");

        Controls.Add(_list);
        Controls.Add(chartBox);
        Controls.Add(_locationHint);
        Controls.Add(Ui.Note(T("扫描只读取附近网络广播的信息，不会连接任何网络，也不会发送数据到网络上。信号越强的网络对同一信道的干扰越大。", "A scan only listens to what nearby networks broadcast; it connects to nothing and sends nothing over the network. Stronger networks disturb their channel more.")));
        Controls.Add(Ui.Toolbar(_scan, new Label { Text = T("频段", "Band") }, _band, _status));
        _scan.Click += async (_, _) => await ScanAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scanning?.Cancel();
        }

        base.Dispose(disposing);
    }

    private async Task ScanAsync()
    {
        if (_scanning is not null)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _scanning = cts;
        _scan.Enabled = false;
        _locationHint.Visible = false;
        _status.Text = T("正在扫描…", "Scanning…");
        try
        {
            Guid? id;
            string? connectedBssid = null;
            using (var wlan = new WlanClient())
            {
                var interfaces = wlan.GetInterfaces();
                id = interfaces.Count > 0 ? interfaces[0].Id : null;
                if (id is not { } adapter)
                {
                    _status.Text = T("没有找到无线网卡。", "No wireless adapter found.");
                    return;
                }

                (connectedBssid, _connectedChannel) = ReadConnection(wlan, adapter);
                wlan.RequestScan(adapter);
            }

            await Task.Delay(ScanWait, cts.Token); // the adapter needs a few seconds to hear every channel

            using (var wlan = new WlanClient())
            {
                _accessPoints = wlan.GetAccessPoints(id.Value, connectedBssid);
            }

            ShowResults();
            _status.Text = T($"{_accessPoints.Count} 个接入点，{DateTime.Now:HH:mm:ss}", $"{_accessPoints.Count} access points, {DateTime.Now:HH:mm:ss}");
            _locationHint.Visible = _accessPoints.Count == 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Win32Exception ex)
        {
            ErrorLog.Write(ex, "WifiScanPanel");
            _locationHint.Visible = ex.NativeErrorCode == WlanClient.ErrorAccessDenied;
            _status.Text = T("扫描失败：", "Scan failed: ") + ex.Message;
        }
        finally
        {
            _scanning = null;
            _scan.Enabled = true;
        }
    }

    private static (string? Bssid, int Channel) ReadConnection(WlanClient wlan, Guid id)
    {
        try
        {
            return wlan.GetCurrentConnection(id) is { } connection ? (connection.Bssid, (int)(connection.Channel ?? 0)) : (null, 0);
        }
        catch (Win32Exception)
        {
            return (null, 0); // no permission to read the connection; the scan itself may still work
        }
    }

    private void ShowResults()
    {
        var band = _band.SelectedItem as string ?? "2.4 GHz";
        var inBand = _accessPoints.Where(a => a.Band == band).ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var ap in inBand)
        {
            var item = new ListViewItem(
            [
                (string.IsNullOrEmpty(ap.Ssid) ? T("（隐藏网络）", "(hidden network)") : ap.Ssid) + (ap.IsConnected ? T("  ← 已连接", "  ← connected") : string.Empty),
                $"{ap.SignalQuality}%  ({ap.RssiDbm} dBm)",
                ap.Channel.ToString(),
                ap.Band,
                ap.Phy,
                ap.Bssid,
            ]);
            if (ap.IsConnected)
            {
                item.Font = new Font(_list.Font, FontStyle.Bold);
            }

            _list.Items.Add(item);
        }

        _list.EndUpdate();

        var loads = WifiChannels.Congestion(_accessPoints, band);
        var recommended = WifiChannels.Recommend(_accessPoints, band);
        _chart.SetData(loads, recommended, _accessPoints.Any(a => a.IsConnected && a.Band == band) ? _connectedChannel : null);
        _advice.Text = _accessPoints.Count == 0
            ? T("点击“扫描”查看附近的网络。", "Click “Scan” to see nearby networks.")
            : inBand.Count == 0
                ? T($"没有扫描到 {band} 的网络。", $"No {band} networks found.")
                : recommended is { } channel
                    ? T($"干扰最小的信道：{channel}。绿色柱为推荐，蓝色为当前连接所在的信道。", $"Least crowded channel: {channel}. The green bar is the recommendation, the blue one your current channel.") +
                        (band == "2.4 GHz" ? T("\n2.4 GHz 里只有 1、6、11 三个信道互不重叠。", "\nOnly channels 1, 6 and 11 do not overlap in 2.4 GHz.") : string.Empty)
                    : T("6 GHz 的信道很多、互不重叠，通常不需要特意选择。", "6 GHz has many channels that do not overlap; picking one rarely matters.");
    }
}

/// <summary>Bars of the load per channel, with the recommended and the current channel highlighted.</summary>
internal sealed class ChannelChart : Control
{
    private static readonly Color Normal = Color.FromArgb(148, 163, 184);
    private static readonly Color Recommended = Color.FromArgb(16, 185, 129);
    private static readonly Color Current = Color.FromArgb(59, 130, 246);
    private readonly ToolTip _toolTip = new();
    private IReadOnlyList<ChannelLoad> _loads = [];
    private int? _recommended;
    private int? _current;
    private int _hover = -1;

    public ChannelChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = SystemColors.Window;
    }

    public void SetData(IReadOnlyList<ChannelLoad> loads, int? recommended, int? current)
    {
        _loads = loads;
        _recommended = recommended;
        _current = current;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_loads.Count == 0)
        {
            return;
        }

        var plot = PlotArea();
        var max = Math.Max(1.0, _loads.Max(l => l.Load));
        g.DrawLine(SystemPens.ControlDark, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
        var slot = plot.Width / (float)_loads.Count;
        var barWidth = Math.Max(2f, slot * 0.7f);
        for (var i = 0; i < _loads.Count; i++)
        {
            var load = _loads[i];
            var x = plot.Left + (i * slot) + ((slot - barWidth) / 2);
            var height = (float)(load.Load / max * (plot.Height - 2));
            var color = load.Channel == _current ? Current : load.Channel == _recommended ? Recommended : Normal;
            using var brush = new SolidBrush(i == _hover ? ControlPaint.Dark(color, 0.1f) : color);
            g.FillRectangle(brush, x, plot.Bottom - height, barWidth, Math.Max(height, load.Load > 0 ? 2 : 0));

            var label = load.Channel.ToString();
            var size = TextRenderer.MeasureText(label, Font);
            if (slot >= size.Width * 0.9f || i % 2 == 0)
            {
                TextRenderer.DrawText(g, label, Font, new Point((int)(x + (barWidth / 2) - (size.Width / 2f)), plot.Bottom + 2), SystemColors.GrayText);
            }

            if (load.Networks > 0)
            {
                var count = load.Networks.ToString();
                var countSize = TextRenderer.MeasureText(count, Font);
                TextRenderer.DrawText(g, count, Font, new Point((int)(x + (barWidth / 2) - (countSize.Width / 2f)), (int)(plot.Bottom - height - countSize.Height)), SystemColors.ControlText);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var plot = PlotArea();
        var index = _loads.Count == 0 || !plot.Contains(e.Location)
            ? -1
            : Math.Clamp((int)((e.X - plot.Left) / (plot.Width / (float)_loads.Count)), 0, _loads.Count - 1);
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

        var load = _loads[index];
        _toolTip.Show(
            T($"信道 {load.Channel}\n网络数 {load.Networks}\n占用 {load.Load:0.0}", $"Channel {load.Channel}\nNetworks {load.Networks}\nLoad {load.Load:0.0}"),
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

    private Rectangle PlotArea()
    {
        var text = Font.Height;
        return new Rectangle(4, text + 2, Math.Max(1, Width - 8), Math.Max(1, Height - (2 * text) - 6));
    }
}
