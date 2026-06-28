using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipelines;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Communication.Ports;
using SmartEdgeHMI.Communication.Protocols.Utils;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.Models.ValueObjects;

namespace SmartEdgeHMI.Communication.Protocols;

public class ModbusProtocolService : IProtocolParser
{
    private readonly ISerialPortService _serialPortService;
    private readonly IProtocolConfig _protocolConfig;
    private readonly ConcurrentDictionary<string, PortPipeState> _pipes = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pollingTokens = new();
    private bool _disposed;
    public const ushort RegisterReset = 0x0001;
    public const ushort RegisterThreshold = 0x0002;

    public string Key => "Modbus";

    public ModbusProtocolService(ISerialPortService serialPortService, IProtocolConfig protocolConfig)
    {
        _serialPortService = serialPortService;
        _protocolConfig = protocolConfig;
        _protocolConfig.PropertyChanged += OnProtocolConfigChanged;
    }

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

    public Task SendModbusCommandAsync(string portName, byte slaveAddress, byte functionCode, ReadOnlySpan<byte> data)
    {
        int frameLength = 1 + 1 + data.Length + 2;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(frameLength);

        buffer[0] = slaveAddress;
        buffer[1] = functionCode;
        data.CopyTo(buffer.AsSpan(2, data.Length));

        ushort crc = CalcCRC16(buffer.AsSpan(0, frameLength - 2));
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

    public Task ReadHoldingRegistersAsync(string portName, byte slaveAddress, ushort startAddress, ushort quantity)
    {
        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(data[..2], startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(2, 2), quantity);

        return SendModbusCommandAsync(portName, slaveAddress, (byte)ModbusFunctionCode.ReadHoldingRegisters, data);
    }

    public Task WriteSingleRegisterAsync(string portName, byte slaveAddress, ushort registerAddress, ushort value)
    {
        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(data[..2], registerAddress);
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(2, 2), value);

        return SendModbusCommandAsync(portName, slaveAddress, (byte)ModbusFunctionCode.WriteSingleRegister, data);
    }

    public async ValueTask OnDataReceivedAsync(string portName, ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0) return;

        var state = _pipes.GetOrAdd(portName, key =>
        {
            var s = new PortPipeState();
            s.ProcessingTask = Task.Run(() => ProcessPipeLoopAsync(key, s));
            return s;
        });

        // 异步写入 Pipe 避免 sync-over-async: 上游 ForwardDataLoop 处于异步上下文, 阻塞将耗尽线程池
        await state.Pipe.Writer.WriteAsync(data).ConfigureAwait(false);
    }

    public void OnDeviceStateChanged(string portName, ConnectionState state)
    {
        switch (state)
        {
            case ConnectionState.Connected:
                if (_protocolConfig.SelectedProtocol == CommunicationProtocol.Modbus)
                    StartPolling(portName);
                break;
            case ConnectionState.Disconnected:
            case ConnectionState.Error:
                StopPolling(portName);
                CleanupPipe(portName);
                break;
        }
    }

    private void OnProtocolConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IProtocolConfig.SelectedProtocol)) return;

        if (_protocolConfig.SelectedProtocol == CommunicationProtocol.Modbus)
        {
            foreach (string portName in _protocolConfig.ConnectedPorts)
                StartPolling(portName);
            Log.Information("[Modbus] 协议已切换至Modbus模式, 已启动所有已连接端口的轮询");
        }
        else
        {
            foreach (string portName in _pollingTokens.Keys.ToList())
                StopPolling(portName);
            foreach (string portName in _pipes.Keys.ToList())
                CleanupPipe(portName);
            Log.Information("[Modbus] 协议已切换至非Modbus模式, 已停止所有轮询并清理管道");
        }
    }

    private void StartPolling(string portName)
    {
        var cts = new CancellationTokenSource();
        if (!_pollingTokens.TryAdd(portName, cts))
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(() => PollLoopAsync(portName, cts.Token), cts.Token);
        Log.Information("[Modbus] 已启动 {Port} 的轮询任务 (间隔 {Interval}ms)", portName, AppConstants.ModbusPollingIntervalMs);
    }

    private void StopPolling(string portName)
    {
        if (_pollingTokens.TryRemove(portName, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            Log.Information("[Modbus] 已停止 {Port} 的轮询任务", portName);
        }
    }

    private async Task PollLoopAsync(string portName, CancellationToken ct)
    {
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(AppConstants.ModbusPollingIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await ReadHoldingRegistersAsync(portName,
                    _protocolConfig.SlaveAddress, AppConstants.ModbusPollStartAddress, AppConstants.ModbusPollRegisterCount);
            }
        }
        catch (OperationCanceledException)
        {
            // 轮询被取消, 正常退出
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Modbus] {Port} 轮询循环异常退出", portName);
        }
        finally
        {
            timer.Dispose();
        }
    }

    private async Task ProcessPipeLoopAsync(string portName, PortPipeState ps)
    {
        var pipe = ps.Pipe;
        var ct = ps.Cts.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ReadResult result = await pipe.Reader.ReadAsync(ct);
                if (result.IsCanceled) break;

                SequencePosition consumed = ParseBuffer(result.Buffer, portName, _protocolConfig.SlaveAddress);
                pipe.Reader.AdvanceTo(consumed, result.Buffer.End);

                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException)
        {
            // 外部触发 Cancellation 时正常退出循环
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Modbus] {Port} 管道处理循环异常退出", portName);
        }
        finally
        {
            await pipe.Reader.CompleteAsync();
            _pipes.TryRemove(portName, out _);
        }
    }

    internal static SequencePosition ParseBuffer(ReadOnlySequence<byte> buffer, string portName, byte slaveAddress)
    {
        var reader = new SequenceReader<byte>(buffer);

        // 主循环保持极度清爽
        while (reader.Remaining >= 5)
        {
            // 如果返回 false，说明剩余数据不足以组成预期的一帧，应当退出循环
            if (!TryProcessNextFrame(ref reader, portName, slaveAddress))
            {
                break;
            }
        }

        return reader.Position;
    }

    /// <summary>尝试处理下一个可能的数据帧</summary>
    private static bool TryProcessNextFrame(ref SequenceReader<byte> reader, string portName, byte slaveAddress)
    {
        ReadOnlySpan<byte> span = reader.UnreadSpan;

        // 校验从机地址
        if (span[0] != slaveAddress) { reader.Advance(1); return true; }
        if (span.Length < 3) return false; // 跨 Segments 导致物理长度不够，跳出外层循环等待数据

        // 计算预期长度
        byte functionCode = span[1];
        int expectedLength = GetExpectedFrameLength(span[2], functionCode);

        if (expectedLength == -1) { reader.Advance(1); return true; }
        if (reader.Remaining < expectedLength) return false; // 剩余总字节不足以组成一整帧，跳出
        // 提取并校验帧数据
        ProcessFrameData(ref reader, expectedLength, portName);
        return true;
    }

    /// <summary>提取帧 Span 并进行 CRC 校验</summary>
    private static void ProcessFrameData(ref SequenceReader<byte> reader, int expectedLength, string portName)
    {
        byte[]? rented = null;
        try
        {
            // 获取只读帧视窗 (自适应单内存块或多内存块租赁)
            ReadOnlySpan<byte> frame = GetFrameSpan(ref reader, expectedLength, out rented);

            ushort calculatedCrc = CalcCRC16(frame[..^2]);
            ushort deviceCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]);

            if (calculatedCrc == deviceCrc)
            {
                ProcessValidFrame(frame, portName);
                reader.Advance(expectedLength);
            }
            else
            {
                Log.Warning("[Modbus] {Port} CRC校验失败 (预期 {CalcCRC}, 设备 {DevCRC}), 丢弃脏字节",
                    portName, calculatedCrc, deviceCrc);
                reader.Advance(1);
            }
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>从 SequenceReader 自适应获取帧 Span</summary>
    private static ReadOnlySpan<byte> GetFrameSpan(ref SequenceReader<byte> reader, int expectedLength, out byte[]? rented)
    {
        if (reader.UnreadSpan.Length >= expectedLength)
        {
            rented = null;
            return reader.UnreadSpan[..expectedLength];
        }

        var frameSeq = reader.Sequence.Slice(reader.Position, expectedLength);
        rented = ArrayPool<byte>.Shared.Rent(expectedLength);
        frameSeq.CopyTo(rented);
        return rented.AsSpan(0, expectedLength);
    }

    private static int GetExpectedFrameLength(byte dataByte, byte functionCode)
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

    private static void ProcessValidFrame(ReadOnlySpan<byte> frame, string portName)
    {
        if (frame[1] != (byte)ModbusFunctionCode.ReadHoldingRegisters) return;

        int dataLength = frame[2];
        if (3 + dataLength > frame.Length - 2)
        {
            Log.Warning("[Modbus] {Port} 收到异常数据长度, 放弃解析", portName);
            return;
        }

        ReadOnlySpan<byte> dataSpan = frame.Slice(3, dataLength);

        short rawTemp = BinaryPrimitives.ReadInt16BigEndian(dataSpan[..2]);
        short rawHum = BinaryPrimitives.ReadInt16BigEndian(dataSpan.Slice(2, 2));

        var actualTemperature = Temperature.FromRawModbus(rawTemp);
        var actualHumidity = Humidity.FromRawModbus(rawHum);

        var statusCode = DeviceStatus.Online;
        var errorCode = ErrorCode.NoError;

        if (dataLength >= 6)
        {
            ushort rawStatus = BinaryPrimitives.ReadUInt16BigEndian(dataSpan.Slice(4, 2));
            statusCode = (DeviceStatus)rawStatus;
        }

        if (dataLength >= 8)
        {
            ushort rawError = BinaryPrimitives.ReadUInt16BigEndian(dataSpan.Slice(6, 2));
            errorCode = (ErrorCode)rawError;
        }

        WeakReferenceMessenger.Default.Send(new SensorReading(
            portName, actualTemperature, actualHumidity, statusCode, errorCode));
    }

    private void CleanupPipe(string portName)
    {
        if (_pipes.TryRemove(portName, out var ps))
        {
            ps.Pipe.Writer.Complete();
            ps.Cts.Cancel();
            ps.Dispose();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    // 标准的 Dispose 模式保护虚方法
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // 取消事件订阅
            _protocolConfig.PropertyChanged -= OnProtocolConfigChanged;
            // 释放所有轮询 Token
            foreach (var cts in _pollingTokens.Values)
            {
                try
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Modbus] 释放轮询 Cts 时发生异常");
                }
            }
            _pollingTokens.Clear();

            // 清理管道资源
            foreach (string portName in _pipes.Keys.ToList())
            {
                CleanupPipe(portName);
            }
        }

        _disposed = true;
    }

    private sealed class PortPipeState : IDisposable
    {
        public Pipe Pipe { get; }
        public CancellationTokenSource Cts { get; }
        public Task ProcessingTask { get; set; } = Task.CompletedTask;

        public PortPipeState()
        {
            Pipe = new Pipe(new PipeOptions(
                pool: MemoryPool<byte>.Shared,
                pauseWriterThreshold: 0,
                resumeWriterThreshold: 0,
                minimumSegmentSize: AppConstants.PipeMinimumSegmentSize,
                useSynchronizationContext: false
            ));
            Cts = new CancellationTokenSource();
        }

        public void Dispose()
        {
            Cts.Cancel();
            Cts.Dispose();
        }
    }
}
