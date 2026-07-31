using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoxLink.Audio;

public sealed class WasapiSpeechCapture : IAsyncDisposable
{
    private readonly string _deviceId;
    private readonly bool _loopback;
    private readonly Func<bool>? _shouldSuppress;
    private readonly VoiceActivitySegmenter _segmenter;
    private readonly int _silenceDurationMs;
    private readonly object _sync = new();
    private IWaveIn? _capture;
    private Timer? _gapTimer;
    private long _lastPacketTimestamp;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private bool _acceptData;
    private bool _disposed;

    public WasapiSpeechCapture(
        string deviceId,
        bool loopback,
        double threshold,
        int silenceDurationMs,
        Func<bool>? shouldSuppress = null,
        bool smartSentenceSegmentation = true)
    {
        _deviceId = deviceId;
        _loopback = loopback;
        _shouldSuppress = shouldSuppress;
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
        CaptureResources? failedResources = null;
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_capture is not null)
                {
                    return;
                }

                try
                {
                    _enumerator = new MMDeviceEnumerator();
                    var flow = _loopback ? DataFlow.Render : DataFlow.Capture;
                    _device = ResolveDevice(_enumerator, flow, _deviceId);
                    _capture = _loopback
                        ? new WasapiLoopbackCapture(_device)
                        : new NAudio.CoreAudioApi.WasapiCapture(_device);
                    _capture.DataAvailable += OnDataAvailable;
                    _capture.RecordingStopped += OnRecordingStopped;
                    Interlocked.Exchange(ref _lastPacketTimestamp, Stopwatch.GetTimestamp());
                    _acceptData = true;
                    _capture.StartRecording();
                    _gapTimer = new Timer(CheckForPacketGap, null, 100, 100);
                }
                catch
                {
                    _acceptData = false;
                    failedResources = DetachResourcesLocked();
                    throw;
                }
            }
        }
        finally
        {
            failedResources?.Dispose();
        }
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

    private MMDevice ResolveDevice(MMDeviceEnumerator enumerator, DataFlow flow, string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                return enumerator.GetDevice(id);
            }
            catch (ArgumentException)
            {
                DeviceFallbackOccurred?.Invoke(this, id);
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

    /// <summary>请求的设备 ID 不存在时回退到 Windows 默认设备前触发（参数为请求的设备 ID）。</summary>
    public event EventHandler<string>? DeviceFallbackOccurred;


    private sealed class CaptureResources(
        Timer? timer,
        IWaveIn? capture,
        MMDevice? device,
        MMDeviceEnumerator? enumerator) : IDisposable
    {
        public void Dispose()
        {
            timer?.Dispose();
            capture?.Dispose();
            device?.Dispose();
            enumerator?.Dispose();
        }
    }
}
