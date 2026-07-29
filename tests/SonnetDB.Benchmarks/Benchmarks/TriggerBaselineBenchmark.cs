using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Exceptions;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// M39 #333 触发器基线。候选 statement 路径是客户端批量参考实现，不代表产品语义。
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("M39", "SqlTrigger")]
[Config(typeof(TriggerBaselineConfig))]
public class TriggerBaselineBenchmark
{
    private string _rootDirectory = string.Empty;
    private Tsdb? _database;
    private string _insertSql = string.Empty;
    private string _candidateSql = string.Empty;
    private SqlExecutionOptions _executionOptions = SqlExecutionOptions.Default;
    private long _lastWalBytes;
    private long _lastRowStoreBytes;
    private long _lastWorkingSetBytes;
    private long _lastManagedBytes;
    private long _lastAllocatedBytes;
    private long _baselineActiveWalBytes;
    private long _baselinePersistedRowStoreBytes;

    /// <summary>本次基线中的关系表 DML 行数。</summary>
    [Params(1, 100, 10_000)]
    public int Rows { get; set; }

    /// <summary>基线路径；由 BenchmarkDotNet 为每个参数值建立独立样本。</summary>
    [Params(TriggerPath.NoTrigger, TriggerPath.V1RowTrigger, TriggerPath.CandidateStatementReference)]
    public TriggerPath Path { get; set; }

    /// <summary>在一次测量迭代前建立干净数据库和对应 schema。</summary>
    [IterationSetup]
    public void IterationSetup()
    {
        _rootDirectory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "sndb-m39-trigger-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDirectory);

        _database = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _rootDirectory,
            SyncWalOnEveryWrite = false,
            FlushWalToOsOnWrite = true,
            Kv = KvOptions.Default with
            {
                SyncWalOnEveryWrite = false,
                AutoCheckpointEnabled = false,
                MaxWalBytes = 0,
                MaxOverlayEntries = 0,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            },
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        });

        SqlExecutor.Execute(_database, "CREATE TABLE trigger_source (id INT, payload INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(_database, "CREATE TABLE trigger_audit (id INT, row_count INT, PRIMARY KEY (id))");
        // Open both keyspaces before the baseline snapshot. Otherwise the first
        // trigger/candidate write would include lazy TableStore initialization.
        _ = _database.Tables.Open("trigger_source");
        _ = _database.Tables.Open("trigger_audit");

        if (Path == TriggerPath.V1RowTrigger)
        {
            SqlExecutor.Execute(_database, """
                CREATE TRIGGER trigger_source_audit AFTER INSERT ON trigger_source FOR EACH ROW
                LANGUAGE SQL AS BEGIN
                    INSERT INTO trigger_audit (id, row_count) VALUES (NEW.id, 1);
                END
                """);
        }

        _insertSql = BuildInsertSql(Rows);
        _candidateSql = $"BEGIN; {_insertSql} INSERT INTO trigger_audit (id, row_count) VALUES (1, {Rows}); COMMIT;";
        // V1 consumes one routine statement for each affected row. Keep the guard
        // enabled, but raise it above the measured batch so the benchmark observes
        // row-trigger cost instead of the safety limit.
        _executionOptions = new SqlExecutionOptions
        {
            Caller = "m39-trigger-baseline",
            MaxRoutineStatements = checked(Rows + 64),
            MaxRoutineDepth = 16,
            MaxRoutineResultRows = checked(Rows + 64),
        };
        _baselineActiveWalBytes = _database.Tables.ActiveWalBytesForEvidence;
        _baselinePersistedRowStoreBytes = SumPersistedRowStoreFiles(_rootDirectory);
        ResetMetrics();
    }

    /// <summary>释放本次迭代的数据库目录。</summary>
    [IterationCleanup]
    public void IterationCleanup()
    {
        _database?.Dispose();
        _database = null;
        try
        {
            if (Directory.Exists(_rootDirectory))
                Directory.Delete(_rootDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Benchmark cleanup must not hide the measured result on Windows.
        }
        catch (UnauthorizedAccessException)
        {
            // A background file handle can outlive disposal by a short interval.
        }
    }

    /// <summary>
    /// 执行一次批量插入。<see cref="TriggerPath.CandidateStatementReference"/> 使用显式事务中的
    /// 单条汇总写入，作为 transition-table/statement trigger 的成本参考，不是已实现功能。
    /// </summary>
    [Benchmark(Baseline = true, Description = "M39 DML baseline / selected path")]
    public int ExecuteDml()
    {
        Tsdb database = _database ?? throw new InvalidOperationException("基准数据库尚未初始化。");
        ResetMetrics();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        string sql = Path == TriggerPath.CandidateStatementReference ? _candidateSql : _insertSql;
        IReadOnlyList<object?> results = SqlExecutor.ExecuteScript(database, sql, _executionOptions);
        int inserted = results
            .OfType<InsertExecutionResult>()
            .Select(static result => result.RowsInserted)
            .FirstOrDefault();

        // Keep the benchmark body limited to the DML itself. File enumeration,
        // working-set sampling, and heap inspection are evidence collection and
        // must not become part of the measured latency.
        _lastAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        return inserted;
    }

    /// <summary>最近一次 smoke DML 产生的 active WAL 逻辑字节数。</summary>
    public long LastWalBytes => _lastWalBytes;

    /// <summary>最近一次 smoke 测量中观测到的关系表 rowstore 文件字节数。</summary>
    public long LastRowStoreBytes => _lastRowStoreBytes;

    /// <summary>最近一次 smoke 测量结束时的进程工作集字节数。</summary>
    public long LastWorkingSetBytes => _lastWorkingSetBytes;

    /// <summary>最近一次 smoke 测量结束时的托管堆字节数。</summary>
    public long LastManagedBytes => _lastManagedBytes;

    /// <summary>最近一次 smoke 测量线程观察到的托管分配字节数。</summary>
    public long LastAllocatedBytes => _lastAllocatedBytes;

    /// <summary>
    /// 供短 smoke 使用的单次运行入口。长时间统计应使用 BenchmarkDotNet。
    /// </summary>
    public TriggerBaselineSample RunSingleIteration()
    {
        IterationSetup();
        try
        {
            var stopwatch = Stopwatch.StartNew();
            int inserted = ExecuteDml();
            stopwatch.Stop();
            CaptureMetrics(LastAllocatedBytes);
            // Persisted storage is sampled after the timed DML so checkpoint I/O
            // does not contaminate the throughput measurement.
            CapturePersistedRowStoreBytes();
            return new TriggerBaselineSample(
                Rows,
                Path,
                inserted,
                stopwatch.Elapsed.TotalMilliseconds,
                LastWalBytes,
                LastRowStoreBytes,
                LastWorkingSetBytes,
                LastManagedBytes,
                LastAllocatedBytes);
        }
        finally
        {
            IterationCleanup();
        }
    }

    private void ResetMetrics()
    {
        _lastWalBytes = 0;
        _lastRowStoreBytes = 0;
        _lastWorkingSetBytes = 0;
        _lastManagedBytes = 0;
        _lastAllocatedBytes = 0;
    }

    private void CaptureMetrics(long allocatedBytes)
    {
        Tsdb database = _database ?? throw new InvalidOperationException("基准数据库尚未初始化。");
        long activeWalBytes = database.Tables.ActiveWalBytesForEvidence;
        _lastWalBytes = Math.Max(0, activeWalBytes - _baselineActiveWalBytes);
        _lastRowStoreBytes = Math.Max(
            0,
            SumPersistedRowStoreFiles(_rootDirectory) - _baselinePersistedRowStoreBytes);
        _lastWorkingSetBytes = Environment.WorkingSet;
        _lastManagedBytes = GC.GetTotalMemory(forceFullCollection: false);
        _lastAllocatedBytes = allocatedBytes;
    }

    private void CapturePersistedRowStoreBytes()
    {
        Tsdb database = _database ?? throw new InvalidOperationException("基准数据库尚未初始化。");
        database.Tables.CheckpointAll();
        _lastRowStoreBytes = Math.Max(
            0,
            SumPersistedRowStoreFiles(_rootDirectory) - _baselinePersistedRowStoreBytes);
    }

    private static long SumPersistedRowStoreFiles(string rootDirectory)
    {
        string rowStoreDirectory = System.IO.Path.Combine(rootDirectory, "tables", "rowstore");
        return checked(
            SumFiles(rowStoreDirectory, "*.SDBKVSNP")
            + SumFiles(rowStoreDirectory, "*.SDBKVSEG"));
    }

    private static long SumFiles(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
            return 0;

        long total = 0;
        foreach (string file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
        {
            try
            {
                total = checked(total + new FileInfo(file).Length);
            }
            catch (FileNotFoundException)
            {
                // A checkpoint/cleanup may remove a file while it is sampled.
            }
        }

        return total;
    }

    private static string BuildInsertSql(int rows)
    {
        var sql = new System.Text.StringBuilder(rows * 24 + 64);
        sql.Append("INSERT INTO trigger_source (id, payload) VALUES ");
        for (int id = 1; id <= rows; id++)
        {
            if (id > 1)
                sql.Append(", ");
            sql.Append('(').Append(id).Append(", ").Append(id % 97).Append(')');
        }

        sql.Append(';');
        return sql.ToString();
    }

    private sealed class TriggerBaselineConfig : ManualConfig
    {
        public TriggerBaselineConfig()
        {
            AddJob(Job.Default
                .WithStrategy(BenchmarkDotNet.Engines.RunStrategy.Monitoring)
                .WithWarmupCount(0)
                .WithIterationCount(3)
                .WithInvocationCount(1)
                .WithUnrollFactor(1));
        }
    }
}

/// <summary>触发器基线中的执行路径。</summary>
public enum TriggerPath
{
    /// <summary>仅执行源表 DML。</summary>
    NoTrigger,

    /// <summary>执行当前 V1 的 FOR EACH ROW 触发器。</summary>
    V1RowTrigger,

    /// <summary>客户端显式事务 + 单条汇总写入的候选 statement 参考。</summary>
    CandidateStatementReference,
}

/// <summary>一次 M39 smoke 样本的文件和内存观测值。</summary>
public sealed record TriggerBaselineSample(
    int Rows,
    TriggerPath Path,
    int RowsInserted,
    double ElapsedMilliseconds,
    long WalBytes,
    long RowStoreBytes,
    long WorkingSetBytes,
    long ManagedBytes,
    long AllocatedBytes);

/// <summary>
/// #333 回滚成本 smoke：在各成本路径上注入一个可重复的失败，验证整批源表和
/// 触发动作均回滚，并记录失败路径的耗时、WAL 和分配。
/// </summary>
public static class TriggerRollbackEvidence
{
    private const string ConstraintViolationFailureCode = "constraint_violation";

    /// <summary>
    /// 执行一次指定行数的失败 DML，并返回回滚后的计量结果。
    /// </summary>
    /// <param name="rows">源表批量行数。</param>
    /// <returns>失败码、回滚行数和资源观测值。</returns>
    public static TriggerRollbackSample RunSingleIteration(int rows)
        => RunSingleIteration(rows, TriggerPath.V1RowTrigger);

    /// <summary>
    /// 在指定成本路径上执行一次失败 DML，并返回回滚后的计量结果。
    /// </summary>
    /// <param name="rows">源表批量行数。</param>
    /// <param name="path">无触发器、V1 行触发器或候选 statement 参考路径。</param>
    /// <returns>失败码、回滚行数和资源观测值。</returns>
    public static TriggerRollbackSample RunSingleIteration(int rows, TriggerPath path)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "sndb-m39-trigger-rollback-" + Guid.NewGuid().ToString("N"));
        Tsdb? database = null;
        try
        {
            database = Tsdb.Open(new TsdbOptions
            {
                RootDirectory = root,
                SyncWalOnEveryWrite = false,
                FlushWalToOsOnWrite = true,
                Kv = KvOptions.Default with
                {
                    SyncWalOnEveryWrite = false,
                    AutoCheckpointEnabled = false,
                    MaxWalBytes = 0,
                    MaxOverlayEntries = 0,
                    ExpirerEnabled = false,
                    CleanupEnabled = false,
                },
                BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
                Compaction = new CompactionPolicy { Enabled = false },
            });
            SqlExecutor.Execute(database,
                "CREATE TABLE rollback_source (id INT, payload INT, PRIMARY KEY (id))");
            SqlExecutor.Execute(database,
                "CREATE TABLE rollback_audit (id INT, row_count INT, PRIMARY KEY (id))");
            // Keep table-store initialization out of the failure-path comparison.
            _ = database.Tables.Open("rollback_source");
            _ = database.Tables.Open("rollback_audit");
            if (path is TriggerPath.V1RowTrigger or TriggerPath.CandidateStatementReference)
            {
                // The pre-existing final key makes only the last action fail.
                SqlExecutor.Execute(database,
                    $"INSERT INTO rollback_audit (id, row_count) VALUES ({rows}, 0)");
            }

            if (path == TriggerPath.V1RowTrigger)
            {
                SqlExecutor.Execute(database, """
                    CREATE TRIGGER rollback_source_audit
                    AFTER INSERT ON rollback_source FOR EACH ROW
                    LANGUAGE SQL AS BEGIN
                        INSERT INTO rollback_audit (id, row_count) VALUES (NEW.id, 1);
                    END
                    """);
            }

            var options = new SqlExecutionOptions
            {
                Caller = "m39-trigger-rollback",
                MaxRoutineStatements = checked(rows + 64),
                MaxRoutineDepth = 16,
                MaxRoutineResultRows = checked(rows + 64),
            };
            string sourceSql = BuildInsertSql(rows);
            string failureSql = path switch
            {
                TriggerPath.NoTrigger => BuildDuplicateSourceScript(sourceSql, rows),
                TriggerPath.V1RowTrigger => sourceSql,
                TriggerPath.CandidateStatementReference =>
                    $"BEGIN; {sourceSql} INSERT INTO rollback_audit (id, row_count) VALUES ({rows}, {rows}); COMMIT;",
                _ => throw new ArgumentOutOfRangeException(nameof(path)),
            };
            long baselineWalBytes = database.Tables.ActiveWalBytesForEvidence;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var stopwatch = Stopwatch.StartNew();
            string failureCode;
            bool failedAsExpected = false;
            try
            {
                _ = SqlExecutor.ExecuteScript(database, failureSql, options);
                failureCode = "unexpected_success";
            }
            catch (RoutineExecutionException exception)
                when (exception.Code == RoutineErrorCodes.ExecutionFailed)
            {
                failedAsExpected = true;
                failureCode = exception.Code;
            }
            catch (TableConstraintException exception)
                when (exception.ErrorCode == TableConstraintException.UniqueViolation
                    && (exception.TableName == "rollback_source" || exception.TableName == "rollback_audit"))
            {
                failedAsExpected = true;
                failureCode = exception.ErrorCode;
            }
            catch (InvalidOperationException exception) when (IsExpectedDuplicateFailure(exception))
            {
                failedAsExpected = true;
                failureCode = ConstraintViolationFailureCode;
            }
            if (!failedAsExpected)
                throw new InvalidDataException("回滚基线预期失败，但 DML 成功提交。");
            stopwatch.Stop();

            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            int sourceRows = Select(database, "SELECT * FROM rollback_source").Rows.Count;
            int auditRows = Select(database, "SELECT * FROM rollback_audit").Rows.Count;

            long walBytes = Math.Max(
                0,
                database.Tables.ActiveWalBytesForEvidence - baselineWalBytes);
            database.Dispose();
            database = null;
            return new TriggerRollbackSample(
                rows,
                path,
                failedAsExpected,
                stopwatch.Elapsed.TotalMilliseconds,
                walBytes,
                sourceRows,
                auditRows,
                allocatedBytes,
                failureCode);
        }
        finally
        {
            database?.Dispose();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static SelectExecutionResult Select(Tsdb database, string sql)
        => SqlExecutor.Execute(database, sql) as SelectExecutionResult
            ?? throw new InvalidOperationException("回滚基线查询未返回 SELECT 结果。");

    private static string BuildInsertSql(int rows)
    {
        var sql = new System.Text.StringBuilder(rows * 26 + 96);
        sql.Append("INSERT INTO rollback_source (id, payload) VALUES ");
        for (int id = 1; id <= rows; id++)
        {
            if (id > 1)
                sql.Append(", ");
            sql.Append('(').Append(id).Append(", ").Append(id % 97).Append(')');
        }

        sql.Append(';');
        return sql.ToString();
    }

    private static string BuildDuplicateSourceScript(string sourceSql, int rows)
    {
        int marker = sourceSql.IndexOf(';');
        if (marker < 0)
            throw new InvalidDataException("回滚基准 INSERT 缺少结束分号。");
        string duplicate = rows == 1
            ? "(1, 100001), (1, 100002)"
            : $"({rows}, 100001), ({rows}, 100002)";
        return sourceSql[..marker] + ", " + duplicate + ";";
    }

    private static bool IsExpectedDuplicateFailure(InvalidOperationException exception)
    {
        string message = exception.Message;
        return message.Contains("rollback_source", StringComparison.Ordinal)
            || message.Contains("rollback_audit", StringComparison.Ordinal)
            || message.Contains("主键", StringComparison.Ordinal)
            || message.Contains("unique", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
    }

}

/// <summary>一次失败触发器 DML 的回滚计量结果。</summary>
public sealed record TriggerRollbackSample(
    int Rows,
    TriggerPath Path,
    bool FailedAsExpected,
    double ElapsedMilliseconds,
    long WalBytes,
    int SourceRowsAfterRollback,
    int AuditRowsAfterRollback,
    long AllocatedBytes,
    string FailureCode);
