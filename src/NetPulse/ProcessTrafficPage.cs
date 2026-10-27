using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using NetPulse.Core;
using NetPulse.Platform;
using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>
/// Owns the ETW session and books its counters into the per-program history. When elevated, tracing
/// starts with the app and runs until stopped or exit.
/// </summary>
internal sealed class ProcessTrafficService(ProcessUsageLedger history) : IDisposable
{
    private KernelNetworkTrace? _trace;
    private ProcessHistoryRecorder? _recorder;

    public ProcessTrafficTable Table { get; } = new();

    public ProcessNameCache Names { get; } = new();

    public ProcessUsageLedger History { get; } = history;

    public DateTime? StartedAt { get; private set; }

    public string? Error { get; private set; }

    public bool IsRunning => _trace?.IsRunning == true;

    public long EventCount => _trace?.EventCount ?? 0;

    public bool TryStart()
    {
        if (_trace is not null)
        {
            return true;
        }

        if (!Elevation.IsAdministrator)
        {
            Error = T("按进程统计流量需要管理员权限。", "Per-program traffic needs administrator rights.");
            return false;
        }

        try
        {
            _trace = KernelNetworkTrace.Start(Table);
            StartedAt = DateTime.Now;
            Error = null;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or Win32Exception or PlatformNotSupportedException)
        {
            ErrorLog.Write(ex, "KernelNetworkTrace.Start");
            Error = T("无法启动内核网络跟踪：", "Could not start kernel network tracing: ") + ex.Message;
            return false;
        }
    }

    /// <summary>Adds the traffic counted since the previous call to today's history.</summary>
    public void RecordHistory()
    {
        if (_trace is null)
        {
            return;
        }

        _recorder ??= new ProcessHistoryRecorder(History, pid => Names.Resolve(pid).Name);
        _recorder.Record(Table.Snapshot(), DateOnly.FromDateTime(DateTime.Now));
    }

    public void Stop()
    {
        RecordHistory();
        _trace?.Dispose();
        _trace = null;
    }

    /// <summary>Restarts the live totals; the history keeps everything counted so far.</summary>
    public void ResetCounters()
    {
        RecordHistory();
        Table.Clear();
        _recorder?.ResetBaseline();
        StartedAt = DateTime.Now;
    }

    public void Dispose() => Stop();
}

internal sealed class ProcessTrafficPage : UserControl, IActivatablePage
{
    private enum Range
    {
        Live,
        Today,
        Cycle,
        Last30Days,
    }

    private readonly ProcessTrafficService _service;
    private readonly AppSettings _settings;
    private readonly Func<bool> _restartElevated;
    private readonly ProcessRateSampler _sampler = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private IReadOnlyList<ProcessRate>? _lastRates;
    private Range? _columnsFor;
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly Button _elevate = new() { Text = T("以管理员身份重启 NetPulse", "Restart NetPulse as administrator"), AutoSize = true };
    private readonly Button _toggle = new() { AutoSize = true };
    private readonly Button _reset = new() { Text = T("清零", "Reset"), AutoSize = true };
    private readonly Button _export = new() { Text = T("导出 CSV…", "Export CSV…"), AutoSize = true };
    private readonly ComboBox _range = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly CheckBox _groupByName = new() { Text = T("按程序合并", "Group by program"), Checked = true, AutoSize = true };
    private readonly CheckBox _hideIdle = new() { Text = T("隐藏无流量的程序", "Hide idle programs"), Checked = true, AutoSize = true };
    private readonly ListView _list = new FlickerFreeListView
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };

    public ProcessTrafficPage(ProcessTrafficService service, AppSettings settings, Func<bool> restartElevated)
    {
        _service = service;
        _settings = settings;
        _restartElevated = restartElevated;
        Padding = new Padding(12);

        _range.Items.AddRange([T("实时（本次记录）", "Live (this session)"), T("今天", "Today"), T("本周期", "This cycle"), T("最近 30 天", "Last 30 days")]);
        _range.SelectedIndex = 0;
        _range.SelectedIndexChanged += (_, _) => RefreshView();

        _elevate.Click += (_, _) =>
        {
            if (!_restartElevated())
            {
                _status.Text = T("已取消。需要在 Windows 的权限提示中选择“是”。", "Cancelled. Choose “Yes” in the Windows permission prompt.");
            }
        };
        _toggle.Click += (_, _) =>
        {
            if (_service.IsRunning)
            {
                _service.Stop();
            }
            else
            {
                _service.TryStart();
            }

            RefreshView();
        };
        _reset.Click += (_, _) =>
        {
            _service.ResetCounters();
            RefreshView();
        };
        _export.Click += (_, _) => ExportCsv();
        _groupByName.CheckedChanged += (_, _) => RefreshView();
        _hideIdle.CheckedChanged += (_, _) => RefreshView();
        _timer.Tick += (_, _) => RefreshView(sample: true);

        Controls.Add(_list);
        Controls.Add(Ui.Toolbar(new Label { Text = T("范围", "Range") }, _range, _toggle, _reset, _groupByName, _hideIdle, _export));
        Controls.Add(Ui.Toolbar(_status, _elevate));
        Controls.Add(Ui.Note(
            T("数据来自 Windows 内核网络事件（ETW），统计各程序的 TCP/UDP 收发字节数。", "Data comes from Windows kernel network events (ETW): bytes sent and received over TCP/UDP by each program. ") +
            T("这是 Windows 对管理员开放的功能，因此需要以管理员身份运行；以管理员身份运行时会自动记录，并按天保存每个程序的流量。", "Windows only offers this to administrators, so NetPulse must run as administrator; it then records automatically and keeps daily totals per program.")));
    }

    private Range SelectedRange => (Range)Math.Max(0, _range.SelectedIndex);

    public void OnActivated()
    {
        _service.TryStart();
        RefreshView();
        _timer.Start();
    }

    public void OnDeactivated() => _timer.Stop();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <param name="sample">Take a new rate sample; option changes only re-render the last one.</param>
    private void RefreshView(bool sample = false)
    {
        var range = SelectedRange;
        var live = range == Range.Live;
        _elevate.Visible = !Elevation.IsAdministrator;
        _toggle.Visible = Elevation.IsAdministrator;
        _reset.Visible = Elevation.IsAdministrator && live;
        _groupByName.Visible = _hideIdle.Visible = live;
        _export.Visible = !live;
        _toggle.Text = _service.IsRunning ? T("停止记录", "Stop recording") : T("开始记录", "Start recording");
        EnsureColumns(range);

        if (live)
        {
            RefreshLive(sample);
        }
        else
        {
            RefreshHistory(range);
        }
    }

    private void RefreshLive(bool sample)
    {
        if (!_service.IsRunning)
        {
            _status.Text = _service.Error ?? T("记录已停止。", "Recording stopped.");
            _list.Items.Clear();
            _lastRates = null;
            return;
        }

        if (sample || _lastRates is null)
        {
            _lastRates = _sampler.Sample(_service.Table.Snapshot(), _clock.Elapsed);
        }

        var rates = _lastRates;
        _status.Text = T($"正在记录（自 {_service.StartedAt:HH:mm:ss} 起）", $"Recording since {_service.StartedAt:HH:mm:ss}")
            + (_service.EventCount == 0 ? T("，暂未收到网络事件", ", no network events yet") : string.Empty);

        var rows = _groupByName.Checked
            ? rates.GroupBy(r => ProcessName(r.ProcessId))
                .Select(g => (Name: g.Key, Id: T($"{g.Count()} 个", $"{g.Count()}"), Down: g.Sum(r => r.DownloadBytesPerSecond), Up: g.Sum(r => r.UploadBytesPerSecond),
                    TotalDown: g.Sum(r => r.TotalDownloaded), TotalUp: g.Sum(r => r.TotalUploaded)))
            : rates.Select(r => (Name: ProcessName(r.ProcessId), Id: r.ProcessId.ToString(), Down: r.DownloadBytesPerSecond, Up: r.UploadBytesPerSecond,
                TotalDown: r.TotalDownloaded, TotalUp: r.TotalUploaded));

        var ordered = rows
            .Where(r => !_hideIdle.Checked || r.Down + r.Up > 0)
            .OrderByDescending(r => r.Down + r.Up)
            .ThenByDescending(r => r.TotalDown + r.TotalUp)
            .ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var row in ordered)
        {
            _list.Items.Add(new ListViewItem(
            [
                row.Name,
                row.Id,
                ByteFormatter.FormatSpeed(row.Down, SpeedUnit.Bytes),
                ByteFormatter.FormatSpeed(row.Up, SpeedUnit.Bytes),
                ByteFormatter.FormatSize(row.TotalDown),
                ByteFormatter.FormatSize(row.TotalUp),
            ]));
        }

        _list.EndUpdate();
    }

    private void RefreshHistory(Range range)
    {
        // Book what was counted since the last housekeeping tick, so the list is current to the second.
        _service.RecordHistory();
        var (from, to) = DatesFor(range);
        var apps = _service.History.Totals(from, to);
        var recording = _service.IsRunning ? T("正在记录", "recording") : Elevation.IsAdministrator ? T("记录已停止", "recording stopped") : T("当前未在记录（需要管理员权限）", "not recording (needs administrator rights)");
        _status.Text = T($"{from:yyyy-MM-dd} – {to:yyyy-MM-dd}，{apps.Count} 个程序，", $"{from:yyyy-MM-dd} – {to:yyyy-MM-dd}: {apps.Count} programs, ") +
            T($"合计 {ByteFormatter.FormatSize(apps.Sum(a => a.Total))}。{recording}。", $"{ByteFormatter.FormatSize(apps.Sum(a => a.Total))} in total; {recording}.");

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var app in apps)
        {
            _list.Items.Add(new ListViewItem(
            [
                app.Name,
                ByteFormatter.FormatSize(app.Downloaded),
                ByteFormatter.FormatSize(app.Uploaded),
                ByteFormatter.FormatSize(app.Total),
            ]));
        }

        _list.EndUpdate();
    }

    private (DateOnly From, DateOnly To) DatesFor(Range range)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return range switch
        {
            Range.Cycle => (BillingCycle.Containing(today, _settings.Plan.CycleStartDay).Start, today),
            Range.Last30Days => (today.AddDays(-29), today),
            _ => (today, today),
        };
    }

    private void EnsureColumns(Range range)
    {
        var live = range == Range.Live;
        if (_columnsFor is { } current && (current == Range.Live) == live)
        {
            return;
        }

        _columnsFor = range;
        _list.Items.Clear();
        _list.Columns.Clear();
        _list.Columns.Add(T("程序", "Program"), 220);
        if (live)
        {
            _list.Columns.Add(T("PID / 进程数", "PID / count"), 100, HorizontalAlignment.Right);
            _list.Columns.Add(T("下载速度", "Download"), 100, HorizontalAlignment.Right);
            _list.Columns.Add(T("上传速度", "Upload"), 100, HorizontalAlignment.Right);
            _list.Columns.Add(T("累计下载", "Downloaded"), 100, HorizontalAlignment.Right);
            _list.Columns.Add(T("累计上传", "Uploaded"), 100, HorizontalAlignment.Right);
        }
        else
        {
            _list.Columns.Add(T("下载", "Downloaded"), 120, HorizontalAlignment.Right);
            _list.Columns.Add(T("上传", "Uploaded"), 120, HorizontalAlignment.Right);
            _list.Columns.Add(T("合计", "Total"), 120, HorizontalAlignment.Right);
        }
    }

    private void ExportCsv()
    {
        var (from, to) = DatesFor(SelectedRange);
        using var dialog = new SaveFileDialog
        {
            Filter = T("CSV 文件 (*.csv)|*.csv", "CSV files (*.csv)|*.csv"),
            FileName = $"NetPulse-programs-{from:yyyyMMdd}-{to:yyyyMMdd}.csv",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _service.RecordHistory();
        var csv = new StringBuilder("program,downloaded_bytes,uploaded_bytes,total_bytes\r\n");
        foreach (var app in _service.History.Totals(from, to))
        {
            csv.Append(CultureInfo.InvariantCulture, $"\"{app.Name.Replace("\"", "\"\"")}\",{app.Downloaded},{app.Uploaded},{app.Total}\r\n");
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

    private string ProcessName(int pid)
    {
        var (name, exited) = _service.Names.Resolve(pid);
        return exited ? name + T("（已退出）", " (exited)") : name;
    }
}

/// <summary>
/// PID → program name, cached; looked up again after 30 s so reused PIDs eventually show the right name.
/// A process that has exited keeps its last known name.
/// </summary>
internal sealed class ProcessNameCache
{
    private static string UnknownName => T("（未知进程）", "(unknown process)");
    private readonly Dictionary<int, (string Name, bool Exited, long CheckedAt)> _names = [];

    public (string Name, bool Exited) Resolve(int pid)
    {
        switch (pid)
        {
            case 0:
                return ("System Idle", false);
            case 4:
                return ("System", false);
        }

        var now = Environment.TickCount64;
        var known = _names.TryGetValue(pid, out var cached);
        if (known && now - cached.CheckedAt < 30_000)
        {
            return (cached.Name, cached.Exited);
        }

        if (!known && _names.Count >= 4096)
        {
            // Long sessions see many short-lived PIDs; forget the ones that are gone.
            foreach (var gone in _names.Where(p => p.Value.Exited).Select(p => p.Key).ToList())
            {
                _names.Remove(gone);
            }
        }

        (string Name, bool Exited) result;
        try
        {
            using var process = Process.GetProcessById(pid);
            result = (process.ProcessName, false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            result = (known ? cached.Name : UnknownName, true);
        }

        _names[pid] = (result.Name, result.Exited, now);
        return result;
    }
}
