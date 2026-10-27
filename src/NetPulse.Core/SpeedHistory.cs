namespace NetPulse.Core;

public readonly record struct SpeedPoint(TimeSpan Time, double DownloadBytesPerSecond, double UploadBytesPerSecond);

public readonly record struct SpeedStats(double PeakDownload, double PeakUpload, double AverageDownload, double AverageUpload)
{
    public static readonly SpeedStats Zero = new(0, 0, 0, 0);
}

/// <summary>Recent speed samples for the live graph, oldest first; older than <see cref="Span"/> is dropped.</summary>
public sealed class SpeedHistory(TimeSpan span)
{
    private readonly Queue<SpeedPoint> _points = new();

    public TimeSpan Span { get; } = span;

    public int Count => _points.Count;

    public void Add(TimeSpan time, SpeedSample sample)
    {
        // A sampler restart or clock jump backwards would make the graph run in reverse.
        if (_points.Count > 0 && time < _points.Last().Time)
        {
            _points.Clear();
        }

        _points.Enqueue(new SpeedPoint(time, sample.DownloadBytesPerSecond, sample.UploadBytesPerSecond));
        while (_points.Count > 0 && time - _points.Peek().Time > Span)
        {
            _points.Dequeue();
        }
    }

    /// <summary>Points no older than <paramref name="window"/> before the newest one.</summary>
    public IReadOnlyList<SpeedPoint> Recent(TimeSpan window)
    {
        if (_points.Count == 0)
        {
            return [];
        }

        var newest = _points.Last().Time;
        return _points.Where(p => newest - p.Time <= window).ToList();
    }

    public static SpeedStats Summarize(IReadOnlyList<SpeedPoint> points)
    {
        if (points.Count == 0)
        {
            return SpeedStats.Zero;
        }

        return new SpeedStats(
            points.Max(p => p.DownloadBytesPerSecond),
            points.Max(p => p.UploadBytesPerSecond),
            points.Average(p => p.DownloadBytesPerSecond),
            points.Average(p => p.UploadBytesPerSecond));
    }

    public void Clear() => _points.Clear();
}

/// <summary>Round axis maxima, so gridline labels read "2 MB/s" or "50 Mbps" rather than "4.77 MB/s".</summary>
public static class NiceScale
{
    private static readonly double[] Steps = [1, 2, 5, 10];

    /// <summary>
    /// Smallest value ≥ <paramref name="bytesPerSecond"/> that is 1, 2 or 5 × 10ⁿ in its display unit
    /// (KB/MB step by 1024, Kbps/Mbps by 1000). Never below 1 KB/s or 10 Kbps, so an idle graph stays flat.
    /// </summary>
    public static double Ceiling(double bytesPerSecond, SpeedUnit unit)
    {
        var (value, unitStep, floor) = unit == SpeedUnit.Bits
            ? (bytesPerSecond * 8, 1000.0, 10_000.0)
            : (bytesPerSecond, 1024.0, 1024.0);
        if (!double.IsFinite(value) || value < floor)
        {
            value = floor;
        }

        var scale = 1.0;
        while (value / scale >= unitStep)
        {
            scale *= unitStep;
        }

        var scaled = value / scale;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(scaled)));
        var nice = Steps.Select(s => s * magnitude).First(s => s >= scaled * (1 - 1e-9));
        if (nice >= 1000)
        {
            nice = unitStep; // "1 MB/s", not "1000 KB/s"
        }

        var result = nice * scale;
        return unit == SpeedUnit.Bits ? result / 8 : result;
    }
}
