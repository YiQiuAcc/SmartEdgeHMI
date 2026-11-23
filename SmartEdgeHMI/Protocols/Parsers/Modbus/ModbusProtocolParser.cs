using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipelines;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Domain.MachineState;
using SmartEdgeHMI.Core.Domain.ValueObjects;
using SmartEdgeHMI.Core.Services;
using SmartEdgeHMI.Models.Messages;
using SmartEdgeHMI.Protocols.Transports;
using SmartEdgeHMI.Utils;

namespace SmartEdgeHMI.Protocols.Parsers.Modbus;

/// <summary>Modbus RTU 协议实现: FC03 轮询采集与 FC06 单寄存器写入</summary>
/// <remarks>
/// 请求经 <see cref="ITransportService.WriteBytesAsync"/> 发出, 响应由传输层读循环写到 <see cref="OnDataReceivedAsync"/>,
/// 再写入每端口一条 <see cref="Pipe"/>, 由独立任务成帧解析, 读写不在同一线程上配对,
/// 本类不等待响应, 只按帧头 + 长度 + CRC 从字节流中自行定位合法帧。
/// </remarks>
public class ModbusProtocolParser : IProtocolParser
{
    private readonly ITransportService _transport;
    private readonly IProtocolConfig _protocolConfig;
    private readonly IDeviceStateContainer _deviceState;
    private readonly IAlarmStateMachine _alarmStateMachine;
    private readonly ISettingsService _settingsService;
    private readonly ConcurrentDictionary<string, PortPipeState> _pipes = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pollingTokens = new();
    private bool _disposed;

    /// <summary>复位动作寄存器: 写入 1 触发设备复位</summary>
    public const ushort RegisterReset = 0x0001;

    /// <summary>报警阈值寄存器: 写入值 = °C × 10</summary>
    public const ushort RegisterThreshold = 0x0002;

    public string Key => "Modbus";

    /// <summary>端口就绪到发出第一帧 FC03 之间的等待, 单位 ms</summary>
    private const int PollStartupDelayMs = 1000;

    public ModbusProtocolParser(ITransportService transport,
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

    // NOTE: 与 ModbusFrameUtility.CalcCRC16 为同一实现; 去重会改动本类的公开静态方法, 留待下次 API 清理
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

    /// <summary>写入后立即归还租用的缓冲区</summary>
    /// <remarks>写入是同步阻塞, 因此返回时传输层已消费完 <paramref name="buffer"/>, 可安全归还</remarks>
    private static async Task WriteAndReturnBufferAsync(ITransportService transport, string portName, byte[] buffer, int length)
    {
        try { await transport.WriteBytesAsync(portName, buffer, length); }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>组装并发送一帧 RTU 请求: 地址 + 功能码 + 数据 + CRC16(小端)</summary>
    /// <remarks>缓冲区从 ArrayPool 租用, 由 <see cref="WriteAndReturnBufferAsync"/> 在 finally 中归还</remarks>
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

    /// <summary>FC06 写单个保持寄存器</summary>
    /// <param name="registerAddress">寄存器地址, 大端写入报文</param>
    /// <param name="value">写入值, 原始整型; 阈值等物理量需调用方先按 ×10 缩放</param>
    public Task WriteSingleRegisterAsync(string portName, byte slaveAddress, ushort registerAddress, ushort value)
    {
        Span<byte> data = stackalloc byte[4];
        // RTU 的地址与数据字段均是大端; 只有帧尾 CRC 是小端
        BinaryPrimitives.WriteUInt16BigEndian(data[..2], registerAddress);
        BinaryPrimitives.WriteUInt16BigEndian(data[2..], value);
        return SendModbusCommandAsync(portName, slaveAddress, (byte)ModbusFunctionCode.WriteSingleRegister, data);
    }

    public async ValueTask OnDataReceivedAsync(string portName, ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0) return;

        var state = _pipes.GetOrAdd(portName, key =>
        {
            var s = new PortPipeState();
            // 消费任务不跟随调用方取消, 端口生命周期由 OnDeviceStateChanged 控制
            s.ProcessingTask = Task.Run(() => ProcessPipeLoopAsync(key, s))
                .ObserveAndLogAsync($"[Modbus] {key} 管道处理任务");
            return s;
        });
        await state.Pipe.Writer.WriteAsync(data);
    }

    /// <summary>按端口连接状态启停轮询与解析管道</summary>
    /// <remarks>
    /// Connected 仅在当前协议为 Modbus 时启动, 且先清理可能残留的旧管道;
    /// Error 只停轮询, 管道保留; Disconnected 视为会话结束, 轮询与管道一并清理。
    /// </remarks>
    public void OnDeviceStateChanged(string portName, ConnectionState state)
    {
        switch (state)
        {
            case ConnectionState.Connected:
                if (_protocolConfig.SelectedProtocol == CommunicationProtocol.Modbus)
                {
                    if (_pipes.ContainsKey(portName))
                        CleanupPipe(portName);
                    StartPolling(portName);
                }
                break;
            case ConnectionState.Disconnected:
                StopPolling(portName);
                CleanupPipe(portName);
                break;
            case ConnectionState.Error:
                StopPolling(portName);
                break;
        }
    }

    /// <summary>协议切换时同步启停轮询: 切换到 Modbus 时启动轮询, 切离时停止轮询并清理管道</summary>
    /// <remarks>
    /// 由 <see cref="INotifyPropertyChanged.PropertyChanged"/> 驱动, 在触发方线程上同步执行;
    /// 只响应 <c>SelectedProtocol</c>, 其余配置项变化忽略。
    /// </remarks>
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

    /// <summary>为端口启动轮询任务; 已存在则直接返回, 保证一个端口只有一条轮询链</summary>
    private void StartPolling(string portName)
    {
        var cts = new CancellationTokenSource();
        if (!_pollingTokens.TryAdd(portName, cts)) { cts.Dispose(); return; }
        _ = Task.Run(() => PollLoopAsync(portName, cts.Token), cts.Token)
            .ObserveAndLogAsync($"[Modbus] {portName} 轮询任务");
        Log.Information("[Modbus] 已启动 {Port} 的轮询任务 (间隔 {Interval}ms)", portName, _settingsService.Current.Modbus.PollingIntervalMs);
    }

    /// <summary>取消并移除端口的轮询 Token; 重复调用无副作用</summary>
    private void StopPolling(string portName)
    {
        if (_pollingTokens.TryRemove(portName, out var cts)) { cts.Cancel(); cts.Dispose(); }
    }

    /// <summary>按固定周期发送 FC03, 起始地址 0, 读 4 个寄存器</summary>
    /// <remarks>
    /// 轮询间隔在计时器创建时读取一次, 运行中修改 settings 不会改变当前任务节奏,
    /// 需断开重连或切换协议后才生效。请求报文复用同一 4 字节缓冲, 每 tick 无分配。
    /// </remarks>
    private async Task PollLoopAsync(string portName, CancellationToken ct)
    {
        try
        {
            await Task.Delay(PollStartupDelayMs, ct);
        }
        catch (OperationCanceledException)
        {
            // 停机或断线时未发出首帧就退出, 不创建计时器
            return;
        }

        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_settingsService.Current.Modbus.PollingIntervalMs));
        byte[] data = new byte[4];
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
        catch (OperationCanceledException)
        {
            // 停止轮询时取消, 正常退出
        }
        finally { timer.Dispose(); }
    }

    /// <summary>端口解析循环: 从管道取字节, 逐帧解析, 只消费已确认合法的完整帧</summary>
    /// <remarks>
    /// AdvanceTo 的第二个参数是 buffer.End, 即 已检查但未消费 的尾部
    /// 半帧保留在管道中, 等待后续字节补齐, 不因一次读取不完整而丢弃。
    /// </remarks>
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
            // 端口清理时由 ps.Cts 取消, 正常退出
        }
        catch (Exception ex) { Log.Error(ex, "[Modbus] {Port} 管道处理循环异常退出", portName); }
        finally
        {
            await pipe.Reader.CompleteAsync();
            _pipes.TryRemove(portName, out _);
        }
    }

    /// <summary>在缓冲区中连续切帧, 返回已消费到的位置</summary>
    /// <remarks>RTU 无帧头标志, 只能靠"地址 + 功能码 + 长度 + CRC"试探; 5 字节是最短合法帧(异常响应)</remarks>
    private SequencePosition ParseBuffer(ReadOnlySequence<byte> buffer, string portName, byte slaveAddress)
    {
        var reader = new SequenceReader<byte>(buffer);
        while (reader.Remaining >= 5)
        {
            if (!TryProcessNextFrame(ref reader, portName, slaveAddress)) break;
        }
        return reader.Position;
    }

    /// <summary>尝试从当前位置解析一帧</summary>
    /// <returns>true 表示位置已推进(解析成功或跳过 1 字节), 可继续下一轮; false 表示数据不足, 须等更多字节</returns>
    /// <remarks>
    /// 失配时只前进 1 字节, 逐字节滑动是 RTU 等无帧头报文重新同步的手段。
    /// </remarks>
    private bool TryProcessNextFrame(ref SequenceReader<byte> reader, string portName, byte slaveAddress)
    {
        if (!reader.TryPeek(out byte currentSlave) || currentSlave != slaveAddress)
        {
            reader.Advance(1);
            return true;
        }

        // 至少 3 字节才能读到功能码与 ByteCount, 不足则等下一批数据
        if (reader.Remaining < 3)
            return false;

        // SequenceReader 的 Peek 可跨内存片段
        reader.TryPeek(1, out byte functionCode);
        reader.TryPeek(2, out byte dataByte);

        int expectedLength = GetExpectedFrameLength(dataByte, functionCode);
        if (expectedLength == -1)
        {
            // 功能码不在支持列表, 视为噪声
            reader.Advance(1);
            return true;
        }

        // 半帧: 地址与功能码合法, 但整帧未齐, 保留不动等后续字节
        if (reader.Remaining < expectedLength)
            return false;

        byte[]? rented = null;
        try
        {
            // 单段内切片, 跨段时才复制到租用缓冲
            ReadOnlySpan<byte> frame;
            if (reader.UnreadSpan.Length >= expectedLength)
            {
                frame = reader.UnreadSpan[..expectedLength];
            }
            else
            {
                frame = RentFrame(reader.Sequence.Slice(reader.Position, expectedLength), expectedLength, out rented);
            }

            ushort calcCrc = CalcCRC16(frame[..^2]);
            ushort devCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]);
            if (calcCrc == devCrc)
            {
                // 只有 FC03 响应携带遥测; FC06 回显与异常响应在此不参与业务判定
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

    /// <summary>把跨多个内存段的帧复制到租用缓冲, 便于一次性做 CRC 校验</summary>
    /// <remarks><paramref name="rented"/> 由调用方在 finally 中归还; 缓冲区长度可能大于 <paramref name="length"/></remarks>
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

    /// <summary>按 FC03 响应的数据区还原遥测, 并交给状态容器与报警状态机</summary>
    /// <remarks>
    /// 数据区按固件寄存器顺序排列, 每项 2 字节大端,
    /// [0..2) 温度 = 值/10 °C, [2..4) 湿度 = 值/10 %RH, [4..6) 设备状态, [6..8) 错误码。
    /// 后两项为可选字段, 数据区不足时按 Online / NoError 处理;
    /// 前两项不可省, 其长度由轮询侧固定读 4 个寄存器保证(正常响应 8 字节数据区)。
    /// </remarks>
    private void DispatchSensorReading(ReadOnlySpan<byte> frame, string portName)
    {
        int dataLength = frame[2];
        // 数据区越界说明帧长度字段与实际内容矛盾, 丢弃
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

    /// <summary>清理端口的管道: 先让 Reader 读到通道结束, 再取消消费任务, 最后释放上下文</summary>
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

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _protocolConfig.PropertyChanged -= OnProtocolConfigChanged;

            foreach (var cts in _pollingTokens.Values)
            {
                try
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Modbus Parser] 释放轮询 Token 时发生异常");
                }
            }
            _pollingTokens.Clear();

            foreach (string portName in _pipes.Keys.ToList())
            {
                CleanupPipe(portName);
            }
            _pipes.Clear();
        }
        _disposed = true;
    }

    /// <summary>单端口的管道与消费任务句柄, 一个实例只服务一条串口链路</summary>
    private sealed class PortPipeState : IDisposable
    {
        public Pipe Pipe { get; } = new(new PipeOptions(pool: MemoryPool<byte>.Shared, pauseWriterThreshold: 0,
            resumeWriterThreshold: 0, minimumSegmentSize: AppConstants.PipeMinimumSegmentSize, useSynchronizationContext: false));
        public CancellationTokenSource Cts { get; } = new();
        public Task ProcessingTask { get; set; } = Task.CompletedTask;

        public void Dispose()
        { Cts.Cancel(); Cts.Dispose(); }
    }
}
