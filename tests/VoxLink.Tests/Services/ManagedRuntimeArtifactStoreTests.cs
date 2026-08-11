using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using VoxLink.Models;
using VoxLink.Services;
using Xunit;

namespace VoxLink.Tests.Services;

public sealed class ManagedRuntimeArtifactStoreTests
{
    private const string ProfileId = "test-profile";

    [Theory]
    [InlineData("https://evil.example.com/runtime.zip")]
    [InlineData("http://www.python.org/runtime.zip")]
    public async Task DisallowedInitialUrl_RejectedBeforeAnyHttp(string url)
    {
        using var temp = new TempDirectory();
        var layout = new ManagedRuntimeLayout(temp.Root, Path.Combine(temp.Root, "assets"));
        var artifact = new ManagedRuntimeArtifact("runtime.zip", 10, new string('0', 64), url);
        using var client = new HttpClient(new RecordingHandler(ThrowIfCalled));
        using var store = new ManagedRuntimeArtifactStore(layout, client);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AcquireAsync(artifact, null, ProfileId, CancellationToken.None));

        Assert.Contains("不在允许的 HTTPS 来源中", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task RedirectToDisallowedHost_RejectedBeforeFollowing()
    {
        using var temp = new TempDirectory();
        var layout = new ManagedRuntimeLayout(temp.Root, Path.Combine(temp.Root, "assets"));
        var artifact = CreateArtifact(Payload(10));
        using var client = new HttpClient(new RecordingHandler((request, _) =>
        {
            requestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.example.com/redirected.zip");
            return Task.FromResult(response);
        }));
        using var store = new ManagedRuntimeArtifactStore(layout, client);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AcquireAsync(artifact, null, ProfileId, CancellationToken.None));

        Assert.Contains("不在允许的 HTTPS 来源中", error.Message, StringComparison.Ordinal);
        // The initial URL is allowed; only the redirect target is rejected, and it is never requested.
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task WrongContentLength_FailsAndDoesNotPromoteFinal()
    {
        using var temp = new TempDirectory();
        var layout = new ManagedRuntimeLayout(temp.Root, Path.Combine(temp.Root, "assets"));
        var payload = Payload(10);
        var artifact = CreateArtifact(payload);
        using var client = new HttpClient(new RecordingHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            response.Content.Headers.ContentLength = payload.Length + 1;
            return Task.FromResult(response);
        }));
        using var store = new ManagedRuntimeArtifactStore(layout, client);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.AcquireAsync(artifact, null, ProfileId, CancellationToken.None));

        Assert.Contains("响应长度与固定清单不一致", error.Message, StringComparison.Ordinal);
        AssertNoArtifactFiles(layout, artifact);
    }

    [Fact]
    public async Task TruncatedBody_FailsAndDoesNotPromoteFinal()
    {
        using var temp = new TempDirectory();
        var layout = new ManagedRuntimeLayout(temp.Root, Path.Combine(temp.Root, "assets"));
        var payload = Payload(10);
        var artifact = CreateArtifact(payload);
        using var client = new HttpClient(new RecordingHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload.AsSpan(0, 7).ToArray())
            };
            response.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(response);
        }));
        using var store = new ManagedRuntimeArtifactStore(layout, client);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            store.AcquireAsync(artifact, null, ProfileId, CancellationToken.None));

        Assert.Contains("下载未完成", error.Message, StringComparison.Ordinal);
        AssertNoArtifactFiles(layout, artifact);
    }

    [Fact]
    public async Task WrongSha_FailsAndDoesNotPromoteFinal()
    {
        using var temp = new TempDirectory();
        var layout = new ManagedRuntimeLayout(temp.Root, Path.Combine(temp.Root, "assets"));
        var artifact = CreateArtifact(Payload(10));
        var wrongPayload = Payload(10, seed: 100);
        using var client = new HttpClient(new RecordingHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(wrongPayload)
            };
            response.Content.Headers.ContentLength = wrongPayload.Length;
            return Task.FromResult(response);
        }));
        using var store = new ManagedRuntimeArtifactStore(layout, client);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.AcquireAsync(artifact, null, ProfileId, CancellationToken.None));

        Assert.Contains("SHA-256 校验失败", error.Message, StringComparison.Ordinal);
        AssertNoArtifactFiles(layout, artifact);
    }

    [Fact]
    public async Task VerifiedExistingFinal_ReturnsWithoutAnyHttp()
    {
        using var temp = new TempDirectory();
        var layout = new ManagedRuntimeLayout(temp.Root, Path.Combine(temp.Root, "assets"));
        var payload = Payload(10);
        var artifact = CreateArtifact(payload);
        var finalPath = Path.Combine(layout.DownloadsDirectory, artifact.FileName);
        Directory.CreateDirectory(layout.DownloadsDirectory);
        File.WriteAllBytes(finalPath, payload);
        using var client = new HttpClient(new RecordingHandler(ThrowIfCalled));
        using var store = new ManagedRuntimeArtifactStore(layout, client);

        var result = await store.AcquireAsync(artifact, null, ProfileId, CancellationToken.None);

        Assert.Equal(finalPath, result);
        Assert.Equal(0, requestCount);
        Assert.Equal(payload.Length, new FileInfo(finalPath).Length);
        Assert.Equal(artifact.Sha256, Convert.ToHexStringLower(SHA256.HashData(payload)));
    }

    [Fact]
    public async Task StrongEtagResume_SendsRangeAndIfRangeAndPromotesValid206()
    {
        using var temp = new TempDirectory();
        var layout = new ManagedRuntimeLayout(temp.Root, Path.Combine(temp.Root, "assets"));
        var payload = Payload(10);
        var artifact = CreateArtifact(payload);
        Directory.CreateDirectory(layout.DownloadsDirectory);
        var finalPath = Path.Combine(layout.DownloadsDirectory, artifact.FileName);
        var partialPath = finalPath + ".download";
        var metadataPath = partialPath + ".resume.json";

        // Seed a previously interrupted download: 4 of 10 bytes plus strong-ETag resume metadata.
        File.WriteAllBytes(partialPath, payload.AsSpan(0, 4).ToArray());
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(new
        {
            Url = artifact.Url,
            ExpectedSize = artifact.ExpectedSize,
            Sha256 = artifact.Sha256,
            ETag = "\"strong-cached\""
        }));

        RequestSnapshot? captured = null;
        using var client = new HttpClient(new RecordingHandler((request, _) =>
        {
            requestCount++;
            captured = new RequestSnapshot(
                request.RequestUri,
                request.Headers.Range?.ToString(),
                request.Headers.IfRange?.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(payload.AsSpan(4).ToArray())
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"strong-cached\"");
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(4, 9, 10);
            return Task.FromResult(response);
        }));
        using var store = new ManagedRuntimeArtifactStore(layout, client);

        var result = await store.AcquireAsync(artifact, null, ProfileId, CancellationToken.None);

        Assert.Equal(finalPath, result);
        Assert.Equal(1, requestCount);
        Assert.NotNull(captured);
        Assert.Equal(artifact.Url, captured.Uri!.ToString());
        Assert.Equal("bytes=4-", captured.Range);
        Assert.Equal("\"strong-cached\"", captured.IfRange);
        Assert.True(File.Exists(finalPath));
        Assert.Equal(payload.Length, new FileInfo(finalPath).Length);
        Assert.Equal(artifact.Sha256, Convert.ToHexStringLower(SHA256.HashData(
            await File.ReadAllBytesAsync(finalPath))));
        Assert.False(File.Exists(metadataPath));
    }

    [Fact]
    public async Task Cancellation_RetainsResumablePartial_WhenStrongEtagMetadataExists()
    {
        using var temp = new TempDirectory();
        var layout = new ManagedRuntimeLayout(temp.Root, Path.Combine(temp.Root, "assets"));
        var payload = Payload(10);
        var artifact = CreateArtifact(payload);
        var (partialPath, metadataPath, finalPath) = ArtifactPaths(layout, artifact);

        using var cts = new CancellationTokenSource();
        var source = new PartiallyBlockingStream(payload, firstReadBytes: 4, cts.Token);
        using var client = new HttpClient(new RecordingHandler((_, _) =>
        {
            requestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(source)
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"strong-cached\"");
            response.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(response);
        }));
        using var store = new ManagedRuntimeArtifactStore(layout, client);

        var acquire = store.AcquireAsync(artifact, null, ProfileId, cts.Token);
        await source.Started.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquire);

        // Strong ETag metadata was written before the body stream, so the partial is resumable.
        Assert.True(File.Exists(metadataPath));
        Assert.True(File.Exists(partialPath));
        Assert.Equal(4, new FileInfo(partialPath).Length);
        Assert.False(File.Exists(finalPath));
    }

    [Fact]
    public async Task Cancellation_DoesNotRetainResumablePartial_WithoutStrongEtagMetadata()
    {
        using var temp = new TempDirectory();
        var layout = new ManagedRuntimeLayout(temp.Root, Path.Combine(temp.Root, "assets"));
        var payload = Payload(10);
        var artifact = CreateArtifact(payload);
        var (partialPath, metadataPath, finalPath) = ArtifactPaths(layout, artifact);

        using var cts = new CancellationTokenSource();
        var source = new PartiallyBlockingStream(payload, firstReadBytes: 4, cts.Token);
        using var client = new HttpClient(new RecordingHandler((_, _) =>
        {
            requestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(source)
            };
            response.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(response);
        }));
        using var store = new ManagedRuntimeArtifactStore(layout, client);

        var acquire = store.AcquireAsync(artifact, null, ProfileId, cts.Token);
        await source.Started.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquire);

        // No strong ETag: no resume metadata is written, so the leftover partial is not resumable.
        Assert.False(File.Exists(metadataPath));
        Assert.True(File.Exists(partialPath));
        Assert.False(File.Exists(finalPath));
    }

    private static ManagedRuntimeArtifact CreateArtifact(byte[] payload) => new(
        "runtime-embed.bin",
        payload.Length,
        Convert.ToHexStringLower(SHA256.HashData(payload)),
        "https://www.python.org/runtime-embed.bin");

    private static byte[] Payload(int length, int seed = 1)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(seed + i);
        }

        return bytes;
    }

    private static (string PartialPath, string MetadataPath, string FinalPath) ArtifactPaths(
        ManagedRuntimeLayout layout,
        ManagedRuntimeArtifact artifact)
    {
        var finalPath = Path.Combine(layout.DownloadsDirectory, artifact.FileName);
        var partialPath = finalPath + ".download";
        return (partialPath, partialPath + ".resume.json", finalPath);
    }

    private static void AssertNoArtifactFiles(
        ManagedRuntimeLayout layout,
        ManagedRuntimeArtifact artifact)
    {
        var (partialPath, metadataPath, finalPath) = ArtifactPaths(layout, artifact);
        Assert.False(File.Exists(finalPath), "失败的下载不得提升为最终工件。");
        Assert.False(File.Exists(partialPath), "失败后不应残留可续传的临时文件。");
        Assert.False(File.Exists(metadataPath), "失败后不应残留续传元数据。");
    }

    private static Task<HttpResponseMessage> ThrowIfCalled(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("不应发起任何 HTTP 请求。");

    private int requestCount;

    private sealed record RequestSnapshot(Uri? Uri, string? Range, string? IfRange);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    /// <summary>Stream that returns the first <paramref name="firstReadBytes"/> bytes, then blocks
    /// until the given token is cancelled and throws <see cref="OperationCanceledException"/>.</summary>
    private sealed class PartiallyBlockingStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _firstReadBytes;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;
        private bool _signaled;

        public PartiallyBlockingStream(byte[] data, int firstReadBytes, CancellationToken cancellationToken)
        {
            _data = data;
            _firstReadBytes = Math.Min(firstReadBytes, data.Length);
            cancellationToken.Register(() => _gate.TrySetResult());
        }

        public Task Started => _started.Task;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!_signaled)
            {
                _signaled = true;
                _started.TrySetResult();
            }

            if (_position < _firstReadBytes)
            {
                var count = Math.Min(_firstReadBytes - _position, buffer.Length);
                _data.AsSpan(_position, count).CopyTo(buffer.Span);
                _position += count;
                return count;
            }

            await _gate.Task.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            throw new OperationCanceledException(token);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "voxlink-artifact-" + Guid.NewGuid().ToString("N"));
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}