using SmartEdgeHMI.Protocols.Parsers.Modbus;

namespace SmartEdgeHMI.Tests.Communication.Protocols;

/// <summary>CRC16 半字节查表的形态与取值守卫</summary>
/// <remarks>表的取值是 Modbus 互操作契约的一部分, 任何改动都会让本机与设备算出的 CRC 不一致</remarks>
public class CRC16TableTests
{
    [Fact]
    public void CrcTlb_ShouldHaveExactly16Entries()
    {
        Assert.Equal(16, Crc16Table.CrcTlb.Length);
    }

    /// <summary>首项为 0 是反射多项式表的固有性质(输入 0 不产生扰动)</summary>
    [Fact]
    public void CrcTlb_FirstEntry_ShouldBeZero()
    {
        Assert.Equal(0x0000, Crc16Table.CrcTlb[0]);
    }

    [Fact]
    public void CrcTlb_KnownEntries_ShouldMatchExpected()
    {
        Assert.Equal(0x0000, Crc16Table.CrcTlb[0]);
        Assert.Equal(0xCC01, Crc16Table.CrcTlb[1]);
        Assert.Equal(0xD801, Crc16Table.CrcTlb[2]);
        Assert.Equal(0x1400, Crc16Table.CrcTlb[3]);
        Assert.Equal(0xF001, Crc16Table.CrcTlb[4]);
    }
}
