using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// M41 #369～#371 P0 对拍基准：比较 IN semijoin、索引并集和倒序 Top-N 快速路径与等价全扫描参考。
/// 每个参考查询使用值相同的未索引镜像列，避免以不同数据或不同谓词语义制造性能差异。
/// </summary>
[Config(typeof(M41ExistsAccessPathBenchmarkConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[BenchmarkCategory("M41", "P0")]
public class M41P0AccessPathBenchmark
{
    private const int RowCount = 30_000;
    private const int BatchSize = 500;
    private const string TableName = "m41_p0_rows";

    private string _root = string.Empty;
    private Tsdb? _db;
    private TableStore? _store;
    private SelectStatement? _primaryInFast;
    private SelectStatement? _primaryInReference;
    private SelectStatement? _secondaryInFast;
    private SelectStatement? _secondaryInReference;
    private SelectStatement? _indexUnionFast;
    private SelectStatement? _indexUnionReference;
    private SelectStatement? _descendingTopNFast;
    private SelectStatement? _descendingTopNReference;
    private long _primaryInChecksum;
    private long _secondaryInChecksum;
    private long _indexUnionChecksum;
    private long _descendingTopNChecksum;

    /// <summary>创建 30k 行镜像数据、三个必要索引和固定 IN 键集合，并预验全部路径与结果。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sonnetdb-m41-p0-bench-{Guid.NewGuid():N}");
        _db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(_db, $"""
            CREATE TABLE {TableName} (
                id INT,
                id_ref INT,
                tenant STRING,
                tenant_ref STRING,
                occurred_at INT NOT NULL,
                occurred_at_ref INT NOT NULL,
                status STRING,
                PRIMARY KEY (id)
            )
            """);
        SqlExecutor.Execute(_db, $"CREATE INDEX ix_m41_p0_tenant ON {TableName} (tenant)");
        SqlExecutor.Execute(_db, $"CREATE INDEX ix_m41_p0_occurred ON {TableName} (occurred_at)");
        SqlExecutor.Execute(_db, """
            CREATE TABLE m41_p0_keys (
                seq INT,
                target_id INT NULL,
                target_tenant STRING NULL,
                PRIMARY KEY (seq)
            )
            """);
        InsertRows();
        InsertKeys();
        _store = _db.Tables.Open(TableName);

        _primaryInFast = ParseSelect($"""
            SELECT id FROM {TableName}
            WHERE id IN (SELECT target_id FROM m41_p0_keys) AND status = 'ready'
            ORDER BY id
            """);
        _primaryInReference = ParseSelect($"""
            SELECT id FROM {TableName}
            WHERE id_ref IN (SELECT target_id FROM m41_p0_keys) AND status = 'ready'
            ORDER BY id
            """);
        _secondaryInFast = ParseSelect($"""
            SELECT id FROM {TableName}
            WHERE tenant IN (SELECT target_tenant FROM m41_p0_keys) AND status = 'ready'
            ORDER BY id
            """);
        _secondaryInReference = ParseSelect($"""
            SELECT id FROM {TableName}
            WHERE tenant_ref IN (SELECT target_tenant FROM m41_p0_keys) AND status = 'ready'
            ORDER BY id
            """);
        _indexUnionFast = ParseSelect($"""
            SELECT id FROM {TableName}
            WHERE tenant = 'tenant-0007' OR occurred_at >= 14968
            ORDER BY id
            """);
        _indexUnionReference = ParseSelect($"""
            SELECT id FROM {TableName}
            WHERE tenant_ref = 'tenant-0007' OR occurred_at_ref >= 14968
            ORDER BY id
            """);
        _descendingTopNFast = ParseSelect($"""
            SELECT id FROM {TableName}
            ORDER BY occurred_at DESC LIMIT 64
            """);
        _descendingTopNReference = ParseSelect($"""
            SELECT id FROM {TableName}
            ORDER BY occurred_at_ref DESC LIMIT 64
            """);

        _primaryInChecksum = RequireEquivalent(_primaryInReference, _primaryInFast, "#369 primary IN");
        _secondaryInChecksum = RequireEquivalent(_secondaryInReference, _secondaryInFast, "#369 secondary IN");
        _indexUnionChecksum = RequireEquivalent(_indexUnionReference, _indexUnionFast, "#370 index union");
        _descendingTopNChecksum = RequireEquivalent(
            _descendingTopNReference,
            _descendingTopNFast,
            "#371 descending Top-N");

        RequireExplainPath(_primaryInFast, "primary_key_in");
        RequireExplainPath(_secondaryInFast, "secondary_index_in");
        RequireExplainPath(_indexUnionFast, "index_union");
        RequireExplainPath(_descendingTopNFast, "secondary_index_range");
    }

    /// <summary>测量未索引镜像主键列的非相关 IN 关系扫描参考。</summary>
    /// <returns>结果校验和。</returns>
    [Benchmark(Baseline = true, Description = "#369 reference: PK IN semijoin relational scan")]
    [BenchmarkCategory("PrimaryIn")]
    public long PrimaryIn_RelationalScanReference()
        => ExecuteAndValidate(_primaryInReference!, _primaryInChecksum, expectedFullScans: 1, expectedMultiGets: 0);

    /// <summary>测量主键 IN semijoin 的去重单快照 MultiGet。</summary>
    /// <returns>结果校验和。</returns>
    [Benchmark(Description = "#369 fast: PK IN semijoin MultiGet")]
    [BenchmarkCategory("PrimaryIn")]
    public long PrimaryIn_MultiGet()
        => ExecuteAndValidate(_primaryInFast!, _primaryInChecksum, expectedFullScans: 0, expectedMultiGets: 1);

    /// <summary>测量未索引镜像租户列的非相关 IN 关系扫描参考。</summary>
    /// <returns>结果校验和。</returns>
    [Benchmark(Baseline = true, Description = "#369 reference: index IN semijoin relational scan")]
    [BenchmarkCategory("SecondaryIn")]
    public long SecondaryIn_RelationalScanReference()
        => ExecuteAndValidate(_secondaryInReference!, _secondaryInChecksum, expectedFullScans: 1, expectedMultiGets: 0);

    /// <summary>测量二级索引 IN semijoin 的去重单快照 MultiGet。</summary>
    /// <returns>结果校验和。</returns>
    [Benchmark(Description = "#369 fast: secondary index IN MultiGet")]
    [BenchmarkCategory("SecondaryIn")]
    public long SecondaryIn_MultiGet()
        => ExecuteAndValidate(_secondaryInFast!, _secondaryInChecksum, expectedFullScans: 0, expectedMultiGets: 1);

    /// <summary>测量两个未索引镜像列 OR 谓词的全表扫描参考。</summary>
    /// <returns>结果校验和。</returns>
    [Benchmark(Baseline = true, Description = "#370 reference: OR full table scan")]
    [BenchmarkCategory("IndexUnion")]
    public long IndexUnion_FullScanReference()
        => ExecuteAndValidate(_indexUnionReference!, _indexUnionChecksum, expectedFullScans: 1, expectedMultiGets: 0);

    /// <summary>测量两个索引分支按主键去重的有界候选并集。</summary>
    /// <returns>结果校验和。</returns>
    [Benchmark(Description = "#370 fast: bounded index union")]
    [BenchmarkCategory("IndexUnion")]
    public long IndexUnion_BoundedCandidates()
        => ExecuteAndValidate(_indexUnionFast!, _indexUnionChecksum, expectedFullScans: 0, expectedMultiGets: 0);

    /// <summary>测量未索引镜像排序列的全扫描有界堆 Top-N 参考。</summary>
    /// <returns>结果校验和。</returns>
    [Benchmark(Baseline = true, Description = "#371 reference: full scan + bounded heap Top-N")]
    [BenchmarkCategory("DescendingTopN")]
    public long DescendingTopN_FullScanHeapReference()
        => ExecuteAndValidate(
            _descendingTopNReference!,
            _descendingTopNChecksum,
            expectedFullScans: 1,
            expectedMultiGets: 0);

    /// <summary>测量反向索引 cursor 在取得 64 行后停止的倒序 Top-N。</summary>
    /// <returns>结果校验和。</returns>
    [Benchmark(Description = "#371 fast: reverse index cursor Top-N")]
    [BenchmarkCategory("DescendingTopN")]
    public long DescendingTopN_ReverseIndexCursor()
        => ExecuteAndValidate(
            _descendingTopNFast!,
            _descendingTopNChecksum,
            expectedFullScans: 0,
            expectedMultiGets: 0);

    /// <summary>释放数据库并删除本次基准使用的临时目录。</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        _db = null;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>执行预解析查询，并核对结果、全扫和 MultiGet 计数增量。</summary>
    private long ExecuteAndValidate(
        SelectStatement statement,
        long expectedChecksum,
        long expectedFullScans,
        long expectedMultiGets)
    {
        long scansBefore = _store!.FullScanCount;
        long multiGetsBefore = _store.MultiGetCount;
        long checksum = ComputeChecksum(SqlExecutor.ExecuteSelect(_db!, statement));
        long fullScans = _store.FullScanCount - scansBefore;
        long multiGets = _store.MultiGetCount - multiGetsBefore;
        if (checksum != expectedChecksum
            || fullScans != expectedFullScans
            || multiGets != expectedMultiGets)
        {
            throw new InvalidOperationException(
                "M41 P0 基准执行合同不一致："
                + $"checksum={checksum}, expectedChecksum={expectedChecksum}, "
                + $"fullScans={fullScans}, expectedFullScans={expectedFullScans}, "
                + $"multiGets={multiGets}, expectedMultiGets={expectedMultiGets}。");
        }
        return checksum;
    }

    /// <summary>执行参考与快速路径并要求输出逐行相同。</summary>
    private long RequireEquivalent(SelectStatement reference, SelectStatement fast, string scenario)
    {
        SelectExecutionResult expected = SqlExecutor.ExecuteSelect(_db!, reference);
        SelectExecutionResult actual = SqlExecutor.ExecuteSelect(_db!, fast);
        if (expected.Rows.Count != actual.Rows.Count)
            throw new InvalidOperationException($"{scenario} 参考与快速路径行数不一致。");
        for (int i = 0; i < expected.Rows.Count; i++)
        {
            if (!expected.Rows[i].SequenceEqual(actual.Rows[i]))
                throw new InvalidOperationException($"{scenario} 参考与快速路径第 {i} 行不一致。");
        }
        return ComputeChecksum(expected);
    }

    /// <summary>通过共享 EXPLAIN 规划器确认基准快速查询命中预期访问路径。</summary>
    private void RequireExplainPath(SelectStatement statement, string expectedAccessPath)
    {
        SqlExplainExecutionResult explain = SqlExplainPlanner.Explain(
            databaseName: null,
            tsdb: _db!,
            statement: statement);
        if (!string.Equals(explain.AccessPath, expectedAccessPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"M41 P0 基准 EXPLAIN 路径不一致：expected={expectedAccessPath}, actual={explain.AccessPath}。");
        }
    }

    /// <summary>分批插入确定性的已索引列与未索引镜像列。</summary>
    private void InsertRows()
    {
        for (int start = 1; start <= RowCount; start += BatchSize)
        {
            int end = Math.Min(RowCount, start + BatchSize - 1);
            var sql = new StringBuilder($"INSERT INTO {TableName} (id, id_ref, tenant, tenant_ref, occurred_at, occurred_at_ref, status) VALUES ");
            for (int id = start; id <= end; id++)
            {
                if (id != start)
                    sql.Append(',');
                string tenant = "tenant-" + (id % 1024).ToString("D4", CultureInfo.InvariantCulture);
                long occurredAt = id - (RowCount / 2L);
                sql.Append('(')
                    .Append(id).Append(',')
                    .Append(id).Append(",'")
                    .Append(tenant).Append("','")
                    .Append(tenant).Append("',")
                    .Append(occurredAt).Append(',')
                    .Append(occurredAt).Append(',')
                    .Append((id & 1) == 0 ? "'ready'" : "'blocked'")
                    .Append(')');
            }
            SqlExecutor.Execute(_db!, sql.ToString());
        }
    }

    /// <summary>插入包含重复值和 NULL 的固定 semijoin 键集合。</summary>
    private void InsertKeys()
    {
        var sql = new StringBuilder(
            "INSERT INTO m41_p0_keys (seq, target_id, target_tenant) VALUES ");
        for (int seq = 1; seq <= 128; seq++)
        {
            if (seq != 1)
                sql.Append(',');
            sql.Append('(').Append(seq).Append(',');
            if (seq % 19 == 0)
                sql.Append("NULL");
            else
                sql.Append(RowCount - (seq % 64));
            sql.Append(',');
            if (seq % 23 == 0)
            {
                sql.Append("NULL");
            }
            else
            {
                sql.Append("'tenant-")
                    .Append((seq % 32).ToString("D4", CultureInfo.InvariantCulture))
                    .Append('\'');
            }
            sql.Append(')');
        }
        SqlExecutor.Execute(_db!, sql.ToString());
    }

    /// <summary>解析基准 SQL，并确保结果是 SELECT AST。</summary>
    private static SelectStatement ParseSelect(string sql)
        => SqlParser.Parse(sql) as SelectStatement
            ?? throw new InvalidOperationException("M41 P0 基准 SQL 未解析为 SELECT。");

    /// <summary>计算单列 Int64 结果的顺序敏感校验和。</summary>
    private static long ComputeChecksum(SelectExecutionResult result)
    {
        long checksum = result.Rows.Count;
        foreach (var row in result.Rows)
            checksum = unchecked((checksum * 397) ^ (long)row[0]!);
        return checksum;
    }
}
