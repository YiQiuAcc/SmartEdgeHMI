using System.ComponentModel;
using SmartEdgeHMI.Models.Enums;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.Models.ValueObjects;

namespace SmartEdgeHMI.Services;

/// <summary>全局单例设备状态容器：遥测、连接、报警状态的单一数据源</summary>
public interface IDeviceStateContainer : INotifyPropertyChanged
{
    // ===== 最新遥测摘要 =====
    Temperature LatestTemperature { get; }
    Humidity LatestHumidity { get; }
    DeviceStatus LatestDeviceStatus { get; }
    ErrorCode LatestErrorCode { get; }
    DataQuality LatestQuality { get; }
    DateTime LatestTimestamp { get; }
    string LatestPortName { get; }

    // ===== 每端口快照 =====
    DeviceStateSnapshot? GetDeviceState(string portName);

    // ===== 报警状态 =====
    IReadOnlyDictionary<string, ErrorCode> ActiveAlarms { get; }
    bool HasActiveAlarms { get; }

    // ===== 连接状态 =====
    bool IsPortConnected(string portName);
    IReadOnlySet<string> ConnectedPorts { get; }

    // ===== 由基础设施调用（MonitorViewModel / 物理层） =====
    void UpdateTelemetry(string portName, Temperature temperature, Humidity humidity,
        DeviceStatus status, ErrorCode error, DataQuality quality);

    void UpdateConnectionState(string portName, ConnectionState state);

    void UpdateActiveAlarms(IReadOnlyDictionary<string, ErrorCode> alarms);
}
