using VoxLink.Services;

namespace VoxLink.Tests.Services;

public sealed class LocalSenseVoiceAsrRecognizerTests
{
    [Theory]
    [InlineData("<|zh|><|NEUTRAL|><|Speech|><|woitn|>你好，世界！", "你好，世界！")]
    [InlineData("<|en|> hello   world ", "hello world")]
    [InlineData("保留 <ordinary> 标签", "保留 <ordinary> 标签")]
    [InlineData("", "")]
    public void StripSenseVoiceMarkers_RemovesOnlyControlTokens(string input, string expected)
    {
        Assert.Equal(expected, LocalSenseVoiceAsrRecognizer.StripSenseVoiceMarkers(input));
    }
}
