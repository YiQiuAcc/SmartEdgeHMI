using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SmartEdgeHMI.Constants;
using SmartEdgeHMI.Models;
using SmartEdgeHMI.Models.Enums;
using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Services;

public class ModbusProtocolService : IRecipient<RawDataReceivedMessage>, IDisposable
{
    private readonly ISerialPortService _serialPortService;
    private readonly ConcurrentDictionary<string, SlidingBuffer> _buffers = new();

    public ModbusProtocolService(ISerialPortService serialPortService)
    {
        _serialPortService = serialPortService;
        WeakReferenceMessenger.Default.RegisterAll(this);
    }

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

        // Modbus CRC 规定低字节在前, 高字节在后
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(frameLength - 2, 2), crc);

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
        // 栈上分配
        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(0, 2), registerAddress);
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(2, 2), value);

        return SendModbusCommandAsync(portName, slaveAddress, (byte)ModbusFunctionCode.WriteSingleRegister, data);
    }

    public void Receive(RawDataReceivedMessage message)
    {
        if (message.Data.Length == 0) return;

        var buffer = _buffers.GetOrAdd(message.PortName, _ => new SlidingBuffer());

        // 锁定当前端口的缓冲区, 防止并发读写引发竞态条件
        lock (buffer)
        {
            buffer.Append(message.Data);
            ProcessBuffer(buffer);
        }
    }

    private static void ProcessBuffer(SlidingBuffer buffer)
    {
        // Modbus RTU 极短的合法报文是 5 个字节
        while (buffer.Length >= 5)
        {
            ReadOnlySpan<byte> currentSpan = buffer.UnreadSpan;

            // 第一字节：从站地址 (假设只处理地址为 1 的设备)
            if (currentSpan[0] != 0x01)
            {
                buffer.Consume(1); // 丢弃脏字节, 窗口滑动
                continue;
            }

            byte functionCode = currentSpan[1];
            int expectedLength = GetExpectedFrameLength(currentSpan, functionCode);

            // -1: 无法解析的功能码
            if (expectedLength == -1)
            {
                buffer.Consume(1);
                continue;
            }

            // 0: 变长报文, 长度字节尚未收到, 继续等待
            if (expectedLength == 0) break;

            // 预测出长度, 但当前积累的字节不够, 退出等待完整包 (解决断包)
            if (buffer.Length < expectedLength) break;

            // 零分配：直接通过 Slice 从底层数组截取这一帧的视图, 完全没有创建新 byte[]
            ReadOnlySpan<byte> frame = currentSpan[..expectedLength];

            // CRC 校验
            ushort calculatedCrc = CalcCRC16(frame[..(expectedLength - 2)]);
            // 使用 BinaryPrimitives 安全读取小端模式的 CRC16
            ushort deviceCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(expectedLength - 2, 2));

            if (calculatedCrc == deviceCrc)
            {
                // 解析业务逻辑
                if (frame[1] == (byte)ModbusFunctionCode.ReadHoldingRegisters)
                {
                    int dataLength = frame[2];
                    if (3 + dataLength > frame.Length - 2)
                    {
                        Log.Warning("收到异常数据长度, 放弃解析该业务帧");
                        buffer.Consume(expectedLength);
                        continue;
                    }

                    ReadOnlySpan<byte> dataSpan = frame.Slice(3, dataLength);

                    // 高效、安全的跨平台解析
                    short rawTemp = BinaryPrimitives.ReadInt16BigEndian(dataSpan.Slice(0, 2));
                    short rawHum = BinaryPrimitives.ReadInt16BigEndian(dataSpan.Slice(2, 2));

                    float actualTemperature = rawTemp / 10f;
                    float actualHumidity = rawHum / 10f;

                    WeakReferenceMessenger.Default.Send(new SensorDataMessage(actualTemperature, actualHumidity));
                }

                // 处理成功, 消费掉这一整帧数据 (解决粘包)
                buffer.Consume(expectedLength);
            }
            else
            {
                Log.Warning("CRC校验失败, 丢弃脏字节");
                buffer.Consume(1);
            }
        }
    }

    private static int GetExpectedFrameLength(ReadOnlySpan<byte> span, byte functionCode)
    {
        if (functionCode > 0x80) return 5;

        switch (functionCode)
        {
            case (byte)ModbusFunctionCode.ReadHoldingRegisters:
            case (byte)ModbusFunctionCode.ReadInputRegisters:
                if (span.Length < 3) return 0;
                int byteCount = span[2];
                return 1 + 1 + 1 + byteCount + 2;
            case (byte)ModbusFunctionCode.WriteSingleRegister:
            case (byte)ModbusFunctionCode.WriteMultipleRegisters:
                return 8;
            default:
                return -1;
        }
    }

    public void Dispose()
    {
        // 释放所有租用的内存
        foreach (var buffer in _buffers.Values)
        {
            buffer.Dispose();
        }
        _buffers.Clear();
        GC.SuppressFinalize(this);
    }
}
