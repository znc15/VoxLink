using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using SharpCompress.Common;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using VoxLink.Models;

namespace VoxLink.Services;

public interface ILocalModelLease : IDisposable
{
    string ModelId { get; }

    string ModelDirectory { get; }

    string ResolvePath(string relativePath);
}

/// <summary>Installs verified local models and protects model files while runtimes use them.</summary>
public interface ILocalModelManager
{
    event EventHandler<LocalModelProgressEventArgs>? ModelProgress;

    IReadOnlyList<LocalModelDefinition> List();

    LocalModelInstallState GetStatus(string modelId);

    Task InstallAsync(string modelId, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(string modelId, CancellationToken cancellationToken = default);

    ILocalModelLease AcquireUsage(string modelId);
}

/// <summary>
/// The disk is the source of truth. Downloads are bounded and verified before replacement;
/// archives are extracted into an isolated sibling directory and switched atomically.
/// </summary>
public sealed class LocalModelManager : ILocalModelManager, IDisposable, IAsyncDisposable
{
    public const long MaxArtifactBytes = 4L * 1024 * 1024 * 1024;
    public const long MaxReviewedArtifactBytes = 8L * 1024 * 1024 * 1024;
    public const long MaxArchiveExpandedBytes = 8L * 1024 * 1024 * 1024;

    internal static readonly IReadOnlyList<string> AllowedHosts =
    [
        "huggingface.co",
        "hf-mirror.com",
        "cdn.hf.co",
        "xethub.hf.co",
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    ];

    private const int MaxRedirects = 5;
    internal static readonly TimeSpan DefaultDownloadReadTimeout = TimeSpan.FromMinutes(2);
    private readonly IReadOnlyList<LocalModelDefinition> _catalog;
    private readonly IWhisperModelInstaller _whisperInstaller;
    private readonly HttpClient _httpClient;
    private readonly string _rootDirectory;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _downloadReadTimeout;
    private readonly Func<string, long> _getAvailableFreeSpaceBytes;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _operationGates =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _whisperInstallGate = new(1, 1);
    private readonly object _stateSync = new();
    private readonly Dictionary<string, int> _usageCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changingModels = new(StringComparer.Ordinal);
    // Counts install/remove operations and active model leases; disposal drains both before freeing gates.
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly TaskCompletionSource _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile LocalModelDefinition? _activeWhisperInstall;
    private TaskCompletionSource? _operationsDrained;
    private int _activeOperations;
    private bool _disposeStarted;
    private int _disposed;
    public LocalModelManager()
        : this(
            DefaultRootDirectory(),
            LocalModelCatalog.All,
            new WhisperModelInstallerAdapter(),
            CreateDefaultHttpClient(),
            ownsHttpClient: true)
    {
    }

    public LocalModelManager(string rootDirectory)
        : this(
            rootDirectory,
            LocalModelCatalog.All,
            new WhisperModelInstallerAdapter(rootDirectory),
            CreateDefaultHttpClient(),
            ownsHttpClient: true)
    {
    }

    internal LocalModelManager(
        string rootDirectory,
        IReadOnlyList<LocalModelDefinition> catalog,
        IWhisperModelInstaller whisperInstaller,
        HttpClient httpClient,
        bool ownsHttpClient = false,
        TimeSpan? downloadReadTimeout = null,
        Func<string, long>? getAvailableFreeSpaceBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(whisperInstaller);
        ArgumentNullException.ThrowIfNull(httpClient);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _catalog = catalog;
        _whisperInstaller = whisperInstaller;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _downloadReadTimeout = downloadReadTimeout ?? DefaultDownloadReadTimeout;
        _getAvailableFreeSpaceBytes = getAvailableFreeSpaceBytes ?? GetAvailableFreeSpaceBytes;
        if (_downloadReadTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(downloadReadTimeout),
                "下载无进度超时必须大于零。");
        }
        _whisperInstaller.ModelProgress += OnWhisperModelProgress;
    }

    public event EventHandler<LocalModelProgressEventArgs>? ModelProgress;

    internal string RootDirectory => _rootDirectory;

    public IReadOnlyList<LocalModelDefinition> List()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _catalog;
    }

    public LocalModelInstallState GetStatus(string modelId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var definition = RequireDefinition(modelId);
        return GetStatusForDefinition(definition);
    }

    public async Task InstallAsync(string modelId, CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdownCancellation.Token);
        var operationToken = linkedCancellation.Token;
        var definition = RequireDefinition(modelId);
        if (!definition.IsInstallable)
        {
            throw new InvalidOperationException($"模型 {definition.Name} 仅提供目录展示，暂不支持一键部署。");
        }

        var operationGate = _operationGates.GetOrAdd(definition.Id, static _ => new SemaphoreSlim(1, 1));
        var gateEntered = false;
        await operationGate.WaitAsync(operationToken).ConfigureAwait(false);
        gateEntered = true;
        var changeReserved = false;
        try
        {
            if (GetArtifactStatusForDefinition(definition) == LocalModelInstallState.Installed)
            {
                ReportProgress(definition, "模型已安装并通过校验", 1);
                return;
            }

            EnsureSufficientDiskSpace(definition);

            lock (_stateSync)
            {
                ThrowIfInUse(definition.Id);
                _changingModels.Add(definition.Id);
                changeReserved = true;
            }

            switch (definition.InstallKind)
            {
                case LocalModelInstallKind.WhisperGgml:
                    await InstallWhisperAsync(definition, operationToken).ConfigureAwait(false);
                    break;
                case LocalModelInstallKind.SingleFile or LocalModelInstallKind.ManifestFiles
                    when definition.Artifacts.Count > 0:
                    await InstallArtifactsAsync(definition, operationToken).ConfigureAwait(false);
                    break;
                case LocalModelInstallKind.Archive when definition.Archive is not null
                    && definition.Artifacts.Count > 0:
                    await InstallArchiveAsync(definition, operationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException($"模型 {definition.Name} 的安装形态尚未实现。");
            }
        }
        finally
        {
            if (changeReserved)
            {
                lock (_stateSync)
                {
                    _changingModels.Remove(definition.Id);
                }
            }

            if (gateEntered)
            {
                operationGate.Release();
            }
        }
    }

    public async Task<bool> RemoveAsync(string modelId, CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdownCancellation.Token);
        var operationToken = linkedCancellation.Token;
        var definition = RequireDefinition(modelId);
        var operationGate = _operationGates.GetOrAdd(
            definition.Id,
            static _ => new SemaphoreSlim(1, 1));
        var gateEntered = false;
        await operationGate.WaitAsync(operationToken).ConfigureAwait(false);
        gateEntered = true;
        var changeReserved = false;
        try
        {
            operationToken.ThrowIfCancellationRequested();
            lock (_stateSync)
            {
                ThrowIfInUse(definition.Id);
                _changingModels.Add(definition.Id);
                changeReserved = true;
            }

            if (definition.InstallKind == LocalModelInstallKind.WhisperGgml
                && string.IsNullOrWhiteSpace(definition.WhisperModelName))
            {
                return false;
            }

            return definition.InstallKind == LocalModelInstallKind.WhisperGgml
                ? _whisperInstaller.TryRemoveModel(RequireWhisperModelName(definition))
                : RemoveModelDirectory(definition.Id);
        }
        finally
        {
            if (changeReserved)
            {
                lock (_stateSync)
                {
                    _changingModels.Remove(definition.Id);
                }
            }

            if (gateEntered)
            {
                operationGate.Release();
            }
        }
    }

    public ILocalModelLease AcquireUsage(string modelId)
    {
        var definition = RequireDefinition(modelId);
        if (definition.InstallKind == LocalModelInstallKind.WhisperGgml)
        {
            throw new NotSupportedException("Whisper 模型由语音识别会话管理，不提供目录租约。");
        }

        string modelDirectory;
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            if (_changingModels.Contains(definition.Id))
            {
                throw new InvalidOperationException($"本地模型 {definition.Name} 正在安装或删除，请稍后重试。");
            }

            modelDirectory = GetModelDirectory(definition.Id);
            _usageCounts.TryGetValue(definition.Id, out var count);
            _usageCounts[definition.Id] = checked(count + 1);
            _activeOperations = checked(_activeOperations + 1);
        }

        try
        {
            if (GetArtifactStatus(definition) != LocalModelInstallState.Installed)
            {
                throw new InvalidOperationException($"本地模型 {definition.Name} 尚未安装或校验失败。");
            }

            return new LocalModelLease(this, definition.Id, modelDirectory);
        }
        catch
        {
            ReleaseUsage(definition.Id);
            throw;
        }
    }

    public void Dispose() => Task.Run(() => DisposeAsync().AsTask()).GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        Task drainTask;
        lock (_stateSync)
        {
            if (_disposeStarted)
            {
                drainTask = _disposeCompletion.Task;
            }
            else
            {
                _disposeStarted = true;
                Volatile.Write(ref _disposed, 1);
                drainTask = _activeOperations == 0
                    ? Task.CompletedTask
                    : (_operationsDrained ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }

        if (ReferenceEquals(drainTask, _disposeCompletion.Task))
        {
            await drainTask.ConfigureAwait(false);
            return;
        }

        try
        {
            _shutdownCancellation.Cancel();
            await drainTask.ConfigureAwait(false);
            _whisperInstaller.ModelProgress -= OnWhisperModelProgress;
            foreach (var gate in _operationGates.Values.Distinct())
            {
                gate.Dispose();
            }
            _operationGates.Clear();
            _whisperInstallGate.Dispose();
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
            _shutdownCancellation.Dispose();
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }

    internal static string DefaultRootDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoxLink",
        "models",
        "local");

    internal static Uri ValidateDownloadUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"模型下载地址无效：{url}");
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("模型下载地址必须使用 HTTPS。");
        }

        if (uri.UserInfo.Length > 0)
        {
            throw new InvalidOperationException("模型下载地址不允许携带凭据。");
        }

        var host = uri.IdnHost;
        var allowed = AllowedHosts.Any(entry =>
            string.Equals(host, entry, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith('.' + entry, StringComparison.OrdinalIgnoreCase));
        if (!allowed)
        {
            throw new InvalidOperationException($"模型下载主机 {host} 不在允许列表中。");
        }

        return uri;
    }

    internal static string ValidateSafeRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (relativePath.Contains('\\')
            || relativePath.Contains(':')
            || relativePath.StartsWith('/')
            || relativePath.Contains("//", StringComparison.Ordinal))
        {
            throw new ArgumentException($"模型工件路径无效：{relativePath}", nameof(relativePath));
        }

        var segments = relativePath.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0
                || segment is "." or ".."
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException($"模型工件路径无效：{relativePath}", nameof(relativePath));
            }
        }

        return string.Join(Path.DirectorySeparatorChar.ToString(), segments);
    }

    private async Task InstallWhisperAsync(
        LocalModelDefinition definition,
        CancellationToken cancellationToken)
    {
        await _whisperInstallGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _activeWhisperInstall = definition;
        try
        {
            await _whisperInstaller.PrepareAsync(
                RequireWhisperModelName(definition),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _activeWhisperInstall = null;
            _whisperInstallGate.Release();
        }
    }

    private LocalModelInstallState GetArtifactStatus(LocalModelDefinition definition)
    {
        if (definition.Artifacts.Count == 0)
        {
            return LocalModelInstallState.NotInstalled;
        }

        var modelDirectory = GetModelDirectory(definition.Id);
        var verified = 0;
        var present = 0;
        foreach (var artifact in definition.Artifacts)
        {
            var targetPath = Path.Combine(
                modelDirectory,
                ValidateSafeRelativePath(artifact.RelativePath));
            if (!File.Exists(targetPath))
            {
                continue;
            }

            present++;
            if (IsFileVerified(targetPath, artifact))
            {
                verified++;
            }
        }

        if (verified == definition.Artifacts.Count)
        {
            return LocalModelInstallState.Installed;
        }

        var temporaryPresent = definition.Artifacts.Any(artifact => File.Exists(
            Path.Combine(modelDirectory, ValidateSafeRelativePath(artifact.RelativePath)) + ".download"))
            || definition.Archive is not null && File.Exists(GetArchiveDownloadPath(definition.Id));
        return present > 0 || temporaryPresent
            ? LocalModelInstallState.Partial
            : LocalModelInstallState.NotInstalled;
    }

    private async Task InstallArtifactsAsync(
        LocalModelDefinition definition,
        CancellationToken cancellationToken)
    {
        var modelDirectory = GetModelDirectory(definition.Id);
        Directory.CreateDirectory(modelDirectory);
        for (var index = 0; index < definition.Artifacts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifact = definition.Artifacts[index];
            var targetPath = Path.Combine(
                modelDirectory,
                ValidateSafeRelativePath(artifact.RelativePath));
            if (File.Exists(targetPath) && IsFileVerified(targetPath, artifact))
            {
                continue;
            }

            await DownloadArtifactAsync(definition, artifact, targetPath, index, cancellationToken)
                .ConfigureAwait(false);
        }

        ReportProgress(definition, "模型安装完成并通过校验", 1);
    }

    private async Task InstallArchiveAsync(
        LocalModelDefinition definition,
        CancellationToken cancellationToken)
    {
        var archive = definition.Archive
            ?? throw new InvalidOperationException($"模型 {definition.Name} 缺少归档来源。");
        ValidateExpectedSize(definition, archive.ExpectedSize);
        var installToken = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(_rootDirectory);
        var archivePath = GetArchiveDownloadPath(definition.Id);
        var stagingDirectory = Path.Combine(_rootDirectory, $".{definition.Id}-{installToken}.staging");
        var backupDirectory = Path.Combine(_rootDirectory, $".{definition.Id}-{installToken}.backup");
        var modelDirectory = GetModelDirectory(definition.Id);
        try
        {
            await DownloadVerifiedAsync(
                definition,
                archive.Url,
                archive.MirrorUrl,
                archive.ExpectedSize,
                archive.Sha256,
                archivePath,
                "模型归档",
                cancellationToken).ConfigureAwait(false);
            ReportProgress(definition, "正在安全解压模型归档…", 0.9);
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                await ExtractTarBzip2Async(
                    archivePath,
                    stagingDirectory,
                    archive.ExpectedRootDirectory,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidFormatException exception)
            {
                throw new InvalidDataException("模型归档格式无效或已损坏。", exception);
            }
            ReportProgress(definition, "正在校验解压后的关键工件…", 0.96);
            await VerifyDirectoryArtifactsAsync(stagingDirectory, definition.Artifacts, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(modelDirectory))
            {
                Directory.Move(modelDirectory, backupDirectory);
            }

            try
            {
                Directory.Move(stagingDirectory, modelDirectory);
            }
            catch
            {
                if (Directory.Exists(backupDirectory) && !Directory.Exists(modelDirectory))
                {
                    Directory.Move(backupDirectory, modelDirectory);
                }

                throw;
            }

            TryDeleteDirectory(backupDirectory);
            ResetPartialDownload(archivePath, archivePath + ".resume.json");
            ReportProgress(definition, "模型安装完成并通过校验", 1);
        }
        catch (InvalidDataException)
        {
            ResetPartialDownload(archivePath, archivePath + ".resume.json");
            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
            if (Directory.Exists(backupDirectory) && !Directory.Exists(modelDirectory))
            {
                Directory.Move(backupDirectory, modelDirectory);
            }
            else
            {
                TryDeleteDirectory(backupDirectory);
            }
        }
    }

    private async Task DownloadArtifactAsync(
        LocalModelDefinition definition,
        LocalModelArtifact artifact,
        string targetPath,
        int artifactIndex,
        CancellationToken cancellationToken)
    {
        ValidateExpectedSize(definition, artifact.ExpectedSize);
        var temporaryPath = targetPath + ".download";
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await DownloadVerifiedAsync(
            definition,
            artifact.PrimaryUrl,
            artifact.MirrorUrl,
            artifact.ExpectedSize,
            artifact.Sha256,
            temporaryPath,
            $"模型工件 {artifactIndex + 1}/{definition.Artifacts.Count}",
            cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, targetPath, overwrite: true);
        ReportProgress(
            definition,
            $"模型工件 {artifactIndex + 1}/{definition.Artifacts.Count} 下载完成",
            Math.Min(0.99, (artifactIndex + 1d) / definition.Artifacts.Count));
    }

    private async Task DownloadVerifiedAsync(
        LocalModelDefinition definition,
        string primaryUrl,
        string? mirrorUrl,
        long expectedSize,
        string sha256,
        string temporaryPath,
        string label,
        CancellationToken cancellationToken)
    {
        ValidateExpectedSize(definition, expectedSize);
        ReportProgress(definition, $"正在准备下载{label}…", 0);
        var failure = await TryDownloadFromUrlAsync(
            definition,
            primaryUrl,
            expectedSize,
            sha256,
            temporaryPath,
            label,
            cancellationToken).ConfigureAwait(false);
        if (failure is not null
            && !File.Exists(temporaryPath + ".resume.json")
            && !string.IsNullOrWhiteSpace(mirrorUrl))
        {
            ReportProgress(definition, "主下载源不可用，正在尝试备用源…", 0);
            failure = await TryDownloadFromUrlAsync(
                definition,
                mirrorUrl,
                expectedSize,
                sha256,
                temporaryPath,
                label,
                cancellationToken).ConfigureAwait(false);
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private async Task<Exception?> TryDownloadFromUrlAsync(
        LocalModelDefinition definition,
        string url,
        long expectedSize,
        string sha256,
        string temporaryPath,
        string label,
        CancellationToken cancellationToken)
    {
        var resumePath = temporaryPath + ".resume.json";
        var validatedUrl = ValidateDownloadUrl(url).AbsoluteUri;
        try
        {
            var offset = 0L;
            string? resumeETag = null;
            if (File.Exists(temporaryPath))
            {
                var existingLength = new FileInfo(temporaryPath).Length;
                if (existingLength == expectedSize)
                {
                    await VerifyFileAsync(temporaryPath, expectedSize, sha256, cancellationToken)
                        .ConfigureAwait(false);
                    TryDeleteFile(resumePath);
                    return null;
                }

                var metadata = await TryReadResumeMetadataAsync(resumePath, cancellationToken)
                    .ConfigureAwait(false);
                if (existingLength > 0
                    && existingLength < expectedSize
                    && metadata is { ETag.Length: > 0 }
                    && string.Equals(metadata.Sha256, sha256, StringComparison.Ordinal)
                    && string.Equals(metadata.SourceUrl, validatedUrl, StringComparison.Ordinal)
                    && metadata.ETag is { } etag)
                {
                    offset = existingLength;
                    resumeETag = etag;
                }
                else
                {
                    ResetPartialDownload(temporaryPath, resumePath);
                }
            }

            while (true)
            {
                using var response = await SendFollowingSafeRedirectsAsync(
                    url,
                    offset > 0 ? offset : null,
                    resumeETag,
                    cancellationToken).ConfigureAwait(false);
                if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    ResetPartialDownload(temporaryPath, resumePath);
                    offset = 0;
                    resumeETag = null;
                    continue;
                }

                response.EnsureSuccessStatusCode();
                ValidateDownloadResponse(response, offset, expectedSize, label);
                var responseETag = GetStrongETag(response);
                if (offset > 0
                    && !string.Equals(responseETag, resumeETag, StringComparison.Ordinal))
                {
                    ResetPartialDownload(temporaryPath, resumePath);
                    offset = 0;
                    resumeETag = null;
                    continue;
                }

                if (offset == 0)
                {
                    if (responseETag is null)
                    {
                        TryDeleteFile(resumePath);
                    }
                    else
                    {
                        await WriteResumeMetadataAsync(
                            resumePath,
                            new DownloadResumeMetadata(responseETag, sha256, validatedUrl),
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                await CopyDownloadResponseAsync(
                    definition,
                    response,
                    temporaryPath,
                    expectedSize,
                    offset,
                    label,
                    cancellationToken).ConfigureAwait(false);
                await VerifyFileAsync(temporaryPath, expectedSize, sha256, cancellationToken)
                    .ConfigureAwait(false);
                TryDeleteFile(resumePath);
                return null;
            }
        }
        catch (InvalidDataException exception)
        {
            ResetPartialDownload(temporaryPath, resumePath);
            return exception;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or TaskCanceledException or TimeoutException
            && !cancellationToken.IsCancellationRequested)
        {
            return exception;
        }
    }

    private static void ValidateDownloadResponse(
        HttpResponseMessage response,
        long offset,
        long expectedSize,
        string label)
    {
        var expectedContentLength = expectedSize - offset;
        if (response.Content.Headers.ContentLength is { } contentLength
            && contentLength != expectedContentLength)
        {
            throw new InvalidDataException($"{label} Content-Length 与声明大小不一致。");
        }

        if (offset == 0)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidDataException($"{label}完整下载返回了意外状态码。");
            }

            return;
        }

        var range = response.Content.Headers.ContentRange;
        if (range?.From != offset
            || range.To != expectedSize - 1
            || range.Length != expectedSize)
        {
            throw new InvalidDataException($"{label} Content-Range 与断点位置不一致。");
        }
    }

    private static string? GetStrongETag(HttpResponseMessage response)
    {
        var etag = response.Headers.ETag;
        return etag is null || etag.IsWeak ? null : etag.ToString();
    }

    private async Task CopyDownloadResponseAsync(
        LocalModelDefinition definition,
        HttpResponseMessage response,
        string temporaryPath,
        long expectedSize,
        long offset,
        string label,
        CancellationToken cancellationToken)
    {
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var output = new FileStream(
            temporaryPath,
            offset > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            useAsync: true);
        var buffer = new byte[1024 * 1024];
        var copied = offset;
        while (true)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(_downloadReadTimeout);
            int read;
            try
            {
                read = await source.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (
                !cancellationToken.IsCancellationRequested
                && readTimeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"{label}下载连续 {_downloadReadTimeout.TotalSeconds:0.#} 秒没有收到数据。",
                    exception);
            }

            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            var maximum = definition.AllowsLargeArtifacts
                ? MaxReviewedArtifactBytes
                : MaxArtifactBytes;
            if (copied > expectedSize || copied > maximum)
            {
                throw new InvalidDataException($"{label}大小超出预期。");
            }

            ReportProgress(
                definition,
                $"正在下载{label}（{copied / 1024 / 1024} MB）…",
                Math.Min(0.89, 0.89 * copied / expectedSize));
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DownloadResumeMetadata?> TryReadResumeMetadataAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<DownloadResumeMetadata>(json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            TryDeleteFile(path);
            return null;
        }
    }

    private static async Task WriteResumeMetadataAsync(
        string path,
        DownloadResumeMetadata metadata,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(metadata);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void ResetPartialDownload(string temporaryPath, string resumePath)
    {
        TryDeleteFile(temporaryPath);
        TryDeleteFile(resumePath);
    }

    private sealed record DownloadResumeMetadata(string ETag, string Sha256, string SourceUrl);

    private async Task<HttpResponseMessage> SendFollowingSafeRedirectsAsync(
        string url,
        long? rangeOffset,
        string? ifRangeETag,
        CancellationToken cancellationToken)
    {
        var current = ValidateDownloadUrl(url);
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (rangeOffset is not null)
            {
                request.Headers.Range = new RangeHeaderValue(rangeOffset, null);
                request.Headers.IfRange = new RangeConditionHeaderValue(
                    EntityTagHeaderValue.Parse(ifRangeETag
                        ?? throw new InvalidOperationException("断点续传缺少 ETag。")));
            }

            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw new InvalidDataException("模型下载重定向缺少 Location。");
            }

            current = ValidateDownloadUrl(
                (location.IsAbsoluteUri ? location : new Uri(current, location)).AbsoluteUri);
        }

        throw new InvalidDataException("模型下载重定向次数过多。");
    }

    private static async Task ExtractTarBzip2Async(
        string archivePath,
        string stagingDirectory,
        string expectedRootDirectory,
        CancellationToken cancellationToken)
    {
        var expectedRoot = ValidateSafeRelativePath(expectedRootDirectory)
            .Replace(Path.DirectorySeparatorChar, '/');
        await using var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            useAsync: true);
        await using var bzip2 = await BZip2Stream.CreateAsync(
            archiveStream,
            CompressionMode.Decompress,
            decompressConcatenated: true,
            leaveOpen: false,
            tolerateTruncatedStream: false,
            cancellationToken).ConfigureAwait(false);
        await using var reader = new TarReader(bzip2, leaveOpen: false);
        long expandedBytes = 0;
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false)
               is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryName = entry.Name.Replace('\\', '/').TrimEnd('/');
            if (entryName.Equals(expectedRoot, StringComparison.Ordinal))
            {
                if (entry.EntryType != TarEntryType.Directory)
                {
                    throw new InvalidDataException("模型归档根条目类型无效。");
                }

                continue;
            }

            var prefix = expectedRoot + "/";
            if (!entryName.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("模型归档包含预期根目录之外的条目。");
            }

            var relativePath = entryName[prefix.Length..];
            var safeRelativePath = ValidateSafeRelativePath(relativePath);
            var targetPath = Path.Combine(stagingDirectory, safeRelativePath);
            EnsurePathIsUnderRoot(targetPath, stagingDirectory);
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                throw new InvalidDataException($"模型归档包含不允许的条目类型：{entry.EntryType}。");
            }

            if (entry.Length < 0 || expandedBytes + entry.Length > MaxArchiveExpandedBytes)
            {
                throw new InvalidDataException("模型归档解压大小超出安全上限。");
            }

            expandedBytes += entry.Length;
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var output = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                useAsync: true);
            var source = entry.DataStream
                ?? throw new InvalidDataException("模型归档文件条目缺少数据流。");
            await source.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (output.Length != entry.Length)
            {
                throw new InvalidDataException("模型归档文件长度与条目声明不一致。");
            }
        }
    }

    private async Task VerifyDirectoryArtifactsAsync(
        string directory,
        IReadOnlyList<LocalModelArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        foreach (var artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, ValidateSafeRelativePath(artifact.RelativePath));
            EnsurePathIsUnderRoot(path, directory);
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"模型归档缺少关键工件：{artifact.RelativePath}");
            }

            await VerifyFileAsync(path, artifact.ExpectedSize, artifact.Sha256, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task VerifyFileAsync(
        string path,
        long expectedSize,
        string sha256,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != expectedSize)
        {
            throw new InvalidDataException("模型工件大小不正确。");
        }

        if (TryGetCachedVerification(path, expectedSize, sha256, out var verified) && verified)
        {
            return;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            useAsync: true);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false));
        CacheVerification(path, expectedSize, sha256, hash);
        if (!hash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("模型工件 SHA-256 不匹配。");
        }
    }

    private bool IsFileVerified(string path, LocalModelArtifact artifact)
    {
        try
        {
            if (TryGetCachedVerification(
                    path,
                    artifact.ExpectedSize,
                    artifact.Sha256,
                    out var cached))
            {
                return cached;
            }

            if (new FileInfo(path).Length != artifact.ExpectedSize)
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            var verified = hash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase);
            CacheVerification(path, artifact.ExpectedSize, artifact.Sha256, hash);
            return verified;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // 校验结论缓存：同进程内 size + LastWriteTimeUtc 未变即视为已校验，
    // 避免 listLocalModels / AcquireUsage / Prepare 每次全量重哈希数 GB 模型。
    // 键含期望哈希，换模型版本（期望值变化）时自动失效。
    private readonly ConcurrentDictionary<string, (long Length, DateTime LastWriteUtc, string Sha256, bool Verified)>
        _verificationCache = new(StringComparer.OrdinalIgnoreCase);

    private bool TryGetCachedVerification(
        string path,
        long expectedSize,
        string expectedSha256,
        out bool verified)
    {
        verified = false;
        if (!_verificationCache.TryGetValue(path, out var entry)
            || entry.Length != expectedSize
            || !string.Equals(entry.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        DateTime lastWriteUtc;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != expectedSize)
            {
                return false;
            }

            lastWriteUtc = info.LastWriteTimeUtc;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (entry.LastWriteUtc != lastWriteUtc)
        {
            return false;
        }

        verified = entry.Verified;
        return true;
    }

    private void CacheVerification(string path, long expectedSize, string expectedSha256, string actualHash)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return;
            }

            _verificationCache[path] = (
                expectedSize,
                info.LastWriteTimeUtc,
                expectedSha256,
                actualHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string GetModelDirectory(string modelId)
    {
        var modelDirectory = Path.Combine(_rootDirectory, ValidateSafeRelativePath(modelId));
        EnsurePathIsUnderRoot(modelDirectory, _rootDirectory);
        return Path.GetFullPath(modelDirectory);
    }

    private string GetArchiveDownloadPath(string modelId) =>
        Path.Combine(_rootDirectory, $".{ValidateSafeRelativePath(modelId)}.archive.download");

    private static void EnsurePathIsUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var rootPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("模型路径逃逸出安装根目录，已拒绝。");
        }
    }

    private bool RemoveModelDirectory(string modelId)
    {
        var removed = false;
        var modelDirectory = GetModelDirectory(modelId);
        if (Directory.Exists(modelDirectory))
        {
            Directory.Delete(modelDirectory, recursive: true);
            removed = true;
        }

        var archivePath = GetArchiveDownloadPath(modelId);
        removed |= TryDeleteFile(archivePath);
        removed |= TryDeleteFile(archivePath + ".resume.json");
        return removed;
    }

    private LocalModelDefinition RequireDefinition(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return _catalog.FirstOrDefault(item =>
                   string.Equals(item.Id, modelId, StringComparison.Ordinal))
               ?? throw new InvalidOperationException($"未知模型 ID：{modelId}");
    }

    private LocalModelInstallState GetStatusForDefinition(LocalModelDefinition definition) =>
        definition.InstallKind switch
        {
            LocalModelInstallKind.WhisperGgml when
                !string.IsNullOrWhiteSpace(definition.WhisperModelName) =>
                _whisperInstaller.GetInstallState(definition.WhisperModelName),
            LocalModelInstallKind.WhisperGgml => LocalModelInstallState.NotInstalled,
            LocalModelInstallKind.SingleFile
                or LocalModelInstallKind.ManifestFiles
                or LocalModelInstallKind.Archive =>
                GetArtifactStatus(definition),
            _ => LocalModelInstallState.NotInstalled
        };

    private LocalModelInstallState GetArtifactStatusForDefinition(LocalModelDefinition definition) =>
        definition.InstallKind == LocalModelInstallKind.WhisperGgml
            ? _whisperInstaller.GetInstallState(RequireWhisperModelName(definition))
            : GetArtifactStatus(definition);

    private OperationLease EnterOperation()
    {
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            _activeOperations = checked(_activeOperations + 1);
            return new OperationLease(this);
        }
    }

    private void ExitOperation()
    {
        TaskCompletionSource? drained = null;
        lock (_stateSync)
        {
            _activeOperations = Math.Max(0, _activeOperations - 1);
            if (_disposeStarted && _activeOperations == 0)
            {
                drained = _operationsDrained;
            }
        }

        drained?.TrySetResult();
    }

    private static string RequireWhisperModelName(LocalModelDefinition definition) =>
        definition.WhisperModelName
        ?? throw new InvalidOperationException($"模型 {definition.Name} 缺少对应的 Whisper 模型名。");

    private static void ValidateExpectedSize(
        LocalModelDefinition definition,
        long expectedSize)
    {
        var maximum = definition.AllowsLargeArtifacts
            ? MaxReviewedArtifactBytes
            : MaxArtifactBytes;
        if (expectedSize <= 0 || expectedSize > maximum)
        {
            throw new InvalidOperationException("模型工件大小声明超出允许范围。");
        }
    }

    private void EnsureSufficientDiskSpace(LocalModelDefinition definition)
    {
        var required = definition.RequiredFreeSpaceBytes > 0
            ? definition.RequiredFreeSpaceBytes
            : checked(definition.DownloadBytes * 2);
        var available = _getAvailableFreeSpaceBytes(_rootDirectory);
        if (available < required)
        {
            throw new IOException(
                $"安装 {definition.Name} 至少需要 {required / 1024 / 1024} MB 可用空间，" +
                $"当前仅有 {available / 1024 / 1024} MB。");
        }
    }

    private static long GetAvailableFreeSpaceBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("无法确定模型目录所在磁盘。");
        return new DriveInfo(root).AvailableFreeSpace;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently
        or HttpStatusCode.Redirect
        or HttpStatusCode.RedirectMethod
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    private void ThrowIfInUse(string modelId)
    {
        if (_usageCounts.TryGetValue(modelId, out var count) && count > 0)
        {
            throw new InvalidOperationException("模型正在被本地运行时使用，请先停止会话或等待当前任务完成。");
        }
    }

    private void ReleaseUsage(string modelId)
    {
        TaskCompletionSource? drained = null;
        lock (_stateSync)
        {
            if (!_usageCounts.TryGetValue(modelId, out var count) || count <= 1)
            {
                _usageCounts.Remove(modelId);
            }
            else
            {
                _usageCounts[modelId] = count - 1;
            }

            _activeOperations = Math.Max(0, _activeOperations - 1);
            if (_disposeStarted && _activeOperations == 0)
            {
                drained = _operationsDrained;
            }
        }

        drained?.TrySetResult();
    }

    private void OnWhisperModelProgress(object? sender, ModelProgressEventArgs eventArgs)
    {
        var active = _activeWhisperInstall;
        if (active is null)
        {
            return;
        }

        ModelProgress?.Invoke(this, new LocalModelProgressEventArgs(
            active.Id,
            active.Category,
            eventArgs.Status,
            eventArgs.Progress));
    }

    private void ReportProgress(LocalModelDefinition definition, string status, double progress) =>
        ModelProgress?.Invoke(this, new LocalModelProgressEventArgs(
            definition.Id,
            definition.Category,
            status,
            progress));

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VoxLink/1.0");
        return client;
    }

    private sealed class OperationLease(LocalModelManager owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ExitOperation();
            }
        }
    }

    private sealed class LocalModelLease(
        LocalModelManager owner,
        string modelId,
        string modelDirectory) : ILocalModelLease
    {
        private int _disposed;

        public string ModelId { get; } = modelId;

        public string ModelDirectory { get; } = modelDirectory;

        public string ResolvePath(string relativePath)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var path = Path.Combine(ModelDirectory, ValidateSafeRelativePath(relativePath));
            EnsurePathIsUnderRoot(path, ModelDirectory);
            return Path.GetFullPath(path);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ReleaseUsage(ModelId);
            }
        }
    }
}
