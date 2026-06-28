namespace SmartEdgeHMI.Common;

public class ConnectionSettings
{
    public string ComPort { get; set; } = string.Empty;
    public int BaudRate { get; set; } = 115200;
}

public class ModbusSettings
{
    public byte SlaveAddress { get; set; } = 1;
}

public class UISettings
{
    public int ChartRefreshRateMs { get; set; } = 33;
}

public class HardwareSettings
{
    public double DefaultThreshold { get; set; } = 100.0;
}

public class AppSettings
{
    public ConnectionSettings Connection { get; set; } = new();
    public ModbusSettings Modbus { get; set; } = new();
    public UISettings UI { get; set; } = new();
    public HardwareSettings Hardware { get; set; } = new();
}
