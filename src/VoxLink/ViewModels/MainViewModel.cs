using System.Collections.ObjectModel;
using System.Windows.Input;
using VoxLink.Audio;
using VoxLink.Infrastructure;
using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly AudioDeviceService _audioDeviceService;
    private readonly TranslationSession _session;
    private readonly SynchronizationContext _uiContext;
    private AppSettings _settings = new();
    private LanguageOption _myLanguage = LanguageCatalog.Get("zh");
    private LanguageOption _otherLanguage = LanguageCatalog.Get("en");
    private AudioDeviceInfo? _microphoneDevice;
    private AudioDeviceInfo? _systemAudioDevice;
    private AudioDeviceInfo? _voiceOutputDevice;
    private TranslationProviderOption? _translationProvider;
    private WhisperModelOption? _whisperModel;
    private string _inputText = string.Empty;
    private string _translatedText = string.Empty;
    private string _statusText = "正在初始化";
    private string _statusDetail = "正在读取音频设备";
    private string _errorText = string.Empty;
    private bool _isRunning;
    private bool _isBusy;
    private bool _isModelProgressVisible;
    private double _modelProgress;
    private bool _showOverlay = true;
    private bool _speakMyTranslation = true;
    private string _openAiBaseUrl = string.Empty;
    private string _openAiApiKey = string.Empty;
    private string _openAiModel = string.Empty;
    private string _toggleHotkey = "Ctrl+Alt+Space";
    private string _translateHotkey = "Ctrl+Alt+Enter";
    private double _voiceThreshold = 0.018;
    private int _silenceDurationMs = 650;

    public MainViewModel(
        SettingsStore settingsStore,
        AudioDeviceService audioDeviceService,
        TranslationSession session,
        ISpeechRecognizer speechRecognizer)
    {
        _settingsStore = settingsStore;
        _audioDeviceService = audioDeviceService;
        _session = session;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _session.StatusChanged += OnStatusChanged;
        _session.MessageReceived += OnMessageReceived;
        _session.ErrorOccurred += OnErrorOccurred;
        speechRecognizer.ModelProgress += OnModelProgress;

        StartStopCommand = new AsyncRelayCommand(ToggleSessionAsync, () => !IsBusy, ShowError);
        TranslateTextCommand = new AsyncRelayCommand(TranslateTypedTextAsync, CanTranslateText, ShowError);
        SwapLanguagesCommand = new RelayCommand(SwapLanguages, () => !IsRunning);
        ClearConversationCommand = new RelayCommand(ClearConversation);
    }

    public IReadOnlyList<LanguageOption> Languages => LanguageCatalog.All;

    public ObservableCollection<AudioDeviceInfo> MicrophoneDevices { get; } = [];

    public ObservableCollection<AudioDeviceInfo> RenderDevices { get; } = [];

    public ObservableCollection<ConversationItemViewModel> Conversation { get; } = [];

    public IReadOnlyList<TranslationProviderOption> TranslationProviders { get; } =
    [
        new(Models.TranslationProvider.GoogleWeb, "免密在线翻译"),
        new(Models.TranslationProvider.OpenAiCompatible, "OpenAI 兼容服务")
    ];

    public IReadOnlyList<WhisperModelOption> WhisperModels { get; } =
    [
        new("tiny", "快速", "约 75 MB"),
        new("base", "均衡", "约 142 MB"),
        new("small", "准确", "约 466 MB")
    ];

    public IReadOnlyList<string> HotkeyOptions { get; } =
    [
        "Ctrl+Alt+Space",
        "Ctrl+Shift+Space",
        "Alt+F8",
        "Ctrl+F8",
        "Ctrl+Alt+T"
    ];

    public IReadOnlyList<string> TranslateHotkeyOptions { get; } =
    [
        "Ctrl+Alt+Enter",
        "Ctrl+Shift+Enter",
        "Alt+F9",
        "Ctrl+F9",
        "Ctrl+Alt+R"
    ];

    public ICommand StartStopCommand { get; }

    public ICommand TranslateTextCommand { get; }

    public ICommand SwapLanguagesCommand { get; }

    public ICommand ClearConversationCommand { get; }

    public event EventHandler<ConversationMessage>? IncomingSubtitle;

    public event EventHandler? SettingsChanged;

    public LanguageOption MyLanguage
    {
        get => _myLanguage;
        set
        {
            if (SetProperty(ref _myLanguage, value))
            {
                _settings.MyLanguageCode = value.Code;
            }
        }
    }

    public LanguageOption OtherLanguage
    {
        get => _otherLanguage;
        set
        {
            if (SetProperty(ref _otherLanguage, value))
            {
                _settings.OtherLanguageCode = value.Code;
            }
        }
    }

    public AudioDeviceInfo? MicrophoneDevice
    {
        get => _microphoneDevice;
        set
        {
            if (SetProperty(ref _microphoneDevice, value))
            {
                _settings.MicrophoneDeviceId = value?.Id ?? string.Empty;
            }
        }
    }

    public AudioDeviceInfo? SystemAudioDevice
    {
        get => _systemAudioDevice;
        set
        {
            if (SetProperty(ref _systemAudioDevice, value))
            {
                _settings.SystemAudioDeviceId = value?.Id ?? string.Empty;
            }
        }
    }

    public AudioDeviceInfo? VoiceOutputDevice
    {
        get => _voiceOutputDevice;
        set
        {
            if (SetProperty(ref _voiceOutputDevice, value))
            {
                _settings.VoiceOutputDeviceId = value?.Id ?? string.Empty;
            }
        }
    }

    public TranslationProviderOption? TranslationProvider
    {
        get => _translationProvider;
        set
        {
            if (SetProperty(ref _translationProvider, value) && value is not null)
            {
                _settings.TranslationProvider = value.Value;
                OnPropertyChanged(nameof(IsOpenAiProvider));
            }
        }
    }

    public bool IsOpenAiProvider => TranslationProvider?.Value == Models.TranslationProvider.OpenAiCompatible;

    public WhisperModelOption? WhisperModel
    {
        get => _whisperModel;
        set
        {
            if (SetProperty(ref _whisperModel, value) && value is not null)
            {
                _settings.WhisperModel = value.Value;
            }
        }
    }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string TranslatedText
    {
        get => _translatedText;
        private set => SetProperty(ref _translatedText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(StartStopLabel));
                OnPropertyChanged(nameof(StartStopGlyph));
                NotifyCommandStates();
            }
        }
    }

    public string StartStopLabel => IsRunning ? "停止翻译" : "开始翻译";

    public string StartStopGlyph => IsRunning ? "\uE71A" : "\uE768";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public bool IsModelProgressVisible
    {
        get => _isModelProgressVisible;
        private set => SetProperty(ref _isModelProgressVisible, value);
    }

    public double ModelProgress
    {
        get => _modelProgress;
        private set => SetProperty(ref _modelProgress, value);
    }

    public bool ShowOverlay
    {
        get => _showOverlay;
        set
        {
            if (SetProperty(ref _showOverlay, value))
            {
                _settings.ShowOverlay = value;
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool SpeakMyTranslation
    {
        get => _speakMyTranslation;
        set
        {
            if (SetProperty(ref _speakMyTranslation, value))
            {
                _settings.SpeakMyTranslation = value;
            }
        }
    }

    public string OpenAiBaseUrl
    {
        get => _openAiBaseUrl;
        set
        {
            if (SetProperty(ref _openAiBaseUrl, value))
            {
                _settings.OpenAiBaseUrl = value;
            }
        }
    }

    public string OpenAiApiKey
    {
        get => _openAiApiKey;
        set
        {
            if (SetProperty(ref _openAiApiKey, value))
            {
                _settings.OpenAiApiKey = value;
            }
        }
    }

    public string OpenAiModel
    {
        get => _openAiModel;
        set
        {
            if (SetProperty(ref _openAiModel, value))
            {
                _settings.OpenAiModel = value;
            }
        }
    }

    public string ToggleHotkey
    {
        get => _toggleHotkey;
        set
        {
            if (SetProperty(ref _toggleHotkey, value))
            {
                _settings.ToggleHotkey = value;
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string TranslateHotkey
    {
        get => _translateHotkey;
        set
        {
            if (SetProperty(ref _translateHotkey, value))
            {
                _settings.TranslateHotkey = value;
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public double VoiceThreshold
    {
        get => _voiceThreshold;
        set
        {
            if (SetProperty(ref _voiceThreshold, value))
            {
                _settings.VoiceThreshold = value;
            }
        }
    }

    public int SilenceDurationMs
    {
        get => _silenceDurationMs;
        set
        {
            if (SetProperty(ref _silenceDurationMs, value))
            {
                _settings.SilenceDurationMs = value;
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _settings = await _settingsStore.LoadAsync(cancellationToken);
        MyLanguage = LanguageCatalog.Get(_settings.MyLanguageCode);
        OtherLanguage = LanguageCatalog.Get(_settings.OtherLanguageCode);
        TranslationProvider = TranslationProviders.FirstOrDefault(option => option.Value == _settings.TranslationProvider)
            ?? TranslationProviders[0];
        WhisperModel = WhisperModels.FirstOrDefault(option => option.Value == _settings.WhisperModel)
            ?? WhisperModels[0];
        ShowOverlay = _settings.ShowOverlay;
        SpeakMyTranslation = _settings.SpeakMyTranslation;
        OpenAiBaseUrl = _settings.OpenAiBaseUrl;
        OpenAiApiKey = _settings.OpenAiApiKey;
        OpenAiModel = _settings.OpenAiModel;
        ToggleHotkey = _settings.ToggleHotkey;
        TranslateHotkey = _settings.TranslateHotkey;
        VoiceThreshold = _settings.VoiceThreshold;
        SilenceDurationMs = _settings.SilenceDurationMs;

        RefreshAudioDevices();
        StatusText = "可以开始";
        StatusDetail = "选择语言后，点击开始翻译";
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default) =>
        await _settingsStore.SaveAsync(_settings, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await SaveAsync();
        }
        finally
        {
            await _session.DisposeAsync();
        }
    }

    private async Task ToggleSessionAsync()
    {
        ErrorText = string.Empty;
        IsBusy = true;
        try
        {
            if (IsRunning)
            {
                await _session.StopAsync();
                IsRunning = false;
            }
            else
            {
                await SaveAsync();
                await _session.StartAsync(_settings);
                IsRunning = true;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TranslateTypedTextAsync()
    {
        if (!CanTranslateText())
        {
            return;
        }

        ErrorText = string.Empty;
        var message = await _session.TranslateTypedTextAsync(InputText, _settings);
        TranslatedText = message.TranslatedText;
    }

    private bool CanTranslateText() => !IsBusy && !string.IsNullOrWhiteSpace(InputText);

    private void SwapLanguages()
    {
        (MyLanguage, OtherLanguage) = (OtherLanguage, MyLanguage);
    }

    private void ClearConversation()
    {
        Conversation.Clear();
        TranslatedText = string.Empty;
        ErrorText = string.Empty;
    }

    private void RefreshAudioDevices()
    {
        MicrophoneDevices.Clear();
        foreach (var device in _audioDeviceService.GetCaptureDevices())
        {
            MicrophoneDevices.Add(device);
        }

        RenderDevices.Clear();
        foreach (var device in _audioDeviceService.GetRenderDevices())
        {
            RenderDevices.Add(device);
        }

        MicrophoneDevice = FindDevice(MicrophoneDevices, _settings.MicrophoneDeviceId);
        SystemAudioDevice = FindDevice(RenderDevices, _settings.SystemAudioDeviceId);
        VoiceOutputDevice = FindDevice(RenderDevices, _settings.VoiceOutputDeviceId);
    }

    private static AudioDeviceInfo? FindDevice(
        IEnumerable<AudioDeviceInfo> devices,
        string selectedId) =>
        devices.FirstOrDefault(device => device.Id == selectedId)
        ?? devices.FirstOrDefault(device => device.IsDefault)
        ?? devices.FirstOrDefault();

    private void OnStatusChanged(object? sender, SessionStatusEventArgs eventArgs) =>
        RunOnUi(() =>
        {
            StatusText = eventArgs.Message;
            StatusDetail = eventArgs.Activity switch
            {
                SessionActivity.Preparing => "首次使用会自动下载模型",
                SessionActivity.Listening => "正在监听麦克风和系统声音",
                SessionActivity.Transcribing => "本地语音识别处理中",
                SessionActivity.Translating => "正在生成自然译文",
                SessionActivity.Speaking => "语音正在发送到所选输出设备",
                _ => "选择语言后，点击开始翻译"
            };
        });

    private void OnMessageReceived(object? sender, ConversationMessage message) =>
        RunOnUi(() =>
        {
            Conversation.Insert(0, new ConversationItemViewModel(message));
            while (Conversation.Count > 60)
            {
                Conversation.RemoveAt(Conversation.Count - 1);
            }

            if (message.Direction == TranslationDirection.Inbound)
            {
                IncomingSubtitle?.Invoke(this, message);
            }
        });

    private void OnErrorOccurred(object? sender, SessionErrorEventArgs eventArgs) =>
        RunOnUi(() => ShowError(new InvalidOperationException(eventArgs.Message, eventArgs.Exception)));

    private void OnModelProgress(object? sender, ModelProgressEventArgs eventArgs) =>
        RunOnUi(() =>
        {
            StatusText = eventArgs.Status;
            IsModelProgressVisible = eventArgs.Progress is not null && eventArgs.Progress < 1;
            ModelProgress = (eventArgs.Progress ?? 0) * 100;
        });

    private void ShowError(Exception exception)
    {
        ErrorText = exception.Message;
        StatusText = "需要处理";
        StatusDetail = exception.InnerException?.Message ?? "请检查设置后重试";
    }

    private void NotifyCommandStates()
    {
        (StartStopCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (TranslateTextCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (SwapLanguagesCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private void RunOnUi(Action action) => _uiContext.Post(_ => action(), null);
}
