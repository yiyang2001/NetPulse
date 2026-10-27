using System.ComponentModel;
using NetPulse.Core;
using NetPulse.Platform;
using QRCoder;
using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>
/// Current Wi-Fi connection and saved networks. Passwords are fetched from Windows only when the user
/// clicks T("显示密码", "Show password"), kept only in this page's controls, cleared when the selection changes or the page
/// is hidden, and never written to disk.
/// </summary>
internal sealed class WifiPage : UserControl, IActivatablePage
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    private readonly Label _connection = new() { AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
    private readonly LinkLabel _locationHint = new()
    {
        AutoSize = true,
        Visible = false,
        Text = T("Windows 要求开启“位置”权限后，应用才能读取 Wi-Fi 连接信息。打开位置设置", "Windows lets apps read Wi-Fi connection details only with the Location permission on. Open location settings"),
    };
    private readonly ListView _profiles = new FlickerFreeListView
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
    };
    private readonly Button _reveal = new() { Text = T("显示密码", "Show password"), AutoSize = true, Enabled = false };
    private readonly Button _copy = new() { Text = T("复制", "Copy"), AutoSize = true, Enabled = false };
    private readonly Button _qr = new() { Text = T("二维码…", "QR code…"), AutoSize = true, Enabled = false };
    private readonly Button _refresh = new() { Text = T("刷新", "Refresh"), AutoSize = true };
    private readonly TextBox _password = new() { ReadOnly = true, Width = 260, UseSystemPasswordChar = true, Text = string.Empty };
    private readonly Label _passwordNote = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 6, 0, 0) };
    private Guid? _interface;
    private string? _revealedFor;

    public WifiPage()
    {
        Padding = new Padding(12);
        _profiles.Columns.Add(T("网络名称 (SSID)", "Network name (SSID)"), 260);
        _profiles.Columns.Add(T("安全类型", "Security"), 140);
        _profiles.Columns.Add(T("自动连接", "Auto-connect"), 80);
        _profiles.Columns.Add(T("隐藏网络", "Hidden"), 80);

        var connectionBox = new GroupBox { Text = T("当前连接", "Current connection"), Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 4, 10, 8) };
        var connectionFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        connectionFlow.Controls.Add(_connection);
        connectionFlow.Controls.Add(_locationHint);
        connectionBox.Controls.Add(connectionFlow);

        var savedLabel = new Label { Text = T("已保存的网络", "Saved networks"), AutoSize = true, Font = new Font(DefaultFont, FontStyle.Bold), Padding = new Padding(0, 10, 0, 4) };
        var savedHeader = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        savedHeader.Controls.Add(savedLabel);

        Controls.Add(_profiles);
        Controls.Add(savedHeader);
        Controls.Add(connectionBox);
        var actions = Ui.Toolbar(_reveal, _password, _copy, _qr, _refresh);
        actions.Dock = DockStyle.Bottom;
        actions.Padding = new Padding(0, 8, 0, 0);
        var note = Ui.Note(T("密码只在点击“显示密码”时向 Windows 读取，不会保存到磁盘，切换网络或离开此页面后自动隐藏。", "Passwords are read from Windows only when you click “Show password”, never saved to disk, and hidden again when you pick another network or leave this page."));
        note.Dock = DockStyle.Bottom;
        var passwordNoteBar = Ui.Toolbar(_passwordNote);
        passwordNoteBar.Dock = DockStyle.Bottom;
        Controls.Add(passwordNoteBar);
        Controls.Add(actions);
        Controls.Add(note);

        var linkText = T("打开位置设置", "Open location settings"); // the end of the hint text
        _locationHint.LinkArea = new LinkArea(_locationHint.Text.Length - linkText.Length, linkText.Length);
        _locationHint.LinkClicked += (_, _) => Ui.OpenUri("ms-settings:privacy-location");
        _profiles.SelectedIndexChanged += (_, _) => OnSelectionChanged();
        _reveal.Click += (_, _) => RevealPassword();
        _copy.Click += (_, _) => CopyPassword();
        _qr.Click += (_, _) => ShowQr();
        _refresh.Click += (_, _) => LoadAll();
        _timer.Tick += (_, _) => LoadConnection();
    }

    public void OnActivated()
    {
        LoadAll();
        _timer.Start();
    }

    public void OnDeactivated()
    {
        _timer.Stop();
        HidePassword();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void LoadAll()
    {
        HidePassword();
        try
        {
            using var wlan = new WlanClient();
            var interfaces = wlan.GetInterfaces();
            _interface = interfaces.Count > 0 ? interfaces[0].Id : null;
            if (_interface is not { } id)
            {
                _connection.Text = T("没有找到无线网卡。", "No wireless adapter found.");
                _profiles.Items.Clear();
                return;
            }

            LoadConnection(wlan, interfaces[0]);
            LoadProfiles(wlan, id);
        }
        catch (Win32Exception ex)
        {
            ErrorLog.Write(ex, "WifiPage.LoadAll");
            _connection.Text = T("无法读取 Wi-Fi 信息：", "Could not read Wi-Fi information: ") + ex.Message;
            _profiles.Items.Clear();
        }
    }

    private void LoadConnection()
    {
        try
        {
            using var wlan = new WlanClient();
            var interfaces = wlan.GetInterfaces();
            if (interfaces.Count > 0)
            {
                LoadConnection(wlan, interfaces[0]);
            }
        }
        catch (Win32Exception ex)
        {
            _connection.Text = T("无法读取 Wi-Fi 信息：", "Could not read Wi-Fi information: ") + ex.Message;
        }
    }

    private void LoadConnection(WlanClient wlan, WlanInterfaceInfo nic)
    {
        try
        {
            var info = wlan.GetCurrentConnection(nic.Id);
            _locationHint.Visible = false;
            _connection.Text = info is null
                ? T($"{nic.Description}\n状态：{nic.State}", $"{nic.Description}\nState: {nic.State}")
                : string.Join('\n',
                    T($"网络：{info.Ssid}", $"Network: {info.Ssid}"),
                    T($"信号：{info.SignalQuality}%", $"Signal: {info.SignalQuality}%") + (info.RssiDbm is { } rssi ? T($"（{rssi} dBm）", $" ({rssi} dBm)") : string.Empty),
                    T("频段：", "Band: ") + (info.Channel is { } channel
                        ? WifiBands.FromChannel(channel) + T($"，信道 {channel}", $", channel {channel}")
                        : T("未知", "unknown")),
                    T($"协议：{info.PhyType}", $"Protocol: {info.PhyType}"),
                    T($"安全：{info.Authentication}，{info.Cipher}", $"Security: {info.Authentication}, {info.Cipher}"),
                    T($"连接速率：接收 {info.ReceiveRateKbps / 1000.0:0.#} Mbps / 发送 {info.TransmitRateKbps / 1000.0:0.#} Mbps", $"Link rate: receive {info.ReceiveRateKbps / 1000.0:0.#} Mbps / send {info.TransmitRateKbps / 1000.0:0.#} Mbps"),
                    $"BSSID：{info.Bssid}",
                    T($"网卡：{nic.Description}", $"Adapter: {nic.Description}"));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == WlanClient.ErrorAccessDenied)
        {
            _connection.Text = T($"{nic.Description}\n状态：{nic.State}", $"{nic.Description}\nState: {nic.State}");
            _locationHint.Visible = true;
        }
    }

    private void LoadProfiles(WlanClient wlan, Guid id)
    {
        _profiles.BeginUpdate();
        _profiles.Items.Clear();
        foreach (var name in wlan.GetProfileNames(id))
        {
            try
            {
                var profile = wlan.GetProfile(id, name, includeKey: false);
                _profiles.Items.Add(new ListViewItem(
                    [profile.Ssid, profile.SecurityLabel, profile.AutoConnect ? T("是", "Yes") : T("否", "No"), profile.Hidden ? T("是", "Yes") : T("否", "No")])
                {
                    Tag = name,
                });
            }
            catch (Exception ex) when (ex is Win32Exception or FormatException or System.Xml.XmlException)
            {
                _profiles.Items.Add(new ListViewItem([name, T("无法读取", "Unreadable"), string.Empty, string.Empty]) { Tag = name });
            }
        }

        _profiles.EndUpdate();
        OnSelectionChanged();
    }

    private string? SelectedProfileName => _profiles.SelectedItems.Count > 0 ? _profiles.SelectedItems[0].Tag as string : null;

    private void OnSelectionChanged()
    {
        if (SelectedProfileName != _revealedFor)
        {
            HidePassword();
        }

        _reveal.Enabled = _qr.Enabled = SelectedProfileName is not null && _interface is not null;
    }

    private void HidePassword()
    {
        _password.Text = string.Empty;
        _password.UseSystemPasswordChar = true;
        _copy.Enabled = false;
        _passwordNote.Text = string.Empty;
        _revealedFor = null;
    }

    /// <summary>Asks Windows for the profile with its key; returns null and explains in the UI if unavailable.</summary>
    private (WifiProfileInfo Profile, string? Key)? ReadSelectedWithKey()
    {
        if (SelectedProfileName is not { } name || _interface is not { } id)
        {
            return null;
        }

        try
        {
            using var wlan = new WlanClient();
            var profile = wlan.GetProfile(id, name, includeKey: true);
            if (profile.Key is null)
            {
                _passwordNote.Text = profile.IsOpen ? T("开放网络，没有密码。", "Open network, no password.")
                    : profile.IsEnterprise ? T("企业网络使用账户登录，没有共享密码。", "Enterprise networks sign in with an account; there is no shared password.")
                    : profile.KeyProtected ? (Elevation.IsAdministrator ? T("Windows 只返回了加密后的密码。", "Windows only returned the encrypted password.") : T("Windows 只返回了加密后的密码。以管理员身份运行 NetPulse 后可查看。", "Windows only returned the encrypted password. Run NetPulse as administrator to see it."))
                    : T("这个网络没有保存密码。", "No password is saved for this network.");
            }

            return (profile, profile.Key);
        }
        catch (Exception ex) when (ex is Win32Exception or FormatException or System.Xml.XmlException)
        {
            _passwordNote.Text = T("读取失败：", "Could not read it: ") + ex.Message;
            return null;
        }
    }

    private void RevealPassword()
    {
        HidePassword();
        if (ReadSelectedWithKey() is not { Key: { } key })
        {
            return;
        }

        _password.UseSystemPasswordChar = false;
        _password.Text = key;
        _copy.Enabled = true;
        _revealedFor = SelectedProfileName;
    }

    private void CopyPassword()
    {
        if (_password.Text.Length > 0)
        {
            Clipboard.SetText(_password.Text);
            _passwordNote.Text = T("已复制到剪贴板。", "Copied to the clipboard.");
        }
    }

    private void ShowQr()
    {
        if (ReadSelectedWithKey() is not { } result)
        {
            return;
        }

        if (!result.Profile.IsOpen && result.Key is null)
        {
            return; // the note already explains why
        }

        using var dialog = new WifiQrDialog(result.Profile, WifiQrPayload.Build(result.Profile, result.Key));
        dialog.ShowDialog(FindForm());
    }
}

/// <summary>Shows a Wi-Fi join QR code rendered from QRCoder's module matrix.</summary>
internal sealed class WifiQrDialog : Form
{
    private readonly bool[,] _modules;

    public WifiQrDialog(WifiProfileInfo profile, string payload)
    {
        Text = T("Wi-Fi 二维码 — ", "Wi-Fi QR code — ") + profile.Ssid;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(380, 460);
        BackColor = Color.White;

        using (var generator = new QRCodeGenerator())
        using (var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M))
        {
            var matrix = data.ModuleMatrix;
            _modules = new bool[matrix.Count, matrix.Count];
            for (var y = 0; y < matrix.Count; y++)
            {
                for (var x = 0; x < matrix.Count; x++)
                {
                    _modules[x, y] = matrix[y][x];
                }
            }
        }

        var canvas = new Panel { Dock = DockStyle.Fill };
        canvas.Paint += (_, e) => DrawCode(e.Graphics, canvas.ClientSize);
        canvas.Resize += (_, _) => canvas.Invalidate();
        var caption = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = T($"用手机相机扫描即可连接“{profile.Ssid}”。\n注意：任何扫描此二维码的人都能连接该网络。", $"Scan with a phone camera to join “{profile.Ssid}”.\nNote: anyone who scans this code can join the network."),
        };
        Controls.Add(canvas);
        Controls.Add(caption);
    }

    private void DrawCode(Graphics g, Size area)
    {
        var count = _modules.GetLength(0);
        var cell = Math.Max(1, (Math.Min(area.Width, area.Height) - 16) / count);
        var size = cell * count;
        var left = (area.Width - size) / 2;
        var top = (area.Height - size) / 2;
        g.Clear(Color.White);
        for (var y = 0; y < count; y++)
        {
            for (var x = 0; x < count; x++)
            {
                if (_modules[x, y])
                {
                    g.FillRectangle(Brushes.Black, left + (x * cell), top + (y * cell), cell, cell);
                }
            }
        }
    }
}
