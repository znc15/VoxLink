using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
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

    internal LocalModelManager(
        string rootDirectory,
        IReadOnlyList<LocalModelDefinition> catalog,
        IWhisperModelInstaller whisperInstaller,
        HttpClient httpClient,
        bool ownsHttpClient = false,
        TimeSpan? downloadReadTimeout = null)
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
                case LocalModelInstallKind.SingleFile when definition.Artifacts.Count > 0:
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
            Path.Combine(modelDirectory, ValidateSafeRelativePath(artifact.RelativePath)) + ".download"));
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
        ValidateExpectedSize(archive.ExpectedSize);
        var installToken = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(_rootDirectory);
        var archivePath = Path.Combine(_rootDirectory, $".{definition.Id}-{installToken}.archive.download");
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
            ReportProgress(definition, "模型安装完成并通过校验", 1);
        }
        finally
        {
            TryDeleteFile(archivePath);
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
        ValidateExpectedSize(artifact.ExpectedSize);
        var temporaryPath = targetPath + ".download";
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        try
        {
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
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
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
        ValidateExpectedSize(expectedSize);
        ReportProgress(definition, $"正在准备下载{label}…", 0);
        var failure = await TryDownloadFromUrlAsync(
            definition,
            primaryUrl,
            expectedSize,
            sha256,
            temporaryPath,
            label,
            cancellationToken).ConfigureAwait(false);
        if (failure is not null && !string.IsNullOrWhiteSpace(mirrorUrl))
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
        try
        {
            using var response = await SendFollowingSafeRedirectsAsync(url, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } declaredLength
                && declaredLength != expectedSize)
            {
                throw new InvalidDataException($"{label} Content-Length 与声明大小不一致。");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var output = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                useAsync: true);
            var buffer = new byte[1024 * 1024];
            long copied = 0;
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
                    throw new TimeoutException($"{label}下载连续 {_downloadReadTimeout.TotalSeconds:0.#} 秒没有收到数据。", exception);
                }
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                if (copied > expectedSize || copied > MaxArtifactBytes)
                {
                    throw new InvalidDataException($"{label}大小超出预期。");
                }

                ReportProgress(
                    definition,
                    $"正在下载{label}（{copied / 1024 / 1024} MB）…",
                    Math.Min(0.89, 0.89 * copied / expectedSize));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Close();
            await VerifyFileAsync(temporaryPath, expectedSize, sha256, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or TaskCanceledException or TimeoutException or InvalidDataException
            && !cancellationToken.IsCancellationRequested)
        {
            TryDeleteFile(temporaryPath);
            return exception;
        }
    }

    private async Task<HttpResponseMessage> SendFollowingSafeRedirectsAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var current = ValidateDownloadUrl(url);
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
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

    private static async Task VerifyDirectoryArtifactsAsync(
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

    private static async Task VerifyFileAsync(
        string path,
        long expectedSize,
        string sha256,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != expectedSize)
        {
            throw new InvalidDataException("模型工件大小不正确。");
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
        if (!hash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("模型工件 SHA-256 不匹配。");
        }
    }

    private static bool IsFileVerified(string path, LocalModelArtifact artifact)
    {
        try
        {
            if (new FileInfo(path).Length != artifact.ExpectedSize)
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            return hash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string GetModelDirectory(string modelId)
    {
        var modelDirectory = Path.Combine(_rootDirectory, ValidateSafeRelativePath(modelId));
        EnsurePathIsUnderRoot(modelDirectory, _rootDirectory);
        return Path.GetFullPath(modelDirectory);
    }

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
        var modelDirectory = GetModelDirectory(modelId);
        if (!Directory.Exists(modelDirectory))
        {
            return false;
        }

        Directory.Delete(modelDirectory, recursive: true);
        return true;
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
            LocalModelInstallKind.SingleFile or LocalModelInstallKind.Archive =>
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

    private static void ValidateExpectedSize(long expectedSize)
    {
        if (expectedSize <= 0 || expectedSize > MaxArtifactBytes)
        {
            throw new InvalidOperationException("模型工件大小声明超出允许范围。");
        }
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
