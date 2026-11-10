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
            await Task.WhenAll(gateway, external);
            var probe = new QualityProbe(network, gateway.Result, settings.ProbeExternal, external.Result);
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
                candidates.Add(new GatewayCandidate(adapter, index, gateway));
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

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterface(uint destinationAddress, out uint bestIfIndex);
}
