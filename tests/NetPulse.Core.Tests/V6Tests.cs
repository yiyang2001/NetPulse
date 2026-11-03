using System.Net;
using System.Net.Sockets;
using NetPulse.Core;

namespace NetPulse.Core.Tests;

public class WifiChannelTests
{
    [Theory]
    [InlineData(2412, 1, "2.4 GHz")]
    [InlineData(2437, 6, "2.4 GHz")]
    [InlineData(2472, 13, "2.4 GHz")]
    [InlineData(2484, 14, "2.4 GHz")]
    [InlineData(5180, 36, "5 GHz")]
    [InlineData(5745, 149, "5 GHz")]
    [InlineData(5955, 1, "6 GHz")]
    [InlineData(6415, 93, "6 GHz")]
    public void FrequencyMapsToChannelAndBand(int mhz, int channel, string band)
    {
        Assert.Equal(channel, WifiChannels.ChannelOf(mhz));
        Assert.Equal(band, WifiChannels.BandOf(mhz));
    }

    [Fact]
    public void UnknownFrequencyHasNoChannel() => Assert.Equal(0, WifiChannels.ChannelOf(900));

    private static WifiAccessPoint Ap(int mhz, int rssi, string id = "x") =>
        new("net" + id, id, rssi, 50, mhz, "802.11n", false);

    [Fact]
    public void RecommendsTheEmptyOneOfTheThreeNonOverlappingChannels()
    {
        // Strong networks on 1 and 6 (and one on 3 overlapping both): 11 is the free one.
        var aps = new[] { Ap(2412, -40, "a"), Ap(2437, -50, "b"), Ap(2422, -60, "c") };
        Assert.Equal(11, WifiChannels.Recommend(aps, "2.4 GHz"));
    }

    [Fact]
    public void OverlapFadesWithDistance()
    {
        var load = WifiChannels.Congestion([Ap(2437, -30)], "2.4 GHz").ToDictionary(c => c.Channel, c => c.Load);
        Assert.True(load[6] > load[7]);
        Assert.True(load[7] > load[9]);
        Assert.Equal(0, load[11]); // five channels away: no overlap
        Assert.Equal(0, load[1]);
    }

    [Fact]
    public void FiveGhzChannelsDoNotOverlap()
    {
        var aps = new[] { Ap(5180, -40, "a"), Ap(5180, -50, "b"), Ap(5200, -45, "c") };
        var load = WifiChannels.Congestion(aps, "5 GHz").ToDictionary(c => c.Channel);
        Assert.Equal(2, load[36].Networks);
        Assert.Equal(1, load[40].Networks);
        Assert.Equal(0, load[44].Load);
        Assert.NotEqual(36, WifiChannels.Recommend(aps, "5 GHz"));
    }

    [Fact]
    public void SixGhzGetsNoRecommendation() => Assert.Null(WifiChannels.Recommend([Ap(5955, -40)], "6 GHz"));

    [Fact]
    public void StrongerSignalsWeighMore() =>
        Assert.True(WifiChannels.Strength(-35) > WifiChannels.Strength(-80));
}

public class LanScanTests
{
    private static LanNetwork Net(string ip, int prefix, string? gateway = null) =>
        new(IPAddress.Parse(ip), prefix, gateway is null ? null : IPAddress.Parse(gateway));

    [Fact]
    public void HostsExcludeNetworkBroadcastAndSelf()
    {
        var hosts = Net("192.168.1.10", 24).Hosts();
        Assert.Equal(253, hosts.Count);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.0"), hosts);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.255"), hosts);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.10"), hosts);
        Assert.Contains(IPAddress.Parse("192.168.1.1"), hosts);
    }

    [Fact]
    public void BigNetworksAreNarrowedToTheSlash24AroundThisPc()
    {
        var hosts = Net("10.20.30.40", 16).Hosts();
        Assert.Equal(253, hosts.Count);
        Assert.All(hosts, h => Assert.StartsWith("10.20.30.", h.ToString()));
    }

    [Fact]
    public void SmallNetworksKeepTheirOwnRange()
    {
        var hosts = Net("192.168.1.130", 26).Hosts(); // .128-.191: 62 usable addresses, minus this PC
        Assert.Equal(61, hosts.Count);
        Assert.All(hosts, h => Assert.True(LanNetwork.ToValue(h) is > 0xC0A80180 and < 0xC0A801BF));
    }

    [Fact]
    public void PointToPointNetworksHaveNothingToSweep() => Assert.Empty(Net("10.0.0.1", 31).Hosts());

    [Fact]
    public void ContainsUsesTheMask()
    {
        var net = Net("192.168.1.10", 24);
        Assert.True(net.Contains(IPAddress.Parse("192.168.1.200")));
        Assert.False(net.Contains(IPAddress.Parse("192.168.2.1")));
        Assert.False(net.Contains(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void ArpNoiseIsFilteredOut()
    {
        var net = Net("192.168.1.10", 24, "192.168.1.1");
        ArpEntry E(string ip, string mac) => new(IPAddress.Parse(ip), mac);
        var table = new[]
        {
            E("192.168.1.1", "AA-BB-CC-00-00-01"),
            E("192.168.1.1", "AA-BB-CC-00-00-01"), // duplicate
            E("192.168.1.255", "FF-FF-FF-FF-FF-FF"),
            E("192.168.1.77", "00-00-00-00-00-00"),
            E("192.168.1.80", ""),
            E("192.168.2.5", "AA-BB-CC-00-00-05"),   // other subnet
            E("224.0.0.22", "01-00-5E-00-00-16"),
            E("192.168.1.42", "AA-BB-CC-00-00-42"),
        };
        var usable = LanScanner.UsableNeighbours(table, net);
        Assert.Equal(["192.168.1.1", "192.168.1.42"], usable.Select(e => e.Address.ToString()));
    }

    [Fact]
    public async Task ScanProbesEveryHostThenReadsArpAndNamesDevices()
    {
        var net = Net("192.168.1.10", 24, "192.168.1.1");
        var probed = 0;
        var devices = await LanScanner.ScanAsync(
            net,
            () => [new ArpEntry(IPAddress.Parse("192.168.1.1"), "AA-BB-CC-00-00-01"), new ArpEntry(IPAddress.Parse("192.168.1.42"), "AA-BB-CC-00-00-42")],
            (_, _) =>
            {
                Interlocked.Increment(ref probed);
                return Task.CompletedTask;
            },
            (ip, _) => Task.FromResult<string?>(ip.ToString().EndsWith(".42") ? "printer" : null),
            progress: null,
            CancellationToken.None);

        Assert.Equal(253, probed);
        Assert.Equal(3, devices.Count);
        Assert.True(devices.Single(d => d.Address.ToString() == "192.168.1.1").IsGateway);
        Assert.Equal("printer", devices.Single(d => d.Address.ToString() == "192.168.1.42").HostName);
        Assert.True(devices.Single(d => d.Address.ToString() == "192.168.1.10").IsThisPc);
    }

    [Fact]
    public async Task ScanWithoutProbeOnlyReadsTheTable()
    {
        var devices = await LanScanner.ScanAsync(Net("192.168.1.10", 24), () => [], probe: null, resolveName: null, progress: null, CancellationToken.None);
        Assert.Single(devices); // only this PC
    }

    [Fact]
    public async Task ScanCanBeCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LanScanner.ScanAsync(Net("192.168.1.10", 24), () => [], (_, _) => Task.CompletedTask, null, null, cts.Token));
    }
}

public class PortCheckTests
{
    [Fact]
    public void ParsesListsAndRanges() =>
        Assert.Equal([22, 80, 443, 8000, 8001, 8002], PortSpec.Parse("443, 80,22 ; 8000-8002, 80"));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("http")]
    [InlineData("-5")]
    [InlineData("10-5")]
    [InlineData("1-2000")]
    public void RejectsBadSpecs(string text) => Assert.Throws<FormatException>(() => PortSpec.Parse(text));

    [Fact]
    public async Task DetectsOpenAndClosedPorts()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var open = ((IPEndPoint)listener.LocalEndpoint).Port;

        var closedListener = new TcpListener(IPAddress.Loopback, 0);
        closedListener.Start();
        var closed = ((IPEndPoint)closedListener.LocalEndpoint).Port;
        closedListener.Stop(); // nobody listens here any more

        var results = await PortChecker.CheckManyAsync(IPAddress.Loopback, [Math.Min(open, closed), Math.Max(open, closed)], TimeSpan.FromSeconds(8), null, CancellationToken.None);
        Assert.Equal(PortState.Open, results.Single(r => r.Port == open).State);
        Assert.Equal(PortState.Closed, results.Single(r => r.Port == closed).State);
        Assert.NotNull(results.Single(r => r.Port == open).Milliseconds);
    }

    [Fact]
    public async Task CancellationIsNotReportedAsTimeout()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PortChecker.CheckAsync(IPAddress.Loopback, 9, TimeSpan.FromSeconds(1), cts.Token));
    }
}

public class SpeedTestScheduleTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0);

    [Fact]
    public void OffByDefault() => Assert.False(new SpeedTestSchedule().IsDue(Now));

    [Fact]
    public void DueWhenNeverRunAndThenAfterTheInterval()
    {
        var schedule = new SpeedTestSchedule { Enabled = true, IntervalHours = 6 };
        Assert.True(schedule.IsDue(Now));
        schedule.LastRun = Now;
        Assert.False(schedule.IsDue(Now.AddHours(5.9)));
        Assert.True(schedule.IsDue(Now.AddHours(6)));
    }

    [Fact]
    public void ClockMovedBackwardsRunsAgain() =>
        Assert.True(new SpeedTestSchedule { Enabled = true, LastRun = Now.AddDays(3) }.IsDue(Now));

    [Fact]
    public void IntervalIsClamped()
    {
        var schedule = new SpeedTestSchedule { IntervalHours = 0 };
        schedule.Normalize();
        Assert.Equal(1, schedule.IntervalHours);
    }

    [Fact]
    public void HistoryKeepsTheNewestOnly()
    {
        var history = new SpeedTestHistory();
        for (var i = 0; i < SpeedTestHistory.MaxResults + 10; i++)
        {
            history.Add(new SpeedTestResult(Now.AddMinutes(i), "SIN", null, i, 0));
        }

        Assert.Equal(SpeedTestHistory.MaxResults, history.Results.Count);
        Assert.Equal(SpeedTestHistory.MaxResults + 9, history.Results[0].DownloadBytesPerSecond);
    }
}
