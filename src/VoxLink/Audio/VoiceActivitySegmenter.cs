namespace VoxLink.Audio;

public sealed class VoiceActivitySegmenter
{
    private const int SampleRate = PcmAudioConverter.TargetSampleRate;
    private const int PreRollMs = 200;
    private const int MinimumSpeechMs = 250;
    private const int MaximumUtteranceMs = 14_000;

    private readonly object _sync = new();
    private readonly Queue<float> _preRoll = new();
    private readonly List<float> _utterance = [];
    private readonly double _threshold;
    private readonly int _silenceSamplesRequired;
    private int _silentSamples;
    private int _activeSamples;
    private bool _isSpeaking;

    public VoiceActivitySegmenter(
        double threshold,
        int silenceDurationMs,
        bool smartSentenceSegmentation = true)
    {
        _threshold = Math.Clamp(threshold, 0.001, 0.5);
        var clampedSilence = Math.Clamp(silenceDurationMs, 200, 2_500);
        if (smartSentenceSegmentation)
        {
            clampedSilence = Math.Min(clampedSilence, 900);
        }

        _silenceSamplesRequired = SampleRate * clampedSilence / 1_000;
    }

    public event EventHandler<AudioUtterance>? UtteranceReady;

    public bool IsSpeaking
    {
        get
        {
            lock (_sync)
            {
                return _isSpeaking;
            }
        }
    }

    public void AddSamples(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        AudioUtterance? completed = null;
        lock (_sync)
        {
            var active = PcmAudioConverter.RootMeanSquare(samples) >= _threshold;
            if (!_isSpeaking)
            {
                if (!active)
                {
                    AddPreRoll(samples);
                    return;
                }

                _isSpeaking = true;
                _utterance.AddRange(_preRoll);
                _preRoll.Clear();
            }

            _utterance.AddRange(samples);
            if (active)
            {
                _activeSamples += samples.Length;
                _silentSamples = 0;
            }
            else
            {
                _silentSamples += samples.Length;
            }

            var reachedSilence = _silentSamples >= _silenceSamplesRequired;
            var reachedLimit = _utterance.Count >= SampleRate * MaximumUtteranceMs / 1_000;
            if (reachedSilence || reachedLimit)
            {
                completed = CompleteLocked();
            }
        }

        if (completed is not null)
        {
            UtteranceReady?.Invoke(this, completed);
        }
    }

    public void Flush()
    {
        AudioUtterance? completed;
        lock (_sync)
        {
            completed = CompleteLocked();
        }

        if (completed is not null)
        {
            UtteranceReady?.Invoke(this, completed);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _preRoll.Clear();
            _utterance.Clear();
            _silentSamples = 0;
            _activeSamples = 0;
            _isSpeaking = false;
        }
    }

    private void AddPreRoll(ReadOnlySpan<float> samples)
    {
        var capacity = SampleRate * PreRollMs / 1_000;
        foreach (var sample in samples)
        {
            _preRoll.Enqueue(sample);
            while (_preRoll.Count > capacity)
            {
                _preRoll.Dequeue();
            }
        }
    }

    private AudioUtterance? CompleteLocked()
    {
        if (!_isSpeaking)
        {
            return null;
        }

        var minimumSamples = SampleRate * MinimumSpeechMs / 1_000;
        var samples = _activeSamples >= minimumSamples ? _utterance.ToArray() : null;
        _utterance.Clear();
        _silentSamples = 0;
        _activeSamples = 0;
        _isSpeaking = false;

        return samples is null ? null : AudioUtterance.FromSamples(samples, SampleRate);
    }
}
