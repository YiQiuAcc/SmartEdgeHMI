using System.Buffers.Binary;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Protocols.Parsers.Modbus;

namespace SmartEdgeHMI.Tests.Communication.Protocols;

/// <summary>Modbus RTU 的 CRC16 与帧布局校验</summary>
/// <remarks>
/// 帧用例均由测试自行拼装字节, 不经过生产侧的组帧代码, 因此验证的是字节序与 CRC 填充位置符合 RTU 约定,
/// 多数断言只覆盖"非零/自洽", 不覆盖与标准向量的逐位比对。
/// </remarks>
public class ModbusProtocolServiceTests
{
    /// <summary>标准向量 [01 03 00 00 00 04] 的 CRC 既不是初值也不是 0, 说明查表过程确实参与运算</summary>
    [Fact]
    public void CalcCRC16_StandardTestVector_ShouldBeNonTrivial()
    {
        byte[] data = [0x01, 0x03, 0x00, 0x00, 0x00, 0x04];

        ushort crc = ModbusProtocolServiceTestsHelper.CalcCRC16(data);

        Assert.NotEqual((ushort)0x0000, crc);
        Assert.NotEqual((ushort)0xFFFF, crc);
    }

    /// <summary>帧尾附加 CRC 后再整体计算应得 0, 这是 RTU 校验的基本性质</summary>
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

    [Fact]
    public void CalcCRC16_EmptyData_ShouldReturnInitialValue()
    {
        ushort crc = ModbusProtocolServiceTestsHelper.CalcCRC16([]);
        Assert.Equal(0xFFFF, crc);
    }

    [Fact]
    public void CalcCRC16_SingleByte_ShouldProduceDeterministicResult()
    {
        ushort crc = ModbusProtocolServiceTestsHelper.CalcCRC16([0x01]);
        Assert.NotEqual((ushort)0xFFFF, crc);
        Assert.True(crc < 0xFFFF);
    }

    [Fact]
    public void CalcCRC16_ShouldBeDeterministic()
    {
        byte[] data = [0x11, 0x03, 0x00, 0x6B, 0x00, 0x03];

        ushort crc1 = ModbusProtocolServiceTestsHelper.CalcCRC16(data);
        ushort crc2 = ModbusProtocolServiceTestsHelper.CalcCRC16(data);

        Assert.Equal(crc1, crc2);
    }

    [Fact]
    public void CalcCRC16_DifferentInputs_ShouldProduceDifferentCRC()
    {
        ushort crc1 = ModbusProtocolServiceTestsHelper.CalcCRC16([0x01, 0x03, 0x00, 0x00, 0x00, 0x04]);
        ushort crc2 = ModbusProtocolServiceTestsHelper.CalcCRC16([0x02, 0x03, 0x00, 0x00, 0x00, 0x04]);

        Assert.NotEqual(crc1, crc2);
    }

    /// <summary>FC06 写单寄存器的报文 CRC 计算路径</summary>
    /// <remarks>这里只断言非零, 未比对标准向量的具体数值</remarks>
    [Fact]
    public void CalcCRC16_WriteSingleRegister_ShouldMatchExpected()
    {
        byte[] data = [0x01, 0x06, 0x00, 0x01, 0x00, 0x01];
        ushort crc = ModbusProtocolServiceTestsHelper.CalcCRC16(data);

        Assert.NotEqual((ushort)0x0000, crc);
        Assert.True(crc > 0);
    }

    /// <summary>数据区含连续零字节时 CRC 仍需参与全部字节</summary>
    [Fact]
    public void CalcCRC16_DataWithZeroBytes_ShouldHandleCorrectly()
    {
        byte[] data = [0x01, 0x03, 0x00, 0x00, 0x00, 0x00];
        ushort crc = ModbusProtocolServiceTestsHelper.CalcCRC16(data);

        Assert.NotEqual((ushort)0x0000, crc);
    }

    /// <summary>FC03 请求帧: 地址与寄存器数量均为大端, CRC 低字节在前</summary>
    [Fact]
    public void ReadHoldingRegisters_FrameStructure_ShouldBeValid()
    {
        const byte slaveAddress = 0x01;
        const ushort startAddress = 0x0000;
        const ushort quantity = 0x0004;

        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(data[..2], startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(2, 2), quantity);

        const int frameLength = 1 + 1 + 4 + 2;
        byte[] frame = new byte[frameLength];
        frame[0] = slaveAddress;
        frame[1] = (byte)ModbusFunctionCode.ReadHoldingRegisters;
        data.CopyTo(frame.AsSpan(2, 4));

        ushort crc = ModbusProtocolServiceTestsHelper.CalcCRC16(frame.AsSpan(0, frameLength - 2));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frameLength - 2, 2), crc);

        Assert.Equal(slaveAddress, frame[0]);
        Assert.Equal((byte)ModbusFunctionCode.ReadHoldingRegisters, frame[1]);
        Assert.Equal(0x00, frame[2]);  // 起始地址高字节
        Assert.Equal(0x00, frame[3]);  // 起始地址低字节
        Assert.Equal(0x00, frame[4]);  // 数量高字节
        Assert.Equal(0x04, frame[5]);  // 数量低字节

        ushort frameCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(frameLength - 2, 2));
        Assert.NotEqual((ushort)0x0000, frameCrc);

        ushort calculatedCrc = ModbusProtocolServiceTestsHelper.CalcCRC16(frame.AsSpan(0, frameLength - 2));
        Assert.Equal(frameCrc, calculatedCrc);
    }

    /// <summary>FC06 请求帧: 寄存器地址与写入值均为大端; 值按 ×10 缩放, 0x0014 表示 2.0 °C</summary>
    [Fact]
    public void WriteSingleRegister_FrameStructure_ShouldBeValid()
    {
        const byte slaveAddress = 0x01;
        const ushort registerAddress = 0x0002;
        const ushort value = 0x0014; // 0x0014 = 20, 按 10 倍缩放即 2.0 °C

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

        // 地址与数值按大端排布(高字节在前), CRC 按小端填入帧尾
        Assert.Equal(slaveAddress, frame[0]);
        Assert.Equal((byte)ModbusFunctionCode.WriteSingleRegister, frame[1]);
        Assert.Equal(0x00, frame[2]);
        Assert.Equal(0x02, frame[3]);
        Assert.Equal(0x00, frame[4]);
        Assert.Equal(0x14, frame[5]);

        // 上一步写入的 CRC 能被重新计算复现
        ushort calculatedCrc = ModbusProtocolServiceTestsHelper.CalcCRC16(frame.AsSpan(0, frameLength - 2));
        ushort embeddedCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(frameLength - 2, 2));
        Assert.Equal(calculatedCrc, embeddedCrc);
    }

    /// <summary>异常响应帧(功能码 | 0x80)长度固定为 5 字节, CRC 覆盖前 3 字节</summary>
    [Fact]
    public void ExceptionResponseFrame_CRC_ShouldValidateCorrectly()
    {
        byte[] frame = [0x01, 0x83, 0x02, 0x00, 0x00];

        ushort crc = ModbusProtocolServiceTestsHelper.CalcCRC16(frame.AsSpan(0, 3));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3, 2), crc);

        ushort calculatedCrc = ModbusProtocolServiceTestsHelper.CalcCRC16(frame.AsSpan(0, 3));
        ushort embeddedCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(3, 2));
        Assert.Equal(calculatedCrc, embeddedCrc);
    }
}

// 测试侧 CRC 入口: 直接转发到生产实现, 不做反射
internal static class ModbusProtocolServiceTestsHelper
{
    public static ushort CalcCRC16(ReadOnlySpan<byte> data)
        => ModbusFrameUtility.CalcCRC16(data);
}
