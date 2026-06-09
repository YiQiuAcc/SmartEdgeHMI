using System.Buffers;
using SmartEdgeHMI.Constants;
using SmartEdgeHMI.Models.Enums;

namespace SmartEdgeHMI.Services;

public class ModbusService(ISerialPortService serialPortService)
{
    private readonly ISerialPortService _serialPortService = serialPortService;

    /// <summary>计算 Modbus RTU CRC16 校验码 (半字节查表法 / 零分配)</summary>
    public static ushort CalcCRC16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            byte ch = data[i];
            crc = (ushort)(CRC16Table.CrcTlb[(ch ^ crc) & 0x0F] ^ (crc >> 4));
            crc = (ushort)(CRC16Table.CrcTlb[((ch >> 4) ^ crc) & 0x0F] ^ (crc >> 4));
        }
        return crc;
    }

    /// <summary>发送通用 Modbus RTU 帧 (物理层只认端口名)</summary>
    public Task SendModbusCommandAsync(string portName, byte slaveAddress, byte functionCode, ReadOnlySpan<byte> data)
    {
        int frameLength = 1 + 1 + data.Length + 2;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(frameLength);
        buffer[0] = slaveAddress;
        buffer[1] = functionCode;
        data.CopyTo(buffer.AsSpan(2, data.Length));

        ushort crc = CalcCRC16(buffer.AsSpan(0, frameLength - 2));
        buffer[frameLength - 2] = (byte)(crc & 0xFF);
        buffer[frameLength - 1] = (byte)((crc >> 8) & 0xFF);

        return WriteAndReturnBufferAsync(portName, buffer, frameLength);
    }

    private async Task WriteAndReturnBufferAsync(string portName, byte[] buffer, int length)
    {
        try
        {
            await _serialPortService.WriteBytesAsync(portName, buffer, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>写单个保持寄存器</summary>
    public Task WriteSingleRegisterAsync(string portName, byte slaveAddress, ushort registerAddress, ushort value)
    {
        Span<byte> data = [
            (byte)(registerAddress >> 8), // 寄存器地址高位
            (byte)registerAddress,        // 寄存器地址低位
            (byte)(value >> 8),           // 写入数值高位
            (byte)value                   // 写入数值低位
        ];
        return SendModbusCommandAsync(portName, slaveAddress, (byte)ModbusFunctionCode.WriteSingleRegister, data);
    }
}
