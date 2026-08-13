using System.Text.Json;
using VoxLink.UI.Core.Models;

namespace VoxLink.Tests.UI;

/// <summary>本地模型行状态机契约：安装/忙碌/启用状态与按钮可用性、文案的联动。</summary>
public sealed class LocalModelItemTests
{
    private static LocalModelItem Item(string installState, bool isInstallable = true) =>
        LocalModelItem.FromJson(JsonDocument.Parse($$"""
            {
              "id": "test-model",
              "name": "测试模型",
              "category": "tts",
              "supportLevel": "stable",
              "runtime": "sherpaonnxkokoro",
              "parameters": "82M",
              "numericParameterBillions": 0.082,
              "license": "MIT",
              "languages": "zh/en",
              "requirements": "2GB 内存",
              "sourceUrl": "https://example.test/model",
              "description": "测试模型",
              "downloadBytes": 147031220,
              "installState": "{{installState}}",
              "isInstallable": {{(isInstallable ? "true" : "false")}}
            }
            """).RootElement);

    [Fact]
    public void NotInstalled_CanInstallButNotTestOrRemove()
    {
        var item = Item("notinstalled");

        Assert.False(item.Installed);
        Assert.True(item.CanInstall);
        Assert.False(item.CanTest);
        Assert.False(item.CanRemove);
        Assert.False(item.CanActivate);
        Assert.True(item.CanRunPrimaryAction);
        Assert.Equal("安装并启用", item.PrimaryActionLabel);
        Assert.Equal("未安装", item.InstallStateLabel);
    }

    [Fact]
    public void Installed_CanTestAndActivate()
    {
        var item = Item("installed");

        Assert.True(item.Installed);
        Assert.False(item.CanInstall);
        Assert.True(item.CanTest);
        Assert.True(item.CanRemove);
        Assert.True(item.CanActivate);
        Assert.True(item.CanRunPrimaryAction);
        Assert.Equal("启用", item.PrimaryActionLabel);
        Assert.Equal("已安装", item.InstallStateLabel);
    }

    [Fact]
    public void Busy_DisablesAllActions()
    {
        var item = Item("installed");
        item.BeginOperation("正在测试…");

        Assert.True(item.IsBusy);
        Assert.False(item.CanTest);
        Assert.False(item.CanRemove);
        Assert.False(item.CanActivate);
        Assert.False(item.CanRunPrimaryAction);
        Assert.Equal("正在测试…", item.OperationStatus);

        item.FailOperation("测试失败，可重试");
        Assert.False(item.IsBusy);
        Assert.True(item.CanTest);
        Assert.Equal("测试失败，可重试", item.OperationStatus);
    }

    [Fact]
    public void Active_IsEnabledAndCannotRunPrimaryAction()
    {
        var item = Item("installed");
        item.IsActive = true;

        Assert.True(item.IsActive);
        Assert.False(item.CanRunPrimaryAction);
        Assert.Equal("已启用", item.PrimaryActionLabel);
        Assert.True(item.CanTest);
    }

    [Fact]
    public void Partial_RequiresRetry()
    {
        var item = Item("partial");

        Assert.True(item.IsPartial);
        Assert.Equal("需要重试", item.InstallStateLabel);
        Assert.Equal("重试并启用", item.PrimaryActionLabel);
        Assert.False(item.CanTest);
        Assert.True(item.CanRunPrimaryAction);
    }

    [Fact]
    public void CatalogOnly_IsNotDeployableAndCannotTest()
    {
        var item = Item("notinstalled", isInstallable: false);

        Assert.False(item.IsInstallable);
        Assert.False(item.CanInstall);
        Assert.False(item.CanTest);
        Assert.False(item.CanRunPrimaryAction);
        Assert.Equal("不可部署", item.InstallStateLabel);
    }

    [Fact]
    public void CompletedTest_KeepsInstalledStateAndReportsDetail()
    {
        var item = Item("installed");
        item.BeginOperation("正在测试…");
        item.CompleteOperation("installed", "测试通过：Hello, world!");

        Assert.True(item.Installed);
        Assert.False(item.IsBusy);
        Assert.Equal("测试通过：Hello, world!", item.OperationStatus);
        Assert.True(item.CanTest);
    }
}
