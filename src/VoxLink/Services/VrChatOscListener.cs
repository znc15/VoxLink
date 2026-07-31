using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VoxLink.Services;

internal sealed class VrChatOscListener : IAsyncDisposable
{
    internal const string MuteSelfAddress = "/avatar/parameters/MuteSelf";
    private readonly IPEndPoint _endpoint;
    private readonly CancellationTokenSource _shutdown = new();
    private UdpClient? _udpClient;
    private Task? _receiveLoop;
    private bool _disposed;

    public VrChatOscListener(string? address, int port)
    {
        _endpoint = ParseEndpoint(address, port);
    }

    public event EventHandler<bool>? MuteStateChanged;

    public event EventHandler<Exception>? ListenFailed;

    public bool IsMuted { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_receiveLoop is not null)
        {
            return;
        }

        _udpClient = new UdpClient(_endpoint);
        _receiveLoop = ReceiveLoopAsync(_shutdown.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _udpClient?.Dispose();
        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
    }

    internal static IPEndPoint ParseEndpoint(string? address, int port)
    {
        if (!IPAddress.TryParse(address?.Trim(), out var ipAddress)
            || ipAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException("VRChat OSC 监听地址必须是有效的本机 IPv4 地址。");
        }

        if (port is < 1 or > 65_535)
        {
            throw new InvalidOperationException("VRChat OSC 监听端口必须在 1 到 65535 之间。");
        }

        return new IPEndPoint(ipAddress, port);
    }

    internal static bool TryDecodeMuteSelf(ReadOnlySpan<byte> packet, out bool muted)
    {
        muted = false;
        return TryDecodePacket(packet, ref muted);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var udpClient = _udpClient ?? throw new InvalidOperationException("OSC 监听器尚未启动。");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await udpClient.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (TryDecodeMuteSelf(result.Buffer, out var muted) && muted != IsMuted)
                {
                    IsMuted = muted;
                    MuteStateChanged?.Invoke(this, muted);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException exception) when (!cancellationToken.IsCancellationRequested)
        {
            ListenFailed?.Invoke(this, exception);
        }
    }

    private static bool TryDecodePacket(ReadOnlySpan<byte> packet, ref bool muted)
    {
        if (packet.StartsWith("#bundle\0"u8))
        {
            if (packet.Length < 16)
            {
                return false;
            }

            var offset = 16;
            var found = false;
            while (offset + sizeof(int) <= packet.Length)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(packet[offset..]);
                offset += sizeof(int);
                if (length <= 0 || offset + length > packet.Length)
                {
                    return found;
                }

                found |= TryDecodePacket(packet.Slice(offset, length), ref muted);
                offset += length;
            }

            return found;
        }

        var cursor = 0;
        if (!TryReadOscString(packet, ref cursor, out var address)
            || !string.Equals(address, MuteSelfAddress, StringComparison.Ordinal)
            || !TryReadOscString(packet, ref cursor, out var typeTags)
            || typeTags.Length < 2
            || typeTags[0] != ',')
        {
            return false;
        }

        switch (typeTags[1])
        {
            case 'T':
                muted = true;
                return true;
            case 'F':
                muted = false;
                return true;
            case 'i' when cursor + sizeof(int) <= packet.Length:
                muted = BinaryPrimitives.ReadInt32BigEndian(packet[cursor..]) != 0;
                return true;
            case 'f' when cursor + sizeof(int) <= packet.Length:
                var bits = BinaryPrimitives.ReadInt32BigEndian(packet[cursor..]);
                muted = BitConverter.Int32BitsToSingle(bits) > 0.5f;
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadOscString(
        ReadOnlySpan<byte> packet,
        ref int offset,
        out string value)
    {
        value = string.Empty;
        if (offset >= packet.Length)
        {
            return false;
        }

        var remainder = packet[offset..];
        var terminator = remainder.IndexOf((byte)0);
        if (terminator < 0)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(remainder[..terminator]);
        offset += terminator + 1;
        offset = (offset + 3) & ~3;
        return offset <= packet.Length;
    }
}
