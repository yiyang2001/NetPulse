using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetPulse.Core;

namespace NetPulse.Platform;

/// <summary>
/// One connection-quality round: finds the default gateway, pings it and, if enabled, the internet address.
/// Pings are plain ICMP echo requests (no administrator rights needed), 1 second timeout, 32 bytes.
/// </summary>
internal sealed class QualityProbeService(WifiNetworkNames networkNames)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private static readonly byte[] Payload = new byte[32];

    public bool IsBusy { get; private set; }

    /// <summary>The latest round, for the page and the tray tooltip.</summary>
    public QualityProbe? Last { get; private set; }

    public IPAddress? Gateway { get; private set; }

    public async Task<QualityProbe> ProbeAsync(ConnectionQualitySettings settings, CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            var target = FindGateway();
            Gateway = target?.Gateway;
            if (target is null)
            {
                Last = QualityProbe.NotConnected;
                return QualityProbe.NotConnected;
            }

            var network = networkNames.Identify(target.Adapter);
            var gateway = PingAsync(target.Gateway, cancellationToken);
            var external = settings.ProbeExternal ? PingHostAsync(settings.ExternalTarget, cancellationToken) : Task.FromResult<double?>(null);
            var dnsProbed = settings.ProbeDns && target.DnsServer is not null;
            var dns = dnsProbed ? QueryDnsAsync(target.DnsServer!, cancellationToken) : Task.FromResult(false);
            await Task.WhenAll(gateway, external, dns);
            var probe = new QualityProbe(network, gateway.Result, settings.ProbeExternal, external.Result, WifiSignal(target.Adapter), dnsProbed, dns.Result);
            Last = probe;
            return probe;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Reset()
    {
        Last = null;
        Gateway = null;
    }

    private static GatewayCandidate? FindGateway()
    {
        var candidates = new List<GatewayCandidate>();
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return null;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            try
            {
                var properties = nic.GetIPProperties();
                var gateways = properties.GatewayAddresses.Select(g => g.Address)
                    .Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any))
                    .ToList();
                // IPv4 first: an IPv6 link-local gateway also works, but some routers rate-limit ICMPv6 to it.
                var gateway = gateways.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? gateways.FirstOrDefault();
                if (gateway is null)
                {
                    continue;
                }

                var index = properties.GetIPv4Properties()?.Index ?? properties.GetIPv6Properties()?.Index ?? -1;
                var adapter = new AdapterSnapshot(nic.Id, nic.Name, nic.Description, nic.NetworkInterfaceType,
                    NetworkAdapters.IsVirtual(nic.Name, nic.Description), new TrafficCounters(0, 0));
                var dnsServers = properties.DnsAddresses.Where(a => !a.IsIPv6SiteLocal).ToList();
                var dnsServer = dnsServers.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? dnsServers.FirstOrDefault();
                candidates.Add(new GatewayCandidate(adapter, index, gateway, dnsServer));
            }
            catch (Exception ex) when (ex is NetworkInformationException or NotSupportedException)
            {
                // The adapter went away, or has no IPv4/IPv6 stack bound.
            }
        }

        // 1.1.1.1 only selects the route table entry; nothing is sent.
        int? best = GetBestInterface(0x01010101, out var bestIndex) == 0 ? (int)bestIndex : null;
        return GatewayChoice.Pick(candidates, best);
    }

    private static async Task<double?> PingHostAsync(string host, CancellationToken cancellationToken)
    {
        IPAddress? address;
        if (!IPAddress.TryParse(host, out address))
        {
            try
            {
                address = (await Dns.GetHostAddressesAsync(host, cancellationToken)).FirstOrDefault();
            }
            catch (SocketException)
            {
                return null; // no name resolution counts as no reply
            }
        }

        return address is null ? null : await PingAsync(address, cancellationToken);
    }

    private static async Task<double?> PingAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, Timeout, Payload, new PingOptions(64, true), cancellationToken);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (PingException)
        {
            return null;
        }
    }

    /// <summary>Signal quality of the current Wi-Fi connection; null for other adapters or when Windows withholds it (location permission).</summary>
    private static int? WifiSignal(AdapterSnapshot adapter)
    {
        if (adapter.Type != NetworkInterfaceType.Wireless80211 || !Guid.TryParse(adapter.Id, out var id))
        {
            return null;
        }

        try
        {
            using var wlan = new WlanClient();
            return wlan.GetCurrentConnection(id) is { } connection ? (int)Math.Min(100, connection.SignalQuality) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Sends one query straight to <paramref name="server"/> (not through the Windows DNS cache); true if it answered within the timeout.</summary>
    private static async Task<bool> QueryDnsAsync(IPAddress server, CancellationToken cancellationToken)
    {
        var id = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var udp = new UdpClient(server.AddressFamily);
            await udp.SendAsync(DnsProbe.BuildQuery(id), new IPEndPoint(server, 53), timeout.Token);
            while (true)
            {
                var reply = await udp.ReceiveAsync(timeout.Token);
                if (reply.RemoteEndPoint.Address.Equals(server) && DnsProbe.IsAnswer(reply.Buffer, id))
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false; // no answer within the timeout
        }
        catch (SocketException)
        {
            return false; // e.g. ICMP port unreachable: nothing listens on port 53 there
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterface(uint destinationAddress, out uint bestIfIndex);
}
