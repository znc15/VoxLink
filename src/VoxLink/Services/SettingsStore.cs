using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoxLink.Models;

namespace VoxLink.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private static readonly byte[] EncryptionEntropy = Encoding.UTF8.GetBytes("VoxLink.Settings.v1");
    private const string EncryptedPrefix = "dpapi:";

    private readonly string _settingsPath;

    public SettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoxLink",
            "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
                ?? new AppSettings();
            settings.OpenAiApiKey = Unprotect(settings.OpenAiApiKey);
            settings.TextToSpeechApiKey = Unprotect(settings.TextToSpeechApiKey);
            settings.AsrApiKey = Unprotect(settings.AsrApiKey);
            settings.OpenAiHeaders = UnprotectValues(settings.OpenAiHeaders);
            settings.TextToSpeechHeaders = UnprotectValues(settings.TextToSpeechHeaders);
            settings.AsrHeaders = UnprotectValues(settings.AsrHeaders);
            return settings;
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("设置路径无效。");
        Directory.CreateDirectory(directory);

        var serializedCopy = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings, SerializerOptions),
            SerializerOptions) ?? throw new InvalidOperationException("无法复制设置。");
        serializedCopy.OpenAiApiKey = Protect(settings.OpenAiApiKey);
        serializedCopy.TextToSpeechApiKey = Protect(settings.TextToSpeechApiKey);
        serializedCopy.AsrApiKey = Protect(settings.AsrApiKey);
        serializedCopy.OpenAiHeaders = ProtectValues(settings.OpenAiHeaders);
        serializedCopy.TextToSpeechHeaders = ProtectValues(settings.TextToSpeechHeaders);
        serializedCopy.AsrHeaders = ProtectValues(settings.AsrHeaders);
        var temporaryPath = _settingsPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, serializedCopy, SerializerOptions, cancellationToken);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var plaintext = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(plaintext, EncryptionEntropy, DataProtectionScope.CurrentUser);
        return EncryptedPrefix + Convert.ToBase64String(protectedBytes);
    }

    private static IReadOnlyDictionary<string, string> ProtectValues(
        IReadOnlyDictionary<string, string> values) => values.ToDictionary(
            pair => pair.Key,
            pair => Protect(pair.Value),
            StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> UnprotectValues(
        IReadOnlyDictionary<string, string> values) => values.ToDictionary(
            pair => pair.Key,
            pair => Unprotect(pair.Value),
            StringComparer.OrdinalIgnoreCase);

    private static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(value[EncryptedPrefix.Length..]);
            var plaintext = ProtectedData.Unprotect(
                protectedBytes,
                EncryptionEntropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }
}
