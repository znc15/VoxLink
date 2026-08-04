using System.Linq;
using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Tests.Services;

public sealed class LocalModelCatalogTests
{
    private static readonly string[] RequiredModelIds =
    [
        LocalModelIds.WhisperTiny,
        LocalModelIds.WhisperBase,
        LocalModelIds.WhisperSmall,
        LocalModelIds.WhisperLargeV3Turbo,
        LocalModelIds.MiniCpm51BGguf,
        LocalModelIds.HyMt1518B,
        LocalModelIds.DotsTts,
        LocalModelIds.MossTranscribeDiarize,
        LocalModelIds.Kokoro82M,
        LocalModelIds.CosyVoice205B,
        LocalModelIds.M2M100418M,
        LocalModelIds.Small100,
        LocalModelIds.SenseVoiceSmall,
        LocalModelIds.Qwen3Tts17B
    ];

    [Fact]
    public void Catalog_ContainsAllRequiredModels()
    {
        var ids = LocalModelCatalog.All.Select(model => model.Id).ToList();
        foreach (var required in RequiredModelIds)
        {
            Assert.Contains(required, ids);
            Assert.True(LocalModelCatalog.TryGet(required, out _), $"缺少目录条目：{required}");
        }

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Catalog_AllParameterSizes_AreAtMostTwoBillion()
    {
        foreach (var model in LocalModelCatalog.All)
        {
            Assert.True(
                model.NumericParameterBillions > 0,
                $"{model.Id} 必须显式声明 NumericParameterBillions。");
            Assert.True(
                model.NumericParameterBillions <= 2.0,
                $"{model.Id} 参数规模 {model.NumericParameterBillions}B 超出 2B 上限。");
            Assert.False(
                string.IsNullOrWhiteSpace(model.Parameters),
                $"{model.Id} 缺少参数规模描述。");
        }
    }

    [Fact]
    public void CatalogOnlyEntries_AreNotInstallable_AndExplainWhy()
    {
        foreach (var model in LocalModelCatalog.All)
        {
            if (model.SupportLevel == LocalModelSupportLevel.CatalogOnly)
            {
                Assert.False(model.IsInstallable, $"{model.Id} 不应可一键部署。");
                Assert.False(
                    string.IsNullOrWhiteSpace(model.UnavailableReason),
                    $"{model.Id} 需要说明不可部署原因。");
                Assert.Empty(model.Artifacts);
                Assert.Null(model.Archive);
            }
        }

        Assert.Contains(
            LocalModelCatalog.All,
            model => model.SupportLevel == LocalModelSupportLevel.CatalogOnly);
    }

    [Fact]
    public void WhisperSmallModels_AreStableInstallable_AndDelegateToExistingInstaller()
    {
        var stableIds = new[]
        {
            LocalModelIds.WhisperTiny,
            LocalModelIds.WhisperBase,
            LocalModelIds.WhisperSmall
        };
        foreach (var id in stableIds)
        {
            var model = Assert.Single(LocalModelCatalog.All, item => item.Id == id);
            Assert.Equal(LocalModelSupportLevel.Stable, model.SupportLevel);
            Assert.Equal(LocalModelCategory.Asr, model.Category);
            Assert.Equal(LocalModelRuntimeKind.WhisperCpp, model.Runtime);
            Assert.Equal(LocalModelInstallKind.WhisperGgml, model.InstallKind);
            Assert.True(model.IsInstallable);
            Assert.Contains(
                model.WhisperModelName,
                new[] { "tiny", "base", "small" });
        }
    }

    [Fact]
    public void WhisperLargeV3Turbo_IsCatalogOnly_UntilInstallerSupportsIt()
    {
        var model = Assert.Single(
            LocalModelCatalog.All,
            item => item.Id == LocalModelIds.WhisperLargeV3Turbo);
        Assert.Equal(LocalModelSupportLevel.CatalogOnly, model.SupportLevel);
        Assert.Null(model.WhisperModelName);
    }

    [Fact]
    public void WhisperDeclaredDownloadBytes_MatchExistingRecognizerMetadata()
    {
        foreach (var model in LocalModelCatalog.All
            .Where(item => item.InstallKind == LocalModelInstallKind.WhisperGgml)
            .Where(item => item.IsInstallable))
        {
            var known = WhisperSpeechRecognizer.GetModelInfo(model.WhisperModelName);
            Assert.Equal(known.Size, model.DownloadBytes);
        }
    }

    [Fact]
    public void MiniCpm5_IsStableInstallable_WithRefinementPurpose()
    {
        var model = Assert.Single(
            LocalModelCatalog.All,
            item => item.Id == LocalModelIds.MiniCpm51BGguf);
        Assert.Equal(LocalModelCategory.Translation, model.Category);
        Assert.Equal(LocalModelRuntimeKind.LlamaCppGguf, model.Runtime);
        Assert.Equal(LocalModelSupportLevel.Stable, model.SupportLevel);
        Assert.True(model.IsInstallable);
        Assert.Equal(LocalModelInstallKind.SingleFile, model.InstallKind);
        Assert.Contains("润色", model.Description, StringComparison.Ordinal);
        Assert.Single(model.Artifacts);
        Assert.Null(model.Archive);
        Assert.Null(model.UnavailableReason);
    }

    [Fact]
    public void RequestedCatalogOnlyModels_ExposeVerifiedLicenseAndRuntimeLimits()
    {
        var dots = Assert.Single(LocalModelCatalog.All, model => model.Id == LocalModelIds.DotsTts);
        Assert.Equal(LocalModelSupportLevel.CatalogOnly, dots.SupportLevel);
        Assert.Equal("Apache-2.0", dots.License);
        Assert.Contains("NVIDIA GPU", dots.Requirements, StringComparison.Ordinal);

        var hyMt = Assert.Single(LocalModelCatalog.All, model => model.Id == LocalModelIds.HyMt1518B);
        Assert.Equal(LocalModelSupportLevel.CatalogOnly, hyMt.SupportLevel);
        Assert.Contains("Tencent HY Community License", hyMt.License, StringComparison.Ordinal);
        Assert.Contains("欧盟", hyMt.License, StringComparison.Ordinal);

        var moss = Assert.Single(
            LocalModelCatalog.All,
            model => model.Id == LocalModelIds.MossTranscribeDiarize);
        Assert.Equal(LocalModelSupportLevel.CatalogOnly, moss.SupportLevel);
        Assert.Contains("CUDA", moss.Requirements, StringComparison.Ordinal);
        Assert.Contains("Python", moss.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_HasCompleteMetadata_ForEveryEntry()
    {
        foreach (var model in LocalModelCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(model.Id));
            Assert.False(string.IsNullOrWhiteSpace(model.Name));
            Assert.False(string.IsNullOrWhiteSpace(model.License));
            Assert.False(string.IsNullOrWhiteSpace(model.Languages));
            Assert.False(string.IsNullOrWhiteSpace(model.Requirements));
            Assert.False(string.IsNullOrWhiteSpace(model.Description));
            Assert.True(
                Uri.TryCreate(model.SourceUrl, UriKind.Absolute, out var sourceUri)
                && sourceUri.Scheme == Uri.UriSchemeHttps,
                $"{model.Id} 的来源页必须为 HTTPS URL。");
        }
    }

    [Fact]
    public void TryGet_IsCaseSensitive_AndRejectsUnknownIds()
    {
        Assert.True(LocalModelCatalog.TryGet(LocalModelIds.WhisperTiny, out var tiny));
        Assert.Equal(LocalModelIds.WhisperTiny, tiny.Id);
        Assert.False(LocalModelCatalog.TryGet("WHISPER-TINY", out _));
        Assert.False(LocalModelCatalog.TryGet("no-such-model", out _));
        Assert.False(LocalModelCatalog.TryGet(null, out _));
    }
}
