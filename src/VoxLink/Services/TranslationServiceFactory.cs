using System.Net.Http;
using VoxLink.Models;

namespace VoxLink.Services;

/// <summary>
/// 翻译/文本生成服务工厂。注入 <see cref="ILocalModelManager"/> 后可创建
/// 本地 MiniCPM5 与本地混元 HY-MT1.5-1.8B（GGUF）服务。
/// </summary>
public sealed class TranslationServiceFactory : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILocalModelManager? _localModelManager;
    private readonly object _poolSync = new();
    private LocalMiniCpmRuntimePool? _miniCpmPool;
    private LocalHyMtRuntimePool? _hyMtPool;
    private bool _disposed;

    public TranslationServiceFactory(
        HttpClient httpClient,
        ILocalModelManager? localModelManager = null)
    {
        _httpClient = httpClient;
        _localModelManager = localModelManager;
    }

    /// <summary>旧版宿主外壳使用的同步释放入口。</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ITranslationService Create(AppSettings settings) =>
        settings.TranslationProvider switch
        {
            TranslationProvider.LocalMiniCpm => CreateMiniCpmPool().CreateClient(),
            TranslationProvider.LocalHyMtGguf => CreateHyMtPool().CreateClient(),
            TranslationProvider.GoogleWeb => new FailoverTranslationService(
                new MyMemoryTranslationService(_httpClient),
                new GoogleWebTranslationService(_httpClient)),
            _ => CreateChatService(settings)
                ?? throw new InvalidOperationException(
                    "文本生成需要选择 DashScope、DeepSeek、本地 MiniCPM 或自定义 AI 服务。")
        };

    /// <summary>
    /// 创建文本生成（润色）服务。本地 MiniCPM5 是通用指令模型，支持润色；
    /// 混元翻译是纯翻译模型，不支持指令润色，返回 null 表示不可用
    /// （会话已在空值时安全降级）。
    /// </summary>
    public ITextGenerationService? CreateChatService(AppSettings settings) =>
        settings.TranslationProvider switch
        {
            TranslationProvider.LocalMiniCpm => CreateMiniCpmPool().CreateClient(),
            TranslationProvider.DashScope => new OpenAiTranslationService(
                _httpClient,
                "https://dashscope.aliyuncs.com/compatible-mode/v1",
                settings.OpenAiApiKey,
                string.IsNullOrWhiteSpace(settings.OpenAiModel)
                    || settings.OpenAiModel.Equals("qwen2.5:7b", StringComparison.OrdinalIgnoreCase)
                    ? "qwen-plus"
                    : settings.OpenAiModel,
                settings.OpenAiHeaders),
            TranslationProvider.DeepSeek => new OpenAiTranslationService(
                _httpClient,
                "https://api.deepseek.com",
                settings.OpenAiApiKey,
                string.IsNullOrWhiteSpace(settings.OpenAiModel)
                    || settings.OpenAiModel.Equals("qwen2.5:7b", StringComparison.OrdinalIgnoreCase)
                    ? "deepseek-v4-flash"
                    : settings.OpenAiModel,
                settings.OpenAiHeaders),
            TranslationProvider.OpenAiCompatible or TranslationProvider.Custom => CreateOpenAiCompatible(
                settings,
                settings.OpenAiBaseUrl,
                settings.OpenAiModel),
            _ => null
        };

    /// <summary>
    /// 无活跃客户端时卸载本地 MiniCPM / 混元权重并释放模型租约；仍有客户端时跳过。
    /// 返回 true 表示本次调用完成了至少一次卸载。
    /// </summary>
    public bool UnloadIdleLocalRuntimes()
    {
        lock (_poolSync)
        {
            return (_miniCpmPool?.UnloadIfIdle() ?? false)
                | (_hyMtPool?.UnloadIfIdle() ?? false);
        }
    }

    /// <summary>强制卸载本地运行时（引擎关闭时调用）。</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_poolSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        LocalMiniCpmRuntimePool? miniCpmPool;
        LocalHyMtRuntimePool? hyMtPool;
        lock (_poolSync)
        {
            miniCpmPool = _miniCpmPool;
            _miniCpmPool = null;
            hyMtPool = _hyMtPool;
            _hyMtPool = null;
        }

        miniCpmPool?.Dispose();
        hyMtPool?.Dispose();
        await Task.CompletedTask;
    }

    internal LocalMiniCpmRuntimePool CreateMiniCpmPool()
    {
        var manager = _localModelManager
            ?? throw new InvalidOperationException("本地模型管理器未配置，无法使用本地 MiniCPM。");
        lock (_poolSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _miniCpmPool ??= new LocalMiniCpmRuntimePool(manager);
        }
    }

    internal LocalHyMtRuntimePool CreateHyMtPool()
    {
        var manager = _localModelManager
            ?? throw new InvalidOperationException("本地模型管理器未配置，无法使用本地混元翻译。");
        lock (_poolSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _hyMtPool ??= new LocalHyMtRuntimePool(manager);
        }
    }

    private OpenAiTranslationService CreateOpenAiCompatible(
        AppSettings settings,
        string defaultBaseUrl,
        string defaultModel) => new(
            _httpClient,
            string.IsNullOrWhiteSpace(settings.OpenAiBaseUrl) ? defaultBaseUrl : settings.OpenAiBaseUrl,
            settings.OpenAiApiKey,
            string.IsNullOrWhiteSpace(settings.OpenAiModel) ? defaultModel : settings.OpenAiModel,
            settings.OpenAiHeaders);
}
