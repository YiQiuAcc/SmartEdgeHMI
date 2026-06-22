namespace SmartEdgeHMI.Common;

public enum DeviceStatus : byte
{
    Offline = 0,
    Online = 1,
    Stopped = 2,
    Maintenance = 3,
    Fault = 4
}

public enum DataQuality : byte
{
    Good = 0,
    Uncertain = 1,
    Bad = 2
}

public enum ErrorCode : ushort
{
    NoError = 0,
    Timeout = 101,
    ChecksumFailed = 102,
    Unauthorized = 103,
    SensorDisconnected = 201,
    PowerLow = 202,
    NotConfigured = 301,
    ThresholdExceeded = 302
}

public enum DeviceAction : byte
{
    None = 0,
    Start = 1,
    Stop = 2,
    Reset = 3,
    Configure = 4,
    TriggerSample = 5
}

public enum CommunicationProtocol : byte
{
    JSON = 0,
    Modbus = 1
}

public enum AlarmState : byte
{
    UNACK = 0,
    ACK = 1,
    RTN_UNACK = 2,
    NORMAL = 3
}

public enum ModbusFunctionCode : byte
{
    ReadCoils = 1,
    ReadDiscreteInputs = 2,
    ReadHoldingRegisters = 3,
    ReadInputRegisters = 4,
    WriteSingleCoil = 5,
    WriteSingleRegister = 6,
    WriteMultipleCoils = 15,
    WriteMultipleRegisters = 16
}
