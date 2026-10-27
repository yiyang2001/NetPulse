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
