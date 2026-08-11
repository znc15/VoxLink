using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Speech.Synthesis;
using EdgeTTS.DotNet;
using EdgeTTS.DotNet.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoxLink.Models;

namespace VoxLink.Services;

public sealed class HybridTextToSpeechService :
    ITextToSpeechService,
    IConfigurableTextToSpeechService
{
    private readonly HttpClient _httpClient;
    private readonly RemoteTextToSpeechClient _remoteClient;
    private readonly LocalKokoroTtsRuntime? _localKokoroRuntime;
    private readonly LocalModelOrchestrator? _managedOrchestrator;
    private ManagedModelHostTtsSynthesizer? _managedTts;

    public ManagedTtsModel? ManagedModel => _managedTts?.Model;
    private readonly bool _enableEdgeTts;
    private readonly SemaphoreSlim _speechGate = new(1, 1);
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _playbackSync = new();
    private WasapiOut? _activeOutput;
    private SpeechSynthesizer? _activeSynthesizer;
    private CancellationTokenSource? _activeSpeech;
    private volatile bool _isSpeaking;
    private AppSettings _settings = new();
    private int _disposeState;
    public HybridTextToSpeechService(HttpClient httpClient)
        : this(httpClient, enableEdgeTts: true, localModelManager: null)
    {
    }

    internal HybridTextToSpeechService(HttpClient httpClient, bool enableEdgeTts)
        : this(httpClient, enableEdgeTts, localModelManager: null)
    {
    }

    internal HybridTextToSpeechService(
        HttpClient httpClient,
        bool enableEdgeTts,
        ILocalModelManager? localModelManager,
        LocalModelOrchestrator? managedOrchestrator = null)
    {
        _httpClient = httpClient;
        _remoteClient = new RemoteTextToSpeechClient(httpClient);
        _localKokoroRuntime = localModelManager is null ? null : new LocalKokoroTtsRuntime(localModelManager);
        _managedOrchestrator = managedOrchestrator;
        _enableEdgeTts = enableEdgeTts;
    }

    public bool IsSpeaking => _isSpeaking;

    internal bool UnloadIdleLocalRuntimes() => _localKokoroRuntime?.UnloadWhenIdle() ?? false;

    public void Configure(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = settings.Clone();
        snapshot.OpenAiHeaders = new Dictionary<string, string>(
            settings.OpenAiHeaders,
            StringComparer.OrdinalIgnoreCase);
        snapshot.TextToSpeechHeaders = new Dictionary<string, string>(
            settings.TextToSpeechHeaders,
            StringComparer.OrdinalIgnoreCase);
        lock (_playbackSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            _settings = snapshot;
        }

        if (!snapshot.UseLocalKokoroTextToSpeech)
        {
            _localKokoroRuntime?.UnloadWhenIdle();
        }
        lock (_playbackSync)
        {
            var desiredManagedModel = snapshot.ManagedTtsModel;
            if (desiredManagedModel is ManagedTtsModel desired && _managedOrchestrator is not null)
            {
                if (_managedTts is null || _managedTts.Model != desired)
                {
                    _managedTts?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    _managedTts = new ManagedModelHostTtsSynthesizer(
                        _managedOrchestrator,
                        desired);
                }
            }
            else
            {
                _managedTts?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _managedTts = null;
            }
        }
    }

    public async Task ValidateConfiguredRemoteAsync(
        string text,
        LanguageOption language,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        AppSettings settings;
        lock (_playbackSync)
        {
            settings = _settings.Clone();
        }

        if (!settings.UseRemoteTextToSpeech)
        {
            throw new InvalidOperationException("远程语音服务尚未启用。");
        }

        _ = await _remoteClient.SynthesizeAsync(
            text,
            language,
            settings,
            cancellationToken);
    }
    public IReadOnlyList<string> GetInstalledVoices(LanguageOption language)
    {
        using var synthesizer = new SpeechSynthesizer();
        return synthesizer.GetInstalledVoices()
            .Where(voice => voice.Enabled && MatchesLanguage(voice.VoiceInfo.Culture, language))
            .Select(voice => voice.VoiceInfo.Name)
            .ToArray();
    }

    public async Task SpeakAsync(
        string text,
        LanguageOption language,
        string? outputDeviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        await _speechGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            AppSettings settings;
            lock (_playbackSync)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
                _activeSpeech?.Cancel();
                _activeSpeech?.Dispose();
                _activeSpeech = linkedCancellation;
                _isSpeaking = true;
                settings = _settings.Clone();
            }

            try
            {
                if (settings.UseLocalKokoroTextToSpeech)
                {
                    var localRuntime = _localKokoroRuntime
                        ?? throw new InvalidOperationException("本地 Kokoro 运行时未配置。");
                    if (!language.Code.Equals("zh", StringComparison.OrdinalIgnoreCase)
                        && !language.Code.Equals("en", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new NotSupportedException($"本地 Kokoro 暂不支持语言代码 {language.Code}。");
                    }

                    var generated = await localRuntime.GenerateAsync(
                        text,
                        settings.KokoroSpeakerId,
                        settings.KokoroSpeed,
                        linkedCancellation.Token);
                    await PlayFloatAudioAsync(
                        generated.Samples,
                        generated.SampleRate,
                        outputDeviceId,
                        linkedCancellation.Token);
                    return;
                }

                if (settings.ManagedTtsModel is { } managedModel)
                {
                    var managed = _managedTts
                        ?? throw new InvalidOperationException("托管语音模型未配置。");
                    var (wavPath, _) = await managed.SynthesizeAsync(
                        text,
                        language,
                        string.IsNullOrWhiteSpace(settings.ManagedTtsReferenceAudioPath)
                            ? null
                            : settings.ManagedTtsReferenceAudioPath,
                        string.IsNullOrWhiteSpace(settings.ManagedTtsReferenceText)
                            ? null
                            : settings.ManagedTtsReferenceText,
                        linkedCancellation.Token);
                    using var reader = new AudioFileReader(wavPath);
                    await PlayAsync(reader, outputDeviceId, linkedCancellation.Token);
                    return;
                }
                if (settings.UseRemoteTextToSpeech)
                {
                    try
                    {
                        var audio = await _remoteClient.SynthesizeAsync(
                            text,
                            language,
                            settings,
                            linkedCancellation.Token);
                        await PlayEncodedAudioAsync(
                            audio,
                            outputDeviceId,
                            linkedCancellation.Token);
                        return;
                    }
                    catch (Exception exception) when (
                        IsRecoverableRemoteFailure(exception)
                        && !linkedCancellation.IsCancellationRequested)
                    {
                    }
                }

                await SpeakWithBuiltInFallbacksAsync(
                    text,
                    language,
                    outputDeviceId,
                    linkedCancellation.Token);
            }
            finally
            {
                lock (_playbackSync)
                {
                    if (ReferenceEquals(_activeSpeech, linkedCancellation))
                    {
                        _activeSpeech = null;
                        _isSpeaking = false;
                    }
                }
            }
        }
        finally
        {
            _speechGate.Release();
        }
    }

    private async Task SpeakWithBuiltInFallbacksAsync(
        string text,
        LanguageOption language,
        string? outputDeviceId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_enableEdgeTts)
            {
                throw new EdgeTTSException("Edge TTS disabled for this service instance.");
            }

            await SpeakWithEdgeAsync(text, language, outputDeviceId, cancellationToken);
        }
        catch (Exception exception) when (
            IsRecoverableOnlineFailure(exception)
            && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SpeakWithGoogleAsync(text, language, outputDeviceId, cancellationToken);
            }
            catch (Exception googleException) when (
                IsRecoverableOnlineFailure(googleException)
                && !cancellationToken.IsCancellationRequested)
            {
                await SpeakWithWindowsAsync(text, language, outputDeviceId, cancellationToken);
            }
        }
    }
    public void Stop()
    {
        lock (_playbackSync)
        {
            _activeSpeech?.Cancel();
            _activeOutput?.Stop();
            _activeSynthesizer?.SpeakAsyncCancelAll();
            _localKokoroRuntime?.UnloadWhenIdle();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            await _disposeCompletion.Task;
            return;
        }

        try
        {
            Stop();
            await _speechGate.WaitAsync();
            try
            {
                lock (_playbackSync)
                {
                    _activeOutput?.Dispose();
                    _activeOutput = null;
                    _activeSynthesizer?.Dispose();
                    _activeSynthesizer = null;
                    _activeSpeech?.Dispose();
                    _activeSpeech = null;
                    _localKokoroRuntime?.Dispose();
                    _managedTts?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    _managedTts = null;
                    _isSpeaking = false;
                }
            }
            finally
            {
                _speechGate.Release();
            }

            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }

    private async Task SpeakWithEdgeAsync(
        string text,
        LanguageOption language,
        string? outputDeviceId,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var request = new Communicate(text, voice: GetEdgeVoice(language));
        using var audio = new MemoryStream();
        await foreach (var chunk in request.StreamAsync(timeout.Token))
        {
            if (chunk is AudioChunk audioChunk)
            {
                await audio.WriteAsync(audioChunk.Data, timeout.Token);
            }
        }

        if (audio.Length < 256)
        {
            throw new InvalidDataException("Edge 在线语音服务返回的数据不完整。");
        }

        audio.Position = 0;
        using var reader = new Mp3FileReader(audio);
        await PlayAsync(reader, outputDeviceId, cancellationToken);
    }

    private async Task SpeakWithGoogleAsync(
        string text,
        LanguageOption language,
        string? outputDeviceId,
        CancellationToken cancellationToken)
    {
        foreach (var chunk in SplitText(text, 180))
        {
            var uri = new Uri(
                "https://translate.google.com/translate_tts" +
                $"?ie=UTF-8&client=tw-ob&tl={Uri.EscapeDataString(language.Culture)}" +
                $"&q={Uri.EscapeDataString(chunk)}");
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await _httpClient.GetAsync(uri, requestTimeout.Token);
            response.EnsureSuccessStatusCode();
            var audio = await response.Content.ReadAsByteArrayAsync(requestTimeout.Token);
            if (audio.Length < 256)
            {
                throw new InvalidDataException("在线语音服务返回的数据不完整。");
            }

            using var stream = new MemoryStream(audio, writable: false);
            using var reader = new Mp3FileReader(stream);
            await PlayAsync(reader, outputDeviceId, cancellationToken);
        }
    }

    private async Task SpeakWithWindowsAsync(
        string text,
        LanguageOption language,
        string? outputDeviceId,
        CancellationToken cancellationToken)
    {
        using var waveStream = new MemoryStream();
        using (var synthesizer = new SpeechSynthesizer())
        {
            lock (_playbackSync)
            {
                _activeSynthesizer = synthesizer;
            }

            try
            {
                var voice = synthesizer.GetInstalledVoices()
                    .FirstOrDefault(candidate => candidate.Enabled && MatchesLanguage(candidate.VoiceInfo.Culture, language));
                if (voice is null)
                {
                    throw new InvalidOperationException(
                        $"找不到 {language.DisplayName} 的 Windows 语音。请联网重试，或在 Windows 语言设置中安装对应语音包。");
                }

                synthesizer.SelectVoice(voice.VoiceInfo.Name);
                synthesizer.SetOutputToWaveStream(waveStream);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<SpeakCompletedEventArgs>? onCompleted = null;
                onCompleted = (_, eventArgs) =>
                {
                    if (eventArgs.Error is not null)
                    {
                        completion.TrySetException(eventArgs.Error);
                    }
                    else if (eventArgs.Cancelled)
                    {
                        completion.TrySetCanceled(cancellationToken);
                    }
                    else
                    {
                        completion.TrySetResult();
                    }
                };
                synthesizer.SpeakCompleted += onCompleted;
                using var registration = cancellationToken.Register(synthesizer.SpeakAsyncCancelAll);
                try
                {
                    synthesizer.SpeakAsync(text);
                    await completion.Task.WaitAsync(cancellationToken);
                }
                finally
                {
                    synthesizer.SpeakCompleted -= onCompleted;
                    synthesizer.SpeakAsyncCancelAll();
                    synthesizer.SetOutputToNull();
                }
            }
            finally
            {
                lock (_playbackSync)
                {
                    if (ReferenceEquals(_activeSynthesizer, synthesizer))
                    {
                        _activeSynthesizer = null;
                    }
                }
            }
        }

        waveStream.Position = 0;
        using var reader = new WaveFileReader(waveStream);
        await PlayAsync(reader, outputDeviceId, cancellationToken);
    }

    private async Task PlayEncodedAudioAsync(
        byte[] audio,
        string? outputDeviceId,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(audio, writable: false);
        if (audio.Length >= 12
            && audio.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && audio.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            using var reader = new WaveFileReader(stream);
            await PlayAsync(reader, outputDeviceId, cancellationToken);
            return;
        }

        using var mp3Reader = new Mp3FileReader(stream);
        await PlayAsync(mp3Reader, outputDeviceId, cancellationToken);
    }
    private async Task PlayFloatAudioAsync(
        float[] samples,
        int sampleRate,
        string? outputDeviceId,
        CancellationToken cancellationToken)
    {
        if (samples.Length == 0 || sampleRate is < 8_000 or > 192_000)
        {
            throw new InvalidDataException("Kokoro 生成的 PCM 音频格式无效。");
        }

        var bytes = new byte[checked(samples.Length * sizeof(float))];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        using var stream = new MemoryStream(bytes, writable: false);
        using var provider = new RawSourceWaveStream(
            stream,
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels: 1));
        await PlayAsync(provider, outputDeviceId, cancellationToken);
    }

    private async Task PlayAsync(
        IWaveProvider provider,
        string? outputDeviceId,
        CancellationToken cancellationToken)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = ResolveOutputDevice(enumerator, outputDeviceId);
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 100);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, eventArgs) =>
        {
            if (eventArgs.Exception is not null)
            {
                completion.TrySetException(eventArgs.Exception);
            }
            else
            {
                completion.TrySetResult();
            }
        };
        lock (_playbackSync)
        {
            _activeOutput = output;
        }

        try
        {
            using var registration = cancellationToken.Register(output.Stop);
            output.Init(provider);
            output.Play();
            await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            lock (_playbackSync)
            {
                if (ReferenceEquals(_activeOutput, output))
                {
                    _activeOutput = null;
                }
            }
        }
    }

    private static MMDevice ResolveOutputDevice(MMDeviceEnumerator enumerator, string? deviceId)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try
            {
                return enumerator.GetDevice(deviceId);
            }
            catch (ArgumentException)
            {
                // The selected virtual cable may have been removed; use the current default.
            }
        }

        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private static bool MatchesLanguage(CultureInfo culture, LanguageOption language) =>
        culture.TwoLetterISOLanguageName.Equals(language.Code, StringComparison.OrdinalIgnoreCase);

    private static bool IsRecoverableOnlineFailure(Exception exception) =>
        exception is EdgeTTSException
            or System.Net.WebSockets.WebSocketException
            or HttpRequestException
            or OperationCanceledException
            or InvalidDataException
            or NotSupportedException
            or FormatException;

    private static bool IsRecoverableRemoteFailure(Exception exception) =>
        exception is HttpRequestException
            or OperationCanceledException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or FormatException;

    private static string GetEdgeVoice(LanguageOption language) => language.Code switch
    {
        "zh" => "zh-CN-XiaoxiaoNeural",
        "ja" => "ja-JP-NanamiNeural",
        "ko" => "ko-KR-SunHiNeural",
        "es" => "es-ES-ElviraNeural",
        "fr" => "fr-FR-DeniseNeural",
        "de" => "de-DE-KatjaNeural",
        "it" => "it-IT-ElsaNeural",
        "pt" => "pt-BR-FranciscaNeural",
        "ru" => "ru-RU-SvetlanaNeural",
        "ar" => "ar-SA-ZariyahNeural",
        "hi" => "hi-IN-SwaraNeural",
        "th" => "th-TH-PremwadeeNeural",
        "vi" => "vi-VN-HoaiMyNeural",
        "id" => "id-ID-GadisNeural",
        "tr" => "tr-TR-EmelNeural",
        "pl" => "pl-PL-ZofiaNeural",
        "nl" => "nl-NL-ColetteNeural",
        "uk" => "uk-UA-PolinaNeural",
        "en" => "en-US-JennyNeural",
        _ => throw new NotSupportedException($"Edge TTS 不支持语言代码 {language.Code}。")
    };

    public static IReadOnlyList<string> SplitText(string text, int maxLength)
    {
        var chunks = new List<string>();
        var remaining = text.Trim();
        while (remaining.Length > maxLength)
        {
            var splitAt = remaining.LastIndexOfAny(['。', '！', '？', '.', '!', '?', ',', '，', ' '], maxLength - 1);
            if (splitAt < maxLength / 2)
            {
                splitAt = maxLength;
            }
            else
            {
                splitAt++;
            }

            chunks.Add(remaining[..splitAt].Trim());
            remaining = remaining[splitAt..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            chunks.Add(remaining);
        }

        return chunks;
    }
}
