namespace SmartEdgeHMI.Common;

/// <summary>跨模块共享的常量</summary>
public static class AppConstants
{
    /// <summary>串口波特率候选值, 对应界面下拉框的展示顺序</summary>
    public static readonly string[] StandardBaudRates =
    [
        "4800", "9600", "19200", "38400", "57600", "115200"
    ];

    /// <summary>界面改阈值后的落盘防抖窗口, 单位 ms</summary>
    public const int SettingsSaveDebounceMs = 3000;

    /// <summary>Modbus 异常响应帧长度: 从站地址 + 功能码|0x80 + 异常码 + CRC16</summary>
    public const int ModbusExceptionFrameLength = 5;

    /// <summary>FC06 写单寄存器响应帧长度: 地址 + 功能码 + 寄存器地址 + 写入值 + CRC16</summary>
    public const int ModbusWriteFrameLength = 8;

    /// <summary>Pipe 最小段大小, 单位字节; 与串口单次读取上限匹配, 减少分段拷贝</summary>
    public const int PipeMinimumSegmentSize = 4096;

    /// <summary>FC03 轮询起始寄存器地址</summary>
    /// <remarks>
    /// 寄存器映射由固件定义: 0 温度(x10), 1 湿度(x10), 2 设备状态, 3 错误码, 4 LED 状态。
    /// 本机只轮询 0~3, 读 4 个寄存器即覆盖遥测与故障码。
    /// </remarks>
    public const ushort ModbusPollStartAddress = 0;

    /// <summary>FC03 单次轮询的寄存器数量</summary>
    public const ushort ModbusPollRegisterCount = 4;

    /// <summary>默认设备标识, 单设备部署下作为所有落库记录与命令的 DeviceId</summary>
    public const string DefaultDeviceName = "Sensor_01";

    /// <summary>默认 Modbus 从站地址, 与固件 SLAVE_ID 默认值一致</summary>
    public const byte DefaultModbusSlaveAddress = 1;
}
