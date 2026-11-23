using SmartEdgeHMI.Shared;

namespace SmartEdgeHMI.Tests.Core;

/// <summary>Watchdog 管道名派生规则测试: 锁死已发布版本的具体取值, 防止算法或前缀被无意改动导致新旧构建的管道对不上</summary>
public class WatchdogPipeNameTests
{
    [Fact]
    public void 已知目录_得到锁死的管道名()
    {
        Assert.Equal("SmartEdgeHMI_Watchdog_Pipe_30686708c530bb19",
            WatchdogPipeName.For(@"C:\Program Files\SmartEdgeHMI"));
    }

    /// <summary>大小写不同或结尾多一个分隔符视为同一目录, 必须得到同一个管道名</summary>
    [Fact]
    public void 大小写或结尾分隔符不同_得到同一管道名()
    {
        string expected = WatchdogPipeName.For(@"C:\Program Files\SmartEdgeHMI");

        Assert.Equal(expected, WatchdogPipeName.For(@"c:\program files\smartedgehmi"));
        Assert.Equal(expected, WatchdogPipeName.For(@"C:\PROGRAM FILES\SMARTEDGEHMI"));
        Assert.Equal(expected, WatchdogPipeName.For(@"C:\Program Files\SmartEdgeHMI\"));
    }

    /// <summary>两个不同目录必须派生出不同管道名, 否则不同安装实例仍会互相干扰</summary>
    [Fact]
    public void 不同目录_得到不同管道名()
    {
        Assert.NotEqual(
            WatchdogPipeName.For(@"C:\Program Files\SmartEdgeHMI"),
            WatchdogPipeName.For(@"D:\Apps\SmartEdgeHMI"));
    }

    [Fact]
    public void 结果_以Prefix开头且后缀为16位小写十六进制()
    {
        string pipeName = WatchdogPipeName.For(@"C:\Windows");

        Assert.StartsWith(WatchdogPipeName.Prefix, pipeName);
        string suffix = pipeName[WatchdogPipeName.Prefix.Length..];
        Assert.Equal(16, suffix.Length);
        Assert.Matches("^[0-9a-f]{16}$", suffix);
    }

    /// <summary>目录为 null/空/空白时抛 ArgumentNullException, 不允许派生出兜底管道名</summary>
    [Fact]
    public void 目录为空或空白_抛ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => WatchdogPipeName.For(null));
        Assert.Throws<ArgumentNullException>(() => WatchdogPipeName.For(""));
        Assert.Throws<ArgumentNullException>(() => WatchdogPipeName.For("   "));
    }
}
