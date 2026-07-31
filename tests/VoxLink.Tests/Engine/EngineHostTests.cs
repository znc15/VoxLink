using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using VoxLink.Engine;

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
}
