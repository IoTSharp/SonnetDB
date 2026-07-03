using System.Diagnostics;
using SonnetMQ;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// SonnetMQ publish 延迟基准（P5a #230 基线）：手动采样每条 publish 的耗时并输出
/// P50/P90/P99/P999 百分位，度量 <see cref="SonnetMqOptions.FlushOnPublish"/> /
/// <see cref="SonnetMqOptions.SyncOnPublish"/> 三种持久性档位的延迟分布。
/// BenchmarkDotNet 的迭代均值模型给不了尾延迟，故以独立 runner 通过
/// <c>dotnet run -c Release -- --mq-latency</c> 触发。
/// </summary>
public static class MqLatencyBenchmark
{
    private const int WarmupCount = 5_000;
    private const int SampleCount = 100_000;
    private static readonly int[] PayloadSizes = [64, 1024];

    private readonly record struct DurabilityMode(string Label, bool FlushOnPublish, bool SyncOnPublish);

    private static readonly DurabilityMode[] Modes =
    [
        new("no-flush     (FlushOnPublish=false)", false, false),
        new("os-flush     (FlushOnPublish=true) ", true, false),
        new("fsync-durable(SyncOnPublish=true)  ", true, true),
    ];

    /// <summary>运行延迟基准并把百分位表打印到标准输出。</summary>
    public static void Run()
    {
        Console.WriteLine();
        Console.WriteLine("=== SonnetMQ publish 延迟基准（P5a #230 基线）===");
        Console.WriteLine($"warmup={WarmupCount:N0}  samples={SampleCount:N0}  单位=微秒(µs)");
        Console.WriteLine();
        Console.WriteLine($"{"mode",-38} {"payload",8} {"p50",10} {"p90",10} {"p99",10} {"p99.9",10} {"max",10}");
        Console.WriteLine(new string('-', 108));

        foreach (int payloadBytes in PayloadSizes)
        {
            foreach (var mode in Modes)
            {
                var stats = MeasureMode(mode, payloadBytes);
                Console.WriteLine(
                    $"{mode.Label,-38} {payloadBytes,8} " +
                    $"{stats.P50,10:F2} {stats.P90,10:F2} {stats.P99,10:F2} {stats.P999,10:F2} {stats.Max,10:F2}");
            }

            Console.WriteLine();
        }
    }

    private static LatencyStats MeasureMode(DurabilityMode mode, int payloadBytes)
    {
        byte[] payload = new byte[payloadBytes];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i * 31);

        string rootDir = Path.Combine(Path.GetTempPath(), $"sonnetmq_lat_{Guid.NewGuid():N}");
        using var store = SonnetMqStore.Open(new SonnetMqOptions
        {
            Path = rootDir,
            FlushOnPublish = mode.FlushOnPublish,
            SyncOnPublish = mode.SyncOnPublish,
            RetentionInterval = TimeSpan.Zero,
        });

        try
        {
            const string topic = "bench.latency";
            for (int i = 0; i < WarmupCount; i++)
                store.Publish(topic, payload);

            var samples = new double[SampleCount];
            double ticksToMicros = 1_000_000.0 / Stopwatch.Frequency;
            for (int i = 0; i < SampleCount; i++)
            {
                long start = Stopwatch.GetTimestamp();
                store.Publish(topic, payload);
                samples[i] = (Stopwatch.GetTimestamp() - start) * ticksToMicros;
            }

            Array.Sort(samples);
            return new LatencyStats(
                Percentile(samples, 0.50),
                Percentile(samples, 0.90),
                Percentile(samples, 0.99),
                Percentile(samples, 0.999),
                samples[^1]);
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                try { Directory.Delete(rootDir, recursive: true); }
                catch (IOException) { /* best effort */ }
            }
        }
    }

    private static double Percentile(double[] sorted, double q)
    {
        int index = (int)Math.Ceiling(q * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private readonly record struct LatencyStats(double P50, double P90, double P99, double P999, double Max);
}
