namespace SmartEdgeHMI.Protocols.Parsers.Modbus;

/// <summary>CRC16(Modbus) 半字节查表: 16 项, 对应反射多项式 0xA001 的 4 bit 变换结果</summary>
/// <remarks>配合 <c>ModbusFrameUtility.CalcCRC16</c> 使用; 表内容由多项式生成, 不作手工修改</remarks>
public static class Crc16Table
{
    public static readonly ushort[] CrcTlb = [
        0x0000, 0xCC01, 0xD801, 0x1400, 0xF001, 0x3C00, 0x2800, 0xE401,
        0xA001, 0x6C00, 0x7800, 0xB401, 0x5000, 0x9C01, 0x8801, 0x4400
    ];
}
