using VoxLink.Services;

namespace VoxLink.Tests.Services;

public sealed class LocalFireRedAsr2CtcRecognizerTests
{
    [Theory]
    [InlineData("<sli>你好<sli>", "你好")]
    [InlineData("<SLI> hello </sli>", "hello")]
    [InlineData("<sli class=\"control\">a</sli>", "a")]
    [InlineData("<sli>", "")]
    [InlineData("  hello   world \t again  ", "hello world again")]
    [InlineData("<sli>你好，世界！</sli>", "你好，世界！")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void SanitizeTranscript_RemovesControlMarkersAndNormalizesWhitespace(
        string? input,
        string expected)
    {
        var actual = LocalFireRedAsr2CtcRecognizer.SanitizeTranscript(input);

        Assert.Equal(expected, actual);
    }
}
