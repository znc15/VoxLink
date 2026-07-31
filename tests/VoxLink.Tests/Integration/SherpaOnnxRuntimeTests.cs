using System.Runtime.InteropServices;
using Xunit;

namespace VoxLink.Tests.Integration;

public sealed class SherpaOnnxRuntimeTests
{
    [Fact]
    public void WinX64Runtime_LoadsNativeApiWithoutDownloadingModel()
    {
        Assert.True(OperatingSystem.IsWindows());
        Assert.True(Environment.Is64BitProcess);

        var nativeDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "runtimes",
            "win-x64",
            "native");
        var onnxRuntimePath = Path.Combine(nativeDirectory, "onnxruntime.dll");
        var sherpaPath = Path.Combine(nativeDirectory, "sherpa-onnx-c-api.dll");
        Assert.True(File.Exists(onnxRuntimePath), $"Missing {onnxRuntimePath}");
        Assert.True(File.Exists(sherpaPath), $"Missing {sherpaPath}");

        var onnxHandle = NativeLibrary.Load(onnxRuntimePath);
        var sherpaHandle = IntPtr.Zero;
        try
        {
            sherpaHandle = NativeLibrary.Load(sherpaPath);
            var version = ReadUtf8Export(sherpaHandle, "SherpaOnnxGetVersionStr");
            var gitSha = ReadUtf8Export(sherpaHandle, "SherpaOnnxGetGitSha1");

            Assert.Equal("1.13.4", version);
            Assert.StartsWith("1428072", gitSha, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (sherpaHandle != IntPtr.Zero)
            {
                NativeLibrary.Free(sherpaHandle);
            }

            NativeLibrary.Free(onnxHandle);
        }
    }

    private static string ReadUtf8Export(IntPtr library, string name)
    {
        var export = NativeLibrary.GetExport(library, name);
        var method = Marshal.GetDelegateForFunctionPointer<GetStringDelegate>(export);
        return Marshal.PtrToStringUTF8(method()) ?? string.Empty;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetStringDelegate();
}
