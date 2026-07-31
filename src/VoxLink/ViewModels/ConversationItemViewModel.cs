using VoxLink.Models;

namespace VoxLink.ViewModels;

public sealed class ConversationItemViewModel(ConversationMessage message)
{
    public string DirectionLabel => message.Direction switch
    {
        TranslationDirection.Inbound => "对方",
        TranslationDirection.Typed => "输入",
        _ => "我"
    };

    public string SourceText => message.SourceText;

    public string TranslatedText => message.TranslatedText;

    public string Time => message.Timestamp.ToLocalTime().ToString("HH:mm");

    public TranslationDirection Direction => message.Direction;
}
