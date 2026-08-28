namespace VoxLink.Models;

/// <summary>本地小模型能力类别。</summary>
public enum LocalModelCategory
{
    Asr,
    Translation,
    Tts
}

/// <summary>
/// 支持级别：Stable 可一键部署并进入管线；Experimental 可部署但带有许可证/质量提醒；
/// CatalogOnly 仅作调研展示，不提供一键部署。
/// </summary>
public enum LocalModelSupportLevel
{
    Stable,
    Experimental,
    CatalogOnly
}

/// <summary>模型运行所依赖的本地运行时。</summary>
public enum LocalModelRuntimeKind
{
    None,
    WhisperCpp,
    LlamaCppGguf,
    SherpaOnnxSenseVoice,
    SherpaOnnxFireRedAsr2Ctc,
    SherpaOnnxKokoro,
    ManagedPython,
    ManagedWslCuda
}

/// <summary>安装形态：单文件、压缩包或复用现有 Whisper ggml 目录。</summary>
public enum LocalModelInstallKind
{
    SingleFile,
    ManifestFiles,
    Archive,
    WhisperGgml
}

/// <summary>
/// 模型的单个可校验工件。RelativePath 是相对模型安装目录的安全相对路径，
/// 下载与安装状态都以磁盘上该文件的大小与 SHA-256 为准。
/// </summary>
public sealed record LocalModelArtifact(
    string RelativePath,
    long ExpectedSize,
    string Sha256,
    string PrimaryUrl,
    string? MirrorUrl);

/// <summary>压缩包工件（如 sherpa-onnx 发布的 tar.bz2 模型包）。</summary>
public sealed record LocalModelArchiveSource(
    string Url,
    string? MirrorUrl,
    long ExpectedSize,
    string Sha256,
    string ExpectedRootDirectory);

/// <summary>本地模型目录条目：调研结论 + 可部署工件信息。</summary>
public sealed record LocalModelDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required LocalModelCategory Category { get; init; }
    public required LocalModelSupportLevel SupportLevel { get; init; }
    public required LocalModelRuntimeKind Runtime { get; init; }
    public required LocalModelInstallKind InstallKind { get; init; }

    /// <summary>参数规模描述，如「约 1B（Q4_K_M）」。</summary>
    public required string Parameters { get; init; }

    /// <summary>
    /// 显式声明的参数规模（十亿参数）。目录强制所有条目 ≤ 2B，
    /// 测试以此字段断言，而不是解析 <see cref="Parameters"/> 文本。
    /// </summary>
    public required double NumericParameterBillions { get; init; }

    public required string License { get; init; }
    public required string Languages { get; init; }

    /// <summary>运行要求描述，如「CPU 推理，建议内存 ≥ 4 GB」。</summary>
    public required string Requirements { get; init; }

    /// <summary>模型来源页（Hugging Face / GitHub），用于「了解更多」。</summary>
    public required string SourceUrl { get; init; }

    public required string Description { get; init; }

    /// <summary>CatalogOnly 条目不可一键部署的原因。</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>需要额外准备的应用托管运行环境；null 表示使用随应用发布的原生运行时。</summary>
    public string? RuntimeProfileId { get; init; }

    /// <summary>安装前必须明确接受的固定许可证 ID；null 表示无需额外确认。</summary>
    public string? LicenseAgreementId { get; init; }

    /// <summary>启用前是否必须选择已授权的加密声音资料。</summary>
    public bool RequiresVoiceProfile { get; init; }

    /// <summary>仅对经过固定清单审查的模型允许单工件超过默认 4 GiB。</summary>
    public bool AllowsLargeArtifacts { get; init; }

    /// <summary>安装前要求的可用磁盘空间；0 时按下载量的两倍计算。</summary>
    public long RequiredFreeSpaceBytes { get; init; }
    /// <summary>需要下载到磁盘并校验的工件；安装状态以这些文件为准。</summary>
    public IReadOnlyList<LocalModelArtifact> Artifacts { get; init; } = [];

    /// <summary>压缩包下载源；存在时安装流程为下载 → 校验 → 解压 → 校验工件。</summary>
    public LocalModelArchiveSource? Archive { get; init; }

    /// <summary>WhisperGgml 条目对应的现有 Whisper 模型名（base/large-v3-turbo）；tiny/small 已不再提供。</summary>
    public string? WhisperModelName { get; init; }

    /// <summary>
    /// 显式声明的下载总量（字节）。WhisperGgml 条目复用现有安装器路径，
    /// 没有工件清单，用该字段展示下载体量；其余条目按工件/压缩包汇总。
    /// </summary>
    public long? DeclaredDownloadBytes { get; init; }

    /// <summary>下载总量（字节），用于界面展示与上限校验。</summary>
    public long DownloadBytes => DeclaredDownloadBytes
        ?? (Archive is not null
            ? Archive.ExpectedSize
            : Artifacts.Sum(artifact => artifact.ExpectedSize));

    /// <summary>仅 Stable / Experimental 条目允许一键部署。</summary>
    public bool IsInstallable => SupportLevel != LocalModelSupportLevel.CatalogOnly;
}

/// <summary>磁盘安装状态（每次以磁盘校验为准，不持久化）。</summary>
public enum LocalModelInstallState
{
    NotInstalled,
    Partial,
    Installed
}

/// <summary>
/// 本地模型安装/下载进度。EngineHost 以 modelProgress 事件转发，
/// 携带 modelId 与 category 以便界面区分不同模型；旧 ASR/说话人模型
/// 进度事件的 modelId 为 null，保持向后兼容。
/// </summary>
public sealed record LocalModelProgressEventArgs(
    string ModelId,
    LocalModelCategory Category,
    string Status,
    double? Progress = null);
