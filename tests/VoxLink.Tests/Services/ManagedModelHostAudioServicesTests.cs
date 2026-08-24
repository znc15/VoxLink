using VoxLink.Audio;
using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Tests.Services;

/// <summary>
/// T5 托管音频服务（MOSS ASR + dots.tts/Qwen3-TTS 合成）的确定性协议测试：
/// 使用 PowerShell fixture 宿主验证 TranscribeAsync/SynthesizeAsync 的参数、
/// WAV 文件边界与错误映射。不加载真实模型，不联网。
/// </summary>
public sealed class ManagedModelHostAudioServicesTests
{
    private static bool LiveTestsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("VOXLINK_RUN_LIVE_TESTS"),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// 真实 WSL2+CUDA 推理闭环（仅 VOXLINK_RUN_LIVE_TESTS=1 时执行）：准备 dots.tts 运行时、
    /// 启动托管宿主并合成一段真实语音。需要 NVIDIA GPU、私有 WSL 发行版与模型权重。
    /// </summary>
    [Fact]
    public async Task Live_RealDotsTtsSynthesis_WritesAudio()
    {
        if (!LiveTestsEnabled)
        {
            return;
        }

        var modelManager = new LocalModelManager();
        var runtimeManager = new ManagedModelRuntimeManager();
        await using var orchestrator = new LocalModelOrchestrator(
            modelManager,
            runtimeManager,
            ownsModelManager: true,
            ownsRuntimeManager: true);

        var probe = await orchestrator.ProbeModelRuntimeAsync(LocalModelIds.DotsTts);
        if (!probe.IsReady)
        {
            throw new InvalidOperationException(
                $"实时测试需要先准备 WSL dots.tts 运行时（当前状态：{probe.State}）。");
        }

        await using var synthesizer = new ManagedModelHostTtsSynthesizer(
            orchestrator,
            ManagedTtsModel.DotsTts);
        var (wavPath, sampleRate) = await synthesizer.SynthesizeAsync(
            "你好，这是 VoxLink 的本地语音合成测试。",
            LanguageCatalog.Get("zh"),
            referenceAudioPath: null,
            referenceText: null);

        Assert.True(File.Exists(wavPath));
        Assert.InRange(sampleRate, 8000, 192000);
        Assert.True(new FileInfo(wavPath).Length > 1000, "合成音频应为非空 WAV。");
    }

    private const string AudioFixtureScript = """
        param(
            [Parameter(Mandatory = $true)][string]$RuntimeProfile,
            [Parameter(Mandatory = $true)][string]$ModelRoot,
            [switch]$FailInfer
        )
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        [Console]::InputEncoding = [System.Text.Encoding]::UTF8
        $ErrorActionPreference = 'Stop'
        while ($true) {
            $line = [Console]::In.ReadLine()
            if ($null -eq $line) { break }
            $request = $line | ConvertFrom-Json
            $id = [int]$request.id
            $method = [string]$request.method
            if ($method -eq 'ping') {
                $result = @{ id = $id; result = @{ ready = $true; protocolVersion = 1; runtimeProfileId = $RuntimeProfile } }
            }
            elseif ($method -eq 'getCapabilities') {
                $result = @{ id = $id; result = @{ protocolVersion = 1; operations = [string[]]@('ping','getCapabilities','shutdown','load','infer','unload','cancel'); inferenceAvailable = $true } }
            }
            elseif ($method -eq 'load') {
                $result = @{ id = $id; result = @{ loaded = $true; modelId = [string]$request.params.modelId } }
            }
            elseif ($method -eq 'infer') {
                if ($FailInfer) {
                    $result = @{ id = $id; error = @{ code = 'adapter_error'; message = 'super-secret-adapter-text' } }
                }
                else {
                    $params = $request.params
                    if ($null -ne $params.audioPath -and [string]$params.audioPath -ne '') {
                        $result = @{ id = $id; result = @{ text = "transcribed $($params.audioPath) lang=$($params.language)" } }
                    }
                    else {
                        $dir = Join-Path $ModelRoot 'outputs'
                        New-Item -ItemType Directory -Force -Path $dir | Out-Null
                        $wav = Join-Path $dir ("out-" + [guid]::NewGuid().ToString('N') + '.wav')
                        $fs = [System.IO.File]::Create($wav)
                        $bw = New-Object System.IO.BinaryWriter($fs)
                        $bw.Write([System.Text.Encoding]::ASCII.GetBytes('RIFF'))
                        $bw.Write([int]36)
                        $bw.Write([System.Text.Encoding]::ASCII.GetBytes('WAVE'))
                        $bw.Write([System.Text.Encoding]::ASCII.GetBytes('fmt '))
                        $bw.Write([int]16); $bw.Write([int16]1); $bw.Write([int16]1)
                        $bw.Write([int]24000); $bw.Write([int]48000); $bw.Write([int16]2); $bw.Write([int16]16)
                        $bw.Write([System.Text.Encoding]::ASCII.GetBytes('data'))
                        $bw.Write([int]4)
                        $bw.Write([int16]0); $bw.Write([int16]0)
                        $bw.Close()
                        $rel = 'outputs/' + (Split-Path $wav -Leaf)
                        $result = @{ id = $id; result = @{ audioPath = $rel; sampleRate = 24000 } }
                    }
                }
            }
            elseif ($method -eq 'unload') {
                $result = @{ id = $id; result = @{ unloaded = $true } }
            }
            elseif ($method -eq 'shutdown') {
                [Console]::Out.WriteLine((@{ id = $id; result = @{ ok = $true } } | ConvertTo-Json -Compress -Depth 5))
                [Console]::Out.Flush()
                exit 0
            }
            else {
                $result = @{ id = $id; error = @{ code = 'method_not_found'; message = 'unknown' } }
            }
            [Console]::Out.WriteLine(($result | ConvertTo-Json -Compress -Depth 5))
            [Console]::Out.Flush()
        }
        """;

    [Fact]
    public async Task AsrRecognizer_TranscribesViaHost_AndReusesSession()
    {
        using var scenario = new AudioScenario();
        await using var recognizer = new ManagedModelHostAsrRecognizer(scenario.Orchestrator);

        var first = await recognizer.TranscribeAsync(
            AudioUtterance.FromSamples(new float[1600], 16000),
            LanguageCatalog.Get("zh"));
        var second = await recognizer.TranscribeAsync(
            AudioUtterance.FromSamples(new float[1600], 16000),
            LanguageCatalog.Get("en"));

        Assert.StartsWith("transcribed inputs/", first.Text, StringComparison.Ordinal);
        Assert.Contains("lang=zh", first.Text, StringComparison.Ordinal);
        Assert.Contains("lang=en", second.Text, StringComparison.Ordinal);
        // 同一实例只启动一次宿主（模型加载一次）。
        Assert.Equal(1, scenario.Runtime.AcquireCount);

        // 每个请求都在租约模型目录写入输入 WAV。
        var inputs = Directory.GetFiles(Path.Combine(scenario.Model.ModelDirectory!, "inputs"));
        Assert.Equal(2, inputs.Length);

        await recognizer.DisposeAsync();
        Assert.Equal(1, scenario.Model.Leases.Single().DisposeCount);
    }

    [Fact]
    public async Task AsrRecognizer_HostError_MapsToFixedSafeMessage()
    {
        using var scenario = new AudioScenario(failInfer: true);
        await using var recognizer = new ManagedModelHostAsrRecognizer(scenario.Orchestrator);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recognizer.TranscribeAsync(
                AudioUtterance.FromSamples(new float[1600], 16000),
                LanguageCatalog.Get("zh")));

        Assert.Equal("本地识别模型推理失败，请检查模型文件与运行时状态。", error.Message);
        Assert.DoesNotContain("super-secret-adapter-text", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TtsSynthesizer_ReturnsWavInsideModelRoot()
    {
        using var scenario = new AudioScenario();
        await using var synthesizer = new ManagedModelHostTtsSynthesizer(
            scenario.Orchestrator, ManagedTtsModel.Qwen3Tts);

        var (wavPath, sampleRate) = await synthesizer.SynthesizeAsync(
            "你好",
            LanguageCatalog.Get("zh"),
            referenceAudioPath: null,
            referenceText: null);

        Assert.Equal(24000, sampleRate);
        Assert.True(File.Exists(wavPath));
        var modelRoot = Path.GetFullPath(scenario.Model.ModelDirectory!) + Path.DirectorySeparatorChar;
        Assert.StartsWith(modelRoot, Path.GetFullPath(wavPath), StringComparison.Ordinal);
        Assert.Equal(1, scenario.Runtime.AcquireCount);

        await synthesizer.DisposeAsync();
        Assert.Equal(1, scenario.Model.Leases.Single().DisposeCount);
    }

    private sealed class AudioScenario : IDisposable
    {
        public AudioScenario(bool failInfer = false)
        {
            TempDir = new TempDirectory();
            var fixturePath = Path.Combine(TempDir.Root, "audio-host.ps1");
            File.WriteAllText(fixturePath, AudioFixtureScript);
            Model = new FakeModelManager { ModelDirectory = TempDir.Root };
            Runtime = new FakeRuntimeManager
            {
                LeaseFactory = (profile, directory) =>
                {
                    var arguments = new List<string>
                    {
                        "-NoProfile",
                        "-ExecutionPolicy",
                        "Bypass",
                        "-File",
                        fixturePath,
                        "-RuntimeProfile",
                        profile,
                        "-ModelRoot",
                        directory
                    };
                    if (failInfer)
                    {
                        arguments.Add("-FailInfer");
                    }

                    RuntimeLease = new FakeRuntimeLease(
                        profile,
                        new ManagedModelHostLaunch(
                            "powershell.exe",
                            arguments,
                            WorkingDirectory: directory));
                    return RuntimeLease;
                }
            };
            if (failInfer)
            {
                Runtime.FailInfer = true;
            }

            // 目录精简后 MOSS/dots.tts/Qwen3-TTS 已不在公开目录中；包装器仍引用
            // 这些保留 ID，这里注入合成目录条目以继续验证托管宿主协议面。
            Orchestrator = new LocalModelOrchestrator(
                Model, Runtime, ownsModelManager: false, ownsRuntimeManager: false,
                catalogLookup: LegacyAudioCatalog);
        }

        internal static LocalModelDefinition? LegacyAudioCatalog(string modelId)
        {
            if (modelId != LocalModelIds.MossTranscribeDiarize
                && modelId != LocalModelIds.DotsTts
                && modelId != LocalModelIds.Qwen3Tts17B)
            {
                return null;
            }

            return new LocalModelDefinition
            {
                Id = modelId,
                Name = "Fixture managed audio model",
                Category = LocalModelCategory.Asr,
                SupportLevel = LocalModelSupportLevel.Stable,
                Runtime = LocalModelRuntimeKind.ManagedWslCuda,
                InstallKind = LocalModelInstallKind.ManifestFiles,
                Parameters = "1B",
                NumericParameterBillions = 1.0,
                License = "MIT",
                Languages = "zh/en",
                Requirements = "test",
                SourceUrl = "https://huggingface.co/test/model",
                Description = "test model",
                RuntimeProfileId = ManagedRuntimeCatalog.WslMoss
            };
        }

        public TempDirectory TempDir { get; }
        public FakeModelManager Model { get; }
        public FakeRuntimeManager Runtime { get; }
        public FakeRuntimeLease? RuntimeLease { get; private set; }
        public LocalModelOrchestrator Orchestrator { get; }

        public void Dispose() => TempDir.Dispose();
    }

    private sealed class FakeModelManager : ILocalModelManager, IDisposable, IAsyncDisposable
    {
        public string? ModelDirectory { get; init; }
        public List<FakeModelLease> Leases { get; } = [];

        public event EventHandler<LocalModelProgressEventArgs>? ModelProgress
        {
            add { }
            remove { }
        }

        public IReadOnlyList<LocalModelDefinition> List() => [];
        public LocalModelInstallState GetStatus(string modelId) => LocalModelInstallState.Installed;
        public Task InstallAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<bool> RemoveAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public ILocalModelLease AcquireUsage(string modelId)
        {
            var lease = new FakeModelLease(
                modelId,
                ModelDirectory ?? Path.Combine(Path.GetTempPath(), "voxlink-t5-models", modelId));
            Leases.Add(lease);
            return lease;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeModelLease(string modelId, string modelDirectory) : ILocalModelLease
    {
        private int _disposed;

        public string ModelId { get; } = modelId;
        public string ModelDirectory { get; } = modelDirectory;
        public int DisposeCount { get; private set; }

        public string ResolvePath(string relativePath) => Path.Combine(ModelDirectory, relativePath);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                DisposeCount++;
            }
        }
    }

    private sealed class FakeRuntimeManager : IManagedModelRuntimeManager
    {
        public bool FailInfer { get; set; }
        public Func<string, string, IManagedRuntimeLease>? LeaseFactory { get; init; }
        public int AcquireCount { get; private set; }

        public event EventHandler<ManagedRuntimeProgressEventArgs>? RuntimeProgress
        {
            add { }
            remove { }
        }

        public IReadOnlyList<ManagedRuntimeDefinition> List() => ManagedRuntimeCatalog.All;
        public bool CancelPreparation(string runtimeProfileId) => false;

        public Task<ManagedRuntimeProbe> ProbeAsync(
            string runtimeProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ManagedRuntimeProbe
            {
                RuntimeProfileId = runtimeProfileId,
                Platform = ManagedRuntimePlatform.WslCuda,
                State = ManagedRuntimeState.NotPrepared,
                RequiredAction = ManagedRuntimeUserAction.None,
                Status = "未准备"
            });

        public Task<ManagedRuntimeProbe> PrepareAsync(
            string runtimeProfileId,
            CancellationToken cancellationToken = default) =>
            ProbeAsync(runtimeProfileId, cancellationToken);

        public Task<IManagedRuntimeLease> AcquireUsageAsync(
            string runtimeProfileId,
            string modelDirectory,
            CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            if (LeaseFactory is null)
            {
                throw new InvalidOperationException("未配置租约工厂。");
            }

            return Task.FromResult(LeaseFactory(runtimeProfileId, modelDirectory));
        }

        public Task<bool> RemoveAsync(
            string runtimeProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRuntimeLease(
        string runtimeProfileId,
        ManagedModelHostLaunch hostLaunch) : IManagedRuntimeLease
    {
        private int _disposed;

        public string RuntimeProfileId { get; } = runtimeProfileId;
        public ManagedRuntimePlatform Platform { get; } = ManagedRuntimePlatform.WslCuda;
        public ManagedModelHostLaunch HostLaunch { get; } = hostLaunch;
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                DisposeCount++;
            }
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "voxlink-t5-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}