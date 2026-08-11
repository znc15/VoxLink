using System.IO;
using VoxLink.Models;

namespace VoxLink.Services;

/// <summary>
/// 通过应用托管的私有 WSL2 宿主合成 TTS 音频（T5：dots.tts / Qwen3-TTS）。
/// 首次调用启动宿主并加载模型，之后复用；合成结果写入租约模型目录，返回绝对路径。
/// 错误统一映射为固定安全消息。
/// </summary>
internal sealed class ManagedModelHostTtsSynthesizer : IAsyncDisposable
{
    private readonly LocalModelOrchestrator _orchestrator;
    private readonly ManagedTtsModel _model;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LocalModelOrchestrator.ManagedModelHostSession? _session;
    private int _disposed;

    public ManagedTtsModel Model => _model;

    public ManagedModelHostTtsSynthesizer(
        LocalModelOrchestrator orchestrator,
        ManagedTtsModel model)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        _orchestrator = orchestrator;
        _model = model;
    }

    public async Task<(string WavPath, int SampleRate)> SynthesizeAsync(
        string text,
        LanguageOption language,
        string? referenceAudioPath,
        string? referenceText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
            var result = await session.RequestAsync(
                "infer",
                new
                {
                    text,
                    language = language.Code,
                    referenceAudioPath,
                    referenceText
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.ValueKind == System.Text.Json.JsonValueKind.Object
                && result.TryGetProperty("audioPath", out var audioPathElement)
                && audioPathElement.ValueKind == System.Text.Json.JsonValueKind.String
                && result.TryGetProperty("sampleRate", out var sampleRateElement)
                && sampleRateElement.TryGetInt32(out var sampleRate))
            {
                var relativePath = audioPathElement.GetString()!;
                var fullPath = Path.GetFullPath(Path.Combine(session.ModelDirectory, relativePath));
                var modelRoot = Path.GetFullPath(session.ModelDirectory)
                    + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(modelRoot, StringComparison.Ordinal)
                    || !File.Exists(fullPath))
                {
                    throw new InvalidOperationException("本地语音模型返回了无效的音频结果。");
                }

                return (fullPath, sampleRate);
            }

            throw new InvalidOperationException("本地语音模型返回了无效结果。");
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
                "本地语音模型合成失败，请检查模型文件与运行时状态。",
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

        var modelId = _model == ManagedTtsModel.DotsTts
            ? LocalModelIds.DotsTts
            : LocalModelIds.Qwen3Tts17B;
        var session = await _orchestrator.StartHostAsync(
            modelId,
            requireInferenceCapability: true,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await session.RequestAsync(
                "load",
                new { modelId },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (loaded.ValueKind != System.Text.Json.JsonValueKind.Object
                || !loaded.TryGetProperty("loaded", out var loadedFlag)
                || loadedFlag.ValueKind != System.Text.Json.JsonValueKind.True)
            {
                throw new InvalidOperationException("本地语音模型加载失败。");
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
}