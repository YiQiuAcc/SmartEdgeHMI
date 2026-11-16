using SmartEdgeHMI.Core.Domain.ValueObjects;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Utils.Math;

namespace SmartEdgeHMI.Tests.Utils;

public class LttbDownsamplerTests
{
    private static readonly DateTime _baseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static SensorReadingRecord CreateReading(double celsius, int secondOffset)
        => new()
        {
            DeviceId = "TEST",
            Timestamp = _baseTime.AddSeconds(secondOffset),
            Temperature = Temperature.FromCelsius(celsius),
            Humidity = Humidity.FromPercent(50)
        };

    private static List<SensorReadingRecord> CreateSeries(int count, Func<int, double> valueFactory)
        => Enumerable.Range(0, count).Select(i => CreateReading(valueFactory(i), i)).ToList();

    [Fact]
    public void Downsample_InputLargerThanTarget_ReturnsExactlyTargetCount()
    {
        var data = CreateSeries(100, i => 25 + Math.Sin(i / 10.0) * 5);

        var result = LttbDownsampler.Downsample(data, 7);

        Assert.Equal(7, result.Count);
    }

    [Fact]
    public void Downsample_PreservesFirstAndLastPoints_TimestampsStrictlyIncreasing()
    {
        var data = CreateSeries(50, i => 20 + i % 7);

        var result = LttbDownsampler.Downsample(data, 9);

        Assert.Equal(data[0].Timestamp, result[0].Timestamp);
        Assert.Equal(data[0].Temperature.Celsius, result[0].Temperature.Celsius);
        Assert.Equal(data[^1].Timestamp, result[^1].Timestamp);
        Assert.Equal(data[^1].Temperature.Celsius, result[^1].Temperature.Celsius);

        for (int i = 1; i < result.Count; i++)
        {
            Assert.True(result[i].Timestamp > result[i - 1].Timestamp,
                $"输出时间戳应严格递增, 索引 {i} 处不满足");
        }
    }

    [Fact]
    public void Downsample_InputNotLargerThanTarget_ReturnsAllPoints()
    {
        var data = CreateSeries(5, i => 25 + i * 0.5);

        var result = LttbDownsampler.Downsample(data, 10);

        Assert.Equal(data.Count, result.Count);
        for (int i = 0; i < data.Count; i++)
        {
            Assert.Equal(data[i].Timestamp, result[i].Timestamp);
            Assert.Equal(data[i].Temperature.Celsius, result[i].Temperature.Celsius);
        }
    }

    [Fact]
    public void Downsample_TargetCountLessThanThree_ReturnsFullCopy()
    {
        var data = CreateSeries(10, i => 25 + i % 3);

        var result = LttbDownsampler.Downsample(data, 2);

        Assert.Equal(data.Count, result.Count);
        for (int i = 0; i < data.Count; i++)
        {
            Assert.Equal(data[i].Timestamp, result[i].Timestamp);
            Assert.Equal(data[i].Temperature.Celsius, result[i].Temperature.Celsius);
        }
    }

    [Fact]
    public void Downsample_SpikeInInput_IsPreserved()
    {
        // 60 个 25°C 平坦样本, 中间插入一个 85°C 的孤立尖峰
        var data = CreateSeries(60, i => i == 30 ? 85 : 25);

        var result = LttbDownsampler.Downsample(data, 6);

        Assert.Contains(result, r => Math.Abs(r.Temperature.Celsius - 85) < 1e-9);
    }
}
