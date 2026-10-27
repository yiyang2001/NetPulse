using Microsoft.Win32;
using NetPulse.Core;
using NetPulse.Platform;

namespace NetPulse;

/// <summary>
/// Owns the app's lifetime: tray icon, widget, sampling timer, persistence and data-plan alerts.
/// Everything runs on the UI thread, so the ledger and settings need no locking.
/// </summary>
internal sealed class NetPulseContext : ApplicationContext
{
    private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(1);

    private readonly JsonFileStore<AppSettings> _settingsStore =
        new(AppPaths.SettingsFile, () => new AppSettings());
    private readonly JsonFileStore<UsageDocument> _usageStore =
        new(AppPaths.UsageFile, () => new UsageDocument());
    private readonly JsonFileStore<ProcessUsageDocument> _processUsageStore =
        new(AppPaths.ProcessUsageFile, () => new ProcessUsageDocument());

    private readonly AppSettings _settings;
    private readonly TrafficMonitor _monitor;
    private readonly Icon _icon;
    private readonly SpeedWidget _widget = new();
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _showWidgetItem = new("显示悬浮窗");
    private readonly ToolStripMenuItem _lockItem = new("锁定位置");
    private readonly ToolStripMenuItem _clickThroughItem = new("鼠标穿透");
    private readonly ToolStripMenuItem _topMostItem = new("始终置顶");
    private readonly ToolStripMenuItem _startupItem = new("开机自动启动");
    private readonly System.Windows.Forms.Timer _sampleTimer = new();
    private readonly System.Windows.Forms.Timer _housekeepingTimer = new() { Interval = 2000 };
    private readonly EventWaitHandle _showWindowSignal;
    private readonly RegisteredWaitHandle _showWindowRegistration;
    private DateTime _lastSave = DateTime.UtcNow;
    private DateTime _lastToolTip = DateTime.MinValue;
    private readonly ProcessTrafficService _processTraffic;
    private readonly SpeedHistory _speedHistory = new(TimeSpan.FromMinutes(10));
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private MainWindow? _mainWindow;
    private SettingsPage? _settingsPage;
    private volatile bool _exiting; // also read by the show-window callback on a pool thread

    /// <param name="openTab">Tab to show right away, e.g. after an elevated restart.</param>
    public NetPulseContext(MainTab? openTab = null)
    {
        _settings = _settingsStore.Load().Normalize();
        var ledger = new UsageLedger(_usageStore.Load().Days ?? []);
        _monitor = new TrafficMonitor(ledger);
        _processTraffic = new ProcessTrafficService(new ProcessUsageLedger(_processUsageStore.Load().Days ?? []));

        using (var stream = typeof(NetPulseContext).Assembly.GetManifestResourceStream("NetPulse.ico"))
        {
            _icon = stream is null ? (Icon)SystemIcons.Application.Clone() : new Icon(stream);
        }

        BuildMenu();
        _tray.Icon = new Icon(_icon, SystemInformation.SmallIconSize);
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
        _widget.ApplySettings(_settings);
        _widget.RestorePosition(_settings.WidgetX, _settings.WidgetY);
        _ = _widget.Handle; // create the window now so it can marshal calls from other threads

        _sampleTimer.Tick += (_, _) => OnSample();
        _housekeepingTimer.Tick += (_, _) => OnHousekeeping();
        ApplySettings(save: false);
        _sampleTimer.Start();
        _housekeepingTimer.Start();

        // A second launch signals this event instead of starting another copy.
        _showWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowWindowEventName);
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
            _tray.Icon?.Dispose();
            _tray.Dispose();
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

        _menu.Items.Add("流量统计…", null, (_, _) => ShowMainWindow(MainTab.Usage));
        _menu.Items.Add("实时速度…", null, (_, _) => ShowMainWindow(MainTab.Graph));
        _menu.Items.Add("进程流量…", null, (_, _) => ShowMainWindow(MainTab.Processes));
        _menu.Items.Add("Wi-Fi…", null, (_, _) => ShowMainWindow(MainTab.Wifi));
        _menu.Items.Add("测速…", null, (_, _) => ShowMainWindow(MainTab.SpeedTest));
        _menu.Items.Add("网络工具…", null, (_, _) => ShowMainWindow(MainTab.Tools));
        _menu.Items.Add("设置…", null, (_, _) => ShowMainWindow(MainTab.Settings));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.AddRange([_showWidgetItem, _topMostItem, _lockItem, _clickThroughItem]);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => Exit());
        _menu.Opening += (_, _) =>
        {
            _showWidgetItem.Checked = _settings.ShowWidget;
            _topMostItem.Checked = _settings.AlwaysOnTop;
            _lockItem.Checked = _settings.LockPosition;
            _clickThroughItem.Checked = _settings.ClickThrough;
            _startupItem.Checked = StartupRegistration.IsEnabled();
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

        _widget.ShowSpeed(sample);
        _speedHistory.Add(_clock.Elapsed, sample);

        // NotifyIcon.Text is limited to 127 characters and updating it is relatively costly.
        var down = ByteFormatter.FormatSpeed(sample.DownloadBytesPerSecond, _settings.Unit);
        var up = ByteFormatter.FormatSpeed(sample.UploadBytesPerSecond, _settings.Unit);
        _tray.Text = $"{AppPaths.AppName}\n↓ {down}   ↑ {up}";

        if (DateTime.UtcNow - _lastToolTip > TimeSpan.FromSeconds(5))
        {
            _lastToolTip = DateTime.UtcNow;
            var today = _monitor.Ledger.Get(DateOnly.FromDateTime(DateTime.Now));
            _widget.SetToolTipText(
                $"今天：↓ {ByteFormatter.FormatSize(today.Downloaded)}  ↑ {ByteFormatter.FormatSize(today.Uploaded)}\n双击打开流量统计");
        }
    }

    private void OnHousekeeping()
    {
        UpdateWidgetVisibility();
        _widget.BringToTopIfTopMost();
        _processTraffic.RecordHistory();

        if (DateTime.UtcNow - _lastSave >= SaveInterval)
        {
            SaveUsage();
            CheckDataPlan();
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
        var plan = _settings.Plan;
        if (!plan.Enabled)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var (cycle, status) = DataPlanCalculator.Evaluate(_monitor.Ledger, plan, today);
        var alert = plan.TakePendingAlert(cycle, status);
        if (alert == PlanAlertLevel.None)
        {
            return;
        }

        SaveSettings();
        var used = $"{ByteFormatter.FormatSize(status.UsedBytes)} / {ByteFormatter.FormatSize(status.LimitBytes)}";
        if (alert == PlanAlertLevel.Exceeded)
        {
            _tray.ShowBalloonTip(10_000, "流量套餐已用完", $"本周期已使用 {used}。", ToolTipIcon.Warning);
        }
        else
        {
            _tray.ShowBalloonTip(10_000, "流量套餐即将用完", $"本周期已使用 {used}（{status.Fraction:P0}）。", ToolTipIcon.Info);
        }
    }

    private void ShowMainWindow(MainTab tab)
    {
        if (_mainWindow is null || _mainWindow.IsDisposed)
        {
            _settingsPage = new SettingsPage(_settings);
            _settingsPage.SettingsChanged += (_, _) => ApplySettings(save: true);
            _mainWindow = new MainWindow(
            [
                (MainTab.Usage, "流量统计", new UsagePage(_monitor.Ledger, _settings, SaveUsage)),
                (MainTab.Graph, "实时速度", new SpeedGraphPage(_speedHistory, _settings)),
                (MainTab.Processes, "进程流量",new ProcessTrafficPage(_processTraffic, _settings, RestartElevated)),
                (MainTab.Wifi, "Wi-Fi", new WifiPage()),
                (MainTab.SpeedTest, "测速", new SpeedTestPage()),
                (MainTab.Tools, "网络工具", new NetworkToolsPage()),
                (MainTab.Settings, "设置", _settingsPage),
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

    private void SaveWidgetPosition()
    {
        _settings.WidgetX = _widget.Left;
        _settings.WidgetY = _widget.Top;
        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex, "SaveSettings");
        }
    }

    /// <summary>Saves the daily totals and the per-program history, whichever changed.</summary>
    private void SaveUsage()
    {
        _lastSave = DateTime.UtcNow;
        var keepFrom = DateOnly.FromDateTime(DateTime.Now).AddDays(-_settings.UsageRetentionDays);
        var ledger = _monitor.Ledger;
        ledger.Prune(keepFrom);
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
        if (_settings.Theme == ThemeMode.Auto
            && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            _widget.ApplySettings(_settings);
        }
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => SaveUsage();

    private void Exit()
    {
        _exiting = true;
        _sampleTimer.Stop();
        _housekeepingTimer.Stop();
        SaveUsage();
        SaveSettings();
        _tray.Visible = false;
        ExitThread();
    }
}
