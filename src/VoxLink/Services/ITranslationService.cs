using VoxLink.Models;

namespace VoxLink.Services;

public interface ITranslationService
{
    Task<string> TranslateAsync(
        string text,
        LanguageOption sourceLanguage,
        LanguageOption targetLanguage,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 本地模型运行时（LLM 权重等）支持的后台预载：会话启动时触发，
/// 权重加载 + 预热推理不阻塞启动，消除首句冷启动。云端服务不实现。
/// </summary>
public interface IPreloadableRuntime
{
    Task PreloadAsync(CancellationToken cancellationToken);
}
