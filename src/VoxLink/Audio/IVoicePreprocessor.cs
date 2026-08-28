using RNNoise.NET;
using SoundFlow.Extensions.WebRtc.Apm;
using VoxLink.Models;

namespace VoxLink.Audio;

/// <summary>麦克风语音后处理节点：在 16 kHz 单声道 PCM 上就地处理。</summary>
public interface IVoicePreprocessor : IDisposable
{
    void ProcessInPlace(float[] samples);
}

/// <summary>按设置创建对应的语音后处理器。</summary>
public static class VoicePreprocessorFactory
{
    public static IVoicePreprocessor? Create(VoicePreprocessingEngine engine) => engine switch
    {
        VoicePreprocessingEngine.WebRtc => new WebRtcVoicePreprocessor(),
        VoicePreprocessingEngine.RNNoise => new RnnNoiseVoicePreprocessor(),
        _ => null,
    };
}

/// <summary>
/// WebRTC APM（SoundFlow.Extensions.WebRtc.Apm 封装的原生 webrtc-apm.dll）：
/// NoiseSuppression High + AGC1 AdaptiveDigital + 80Hz 高通。
/// WebRTC APM 固定 10 ms 帧（16 kHz 下 160 样本），不足一帧的数据内部缓存延迟到下一块。
/// </summary>
public sealed class WebRtcVoicePreprocessor : IVoicePreprocessor
{
    private const int SampleRate = PcmAudioConverter.TargetSampleRate;

    private readonly AudioProcessingModule _module;
    private readonly ApmConfig _config;
    private readonly StreamConfig _streamConfig;
    private readonly int _frameSize;
    private readonly float[] _frame;
    private readonly float[] _processed;
    private int _pendingCount;

    public WebRtcVoicePreprocessor()
    {
        _module = new AudioProcessingModule();
        _config = new ApmConfig();
        _config.SetEchoCanceller(enabled: false, mobileMode: false);
        _config.SetNoiseSuppression(enabled: true, level: NoiseSuppressionLevel.High);
        _config.SetGainController1(
            enabled: true,
            mode: GainControlMode.AdaptiveDigital,
            targetLevelDbfs: -20,
            compressionGainDb: 9,
            enableLimiter: true);
        _config.SetGainController2(enabled: false);
        _config.SetHighPassFilter(enabled: true);
        _config.SetPreAmplifier(enabled: false, fixedGainFactor: 1f);
        _config.SetPipeline(SampleRate, multiChannelRender: false, multiChannelCapture: false, DownmixMethod.AverageChannels);
        _module.ApplyConfig(_config);
        _module.Initialize();
        _streamConfig = new StreamConfig(SampleRate, 1);
        _frameSize = AudioProcessingModule.GetFrameSize(SampleRate);
        _frame = new float[_frameSize];
        _processed = new float[_frameSize];
    }

    public void ProcessInPlace(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0)
        {
            return;
        }

        var sourceIndex = 0;
        while (sourceIndex < samples.Length)
        {
            var needed = _frameSize - _pendingCount;
            var take = Math.Min(needed, samples.Length - sourceIndex);
            Array.Copy(samples, sourceIndex, _frame, _pendingCount, take);
            _pendingCount += take;
            sourceIndex += take;

            if (_pendingCount == _frameSize)
            {
                var inputChannels = new[] { _frame };
                var outputChannels = new[] { _processed };
                _module.ProcessStream(inputChannels, _streamConfig, _streamConfig, outputChannels);

                // 写入本块对应的输出位置（待处理帧会被延迟最多 10 ms）。
                var outputStart = sourceIndex - _frameSize;
                Array.Copy(_processed, 0, samples, outputStart, _frameSize);
                _pendingCount = 0;
            }
        }

        // 未凑满一帧的尾巴留给下一块处理，当前位置必须清零，
        // 避免把原始音频直接喂给 VAD/ASR 造成“双重音频”。
        if (sourceIndex < samples.Length)
        {
            Array.Clear(samples, sourceIndex, samples.Length - sourceIndex);
        }
    }

    public void Dispose()
    {
        _config.Dispose();
        _module.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// RNNoise（YellowDogMan.RRNoise.NET 封装的原生 rnnoise.dll）：
/// 神经网络降噪，内部按 480 样本帧处理任意长度输入。
/// </summary>
public sealed class RnnNoiseVoicePreprocessor : IVoicePreprocessor
{
    private readonly Denoiser _denoiser = new();
    private readonly RmsAgcPostFilter _agc = new();

    public void ProcessInPlace(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0)
        {
            return;
        }

        // finish:false 允许跨块处理；RNNoise.NET 会把未消费结果缓存在内部，
        // 下一次调用时先输出到当前 buffer 的头部，保持音频连续。
        // 只有返回的 processedCount 个样本是本次有效输出，尾巴必须清零。
        var processedCount = _denoiser.Denoise(samples, finish: false);
        if (processedCount < samples.Length)
        {
            Array.Clear(samples, processedCount, samples.Length - processedCount);
        }

        // RNNoise 只做降噪；这里再补一级轻量 RMS 自动增益，保证说话音量稳定。
        _agc.Apply(samples);
    }

    public void Dispose()
    {
        _denoiser.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>轻量 RMS 自动增益：向目标响度收敛，限制增益范围并逐样本插值防爆音。</summary>
internal sealed class RmsAgcPostFilter
{
    private const double TargetRms = 0.085;
    private const double MinGain = 0.25;
    private const double MaxGain = 8.0;

    private double _smoothedGain = 1.0;

    public void Apply(float[] samples)
    {
        if (samples is null || samples.Length == 0)
        {
            return;
        }

        double sumSquares = 0;
        foreach (var sample in samples)
        {
            sumSquares += sample * sample;
        }

        var rms = Math.Sqrt(sumSquares / samples.Length);
        var desiredGain = Math.Clamp(TargetRms / Math.Max(rms, 1e-6), MinGain, MaxGain);

        var startGain = _smoothedGain;
        var endGain = startGain
            + (desiredGain - startGain) * (desiredGain >= startGain ? 0.5 : 0.65);
        _smoothedGain = endGain;

        for (var index = 0; index < samples.Length; index++)
        {
            var progress = samples.Length == 1 ? 1.0 : (index + 1.0) / samples.Length;
            var gain = startGain + ((endGain - startGain) * progress);
            samples[index] = (float)Math.Clamp(samples[index] * gain, -1.0, 1.0);
        }
    }
}
