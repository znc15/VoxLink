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
            ConfirmOnClose = true,
            DesktopOverlayLeft = 150,
            DesktopOverlayTop = 380,
            DesktopOverlayWidth = 860,
            DesktopOverlayHeight = 460,
            DesktopOverlayFontSize = 30,
            DesktopOverlayTopmost = false,
            DesktopOverlayLockPosition = false,
            LocalModelDirectory = @"D:\VoxLinkModels",
            ManagedRuntimeDirectory = @"E:\VoxLinkRuntimes"
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
        Assert.True(loaded.OnboardingCompleted);
        Assert.Equal(OutboundSpeechContent.Original, loaded.OutboundSpeechContent);
        Assert.True(loaded.SpeakMyTranslation);
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
        Assert.Equal(150, loaded.DesktopOverlayLeft);
        Assert.Equal(380, loaded.DesktopOverlayTop);
        Assert.Equal(860, loaded.DesktopOverlayWidth);
        Assert.Equal(460, loaded.DesktopOverlayHeight);
        Assert.Equal(30, loaded.DesktopOverlayFontSize);
        Assert.False(loaded.DesktopOverlayTopmost);
        Assert.False(loaded.DesktopOverlayLockPosition);
        Assert.Equal(@"D:\VoxLinkModels", loaded.LocalModelDirectory);
        Assert.Equal(@"E:\VoxLinkRuntimes", loaded.ManagedRuntimeDirectory);
        Assert.False(loaded.MinimizeToTray);
        Assert.True(loaded.ConfirmOnClose);
        Assert.Contains("vrChatOscAddress", publicJson, StringComparison.Ordinal);
        Assert.Contains("secondaryTargetLanguageCode", publicJson, StringComparison.Ordinal);
        Assert.Contains("speakerLabelMode", publicJson, StringComparison.Ordinal);
        Assert.DoesNotContain("quickStartMode", publicJson, StringComparison.Ordinal);
        Assert.Contains("outboundSpeechContent", publicJson, StringComparison.Ordinal);
        Assert.Contains("minimizeToTray", publicJson, StringComparison.Ordinal);
        Assert.Contains("confirmOnClose", publicJson, StringComparison.Ordinal);
        Assert.Contains("desktopOverlayHeight", publicJson, StringComparison.Ordinal);
        Assert.Contains("desktopOverlayFontSize", publicJson, StringComparison.Ordinal);
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
        Assert.False(loaded.ShowOverlay);
        Assert.True(File.Exists(PathFor("settings.json")));
        Assert.True(File.Exists(PathFor("secrets.dat")));

        var migratedPublicJson = await File.ReadAllTextAsync(PathFor("settings.json"));
        Assert.DoesNotContain("legacy-translation-key", migratedPublicJson, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-header-value", migratedPublicJson, StringComparison.Ordinal);
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadAsync_KeepsPersistedSpeakMyTranslationWithoutInference(
        bool speakMyTranslation)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            PathFor("settings.json"),
            JsonSerializer.Serialize(new { speakMyTranslation }));

        var loaded = await CreateRepository().LoadAsync();

        Assert.Equal(speakMyTranslation, loaded.SpeakMyTranslation);
        var migratedJson = await File.ReadAllTextAsync(PathFor("settings.json"));
        Assert.DoesNotContain("quickStartMode", migratedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_RoundTripsLocalModelSettingsAsPublicSettings()
    {
        var repository = CreateRepository();
        var settings = new AppSettings
        {
            UseAiTranslation = true,
            TranslationBackend = TranslationBackend.LocalMiniCpm,
            UseLocalKokoroTextToSpeech = true,
            KokoroSpeakerId = 42,
            KokoroSpeed = 1.25
        };

        await repository.SaveAsync(settings);

        var publicJson = await File.ReadAllTextAsync(PathFor("settings.json"));
        var loaded = await repository.LoadAsync();

        Assert.Equal(TranslationBackend.LocalMiniCpm, loaded.TranslationBackend);
        Assert.True(loaded.UseAiTranslation);
        Assert.True(loaded.UseLocalKokoroTextToSpeech);
        Assert.Equal(42, loaded.KokoroSpeakerId);
        Assert.Equal(1.25, loaded.KokoroSpeed, precision: 3);
        Assert.Contains("\"translationBackend\": \"LocalMiniCpm\"", publicJson, StringComparison.Ordinal);
        Assert.Contains("\"useLocalKokoroTextToSpeech\": true", publicJson, StringComparison.Ordinal);
        Assert.Contains("\"kokoroSpeakerId\": 42", publicJson, StringComparison.Ordinal);
        Assert.Contains("\"kokoroSpeed\": 1.25", publicJson, StringComparison.Ordinal);

        // 本地模型字段属于公开设置：secrets.dat 解密后不应包含它们。
        var protectedBytes = await File.ReadAllBytesAsync(PathFor("secrets.dat"));
        var secretJson = Encoding.UTF8.GetString(ProtectedData.Unprotect(
            protectedBytes,
            Encoding.UTF8.GetBytes("VoxLink.UI.Secrets.v1"),
            DataProtectionScope.CurrentUser));
        Assert.DoesNotContain("kokoro", secretJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localMiniCpm", secretJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_NormalizesConflictingServiceSelectionsWithoutLosingSecrets()
    {
        var repository = CreateRepository();
        var settings = new AppSettings
        {
            UseAiTranslation = false,
            TranslationBackend = TranslationBackend.DeepSeek,
            TranslationBaseUrl = "https://translation.example/v1",
            TranslationModel = "saved-model",
            TranslationApiKey = "saved-translation-key",
            TranslationHeaders = new Dictionary<string, string> { ["X-Translation"] = "saved-header" },
            UseCloudAsr = false,
            AsrProvider = AsrProvider.Soniox,
            AsrProtocol = AsrProtocol.SonioxStreaming,
            AsrBaseUrl = "wss://stt-rt.soniox.com/transcribe-websocket",
            AsrModel = "stt-rt-v5",
            AsrApiKey = "saved-asr-key",
            AsrHeaders = new Dictionary<string, string> { ["X-ASR"] = "saved-asr-header" },
            AllowCloudAudioUpload = true,
            UseRemoteSpeech = true,
            UseLocalKokoroTextToSpeech = true,
            SpeechApiKey = "saved-speech-key"
        };
        await repository.SaveAsync(settings);

        var loaded = await repository.LoadAsync();

        Assert.False(loaded.UseAiTranslation);
        Assert.Equal(TranslationBackend.PublicFree, loaded.TranslationBackend);
        Assert.Equal("https://translation.example/v1", loaded.TranslationBaseUrl);
        Assert.Equal("saved-model", loaded.TranslationModel);
        Assert.Equal("saved-translation-key", loaded.TranslationApiKey);
        Assert.Equal("saved-header", loaded.TranslationHeaders["X-Translation"]);
        Assert.False(loaded.UseCloudAsr);
        Assert.Equal(AsrProvider.LocalWhisper, loaded.AsrProvider);
        Assert.Equal(AsrProtocol.LocalWhisper, loaded.AsrProtocol);
        Assert.False(loaded.AllowCloudAudioUpload);
        Assert.Equal("wss://stt-rt.soniox.com/transcribe-websocket", loaded.AsrBaseUrl);
        Assert.Equal("stt-rt-v5", loaded.AsrModel);
        Assert.Equal("saved-asr-key", loaded.AsrApiKey);
        Assert.Equal("saved-asr-header", loaded.AsrHeaders["X-ASR"]);
        Assert.False(loaded.UseRemoteSpeech);
        Assert.True(loaded.UseLocalKokoroTextToSpeech);
        Assert.Equal("saved-speech-key", loaded.SpeechApiKey);
    }


    [Fact]
    public async Task SaveAsync_WritesMatchingGenerationToPublicAndSecretFiles()
    {
        var repository = CreateRepository();
        await repository.SaveAsync(new AppSettings { TranslationApiKey = "secret" });

        using var publicDocument = JsonDocument.Parse(
            await File.ReadAllBytesAsync(PathFor("settings.json")));
        var publicGeneration = publicDocument.RootElement
            .GetProperty("settingsGeneration").GetString();
        var secretJson = ProtectedData.Unprotect(
            await File.ReadAllBytesAsync(PathFor("secrets.dat")),
            Encoding.UTF8.GetBytes("VoxLink.UI.Secrets.v1"),
            DataProtectionScope.CurrentUser);
        using var secretDocument = JsonDocument.Parse(secretJson);
        var secretGeneration = secretDocument.RootElement
            .GetProperty("voxlink.settings.generation").GetString();

        Assert.False(string.IsNullOrWhiteSpace(publicGeneration));
        Assert.Equal(publicGeneration, secretGeneration);
    }

    [Fact]
    public async Task LoadAsync_RejectsMismatchedGenerationBeforeCombiningSecrets()
    {
        var repository = CreateRepository();
        await repository.SaveAsync(new AppSettings
        {
            TranslationBaseUrl = "https://provider-a.example/v1",
            TranslationApiKey = "secret-a"
        });
        var oldSecrets = await File.ReadAllBytesAsync(PathFor("secrets.dat"));
        await repository.SaveAsync(new AppSettings
        {
            TranslationBaseUrl = "https://provider-b.example/v1",
            TranslationApiKey = "secret-b"
        });
        await File.WriteAllBytesAsync(PathFor("secrets.dat"), oldSecrets);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadAsync());

        Assert.Contains("版本不一致", error.Message);
        Assert.Contains(
            "provider-b.example",
            await File.ReadAllTextAsync(PathFor("settings.json")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentRepositories_SaveWithoutGenerationOrCredentialMismatch()
    {
        var first = CreateRepository();
        var second = CreateRepository();
        var saves = Enumerable.Range(0, 12).Select(index =>
        {
            var settings = new AppSettings
            {
                TranslationBaseUrl = $"https://provider-{index}.example/v1",
                TranslationApiKey = $"secret-{index}"
            };
            return (index % 2 == 0 ? first : second).SaveAsync(settings);
        });

        await Task.WhenAll(saves);
        var loaded = await CreateRepository().LoadAsync();

        var hostIndex = loaded.TranslationBaseUrl
            .Split("provider-", StringSplitOptions.None)[1]
            .Split('.', StringSplitOptions.None)[0];
        Assert.Equal($"secret-{hostIndex}", loaded.TranslationApiKey);
    }

    [Fact]
    public async Task LoadAsync_RetiredManagedBackendsFallBackToSafeDefaults()
    {
        // 旧版本 settings.json 指向已下线的托管翻译/ASR 模型：
        // 加载时必须安全回退公共免密 + 本地 Whisper，不得崩溃或保留无效选择。
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            PathFor("settings.json"),
            JsonSerializer.Serialize(new
            {
                useAiTranslation = true,
                translationBackend = "managedHyMt",
                useCloudAsr = true,
                asrProvider = "localManagedMoss",
                asrProtocol = "localManagedMoss"
            }));

        var loaded = await CreateRepository().LoadAsync();

        Assert.False(loaded.UseAiTranslation);
        Assert.Equal(TranslationBackend.PublicFree, loaded.TranslationBackend);
        Assert.False(loaded.UseCloudAsr);
        Assert.Equal(AsrProvider.LocalWhisper, loaded.AsrProvider);
        Assert.Equal(AsrProtocol.LocalWhisper, loaded.AsrProtocol);

        var migratedJson = await File.ReadAllTextAsync(PathFor("settings.json"));
        Assert.Contains("\"translationBackend\": \"PublicFree\"", migratedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpeechRefinementEnabled_RoundTripsAndReachesEngineJson()
    {
        var repository = CreateRepository();
        var settings = new AppSettings
        {
            SpeechRefinementEnabled = true,
            UseAiTranslation = true,
            TranslationBackend = TranslationBackend.DeepSeek
        };

        await repository.SaveAsync(settings);
        var loaded = await repository.LoadAsync();

        Assert.True(loaded.SpeechRefinementEnabled);
        Assert.True(Assert.IsType<bool>(
            loaded.ToEngineJson()["speechRefinementEnabled"]));
    }

    [Fact]
    public async Task TranscriptionCleanup_RoundTripsAndReachesEngineJson()
    {
        var repository = CreateRepository();
        var settings = new AppSettings
        {
            TranscriptionCleanupEnabled = true,
            TranscriptionCleanupPrompt = "只修正明显口误，不改变原意。",
            UseAiTranslation = true,
            TranslationBackend = TranslationBackend.DeepSeek
        };

        await repository.SaveAsync(settings);
        var loaded = await repository.LoadAsync();

        Assert.True(loaded.TranscriptionCleanupEnabled);
        Assert.Equal("只修正明显口误，不改变原意。", loaded.TranscriptionCleanupPrompt);
        Assert.True(Assert.IsType<bool>(
            loaded.ToEngineJson()["transcriptionCleanupEnabled"]));
        Assert.Equal(
            "只修正明显口误，不改变原意。",
            Assert.IsType<string>(loaded.ToEngineJson()["transcriptionCleanupPrompt"]));
    }

    [Fact]
    public async Task LoadAsync_CorruptCurrentSettingsDoesNotMigrateOrOverwrite()
    {
        Directory.CreateDirectory(_directory);
        var corruptSettings = Encoding.UTF8.GetBytes("{not-valid-json");
        await File.WriteAllBytesAsync(PathFor("settings.json"), corruptSettings);
        await File.WriteAllTextAsync(
            PathFor("legacy-preferences.json"),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["flutter.voxlink.settings.v2"] = JsonSerializer.Serialize(new
                {
                    myLanguageCode = "ja",
                    translationBackend = "deepSeek"
                })
            }));

        var error = await Assert.ThrowsAnyAsync<JsonException>(() => CreateRepository().LoadAsync());
        Assert.NotEmpty(error.Message);

        Assert.Equal(corruptSettings, await File.ReadAllBytesAsync(PathFor("settings.json")));
        Assert.False(File.Exists(PathFor("secrets.dat")));
    }

    [Fact]
    public async Task LoadAsync_CorruptCurrentSecretsDoesNotMigrateOrOverwrite()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(PathFor("settings.json"), "{}");
        var corruptSecrets = Encoding.UTF8.GetBytes("not-dpapi-data");
        await File.WriteAllBytesAsync(PathFor("secrets.dat"), corruptSecrets);
        var legacySecretsJson = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["voxlink.translation.apiKey"] = "legacy-key"
        });
        await File.WriteAllBytesAsync(
            PathFor("legacy-secrets.dat"),
            ProtectedData.Protect(
                legacySecretsJson,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser));

        await Assert.ThrowsAsync<CryptographicException>(() => CreateRepository().LoadAsync());

        Assert.Equal(corruptSecrets, await File.ReadAllBytesAsync(PathFor("secrets.dat")));
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
