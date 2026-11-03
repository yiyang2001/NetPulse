using NetPulse.Core;
using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>
/// Runs <see cref="SpeedTestRunner"/> on demand and shows the history. The only network access happens after
/// "Start test", or on the schedule if the user turned it on.
/// </summary>
internal sealed class SpeedTestPage : UserControl, IActivatablePage
{
    private readonly SpeedTestService _service;
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly Label _latency = Metric();
    private readonly Label _download = Metric();
    private readonly Label _upload = Metric();
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly ProgressBar _progress = new() { Width = 300, Height = 18, Maximum = 1000 };
    private readonly Button _start = new() { Text = T("开始测速", "Start test"), AutoSize = true };
    private readonly SpeedHistoryChart _chart = new() { Dock = DockStyle.Top, Height = 150 };
    private readonly CheckBox _scheduleEnabled = new() { Text = T("自动测速，每隔", "Test automatically every"), AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly NumericUpDown _scheduleHours = new() { Minimum = 1, Maximum = 24 * 7, Width = 60 };
    private readonly Label _scheduleNote = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 6, 0, 0) };
    private readonly ListView _list = new FlickerFreeListView
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private CancellationTokenSource? _running;
    private bool _loading;

    public SpeedTestPage(SpeedTestService service, AppSettings settings, Action saveSettings)
    {
        _service = service;
        _settings = settings;
        _saveSettings = saveSettings;
        Padding = new Padding(12);

        _list.Columns.Add(T("时间", "Time"), 150);
        _list.Columns.Add(T("服务器", "Server"), 70);
        _list.Columns.Add(T("延迟", "Latency"), 80, HorizontalAlignment.Right);
        _list.Columns.Add(T("抖动", "Jitter"), 80, HorizontalAlignment.Right);
        _list.Columns.Add(T("下载", "Download"), 110, HorizontalAlignment.Right);
        _list.Columns.Add(T("上传", "Upload"), 110, HorizontalAlignment.Right);

        var metrics = new TableLayoutPanel { Dock = DockStyle.Top, Height = 96, ColumnCount = 3 };
        for (var i = 0; i < 3; i++)
        {
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        }

        metrics.Controls.Add(MetricBox(T("延迟 / 抖动", "Latency / jitter"), _latency), 0, 0);
        metrics.Controls.Add(MetricBox(T("下载", "Download"), _download), 1, 0);
        metrics.Controls.Add(MetricBox(T("上传", "Upload"), _upload), 2, 0);

        var historyLabel = new Label { Text = T("历史记录（仅保存在本机，最多 200 次）", "History (kept on this PC only, up to 200 tests)"), AutoSize = true, Font = new Font(DefaultFont, FontStyle.Bold), Padding = new Padding(0, 10, 0, 4) };
        var historyHeader = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        historyHeader.Controls.Add(historyLabel);

        // Dock order: the last control added is laid out first.
        Controls.Add(_list);
        Controls.Add(_chart);
        Controls.Add(historyHeader);
        Controls.Add(Ui.Toolbar(_scheduleEnabled, _scheduleHours, new Label { Text = T("小时", "hours") }, _scheduleNote));
        Controls.Add(metrics);
        Controls.Add(Ui.Toolbar(_start, _progress, _status));
        Controls.Add(Ui.Note(
            T($"测速会连接 Cloudflare 的公共测速服务器（{SpeedTestRunner.ServerHost}），只在点击“开始测速”后联网（或在下面开启自动测速之后），", $"The test connects to Cloudflare's public speed test servers ({SpeedTestRunner.ServerHost}), only after you click “Start test” (or after you turn on automatic tests below). ") +
            T("下载和上传各约 10 秒，会消耗几十到几百 MB 流量。按流量计费的网络请注意。", "Download and upload run about 10 seconds each and use tens to hundreds of MB; mind metered connections.")));

        _start.Click += async (_, _) => await StartOrStopAsync();
        _scheduleEnabled.CheckedChanged += (_, _) => OnScheduleChanged();
        _scheduleHours.ValueChanged += (_, _) => OnScheduleChanged();
        _service.ResultAdded += OnResultAdded;
        ResetMetrics();
        LoadSchedule();
        ShowHistory();
    }

    public void OnActivated()
    {
        LoadSchedule();
        ShowHistory();
    }

    public void OnDeactivated()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _service.ResultAdded -= OnResultAdded;
            _running?.Cancel();
        }

        base.Dispose(disposing);
    }

    private static Label Metric() => new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font(DefaultFont.FontFamily, 15f, FontStyle.Bold),
    };

    private static GroupBox MetricBox(string title, Label value)
    {
        var box = new GroupBox { Text = title, Dock = DockStyle.Fill };
        box.Controls.Add(value);
        return box;
    }

    private static string Mbps(double bytesPerSecond) => ByteFormatter.FormatSpeed(bytesPerSecond, SpeedUnit.Bits);

    private static string Latency(LatencyStats? stats) =>
        stats is { } s ? $"{s.MedianMs:0} ms / {s.JitterMs:0.0} ms" : "—";

    private void ResetMetrics()
    {
        _latency.Text = _download.Text = _upload.Text = "—";
        _progress.Value = 0;
    }

    private void LoadSchedule()
    {
        _loading = true;
        try
        {
            _scheduleEnabled.Checked = _settings.SpeedTestSchedule.Enabled;
            _scheduleHours.Value = _settings.SpeedTestSchedule.IntervalHours;
            _scheduleHours.Enabled = _settings.SpeedTestSchedule.Enabled;
            _scheduleNote.Text = _settings.SpeedTestSchedule is { Enabled: true, LastRun: { } last }
                ? T($"上次：{last:M/d HH:mm}；全屏应用运行时会推迟", $"Last: {last:M/d HH:mm}; postponed while a full-screen app runs")
                : string.Empty;
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnScheduleChanged()
    {
        if (_loading)
        {
            return;
        }

        var schedule = _settings.SpeedTestSchedule;
        if (_scheduleEnabled.Checked && !schedule.Enabled)
        {
            var answer = MessageBox.Show(
                this,
                T($"自动测速会每隔 {(int)_scheduleHours.Value} 小时连接 Cloudflare 测速一次，每次消耗几十到几百 MB 流量，并且立即开始第一次。\n按流量计费的网络请不要开启。要开启吗？", $"Automatic tests contact Cloudflare every {(int)_scheduleHours.Value} hours, use tens to hundreds of MB each time, and the first one starts right away.\nDo not turn this on for a metered connection. Turn it on?"),
                AppPaths.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                LoadSchedule();
                return;
            }

            schedule.LastRun = null; // due now
        }

        schedule.Enabled = _scheduleEnabled.Checked;
        schedule.IntervalHours = (int)_scheduleHours.Value;
        schedule.Normalize();
        _scheduleHours.Enabled = schedule.Enabled;
        _saveSettings();
    }

    private async Task StartOrStopAsync()
    {
        if (_running is not null)
        {
            await _running.CancelAsync();
            return;
        }

        if (_service.IsBusy)
        {
            _status.Text = T("正在进行自动测速，请稍后再试。", "An automatic test is running; try again in a minute.");
            return;
        }

        ResetMetrics();
        _start.Text = T("停止", "Stop");
        _status.Text = T("正在连接…", "Connecting…");
        using var cts = new CancellationTokenSource();
        _running = cts;
        var progress = new Progress<SpeedTestProgress>(OnProgress);
        try
        {
            var result = await _service.RunAsync(progress, cts.Token);
            ShowResult(result);
            _status.Text = string.IsNullOrEmpty(result.Server) ? T("完成。", "Done.") : T($"完成（服务器：Cloudflare {result.Server}）。", $"Done (server: Cloudflare {result.Server}).");
        }
        catch (OperationCanceledException)
        {
            _status.Text = T("已停止。", "Stopped.");
        }
        catch (HttpRequestException ex)
        {
            _status.Text = T("测速失败：", "Speed test failed: ") + ex.Message;
        }
        finally
        {
            _running = null;
            _start.Text = T("开始测速", "Start test");
        }
    }

    private void OnProgress(SpeedTestProgress p)
    {
        // Latency is the first third of the bar, download and upload the others.
        var offset = p.Phase switch { SpeedTestPhase.Latency => 0, SpeedTestPhase.Download => 1, SpeedTestPhase.Upload => 2, _ => 3 };
        _progress.Value = (int)Math.Clamp((offset + p.Fraction) / 3 * 1000, 0, 1000);
        _latency.Text = Latency(p.Latency);
        switch (p.Phase)
        {
            case SpeedTestPhase.Latency:
                _status.Text = T("正在测量延迟…", "Measuring latency…");
                break;
            case SpeedTestPhase.Download:
                _status.Text = T("正在测试下载…", "Testing download…");
                _download.Text = Mbps(p.CurrentBytesPerSecond);
                break;
            case SpeedTestPhase.Upload:
                _status.Text = T("正在测试上传…", "Testing upload…");
                _upload.Text = Mbps(p.CurrentBytesPerSecond);
                break;
        }
    }

    private void ShowResult(SpeedTestResult result)
    {
        _latency.Text = Latency(result.Latency);
        _download.Text = Mbps(result.DownloadBytesPerSecond);
        _upload.Text = Mbps(result.UploadBytesPerSecond);
        _progress.Value = _progress.Maximum;
    }

    /// <summary>A test (ours or a scheduled one) finished.</summary>
    private void OnResultAdded(object? sender, EventArgs e)
    {
        ShowHistory();
        LoadSchedule();
    }

    private void ShowHistory()
    {
        var results = _service.History.Results;
        _chart.SetData(results);
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in results)
        {
            _list.Items.Add(new ListViewItem(
            [
                r.StartedAt.ToString("yyyy-MM-dd HH:mm"),
                r.Server,
                r.Latency is { } l ? $"{l.MedianMs:0} ms" : "—",
                r.Latency is { } j ? $"{j.JitterMs:0.0} ms" : "—",
                Mbps(r.DownloadBytesPerSecond),
                Mbps(r.UploadBytesPerSecond),
            ]));
        }

        _list.EndUpdate();
    }
}
