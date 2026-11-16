using Microsoft.Extensions.DependencyInjection;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.Protocols.Transports.Serial;

namespace SmartEdgeHMI.Tests.Transport;

public class SerialPortServiceTests
{
    [Fact]
    public void OpenPort_PortDoesNotExist_DoesNotThrowAndDoesNotRaiseConnected()
    {
        using ServiceProvider serviceProvider = new ServiceCollection().BuildServiceProvider();
        var stateEvents = new List<(string PortName, ConnectionState State)>();
        using var service = new SerialPortService(serviceProvider);
        service.StateChanged += (portName, state) => stateEvents.Add((portName, state));

        // 打开不存在的串口不应抛出异常（原缺陷会吞掉异常后继续走成功路径）
        var exception = Record.Exception(() => service.OpenPort("COM99", 115200));

        Assert.Null(exception);

        // 观察 6 秒: 旧缺陷会让已释放的串口进入后台读取循环, 并在 5 秒稳定期
        // (StabilizationDelayMs=5000) 之后发出 Connected, 1 秒窗口无法捕捉该缺陷
        Thread.Sleep(TimeSpan.FromSeconds(6));

        Assert.DoesNotContain(stateEvents, e => e.State == ConnectionState.Connected);
    }
}
