using NAudio.Wave;
using VoxLink.Audio;

namespace VoxLink.Tests.Audio;

public sealed class PcmAudioConverterTests
{
    [Fact]
    public void ConvertToMono16Khz_AveragesStereoPcm16()
    {
        var format = new WaveFormat(16_000, 16, 2);
        var samples = new short[] { 16_384, -16_384, 8_192, 8_192 };
        var buffer = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, buffer, 0, buffer.Length);

        var result = PcmAudioConverter.ConvertToMono16Khz(buffer, buffer.Length, format);

        Assert.Equal(2, result.Length);
        Assert.Equal(0, result[0], precision: 4);
        Assert.Equal(0.25, result[1], precision: 4);
    }

    [Fact]
    public void ConvertToMono16Khz_ResamplesOneSecondToTargetRate()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);
        var source = Enumerable.Repeat(0.25f, 48_000).ToArray();
        var buffer = new byte[source.Length * sizeof(float)];
        Buffer.BlockCopy(source, 0, buffer, 0, buffer.Length);

        var result = PcmAudioConverter.ConvertToMono16Khz(buffer, buffer.Length, format);

        Assert.Equal(16_000, result.Length);
        Assert.All(result, sample => Assert.Equal(0.25, sample, precision: 4));
    }

    [Fact]
    public void RootMeanSquare_ReturnsExpectedEnergy()
    {
        var result = PcmAudioConverter.RootMeanSquare([1f, -1f, 1f, -1f]);

        Assert.Equal(1, result, precision: 6);
    }
}
