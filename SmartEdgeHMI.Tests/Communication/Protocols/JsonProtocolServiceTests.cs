using System.Buffers;
using System.Reflection;
using System.Text;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Models.Dtos;
using SmartEdgeHMI.Protocols.Parsers.Json;

namespace SmartEdgeHMI.Tests.Communication.Protocols;

/// <summary>JSON 行协议解析: 用反射直接驱动私有的 ProcessLine, 覆盖有效报文、非法报文与边界输入</summary>
/// <remarks>
/// 使用反射是因为 OnDataReceivedAsync 依赖 Pipe 与后台任务, 单测中难以稳定复现"一帧一行"的切分时序。
/// FakeDeviceStateContainer 忽略全部写入, 因此这些用例只保证不抛异常, 断言不到解析后的字段值。
/// </remarks>
public class JsonProtocolServiceTests
{
    private static readonly MethodInfo _processLineMethod = typeof(JsonProtocolParser)
        .GetMethod("ProcessLine", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly JsonProtocolParser _parser = new(new FakeDeviceStateContainer());

    [Fact]
    public void ProcessLine_ValidTelemetryJson_ShouldDeserializeSuccessfully()
    {
        const string json = """{"deviceId":"Sensor_01","temperature":{"celsius":25.5},"humidity":{"percent":60.0},"status":1,"err_code":0,"quality":0}""";
        var seq = CreateSequence(json);

        var ex = Record.Exception(() => InvokeProcessLine("COM1", seq));
        Assert.Null(ex);
    }

    [Fact]
    public void ProcessLine_JsonWithTrailingCR_ShouldTrimAndDeserialize()
    {
#pragma warning disable RCS1190
        const string json = """{"deviceId":"Sensor_01","temperature":{"celsius":25.5},"humidity":{"percent":60.0},"status":1,"err_code":0,"quality":0}""" + "\r";
#pragma warning restore RCS1190

        var seq = CreateSequence(json);

        var ex = Record.Exception(() => InvokeProcessLine("COM1", seq));
        Assert.Null(ex);
    }

    [Fact]
    public void ProcessLine_InvalidJson_ShouldNotThrow()
    {
        var seq = CreateSequence("这不是有效的JSON");

        var ex = Record.Exception(() => InvokeProcessLine("COM1", seq));
        Assert.Null(ex);
    }

    [Fact]
    public void ProcessLine_EmptyData_ShouldNotThrow()
    {
        var seq = new ReadOnlySequence<byte>(ReadOnlyMemory<byte>.Empty);

        var ex = Record.Exception(() => InvokeProcessLine("COM1", seq));
        Assert.Null(ex);
    }

    [Fact]
    public void ProcessLine_ErrorCodeJson_ShouldDeserializeCorrectly()
    {
        const string json = """{"deviceId":"Sensor_01","temperature":{"celsius":30.0},"humidity":{"percent":80.0},"status":1,"err_code":302,"quality":0}""";
        var seq = CreateSequence(json);

        var ex = Record.Exception(() => InvokeProcessLine("COM1", seq));
        Assert.Null(ex);
    }

    [Fact]
    public void ProcessLine_BadQualityJson_ShouldDeserializeCorrectly()
    {
        const string json = """{"deviceId":"Sensor_01","temperature":{"celsius":0.0},"humidity":{"percent":0.0},"status":0,"err_code":0,"quality":2}""";
        var seq = CreateSequence(json);

        var ex = Record.Exception(() => InvokeProcessLine("COM1", seq));
        Assert.Null(ex);
    }

    [Fact]
    public void ProcessLine_PartialJson_ShouldNotThrow()
    {
        var seq = CreateSequence("""{"deviceId":"Sensor_01"}""");

        var ex = Record.Exception(() => InvokeProcessLine("COM1", seq));
        Assert.Null(ex);
    }

    /// <summary>下行命令的键名契约: cmd_id / deviceId / action / timestamp, 改名会让既有接收端读不到字段</summary>
    [Fact]
    public void CommandPayload_Serialization_ShouldProduceValidJson()
    {
        var command = new CommandPayload(
            CommandId: Guid.NewGuid(),
            DeviceId: "Sensor_01",
            Action: DeviceAction.Reset,
            TimestampUnix: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Parameters: null
        );

        string json = System.Text.Json.JsonSerializer.Serialize(command);
        Assert.Contains("cmd_id", json);
        Assert.Contains("deviceId", json);
        Assert.Contains("action", json);

        var deserialized = System.Text.Json.JsonSerializer.Deserialize<CommandPayload>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(command.DeviceId, deserialized.DeviceId);
        Assert.Equal(command.Action, deserialized.Action);
    }

    private static ReadOnlySequence<byte> CreateSequence(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return new ReadOnlySequence<byte>(new ReadOnlyMemory<byte>(bytes));
    }

    private static void InvokeProcessLine(string portName, ReadOnlySequence<byte> line)
    {
        _processLineMethod.Invoke(_parser, [portName, line]);
    }

    private class FakeDeviceStateContainer : SmartEdgeHMI.Core.Domain.MachineState.IDeviceStateContainer
    {
#pragma warning disable CS0067
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
#pragma warning restore CS0067
        public SmartEdgeHMI.Core.Domain.ValueObjects.Temperature LatestTemperature => default;
        public SmartEdgeHMI.Core.Domain.ValueObjects.Humidity LatestHumidity => default;
        public DeviceStatus LatestDeviceStatus => default;
        public ErrorCode LatestErrorCode => default;
        public DataQuality LatestQuality => default;
        public DateTime LatestTimestamp => default;
        public string LatestPortName => "";

        public IReadOnlyDictionary<string, ErrorCode> GetActiveAlarms() => new Dictionary<string, ErrorCode>();
        public bool HasActiveAlarms => false;

        public bool IsPortConnected(string portName) => false;

        public IReadOnlySet<string> GetConnectedPorts() => new HashSet<string>();

        public void UpdateTelemetry(string portName, SmartEdgeHMI.Core.Domain.ValueObjects.Temperature temperature, SmartEdgeHMI.Core.Domain.ValueObjects.Humidity humidity,
            DeviceStatus status, ErrorCode error, DataQuality quality)
        { }
        public void UpdateConnectionState(string portName, SmartEdgeHMI.Models.Messages.ConnectionState state)
        { }

        public void UpdateActiveAlarms(IReadOnlyDictionary<string, ErrorCode> alarms)
        { }
    }
}
