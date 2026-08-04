using System.Net.Http;
using VoxLink.Models;

namespace VoxLink.Services;

/// <summary>
/// 翻译/文本生成服务工厂。注入 <see cref="ILocalModelManager"/> 后可创建
/// 本地 MiniCPM5 服务：<see cref="CreateChatPool"/> 返回共享的引用计数池
/// （权重懒加载、单并发），服务客户端存活期间复用，最后一个客户端释放后卸载。
/// </summary>
public sealed class TranslationServiceFactory(HttpClient httpClient, ILocalModelManager? localModelManager = null) : IDisposable
{
    private readonly object _poolSync = new();
    private LocalMiniCpmRuntimePool? _miniCpmPool;
    private bool _disposed;

    public ITranslationService Create(AppSettings settings) =>
        settings.TranslationProvider switch
        {
            TranslationProvider.LocalMiniCpm => CreateChatPool(settings).CreateClient(),
            TranslationProvider.GoogleWeb => new FailoverTranslationService(
                new MyMemoryTranslationService(httpClient),
                new GoogleWebTranslationService(httpClient)),
            _ => CreateChatService(settings)
        };

    public ITextGenerationService CreateChatService(AppSettings settings) =>
        settings.TranslationProvider switch
        {
            TranslationProvider.LocalMiniCpm => CreateChatPool(settings).CreateClient(),
            TranslationProvider.DashScope => new OpenAiTranslationService(
                httpClient,
                "https://dashscope.aliyuncs.com/compatible-mode/v1",
                settings.OpenAiApiKey,
                string.IsNullOrWhiteSpace(settings.OpenAiModel)
                    || settings.OpenAiModel.Equals("qwen2.5:7b", StringComparison.OrdinalIgnoreCase)
                    ? "qwen-plus"
                    : settings.OpenAiModel,
                settings.OpenAiHeaders),
            TranslationProvider.DeepSeek => new OpenAiTranslationService(
                httpClient,
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
            _ => throw new InvalidOperationException(
                "文本生成需要选择 DashScope、DeepSeek、本地 MiniCPM 或自定义 AI 服务。")
        };

    /// <summary>
    /// 无活跃客户端时卸载本地 MiniCPM 权重并释放模型租约；仍有客户端时跳过。
    /// 返回 true 表示本次调用完成了卸载。
    /// </summary>
    public bool UnloadIdleLocalRuntimes()
    {
        lock (_poolSync)
        {
            return _miniCpmPool?.UnloadIfIdle() ?? false;
        }
    }

    /// <summary>强制卸载本地运行时（引擎关闭时调用）。</summary>
    public void Dispose()
    {
        lock (_poolSync)
        {
            _disposed = true;
            _miniCpmPool?.Dispose();
        }
    }

    internal LocalMiniCpmRuntimePool CreateChatPool(AppSettings settings)
    {
        var manager = localModelManager
            ?? throw new InvalidOperationException("本地模型管理器未配置，无法使用本地 MiniCPM。");
        lock (_poolSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _miniCpmPool ??= new LocalMiniCpmRuntimePool(manager);
        }
    }

    private OpenAiTranslationService CreateOpenAiCompatible(
        AppSettings settings,
        string defaultBaseUrl,
        string defaultModel) => new(
            httpClient,
            string.IsNullOrWhiteSpace(settings.OpenAiBaseUrl) ? defaultBaseUrl : settings.OpenAiBaseUrl,
            settings.OpenAiApiKey,
            string.IsNullOrWhiteSpace(settings.OpenAiModel) ? defaultModel : settings.OpenAiModel,
            settings.OpenAiHeaders);
}
