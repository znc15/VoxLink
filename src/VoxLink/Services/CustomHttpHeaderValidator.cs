namespace VoxLink.Services;

internal static class CustomHttpHeaderValidator
{
    private const string TokenPunctuation = "!#$%&'*+-.^_`|~";

    public static bool IsRestricted(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Host", StringComparison.OrdinalIgnoreCase);

    public static void Validate(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name)
            || !name.All(character =>
                char.IsAsciiLetterOrDigit(character) || TokenPunctuation.Contains(character)))
        {
            throw new InvalidOperationException($"自定义请求头名称无效：{name}");
        }

        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new InvalidOperationException($"自定义请求头 {name} 的值不能包含换行符。");
        }
    }
}
