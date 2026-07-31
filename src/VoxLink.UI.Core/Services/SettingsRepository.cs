using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoxLink.UI.Core.Models;

namespace VoxLink.UI.Core.Services;

public interface ISettingsRepository
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public sealed class SettingsRepository : ISettingsRepository
{
    private static readonly byte[] SecretEntropy = Encoding.UTF8.GetBytes("VoxLink.UI.Secrets.v1");
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _settingsPath;
    private readonly string _secretsPath;
    private readonly string _legacyPreferencesPath;
    private readonly string _legacySecretsPath;

    public SettingsRepository(
        string? settingsPath = null,
        string? secretsPath = null,
        string? legacyPreferencesPath = null,
        string? legacySecretsPath = null)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _settingsPath = settingsPath ?? Path.Combine(roaming, "VoxLink", "settings.json");
        _secretsPath = secretsPath ?? Path.Combine(roaming, "VoxLink", "secrets.dat");
        _legacyPreferencesPath = legacyPreferencesPath
            ?? Path.Combine(roaming, "VoxLink", "VoxLink", "shared_preferences.json");
        _legacySecretsPath = legacySecretsPath
            ?? Path.Combine(roaming, "VoxLink", "VoxLink", "flutter_secure_storage.dat");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var migrated = false;
        var publicLoad = await LoadPublicSettingsAsync(cancellationToken);
        var settings = publicLoad.Settings;
        var hasQuickStartMode = publicLoad.HasQuickStartMode;
        if (settings is null)
        {
            var legacyLoad = await LoadLegacyPublicSettingsAsync(cancellationToken);
            settings = legacyLoad.Settings;
            hasQuickStartMode = legacyLoad.HasQuickStartMode;
            migrated = true;
        }

        if (settings is null)
        {
            settings = new AppSettings();
            hasQuickStartMode = false;
        }

        var secrets = await LoadSecretsAsync(_secretsPath, SecretEntropy, cancellationToken);
        if (secrets is null)
        {
            secrets = await LoadSecretsAsync(_legacySecretsPath, entropy: null, cancellationToken)
                ?? new SecretSettings();
            migrated = true;
        }

        settings.TranslationApiKey = secrets.TranslationApiKey;
        settings.SpeechApiKey = secrets.SpeechApiKey;
        settings.AsrApiKey = secrets.AsrApiKey;
        settings.TranslationHeaders = secrets.TranslationHeaders;
        settings.SpeechHeaders = secrets.SpeechHeaders;
        settings.AsrHeaders = secrets.AsrHeaders;
        migrated |= !hasQuickStartMode;

        var normalized = settings.NormalizeQuickStartSettings(hasQuickStartMode);
        if (migrated || normalized)
        {
            await SaveAsync(settings, cancellationToken);
        }

        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var publicJson = JsonSerializer.SerializeToUtf8Bytes(settings, SerializerOptions);
        var secretJson = JsonSerializer.SerializeToUtf8Bytes(
            new SecretSettings
            {
                TranslationApiKey = settings.TranslationApiKey,
                SpeechApiKey = settings.SpeechApiKey,
                AsrApiKey = settings.AsrApiKey,
                TranslationHeaders = new(settings.TranslationHeaders, StringComparer.OrdinalIgnoreCase),
                SpeechHeaders = new(settings.SpeechHeaders, StringComparer.OrdinalIgnoreCase),
                AsrHeaders = new(settings.AsrHeaders, StringComparer.OrdinalIgnoreCase)
            },
            SerializerOptions);
        var protectedSecrets = ProtectedData.Protect(
            secretJson,
            SecretEntropy,
            DataProtectionScope.CurrentUser);

        await WriteAtomicallyAsync(_settingsPath, publicJson, cancellationToken);
        await WriteAtomicallyAsync(_secretsPath, protectedSecrets, cancellationToken);
    }

    private async Task<PublicSettingsLoad> LoadPublicSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new PublicSettingsLoad(null, false);
            }

            var raw = await File.ReadAllBytesAsync(_settingsPath, cancellationToken);
            using var document = JsonDocument.Parse(raw);
            var settings = document.RootElement.Deserialize<AppSettings>(SerializerOptions);
            return new PublicSettingsLoad(
                settings,
                HasProperty(document.RootElement, "quickStartMode"));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new PublicSettingsLoad(null, false);
        }
    }

    private async Task<PublicSettingsLoad> LoadLegacyPublicSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_legacyPreferencesPath))
            {
                return new PublicSettingsLoad(null, false);
            }

            var raw = await File.ReadAllTextAsync(_legacyPreferencesPath, cancellationToken);
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("flutter.voxlink.settings.v2", out var nested)
                || nested.ValueKind != JsonValueKind.String)
            {
                return new PublicSettingsLoad(null, false);
            }

            var settingsJson = nested.GetString();
            if (string.IsNullOrWhiteSpace(settingsJson))
            {
                return new PublicSettingsLoad(null, false);
            }

            using var settingsDocument = JsonDocument.Parse(settingsJson);
            var settings = settingsDocument.RootElement.Deserialize<AppSettings>(SerializerOptions);
            return new PublicSettingsLoad(
                settings,
                HasProperty(settingsDocument.RootElement, "quickStartMode"));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new PublicSettingsLoad(null, false);
        }
    }


    private static async Task<SecretSettings?> LoadSecretsAsync(
        string path,
        byte[]? entropy,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var json = ProtectedData.Unprotect(
                protectedBytes,
                entropy,
                DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<SecretSettings>(json, SerializerOptions);
        }
        catch (Exception exception) when (
            exception is CryptographicException
                or JsonException
                or IOException
                or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("设置路径无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record PublicSettingsLoad(AppSettings? Settings, bool HasQuickStartMode);

    private sealed class SecretSettings
    {
        [JsonPropertyName("voxlink.translation.apiKey")]
        public string TranslationApiKey { get; set; } = string.Empty;

        [JsonPropertyName("voxlink.speech.apiKey")]
        public string SpeechApiKey { get; set; } = string.Empty;

        [JsonPropertyName("voxlink.asr.apiKey")]
        public string AsrApiKey { get; set; } = string.Empty;

        [JsonPropertyName("voxlink.translation.headers")]
        [JsonConverter(typeof(HeaderDictionaryConverter))]
        public Dictionary<string, string> TranslationHeaders { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("voxlink.speech.headers")]
        [JsonConverter(typeof(HeaderDictionaryConverter))]
        public Dictionary<string, string> SpeechHeaders { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("voxlink.asr.headers")]
        [JsonConverter(typeof(HeaderDictionaryConverter))]
        public Dictionary<string, string> AsrHeaders { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class HeaderDictionaryConverter : JsonConverter<Dictionary<string, string>>
    {
        public override Dictionary<string, string> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var nested = reader.GetString();
                if (string.IsNullOrWhiteSpace(nested))
                {
                    return new(StringComparer.OrdinalIgnoreCase);
                }

                return JsonSerializer.Deserialize<Dictionary<string, string>>(nested, options)
                    ?? new(StringComparer.OrdinalIgnoreCase);
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options)
                    ?? [];
                return new(values, StringComparer.OrdinalIgnoreCase);
            }

            reader.Skip();
            return new(StringComparer.OrdinalIgnoreCase);
        }

        public override void Write(
            Utf8JsonWriter writer,
            Dictionary<string, string> value,
            JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, options);
    }

    private static bool HasProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().Any(property =>
            property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
