using System.Collections.Concurrent;

namespace NetPulse.Core;

/// <summary>
/// Thread-safe running byte totals per process id. The ETW callback thread adds to it;
/// the UI thread takes snapshots.
/// </summary>
public sealed class ProcessTrafficTable
{
    private readonly ConcurrentDictionary<int, Counter> _counters = new();

    public void Add(int processId, long received, long sent)
    {
        var counter = _counters.GetOrAdd(processId, static _ => new Counter());
        if (received > 0)
        {
            Interlocked.Add(ref counter.Received, received);
        }

        if (sent > 0)
        {
            Interlocked.Add(ref counter.Sent, sent);
        }
    }

    public Dictionary<int, TrafficCounters> Snapshot()
    {
        var result = new Dictionary<int, TrafficCounters>(_counters.Count);
        foreach (var (pid, counter) in _counters)
        {
            result[pid] = new TrafficCounters(Interlocked.Read(ref counter.Received), Interlocked.Read(ref counter.Sent));
        }

        return result;
    }

    public void Clear() => _counters.Clear();

    private sealed class Counter
    {
        public long Received;
        public long Sent;
    }
}

public sealed record ProcessRate(
    int ProcessId,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    long TotalDownloaded,
    long TotalUploaded)
{
    public double TotalBytesPerSecond => DownloadBytesPerSecond + UploadBytesPerSecond;
}

/// <summary>Turns successive <see cref="ProcessTrafficTable"/> snapshots into per-process rates.</summary>
public sealed class ProcessRateSampler
{
    private Dictionary<int, TrafficCounters> _previous = [];
    private TimeSpan? _previousTimestamp;

    public IReadOnlyList<ProcessRate> Sample(IReadOnlyDictionary<int, TrafficCounters> current, TimeSpan timestamp)
    {
        var elapsed = _previousTimestamp is { } before ? (timestamp - before).TotalSeconds : 0;
        var rates = new List<ProcessRate>(current.Count);
        foreach (var (pid, counters) in current)
        {
            double down = 0;
            double up = 0;
            if (elapsed > 0 && _previous.TryGetValue(pid, out var previous))
            {
                down = Math.Max(0, counters.BytesReceived - previous.BytesReceived) / elapsed;
                up = Math.Max(0, counters.BytesSent - previous.BytesSent) / elapsed;
            }
            else if (elapsed > 0)
            {
                // First seen during this interval: everything it has counted happened since the last sample.
                down = counters.BytesReceived / elapsed;
                up = counters.BytesSent / elapsed;
            }

            rates.Add(new ProcessRate(pid, down, up, counters.BytesReceived, counters.BytesSent));
        }

        _previous = new Dictionary<int, TrafficCounters>(current);
        _previousTimestamp = timestamp;
        rates.Sort((a, b) =>
        {
            var byRate = b.TotalBytesPerSecond.CompareTo(a.TotalBytesPerSecond);
            return byRate != 0 ? byRate : (b.TotalDownloaded + b.TotalUploaded).CompareTo(a.TotalDownloaded + a.TotalUploaded);
        });
        return rates;
    }
}
