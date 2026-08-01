using System.Net;
using System.Net.Sockets;
using System.Text;
using VoxLink.Services;
using Xunit;

namespace VoxLink.Tests.Services;

public sealed class VrChatOscSenderTests
{
    [Fact]
    public void EncodeChatboxInput_UsesOscStringsAndBooleanTypeTag()
    {
        var packet = VrChatOscSender.EncodeChatboxInput("Hello");

        Assert.Equal(
            "/chatbox/input\0\0,sT\0Hello\0\0\0",
            Encoding.UTF8.GetString(packet));
        Assert.Equal(0, packet.Length % 4);
    }

    [Fact]
    public void EncodeChatboxInput_PrefillUsesFalseTypeTag()
    {
        var packet = VrChatOscSender.EncodeChatboxInput("test", sendImmediately: false);

        Assert.Contains(",sF\0", Encoding.UTF8.GetString(packet), StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeChatboxText_TruncatesByTextElementAndRemovesNulls()
    {
        var input = string.Concat(Enumerable.Repeat("译", 150)) + "\0ignored";

        var result = VrChatOscSender.NormalizeChatboxText(input);

        Assert.Equal(VrChatOscSender.MaxChatboxTextElements, result.EnumerateRunes().Count());
        Assert.DoesNotContain('\0', result);
    }

    [Fact]
    public void ComposeTranslation_PreservesTranslationBeforeOptionalSourceText()
    {
        var translated = string.Concat(Enumerable.Repeat("译", 140));

        var result = VrChatOscSender.ComposeTranslation(
            translated,
            "source text that would exceed the limit",
            includeSourceText: true);

        Assert.StartsWith(translated, result, StringComparison.Ordinal);
        Assert.Equal(VrChatOscSender.MaxChatboxTextElements, result.EnumerateRunes().Count());
    }

    [Fact]
    public void ComposeTranslation_AppendsSecondaryBeforeSource()
    {
        var result = VrChatOscSender.ComposeTranslation(
            "primary",
            "source",
            includeSourceText: true,
            secondaryText: "secondary");

        Assert.Equal("primary\nsecondary\nsource", result);
    }

    [Fact]
    public void ComposeTranslation_OmitsBlankSecondaryAndSource()
    {
        Assert.Equal(
            "primary",
            VrChatOscSender.ComposeTranslation(
                "primary",
                " ",
                includeSourceText: true,
                secondaryText: "   "));
    }
    [Fact]
    public async Task Configure_InvalidAddressThrows()
    {
        await using var sender = new VrChatOscSender();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            sender.Configure(enabled: false, "not-an-address", 9000));

        Assert.Contains("IPv4", exception.Message, StringComparison.Ordinal);
    }
    [Fact]
    public async Task SendTestAsync_SendsPacketToConfiguredUdpEndpoint()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)receiver.Client.LocalEndPoint!;
        await using var sender = new VrChatOscSender();
        sender.Configure(enabled: false, "127.0.0.1", endpoint.Port);

        await sender.SendTestAsync("VoxLink 测试");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var received = await receiver.ReceiveAsync(timeout.Token);

        Assert.Equal(
            VrChatOscSender.EncodeChatboxInput("VoxLink 测试"),
            received.Buffer);
    }

    [Fact]
    public async Task TryQueue_SuppressesOnlyImmediateDuplicateMessages()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)receiver.Client.LocalEndPoint!;
        await using var sender = new VrChatOscSender();
        sender.Configure(enabled: true, "127.0.0.1", endpoint.Port);

        Assert.True(sender.TryQueue("repeatable"));
        Assert.True(sender.TryQueue("repeatable"));
        using (var firstTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            _ = await receiver.ReceiveAsync(firstTimeout.Token);
        }

        using (var duplicateTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(350)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await receiver.ReceiveAsync(duplicateTimeout.Token));
        }

        await Task.Delay(VrChatOscSender.MinimumSendInterval + TimeSpan.FromMilliseconds(100));
        Assert.True(sender.TryQueue("repeatable"));
        using var repeatedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var repeated = await receiver.ReceiveAsync(repeatedTimeout.Token);

        Assert.Equal(VrChatOscSender.EncodeChatboxInput("repeatable"), repeated.Buffer);
    }
}
