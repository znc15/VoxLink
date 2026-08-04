using VoxLink.Models;

namespace VoxLink.Services;

/// <summary>
/// 文本生成服务契约：在 <see cref="ITranslationService"/> 的翻译能力之上
/// 增加自由生成（译文润色、口语化改写等）。云端 OpenAI 兼容服务与本地
/// MiniCPM5 都实现该接口，TranslationSession 的润色链路只依赖它。
/// </summary>
public interface ITextGenerationService : ITranslationService
{
    Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default);
}
