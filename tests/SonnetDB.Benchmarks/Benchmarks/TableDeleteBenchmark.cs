using BenchmarkDotNet.Attributes;
using SonnetDB.Engine;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// M19 #126.1 关系表大批量删除基准：逐行 tombstone、批量 tombstone 与 generation 快速清表对照。
/// </summary>
[MemoryDiagnoser]
public sealed class TableDeleteBenchmark
{
    private string _workRoot = null!;
    private Tsdb _rowByRow = null!;
    private Tsdb _batchPredicate = null!;
    private Tsdb _generationDelete = null!;
    private Tsdb _truncate = null!;

    [Params(1_000, 10_000)]
    public int Rows { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        _workRoot = Path.Combine(Path.GetTempPath(), "sndb-table-delete-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workRoot);
        _rowByRow = CreateDatabase("row-by-row");
        _batchPredicate = CreateDatabase("batch-predicate");
        _generationDelete = CreateDatabase("generation-delete");
        _truncate = CreateDatabase("truncate");
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _rowByRow?.Dispose();
        _batchPredicate?.Dispose();
        _generationDelete?.Dispose();
        _truncate?.Dispose();
        try { Directory.Delete(_workRoot, recursive: true); } catch { }
    }

    [Benchmark(Baseline = true, Description = "旧路径：逐主键 delete record")]
    public int DeleteRowByRow()
    {
        var store = _rowByRow.Tables.Open("devices");
        int removed = 0;
        for (int id = 1; id <= Rows; id++)
        {
            if (store.DeleteByPrimaryKey([(long)id]))
                removed++;
        }

        return removed;
    }

    [Benchmark(Description = "批量 tombstone：DELETE WHERE predicate")]
    public int DeleteWithBatchTombstones()
        => SqlExecutor.ExecuteDelete(
            _batchPredicate,
            (SonnetDB.Sql.Ast.DeleteStatement)SonnetDB.Sql.SqlParser.Parse(
                "DELETE FROM devices WHERE id >= 1")).SeriesAffected;

    [Benchmark(Description = "generation：DELETE WHERE TRUE")]
    public int DeleteWithGeneration()
        => SqlExecutor.ExecuteDelete(
            _generationDelete,
            (SonnetDB.Sql.Ast.DeleteStatement)SonnetDB.Sql.SqlParser.Parse(
                "DELETE FROM devices WHERE TRUE")).SeriesAffected;

    [Benchmark(Description = "generation：TRUNCATE TABLE")]
    public int TruncateTable()
        => _truncate.Tables.Truncate("devices");

    private Tsdb CreateDatabase(string name)
    {
        var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = Path.Combine(_workRoot, name),
            Kv = KvOptions.Default with
            {
                SyncWalOnEveryWrite = false,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            },
        });
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX idx_devices_name ON devices (name)");
        db.Tables.Open("devices").InsertMany(Enumerable.Range(1, Rows)
            .Select(static id => (IReadOnlyList<object?>)new object?[] { (long)id, $"device-{id:D8}" })
            .ToArray());
        return db;
    }
}
