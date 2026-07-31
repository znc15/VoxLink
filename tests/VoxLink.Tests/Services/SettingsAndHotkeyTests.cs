using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Tests.Services;

public sealed class SettingsAndHotkeyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"VoxLink.Tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SettingsStore_RoundTripsSettings()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new SettingsStore(path);
        var settings = new AppSettings
        {
            MyLanguageCode = "ja",
            OtherLanguageCode = "en",
            WhisperModel = "base",
            ShowOverlay = false,
            OpenAiApiKey = "secret-test-key"
        };

        await store.SaveAsync(settings);
        var persistedJson = await File.ReadAllTextAsync(path);
        var loaded = await store.LoadAsync();

        Assert.Equal("ja", loaded.MyLanguageCode);
        Assert.Equal("en", loaded.OtherLanguageCode);
        Assert.Equal("base", loaded.WhisperModel);
        Assert.False(loaded.ShowOverlay);
        Assert.Equal("secret-test-key", loaded.OpenAiApiKey);
        Assert.DoesNotContain("secret-test-key", persistedJson, StringComparison.Ordinal);
        Assert.Contains("dpapi:", persistedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsStore_ReturnsDefaultsForCorruptJson()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, "not-json");

        var loaded = await new SettingsStore(path).LoadAsync();

        Assert.Equal("zh", loaded.MyLanguageCode);
        Assert.Equal("en", loaded.OtherLanguageCode);
    }

    [Fact]
    public void ParseHotkey_RequiresModifierAndReturnsVirtualKey()
    {
        var parsed = GlobalHotkeyService.Parse("Ctrl+Alt+Space");

        Assert.NotEqual(0u, parsed.Modifiers);
        Assert.NotEqual(0u, parsed.VirtualKey);
        Assert.Throws<ArgumentException>(() => GlobalHotkeyService.Parse("Space"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
