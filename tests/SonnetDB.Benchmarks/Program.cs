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
//   dotnet run -c Release -- --table-delete-smoke （#126.1 delete/truncate 路径烟测）
//   dotnet run -c Release -- --m39-trigger-baseline-smoke （#333 触发器基线证据）
//   dotnet run -c Release -- --m39-trigger-evidence --output artifacts/m39-trigger-v2 （#333 JSON/Markdown 报告）
//   dotnet run -c Release -- --filter *         （运行所有基准）
//
// 运行前请先启动外部数据库（见 docker/docker-compose.yml）：
//   docker compose -f tests/SonnetDB.Benchmarks/docker/docker-compose.yml up -d
if (args.Contains("--segment-maintenance-smoke", StringComparer.OrdinalIgnoreCase))
{
    RunSegmentMaintenanceSmoke();
    return;
}

if (args.Contains("--table-delete-smoke", StringComparer.OrdinalIgnoreCase))
{
    RunTableDeleteSmoke();
    return;
}

if (args.Contains("--m39-trigger-baseline-smoke", StringComparer.OrdinalIgnoreCase))
{
    RunTriggerBaselineSmoke();
    return;
}

if (args.Contains("--m39-trigger-evidence", StringComparer.OrdinalIgnoreCase))
{
    string outputDirectory = ReadOutputDirectory(args);
    TriggerEvidenceReportRunner.Run(
        outputDirectory,
        args.Contains("--quick", StringComparer.OrdinalIgnoreCase) ? [1] : [1, 100, 10_000]);
    Console.WriteLine($"m39-trigger-evidence=PASS output={outputDirectory}");
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

static void RunTableDeleteSmoke()
{
    var benchmark = new TableDeleteBenchmark { Rows = 1_000 };
    benchmark.IterationSetup();
    try
    {
        if (benchmark.DeleteRowByRow() != benchmark.Rows
            || benchmark.DeleteWithBatchTombstones() != benchmark.Rows
            || benchmark.DeleteWithGeneration() != benchmark.Rows
            || benchmark.TruncateTable() != benchmark.Rows)
        {
            throw new InvalidDataException("table delete smoke 返回的受影响行数不一致。");
        }
    }
    finally
    {
        benchmark.IterationCleanup();
    }

    Console.WriteLine("table-delete-smoke=PASS");
}

static void RunTriggerBaselineSmoke()
{
    Console.WriteLine(
        "rows\tpath\trows_inserted\telapsed_ms\twal_bytes\trowstore_bytes\tworking_set_bytes\tmanaged_bytes\tallocated_bytes");
    foreach (int rows in new[] { 1, 100, 10_000 })
    {
        foreach (TriggerPath path in Enum.GetValues<TriggerPath>())
        {
            var sample = new TriggerBaselineBenchmark
            {
                Rows = rows,
                Path = path,
            }.RunSingleIteration();
            Console.WriteLine(
                $"{sample.Rows}\t{sample.Path}\t{sample.RowsInserted}\t"
                + $"{sample.ElapsedMilliseconds:F3}\t{sample.WalBytes}\t{sample.RowStoreBytes}\t"
                + $"{sample.WorkingSetBytes}\t{sample.ManagedBytes}\t{sample.AllocatedBytes}");
            CollectBetweenEvidenceSamples();
        }
    }

    Console.WriteLine(
        "rollback_rows\tpath\telapsed_ms\twal_bytes\tsource_rows_after_rollback\t"
        + "audit_rows_after_rollback\tallocated_bytes\tfailure_code");
    foreach (int rows in new[] { 1, 100, 10_000 })
    {
        foreach (TriggerPath path in Enum.GetValues<TriggerPath>())
        {
            var sample = TriggerRollbackEvidence.RunSingleIteration(rows, path);
            int expectedAuditRows = path == TriggerPath.NoTrigger ? 0 : 1;
            if (sample.SourceRowsAfterRollback != 0 || sample.AuditRowsAfterRollback != expectedAuditRows)
            {
                throw new InvalidDataException(
                    $"M39 rollback evidence mismatch: rows={rows}, path={path}, "
                    + $"source={sample.SourceRowsAfterRollback}, audit={sample.AuditRowsAfterRollback}");
            }

            Console.WriteLine(
                $"{sample.Rows}\t{sample.Path}\t{sample.ElapsedMilliseconds:F3}\t{sample.WalBytes}\t"
                + $"{sample.SourceRowsAfterRollback}\t{sample.AuditRowsAfterRollback}\t"
                + $"{sample.AllocatedBytes}\t{sample.FailureCode}");
            CollectBetweenEvidenceSamples();
        }
    }

    Console.WriteLine("m39-trigger-baseline-smoke=PASS");
}

static void CollectBetweenEvidenceSamples()
{
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
}

static string ReadOutputDirectory(string[] args)
{
    const string option = "--output";
    for (int index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
        {
            string value = args[index + 1];
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
    }

    return Path.Combine("artifacts", "m39-trigger-v2");
}
