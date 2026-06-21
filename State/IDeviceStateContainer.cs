using System.ComponentModel;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.Models.ValueObjects;

namespace SmartEdgeHMI.State;

public interface IDeviceStateContainer : INotifyPropertyChanged
{
    Temperature LatestTemperature { get; }
    Humidity LatestHumidity { get; }
    DeviceStatus LatestDeviceStatus { get; }
    ErrorCode LatestErrorCode { get; }
    DataQuality LatestQuality { get; }
    DateTime LatestTimestamp { get; }
    string LatestPortName { get; }

    DeviceStateSnapshot? GetDeviceState(string portName);

    IReadOnlyDictionary<string, ErrorCode> ActiveAlarms { get; }
    bool HasActiveAlarms { get; }

    bool IsPortConnected(string portName);
    IReadOnlySet<string> ConnectedPorts { get; }

    void UpdateTelemetry(string portName, Temperature temperature, Humidity humidity,
        DeviceStatus status, ErrorCode error, DataQuality quality);

    void UpdateConnectionState(string portName, ConnectionState state);

    void UpdateActiveAlarms(IReadOnlyDictionary<string, ErrorCode> alarms);
}
