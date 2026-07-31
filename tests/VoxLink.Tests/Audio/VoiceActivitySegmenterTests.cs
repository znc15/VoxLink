using VoxLink.Audio;

namespace VoxLink.Tests.Audio;

public sealed class VoiceActivitySegmenterTests
{
    [Fact]
    public void AddSamples_EmitsSpeechAfterRequiredSilence()
    {
        var segmenter = new VoiceActivitySegmenter(0.02, 650);
        AudioUtterance? captured = null;
        segmenter.UtteranceReady += (_, utterance) => captured = utterance;

        segmenter.AddSamples(new float[1_600]);
        segmenter.AddSamples(Enumerable.Repeat(0.2f, 5_120).ToArray());
        segmenter.AddSamples(new float[10_400]);

        Assert.NotNull(captured);
        Assert.Equal(17_120, captured.Samples.Length);
        Assert.Equal(16_000, captured.SampleRate);
    }

    [Fact]
    public void AddSamples_DiscardsShortNoiseBurst()
    {
        var segmenter = new VoiceActivitySegmenter(0.02, 650);
        var emitted = false;
        segmenter.UtteranceReady += (_, _) => emitted = true;

        segmenter.AddSamples(Enumerable.Repeat(0.2f, 1_600).ToArray());
        segmenter.AddSamples(new float[10_400]);

        Assert.False(emitted);
    }

    [Fact]
    public void Reset_DiscardsPendingUtterance()
    {
        var segmenter = new VoiceActivitySegmenter(0.02, 650);
        var emitted = false;
        segmenter.UtteranceReady += (_, _) => emitted = true;

        segmenter.AddSamples(Enumerable.Repeat(0.2f, 5_120).ToArray());
        segmenter.Reset();
        segmenter.Flush();

        Assert.False(emitted);
    }
}
