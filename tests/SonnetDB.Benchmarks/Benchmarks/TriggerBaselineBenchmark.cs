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
    private string _dmlSql = string.Empty;
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

    /// <summary>本次基线执行的关系表 DML 类型。</summary>
    [Params(TriggerDmlOperation.Insert, TriggerDmlOperation.Update, TriggerDmlOperation.Delete)]
    public TriggerDmlOperation Operation { get; set; }

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
        // 在基线快照前打开两个 keyspace，避免首次触发器或候选写入混入 TableStore 延迟初始化成本。
        _ = _database.Tables.Open("trigger_source");
        _ = _database.Tables.Open("trigger_audit");

        // UPDATE/DELETE 必须在创建触发器前预置同规模源数据，避免 setup 产生触发动作。
        if (Operation is not TriggerDmlOperation.Insert)
            SqlExecutor.Execute(_database, BuildInsertSql(Rows));

        if (Path == TriggerPath.V1RowTrigger)
            SqlExecutor.Execute(_database, BuildTriggerSql(Operation));

        _dmlSql = BuildDmlSql(Operation, Rows);
        _candidateSql = BuildCandidateSql(_dmlSql, Rows);
        // V1 每个受影响行消耗一条 routine 语句；保留安全阈值，但提高到被测批次之上，
        // 确保基准观测行触发器成本而非提前撞到安全上限。
        _executionOptions = new SqlExecutionOptions
        {
            Caller = "m39-trigger-baseline",
            MaxRoutineStatements = checked(Rows + 64),
            MaxRoutineDepth = 16,
            MaxRoutineResultRows = checked(Rows + 64),
        };
        // 先固化 setup 代际，再取文件基线；否则 UPDATE/DELETE 的结果会混入预置数据成本。
        _database.Tables.CheckpointAll();
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
            // Windows 清理失败不能覆盖已经完成的基准结果。
        }
        catch (UnauthorizedAccessException)
        {
            // 后台文件句柄可能在释放后短暂存活，临时目录可由系统后续清理。
        }
    }

    /// <summary>
    /// 执行一次批量 DML。<see cref="TriggerPath.CandidateStatementReference"/> 使用显式事务中的
    /// 单条汇总写入，作为 transition-table/statement trigger 的成本参考，不是已实现功能。
    /// </summary>
    [Benchmark(Baseline = true, Description = "M39 DML baseline / selected path")]
    public int ExecuteDml()
    {
        Tsdb database = _database ?? throw new InvalidOperationException("基准数据库尚未初始化。");
        ResetMetrics();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        string sql = Path == TriggerPath.CandidateStatementReference ? _candidateSql : _dmlSql;
        IReadOnlyList<object?> results = SqlExecutor.ExecuteScript(database, sql, _executionOptions);
        int affected = ExtractRowsAffected(results, Operation);

        // 计时主体只包含 DML；文件枚举、工作集和托管堆采样属于证据收集，不应计入延迟。
        _lastAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        return affected;
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
            int affected = ExecuteDml();
            stopwatch.Stop();
            if (affected != Rows)
            {
                throw new InvalidDataException(
                    $"M39 成功样本受影响行数不一致：operation={Operation}, path={Path}, "
                    + $"expected={Rows}, actual={affected}。");
            }
            CaptureMetrics(LastAllocatedBytes);
            ValidateCommittedState(_database!, Rows, Operation, Path);
            // 在 DML 停止计时后采样持久化文件，避免 checkpoint I/O 污染吞吐观测。
            CapturePersistedRowStoreBytes();
            return new TriggerBaselineSample(
                Rows,
                Path,
                affected,
                stopwatch.Elapsed.TotalMilliseconds,
                LastWalBytes,
                LastRowStoreBytes,
                LastWorkingSetBytes,
                LastManagedBytes,
                LastAllocatedBytes)
            {
                Operation = Operation,
                JournalBytes = File.Exists(System.IO.Path.Combine(_rootDirectory, "tables", "transaction.sdbtxn"))
                    ? new FileInfo(System.IO.Path.Combine(_rootDirectory, "tables", "transaction.sdbtxn")).Length : 0,
            };
        }
        finally
        {
            IterationCleanup();
        }
    }

    /// <summary>清空当前迭代的全部观测指标。</summary>
    private void ResetMetrics()
    {
        _lastWalBytes = 0;
        _lastRowStoreBytes = 0;
        _lastWorkingSetBytes = 0;
        _lastManagedBytes = 0;
        _lastAllocatedBytes = 0;
    }

    /// <summary>采集不需要 checkpoint 的 WAL、进程和托管内存指标。</summary>
    private void CaptureMetrics(long allocatedBytes)
    {
        Tsdb database = _database ?? throw new InvalidOperationException("基准数据库尚未初始化。");
        long activeWalBytes = database.Tables.ActiveWalBytesForEvidence;
        _lastWalBytes = Math.Max(0, activeWalBytes - _baselineActiveWalBytes);
        _lastRowStoreBytes = SumPersistedRowStoreFiles(_rootDirectory) - _baselinePersistedRowStoreBytes;
        _lastWorkingSetBytes = Environment.WorkingSet;
        _lastManagedBytes = GC.GetTotalMemory(forceFullCollection: false);
        _lastAllocatedBytes = allocatedBytes;
    }

    /// <summary>在停止计时后 checkpoint，并采集 rowstore 相对基线的文件增量。</summary>
    private void CapturePersistedRowStoreBytes()
    {
        Tsdb database = _database ?? throw new InvalidOperationException("基准数据库尚未初始化。");
        database.Tables.CheckpointAll();
        _lastRowStoreBytes = SumPersistedRowStoreFiles(_rootDirectory) - _baselinePersistedRowStoreBytes;
    }

    /// <summary>统计数据库目录内已持久化的 rowstore 快照和段文件总字节数。</summary>
    private static long SumPersistedRowStoreFiles(string rootDirectory)
    {
        string rowStoreDirectory = System.IO.Path.Combine(rootDirectory, "tables", "rowstore");
        return checked(
            SumFiles(rowStoreDirectory, "*.SDBKVSNP")
            + SumFiles(rowStoreDirectory, "*.SDBKVSEG"));
    }

    /// <summary>按文件模式递归统计目录中的文件总字节数。</summary>
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
                // checkpoint 或清理可能恰好在采样时移除文件，此次枚举忽略该竞态。
            }
        }

        return total;
    }

    /// <summary>构造指定行数的基线源表批量 INSERT。</summary>
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

    /// <summary>按 operation 构造只影响当前样本行集的源表 DML。</summary>
    private static string BuildDmlSql(TriggerDmlOperation operation, int rows)
        => operation switch
        {
            TriggerDmlOperation.Insert => BuildInsertSql(rows),
            TriggerDmlOperation.Update =>
                "UPDATE trigger_source SET payload = payload + 1000 WHERE id >= 1;",
            TriggerDmlOperation.Delete => "DELETE FROM trigger_source WHERE id >= 1;",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    /// <summary>构造客户端 statement 参考路径；单条汇总写不代表 transition table 已实现。</summary>
    private static string BuildCandidateSql(string dmlSql, int rows)
        => $"BEGIN; {dmlSql} INSERT INTO trigger_audit (id, row_count) VALUES (0, {rows}); COMMIT;";

    /// <summary>按 operation 构造当前 V1 AFTER ROW 触发器。</summary>
    private static string BuildTriggerSql(TriggerDmlOperation operation)
    {
        string eventSql;
        string rowIdSql;
        switch (operation)
        {
            case TriggerDmlOperation.Insert:
                eventSql = "INSERT";
                rowIdSql = "NEW.id";
                break;
            case TriggerDmlOperation.Update:
                eventSql = "UPDATE";
                rowIdSql = "NEW.id";
                break;
            case TriggerDmlOperation.Delete:
                eventSql = "DELETE";
                rowIdSql = "OLD.id";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }

        return $"""
            CREATE TRIGGER trigger_source_audit AFTER {eventSql} ON trigger_source FOR EACH ROW
            LANGUAGE SQL AS BEGIN
                INSERT INTO trigger_audit (id, row_count) VALUES ({rowIdSql}, 1);
            END
            """;
    }

    /// <summary>从脚本结果中读取源 DML 的受影响行数，避免误取候选汇总 INSERT。</summary>
    private static int ExtractRowsAffected(
        IReadOnlyList<object?> results,
        TriggerDmlOperation operation)
        => operation switch
        {
            TriggerDmlOperation.Insert => results
                .OfType<InsertExecutionResult>()
                .Where(static result => string.Equals(
                    result.Measurement,
                    "trigger_source",
                    StringComparison.Ordinal))
                .Select(static result => result.RowsInserted)
                .FirstOrDefault(),
            TriggerDmlOperation.Update => results
                .OfType<RowsAffectedExecutionResult>()
                .Where(static result => string.Equals(result.Target, "trigger_source", StringComparison.Ordinal)
                    && string.Equals(result.Operation, "update", StringComparison.Ordinal))
                .Select(static result => result.RowsAffected)
                .FirstOrDefault(),
            TriggerDmlOperation.Delete => results
                .OfType<DeleteExecutionResult>()
                .Where(static result => string.Equals(
                    result.Measurement,
                    "trigger_source",
                    StringComparison.Ordinal))
                .Select(static result => result.SeriesAffected)
                .FirstOrDefault(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    /// <summary>在计时与内存采样后核对源表和审计表的已提交状态。</summary>
    private static void ValidateCommittedState(
        Tsdb database,
        int rows,
        TriggerDmlOperation operation,
        TriggerPath path)
    {
        var source = SqlExecutor.Execute(
            database,
            "SELECT id, payload FROM trigger_source ORDER BY id") as SelectExecutionResult
            ?? throw new InvalidDataException("M39 成功样本未能读取源表状态。");
        int expectedSourceRows = operation == TriggerDmlOperation.Delete ? 0 : rows;
        if (source.Rows.Count != expectedSourceRows)
        {
            throw new InvalidDataException(
                $"M39 成功样本源表行数不一致：operation={operation}, expected={expectedSourceRows}, "
                + $"actual={source.Rows.Count}。");
        }

        long payloadOffset = operation == TriggerDmlOperation.Update ? 1000 : 0;
        for (int index = 0; index < source.Rows.Count; index++)
        {
            long expectedId = index + 1L;
            long actualId = Convert.ToInt64(
                source.Rows[index][0],
                System.Globalization.CultureInfo.InvariantCulture);
            long actualPayload = Convert.ToInt64(
                source.Rows[index][1],
                System.Globalization.CultureInfo.InvariantCulture);
            if (actualId != expectedId || actualPayload != expectedId % 97 + payloadOffset)
            {
                throw new InvalidDataException(
                    $"M39 成功样本源表内容不一致：operation={operation}, row={index}。");
            }
        }

        var audit = SqlExecutor.Execute(
            database,
            "SELECT id, row_count FROM trigger_audit ORDER BY id") as SelectExecutionResult
            ?? throw new InvalidDataException("M39 成功样本未能读取审计表状态。");
        int expectedAuditRows = path switch
        {
            TriggerPath.NoTrigger => 0,
            TriggerPath.V1RowTrigger => rows,
            TriggerPath.CandidateStatementReference => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };
        if (audit.Rows.Count != expectedAuditRows)
        {
            throw new InvalidDataException(
                $"M39 成功样本审计行数不一致：operation={operation}, path={path}, "
                + $"expected={expectedAuditRows}, actual={audit.Rows.Count}。");
        }

        for (int index = 0; index < audit.Rows.Count; index++)
        {
            long expectedId = path == TriggerPath.CandidateStatementReference ? 0 : index + 1L;
            long expectedCount = path == TriggerPath.CandidateStatementReference ? rows : 1;
            long actualId = Convert.ToInt64(
                audit.Rows[index][0],
                System.Globalization.CultureInfo.InvariantCulture);
            long actualCount = Convert.ToInt64(
                audit.Rows[index][1],
                System.Globalization.CultureInfo.InvariantCulture);
            if (actualId != expectedId || actualCount != expectedCount)
            {
                throw new InvalidDataException(
                    $"M39 成功样本审计内容不一致：operation={operation}, path={path}, row={index}。");
            }
        }
    }

    private sealed class TriggerBaselineConfig : ManualConfig
    {
        /// <summary>配置单次调用、无预热的监测型触发器基准任务。</summary>
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

/// <summary>M39 证据矩阵中的关系表 DML 类型。</summary>
public enum TriggerDmlOperation
{
    /// <summary>批量插入新行。</summary>
    Insert,

    /// <summary>批量更新预置行。</summary>
    Update,

    /// <summary>批量删除预置行。</summary>
    Delete,
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
    long AllocatedBytes)
{
    /// <summary>跨表恢复日志的持久化字节数；独立于各表 WAL 计量。</summary>
    public long JournalBytes { get; init; }
    /// <summary>本样本执行的 DML 类型；旧调用方默认仍表示 INSERT。</summary>
    public TriggerDmlOperation Operation { get; init; } = TriggerDmlOperation.Insert;

    /// <summary>源 DML 的受影响行数；兼容保留主构造器中的 RowsInserted。</summary>
    public int RowsAffected => RowsInserted;

    /// <summary>checkpoint 后关系 rowstore 文件总量相对 setup 基线的有符号差值。</summary>
    public long RowStoreBytesDelta => RowStoreBytes;
}

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
        => RunSingleIteration(rows, TriggerPath.V1RowTrigger, TriggerDmlOperation.Insert);

    /// <summary>
    /// 在指定成本路径上执行一次失败 DML，并返回回滚后的计量结果。
    /// </summary>
    /// <param name="rows">源表批量行数。</param>
    /// <param name="path">无触发器、V1 行触发器或候选 statement 参考路径。</param>
    /// <returns>失败码、回滚行数和资源观测值。</returns>
    public static TriggerRollbackSample RunSingleIteration(int rows, TriggerPath path)
        => RunSingleIteration(rows, path, TriggerDmlOperation.Insert);

    /// <summary>
    /// 在指定 operation 和成本路径上执行一次事务内约束检查失败，并验证源表恢复到 DML 前状态。
    /// </summary>
    /// <param name="rows">源表批量行数。</param>
    /// <param name="path">无触发器、V1 行触发器或候选 statement 参考路径。</param>
    /// <param name="operation">INSERT、UPDATE 或 DELETE。</param>
    /// <returns>失败码、精确状态恢复结果和资源观测值。</returns>
    public static TriggerRollbackSample RunSingleIteration(
        int rows,
        TriggerPath path,
        TriggerDmlOperation operation)
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
            // 提前初始化表存储，避免失败路径比较混入首次打开成本。
            _ = database.Tables.Open("rollback_source");
            _ = database.Tables.Open("rollback_audit");

            // UPDATE/DELETE 的预置数据不应经过本次被测触发器。
            if (operation is not TriggerDmlOperation.Insert)
                SqlExecutor.Execute(database, BuildInsertSql(rows));

            // 三条路径统一在事务内约束检查阶段撞 sentinel，使失败阶段和初始 audit 状态可比。
            SqlExecutor.Execute(database,
                "INSERT INTO rollback_audit (id, row_count) VALUES (-1, 0)");

            if (path == TriggerPath.V1RowTrigger)
                SqlExecutor.Execute(database, BuildTriggerSql(operation));

            database.Tables.CheckpointAll();

            var options = new SqlExecutionOptions
            {
                Caller = "m39-trigger-rollback",
                MaxRoutineStatements = checked(rows + 64),
                MaxRoutineDepth = 16,
                MaxRoutineResultRows = checked(rows + 64),
            };
            string sourceSql = BuildDmlSql(operation, rows);
            string failureSql = BuildFailureSql(sourceSql, rows, path);
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
            SelectExecutionResult sourceResult = Select(
                database,
                "SELECT id, payload FROM rollback_source ORDER BY id");
            int sourceRows = sourceResult.Rows.Count;
            bool sourceStateRestored = SourceStateRestored(sourceResult, rows, operation);
            SelectExecutionResult auditResult = Select(
                database,
                "SELECT id, row_count FROM rollback_audit ORDER BY id");
            int auditRows = auditResult.Rows.Count;
            bool auditStateRestored = AuditStateRestored(auditResult);

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
                failureCode)
            {
                Operation = operation,
                SourceStateRestored = sourceStateRestored,
                AuditStateRestored = auditStateRestored,
            };
        }
        finally
        {
            database?.Dispose();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException)
            {
                // 临时目录清理失败不能覆盖已经完成的回滚证据。
            }
            catch (UnauthorizedAccessException)
            {
                // Windows 文件句柄可能短暂存活，目录可由系统后续清理。
            }
        }
    }

    /// <summary>执行回滚验证查询，并要求返回关系表 SELECT 结果。</summary>
    private static SelectExecutionResult Select(Tsdb database, string sql)
        => SqlExecutor.Execute(database, sql) as SelectExecutionResult
            ?? throw new InvalidOperationException("回滚基线查询未返回 SELECT 结果。");

    /// <summary>构造回滚样本所需的源表预置 INSERT。</summary>
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

    /// <summary>按 operation 构造 rollback_source 的被测 DML。</summary>
    private static string BuildDmlSql(TriggerDmlOperation operation, int rows)
        => operation switch
        {
            TriggerDmlOperation.Insert => BuildInsertSql(rows),
            TriggerDmlOperation.Update =>
                "UPDATE rollback_source SET payload = payload + 1000 WHERE id >= 1;",
            TriggerDmlOperation.Delete => "DELETE FROM rollback_source WHERE id >= 1;",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    /// <summary>构造与 operation 对应的 V1 AFTER ROW 审计触发器。</summary>
    private static string BuildTriggerSql(TriggerDmlOperation operation)
    {
        string eventSql;
        string rowIdSql;
        switch (operation)
        {
            case TriggerDmlOperation.Insert:
                eventSql = "INSERT";
                rowIdSql = "NEW.id";
                break;
            case TriggerDmlOperation.Update:
                eventSql = "UPDATE";
                rowIdSql = "NEW.id";
                break;
            case TriggerDmlOperation.Delete:
                eventSql = "DELETE";
                rowIdSql = "OLD.id";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }

        return $"""
            CREATE TRIGGER rollback_source_audit
            AFTER {eventSql} ON rollback_source FOR EACH ROW
            LANGUAGE SQL AS BEGIN
                INSERT INTO rollback_audit (id, row_count) VALUES ({rowIdSql}, 1);
            END
            """;
    }

    /// <summary>构造统一在 sentinel 唯一约束处失败的显式事务脚本。</summary>
    private static string BuildFailureSql(string sourceSql, int rows, TriggerPath path)
    {
        string candidateSummary = path switch
        {
            TriggerPath.NoTrigger or TriggerPath.V1RowTrigger => string.Empty,
            TriggerPath.CandidateStatementReference =>
                $"INSERT INTO rollback_audit (id, row_count) VALUES (0, {rows});",
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };
        return $"BEGIN; {sourceSql} {candidateSummary} "
            + "INSERT INTO rollback_audit (id, row_count) VALUES (-1, -1); COMMIT;";
    }

    /// <summary>逐行验证失败后源表与 DML 前基线完全一致。</summary>
    private static bool SourceStateRestored(
        SelectExecutionResult result,
        int rows,
        TriggerDmlOperation operation)
    {
        if (operation == TriggerDmlOperation.Insert)
            return result.Rows.Count == 0;
        if (result.Rows.Count != rows)
            return false;

        for (int index = 0; index < result.Rows.Count; index++)
        {
            long expectedId = index + 1L;
            if (Convert.ToInt64(result.Rows[index][0], System.Globalization.CultureInfo.InvariantCulture) != expectedId
                || Convert.ToInt64(result.Rows[index][1], System.Globalization.CultureInfo.InvariantCulture)
                    != expectedId % 97)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>验证失败后审计表只保留 setup sentinel，且 sentinel 内容未被替换。</summary>
    private static bool AuditStateRestored(SelectExecutionResult result)
        => result.Rows.Count == 1
            && Convert.ToInt64(
                result.Rows[0][0],
                System.Globalization.CultureInfo.InvariantCulture) == -1
            && Convert.ToInt64(
                result.Rows[0][1],
                System.Globalization.CultureInfo.InvariantCulture) == 0;

    /// <summary>识别旧路径可能抛出的主键或唯一约束重复异常。</summary>
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
    string FailureCode)
{
    /// <summary>本样本执行的 DML 类型；旧调用方默认仍表示 INSERT。</summary>
    public TriggerDmlOperation Operation { get; init; } = TriggerDmlOperation.Insert;

    /// <summary>失败后源表是否逐行恢复到 DML 前状态。</summary>
    public bool SourceStateRestored { get; init; }

    /// <summary>失败后审计表是否只保留原始 sentinel。</summary>
    public bool AuditStateRestored { get; init; }
}
