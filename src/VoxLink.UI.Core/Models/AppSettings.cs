using System.Text.Json;
using System.Text.Json.Serialization;
using VoxLink.UI.Core.Infrastructure;

namespace VoxLink.UI.Core.Models;

public enum TranslationBackend
{
    PublicFree,
    DashScope,
    DeepSeek,
    OpenAiCompatible,
    Custom
}

public enum SpeechProtocol
{
    DashScope,
    MiMo,
    OpenAiCompatible
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

public enum QuickStartMode
{
    OscText,
    VrChatVoice
}

public enum OutboundSpeechContent
{
    Translation,
    Original
}

public sealed class AppSettings : ObservableObject
{
    private QuickStartMode _quickStartMode = QuickStartMode.OscText;
    private bool _onboardingCompleted;
    private string _myLanguageCode = "zh";
    private string _otherLanguageCode = "en";
    private string _secondaryTargetLanguageCode = string.Empty;
    private bool _captureMicrophone = true;
    private bool _captureSystemAudio;
    private string _microphoneDeviceId = string.Empty;
    private string _systemAudioDeviceId = string.Empty;
    private string _voiceOutputDeviceId = string.Empty;
    private TranslationBackend _translationBackend = TranslationBackend.PublicFree;
    private string _translationBaseUrl = "http://localhost:11434/v1";
    private string _translationApiKey = string.Empty;
    private string _translationModel = "qwen2.5:7b";
    private Dictionary<string, string> _translationHeaders = new(StringComparer.OrdinalIgnoreCase);
    private bool _enableTranslationRefinement;
    private string _translationRefinementPrompt = string.Empty;
    private AsrProvider _asrProvider = AsrProvider.LocalWhisper;
    private AsrProtocol _asrProtocol = AsrProtocol.LocalWhisper;
    private string _asrBaseUrl = string.Empty;
    private string _asrApiKey = string.Empty;
    private string _asrModel = string.Empty;
    private Dictionary<string, string> _asrHeaders = new(StringComparer.OrdinalIgnoreCase);
    private bool _allowCloudAudioUpload;
    private bool _useRemoteSpeech;
    private SpeechProtocol _speechProtocol = SpeechProtocol.DashScope;
    private string _speechBaseUrl = "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation";
    private string _speechApiKey = string.Empty;
    private string _speechModel = "qwen3-tts-flash";
    private string _speechVoice = "Cherry";
    private Dictionary<string, string> _speechHeaders = new(StringComparer.OrdinalIgnoreCase);
    private string _whisperModel = "tiny";
    private double _voiceThreshold = 0.018;
    private int _silenceDurationMs = 650;
    private bool _smartSentenceSegmentation = true;
    private bool _transcriptionOnly;
    private SpeakerLabelMode _speakerLabelMode;
    private string _speakerEmbeddingModel = "3dspeaker-zh-en";
    private OutboundSpeechContent _outboundSpeechContent = OutboundSpeechContent.Translation;
    private bool _speakMyTranslation;
    private bool _speakInboundTranslation;
    private bool _showOverlay = true;
    private bool _showVrOverlay;
    private double _vrOverlayWidthMeters = 1.6;
    private double _vrOverlayDistanceMeters = 1.8;
    private double _vrOverlayVerticalOffsetMeters = -0.35;
    private bool _vrChatChatboxEnabled = true;
    private string _vrChatOscAddress = "127.0.0.1";
    private int _vrChatOscPort = 9000;
    private bool _vrChatIncludeSourceText;
    private bool _vrChatMuteSelfEnabled;
    private string _vrChatOscListenAddress = "127.0.0.1";
    private int _vrChatOscListenPort = 9001;
    private string _toggleHotkey = "Ctrl+Alt+Space";
    private string _translateHotkey = "Ctrl+Alt+Enter";

    [JsonConverter(typeof(JsonStringEnumConverter<QuickStartMode>))]
    public QuickStartMode QuickStartMode { get => _quickStartMode; set => SetProperty(ref _quickStartMode, value); }
    public bool OnboardingCompleted { get => _onboardingCompleted; set => SetProperty(ref _onboardingCompleted, value); }

    public string MyLanguageCode { get => _myLanguageCode; set => SetProperty(ref _myLanguageCode, value); }
    public string OtherLanguageCode { get => _otherLanguageCode; set => SetProperty(ref _otherLanguageCode, value); }
    public string SecondaryTargetLanguageCode { get => _secondaryTargetLanguageCode; set => SetProperty(ref _secondaryTargetLanguageCode, value); }
    public bool CaptureMicrophone { get => _captureMicrophone; set => SetProperty(ref _captureMicrophone, value); }
    public bool CaptureSystemAudio { get => _captureSystemAudio; set => SetProperty(ref _captureSystemAudio, value); }
    public string MicrophoneDeviceId { get => _microphoneDeviceId; set => SetProperty(ref _microphoneDeviceId, value); }
    public string SystemAudioDeviceId { get => _systemAudioDeviceId; set => SetProperty(ref _systemAudioDeviceId, value); }
    public string VoiceOutputDeviceId { get => _voiceOutputDeviceId; set => SetProperty(ref _voiceOutputDeviceId, value); }

    [JsonConverter(typeof(JsonStringEnumConverter<TranslationBackend>))]
    public TranslationBackend TranslationBackend
    {
        get => _translationBackend;
        set
        {
            if (SetProperty(ref _translationBackend, value))
            {
                if (value == TranslationBackend.PublicFree)
                {
                    EnableTranslationRefinement = false;
                }

                OnPropertyChanged(nameof(SupportsGeneration));
            }
        }
    }

    public string TranslationBaseUrl { get => _translationBaseUrl; set => SetProperty(ref _translationBaseUrl, value); }

    [JsonIgnore]
    public string TranslationApiKey { get => _translationApiKey; set => SetProperty(ref _translationApiKey, value); }

    public string TranslationModel { get => _translationModel; set => SetProperty(ref _translationModel, value); }

    [JsonIgnore]
    public Dictionary<string, string> TranslationHeaders
    {
        get => _translationHeaders;
        set => SetProperty(ref _translationHeaders, new(value ?? [], StringComparer.OrdinalIgnoreCase));
    }

    public bool EnableTranslationRefinement
    {
        get => _enableTranslationRefinement;
        set => SetProperty(
            ref _enableTranslationRefinement,
            value && TranslationBackend != TranslationBackend.PublicFree);
    }
    public string TranslationRefinementPrompt { get => _translationRefinementPrompt; set => SetProperty(ref _translationRefinementPrompt, value); }

    [JsonConverter(typeof(JsonStringEnumConverter<AsrProvider>))]
    public AsrProvider AsrProvider
    {
        get => _asrProvider;
        set
        {
            if (SetProperty(ref _asrProvider, value))
            {
                RaiseAsrCapabilityProperties();
            }
        }
    }

    [JsonConverter(typeof(JsonStringEnumConverter<AsrProtocol>))]
    public AsrProtocol AsrProtocol
    {
        get => _asrProtocol;
        set
        {
            if (SetProperty(ref _asrProtocol, value))
            {
                RaiseAsrCapabilityProperties();
            }
        }
    }

    public string AsrBaseUrl { get => _asrBaseUrl; set => SetProperty(ref _asrBaseUrl, value); }

    [JsonIgnore]
    public string AsrApiKey { get => _asrApiKey; set => SetProperty(ref _asrApiKey, value); }

    public string AsrModel { get => _asrModel; set => SetProperty(ref _asrModel, value); }

    [JsonIgnore]
    public Dictionary<string, string> AsrHeaders
    {
        get => _asrHeaders;
        set => SetProperty(ref _asrHeaders, new(value ?? [], StringComparer.OrdinalIgnoreCase));
    }

    public bool AllowCloudAudioUpload { get => _allowCloudAudioUpload; set => SetProperty(ref _allowCloudAudioUpload, value); }
    public bool UseRemoteSpeech { get => _useRemoteSpeech; set => SetProperty(ref _useRemoteSpeech, value); }

    [JsonConverter(typeof(JsonStringEnumConverter<SpeechProtocol>))]
    public SpeechProtocol SpeechProtocol { get => _speechProtocol; set => SetProperty(ref _speechProtocol, value); }

    public string SpeechBaseUrl { get => _speechBaseUrl; set => SetProperty(ref _speechBaseUrl, value); }

    [JsonIgnore]
    public string SpeechApiKey { get => _speechApiKey; set => SetProperty(ref _speechApiKey, value); }

    public string SpeechModel { get => _speechModel; set => SetProperty(ref _speechModel, value); }
    public string SpeechVoice { get => _speechVoice; set => SetProperty(ref _speechVoice, value); }

    [JsonIgnore]
    public Dictionary<string, string> SpeechHeaders
    {
        get => _speechHeaders;
        set => SetProperty(ref _speechHeaders, new(value ?? [], StringComparer.OrdinalIgnoreCase));
    }

    public string WhisperModel { get => _whisperModel; set => SetProperty(ref _whisperModel, value); }
    public double VoiceThreshold { get => _voiceThreshold; set => SetProperty(ref _voiceThreshold, Math.Clamp(value, 0.005, 0.08)); }
    public int SilenceDurationMs { get => _silenceDurationMs; set => SetProperty(ref _silenceDurationMs, Math.Clamp(value, 300, 1800)); }
    public bool SmartSentenceSegmentation { get => _smartSentenceSegmentation; set => SetProperty(ref _smartSentenceSegmentation, value); }
    public bool TranscriptionOnly { get => _transcriptionOnly; set => SetProperty(ref _transcriptionOnly, value); }

    [JsonConverter(typeof(JsonStringEnumConverter<SpeakerLabelMode>))]
    public SpeakerLabelMode SpeakerLabelMode { get => _speakerLabelMode; set => SetProperty(ref _speakerLabelMode, value); }

    public string SpeakerEmbeddingModel { get => _speakerEmbeddingModel; set => SetProperty(ref _speakerEmbeddingModel, value); }

    [JsonConverter(typeof(JsonStringEnumConverter<OutboundSpeechContent>))]
    public OutboundSpeechContent OutboundSpeechContent { get => _outboundSpeechContent; set => SetProperty(ref _outboundSpeechContent, value); }
    public bool SpeakMyTranslation { get => _speakMyTranslation; set => SetProperty(ref _speakMyTranslation, value); }
    public bool SpeakInboundTranslation { get => _speakInboundTranslation; set => SetProperty(ref _speakInboundTranslation, value); }
    public bool ShowOverlay { get => _showOverlay; set => SetProperty(ref _showOverlay, value); }
    public bool ShowVrOverlay { get => _showVrOverlay; set => SetProperty(ref _showVrOverlay, value); }
    public double VrOverlayWidthMeters { get => _vrOverlayWidthMeters; set => SetProperty(ref _vrOverlayWidthMeters, Math.Clamp(value, 0.6, 3.0)); }
    public double VrOverlayDistanceMeters { get => _vrOverlayDistanceMeters; set => SetProperty(ref _vrOverlayDistanceMeters, Math.Clamp(value, 0.6, 4.0)); }
    public double VrOverlayVerticalOffsetMeters { get => _vrOverlayVerticalOffsetMeters; set => SetProperty(ref _vrOverlayVerticalOffsetMeters, Math.Clamp(value, -1.0, 0.5)); }
    public bool VrChatChatboxEnabled { get => _vrChatChatboxEnabled; set => SetProperty(ref _vrChatChatboxEnabled, value); }
    public string VrChatOscAddress { get => _vrChatOscAddress; set => SetProperty(ref _vrChatOscAddress, value); }
    public int VrChatOscPort { get => _vrChatOscPort; set => SetProperty(ref _vrChatOscPort, Math.Clamp(value, 1, 65_535)); }
    public bool VrChatIncludeSourceText { get => _vrChatIncludeSourceText; set => SetProperty(ref _vrChatIncludeSourceText, value); }
    public bool VrChatMuteSelfEnabled { get => _vrChatMuteSelfEnabled; set => SetProperty(ref _vrChatMuteSelfEnabled, value); }
    public string VrChatOscListenAddress { get => _vrChatOscListenAddress; set => SetProperty(ref _vrChatOscListenAddress, value); }
    public int VrChatOscListenPort { get => _vrChatOscListenPort; set => SetProperty(ref _vrChatOscListenPort, Math.Clamp(value, 1, 65_535)); }
    public string ToggleHotkey { get => _toggleHotkey; set => SetProperty(ref _toggleHotkey, value); }
    public string TranslateHotkey { get => _translateHotkey; set => SetProperty(ref _translateHotkey, value); }

    [JsonIgnore]
    public bool SupportsGeneration => TranslationBackend != TranslationBackend.PublicFree;

    [JsonIgnore]
    public bool UsesCloudAsr => AsrProtocol != AsrProtocol.LocalWhisper;

    [JsonIgnore]
    public bool UsesStreamingAsr => AsrProtocol is AsrProtocol.DashScopeStreaming or AsrProtocol.SonioxStreaming;

    [JsonIgnore]
    public bool SupportsCloudSpeakerLabels => AsrProtocol == AsrProtocol.SonioxStreaming;

    public bool NormalizeQuickStartSettings(bool quickStartModeWasPersisted = true)
    {
        var voiceMode = quickStartModeWasPersisted
            ? QuickStartMode == QuickStartMode.VrChatVoice
            : SpeakMyTranslation;
        var normalizedMode = voiceMode ? QuickStartMode.VrChatVoice : QuickStartMode.OscText;
        var changed = QuickStartMode != normalizedMode || SpeakMyTranslation != voiceMode;
        QuickStartMode = normalizedMode;
        SpeakMyTranslation = voiceMode;
        return changed;
    }

    public void ApplyTranslationBackendDefaults(TranslationBackend backend)
    {
        TranslationBackend = backend;
        switch (backend)
        {
            case TranslationBackend.DashScope:
                TranslationBaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1";
                TranslationModel = "qwen-plus";
                break;
            case TranslationBackend.DeepSeek:
                TranslationBaseUrl = "https://api.deepseek.com";
                TranslationModel = "deepseek-v4-flash";
                break;
            case TranslationBackend.OpenAiCompatible:
                TranslationBaseUrl = "http://localhost:11434/v1";
                TranslationModel = "qwen2.5:7b";
                break;
        }
    }

    public void ApplyAsrProviderDefaults(AsrProvider provider)
    {
        AsrProvider = provider;
        switch (provider)
        {
            case AsrProvider.LocalWhisper:
                AsrProtocol = AsrProtocol.LocalWhisper;
                AsrBaseUrl = string.Empty;
                AsrModel = string.Empty;
                AllowCloudAudioUpload = false;
                break;
            case AsrProvider.DashScope:
                AsrProtocol = AsrProtocol.DashScopeStreaming;
                AsrBaseUrl = "wss://dashscope.aliyuncs.com/api-ws/v1/inference";
                AsrModel = "qwen-audio-3.0-asr-flash-streaming";
                break;
            case AsrProvider.Soniox:
                AsrProtocol = AsrProtocol.SonioxStreaming;
                AsrBaseUrl = "wss://stt-rt.soniox.com/transcribe-websocket";
                AsrModel = "stt-rt-v5";
                break;
            case AsrProvider.SiliconFlow:
                AsrProtocol = AsrProtocol.OpenAiMultipart;
                AsrBaseUrl = "https://api.siliconflow.cn/v1/audio/transcriptions";
                AsrModel = "FunAudioLLM/SenseVoiceSmall";
                break;
            case AsrProvider.MiMo:
                AsrProtocol = AsrProtocol.MiMoInputAudio;
                AsrBaseUrl = "https://api.xiaomimimo.com/v1/chat/completions";
                AsrModel = "mimo-v2.5-asr";
                break;
            case AsrProvider.OpenAiCompatible:
                AsrProtocol = AsrProtocol.OpenAiMultipart;
                AsrBaseUrl = "http://localhost:8000/v1/audio/transcriptions";
                AsrModel = "whisper-1";
                break;
            case AsrProvider.Custom:
                if (AsrProtocol == AsrProtocol.LocalWhisper)
                {
                    AsrProtocol = AsrProtocol.OpenAiMultipart;
                }
                break;
        }
    }

    public void ApplySpeechProtocolDefaults(SpeechProtocol protocol)
    {
        SpeechProtocol = protocol;
        switch (protocol)
        {
            case SpeechProtocol.DashScope:
                SpeechBaseUrl = "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation";
                SpeechModel = "qwen3-tts-flash";
                SpeechVoice = "Cherry";
                break;
            case SpeechProtocol.MiMo:
                SpeechBaseUrl = "https://api.xiaomimimo.com/v1/chat/completions";
                SpeechModel = "mimo-v2.5-tts";
                SpeechVoice = "mimo_default";
                break;
            case SpeechProtocol.OpenAiCompatible:
                SpeechBaseUrl = "http://localhost:8000/v1/audio/speech";
                SpeechModel = "tts-1";
                SpeechVoice = "alloy";
                break;
        }
    }

    public Dictionary<string, object?> ToEngineJson() => new(StringComparer.Ordinal)
    {
        ["myLanguageCode"] = MyLanguageCode,
        ["otherLanguageCode"] = OtherLanguageCode,
        ["secondaryTargetLanguageCode"] = SecondaryTargetLanguageCode,
        ["captureMicrophone"] = CaptureMicrophone,
        ["captureSystemAudio"] = CaptureSystemAudio,
        ["microphoneDeviceId"] = MicrophoneDeviceId,
        ["systemAudioDeviceId"] = SystemAudioDeviceId,
        ["voiceOutputDeviceId"] = VoiceOutputDeviceId,
        ["translationProvider"] = TranslationBackend switch
        {
            TranslationBackend.PublicFree => "googleWeb",
            TranslationBackend.DashScope => "dashScope",
            TranslationBackend.DeepSeek => "deepSeek",
            TranslationBackend.OpenAiCompatible => "openAiCompatible",
            _ => "custom"
        },
        ["openAiBaseUrl"] = TranslationBaseUrl,
        ["openAiApiKey"] = TranslationApiKey,
        ["openAiModel"] = TranslationModel,
        ["openAiHeaders"] = TranslationHeaders,
        ["enableTranslationRefinement"] = EnableTranslationRefinement,
        ["translationRefinementPrompt"] = TranslationRefinementPrompt,
        ["asrProvider"] = JsonNamingPolicy.CamelCase.ConvertName(AsrProvider.ToString()),
        ["asrProtocol"] = JsonNamingPolicy.CamelCase.ConvertName(AsrProtocol.ToString()),
        ["asrBaseUrl"] = AsrBaseUrl,
        ["asrApiKey"] = AsrApiKey,
        ["asrModel"] = AsrModel,
        ["asrHeaders"] = AsrHeaders,
        ["allowCloudAudioUpload"] = AllowCloudAudioUpload,
        ["useRemoteTextToSpeech"] = UseRemoteSpeech,
        ["textToSpeechBaseUrl"] = SpeechBaseUrl,
        ["textToSpeechApiKey"] = SpeechApiKey,
        ["textToSpeechModel"] = SpeechModel,
        ["textToSpeechVoice"] = SpeechVoice,
        ["textToSpeechProtocol"] = SpeechProtocol switch
        {
            SpeechProtocol.DashScope => "dashscope",
            SpeechProtocol.MiMo => "mimo",
            _ => "openai"
        },
        ["textToSpeechHeaders"] = SpeechHeaders,
        ["whisperModel"] = WhisperModel,
        ["voiceThreshold"] = VoiceThreshold,
        ["silenceDurationMs"] = SilenceDurationMs,
        ["smartSentenceSegmentation"] = SmartSentenceSegmentation,
        ["transcriptionOnly"] = TranscriptionOnly,
        ["speakerLabelMode"] = JsonNamingPolicy.CamelCase.ConvertName(SpeakerLabelMode.ToString()),
        ["speakerEmbeddingModel"] = SpeakerEmbeddingModel,
        ["outboundSpeechContent"] = JsonNamingPolicy.CamelCase.ConvertName(OutboundSpeechContent.ToString()),
        ["speakMyTranslation"] = SpeakMyTranslation,
        ["speakInboundTranslation"] = SpeakInboundTranslation,
        ["showOverlay"] = ShowOverlay,
        ["showVrOverlay"] = ShowVrOverlay,
        ["vrOverlayWidthMeters"] = VrOverlayWidthMeters,
        ["vrOverlayDistanceMeters"] = VrOverlayDistanceMeters,
        ["vrOverlayVerticalOffsetMeters"] = VrOverlayVerticalOffsetMeters,
        ["vrChatChatboxEnabled"] = VrChatChatboxEnabled,
        ["vrChatOscAddress"] = VrChatOscAddress,
        ["vrChatOscPort"] = VrChatOscPort,
        ["vrChatIncludeSourceText"] = VrChatIncludeSourceText,
        ["vrChatMuteSelfEnabled"] = VrChatMuteSelfEnabled,
        ["vrChatOscListenAddress"] = VrChatOscListenAddress,
        ["vrChatOscListenPort"] = VrChatOscListenPort,
        ["toggleHotkey"] = ToggleHotkey,
        ["translateHotkey"] = TranslateHotkey
    };

    private void RaiseAsrCapabilityProperties()
    {
        OnPropertyChanged(nameof(UsesCloudAsr));
        OnPropertyChanged(nameof(UsesStreamingAsr));
        OnPropertyChanged(nameof(SupportsCloudSpeakerLabels));
    }
}
