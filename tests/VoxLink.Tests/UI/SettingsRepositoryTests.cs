using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.Services;

namespace VoxLink.Tests.UI;

public sealed class SettingsRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"VoxLink.UI.Tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveAsync_SeparatesPublicSettingsFromDpapiSecrets()
    {
        var repository = CreateRepository();
        var settings = new AppSettings
        {
            QuickStartMode = QuickStartMode.VrChatVoice,
            OnboardingCompleted = true,
            OutboundSpeechContent = OutboundSpeechContent.Original,
            SpeakMyTranslation = true,
            MyLanguageCode = "ja",
            OtherLanguageCode = "en",
            SecondaryTargetLanguageCode = "fr",
            CaptureMicrophone = false,
            CaptureSystemAudio = true,
            UseAiTranslation = true,
            TranslationBackend = TranslationBackend.DeepSeek,
            TranslationApiKey = "translation-secret-value",
            EnableTranslationRefinement = true,
            TranslationRefinementPrompt = "Keep terminology.",
            AsrProvider = AsrProvider.Soniox,
            UseCloudAsr = true,
            AsrProtocol = AsrProtocol.SonioxStreaming,
            AsrBaseUrl = "wss://stt-rt.soniox.com/transcribe-websocket",
            AsrApiKey = "asr-secret-value",
            AsrModel = "stt-rt-v5",
            AllowCloudAudioUpload = true,
            SpeechApiKey = "speech-secret-value",
            ShowVrOverlay = true,
            VrOverlayWidthMeters = 2.2,
            VrOverlayDistanceMeters = 2.6,
            VrOverlayVerticalOffsetMeters = -0.5,
            VrChatChatboxEnabled = true,
            VrChatOscAddress = "127.0.0.2",
            VrChatOscPort = 9011,
            VrChatIncludeSourceText = true,
            VrChatMuteSelfEnabled = true,
            VrChatOscListenAddress = "127.0.0.3",
            VrChatOscListenPort = 9012,
            SmartSentenceSegmentation = false,
            TranscriptionOnly = true,
            SpeakerLabelMode = SpeakerLabelMode.Cloud,
            SpeakInboundTranslation = true,
            TranslationHeaders = new Dictionary<string, string>
            {
                ["X-Translation-Token"] = "translation-header-secret"
            },
            SpeechHeaders = new Dictionary<string, string>
            {
                ["X-Speech-Token"] = "speech-header-secret"
            },
            AsrHeaders = new Dictionary<string, string>
            {
                ["X-ASR-Token"] = "asr-header-secret"
            },
            MinimizeToTray = false,
            ConfirmOnClose = true
        };

        await repository.SaveAsync(settings);

        var publicJson = await File.ReadAllTextAsync(PathFor("settings.json"));
        var protectedBytes = await File.ReadAllBytesAsync(PathFor("secrets.dat"));
        var loaded = await repository.LoadAsync();

        Assert.DoesNotContain("translation-secret-value", publicJson, StringComparison.Ordinal);
        Assert.DoesNotContain("speech-secret-value", publicJson, StringComparison.Ordinal);
        Assert.DoesNotContain("asr-secret-value", publicJson, StringComparison.Ordinal);
        Assert.DoesNotContain("translation-header-secret", publicJson, StringComparison.Ordinal);
        Assert.DoesNotContain("speech-header-secret", publicJson, StringComparison.Ordinal);
        Assert.DoesNotContain("asr-header-secret", publicJson, StringComparison.Ordinal);
        Assert.DoesNotContain("translation-secret-value", Encoding.UTF8.GetString(protectedBytes), StringComparison.Ordinal);
        Assert.Equal("translation-secret-value", loaded.TranslationApiKey);
        Assert.Equal("speech-secret-value", loaded.SpeechApiKey);
        Assert.Equal("asr-secret-value", loaded.AsrApiKey);
        Assert.Equal("translation-header-secret", loaded.TranslationHeaders["x-translation-token"]);
        Assert.Equal("speech-header-secret", loaded.SpeechHeaders["x-speech-token"]);
        Assert.Equal("asr-header-secret", loaded.AsrHeaders["x-asr-token"]);
        Assert.Equal(TranslationBackend.DeepSeek, loaded.TranslationBackend);
        Assert.Equal("fr", loaded.SecondaryTargetLanguageCode);
        Assert.False(loaded.CaptureMicrophone);
        Assert.True(loaded.CaptureSystemAudio);
        Assert.True(loaded.UseAiTranslation);
        Assert.True(loaded.UseCloudAsr);
        Assert.True(loaded.EnableTranslationRefinement);
        Assert.Equal("Keep terminology.", loaded.TranslationRefinementPrompt);
        Assert.Equal(AsrProvider.Soniox, loaded.AsrProvider);
        Assert.Equal(AsrProtocol.SonioxStreaming, loaded.AsrProtocol);
        Assert.True(loaded.AllowCloudAudioUpload);
        Assert.False(loaded.SmartSentenceSegmentation);
        Assert.True(loaded.TranscriptionOnly);
        Assert.Equal(SpeakerLabelMode.Cloud, loaded.SpeakerLabelMode);
        Assert.Equal(QuickStartMode.VrChatVoice, loaded.QuickStartMode);
        Assert.True(loaded.OnboardingCompleted);
        Assert.Equal(OutboundSpeechContent.Original, loaded.OutboundSpeechContent);
        Assert.True(loaded.SpeakMyTranslation);
        Assert.True(loaded.SpeakInboundTranslation);
        Assert.True(loaded.ShowVrOverlay);
        Assert.Equal(2.2, loaded.VrOverlayWidthMeters);
        Assert.Equal(2.6, loaded.VrOverlayDistanceMeters);
        Assert.Equal(-0.5, loaded.VrOverlayVerticalOffsetMeters);
        Assert.True(loaded.VrChatChatboxEnabled);
        Assert.Equal("127.0.0.2", loaded.VrChatOscAddress);
        Assert.Equal(9011, loaded.VrChatOscPort);
        Assert.True(loaded.VrChatIncludeSourceText);
        Assert.True(loaded.VrChatMuteSelfEnabled);
        Assert.Equal("127.0.0.3", loaded.VrChatOscListenAddress);
        Assert.Equal(9012, loaded.VrChatOscListenPort);
        Assert.False(loaded.MinimizeToTray);
        Assert.True(loaded.ConfirmOnClose);
        Assert.Contains("vrChatOscAddress", publicJson, StringComparison.Ordinal);
        Assert.Contains("secondaryTargetLanguageCode", publicJson, StringComparison.Ordinal);
        Assert.Contains("speakerLabelMode", publicJson, StringComparison.Ordinal);
        Assert.Contains("quickStartMode", publicJson, StringComparison.Ordinal);
        Assert.Contains("outboundSpeechContent", publicJson, StringComparison.Ordinal);
        Assert.Contains("minimizeToTray", publicJson, StringComparison.Ordinal);
        Assert.Contains("confirmOnClose", publicJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_MigratesFlutterPreferencesAndDpapiSecrets()
    {
        Directory.CreateDirectory(_directory);
        var legacySettings = JsonSerializer.Serialize(new
        {
            myLanguageCode = "ko",
            otherLanguageCode = "ja",
            translationBackend = "dashScope",
            translationBaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            translationModel = "qwen-plus",
            useRemoteSpeech = true,
            speechProtocol = "openAiCompatible",
            speechBaseUrl = "https://speech.example/v1/audio/speech",
            speechModel = "tts-1",
            speechVoice = "alloy",
            whisperModel = "base",
            voiceThreshold = 0.024,
            silenceDurationMs = 900,
            speakMyTranslation = false,
            showOverlay = false,
            toggleHotkey = "Ctrl+Shift+Space",
            translateHotkey = "Ctrl+Shift+Enter"
        });
        var preferences = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["flutter.voxlink.settings.v2"] = legacySettings
        });
        await File.WriteAllTextAsync(PathFor("legacy-preferences.json"), preferences);

        var legacySecretsJson = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["voxlink.translation.apiKey"] = "legacy-translation-key",
            ["voxlink.speech.apiKey"] = "legacy-speech-key",
            ["voxlink.translation.headers"] = "{\"X-Legacy\":\"legacy-header-value\"}",
            ["voxlink.speech.headers"] = "{\"X-Voice\":\"legacy-voice-value\"}"
        });
        var legacySecrets = ProtectedData.Protect(
            legacySecretsJson,
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(PathFor("legacy-secrets.dat"), legacySecrets);

        var loaded = await CreateRepository().LoadAsync();

        Assert.Equal("ko", loaded.MyLanguageCode);
        Assert.Equal("ja", loaded.OtherLanguageCode);
        Assert.True(loaded.UseAiTranslation);
        Assert.False(loaded.UseCloudAsr);
        Assert.Equal(TranslationBackend.DashScope, loaded.TranslationBackend);
        Assert.Equal(SpeechProtocol.OpenAiCompatible, loaded.SpeechProtocol);
        Assert.Equal("legacy-translation-key", loaded.TranslationApiKey);
        Assert.Equal("legacy-speech-key", loaded.SpeechApiKey);
        Assert.Equal("legacy-header-value", loaded.TranslationHeaders["x-legacy"]);
        Assert.Equal("legacy-voice-value", loaded.SpeechHeaders["x-voice"]);
        Assert.Equal("base", loaded.WhisperModel);
        Assert.Equal(0.024, loaded.VoiceThreshold, precision: 3);
        Assert.Equal(900, loaded.SilenceDurationMs);
        Assert.False(loaded.SpeakMyTranslation);
        Assert.Equal(QuickStartMode.OscText, loaded.QuickStartMode);
        Assert.False(loaded.ShowOverlay);
        Assert.True(File.Exists(PathFor("settings.json")));
        Assert.True(File.Exists(PathFor("secrets.dat")));

        var migratedPublicJson = await File.ReadAllTextAsync(PathFor("settings.json"));
        Assert.DoesNotContain("legacy-translation-key", migratedPublicJson, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-header-value", migratedPublicJson, StringComparison.Ordinal);
        Assert.Contains("\"quickStartMode\": \"OscText\"", migratedPublicJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_InfersServiceSwitchesFromLegacyProviderSelection()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            PathFor("settings.json"),
            JsonSerializer.Serialize(new
            {
                translationBackend = "deepSeek",
                asrProvider = "soniox",
                asrProtocol = "sonioxStreaming"
            }));

        var loaded = await CreateRepository().LoadAsync();

        Assert.True(loaded.UseAiTranslation);
        Assert.True(loaded.UseCloudAsr);
        var migratedJson = await File.ReadAllTextAsync(PathFor("settings.json"));
        Assert.Contains("\"useAiTranslation\": true", migratedJson, StringComparison.Ordinal);
        Assert.Contains("\"useCloudAsr\": true", migratedJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, QuickStartMode.OscText)]
    [InlineData(true, QuickStartMode.VrChatVoice)]
    public async Task LoadAsync_InfersQuickStartModeWhenLegacyFieldIsMissing(
        bool speakMyTranslation,
        QuickStartMode expectedMode)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            PathFor("settings.json"),
            JsonSerializer.Serialize(new { speakMyTranslation }));

        var loaded = await CreateRepository().LoadAsync();

        Assert.Equal(expectedMode, loaded.QuickStartMode);
        Assert.Equal(speakMyTranslation, loaded.SpeakMyTranslation);
        var migratedJson = await File.ReadAllTextAsync(PathFor("settings.json"));
        Assert.Contains("quickStartMode", migratedJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("oscText", true, QuickStartMode.OscText, false)]
    [InlineData("vrChatVoice", false, QuickStartMode.VrChatVoice, true)]
    public async Task LoadAsync_PersistedQuickStartModeOverridesConflictingSpeechFlag(
        string quickStartMode,
        bool speakMyTranslation,
        QuickStartMode expectedMode,
        bool expectedSpeech)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            PathFor("settings.json"),
            $$"""{"quickStartMode":"{{quickStartMode}}","speakMyTranslation":{{speakMyTranslation.ToString().ToLowerInvariant()}}}""");

        var loaded = await CreateRepository().LoadAsync();

        Assert.Equal(expectedMode, loaded.QuickStartMode);
        Assert.Equal(expectedSpeech, loaded.SpeakMyTranslation);
        var normalizedJson = await File.ReadAllTextAsync(PathFor("settings.json"));
        Assert.Contains($"\"speakMyTranslation\": {expectedSpeech.ToString().ToLowerInvariant()}", normalizedJson, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private SettingsRepository CreateRepository() => new(
        settingsPath: PathFor("settings.json"),
        secretsPath: PathFor("secrets.dat"),
        legacyPreferencesPath: PathFor("legacy-preferences.json"),
        legacySecretsPath: PathFor("legacy-secrets.dat"));

    private string PathFor(string fileName) => Path.Combine(_directory, fileName);
}
