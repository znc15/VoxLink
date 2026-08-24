using System.Speech.Synthesis;
using System.Text.Json;
using NAudio.Wave;
using VoxLink.Audio;
using VoxLink.Engine;
using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Tests.Integration;

/// <summary>
/// 真实本地模型可用性验证：通过 Engine RPC 全链路（安装 → 测试 → 删除）跑真实推理。
/// 仅在 VOXLINK_RUN_LIVE_TESTS=1 时执行（默认跳过）；需要网络下载模型。
/// </summary>
public sealed class LocalModelLiveTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static bool LiveTestsEnabled() => string.Equals(
        Environment.GetEnvironmentVariable("VOXLINK_RUN_LIVE_TESTS"),
        "1",
        StringComparison.Ordinal);

    [Theory]
    [InlineData("tiny")]
    [InlineData("base")]
    [InlineData("small")]
    [Trait("Category", "Live")]
    public async Task WhisperModel_TranscribesSynthesizedEnglishSpeech(string modelName)
    {
        if (!LiveTestsEnabled())
        {
            return;
        }

        var samples = Synthesize("Welcome to the voice translation test.");
        await using var recognizer = new WhisperSpeechRecognizer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));

        var transcription = await recognizer.TranscribeAsync(
            AudioUtterance.FromSamples(samples, 16_000),
            LanguageCatalog.Get("en"),
            modelName,
            timeout.Token);

        Assert.Contains("translation", transcription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test", transcription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task MiniCpm_InstallsTestsTranslationAndRemoves()
    {
        if (!LiveTestsEnabled())
        {
            return;
        }

        await using var host = new EngineHost((_, _) => { }, startUiHost: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var parameters = JsonSerializer.SerializeToElement(new { modelId = LocalModelIds.MiniCpm51BGguf });

        var installed = await host.HandleAsync(
            "installLocalModel", parameters, SerializerOptions, timeout.Token);
        Assert.Equal(
            "installed",
            JsonSerializer.SerializeToElement(installed, SerializerOptions)
                .GetProperty("installState").GetString());

        try
        {
            var result = await host.HandleAsync(
                "testLocalModel", parameters, SerializerOptions, timeout.Token);
            var json = JsonSerializer.SerializeToElement(result, SerializerOptions);
            Assert.True(json.GetProperty("ok").GetBoolean(), "翻译测试应成功");
            var detail = json.GetProperty("detail").GetString();
            Assert.False(string.IsNullOrWhiteSpace(detail), "翻译测试应返回译文");
            Assert.NotEqual("你好，世界！", detail);
        }
        finally
        {
            await host.HandleAsync(
                "removeLocalModel", parameters, SerializerOptions, timeout.Token);
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task Kokoro_InstallsTestsSynthesisAndRemoves()
    {
        if (!LiveTestsEnabled())
        {
            return;
        }

        await using var host = new EngineHost((_, _) => { }, startUiHost: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var parameters = JsonSerializer.SerializeToElement(new { modelId = LocalModelIds.Kokoro82M });

        var installed = await host.HandleAsync(
            "installLocalModel", parameters, SerializerOptions, timeout.Token);
        Assert.Equal(
            "installed",
            JsonSerializer.SerializeToElement(installed, SerializerOptions)
                .GetProperty("installState").GetString());

        try
        {
            var result = await host.HandleAsync(
                "testLocalModel", parameters, SerializerOptions, timeout.Token);
            var json = JsonSerializer.SerializeToElement(result, SerializerOptions);
            Assert.True(json.GetProperty("ok").GetBoolean(), "语音合成测试应成功");
        }
        finally
        {
            await host.HandleAsync(
                "removeLocalModel", parameters, SerializerOptions, timeout.Token);
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task HyMt15Gguf_InstallsTestsTranslationAndRemoves()
    {
        if (!LiveTestsEnabled())
        {
            return;
        }

        // 本地混元翻译 GGUF：单文件下载 + LLamaSharp CPU 推理，
        // 与 MiniCPM 共用 LlamaCppGguf 运行类别。
        await using var host = new EngineHost((_, _) => { }, startUiHost: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(45));
        var parameters = JsonSerializer.SerializeToElement(new { modelId = LocalModelIds.HyMt15Gguf });

        var installed = await host.HandleAsync(
            "installLocalModel", parameters, SerializerOptions, timeout.Token);
        Assert.Equal(
            "installed",
            JsonSerializer.SerializeToElement(installed, SerializerOptions)
                .GetProperty("installState").GetString());

        try
        {
            var result = await host.HandleAsync(
                "testLocalModel", parameters, SerializerOptions, timeout.Token);
            var json = JsonSerializer.SerializeToElement(result, SerializerOptions);
            Assert.True(json.GetProperty("ok").GetBoolean(), "本地混元翻译测试应成功");
            var detail = json.GetProperty("detail").GetString();
            Assert.False(string.IsNullOrWhiteSpace(detail), "本地混元翻译测试应返回译文");
        }
        finally
        {
            // 调试时设置 VOXLINK_KEEP_MODELS=1 可保留模型现场。
            if (Environment.GetEnvironmentVariable("VOXLINK_KEEP_MODELS") != "1")
            {
                await host.HandleAsync(
                    "removeLocalModel", parameters, SerializerOptions, timeout.Token);
            }
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task Whisper_TestLocalModelWithoutMic_ReportsFriendlyError()
    {
        if (!LiveTestsEnabled())
        {
            return;
        }

        await using var host = new EngineHost((_, _) => { }, startUiHost: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var parameters = JsonSerializer.SerializeToElement(new { modelId = LocalModelIds.WhisperTiny });

        var installed = await host.HandleAsync(
            "installLocalModel", parameters, SerializerOptions, timeout.Token);
        Assert.Equal(
            "installed",
            JsonSerializer.SerializeToElement(installed, SerializerOptions)
                .GetProperty("installState").GetString());

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                host.HandleAsync(
                    "testLocalModel", parameters, SerializerOptions, timeout.Token));
            Assert.Contains("麦克风", exception.Message);
        }
        finally
        {
            await host.HandleAsync(
                "removeLocalModel", parameters, SerializerOptions, timeout.Token);
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task Whisper_TestLocalModelWithMic_CapturesAndReturnsResult()
    {
        if (!LiveTestsEnabled())
        {
            return;
        }

        var devices = new AudioDeviceService().GetCaptureDevices();
        if (devices.Count == 0)
        {
            return; // 无麦克风，跳过（报告会记录该分支需真机验证）
        }

        await using var host = new EngineHost((_, _) => { }, startUiHost: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var parameters = JsonSerializer.SerializeToElement(new { modelId = LocalModelIds.WhisperTiny });
        var settings = JsonSerializer.SerializeToElement(new
        {
            settings = new AppSettings
            {
                MicrophoneDeviceId = devices[0].Id,
                WhisperModel = "tiny"
            }
        }, SerializerOptions);
        await host.HandleAsync("initialize", settings, SerializerOptions, timeout.Token);

        var installed = await host.HandleAsync(
            "installLocalModel", parameters, SerializerOptions, timeout.Token);
        Assert.Equal(
            "installed",
            JsonSerializer.SerializeToElement(installed, SerializerOptions)
                .GetProperty("installState").GetString());

        try
        {
            // 环境安静时预期 ok=false（"没听清"），有人说话时 ok=true；
            // 只要不抛异常、返回结构正确即说明录音→识别链路可用。
            var result = await host.HandleAsync(
                "testLocalModel", parameters, SerializerOptions, timeout.Token);
            var json = JsonSerializer.SerializeToElement(result, SerializerOptions);
            Assert.True(json.TryGetProperty("ok", out _), "应返回 ok 字段");
            Assert.True(json.TryGetProperty("detail", out _), "应返回 detail 字段");
        }
        finally
        {
            await host.HandleAsync(
                "removeLocalModel", parameters, SerializerOptions, timeout.Token);
        }
    }

    private static float[] Synthesize(string text)
    {
        using var waveStream = new MemoryStream();
        using (var synthesizer = new SpeechSynthesizer())
        {
            synthesizer.SelectVoice("Microsoft Zira Desktop");
            synthesizer.SetOutputToWaveStream(waveStream);
            synthesizer.Speak(text);
            synthesizer.SetOutputToNull();
        }

        waveStream.Position = 0;
        using var reader = new WaveFileReader(waveStream);
        var bytes = new byte[reader.Length];
        var read = reader.Read(bytes, 0, bytes.Length);
        return PcmAudioConverter.ConvertToMono16Khz(bytes, read, reader.WaveFormat);
    }
}
