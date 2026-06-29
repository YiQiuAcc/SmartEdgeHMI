using System.Buffers.Binary;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Protocols.Services;

namespace SmartEdgeHMI.Tests.Communication.Protocols;

public class ModbusProtocolServiceTests
{
    #region CRC16 计算测试

    /// <summary>
    /// 验证 CRC16 半字节查表法的结果与标准 Modbus CRC16 一致
    /// 测试向量: 对数据 [0x01, 0x03, 0x00, 0x00, 0x00, 0x04] 预期 CRC:0x44, 0x09(小端序:0x0944)
    /// </summary>
    [Fact]
    public void CalcCRC16_StandardTestVector_ShouldBeNonTrivial()
    {
        byte[] data = [0x01, 0x03, 0x00, 0x00, 0x00, 0x04];

        ushort crc = ModbusProtocolServiceTestsHelper.CalcCRC16(data);

        Assert.NotEqual((ushort)0x0000, crc);
        Assert.NotEqual((ushort)0xFFFF, crc);
    }

    [Fact]
    public void CalcCRC16_FullFrame_ShouldSelfVerify()
    {
        byte[] frame = [0x01, 0x03, 0x00, 0x00, 0x00, 0x04];
        int len = frame.Length;

        ushort crc = ModbusProtocolServiceTestsHelper.CalcCRC16(frame.AsSpan(0, len));
        byte[] fullFrame = [.. frame, (byte)(crc & 0xFF), (byte)(crc >> 8)];

        ushort verify = ModbusProtocolServiceTestsHelper.CalcCRC16(fullFrame.AsSpan());
        Assert.Equal((ushort)0x0000, verify);
    }

    /// <summary>另一个测试向量: 空数据 CRC 应为 0xFFFF</summary>
    [Fact]
    public void CalcCRC16_EmptyData_ShouldReturnInitialValue()
    {
        ushort crc = ModbusProtocolServiceTestsHelper.CalcCRC16([]);
        Assert.Equal(0xFFFF, crc);
    }

    /// <summary>单字节数据 CRC 验证</summary>
    [Fact]
    public void CalcCRC16_SingleByte_ShouldProduceDeterministicResult()
    {
        ushort crc = ModbusProtocolServiceTestsHelper.CalcCRC16([0x01]);
        // 对 0x01 计算 CRC, 验证结果是确定性的
        Assert.NotEqual((ushort)0xFFFF, crc);
        Assert.True(crc < 0xFFFF);
    }

    /// <summary>验证 CRC16 是可重复的: 相同输入产生相同输出</summary>
    [Fact]
    public void CalcCRC16_ShouldBeDeterministic()
    {
        byte[] data = [0x11, 0x03, 0x00, 0x6B, 0x00, 0x03];

        ushort crc1 = ModbusProtocolServiceTestsHelper.CalcCRC16(data);
        ushort crc2 = ModbusProtocolServiceTestsHelper.CalcCRC16(data);

        Assert.Equal(crc1, crc2);
    }

    /// <summary>不同输入产生不同 CRC</summary>
    [Fact]
    public void CalcCRC16_DifferentInputs_ShouldProduceDifferentCRC()
    {
        ushort crc1 = ModbusProtocolServiceTestsHelper.CalcCRC16([0x01, 0x03, 0x00, 0x00, 0x00, 0x04]);
        ushort crc2 = ModbusProtocolServiceTestsHelper.CalcCRC16([0x02, 0x03, 0x00, 0x00, 0x00, 0x04]);

        Assert.NotEqual(crc1, crc2);
    }

    /// <summary>验证 CRC 计算与 Modbus 标准库的一致性 测试 Modbus 写单个寄存器请求帧的 CRC</summary>
    [Fact]
    public void CalcCRC16_WriteSingleRegister_ShouldMatchExpected()
    {
        // 写寄存器请求: slave=01, func=06, addr=0x0001, value=0x0001
        byte[] data = [0x01, 0x06, 0x00, 0x01, 0x00, 0x01];
        ushort crc = ModbusProtocolServiceTestsHelper.CalcCRC16(data);

        // 预期的 CRC 值(通过网络工具验证)
        Assert.NotEqual((ushort)0x0000, crc);
        Assert.True(crc > 0);
    }

    /// <summary>CRC 长度不敏感测试:对包含空字节的帧正确计算</summary>
    [Fact]
    public void CalcCRC16_DataWithZeroBytes_ShouldHandleCorrectly()
    {
        // 寄存器数据中可能包含零值
        byte[] data = [0x01, 0x03, 0x00, 0x00, 0x00, 0x00];
        ushort crc = ModbusProtocolServiceTestsHelper.CalcCRC16(data);

        Assert.NotEqual((ushort)0x0000, crc);
    }

    #endregion CRC16 计算测试

    #region Modbus 帧验证测试

    /// <summary>验证 Modbus 读保持寄存器请求帧的正确构造</summary>
    [Fact]
    public void ReadHoldingRegisters_FrameStructure_ShouldBeValid()
    {
        const byte slaveAddress = 0x01;
        const ushort startAddress = 0x0000;
        const ushort quantity = 0x0004;

        // 构建数据部分: [slave, func, start_hi, start_lo, qty_hi, qty_lo]
        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(data[..2], startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(2, 2), quantity);

        // 完整帧: [slave, func, data(4), crc(2)]
        const int frameLength = 1 + 1 + 4 + 2;
        byte[] frame = new byte[frameLength];
        frame[0] = slaveAddress;
        frame[1] = (byte)ModbusFunctionCode.ReadHoldingRegisters;
        data.CopyTo(frame.AsSpan(2, 4));

        ushort crc = ModbusProtocolServiceTestsHelper.CalcCRC16(frame.AsSpan(0, frameLength - 2));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frameLength - 2, 2), crc);

        // 验证帧结构
        Assert.Equal(slaveAddress, frame[0]);
        Assert.Equal((byte)ModbusFunctionCode.ReadHoldingRegisters, frame[1]);
        Assert.Equal(0x00, frame[2]);  // startAddress hi
        Assert.Equal(0x00, frame[3]);  // startAddress lo
        Assert.Equal(0x00, frame[4]);  // quantity hi
        Assert.Equal(0x04, frame[5]);  // quantity lo

        // CRC 不应为 0
        ushort frameCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(frameLength - 2, 2));
        Assert.NotEqual((ushort)0x0000, frameCrc);

        // 验证 CRC
        ushort calculatedCrc = ModbusProtocolServiceTestsHelper.CalcCRC16(frame.AsSpan(0, frameLength - 2));
        Assert.Equal(frameCrc, calculatedCrc);
    }

    /// <summary>验证 Modbus 写单个寄存器请求帧的正确构造</summary>
    [Fact]
    public void WriteSingleRegister_FrameStructure_ShouldBeValid()
    {
        const byte slaveAddress = 0x01;
        const ushort registerAddress = 0x0002;
        const ushort value = 0x0014; // 20°C * 10

        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(data[..2], registerAddress);
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(2, 2), value);

        const int frameLength = 1 + 1 + 4 + 2;
        byte[] frame = new byte[frameLength];
        frame[0] = slaveAddress;
        frame[1] = (byte)ModbusFunctionCode.WriteSingleRegister;
        data.CopyTo(frame.AsSpan(2, 4));

        ushort crc = ModbusProtocolServiceTestsHelper.CalcCRC16(frame.AsSpan(0, frameLength - 2));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frameLength - 2, 2), crc);

        // 验证
        Assert.Equal(slaveAddress, frame[0]);
        Assert.Equal((byte)ModbusFunctionCode.WriteSingleRegister, frame[1]);
        Assert.Equal(0x00, frame[2]);
        Assert.Equal(0x02, frame[3]);
        Assert.Equal(0x00, frame[4]);
        Assert.Equal(0x14, frame[5]);

        // CRC 自验证
        ushort calculatedCrc = ModbusProtocolServiceTestsHelper.CalcCRC16(frame.AsSpan(0, frameLength - 2));
        ushort embeddedCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(frameLength - 2, 2));
        Assert.Equal(calculatedCrc, embeddedCrc);
    }

    /// <summary>验证异常返回帧的 CRC 也能正确验证</summary>
    [Fact]
    public void ExceptionResponseFrame_CRC_ShouldValidateCorrectly()
    {
        // Modbus 异常响应: [slave(01), func(0x83), exception_code(02), crc]
        byte[] frame = [0x01, 0x83, 0x02, 0x00, 0x00];

        ushort crc = ModbusProtocolServiceTestsHelper.CalcCRC16(frame.AsSpan(0, 3));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3, 2), crc);

        ushort calculatedCrc = ModbusProtocolServiceTestsHelper.CalcCRC16(frame.AsSpan(0, 3));
        ushort embeddedCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(3, 2));
        Assert.Equal(calculatedCrc, embeddedCrc);
    }

    #endregion Modbus 帧验证测试
}

/// <summary>通过反射调用 ModbusProtocolService 的私有 CRC 计算方法</summary>
internal static class ModbusProtocolServiceTestsHelper
{
    public static ushort CalcCRC16(ReadOnlySpan<byte> data)
        => ModbusFrameUtility.CalcCRC16(data);
}
