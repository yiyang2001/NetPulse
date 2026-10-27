namespace NetPulse.Core;

/// <summary>Cumulative byte counters of one network interface.</summary>
public readonly record struct TrafficCounters(long BytesReceived, long BytesSent);

/// <summary>Result of one sampling step.</summary>
/// <param name="DownloadBytesPerSecond">Average download rate since the previous sample.</param>
/// <param name="UploadBytesPerSecond">Average upload rate since the previous sample.</param>
/// <param name="DownloadedBytes">Bytes received since the previous sample.</param>
/// <param name="UploadedBytes">Bytes sent since the previous sample.</param>
public readonly record struct SpeedSample(
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    long DownloadedBytes,
    long UploadedBytes)
{
    public static readonly SpeedSample Zero = new(0, 0, 0, 0);
}

/// <summary>
/// Turns successive snapshots of cumulative per-interface counters into rates and deltas.
/// Interfaces that appear, disappear or reset their counters between snapshots contribute nothing
/// for that step instead of producing spikes.
/// </summary>
public sealed class SpeedSampler
{
    private Dictionary<string, TrafficCounters> _previous = new(StringComparer.Ordinal);
    private TimeSpan? _previousTimestamp;

    /// <param name="current">Counters keyed by a stable interface id.</param>
    /// <param name="timestamp">Monotonic time of the snapshot (e.g. from <see cref="System.Diagnostics.Stopwatch"/>).</param>
    public SpeedSample Sample(IReadOnlyDictionary<string, TrafficCounters> current, TimeSpan timestamp)
    {
        long downloaded = 0;
        long uploaded = 0;
        foreach (var (id, counters) in current)
        {
            if (_previous.TryGetValue(id, out var before))
            {
                downloaded += Math.Max(0, counters.BytesReceived - before.BytesReceived);
                uploaded += Math.Max(0, counters.BytesSent - before.BytesSent);
            }
        }

        var elapsed = _previousTimestamp is { } previousTimestamp ? (timestamp - previousTimestamp).TotalSeconds : 0;
        _previous = new Dictionary<string, TrafficCounters>(current, StringComparer.Ordinal);
        _previousTimestamp = timestamp;

        return elapsed > 0
            ? new SpeedSample(downloaded / elapsed, uploaded / elapsed, downloaded, uploaded)
            : SpeedSample.Zero;
    }

    /// <summary>Forgets the baseline so the next sample starts fresh (e.g. after resume from sleep).</summary>
    public void Reset()
    {
        _previous = new Dictionary<string, TrafficCounters>(StringComparer.Ordinal);
        _previousTimestamp = null;
    }
}
