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

    private readonly UsageLedger _ledger;
    private readonly AppSettings _settings;
    private readonly Action _saveUsage;
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

    public UsagePage(UsageLedger ledger, AppSettings settings, Action saveUsage)
    {
        _ledger = ledger;
        _settings = settings;
        _saveUsage = saveUsage;
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

        // Dock order: last added is laid out first.
        Controls.Add(_list);
        Controls.Add(chartPanel);
        Controls.Add(cards);
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

    public void RefreshData()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var todayUsage = _ledger.Get(today);
        _todayValue.Text = Describe(todayUsage);

        var plan = _settings.Plan;
        var (cycle, status) = DataPlanCalculator.Evaluate(_ledger, plan, today);
        _cycleTitle.Text = T($"本周期（{cycle.Start:M/d} – {cycle.End:M/d}）", $"This cycle ({cycle.Start:M/d} – {cycle.End:M/d})");
        _cycleValue.Text = Describe(_ledger.Sum(cycle.Start, cycle.End));

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
            _planValue.Text = T("未启用（可在“设置”中开启）", "Off (turn it on in Settings)");
            _planBar.Visible = false;
        }

        var days = _ledger.Range(today.AddDays(-(ChartDays - 1)), today);
        _chart.SetData(days);

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var day in Enumerable.Reverse(days))
        {
            _list.Items.Add(new ListViewItem(
            [
                $"{day.Date:yyyy-MM-dd} {WeekdayNames[(int)day.Date.DayOfWeek]}",
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
        foreach (var day in _ledger.ToList())
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
            _ledger.Clear();
            _saveUsage();
            RefreshData();
        }
    }
}
