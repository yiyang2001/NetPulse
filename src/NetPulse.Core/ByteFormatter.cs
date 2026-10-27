using System.Globalization;

namespace NetPulse.Core;

public enum SpeedUnit
{
    /// <summary>Bytes per second with binary prefixes (KB/s = 1024 B/s).</summary>
    Bytes,

    /// <summary>Bits per second with decimal prefixes (Kbps = 1000 bit/s), as ISPs advertise.</summary>
    Bits,
}

public static class ByteFormatter
{
    private static readonly string[] ByteSpeedUnits = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"];
    private static readonly string[] BitSpeedUnits = ["bps", "Kbps", "Mbps", "Gbps", "Tbps"];
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <param name="trimZeros">Drop trailing zeros ("5 MB/s", "2.5 MB/s"), e.g. for round axis labels.</param>
    public static string FormatSpeed(double bytesPerSecond, SpeedUnit unit, CultureInfo? culture = null, bool trimZeros = false)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond < 0)
        {
            bytesPerSecond = 0;
        }

        return unit == SpeedUnit.Bits
            ? Scale(bytesPerSecond * 8, 1000, BitSpeedUnits, culture, trimZeros)
            : Scale(bytesPerSecond, 1024, ByteSpeedUnits, culture, trimZeros);
    }

    public static string FormatSize(long bytes, CultureInfo? culture = null) =>
        Scale(Math.Max(0, bytes), 1024, SizeUnits, culture, trimZeros: false);

    /// <summary>
    /// Scales <paramref name="value"/> to the largest unit that keeps it below <paramref name="step"/>
    /// and prints three significant digits (e.g. "1.23", "12.3", "123"), integers for the base unit.
    /// </summary>
    private static string Scale(double value, double step, string[] units, CultureInfo? culture, bool trimZeros)
    {
        culture ??= CultureInfo.CurrentCulture;
        var index = 0;
        while (value >= step && index < units.Length - 1)
        {
            value /= step;
            index++;
        }

        // Rounding can push e.g. 1023.6 KB up to "1024 KB"; promote it to the next unit instead.
        if (index < units.Length - 1 && Math.Round(value, value < 10 ? 2 : value < 100 ? 1 : 0) >= step)
        {
            value /= step;
            index++;
        }

        var format = index == 0 ? "0"
            : value < 10 ? (trimZeros ? "0.##" : "0.00")
            : value < 100 ? (trimZeros ? "0.#" : "0.0")
            : "0";
        return value.ToString(format, culture) + " " + units[index];
    }
}
