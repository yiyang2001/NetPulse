using NetPulse.Core;

namespace NetPulse.Core.Tests;

public class SustainedSpeedDetectorTests
{
    private static TimeSpan S(int seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void FiresOnceAfterTheWholeDurationAboveTheLimit()
    {
        var detector = new SustainedSpeedDetector(100, S(60));
        var fired = new List<int>();
        for (var t = 0; t <= 200; t++)
        {
            if (detector.Feed(S(t), 150))
            {
                fired.Add(t);
            }
        }

        Assert.Equal([60], fired);
    }

    [Fact]
    public void ADipRestartsTheCountAndRearms()
    {
        var detector = new SustainedSpeedDetector(100, S(10));
        for (var t = 0; t < 9; t++)
        {
            Assert.False(detector.Feed(S(t), 500));
        }

        Assert.False(detector.Feed(S(9), 10)); // dip: starts over
        for (var t = 10; t < 20; t++)
        {
            Assert.False(detector.Feed(S(t), 500));
        }

        Assert.True(detector.Feed(S(20), 500));
        Assert.False(detector.Feed(S(21), 500));
        detector.Feed(S(22), 0);
        for (var t = 23; t < 33; t++)
        {
            detector.Feed(S(t), 500);
        }

        Assert.True(detector.Feed(S(33), 500)); // fires again after having dropped below
    }
}

public class ProgramLimitTrackerTests
{
    private static readonly DateOnly Day = new(2026, 11, 3);

    [Fact]
    public void ReportsEachProgramOncePerDay()
    {
        var ledger = new ProcessUsageLedger();
        var tracker = new ProgramLimitTracker();
        ledger.Add(Day, "a.exe", 600, 0);
        Assert.Empty(tracker.NewlyOver(ledger, Day, 1000));

        ledger.Add(Day, "A.EXE", 0, 500); // names are case-insensitive
        ledger.Add(Day, "b.exe", 10, 0);
        Assert.Equal("a.exe", Assert.Single(tracker.NewlyOver(ledger, Day, 1000)).Name);
        Assert.Empty(tracker.NewlyOver(ledger, Day, 1000));

        ledger.Add(Day.AddDays(1), "a.exe", 2000, 0);
        Assert.Single(tracker.NewlyOver(ledger, Day.AddDays(1), 1000)); // next day: again
    }

    [Fact]
    public void NoLimitNoAlert() => Assert.Empty(new ProgramLimitTracker().NewlyOver(new ProcessUsageLedger(), Day, 0));
}

public class ReportTests
{
    [Fact]
    public void WeeklySummaryCoversLastMondayToSundayOnce()
    {
        var ledger = new UsageLedger();
        var wed = new DateOnly(2026, 11, 4); // Wednesday
        Assert.Equal(DayOfWeek.Wednesday, wed.DayOfWeek);
        ledger.Add(new DateOnly(2026, 10, 26), 100, 10); // Monday of last week
        ledger.Add(new DateOnly(2026, 11, 1), 50, 5);    // Sunday of last week
        ledger.Add(new DateOnly(2026, 10, 25), 999, 0);  // Sunday before: previous week
        ledger.Add(new DateOnly(2026, 11, 2), 7777, 0);  // this Monday: not counted

        var summary = WeeklySummary.DueOn(ledger, wed, null)!;
        Assert.Equal(new DateOnly(2026, 10, 26), summary.From);
        Assert.Equal(new DateOnly(2026, 11, 1), summary.To);
        Assert.Equal(150, summary.Week.Downloaded);
        Assert.Equal(999, summary.PreviousWeek.Downloaded);

        Assert.Null(WeeklySummary.DueOn(ledger, wed, WeeklySummary.MondayOf(wed))); // already shown
        Assert.NotNull(WeeklySummary.DueOn(ledger, wed, new DateOnly(2026, 10, 26)));
    }

    [Fact]
    public void WeeklySummarySkippedWithoutTraffic() =>
        Assert.Null(WeeklySummary.DueOn(new UsageLedger(), new DateOnly(2026, 11, 4), null));

    [Fact]
    public void MondayOfSundayIsTheSixDaysBefore() =>
        Assert.Equal(new DateOnly(2026, 10, 26), WeeklySummary.MondayOf(new DateOnly(2026, 11, 1)));

    [Fact]
    public void MonthsAreCalendarMonthsEndingNow()
    {
        var ledger = new UsageLedger();
        ledger.Add(new DateOnly(2026, 9, 30), 1, 0);
        ledger.Add(new DateOnly(2026, 10, 1), 10, 0);
        ledger.Add(new DateOnly(2026, 10, 31), 20, 2);
        ledger.Add(new DateOnly(2026, 11, 3), 100, 0);

        var months = ledger.Months(3, new DateOnly(2026, 11, 3));
        Assert.Equal([new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1)], months.Select(m => m.Date));
        Assert.Equal([1L, 30L, 100L], months.Select(m => m.Downloaded));
        Assert.Equal(2, months[1].Uploaded);
        Assert.Equal(12, ledger.Months(12, new DateOnly(2026, 1, 15)).Count);
    }
}

public class BackupTests
{
    [Fact]
    public void RoundTripsEverything()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netpulse-backup-{Guid.NewGuid():N}.json");
        try
        {
            var ledger = new UsageLedger();
            ledger.Add(new DateOnly(2026, 11, 3), 5, 6);
            var networks = new NetworkUsageLedger();
            networks.Add(NetworkIdentity.ForWifi("Home"), new DateOnly(2026, 11, 3), 5, 6);
            var programs = new ProcessUsageLedger();
            programs.Add(new DateOnly(2026, 11, 3), "a.exe", 1, 2);
            BackupDocument.Write(path, new BackupDocument
            {
                Settings = new AppSettings { Layout = WidgetLayout.OneLine, SpeedAlert = { Enabled = true, Minutes = 7 } },
                Usage = new UsageDocument { Days = [.. ledger.ToList()] },
                NetworkUsage = networks.ToDocument(),
                ProcessUsage = programs.ToDocument(),
            });

            var backup = BackupDocument.Read(path);
            Assert.Equal(WidgetLayout.OneLine, backup.Settings.Layout);
            Assert.True(backup.Settings.SpeedAlert.Enabled);
            Assert.Equal(7, backup.Settings.SpeedAlert.Minutes);
            Assert.Equal(5, Assert.Single(backup.Usage.Days).Downloaded);
            Assert.Equal("Home", Assert.Single(backup.NetworkUsage.Networks).Label);
            Assert.Equal("a.exe", Assert.Single(backup.ProcessUsage.Days).Apps[0].Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"Format\": 99}")]
    [InlineData("{\"Format\": 0}")]
    public void RejectsFilesThatAreNotUsableBackups(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"netpulse-bad-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        try
        {
            Assert.Throws<InvalidDataException>(() => BackupDocument.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SettingsFromBeforeV05GetAlertDefaults()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.Normalize();
        Assert.False(settings.SpeedAlert.Enabled);
        Assert.False(settings.ProgramAlert.Enabled);
        Assert.True(settings.WeeklySummary);
    }
}
