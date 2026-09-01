using System.Runtime.InteropServices;
using VoxLink.Audio;

namespace VoxLink.Tests.Audio;

public sealed class WasapiSpeechCaptureTests
{
    private const int DeviceInvalidatedHResult = unchecked((int)0x88890004);

    [Fact]
    public void IsDeviceInvalidated_DetectsDirectComException()
    {
        var exception = new COMException("Audio endpoint was invalidated.", DeviceInvalidatedHResult);

        Assert.True(WasapiSpeechCapture.IsDeviceInvalidated(exception));
    }

    [Fact]
    public void IsDeviceInvalidated_DetectsWrappedComException()
    {
        var exception = new InvalidOperationException(
            "Capture failed.",
            new COMException("Audio endpoint was invalidated.", DeviceInvalidatedHResult));

        Assert.True(WasapiSpeechCapture.IsDeviceInvalidated(exception));
    }

    [Fact]
    public void IsDeviceInvalidated_RejectsOtherAudioClientErrors()
    {
        var exception = new COMException("Audio client failed.", unchecked((int)0x8889000A));

        Assert.False(WasapiSpeechCapture.IsDeviceInvalidated(exception));
    }

    [Fact]
    public void IsDeviceInvalidated_RejectsOrdinaryException()
    {
        Assert.False(WasapiSpeechCapture.IsDeviceInvalidated(new InvalidOperationException("Capture failed.")));
    }
}
