using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Tests.Services;

public sealed class AsrRecognizerFactoryTests
{
    [Fact]
    public async Task Create_LocalSenseVoice_UsesNativeRecognizerAndRequiresInstalledModel()
    {
        var manager = new MissingModelManager();
        await using var factory = new AsrRecognizerFactory(
            new HttpClient(),
            new WhisperSpeechRecognizer(),
            new ClientAsrWebSocketFactory(),
            manager);
        var settings = new AppSettings
        {
            AsrProtocol = AsrProtocol.LocalSenseVoice
        };

        await using var recognizer = factory.Create(settings);

        Assert.IsType<LocalSenseVoiceAsrRecognizer>(recognizer);
        Assert.Equal(AsrTransport.Local, recognizer.Capabilities.Transport);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recognizer.PrepareAsync(CancellationToken.None));
        Assert.Contains("尚未安装", error.Message, StringComparison.Ordinal);
        Assert.Equal(LocalModelIds.SenseVoiceSmall, manager.AcquiredModelId);
    }

    [Fact]
    public async Task Create_LocalFireRedAsr2Ctc_UsesNativeRecognizerAndRequiresInstalledModel()
    {
        var manager = new MissingModelManager();
        await using var factory = new AsrRecognizerFactory(
            new HttpClient(),
            new WhisperSpeechRecognizer(),
            new ClientAsrWebSocketFactory(),
            manager);
        var settings = new AppSettings
        {
            AsrProtocol = AsrProtocol.LocalFireRedAsr2Ctc
        };

        await using var recognizer = factory.Create(settings);

        Assert.IsType<LocalFireRedAsr2CtcRecognizer>(recognizer);
        Assert.Equal(AsrTransport.Local, recognizer.Capabilities.Transport);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recognizer.PrepareAsync(CancellationToken.None));
        Assert.Contains("尚未安装", error.Message, StringComparison.Ordinal);
        Assert.Equal(LocalModelIds.FireRedAsr2Ctc, manager.AcquiredModelId);
    }

    private sealed class MissingModelManager : ILocalModelManager
    {
        public event EventHandler<LocalModelProgressEventArgs>? ModelProgress
        {
            add { }
            remove { }
        }

        public string? AcquiredModelId { get; private set; }

        public IReadOnlyList<LocalModelDefinition> List() => LocalModelCatalog.All;

        public LocalModelInstallState GetStatus(string modelId) =>
            LocalModelInstallState.NotInstalled;

        public Task InstallAsync(string modelId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RemoveAsync(string modelId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ILocalModelLease AcquireUsage(string modelId)
        {
            AcquiredModelId = modelId;
            throw new InvalidOperationException("本地模型 SenseVoice-Small 尚未安装或校验失败。");
        }
    }
}
