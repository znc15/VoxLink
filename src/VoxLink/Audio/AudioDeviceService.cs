using NAudio.CoreAudioApi;
using VoxLink.Models;

namespace VoxLink.Audio;

public sealed class AudioDeviceService
{
    public IReadOnlyList<AudioDeviceInfo> GetCaptureDevices() => GetDevices(DataFlow.Capture);

    public IReadOnlyList<AudioDeviceInfo> GetRenderDevices() => GetDevices(DataFlow.Render);

    private static IReadOnlyList<AudioDeviceInfo> GetDevices(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        string defaultId;
        if (enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            defaultId = defaultDevice.ID;
        }
        else
        {
            defaultId = string.Empty;
        }

        var results = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                results.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
            }
        }

        return results
            .OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
