using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartEdgeHMI.Models.ValueObjects;

[JsonConverter(typeof(TemperatureJsonConverter))]
public readonly record struct Temperature
{
    private Temperature(double celsius) => Celsius = celsius;

    /// <summary>摄氏度</summary>
    public double Celsius { get; }

    /// <summary>从 Modbus 原始寄存器值创建(寄存器值 / 10 = 摄氏度)</summary>
    public static Temperature FromRawModbus(int rawValue) => new(rawValue / 10.0);

    /// <summary>从摄氏度值创建</summary>
    public static Temperature FromCelsius(double celsius) => new(celsius);

    public override string ToString() => $"{Celsius:F1} °C";
}

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
