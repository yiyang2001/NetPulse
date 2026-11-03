using System.Text.Json.Serialization;

namespace NetPulse.Core;

public sealed record DailyUsage(DateOnly Date, long Downloaded, long Uploaded)
{
    [JsonIgnore]
    public long Total => Downloaded + Uploaded;
}

/// <summary>On-disk form of the ledger.</summary>
public sealed class UsageDocument
{
    public int Version { get; set; } = 1;

    public List<DailyUsage> Days { get; set; } = [];
}

/// <summary>Per-day traffic totals. Not thread-safe; use from one thread (the UI thread).</summary>
public sealed class UsageLedger
{
    private readonly SortedDictionary<DateOnly, (long Down, long Up)> _days = [];

    public UsageLedger()
    {
    }

    public UsageLedger(IEnumerable<DailyUsage> days)
    {
        foreach (var day in days)
        {
            Add(day.Date, day.Downloaded, day.Uploaded);
        }
    }

    /// <summary>True after any change since the last <see cref="MarkSaved"/>.</summary>
    public bool IsDirty { get; private set; }

    public void Add(DateOnly date, long downloaded, long uploaded)
    {
        if (downloaded <= 0 && uploaded <= 0)
        {
            return;
        }

        _days.TryGetValue(date, out var current);
        _days[date] = (current.Down + Math.Max(0, downloaded), current.Up + Math.Max(0, uploaded));
        IsDirty = true;
    }

    public DailyUsage Get(DateOnly date) =>
        _days.TryGetValue(date, out var value)
            ? new DailyUsage(date, value.Down, value.Up)
            : new DailyUsage(date, 0, 0);

    /// <summary>Sums the inclusive date range.</summary>
    public DailyUsage Sum(DateOnly from, DateOnly to)
    {
        long down = 0;
        long up = 0;
        foreach (var (date, value) in _days)
        {
            if (date >= from && date <= to)
            {
                down += value.Down;
                up += value.Up;
            }
        }

        return new DailyUsage(from, down, up);
    }

    /// <summary>Every day in the inclusive range, with zero entries for days without traffic.</summary>
    public IReadOnlyList<DailyUsage> Range(DateOnly from, DateOnly to)
    {
        var result = new List<DailyUsage>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            result.Add(Get(date));
        }

        return result;
    }

    /// <summary>
    /// The <paramref name="count"/> calendar months up to and including the one of <paramref name="today"/>,
    /// oldest first, each as one entry dated the 1st (months without traffic are zero).
    /// </summary>
    public IReadOnlyList<DailyUsage> Months(int count, DateOnly today)
    {
        var result = new List<DailyUsage>();
        var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-(Math.Max(1, count) - 1));
        for (var start = first; start <= today; start = start.AddMonths(1))
        {
            var end = start.AddMonths(1).AddDays(-1);
            var sum = Sum(start, end);
            result.Add(new DailyUsage(start, sum.Downloaded, sum.Uploaded));
        }

        return result;
    }

    /// <summary>Drops days before <paramref name="keepFrom"/>.</summary>
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

    public IReadOnlyList<DailyUsage> ToList() =>
        _days.Select(pair => new DailyUsage(pair.Key, pair.Value.Down, pair.Value.Up)).ToList();

    public void MarkSaved() => IsDirty = false;
}
