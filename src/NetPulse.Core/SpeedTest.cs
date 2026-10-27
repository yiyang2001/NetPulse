using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace NetPulse.Core;

public enum SpeedTestPhase
{
    Latency,
    Download,
    Upload,
    Done,
}

public readonly record struct SpeedTestProgress(SpeedTestPhase Phase, double Fraction, double CurrentBytesPerSecond, LatencyStats? Latency);

public sealed record SpeedTestResult(
    DateTime StartedAt,
    string Server,
    LatencyStats? Latency,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond);

/// <summary>On-disk history of past results.</summary>
public sealed class SpeedTestHistory
{
    public List<SpeedTestResult> Results { get; set; } = [];
}

/// <summary>
/// Measures latency, download and upload against Cloudflare's public speed-test endpoints
/// (the same ones speed.cloudflare.com uses). Nothing is sent besides the test traffic itself:
/// no cookies, no identifiers, only a product User-Agent.
/// </summary>
/// <param name="handler">Transport override (tests); defaults to a cookie-less <see cref="SocketsHttpHandler"/>.</param>
/// <param name="phaseDuration">Length of the download and upload phases; 10 s by default.</param>
public sealed class SpeedTestRunner(HttpMessageHandler? handler = null, TimeSpan? phaseDuration = null) : IDisposable
{
    public const string ServerHost = "speed.cloudflare.com";

    private const int LatencyProbes = 12;
    private const int Streams = 4;
    private const long DownloadChunkBytes = 25_000_000;
    private const int UploadChunkBytes = 4_000_000;

    private static readonly Uri Base = new($"https://{ServerHost}/");

    private readonly HttpClient _http = CreateClient(handler);
    private readonly TimeSpan _phaseDuration = phaseDuration ?? TimeSpan.FromSeconds(10);

    /// <summary>The first fifth of each phase (2 s by default) is TCP ramp-up and is not counted.</summary>
    private TimeSpan WarmUp => _phaseDuration / 5;

    public async Task<SpeedTestResult> RunAsync(IProgress<SpeedTestProgress>? progress, CancellationToken cancellationToken)
    {
        var startedAt = DateTime.Now;
        var (latency, server) = await MeasureLatencyAsync(progress, cancellationToken).ConfigureAwait(false);
        var download = await MeasureAsync(SpeedTestPhase.Download, DownloadWorkerAsync, progress, latency, cancellationToken).ConfigureAwait(false);
        var upload = await MeasureAsync(SpeedTestPhase.Upload, UploadWorkerAsync, progress, latency, cancellationToken).ConfigureAwait(false);
        progress?.Report(new SpeedTestProgress(SpeedTestPhase.Done, 1, 0, latency));
        return new SpeedTestResult(startedAt, server, latency, download, upload);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The data center code is the suffix of Cloudflare's "cf-ray" header, e.g. "8c1d…-SIN".</summary>
    public static string ColoFromRay(string? ray)
    {
        var dash = ray?.LastIndexOf('-') ?? -1;
        return dash >= 0 && dash < ray!.Length - 1 ? ray[(dash + 1)..] : string.Empty;
    }

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        handler ??= new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = Streams + 1,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        };
        var client = new HttpClient(handler) { BaseAddress = Base, Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NetPulse", "0.2"));
        return client;
    }

    private async Task<(LatencyStats? Stats, string Server)> MeasureLatencyAsync(IProgress<SpeedTestProgress>? progress, CancellationToken cancellationToken)
    {
        var samples = new List<double>();
        var server = string.Empty;
        // The first request pays for DNS + TCP + TLS; it warms the connection and is not counted.
        for (var i = 0; i <= LatencyProbes; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            using var response = await _http.GetAsync("__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            response.EnsureSuccessStatusCode();
            if (response.Headers.TryGetValues("cf-ray", out var rays))
            {
                server = ColoFromRay(rays.FirstOrDefault());
            }

            if (i > 0)
            {
                samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            }

            progress?.Report(new SpeedTestProgress(SpeedTestPhase.Latency, (double)i / LatencyProbes, 0, LatencyStats.Compute(samples)));
        }

        return (LatencyStats.Compute(samples), server);
    }

    private async Task<double> MeasureAsync(
        SpeedTestPhase phase,
        Func<Action<long>, CancellationToken, Task> worker,
        IProgress<SpeedTestProgress>? progress,
        LatencyStats? latency,
        CancellationToken cancellationToken)
    {
        long transferred = 0;
        var meter = new ThroughputMeter(WarmUp);
        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var clock = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, Streams)
            .Select(_ => RunUntilCancelled(() => worker(bytes => Interlocked.Add(ref transferred, bytes), phaseCts.Token)))
            .ToArray();

        try
        {
            while (clock.Elapsed < _phaseDuration)
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                meter.Add(clock.Elapsed, Interlocked.Read(ref transferred));
                progress?.Report(new SpeedTestProgress(
                    phase, Math.Min(1, clock.Elapsed / _phaseDuration), meter.CurrentBytesPerSecond(TimeSpan.FromSeconds(1)), latency));

                if (workers.All(w => w.IsCompleted))
                {
                    break; // every stream failed; report what we have
                }
            }

            meter.Add(clock.Elapsed, Interlocked.Read(ref transferred));
        }
        finally
        {
            // Stop the streams and wait for them, also when the user cancelled, so no request outlives the test.
            await phaseCts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(workers.Select(w => w.ContinueWith(static _ => { }, TaskScheduler.Default))).ConfigureAwait(false);
        }

        var failure = workers.Select(w => w.Exception?.GetBaseException()).FirstOrDefault(e => e is not null);
        if (Interlocked.Read(ref transferred) == 0 && failure is not null)
        {
            throw new HttpRequestException($"{phase} failed: {failure.Message}", failure);
        }

        return meter.ResultBytesPerSecond();
    }

    /// <summary>Loops one stream; cancellation ends it normally, real errors are kept for reporting.</summary>
    private static async Task RunUntilCancelled(Func<Task> loop)
    {
        try
        {
            await loop().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DownloadWorkerAsync(Action<long> onBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            using var response = await _http.GetAsync($"__down?bytes={DownloadChunkBytes}", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                onBytes(read);
            }
        }
    }

    private async Task UploadWorkerAsync(Action<long> onBytes, CancellationToken cancellationToken)
    {
        var payload = new byte[UploadChunkBytes];
        Random.Shared.NextBytes(payload); // incompressible
        while (!cancellationToken.IsCancellationRequested)
        {
            using var content = new CountingContent(payload, onBytes);
            using var response = await _http.PostAsync("__up", content, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>Request body that reports bytes as they are written to the socket, not when the request completes.</summary>
    private sealed class CountingContent(byte[] payload, Action<long> onBytes) : HttpContent
    {
        private const int Slice = 64 * 1024;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            for (var offset = 0; offset < payload.Length; offset += Slice)
            {
                var count = Math.Min(Slice, payload.Length - offset);
                await stream.WriteAsync(payload.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
                onBytes(count);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = payload.Length;
            return true;
        }
    }
}
