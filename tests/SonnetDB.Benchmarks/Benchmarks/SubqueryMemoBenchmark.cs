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
/// P2 子查询路径基准：覆盖 10k 外层行投影与 10k JOIN 候选，
/// 并在每次测量中校验非相关子查询实际执行次数恒为 1。
/// </summary>
[Config(typeof(SubqueryMemoBenchmarkConfig))]
[BenchmarkCategory("P2", "Subquery")]
public class SubqueryMemoBenchmark
{
    private const int OuterRowCount = 10_000;
    private const int JoinSideRowCount = 100;
    private const int ExpectedCacheHits = 9_999;

    private string _root = string.Empty;
    private Tsdb? _db;
    private SelectStatement? _projectionStatement;
    private SelectStatement? _joinStatement;

    /// <summary>建立 10k 外层行、两个 100 行 JOIN 关系和单行子查询表。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sonnetdb-subquery-bench-{Guid.NewGuid():N}");
        _db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });

        SqlExecutor.Execute(_db, "CREATE TABLE bench_outer (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(_db, "CREATE TABLE bench_left (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(_db, "CREATE TABLE bench_right (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(_db, "CREATE TABLE bench_singleton (id INT, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(_db, "INSERT INTO bench_singleton (id, value) VALUES (1, 1)");

        InsertRows("bench_outer", OuterRowCount);
        InsertRows("bench_left", JoinSideRowCount);
        InsertRows("bench_right", JoinSideRowCount);

        _projectionStatement = ParseSelect("""
            SELECT o.id, (SELECT value FROM bench_singleton) AS marker
            FROM bench_outer o
            """);
        _joinStatement = ParseSelect("""
            SELECT l.id, r.id
            FROM bench_left l
            JOIN bench_right r ON (SELECT value FROM bench_singleton) = 1
            """);

        _ = Projection_10kOuterRows();
        _ = Join_10kCandidates();
    }

    /// <summary>测量非相关投影子查询跨 10k 外层行复用结果的路径。</summary>
    /// <returns>投影结果行数。</returns>
    [Benchmark(Description = "P2 subquery memo: 10k outer rows")]
    public int Projection_10kOuterRows()
        => ExecuteAndValidate(_projectionStatement!, OuterRowCount);

    /// <summary>测量非相关 JOIN ON 子查询跨 100×100 候选复用结果的路径。</summary>
    /// <returns>JOIN 结果行数。</returns>
    [Benchmark(Description = "P2 subquery memo: 10k JOIN candidates")]
    public int Join_10kCandidates()
        => ExecuteAndValidate(_joinStatement!, JoinSideRowCount * JoinSideRowCount);

    /// <summary>释放数据库并删除基准目录。</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        _db = null;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private int ExecuteAndValidate(SelectStatement statement, int expectedRows)
    {
        var metrics = new RelationalSelectExecutionMetrics();
        var result = RelationalSelectExecutor.Execute(_db!, statement, metrics);
        if (result.Rows.Count != expectedRows)
        {
            throw new InvalidOperationException(
                $"子查询基准结果行数不一致：expected={expectedRows}, actual={result.Rows.Count}。");
        }
        if (metrics.SubqueryExecutionCount != 1 || metrics.SubqueryCacheHitCount != ExpectedCacheHits)
        {
            throw new InvalidOperationException(
                $"非相关子查询记忆化失效：executions={metrics.SubqueryExecutionCount}, "
                + $"cacheHits={metrics.SubqueryCacheHitCount}。");
        }
        return result.Rows.Count;
    }

    private void InsertRows(string table, int count)
    {
        const int BatchSize = 500;
        for (int start = 1; start <= count; start += BatchSize)
        {
            int end = Math.Min(count, start + BatchSize - 1);
            var sql = new StringBuilder($"INSERT INTO {table} (id) VALUES ");
            for (int id = start; id <= end; id++)
            {
                if (id != start)
                    sql.Append(',');
                sql.Append('(').Append(id).Append(')');
            }
            SqlExecutor.Execute(_db!, sql.ToString());
        }
    }

    private static SelectStatement ParseSelect(string sql)
        => SqlParser.Parse(sql) as SelectStatement
            ?? throw new InvalidOperationException("子查询基准 SQL 未解析为 SELECT。");
}

internal sealed class SubqueryMemoBenchmarkConfig : ManualConfig
{
    public SubqueryMemoBenchmarkConfig()
    {
        AddJob(Job.Default
            .WithWarmupCount(3)
            .WithIterationCount(7));
        AddColumn(StatisticColumn.Median, StatisticColumn.P90);
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}
