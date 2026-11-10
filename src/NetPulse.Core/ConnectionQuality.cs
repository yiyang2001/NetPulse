using System.Text.Json.Serialization;

namespace NetPulse.Core;

/// <summary>Connection quality monitoring: off by default; when on, pings the gateway and optionally one internet address.</summary>
public sealed class ConnectionQualitySettings
{
    public const string DefaultExternalTarget = "1.1.1.1";

    public bool Enabled { get; set; }

    /// <summary>Also ping <see cref="ExternalTarget"/>, to tell local problems from internet (ISP) problems.</summary>
    public bool ProbeExternal { get; set; }

    public string ExternalTarget { get; set; } = DefaultExternalTarget;

    public int IntervalSeconds { get; set; } = 5;

    public int HighLatencyMs { get; set; } = 200;

    /// <summary>Notify after an outage that lasted at least <see cref="OutageAlertSeconds"/>.</summary>
    public bool OutageAlert { get; set; } = true;

    public int OutageAlertSeconds { get; set; } = 60;

    [JsonIgnore]
    public TimeSpan Interval => TimeSpan.FromSeconds(IntervalSeconds);

    public bool ShouldAlert(Outage outage) => OutageAlert && outage.Duration >= TimeSpan.FromSeconds(OutageAlertSeconds);

    public void Normalize()
    {
        IntervalSeconds = Math.Clamp(IntervalSeconds, 2, 60);
        HighLatencyMs = Math.Clamp(HighLatencyMs, 20, 2000);
        OutageAlertSeconds = Math.Clamp(OutageAlertSeconds, 15, 86_400);
        ExternalTarget = ExternalTarget?.Trim() ?? string.Empty;
        if (ExternalTarget.Length is 0 or > 253)
        {
            ExternalTarget = DefaultExternalTarget;
        }
    }
}

/// <summary>
/// One probe round. <paramref name="Network"/> is null when no adapter has a gateway (not connected).
/// Latencies are in milliseconds; null means no reply within the timeout.
/// </summary>
public readonly record struct QualityProbe(NetworkIdentity? Network, double? GatewayMs, bool ExternalProbed = false, double? ExternalMs = null)
{
    public static QualityProbe NotConnected => new(null, null);
}

/// <summary>State of a sample or a minute, in increasing severity (a minute shows its worst sample).</summary>
public enum QualityState
{
    Ok,
    HighLatency,

    /// <summary>A lost reply that did not (yet) become an outage.</summary>
    Loss,
    Disconnected,
    ExternalOutage,
    LocalOutage,
}

public enum OutageKind
{
    /// <summary>The gateway did not answer: Wi-Fi signal, cable or router.</summary>
    Local,

    /// <summary>The gateway answered but the internet address did not: ISP or upstream.</summary>
    External,
}

public sealed record Outage(DateTime Start, DateTime End, OutageKind Kind, string NetworkKey, string NetworkLabel)
{
    [JsonIgnore]
    public TimeSpan Duration => End - Start;
}

/// <summary>Latency distribution in fixed buckets, so days merge and median / P95 need no raw samples.</summary>
public static class LatencyHistogram
{
    /// <summary>Upper bounds in ms; one more bucket holds everything slower.</summary>
    public static readonly double[] Bounds = [1, 2, 3, 5, 7, 10, 15, 20, 30, 50, 70, 100, 150, 200, 300, 500, 700, 1000];

    public static int Length => Bounds.Length + 1;

    public static int[] Create() => new int[Length];

    public static void Add(int[] counts, double ms)
    {
        var index = Array.FindIndex(Bounds, b => ms <= b);
        counts[index < 0 ? Bounds.Length : index]++;
    }

    public static void Merge(int[] into, int[] from)
    {
        for (var i = 0; i < Math.Min(into.Length, from.Length); i++)
        {
            into[i] += from[i];
        }
    }

    /// <summary>Upper bound of the bucket holding the <paramref name="fraction"/> quantile; null without samples.</summary>
    public static double? Percentile(int[] counts, double fraction)
    {
        long total = counts.Sum(c => (long)c);
        if (total == 0)
        {
            return null;
        }

        var rank = Math.Max(1, (long)Math.Ceiling(total * fraction));
        long seen = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            seen += counts[i];
            if (seen >= rank)
            {
                return Bounds[Math.Min(i, Bounds.Length - 1)];
            }
        }

        return Bounds[^1];
    }

    /// <summary>A stored histogram of the wrong length (hand-edited, other version) is reset rather than misread.</summary>
    public static int[] Sanitize(int[]? counts) =>
        counts is { } c && c.Length == Length && c.All(v => v >= 0) ? c : Create();
}

/// <summary>Totals for one day and one network. Disconnected time is kept under the empty network key.</summary>
public sealed class QualityDay
{
    public DateOnly Date { get; set; }

    public string NetworkKey { get; set; } = string.Empty;

    /// <summary>Time connected and probed; excludes disconnected and unmonitored time.</summary>
    public double MonitoredSeconds { get; set; }

    /// <summary>Part of <see cref="MonitoredSeconds"/> inside outages.</summary>
    public double DownSeconds { get; set; }

    public double DisconnectedSeconds { get; set; }

    /// <summary>Outages that started on this day.</summary>
    public int Outages { get; set; }

    public int GatewaySamples { get; set; }

    public int GatewayLost { get; set; }

    public int ExternalSamples { get; set; }

    public int ExternalLost { get; set; }

    public int[] GatewayLatency { get; set; } = LatencyHistogram.Create();

    public int[] ExternalLatency { get; set; } = LatencyHistogram.Create();
}

/// <summary>One minute of the 24-hour timeline.</summary>
public sealed class QualityMinute
{
    public DateTime Minute { get; set; }

    public QualityState State { get; set; }

    public double GatewayMsSum { get; set; }

    public int GatewayReplies { get; set; }

    public double ExternalMsSum { get; set; }

    public int ExternalReplies { get; set; }

    [JsonIgnore]
    public double? GatewayMs => GatewayReplies > 0 ? GatewayMsSum / GatewayReplies : null;

    [JsonIgnore]
    public double? ExternalMs => ExternalReplies > 0 ? ExternalMsSum / ExternalReplies : null;

    public void Raise(QualityState state)
    {
        if (state > State)
        {
            State = state;
        }
    }
}

public sealed record QualitySummary(
    double MonitoredSeconds,
    double DownSeconds,
    double DisconnectedSeconds,
    int Outages,
    int GatewaySamples,
    int GatewayLost,
    int ExternalSamples,
    int ExternalLost,
    double? GatewayMedianMs,
    double? GatewayP95Ms,
    double? ExternalMedianMs,
    double? ExternalP95Ms)
{
    /// <summary>Share of monitored time outside outages; null when nothing was monitored.</summary>
    public double? Availability => MonitoredSeconds > 0 ? Math.Clamp((MonitoredSeconds - DownSeconds) / MonitoredSeconds, 0, 1) : null;

    public double? GatewayLossRate => GatewaySamples > 0 ? (double)GatewayLost / GatewaySamples : null;

    public double? ExternalLossRate => ExternalSamples > 0 ? (double)ExternalLost / ExternalSamples : null;
}

/// <summary>On-disk form of <see cref="QualityLedger"/> (quality.json).</summary>
public sealed class QualityDocument
{
    public int Version { get; set; } = 1;

    /// <summary>Network labels by key.</summary>
    public Dictionary<string, string> Networks { get; set; } = [];

    public List<QualityDay> Days { get; set; } = [];

    public List<Outage> Outages { get; set; } = [];

    public List<QualityMinute> Minutes { get; set; } = [];
}

/// <summary>Daily totals, outage list and 24-hour minute timeline. Not thread-safe; use from the UI thread.</summary>
public sealed class QualityLedger
{
    public const int OutageRetentionDays = 90;
    public const int MaxOutages = 2000;
    public static readonly TimeSpan TimelineSpan = TimeSpan.FromHours(24);

    private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);
    private readonly Dictionary<(DateOnly Date, string Key), QualityDay> _days = [];
    private readonly List<Outage> _outages = [];
    private readonly SortedDictionary<DateTime, QualityMinute> _minutes = [];

    public QualityLedger()
    {
    }

    public QualityLedger(QualityDocument? document)
    {
        if (document is null)
        {
            return;
        }

        foreach (var (key, label) in document.Networks ?? [])
        {
            if (!string.IsNullOrEmpty(key))
            {
                _labels[key] = label ?? key;
            }
        }

        foreach (var day in document.Days ?? [])
        {
            if (day is null)
            {
                continue;
            }

            day.NetworkKey ??= string.Empty;
            day.GatewayLatency = LatencyHistogram.Sanitize(day.GatewayLatency);
            day.ExternalLatency = LatencyHistogram.Sanitize(day.ExternalLatency);
            _days[(day.Date, day.NetworkKey)] = day;
        }

        _outages.AddRange((document.Outages ?? []).Where(o => o is not null && o.End >= o.Start).OrderBy(o => o.Start));
        foreach (var minute in document.Minutes ?? [])
        {
            if (minute is not null && Enum.IsDefined(minute.State))
            {
                _minutes[minute.Minute] = minute;
            }
        }
    }

    public bool IsDirty { get; private set; }

    /// <summary>Known networks, by label.</summary>
    public IReadOnlyList<NetworkIdentity> Networks =>
        [.. _labels.Select(l => new NetworkIdentity(l.Key, l.Value)).OrderBy(n => n.Label, StringComparer.CurrentCultureIgnoreCase)];

    internal QualityDay Day(DateOnly date, NetworkIdentity? network)
    {
        var key = network?.Key ?? string.Empty;
        if (network is { } n && (!_labels.TryGetValue(n.Key, out var label) || label != n.Label))
        {
            _labels[n.Key] = n.Label;
        }

        if (!_days.TryGetValue((date, key), out var day))
        {
            day = new QualityDay { Date = date, NetworkKey = key };
            _days[(date, key)] = day;
        }

        IsDirty = true;
        return day;
    }

    internal QualityDay DayByKey(DateOnly date, string key) =>
        Day(date, string.IsNullOrEmpty(key) ? null : new NetworkIdentity(key, _labels.GetValueOrDefault(key, key)));

    internal QualityMinute Minute(DateTime time)
    {
        var key = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, time.Kind);
        if (!_minutes.TryGetValue(key, out var minute))
        {
            minute = new QualityMinute { Minute = key };
            _minutes[key] = minute;
            while (_minutes.Count > 0 && _minutes.First().Key <= key - TimelineSpan)
            {
                _minutes.Remove(_minutes.First().Key);
            }
        }

        IsDirty = true;
        return minute;
    }

    internal void AddOutage(Outage outage)
    {
        _outages.Add(outage);
        if (_outages.Count > MaxOutages)
        {
            _outages.RemoveRange(0, _outages.Count - MaxOutages);
        }

        IsDirty = true;
    }

    /// <summary>Totals for the inclusive range; <paramref name="networkKey"/> null means all networks (and disconnected time).</summary>
    public QualitySummary Summarize(DateOnly from, DateOnly to, string? networkKey = null)
    {
        var days = _days.Values.Where(d => d.Date >= from && d.Date <= to && (networkKey is null || d.NetworkKey == networkKey));
        double monitored = 0, down = 0, disconnected = 0;
        int outages = 0, gwSamples = 0, gwLost = 0, extSamples = 0, extLost = 0;
        var gateway = LatencyHistogram.Create();
        var external = LatencyHistogram.Create();
        foreach (var day in days)
        {
            monitored += day.MonitoredSeconds;
            down += day.DownSeconds;
            disconnected += day.DisconnectedSeconds;
            outages += day.Outages;
            gwSamples += day.GatewaySamples;
            gwLost += day.GatewayLost;
            extSamples += day.ExternalSamples;
            extLost += day.ExternalLost;
            LatencyHistogram.Merge(gateway, day.GatewayLatency);
            LatencyHistogram.Merge(external, day.ExternalLatency);
        }

        return new QualitySummary(
            monitored, down, disconnected, outages, gwSamples, gwLost, extSamples, extLost,
            LatencyHistogram.Percentile(gateway, 0.5), LatencyHistogram.Percentile(gateway, 0.95),
            LatencyHistogram.Percentile(external, 0.5), LatencyHistogram.Percentile(external, 0.95));
    }

    /// <summary>One summary per day of the inclusive range, oldest first.</summary>
    public IReadOnlyList<(DateOnly Date, QualitySummary Summary)> Daily(DateOnly from, DateOnly to, string? networkKey = null)
    {
        var result = new List<(DateOnly, QualitySummary)>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            result.Add((date, Summarize(date, date, networkKey)));
        }

        return result;
    }

    /// <summary>Outages that started in [<paramref name="from"/>, <paramref name="to"/>), newest first.</summary>
    public IReadOnlyList<Outage> Outages(DateTime from, DateTime to, string? networkKey = null) =>
        [.. _outages.Where(o => o.Start >= from && o.Start < to && (networkKey is null || o.NetworkKey == networkKey)).OrderByDescending(o => o.Start)];

    /// <summary>Timeline minutes from <paramref name="since"/> on, oldest first; minutes without samples are absent.</summary>
    public IReadOnlyList<QualityMinute> Minutes(DateTime since) => [.. _minutes.Values.Where(m => m.Minute >= since)];

    /// <summary>Drops days before <paramref name="keepDaysFrom"/>, old outages and timeline minutes older than 24 hours.</summary>
    public void Prune(DateOnly keepDaysFrom, DateTime now)
    {
        foreach (var key in _days.Keys.Where(k => k.Date < keepDaysFrom).ToList())
        {
            _days.Remove(key);
            IsDirty = true;
        }

        var outageCutoff = now.AddDays(-OutageRetentionDays);
        if (_outages.RemoveAll(o => o.Start < outageCutoff) > 0)
        {
            IsDirty = true;
        }

        foreach (var key in _minutes.Keys.Where(k => k <= now - TimelineSpan).ToList())
        {
            _minutes.Remove(key);
            IsDirty = true;
        }

        var used = _days.Keys.Select(k => k.Key).Concat(_outages.Select(o => o.NetworkKey)).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _labels.Keys.Where(k => !used.Contains(k)).ToList())
        {
            _labels.Remove(key);
            IsDirty = true;
        }
    }

    public void Clear()
    {
        _labels.Clear();
        _days.Clear();
        _outages.Clear();
        _minutes.Clear();
        IsDirty = true;
    }

    public QualityDocument ToDocument() => new()
    {
        Networks = new Dictionary<string, string>(_labels, StringComparer.Ordinal),
        Days = [.. _days.Values.OrderBy(d => d.Date).ThenBy(d => d.NetworkKey, StringComparer.Ordinal)],
        Outages = [.. _outages],
        Minutes = [.. _minutes.Values],
    };

    public void MarkSaved() => IsDirty = false;
}

public readonly record struct QualityUpdate(QualityState State, bool OutageStarted, Outage? OutageEnded);

/// <summary>
/// Turns probe rounds into states, outages and ledger totals.
/// <list type="bullet">
/// <item><see cref="FailuresForOutage"/> failed rounds in a row make an outage; it starts at the first of them and ends at
/// the next round that gets every reply. Its kind is <see cref="OutageKind.Local"/> if the gateway failed at any point.</item>
/// <item>A gap longer than <see cref="GapFactor"/> intervals (sleep, hibernation, a stalled app) is unmonitored time: it
/// counts nowhere, and an open outage ends at the last round before it rather than spanning the gap.</item>
/// <item>Switching network or disconnecting also ends an open outage at the last round on that network.</item>
/// </list>
/// Durations come from the monotonic clock; wall time only places samples on days and minutes.
/// <see cref="QualityDay.DownSeconds"/> adds up to exactly the durations of the outages.
/// </summary>
public sealed class QualityMonitor(QualityLedger ledger)
{
    public const int FailuresForOutage = 3;
    public const int GapFactor = 3;

    private readonly List<(DateOnly Date, string Key, double Seconds, DateTime Wall)> _pending = [];
    private TimeSpan? _lastMono;
    private string? _lastKey;

    private int _failures;
    private OutageKind _runKind;
    private TimeSpan _runStartMono;
    private DateTime _runStartWall;

    private bool _inOutage;
    private NetworkIdentity _outageNetwork;

    public QualityState? State { get; private set; }

    /// <summary>The outage in progress, ending at the latest round; null when there is none.</summary>
    public Outage? Ongoing => _inOutage && _lastMono is { } last ? MakeOutage(last) : null;

    public QualityUpdate Feed(QualityProbe probe, TimeSpan now, DateTime wall, TimeSpan interval, double highLatencyMs)
    {
        Outage? ended = null;
        var seconds = 0.0;
        var key = probe.Network?.Key ?? string.Empty;
        if (_lastMono is { } last)
        {
            var elapsed = now - last;
            if (elapsed < TimeSpan.Zero || elapsed > interval * GapFactor)
            {
                ended = EndOutage(last);
                ResetRun();
            }
            else
            {
                seconds = elapsed.TotalSeconds;
                if (key != _lastKey)
                {
                    ended = EndOutage(last);
                    ResetRun();
                }
            }
        }

        var date = DateOnly.FromDateTime(wall);
        var started = false;
        QualityState state;
        if (probe.Network is null)
        {
            if (_lastMono is { } previous)
            {
                ended ??= EndOutage(previous);
            }

            ResetRun();
            ledger.Day(date, null).DisconnectedSeconds += seconds;
            state = QualityState.Disconnected;
            ledger.Minute(wall).Raise(state);
        }
        else
        {
            var day = ledger.Day(date, probe.Network);
            var minute = ledger.Minute(wall);
            day.MonitoredSeconds += seconds;
            day.GatewaySamples++;
            if (probe.GatewayMs is { } gw)
            {
                LatencyHistogram.Add(day.GatewayLatency, gw);
                minute.GatewayMsSum += gw;
                minute.GatewayReplies++;
            }
            else
            {
                day.GatewayLost++;
            }

            if (probe.ExternalProbed)
            {
                day.ExternalSamples++;
                if (probe.ExternalMs is { } ext)
                {
                    LatencyHistogram.Add(day.ExternalLatency, ext);
                    minute.ExternalMsSum += ext;
                    minute.ExternalReplies++;
                }
                else
                {
                    day.ExternalLost++;
                }
            }

            OutageKind? failure = probe.GatewayMs is null ? OutageKind.Local
                : probe.ExternalProbed && probe.ExternalMs is null ? OutageKind.External
                : null;
            if (failure is null)
            {
                if (_inOutage)
                {
                    day.DownSeconds += seconds; // the link was still down at the previous round
                    ended = EndOutage(now);
                }

                ResetRun();
                state = probe.GatewayMs >= highLatencyMs || probe.ExternalMs >= highLatencyMs ? QualityState.HighLatency : QualityState.Ok;
                minute.Raise(state);
            }
            else
            {
                if (_failures == 0)
                {
                    _runStartMono = now;
                    _runStartWall = wall;
                    _runKind = failure.Value;
                }
                else if (failure == OutageKind.Local)
                {
                    _runKind = OutageKind.Local;
                }

                _failures++;
                // The first failed round's interval ended with a reply, so only later rounds are down time.
                var downSeconds = _failures == 1 ? 0 : seconds;
                if (_inOutage)
                {
                    day.DownSeconds += downSeconds;
                }
                else if (_failures >= FailuresForOutage)
                {
                    _inOutage = true;
                    _outageNetwork = probe.Network.Value;
                    started = true;
                    ledger.Day(DateOnly.FromDateTime(_runStartWall), _outageNetwork).Outages++;
                    foreach (var pending in _pending)
                    {
                        ledger.DayByKey(pending.Date, pending.Key).DownSeconds += pending.Seconds;
                        ledger.Minute(pending.Wall).Raise(OutageState(_runKind));
                    }

                    _pending.Clear();
                    day.DownSeconds += downSeconds;
                }
                else
                {
                    _pending.Add((date, key, downSeconds, wall));
                }

                state = _inOutage ? OutageState(_runKind) : QualityState.Loss;
                minute.Raise(state);
            }
        }

        _lastMono = now;
        _lastKey = key;
        State = state;
        return new QualityUpdate(state, started, ended);
    }

    /// <summary>Ends an open outage at the last round, e.g. when monitoring is turned off or NetPulse exits.</summary>
    public Outage? Stop()
    {
        var ended = _lastMono is { } last ? EndOutage(last) : null;
        ResetRun();
        _lastMono = null;
        _lastKey = null;
        State = null;
        return ended;
    }

    private static QualityState OutageState(OutageKind kind) =>
        kind == OutageKind.Local ? QualityState.LocalOutage : QualityState.ExternalOutage;

    private Outage MakeOutage(TimeSpan endMono) =>
        new(_runStartWall, _runStartWall + (endMono - _runStartMono), _runKind, _outageNetwork.Key, _outageNetwork.Label);

    private Outage? EndOutage(TimeSpan endMono)
    {
        if (!_inOutage)
        {
            return null;
        }

        var outage = MakeOutage(endMono);
        ledger.AddOutage(outage);
        _inOutage = false;
        return outage;
    }

    private void ResetRun()
    {
        _failures = 0;
        _pending.Clear();
        _inOutage = false;
    }
}

/// <summary>An up adapter with a default gateway, a candidate for the connection quality probe.</summary>
public sealed record GatewayCandidate(AdapterSnapshot Adapter, int InterfaceIndex, System.Net.IPAddress Gateway);

public static class GatewayChoice
{
    /// <summary>
    /// The adapter Windows routes internet traffic through (<paramref name="bestInterfaceIndex"/>, from GetBestInterface),
    /// if it has a gateway; otherwise the first physical one, then any. A VPN without its own gateway thus falls back
    /// to the network underneath it.
    /// </summary>
    public static GatewayCandidate? Pick(IReadOnlyList<GatewayCandidate> candidates, int? bestInterfaceIndex) =>
        candidates.FirstOrDefault(c => c.InterfaceIndex == bestInterfaceIndex)
        ?? candidates.FirstOrDefault(c => !c.Adapter.IsLikelyVirtual)
        ?? candidates.FirstOrDefault();
}
