using System.IO;
using SherpaOnnx;
using VoxLink.Models;

namespace VoxLink.Services;

internal sealed record LocalKokoroAudio(float[] Samples, int SampleRate);

/// <summary>
/// Owns the verified Kokoro model lease and the sherpa-onnx runtime. Native
/// generation is serialized; unload requests are deferred until the active call exits.
/// </summary>
internal sealed class LocalKokoroTtsRuntime : IDisposable
{
    internal const int MinimumSpeakerId = 0;
    internal const int MaximumSpeakerId = 102;
    internal const double MinimumSpeed = 0.5;
    internal const double MaximumSpeed = 2.0;

    private readonly ILocalModelManager _manager;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _generationGate = new(1, 1);
    private readonly TaskCompletionSource _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private OfflineTts? _tts;
    private ILocalModelLease? _lease;
    private int _activeGenerations;
    private TaskCompletionSource? _generationsDrained;
    private bool _unloadRequested;
    private bool _disposeStarted;
    private bool _disposed;
    internal LocalKokoroTtsRuntime(ILocalModelManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    internal async Task<LocalKokoroAudio> GenerateAsync(
        string text,
        int speakerId,
        double speed,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (speakerId is < MinimumSpeakerId or > MaximumSpeakerId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(speakerId),
                $"Kokoro speaker 必须在 {MinimumSpeakerId}-{MaximumSpeakerId} 之间。");
        }

        if (double.IsNaN(speed)
            || double.IsInfinity(speed)
            || speed is < MinimumSpeed or > MaximumSpeed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(speed),
                $"Kokoro 语速必须在 {MinimumSpeed:F1}-{MaximumSpeed:F1} 之间。");
        }

        BeginGeneration();
        var gateEntered = false;
        try
        {
            await _generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            return await Task.Run(
                () => GenerateCore(text.Trim(), speakerId, (float)speed, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (gateEntered)
            {
                _generationGate.Release();
            }

            EndGeneration();
        }
    }

    internal bool UnloadWhenIdle()
    {
        lock (_sync)
        {
            _unloadRequested = true;
            return UnloadIfPossibleCore();
        }
    }

    public void Dispose()
    {
        Task drainTask;
        var ownsDisposal = false;
        lock (_sync)
        {
            if (_disposeStarted)
            {
                drainTask = _disposeCompletion.Task;
            }
            else
            {
                _disposeStarted = true;
                _disposed = true;
                _unloadRequested = true;
                ownsDisposal = true;
                drainTask = _activeGenerations == 0
                    ? Task.CompletedTask
                    : (_generationsDrained ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }

        if (!ownsDisposal)
        {
            drainTask.GetAwaiter().GetResult();
            return;
        }

        try
        {
            drainTask.GetAwaiter().GetResult();
            lock (_sync)
            {
                UnloadIfPossibleCore();
            }
            _generationGate.Dispose();
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }

    private LocalKokoroAudio GenerateCore(
        string text,
        int speakerId,
        float speed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tts = EnsureLoaded();
        if (tts.NumSpeakers > 0 && speakerId >= tts.NumSpeakers)
        {
            throw new ArgumentOutOfRangeException(
                nameof(speakerId),
                $"当前 Kokoro 模型仅包含 {tts.NumSpeakers} 个 speaker。");
        }

        var generationConfig = new OfflineTtsGenerationConfig
        {
            Sid = speakerId,
            Speed = speed,
            SilenceScale = 0.2f
        };
        var callback = new OfflineTtsCallbackProgressWithArg(
            (_, _, _, _) => cancellationToken.IsCancellationRequested ? 0 : 1);
        var audio = tts.GenerateWithConfig(text, generationConfig, callback);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (audio.NumSamples <= 0 || audio.SampleRate <= 0)
            {
                throw new InvalidDataException("Kokoro 未生成有效音频，请检查模型安装后重试。");
            }

            return new LocalKokoroAudio(audio.Samples, audio.SampleRate);
        }
        finally
        {
            audio.Dispose();
        }
    }

    private OfflineTts EnsureLoaded()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tts is not null)
            {
                _unloadRequested = false;
                return _tts;
            }
        }

        var lease = _manager.AcquireUsage(LocalModelIds.Kokoro82M);
        OfflineTts? loaded = null;
        try
        {
            var model = RequireFile(lease, "model.int8.onnx");
            var voices = RequireFile(lease, "voices.bin");
            var tokens = RequireFile(lease, "tokens.txt");
            var englishLexicon = RequireFile(lease, "lexicon-us-en.txt");
            var chineseLexicon = RequireFile(lease, "lexicon-zh.txt");
            var dateRules = RequireFile(lease, "date-zh.fst");
            var numberRules = RequireFile(lease, "number-zh.fst");
            var phoneRules = RequireFile(lease, "phone-zh.fst");
            var dataDirectory = RequireDirectory(lease, "espeak-ng-data");
            var dictionaryDirectory = RequireDirectory(lease, "dict");

            var config = new OfflineTtsConfig
            {
                // sherpa-onnx processes all sentences; this only caps each batch to avoid OOM.
                MaxNumSentences = 1,
                RuleFsts = string.Join(',', dateRules, numberRules, phoneRules)
            };
            config.Model.Kokoro.Model = model;
            config.Model.Kokoro.Voices = voices;
            config.Model.Kokoro.Tokens = tokens;
            config.Model.Kokoro.DataDir = dataDirectory;
            config.Model.Kokoro.DictDir = dictionaryDirectory;
            config.Model.Kokoro.Lexicon = string.Join(',', englishLexicon, chineseLexicon);
            config.Model.NumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
            config.Model.Debug = 0;
            config.Model.Provider = "cpu";

            loaded = new OfflineTts(config);
            if (loaded.SampleRate <= 0 || loaded.NumSpeakers <= 0)
            {
                throw new InvalidDataException("Kokoro 模型加载后未报告有效的采样率或 speaker 数量。");
            }

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _tts = loaded;
                _lease = lease;
                _unloadRequested = false;
                loaded = null;
                return _tts;
            }
        }
        catch
        {
            loaded?.Dispose();
            lease.Dispose();
            throw;
        }
    }

    private void BeginGeneration()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeGenerations = checked(_activeGenerations + 1);
        }
    }

    private void EndGeneration()
    {
        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            _activeGenerations = Math.Max(0, _activeGenerations - 1);
            UnloadIfPossibleCore();
            if (_disposeStarted && _activeGenerations == 0)
            {
                drained = _generationsDrained;
            }
        }

        drained?.TrySetResult();
    }

    private bool UnloadIfPossibleCore()
    {
        if (!_unloadRequested || _activeGenerations > 0 || _tts is null)
        {
            return false;
        }

        _tts.Dispose();
        _tts = null;
        _lease?.Dispose();
        _lease = null;
        _unloadRequested = false;
        return true;
    }

    private static string RequireFile(ILocalModelLease lease, string relativePath)
    {
        var path = lease.ResolvePath(relativePath);
        return File.Exists(path)
            ? path
            : throw new InvalidDataException($"Kokoro 模型工件缺失：{relativePath}");
    }

    private static string RequireDirectory(ILocalModelLease lease, string relativePath)
    {
        var path = lease.ResolvePath(relativePath);
        return Directory.Exists(path)
            ? path
            : throw new InvalidDataException($"Kokoro 模型目录缺失：{relativePath}");
    }
}
