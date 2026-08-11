using System.IO;
using System.Linq;
using VoxLink.Audio;
using VoxLink.Models;

namespace VoxLink.Services;

/// <summary>
/// 通过应用托管的私有 WSL2 宿主执行 MOSS-Transcribe-Diarize 识别（T5）。
/// 每次 TranscribeAsync 将音频写入租约模型目录，宿主在 Δistribute 内加载模型并返回文本。
/// 会话与模型在首个请求时建立并在释放前复用；错误映射为固定安全消息。
/// </summary>
public sealed class ManagedModelHostAsrRecognizer : IAsrRecognizer
{
    private readonly LocalModelOrchestrator _orchestrator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LocalModelOrchestrator.ManagedModelHostSession? _session;
    private int _disposed;

    internal ManagedModelHostAsrRecognizer(LocalModelOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        _orchestrator = orchestrator;
    }

    public AsrCapabilities Capabilities { get; } = new(
        AsrTransport.Local,
        SupportsPartialResults: false,
        SupportsCloudSpeakerLabels: false);

    public Task PrepareAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IAsrStream> StartStreamAsync(
        LanguageOption language,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IAsrStream>(new NotSupportedException(
            "本地 MOSS 不支持流式识别，请使用分段识别。"));

    public async Task<SpeechRecognitionResult> TranscribeAsync(
        AudioUtterance utterance,
        LanguageOption language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
            var relativePath = await WriteUtteranceAsync(session, utterance, cancellationToken)
                .ConfigureAwait(false);
            var result = await session.RequestAsync(
                "infer",
                new
                {
                    audioPath = relativePath,
                    language = language.Code
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.ValueKind == System.Text.Json.JsonValueKind.Object
                && result.TryGetProperty("text", out var transcribed)
                && transcribed.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return new SpeechRecognitionResult(transcribed.GetString() ?? string.Empty);
            }

            throw new InvalidOperationException("本地识别模型返回了无效结果。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ManagedModelHostException
                                         or InvalidOperationException
                                         or ObjectDisposedException)
        {
            if (exception is InvalidOperationException fixedError)
            {
                throw;
            }

            throw new InvalidOperationException(
                "本地识别模型推理失败，请检查模型文件与运行时状态。",
                exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = _session;
            _session = null;
            if (session is not null)
            {
                try
                {
                    await session.RequestAsync("unload").ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ManagedModelHostException
                                                  or InvalidOperationException
                                                  or OperationCanceledException)
                {
                }
                finally
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<LocalModelOrchestrator.ManagedModelHostSession> EnsureSessionAsync(
        CancellationToken cancellationToken)
    {
        if (_session is not null)
        {
            return _session;
        }

        var session = await _orchestrator.StartHostAsync(
            LocalModelIds.MossTranscribeDiarize,
            requireInferenceCapability: true,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await session.RequestAsync(
                "load",
                new { modelId = LocalModelIds.MossTranscribeDiarize },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (loaded.ValueKind != System.Text.Json.JsonValueKind.Object
                || !loaded.TryGetProperty("loaded", out var loadedFlag)
                || loadedFlag.ValueKind != System.Text.Json.JsonValueKind.True)
            {
                throw new InvalidOperationException("本地识别模型加载失败。");
            }

            _session = session;
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<string> WriteUtteranceAsync(
        LocalModelOrchestrator.ManagedModelHostSession session,
        AudioUtterance utterance,
        CancellationToken cancellationToken)
    {
        var inputsDirectory = Path.Combine(session.ModelDirectory, "inputs");
        Directory.CreateDirectory(inputsDirectory);
        var relativePath = $"inputs/{Guid.NewGuid():N}.wav";
        var path = Path.Combine(inputsDirectory, $"{Guid.NewGuid():N}.wav");
        var bytes = ToWavBytes(utterance.Samples, utterance.SampleRate);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        return relativePath;
    }

    private static byte[] ToWavBytes(float[] samples, int sampleRate)
    {
        const int bytesPerSample = 2;
        var pcm = new byte[samples.Length * bytesPerSample];
        Buffer.BlockCopy(
            samples.Select(sample => (short)Math.Clamp(sample * 32767.0f, short.MinValue, short.MaxValue)).ToArray(),
            0,
            pcm,
            0,
            pcm.Length);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + pcm.Length);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * bytesPerSample);
            writer.Write((short)bytesPerSample);
            writer.Write((short)(bytesPerSample * 8));
            writer.Write("data"u8);
            writer.Write(pcm.Length);
            writer.Write(pcm);
        }

        return stream.ToArray();
    }
}