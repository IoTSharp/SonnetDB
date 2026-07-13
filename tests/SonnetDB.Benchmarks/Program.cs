using BenchmarkDotNet.Running;
using SonnetDB.Benchmarks.Benchmarks;

// BenchmarkDotNet 需要在 Release 模式下运行。
// 使用示例：
//   dotnet run -c Release -- --filter *Insert*
//   dotnet run -c Release -- --filter *Query*
//   dotnet run -c Release -- --filter *Aggregate*
//   dotnet run -c Release -- --filter *Compaction*
//   dotnet run -c Release -- --filter *SegmentManagerMaintenance*
//   dotnet run -c Release -- --filter *Vector*
//   dotnet run -c Release -- --filter *MqThroughput*
//   dotnet run -c Release -- --filter *FrameEncoding*   （二进制帧 vs JSON+Base64 编解码）
//   dotnet run -c Release -- --filter *SparkplugDecode* （Sparkplug protobuf 解码与 Point 映射）
//   dotnet run -c Release -- --mq-latency   （SonnetMQ publish 尾延迟百分位）
//   dotnet run -c Release -- --segment-maintenance-smoke （#124 基准生命周期烟测）
//   dotnet run -c Release -- --filter *         （运行所有基准）
//
// 运行前请先启动外部数据库（见 docker/docker-compose.yml）：
//   docker compose -f tests/SonnetDB.Benchmarks/docker/docker-compose.yml up -d
if (args.Contains("--segment-maintenance-smoke", StringComparer.OrdinalIgnoreCase))
{
    RunSegmentMaintenanceSmoke();
    return;
}

if (args.Contains("--mq-latency", StringComparer.OrdinalIgnoreCase))
{
    MqLatencyBenchmark.Run();
    return;
}

if (args.Contains("--comparison-smoke", StringComparer.OrdinalIgnoreCase))
{
    await DatabaseComparisonBenchmark.RunSmokeComparison().ConfigureAwait(false);
    return;
}

if (args.Contains("--comparison-server-smoke", StringComparer.OrdinalIgnoreCase))
{
    await DatabaseComparisonBenchmark.RunServerSmokeComparison().ConfigureAwait(false);
    return;
}

if (args.Contains("--comparison-full", StringComparer.OrdinalIgnoreCase))
{
    await DatabaseComparisonBenchmark.RunFullComparison().ConfigureAwait(false);
    return;
}

if (args.Contains("--comparison", StringComparer.OrdinalIgnoreCase))
{
    await DatabaseComparisonBenchmark.RunComparison().ConfigureAwait(false);
    return;
}

if (args.Contains("--comparison-server", StringComparer.OrdinalIgnoreCase))
{
    await DatabaseComparisonBenchmark.RunServerComparison().ConfigureAwait(false);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

static void RunSegmentMaintenanceSmoke()
{
    var benchmark = new SegmentManagerMaintenanceBenchmark
    {
        LoadedSegments = 16,
        QueryWorkers = 4,
    };

    benchmark.GlobalSetup();
    try
    {
        RunIteration(benchmark, static value => value.FullIndexRebuildReference());
        RunIteration(benchmark, static value => value.AddSegment());
        RunIteration(benchmark, static value => value.SwapSegments());
        RunIteration(benchmark, static value => value.DropSegments());
    }
    finally
    {
        benchmark.GlobalCleanup();
    }

    Console.WriteLine("segment-maintenance-smoke=PASS");
}

static void RunIteration(
    SegmentManagerMaintenanceBenchmark benchmark,
    Func<SegmentManagerMaintenanceBenchmark, int> action)
{
    benchmark.IterationSetup();
    try
    {
        if (action(benchmark) <= 0)
            throw new InvalidDataException("SegmentManager 维护烟测返回了无效段数量。");
    }
    finally
    {
        benchmark.IterationCleanup();
    }
}
