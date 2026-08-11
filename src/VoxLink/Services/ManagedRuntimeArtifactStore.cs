using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using VoxLink.Models;

namespace VoxLink.Services;

internal interface IManagedRuntimeArtifactStore
{
    Task<string> AcquireAsync(
        ManagedRuntimeArtifact artifact,
        IProgress<ManagedRuntimeProgressEventArgs>? progress,
        string runtimeProfileId,
        CancellationToken cancellationToken);
}

internal sealed class ManagedRuntimeArtifactStore : IManagedRuntimeArtifactStore, IDisposable
{
    private const int MaxRedirects = 5;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(2);
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "www.python.org",
        "python.org",
        "files.pythonhosted.org",
        "releases.ubuntu.com",
        "github.com",
        "codeload.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    };

    private readonly ManagedRuntimeLayout _layout;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _acquireGate = new(1, 1);
    private int _disposed;

    public ManagedRuntimeArtifactStore(ManagedRuntimeLayout layout)
        : this(layout, CreateHttpClient(), ownsHttpClient: true)
    {
    }

    internal ManagedRuntimeArtifactStore(
        ManagedRuntimeLayout layout,
        HttpClient httpClient,
        bool ownsHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(httpClient);
        _layout = layout;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<string> AcquireAsync(
        ManagedRuntimeArtifact artifact,
        IProgress<ManagedRuntimeProgressEventArgs>? progress,
        string runtimeProfileId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _acquireGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AcquireCoreAsync(
                artifact,
                progress,
                runtimeProfileId,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _acquireGate.Release();
        }
    }

    private async Task<string> AcquireCoreAsync(
        ManagedRuntimeArtifact artifact,
        IProgress<ManagedRuntimeProgressEventArgs>? progress,
        string runtimeProfileId,
        CancellationToken cancellationToken)
    {
        ValidateArtifact(artifact);
        ManagedRuntimeLayout.ValidateIdentifier(runtimeProfileId);
        Directory.CreateDirectory(_layout.DownloadsDirectory);
        var targetPath = Path.Combine(_layout.DownloadsDirectory, artifact.FileName);
        if (await IsVerifiedAsync(targetPath, artifact, cancellationToken).ConfigureAwait(false))
        {
            return targetPath;
        }

        var partialPath = targetPath + ".download";
        var metadataPath = partialPath + ".resume.json";
        var resume = await ReadResumeAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        var offset = GetResumeOffset(partialPath, resume, artifact);
        if (offset == 0)
        {
            ResetPartial(partialPath, metadataPath);
        }

        progress?.Report(new ManagedRuntimeProgressEventArgs(
            runtimeProfileId,
            offset > 0 ? "正在恢复运行时工件下载…" : "正在下载运行时工件…",
            artifact.ExpectedSize == 0 ? null : offset / (double)artifact.ExpectedSize));

        try
        {
            await DownloadAsync(
                artifact,
                partialPath,
                metadataPath,
                resume,
                offset,
                progress,
                runtimeProfileId,
                cancellationToken).ConfigureAwait(false);
            if (!await IsVerifiedAsync(partialPath, artifact, cancellationToken).ConfigureAwait(false))
            {
                ResetPartial(partialPath, metadataPath);
                throw new InvalidDataException("托管运行时工件大小或 SHA-256 校验失败。");
            }

            File.Move(partialPath, targetPath, overwrite: true);
            TryDeleteFile(metadataPath);
            return targetPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            if (!File.Exists(metadataPath))
            {
                ResetPartial(partialPath, metadataPath);
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task DownloadAsync(
        ManagedRuntimeArtifact artifact,
        string partialPath,
        string metadataPath,
        ArtifactResumeState? resume,
        long offset,
        IProgress<ManagedRuntimeProgressEventArgs>? progress,
        string runtimeProfileId,
        CancellationToken cancellationToken)
    {
        var responseAndRequest = await SendAsync(
            artifact.Url,
            offset,
            offset > 0 ? resume?.ETag : null,
            cancellationToken).ConfigureAwait(false);
        using var request = responseAndRequest.Request;
        using var response = responseAndRequest.Response;

        var append = offset > 0
            && response.StatusCode == HttpStatusCode.PartialContent
            && IsMatchingPartialResponse(response, offset, resume?.ETag);
        if (offset > 0 && !append)
        {
            ResetPartial(partialPath, metadataPath);
            offset = 0;
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new HttpRequestException(
                    "服务器未接受安全的断点续传请求。",
                    null,
                    response.StatusCode);
            }
        }

        if (offset == 0 && response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException(
                $"运行时工件下载失败：HTTP {(int)response.StatusCode}。",
                null,
                response.StatusCode);
        }

        ValidateResponseLength(response, artifact.ExpectedSize, offset, append);
        var strongEtag = GetStrongEtag(response.Headers.ETag);
        if (strongEtag is not null)
        {
            await WriteResumeAsync(
                metadataPath,
                new ArtifactResumeState(
                    artifact.Url,
                    artifact.ExpectedSize,
                    artifact.Sha256,
                    strongEtag),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            TryDeleteFile(metadataPath);
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var destination = new FileStream(
            partialPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        var downloaded = offset;
        while (true)
        {
            var read = await ReadWithTimeoutAsync(source, buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            downloaded += read;
            if (downloaded > artifact.ExpectedSize)
            {
                ResetPartial(partialPath, metadataPath);
                throw new InvalidDataException("托管运行时工件超过固定长度。");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new ManagedRuntimeProgressEventArgs(
                runtimeProfileId,
                "正在下载运行时工件…",
                downloaded / (double)artifact.ExpectedSize));
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (downloaded != artifact.ExpectedSize)
        {
            throw new IOException("托管运行时工件下载未完成，可稍后继续。 ");
        }
    }

    private async Task<(HttpResponseMessage Response, HttpRequestMessage Request)> SendAsync(
        string url,
        long offset,
        string? etag,
        CancellationToken cancellationToken)
    {
        var current = ValidateUrl(url);
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                if (!string.IsNullOrWhiteSpace(etag))
                {
                    request.Headers.IfRange = new RangeConditionHeaderValue(etag);
                }
            }

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                request.Dispose();
                throw;
            }

            if (!IsRedirect(response.StatusCode))
            {
                return (response, request);
            }

            var location = response.Headers.Location;
            response.Dispose();
            request.Dispose();
            if (location is null)
            {
                throw new HttpRequestException("运行时工件重定向缺少 Location。 ");
            }

            current = ValidateUrl(location.IsAbsoluteUri ? location : new Uri(current, location));
        }

        throw new HttpRequestException("运行时工件重定向次数过多。");
    }

    private static Uri ValidateUrl(string url) => ValidateUrl(new Uri(url, UriKind.Absolute));

    private static Uri ValidateUrl(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host))
        {
            throw new InvalidOperationException("运行时工件下载地址不在允许的 HTTPS 来源中。");
        }

        return uri;
    }

    private static void ValidateArtifact(ManagedRuntimeArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!string.Equals(artifact.FileName, Path.GetFileName(artifact.FileName), StringComparison.Ordinal)
            || artifact.ExpectedSize <= 0
            || artifact.Sha256.Length != 64
            || !artifact.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("托管运行时工件清单无效。");
        }

        _ = ValidateUrl(artifact.Url);
    }

    private static long GetResumeOffset(
        string partialPath,
        ArtifactResumeState? resume,
        ManagedRuntimeArtifact artifact)
    {
        if (resume is null
            || !string.Equals(resume.Url, artifact.Url, StringComparison.Ordinal)
            || resume.ExpectedSize != artifact.ExpectedSize
            || !string.Equals(resume.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(resume.ETag)
            || !File.Exists(partialPath))
        {
            return 0;
        }

        var length = new FileInfo(partialPath).Length;
        return length > 0 && length < artifact.ExpectedSize ? length : 0;
    }

    private static bool IsMatchingPartialResponse(
        HttpResponseMessage response,
        long expectedOffset,
        string? expectedEtag) =>
        response.Content.Headers.ContentRange?.From == expectedOffset
        && response.Content.Headers.ContentRange?.To is not null
        && string.Equals(
            GetStrongEtag(response.Headers.ETag),
            expectedEtag,
            StringComparison.Ordinal);

    private static void ValidateResponseLength(
        HttpResponseMessage response,
        long expectedSize,
        long offset,
        bool append)
    {
        var expectedContentLength = append ? expectedSize - offset : expectedSize;
        if (response.Content.Headers.ContentLength is long contentLength
            && contentLength != expectedContentLength)
        {
            throw new InvalidDataException("运行时工件响应长度与固定清单不一致。");
        }

        if (append && response.Content.Headers.ContentRange?.Length is long totalLength
            && totalLength != expectedSize)
        {
            throw new InvalidDataException("运行时工件总长度与固定清单不一致。");
        }
    }

    private static string? GetStrongEtag(EntityTagHeaderValue? etag) =>
        etag is { IsWeak: false } ? etag.ToString() : null;

    private static async Task<int> ReadWithTimeoutAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        try
        {
            return await stream.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("运行时工件下载长时间没有进度。");
        }
    }

    private static async Task<bool> IsVerifiedAsync(
        string path,
        ManagedRuntimeArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != artifact.ExpectedSize)
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(
            Convert.ToHexStringLower(hash),
            artifact.Sha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ArtifactResumeState?> ReadResumeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ArtifactResumeState>(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task WriteResumeAsync(
        string path,
        ArtifactResumeState state,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static void ResetPartial(string partialPath, string metadataPath)
    {
        TryDeleteFile(partialPath);
        TryDeleteFile(metadataPath);
        TryDeleteFile(metadataPath + ".tmp");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false
        };
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VoxLink.RuntimeManager/1.0");
        return client;
    }

    private sealed record ArtifactResumeState(
        string Url,
        long ExpectedSize,
        string Sha256,
        string ETag);
}
