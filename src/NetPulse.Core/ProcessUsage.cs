namespace NetPulse.Core;

public sealed record AppUsage(string Name, long Downloaded, long Uploaded)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public long Total => Downloaded + Uploaded;
}

public sealed record ProcessUsageDay(DateOnly Date, List<AppUsage> Apps);

public sealed class ProcessUsageDocument
{
    public int Version { get; set; } = 1;

    public List<ProcessUsageDay> Days { get; set; } = [];
}

/// <summary>Bytes per program name per day. Program names, not PIDs: PIDs are reused and mean nothing tomorrow.</summary>
public sealed class ProcessUsageLedger
{
    private readonly SortedDictionary<DateOnly, Dictionary<string, (long Down, long Up)>> _days = [];

    public ProcessUsageLedger()
    {
    }

    public ProcessUsageLedger(IEnumerable<ProcessUsageDay> days)
    {
        foreach (var day in days)
        {
            foreach (var app in day.Apps ?? [])
            {
                Add(day.Date, app.Name, app.Downloaded, app.Uploaded);
            }
        }

        IsDirty = false;
    }

    public bool IsDirty { get; private set; }

    public void Add(DateOnly date, string name, long downloaded, long uploaded)
    {
        downloaded = Math.Max(0, downloaded);
        uploaded = Math.Max(0, uploaded);
        if ((downloaded == 0 && uploaded == 0) || string.IsNullOrEmpty(name))
        {
            return;
        }

        if (!_days.TryGetValue(date, out var apps))
        {
            _days[date] = apps = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        }

        apps.TryGetValue(name, out var current);
        apps[name] = (current.Down + downloaded, current.Up + uploaded);
        IsDirty = true;
    }

    /// <summary>Per-program totals over the inclusive range, largest first.</summary>
    public IReadOnlyList<AppUsage> Totals(DateOnly from, DateOnly to)
    {
        var totals = new Dictionary<string, (long Down, long Up)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (date, apps) in _days)
        {
            if (date < from || date > to)
            {
                continue;
            }

            foreach (var (name, usage) in apps)
            {
                totals.TryGetValue(name, out var sum);
                totals[name] = (sum.Down + usage.Down, sum.Up + usage.Up);
            }
        }

        return totals
            .Select(p => new AppUsage(p.Key, p.Value.Down, p.Value.Up))
            .OrderByDescending(a => a.Total)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Prune(DateOnly keepFrom)
    {
        foreach (var date in _days.Keys.Where(d => d < keepFrom).ToList())
        {
            _days.Remove(date);
            IsDirty = true;
        }
    }

    public void Clear()
    {
        if (_days.Count > 0)
        {
            _days.Clear();
            IsDirty = true;
        }
    }

    public ProcessUsageDocument ToDocument() => new()
    {
        Days = _days
            .Select(d => new ProcessUsageDay(d.Key, d.Value.Select(a => new AppUsage(a.Key, a.Value.Down, a.Value.Up)).ToList()))
            .ToList(),
    };

    public void MarkSaved() => IsDirty = false;
}

/// <summary>
/// Books the growth of <see cref="ProcessTrafficTable"/> counters since the previous call into a
/// <see cref="ProcessUsageLedger"/> under each process's program name.
/// </summary>
public sealed class ProcessHistoryRecorder(ProcessUsageLedger ledger, Func<int, string> resolveName)
{
    private Dictionary<int, TrafficCounters> _previous = [];

    public ProcessUsageLedger Ledger { get; } = ledger;

    public void Record(IReadOnlyDictionary<int, TrafficCounters> current, DateOnly date)
    {
        foreach (var (pid, counters) in current)
        {
            _previous.TryGetValue(pid, out var before);
            // A counter that went down was reset (table cleared): everything it holds now is new.
            var down = counters.BytesReceived >= before.BytesReceived ? counters.BytesReceived - before.BytesReceived : counters.BytesReceived;
            var up = counters.BytesSent >= before.BytesSent ? counters.BytesSent - before.BytesSent : counters.BytesSent;
            if (down > 0 || up > 0)
            {
                Ledger.Add(date, resolveName(pid), down, up);
            }
        }

        _previous = new Dictionary<int, TrafficCounters>(current);
    }

    /// <summary>Call after the counters were cleared, once their contents have been recorded.</summary>
    public void ResetBaseline() => _previous = [];
}
