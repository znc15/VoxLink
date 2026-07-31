using System.Text.Json;

namespace VoxLink.UI.Core.Models;

internal static class JsonValue
{
    public static string String(JsonElement element, string name, string fallback = "") =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;

    public static bool Boolean(JsonElement element, string name, bool fallback = false) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property)
        && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : fallback;
}
