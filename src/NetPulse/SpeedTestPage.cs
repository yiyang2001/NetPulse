using NetPulse.Core;
using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>Runs <see cref="SpeedTestRunner"/> on demand. The only network access happens after T("开始测速", "Start test").</summary>
internal sealed class SpeedTestPage : UserControl
{
    private const int MaxHistory = 50;

    private readonly JsonFileStore<SpeedTestHistory> _store = new(AppPaths.SpeedTestFile, () => new SpeedTestHistory());
    private readonly SpeedTestHistory _history;
    private readonly Label _latency = Metric();
    private readonly Label _download = Metric();
    private readonly Label _upload = Metric();
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly ProgressBar _progress = new() { Width = 300, Height = 18, Maximum = 1000 };
    private readonly Button _start = new() { Text = T("开始测速", "Start test"), AutoSize = true };
    private readonly ListView _list = new FlickerFreeListView
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private CancellationTokenSource? _running;

    public SpeedTestPage()
    {
        Padding = new Padding(12);
        _history = _store.Load();

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

        var historyLabel = new Label { Text = T("历史记录（仅保存在本机）", "History (kept on this PC only)"), AutoSize = true, Font = new Font(DefaultFont, FontStyle.Bold), Padding = new Padding(0, 10, 0, 4) };
        var historyHeader = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        historyHeader.Controls.Add(historyLabel);

        Controls.Add(_list);
        Controls.Add(historyHeader);
        Controls.Add(metrics);
        Controls.Add(Ui.Toolbar(_start, _progress, _status));
        Controls.Add(Ui.Note(
            T($"测速会连接 Cloudflare 的公共测速服务器（{SpeedTestRunner.ServerHost}），只在点击“开始测速”后联网，", $"The test connects to Cloudflare's public speed test servers ({SpeedTestRunner.ServerHost}), only after you click “Start test”. ") +
            T("下载和上传各约 10 秒，会消耗几十到几百 MB 流量。按流量计费的网络请注意。", "Download and upload run about 10 seconds each and use tens to hundreds of MB; mind metered connections.")));

        _start.Click += async (_, _) => await StartOrStopAsync();
        ResetMetrics();
        ShowHistory();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
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

    private async Task StartOrStopAsync()
    {
        if (_running is not null)
        {
            await _running.CancelAsync();
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
            using var runner = new SpeedTestRunner();
            var result = await runner.RunAsync(progress, cts.Token);
            ShowResult(result);
            _history.Results.Insert(0, result);
            if (_history.Results.Count > MaxHistory)
            {
                _history.Results.RemoveRange(MaxHistory, _history.Results.Count - MaxHistory);
            }

            SaveHistory();
            ShowHistory();
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

    private void ShowHistory()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in _history.Results)
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

    private void SaveHistory()
    {
        try
        {
            _store.Save(_history);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex, "SaveSpeedTestHistory");
        }
    }
}
