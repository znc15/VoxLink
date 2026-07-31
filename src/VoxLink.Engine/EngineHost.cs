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
    private readonly VrChatOscSender _vrChatOsc = new();
    private readonly UiHost? _uiHost;
    private AppSettings _settings = new();
    private Exception? _vrChatOscConfigurationError;
    private bool _disposed;

    public EngineHost(Action<string, object> notify)
        : this(notify, startUiHost: true)
    {
    }

    internal EngineHost(Action<string, object> notify, bool startUiHost)
    {
        _notify = notify;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VoxLink.Engine/1.0");
        _asrFactory = new AsrRecognizerFactory(_httpClient);
        _translationFactory = new TranslationServiceFactory(_httpClient);
        _textToSpeech = new HybridTextToSpeechService(_httpClient);
        _session = new TranslationSession(_asrFactory, _translationFactory, _textToSpeech);
        _vrChatOsc.SendFailed += OnVrChatOscSendFailed;
        if (startUiHost)
        {
            _uiHost = new UiHost(action => _notify("hotkey", new { action }));
        }
        _session.StatusChanged += OnStatusChanged;
        _session.MessageReceived += OnMessageReceived;
        _session.PartialMessageReceived += OnPartialMessageReceived;
        _session.ErrorOccurred += OnErrorOccurred;
        _session.ModelProgress += OnModelProgress;
    }

    public bool ShouldShutdown { get; private set; }

    public async Task<object?> HandleAsync(
        string method,
        JsonElement parameters,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        switch (method)
        {
            case "initialize":
            case "configure":
                ApplySettings(ReadSettings(parameters, serializerOptions));
                return GetBootstrap();
            case "getBootstrap":
                return GetBootstrap();
            case "startSession":
                ApplySettings(ReadSettings(parameters, serializerOptions));
                await _session.StartAsync(_settings, cancellationToken);
                return new { running = _session.IsRunning };
            case "stopSession":
                await _session.StopAsync();
                return new { running = false };
            case "translate":
            {
                ApplyOptionalSettings(parameters, serializerOptions);
                var text = ReadString(parameters, "text");
                return await _session.TranslateTypedTextAsync(text, _settings, cancellationToken);
            }
            case "generate":
            {
                ApplyOptionalSettings(parameters, serializerOptions);
                var prompt = ReadString(parameters, "prompt");
                var chatService = _translationFactory.CreateChatService(_settings);
                var generated = await chatService.GenerateAsync(prompt, cancellationToken);
                if (_settings.VrChatChatboxEnabled)
                {
                    _vrChatOsc.TryQueue(VrChatOscSender.ComposeTranslation(
                        generated,
                        prompt,
                        _settings.VrChatIncludeSourceText));
                }
                if (ReadBool(parameters, "speak"))
                {
                    var speech = ResolveGeneratedSpeech(prompt, generated, _settings);
                    await _textToSpeech.SpeakAsync(
                        speech.Text,
                        speech.Language,
                        _settings.VoiceOutputDeviceId,
                        cancellationToken);
                }

                return new { text = generated };
            }
            case "speak":
            {
                ApplyOptionalSettings(parameters, serializerOptions);
                var text = ReadString(parameters, "text");
                var languageCode = TryReadString(parameters, "languageCode")
                    ?? _settings.OtherLanguageCode;
                await _textToSpeech.SpeakAsync(
                    text,
                    LanguageCatalog.Get(languageCode),
                    _settings.VoiceOutputDeviceId,
                    cancellationToken);
                return new { spoken = true };
            }
            case "prepareModel":
                ApplyOptionalSettings(parameters, serializerOptions);
                await _asrFactory.PrepareAsync(_settings, cancellationToken);
                return new { ready = true };
            case "testTranslation":
            {
                ApplyOptionalSettings(parameters, serializerOptions);
                var translated = await _translationFactory.Create(_settings).TranslateAsync(
                    "Connection test",
                    LanguageCatalog.Get("en"),
                    LanguageCatalog.Get("zh"),
                    cancellationToken);
                return new { translated };
            }
            case "testSpeech":
                ApplyOptionalSettings(parameters, serializerOptions);
                if (_settings.UseRemoteTextToSpeech)
                {
                    await _textToSpeech.ValidateConfiguredRemoteAsync(
                        "语音服务连接测试",
                        LanguageCatalog.Get("zh"),
                        cancellationToken);
                }
                else
                {
                    await _textToSpeech.SpeakAsync(
                        "语音服务连接成功",
                        LanguageCatalog.Get("zh"),
                        _settings.VoiceOutputDeviceId,
                        cancellationToken);
                }
                return new { spoken = true };
            case "testVoiceOutput":
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
                return new { spoken = true, deviceId = _settings.VoiceOutputDeviceId };
            }
            case "testVrChatOsc":
            {
                ApplyOptionalSettings(parameters, serializerOptions);
                if (_vrChatOscConfigurationError is not null)
                {
                    throw new InvalidOperationException(
                        "VRChat OSC 配置无效。",
                        _vrChatOscConfigurationError);
                }

                var text = TryReadString(parameters, "text") ?? "VoxLink VRChat OSC test";
                await _vrChatOsc.SendTestAsync(text, cancellationToken: cancellationToken);
                return new
                {
                    sent = true,
                    address = _settings.VrChatOscAddress,
                    port = _settings.VrChatOscPort
                };
            }
            case "testVrOverlay":
            {
                ApplyOptionalSettings(parameters, serializerOptions);
                var status = _uiHost?.TestVrOverlay() ?? "SteamVR 字幕宿主未启动";
                return new { status };
            }
            case "shutdown":
                await _session.StopAsync();
                ShouldShutdown = true;
                return new { shutdown = true };
            default:
                throw new InvalidOperationException($"未知引擎命令：{method}");
        }
    }

    public string Redact(string message) => SecretRedactor.Redact(message, GetSecrets());

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.StatusChanged -= OnStatusChanged;
        _session.MessageReceived -= OnMessageReceived;
        _session.PartialMessageReceived -= OnPartialMessageReceived;
        _session.ErrorOccurred -= OnErrorOccurred;
        _session.ModelProgress -= OnModelProgress;
        _vrChatOsc.SendFailed -= OnVrChatOscSendFailed;
        await _session.DisposeAsync();
        await _vrChatOsc.DisposeAsync();
        _uiHost?.Dispose();
        _httpClient.Dispose();
    }

    private void ApplySettings(AppSettings settings)
    {
        NormalizeSettings(settings);
        _settings = settings.Clone();
        _textToSpeech.Configure(_settings);
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
        settings.VoiceThreshold = Math.Clamp(settings.VoiceThreshold, 0.005, 0.08);
        settings.SilenceDurationMs = Math.Clamp(settings.SilenceDurationMs, 300, 1_800);
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
            ApplySettings(ReadSettings(parameters, serializerOptions));
        }
    }

    private object GetBootstrap() => new
    {
        engineVersion = "1.0.0",
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
        if (message.Direction == TranslationDirection.Inbound)
        {
            if (_settings.ShowOverlay || _settings.ShowVrOverlay)
            {
                _uiHost?.ShowSubtitle(message);
            }

            return;
        }

        var chatboxText = ComposeVrChatMessage(message, _settings);
        if (chatboxText is not null)
        {
            _vrChatOsc.TryQueue(chatboxText);
        }
    }

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
                    settings.VrChatIncludeSourceText)
                : null;
    }

    private void OnPartialMessageReceived(object? sender, ConversationMessage message)
    {
        _notify("partialMessage", ToMessagePayload(message));
        if (message.Direction == TranslationDirection.Inbound
            && (_settings.ShowOverlay || _settings.ShowVrOverlay))
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

    private void OnModelProgress(object? sender, ModelProgressEventArgs eventArgs) =>
        _notify("modelProgress", new
        {
            status = eventArgs.Status,
            progress = eventArgs.Progress
        });
}
