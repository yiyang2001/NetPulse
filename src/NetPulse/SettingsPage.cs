using System.Diagnostics;
using NetPulse.Core;
using NetPulse.Platform;

namespace NetPulse;

/// <summary>Edits <see cref="AppSettings"/> in place; every change applies immediately.</summary>
internal sealed class SettingsPage : UserControl
{
    private readonly AppSettings _settings;
    private readonly ComboBox _unit = DropDown("字节（KB/s、MB/s）", "比特（Kbps、Mbps）");
    private readonly NumericUpDown _interval = Number(250, 10_000, 250, 0);
    private readonly ComboBox _adapter = DropDown();
    private readonly CheckBox _excludeVirtual = Check("自动模式下排除虚拟网卡（VPN、Hyper-V 等，避免重复计算）");
    private readonly ComboBox _theme = DropDown("跟随任务栏", "深色", "浅色");
    private readonly TrackBar _opacity = new() { Minimum = 30, Maximum = 100, TickFrequency = 10, Width = 220, AutoSize = false, Height = 32 };
    private readonly NumericUpDown _fontSize = Number(7, 24, 0.5m, 1);
    private readonly CheckBox _showWidget = Check("显示悬浮窗");
    private readonly CheckBox _alwaysOnTop = Check("始终置顶");
    private readonly CheckBox _hideFullscreen = Check("全屏应用（游戏、视频、演示）时自动隐藏");
    private readonly CheckBox _lock = Check("锁定位置");
    private readonly CheckBox _clickThrough = Check("鼠标穿透（点击会穿过悬浮窗；需通过托盘菜单关闭）");
    private readonly ComboBox _startup = DropDown("不启动", "启动", "以管理员身份启动（自动记录进程流量）");
    private readonly Label _startupStatus = new() { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = SystemColors.GrayText, Padding = new Padding(0, 2, 0, 4) };
    private readonly Button _updateInstalled = new() { Text = "用当前版本更新已安装的副本", AutoSize = true };
    private StartupMode _startupMode;
    private readonly CheckBox _planEnabled = Check("启用流量套餐提醒");
    private readonly NumericUpDown _planLimit = Number(0.1m, 1_000_000, 1, 1);
    private readonly NumericUpDown _planDay = Number(1, 31, 1, 0);
    private readonly NumericUpDown _planWarn = Number(1, 100, 5, 0);
    private readonly CheckBox _planUpload = Check("上传流量也计入套餐");
    private readonly NumericUpDown _retention = Number(31, 3660, 30, 0);
    private bool _loading;

    public SettingsPage(AppSettings settings)
    {
        _settings = settings;
        AutoScroll = true;
        Padding = new Padding(12);

        var openFolder = new Button { Text = "打开数据文件夹", AutoSize = true };
        openFolder.Click += (_, _) => OpenDataFolder();

        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        layout.Controls.Add(Group(
            "显示",
            Row("速度单位", _unit),
            Row("刷新间隔（毫秒）", _interval),
            Row("网卡", _adapter),
            _excludeVirtual,
            Row("主题", _theme),
            Row("不透明度", _opacity),
            Row("字号", _fontSize)));
        layout.Controls.Add(Group(
            "行为",
            _showWidget, _alwaysOnTop, _hideFullscreen, _lock, _clickThrough,
            Row("开机启动", _startup),
            _startupStatus,
            _updateInstalled));
        layout.Controls.Add(Group(
            "流量套餐",
            _planEnabled,
            Row("每周期流量（GB）", _planLimit),
            Row("每月重置日", _planDay),
            Row("提醒阈值（%）", _planWarn),
            _planUpload));
        layout.Controls.Add(Group(
            "数据与隐私",
            Row("统计保留天数", _retention),
            new Label
            {
                AutoSize = true,
                MaximumSize = new Size(560, 0),
                Text = "NetPulse 不收集、不上传任何数据，也没有广告或付费功能。只有在你主动使用测速、查询公网 IP、" +
                    "Ping / 路由追踪 / DNS 查询时才会联网，且只连接对应的目标。\n所有设置和统计只保存在本机：" + AppPaths.DataDirectory,
                Padding = new Padding(0, 6, 0, 6),
            },
            openFolder));
        Controls.Add(layout);

        foreach (var combo in new[] { _unit, _adapter, _theme })
        {
            combo.SelectedIndexChanged += OnChanged;
        }

        foreach (var number in new[] { _interval, _fontSize, _planLimit, _planDay, _planWarn, _retention })
        {
            number.ValueChanged += OnChanged;
        }

        foreach (var check in new[] { _excludeVirtual, _showWidget, _alwaysOnTop, _hideFullscreen, _lock, _clickThrough, _planEnabled, _planUpload })
        {
            check.CheckedChanged += OnChanged;
        }

        _opacity.ValueChanged += OnChanged;
        _startup.SelectedIndexChanged += (_, _) =>
        {
            var mode = (StartupMode)Math.Max(0, _startup.SelectedIndex);
            if (!_loading && mode != _startupMode)
            {
                ChangeStartup(mode);
            }
        };
        _updateInstalled.Click += (_, _) => ChangeStartup(StartupMode.Elevated);
        Reload();
    }

    /// <summary>Raised after the user changed a setting; the owner applies and saves it.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Copies the current settings into the controls (e.g. after a change from the tray menu).</summary>
    public void Reload()
    {
        _loading = true;
        try
        {
            _unit.SelectedIndex = (int)_settings.Unit;
            _interval.Value = _settings.RefreshIntervalMs;
            LoadAdapters();
            _excludeVirtual.Checked = _settings.ExcludeVirtualAdapters;
            _theme.SelectedIndex = (int)_settings.Theme;
            _opacity.Value = (int)Math.Round(_settings.Opacity * 100);
            _fontSize.Value = (decimal)_settings.FontSize;
            _showWidget.Checked = _settings.ShowWidget;
            _alwaysOnTop.Checked = _settings.AlwaysOnTop;
            _hideFullscreen.Checked = _settings.HideWhenFullscreen;
            _lock.Checked = _settings.LockPosition;
            _clickThrough.Checked = _settings.ClickThrough;
            LoadStartup();
            _planEnabled.Checked = _settings.Plan.Enabled;
            _planLimit.Value = (decimal)_settings.Plan.LimitGb;
            _planDay.Value = _settings.Plan.CycleStartDay;
            _planWarn.Value = _settings.Plan.WarnPercent;
            _planUpload.Checked = _settings.Plan.CountUpload;
            _retention.Value = _settings.UsageRetentionDays;
            UpdatePlanEnabledState();
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.Unit = (SpeedUnit)Math.Max(0, _unit.SelectedIndex);
        _settings.RefreshIntervalMs = (int)_interval.Value;
        _settings.AdapterId = (_adapter.SelectedItem as AdapterItem)?.Id;
        _settings.ExcludeVirtualAdapters = _excludeVirtual.Checked;
        _settings.Theme = (ThemeMode)Math.Max(0, _theme.SelectedIndex);
        _settings.Opacity = _opacity.Value / 100.0;
        _settings.FontSize = (float)_fontSize.Value;
        _settings.ShowWidget = _showWidget.Checked;
        _settings.AlwaysOnTop = _alwaysOnTop.Checked;
        _settings.HideWhenFullscreen = _hideFullscreen.Checked;
        _settings.LockPosition = _lock.Checked;
        _settings.ClickThrough = _clickThrough.Checked;
        _settings.Plan.Enabled = _planEnabled.Checked;
        _settings.Plan.LimitGb = (double)_planLimit.Value;
        _settings.Plan.CycleStartDay = (int)_planDay.Value;
        _settings.Plan.WarnPercent = (int)_planWarn.Value;
        _settings.Plan.CountUpload = _planUpload.Checked;
        _settings.UsageRetentionDays = (int)_retention.Value;
        _settings.Normalize();
        UpdatePlanEnabledState();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies a new startup mode; the elevated mode (and leaving it) goes through a UAC prompt.</summary>
    private void ChangeStartup(StartupMode mode)
    {
        string? error;
        UseWaitCursor = true;
        try
        {
            error = Startup.Change(_startupMode, mode);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            ErrorLog.Write(ex, "Startup.Change");
            error = ex.Message;
        }
        finally
        {
            UseWaitCursor = false;
        }

        if (error is not null)
        {
            MessageBox.Show(this, "无法修改开机启动设置：" + error, AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        _loading = true;
        try
        {
            LoadStartup();
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadStartup()
    {
        _startupMode = Startup.Current();
        _startup.SelectedIndex = (int)_startupMode;
        var installed = ElevatedAutostart.InstalledVersion();
        var outdated = _startupMode == StartupMode.Elevated && !ElevatedAutostart.IsRunningInstalledCopy
            && installed != ElevatedAutostart.CurrentVersion;
        _updateInstalled.Visible = outdated;
        _startupStatus.Text = _startupMode switch
        {
            StartupMode.Elevated =>
                $"登录后由“任务计划程序”以管理员身份启动 {ElevatedAutostart.InstalledExe}（版本 {installed ?? "?"}），" +
                "不会弹出权限提示。" + (outdated ? $"\n当前运行的是版本 {ElevatedAutostart.CurrentVersion}。" : string.Empty),
            StartupMode.Normal => "登录后以普通权限启动，不能记录进程流量。",
            _ => "选择“以管理员身份启动”时，NetPulse 会被复制到 Program Files，以免这个以管理员身份运行的程序被其他程序替换。",
        };
    }

    private void LoadAdapters()
    {
        _adapter.Items.Clear();
        _adapter.Items.Add(new AdapterItem(null, "全部网卡（自动）"));
        var found = false;
        foreach (var adapter in NetworkAdapters.ReadAll().OrderBy(a => a.IsLikelyVirtual).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var suffix = adapter.IsLikelyVirtual ? "（虚拟）" : string.Empty;
            _adapter.Items.Add(new AdapterItem(adapter.Id, $"{adapter.Name} — {adapter.Description}{suffix}"));
            found |= adapter.Id == _settings.AdapterId;
        }

        if (!found && _settings.AdapterId is not null)
        {
            _adapter.Items.Add(new AdapterItem(_settings.AdapterId, "已选网卡（当前未连接）"));
        }

        _adapter.SelectedItem = _adapter.Items.Cast<AdapterItem>().First(i => i.Id == _settings.AdapterId);
    }

    private void UpdatePlanEnabledState()
    {
        _planLimit.Enabled = _planDay.Enabled = _planWarn.Enabled = _planUpload.Enabled = _planEnabled.Checked;
    }

    private void OpenDataFolder()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        Process.Start(new ProcessStartInfo { FileName = AppPaths.DataDirectory, UseShellExecute = true })?.Dispose();
    }

    private static ComboBox DropDown(params string[] items)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
        combo.Items.AddRange(items);
        return combo;
    }

    private static NumericUpDown Number(decimal min, decimal max, decimal step, int decimals) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = step,
        DecimalPlaces = decimals,
        Width = 120,
    };

    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 4, 3, 4) };

    private static FlowLayoutPanel Row(string label, Control control)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.Add(new Label { Text = label, Width = 150, TextAlign = ContentAlignment.MiddleLeft, Height = 28 });
        row.Controls.Add(control);
        return row;
    }

    private static GroupBox Group(string title, params Control[] children)
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
        };
        flow.Controls.AddRange(children);
        var box = new GroupBox
        {
            Text = title,
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(10, 6, 10, 8),
            Margin = new Padding(0, 0, 0, 10),
            MinimumSize = new Size(600, 0),
        };
        box.Controls.Add(flow);
        return box;
    }

    private sealed record AdapterItem(string? Id, string Label)
    {
        public override string ToString() => Label;
    }
}
