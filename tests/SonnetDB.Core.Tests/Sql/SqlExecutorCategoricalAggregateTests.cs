using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// Milestone 31：selector 与 categorical 聚合的类型语义端到端测试。
/// </summary>
public sealed class SqlExecutorCategoricalAggregateTests : IDisposable
{
    private readonly string _root;

    public SqlExecutorCategoricalAggregateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-categorical-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private TsdbOptions Options() => new()
    {
        RootDirectory = _root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new SonnetDB.Engine.Compaction.CompactionPolicy { Enabled = false },
        SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
    };

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));

    [Fact]
    public void FirstLast_StringAndBoolean_GroupByTime_PreserveValuesByTimestamp()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT ST_DeviceVal (deviceId TAG, str_val FIELD STRING, bool_val FIELD BOOL)");
        SqlExecutor.Execute(db,
            "INSERT INTO ST_DeviceVal (time, deviceId, str_val, bool_val) VALUES " +
            "(900, 'xx', 'late-0', FALSE), (100, 'xx', 'early-0', TRUE), " +
            "(1900, 'xx', 'late-1', TRUE), (1100, 'xx', 'early-1', FALSE)");
        Assert.NotNull(db.FlushNow());

        var result = Select(db,
            "SELECT time, first(str_val), last(str_val), first(bool_val), last(bool_val) " +
            "FROM ST_DeviceVal WHERE deviceId='xx' AND time >= 0 AND time <= 1999 " +
            "GROUP BY time(1000ms)");

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal([0L, "early-0", "late-0", true, false], result.Rows[0]);
        Assert.Equal([1000L, "early-1", "late-1", false, true], result.Rows[1]);

        var unbucketed = Select(db,
            "SELECT first(str_val), last(str_val), first(bool_val), last(bool_val) " +
            "FROM ST_DeviceVal WHERE deviceId='xx'");
        Assert.Equal(["early-0", "late-1", true, true], Assert.Single(unbucketed.Rows));
    }

    [Fact]
    public void FirstLast_AllFieldTypes_PreserveOriginalResultTypes()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT samples (device TAG, int_val FIELD INT, vec FIELD VECTOR(2), pos FIELD GEOPOINT)");
        SqlExecutor.Execute(db,
            "INSERT INTO samples (time, device, int_val, vec, pos) VALUES " +
            "(100, 'd1', 9007199254740993, [1, 2], POINT(31.2, 121.4)), " +
            "(200, 'd1', 9007199254740995, [3, 4], POINT(39.9, 116.4))");

        var result = Select(db,
            "SELECT first(int_val), last(int_val), first(vec), last(pos) " +
            "FROM samples WHERE device='d1'");

        var row = Assert.Single(result.Rows);
        Assert.Equal(9007199254740993L, Assert.IsType<long>(row[0]));
        Assert.Equal(9007199254740995L, Assert.IsType<long>(row[1]));
        Assert.Equal([1f, 2f], Assert.IsType<float[]>(row[2]));
        Assert.Equal(new GeoPoint(39.9, 116.4), Assert.IsType<GeoPoint>(row[3]));
    }

    [Fact]
    public void CategoricalAggregates_StringAndBoolean_UseStableSemantics()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT states (device TAG, label FIELD STRING, enabled FIELD BOOL)");
        SqlExecutor.Execute(db,
            "INSERT INTO states (time, device, label, enabled) VALUES " +
            "(100, 'd1', 'a', TRUE), (200, 'd1', 'Z', FALSE), " +
            "(300, 'd1', 'a', FALSE), (400, 'd1', 'Z', TRUE), " +
            "(500, 'd1', 'other', FALSE)");

        var result = Select(db,
            "SELECT min(label), max(label), mode(label), distinct_count(label), " +
            "min(enabled), max(enabled), mode(enabled), distinct_count(enabled) " +
            "FROM states WHERE device='d1'");

        var row = Assert.Single(result.Rows);
        Assert.Equal("Z", row[0]);
        Assert.Equal("other", row[1]);
        Assert.Equal("Z", row[2]);
        Assert.Equal(3L, row[3]);
        Assert.Equal(false, row[4]);
        Assert.Equal(true, row[5]);
        Assert.Equal(false, row[6]);
        Assert.Equal(2L, row[7]);
    }

    [Fact]
    public void CategoricalAggregates_AfterFlush_MatchInMemoryResults()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT states (device TAG, label FIELD STRING, enabled FIELD BOOL)");
        SqlExecutor.Execute(db,
            "INSERT INTO states (time, device, label, enabled) VALUES " +
            "(100, 'd1', 'idle', FALSE), (200, 'd1', 'run', TRUE), " +
            "(1100, 'd1', 'run', TRUE), (1200, 'd1', 'alarm', FALSE)");
        Assert.NotNull(db.FlushNow());

        var result = Select(db,
            "SELECT time, mode(label), distinct_count(label), min(enabled), max(enabled) " +
            "FROM states WHERE device='d1' GROUP BY time(1000ms)");

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal([0L, "idle", 2L, false, true], result.Rows[0]);
        Assert.Equal([1000L, "alarm", 2L, false, true], result.Rows[1]);

        var unbucketed = Select(db,
            "SELECT distinct_count(label), distinct_count(enabled) FROM states WHERE device='d1'");
        Assert.Equal([3L, 2L], Assert.Single(unbucketed.Rows));
    }

    [Fact]
    public void NumericAggregate_OnStringField_ReportsFunctionSpecificRequirement()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT states (device TAG, label FIELD STRING)");

        var error = Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "SELECT sum(label) FROM states WHERE device='d1'"));

        Assert.Contains("sum", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("需要数值字段", error.Message, StringComparison.Ordinal);
    }
}
