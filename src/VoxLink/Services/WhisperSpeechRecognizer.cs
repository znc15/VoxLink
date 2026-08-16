using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using VoxLink.Audio;
using VoxLink.Models;
using Whisper.net;
using Whisper.net.Ggml;

namespace VoxLink.Services;

public sealed class WhisperSpeechRecognizer : ISpeechRecognizer
{
    private static readonly HttpClient ModelHttpClient = CreateModelHttpClient();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ModelPreparationGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _modelDirectory;
    private readonly SemaphoreSlim _recognitionGate = new(1, 1);
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WhisperFactory? _factory;
    private string? _loadedModelPath;
    private int _disposeState;

    public event EventHandler<ModelProgressEventArgs>? ModelProgress;

    public WhisperSpeechRecognizer(string? modelDirectory = null)
    {
        _modelDirectory = string.IsNullOrWhiteSpace(modelDirectory)
            ? null
            : Path.GetFullPath(modelDirectory);
    }

    public async Task PrepareAsync(string modelName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        var modelPath = GetModelPath(modelName, _modelDirectory);
        var model = GetModelInfo(modelName);
        await _recognitionGate.WaitAsync(cancellationToken);
        try
        {
            using var modelPreparation = await AcquireModelPreparationAsync(
                modelPath,
                cancellationToken).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            if (_loadedModelPath == modelPath && _factory is not null)
            {
                return;
            }

            if (!await IsModelFileUsableAsync(modelPath, model, cancellationToken))
            {
                DeleteIfPresent(modelPath);
                await DownloadModelAsync(model, modelPath, cancellationToken);
            }

            ModelProgress?.Invoke(this, new ModelProgressEventArgs("正在加载本地语音模型…"));
            _factory?.Dispose();
            try
            {
                _factory = WhisperFactory.FromPath(modelPath);
            }
            catch (WhisperModelLoadException)
            {
                DeleteIfPresent(modelPath);
                await DownloadModelAsync(model, modelPath, cancellationToken);
                _factory = WhisperFactory.FromPath(modelPath);
            }

            _loadedModelPath = modelPath;
            ModelProgress?.Invoke(this, new ModelProgressEventArgs("语音模型已就绪", 1));
        }
        finally
        {
            _recognitionGate.Release();
        }
    }

    public async Task<string> TranscribeAsync(
        AudioUtterance utterance,
        LanguageOption language,
        string modelName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        await PrepareAsync(modelName, cancellationToken);
        await _recognitionGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            var factory = _factory ?? throw new InvalidOperationException("语音模型尚未加载。");
            using var processor = factory.CreateBuilder()
                .WithLanguage(language.Code)
                .Build();
            var text = new StringBuilder();

            await foreach (var segment in processor.ProcessAsync(utterance.Samples, cancellationToken))
            {
                text.Append(segment.Text);
            }

            return text.ToString().Trim();
        }
        finally
        {
            _recognitionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            await _disposeCompletion.Task;
            return;
        }

        try
        {
            await _recognitionGate.WaitAsync();
            try
            {
                _factory?.Dispose();
                _factory = null;
                _loadedModelPath = null;
            }
            finally
            {
                _recognitionGate.Release();
            }

            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }

    internal static async Task<IDisposable> AcquireModelPreparationAsync(
        string modelPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        var normalizedPath = Path.GetFullPath(modelPath);
        var gate = ModelPreparationGates.GetOrAdd(
            normalizedPath,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new GateLease(gate);
    }
    internal static string GetModelPath(string modelName, string? modelDirectory = null)
    {
        var safeName = NormalizeModelName(modelName);
        var root = string.IsNullOrWhiteSpace(modelDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VoxLink",
                "models")
            : Path.GetFullPath(modelDirectory);
        return Path.Combine(root, $"ggml-{safeName}.bin");
    }

    private async Task DownloadModelAsync(
        ModelInfo model,
        string modelPath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(modelPath)
            ?? throw new InvalidOperationException("模型路径无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = modelPath + ".download";

        ModelProgress?.Invoke(this, new ModelProgressEventArgs("首次使用：正在下载本地语音模型…", 0));
        try
        {
            Exception? mirrorFailure = null;
            try
            {
                using var response = await ModelHttpClient.GetAsync(
                    $"https://hf-mirror.com/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-{model.Name}.bin",
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var mirrorStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await CopyModelAsync(mirrorStream, temporaryPath, model.Size, cancellationToken);
                await VerifyModelAsync(temporaryPath, model, cancellationToken);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException or InvalidDataException
                && !cancellationToken.IsCancellationRequested)
            {
                mirrorFailure = exception;
                DeleteIfPresent(temporaryPath);
                ModelProgress?.Invoke(this, new ModelProgressEventArgs("镜像不可用，正在尝试官方模型源…", 0));
                await using var officialStream = await WhisperGgmlDownloader.Default
                    .GetGgmlModelAsync(model.Type, cancellationToken: cancellationToken);
                await CopyModelAsync(officialStream, temporaryPath, model.Size, cancellationToken);
                try
                {
                    await VerifyModelAsync(temporaryPath, model, cancellationToken);
                }
                catch (InvalidDataException officialFailure)
                {
                    throw new InvalidDataException(
                        "语音模型未通过 SHA-256 完整性校验。",
                        new AggregateException(mirrorFailure, officialFailure));
                }
            }

            File.Move(temporaryPath, modelPath, overwrite: true);
            ModelProgress?.Invoke(this, new ModelProgressEventArgs("语音模型下载完成", 1));
        }
        catch
        {
            DeleteIfPresent(temporaryPath);
            throw;
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private async Task CopyModelAsync(
        Stream source,
        string temporaryPath,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            useAsync: true);
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            ModelProgress?.Invoke(this, new ModelProgressEventArgs(
                $"正在下载语音模型 {copied / 1024 / 1024} MB…",
                Math.Min(0.99, (double)copied / expectedBytes)));
        }

        await output.FlushAsync(cancellationToken);
    }

    private static HttpClient CreateModelHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VoxLink/0.1");
        return client;
    }

    private static async Task<bool> IsModelFileUsableAsync(
        string modelPath,
        ModelInfo model,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(modelPath))
        {
            return false;
        }

        try
        {
            await VerifyModelAsync(modelPath, model, cancellationToken);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static async Task VerifyModelAsync(
        string modelPath,
        ModelInfo model,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(modelPath).Length != model.Size)
        {
            throw new InvalidDataException("语音模型大小不正确。");
        }

        await using var stream = new FileStream(
            modelPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var actualHash = Convert.ToHexStringLower(hash);
        if (!actualHash.Equals(model.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("语音模型 SHA-256 不匹配。");
        }
    }

    internal static ModelInfo GetModelInfo(string? modelName) => modelName?.ToLowerInvariant() switch
    {
        "base" => new(
            "base",
            GgmlType.Base,
            147_951_465,
            "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
        "small" => new(
            "small",
            GgmlType.Small,
            487_601_967,
            "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
        "large-v3-turbo" => new(
            "large-v3-turbo",
            GgmlType.LargeV3Turbo,
            1_624_555_275,
            "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69"),
        _ => new(
            "tiny",
            GgmlType.Tiny,
            77_691_713,
            "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21")
    };

    private static string NormalizeModelName(string? modelName) => GetModelInfo(modelName).Name;

    private sealed class GateLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
    internal sealed record ModelInfo(string Name, GgmlType Type, long Size, string Sha256);
}
