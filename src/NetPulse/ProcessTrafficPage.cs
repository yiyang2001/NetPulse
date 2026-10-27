using System.ComponentModel;
using System.Diagnostics;
using NetPulse.Core;
using NetPulse.Platform;

namespace NetPulse;

/// <summary>Owns the ETW session. Tracing starts the first time the page is opened and runs until stopped or exit.</summary>
internal sealed class ProcessTrafficService : IDisposable
{
    private KernelNetworkTrace? _trace;

    public ProcessTrafficTable Table { get; } = new();

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
            Error = "按进程统计流量需要管理员权限。";
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
            Error = "无法启动内核网络跟踪：" + ex.Message;
            return false;
        }
    }

    public void Stop()
    {
        _trace?.Dispose();
        _trace = null;
    }

    public void ResetCounters()
    {
        Table.Clear();
        StartedAt = DateTime.Now;
    }

    public void Dispose() => Stop();
}

internal sealed class ProcessTrafficPage : UserControl, IActivatablePage
{
    private readonly ProcessTrafficService _service;
    private readonly Func<bool> _restartElevated;
    private readonly ProcessRateSampler _sampler = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<int, (string Name, long CheckedAt)> _names = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private IReadOnlyList<ProcessRate>? _lastRates;
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly Button _elevate = new() { Text = "以管理员身份重启 NetPulse", AutoSize = true };
    private readonly Button _toggle = new() { AutoSize = true };
    private readonly Button _reset = new() { Text = "清零", AutoSize = true };
    private readonly CheckBox _groupByName = new() { Text = "按程序合并", Checked = true, AutoSize = true };
    private readonly CheckBox _hideIdle = new() { Text = "隐藏无流量的程序", Checked = true, AutoSize = true };
    private readonly ListView _list = new FlickerFreeListView
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };

    public ProcessTrafficPage(ProcessTrafficService service, Func<bool> restartElevated)
    {
        _service = service;
        _restartElevated = restartElevated;
        Padding = new Padding(12);

        _list.Columns.Add("程序", 220);
        _list.Columns.Add("PID / 进程数", 100, HorizontalAlignment.Right);
        _list.Columns.Add("下载速度", 100, HorizontalAlignment.Right);
        _list.Columns.Add("上传速度", 100, HorizontalAlignment.Right);
        _list.Columns.Add("累计下载", 100, HorizontalAlignment.Right);
        _list.Columns.Add("累计上传", 100, HorizontalAlignment.Right);

        _elevate.Click += (_, _) =>
        {
            if (!_restartElevated())
            {
                _status.Text = "已取消。需要在 Windows 的权限提示中选择“是”。";
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
        _groupByName.CheckedChanged += (_, _) => RefreshView();
        _hideIdle.CheckedChanged += (_, _) => RefreshView();
        _timer.Tick += (_, _) => RefreshView(sample: true);

        Controls.Add(_list);
        Controls.Add(Ui.Toolbar(_toggle, _reset, _groupByName, _hideIdle));
        Controls.Add(Ui.Toolbar(_status, _elevate));
        Controls.Add(Ui.Note(
            "数据来自 Windows 内核网络事件（ETW），统计各程序的 TCP/UDP 收发字节数，只包含开始记录之后的流量。" +
            "这是 Windows 对管理员开放的功能，因此需要以管理员身份运行。"));
    }

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
        _elevate.Visible = !Elevation.IsAdministrator;
        _toggle.Visible = _reset.Visible = Elevation.IsAdministrator;
        _toggle.Text = _service.IsRunning ? "停止记录" : "开始记录";

        if (!_service.IsRunning)
        {
            _status.Text = _service.Error ?? "记录已停止。";
            _list.Items.Clear();
            _lastRates = null;
            return;
        }

        if (sample || _lastRates is null)
        {
            _lastRates = _sampler.Sample(_service.Table.Snapshot(), _clock.Elapsed);
        }

        var rates = _lastRates;
        _status.Text = $"正在记录（自 {_service.StartedAt:HH:mm:ss} 起）"
            + (_service.EventCount == 0 ? "，暂未收到网络事件" : string.Empty);

        var rows = _groupByName.Checked
            ? rates.GroupBy(r => ProcessName(r.ProcessId))
                .Select(g => (Name: g.Key, Id: $"{g.Count()} 个", Down: g.Sum(r => r.DownloadBytesPerSecond), Up: g.Sum(r => r.UploadBytesPerSecond),
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

    /// <summary>Cached process names; looked up again after 30 s so reused PIDs eventually show the right name.</summary>
    private string ProcessName(int pid)
    {
        switch (pid)
        {
            case 0:
                return "System Idle";
            case 4:
                return "System";
        }

        var now = Environment.TickCount64;
        if (_names.TryGetValue(pid, out var cached) && now - cached.CheckedAt < 30_000)
        {
            return cached.Name;
        }

        string name;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            name = cached.Name is { Length: > 0 } previous && !previous.EndsWith("（已退出）", StringComparison.Ordinal)
                ? previous + "（已退出）"
                : cached.Name ?? $"PID {pid}（已退出）";
        }

        _names[pid] = (name, now);
        return name;
    }
}
