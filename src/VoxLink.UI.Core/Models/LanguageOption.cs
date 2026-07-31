namespace VoxLink.UI.Core.Models;

public sealed record LanguageOption(string Code, string Culture, string DisplayName)
{
    public string ProviderCode => Code.Equals("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : Code;

    public override string ToString() => DisplayName;

    public static IReadOnlyList<LanguageOption> All { get; } =
    [
        new("zh", "zh-CN", "中文（简体）"),
        new("en", "en-US", "English"),
        new("ja", "ja-JP", "日本語"),
        new("ko", "ko-KR", "한국어"),
        new("es", "es-ES", "Español"),
        new("fr", "fr-FR", "Français"),
        new("de", "de-DE", "Deutsch"),
        new("it", "it-IT", "Italiano"),
        new("pt", "pt-BR", "Português"),
        new("ru", "ru-RU", "Русский"),
        new("ar", "ar-SA", "العربية"),
        new("hi", "hi-IN", "हिन्दी"),
        new("th", "th-TH", "ไทย"),
        new("vi", "vi-VN", "Tiếng Việt"),
        new("id", "id-ID", "Bahasa Indonesia"),
        new("tr", "tr-TR", "Türkçe"),
        new("pl", "pl-PL", "Polski"),
        new("nl", "nl-NL", "Nederlands"),
        new("uk", "uk-UA", "Українська")
    ];

    public static IReadOnlyList<LanguageOption> OptionalTargets { get; } =
        [new("", "", "关闭第二译文"), .. All];

    public static LanguageOption ByCode(string code) =>
        All.FirstOrDefault(language => language.Code == code) ?? All[0];
}
