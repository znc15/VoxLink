using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Tests.Services;

/// <summary>
/// 本地混元翻译（HY-MT1.5-1.8B GGUF）服务的确定性测试：官方提示词模板、
/// 采样参数与输出清洗。不加载真实模型权重、不联网。
/// </summary>
public sealed class LocalHyMtTextServiceTests
{
    [Fact]
    public void BuildPrompt_ChineseInvolved_UsesChineseTemplateWithChineseLanguageName()
    {
        // ZH <=> XX：官方中文模板，语言用中文名。
        var zhToEn = LocalHyMtTextService.BuildPrompt(
            LanguageCatalog.Get("zh"),
            LanguageCatalog.Get("en"),
            "你好，世界！");
        Assert.Equal(
            "将以下文本翻译为英语，注意只需要输出翻译后的结果，不要额外解释：\n\n你好，世界！",
            zhToEn);

        var enToZh = LocalHyMtTextService.BuildPrompt(
            LanguageCatalog.Get("en"),
            LanguageCatalog.Get("zh"),
            "Hello, world!");
        Assert.Equal(
            "将以下文本翻译为中文，注意只需要输出翻译后的结果，不要额外解释：\n\nHello, world!",
            enToZh);
    }

    [Fact]
    public void BuildPrompt_NonChinesePair_UsesEnglishTemplateWithEnglishLanguageName()
    {
        // XX <=> XX（无中文参与）：官方英文模板。
        var prompt = LocalHyMtTextService.BuildPrompt(
            LanguageCatalog.Get("ja"),
            LanguageCatalog.Get("fr"),
            "こんにちは");
        Assert.Equal(
            "Translate the following segment into French, without additional explanation.\n\nこんにちは",
            prompt);
    }

    [Fact]
    public async Task TranslateAsync_EmptyOrSameLanguage_ShortCircuitsWithoutLoadingModel()
    {
        var manager = new RecordingModelManager();
        using var pool = new LocalHyMtRuntimePool(manager);
        using var service = pool.CreateClient();

        Assert.Equal(string.Empty, await service.TranslateAsync(
            "   ",
            LanguageCatalog.Get("zh"),
            LanguageCatalog.Get("en")));
        Assert.Equal("原样返回", await service.TranslateAsync(
            "  原样返回  ",
            LanguageCatalog.Get("zh"),
            LanguageCatalog.Get("zh")));
        Assert.Equal(1, pool.ClientCount);
        Assert.Equal(0, manager.AcquireCount);
    }

    [Fact]
    public void CleanOutput_StripsThinkBlocksAndSpecialTokens()
    {
        Assert.Equal(
            "Hello!",
            LocalHyMtRuntimePool.CleanOutput("<think>reasoning</think>Hello!<|eot|>"));
        Assert.Throws<InvalidOperationException>(() =>
            LocalHyMtRuntimePool.CleanOutput("<think>only</think>"));
    }

    private sealed class RecordingModelManager : ILocalModelManager
    {
        public event EventHandler<LocalModelProgressEventArgs>? ModelProgress
        {
            add { }
            remove { }
        }

        public int AcquireCount { get; private set; }

        public IReadOnlyList<LocalModelDefinition> List() => [];
        public LocalModelInstallState GetStatus(string modelId) => LocalModelInstallState.Installed;
        public Task InstallAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<bool> RemoveAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public ILocalModelLease AcquireUsage(string modelId)
        {
            AcquireCount++;
            throw new InvalidOperationException("本地模型尚未安装或校验失败。");
        }
    }
}
