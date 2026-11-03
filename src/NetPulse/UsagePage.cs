using System.Globalization;
using System.Text;
using NetPulse.Core;
using static NetPulse.Core.Lang;

namespace NetPulse;

internal sealed class UsagePage : UserControl, IActivatablePage
{
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 2000 };
    private const int ChartDays = 30;
    private static readonly string[] WeekdayNames = [T("周日", "Sun"), T("周一", "Mon"), T("周二", "Tue"), T("周三", "Wed"), T("周四", "Thu"), T("周五", "Fri"), T("周六", "Sat")];

    private readonly UsageLedger _allLedger;
    private readonly NetworkUsageLedger _networks;
    private readonly AppSettings _settings;
    private readonly Action _saveUsage;
    private readonly Action _saveSettings;
    private readonly ComboBox _network = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly Button _networkPlanButton = new() { Text = T("此网络的套餐…", "Plan for this network…"), AutoSize = true };
    private readonly ComboBox _range = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private string? _networkKey; // null: all networks
    private bool _loadingNetworks;
    private readonly Label _todayValue = ValueLabel();
    private readonly Label _cycleTitle = TitleLabel(T("本周期", "This cycle"));
    private readonly Label _cycleValue = ValueLabel();
    private readonly Label _planValue = ValueLabel();
    private readonly ProgressBar _planBar = new() { Dock = DockStyle.Top, Height = 14, Maximum = 1000 };
    private readonly UsageChart _chart = new() { Dock = DockStyle.Fill };
    private readonly ListView _list = new FlickerFreeListView
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };

    public UsagePage(UsageLedger ledger, NetworkUsageLedger networks, AppSettings settings, Action saveUsage, Action saveSettings)
    {
        _allLedger = ledger;
        _networks = networks;
        _settings = settings;
        _saveUsage = saveUsage;
        _saveSettings = saveSettings;
        Padding = new Padding(12);

        _list.Columns.Add(T("日期", "Date"), 150);
        _list.Columns.Add(T("下载", "Download"), 120, HorizontalAlignment.Right);
        _list.Columns.Add(T("上传", "Upload"), 120, HorizontalAlignment.Right);
        _list.Columns.Add(T("合计", "Total"), 120, HorizontalAlignment.Right);

        var cards = new TableLayoutPanel { Dock = DockStyle.Top, Height = 92, ColumnCount = 3, RowCount = 1 };
        for (var i = 0; i < 3; i++)
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        }

        cards.Controls.Add(Card(TitleLabel(T("今天", "Today")), _todayValue), 0, 0);
        cards.Controls.Add(Card(_cycleTitle, _cycleValue), 1, 0);
        cards.Controls.Add(Card(TitleLabel(T("流量套餐", "Data plan")), _planValue, _planBar), 2, 0);

        var chartPanel = new Panel { Dock = DockStyle.Top, Height = 170, Padding = new Padding(0, 8, 0, 8) };
        chartPanel.Controls.Add(_chart);

        var exportButton = new Button { Text = T("导出 CSV…", "Export CSV…"), AutoSize = true };
        exportButton.Click += (_, _) => ExportCsv();
        var resetButton = new Button { Text = T("清空统计…", "Clear statistics…"), AutoSize = true };
        resetButton.Click += (_, _) => ResetStatistics();
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(resetButton);
        buttons.Controls.Add(exportButton);
        buttons.Controls.Add(_networkPlanButton);
        _networkPlanButton.Click += (_, _) => EditNetworkPlan();
        _network.SelectedIndexChanged += (_, _) =>
        {
            if (!_loadingNetworks)
            {
                _networkKey = (_network.SelectedItem as NetworkItem)?.Key;
                RefreshData();
            }
        };
        _range.Items.AddRange([T("最近 7 天", "Last 7 days"), T("最近 30 天", "Last 30 days"), T("最近 12 个月", "Last 12 months")]);
        _range.SelectedIndex = 1;
        _range.SelectedIndexChanged += (_, _) => RefreshData();
        var networkBar = Ui.Toolbar(new Label { Text = T("网络", "Network") }, _network, new Label { Text = T("范围", "Range") }, _range);

        // Dock order: last added is laid out first.
        Controls.Add(_list);
        Controls.Add(chartPanel);
        Controls.Add(cards);
        Controls.Add(networkBar);
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

    /// <summary>The totals of all networks, or of the one picked in the selector.</summary>
    private UsageLedger Ledger => _networkKey is null ? _allLedger : _networks.Get(_networkKey) ?? new UsageLedger();

    private DataPlanSettings ActivePlan =>
        _networkKey is null ? _settings.Plan
        : _settings.NetworkPlans.TryGetValue(_networkKey, out var plan) ? plan : new DataPlanSettings();

    private void LoadNetworks()
    {
        var known = _networks.Networks;
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

            // A selected network that no longer has data (pruned or cleared) falls back to "all".
            _networkKey = known.Any(n => n.Key == _networkKey) ? _networkKey : null;
            _network.SelectedItem = _network.Items.Cast<NetworkItem>().First(i => i.Key == _networkKey);
        }
        finally
        {
            _loadingNetworks = false;
        }
    }

    public void RefreshData()
    {
        LoadNetworks();
        _networkPlanButton.Visible = _networkKey is not null;
        var ledger = Ledger;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var todayUsage = ledger.Get(today);
        _todayValue.Text = Describe(todayUsage);

        var plan = ActivePlan;
        var (cycle, status) = DataPlanCalculator.Evaluate(ledger, plan, today);
        _cycleTitle.Text = T($"本周期（{cycle.Start:M/d} – {cycle.End:M/d}）", $"This cycle ({cycle.Start:M/d} – {cycle.End:M/d})");
        _cycleValue.Text = Describe(ledger.Sum(cycle.Start, cycle.End));

        if (plan.Enabled)
        {
            _planValue.Text =
                T($"已用 {ByteFormatter.FormatSize(status.UsedBytes)} / {ByteFormatter.FormatSize(status.LimitBytes)}", $"Used {ByteFormatter.FormatSize(status.UsedBytes)} / {ByteFormatter.FormatSize(status.LimitBytes)}") +
                T($"（{status.Fraction:P0}）\n剩余 {ByteFormatter.FormatSize(status.RemainingBytes)}，{cycle.DaysRemaining(today)} 天后重置", $" ({status.Fraction:P0})\n{ByteFormatter.FormatSize(status.RemainingBytes)} left, resets in {cycle.DaysRemaining(today)} days");
            _planBar.Value = (int)Math.Clamp(status.Fraction * 1000, 0, 1000);
            _planBar.Visible = true;
        }
        else
        {
            _planValue.Text = _networkKey is null
                ? T("未启用（可在“设置”中开启）", "Off (turn it on in Settings)")
                : T("此网络没有单独的套餐", "No separate plan for this network");
            _planBar.Visible = false;
        }

        var monthly = _range.SelectedIndex == 2;
        var days = monthly
            ? ledger.Months(12, today)
            : ledger.Range(today.AddDays(-((_range.SelectedIndex == 0 ? 7 : ChartDays) - 1)), today);
        _chart.SetData(days, monthly);
        _list.Columns[0].Text = monthly ? T("月份", "Month") : T("日期", "Date");

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var day in Enumerable.Reverse(days))
        {
            _list.Items.Add(new ListViewItem(
            [
                monthly ? $"{day.Date:yyyy-MM}" : $"{day.Date:yyyy-MM-dd} {WeekdayNames[(int)day.Date.DayOfWeek]}",
                ByteFormatter.FormatSize(day.Downloaded),
                ByteFormatter.FormatSize(day.Uploaded),
                ByteFormatter.FormatSize(day.Total),
            ]));
        }

        _list.EndUpdate();
    }

    private static string Describe(DailyUsage usage) =>
        T($"↓ {ByteFormatter.FormatSize(usage.Downloaded)}   ↑ {ByteFormatter.FormatSize(usage.Uploaded)}\n合计 {ByteFormatter.FormatSize(usage.Total)}", $"↓ {ByteFormatter.FormatSize(usage.Downloaded)}   ↑ {ByteFormatter.FormatSize(usage.Uploaded)}\nTotal {ByteFormatter.FormatSize(usage.Total)}");

    private static Label TitleLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Top,
        AutoSize = false,
        Height = 22,
        Font = new Font(Control.DefaultFont, FontStyle.Bold),
    };

    private static Label ValueLabel() => new() { Dock = DockStyle.Top, AutoSize = false, Height = 40 };

    private static GroupBox Card(params Control[] children)
    {
        var box = new GroupBox { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 4) };
        // Top-docked controls stack in reverse order of addition.
        foreach (var child in Enumerable.Reverse(children))
        {
            box.Controls.Add(child);
        }

        return box;
    }

    private void ExportCsv()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = T("CSV 文件 (*.csv)|*.csv", "CSV files (*.csv)|*.csv"),
            FileName = $"NetPulse-usage-{DateTime.Now:yyyyMMdd}.csv",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var csv = new StringBuilder("date,downloaded_bytes,uploaded_bytes,total_bytes\r\n");
        foreach (var day in Ledger.ToList())
        {
            csv.Append(CultureInfo.InvariantCulture, $"{day.Date:yyyy-MM-dd},{day.Downloaded},{day.Uploaded},{day.Total}\r\n");
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

    private void ResetStatistics()
    {
        var answer = MessageBox.Show(
            this, T("确定要清空全部流量统计吗？此操作无法撤销。", "Clear all usage statistics? This cannot be undone."), AppPaths.AppName,
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes)
        {
            _allLedger.Clear();
            _networks.Clear();
            _saveUsage();
            RefreshData();
        }
    }

    private void EditNetworkPlan()
    {
        if (_networkKey is not { } key)
        {
            return;
        }

        var plan = _settings.NetworkPlans.TryGetValue(key, out var existing) ? existing : new DataPlanSettings { Enabled = true };
        var label = (_network.SelectedItem as NetworkItem)?.Label ?? key;
        if (!PlanDialog.Edit(this, label, plan))
        {
            return;
        }

        // A changed limit or cycle should alert again.
        plan.AlertedCycleStart = null;
        plan.AlertedLevel = PlanAlertLevel.None;
        _settings.NetworkPlans[key] = plan;
        _saveSettings();
        RefreshData();
    }

    private sealed record NetworkItem(string? Key, string Label)
    {
        public override string ToString() => Label;
    }
}
