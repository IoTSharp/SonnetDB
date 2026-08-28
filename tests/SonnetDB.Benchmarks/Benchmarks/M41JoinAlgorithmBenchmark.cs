using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// M41 #378 JOIN 算法对拍：在相同结果集上比较 Hash 扫描与 index nested-loop、Hash 与 merge join。
/// </summary>
[Config(typeof(M41ExistsAccessPathBenchmarkConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[BenchmarkCategory("M41", "P3", "JoinAlgorithms")]
public class M41JoinAlgorithmBenchmark
{
    private const int DimensionRows = 30_000;
    private const int ProbeRows = 64;
    private const int MergeRows = 10_000;

    private string _root = string.Empty;
    private Tsdb? _database;
    private SelectStatement? _indexNestedLoop;
    private SelectStatement? _indexNestedLoopHashReference;
    private SelectStatement? _mergeJoin;
    private SelectStatement? _mergeJoinHashReference;
    private long _indexChecksum;
    private long _mergeChecksum;

    /// <summary>创建确定性镜像数据，并预验四条查询的结果与实际 JOIN 算子。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sonnetdb-m41-join-bench-{Guid.NewGuid():N}");
        _database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        CreateSchemas();
        InsertIndexNestedLoopData();
        InsertMergeJoinData();

        _indexNestedLoop = ParseSelect("""
            SELECT p.id, d.name
            FROM m41_join_probe p
            JOIN m41_join_dimension_indexed d ON p.dimension_id = d.id
            """);
        _indexNestedLoopHashReference = ParseSelect("""
            SELECT p.id, d.name
            FROM m41_join_probe p
            JOIN m41_join_dimension_scan d ON p.dimension_id = d.lookup_id
            """);
        _mergeJoin = ParseSelect("""
            SELECT l.id, r.payload
            FROM m41_merge_left_indexed l
            JOIN m41_merge_right_indexed r ON l.join_key = r.join_key
            """);
        _mergeJoinHashReference = ParseSelect("""
            SELECT l.id, r.payload
            FROM m41_merge_left_scan l
            JOIN m41_merge_right_scan r ON l.join_key = r.join_key
            """);

        _indexChecksum = RequireEquivalentAndOperator(
            _indexNestedLoopHashReference,
            "hash_join",
            _indexNestedLoop,
            "index_nested_loop");
        _mergeChecksum = RequireEquivalentAndOperator(
            _mergeJoinHashReference,
            "hash_join",
            _mergeJoin,
            "merge_join");
    }

    /// <summary>测量小 probe 与未索引大维表的 Hash Join 扫描参考。</summary>
    /// <returns>结果校验和。</returns>
    [Benchmark(Baseline = true, Description = "#378 reference: Hash Join scans dimension")]
    [BenchmarkCategory("IndexNestedLoop")]
    public long IndexNestedLoop_HashScanReference()
        => ExecuteAndValidate(_indexNestedLoopHashReference!, _indexChecksum, "hash_join");

    /// <summary>测量小 probe 对大维表主键的 index nested-loop。</summary>
    /// <returns>结果校验和。</returns>
    [Benchmark(Description = "#378 index nested-loop: bounded primary probes")]
    [BenchmarkCategory("IndexNestedLoop")]
    public long IndexNestedLoop_PrimaryKeyProbes()
        => ExecuteAndValidate(_indexNestedLoop!, _indexChecksum, "index_nested_loop");

    /// <summary>测量两个未索引大输入的 Hash Join 参考。</summary>
    /// <returns>结果校验和。</returns>
    [Benchmark(Baseline = true, Description = "#378 reference: Hash Join builds input")]
    [BenchmarkCategory("MergeJoin")]
    public long MergeJoin_HashReference()
        => ExecuteAndValidate(_mergeJoinHashReference!, _mergeChecksum, "hash_join");

    /// <summary>测量两个兼容有序索引输入的流式 merge join。</summary>
    /// <returns>结果校验和。</returns>
    [Benchmark(Description = "#378 merge join: ordered index inputs")]
    [BenchmarkCategory("MergeJoin")]
    public long MergeJoin_OrderedIndexes()
        => ExecuteAndValidate(_mergeJoin!, _mergeChecksum, "merge_join");

    /// <summary>释放数据库并删除本次基准使用的临时目录。</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _database?.Dispose();
        _database = null;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void CreateSchemas()
    {
        SqlExecutor.Execute(_database!, "CREATE TABLE m41_join_probe (id INT, dimension_id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(_database!, "CREATE TABLE m41_join_dimension_indexed (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(_database!, "CREATE TABLE m41_join_dimension_scan (row_id INT, lookup_id INT, name STRING, PRIMARY KEY (row_id))");
        SqlExecutor.Execute(_database!, "CREATE TABLE m41_merge_left_indexed (id INT, join_key INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(_database!, "CREATE INDEX ix_m41_merge_left_key ON m41_merge_left_indexed (join_key)");
        SqlExecutor.Execute(_database!, "CREATE TABLE m41_merge_right_indexed (join_key INT, payload INT, PRIMARY KEY (join_key))");
        SqlExecutor.Execute(_database!, "CREATE TABLE m41_merge_left_scan (id INT, join_key INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(_database!, "CREATE TABLE m41_merge_right_scan (id INT, join_key INT, payload INT, PRIMARY KEY (id))");
    }

    private void InsertIndexNestedLoopData()
    {
        _database!.Tables.Open("m41_join_probe").InsertMany(Enumerable.Range(0, ProbeRows)
            .Select(static id => (IReadOnlyList<object?>)new object?[]
            {
                (long)id,
                (long)(id * (DimensionRows / ProbeRows)),
            })
            .ToArray());
        _database.Tables.Open("m41_join_dimension_indexed").InsertMany(Enumerable.Range(0, DimensionRows)
            .Select(static id => (IReadOnlyList<object?>)new object?[] { (long)id, $"dimension-{id}" })
            .ToArray());
        _database.Tables.Open("m41_join_dimension_scan").InsertMany(Enumerable.Range(0, DimensionRows)
            .Select(static id => (IReadOnlyList<object?>)new object?[]
            {
                (long)id,
                (long)id,
                $"dimension-{id}",
            })
            .ToArray());
    }

    private void InsertMergeJoinData()
    {
        IReadOnlyList<object?>[] leftRows = Enumerable.Range(0, MergeRows)
            .Select(static id => (IReadOnlyList<object?>)new object?[]
            {
                (long)id,
                (long)(id - (MergeRows / 2)),
            })
            .ToArray();
        IReadOnlyList<object?>[] rightIndexedRows = Enumerable.Range(0, MergeRows)
            .Select(static id => (IReadOnlyList<object?>)new object?[]
            {
                (long)(id - (MergeRows / 2)),
                (long)(id * 3),
            })
            .ToArray();
        IReadOnlyList<object?>[] rightScanRows = Enumerable.Range(0, MergeRows)
            .Select(static id => (IReadOnlyList<object?>)new object?[]
            {
                (long)id,
                (long)(id - (MergeRows / 2)),
                (long)(id * 3),
            })
            .ToArray();
        _database!.Tables.Open("m41_merge_left_indexed").InsertMany(leftRows);
        _database.Tables.Open("m41_merge_left_scan").InsertMany(leftRows);
        _database.Tables.Open("m41_merge_right_indexed").InsertMany(rightIndexedRows);
        _database.Tables.Open("m41_merge_right_scan").InsertMany(rightScanRows);
    }

    private long RequireEquivalentAndOperator(
        SelectStatement reference,
        string referenceOperator,
        SelectStatement optimized,
        string optimizedOperator)
    {
        long expected = ExecuteAndValidate(reference, expectedChecksum: null, referenceOperator);
        long actual = ExecuteAndValidate(optimized, expected, optimizedOperator);
        if (actual != expected)
            throw new InvalidOperationException("M41 #378 基准参考与优化路径结果不一致。");
        return expected;
    }

    private long ExecuteAndValidate(
        SelectStatement statement,
        long? expectedChecksum,
        string expectedOperator)
    {
        var metrics = new RelationalSelectExecutionMetrics();
        SelectExecutionResult result = RelationalSelectExecutor.Execute(_database!, statement, metrics);
        long checksum = ComputeChecksum(result);
        if (expectedChecksum is long expected && checksum != expected
            || !string.Equals(metrics.LastJoinOperator, expectedOperator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "M41 #378 基准执行合同不一致："
                + $"checksum={checksum}, expectedChecksum={expectedChecksum}, "
                + $"operator={metrics.LastJoinOperator}, expectedOperator={expectedOperator}。");
        }
        return checksum;
    }

    private static SelectStatement ParseSelect(string sql)
        => SqlParser.Parse(sql) as SelectStatement
            ?? throw new InvalidOperationException("M41 #378 基准 SQL 未解析为 SELECT。");

    private static long ComputeChecksum(SelectExecutionResult result)
    {
        long checksum = result.Rows.Count;
        foreach (IReadOnlyList<object?> row in result.Rows)
        {
            checksum = unchecked(
                checksum
                + ((long)row[0]! * 397)
                + StringComparer.Ordinal.GetHashCode(Convert.ToString(row[1], System.Globalization.CultureInfo.InvariantCulture)!));
        }
        return checksum;
    }
}
