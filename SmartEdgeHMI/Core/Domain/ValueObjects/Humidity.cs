using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartEdgeHMI.Core.Domain.ValueObjects;

/// <summary>相对湿度值对象, 内部以百分比保存</summary>
/// <remarks>
/// 工厂方法不做范围裁剪, 越界值原样保留, 由数据质量标记与报警逻辑判定。
/// JSON 序列化为裸数值(%RH), 不含单位。
/// </remarks>
[JsonConverter(typeof(HumidityJsonConverter))]
public readonly record struct Humidity
{
    private Humidity(double percent) => Percent = percent;

    /// <summary>相对湿度百分比, 单位 %RH</summary>
    public double Percent { get; }

    /// <summary>从 Modbus 保持寄存器原始值创建</summary>
    /// <param name="rawValue">按 int16 大端解析后的寄存器值</param>
    /// <remarks>设备侧缩放系数为 10, 即 452 表示 45.2 %RH</remarks>
    public static Humidity FromRawModbus(int rawValue) => new(rawValue / 10.0);

    public static Humidity FromPercent(double percent) => new(percent);

    public override string ToString() => $"{Percent:F1} %";
}

/// <summary>Humidity 的 JSON 转换器, 契约是单个数值</summary>
public sealed class HumidityJsonConverter : JsonConverter<Humidity>
{
    public override Humidity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
            throw new JsonException("Expected numeric value for Humidity.");
        return Humidity.FromPercent(reader.GetDouble());
    }

    public override void Write(Utf8JsonWriter writer, Humidity value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Percent);
}
