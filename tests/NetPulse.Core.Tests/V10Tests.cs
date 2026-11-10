using System.Text.Json;
using NetPulse.Core;
using Xunit;

namespace NetPulse.Core.Tests;

public sealed class QualityMonitorTests
{
    private static readonly NetworkIdentity Home = NetworkIdentity.ForWifi("Home");
    private static readonly NetworkIdentity Office = NetworkIdentity.ForWifi("Office");
    private static readonly DateTime Base = new(2026, 11, 10, 12, 0, 0);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Base);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly QualityLedger _ledger = new();
    private readonly QualityMonitor _monitor;

    public QualityMonitorTests() => _monitor = new QualityMonitor(_ledger);

    private static QualityProbe Up(double ms = 3, NetworkIdentity? network = null) => new(network ?? Home, ms);

    private static QualityProbe GatewayDown(NetworkIdentity? network = null) => new(network ?? Home, null);

    private static QualityProbe Internet(double? ms, double gateway = 3) => new(Home, gateway, ExternalProbed: true, ExternalMs: ms);

    private QualityUpdate Feed(QualityProbe probe, double seconds) =>
        _monitor.Feed(probe, TimeSpan.FromSeconds(seconds), Base.AddSeconds(seconds), Interval, highLatencyMs: 200);

    [Fact]
    public void TwoLostRepliesAreLossNotAnOutage()
    {
        Feed(Up(), 0);
        Assert.Equal(QualityState.Loss, Feed(GatewayDown(), 5).State);
        Assert.Equal(QualityState.Loss, Feed(GatewayDown(), 10).State);
        var back = Feed(Up(), 15);

        Assert.Equal(QualityState.Ok, back.State);
        Assert.Null(back.OutageEnded);
        var summary = _ledger.Summarize(Today, Today);
        Assert.Equal(0, summary.Outages);
        Assert.Equal(0, summary.DownSeconds);
        Assert.Equal(1, summary.Availability);
        Assert.Equal(2, summary.GatewayLost);
        Assert.Equal(0.5, summary.GatewayLossRate);
        Assert.Empty(_ledger.Outages(DateTime.MinValue, DateTime.MaxValue));
    }

    [Fact]
    public void ThreeFailuresInARowMakeAnOutageFromTheFirstToTheNextReply()
    {
        Feed(Up(), 0);
        Assert.False(Feed(GatewayDown(), 5).OutageStarted);
        Assert.False(Feed(GatewayDown(), 10).OutageStarted);
        var third = Feed(GatewayDown(), 15);
        Assert.True(third.OutageStarted);
        Assert.Equal(QualityState.LocalOutage, third.State);
        Assert.Equal(TimeSpan.FromSeconds(10), _monitor.Ongoing!.Duration);
        Assert.Null(Feed(GatewayDown(), 20).OutageEnded);

        var ended = Feed(Up(), 25).OutageEnded;
        Assert.NotNull(ended);
        Assert.Equal(Base.AddSeconds(5), ended.Start);
        Assert.Equal(TimeSpan.FromSeconds(20), ended.Duration);
        Assert.Equal(OutageKind.Local, ended.Kind);
        Assert.Equal(Home.Key, ended.NetworkKey);
        Assert.Null(_monitor.Ongoing);

        var summary = _ledger.Summarize(Today, Today);
        Assert.Equal(1, summary.Outages);
        Assert.Equal(25, summary.MonitoredSeconds);
        Assert.Equal(ended.Duration.TotalSeconds, summary.DownSeconds); // down time adds up to the outages
        Assert.Equal(0.2, summary.Availability!.Value, 6);
        Assert.Same(ended, Assert.Single(_ledger.Outages(DateTime.MinValue, DateTime.MaxValue)));
    }

    [Fact]
    public void GatewayUpButInternetDownIsAnExternalOutage()
    {
        Feed(Internet(20), 0);
        Feed(Internet(null), 5);
        Feed(Internet(null), 10);
        Assert.Equal(QualityState.ExternalOutage, Feed(Internet(null), 15).State);

        var ended = Feed(Internet(20), 20).OutageEnded;
        Assert.Equal(OutageKind.External, ended!.Kind);
        var summary = _ledger.Summarize(Today, Today);
        Assert.Equal(0, summary.GatewayLost);
        Assert.Equal(3, summary.ExternalLost);
    }

    [Fact]
    public void AGatewayFailureAnywhereMakesTheOutageLocal()
    {
        Feed(Internet(20), 0);
        Feed(Internet(null), 5);
        Feed(Internet(null), 10);
        Feed(Internet(null), 15);
        Feed(new QualityProbe(Home, null, ExternalProbed: true, ExternalMs: null), 20);
        Assert.Equal(OutageKind.Local, Feed(Internet(20), 25).OutageEnded!.Kind);
    }

    [Fact]
    public void ASleepGapIsUnmonitoredAndEndsAnOutageBeforeIt()
    {
        Feed(Up(), 0);
        Feed(GatewayDown(), 5);
        Feed(GatewayDown(), 10);
        Feed(GatewayDown(), 15);

        var afterSleep = Feed(Up(), 15 + 3600);
        Assert.Equal(TimeSpan.FromSeconds(10), afterSleep.OutageEnded!.Duration); // not an hour
        Assert.Equal(QualityState.Ok, afterSleep.State);

        var summary = _ledger.Summarize(Today, Today);
        Assert.Equal(15, summary.MonitoredSeconds); // the hour asleep counts nowhere
        Assert.Equal(10, summary.DownSeconds);
    }

    [Fact]
    public void FailuresDoNotAddUpAcrossAGap()
    {
        Feed(Up(), 0);
        Feed(GatewayDown(), 5);
        Feed(GatewayDown(), 10);
        var afterSleep = Feed(GatewayDown(), 10 + 600); // third failure, but after a gap: a new run
        Assert.False(afterSleep.OutageStarted);
        Assert.Equal(QualityState.Loss, afterSleep.State);
    }

    [Fact]
    public void ClockGoingBackwardsIsTreatedAsAGap()
    {
        Feed(Up(), 100);
        var update = Feed(Up(), 50);
        Assert.Equal(QualityState.Ok, update.State);
        Assert.Equal(0, _ledger.Summarize(Today, Today).MonitoredSeconds);
    }

    [Fact]
    public void DisconnectingEndsTheOutageAndIsCountedSeparately()
    {
        Feed(Up(), 0);
        Feed(GatewayDown(), 5);
        Feed(GatewayDown(), 10);
        Feed(GatewayDown(), 15);
        var disconnected = Feed(QualityProbe.NotConnected, 20);
        Assert.Equal(QualityState.Disconnected, disconnected.State);
        Assert.Equal(TimeSpan.FromSeconds(10), disconnected.OutageEnded!.Duration);
        Feed(QualityProbe.NotConnected, 25);

        var home = _ledger.Summarize(Today, Today, Home.Key);
        Assert.Equal(15, home.MonitoredSeconds);
        Assert.Equal(0, home.DisconnectedSeconds);
        Assert.Equal(10, _ledger.Summarize(Today, Today).DisconnectedSeconds);
        Assert.DoesNotContain(_ledger.Networks, n => n.Key.Length == 0);
    }

    [Fact]
    public void SwitchingNetworkEndsTheOutageOnTheOldOne()
    {
        Feed(Up(), 0);
        Feed(GatewayDown(), 5);
        Feed(GatewayDown(), 10);
        Feed(GatewayDown(), 15);
        var switched = Feed(Up(network: Office), 20);
        Assert.Equal(Home.Key, switched.OutageEnded!.NetworkKey);
        Assert.Equal(TimeSpan.FromSeconds(10), switched.OutageEnded.Duration);
        Assert.Equal(5, _ledger.Summarize(Today, Today, Office.Key).MonitoredSeconds);
        Assert.Equal(["Home", "Office"], _ledger.Networks.Select(n => n.Label));
    }

    [Fact]
    public void StopEndsAnOpenOutageAtTheLastRound()
    {
        Feed(Up(), 0);
        Feed(GatewayDown(), 5);
        Feed(GatewayDown(), 10);
        Feed(GatewayDown(), 15);
        Assert.Equal(TimeSpan.FromSeconds(10), _monitor.Stop()!.Duration);
        Assert.Null(_monitor.Stop());
        Assert.Null(_monitor.State);
        Assert.Equal(QualityState.Ok, Feed(Up(), 1000).State); // starts afresh, nothing carried over
        Assert.Equal(10, _ledger.Summarize(Today, Today).DownSeconds);
    }

    [Fact]
    public void SlowRepliesAreHighLatency()
    {
        Assert.Equal(QualityState.HighLatency, Feed(Up(250), 0).State);
        Assert.Equal(QualityState.HighLatency, Feed(Internet(400), 5).State);
        Assert.Equal(QualityState.Ok, Feed(Internet(50), 10).State);
    }

    [Fact]
    public void SamplesAreDatedByWallClockAndOutagesByTheirStart()
    {
        var beforeMidnight = new DateTime(2026, 11, 10, 23, 59, 50);
        var monitor = new QualityMonitor(_ledger);
        QualityUpdate At(QualityProbe probe, double s) =>
            monitor.Feed(probe, TimeSpan.FromSeconds(s), beforeMidnight.AddSeconds(s), Interval, 200);

        At(Up(), 0);
        At(GatewayDown(), 5);   // 23:59:55, outage starts here
        At(GatewayDown(), 10);  // 00:00:00
        At(GatewayDown(), 15);
        At(Up(), 20);

        var day1 = new DateOnly(2026, 11, 10);
        var day2 = day1.AddDays(1);
        Assert.Equal(1, _ledger.Summarize(day1, day1).Outages);
        Assert.Equal(0, _ledger.Summarize(day2, day2).Outages);
        Assert.Equal(5, _ledger.Summarize(day1, day1).MonitoredSeconds);
        Assert.Equal(15, _ledger.Summarize(day2, day2).MonitoredSeconds);
        Assert.Equal(15, _ledger.Summarize(day1, day2).DownSeconds);
        Assert.Equal(2, _ledger.Daily(day1, day2).Count);
    }

    [Fact]
    public void TimelineMinutesShowTheWorstStateIncludingConfirmedOutages()
    {
        var start = new DateTime(2026, 11, 10, 12, 0, 50);
        var monitor = new QualityMonitor(_ledger);
        void At(QualityProbe probe, double s) => monitor.Feed(probe, TimeSpan.FromSeconds(s), start.AddSeconds(s), Interval, 200);

        At(Up(10), 0);          // 12:00:50
        At(GatewayDown(), 5);   // 12:00:55: loss now, an outage once confirmed
        At(GatewayDown(), 10);  // 12:01:00
        At(GatewayDown(), 15);
        At(Up(30), 20);

        var minutes = _ledger.Minutes(DateTime.MinValue);
        Assert.Equal(2, minutes.Count);
        Assert.Equal(QualityState.LocalOutage, minutes[0].State);
        Assert.Equal(10, minutes[0].GatewayMs);
        Assert.Equal(new DateTime(2026, 11, 10, 12, 1, 0), minutes[1].Minute);
        Assert.Equal(QualityState.LocalOutage, minutes[1].State);
        Assert.Equal(30, minutes[1].GatewayMs);
    }
}

public sealed class QualityLedgerTests
{
    [Fact]
    public void HistogramPercentilesUseBucketUpperBounds()
    {
        var counts = LatencyHistogram.Create();
        Assert.Null(LatencyHistogram.Percentile(counts, 0.5));
        foreach (var ms in new[] { 4.0, 4, 4, 6, 6, 9, 12, 25, 80, 5000 })
        {
            LatencyHistogram.Add(counts, ms);
        }

        Assert.Equal(5, LatencyHistogram.Percentile(counts, 0.3));
        Assert.Equal(7, LatencyHistogram.Percentile(counts, 0.5));
        Assert.Equal(1000, LatencyHistogram.Percentile(counts, 0.95)); // slower than the last bound
    }

    [Fact]
    public void RoundTripsThroughJsonAndRepairsBadHistograms()
    {
        var ledger = new QualityLedger();
        var monitor = new QualityMonitor(ledger);
        var home = NetworkIdentity.ForWifi("Home");
        var t0 = new DateTime(2026, 11, 10, 8, 0, 0);
        for (var i = 0; i < 6; i++)
        {
            monitor.Feed(new QualityProbe(home, i is >= 1 and <= 3 ? null : 8), TimeSpan.FromSeconds(i * 5), t0.AddSeconds(i * 5), TimeSpan.FromSeconds(5), 200);
        }

        var json = JsonSerializer.Serialize(ledger.ToDocument(), AppJson.Options);
        var copy = new QualityLedger(JsonSerializer.Deserialize<QualityDocument>(json, AppJson.Options));
        var day = DateOnly.FromDateTime(t0);
        Assert.Equal(ledger.Summarize(day, day), copy.Summarize(day, day));
        Assert.Equal(1, copy.Summarize(day, day).Outages);
        Assert.Equal(ledger.Outages(DateTime.MinValue, DateTime.MaxValue), copy.Outages(DateTime.MinValue, DateTime.MaxValue));
        Assert.Equal("Home", Assert.Single(copy.Networks).Label);
        Assert.Equal(QualityState.LocalOutage, Assert.Single(copy.Minutes(DateTime.MinValue)).State);

        var broken = new QualityDocument { Days = [new QualityDay { Date = day, NetworkKey = home.Key, GatewaySamples = 1, GatewayLatency = [1, 2] }, null!] };
        var repaired = new QualityLedger(broken).Summarize(day, day);
        Assert.Null(repaired.GatewayMedianMs);
        Assert.Equal(1, repaired.GatewaySamples);
        Assert.Equal(0, new QualityLedger(null).Summarize(day, day).MonitoredSeconds);
    }

    [Fact]
    public void SummariesForEqualLedgersAreEqual()
    {
        // QualitySummary is a record: equality is used by the round-trip test above.
        var a = new QualityLedger().Summarize(DateOnly.MinValue, DateOnly.MaxValue);
        var b = new QualityLedger().Summarize(DateOnly.MinValue, DateOnly.MaxValue);
        Assert.Equal(a, b);
        Assert.Null(a.Availability);
    }

    [Fact]
    public void PruneDropsOldDaysOutagesMinutesAndUnusedNetworks()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0);
        var document = new QualityDocument
        {
            Networks = new() { ["wifi:Old"] = "Old", ["wifi:Home"] = "Home" },
            Days =
            [
                new QualityDay { Date = new DateOnly(2025, 1, 1), NetworkKey = "wifi:Old", MonitoredSeconds = 1 },
                new QualityDay { Date = new DateOnly(2026, 11, 10), NetworkKey = "wifi:Home", MonitoredSeconds = 1 },
            ],
            Outages =
            [
                new Outage(now.AddDays(-91), now.AddDays(-91).AddMinutes(1), OutageKind.Local, "wifi:Home", "Home"),
                new Outage(now.AddDays(-1), now.AddDays(-1).AddMinutes(1), OutageKind.External, "wifi:Home", "Home"),
            ],
            Minutes =
            [
                new QualityMinute { Minute = now.AddHours(-25) },
                new QualityMinute { Minute = now.AddMinutes(-5) },
            ],
        };
        var ledger = new QualityLedger(document);
        ledger.Prune(new DateOnly(2025, 10, 1), now);

        Assert.Equal(0, ledger.Summarize(DateOnly.MinValue, new DateOnly(2025, 12, 31)).MonitoredSeconds);
        Assert.Equal(OutageKind.External, Assert.Single(ledger.Outages(DateTime.MinValue, DateTime.MaxValue)).Kind);
        Assert.Single(ledger.Minutes(DateTime.MinValue));
        Assert.Equal("Home", Assert.Single(ledger.Networks).Label);
        Assert.True(ledger.IsDirty);
    }

    [Fact]
    public void KeepsOnlyTheNewestOutages()
    {
        var ledger = new QualityLedger();
        var monitor = new QualityMonitor(ledger);
        var home = NetworkIdentity.ForWifi("Home");
        var t = 0.0;
        void Round(bool up)
        {
            monitor.Feed(new QualityProbe(home, up ? 5 : null), TimeSpan.FromSeconds(t), DateTime.Today.AddSeconds(t), TimeSpan.FromSeconds(5), 200);
            t += 5;
        }

        for (var i = 0; i < QualityLedger.MaxOutages + 5; i++)
        {
            Round(true);
            Round(false);
            Round(false);
            Round(false);
        }

        Round(true);
        var outages = ledger.Outages(DateTime.MinValue, DateTime.MaxValue);
        Assert.Equal(QualityLedger.MaxOutages, outages.Count);
        Assert.Equal(DateTime.Today.AddSeconds(t - 20), outages[0].Start); // newest first
    }
}

public sealed class QualitySettingsTests
{
    [Fact]
    public void DefaultsAreOffAndNormalizeClamps()
    {
        var settings = new AppSettings().Normalize().Quality;
        Assert.False(settings.Enabled);
        Assert.False(settings.ProbeExternal);
        Assert.Equal("1.1.1.1", settings.ExternalTarget);

        var odd = new ConnectionQualitySettings { IntervalSeconds = 0, HighLatencyMs = 1, OutageAlertSeconds = -5, ExternalTarget = "   " };
        odd.Normalize();
        Assert.Equal(2, odd.IntervalSeconds);
        Assert.Equal(20, odd.HighLatencyMs);
        Assert.Equal(15, odd.OutageAlertSeconds);
        Assert.Equal(ConnectionQualitySettings.DefaultExternalTarget, odd.ExternalTarget);

        var missing = JsonSerializer.Deserialize<AppSettings>("{\"Quality\": null}", AppJson.Options)!.Normalize();
        Assert.NotNull(missing.Quality);
    }

    [Fact]
    public void AlertsOnlyForLongEnoughOutages()
    {
        var settings = new ConnectionQualitySettings { OutageAlertSeconds = 60 };
        var start = new DateTime(2026, 11, 10, 12, 0, 0);
        Outage Lasting(int seconds) => new(start, start.AddSeconds(seconds), OutageKind.Local, "k", "l");

        Assert.False(settings.ShouldAlert(Lasting(59)));
        Assert.True(settings.ShouldAlert(Lasting(60)));
        settings.OutageAlert = false;
        Assert.False(settings.ShouldAlert(Lasting(600)));
    }
}

public sealed class QualityBackupTests
{
    [Fact]
    public void FormatOneBackupsStillRestoreWithoutQualityData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netpulse-v1-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"Format\": 1, \"Settings\": {\"Layout\": \"OneLine\"}}");
        try
        {
            var backup = BackupDocument.Read(path);
            Assert.Equal(WidgetLayout.OneLine, backup.Settings.Layout);
            Assert.Empty(backup.Quality.Days);
            Assert.False(backup.Settings.Quality.Enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FormatTwoBackupsCarryQualityData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netpulse-v2-{Guid.NewGuid():N}.json");
        var start = new DateTime(2026, 11, 10, 12, 0, 0);
        try
        {
            BackupDocument.Write(path, new BackupDocument
            {
                Settings = new AppSettings { Quality = { Enabled = true, ProbeExternal = true } },
                Quality = new QualityDocument
                {
                    Outages = [new Outage(start, start.AddMinutes(2), OutageKind.External, "wifi:Home", "Home")],
                },
            });

            var backup = BackupDocument.Read(path);
            Assert.Equal(2, backup.Format);
            Assert.True(backup.Settings.Quality.ProbeExternal);
            Assert.Equal(TimeSpan.FromMinutes(2), Assert.Single(backup.Quality.Outages).Duration);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class GatewayChoiceTests
{
    private static GatewayCandidate Candidate(string name, int index, bool isVirtual) =>
        new(new AdapterSnapshot("{" + name + "}", name, name, System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, isVirtual, new TrafficCounters(0, 0)),
            index, System.Net.IPAddress.Parse($"192.168.{index}.1"));

    [Fact]
    public void PrefersTheAdapterWindowsRoutesThrough()
    {
        var wifi = Candidate("Wi-Fi", 7, isVirtual: false);
        var vpn = Candidate("VPN", 12, isVirtual: true);
        Assert.Same(vpn, GatewayChoice.Pick([wifi, vpn], 12));
    }

    [Fact]
    public void FallsBackToAPhysicalAdapterThenAnyAndNoneWithoutCandidates()
    {
        var vpn = Candidate("VPN", 12, isVirtual: true);
        var wifi = Candidate("Wi-Fi", 7, isVirtual: false);
        Assert.Same(wifi, GatewayChoice.Pick([vpn, wifi], 30)); // route goes through a tunnel without a gateway
        Assert.Same(wifi, GatewayChoice.Pick([vpn, wifi], null));
        Assert.Same(vpn, GatewayChoice.Pick([vpn], null));
        Assert.Null(GatewayChoice.Pick([], 7));
    }
}

public sealed class QualityDnsAndSignalTests
{
    private static readonly NetworkIdentity Home = NetworkIdentity.ForWifi("Home");
    private static readonly DateTime Base = new(2026, 11, 10, 12, 0, 0);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Base);

    private readonly QualityLedger _ledger = new();
    private readonly QualityMonitor _monitor;

    public QualityDnsAndSignalTests() => _monitor = new QualityMonitor(_ledger);

    private static QualityProbe Round(bool dnsOk, double? external = 20, int? signal = null) =>
        new(Home, 3, ExternalProbed: true, ExternalMs: external, SignalPercent: signal, DnsProbed: true, DnsOk: dnsOk);

    private QualityUpdate Feed(QualityProbe probe, double seconds) =>
        _monitor.Feed(probe, TimeSpan.FromSeconds(seconds), Base.AddSeconds(seconds), TimeSpan.FromSeconds(5), 200);

    [Fact]
    public void PingsAnsweredButNoDnsIsADnsOutage()
    {
        Feed(Round(true), 0);
        Feed(Round(false), 5);
        Feed(Round(false), 10);
        Assert.Equal(QualityState.DnsOutage, Feed(Round(false), 15).State);
        Assert.Equal(OutageKind.Dns, Feed(Round(true), 20).OutageEnded!.Kind);

        var summary = _ledger.Summarize(Today, Today);
        Assert.Equal(5, summary.DnsSamples);
        Assert.Equal(3, summary.DnsLost);
        Assert.Equal(0.6, summary.DnsFailureRate);
        Assert.Equal(0, summary.ExternalLost);
    }

    [Fact]
    public void TheDeepestFailingLayerNamesTheOutage()
    {
        Feed(Round(true), 0);
        Feed(Round(false), 5);                  // DNS only
        Feed(Round(false, external: null), 10); // internet down too
        Feed(Round(false), 15);
        Assert.Equal(OutageKind.External, Feed(Round(true), 20).OutageEnded!.Kind);

        // DNS failing while the internet address is already down stays an internet problem.
        Feed(Round(false, external: null), 25);
        Feed(Round(false, external: null), 30);
        Assert.Equal(QualityState.ExternalOutage, Feed(Round(false), 35).State);
    }

    [Fact]
    public void DnsIsNotCountedWhenNotProbed()
    {
        Feed(new QualityProbe(Home, 3), 0);
        Assert.Null(_ledger.Summarize(Today, Today).DnsFailureRate);
    }

    [Fact]
    public void TimelineMinutesAverageTheWifiSignal()
    {
        Feed(Round(true, signal: 80), 0);
        Feed(Round(true, signal: 60), 5);
        Feed(Round(true), 10); // no reading (e.g. location permission withdrawn)
        Assert.Equal(70, Assert.Single(_ledger.Minutes(DateTime.MinValue)).SignalPercent);

        var json = JsonSerializer.Serialize(_ledger.ToDocument(), AppJson.Options);
        var copy = new QualityLedger(JsonSerializer.Deserialize<QualityDocument>(json, AppJson.Options));
        Assert.Equal(70, Assert.Single(copy.Minutes(DateTime.MinValue)).SignalPercent);
    }

    [Fact]
    public void BuildsAStandardQuery()
    {
        var query = DnsProbe.BuildQuery(0xBEEF, "example.com");
        byte[] expected =
        [
            0xBE, 0xEF, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
            3, (byte)'c', (byte)'o', (byte)'m', 0,
            0x00, 0x01, 0x00, 0x01,
        ];
        Assert.Equal(expected, query);
        Assert.Throws<ArgumentException>(() => DnsProbe.BuildQuery(1, "bad..name"));
    }

    [Theory]
    [InlineData(new byte[] { 0x12, 0x34, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0 }, true)]   // answer
    [InlineData(new byte[] { 0x12, 0x34, 0x81, 0x83, 0, 1, 0, 0, 0, 0, 0, 0 }, true)]   // NXDOMAIN: the server works
    [InlineData(new byte[] { 0x12, 0x34, 0x81, 0x82, 0, 1, 0, 0, 0, 0, 0, 0 }, false)]  // SERVFAIL
    [InlineData(new byte[] { 0x12, 0x34, 0x81, 0x85, 0, 1, 0, 0, 0, 0, 0, 0 }, false)]  // REFUSED
    [InlineData(new byte[] { 0x12, 0x35, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0 }, false)]  // other query
    [InlineData(new byte[] { 0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 }, false)]  // a query, not a reply
    [InlineData(new byte[] { 0x12, 0x34, 0x81 }, false)]                                 // truncated
    public void RecognisesAnswers(byte[] reply, bool expected) =>
        Assert.Equal(expected, DnsProbe.IsAnswer(reply, 0x1234));
}
