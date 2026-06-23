using SmartEdgeHMI.Data.Entities;

namespace SmartEdgeHMI.Infrastructure.Math;

public static class LttbDownsampler
{
    public static List<SensorReadingRecord> Downsample(IReadOnlyList<SensorReadingRecord> data, int targetCount)
    {
        if (data.Count <= targetCount || targetCount < 3)
        {
            var list = new List<SensorReadingRecord>(data.Count);
            for (int i = 0; i < data.Count; i++) list.Add(data[i]);
            return list;
        }

        var result = new List<SensorReadingRecord>(targetCount) { data[0] };
        double bucketSize = (double)(data.Count - 2) / (targetCount - 2);

        long startTicks = data[0].Timestamp.Ticks;

        for (int bucketIndex = 0; bucketIndex < targetCount - 2; bucketIndex++)
        {
            int bucketStart = 1 + (int)(bucketIndex * bucketSize);
            int bucketEnd = 1 + (int)((bucketIndex + 1) * bucketSize);
            if (bucketEnd >= data.Count - 1) bucketEnd = data.Count - 1;

            var prev = result[^1];
            int nextBucketStart = bucketEnd;
            int nextBucketEnd = 1 + (int)((bucketIndex + 2) * bucketSize);
            if (nextBucketEnd >= data.Count) nextBucketEnd = data.Count - 1;

            double avgX = 0, avgY = 0;
            int avgCount = nextBucketEnd - nextBucketStart;
            if (avgCount > 0)
            {
                for (int i = nextBucketStart; i < nextBucketEnd; i++)
                {
                    // 使用相对偏移量，确保数值在 double 的高精度范围内
                    avgX += (data[i].Timestamp.Ticks - startTicks);
                    avgY += data[i].Temperature.Celsius;
                }
                avgX /= avgCount;
                avgY /= avgCount;
            }
            else
            {
                var last = data[^1];
                avgX = last.Timestamp.Ticks - startTicks;
                avgY = last.Temperature.Celsius;
            }

            double maxArea = -1;
            int selectedIndex = bucketStart;

            // 当前 prev 点相对起始时间的偏移量
            double prevX = prev.Timestamp.Ticks - startTicks;

            for (int i = bucketStart; i < bucketEnd; i++)
            {
                double currentX = data[i].Timestamp.Ticks - startTicks;

                double area = TriangleArea(
                    prevX, prev.Temperature.Celsius,
                    currentX, data[i].Temperature.Celsius,
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
        => System.Math.Abs(((x1 * (y2 - y3)) + (x2 * (y3 - y1)) + (x3 * (y1 - y2))) * 0.5);
}
