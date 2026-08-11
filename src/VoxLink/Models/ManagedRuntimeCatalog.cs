namespace VoxLink.Models;

public enum ManagedRuntimePlatform
{
    WindowsPython,
    WslCuda
}

public sealed record ManagedRuntimeDefinition(
    string Id,
    ManagedRuntimePlatform Platform,
    string PythonVersion,
    string LockFile,
    string? SourceRepository,
    string? SourceRevision,
    bool RequiresNvidiaGpu,
    long MinimumGpuMemoryBytes);

public sealed record ManagedRuntimeArtifact(
    string FileName,
    long ExpectedSize,
    string Sha256,
    string Url);

/// <summary>
/// 应用托管推理环境的固定供应链清单。模型权重不属于运行环境，仍由
/// <see cref="LocalModelCatalog"/> 和 LocalModelManager 单独下载、校验和租约保护。
/// </summary>
public static class ManagedRuntimeCatalog
{
    public const string WindowsTranslation = "windows-translation-v1";
    public const string WslMoss = "wsl-moss-v1";
    public const string WslDotsTts = "wsl-dots-tts-v1";
    public const string WslCosyVoice2 = "wsl-cosyvoice2-v1";
    public const string WslQwen3Tts = "wsl-qwen3-tts-v1";

    public const string WslDistributionName = "VoxLink-Models";

    public static ManagedRuntimeArtifact WindowsPython { get; } = new(
        "python-3.12.10-embed-amd64.zip",
        11_133_606,
        "4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3",
        "https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip");

    public static ManagedRuntimeArtifact PipWheel { get; } = new(
        "pip-26.1.2-py3-none-any.whl",
        1_813_144,
        "382ff9f685ee3bc25864f820aa50505825f10f5458ffff07e30a6d96e5715cab",
        "https://files.pythonhosted.org/packages/5d/95/6b5cb3461ea5673ba0995989746db58eb18b91b54dbf331e72f569540946/pip-26.1.2-py3-none-any.whl");

    public static ManagedRuntimeArtifact LinuxPython310 { get; } = new(
        "cpython-3.10.18+20250712-x86_64-unknown-linux-gnu-install_only_stripped.tar.gz",
        30_224_168,
        "ba282bc7e494c38c7f5483437fd1108e1d55f0b24effb3eb5b28e03966667d7c",
        "https://github.com/astral-sh/python-build-standalone/releases/download/20250712/cpython-3.10.18%2B20250712-x86_64-unknown-linux-gnu-install_only_stripped.tar.gz");

    public static ManagedRuntimeArtifact LinuxPython312 { get; } = new(
        "cpython-3.12.11+20250712-x86_64-unknown-linux-gnu-install_only_stripped.tar.gz",
        34_429_786,
        "e42c16fe50fda85dad3f5042b6d507476ea8e88c0f039018fef0680038d87c17",
        "https://github.com/astral-sh/python-build-standalone/releases/download/20250712/cpython-3.12.11%2B20250712-x86_64-unknown-linux-gnu-install_only_stripped.tar.gz");

    public static ManagedRuntimeArtifact UbuntuWslImage { get; } = new(
        "ubuntu-24.04.3-wsl-amd64.wsl",
        379_031_026,
        "c74833a55e525b1e99e1541509c566bb3e32bdb53bf27ea3347174364a57f47c",
        "https://releases.ubuntu.com/24.04/ubuntu-24.04.3-wsl-amd64.wsl");

    public static IReadOnlyList<ManagedRuntimeDefinition> All { get; } =
    [
        new(
            WindowsTranslation,
            ManagedRuntimePlatform.WindowsPython,
            "3.12",
            "windows-translation.lock",
            null,
            null,
            RequiresNvidiaGpu: false,
            MinimumGpuMemoryBytes: 0),
        new(
            WslMoss,
            ManagedRuntimePlatform.WslCuda,
            "3.12",
            "wsl-moss.lock",
            "https://github.com/OpenMOSS/MOSS-Transcribe-Diarize.git",
            "0e3d1403fd8f1f1c674e883ece96b9f630794ebe",
            RequiresNvidiaGpu: true,
            MinimumGpuMemoryBytes: 6L * 1024 * 1024 * 1024),
        new(
            WslDotsTts,
            ManagedRuntimePlatform.WslCuda,
            "3.10",
            "wsl-dots-tts.lock",
            "https://github.com/rednote-hilab/dots.tts.git",
            "5ed719e3d36f5a3f6d8037ca9a7009d4fd0520ba",
            RequiresNvidiaGpu: true,
            MinimumGpuMemoryBytes: 11L * 1024 * 1024 * 1024),
        new(
            WslCosyVoice2,
            ManagedRuntimePlatform.WslCuda,
            "3.10",
            "wsl-cosyvoice2.lock",
            "https://github.com/FunAudioLLM/CosyVoice.git",
            "074ca6dc9e80a2f424f1f74b48bdd7d3fea531cc",
            RequiresNvidiaGpu: true,
            MinimumGpuMemoryBytes: 8L * 1024 * 1024 * 1024),
        new(
            WslQwen3Tts,
            ManagedRuntimePlatform.WslCuda,
            "3.12",
            "wsl-qwen3-tts.lock",
            "https://github.com/QwenLM/Qwen3-TTS.git",
            "022e286b98fbec7e1e916cb940cdf532cd9f488e",
            RequiresNvidiaGpu: true,
            MinimumGpuMemoryBytes: 8L * 1024 * 1024 * 1024)
    ];

    public static bool TryGet(string? id, out ManagedRuntimeDefinition definition)
    {
        var found = All.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.Ordinal));
        definition = found!;
        return found is not null;
    }
}
