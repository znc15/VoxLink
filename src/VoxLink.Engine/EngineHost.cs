using System.Linq;
using System.Net.Http;
using System.Text.Json;
using VoxLink.Audio;
using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Engine;

internal sealed class EngineHost : IAsyncDisposable
{
    private readonly Action<string, object> _notify;
    private readonly HttpClient _httpClient;
    private readonly AudioDeviceService _audioDevices = new();
    private readonly AsrRecognizerFactory _asrFactory;
    private readonly TranslationServiceFactory _translationFactory;
    private readonly HybridTextToSpeechService _textToSpeech;
    private readonly TranslationSession _session;
    private readonly SemaphoreSlim _sessionModelGate = new(1, 1);
    private readonly VrChatOscSender _vrChatOsc = new();
    private readonly UiHost? _uiHost;
    private readonly ILocalModelManager _localModelManager;
    private readonly bool _ownsLocalModelManager;
    private readonly IManagedModelRuntimeManager _managedRuntimeManager;
    private readonly bool _ownsManagedRuntimeManager;
    private readonly ILocalModelOrchestrator _localModelOrchestrator;
    private readonly LocalModelOrchestrator? _defaultManagedOrchestrator;
    private readonly bool _ownsLocalModelOrchestrator;
    private readonly object _lifecycleSync = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly TaskCompletionSource _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _requestsDrained;
    private int _activeRequests;
    private bool _disposeStarted;
    internal const double EchoSimilarityThreshold = 0.7;
    internal static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(10);
    private const int MaxRecentInbound = 3;
    private readonly object _recentInboundGate = new();
    private readonly List<ConversationMessage> _recentInbound = [];
    private int _echoSuppressed;

    private AppSettings _settings = new();
    private Exception? _vrChatOscConfigurationError;
    public EngineHost(Action<string, object> notify)
        : this(notify, startUiHost: true)
    {
    }

    public EngineHost(
        Action<string, object> notify,
        string? localModelDirectory = null,
        string? managedRuntimeDirectory = null)
        : this(
            notify,
            startUiHost: true,
            localModelManager: null,
            managedRuntimeManager: null,
            localModelOrchestrator: null,
            localModelDirectory: localModelDirectory,
            managedRuntimeDirectory: managedRuntimeDirectory)
    {
    }

    internal EngineHost(Action<string, object> notify, bool startUiHost)
        : this(notify, startUiHost, localModelManager: null)
    {
    }

    internal EngineHost(
        Action<string, object> notify,
        bool startUiHost,
        ILocalModelManager? localModelManager)
        : this(
            notify,
            startUiHost,
            localModelManager,
            managedRuntimeManager: null,
            localModelOrchestrator: null)
    {
    }

    internal EngineHost(
        Action<string, object> notify,
        bool startUiHost,
        ILocalModelManager? localModelManager,
        IManagedModelRuntimeManager? managedRuntimeManager,
        ILocalModelOrchestrator? localModelOrchestrator,
        string? localModelDirectory = null,
        string? managedRuntimeDirectory = null)
    {
        _notify = notify;
        _localModelManager = localModelManager
            ?? (string.IsNullOrWhiteSpace(localModelDirectory)
                ? new LocalModelManager()
                : new LocalModelManager(localModelDirectory));
        _ownsLocalModelManager = localModelManager is null;
        _managedRuntimeManager = managedRuntimeManager
            ?? (string.IsNullOrWhiteSpace(managedRuntimeDirectory)
                ? new ManagedModelRuntimeManager()
                : new ManagedModelRuntimeManager(managedRuntimeDirectory));
        _ownsManagedRuntimeManager = managedRuntimeManager is null;
        if (localModelOrchestrator is null)
        {
            _localModelOrchestrator = new LocalModelOrchestrator(
                _localModelManager,
                _managedRuntimeManager);
            _ownsLocalModelOrchestrator = true;
            _defaultManagedOrchestrator = (LocalModelOrchestrator)_localModelOrchestrator;
        }
        else
        {
            _localModelOrchestrator = localModelOrchestrator;
            _ownsLocalModelOrchestrator = false;
        }

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VoxLink.Engine/1.0");
        _asrFactory = new AsrRecognizerFactory(
            _httpClient,
            new WhisperSpeechRecognizer(localModelDirectory),
            new ClientAsrWebSocketFactory(),
            _localModelManager,
            managedOrchestrator: _defaultManagedOrchestrator);
        _translationFactory = new TranslationServiceFactory(
            _httpClient,
            _localModelManager);
        _textToSpeech = new HybridTextToSpeechService(
            _httpClient,
            enableEdgeTts: true,
            _localModelManager,
            _defaultManagedOrchestrator);
        _session = new TranslationSession(_asrFactory, _translationFactory, _textToSpeech);
        _vrChatOsc.SendFailed += OnVrChatOscSendFailed;
        if (startUiHost)
        {
            _uiHost = new UiHost(
                action => _notify("hotkey", new { action }),
                (name, data) => _notify(name, data));
        }
        _session.StatusChanged += OnStatusChanged;
        _session.MessageReceived += OnMessageReceived;
        _session.PartialMessageReceived += OnPartialMessageReceived;
        _session.ErrorOccurred += OnErrorOccurred;
        _session.WarningOccurred += OnWarningOccurred;
        _session.ModelProgress += OnModelProgress;
        _localModelManager.ModelProgress += OnLocalModelProgress;
        _managedRuntimeManager.RuntimeProgress += OnManagedRuntimeProgress;
    }

    public bool ShouldShutdown { get; private set; }

    public async Task<object?> HandleAsync(
        string method,
        JsonElement parameters,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken)
    {
        using var request = EnterRequest();
        if (method.Equals("shutdown", StringComparison.Ordinal))
        {
            _shutdownCancellation.Cancel();
            return await HandleCoreAsync(
                method,
                parameters,
                serializerOptions,
                cancellationToken).ConfigureAwait(false);
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdownCancellation.Token);
        try
        {
            return await HandleCoreAsync(
                method,
                parameters,
                serializerOptions,
                linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ManagedRuntimeException)
        {
            throw;
        }
        catch (Exception exception) when (IsManagedRuntimeMethod(method))
        {
            throw new ManagedRuntimeException("托管模型运行时操作失败，请重试或修复运行时。", exception);
        }
    }

    private async Task<object?> HandleCoreAsync(
        string method,
        JsonElement parameters,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                return await RunSessionModelOperationAsync(
                    () =>
                    {
                        ApplySettings(ReadSettings(parameters, serializerOptions));
                        return Task.FromResult<object?>(GetBootstrap());
                    },
                    cancellationToken);
            case "configure":
                return await RunSessionModelOperationAsync(
                    () =>
                    {
                        ApplySettings(
                            ReadSettings(parameters, serializerOptions),
                            applyTextToSpeech: !_session.IsRunning);
                        return Task.FromResult<object?>(GetBootstrap());
                    },
                    cancellationToken);
            case "getBootstrap":
                return GetBootstrap();
            case "startSession":
            {
                var running = await RunSessionModelOperationAsync(async () =>
                {
                    ApplySettings(ReadSettings(parameters, serializerOptions));
                    await _session.StartAsync(_settings, cancellationToken);
                    return _session.IsRunning;
                }, cancellationToken);
                return new { running };
            }
            case "stopSession":
            {
                await RunSessionModelOperationAsync(async () =>
                {
                    await _session.StopAsync();
                    _textToSpeech.Configure(_settings);
                    return false;
                }, cancellationToken);
                return new { running = false };
            }
            case "translate":
            {
                return await RunSessionModelOperationAsync(async () =>
                {
                    ApplyOptionalSettings(parameters, serializerOptions);
                    var text = ReadString(parameters, "text");
                    return await _session.TranslateTypedTextAsync(
                        text, _settings, cancellationToken);
                }, cancellationToken);
            }
            case "generate":
            {
                return await RunSessionModelOperationAsync(async () =>
                {
                    ApplyOptionalSettings(parameters, serializerOptions);
                    var effectiveSettings = _session.GetEffectiveSettingsSnapshot(_settings);
                    var prompt = ReadString(parameters, "prompt");
                    var chatService = _translationFactory.CreateChatService(effectiveSettings);
                    if (chatService is null)
                    {
                        throw new InvalidOperationException("当前翻译模型不支持文本生成。");
                    }

                    string generated;
                    try
                    {
                        generated = await chatService.GenerateAsync(prompt, cancellationToken);
                    }
                    finally
                    {
                        await DisposeServiceAsync(chatService);
                    }
                    if (effectiveSettings.VrChatChatboxEnabled)
                    {
                        _vrChatOsc.TryQueue(VrChatOscSender.ComposeTranslation(
                            generated,
                            prompt,
                            effectiveSettings.VrChatIncludeSourceText));
                    }
                    if (ReadBool(parameters, "speak"))
                    {
                        var speech = ResolveGeneratedSpeech(
                            prompt, generated, effectiveSettings);
                        await _textToSpeech.SpeakAsync(
                            speech.Text,
                            speech.Language,
                            effectiveSettings.VoiceOutputDeviceId,
                            cancellationToken);
                    }

                    return (object?)new { text = generated };
                }, cancellationToken);
            }
            case "speak":
            {
                return await RunSessionModelOperationAsync(async () =>
                {
                    ApplyOptionalSettings(parameters, serializerOptions);
                    var effectiveSettings = _session.GetEffectiveSettingsSnapshot(_settings);
                    var text = ReadString(parameters, "text");
                    var languageCode = TryReadString(parameters, "languageCode")
                        ?? effectiveSettings.OtherLanguageCode;
                    await _textToSpeech.SpeakAsync(
                        text,
                        LanguageCatalog.Get(languageCode),
                        effectiveSettings.VoiceOutputDeviceId,
                        cancellationToken);
                    return (object?)new { spoken = true };
                }, cancellationToken);
            }
            case "prepareModel":
                return await RunSessionModelOperationAsync(async () =>
                {
                    ApplyOptionalSettings(parameters, serializerOptions);
                    await _asrFactory.PrepareAsync(_settings, cancellationToken);
                    return (object?)new { ready = true };
                }, cancellationToken);
            case "listLocalModels":
                return HandleListLocalModels();
            case "installLocalModel":
            {
                var modelId = ReadString(parameters, "modelId");
                return await RunSessionModelOperationAsync(async () =>
                {
                    _translationFactory.UnloadIdleLocalRuntimes();
                    _textToSpeech.UnloadIdleLocalRuntimes();
                    await _localModelManager.InstallAsync(modelId, cancellationToken);
                    return (object?)new
                    {
                        installed = true,
                        installState = _localModelManager.GetStatus(modelId)
                            .ToString().ToLowerInvariant()
                    };
                }, cancellationToken);
            }
            case "removeLocalModel":
            {
                var modelId = ReadString(parameters, "modelId");
                return await RunSessionModelOperationAsync(async () =>
                {
                    if (await _session.UsesLocalModelAsync(modelId, cancellationToken))
                    {
                        throw new InvalidOperationException("当前会话仍在使用该模型，请先停止翻译。");
                    }
                    _translationFactory.UnloadIdleLocalRuntimes();
                    _textToSpeech.UnloadIdleLocalRuntimes();
                    var removed = await _localModelManager.RemoveAsync(modelId, cancellationToken);
                    return (object?)new { removed };
                }, cancellationToken);
            }
            case "testLocalModel":
            {
                var modelId = ReadString(parameters, "modelId");
                return await RunSessionModelOperationAsync(
                    () => TestLocalModelAsync(parameters, serializerOptions, modelId, cancellationToken),
                    cancellationToken);
            }
            case "listManagedRuntimes":
                return HandleListManagedRuntimes();
            case "probeManagedRuntime":
            {
                var runtimeProfileId = ReadString(parameters, "runtimeProfileId");
                return await _managedRuntimeManager.ProbeAsync(
                    runtimeProfileId,
                    cancellationToken).ConfigureAwait(false);
            }
            case "prepareManagedRuntime":
            {
                var runtimeProfileId = ReadString(parameters, "runtimeProfileId");
                return await RunSessionModelOperationAsync(
                    async () => await _managedRuntimeManager.PrepareAsync(
                        runtimeProfileId,
                        cancellationToken).ConfigureAwait(false),
                    cancellationToken);
            }
            case "cancelManagedRuntimePreparation":
            {
                var runtimeProfileId = ReadString(parameters, "runtimeProfileId");
                var cancelled = _managedRuntimeManager.CancelPreparation(runtimeProfileId);
                return new { cancelled };
            }
            case "removeManagedRuntime":
            {
                var runtimeProfileId = ReadString(parameters, "runtimeProfileId");
                return await RunSessionModelOperationAsync(async () =>
                {
                    if (_session.IsRunning)
                    {
                        throw new InvalidOperationException("当前翻译会话正在运行，请先停止翻译。");
                    }

                    var removed = await _managedRuntimeManager.RemoveAsync(
                        runtimeProfileId,
                        cancellationToken).ConfigureAwait(false);
                    return (object?)new { removed };
                }, cancellationToken);
            }
            case "testTranslation":
            {
                return await RunSessionModelOperationAsync(async () =>
                {
                    ApplyOptionalSettings(parameters, serializerOptions);
                    var translator = _translationFactory.Create(_settings);
                    string translated;
                    try
                    {
                        translated = await translator.TranslateAsync(
                            "Connection test",
                            LanguageCatalog.Get("en"),
                            LanguageCatalog.Get("zh"),
                            cancellationToken);
                    }
                    finally
                    {
                        await DisposeServiceAsync(translator);
                    }
                    return (object?)new { translated };
                }, cancellationToken);
            }
            case "testSpeech":
                return await RunSessionModelOperationAsync(async () =>
                {
                    ApplyOptionalSettings(parameters, serializerOptions);
                    await _textToSpeech.SpeakAsync(
                        "语音服务连接测试",
                        LanguageCatalog.Get("zh"),
                        outputDeviceId: string.Empty,
                        cancellationToken);
                    return (object?)new { spoken = true, outputDevice = "default" };
                }, cancellationToken);
            case "testVoiceOutput":
            {
                return await RunSessionModelOperationAsync(async () =>
                {
                    ApplyOptionalSettings(parameters, serializerOptions);
                    var language = _settings.OutboundSpeechContent == OutboundSpeechContent.Original
                        ? LanguageCatalog.Get(_settings.MyLanguageCode)
                        : LanguageCatalog.Get(_settings.OtherLanguageCode);
                    await _textToSpeech.SpeakAsync(
                        VoiceOutputTestText(language),
                        language,
                        _settings.VoiceOutputDeviceId,
                        cancellationToken);
                    return (object?)new
                    {
                        spoken = true,
                        deviceId = _settings.VoiceOutputDeviceId
                    };
                }, cancellationToken);
            }
            case "testVrChatOsc":
            {
                return await RunSessionModelOperationAsync(async () =>
                {
                    ApplyOptionalSettings(parameters, serializerOptions);
                    if (_vrChatOscConfigurationError is not null)
                    {
                        throw new InvalidOperationException(
                            "VRChat OSC 配置无效。",
                            _vrChatOscConfigurationError);
                    }

                    var text = TryReadString(parameters, "text")
                        ?? "VoxLink VRChat OSC test";
                    await _vrChatOsc.SendTestAsync(
                        text, cancellationToken: cancellationToken);
                    return (object?)new
                    {
                        sent = true,
                        address = _settings.VrChatOscAddress,
                        port = _settings.VrChatOscPort
                    };
                }, cancellationToken);
            }
            case "testVrOverlay":
                return await RunSessionModelOperationAsync(
                    () =>
                    {
                        ApplyOptionalSettings(parameters, serializerOptions);
                        var status = _uiHost?.TestVrOverlay() ?? "SteamVR 字幕宿主未启动";
                        return Task.FromResult<object?>(new { status });
                    },
                    cancellationToken);
            case "testDesktopOverlay":
                return await RunSessionModelOperationAsync(
                    () =>
                    {
                        ApplyOptionalSettings(parameters, serializerOptions);
                        var status = _uiHost?.TestDesktopOverlay() ?? "桌面字幕宿主未启动";
                        return Task.FromResult<object?>(new { status });
                    },
                    cancellationToken);
            case "shutdown":
                return await RunSessionModelOperationAsync(async () =>
                {
                    await _session.StopAsync();
                    ShouldShutdown = true;
                    return (object?)new { shutdown = true };
                }, cancellationToken);
            default:
                throw new InvalidOperationException($"未知引擎命令：{method}");
        }
    }

    private static bool IsManagedRuntimeMethod(string method) =>
        method is "listManagedRuntimes"
            or "probeManagedRuntime"
            or "prepareManagedRuntime"
            or "cancelManagedRuntimePreparation"
            or "removeManagedRuntime";

    private static async Task DisposeServiceAsync(object service)
    {
        if (service is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (service is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
    public string Redact(string message) => SecretRedactor.Redact(message, GetSecrets());

    public async ValueTask DisposeAsync()
    {
        Task drainTask;
        var ownsDisposal = false;
        lock (_lifecycleSync)
        {
            if (_disposeStarted)
            {
                drainTask = _disposeCompletion.Task;
            }
            else
            {
                _disposeStarted = true;
                ownsDisposal = true;
                drainTask = _activeRequests == 0
                    ? Task.CompletedTask
                    : (_requestsDrained ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }

        if (!ownsDisposal)
        {
            await drainTask.ConfigureAwait(false);
            return;
        }

        try
        {
            _shutdownCancellation.Cancel();
            await drainTask.ConfigureAwait(false);
            _session.StatusChanged -= OnStatusChanged;
            _session.MessageReceived -= OnMessageReceived;
            _session.PartialMessageReceived -= OnPartialMessageReceived;
            _session.ErrorOccurred -= OnErrorOccurred;
            _session.WarningOccurred -= OnWarningOccurred;
            _session.ModelProgress -= OnModelProgress;
            _localModelManager.ModelProgress -= OnLocalModelProgress;
            _managedRuntimeManager.RuntimeProgress -= OnManagedRuntimeProgress;
            _vrChatOsc.SendFailed -= OnVrChatOscSendFailed;
            await _session.DisposeAsync().ConfigureAwait(false);
            await _translationFactory.DisposeAsync().ConfigureAwait(false);
            await _vrChatOsc.DisposeAsync().ConfigureAwait(false);
            _uiHost?.Dispose();
            _httpClient.Dispose();
            if (_ownsLocalModelOrchestrator)
            {
                await _localModelOrchestrator.DisposeAsync().ConfigureAwait(false);
            }

            if (_ownsManagedRuntimeManager)
            {
                await _managedRuntimeManager.DisposeAsync().ConfigureAwait(false);
            }

            if (_ownsLocalModelManager)
            {
                if (_localModelManager is IAsyncDisposable asyncManager)
                {
                    await asyncManager.DisposeAsync().ConfigureAwait(false);
                }
                else if (_localModelManager is IDisposable disposableManager)
                {
                    disposableManager.Dispose();
                }
            }
            _sessionModelGate.Dispose();
            _shutdownCancellation.Dispose();
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }

    private RequestLease EnterRequest()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            _activeRequests = checked(_activeRequests + 1);
            return new RequestLease(this);
        }
    }

    private void ExitRequest()
    {
        TaskCompletionSource? drained = null;
        lock (_lifecycleSync)
        {
            _activeRequests = Math.Max(0, _activeRequests - 1);
            if (_disposeStarted && _activeRequests == 0)
            {
                drained = _requestsDrained;
            }
        }

        drained?.TrySetResult();
    }

    private async Task<T> RunSessionModelOperationAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await _sessionModelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _sessionModelGate.Release();
        }
    }

    private void ApplySettings(AppSettings settings, bool applyTextToSpeech = true)
    {
        NormalizeSettings(settings);
        _settings = settings.Clone();
        if (applyTextToSpeech)
        {
            _textToSpeech.Configure(_settings);
        }
        try
        {
            _vrChatOsc.Configure(
                _settings.VrChatChatboxEnabled,
                _settings.VrChatOscAddress,
                _settings.VrChatOscPort);
            _vrChatOscConfigurationError = null;
        }
        catch (InvalidOperationException exception)
        {
            _vrChatOsc.Configure(enabled: false, "127.0.0.1", 9000);
            _vrChatOscConfigurationError = exception;
            _notify("error", new
            {
                message = "VRChat OSC 配置无效，Chatbox 输出已停用。",
                detail = exception.Message
            });
        }
        _uiHost?.Configure(_settings);
    }

    internal static (string Text, LanguageOption Language) ResolveGeneratedSpeech(
        string prompt,
        string generated,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(settings);
        var useOriginal = settings.OutboundSpeechContent == OutboundSpeechContent.Original;
        return useOriginal
            ? (prompt, LanguageCatalog.Get(settings.MyLanguageCode))
            : (generated, LanguageCatalog.Get(settings.OtherLanguageCode));
    }

    internal static void NormalizeSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // 旧前端可能仍指向已下线的应用托管翻译模型；统一安全回退到公共免密翻译。
        if (settings.TranslationProvider is TranslationProvider.ManagedHyMt
            or TranslationProvider.ManagedM2M100
            or TranslationProvider.ManagedSmall100)
        {
            settings.TranslationProvider = TranslationProvider.GoogleWeb;
        }

        settings.VoiceThreshold = Math.Clamp(settings.VoiceThreshold, 0.005, 0.08);
        settings.SilenceDurationMs = Math.Clamp(settings.SilenceDurationMs, 300, 1_800);
        settings.KokoroSpeakerId = Math.Clamp(
            settings.KokoroSpeakerId,
            LocalKokoroTtsRuntime.MinimumSpeakerId,
            LocalKokoroTtsRuntime.MaximumSpeakerId);
        settings.KokoroSpeed = Math.Clamp(
            double.IsFinite(settings.KokoroSpeed) ? settings.KokoroSpeed : 1.0,
            LocalKokoroTtsRuntime.MinimumSpeed,
            LocalKokoroTtsRuntime.MaximumSpeed);
        var ttsVolume = settings.TtsOutputVolume;
        settings.TtsOutputVolume = double.IsFinite(ttsVolume) ? Math.Clamp(ttsVolume, 0.5, 2.0) : 1.0;
        settings.DesktopOverlayFontSize = Math.Clamp(settings.DesktopOverlayFontSize, 14, 40);
        if (settings.DesktopOverlayHeight is { } overlayHeight)
        {
            settings.DesktopOverlayHeight = Math.Clamp(overlayHeight, 88, 2000);
        }
        settings.DesktopOverlayAutoHideSeconds = Math.Clamp(
            settings.DesktopOverlayAutoHideSeconds,
            3,
            300);

        // tiny / small 已从产品中移除：旧版本设置自动升级到 base。
        if (settings.WhisperModel is "tiny" or "small")
        {
            settings.WhisperModel = "base";
        }
    }

    private static string VoiceOutputTestText(LanguageOption language) => language.Code switch
    {
        "zh" => "VoxLink 语音路由测试成功",
        "ja" => "VoxLink 音声ルートのテストです",
        "ko" => "VoxLink 음성 경로 테스트입니다",
        "es" => "Prueba de audio de VoxLink",
        "fr" => "Test audio de VoxLink",
        "de" => "VoxLink Audiotest",
        "it" => "Test audio VoxLink",
        "pt" => "Teste de áudio do VoxLink",
        "ru" => "Проверка звука VoxLink",
        _ => "VoxLink voice route test"
    };
    private void ApplyOptionalSettings(
        JsonElement parameters,
        JsonSerializerOptions serializerOptions)
    {
        if (parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("settings", out _))
        {
            ApplySettings(
                ReadSettings(parameters, serializerOptions),
                applyTextToSpeech: !_session.IsRunning);
        }
    }

    private object GetBootstrap() => new
    {
        engineVersion = typeof(EngineHost).Assembly.GetName().Version?.ToString(3) ?? "unknown",
        running = _session.IsRunning,
        languages = LanguageCatalog.All,
        captureDevices = _audioDevices.GetCaptureDevices(),
        renderDevices = _audioDevices.GetRenderDevices()
    };

    private static AppSettings ReadSettings(
        JsonElement parameters,
        JsonSerializerOptions serializerOptions)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("settings", out var settingsElement))
        {
            throw new InvalidOperationException("请求缺少 settings 配置。");
        }

        return settingsElement.Deserialize<AppSettings>(serializerOptions)
            ?? throw new InvalidOperationException("无法解析 settings 配置。");
    }

    private static string ReadString(JsonElement parameters, string propertyName) =>
        TryReadString(parameters, propertyName) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"请求缺少 {propertyName}。");

    private static string? TryReadString(JsonElement parameters, string propertyName) =>
        parameters.ValueKind == JsonValueKind.Object
        && parameters.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;

    private static bool ReadBool(JsonElement parameters, string propertyName) =>
        parameters.ValueKind == JsonValueKind.Object
        && parameters.TryGetProperty(propertyName, out var property)
        && property.ValueKind is JsonValueKind.True or JsonValueKind.False
        && property.GetBoolean();

    private IEnumerable<string> GetSecrets()
    {
        yield return _settings.OpenAiApiKey;
        yield return _settings.AsrApiKey;
        yield return _settings.TextToSpeechApiKey;
        foreach (var value in _settings.OpenAiHeaders.Values)
        {
            yield return value;
        }

        foreach (var value in _settings.AsrHeaders.Values)
        {
            yield return value;
        }

        foreach (var value in _settings.TextToSpeechHeaders.Values)
        {
            yield return value;
        }
    }

    private void OnStatusChanged(object? sender, SessionStatusEventArgs eventArgs) =>
        _notify("status", new
        {
            message = eventArgs.Message,
            activity = eventArgs.Activity.ToString().ToLowerInvariant(),
            running = _session.IsRunning
        });

    private void OnMessageReceived(object? sender, ConversationMessage message)
    {
        _notify("message", ToMessagePayload(message));
        if (_settings.ShowOverlay || _settings.ShowVrOverlay)
        {
            _uiHost?.ShowSubtitle(message);
        }

        if (message.Direction == TranslationDirection.Inbound)
        {
            if (message.IsFinal && !message.TranscriptionOnly
                && !string.IsNullOrWhiteSpace(message.TranslatedText))
            {
                TrackRecentInbound(message);
            }

            return;
        }

        var chatboxText = ComposeVrChatMessage(message, _settings);
        if (chatboxText is not null)
        {
            if (IsEchoOfRecentInbound(message.TranslatedText))
            {
                if (Interlocked.Exchange(ref _echoSuppressed, 1) == 0)
                {
                    _notify("warning", new
                    {
                        message = "检测到麦克风拾取了系统音频中的他人语音回声，已抑制，未发送到 Chatbox。",
                        detail = "如经常出现，请检查麦克风是否误选为立体声混音等回环设备。"
                    });
                }

                return;
            }

            _vrChatOsc.TryQueue(chatboxText);
        }
    }

    /// <summary>
    /// 保留最近一段时间内的他人语音译文，用于识别麦克风拾取到的系统音频回声。
    /// </summary>
    private void TrackRecentInbound(ConversationMessage message)
    {
        lock (_recentInboundGate)
        {
            _recentInbound.Add(message);
            if (_recentInbound.Count > MaxRecentInbound)
            {
                _recentInbound.RemoveAt(0);
            }
        }
    }

    private bool IsEchoOfRecentInbound(string? text)
    {
        ConversationMessage[] recent;
        lock (_recentInboundGate)
        {
            var cutoff = DateTimeOffset.UtcNow - EchoWindow;
            _recentInbound.RemoveAll(item => item.Timestamp < cutoff);
            recent = _recentInbound.ToArray();
        }

        return IsEchoText(text, recent, EchoSimilarityThreshold);
    }

    /// <summary>
    /// 出站译文与最近他人语音译文高度相似时视为麦克风拾取到的系统音频回声。
    /// </summary>
    internal static bool IsEchoText(
        string? outboundTranslatedText,
        IEnumerable<ConversationMessage> recentInbound,
        double threshold)
    {
        if (string.IsNullOrWhiteSpace(outboundTranslatedText))
        {
            return false;
        }

        return recentInbound.Any(inbound =>
            CharBigramJaccard(outboundTranslatedText, inbound.TranslatedText) >= threshold);
    }


    internal static double CharBigramJaccard(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0;
        }

        var a = NormalizeForCompare(left);
        var b = NormalizeForCompare(right);
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (a.Length < 2 && b.Length < 2)
        {
            return a == b ? 1 : 0;
        }

        static IEnumerable<string> Bigrams(string text)
        {
            for (var i = 0; i < text.Length - 1; i++)
            {
                yield return text.Substring(i, 2);
            }
        }

        var leftBigrams = new HashSet<string>(Bigrams(a), StringComparer.Ordinal);
        var rightBigrams = new HashSet<string>(Bigrams(b), StringComparer.Ordinal);
        var intersection = leftBigrams.Count(bigram => rightBigrams.Contains(bigram));
        var union = leftBigrams.Count + rightBigrams.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static string NormalizeForCompare(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());


    internal static string? ComposeVrChatMessage(
        ConversationMessage message,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(settings);
        return settings.VrChatChatboxEnabled
            && message.Direction is TranslationDirection.Outbound or TranslationDirection.Typed
            && message.IsFinal
            && !message.TranscriptionOnly
                ? VrChatOscSender.ComposeTranslation(
                    message.TranslatedText,
                    message.SourceText,
                    settings.VrChatIncludeSourceText,
                    message.SecondaryTranslatedText)
                : null;
    }

    private void OnPartialMessageReceived(object? sender, ConversationMessage message)
    {
        _notify("partialMessage", ToMessagePayload(message));
        if (_settings.ShowOverlay || _settings.ShowVrOverlay)
        {
            _uiHost?.ShowSubtitle(message);
        }
    }

    internal static object ToMessagePayload(ConversationMessage message) => new
    {
        direction = message.Direction.ToString().ToLowerInvariant(),
        sourceText = message.SourceText,
        translatedText = message.TranslatedText,
        secondaryTranslatedText = message.SecondaryTranslatedText,
        speakerId = message.SpeakerId,
        speakerLabel = message.SpeakerLabel,
        utteranceId = message.UtteranceId,
        isFinal = message.IsFinal,
        transcriptionOnly = message.TranscriptionOnly,
        timestamp = message.Timestamp
    };
    private void OnVrChatOscSendFailed(object? sender, Exception exception) =>
        _notify("error", new
        {
            message = "VRChat OSC 发送失败。",
            detail = Redact(exception.Message)
        });
    private void OnErrorOccurred(object? sender, SessionErrorEventArgs eventArgs) =>
        _notify("error", new
        {
            message = Redact(eventArgs.Message),
            detail = Redact(eventArgs.Exception.Message)
        });

    private void OnWarningOccurred(object? sender, string message) =>
        _notify("warning", new
        {
            message
        });


    /// <summary>
    /// 本地模型目录列表：只下发展示与状态字段，不含下载 URL、SHA-256 或真实路径。
    /// </summary>
    private object HandleListLocalModels() => new
    {
        models = _localModelManager.List()
            .Select(definition =>
            {
                var status = _localModelManager.GetStatus(definition.Id);
                return new
                {
                    id = definition.Id,
                    name = definition.Name,
                    category = definition.Category.ToString().ToLowerInvariant(),
                    supportLevel = definition.SupportLevel.ToString().ToLowerInvariant(),
                    runtime = definition.Runtime.ToString().ToLowerInvariant(),
                    parameters = definition.Parameters,
                    numericParameterBillions = definition.NumericParameterBillions,
                    license = definition.License,
                    languages = definition.Languages,
                    requirements = definition.Requirements,
                    sourceUrl = definition.SourceUrl,
                    description = definition.Description,
                    unavailableReason = definition.UnavailableReason,
                    downloadBytes = definition.DownloadBytes,
                    installed = status == LocalModelInstallState.Installed,
                    installState = status.ToString().ToLowerInvariant(),
                    isInstallable = definition.IsInstallable
                };
            })
            .ToArray()
    };

    private object HandleListManagedRuntimes() => new
    {
        runtimes = _managedRuntimeManager.List()
            .Select(definition => new
            {
                id = definition.Id,
                platform = definition.Platform.ToString().ToLowerInvariant(),
                pythonVersion = definition.PythonVersion,
                requiresNvidiaGpu = definition.RequiresNvidiaGpu,
                minimumGpuMemoryBytes = definition.MinimumGpuMemoryBytes
            })
            .ToArray()
    };

    /// <summary>
    /// 本地模型冒烟测试：按类别真实跑一次最小推理（翻译/播放/识别），
    /// 结果通过 { ok, detail } 返回给界面展示。
    /// </summary>
    private async Task<object> TestLocalModelAsync(
        JsonElement parameters,
        JsonSerializerOptions serializerOptions,
        string modelId,
        CancellationToken cancellationToken)
    {
        var definition = _localModelManager.List().FirstOrDefault(item =>
            item.Id.Equals(modelId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"未找到本地模型：{modelId}。");
        if (!definition.IsInstallable)
        {
            throw new InvalidOperationException($"{definition.Name} 不支持一键部署，无法测试。");
        }

        if (_localModelManager.GetStatus(modelId) != LocalModelInstallState.Installed)
        {
            throw new InvalidOperationException($"{definition.Name} 还没安装，先安装再测试。");
        }

        ApplyOptionalSettings(parameters, serializerOptions);
        return definition.Category switch
        {
            LocalModelCategory.Translation => await TestLocalTranslationAsync(definition, cancellationToken),
            LocalModelCategory.Tts => await TestLocalTtsAsync(cancellationToken),
            LocalModelCategory.Asr => await TestLocalAsrAsync(definition, cancellationToken),
            _ => throw new InvalidOperationException($"{definition.Name} 暂不支持测试。")
        };
    }

    private async Task<object> TestLocalTranslationAsync(
        LocalModelDefinition definition,
        CancellationToken cancellationToken)
    {
        var testSettings = _settings.Clone();
        testSettings.TranslationProvider = definition.Id switch
        {
            LocalModelIds.MiniCpm51BGguf => TranslationProvider.LocalMiniCpm,
            LocalModelIds.HyMt15Gguf => TranslationProvider.LocalHyMtGguf,
            _ => throw new InvalidOperationException("该模型不是本地翻译模型。")
        };
        var translator = _translationFactory.Create(testSettings);
        string translated;
        try
        {
            translated = await translator.TranslateAsync(
                "你好，世界！",
                LanguageCatalog.Get("zh"),
                LanguageCatalog.Get("en"),
                cancellationToken);
        }
        finally
        {
            await DisposeServiceAsync(translator);
        }

        if (string.IsNullOrWhiteSpace(translated))
        {
            throw new InvalidOperationException("翻译模型返回了空结果。");
        }

        return (object)new { ok = true, detail = translated };
    }

    private async Task<object> TestLocalTtsAsync(CancellationToken cancellationToken)
    {
        var testSettings = _settings.Clone();
        testSettings.UseRemoteTextToSpeech = false;
        testSettings.UseLocalKokoroTextToSpeech = true;
        _textToSpeech.Configure(testSettings);
        try
        {
            await _textToSpeech.SpeakAsync(
                "本地语音测试",
                LanguageCatalog.Get("zh"),
                outputDeviceId: string.Empty,
                cancellationToken);
        }
        finally
        {
            _textToSpeech.Configure(_settings);
        }

        return (object)new { ok = true, detail = "已播放测试语音，请确认能听到声音" };
    }

    private async Task<object> TestLocalAsrAsync(
        LocalModelDefinition definition,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.MicrophoneDeviceId))
        {
            throw new InvalidOperationException("请先在「音频设备」页选择麦克风，再测试语音识别。");
        }

        var testSettings = _settings.Clone();
        if (definition.Id == LocalModelIds.MossTranscribeDiarize)
        {
            testSettings.AsrProtocol = AsrProtocol.LocalManagedMoss;
        }
        else if (definition.Id == LocalModelIds.SenseVoiceSmall)
        {
            testSettings.AsrProtocol = AsrProtocol.LocalSenseVoice;
        }
        else if (definition.Id == LocalModelIds.FireRedAsr2Ctc)
        {
            testSettings.AsrProtocol = AsrProtocol.LocalFireRedAsr2Ctc;
        }
        else
        {
            testSettings.AsrProtocol = AsrProtocol.LocalWhisper;
            testSettings.WhisperModel = definition.WhisperModelName ?? "base";
        }

        await using var recognizer = _asrFactory.Create(testSettings);
        await recognizer.PrepareAsync(cancellationToken);
        var samples = await CaptureMicrophoneSamplesAsync(
            _settings.MicrophoneDeviceId,
            TimeSpan.FromSeconds(4),
            cancellationToken);
        var result = await recognizer.TranscribeAsync(
            AudioUtterance.FromSamples(samples, LocalAsrTestSampleRate),
            LanguageCatalog.Get(_settings.MyLanguageCode),
            cancellationToken);
        var text = result.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return (object)new { ok = false, detail = "没听清，请对着麦克风说一句话再试" };
        }

        return (object)new { ok = true, detail = text };
    }

    private const int LocalAsrTestSampleRate = 16000;

    /// <summary>采集一段时间的麦克风 PCM（16 kHz 单声道），用于本地识别模型测试。</summary>
    private async Task<float[]> CaptureMicrophoneSamplesAsync(
        string deviceId,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        await using var capture = new WasapiSpeechCapture(
            deviceId,
            loopback: false,
            threshold: 0.005,
            silenceDurationMs: 650,
            smartSentenceSegmentation: false);
        var chunks = new List<float[]>();
        long collectedSamples = 0;
        void OnChunk(object? sender, float[] samples)
        {
            chunks.Add(samples);
            collectedSamples += samples.Length;
        }

        capture.PcmChunkReady += OnChunk;
        try
        {
            capture.Start();
            var deadline = DateTimeOffset.UtcNow + duration;
            while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                if (collectedSamples >= LocalAsrTestSampleRate * duration.TotalSeconds)
                {
                    break;
                }

                await Task.Delay(100, cancellationToken);
            }
        }
        finally
        {
            capture.PcmChunkReady -= OnChunk;
            capture.Stop();
        }

        if (collectedSamples == 0)
        {
            throw new InvalidOperationException("没有采集到麦克风音频，请检查麦克风是否可用。");
        }

        var samples = new float[collectedSamples];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(samples, offset);
            offset += chunk.Length;
        }

        return samples;
    }

    private void OnManagedRuntimeProgress(
        object? sender,
        ManagedRuntimeProgressEventArgs eventArgs) =>
        _notify("runtimeProgress", new
        {
            runtimeProfileId = eventArgs.RuntimeProfileId,
            status = eventArgs.Status,
            progress = eventArgs.Progress
        });

    private void OnModelProgress(object? sender, ModelProgressEventArgs eventArgs) =>
        _notify("modelProgress", CreateModelProgressPayload(
            modelId: null,
            category: null,
            eventArgs.Status,
            eventArgs.Progress));

    private void OnLocalModelProgress(object? sender, LocalModelProgressEventArgs eventArgs) =>
        _notify("modelProgress", CreateModelProgressPayload(
            eventArgs.ModelId,
            eventArgs.Category.ToString().ToLowerInvariant(),
            eventArgs.Status,
            eventArgs.Progress));

    /// <summary>
    /// modelProgress 事件 payload。新增 modelId/category 字段；旧 ASR/说话人
    /// 模型进度保持 modelId 为 null 的兼容语义。
    /// </summary>
    internal static object CreateModelProgressPayload(
        string? modelId,
        string? category,
        string status,
        double? progress) => new
    {
        modelId,
        category,
        status,
        progress
    };
    private sealed class RequestLease(EngineHost owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ExitRequest();
            }
        }
    }
}
