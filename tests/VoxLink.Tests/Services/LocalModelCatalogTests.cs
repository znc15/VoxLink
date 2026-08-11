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
    public void Catalog_AllModelsAreInstallable_WithVerifiedDeploymentMetadata()
    {
        Assert.Equal(RequiredModelIds.Length, LocalModelCatalog.All.Count);
        Assert.DoesNotContain(
            LocalModelCatalog.All,
            model => model.SupportLevel == LocalModelSupportLevel.CatalogOnly);

        foreach (var model in LocalModelCatalog.All)
        {
            Assert.True(model.IsInstallable, $"{model.Id} 必须进入软件内安装链路。");
            Assert.Null(model.UnavailableReason);
            if (model.InstallKind == LocalModelInstallKind.WhisperGgml)
            {
                Assert.False(string.IsNullOrWhiteSpace(model.WhisperModelName));
                Assert.True(model.DeclaredDownloadBytes > 0);
                continue;
            }

            Assert.NotEmpty(model.Artifacts);
            Assert.All(model.Artifacts, artifact =>
            {
                Assert.True(artifact.ExpectedSize > 0);
                Assert.Matches("^[0-9a-f]{64}$", artifact.Sha256);
            });
        }
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
    public void WhisperLargeV3Turbo_IsStableAndUsesVerifiedWhisperInstaller()
    {
        var model = Assert.Single(
            LocalModelCatalog.All,
            item => item.Id == LocalModelIds.WhisperLargeV3Turbo);
        Assert.Equal(LocalModelSupportLevel.Stable, model.SupportLevel);
        Assert.Equal(LocalModelRuntimeKind.WhisperCpp, model.Runtime);
        Assert.Equal(LocalModelInstallKind.WhisperGgml, model.InstallKind);
        Assert.Equal("large-v3-turbo", model.WhisperModelName);
        Assert.Equal(1_624_555_275, model.DownloadBytes);

        var metadata = WhisperSpeechRecognizer.GetModelInfo(model.WhisperModelName);
        Assert.Equal(Whisper.net.Ggml.GgmlType.LargeV3Turbo, metadata.Type);
        Assert.Equal("1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69", metadata.Sha256);
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
    public void ManagedModels_ExposeRuntimeLicenseHardwareAndVoiceGates()
    {
        var managed = LocalModelCatalog.All
            .Where(model => model.Runtime is
                LocalModelRuntimeKind.ManagedPython or LocalModelRuntimeKind.ManagedWslCuda)
            .ToArray();
        Assert.Equal(7, managed.Length);
        Assert.All(managed, model =>
        {
            Assert.Equal(LocalModelInstallKind.ManifestFiles, model.InstallKind);
            Assert.NotEmpty(model.Artifacts);
            Assert.True(ManagedRuntimeCatalog.TryGet(model.RuntimeProfileId, out _));
            Assert.True(model.RequiredFreeSpaceBytes > model.DownloadBytes);
            Assert.All(model.Artifacts, artifact =>
            {
                Assert.Contains("/resolve/", artifact.PrimaryUrl, StringComparison.Ordinal);
                Assert.DoesNotContain("/main/", artifact.PrimaryUrl, StringComparison.Ordinal);
                Assert.True(Uri.TryCreate(artifact.PrimaryUrl, UriKind.Absolute, out _));
            });
        });

        var hyMt = Assert.Single(managed, model => model.Id == LocalModelIds.HyMt1518B);
        Assert.Equal(LocalModelSupportLevel.Experimental, hyMt.SupportLevel);
        Assert.False(string.IsNullOrWhiteSpace(hyMt.LicenseAgreementId));
        Assert.Contains("欧盟", hyMt.License, StringComparison.Ordinal);

        var gpuModels = managed
            .Where(model => model.Runtime == LocalModelRuntimeKind.ManagedWslCuda)
            .ToArray();
        Assert.Equal(4, gpuModels.Length);
        Assert.All(gpuModels, model =>
        {
            Assert.Equal(LocalModelSupportLevel.Experimental, model.SupportLevel);
            Assert.Contains("NVIDIA GPU", model.Requirements, StringComparison.Ordinal);
            Assert.True(ManagedRuntimeCatalog.TryGet(model.RuntimeProfileId, out var runtime));
            Assert.True(runtime.RequiresNvidiaGpu);
            Assert.True(runtime.MinimumGpuMemoryBytes > 0);
        });

        Assert.All(
            gpuModels.Where(model => model.Category == LocalModelCategory.Tts),
            model => Assert.True(model.RequiresVoiceProfile));
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
