using System.Net.Http;
using VoxLink.Models;

namespace VoxLink.Services;

/// <summary>
/// 翻译/文本生成服务工厂。注入 <see cref="ILocalModelManager"/> 后可创建
/// 本地 MiniCPM5 服务；注入 <see cref="LocalModelOrchestrator"/> 后可创建
/// 应用托管翻译模型服务（HY-MT / M2M-100 / SMaLL-100）。
/// </summary>
public sealed class TranslationServiceFactory : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILocalModelManager? _localModelManager;
    private readonly LocalModelOrchestrator? _managedOrchestrator;
    private readonly object _poolSync = new();
    private readonly List<ManagedModelHostTranslationService> _managedServices = [];
    private LocalMiniCpmRuntimePool? _miniCpmPool;
    private bool _disposed;

    public TranslationServiceFactory(
        HttpClient httpClient,
        ILocalModelManager? localModelManager = null)
        : this(httpClient, localModelManager, managedOrchestrator: null)
    {
    }

    internal TranslationServiceFactory(
        HttpClient httpClient,
        ILocalModelManager? localModelManager,
        LocalModelOrchestrator? managedOrchestrator)
    {
        _httpClient = httpClient;
        _localModelManager = localModelManager;
        _managedOrchestrator = managedOrchestrator;
    }

    /// <summary>旧版宿主外壳使用的同步释放入口。</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ITranslationService Create(AppSettings settings) =>
        CreateManaged(settings) ?? settings.TranslationProvider switch
        {
            TranslationProvider.LocalMiniCpm => CreateChatPool(settings).CreateClient(),
            TranslationProvider.GoogleWeb => new FailoverTranslationService(
                new MyMemoryTranslationService(_httpClient),
                new GoogleWebTranslationService(_httpClient)),
            _ => CreateChatService(settings)
                ?? throw new InvalidOperationException(
                    "文本生成需要选择 DashScope、DeepSeek、本地 MiniCPM 或自定义 AI 服务。")
        };

    /// <summary>
    /// 创建文本生成（润色）服务。托管翻译模型是纯翻译模型，不支持指令润色，
    /// 返回 null 表示不可用（会话已在空值时安全降级）。
    /// </summary>
    public ITextGenerationService? CreateChatService(AppSettings settings) =>
        CreateManaged(settings) is not null
            ? null
            : settings.TranslationProvider switch
        {
            TranslationProvider.LocalMiniCpm => CreateChatPool(settings).CreateClient(),
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
    public async ValueTask DisposeAsync()
    {
        List<ManagedModelHostTranslationService> managed;
        lock (_poolSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _miniCpmPool?.Dispose();
            managed = [.. _managedServices];
            _managedServices.Clear();
        }

        foreach (var service in managed)
        {
            await service.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal LocalMiniCpmRuntimePool CreateChatPool(AppSettings settings)
    {
        var manager = _localModelManager
            ?? throw new InvalidOperationException("本地模型管理器未配置，无法使用本地 MiniCPM。");
        lock (_poolSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _miniCpmPool ??= new LocalMiniCpmRuntimePool(manager);
        }
    }

    private ITranslationService? CreateManaged(AppSettings settings)
    {
        var modelId = settings.TranslationProvider switch
        {
            TranslationProvider.ManagedHyMt => LocalModelIds.HyMt1518B,
            TranslationProvider.ManagedM2M100 => LocalModelIds.M2M100418M,
            TranslationProvider.ManagedSmall100 => LocalModelIds.Small100,
            _ => null
        };
        if (modelId is null)
        {
            return null;
        }

        var orchestrator = _managedOrchestrator
            ?? throw new InvalidOperationException("托管模型编排器未配置，无法使用托管翻译模型。");
        lock (_poolSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var service = new ManagedModelHostTranslationService(orchestrator, modelId);
            _managedServices.Add(service);
            return service;
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
