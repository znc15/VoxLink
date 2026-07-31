using System.Buffers.Binary;
using System.Text;
using VoxLink.Engine;
using VoxLink.Models;
using VoxLink.Services;
using Xunit;

namespace VoxLink.Tests.Services;

public sealed class VrChatOscListenerTests
{
    [Theory]
    [InlineData('T', 0, true)]
    [InlineData('F', 0, false)]
    [InlineData('i', 1, true)]
    [InlineData('i', 0, false)]
    public void TryDecodeMuteSelf_DecodesBooleanAndInteger(char type, int value, bool expected)
    {
        var packet = OscMessage(VrChatOscListener.MuteSelfAddress, type, value);

        var decoded = VrChatOscListener.TryDecodeMuteSelf(packet, out var muted);

        Assert.True(decoded);
        Assert.Equal(expected, muted);
    }

    [Theory]
    [InlineData(0.75f, true)]
    [InlineData(0.25f, false)]
    public void TryDecodeMuteSelf_DecodesFloat(float value, bool expected)
    {
        var packet = OscMessage(VrChatOscListener.MuteSelfAddress, 'f', value);

        var decoded = VrChatOscListener.TryDecodeMuteSelf(packet, out var muted);

        Assert.True(decoded);
        Assert.Equal(expected, muted);
    }

    [Fact]
    public void TryDecodeMuteSelf_FindsLastMatchingMessageInBundle()
    {
        var packet = OscBundle(
            OscMessage("/avatar/parameters/Unrelated", 'T', 0),
            OscMessage(VrChatOscListener.MuteSelfAddress, 'F', 0),
            OscMessage(VrChatOscListener.MuteSelfAddress, 'i', 1));

        var decoded = VrChatOscListener.TryDecodeMuteSelf(packet, out var muted);

        Assert.True(decoded);
        Assert.True(muted);
    }

    [Fact]
    public void TryDecodeMuteSelf_RejectsMalformedAndUnrelatedPackets()
    {
        Assert.False(VrChatOscListener.TryDecodeMuteSelf([0x01, 0x02], out _));
        Assert.False(VrChatOscListener.TryDecodeMuteSelf(
            OscMessage("/avatar/parameters/Voice", 'T', 0),
            out _));
    }

    [Fact]
    public void ComposeVrChatMessage_UsesOnlyPrimaryFinalOutboundTranslation()
    {
        var settings = new AppSettings
        {
            VrChatChatboxEnabled = true,
            VrChatIncludeSourceText = true
        };
        var outbound = new ConversationMessage(
            TranslationDirection.Outbound,
            "source",
            "primary",
            DateTimeOffset.UtcNow)
        {
            SecondaryTranslatedText = "secondary"
        };

        Assert.Equal("primary\nsource", EngineHost.ComposeVrChatMessage(outbound, settings));
        Assert.Equal(
            "primary\nsource",
            EngineHost.ComposeVrChatMessage(outbound with { Direction = TranslationDirection.Typed }, settings));
        Assert.Null(EngineHost.ComposeVrChatMessage(
            outbound with { Direction = TranslationDirection.Inbound },
            settings));
        Assert.Null(EngineHost.ComposeVrChatMessage(outbound with { IsFinal = false }, settings));
        Assert.Null(EngineHost.ComposeVrChatMessage(outbound with { TranscriptionOnly = true }, settings));

        settings.VrChatChatboxEnabled = false;
        Assert.Null(EngineHost.ComposeVrChatMessage(outbound, settings));
    }

    private static byte[] OscMessage(string address, char type, object value)
    {
        using var stream = new MemoryStream();
        WriteOscString(stream, address);
        WriteOscString(stream, $",{type}");
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        switch (type)
        {
            case 'i':
                BinaryPrimitives.WriteInt32BigEndian(buffer, Convert.ToInt32(value));
                stream.Write(buffer);
                break;
            case 'f':
                BinaryPrimitives.WriteInt32BigEndian(
                    buffer,
                    BitConverter.SingleToInt32Bits(Convert.ToSingle(value)));
                stream.Write(buffer);
                break;
        }

        return stream.ToArray();
    }

    private static byte[] OscBundle(params byte[][] messages)
    {
        using var stream = new MemoryStream();
        WriteOscString(stream, "#bundle");
        stream.Write(new byte[8]);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var message in messages)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, message.Length);
            stream.Write(length);
            stream.Write(message);
        }

        return stream.ToArray();
    }

    private static void WriteOscString(Stream stream, string value)
    {
        stream.Write(Encoding.UTF8.GetBytes(value));
        stream.WriteByte(0);
        while (stream.Length % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }
}
