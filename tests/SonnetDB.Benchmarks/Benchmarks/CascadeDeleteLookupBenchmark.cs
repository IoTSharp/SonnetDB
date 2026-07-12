using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using SonnetDB.Engine;
using SonnetDB.Kv;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// P4 级联删除查找基准：100k 子行、100 个父键，覆盖 CASCADE / SET NULL 与索引/回退路径。
/// 通过末尾故意失败的 guard mutation 阻止提交，使每次调用都在同一数据快照上重复测量展开阶段。
/// </summary>
[Config(typeof(CascadeDeleteLookupBenchmarkConfig))]
[BenchmarkCategory("P4", "CascadeDelete")]
public class CascadeDeleteLookupBenchmark
{
    private const int ChildRowCount = 100_000;
    private const int ParentCount = 100;
    private const int BatchSize = 2_000;

    private string _root = string.Empty;
    private Tsdb? _db;
    private IReadOnlyDictionary<string, IReadOnlyList<TableRowMutation>>? _mutations;

    /// <summary>要验证的外键删除动作。</summary>
    [Params(ForeignKeyAction.Cascade, ForeignKeyAction.SetNull)]
    public ForeignKeyAction Action { get; set; }

    /// <summary>是否为 FK 完整列序创建持久二级索引。</summary>
    [Params(false, true)]
    public bool HasIndex { get; set; }

    /// <summary>建立 100 个父行和均匀分布的 100k 子行。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sonnetdb-cascade-bench-{Guid.NewGuid():N}");
        _db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            Kv = new KvOptions
            {
                SyncWalOnEveryWrite = false,
                ExpirerEnabled = false,
            },
        });

        string actionSql = Action == ForeignKeyAction.Cascade ? "CASCADE" : "SET NULL";
        SqlExecutor.Execute(_db, "CREATE TABLE bench_parents (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(_db,
            "CREATE TABLE bench_children (id INT, parent_id INT NULL, payload STRING, PRIMARY KEY (id), "
            + $"FOREIGN KEY (parent_id) REFERENCES bench_parents (id) ON DELETE {actionSql})");
        SqlExecutor.Execute(_db, "CREATE TABLE bench_guard (id INT, PRIMARY KEY (id))");
        if (HasIndex)
            SqlExecutor.Execute(_db, "CREATE INDEX idx_bench_children_parent ON bench_children (parent_id)");

        var parentRows = new TableRowMutation[ParentCount];
        for (int i = 0; i < ParentCount; i++)
            parentRows[i] = new TableRowMutation(PrimaryKeyValues: null, [i + 1L]);
        _db.Tables.Open("bench_parents").ApplyBatch(parentRows);
        _db.Tables.Open("bench_guard").ApplyBatch([new TableRowMutation(PrimaryKeyValues: null, [1L])]);

        TableStore childStore = _db.Tables.Open("bench_children");
        for (int start = 0; start < ChildRowCount; start += BatchSize)
        {
            int count = Math.Min(BatchSize, ChildRowCount - start);
            var rows = new TableRowMutation[count];
            for (int offset = 0; offset < count; offset++)
            {
                long id = start + offset + 1L;
                long parentId = (start + offset) % ParentCount + 1L;
                rows[offset] = new TableRowMutation(PrimaryKeyValues: null, [id, parentId, "payload"]);
            }
            childStore.ApplyBatch(rows);
        }

        var deletes = new TableRowMutation[ParentCount];
        for (int i = 0; i < ParentCount; i++)
            deletes[i] = new TableRowMutation([i + 1L], NewValues: null);
        _mutations = new Dictionary<string, IReadOnlyList<TableRowMutation>>(StringComparer.Ordinal)
        {
            ["bench_parents"] = deletes,
            ["bench_guard"] = [new TableRowMutation(PrimaryKeyValues: null, [1L])],
        };

        _ = ExpandCascade_100kChildren_100Parents();
    }

    /// <summary>
    /// 测量单批级联展开；返回扫描解码行数或持久索引查找次数，便于结果表识别实际路径。
    /// </summary>
    /// <returns>回退路径为 100000，索引路径为 100。</returns>
    [Benchmark(Description = "P4 cascade lookup: 100k children / 100 parents")]
    public long ExpandCascade_100kChildren_100Parents()
    {
        var metrics = new CascadeDeleteExecutionMetrics();
        try
        {
            _db!.Tables.ApplyTransaction(_mutations!, metrics);
            throw new InvalidOperationException("guard mutation 应阻止基准事务提交。");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("主键已存在", StringComparison.Ordinal))
        {
        }

        if (metrics.CatalogSnapshotCount != 1)
            throw new InvalidOperationException($"catalog snapshot 次数错误：{metrics.CatalogSnapshotCount}。");

        if (HasIndex)
        {
            if (metrics.PersistentIndexLookupCount != ParentCount
                || metrics.FallbackScanCount != 0
                || metrics.FallbackDecodedRowCount != 0)
            {
                throw new InvalidOperationException(
                    $"持久索引路径错误：lookups={metrics.PersistentIndexLookupCount}, "
                    + $"scans={metrics.FallbackScanCount}, decoded={metrics.FallbackDecodedRowCount}。");
            }
            return metrics.PersistentIndexLookupCount;
        }

        if (metrics.PersistentIndexLookupCount != 0
            || metrics.FallbackScanCount != 1
            || metrics.FallbackDecodedRowCount != ChildRowCount)
        {
            throw new InvalidOperationException(
                $"临时哈希回退路径错误：lookups={metrics.PersistentIndexLookupCount}, "
                + $"scans={metrics.FallbackScanCount}, decoded={metrics.FallbackDecodedRowCount}。");
        }
        return metrics.FallbackDecodedRowCount;
    }

    /// <summary>释放数据库并删除基准目录。</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        _db = null;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

internal sealed class CascadeDeleteLookupBenchmarkConfig : ManualConfig
{
    public CascadeDeleteLookupBenchmarkConfig()
    {
        AddJob(Job.Default.WithWarmupCount(1).WithIterationCount(3));
        AddColumn(StatisticColumn.Median, StatisticColumn.P90);
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}
