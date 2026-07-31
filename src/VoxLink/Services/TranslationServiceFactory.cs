using System.Net.Http;
using VoxLink.Models;

namespace VoxLink.Services;

public sealed class TranslationServiceFactory(HttpClient httpClient)
{
    public ITranslationService Create(AppSettings settings) =>
        settings.TranslationProvider == TranslationProvider.GoogleWeb
            ? new FailoverTranslationService(
                new MyMemoryTranslationService(httpClient),
                new GoogleWebTranslationService(httpClient))
            : CreateChatService(settings);

    public OpenAiTranslationService CreateChatService(AppSettings settings) =>
        settings.TranslationProvider switch
        {
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
                "文本生成需要选择 DashScope、DeepSeek 或自定义 AI 服务。")
        };

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
