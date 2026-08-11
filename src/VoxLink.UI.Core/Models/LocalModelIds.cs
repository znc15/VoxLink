namespace VoxLink.UI.Core.Models;

/// <summary>Engine 本地模型目录协议中的稳定 ID。</summary>
public static class LocalModelIds
{
    public const string WhisperTiny = "whisper-tiny";
    public const string WhisperBase = "whisper-base";
    public const string WhisperSmall = "whisper-small";
    public const string MiniCpm51BGguf = "minicpm5-1b-gguf";
    public const string HyMt1518B = "hy-mt1.5-1.8b";
    public const string M2M100418M = "m2m100-418m";
    public const string Small100 = "small-100";
    public const string MossTranscribeDiarize = "moss-transcribe-diarize";
    public const string Kokoro82M = "kokoro-82m";

    public static string WhisperId(string? modelName) => modelName?.Trim().ToLowerInvariant() switch
    {
        "base" => WhisperBase,
        "small" => WhisperSmall,
        _ => WhisperTiny
    };

    public static string? WhisperName(string? modelId) => modelId switch
    {
        WhisperTiny => "tiny",
        WhisperBase => "base",
        WhisperSmall => "small",
        _ => null
    };
}
