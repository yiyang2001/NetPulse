namespace NetPulse.Core;

public enum SpeedAlertDirection
{
    Upload,
    Download,
}

/// <summary>Alert when the speed stays above a limit for a while, e.g. to notice a background upload.</summary>
public sealed class SpeedAlertSettings
{
    public bool Enabled { get; set; }

    public SpeedAlertDirection Direction { get; set; } = SpeedAlertDirection.Upload;

    public double ThresholdMBps { get; set; } = 1;

    public int Minutes { get; set; } = 5;

    public long ThresholdBytesPerSecond => (long)(ThresholdMBps * 1024 * 1024);

    public void Normalize()
    {
        ThresholdMBps = double.IsFinite(ThresholdMBps) ? Math.Clamp(ThresholdMBps, 0.01, 100_000) : 1;
        Minutes = Math.Clamp(Minutes, 1, 240);
        if (!Enum.IsDefined(Direction))
        {
            Direction = SpeedAlertDirection.Upload;
        }
    }
}

/// <summary>Alert when one program moves more than a limit in a day.</summary>
public sealed class ProgramAlertSettings
{
    public bool Enabled { get; set; }

    public double LimitMb { get; set; } = 1024;

    public long LimitBytes => (long)(LimitMb * 1024 * 1024);

    public void Normalize() => LimitMb = double.IsFinite(LimitMb) ? Math.Clamp(LimitMb, 1, 100_000_000) : 1024;
}

/// <summary>
/// Reports once when the speed has been above the threshold without a break for the whole duration,
/// and arms again only after it dropped below. One dip below the threshold restarts the count.
/// </summary>
public sealed class SustainedSpeedDetector(double thresholdBytesPerSecond, TimeSpan duration)
{
    private TimeSpan? _since;
    private bool _fired;

    /// <param name="now">Monotonic time of the sample.</param>
    /// <returns>True exactly once per sustained period, at the sample that completes it.</returns>
    public bool Feed(TimeSpan now, double bytesPerSecond)
    {
        if (bytesPerSecond < thresholdBytesPerSecond)
        {
            _since = null;
            _fired = false;
            return false;
        }

        _since ??= now;
        if (!_fired && now - _since.Value >= duration)
        {
            _fired = true;
            return true;
        }

        return false;
    }
}

/// <summary>Finds programs that crossed the daily limit, each once per day.</summary>
public sealed class ProgramLimitTracker
{
    private readonly HashSet<string> _alerted = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly _date;

    public IReadOnlyList<AppUsage> NewlyOver(ProcessUsageLedger ledger, DateOnly today, long limitBytes)
    {
        if (today != _date)
        {
            _date = today;
            _alerted.Clear();
        }

        if (limitBytes <= 0)
        {
            return [];
        }

        var over = ledger.Totals(today, today).Where(a => a.Total >= limitBytes && _alerted.Add(a.Name)).ToList();
        return over;
    }
}

/// <summary>Last full week (Monday to Sunday) against the one before.</summary>
public sealed record WeeklySummary(DateOnly From, DateOnly To, DailyUsage Week, DailyUsage PreviousWeek)
{
    /// <summary>
    /// The summary to show now, or null: nothing was shown for this week yet (<paramref name="lastShownWeekStart"/>
    /// is older than this week's Monday) and the last week had traffic.
    /// </summary>
    public static WeeklySummary? DueOn(UsageLedger ledger, DateOnly today, DateOnly? lastShownWeekStart)
    {
        var thisMonday = MondayOf(today);
        if (lastShownWeekStart is { } shown && shown >= thisMonday)
        {
            return null;
        }

        var from = thisMonday.AddDays(-7);
        var to = thisMonday.AddDays(-1);
        var week = ledger.Sum(from, to);
        return week.Total > 0 ? new WeeklySummary(from, to, week, ledger.Sum(from.AddDays(-7), to.AddDays(-7))) : null;
    }

    public static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
