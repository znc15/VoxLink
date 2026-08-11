using System.Text.RegularExpressions;
using SherpaOnnx;
using VoxLink.Audio;
using VoxLink.Models;

namespace VoxLink.Services;

/// <summary>
/// 本地 SenseVoice-Small 非流式 ASR runtime（Windows x64，CPU）。
/// 模型压缩包由 <see cref="LocalModelManager"/> 按固定 revision 下载并校验到目录，
/// 这里的租约路径指向已校验的 model.int8.onnx 与 tokens.txt；运行时绝不联网。
/// </summary>
internal sealed partial class LocalSenseVoiceAsrRecognizer : IAsrRecognizer
{
    private readonly ILocalModelManager _modelManager;
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly object _disposeSync = new();
    private ILocalModelLease? _lease;
    private OfflineRecognizer? _recognizer;
    private Task? _disposeTask;
    private int _disposeState;

    public LocalSenseVoiceAsrRecognizer(ILocalModelManager modelManager)
    {
        _modelManager = modelManager ?? throw new ArgumentNullException(nameof(modelManager));
    }

    public AsrCapabilities Capabilities =>
        new(AsrTransport.Local, SupportsPartialResults: false, SupportsCloudSpeakerLabels: false);

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposing();
            if (_recognizer is not null)
            {
                return;
            }

            await EnsurePreparedLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    private Task EnsurePreparedLockedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lease = _modelManager.AcquireUsage(LocalModelIds.SenseVoiceSmall);
        OfflineRecognizer? recognizer = null;
        try
        {
            var modelPath = lease.ResolvePath("model.int8.onnx");
            var tokensPath = lease.ResolvePath("tokens.txt");
            var config = new OfflineRecognizerConfig
            {
                FeatConfig = new FeatureConfig
                {
                    SampleRate = 16000,
                    FeatureDim = 80
                },
                ModelConfig = new OfflineModelConfig
                {
                    Tokens = tokensPath,
                    NumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount)),
                    Debug = 0,
                    Provider = "cpu",
                    ModelType = "sensevoice",
                    SenseVoice = new OfflineSenseVoiceModelConfig
                    {
                        Model = modelPath,
                        Language = "auto",
                        UseInverseTextNormalization = 1
                    }
                },
                DecodingMethod = "greedy_search",
                MaxActivePaths = 1
            };

            recognizer = new OfflineRecognizer(config);
            cancellationToken.ThrowIfCancellationRequested();
            _lease = lease;
            _recognizer = recognizer;
            return Task.CompletedTask;
        }
        catch
        {
            recognizer?.Dispose();
            lease.Dispose();
            throw;
        }
    }

    public async Task<SpeechRecognitionResult> TranscribeAsync(
        AudioUtterance utterance,
        LanguageOption language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposing();
            if (_recognizer is null)
            {
                await EnsurePreparedLockedAsync(cancellationToken).ConfigureAwait(false);
            }

            var recognizer = _recognizer
                ?? throw new InvalidOperationException("SenseVoice 识别器未就绪。");
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(utterance.SampleRate, utterance.Samples);
            recognizer.Decode(stream);
            cancellationToken.ThrowIfCancellationRequested();
            var text = stream.Result.Text ?? string.Empty;
            return new SpeechRecognitionResult(StripSenseVoiceMarkers(text));
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    public Task<IAsrStream> StartStreamAsync(
        LanguageOption language,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IAsrStream>(new NotSupportedException(
            "本地 SenseVoice 不支持流式识别，请使用分段识别。"));

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            if (_disposeTask is null)
            {
                Volatile.Write(ref _disposeState, 1);
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _inferenceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _recognizer?.Dispose();
            _recognizer = null;
            _lease?.Dispose();
            _lease = null;
        }
        finally
        {
            Volatile.Write(ref _disposeState, 2);
            _inferenceGate.Release();
            _inferenceGate.Dispose();
        }
    }

    private void ThrowIfDisposing() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

    /// <summary>
    /// 剥离 SenseVoice 输出的事件/语言/情感标记 token（形如 &lt;|zh|&gt;、&lt;|NEUTRAL|&gt;、
    /// &lt;|Speech|&gt;、&lt;|woitn|&gt;），并规范化空白。
    /// </summary>
    internal static string StripSenseVoiceMarkers(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var stripped = SenseVoiceMarkerRegex().Replace(text, " ");
        return string.Join(" ", stripped.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    [GeneratedRegex(@"<\|[^|]*\|>", RegexOptions.Compiled)]
    private static partial Regex SenseVoiceMarkerRegex();
}