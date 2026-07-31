using NAudio.Wave;

namespace VoxLink.Audio;

public static class PcmAudioConverter
{
    public const int TargetSampleRate = 16_000;

    public static float[] ConvertToMono16Khz(byte[] buffer, int byteCount, WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(format);

        var effectiveFormat = format is WaveFormatExtensible extensible
            ? extensible.ToStandardWaveFormat()
            : format;
        var bytesPerSample = effectiveFormat.BitsPerSample / 8;
        if (bytesPerSample <= 0 || effectiveFormat.Channels <= 0 || byteCount < bytesPerSample * effectiveFormat.Channels)
        {
            return [];
        }

        var frameSize = bytesPerSample * effectiveFormat.Channels;
        var frameCount = byteCount / frameSize;
        var mono = new float[frameCount];
        var isFloat = effectiveFormat.Encoding == WaveFormatEncoding.IeeeFloat;

        for (var frame = 0; frame < frameCount; frame++)
        {
            double sum = 0;
            for (var channel = 0; channel < effectiveFormat.Channels; channel++)
            {
                var offset = (frame * frameSize) + (channel * bytesPerSample);
                sum += ReadSample(buffer, offset, effectiveFormat.BitsPerSample, isFloat);
            }

            mono[frame] = (float)Math.Clamp(sum / effectiveFormat.Channels, -1, 1);
        }

        if (effectiveFormat.SampleRate == TargetSampleRate)
        {
            return mono;
        }

        var outputLength = Math.Max(1, (int)Math.Round(
            mono.Length * (double)TargetSampleRate / effectiveFormat.SampleRate));
        var output = new float[outputLength];
        var sourceStep = (double)effectiveFormat.SampleRate / TargetSampleRate;

        for (var index = 0; index < output.Length; index++)
        {
            var sourcePosition = index * sourceStep;
            var lower = Math.Min((int)sourcePosition, mono.Length - 1);
            var upper = Math.Min(lower + 1, mono.Length - 1);
            var fraction = sourcePosition - lower;
            output[index] = (float)(mono[lower] + ((mono[upper] - mono[lower]) * fraction));
        }

        return output;
    }

    public static double RootMeanSquare(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    private static float ReadSample(byte[] buffer, int offset, int bitsPerSample, bool isFloat)
    {
        if (isFloat && bitsPerSample == 32)
        {
            return BitConverter.ToSingle(buffer, offset);
        }

        return bitsPerSample switch
        {
            8 => (buffer[offset] - 128) / 128f,
            16 => BitConverter.ToInt16(buffer, offset) / 32768f,
            24 => ReadInt24(buffer, offset) / 8_388_608f,
            32 => BitConverter.ToInt32(buffer, offset) / 2_147_483_648f,
            _ => 0
        };
    }

    private static int ReadInt24(byte[] buffer, int offset)
    {
        var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
        return (value & 0x0080_0000) != 0 ? value | unchecked((int)0xFF00_0000) : value;
    }
}
