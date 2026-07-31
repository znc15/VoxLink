using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoxLink.Engine;

internal static class Program
{
    private static readonly object OutputSync = new();
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [STAThread]
    private static async Task<int> Main()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        EngineHost? host = null;
        try
        {
            host = new EngineHost(WriteEvent);
            WriteEvent("ready", new
            {
                processId = Environment.ProcessId,
                protocolVersion = 1
            });

            while (!host.ShouldShutdown)
            {
                var line = await Console.In.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                await HandleLineAsync(host, line);
            }

            return 0;
        }
        catch (Exception exception)
        {
            WriteEvent("fatal", new
            {
                message = host?.Redact(exception.GetBaseException().Message)
                    ?? exception.GetBaseException().Message
            });
            return 1;
        }
        finally
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }
        }
    }

    private static async Task HandleLineAsync(EngineHost host, string line)
    {
        JsonElement id = default;
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("引擎请求必须是 JSON 对象。");
            }

            if (!root.TryGetProperty("id", out var idElement))
            {
                throw new InvalidOperationException("引擎请求缺少 id。");
            }

            id = idElement.Clone();
            if (!root.TryGetProperty("method", out var methodElement)
                || methodElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(methodElement.GetString()))
            {
                throw new InvalidOperationException("引擎请求缺少 method。");
            }

            var parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement.Clone()
                : JsonSerializer.SerializeToElement(new { }, SerializerOptions);
            var result = await host.HandleAsync(
                methodElement.GetString()!,
                parameters,
                SerializerOptions,
                CancellationToken.None);
            Write(new RpcResponse(id, result, null));
        }
        catch (Exception exception)
        {
            var message = host.Redact(exception.GetBaseException().Message);
            if (id.ValueKind == JsonValueKind.Undefined)
            {
                WriteEvent("protocolError", new { message });
                return;
            }

            Write(new RpcResponse(id, null, new RpcError("engine_error", message)));
        }
    }

    private static void WriteEvent(string name, object data) =>
        Write(new RpcEvent(name, data));

    private static void Write(object message)
    {
        var json = JsonSerializer.Serialize(message, SerializerOptions);
        lock (OutputSync)
        {
            Console.Out.WriteLine(json);
            Console.Out.Flush();
        }
    }

    private sealed record RpcResponse(JsonElement Id, object? Result, RpcError? Error);

    private sealed record RpcError(string Code, string Message);

    private sealed record RpcEvent(string Event, object Data);
}
