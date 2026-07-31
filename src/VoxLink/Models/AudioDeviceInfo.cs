namespace VoxLink.Models;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault = false)
{
    public override string ToString() => IsDefault ? $"{Name}（默认）" : Name;
}
