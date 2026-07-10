using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Engine.Retention;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// M33 #285~#287 对拍基准：比较旧式时间戳 HashSet、raw 全量物化、全量排序与下推后的执行路径。
/// 数据全部 flush 到本地段，后台 flush / compaction / retention 均关闭；测量为预热后的热查询。
/// </summary>
[Config(typeof(M33PushdownBenchmarkConfig))]
[BenchmarkCategory("M33Pushdown")]
public class M33PushdownBenchmark
{
    private const int PointCount = 200_000;
    private const int SegmentCount = 16;
    private const int Fetch = 64;

    private string _root = string.Empty;
    private Tsdb? _db;
    private ulong _seriesId;

    /// <summary>创建 16 个不重叠落盘段，每个时刻写入三个字段。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sonnetdb-m33-bench-{Guid.NewGuid():N}");
        _db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            FlushPolicy = new MemTableFlushPolicy
            {
                MaxPoints = int.MaxValue,
                MaxBytes = long.MaxValue,
                MaxAge = TimeSpan.MaxValue,
            },
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
            Retention = new RetentionPolicy { Enabled = false },
        });

        SqlExecutor.Execute(_db,
            "CREATE MEASUREMENT m33 (host TAG, value FIELD FLOAT, status FIELD INT, ok FIELD BOOL)");

        var tags = new Dictionary<string, string> { ["host"] = "h1" };
        int pointsPerSegment = PointCount / SegmentCount;
        for (int i = 0; i < PointCount; i++)
        {
            _db.Write(Point.Create(
                "m33",
                1_700_000_000_000L + i,
                tags,
                new Dictionary<string, FieldValue>
                {
                    ["value"] = FieldValue.FromDouble(i),
                    ["status"] = FieldValue.FromLong(i % 8),
                    ["ok"] = FieldValue.FromBool((i & 1) == 0),
                }));

            if ((i + 1) % pointsPerSegment == 0)
                _db.FlushNow();
        }

        _seriesId = _db.Catalog.Snapshot().Single().Id;
        ValidateEquivalentResults();
    }

    /// <summary>旧 count(*) 参考：三个字段逐点扫描并用 HashSet 构造时间戳并集。</summary>
    [Benchmark(Description = "#285 before: HashSet timestamp union")]
    public int CountStar_BeforeHashSet()
    {
        var timestamps = new HashSet<long>();
        foreach (string field in new[] { "value", "status", "ok" })
        {
            foreach (var point in _db!.Query.Execute(
                new PointQuery(_seriesId, field, TimeRange.All)))
            {
                timestamps.Add(point.Timestamp);
            }
        }
        return timestamps.Count;
    }

    /// <summary>新 count(*)：多字段有序流 k-way merge，不保留全量时间戳集合。</summary>
    [Benchmark(Description = "#285 after: count(*) k-way merge")]
    public long CountStar_AfterPushdown()
        => (long)Select("SELECT count(*) FROM m33 WHERE host = 'h1'").Rows.Single()[0]!;

    /// <summary>旧 LIMIT 参考：先物化完整 raw 结果，再取前 64 行。</summary>
    [Benchmark(Description = "#286 before: materialize all then Take")]
    public int Limit_BeforeMaterializeAll()
        => Select("SELECT time, value, status, ok FROM m33 WHERE host = 'h1'")
            .Rows.Take(Fetch).Count();

    /// <summary>新 LIMIT：时间轴合并器拿够 64 个不同时间戳后停止。</summary>
    [Benchmark(Description = "#286 after: LIMIT pushdown")]
    public int Limit_AfterPushdown()
        => Select($"SELECT time, value, status, ok FROM m33 WHERE host = 'h1' LIMIT {Fetch}")
            .Rows.Count;

    /// <summary>旧 latest-N 参考：物化全部行并全量倒序排序。</summary>
    [Benchmark(Description = "#287 before: full DESC sort then Take")]
    public int LatestN_BeforeFullSort()
        => Select("SELECT time, value, status, ok FROM m33 WHERE host = 'h1' ORDER BY time DESC")
            .Rows.Take(Fetch).Count();

    /// <summary>新 latest-N：PointQuery 从最大时间戳 block 反向扫描并提前停止。</summary>
    [Benchmark(Description = "#287 after: DESC latest-N pushdown")]
    public int LatestN_AfterPushdown()
        => Select(
            $"SELECT time, value, status, ok FROM m33 WHERE host = 'h1' ORDER BY time DESC LIMIT {Fetch}")
            .Rows.Count;

    /// <summary>释放数据库并删除基准数据目录。</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        _db = null;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private SelectExecutionResult Select(string sql)
        => SqlExecutor.Execute(_db!, sql) as SelectExecutionResult
            ?? throw new InvalidOperationException("M33 基准查询未返回 SELECT 结果。");

    private void ValidateEquivalentResults()
    {
        if (CountStar_BeforeHashSet() != CountStar_AfterPushdown())
            throw new InvalidOperationException("#285 before/after count(*) 结果不一致。");

        var materializedPrefix = Select(
            "SELECT time, value, status, ok FROM m33 WHERE host = 'h1'")
            .Rows.Take(Fetch).ToList();
        var pushedPrefix = Select(
            $"SELECT time, value, status, ok FROM m33 WHERE host = 'h1' LIMIT {Fetch}").Rows;
        RequireRowsEqual(materializedPrefix, pushedPrefix, "#286");

        var materializedLatest = Select(
            "SELECT time, value, status, ok FROM m33 WHERE host = 'h1' ORDER BY time DESC")
            .Rows.Take(Fetch).ToList();
        var pushedLatest = Select(
            $"SELECT time, value, status, ok FROM m33 WHERE host = 'h1' ORDER BY time DESC LIMIT {Fetch}").Rows;
        RequireRowsEqual(materializedLatest, pushedLatest, "#287");
    }

    private static void RequireRowsEqual(
        IReadOnlyList<IReadOnlyList<object?>> expected,
        IReadOnlyList<IReadOnlyList<object?>> actual,
        string scenario)
    {
        if (expected.Count != actual.Count)
            throw new InvalidOperationException($"{scenario} before/after 行数不一致。");

        for (int row = 0; row < expected.Count; row++)
        {
            if (!expected[row].SequenceEqual(actual[row]))
                throw new InvalidOperationException($"{scenario} before/after 第 {row} 行不一致。");
        }
    }
}

internal sealed class M33PushdownBenchmarkConfig : ManualConfig
{
    public M33PushdownBenchmarkConfig()
    {
        AddJob(Job.Default
            .WithWarmupCount(3)
            .WithIterationCount(7));
        AddColumn(StatisticColumn.Median, StatisticColumn.P90);
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}
