using VoxLink.Services;

namespace VoxLink.Tests.Services;

public sealed class CustomHttpHeaderValidatorTests
{
    [Fact]
    public void Validate_AllowsHttpTokenNameAndSingleLineValue()
    {
        CustomHttpHeaderValidator.Validate("X-VoxLink_Tenant.1", "tenant-value");
    }

    [Theory]
    [InlineData("Bad Header")]
    [InlineData("X:Bad")]
    [InlineData("中文请求头")]
    public void Validate_RejectsInvalidHttpFieldName(string name)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            CustomHttpHeaderValidator.Validate(name, "value"));

        Assert.Contains("名称无效", error.Message);
    }

    [Theory]
    [InlineData("line1\rline2")]
    [InlineData("line1\nline2")]
    public void Validate_RejectsHeaderValueNewlines(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            CustomHttpHeaderValidator.Validate("X-Test", value));

        Assert.Contains("换行符", error.Message);
    }
}
