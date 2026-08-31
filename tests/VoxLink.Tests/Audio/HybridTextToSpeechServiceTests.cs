using NAudio.Wave;
using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Tests.Audio;

public sealed class HybridTextToSpeechServiceTests
{
    [Fact]
    public void MaterializeWave_ReadsProviderToCompletion()
    {
        var expected = Enumerable.Range(0, 70_000)
            .Select(index => (byte)(index % 251))
            .ToArray();
        var provider = new ChunkedWaveProvider(expected, maximumChunkSize: 777);

        var (data, format) = HybridTextToSpeechService.MaterializeWave(
            provider,
            CancellationToken.None);

        Assert.Equal(expected, data);
        Assert.Equal(provider.WaveFormat, format);
        Assert.True(provider.ReadCount > 1);
    }

    [Fact]
    public void MaterializedWave_CreatesTwoIndependentCompleteReaders()
    {
        var expected = Enumerable.Range(0, 9_000)
            .Select(index => (byte)(index % 239))
            .ToArray();
        var provider = new ChunkedWaveProvider(expected, maximumChunkSize: 503);
        var (data, format) = HybridTextToSpeechService.MaterializeWave(
            provider,
            CancellationToken.None);

        using var cableReader = new RawSourceWaveStream(
            new MemoryStream(data, writable: false),
            format);
        using var monitorReader = new RawSourceWaveStream(
            new MemoryStream(data, writable: false),
            format);

        Assert.Equal(expected, ReadAll(cableReader));
        Assert.Equal(expected, ReadAll(monitorReader));
    }

    [Fact]
    public void MaterializeWave_ObservesPreCanceledTokenBeforeReading()
    {
        var provider = new ChunkedWaveProvider([1, 2, 3, 4], maximumChunkSize: 2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            HybridTextToSpeechService.MaterializeWave(provider, cancellation.Token));
        Assert.Equal(0, provider.ReadCount);
    }

    [Fact]
    public void BuildEnhancedWaveProvider_AppliesConfiguredGainAndSoftLimiter()
    {
        var samples = new[] { 0.6f, -1.0f };
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        using var source = new RawSourceWaveStream(
            new MemoryStream(bytes, writable: false),
            WaveFormat.CreateIeeeFloatWaveFormat(16_000, channels: 1));
        var enhanced = HybridTextToSpeechService.BuildEnhancedWaveProvider(
            source,
            new AppSettings { TtsOutputVolume = 2.0 });
        var outputBytes = new byte[bytes.Length];

        var read = enhanced.Read(outputBytes, 0, outputBytes.Length);
        var output = new float[samples.Length];
        Buffer.BlockCopy(outputBytes, 0, output, 0, read);

        Assert.Equal(outputBytes.Length, read);
        Assert.Equal(MathF.Tanh(1.2f), output[0], 5);
        Assert.Equal(MathF.Tanh(-2.0f), output[1], 5);
    }

    private static byte[] ReadAll(IWaveProvider provider)
    {
        using var output = new MemoryStream();
        var buffer = new byte[1_024];
        while (true)
        {
            var read = provider.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return output.ToArray();
            }

            output.Write(buffer, 0, read);
        }
    }

    private sealed class ChunkedWaveProvider(byte[] data, int maximumChunkSize) : IWaveProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = new(16_000, bits: 16, channels: 1);

        public int ReadCount { get; private set; }

        public int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            var available = data.Length - _position;
            if (available <= 0)
            {
                return 0;
            }

            var toCopy = Math.Min(Math.Min(count, maximumChunkSize), available);
            Array.Copy(data, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }
    }
}
