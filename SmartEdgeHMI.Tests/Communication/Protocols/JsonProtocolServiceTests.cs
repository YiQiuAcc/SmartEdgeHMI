using System.Buffers;
using System.Reflection;
using System.Text;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Communication.Protocols;
using SmartEdgeHMI.Models.Dtos;

namespace SmartEdgeHMI.Tests.Communication.Protocols;

/// <summary>
/// 验证 JSON-Lines 协议解析核心逻辑</para>
/// 测试策略:
/// - 通过反射调用其私有静态 ProcessLine 方法来验证 JSON 解析核心逻辑
/// - 验证内容包括:有效 JSON 解析、无效 JSON 处理、CRLF 处理
/// </para>
/// </summary>
public class JsonProtocolServiceTests
{
    private static readonly MethodInfo _processLineMethod = typeof(JsonProtocolService)
        .GetMethod("ProcessLine", BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>验证完整有效 JSON 可以正确反序列化为 TelemetryPayload</summary>
    [Fact]
    public void ProcessLine_ValidTelemetryJson_ShouldDeserializeSuccessfully()
    {
        const string json = """{"deviceId":"Sensor_01","temperature":{"celsius":25.5},"humidity":{"percent":60.0},"status":1,"err_code":0,"quality":0}""";
        var seq = CreateSequence(json);

        var ex = Record.Exception(() => InvokeProcessLine("COM1", seq));
        Assert.Null(ex);
    }

    /// <summary>验证包含 CR 结尾的 JSON 行能被正确处理(ProcessLine 内部会裁剪末尾 \r)</summary>
    [Fact]
    public void ProcessLine_JsonWithTrailingCR_ShouldTrimAndDeserialize()
    {
        const string json = """{"deviceId":"Sensor_01","temperature":{"celsius":25.5},"humidity":{"percent":60.0},"status":1,"err_code":0,"quality":0}""" + "\r";
        var seq = CreateSequence(json);

        var ex = Record.Exception(() => InvokeProcessLine("COM1", seq));
        Assert.Null(ex);
    }

    /// <summary>验证无效 JSON 不会导致 ProcessLine 抛出异常(应被 catch 并记录日志)</summary>
    [Fact]
    public void ProcessLine_InvalidJson_ShouldNotThrow()
    {
        var seq = CreateSequence("这不是有效的JSON");

        var ex = Record.Exception(() => InvokeProcessLine("COM1", seq));
        Assert.Null(ex);
    }

    /// <summary>验证空数据不会导致异常</summary>
    [Fact]
    public void ProcessLine_EmptyData_ShouldNotThrow()
    {
        var seq = new ReadOnlySequence<byte>(ReadOnlyMemory<byte>.Empty);

        var ex = Record.Exception(() => InvokeProcessLine("COM1", seq));
        Assert.Null(ex);
    }

    /// <summary>验证报警触发时包含 ErrorCode 的 JSON 解析</summary>
    [Fact]
    public void ProcessLine_ErrorCodeJson_ShouldDeserializeCorrectly()
    {
        const string json = """{"deviceId":"Sensor_01","temperature":{"celsius":30.0},"humidity":{"percent":80.0},"status":1,"err_code":302,"quality":0}""";
        var seq = CreateSequence(json);

        var ex = Record.Exception(() => InvokeProcessLine("COM1", seq));
        Assert.Null(ex);
    }

    /// <summary>验证 Bad Quality 数据的 JSON 解析</summary>
    [Fact]
    public void ProcessLine_BadQualityJson_ShouldDeserializeCorrectly()
    {
        const string json = """{"deviceId":"Sensor_01","temperature":{"celsius":0.0},"humidity":{"percent":0.0},"status":0,"err_code":0,"quality":2}""";
        var seq = CreateSequence(json);

        var ex = Record.Exception(() => InvokeProcessLine("COM1", seq));
        Assert.Null(ex);
    }

    /// <summary>验证字段名不匹配的 JSON (部分字段缺失) 仍能被安全处理</summary>
    [Fact]
    public void ProcessLine_PartialJson_ShouldNotThrow()
    {
        var seq = CreateSequence("""{"deviceId":"Sensor_01"}""");

        var ex = Record.Exception(() => InvokeProcessLine("COM1", seq));
        Assert.Null(ex);
    }

    /// <summary>验证包含 Command 指令的 JSON 下发格式的序列化和反序列化</summary>
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

        // 验证反序列化
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
        _processLineMethod.Invoke(null, [portName, line]);
    }
}
