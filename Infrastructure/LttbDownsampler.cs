using SmartEdgeHMI.Models.Entities;

namespace SmartEdgeHMI.Infrastructure;

public static class LttbDownsampler
{
    /// <summary>
    /// Largest Triangle Three Buckets 降采样。首尾点强制保留，中间每桶 选取与前后已选点构成最大三角形面积的数据点，保持曲线的视觉特征。
    /// </summary>
    public static List<SensorReadingEntity> Downsample(List<SensorReadingEntity> data, int targetCount)
    {
        if (data.Count <= targetCount || targetCount < 3)
            return [.. data];

        var result = new List<SensorReadingEntity>(targetCount)
        {
            data[0]
        };

        double bucketSize = (double)(data.Count - 2) / (targetCount - 2);

        for (int bucketIndex = 0; bucketIndex < targetCount - 2; bucketIndex++)
        {
            int bucketStart = 1 + (int)(bucketIndex * bucketSize);
            int bucketEnd = 1 + (int)((bucketIndex + 1) * bucketSize);
            if (bucketEnd >= data.Count - 1) bucketEnd = data.Count - 1;

            var prev = result[^1];

            // 下一桶的平均点（用于三角形面积计算）
            int nextBucketStart = bucketEnd;
            int nextBucketEnd = 1 + (int)((bucketIndex + 2) * bucketSize);
            if (nextBucketEnd >= data.Count) nextBucketEnd = data.Count - 1;

            double avgX = 0, avgY = 0;
            int avgCount = nextBucketEnd - nextBucketStart;
            if (avgCount > 0)
            {
                for (int i = nextBucketStart; i < nextBucketEnd; i++)
                {
                    avgX += data[i].Timestamp.Ticks;
                    avgY += data[i].Temperature.Celsius;
                }
                avgX /= avgCount;
                avgY /= avgCount;
            }
            else
            {
                var last = data[data.Count - 1];
                avgX = last.Timestamp.Ticks;
                avgY = last.Temperature.Celsius;
            }

            double maxArea = -1;
            int selectedIndex = bucketStart;

            for (int i = bucketStart; i < bucketEnd; i++)
            {
                double area = TriangleArea(
                    prev.Timestamp.Ticks, prev.Temperature.Celsius,
                    data[i].Timestamp.Ticks, data[i].Temperature.Celsius,
                    avgX, avgY);

                if (area > maxArea)
                {
                    maxArea = area;
                    selectedIndex = i;
                }
            }

            result.Add(data[selectedIndex]);
        }

        result.Add(data[^1]);
        return result;
    }

    private static double TriangleArea(double x1, double y1, double x2, double y2, double x3, double y3)
        => Math.Abs((x1 * (y2 - y3) + x2 * (y3 - y1) + x3 * (y1 - y2)) * 0.5);
}
