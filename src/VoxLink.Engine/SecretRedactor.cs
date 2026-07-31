namespace VoxLink.Engine;

internal static class SecretRedactor
{
    public static string Redact(string message, IEnumerable<string?> secrets)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(secrets);

        foreach (var secret in secrets
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(value => value!)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(value => value.Length))
        {
            message = message.Replace(secret, "[redacted]", StringComparison.OrdinalIgnoreCase);
        }

        return message;
    }
}
