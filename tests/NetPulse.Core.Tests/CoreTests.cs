using System.Globalization;
using System.Net.NetworkInformation;
using NetPulse.Core;

namespace NetPulse.Core.Tests;

public class ByteFormatterTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(512, "512 B/s")]
    [InlineData(1024, "1.00 KB/s")]
    [InlineData(1536, "1.50 KB/s")]
    [InlineData(12.5 * 1024, "12.5 KB/s")]
    [InlineData(123.4 * 1024, "123 KB/s")]
    [InlineData(1023.9 * 1024, "1.00 MB/s")] // rounding would print "1024 KB/s"; promote instead
    [InlineData(5 * 1024 * 1024, "5.00 MB/s")]
    [InlineData(-5, "0 B/s")]
    public void FormatsBytesPerSecond(double value, string expected) =>
        Assert.Equal(expected, ByteFormatter.FormatSpeed(value, SpeedUnit.Bytes, Inv));

    [Theory]
    [InlineData(125, "1.00 Kbps")]          // 1000 bits
    [InlineData(12_500_000, "100 Mbps")]    // 100 Mbit
    [InlineData(125_000_000, "1.00 Gbps")]
    public void FormatsBitsPerSecond(double bytesPerSecond, string expected) =>
        Assert.Equal(expected, ByteFormatter.FormatSpeed(bytesPerSecond, SpeedUnit.Bits, Inv));

    [Fact]
    public void FormatsSizes()
    {
        Assert.Equal("0 B", ByteFormatter.FormatSize(-1, Inv));
        Assert.Equal("1.00 GB", ByteFormatter.FormatSize(1L << 30, Inv));
        Assert.Equal("2.50 TB", ByteFormatter.FormatSize((long)(2.5 * (1L << 40)), Inv));
    }
}

public class SpeedSamplerTests
{
    private static Dictionary<string, TrafficCounters> Snap(params (string Id, long Rx, long Tx)[] items) =>
        items.ToDictionary(i => i.Id, i => new TrafficCounters(i.Rx, i.Tx));

    [Fact]
    public void FirstSampleIsZero()
    {
        var sampler = new SpeedSampler();
        Assert.Equal(SpeedSample.Zero, sampler.Sample(Snap(("a", 1000, 1000)), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ComputesRateFromElapsedTime()
    {
        var sampler = new SpeedSampler();
        sampler.Sample(Snap(("a", 1000, 500)), TimeSpan.FromSeconds(10));
        var sample = sampler.Sample(Snap(("a", 5000, 1500)), TimeSpan.FromSeconds(12));
        Assert.Equal(2000, sample.DownloadBytesPerSecond);
        Assert.Equal(500, sample.UploadBytesPerSecond);
        Assert.Equal(4000, sample.DownloadedBytes);
        Assert.Equal(1000, sample.UploadedBytes);
    }

    [Fact]
    public void IgnoresNewInterfacesAndCounterResets()
    {
        var sampler = new SpeedSampler();
        sampler.Sample(Snap(("a", 1000, 1000)), TimeSpan.FromSeconds(1));
        // "a" reset its counters (e.g. adapter re-enabled); "b" just appeared.
        var sample = sampler.Sample(Snap(("a", 10, 10), ("b", 999_999, 999_999)), TimeSpan.FromSeconds(2));
        Assert.Equal(0, sample.DownloadedBytes);
        Assert.Equal(0, sample.UploadedBytes);

        // From now on both count.
        sample = sampler.Sample(Snap(("a", 110, 10), ("b", 1_000_099, 999_999)), TimeSpan.FromSeconds(3));
        Assert.Equal(200, sample.DownloadedBytes);
    }

    [Fact]
    public void ResetStartsFresh()
    {
        var sampler = new SpeedSampler();
        sampler.Sample(Snap(("a", 0, 0)), TimeSpan.FromSeconds(1));
        sampler.Reset();
        Assert.Equal(SpeedSample.Zero, sampler.Sample(Snap(("a", 5000, 5000)), TimeSpan.FromSeconds(2)));
    }
}

public class BillingCycleTests
{
    [Theory]
    [InlineData("2026-10-15", 1, "2026-10-01", "2026-10-31")]
    [InlineData("2026-10-15", 20, "2026-09-20", "2026-10-19")]
    [InlineData("2026-10-20", 20, "2026-10-20", "2026-11-19")]
    [InlineData("2026-02-28", 31, "2026-02-28", "2026-03-30")] // Feb has no 31st: renews on the 28th
    [InlineData("2026-03-15", 31, "2026-02-28", "2026-03-30")]
    [InlineData("2026-03-31", 31, "2026-03-31", "2026-04-29")]
    [InlineData("2026-01-05", 10, "2025-12-10", "2026-01-09")] // crosses the year boundary
    public void FindsCycleContainingDay(string today, int startDay, string start, string end)
    {
        var cycle = BillingCycle.Containing(DateOnly.Parse(today, CultureInfo.InvariantCulture), startDay);
        Assert.Equal(DateOnly.Parse(start, CultureInfo.InvariantCulture), cycle.Start);
        Assert.Equal(DateOnly.Parse(end, CultureInfo.InvariantCulture), cycle.End);
    }

    [Fact]
    public void CountsRemainingDaysInclusive()
    {
        var cycle = new BillingCycle(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.Equal(17, cycle.DaysRemaining(new DateOnly(2026, 10, 15)));
        Assert.Equal(1, cycle.DaysRemaining(new DateOnly(2026, 10, 31)));
    }
}

public class DataPlanTests
{
    [Theory]
    [InlineData(79, PlanAlertLevel.None)]
    [InlineData(80, PlanAlertLevel.Warning)]
    [InlineData(100, PlanAlertLevel.Exceeded)]
    [InlineData(150, PlanAlertLevel.Exceeded)]
    public void EvaluatesThresholds(long used, PlanAlertLevel expected) =>
        Assert.Equal(expected, PlanStatus.Evaluate(used, 100, 80).Level);

    [Fact]
    public void AlertsEachLevelOncePerCycle()
    {
        var plan = new DataPlanSettings { Enabled = true };
        var cycle = new BillingCycle(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        var warning = PlanStatus.Evaluate(85, 100, 80);
        var exceeded = PlanStatus.Evaluate(100, 100, 80);

        Assert.Equal(PlanAlertLevel.Warning, plan.TakePendingAlert(cycle, warning));
        Assert.Equal(PlanAlertLevel.None, plan.TakePendingAlert(cycle, warning));
        Assert.Equal(PlanAlertLevel.Exceeded, plan.TakePendingAlert(cycle, exceeded));
        Assert.Equal(PlanAlertLevel.None, plan.TakePendingAlert(cycle, exceeded));

        var next = new BillingCycle(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));
        Assert.Equal(PlanAlertLevel.Warning, plan.TakePendingAlert(next, warning));
    }

    [Fact]
    public void DisabledPlanNeverAlerts()
    {
        var plan = new DataPlanSettings { Enabled = false };
        var cycle = new BillingCycle(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.Equal(PlanAlertLevel.None, plan.TakePendingAlert(cycle, PlanStatus.Evaluate(500, 100, 80)));
    }

    [Fact]
    public void CalculatorRespectsCountUpload()
    {
        var ledger = new UsageLedger();
        ledger.Add(new DateOnly(2026, 10, 3), 60L << 30, 30L << 30);
        var plan = new DataPlanSettings { Enabled = true, LimitGb = 100, CycleStartDay = 1, WarnPercent = 80 };

        Assert.Equal(PlanAlertLevel.Warning, DataPlanCalculator.Evaluate(ledger, plan, new DateOnly(2026, 10, 15)).Status.Level);
        plan.CountUpload = false;
        Assert.Equal(PlanAlertLevel.None, DataPlanCalculator.Evaluate(ledger, plan, new DateOnly(2026, 10, 15)).Status.Level);
    }
}

public class UsageLedgerTests
{
    [Fact]
    public void AccumulatesSumsAndFillsGaps()
    {
        var ledger = new UsageLedger();
        var d1 = new DateOnly(2026, 10, 1);
        ledger.Add(d1, 100, 10);
        ledger.Add(d1, 50, 5);
        ledger.Add(d1.AddDays(2), 1, 1);

        Assert.Equal(new DailyUsage(d1, 150, 15), ledger.Get(d1));
        Assert.Equal(167, ledger.Sum(d1, d1.AddDays(2)).Total);
        var range = ledger.Range(d1, d1.AddDays(2));
        Assert.Equal(3, range.Count);
        Assert.Equal(0, range[1].Total);
    }

    [Fact]
    public void TracksDirtyStateAndPrunes()
    {
        var ledger = new UsageLedger();
        Assert.False(ledger.IsDirty);
        ledger.Add(new DateOnly(2026, 1, 1), 0, 0);
        Assert.False(ledger.IsDirty);
        ledger.Add(new DateOnly(2026, 1, 1), 1, 0);
        ledger.Add(new DateOnly(2026, 6, 1), 1, 0);
        Assert.True(ledger.IsDirty);
        ledger.MarkSaved();

        ledger.Prune(new DateOnly(2026, 3, 1));
        Assert.True(ledger.IsDirty);
        Assert.Single(ledger.ToList());
    }
}

public class NetworkAdapterTests
{
    private static AdapterSnapshot Adapter(string id, bool isVirtual, long rx = 0) =>
        new(id, id, id, NetworkInterfaceType.Ethernet, isVirtual, new TrafficCounters(rx, 0));

    [Theory]
    [InlineData("Ethernet", "Intel(R) Ethernet Connection I219-V", false)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", false)]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", true)]
    [InlineData("Local Area Connection", "TAP-Windows Adapter V9", true)]
    [InlineData("Tailscale", "Tailscale Tunnel", true)]
    public void DetectsVirtualAdapters(string name, string description, bool expected) =>
        Assert.Equal(expected, NetworkAdapters.IsVirtual(name, description));

    [Fact]
    public void SelectsSpecificAdapterOrFallsBackToAll()
    {
        var adapters = new[] { Adapter("eth", false), Adapter("wifi", false), Adapter("hyperv", true) };

        Assert.Equal(new[] { "eth", "wifi" }, NetworkAdapters.Select(adapters, null, excludeVirtual: true).Keys.Order());
        Assert.Equal(3, NetworkAdapters.Select(adapters, null, excludeVirtual: false).Count);
        Assert.Equal(new[] { "hyperv" }, NetworkAdapters.Select(adapters, "hyperv", excludeVirtual: true).Keys);
        // A remembered adapter that is no longer present falls back to automatic selection.
        Assert.Equal(2, NetworkAdapters.Select(adapters, "gone", excludeVirtual: true).Count);
    }
}

public class TrafficMonitorTests
{
    [Fact]
    public void CountsUsageForAllAdaptersRegardlessOfDisplayedAdapter()
    {
        long eth = 0, wifi = 0;
        IReadOnlyList<AdapterSnapshot> Read() =>
        [
            new("eth", "eth", "eth", NetworkInterfaceType.Ethernet, false, new TrafficCounters(eth, 0)),
            new("wifi", "wifi", "wifi", NetworkInterfaceType.Wireless80211, false, new TrafficCounters(wifi, 0)),
        ];
        var day = new DateTime(2026, 10, 15, 12, 0, 0);
        var monitor = new TrafficMonitor(new UsageLedger(), Read, () => day);

        monitor.Tick("eth", excludeVirtual: true);
        eth += 1000;
        wifi += 300;
        Thread.Sleep(20);
        var sample = monitor.Tick("eth", excludeVirtual: true);

        Assert.Equal(1000, sample.DownloadedBytes);
        Assert.Equal(1300, monitor.Ledger.Get(DateOnly.FromDateTime(day)).Downloaded);
    }
}

public class JsonFileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NetPulseTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void RoundTripsSettingsAndUsage()
    {
        var settingsStore = new JsonFileStore<AppSettings>(Path.Combine(_dir, "settings.json"), () => new AppSettings());
        var settings = new AppSettings { Unit = SpeedUnit.Bits, AdapterId = "{abc}", Theme = ThemeMode.Light };
        settings.Plan.Enabled = true;
        settings.Plan.AlertedCycleStart = new DateOnly(2026, 10, 1);
        settings.Plan.AlertedLevel = PlanAlertLevel.Warning;
        settingsStore.Save(settings);

        var loaded = settingsStore.Load();
        Assert.Equal(SpeedUnit.Bits, loaded.Unit);
        Assert.Equal("{abc}", loaded.AdapterId);
        Assert.Equal(new DateOnly(2026, 10, 1), loaded.Plan.AlertedCycleStart);
        Assert.Equal(PlanAlertLevel.Warning, loaded.Plan.AlertedLevel);

        var usageStore = new JsonFileStore<UsageDocument>(Path.Combine(_dir, "usage.json"), () => new UsageDocument());
        usageStore.Save(new UsageDocument { Days = [new DailyUsage(new DateOnly(2026, 10, 15), 123, 45)] });
        Assert.Equal(new DailyUsage(new DateOnly(2026, 10, 15), 123, 45), usageStore.Load().Days.Single());
    }

    [Fact]
    public void CorruptFileFallsBackToDefaultsAndIsKeptAside()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        var store = new JsonFileStore<AppSettings>(path, () => new AppSettings());

        Assert.Equal(1000, store.Load().RefreshIntervalMs);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void NormalizeClampsOutOfRangeValues()
    {
        var settings = new AppSettings { RefreshIntervalMs = 1, Opacity = 7, FontSize = float.NaN, Unit = (SpeedUnit)42 };
        settings.Plan.CycleStartDay = 99;
        settings.Normalize();

        Assert.Equal(250, settings.RefreshIntervalMs);
        Assert.Equal(1.0, settings.Opacity);
        Assert.Equal(9f, settings.FontSize);
        Assert.Equal(SpeedUnit.Bytes, settings.Unit);
        Assert.Equal(31, settings.Plan.CycleStartDay);
    }
}
