namespace VoxLink.UI.Core.Services;

/// <summary>应用版本与 GitHub 发布源配置。发布新版本时保持与仓库一致。</summary>
public static class ReleaseMetadata
{
    /// <summary>GitHub 仓库所有者。</summary>
    public const string RepositoryOwner = "znc15";

    /// <summary>GitHub 仓库名称。</summary>
    public const string RepositoryName = "VoxLink";

    /// <summary>最新版检查端点（GitHub Releases API）。</summary>
    public const string UpdateFeedUrl =
        "https://api.github.com/repos/znc15/VoxLink/releases/latest";

    /// <summary>发布下载页。</summary>
    public const string ReleasesPageUrl =
        "https://github.com/znc15/VoxLink/releases";
}
