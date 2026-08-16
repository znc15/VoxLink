namespace VoxLink.Models;

public enum TranslationProvider
{
    GoogleWeb,
    LocalMiniCpm,
    ManagedHyMt,
    ManagedM2M100,
    ManagedSmall100,
    OpenAiCompatible,
    DashScope,
    DeepSeek,
    Custom
}

public enum AsrProvider
{
    LocalWhisper,
    DashScope,
    Soniox,
    SiliconFlow,
    MiMo,
    OpenAiCompatible,
    Custom
}

public enum AsrProtocol
{
    LocalWhisper,
    LocalSenseVoice,
    LocalManagedMoss,
    DashScopeStreaming,
    SonioxStreaming,
    OpenAiMultipart,
    MiMoInputAudio
}

public enum SpeakerLabelMode
{
    Off,
    Local,
    Cloud
}

public enum OutboundSpeechContent
{
    Translation,
    Original
}

public enum ManagedTtsModel
{
    DotsTts,
    Qwen3Tts
}

public sealed class AppSettings
{
    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.OpenAiHeaders = new Dictionary<string, string>(
            OpenAiHeaders,
            StringComparer.OrdinalIgnoreCase);
        clone.TextToSpeechHeaders = new Dictionary<string, string>(
            TextToSpeechHeaders,
            StringComparer.OrdinalIgnoreCase);
        clone.AsrHeaders = new Dictionary<string, string>(
            AsrHeaders,
            StringComparer.OrdinalIgnoreCase);
        return clone;
    }

    public string MyLanguageCode { get; set; } = "zh";

    public string OtherLanguageCode { get; set; } = "en";

    public string SecondaryTargetLanguageCode { get; set; } = string.Empty;

    public bool CaptureMicrophone { get; set; } = true;

    public bool CaptureSystemAudio { get; set; } = true;

    public string MicrophoneDeviceId { get; set; } = string.Empty;

    public string SystemAudioDeviceId { get; set; } = string.Empty;

    public string VoiceOutputDeviceId { get; set; } = string.Empty;

    public TranslationProvider TranslationProvider { get; set; } = TranslationProvider.GoogleWeb;

    public string OpenAiBaseUrl { get; set; } = "http://localhost:11434/v1";

    public string OpenAiApiKey { get; set; } = string.Empty;

    public string OpenAiModel { get; set; } = "qwen2.5:7b";

    public IReadOnlyDictionary<string, string> OpenAiHeaders { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool EnableTranslationRefinement { get; set; }
    public string TranslationRefinementPrompt { get; set; } = string.Empty;

    public bool SpeechRefinementEnabled { get; set; }
    public string SpeechRefinementPrompt { get; set; } = string.Empty;
    public AsrProvider AsrProvider { get; set; } = AsrProvider.LocalWhisper;

    public AsrProtocol AsrProtocol { get; set; } = AsrProtocol.LocalWhisper;

    public string AsrBaseUrl { get; set; } = string.Empty;

    public string AsrApiKey { get; set; } = string.Empty;

    public string AsrModel { get; set; } = string.Empty;

    public IReadOnlyDictionary<string, string> AsrHeaders { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool AllowCloudAudioUpload { get; set; }

    public bool UseRemoteTextToSpeech { get; set; }

    public bool UseLocalKokoroTextToSpeech { get; set; }

    public ManagedTtsModel? ManagedTtsModel { get; set; }

    public string ManagedTtsReferenceAudioPath { get; set; } = string.Empty;

    public string ManagedTtsReferenceText { get; set; } = string.Empty;

    public int KokoroSpeakerId { get; set; } = 3;

    public double KokoroSpeed { get; set; } = 1.0;

    public string TextToSpeechBaseUrl { get; set; } =
        "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation";

    public string TextToSpeechApiKey { get; set; } = string.Empty;

    public string TextToSpeechModel { get; set; } = "qwen3-tts-flash";

    public string TextToSpeechVoice { get; set; } = "Cherry";

    public string TextToSpeechProtocol { get; set; } = "dashscope";

    public IReadOnlyDictionary<string, string> TextToSpeechHeaders { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string WhisperModel { get; set; } = "tiny";

    public double VoiceThreshold { get; set; } = 0.018;

    public int SilenceDurationMs { get; set; } = 650;

    public bool SmartSentenceSegmentation { get; set; } = true;

    public bool TranscriptionOnly { get; set; }

    public SpeakerLabelMode SpeakerLabelMode { get; set; } = SpeakerLabelMode.Off;

    public string SpeakerEmbeddingModel { get; set; } = "3dspeaker-zh-en";

    public OutboundSpeechContent OutboundSpeechContent { get; set; } = OutboundSpeechContent.Translation;

    public bool SpeakMyTranslation { get; set; } = true;

    public bool SpeakInboundTranslation { get; set; }

    public bool ShowOverlay { get; set; } = true;

    public bool ShowVrOverlay { get; set; }

    public double VrOverlayWidthMeters { get; set; } = 1.6;

    public double VrOverlayDistanceMeters { get; set; } = 1.8;

    public double VrOverlayVerticalOffsetMeters { get; set; } = -0.35;

    public bool VrChatChatboxEnabled { get; set; }

    public string VrChatOscAddress { get; set; } = "127.0.0.1";

    public int VrChatOscPort { get; set; } = 9000;

    public bool VrChatIncludeSourceText { get; set; }

    public bool VrChatMuteSelfEnabled { get; set; }

    public string VrChatOscListenAddress { get; set; } = "127.0.0.1";

    public int VrChatOscListenPort { get; set; } = 9001;

    public string ToggleHotkey { get; set; } = "Ctrl+Alt+Space";

    public string TranslateHotkey { get; set; } = "Ctrl+Alt+Enter";

    public double? DesktopOverlayLeft { get; set; }

    public double? DesktopOverlayTop { get; set; }

    public double? DesktopOverlayWidth { get; set; }

    public bool DesktopOverlayTopmost { get; set; } = true;

    public bool DesktopOverlayLockPosition { get; set; } = true;

    public string LocalModelDirectory { get; set; } = string.Empty;

    public string ManagedRuntimeDirectory { get; set; } = string.Empty;
}
