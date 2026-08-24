namespace VoxLink.UI.Core.Models;

/// <summary>Engine 本地模型目录协议中的稳定 ID。</summary>
public static class LocalModelIds
{
    public const string WhisperTiny = "whisper-tiny";
    public const string WhisperBase = "whisper-base";
    public const string WhisperSmall = "whisper-small";
    public const string WhisperLargeV3Turbo = "whisper-large-v3-turbo";
    public const string MiniCpm51BGguf = "minicpm5-1b-gguf";
    public const string HyMt15Gguf = "hy-mt15-18b-gguf";
    public const string SenseVoiceSmall = "sensevoice-small";
    public const string Kokoro82M = "kokoro-82m";

    public static string WhisperId(string? modelName) => modelName?.Trim().ToLowerInvariant() switch
    {
        "base" => WhisperBase,
        "small" => WhisperSmall,
        "large-v3-turbo" => WhisperLargeV3Turbo,
        _ => WhisperTiny
    };

    public static string? WhisperName(string? modelId) => modelId switch
    {
        WhisperTiny => "tiny",
        WhisperBase => "base",
        WhisperSmall => "small",
        WhisperLargeV3Turbo => "large-v3-turbo",
        _ => null
    };
}
