using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// M41 #369 EXISTS 访问路径基准：比较唯一索引命中、唯一索引未命中与明确的全表扫描回退。
/// 每次测量同时核对结果、访问路径和候选检查数量，避免错误计划产生无效性能数据。
/// </summary>
[Config(typeof(M41ExistsAccessPathBenchmarkConfig))]
[BenchmarkCategory("M41", "Exists")]
public class M41ExistsAccessPathBenchmark
{
    private const int RowCount = 30_000;
    private const int BatchSize = 500;
    private const string TableName = "m41_exists_audits";
    private const string IndexName = "ux_m41_exists_audits_key";

    private string _root = string.Empty;
    private Tsdb? _db;
    private SelectStatement? _indexedHitStatement;
    private SelectStatement? _indexedMissStatement;
    private SelectStatement? _fullScanFallbackStatement;

    /// <summary>创建 30k 行审计表、唯一幂等键索引和三条固定 EXISTS 查询，并预验全部执行合同。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sonnetdb-m41-exists-bench-{Guid.NewGuid():N}");
        _db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });

        SqlExecutor.Execute(_db, $"""
            CREATE TABLE {TableName} (
                id INT,
                idempotency_key STRING,
                status STRING,
                occurred_at INT,
                PRIMARY KEY (id)
            )
            """);
        SqlExecutor.Execute(
            _db,
            $"CREATE UNIQUE INDEX {IndexName} ON {TableName} (idempotency_key)");
        InsertRows();

        // 命中查询保留一个未被索引覆盖的状态残余条件，验证候选缩小后仍执行完整谓词。
        _indexedHitStatement = ParseSelect($"""
            SELECT EXISTS (
                SELECT 1 FROM {TableName} a
                WHERE a.idempotency_key = 'key-{RowCount:D8}' AND a.status = 'ready'
            )
            """);
        _indexedMissStatement = ParseSelect($"""
            SELECT EXISTS (
                SELECT 1 FROM {TableName} a
                WHERE a.idempotency_key = 'key-missing'
            )
            """);
        _fullScanFallbackStatement = ParseSelect($"""
            SELECT EXISTS (
                SELECT 1 FROM {TableName} a
                WHERE a.status = 'missing'
            )
            """);

        _ = IndexedHit_UniqueKeyWithResidual();
        _ = IndexedMiss_UniqueKey();
        _ = FullScanFallback_UnindexedPredicate();
    }

    /// <summary>测量唯一索引命中后复检单行残余谓词并立即停止的路径。</summary>
    /// <returns>EXISTS 的布尔结果。</returns>
    [Benchmark(Description = "M41 EXISTS: unique index hit + residual")]
    public bool IndexedHit_UniqueKeyWithResidual()
        => ExecuteAndValidate(
            _indexedHitStatement!,
            expectedResult: true,
            expectedAccessPath: "secondary_index",
            expectedExaminedRows: 1,
            expectedFullScans: 0,
            expectedEarlyExits: 1,
            expectedFallbackReason: null);

    /// <summary>测量唯一索引未命中且不读取任何候选行的路径。</summary>
    /// <returns>EXISTS 的布尔结果。</returns>
    [Benchmark(Description = "M41 EXISTS: unique index miss")]
    public bool IndexedMiss_UniqueKey()
        => ExecuteAndValidate(
            _indexedMissStatement!,
            expectedResult: false,
            expectedAccessPath: "secondary_index",
            expectedExaminedRows: 0,
            expectedFullScans: 0,
            expectedEarlyExits: 0,
            expectedFallbackReason: null);

    /// <summary>测量无可用索引谓词时扫描全部 30k 行并确认未命中的回退路径。</summary>
    /// <returns>EXISTS 的布尔结果。</returns>
    [Benchmark(Baseline = true, Description = "M41 EXISTS: explicit table scan fallback")]
    public bool FullScanFallback_UnindexedPredicate()
        => ExecuteAndValidate(
            _fullScanFallbackStatement!,
            expectedResult: false,
            expectedAccessPath: "table_scan",
            expectedExaminedRows: RowCount,
            expectedFullScans: 1,
            expectedEarlyExits: 0,
            expectedFallbackReason: "no_sargable_predicate");

    /// <summary>释放数据库并删除本次基准使用的临时目录。</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        _db = null;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>执行单条 EXISTS，并用本次调用的计数增量验证实际访问路径。</summary>
    private bool ExecuteAndValidate(
        SelectStatement statement,
        bool expectedResult,
        string expectedAccessPath,
        long expectedExaminedRows,
        long expectedFullScans,
        int expectedEarlyExits,
        string? expectedFallbackReason)
    {
        var store = _db!.Tables.Open(TableName);
        long scansBefore = store.FullScanCount;
        var metrics = new RelationalSelectExecutionMetrics();

        var result = RelationalSelectExecutor.Execute(_db, statement, metrics);
        bool actualResult = ReadExists(result);
        long actualFullScans = store.FullScanCount - scansBefore;

        if (actualResult != expectedResult
            || actualFullScans != expectedFullScans
            || metrics.SubqueryExecutionCount != 1
            || metrics.ExistsFastPathExecutionCount != 1
            || metrics.ExistsRowsExamined != expectedExaminedRows
            || metrics.ExistsEarlyExitCount != expectedEarlyExits
            || metrics.ExistsFallbackExecutionCount != 0
            || !string.Equals(metrics.LastExistsAccessPath, expectedAccessPath, StringComparison.Ordinal)
            || !string.Equals(metrics.LastExistsFallbackReason, expectedFallbackReason, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "M41 EXISTS 基准执行合同不一致："
                + $"result={actualResult}, path={metrics.LastExistsAccessPath}, "
                + $"fullScans={actualFullScans}, "
                + $"examined={metrics.ExistsRowsExamined}, earlyExits={metrics.ExistsEarlyExitCount}, "
                + $"fallbacks={metrics.ExistsFallbackExecutionCount}, reason={metrics.LastExistsFallbackReason}。");
        }

        if (string.Equals(expectedAccessPath, "secondary_index", StringComparison.Ordinal)
            && !string.Equals(metrics.LastExistsIndexName, IndexName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"M41 EXISTS 基准命中了错误索引：expected={IndexName}, actual={metrics.LastExistsIndexName}。");
        }

        return actualResult;
    }

    /// <summary>分批插入固定审计数据，控制初始化 SQL 大小并保持 smoke 启动时间可接受。</summary>
    private void InsertRows()
    {
        for (int start = 1; start <= RowCount; start += BatchSize)
        {
            int end = Math.Min(RowCount, start + BatchSize - 1);
            var sql = new StringBuilder($"INSERT INTO {TableName} (id, idempotency_key, status, occurred_at) VALUES ");
            for (int id = start; id <= end; id++)
            {
                if (id != start)
                    sql.Append(',');
                string status = (id & 1) == 0 ? "ready" : "blocked";
                sql.Append('(')
                    .Append(id)
                    .Append(",'key-")
                    .Append(id.ToString("D8", System.Globalization.CultureInfo.InvariantCulture))
                    .Append("','")
                    .Append(status)
                    .Append("',")
                    .Append(id)
                    .Append(')');
            }
            SqlExecutor.Execute(_db!, sql.ToString());
        }
    }

    /// <summary>解析基准 SQL，并确保结果是关系 SELECT AST。</summary>
    private static SelectStatement ParseSelect(string sql)
        => SqlParser.Parse(sql) as SelectStatement
            ?? throw new InvalidOperationException("M41 EXISTS 基准 SQL 未解析为 SELECT。");

    /// <summary>读取独立 SELECT EXISTS 的唯一布尔结果。</summary>
    private static bool ReadExists(SelectExecutionResult result)
    {
        if (result.Rows.Count != 1
            || result.Rows[0].Count != 1
            || result.Rows[0][0] is not bool exists)
        {
            throw new InvalidOperationException("M41 EXISTS 基准查询未返回单行布尔结果。");
        }

        return exists;
    }
}

/// <summary>M41 EXISTS 访问路径基准的固定测量配置。</summary>
internal sealed class M41ExistsAccessPathBenchmarkConfig : ManualConfig
{
    /// <summary>配置预热、正式迭代、尾延迟列和托管内存诊断。</summary>
    public M41ExistsAccessPathBenchmarkConfig()
    {
        AddJob(Job.Default
            .WithWarmupCount(3)
            .WithIterationCount(7));
        AddColumn(StatisticColumn.Median, StatisticColumn.P90);
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}
