using System.Collections.Concurrent;
using System.Net.NetworkInformation;

namespace NetPulse.Core;

/// <summary>A network interface that can carry user traffic, with its current counters.</summary>
public sealed record AdapterSnapshot(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceType Type,
    bool IsLikelyVirtual,
    TrafficCounters Counters);

public static class NetworkAdapters
{
    // Traffic through these also passes a physical adapter, so counting both would double it.
    private static readonly string[] VirtualMarkers =
    [
        "virtual", "hyper-v", "vmware", "virtualbox", "vethernet", "wsl", "loopback",
        "tap-windows", "wintun", "wireguard", "openvpn", "tailscale", "zerotier", "npcap",
        "bluetooth", "wi-fi direct", "teredo", "isatap", "pseudo",
    ];

    /// <summary>Reads all operational, non-loopback, non-tunnel adapters.</summary>
    public static IReadOnlyList<AdapterSnapshot> ReadAll()
    {
        var result = new List<AdapterSnapshot>();
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return result;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            try
            {
                // GetIPStatistics covers IPv4 and IPv6 with 64-bit counters.
                var stats = nic.GetIPStatistics();
                result.Add(new AdapterSnapshot(
                    nic.Id,
                    nic.Name,
                    nic.Description,
                    nic.NetworkInterfaceType,
                    IsVirtual(nic.Name, nic.Description),
                    new TrafficCounters(stats.BytesReceived, stats.BytesSent)));
            }
            catch (NetworkInformationException)
            {
                // The adapter went away between enumeration and the statistics query.
            }
        }

        return result;
    }

    // Adapter names almost never change, but ReadAll runs every sampling tick.
    private static readonly ConcurrentDictionary<(string, string), bool> VirtualCache = new();

    public static bool IsVirtual(string name, string description) =>
        VirtualCache.Count > 512 ? ComputeIsVirtual(name, description)
            : VirtualCache.GetOrAdd((name, description), static k => ComputeIsVirtual(k.Item1, k.Item2));

    private static bool ComputeIsVirtual(string name, string description)
    {
        foreach (var marker in VirtualMarkers)
        {
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase)
                || description.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Selects the counters to measure: a specific adapter when <paramref name="adapterId"/> is set and present,
    /// otherwise every adapter (excluding likely-virtual ones when <paramref name="excludeVirtual"/> is true).
    /// </summary>
    public static Dictionary<string, TrafficCounters> Select(
        IReadOnlyList<AdapterSnapshot> adapters, string? adapterId, bool excludeVirtual)
    {
        var selected = new Dictionary<string, TrafficCounters>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(adapterId))
        {
            foreach (var adapter in adapters)
            {
                if (adapter.Id == adapterId)
                {
                    selected[adapter.Id] = adapter.Counters;
                    return selected;
                }
            }
        }

        foreach (var adapter in adapters)
        {
            if (!(excludeVirtual && adapter.IsLikelyVirtual))
            {
                selected[adapter.Id] = adapter.Counters;
            }
        }

        return selected;
    }
}
