using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// M41 #372 关系输入下推对拍：比较单表 WHERE/索引/列裁剪与逻辑等价的 JOIN 后跨输入残余路径。
/// </summary>
[Config(typeof(M41ExistsAccessPathBenchmarkConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[BenchmarkCategory("M41", "P1", "RelationInputPushdown")]
public sealed class M41RelationInputPushdownBenchmark
{
    private const int TaskRows = 30_000;
    private const int DeviceRows = 128;
    private const int BatchSize = 500;

    private string _root = string.Empty;
    private Tsdb? _database;
    private SelectStatement? _residualReference;
    private SelectStatement? _pushed;
    private long _expectedChecksum;

    /// <summary>创建固定关系语料、状态索引，并预验参考与下推路径的结果和执行计数。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sonnetdb-m41-pushdown-bench-{Guid.NewGuid():N}");
        _database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(_database, """
            CREATE TABLE m41_push_devices (
                id INT,
                region STRING,
                payload STRING,
                PRIMARY KEY (id))
            """);
        SqlExecutor.Execute(_database, """
            CREATE TABLE m41_push_tasks (
                id INT,
                device_id INT,
                status STRING,
                payload STRING,
                note STRING,
                PRIMARY KEY (id))
            """);
        SqlExecutor.Execute(
            _database,
            "CREATE INDEX ix_m41_push_tasks_status ON m41_push_tasks (status)");
        InsertDevices();
        InsertTasks();

        _residualReference = ParseSelect("""
            SELECT t.id, d.region
            FROM m41_push_tasks t
            JOIN m41_push_devices d ON t.device_id = d.id
            WHERE (t.status = 'ready' AND d.id = d.id) OR FALSE
            """);
        _pushed = ParseSelect("""
            SELECT t.id, d.region
            FROM m41_push_tasks t
            JOIN m41_push_devices d ON t.device_id = d.id
            WHERE t.status = 'ready'
            """);

        SelectExecutionResult expected = ExecuteWithMetrics(
            _residualReference,
            out RelationalSelectExecutionMetrics referenceMetrics);
        SelectExecutionResult actual = ExecuteWithMetrics(
            _pushed,
            out RelationalSelectExecutionMetrics pushedMetrics);
        RequireEquivalent(expected, actual);
        if (referenceMetrics.InputPredicatePushdownCount != 0
            || pushedMetrics.InputPredicatePushdownCount != 1
            || pushedMetrics.InputProjectionPushdownCount != 2
            || pushedMetrics.InputProjectedColumns >= pushedMetrics.InputSourceColumns)
        {
            throw new InvalidOperationException("M41 #372 基准未命中预期的关系输入下推合同。");
        }
        _expectedChecksum = ComputeChecksum(expected);
    }

    /// <summary>测量跨输入表达式保留到 JOIN 后复检的参考路径。</summary>
    /// <returns>结果校验和。</returns>
    [Benchmark(Baseline = true, Description = "#372 reference: post-JOIN residual")]
    [BenchmarkCategory("PredicateProjection")]
    public long PostJoinResidualReference()
        => ExecuteAndValidate(_residualReference!);

    /// <summary>测量单表谓词、索引候选和所需列在 JOIN 前下推的路径。</summary>
    /// <returns>结果校验和。</returns>
    [Benchmark(Description = "#372 pushed: input predicate + projection")]
    [BenchmarkCategory("PredicateProjection")]
    public long InputPredicateAndProjectionPushdown()
        => ExecuteAndValidate(_pushed!);

    /// <summary>释放数据库并删除本次基准使用的临时目录。</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _database?.Dispose();
        _database = null;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void InsertDevices()
    {
        var sql = new StringBuilder(
            "INSERT INTO m41_push_devices (id, region, payload) VALUES ");
        for (int id = 1; id <= DeviceRows; id++)
        {
            if (id != 1)
                sql.Append(',');
            sql.Append('(')
                .Append(id)
                .Append(",'")
                .Append((id & 1) == 0 ? "north" : "south")
                .Append("','device-payload-")
                .Append(id)
                .Append("')");
        }
        SqlExecutor.Execute(_database!, sql.ToString());
    }

    private void InsertTasks()
    {
        for (int start = 1; start <= TaskRows; start += BatchSize)
        {
            int end = Math.Min(TaskRows, start + BatchSize - 1);
            var sql = new StringBuilder(
                "INSERT INTO m41_push_tasks (id, device_id, status, payload, note) VALUES ");
            for (int id = start; id <= end; id++)
            {
                if (id != start)
                    sql.Append(',');
                int deviceId = ((id - 1) % DeviceRows) + 1;
                sql.Append('(')
                    .Append(id)
                    .Append(',')
                    .Append(deviceId)
                    .Append(",'")
                    .Append((id & 1) == 0 ? "ready" : "blocked")
                    .Append("','task-payload-")
                    .Append(id)
                    .Append("','note-")
                    .Append(id)
                    .Append("')");
            }
            SqlExecutor.Execute(_database!, sql.ToString());
        }
    }

    private SelectExecutionResult ExecuteWithMetrics(
        SelectStatement statement,
        out RelationalSelectExecutionMetrics metrics)
    {
        metrics = new RelationalSelectExecutionMetrics();
        return RelationalSelectExecutor.Execute(_database!, statement, metrics);
    }

    private long ExecuteAndValidate(SelectStatement statement)
    {
        long checksum = ComputeChecksum(SqlExecutor.ExecuteSelect(_database!, statement));
        if (checksum != _expectedChecksum)
        {
            throw new InvalidOperationException(
                $"M41 #372 基准结果校验失败：expected={_expectedChecksum}, actual={checksum}。");
        }
        return checksum;
    }

    private static void RequireEquivalent(SelectExecutionResult expected, SelectExecutionResult actual)
    {
        if (expected.Rows.Count != actual.Rows.Count)
            throw new InvalidOperationException("M41 #372 参考与下推路径行数不一致。");

        var expectedRows = expected.Rows
            .Select(static row => ((long)row[0]!, (string)row[1]!))
            .OrderBy(static row => row.Item1)
            .ToArray();
        var actualRows = actual.Rows
            .Select(static row => ((long)row[0]!, (string)row[1]!))
            .OrderBy(static row => row.Item1)
            .ToArray();
        if (!expectedRows.SequenceEqual(actualRows))
            throw new InvalidOperationException("M41 #372 参考与下推路径结果不一致。");
    }

    private static long ComputeChecksum(SelectExecutionResult result)
    {
        long checksum = result.Rows.Count;
        foreach (var row in result.Rows)
        {
            checksum = unchecked(
                checksum
                + ((long)row[0]! * 397)
                + StringComparer.Ordinal.GetHashCode((string)row[1]!));
        }
        return checksum;
    }

    private static SelectStatement ParseSelect(string sql)
        => SqlParser.Parse(sql) as SelectStatement
            ?? throw new InvalidOperationException("M41 #372 基准 SQL 未解析为 SELECT。");
}
