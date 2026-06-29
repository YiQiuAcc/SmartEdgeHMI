using SmartEdgeHMI.Data.Entities;

namespace SmartEdgeHMI.Utils.Math;

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

            int nextBucketStart = bucketEnd;
            int nextBucketEnd = 1 + (int)((bucketIndex + 2) * bucketSize);
            if (nextBucketEnd >= data.Count) nextBucketEnd = data.Count - 1;

            // 计算下一桶的平均参考点 (C)
            var (avgX, avgY) = CalculateNextBucketAverage(data, nextBucketStart, nextBucketEnd, startTicks);

            // 在当前桶中寻找与 prev(A) 和 avg(C) 构成最大三角形面积的点 (B)
            int selectedIndex = FindMaxAreaIndex(data, bucketStart, bucketEnd, result[^1], startTicks, avgX, avgY);

            result.Add(data[selectedIndex]);
        }

        result.Add(data[^1]);
        return result;
    }

    /// <summary>计算下一个桶中所有点的平均坐标</summary>
    private static (double X, double Y) CalculateNextBucketAverage(
        IReadOnlyList<SensorReadingRecord> data, int start, int end, long startTicks)
    {
        int avgCount = end - start;
        if (avgCount <= 0)
        {
            var last = data[^1];
            return (last.Timestamp.Ticks - startTicks, last.Temperature.Celsius);
        }

        double avgX = 0, avgY = 0;
        for (int i = start; i < end; i++)
        {
            avgX += (data[i].Timestamp.Ticks - startTicks);
            avgY += data[i].Temperature.Celsius;
        }

        return (avgX / avgCount, avgY / avgCount);
    }

    /// <summary>在当前桶内循环遍历, 寻找与前一个保留点、下一个桶平均点构成最大三角形面积的样本索引</summary>
    private static int FindMaxAreaIndex(
        IReadOnlyList<SensorReadingRecord> data, int start, int end,
        SensorReadingRecord prev, long startTicks, double avgX, double avgY)
    {
        double maxArea = -1;
        int selectedIndex = start;
        double prevX = prev.Timestamp.Ticks - startTicks;

        for (int i = start; i < end; i++)
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

        return selectedIndex;
    }

    private static double TriangleArea(double x1, double y1, double x2, double y2, double x3, double y3)
        => System.Math.Abs(((x1 * (y2 - y3)) + (x2 * (y3 - y1)) + (x3 * (y1 - y2))) * 0.5);
}
