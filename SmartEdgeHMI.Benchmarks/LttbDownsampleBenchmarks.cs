using BenchmarkDotNet.Attributes;
using SmartEdgeHMI.Common;
using SmartEdgeHMI.Core.Domain.ValueObjects;
using SmartEdgeHMI.Data.Entities;
using SmartEdgeHMI.Utils.Math;

namespace SmartEdgeHMI.Benchmarks;

/// <summary>LTTB 降采样基准: 100 万级输入压缩至 1000 点, 考察耗时与分配。</summary>
[MemoryDiagnoser]
public class LttbDownsampleBenchmarks
{
    private SensorReadingRecord[] _data = [];

    /// <summary>输入点数</summary>
    [Params(10_000, 100_000, 1_000_000)]
    public int N;

    [GlobalSetup]
    public void Setup() => _data = GenerateData(N);

    /// <summary>把 N 个点降采样到固定 1000 点</summary>
    [Benchmark]
    public List<SensorReadingRecord> DownsampleTo1000() => LttbDownsampler.Downsample(_data, 1000);

    /// <summary>
    /// 生成确定性测试数据: 时间戳每点 +100ms 递增, 温湿度为固定种子的伪随机游走 (15-35°C / 20-80%)。
    /// 仅在 GlobalSetup 中调用, 不计入被测耗时。
    /// </summary>
    internal static SensorReadingRecord[] GenerateData(int count)
    {
        var rng = new Random(42);
        var data = new SensorReadingRecord[count];
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double temperature = 25, humidity = 50;

        for (int i = 0; i < count; i++)
        {
            temperature = Math.Clamp(temperature + (rng.NextDouble() - 0.5) * 0.5, 15, 35);
            humidity = Math.Clamp(humidity + (rng.NextDouble() - 0.5) * 0.8, 20, 80);

            data[i] = new SensorReadingRecord
            {
                Id = i + 1,
                DeviceId = "SIM-001",
                Timestamp = start.AddMilliseconds(i * 100L),
                Temperature = Temperature.FromCelsius(Math.Round(temperature, 2)),
                Humidity = Humidity.FromPercent(Math.Round(humidity, 2)),
                StatusCode = DeviceStatus.Online,
                ErrorCode = ErrorCode.NoError,
                QualityCode = DataQuality.Good,
            };
        }

        return data;
    }
}
