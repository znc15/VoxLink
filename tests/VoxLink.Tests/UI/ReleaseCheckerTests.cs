using System.Net;
using System.Text;
using System.Text.Json;
using VoxLink.UI.Core.Services;
using Xunit;

namespace VoxLink.Tests.UI;

public sealed class ReleaseCheckerTests
{
    private static readonly Version Current = new(1, 0, 0);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static GitHubReleaseChecker CreateChecker(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        Version? current = null) =>
        new(
            current ?? Current,
            new HttpClient(new StubHandler(respond)),
            feedUrl: "https://api.github.com/repos/example/VoxLink/releases/latest",
            releasesPageUrl: "https://github.com/example/VoxLink/releases");

    private static HttpResponseMessage JsonRelease(string tagName, string? htmlUrl = null) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    tag_name = tagName,
                    html_url = htmlUrl ?? $"https://github.com/example/VoxLink/releases/tag/{tagName}",
                    body = "notes"
                }),
                Encoding.UTF8,
                "application/json")
        };

    [Fact]
    public async Task NewerRelease_ReportsUpdateAvailable()
    {
        var checker = CreateChecker(_ => JsonRelease("v1.0.1"));

        var result = await checker.CheckAsync();

        Assert.Equal(ReleaseCheckState.UpdateAvailable, result.State);
        Assert.Equal(new Version(1, 0, 1, 0), result.LatestVersion);
        Assert.Contains("1.0.1", result.Message);
        Assert.Equal("https://github.com/example/VoxLink/releases/tag/v1.0.1", result.ReleaseUrl);
    }

    [Fact]
    public async Task SameVersion_ReportsUpToDate()
    {
        var checker = CreateChecker(_ => JsonRelease("1.0.0"));

        var result = await checker.CheckAsync();

        Assert.Equal(ReleaseCheckState.UpToDate, result.State);
        Assert.Contains("已是最新", result.Message);
    }

    [Fact]
    public async Task OlderVersion_ReportsUpToDate()
    {
        var checker = CreateChecker(_ => JsonRelease("v0.9.9"));

        var result = await checker.CheckAsync();

        Assert.Equal(ReleaseCheckState.UpToDate, result.State);
    }

    [Fact]
    public async Task MissingRelease_ReportsUpToDateWithNoReleaseMessage()
    {
        var checker = CreateChecker(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await checker.CheckAsync();

        Assert.Equal(ReleaseCheckState.UpToDate, result.State);
        Assert.Contains("尚未发布", result.Message);
    }

    [Fact]
    public async Task ServerError_ReportsError()
    {
        var checker = CreateChecker(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await checker.CheckAsync();

        Assert.Equal(ReleaseCheckState.Error, result.State);
        Assert.Contains("500", result.Message);
    }

    [Fact]
    public async Task UnparsableTag_ReportsError()
    {
        var checker = CreateChecker(_ => JsonRelease("nightly"));

        var result = await checker.CheckAsync();

        Assert.Equal(ReleaseCheckState.Error, result.State);
        Assert.Contains("无法识别", result.Message);
    }

    [Fact]
    public async Task MalformedJson_ReturnsErrorWithoutThrowing()
    {
        var checker = CreateChecker(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json", Encoding.UTF8, "application/json")
        });

        var result = await checker.CheckAsync();

        Assert.Equal(ReleaseCheckState.Error, result.State);
    }

    [Fact]
    public async Task NetworkFailure_ReturnsErrorWithoutThrowing()
    {
        var checker = CreateChecker(_ => throw new HttpRequestException("offline"));

        var result = await checker.CheckAsync();

        Assert.Equal(ReleaseCheckState.Error, result.State);
        Assert.Contains("无法连接", result.Message);
    }
}
