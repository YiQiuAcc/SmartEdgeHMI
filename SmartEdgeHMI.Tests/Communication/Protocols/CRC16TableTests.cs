using SmartEdgeHMI.Communication.Protocols.Utils;

namespace SmartEdgeHMI.Tests.Communication.Protocols;

public class CRC16TableTests
{
    /// <summary>CRC16 查找表应有 16 个预计算项(半字节查表法)</summary>
    [Fact]
    public void CrcTlb_ShouldHaveExactly16Entries()
    {
        Assert.Equal(16, Crc16Table.CrcTlb.Length);
    }

    /// <summary>验证查找表的已知值: 标准 Modbus CRC16 半字节表的前半部分</summary>
    [Fact]
    public void CrcTlb_FirstEntry_ShouldBeZero()
    {
        Assert.Equal(0x0000, Crc16Table.CrcTlb[0]);
    }

    [Fact]
    public void CrcTlb_KnownEntries_ShouldMatchExpected()
    {
        // 验证几个已知的常量值
        Assert.Equal(0x0000, Crc16Table.CrcTlb[0]);
        Assert.Equal(0xCC01, Crc16Table.CrcTlb[1]);
        Assert.Equal(0xD801, Crc16Table.CrcTlb[2]);
        Assert.Equal(0x1400, Crc16Table.CrcTlb[3]);
        Assert.Equal(0xF001, Crc16Table.CrcTlb[4]);
    }
}
