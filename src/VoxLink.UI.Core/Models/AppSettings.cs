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
    Custom,
    LocalMiniCpm,
    LocalHyMtGguf,

    // 已下线的应用托管翻译模型：仅用于兼容旧 settings.json 反序列化，
    // NormalizeServiceSelections 会将其安全回退为 PublicFree。
    ManagedHyMt,
    ManagedM2M100,
    ManagedSmall100
}

public enum ManagedTtsModel
{
    DotsTts,
    Qwen3Tts
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

    // 已下线的应用托管 MOSS 模型：仅用于兼容旧 settings.json 反序列化，
    // NormalizeServiceSelections 会将其安全回退为 LocalWhisper。
    LocalManagedMoss,

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
    LocalFireRedAsr2Ctc,
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

public enum SpeechServiceMode
{
    SystemFallback,
    Remote,
    Kokoro
}

/// <summary>桌面字幕悬浮窗的显示方式。</summary>
public enum DesktopOverlayDisplayMode
{
    /// <summary>开启悬浮窗后始终显示，不自动隐藏。</summary>
    AlwaysVisible,

    /// <summary>收到新字幕时显示，等待指定秒数后自动隐藏。</summary>
    AutoHide
}

/// <summary>麦克风语音增强引擎（可切换）。</summary>
public enum VoicePreprocessingMode
{
    /// <summary>关闭语音后处理。</summary>
    Off,

    /// <summary>WebRTC AudioProcessing Module：降噪 + 自动增益 + 高通（推荐）。</summary>
    WebRtc,

    /// <summary>RNNoise 神经网络降噪。</summary>
    RNNoise
}

public sealed class AppSettings : ObservableObject
{
    private bool _enableSpeechRefinement;
    private string _speechRefinementPrompt = "用口语化的方式改写这段话，像朋友聊天一样自然简洁，不要书面语和生硬的翻译腔。只返回改写后的内容。";
    private bool _onboardingCompleted;
    private string _myLanguageCode = "zh";
    private string _otherLanguageCode = "en";
    private string _secondaryTargetLanguageCode = string.Empty;
    private bool _captureMicrophone = true;
    private bool _captureSystemAudio;
    private string _microphoneDeviceId = string.Empty;
    private string _systemAudioDeviceId = string.Empty;
    private string _voiceOutputDeviceId = string.Empty;
    private bool _useAiTranslation;
    private TranslationBackend _translationBackend = TranslationBackend.PublicFree;
    private string _translationBaseUrl = "http://localhost:11434/v1";
    private string _translationApiKey = string.Empty;
    private string _translationModel = "qwen2.5:7b";
    private Dictionary<string, string> _translationHeaders = new(StringComparer.OrdinalIgnoreCase);
    private bool _enableTranslationRefinement;
    private string _translationRefinementPrompt = string.Empty;
    private bool _useCloudAsr;
    private AsrProvider _asrProvider = AsrProvider.LocalWhisper;
    private AsrProtocol _asrProtocol = AsrProtocol.LocalWhisper;
    private string _asrBaseUrl = string.Empty;
    private string _asrApiKey = string.Empty;
    private string _asrModel = string.Empty;
    private Dictionary<string, string> _asrHeaders = new(StringComparer.OrdinalIgnoreCase);
    private bool _allowCloudAudioUpload;
    private bool _useRemoteSpeech;
    private bool _useLocalKokoroTextToSpeech;
    private int _kokoroSpeakerId = 3;
    private double _kokoroSpeed = 1.0;
    private double _ttsOutputVolume = 1.0;
    private bool _enableVoiceMonitoring;
    private string _voiceMonitorDeviceId = string.Empty;
    private ManagedTtsModel? _managedTtsModel;
    private string _managedTtsReferenceAudioPath = string.Empty;
    private string _managedTtsReferenceText = string.Empty;
    private SpeechProtocol _speechProtocol = SpeechProtocol.DashScope;
    private string _speechBaseUrl = "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation";
    private string _speechApiKey = string.Empty;
    private string _speechModel = "qwen3-tts-flash";
    private string _speechVoice = "Cherry";
    private Dictionary<string, string> _speechHeaders = new(StringComparer.OrdinalIgnoreCase);
    private string _whisperModel = "base";
    private double _voiceThreshold = 0.018;
    private int _silenceDurationMs = 650;
    private bool _smartSentenceSegmentation = true;
    private VoicePreprocessingMode _voicePreprocessingMode = VoicePreprocessingMode.WebRtc;
    private bool _transcriptionOnly;
    private SpeakerLabelMode _speakerLabelMode;
    private string _speakerEmbeddingModel = "3dspeaker-zh-en";
    private OutboundSpeechContent _outboundSpeechContent = OutboundSpeechContent.Translation;
    private bool _speakMyTranslation;
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
    private bool _useMicaBackdrop = true;
    private bool _minimizeToTray = true;
    private bool _confirmOnClose = true;
    private double? _desktopOverlayLeft;
    private double? _desktopOverlayTop;
    private double? _desktopOverlayWidth;
    private double? _desktopOverlayHeight;
    private int _desktopOverlayFontSize = 24;
    private bool _desktopOverlayTopmost = true;
    private bool _desktopOverlayLockPosition = true;
    private DesktopOverlayDisplayMode _desktopOverlayDisplayMode = DesktopOverlayDisplayMode.AutoHide;
    private int _desktopOverlayAutoHideSeconds = 9;
    private string _localModelDirectory = string.Empty;
    private string _managedRuntimeDirectory = string.Empty;

    public bool OnboardingCompleted { get => _onboardingCompleted; set => SetProperty(ref _onboardingCompleted, value); }

    public string MyLanguageCode { get => _myLanguageCode; set => SetProperty(ref _myLanguageCode, value); }
    public bool SpeechRefinementEnabled { get => _enableSpeechRefinement; set => SetProperty(ref _enableSpeechRefinement, value); }
    public string SpeechRefinementPrompt { get => _speechRefinementPrompt; set => SetProperty(ref _speechRefinementPrompt, value); }
    public string OtherLanguageCode { get => _otherLanguageCode; set => SetProperty(ref _otherLanguageCode, value); }
    public string SecondaryTargetLanguageCode { get => _secondaryTargetLanguageCode; set => SetProperty(ref _secondaryTargetLanguageCode, value); }
    public bool CaptureMicrophone { get => _captureMicrophone; set => SetProperty(ref _captureMicrophone, value); }
    public bool CaptureSystemAudio { get => _captureSystemAudio; set => SetProperty(ref _captureSystemAudio, value); }
    public string MicrophoneDeviceId { get => _microphoneDeviceId; set => SetProperty(ref _microphoneDeviceId, value); }
    public string SystemAudioDeviceId { get => _systemAudioDeviceId; set => SetProperty(ref _systemAudioDeviceId, value); }
    public string VoiceOutputDeviceId { get => _voiceOutputDeviceId; set => SetProperty(ref _voiceOutputDeviceId, value); }

    public bool UseAiTranslation { get => _useAiTranslation; set => SetProperty(ref _useAiTranslation, value); }

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

    public bool UseCloudAsr { get => _useCloudAsr; set => SetProperty(ref _useCloudAsr, value); }

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
    public bool UseLocalKokoroTextToSpeech
    {
        get => _useLocalKokoroTextToSpeech;
        set => SetProperty(ref _useLocalKokoroTextToSpeech, value);
    }
    public int KokoroSpeakerId
    {
        get => _kokoroSpeakerId;
        set => SetProperty(ref _kokoroSpeakerId, Math.Clamp(value, 0, 102));
    }
    public double KokoroSpeed
    {
        get => _kokoroSpeed;
        set => SetProperty(
            ref _kokoroSpeed,
            Math.Clamp(double.IsFinite(value) ? value : 1.0, 0.5, 2.0));
    }

    /// <summary>TTS 输出增益（0.5–2.0，默认 1.0），仅作用于语音输出，不影响麦克风识别。</summary>
    public double TtsOutputVolume
    {
        get => _ttsOutputVolume;
        set => SetProperty(
            ref _ttsOutputVolume,
            Math.Clamp(double.IsFinite(value) ? value : 1.0, 0.5, 2.0));
    }

    /// <summary>是否开启反听：把增强后的 TTS 音频并行输出到本地监听设备。</summary>
    public bool EnableVoiceMonitoring
    {
        get => _enableVoiceMonitoring;
        set => SetProperty(ref _enableVoiceMonitoring, value);
    }

    /// <summary>监听设备 Id（Render 端点，不限于虚拟声卡）。为空则用系统默认输出。</summary>
    public string VoiceMonitorDeviceId
    {
        get => _voiceMonitorDeviceId;
        set => SetProperty(ref _voiceMonitorDeviceId, value);
    }

    public ManagedTtsModel? ManagedTtsModel
    {
        get => _managedTtsModel;
        set => SetProperty(ref _managedTtsModel, value);
    }

    public string ManagedTtsReferenceAudioPath
    {
        get => _managedTtsReferenceAudioPath;
        set => SetProperty(ref _managedTtsReferenceAudioPath, value);
    }

    public string ManagedTtsReferenceText
    {
        get => _managedTtsReferenceText;
        set => SetProperty(ref _managedTtsReferenceText, value);
    }

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

    /// <summary>麦克风语音增强引擎：Off / WebRtc / RNNoise，仅作用于用户输入语音。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<VoicePreprocessingMode>))]
    public VoicePreprocessingMode VoicePreprocessingMode { get => _voicePreprocessingMode; set => SetProperty(ref _voicePreprocessingMode, value); }
    public bool TranscriptionOnly { get => _transcriptionOnly; set => SetProperty(ref _transcriptionOnly, value); }

    [JsonConverter(typeof(JsonStringEnumConverter<SpeakerLabelMode>))]
    public SpeakerLabelMode SpeakerLabelMode { get => _speakerLabelMode; set => SetProperty(ref _speakerLabelMode, value); }

    public string SpeakerEmbeddingModel { get => _speakerEmbeddingModel; set => SetProperty(ref _speakerEmbeddingModel, value); }

    [JsonConverter(typeof(JsonStringEnumConverter<OutboundSpeechContent>))]
    public OutboundSpeechContent OutboundSpeechContent { get => _outboundSpeechContent; set => SetProperty(ref _outboundSpeechContent, value); }
    public bool SpeakMyTranslation { get => _speakMyTranslation; set => SetProperty(ref _speakMyTranslation, value); }
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
    public bool UseMicaBackdrop { get => _useMicaBackdrop; set => SetProperty(ref _useMicaBackdrop, value); }
    public bool MinimizeToTray { get => _minimizeToTray; set => SetProperty(ref _minimizeToTray, value); }
    public bool ConfirmOnClose { get => _confirmOnClose; set => SetProperty(ref _confirmOnClose, value); }
    public double? DesktopOverlayLeft { get => _desktopOverlayLeft; set => SetProperty(ref _desktopOverlayLeft, value); }
    public double? DesktopOverlayTop { get => _desktopOverlayTop; set => SetProperty(ref _desktopOverlayTop, value); }
    public double? DesktopOverlayWidth
    {
        get => _desktopOverlayWidth;
        set => SetProperty(ref _desktopOverlayWidth, value);
    }
    /// <summary>null 表示高度自适应内容；设置后窗口固定为该高度（88–2000）。</summary>
    public double? DesktopOverlayHeight
    {
        get => _desktopOverlayHeight;
        set => SetProperty(
            ref _desktopOverlayHeight,
            value is null ? null : Math.Clamp(value.Value, 88, 2000));
    }
    /// <summary>主译文字号（14–40），次译文与原文按比例联动。</summary>
    public int DesktopOverlayFontSize
    {
        get => _desktopOverlayFontSize;
        set => SetProperty(ref _desktopOverlayFontSize, Math.Clamp(value, 14, 40));
    }
    public bool DesktopOverlayTopmost { get => _desktopOverlayTopmost; set => SetProperty(ref _desktopOverlayTopmost, value); }
    public bool DesktopOverlayLockPosition { get => _desktopOverlayLockPosition; set => SetProperty(ref _desktopOverlayLockPosition, value); }
    public DesktopOverlayDisplayMode DesktopOverlayDisplayMode { get => _desktopOverlayDisplayMode; set => SetProperty(ref _desktopOverlayDisplayMode, value); }

    /// <summary>自动隐藏等待秒数（3–300），仅在 AutoHide 模式下生效。</summary>
    public int DesktopOverlayAutoHideSeconds
    {
        get => _desktopOverlayAutoHideSeconds;
        set => SetProperty(ref _desktopOverlayAutoHideSeconds, Math.Clamp(value, 3, 300));
    }
    public string LocalModelDirectory { get => _localModelDirectory; set => SetProperty(ref _localModelDirectory, value); }
    public string ManagedRuntimeDirectory { get => _managedRuntimeDirectory; set => SetProperty(ref _managedRuntimeDirectory, value); }

    /// <summary>
    /// 非持久化三态语音服务模式，由 UseRemoteSpeech / UseLocalKokoroTextToSpeech 计算得出。
    /// 历史同时为 true 时以 Kokoro 优先。
    /// </summary>
    [JsonIgnore]
    public SpeechServiceMode SpeechServiceMode
    {
        get
        {
            if (UseLocalKokoroTextToSpeech) return global::VoxLink.UI.Core.Models.SpeechServiceMode.Kokoro;
            if (UseRemoteSpeech) return global::VoxLink.UI.Core.Models.SpeechServiceMode.Remote;
            return global::VoxLink.UI.Core.Models.SpeechServiceMode.SystemFallback;
        }
    }

    [JsonIgnore]
    public bool SupportsGeneration => TranslationBackend != TranslationBackend.PublicFree;

    [JsonIgnore]
    public bool UsesCloudAsr => AsrProtocol is not (AsrProtocol.LocalWhisper
        or AsrProtocol.LocalSenseVoice
        or AsrProtocol.LocalFireRedAsr2Ctc
        or AsrProtocol.LocalManagedMoss);

    [JsonIgnore]
    public bool UsesStreamingAsr => AsrProtocol is AsrProtocol.DashScopeStreaming or AsrProtocol.SonioxStreaming;

    [JsonIgnore]
    public bool SupportsCloudSpeakerLabels => AsrProtocol == AsrProtocol.SonioxStreaming;

    /// <summary>本地 SenseVoice 或 FireRedASR2-CTC 选择：走 sherpa-onnx 原生运行时，仍需作为本地协议传给引擎。</summary>
    [JsonIgnore]
    private bool IsLocalSenseVoiceOrFireRed =>
        AsrProtocol is AsrProtocol.LocalSenseVoice or AsrProtocol.LocalFireRedAsr2Ctc;
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
            case TranslationBackend.LocalMiniCpm:
            case TranslationBackend.LocalHyMtGguf:
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

    /// <summary>
    /// 集中式翻译选择：PublicFree 关闭 AI 翻译并固定为免费端点；其余提供方应用默认值并开启 AI 翻译。
    /// 不触碰已保存的 API Key / 自定义请求头。
    /// </summary>
    public void SelectTranslationBackend(TranslationBackend backend)
    {
        if (backend == TranslationBackend.PublicFree)
        {
            UseAiTranslation = false;
            TranslationBackend = TranslationBackend.PublicFree;
            return;
        }

        ApplyTranslationBackendDefaults(backend);
        UseAiTranslation = true;
    }

    /// <summary>
    /// 集中式 ASR 选择：本地 Whisper 关闭云端、撤销音频上传授权；
    /// 云提供方应用默认值并开启云端，但绝不自动开启 AllowCloudAudioUpload。
    /// 切回本地时保留云 URL / model / API Key，便于恢复。
    /// </summary>
    public void SelectAsrProvider(AsrProvider provider)
    {
        if (provider == AsrProvider.LocalWhisper)
        {
            UseCloudAsr = false;
            AsrProvider = AsrProvider.LocalWhisper;
            AsrProtocol = AsrProtocol.LocalWhisper;
            AllowCloudAudioUpload = false;
            return;
        }

        var providerChanged = AsrProvider != provider;
        ApplyAsrProviderDefaults(provider);
        if (providerChanged)
        {
            AllowCloudAudioUpload = false;
        }
        UseCloudAsr = true;
    }

    /// <summary>
    /// 集中式 TTS 三态选择：SystemFallback=false/false；Remote=true/false；Kokoro=false/true。
    /// </summary>
    public void SelectSpeechService(SpeechServiceMode mode)
    {
        switch (mode)
        {
            case global::VoxLink.UI.Core.Models.SpeechServiceMode.SystemFallback:
                UseRemoteSpeech = false;
                UseLocalKokoroTextToSpeech = false;
                break;
            case global::VoxLink.UI.Core.Models.SpeechServiceMode.Remote:
                UseRemoteSpeech = true;
                UseLocalKokoroTextToSpeech = false;
                break;
            case global::VoxLink.UI.Core.Models.SpeechServiceMode.Kokoro:
                UseRemoteSpeech = false;
                UseLocalKokoroTextToSpeech = true;
                break;
        }
    }

    /// <summary>
    /// 将旧版本中互相矛盾的服务开关规范为下拉框可表达的有效状态。
    /// 仅修正实际生效字段，不清空地址、模型、密钥或请求头。
    /// 旧的应用托管翻译模型（ManagedHyMt/M2M/SMaLL-100）已下线，安全回退公共免密。
    /// </summary>
    public bool NormalizeServiceSelections()
    {
        var changed = false;
        if (TranslationBackend is TranslationBackend.ManagedHyMt
            or TranslationBackend.ManagedM2M100
            or TranslationBackend.ManagedSmall100)
        {
            UseAiTranslation = false;
            TranslationBackend = TranslationBackend.PublicFree;
            changed = true;
        }

        if (!UseAiTranslation && TranslationBackend != TranslationBackend.PublicFree)
        {
            TranslationBackend = TranslationBackend.PublicFree;
            changed = true;
        }
        else if (UseAiTranslation && TranslationBackend == TranslationBackend.PublicFree)
        {
            UseAiTranslation = false;
            changed = true;
        }

        if (!UseCloudAsr
            || AsrProvider is AsrProvider.LocalWhisper or AsrProvider.LocalManagedMoss
            || AsrProtocol is AsrProtocol.LocalWhisper or AsrProtocol.LocalManagedMoss
            || AsrProtocol is AsrProtocol.LocalSenseVoice or AsrProtocol.LocalFireRedAsr2Ctc)
        {
            changed |= UseCloudAsr
                || AsrProvider != AsrProvider.LocalWhisper
                || AsrProtocol is not (AsrProtocol.LocalWhisper
                    or AsrProtocol.LocalSenseVoice
                    or AsrProtocol.LocalFireRedAsr2Ctc)
                || AllowCloudAudioUpload;
            UseCloudAsr = false;
            AsrProvider = AsrProvider.LocalWhisper;
            if (AsrProtocol is not (AsrProtocol.LocalSenseVoice or AsrProtocol.LocalFireRedAsr2Ctc))
            {
                AsrProtocol = AsrProtocol.LocalWhisper;
            }
            AllowCloudAudioUpload = false;
        }
        else if (AsrProvider != AsrProvider.Custom)
        {
            var expectedProtocol = AsrProvider switch
            {
                AsrProvider.DashScope => AsrProtocol.DashScopeStreaming,
                AsrProvider.Soniox => AsrProtocol.SonioxStreaming,
                AsrProvider.SiliconFlow or AsrProvider.OpenAiCompatible => AsrProtocol.OpenAiMultipart,
                AsrProvider.MiMo => AsrProtocol.MiMoInputAudio,
                _ => AsrProtocol
            };
            if (AsrProtocol != expectedProtocol)
            {
                AsrProtocol = expectedProtocol;
                changed = true;
            }
        }

        if (UseLocalKokoroTextToSpeech && UseRemoteSpeech)
        {
            UseRemoteSpeech = false;
            changed = true;
        }

        // tiny / small 已从产品中移除：旧设置自动升级到 base，保留用户可用的识别能力。
        if (WhisperModel.Equals("tiny", StringComparison.OrdinalIgnoreCase)
            || WhisperModel.Equals("small", StringComparison.OrdinalIgnoreCase))
        {
            WhisperModel = "base";
            changed = true;
        }

        return changed;
    }

    public Dictionary<string, object?> ToEngineJson(bool respectSwitches = true) => new(StringComparer.Ordinal)
    {
        ["myLanguageCode"] = MyLanguageCode,
        ["otherLanguageCode"] = OtherLanguageCode,
        ["secondaryTargetLanguageCode"] = SecondaryTargetLanguageCode,
        ["captureMicrophone"] = CaptureMicrophone,
        ["captureSystemAudio"] = CaptureSystemAudio,
        ["microphoneDeviceId"] = MicrophoneDeviceId,
        ["systemAudioDeviceId"] = SystemAudioDeviceId,
        ["voiceOutputDeviceId"] = VoiceOutputDeviceId,
        ["translationProvider"] = respectSwitches && !UseAiTranslation ? "googleWeb" : TranslationBackend switch
        {
            TranslationBackend.PublicFree => "googleWeb",
            TranslationBackend.DashScope => "dashScope",
            TranslationBackend.DeepSeek => "deepSeek",
            TranslationBackend.OpenAiCompatible => "openAiCompatible",
            TranslationBackend.LocalMiniCpm => "localMiniCpm",
            TranslationBackend.LocalHyMtGguf => "localHyMtGguf",
            _ => "custom"
        },
        ["openAiBaseUrl"] = TranslationBaseUrl,
        ["openAiApiKey"] = TranslationApiKey,
        ["openAiModel"] = TranslationModel,
        ["openAiHeaders"] = TranslationHeaders,
        ["enableTranslationRefinement"] = EnableTranslationRefinement,
        ["translationRefinementPrompt"] = TranslationRefinementPrompt,
        ["speechRefinementEnabled"] = SpeechRefinementEnabled && UseAiTranslation && SupportsGeneration,
        ["speechRefinementPrompt"] = SpeechRefinementPrompt,
        ["asrProvider"] = respectSwitches && !UseCloudAsr
            ? "localWhisper"
            : JsonNamingPolicy.CamelCase.ConvertName(AsrProvider.ToString()),
        ["asrProtocol"] = respectSwitches && !UseCloudAsr
            ? AsrProtocol is AsrProtocol.LocalSenseVoice or AsrProtocol.LocalFireRedAsr2Ctc
                ? JsonNamingPolicy.CamelCase.ConvertName(AsrProtocol.ToString())
                : "localWhisper"
            : JsonNamingPolicy.CamelCase.ConvertName(AsrProtocol.ToString()),
        ["asrBaseUrl"] = AsrBaseUrl,
        ["asrApiKey"] = AsrApiKey,
        ["asrModel"] = AsrModel,
        ["asrHeaders"] = AsrHeaders,
        ["allowCloudAudioUpload"] = AllowCloudAudioUpload,
        ["useRemoteTextToSpeech"] = respectSwitches
            ? UseRemoteSpeech
            : UseRemoteSpeech || !string.IsNullOrWhiteSpace(SpeechApiKey),
        ["useLocalKokoroTextToSpeech"] = UseLocalKokoroTextToSpeech,
        ["kokoroSpeakerId"] = KokoroSpeakerId,
        ["kokoroSpeed"] = KokoroSpeed,
        ["managedTtsModel"] = ManagedTtsModel is null
            ? null
            : JsonNamingPolicy.CamelCase.ConvertName(ManagedTtsModel.Value.ToString()),
        ["managedTtsReferenceAudioPath"] = ManagedTtsReferenceAudioPath,
        ["managedTtsReferenceText"] = ManagedTtsReferenceText,
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
        ["voicePreprocessingMode"] = JsonNamingPolicy.CamelCase.ConvertName(VoicePreprocessingMode.ToString()),
        ["transcriptionOnly"] = TranscriptionOnly,
        ["speakerLabelMode"] = JsonNamingPolicy.CamelCase.ConvertName(SpeakerLabelMode.ToString()),
        ["speakerEmbeddingModel"] = SpeakerEmbeddingModel,
        ["outboundSpeechContent"] = JsonNamingPolicy.CamelCase.ConvertName(OutboundSpeechContent.ToString()),
        ["speakMyTranslation"] = SpeakMyTranslation,
        ["ttsOutputVolume"] = TtsOutputVolume,
        ["enableVoiceMonitoring"] = EnableVoiceMonitoring,
        ["voiceMonitorDeviceId"] = VoiceMonitorDeviceId,
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
        ["translateHotkey"] = TranslateHotkey,
        ["desktopOverlayLeft"] = DesktopOverlayLeft,
        ["desktopOverlayTop"] = DesktopOverlayTop,
        ["desktopOverlayWidth"] = DesktopOverlayWidth,
        ["desktopOverlayHeight"] = DesktopOverlayHeight,
        ["desktopOverlayFontSize"] = DesktopOverlayFontSize,
        ["desktopOverlayTopmost"] = DesktopOverlayTopmost,
        ["desktopOverlayLockPosition"] = DesktopOverlayLockPosition,
        ["desktopOverlayDisplayMode"] = JsonNamingPolicy.CamelCase.ConvertName(DesktopOverlayDisplayMode.ToString()),
        ["desktopOverlayAutoHideSeconds"] = DesktopOverlayAutoHideSeconds,
        ["localModelDirectory"] = LocalModelDirectory,
        ["managedRuntimeDirectory"] = ManagedRuntimeDirectory
    };

    private void RaiseAsrCapabilityProperties()
    {
        OnPropertyChanged(nameof(UsesCloudAsr));
        OnPropertyChanged(nameof(UsesStreamingAsr));
        OnPropertyChanged(nameof(SupportsCloudSpeakerLabels));
    }
}
