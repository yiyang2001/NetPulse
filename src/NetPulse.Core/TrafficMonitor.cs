using System.Diagnostics;

namespace NetPulse.Core;

/// <summary>
/// One sampling step reads every adapter once and feeds two samplers: the displayed speed follows the
/// user's adapter choice, while usage statistics always count all (non-virtual) adapters so that
/// changing the displayed adapter never changes what the history means.
/// </summary>
public sealed class TrafficMonitor(UsageLedger ledger, Func<IReadOnlyList<AdapterSnapshot>>? readAdapters = null, Func<DateTime>? now = null)
{
    private readonly SpeedSampler _displaySampler = new();
    private readonly SpeedSampler _usageSampler = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Func<IReadOnlyList<AdapterSnapshot>> _readAdapters = readAdapters ?? NetworkAdapters.ReadAll;
    private readonly Func<DateTime> _now = now ?? (() => DateTime.Now);

    public UsageLedger Ledger { get; } = ledger;

    public SpeedSample Last { get; private set; } = SpeedSample.Zero;

    public SpeedSample Tick(string? displayAdapterId, bool excludeVirtual)
    {
        var adapters = _readAdapters();
        var timestamp = _clock.Elapsed;

        var usage = _usageSampler.Sample(NetworkAdapters.Select(adapters, null, excludeVirtual), timestamp);
        Ledger.Add(DateOnly.FromDateTime(_now()), usage.DownloadedBytes, usage.UploadedBytes);

        Last = _displaySampler.Sample(NetworkAdapters.Select(adapters, displayAdapterId, excludeVirtual), timestamp);
        return Last;
    }

    /// <summary>Starts both samplers from scratch, e.g. after the adapter filter changed.</summary>
    public void ResetBaseline()
    {
        _displaySampler.Reset();
        _usageSampler.Reset();
        Last = SpeedSample.Zero;
    }
}
