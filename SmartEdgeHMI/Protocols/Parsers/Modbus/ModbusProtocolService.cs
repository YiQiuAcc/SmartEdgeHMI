using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipelines;
using System.Net;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.MachineState;
using SmartEdgeHMI.Models.Dtos;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.Models.ValueObjects;
using SmartEdgeHMI.Protocols.Services.Utils;

namespace SmartEdgeHMI.Protocols.Services;

/// <summary>
/// Modbus RTU 协议服务: 轮询(FC03) + 单寄存器写入(FC06)。
/// 命令通过 ITransportService.WriteBytesAsync 发送, 响应由传输层后台读循环异步送达 OnDataReceivedAsync → Pipe → ProcessPipeLoop。
/// </summary>
public class ModbusProtocolService : IProtocolParser
{
    private readonly ITransportService _transport;
    private readonly IProtocolConfig _protocolConfig;
    private readonly IDeviceStateContainer _deviceState;
    private readonly IAlarmStateMachine _alarmStateMachine;
    private readonly ISettingsService _settingsService;
    private readonly ConcurrentDictionary<string, PortPipeState> _pipes = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pollingTokens = new();
    private bool _disposed;

    public const ushort RegisterReset = 0x0001;
    public const ushort RegisterThreshold = 0x0002;
    public string Key => "Modbus";

    public ModbusProtocolService(ITransportService transport,
        IProtocolConfig protocolConfig, IDeviceStateContainer deviceState,
        IAlarmStateMachine alarmStateMachine, ISettingsService settingsService)
    {
        _transport = transport;
        _protocolConfig = protocolConfig;
        _deviceState = deviceState;
        _alarmStateMachine = alarmStateMachine;
        _settingsService = settingsService;
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

    private static async Task WriteAndReturnBufferAsync(ITransportService transport, string portName, byte[] buffer, int length)
    {
        try { await transport.WriteBytesAsync(portName, buffer, length); }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private Task SendModbusCommandAsync(string portName, byte slaveAddress, byte functionCode, ReadOnlySpan<byte> data)
    {
        int frameLength = 1 + 1 + data.Length + 2;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(frameLength);
        buffer[0] = slaveAddress;
        buffer[1] = functionCode;
        data.CopyTo(buffer.AsSpan(2));
        ushort crc = CalcCRC16(buffer.AsSpan(0, frameLength - 2));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(frameLength - 2), crc);
        return WriteAndReturnBufferAsync(_transport, portName, buffer, frameLength);
    }

    public Task WriteSingleRegisterAsync(string portName, byte slaveAddress, ushort registerAddress, ushort value)
    {
        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(data[..2], registerAddress);
        BinaryPrimitives.WriteUInt16BigEndian(data[2..], value);
        return SendModbusCommandAsync(portName, slaveAddress, (byte)ModbusFunctionCode.WriteSingleRegister, data);
    }

    public async ValueTask OnDataReceivedAsync(string portName, ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0) return;

        var state = _pipes.GetOrAdd(portName, _ =>
        {
            var s = new PortPipeState();
            s.ProcessingTask = Task.Run(() => ProcessPipeLoopAsync(portName, s));
            return s;
        });
        await state.Pipe.Writer.WriteAsync(data);
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
        if (!_pollingTokens.TryAdd(portName, cts)) { cts.Dispose(); return; }
        _ = Task.Run(() => PollLoopAsync(portName, cts.Token), cts.Token);
        Log.Information("[Modbus] 已启动 {Port} 的轮询任务 (间隔 {Interval}ms)", portName, _settingsService.Current.Modbus.PollingIntervalMs);
    }

    private void StopPolling(string portName)
    {
        if (_pollingTokens.TryRemove(portName, out var cts)) { cts.Cancel(); cts.Dispose(); }
    }

    private async Task PollLoopAsync(string portName, CancellationToken ct)
    {
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_settingsService.Current.Modbus.PollingIntervalMs));
        var data = new byte[4];
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0, 2), AppConstants.ModbusPollStartAddress);
                    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2, 2), AppConstants.ModbusPollRegisterCount);
                    await SendModbusCommandAsync(portName, _protocolConfig.SlaveAddress,
                        (byte)ModbusFunctionCode.ReadHoldingRegisters, data);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { Log.Error(ex, "[Modbus] {Port} 轮询异常", portName); }
            }
        }
        catch (OperationCanceledException) { }
        finally { timer.Dispose(); }
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
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error(ex, "[Modbus] {Port} 管道处理循环异常退出", portName); }
        finally
        {
            await pipe.Reader.CompleteAsync();
            _pipes.TryRemove(portName, out _);
        }
    }

    private SequencePosition ParseBuffer(ReadOnlySequence<byte> buffer, string portName, byte slaveAddress)
    {
        var reader = new SequenceReader<byte>(buffer);
        while (reader.Remaining >= 5)
        {
            if (!TryProcessNextFrame(ref reader, portName, slaveAddress)) break;
        }
        return reader.Position;
    }

    private bool TryProcessNextFrame(ref SequenceReader<byte> reader, string portName, byte slaveAddress)
    {
        if (!reader.TryPeek(out byte currentSlave) || currentSlave != slaveAddress)
        {
            // 如果地址不匹配, 说明是错位数据或杂讯, 跳过 1 字节, 继续搜寻帧头
            reader.Advance(1);
            return true;
        }

        // Modbus 至少需要 3 个字节才能解析出功能码和用于计算长度的 ByteCount
        if (reader.Remaining < 3)
            return false;

        // 跨内存片段安全地窥探功能码和第三字节 (DataByte / ByteCount)
        reader.TryPeek(1, out byte functionCode);
        reader.TryPeek(2, out byte dataByte);

        // 根据功能码计算期望的完整帧长度
        int expectedLength = GetExpectedFrameLength(dataByte, functionCode);
        if (expectedLength == -1)
        {
            // 未知的功码, 跳过 1 字节重新搜寻
            reader.Advance(1);
            return true;
        }

        // 检查整个管道中现存的所有数据总和, 是否满足期望的帧长度
        if (reader.Remaining < expectedLength)
            return false; // 帧不完整, 返回 false 等待更多数据

        byte[]? rented = null;
        try
        {
            // 提取一整帧
            ReadOnlySpan<byte> frame;
            if (reader.UnreadSpan.Length >= expectedLength)
            {
                frame = reader.UnreadSpan[..expectedLength];
            }
            else
            {
                frame = RentFrame(reader.Sequence.Slice(reader.Position, expectedLength), expectedLength, out rented);
            }
            // CRC 校验
            ushort calcCrc = CalcCRC16(frame[..^2]);
            ushort devCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]);
            if (calcCrc == devCrc)
            {
                // 校验通过，派发数据
                if (functionCode == (byte)ModbusFunctionCode.ReadHoldingRegisters)
                    DispatchSensorReading(frame, portName);
                reader.Advance(expectedLength);
            }
            else
            {
                reader.Advance(1);
            }
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
        return true;
    }

    private static ReadOnlySpan<byte> RentFrame(ReadOnlySequence<byte> seq, int length, out byte[]? rented)
    {
        rented = ArrayPool<byte>.Shared.Rent(length);
        seq.CopyTo(rented);
        return rented.AsSpan(0, length);
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

    private void DispatchSensorReading(ReadOnlySpan<byte> frame, string portName)
    {
        int dataLength = frame[2];
        if (3 + dataLength > frame.Length - 2) return;
        ReadOnlySpan<byte> dataSpan = frame.Slice(3, dataLength);

        var temperature = Temperature.FromRawModbus(BinaryPrimitives.ReadInt16BigEndian(dataSpan[..2]));
        var humidity = Humidity.FromRawModbus(BinaryPrimitives.ReadInt16BigEndian(dataSpan[2..4]));
        var status = dataLength >= 6 ? (DeviceStatus)BinaryPrimitives.ReadUInt16BigEndian(dataSpan[4..6]) : DeviceStatus.Online;
        var error = dataLength >= 8 ? (ErrorCode)BinaryPrimitives.ReadUInt16BigEndian(dataSpan[6..8]) : ErrorCode.NoError;

        _deviceState.UpdateTelemetry(portName, temperature, humidity, status, error, DataQuality.Good);
        _alarmStateMachine.Evaluate(new Models.Dtos.TelemetryPayload
        {
            DeviceId = AppConstants.DefaultDeviceName,
            Temperature = temperature,
            Humidity = humidity,
            StatusCode = status,
            ErrorCode = error,
            QualityCode = DataQuality.Good
        });
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
        if (_disposed) return;
        _disposed = true;
        _protocolConfig.PropertyChanged -= OnProtocolConfigChanged;
        foreach (var cts in _pollingTokens.Values) { try { cts.Cancel(); cts.Dispose(); } catch { } }
        _pollingTokens.Clear();
        foreach (string portName in _pipes.Keys.ToList()) CleanupPipe(portName);
        GC.SuppressFinalize(this);
    }

    private sealed class PortPipeState : IDisposable
    {
        public Pipe Pipe { get; } = new(new PipeOptions(pool: MemoryPool<byte>.Shared, pauseWriterThreshold: 0,
            resumeWriterThreshold: 0, minimumSegmentSize: AppConstants.PipeMinimumSegmentSize, useSynchronizationContext: false));
        public CancellationTokenSource Cts { get; } = new();
        public Task ProcessingTask { get; set; } = Task.CompletedTask;
        public void Dispose() { Cts.Cancel(); Cts.Dispose(); }
    }
}
