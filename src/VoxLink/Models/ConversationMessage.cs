namespace VoxLink.Models;

public enum TranslationDirection
{
    Outbound,
    Inbound,
    Typed
}

public sealed record ConversationMessage(
    TranslationDirection Direction,
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
        TranslationDirection.Outbound => "我的语音",
        TranslationDirection.Inbound => "对方语音",
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
}
