using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LLama;
using LLama.Common;
using LLama.Sampling;
using VoxLink.Models;

namespace VoxLink.Services;

/// <summary>
/// 腾讯混元翻译 HY-MT1.5-1.8B（GGUF）本地文本翻译。结构与
/// <see cref="LocalMiniCpmTextService"/> 一致：客户端共享模型权重，
/// 每个请求使用隔离 context，单并发限制推理。它是纯翻译模型，
/// 不提供自由文本生成（润色）能力。
/// </summary>
public sealed class LocalHyMtTextService : ITranslationService, IPreloadableRuntime, IDisposable
{
    internal const int MaxTranslateTokens = 512;

    private readonly LocalHyMtRuntimePool _pool;
    private int _disposed;

    internal LocalHyMtTextService(LocalHyMtRuntimePool pool)
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

        var prompt = BuildPrompt(sourceLanguage, targetLanguage, text.Trim());
        var translated = await _pool.CompleteAsync(
            prompt,
            MaxTranslateTokens,
            cancellationToken).ConfigureAwait(false);
        // 简体中文最终输出统一走 LCMapStringEx 简体归一化（架构不变量，MiniCPM 同款路径）。
        return ChineseTextNormalizer.Normalize(translated, targetLanguage);
    }

    /// <summary>会话启动时后台预载权重并预热（消除首句数秒冷启动）。不阻塞启动。</summary>
    public Task PreloadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _pool.PreloadAsync(cancellationToken);
    }

    /// <summary>
    /// 按官方模板构造提示词：源或目标任一方为中文（ZH&lt;=&gt;XX）时用中文模板，
    /// 目标语言名使用中文名；双方都不涉及中文（XX&lt;=&gt;XX）时用英文模板，
    /// 语言名用英文名。模型内置 DeepSeek 风格 chat 模板，运行时由
    /// ApplyChatTemplate 自动套用，这里只提供用户消息。
    /// </summary>
    internal static string BuildPrompt(
        LanguageOption sourceLanguage,
        LanguageOption targetLanguage,
        string text)
    {
        if (IsChinese(sourceLanguage) || IsChinese(targetLanguage))
        {
            return $"将以下文本翻译为{ChineseLanguageName(targetLanguage)}，"
                + $"注意只需要输出翻译后的结果，不要额外解释：\n\n{text}";
        }

        return $"Translate the following segment into {EnglishLanguageName(targetLanguage)}, "
            + $"without additional explanation.\n\n{text}";
    }

    private static bool IsChinese(LanguageOption language) =>
        language.Code.Equals("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>中文模板下的目标语言中文名（覆盖应用支持的全部目标语言）。</summary>
    private static string ChineseLanguageName(LanguageOption language) =>
        ChineseTargetNames.GetValueOrDefault(language.Code, language.DisplayName);

    private static readonly Dictionary<string, string> ChineseTargetNames = new()
    {
        ["zh"] = "中文",
        ["en"] = "英语",
        ["ja"] = "日语",
        ["ko"] = "韩语",
        ["es"] = "西班牙语",
        ["fr"] = "法语",
        ["de"] = "德语",
        ["it"] = "意大利语",
        ["pt"] = "葡萄牙语",
        ["ru"] = "俄语",
        ["ar"] = "阿拉伯语",
        ["hi"] = "印地语",
        ["th"] = "泰语",
        ["vi"] = "越南语",
        ["id"] = "印尼语",
        ["tr"] = "土耳其语",
        ["pl"] = "波兰语",
        ["nl"] = "荷兰语",
        ["uk"] = "乌克兰语"
    };

    /// <summary>英文模板下的目标语言全名（覆盖应用支持的全部目标语言）。</summary>
    private static string EnglishLanguageName(LanguageOption language) =>
        EnglishTargetNames.GetValueOrDefault(language.Code, language.DisplayName);

    private static readonly Dictionary<string, string> EnglishTargetNames = new()
    {
        ["zh"] = "Chinese",
        ["en"] = "English",
        ["ja"] = "Japanese",
        ["ko"] = "Korean",
        ["es"] = "Spanish",
        ["fr"] = "French",
        ["de"] = "German",
        ["it"] = "Italian",
        ["pt"] = "Portuguese",
        ["ru"] = "Russian",
        ["ar"] = "Arabic",
        ["hi"] = "Hindi",
        ["th"] = "Thai",
        ["vi"] = "Vietnamese",
        ["id"] = "Indonesian",
        ["tr"] = "Turkish",
        ["pl"] = "Polish",
        ["nl"] = "Dutch",
        ["uk"] = "Ukrainian"
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _pool.ReleaseClient();
        }
    }
}

/// <summary>
/// 共享 HY-MT GGUF 运行时：已验证模型租约、懒加载权重、隔离请求 context、
/// 单并发推理；无客户端且无请求时自动卸载权重并释放租约。
/// </summary>
internal sealed partial class LocalHyMtRuntimePool : IDisposable
{
    internal const string HyMtGgufFileName = "HY-MT1.5-1.8B-Q4_K_M.gguf";
    // 翻译提示词 + 512 输出 token 实测 <1k token，2048 足够且每句 context 分配减半。
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

    internal LocalHyMtRuntimePool(ILocalModelManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    internal int ClientCount => Volatile.Read(ref _clients);

    internal LocalHyMtTextService CreateClient() => new(this);

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
                        ?? throw new InvalidOperationException("本地混元翻译模型未加载。");
                    parameters = _modelParameters
                        ?? throw new InvalidOperationException("本地混元翻译模型参数未加载。");
                }

                var executor = new StatelessExecutor(weights, parameters)
                {
                    ApplyTemplate = true
                };
                using var pipeline = new DefaultSamplingPipeline
                {
                    // 官方推荐采样参数：temperature 0.7 / top_k 20 / top_p 0.6 / repetition_penalty 1.05。
                    Temperature = 0.7f,
                    TopP = 0.6f,
                    TopK = 20,
                    RepeatPenalty = 1.05f
                };

                try
                {
                    var inferenceParameters = new InferenceParams
                    {
                        MaxTokens = maxTokens,
                        AntiPrompts = ["<|im_end|>", "<|eot_id|>", "</s>"],
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
                var lease = _manager.AcquireUsage(LocalModelIds.HyMt15Gguf);
                LLamaWeights? loadedWeights = null;
                try
                {
                    var modelPath = lease.ResolvePath(HyMtGgufFileName);
                    if (!File.Exists(modelPath))
                    {
                        throw new InvalidOperationException(
                            "本地混元翻译模型文件缺失，请先在本地模型页完成安装。");
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
    /// 注意：不等待完成——真实请求与预热并发抢 _inferenceGate，谁先到谁先跑。
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
                    await CompleteAsync("你好", 1, CancellationToken.None).ConfigureAwait(false);
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
                "本地混元翻译模型未返回有效结果，请重试或检查模型安装。");
        }

        return text;
    }

    [GeneratedRegex("(?s)<think>.*?</think>\\s*")]
    private static partial Regex ThinkBlockRegex();

    [GeneratedRegex("<｜[^<>|]{0,64}｜>|<\\|[^<>|]{0,64}\\|>")]
    private static partial Regex SpecialTokenRegex();
}
