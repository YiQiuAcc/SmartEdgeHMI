namespace SmartEdgeHMI.Common;

public static class AppConstants
{
    // 串口通信配置
    public static readonly string[] StandardBaudRates =
    [
        "4800", "9600", "19200", "38400", "57600", "115200"
    ];

    // 系统时间参数
    public const int SettingsSaveDebounceMs = 3000;

    // Modbus 协议标准报文长度
    public const int ModbusExceptionFrameLength = 5;
    public const int ModbusWriteFrameLength = 8;

    // 底层 IO 管道优化参数
    public const int PipeMinimumSegmentSize = 4096;

    // Modbus 通信默认起始地址和寄存器数量
    public const ushort ModbusPollStartAddress = 0;
    public const ushort ModbusPollRegisterCount = 4;

    // 默认设备配置
    public const string DefaultDeviceName = "Sensor_01";
    public const byte DefaultModbusSlaveAddress = 1;
}
