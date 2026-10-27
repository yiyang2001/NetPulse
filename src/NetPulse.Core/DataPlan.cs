namespace NetPulse.Core;

public enum PlanAlertLevel
{
    None = 0,
    Warning = 1,
    Exceeded = 2,
}

public readonly record struct BillingCycle(DateOnly Start, DateOnly End)
{
    /// <summary>
    /// The cycle containing <paramref name="today"/> for a plan that renews on day <paramref name="startDay"/>
    /// of each month. Days past the end of a short month renew on its last day (e.g. 31 → Feb 28).
    /// </summary>
    public static BillingCycle Containing(DateOnly today, int startDay)
    {
        startDay = Math.Clamp(startDay, 1, 31);
        var thisMonthStart = StartIn(today.Year, today.Month, startDay);
        var start = today >= thisMonthStart
            ? thisMonthStart
            : StartIn(today.AddMonths(-1).Year, today.AddMonths(-1).Month, startDay);

        var nextMonth = new DateOnly(start.Year, start.Month, 1).AddMonths(1);
        var nextStart = StartIn(nextMonth.Year, nextMonth.Month, startDay);
        return new BillingCycle(start, nextStart.AddDays(-1));
    }

    public int DaysRemaining(DateOnly today) => Math.Max(0, End.DayNumber - today.DayNumber + 1);

    private static DateOnly StartIn(int year, int month, int startDay) =>
        new(year, month, Math.Min(startDay, DateTime.DaysInMonth(year, month)));
}

public static class DataPlanCalculator
{
    public static (BillingCycle Cycle, PlanStatus Status) Evaluate(UsageLedger ledger, DataPlanSettings plan, DateOnly today)
    {
        var cycle = BillingCycle.Containing(today, plan.CycleStartDay);
        var used = ledger.Sum(cycle.Start, cycle.End);
        var counted = plan.CountUpload ? used.Total : used.Downloaded;
        return (cycle, PlanStatus.Evaluate(counted, plan.LimitBytes, plan.WarnPercent));
    }
}

public readonly record struct PlanStatus(long UsedBytes, long LimitBytes, PlanAlertLevel Level)
{
    public double Fraction => LimitBytes > 0 ? (double)UsedBytes / LimitBytes : 0;

    public long RemainingBytes => Math.Max(0, LimitBytes - UsedBytes);

    public static PlanStatus Evaluate(long usedBytes, long limitBytes, int warnPercent)
    {
        if (limitBytes <= 0)
        {
            return new PlanStatus(usedBytes, limitBytes, PlanAlertLevel.None);
        }

        var level = usedBytes >= limitBytes
            ? PlanAlertLevel.Exceeded
            : usedBytes * 100 >= (long)Math.Clamp(warnPercent, 1, 100) * limitBytes
                ? PlanAlertLevel.Warning
                : PlanAlertLevel.None;
        return new PlanStatus(usedBytes, limitBytes, level);
    }
}
