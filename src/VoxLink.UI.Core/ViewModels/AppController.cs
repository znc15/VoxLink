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
    private bool _settingsLoaded;
    private bool _savePending;
    private Task? _initializeTask;
    private Task? _shutdownTask;
    private AppSettings _settings = new();
    private bool _initialized;
    private bool _engineConnected;
    private bool _isRunning;
    private bool _isBusy;
    private bool _closing;
    private string _statusMessage = "正在启动软件";
    private string _activity = "preparing";
    private string _modelStatus = string.Empty;
    private double _modelProgress;
    private string? _errorMessage;
    private string? _warningMessage;
    private string? _testResultMessage;
    private string? _modelServiceResultMessage;
    private string? _localModelResultMessage;
    private bool _onboardingRequestPending;
    private bool _isCheckingForUpdates;
    private bool _isUpdateAvailable;
    private bool _updateBannerDismissed;
    private string? _updateStatusText;
    private string? _latestReleaseUrl;
    private bool _needsSessionRestart;

    private const string SourceApp = "应用";
    private const string SourceEngine = "引擎";
    private const string SourceSession = "会话";
    private const string SourceTranslation = "翻译";
    private const string SourceUpdate = "更新";
    private const string SourceSettings = "设置";

    private static readonly string[] RecommendedLocalModelIds =
    [
        LocalModelIds.WhisperBase,
        LocalModelIds.MiniCpm51BGguf,
        LocalModelIds.Kokoro82M
    ];
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
            RaiseQuickStartProperties();
        }
    }

    public IReadOnlyList<LanguageOption> Languages => LanguageOption.All;
    public IReadOnlyList<LanguageOption> SecondaryLanguages => LanguageOption.OptionalTargets;
    public ObservableCollection<AudioDeviceInfo> MicrophoneDevices { get; } = [];
    public ObservableCollection<AudioDeviceInfo> RenderDevices { get; } = [];
    public ObservableCollection<ConversationMessage> Messages { get; } = [];
    public ObservableCollection<LocalModelItem> LocalModels { get; } = [];
    public ObservableCollection<LocalModelItem> InstallableLocalModels { get; } = [];
    public ObservableCollection<LocalModelItem> CatalogOnlyLocalModels { get; } = [];
    public ObservableCollection<LocalModelItem> SpeechRecognitionModels { get; } = [];
    public ObservableCollection<LocalModelItem> TranslationModels { get; } = [];
    public ObservableCollection<LocalModelItem> SpeechSynthesisModels { get; } = [];
    public bool HasBusyLocalModels => LocalModels.Any(model => model.IsBusy);
    public bool RecommendedLocalModelsReady =>
        IsInstalled(LocalModelIds.WhisperBase)
        && IsInstalled(LocalModelIds.MiniCpm51BGguf)
        && IsInstalled(LocalModelIds.Kokoro82M);

    public bool Initialized { get => _initialized; private set => SetProperty(ref _initialized, value); }
    public bool EngineConnected { get => _engineConnected; private set => SetProperty(ref _engineConnected, value); }
    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string Activity { get => _activity; private set => SetProperty(ref _activity, value); }
    public string ModelStatus { get => _modelStatus; private set => SetProperty(ref _modelStatus, value); }
    public double ModelProgress { get => _modelProgress; private set => SetProperty(ref _modelProgress, value); }
    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public string? WarningMessage { get => _warningMessage; private set => SetProperty(ref _warningMessage, value); }
    public string? TestResultMessage
    {
        get => _testResultMessage;
        private set => SetProperty(ref _testResultMessage, value);
    }

    public string? ModelServiceResultMessage
    {
        get => _modelServiceResultMessage;
        private set => SetProperty(ref _modelServiceResultMessage, value);
    }

    public string? LocalModelResultMessage
    {
        get => _localModelResultMessage;
        private set => SetProperty(ref _localModelResultMessage, value);
    }

    public Version AppVersion { get; }
    public bool IsCheckingForUpdates { get => _isCheckingForUpdates; private set => SetProperty(ref _isCheckingForUpdates, value); }
    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set
        {
            if (SetProperty(ref _isUpdateAvailable, value))
            {
                OnPropertyChanged(nameof(UpdateBannerVisible));
            }
        }
    }
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
    public event EventHandler? ConversationHistoryRequested;
    public event EventHandler? LocalModelsRequested;
    public bool HasVirtualCable => FindVirtualCable() is not null;
    public string? VirtualCableName => FindVirtualCable()?.Name;
    public bool IsVoiceRouteReady => Settings.SpeakMyTranslation && ValidateVoiceRouteSettings() is null;
    public string VoiceRouteStatus
    {
        get
        {
            if (!Settings.SpeakMyTranslation)
            {
                return "未开启朗读我的译文，不输出语音。";
            }

            var output = FindSelectedVoiceOutput();
            return output is not null && IsVirtualCableName(output.Name)
                ? $"语音将输出到 {output.Name}。请在 VRChat 中选择对应录音端。"
                : "尚未配置虚拟声卡。打开新手引导完成语音路由。";
        }
    }
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
        RaiseQuickStartProperties();
    }

    public void MarkSessionRestartRequired()
    {
        if (IsRunning)
        {
            NeedsSessionRestart = true;
        }
    }

    public Task SaveCommittedServiceSettingsAsync(bool reportToLocalModels = false) =>
        RunOperationAsync(async () =>
        {
            MarkSessionRestartRequired();
            await SaveNowAsync();
            var message = IsRunning ? "设置已保存，重启翻译后生效" : "设置已保存";
            if (reportToLocalModels)
            {
                LocalModelResultMessage = message;
            }
            else
            {
                ModelServiceResultMessage = message;
            }
        });

    public void RequestConversationHistory() =>
        ConversationHistoryRequested?.Invoke(this, EventArgs.Empty);

    public void SwapLanguages()
    {
        (Settings.MyLanguageCode, Settings.OtherLanguageCode) =
            (Settings.OtherLanguageCode, Settings.MyLanguageCode);
        NotifySettingsChanged();
    }


    public AudioDeviceInfo? FindVirtualCable() => RenderDevices.FirstOrDefault(device =>
        IsVirtualCableName(device.Name));

    private AudioDeviceInfo? FindSelectedVoiceOutput() => RenderDevices.FirstOrDefault(device =>
        device.Id.Equals(Settings.VoiceOutputDeviceId, StringComparison.OrdinalIgnoreCase));

    public bool EnsureVirtualCableSelected()
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

    public void RequestLocalModels() => LocalModelsRequested?.Invoke(this, EventArgs.Empty);
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
        OnPropertyChanged(nameof(VirtualCableName));
        OnPropertyChanged(nameof(IsVoiceRouteReady));
        OnPropertyChanged(nameof(VoiceRouteStatus));
    }

    public async Task ToggleSessionAsync()
    {
        if (!Initialized)
        {
            await InitializeAsync();
        }

        if (!EngineConnected)
        {
            ErrorMessage ??= "音频引擎尚未就绪，请稍后重试。";
            return;
        }

        if (IsRunning)
        {
            await RunOperationAsync(async () =>
            {
                await _engine.RequestAsync("stopSession", timeout: TimeSpan.FromSeconds(20));
                IsRunning = false;
                RemovePartialMessages();
                NeedsSessionRestart = false;
                StatusMessage = "软件已停止";
                Activity = "idle";
                LogService.Instance.Info(SourceSession, "已停止翻译会话。");
            });
            return;
        }

        var validationError = ValidateSessionSettings();
        if (validationError is not null)
        {
            ErrorMessage = validationError;
            return;
        }

        await RunOperationAsync(StartSessionCoreAsync);
    }

    private async Task StartSessionCoreAsync()
    {
        await EnsureSelectedLocalModelsInstalledAsync();
        await SaveNowAsync();
        NeedsSessionRestart = false;
        StatusMessage = "正在启动软件";
        Activity = "preparing";
        await _engine.RequestAsync(
            "startSession",
            new Dictionary<string, object?> { ["settings"] = Settings.ToEngineJson() },
            TimeSpan.FromMinutes(20));
        IsRunning = true;
        StatusMessage = "软件运行中";
        LogService.Instance.Info(SourceSession, $"开始翻译会话：{Settings.MyLanguageCode} → {Settings.OtherLanguageCode}（{DescribeCaptureSources()}）。");
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

        await RunOperationAsync(async () =>
        {
            await SaveNowAsync();
            LogService.Instance.Info(SourceTranslation, "手动翻译：" + TruncateForLog(trimmed));
            await _engine.RequestAsync("translate", new Dictionary<string, object?>
            {
                ["text"] = trimmed,
                ["settings"] = Settings.ToEngineJson()
            });
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
        TestResultMessage = null;
        ModelServiceResultMessage = null;
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
            TestResultMessage = $"翻译连接正常：{translated}";
            ModelServiceResultMessage = TestResultMessage;
        });
    }

    public async Task TestSpeechAsync()
    {
        TestResultMessage = null;
        ModelServiceResultMessage = null;
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
            TestResultMessage = "语音试听完成。";
            ModelServiceResultMessage = TestResultMessage;
        });
    }

    public async Task TestVoiceOutputAsync()
    {
        TestResultMessage = null;
        if (!Settings.SpeakMyTranslation)
        {
            ErrorMessage = "请先在「自动朗读」页开启朗读我的译文，再测试语音输出。";
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
            TestResultMessage = $"测试语音已发送到 {FindSelectedVoiceOutput()?.Name ?? "默认输出设备"}。";
        });
    }

    public async Task TestVrChatOscAsync()
    {
        TestResultMessage = null;
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
            TestResultMessage = "VRChat OSC 测试消息已发送。";
        });
    }

    public async Task TestVrOverlayAsync()
    {
        TestResultMessage = null;
        await RunOperationAsync(async () =>
        {
            await SaveNowAsync();
            var result = await _engine.RequestAsync(
                "testVrOverlay",
                new Dictionary<string, object?> { ["settings"] = Settings.ToEngineJson() },
                TimeSpan.FromSeconds(15));
            TestResultMessage = result is { ValueKind: JsonValueKind.Object } value
                && value.TryGetProperty("status", out var status)
                ? status.GetString() ?? "SteamVR 字幕测试完成"
                : "SteamVR 字幕测试完成";
        });
    }

    public async Task TestDesktopOverlayAsync()
    {
        TestResultMessage = null;
        await RunOperationAsync(async () =>
        {
            await SaveNowAsync();
            var result = await _engine.RequestAsync(
                "testDesktopOverlay",
                new Dictionary<string, object?> { ["settings"] = Settings.ToEngineJson() },
                TimeSpan.FromSeconds(15));
            TestResultMessage = result is { ValueKind: JsonValueKind.Object } value
                && value.TryGetProperty("status", out var status)
                ? status.GetString() ?? "桌面字幕测试完成"
                : "桌面字幕测试完成";
        });
    }

    /// <summary>
    /// 清空桌面字幕的持久化位置与大小并恢复默认字号；
    /// 随后的 configure 携带全空位置，悬浮窗将回到主屏底部居中。
    /// </summary>
    public void ResetDesktopOverlayPlacement()
    {
        Settings.DesktopOverlayLeft = null;
        Settings.DesktopOverlayTop = null;
        Settings.DesktopOverlayWidth = null;
        Settings.DesktopOverlayHeight = null;
        Settings.DesktopOverlayFontSize = 24;
        NotifySettingsChanged();
    }

    public async Task PrepareModelAsync()
    {
        TestResultMessage = null;
        ModelServiceResultMessage = null;
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
            TestResultMessage = Settings.UseCloudAsr
                ? "云端语音识别配置已保存并通过本地校验"
                : "本地识别模型已就绪";
            ModelServiceResultMessage = TestResultMessage;
        });
    }

    public async Task PrepareWhisperModelAsync()
    {
        TestResultMessage = null;
        LocalModelResultMessage = null;
        var validationError = ValidateLocalWhisperSettings();
        if (validationError is not null)
        {
            ErrorMessage = validationError;
            return;
        }

        await RunOperationAsync(async () =>
        {
            await SaveNowAsync();
            var engineSettings = Settings.ToEngineJson();
            engineSettings["asrProvider"] = "localWhisper";
            engineSettings["asrProtocol"] = "localWhisper";
            engineSettings["allowCloudAudioUpload"] = false;
            try
            {
                await _engine.RequestAsync(
                    "prepareModel",
                    new Dictionary<string, object?> { ["settings"] = engineSettings },
                    TimeSpan.FromMinutes(20));
                TestResultMessage = "本地识别模型已就绪";
                LocalModelResultMessage = TestResultMessage;
            }
            finally
            {
                if (_engine.IsConnected && !_closing)
                {
                    await _engine.RequestAsync(
                        "configure",
                        new Dictionary<string, object?> { ["settings"] = Settings.ToEngineJson() });
                }
            }
        });
    }

    public Task RefreshLocalModelsAsync() => RunOperationAsync(RefreshLocalModelsCoreAsync);

    public Task InstallLocalModelAsync(string modelId) =>
        RunOperationAsync(async () => _ = await RunLocalModelOperationAsync(modelId, install: true));

    public Task RetryLocalModelAsync(string modelId) => InstallLocalModelAsync(modelId);

    public Task RemoveLocalModelAsync(string modelId) =>
        RunOperationAsync(async () => _ = await RunLocalModelOperationAsync(modelId, install: false));

    public async Task<bool> InstallAndActivateLocalModelAsync(
        string modelId,
        bool reportToModelService = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var activated = false;
        await RunOperationAsync(async () =>
        {
            var model = FindLocalModel(modelId);
            if (model is null || !model.IsInstallable || model.IsBusy)
            {
                throw new EngineException("该模型当前不可安装或启用。");
            }

            if (!model.Installed && !await RunLocalModelOperationAsync(modelId, install: true))
            {
                throw new EngineException($"{model.Name} 安装失败，未更改当前服务。");
            }

            var previous = CaptureServiceSelections();
            ActivateLocalModel(modelId);
            await SaveSelectionsOrRollbackAsync(previous, $"{model.Name} 启用失败");
            var message = $"{model.Name} 已启用";
            if (reportToModelService)
            {
                ModelServiceResultMessage = message;
            }
            else
            {
                LocalModelResultMessage = message;
            }
            RefreshActiveLocalModels();
            activated = true;
        });
        return activated;
    }

    public Task RemoveLocalModelWithFallbackAsync(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return RunOperationAsync(async () =>
        {
            if (IsRunning)
            {
                ErrorMessage = "请先停止翻译，再删除正在使用的模型。";
                return;
            }

            if (!await RunLocalModelOperationAsync(modelId, install: false))
            {
                return;
            }

            ApplyRemovedModelFallback(modelId);
            try
            {
                await SaveNowAsync();
            }
            catch (Exception exception) when (IsRecoverableOperationException(exception))
            {
                var retryError = await TryPersistCurrentSettingsAsync();
                var detail = retryError is null
                    ? FriendlyError(exception)
                    : $"首次失败：{FriendlyError(exception)}；重试失败：{FriendlyError(retryError)}";
                throw new EngineException(
                    $"模型已删除，但安全回退设置保存或应用失败，请重试保存设置。{detail}");
            }
            LocalModelResultMessage = "模型已删除";
            RefreshActiveLocalModels();
        });
    }

    public Task InstallRecommendedLocalModelsAsync(bool startSession = false) =>
        RunOperationAsync(async () =>
        {
            if (IsRunning)
            {
                throw new EngineException("软件正在运行，请先停止后再启用推荐模型。");
            }
            if (HasBusyLocalModels)
            {
                throw new EngineException("当前有模型操作正在进行，请稍后重试。");
            }

            var previous = CaptureServiceSelections();
            foreach (var modelId in RecommendedLocalModelIds)
            {
                if (!IsInstalled(modelId)
                    && !await RunLocalModelOperationAsync(modelId, install: true))
                {
                    RestoreServiceSelections(previous);
                    RefreshActiveLocalModels();
                    throw new EngineException("推荐模型未全部安装，当前服务选择保持不变。");
                }
            }

            Settings.SelectTranslationBackend(TranslationBackend.LocalMiniCpm);
            Settings.SelectAsrProvider(AsrProvider.LocalWhisper);
            Settings.WhisperModel = "base";
            Settings.SelectSpeechService(SpeechServiceMode.Kokoro);
            await SaveSelectionsOrRollbackAsync(previous, "推荐模型启用失败");
            RefreshActiveLocalModels();
            LocalModelResultMessage = "本地模型已准备并启用";
            if (startSession)
            {
                var validationError = ValidateSessionSettings();
                if (validationError is not null)
                {
                    throw new EngineException(validationError);
                }
                await StartSessionCoreAsync();
            }
        });

    public Task TestLocalModelAsync(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return RunOperationAsync(async () =>
        {
            if (IsRunning)
            {
                throw new EngineException("请先停止翻译，再测试模型。");
            }

            var model = FindLocalModel(modelId);
            if (model is null || !model.IsInstallable || model.IsBusy)
            {
                throw new EngineException("该模型当前不可测试。");
            }

            if (!model.Installed)
            {
                throw new EngineException($"{model.Name} 还没安装，先安装再测试。");
            }

            model.BeginOperation("正在测试…");
            OnPropertyChanged(nameof(HasBusyLocalModels));
            try
            {
                var result = await _engine.RequestAsync(
                    "testLocalModel",
                    new Dictionary<string, object?> { ["modelId"] = model.Id },
                    TimeSpan.FromMinutes(10));
                var ok = result is { ValueKind: JsonValueKind.Object } value
                    && value.TryGetProperty("ok", out var okValue)
                    && okValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && okValue.GetBoolean();
                var detail = result is { ValueKind: JsonValueKind.Object } response
                    ? ReadString(response, "detail")
                    : string.Empty;
                if (ok)
                {
                    model.CompleteOperation("installed", $"测试通过：{detail}");
                    LocalModelResultMessage = $"{model.Name} 测试通过：{detail}";
                }
                else
                {
                    model.FailOperation("测试未通过，可重试");
                    ErrorMessage = $"{model.Name} 测试未通过：{detail}";
                }
            }
            catch (Exception exception) when (IsRecoverableOperationException(exception))
            {
                model.FailOperation("测试失败，可重试");
                throw;
            }
            finally
            {
                OnPropertyChanged(nameof(HasBusyLocalModels));
            }
        });
    }

    private async Task<bool> RunLocalModelOperationAsync(string modelId, bool install)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var item = LocalModels.FirstOrDefault(model =>
            model.Id.Equals(modelId, StringComparison.Ordinal));
        if (item is null || item.IsBusy)
        {
            return false;
        }

        var operationSucceeded = false;
        ErrorMessage = null;
        item.BeginOperation(install ? "正在准备安装…" : "正在删除…");
        OnPropertyChanged(nameof(HasBusyLocalModels));
        try
        {
            var result = await _engine.RequestAsync(
                install ? "installLocalModel" : "removeLocalModel",
                new Dictionary<string, object?> { ["modelId"] = item.Id },
                install ? TimeSpan.FromMinutes(20) : TimeSpan.FromMinutes(2));
            if (result is not { ValueKind: JsonValueKind.Object } response)
            {
                throw new EngineException("引擎返回了无效的本地模型操作结果。");
            }

            if (install)
            {
                var installState = ReadString(response, "installState");
                if (string.IsNullOrWhiteSpace(installState))
                {
                    throw new EngineException("引擎未返回模型安装状态。");
                }

                item.CompleteOperation(installState, "模型已安装并通过校验");
                operationSucceeded = installState.Equals("installed", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var removed = response.TryGetProperty("removed", out var removedValue)
                    && removedValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && removedValue.GetBoolean();
                operationSucceeded = removed;
                item.CompleteOperation(
                    removed ? "notinstalled" : item.InstallState,
                    removed ? "模型已删除" : "未删除模型（可能已不存在或正在使用）");
            }

            try
            {
                await RefreshLocalModelsCoreAsync();
                operationSucceeded = install
                    ? IsInstalled(modelId)
                    : operationSucceeded && !IsInstalled(modelId);
            }
            catch (Exception refreshError) when (refreshError is EngineException or IOException)
            {
                LogService.Instance.Warning(
                    SourceEngine,
                    "模型操作已完成，但刷新目录失败：" + FriendlyError(refreshError));
            }
        }
        catch (Exception exception) when (exception is
            EngineException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            item.FailOperation(install ? "安装失败，可重试" : "删除失败");
            try
            {
                await RefreshLocalModelsCoreAsync();
                operationSucceeded = install ? IsInstalled(modelId) : !IsInstalled(modelId);
                var refreshed = LocalModels.FirstOrDefault(model =>
                    model.Id.Equals(modelId, StringComparison.Ordinal));
                if (operationSucceeded)
                {
                    refreshed?.CompleteOperation(
                        install ? "installed" : "notinstalled",
                        install ? "模型已安装并通过校验" : "模型已删除");
                    LogService.Instance.Warning(
                        SourceEngine,
                        "模型请求返回错误，但目录刷新确认操作已完成：" + FriendlyError(exception));
                }
                else
                {
                    refreshed?.FailOperation(install ? "安装失败，可重试" : "删除失败");
                }
            }
            catch (Exception refreshError) when (refreshError is EngineException or IOException)
            {
                if (install)
                {
                    item.CompleteOperation("partial", "安装失败，可重试");
                }
                else
                {
                    item.FailOperation("删除失败");
                }

                LogService.Instance.Warning(
                    SourceEngine,
                    "模型操作失败后刷新目录失败：" + FriendlyError(refreshError));
            }

            if (!operationSucceeded)
            {
                ErrorMessage = FriendlyError(exception);
                Activity = "error";
                LogService.Instance.Error(SourceApp, exception, "本地模型操作失败");
            }
        }
        finally
        {
            OnPropertyChanged(nameof(HasBusyLocalModels));
            OnPropertyChanged(nameof(RecommendedLocalModelsReady));
        }

        return operationSucceeded;
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
            LogService.Instance.Info(SourceUpdate, result.Message);
        }
        catch (Exception exception)
        {
            UpdateStatusText = "无法检查更新，请稍后重试。";
            LogService.Instance.Warning(SourceUpdate, "检查更新失败：" + exception.GetBaseException().Message);
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

        TestResultMessage = "设备列表已刷新。";
    });

    private async Task RefreshLocalModelsCoreAsync()
    {
        var result = await _engine.RequestAsync(
            "listLocalModels",
            timeout: TimeSpan.FromSeconds(30));
        if (result is not { ValueKind: JsonValueKind.Object } response
            || !response.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            throw new EngineException("引擎返回了无效的本地模型目录。");
        }

        var parsedModels = new List<LocalModelItem>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models.EnumerateArray())
        {
            var item = LocalModelItem.FromJson(model);
            if (!string.IsNullOrWhiteSpace(item.Id) && seenIds.Add(item.Id))
            {
                parsedModels.Add(item);
            }
        }

        var busyModels = LocalModels
            .Where(item => item.IsBusy)
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        for (var index = 0; index < parsedModels.Count; index++)
        {
            var parsed = parsedModels[index];
            if (busyModels.TryGetValue(parsed.Id, out var busy))
            {
                parsedModels[index] = busy;
            }
        }

        // LocalModels 保留完整 Engine 目录以兼容协议和进度事件；公开 UI 只消费
        // 已接入原生运行时的 IsInstallable 条目，并按能力分类。
        LocalModels.Clear();
        InstallableLocalModels.Clear();
        CatalogOnlyLocalModels.Clear();
        SpeechRecognitionModels.Clear();
        TranslationModels.Clear();
        SpeechSynthesisModels.Clear();
        foreach (var item in parsedModels)
        {
            LocalModels.Add(item);
            if (!item.IsInstallable)
            {
                CatalogOnlyLocalModels.Add(item);
                continue;
            }

            InstallableLocalModels.Add(item);

            // tiny / small 已从产品中移除：仍在 LocalModels 中保留以兼容引擎目录协议
            // 与旧安装状态，但不再进入「本地模型」页的语音识别列表。
            var isRetiredAsr = item.Category == "asr"
                && (item.Id == LocalModelIds.WhisperTiny
                    || item.Id == LocalModelIds.WhisperSmall);
            if (isRetiredAsr)
            {
                continue;
            }

            switch (item.Category)
            {
                case "asr":
                    SpeechRecognitionModels.Add(item);
                    break;
                case "translation":
                    TranslationModels.Add(item);
                    break;
                case "tts":
                    SpeechSynthesisModels.Add(item);
                    break;
            }
        }

        RefreshActiveLocalModels();
        OnPropertyChanged(nameof(HasBusyLocalModels));
        OnPropertyChanged(nameof(RecommendedLocalModelsReady));
    }
    public void ClearMessages() => Messages.Clear();
    public void DismissError() => ErrorMessage = null;


    public void DismissWarning() => WarningMessage = null;
    public async Task SaveNowAsync(CancellationToken cancellationToken = default)
    {
        _saveDebounce?.Cancel();
        if (!_settingsLoaded)
        {
            _savePending = true;
            return;
        }
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
        if (!Settings.UseCloudAsr)
        {
            return Settings.AsrProtocol switch
            {
                AsrProtocol.LocalSenseVoice => null,
                AsrProtocol.LocalFireRedAsr2Ctc => null,
                _ => ValidateLocalWhisperSettings()
            };
        }

        if (Settings.AsrProvider == AsrProvider.LocalWhisper)
        {
            return "请先在模型服务页选择云端语音识别提供方。";
        }

        return ValidateAsrSettingsForTest();
    }

    public string? ValidateAsrSettingsForTest()
    {
        if (!Settings.UsesCloudAsr)
        {
            return Settings.AsrProtocol switch
            {
                AsrProtocol.LocalSenseVoice or AsrProtocol.LocalFireRedAsr2Ctc => null,
                _ => ValidateLocalWhisperSettings()
            };
        }

        var compatibilityError = ValidateAsrProviderProtocol();
        if (compatibilityError is not null)
        {
            return compatibilityError;
        }

        if (Settings.AsrProtocol == AsrProtocol.LocalWhisper)
        {
            return "请先在模型服务页选择云端语音识别协议。";
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

    private string? ValidateLocalWhisperSettings() =>
        string.IsNullOrWhiteSpace(Settings.WhisperModel)
            ? "请选择本地 Whisper 模型。"
            : null;

    public string? ValidateTranslationSettings()
    {
        if (!Settings.UseAiTranslation)
        {
            return null;
        }

        return ValidateTranslationSettingsForTest();
    }

    public string? ValidateTranslationSettingsForTest()
    {
        if (Settings.TranslationBackend is TranslationBackend.PublicFree
            or TranslationBackend.LocalMiniCpm
            or TranslationBackend.LocalHyMtGguf)
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
        Settings.SpeakMyTranslation
            ? ValidateSpeechSettings()
            : null;

    public string? ValidateSpeechSettings()
    {
        if (Settings.UseLocalKokoroTextToSpeech || !Settings.UseRemoteSpeech)
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
        if (!Settings.SpeakMyTranslation)
        {
            return null;
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
            LogService.Instance.Info(SourceApp, "正在加载设置并连接音频引擎…");
            var loadedSettings = await _settingsRepository.LoadAsync(cancellationToken);
            Settings = loadedSettings;
            _settingsLoaded = true;
            if (!_closing && _savePending)
            {
                _savePending = false;
                ScheduleSaveAndConfigure();
            }
            if (_closing)
            {
                return;
            }

            var launchArguments = new List<string>();
            if (!string.IsNullOrWhiteSpace(Settings.LocalModelDirectory))
            {
                launchArguments.Add("--model-dir");
                launchArguments.Add(Settings.LocalModelDirectory);
            }

            if (!string.IsNullOrWhiteSpace(Settings.ManagedRuntimeDirectory))
            {
                launchArguments.Add("--runtime-dir");
                launchArguments.Add(Settings.ManagedRuntimeDirectory);
            }

            if (launchArguments.Count > 0)
            {
                _engine.SetLaunchArguments(launchArguments);
            }

            await _engine.ConnectAsync(cancellationToken);
            if (_closing)
            {
                return;
            }

            EngineConnected = true;
            LogService.Instance.Info(SourceEngine, "音频引擎已连接。");
            var result = await _engine.RequestAsync(
                "initialize",
                new Dictionary<string, object?> { ["settings"] = Settings.ToEngineJson() },
                cancellationToken: cancellationToken);
            if (result is { ValueKind: JsonValueKind.Object } bootstrap)
            {
                ApplyBootstrap(bootstrap);
            }

            try
            {
                await RefreshLocalModelsCoreAsync();
            }
            catch (Exception exception) when (exception is EngineException or IOException)
            {
                LogService.Instance.Warning(
                    SourceEngine,
                    "读取本地模型目录失败：" + FriendlyError(exception));
            }

            StatusMessage = "软件已就绪";
            Activity = "idle";
            LogService.Instance.Info(SourceApp, "初始化完成，已就绪。");
        }
        catch (Exception exception) when (exception is EngineException
            or IOException
            or UnauthorizedAccessException
            or CryptographicException
            or JsonException)
        {
            if (!_closing)
            {
                ErrorMessage = FriendlyError(exception);
                StatusMessage = "软件启动失败";
                Activity = "error";
                LogService.Instance.Error(SourceApp, exception, "初始化失败");
            }
        }
        finally
        {
            Initialized = true;
            if (_settingsLoaded && !Settings.OnboardingCompleted && !_closing)
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
        if (!_settingsLoaded)
        {
            _savePending = true;
            return;
        }
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
            LogService.Instance.Warning(SourceSettings, "保存或同步配置失败：" + FriendlyError(exception));
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
            ErrorMessage = "当前有操作正在进行，请稍后重试。";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        TestResultMessage = null;
        try
        {
            await operation();
        }
        catch (Exception exception) when (exception is EngineException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            ErrorMessage = FriendlyError(exception);
            Activity = "error";
            LogService.Instance.Error(SourceApp, exception, "操作失败");
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
                if (_settingsLoaded)
                {
                    await _settingsRepository.SaveAsync(Settings);
                }
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
                LogService.Instance.Info(SourceEngine, "引擎就绪。");
                break;
            case "status":
                Activity = ReadString(engineEvent.Data, "activity", Activity);
                IsRunning = ReadBool(engineEvent.Data, "running", IsRunning);
                StatusMessage = IsRunning ? "软件运行中" : "软件已就绪";
                LogService.Instance.Debug(SourceEngine, $"状态：{StatusMessage}（{Activity}，运行中={IsRunning}）");
                break;
            case "message":
                var finalMessage = ConversationMessage.FromJson(engineEvent.Data);
                UpsertConversationMessage(finalMessage);
                if (finalMessage.IsFinal)
                {
                    LogService.Instance.Info(SourceEngine, $"识别完成：{TruncateForLog(finalMessage.SourceText)} → {TruncateForLog(finalMessage.TranslatedText)}");
                }
                else
                {
                    LogService.Instance.Debug(SourceEngine, "识别中：" + TruncateForLog(finalMessage.SourceText));
                }
                break;
            case "partialMessage":
                UpsertConversationMessage(ConversationMessage.FromJson(engineEvent.Data));
                break;
            case "modelProgress":
                var progressStatus = ReadString(engineEvent.Data, "status");
                var modelProgress = ReadNullableDouble(engineEvent.Data, "progress");
                var modelId = ReadString(engineEvent.Data, "modelId");
                var modelCategory = ReadString(engineEvent.Data, "category");
                if (string.IsNullOrWhiteSpace(modelId))
                {
                    ModelStatus = progressStatus;
                    ModelProgress = modelProgress ?? 0;
                }
                else
                {
                    LocalModels.FirstOrDefault(model =>
                            model.Id.Equals(modelId, StringComparison.Ordinal)
                            && (string.IsNullOrWhiteSpace(modelCategory)
                                || model.Category.Equals(modelCategory, StringComparison.OrdinalIgnoreCase)))
                        ?.UpdateProgress(progressStatus, modelProgress);
                }

                LogService.Instance.Debug(
                    SourceEngine,
                    $"模型进度：{progressStatus} {(modelProgress ?? 0):P0}"
                    + (string.IsNullOrWhiteSpace(modelId) ? "" : $"（{modelId}）"));
                break;
            case "error":
                ErrorMessage = ReadString(engineEvent.Data, "message", "引擎处理失败。");
                LogService.Instance.Warning(SourceEngine, "引擎错误：" + ErrorMessage);
                break;
            case "warning":
                WarningMessage = ReadString(engineEvent.Data, "message", "引擎警告。");
                LogService.Instance.Warning(SourceEngine, "引擎警告：" + WarningMessage);
                break;
            case "fatal":
                EngineConnected = false;
                IsRunning = false;
                ErrorMessage = ReadString(engineEvent.Data, "message", "音频引擎已退出。");
                LogService.Instance.Error(SourceEngine, "引擎致命错误：" + ErrorMessage);
                break;
            case "protocolError":
                ErrorMessage = ReadString(engineEvent.Data, "message", "引擎协议错误。");
                LogService.Instance.Warning(SourceEngine, "协议错误：" + ErrorMessage);
                break;
            case "diagnostic":
                LogService.Instance.Debug(SourceEngine, "诊断：" + ReadString(engineEvent.Data, "message"));
                break;
            case "hotkey":
                HandleHotkey(ReadString(engineEvent.Data, "action"));
                break;
            case "overlayPlacement":
                var overlayLeft = ReadNullableDouble(engineEvent.Data, "left");
                var overlayTop = ReadNullableDouble(engineEvent.Data, "top");
                var overlayWidth = ReadNullableDouble(engineEvent.Data, "width");
                // null 表示窗口处于高度自适应状态，清空持久化高度以保持自动。
                var overlayHeight = ReadNullableDouble(engineEvent.Data, "height");
                if (overlayLeft is not null)
                {
                    Settings.DesktopOverlayLeft = overlayLeft;
                }

                if (overlayTop is not null)
                {
                    Settings.DesktopOverlayTop = overlayTop;
                }

                if (overlayWidth is not null)
                {
                    Settings.DesktopOverlayWidth = overlayWidth;
                }

                Settings.DesktopOverlayHeight = overlayHeight;

                _ = SaveNowAsync();
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
        if (Settings.SpeakMyTranslation)
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

    private bool _replacingDevices;

    private void ReplaceDevices(
        ObservableCollection<AudioDeviceInfo> target,
        IEnumerable<AudioDeviceInfo> values)
    {
        var incoming = values.ToList();
        var existingIds = target
            .Select(device => device.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);


        _replacingDevices = true;
        try
        {
            foreach (var removed in target
                .Where(device => !incoming.Any(next =>
                    string.Equals(next.Id, device.Id, StringComparison.OrdinalIgnoreCase)))
                .ToList())
            {
                target.Remove(removed);
            }

            foreach (var added in incoming.Where(device => !existingIds.Contains(device.Id)))
            {
                target.Add(added);
            }
        }
        finally
        {
            _replacingDevices = false;
        }
    }

    private void AttachSettings(AppSettings settings) => settings.PropertyChanged += OnSettingsPropertyChanged;
    private void DetachSettings(AppSettings settings) => settings.PropertyChanged -= OnSettingsPropertyChanged;

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {

        if (_replacingDevices
            && args.PropertyName is nameof(AppSettings.MicrophoneDeviceId)
                or nameof(AppSettings.SystemAudioDeviceId)
                or nameof(AppSettings.VoiceOutputDeviceId))
        {
            return;
        }


        if (args.PropertyName is nameof(AppSettings.VoiceOutputDeviceId)
            or nameof(AppSettings.SpeakMyTranslation)
            or nameof(AppSettings.OutboundSpeechContent))
        {
            RaiseQuickStartProperties();
        }

        if (args.PropertyName is nameof(AppSettings.UseAiTranslation)
            or nameof(AppSettings.TranslationBackend)
            or nameof(AppSettings.UseCloudAsr)
            or nameof(AppSettings.AsrProvider)
            or nameof(AppSettings.AsrProtocol)
            or nameof(AppSettings.WhisperModel)
            or nameof(AppSettings.UseRemoteSpeech)
            or nameof(AppSettings.UseLocalKokoroTextToSpeech))
        {
            RefreshActiveLocalModels();
            OnPropertyChanged(nameof(Settings));
            if (IsRunning)
            {
                NeedsSessionRestart = true;
            }
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

    private LocalModelItem? FindLocalModel(string modelId) => LocalModels.FirstOrDefault(model =>
        model.Id.Equals(modelId, StringComparison.Ordinal));

    private bool IsInstalled(string modelId) => FindLocalModel(modelId)?.Installed == true;

    private async Task EnsureSelectedLocalModelsInstalledAsync()
    {
        var required = new List<string>();
        if (!Settings.UseCloudAsr)
        {
            required.Add(Settings.AsrProtocol switch
            {
                AsrProtocol.LocalSenseVoice => LocalModelIds.SenseVoiceSmall,
                AsrProtocol.LocalFireRedAsr2Ctc => LocalModelIds.FireRedAsr2Ctc,
                _ => LocalModelIds.WhisperId(Settings.WhisperModel)
            });
        }

        if (!Settings.TranscriptionOnly && Settings.UseAiTranslation)
        {
            var translationModelId = Settings.TranslationBackend switch
            {
                TranslationBackend.LocalMiniCpm => LocalModelIds.MiniCpm51BGguf,
                TranslationBackend.LocalHyMtGguf => LocalModelIds.HyMt15Gguf,
                _ => null
            };
            if (translationModelId is not null)
            {
                required.Add(translationModelId);
            }
        }

        if (!Settings.TranscriptionOnly
            && Settings.SpeakMyTranslation
            && Settings.SpeechServiceMode == SpeechServiceMode.Kokoro)
        {
            required.Add(LocalModelIds.Kokoro82M);
        }

        var requiredModelIds = required.Distinct(StringComparer.Ordinal).ToArray();
        if (requiredModelIds.Any(modelId => FindLocalModel(modelId) is null))
        {
            // 冷启动时引擎连接会先于模型目录读取完成；初始化期间的首次目录请求也可能
            // 因磁盘或安全软件暂时占用而失败。启动前主动刷新一次，避免把瞬时状态误报
            // 为“目录尚未就绪”。
            await RefreshLocalModelsCoreAsync();
        }

        foreach (var modelId in requiredModelIds)
        {
            var model = FindLocalModel(modelId)
                ?? throw new EngineException(
                    $"本地模型目录中未找到所选模型（{modelId}）。请重启 VoxLink 后重试。");
            if (!model.Installed && !await RunLocalModelOperationAsync(modelId, install: true))
            {
                throw new EngineException($"{model.Name} 安装失败，无法启动翻译。");
            }
        }
    }
    private bool IsLocalModelActive(string modelId) => modelId switch
    {
        LocalModelIds.WhisperTiny or LocalModelIds.WhisperBase or LocalModelIds.WhisperSmall
            or LocalModelIds.WhisperLargeV3Turbo =>
            !Settings.UseCloudAsr
            && Settings.AsrProtocol == AsrProtocol.LocalWhisper
            && LocalModelIds.WhisperId(Settings.WhisperModel).Equals(modelId, StringComparison.Ordinal),
        LocalModelIds.SenseVoiceSmall => !Settings.UseCloudAsr
            && Settings.AsrProtocol == AsrProtocol.LocalSenseVoice,
        LocalModelIds.FireRedAsr2Ctc => !Settings.UseCloudAsr
            && Settings.AsrProtocol == AsrProtocol.LocalFireRedAsr2Ctc,
        LocalModelIds.MiniCpm51BGguf => Settings.UseAiTranslation
            && Settings.TranslationBackend == TranslationBackend.LocalMiniCpm,
        LocalModelIds.HyMt15Gguf => Settings.UseAiTranslation
            && Settings.TranslationBackend == TranslationBackend.LocalHyMtGguf,
        LocalModelIds.Kokoro82M => Settings.SpeechServiceMode == SpeechServiceMode.Kokoro,
        _ => false
    };

    private void RefreshActiveLocalModels()
    {
        foreach (var model in LocalModels)
        {
            model.IsActive = model.Installed && IsLocalModelActive(model.Id);
        }
    }

    private void ActivateLocalModel(string modelId)
    {
        var whisperModel = LocalModelIds.WhisperName(modelId);
        if (whisperModel is not null)
        {
            Settings.SelectAsrProvider(AsrProvider.LocalWhisper);
            Settings.WhisperModel = whisperModel;
        }
        else if (modelId.Equals(LocalModelIds.SenseVoiceSmall, StringComparison.Ordinal))
        {
            Settings.SelectAsrProvider(AsrProvider.LocalWhisper);
            Settings.AsrProtocol = AsrProtocol.LocalSenseVoice;
        }
        else if (modelId.Equals(LocalModelIds.FireRedAsr2Ctc, StringComparison.Ordinal))
        {
            Settings.SelectAsrProvider(AsrProvider.LocalWhisper);
            Settings.AsrProtocol = AsrProtocol.LocalFireRedAsr2Ctc;
        }
        else if (modelId.Equals(LocalModelIds.MiniCpm51BGguf, StringComparison.Ordinal))
        {
            Settings.SelectTranslationBackend(TranslationBackend.LocalMiniCpm);
        }
        else if (modelId.Equals(LocalModelIds.HyMt15Gguf, StringComparison.Ordinal))
        {
            Settings.SelectTranslationBackend(TranslationBackend.LocalHyMtGguf);
        }
        else if (modelId.Equals(LocalModelIds.Kokoro82M, StringComparison.Ordinal))
        {
            Settings.SelectSpeechService(SpeechServiceMode.Kokoro);
        }
        else
        {
            throw new ArgumentException("未知的可运行本地模型。", nameof(modelId));
        }

        if (IsRunning)
        {
            NeedsSessionRestart = true;
        }
    }

    private void ApplyRemovedModelFallback(string modelId)
    {
        var whisperName = LocalModelIds.WhisperName(modelId);
        if (whisperName is not null
            && !Settings.UseCloudAsr
            && Settings.AsrProtocol == AsrProtocol.LocalWhisper
            && Settings.WhisperModel.Equals(whisperName, StringComparison.OrdinalIgnoreCase))
        {
            var fallback = new[]
            {
                LocalModelIds.WhisperBase,
                LocalModelIds.WhisperLargeV3Turbo
            }.FirstOrDefault(id => !id.Equals(modelId, StringComparison.Ordinal) && IsInstalled(id));
            if (fallback is not null)
            {
                Settings.WhisperModel = LocalModelIds.WhisperName(fallback)!;
            }
            else
            {
                Settings.WhisperModel = string.Empty;
            }
        }
        else if ((modelId.Equals(LocalModelIds.SenseVoiceSmall, StringComparison.Ordinal)
                  && !Settings.UseCloudAsr
                  && Settings.AsrProtocol == AsrProtocol.LocalSenseVoice)
                 || (modelId.Equals(LocalModelIds.FireRedAsr2Ctc, StringComparison.Ordinal)
                     && !Settings.UseCloudAsr
                     && Settings.AsrProtocol == AsrProtocol.LocalFireRedAsr2Ctc))
        {
            // 回退到已安装的 Whisper；全都没有时清空模型名，走默认
            var fallback = new[]
            {
                LocalModelIds.WhisperBase,
                LocalModelIds.WhisperLargeV3Turbo
            }.FirstOrDefault(IsInstalled);
            Settings.AsrProtocol = AsrProtocol.LocalWhisper;
            Settings.WhisperModel = fallback is not null ? LocalModelIds.WhisperName(fallback)! : string.Empty;
        }
        else if ((modelId.Equals(LocalModelIds.MiniCpm51BGguf, StringComparison.Ordinal)
                  && Settings.UseAiTranslation
                  && Settings.TranslationBackend == TranslationBackend.LocalMiniCpm)
                 || (modelId.Equals(LocalModelIds.HyMt15Gguf, StringComparison.Ordinal)
                     && Settings.UseAiTranslation
                     && Settings.TranslationBackend == TranslationBackend.LocalHyMtGguf))
        {
            Settings.SelectTranslationBackend(TranslationBackend.PublicFree);
        }
        else if (modelId.Equals(LocalModelIds.Kokoro82M, StringComparison.Ordinal)
            && Settings.SpeechServiceMode == SpeechServiceMode.Kokoro)
        {
            Settings.SelectSpeechService(SpeechServiceMode.SystemFallback);
        }
    }

    private SettingsTransactionSnapshot CaptureServiceSelections()
    {
        var snapshot = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(Settings))
            ?? throw new JsonException("无法创建设置事务快照。");
        snapshot.TranslationApiKey = Settings.TranslationApiKey;
        snapshot.AsrApiKey = Settings.AsrApiKey;
        snapshot.SpeechApiKey = Settings.SpeechApiKey;
        snapshot.TranslationHeaders = new(Settings.TranslationHeaders, StringComparer.OrdinalIgnoreCase);
        snapshot.AsrHeaders = new(Settings.AsrHeaders, StringComparer.OrdinalIgnoreCase);
        snapshot.SpeechHeaders = new(Settings.SpeechHeaders, StringComparer.OrdinalIgnoreCase);
        return new SettingsTransactionSnapshot(snapshot, NeedsSessionRestart);
    }

    private void RestoreServiceSelections(SettingsTransactionSnapshot snapshot)
    {
        Settings = snapshot.Settings;
        NeedsSessionRestart = snapshot.NeedsSessionRestart;
    }

    private async Task SaveSelectionsOrRollbackAsync(
        SettingsTransactionSnapshot previous,
        string failureContext)
    {
        try
        {
            await SaveNowAsync();
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            RestoreServiceSelections(previous);
            RefreshActiveLocalModels();
            var rollbackError = await TryPersistCurrentSettingsAsync();
            if (rollbackError is null)
            {
                throw new EngineException(
                    $"{failureContext}，已恢复原服务选择。{FriendlyError(exception)}");
            }
            throw new EngineException(
                $"{failureContext}。内存已恢复，但磁盘或引擎回滚失败，状态可能不一致。"
                + $"首次失败：{FriendlyError(exception)}；回滚失败：{FriendlyError(rollbackError)}");
        }
    }

    private async Task<Exception?> TryPersistCurrentSettingsAsync()
    {
        try
        {
            await SaveNowAsync();
            return null;
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            LogService.Instance.Error(SourceSettings, exception, "保存恢复后的设置失败");
            return exception;
        }
    }

    private static bool IsRecoverableOperationException(Exception exception) =>
        exception is EngineException or IOException or UnauthorizedAccessException or CryptographicException;

    private sealed record SettingsTransactionSnapshot(
        AppSettings Settings,
        bool NeedsSessionRestart);

    private string? ValidateAsrProviderProtocol()
    {
        if (Settings.AsrProvider == AsrProvider.Custom)
        {
            return null;
        }

        var compatible = Settings.AsrProvider switch
        {
            AsrProvider.LocalWhisper => Settings.AsrProtocol is AsrProtocol.LocalWhisper
                or AsrProtocol.LocalSenseVoice
                or AsrProtocol.LocalFireRedAsr2Ctc,
            AsrProvider.LocalManagedMoss => Settings.AsrProtocol == AsrProtocol.LocalManagedMoss,
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

    private string DescribeCaptureSources()
    {
        var sources = new List<string>();
        if (Settings.CaptureMicrophone)
        {
            sources.Add("麦克风");
        }

        if (Settings.CaptureSystemAudio)
        {
            sources.Add("系统回环");
        }

        return sources.Count == 0 ? "无采集来源" : string.Join(" + ", sources);
    }

    private static string TruncateForLog(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\n", " ").Replace("\r", " ").Trim();
        const int Max = 200;
        return normalized.Length > Max ? normalized[..Max] + "…" : normalized;
    }

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
        ReadNullableDouble(json, name) ?? 0;

    private static double? ReadNullableDouble(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object
        && json.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : null;
}
