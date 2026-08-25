using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LLama;
using LLama.Common;
using LLama.Sampling;
using VoxLink.Models;

namespace VoxLink.Services;

/// <summary>
/// Local MiniCPM5-1B (GGUF) text generation. Clients share model weights through
/// <see cref="LocalMiniCpmRuntimePool"/> while each request receives an isolated context.
/// </summary>
public sealed class LocalMiniCpmTextService : ITextGenerationService, ITranslationService, IPreloadableRuntime, IDisposable
{
    internal const int MaxTranslateTokens = 384;
    internal const int MaxGenerateTokens = 512;

    private readonly LocalMiniCpmRuntimePool _pool;
    private int _disposed;

    internal LocalMiniCpmTextService(LocalMiniCpmRuntimePool pool)
    {
        _pool = pool;
        _pool.AddClient();
    }

    public async Task<string> TranslateAsync(
        string text,
        LanguageOption sourceLanguage,
        LanguageOption targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (sourceLanguage.Code == targetLanguage.Code)
        {
            return text.Trim();
        }

        return await _pool.CompleteAsync(
            "You are a professional real-time translator for multiplayer game voice chat. " +
            "Translate accurately and naturally, preserving names, numbers, tone, and game terminology. " +
            "When the target is Simplified Chinese, use 简体中文 only; never use 繁體中文. " +
            "Return only the translation without explanations.",
            $"Translate from {sourceLanguage.DisplayName} to {targetLanguage.DisplayName}:\n{text.Trim()}",
            MaxTranslateTokens,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _pool.CompleteAsync(
            "You are a concise assistant for multiplayer game communication. " +
            "Follow the user's requested language and return directly useful text.",
            prompt,
            MaxGenerateTokens,
            cancellationToken);
    }

    /// <summary>会话启动时后台预载权重并预热（消除首句数秒冷启动）。不阻塞启动。</summary>
    public Task PreloadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _pool.PreloadAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _pool.ReleaseClient();
        }
    }
}

/// <summary>
/// Shared MiniCPM runtime: verified model lease, lazily loaded weights, isolated request
/// contexts, and a single inference at a time to bound CPU and memory use.
/// </summary>
internal sealed partial class LocalMiniCpmRuntimePool : IDisposable
{
    internal const string LocalMiniCpmGgufFileName = "MiniCPM5-1B-Q4_K_M.gguf";
    // 提示词 + 384/512 输出 token 实测 <1k token，2048 足够且每句 context 分配减半。
    internal const uint ContextSize = 2048;
    internal const int MaxOutputChars = 4096;

    private readonly ILocalModelManager _manager;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly TaskCompletionSource _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LLamaWeights? _weights;
    private ModelParams? _modelParameters;
    private ILocalModelLease? _lease;
    private int _clients;
    private int _activeOperations;
    private TaskCompletionSource? _operationsDrained;
    private bool _disposeStarted;
    private bool _disposed;
    private int _warmupStarted;
    internal LocalMiniCpmRuntimePool(ILocalModelManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    internal int ClientCount => Volatile.Read(ref _clients);

    internal LocalMiniCpmTextService CreateClient() => new(this);

    public void AddClient()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _clients = checked(_clients + 1);
        }
    }

    public void ReleaseClient()
    {
        lock (_sync)
        {
            _clients = Math.Max(0, _clients - 1);
            UnloadWhenIdleCore();
        }
    }

    /// <summary>Unloads weights and releases the model lease when no client or request is active.</summary>
    public bool UnloadIfIdle()
    {
        lock (_sync)
        {
            return UnloadWhenIdleCore();
        }
    }

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            return string.Empty;
        }

        BeginOperation();
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            EnsureWarmupStarted();
            await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                LLamaWeights weights;
                ModelParams parameters;
                lock (_sync)
                {
                    weights = _weights
                        ?? throw new InvalidOperationException("本地 MiniCPM 模型未加载。");
                    parameters = _modelParameters
                        ?? throw new InvalidOperationException("本地 MiniCPM 模型参数未加载。");
                }

                var executor = new StatelessExecutor(weights, parameters)
                {
                    ApplyTemplate = true,
                    SystemMessage = systemPrompt
                };
                using var pipeline = new DefaultSamplingPipeline
                {
                    Temperature = 0.1f,
                    TopP = 0.95f,
                    TopK = 40,
                    RepeatPenalty = 1.05f,
                    PenaltyCount = 64
                };

                try
                {
                    var inferenceParameters = new InferenceParams
                    {
                        MaxTokens = maxTokens,
                        AntiPrompts = ["<|im_end|>", "<|eot|>", "</s>"],
                        SamplingPipeline = pipeline
                    };
                    var output = new StringBuilder();
                    await foreach (var token in executor.InferAsync(
                                       userPrompt.Trim(),
                                       inferenceParameters,
                                       cancellationToken).ConfigureAwait(false))
                    {
                        output.Append(token);
                        if (output.Length > MaxOutputChars)
                        {
                            break;
                        }
                    }

                    return CleanOutput(output.ToString());
                }
                finally
                {
                    executor.Context.Dispose();
                }
            }
            finally
            {
                _inferenceGate.Release();
            }
        }
        finally
        {
            EndOperation();
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
                ownsDisposal = true;
                drainTask = _activeOperations == 0
                    ? Task.CompletedTask
                    : (_operationsDrained ??= new TaskCompletionSource(
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
                ForceUnloadCore();
            }
            _inferenceGate.Dispose();
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _weights) is not null)
        {
            return;
        }

        await Task.Run(() =>
        {
            lock (_sync)
            {
                if (_weights is not null)
                {
                    return;
                }

                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                var lease = _manager.AcquireUsage(LocalModelIds.MiniCpm51BGguf);
                LLamaWeights? loadedWeights = null;
                try
                {
                    var modelPath = lease.ResolvePath(LocalMiniCpmGgufFileName);
                    if (!File.Exists(modelPath))
                    {
                        throw new InvalidOperationException(
                            "本地 MiniCPM 模型文件缺失，请先在本地模型页完成安装。");
                    }

                    var parameters = new ModelParams(modelPath)
                    {
                        ContextSize = ContextSize,
                        GpuLayerCount = 0,
                        Threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2)),
                        UseMemorymap = true
                    };
                    loadedWeights = LLamaWeights.LoadFromFile(parameters);
                    _weights = loadedWeights;
                    _modelParameters = parameters;
                    _lease = lease;
                    loadedWeights = null;
                }
                catch
                {
                    loadedWeights?.Dispose();
                    lease.Dispose();
                    throw;
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 后台预载权重并触发预热推理；调用方不必等待（首句翻译仍走正常路径，
    /// 谁先就绪谁先服务）。加载失败延迟到真实请求再暴露。
    /// </summary>
    public Task PreloadAsync(CancellationToken cancellationToken)
    {
        BeginOperation();
        // 不把 token 传给 Task.Run：令牌在委托启动前取消会跳过整个委托，
        // finally 的 EndOperation 永不配对 → Dispose 排水永久挂起。取消
        // 语义由 EnsureLoadedAsync 内部的令牌检查承担。
        return Task.Run(async () =>
        {
            try
            {
                await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
                EnsureWarmupStarted();
            }
            catch
            {
                // 后台预载失败延迟到首个真实请求再暴露；任务自身永不抛出，
                // 避免调用方丢弃任务时产生未观察异常。
            }
            finally
            {
                EndOperation();
            }
        });
    }

    /// <summary>
    /// 权重首次加载后做一次 1 token 后台推理，把 JIT / mmap 换页 / 线程池
    /// 预热掉，避免真实首句承受数秒冷启动。失败静默（首句仍走正常路径）。
    /// 不等待完成——真实请求与预热并发抢 _inferenceGate，谁先到谁先跑。
    /// </summary>
    private void EnsureWarmupStarted()
    {
        if (Interlocked.CompareExchange(ref _warmupStarted, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            // BeginOperation 放进 try/catch 之内：pool 已释放时立刻退出，
            // 不留下无人观察的 ObjectDisposedException 任务。
            try
            {
                BeginOperation();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (Volatile.Read(ref _weights) is null)
                {
                    return;
                }

                try
                {
                    await CompleteAsync("warmup", "hi", 1, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // 预热失败不影响正常使用，交给首个真实请求去暴露错误。
                }
            }
            finally
            {
                EndOperation();
            }
        });
    }

    private void BeginOperation()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeOperations = checked(_activeOperations + 1);
        }
    }

    private void EndOperation()
    {
        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            _activeOperations = Math.Max(0, _activeOperations - 1);
            UnloadWhenIdleCore();
            if (_disposeStarted && _activeOperations == 0)
            {
                drained = _operationsDrained;
            }
        }

        drained?.TrySetResult();
    }

    private bool UnloadWhenIdleCore()
    {
        if (_clients > 0 || _activeOperations > 0 || _weights is null)
        {
            return false;
        }

        _weights.Dispose();
        _weights = null;
        _modelParameters = null;
        _lease?.Dispose();
        _lease = null;
        return true;
    }

    private void ForceUnloadCore()
    {
        _weights?.Dispose();
        _weights = null;
        _modelParameters = null;
        _lease?.Dispose();
        _lease = null;
    }

    internal static string CleanOutput(string raw)
    {
        var text = ThinkBlockRegex().Replace(raw ?? string.Empty, string.Empty);
        text = SpecialTokenRegex().Replace(text, string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new InvalidOperationException(
                "本地 MiniCPM 模型未返回有效结果，请重试或检查模型安装。");
        }

        return text;
    }

    [GeneratedRegex("(?s)<think>.*?</think>\\s*")]
    private static partial Regex ThinkBlockRegex();

    [GeneratedRegex("<\\|[^<>|]{0,64}\\|>")]
    private static partial Regex SpecialTokenRegex();
}
