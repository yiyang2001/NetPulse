using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NetPulse.Core;

public enum PortState
{
    /// <summary>A TCP connection was accepted.</summary>
    Open,

    /// <summary>The host answered that nothing listens there (connection refused). Windows retries a refused connect, so this can take about two seconds to show.</summary>
    Closed,

    /// <summary>No answer in time: usually a firewall dropping the packets, or a host that is off.</summary>
    Timeout,

    /// <summary>The network said it cannot be reached, or another error.</summary>
    Error,
}

public sealed record PortResult(int Port, PortState State, double? Milliseconds, string? Detail = null);

public static class PortSpec
{
    public const int MaxPorts = 1000;

    /// <summary>Parses "80, 443, 8000-8010" into sorted distinct ports; throws <see cref="FormatException"/> on bad input.</summary>
    public static IReadOnlyList<int> Parse(string text)
    {
        var ports = new SortedSet<int>();
        foreach (var part in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-', 2);
            if (!TryPort(range[0], out var first) || (range.Length == 2 && !TryPort(range[1], out _)))
            {
                throw new FormatException($"Not a port: {part}");
            }

            var last = range.Length == 2 ? int.Parse(range[1]) : first;
            if (last < first)
            {
                throw new FormatException($"Empty range: {part}");
            }

            for (var port = first; port <= last; port++)
            {
                ports.Add(port);
                if (ports.Count > MaxPorts)
                {
                    throw new FormatException($"At most {MaxPorts} ports at a time.");
                }
            }
        }

        return ports.Count > 0 ? [.. ports] : throw new FormatException("No port given.");
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
}

public static class PortChecker
{
    public static async Task<PortResult> CheckAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), timeoutSource.Token).ConfigureAwait(false);
            return new PortResult(port, PortState.Open, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PortResult(port, PortState.Timeout, null);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return new PortResult(port, PortState.Closed, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (SocketException ex)
        {
            return new PortResult(port, PortState.Error, null, ex.SocketErrorCode.ToString());
        }
    }

    /// <summary>Checks the ports a few at a time and reports each result as it arrives; results come back in port order.</summary>
    public static async Task<IReadOnlyList<PortResult>> CheckManyAsync(
        IPAddress address,
        IReadOnlyList<int> ports,
        TimeSpan timeout,
        Action<PortResult>? onResult,
        CancellationToken cancellationToken,
        int concurrency = 32)
    {
        var results = new List<PortResult>();
        await Parallel.ForEachAsync(
            ports,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken },
            async (port, token) =>
            {
                var result = await CheckAsync(address, port, timeout, token).ConfigureAwait(false);
                lock (results)
                {
                    results.Add(result);
                }

                onResult?.Invoke(result);
            }).ConfigureAwait(false);
        return [.. results.OrderBy(r => r.Port)];
    }
}
