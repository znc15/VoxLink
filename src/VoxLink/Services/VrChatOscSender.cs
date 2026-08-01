using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace VoxLink.Services;

internal sealed class VrChatOscSender : IAsyncDisposable
{
    internal const int MaxChatboxTextElements = 144;
    internal static readonly TimeSpan MinimumSendInterval = TimeSpan.FromMilliseconds(1_500);

    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(4)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly UdpClient _udpClient = new(AddressFamily.InterNetwork);
    private readonly object _configurationGate = new();
    private readonly Task _worker;
    private IPEndPoint _endpoint = new(IPAddress.Loopback, 9000);
    private bool _enabled;
    private DateTimeOffset _nextSendAt;
    private string? _lastSentText;
    private DateTimeOffset _lastSentAt;
    private bool _disposed;

    public event EventHandler<Exception>? SendFailed;
    public VrChatOscSender()
    {
        _worker = ProcessQueueAsync();
    }

    public void Configure(bool enabled, string? host, int port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var endpoint = ParseEndpoint(host, port);
        lock (_configurationGate)
        {
            var endpointChanged = !_endpoint.Equals(endpoint);
            _endpoint = endpoint;
            _enabled = enabled;
            if (endpointChanged)
            {
                _lastSentText = null;
                _lastSentAt = default;
            }
        }
    }

    internal static IPEndPoint ParseEndpoint(string? host, int port)
    {
        if (!IPAddress.TryParse(host?.Trim(), out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException("VRChat OSC 地址必须是有效的 IPv4 地址。");
        }

        if (port is < 1 or > 65_535)
        {
            throw new InvalidOperationException("VRChat OSC 端口必须在 1 到 65535 之间。");
        }

        return new IPEndPoint(address, port);
    }

    public bool TryQueue(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_configurationGate)
        {
            if (!_enabled)
            {
                return false;
            }
        }

        var normalized = NormalizeChatboxText(text);
        return normalized.Length > 0 && _queue.Writer.TryWrite(normalized);
    }

    public Task SendTestAsync(
        string text,
        bool sendImmediately = true,
        CancellationToken cancellationToken = default) =>
        SendAsync(text, sendImmediately, allowWhenDisabled: true, force: true, cancellationToken);

    internal static byte[] EncodeChatboxInput(string text, bool sendImmediately = true)
    {
        var normalized = NormalizeChatboxText(text);
        using var stream = new MemoryStream();
        WriteOscString(stream, "/chatbox/input");
        WriteOscString(stream, sendImmediately ? ",sT" : ",sF");
        WriteOscString(stream, normalized);
        return stream.ToArray();
    }

    internal static string ComposeTranslation(
        string translatedText,
        string sourceText,
        bool includeSourceText,
        string? secondaryText = null)
    {
        var parts = new List<string> { translatedText.Trim() };
        if (!string.IsNullOrWhiteSpace(secondaryText))
        {
            parts.Add(secondaryText.Trim());
        }

        if (includeSourceText && !string.IsNullOrWhiteSpace(sourceText))
        {
            parts.Add(sourceText.Trim());
        }

        return NormalizeChatboxText(string.Join("\n", parts));

    }
    internal static string NormalizeChatboxText(string text)
    {
        var normalized = (text ?? string.Empty).Replace('\0', ' ').Trim();
        var elementStarts = StringInfo.ParseCombiningCharacters(normalized);
        return elementStarts.Length <= MaxChatboxTextElements
            ? normalized
            : normalized[..elementStarts[MaxChatboxTextElements]];
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.TryComplete();
        _shutdown.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _udpClient.Dispose();
        _sendGate.Dispose();
        _shutdown.Dispose();
    }

    private static void WriteOscString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
        stream.WriteByte(0);
        while (stream.Length % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var text in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    await SendAsync(
                        text,
                        sendImmediately: true,
                        allowWhenDisabled: false,
                        force: false,
                        _shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is SocketException or InvalidOperationException)
                {
                    SendFailed?.Invoke(this, exception);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task SendAsync(
        string text,
        bool sendImmediately,
        bool allowWhenDisabled,
        bool force,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeChatboxText(text);
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("VRChat Chatbox 消息不能为空。");
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IPEndPoint endpoint;
            lock (_configurationGate)
            {
                if (!_enabled && !allowWhenDisabled)
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                if (!force
                    && string.Equals(_lastSentText, normalized, StringComparison.Ordinal)
                    && now - _lastSentAt < MinimumSendInterval)
                {
                    return;
                }

                endpoint = _endpoint;
            }

            var delay = _nextSendAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            var packet = EncodeChatboxInput(normalized, sendImmediately);
            await _udpClient.SendAsync(packet, endpoint, cancellationToken).ConfigureAwait(false);
            _nextSendAt = DateTimeOffset.UtcNow + MinimumSendInterval;
            lock (_configurationGate)
            {
                _lastSentText = normalized;
                _lastSentAt = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }
}
