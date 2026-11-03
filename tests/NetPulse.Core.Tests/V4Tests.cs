using System.Drawing;
using NetPulse.Core;

namespace NetPulse.Core.Tests;

public class CompactSpeedTests
{
    [Theory]
    [InlineData(0, SpeedUnit.Bytes, "0", 0)]
    [InlineData(856, SpeedUnit.Bytes, "856", 0)]
    [InlineData(1024, SpeedUnit.Bytes, "1.0", 1)]
    [InlineData(1000, SpeedUnit.Bytes, "1.0", 1)] // not "1000"
    [InlineData(1.5 * 1024 * 1024, SpeedUnit.Bytes, "1.5", 2)]
    [InlineData(45.4 * 1024, SpeedUnit.Bytes, "45", 1)]
    [InlineData(125_000, SpeedUnit.Bits, "1.0", 2)] // 1 Mbps
    [InlineData(-5, SpeedUnit.Bytes, "0", 0)]
    public void FitsInThreeCharacters(double bytesPerSecond, SpeedUnit unit, string text, int unitIndex)
    {
        var (actualText, actualIndex) = ByteFormatter.FormatCompact(bytesPerSecond, unit);
        Assert.Equal(text, actualText);
        Assert.Equal(unitIndex, actualIndex);
        Assert.True(actualText.Length <= 3);
    }

    [Fact]
    public void NeverNeedsFourCharactersAcrossTheRange()
    {
        for (double v = 0; v < 5e11; v = (v * 1.07) + 1)
        {
            Assert.True(ByteFormatter.FormatCompact(v, SpeedUnit.Bytes).Text.Length <= 3, $"{v}");
            Assert.True(ByteFormatter.FormatCompact(v, SpeedUnit.Bits).Text.Length <= 3, $"{v}");
        }
    }
}

public class WidgetPlacementTests
{
    private static readonly Rectangle Screen1080 = new(0, 0, 1920, 1040);

    [Fact]
    public void KeepsDistanceFromTheNearestCornerAcrossResolutions()
    {
        var bounds = new Rectangle(1920 - 100 - 16, 1040 - 40 - 16, 100, 40);
        var placement = WidgetPlacement.Capture("\\\\.\\DISPLAY1", Screen1080, bounds, 1.0);

        Assert.True(placement.Right);
        Assert.True(placement.Bottom);
        Assert.Equal(16, placement.OffsetX);

        // Same monitor at 150 % scaling and a smaller work area: still 16 logical units from the corner.
        var scaled = new Rectangle(0, 0, 2560, 1400);
        var point = placement.Resolve(scaled, new Size(150, 60), 1.5);
        Assert.Equal(2560 - 150 - 24, point.X);
        Assert.Equal(1400 - 60 - 24, point.Y);
    }

    [Fact]
    public void TopLeftAnchorUsesTheWorkAreaOrigin()
    {
        var area = new Rectangle(1920, 40, 1920, 1040); // second monitor with a top taskbar
        var placement = WidgetPlacement.Capture("b", area, new Rectangle(1920 + 10, 40 + 20, 100, 40), 1.0);

        Assert.False(placement.Right);
        Assert.False(placement.Bottom);
        Assert.Equal(new Point(1930, 60), placement.Resolve(area, new Size(100, 40), 1.0));
    }

    [Fact]
    public void ResolveStaysInsideASmallerScreen()
    {
        var placement = new WidgetPlacement { OffsetX = 5000, OffsetY = 5000 };
        var area = new Rectangle(0, 0, 800, 600);
        var point = placement.Resolve(area, new Size(100, 40), 1.0);
        Assert.Equal(new Point(700, 560), point);
    }

    [Fact]
    public void InvalidSavedPlacementIsDropped()
    {
        var settings = new AppSettings { Placement = new WidgetPlacement { OffsetX = double.NaN } };
        Assert.Null(settings.Normalize().Placement);
    }

    [Fact]
    public void OldSettingsFilesKeepWorking()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"WidgetX": 10, "WidgetY": 20}""")!.Normalize();
        Assert.Equal(WidgetLayout.TwoLines, settings.Layout);
        Assert.Equal(TrayIconMode.Logo, settings.TrayIcon);
        Assert.False(settings.ShowMiniGraph);
        Assert.Null(settings.Placement);
        Assert.Equal(10, settings.WidgetX);
    }
}

public class NetworkUsageTests
{
    private static AdapterSnapshot Nic(string id, string name, long rx, long tx, bool virt = false) =>
        new(id, name, name, System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, virt, new TrafficCounters(rx, tx));

    private static readonly DateOnly Day = new(2026, 11, 3);

    [Fact]
    public void SplitsTrafficByNetworkAndSumsAdaptersOfTheSameNetwork()
    {
        var sampler = new NetworkSampler();
        Func<AdapterSnapshot, NetworkIdentity> identify = a => a.Id.StartsWith('w') ? NetworkIdentity.ForWifi("Home") : NetworkIdentity.ForAdapter(a);

        Assert.Empty(sampler.Sample([Nic("w1", "Wi-Fi", 0, 0), Nic("e1", "Ethernet", 0, 0)], identify));
        var deltas = sampler.Sample([Nic("w1", "Wi-Fi", 100, 10), Nic("w2", "Wi-Fi 2", 5, 5), Nic("e1", "Ethernet", 7, 3)], identify);

        Assert.Equal(2, deltas.Count);
        Assert.Contains(deltas, d => d.Network.Key == "wifi:Home" && d.Downloaded == 100 && d.Uploaded == 10);
        Assert.Contains(deltas, d => d.Network.Key == "adapter:Ethernet" && d.Downloaded == 7 && d.Uploaded == 3);
    }

    [Fact]
    public void CounterResetContributesNothing()
    {
        var sampler = new NetworkSampler();
        sampler.Sample([Nic("e1", "Ethernet", 1000, 1000)], NetworkIdentity.ForAdapter);
        Assert.Empty(sampler.Sample([Nic("e1", "Ethernet", 10, 10)], NetworkIdentity.ForAdapter));
    }

    [Fact]
    public void NetworksAddUpToTheOverallTotal()
    {
        var step = 0;
        Func<IReadOnlyList<AdapterSnapshot>> read = () =>
        {
            step++;
            return [Nic("e1", "Ethernet", step * 1000, step * 100), Nic("w1", "Wi-Fi", step * 50, step * 5), Nic("v1", "vEthernet", step * 9999, 0, virt: true)];
        };
        var monitor = new TrafficMonitor(new UsageLedger(), read, () => new DateTime(2026, 11, 3, 12, 0, 0));

        for (var i = 0; i < 4; i++)
        {
            monitor.Tick(null, excludeVirtual: true);
        }

        var all = monitor.Ledger.Get(Day);
        var networks = monitor.Networks.Networks.Select(n => monitor.Networks.Get(n.Key)!.Get(Day)).ToList();
        Assert.Equal(2, networks.Count); // the virtual adapter is not attributed
        Assert.Equal(all.Downloaded, networks.Sum(n => n.Downloaded));
        Assert.Equal(all.Uploaded, networks.Sum(n => n.Uploaded));
        Assert.Equal(3 * 1050, all.Downloaded);
    }

    [Fact]
    public void LedgerRoundTripsAndDropsEmptyNetworks()
    {
        var ledger = new NetworkUsageLedger();
        var home = NetworkIdentity.ForWifi("Home");
        var phone = NetworkIdentity.ForWifi("Phone");
        ledger.Add(home, Day, 10, 1);
        ledger.Add(phone, Day.AddDays(-100), 5, 5);
        Assert.True(ledger.IsDirty);

        var copy = new NetworkUsageLedger(ledger.ToDocument().Networks);
        Assert.Equal(["Home", "Phone"], copy.Networks.Select(n => n.Label));
        Assert.Equal(10, copy.Get(home.Key)!.Get(Day).Downloaded);

        ledger.MarkSaved();
        Assert.False(ledger.IsDirty);
        ledger.Prune(Day.AddDays(-30));
        Assert.True(ledger.IsDirty);
        Assert.Null(ledger.Get(phone.Key));
        Assert.NotNull(ledger.Get(home.Key));
    }

    [Fact]
    public void RenamedNetworkKeepsItsHistory()
    {
        var ledger = new NetworkUsageLedger();
        ledger.Add(new NetworkIdentity("wifi:x", "Old"), Day, 1, 1);
        ledger.Add(new NetworkIdentity("wifi:x", "New"), Day, 1, 1);
        Assert.Equal("New", Assert.Single(ledger.Networks).Label);
        Assert.Equal(2, ledger.Get("wifi:x")!.Get(Day).Downloaded);
    }

    [Fact]
    public void SettingsKeepNetworkPlans()
    {
        var settings = new AppSettings { NetworkPlans = { ["wifi:Phone"] = new DataPlanSettings { Enabled = true, LimitGb = 5 } } };
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!.Normalize();
        Assert.Equal(5, loaded.NetworkPlans["wifi:Phone"].LimitGb);
        Assert.Empty(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.Normalize().NetworkPlans);
    }
}
