using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Domain.ValueObjects;
using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Core.Domain.MachineState;

/// <summary>设备运行时状态容器: 聚合最新遥测、连接状态与活跃报警, 作为 UI 的数据绑定源</summary>
/// <remarks>
/// 写入方为串口读循环所在的后台线程, 读取方为 UI 线程。
/// <see cref="PropertyChanged"/> 在写入线程上同步触发(不经 Dispatcher), 因此这里只承载标量属性;
/// 集合类数据通过 <see cref="GetActiveAlarms"/> / <see cref="GetConnectedPorts"/> 拉取副本, 不做增量通知。
/// </remarks>
public class DeviceStateContainer : IDeviceStateContainer
{
    private readonly ConcurrentDictionary<string, DeviceStateSnapshot> _deviceStates = new();
    private readonly ConcurrentDictionary<string, ErrorCode> _activeAlarms = new();
    private readonly HashSet<string> _connectedPorts = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public Temperature LatestTemperature { get; private set; } = Temperature.FromCelsius(25.0);
    public Humidity LatestHumidity { get; private set; }
    public DeviceStatus LatestDeviceStatus { get; private set; } = DeviceStatus.Online;
    public ErrorCode LatestErrorCode { get; private set; }
    public DataQuality LatestQuality { get; private set; } = DataQuality.Good;
    public DateTime LatestTimestamp { get; private set; } = DateTime.Now;
    public string LatestPortName { get; private set; } = string.Empty;

    public IReadOnlyDictionary<string, ErrorCode> GetActiveAlarms() =>
        new Dictionary<string, ErrorCode>(_activeAlarms);

    public bool HasActiveAlarms => !_activeAlarms.IsEmpty;

    private readonly object _connectionLock = new();

    public bool IsPortConnected(string portName)
    {
        lock (_connectionLock) return _connectedPorts.Contains(portName);
    }

    public IReadOnlySet<string> GetConnectedPorts()
    {
        lock (_connectionLock)
        {
            return new HashSet<string>(_connectedPorts);
        }
    }

    public void UpdateTelemetry(string portName, Temperature temperature, Humidity humidity,
        DeviceStatus status, ErrorCode error, DataQuality quality)
    {
        var snapshot = new DeviceStateSnapshot(portName, temperature, humidity,
            status, error, quality, DateTime.Now);

        _deviceStates[portName] = snapshot;

        // Latest* 是最后写入者胜出的全局视图, 多端口同时上报时不保证属于同一端口,
        // 需要按端口区分的场景要查 _deviceStates 快照
        LatestTemperature = temperature;
        LatestHumidity = humidity;
        LatestDeviceStatus = status;
        LatestErrorCode = error;
        LatestQuality = quality;
        LatestTimestamp = snapshot.Timestamp;
        LatestPortName = portName;

        Notify(nameof(LatestTemperature));
        Notify(nameof(LatestHumidity));
        Notify(nameof(LatestDeviceStatus));
        Notify(nameof(LatestErrorCode));
        Notify(nameof(LatestQuality));
        Notify(nameof(LatestTimestamp));
        Notify(nameof(LatestPortName));
    }

    public void UpdateConnectionState(string portName, ConnectionState state)
    {
        lock (_connectionLock)
        {
            if (state == ConnectionState.Connected)
                _connectedPorts.Add(portName);
            else
                _connectedPorts.Remove(portName);
        }
        // 这两个成员为方法, 通知名取 nameof(方法名), 订阅方按同名过滤
        Notify(nameof(GetConnectedPorts));
        Notify(nameof(IsPortConnected));
    }

    public void UpdateActiveAlarms(IReadOnlyDictionary<string, ErrorCode> alarms)
    {
        // Clear 与逐项写入不是原子操作, 并发调用 GetActiveAlarms 可能读到中间态
        // 现有唯一调用方在报警事件线程上串行触发, 暂未加锁; 若引入第二个写入方需改为整体替换
        _activeAlarms.Clear();
        foreach (var kv in alarms)
            _activeAlarms.TryAdd(kv.Key, kv.Value);

        Notify(nameof(GetActiveAlarms));
        Notify(nameof(HasActiveAlarms));
    }
}
