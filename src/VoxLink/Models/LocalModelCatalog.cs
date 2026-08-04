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
    public const string HyMt1518B = "hy-mt1.5-1.8b";
    public const string DotsTts = "dots-tts";
    public const string MossTranscribeDiarize = "moss-transcribe-diarize";
    public const string Kokoro82M = "kokoro-82m";
    public const string CosyVoice205B = "cosyvoice2-0.5b";
    public const string M2M100418M = "m2m100-418m";
    public const string Small100 = "small-100";
    public const string SenseVoiceSmall = "sensevoice-small";
    public const string Qwen3Tts17B = "qwen3-tts-1.7b";
}

/// <summary>
/// 本地模型调研目录（≤2B）。Stable 条目均固定下载 revision、字节数与 SHA-256，
/// 并通过 Windows 原生运行时接入；CatalogOnly 条目只展示核验后的兼容性信息。
/// 安装状态始终以磁盘工件校验结果为准。
/// </summary>
public static class LocalModelCatalog
{
    /// <summary>下一里程碑提示（CatalogOnly 条目的不可用原因统一引用）。</summary>
    private const string NextMilestone = "计划在下一里程碑接入对应运行时后提供一键部署";

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
        new()
        {
            Id = LocalModelIds.WhisperLargeV3Turbo,
            Name = "Whisper large-v3-turbo",
            Category = LocalModelCategory.Asr,
            SupportLevel = LocalModelSupportLevel.CatalogOnly,
            Runtime = LocalModelRuntimeKind.WhisperCpp,
            InstallKind = LocalModelInstallKind.WhisperGgml,
            Parameters = "约 809M",
            NumericParameterBillions = 0.809,
            License = "MIT",
            Languages = "多语言（约 100 种，中文/英语为主）",
            Requirements = "CPU 推理，建议内存 ≥ 6 GB",
            SourceUrl = "https://huggingface.co/openai/whisper-large-v3-turbo",
            Description = "large-v3 的解码器精简版，质量接近 large-v3 而速度显著提升，是本地 ASR 的高质量候选。",
            UnavailableReason = "当前本地 Whisper 集成仅内置 tiny/base/small 的固定源下载与 SHA-256 校验，large-v3-turbo 安装器待后续里程碑接入。",
            WhisperModelName = null
        },
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
            Id = LocalModelIds.HyMt1518B,
            Name = "腾讯混元翻译 HY-MT1.5-1.8B",
            Category = LocalModelCategory.Translation,
            SupportLevel = LocalModelSupportLevel.CatalogOnly,
            Runtime = LocalModelRuntimeKind.LlamaCppGguf,
            InstallKind = LocalModelInstallKind.Archive,
            Parameters = "约 1.8B（官方口径）",
            NumericParameterBillions = 1.8,
            License = "Tencent HY Community License（非 OSI；不适用于欧盟、英国和韩国）",
            Languages = "33 种语言互译 + 5 种方言变体（含粤语、繁体中文、藏语等）",
            Requirements = "官方 safetensors/Python 推理；原生 Windows 无受支持的一键运行时",
            SourceUrl = "https://huggingface.co/tencent/HY-MT1.5-1.8B",
            Description = "腾讯混元翻译 1.5 系列的 1.8B 规格，支持术语干预与混合语言场景；许可证含明确地域限制。",
            UnavailableReason = "Tencent HY Community License 有地域限制，且官方权重需要 Python/transformers；本项目不提供虚假的原生 Windows 安装入口。"
        },
        new()
        {
            Id = LocalModelIds.DotsTts,
            Name = "dots.tts",
            Category = LocalModelCategory.Tts,
            SupportLevel = LocalModelSupportLevel.CatalogOnly,
            Runtime = LocalModelRuntimeKind.None,
            InstallKind = LocalModelInstallKind.Archive,
            Parameters = "约 2B（官方口径）",
            NumericParameterBillions = 2.0,
            License = "Apache-2.0",
            Languages = "中文/英语，支持 3 秒参考音频克隆音色",
            Requirements = "官方 Python/PyTorch 推理；NVIDIA GPU 显存约 5.3–10.5 GB（随音频长度增长）",
            SourceUrl = "https://huggingface.co/rednote-hilab/dots.tts-base",
            Description = "小红书 rednote-hilab 的 2B 端到端自回归 TTS：语义编码器 + LLM + 流匹配声学头，48 kHz AudioVAE，支持授权参考音频的声音克隆。",
            UnavailableReason = "官方链路依赖 Python/PyTorch 与 NVIDIA GPU，未提供可供 VoxLink 原生 Windows CPU 使用的 ONNX/sherpa 工件，因此仅展示兼容性信息。"
        },
        new()
        {
            Id = LocalModelIds.MossTranscribeDiarize,
            Name = "MOSS-Transcribe-Diarize",
            Category = LocalModelCategory.Asr,
            SupportLevel = LocalModelSupportLevel.CatalogOnly,
            Runtime = LocalModelRuntimeKind.None,
            InstallKind = LocalModelInstallKind.Archive,
            Parameters = "约 0.9B",
            NumericParameterBillions = 0.9,
            License = "Apache-2.0",
            Languages = "50+ 种语言（中文/英语重点），输出说话人标签与时间戳",
            Requirements = "官方推荐 SGLang Omni（CUDA 13）或 vLLM（CUDA 12/13）服务",
            SourceUrl = "https://huggingface.co/OpenMOSS-Team/MOSS-Transcribe-Diarize",
            Description = "OpenMOSS 的 0.9B 长音频模型，可同时输出转写、说话人分离、时间戳与声学事件。",
            UnavailableReason = "官方部署依赖 Python 与 CUDA 服务栈，当前无适合 VoxLink 无 Python 原生 Windows 侧车的受支持运行时，因此仅展示目录信息。"
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
            Id = LocalModelIds.CosyVoice205B,
            Name = "CosyVoice2-0.5B",
            Category = LocalModelCategory.Tts,
            SupportLevel = LocalModelSupportLevel.CatalogOnly,
            Runtime = LocalModelRuntimeKind.None,
            InstallKind = LocalModelInstallKind.Archive,
            Parameters = "约 0.5B",
            NumericParameterBillions = 0.5,
            License = "Apache-2.0",
            Languages = "中文/英语/日语/韩语/粤语，支持零样本音色克隆与指令控制",
            Requirements = "PyTorch/transformers 推理，建议内存 ≥ 6 GB",
            SourceUrl = "https://huggingface.co/FunAudioLLM/CosyVoice2-0.5B",
            Description = "阿里通义 FunAudioLLM 的流式 TTS 模型，中英日韩粤多语种、3 秒零样本克隆，适合高质量的本地译文朗读。",
            UnavailableReason = "依赖 PyTorch 推理链路且为多文件权重，下载校验与运行时均未接入，" + NextMilestone + "。"
        },
        new()
        {
            Id = LocalModelIds.M2M100418M,
            Name = "M2M-100 418M",
            Category = LocalModelCategory.Translation,
            SupportLevel = LocalModelSupportLevel.CatalogOnly,
            Runtime = LocalModelRuntimeKind.None,
            InstallKind = LocalModelInstallKind.Archive,
            Parameters = "约 418M",
            NumericParameterBillions = 0.418,
            License = "MIT",
            Languages = "100 种语言直接互译（无需 pivot 语言）",
            Requirements = "fairseq/transformers CPU 推理，建议内存 ≥ 4 GB",
            SourceUrl = "https://huggingface.co/facebook/m2m100_418M",
            Description = "Facebook AI 的 massively multilingual 翻译基线模型，覆盖 100 种语言直译，适合作为小语种离线兜底翻译。",
            UnavailableReason = "需要 transformers/fairseq 运行时与多文件权重校验清单，尚未接入，" + NextMilestone + "。"
        },
        new()
        {
            Id = LocalModelIds.Small100,
            Name = "SMaLL-100",
            Category = LocalModelCategory.Translation,
            SupportLevel = LocalModelSupportLevel.CatalogOnly,
            Runtime = LocalModelRuntimeKind.None,
            InstallKind = LocalModelInstallKind.Archive,
            Parameters = "约 0.33B",
            NumericParameterBillions = 0.33,
            License = "MIT",
            Languages = "100+ 种语言（M2M-100 蒸馏，低资源语言优化）",
            Requirements = "transformers CPU 推理，建议内存 ≥ 4 GB",
            SourceUrl = "https://huggingface.co/alirezamsh/small100",
            Description = "M2M-100 的蒸馏版本（EMNLP 2022），质量接近 M2M-100 而体积更小、推理更快约 4 倍，适合低配置机器的离线多语翻译。",
            UnavailableReason = "需要 transformers 运行时与多文件权重校验清单，尚未接入，" + NextMilestone + "。"
        },
        new()
        {
            Id = LocalModelIds.SenseVoiceSmall,
            Name = "SenseVoice-Small",
            Category = LocalModelCategory.Asr,
            SupportLevel = LocalModelSupportLevel.CatalogOnly,
            Runtime = LocalModelRuntimeKind.None,
            InstallKind = LocalModelInstallKind.Archive,
            Parameters = "约 234M（与 Whisper-Small 同量级）",
            NumericParameterBillions = 0.234,
            License = "MIT",
            Languages = "中文/英语/日语/韩语/粤语，附带情感与音频事件识别",
            Requirements = "FunASR/ONNX 推理，CPU 即可",
            SourceUrl = "https://huggingface.co/FunAudioLLM/SenseVoiceSmall",
            Description = "阿里 FunAudioLLM 的非自回归多语 ASR，中文字错率优于同量级 Whisper 且推理延迟极低，是本地中文 ASR 的高潜力候选。",
            UnavailableReason = "需要 FunASR/ONNX Runtime 接入与多文件权重校验清单，尚未接入，" + NextMilestone + "。"
        },
        new()
        {
            Id = LocalModelIds.Qwen3Tts17B,
            Name = "Qwen3-TTS 1.7B (Base)",
            Category = LocalModelCategory.Tts,
            SupportLevel = LocalModelSupportLevel.CatalogOnly,
            Runtime = LocalModelRuntimeKind.None,
            InstallKind = LocalModelInstallKind.Archive,
            Parameters = "约 1.7B",
            NumericParameterBillions = 1.7,
            License = "Apache-2.0",
            Languages = "中文/英语/日语/韩语等 10 种语言",
            Requirements = "PyTorch/transformers 推理，建议内存 ≥ 8 GB",
            SourceUrl = "https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-Base",
            Description = "通义千问 Qwen3-TTS 系列 1.7B 规格基础权重（12 Hz 音频 tokenizer），另有 CustomVoice/VoiceDesign 变体，适合高质量多语本地朗读。",
            UnavailableReason = "依赖 PyTorch 推理链路且为多文件权重，下载校验与运行时均未接入，" + NextMilestone + "。"
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
