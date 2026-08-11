using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using VoxLink.Engine;
using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Tests.Engine;

public sealed class EngineHostTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void SecretRedactor_RedactsDistinctSecretsLongestFirst()
    {
        var result = SecretRedactor.Redact(
            "API-KEY-LONG header-secret and API-KEY",
            ["api-key", "api-key-long", "header-secret", "", null]);

        Assert.Equal("[redacted] [redacted] and [redacted]", result);
    }

    [Fact]
    public async Task Shutdown_SetsTerminationFlagWithoutStartingAudio()
    {
        await using var host = new EngineHost((_, _) => { }, startUiHost: false);
        using var parameters = JsonDocument.Parse("{}");

        var result = await host.HandleAsync(
            "shutdown",
            parameters.RootElement,
            SerializerOptions,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(host.ShouldShutdown);
    }

    [Fact]
    public async Task Bootstrap_ReportsEngineAssemblyVersion()
    {
        await using var host = new EngineHost((_, _) => { }, startUiHost: false);
        using var parameters = JsonDocument.Parse("{}");

        var result = await host.HandleAsync(
            "getBootstrap",
            parameters.RootElement,
            SerializerOptions,
            CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result, SerializerOptions);

        Assert.Equal(
            typeof(EngineHost).Assembly.GetName().Version?.ToString(3),
            json.GetProperty("engineVersion").GetString());
        Assert.Equal("1.3.0", json.GetProperty("engineVersion").GetString());
    }

    [Fact]
    public void MessagePayload_PreservesReferenceFeatureMetadata()
    {
        var timestamp = DateTimeOffset.Parse("2026-07-31T04:00:00+00:00");
        var message = new VoxLink.Models.ConversationMessage(
            VoxLink.Models.TranslationDirection.Inbound,
            "source",
            "primary",
            timestamp)
        {
            SecondaryTranslatedText = "secondary",
            SpeakerId = "speaker-7",
            SpeakerLabel = "说话人 speaker-7",
            UtteranceId = "utterance-42",
            IsFinal = false,
            TranscriptionOnly = true
        };

        var json = JsonSerializer.SerializeToElement(
            EngineHost.ToMessagePayload(message),
            SerializerOptions);

        Assert.Equal("inbound", json.GetProperty("direction").GetString());
        Assert.Equal("source", json.GetProperty("sourceText").GetString());
        Assert.Equal("primary", json.GetProperty("translatedText").GetString());
        Assert.Equal("secondary", json.GetProperty("secondaryTranslatedText").GetString());
        Assert.Equal("speaker-7", json.GetProperty("speakerId").GetString());
        Assert.Equal("说话人 speaker-7", json.GetProperty("speakerLabel").GetString());
        Assert.Equal("utterance-42", json.GetProperty("utteranceId").GetString());
        Assert.False(json.GetProperty("isFinal").GetBoolean());
        Assert.True(json.GetProperty("transcriptionOnly").GetBoolean());
        Assert.Equal(timestamp, json.GetProperty("timestamp").GetDateTimeOffset());
    }

    [Theory]
    [InlineData(0.001, 100, 0.005, 300)]
    [InlineData(0.2, 2500, 0.08, 1800)]
    [InlineData(0.024, 900, 0.024, 900)]
    public void NormalizeSettings_MatchesWinUiAudioRanges(
        double threshold,
        int silenceMs,
        double expectedThreshold,
        int expectedSilenceMs)
    {
        var settings = new VoxLink.Models.AppSettings
        {
            VoiceThreshold = threshold,
            SilenceDurationMs = silenceMs
        };

        EngineHost.NormalizeSettings(settings);

        Assert.Equal(expectedThreshold, settings.VoiceThreshold, precision: 3);
        Assert.Equal(expectedSilenceMs, settings.SilenceDurationMs);
    }
    [Theory]
    [InlineData(VoxLink.Models.OutboundSpeechContent.Original, "prompt text", "zh")]
    [InlineData(VoxLink.Models.OutboundSpeechContent.Translation, "generated text", "en")]
    public void ResolveGeneratedSpeech_UsesConfiguredContentAndLanguage(
        VoxLink.Models.OutboundSpeechContent content,
        string expectedText,
        string expectedLanguage)
    {
        var settings = new VoxLink.Models.AppSettings
        {
            MyLanguageCode = "zh",
            OtherLanguageCode = "en",
            OutboundSpeechContent = content
        };

        var speech = EngineHost.ResolveGeneratedSpeech("prompt text", "generated text", settings);

        Assert.Equal(expectedText, speech.Text);
        Assert.Equal(expectedLanguage, speech.Language.Code);
    }

    [Fact]
    public async Task UnknownCommand_ReturnsProtocolSafeError()
    {
        await using var host = new EngineHost((_, _) => { }, startUiHost: false);
        using var parameters = JsonDocument.Parse("{}");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.HandleAsync(
                "unknown-command",
                parameters.RootElement,
                SerializerOptions,
                CancellationToken.None));

        Assert.Contains("unknown-command", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestVrChatOsc_SendsChatboxPacketThroughConfiguredEndpoint()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)receiver.Client.LocalEndPoint!;
        await using var host = new EngineHost((_, _) => { }, startUiHost: false);
        var parameters = JsonSerializer.SerializeToElement(new
        {
            text = "Engine OSC test",
            settings = new VoxLink.Models.AppSettings
            {
                VrChatOscAddress = "127.0.0.1",
                VrChatOscPort = endpoint.Port
            }
        }, SerializerOptions);

        var result = await host.HandleAsync(
            "testVrChatOsc",
            parameters,
            SerializerOptions,
            CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var received = await receiver.ReceiveAsync(timeout.Token);

        Assert.NotNull(result);
        Assert.StartsWith("/chatbox/input", System.Text.Encoding.UTF8.GetString(received.Buffer));
    }

    [Fact]
    public async Task TestVrOverlay_WithoutUiHostReturnsSafeStatus()
    {
        await using var host = new EngineHost((_, _) => { }, startUiHost: false);
        var parameters = JsonSerializer.SerializeToElement(new
        {
            settings = new VoxLink.Models.AppSettings { ShowVrOverlay = true }
        }, SerializerOptions);

        var result = await host.HandleAsync(
            "testVrOverlay",
            parameters,
            SerializerOptions,
            CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result, SerializerOptions);

        Assert.Equal("SteamVR 字幕宿主未启动", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task InvalidVrChatEndpoint_DisablesOptionalOutputWithoutBreakingInitialization()
    {
        var events = new List<(string Name, object Data)>();
        await using var host = new EngineHost(
            (name, data) => events.Add((name, data)),
            startUiHost: false);
        var parameters = JsonSerializer.SerializeToElement(new
        {
            settings = new VoxLink.Models.AppSettings
            {
                VrChatChatboxEnabled = true,
                VrChatOscAddress = "invalid"
            }
        }, SerializerOptions);

        var result = await host.HandleAsync(
            "initialize",
            parameters,
            SerializerOptions,
            CancellationToken.None);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.HandleAsync(
                "testVrChatOsc",
                parameters,
                SerializerOptions,
                CancellationToken.None));

        Assert.NotNull(result);
        Assert.Contains(events, item => item.Name == "error");
        Assert.Contains("OSC 配置无效", exception.Message, StringComparison.Ordinal);
    }
    [Fact]
    public void CharBigramJaccard_IdenticalTextScoresOne()
    {
        Assert.Equal(1.0, EngineHost.CharBigramJaccard("今天天气真不错", "今天天气真不错"));
    }

    [Fact]
    public void CharBigramJaccard_UnrelatedTextScoresLow()
    {
        Assert.True(EngineHost.CharBigramJaccard("今天天气真不错", "我想去吃火锅") < 0.5);
    }

    [Fact]
    public void CharBigramJaccard_IgnoresCaseAndPunctuation()
    {
        Assert.Equal(1.0, EngineHost.CharBigramJaccard("Hello, World!", "hello world"));
    }

    [Fact]
    public void CharBigramJaccard_EdgeCases()
    {
        Assert.Equal(1.0, EngineHost.CharBigramJaccard("好", "好"));
        Assert.Equal(0.0, EngineHost.CharBigramJaccard("好", "你好"));
        Assert.Equal(0.0, EngineHost.CharBigramJaccard("!", "?"));
        Assert.Equal(0.0, EngineHost.CharBigramJaccard(string.Empty, string.Empty));
        Assert.Equal(0.0, EngineHost.CharBigramJaccard(null, "文本"));
    }

    [Fact]
    public void IsEchoText_MatchesRecentInboundTranslation()
    {
        var inbound = new VoxLink.Models.ConversationMessage(
            VoxLink.Models.TranslationDirection.Inbound,
            "source",
            "我们一起去玩吧",
            DateTimeOffset.UtcNow);

        Assert.True(EngineHost.IsEchoText(
            "我们一起去玩吧",
            [inbound],
            EngineHost.EchoSimilarityThreshold));
        Assert.False(EngineHost.IsEchoText(
            "我晚上想早点睡",
            [inbound],
            EngineHost.EchoSimilarityThreshold));
        Assert.False(EngineHost.IsEchoText(null, [inbound], EngineHost.EchoSimilarityThreshold));
    }

    [Fact]
    public async Task LocalModelRpc_ListsInstallsAndRemovesThroughInjectedManager()
    {
        var manager = new RecordingLocalModelManager();
        await using var host = new EngineHost((_, _) => { }, startUiHost: false, manager);
        using var empty = JsonDocument.Parse("{}");

        var listed = await host.HandleAsync(
            "listLocalModels", empty.RootElement, SerializerOptions, CancellationToken.None);
        var listJson = JsonSerializer.SerializeToElement(listed, SerializerOptions);
        var model = Assert.Single(listJson.GetProperty("models").EnumerateArray());
        Assert.Equal("test-local-model", model.GetProperty("id").GetString());
        Assert.False(model.TryGetProperty("artifacts", out _));
        Assert.False(model.TryGetProperty("archive", out _));

        var parameters = JsonSerializer.SerializeToElement(new { modelId = "test-local-model" });
        var installed = await host.HandleAsync(
            "installLocalModel", parameters, SerializerOptions, CancellationToken.None);
        var installedJson = JsonSerializer.SerializeToElement(installed, SerializerOptions);
        Assert.True(installedJson.GetProperty("installed").GetBoolean());
        Assert.Equal("installed", installedJson.GetProperty("installState").GetString());

        var removed = await host.HandleAsync(
            "removeLocalModel", parameters, SerializerOptions, CancellationToken.None);
        Assert.True(JsonSerializer.SerializeToElement(removed, SerializerOptions)
            .GetProperty("removed").GetBoolean());
        Assert.Equal(["test-local-model"], manager.Installed);
        Assert.Equal(["test-local-model"], manager.Removed);
    }

    [Fact]
    public async Task RemoveLocalModel_SerializesWithConcurrentSessionStart()
    {
        var manager = new RecordingLocalModelManager { BlockRemove = true };
        await using var host = new EngineHost((_, _) => { }, startUiHost: false, manager);
        var removeParameters = JsonSerializer.SerializeToElement(
            new { modelId = "test-local-model" });
        var startParameters = JsonSerializer.SerializeToElement(new
        {
            settings = new AppSettings
            {
                CaptureMicrophone = false,
                CaptureSystemAudio = false
            }
        }, SerializerOptions);

        var remove = host.HandleAsync(
            "removeLocalModel",
            removeParameters,
            SerializerOptions,
            CancellationToken.None);
        await manager.RemoveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var start = host.HandleAsync(
            "startSession",
            startParameters,
            SerializerOptions,
            CancellationToken.None);

        await Task.Delay(100);
        Assert.False(start.IsCompleted);

        manager.RemoveRelease.TrySetResult();
        var removed = await remove.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(JsonSerializer.SerializeToElement(removed, SerializerOptions)
            .GetProperty("removed").GetBoolean());
        await Assert.ThrowsAsync<InvalidOperationException>(() => start);
    }

    [Fact]
    public async Task Shutdown_CancelsBlockedInstallBeforeWaitingForSessionModelGate()
    {
        var manager = new RecordingLocalModelManager { BlockInstall = true };
        await using var host = new EngineHost((_, _) => { }, startUiHost: false, manager);
        var installParameters = JsonSerializer.SerializeToElement(
            new { modelId = "test-local-model" });
        using var empty = JsonDocument.Parse("{}");
        var install = host.HandleAsync(
            "installLocalModel",
            installParameters,
            SerializerOptions,
            CancellationToken.None);
        await manager.InstallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var shutdown = host.HandleAsync(
            "shutdown",
            empty.RootElement,
            SerializerOptions,
            CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        var result = await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(JsonSerializer.SerializeToElement(result, SerializerOptions)
            .GetProperty("shutdown").GetBoolean());
        Assert.True(host.ShouldShutdown);
    }

    [Fact]
    public async Task DisposeAsync_CancelsAndDrainsInFlightRequest_ThenRejectsNewRequests()
    {
        var manager = new RecordingLocalModelManager { BlockInstall = true };
        var host = new EngineHost((_, _) => { }, startUiHost: false, manager);
        var parameters = JsonSerializer.SerializeToElement(new { modelId = "test-local-model" });
        var request = host.HandleAsync(
            "installLocalModel", parameters, SerializerOptions, CancellationToken.None);
        await manager.InstallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var dispose = host.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.HandleAsync(
            "listLocalModels", parameters, SerializerOptions, CancellationToken.None));
        Assert.False(manager.Disposed);
    }

    [Fact]
    public async Task DisposeAsync_IsConcurrentAndDoesNotDisposeInjectedManager()
    {
        var manager = new RecordingLocalModelManager();
        var host = new EngineHost((_, _) => { }, startUiHost: false, manager);

        var first = host.DisposeAsync().AsTask();
        var second = host.DisposeAsync().AsTask();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(manager.Disposed);
    }

    [Fact]
    public void NormalizeSettings_ClampsKokoroSpeakerAndSpeed()
    {
        var settings = new AppSettings { KokoroSpeakerId = 999, KokoroSpeed = double.NaN };

        EngineHost.NormalizeSettings(settings);

        Assert.Equal(102, settings.KokoroSpeakerId);
        Assert.Equal(1.0, settings.KokoroSpeed);
    }

    [Theory]
    [InlineData("{\"id\":1,\"method\":\"installLocalModel\",\"params\":{}}", true)]
    [InlineData("{\"id\":2,\"method\":\"listLocalModels\"}", false)]
    [InlineData("{\"id\":3,\"method\":\"shutdown\"}", false)]
    [InlineData("not-json", false)]
    public void Program_BackgroundsOnlyInstallRequests(string request, bool expected) =>
        Assert.Equal(expected, Program.IsBackgroundRequest(request));

    [Theory]
    [InlineData("{\"id\":1,\"method\":\"prepareManagedRuntime\",\"params\":{\"runtimeProfileId\":\"windows-translation-v1\"}}", true)]
    [InlineData("{\"id\":2,\"method\":\"probeManagedRuntime\",\"params\":{}}", false)]
    [InlineData("{\"id\":3,\"method\":\"cancelManagedRuntimePreparation\",\"params\":{}}", false)]
    [InlineData("{\"id\":4,\"method\":\"removeManagedRuntime\",\"params\":{}}", false)]
    [InlineData("{\"id\":5,\"method\":\"listManagedRuntimes\"}", false)]
    public void Program_BackgroundsOnlyPrepareManagedRuntimeRequests(string request, bool expected) =>
        Assert.Equal(expected, Program.IsBackgroundRequest(request));

    [Fact]
    public void ModelProgressPayload_PreservesLegacyAndModelScopedShapes()
    {
        var scoped = JsonSerializer.SerializeToElement(
            EngineHost.CreateModelProgressPayload("model-a", "translation", "下载中", 0.25),
            SerializerOptions);
        var legacy = JsonSerializer.SerializeToElement(
            EngineHost.CreateModelProgressPayload(null, null, "Whisper 下载中", 0.5),
            SerializerOptions);

        Assert.Equal("model-a", scoped.GetProperty("modelId").GetString());
        Assert.Equal("translation", scoped.GetProperty("category").GetString());
        Assert.Equal(0.25, scoped.GetProperty("progress").GetDouble());
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("modelId").ValueKind);
        Assert.Equal(0.5, legacy.GetProperty("progress").GetDouble());
    }

    [Fact]
    public void IsLoopbackLikeDeviceName_DetectsStereoMixAndLoopbackDevices()
    {
        Assert.True(VoxLink.Audio.WasapiSpeechCapture.IsLoopbackLikeDeviceName("立体声混音 (Realtek(R) Audio)"));
        Assert.True(VoxLink.Audio.WasapiSpeechCapture.IsLoopbackLikeDeviceName("Stereo Mix"));
        Assert.True(VoxLink.Audio.WasapiSpeechCapture.IsLoopbackLikeDeviceName("What U Hear"));
        Assert.True(VoxLink.Audio.WasapiSpeechCapture.IsLoopbackLikeDeviceName("系统回环音频"));
        Assert.False(VoxLink.Audio.WasapiSpeechCapture.IsLoopbackLikeDeviceName("麦克风阵列 (Realtek(R) Audio)"));
        Assert.False(VoxLink.Audio.WasapiSpeechCapture.IsLoopbackLikeDeviceName(null));
    }

    [Fact]
    public async Task ListManagedRuntimes_OntheWireShapeIsSanitized()
    {
        var runtimeManager = new RecordingManagedRuntimeManager();
        await using var host = new EngineHost(
            (_, _) => { },
            startUiHost: false,
            localModelManager: null,
            managedRuntimeManager: runtimeManager,
            localModelOrchestrator: null);
        using var empty = JsonDocument.Parse("{}");

        var listed = await host.HandleAsync(
            "listManagedRuntimes", empty.RootElement, SerializerOptions, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(listed, SerializerOptions);
        var runtimes = json.GetProperty("runtimes").EnumerateArray().ToArray();

        Assert.Equal(2, runtimes.Length);
        Assert.Equal(ManagedRuntimeCatalog.WindowsTranslation, runtimes[0].GetProperty("id").GetString());
        Assert.Equal("windowspython", runtimes[0].GetProperty("platform").GetString());
        Assert.Equal("3.12", runtimes[0].GetProperty("pythonVersion").GetString());
        Assert.False(runtimes[0].GetProperty("requiresNvidiaGpu").GetBoolean());
        Assert.Equal(0L, runtimes[0].GetProperty("minimumGpuMemoryBytes").GetInt64());
        Assert.Equal(ManagedRuntimeCatalog.WslMoss, runtimes[1].GetProperty("id").GetString());
        Assert.Equal("wslcuda", runtimes[1].GetProperty("platform").GetString());
        Assert.True(runtimes[1].GetProperty("requiresNvidiaGpu").GetBoolean());
        Assert.Equal(6L * 1024 * 1024 * 1024, runtimes[1].GetProperty("minimumGpuMemoryBytes").GetInt64());

        foreach (var runtime in runtimes)
        {
            var names = runtime.EnumerateObject().Select(property => property.Name).ToArray();
            Assert.Equal(
                ["id", "platform", "pythonVersion", "requiresNvidiaGpu", "minimumGpuMemoryBytes"],
                names);
            Assert.DoesNotContain("lockFile", names);
            Assert.DoesNotContain("sourceRepository", names);
            Assert.DoesNotContain("sourceRevision", names);
            Assert.DoesNotContain("sha256", names);
            Assert.DoesNotContain("url", names);
            Assert.DoesNotContain("path", names);
        }

        var wire = JsonSerializer.Serialize(listed, SerializerOptions);
        Assert.DoesNotContain("github.com", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("0e3d1403", wire, StringComparison.Ordinal);
        Assert.DoesNotContain(".lock", wire, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListManagedRuntimes_WithoutInjectedManager_UsesDefaultCatalogSanitized()
    {
        await using var host = new EngineHost((_, _) => { }, startUiHost: false);
        using var empty = JsonDocument.Parse("{}");

        var listed = await host.HandleAsync(
            "listManagedRuntimes", empty.RootElement, SerializerOptions, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(listed, SerializerOptions);
        var runtimes = json.GetProperty("runtimes").EnumerateArray().ToArray();

        Assert.Equal(ManagedRuntimeCatalog.All.Count, runtimes.Length);
        Assert.Contains(runtimes,
            runtime => runtime.GetProperty("id").GetString() == ManagedRuntimeCatalog.WindowsTranslation);
        foreach (var runtime in runtimes)
        {
            var names = runtime.EnumerateObject().Select(property => property.Name).ToArray();
            Assert.Equal(
                ["id", "platform", "pythonVersion", "requiresNvidiaGpu", "minimumGpuMemoryBytes"],
                names);
        }

        var wire = JsonSerializer.Serialize(listed, SerializerOptions);
        Assert.DoesNotContain("github.com", wire, StringComparison.Ordinal);
        Assert.DoesNotContain(".lock", wire, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagedRuntimeRpc_DispatchesProbePrepareCancelRemoveThroughInjectedManager()
    {
        var runtimeManager = new RecordingManagedRuntimeManager();
        await using var host = new EngineHost(
            (_, _) => { },
            startUiHost: false,
            localModelManager: null,
            managedRuntimeManager: runtimeManager,
            localModelOrchestrator: null);
        var parameters = JsonSerializer.SerializeToElement(
            new { runtimeProfileId = ManagedRuntimeCatalog.WindowsTranslation });

        var probe = await host.HandleAsync(
            "probeManagedRuntime", parameters, SerializerOptions, CancellationToken.None);
        var probeJson = JsonSerializer.SerializeToElement(probe, SerializerOptions);
        Assert.Equal(ManagedRuntimeCatalog.WindowsTranslation,
            probeJson.GetProperty("runtimeProfileId").GetString());
        Assert.Equal((int)ManagedRuntimeState.Ready, probeJson.GetProperty("state").GetInt32());
        Assert.True(probeJson.GetProperty("isReady").GetBoolean());
        Assert.Equal("就绪", probeJson.GetProperty("status").GetString());
        Assert.Equal([ManagedRuntimeCatalog.WindowsTranslation], runtimeManager.Probed);

        var prepared = await host.HandleAsync(
            "prepareManagedRuntime", parameters, SerializerOptions, CancellationToken.None);
        var preparedJson = JsonSerializer.SerializeToElement(prepared, SerializerOptions);
        Assert.Equal((int)ManagedRuntimeState.Ready, preparedJson.GetProperty("state").GetInt32());
        Assert.True(preparedJson.GetProperty("isReady").GetBoolean());
        Assert.Equal([ManagedRuntimeCatalog.WindowsTranslation], runtimeManager.Prepared);

        var cancelled = await host.HandleAsync(
            "cancelManagedRuntimePreparation", parameters, SerializerOptions, CancellationToken.None);
        Assert.True(JsonSerializer.SerializeToElement(cancelled, SerializerOptions)
            .GetProperty("cancelled").GetBoolean());
        Assert.Equal([ManagedRuntimeCatalog.WindowsTranslation], runtimeManager.Cancelled);

        runtimeManager.CancelResult = false;
        var notCancelled = await host.HandleAsync(
            "cancelManagedRuntimePreparation", parameters, SerializerOptions, CancellationToken.None);
        Assert.False(JsonSerializer.SerializeToElement(notCancelled, SerializerOptions)
            .GetProperty("cancelled").GetBoolean());
        Assert.Equal(
            [ManagedRuntimeCatalog.WindowsTranslation, ManagedRuntimeCatalog.WindowsTranslation],
            runtimeManager.Cancelled);

        var removed = await host.HandleAsync(
            "removeManagedRuntime", parameters, SerializerOptions, CancellationToken.None);
        Assert.True(JsonSerializer.SerializeToElement(removed, SerializerOptions)
            .GetProperty("removed").GetBoolean());
        Assert.Equal([ManagedRuntimeCatalog.WindowsTranslation], runtimeManager.Removed);
    }

    [Fact]
    public async Task ManagedRuntimeRpc_UnexpectedInfrastructureFailure_UsesFixedSafeMessage()
    {
        const string sensitive = @"C:\Users\secret\runtime https://example.invalid stderr-token api-secret";
        var runtimeManager = new RecordingManagedRuntimeManager
        {
            ProbeException = new InvalidOperationException(sensitive)
        };
        await using var host = new EngineHost(
            (_, _) => { },
            startUiHost: false,
            localModelManager: null,
            managedRuntimeManager: runtimeManager,
            localModelOrchestrator: null);
        var parameters = JsonSerializer.SerializeToElement(
            new { runtimeProfileId = ManagedRuntimeCatalog.WindowsTranslation });

        var exception = await Assert.ThrowsAsync<ManagedRuntimeException>(() => host.HandleAsync(
            "probeManagedRuntime",
            parameters,
            SerializerOptions,
            CancellationToken.None));

        Assert.Equal("托管模型运行时操作失败，请重试或修复运行时。", exception.Message);
        Assert.Same(runtimeManager.ProbeException, exception.InnerException);
        var publicMessage = Program.GetPublicErrorMessage(exception);
        Assert.Equal(exception.Message, publicMessage);
        Assert.DoesNotContain(sensitive, publicMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", publicMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("stderr-token", publicMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("api-secret", publicMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestTranslation_ManagedProvider_UnpreparedRuntime_UsesFixedSafeMessage()
    {
        var runtimeManager = new RecordingManagedRuntimeManager();
        await using var host = new EngineHost(
            (_, _) => { },
            startUiHost: false,
            localModelManager: null,
            managedRuntimeManager: runtimeManager,
            localModelOrchestrator: null);

        var initialize = JsonSerializer.SerializeToElement(new
        {
            settings = new AppSettings
            {
                TranslationProvider = TranslationProvider.ManagedSmall100
            }
        }, SerializerOptions);
        await host.HandleAsync(
            "initialize",
            initialize,
            SerializerOptions,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.HandleAsync(
                "testTranslation",
                JsonSerializer.SerializeToElement(new { }, SerializerOptions),
                SerializerOptions,
                CancellationToken.None));

        // 模型未安装 → 模型管理器的固定安全消息（比通用消息更精确），且不暴露基础设施文本。
        Assert.Contains("尚未安装", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Not supported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RuntimeProgressEvent_NotifiesSanitizedPayload()
    {
        var events = new List<(string Name, object Data)>();
        var runtimeManager = new RecordingManagedRuntimeManager();
        await using var host = new EngineHost(
            (name, data) => events.Add((name, data)),
            startUiHost: false,
            localModelManager: null,
            managedRuntimeManager: runtimeManager,
            localModelOrchestrator: null);

        runtimeManager.RaiseProgress(ManagedRuntimeCatalog.WslMoss, "正在检查托管运行时…", 0);
        runtimeManager.RaiseProgress(ManagedRuntimeCatalog.WslMoss, "托管运行时已就绪", 1);

        var payloads = events
            .Where(item => item.Name == "runtimeProgress")
            .Select(item => JsonSerializer.SerializeToElement(item.Data, SerializerOptions))
            .ToArray();
        Assert.Equal(2, payloads.Length);
        Assert.Equal(ManagedRuntimeCatalog.WslMoss,
            payloads[0].GetProperty("runtimeProfileId").GetString());
        Assert.Equal("正在检查托管运行时…", payloads[0].GetProperty("status").GetString());
        Assert.Equal(0.0, payloads[0].GetProperty("progress").GetDouble());
        Assert.Equal(ManagedRuntimeCatalog.WslMoss,
            payloads[1].GetProperty("runtimeProfileId").GetString());
        Assert.Equal("托管运行时已就绪", payloads[1].GetProperty("status").GetString());
        Assert.Equal(1.0, payloads[1].GetProperty("progress").GetDouble());
    }

    [Fact]
    public async Task Shutdown_CancelsBlockedManagedRuntimePreparationBeforeWaitingForSessionModelGate()
    {
        var runtimeManager = new RecordingManagedRuntimeManager { BlockPrepare = true };
        await using var host = new EngineHost(
            (_, _) => { },
            startUiHost: false,
            localModelManager: null,
            managedRuntimeManager: runtimeManager,
            localModelOrchestrator: null);
        var parameters = JsonSerializer.SerializeToElement(
            new { runtimeProfileId = ManagedRuntimeCatalog.WindowsTranslation });
        using var empty = JsonDocument.Parse("{}");

        var prepare = host.HandleAsync(
            "prepareManagedRuntime", parameters, SerializerOptions, CancellationToken.None);
        await runtimeManager.PrepareStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var shutdown = host.HandleAsync(
            "shutdown", empty.RootElement, SerializerOptions, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare);
        var result = await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(JsonSerializer.SerializeToElement(result, SerializerOptions)
            .GetProperty("shutdown").GetBoolean());
        Assert.True(host.ShouldShutdown);
    }

    [Fact]
    public async Task DisposeAsync_CancelsBlockedManagedRuntimePreparation_AndDoesNotDisposeInjectedManagers()
    {
        var runtimeManager = new RecordingManagedRuntimeManager { BlockPrepare = true };
        var orchestrator = new RecordingLocalModelOrchestrator();
        var host = new EngineHost(
            (_, _) => { },
            startUiHost: false,
            localModelManager: null,
            managedRuntimeManager: runtimeManager,
            localModelOrchestrator: orchestrator);
        var parameters = JsonSerializer.SerializeToElement(
            new { runtimeProfileId = ManagedRuntimeCatalog.WindowsTranslation });
        var prepare = host.HandleAsync(
            "prepareManagedRuntime", parameters, SerializerOptions, CancellationToken.None);
        await runtimeManager.PrepareStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var dispose = host.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare);
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.HandleAsync(
            "listManagedRuntimes", parameters, SerializerOptions, CancellationToken.None));
        Assert.False(runtimeManager.Disposed);
        Assert.False(orchestrator.Disposed);
    }

    private sealed class RecordingLocalModelManager : ILocalModelManager, IAsyncDisposable
    {
        private LocalModelInstallState _state = LocalModelInstallState.NotInstalled;

        public event EventHandler<LocalModelProgressEventArgs>? ModelProgress;
        public List<string> Installed { get; } = [];
        public List<string> Removed { get; } = [];
        public TaskCompletionSource InstallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockInstall { get; init; }
        public bool BlockRemove { get; init; }
        public TaskCompletionSource RemoveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RemoveRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public IReadOnlyList<LocalModelDefinition> List() =>
        [
            new()
            {
                Id = "test-local-model",
                Name = "Test local model",
                Category = LocalModelCategory.Translation,
                SupportLevel = LocalModelSupportLevel.Stable,
                Runtime = LocalModelRuntimeKind.LlamaCppGguf,
                InstallKind = LocalModelInstallKind.SingleFile,
                Parameters = "1B",
                NumericParameterBillions = 1,
                License = "MIT",
                Languages = "zh/en",
                Requirements = "test",
                SourceUrl = "https://huggingface.co/test/model",
                Description = "test model",
                Artifacts =
                [
                    new LocalModelArtifact(
                        "secret.bin", 1, new string('0', 64),
                        "https://huggingface.co/test/model.bin", null)
                ]
            }
        ];

        public LocalModelInstallState GetStatus(string modelId) => _state;

        public async Task InstallAsync(string modelId, CancellationToken cancellationToken = default)
        {
            Installed.Add(modelId);
            InstallStarted.TrySetResult();
            if (BlockInstall)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            _state = LocalModelInstallState.Installed;
            ModelProgress?.Invoke(this, new LocalModelProgressEventArgs(
                modelId, LocalModelCategory.Translation, "完成", 1));
        }

        public async Task<bool> RemoveAsync(
            string modelId,
            CancellationToken cancellationToken = default)
        {
            Removed.Add(modelId);
            RemoveStarted.TrySetResult();
            if (BlockRemove)
            {
                await RemoveRelease.Task.WaitAsync(cancellationToken);
            }
            _state = LocalModelInstallState.NotInstalled;
            return true;
        }

        public ILocalModelLease AcquireUsage(string modelId) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingManagedRuntimeManager : IManagedModelRuntimeManager
    {
        public event EventHandler<ManagedRuntimeProgressEventArgs>? RuntimeProgress;

        public List<string> Probed { get; } = [];
        public List<string> Prepared { get; } = [];
        public List<string> Cancelled { get; } = [];
        public List<string> Removed { get; } = [];

        public bool BlockPrepare { get; init; }
        public bool CancelResult { get; set; } = true;
        public Exception? ProbeException { get; init; }
        public TaskCompletionSource PrepareStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PrepareRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public IReadOnlyList<ManagedRuntimeDefinition> List() => RuntimeCatalog;

        public Task<ManagedRuntimeProbe> ProbeAsync(
            string runtimeProfileId,
            CancellationToken cancellationToken = default)
        {
            Probed.Add(runtimeProfileId);
            if (ProbeException is not null)
            {
                throw ProbeException;
            }

            return Task.FromResult(CreateReadyProbe(runtimeProfileId));
        }

        public async Task<ManagedRuntimeProbe> PrepareAsync(
            string runtimeProfileId,
            CancellationToken cancellationToken = default)
        {
            Prepared.Add(runtimeProfileId);
            PrepareStarted.TrySetResult();
            if (BlockPrepare)
            {
                await PrepareRelease.Task.WaitAsync(cancellationToken);
            }

            return CreateReadyProbe(runtimeProfileId);
        }

        public Task<IManagedRuntimeLease> AcquireUsageAsync(
            string runtimeProfileId,
            string modelDirectory,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool CancelPreparation(string runtimeProfileId)
        {
            Cancelled.Add(runtimeProfileId);
            return CancelResult;
        }

        public Task<bool> RemoveAsync(
            string runtimeProfileId,
            CancellationToken cancellationToken = default)
        {
            Removed.Add(runtimeProfileId);
            return Task.FromResult(true);
        }

        public void RaiseProgress(string runtimeProfileId, string status, double? progress) =>
            RuntimeProgress?.Invoke(this,
                new ManagedRuntimeProgressEventArgs(runtimeProfileId, status, progress));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        private static ManagedRuntimeProbe CreateReadyProbe(string runtimeProfileId) => new()
        {
            RuntimeProfileId = runtimeProfileId,
            Platform = ManagedRuntimePlatform.WindowsPython,
            State = ManagedRuntimeState.Ready,
            RequiredAction = ManagedRuntimeUserAction.None,
            Status = "就绪",
            PythonVersion = "3.12",
            WslAvailable = false,
            DistributionInstalled = false,
            NvidiaAvailable = false,
            NvidiaMemoryBytes = null,
            NvidiaDriverVersion = null
        };

        private static readonly ManagedRuntimeDefinition[] RuntimeCatalog =
        [
            new(
                "windows-translation-v1",
                ManagedRuntimePlatform.WindowsPython,
                "3.12",
                "windows-translation.lock",
                null,
                null,
                RequiresNvidiaGpu: false,
                MinimumGpuMemoryBytes: 0),
            new(
                "wsl-moss-v1",
                ManagedRuntimePlatform.WslCuda,
                "3.12",
                "wsl-moss.lock",
                "https://github.com/OpenMOSS/MOSS-Transcribe-Diarize.git",
                "0e3d1403fd8f1f1c674e883ece96b9f630794ebe",
                RequiresNvidiaGpu: true,
                MinimumGpuMemoryBytes: 6L * 1024 * 1024 * 1024)
        ];
    }

    private sealed class RecordingLocalModelOrchestrator : ILocalModelOrchestrator
    {
        public bool Disposed { get; private set; }

        public Task<ManagedRuntimeProbe> ProbeModelRuntimeAsync(
            string modelId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
