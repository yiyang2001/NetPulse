using System.Reflection;
using NetPulse.Core;

namespace NetPulse;

/// <summary>
/// Asks GitHub for the newest release of the repository this build was published from. Only runs when the
/// user turned the check on (or clicks "Check now"); sends no cookies, only "NetPulse/x.y.z" as User-Agent.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private readonly HttpClient _http;

    public UpdateService()
    {
        _http = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"NetPulse/{CurrentVersion}");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    /// <summary>"owner/repo" baked in at build time (NetPulseUpdateRepository), or null: then there is no update check.</summary>
    public static string? Repository { get; } = ReadRepository();

    public static Version CurrentVersion { get; } =
        AppVersion.Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public static bool IsConfigured => Repository is not null;

    public bool IsBusy { get; private set; }

    /// <summary>Latest release, or null when the repository has none yet. Throws when the request fails.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        if (Repository is null)
        {
            throw new InvalidOperationException("No update repository configured.");
        }

        IsBusy = true;
        try
        {
            return await UpdateFeed.GetLatestAsync(_http, UpdateFeed.LatestReleaseUri(Repository), cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Dispose() => _http.Dispose();

    private static string? ReadRepository()
    {
        var value = typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value?.Trim();
        return UpdateFeed.IsValidRepository(value) ? value : null;
    }
}
