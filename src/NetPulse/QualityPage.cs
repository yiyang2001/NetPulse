using System.Globalization;
using System.Text;
using NetPulse.Core;
using NetPulse.Platform;
using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>Connection quality: live latency, 24-hour timeline, daily down time and the outage list.</summary>
internal sealed class QualityPage : UserControl, IActivatablePage
{
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 2000 };
    private readonly QualityLedger _ledger;
    private readonly QualityMonitor _monitor;
    private readonly QualityProbeService _probe;
    private readonly AppSettings _settings;
    private readonly Action _saveUsage;
    private readonly Panel _offPanel = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 8) };
    private readonly ComboBox _network = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly ComboBox _range = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly ComboBox _span = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly Label _nowTitle = TitleLabel(T("当前", "Now"));
    private readonly Label _nowValue = ValueLabel();
    private readonly Label _todayValue = ValueLabel();
    private readonly Label _rangeTitle = TitleLabel(string.Empty);
    private readonly Label _rangeValue = ValueLabel();
    private readonly QualityTimeline _timeline = new() { Dock = DockStyle.Fill };
    private readonly QualityDailyChart _daily = new() { Dock = DockStyle.Fill };
    private readonly ListView _outages = new FlickerFreeListView
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private string? _networkKey;
    private bool _loadingNetworks;
    private string? _outageSignature;

    public QualityPage(QualityLedger ledger, QualityMonitor monitor, QualityProbeService probe, AppSettings settings, Action enable, Action saveUsage)
    {
        _ledger = ledger;
        _monitor = monitor;
        _probe = probe;
        _settings = settings;
        _saveUsage = saveUsage;
        Padding = new Padding(12);

        var enableButton = new Button { Text = T("开启连接质量监测", "Turn on connection monitoring"), AutoSize = true };
        enableButton.Click += (_, _) =>
        {
            enable();
            RefreshData();
        };
        var offNote = Ui.Note(T(
            "连接质量监测默认关闭。开启后每 5 秒 Ping 一次网关（路由器），记录延迟、丢包和断线，只在局域网内进行。" +
            "也可以在“设置 → 连接质量”中再开启外网探测，用来区分是本地（Wi-Fi、路由器）还是运营商的问题。",
            "Connection monitoring is off by default. When on, NetPulse pings your gateway (router) every 5 seconds and records latency, " +
            "packet loss and outages; this stays inside your local network. In Settings → Connection quality you can also ping an internet " +
            "address, to tell local problems (Wi-Fi, router) from ISP problems."));
        enableButton.Dock = DockStyle.Top;
        _offPanel.Controls.Add(enableButton);
        _offPanel.Controls.Add(offNote);

        var cards = new TableLayoutPanel { Dock = DockStyle.Top, Height = 140, ColumnCount = 3, RowCount = 1 };
        for (var i = 0; i < 3; i++)
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        }

        cards.Controls.Add(Card(_nowTitle, _nowValue), 0, 0);
        cards.Controls.Add(Card(TitleLabel(T("今天", "Today")), _todayValue), 1, 0);
        cards.Controls.Add(Card(_rangeTitle, _rangeValue), 2, 0);

        var timelinePanel = new Panel { Dock = DockStyle.Top, Height = 130, Padding = new Padding(0, 4, 0, 4) };
        timelinePanel.Controls.Add(_timeline);
        var legend = Ui.Note(T(
            "时间线：绿 正常 · 橙 延迟高 · 黄 丢包 · 红 本地问题 · 紫 外网问题 · 青 DNS 问题 · 灰 未连接。曲线：绿 网关，蓝 外网，灰色虚线 Wi-Fi 信号（0–100%）。",
            "Timeline: green OK · orange slow · yellow loss · red local · purple internet · cyan DNS · gray offline. Lines: green gateway, blue internet, dashed gray Wi-Fi signal (0–100%)."));
        var dailyPanel = new Panel { Dock = DockStyle.Top, Height = 96, Padding = new Padding(0, 4, 0, 8) };
        dailyPanel.Controls.Add(_daily);

        _outages.Columns.Add(T("开始", "Start"), 160);
        _outages.Columns.Add(T("时长", "Duration"), 110, HorizontalAlignment.Right);
        _outages.Columns.Add(T("类别", "Kind"), 110);
        _outages.Columns.Add(T("网络", "Network"), 200);

        var exportButton = new Button { Text = T("导出断线记录 CSV…", "Export outages CSV…"), AutoSize = true };
        exportButton.Click += (_, _) => ExportCsv();
        var clearButton = new Button { Text = T("清空连接质量记录…", "Clear quality history…"), AutoSize = true };
        clearButton.Click += (_, _) => Clear();
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(clearButton);
        buttons.Controls.Add(exportButton);

        _range.Items.AddRange([T("最近 7 天", "Last 7 days"), T("最近 30 天", "Last 30 days")]);
        _range.SelectedIndex = 1;
        _range.SelectedIndexChanged += (_, _) => RefreshData();
        _network.SelectedIndexChanged += (_, _) =>
        {
            if (!_loadingNetworks)
            {
                _networkKey = (_network.SelectedItem as NetworkItem)?.Key;
                RefreshData();
            }
        };
        _span.Items.AddRange([T("1 小时", "1 hour"), T("6 小时", "6 hours"), T("24 小时", "24 hours")]);
        _span.SelectedIndex = 0; // the last hour, where single minutes are readable; 24 h is one click away
        _span.SelectedIndexChanged += (_, _) => RefreshData();
        var toolbar = Ui.Toolbar(
            new Label { Text = T("网络", "Network") }, _network,
            new Label { Text = T("范围", "Range") }, _range,
            new Label { Text = T("时间线", "Timeline") }, _span);

        // Dock order: last added is laid out first.
        Controls.Add(_outages);
        Controls.Add(dailyPanel);
        Controls.Add(legend);
        Controls.Add(timelinePanel);
        Controls.Add(cards);
        Controls.Add(toolbar);
        Controls.Add(_offPanel);
        Controls.Add(buttons);
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

    /// <summary>Availability, outages, latency and loss of a period, as two or three lines.</summary>
    internal static string Describe(QualitySummary summary)
    {
        if (summary.Availability is not { } availability)
        {
            return summary.DisconnectedSeconds > 0
                ? T($"未连接 {NetPulseContext.FormatDuration(TimeSpan.FromSeconds(summary.DisconnectedSeconds))}", $"Not connected for {NetPulseContext.FormatDuration(TimeSpan.FromSeconds(summary.DisconnectedSeconds))}")
                : T("没有记录", "No data");
        }

        var text = new StringBuilder();
        text.Append(T($"可用率 {availability:P2} · 断线 {summary.Outages} 次", $"{availability:P2} up · {summary.Outages} outage{(summary.Outages == 1 ? "" : "s")}"));
        if (summary.DownSeconds > 0)
        {
            text.Append(T($"，共 {NetPulseContext.FormatDuration(TimeSpan.FromSeconds(summary.DownSeconds))}", $", {NetPulseContext.FormatDuration(TimeSpan.FromSeconds(summary.DownSeconds))}"));
        }

        text.Append('\n').Append(T("网关 ", "Gateway ")).Append(Latency(summary.GatewayMedianMs, summary.GatewayP95Ms, summary.GatewayLossRate));
        if (summary.ExternalSamples > 0)
        {
            text.Append('\n').Append(T("外网 ", "Internet ")).Append(Latency(summary.ExternalMedianMs, summary.ExternalP95Ms, summary.ExternalLossRate));
        }

        if (summary.DnsFailureRate is { } dns)
        {
            text.Append('\n').Append(dns > 0 ? T($"DNS 失败 {dns:P1}", $"DNS {dns:P1} failed") : T("DNS 正常", "DNS OK"));
        }

        return text.ToString();
    }

    private static string Latency(double? median, double? p95, double? loss) =>
        (median is { } m ? $"≤{m:0} ms (P95 ≤{p95:0})" : T("无回应", "no replies"))
        + (loss is { } l && l > 0 ? T($" · 丢包 {l:P1}", $" · {l:P1} loss") : string.Empty);

    private void RefreshData()
    {
        var enabled = _settings.Quality.Enabled;
        _offPanel.Visible = !enabled;
        LoadNetworks();

        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        _nowValue.Text = DescribeNow(enabled);
        _todayValue.Text = Describe(_ledger.Summarize(today, today, _networkKey));
        var days = _range.SelectedIndex == 0 ? 7 : 30;
        var from = today.AddDays(-(days - 1));
        _rangeTitle.Text = T($"最近 {days} 天", $"Last {days} days");
        _rangeValue.Text = Describe(_ledger.Summarize(from, today, _networkKey));

        _timeline.Span = TimeSpan.FromHours(_span.SelectedIndex switch { 0 => 1, 1 => 6, _ => 24 });
        _timeline.SetData(_ledger.Minutes(now - _timeline.Span), now);
        _daily.SetData(_ledger.Daily(from, today, _networkKey));
        RefreshOutages();
    }

    private string DescribeNow(bool enabled)
    {
        _nowTitle.Text = T("当前", "Now");
        if (!enabled)
        {
            return T("未开启", "Off");
        }

        if (_probe.Last is not { } last)
        {
            return T("正在测量…", "Measuring…");
        }

        if (last.Network is not { } network)
        {
            return T("未连接（没有网关）", "Not connected (no gateway)");
        }

        _nowTitle.Text = T("当前 · ", "Now · ") + network.Label;
        var state = _monitor.State is { } s ? QualityLook.Name(s) : string.Empty;
        if (_monitor.Ongoing is { } outage)
        {
            state += " · " + NetPulseContext.FormatDuration(outage.Duration);
        }

        var text = state + "\n" + T("网关 ", "Gateway ") + $"{_probe.Gateway}: {NetPulseContext.FormatLatency(last.GatewayMs)}";
        if (last.ExternalProbed)
        {
            text += "\n" + T("外网 ", "Internet ") + $"{_settings.Quality.ExternalTarget}: {NetPulseContext.FormatLatency(last.ExternalMs)}";
        }

        var extras = new List<string>();
        if (last.DnsProbed)
        {
            extras.Add(last.DnsOk ? T("DNS 正常", "DNS OK") : T("DNS 无回应", "DNS no answer"));
        }

        if (last.SignalPercent is { } signal)
        {
            extras.Add(T($"信号 {signal}%", $"signal {signal}%"));
        }

        if (extras.Count > 0)
        {
            text += "\n" + string.Join(" · ", extras);
        }

        return text;
    }

    private void RefreshOutages()
    {
        var outages = _ledger.Outages(DateTime.MinValue, DateTime.MaxValue, _networkKey);
        var ongoing = _monitor.Ongoing is { } o && (_networkKey is null || o.NetworkKey == _networkKey) ? o : null;
        var signature = $"{_networkKey}|{outages.Count}|{(outages.Count > 0 ? outages[0].Start.Ticks : 0)}|{ongoing?.Duration.TotalSeconds:0}|{ongoing?.Kind}";
        if (signature == _outageSignature)
        {
            return;
        }

        _outageSignature = signature;
        _outages.BeginUpdate();
        _outages.Items.Clear();
        if (ongoing is not null)
        {
            _outages.Items.Add(Row(ongoing, T("（进行中）", " (ongoing)")));
        }

        foreach (var outage in outages)
        {
            _outages.Items.Add(Row(outage, string.Empty));
        }

        static ListViewItem Row(Outage outage, string suffix) => new(
        [
            $"{outage.Start:yyyy-MM-dd HH:mm:ss}",
            NetPulseContext.FormatDuration(outage.Duration) + suffix,
            QualityLook.Name(outage.Kind),
            outage.NetworkLabel,
        ]);

        if (outages.Count == 0 && ongoing is null)
        {
            _outages.Items.Add(new ListViewItem([T("没有断线记录", "No outages recorded"), string.Empty, string.Empty, string.Empty]) { ForeColor = SystemColors.GrayText });
        }

        _outages.EndUpdate();
    }

    private void LoadNetworks()
    {
        var known = _ledger.Networks;
        var current = _network.Items.Cast<NetworkItem>().Select(i => i.Key).ToList();
        if (current.SequenceEqual(new string?[] { null }.Concat(known.Select(n => n.Key))))
        {
            return;
        }

        _loadingNetworks = true;
        try
        {
            _network.Items.Clear();
            _network.Items.Add(new NetworkItem(null, T("全部网络", "All networks")));
            foreach (var network in known)
            {
                _network.Items.Add(new NetworkItem(network.Key, network.Label));
            }

            _networkKey = known.Any(n => n.Key == _networkKey) ? _networkKey : null;
            _network.SelectedItem = _network.Items.Cast<NetworkItem>().First(i => i.Key == _networkKey);
        }
        finally
        {
            _loadingNetworks = false;
        }
    }

    private void ExportCsv()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = T("CSV 文件 (*.csv)|*.csv", "CSV files (*.csv)|*.csv"),
            FileName = $"NetPulse-outages-{DateTime.Now:yyyyMMdd}.csv",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var csv = new StringBuilder("start,end,duration_seconds,kind,network\r\n");
        foreach (var outage in Enumerable.Reverse(_ledger.Outages(DateTime.MinValue, DateTime.MaxValue, _networkKey)))
        {
            csv.Append(CultureInfo.InvariantCulture,
                $"{outage.Start:yyyy-MM-dd HH:mm:ss},{outage.End:yyyy-MM-dd HH:mm:ss},{(long)outage.Duration.TotalSeconds},{outage.Kind.ToString().ToLowerInvariant()},{Csv(outage.NetworkLabel)}\r\n");
        }

        try
        {
            File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, T("导出失败：", "Export failed: ") + ex.Message, AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Quotes a field that contains a comma, quote or line break (SSIDs may).</summary>
    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

    private void Clear()
    {
        var answer = MessageBox.Show(
            this, T("确定要清空全部连接质量记录吗？此操作无法撤销。", "Clear all connection quality history? This cannot be undone."), AppPaths.AppName,
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes)
        {
            _monitor.Stop(); // an open outage would otherwise be written back into the empty ledger
            _ledger.Clear();
            _saveUsage();
            _outageSignature = null;
            RefreshData();
        }
    }

    private static Label TitleLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Top,
        AutoSize = false,
        Height = 22,
        Font = new Font(Control.DefaultFont, FontStyle.Bold),
    };

    private static Label ValueLabel() => new() { Dock = DockStyle.Fill, AutoSize = false };

    private static GroupBox Card(params Control[] children)
    {
        var box = new GroupBox { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 4) };
        foreach (var child in Enumerable.Reverse(children))
        {
            box.Controls.Add(child);
        }

        return box;
    }

    private sealed record NetworkItem(string? Key, string Label)
    {
        public override string ToString() => Label;
    }
}
