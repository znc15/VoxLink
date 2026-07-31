using System.Text.Json;

namespace VoxLink.UI.Core.Models;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault)
{
    public string Label => IsDefault ? $"{Name}（默认）" : Name;

    public static AudioDeviceInfo FromJson(JsonElement json) => new(
        JsonValue.String(json, "id"),
        JsonValue.String(json, "name", "未知设备"),
        JsonValue.Boolean(json, "isDefault"));
}
