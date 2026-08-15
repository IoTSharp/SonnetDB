using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonnetDB.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>M41 #368 固定关系查询语料、合成数据与执行证据报告 runner。</summary>
public static class M41PerformanceBaselineRunner
{
    private const int QuickRowCount = 64;
    private const int FullRowCount = 10_000;
    private const int QuickIterations = 3;
    private const int FullIterations = 7;
    private const int BatchSize = 256;
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>运行本地基线并写出机器可读 JSON 与 Markdown 报告。</summary>
    /// <param name="outputDirectory">报告输出目录。</param>
    /// <param name="quick">是否使用缩规模 quick smoke 数据集。</param>
    /// <returns>本次运行生成的报告。</returns>
    public static M41PerformanceBaselineReport Run(string outputDirectory, bool quick = false)
        => Run(
            outputDirectory,
            quick ? QuickRowCount : FullRowCount,
            quick ? QuickIterations : FullIterations,
            quick ? "quick_smoke" : "local_baseline");

    /// <summary>以指定规模运行基线；用于报告合同测试，不构成固定硬件证据。</summary>
    /// <param name="outputDirectory">报告输出目录。</param>
    /// <param name="rowCount">审计与任务表的确定性行数，至少 64。</param>
    /// <param name="iterations">每条查询的正式采样次数。</param>
    /// <returns>本次运行生成的报告。</returns>
    public static M41PerformanceBaselineReport Run(
        string outputDirectory,
        int rowCount,
        int iterations)
        => Run(outputDirectory, rowCount, iterations, "contract_smoke");

    private static M41PerformanceBaselineReport Run(
        string outputDirectory,
        int rowCount,
        int iterations,
        string mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (rowCount < QuickRowCount)
            throw new ArgumentOutOfRangeException(nameof(rowCount), "M41 基线至少需要 64 行数据。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);

        Directory.CreateDirectory(outputDirectory);
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        string databaseRoot = Path.Combine(
            Path.GetTempPath(),
            "sndb-m41-performance-baseline-" + Guid.NewGuid().ToString("N"));
        M41QueryEvidence[] workloads;
        int deviceCount = Math.Clamp(rowCount / 8, 8, 128);
        try
        {
            using var database = Tsdb.Open(new TsdbOptions { RootDirectory = databaseRoot });
            CreateDataset(database, rowCount, deviceCount);
            workloads = CreateWorkloads(rowCount)
                .Select(workload => Measure(database, workload, iterations))
                .ToArray();
        }
        finally
        {
            TryDeleteDirectory(databaseRoot);
        }

        DateTimeOffset finishedUtc = DateTimeOffset.UtcNow;
        var report = new M41PerformanceBaselineReport(
            "m41-performance-baseline-v1",
            "#368",
            ResolveCommitSha(),
            startedUtc,
            finishedUtc,
            mode,
            new M41DatasetEvidence(
                "mulei-relational-synthetic-v1",
                rowCount,
                rowCount,
                deviceCount,
                20,
                20260815),
            CreateEnvironment(),
            workloads,
            "PASS",
            "NOT_RUN",
            "NOT_RUN",
            "NOT_APPLICABLE_EMBEDDED",
            [
                "本地 runner 固化查询形状、正确性和执行证据合同，不构成固定目标硬件容量或生产 SLO 声明。",
                "嵌入式执行不经过 Server SQL permit；队列等待须在 REST/Frame 生产复测中单独采集。",
                "报告不保存参数值、行内容、数据库目录或机器名；query fingerprint 仅基于规范化 SQL。",
            ]);

        File.WriteAllText(
            Path.Combine(outputDirectory, "m41-performance-baseline.json"),
            JsonSerializer.Serialize(report, M41PerformanceBaselineJsonContext.Default.M41PerformanceBaselineReport),
            Utf8WithoutBom);
        File.WriteAllText(
            Path.Combine(outputDirectory, "m41-performance-baseline.md"),
            BuildMarkdown(report),
            Utf8WithoutBom);
        return report;
    }

    private static IReadOnlyList<M41Workload> CreateWorkloads(int rowCount)
    {
        int nullableThreshold = rowCount - 8;
        return
        [
            new(
                "indexed_exists",
                """
                SELECT EXISTS (
                    SELECT 1 FROM m41_audits a
                    WHERE a.idempotency_key = 'key-00000002' AND a.status = 'ready'
                )
                """,
                static result =>
                {
                    if (result.Rows.Count != 1
                        || result.Rows[0].Count != 1
                        || result.Rows[0][0] is not true)
                    {
                        throw new InvalidDataException("M41 indexed EXISTS 未返回预期 true。");
                    }
                }),
            new(
                "scalar_in",
                """
                SELECT id FROM m41_tasks
                WHERE device_id IN (SELECT id FROM m41_devices WHERE region = 'north')
                ORDER BY id LIMIT 20
                """,
                static result => ValidateIds(result, static id => ((id - 1) % 8 + 1) % 2 == 0, 20)),
            new(
                "nullable_or",
                $"""
                SELECT id FROM m41_tasks
                WHERE completed_at IS NULL OR completed_at >= {nullableThreshold}
                ORDER BY id LIMIT 20
                """,
                result => ValidateIds(result, id => id % 5 == 0 || id >= nullableThreshold, maximumRows: 20)),
            new(
                "multi_table_join",
                """
                SELECT t.id, d.region FROM m41_tasks t
                JOIN m41_devices d ON t.device_id = d.id
                WHERE t.status = 'ready'
                ORDER BY t.id LIMIT 20
                """,
                static result =>
                {
                    if (result.Rows.Count != 20
                        || result.Rows.Any(static row => row.Count != 2 || (long)row[0]! % 2 != 0))
                    {
                        throw new InvalidDataException("M41 JOIN 结果不符合固定语料合同。");
                    }
                }),
            new(
                "descending_pagination",
                """
                SELECT id, occurred_at FROM m41_audits
                WHERE occurred_at >= 1
                ORDER BY occurred_at DESC, id DESC LIMIT 20 OFFSET 20
                """,
                result =>
                {
                    if (result.Rows.Count != 20
                        || (long)result.Rows[0][0]! != rowCount - 20
                        || result.Rows.Any(static row => !Equals(row[0], row[1])))
                    {
                        throw new InvalidDataException("M41 倒序分页结果不符合固定语料合同。");
                    }
                }),
        ];
    }

    private static M41QueryEvidence Measure(Tsdb database, M41Workload workload, int iterations)
    {
        _ = ExecuteOnce(database, workload);
        var samples = new M41ExecutionSample[iterations];
        for (int index = 0; index < iterations; index++)
            samples[index] = ExecuteOnce(database, workload);

        int returnedRows = RequireStable(samples, static sample => sample.ReturnedRows, "returned rows");
        long candidateRows = RequireStable(samples, static sample => sample.Metrics.CandidateRows, "candidate rows");
        long examinedRows = RequireStable(samples, static sample => sample.Metrics.ExaminedRows, "examined rows");
        long logicalReads = RequireStable(samples, static sample => sample.Metrics.LogicalReads, "logical reads");
        string? accessPath = Merge(samples.Select(static sample => sample.Metrics.AccessPath));
        string? indexName = Merge(samples.Select(static sample => sample.Metrics.IndexName));
        string? fallbackReason = Merge(samples.Select(static sample => sample.Metrics.FallbackReason));
        string normalizedSql = SqlFingerprint.Normalize(workload.Sql);

        return new M41QueryEvidence(
            workload.Name,
            normalizedSql,
            SqlFingerprint.Compute(normalizedSql),
            "PASS",
            accessPath,
            indexName,
            fallbackReason,
            iterations,
            returnedRows,
            candidateRows,
            examinedRows,
            returnedRows == 0 ? 0 : examinedRows / (double)returnedRows,
            logicalReads,
            Percentile(samples.Select(static sample => sample.Metrics.ExecutionElapsedMs), 0.50),
            Percentile(samples.Select(static sample => sample.Metrics.ExecutionElapsedMs), 0.95),
            Percentile(samples.Select(static sample => sample.Metrics.ExecutionElapsedMs), 0.99),
            Percentile(samples.Select(static sample => sample.Metrics.AllocatedBytes), 0.50),
            Percentile(samples.Select(static sample => sample.Metrics.AllocatedBytes), 0.95),
            Percentile(samples.Select(static sample => sample.Metrics.AllocatedBytes), 0.99),
            0,
            Percentile(samples.Select(static sample => sample.Metrics.TableLockWaitMs), 0.95),
            Percentile(samples.Select(static sample => sample.Metrics.KvLockWaitMs), 0.95),
            Percentile(samples.Select(static sample => sample.Metrics.WalFsyncMs), 0.95),
            samples.Sum(static sample => sample.Metrics.PhysicalReads),
            samples.Sum(static sample => sample.Metrics.PhysicalReadBytes),
            samples.Sum(static sample => sample.Metrics.PhysicalWrites),
            samples.Sum(static sample => sample.Metrics.PhysicalWriteBytes),
            samples.Sum(static sample => sample.Metrics.Gen0Collections),
            samples.Sum(static sample => sample.Metrics.Gen1Collections),
            samples.Sum(static sample => sample.Metrics.Gen2Collections));
    }

    private static M41ExecutionSample ExecuteOnce(Tsdb database, M41Workload workload)
    {
        var metrics = new SqlExecutionMetrics();
        var result = SqlExecutor.Execute(
            database,
            databaseName: "m41_baseline",
            workload.Sql,
            parameters: null,
            controlPlane: null,
            new SqlExecutionOptions { Metrics = metrics }) as SelectExecutionResult
            ?? throw new InvalidDataException($"M41 workload {workload.Name} 未返回 SELECT 结果。");
        workload.Validate(result);
        return new M41ExecutionSample(result.Rows.Count, metrics.Complete());
    }

    private static void CreateDataset(Tsdb database, int rowCount, int deviceCount)
    {
        SqlExecutor.Execute(database,
            "CREATE TABLE m41_devices (id INT, region STRING, active BOOL, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "CREATE TABLE m41_tasks (id INT, device_id INT, status STRING, completed_at INT NULL, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "CREATE TABLE m41_audits (id INT, idempotency_key STRING, status STRING, occurred_at INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "CREATE INDEX ix_m41_tasks_device ON m41_tasks (device_id)");
        SqlExecutor.Execute(database,
            "CREATE INDEX ix_m41_tasks_completed ON m41_tasks (completed_at, id)");
        SqlExecutor.Execute(database,
            "CREATE UNIQUE INDEX ux_m41_audits_key ON m41_audits (idempotency_key)");
        SqlExecutor.Execute(database,
            "CREATE INDEX ix_m41_audits_occurred ON m41_audits (occurred_at, id)");

        var devices = new StringBuilder(
            "INSERT INTO m41_devices (id, region, active) VALUES ");
        for (int id = 1; id <= deviceCount; id++)
        {
            if (id != 1)
                devices.Append(',');
            devices.Append('(')
                .Append(id)
                .Append(id % 2 == 0 ? ",'north',TRUE)" : ",'south',TRUE)");
        }
        SqlExecutor.Execute(database, devices.ToString());

        for (int start = 1; start <= rowCount; start += BatchSize)
        {
            int end = Math.Min(rowCount, start + BatchSize - 1);
            var tasks = new StringBuilder(
                "INSERT INTO m41_tasks (id, device_id, status, completed_at) VALUES ");
            var audits = new StringBuilder(
                "INSERT INTO m41_audits (id, idempotency_key, status, occurred_at) VALUES ");
            for (int id = start; id <= end; id++)
            {
                if (id != start)
                {
                    tasks.Append(',');
                    audits.Append(',');
                }

                int deviceId = ((id - 1) % deviceCount) + 1;
                string status = id % 2 == 0 ? "ready" : "blocked";
                tasks.Append('(')
                    .Append(id)
                    .Append(',')
                    .Append(deviceId)
                    .Append(",'")
                    .Append(status)
                    .Append("',")
                    .Append(id % 5 == 0 ? "NULL" : id.ToString(CultureInfo.InvariantCulture))
                    .Append(')');
                audits.Append('(')
                    .Append(id)
                    .Append(",'key-")
                    .Append(id.ToString("D8", CultureInfo.InvariantCulture))
                    .Append("','")
                    .Append(status)
                    .Append("',")
                    .Append(id)
                    .Append(')');
            }
            SqlExecutor.Execute(database, tasks.ToString());
            SqlExecutor.Execute(database, audits.ToString());
        }
    }

    private static void ValidateIds(
        SelectExecutionResult result,
        Func<long, bool> predicate,
        int maximumRows)
    {
        if (result.Rows.Count == 0
            || result.Rows.Count > maximumRows
            || result.Rows.Any(row => row.Count != 1 || row[0] is not long id || !predicate(id)))
        {
            throw new InvalidDataException("M41 单列查询结果不符合固定语料合同。");
        }
    }

    private static T RequireStable<T>(
        IReadOnlyList<M41ExecutionSample> samples,
        Func<M41ExecutionSample, T> selector,
        string field)
        where T : IEquatable<T>
    {
        T expected = selector(samples[0]);
        if (samples.Skip(1).Any(sample => !selector(sample).Equals(expected)))
            throw new InvalidDataException($"M41 {field} 在重复样本间不稳定。");
        return expected;
    }

    private static string? Merge(IEnumerable<string?> values)
    {
        string? merged = null;
        foreach (string? value in values)
        {
            if (value is null)
                continue;
            merged = merged is null || string.Equals(merged, value, StringComparison.Ordinal)
                ? value
                : "mixed";
        }
        return merged;
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] ordered = values.Order().ToArray();
        int index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static long Percentile(IEnumerable<long> values, double percentile)
    {
        long[] ordered = values.Where(static value => value >= 0).Order().ToArray();
        if (ordered.Length == 0)
            return -1;
        int index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static M41EnvironmentEvidence CreateEnvironment()
    {
        string root = Path.GetPathRoot(Path.GetTempPath()) ?? Path.DirectorySeparatorChar.ToString();
        var drive = new DriveInfo(root);
        return new M41EnvironmentEvidence(
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            drive.DriveFormat,
            drive.AvailableFreeSpace);
    }

    private static string ResolveCommitSha()
    {
        string? configured = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("rev-parse");
            startInfo.ArgumentList.Add("HEAD");
            using var process = Process.Start(startInfo);
            if (process is null)
                return "unknown";
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode != 0 || output.Length == 0)
                return "unknown";

            var statusStartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            statusStartInfo.ArgumentList.Add("status");
            statusStartInfo.ArgumentList.Add("--porcelain");
            statusStartInfo.ArgumentList.Add("--untracked-files=normal");
            using var statusProcess = Process.Start(statusStartInfo);
            if (statusProcess is null)
                return output + "-status-unknown";
            string status = statusProcess.StandardOutput.ReadToEnd();
            statusProcess.WaitForExit();
            if (statusProcess.ExitCode != 0)
                return output + "-status-unknown";
            return status.Length == 0 ? output : output + "-dirty";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string BuildMarkdown(M41PerformanceBaselineReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("# M41 Performance Contract and Observability Baseline");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Schema: `{report.Schema}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Issue: `{report.Issue}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Commit: `{report.CommitSha}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Mode: `{report.Mode}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Local correctness: `{report.LocalCorrectness}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Fixed hardware: `{report.FixedHardware}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Production gate: `{report.ProductionGate}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- SQL permit queue: `{report.SqlPermitQueueEvidence}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Rows: `{report.Dataset.TaskRows:N0}` tasks / `{report.Dataset.AuditRows:N0}` audits");
        text.AppendLine();
        text.AppendLine("## Query evidence");
        text.AppendLine();
        text.AppendLine("| Workload | Path | Fallback | Returned | Examined | Amplification | P50 ms | P95 ms | P99 ms | Alloc P95 | GC 0/1/2 |");
        text.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | ");
        foreach (M41QueryEvidence query in report.Queries)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {query.Name} | {query.AccessPath ?? "n/a"} | {query.FallbackReason ?? "none"} | "
                + $"{query.ReturnedRows:N0} | {query.ExaminedRows:N0} | {query.ExaminedToReturnedRatio:F2} | "
                + $"{query.ExecutionP50Ms:F3} | {query.ExecutionP95Ms:F3} | {query.ExecutionP99Ms:F3} | "
                + $"{query.AllocatedBytesP95:N0} | {query.Gen0Collections}/{query.Gen1Collections}/{query.Gen2Collections} |");
        }
        text.AppendLine();
        text.AppendLine("## Boundaries");
        text.AppendLine();
        foreach (string limitation in report.Limitations)
            text.AppendLine(CultureInfo.InvariantCulture, $"- {limitation}");
        return text.ToString();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // 临时证据库清理不能覆盖查询合同失败。
        }
        catch (UnauthorizedAccessException)
        {
            // Windows 文件句柄短暂存活时交给系统临时目录后续清理。
        }
    }

    private sealed record M41Workload(
        string Name,
        string Sql,
        Action<SelectExecutionResult> Validate);

    private sealed record M41ExecutionSample(
        int ReturnedRows,
        SqlExecutionMetricsSnapshot Metrics);
}

/// <summary>M41 #368 性能合同与可观测性基线报告。</summary>
/// <param name="Schema">报告 schema。</param>
/// <param name="Issue">ROADMAP PR 编号。</param>
/// <param name="CommitSha">运行对应的提交。</param>
/// <param name="StartedUtc">开始时间。</param>
/// <param name="FinishedUtc">结束时间。</param>
/// <param name="Mode">运行规模模式。</param>
/// <param name="Dataset">固定合成数据集说明。</param>
/// <param name="Environment">本地运行环境。</param>
/// <param name="Queries">五类固定查询证据。</param>
/// <param name="LocalCorrectness">本地结果合同状态。</param>
/// <param name="FixedHardware">固定硬件运行状态。</param>
/// <param name="ProductionGate">生产发布门禁状态。</param>
/// <param name="SqlPermitQueueEvidence">Server SQL permit 队列证据状态。</param>
/// <param name="Limitations">不得从本报告外推的边界。</param>
public sealed record M41PerformanceBaselineReport(
    string Schema,
    string Issue,
    string CommitSha,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    string Mode,
    M41DatasetEvidence Dataset,
    M41EnvironmentEvidence Environment,
    IReadOnlyList<M41QueryEvidence> Queries,
    string LocalCorrectness,
    string FixedHardware,
    string ProductionGate,
    string SqlPermitQueueEvidence,
    IReadOnlyList<string> Limitations);

/// <summary>M41 固定合成数据集合同。</summary>
/// <param name="Name">数据集名称与版本。</param>
/// <param name="TaskRows">任务表行数。</param>
/// <param name="AuditRows">审计表行数。</param>
/// <param name="DeviceRows">设备表行数。</param>
/// <param name="PageSize">固定分页大小。</param>
/// <param name="Seed">确定性数据生成种子标识。</param>
public sealed record M41DatasetEvidence(
    string Name,
    int TaskRows,
    int AuditRows,
    int DeviceRows,
    int PageSize,
    int Seed);

/// <summary>M41 本地证据运行环境。</summary>
/// <param name="Framework">.NET 运行时。</param>
/// <param name="Os">操作系统。</param>
/// <param name="Architecture">进程架构。</param>
/// <param name="ProcessorCount">逻辑处理器数。</param>
/// <param name="AvailableMemoryBytes">GC 可用内存估计。</param>
/// <param name="DiskFormat">临时证据盘文件系统。</param>
/// <param name="DiskAvailableBytes">运行时磁盘可用字节数。</param>
public sealed record M41EnvironmentEvidence(
    string Framework,
    string Os,
    string Architecture,
    int ProcessorCount,
    long AvailableMemoryBytes,
    string DiskFormat,
    long DiskAvailableBytes);

/// <summary>M41 单一固定查询形状的聚合执行证据。</summary>
/// <param name="Name">稳定工作负载名称。</param>
/// <param name="NormalizedSql">移除参数值后的规范化 SQL。</param>
/// <param name="Fingerprint">规范化 SQL 指纹。</param>
/// <param name="Correctness">结果合同状态。</param>
/// <param name="AccessPath">实际访问路径。</param>
/// <param name="IndexName">实际索引名。</param>
/// <param name="FallbackReason">稳定回退原因。</param>
/// <param name="Iterations">正式样本数。</param>
/// <param name="ReturnedRows">每次执行返回行数。</param>
/// <param name="CandidateRows">每次执行候选行数。</param>
/// <param name="ExaminedRows">每次执行检查行数。</param>
/// <param name="ExaminedToReturnedRatio">检查/返回放大比。</param>
/// <param name="LogicalReads">每次执行逻辑读取数。</param>
/// <param name="ExecutionP50Ms">Core 执行耗时 P50。</param>
/// <param name="ExecutionP95Ms">Core 执行耗时 P95。</param>
/// <param name="ExecutionP99Ms">Core 执行耗时 P99。</param>
/// <param name="AllocatedBytesP50">同步执行线程分配字节 P50。</param>
/// <param name="AllocatedBytesP95">同步执行线程分配字节 P95。</param>
/// <param name="AllocatedBytesP99">同步执行线程分配字节 P99。</param>
/// <param name="QueueWaitP95Ms">SQL permit 队列等待 P95；嵌入式为 0。</param>
/// <param name="TableLockWaitP95Ms">关系表锁等待 P95。</param>
/// <param name="KvLockWaitP95Ms">KV 锁等待 P95。</param>
/// <param name="WalFsyncP95Ms">WAL fsync 等待 P95。</param>
/// <param name="PhysicalReadsTotal">正式样本物理读取总数。</param>
/// <param name="PhysicalReadBytesTotal">正式样本物理读取字节总数。</param>
/// <param name="PhysicalWritesTotal">正式样本物理写入总数。</param>
/// <param name="PhysicalWriteBytesTotal">正式样本物理写入字节总数。</param>
/// <param name="Gen0Collections">正式样本 Gen0 GC 总数。</param>
/// <param name="Gen1Collections">正式样本 Gen1 GC 总数。</param>
/// <param name="Gen2Collections">正式样本 Gen2 GC 总数。</param>
public sealed record M41QueryEvidence(
    string Name,
    string NormalizedSql,
    string Fingerprint,
    string Correctness,
    string? AccessPath,
    string? IndexName,
    string? FallbackReason,
    int Iterations,
    int ReturnedRows,
    long CandidateRows,
    long ExaminedRows,
    double ExaminedToReturnedRatio,
    long LogicalReads,
    double ExecutionP50Ms,
    double ExecutionP95Ms,
    double ExecutionP99Ms,
    long AllocatedBytesP50,
    long AllocatedBytesP95,
    long AllocatedBytesP99,
    double QueueWaitP95Ms,
    double TableLockWaitP95Ms,
    double KvLockWaitP95Ms,
    double WalFsyncP95Ms,
    long PhysicalReadsTotal,
    long PhysicalReadBytesTotal,
    long PhysicalWritesTotal,
    long PhysicalWriteBytesTotal,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(M41PerformanceBaselineReport))]
internal sealed partial class M41PerformanceBaselineJsonContext : JsonSerializerContext;
