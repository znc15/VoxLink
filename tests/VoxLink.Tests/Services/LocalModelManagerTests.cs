using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Tests.Services;

public sealed class LocalModelManagerTests : IDisposable
{
    private static readonly byte[] DummyContent =
        "voxlink-dummy-model-artifact-v1"u8.ToArray();

    private static readonly byte[] OtherContent =
        "voxlink-dummy-model-artifact-v2"u8.ToArray();

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "voxlink-tests",
        $"local-models-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void DefaultRootDirectory_IsUnderLocalAppData()
    {
        var root = LocalModelManager.DefaultRootDirectory();
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            root,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("VoxLink", "models", "local"), root);
    }

    [Fact]
    public async Task Install_SingleFile_DownloadsVerifiesAndPlacesAtomically()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueBytes(DummyContent);
        using var manager = CreateManager(handler, [TestDefinition(artifact: DummyArtifact())]);

        var progress = new List<LocalModelProgressEventArgs>();
        manager.ModelProgress += (_, args) => progress.Add(args);
        await manager.InstallAsync("test-model", CancellationToken.None);

        var targetPath = Path.Combine(_root, "test-model", "model.bin");
        Assert.Equal(LocalModelInstallState.Installed, manager.GetStatus("test-model"));
        Assert.Equal(DummyContent, File.ReadAllBytes(targetPath));
        Assert.False(File.Exists(targetPath + ".download"));
        Assert.Single(handler.RequestedUris);
        Assert.Contains(progress, args => args.Progress is 1);
        Assert.All(progress, args => Assert.Equal("test-model", args.ModelId));
    }

    [Fact]
    public async Task Install_AlreadyVerified_SkipsDownload()
    {
        var handler = new ScriptedHttpHandler();
        using var manager = CreateManager(handler, [TestDefinition(artifact: DummyArtifact())]);
        SeedArtifact(manager, "test-model", "model.bin", DummyContent);

        await manager.InstallAsync("test-model", CancellationToken.None);

        Assert.Equal(LocalModelInstallState.Installed, manager.GetStatus("test-model"));
        Assert.Empty(handler.RequestedUris);
    }

    [Fact]
    public async Task Install_CorruptedContent_FailsAndCleansTemporaryFile()
    {
        var artifact = DummyArtifact();
        var handler = new ScriptedHttpHandler();
        handler.EnqueueBytes(OtherContent); // 同长度不同内容：SHA-256 不匹配
        handler.EnqueueBytes(OtherContent); // 镜像同样损坏
        using var manager = CreateManager(handler, [TestDefinition(artifact: artifact)]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.InstallAsync("test-model", CancellationToken.None));

        Assert.Equal(LocalModelInstallState.NotInstalled, manager.GetStatus("test-model"));
        Assert.False(File.Exists(Path.Combine(_root, "test-model", "model.bin")));
        Assert.False(File.Exists(Path.Combine(_root, "test-model", "model.bin.download")));
    }

    [Fact]
    public async Task Install_TruncatedBody_FailsVerificationAndCleansUp()
    {
        var artifact = DummyArtifact(mirrorUrl: null);
        var handler = new ScriptedHttpHandler();
        handler.EnqueueNoLength(DummyContent[..^5]); // 无 Content-Length 的截断响应
        using var manager = CreateManager(handler, [TestDefinition(artifact: artifact)]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.InstallAsync("test-model", CancellationToken.None));

        Assert.Equal(LocalModelInstallState.NotInstalled, manager.GetStatus("test-model"));
        Assert.False(File.Exists(Path.Combine(_root, "test-model", "model.bin.download")));
    }

    [Fact]
    public async Task Install_PrimaryFails_FallsBackToMirror()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueStatus(HttpStatusCode.InternalServerError);
        handler.EnqueueBytes(DummyContent);
        using var manager = CreateManager(handler, [TestDefinition(artifact: DummyArtifact())]);

        await manager.InstallAsync("test-model", CancellationToken.None);

        Assert.Equal(LocalModelInstallState.Installed, manager.GetStatus("test-model"));
        Assert.Equal(2, handler.RequestedUris.Count);
        Assert.Contains("hf-mirror.com", handler.RequestedUris[1].Host, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_RejectsHostOutsideAllowlist_WithoutAnyRequest()
    {
        var handler = new ScriptedHttpHandler();
        var artifact = DummyArtifact(
            primaryUrl: "https://evil.example.com/model.bin",
            mirrorUrl: null);
        using var manager = CreateManager(handler, [TestDefinition(artifact: artifact)]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.InstallAsync("test-model", CancellationToken.None));

        Assert.Contains("evil.example.com", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestedUris);
        Assert.Equal(LocalModelInstallState.NotInstalled, manager.GetStatus("test-model"));
    }

    [Fact]
    public async Task Install_CatalogOnlyDefinition_IsRejected()
    {
        using var manager = CreateManager(new ScriptedHttpHandler(), [TestDefinition(
            supportLevel: LocalModelSupportLevel.CatalogOnly)]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.InstallAsync("test-model", CancellationToken.None));

        Assert.Contains("目录展示", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_ArchiveWithEmptyArtifacts_ThrowsNotSupported()
    {
        var definition = TestDefinition() with
        {
            InstallKind = LocalModelInstallKind.Archive,
            Artifacts = [],
            Archive = new LocalModelArchiveSource(
                "https://huggingface.co/test-org/test-model/resolve/main/model.tar.bz2",
                null,
                1024,
                new string('0', 64),
                "test-model")
        };
        using var manager = CreateManager(new ScriptedHttpHandler(), [definition]);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            manager.InstallAsync("test-model", CancellationToken.None));
    }

    [Fact]
    public async Task Install_Archive_VerifiesExtractsAndAtomicallyReplacesExistingModel()
    {
        var archiveBytes = CreateTarBzip2Archive(("archive-root/model.bin", DummyContent));
        var artifact = DummyArtifact(relativePath: "model.bin", primaryUrl: string.Empty, mirrorUrl: null);
        var definition = TestDefinition(artifact: artifact, installKind: LocalModelInstallKind.Archive) with
        {
            Archive = new LocalModelArchiveSource(
                "https://huggingface.co/test-org/test-model/resolve/main/model.tar.bz2",
                null,
                archiveBytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(archiveBytes)),
                "archive-root")
        };
        var handler = new ScriptedHttpHandler();
        handler.EnqueueBytes(archiveBytes);
        using var manager = CreateManager(handler, [definition]);
        SeedArtifact(manager, "test-model", "old.bin", OtherContent);

        await manager.InstallAsync("test-model", CancellationToken.None);

        Assert.Equal(LocalModelInstallState.Installed, manager.GetStatus("test-model"));
        Assert.Equal(DummyContent, File.ReadAllBytes(
            Path.Combine(manager.RootDirectory, "test-model", "model.bin")));
        Assert.False(File.Exists(Path.Combine(manager.RootDirectory, "test-model", "old.bin")));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(manager.RootDirectory),
            path => Path.GetFileName(path).Contains(".staging", StringComparison.Ordinal)
                || Path.GetFileName(path).Contains(".backup", StringComparison.Ordinal)
                || Path.GetFileName(path).EndsWith(".archive.download", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Install_CorruptArchive_PreservesExistingVerifiedModelDirectory()
    {
        var goodArtifact = DummyArtifact(relativePath: "model.bin", primaryUrl: string.Empty, mirrorUrl: null);
        var corruptArchive = OtherContent;
        var definition = TestDefinition(artifact: goodArtifact, installKind: LocalModelInstallKind.Archive) with
        {
            Archive = new LocalModelArchiveSource(
                "https://huggingface.co/test-org/test-model/resolve/main/model.tar.bz2",
                null,
                corruptArchive.Length,
                Convert.ToHexStringLower(SHA256.HashData(corruptArchive)),
                "archive-root")
        };
        var handler = new ScriptedHttpHandler();
        handler.EnqueueBytes(corruptArchive);
        using var manager = CreateManager(handler, [definition]);
        SeedArtifact(manager, "test-model", "model.bin", DummyContent);
        File.WriteAllText(Path.Combine(manager.RootDirectory, "test-model", "marker.txt"), "keep");

        // Force installation despite a valid target by corrupting the catalog artifact hash while
        // preserving the existing directory as rollback evidence.
        definition = definition with
        {
            Artifacts = [goodArtifact with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(OtherContent)) }]
        };
        using var retryManager = CreateManager(handler, [definition]);

        await Assert.ThrowsAnyAsync<InvalidDataException>(() =>
            retryManager.InstallAsync("test-model", CancellationToken.None));

        Assert.Equal("keep", File.ReadAllText(
            Path.Combine(retryManager.RootDirectory, "test-model", "marker.txt")));
        Assert.Equal(DummyContent, File.ReadAllBytes(
            Path.Combine(retryManager.RootDirectory, "test-model", "model.bin")));
    }

    [Fact]
    public async Task Install_UnknownModelId_Throws()
    {
        using var manager = CreateManager(new ScriptedHttpHandler(), [TestDefinition()]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.InstallAsync("no-such-model", CancellationToken.None));
    }

    [Fact]
    public async Task Install_WhisperModel_DelegatesToWhisperInstallerAndForwardsProgress()
    {
        var installer = new FakeWhisperInstaller { State = LocalModelInstallState.NotInstalled };
        var definition = TestDefinition(installKind: LocalModelInstallKind.WhisperGgml)
            with { Artifacts = [], WhisperModelName = "tiny" };
        using var manager = CreateManager(
            new ScriptedHttpHandler(), [definition], installer);

        var progress = new List<LocalModelProgressEventArgs>();
        manager.ModelProgress += (_, args) => progress.Add(args);
        await manager.InstallAsync("test-model", CancellationToken.None);

        Assert.Equal(["tiny"], installer.Prepared);
        Assert.Equal(LocalModelInstallState.Installed, manager.GetStatus("test-model"));
        Assert.Contains(progress, args => args is
            { ModelId: "test-model", Category: LocalModelCategory.Translation, Progress: 1 });
    }

    [Fact]
    public void GetStatus_CachesVerificationPerFileAndRechecksOnModification()
    {
        // 同一文件 size+mtime 未变时不应重复哈希；文件被替换后必须重新校验。
        using var manager = CreateManager(
            new ScriptedHttpHandler(),
            [TestDefinition(artifact: DummyArtifact())]);
        SeedArtifact(manager, "test-model", "model.bin", DummyContent);

        Assert.Equal(LocalModelInstallState.Installed, manager.GetStatus("test-model"));

        // 同内容重写但保持 mtime 不变 → 缓存命中（仍 Installed，无需重新哈希）。
        var artifact = DummyArtifact();
        var artifactPath = Path.Combine(manager.RootDirectory, "test-model", "model.bin");
        var lastWrite = File.GetLastWriteTimeUtc(artifactPath);
        File.WriteAllBytes(artifactPath, DummyContent);
        File.SetLastWriteTimeUtc(artifactPath, lastWrite);
        Assert.Equal(LocalModelInstallState.Installed, manager.GetStatus("test-model"));

        // 换成内容不匹配但长度相同的字节并更新 mtime → 缓存失效，重新哈希发现损坏。
        var corrupted = new byte[artifact.ExpectedSize];
        OtherContent.AsSpan().CopyTo(corrupted);
        File.WriteAllBytes(artifactPath, corrupted);
        File.SetLastWriteTimeUtc(artifactPath, lastWrite.AddSeconds(5));
        Assert.Equal(LocalModelInstallState.Partial, manager.GetStatus("test-model"));

        // 恢复正确内容 → 重新校验通过且结论入缓存。
        File.WriteAllBytes(artifactPath, DummyContent);
        File.SetLastWriteTimeUtc(artifactPath, lastWrite.AddSeconds(10));
        Assert.Equal(LocalModelInstallState.Installed, manager.GetStatus("test-model"));
    }

    [Fact]
    public async Task AcquireUsage_AfterInstall_HitsVerificationCacheWithoutRehash()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueBytes(DummyContent);
        using var manager = CreateManager(handler, [TestDefinition(artifact: DummyArtifact())]);

        await manager.InstallAsync("test-model", CancellationToken.None);

        // 安装完成后立刻取用：校验结论来自缓存，不应触发任何额外 HTTP 或失败。
        using var lease = manager.AcquireUsage("test-model");
        Assert.True(File.Exists(lease.ResolvePath("model.bin")));
    }

    [Theory]
    [InlineData(LocalModelInstallKind.SingleFile)]
    [InlineData(LocalModelInstallKind.ManifestFiles)]
    [InlineData(LocalModelInstallKind.Archive)]
    public void GetStatus_ArtifactBasedKinds_FollowDiskTruth(LocalModelInstallKind installKind)
    {
        var artifactA = DummyArtifact(relativePath: "a.bin");
        var artifactB = DummyArtifact(relativePath: "b.bin");
        using var manager = CreateManager(
            new ScriptedHttpHandler(),
            [TestDefinition(installKind: installKind, artifacts: [artifactA, artifactB])]);

        Assert.Equal(LocalModelInstallState.NotInstalled, manager.GetStatus("test-model"));

        SeedArtifact(manager, "test-model", "a.bin", DummyContent);
        Assert.Equal(LocalModelInstallState.Partial, manager.GetStatus("test-model"));

        SeedArtifact(manager, "test-model", "b.bin", DummyContent);
        Assert.Equal(LocalModelInstallState.Installed, manager.GetStatus("test-model"));

        SeedArtifact(manager, "test-model", "b.bin", OtherContent); // 损坏
        Assert.Equal(LocalModelInstallState.Partial, manager.GetStatus("test-model"));
    }

    [Fact]
    public void GetStatus_WhisperModel_DelegatesToInstaller()
    {
        var installer = new FakeWhisperInstaller { State = LocalModelInstallState.Partial };
        var definition = TestDefinition(installKind: LocalModelInstallKind.WhisperGgml)
            with { Artifacts = [], WhisperModelName = "base" };
        using var manager = CreateManager(new ScriptedHttpHandler(), [definition], installer);

        Assert.Equal(LocalModelInstallState.Partial, manager.GetStatus("test-model"));
        installer.State = LocalModelInstallState.Installed;
        Assert.Equal(LocalModelInstallState.Installed, manager.GetStatus("test-model"));
    }

    [Fact]
    public async Task Remove_DeletesModelDirectory_AndReportsWhetherAnythingWasRemoved()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueBytes(DummyContent);
        using var manager = CreateManager(handler, [TestDefinition(artifact: DummyArtifact())]);
        await manager.InstallAsync("test-model", CancellationToken.None);

        Assert.True(await manager.RemoveAsync("test-model", CancellationToken.None));
        Assert.Equal(LocalModelInstallState.NotInstalled, manager.GetStatus("test-model"));
        Assert.False(Directory.Exists(Path.Combine(_root, "test-model")));
        Assert.False(await manager.RemoveAsync("test-model", CancellationToken.None));
    }

    [Fact]
    public async Task AcquireUsage_BlocksRemovalUntilLeaseIsReleased()
    {
        using var manager = CreateManager(
            new ScriptedHttpHandler(),
            [TestDefinition(artifact: DummyArtifact())]);
        SeedArtifact(manager, "test-model", "model.bin", DummyContent);
        using var lease = manager.AcquireUsage("test-model");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.RemoveAsync("test-model", CancellationToken.None));

        Assert.Contains("正在被本地运行时使用", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(lease.ResolvePath("model.bin")));
        lease.Dispose();
        Assert.True(await manager.RemoveAsync("test-model", CancellationToken.None));
    }

    [Fact]
    public async Task DisposeAsync_WaitsForActiveLeaseAndRejectsNewOperations()
    {
        var manager = CreateManager(
            new ScriptedHttpHandler(),
            [TestDefinition(artifact: DummyArtifact())]);
        SeedArtifact(manager, "test-model", "model.bin", DummyContent);
        var lease = manager.AcquireUsage("test-model");

        var dispose = manager.DisposeAsync().AsTask();
        await Task.Yield();

        Assert.False(dispose.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => manager.AcquireUsage("test-model"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            manager.RemoveAsync("test-model", CancellationToken.None));

        lease.Dispose();
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CancelsAndDrainsInFlightInstall()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var manager = CreateManager(handler, [TestDefinition(artifact: DummyArtifact(mirrorUrl: null))]);
        var install = manager.InstallAsync("test-model", CancellationToken.None);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var dispose = manager.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(File.Exists(Path.Combine(_root, "test-model", "model.bin.download")));
    }

    [Fact]
    public async Task CancelledInstall_ReleasesModelGateAndSubsequentInstallSucceeds()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        handler.EnqueueBytes(DummyContent);
        using var manager = CreateManager(
            handler,
            [TestDefinition(artifact: DummyArtifact(mirrorUrl: null))]);
        using var cancellation = new CancellationTokenSource();
        var first = manager.InstallAsync("test-model", cancellation.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(File.Exists(Path.Combine(_root, "test-model", "model.bin.download")));

        await manager.InstallAsync("test-model", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(LocalModelInstallState.Installed, manager.GetStatus("test-model"));
        Assert.Equal(2, handler.RequestedUris.Count);
    }

    [Fact]
    public async Task Install_NetworkInterruption_ResumesWithRangeAndStrongEtag()
    {
        const string etag = "\"model-v1\"";
        var prefixLength = 10;
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(_ => CreateDownloadResponse(
            HttpStatusCode.OK,
            new PrefixThenBrokenStream(DummyContent[..prefixLength]),
            etag));
        handler.Enqueue(request =>
        {
            Assert.Equal(prefixLength, request.Headers.Range?.Ranges.Single().From);
            Assert.Equal(etag, request.Headers.IfRange?.EntityTag?.ToString());
            var response = CreateDownloadResponse(
                HttpStatusCode.PartialContent,
                new MemoryStream(DummyContent[prefixLength..], writable: false),
                etag);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                prefixLength,
                DummyContent.Length - 1,
                DummyContent.Length);
            return response;
        });
        using var manager = CreateManager(
            handler,
            [TestDefinition(artifact: DummyArtifact(mirrorUrl: null))]);

        await Assert.ThrowsAsync<IOException>(() =>
            manager.InstallAsync("test-model", CancellationToken.None));
        Assert.Equal(LocalModelInstallState.Partial, manager.GetStatus("test-model"));
        Assert.True(File.Exists(Path.Combine(_root, "test-model", "model.bin.download")));

        await manager.InstallAsync("test-model", CancellationToken.None);

        Assert.Equal(LocalModelInstallState.Installed, manager.GetStatus("test-model"));
        Assert.Equal(DummyContent, File.ReadAllBytes(Path.Combine(_root, "test-model", "model.bin")));
        Assert.Null(handler.RequestedRanges[0].Range);
        Assert.NotNull(handler.RequestedRanges[1].Range);
    }

    [Fact]
    public async Task Install_ServerIgnoresRange_ResetsPartialBeforeFullDownload()
    {
        const string etag = "\"model-v1\"";
        var prefixLength = 8;
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(_ => CreateDownloadResponse(
            HttpStatusCode.OK,
            new PrefixThenBrokenStream(DummyContent[..prefixLength]),
            etag));
        handler.Enqueue(_ => CreateDownloadResponse(
            HttpStatusCode.OK,
            new MemoryStream(DummyContent, writable: false),
            etag));
        handler.Enqueue(_ => CreateDownloadResponse(
            HttpStatusCode.OK,
            new MemoryStream(DummyContent, writable: false),
            etag));
        using var manager = CreateManager(
            handler,
            [TestDefinition(artifact: DummyArtifact(mirrorUrl: null))]);

        await Assert.ThrowsAsync<IOException>(() =>
            manager.InstallAsync("test-model", CancellationToken.None));
        await manager.InstallAsync("test-model", CancellationToken.None);

        Assert.Equal(LocalModelInstallState.Installed, manager.GetStatus("test-model"));
        Assert.NotNull(handler.RequestedRanges[1].Range);
        Assert.Null(handler.RequestedRanges[2].Range);
        Assert.Equal(3, handler.RequestedUris.Count);
    }

    [Fact]
    public async Task Install_CancelAfterReceivingData_PreservesResumablePartial()
    {
        const string etag = "\"model-v1\"";
        var stream = new PrefixThenBlockingStream(DummyContent[..9]);
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(_ => CreateDownloadResponse(HttpStatusCode.OK, stream, etag));
        using var manager = CreateManager(
            handler,
            [TestDefinition(artifact: DummyArtifact(mirrorUrl: null))]);
        using var cancellation = new CancellationTokenSource();
        var install = manager.InstallAsync("test-model", cancellation.Token);
        await stream.PrefixRead.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);

        Assert.Equal(LocalModelInstallState.Partial, manager.GetStatus("test-model"));
        Assert.Equal(
            9,
            new FileInfo(Path.Combine(_root, "test-model", "model.bin.download")).Length);
        Assert.True(File.Exists(Path.Combine(_root, "test-model", "model.bin.download.resume.json")));
    }

    [Fact]
    public async Task Install_InsufficientDiskSpace_FailsBeforeNetworkOrDirectoryMutation()
    {
        var handler = new ScriptedHttpHandler();
        using var manager = CreateManager(
            handler,
            [TestDefinition(artifact: DummyArtifact(mirrorUrl: null))],
            getAvailableFreeSpaceBytes: _ => 0);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            manager.InstallAsync("test-model", CancellationToken.None));

        Assert.Contains("可用空间", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestedUris);
        Assert.False(Directory.Exists(Path.Combine(_root, "test-model")));
    }

    [Fact]
    public async Task Install_LargeArtifact_RequiresExplicitReviewedCatalogFlag()
    {
        var largeArtifact = new LocalModelArtifact(
            "model.bin",
            LocalModelManager.MaxArtifactBytes + 1,
            new string('a', 64),
            "https://huggingface.co/test-org/test-model/resolve/fixed/model.bin",
            null);

        var blockedHandler = new ScriptedHttpHandler();
        using (var blocked = CreateManager(
            blockedHandler,
            [TestDefinition(artifact: largeArtifact)],
            getAvailableFreeSpaceBytes: _ => long.MaxValue))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                blocked.InstallAsync("test-model", CancellationToken.None));
            Assert.Empty(blockedHandler.RequestedUris);
        }

        var allowedHandler = new ScriptedHttpHandler();
        allowedHandler.EnqueueStatus(HttpStatusCode.NotFound);
        var reviewedDefinition = TestDefinition(
            artifact: largeArtifact,
            installKind: LocalModelInstallKind.ManifestFiles) with
        {
            AllowsLargeArtifacts = true
        };
        using var allowed = CreateManager(
            allowedHandler,
            [reviewedDefinition],
            getAvailableFreeSpaceBytes: _ => long.MaxValue);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            allowed.InstallAsync("test-model", CancellationToken.None));
        Assert.Single(allowedHandler.RequestedUris);
    }

    private static HttpResponseMessage CreateDownloadResponse(
        HttpStatusCode statusCode,
        Stream stream,
        string etag)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StreamContent(stream)
        };
        response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }

    [Fact]
    public async Task Install_StalledResponseBody_TimesOutAndFallsBackToMirror()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueStalledBody();
        handler.EnqueueBytes(DummyContent);
        using var manager = CreateManager(
            handler,
            [TestDefinition(artifact: DummyArtifact())],
            downloadReadTimeout: TimeSpan.FromMilliseconds(50));

        await manager.InstallAsync("test-model", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(LocalModelInstallState.Installed, manager.GetStatus("test-model"));
        Assert.Equal(2, handler.RequestedUris.Count);
        Assert.False(File.Exists(Path.Combine(_root, "test-model", "model.bin.download")));
    }

    [Fact]
    public async Task Install_ResponseBodyIoFailure_FallsBackToMirror()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueBrokenBody();
        handler.EnqueueBytes(DummyContent);
        using var manager = CreateManager(
            handler,
            [TestDefinition(artifact: DummyArtifact())]);

        await manager.InstallAsync("test-model", CancellationToken.None);

        Assert.Equal(LocalModelInstallState.Installed, manager.GetStatus("test-model"));
        Assert.Equal(2, handler.RequestedUris.Count);
        Assert.False(File.Exists(Path.Combine(_root, "test-model", "model.bin.download")));
    }

    [Fact]
    public async Task Remove_WhisperModel_DelegatesToInstaller()
    {
        var installer = new FakeWhisperInstaller();
        var definition = TestDefinition(installKind: LocalModelInstallKind.WhisperGgml)
            with { Artifacts = [], WhisperModelName = "small" };
        using var manager = CreateManager(new ScriptedHttpHandler(), [definition], installer);

        Assert.True(await manager.RemoveAsync("test-model", CancellationToken.None));
        Assert.Equal(["small"], installer.Removed);
    }

    [Theory]
    [InlineData("https://huggingface.co/org/model/resolve/main/m.bin", true)]
    [InlineData("https://cdn-lfs.huggingface.co/repos/12/34", true)]
    [InlineData("https://us.aws.cdn.hf.co/xet-bridge-us/model", true)]
    [InlineData("https://cas-bridge.xethub.hf.co/xet-bridge-us/model", true)]
    [InlineData("https://untrusted.hf.co/model", false)]
    [InlineData("https://hf-mirror.com/org/model/resolve/main/m.bin", true)]
    [InlineData("https://github.com/org/repo/releases/download/v1/m.bin", true)]
    [InlineData("https://objects.githubusercontent.com/x/y", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/x/y", true)]
    [InlineData("http://huggingface.co/org/model.bin", false)]
    [InlineData("ftp://huggingface.co/org/model.bin", false)]
    [InlineData("https://evil.example.com/model.bin", false)]
    [InlineData("https://huggingface.co.evil.example.com/m.bin", false)]
    [InlineData("https://user:pass@huggingface.co/org/model.bin", false)]
    [InlineData("not-a-url", false)]
    public void ValidateDownloadUrl_EnforcesHttpsHostAllowlist(string url, bool allowed)
    {
        if (allowed)
        {
            var uri = LocalModelManager.ValidateDownloadUrl(url);
            Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() =>
                LocalModelManager.ValidateDownloadUrl(url));
        }
    }

    [Theory]
    [InlineData("model.bin", true)]
    [InlineData("sub/dir/model.bin", true)]
    [InlineData("../evil.bin", false)]
    [InlineData("a/../../evil.bin", false)]
    [InlineData("./model.bin", false)]
    [InlineData("/abs/model.bin", false)]
    [InlineData("a\\b.bin", false)]
    [InlineData("a:b.bin", false)]
    [InlineData("a//b.bin", false)]
    [InlineData("", false)]
    public void ValidateSafeRelativePath_RejectsTraversal(string relativePath, bool valid)
    {
        if (valid)
        {
            Assert.DoesNotContain("..", LocalModelManager.ValidateSafeRelativePath(relativePath));
        }
        else
        {
            Assert.ThrowsAny<ArgumentException>(() =>
                LocalModelManager.ValidateSafeRelativePath(relativePath));
        }
    }

    [Fact]
    public void ProductionCatalog_AllStatusesAreQueryable()
    {
        using var manager = CreateManager(
            new ScriptedHttpHandler(),
            LocalModelCatalog.All,
            new FakeWhisperInstaller());

        var states = LocalModelCatalog.All.ToDictionary(
            model => model.Id,
            model => manager.GetStatus(model.Id),
            StringComparer.Ordinal);

        Assert.Equal(LocalModelCatalog.All.Count, states.Count);
        Assert.Equal(
            LocalModelInstallState.NotInstalled,
            states[LocalModelIds.WhisperLargeV3Turbo]);
    }

    private LocalModelManager CreateManager(
        ScriptedHttpHandler handler,
        IReadOnlyList<LocalModelDefinition> catalog,
        IWhisperModelInstaller? installer = null,
        TimeSpan? downloadReadTimeout = null,
        Func<string, long>? getAvailableFreeSpaceBytes = null) => new(
            _root,
            catalog,
            installer ?? new FakeWhisperInstaller(),
            new HttpClient(handler),
            downloadReadTimeout: downloadReadTimeout,
            getAvailableFreeSpaceBytes: getAvailableFreeSpaceBytes);

    private static LocalModelArtifact DummyArtifact(
        string relativePath = "model.bin",
        string primaryUrl = "https://huggingface.co/test-org/test-model/resolve/main/model.bin",
        string? mirrorUrl = "https://hf-mirror.com/test-org/test-model/resolve/main/model.bin") => new(
            relativePath,
            DummyContent.Length,
            Convert.ToHexStringLower(SHA256.HashData(DummyContent)),
            primaryUrl,
            mirrorUrl);

    private static LocalModelDefinition TestDefinition(
        LocalModelArtifact? artifact = null,
        IReadOnlyList<LocalModelArtifact>? artifacts = null,
        LocalModelSupportLevel supportLevel = LocalModelSupportLevel.Experimental,
        LocalModelInstallKind installKind = LocalModelInstallKind.SingleFile) => new()
    {
        Id = "test-model",
        Name = "Test Model",
        Category = LocalModelCategory.Translation,
        SupportLevel = supportLevel,
        Runtime = LocalModelRuntimeKind.LlamaCppGguf,
        InstallKind = installKind,
        Parameters = "约 1B",
        NumericParameterBillions = 1.0,
        License = "MIT",
        Languages = "zh/en",
        Requirements = "test",
        SourceUrl = "https://huggingface.co/test-org/test-model",
        Description = "test-only definition",
        Artifacts = artifacts ?? (artifact is null ? [] : [artifact])
    };

    private static byte[] CreateTarBzip2Archive(params (string Path, byte[] Content)[] files)
    {
        using var compressed = new MemoryStream();
        using (var bzip2 = BZip2Stream.Create(
                   compressed,
                   CompressionMode.Compress,
                   decompressConcatenated: false,
                   leaveOpen: true,
                   tolerateTruncatedStream: false))
        {
            using (var writer = new TarWriter(bzip2, leaveOpen: true))
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "archive-root"));
                foreach (var (path, content) in files)
                {
                    var entry = new PaxTarEntry(TarEntryType.RegularFile, path)
                    {
                        DataStream = new MemoryStream(content, writable: false)
                    };
                    writer.WriteEntry(entry);
                }
            }

            bzip2.Finish();
        }

        return compressed.ToArray();
    }

    private static void SeedArtifact(
        LocalModelManager manager,
        string modelId,
        string relativePath,
        byte[] content)
    {
        var directory = Path.Combine(manager.RootDirectory, modelId);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, relativePath), content);
    }

    private sealed class ScriptedHttpHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();

        public void EnqueueStalledBody() => Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream())
        });

        public void EnqueueBrokenBody() => Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BrokenStream())
        });
        public List<Uri> RequestedUris { get; } = [];
        public List<(RangeHeaderValue? Range, RangeConditionHeaderValue? IfRange)> RequestedRanges { get; } = [];
        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void EnqueueBytes(byte[] content) => Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
        });

        /// <summary>模拟无 Content-Length 的截断响应（chunked）。</summary>
        public void EnqueueNoLength(byte[] content) => Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new NoLengthContent(content)
        });

        public void EnqueueStatus(HttpStatusCode statusCode) =>
            Enqueue(_ => new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent([])
            });

        public void Enqueue(Func<CancellationToken, Task<HttpResponseMessage>> factory) =>
            _responses.Enqueue((_, cancellationToken) => factory(cancellationToken));

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> factory) =>
            _responses.Enqueue((request, _) => Task.FromResult(factory(request)));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);
            RequestedRanges.Add((request.Headers.Range, request.Headers.IfRange));
            RequestStarted.TrySetResult();
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("未预期的 HTTP 请求：" + request.RequestUri);
            }

            return _responses.Dequeue()(request, cancellationToken);
        }
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("connection reset"));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PrefixThenBrokenStream(byte[] prefix) : Stream
    {
        private bool _prefixReturned;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_prefixReturned)
            {
                return ValueTask.FromException<int>(new IOException("connection reset"));
            }

            _prefixReturned = true;
            prefix.CopyTo(buffer);
            return ValueTask.FromResult(prefix.Length);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PrefixThenBlockingStream(byte[] prefix) : Stream
    {
        private bool _prefixReturned;
        public TaskCompletionSource PrefixRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_prefixReturned)
            {
                _prefixReturned = true;
                prefix.CopyTo(buffer);
                PrefixRead.TrySetResult();
                return prefix.Length;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NoLengthContent : HttpContent
    {
        private readonly byte[] _data;

        public NoLengthContent(byte[] data) => _data = data;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await stream.WriteAsync(_data, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private sealed class FakeWhisperInstaller : IWhisperModelInstaller
    {
        public event EventHandler<ModelProgressEventArgs>? ModelProgress;

        public List<string> Prepared { get; } = [];
        public List<string> Removed { get; } = [];
        public LocalModelInstallState State { get; set; } = LocalModelInstallState.NotInstalled;

        public Task PrepareAsync(string modelName, CancellationToken cancellationToken = default)
        {
            Prepared.Add(modelName);
            ModelProgress?.Invoke(this, new ModelProgressEventArgs("下载中…", 0.5));
            ModelProgress?.Invoke(this, new ModelProgressEventArgs("完成", 1));
            State = LocalModelInstallState.Installed;
            return Task.CompletedTask;
        }

        public LocalModelInstallState GetInstallState(string modelName) => State;

        public bool TryRemoveModel(string modelName)
        {
            Removed.Add(modelName);
            return true;
        }
    }
}
