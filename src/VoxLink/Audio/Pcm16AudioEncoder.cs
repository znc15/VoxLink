using System.Buffers.Binary;

namespace VoxLink.Audio;

internal static class Pcm16AudioEncoder
{
    public static byte[] EncodePcm16(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[checked(samples.Length * sizeof(short))];
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = Math.Clamp(samples[index], -1f, 1f);
            var value = sample <= -1f
                ? short.MinValue
                : (short)Math.Round(sample * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * sizeof(short)), value);
        }

        return bytes;
    }

    public static byte[] EncodeWave(AudioUtterance utterance)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        var pcm = EncodePcm16(utterance.Samples);
        var output = new byte[checked(44 + pcm.Length)];
        var span = output.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + pcm.Length);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], utterance.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], utterance.SampleRate * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], pcm.Length);
        pcm.CopyTo(span[44..]);
        return output;
    }
}
