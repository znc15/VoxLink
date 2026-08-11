using System.Text.Json;
using System.Text.Json.Serialization;
using EngineSettings = VoxLink.Models.AppSettings;
using EngineTranslationProvider = VoxLink.Models.TranslationProvider;
using EngineAsrProvider = VoxLink.Models.AsrProvider;
using EngineAsrProtocol = VoxLink.Models.AsrProtocol;
using VoxLink.UI.Core.Models;

namespace VoxLink.Tests.UI;

public sealed class ModelServiceSelectionTests
{
    private static readonly JsonSerializerOptions EngineJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    // ---------- 翻译选择 ----------

    [Fact]
    public void SelectTranslationBackend_PublicFree_DisablesAiTranslationAndUsesGoogleWeb()
    {
        var settings = new AppSettings();
        settings.SelectTranslationBackend(TranslationBackend.PublicFree);

        Assert.False(settings.UseAiTranslation);
        Assert.Equal(TranslationBackend.PublicFree, settings.TranslationBackend);
    }

    [Theory]
    [InlineData(TranslationBackend.DashScope)]
    [InlineData(TranslationBackend.DeepSeek)]
    [InlineData(TranslationBackend.OpenAiCompatible)]
    [InlineData(TranslationBackend.Custom)]
    [InlineData(TranslationBackend.LocalMiniCpm)]
    public void SelectTranslationBackend_OtherProviders_EnableAiTranslationAndApplyDefaults(TranslationBackend backend)
    {
        var settings = new AppSettings();
        settings.SelectTranslationBackend(backend);

        Assert.True(settings.UseAiTranslation);
        Assert.Equal(backend, settings.TranslationBackend);
    }

    [Fact]
    public void SelectTranslationBackend_PreservesSavedApiKeyAndHeaders()
    {
        var settings = new AppSettings
        {
            TranslationApiKey = "secret-key",
            TranslationHeaders = new Dictionary<string, string> { ["X-Token"] = "secret-header" }
        };

        settings.SelectTranslationBackend(TranslationBackend.DeepSeek);
        Assert.Equal("secret-key", settings.TranslationApiKey);
        Assert.Equal("secret-header", settings.TranslationHeaders["X-Token"]);

        settings.SelectTranslationBackend(TranslationBackend.PublicFree);
        Assert.Equal("secret-key", settings.TranslationApiKey);
        Assert.Equal("secret-header", settings.TranslationHeaders["X-Token"]);
    }

    // ---------- ASR 选择 ----------

    [Fact]
    public void SelectAsrProvider_LocalWhisper_DisablesCloudAndRevokesUploadAuthorization()
    {
        var settings = new AppSettings
        {
            UseCloudAsr = true,
            AllowCloudAudioUpload = true
        };
        settings.SelectAsrProvider(AsrProvider.LocalWhisper);

        Assert.False(settings.UseCloudAsr);
        Assert.False(settings.AllowCloudAudioUpload);
        Assert.Equal(AsrProvider.LocalWhisper, settings.AsrProvider);
        Assert.Equal(AsrProtocol.LocalWhisper, settings.AsrProtocol);
    }

    [Theory]
    [InlineData(AsrProvider.DashScope)]
    [InlineData(AsrProvider.Soniox)]
    [InlineData(AsrProvider.SiliconFlow)]
    [InlineData(AsrProvider.MiMo)]
    [InlineData(AsrProvider.OpenAiCompatible)]
    [InlineData(AsrProvider.Custom)]
    public void SelectAsrProvider_CloudProviders_EnableCloudWithoutAutoUpload(AsrProvider provider)
    {
        var settings = new AppSettings { AllowCloudAudioUpload = false };
        settings.SelectAsrProvider(provider);

        Assert.True(settings.UseCloudAsr);
        Assert.Equal(provider, settings.AsrProvider);
        Assert.NotEqual(AsrProtocol.LocalWhisper, settings.AsrProtocol);
        // 云音频必须显式授权：选择云提供方绝不自动开启上传。
        Assert.False(settings.AllowCloudAudioUpload);
    }

    [Fact]
    public void SelectAsrProvider_ChangingCloudProviderRevokesPreviousAuthorization()
    {
        var settings = new AppSettings();
        settings.SelectAsrProvider(AsrProvider.Soniox);
        settings.AllowCloudAudioUpload = true;

        settings.SelectAsrProvider(AsrProvider.DashScope);

        Assert.Equal(AsrProvider.DashScope, settings.AsrProvider);
        Assert.False(settings.AllowCloudAudioUpload);
    }
    [Fact]
    public void SelectAsrProvider_CloudThenLocal_KeepsCloudConfigAndRevokesUpload()
    {
        var settings = new AppSettings();
        settings.SelectAsrProvider(AsrProvider.Soniox);
        settings.AsrApiKey = "asr-key";
        settings.SelectAsrProvider(AsrProvider.LocalWhisper);

        Assert.False(settings.UseCloudAsr);
        Assert.False(settings.AllowCloudAudioUpload);
        Assert.Equal(AsrProvider.LocalWhisper, settings.AsrProvider);
        Assert.Equal(AsrProtocol.LocalWhisper, settings.AsrProtocol);
        // 切回本地时保留云配置，便于恢复。
        Assert.Equal("wss://stt-rt.soniox.com/transcribe-websocket", settings.AsrBaseUrl);
        Assert.Equal("stt-rt-v5", settings.AsrModel);
        Assert.Equal("asr-key", settings.AsrApiKey);
    }

    // ---------- TTS 三态 ----------

    [Theory]
    [InlineData(SpeechServiceMode.SystemFallback, false, false)]
    [InlineData(SpeechServiceMode.Remote, true, false)]
    [InlineData(SpeechServiceMode.Kokoro, false, true)]
    public void SelectSpeechService_MapsModeExactly(SpeechServiceMode mode, bool remote, bool kokoro)
    {
        // 从冲突的历史状态出发，确保三态精确互斥。
        var settings = new AppSettings
        {
            UseRemoteSpeech = !remote,
            UseLocalKokoroTextToSpeech = !kokoro
        };
        settings.SelectSpeechService(mode);

        Assert.Equal(remote, settings.UseRemoteSpeech);
        Assert.Equal(kokoro, settings.UseLocalKokoroTextToSpeech);
        Assert.Equal(mode, settings.SpeechServiceMode);
    }

    [Theory]
    [InlineData(false, false, SpeechServiceMode.SystemFallback)]
    [InlineData(true, false, SpeechServiceMode.Remote)]
    [InlineData(false, true, SpeechServiceMode.Kokoro)]
    // 历史 double-true 时以 Kokoro 优先。
    [InlineData(true, true, SpeechServiceMode.Kokoro)]
    public void SpeechServiceMode_ComputedFromSwitches_WithKokoroPriority(
        bool remote, bool kokoro, SpeechServiceMode expected)
    {
        var settings = new AppSettings
        {
            UseRemoteSpeech = remote,
            UseLocalKokoroTextToSpeech = kokoro
        };
        Assert.Equal(expected, settings.SpeechServiceMode);
    }

    // ---------- ToEngineJson 实际输出一致性 ----------

    [Theory]
    [InlineData(SpeechServiceMode.SystemFallback, false, false)]
    [InlineData(SpeechServiceMode.Remote, true, false)]
    [InlineData(SpeechServiceMode.Kokoro, false, true)]
    public void ToEngineJson_MatchesSpeechServiceMode(SpeechServiceMode mode, bool remote, bool kokoro)
    {
        var settings = new AppSettings();
        settings.SelectSpeechService(mode);

        var engine = Deserialize(settings);
        Assert.Equal(remote, engine.UseRemoteTextToSpeech);
        Assert.Equal(kokoro, engine.UseLocalKokoroTextToSpeech);
    }

    [Fact]
    public void ToEngineJson_AfterSelectTranslation_MatchesConfiguredProvider()
    {
        var settings = new AppSettings();
        settings.SelectTranslationBackend(TranslationBackend.DeepSeek);
        Assert.Equal(EngineTranslationProvider.DeepSeek, Deserialize(settings).TranslationProvider);

        settings.SelectTranslationBackend(TranslationBackend.PublicFree);
        Assert.Equal(EngineTranslationProvider.GoogleWeb, Deserialize(settings).TranslationProvider);
    }

    [Fact]
    public void ToEngineJson_AfterSelectAsr_MatchesConfiguredProviderAndRevokesUpload()
    {
        var settings = new AppSettings();
        settings.SelectAsrProvider(AsrProvider.Soniox);
        var engine = Deserialize(settings);
        Assert.Equal(EngineAsrProvider.Soniox, engine.AsrProvider);
        Assert.Equal(EngineAsrProtocol.SonioxStreaming, engine.AsrProtocol);

        settings.SelectAsrProvider(AsrProvider.LocalWhisper);
        engine = Deserialize(settings);
        Assert.Equal(EngineAsrProvider.LocalWhisper, engine.AsrProvider);
        Assert.Equal(EngineAsrProtocol.LocalWhisper, engine.AsrProtocol);
        Assert.False(engine.AllowCloudAudioUpload);
    }

    [Theory]
    [InlineData(AsrProvider.DashScope, AsrProtocol.DashScopeStreaming)]
    [InlineData(AsrProvider.Soniox, AsrProtocol.SonioxStreaming)]
    [InlineData(AsrProvider.SiliconFlow, AsrProtocol.OpenAiMultipart)]
    [InlineData(AsrProvider.MiMo, AsrProtocol.MiMoInputAudio)]
    [InlineData(AsrProvider.OpenAiCompatible, AsrProtocol.OpenAiMultipart)]
    public void NormalizeServiceSelections_RepairsFixedProviderProtocol(
        AsrProvider provider, AsrProtocol expectedProtocol)
    {
        var settings = new AppSettings
        {
            UseCloudAsr = true,
            AsrProvider = provider,
            AsrProtocol = AsrProtocol.SonioxStreaming,
            AsrBaseUrl = "https://saved.example/asr",
            AsrModel = "saved-model",
            AllowCloudAudioUpload = true
        };

        settings.NormalizeServiceSelections();

        Assert.True(settings.UseCloudAsr);
        Assert.Equal(provider, settings.AsrProvider);
        Assert.Equal(expectedProtocol, settings.AsrProtocol);
        Assert.Equal("https://saved.example/asr", settings.AsrBaseUrl);
        Assert.Equal("saved-model", settings.AsrModel);
        Assert.True(settings.AllowCloudAudioUpload);
    }
    private static EngineSettings Deserialize(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings.ToEngineJson(), EngineJsonOptions);
        return JsonSerializer.Deserialize<EngineSettings>(json, EngineJsonOptions)!;
    }
}