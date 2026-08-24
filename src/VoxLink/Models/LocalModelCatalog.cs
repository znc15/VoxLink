using System.Linq;

namespace VoxLink.Models;

/// <summary>本地模型目录条目的稳定 ID。</summary>
public static class LocalModelIds
{
    public const string WhisperTiny = "whisper-tiny";
    public const string WhisperBase = "whisper-base";
    public const string WhisperSmall = "whisper-small";
    public const string WhisperLargeV3Turbo = "whisper-large-v3-turbo";
    public const string MiniCpm51BGguf = "minicpm5-1b-gguf";
    public const string HyMt15Gguf = "hy-mt15-18b-gguf";
    public const string Kokoro82M = "kokoro-82m";
    public const string SenseVoiceSmall = "sensevoice-small";

    // 以下 ID 已从公开目录移除，仅供保留的应用托管运行时代码
    // （MOSS ASR / dots.tts / Qwen3-TTS 宿主适配器）引用；
    // 产品界面不再提供这些模型的安装或选择入口。
    public const string DotsTts = "dots-tts";
    public const string MossTranscribeDiarize = "moss-transcribe-diarize";
    public const string Qwen3Tts17B = "qwen3-tts-1.7b";
}

/// <summary>
/// 所有可安装条目都固定下载 revision、字节数与 SHA-256，全部使用随应用发布或
/// 隔离的 Windows 原生运行时（whisper.net / LLamaSharp / sherpa-onnx），
/// 不依赖应用托管 Python 或 WSL；安装状态始终以磁盘工件校验结果为准。
/// </summary>
public static partial class LocalModelCatalog
{
    private static IReadOnlyList<LocalModelArtifact> KokoroArtifacts { get; } =
    [
        ArchiveArtifact("model.int8.onnx", 114_299_010, "bda15858163726a492d02a9a727bc263551b86ac77f90812c4b30ff41d380e26"),
        ArchiveArtifact("voices.bin", 53_790_720, "e64a5a581d8c2a350d848f51c3121657cd83aa07ed6109172177345874a7244c"),
        ArchiveArtifact("tokens.txt", 1_111, "931ab2df2400cd65d580a22402024c2347ced8ae9ea300e545144b1aacc48e14"),
        ArchiveArtifact("lexicon-us-en.txt", 5_956_885, "7daaab53a181be9885b853a8582bf1838186317e5dadacbcef9c426d6fa0da14"),
        ArchiveArtifact("lexicon-gb-en.txt", 6_366_635, "c4cbb37316f62210dff52718a7afcaae24f50c032cc75ab47ae67b831d1049e7"),
        ArchiveArtifact("lexicon-zh.txt", 2_119_465, "11111d8cd695fba2ace1367a1d0a708b586e6ef5c1f9be91da5d7eef129b651c"),
        ArchiveArtifact("date-zh.fst", 59_154, "eb8aa079ae3cb81d8f4404992f39d61a0cb990947512b5b8d1e54d1f6980e718"),
        ArchiveArtifact("number-zh.fst", 64_482, "743f402181fcfebf76cc2f0546b71fa26476e626fbe4e460fb7b4c3a7a8bd5bd"),
        ArchiveArtifact("phone-zh.fst", 88_630, "1ac2b6fa56b1442320c4de7db08353bab8963a2b57f365eebcdd3a2d3562f8d7"),
        ArchiveArtifact("dict/jieba.dict.utf8", 5_071_204, "3043b77068e09c9904f27cad82f12b6ebe9dbdb5aeff3b25e45ab7f9c1122b55"),
        ArchiveArtifact("espeak-ng-data/phondata", 550_424, "4e0288957874029a8c3c9f41a8f517ad4bf18127046decbdd4b9d1d6807ce3a3"),
        ArchiveArtifact("espeak-ng-data/phonindex", 39_074, "3ca7b8fa3b42624e4b0f152707e7a39245fce569aa99ea47c055d9e622fcf0c4"),
        ArchiveArtifact("espeak-ng-data/phontab", 55_796, "886f3fa402cb0ba73d483aa8ad000af47a6b7cc06293c75a97913fba68a530f6"),
        ArchiveArtifact("espeak-ng-data/intonations", 2_040, "3f8af65fd3eda9759a10f021d61361c120871f463515229c925995c7f90918cc")
    ];
    public static IReadOnlyList<LocalModelDefinition> All { get; } =
    [
        Whisper(
            LocalModelIds.WhisperTiny,
            "Whisper tiny",
            "tiny",
            "约 39M",
            0.039,
            77_691_713,
            "最小规格，速度最快，适合低配置机器快速出字。"),
        Whisper(
            LocalModelIds.WhisperBase,
            "Whisper base",
            "base",
            "约 74M",
            0.074,
            147_951_465,
            "速度与识别质量的平衡选项，VoxLink 默认本地模型。"),
        Whisper(
            LocalModelIds.WhisperSmall,
            "Whisper small",
            "small",
            "约 244M",
            0.244,
            487_601_967,
            "中文识别质量更好，建议内存 ≥ 4 GB。"),
        Whisper(
            LocalModelIds.WhisperLargeV3Turbo,
            "Whisper large-v3-turbo",
            "large-v3-turbo",
            "约 809M",
            0.809,
            1_624_555_275,
            "large-v3 的解码器精简版，质量接近 large-v3，适合高质量本地识别；建议内存 ≥ 6 GB。"),
        new()
        {
            Id = LocalModelIds.MiniCpm51BGguf,
            Name = "MiniCPM5-1B (GGUF)",
            Category = LocalModelCategory.Translation,
            SupportLevel = LocalModelSupportLevel.Stable,
            Runtime = LocalModelRuntimeKind.LlamaCppGguf,
            InstallKind = LocalModelInstallKind.SingleFile,
            Parameters = "约 1B（Q4_K_M）",
            NumericParameterBillions = 1.0,
            License = "Apache-2.0",
            Languages = "中文/英语为主",
            Requirements = "LLamaSharp/llama.cpp CPU 推理，建议内存 ≥ 4 GB",
            SourceUrl = "https://huggingface.co/openbmb/MiniCPM5-1B-GGUF",
            Description = "通用端侧 1B 指令模型；VoxLink 以受控提示词将其用于离线翻译、译文润色与口语化改写，并不把它宣传为专用翻译模型。",
            Artifacts =
            [
                new LocalModelArtifact(
                    "MiniCPM5-1B-Q4_K_M.gguf",
                    688_065_920,
                    "81b64d05a23b17b34c475f42b3e72fbde62d4b92cc34541f7a8031d0752deafa",
                    "https://huggingface.co/openbmb/MiniCPM5-1B-GGUF/resolve/87007042419d30c1d8f38ef065424ee33870831e/MiniCPM5-1B-Q4_K_M.gguf",
                    "https://hf-mirror.com/openbmb/MiniCPM5-1B-GGUF/resolve/87007042419d30c1d8f38ef065424ee33870831e/MiniCPM5-1B-Q4_K_M.gguf")
            ]
        },
        new()
        {
            Id = LocalModelIds.HyMt15Gguf,
            Name = "腾讯混元翻译 HY-MT1.5-1.8B (GGUF)",
            Category = LocalModelCategory.Translation,
            SupportLevel = LocalModelSupportLevel.Stable,
            Runtime = LocalModelRuntimeKind.LlamaCppGguf,
            InstallKind = LocalModelInstallKind.SingleFile,
            Parameters = "约 1.8B（Q4_K_M）",
            NumericParameterBillions = 1.8,
            License = "Tencent HY Community License（非 OSI；不适用于欧盟、英国和韩国）",
            Languages = "33 种语言互译 + 5 种方言变体（含粤语、繁体中文、藏语等）",
            Requirements = "LLamaSharp/llama.cpp CPU 推理（GGUF Q4_K_M），8GB 内存起步",
            SourceUrl = "https://huggingface.co/tencent/HY-MT1.5-1.8B-GGUF",
            Description = "腾讯混元翻译 1.5 系列 1.8B 规格的 GGUF 量化版本，纯 Windows 原生 CPU 推理；安装前必须确认许可证和适用地区。",
            Artifacts =
            [
                new LocalModelArtifact(
                    "HY-MT1.5-1.8B-Q4_K_M.gguf",
                    1_133_080_512,
                    "4383ac0c3c8e476de98ff979c2a3f069f8c4fb385e7860cf2d28da896cc477c7",
                    "https://huggingface.co/tencent/HY-MT1.5-1.8B-GGUF/resolve/265b2e615a7dc9b06c435dc878829ad99a512ba2/HY-MT1.5-1.8B-Q4_K_M.gguf",
                    "https://hf-mirror.com/tencent/HY-MT1.5-1.8B-GGUF/resolve/265b2e615a7dc9b06c435dc878829ad99a512ba2/HY-MT1.5-1.8B-Q4_K_M.gguf")
            ]
        },
        new()
        {
            Id = LocalModelIds.Kokoro82M,
            Name = "Kokoro-82M",
            Category = LocalModelCategory.Tts,
            SupportLevel = LocalModelSupportLevel.Stable,
            Runtime = LocalModelRuntimeKind.SherpaOnnxKokoro,
            InstallKind = LocalModelInstallKind.Archive,
            Parameters = "约 82M",
            NumericParameterBillions = 0.082,
            License = "Apache-2.0",
            Languages = "中文/英语（103 个内置 speaker）",
            Requirements = "sherpa-onnx CPU 推理，建议内存 ≥ 2 GB",
            SourceUrl = "https://huggingface.co/hexgrad/Kokoro-82M-v1.1-zh",
            Description = "轻量多语 TTS，通过 sherpa-onnx 在 Windows CPU 上离线生成 24 kHz 语音。",
            Archive = new LocalModelArchiveSource(
                "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-int8-multi-lang-v1_1.tar.bz2",
                null,
                147_031_220,
                "a1e94694776049035c4f2c6529f003aaece993c76aae9a78995831c3c4dcafc6",
                "kokoro-int8-multi-lang-v1_1"),
            Artifacts = KokoroArtifacts
        },
        new()
        {
            Id = LocalModelIds.SenseVoiceSmall,
            Name = "SenseVoice-Small",
            Category = LocalModelCategory.Asr,
            SupportLevel = LocalModelSupportLevel.Stable,
            Runtime = LocalModelRuntimeKind.SherpaOnnxSenseVoice,
            InstallKind = LocalModelInstallKind.Archive,
            Parameters = "约 234M（int8）",
            NumericParameterBillions = 0.234,
            License = "MIT",
            Languages = "中文/英语/日语/韩语/粤语，附带情感与音频事件识别",
            Requirements = "sherpa-onnx CPU 推理，建议内存 ≥ 2 GB",
            SourceUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models",
            Description = "FunAudioLLM SenseVoice-Small 的 sherpa-onnx int8 导出，在 Windows CPU 上执行低延迟离线识别。",
            Archive = new LocalModelArchiveSource(
                "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09.tar.bz2",
                null,
                165_783_878,
                "7305f7905bfcf77fa0b39388a313f3da35c68d971661a65475b56fb2162c8e63",
                "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09"),
            Artifacts =
            [
                ArchiveArtifact("model.int8.onnx", 237_115_547, "12ca1a2ae7ecf3e0019ef2822307ee0b5cadc9196569e379b4c4026f8205276d"),
                ArchiveArtifact("tokens.txt", 315_894, "f449eb28dc567533d7fa59be34e2abca8784f771850c78a47fb731a31429a1dc")
            ]
        }
    ];

    /// <summary>按 ID 查找目录条目（区分大小写）。</summary>
    public static bool TryGet(string? modelId, out LocalModelDefinition definition)
    {
        var found = All.FirstOrDefault(item =>
            string.Equals(item.Id, modelId, StringComparison.Ordinal));
        definition = found!;
        return found is not null;
    }

    private static LocalModelDefinition Whisper(
        string id,
        string name,
        string whisperModelName,
        string parameters,
        double numericParameterBillions,
        long downloadBytes,
        string description) => new()
    {
        Id = id,
        Name = name,
        Category = LocalModelCategory.Asr,
        SupportLevel = LocalModelSupportLevel.Stable,
        Runtime = LocalModelRuntimeKind.WhisperCpp,
        InstallKind = LocalModelInstallKind.WhisperGgml,
        Parameters = parameters,
        NumericParameterBillions = numericParameterBillions,
        License = "MIT",
        Languages = "多语言（约 100 种，中文/英语为主）",
        Requirements = "Whisper.cpp CPU 推理，VoxLink 现有本地识别链路",
        SourceUrl = "https://github.com/ggml-org/whisper.cpp",
        Description = description,
        WhisperModelName = whisperModelName,
        DeclaredDownloadBytes = downloadBytes
    };

    private static LocalModelArtifact ArchiveArtifact(string path, long size, string sha256) =>
        new(path, size, sha256, string.Empty, null);
}
