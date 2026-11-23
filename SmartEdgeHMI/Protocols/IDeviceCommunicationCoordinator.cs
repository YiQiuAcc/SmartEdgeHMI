namespace SmartEdgeHMI.Protocols;

/// <summary>设备通信协调器: 指令下发的统一入口, 与协议类型无关</summary>
/// <remarks>调用方无需感知 JSON / Modbus 差异; 写失败在实现内部记录日志, 不抛出</remarks>
public interface IDeviceCommunicationCoordinator
{
    /// <summary>下发报警阈值到已连接设备</summary>
    /// <param name="value">阈值, 单位 °C</param>
    Task SendThresholdAsync(double value);

    /// <summary>复位界面当前选中的设备</summary>
    Task ResetDeviceAsync();
}
