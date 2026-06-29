using System.Buffers;
using System.Buffers.Binary;
using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.Protocols.Parsers.Modbus;

/// <summary>Modbus 帧构建与校验的纯静态工具方法</summary>
public static class ModbusFrameUtility
{
    public static ushort CalcCRC16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            byte ch = data[i];
            crc = (ushort)(Crc16Table.CrcTlb[(ch ^ crc) & 0x0F] ^ (crc >> 4));
            crc = (ushort)(Crc16Table.CrcTlb[((ch >> 4) ^ crc) & 0x0F] ^ (crc >> 4));
        }
        return crc;
    }

    /// <summary>构建 Modbus RTU 帧 (从机地址 + 功能码 + 数据 + CRC), 返回 ArrayPool 租赁的缓冲区</summary>
    public static byte[] BuildFrame(byte slaveAddr, byte funcCode, ReadOnlySpan<byte> data)
    {
        int frameLen = 1 + 1 + data.Length + 2;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(frameLen);
        buffer[0] = slaveAddr;
        buffer[1] = funcCode;
        data.CopyTo(buffer.AsSpan(2));
        ushort crc = CalcCRC16(buffer.AsSpan(0, frameLen - 2));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(frameLen - 2), crc);
        return buffer;
    }

    /// <summary>根据功能码和数据字节计算期望的响应帧长度</summary>
    public static int GetExpectedFrameLength(byte dataByte, byte functionCode)
    {
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
