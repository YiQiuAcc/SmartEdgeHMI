using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Models.Dtos;
using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Communication.Protocols;

public class JsonProtocolService : IProtocolParser
{
    private readonly ConcurrentDictionary<string, PortPipeState> _pipes = new();

    public string Key => "JSON";

    public async ValueTask OnDataReceivedAsync(string portName, ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0) return;

        var state = _pipes.GetOrAdd(portName, _ =>
        {
            var s = new PortPipeState();
            s.ProcessingTask = Task.Run(() => ProcessPipeLoopAsync(portName, s));
            return s;
        });

        // 异步写入 Pipe，不再 .GetAwaiter().GetResult()
        await state.Pipe.Writer.WriteAsync(data);
    }

    public void OnDeviceStateChanged(string portName, ConnectionState state)
    {
        if (state == ConnectionState.Disconnected || state == ConnectionState.Error)
        {
            if (_pipes.TryRemove(portName, out var ps))
            {
                ps.Pipe.Writer.Complete();
                ps.Cts.Cancel();
                ps.Dispose();
            }
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
                DrainBuffer(result.Buffer, pipe.Reader, portName);
                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "[JSON] {Port} 管道处理循环异常退出", portName);
        }
        finally
        {
            await pipe.Reader.CompleteAsync();
            _pipes.TryRemove(portName, out _);
        }
    }

    private static void DrainBuffer(ReadOnlySequence<byte> buffer, PipeReader reader, string portName)
    {
        var seqReader = new SequenceReader<byte>(buffer);
        while (seqReader.TryReadTo(out ReadOnlySequence<byte> line, (byte)'\n'))
        {
            ProcessLine(portName, line);
        }
        reader.AdvanceTo(seqReader.Position, buffer.End);
    }

    private static void ProcessLine(string portName, ReadOnlySequence<byte> line)
    {
        byte[]? rented = null;
        try
        {
            ReadOnlySpan<byte> span;
            if (line.IsSingleSegment)
            {
                span = line.FirstSpan;
            }
            else
            {
                rented = ArrayPool<byte>.Shared.Rent((int)line.Length);
                line.CopyTo(rented);
                span = rented.AsSpan(0, (int)line.Length);
            }

            if (span.Length > 0 && span[^1] == (byte)'\r')
                span = span[..^1];

            if (span.Length > 0)
            {
                try
                {
                    var payload = JsonSerializer.Deserialize<TelemetryPayload>(span);
                    if (payload != null)
                    {
                        WeakReferenceMessenger.Default.Send(new DeviceTelemetry(portName, payload));
                    }
                }
                catch (JsonException ex)
                {
                    string errorJson = Encoding.UTF8.GetString(span);
                    Log.Warning(ex, "串口 {PortName} 收到无效的 JSON 数据: {RawData}", portName, errorJson);
                }
            }
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void Dispose()
    {
        foreach (string portName in _pipes.Keys.ToList())
        {
            if (_pipes.TryRemove(portName, out var ps))
            {
                ps.Pipe.Writer.Complete();
                ps.Cts.Cancel();
                ps.Dispose();
            }
        }
        GC.SuppressFinalize(this);
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
