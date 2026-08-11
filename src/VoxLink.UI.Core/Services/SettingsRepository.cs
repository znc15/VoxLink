using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const string GenerationPropertyName = "settingsGeneration";
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
    private readonly string _lockPath;
    private readonly string _legacyPreferencesPath;
    private readonly string _legacySecretsPath;

    public SettingsRepository(
        string? settingsPath = null,
        string? secretsPath = null,
        string? legacyPreferencesPath = null,
        string? legacySecretsPath = null)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _settingsPath = Path.GetFullPath(
            settingsPath ?? Path.Combine(roaming, "VoxLink", "settings.json"));
        _secretsPath = Path.GetFullPath(
            secretsPath ?? Path.Combine(roaming, "VoxLink", "secrets.dat"));
        _lockPath = _settingsPath + ".lock";
        _legacyPreferencesPath = legacyPreferencesPath
            ?? Path.Combine(roaming, "VoxLink", "VoxLink", "shared_preferences.json");
        _legacySecretsPath = legacySecretsPath
            ?? Path.Combine(roaming, "VoxLink", "VoxLink", "flutter_secure_storage.dat");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var transactionLock = await AcquireTransactionLockAsync(cancellationToken);
        return await LoadCoreAsync(cancellationToken);
    }

    private async Task<AppSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var migrated = false;
        var publicLoad = await LoadPublicSettingsAsync(cancellationToken);
        var currentPublicMissing = publicLoad.IsMissing;
        var settings = publicLoad.Settings;
        var hasUseAiTranslation = publicLoad.HasUseAiTranslation;
        var hasUseCloudAsr = publicLoad.HasUseCloudAsr;
        if (currentPublicMissing)
        {
            var legacyLoad = await LoadLegacyPublicSettingsAsync(cancellationToken);
            settings = legacyLoad.Settings;
            hasUseAiTranslation = legacyLoad.HasUseAiTranslation;
            hasUseCloudAsr = legacyLoad.HasUseCloudAsr;
            migrated = true;
        }

        if (settings is null)
        {
            settings = new AppSettings();
            hasUseAiTranslation = false;
            hasUseCloudAsr = false;
        }

        var secretsLoad = await LoadSecretsAsync(_secretsPath, SecretEntropy, cancellationToken);
        var currentSecretsMissing = secretsLoad.IsMissing;
        migrated |= ValidateCurrentGenerationPair(publicLoad, secretsLoad);
        var secrets = secretsLoad.Settings;
        if (currentPublicMissing && !currentSecretsMissing)
        {
            secrets = new SecretSettings();
            migrated = true;
        }
        if (currentSecretsMissing)
        {
            if (currentPublicMissing)
            {
                var legacySecretsLoad = await LoadSecretsAsync(
                    _legacySecretsPath,
                    entropy: null,
                    cancellationToken);
                secrets = legacySecretsLoad.Settings ?? new SecretSettings();
            }
            else
            {
                secrets = new SecretSettings();
            }
            migrated = true;
        }

        secrets ??= new SecretSettings();
        settings.TranslationApiKey = secrets.TranslationApiKey;
        settings.SpeechApiKey = secrets.SpeechApiKey;
        settings.AsrApiKey = secrets.AsrApiKey;
        settings.TranslationHeaders = secrets.TranslationHeaders;
        settings.SpeechHeaders = secrets.SpeechHeaders;
        settings.AsrHeaders = secrets.AsrHeaders;
        if (!hasUseAiTranslation || !hasUseCloudAsr)
        {
            if (!hasUseAiTranslation)
            {
                settings.UseAiTranslation = settings.TranslationBackend != TranslationBackend.PublicFree;
            }

            if (!hasUseCloudAsr)
            {
                settings.UseCloudAsr = settings.AsrProvider != AsrProvider.LocalWhisper;
            }

            migrated = true;
        }

        migrated |= settings.NormalizeServiceSelections();
        if (migrated)
        {
            await SaveCoreAsync(settings, cancellationToken);
        }

        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var transactionLock = await AcquireTransactionLockAsync(cancellationToken);
        await SaveCoreAsync(settings, cancellationToken);
    }

    private async Task SaveCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var generation = Guid.NewGuid().ToString("N");
        var publicObject = JsonSerializer.SerializeToNode(settings, SerializerOptions) as JsonObject
            ?? throw new JsonException("无法序列化设置。");
        publicObject[GenerationPropertyName] = generation;
        var publicJson = JsonSerializer.SerializeToUtf8Bytes(publicObject, SerializerOptions);
        var secretJson = JsonSerializer.SerializeToUtf8Bytes(
            new SecretSettings
            {
                SettingsGeneration = generation,
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

        await WritePairAtomicallyAsync(
            _settingsPath,
            publicJson,
            _secretsPath,
            protectedSecrets,
            cancellationToken);
    }

    private async Task<PublicSettingsLoad> LoadPublicSettingsAsync(CancellationToken cancellationToken)
    {
        byte[] raw;
        try
        {
            raw = await File.ReadAllBytesAsync(_settingsPath, cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return PublicSettingsLoad.Missing;
        }

        using var document = JsonDocument.Parse(raw);
        var settings = document.RootElement.Deserialize<AppSettings>(SerializerOptions)
            ?? throw new JsonException("设置文件不包含有效的 JSON 对象。");
        return new PublicSettingsLoad(
            settings,
            HasProperty(document.RootElement, "useAiTranslation"),
            HasProperty(document.RootElement, "useCloudAsr"),
            ReadOptionalString(document.RootElement, GenerationPropertyName));
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
                HasProperty(settingsDocument.RootElement, "useAiTranslation"),
                HasProperty(settingsDocument.RootElement, "useCloudAsr"));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new PublicSettingsLoad(null, false);
        }
    }


    private static async Task<SecretSettingsLoad> LoadSecretsAsync(
        string path,
        byte[]? entropy,
        CancellationToken cancellationToken)
    {
        byte[] protectedBytes;
        try
        {
            protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return SecretSettingsLoad.Missing;
        }

        var json = ProtectedData.Unprotect(
            protectedBytes,
            entropy,
            DataProtectionScope.CurrentUser);
        var settings = JsonSerializer.Deserialize<SecretSettings>(json, SerializerOptions)
            ?? throw new JsonException("秘密设置文件不包含有效的 JSON 对象。");
        return new SecretSettingsLoad(settings);
    }

    private async Task<FileStream> AcquireTransactionLockAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_lockPath)
            ?? throw new InvalidOperationException("设置锁路径无效。");
        Directory.CreateDirectory(directory);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException exception)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new IOException(
                        "设置正被另一个 VoxLink 进程使用，请稍后重试。",
                        exception);
                }
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private static async Task WritePairAtomicallyAsync(
        string publicPath,
        byte[] publicContent,
        string secretPath,
        byte[] secretContent,
        CancellationToken cancellationToken)
    {
        var publicDirectory = Path.GetDirectoryName(publicPath)
            ?? throw new InvalidOperationException("设置路径无效。");
        var secretDirectory = Path.GetDirectoryName(secretPath)
            ?? throw new InvalidOperationException("秘密设置路径无效。");
        Directory.CreateDirectory(publicDirectory);
        Directory.CreateDirectory(secretDirectory);

        var previousPublic = await ReadExistingFileAsync(publicPath, cancellationToken);
        var previousSecret = await ReadExistingFileAsync(secretPath, cancellationToken);
        var publicTemporaryPath = publicPath + $".{Guid.NewGuid():N}.tmp";
        var secretTemporaryPath = secretPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(publicTemporaryPath, publicContent, cancellationToken);
            await File.WriteAllBytesAsync(secretTemporaryPath, secretContent, cancellationToken);
            File.Move(publicTemporaryPath, publicPath, overwrite: true);
            try
            {
                File.Move(secretTemporaryPath, secretPath, overwrite: true);
            }
            catch
            {
                RestoreFile(publicPath, previousPublic);
                RestoreFile(secretPath, previousSecret);
                throw;
            }
        }
        finally
        {
            DeleteTemporaryFile(publicTemporaryPath);
            DeleteTemporaryFile(secretTemporaryPath);
        }
    }

    private static async Task<byte[]?> ReadExistingFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void RestoreFile(string path, byte[]? content)
    {
        if (content is null)
        {
            File.Delete(path);
            return;
        }

        var temporaryPath = path + $".{Guid.NewGuid():N}.rollback";
        try
        {
            File.WriteAllBytes(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
        }
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool ValidateCurrentGenerationPair(
        PublicSettingsLoad publicLoad,
        SecretSettingsLoad secretsLoad)
    {
        if (publicLoad.IsMissing && secretsLoad.IsMissing)
        {
            return false;
        }
        if (publicLoad.IsMissing || secretsLoad.IsMissing)
        {
            var presentGeneration = publicLoad.IsMissing
                ? secretsLoad.Settings?.SettingsGeneration
                : publicLoad.SettingsGeneration;
            if (string.IsNullOrWhiteSpace(presentGeneration))
            {
                return true;
            }
            throw new InvalidDataException("设置与秘密设置不完整，已拒绝加载以保护服务凭据。");
        }

        var publicGeneration = publicLoad.SettingsGeneration;
        var secretGeneration = secretsLoad.Settings?.SettingsGeneration;
        if (string.IsNullOrWhiteSpace(publicGeneration)
            && string.IsNullOrWhiteSpace(secretGeneration))
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(publicGeneration)
            || string.IsNullOrWhiteSpace(secretGeneration)
            || !string.Equals(publicGeneration, secretGeneration, StringComparison.Ordinal))
        {
            throw new InvalidDataException("设置与秘密设置版本不一致，已拒绝加载以保护服务凭据。");
        }

        return false;
    }

    private sealed record PublicSettingsLoad(
        AppSettings? Settings,
        bool HasUseAiTranslation = false,
        bool HasUseCloudAsr = false,
        string? SettingsGeneration = null,
        bool IsMissing = false)
    {
        public static PublicSettingsLoad Missing { get; } = new(null, IsMissing: true);
    }

    private sealed record SecretSettingsLoad(SecretSettings? Settings, bool IsMissing = false)
    {
        public static SecretSettingsLoad Missing { get; } = new(null, IsMissing: true);
    }

    private sealed class SecretSettings
    {
        [JsonPropertyName("voxlink.settings.generation")]
        public string? SettingsGeneration { get; set; }

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

    private static string? ReadOptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().FirstOrDefault(property =>
            property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value
            is { ValueKind: JsonValueKind.String } value
            ? value.GetString()
            : null;

    private static bool HasProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().Any(property =>
            property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
