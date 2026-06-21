using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartEdgeHMI.Models.ValueObjects;

[JsonConverter(typeof(HumidityJsonConverter))]
public readonly record struct Humidity
{
    private Humidity(double percent) => Percent = percent;

    /// <summary>相对湿度百分比 (0-100)</summary>
    public double Percent { get; }

    /// <summary>从 Modbus 原始寄存器值创建(寄存器值 / 10 = 百分比)</summary>
    public static Humidity FromRawModbus(int rawValue) => new(rawValue / 10.0);

    /// <summary>从百分比值创建</summary>
    public static Humidity FromPercent(double percent) => new(percent);

    public override string ToString() => $"{Percent:F1} %";
}

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
