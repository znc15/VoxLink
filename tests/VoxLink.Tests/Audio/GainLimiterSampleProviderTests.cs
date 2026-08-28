using NAudio.Wave;
using VoxLink.Audio;

namespace VoxLink.Tests.Audio;

/// <summary>TTS 输出增益 + tanh 软限幅限幅器单元测试。</summary>
public sealed class GainLimiterSampleProviderTests
{
    [Fact]
    public void GainAtOne_IsTransparent()
    {
        var source = new FloatSampleProvider([0.25f, -0.5f, 0.75f, -1.0f]);
        var limiter = new GainLimiterSampleProvider(source, gain: 1.0);

        var output = new float[4];
        var read = limiter.Read(output, 0, output.Length);

        Assert.Equal(4, read);
        Assert.Equal(0.25f, output[0], 5);
        Assert.Equal(-0.5f, output[1], 5);
        Assert.Equal(0.75f, output[2], 5);
        Assert.Equal(-1.0f, output[3], 5);
    }

    [Fact]
    public void GainAtHalf_ScalesDownPurelyLinearly()
    {
        var source = new FloatSampleProvider([0.5f, 1.0f]);
        var limiter = new GainLimiterSampleProvider(source, gain: 0.5);

        var output = new float[2];
        var read = limiter.Read(output, 0, output.Length);

        Assert.Equal(2, read);
        // 增益 <= 1.0 为纯线性：0.5*0.5=0.25，1.0*0.5=0.5（不施加 tanh）。
        Assert.Equal(0.25f, output[0], 5);
        Assert.Equal(0.5f, output[1], 5);
    }

    [Fact]
    public void GainAtTwo_SoftLimitsWithoutClippingBeyondUnity()
    {
        var source = new FloatSampleProvider([0.6f, 1.0f]);
        var limiter = new GainLimiterSampleProvider(source, gain: 2.0);

        var output = new float[2];
        var read = limiter.Read(output, 0, output.Length);

        Assert.Equal(2, read);
        // tanh 软限幅：输出永远不超过 ±1.0，无硬削波。
        Assert.All(output, sample => Assert.InRange(sample, -1.0f, 1.0f));
        // 增益 > 1.0：tanh(0.6*2.0)=tanh(1.2)≈0.8337，tanh(1.0*2.0)=tanh(2.0)≈0.9640。
        Assert.Equal(MathF.Tanh(1.2f), output[0], 5);
        Assert.Equal(MathF.Tanh(2.0f), output[1], 5);
    }

    [Fact]
    public void Read_PreservesWaveFormatAndChannelCount()
    {
        var source = new FloatSampleProvider([0.1f, 0.2f], channels: 2);
        var limiter = new GainLimiterSampleProvider(source, gain: 1.0);

        Assert.Equal(2, limiter.WaveFormat.Channels);
        Assert.Equal(32, limiter.WaveFormat.BitsPerSample);
        Assert.Equal(WaveFormatEncoding.IeeeFloat, limiter.WaveFormat.Encoding);
    }

    private sealed class FloatSampleProvider(float[] samples, int channels = 1) : ISampleProvider
    {
        private readonly float[] _samples = samples;
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(16_000, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            var toCopy = Math.Min(count, _samples.Length - _position);
            Array.Copy(_samples, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }
    }
}
