namespace VoxLink.Audio;

public sealed record AudioUtterance(float[] Samples, int SampleRate, TimeSpan Duration)
{
    public static AudioUtterance FromSamples(float[] samples, int sampleRate) =>
        new(samples, sampleRate, TimeSpan.FromSeconds((double)samples.Length / sampleRate));
}
