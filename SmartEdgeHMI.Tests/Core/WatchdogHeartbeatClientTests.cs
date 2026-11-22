using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Extensions.Configuration;
using SmartEdgeHMI.Core.Services;
using SmartEdgeHMI.Shared;

namespace SmartEdgeHMI.Tests.Core;

/// <summary>Watchdog 心跳客户端测试: 用进程内命名管道服务端充当假 Watchdog, 直接验证心跳发送/断线重连/Dispose 行为契约</summary>
/// <remarks>xunit 同一测试类内的用例默认串行执行, 正好避免多个用例抢占同一个管道名, 因此所有用例都放在同一个类里</remarks>
public class WatchdogHeartbeatClientTests : IDisposable
{
    // 与产品客户端同一规则派生(基于测试进程的 AppContext.BaseDirectory), 保证假服务端与产品客户端算出同一个名字
    private static readonly string _pipeName = WatchdogPipeName.For(AppContext.BaseDirectory);

    // 测试提速参数: 让每个用例在 1-3 秒内结束, 整套新增用例总耗时控制在 15 秒内。
    // 数值直接写入内存设置对象(不经过 AppSettingsValidator, 其下限为 500ms), 也不会落盘。
    private const int HeartbeatIntervalMs = 50;
    private const int ConnectTimeoutMs = 200;
    private const int ReconnectDelayMs = 100;

    // 每个测试方法创建的临时设置目录, Dispose 时统一清理
    private readonly List<string> _tempDirectories = [];

    /// <summary>启动后连上管道, 按周期发送心跳: 3 秒内至少收到 2 条 "hb"(每条 2 字节 ASCII)</summary>
    [Fact]
    public async Task 启动后_连上管道_按周期发送心跳()
    {
        var settingsService = CreateSettingsService();
        NamedPipeServerStream server = CreateServer();
        WatchdogHeartbeatClient client = new(settingsService);
        try
        {
            Task waitForConnection = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            client.Start();
            await waitForConnection.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // 两个心跳周期(50ms/个)内应累计写满 4 字节; 字节模式下相邻两次写入可能被合并读取, 故按累计字节校验
            byte[] received = await ReadUntilCountAsync(server, expectedByteCount: 4, timeout: TimeSpan.FromSeconds(3));

            Assert.Equal("hbhb"u8.ToArray(), received); // 两条心跳, 每条 2 字节 "hb"
        }
        finally
        {
            client.Dispose();
            server.Dispose();
        }
    }

    /// <summary>没有任何服务端时 Start 不抛异常; Dispose 后 2 秒内后台任务结束(轮询"无新连接/无心跳"判定, 不硬等满)</summary>
    [Fact]
    public async Task 未启动Watchdog_不抛异常且Dispose可快速退出()
    {
        var settingsService = CreateSettingsService();
        WatchdogHeartbeatClient client = new(settingsService);
        NamedPipeServerStream? probe = null;
        bool clientDisposed = false; // 产品 Dispose 不幂等(重复调用会在 _cts.Cancel 处抛 ObjectDisposedException), 必须只释放一次
        try
        {
            // 管道名上没有任何服务端: Start 不得抛出
            Exception? startException = Record.Exception(client.Start);
            Assert.Null(startException);

            client.Dispose();
            clientDisposed = true;

            // 事后才创建同名探针服务端: 若后台任务在 Dispose 后仍在重试, 它会在一个重试周期
            // (ConnectTimeoutMs + ReconnectDelayMs ≈ 300ms)内连上探针; 轮询 2 秒无新连接即判定任务已退出
            probe = CreateServer();
            bool reconnected = false;
            // 链接测试级取消令牌: 探针窗口 2 秒先到先超时, 测试被整体取消时也随之退出
            using CancellationTokenSource probeTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            probeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await probe.WaitForConnectionAsync(probeTimeout.Token);
                reconnected = true;
            }
            catch (OperationCanceledException)
            {
                // 2 秒内没有新连接: 后台任务已退出, 用例通过
            }

            if (reconnected)
            {
                // Dispose 瞬间的在途连接尝试可能在取消生效前完成(任务随后仍会退出);
                // 此时再轮询 500ms: 若没有新的心跳字节写出, 后台任务同样已退出
                int residue = await CountIncomingBytesAsync(probe, TimeSpan.FromMilliseconds(500));
                Assert.Equal(0, residue); // Dispose 后不应再写出任何心跳
            }
        }
        finally
        {
            probe?.Dispose();
            if (!clientDisposed)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>服务端断开后, 客户端经 ReconnectDelayMs 重连第二个同名服务端, 并继续发送心跳</summary>
    [Fact]
    public async Task 服务端断开后_能重连并继续发心跳()
    {
        var settingsService = CreateSettingsService();
        NamedPipeServerStream firstServer = CreateServer();
        WatchdogHeartbeatClient client = new(settingsService);
        NamedPipeServerStream? secondServer = null;
        try
        {
            Task waitForFirstConnection = firstServer.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            client.Start();
            await waitForFirstConnection.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // 先确认心跳已按周期正常发送(4 字节 = 2 条 "hb")
            byte[] before = await ReadUntilCountAsync(firstServer, expectedByteCount: 4, timeout: TimeSpan.FromSeconds(3));
            Assert.Equal("hbhb"u8.ToArray(), before);

            // 关掉服务端: 客户端写出下一次心跳时发现断开(IsConnected 翻转或 IOException), 经 ReconnectDelayMs 后重试
            firstServer.Dispose();

            // 立刻起第二个同名服务端, 3 秒内应完成重连并再次收到心跳
            secondServer = CreateServer();
            Task waitForSecondConnection = secondServer.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            await waitForSecondConnection.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            byte[] after = await ReadUntilCountAsync(secondServer, expectedByteCount: 2, timeout: TimeSpan.FromSeconds(3));
            Assert.Equal("hb"u8.ToArray(), after);
        }
        finally
        {
            client.Dispose();
            secondServer?.Dispose();
            firstServer.Dispose();
        }
    }

    /// <summary>Dispose 后不再发送心跳: 收到 1 条后停止, 清空在途数据, 再轮询 500ms 断言没有任何新的 2 字节数据可读</summary>
    [Fact]
    public async Task Dispose后_不再发送心跳()
    {
        var settingsService = CreateSettingsService();
        NamedPipeServerStream server = CreateServer();
        WatchdogHeartbeatClient client = new(settingsService);
        bool clientDisposed = false; // 产品 Dispose 不幂等(重复调用会在 _cts.Cancel 处抛 ObjectDisposedException), 必须只释放一次
        try
        {
            Task waitForConnection = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            client.Start();
            await waitForConnection.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // 收到至少 1 条心跳后再 Dispose
            byte[] before = await ReadUntilCountAsync(server, expectedByteCount: 2, timeout: TimeSpan.FromSeconds(3));
            Assert.Equal("hb"u8.ToArray(), before);

            client.Dispose();
            clientDisposed = true;

            // 清空读取缓冲区: Dispose 瞬间可能有一次取消尚未生效的在途心跳, 250ms 内到达的都算残留(读走丢弃)
            await CountIncomingBytesAsync(server, TimeSpan.FromMilliseconds(250));

            // 再轮询 500ms(HeartbeatIntervalMs=50, 若后台任务仍在发送必然再次出现): 不应有任何新的 2 字节数据可读
            int leaked = await CountIncomingBytesAsync(server, TimeSpan.FromMilliseconds(500));
            Assert.Equal(0, leaked);
        }
        finally
        {
            server.Dispose();
            if (!clientDisposed)
            {
                client.Dispose();
            }
        }
    }

    public void Dispose()
    {
        foreach (string directory in _tempDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // 清理失败不影响测试结果
            }
        }
    }

    /// <summary>创建替身设置服务: 与 AlarmStateMachineTests 相同的写法, 真实 SettingsService + 临时目录, 不读写真实 %LOCALAPPDATA%</summary>
    private SettingsService CreateSettingsService()
    {
        string settingsDirectory = Path.Combine(
            Path.GetTempPath(), "SmartEdgeHMI.Tests", Guid.NewGuid().ToString("N"));
        _tempDirectories.Add(settingsDirectory);

        var settingsService = new SettingsService(new ConfigurationBuilder().Build(), settingsDirectory);
        settingsService.Current.Watchdog.HeartbeatIntervalMs = HeartbeatIntervalMs;
        settingsService.Current.Watchdog.ConnectTimeoutMs = ConnectTimeoutMs;
        settingsService.Current.Watchdog.ReconnectDelayMs = ReconnectDelayMs;
        return settingsService;
    }

    /// <summary>创建与产品约定一致的测试用服务端(入方向、单实例、字节模式、异步)</summary>
    private static NamedPipeServerStream CreateServer()
        => new(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    /// <summary>从服务端管道累计读取 expectedByteCount 个字节, 超时则返回已读到的部分(由调用方断言判定)</summary>
    private static async Task<byte[]> ReadUntilCountAsync(NamedPipeServerStream server, int expectedByteCount, TimeSpan timeout)
    {
        byte[] buffer = new byte[expectedByteCount];
        int total = 0;
        // 链接测试级取消令牌: 超时先到按超时算, 测试被整体取消时也随之退出
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            while (total < expectedByteCount)
            {
                int read = await server.ReadAsync(buffer.AsMemory(total), timeoutCts.Token);
                if (read == 0)
                {
                    break; // 客户端管道已关闭, 不会再有更多数据
                }

                total += read;
            }
        }
        catch (OperationCanceledException)
        {
            // 超时: 交由调用方对已读内容做断言
        }

        return buffer[..total];
    }

    /// <summary>在窗口时间内按 50ms 切片轮询读取管道数据, 返回累计读到的字节数(读到的数据由调用方决定丢弃还是断言)</summary>
    private static async Task<int> CountIncomingBytesAsync(NamedPipeServerStream server, TimeSpan window)
    {
        byte[] buffer = new byte[64];
        int total = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < window)
        {
            // 链接测试级取消令牌: 50ms 切片先到先超时, 测试被整体取消时也随之退出
            using CancellationTokenSource sliceTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            sliceTimeout.CancelAfter(TimeSpan.FromMilliseconds(50));
            try
            {
                int read = await server.ReadAsync(buffer, sliceTimeout.Token);
                if (read == 0)
                {
                    break; // 客户端管道已关闭且无残留数据, 不会再有更多数据
                }

                total += read;
            }
            catch (OperationCanceledException)
            {
                // 本切片内没有数据, 继续下一片直到窗口结束
            }
        }

        return total;
    }
}
