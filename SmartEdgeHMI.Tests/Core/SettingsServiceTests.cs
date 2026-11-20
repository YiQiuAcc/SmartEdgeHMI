using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Services;

namespace SmartEdgeHMI.Tests.Core;

/// <summary>SettingsService 首启健壮性测试: 全部使用临时目录, 不读写真实 %LOCALAPPDATA%</summary>
public class SettingsServiceTests : IDisposable
{
    // 每个测试方法独立的临时根目录: xunit 每个方法新建实例, 结束后调用 Dispose 清理
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "SmartEdgeHMI.Tests", Guid.NewGuid().ToString("N"));

    /// <summary>首次运行且设置目录不可写时应 fail-soft: 构造不抛异常, 使用内存默认值</summary>
    [Fact]
    public void 首次运行_目录不可写_不抛异常且使用默认值()
    {
        // 先在临时目录放一个普通文件, 再把设置目录指向该文件下的子目录, 使 Directory.CreateDirectory 必定抛异常
        Directory.CreateDirectory(_tempRoot);
        string blockerFile = Path.Combine(_tempRoot, "not-a-directory");
        File.WriteAllText(blockerFile, string.Empty);
        string settingsDirectory = Path.Combine(blockerFile, "sub");

        var service = new SettingsService(new ConfigurationBuilder().Build(), settingsDirectory);

        Assert.Equal(string.Empty, service.Current.Connection.ComPort);
        Assert.Equal(115200, service.Current.Connection.BaudRate);
    }

    /// <summary>首次运行且目录可写时应写出 settings.json, 且 ComPort 默认为空(语义为未选择)</summary>
    [Fact]
    public void 首次运行_可写目录_写出settings_json且ComPort为空()
    {
        string settingsDirectory = Path.Combine(_tempRoot, "writable");
        string filePath = Path.Combine(settingsDirectory, "settings.json");

        var service = new SettingsService(new ConfigurationBuilder().Build(), settingsDirectory);

        Assert.True(File.Exists(filePath));
        var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath));
        Assert.NotNull(loaded);
        Assert.Equal(string.Empty, loaded!.Connection.ComPort);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (Exception)
        {
            // 清理失败不影响测试结果
        }
    }
}
