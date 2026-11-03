using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetPulse.Core;
using NetPulse.Platform;
using static NetPulse.Core.Lang;

namespace NetPulse;

/// <summary>Checks which TCP ports of a host accept connections. Only connects to what the user typed in.</summary>
internal sealed class PortTool : ToolPanel
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4); // Windows retries a refused connect for ~2 s
    private static readonly Dictionary<int, string> WellKnown = new()
    {
        [21] = "FTP", [22] = "SSH", [23] = "Telnet", [25] = "SMTP", [53] = "DNS", [80] = "HTTP", [110] = "POP3",
        [143] = "IMAP", [443] = "HTTPS", [445] = "SMB", [993] = "IMAPS", [995] = "POP3S", [1433] = "SQL Server",
        [3306] = "MySQL", [3389] = "RDP", [5432] = "PostgreSQL", [5900] = "VNC", [8080] = "HTTP alt",
    };

    private readonly TextBox _host;
    private readonly TextBox _ports;

    public PortTool()
        : this(
            new TextBox { Width = 220, PlaceholderText = T("域名或 IP", "Host name or IP") },
            new TextBox { Width = 200, Text = "80, 443", PlaceholderText = T("例如 80, 443, 8000-8010", "e.g. 80, 443, 8000-8010") })
    {
    }

    private PortTool(TextBox host, TextBox ports)
        : base(T("检测", "Check"), new Label { Text = T("目标", "Target") }, host, new Label { Text = T("端口", "Ports") }, ports)
    {
        _host = host;
        _ports = ports;
    }

    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        var host = _host.Text.Trim();
        if (host.Length == 0)
        {
            Output.AppendLine(T("请输入目标。", "Enter a target."));
            return;
        }

        IReadOnlyList<int> ports;
        try
        {
            ports = PortSpec.Parse(_ports.Text);
        }
        catch (FormatException)
        {
            Output.AppendLine(T($"端口格式不对。示例：80, 443, 8000-8010（最多 {PortSpec.MaxPorts} 个）。", $"Not a valid port list. Example: 80, 443, 8000-8010 (at most {PortSpec.MaxPorts})."));
            return;
        }

        var address = await NetworkToolsPage.ResolveAsync(host, cancellationToken);
        Output.AppendLine(T($"正在检测 {host} [{address}] 的 {ports.Count} 个 TCP 端口…", $"Checking {ports.Count} TCP port(s) of {host} [{address}]…"));
        var listAll = ports.Count <= 30;
        var results = await PortChecker.CheckManyAsync(
            address,
            ports,
            Timeout,
            result =>
            {
                // Long lists would look frozen until the end, so open ports are shown as they are found.
                if (!listAll && result.State == PortState.Open && !IsDisposed)
                {
                    BeginInvoke(() => Output.AppendLine("  " + Describe(result)));
                }
            },
            cancellationToken);

        if (listAll)
        {
            foreach (var result in results)
            {
                Output.AppendLine("  " + Describe(result));
            }
        }

        var open = results.Count(r => r.State == PortState.Open);
        var closed = results.Count(r => r.State == PortState.Closed);
        var timeout = results.Count(r => r.State == PortState.Timeout);
        Output.AppendLine(T($"结果：{open} 个开放，{closed} 个关闭，{timeout} 个无响应，{results.Count - open - closed - timeout} 个出错。", $"Result: {open} open, {closed} closed, {timeout} without answer, {results.Count - open - closed - timeout} failed."));
        if (timeout > 0)
        {
            Output.AppendLine(T("“无响应”通常是防火墙丢弃了数据包，或者主机已关机。", "“No answer” usually means a firewall drops the packets, or the host is off."));
        }

        Output.AppendLine(string.Empty);
    }

    private static string Describe(PortResult result)
    {
        var name = WellKnown.TryGetValue(result.Port, out var service) ? $" ({service})" : string.Empty;
        var state = result.State switch
        {
            PortState.Open => T($"开放，{result.Milliseconds:0} ms", $"open, {result.Milliseconds:0} ms"),
            PortState.Closed => T("关闭", "closed"),
            PortState.Timeout => T("无响应", "no answer"),
            _ => T($"出错：{result.Detail}", $"error: {result.Detail}"),
        };
        return $"{result.Port,5}{name}: {state}";
    }
}

/// <summary>
/// Devices on the local network, from this PC's ARP table, optionally after pinging every address once so
/// that quiet devices show up too. Everything stays on the local network and starts only on click.
/// </summary>
internal sealed class LanDevicesTool : UserControl
{
    private readonly Button _scan = new() { Text = T("扫描", "Scan"), AutoSize = true };
    private readonly CheckBox _probe = new() { Text = T("主动探测（向每个地址发送一次 Ping）", "Probe actively (one ping to every address)"), Checked = true, AutoSize = true };
    private readonly CheckBox _names = new() { Text = T("查询设备名称", "Look up device names"), Checked = true, AutoSize = true };
    private readonly ProgressBar _progress = new() { Width = 160, Height = 18, Maximum = 1000 };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly ListView _list = new FlickerFreeListView
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private CancellationTokenSource? _running;

    public LanDevicesTool()
    {
        _list.Columns.Add("IP", 130);
        _list.Columns.Add(T("名称", "Name"), 200);
        _list.Columns.Add("MAC", 150);
        _list.Columns.Add(T("备注", "Note"), 150);

        Controls.Add(_list);
        Controls.Add(Ui.Note(T("只在本地网络内进行：读取本机的 ARP 表；勾选主动探测时，向本网段的每个地址发送一次 Ping。不会访问互联网。名称只有在路由器支持反向 DNS 时才会显示。", "Local only: reads this PC's ARP table and, with active probing, sends one ping to each address of this subnet. Nothing goes to the internet. Names show only when your router answers reverse DNS lookups.")));
        Controls.Add(Ui.Toolbar(_progress, _status));
        Controls.Add(Ui.Toolbar(_scan, _probe, _names));
        _scan.Click += async (_, _) => await ScanOrStopAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _running?.Cancel();
        }

        base.Dispose(disposing);
    }

    private async Task ScanOrStopAsync()
    {
        if (_running is not null)
        {
            await _running.CancelAsync();
            return;
        }

        var network = LanNetwork.Primary(NetworkInterface.GetAllNetworkInterfaces());
        if (network is null)
        {
            _status.Text = T("没有找到可用的本地网络（需要有网关的 IPv4 网卡）。", "No local network found (needs an IPv4 adapter with a gateway).");
            return;
        }

        using var cts = new CancellationTokenSource();
        _running = cts;
        _scan.Text = T("停止", "Stop");
        _progress.Value = 0;
        _status.Text = T($"正在扫描 {network.Address}/{network.PrefixLength}…", $"Scanning {network.Address}/{network.PrefixLength}…");
        try
        {
            var devices = await LanScanner.ScanAsync(
                network,
                ArpTable.Read,
                _probe.Checked ? PingAsync : null,
                _names.Checked ? ResolveNameAsync : null,
                new Progress<double>(fraction => _progress.Value = (int)Math.Clamp(fraction * 1000, 0, 1000)),
                cts.Token);
            Show(devices);
            _progress.Value = _progress.Maximum;
            _status.Text = T($"找到 {devices.Count} 台设备（含本机），{DateTime.Now:HH:mm:ss}", $"{devices.Count} devices found (including this PC), {DateTime.Now:HH:mm:ss}");
        }
        catch (OperationCanceledException)
        {
            _status.Text = T("已停止。", "Stopped.");
        }
        finally
        {
            _running = null;
            _scan.Text = T("扫描", "Scan");
        }
    }

    private void Show(IReadOnlyList<LanDevice> devices)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var device in devices)
        {
            var note = device.IsThisPc ? T("本机", "This PC") : device.IsGateway ? T("网关（路由器）", "Gateway (router)") : string.Empty;
            _list.Items.Add(new ListViewItem([device.Address.ToString(), device.HostName ?? string.Empty, device.Mac, note]));
        }

        _list.EndUpdate();
    }

    private static async Task PingAsync(IPAddress address, CancellationToken cancellationToken)
    {
        // The reply does not matter: sending the packet makes Windows ask ARP who has this address.
        try
        {
            using var ping = new Ping();
            await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(500), new byte[16], new PingOptions(64, true), cancellationToken);
        }
        catch (PingException)
        {
        }
    }

    private static async Task<string?> ResolveNameAsync(IPAddress address, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1.5));
        try
        {
            // The lookup itself cannot be cancelled; on timeout we just stop waiting for it.
            var entry = await Dns.GetHostEntryAsync(address).WaitAsync(timeout.Token);
            return string.IsNullOrEmpty(entry.HostName) || entry.HostName == address.ToString() ? null : entry.HostName;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
