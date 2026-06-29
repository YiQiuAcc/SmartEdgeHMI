using System.ComponentModel;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.Models.ValueObjects;

namespace SmartEdgeHMI.MachineState;

/// <summary>设备运行时状态容器: 聚合最新遥测值、连接状态和活跃报警, 提供 UI 绑定数据源</summary>
public interface IDeviceStateContainer : INotifyPropertyChanged
{
    /// <summary>最新温度值</summary>
    Temperature LatestTemperature { get; }

    /// <summary>最新湿度值</summary>
    Humidity LatestHumidity { get; }

    /// <summary>最新设备状态</summary>
    DeviceStatus LatestDeviceStatus { get; }

    /// <summary>最新设备错误码</summary>
    ErrorCode LatestErrorCode { get; }

    /// <summary>最新数据质量标记</summary>
    DataQuality LatestQuality { get; }

    /// <summary>最新遥测时间戳</summary>
    DateTime LatestTimestamp { get; }

    /// <summary>最新源端口名</summary>
    string LatestPortName { get; }

    /// <summary>获取当前活跃报警字典(深拷贝)，避免外部修改内部状态</summary>
    IReadOnlyDictionary<string, ErrorCode> GetActiveAlarms();

    /// <summary>是否存在活跃报警</summary>
    bool HasActiveAlarms { get; }

    /// <summary>检查指定端口是否已连接</summary>
    bool IsPortConnected(string portName);

    /// <summary>获取已连接端口集合(深拷贝)，避免外部修改内部状态</summary>
    IReadOnlySet<string> GetConnectedPorts();

    /// <summary>更新遥测数据并触发属性变更通知</summary>
    void UpdateTelemetry(string portName, Temperature temperature, Humidity humidity,
        DeviceStatus status, ErrorCode error, DataQuality quality);

    /// <summary>更新端口连接状态</summary>
    void UpdateConnectionState(string portName, ConnectionState state);

    /// <summary>替换活跃报警字典</summary>
    void UpdateActiveAlarms(IReadOnlyDictionary<string, ErrorCode> alarms);
}
