using System.Text.Json;

namespace VoxLink.UI.Core.Models;

public enum ConversationDirection
{
    Outbound,
    Inbound,
    Typed
}

public sealed record ConversationMessage(
    ConversationDirection Direction,
    string SourceText,
    string TranslatedText,
    DateTimeOffset Timestamp)
{
    public string SecondaryTranslatedText { get; init; } = string.Empty;

    public string? SpeakerId { get; init; }

    public string? SpeakerLabel { get; init; }

    public string? UtteranceId { get; init; }
    public bool IsFinal { get; init; } = true;

    public bool TranscriptionOnly { get; init; }

    public string DirectionLabel => Direction switch
    {
        ConversationDirection.Outbound => "我的语音",
        ConversationDirection.Inbound => "对方语音",
        _ => "键入内容"
    };

    public string HeaderLabel
    {
        get
        {
            var parts = new List<string> { DirectionLabel };
            if (!string.IsNullOrWhiteSpace(SpeakerLabel))
            {
                parts.Add(SpeakerLabel);
            }

            if (!IsFinal)
            {
                parts.Add("实时");
            }

            return string.Join(" · ", parts);
        }
    }

    public string PrimaryDisplayText => TranscriptionOnly ? SourceText : TranslatedText;

    public string SecondaryDisplayText => TranscriptionOnly ? string.Empty : SecondaryTranslatedText;

    public string SourceDisplayText => TranscriptionOnly ? string.Empty : SourceText;

    public bool CanSpeak => IsFinal && !TranscriptionOnly && !string.IsNullOrWhiteSpace(TranslatedText);
    public string DirectionGlyph => Direction switch
    {
        ConversationDirection.Outbound => "\uE720",
        ConversationDirection.Inbound => "\uE7F6",
        _ => "\uE70F"
    };

    public string TimeLabel => Timestamp.ToLocalTime().ToString("HH:mm");

    public static ConversationMessage FromJson(JsonElement json)
    {
        var direction = JsonValue.String(json, "direction") switch
        {
            "outbound" => ConversationDirection.Outbound,
            "inbound" => ConversationDirection.Inbound,
            _ => ConversationDirection.Typed
        };
        var timestamp = DateTimeOffset.TryParse(JsonValue.String(json, "timestamp"), out var parsed)
            ? parsed
            : DateTimeOffset.Now;
        return new ConversationMessage(
            direction,
            JsonValue.String(json, "sourceText"),
            JsonValue.String(json, "translatedText"),
            timestamp)
        {
            SecondaryTranslatedText = JsonValue.String(json, "secondaryTranslatedText"),
            SpeakerId = OptionalString(json, "speakerId"),
            SpeakerLabel = OptionalString(json, "speakerLabel"),
            UtteranceId = OptionalString(json, "utteranceId"),
            IsFinal = OptionalBool(json, "isFinal", fallback: true),
            TranscriptionOnly = OptionalBool(json, "transcriptionOnly", fallback: false)
        };
    }

    private static string? OptionalString(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object
        && json.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool OptionalBool(JsonElement json, string name, bool fallback) =>
        json.ValueKind == JsonValueKind.Object
        && json.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}
