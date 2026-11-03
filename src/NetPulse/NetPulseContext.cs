using Microsoft.Win32;
using NetPulse.Core;
using NetPulse.Platform;
using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>
/// Owns the app's lifetime: tray icon, widget, sampling timer, persistence and data-plan alerts.
/// Everything runs on the UI thread, so the ledger and settings need no locking.
/// </summary>
internal sealed class NetPulseContext : ApplicationContext
{
    private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(1);

    internal static readonly JsonFileStore<AppSettings> SettingsStore =
        new(AppPaths.SettingsFile, () => new AppSettings());
    private readonly JsonFileStore<UsageDocument> _usageStore =
        new(AppPaths.UsageFile, () => new UsageDocument());
    private readonly JsonFileStore<NetworkUsageDocument> _networkUsageStore =
        new(AppPaths.NetworkUsageFile, () => new NetworkUsageDocument());
    private readonly WifiNetworkNames _networkNames = new();
    private readonly JsonFileStore<ProcessUsageDocument> _processUsageStore =
        new(AppPaths.ProcessUsageFile, () => new ProcessUsageDocument());

    private readonly AppSettings _settings;
    private readonly TrafficMonitor _monitor;
    private readonly Icon _icon;
    private readonly SpeedWidget _widget = new();
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _showWidgetItem = new(T("显示悬浮窗", "Show widget"));
    private readonly ToolStripMenuItem _lockItem = new(T("锁定位置", "Lock position"));
    private readonly ToolStripMenuItem _clickThroughItem = new(T("鼠标穿透", "Click-through"));
    private readonly ToolStripMenuItem _topMostItem = new(T("始终置顶", "Always on top"));
    private readonly ToolStripMenuItem _startupItem = new(T("开机自动启动", "Start with Windows"));
    private readonly System.Windows.Forms.Timer _sampleTimer = new();
    private readonly System.Windows.Forms.Timer _housekeepingTimer = new() { Interval = 2000 };
    private readonly EventWaitHandle _showWindowSignal;
    private readonly RegisteredWaitHandle _showWindowRegistration;
    private DateTime _lastSave = DateTime.UtcNow;
    private DateTime _lastToolTip = DateTime.MinValue;
    private readonly ProcessTrafficService _processTraffic;
    private readonly SpeedHistory _speedHistory = new(TimeSpan.FromMinutes(10));
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private bool _importing; // a restored backup is on disk; the in-memory state must not overwrite it
    private readonly SpeedTestService _speedTests = new();
    private readonly CancellationTokenSource _speedTestCancel = new();
    private readonly ProgramLimitTracker _programLimits = new();
    private SustainedSpeedDetector? _speedDetector;
    private (SpeedAlertDirection, long, int)? _speedDetectorKey;
    private readonly Icon _logoTrayIcon;
    private Icon? _speedTrayIcon;
    private string? _trayIconKey;
    private MainWindow? _mainWindow;
    private SettingsPage? _settingsPage;
    private volatile bool _exiting; // also read by the show-window callback on a pool thread

    /// <param name="settings">Loaded by Program, which needs them before any window exists.</param>
    /// <param name="openTab">Tab to show right away, e.g. after an elevated restart.</param>
    public NetPulseContext(AppSettings settings, MainTab? openTab = null)
    {
        _settings = settings;
        var ledger = new UsageLedger(_usageStore.Load().Days ?? []);
        _monitor = new TrafficMonitor(ledger)
        {
            Networks = new NetworkUsageLedger(_networkUsageStore.Load().Networks ?? []),
            IdentifyNetwork = _networkNames.Identify,
        };
        _processTraffic = new ProcessTrafficService(new ProcessUsageLedger(_processUsageStore.Load().Days ?? []));

        using (var stream = typeof(NetPulseContext).Assembly.GetManifestResourceStream("NetPulse.ico"))
        {
            _icon = stream is null ? (Icon)SystemIcons.Application.Clone() : new Icon(stream);
        }

        BuildMenu();
        _logoTrayIcon = new Icon(_icon, SystemInformation.SmallIconSize);
        _tray.Icon = _logoTrayIcon;
        _tray.Text = AppPaths.AppName;
        _tray.ContextMenuStrip = _menu;
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowMainWindow(MainTab.Usage);
            }
        };
        _tray.Visible = true;

        _widget.Icon = _icon;
        _widget.ContextMenuStrip = _menu;
        _widget.OpenRequested += (_, _) => ShowMainWindow(MainTab.Usage);
        _widget.Moved += (_, _) => SaveWidgetPosition();
        // WM_CLOSE from outside (taskkill, shutdown) ends the whole app cleanly instead of orphaning it.
        _widget.FormClosing += (_, e) =>
        {
            if (!_exiting)
            {
                e.Cancel = true;
                Exit();
            }
        };
        _widget.SetHistory(_speedHistory);
        _widget.ApplySettings(_settings);
        _widget.RestorePosition(_settings.Placement, _settings.WidgetX, _settings.WidgetY);
        _ = _widget.Handle; // create the window now so it can marshal calls from other threads

        _sampleTimer.Tick += (_, _) => OnSample();
        _housekeepingTimer.Tick += (_, _) => OnHousekeeping();
        ApplySettings(save: false);
        _sampleTimer.Start();
        _housekeepingTimer.Start();

        // A second launch signals this event instead of starting another copy.
        _showWindowSignal = Program.CreateShowWindowSignal();
        _showWindowRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showWindowSignal,
            (_, timedOut) =>
            {
                // Runs on a pool thread; during shutdown the widget's handle may already be gone.
                if (timedOut || _exiting)
                {
                    return;
                }

                try
                {
                    _widget.BeginInvoke(() => ShowMainWindow(MainTab.Usage));
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
        StartupRegistration.RefreshPathIfEnabled();
        if (Elevation.IsAdministrator)
        {
            _processTraffic.TryStart(); // per-program history is recorded whenever we can
        }

        CheckDataPlan();
        if (openTab is { } tab)
        {
            ShowMainWindow(tab);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
            // Wait until the pool has really dropped the wait before closing its handle; closing it
            // under a live wait can fire the callback one last time.
            using (var unregistered = new ManualResetEvent(false))
            {
                if (_showWindowRegistration.Unregister(unregistered))
                {
                    unregistered.WaitOne(TimeSpan.FromSeconds(2));
                }
            }

            _showWindowSignal.Dispose();
            _sampleTimer.Dispose();
            _housekeepingTimer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            if (_speedTrayIcon is not null)
            {
                TrayIconRenderer.Destroy(_speedTrayIcon);
            }

            _logoTrayIcon.Dispose();
            _mainWindow?.Dispose();
            _processTraffic.Dispose();
            _widget.Dispose();
            _menu.Dispose();
            _icon.Dispose();
        }

        base.Dispose(disposing);
    }

    private void BuildMenu()
    {
        _showWidgetItem.Click += (_, _) => Toggle(s => s.ShowWidget = !s.ShowWidget);
        _lockItem.Click += (_, _) => Toggle(s => s.LockPosition = !s.LockPosition);
        _clickThroughItem.Click += (_, _) => Toggle(s => s.ClickThrough = !s.ClickThrough);
        _topMostItem.Click += (_, _) => Toggle(s => s.AlwaysOnTop = !s.AlwaysOnTop);
        _startupItem.Click += (_, _) =>
        {
            if (Startup.Current() == StartupMode.Elevated)
            {
                ShowMainWindow(MainTab.Settings); // leaving it needs a UAC prompt; let the user choose there
                return;
            }

            try
            {
                StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled());
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                ErrorLog.Write(ex, "StartupRegistration");
            }

            _settingsPage?.Reload();
        };

        _menu.Items.Add(T("流量统计…", "Usage…"), null, (_, _) => ShowMainWindow(MainTab.Usage));
        _menu.Items.Add(T("实时速度…", "Live speed…"), null, (_, _) => ShowMainWindow(MainTab.Graph));
        _menu.Items.Add(T("进程流量…", "Programs…"), null, (_, _) => ShowMainWindow(MainTab.Processes));
        _menu.Items.Add("Wi-Fi…", null, (_, _) => ShowMainWindow(MainTab.Wifi));
        _menu.Items.Add(T("测速…", "Speed test…"), null, (_, _) => ShowMainWindow(MainTab.SpeedTest));
        _menu.Items.Add(T("网络工具…", "Network tools…"), null, (_, _) => ShowMainWindow(MainTab.Tools));
        _menu.Items.Add(T("设置…", "Settings…"), null, (_, _) => ShowMainWindow(MainTab.Settings));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.AddRange([_showWidgetItem, _topMostItem, _lockItem, _clickThroughItem]);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(T("退出", "Exit"), null, (_, _) => Exit());
        _menu.Opening += (_, _) =>
        {
            _showWidgetItem.Checked = _settings.ShowWidget;
            _topMostItem.Checked = _settings.AlwaysOnTop;
            _lockItem.Checked = _settings.LockPosition;
            _clickThroughItem.Checked = _settings.ClickThrough;
            _startupItem.Checked = Startup.Current() != StartupMode.Off;
        };
    }

    private void Toggle(Action<AppSettings> change)
    {
        change(_settings);
        ApplySettings(save: true);
        _settingsPage?.Reload();
    }

    private void ApplySettings(bool save)
    {
        _sampleTimer.Interval = _settings.RefreshIntervalMs;
        _widget.ApplySettings(_settings);
        UpdateWidgetVisibility();
        if (save)
        {
            SaveSettings();
        }
    }

    private void OnSample()
    {
        SpeedSample sample;
        try
        {
            sample = _monitor.Tick(_settings.AdapterId, _settings.ExcludeVirtualAdapters);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "Sampling");
            return;
        }

        _speedHistory.Add(_clock.Elapsed, sample);
        _widget.ShowSpeed(sample);
        UpdateTrayIcon(sample);
        CheckSpeedAlert(sample);

        // NotifyIcon.Text is limited to 127 characters and updating it is relatively costly.
        var down = ByteFormatter.FormatSpeed(sample.DownloadBytesPerSecond, _settings.Unit);
        var up = ByteFormatter.FormatSpeed(sample.UploadBytesPerSecond, _settings.Unit);
        _tray.Text = $"{AppPaths.AppName}\n↓ {down}   ↑ {up}";

        if (DateTime.UtcNow - _lastToolTip > TimeSpan.FromSeconds(5))
        {
            _lastToolTip = DateTime.UtcNow;
            var today = _monitor.Ledger.Get(DateOnly.FromDateTime(DateTime.Now));
            _widget.SetToolTipText(
                T($"今天：↓ {ByteFormatter.FormatSize(today.Downloaded)}  ↑ {ByteFormatter.FormatSize(today.Uploaded)}\n双击打开流量统计", $"Today: ↓ {ByteFormatter.FormatSize(today.Downloaded)}  ↑ {ByteFormatter.FormatSize(today.Uploaded)}\nDouble-click to open usage"));
        }
    }

    private void CheckSpeedAlert(SpeedSample sample)
    {
        var alert = _settings.SpeedAlert;
        if (!alert.Enabled)
        {
            _speedDetector = null;
            _speedDetectorKey = null;
            return;
        }

        var key = (alert.Direction, alert.ThresholdBytesPerSecond, alert.Minutes);
        if (_speedDetector is null || _speedDetectorKey != key)
        {
            _speedDetector = new SustainedSpeedDetector(alert.ThresholdBytesPerSecond, TimeSpan.FromMinutes(alert.Minutes));
            _speedDetectorKey = key;
        }

        var upload = alert.Direction == SpeedAlertDirection.Upload;
        if (_speedDetector.Feed(_clock.Elapsed, upload ? sample.UploadBytesPerSecond : sample.DownloadBytesPerSecond))
        {
            var limit = ByteFormatter.FormatSpeed(alert.ThresholdBytesPerSecond, _settings.Unit);
            var now = ByteFormatter.FormatSpeed(upload ? sample.UploadBytesPerSecond : sample.DownloadBytesPerSecond, _settings.Unit);
            _tray.ShowBalloonTip(
                10_000,
                upload ? T("上传速度持续偏高", "Upload speed stays high") : T("下载速度持续偏高", "Download speed stays high"),
                T($"已连续 {alert.Minutes} 分钟超过 {limit}，当前 {now}。可在“进程流量”中查看是哪个程序。", $"Above {limit} for {alert.Minutes} minutes, now {now}. Check the Programs tab to see which program."),
                ToolTipIcon.Warning);
        }
    }

    /// <summary>Starts the scheduled speed test when it is due; waits while a full-screen app runs or another test is going.</summary>
    private void CheckSpeedTestSchedule()
    {
        var schedule = _settings.SpeedTestSchedule;
        if (_exiting || _speedTests.IsBusy || !schedule.IsDue(DateTime.Now) || NativeMethods.IsFullscreenAppActive())
        {
            return;
        }

        schedule.LastRun = DateTime.Now; // also when the test fails: a missing connection is not retried every two seconds
        SaveSettings();
        _ = RunScheduledSpeedTestAsync();
    }

    private async Task RunScheduledSpeedTestAsync()
    {
        try
        {
            await _speedTests.RunAsync(null, _speedTestCancel.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "ScheduledSpeedTest"); // nobody awaits this task, so it must not escape
        }
    }

    private void CheckProgramAlert()
    {
        var alert = _settings.ProgramAlert;
        if (!alert.Enabled || !_processTraffic.IsRunning)
        {
            return;
        }

        var over = _programLimits.NewlyOver(_processTraffic.History, DateOnly.FromDateTime(DateTime.Now), alert.LimitBytes);
        if (over.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", over.Take(3).Select(a => $"{a.Name} ({ByteFormatter.FormatSize(a.Total)})"))
            + (over.Count > 3 ? T($" 等 {over.Count} 个程序", $" and {over.Count - 3} more") : string.Empty);
        _tray.ShowBalloonTip(
            10_000,
            T("程序今日流量超出设定", "Program over its daily limit"),
            T($"超过 {ByteFormatter.FormatSize(alert.LimitBytes)}：{names}", $"Over {ByteFormatter.FormatSize(alert.LimitBytes)}: {names}"),
            ToolTipIcon.Warning);
    }

    private void CheckWeeklySummary()
    {
        if (!_settings.WeeklySummary)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        if (WeeklySummary.DueOn(_monitor.Ledger, today, _settings.WeeklySummaryShownFor) is not { } summary)
        {
            return;
        }

        _settings.WeeklySummaryShownFor = WeeklySummary.MondayOf(today);
        SaveSettings();
        var change = summary.PreviousWeek.Total > 0
            ? T($"，比前一周{((double)summary.Week.Total / summary.PreviousWeek.Total) - 1:+0%;-0%;±0%}", $", {((double)summary.Week.Total / summary.PreviousWeek.Total) - 1:+0%;-0%;±0%} vs the week before")
            : string.Empty;
        _tray.ShowBalloonTip(
            10_000,
            T($"上周流量（{summary.From:M/d} – {summary.To:M/d}）", $"Last week ({summary.From:M/d} – {summary.To:M/d})"),
            T($"↓ {ByteFormatter.FormatSize(summary.Week.Downloaded)}  ↑ {ByteFormatter.FormatSize(summary.Week.Uploaded)}，合计 {ByteFormatter.FormatSize(summary.Week.Total)}{change}", $"↓ {ByteFormatter.FormatSize(summary.Week.Downloaded)}  ↑ {ByteFormatter.FormatSize(summary.Week.Uploaded)}, {ByteFormatter.FormatSize(summary.Week.Total)} in total{change}"),
            ToolTipIcon.Info);
    }

    /// <summary>Shows the speed in the tray icon, redrawing only when the picture would change.</summary>
    private void UpdateTrayIcon(SpeedSample sample)
    {
        var mode = _settings.TrayIcon;
        string key;
        Icon next;
        if (mode == TrayIconMode.Logo)
        {
            key = "logo";
            if (key == _trayIconKey)
            {
                return;
            }

            next = _logoTrayIcon;
        }
        else
        {
            var download = mode == TrayIconMode.Download;
            var (text, unitIndex) = ByteFormatter.FormatCompact(
                download ? sample.DownloadBytesPerSecond : sample.UploadBytesPerSecond, _settings.Unit);
            // The taskbar, not the widget theme, decides which colors are readable here.
            var palette = WidgetPalette.For(ThemeMode.Auto);
            var color = download ? palette.Download : palette.Upload;
            key = $"{text}|{unitIndex}|{color.ToArgb()}";
            if (key == _trayIconKey)
            {
                return;
            }

            next = TrayIconRenderer.Render(text, unitIndex, color);
        }

        _trayIconKey = key;
        _tray.Icon = next;
        if (_speedTrayIcon is not null)
        {
            TrayIconRenderer.Destroy(_speedTrayIcon);
        }

        _speedTrayIcon = ReferenceEquals(next, _logoTrayIcon) ? null : next;
    }

    private void OnHousekeeping()
    {
        UpdateWidgetVisibility();
        _widget.BringToTopIfTopMost();
        _processTraffic.RecordHistory();
        CheckProgramAlert();
        CheckSpeedTestSchedule();

        if (DateTime.UtcNow - _lastSave >= SaveInterval)
        {
            SaveUsage();
            CheckDataPlan();
            CheckWeeklySummary();
        }
    }

    private void UpdateWidgetVisibility()
    {
        var hiddenForFullscreen = _settings.HideWhenFullscreen && NativeMethods.IsFullscreenAppActive();
        var shouldShow = _settings.ShowWidget && !hiddenForFullscreen && !_exiting;
        if (shouldShow && !_widget.Visible)
        {
            _widget.Show();
            _widget.BringToTopIfTopMost();
        }
        else if (!shouldShow && _widget.Visible)
        {
            _widget.Hide();
        }
    }

    private void CheckDataPlan()
    {
        CheckPlan(_settings.Plan, _monitor.Ledger, networkLabel: null);
        foreach (var (key, plan) in _settings.NetworkPlans)
        {
            if (_monitor.Networks.Get(key) is { } ledger)
            {
                var label = _monitor.Networks.Networks.FirstOrDefault(n => n.Key == key).Label;
                CheckPlan(plan, ledger, label);
            }
        }
    }

    /// <summary>Shows a balloon when <paramref name="plan"/> reaches a new alert level; <paramref name="networkLabel"/> names a per-network plan.</summary>
    private void CheckPlan(DataPlanSettings plan, UsageLedger ledger, string? networkLabel)
    {
        if (!plan.Enabled)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var (cycle, status) = DataPlanCalculator.Evaluate(ledger, plan, today);
        var alert = plan.TakePendingAlert(cycle, status);
        if (alert == PlanAlertLevel.None)
        {
            return;
        }

        SaveSettings();
        var used = $"{ByteFormatter.FormatSize(status.UsedBytes)} / {ByteFormatter.FormatSize(status.LimitBytes)}";
        var name = networkLabel is null ? string.Empty : $"{networkLabel}: ";
        if (alert == PlanAlertLevel.Exceeded)
        {
            _tray.ShowBalloonTip(10_000, name + T("流量套餐已用完", "Data plan used up"), T($"本周期已使用 {used}。", $"Used {used} this cycle."), ToolTipIcon.Warning);
        }
        else
        {
            _tray.ShowBalloonTip(10_000, name + T("流量套餐即将用完", "Data plan almost used up"), T($"本周期已使用 {used}（{status.Fraction:P0}）。", $"Used {used} this cycle ({status.Fraction:P0})."), ToolTipIcon.Info);
        }
    }

    private void ShowMainWindow(MainTab tab)
    {
        if (_mainWindow is null || _mainWindow.IsDisposed)
        {
            _settingsPage = new SettingsPage(_settings, ExportBackup, ImportBackup);
            _settingsPage.SettingsChanged += (_, _) => ApplySettings(save: true);
            _mainWindow = new MainWindow(
            [
                (MainTab.Usage, T("流量统计", "Usage"), new UsagePage(_monitor.Ledger, _monitor.Networks, _settings, SaveUsage, SaveSettings)),
                (MainTab.Graph, T("实时速度", "Live speed"), new SpeedGraphPage(_speedHistory, _settings)),
                (MainTab.Processes, T("进程流量", "Programs"),new ProcessTrafficPage(_processTraffic, _settings, RestartElevated)),
                (MainTab.Wifi, "Wi-Fi", new WifiHostPage()),
                (MainTab.SpeedTest, T("测速", "Speed test"), new SpeedTestPage(_speedTests, _settings, SaveSettings)),
                (MainTab.Tools, T("网络工具", "Network tools"), new NetworkToolsPage()),
                (MainTab.Settings, T("设置", "Settings"), _settingsPage),
            ],
                _icon);
            _mainWindow.ExitRequested += (_, _) => Exit();
        }
        else if (tab == MainTab.Settings)
        {
            _settingsPage?.Reload();
        }

        _mainWindow.ShowTab(tab);
    }

    /// <summary>Starts an elevated copy and exits; returns false if the user declined the UAC prompt.</summary>
    private bool RestartElevated()
    {
        // Persist first: the new process loads settings and usage from disk.
        SaveUsage();
        SaveSettings();
        if (!Elevation.TryRestartElevated(MainTab.Processes))
        {
            return false;
        }

        Exit();
        return true;
    }

    private void ExportBackup(string path)
    {
        SaveUsage();
        SaveSettings();
        BackupDocument.Write(path, new BackupDocument
        {
            AppVersion = ElevatedAutostart.CurrentVersion,
            Settings = _settings,
            Usage = new UsageDocument { Days = [.. _monitor.Ledger.ToList()] },
            NetworkUsage = _monitor.Networks.ToDocument(),
            ProcessUsage = _processTraffic.History.ToDocument(),
        });
    }

    /// <summary>Replaces the files on disk with the backup's and restarts; nothing is kept in memory, so nothing overwrites them on exit.</summary>
    private void ImportBackup(string path)
    {
        var backup = BackupDocument.Read(path);
        SettingsStore.Save(backup.Settings);
        _usageStore.Save(backup.Usage);
        _networkUsageStore.Save(backup.NetworkUsage);
        _processUsageStore.Save(backup.ProcessUsage);

        _importing = true;
        try
        {
            // A plain child keeps this process's rights, so an elevated NetPulse restarts elevated without a prompt.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Application.ExecutablePath,
                Arguments = $"{Program.RestartedArgument} {Program.OpenArgumentPrefix}{MainTab.Settings}{AppPaths.ProfileArgument}",
            })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _importing = false;
            ErrorLog.Write(ex, "ImportBackup restart");
            throw new IOException(T("已恢复数据，但无法自动重启，请手动重启 NetPulse。", "The data was restored, but NetPulse could not restart by itself. Please start it again."), ex);
        }

        Exit();
    }

    private void SaveWidgetPosition()
    {
        _settings.WidgetX = _widget.Left;
        _settings.WidgetY = _widget.Top;
        _settings.Placement = _widget.CapturePlacement();
        SaveSettings();
    }

    private void SaveSettings()
    {
        if (_importing)
        {
            return;
        }

        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex, "SaveSettings");
        }
    }

    /// <summary>Saves the daily totals and the per-program history, whichever changed.</summary>
    private void SaveUsage()
    {
        if (_importing)
        {
            return;
        }

        _lastSave = DateTime.UtcNow;
        var keepFrom = DateOnly.FromDateTime(DateTime.Now).AddDays(-_settings.UsageRetentionDays);
        var ledger = _monitor.Ledger;
        ledger.Prune(keepFrom);
        var networks = _monitor.Networks;
        networks.Prune(keepFrom);
        if (networks.IsDirty)
        {
            try
            {
                _networkUsageStore.Save(networks.ToDocument());
                networks.MarkSaved();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Write(ex, "SaveNetworkUsage");
            }
        }

        if (ledger.IsDirty)
        {
            try
            {
                _usageStore.Save(new UsageDocument { Days = [.. ledger.ToList()] });
                ledger.MarkSaved();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Write(ex, "SaveUsage");
            }
        }

        _processTraffic.RecordHistory();
        var history = _processTraffic.History;
        history.Prune(keepFrom);
        if (history.IsDirty)
        {
            try
            {
                _processUsageStore.Save(history.ToDocument());
                history.MarkSaved();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Write(ex, "SaveProcessUsage");
            }
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            _trayIconKey = null; // the taskbar may have changed between light and dark
            if (_settings.Theme == ThemeMode.Auto)
            {
                _widget.ApplySettings(_settings);
            }
        }
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => SaveUsage();

    private void Exit()
    {
        _exiting = true;
        _speedTestCancel.Cancel();
        _sampleTimer.Stop();
        _housekeepingTimer.Stop();
        SaveUsage();
        SaveSettings();
        _tray.Visible = false;
        ExitThread();
    }
}
