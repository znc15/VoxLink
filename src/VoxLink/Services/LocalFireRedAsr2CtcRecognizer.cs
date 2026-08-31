using SherpaOnnx;
using System.Text.RegularExpressions;
using VoxLink.Audio;
using VoxLink.Models;

namespace VoxLink.Services;

/// <summary>
/// 本地 FireRedASR2-CTC 非流式 ASR runtime（Windows x64，CPU）。
/// 模型压缩包由 <see cref="LocalModelManager"/> 按固定 revision 下载并校验到目录，
/// 这里的租约路径指向已校验的 model.int8.onnx 与 tokens.txt；运行时绝不联网。
/// CTC 为单遍前向推理，中英混合与中文方言准确率领先，CPU 延迟低。
/// </summary>
internal sealed class LocalFireRedAsr2CtcRecognizer : IAsrRecognizer
{
    private static readonly Regex ControlMarkerRegex = new(
        @"<\s*/?\s*sli(?:\s+[^>]*)?\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.CultureInvariant);

    private readonly ILocalModelManager _modelManager;
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly object _disposeSync = new();
    private ILocalModelLease? _lease;
    private OfflineRecognizer? _recognizer;
    private Task? _disposeTask;
    private int _disposeState;

    public LocalFireRedAsr2CtcRecognizer(ILocalModelManager modelManager)
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

            EnsurePreparedLocked(cancellationToken);
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    private void EnsurePreparedLocked(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lease = _modelManager.AcquireUsage(LocalModelIds.FireRedAsr2Ctc);
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
                    ModelType = string.Empty,
                    FireRedAsrCtc = new OfflineFireRedAsrCtcModelConfig
                    {
                        Model = modelPath
                    }
                },
                DecodingMethod = "greedy_search",
                MaxActivePaths = 1
            };

            recognizer = new OfflineRecognizer(config);
            cancellationToken.ThrowIfCancellationRequested();
            _lease = lease;
            _recognizer = recognizer;
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
                EnsurePreparedLocked(cancellationToken);
            }

            var recognizer = _recognizer
                ?? throw new InvalidOperationException("FireRedASR2-CTC 识别器未就绪。");
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(utterance.SampleRate, utterance.Samples);
            recognizer.Decode(stream);
            cancellationToken.ThrowIfCancellationRequested();
            var text = SanitizeTranscript(stream.Result.Text);
            return new SpeechRecognitionResult(text);
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    /// <summary>移除 FireRed 解码器偶尔混入转写的控制标记，并统一空白。</summary>
    internal static string SanitizeTranscript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var withoutMarkers = ControlMarkerRegex.Replace(text, " ");
        return WhitespaceRegex.Replace(withoutMarkers, " ").Trim();
    }

    public Task<IAsrStream> StartStreamAsync(
        LanguageOption language,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IAsrStream>(new NotSupportedException(
            "本地 FireRedASR2-CTC 不支持流式识别，请使用分段识别。"));

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
}
