using System.Text.Json;
using System.Text.Json.Serialization;
using EngineSettings = VoxLink.Models.AppSettings;
using EngineTranslationProvider = VoxLink.Models.TranslationProvider;
using EngineAsrProvider = VoxLink.Models.AsrProvider;
using EngineAsrProtocol = VoxLink.Models.AsrProtocol;
using EngineSpeakerLabelMode = VoxLink.Models.SpeakerLabelMode;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.Services;
using VoxLink.UI.Core.ViewModels;

namespace VoxLink.Tests.UI;

public sealed class AppControllerTests
{
    private static readonly JsonSerializerOptions EngineJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void ToEngineJson_DeserializesIntoEngineContract()
    {
        var settings = new AppSettings
        {
            MyLanguageCode = "ja",
            OtherLanguageCode = "en",
            SecondaryTargetLanguageCode = "fr",
            CaptureMicrophone = false,
            CaptureSystemAudio = true,
            MicrophoneDeviceId = "capture-1",
            SystemAudioDeviceId = "render-1",
            VoiceOutputDeviceId = "render-2",
            UseAiTranslation = true,
            TranslationBackend = TranslationBackend.DeepSeek,
            TranslationBaseUrl = "https://api.deepseek.com",
            TranslationApiKey = "translation-key",
            TranslationModel = "deepseek-chat",
            TranslationHeaders = new Dictionary<string, string> { ["X-Test"] = "header-value" },
            EnableTranslationRefinement = true,
            TranslationRefinementPrompt = "Keep game terms concise.",
            AsrProvider = AsrProvider.Soniox,
            UseCloudAsr = true,
            AsrProtocol = AsrProtocol.SonioxStreaming,
            AsrBaseUrl = "wss://stt-rt.soniox.com/transcribe-websocket",
            AsrApiKey = "asr-key",
            AsrModel = "stt-rt-v5",
            AsrHeaders = new Dictionary<string, string> { ["X-ASR"] = "asr-header" },
            AllowCloudAudioUpload = true,
            UseRemoteSpeech = true,
            SpeechProtocol = SpeechProtocol.OpenAiCompatible,
            SpeechBaseUrl = "https://speech.example/v1/audio/speech",
            SpeechApiKey = "speech-key",
            SpeechModel = "tts-1",
            SpeechVoice = "alloy",
            SpeechHeaders = new Dictionary<string, string> { ["X-Voice"] = "voice-value" },
            WhisperModel = "base",
            VoiceThreshold = 0.025,
            SilenceDurationMs = 850,
            SmartSentenceSegmentation = false,
            TranscriptionOnly = true,
            SpeakerLabelMode = SpeakerLabelMode.Cloud,
            SpeakerEmbeddingModel = "speaker-model",
            OutboundSpeechContent = OutboundSpeechContent.Original,
            SpeakMyTranslation = false,
            SpeakInboundTranslation = true,
            ShowOverlay = false,
            ShowVrOverlay = true,
            VrOverlayWidthMeters = 2.1,
            VrOverlayDistanceMeters = 2.4,
            VrOverlayVerticalOffsetMeters = -0.45,
            VrChatChatboxEnabled = true,
            VrChatOscAddress = "127.0.0.2",
            VrChatOscPort = 9010,
            VrChatIncludeSourceText = true,
            VrChatMuteSelfEnabled = true,
            VrChatOscListenAddress = "127.0.0.3",
            VrChatOscListenPort = 9012,
            ToggleHotkey = "Ctrl+Shift+Space",
            TranslateHotkey = "Ctrl+Shift+Enter"
        };

        var json = JsonSerializer.Serialize(settings.ToEngineJson(), EngineJsonOptions);
        var engineSettings = JsonSerializer.Deserialize<EngineSettings>(json, EngineJsonOptions);

        Assert.NotNull(engineSettings);
        Assert.Equal("ja", engineSettings.MyLanguageCode);
        Assert.Equal("en", engineSettings.OtherLanguageCode);
        Assert.Equal("fr", engineSettings.SecondaryTargetLanguageCode);
        Assert.False(engineSettings.CaptureMicrophone);
        Assert.True(engineSettings.CaptureSystemAudio);
        Assert.Equal(EngineTranslationProvider.DeepSeek, engineSettings.TranslationProvider);
        Assert.Equal("translation-key", engineSettings.OpenAiApiKey);
        Assert.Equal("header-value", engineSettings.OpenAiHeaders["X-Test"]);
        Assert.True(engineSettings.EnableTranslationRefinement);
        Assert.Equal("Keep game terms concise.", engineSettings.TranslationRefinementPrompt);
        Assert.Equal(EngineAsrProvider.Soniox, engineSettings.AsrProvider);
        Assert.Equal(EngineAsrProtocol.SonioxStreaming, engineSettings.AsrProtocol);
        Assert.Equal("asr-key", engineSettings.AsrApiKey);
        Assert.Equal("asr-header", engineSettings.AsrHeaders["X-ASR"]);
        Assert.True(engineSettings.AllowCloudAudioUpload);
        Assert.True(engineSettings.UseRemoteTextToSpeech);
        Assert.Equal("openai", engineSettings.TextToSpeechProtocol);
        Assert.Equal("speech-key", engineSettings.TextToSpeechApiKey);
        Assert.Equal("voice-value", engineSettings.TextToSpeechHeaders["X-Voice"]);
        Assert.Equal("base", engineSettings.WhisperModel);
        Assert.Equal(0.025, engineSettings.VoiceThreshold, precision: 3);
        Assert.Equal(850, engineSettings.SilenceDurationMs);
        Assert.False(engineSettings.SmartSentenceSegmentation);
        Assert.True(engineSettings.TranscriptionOnly);
        Assert.Equal(EngineSpeakerLabelMode.Cloud, engineSettings.SpeakerLabelMode);
        Assert.Equal("speaker-model", engineSettings.SpeakerEmbeddingModel);
        Assert.Equal(VoxLink.Models.OutboundSpeechContent.Original, engineSettings.OutboundSpeechContent);
        Assert.False(engineSettings.SpeakMyTranslation);
        Assert.True(engineSettings.SpeakInboundTranslation);
        Assert.False(engineSettings.ShowOverlay);
        Assert.True(engineSettings.ShowVrOverlay);
        Assert.Equal(2.1, engineSettings.VrOverlayWidthMeters);
        Assert.Equal(2.4, engineSettings.VrOverlayDistanceMeters);
        Assert.Equal(-0.45, engineSettings.VrOverlayVerticalOffsetMeters);
        Assert.True(engineSettings.VrChatChatboxEnabled);
        Assert.Equal("127.0.0.2", engineSettings.VrChatOscAddress);
        Assert.Equal(9010, engineSettings.VrChatOscPort);
        Assert.True(engineSettings.VrChatIncludeSourceText);
        Assert.True(engineSettings.VrChatMuteSelfEnabled);
        Assert.Equal("127.0.0.3", engineSettings.VrChatOscListenAddress);
        Assert.Equal(9012, engineSettings.VrChatOscListenPort);
    }

    [Fact]
    public void ToEngineJson_DisabledSwitchesForcePublicAndLocal()
    {
        var settings = new AppSettings
        {
            UseAiTranslation = false,
            TranslationBackend = TranslationBackend.DeepSeek,
            UseCloudAsr = false,
            AsrProvider = AsrProvider.Soniox,
            AsrProtocol = AsrProtocol.SonioxStreaming
        };

        var json = JsonSerializer.Serialize(settings.ToEngineJson(), EngineJsonOptions);
        var engineSettings = JsonSerializer.Deserialize<EngineSettings>(json, EngineJsonOptions);

        Assert.NotNull(engineSettings);
        Assert.Equal(EngineTranslationProvider.GoogleWeb, engineSettings.TranslationProvider);
        Assert.Equal(EngineAsrProvider.LocalWhisper, engineSettings.AsrProvider);
        Assert.Equal(EngineAsrProtocol.LocalWhisper, engineSettings.AsrProtocol);
    }

    [Fact]
    public void ToEngineJson_TestModeUsesConfiguredProvidersDespiteDisabledSwitches()
    {
        var settings = new AppSettings
        {
            UseAiTranslation = false,
            TranslationBackend = TranslationBackend.DeepSeek,
            UseCloudAsr = false,
            AsrProvider = AsrProvider.Soniox,
            AsrProtocol = AsrProtocol.SonioxStreaming,
            UseRemoteSpeech = false,
            SpeechApiKey = "speech-key"
        };

        var json = JsonSerializer.Serialize(settings.ToEngineJson(respectSwitches: false), EngineJsonOptions);
        var engineSettings = JsonSerializer.Deserialize<EngineSettings>(json, EngineJsonOptions);

        Assert.NotNull(engineSettings);
        Assert.Equal(EngineTranslationProvider.DeepSeek, engineSettings.TranslationProvider);
        Assert.Equal(EngineAsrProvider.Soniox, engineSettings.AsrProvider);
        Assert.Equal(EngineAsrProtocol.SonioxStreaming, engineSettings.AsrProtocol);
        Assert.True(engineSettings.UseRemoteTextToSpeech);
    }

    [Fact]
    public async Task InitializeAsync_LoadsSettingsAndAppliesBootstrap()
    {
        var gateway = new FakeEngineGateway();
        var repository = new FakeSettingsRepository(new AppSettings { MyLanguageCode = "ko" });
        await using var controller = new AppController(gateway, repository, new InlineSynchronizationContext());

        await controller.InitializeAsync();

        Assert.True(controller.Initialized);
        Assert.True(controller.EngineConnected);
        Assert.Equal("ko", controller.Settings.MyLanguageCode);
        Assert.Equal("软件已就绪", controller.StatusMessage);
        Assert.Collection(controller.MicrophoneDevices,
            device => Assert.Equal("capture-default", device.Id));
        Assert.Collection(controller.RenderDevices,
            device => Assert.Equal("render-default", device.Id));
        Assert.Contains("initialize", gateway.Requests);
    }

    [Fact]
    public async Task RefreshDevices_PreservesSelectedDeviceIds()
    {
        var gateway = new FakeEngineGateway();
        var repository = new FakeSettingsRepository(new AppSettings
        {
            MicrophoneDeviceId = "capture-default",
            SystemAudioDeviceId = "render-default",
            VoiceOutputDeviceId = "render-default"
        });
        await using var controller = new AppController(gateway, repository, new InlineSynchronizationContext());

        await controller.InitializeAsync();
        await controller.RefreshDevicesAsync();

        Assert.Equal("capture-default", controller.Settings.MicrophoneDeviceId);
        Assert.Equal("render-default", controller.Settings.SystemAudioDeviceId);
        Assert.Equal("render-default", controller.Settings.VoiceOutputDeviceId);
        Assert.Single(controller.MicrophoneDevices);
        Assert.Single(controller.RenderDevices);
    }
    [Fact]
    public async Task ShutdownAsync_DuringConnectCompletesAndSavesOnce()
    {
        var gateway = new BlockingConnectGateway();
        var repository = new FakeSettingsRepository(new AppSettings());
        await using var controller = new AppController(gateway, repository, new InlineSynchronizationContext());

        var initialize = controller.InitializeAsync();
        await gateway.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var shutdown = controller.ShutdownAsync();

        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        await initialize.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(controller.Initialized);
        Assert.Equal(1, gateway.CloseCount);
        Assert.Equal(1, repository.SaveCount);
    }

    [Fact]
    public async Task EngineEvents_KeepOverlappingPartialsSeparatedByUtteranceId()
    {
        var gateway = new FakeEngineGateway();
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        gateway.Raise("partialMessage", MessagePayload("first draft", isFinal: false, "utterance-1"));
        gateway.Raise("partialMessage", MessagePayload("second draft", isFinal: false, "utterance-2"));
        gateway.Raise("message", MessagePayload("first final", isFinal: true, "utterance-1"));

        Assert.Collection(
            controller.Messages,
            first =>
            {
                Assert.True(first.IsFinal);
                Assert.Equal("first final", first.SourceText);
                Assert.Equal("utterance-1", first.UtteranceId);
            },
            second =>
            {
                Assert.False(second.IsFinal);
                Assert.Equal("second draft", second.SourceText);
                Assert.Equal("utterance-2", second.UtteranceId);
            });

        gateway.Raise("message", MessagePayload("second final", isFinal: true, "utterance-2"));

        Assert.Equal(2, controller.Messages.Count);
        Assert.All(controller.Messages, message => Assert.True(message.IsFinal));
        Assert.Equal("second final", controller.Messages[1].SourceText);
    }

    [Fact]
    public async Task ValidateSessionSettings_EnforcesSourcesAndCloudConsentButAllowsTranscriptionOnly()
    {
        await using var controller = new AppController(
            new FakeEngineGateway(),
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        controller.Settings.CaptureMicrophone = false;
        controller.Settings.CaptureSystemAudio = false;

        Assert.Contains("至少启用", controller.ValidateSessionSettings(), StringComparison.Ordinal);

        controller.Settings.CaptureSystemAudio = true;
        controller.Settings.TranscriptionOnly = true;
        controller.Settings.TranslationBackend = TranslationBackend.Custom;
        controller.Settings.TranslationBaseUrl = "invalid";
        controller.Settings.UseRemoteSpeech = true;
        controller.Settings.SpeechBaseUrl = "invalid";

        Assert.Null(controller.ValidateSessionSettings());

        controller.Settings.ApplyAsrProviderDefaults(AsrProvider.MiMo);
        controller.Settings.UseCloudAsr = true;
        Assert.Contains("允许上传", controller.ValidateSessionSettings(), StringComparison.Ordinal);

        controller.Settings.AllowCloudAudioUpload = true;
        Assert.Contains("API Key", controller.ValidateSessionSettings(), StringComparison.Ordinal);

        controller.Settings.AsrApiKey = "asr-key";
        Assert.Null(controller.ValidateSessionSettings());

        controller.Settings.AsrProvider = AsrProvider.DashScope;
        Assert.Contains("提供方与协议不匹配", controller.ValidateSessionSettings(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_RejectsUnknownLanguagesButIgnoresDisabledSecondaryTarget()
    {
        await using var controller = new AppController(
            new FakeEngineGateway(),
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());

        controller.Settings.SecondaryTargetLanguageCode = "xx";
        Assert.Contains("第二目标语言", controller.ValidateSettings(), StringComparison.Ordinal);
        Assert.Contains("第二目标语言", controller.ValidateSessionSettings(), StringComparison.Ordinal);

        controller.Settings.TranscriptionOnly = true;
        Assert.Null(controller.ValidateSessionSettings());

        controller.Settings.MyLanguageCode = "xx";
        Assert.Contains("我的语言", controller.ValidateSessionSettings(), StringComparison.Ordinal);

        controller.Settings.MyLanguageCode = "zh";
        controller.Settings.OtherLanguageCode = "xx";
        Assert.Contains("对方语言", controller.ValidateSessionSettings(), StringComparison.Ordinal);

        controller.Settings.OtherLanguageCode = "EN";
        Assert.Null(controller.ValidateSessionSettings());
    }
    [Fact]
    public async Task QuickStartMode_StaysSynchronizedAndAppliesSafeInputPreset()
    {
        await using var controller = new AppController(
            new FakeEngineGateway(),
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());

        controller.ApplyQuickStartMode(QuickStartMode.VrChatVoice);

        Assert.Equal(QuickStartMode.VrChatVoice, controller.Settings.QuickStartMode);
        Assert.True(controller.Settings.SpeakMyTranslation);
        Assert.True(controller.Settings.CaptureMicrophone);
        Assert.False(controller.Settings.CaptureSystemAudio);
        Assert.True(controller.Settings.VrChatChatboxEnabled);

        controller.Settings.SpeakMyTranslation = false;

        Assert.Equal(QuickStartMode.OscText, controller.Settings.QuickStartMode);
        Assert.False(controller.Settings.SpeakMyTranslation);
    }

    [Fact]
    public async Task ValidateSessionSettings_IgnoresUnusedSpeechConfigUntilVoiceModeIsEnabled()
    {
        await using var controller = new AppController(
            new FakeEngineGateway(),
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        controller.Settings.UseRemoteSpeech = true;
        controller.Settings.SpeechBaseUrl = "invalid";

        Assert.Null(controller.ValidateSessionSettings());

        controller.ApplyQuickStartMode(QuickStartMode.VrChatVoice);

        Assert.Contains("语音服务", controller.ValidateSessionSettings(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateVoiceRouteSettings_RequiresRecognizedVirtualPlaybackDevice()
    {
        await using var controller = new AppController(
            new FakeEngineGateway(),
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        controller.RenderDevices.Add(new AudioDeviceInfo("speaker", "桌面扬声器", true));
        controller.RenderDevices.Add(new AudioDeviceInfo("cable", "CABLE Input (VB-Audio Virtual Cable)", false));
        controller.ApplyQuickStartMode(QuickStartMode.VrChatVoice);

        controller.Settings.VoiceOutputDeviceId = "speaker";
        Assert.Contains("虚拟声卡", controller.ValidateVoiceRouteSettings(), StringComparison.Ordinal);

        controller.Settings.VoiceOutputDeviceId = "cable";
        Assert.Null(controller.ValidateVoiceRouteSettings());
        Assert.True(controller.IsVoiceRouteReady);
    }

    [Fact]
    public async Task ValidateTranslationSettings_RequiresConfiguredProviderOnlyWhenAiEnabled()
    {
        await using var controller = new AppController(
            new FakeEngineGateway(),
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        controller.Settings.TranslationBackend = TranslationBackend.DeepSeek;
        controller.Settings.TranslationBaseUrl = "invalid";
        controller.Settings.TranslationModel = string.Empty;

        Assert.Null(controller.ValidateTranslationSettings());

        controller.Settings.UseAiTranslation = true;

        Assert.Contains("翻译服务", controller.ValidateTranslationSettings(), StringComparison.Ordinal);

        controller.Settings.TranslationBaseUrl = "https://api.deepseek.com";
        controller.Settings.TranslationModel = "deepseek-chat";
        controller.Settings.TranslationApiKey = "key";

        Assert.Null(controller.ValidateTranslationSettings());
    }

    [Fact]
    public async Task ValidateTranslationSettingsForTest_IgnoresAiSwitch()
    {
        await using var controller = new AppController(
            new FakeEngineGateway(),
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        controller.Settings.UseAiTranslation = false;
        controller.Settings.TranslationBackend = TranslationBackend.DeepSeek;
        controller.Settings.TranslationBaseUrl = "invalid";

        Assert.Null(controller.ValidateTranslationSettings());
        Assert.Contains("翻译服务", controller.ValidateTranslationSettingsForTest(), StringComparison.Ordinal);

        controller.Settings.TranslationBaseUrl = "https://api.deepseek.com";
        controller.Settings.TranslationModel = "deepseek-chat";
        controller.Settings.TranslationApiKey = "key";
        Assert.Null(controller.ValidateTranslationSettingsForTest());
    }

    [Fact]
    public async Task ValidateAsrSettings_RequiresCloudProviderOnlyWhenCloudEnabled()
    {
        await using var controller = new AppController(
            new FakeEngineGateway(),
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        controller.Settings.ApplyAsrProviderDefaults(AsrProvider.Soniox);

        Assert.Null(controller.ValidateAsrSettings());

        controller.Settings.UseCloudAsr = true;
        Assert.Contains("允许上传", controller.ValidateAsrSettings(), StringComparison.Ordinal);

        controller.Settings.AllowCloudAudioUpload = true;
        controller.Settings.AsrApiKey = "key";
        Assert.Null(controller.ValidateAsrSettings());

        controller.Settings.AsrProvider = AsrProvider.LocalWhisper;
        Assert.Contains("云端语音识别提供方", controller.ValidateAsrSettings(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsrSettingsForTest_IgnoresCloudSwitch()
    {
        await using var controller = new AppController(
            new FakeEngineGateway(),
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        controller.Settings.UseCloudAsr = false;
        controller.Settings.ApplyAsrProviderDefaults(AsrProvider.Soniox);

        Assert.Null(controller.ValidateAsrSettings());
        Assert.Contains("允许上传", controller.ValidateAsrSettingsForTest(), StringComparison.Ordinal);

        controller.Settings.AllowCloudAudioUpload = true;
        controller.Settings.AsrApiKey = "key";
        Assert.Null(controller.ValidateAsrSettingsForTest());
    }

    [Fact]
    public async Task TestResultMessage_DoesNotReplaceSoftwareStatus()
    {
        var gateway = new FakeEngineGateway();
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        await controller.TestVrChatOscAsync();

        Assert.Equal("软件已就绪", controller.StatusMessage);
        Assert.Equal("VRChat OSC 测试消息已发送。", controller.TestResultMessage);
        Assert.Contains("testVrChatOsc", gateway.Requests);
    }

    [Fact]
    public async Task TestVoiceOutputAsync_RejectsSpeakerAndSendsConfiguredCableToEngine()
    {
        var gateway = new FakeEngineGateway();
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        controller.RenderDevices.Add(new AudioDeviceInfo("speaker", "桌面扬声器", true));
        controller.RenderDevices.Add(new AudioDeviceInfo("cable", "Voicemeeter Input", false));
        controller.ApplyQuickStartMode(QuickStartMode.VrChatVoice);

        controller.Settings.VoiceOutputDeviceId = "speaker";
        await controller.TestVoiceOutputAsync();
        Assert.DoesNotContain("testVoiceOutput", gateway.Requests);

        controller.Settings.VoiceOutputDeviceId = "cable";
        await controller.TestVoiceOutputAsync();

        Assert.Contains("testVoiceOutput", gateway.Requests);
        var call = Assert.Single(gateway.Calls, item => item.Method == "testVoiceOutput");
        var engineSettings = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            call.Parameters!["settings"]);
        Assert.Equal("cable", engineSettings["voiceOutputDeviceId"]);
    }

    [Theory]
    [InlineData(ConversationDirection.Outbound, OutboundSpeechContent.Original, "识别原话", "zh")]
    [InlineData(ConversationDirection.Outbound, OutboundSpeechContent.Translation, "translated text", "en")]
    [InlineData(ConversationDirection.Typed, OutboundSpeechContent.Original, "识别原话", "zh")]
    [InlineData(ConversationDirection.Inbound, OutboundSpeechContent.Original, "translated text", "zh")]
    public async Task SpeakAsync_UsesDirectionAndSpeechContentToChooseTextAndLanguage(
        ConversationDirection direction,
        OutboundSpeechContent speechContent,
        string expectedText,
        string expectedLanguage)
    {
        var gateway = new FakeEngineGateway();
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        controller.Settings.MyLanguageCode = "zh";
        controller.Settings.OtherLanguageCode = "en";
        controller.Settings.OutboundSpeechContent = speechContent;
        var message = new ConversationMessage(
            direction,
            "识别原话",
            "translated text",
            DateTimeOffset.UtcNow);

        await controller.SpeakAsync(message);

        var call = Assert.Single(gateway.Calls, item => item.Method == "speak");
        Assert.Equal(expectedText, call.Parameters!["text"]);
        Assert.Equal(expectedLanguage, call.Parameters["languageCode"]);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ExposesUpdateStateAndDismissableBanner()
    {
        var gateway = new FakeEngineGateway();
        var checker = new FakeReleaseChecker(new ReleaseCheckResult(
            ReleaseCheckState.UpdateAvailable,
            new Version(1, 0, 1),
            "https://github.com/znc15/VoxLink/releases/tag/v1.0.1",
            "发现新版本 1.0.1。"));
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext(),
            releaseChecker: checker,
            appVersion: new Version(1, 0, 0));

        await controller.CheckForUpdatesAsync();

        Assert.Equal(new Version(1, 0, 0), controller.AppVersion);
        Assert.True(controller.IsUpdateAvailable);
        Assert.True(controller.UpdateBannerVisible);
        Assert.Equal("发现新版本 1.0.1。", controller.UpdateStatusText);
        Assert.Equal("https://github.com/znc15/VoxLink/releases/tag/v1.0.1", controller.LatestReleaseUrl);
        Assert.Equal(1, checker.CheckCount);

        controller.DismissUpdateBanner();

        Assert.False(controller.UpdateBannerVisible);
        Assert.True(controller.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_UpToDate_HidesBanner()
    {
        var checker = new FakeReleaseChecker(new ReleaseCheckResult(
            ReleaseCheckState.UpToDate,
            null,
            "https://github.com/znc15/VoxLink/releases",
            "已是最新版本。"));
        await using var controller = new AppController(
            new FakeEngineGateway(),
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext(),
            releaseChecker: checker);

        await controller.CheckForUpdatesAsync();

        Assert.False(controller.IsUpdateAvailable);
        Assert.False(controller.UpdateBannerVisible);
        Assert.Equal("已是最新版本。", controller.UpdateStatusText);
    }

    [Fact]
    public async Task ChangingCaptureSources_WhileRunning_RequiresSessionRestart()
    {
        var gateway = new FakeEngineGateway();
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        controller.Settings.CaptureSystemAudio = true;
        Assert.False(controller.NeedsSessionRestart);

        await controller.ToggleSessionAsync();
        Assert.True(controller.IsRunning);

        controller.Settings.CaptureSystemAudio = false;
        Assert.True(controller.NeedsSessionRestart);

        controller.Settings.WhisperModel = "base";
        Assert.True(controller.NeedsSessionRestart);

        await controller.ToggleSessionAsync();
        Assert.False(controller.IsRunning);
        Assert.False(controller.NeedsSessionRestart);
    }

    private static JsonElement MessagePayload(string text, bool isFinal, string utteranceId) =>
        JsonSerializer.SerializeToElement(new
        {
            direction = "inbound",
            sourceText = text,
            translatedText = isFinal ? $"translated {text}" : text,
            secondaryTranslatedText = isFinal ? $"secondary {text}" : string.Empty,
            utteranceId,
            isFinal,
            transcriptionOnly = !isFinal,
            timestamp = DateTimeOffset.UtcNow
        });

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    private sealed class FakeSettingsRepository(AppSettings settings) : ISettingsRepository
    {
        public int SaveCount { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(settings);

        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReleaseChecker(ReleaseCheckResult result) : IReleaseChecker
    {
        public int CheckCount { get; private set; }

        public Task<ReleaseCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            CheckCount++;
            return Task.FromResult(result);
        }
    }


    private sealed class FakeEngineGateway : IEngineGateway
    {
        public event EventHandler<EngineEvent>? EventReceived;

        public bool IsConnected { get; private set; }
        public List<string> Requests { get; } = [];
        public List<(string Method, IReadOnlyDictionary<string, object?>? Parameters)> Calls { get; } = [];
        public void Raise(string name, JsonElement data) =>
            EventReceived?.Invoke(this, new EngineEvent(name, data));

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task<JsonElement?> RequestAsync(
            string method,
            IReadOnlyDictionary<string, object?>? parameters = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(method);
            Calls.Add((
                method,
                parameters is null
                    ? null
                    : new Dictionary<string, object?>(parameters, StringComparer.Ordinal)));
            if (method != "initialize" && method != "getBootstrap")
            {
                return Task.FromResult<JsonElement?>(null);
            }

            var bootstrap = JsonSerializer.SerializeToElement(new
            {
                captureDevices = new[]
                {
                    new { id = "capture-default", name = "默认麦克风", isDefault = true }
                },
                renderDevices = new[]
                {
                    new { id = "render-default", name = "默认扬声器", isDefault = true }
                }
            });
            return Task.FromResult<JsonElement?>(bootstrap);
        }

        public Task CloseAsync()
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingConnectGateway : IEngineGateway
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<EngineEvent>? EventReceived
        {
            add { }
            remove { }
        }

        public bool IsConnected => false;
        public int CloseCount { get; private set; }
        public TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectStarted.TrySetResult();
            await _closed.Task;
            throw new EngineException("引擎正在关闭。");
        }

        public Task<JsonElement?> RequestAsync(
            string method,
            IReadOnlyDictionary<string, object?>? parameters = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("连接完成前不应发送请求。");

        public Task CloseAsync()
        {
            CloseCount++;
            _closed.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
