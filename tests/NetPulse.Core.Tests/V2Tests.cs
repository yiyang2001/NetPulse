using System.Net;
using NetPulse.Core;

namespace NetPulse.Core.Tests;

public class ProcessTrafficTests
{
    [Fact]
    public void TableAccumulatesConcurrently()
    {
        var table = new ProcessTrafficTable();
        Parallel.For(0, 10_000, i => table.Add(i % 4, 10, 1));

        var snapshot = table.Snapshot();
        Assert.Equal(4, snapshot.Count);
        Assert.All(snapshot.Values, c => Assert.Equal(new TrafficCounters(25_000, 2_500), c));
    }

    [Fact]
    public void SamplerComputesPerProcessRatesAndSortsByActivity()
    {
        var sampler = new ProcessRateSampler();
        var t0 = new Dictionary<int, TrafficCounters> { [100] = new(1000, 0), [200] = new(0, 0) };
        Assert.All(sampler.Sample(t0, TimeSpan.FromSeconds(1)), r => Assert.Equal(0, r.TotalBytesPerSecond));

        var t1 = new Dictionary<int, TrafficCounters>
        {
            [100] = new(3000, 0),     // +2000 over 2 s
            [200] = new(0, 10_000),   // +10000 over 2 s
            [300] = new(400, 0),      // new process: all of it is recent
        };
        var rates = sampler.Sample(t1, TimeSpan.FromSeconds(3));

        Assert.Equal(new[] { 200, 100, 300 }, rates.Select(r => r.ProcessId));
        Assert.Equal(5000, rates[0].UploadBytesPerSecond);
        Assert.Equal(1000, rates[1].DownloadBytesPerSecond);
        Assert.Equal(200, rates[2].DownloadBytesPerSecond);
        Assert.Equal(3000, rates[1].TotalDownloaded);
    }

    [Fact]
    public void ClearedTableDoesNotProduceNegativeRates()
    {
        var sampler = new ProcessRateSampler();
        sampler.Sample(new Dictionary<int, TrafficCounters> { [1] = new(5000, 5000) }, TimeSpan.FromSeconds(1));
        var rates = sampler.Sample(new Dictionary<int, TrafficCounters> { [1] = new(10, 10) }, TimeSpan.FromSeconds(2));
        Assert.Equal(0, rates.Single().TotalBytesPerSecond);
    }
}

public class MeasurementTests
{
    [Fact]
    public void LatencyStatsUseMedianAndConsecutiveJitter()
    {
        var stats = LatencyStats.Compute([10, 30, 20, 40])!.Value;
        Assert.Equal(10, stats.MinMs);
        Assert.Equal(25, stats.MedianMs);
        Assert.Equal(40, stats.MaxMs);
        Assert.Equal((20 + 10 + 20) / 3.0, stats.JitterMs, 6);
        Assert.Null(LatencyStats.Compute([]));
        Assert.Equal(0, LatencyStats.Compute([5])!.Value.JitterMs);
    }

    [Fact]
    public void ThroughputExcludesWarmUp()
    {
        var meter = new ThroughputMeter(TimeSpan.FromSeconds(2));
        // Slow first two seconds (ramp-up), then a steady 1000 B/s.
        meter.Add(TimeSpan.Zero, 0);
        meter.Add(TimeSpan.FromSeconds(1), 50);
        meter.Add(TimeSpan.FromSeconds(2), 100);
        meter.Add(TimeSpan.FromSeconds(6), 4100);

        Assert.Equal(1000, meter.ResultBytesPerSecond(), 6);
        Assert.Equal(1000, meter.CurrentBytesPerSecond(TimeSpan.FromSeconds(1)), 6);
    }

    [Fact]
    public void ThroughputFallsBackToWholeRunWhenShorterThanWarmUp()
    {
        var meter = new ThroughputMeter(TimeSpan.FromSeconds(5));
        meter.Add(TimeSpan.Zero, 0);
        meter.Add(TimeSpan.FromSeconds(2), 2000);
        Assert.Equal(1000, meter.ResultBytesPerSecond(), 6);
        Assert.Equal(0, new ThroughputMeter(TimeSpan.Zero).ResultBytesPerSecond());
    }

    [Fact]
    public void PingSummaryCountsLoss()
    {
        var summary = PingSummary.From([10, null, 30, null]);
        Assert.Equal(4, summary.Sent);
        Assert.Equal(2, summary.Lost);
        Assert.Equal(0.5, summary.LossFraction);
        Assert.Equal(20, summary.AverageMs);
        Assert.Null(PingSummary.From([null]).AverageMs);
    }

    [Theory]
    [InlineData(6u, "2.4 GHz")]
    [InlineData(36u, "5/6 GHz")]
    [InlineData(0u, "未知")]
    public void BandFromChannel(uint channel, string expected) => Assert.Equal(expected, WifiBands.FromChannel(channel));

    [Fact]
    public void ColoIsTakenFromCfRay()
    {
        Assert.Equal("SIN", SpeedTestRunner.ColoFromRay("8c1d2e3f4a5b6c7d-SIN"));
        Assert.Equal(string.Empty, SpeedTestRunner.ColoFromRay(null));
        Assert.Equal(string.Empty, SpeedTestRunner.ColoFromRay("nodash"));
    }
}

public class WifiProfileTests
{
    private const string Personal = """
        <?xml version="1.0"?>
        <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
          <name>Home</name>
          <SSIDConfig><SSID><hex>486F6D652D3547</hex><name>Home-5G</name></SSID></SSIDConfig>
          <connectionType>ESS</connectionType>
          <connectionMode>auto</connectionMode>
          <MSM><security>
            <authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>
            <sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>p@ss;word</keyMaterial></sharedKey>
          </security></MSM>
        </WLANProfile>
        """;

    private const string Protected = """
        <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
          <name>Cafe</name>
          <SSIDConfig><SSID><hex>43616665</hex></SSID><nonBroadcast>true</nonBroadcast></SSIDConfig>
          <connectionMode>manual</connectionMode>
          <MSM><security>
            <authEncryption><authentication>WPA3SAE</authentication><encryption>AES</encryption></authEncryption>
            <sharedKey><keyType>passPhrase</keyType><protected>true</protected><keyMaterial>01000000D08C9DDF0115D1118C7A00C0</keyMaterial></sharedKey>
          </security></MSM>
        </WLANProfile>
        """;

    private const string Open = """
        <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
          <name>Airport Free WiFi</name>
          <SSIDConfig><SSID><name>Airport Free WiFi</name></SSID></SSIDConfig>
          <MSM><security><authEncryption><authentication>open</authentication><encryption>none</encryption></authEncryption></security></MSM>
        </WLANProfile>
        """;

    [Fact]
    public void ParsesPersonalProfileWithPlainKey()
    {
        var profile = WifiProfileParser.Parse(Personal);
        Assert.Equal("Home", profile.Name);
        Assert.Equal("Home-5G", profile.Ssid);
        Assert.Equal("WPA2-个人", profile.SecurityLabel);
        Assert.Equal("p@ss;word", profile.Key);
        Assert.False(profile.KeyProtected);
        Assert.True(profile.AutoConnect);
        Assert.False(profile.IsOpen);
        Assert.False(profile.IsEnterprise);
    }

    [Fact]
    public void ProtectedKeyIsNeverExposed()
    {
        var profile = WifiProfileParser.Parse(Protected);
        Assert.Equal("Cafe", profile.Ssid); // decoded from hex
        Assert.True(profile.Hidden);
        Assert.False(profile.AutoConnect);
        Assert.True(profile.KeyProtected);
        Assert.Null(profile.Key);
        Assert.Equal("WPA3-个人", profile.SecurityLabel);
    }

    [Fact]
    public void QrPayloadEscapesSpecialCharacters()
    {
        var profile = WifiProfileParser.Parse(Personal);
        Assert.Equal(@"WIFI:T:WPA;S:Home-5G;P:p@ss\;word;;", WifiQrPayload.Build(profile, profile.Key));

        var hidden = WifiProfileParser.Parse(Protected);
        Assert.Equal(@"WIFI:T:SAE;S:Cafe;P:a\:b\\c;H:true;;", WifiQrPayload.Build(hidden, @"a:b\c"));

        var open = WifiProfileParser.Parse(Open);
        Assert.True(open.IsOpen);
        Assert.Equal("WIFI:T:nopass;S:Airport Free WiFi;;", WifiQrPayload.Build(open, null));
    }
}

public class SpeedTestRunnerTests
{
    /// <summary>Serves the speed-test endpoints locally so the runner's control flow is tested offline.</summary>
    private sealed class FakeCloudflare : HttpMessageHandler
    {
        public int Requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            Assert.Equal("speed.cloudflare.com", request.RequestUri!.Host);
            Assert.False(request.Headers.Contains("Cookie"));
            if (request.Content is not null)
            {
                await request.Content.CopyToAsync(Stream.Null, cancellationToken); // "upload" the body
            }

            await Task.Delay(5, cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri.AbsolutePath == "/__down" ? new ByteArrayContent(new byte[64 * 1024]) : new ByteArrayContent([]),
            };
            response.Headers.Add("cf-ray", "abc123-HKG");
            return response;
        }
    }

    [Fact]
    public async Task CompletesAllPhasesAndReportsServer()
    {
        var handler = new FakeCloudflare();
        using var runner = new SpeedTestRunner(handler, TimeSpan.FromSeconds(1));
        var phases = new HashSet<SpeedTestPhase>();
        var progress = new SynchronousProgress(p => phases.Add(p.Phase));

        var result = await runner.RunAsync(progress, CancellationToken.None);

        Assert.Equal("HKG", result.Server);
        Assert.NotNull(result.Latency);
        Assert.True(result.DownloadBytesPerSecond > 0);
        Assert.True(result.UploadBytesPerSecond > 0);
        Assert.Superset(new HashSet<SpeedTestPhase> { SpeedTestPhase.Latency, SpeedTestPhase.Download, SpeedTestPhase.Upload, SpeedTestPhase.Done }, phases);
    }

    [Fact]
    public async Task CancellationStopsPromptly()
    {
        using var runner = new SpeedTestRunner(new FakeCloudflare());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(null, cts.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    private sealed class SynchronousProgress(Action<SpeedTestProgress> report) : IProgress<SpeedTestProgress>
    {
        private readonly Lock _gate = new();

        public void Report(SpeedTestProgress value)
        {
            lock (_gate)
            {
                report(value);
            }
        }
    }
}
