using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using NetPulse.Core;

namespace NetPulse;

/// <summary>Ping, traceroute, DNS lookup and local adapter details. Every network action is user-initiated.</summary>
internal sealed class NetworkToolsPage : UserControl
{
    public NetworkToolsPage()
    {
        Padding = new Padding(12);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Wrap("Ping", new PingTool()));
        tabs.TabPages.Add(Wrap("路由追踪", new TraceRouteTool()));
        tabs.TabPages.Add(Wrap("DNS 查询", new DnsTool()));
        tabs.TabPages.Add(Wrap("本机网络", new LocalNetworkTool()));
        Controls.Add(tabs);
    }

    private static TabPage Wrap(string title, Control tool)
    {
        tool.Dock = DockStyle.Fill;
        var page = new TabPage(title) { Padding = new Padding(8) };
        page.Controls.Add(tool);
        return page;
    }

    /// <summary>Resolves a host name or literal address, preferring IPv4 as ping.exe does.</summary>
    internal static async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return literal;
        }

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    internal static string Describe(IPStatus status) => status switch
    {
        IPStatus.TimedOut => "请求超时",
        IPStatus.DestinationHostUnreachable => "目标主机不可达",
        IPStatus.DestinationNetworkUnreachable => "目标网络不可达",
        IPStatus.DestinationUnreachable => "目标不可达",
        IPStatus.TtlExpired or IPStatus.TimeExceeded => "TTL 已过期",
        IPStatus.PacketTooBig => "数据包过大",
        IPStatus.BadDestination => "目标地址无效",
        _ => status.ToString(),
    };
}

/// <summary>Shared layout: input row, start/stop button, output box, and cancellation.</summary>
internal abstract class ToolPanel : UserControl
{
    private readonly Button _run = new() { AutoSize = true };
    private readonly string _runText;
    private CancellationTokenSource? _cts;

    protected ToolPanel(string runText, params Control[] inputs)
    {
        _runText = runText;
        _run.Text = runText;
        Output = Ui.OutputBox();
        Controls.Add(Output);
        Controls.Add(Ui.Toolbar([.. inputs, _run]));
        _run.Click += async (_, _) => await RunOrStopAsync();
        foreach (var input in inputs.OfType<TextBox>())
        {
            input.KeyDown += async (_, e) =>
            {
                if (e.KeyCode == Keys.Enter && _cts is null)
                {
                    e.SuppressKeyPress = true;
                    await RunOrStopAsync();
                }
            };
        }
    }

    protected TextBox Output { get; }

    protected abstract Task RunAsync(CancellationToken cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
        }

        base.Dispose(disposing);
    }

    private async Task RunOrStopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        _run.Text = "停止";
        try
        {
            await RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Output.AppendLine("已停止。");
        }
        catch (Exception ex) when (ex is SocketException or PingException or ArgumentException or HttpRequestException)
        {
            Output.AppendLine("出错：" + (ex.InnerException?.Message ?? ex.Message));
        }
        finally
        {
            _cts = null;
            _run.Text = _runText;
        }
    }
}

internal sealed class PingTool : ToolPanel
{
    private readonly TextBox _host;
    private readonly NumericUpDown _count;

    public PingTool()
        : this(new TextBox { Width = 260, PlaceholderText = "域名或 IP，例如 1.1.1.1" }, new NumericUpDown { Minimum = 1, Maximum = 1000, Value = 4, Width = 70 })
    {
    }

    private PingTool(TextBox host, NumericUpDown count)
        : base("开始", new Label { Text = "目标" }, host, new Label { Text = "次数" }, count)
    {
        _host = host;
        _count = count;
    }

    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        var host = _host.Text.Trim();
        if (host.Length == 0)
        {
            Output.AppendLine("请输入目标。");
            return;
        }

        var address = await NetworkToolsPage.ResolveAsync(host, cancellationToken);
        Output.AppendLine($"正在 Ping {host} [{address}]，32 字节数据：");
        var results = new List<double?>();
        using var ping = new Ping();
        var buffer = new byte[32];
        for (var i = 0; i < (int)_count.Value; i++)
        {
            if (i > 0)
            {
                await Task.Delay(1000, cancellationToken);
            }

            var reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(2), buffer, new PingOptions(128, true), cancellationToken);
            if (reply.Status == IPStatus.Success)
            {
                results.Add(reply.RoundtripTime);
                var ttl = reply.Options?.Ttl is { } t ? $" TTL={t}" : string.Empty;
                Output.AppendLine($"来自 {reply.Address} 的回复：时间={reply.RoundtripTime} ms{ttl}");
            }
            else
            {
                results.Add(null);
                Output.AppendLine(NetworkToolsPage.Describe(reply.Status) + "。");
            }
        }

        var summary = PingSummary.From(results);
        Output.AppendLine($"统计：已发送 {summary.Sent}，已接收 {summary.Received}，丢失 {summary.Lost}（{summary.LossFraction:P0}）");
        if (summary.AverageMs is { } average)
        {
            Output.AppendLine($"往返时间：最短 {summary.MinMs:0} ms，最长 {summary.MaxMs:0} ms，平均 {average:0} ms");
        }

        Output.AppendLine(string.Empty);
    }
}

internal sealed class TraceRouteTool : ToolPanel
{
    private const int MaxHops = 30;
    private readonly TextBox _host;
    private readonly CheckBox _resolveNames;

    public TraceRouteTool()
        : this(new TextBox { Width = 260, PlaceholderText = "域名或 IP" }, new CheckBox { Text = "解析节点名称", Checked = true, AutoSize = true })
    {
    }

    private TraceRouteTool(TextBox host, CheckBox resolveNames)
        : base("开始", new Label { Text = "目标" }, host, resolveNames)
    {
        _host = host;
        _resolveNames = resolveNames;
    }

    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        var host = _host.Text.Trim();
        if (host.Length == 0)
        {
            Output.AppendLine("请输入目标。");
            return;
        }

        var target = await NetworkToolsPage.ResolveAsync(host, cancellationToken);
        Output.AppendLine($"通过最多 {MaxHops} 个跃点跟踪到 {host} [{target}] 的路由：");
        using var ping = new Ping();
        var buffer = new byte[32];
        for (var ttl = 1; ttl <= MaxHops; ttl++)
        {
            var stopwatch = Stopwatch.StartNew();
            var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(1500), buffer, new PingOptions(ttl, true), cancellationToken);
            stopwatch.Stop();

            if (reply.Status is IPStatus.TimedOut)
            {
                Output.AppendLine($"{ttl,3}  *        请求超时");
                continue;
            }

            // Windows reports no round-trip time for "TTL expired" replies; measure it ourselves.
            var rtt = reply.RoundtripTime > 0 ? reply.RoundtripTime : stopwatch.ElapsedMilliseconds;
            var name = _resolveNames.Checked ? await ReverseLookupAsync(reply.Address, cancellationToken) : null;
            var node = name is null ? reply.Address.ToString() : $"{name} [{reply.Address}]";
            Output.AppendLine($"{ttl,3}  {rtt,4} ms  {node}");

            if (reply.Status == IPStatus.Success || reply.Address.Equals(target))
            {
                Output.AppendLine("跟踪完成。" + Environment.NewLine);
                return;
            }

            if (reply.Status is not (IPStatus.TtlExpired or IPStatus.TimeExceeded))
            {
                Output.AppendLine($"     {NetworkToolsPage.Describe(reply.Status)}，停止跟踪。" + Environment.NewLine);
                return;
            }
        }

        Output.AppendLine("已达到最大跃点数。" + Environment.NewLine);
    }

    private static async Task<string?> ReverseLookupAsync(IPAddress address, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), timeout.Token);
            return entry.HostName == address.ToString() ? null : entry.HostName;
        }
        catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }
}

internal sealed class DnsTool : ToolPanel
{
    private readonly TextBox _query;

    public DnsTool()
        : this(new TextBox { Width = 300, PlaceholderText = "域名（正向查询）或 IP（反向查询）" })
    {
    }

    private DnsTool(TextBox query)
        : base("查询", new Label { Text = "查询" }, query)
    {
        _query = query;
    }

    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        var query = _query.Text.Trim();
        if (query.Length == 0)
        {
            Output.AppendLine("请输入要查询的域名或 IP。");
            return;
        }

        var servers = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().DnsAddresses)
            .Where(a => !a.IsIPv6SiteLocal) // fec0:0:0:ffff::1-3 are placeholders on adapters without DNS
            .Distinct()
            .ToList();
        Output.AppendLine("使用系统 DNS 服务器：" + (servers.Count > 0 ? string.Join(", ", servers) : "（未知）"));

        var stopwatch = Stopwatch.StartNew();
        if (IPAddress.TryParse(query, out var address))
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), cancellationToken);
            Output.AppendLine($"{address} 的反向解析（{stopwatch.ElapsedMilliseconds} ms）：");
            Output.AppendLine("  " + entry.HostName);
            foreach (var alias in entry.Aliases)
            {
                Output.AppendLine("  别名 " + alias);
            }
        }
        else
        {
            var addresses = await Dns.GetHostAddressesAsync(query, cancellationToken);
            Output.AppendLine($"{query} 的解析结果（{stopwatch.ElapsedMilliseconds} ms）：");
            foreach (var a in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6))
            {
                Output.AppendLine($"  {(a.AddressFamily == AddressFamily.InterNetworkV6 ? "AAAA" : "A   ")}  {a}");
            }
        }

        Output.AppendLine(string.Empty);
    }
}

internal sealed class LocalNetworkTool : UserControl
{
    private static readonly HttpClient Http = CreateHttpClient();
    private readonly TextBox _output = Ui.OutputBox();
    private readonly Button _publicIp = new() { Text = "查询公网 IP（连接 Cloudflare）", AutoSize = true };

    public LocalNetworkTool()
    {
        var refresh = new Button { Text = "刷新", AutoSize = true };
        refresh.Click += (_, _) => ShowAdapters();
        _publicIp.Click += async (_, _) => await ShowPublicIpAsync();
        Controls.Add(_output);
        Controls.Add(Ui.Toolbar(refresh, _publicIp));
        ShowAdapters();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NetPulse/0.2");
        return client;
    }

    private void ShowAdapters()
    {
        var text = new StringBuilder();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                     .OrderBy(n => NetworkAdapters.IsVirtual(n.Name, n.Description)))
        {
            var ip = nic.GetIPProperties();
            text.AppendLine($"■ {nic.Name}" + (NetworkAdapters.IsVirtual(nic.Name, nic.Description) ? "（虚拟）" : string.Empty));
            text.AppendLine($"  描述      {nic.Description}");
            text.AppendLine($"  类型      {nic.NetworkInterfaceType}，{FormatLinkSpeed(nic.Speed)}");
            var mac = nic.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length > 0)
            {
                text.AppendLine($"  MAC 地址  {string.Join('-', mac.Select(b => b.ToString("X2")))}");
            }

            foreach (var unicast in ip.UnicastAddresses)
            {
                var label = unicast.Address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
                text.AppendLine($"  {label,-8}  {unicast.Address}/{unicast.PrefixLength}");
            }

            foreach (var gateway in ip.GatewayAddresses)
            {
                text.AppendLine($"  网关      {gateway.Address}");
            }

            foreach (var dns in ip.DnsAddresses)
            {
                text.AppendLine($"  DNS       {dns}");
            }

            try
            {
                text.AppendLine($"  DHCP      {(ip.GetIPv4Properties().IsDhcpEnabled ? "是" : "否")}");
            }
            catch (NetworkInformationException)
            {
            }

            text.AppendLine();
        }

        _output.Text = text.ToString();
    }

    private static string FormatLinkSpeed(long bitsPerSecond) =>
        bitsPerSecond <= 0 ? "速率未知" : ByteFormatter.FormatSpeed(bitsPerSecond / 8.0, SpeedUnit.Bits);

    /// <summary>Cloudflare's trace endpoint echoes the caller's public address and nearest data center.</summary>
    private async Task ShowPublicIpAsync()
    {
        _publicIp.Enabled = false;
        try
        {
            var body = await Http.GetStringAsync("https://1.1.1.1/cdn-cgi/trace");
            var fields = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1].Trim());
            _output.AppendLine("■ 公网信息（来自 Cloudflare）");
            _output.AppendLine($"  公网 IP   {fields.GetValueOrDefault("ip", "未知")}");
            _output.AppendLine($"  地区      {fields.GetValueOrDefault("loc", "未知")}");
            _output.AppendLine($"  节点      {fields.GetValueOrDefault("colo", "未知")}");
            _output.AppendLine(string.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _output.AppendLine("查询公网 IP 失败：" + ex.Message);
        }
        finally
        {
            _publicIp.Enabled = true;
        }
    }
}
