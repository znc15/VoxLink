using VoxLink.Services;
using Xunit;

namespace VoxLink.Tests.Services;

public sealed class SteamVrOverlayHostTests
{
    [Fact]
    public void ShowTest_WhenSteamVrIsNotRunningReturnsSafeStatus()
    {
        string? status = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var host = new SteamVrOverlayHost(() => false);
                status = host.ShowTest();
            }
            catch (Exception exception)
            {
                error = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "SteamVR overlay test thread timed out.");
        Assert.Null(error);
        Assert.Equal("SteamVR 未运行", status);
    }
}
