using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartEdgeHMI.Core.Domain.ValueObjects;

/// <summary>温度值对象, 内部恒以摄氏度保存</summary>
/// <remarks>
/// 工厂方法不做范围校验, 传感器异常值会原样保留, 由报警与质量位判定负责拦截。
/// JSON 序列化为裸数值(°C), 不含单位。
/// </remarks>
[JsonConverter(typeof(TemperatureJsonConverter))]
public readonly record struct Temperature
{
    private Temperature(double celsius) => Celsius = celsius;

    /// <summary>摄氏度, 单位 °C</summary>
    public double Celsius { get; }

    /// <summary>从 Modbus 保持寄存器原始值创建</summary>
    /// <param name="rawValue">按 int16 大端解析后的寄存器值</param>
    /// <remarks>设备侧缩放系数为 10, 即 253 表示 25.3 °C</remarks>
    public static Temperature FromRawModbus(int rawValue) => new(rawValue / 10.0);

    public static Temperature FromCelsius(double celsius) => new(celsius);

    public override string ToString() => $"{Celsius:F1} °C";
}

/// <summary>Temperature 的 JSON 转换器, 契约是单个数值</summary>
public sealed class TemperatureJsonConverter : JsonConverter<Temperature>
{
    public override Temperature Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
            throw new JsonException("Expected numeric value for Temperature.");
        return Temperature.FromCelsius(reader.GetDouble());
    }

    public override void Write(Utf8JsonWriter writer, Temperature value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Celsius);
}
