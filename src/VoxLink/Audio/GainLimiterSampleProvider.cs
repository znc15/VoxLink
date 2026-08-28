using NAudio.Wave;

namespace VoxLink.Audio;

/// <summary>
/// TTS 输出增益 + tanh 软限幅限幅器。单一 ISampleProvider，输入任意 channel/格式已归一化为 float [-1,1]。
/// 增益 &lt;= 1.0 时为纯线性（默认 100% 完全透明，不改变原始音频）；
/// 增益 &gt; 1.0 时才施加 tanh 软限幅，防止增益放大后硬削波爆音。
/// </summary>
public sealed class GainLimiterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float _gain;

    public GainLimiterSampleProvider(ISampleProvider source, double gain)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _gain = (float)gain;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (_gain <= 1.0f)
        {
            // 增益不超过 1.0：输入已在 [-1,1]，纯线性衰减即可，无需限幅。
            for (var index = 0; index < read; index++)
            {
                buffer[offset + index] *= _gain;
            }
        }
        else
        {
            // 增益放大：tanh 软限幅平滑压缩，避免超出 ±1.0 的硬削波爆音。
            for (var index = 0; index < read; index++)
            {
                buffer[offset + index] = (float)Math.Tanh(buffer[offset + index] * _gain);
            }
        }

        return read;
    }
}
