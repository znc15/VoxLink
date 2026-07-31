namespace VoxLink.Models;

public sealed record TranslationProviderOption(TranslationProvider Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record WhisperModelOption(string Value, string DisplayName, string Detail)
{
    public override string ToString() => $"{DisplayName} · {Detail}";
}
