using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetPulse.Core;

/// <summary>An entry of the ARP table: a neighbour this PC has recently talked to.</summary>
public sealed record ArpEntry(IPAddress Address, string Mac);

public sealed record LanDevice(IPAddress Address, string Mac, string? HostName, bool IsGateway, bool IsThisPc);

/// <summary>The IPv4 network this PC is on.</summary>
public sealed record LanNetwork(IPAddress Address, int PrefixLength, IPAddress? Gateway)
{
    public uint AddressValue => ToValue(Address);

    public uint Mask => PrefixLength <= 0 ? 0 : uint.MaxValue << (32 - PrefixLength);

    public uint NetworkValue => AddressValue & Mask;

    public uint BroadcastValue => NetworkValue | ~Mask;

    public bool Contains(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && (ToValue(address) & Mask) == NetworkValue;

    /// <summary>
    /// Every other host address of the network, nearest first not required. Networks bigger than /24 are
    /// narrowed to the /24 around this PC: sweeping thousands of addresses is neither quick nor polite.
    /// </summary>
    public IReadOnlyList<IPAddress> Hosts()
    {
        var prefix = Math.Max(PrefixLength, 24);
        if (prefix >= 31)
        {
            return [];
        }

        var mask = uint.MaxValue << (32 - prefix);
        var network = AddressValue & mask;
        var broadcast = network | ~mask;
        var hosts = new List<IPAddress>();
        for (var value = network + 1; value < broadcast; value++)
        {
            if (value != AddressValue)
            {
                hosts.Add(FromValue(value));
            }
        }

        return hosts;
    }

    /// <summary>The first IPv4 interface that is up, has a gateway and is not virtual, or null.</summary>
    public static LanNetwork? Primary(IEnumerable<NetworkInterface> interfaces)
    {
        foreach (var nic in interfaces
                     .Where(n => n.OperationalStatus == OperationalStatus.Up
                                 && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                                 && !NetworkAdapters.IsVirtual(n.Name, n.Description)))
        {
            var properties = nic.GetIPProperties();
            var gateway = properties.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            var unicast = properties.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
            if (gateway is not null && unicast is not null && unicast.PrefixLength is > 0 and < 31)
            {
                return new LanNetwork(unicast.Address, unicast.PrefixLength, gateway);
            }
        }

        return null;
    }

    public static uint ToValue(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    public static IPAddress FromValue(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}

/// <summary>Finds the devices on the local network: optionally pings every address to wake the ARP table, then reads it.</summary>
public static class LanScanner
{
    /// <summary>ARP rows that are real unicast neighbours of <paramref name="network"/>, not broadcast, multicast or empty.</summary>
    public static IReadOnlyList<ArpEntry> UsableNeighbours(IEnumerable<ArpEntry> table, LanNetwork network) =>
        [.. table
            .Where(e => network.Contains(e.Address)
                        && LanNetwork.ToValue(e.Address) != network.BroadcastValue
                        && LanNetwork.ToValue(e.Address) != network.NetworkValue
                        && !IsEmptyOrBroadcastMac(e.Mac))
            .GroupBy(e => LanNetwork.ToValue(e.Address))
            .Select(g => g.First())
            .OrderBy(e => LanNetwork.ToValue(e.Address))];

    /// <param name="readArp">Reads the system ARP table.</param>
    /// <param name="probe">Pings an address; any reply or even a failure still makes the OS ask ARP, which is all we want.</param>
    /// <param name="resolveName">Reverse lookup, or null to skip names.</param>
    public static async Task<IReadOnlyList<LanDevice>> ScanAsync(
        LanNetwork network,
        Func<IReadOnlyList<ArpEntry>> readArp,
        Func<IPAddress, CancellationToken, Task>? probe,
        Func<IPAddress, CancellationToken, Task<string?>>? resolveName,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        int concurrency = 48)
    {
        if (probe is not null)
        {
            var hosts = network.Hosts();
            var done = 0;
            await Parallel.ForEachAsync(
                hosts,
                new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken },
                async (host, token) =>
                {
                    await probe(host, token).ConfigureAwait(false);
                    progress?.Report(Interlocked.Increment(ref done) / (double)hosts.Count);
                }).ConfigureAwait(false);
        }

        var neighbours = UsableNeighbours(readArp(), network);
        var names = new Dictionary<uint, string?>();
        if (resolveName is not null)
        {
            await Parallel.ForEachAsync(
                neighbours,
                new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken },
                async (entry, token) =>
                {
                    var name = await resolveName(entry.Address, token).ConfigureAwait(false);
                    lock (names)
                    {
                        names[LanNetwork.ToValue(entry.Address)] = name;
                    }
                }).ConfigureAwait(false);
        }

        var devices = neighbours
            .Select(e => new LanDevice(
                e.Address,
                e.Mac,
                names.GetValueOrDefault(LanNetwork.ToValue(e.Address)),
                network.Gateway is not null && e.Address.Equals(network.Gateway),
                IsThisPc: false))
            .ToList();
        // This PC is never in its own ARP table.
        devices.Add(new LanDevice(network.Address, string.Empty, Dns.GetHostName(), IsGateway: false, IsThisPc: true));
        return [.. devices.OrderBy(d => LanNetwork.ToValue(d.Address))];
    }

    private static bool IsEmptyOrBroadcastMac(string mac)
    {
        var digits = mac.Where(char.IsAsciiHexDigit).ToArray();
        return digits.Length < 12 || digits.All(c => c == '0') || digits.All(c => c is 'F' or 'f');
    }
}
