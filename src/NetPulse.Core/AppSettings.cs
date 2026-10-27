namespace NetPulse.Core;

public enum ThemeMode
{
    Auto,
    Dark,
    Light,
}

public sealed class AppSettings
{
    public SpeedUnit Unit { get; set; } = SpeedUnit.Bytes;

    public int RefreshIntervalMs { get; set; } = 1000;

    /// <summary>Adapter shown in the widget; null measures all non-virtual adapters.</summary>
    public string? AdapterId { get; set; }

    public bool ExcludeVirtualAdapters { get; set; } = true;

    public bool ShowWidget { get; set; } = true;

    public bool AlwaysOnTop { get; set; } = true;

    public bool LockPosition { get; set; }

    public bool ClickThrough { get; set; }

    public bool HideWhenFullscreen { get; set; } = true;

    public double Opacity { get; set; } = 0.92;

    public float FontSize { get; set; } = 9f;

    /// <summary>Widget colors; Auto follows the taskbar.</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.Auto;

    /// <summary>Main window colors; Auto follows the Windows app mode. Applied at start-up.</summary>
    public ThemeMode WindowTheme { get; set; } = ThemeMode.Auto;

    public int? WidgetX { get; set; }

    public int? WidgetY { get; set; }

    public int UsageRetentionDays { get; set; } = 400;

    public DataPlanSettings Plan { get; set; } = new();

    /// <summary>Clamps values edited by hand or written by older versions into supported ranges.</summary>
    public AppSettings Normalize()
    {
        RefreshIntervalMs = Math.Clamp(RefreshIntervalMs, 250, 10_000);
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.3, 1.0) : 0.92;
        FontSize = float.IsFinite(FontSize) ? Math.Clamp(FontSize, 7f, 24f) : 9f;
        UsageRetentionDays = Math.Clamp(UsageRetentionDays, 31, 3660);
        if (!Enum.IsDefined(Unit))
        {
            Unit = SpeedUnit.Bytes;
        }

        if (!Enum.IsDefined(WindowTheme))
        {
            WindowTheme = ThemeMode.Auto;
        }

        if (!Enum.IsDefined(Theme))
        {
            Theme = ThemeMode.Auto;
        }

        Plan ??= new DataPlanSettings();
        Plan.Normalize();
        return this;
    }
}

public sealed class DataPlanSettings
{
    public bool Enabled { get; set; }

    public double LimitGb { get; set; } = 100;

    public int CycleStartDay { get; set; } = 1;

    public int WarnPercent { get; set; } = 80;

    public bool CountUpload { get; set; } = true;

    /// <summary>Start of the cycle the last alert was shown for, so each level alerts once per cycle.</summary>
    public DateOnly? AlertedCycleStart { get; set; }

    public PlanAlertLevel AlertedLevel { get; set; }

    public long LimitBytes => (long)(LimitGb * 1024 * 1024 * 1024);

    public void Normalize()
    {
        LimitGb = double.IsFinite(LimitGb) ? Math.Clamp(LimitGb, 0.1, 1_000_000) : 100;
        CycleStartDay = Math.Clamp(CycleStartDay, 1, 31);
        WarnPercent = Math.Clamp(WarnPercent, 1, 100);
        if (!Enum.IsDefined(AlertedLevel))
        {
            AlertedLevel = PlanAlertLevel.None;
        }
    }

    /// <summary>
    /// Returns the level to notify about now, or <see cref="PlanAlertLevel.None"/> if this level
    /// (or a higher one) was already shown in the current cycle. Records the returned level.
    /// </summary>
    public PlanAlertLevel TakePendingAlert(BillingCycle cycle, PlanStatus status)
    {
        if (AlertedCycleStart != cycle.Start)
        {
            AlertedCycleStart = cycle.Start;
            AlertedLevel = PlanAlertLevel.None;
        }

        if (!Enabled || status.Level <= AlertedLevel)
        {
            return PlanAlertLevel.None;
        }

        AlertedLevel = status.Level;
        return status.Level;
    }
}
