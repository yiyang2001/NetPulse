namespace NetPulse.Core;

/// <summary>
/// A network traffic is attributed to: a Wi-Fi network by its name (SSID), anything else by adapter name.
/// The key is what is stored; the label is what is shown and may change.
/// </summary>
public readonly record struct NetworkIdentity(string Key, string Label)
{
    public static NetworkIdentity ForAdapter(AdapterSnapshot adapter) =>
        new("adapter:" + adapter.Name, adapter.Name);

    public static NetworkIdentity ForWifi(string ssid) => new("wifi:" + ssid, ssid);
}

public readonly record struct NetworkDelta(NetworkIdentity Network, long Downloaded, long Uploaded);

/// <summary>
/// Splits the bytes moved since the previous call by network. It counts exactly what
/// <see cref="SpeedSampler"/> counts (an adapter that is new or whose counters reset contributes nothing
/// for that step), so the networks always add up to the overall total.
/// </summary>
public sealed class NetworkSampler
{
    private Dictionary<string, TrafficCounters> _previous = new(StringComparer.Ordinal);

    public IReadOnlyList<NetworkDelta> Sample(IEnumerable<AdapterSnapshot> adapters, Func<AdapterSnapshot, NetworkIdentity> identify)
    {
        var totals = new Dictionary<string, (NetworkIdentity Network, long Down, long Up)>(StringComparer.Ordinal);
        var current = new Dictionary<string, TrafficCounters>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            current[adapter.Id] = adapter.Counters;
            if (!_previous.TryGetValue(adapter.Id, out var before))
            {
                continue;
            }

            var down = Math.Max(0, adapter.Counters.BytesReceived - before.BytesReceived);
            var up = Math.Max(0, adapter.Counters.BytesSent - before.BytesSent);
            if (down == 0 && up == 0)
            {
                continue;
            }

            var network = identify(adapter);
            totals.TryGetValue(network.Key, out var sum);
            totals[network.Key] = (network, sum.Down + down, sum.Up + up);
        }

        _previous = current;
        return [.. totals.Values.Select(t => new NetworkDelta(t.Network, t.Down, t.Up))];
    }

    public void Reset() => _previous = new Dictionary<string, TrafficCounters>(StringComparer.Ordinal);
}

public sealed class NetworkUsageEntry
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public List<DailyUsage> Days { get; set; } = [];
}

/// <summary>On-disk form of <see cref="NetworkUsageLedger"/>.</summary>
public sealed class NetworkUsageDocument
{
    public int Version { get; set; } = 1;

    public List<NetworkUsageEntry> Networks { get; set; } = [];
}

/// <summary>Daily totals per network. Not thread-safe; use from the UI thread.</summary>
public sealed class NetworkUsageLedger
{
    private readonly Dictionary<string, (string Label, UsageLedger Ledger)> _networks = new(StringComparer.Ordinal);
    private bool _structureDirty;

    public NetworkUsageLedger()
    {
    }

    public NetworkUsageLedger(IEnumerable<NetworkUsageEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (!string.IsNullOrEmpty(entry.Key))
            {
                _networks[entry.Key] = (entry.Label, new UsageLedger(entry.Days ?? []));
            }
        }
    }

    public bool IsDirty => _structureDirty || _networks.Values.Any(n => n.Ledger.IsDirty);

    /// <summary>Known networks, by label.</summary>
    public IReadOnlyList<NetworkIdentity> Networks =>
        [.. _networks.Select(n => new NetworkIdentity(n.Key, n.Value.Label))
            .OrderBy(n => n.Label, StringComparer.CurrentCultureIgnoreCase)];

    public UsageLedger? Get(string key) => _networks.TryGetValue(key, out var network) ? network.Ledger : null;

    public void Add(NetworkIdentity network, DateOnly date, long downloaded, long uploaded)
    {
        if (!_networks.TryGetValue(network.Key, out var existing))
        {
            existing = (network.Label, new UsageLedger());
            _structureDirty = true;
        }
        else if (existing.Label != network.Label)
        {
            existing = (network.Label, existing.Ledger);
            _structureDirty = true;
        }

        _networks[network.Key] = existing;
        existing.Ledger.Add(date, downloaded, uploaded);
    }

    public void Add(IEnumerable<NetworkDelta> deltas, DateOnly date)
    {
        foreach (var delta in deltas)
        {
            Add(delta.Network, date, delta.Downloaded, delta.Uploaded);
        }
    }

    /// <summary>Drops old days and then networks left without any.</summary>
    public void Prune(DateOnly keepFrom)
    {
        foreach (var (key, network) in _networks.ToList())
        {
            network.Ledger.Prune(keepFrom);
            if (network.Ledger.ToList().Count == 0)
            {
                _networks.Remove(key);
                _structureDirty = true;
            }
        }
    }

    public void Clear()
    {
        if (_networks.Count > 0)
        {
            _networks.Clear();
            _structureDirty = true;
        }
    }

    public NetworkUsageDocument ToDocument() => new()
    {
        Networks = [.. _networks.Select(n => new NetworkUsageEntry { Key = n.Key, Label = n.Value.Label, Days = [.. n.Value.Ledger.ToList()] })],
    };

    public void MarkSaved()
    {
        _structureDirty = false;
        foreach (var network in _networks.Values)
        {
            network.Ledger.MarkSaved();
        }
    }
}
