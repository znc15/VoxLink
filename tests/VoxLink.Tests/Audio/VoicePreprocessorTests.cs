using VoxLink.Audio;
using VoxLink.Models;

namespace VoxLink.Tests.Audio;

/// <summary>WebRTC APM 与 RNNoise 真实引擎的集成测试（包含原生库）。</summary>
public sealed class VoicePreprocessorTests
{
    [Fact]
    public void WebRtc_ProcessesContinuousFrames_AndKeepsSignalFinite()
    {
        using var processor = new WebRtcVoicePreprocessor();
        var samples = SineWave(amplitude: 0.05f, sampleCount: 16_000);
        var original = (float[])samples.Clone();

        processor.ProcessInPlace(samples);

        Assert.NotEqual(original, samples);
        Assert.All(samples, sample => Assert.True(float.IsFinite(sample)));
        var rms = PcmAudioConverter.RootMeanSquare(samples);
        Assert.True(double.IsFinite(rms));
    }

    [Fact]
    public void RnnNoise_ProcessesArbitraryChunks_AndKeepsSignalFinite()
    {
        using var processor = new RnnNoiseVoicePreprocessor();
        var samples = SineWave(amplitude: 0.05f, sampleCount: 16_000);
        var original = (float[])samples.Clone();

        processor.ProcessInPlace(samples);

        Assert.NotEqual(original, samples);
        Assert.All(samples, sample => Assert.True(float.IsFinite(sample)));
        var rms = PcmAudioConverter.RootMeanSquare(samples);
        Assert.True(double.IsFinite(rms));
    }

    [Fact]
    public void Factory_OffReturnsNull_AndEnginesReturnProcessors()
    {
        Assert.Null(VoicePreprocessorFactory.Create(VoicePreprocessingEngine.Off));
        using var webRtc = VoicePreprocessorFactory.Create(VoicePreprocessingEngine.WebRtc);
        using var rnnoise = VoicePreprocessorFactory.Create(VoicePreprocessingEngine.RNNoise);
        Assert.NotNull(webRtc);
        Assert.NotNull(rnnoise);
    }

    private static float[] SineWave(float amplitude, int sampleCount)
    {
        var samples = new float[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            samples[index] = amplitude * MathF.Sin(2.0f * MathF.PI * 440.0f * index / 16_000.0f);
        }

        return samples;
    }
}
