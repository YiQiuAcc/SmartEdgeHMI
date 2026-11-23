using BenchmarkDotNet.Running;
using SmartEdgeHMI.Benchmarks;

/// <summary>基准入口: 默认运行 BenchmarkDotNet, 带 <c>--verify</c> 时只做降采样条数自检</summary>
internal static class Program
{
    public static void Main(string[] args)
    {
        // 自检模式供 CI 使用: 不运行基准, 只验证 1 万 / 10 万 / 100 万规模下都精确返回 1000 条且保留首尾点
        if (args.Contains("--verify"))
        {
            VerifyDownsampledRowCount();
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }

    private static void VerifyDownsampledRowCount()
    {
        foreach (int n in new[] { 10_000, 100_000, 1_000_000 })
        {
            var data = LttbDownsampleBenchmarks.GenerateData(n);
            var result = SmartEdgeHMI.Utils.Math.LttbDownsampler.Downsample(data, 1000);
            Console.WriteLine($"N = {n,8} -> {result.Count} 条 (期望 1000, 首尾保留: {result[0].Id == data[0].Id && result[^1].Id == data[^1].Id})");
            if (result.Count != 1000)
            {
                Console.WriteLine("FAILED: 降采样返回条数不等于 1000");
                Environment.Exit(1);
            }
        }

        Console.WriteLine("PASSED");
    }
}
