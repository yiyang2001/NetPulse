using NetPulse.Core;

namespace NetPulse.Core.Tests;

public class SpeedHistoryTests
{
    private static SpeedSample Rate(double down, double up) => new(down, up, 0, 0);

    [Fact]
    public void DropsPointsOlderThanSpan()
    {
        var history = new SpeedHistory(TimeSpan.FromSeconds(10));
        for (var s = 0; s <= 20; s++)
        {
            history.Add(TimeSpan.FromSeconds(s), Rate(s, 0));
        }

        Assert.Equal(11, history.Count); // 10..20 inclusive
        Assert.Equal(10, history.Recent(TimeSpan.FromHours(1))[0].DownloadBytesPerSecond);
    }

    [Fact]
    public void RecentIsRelativeToNewestPoint()
    {
        var history = new SpeedHistory(TimeSpan.FromMinutes(10));
        for (var s = 0; s < 100; s++)
        {
            history.Add(TimeSpan.FromSeconds(s), Rate(s, 0));
        }

        var recent = history.Recent(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 94.0, 95, 96, 97, 98, 99 }, recent.Select(p => p.DownloadBytesPerSecond));
    }

    [Fact]
    public void ClockGoingBackwardsStartsOver()
    {
        var history = new SpeedHistory(TimeSpan.FromMinutes(1));
        history.Add(TimeSpan.FromSeconds(50), Rate(1, 1));
        history.Add(TimeSpan.FromSeconds(51), Rate(1, 1));
        history.Add(TimeSpan.FromSeconds(2), Rate(7, 7));
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void SummarizesPeakAndAverage()
    {
        var points = new[]
        {
            new SpeedPoint(TimeSpan.Zero, 100, 10),
            new SpeedPoint(TimeSpan.FromSeconds(1), 300, 30),
            new SpeedPoint(TimeSpan.FromSeconds(2), 200, 50),
        };
        Assert.Equal(new SpeedStats(300, 50, 200, 30), SpeedHistory.Summarize(points));
        Assert.Equal(SpeedStats.Zero, SpeedHistory.Summarize([]));
    }

    [Theory]
    [InlineData(0, 1024)]                       // floor: 1 KB/s
    [InlineData(1500, 2 * 1024)]                // 1.46 KB/s -> 2 KB/s
    [InlineData(4.77 * 1024 * 1024, 5 * 1024 * 1024)]
    [InlineData(600 * 1024, 1024 * 1024)]       // 600 KB/s -> 1 MB/s, not 1000 KB/s
    [InlineData(1024 * 1024, 1024 * 1024)]      // exact values stay
    [InlineData(30 * 1024 * 1024, 50 * 1024 * 1024)]
    public void NiceCeilingInBytes(double input, double expected)
    {
        Assert.Equal(expected, NiceScale.Ceiling(input, SpeedUnit.Bytes), 6);
    }

    [Theory]
    [InlineData(0, 10_000)]                     // floor: 10 Kbps
    [InlineData(37_500_000, 500_000_000)]       // 300 Mbps -> 500 Mbps
    [InlineData(150_000_000, 2_000_000_000)]    // 1.2 Gbps -> 2 Gbps
    [InlineData(80_000, 1_000_000)]             // 640 Kbps -> 1 Mbps
    public void NiceCeilingInBits(double inputBytes, double expectedBits)
    {
        Assert.Equal(expectedBits, NiceScale.Ceiling(inputBytes, SpeedUnit.Bits) * 8, 3);
    }
}

public class AxisFormatTests
{
    [Theory]
    [InlineData(5 * 1024 * 1024, "5 MB/s")]
    [InlineData(2.5 * 1024 * 1024, "2.5 MB/s")]
    [InlineData(50 * 1024, "50 KB/s")]
    [InlineData(1.25 * 1024, "1.25 KB/s")]
    public void TrimsTrailingZeros(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, ByteFormatter.FormatSpeed(bytesPerSecond, SpeedUnit.Bytes, System.Globalization.CultureInfo.InvariantCulture, trimZeros: true));
    }
}

public class ProcessHistoryTests
{
    private static readonly DateOnly Day1 = new(2026, 10, 1);
    private static readonly DateOnly Day2 = new(2026, 10, 2);

    private static Dictionary<int, TrafficCounters> Counters(params (int Pid, long Down, long Up)[] rows) =>
        rows.ToDictionary(r => r.Pid, r => new TrafficCounters(r.Down, r.Up));

    [Fact]
    public void RecordsGrowthUnderProgramName()
    {
        var names = new Dictionary<int, string> { [10] = "chrome", [11] = "chrome", [20] = "steam" };
        var recorder = new ProcessHistoryRecorder(new ProcessUsageLedger(), pid => names[pid]);

        recorder.Record(Counters((10, 100, 10), (20, 1000, 0)), Day1);
        recorder.Record(Counters((10, 150, 10), (11, 50, 5), (20, 1000, 0)), Day1);

        var totals = recorder.Ledger.Totals(Day1, Day1);
        Assert.Equal(new[] { new AppUsage("steam", 1000, 0), new AppUsage("chrome", 200, 15) }, totals);
    }

    [Fact]
    public void CounterResetCountsNewBytesOnly()
    {
        var recorder = new ProcessHistoryRecorder(new ProcessUsageLedger(), _ => "app");
        recorder.Record(Counters((1, 500, 0)), Day1);
        recorder.Record(Counters((1, 30, 0)), Day1); // table was cleared, then 30 bytes arrived

        Assert.Equal(530, recorder.Ledger.Totals(Day1, Day1).Single().Downloaded);
    }

    [Fact]
    public void ResetBaselineAfterClearDoesNotDoubleCount()
    {
        var recorder = new ProcessHistoryRecorder(new ProcessUsageLedger(), _ => "app");
        recorder.Record(Counters((1, 500, 0)), Day1);
        recorder.ResetBaseline();
        recorder.Record(Counters((1, 700, 0)), Day1); // counters restarted at 0 and reached 700

        Assert.Equal(1200, recorder.Ledger.Totals(Day1, Day1).Single().Downloaded);
    }

    [Fact]
    public void BooksIntoTheDateOfEachCall()
    {
        var recorder = new ProcessHistoryRecorder(new ProcessUsageLedger(), _ => "app");
        recorder.Record(Counters((1, 100, 0)), Day1);
        recorder.Record(Counters((1, 250, 0)), Day2);

        Assert.Equal(100, recorder.Ledger.Totals(Day1, Day1).Single().Downloaded);
        Assert.Equal(150, recorder.Ledger.Totals(Day2, Day2).Single().Downloaded);
        Assert.Equal(250, recorder.Ledger.Totals(Day1, Day2).Single().Downloaded);
    }

    [Fact]
    public void LedgerRoundTripsThroughDocumentAndMergesNamesIgnoringCase()
    {
        var ledger = new ProcessUsageLedger();
        ledger.Add(Day1, "Code", 10, 1);
        ledger.Add(Day1, "code", 5, 0);
        ledger.Add(Day2, "steam", 0, 0); // nothing to book
        Assert.True(ledger.IsDirty);

        var copy = new ProcessUsageLedger(ledger.ToDocument().Days);
        Assert.False(copy.IsDirty);
        Assert.Equal(new[] { new AppUsage("Code", 15, 1) }, copy.Totals(Day1, Day2));

        copy.Prune(Day2);
        Assert.Empty(copy.Totals(Day1, Day2));
        Assert.True(copy.IsDirty);
    }

    [Fact]
    public void LedgerSurvivesJsonStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netpulse-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonFileStore<ProcessUsageDocument>(path, () => new ProcessUsageDocument());
            var ledger = new ProcessUsageLedger();
            ledger.Add(Day1, "firefox", 123, 45);
            store.Save(ledger.ToDocument());

            var loaded = new ProcessUsageLedger(store.Load().Days);
            Assert.Equal(new[] { new AppUsage("firefox", 123, 45) }, loaded.Totals(Day1, Day1));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class LogonTaskTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";

    private static System.Xml.Linq.XElement Parse(string xml) => System.Xml.Linq.XDocument.Parse(xml).Root!;

    private static string Value(System.Xml.Linq.XElement root, params string[] path)
    {
        var element = root;
        foreach (var name in path)
        {
            element = element.Element(LogonTask.Namespace + name)!;
        }

        return element.Value;
    }

    [Fact]
    public void StartsElevatedForThatUserAtLogon()
    {
        var root = Parse(LogonTask.Build(Sid, @"C:\Program Files\NetPulse\NetPulse.exe", null, "d"));
        Assert.Equal(Sid, Value(root, "Triggers", "LogonTrigger", "UserId"));
        Assert.Equal(Sid, Value(root, "Principals", "Principal", "UserId"));
        Assert.Equal("InteractiveToken", Value(root, "Principals", "Principal", "LogonType"));
        Assert.Equal("HighestAvailable", Value(root, "Principals", "Principal", "RunLevel"));
        Assert.Equal(@"C:\Program Files\NetPulse\NetPulse.exe", Value(root, "Actions", "Exec", "Command"));
    }

    [Fact]
    public void RunsWithoutTimeLimitAtNormalPriority()
    {
        var root = Parse(LogonTask.Build(Sid, "x.exe", null, "d"));
        Assert.Equal("PT0S", Value(root, "Settings", "ExecutionTimeLimit"));
        Assert.Equal("4", Value(root, "Settings", "Priority"));
        Assert.Equal("IgnoreNew", Value(root, "Settings", "MultipleInstancesPolicy"));
        Assert.Equal("false", Value(root, "Settings", "DisallowStartIfOnBatteries"));
    }

    [Fact]
    public void ArgumentsOnlyWhenGivenAndEscaped()
    {
        var withoutArgs = Parse(LogonTask.Build(Sid, "x.exe", "", "d"));
        Assert.Null(withoutArgs.Element(LogonTask.Namespace + "Actions")!.Element(LogonTask.Namespace + "Exec")!.Element(LogonTask.Namespace + "Arguments"));

        var xml = LogonTask.Build(Sid, "x.exe", "--profile=dev", "a < b & c");
        Assert.Equal("--profile=dev", Value(Parse(xml), "Actions", "Exec", "Arguments"));
        Assert.Equal("a < b & c", Value(Parse(xml), "RegistrationInfo", "Description"));
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
    }
}
