using static NetPulse.Core.Lang;

namespace NetPulse.Core;

public readonly record struct LatencyStats(double MinMs, double MedianMs, double MaxMs, double JitterMs, int Count)
{
    /// <summary>
    /// Jitter is the mean absolute difference between consecutive samples (as in RFC 3550 / most speed tests),
    /// so it reflects variation over time rather than spread.
    /// </summary>
    public static LatencyStats? Compute(IReadOnlyList<double> samplesMs)
    {
        if (samplesMs.Count == 0)
        {
            return null;
        }

        var sorted = samplesMs.Order().ToArray();
        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;

        double jitter = 0;
        for (var i = 1; i < samplesMs.Count; i++)
        {
            jitter += Math.Abs(samplesMs[i] - samplesMs[i - 1]);
        }

        if (samplesMs.Count > 1)
        {
            jitter /= samplesMs.Count - 1;
        }

        return new LatencyStats(sorted[0], median, sorted[^1], jitter, samplesMs.Count);
    }
}

/// <summary>
/// Collects (time, cumulative bytes) points during a throughput test. The reported speed ignores the
/// warm-up period, in which TCP slow start and connection setup would understate the line speed.
/// </summary>
public sealed class ThroughputMeter(TimeSpan warmUp)
{
    private readonly List<(TimeSpan Time, long Bytes)> _points = [];

    public IReadOnlyList<(TimeSpan Time, long Bytes)> Points => _points;

    public void Add(TimeSpan time, long cumulativeBytes) => _points.Add((time, cumulativeBytes));

    /// <summary>Average bytes/second over the most recent <paramref name="window"/>, for live display.</summary>
    public double CurrentBytesPerSecond(TimeSpan window)
    {
        if (_points.Count < 2)
        {
            return 0;
        }

        var last = _points[^1];
        var first = _points.FindLast(p => last.Time - p.Time >= window);
        if (first == default)
        {
            first = _points[0];
        }

        var seconds = (last.Time - first.Time).TotalSeconds;
        return seconds > 0 ? (last.Bytes - first.Bytes) / seconds : 0;
    }

    /// <summary>Average bytes/second after the warm-up; falls back to the whole run if it was shorter.</summary>
    public double ResultBytesPerSecond()
    {
        if (_points.Count < 2)
        {
            return 0;
        }

        var start = _points.Find(p => p.Time >= warmUp);
        if (start == default || start == _points[^1])
        {
            start = _points[0];
        }

        var end = _points[^1];
        var seconds = (end.Time - start.Time).TotalSeconds;
        return seconds > 0 ? (end.Bytes - start.Bytes) / seconds : 0;
    }
}

public readonly record struct PingSummary(int Sent, int Received, double? MinMs, double? AverageMs, double? MaxMs)
{
    public int Lost => Sent - Received;

    public double LossFraction => Sent > 0 ? (double)Lost / Sent : 0;

    /// <param name="roundTripsMs">One entry per probe; null for a probe that got no reply.</param>
    public static PingSummary From(IReadOnlyList<double?> roundTripsMs)
    {
        var replies = roundTripsMs.Where(r => r.HasValue).Select(r => r!.Value).ToList();
        return replies.Count == 0
            ? new PingSummary(roundTripsMs.Count, 0, null, null, null)
            : new PingSummary(roundTripsMs.Count, replies.Count, replies.Min(), replies.Average(), replies.Max());
    }
}

public static class WifiBands
{
    /// <summary>
    /// Band implied by an 802.11 channel number. Channels above 14 are reported as "5/6 GHz" because
    /// 6 GHz channels reuse the 1–233 numbering and the channel alone cannot tell them apart.
    /// </summary>
    public static string FromChannel(uint channel) => channel switch
    {
        0 => T("未知", "Unknown"),
        <= 14 => "2.4 GHz",
        _ => "5/6 GHz",
    };

    public static string FromFrequencyKhz(uint kHz) => kHz switch
    {
        >= 5_925_000 => "6 GHz",
        >= 4_900_000 => "5 GHz",
        >= 2_400_000 => "2.4 GHz",
        _ => T("未知", "Unknown"),
    };
}
