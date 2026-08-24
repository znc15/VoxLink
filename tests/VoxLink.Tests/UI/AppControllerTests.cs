using System.Text.Json;
using System.Text.Json.Serialization;
using EngineSettings = VoxLink.Models.AppSettings;
using EngineTranslationProvider = VoxLink.Models.TranslationProvider;
using EngineAsrProvider = VoxLink.Models.AsrProvider;
using EngineAsrProtocol = VoxLink.Models.AsrProtocol;
using EngineManagedTtsModel = VoxLink.Models.ManagedTtsModel;
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
            TranslateHotkey = "Ctrl+Shift+Enter",
            DesktopOverlayLeft = 120,
            DesktopOverlayTop = 340,
            DesktopOverlayWidth = 900,
            DesktopOverlayHeight = 420,
            DesktopOverlayFontSize = 32,
            DesktopOverlayTopmost = false,
            DesktopOverlayLockPosition = false,
            LocalModelDirectory = @"D:\VoxLinkModels",
            ManagedRuntimeDirectory = @"E:\VoxLinkRuntimes"
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
        Assert.Equal(120, engineSettings.DesktopOverlayLeft);
        Assert.Equal(340, engineSettings.DesktopOverlayTop);
        Assert.Equal(900, engineSettings.DesktopOverlayWidth);
        Assert.Equal(420, engineSettings.DesktopOverlayHeight);
        Assert.Equal(32, engineSettings.DesktopOverlayFontSize);
        Assert.False(engineSettings.DesktopOverlayTopmost);
        Assert.False(engineSettings.DesktopOverlayLockPosition);
        Assert.Equal(@"D:\VoxLinkModels", engineSettings.LocalModelDirectory);
        Assert.Equal(@"E:\VoxLinkRuntimes", engineSettings.ManagedRuntimeDirectory);
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
    public async Task InitializeAsync_PassesCustomModelDirectoriesAsLaunchArguments()
    {
        var gateway = new FakeEngineGateway();
        var repository = new FakeSettingsRepository(new AppSettings
        {
            LocalModelDirectory = @"D:\VoxLinkModels",
            ManagedRuntimeDirectory = @"E:\VoxLinkRuntimes"
        });
        await using var controller = new AppController(
            gateway,
            repository,
            new InlineSynchronizationContext());

        await controller.InitializeAsync();

        Assert.Contains(
            gateway.LaunchArgumentSets,
            set => set.SequenceEqual(
                ["--model-dir", @"D:\VoxLinkModels", "--runtime-dir", @"E:\VoxLinkRuntimes"]));
    }

    [Fact]
    public async Task InitializeAsync_CorruptSettingsShowsErrorWithoutConnectingOrOnboarding()
    {
        var gateway = new FakeEngineGateway();
        await using var controller = new AppController(
            gateway,
            new ThrowingSettingsRepository(new JsonException("settings are corrupt")),
            new InlineSynchronizationContext());
        var onboardingRequested = false;
        controller.OnboardingRequested += (_, _) => onboardingRequested = true;

        await controller.InitializeAsync();

        Assert.True(controller.Initialized);
        Assert.False(controller.EngineConnected);
        Assert.Equal("软件启动失败", controller.StatusMessage);
        Assert.Equal("error", controller.Activity);
        Assert.Equal("settings are corrupt", controller.ErrorMessage);
        Assert.Empty(gateway.Requests);
        Assert.False(onboardingRequested);
    }
    [Fact]
    public async Task SettingsChangeBeforeInitialization_DoesNotOverwritePersistedSettings()
    {
        // 复现“第一次打开应用不记录第二目标语言”：LivePage 的 x:Bind 初始化会在设置加载完成前
        // 触发一次 SelectionChanged → NotifySettingsChanged，而慢速冷启动时读取可能超过防抖窗口。
        var repository = new BlockingSettingsRepository(new AppSettings
        {
            SecondaryTargetLanguageCode = "ja"
        });
        await using var controller = new AppController(
            new FakeEngineGateway(),
            repository,
            new InlineSynchronizationContext());

        controller.NotifySettingsChanged();

        // 加载被阻塞（模拟冷启动慢读），等待超过 650ms 防抖窗口。
        await Task.Delay(TimeSpan.FromMilliseconds(800));
        Assert.Equal(0, repository.SaveCount);

        var initialize = controller.InitializeAsync();
        await repository.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        repository.LoadRelease.TrySetResult();
        await initialize.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("ja", controller.Settings.SecondaryTargetLanguageCode);
        await controller.SaveNowAsync();
        Assert.Equal("ja", repository.LastSaved!.SecondaryTargetLanguageCode);
    }

    [Fact]
    public async Task SettingsChangeAfterInitialization_SavesNormally()
    {
        var repository = new BlockingSettingsRepository(new AppSettings());
        await using var controller = new AppController(
            new FakeEngineGateway(),
            repository,
            new InlineSynchronizationContext());

        var initialize = controller.InitializeAsync();
        await repository.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        repository.LoadRelease.TrySetResult();
        await initialize.WaitAsync(TimeSpan.FromSeconds(15));

        controller.Settings.SecondaryTargetLanguageCode = "fr";
        controller.NotifySettingsChanged();
        await repository.SaveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("fr", repository.LastSaved!.SecondaryTargetLanguageCode);
    }

    [Fact]
    public async Task ShutdownBeforeInitialization_DoesNotPersistDefaults()
    {
        var repository = new BlockingSettingsRepository(new AppSettings
        {
            SecondaryTargetLanguageCode = "ja"
        });
        await using var controller = new AppController(
            new FakeEngineGateway(),
            repository,
            new InlineSynchronizationContext());

        controller.NotifySettingsChanged();
        await controller.ShutdownAsync();

        Assert.Equal(0, repository.SaveCount);
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
        await gateway.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var shutdown = controller.ShutdownAsync();

        await shutdown.WaitAsync(TimeSpan.FromSeconds(15));
        await initialize.WaitAsync(TimeSpan.FromSeconds(15));

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
    public async Task SpeakMyTranslation_ControlsVoiceRouteValidation()
    {
        await using var controller = new AppController(
            new FakeEngineGateway(),
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());

        // 未开启朗读我的译文时，不校验语音路由。
        controller.Settings.SpeakMyTranslation = false;
        Assert.Null(controller.ValidateVoiceRouteSettings());

        // 开启朗读后需要配置虚拟声卡播放端。
        controller.Settings.SpeakMyTranslation = true;
        Assert.NotNull(controller.ValidateVoiceRouteSettings());
        Assert.False(controller.IsVoiceRouteReady);
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

        controller.Settings.SpeakMyTranslation = true;

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
        controller.Settings.SpeakMyTranslation = true;

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
        controller.Settings.SpeakMyTranslation = true;

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

    [Fact]
    public async Task TestSpeechAsync_UsesCurrentlySelectedSpeechService()
    {
        var gateway = new FakeEngineGateway();
        var settings = new AppSettings
        {
            UseRemoteSpeech = false,
            UseLocalKokoroTextToSpeech = false,
            SpeechApiKey = "stale-remote-key"
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(settings),
            new InlineSynchronizationContext());

        await controller.TestSpeechAsync();

        var call = Assert.Single(gateway.Calls, item => item.Method == "testSpeech");
        var engineSettings = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            call.Parameters!["settings"]);
        Assert.Equal(false, engineSettings["useRemoteTextToSpeech"]);
        Assert.Equal(false, engineSettings["useLocalKokoroTextToSpeech"]);
    }

    [Fact]
    public async Task PrepareWhisperModelAsync_DoesNotChangeSelectedCloudAsr()
    {
        var gateway = new FakeEngineGateway();
        var settings = new AppSettings
        {
            UseCloudAsr = true,
            AsrProvider = AsrProvider.Soniox,
            AsrProtocol = AsrProtocol.SonioxStreaming,
            AsrBaseUrl = "wss://stt-rt.soniox.com/transcribe-websocket",
            AsrApiKey = "key",
            AsrModel = "stt-rt-v5",
            AllowCloudAudioUpload = true,
            WhisperModel = "base"
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(settings),
            new InlineSynchronizationContext());

        await controller.InitializeAsync();
        await controller.PrepareWhisperModelAsync();

        var call = Assert.Single(gateway.Calls, item => item.Method == "prepareModel");
        var engineSettings = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            call.Parameters!["settings"]);
        Assert.Equal("localWhisper", engineSettings["asrProvider"]);
        Assert.Equal("localWhisper", engineSettings["asrProtocol"]);
        Assert.Equal(false, engineSettings["allowCloudAudioUpload"]);
        var restoreCall = gateway.Calls.Last(item => item.Method == "configure");
        var restoredSettings = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            restoreCall.Parameters!["settings"]);
        Assert.Equal("soniox", restoredSettings["asrProvider"]);
        Assert.Equal("sonioxStreaming", restoredSettings["asrProtocol"]);
        Assert.Equal(true, restoredSettings["allowCloudAudioUpload"]);
        Assert.True(settings.UseCloudAsr);
        Assert.Equal(AsrProvider.Soniox, settings.AsrProvider);
        Assert.Equal(AsrProtocol.SonioxStreaming, settings.AsrProtocol);
        Assert.True(settings.AllowCloudAudioUpload);
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
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.WhisperTiny, "asr", "installed"))
        };
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

    [Fact]
    public void ToEngineJson_MapsLocalMiniCpmAndLocalKokoroIntoEngineSettings()
    {
        var settings = new AppSettings
        {
            UseAiTranslation = true,
            TranslationBackend = TranslationBackend.LocalMiniCpm,
            UseRemoteSpeech = false,
            UseLocalKokoroTextToSpeech = true,
            KokoroSpeakerId = 42,
            KokoroSpeed = 1.25
        };

        var json = JsonSerializer.Serialize(settings.ToEngineJson(), EngineJsonOptions);
        var engineSettings = JsonSerializer.Deserialize<EngineSettings>(json, EngineJsonOptions);

        Assert.NotNull(engineSettings);
        Assert.Equal(EngineTranslationProvider.LocalMiniCpm, engineSettings.TranslationProvider);
        Assert.True(engineSettings.UseLocalKokoroTextToSpeech);
        Assert.Equal(42, engineSettings.KokoroSpeakerId);
        Assert.Equal(1.25, engineSettings.KokoroSpeed, precision: 3);
        Assert.False(engineSettings.UseRemoteTextToSpeech);

        // 关闭 AI 翻译开关后仍然回退到公共免费翻译。
        settings.UseAiTranslation = false;
        json = JsonSerializer.Serialize(settings.ToEngineJson(), EngineJsonOptions);
        engineSettings = JsonSerializer.Deserialize<EngineSettings>(json, EngineJsonOptions);

        Assert.NotNull(engineSettings);
        Assert.Equal(EngineTranslationProvider.GoogleWeb, engineSettings.TranslationProvider);
        Assert.True(engineSettings.UseLocalKokoroTextToSpeech);
    }

    [Fact]
    public void ToEngineJson_NormalizesLegacyManagedBackendToGoogleWeb()
    {
        var settings = new AppSettings
        {
            UseAiTranslation = true,
            TranslationBackend = TranslationBackend.ManagedSmall100,
            UseCloudAsr = false,
            AsrProvider = AsrProvider.LocalManagedMoss,
            AsrProtocol = AsrProtocol.LocalManagedMoss,
            UseRemoteSpeech = false,
            UseLocalKokoroTextToSpeech = false,
            ManagedTtsModel = ManagedTtsModel.Qwen3Tts,
            ManagedTtsReferenceAudioPath = @"C:\voice\ref.wav",
            ManagedTtsReferenceText = "reference transcript"
        };

        // 已下线的托管翻译/ASR 选择在序列化前必须安全回退，
        // 引擎收到的永远是有效组合。
        settings.NormalizeServiceSelections();

        var json = JsonSerializer.Serialize(settings.ToEngineJson(), EngineJsonOptions);
        var engineSettings = JsonSerializer.Deserialize<EngineSettings>(json, EngineJsonOptions);

        Assert.NotNull(engineSettings);
        Assert.Equal(EngineTranslationProvider.GoogleWeb, engineSettings.TranslationProvider);
        Assert.Equal(EngineAsrProtocol.LocalWhisper, engineSettings.AsrProtocol);
        Assert.Equal(EngineAsrProvider.LocalWhisper, engineSettings.AsrProvider);
        Assert.Equal(EngineManagedTtsModel.Qwen3Tts, engineSettings.ManagedTtsModel);
        Assert.Equal(@"C:\voice\ref.wav", engineSettings.ManagedTtsReferenceAudioPath);
        Assert.Equal("reference transcript", engineSettings.ManagedTtsReferenceText);
    }

    [Fact]
    public void KokoroSettings_ClampSpeakerIdAndSpeedToSupportedRanges()
    {
        var settings = new AppSettings();

        settings.KokoroSpeakerId = 500;
        Assert.Equal(102, settings.KokoroSpeakerId);

        settings.KokoroSpeakerId = -3;
        Assert.Equal(0, settings.KokoroSpeakerId);

        settings.KokoroSpeakerId = 42;
        Assert.Equal(42, settings.KokoroSpeakerId);

        settings.KokoroSpeed = 3.5;
        Assert.Equal(2.0, settings.KokoroSpeed);

        settings.KokoroSpeed = 0.1;
        Assert.Equal(0.5, settings.KokoroSpeed);

        settings.KokoroSpeed = double.NaN;
        Assert.Equal(1.0, settings.KokoroSpeed);

        settings.KokoroSpeed = double.PositiveInfinity;
        Assert.Equal(1.0, settings.KokoroSpeed);

        settings.KokoroSpeed = 1.25;
        Assert.Equal(1.25, settings.KokoroSpeed);
    }

    [Fact]
    public void ApplyTranslationBackendDefaults_LocalMiniCpmKeepsConfiguredCloudEndpoint()
    {
        var settings = new AppSettings();
        settings.ApplyTranslationBackendDefaults(TranslationBackend.DeepSeek);

        settings.ApplyTranslationBackendDefaults(TranslationBackend.LocalMiniCpm);

        Assert.Equal(TranslationBackend.LocalMiniCpm, settings.TranslationBackend);
        Assert.Equal("https://api.deepseek.com", settings.TranslationBaseUrl);
        Assert.Equal("deepseek-v4-flash", settings.TranslationModel);
    }

    [Fact]
    public async Task InitializeAsync_LoadsLocalModelCatalogFromEngine()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson("minicpm5-1b", category: "translation", installState: "installed"),
                LocalModelJson("kokoro-82m", category: "tts"))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());

        await controller.InitializeAsync();

        Assert.Contains("listLocalModels", gateway.Requests);
        Assert.Collection(
            controller.LocalModels,
            model =>
            {
                Assert.Equal("minicpm5-1b", model.Id);
                Assert.True(model.Installed);
                Assert.True(model.CanRemove);
                Assert.False(model.CanInstall);
            },
            model =>
            {
                Assert.Equal("kokoro-82m", model.Id);
                Assert.False(model.Installed);
                Assert.True(model.CanInstall);
            });
    }

    [Fact]
    public async Task RefreshLocalModels_PartitionsInstallableAndCatalogOnlyWithSharedInstances()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson("minicpm5-1b", category: "translation", installState: "installed"),
                LocalModelJson("kokoro-82m", category: "tts"),
                LocalModelJson("catalog-only-model", category: "tts", installState: "notinstalled", isInstallable: false))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());

        await controller.InitializeAsync();

        // 主集合保留全部模型，顺序与目录返回一致。
        Assert.Collection(
            controller.LocalModels,
            model => Assert.Equal("minicpm5-1b", model.Id),
            model => Assert.Equal("kokoro-82m", model.Id),
            model => Assert.Equal("catalog-only-model", model.Id));

        // 可安装集合只含 IsInstallable == true 的条目。
        Assert.Collection(
            controller.InstallableLocalModels,
            model => Assert.Equal("minicpm5-1b", model.Id),
            model => Assert.Equal("kokoro-82m", model.Id));

        Assert.Empty(controller.SpeechRecognitionModels);
        Assert.Collection(
            controller.TranslationModels,
            model => Assert.Equal("minicpm5-1b", model.Id));
        Assert.Collection(
            controller.SpeechSynthesisModels,
            model => Assert.Equal("kokoro-82m", model.Id));
        Assert.DoesNotContain(
            controller.SpeechSynthesisModels,
            model => model.Id == "catalog-only-model");
        // 只读目录集合只含 IsInstallable == false 的条目。
        var catalogOnly = Assert.Single(controller.CatalogOnlyLocalModels);
        Assert.Equal("catalog-only-model", catalogOnly.Id);
        Assert.False(catalogOnly.IsInstallable);

        // 分区必须复用 LocalModels 中的同一实例，保证进度更新同步生效。
        Assert.Same(controller.InstallableLocalModels[0], controller.LocalModels[0]);
        Assert.Same(controller.InstallableLocalModels[1], controller.LocalModels[1]);
        Assert.Same(catalogOnly, controller.LocalModels[2]);

        // 无重复、无遗漏。
        var allIds = controller.LocalModels.Select(m => m.Id).ToHashSet();
        var partitionedIds = controller.InstallableLocalModels.Select(m => m.Id)
            .Concat(controller.CatalogOnlyLocalModels.Select(m => m.Id));
        Assert.Equal(allIds.Count, partitionedIds.Distinct().Count());
        Assert.Equal(allIds.Count, partitionedIds.Count());
    }

    [Fact]
    public async Task RefreshLocalModels_PartitionsByCategoryWithoutExperimentalSection()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.WhisperLargeV3Turbo, category: "asr", installState: "installed"),
                LocalModelJson(LocalModelIds.HyMt15Gguf, category: "translation"),
                LocalModelJson(LocalModelIds.MiniCpm51BGguf, category: "translation", installState: "installed"),
                LocalModelJson(LocalModelIds.Kokoro82M, category: "tts", installState: "installed"))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());

        await controller.InitializeAsync();

        // 目录精简后所有可安装模型直接进入分类列表，不再有「更多模型」折叠区。
        Assert.Collection(
            controller.SpeechRecognitionModels,
            model => Assert.Equal(LocalModelIds.WhisperLargeV3Turbo, model.Id));
        Assert.Collection(
            controller.TranslationModels,
            model => Assert.Equal(LocalModelIds.HyMt15Gguf, model.Id),
            model => Assert.Equal(LocalModelIds.MiniCpm51BGguf, model.Id));
        Assert.Collection(
            controller.SpeechSynthesisModels,
            model => Assert.Equal(LocalModelIds.Kokoro82M, model.Id));

        // 全部条目都进入主集合与分类集合，无遗漏。
        Assert.Equal(controller.LocalModels.Count,
            controller.SpeechRecognitionModels.Count
            + controller.TranslationModels.Count
            + controller.SpeechSynthesisModels.Count);
    }

    [Fact]
    public async Task TestLocalModelAsync_Success_ReportsDetailAndClearsBusy()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.MiniCpm51BGguf, category: "translation", installState: "installed")),
            TestResponse = JsonSerializer.SerializeToElement(new { ok = true, detail = "Hello, world!" })
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        await controller.TestLocalModelAsync(LocalModelIds.MiniCpm51BGguf);

        Assert.Contains("testLocalModel", gateway.Requests);
        var model = controller.LocalModels.Single();
        Assert.Equal("测试通过：Hello, world!", model.OperationStatus);
        Assert.Contains("测试通过：Hello, world!", controller.LocalModelResultMessage);
        Assert.False(model.IsBusy);
        Assert.False(controller.HasBusyLocalModels);
        Assert.Null(controller.ErrorMessage);
    }

    [Fact]
    public async Task TestLocalModelAsync_SoftFailure_ReportsDetailAsError()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.WhisperBase, category: "asr", installState: "installed")),
            TestResponse = JsonSerializer.SerializeToElement(
                new { ok = false, detail = "没听清，请对着麦克风说一句话再试" })
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        await controller.TestLocalModelAsync(LocalModelIds.WhisperBase);

        Assert.Contains("测试未通过：没听清", controller.ErrorMessage);
        var model = controller.LocalModels.Single();
        Assert.Equal("测试未通过，可重试", model.OperationStatus);
        Assert.False(model.IsBusy);
        Assert.Null(controller.LocalModelResultMessage);
    }

    [Fact]
    public async Task TestLocalModelAsync_NotInstalled_ShowsErrorWithoutEngineCall()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.MiniCpm51BGguf, category: "translation"))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        await controller.TestLocalModelAsync(LocalModelIds.MiniCpm51BGguf);

        Assert.Contains("还没安装", controller.ErrorMessage);
        Assert.DoesNotContain("testLocalModel", gateway.Requests);
    }

    [Fact]
    public async Task TestLocalModelAsync_UnknownModelId_ShowsError()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.Kokoro82M, category: "tts", installState: "installed"))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        await controller.TestLocalModelAsync("missing-model");

        Assert.Contains("不可测试", controller.ErrorMessage);
        Assert.DoesNotContain("testLocalModel", gateway.Requests);
    }

    [Fact]
    public async Task TestLocalModelAsync_WhileSessionRunning_Rejected()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.WhisperTiny, category: "asr", installState: "installed"))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();
        await controller.ToggleSessionAsync();
        Assert.True(controller.IsRunning);

        await controller.TestLocalModelAsync(LocalModelIds.WhisperTiny);

        Assert.Contains("请先停止翻译", controller.ErrorMessage);
        Assert.DoesNotContain("testLocalModel", gateway.Requests);
    }

    [Fact]
    public async Task TestLocalModelAsync_EngineFailure_MarksModelRetryable()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.Kokoro82M, category: "tts", installState: "installed")),
            FailNextMethod = "testLocalModel"
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        await controller.TestLocalModelAsync(LocalModelIds.Kokoro82M);

        Assert.Contains("模拟引擎失败", controller.ErrorMessage);
        var model = controller.LocalModels.Single();
        Assert.Equal("测试失败，可重试", model.OperationStatus);
        Assert.False(model.IsBusy);
        Assert.False(controller.HasBusyLocalModels);
        Assert.Null(controller.LocalModelResultMessage);
    }

    [Fact]
    public async Task RefreshLocalModels_ReplacesCollectionsDeterministically()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson("model-a", category: "translation"),
                LocalModelJson("model-b", category: "tts", isInstallable: false))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        Assert.Collection(controller.InstallableLocalModels, m => Assert.Equal("model-a", m.Id));
        Assert.Single(controller.CatalogOnlyLocalModels);

        // 第二次刷新返回不同目录，三个集合应整体替换为新实例，且顺序保持目录顺序。
        gateway.ModelsResponse = ModelsPayload(
            LocalModelJson("model-c", category: "tts"),
            LocalModelJson("model-d", category: "translation", isInstallable: false));
        await controller.RefreshLocalModelsAsync();

        Assert.Collection(
            controller.LocalModels,
            m => Assert.Equal("model-c", m.Id),
            m => Assert.Equal("model-d", m.Id));
        Assert.Collection(
            controller.InstallableLocalModels,
            m => Assert.Equal("model-c", m.Id));
        Assert.Collection(
            controller.CatalogOnlyLocalModels,
            m => Assert.Equal("model-d", m.Id));
    }

    [Fact]
    public async Task ModelProgressEvent_UpdatesSharedInstallableInstanceAcrossLists()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson("kokoro-82m", category: "tts"),
                LocalModelJson("catalog-only-model", category: "tts", isInstallable: false))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        gateway.Raise("modelProgress", JsonSerializer.SerializeToElement(new
        {
            status = "正在下载",
            progress = 0.4,
            modelId = "kokoro-82m"
        }));

        // 同一实例在 LocalModels 与 InstallableLocalModels 中同步更新。
        var source = controller.LocalModels.Single(m => m.Id == "kokoro-82m");
        var partitioned = controller.InstallableLocalModels.Single(m => m.Id == "kokoro-82m");
        Assert.Same(source, partitioned);
        Assert.Equal("正在下载", partitioned.OperationStatus);
        Assert.Equal(0.4, partitioned.Progress, precision: 3);
        Assert.True(partitioned.IsBusy);

        // 只读目录模型不受该进度事件影响。
        var catalogOnly = controller.CatalogOnlyLocalModels.Single();
        Assert.Equal(string.Empty, catalogOnly.OperationStatus);
        Assert.False(catalogOnly.IsBusy);
    }

    [Fact]
    public async Task ModelProgressEvent_WithModelId_OnlyUpdatesMatchingItem()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson("minicpm5-1b", category: "translation"),
                LocalModelJson("kokoro-82m", category: "tts"))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        gateway.Raise("modelProgress", JsonSerializer.SerializeToElement(new
        {
            status = "正在下载",
            progress = 0.4,
            modelId = "kokoro-82m"
        }));

        var translation = controller.LocalModels.Single(model => model.Id == "minicpm5-1b");
        var speech = controller.LocalModels.Single(model => model.Id == "kokoro-82m");
        Assert.Equal("正在下载", speech.OperationStatus);
        Assert.Equal(0.4, speech.Progress, precision: 3);
        Assert.True(speech.IsBusy);
        Assert.Equal(string.Empty, translation.OperationStatus);
        Assert.Equal(0, translation.Progress);
        Assert.False(translation.IsBusy);
        // 带 modelId 的进度不更新全局旧进度字段。
        Assert.Equal(string.Empty, controller.ModelStatus);
        Assert.Equal(0, controller.ModelProgress);

        // 分类不匹配的进度事件不会误更新同名模型。
        gateway.Raise("modelProgress", JsonSerializer.SerializeToElement(new
        {
            status = "错误分类",
            progress = 0.9,
            modelId = "kokoro-82m",
            category = "asr"
        }));
        Assert.Equal("正在下载", speech.OperationStatus);
        Assert.Equal(0.4, speech.Progress, precision: 3);

        // 未知模型不抛异常，也不更新任何项。
        gateway.Raise("modelProgress", JsonSerializer.SerializeToElement(new
        {
            status = "未知模型",
            progress = 0.5,
            modelId = "missing-model"
        }));
        Assert.Equal("正在下载", speech.OperationStatus);
        Assert.Equal(string.Empty, translation.OperationStatus);

        // 本地管理器只在校验和原子替换全部完成后报告 1；即使 UI 的 RPC 等待已超时，
        // 最终事件也能恢复真实安装状态并解除忙碌。
        gateway.Raise("modelProgress", JsonSerializer.SerializeToElement(new
        {
            status = "安装完成",
            progress = 1.0,
            modelId = "kokoro-82m",
            category = "tts"
        }));
        Assert.Equal("安装完成", speech.OperationStatus);
        Assert.Equal(1.0, speech.Progress, precision: 3);
        Assert.True(speech.Installed);
        Assert.False(speech.IsBusy);
    }

    [Fact]
    public async Task ModelProgressEvent_WithoutModelId_UpdatesLegacyGlobalProgress()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(LocalModelJson("kokoro-82m", category: "tts"))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        gateway.Raise("modelProgress", JsonSerializer.SerializeToElement(new
        {
            status = "正在下载模型",
            progress = 0.62
        }));

        Assert.Equal("正在下载模型", controller.ModelStatus);
        Assert.Equal(0.62, controller.ModelProgress, precision: 3);
        var model = Assert.Single(controller.LocalModels);
        Assert.False(model.IsBusy);
        Assert.Equal(string.Empty, model.OperationStatus);

        // 空字符串 modelId 同样走全局进度。
        gateway.Raise("modelProgress", JsonSerializer.SerializeToElement(new
        {
            status = "模型就绪",
            progress = 1.0,
            modelId = ""
        }));
        Assert.Equal("模型就绪", controller.ModelStatus);
        Assert.Equal(1.0, controller.ModelProgress, precision: 3);
        Assert.Equal(string.Empty, model.OperationStatus);
    }

    [Fact]
    public async Task InstallLocalModelAsync_Success_MarksInstalledAndRefreshesCatalog()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(LocalModelJson("minicpm5-1b", category: "translation"))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        gateway.ModelsResponse = ModelsPayload(
            LocalModelJson("minicpm5-1b", category: "translation", installState: "installed"));
        await controller.InstallLocalModelAsync("minicpm5-1b");

        var installCall = Assert.Single(gateway.Calls, call => call.Method == "installLocalModel");
        Assert.Equal("minicpm5-1b", installCall.Parameters!["modelId"]);
        Assert.Null(controller.ErrorMessage);
        var model = Assert.Single(controller.LocalModels);
        Assert.True(model.Installed);
        Assert.True(model.CanRemove);
        Assert.False(model.IsBusy);
        // 安装成功后会重新拉取模型目录。
        Assert.Equal(2, gateway.Requests.Count(request => request == "listLocalModels"));
    }

    [Fact]
    public async Task InstallAndActivateLocalModelAsync_ActivatesOnlyAfterVerifiedInstall()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.MiniCpm51BGguf, category: "translation"))
        };
        var repository = new FakeSettingsRepository(new AppSettings());
        await using var controller = new AppController(
            gateway,
            repository,
            new InlineSynchronizationContext());
        await controller.InitializeAsync();
        gateway.ModelsResponse = ModelsPayload(
            LocalModelJson(
                LocalModelIds.MiniCpm51BGguf,
                category: "translation",
                installState: "installed"));

        await controller.InstallAndActivateLocalModelAsync(LocalModelIds.MiniCpm51BGguf);

        Assert.True(controller.Settings.UseAiTranslation);
        Assert.Equal(TranslationBackend.LocalMiniCpm, controller.Settings.TranslationBackend);
        var activeModel = Assert.Single(controller.TranslationModels);
        Assert.True(activeModel.IsActive);
        Assert.Equal("已启用", activeModel.PrimaryActionLabel);
        Assert.False(activeModel.CanRunPrimaryAction);
        Assert.Equal(1, repository.SaveCount);
        Assert.Contains("configure", gateway.Requests);
    }

    [Fact]
    public async Task InstallAndActivateLocalModelAsync_SaveFailureRestoresCompleteServiceConfiguration()
    {
        var settings = new AppSettings
        {
            UseAiTranslation = true,
            TranslationBackend = TranslationBackend.DeepSeek,
            TranslationBaseUrl = "https://api.deepseek.example/v1",
            TranslationModel = "deepseek-test",
            TranslationApiKey = "cloud-secret",
            TranslationHeaders = new() { ["X-Tenant"] = "tenant-a" }
        };
        var repository = new FakeSettingsRepository(settings);
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.MiniCpm51BGguf, "translation", "installed"))
        };
        await using var controller = new AppController(
            gateway, repository, new InlineSynchronizationContext());
        await controller.InitializeAsync();
        repository.SaveFailuresRemaining = 1;

        var activated = await controller.InstallAndActivateLocalModelAsync(
            LocalModelIds.MiniCpm51BGguf);

        Assert.False(activated);
        Assert.True(controller.Settings.UseAiTranslation);
        Assert.Equal(TranslationBackend.DeepSeek, controller.Settings.TranslationBackend);
        Assert.Equal("https://api.deepseek.example/v1", controller.Settings.TranslationBaseUrl);
        Assert.Equal("deepseek-test", controller.Settings.TranslationModel);
        Assert.Equal("cloud-secret", controller.Settings.TranslationApiKey);
        Assert.Equal("tenant-a", controller.Settings.TranslationHeaders["X-Tenant"]);
        Assert.Contains("已恢复原服务选择", controller.ErrorMessage);
        Assert.Equal(2, repository.SaveCount);
    }

    [Fact]
    public async Task InstallAndActivateLocalModelAsync_ConfigureFailureRestoresCompleteServiceConfiguration()
    {
        var settings = new AppSettings
        {
            UseAiTranslation = true,
            TranslationBackend = TranslationBackend.DeepSeek,
            TranslationBaseUrl = "https://api.deepseek.example/v1",
            TranslationModel = "deepseek-test",
            TranslationApiKey = "cloud-secret"
        };
        var repository = new FakeSettingsRepository(settings);
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.MiniCpm51BGguf, "translation", "installed"))
        };
        await using var controller = new AppController(
            gateway, repository, new InlineSynchronizationContext());
        await controller.InitializeAsync();
        gateway.FailNextMethod = "configure";

        var activated = await controller.InstallAndActivateLocalModelAsync(
            LocalModelIds.MiniCpm51BGguf);

        Assert.False(activated);
        Assert.Equal(TranslationBackend.DeepSeek, controller.Settings.TranslationBackend);
        Assert.Equal("https://api.deepseek.example/v1", controller.Settings.TranslationBaseUrl);
        Assert.Equal("deepseek-test", controller.Settings.TranslationModel);
        Assert.Equal("cloud-secret", controller.Settings.TranslationApiKey);
        Assert.Contains("已恢复原服务选择", controller.ErrorMessage);
        Assert.Equal(2, repository.SaveCount);
        Assert.Equal(2, gateway.Requests.Count(request => request == "configure"));
    }

    [Fact]
    public async Task InstallAndActivateLocalModelAsync_RollbackSaveFailureReportsUncertainState()
    {
        var settings = new AppSettings
        {
            UseAiTranslation = true,
            TranslationBackend = TranslationBackend.DeepSeek,
            TranslationBaseUrl = "https://api.deepseek.example/v1",
            TranslationModel = "deepseek-test"
        };
        var repository = new FakeSettingsRepository(settings);
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.MiniCpm51BGguf, "translation", "installed"))
        };
        await using var controller = new AppController(
            gateway, repository, new InlineSynchronizationContext());
        await controller.InitializeAsync();
        gateway.FailNextMethod = "configure";
        repository.FailOnSaveNumbers.Add(2);

        var activated = await controller.InstallAndActivateLocalModelAsync(
            LocalModelIds.MiniCpm51BGguf);

        Assert.False(activated);
        Assert.Equal(TranslationBackend.DeepSeek, controller.Settings.TranslationBackend);
        Assert.Contains("状态可能不一致", controller.ErrorMessage);
        Assert.Contains("回滚失败", controller.ErrorMessage);
        Assert.DoesNotContain("已恢复原服务选择", controller.ErrorMessage);
        Assert.Equal(2, repository.SaveCount);
    }

    [Fact]
    public async Task InstallAndActivateLocalModelAsync_RpcFailureUsesVerifiedDiskState()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.MiniCpm51BGguf, "translation"))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();
        gateway.ModelsResponse = ModelsPayload(
            LocalModelJson(LocalModelIds.MiniCpm51BGguf, "translation", "installed"));
        gateway.FailNextMethod = "installLocalModel";

        var activated = await controller.InstallAndActivateLocalModelAsync(
            LocalModelIds.MiniCpm51BGguf);

        Assert.True(activated);
        Assert.Null(controller.ErrorMessage);
        Assert.Equal(TranslationBackend.LocalMiniCpm, controller.Settings.TranslationBackend);
        Assert.True(Assert.Single(controller.TranslationModels).Installed);
    }

    [Fact]
    public async Task RemoveLocalModelWithFallbackAsync_SaveFailureKeepsSafeFallback()
    {
        var settings = new AppSettings
        {
            UseAiTranslation = true,
            TranslationBackend = TranslationBackend.LocalMiniCpm
        };
        var repository = new FakeSettingsRepository(settings);
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.MiniCpm51BGguf, "translation", "installed"))
        };
        await using var controller = new AppController(
            gateway, repository, new InlineSynchronizationContext());
        await controller.InitializeAsync();
        gateway.ModelsResponse = ModelsPayload(
            LocalModelJson(LocalModelIds.MiniCpm51BGguf, "translation"));
        repository.SaveFailuresRemaining = 1;

        await controller.RemoveLocalModelWithFallbackAsync(LocalModelIds.MiniCpm51BGguf);

        Assert.False(controller.Settings.UseAiTranslation);
        Assert.Equal(TranslationBackend.PublicFree, controller.Settings.TranslationBackend);
        Assert.Contains("模型已删除", controller.ErrorMessage);
        Assert.Equal(2, repository.SaveCount);
    }

    [Fact]
    public async Task InstallRecommendedLocalModelsAsync_ConfigureFailureRestoresCompleteSelections()
    {
        var settings = new AppSettings
        {
            UseAiTranslation = true,
            TranslationBackend = TranslationBackend.DeepSeek,
            TranslationBaseUrl = "https://api.deepseek.example/v1",
            TranslationModel = "deepseek-test",
            UseCloudAsr = true,
            AsrProvider = AsrProvider.Soniox,
            AsrProtocol = AsrProtocol.SonioxStreaming,
            AsrBaseUrl = "wss://soniox.example",
            AsrModel = "stt-custom",
            AllowCloudAudioUpload = true,
            UseRemoteSpeech = true
        };
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.WhisperBase, "asr", "installed"),
                LocalModelJson(LocalModelIds.MiniCpm51BGguf, "translation", "installed"),
                LocalModelJson(LocalModelIds.Kokoro82M, "tts", "installed"))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(settings),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();
        gateway.FailNextMethod = "configure";

        await controller.InstallRecommendedLocalModelsAsync(startSession: true);

        Assert.False(controller.IsRunning);
        Assert.Equal(TranslationBackend.DeepSeek, controller.Settings.TranslationBackend);
        Assert.Equal("https://api.deepseek.example/v1", controller.Settings.TranslationBaseUrl);
        Assert.Equal("deepseek-test", controller.Settings.TranslationModel);
        Assert.True(controller.Settings.UseCloudAsr);
        Assert.Equal(AsrProvider.Soniox, controller.Settings.AsrProvider);
        Assert.Equal(AsrProtocol.SonioxStreaming, controller.Settings.AsrProtocol);
        Assert.Equal("wss://soniox.example", controller.Settings.AsrBaseUrl);
        Assert.Equal("stt-custom", controller.Settings.AsrModel);
        Assert.True(controller.Settings.AllowCloudAudioUpload);
        Assert.Equal(SpeechServiceMode.Remote, controller.Settings.SpeechServiceMode);
        Assert.Contains("已恢复原服务选择", controller.ErrorMessage);
        Assert.DoesNotContain("startSession", gateway.Requests);
    }

    [Fact]
    public async Task InstallAndActivateLocalModelAsync_FailurePreservesPreviousService()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.MiniCpm51BGguf, category: "translation")),
            InstallResponse = null
        };
        var settings = new AppSettings();
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(settings),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();
        gateway.ModelsResponse = ModelsPayload(
            LocalModelJson(
                LocalModelIds.MiniCpm51BGguf,
                category: "translation",
                installState: "partial"));

        await controller.InstallAndActivateLocalModelAsync(LocalModelIds.MiniCpm51BGguf);

        Assert.False(controller.Settings.UseAiTranslation);
        Assert.Equal(TranslationBackend.PublicFree, controller.Settings.TranslationBackend);
        var failedModel = Assert.Single(controller.TranslationModels);
        Assert.False(failedModel.IsActive);
        Assert.Equal("重试并启用", failedModel.PrimaryActionLabel);
        Assert.True(failedModel.CanRunPrimaryAction);
        Assert.NotNull(controller.ErrorMessage);
    }

    [Fact]
    public async Task RemoveLocalModelWithFallbackAsync_UsesSafeCategoryFallbacks()
    {
        var settings = new AppSettings
        {
            UseAiTranslation = true,
            TranslationBackend = TranslationBackend.LocalMiniCpm,
            UseLocalKokoroTextToSpeech = true,
            WhisperModel = "base"
        };
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.WhisperTiny, "asr", "installed"),
                LocalModelJson(LocalModelIds.WhisperBase, "asr", "installed"),
                LocalModelJson(LocalModelIds.MiniCpm51BGguf, "translation", "installed"),
                LocalModelJson(LocalModelIds.Kokoro82M, "tts", "installed"))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(settings),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        gateway.ModelsResponse = ModelsPayload(
            LocalModelJson(LocalModelIds.WhisperTiny, "asr", "installed"),
            LocalModelJson(LocalModelIds.WhisperBase, "asr"),
            LocalModelJson(LocalModelIds.MiniCpm51BGguf, "translation", "installed"),
            LocalModelJson(LocalModelIds.Kokoro82M, "tts", "installed"));
        await controller.RemoveLocalModelWithFallbackAsync(LocalModelIds.WhisperBase);
        Assert.Equal("tiny", controller.Settings.WhisperModel);

        gateway.ModelsResponse = ModelsPayload(
            LocalModelJson(LocalModelIds.WhisperTiny, "asr", "installed"),
            LocalModelJson(LocalModelIds.WhisperBase, "asr"),
            LocalModelJson(LocalModelIds.MiniCpm51BGguf, "translation"),
            LocalModelJson(LocalModelIds.Kokoro82M, "tts", "installed"));
        await controller.RemoveLocalModelWithFallbackAsync(LocalModelIds.MiniCpm51BGguf);
        Assert.False(controller.Settings.UseAiTranslation);
        Assert.Equal(TranslationBackend.PublicFree, controller.Settings.TranslationBackend);

        gateway.ModelsResponse = ModelsPayload(
            LocalModelJson(LocalModelIds.WhisperTiny, "asr", "installed"),
            LocalModelJson(LocalModelIds.WhisperBase, "asr"),
            LocalModelJson(LocalModelIds.MiniCpm51BGguf, "translation"),
            LocalModelJson(LocalModelIds.Kokoro82M, "tts"));
        await controller.RemoveLocalModelWithFallbackAsync(LocalModelIds.Kokoro82M);
        Assert.Equal(SpeechServiceMode.SystemFallback, controller.Settings.SpeechServiceMode);
    }

    [Fact]
    public async Task ToggleSessionAsync_InstallsSelectedLocalModelBeforeStarting()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.WhisperTiny, category: "asr"))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();
        gateway.ModelsResponse = ModelsPayload(
            LocalModelJson(LocalModelIds.WhisperTiny, "asr", "installed"));

        await controller.ToggleSessionAsync();

        Assert.True(controller.IsRunning);
        Assert.True(Assert.Single(controller.SpeechRecognitionModels).Installed);
        Assert.True(gateway.Requests.IndexOf("installLocalModel")
            < gateway.Requests.IndexOf("startSession"));
    }

    [Fact]
    public async Task InstallRecommendedLocalModelsAsync_ReadyStackEnablesAndStarts()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.WhisperBase, "asr", "installed"),
                LocalModelJson(LocalModelIds.MiniCpm51BGguf, "translation", "installed"),
                LocalModelJson(LocalModelIds.Kokoro82M, "tts", "installed"))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        await controller.InstallRecommendedLocalModelsAsync(startSession: true);

        Assert.True(controller.RecommendedLocalModelsReady);
        Assert.True(controller.Settings.UseAiTranslation);
        Assert.Equal(TranslationBackend.LocalMiniCpm, controller.Settings.TranslationBackend);
        Assert.Equal("base", controller.Settings.WhisperModel);
        Assert.Equal(SpeechServiceMode.Kokoro, controller.Settings.SpeechServiceMode);
        Assert.True(controller.IsRunning);
        Assert.Contains("startSession", gateway.Requests);
    }

    [Fact]
    public async Task InstallRecommendedLocalModelsAsync_FailurePreservesServiceSelections()
    {
        var settings = new AppSettings
        {
            UseAiTranslation = false,
            TranslationBackend = TranslationBackend.PublicFree,
            UseCloudAsr = true,
            AsrProvider = AsrProvider.Soniox,
            AsrProtocol = AsrProtocol.SonioxStreaming,
            WhisperModel = "small",
            UseRemoteSpeech = true
        };
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.WhisperBase, "asr"),
                LocalModelJson(LocalModelIds.MiniCpm51BGguf, "translation"),
                LocalModelJson(LocalModelIds.Kokoro82M, "tts")),
            InstallResponse = null
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(settings),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        await controller.InstallRecommendedLocalModelsAsync(startSession: true);

        Assert.False(controller.IsRunning);
        Assert.False(controller.Settings.UseAiTranslation);
        Assert.Equal(TranslationBackend.PublicFree, controller.Settings.TranslationBackend);
        Assert.True(controller.Settings.UseCloudAsr);
        Assert.Equal(AsrProvider.Soniox, controller.Settings.AsrProvider);
        Assert.Equal(AsrProtocol.SonioxStreaming, controller.Settings.AsrProtocol);
        Assert.Equal("small", controller.Settings.WhisperModel);
        Assert.Equal(SpeechServiceMode.Remote, controller.Settings.SpeechServiceMode);
    }

    [Fact]
    public async Task RemoveLocalModelWithFallbackAsync_RejectsActiveModelWhileRunning()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.WhisperTiny, "asr", "installed"))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();
        await controller.ToggleSessionAsync();

        await controller.RemoveLocalModelWithFallbackAsync(LocalModelIds.WhisperTiny);

        Assert.True(controller.IsRunning);
        Assert.DoesNotContain("removeLocalModel", gateway.Requests);
        Assert.Equal("请先停止翻译，再删除正在使用的模型。", controller.ErrorMessage);
    }

    [Fact]
    public async Task RemoveOnlyWhisperModel_LeavesExplicitMissingSelectionAndBlocksStart()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.WhisperTiny, "asr", "installed"))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();
        gateway.ModelsResponse = ModelsPayload(
            LocalModelJson(LocalModelIds.WhisperTiny, "asr"));

        await controller.RemoveLocalModelWithFallbackAsync(LocalModelIds.WhisperTiny);
        await controller.ToggleSessionAsync();

        Assert.Equal(string.Empty, controller.Settings.WhisperModel);
        Assert.False(controller.IsRunning);
        Assert.Equal("请选择本地 Whisper 模型。", controller.ErrorMessage);
        Assert.DoesNotContain("startSession", gateway.Requests);
    }
    [Fact]
    public async Task RemoveLocalModelAsync_Success_MarksNotInstalled()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson("minicpm5-1b", category: "translation", installState: "installed"))
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        gateway.ModelsResponse = ModelsPayload(LocalModelJson("minicpm5-1b", category: "translation"));
        await controller.RemoveLocalModelAsync("minicpm5-1b");

        var removeCall = Assert.Single(gateway.Calls, call => call.Method == "removeLocalModel");
        Assert.Equal("minicpm5-1b", removeCall.Parameters!["modelId"]);
        Assert.Null(controller.ErrorMessage);
        var model = Assert.Single(controller.LocalModels);
        Assert.False(model.Installed);
        Assert.True(model.CanInstall);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task InstallLocalModelAsync_Failure_RestoresRetryableState()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(LocalModelJson("minicpm5-1b", category: "translation")),
            InstallResponse = null
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        // 失败后控制器会重新拉取目录；引擎此时将模型报告为 partial。
        gateway.ModelsResponse = ModelsPayload(
            LocalModelJson("minicpm5-1b", category: "translation", installState: "partial"));
        await controller.InstallLocalModelAsync("minicpm5-1b");

        Assert.NotNull(controller.ErrorMessage);
        Assert.Equal("error", controller.Activity);
        Assert.Equal(2, gateway.Requests.Count(request => request == "listLocalModels"));
        var model = Assert.Single(controller.LocalModels);
        Assert.True(model.IsPartial);
        Assert.False(model.IsBusy);
        Assert.True(model.CanInstall);
        Assert.Equal("重试", model.InstallActionLabel);
        Assert.Equal("安装失败，可重试", model.OperationStatus);

        // 重试成功后恢复正常状态。
        gateway.InstallResponse = JsonSerializer.SerializeToElement(new { installState = "installed" });
        gateway.ModelsResponse = ModelsPayload(
            LocalModelJson("minicpm5-1b", category: "translation", installState: "installed"));
        await controller.RetryLocalModelAsync("minicpm5-1b");

        Assert.Null(controller.ErrorMessage);
        model = Assert.Single(controller.LocalModels);
        Assert.True(model.Installed);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task InstallLocalModelAsync_FailureWithUnavailableCatalog_MarksOriginalItemPartial()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(LocalModelJson("minicpm5-1b", category: "translation")),
            InstallResponse = null
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        // 安装失败且失败后的目录刷新也失败时，回退为标记原条目 partial。
        gateway.ModelsResponse = null;
        await controller.InstallLocalModelAsync("minicpm5-1b");

        Assert.NotNull(controller.ErrorMessage);
        var model = Assert.Single(controller.LocalModels);
        Assert.True(model.IsPartial);
        Assert.False(model.IsBusy);
        Assert.True(model.CanInstall);
        Assert.Equal("重试", model.InstallActionLabel);
        Assert.Equal("安装失败，可重试", model.OperationStatus);
    }

    [Fact]
    public async Task InstallLocalModelAsync_BlocksConcurrentSessionStartWithFeedback()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(
                LocalModelJson(LocalModelIds.WhisperTiny, "asr", "installed"),
                LocalModelJson("minicpm5-1b", category: "translation")),
            BlockInstall = true
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();

        var install = controller.InstallLocalModelAsync("minicpm5-1b");
        await gateway.InstallStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));

        await controller.ToggleSessionAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.DoesNotContain("startSession", gateway.Requests);
        Assert.False(controller.IsRunning);
        Assert.Equal("当前有操作正在进行，请稍后重试。", controller.ErrorMessage);
        Assert.False(install.IsCompleted);
        gateway.ModelsResponse = ModelsPayload(
            LocalModelJson(LocalModelIds.WhisperTiny, "asr", "installed"),
            LocalModelJson("minicpm5-1b", category: "translation", installState: "installed"));
        gateway.InstallRelease.TrySetResult();
        await install.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(controller.LocalModels.Single(model => model.Id == "minicpm5-1b").Installed);
    }

    [Fact]
    public async Task RefreshLocalModels_PreservesBusyInstanceDuringInstall()
    {
        var gateway = new FakeEngineGateway
        {
            ModelsResponse = ModelsPayload(LocalModelJson("minicpm5-1b", category: "translation")),
            BlockInstall = true
        };
        await using var controller = new AppController(
            gateway,
            new FakeSettingsRepository(new AppSettings()),
            new InlineSynchronizationContext());
        await controller.InitializeAsync();
        var original = Assert.Single(controller.LocalModels);

        var install = controller.InstallLocalModelAsync(original.Id);
        await gateway.InstallStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(original.IsBusy);
        Assert.True(controller.HasBusyLocalModels);

        gateway.ModelsResponse = ModelsPayload(
            LocalModelJson(original.Id, category: "translation", installState: "partial"));
        await controller.RefreshLocalModelsAsync();

        var refreshed = Assert.Single(controller.LocalModels);
        Assert.Same(original, refreshed);
        Assert.True(refreshed.IsBusy);
        Assert.False(refreshed.CanInstall);
        Assert.True(controller.HasBusyLocalModels);

        gateway.ModelsResponse = ModelsPayload(
            LocalModelJson(original.Id, category: "translation", installState: "installed"));
        gateway.InstallRelease.TrySetResult();
        await install.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(controller.HasBusyLocalModels);
        Assert.True(Assert.Single(controller.LocalModels).Installed);
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

    private static JsonElement ModelsPayload(params object[] models) =>
        JsonSerializer.SerializeToElement(new { models });

    private static object LocalModelJson(
        string id,
        string category,
        string installState = "notinstalled",
        bool isInstallable = true) => new
        {
            id,
            name = id,
            category,
            supportLevel = "stable",
            runtime = "llamaCpp",
            parameters = "1B",
            numericParameterBillions = 1.0,
            license = "Apache-2.0",
            languages = "zh,en",
            requirements = "4GB 内存",
            sourceUrl = "https://example.test/model",
            description = "测试模型",
            downloadBytes = 1024,
            isInstallable,
            installState
        };

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    private sealed class FakeSettingsRepository(AppSettings settings) : ISettingsRepository
    {
        public int SaveCount { get; private set; }
        public int SaveFailuresRemaining { get; set; }
        public HashSet<int> FailOnSaveNumbers { get; } = [];
        public Exception SaveError { get; set; } = new IOException("设置保存失败");

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(settings);

        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            if (SaveFailuresRemaining > 0 || FailOnSaveNumbers.Remove(SaveCount))
            {
                if (SaveFailuresRemaining > 0)
                {
                    SaveFailuresRemaining--;
                }
                return Task.FromException(SaveError);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSettingsRepository(Exception error) : ISettingsRepository
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<AppSettings>(error);

        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
    private sealed class BlockingSettingsRepository(AppSettings settings) : ISettingsRepository
    {
        public int SaveCount { get; private set; }
        public AppSettings? LastSaved { get; private set; }

        public TaskCompletionSource LoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LoadRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SaveCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadStarted.TrySetResult();
            await LoadRelease.Task.WaitAsync(cancellationToken);
            return settings;
        }

        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            LastSaved = value;
            SaveCompleted.TrySetResult();
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
        public List<IReadOnlyList<string>> LaunchArgumentSets { get; } = [];
        public JsonElement? ModelsResponse { get; set; }
        public JsonElement? InstallResponse { get; set; } =
            JsonSerializer.SerializeToElement(new { installState = "installed" });
        public JsonElement? RemoveResponse { get; set; } =
            JsonSerializer.SerializeToElement(new { removed = true });
        public JsonElement? TestResponse { get; set; } =
            JsonSerializer.SerializeToElement(new { ok = true, detail = "测试通过" });
        public string? FailNextMethod { get; set; }
        public Exception NextFailure { get; set; } = new EngineException("模拟引擎失败");
        public bool BlockInstall { get; init; }
        public TaskCompletionSource InstallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InstallRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Raise(string name, JsonElement data) =>
            EventReceived?.Invoke(this, new EngineEvent(name, data));

        public void SetLaunchArguments(IReadOnlyList<string> arguments) =>
            LaunchArgumentSets.Add([.. arguments]);

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public async Task<JsonElement?> RequestAsync(
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
            if (string.Equals(method, FailNextMethod, StringComparison.Ordinal))
            {
                FailNextMethod = null;
                throw NextFailure;
            }
            if (method == "installLocalModel" && BlockInstall)
            {
                InstallStarted.TrySetResult();
                await InstallRelease.Task.WaitAsync(cancellationToken);
            }

            if (method is "listLocalModels" or "installLocalModel" or "removeLocalModel" or "testLocalModel")
            {
                return method switch
                {
                    "listLocalModels" => ModelsResponse,
                    "installLocalModel" => InstallResponse,
                    "removeLocalModel" => RemoveResponse,
                    _ => TestResponse
                };
            }

            if (method != "initialize" && method != "getBootstrap")
            {
                return null;
            }

            return JsonSerializer.SerializeToElement(new
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

        public void SetLaunchArguments(IReadOnlyList<string> arguments)
        {
        }

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
