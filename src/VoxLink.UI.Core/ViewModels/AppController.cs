using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using VoxLink.UI.Core.Infrastructure;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.Services;

namespace VoxLink.UI.Core.ViewModels;

public enum ComposerMode
{
    Translate,
    Generate
}

public sealed class AppController : ObservableObject, IAsyncDisposable
{
    private readonly IEngineGateway _engine;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IReleaseChecker _releaseChecker;
    private readonly bool _autoCheckForUpdates;
    private readonly SynchronizationContext _uiContext;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CancellationTokenSource? _saveDebounce;
    private Task? _initializeTask;
    private Task? _shutdownTask;
    private AppSettings _settings = new();
    private bool _initialized;
    private bool _engineConnected;
    private bool _isRunning;
    private bool _isBusy;
    private bool _closing;
    private string _statusMessage = "正在启动音频引擎";
    private string _activity = "preparing";
    private string _modelStatus = string.Empty;
    private double _modelProgress;
    private string? _errorMessage;
    private ComposerMode _composerMode;
    private bool _onboardingRequestPending;
    private bool _applyingQuickStartMode;
    private bool _isCheckingForUpdates;
    private bool _isUpdateAvailable;
    private bool _updateBannerDismissed;
    private string? _updateStatusText;
    private string? _latestReleaseUrl;
    private bool _needsSessionRestart;
    public AppController(
        IEngineGateway engine,
        ISettingsRepository settingsRepository,
        SynchronizationContext? synchronizationContext = null,
        IReleaseChecker? releaseChecker = null,
        Version? appVersion = null,
        bool autoCheckForUpdates = false)
    {
        _engine = engine;
        _settingsRepository = settingsRepository;
        _uiContext = synchronizationContext ?? SynchronizationContext.Current ?? new SynchronizationContext();
        AppVersion = appVersion
            ?? Assembly.GetEntryAssembly()?.GetName().Version
            ?? typeof(AppController).Assembly.GetName().Version
            ?? new Version(1, 0, 0);
        _releaseChecker = releaseChecker ?? new GitHubReleaseChecker(AppVersion);
        _autoCheckForUpdates = autoCheckForUpdates;
        _engine.EventReceived += OnEngineEventReceived;
        AttachSettings(_settings);
    }

    public AppSettings Settings
    {
        get => _settings;
        private set
        {
            if (ReferenceEquals(_settings, value))
            {
                return;
            }

            DetachSettings(_settings);
            _settings = value;
            AttachSettings(_settings);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanGenerate));
            RaiseQuickStartProperties();
        }
    }

    public IReadOnlyList<LanguageOption> Languages => LanguageOption.All;
    public IReadOnlyList<LanguageOption> SecondaryLanguages => LanguageOption.OptionalTargets;
    public ObservableCollection<AudioDeviceInfo> MicrophoneDevices { get; } = [];
    public ObservableCollection<AudioDeviceInfo> RenderDevices { get; } = [];
    public ObservableCollection<ConversationMessage> Messages { get; } = [];

    public bool Initialized { get => _initialized; private set => SetProperty(ref _initialized, value); }
    public bool EngineConnected { get => _engineConnected; private set => SetProperty(ref _engineConnected, value); }
    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string Activity { get => _activity; private set => SetProperty(ref _activity, value); }
    public string ModelStatus { get => _modelStatus; private set => SetProperty(ref _modelStatus, value); }
    public double ModelProgress { get => _modelProgress; private set => SetProperty(ref _modelProgress, value); }
    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }

    public Version AppVersion { get; }
    public bool IsCheckingForUpdates { get => _isCheckingForUpdates; private set => SetProperty(ref _isCheckingForUpdates, value); }
    public bool IsUpdateAvailable { get => _isUpdateAvailable; private set => SetProperty(ref _isUpdateAvailable, value); }
    public bool UpdateBannerVisible => IsUpdateAvailable && !_updateBannerDismissed;
    public void DismissUpdateBanner()
    {
        _updateBannerDismissed = true;
        OnPropertyChanged(nameof(UpdateBannerVisible));
    }
    public string? UpdateStatusText { get => _updateStatusText; private set => SetProperty(ref _updateStatusText, value); }
    public string? LatestReleaseUrl { get => _latestReleaseUrl; private set => SetProperty(ref _latestReleaseUrl, value); }
    public bool NeedsSessionRestart { get => _needsSessionRestart; private set => SetProperty(ref _needsSessionRestart, value); }

    public event EventHandler? OnboardingRequested;

    public ComposerMode ComposerMode
    {
        get => _composerMode;
        set => SetProperty(ref _composerMode, value);
    }

    public bool HasVirtualCable => FindVirtualCable() is not null;
    public bool IsVoiceMode => Settings.QuickStartMode == QuickStartMode.VrChatVoice;
    public string? VirtualCableName => FindVirtualCable()?.Name;
    public bool IsVoiceRouteReady => IsVoiceMode && ValidateVoiceRouteSettings() is null;
    public string VoiceRouteStatus
    {
        get
        {
            if (!IsVoiceMode)
            {
                return "麦克风译文将发送到 VRChat Chatbox，不播放语音。";
            }

            var output = FindSelectedVoiceOutput();
            return output is not null && IsVirtualCableName(output.Name)
                ? $"语音将输出到 {output.Name}。请在 VRChat 中选择对应录音端。"
                : "尚未配置虚拟声卡。打开新手引导完成语音路由。";
        }
    }
    public bool CanGenerate => Settings.SupportsGeneration;

    public string? ValidateVrChatChatboxSettings() => Settings.VrChatChatboxEnabled
        ? ValidateIpv4Endpoint(
            Settings.VrChatOscAddress,
            Settings.VrChatOscPort,
            "VRChat OSC 目标")
        : null;

    public string? ValidateVrChatSettings() => ValidateVrChatChatboxSettings()
        ?? (Settings.VrChatMuteSelfEnabled
            ? ValidateIpv4Endpoint(
                Settings.VrChatOscListenAddress,
                Settings.VrChatOscListenPort,
                "VRChat MuteSelf 监听")
            : null);
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_closing)
        {
            return Task.CompletedTask;
        }

        return _initializeTask ??= InitializeCoreAsync(cancellationToken);
    }

    public void NotifySettingsChanged()
    {
        ErrorMessage = null;
        ScheduleSaveAndConfigure();
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(CanGenerate));
        RaiseQuickStartProperties();
    }

    public void SwapLanguages()
    {
        (Settings.MyLanguageCode, Settings.OtherLanguageCode) =
            (Settings.OtherLanguageCode, Settings.MyLanguageCode);
        NotifySettingsChanged();
    }

    public void ApplyQuickStartMode(QuickStartMode mode)
    {
        if (IsRunning)
        {
            ErrorMessage = "请先停止当前会话，再切换输出模式。";
            return;
        }

        _applyingQuickStartMode = true;
        try
        {
            Settings.QuickStartMode = mode;
            Settings.TranscriptionOnly = false;
            Settings.CaptureMicrophone = true;
            Settings.CaptureSystemAudio = false;
            Settings.VrChatChatboxEnabled = true;
            Settings.SpeakMyTranslation = mode == QuickStartMode.VrChatVoice;
            if (mode == QuickStartMode.VrChatVoice)
            {
                EnsureVirtualCableSelected();
            }
        }
        finally
        {
            _applyingQuickStartMode = false;
        }

        NotifySettingsChanged();
        if (mode == QuickStartMode.VrChatVoice && !IsVoiceRouteReady)
        {
            ErrorMessage = "VRChat 语音翻译需要虚拟声卡。请打开新手引导选择 Cable Input 或 Voicemeeter 输出。";
        }
    }

    public AudioDeviceInfo? FindVirtualCable() => RenderDevices.FirstOrDefault(device =>
        IsVirtualCableName(device.Name));

    private AudioDeviceInfo? FindSelectedVoiceOutput() => RenderDevices.FirstOrDefault(device =>
        device.Id.Equals(Settings.VoiceOutputDeviceId, StringComparison.OrdinalIgnoreCase));

    private bool EnsureVirtualCableSelected()
    {
        var selected = FindSelectedVoiceOutput();
        if (selected is not null && IsVirtualCableName(selected.Name))
        {
            return false;
        }

        var cable = FindVirtualCable();
        if (cable is null)
        {
            return false;
        }

        Settings.VoiceOutputDeviceId = cable.Id;
        return true;
    }
    internal static bool IsVirtualCableName(string name) =>
        name.Contains("virtual audio cable", StringComparison.OrdinalIgnoreCase)
        || name.Contains("vb-audio", StringComparison.OrdinalIgnoreCase)
        || name.Contains("cable input", StringComparison.OrdinalIgnoreCase)
        || name.Contains("voicemeeter", StringComparison.OrdinalIgnoreCase)
        || name.Contains("virtual cable", StringComparison.OrdinalIgnoreCase);

    public void RequestOnboarding()
    {
        _onboardingRequestPending = true;
        TryRaiseOnboardingRequested();
    }

    public void CompleteOnboarding()
    {
        Settings.OnboardingCompleted = true;
        _onboardingRequestPending = false;
        NotifySettingsChanged();
    }

    private void TryRaiseOnboardingRequested()
    {
        if (_onboardingRequestPending && Initialized)
        {
            _onboardingRequestPending = false;
            OnboardingRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RaiseQuickStartProperties()
    {
        OnPropertyChanged(nameof(HasVirtualCable));
        OnPropertyChanged(nameof(IsVoiceMode));
        OnPropertyChanged(nameof(VirtualCableName));
        OnPropertyChanged(nameof(IsVoiceRouteReady));
        OnPropertyChanged(nameof(VoiceRouteStatus));
    }

    public async Task ToggleSessionAsync()
    {
        if (IsRunning)
        {
            await RunOperationAsync(async () =>
            {
                await _engine.RequestAsync("stopSession", timeout: TimeSpan.FromSeconds(20));
                IsRunning = false;
                RemovePartialMessages();
                NeedsSessionRestart = false;
                StatusMessage = "已停止";
                Activity = "idle";
            });
            return;
        }

        var validationError = ValidateSessionSettings();
        if (validationError is not null)
        {
            ErrorMessage = validationError;
            return;
        }

        await RunOperationAsync(async () =>
        {
            await SaveNowAsync();
            NeedsSessionRestart = false;
            StatusMessage = "正在准备语音识别";
            Activity = "preparing";
            await _engine.RequestAsync(
                "startSession",
                new Dictionary<string, object?> { ["settings"] = Settings.ToEngineJson() },
                TimeSpan.FromMinutes(20));
            IsRunning = true;
        });
    }

    public async Task SubmitAsync(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var validationError = ValidateSettings();
        if (validationError is not null)
        {
            ErrorMessage = validationError;
            return;
        }

        if (ComposerMode == ComposerMode.Generate && !CanGenerate)
        {
            ErrorMessage = "文本生成需要选择 DashScope、DeepSeek 或自定义 AI 服务。";
            return;
        }

        await RunOperationAsync(async () =>
        {
            await SaveNowAsync();
            if (ComposerMode == ComposerMode.Translate)
            {
                await _engine.RequestAsync("translate", new Dictionary<string, object?>
                {
                    ["text"] = trimmed,
                    ["settings"] = Settings.ToEngineJson()
                });
                return;
            }

            var result = await _engine.RequestAsync("generate", new Dictionary<string, object?>
            {
                ["prompt"] = trimmed,
                ["speak"] = Settings.SpeakMyTranslation,
                ["settings"] = Settings.ToEngineJson()
            });
            var generated = result is { ValueKind: JsonValueKind.Object } value
                && value.TryGetProperty("text", out var generatedText)
                ? generatedText.GetString() ?? string.Empty
                : string.Empty;
            Messages.Add(new ConversationMessage(
                ConversationDirection.Typed,
                trimmed,
                generated,
                DateTimeOffset.Now));
        });
    }

    public async Task SpeakAsync(ConversationMessage message)
    {
        if (!message.CanSpeak)
        {
            return;
        }
        var useOriginal = message.Direction != ConversationDirection.Inbound
            && Settings.OutboundSpeechContent == OutboundSpeechContent.Original;
        var languageCode = message.Direction == ConversationDirection.Inbound || !useOriginal
            ? (message.Direction == ConversationDirection.Inbound ? Settings.MyLanguageCode : Settings.OtherLanguageCode)
            : Settings.MyLanguageCode;
        var text = useOriginal ? message.SourceText : message.TranslatedText;
        await RunOperationAsync(() => _engine.RequestAsync("speak", new Dictionary<string, object?>
        {
            ["text"] = text,
            ["languageCode"] = languageCode,
            ["settings"] = Settings.ToEngineJson()
        }));
    }

    public async Task TestTranslationAsync()
    {
        var validationError = ValidateTranslationSettings();
        if (validationError is not null)
        {
            ErrorMessage = validationError;
            return;
        }

        await RunOperationAsync(async () =>
        {
            await SaveNowAsync();
            var result = await _engine.RequestAsync(
                "testTranslation",
                new Dictionary<string, object?> { ["settings"] = Settings.ToEngineJson() },
                TimeSpan.FromSeconds(30));
            var translated = result is { ValueKind: JsonValueKind.Object } value
                && value.TryGetProperty("translated", out var text)
                ? text.GetString()
                : null;
            StatusMessage = $"翻译连接正常：{translated}";
        });
    }

    public async Task TestSpeechAsync()
    {
        var validationError = ValidateSpeechSettings();
        if (validationError is not null)
        {
            ErrorMessage = validationError;
            return;
        }

        await RunOperationAsync(async () =>
        {
            await SaveNowAsync();
            await _engine.RequestAsync(
                "testSpeech",
                new Dictionary<string, object?> { ["settings"] = Settings.ToEngineJson() },
                TimeSpan.FromSeconds(45));
            StatusMessage = "语音服务测试完成";
        });
    }

    public async Task TestVoiceOutputAsync()
    {
        if (!IsVoiceMode)
        {
            ErrorMessage = "请先切换到 VRChat 语音模式，再测试虚拟声卡路由。";
            return;
        }

        var validationError = ValidateSpeechSettings() ?? ValidateVoiceRouteSettings();
        if (validationError is not null)
        {
            ErrorMessage = validationError;
            return;
        }

        await RunOperationAsync(async () =>
        {
            await SaveNowAsync();
            await _engine.RequestAsync(
                "testVoiceOutput",
                new Dictionary<string, object?> { ["settings"] = Settings.ToEngineJson() },
                TimeSpan.FromSeconds(45));
            StatusMessage = $"测试语音已发送到 {FindSelectedVoiceOutput()?.Name ?? "默认输出设备"}";
        });
    }

    public async Task TestVrChatOscAsync()
    {
        var validationError = ValidateVrChatChatboxSettings();
        if (validationError is not null)
        {
            ErrorMessage = validationError;
            return;
        }

        await RunOperationAsync(async () =>
        {
            await SaveNowAsync();
            await _engine.RequestAsync(
                "testVrChatOsc",
                new Dictionary<string, object?>
                {
                    ["text"] = "VoxLink VRChat OSC 测试",
                    ["settings"] = Settings.ToEngineJson()
                },
                TimeSpan.FromSeconds(10));
            StatusMessage = "VRChat OSC 测试消息已发送";
        });
    }

    public async Task TestVrOverlayAsync()
    {
        await RunOperationAsync(async () =>
        {
            await SaveNowAsync();
            var result = await _engine.RequestAsync(
                "testVrOverlay",
                new Dictionary<string, object?> { ["settings"] = Settings.ToEngineJson() },
                TimeSpan.FromSeconds(15));
            StatusMessage = result is { ValueKind: JsonValueKind.Object } value
                && value.TryGetProperty("status", out var status)
                ? status.GetString() ?? "SteamVR 字幕测试完成"
                : "SteamVR 字幕测试完成";
        });
    }

    public async Task PrepareModelAsync()
    {
        var validationError = ValidateAsrSettings();
        if (validationError is not null)
        {
            ErrorMessage = validationError;
            return;
        }

        await RunOperationAsync(async () =>
        {
            await SaveNowAsync();
            await _engine.RequestAsync(
                "prepareModel",
                new Dictionary<string, object?> { ["settings"] = Settings.ToEngineJson() },
                TimeSpan.FromMinutes(20));
            StatusMessage = Settings.UsesCloudAsr
                ? "云端语音识别配置已就绪"
                : "本地识别模型已就绪";
        });
    }

    public async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdates)
        {
            return;
        }

        IsCheckingForUpdates = true;
        IsUpdateAvailable = false;
        _updateBannerDismissed = false;
        UpdateStatusText = "正在检查更新…";
        try
        {
            var result = await _releaseChecker.CheckAsync();
            LatestReleaseUrl = result.ReleaseUrl;
            IsUpdateAvailable = result.State == ReleaseCheckState.UpdateAvailable;
            UpdateStatusText = result.Message;
        }
        catch (Exception)
        {
            UpdateStatusText = "无法检查更新，请稍后重试。";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    public void OpenLatestReleasePage()
    {
        var url = LatestReleaseUrl ?? ReleaseMetadata.ReleasesPageUrl;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ErrorMessage = "无法打开浏览器：" + exception.GetBaseException().Message.Trim();
        }
    }

    public Task RefreshDevicesAsync() => RunOperationAsync(async () =>
    {
        var result = await _engine.RequestAsync("getBootstrap");
        if (result is { ValueKind: JsonValueKind.Object } bootstrap)
        {
            ApplyBootstrap(bootstrap);
        }

        StatusMessage = "设备列表已刷新";
    });

    public void ClearMessages() => Messages.Clear();
    public void DismissError() => ErrorMessage = null;

    public async Task SaveNowAsync(CancellationToken cancellationToken = default)
    {
        _saveDebounce?.Cancel();
        await SaveAndConfigureAsync(cancellationToken);
    }

    public Task ShutdownAsync()
    {
        _closing = true;
        return _shutdownTask ??= ShutdownCoreAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        _engine.EventReceived -= OnEngineEventReceived;
        DetachSettings(Settings);
        _saveDebounce?.Dispose();
        _operationGate.Dispose();
        _saveGate.Dispose();
        await _engine.DisposeAsync();
    }

    public string? ValidateSettings() => ValidateLanguageSettings(includeSecondary: true)
        ?? ValidateTranslationSettings()
        ?? ValidateConfiguredSpeechSettings()
        ?? ValidateVoiceRouteSettings()
        ?? ValidateVrChatChatboxSettings();

    public string? ValidateLanguageSettingsForOnboarding() =>
        ValidateLanguageSettings(includeSecondary: false);

    public string? ValidateMicrophoneSettingsForOnboarding()
    {
        if (MicrophoneDevices.Count == 0)
        {
            return "未检测到麦克风。请连接麦克风并刷新设备列表。";
        }

        return MicrophoneDevices.Any(device =>
            device.Id.Equals(Settings.MicrophoneDeviceId, StringComparison.OrdinalIgnoreCase))
                ? null
                : "请选择有效的麦克风输入设备。";
    }
    public string? ValidateSessionSettings()
    {
        if (!Settings.CaptureMicrophone && !Settings.CaptureSystemAudio)
        {
            return "请至少启用麦克风或系统音频中的一个来源。";
        }

        return ValidateLanguageSettings(includeSecondary: !Settings.TranscriptionOnly)
            ?? ValidateAsrSettings()
            ?? (Settings.TranscriptionOnly ? null : ValidateTranslationSettings())
            ?? (Settings.TranscriptionOnly ? null : ValidateConfiguredSpeechSettings())
            ?? (Settings.TranscriptionOnly ? null : ValidateVoiceRouteSettings())
            ?? ValidateVrChatSettings();
    }

    private string? ValidateLanguageSettings(bool includeSecondary)
    {
        if (!IsSupportedLanguage(Settings.MyLanguageCode))
        {
            return "请选择有效的我的语言。";
        }

        if (!IsSupportedLanguage(Settings.OtherLanguageCode))
        {
            return "请选择有效的对方语言。";
        }

        var secondary = Settings.SecondaryTargetLanguageCode?.Trim();
        return includeSecondary
            && !string.IsNullOrWhiteSpace(secondary)
            && !IsSupportedLanguage(secondary)
                ? "请选择有效的第二目标语言。"
                : null;
    }

    private static bool IsSupportedLanguage(string? code) =>
        !string.IsNullOrWhiteSpace(code)
        && LanguageOption.All.Any(
            language => language.Code.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));
    public string? ValidateAsrSettings()
    {
        var compatibilityError = ValidateAsrProviderProtocol();
        if (compatibilityError is not null)
        {
            return compatibilityError;
        }
        if (Settings.AsrProtocol == AsrProtocol.LocalWhisper)
        {
            return string.IsNullOrWhiteSpace(Settings.WhisperModel)
                ? "请选择本地 Whisper 模型。"
                : null;
        }

        if (!Settings.AllowCloudAudioUpload)
        {
            return "云端 ASR 会上传原始音频；请先明确允许上传。";
        }

        if (string.IsNullOrWhiteSpace(Settings.AsrModel))
        {
            return "请输入 ASR 模型名称。";
        }

        var endpointError = Settings.UsesStreamingAsr
            ? ValidateStreamingEndpoint(Settings.AsrBaseUrl)
            : ValidateEndpoint(Settings.AsrBaseUrl, "ASR 服务");
        if (endpointError is not null)
        {
            return endpointError;
        }

        if ((Settings.AsrProtocol is AsrProtocol.DashScopeStreaming
                or AsrProtocol.SonioxStreaming
                or AsrProtocol.MiMoInputAudio
                || Settings.AsrProvider == AsrProvider.SiliconFlow)
            && string.IsNullOrWhiteSpace(Settings.AsrApiKey))
        {
            return "当前 ASR 协议需要 API Key。";
        }

        return ValidateAsrProviderProtocol();
    }

    public string? ValidateTranslationSettings()
    {
        if (Settings.TranslationBackend == TranslationBackend.PublicFree)
        {
            return null;
        }

        var endpointError = ValidateEndpoint(Settings.TranslationBaseUrl, "翻译服务");
        if (endpointError is not null)
        {
            return endpointError;
        }

        if (string.IsNullOrWhiteSpace(Settings.TranslationModel))
        {
            return "请输入翻译模型名称。";
        }

        if (Settings.TranslationBackend is TranslationBackend.DashScope or TranslationBackend.DeepSeek
            && string.IsNullOrWhiteSpace(Settings.TranslationApiKey))
        {
            return "当前翻译服务需要 API Key。";
        }

        return null;
    }

    private string? ValidateConfiguredSpeechSettings() =>
        Settings.SpeakMyTranslation || Settings.SpeakInboundTranslation
            ? ValidateSpeechSettings()
            : null;

    public string? ValidateSpeechSettings()
    {
        if (!Settings.UseRemoteSpeech)
        {
            return null;
        }

        var endpointError = ValidateEndpoint(Settings.SpeechBaseUrl, "语音服务");
        if (endpointError is not null)
        {
            return endpointError;
        }

        if (string.IsNullOrWhiteSpace(Settings.SpeechModel)
            || string.IsNullOrWhiteSpace(Settings.SpeechVoice))
        {
            return "请输入语音模型和音色。";
        }

        if (Settings.SpeechProtocol != SpeechProtocol.OpenAiCompatible
            && string.IsNullOrWhiteSpace(Settings.SpeechApiKey))
        {
            return "当前语音服务需要 API Key。";
        }

        return null;
    }

    public string? ValidateVoiceRouteSettings()
    {
        if (!IsVoiceMode)
        {
            return null;
        }

        if (!Settings.SpeakMyTranslation)
        {
            return "VRChat 语音翻译模式需要启用我的语音朗读。请重新选择该模式。";
        }

        var output = FindSelectedVoiceOutput();
        if (output is null)
        {
            return "请选择虚拟声卡的播放端作为语音输出设备。";
        }

        return IsVirtualCableName(output.Name)
            ? null
            : "VRChat 语音翻译必须输出到虚拟声卡（如 Cable Input 或 Voicemeeter），不能直接使用扬声器。";
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var loadedSettings = await _settingsRepository.LoadAsync(cancellationToken);
            loadedSettings.NormalizeQuickStartSettings();
            Settings = loadedSettings;
            if (_closing)
            {
                return;
            }

            await _engine.ConnectAsync(cancellationToken);
            if (_closing)
            {
                return;
            }

            EngineConnected = true;
            var result = await _engine.RequestAsync(
                "initialize",
                new Dictionary<string, object?> { ["settings"] = Settings.ToEngineJson() },
                cancellationToken: cancellationToken);
            if (result is { ValueKind: JsonValueKind.Object } bootstrap)
            {
                ApplyBootstrap(bootstrap);
            }

            StatusMessage = "就绪";
            Activity = "idle";
        }
        catch (Exception exception) when (exception is EngineException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            if (!_closing)
            {
                ErrorMessage = FriendlyError(exception);
                StatusMessage = "引擎不可用";
                Activity = "error";
            }
        }
        finally
        {
            Initialized = true;
            if (!Settings.OnboardingCompleted && !_closing)
            {
                _onboardingRequestPending = true;
            }
            TryRaiseOnboardingRequested();
            if (_autoCheckForUpdates && !_closing)
            {
                _ = CheckForUpdatesAsync();
            }
        }
    }

    private void ScheduleSaveAndConfigure()
    {
        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();
        var cancellation = new CancellationTokenSource();
        _saveDebounce = cancellation;
        _ = DebouncedSaveAsync(cancellation.Token);
    }

    private async Task DebouncedSaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(650), cancellationToken);
            await SaveAndConfigureAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is EngineException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            PostToUi(() => ErrorMessage = FriendlyError(exception));
        }
    }

    private async Task SaveAndConfigureAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            await _settingsRepository.SaveAsync(Settings, cancellationToken);
            if (_engine.IsConnected && !_closing)
            {
                await _engine.RequestAsync(
                    "configure",
                    new Dictionary<string, object?> { ["settings"] = Settings.ToEngineJson() },
                    TimeSpan.FromSeconds(20),
                    cancellationToken);
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task RunOperationAsync(Func<Task> operation)
    {
        if (!await _operationGate.WaitAsync(0))
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await operation();
        }
        catch (Exception exception) when (exception is EngineException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            ErrorMessage = FriendlyError(exception);
            Activity = "error";
        }
        finally
        {
            IsBusy = false;
            _operationGate.Release();
        }
    }

    private async Task ShutdownCoreAsync()
    {
        _saveDebounce?.Cancel();
        var closeTask = _engine.CloseAsync();
        try
        {
            if (_initializeTask is not null)
            {
                await _initializeTask;
            }
        }
        catch (Exception exception) when (exception is EngineException or IOException or UnauthorizedAccessException or CryptographicException)
        {
        }

        try
        {
            await _saveGate.WaitAsync();
            try
            {
                await _settingsRepository.SaveAsync(Settings);
            }
            finally
            {
                _saveGate.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
        }
        finally
        {
            await closeTask;
        }
    }

    private void OnEngineEventReceived(object? sender, EngineEvent engineEvent) =>
        PostToUi(() => HandleEngineEvent(engineEvent));

    private void HandleEngineEvent(EngineEvent engineEvent)
    {
        switch (engineEvent.Name)
        {
            case "ready":
                EngineConnected = true;
                break;
            case "status":
                StatusMessage = ReadString(engineEvent.Data, "message", StatusMessage);
                Activity = ReadString(engineEvent.Data, "activity", Activity);
                IsRunning = ReadBool(engineEvent.Data, "running", IsRunning);
                break;
            case "message":
                UpsertConversationMessage(ConversationMessage.FromJson(engineEvent.Data));
                break;
            case "partialMessage":
                UpsertConversationMessage(ConversationMessage.FromJson(engineEvent.Data));
                break;
            case "modelProgress":
                ModelStatus = ReadString(engineEvent.Data, "status");
                ModelProgress = ReadDouble(engineEvent.Data, "progress");
                break;
            case "error":
                ErrorMessage = ReadString(engineEvent.Data, "message", "引擎处理失败。");
                break;
            case "fatal":
                EngineConnected = false;
                IsRunning = false;
                ErrorMessage = ReadString(engineEvent.Data, "message", "音频引擎已退出。");
                break;
            case "protocolError":
                ErrorMessage = ReadString(engineEvent.Data, "message", "引擎协议错误。");
                break;
            case "hotkey":
                HandleHotkey(ReadString(engineEvent.Data, "action"));
                break;
        }
    }

    private void HandleHotkey(string action)
    {
        if (action == "toggle")
        {
            _ = ToggleSessionAsync();
        }
    }

    private void ApplyBootstrap(JsonElement bootstrap)
    {
        ReplaceDevices(MicrophoneDevices, DecodeDevices(bootstrap, "captureDevices"));
        ReplaceDevices(RenderDevices, DecodeDevices(bootstrap, "renderDevices"));
        if (IsVoiceMode)
        {
            EnsureVirtualCableSelected();
        }
        RaiseQuickStartProperties();
        IsRunning = ReadBool(bootstrap, "running", IsRunning);
    }

    private static IEnumerable<AudioDeviceInfo> DecodeDevices(JsonElement bootstrap, string name)
    {
        if (!bootstrap.TryGetProperty(name, out var devices) || devices.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return devices.EnumerateArray()
            .Select(AudioDeviceInfo.FromJson)
            .Where(device => !string.IsNullOrWhiteSpace(device.Id))
            .ToArray();
    }

    private static void ReplaceDevices(
        ObservableCollection<AudioDeviceInfo> target,
        IEnumerable<AudioDeviceInfo> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private void AttachSettings(AppSettings settings) => settings.PropertyChanged += OnSettingsPropertyChanged;
    private void DetachSettings(AppSettings settings) => settings.PropertyChanged -= OnSettingsPropertyChanged;

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_applyingQuickStartMode)
        {
            return;
        }

        if (args.PropertyName == nameof(AppSettings.TranslationBackend))
        {
            OnPropertyChanged(nameof(CanGenerate));
        }

        if (args.PropertyName is nameof(AppSettings.QuickStartMode)
            or nameof(AppSettings.SpeakMyTranslation))
        {
            _applyingQuickStartMode = true;
            try
            {
                if (args.PropertyName == nameof(AppSettings.QuickStartMode))
                {
                    Settings.SpeakMyTranslation = Settings.QuickStartMode == QuickStartMode.VrChatVoice;
                }
                else
                {
                    Settings.QuickStartMode = Settings.SpeakMyTranslation
                        ? QuickStartMode.VrChatVoice
                        : QuickStartMode.OscText;
                }
            }
            finally
            {
                _applyingQuickStartMode = false;
            }
        }

        if (args.PropertyName is nameof(AppSettings.QuickStartMode)
            or nameof(AppSettings.VoiceOutputDeviceId)
            or nameof(AppSettings.SpeakMyTranslation)
            or nameof(AppSettings.OutboundSpeechContent))
        {
            RaiseQuickStartProperties();
        }

        if (IsRunning
            && args.PropertyName is nameof(AppSettings.CaptureMicrophone)
                or nameof(AppSettings.CaptureSystemAudio)
                or nameof(AppSettings.MicrophoneDeviceId)
                or nameof(AppSettings.SystemAudioDeviceId))
        {
            NeedsSessionRestart = true;
        }

        if (!_closing)
        {
            ErrorMessage = null;
            ScheduleSaveAndConfigure();
        }
    }

    private void PostToUi(Action action)
    {
        if (SynchronizationContext.Current == _uiContext)
        {
            action();
            return;
        }

        _uiContext.Post(_ => action(), null);
    }

    private void UpsertConversationMessage(ConversationMessage message)
    {
        var matchingPartialIndex = FindPartialIndex(message.Direction, message.UtteranceId);
        if (!message.IsFinal)
        {
            if (matchingPartialIndex >= 0)
            {
                Messages[matchingPartialIndex] = message;
            }
            else
            {
                Messages.Add(message);
            }

            return;
        }

        if (matchingPartialIndex >= 0)
        {
            Messages[matchingPartialIndex] = message;
            return;
        }

        var latestPartialIndex = FindLatestPartialIndex(message.Direction);
        if (latestPartialIndex < 0)
        {
            Messages.Add(message);
            return;
        }

        var latestPartial = Messages[latestPartialIndex];
        if (string.IsNullOrWhiteSpace(message.UtteranceId)
            && string.IsNullOrWhiteSpace(latestPartial.UtteranceId)
            && RefersToSameUtterance(latestPartial.SourceText, message.SourceText))
        {
            Messages[latestPartialIndex] = message;
        }
        else
        {
            Messages.Insert(latestPartialIndex, message);
        }
    }

    private int FindPartialIndex(
        ConversationDirection direction,
        string? utteranceId)
    {
        if (string.IsNullOrWhiteSpace(utteranceId))
        {
            return FindLatestPartialIndex(direction);
        }

        for (var index = Messages.Count - 1; index >= 0; index--)
        {
            var candidate = Messages[index];
            if (!candidate.IsFinal
                && candidate.Direction == direction
                && string.Equals(candidate.UtteranceId, utteranceId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private int FindLatestPartialIndex(ConversationDirection direction)
    {
        for (var index = Messages.Count - 1; index >= 0; index--)
        {
            if (!Messages[index].IsFinal && Messages[index].Direction == direction)
            {
                return index;
            }
        }

        return -1;
    }

    private void RemovePartialMessages()
    {
        for (var index = Messages.Count - 1; index >= 0; index--)
        {
            if (!Messages[index].IsFinal)
            {
                Messages.RemoveAt(index);
            }
        }
    }

    private static bool RefersToSameUtterance(string partial, string final)
    {
        var normalizedPartial = partial.Trim();
        var normalizedFinal = final.Trim();
        return normalizedPartial.Length > 0
            && normalizedFinal.Length > 0
            && (normalizedPartial.StartsWith(normalizedFinal, StringComparison.OrdinalIgnoreCase)
                || normalizedFinal.StartsWith(normalizedPartial, StringComparison.OrdinalIgnoreCase));
    }
    private string? ValidateAsrProviderProtocol()
    {
        if (Settings.AsrProvider == AsrProvider.Custom)
        {
            return null;
        }

        var compatible = Settings.AsrProvider switch
        {
            AsrProvider.LocalWhisper => Settings.AsrProtocol == AsrProtocol.LocalWhisper,
            AsrProvider.DashScope => Settings.AsrProtocol == AsrProtocol.DashScopeStreaming,
            AsrProvider.Soniox => Settings.AsrProtocol == AsrProtocol.SonioxStreaming,
            AsrProvider.SiliconFlow or AsrProvider.OpenAiCompatible =>
                Settings.AsrProtocol == AsrProtocol.OpenAiMultipart,
            AsrProvider.MiMo => Settings.AsrProtocol == AsrProtocol.MiMoInputAudio,
            _ => false
        };
        return compatible ? null : "ASR 提供方与协议不匹配，请重新选择提供方。";
    }

    private static string? ValidateIpv4Endpoint(string addressValue, int port, string label)
    {
        if (!IPAddress.TryParse(addressValue.Trim(), out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return $"{label}地址必须是有效的 IPv4 地址。";
        }

        return port is < 1 or > 65_535
            ? $"{label}端口必须在 1 到 65535 之间。"
            : null;
    }

    private static string? ValidateStreamingEndpoint(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != "wss" && !(uri.Scheme == "ws" && uri.IsLoopback)))
        {
            return "流式 ASR 地址必须是完整的 WSS URL；本机服务可使用 WS。";
        }

        return null;
    }

    private static string? ValidateEndpoint(string value, string label) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
            ? null
            : $"{label}地址必须是完整的 HTTP 或 HTTPS URL。";

    private static string FriendlyError(Exception error) => error.GetBaseException().Message.Trim();

    private static string ReadString(JsonElement json, string name, string fallback = "") =>
        json.ValueKind == JsonValueKind.Object
        && json.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static bool ReadBool(JsonElement json, string name, bool fallback) =>
        json.ValueKind == JsonValueKind.Object
        && json.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static double ReadDouble(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object
        && json.TryGetProperty(name, out var value)
        && value.TryGetDouble(out var number)
            ? number
            : 0;
}
