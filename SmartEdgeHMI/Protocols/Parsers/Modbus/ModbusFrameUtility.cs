using System.Buffers;
using System.Buffers.Binary;
using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.Protocols.Parsers.Modbus;

/// <summary>Modbus RTU 帧的构建与长度推导, 无状态, 可在任意线程调用</summary>
public static class ModbusFrameUtility
{
    /// <summary>计算 Modbus RTU 的 CRC16(初值 0xFFFF, 反射多项式 0xA001), 结果按小端填入帧尾</summary>
    public static ushort CalcCRC16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            byte ch = data[i];
            // 半字节查表: 两次 4 bit 查表替代 8 次移位异或, 避免逐位运算
            crc = (ushort)(Crc16Table.CrcTlb[(ch ^ crc) & 0x0F] ^ (crc >> 4));
            crc = (ushort)(Crc16Table.CrcTlb[((ch >> 4) ^ crc) & 0x0F] ^ (crc >> 4));
        }
        return crc;
    }

    /// <summary>构建一帧 RTU 请求: 从站地址 + 功能码 + 数据 + CRC16</summary>
    /// <returns>ArrayPool 租用的缓冲区, 长度可能大于帧长; <b>调用方负责归还</b></returns>
    /// <remarks>归还前不得让缓冲区逃逸到其他线程, 否则会与下一次 Rent 复用同一块内存</remarks>
    public static byte[] BuildFrame(byte slaveAddr, byte funcCode, ReadOnlySpan<byte> data)
    {
        int frameLen = 1 + 1 + data.Length + 2;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(frameLen);
        buffer[0] = slaveAddr;
        buffer[1] = funcCode;
        data.CopyTo(buffer.AsSpan(2));
        // RTU 的 CRC 低字节在前
        ushort crc = CalcCRC16(buffer.AsSpan(0, frameLen - 2));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(frameLen - 2), crc);
        return buffer;
    }

    /// <summary>按功能码推导完整响应帧长度</summary>
    /// <param name="dataByte">帧中第 3 字节: 读类功能码为字节数(ByteCount), 写类功能码为寄存器地址高字节</param>
    /// <returns>期望长度; 无法识别的功能码返回 -1, 调用方据此跳过 1 字节重新找帧头</returns>
    public static int GetExpectedFrameLength(byte dataByte, byte functionCode)
    {
        // 最高位置 1 表示异常响应, 长度固定为 5 字节, 与异常码无关
        if (functionCode > 0x80) return AppConstants.ModbusExceptionFrameLength;
        return functionCode switch
        {
            (byte)ModbusFunctionCode.ReadHoldingRegisters or
            (byte)ModbusFunctionCode.ReadInputRegisters => 1 + 1 + 1 + dataByte + 2,
            (byte)ModbusFunctionCode.WriteSingleRegister or
            (byte)ModbusFunctionCode.WriteMultipleRegisters => AppConstants.ModbusWriteFrameLength,
            _ => -1
        };
    }
}
