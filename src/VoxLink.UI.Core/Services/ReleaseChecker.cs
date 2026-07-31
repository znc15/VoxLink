using System.Net;
using System.Text.Json;

namespace VoxLink.UI.Core.Services;

public enum ReleaseCheckState
{
    UpToDate,
    UpdateAvailable,
    Error
}

public sealed record ReleaseCheckResult(
    ReleaseCheckState State,
    Version? LatestVersion,
    string? ReleaseUrl,
    string Message);

public interface IReleaseChecker
{
    Task<ReleaseCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>从 GitHub Releases 检查最新版本的只读服务；任何失败都返回结果而不是抛出。</summary>
public sealed class GitHubReleaseChecker : IReleaseChecker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly Version _currentVersion;
    private readonly string _feedUrl;
    private readonly string _releasesPageUrl;

    public GitHubReleaseChecker(
        Version currentVersion,
        HttpClient? http = null,
        string? feedUrl = null,
        string? releasesPageUrl = null)
    {
        _currentVersion = currentVersion;
        _feedUrl = feedUrl ?? ReleaseMetadata.UpdateFeedUrl;
        _releasesPageUrl = releasesPageUrl ?? ReleaseMetadata.ReleasesPageUrl;
        _http = http ?? new HttpClient();
    }

    public async Task<ReleaseCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _feedUrl);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("User-Agent", $"VoxLink/{_currentVersion}");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ReleaseCheckResult(
                    ReleaseCheckState.UpToDate,
                    null,
                    _releasesPageUrl,
                    "尚未发布版本。");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ReleaseCheckResult(
                    ReleaseCheckState.Error,
                    null,
                    _releasesPageUrl,
                    $"更新源返回 {(int)response.StatusCode}。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tag)
                ? tag.GetString() ?? string.Empty
                : string.Empty;
            var releaseUrl = root.TryGetProperty("html_url", out var htmlUrl)
                ? htmlUrl.GetString() ?? _releasesPageUrl
                : _releasesPageUrl;

            var latestVersion = ParseVersion(tagName);
            if (latestVersion is null)
            {
                return new ReleaseCheckResult(
                    ReleaseCheckState.Error,
                    null,
                    releaseUrl,
                    "最新发布的版本号无法识别。");
            }

            var updateAvailable = latestVersion > Normalize(_currentVersion);
            return new ReleaseCheckResult(
                updateAvailable ? ReleaseCheckState.UpdateAvailable : ReleaseCheckState.UpToDate,
                latestVersion,
                releaseUrl,
                updateAvailable
                    ? $"发现新版本 {latestVersion}。"
                    : "已是最新版本。");
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or JsonException
                or OperationCanceledException
                or IOException)
        {
            return new ReleaseCheckResult(
                ReleaseCheckState.Error,
                null,
                _releasesPageUrl,
                "无法连接更新源，请稍后重试。");
        }
    }

    private static Version? ParseVersion(string tagName)
    {
        var candidate = tagName.Trim();
        if (candidate.Length > 0 && (candidate[0] is 'v' or 'V'))
        {
            candidate = candidate[1..];
        }

        if (!Version.TryParse(candidate, out var version))
        {
            return null;
        }

        return Normalize(version);
    }

    private static Version Normalize(Version version) =>
        new(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));


}
