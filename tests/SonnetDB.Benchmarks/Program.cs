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
//   dotnet run -c Release -- --m40-graph-evidence --quick （M40 Native Graph Preview 本地 evidence）
//   dotnet run -c Release -- --m40-weighted-path-evidence --quick （#362 加权路径收益矩阵）
//   dotnet run -c Release -- --m40-production-gate --quick （#367 门禁管线 smoke）
//   dotnet run -c Release -- --m40-production-gate --manifest <path> （#367 完整 evidence 判定）
//   dotnet run -c Release -- --filter *GraphWeightedPath* （#362 Dijkstra/A*/双向 Dijkstra 基准）
//   dotnet run -c Release -- --m41-baseline-evidence --quick （#368 性能合同与可观测性基线）
//   dotnet run -c Release -- --filter *M41P0AccessPath* （#369～#371 P0 快速路径对拍）
//   dotnet run -c Release -- --filter *M41RelationInputPushdown* （#372 关系输入下推对拍）
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
    string outputDirectory = ReadOutputDirectory(args, Path.Combine("artifacts", "m39-trigger-v2"));
    TriggerEvidenceReportRunner.Run(
        outputDirectory,
        args.Contains("--quick", StringComparer.OrdinalIgnoreCase) ? [1] : [1, 100, 10_000]);
    Console.WriteLine($"m39-trigger-evidence=PASS output={outputDirectory}");
    return;
}

if (args.Contains("--m40-graph-evidence", StringComparer.OrdinalIgnoreCase))
{
    string outputDirectory = ReadOutputDirectory(args, Path.Combine("artifacts", "m40-native-graph-preview"));
    GraphPreviewEvidenceReport report = GraphPreviewEvidenceRunner.Run(
        outputDirectory,
        args.Contains("--quick", StringComparer.OrdinalIgnoreCase));
    Console.WriteLine(
        $"m40-graph-local-smoke={report.Correctness} output={outputDirectory} "
        + $"correctness-recovery={report.CorrectnessRecovery} performance-capacity={report.PerformanceCapacity} "
        + $"release-decision={report.ReleaseDecision} fixed-hardware={report.FixedHardware} neo4j={report.Neo4jComparison}");
    return;
}

if (args.Contains("--m40-weighted-path-evidence", StringComparer.OrdinalIgnoreCase))
{
    string outputDirectory = ReadOutputDirectory(args, Path.Combine("artifacts", "m40-graph-weighted-path"));
    GraphWeightedPathEvidenceReport report = GraphWeightedPathEvidenceRunner.Run(
        outputDirectory,
        args.Contains("--quick", StringComparer.OrdinalIgnoreCase));
    Console.WriteLine(
        $"m40-weighted-path-local={report.LocalCorrectness} output={outputDirectory} "
        + $"algorithm-benefit={report.AlgorithmBenefit} fixed-hardware={report.FixedHardware} "
        + $"production-gate={report.ProductionGate}");
    return;
}

if (args.Contains("--m40-production-gate", StringComparer.OrdinalIgnoreCase))
{
    string outputDirectory = ReadOutputDirectory(args, Path.Combine("artifacts", "m40-graph-production-gate"));
    bool quick = args.Contains("--quick", StringComparer.OrdinalIgnoreCase);
    bool manifestRequested = args.Contains("--manifest", StringComparer.OrdinalIgnoreCase);
    string? manifestPath = ReadOption(args, "--manifest");
    if (quick && manifestRequested)
        throw new ArgumentException("--quick 与 --manifest 不能同时使用。");
    if (manifestRequested
        && (manifestPath is null || manifestPath.StartsWith("--", StringComparison.Ordinal)))
        throw new ArgumentException("--manifest 必须提供 M40 #367 evidence 清单路径。");
    if (!quick && manifestPath is null)
        throw new ArgumentException("--m40-production-gate 必须显式提供 --quick 或 --manifest <path>。");
    GraphProductionGateReport report = manifestPath is null
        ? GraphProductionGateRunner.RunQuick(outputDirectory)
        : GraphProductionGateRunner.EvaluateManifest(manifestPath, outputDirectory);
    Console.WriteLine(
        $"m40-production-local={report.LocalSmoke} output={outputDirectory} "
        + $"correctness-recovery={report.CorrectnessRecovery} "
        + $"performance-capacity={report.PerformanceCapacity} "
        + $"release-decision={report.ReleaseDecision} findings={report.Findings.Count}");
    if ((manifestPath is null && report.LocalSmoke != GraphProductionEvidenceStatus.Pass)
        || (manifestPath is not null && report.ReleaseDecision != GraphProductionEvidenceStatus.Pass))
    {
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Contains("--m41-baseline-evidence", StringComparer.OrdinalIgnoreCase))
{
    string outputDirectory = ReadOutputDirectory(args, Path.Combine("artifacts", "m41-performance-baseline"));
    M41PerformanceBaselineReport report = M41PerformanceBaselineRunner.Run(
        outputDirectory,
        args.Contains("--quick", StringComparer.OrdinalIgnoreCase));
    Console.WriteLine(
        $"m41-baseline-local={report.LocalCorrectness} output={outputDirectory} "
        + $"fixed-hardware={report.FixedHardware} production-gate={report.ProductionGate}");
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

// 执行 SegmentManager 生命周期烟测并验证每个动作返回有效段数量。
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

// 在独立迭代生命周期内执行一次 SegmentManager 动作。
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

// 执行关系表 DELETE/TRUNCATE 各路径烟测并核对受影响行数。
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

// 执行 M39 三种 DML、三条路径的成功与回滚证据矩阵。
static void RunTriggerBaselineSmoke()
{
    Console.WriteLine(
        "rows\toperation\tpath\trows_affected\telapsed_ms\twal_bytes\trowstore_bytes_delta\t"
        + "working_set_bytes\tmanaged_bytes\tallocated_bytes");
    foreach (int rows in new[] { 1, 100, 10_000 })
    {
        foreach (TriggerDmlOperation operation in Enum.GetValues<TriggerDmlOperation>())
        {
            foreach (TriggerPath path in Enum.GetValues<TriggerPath>())
            {
                var sample = new TriggerBaselineBenchmark
                {
                    Rows = rows,
                    Operation = operation,
                    Path = path,
                }.RunSingleIteration();
                Console.WriteLine(
                    $"{sample.Rows}\t{sample.Operation}\t{sample.Path}\t{sample.RowsAffected}\t"
                    + $"{sample.ElapsedMilliseconds:F3}\t{sample.WalBytes}\t{sample.RowStoreBytesDelta}\t"
                    + $"{sample.WorkingSetBytes}\t{sample.ManagedBytes}\t{sample.AllocatedBytes}");
                CollectBetweenEvidenceSamples();
            }
        }
    }

    Console.WriteLine(
        "rollback_rows\toperation\tpath\telapsed_ms\twal_bytes\tsource_rows_after_rollback\t"
        + "source_state_restored\taudit_state_restored\taudit_rows_after_rollback\t"
        + "allocated_bytes\tfailure_code");
    foreach (int rows in new[] { 1, 100, 10_000 })
    {
        foreach (TriggerDmlOperation operation in Enum.GetValues<TriggerDmlOperation>())
        {
            foreach (TriggerPath path in Enum.GetValues<TriggerPath>())
            {
                var sample = TriggerRollbackEvidence.RunSingleIteration(rows, path, operation);
                int expectedSourceRows = operation == TriggerDmlOperation.Insert ? 0 : rows;
                const int expectedAuditRows = 1;
                if (!sample.SourceStateRestored
                    || !sample.AuditStateRestored
                    || sample.SourceRowsAfterRollback != expectedSourceRows
                    || sample.AuditRowsAfterRollback != expectedAuditRows)
                {
                    throw new InvalidDataException(
                        $"M39 rollback evidence mismatch: rows={rows}, operation={operation}, path={path}, "
                        + $"source={sample.SourceRowsAfterRollback}, restored={sample.SourceStateRestored}, "
                        + $"audit={sample.AuditRowsAfterRollback}, auditRestored={sample.AuditStateRestored}");
                }

                Console.WriteLine(
                    $"{sample.Rows}\t{sample.Operation}\t{sample.Path}\t"
                    + $"{sample.ElapsedMilliseconds:F3}\t{sample.WalBytes}\t"
                    + $"{sample.SourceRowsAfterRollback}\t{sample.SourceStateRestored}\t{sample.AuditStateRestored}\t"
                    + $"{sample.AuditRowsAfterRollback}\t{sample.AllocatedBytes}\t{sample.FailureCode}");
                CollectBetweenEvidenceSamples();
            }
        }
    }

    Console.WriteLine("m39-trigger-baseline-smoke=PASS");
}

// 在证据样本之间回收托管堆，降低前序样本的保留内存干扰。
static void CollectBetweenEvidenceSamples()
{
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
}

// 读取报告输出目录；未指定时返回仓库内的默认 artifact 路径。
static string ReadOutputDirectory(string[] args, string defaultDirectory)
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

    return defaultDirectory;
}

// 读取单值命令行选项；未提供时返回 null。
static string? ReadOption(string[] args, string option)
{
    for (int index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(args[index + 1]))
        {
            return args[index + 1];
        }
    }

    return null;
}
