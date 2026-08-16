using System.IO;
using VoxLink.Models;

namespace VoxLink.Services;

/// <summary>
/// 现有 Whisper ggml 模型安装能力的抽象，供 <see cref="LocalModelManager"/>
/// 复用与测试替换。生产实现委托给 <see cref="WhisperSpeechRecognizer.PrepareAsync"/>：
/// 固定 revision 的 HTTPS 下载源、已知大小与 SHA-256 校验、临时文件原子替换。
/// </summary>
public interface IWhisperModelInstaller
{
    /// <summary>下载/校验进度（与现有 modelProgress 事件语义一致）。</summary>
    event EventHandler<ModelProgressEventArgs>? ModelProgress;

    /// <summary>确保指定 Whisper 模型（tiny/base/small）已下载并通过校验。</summary>
    Task PrepareAsync(string modelName, CancellationToken cancellationToken = default);

    /// <summary>以磁盘文件（大小 + SHA-256）为准的安装状态，不持久化。</summary>
    LocalModelInstallState GetInstallState(string modelName);

    /// <summary>删除已安装的模型文件（含残留的 .download 临时文件），返回是否删除了文件。</summary>
    bool TryRemoveModel(string modelName);
}

/// <summary>
/// 生产实现：为每次安装创建独立的 <see cref="WhisperSpeechRecognizer"/>，
/// 完成后释放（下载的模型文件保留在现有 Whisper 目录，供会话识别复用）。
/// </summary>
internal sealed class WhisperModelInstallerAdapter : IWhisperModelInstaller
{
    private readonly string? _modelDirectory;

    public WhisperModelInstallerAdapter(string? modelDirectory = null)
    {
        _modelDirectory = modelDirectory;
    }

    public event EventHandler<ModelProgressEventArgs>? ModelProgress;

    public async Task PrepareAsync(string modelName, CancellationToken cancellationToken = default)
    {
        var recognizer = new WhisperSpeechRecognizer(_modelDirectory);
        recognizer.ModelProgress += OnModelProgress;
        try
        {
            await recognizer.PrepareAsync(modelName, cancellationToken);
        }
        finally
        {
            recognizer.ModelProgress -= OnModelProgress;
            await recognizer.DisposeAsync();
        }
    }

    public LocalModelInstallState GetInstallState(string modelName)
    {
        var modelPath = WhisperSpeechRecognizer.GetModelPath(modelName, _modelDirectory);
        if (!File.Exists(modelPath))
        {
            return HasTemporaryFile(modelPath)
                ? LocalModelInstallState.Partial
                : LocalModelInstallState.NotInstalled;
        }

        var model = WhisperSpeechRecognizer.GetModelInfo(modelName);
        return IsFileVerified(modelPath, model)
            ? LocalModelInstallState.Installed
            : LocalModelInstallState.Partial;
    }

    public bool TryRemoveModel(string modelName)
    {
        var modelPath = WhisperSpeechRecognizer.GetModelPath(modelName, _modelDirectory);
        var removed = TryDeleteFile(modelPath);
        removed |= TryDeleteFile(modelPath + ".download");
        return removed;
    }

    private static bool HasTemporaryFile(string modelPath) =>
        File.Exists(modelPath + ".download");

    private static bool IsFileVerified(
        string modelPath,
        WhisperSpeechRecognizer.ModelInfo model)
    {
        try
        {
            if (new FileInfo(modelPath).Length != model.Size)
            {
                return false;
            }

            using var stream = new FileStream(
                modelPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
            return hash.Equals(model.Sha256, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void OnModelProgress(object? sender, ModelProgressEventArgs eventArgs) =>
        ModelProgress?.Invoke(this, eventArgs);
}
