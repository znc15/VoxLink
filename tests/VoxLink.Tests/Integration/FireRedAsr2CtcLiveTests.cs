using NAudio.Wave;
using VoxLink.Audio;
using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Tests.Integration;

/// <summary>
/// FireRedASR2-CTC 真实原生解码验证：加载默认模型目录中已安装的
/// fire-red-asr2-ctc（model.int8.onnx/tokens.txt）并解码官方测试音频。
/// 仅当模型已安装时执行（VOXLINK_RUN_LIVE_TESTS=1），不依赖网络。
/// </summary>
public sealed class FireRedAsr2CtcLiveTests
{
    private static bool LiveTestsEnabled() => string.Equals(
        Environment.GetEnvironmentVariable("VOXLINK_RUN_LIVE_TESTS"),
        "1",
        StringComparison.Ordinal);

    [Fact]
    [Trait("Category", "Live")]
    public async Task NativeRecognizer_DecodesOfficialChineseAndDialectSamples()
    {
        if (!LiveTestsEnabled())
        {
            return;
        }

        var manager = new LocalModelManager(
            LocalModelManager.DefaultRootDirectory());
        if (manager.GetStatus(LocalModelIds.FireRedAsr2Ctc) != LocalModelInstallState.Installed)
        {
            return; // 模型未安装：跳过（报告会记录该分支需要先安装）
        }

        await using var recognizer = new LocalFireRedAsr2CtcRecognizer(manager);
        await recognizer.PrepareAsync(CancellationToken.None);

        var modelDir = Path.Combine(
            LocalModelManager.DefaultRootDirectory(),
            "fire-red-asr2-ctc");

        // 0.wav：普通话（中英混合）；4-tianjin.wav：天津方言。
        var mandarin = await TranscribeFileAsync(recognizer,
            Path.Combine(modelDir, "test_wavs", "0.wav"));
        Assert.False(string.IsNullOrWhiteSpace(mandarin));
        Assert.Contains("星期", mandarin, StringComparison.Ordinal);

        var tianjin = await TranscribeFileAsync(recognizer,
            Path.Combine(modelDir, "test_wavs", "4-tianjin.wav"));
        Assert.False(string.IsNullOrWhiteSpace(tianjin));
        Assert.Contains("法律", tianjin, StringComparison.Ordinal);
    }

    private static async Task<string> TranscribeFileAsync(
        IAsrRecognizer recognizer,
        string wavPath)
    {
        var samples = ReadWav16kMono(wavPath);
        var result = await recognizer.TranscribeAsync(
            AudioUtterance.FromSamples(samples, 16_000),
            LanguageCatalog.Get("zh"),
            CancellationToken.None);
        return result.Text ?? string.Empty;
    }

    private static float[] ReadWav16kMono(string path)
    {
        using var reader = new WaveFileReader(path);
        var bytes = new byte[reader.Length];
        var read = reader.Read(bytes, 0, bytes.Length);
        return PcmAudioConverter.ConvertToMono16Khz(bytes, read, reader.WaveFormat);
    }
}
