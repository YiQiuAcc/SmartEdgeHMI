namespace SmartEdgeHMI.Communication;

/// <summary>设备通信协调器: 封装指令下发(阈值设置、设备复位)的统一入口, 与协议类型无关</summary>
public interface IDeviceCommunicationCoordinator
{
    /// <summary>异步下发阈值数据到设备</summary>
    /// <param name="value">阈值</param>
    Task SendThresholdAsync(double value);

    /// <summary>异步复位设备</summary>
    Task ResetDeviceAsync();
}
