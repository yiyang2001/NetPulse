using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NetPulse.Core;

/// <summary>Opt-in check for a newer release. Off by default; nothing is downloaded, the user is only told.</summary>
public sealed class UpdateCheckSettings
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    public bool Enabled { get; set; }

    public DateTime? LastCheck { get; set; }

    /// <summary>Version the user was last notified about, so each release is announced once.</summary>
    public string? NotifiedVersion { get; set; }

    /// <summary>A clock set back (LastCheck in the future) does not block checks for ever.</summary>
    public bool IsDue(DateTime now) => Enabled && (LastCheck is not { } last || now - last >= Interval || last > now);

    public void Normalize()
    {
        if (NotifiedVersion is { Length: > 64 })
        {
            NotifiedVersion = null;
        }
    }
}

/// <summary>A published release: its version, tag and the page to download it from.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, Uri Page, string? Name);

public static class AppVersion
{
    /// <summary>"v0.7.0", "0.7", "1.0.0-beta.1+abc" → 0.7.0, 0.7.0, 1.0.0 (always three parts); null if not a version.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var core = text.Trim();
        if (core.StartsWith('v') || core.StartsWith('V'))
        {
            core = core[1..];
        }

        var end = core.IndexOfAny(['-', '+', ' ']);
        if (end >= 0)
        {
            core = core[..end];
        }

        return Version.TryParse(core.Contains('.') ? core : core + ".0", out var version) ? Normalize(version) : null;
    }

    /// <summary>Major.Minor.Build with missing parts as 0 and the revision dropped, so 0.7 equals 0.7.0.0.</summary>
    public static Version Normalize(Version version) => new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));
}

/// <summary>Reads the newest release from the GitHub Releases API ("/repos/{owner}/{repo}/releases/latest").</summary>
public static class UpdateFeed
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>"owner/repo", letters, digits, '-', '_' and '.' only; anything else is not a repository.</summary>
    public static bool IsValidRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        var parts = repository.Split('/');
        return parts.Length == 2 && parts.All(p => p.Length is > 0 and <= 100 && p is not "." and not ".." &&
            p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
    }

    public static Uri LatestReleaseUri(string repository) =>
        IsValidRepository(repository)
            ? new Uri($"https://api.github.com/repos/{repository}/releases/latest")
            : throw new ArgumentException("Not an owner/repo name.", nameof(repository));

    /// <summary>
    /// The release in a GitHub "latest release" response, or null for drafts, pre-releases, unknown version
    /// tags and pages outside github.com (the link is opened in the browser, so it must not point anywhere else).
    /// </summary>
    public static ReleaseInfo? ParseLatest(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || IsTrue(root, "draft") || IsTrue(root, "prerelease"))
            {
                return null;
            }

            var tag = Text(root, "tag_name");
            var page = Text(root, "html_url");
            if (AppVersion.Parse(tag) is not { } version || !Uri.TryCreate(page, UriKind.Absolute, out var uri) || !IsGitHubPage(uri))
            {
                return null;
            }

            return new ReleaseInfo(version, tag!, uri, Text(root, "name"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsNewer(ReleaseInfo release, Version current) => release.Version > AppVersion.Normalize(current);

    /// <summary>
    /// Asks for the latest release. Returns null when the repository has no (usable) release yet; throws
    /// <see cref="HttpRequestException"/> or <see cref="TaskCanceledException"/> when the request fails.
    /// </summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(HttpClient http, Uri endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null; // no release published yet
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return json.Length > 1_000_000 ? null : ParseLatest(json);
    }

    private static bool IsGitHubPage(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort;

    private static bool IsTrue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
