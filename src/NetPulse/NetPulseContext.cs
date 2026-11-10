using Microsoft.Win32;
using NetPulse.Core;
using NetPulse.Platform;
using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>
/// Owns the app's lifetime: tray icon, widget, sampling timer, persistence and data-plan alerts.
/// Everything runs on the UI thread, so the ledger and settings need no locking.
/// </summary>
internal sealed partial class NetPulseContext : ApplicationContext
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
    private readonly UpdateService _updates = new();
    private Uri? _balloonLink; // opened when the balloon now showing is clicked
    private readonly ProgramLimitTracker _programLimits = new();
    private SustainedSpeedDetector? _speedDetector;
    private (SpeedAlertDirection, long, int)? _speedDetectorKey;
    private readonly Icon _logoTrayIcon;
    private Icon? _speedTrayIcon;
    private string? _trayIconKey;
    private string? _trayText;
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
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_balloonLink is { } link)
            {
                Ui.OpenUri(link.AbsoluteUri);
            }

            _balloonLink = null;
        };
        _tray.BalloonTipClosed += (_, _) => _balloonLink = null;
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

        InitQuality();
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
            _qualityTimer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            if (_speedTrayIcon is not null)
            {
                TrayIconRenderer.Destroy(_speedTrayIcon);
            }

            _logoTrayIcon.Dispose();
            _updates.Dispose();
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
        _menu.Items.Add(T("连接质量…", "Connection quality…"), null, (_, _) => ShowMainWindow(MainTab.Quality));
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
        ApplyQualitySettings();
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
        var trayText = $"{AppPaths.AppName}\n↓ {down}   ↑ {up}";
        if (QualityTrayLine() is { } quality)
        {
            trayText += "\n" + quality;
        }

        if (trayText != _trayText)
        {
            _trayText = trayText;
            _tray.Text = trayText;
        }

        if (DateTime.UtcNow - _lastToolTip > TimeSpan.FromSeconds(5))
        {
            _lastToolTip = DateTime.UtcNow;
            var today = _monitor.Ledger.Get(DateOnly.FromDateTime(DateTime.Now));
            _widget.SetToolTipText(
                T($"今天：↓ {ByteFormatter.FormatSize(today.Downloaded)}  ↑ {ByteFormatter.FormatSize(today.Uploaded)}\n双击打开流量统计", $"Today: ↓ {ByteFormatter.FormatSize(today.Downloaded)}  ↑ {ByteFormatter.FormatSize(today.Uploaded)}\nDouble-click to open usage"));
        }
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
        CheckForUpdates();

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

    private void ShowMainWindow(MainTab tab)
    {
        if (_mainWindow is null || _mainWindow.IsDisposed)
        {
            _settingsPage = new SettingsPage(_settings, ExportBackup, ImportBackup, _updates, InstallAndRestart, UninstallAndExit);
            _settingsPage.SettingsChanged += (_, _) => ApplySettings(save: true);
            _mainWindow = new MainWindow(
            [
                (MainTab.Usage, T("流量统计", "Usage"), new UsagePage(_monitor.Ledger, _monitor.Networks, _settings, SaveUsage, SaveSettings)),
                (MainTab.Graph, T("实时速度", "Live speed"), new SpeedGraphPage(_speedHistory, _settings)),
                (MainTab.Quality, T("连接质量", "Quality"), new QualityPage(_quality, _qualityMonitor, _qualityProbe, _settings, EnableQuality, SaveUsage)),
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
        _qualityTimer.Stop();
        StopQuality();
        SaveUsage();
        SaveSettings();
        _tray.Visible = false;
        ExitThread();
    }
}
