using System.ComponentModel;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Domain.ValueObjects;
using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Core.Domain.MachineState;

/// <summary>设备运行时状态容器: 聚合最新遥测、连接状态与活跃报警, 作为 UI 的数据绑定源</summary>
/// <remarks>
/// 写入来自串口读循环的后台线程, 读取在 UI 线程; 实现须保证二者并发安全。
/// <see cref="INotifyPropertyChanged.PropertyChanged"/> 在写入线程上触发, 集合数据以副本形式拉取。
/// </remarks>
public interface IDeviceStateContainer : INotifyPropertyChanged
{
    Temperature LatestTemperature { get; }

    Humidity LatestHumidity { get; }

    DeviceStatus LatestDeviceStatus { get; }

    ErrorCode LatestErrorCode { get; }

    DataQuality LatestQuality { get; }

    DateTime LatestTimestamp { get; }

    /// <summary>最近一次上报数据的源端口, 用于定位是哪一路设备在驱动界面显示</summary>
    string LatestPortName { get; }

    /// <summary>当前活跃报警的独立副本(设备ID, 错误码), 调用方修改不影响内部状态</summary>
    IReadOnlyDictionary<string, ErrorCode> GetActiveAlarms();

    /// <summary>是否存在活跃报警, 用于界面告警条显隐</summary>
    bool HasActiveAlarms { get; }

    /// <summary>指定端口当前是否处于 Connected 状态</summary>
    bool IsPortConnected(string portName);

    /// <summary>已连接端口的独立副本</summary>
    IReadOnlySet<string> GetConnectedPorts();

    /// <summary>写入一次遥测并触发属性变更通知</summary>
    /// <remarks>可在任意线程调用; 多端口并发上报时 Latest* 为最后写入者胜出</remarks>
    void UpdateTelemetry(string portName, Temperature temperature, Humidity humidity,
        DeviceStatus status, ErrorCode error, DataQuality quality);

    /// <summary>更新端口连接状态, 除 Connected 外均视为断开</summary>
    void UpdateConnectionState(string portName, ConnectionState state);

    /// <summary>整体替换活跃报警表</summary>
    void UpdateActiveAlarms(IReadOnlyDictionary<string, ErrorCode> alarms);
}
