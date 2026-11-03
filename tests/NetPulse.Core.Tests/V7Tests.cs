using System.Net;
using System.Text;
using NetPulse.Core;
using Xunit;

namespace NetPulse.Core.Tests;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("v0.7.0", "0.7.0")]
    [InlineData("0.7", "0.7.0")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("1.0.0-beta.1", "1.0.0")]
    [InlineData("0.7.0+abc123", "0.7.0")]
    [InlineData("2", "2.0.0")]
    public void ParsesTags(string text, string expected) => Assert.Equal(Version.Parse(expected), AppVersion.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    public void RejectsNonVersions(string? text) => Assert.Null(AppVersion.Parse(text));

    [Fact]
    public void NormalizeDropsRevisionAndFillsMissingParts()
    {
        Assert.Equal(new Version(0, 7, 0), AppVersion.Normalize(new Version(0, 7)));
        Assert.Equal(new Version(0, 7, 0), AppVersion.Normalize(new Version(0, 7, 0, 0)));
    }
}

public sealed class UpdateFeedTests
{
    private const string Release = """
        {"tag_name":"v0.8.0","name":"NetPulse 0.8","html_url":"https://github.com/owner/netpulse/releases/tag/v0.8.0","draft":false,"prerelease":false}
        """;

    [Fact]
    public void ParsesLatestRelease()
    {
        var release = UpdateFeed.ParseLatest(Release);
        Assert.NotNull(release);
        Assert.Equal(new Version(0, 8, 0), release.Version);
        Assert.Equal("v0.8.0", release.Tag);
        Assert.Equal("NetPulse 0.8", release.Name);
        Assert.Equal("https://github.com/owner/netpulse/releases/tag/v0.8.0", release.Page.AbsoluteUri);
    }

    [Theory]
    [InlineData("""{"tag_name":"v0.8.0","html_url":"https://github.com/o/r/releases/tag/v0.8.0","draft":true}""")]
    [InlineData("""{"tag_name":"v0.8.0","html_url":"https://github.com/o/r/releases/tag/v0.8.0","prerelease":true}""")]
    [InlineData("""{"tag_name":"nightly","html_url":"https://github.com/o/r/releases/tag/nightly"}""")]
    [InlineData("""{"tag_name":"v0.8.0","html_url":"https://evil.example/download"}""")]
    [InlineData("""{"tag_name":"v0.8.0","html_url":"http://github.com/o/r/releases/tag/v0.8.0"}""")]
    [InlineData("""{"tag_name":"v0.8.0","html_url":"https://github.com.evil.example/o/r"}""")]
    [InlineData("""{"tag_name":"v0.8.0"}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""not json""")]
    public void IgnoresUnusableReleases(string json) => Assert.Null(UpdateFeed.ParseLatest(json));

    [Fact]
    public void ComparesWithTheRunningVersion()
    {
        var release = UpdateFeed.ParseLatest(Release)!;
        Assert.True(UpdateFeed.IsNewer(release, new Version(0, 7, 0, 0)));
        Assert.False(UpdateFeed.IsNewer(release, new Version(0, 8)));
        Assert.False(UpdateFeed.IsNewer(release, new Version(0, 9, 0)));
    }

    [Theory]
    [InlineData("owner/netpulse", true)]
    [InlineData("my-org/Net.Pulse_2", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("owner", false)]
    [InlineData("owner/repo/extra", false)]
    [InlineData("../repo", false)]
    [InlineData("owner/re po", false)]
    [InlineData("owner/repo?x=1", false)]
    public void ValidatesRepositoryNames(string? repository, bool valid) => Assert.Equal(valid, UpdateFeed.IsValidRepository(repository));

    [Fact]
    public void BuildsTheApiAddress() =>
        Assert.Equal("https://api.github.com/repos/owner/netpulse/releases/latest", UpdateFeed.LatestReleaseUri("owner/netpulse").AbsoluteUri);

    [Fact]
    public async Task ReadsTheReleaseOverHttp()
    {
        var handler = new FakeGitHub(HttpStatusCode.OK, Release);
        using var http = new HttpClient(handler);
        var release = await UpdateFeed.GetLatestAsync(http, UpdateFeed.LatestReleaseUri("owner/netpulse"), CancellationToken.None);
        Assert.Equal(new Version(0, 8, 0), release?.Version);
        Assert.Equal("application/vnd.github+json", Assert.Single(handler.Request!.Headers.Accept).MediaType);
        Assert.Null(handler.Request.Headers.Authorization);
    }

    [Fact]
    public async Task NoReleaseYetIsNotAnError()
    {
        using var http = new HttpClient(new FakeGitHub(HttpStatusCode.NotFound, """{"message":"Not Found"}"""));
        Assert.Null(await UpdateFeed.GetLatestAsync(http, UpdateFeed.LatestReleaseUri("owner/netpulse"), CancellationToken.None));
    }

    [Fact]
    public async Task ServerErrorsThrow()
    {
        using var http = new HttpClient(new FakeGitHub(HttpStatusCode.Forbidden, """{"message":"rate limited"}"""));
        await Assert.ThrowsAsync<HttpRequestException>(() => UpdateFeed.GetLatestAsync(http, UpdateFeed.LatestReleaseUri("owner/netpulse"), CancellationToken.None));
    }

    private sealed class FakeGitHub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}

public sealed class UpdateCheckSettingsTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0);

    [Fact]
    public void OffByDefault()
    {
        var settings = new AppSettings();
        Assert.False(settings.UpdateCheck.Enabled);
        Assert.False(settings.UpdateCheck.IsDue(Now));
    }

    [Fact]
    public void DueOncePerDay()
    {
        var check = new UpdateCheckSettings { Enabled = true };
        Assert.True(check.IsDue(Now));
        check.LastCheck = Now.AddHours(-23);
        Assert.False(check.IsDue(Now));
        check.LastCheck = Now.AddHours(-24);
        Assert.True(check.IsDue(Now));
    }

    [Fact]
    public void ClockSetBackDoesNotBlockChecks()
    {
        var check = new UpdateCheckSettings { Enabled = true, LastCheck = Now.AddDays(30) };
        Assert.True(check.IsDue(Now));
    }

    [Fact]
    public void OldSettingsFilesGetTheDefault()
    {
        var settings = new AppSettings { UpdateCheck = null! }.Normalize();
        Assert.NotNull(settings.UpdateCheck);
        Assert.False(settings.UpdateCheck.Enabled);
    }
}

public sealed class InstallLayoutTests
{
    [Fact]
    public void UninstallEntryRunsTheInstalledExe()
    {
        var values = InstallLayout.UninstallValues(
            "NetPulse", @"C:\Users\me\AppData\Local\Programs\NetPulse\NetPulse.exe", "0.7.0", 5_000_000, new DateOnly(2026, 11, 3));
        Assert.Equal(@"""C:\Users\me\AppData\Local\Programs\NetPulse\NetPulse.exe"" --uninstall", values["UninstallString"]);
        Assert.Equal(@"""C:\Users\me\AppData\Local\Programs\NetPulse\NetPulse.exe"" --uninstall --quiet", values["QuietUninstallString"]);
        Assert.Equal(@"C:\Users\me\AppData\Local\Programs\NetPulse", values["InstallLocation"]);
        Assert.Equal("20261103", values["InstallDate"]);
        Assert.Equal("0.7.0", values["DisplayVersion"]);
        Assert.Equal(4883, values["EstimatedSize"]); // KB, rounded up
        Assert.Equal(1, values["NoModify"]);
    }

    [Fact]
    public void ProfileArgumentIsKept()
    {
        var values = InstallLayout.UninstallValues("NetPulse (dev)", @"C:\p\NetPulse-dev\NetPulse.exe", "0.7.0", 1, new DateOnly(2026, 1, 1), " --profile=dev");
        Assert.Equal(@"""C:\p\NetPulse-dev\NetPulse.exe"" --uninstall --profile=dev", values["UninstallString"]);
    }

    [Theory]
    [InlineData(true, "0.6.0", "0.7.0", InstallOffer.None)]
    [InlineData(false, null, "0.7.0", InstallOffer.Install)]
    [InlineData(false, "0.6.0", "0.7.0", InstallOffer.Update)]
    [InlineData(false, "0.7.0", "0.7.0", InstallOffer.Reinstall)]
    [InlineData(false, "0.8.0", "0.7.0", InstallOffer.None)]
    [InlineData(false, "garbage", "0.7.0", InstallOffer.Update)]
    public void OffersWhatMakesSense(bool runningInstalled, string? installed, string current, InstallOffer expected) =>
        Assert.Equal(expected, InstallLayout.Offer(runningInstalled, installed, current));
}
