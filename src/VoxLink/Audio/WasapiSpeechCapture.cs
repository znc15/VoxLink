using System.Diagnostics;
using System.Runtime.ExceptionServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoxLink.Audio;

public sealed class WasapiSpeechCapture : IAsyncDisposable
{
    private readonly string _deviceId;
    private readonly bool _loopback;
    private readonly Func<bool>? _shouldSuppress;
    private readonly VoiceActivitySegmenter _segmenter;
    private readonly IVoicePreprocessor? _voicePreprocessor;
    private readonly int _silenceDurationMs;
    private readonly object _sync = new();
    private IWaveIn? _capture;
    private Timer? _gapTimer;
    private long _lastPacketTimestamp;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private bool _acceptData;
    private bool _starting;
    private bool _disposed;

    public WasapiSpeechCapture(
        string deviceId,
        bool loopback,
        double threshold,
        int silenceDurationMs,
        Func<bool>? shouldSuppress = null,
        bool smartSentenceSegmentation = true,
        IVoicePreprocessor? voicePreprocessor = null)
    {
        _deviceId = deviceId;
        _loopback = loopback;
        _shouldSuppress = shouldSuppress;
        _voicePreprocessor = voicePreprocessor;
        _silenceDurationMs = Math.Clamp(silenceDurationMs, 200, 2_500);
        _segmenter = new VoiceActivitySegmenter(
            threshold,
            _silenceDurationMs,
            smartSentenceSegmentation);
        _segmenter.UtteranceReady += (_, utterance) => UtteranceReady?.Invoke(this, utterance);
    }

    public event EventHandler<float[]>? PcmChunkReady;

    public event EventHandler<AudioUtterance>? UtteranceReady;
    public bool IsSpeaking => _segmenter.IsSpeaking;

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_capture is not null || _starting)
            {
                return;
            }

            _starting = true;
        }

        var forceDefaultEndpoint = false;
        try
        {
            while (true)
            {
                CaptureResources? failedResources = null;
                Exception? failure = null;
                var usedDefaultFallback = false;

                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    try
                    {
                        usedDefaultFallback = StartCaptureLocked(forceDefaultEndpoint);
                    }
                    catch (Exception exception)
                    {
                        _acceptData = false;
                        failedResources = DetachResourcesLocked();
                        failure = exception;
                    }
                }

                // 设备失效时必须先完整释放旧 AudioClient，再重新枚举当前默认端点。
                failedResources?.Dispose();
                if (failure is null)
                {
                    if (usedDefaultFallback)
                    {
                        DeviceFallbackOccurred?.Invoke(this, _deviceId);
                    }

                    return;
                }

                if (!forceDefaultEndpoint && IsDeviceInvalidated(failure))
                {
                    forceDefaultEndpoint = true;
                    continue;
                }

                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        finally
        {
            lock (_sync)
            {
                _starting = false;
            }
        }
    }

    private bool StartCaptureLocked(bool forceDefaultEndpoint)
    {
        _enumerator = new MMDeviceEnumerator();
        var flow = _loopback ? DataFlow.Render : DataFlow.Capture;
        _device = ResolveDevice(_enumerator, flow, _deviceId, forceDefaultEndpoint, out var usedDefaultFallback);
        if (!_loopback && IsLoopbackLikeDeviceName(_device.FriendlyName))
        {
            LoopbackLikeMicWarning?.Invoke(this, _device.FriendlyName);
        }

        _capture = _loopback
            ? new WasapiLoopbackCapture(_device)
            : new NAudio.CoreAudioApi.WasapiCapture(_device);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        Interlocked.Exchange(ref _lastPacketTimestamp, Stopwatch.GetTimestamp());
        _acceptData = true;
        _capture.StartRecording();
        _gapTimer = new Timer(CheckForPacketGap, null, 100, 100);
        return usedDefaultFallback;
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _acceptData = false;
            _gapTimer?.Dispose();
            _gapTimer = null;
            _capture?.StopRecording();
            _segmenter.Flush();
        }
    }

    public ValueTask DisposeAsync()
    {
        CaptureResources? resources;
        lock (_sync)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _acceptData = false;
            resources = DetachResourcesLocked();
            _segmenter.Reset();
        }

        resources.Dispose();
        _voicePreprocessor?.Dispose();
        return ValueTask.CompletedTask;
    }

    private CaptureResources DetachResourcesLocked()
    {
        var timer = _gapTimer;
        _gapTimer = null;
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
        }

        var device = _device;
        _device = null;
        var enumerator = _enumerator;
        _enumerator = null;
        return new CaptureResources(timer, capture, device, enumerator);
    }

    private static MMDevice ResolveDevice(
        MMDeviceEnumerator enumerator,
        DataFlow flow,
        string id,
        bool forceDefaultEndpoint,
        out bool usedDefaultFallback)
    {
        usedDefaultFallback = forceDefaultEndpoint && !string.IsNullOrWhiteSpace(id);
        if (!forceDefaultEndpoint && !string.IsNullOrWhiteSpace(id))
        {
            try
            {
                return enumerator.GetDevice(id);
            }
            catch (ArgumentException)
            {
                usedDefaultFallback = true;
            }
        }

        return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
    }



    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        WaveFormat waveFormat;
        lock (_sync)
        {
            if (_disposed || !_acceptData || _capture is null)
            {
                return;
            }

            waveFormat = _capture.WaveFormat;
        }

        Interlocked.Exchange(ref _lastPacketTimestamp, Stopwatch.GetTimestamp());
        var shouldSuppress = _shouldSuppress?.Invoke() ?? false;
        var samples = shouldSuppress
            ? null
            : PcmAudioConverter.ConvertToMono16Khz(
                eventArgs.Buffer,
                eventArgs.BytesRecorded,
                waveFormat);

        // 仅对麦克风采集启用语音增强（WebRTC / RNNoise），提升 VAD 与 ASR 的输入质量；
        // 系统回环保留原始音频，避免改变游戏/媒体听感。
        if (samples is { Length: > 0 } && _voicePreprocessor is not null)
        {
            _voicePreprocessor.ProcessInPlace(samples);
        }

        var emitChunk = false;
        lock (_sync)
        {
            if (_disposed || !_acceptData)
            {
                return;
            }

            if (samples is null)
            {
                _segmenter.Reset();
            }
            else
            {
                emitChunk = true;
            }
        }

        if (!emitChunk || samples is null)
        {
            return;
        }

        PcmChunkReady?.Invoke(this, samples);
        lock (_sync)
        {
            if (!_disposed && _acceptData)
            {
                _segmenter.AddSamples(samples);
            }
        }
    }

    private void CheckForPacketGap(object? state)
    {
        lock (_sync)
        {
            if (_disposed || !_acceptData || !_segmenter.IsSpeaking)
            {
                return;
            }

            var lastPacket = Interlocked.Read(ref _lastPacketTimestamp);
            if (Stopwatch.GetElapsedTime(lastPacket) >= TimeSpan.FromMilliseconds(_silenceDurationMs))
            {
                _segmenter.Flush();
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        lock (_sync)
        {
            if (!_disposed && eventArgs.Exception is not null)
            {
                CaptureFailed?.Invoke(this, eventArgs.Exception);
            }
        }
    }

    public event EventHandler<Exception>? CaptureFailed;

    /// <summary>麦克风设备可能是系统音频回环设备（如立体声混音）时触发，参数为设备名称。</summary>
    public event EventHandler<string>? LoopbackLikeMicWarning;

    /// <summary>请求的设备不存在或初始化时失效，成功回退到 Windows 默认设备后触发。</summary>
    public event EventHandler<string>? DeviceFallbackOccurred;

    /// <summary>检查异常链中是否包含 WASAPI 设备已失效错误。</summary>
    internal static bool IsDeviceInvalidated(Exception exception)
    {
        const int deviceInvalidatedHResult = unchecked((int)0x88890004);
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.HResult == deviceInvalidatedHResult)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 判断设备名是否可能是系统音频回环设备（立体声混音等）。这类设备作为麦克风时，
    /// 会把系统播放的他人语音当作“我的语音”送入出站链路，进而被发送到 VRChat Chatbox。
    /// </summary>
    internal static bool IsLoopbackLikeDeviceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = name.ToLowerInvariant();
        return normalized.Contains("stereo mix", StringComparison.Ordinal)
            || normalized.Contains("立体声混音", StringComparison.Ordinal)
            || normalized.Contains("what u hear", StringComparison.Ordinal)
            || normalized.Contains("wave out mix", StringComparison.Ordinal)
            || normalized.Contains("loopback", StringComparison.Ordinal)
            || normalized.Contains("回环", StringComparison.Ordinal)
            || normalized.Contains("monitor of", StringComparison.Ordinal);
    }



    private sealed class CaptureResources(
        Timer? timer,
        IWaveIn? capture,
        MMDevice? device,
        MMDeviceEnumerator? enumerator) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                timer?.Dispose();
            }
            finally
            {
                try
                {
                    capture?.Dispose();
                }
                finally
                {
                    try
                    {
                        device?.Dispose();
                    }
                    finally
                    {
                        enumerator?.Dispose();
                    }
                }
            }
        }
    }
}
